using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Windows.Safety;

/// <summary>Finite opt-in diagnostic: real registration, messages posted only to our own queue.</summary>
internal static class HotkeyDiagnostic
{
    public static async Task<int> RunAsync(string reportPath)
    {
        var checks = new List<object>();
        bool passed = true;
        void Check(string name, bool result) { checks.Add(new { name, passed = result }); passed &= result; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var gate = new InputSafetyGate();
        var service = new EmergencyHotkeyService(gate);
        InputRun? run = null;
        TaskLeaseSession? session = null;
        double? closeMs = null;
        try
        {
            var registered = await service.StartAsync().WaitAsync(deadline.Token);
            Check("native_registration", registered.Registered);
            checks.Add(new { registration = registered });
            if (registered.Registered)
            {
                var collisionGate = new InputSafetyGate();
                await using (var collision = new EmergencyHotkeyService(collisionGate))
                {
                    var rejected = await collision.StartAsync().WaitAsync(deadline.Token);
                    Check("duplicate_registration_fails_closed", !rejected.Registered && !collisionGate.Status.HotkeysReady);
                }
                var registry = new TaskLeaseRegistry();
                Check("lease", registry.TryAcquire(Guid.NewGuid(), out session));
                Check("arm", gate.TryArm(session!, session!.Current, gate.Status.Revision, TimeSpan.FromSeconds(5), out run));
                Check("posted_pause", service.PostDiagnosticSignal(stop: false));
                await UntilAsync(() => service.MessageCount >= 1, deadline.Token);
                Check("pause_closes_gate", !gate.Status.IsOpen && gate.Status.Reason == InputStopReason.Paused && run!.Token.IsCancellationRequested);
                Check("posted_stop", service.PostDiagnosticSignal(stop: true));
                await UntilAsync(() => service.MessageCount >= 2, deadline.Token);
                Check("stop_remains_closed", gate.Status.Reason == InputStopReason.Stopped && !gate.Status.IsOpen);
                closeMs = service.LastCloseTicks * 1000.0 / Stopwatch.Frequency;
                Check("message_to_gate_under_100ms", closeMs <= 100);
            }
        }
        catch (Exception ex) { Check("diagnostic_completed_" + ex.GetType().Name, false); }
        finally
        {
            gate.Trip(InputStopReason.Shutdown);
            if (run is not null)
            {
                try { await UntilAsync(() => gate.TryRelease(run), deadline.Token); }
                catch { Check("input_permit_cleanup", false); }
            }
            if (session is not null)
            {
                await session.RequestCompletionAsync();
                Check("lease_cleanup", session.TryCompleteCleanup());
            }
            try { await service.DisposeAsync(); Check("message_thread_exit", service.ThreadExited); }
            catch { Check("message_thread_exit", false); }
        }
        if (passed)
        {
            var freshGate = new InputSafetyGate();
            await using var fresh = new EmergencyHotkeyService(freshGate);
            var result = await fresh.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Check("registration_after_cleanup", result.Registered);
        }
        var report = new
        {
            atUtc = DateTimeOffset.UtcNow, passed,
            evidenceMode = "NATIVE_HOTKEY_REGISTRATION_AND_POSTED_THREAD_MESSAGES",
            physicalHotkeyPress = "NOT_RUN", systemInputInjected = false, modelCalls = 0,
            closeMs, measurement = "WM_HOTKEY handler entry to gate trip completion, not hardware key latency", checks
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }
}
