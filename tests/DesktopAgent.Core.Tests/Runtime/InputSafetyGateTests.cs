using DesktopAgent.Core.Runtime;
using Xunit;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class InputSafetyGateTests
{
    private static (InputSafetyGate Gate, TaskLeaseSession Session) Create()
    {
        var registry = new TaskLeaseRegistry();
        Assert.True(registry.TryAcquire(Guid.NewGuid(), out var session));
        return (new InputSafetyGate(), session!);
    }

    private static InputRun Arm(InputSafetyGate gate, TaskLeaseSession session, TimeSpan? duration = null)
    {
        Assert.True(gate.TryArm(session, session.Current, gate.Status.Revision,
            duration ?? TimeSpan.FromSeconds(20), out var run));
        return run!;
    }

    private static async Task Release(InputSafetyGate gate, InputRun run, TaskLeaseSession session)
    {
        gate.Trip(InputStopReason.Stopped);
        for (int i = 0; i < 200 && !gate.TryRelease(run); i++) await Task.Delay(5);
        Assert.True(run.IsClosed);
        await session.RequestCompletionAsync();
        Assert.True(session.TryCompleteCleanup());
    }

    [Fact]
    public async Task RegistrationAndFreshUserRevisionAreRequired()
    {
        var (gate, session) = Create();
        Assert.False(gate.TryArm(session, session.Current, gate.Status.Revision, TimeSpan.FromSeconds(1), out _));
        gate.SetHotkeysReady(true);
        Assert.Equal(InputStopReason.None, gate.Status.Reason);
        long revision = gate.Status.Revision;
        gate.Trip(InputStopReason.Paused);
        Assert.False(gate.TryArm(session, session.Current, revision, TimeSpan.FromSeconds(1), out _));
        var run = Arm(gate, session);
        Assert.Equal(new InputSegmentResult(true, 2), gate.TryExecuteSegment(run, () => 2));
        gate.SetHotkeysReady(false);
        Assert.False(gate.TryExecuteSegment(run, () => throw new Exception("must not run")).Admitted);
        Assert.True(run.Token.IsCancellationRequested);
        gate.SetHotkeysReady(true);
        Assert.False(gate.Status.IsOpen); // Registration cannot resume an old run.
        await Release(gate, run, session);
    }

    [Fact]
    public async Task StopDoesNotWaitForInFlightSegmentAndDeniesEverySubsequentOne()
    {
        var (gate, session) = Create();
        gate.SetHotkeysReady(true);
        var run = Arm(gate, session);
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var inFlight = Task.Run(() => gate.TryExecuteSegment(run, () =>
        {
            entered.Set();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(5)));
            return 1;
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            // Bounded join would time out if Trip incorrectly waited for _injectionSync.
            await Task.Run(() => gate.Trip(InputStopReason.Stopped)).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(gate.Status.IsOpen);
            Assert.False(gate.TryRelease(run));
            Assert.False(gate.TryExecuteSegment(run, () => 99).Admitted);
        }
        finally { finish.Set(); }
        Assert.Equal(1, (await inFlight).AppliedCount);
        Assert.False(gate.TryExecuteSegment(run, () => 99).Admitted);
        await Release(gate, run, session);
    }

    [Fact]
    public async Task BlockingCancellationCallbackCannotDelayGateButPreventsPrematureReuse()
    {
        var (gate, session) = Create();
        gate.SetHotkeysReady(true);
        var run = Arm(gate, session);
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        using var callback = run.Token.Register(() => { entered.Set(); finish.Wait(TimeSpan.FromSeconds(5)); });
        try
        {
            gate.Trip(InputStopReason.Paused);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(gate.Status.IsOpen);
            Assert.False(gate.TryRelease(run));
            Assert.False(gate.TryArm(session, session.Current, gate.Status.Revision, TimeSpan.FromSeconds(1), out _));
        }
        finally { finish.Set(); }
        await Release(gate, run, session);
    }

    [Fact]
    public async Task ResumeRequiresFreshLeaseAndOldPermitNeverRunsAgain()
    {
        var (gate, session) = Create();
        gate.SetHotkeysReady(true);
        var old = Arm(gate, session);
        gate.Trip(InputStopReason.Paused);
        Assert.True(gate.TryRelease(old));
        Assert.False(gate.TryArm(session, session.Current, gate.Status.Revision, TimeSpan.FromSeconds(1), out _));
        await session.InvalidateAsync();
        session.Resume();
        var next = Arm(gate, session);
        Assert.False(gate.TryExecuteSegment(old, () => 1).Admitted);
        Assert.True(gate.TryExecuteSegment(next, () => 1).Admitted);
        await Release(gate, next, session);
    }

    [Fact]
    public async Task LeaseCancellationAndDeadlineCloseInputWithoutAnExternalController()
    {
        var (gate, session) = Create();
        gate.SetHotkeysReady(true);
        var run = Arm(gate, session);
        await session.InvalidateAsync();
        Assert.True(run.Token.IsCancellationRequested);
        Assert.False(gate.TryExecuteSegment(run, () => 1).Admitted);
        Assert.True(gate.TryRelease(run));
        session.Resume();
        var timed = Arm(gate, session, TimeSpan.FromMilliseconds(30));
        await Task.Delay(Timeout.Infinite, timed.Token).ContinueWith(t => Assert.True(t.IsCanceled)).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(InputStopReason.Deadline, gate.Status.Reason);
        Assert.False(gate.TryExecuteSegment(timed, () => 1).Admitted);
        await Release(gate, timed, session);
    }

    [Fact]
    public async Task FaultsAndShutdownFailClosed()
    {
        var (gate, session) = Create();
        gate.SetHotkeysReady(true);
        var run = Arm(gate, session);
        Assert.Throws<InvalidOperationException>(() => gate.TryExecuteSegment(run, () => throw new InvalidOperationException()));
        Assert.Equal(InputStopReason.InputFault, gate.Status.Reason);
        gate.Trip(InputStopReason.Shutdown);
        gate.Trip(InputStopReason.Paused);
        gate.SetHotkeysReady(true);
        Assert.Equal(InputStopReason.Shutdown, gate.Status.Reason);
        await Release(gate, run, session);
        Assert.False(gate.TryArm(session, run.Lease, gate.Status.Revision, TimeSpan.FromSeconds(1), out _));
        gate.SetHotkeysReady(false);
        Assert.False(gate.Status.HotkeysReady);
    }

    [Fact]
    public async Task FaultyCancellationCallbackIsObservedAndDoesNotLeakThePermit()
    {
        var (gate, session) = Create();
        gate.SetHotkeysReady(true);
        var run = Arm(gate, session);
        using var callback = run.Token.Register(() => throw new InvalidOperationException("test-only"));
        gate.Trip(InputStopReason.Stopped);
        for (int i = 0; i < 200 && !run.CancellationDrained; i++) await Task.Delay(5);
        Assert.True(run.CancellationFailed);
        Assert.False(gate.TryExecuteSegment(run, () => 1).Admitted);
        await Release(gate, run, session);
    }
}
