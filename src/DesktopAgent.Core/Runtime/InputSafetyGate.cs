using DesktopAgent.Core.Domain;

namespace DesktopAgent.Core.Runtime;

public enum InputStopReason { None, Paused, Stopped, Deadline, InputFault, HotkeysUnavailable, Shutdown }
public sealed record InputGateStatus(long Revision, bool HotkeysReady, Lease? Lease, InputStopReason Reason, bool IsOpen);
public readonly record struct InputSegmentResult(bool Admitted, int AppliedCount);

/// <summary>
/// Admission barrier for short, synchronous native input segments. Trip never waits for the input
/// worker, UI, provider or cancellation callbacks. A segment admitted before Trip is in flight and
/// cannot be recalled; every subsequent segment must obtain admission again.
/// </summary>
public sealed class InputSafetyGate
{
    private sealed record State(long Revision, bool Ready, InputRun? Run, InputStopReason Reason, Lease? RetiredLease = null);
    private State _state = new(0, false, null, InputStopReason.HotkeysUnavailable);
    private readonly object _injectionSync = new();

    public InputGateStatus Status
    {
        get
        {
            var s = Volatile.Read(ref _state);
            return new(s.Revision, s.Ready, s.Run?.Lease.Lease, s.Reason,
                s.Ready && s.Run is not null && s.Reason == InputStopReason.None && !s.Run.Token.IsCancellationRequested);
        }
    }

    // This must be set only by the owner of the registered native emergency hotkeys.
    public void SetHotkeysReady(bool ready)
    {
        while (true)
        {
            var old = Volatile.Read(ref _state);
            if (old.Reason == InputStopReason.Shutdown && ready) return;
            var next = old with { Revision = checked(old.Revision + 1), Ready = ready,
                Reason = old.Reason == InputStopReason.Shutdown ? old.Reason :
                    ready ? (old.Run is null && old.Reason == InputStopReason.HotkeysUnavailable ? InputStopReason.None : old.Reason)
                          : InputStopReason.HotkeysUnavailable };
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _state, next, old), old)) continue;
            if (!ready) old.Run?.Cancel();
            return;
        }
    }

    /// <summary>Explicit start/resume only, using the revision observed when the user request began.</summary>
    public bool TryArm(TaskLeaseSession session, LeaseSnapshot lease, long expectedRevision,
        TimeSpan maximumDuration, out InputRun? run)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(lease);
        if (maximumDuration <= TimeSpan.Zero || maximumDuration > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        run = null;
        var old = Volatile.Read(ref _state);
        if (!old.Ready || old.Run is not null || old.Revision != expectedRevision ||
            old.Reason == InputStopReason.Shutdown || !session.IsCurrent(lease) ||
            (old.RetiredLease is { } retired && retired.TaskId == lease.Lease.TaskId && retired.Epoch >= lease.Lease.Epoch)) return false;
        var candidate = new InputRun(this, session, lease);
        var next = new State(checked(old.Revision + 1), true, candidate, InputStopReason.None, old.RetiredLease);
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _state, next, old), old))
        {
            candidate.CloseResources();
            return false;
        }
        candidate.Activate(maximumDuration);
        run = candidate;
        return true;
    }

    public void Trip(InputStopReason reason) => TripCore(reason, null);
    internal void TripRun(InputStopReason reason, InputRun run) => TripCore(reason, run);

    private void TripCore(InputStopReason reason, InputRun? expectedRun)
    {
        if (reason == InputStopReason.None) throw new ArgumentOutOfRangeException(nameof(reason));
        while (true)
        {
            var old = Volatile.Read(ref _state);
            if (expectedRun is not null && !ReferenceEquals(old.Run, expectedRun)) return;
            var effectiveReason = old.Reason > reason ? old.Reason : reason;
            var next = old with { Revision = checked(old.Revision + 1), Reason = effectiveReason };
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _state, next, old), old)) continue;
            old.Run?.Cancel();
            return;
        }
    }

    /// <summary>Only a bounded transport call belongs here; never wait, perform I/O or run model code.</summary>
    public InputSegmentResult TryExecuteSegment(InputRun run, Func<int> inject)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(inject);
        if (!Monitor.TryEnter(_injectionSync)) return new(false, 0);
        try
        {
            var s = Volatile.Read(ref _state);
            if (!s.Ready || s.Reason != InputStopReason.None || !ReferenceEquals(s.Run, run) ||
                run.Token.IsCancellationRequested || !run.Session.IsCurrent(run.Lease)) return new(false, 0);
            // Admission linearizes here. Trip remains lock-free while this native call is in flight.
            try { return new(true, inject()); }
            catch { TripRun(InputStopReason.InputFault, run); throw; }
        }
        finally { Monitor.Exit(_injectionSync); }
    }

    /// <summary>Call only after the action worker has released its injected keys/buttons.</summary>
    public bool TryRelease(InputRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!Monitor.TryEnter(_injectionSync)) return false;
        try
        {
            var old = Volatile.Read(ref _state);
            if (!ReferenceEquals(old.Run, run)) return run.IsClosed;
            TripRun(InputStopReason.Stopped, run);
            if (!run.CancellationDrained || run.ActionActive || run.CleanupFailed) return false;
            while (true)
            {
                old = Volatile.Read(ref _state);
                if (!ReferenceEquals(old.Run, run)) return run.IsClosed;
                var next = old with { Revision = checked(old.Revision + 1), Run = null, RetiredLease = run.Lease.Lease };
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _state, next, old), old)) continue;
                run.CloseResources();
                return true;
            }
        }
        finally { Monitor.Exit(_injectionSync); }
    }
}

/// <summary>One epoch's input permit. LeaseWorker must still reserve the whole task until cleanup.</summary>
public sealed class InputRun
{
    private readonly InputSafetyGate _gate;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _cancelSync = new();
    private CancellationTokenRegistration _sourceRegistration;
    private Timer? _deadline;
    private Task? _cancellationTask;
    private bool _closed;
    private int _actionActive;
    private int _cleanupFailed;
    internal TaskLeaseSession Session { get; }
    public LeaseSnapshot Lease { get; }
    public CancellationToken Token { get; }
    public bool IsClosed { get { lock (_cancelSync) return _closed; } }
    public bool CancellationDrained { get { lock (_cancelSync) return _cancellationTask?.IsCompleted == true; } }
    public bool CancellationFailed { get { lock (_cancelSync) return _cancellationTask?.IsFaulted == true; } }
    public bool ActionActive => Volatile.Read(ref _actionActive) != 0;
    public bool CleanupFailed => Volatile.Read(ref _cleanupFailed) != 0;
    internal bool TryBeginAction() => Interlocked.CompareExchange(ref _actionActive, 1, 0) == 0;
    internal void EndAction() => Volatile.Write(ref _actionActive, 0);
    internal void MarkCleanupFailed() => Volatile.Write(ref _cleanupFailed, 1);

    internal InputRun(InputSafetyGate gate, TaskLeaseSession session, LeaseSnapshot lease)
    {
        (_gate, Session, Lease) = (gate, session, lease);
        Token = _cancellation.Token;
    }

    internal void Activate(TimeSpan maximumDuration)
    {
        _sourceRegistration = Lease.CancellationToken.UnsafeRegister(static state =>
        {
            var run = (InputRun)state!;
            run._gate.TripRun(InputStopReason.Stopped, run);
        }, this);
        _deadline = new Timer(static state =>
        {
            var run = (InputRun)state!;
            run._gate.TripRun(InputStopReason.Deadline, run);
        }, this, maximumDuration, Timeout.InfiniteTimeSpan);
    }

    internal void Cancel()
    {
        lock (_cancelSync)
        {
            if (_closed || _cancellationTask is not null) return;
            // .NET CancelAsync marks cancellation now, but invokes callbacks asynchronously.
            _cancellationTask = _cancellation.CancelAsync();
            _ = _cancellationTask.ContinueWith(static task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    internal void CloseResources()
    {
        lock (_cancelSync)
        {
            if (_closed) return;
            _closed = true;
            _deadline?.Dispose();
            _sourceRegistration.Unregister();
            _cancellation.Dispose();
        }
    }
}
