using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Configuration;
using DesktopAgent.Windows.Observation;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit, two-request real-screen/read-only-UIA probe. Never arms an input gate.</summary>
internal static class HybridControlDiagnostic
{
    private const string PromptVariant = "hybrid-controls-v1";

    public static async Task<ProbeReport> RunAsync(ProviderProfile profile, LocalConfigurationStore store,
        string directory, CancellationToken ct)
    {
        directory = PrepareDirectory(directory);
        ProviderConfiguration.Validate(profile);
        var attempts = ImmutableArray.CreateBuilder<ProbeAttempt>();
        bool cancelled = false;
        string? failure = null;
        int apiAttempts = 0;
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "run.json"), new
        {
            schemaVersion = 1, evidenceMode = "OWNED_FIXTURE_REAL_BITBLT_READ_ONLY_UIA", maximumModelCalls = 2,
            modelCallsAtCreation = 0, profileFingerprint = ProviderConfiguration.Fingerprint(profile),
            userSettingsChanged = false, inputInjected = false, promptVariant = PromptVariant, atUtc = DateTimeOffset.UtcNow
        }, ct);
        using var provider = new ChatCompletionProvider(profile, store);
        for (int index = 0; index < 2; index++)
        {
            OwnedObservation? observed = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(65));
            try
            {
                observed = await PrepareCaseAsync(directory, index, deadline.Token);
                var request = HybridVisualProbe.CreateRequest(observed.Frame, observed.Snapshot);
                await SaveCaseAsync(directory, index, observed, request, deadline.Token);
                await EnsureFreshAsync(observed, deadline.Token);
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"checkpoint-{index:D2}-before-send.json"), new
                {
                    index, id = observed.Frame.Id, promptVariant = PromptVariant, recordedAtUtc = DateTimeOffset.UtcNow,
                    stage = "before-send", note = "This checkpoint does not prove server receipt or billing."
                }, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                bool schema = false, passed = false;
                string? error = null;
                ProviderUsage usage = new(null, null);
                ProbeDiagnostic? diagnostic = null;
                apiAttempts++;
                try
                {
                    var reply = await provider.SendImagesAsync(request.SystemPrompt, request.UserPrompt, request.Images, deadline.Token);
                    usage = reply.Usage;
                    var verdict = HybridVisualProbe.Evaluate(observed.Frame, observed.Snapshot, observed.Truth.ExpectedCode,
                        observed.Expected.Id, reply.Content);
                    schema = verdict.SchemaValid;
                    diagnostic = verdict.Diagnostic;
                    if (verdict.SchemaValid && diagnostic.ActualControlId is { } actualId)
                    {
                        var resolution = await observed.Controls.ResolveAsync(observed.Frame, observed.Snapshot, actualId, deadline.Token);
                        bool inside = resolution.Point is { } point && FrameChecks.Contains(observed.Truth.TargetBounds, point);
                        string? stale = null;
                        if (resolution.Point is { } resolved)
                            stale = await observed.Desktop.CheckPointAsync(observed.Frame, observed.Frame.Lease,
                                Normalize(resolved, observed.Frame.PhysicalRegion), TimeSpan.FromMilliseconds(profile.FrameTtlMs), deadline.Token);
                        diagnostic = diagnostic with
                        {
                            PointInsideHitBox = inside && resolution.ErrorCode is null && stale is null,
                            MappedX = resolution.Point?.X - observed.Frame.PhysicalRegion.Left,
                            MappedY = resolution.Point?.Y - observed.Frame.PhysicalRegion.Top
                        };
                        passed = verdict.Passed && diagnostic.PointInsideHitBox == true;
                        error = resolution.ErrorCode ?? stale ?? (passed ? null : "HYBRID_VISUAL_MISMATCH");
                    }
                    else error = schema ? "HYBRID_VISUAL_MISMATCH" : "HYBRID_SCHEMA";
                }
                catch (OperationCanceledException) { cancelled = true; error = "CANCELLED_OR_DEADLINE"; }
                catch (ProviderCallException ex) { error = ex.Code; }
                catch (Exception ex) { error = SafeError(ex); }
                var attempt = new ProbeAttempt(observed.Frame.Id, ProbeKind.GroundPoint, schema, passed, error,
                    timer.ElapsedMilliseconds, usage, diagnostic, PromptVariant);
                attempts.Add(attempt);
                // Keep a completed response even if cancellation arrived while the request was in flight.
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"checkpoint-{index:D2}-completed.json"), new
                {
                    index, id = observed.Frame.Id, promptVariant = PromptVariant, recordedAtUtc = DateTimeOffset.UtcNow,
                    stage = "completed", attempt
                }, CancellationToken.None);
                var progress = HybridVisualProbe.BuildReport(profile, attempts.ToImmutable(), cancelled) with { ApiAttempts = apiAttempts };
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "progress.json"), progress, CancellationToken.None);
                if (cancelled || error is not (null or "HYBRID_VISUAL_MISMATCH" or "HYBRID_SCHEMA")) break;
            }
            catch (OperationCanceledException) { cancelled = true; failure = "CANCELLED_OR_DEADLINE"; break; }
            catch (Exception ex) { failure = SafeError(ex); break; }
            finally { if (observed is not null) await observed.DisposeAsync(); }
        }
        cancelled |= ct.IsCancellationRequested;
        var report = HybridVisualProbe.BuildReport(profile, attempts.ToImmutable(), cancelled) with { ApiAttempts = apiAttempts };
        if (failure is not null)
            report = report with { Results = report.Results.Select(r => r with { Status = CapabilityStatus.Unknown }).ToImmutableArray()
                .Add(new("hybrid_environment", CapabilityStatus.Unknown, failure)) };
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "report.json"), report, CancellationToken.None);
        return report;
    }

    public static async Task<int> RunOfflineAsync(string directory, CancellationToken ct = default)
    {
        directory = PrepareDirectory(directory);
        var results = new List<object>();
        string? failure = null;
        for (int index = 0; index < 2; index++)
        {
            OwnedObservation? observed = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                observed = await PrepareCaseAsync(directory, index, deadline.Token);
                var request = HybridVisualProbe.CreateRequest(observed.Frame, observed.Snapshot);
                await SaveCaseAsync(directory, index, observed, request, deadline.Token);
                await EnsureFreshAsync(observed, deadline.Token);
                var resolution = await observed.Controls.ResolveAsync(observed.Frame, observed.Snapshot, observed.Expected.Id, deadline.Token);
                bool inBox = resolution.Point is { } point && FrameChecks.Contains(observed.Truth.TargetBounds, point);
                string? fresh = resolution.Point is { } resolved
                    ? await observed.Desktop.CheckPointAsync(observed.Frame, observed.Frame.Lease, Normalize(resolved, observed.Frame.PhysicalRegion),
                        TimeSpan.FromSeconds(90), deadline.Token) : "NO_RESOLVED_POINT";
                bool codePrivate = !request.UserPrompt.Contains(observed.Truth.ExpectedCode, StringComparison.Ordinal) &&
                    !request.SystemPrompt.Contains(observed.Truth.ExpectedCode, StringComparison.Ordinal);
                bool allPassed = inBox && resolution.ErrorCode is null && fresh is null && codePrivate;
                results.Add(new { index, passed = allPassed, candidateCount = observed.Snapshot.Candidates.Length,
                    observed.Snapshot.Status, expectedFound = true, visualCodeNotInPrompt = codePrivate,
                    resolvedInsideTarget = inBox, resolution, fresh,
                    imageWidth = observed.Frame.Image.Width, imageHeight = observed.Frame.Image.Height });
                if (!allPassed) { failure = "HYBRID_OFFLINE_CHECK_FAILED"; break; }
            }
            catch (Exception ex) { failure = ex is OperationCanceledException ? "CANCELLED_OR_DEADLINE" : SafeError(ex); break; }
            finally { if (observed is not null) await observed.DisposeAsync(); }
        }
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, "offline-report.json"), new
        {
            passed = failure is null && results.Count == 2, failure, results, modelCalls = 0, inputInjected = false,
            userSettingsChanged = false, evidenceMode = "OWNED_FIXTURE_REAL_BITBLT_AND_READ_ONLY_UIA_NOT_MODEL", atUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        return failure is null && results.Count == 2 ? 0 : 1;
    }

    private static async Task<OwnedObservation> PrepareCaseAsync(string directory, int index, CancellationToken ct)
    {
        string truthPath = Path.Combine(directory, $"fixture-{index:D2}.json");
        var child = StartFixture(truthPath, index);
        var desktop = new WindowsDesktopObserver();
        var controls = new WindowsControlObserver(desktop);
        try
        {
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ready.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(truthPath))
            {
                if (child.HasExited) throw new InvalidOperationException("HYBRID_FIXTURE_EXITED_BEFORE_READY");
                await Task.Delay(40, ready.Token);
            }
            if (new FileInfo(truthPath).Length > 16 * 1024) throw new InvalidOperationException("HYBRID_FIXTURE_TRUTH_TOO_LARGE");
            using var truthDocument = JsonDocument.Parse(await File.ReadAllTextAsync(truthPath, ready.Token));
            var truthJson = truthDocument.RootElement;
            PhysicalRect Rect(string name)
            {
                var value = truthJson.GetProperty(name);
                return new(value.GetProperty("Left").GetInt32(), value.GetProperty("Top").GetInt32(),
                    value.GetProperty("Width").GetInt32(), value.GetProperty("Height").GetInt32());
            }
            var truth = new HybridFixtureTruth(truthJson.GetProperty("ProcessId").GetInt32(), truthJson.GetProperty("Hwnd").GetInt64(),
                truthJson.GetProperty("Layout").GetInt32(), Rect("CanvasBounds"), truthJson.GetProperty("ExpectedCode").GetString()!,
                truthJson.GetProperty("ExpectedTargetName").GetString()!, Rect("TargetBounds"), truthJson.GetProperty("Nonce").GetString()!);
            if (truth.ProcessId != child.Id || truth.ProcessId == Environment.ProcessId || truth.Hwnd == 0 || truth.Layout != index ||
                !truth.CanvasBounds.IsValid || !truth.CanvasBounds.Contains(truth.TargetBounds) || truth.ExpectedCode.Length != 6 ||
                string.IsNullOrWhiteSpace(truth.ExpectedTargetName)) throw new InvalidOperationException("HYBRID_FIXTURE_ID_MISMATCH");
            DesktopEnvironment environment;
            while (true)
            {
                environment = await desktop.GetEnvironmentAsync(ready.Token);
                if (environment.SessionState == DesktopSessionState.Available && environment.Foreground is { } fg &&
                    fg.ProcessId == child.Id && string.Equals(fg.HwndHex, truth.Hwnd.ToString("X"), StringComparison.OrdinalIgnoreCase)) break;
                if (child.HasExited) throw new InvalidOperationException("HYBRID_FIXTURE_EXITED");
                await Task.Delay(40, ready.Token);
            }
            var monitor = environment.Displays.SingleOrDefault(d => d.Bounds.Contains(truth.CanvasBounds))
                ?? throw new InvalidOperationException("HYBRID_FIXTURE_OUTSIDE_MONITOR");
            var lease = new Lease(Guid.NewGuid(), 0);
            // Overview is only an in-memory crop parent. Only this child's full canvas is encoded for the request or saved.
            _ = await desktop.CaptureAsync(lease, monitor.Id, null, ct);
            var frame = await desktop.CaptureAsync(lease, monitor.Id, truth.CanvasBounds, ct);
            if (frame.Foreground.ProcessId != child.Id || frame.Image.Width != truth.CanvasBounds.Width ||
                frame.Image.Height != truth.CanvasBounds.Height || (long)frame.Image.Width * frame.Image.Height > 640000)
                throw new InvalidOperationException("HYBRID_CAPTURE_NOT_ORIGINAL_OWNED_CANVAS");
            var snapshot = await controls.ObserveAsync(frame, ct);
            if (snapshot.Status is not (ControlSnapshotStatus.Available or ControlSnapshotStatus.Partial))
                throw new InvalidOperationException("HYBRID_UIA_UNAVAILABLE");
            if (snapshot.Candidates.Any(c => c.Name.Contains(truth.ExpectedCode, StringComparison.Ordinal) || c.Name.Contains('★')))
                throw new InvalidOperationException("HYBRID_VISUAL_MARKER_LEAKED_TO_UIA");
            var actionable = snapshot.Candidates.Where(c => c.Role == (index == 0 ? "Button" : "CheckBox")).ToArray();
            if (actionable.Length != 8) throw new InvalidOperationException("HYBRID_UIA_CANDIDATES_INCOMPLETE");
            var expected = actionable.SingleOrDefault(c => c.Name == truth.ExpectedTargetName);
            if (expected is null || expected.Bounds != truth.TargetBounds) throw new InvalidOperationException("HYBRID_UIA_TARGET_MISMATCH");
            return new(child, desktop, controls, truth, frame, snapshot, expected);
        }
        catch
        {
            controls.Dispose();
            await desktop.DisposeAsync();
            await CloseOwnedAsync(child);
            throw;
        }
    }

    private static async Task SaveCaseAsync(string directory, int index, OwnedObservation observed, ProbeRequest request, CancellationToken ct)
    {
        await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(directory, $"case-{index:D2}.png"), observed.Frame.Image.Bytes.ToArray(), ct);
        await HybridDiagnosticEvidence.WriteAsync(Path.Combine(directory, $"case-{index:D2}.json"), new
        {
            schemaVersion = 1, index, promptVariant = PromptVariant, observed.Truth,
            frame = observed.Frame, controls = observed.Snapshot, expectedControlId = observed.Expected.Id,
            request = new { request.SystemPrompt, request.UserPrompt }, image = $"case-{index:D2}.png",
            inputInjected = false, modelCallsAtCasePreparation = 0, atUtc = DateTimeOffset.UtcNow
        }, ct);
    }

    private static async Task EnsureFreshAsync(OwnedObservation observed, CancellationToken ct)
    {
        if (observed.Child.HasExited) throw new InvalidOperationException("HYBRID_FIXTURE_EXITED");
        var environment = await observed.Desktop.GetEnvironmentAsync(ct);
        string? stale = FrameChecks.Validate(observed.Frame, observed.Frame.Lease, environment, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90));
        if (stale is not null || environment.Foreground?.ProcessId != observed.Child.Id)
            throw new InvalidOperationException(stale ?? "HYBRID_FIXTURE_NOT_FOREGROUND");
    }

    private static Process StartFixture(string truthPath, int layout)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("HYBRID_APP_PATH_UNAVAILABLE");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--hybrid-fixture"); start.ArgumentList.Add(truthPath); start.ArgumentList.Add(layout.ToString());
        return Process.Start(start) ?? throw new InvalidOperationException("HYBRID_FIXTURE_START_FAILED");
    }

    private static async Task CloseOwnedAsync(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.CloseMainWindow();
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { if (!child.HasExited) child.Kill(entireProcessTree: false); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            }
        }
        catch (InvalidOperationException) { }
        finally { child.Dispose(); }
    }

    private static string PrepareDirectory(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("HYBRID_EVIDENCE_DIRECTORY_NOT_EMPTY");
        Directory.CreateDirectory(directory);
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("HYBRID_EVIDENCE_REPARSE_POINT");
        return directory;
    }

    private static NormalizedPoint Normalize(PhysicalPoint point, PhysicalRect region)
        => new((point.X - region.Left) * 1000d / (region.Width - 1), (point.Y - region.Top) * 1000d / (region.Height - 1));
    private static string SafeError(Exception ex) => ex is InvalidOperationException && ex.Message.Length is > 0 and <= 100 &&
        ex.Message.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? ex.Message : ex.GetType().Name;

    private sealed record OwnedObservation(Process Child, WindowsDesktopObserver Desktop, WindowsControlObserver Controls,
        HybridFixtureTruth Truth, Frame Frame, ControlSnapshot Snapshot, ControlCandidate Expected) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Controls.Dispose();
            await Desktop.DisposeAsync();
            await CloseOwnedAsync(Child);
        }
    }
}
