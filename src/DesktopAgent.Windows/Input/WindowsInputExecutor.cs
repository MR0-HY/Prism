using System.Diagnostics;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Observation;

namespace DesktopAgent.Windows.Input;

internal sealed class WindowsInputExecutor(WindowsDesktopObserver observer, InputSafetyGate gate, InputRun run, TimeSpan frameTtl) : IInputExecutor
{
    // The real Notepad mixed-text check corrupted a surrogate at 4 ms; the same
    // Unicode event sequence matched exactly at 40 ms. Keep cancellation between runes.
    private readonly InputGestureRunner _gestures = new(gate, new Win32InputDevice(), textDelayMs: 40);
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    public async Task<ActionResult> ExecuteAsync(Lease lease, ValidatedAction action, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        ActionResult Result(ActionStatus status, string code, string summary, int applied = 0, int expected = 0) =>
            new(action.ProposalId, lease.TaskId, lease.Epoch, status, code, started, timer.ElapsedMilliseconds, applied, expected, summary, null);
        if (lease != action.Lease || lease != run.Lease.Lease || !gate.Status.IsOpen || run.Token.IsCancellationRequested)
            return Result(ActionStatus.Rejected, "STALE_INPUT_PERMIT", "输入许可已失效。");
        lock (_consumed) if (!_consumed.Add(action.ProposalId)) return Result(ActionStatus.Rejected, "DUPLICATE_INPUT", "已处理过此动作，未再次输入。");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Token);
        bool attempted = false;
        try
        {
            var frame = observer.CurrentFrame;
            if (frame is null || frame.Id != action.FrameId || frame.Lease != lease) return Result(ActionStatus.Rejected, "STALE_FRAME_ID", "当前图像已更新。");
            string? stale = FrameChecks.Validate(frame, lease, await observer.GetEnvironmentAsync(cancelled.Token), DateTimeOffset.UtcNow, frameTtl);
            if (stale is not null) return Result(ActionStatus.Rejected, stale, "当前桌面已变化。");
            NormalizedPoint[] points = action.Action switch
            { MoveAction m => [m.Point], ClickAction c => [c.Point], ScrollAction s => [s.Point], DragAction d => [d.From, d.To], _ => [] };
            foreach (var point in points)
            {
                stale = await observer.CheckPointAsync(frame, lease, point, frameTtl, cancelled.Token);
                if (stale is not null) return Result(ActionStatus.Rejected, stale, "目标画面已变化，需重新观察。");
            }
            cancelled.Token.ThrowIfCancellationRequested();
            attempted = true;
            var result = await _gestures.ExecuteAsync(run, new(frame.Foreground, frame.PhysicalRegion, Win32InputDevice.VirtualDesktop()), action.Action, cancelled.Token);
            var status = result.Status switch { "applied" => ActionStatus.Injected, "cancelled" => ActionStatus.Cancelled, "rejected" => ActionStatus.Rejected, _ => ActionStatus.Uncertain };
            if (!result.CleanupComplete) { gate.Trip(InputStopReason.InputFault); status = ActionStatus.Uncertain; }
            if (status == ActionStatus.Injected && result.AppliedEvents > 0)
                observer.MarkInputApplied(action.Action is ClickAction { Button: MouseButton.Right } && DesktopNavigation.IsDesktopShell(frame.Foreground));
            return Result(status, result.Code ?? "INPUT_APPLIED", status == ActionStatus.Injected ? "已输入，等待观察实际结果。" : "动作已中断或被拒绝，请核对当前界面。",
                result.AppliedEvents, result.AttemptedEvents);
        }
        catch (OperationCanceledException) { return Result(ActionStatus.Cancelled, "CANCELLED", "输入已取消。"); }
        catch
        {
            if (attempted) gate.Trip(InputStopReason.InputFault);
            return Result(attempted ? ActionStatus.Uncertain : ActionStatus.Rejected, "INPUT_CHECK_FAILED", "输入检查或执行失败，已停止此动作。");
        }
    }
}
