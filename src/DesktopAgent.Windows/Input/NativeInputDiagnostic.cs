using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Safety;

namespace DesktopAgent.Windows.Input;

/// <summary>Explicit finite test entry, restricted to a newly launched independent fixture.</summary>
internal static class NativeInputDiagnostic
{
    public static async Task<int> RunAsync(string fixtureExe, string outputDirectory)
    {
        fixtureExe = Path.GetFullPath(fixtureExe);
        if (!File.Exists(fixtureExe) || Path.GetFileName(fixtureExe) != "DesktopAgent.NativeFixture.exe") return 2;
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string probePath = Path.Combine(outputDirectory, "fixture-" + Guid.NewGuid().ToString("N") + ".json");
        var checks = new List<object>();
        var gestures = new List<object>();
        bool passed = true;
        void Check(string name, bool value) { checks.Add(new { name, passed = value }); passed &= value; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var gate = new InputSafetyGate();
        var hotkeys = new EmergencyHotkeyService(gate);
        var device = new Win32InputDevice();
        var runner = new InputGestureRunner(gate, device);
        Process? fixture = null;
        InputRun? run = null;
        TaskLeaseSession? session = null;
        LeaseWorker? worker = null;
        JsonElement? finalSnapshot = null;
        double? cancelledAfterSignalMs = null;
        try
        {
            Check("x64_input_abi_40", Win32InputDevice.NativeInputSize == 40);
            var ready = await hotkeys.StartAsync().WaitAsync(deadline.Token);
            Check("hotkeys_registered", ready.Registered);
            if (!passed) throw new InvalidOperationException("PRECONDITION_FAILED");
            var start = new ProcessStartInfo(fixtureExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(fixtureExe)! };
            start.ArgumentList.Add("--input-probe"); start.ArgumentList.Add(probePath);
            fixture = Process.Start(start) ?? throw new InvalidOperationException("FIXTURE_START_FAILED");
            await UntilAsync(() => File.Exists(probePath), deadline.Token);
            var initial = ReadProbe(probePath);
            if (initial.GetProperty("processId").GetInt32() != fixture.Id) throw new InvalidOperationException("FIXTURE_ID_MISMATCH");
            nint hwnd = (nint)initial.GetProperty("hwnd").GetInt64();
            // The test never forces focus or sends keys to obtain it. Only its own foreground window is eligible.
            using (var focusDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                focusDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                await UntilAsync(() => Win32InputDevice.GetForegroundWindow() == hwnd, focusDeadline.Token);
            }
            var target = Win32InputDevice.ReadTarget(hwnd, Win32InputDevice.VirtualDesktop());
            target = target with { Viewport = target.Foreground.WindowRect }; // Confine every pointer target to fixture.
            Check("fixture_foreground", device.TargetIsCurrent(target));
            var registry = new TaskLeaseRegistry();
            if (!registry.TryAcquire(Guid.NewGuid(), out session) || !session!.TryStartWorker(session.Current, out worker) ||
                !gate.TryArm(session, session.Current, gate.Status.Revision, TimeSpan.FromSeconds(25), out run))
                throw new InvalidOperationException("LEASE_FAILED");

            NormalizedPoint Point(string name)
            {
                var item = ReadProbe(probePath).GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);
                return new((item.GetProperty("x").GetDouble() - target.Viewport.Left) * 1000 / (target.Viewport.Width - 1),
                    (item.GetProperty("y").GetDouble() - target.Viewport.Top) * 1000 / (target.Viewport.Height - 1));
            }
            async Task Act(string name, AgentAction action)
            {
                var result = await runner.ExecuteAsync(run!, target, action, deadline.Token);
                gestures.Add(new { name, result });
                if (result.Status != "applied") throw new InvalidOperationException(name + "_" + result.Code);
                await Task.Delay(120, deadline.Token);
            }
            int Count(string name)
            {
                var counts = ReadProbe(probePath).GetProperty("snapshot").GetProperty("counters");
                return counts.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
            }
            string Text() => ReadProbe(probePath).GetProperty("snapshot").GetProperty("plainText").GetString()!;

            await Act("move", new MoveAction(Point("TargetButton")));
            Check("received_mouse_move", Count("mouse_move") > 0);
            await Act("click", new ClickAction(Point("TargetButton"), MouseButton.Left, 1));
            Check("received_click", Count("button_click") == 1);
            await Act("double_click", new ClickAction(Point("TargetButton"), MouseButton.Left, 2));
            Check("received_double_click", Count("double_click") >= 1 && Count("button_click") == 3);
            await Act("right_click", new ClickAction(Point("TargetButton"), MouseButton.Right, 1));
            Check("received_right_click", Count("right_down") >= 1);
            await Act("focus_text", new ClickAction(Point("PlainTextBox"), MouseButton.Left, 1));
            const string sample = "桌面 Agent 😀";
            await Act("unicode_text", new TextAction(sample));
            Check("exact_chinese_ascii_emoji", Text() == sample);
            await Act("select_all", new HotkeyAction([AgentKey.CTRL, AgentKey.A]));
            await Act("replace_text", new TextAction("核对通过😀"));
            Check("shortcut_and_replace", Text() == "核对通过😀");
            await Act("scroll", new ScrollAction(Point("TargetButton"), -2));
            Check("received_scroll", Count("wheel") > 0 && Count("wheel_delta") == -240);
            await Act("drag", new DragAction(Point("DragTarget"), Point("DropZone"), 500));
            Check("received_drag_hit", Count("drag_completed") == 1 && Count("drop_hit") == 1);
            await Act("refocus_text", new ClickAction(Point("PlainTextBox"), MouseButton.Left, 1));
            await Act("select_before_stop", new HotkeyAction([AgentKey.CTRL, AgentKey.A]));

            long signalAt = 0;
            var signalTask = Task.Run(async () =>
            {
                await Task.Delay(90, deadline.Token);
                Interlocked.Exchange(ref signalAt, Stopwatch.GetTimestamp());
                if (!hotkeys.PostDiagnosticSignal(stop: true)) gate.Trip(InputStopReason.Stopped);
            });
            var interrupted = await runner.ExecuteAsync(run!, target, new TextAction(new string('测', 1000)), deadline.Token);
            long returnedAt = Stopwatch.GetTimestamp();
            await signalTask;
            cancelledAfterSignalMs = (returnedAt - signalAt) * 1000d / Stopwatch.Frequency;
            gestures.Add(new { name = "stop_during_long_text", result = interrupted });
            Check("long_text_stopped", interrupted.Status == "cancelled" && interrupted.CleanupComplete && cancelledAfterSignalMs <= 150);
            await Task.Delay(180, deadline.Token);
            string stoppedText = Text();
            await Task.Delay(200, deadline.Token);
            Check("no_text_after_stop", Text() == stoppedText && stoppedText.Length < 1000);
            Check("no_stuck_modifiers_or_buttons", !device.IsKeyDown(0x11) && !device.IsKeyDown(0x12) && !device.IsKeyDown(1) && !device.IsKeyDown(2));
            finalSnapshot = ReadProbe(probePath);
        }
        catch (Exception error) { Check("run_" + (error is OperationCanceledException ? "DEADLINE_OR_FOREGROUND_NOT_READY" : error.Message), false); }
        finally
        {
            gate.Trip(InputStopReason.Shutdown);
            if (run is not null) Check("input_permit_cleanup", gate.TryRelease(run));
            worker?.Dispose();
            if (session is not null) { await session.RequestCompletionAsync(); Check("task_cleanup", session.TryCompleteCleanup()); }
            try { await hotkeys.DisposeAsync(); Check("hotkey_cleanup", hotkeys.ThreadExited); } catch { Check("hotkey_cleanup", false); }
            if (fixture is not null)
            {
                try
                {
                    if (!fixture.HasExited)
                    {
                        fixture.CloseMainWindow();
                        try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch (TimeoutException) { fixture.Kill(); await fixture.WaitForExitAsync(); }
                    }
                    Check("owned_fixture_closed", fixture.HasExited);
                }
                catch { Check("owned_fixture_closed", false); }
                fixture.Dispose();
            }
        }
        var report = new
        {
            atUtc = DateTimeOffset.UtcNow, passed, evidenceMode = "REAL_SENDINPUT_TO_INDEPENDENT_NATIVE_FIXTURE",
            inputSource = "FIXTURE_TRUTH_TEST_ONLY_NOT_MODEL", stopSource = "POSTED_OWN_HOTKEY_MESSAGE_NOT_PHYSICAL_PRESS",
            modelCalls = 0, apiCalls = 0, userApplicationsOperated = false, cancelledAfterSignalMs,
            probePath, checks, gestures, finalSnapshot
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "native-input.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
    private static JsonElement ReadProbe(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    { while (!condition()) await Task.Delay(40, ct); }
}
