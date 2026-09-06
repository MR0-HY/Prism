using System.Collections.Immutable;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Providers;

namespace DesktopAgent.Core.Tests.Providers;

public sealed class ProbeCheckpointTests
{
    private static readonly ProviderProfile Profile = ProviderConfiguration.DefaultDeepSeek();
    private static GeneratedProbeImage Case(int index) => new("journal-" + index,
        new(1001, 1001, "image/png", [1, 2, 3]), new(ProbeKind.GroundPoint, "K9R2VC", null, new(450, 450, 100, 100)));
    private static ImmutableArray<GeneratedProbeImage> Pair => [Case(0), Case(1)];
    private static ProviderReply Answer(int index) => new(
        $$"""{"frameId":"journal-{{index}}","type":"point","code":"K9R2VC","x":500,"y":500}""", new(10, 4));

    [Fact]
    public async Task CompletedFirstResultIsSavedBeforeSecondRequestFinishes()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource<ProviderReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new List<ProbeCheckpoint>();
        int count = 0;
        var running = VisualProbe.RunGroundingDiagnosticAsync(Profile, Pair, async (_, _) =>
        {
            if (count++ == 0) return Answer(0);
            secondStarted.SetResult();
            return await finishSecond.Task;
        }, default, (point, _) => { journal.Add(point); return Task.CompletedTask; });
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(running.IsCompleted);
            Assert.Equal(new[] { "before-send", "completed", "before-send" }, journal.Select(p => p.Stage));
            Assert.Equal(new[] { 0, 0, 1 }, journal.Select(p => p.Index));
            Assert.True(journal[1].Attempt!.Passed);
            Assert.Equal("baseline", journal[1].PromptVariant);
            Assert.Equal(Answer(0).Content, journal[1].Attempt!.Diagnostic!.FinalContent);
            Assert.Equal("row-aligned-v1", journal[2].PromptVariant);
        }
        finally { finishSecond.TrySetResult(Answer(1)); }
        var report = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, count);
        Assert.Equal(4, journal.Count);
        Assert.Equal(report.Attempts[1], journal[3].Attempt);
        Assert.True(report.DiagnosticOnly);
        Assert.All(report.Results, r => Assert.Equal(CapabilityStatus.Unknown, r.Status));
    }

    [Theory]
    [InlineData("before-send", 0)]
    [InlineData("completed", 1)]
    public async Task JournalFailureStopsBeforeAnyFurtherPaidRequest(string failingStage, int expectedSends)
    {
        int sends = 0;
        await Assert.ThrowsAsync<IOException>(() => VisualProbe.RunGroundingDiagnosticAsync(Profile, Pair,
            (_, _) => Task.FromResult(Answer(sends++)), default, (point, _) =>
            {
                if (point.Stage == failingStage) throw new IOException("simulated disk failure");
                return Task.CompletedTask;
            }));
        Assert.Equal(expectedSends, sends);
    }

    [Fact]
    public async Task CancellationDuringSendStillPersistsTheCancelledAttemptWithoutACancelledSaveToken()
    {
        using var stop = new CancellationTokenSource();
        var journal = new List<ProbeCheckpoint>();
        int sends = 0;
        var report = await VisualProbe.RunGroundingDiagnosticAsync(Profile, Pair, (_, _) =>
        {
            sends++;
            stop.Cancel();
            return Task.FromResult(Answer(0));
        }, stop.Token, (point, token) =>
        {
            if (point.Stage == "completed") Assert.False(token.CanBeCanceled);
            journal.Add(point);
            return Task.CompletedTask;
        });
        Assert.Equal(1, sends);
        Assert.True(report.Cancelled);
        Assert.Equal(2, journal.Count);
        Assert.Equal("CANCELLED", journal[1].Attempt!.ErrorCode);
        Assert.False(journal[1].Attempt!.Passed);
        Assert.Equal(new ProviderUsage(10, 4), journal[1].Attempt!.Usage);
        Assert.All(report.Results, r => Assert.Equal(CapabilityStatus.Unknown, r.Status));
    }

    [Fact]
    public async Task CancelledBeforeDispatchDoesNotSendEvenIfBeforeSendWasWritten()
    {
        using var stop = new CancellationTokenSource();
        int sends = 0, checkpoints = 0;
        var report = await VisualProbe.RunGroundingDiagnosticAsync(Profile, Pair,
            (_, _) => { sends++; return Task.FromResult(Answer(0)); }, stop.Token, (point, _) =>
            {
                Assert.Equal("before-send", point.Stage);
                checkpoints++;
                stop.Cancel();
                return Task.CompletedTask;
            });
        Assert.Equal(0, sends);
        Assert.Equal(1, checkpoints);
        Assert.True(report.Cancelled);
        Assert.Equal(0, report.ApiAttempts);
    }

    [Fact]
    public async Task TransportFailureIsSavedBeforeStoppingWithoutRetry()
    {
        var journal = new List<ProbeCheckpoint>();
        int sends = 0;
        var report = await VisualProbe.RunGroundingDiagnosticAsync(Profile, Pair,
            (_, _) => { sends++; throw new ProviderCallException("NETWORK_ERROR"); }, default,
            (point, _) => { journal.Add(point); return Task.CompletedTask; });
        Assert.Equal(1, sends);
        Assert.Equal(2, journal.Count);
        Assert.Equal("NETWORK_ERROR", journal[1].Attempt!.ErrorCode);
        Assert.Equal(report.Attempts[0], journal[1].Attempt);
    }
}
