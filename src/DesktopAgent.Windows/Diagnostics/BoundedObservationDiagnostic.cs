using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Windows.Input;
using DesktopAgent.Windows.Observation;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Two fresh, independently bound observations; no provider, setting workflow, or input executor.</summary>
internal static class BoundedObservationDiagnostic
{
    internal static async Task<int> RunAsync(string planPath, string directory, string? targetName = null)
    {
        var clock = Stopwatch.StartNew();
        ObservationPlan plan;
        try
        {
            if (targetName is not null && (string.IsNullOrWhiteSpace(targetName) || targetName.Length > 128 || targetName.Any(char.IsControl))) return 2;
            plan = ReadPlan(planPath);
            directory = Path.GetFullPath(directory);
            if (File.Exists(directory) || Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
            Directory.CreateDirectory(directory);
        }
        catch { return 2; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1, 27000 - clock.ElapsedMilliseconds)));
        var ct = deadline.Token;
        var desktop = new WindowsDesktopObserver(preferForegroundWindow: true);
        var controlTrace = new System.Collections.Concurrent.ConcurrentQueue<object>();
        using var controls = new WindowsControlObserver(desktop, entry => { if (controlTrace.Count < 256) controlTrace.Enqueue(entry); });
        HostedWindowIdentity? binding = null;
        ForegroundIdentity? latest = null;
        string stage = "BINDING_PLANNED_WINDOW";
        string? failure = null;
        bool pixelsCleared = false, activationAttempted = false;
        bool? activationReturned = null;
        var availability = new List<bool>();
        Task Write(string name, object value, CancellationToken token) => HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, name), value, token);
        void CheckBinding()
        {
            ct.ThrowIfCancellationRequested();
            if (binding is null || !binding.IsCurrent()) throw new InvalidOperationException("OBSERVATION_WINDOW_BINDING_CHANGED");
        }
        async Task<DesktopEnvironment> EnvironmentAsync()
        {
            CheckBinding();
            var environment = await desktop.GetEnvironmentAsync(ct).WaitAsync(ct);
            latest = environment.Foreground;
            if (environment.SessionState != DesktopSessionState.Available || latest is null || latest.ProcessId != binding!.HostProcessId ||
                ParseHwnd(latest.HwndHex) != binding.HostHwnd || !string.Equals(latest.ProcessName, binding.HostProcessName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OBSERVATION_TARGET_NOT_FOREGROUND");
            CheckBinding();
            return environment;
        }
        try
        {
            await Write("run.json", new { atUtc = DateTimeOffset.UtcNow, plan, targetName, maximumMs = 30000,
                modelCalls = 0, inputInjected = false, settingsVerified = false, restorationVerified = false }, ct);
            var initial = Win32InputDevice.ReadTarget(ParseHwnd(plan.ExpectedHwndHex), new(0, 0, 1, 1)).Foreground;
            using (var process = Process.GetProcessById(initial.ProcessId)) initial = initial with { ProcessName = process.ProcessName };
            if (!string.Equals(initial.ProcessName, plan.ExpectedProcessName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OBSERVATION_PLANNED_PROCESS_MISMATCH");
            binding = HostedWindowIdentity.TryCapture(initial);
            if (binding is null || binding.HostProcessName is not ("SystemSettings" or "explorer") &&
                !(binding.HostProcessName == "ApplicationFrameHost" && binding.ContentProcessName is "SystemSettings" or "CalculatorApp"))
                throw new InvalidOperationException("OBSERVATION_PROCESS_SCOPE_REJECTED");
            CheckBinding();
            stage = "ACTIVATING_PLANNED_WINDOW";
            await Write("activation-before.json", new { atUtc = DateTimeOffset.UtcNow, initial, binding = Evidence(binding),
                maximumWaitMs = 10000, operation = "SetForegroundWindow_ONCE_IF_NEEDED", preparationOnly = true }, ct);
            CheckBinding();
            if (GetForegroundWindow() != binding.HostHwnd)
            { activationAttempted = true; activationReturned = SetForegroundWindow(binding.HostHwnd); }
            var activation = Stopwatch.StartNew();
            while (GetForegroundWindow() != binding.HostHwnd)
            {
                CheckBinding();
                if (activation.ElapsedMilliseconds >= 10000) throw new InvalidOperationException("OBSERVATION_ACTIVATION_WAIT_EXPIRED");
                await Task.Delay(50, ct);
            }
            _ = await EnvironmentAsync();
            await Write("activation-completed.json", new { atUtc = DateTimeOffset.UtcNow, activationAttempted, activationReturned,
                foreground = latest, binding = Evidence(binding), modelCalls = 0, inputInjected = false }, ct);
            var lease = new Lease(Guid.NewGuid(), 0);
            for (int index = 1; index <= 2; index++)
            {
                if (index > 1) await Task.Delay(500, ct);
                stage = "OBSERVING_BOUND_WINDOW_" + index;
                var environment = await EnvironmentAsync();
                var display = environment.Displays.OrderByDescending(d => Intersection(d.Bounds, latest!.WindowRect)).First();
                await Write($"observation-{index:D2}-before.json", new { atUtc = DateTimeOffset.UtcNow, foreground = latest, binding = Evidence(binding) }, ct);
                var frame = await desktop.CaptureAsync(lease, display.Id, null, ct).WaitAsync(ct);
                _ = await EnvironmentAsync();
                if (frame.Foreground.ProcessId != binding.HostProcessId || ParseHwnd(frame.Foreground.HwndHex) != binding.HostHwnd ||
                    frame.Foreground.ProcessName != binding.HostProcessName || !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion))
                    throw new InvalidOperationException("OBSERVATION_FRAME_SCOPE_MISMATCH");
                await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(directory, $"observation-{index:D2}.png"), frame.Image.Bytes.ToArray(), ct);
                var snapshot = await controls.ObserveAsync(frame, ct).WaitAsync(ct);
                _ = await EnvironmentAsync();
                await Write($"observation-{index:D2}.json", new { atUtc = DateTimeOffset.UtcNow, frame, snapshot, binding = Evidence(binding) }, ct);
                var matches = targetName is null ? [] : snapshot.Candidates.Where(c => c.Enabled && c.Role is not ("Window" or "Text" or "Image") && c.Name == targetName).ToArray();
                var target = targetName is null ? snapshot.Candidates.FirstOrDefault(c => c.Enabled && c.Focused && c.Role != "Window")
                    ?? snapshot.Candidates.FirstOrDefault(c => c.Enabled && c.Role == "Button") : matches.Length == 1 ? matches[0] : null;
                _ = await EnvironmentAsync();
                var resolution = target is null ? new ControlResolution(null, targetName is null ? "NO_ELIGIBLE_CONTROL" : "NAMED_CONTROL_NOT_UNIQUE_OR_MISSING")
                    : await controls.ResolveAsync(frame, snapshot, target.Id, ct).WaitAsync(ct);
                _ = await EnvironmentAsync();
                bool usable = snapshot.Status is ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial &&
                    target is not null && resolution.Point is not null && resolution.ErrorCode is null;
                await Write($"resolution-{index:D2}.json", new { atUtc = DateTimeOffset.UtcNow, targetName, namedMatchCount = targetName is null ? (int?)null : matches.Length, target, resolution, usable,
                    readOnly = true, modelCalls = 0, inputInjected = false }, ct);
                availability.Add(usable);
            }
        }
        catch (Exception error) { failure = SafeError(error); }
        finally
        {
            var cleanup = desktop.DisposeAsync().AsTask();
            try { await cleanup.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(2000, 29500 - clock.ElapsedMilliseconds)))); pixelsCleared = true; }
            catch { failure ??= "OBSERVATION_PIXEL_CLEANUP_INCOMPLETE"; _ = cleanup.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        }
        if (clock.ElapsedMilliseconds >= 30000) failure ??= "OBSERVATION_DEADLINE_EXCEEDED";
        bool completed = availability.Count == 2 && failure is null && pixelsCleared;
        bool usableBothTimes = completed && availability.All(usable => usable);
        await Write("control-resolution-trace.json", controlTrace.ToArray(), CancellationToken.None);
        await Write("report.json", new { atUtc = DateTimeOffset.UtcNow, completed, usableBothTimes, targetName, stage, failure,
            observationsCompleted = availability.Count, availability, foreground = latest, binding = Evidence(binding), activationAttempted, activationReturned,
            elapsedMs = clock.ElapsedMilliseconds, pixelsCleared, modelCalls = 0, inputInjected = false, settingsVerified = false,
            originalValueVerified = false, restorationVerified = false, userApplicationClosed = false, userApplicationResized = false }, CancellationToken.None);
        return usableBothTimes ? 0 : 1;
    }
    private sealed record ObservationPlan(string Goal, string ExpectedProcessName, string ExpectedHwndHex, int ActionLimit, int RequestLimit, long ActiveMsLimit);
    private static ObservationPlan ReadPlan(string path)
    {
        using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 16384) throw new InvalidOperationException("PLAN_SIZE_INVALID");
        byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        string[] names = ["goal", "expectedProcessName", "expectedHwndHex", "actionLimit", "requestLimit", "activeMsLimit"];
        var fields = root.EnumerateObject().ToArray();
        if (fields.Length != 6 || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 6 || fields.Any(p => !names.Contains(p.Name, StringComparer.Ordinal)))
            throw new InvalidOperationException("PLAN_FIELDS_INVALID");
        string goal = root.GetProperty("goal").GetString() ?? "", process = root.GetProperty("expectedProcessName").GetString() ?? "", hwnd = root.GetProperty("expectedHwndHex").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(goal) || goal.Length > 4000 || process is not ("SystemSettings" or "explorer" or "ApplicationFrameHost") || ParseHwnd(hwnd) == 0 ||
            !root.GetProperty("actionLimit").TryGetInt32(out int actions) || actions is < 1 or > 12 || !root.GetProperty("requestLimit").TryGetInt32(out int requests) || requests is < 1 or > 16 ||
            !root.GetProperty("activeMsLimit").TryGetInt64(out long active) || active is < 10 or > 180000) throw new InvalidOperationException("PLAN_VALUES_INVALID");
        return new(goal, process, hwnd, actions, requests, active);
    }
    private static object? Evidence(HostedWindowIdentity? identity) => identity is null ? null : new
    { hostHwndHex = identity.HostHwnd.ToString("X"), identity.HostProcessId, identity.HostProcessName, identity.HostStartTimeUtcTicks,
        contentHwndHex = identity.ContentHwnd?.ToString("X"), identity.ContentProcessId, identity.ContentProcessName, identity.ContentStartTimeUtcTicks };
    private static nint ParseHwnd(string text) => text.Length is > 0 and <= 18 && long.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long hwnd) ? (nint)hwnd : 0;
    private static long Intersection(PhysicalRect a, PhysicalRect b) => Math.Max(0L, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0L, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
    private static string SafeError(Exception error) => error is OperationCanceledException ? "OBSERVATION_DEADLINE" : error is InvalidOperationException &&
        error.Message.Length is > 0 and <= 100 && error.Message.All(c => char.IsAsciiLetterUpper(c) || c == '_') ? error.Message : "OBSERVATION_FAILED";
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint hwnd);
}
