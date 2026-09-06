using DesktopAgent.Core.Domain;

namespace DesktopAgent.Core.Runtime;

/// <summary>
/// Owns the single task reservation and epoch/worker lifetime. This is NOT a native input gate:
/// D02 must also check its independent emergency gate before every injection segment.
/// </summary>
public sealed class TaskLeaseRegistry
{
    internal object Sync { get; } = new();
    private TaskLeaseSession? _active;

    public bool TryAcquire(Guid taskId, out TaskLeaseSession? session)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Task ID must be nonempty.", nameof(taskId));
        lock (Sync)
        {
            session = null;
            if (_active is not null) return false;
            session = _active = new TaskLeaseSession(this, taskId);
            return true;
        }
    }

    internal void Release(TaskLeaseSession session)
    {
        // Called while holding Sync, after all workers AND cancellation callbacks have drained.
        if (!ReferenceEquals(_active, session)) throw new InvalidOperationException("Lease owner mismatch.");
        _active = null;
    }
}

public sealed class LeaseSnapshot
{
    public Lease Lease { get; }
    public CancellationToken CancellationToken { get; }
    internal LeaseSnapshot(Lease lease, CancellationToken token) => (Lease, CancellationToken) = (lease, token);
}

public sealed class TaskLeaseSession
{
    private readonly TaskLeaseRegistry _registry;
    private readonly Guid _taskId;
    private readonly List<CancellationTokenSource> _sources = [];
    private long _epoch;
    private CancellationTokenSource _source;
    private LeaseSnapshot? _current;
    private int _workers;
    private bool _cancellationPending;
    private bool _completionRequested;
    private bool _closed;
    private Task _cancellationTask = Task.CompletedTask;

    internal TaskLeaseSession(TaskLeaseRegistry registry, Guid taskId)
    {
        (_registry, _taskId) = (registry, taskId);
        _source = new CancellationTokenSource();
        _sources.Add(_source);
        _current = new LeaseSnapshot(new Lease(taskId, 0), _source.Token);
    }

    public LeaseSnapshot Current
    {
        get
        {
            lock (_registry.Sync)
                return _current ?? throw new InvalidOperationException("No active epoch. Resume only after old work has drained.");
        }
    }

    public Lease Identity
    {
        get { lock (_registry.Sync) return new Lease(_taskId, _epoch); }
    }

    public bool IsCurrent(LeaseSnapshot snapshot)
    {
        lock (_registry.Sync) return IsCurrentLocked(snapshot);
    }

    private bool IsCurrentLocked(LeaseSnapshot snapshot) => !_closed && !_completionRequested &&
        ReferenceEquals(_current, snapshot) && !snapshot.CancellationToken.IsCancellationRequested;

    public bool TryStartWorker(LeaseSnapshot snapshot, out LeaseWorker? worker)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_registry.Sync)
        {
            worker = null;
            if (!IsCurrentLocked(snapshot) || _workers != 0) return false;
            _workers = 1;
            worker = new LeaseWorker(this, snapshot);
            return true;
        }
    }

    /// <summary>Serialize a small in-memory update with identity validation. Never perform I/O or injection here.</summary>
    public bool TryApply(LeaseSnapshot snapshot, Action update)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(update);
        lock (_registry.Sync)
        {
            if (!IsCurrentLocked(snapshot)) return false;
            update();
            return true;
        }
    }

    /// <summary>Immediately invalidates publication identity, then asynchronously cancels callbacks outside the lock.</summary>
    public Task InvalidateAsync() => InvalidateCore(requestCompletion: false);

    /// <summary>Seal any terminal outcome. Does not release the reservation until TryCompleteCleanup succeeds.</summary>
    public Task RequestCompletionAsync() => InvalidateCore(requestCompletion: true);

    private Task InvalidateCore(bool requestCompletion)
    {
        CancellationTokenSource source;
        TaskCompletionSource completion;
        lock (_registry.Sync)
        {
            if (_closed) return Task.CompletedTask;
            if (requestCompletion && _completionRequested) return _cancellationTask;
            if (requestCompletion) _completionRequested = true;
            _epoch = checked(_epoch + 1);
            if (_current is null) return _cancellationTask;
            _current = null;
            source = _source;
            _cancellationPending = true;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _cancellationTask = completion.Task;
        }
        _ = CancelOutsideLockAsync(source, completion);
        return completion.Task;
    }

    private async Task CancelOutsideLockAsync(CancellationTokenSource source, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        finally
        {
            lock (_registry.Sync) _cancellationPending = false;
        }
        if (failure is null) completion.TrySetResult();
        else completion.TrySetException(failure);
    }

    public LeaseSnapshot Resume()
    {
        lock (_registry.Sync)
        {
            if (_closed || _completionRequested || _current is not null || _workers != 0 || _cancellationPending)
                throw new InvalidOperationException("Cannot resume before the previous epoch has fully drained.");
            _epoch = checked(_epoch + 1);
            _source = new CancellationTokenSource();
            _sources.Add(_source);
            return _current = new LeaseSnapshot(new Lease(_taskId, _epoch), _source.Token);
        }
    }

    public bool TryCompleteCleanup()
    {
        CancellationTokenSource[] sources;
        lock (_registry.Sync)
        {
            if (_closed) return true;
            if (!_completionRequested || _current is not null || _workers != 0 || _cancellationPending) return false;
            _closed = true;
            sources = _sources.ToArray();
            _sources.Clear();
            _registry.Release(this);
        }
        foreach (var source in sources) source.Dispose();
        return true;
    }

    internal void EndWorker()
    {
        lock (_registry.Sync) _workers--;
    }
}

/// <summary>Keep this alive until the old async worker and its injection/cleanup calls have actually returned.</summary>
public sealed class LeaseWorker : IDisposable
{
    private TaskLeaseSession? _owner;
    public LeaseSnapshot Snapshot { get; }
    public CancellationToken CancellationToken => Snapshot.CancellationToken;
    internal LeaseWorker(TaskLeaseSession owner, LeaseSnapshot snapshot) => (_owner, Snapshot) = (owner, snapshot);
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndWorker();
}
