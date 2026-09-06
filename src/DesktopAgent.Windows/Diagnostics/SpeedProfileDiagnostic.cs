using System.Collections.Immutable;
using System.IO;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Configuration;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit user-authorized thinking change followed by two new read-only visual probes.</summary>
internal static class SpeedProfileDiagnostic
{
    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(140));
        var store = new LocalConfigurationStore(LocalConfigurationStore.DefaultRoot);
        ProviderProfile? unverified = null;
        bool saved = false, passed = false;
        string? failure = null;
        try
        {
            var matches = (await store.ReadAsync(deadline.Token)).Where(p => p.ProviderKind == ProviderKind.DeepSeek &&
                p.Model == "deepseek-v4-flash-vision-exp" && p.BaseUrl.AbsoluteUri.TrimEnd('/') == "https://api.deepseek.com").ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("EXACT_DEEPSEEK_PROFILE_NOT_UNIQUE");
            var previous = matches[0];
            unverified = previous with { Thinking = "disabled", Capabilities = [], ProbeFingerprint = null };
            await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "configuration-change.json"), new
            {
                atUtc = DateTimeOffset.UtcNow, previousThinking = previous.Thinking, thinking = unverified.Thinking,
                previousFingerprint = ProviderConfiguration.Fingerprint(previous),
                profileFingerprint = ProviderConfiguration.Fingerprint(unverified), previous.ReasoningEffort,
                previous.Model, baseUrl = previous.BaseUrl.AbsoluteUri, previous.RequestTimeoutMs, previous.FrameTtlMs,
                keyChanged = false, previousCapabilitiesInvalidated = true, maximumModelCalls = 2, inputInjected = false
            }, deadline.Token);
            await store.SaveAsync(unverified, deadline.Token);
            saved = true;
            var report = await HybridControlDiagnostic.RunAsync(unverified, store, Path.Combine(directory, "probe"), deadline.Token);
            await store.SaveProbeReportAsync(report);
            deadline.Token.ThrowIfCancellationRequested();
            var verified = unverified with { Capabilities = report.Results.Select(r => new CapabilityRecord(
                r.Name, r.Status, "native-controls-v1", report.TestedAtUtc, report.ProfileFingerprint, r.PublicSummary)).ToImmutableArray() };
            passed = !report.Cancelled && report.ApiAttempts == 2 && DesktopTaskCoordinator.IsProfileVerified(verified, controlsRequired: true);
            if (passed) await store.SaveAsync(verified, deadline.Token);
        }
        catch (OperationCanceledException) { failure = "CANCELLED_OR_DEADLINE"; passed = false; }
        catch { failure = "CONFIGURATION_OR_PROBE_FAILED"; passed = false; }
        if (!passed && saved && unverified is not null)
        {
            try { await store.SaveAsync(unverified, CancellationToken.None); }
            catch { failure = "UNVERIFIED_PROFILE_SAVE_FAILED"; }
        }
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            atUtc = DateTimeOffset.UtcNow, thinkingDisabledSaved = saved, hybridPrerequisitePassed = passed, failure,
            keyChanged = false, inputInjected = false, settingsTasksVerified = false, pureVisionVerified = false
        }, CancellationToken.None);
        return passed ? 0 : 1;
    }
}
