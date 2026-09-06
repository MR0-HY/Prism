using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Observation;
using DesktopAgent.Windows.Safety;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit, finite native regression: minimize, resolve empty desktop, right-click, dismiss,
/// then request Windows' minimize restoration. No model, left-click, text, desktop file or setting changes.</summary>
internal static class DesktopBackgroundDiagnostic
{
    internal static async Task<int> RunAsync(string directory)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            directory = Path.GetFullPath(directory);
            CheckPath(directory);
            if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
            Directory.CreateDirectory(directory);
            CheckPath(directory);
        }
        catch { return 2; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var gate = new InputSafetyGate();
        using var deadlineRegistration = deadline.Token.Register(() => gate.Trip(InputStopReason.Deadline));
        var hotkeys = new EmergencyHotkeyService(gate);
        var desktop = new WindowsDesktopObserver(preferForegroundWindow: true);
        using var controls = new WindowsControlObserver(desktop);
        var registry = new TaskLeaseRegistry();
        TaskLeaseSession? session = null;
        LeaseWorker? worker = null;
        InputRun? run = null;
        CancellationTokenSource? active = null;
        var gestures = new List<object>();
        var checks = new List<object>();
        string stage = "REGISTERING_HOTKEYS";
        string? failure = null;
        ForegroundIdentity? initialForeground = null, shell = null, finalForeground = null;
        bool candidateResolved = false, menuItemResolved = false, contextMenuObserved = false, menuDismissed = false, restoreRequested = false;
        bool minimizeApplied = false, rightClickAttempted = false, rightClickInjected = false, nativeMenuEverVisible = false;
        bool restoreAttempted = false, failureRestoreAttempted = false;
        string? failureRestoreError = null;
        bool inputCleanup = true, leaseCleanup = true, hotkeyCleanup = false, pixelsCleared = false, cancelled = false;
        int actionCount = 0;
        CancellationToken Token() => active?.Token ?? deadline.Token;
        async Task Write(string name, object value, CancellationToken token)
        {
            CheckPath(directory);
            await using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions { WriteIndented = true }, token);
            await stream.FlushAsync(token);
        }
        async Task<DesktopEnvironment> EnvironmentAsync(bool expectedShell = false, bool allowMenu = false)
        {
            Token().ThrowIfCancellationRequested();
            var environment = await desktop.GetEnvironmentAsync(Token()).WaitAsync(Token());
            finalForeground = environment.Foreground;
            if (environment.SessionState != DesktopSessionState.Available || environment.Foreground is null || environment.Foreground.ProcessId == Environment.ProcessId)
                throw new InvalidOperationException("DESKTOP_UNAVAILABLE");
            if (expectedShell && (shell is null || !MatchesShell(environment.Foreground, shell, allowMenu)))
                throw new InvalidOperationException("EXPECTED_DESKTOP_NOT_FOREGROUND");
            return environment;
        }
        async Task<Frame> Capture(string name, bool expectedShell = false, bool allowMenu = false)
        {
            var environment = await EnvironmentAsync(expectedShell, allowMenu);
            var display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, environment.Foreground!.WindowRect)).ThenByDescending(d => d.IsPrimary).First();
            var frame = await desktop.CaptureAsync(run!.Lease.Lease, display.Id, null, Token()).WaitAsync(Token());
            _ = await EnvironmentAsync(expectedShell, allowMenu);
            await Write(name + "-frame.json", new { atUtc = DateTimeOffset.UtcNow, frame }, Token());
            CheckPath(directory);
            await using var image = new FileStream(Path.Combine(directory, name + ".png"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await image.WriteAsync(frame.Image.Bytes, Token());
            await image.FlushAsync(Token());
            return frame;
        }
        async Task Act(string name, Frame frame, AgentAction action, bool expectedShell = false, bool allowMenu = false,
            ControlSnapshot? snapshot = null, ControlCandidate? candidate = null)
        {
            if (++actionCount > 4) throw new InvalidOperationException("ACTION_LIMIT");
            stage = name;
            var context = new TaskContext(run!.Lease.Lease.TaskId, run.Lease.Lease.Epoch,
                "最小化所有窗口，在桌面空白处右键查看菜单，然后收起菜单。", DateTimeOffset.UtcNow,
                TaskState.Running, false, "NATIVE_DIAGNOSTIC_NO_MODEL", frame.MonitorId, new(4, 0, 25000), new(actionCount - 1, 0, clock.ElapsedMilliseconds, 0, 0), [], null, null);
            var proposal = new Proposal(2, Guid.NewGuid().ToString("N"), context.Id, context.Epoch, frame.Id,
                "明确的桌面原生回归步骤", "重新观察结果", new ActDecision(action, name, "核对桌面或菜单"));
            var environment = await EnvironmentAsync(expectedShell, allowMenu);
            var policy = new DesktopPolicyValidator(TimeSpan.FromSeconds(10), Environment.ProcessId).Validate(context, frame, proposal, environment);
            var cursorBefore = action is ClickAction ? ReadCursor() : (PhysicalPoint?)null;
            var expectedPhysicalPoint = action is ClickAction requestedClick
                ? InputCoordinates.ToPhysical(requestedClick.Point, frame.PhysicalRegion) : (PhysicalPoint?)null;
            await Write(name + "-before.json", new { atUtc = DateTimeOffset.UtcNow, frame.Id, frame.Foreground,
                policy.Disposition, policy.Code, action = JsonSerializer.SerializeToElement(action, action.GetType()), snapshot, candidate,
                cursorBefore, expectedPhysicalPoint, cursorCoordinateSpace = "PHYSICAL_PIXELS" }, Token());
            if (policy.Disposition != PolicyDisposition.Allow || policy.Action is null) throw new InvalidOperationException("POLICY_" + policy.Code);
            _ = await EnvironmentAsync(expectedShell, allowMenu);
            if (snapshot is not null && candidate is not null)
            {
                var resolved = await controls.ResolveAsync(frame, snapshot, candidate.Id, Token()).WaitAsync(Token());
                if (resolved.Point is not { } point || resolved.ErrorCode is not null || action is not ClickAction click ||
                    InputCoordinates.ToPhysical(click.Point, frame.PhysicalRegion) != point)
                    throw new InvalidOperationException("DESKTOP_TARGET_CHANGED_BEFORE_INPUT");
            }
            var result = await new WindowsInputExecutor(desktop, gate, run, TimeSpan.FromSeconds(10)).ExecuteAsync(context.Lease, policy.Action, Token());
            var cursorAfter = action is ClickAction ? ReadCursor() : (PhysicalPoint?)null;
            gestures.Add(new { name, result });
            await Write(name + "-completed.json", new { atUtc = DateTimeOffset.UtcNow, result, cursorBefore, cursorAfter,
                expectedPhysicalPoint, cursorCoordinateSpace = "PHYSICAL_PIXELS" }, CancellationToken.None);
            Token().ThrowIfCancellationRequested();
            if (result.Status != ActionStatus.Injected || result.AppliedEventCount <= 0) throw new InvalidOperationException("INPUT_NOT_APPLIED");
        }
        async Task Restore(string name, Frame frame)
        {
            // This explicit diagnostic-only restore chord uses the existing permit and native segment gate;
            // it does not grant the production model a new shortcut or mint a Core ValidatedAction.
            if (restoreAttempted || !minimizeApplied || run is null || shell is null || actionCount >= 4 ||
                !gate.Status.IsOpen || Token().IsCancellationRequested || clock.ElapsedMilliseconds >= 23000)
                throw new InvalidOperationException("RESTORE_NOT_ADMITTED");
            restoreAttempted = true;
            stage = "RESTORING_MINIMIZED_WINDOWS";
            await Write(name + "-before.json", new { atUtc = DateTimeOffset.UtcNow, action = "WIN+SHIFT+M", frame.Id, frame.Foreground,
                explicitDiagnosticOnly = true, sameInputGate = true, noForcedRestoreAfterCancellation = true }, Token());
            var current = await EnvironmentAsync(true);
            var stale = FrameChecks.Validate(frame, run.Lease.Lease, current, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
            if (stale is not null || ReadMenuState(shell.ProcessId).Visible) throw new InvalidOperationException("RESTORE_PRECONDITION_CHANGED");
            Token().ThrowIfCancellationRequested();
            ++actionCount;
            var restore = await new InputGestureRunner(gate, new Win32InputDevice()).ExecuteAsync(run,
                new(frame.Foreground, frame.PhysicalRegion, Win32InputDevice.VirtualDesktop()),
                new HotkeyAction([AgentKey.WIN, AgentKey.SHIFT, AgentKey.M]), Token());
            restoreRequested = restore.Status == "applied" && restore.CleanupComplete;
            gestures.Add(new { name, result = restore });
            await Write(name + "-completed.json", new { atUtc = DateTimeOffset.UtcNow, restore, restoreRequested,
                exactPriorWindowStateVerified = false }, CancellationToken.None);
            Token().ThrowIfCancellationRequested();
            if (!restoreRequested) throw new InvalidOperationException("RESTORE_NOT_APPLIED");
            finalForeground = (await EnvironmentAsync()).Foreground;
        }
        try
        {
            await Write("run.json", new { atUtc = DateTimeOffset.UtcNow, maximumMs = 25000, maximumGestures = 4,
                modelCalls = 0, apiCalls = 0, createsDesktopFiles = false, changesSettings = false,
                operations = new[] { "WIN+M", "RIGHT_CLICK_RESOLVED_DESKTOP_BACKGROUND", "ESC_IF_EXPECTED_MENU", "WIN+SHIFT+M_IF_EXPECTED_DESKTOP" },
                pause = "Ctrl+Alt+F8", stop = "Ctrl+Alt+F9", restorationAfterCancellation = false }, Token());
            var registration = await hotkeys.StartAsync().WaitAsync(Token());
            await Write("hotkeys.json", registration, Token());
            if (!registration.Registered) throw new InvalidOperationException("HOTKEYS_UNAVAILABLE");
            if (!registry.TryAcquire(Guid.NewGuid(), out session) || !session!.TryStartWorker(session.Current, out worker) ||
                !gate.TryArm(session, session.Current, gate.Status.Revision, TimeSpan.FromSeconds(25), out run))
                throw new InvalidOperationException("LEASE_UNAVAILABLE");
            active = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, run!.Token);
            initialForeground = (await EnvironmentAsync()).Foreground;
            var before = await Capture("01-initial");
            await Act("01-minimize", before, new HotkeyAction([AgentKey.WIN, AgentKey.M]));
            minimizeApplied = true;
            stage = "WAITING_FOR_DESKTOP";
            var settling = Stopwatch.StartNew();
            while (true)
            {
                var environment = await EnvironmentAsync();
                if (IsShell(environment.Foreground!)) { shell = environment.Foreground; break; }
                if (settling.ElapsedMilliseconds > 4000) throw new InvalidOperationException("DESKTOP_NOT_REACHED");
                await Task.Delay(80, Token());
            }
            var desktopFrame = await Capture("02-desktop", expectedShell: true);
            var snapshot = await controls.ObserveAsync(desktopFrame, Token()).WaitAsync(Token());
            await Write("02-desktop-controls.json", snapshot, Token());
            var candidates = snapshot.Candidates.Where(c => c.Role == "DesktopBackground" && c.Enabled).ToArray();
            if (snapshot.Status is not (ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial) || candidates.Length != 1)
                throw new InvalidOperationException("DESKTOP_BACKGROUND_NOT_UNIQUE");
            var candidate = candidates[0];
            var resolution = await controls.ResolveAsync(desktopFrame, snapshot, candidate.Id, Token()).WaitAsync(Token());
            candidateResolved = resolution.Point is not null && resolution.ErrorCode is null;
            await Write("02-desktop-resolution.json", new { atUtc = DateTimeOffset.UtcNow, candidate, resolution, candidateResolved }, Token());
            if (!candidateResolved) throw new InvalidOperationException("DESKTOP_BACKGROUND_UNRESOLVED");
            var resolvedPoint = resolution.Point!.Value;
            var point = new NormalizedPoint((resolvedPoint.X - desktopFrame.PhysicalRegion.Left) * 1000d / (desktopFrame.PhysicalRegion.Width - 1),
                (resolvedPoint.Y - desktopFrame.PhysicalRegion.Top) * 1000d / (desktopFrame.PhysicalRegion.Height - 1));
            if (ReadMenuState(shell!.ProcessId).Visible) throw new InvalidOperationException("DESKTOP_MENU_ALREADY_VISIBLE");
            rightClickAttempted = true;
            await Act("02-rightclick", desktopFrame, new ClickAction(point, MouseButton.Right, 1), true, snapshot: snapshot, candidate: candidate);
            rightClickInjected = true;
            stage = "OBSERVING_CONTEXT_MENU";
            var menuWait = Stopwatch.StartNew();
            await Write("03-menu-wait-before.json", new { atUtc = DateTimeOffset.UtcNow, maximumWaitMs = 4000,
                additionalInputs = 0, rightClickReplayed = false }, Token());
            MenuState waitedMenu;
            do
            {
                _ = await EnvironmentAsync(true, true);
                waitedMenu = ReadMenuState(shell.ProcessId);
                nativeMenuEverVisible |= waitedMenu.Visible;
                if (waitedMenu.Visible || menuWait.ElapsedMilliseconds >= 4000) break;
                await Task.Delay(100, Token());
            } while (true);
            await Write("03-menu-wait-completed.json", new { atUtc = DateTimeOffset.UtcNow, elapsedMs = menuWait.ElapsedMilliseconds,
                menuState = waitedMenu, additionalInputs = 0, rightClickReplayed = false }, Token());
            var menuFrame = await Capture("03-menu", true, true);
            var menuControls = await controls.ObserveAsync(menuFrame, Token()).WaitAsync(Token());
            var menuState = ReadMenuState(shell!.ProcessId);
            nativeMenuEverVisible |= menuState.Visible;
            var menuItems = menuControls.Candidates.Where(c => c.Enabled && c.Role == "MenuItem").ToArray();
            await Write("03-menu-controls.json", new { atUtc = DateTimeOffset.UtcNow, menuControls, menuState, menuItems }, Token());
            // A listed menu name is not enough: verify that an actual visible item resolves now.
            // This remains read-only and bounds cold or unavailable providers to at most three resolutions.
            if (menuState.Visible && menuControls.Status is ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial)
            {
                int index = 0;
                foreach (var menuItem in menuItems.OrderByDescending(c => c.Name.Contains("新建", StringComparison.Ordinal) ||
                    c.Name.Equals("New", StringComparison.OrdinalIgnoreCase)).Take(3))
                {
                    _ = await EnvironmentAsync(true, true);
                    if (!ReadMenuState(shell.ProcessId).Visible) throw new InvalidOperationException("DESKTOP_MENU_CHANGED");
                    string prefix = $"03-menu-resolve-{++index:D2}";
                    await Write(prefix + "-before.json", new { atUtc = DateTimeOffset.UtcNow, menuFrame.Id, candidate = menuItem,
                        readOnly = true, clicks = 0 }, Token());
                    var itemResolution = await controls.ResolveAsync(menuFrame, menuControls, menuItem.Id, Token()).WaitAsync(Token());
                    _ = await EnvironmentAsync(true, true);
                    menuItemResolved = itemResolution.Point is { } menuPoint && itemResolution.ErrorCode is null &&
                        FrameChecks.Contains(menuItem.Bounds, menuPoint) && ReadMenuState(shell.ProcessId).Visible;
                    await Write(prefix + "-completed.json", new { atUtc = DateTimeOffset.UtcNow, candidate = menuItem,
                        resolution = itemResolution, passed = menuItemResolved, readOnly = true, clicks = 0 }, Token());
                    if (menuItemResolved) break;
                }
            }
            contextMenuObserved = menuItems.Length > 0 && menuState.Visible && menuItemResolved;
            await Write("03-menu-verification.json", new { atUtc = DateTimeOffset.UtcNow, menuItemResolved, contextMenuObserved,
                readOnly = true, menuItemsClicked = 0 }, Token());
            checks.Add(new { name = "native_context_menu_and_resolved_menu_item", passed = contextMenuObserved });
            // Dismiss only an observed menu belonging to the same shell. No blind Escape in a catch/finally.
            if (!menuState.Visible) throw new InvalidOperationException("DESKTOP_MENU_NOT_OBSERVED");
            _ = await EnvironmentAsync(true, true);
            if (!ReadMenuState(shell.ProcessId).Visible) throw new InvalidOperationException("DESKTOP_MENU_CHANGED");
            await Act("03-dismiss", menuFrame, new HotkeyAction([AgentKey.ESC]), true, true);
            var dismissedFrame = await Capture("04-dismissed", true);
            menuDismissed = !ReadMenuState(shell.ProcessId).Visible;
            await Write("04-dismissed-state.json", new { atUtc = DateTimeOffset.UtcNow, menuDismissed, dismissedFrame.Foreground }, Token());
            if (!menuDismissed) throw new InvalidOperationException("DESKTOP_MENU_STILL_VISIBLE");
            await Restore("04-restore", dismissedFrame);
            if (!contextMenuObserved) failure = "DESKTOP_MENU_CONTROLS_NOT_OBSERVED";
            stage = "COMPLETED";
        }
        catch (Exception error)
        {
            cancelled = Token().IsCancellationRequested;
            failure = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : SafeError(error);
            // A failed observation, or one confirmed right-click which never exposed a menu, may leave the
            // just-minimized windows hidden. Reserve five seconds and obtain a fresh frame of the exact shell
            // without menus. Never replay a right-click or restore after cancelled/uncertain input.
            bool failureRestoreEligible = !rightClickAttempted || rightClickInjected && !nativeMenuEverVisible;
            if (!cancelled && minimizeApplied && failureRestoreEligible && !restoreAttempted && run is not null && shell is not null &&
                actionCount < 4 && gate.Status.IsOpen && clock.ElapsedMilliseconds < 20000)
            {
                string failedStage = stage;
                try
                {
                    _ = await EnvironmentAsync(true);
                    if (ReadMenuState(shell.ProcessId).Visible) throw new InvalidOperationException("FAILURE_RESTORE_MENU_VISIBLE");
                    var restoreFrame = await Capture("failure-restore", true);
                    failureRestoreAttempted = true;
                    await Restore("failure-restore", restoreFrame);
                }
                catch (Exception restoreError)
                {
                    failureRestoreError = restoreError is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : SafeError(restoreError);
                    cancelled |= Token().IsCancellationRequested;
                }
                stage = failedStage;
            }
        }
        finally
        {
            gate.Trip(InputStopReason.Shutdown);
            if (session is not null)
            {
                try { await session.RequestCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { leaseCleanup = false; }
            }
            if (run is not null)
            {
                var draining = Stopwatch.StartNew();
                while (!run.CancellationDrained && draining.ElapsedMilliseconds < 1000) await Task.Delay(10);
                inputCleanup = gate.TryRelease(run);
            }
            worker?.Dispose();
            if (session is not null) leaseCleanup &= session.TryCompleteCleanup();
            active?.Dispose();
            try { await hotkeys.DisposeAsync(); hotkeyCleanup = hotkeys.ThreadExited; } catch { hotkeyCleanup = false; }
            try { await desktop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); pixelsCleared = true; } catch { pixelsCleared = false; }
        }
        bool passed = failure is null && candidateResolved && contextMenuObserved && menuDismissed && restoreRequested &&
            inputCleanup && leaseCleanup && hotkeyCleanup && pixelsCleared;
        await Write("report.json", new { atUtc = DateTimeOffset.UtcNow, passed, stage, failure, elapsedMs = clock.ElapsedMilliseconds,
            candidateResolved, menuItemResolved, contextMenuObserved, menuDismissed, minimizeApplied, rightClickAttempted,
            rightClickInjected, nativeMenuEverVisible,
            restoreAttempted, restoreRequested, failureRestoreAttempted, failureRestoreError, exactPriorWindowStateVerified = false,
            inputCleanup, leaseCleanup, hotkeyCleanup, pixelsCleared, cancelled, actionCount, initialForeground, shell, finalForeground, checks, gestures,
            modelCalls = 0, apiCalls = 0, evidenceMode = "REAL_NATIVE_DESKTOP_RIGHTCLICK_DIAGNOSTIC_NO_MODEL",
            createsDesktopFiles = false, textEntered = false, changesSettings = false, fullUserFileTaskPassed = false }, CancellationToken.None);
        return passed ? 0 : 1;
    }

    private static bool IsShell(ForegroundIdentity foreground) => foreground.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
        foreground.WindowClass is "Progman" or "WorkerW";
    private static PhysicalPoint? ReadCursor()
    {
        try { using var dpi = new PhysicalDpiScope(); return new Win32InputDevice().CursorPosition(); }
        catch { return null; }
    }
    private static bool MatchesShell(ForegroundIdentity foreground, ForegroundIdentity shell, bool allowMenu) =>
        foreground.ProcessId == shell.ProcessId && foreground.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
        (foreground.HwndHex.Equals(shell.HwndHex, StringComparison.OrdinalIgnoreCase) && foreground.WindowClass == shell.WindowClass ||
         allowMenu && foreground.WindowClass is "#32768" or "Microsoft.UI.Content.PopupWindowSiteBridge");
    private sealed record NativeWindowEvidence(string HwndHex, string Class, uint ProcessId, string OwnerHwndHex, string ParentHwndHex);
    private sealed record NativePopupEvidence(NativeWindowEvidence Window, NativeWindowEvidence[] OwnerParentChain);
    private sealed record MenuState(bool Visible, bool ThreadInMenuMode, string[] PopupHandles,
        NativePopupEvidence[] Popups, uint ForegroundThreadId, uint ForegroundProcessId, bool GuiThreadInfoRead,
        uint GuiThreadFlags, string GuiMenuOwnerHwndHex, NativeWindowEvidence[] GuiMenuOwnerParentChain);
    private static MenuState ReadMenuState(int expectedPid)
    {
        var windows = new List<string>();
        var popups = new List<NativePopupEvidence>();
        int count = 0;
        _ = EnumWindows((hwnd, parameter) =>
        {
            if (++count > 1024) return false;
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != expectedPid || !IsWindowVisible(hwnd)) return true;
            var name = new StringBuilder(256);
            _ = GetClassNameW(hwnd, name, name.Capacity);
            if (name.ToString() is "#32768" or "Microsoft.UI.Content.PopupWindowSiteBridge")
            {
                windows.Add(hwnd.ToString("X"));
                popups.Add(new(ReadNativeWindow(hwnd), ReadParentChain(GetWindow(hwnd, 4))));
            }
            return true;
        }, 0);
        nint foreground = Win32InputDevice.GetForegroundWindow();
        uint thread = GetWindowThreadProcessId(foreground, out uint foregroundPid);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        bool guiRead = thread != 0 && GetGUIThreadInfo(thread, ref info);
        bool inMenu = foregroundPid == expectedPid && guiRead && (info.Flags & 4) != 0;
        return new(inMenu || windows.Count > 0, inMenu, windows.ToArray(), popups.ToArray(), thread, foregroundPid,
            guiRead, guiRead ? info.Flags : 0, (guiRead ? info.MenuOwner : 0).ToString("X"),
            guiRead ? ReadParentChain(info.MenuOwner) : []);
    }
    private static NativeWindowEvidence ReadNativeWindow(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        var name = new StringBuilder(256);
        _ = GetClassNameW(hwnd, name, name.Capacity);
        return new(hwnd.ToString("X"), name.ToString(), pid, GetWindow(hwnd, 4).ToString("X"), GetParent(hwnd).ToString("X"));
    }
    private static NativeWindowEvidence[] ReadParentChain(nint first)
    {
        var result = new List<NativeWindowEvidence>();
        var seen = new HashSet<nint>();
        for (nint current = first; current != 0 && result.Count < 8 && seen.Add(current); current = GetParent(current))
            result.Add(ReadNativeWindow(current));
        return result.ToArray();
    }
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0L, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0L, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static string SafeError(Exception error) => error is InvalidOperationException && error.Message.Length is > 0 and <= 100 &&
        error.Message.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? error.Message : "DIAGNOSTIC_FAILED_" + error.GetType().Name;
    private static void CheckPath(string directory)
    {
        for (DirectoryInfo? item = new(directory); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("REPARSE_PATH_REJECTED");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public NativeRect CaretRect;
    }
    private delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
}
