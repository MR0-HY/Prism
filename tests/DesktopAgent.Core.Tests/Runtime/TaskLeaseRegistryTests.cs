using System.Collections.Immutable;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class TaskLeaseRegistryTests
{
    [Fact]
    public async Task OnlyOneTaskAndOneWorkerMayOwnTheReservation()
    {
        var registry = new TaskLeaseRegistry();
        Assert.Throws<ArgumentException>(() => registry.TryAcquire(Guid.Empty, out _));
        Assert.True(registry.TryAcquire(Guid.NewGuid(), out var session));
        Assert.False(registry.TryAcquire(Guid.NewGuid(), out _));
        Assert.True(session!.TryStartWorker(session.Current, out var worker));
        Assert.False(session.TryStartWorker(session.Current, out _));
        worker!.Dispose();
        worker.Dispose(); // idempotent, cannot drive the worker count below zero
        Assert.True(session.TryStartWorker(session.Current, out var replacement));
        replacement!.Dispose();
        await session.RequestCompletionAsync();
        Assert.True(session.TryCompleteCleanup());
    }

    [Fact]
    public async Task IgnoredProviderCancellationCannotPublishAfterPauseOrResume()
    {
        var registry = new TaskLeaseRegistry();
        Assert.True(registry.TryAcquire(Guid.NewGuid(), out var session));
        var old = session!.Current;
        Assert.True(session.TryStartWorker(old, out var worker));
        var provider = new ControlledProvider();
        var pendingReply = provider.DecideAsync(RequestFor(old.Lease), worker!.CancellationToken);
        var published = new List<string>();

        await session.InvalidateAsync();
        Assert.True(worker.CancellationToken.IsCancellationRequested);
        Assert.False(session.IsCurrent(old));
        Assert.Throws<InvalidOperationException>(() => session.Resume());
        Assert.False(registry.TryAcquire(Guid.NewGuid(), out _));

        // Deliberately broken test provider returns a late result despite cancellation.
        provider.Reply.TrySetResult(new ProviderReply("late result", new ProviderUsage(null, null)));
        var result = await pendingReply;
        Assert.False(session.TryApply(old, () => published.Add(result.Content)));
        worker.Dispose();
        var fresh = session.Resume();
        Assert.True(fresh.Lease.Epoch > old.Lease.Epoch);
        Assert.False(session.TryApply(old, () => published.Add("old result after resume")));
        Assert.True(session.TryApply(fresh, () => published.Add("fresh result")));
        Assert.Equal(["fresh result"], published);
        await session.RequestCompletionAsync();
        Assert.True(session.TryCompleteCleanup());
    }

    [Fact]
    public async Task StopDoesNotAllowANewTaskUntilWorkerAndExplicitCleanupComplete()
    {
        var registry = new TaskLeaseRegistry();
        var id = Guid.NewGuid();
        Assert.True(registry.TryAcquire(id, out var session));
        var old = session!.Current;
        Assert.True(session.TryStartWorker(old, out var worker));
        await session.RequestCompletionAsync();
        var stoppedIdentity = session.Identity;
        await session.RequestCompletionAsync();
        Assert.Equal(stoppedIdentity, session.Identity); // repeated stop is idempotent
        Assert.False(session.TryCompleteCleanup());
        Assert.False(registry.TryAcquire(Guid.NewGuid(), out _));
        Assert.Throws<InvalidOperationException>(() => session.Resume());
        worker!.Dispose();
        Assert.False(registry.TryAcquire(Guid.NewGuid(), out _));
        Assert.True(session.TryCompleteCleanup());
        Assert.True(session.TryCompleteCleanup());
        // Even reusing an ID cannot make an old session/snapshot eligible again.
        Assert.True(registry.TryAcquire(id, out var next));
        Assert.False(session.TryApply(old, () => throw new Exception("Stale callback ran.")));
        Assert.False(next!.TryApply(old, () => throw new Exception("Foreign snapshot ran.")));
        await next.RequestCompletionAsync();
        Assert.True(next.TryCompleteCleanup());
    }

    [Fact]
    public async Task CancellationCallbacksDrainOutsideLockBeforeCleanupCanReleaseReservation()
    {
        var registry = new TaskLeaseRegistry();
        Assert.True(registry.TryAcquire(Guid.NewGuid(), out var session));
        var old = session!.Current;
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        using var registration = old.CancellationToken.Register(() =>
        {
            callbackEntered.TrySetResult();
            // Runs on CancelAsync's callback worker, never on the test thread.
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        });

        var stop = session.RequestCompletionAsync();
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stop.IsCompleted);
            Assert.False(session.IsCurrent(old)); // would deadlock if cancellation held the registry lock
            Assert.False(session.TryCompleteCleanup());
            Assert.False(registry.TryAcquire(Guid.NewGuid(), out _));
        }
        finally { releaseCallback.Set(); }
        await stop;
        Assert.True(session.TryCompleteCleanup());
    }

    [Fact]
    public async Task FaultyCancellationCallbackCannotRestorePublicationOrLeakTheReservation()
    {
        var registry = new TaskLeaseRegistry();
        Assert.True(registry.TryAcquire(Guid.NewGuid(), out var session));
        var old = session!.Current;
        using var registration = old.CancellationToken.Register(() => throw new InvalidOperationException("test callback"));
        await Assert.ThrowsAsync<AggregateException>(() => session.RequestCompletionAsync());
        Assert.False(session.IsCurrent(old));
        Assert.False(session.TryApply(old, () => throw new Exception("Should not run.")));
        Assert.True(session.TryCompleteCleanup());
        Assert.True(registry.TryAcquire(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task InvalidationAndResumeProduceFreshIdentitiesEvenWithoutWorkers()
    {
        var registry = new TaskLeaseRegistry();
        Assert.True(registry.TryAcquire(Guid.NewGuid(), out var session));
        var first = session!.Current;
        await session.InvalidateAsync();
        var paused = session.Identity;
        await session.InvalidateAsync();
        Assert.True(session.Identity.Epoch > paused.Epoch);
        var beforeResume = session.Identity;
        var second = session.Resume();
        Assert.True(second.Lease.Epoch > beforeResume.Epoch);
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        await session.RequestCompletionAsync();
        Assert.True(session.TryCompleteCleanup());
    }

    private static ModelRequest RequestFor(Lease lease)
    {
        var rect = new PhysicalRect(0, 0, 100, 100);
        var frame = new Frame("f-current", lease, DateTimeOffset.UtcNow, 1, "monitor-test", rect,
            new FrameImage(1, 1, "image/png", [1, 2, 3]), FrameViewKind.Overview,
            new ForegroundIdentity("0x123", 42, "SyntheticFixture", rect), []);
        return new ModelRequest(new ModelTaskSnapshot(lease, "synthetic goal", TaskState.Running,
            "test-fingerprint", "monitor-test", TaskBudget.Default, new TaskUsage(0, 0, 0, null, null)),
            frame, [], "test-only prompt", new ProposalScope(lease.TaskId, lease.Epoch, frame.Id));
    }

    private sealed class ControlledProvider : IModelProvider
    {
        public TaskCompletionSource<ProviderReply> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct) => Reply.Task;
        public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct)
            => throw new NotSupportedException("No network or probe in the cancellation fixture.");
    }
}
