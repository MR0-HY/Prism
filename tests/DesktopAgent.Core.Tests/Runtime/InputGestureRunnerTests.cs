using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using Xunit;

namespace DesktopAgent.Core.Tests.Runtime;

public sealed class InputGestureRunnerTests
{
    private static readonly InputTarget Target = new(new("42", 123, "fixture", new(0, 0, 800, 600)), new(0, 0, 800, 600), new(0, 0, 800, 600));
    private sealed class Device : IInputDevice
    {
        public List<DeviceInput> Events { get; } = [];
        public HashSet<int> Held { get; } = [];
        public bool Current = true;
        public Func<DeviceInput[], int>? SendOverride;
        public Action<DeviceInput[]>? Sent;
        public bool TargetIsCurrent(InputTarget target) => Current;
        public bool PointIsOnDisplay(PhysicalPoint point) => point.X is >= 0 and < 800 && point.Y is >= 0 and < 600;
        public bool IsKeyDown(int virtualKey) => Held.Contains(virtualKey);
        public PhysicalPoint CursorPosition() => new(10, 10);
        public int Send(ReadOnlySpan<DeviceInput> inputs, PhysicalRect desktop)
        {
            var batch = inputs.ToArray();
            int count = SendOverride?.Invoke(batch) ?? batch.Length;
            Events.AddRange(batch.Take(count));
            Sent?.Invoke(batch);
            return count;
        }
    }
    private static (InputSafetyGate Gate, InputRun Run, TaskLeaseSession Session) Start()
    {
        var registry = new TaskLeaseRegistry();
        Assert.True(registry.TryAcquire(Guid.NewGuid(), out var session));
        var gate = new InputSafetyGate();
        gate.SetHotkeysReady(true);
        Assert.True(gate.TryArm(session!, session!.Current, gate.Status.Revision, TimeSpan.FromSeconds(10), out var run));
        return (gate, run!, session);
    }
    private static async Task Cleanup(InputSafetyGate gate, InputRun run, TaskLeaseSession session)
    {
        gate.Trip(InputStopReason.Stopped);
        for (int i = 0; i < 200 && !gate.TryRelease(run); i++) await Task.Delay(5);
        Assert.True(run.IsClosed);
        await session.RequestCompletionAsync();
        Assert.True(session.TryCompleteCleanup());
    }

    [Fact]
    public async Task UnicodeUsesPairedUnitsAndPreservesEmojiWithoutSubmit()
    {
        var (gate, run, session) = Start();
        var device = new Device();
        var result = await new InputGestureRunner(gate, device).ExecuteAsync(run, Target, new TextAction("中A😀"), default);
        Assert.Equal("applied", result.Status);
        Assert.Equal(8, device.Events.Count);
        Assert.Equal("中A😀", new string(device.Events.Where(e => e.Kind == DeviceEventKind.UnicodeDown).Select(e => (char)e.Code).ToArray()));
        Assert.All(device.Events, e => Assert.Contains(e.Kind, new[] { DeviceEventKind.UnicodeDown, DeviceEventKind.UnicodeUp }));
        await Cleanup(gate, run, session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowTextCancellationOrStopAllowsNoRuneAfterTheAdmittedPair(bool stopGate)
    {
        var (gate, run, session) = Start();
        using var cancellation = new CancellationTokenSource();
        var device = new Device();
        device.Sent = _ =>
        {
            if (stopGate) gate.Trip(InputStopReason.Stopped);
            else cancellation.Cancel();
        };

        var result = await new InputGestureRunner(gate, device, textDelayMs: 40)
            .ExecuteAsync(run, Target, new TextAction("😀must-not-send"), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("cancelled", result.Status);
        Assert.Equal("CANCELLED", result.Code);
        Assert.True(result.CleanupComplete);
        Assert.False(run.ActionActive);
        Assert.Equal(4, result.AppliedEvents);
        Assert.Equal(4, result.AttemptedEvents);
        Assert.Equal(new[]
        {
            new DeviceInput(DeviceEventKind.UnicodeDown, Code: 0xD83D),
            new DeviceInput(DeviceEventKind.UnicodeUp, Code: 0xD83D),
            new DeviceInput(DeviceEventKind.UnicodeDown, Code: 0xDE00),
            new DeviceInput(DeviceEventKind.UnicodeUp, Code: 0xDE00)
        }, device.Events);
        await Cleanup(gate, run, session);
    }

    [Fact]
    public void TextDelayOutsideDiagnosticRangeIsRejectedAtConstruction()
    {
        var gate = new InputSafetyGate();
        var device = new Device();
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputGestureRunner(gate, device, textDelayMs: 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputGestureRunner(gate, device, textDelayMs: 51));
        Assert.Empty(device.Events);
    }

    [Fact]
    public async Task PartialChordOnlyReleasesAcceptedDownsAndNeverReplays()
    {
        var (gate, run, session) = Start();
        var device = new Device();
        int sends = 0;
        device.SendOverride = input => ++sends == 1 ? 1 : input.Length;
        var result = await new InputGestureRunner(gate, device).ExecuteAsync(run, Target,
            new HotkeyAction([AgentKey.CTRL, AgentKey.A]), default);
        Assert.Equal("INPUT_UNCERTAIN", result.Code);
        Assert.Equal(new[] { new DeviceInput(DeviceEventKind.KeyDown, Code: 0x11), new(DeviceEventKind.KeyUp, Code: 0x11) }, device.Events);
        Assert.Equal(2, sends);
        await Cleanup(gate, run, session);
    }

    [Fact]
    public async Task UserHeldModifierRejectsWithoutReleasingUsersKey()
    {
        var (gate, run, session) = Start();
        var device = new Device();
        device.Held.Add(0x11);
        var result = await new InputGestureRunner(gate, device).ExecuteAsync(run, Target, new TextAction("a"), default);
        Assert.Equal("USER_INPUT_HELD", result.Code);
        Assert.Empty(device.Events);
        Assert.Contains(0x11, device.Held);
        await Cleanup(gate, run, session);
    }

    [Fact]
    public async Task StopDuringDragReleasesButtonAndAdmitsNoMoreMovement()
    {
        var (gate, run, session) = Start();
        var device = new Device();
        device.Sent = batch => { if (batch.Any(e => e.Kind == DeviceEventKind.LeftDown)) gate.Trip(InputStopReason.Stopped); };
        var result = await new InputGestureRunner(gate, device).ExecuteAsync(run, Target, new DragAction(new(100, 100), new(600, 600), 500), default);
        Assert.Equal("cancelled", result.Status);
        Assert.True(result.CleanupComplete);
        Assert.Equal(DeviceEventKind.LeftUp, device.Events[^1].Kind);
        int down = device.Events.FindIndex(e => e.Kind == DeviceEventKind.LeftDown);
        Assert.DoesNotContain(device.Events.Skip(down + 1), e => e.Kind == DeviceEventKind.Move);
        await Cleanup(gate, run, session);
    }

    [Fact]
    public async Task LostForegroundStopsNextTextSegment()
    {
        var (gate, run, session) = Start();
        var device = new Device();
        device.Sent = _ => device.Current = false;
        var result = await new InputGestureRunner(gate, device).ExecuteAsync(run, Target, new TextAction("abcdef"), default);
        Assert.Equal("TARGET_CHANGED", result.Code);
        Assert.Equal(2, device.Events.Count);
        await Cleanup(gate, run, session);
    }

    [Fact]
    public async Task GestureReservationSurvivesAwaitsAndPreventsEarlyRelease()
    {
        var (gate, run, session) = Start();
        var device = new Device();
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.Sent = _ => firstSent.TrySetResult();
        var runner = new InputGestureRunner(gate, device);
        var first = runner.ExecuteAsync(run, Target, new TextAction(new string('a', 1000)), default);
        await firstSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await new InputGestureRunner(gate, device).ExecuteAsync(run, Target, new TextAction("wrong"), default);
        Assert.Equal("INPUT_BUSY", second.Code);
        Assert.False(gate.TryRelease(run));
        Assert.Equal("cancelled", (await first.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        await Cleanup(gate, run, session);
    }

    [Fact]
    public async Task InvalidSecondDragPointRejectsBeforeMoving()
    {
        var (gate, run, session) = Start();
        var device = new Device();
        var result = await new InputGestureRunner(gate, device).ExecuteAsync(run, Target, new DragAction(new(500, 500), new(double.NaN, 0), 500), default);
        Assert.NotEqual("applied", result.Status);
        Assert.Empty(device.Events);
        await Cleanup(gate, run, session);
    }

    [Theory]
    [InlineData(0, 0, -1920, -200)]
    [InlineData(1000, 1000, -1, 879)]
    [InlineData(500, 500, -960, 340)]
    public void PhysicalMappingIncludesNegativeOriginsAndEdges(double x, double y, int expectedX, int expectedY)
        => Assert.Equal(new PhysicalPoint(expectedX, expectedY), InputCoordinates.ToPhysical(new(x, y), new(-1920, -200, 1920, 1080)));

    [Fact]
    public void AbsoluteMappingRejectsInvalidAndOutsideDesktop()
    {
        Assert.Equal(new PhysicalPoint(65535, 65535), InputCoordinates.ToAbsolute(new(-1, 879), new(-1920, -200, 1920, 1080)));
        Assert.Equal(new PhysicalPoint(0, 0), InputCoordinates.ToAbsolute(new(-1920, -200), new(-1920, -200, 1920, 1080)));
        Assert.Throws<ArgumentOutOfRangeException>(() => InputCoordinates.ToAbsolute(new(0, 0), new(0, 0, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => InputCoordinates.ToAbsolute(new(-1, 0), new(0, 0, 800, 600)));
    }
}
