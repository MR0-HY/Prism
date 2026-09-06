using System.Collections.Immutable;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Runtime;

public readonly record struct PhysicalPoint(int X, int Y);
public enum DeviceEventKind { Move, LeftDown, LeftUp, RightDown, RightUp, Wheel, KeyDown, KeyUp, UnicodeDown, UnicodeUp }
public readonly record struct DeviceInput(DeviceEventKind Kind, int X = 0, int Y = 0, int Code = 0);
public sealed record InputTarget(ForegroundIdentity Foreground, PhysicalRect Viewport, PhysicalRect VirtualDesktop);
public sealed record GestureResult(string Status, string? Code, int AppliedEvents, int AttemptedEvents, bool CleanupComplete);

/// <summary>Platform boundary only. A provider must never receive this interface.</summary>
public interface IInputDevice
{
    bool TargetIsCurrent(InputTarget target);
    bool PointIsOnDisplay(PhysicalPoint point);
    bool IsKeyDown(int virtualKey);
    PhysicalPoint CursorPosition();
    int Send(ReadOnlySpan<DeviceInput> inputs, PhysicalRect virtualDesktop);
}

public static class InputCoordinates
{
    public static PhysicalPoint ToPhysical(NormalizedPoint point, PhysicalRect region)
    {
        if (!region.IsValid || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < 0 || point.X > 1000 || point.Y < 0 || point.Y > 1000)
            throw new ArgumentOutOfRangeException(nameof(point));
        return new(checked(region.Left + (int)Math.Round(point.X * (region.Width - 1) / 1000, MidpointRounding.AwayFromZero)),
            checked(region.Top + (int)Math.Round(point.Y * (region.Height - 1) / 1000, MidpointRounding.AwayFromZero)));
    }

    public static PhysicalPoint ToAbsolute(PhysicalPoint point, PhysicalRect desktop)
    {
        if (desktop.Width <= 1 || desktop.Height <= 1 || point.X < desktop.Left || point.X >= desktop.Right ||
            point.Y < desktop.Top || point.Y >= desktop.Bottom) throw new ArgumentOutOfRangeException(nameof(point));
        return new((int)Math.Round(((long)point.X - desktop.Left) * 65535d / (desktop.Width - 1), MidpointRounding.AwayFromZero),
            (int)Math.Round(((long)point.Y - desktop.Top) * 65535d / (desktop.Height - 1), MidpointRounding.AwayFromZero));
    }
}

/// <summary>
/// Low-level gestures only, not a policy authorizer. The production adapter must accept a
/// ValidatedAction and current observation; the explicit native fixture diagnostic uses local truth.
/// </summary>
public sealed class InputGestureRunner(InputSafetyGate gate, IInputDevice device, int textDelayMs = 4)
{
    private readonly int _textDelayMs = textDelayMs is >= 4 and <= 50
        ? textDelayMs : throw new ArgumentOutOfRangeException(nameof(textDelayMs));
    private static readonly int[] ConflictKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0x01, 0x02, 0x04];

    public async Task<GestureResult> ExecuteAsync(InputRun run, InputTarget target, AgentAction action, CancellationToken ct)
    {
        if (!run.TryBeginAction()) return new("rejected", "INPUT_BUSY", 0, 0, true);
        var held = new List<DeviceInput>();
        int applied = 0, attempted = 0;
        string status = "applied";
        string? code = null;
        bool cleanup = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Token);
        try
        {
            ValidateAction(action); // Validate the whole gesture before sending any event.
            if (!device.TargetIsCurrent(target)) throw new GestureRejected("TARGET_CHANGED");
            if (ConflictKeys.Any(device.IsKeyDown)) throw new GestureRejected("USER_INPUT_HELD");

            void Send(params DeviceInput[] inputs)
            {
                linked.Token.ThrowIfCancellationRequested();
                var result = gate.TryExecuteSegment(run, () =>
                {
                    // Recheck at the actual injection boundary, after any await/trajectory segment.
                    if (!device.TargetIsCurrent(target)) throw new GestureRejected("TARGET_CHANGED");
                    foreach (int key in ConflictKeys)
                        if (!OwnsKey(held, key) && device.IsKeyDown(key)) throw new GestureRejected("USER_INPUT_HELD");
                    foreach (var input in inputs)
                        if (input.Kind == DeviceEventKind.Move && !device.PointIsOnDisplay(new(input.X, input.Y)))
                            throw new GestureRejected("DISPLAY_GAP");
                    attempted += inputs.Length;
                    int count = device.Send(inputs, target.VirtualDesktop);
                    if (count < 0 || count > inputs.Length) throw new GestureRejected("INVALID_INPUT_COUNT");
                    applied += count;
                    for (int i = 0; i < count; i++) Track(held, inputs[i]);
                    return count;
                });
                if (!result.Admitted) throw new OperationCanceledException(linked.Token);
                if (result.AppliedCount != inputs.Length) throw new GestureRejected("INPUT_UNCERTAIN");
            }

            async Task Move(NormalizedPoint point, int durationMs)
            {
                var destination = InputCoordinates.ToPhysical(point, target.Viewport);
                _ = InputCoordinates.ToAbsolute(destination, target.VirtualDesktop);
                if (!device.PointIsOnDisplay(destination)) throw new GestureRejected("DISPLAY_GAP");
                var start = device.CursorPosition();
                int steps = Math.Max(1, durationMs / 12);
                for (int step = 1; step <= steps; step++)
                {
                    double progress = step / (double)steps;
                    int x = (int)Math.Round(start.X + ((long)destination.X - start.X) * progress);
                    int y = (int)Math.Round(start.Y + ((long)destination.Y - start.Y) * progress);
                    Send(new DeviceInput(DeviceEventKind.Move, x, y));
                    if (step != steps) await Task.Delay(12, linked.Token).ConfigureAwait(false);
                }
            }

            switch (action)
            {
                case MoveAction move: await Move(move.Point, 156); break;
                case ClickAction click:
                    await Move(click.Point, 156);
                    for (int n = 0; n < click.ClickCount; n++)
                    {
                        Send(new DeviceInput(click.Button == MouseButton.Left ? DeviceEventKind.LeftDown : DeviceEventKind.RightDown),
                            new(click.Button == MouseButton.Left ? DeviceEventKind.LeftUp : DeviceEventKind.RightUp));
                        if (n + 1 < click.ClickCount) await Task.Delay(60, linked.Token).ConfigureAwait(false);
                    }
                    break;
                case ScrollAction scroll:
                    await Move(scroll.Point, 156);
                    Send(new DeviceInput(DeviceEventKind.Wheel, Code: scroll.Delta * 120));
                    break;
                case DragAction drag:
                    await Move(drag.From, 156);
                    Send(new DeviceInput(DeviceEventKind.LeftDown));
                    await Move(drag.To, drag.DurationMs);
                    Send(new DeviceInput(DeviceEventKind.LeftUp));
                    break;
                case HotkeyAction hotkey:
                    var keys = hotkey.Keys.Select(VirtualKey).ToArray();
                    if (keys.Any(device.IsKeyDown)) throw new GestureRejected("USER_INPUT_HELD");
                    Send(keys.Select(k => new DeviceInput(DeviceEventKind.KeyDown, Code: k))
                        .Concat(keys.Reverse().Select(k => new DeviceInput(DeviceEventKind.KeyUp, Code: k))).ToArray());
                    break;
                case TextAction text:
                    foreach (var rune in text.Text.EnumerateRunes())
                    {
                        Send(rune.ToString().SelectMany(ch => new[] { new DeviceInput(DeviceEventKind.UnicodeDown, Code: ch),
                            new DeviceInput(DeviceEventKind.UnicodeUp, Code: ch) }).ToArray());
                        await Task.Delay(_textDelayMs, linked.Token).ConfigureAwait(false);
                    }
                    break;
                default: throw new GestureRejected("UNKNOWN_ACTION");
            }
        }
        catch (OperationCanceledException) { status = "cancelled"; code = "CANCELLED"; }
        catch (GestureRejected error)
        {
            status = error.Code == "INPUT_UNCERTAIN" ? "uncertain" : "rejected";
            code = error.Code;
            gate.Trip(InputStopReason.InputFault);
        }
        catch { status = "uncertain"; code = "INPUT_FAILURE"; gate.Trip(InputStopReason.InputFault); }
        finally
        {
            // Release only downs accepted from this gesture, even when the gate is now closed.
            // Cleanup contains no clicks, movement, text or new key-downs, and never replays the action.
            if (held.Count != 0)
            {
                var releases = held.AsEnumerable().Reverse().Select(ReleaseOf).ToArray();
                try { cleanup = device.Send(releases, target.VirtualDesktop) == releases.Length; }
                catch { cleanup = false; }
                if (!cleanup) { run.MarkCleanupFailed(); gate.Trip(InputStopReason.InputFault); status = "uncertain"; code = "RELEASE_UNCERTAIN"; }
            }
            run.EndAction();
        }
        return new(status, code, applied, attempted, cleanup);
    }

    private static bool OwnsKey(List<DeviceInput> held, int key) => held.Any(i =>
        (i.Kind == DeviceEventKind.KeyDown && i.Code == key) ||
        (i.Kind == DeviceEventKind.LeftDown && key == 1) || (i.Kind == DeviceEventKind.RightDown && key == 2));
    private static DeviceInput ReleaseOf(DeviceInput input) => input with { Kind = input.Kind switch
    {
        DeviceEventKind.KeyDown => DeviceEventKind.KeyUp, DeviceEventKind.UnicodeDown => DeviceEventKind.UnicodeUp,
        DeviceEventKind.LeftDown => DeviceEventKind.LeftUp, DeviceEventKind.RightDown => DeviceEventKind.RightUp,
        _ => throw new InvalidOperationException("Only owned downs may be released.")
    } };
    private static void Track(List<DeviceInput> held, DeviceInput input)
    {
        if (input.Kind is DeviceEventKind.KeyDown or DeviceEventKind.UnicodeDown or DeviceEventKind.LeftDown or DeviceEventKind.RightDown) held.Add(input);
        else if (input.Kind is DeviceEventKind.KeyUp or DeviceEventKind.UnicodeUp or DeviceEventKind.LeftUp or DeviceEventKind.RightUp)
        {
            int index = held.FindLastIndex(i => ReleaseOf(i) == input);
            if (index >= 0) held.RemoveAt(index);
        }
    }

    private static void ValidateAction(AgentAction action)
    {
        static void Point(NormalizedPoint p) { _ = InputCoordinates.ToPhysical(p, new PhysicalRect(0, 0, 100, 100)); }
        switch (action)
        {
            case MoveAction m: Point(m.Point); break;
            case ClickAction c when c.ClickCount is 1 or 2 && Enum.IsDefined(c.Button): Point(c.Point); break;
            case ScrollAction s when s.Delta is >= -5 and <= 5 && s.Delta != 0: Point(s.Point); break;
            case DragAction d when d.DurationMs is >= 200 and <= 2000: Point(d.From); Point(d.To); break;
            case TextAction t when !string.IsNullOrEmpty(t.Text) && t.Text.Length <= 1000:
                for (int i = 0; i < t.Text.Length; i++)
                {
                    char c = t.Text[i];
                    if (char.IsControl(c) || c is '\u2028' or '\u2029') throw new GestureRejected("TEXT_CONTROL");
                    if (char.IsHighSurrogate(c)) { if (++i >= t.Text.Length || !char.IsLowSurrogate(t.Text[i])) throw new GestureRejected("INVALID_UNICODE"); }
                    else if (char.IsLowSurrogate(c)) throw new GestureRejected("INVALID_UNICODE");
                }
                break;
            case HotkeyAction h when !h.Keys.IsDefaultOrEmpty && h.Keys.Length <= 4 && h.Keys.Distinct().Count() == h.Keys.Length:
                bool ordinary = false;
                foreach (var key in h.Keys)
                {
                    _ = VirtualKey(key);
                    bool modifier = key is AgentKey.CTRL or AgentKey.ALT or AgentKey.SHIFT or AgentKey.WIN;
                    if (modifier && ordinary) throw new GestureRejected("MODIFIER_ORDER");
                    ordinary |= !modifier;
                }
                if ((!ordinary && !(h.Keys.Length == 1 && h.Keys[0] == AgentKey.WIN)) ||
                    (h.Keys.Contains(AgentKey.CTRL) && h.Keys.Contains(AgentKey.ALT) && h.Keys.Any(k => k is AgentKey.DELETE or AgentKey.F8 or AgentKey.F9)))
                    throw new GestureRejected("RESERVED_HOTKEY");
                break;
            default: throw new GestureRejected("INVALID_ACTION");
        }
    }

    public static int VirtualKey(AgentKey key) => key switch
    {
        >= AgentKey.A and <= AgentKey.Z => 0x41 + key - AgentKey.A,
        >= AgentKey.D0 and <= AgentKey.D9 => 0x30 + key - AgentKey.D0,
        >= AgentKey.F1 and <= AgentKey.F12 => 0x70 + key - AgentKey.F1,
        AgentKey.CTRL => 0x11, AgentKey.ALT => 0x12, AgentKey.SHIFT => 0x10, AgentKey.WIN => 0x5B,
        AgentKey.ENTER => 0x0D, AgentKey.ESC => 0x1B, AgentKey.TAB => 9, AgentKey.SPACE => 0x20,
        AgentKey.BACKSPACE => 8, AgentKey.DELETE => 0x2E, AgentKey.HOME => 0x24, AgentKey.END => 0x23,
        AgentKey.PAGEUP => 0x21, AgentKey.PAGEDOWN => 0x22, AgentKey.UP => 0x26, AgentKey.DOWN => 0x28,
        AgentKey.LEFT => 0x25, AgentKey.RIGHT => 0x27, _ => throw new GestureRejected("UNKNOWN_KEY")
    };
    private sealed class GestureRejected(string code) : Exception { public string Code { get; } = code; }
}
