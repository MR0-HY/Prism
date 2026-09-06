using System.Collections.Concurrent;

namespace DesktopAgent.Core.Runtime;

public enum ReadWorkerStatus { Completed, Busy, TimedOut, Unavailable }
public sealed record ReadWorkerResult<T>(ReadWorkerStatus Status, T? Value) where T : class;

/// <summary>One background reader only. A timed-out native call stays busy until it actually returns; no additional reader is created.</summary>
public sealed class BoundedReadWorker : IDisposable
{
    private readonly BlockingCollection<Action> _jobs = new(1);
    private readonly Thread _thread;
    private int _busy, _disposed;
    public BoundedReadWorker(string name)
    {
        _thread = new Thread(() => { foreach (var job in _jobs.GetConsumingEnumerable()) job(); }) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }
    public async Task<ReadWorkerResult<T>> ReadAsync<T>(Func<T> read, TimeSpan timeout, CancellationToken ct) where T : class
    {
        if (timeout < TimeSpan.FromMilliseconds(10) || timeout > TimeSpan.FromSeconds(3)) throw new ArgumentOutOfRangeException(nameof(timeout));
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0) return new(ReadWorkerStatus.Unavailable, null);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return new(ReadWorkerStatus.Busy, null);
        var completion = new TaskCompletionSource<ReadWorkerResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!_jobs.TryAdd(() =>
            {
                ReadWorkerResult<T> result;
                try { result = new(ReadWorkerStatus.Completed, read()); }
                catch { result = new(ReadWorkerStatus.Unavailable, null); }
                finally { Volatile.Write(ref _busy, 0); }
                completion.TrySetResult(result);
            })) { Volatile.Write(ref _busy, 0); return new(ReadWorkerStatus.Unavailable, null); }
        }
        catch (InvalidOperationException) { Volatile.Write(ref _busy, 0); return new(ReadWorkerStatus.Unavailable, null); }
        try { return await completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            return new(ReadWorkerStatus.TimedOut, null);
        }
        // Cancellation ends caller waiting; the one existing read may finish, but cannot publish to caller.
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _jobs.CompleteAdding();
        // Never join an untrusted native provider or abort it while it owns COM state.
    }
}
