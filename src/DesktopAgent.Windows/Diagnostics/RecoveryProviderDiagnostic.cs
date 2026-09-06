using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Providers;
using DesktopAgent.Windows.Configuration;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit read-only wire check, using generated pixels only. Never arms input.</summary>
internal static class RecoveryProviderDiagnostic
{
    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(135));
        var store = new LocalConfigurationStore(LocalConfigurationStore.DefaultRoot);
        var profile = (await store.ReadAsync(deadline.Token)).Single(p => p.ProviderKind == ProviderKind.DeepSeek &&
            p.BaseUrl.AbsoluteUri.TrimEnd('/') == "https://api.deepseek.com" && p.Model == "deepseek-v4-flash-vision-exp");
        using var provider = new ChatCompletionProvider(profile, store);
        var generated = SyntheticProbeImages.Create()[0].Image;
        bool passed = true;
        foreach (int level in new[] { 2 })
        {
            var clock = Stopwatch.StartNew();
            string? error = null; ProviderUsage? usage = null; bool json = false;
            string? publicTestAnswer = null;
            var transport = new List<ChatCompletionProvider.TransportTrace?>(); int requestCount = 0;
            try
            {
                requestCount++;
                var reply = await provider.SendImagesAsync("Return one JSON object with an answer string. This is a read-only API diagnostic, no desktop action is allowed.",
                    level == 1 ? "Observe the synthetic image and briefly describe its colored shapes in answer."
                    : "Use web search to consult Microsoft documentation: in Windows scientific Calculator, what do C and the left parenthesis mean? Put the distinction and a source URL in answer. The attached image is synthetic test data, unrelated to any desktop.",
                    [generated], deadline.Token, level);
                transport.Add(provider.LastTransport);
                if (reply.SearchContinuation is { } reference)
                {
                    requestCount++;
                    reply = await provider.SendImagesAsync("Return one JSON object with an answer string. No desktop action is allowed.",
                        "Using the restored search evidence, explain C versus left parenthesis in Windows scientific Calculator, including one source URL in answer.",
                        [generated], deadline.Token, 2, reference);
                    transport.Add(provider.LastTransport);
                    reply = reply with { Usage = new(null, null) }; // Search was ended at a tool result, before aggregate billing usage.
                }
                usage = reply.Usage;
                using var doc = JsonDocument.Parse(reply.Content);
                json = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("answer", out var answer) &&
                    answer.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(answer.GetString());
                if (json) publicTestAnswer = doc.RootElement.GetProperty("answer").GetString(); // Fixed public software question only, no user desktop content.
            }
            catch (ProviderCallException failure) { error = failure.Code; usage = failure.ResponseMetadata?.Usage; }
            catch (OperationCanceledException) { error = "DEADLINE"; }
            catch (JsonException) { error = "INVALID_CONTENT_JSON"; }
            passed &= json && error is null;
            await File.WriteAllTextAsync(Path.Combine(directory, $"level-{level}.json"), JsonSerializer.Serialize(new
            { atUtc = DateTimeOffset.UtcNow, level, profile.Model, elapsedMs = clock.ElapsedMilliseconds, json, error, usage,
                syntheticImagesOnly = true, desktopInput = false, requestCount,
                transport, lastTransport = provider.LastTransport,
                outputShape = provider.LastOutputShape,
                publicTestAnswer,
                scope = "WIRE_ACCEPTANCE_ONLY_NOT_DESKTOP_GROUNDING_OR_SEARCH_QUALITY" }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;
    }
}
