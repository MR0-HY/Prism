using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Observation;
using DesktopAgent.Windows.Safety;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Finite real Explorer desktop rename of one newly-created empty test directory.
/// No model, user-file edit, UIA mutation or blind cleanup input.</summary>
internal static class ShellRenameDiagnostic
{
    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        TextReplacementDiagnostic.CheckDirectory(directory);
        if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
        Directory.CreateDirectory(directory);
        string desktopPath = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        string id = Guid.NewGuid().ToString("N");
        string originalName = "DesktopAgent-Rename-" + id, desiredName = "DesktopAgent-Rn-" + id;
        string originalPath = Path.Combine(desktopPath, originalName), desiredPath = Path.Combine(desktopPath, desiredName);
        var clock = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var gate = new InputSafetyGate();
        using var deadlineRegistration = deadline.Token.Register(() => gate.Trip(InputStopReason.Deadline));
        var hotkeys = new EmergencyHotkeyService(gate);
        var desktop = new WindowsDesktopObserver(preferForegroundWindow: true);
        using var controls = new WindowsControlObserver(desktop, detail => File.AppendAllText(Path.Combine(directory, "resolution-trace.jsonl"),
            JsonSerializer.Serialize(new { atUtc = DateTimeOffset.UtcNow, detail }) + System.Environment.NewLine));
        TaskLeaseSession? session = null; LeaseWorker? worker = null; InputRun? run = null;
        CancellationTokenSource? active = null;
        var actions = new List<ActionResult>();
        ForegroundIdentity? shell = null;
        string stage = "PREPARING"; string? failure = null, cleanupFailure = null;
        DateTime createdUtc = default;
        bool created = false, selected = false, replacementVerified = false, fileSystemRenamed = false;
        bool minimized = false, restoreAttempted = false, windowsRestored = false, cancelled = false;
        bool unchangedPriorEditCancelled = false;
        string? restoreError = null;
        bool directoryRestored = false, directoryRemoved = false, inputCleanup = true, leaseCleanup = true, hotkeyCleanup = false, pixelsCleared = false;
        TextReplacementPlan? plan = null;
        CancellationToken Token() => active?.Token ?? deadline.Token;
        async Task Write(string name, object value, CancellationToken ct)
        {
            TextReplacementDiagnostic.CheckDirectory(directory);
            await using var output = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(output, value, new JsonSerializerOptions { WriteIndented = true }, ct);
        }
        bool OwnedEmpty(string path)
        {
            string actual = Path.GetFullPath(path);
            if (actual != originalPath && actual != desiredPath || Path.GetDirectoryName(actual) != desktopPath ||
                !Path.GetFileName(actual).EndsWith(id, StringComparison.Ordinal) || !Directory.Exists(actual)) return false;
            var info = new DirectoryInfo(actual);
            return (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.CreationTimeUtc == createdUtc &&
                !Directory.EnumerateFileSystemEntries(actual).Any();
        }
        async Task<DesktopEnvironment> EnvironmentAsync(bool requireShell = true)
        {
            Token().ThrowIfCancellationRequested();
            var env = await desktop.GetEnvironmentAsync(Token()).WaitAsync(Token());
            if (env.SessionState != DesktopSessionState.Available || env.Foreground is null) throw new InvalidOperationException("DESKTOP_UNAVAILABLE");
            if (requireShell && (shell is null || !FrameChecks.SameForeground(env.Foreground, shell))) throw new InvalidOperationException("TEST_SHELL_NOT_FOREGROUND");
            return env;
        }
        async Task<(Frame Frame, ControlSnapshot Snapshot)> Observe(string name, bool requireShell = true)
        {
            stage = name;
            var env = await EnvironmentAsync(requireShell);
            var display = env.Displays.OrderByDescending(d => Intersection(d.Bounds, env.Foreground!.WindowRect)).First();
            var frame = await desktop.CaptureAsync(run!.Lease.Lease, display.Id, null, Token()).WaitAsync(Token());
            _ = await EnvironmentAsync(requireShell);
            var snapshot = await controls.ObserveAsync(frame, Token()).WaitAsync(Token());
            _ = await EnvironmentAsync(requireShell);
            await Write(name + ".json", new { atUtc = DateTimeOffset.UtcNow, frame, snapshot }, Token());
            await using var image = new FileStream(Path.Combine(directory, name + ".png"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await image.WriteAsync(frame.Image.Bytes, Token());
            return (frame, snapshot);
        }
        async Task Act(string name, Frame frame, AgentAction action, ControlSnapshot? snapshot = null, ControlCandidate? target = null,
            bool requireShell = true)
        {
            stage = name;
            if (actions.Count >= 7) throw new InvalidOperationException("ACTION_LIMIT");
            var task = new TaskContext(run!.Lease.Lease.TaskId, run.Lease.Lease.Epoch,
                "最小化所有窗口，将本次创建的桌面测试文件夹重命名。测试文件夹为" + originalName, DateTimeOffset.UtcNow,
                TaskState.Running, false, "OWNED_REAL_SHELL_DIAGNOSTIC_NO_MODEL", frame.MonitorId, new(7, 0, 20000),
                new(actions.Count, 0, clock.ElapsedMilliseconds, 0, 0), [], null, null);
            var proposal = new Proposal(2, Guid.NewGuid().ToString("N"), task.Id, task.Epoch, frame.Id, "唯一自有测试目录", "核对原生编辑框和实际目录",
                new ActDecision(action, "唯一自有测试目录", desiredName));
            var validation = new DesktopPolicyValidator(TimeSpan.FromSeconds(10), Environment.ProcessId)
                .Validate(task, frame, proposal, await EnvironmentAsync(requireShell));
            await Write(name + "-before.json", new { atUtc = DateTimeOffset.UtcNow, frame.Id, validation.Code, validation.Disposition,
                action = JsonSerializer.SerializeToElement(action, action.GetType()), target }, Token());
            if (validation.Disposition != PolicyDisposition.Allow || validation.Action is null) throw new InvalidOperationException("POLICY_" + validation.Code);
            if (snapshot is not null && target is not null)
            {
                var resolved = await controls.ResolveAsync(frame, snapshot, target.Id, Token()).WaitAsync(Token());
                await Write(name + "-resolution.json", new { atUtc = DateTimeOffset.UtcNow, target, resolved }, Token());
                if (resolved.ErrorCode is not null || resolved.Point is null) throw new InvalidOperationException("TARGET_RESOLUTION_" + resolved.ErrorCode);
                if (action is ClickAction click && InputCoordinates.ToPhysical(click.Point, frame.PhysicalRegion) != resolved.Point.Value)
                    throw new InvalidOperationException("CLICK_POINT_CHANGED");
            }
            if (action is TextAction && TextReplacement.ValidateSelection(plan!, frame, snapshot) is { } selectionError)
                throw new InvalidOperationException(selectionError);
            _ = await EnvironmentAsync(requireShell);
            var result = await new WindowsInputExecutor(desktop, gate, run, TimeSpan.FromSeconds(10)).ExecuteAsync(task.Lease, validation.Action, Token());
            actions.Add(result);
            await Write(name + "-completed.json", new { atUtc = DateTimeOffset.UtcNow, result }, CancellationToken.None);
            Token().ThrowIfCancellationRequested();
            if (result.Status != ActionStatus.Injected || result.AppliedEventCount <= 0) throw new InvalidOperationException("NATIVE_INPUT_NOT_APPLIED");
        }
        ControlCandidate RenameEditor(ControlSnapshot snapshot, string value) =>
            snapshot.Candidates.SingleOrDefault(c => c.Id == NativeShellRenameEdit.CandidateId && c.Role == "Edit" && c.Enabled && c.Focused &&
                c.Focusable && c.CurrentValue == value && c.ValueTruncated == false) ?? throw new InvalidOperationException("OWNED_NATIVE_RENAME_EDIT_UNAVAILABLE");
        async Task RestoreWindows(string name)
        {
            if (!minimized || restoreAttempted || Token().IsCancellationRequested || clock.ElapsedMilliseconds >= 18000 || run is null || shell is null)
                throw new InvalidOperationException("WINDOW_RESTORE_NOT_AVAILABLE");
            var fresh = await Observe(name);
            var environment = await EnvironmentAsync();
            if (FrameChecks.Validate(fresh.Frame, run.Lease.Lease, environment, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10)) is not null ||
                fresh.Snapshot.Candidates.Any(c => c.Role == "MenuItem") || !ShellFreeForRestore(shell))
                throw new InvalidOperationException("WINDOW_RESTORE_MENU_ACTIVE");
            restoreAttempted = true;
            await Write(name + "-before.json", new { atUtc = DateTimeOffset.UtcNow, action = "WIN+SHIFT+M", fresh.Frame.Id,
                explicitDiagnosticOnly = true, sameGateAndLease = true, afterCancellation = false }, Token());
            var result = await new InputGestureRunner(gate, new Win32InputDevice()).ExecuteAsync(run,
                new(fresh.Frame.Foreground, fresh.Frame.PhysicalRegion, Win32InputDevice.VirtualDesktop()),
                new HotkeyAction([AgentKey.WIN, AgentKey.SHIFT, AgentKey.M]), Token());
            windowsRestored = result.Status == "applied" && result.CleanupComplete;
            await Write(name + "-completed.json", new { atUtc = DateTimeOffset.UtcNow, result, windowsRestored,
                exactPriorWindowStateVerified = false }, CancellationToken.None);
            if (!windowsRestored) throw new InvalidOperationException("WINDOW_RESTORE_NOT_APPLIED");
        }
        try
        {
            await Write("run.json", new { atUtc = DateTimeOffset.UtcNow, maximumMs = 20000, maximumGestures = 8, originalPath, desiredPath,
                modelCalls = 0, apiCalls = 0, userFilesTouched = false,
                operations = new[] { "WIN+M", "ESC_ONLY_EXISTING_UNCHANGED_LABEL_EDIT", "CLICK_OWN_TEST_FOLDER", "F2", "CTRL+A", "REPLACE_NAME", "ENTER", "WIN+SHIFT+M_IF_EXPECTED_SHELL" },
                fixtureCleanup = "Restore own empty directory name and remove only that directory, no input after cancellation",
                pause = "Ctrl+Alt+F8", stop = "Ctrl+Alt+F9" }, Token());
            var registration = await hotkeys.StartAsync().WaitAsync(Token());
            await Write("hotkeys.json", registration, Token());
            if (!registration.Registered) throw new InvalidOperationException("HOTKEYS_UNAVAILABLE");
            var registry = new TaskLeaseRegistry();
            if (!registry.TryAcquire(Guid.NewGuid(), out session) || !session!.TryStartWorker(session.Current, out worker) ||
                !gate.TryArm(session, session.Current, gate.Status.Revision, TimeSpan.FromSeconds(20), out run)) throw new InvalidOperationException("LEASE_UNAVAILABLE");
            active = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, run!.Token);
            if (!Directory.Exists(desktopPath) || File.Exists(originalPath) || Directory.Exists(originalPath) || File.Exists(desiredPath) || Directory.Exists(desiredPath))
                throw new InvalidOperationException("TEST_DIRECTORY_NOT_FRESH");
            Directory.CreateDirectory(originalPath); createdUtc = Directory.GetCreationTimeUtc(originalPath); created = true;
            if (!OwnedEmpty(originalPath)) throw new InvalidOperationException("TEST_DIRECTORY_IDENTITY_INVALID");
            var initial = await Observe("01-initial", false);
            await Act("01-minimize", initial.Frame, new HotkeyAction([AgentKey.WIN, AgentKey.M]), requireShell: false);
            minimized = true;
            var waiting = Stopwatch.StartNew();
            while (true)
            {
                var env = await EnvironmentAsync(false);
                if (IsShell(env.Foreground!)) { shell = env.Foreground; break; }
                if (waiting.ElapsedMilliseconds > 2000) throw new InvalidOperationException("DESKTOP_NOT_REACHED");
                await Task.Delay(60, Token());
            }
            await Task.Delay(350, Token());
            var shown = await Observe("02-desktop");
            var priorEdits = shown.Snapshot.Candidates.Where(c => c.Id == NativeShellRenameEdit.CandidateId).ToArray();
            if (priorEdits.Length > 0)
            {
                // Explorer can defer publishing new desktop items while an old label edit is open.
                // Escape is allowed only after proving that this editor still contains its existing selected item's exact name.
                var selectedExisting = shown.Snapshot.Candidates.Where(c => c.Role == "ListItem" && c.Selected == true && c.Enabled).ToArray();
                var prior = priorEdits.Single();
                string? originalLabel = prior.CurrentValue;
                if (prior.ValueTruncated != false || string.IsNullOrEmpty(originalLabel) || !prior.Focused ||
                    selectedExisting.Length != 1 || selectedExisting[0].Name != originalLabel || Path.GetFileName(originalLabel) != originalLabel ||
                    !Directory.Exists(Path.Combine(desktopPath, originalLabel)) ||
                    !FrameChecks.Contains(selectedExisting[0].Bounds, new(prior.Bounds.Left + prior.Bounds.Width / 2, prior.Bounds.Top + prior.Bounds.Height / 2)))
                    throw new InvalidOperationException("EXISTING_LABEL_EDIT_NOT_PROVEN_UNCHANGED");
                await Write("02-prior-label-verification.json", new { atUtc = DateTimeOffset.UtcNow, prior, selectedItem = selectedExisting[0],
                    unchangedNameVerified = true, fileSystemWrite = false }, Token());
                await Act("02-cancel-unchanged-label", shown.Frame, new HotkeyAction([AgentKey.ESC]), shown.Snapshot, prior);
                unchangedPriorEditCancelled = true;
                await Task.Delay(120, Token());
                shown = await Observe("02-desktop-after-cancel");
                if (shown.Snapshot.Candidates.Any(c => c.Id == NativeShellRenameEdit.CandidateId) || !Directory.Exists(Path.Combine(desktopPath, originalLabel)))
                    throw new InvalidOperationException("PRIOR_LABEL_CANCEL_NOT_VERIFIED");
            }
            var itemWait = Stopwatch.StartNew(); int refresh = 0;
            ControlCandidate? item;
            while ((item = shown.Snapshot.Candidates.SingleOrDefault(c => c.Role == "ListItem" && c.Name == originalName && c.Enabled)) is null)
            {
                if (itemWait.ElapsedMilliseconds >= 2000) throw new InvalidOperationException("OWNED_TEST_FOLDER_NOT_VISIBLE");
                await Task.Delay(100, Token());
                shown = await Observe($"02-desktop-refresh-{++refresh:D2}");
            }
            var resolution = await controls.ResolveAsync(shown.Frame, shown.Snapshot, item.Id, Token()).WaitAsync(Token());
            await Write("02-own-folder-resolution.json", new { atUtc = DateTimeOffset.UtcNow, item, resolution }, Token());
            if (resolution.ErrorCode is not null || resolution.Point is not { } point) throw new InvalidOperationException("OWNED_TEST_FOLDER_UNRESOLVED");
            var normalized = new NormalizedPoint((point.X - shown.Frame.PhysicalRegion.Left) * 1000d / (shown.Frame.PhysicalRegion.Width - 1),
                (point.Y - shown.Frame.PhysicalRegion.Top) * 1000d / (shown.Frame.PhysicalRegion.Height - 1));
            await Act("02-select-folder", shown.Frame, new ClickAction(normalized, MouseButton.Left, 1), shown.Snapshot, item);
            var selection = await Observe("03-folder-selected");
            var selectedItems = selection.Snapshot.Candidates.Where(c => c.Role == "ListItem" && c.Selected == true).ToArray();
            if (selectedItems.Length != 1 || selectedItems[0].Name != originalName || !OwnedEmpty(originalPath)) throw new InvalidOperationException("OWNED_FOLDER_SELECTION_UNVERIFIED");
            selected = true;
            await Act("03-start-rename", selection.Frame, new HotkeyAction([AgentKey.F2]), selection.Snapshot, selectedItems[0]);
            await Task.Delay(180, Token());
            var editing = await Observe("04-native-editor");
            var editor = RenameEditor(editing.Snapshot, originalName);
            string? prepareError = TextReplacement.Prepare(editing.Frame, editing.Snapshot,
                new ReplaceTextAction(desiredName, originalName, editing.Snapshot.Id, editor.Id), out plan);
            await Write("04-prepare.json", new { atUtc = DateTimeOffset.UtcNow, prepareError, editor, identityBound = plan?.EditorIdentity is not null }, Token());
            if (prepareError is not null) throw new InvalidOperationException(prepareError);
            await Act("04-select-all", editing.Frame, new HotkeyAction([AgentKey.CTRL, AgentKey.A]), editing.Snapshot, editor);
            var all = await Observe("05-all-selected");
            string? selectionError = TextReplacement.ValidateSelection(plan!, all.Frame, all.Snapshot);
            if (selectionError is not null) throw new InvalidOperationException(selectionError);
            await Act("05-replace", all.Frame, new TextAction(desiredName), all.Snapshot, RenameEditor(all.Snapshot, originalName));
            var changed = await Observe("06-replaced");
            string? resultError = TextReplacement.VerifyResult(plan!, changed.Frame, changed.Snapshot);
            replacementVerified = resultError is null;
            await Write("06-verification.json", new { atUtc = DateTimeOffset.UtcNow, resultError, replacementVerified, oldBounds = editor.Bounds,
                newBounds = changed.Snapshot.Candidates.FirstOrDefault(c => c.Id == NativeShellRenameEdit.CandidateId)?.Bounds }, Token());
            if (resultError is not null) throw new InvalidOperationException(resultError);
            await Act("06-commit", changed.Frame, new HotkeyAction([AgentKey.ENTER]), changed.Snapshot, RenameEditor(changed.Snapshot, desiredName));
            waiting.Restart();
            while (!OwnedEmpty(desiredPath) || Directory.Exists(originalPath))
            {
                _ = await EnvironmentAsync();
                if (waiting.ElapsedMilliseconds > 2000) throw new InvalidOperationException("FILESYSTEM_RENAME_NOT_VERIFIED");
                await Task.Delay(60, Token());
            }
            fileSystemRenamed = true;
            _ = await Observe("07-committed");
            await RestoreWindows("08-restore-windows");
            stage = "COMPLETED";
        }
        catch (Exception error)
        {
            cancelled = Token().IsCancellationRequested;
            failure = error is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : error is InvalidOperationException &&
                error.Message.Length <= 160 && error.Message.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
                ? error.Message : "DIAGNOSTIC_FAILED_" + error.GetType().Name;
            if (!cancelled && minimized && !restoreAttempted && shell is not null && clock.ElapsedMilliseconds < 17000)
            {
                try { await RestoreWindows("failure-restore-windows"); }
                catch (Exception restore) { restoreError = restore.Message.Length < 160 ? restore.Message : restore.GetType().Name; }
            }
        }
        finally
        {
            gate.Trip(InputStopReason.Shutdown);
            if (session is not null) { try { await session.RequestCompletionAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { leaseCleanup = false; } }
            if (run is not null)
            {
                var drain = Stopwatch.StartNew();
                while (!run.CancellationDrained && drain.ElapsedMilliseconds < 1000) await Task.Delay(10);
                inputCleanup = gate.TryRelease(run);
            }
            worker?.Dispose();
            if (session is not null) leaseCleanup &= session.TryCompleteCleanup();
            active?.Dispose();
            try { await hotkeys.DisposeAsync(); hotkeyCleanup = hotkeys.ThreadExited; } catch { }
            try { await desktop.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); pixelsCleared = true; } catch { }
            // Explicit owned-fixture cleanup. Never search, recurse, delete a reparse point, or change another directory.
            if (created)
            {
                try
                {
                    if (OwnedEmpty(desiredPath) && !Directory.Exists(originalPath) && !File.Exists(originalPath)) Directory.Move(desiredPath, originalPath);
                    directoryRestored = OwnedEmpty(originalPath) && !Directory.Exists(desiredPath) && !File.Exists(desiredPath);
                    if (!directoryRestored) throw new InvalidOperationException("OWNED_DIRECTORY_CLEANUP_IDENTITY_CHANGED");
                    if (!OwnedEmpty(originalPath)) throw new InvalidOperationException("OWNED_DIRECTORY_CLEANUP_CHANGED");
                    Directory.Delete(originalPath, recursive: false);
                    directoryRemoved = !Directory.Exists(originalPath) && !Directory.Exists(desiredPath);
                }
                catch (Exception error) { cleanupFailure = error.GetType().Name; }
            }
        }
        bool passed = failure is null && replacementVerified && fileSystemRenamed && actions.Count == (unchangedPriorEditCancelled ? 7 : 6) && windowsRestored && directoryRestored && directoryRemoved &&
            inputCleanup && leaseCleanup && hotkeyCleanup && pixelsCleared;
        await Write("report.json", new { atUtc = DateTimeOffset.UtcNow, passed, stage, failure, elapsedMs = clock.ElapsedMilliseconds,
            originalPath, desiredPath, created, selected, replacementVerified, fileSystemRenamed, directoryRestored, directoryRemoved, cleanupFailure,
            inputCleanup, leaseCleanup, hotkeyCleanup, pixelsCleared, actions, maximumGestures = 8, modelCalls = 0, apiCalls = 0,
            userFilesTouched = false, unchangedPriorEditCancelled, restoreAttempted, windowsRestored, restoreError, cancelled,
            source = "REAL_EXPLORER_DESKTOP_NATIVE_RENAME_OWNED_EMPTY_DIRECTORY_NOT_MODEL_TASK_ACCEPTANCE" }, CancellationToken.None);
        return passed ? 0 : 1;
    }
    private static bool IsShell(ForegroundIdentity foreground)
    {
        var identity = HostedWindowIdentity.TryCapture(foreground);
        if (identity is null || !foreground.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false;
        var name = new System.Text.StringBuilder(128);
        _ = GetClassNameW(identity.HostHwnd, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) *
        Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static bool ShellFreeForRestore(ForegroundIdentity shell)
    {
        var identity = HostedWindowIdentity.TryCapture(shell);
        if (identity is null || GetForegroundWindow() != identity.HostHwnd || !IsShell(shell)) return false;
        var gui = new Gui { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Gui>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(identity.HostHwnd, out _), ref gui) || gui.MenuOwner != 0 || (gui.Flags & 0x1c) != 0) return false;
        // Win+Shift+M is a global window-restoration chord, not text or a rename commit.
        // A label edit alone is no reason to strand every minimized window after a failed diagnostic.
        return true;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Gui { public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref Gui info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, System.Text.StringBuilder name, int capacity);
}
