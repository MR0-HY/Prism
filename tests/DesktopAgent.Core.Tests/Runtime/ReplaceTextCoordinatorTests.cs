using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Runtime;

/// <summary>Coordinator integration with stateful simulated editor/executor only. No Windows UI or native input.</summary>
public sealed class ReplaceTextCoordinatorTests
{
    private const string Original = "新建 文本文档.txt";
    private const string Desired = "325.txt";
    private enum SelectionOutcome { Full, Partial, LostFocus }

    private sealed class EditorState
    {
        public string Value = Original;
        public bool Focused = true;
        public int? SelectionStart, SelectionLength;
        public SelectionOutcome Outcome;
    }

    private sealed class Desktop : IDesktopObserver
    {
        public static readonly PhysicalRect Bounds = new(0, 0, 1000, 800);
        public static readonly ForegroundIdentity Foreground = new("1", 100, "explorer", Bounds) { WindowClass = "CabinetWClass" };
        public readonly List<Frame> Frames = [];
        public Task<DesktopEnvironment> GetEnvironmentAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new DesktopEnvironment(1, [new("display", Bounds, Bounds, 120, 120, true)], Foreground, DesktopSessionState.Available));
        }
        public Task<Frame> CaptureAsync(Lease lease, string monitorId, PhysicalRect? physicalRegion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var region = physicalRegion ?? Bounds;
            var frame = new Frame("frame-" + (Frames.Count + 1), lease, DateTimeOffset.UtcNow, 1, monitorId, region,
                new(region.Width, region.Height, "image/png", [1, (byte)(Frames.Count + 1)]),
                physicalRegion is null ? FrameViewKind.Overview : FrameViewKind.Crop, Foreground, [],
                physicalRegion is null ? null : Frames.Last().Id);
            Frames.Add(frame);
            return Task.FromResult(frame);
        }
    }

    private sealed class Controls(EditorState state) : IControlObserver
    {
        private static readonly PhysicalRect EditorBounds = new(120, 100, 260, 28);
        public readonly List<ControlSnapshot> Observations = [];
        public Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = new ControlCandidate("edit", null, "文件名", "Edit", EditorBounds, true, state.Focused, true, null, null)
            { CurrentValue = state.Value, ValueTruncated = false, SelectionStart = state.SelectionStart, SelectionLength = state.SelectionLength };
            var snapshot = new ControlSnapshot(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow,
                ControlSnapshotStatus.Available, frame.PhysicalRegion.Contains(EditorBounds) ? [candidate] : []);
            Observations.Add(snapshot);
            return Task.FromResult(snapshot);
        }
        public Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ControlResolution(new(250, 114), controlId == "edit" ? null : "UNKNOWN_CONTROL"));
        }
    }

    private sealed class Input(EditorState state) : IInputExecutor
    {
        public readonly List<ValidatedAction> Actions = [];
        public Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Actions.Add(action);
            if (action.Action is HotkeyAction { Keys: [AgentKey.CTRL, AgentKey.A] })
            {
                if (state.Outcome == SelectionOutcome.LostFocus) state.Focused = false;
                else
                {
                    state.SelectionStart = 0;
                    state.SelectionLength = state.Outcome == SelectionOutcome.Full ? state.Value.Length : Math.Min(2, state.Value.Length);
                }
            }
            else if (action.Action is TextAction text)
            {
                // Simulate ordinary editor semantics: partial/absent selection really WOULD append or corrupt
                // the name if the coordinator erroneously sends text after an unverified Ctrl+A.
                int start = state.SelectionStart ?? state.Value.Length;
                int length = state.SelectionLength ?? 0;
                state.Value = state.Value.Remove(start, length).Insert(start, text.Text);
                state.SelectionStart = start + text.Text.Length;
                state.SelectionLength = 0;
            }
            return Task.FromResult(new ActionResult(action.ProposalId, lease.TaskId, lease.Epoch, ActionStatus.Injected,
                "SIMULATED_INPUT", DateTimeOffset.UtcNow, 1, 1, 1, "模拟输入，需后续核对", null));
        }
    }

    private sealed class Provider(EditorState state, bool ordinaryText) : IModelProvider
    {
        public readonly List<ModelRequest> Requests = [];
        public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            object decision;
            if (Requests.Count == 1)
            {
                object action = ordinaryText ? new { type = "text", text = Desired } :
                    new { type = "replace_existing", text = Desired, expectedCurrent = state.Value,
                        snapshotId = request.Controls!.Id, controlId = "edit" };
                decision = new { kind = "act", action, target = "当前文件名编辑框", expected = Desired };
            }
            else
            {
                bool matched = state.Value == Desired;
                decision = new { kind = "finish", outcome = matched ? "succeeded" : "partial",
                    summary = matched ? "已核对完整文件名325.txt" : "当前文件名尚未达标，未继续盲目输入",
                    evidence = new[] { new { frameId = request.CurrentFrame.Id, appName = "Explorer",
                        observedText = state.Value, interpretation = matched ? "文件名正确" : "保留当前结果供进一步判断" } } };
            }
            return Task.FromResult(new ProviderReply(JsonSerializer.Serialize(new
            {
                schemaVersion = 2, proposalId = Guid.NewGuid().ToString("N"), taskId = request.Task.Lease.TaskId,
                epoch = request.Task.Lease.Epoch, frameId = request.CurrentFrame.Id, current = "核对当前命名状态", next = "核对完整文件名", decision
            }), new(10, 4)));
        }
        public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Harness : IDisposable
    {
        public readonly EditorState State;
        public readonly Desktop Desktop = new();
        public readonly Controls Controls;
        public readonly Input Input;
        public readonly Provider Provider;
        public readonly InputSafetyGate Gate = new();
        public readonly DesktopTaskCoordinator Coordinator;
        private readonly ProviderProfile _profile;
        public Harness(SelectionOutcome outcome = SelectionOutcome.Full, bool ordinaryText = false, string initialValue = Original)
        {
            State = new() { Outcome = outcome, Value = initialValue };
            Controls = new(State); Input = new(State); Provider = new(State, ordinaryText);
            Gate.SetHotkeysReady(true); // Offline fixture only; no hotkeys or SendInput are invoked.
            _profile = ProviderConfiguration.DefaultDeepSeek();
            string fingerprint = ProviderConfiguration.Fingerprint(_profile);
            _profile = _profile with { ProbeFingerprint = fingerprint, Capabilities = new[] { "hybrid_vision", "hybrid_grounding", "hybrid_schema" }
                .Select(name => new CapabilityRecord(name, CapabilityStatus.ProbePassed, null, DateTimeOffset.UtcNow, fingerprint, "TEST_ONLY")).ToImmutableArray() };
            Coordinator = new(Gate, Desktop, Controls, Provider, _profile, new DesktopPolicyValidator(TimeSpan.FromSeconds(90)),
                _ => Input, controlsRequired: true);
        }
        public async Task Run()
        {
            await Coordinator.StartAsync(new("将当前正在命名的文本文件名改为325.txt并核对，不修改其他文件", ProviderConfiguration.Fingerprint(_profile),
                "display", new(6, 6, 10000)), default);
            await Coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public void Dispose() => Gate.Trip(InputStopReason.Shutdown);
    }

    [Fact]
    public async Task FullSelectionRequiresNewObservationBeforeTextAndFinalModelVerification()
    {
        using var h = new Harness();
        await h.Run();
        Assert.Equal(2, h.Input.Actions.Count);
        Assert.Equal(new[] { AgentKey.CTRL, AgentKey.A }, Assert.IsType<HotkeyAction>(h.Input.Actions[0].Action).Keys);
        Assert.Equal(Desired, Assert.IsType<TextAction>(h.Input.Actions[1].Action).Text);
        Assert.NotEqual(h.Input.Actions[0].FrameId, h.Input.Actions[1].FrameId);
        Assert.NotEqual(h.Input.Actions[1].FrameId, h.Provider.Requests.Last().CurrentFrame.Id);
        Assert.Contains(h.Controls.Observations, s => s.FrameId == h.Input.Actions[1].FrameId &&
            s.Candidates.Any(c => c.SelectionStart == 0 && c.SelectionLength == Original.Length));
        Assert.Equal(Desired, h.State.Value);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.Equal(2, h.Coordinator.Progress.Task.Usage.ActionsAttempted);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
        Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task PartialSelectionNeverFallsThroughToAppendingText()
    {
        using var h = new Harness(SelectionOutcome.Partial);
        await h.Run();
        var only = Assert.Single(h.Input.Actions);
        Assert.IsType<HotkeyAction>(only.Action);
        Assert.DoesNotContain(h.Input.Actions, a => a.Action is TextAction);
        Assert.Equal(Original, h.State.Value);
        Assert.NotEqual(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.True(h.Desktop.Frames.Count >= 2);
        Assert.True(h.Coordinator.Progress.Task.CleanupComplete);
    }

    [Fact]
    public async Task FocusLostAfterSelectAllNeverReceivesText()
    {
        using var h = new Harness(SelectionOutcome.LostFocus);
        await h.Run();
        Assert.IsType<HotkeyAction>(Assert.Single(h.Input.Actions).Action);
        Assert.Equal(Original, h.State.Value);
        Assert.DoesNotContain(h.Input.Actions, a => a.Action is TextAction);
        Assert.NotEqual(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.False(h.Gate.Status.IsOpen);
    }

    [Fact]
    public async Task AlreadyMatchingCurrentValueDoesNotIssueSelectionOrText()
    {
        using var h = new Harness(initialValue: Desired);
        await h.Run();
        Assert.Empty(h.Input.Actions);
        Assert.Equal(Desired, h.State.Value);
        Assert.Equal(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        Assert.Equal(0, h.Coordinator.Progress.Task.Usage.ActionsAttempted);
        Assert.True(h.Provider.Requests.Count >= 2);
    }

    [Fact]
    public async Task PlainTextCannotAppendARequestedNameToNonemptyExplorerEdit()
    {
        using var h = new Harness(ordinaryText: true);
        await h.Run();
        Assert.Empty(h.Input.Actions);
        Assert.Equal(Original, h.State.Value);
        Assert.NotEqual(TaskState.Succeeded, h.Coordinator.Progress!.Task.State);
        var recorded = h.Provider.Requests.SelectMany(r => r.RecentResults)
            .Concat(h.Coordinator.Progress.Task.RecentResults).Select(r => (r.Code ?? "") + " " + r.PublicSummary)
            .Append((h.Coordinator.Progress.Task.LastError?.Code ?? "") + " " + h.Coordinator.Progress.Summary);
        Assert.Contains(recorded, reason => reason.Contains("REPLAC", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("替换", StringComparison.Ordinal));
    }
}
