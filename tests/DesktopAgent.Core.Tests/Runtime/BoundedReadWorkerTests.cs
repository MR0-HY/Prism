using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class BoundedReadWorkerTests
{
    private sealed record Value(int Number);
    [Fact]
    public async Task NativeTimeoutDoesNotBlockCallerOrCreateMoreReaders()
    {
        using var release = new ManualResetEventSlim();
        using var worker = new BoundedReadWorker("test native timeout");
        int calls = 0;
        try
        {
            var result = await worker.ReadAsync(() => { Interlocked.Increment(ref calls); release.Wait(); return new Value(1); }, TimeSpan.FromMilliseconds(50), default);
            Assert.Equal(ReadWorkerStatus.TimedOut, result.Status);
            var next = await worker.ReadAsync(() => { Interlocked.Increment(ref calls); return new Value(2); }, TimeSpan.FromMilliseconds(50), default);
            Assert.Equal(ReadWorkerStatus.Busy, next.Status);
            Assert.InRange(calls, 0, 1);
        }
        finally { release.Set(); }
    }
    [Fact]
    public async Task ReturnedTimedOutCallAllowsFreshReadWithoutPublishingOldValue()
    {
        using var release = new ManualResetEventSlim();
        using var worker = new BoundedReadWorker("test timeout recovery");
        int firstThread = 0, secondThread = 0;
        try
        {
            var old = await worker.ReadAsync(() => { firstThread = Environment.CurrentManagedThreadId; release.Wait(); return new Value(1); }, TimeSpan.FromMilliseconds(50), default);
            Assert.Equal(ReadWorkerStatus.TimedOut, old.Status); Assert.Null(old.Value);
            release.Set();
            ReadWorkerResult<Value> fresh;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            do
            {
                await Task.Delay(10, deadline.Token);
                fresh = await worker.ReadAsync(() => { secondThread = Environment.CurrentManagedThreadId; return new Value(2); }, TimeSpan.FromSeconds(1), deadline.Token);
            } while (fresh.Status == ReadWorkerStatus.Busy);
            Assert.Equal(ReadWorkerStatus.Completed, fresh.Status);
            Assert.Equal(2, fresh.Value!.Number); Assert.Equal(firstThread, secondThread);
            Assert.Null(old.Value);
        }
        finally { release.Set(); }
    }
    [Fact]
    public async Task CancellationDiscardsLateValueAndBusyReaderIsNotDuplicated()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var worker = new BoundedReadWorker("test cancellation");
        using var stop = new CancellationTokenSource();
        try
        {
            var pending = worker.ReadAsync(() => { entered.Set(); release.Wait(); return new Value(1); }, TimeSpan.FromSeconds(1), stop.Token);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            var busy = await worker.ReadAsync(() => new Value(2), TimeSpan.FromSeconds(1), default);
            Assert.Equal(ReadWorkerStatus.Busy, busy.Status);
        }
        finally { release.Set(); }
    }
    [Fact]
    public async Task FaultsAreUnavailableAndSuccessfulReadsRemainUsable()
    {
        using var worker = new BoundedReadWorker("test read");
        var failed = await worker.ReadAsync<Value>(() => throw new InvalidOperationException("private provider error"), TimeSpan.FromSeconds(1), default);
        Assert.Equal(ReadWorkerStatus.Unavailable, failed.Status);
        var completed = await worker.ReadAsync(() => new Value(2), TimeSpan.FromSeconds(1), default);
        Assert.Equal(2, completed.Value!.Number);
        worker.Dispose();
        Assert.Equal(ReadWorkerStatus.Unavailable, (await worker.ReadAsync(() => new Value(3), TimeSpan.FromSeconds(1), default)).Status);
    }
}
