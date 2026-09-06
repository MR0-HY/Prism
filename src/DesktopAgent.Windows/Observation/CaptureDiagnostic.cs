using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Safety;
using DesktopAgent.Windows.Overlay;

namespace DesktopAgent.Windows.Observation;

internal static class CaptureDiagnostic
{
    public static async Task<int> RunAsync(string fixtureExe, string directory, bool useControls = false, bool useOverlay = false)
    {
        fixtureExe = Path.GetFullPath(fixtureExe);
        if (!File.Exists(fixtureExe) || Path.GetFileName(fixtureExe) != "DesktopAgent.NativeFixture.exe") return 2;
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        string truthPath = Path.Combine(directory, "fixture-" + Guid.NewGuid().ToString("N") + ".json");
        var checks = new List<object>();
        var targets = new List<object>();
        bool passed = true;
        string? failure = null;
        DesktopEnvironment? environment = null;
        Process? fixture = null;
        TaskLeaseSession? session = null;
        LeaseWorker? worker = null;
        InputRun? run = null;
        var gate = new InputSafetyGate();
        var hotkeys = new EmergencyHotkeyService(gate);
        using var overlay = useOverlay ? new DesktopOverlayController(System.Windows.Application.Current.Dispatcher) : null;
        await using var observer = new WindowsDesktopObserver(overlay is null ? null : overlay.HideForCaptureAsync);
        using var controls = new WindowsControlObserver(observer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        void Check(string name, bool value) { checks.Add(new { name, passed = value }); passed &= value; }
        JsonElement Read() { using var doc = JsonDocument.Parse(File.ReadAllText(truthPath)); return doc.RootElement.Clone(); }
        try
        {
            Check("hotkeys_registered", (await hotkeys.StartAsync().WaitAsync(deadline.Token)).Registered);
            if (!passed) throw new InvalidOperationException("HOTKEYS_UNAVAILABLE");
            var start = new ProcessStartInfo(fixtureExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(fixtureExe)! };
            start.ArgumentList.Add("--capture-probe"); start.ArgumentList.Add(truthPath);
            fixture = Process.Start(start) ?? throw new InvalidOperationException("FIXTURE_START_FAILED");
            using (var ready = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                ready.CancelAfter(TimeSpan.FromSeconds(8));
                while (!File.Exists(truthPath)) await Task.Delay(40, ready.Token);
                if (Read().GetProperty("processId").GetInt32() != fixture.Id) throw new InvalidOperationException("FIXTURE_ID_MISMATCH");
                nint hwnd = (nint)Read().GetProperty("hwnd").GetInt64();
                while (Win32InputDevice.GetForegroundWindow() != hwnd) await Task.Delay(40, ready.Token);
            }
            environment = await observer.GetEnvironmentAsync(deadline.Token);
            Check("interactive_desktop", environment.SessionState == DesktopSessionState.Available);
            Check("fixture_foreground", environment.Foreground?.ProcessId == fixture.Id);
            var bounds = Read().GetProperty("bounds");
            var fixtureRegion = new PhysicalRect(bounds.GetProperty("left").GetInt32(), bounds.GetProperty("top").GetInt32(), bounds.GetProperty("width").GetInt32(), bounds.GetProperty("height").GetInt32());
            var monitor = environment.Displays.Single(d => d.Bounds.Contains(fixtureRegion));
            if (!passed) throw new InvalidOperationException("DESKTOP_PRECONDITION_FAILED");
            var registry = new TaskLeaseRegistry();
            if (!registry.TryAcquire(Guid.NewGuid(), out session) || !session!.TryStartWorker(session.Current, out worker) ||
                !gate.TryArm(session, session.Current, gate.Status.Revision, TimeSpan.FromSeconds(25), out run))
                throw new InvalidOperationException("LEASE_FAILED");
            var device = new Win32InputDevice();
            var gestures = new InputGestureRunner(gate, device);
            if (overlay is not null)
            {
                overlay.SetDisplay(monitor);
                await overlay.SetStateAsync(new(run!.Lease.Lease, TaskState.Running, "原生夹具诊断 · 无模型", "测试截图隐藏、前台和真实点击", "独立限时 25 秒，只操作本次夹具"), deadline.Token);
                await Task.Delay(100, deadline.Token);
                Check("overlay_does_not_activate", (await observer.GetEnvironmentAsync(deadline.Token)).Foreground?.ProcessId == fixture.Id);
                Check("decorative_border_not_blocking_region", !Win32DesktopEnvironment.OwnWindows().Any(r => r == monitor.Bounds));
            }
            ControlSnapshot? previousControls = null;
            Frame? previousFrame = null;
            for (int i = 0; i < 12; i++)
            {
                var timer = Stopwatch.StartNew();
                var truth = Read().GetProperty("targets")[i];
                if (overlay is not null)
                {
                    var targetRect = new PhysicalRect((int)truth.GetProperty("left").GetDouble(), (int)truth.GetProperty("top").GetDouble(), (int)truth.GetProperty("width").GetDouble(), (int)truth.GetProperty("height").GetDouble());
                    await overlay.RelocateAsync(run!.Lease.Lease, targetRect, deadline.Token);
                }
                var overview = await observer.CaptureAsync(run!.Lease.Lease, monitor.Id, null, deadline.Token);
                if (overlay is not null)
                {
                    Check("overlays_restored_after_capture_" + i, overlay.Hud.IsVisible && overlay.Rainbow.IsVisible);
                    Check("hud_occupancy_preserved_" + i, !overview.OwnWindowRects.IsEmpty);
                }
                var frame = i % 3 == 0 ? overview : await observer.CaptureAsync(run.Lease.Lease, monitor.Id, fixtureRegion, deadline.Token);
                if (i % 3 == 2)
                {
                    var crop = new PhysicalRect((int)truth.GetProperty("left").GetDouble() - 8, (int)truth.GetProperty("top").GetDouble() - 8,
                        (int)truth.GetProperty("width").GetDouble() + 16, (int)truth.GetProperty("height").GetDouble() + 16);
                    frame = await observer.CaptureAsync(run.Lease.Lease, monitor.Id, crop, deadline.Token);
                }
                var point = new NormalizedPoint((truth.GetProperty("x").GetDouble() - frame.PhysicalRegion.Left) * 1000 / (frame.PhysicalRegion.Width - 1),
                    (truth.GetProperty("y").GetDouble() - frame.PhysicalRegion.Top) * 1000 / (frame.PhysicalRegion.Height - 1));
                long? controlReadMs = null, controlResolveMs = null;
                int? candidateCount = null;
                if (useControls)
                {
                    var readTimer = Stopwatch.StartNew();
                    var snapshot = await controls.ObserveAsync(frame, deadline.Token);
                    controlReadMs = readTimer.ElapsedMilliseconds;
                    candidateCount = snapshot.Candidates.Length;
                    Check("uia_available_" + i, snapshot.Status is ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial);
                    Check("password_and_offscreen_filtered_" + i, snapshot.Candidates.All(c => c.Name is not ("PRIVATE_PASSWORD_FIELD" or "OFFSCREEN_FIXTURE")));
                    var candidate = snapshot.Candidates.SingleOrDefault(c => c.Role == "Button" && c.Name == (i + 1).ToString("00"));
                    if (candidate is null) throw new InvalidOperationException("UIA_TARGET_MISSING_" + snapshot.Status);
                    if (i == 0)
                    {
                        Check("toggle_state_readonly", snapshot.Candidates.Any(c => c.Name == "Fixture toggle" && c.ToggleState == "on"));
                        var disabled = snapshot.Candidates.Single(c => c.Name == "Disabled fixture" && c.Role == "Button");
                        Check("disabled_control_rejected", (await controls.ResolveAsync(frame, snapshot, disabled.Id, deadline.Token)).ErrorCode == "CONTROL_DISABLED");
                    }
                    if (previousControls is not null && previousFrame is not null)
                        Check("old_control_reference_rejected_" + i, (await controls.ResolveAsync(previousFrame, previousControls, previousControls.Candidates[0].Id, deadline.Token)).ErrorCode == "STALE_CONTROL_REFERENCE");
                    readTimer.Restart();
                    var resolution = await controls.ResolveAsync(frame, snapshot, candidate.Id, deadline.Token);
                    controlResolveMs = readTimer.ElapsedMilliseconds;
                    Check("uia_resolved_" + i, resolution.Point is not null && resolution.ErrorCode is null);
                    if (resolution.Point is not { } resolved) throw new InvalidOperationException(resolution.ErrorCode ?? "UIA_RESOLVE_FAILED");
                    point = new((resolved.X - frame.PhysicalRegion.Left) * 1000d / (frame.PhysicalRegion.Width - 1),
                        (resolved.Y - frame.PhysicalRegion.Top) * 1000d / (frame.PhysicalRegion.Height - 1));
                    previousControls = snapshot; previousFrame = frame;
                }
                bool colorCorrect = PixelMatches(frame, truth);
                Check("screen_color_" + i, colorCorrect);
                string? fresh = await observer.CheckPointAsync(frame, run.Lease.Lease, point, TimeSpan.FromSeconds(90), deadline.Token);
                Check("fresh_target_" + i, fresh is null);
                if (!passed) throw new InvalidOperationException(fresh ?? "SCREEN_COLOR_MISMATCH");
                if (i == 1) await File.WriteAllBytesAsync(Path.Combine(directory, "owned-fixture-crop.png"), frame.Image.Bytes.ToArray(), deadline.Token);
                var target = new InputTarget(frame.Foreground, frame.PhysicalRegion, Win32InputDevice.VirtualDesktop());
                var result = await gestures.ExecuteAsync(run, target, new ClickAction(point, Core.Protocol.MouseButton.Left, 1), deadline.Token);
                Check("injected_" + i, result.Status == "applied");
                if (result.Status != "applied") throw new InvalidOperationException(result.Code ?? "INPUT_FAILED");
                await Task.Delay(160, deadline.Token);
                Check("independent_click_" + i, Read().GetProperty("targets")[i].GetProperty("clicks").GetInt32() == 1);
                string? stale = await observer.CheckPointAsync(frame, run.Lease.Lease, point, TimeSpan.FromSeconds(90), deadline.Token);
                Check("changed_pixels_rejected_" + i, stale == "TARGET_PIXELS_CHANGED");
                targets.Add(new { index = i, frame.ViewKind, frame.PhysicalRegion, frame.Image.Width, frame.Image.Height,
                    frame.SourceFrameId, elapsedMs = timer.ElapsedMilliseconds, controlReadMs, controlResolveMs, candidateCount, fresh, afterClick = stale, result });
                if (!passed) throw new InvalidOperationException("TARGET_CHECK_FAILED");
            }
            Check("twelve_distinct_targets", Read().GetProperty("targets").EnumerateArray().All(t => t.GetProperty("clicks").GetInt32() == 1));
            if (useControls)
            {
                var frame = await observer.CaptureAsync(run!.Lease.Lease, monitor.Id, null, deadline.Token);
                controls.Enabled = false;
                var fallback = await controls.ObserveAsync(frame, deadline.Token);
                Check("explicit_visual_fallback_keeps_image", fallback.Status == ControlSnapshotStatus.Disabled && fallback.Candidates.IsEmpty && !frame.Image.Bytes.IsEmpty);
            }
        }
        catch (OperationCanceledException) { failure = "CANCELLED_OR_DEADLINE"; Check("completed", false); }
        catch (Exception error) { failure = error is InvalidOperationException ? error.Message : error.GetType().Name; Check("completed", false); }
        finally
        {
            gate.Trip(InputStopReason.Stopped);
            overlay?.HideAll();
            Check("input_closed", !gate.Status.IsOpen);
            if (run is not null)
            {
                var until = Stopwatch.StartNew();
                while (!gate.TryRelease(run) && until.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
                Check("permit_released", gate.Status.Lease is null);
            }
            worker?.Dispose();
            if (session is not null) { await session.RequestCompletionAsync(); Check("lease_released", session.TryCompleteCleanup()); }
            try { await hotkeys.DisposeAsync(); Check("hotkeys_released", hotkeys.ThreadExited); } catch { Check("hotkeys_released", false); }
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
        await File.WriteAllTextAsync(Path.Combine(directory, "capture-input.json"), JsonSerializer.Serialize(new
        {
            atUtc = DateTimeOffset.UtcNow, passed, failure, checks, targets, environment, truthPath,
            evidenceMode = "REAL_SCREEN_BITBLT_AND_SENDINPUT_TO_INDEPENDENT_FIXTURE", groundingSource = useControls ? "READ_ONLY_UIA_WITH_INDEPENDENT_TARGET_LABEL_NOT_MODEL" : "INDEPENDENT_FIXTURE_TRUTH_NOT_MODEL",
            modelCalls = 0, useOverlay, glassAttributeAccepted = overlay?.GlassRequested, userSettingsChanged = false, physicalHotkeyPressed = false, screenshotsSaved = "OWNED_FIXTURE_CROP_ONLY"
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    private static bool PixelMatches(Frame frame, JsonElement truth)
    {
        using var stream = new MemoryStream(frame.Image.Bytes.ToArray());
        var image = new FormatConvertedBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad), PixelFormats.Bgra32, null, 0);
        int x = (int)((truth.GetProperty("left").GetDouble() + 10 - frame.PhysicalRegion.Left) * image.PixelWidth / frame.PhysicalRegion.Width);
        int y = (int)((truth.GetProperty("top").GetDouble() + 10 - frame.PhysicalRegion.Top) * image.PixelHeight / frame.PhysicalRegion.Height);
        if (x < 0 || y < 0 || x >= image.PixelWidth || y >= image.PixelHeight) return false;
        byte[] pixel = new byte[4];
        image.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Math.Abs(pixel[0] - truth.GetProperty("blue").GetInt32()) <= 16 &&
            Math.Abs(pixel[1] - truth.GetProperty("green").GetInt32()) <= 16 && Math.Abs(pixel[2] - truth.GetProperty("red").GetInt32()) <= 16;
    }
}
