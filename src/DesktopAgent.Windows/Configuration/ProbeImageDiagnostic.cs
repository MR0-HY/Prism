using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopAgent.Core.Domain;

namespace DesktopAgent.Windows.Configuration;

internal static class ProbeImageDiagnostic
{
    public static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var cases = SyntheticProbeImages.Create();
        var manifest = new List<object>();
        for (int i = 0; i < cases.Length; i++)
        {
            var test = cases[i];
            await File.WriteAllBytesAsync(Path.Combine(directory, $"case-{i}.png"), test.Image.Bytes.ToArray());
            if (test.Overview is not null)
                await File.WriteAllBytesAsync(Path.Combine(directory, $"overview-{i}.png"), test.Overview.Bytes.ToArray());
            manifest.Add(new { index = i, test.Id, test.Image.Width, test.Image.Height, cropped = test.Overview is not null,
                expected = test.Expected, localTruthOnly = true });
        }
        // Round-trip the new production evidence writer with invented data only.
        var evidenceStore = new LocalConfigurationStore(Path.Combine(directory, "isolated-evidence-store"));
        var paired = SyntheticProbeImages.CreateGroundingDiagnostic();
        if (paired.Length != 2 || paired[0].Id == paired[1].Id || paired[0].Expected != paired[1].Expected ||
            paired[0].Image.Width != paired[1].Image.Width || paired[0].Image.Height != paired[1].Image.Height ||
            !paired[0].Image.Bytes.Span.SequenceEqual(paired[1].Image.Bytes.Span) ||
            paired.Any(c => c.Overview is not null))
            throw new InvalidOperationException("DIAGNOSTIC_SCENES_DIFFER");
        string evidenceDirectory = await evidenceStore.BeginSyntheticProbeEvidenceAsync(paired, true, default);
        var testReport = new DesktopAgent.Core.Domain.ProbeReport("SYNTHETIC_OFFLINE_ONLY", [], 0, new(null, null), DateTimeOffset.UtcNow, [], false, true);
        await evidenceStore.CompleteSyntheticProbeEvidenceAsync(evidenceDirectory, testReport);
        using (var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidenceDirectory, "cases.json"))))
        {
            var savedCases = evidence.RootElement.GetProperty("cases");
            if (savedCases.GetArrayLength() != 2 || !evidence.RootElement.GetProperty("diagnosticOnly").GetBoolean() ||
                !savedCases[0].GetProperty("expected").TryGetProperty("HitBox", out _) ||
                !(await File.ReadAllBytesAsync(Path.Combine(evidenceDirectory, "case-00.png"))).AsSpan().SequenceEqual(paired[0].Image.Bytes.Span) ||
                !(await File.ReadAllBytesAsync(Path.Combine(evidenceDirectory, "case-01.png"))).AsSpan().SequenceEqual(paired[1].Image.Bytes.Span))
                throw new InvalidOperationException("SYNTHETIC_EVIDENCE_ROUNDTRIP_FAILED");
            for (int i = 0; i < paired.Length; i++)
            {
                var actualRequest = Core.Providers.VisualProbe.RequestForAttempt(paired[i], true, i);
                if (savedCases[i].GetProperty("request").GetProperty("UserPrompt").GetString() != actualRequest.UserPrompt ||
                    savedCases[i].GetProperty("request").GetProperty("SystemPrompt").GetString() != actualRequest.SystemPrompt ||
                    savedCases[i].GetProperty("promptVariant").GetString() != Core.Providers.VisualProbe.PromptVariantForAttempt(paired[i], true, i))
                    throw new InvalidOperationException("SAVED_PROMPT_DIFFERS_FROM_REQUEST");
            }
        }
        string fullEvidenceDirectory = await evidenceStore.BeginSyntheticProbeEvidenceAsync(cases, false, default);
        if (!(await File.ReadAllBytesAsync(Path.Combine(fullEvidenceDirectory, "overview-06.png"))).AsSpan().SequenceEqual(cases[6].Overview!.Bytes.Span))
            throw new InvalidOperationException("OVERVIEW_EVIDENCE_ROUNDTRIP_FAILED");
        await CheckCheckpointRoundTripAsync(evidenceStore, evidenceDirectory, directory, paired[0]);
        // Isolated empty store: never renders a user's existing configuration or password.
        var window = new SettingsWindow(new LocalConfigurationStore(Path.Combine(directory, "empty-ui-store")));
        window.Show();
        try
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)window.Content;
            content.UpdateLayout();
            int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
            var foreground = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            foreground.Render(content);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                dc.DrawRectangle(window.Background, null, bounds);
                dc.DrawImage(foreground, bounds);
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "settings.png"));
            encoder.Save(stream);
        }
        finally { window.Close(); }
        await File.WriteAllTextAsync(Path.Combine(directory, "images.json"), JsonSerializer.Serialize(new
        {
            generatedAtUtc = DateTimeOffset.UtcNow, modelCalls = 0, evidenceMode = "WPF_SYNTHETIC_RENDER_NOT_DESKTOP_CAPTURE", cases = manifest,
            syntheticEvidenceRoundTrip = true, sameSceneComparison = true, checkpointRoundTrip = true, evidenceDirectory, fullEvidenceDirectory
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task CheckCheckpointRoundTripAsync(LocalConfigurationStore store, string evidenceDirectory,
        string diagnosticDirectory, GeneratedProbeImage image)
    {
        string variant = Core.Providers.VisualProbe.PromptVariantForAttempt(image, true, 0);
        var before = new ProbeCheckpoint(0, image.Id, variant, DateTimeOffset.UtcNow);
        var completed = before with
        {
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Attempt = new ProbeAttempt(image.Id, ProbeKind.GroundPoint, true, true, null, 0, new(0, 0), PromptVariant: variant)
        };
        await store.SaveSyntheticProbeCheckpointAsync(evidenceDirectory, before, default);
        await store.SaveSyntheticProbeCheckpointAsync(evidenceDirectory, completed, default);
        string beforePath = Path.Combine(evidenceDirectory, "checkpoint-00-before-send.json");
        string completedPath = Path.Combine(evidenceDirectory, "checkpoint-00-completed.json");
        byte[] beforeBytes = await File.ReadAllBytesAsync(beforePath), completedBytes = await File.ReadAllBytesAsync(completedPath);
        if (JsonSerializer.Deserialize<ProbeCheckpoint>(beforeBytes) != before ||
            JsonSerializer.Deserialize<ProbeCheckpoint>(completedBytes) != completed)
            throw new InvalidOperationException("CHECKPOINT_ROUNDTRIP_FAILED");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await store.SaveSyntheticProbeCheckpointAsync(evidenceDirectory, completed with { RecordedAtUtc = DateTimeOffset.UtcNow }, cancelled.Token);
            throw new InvalidOperationException("CANCELLED_CHECKPOINT_WRITE_ACCEPTED");
        }
        catch (OperationCanceledException) { }
        foreach (var invalid in new[]
        {
            before with { Id = "WRONG_CASE" },
            before with { PromptVariant = "WRONG_VARIANT" },
            completed with { Attempt = completed.Attempt! with { Id = "WRONG_ATTEMPT" } },
            completed with { Attempt = completed.Attempt! with { PromptVariant = "WRONG_VARIANT" } },
            before with { Index = -1 }, before with { Index = 8 }, before with { Index = 2 }
        })
            await ExpectCheckpointRejectedAsync(store, evidenceDirectory, invalid);
        string outsideDirectory = Path.Combine(diagnosticDirectory, "outside-evidence-store");
        Directory.CreateDirectory(outsideDirectory);
        File.Copy(Path.Combine(evidenceDirectory, "cases.json"), Path.Combine(outsideDirectory, "cases.json"), overwrite: true);
        await ExpectCheckpointRejectedAsync(store, outsideDirectory, before);
        if (!(await File.ReadAllBytesAsync(beforePath)).AsSpan().SequenceEqual(beforeBytes) ||
            !(await File.ReadAllBytesAsync(completedPath)).AsSpan().SequenceEqual(completedBytes) ||
            Directory.GetFiles(evidenceDirectory, "checkpoint-*.json").Length != 2 ||
            Directory.GetFiles(evidenceDirectory, "*.tmp").Length != 0 ||
            Directory.GetFiles(outsideDirectory, "checkpoint-*.json").Length != 0)
            throw new InvalidOperationException("REJECTED_CHECKPOINT_WRITE_CHANGED_EVIDENCE");
    }

    private static async Task ExpectCheckpointRejectedAsync(LocalConfigurationStore store, string directory, ProbeCheckpoint checkpoint)
    {
        try { await store.SaveSyntheticProbeCheckpointAsync(directory, checkpoint, default); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("INVALID_CHECKPOINT_WRITE_ACCEPTED");
    }
}
