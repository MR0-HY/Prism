using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Providers;
using Xunit;

namespace DesktopAgent.Core.Tests.Providers;

public sealed class ChatCompletionProviderTests
{
    private const string ResponsesReply = """{"status":"completed","output":[{"type":"reasoning","content":"not an action"},{"type":"web_search_call","status":"completed"},{"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"{\"answer\":true}"}]}],"usage":{"input_tokens":12,"output_tokens":8}}""";

    [Fact]
    public async Task RecoveryThinkingAndSearchAreRequestScopedAndKeepExactModel()
    {
        var profile = ProviderConfiguration.DefaultDeepSeek() with { Thinking = "disabled" };
        int index = 0;
        using var provider = new ChatCompletionProvider(profile, new Secrets(), new Handler(async (http, ct) =>
        {
            using var doc = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var wire = doc.RootElement;
            Assert.Equal(profile.Model, wire.GetProperty("model").GetString());
            int level = index++;
            if (level == 2)
            {
                Assert.Equal("https://api.deepseek.com/responses", http.RequestUri!.AbsoluteUri);
                Assert.True(wire.GetProperty("stream").GetBoolean());
                Assert.Equal("web_search", wire.GetProperty("tools")[0].GetProperty("type").GetString());
                Assert.Equal("low", wire.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.Equal("input_image", wire.GetProperty("input")[0].GetProperty("content")[1].GetProperty("type").GetString());
                Assert.Contains("Never include user text", wire.GetProperty("instructions").GetString());
                return Reply(ResponsesReply);
            }
            Assert.EndsWith("/chat/completions", http.RequestUri!.AbsoluteUri);
            Assert.Equal(level == 1 ? "enabled" : "disabled", wire.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(wire.TryGetProperty("tools", out _));
            return Reply(ValidReply);
        }));
        foreach (int level in new[] { 0, 1, 2, 0 })
        {
            var reply = await provider.SendImagesAsync("Return JSON", "image", Images, default, level);
            Assert.Equal("{\"answer\":true}", reply.Content);
            Assert.Equal(level == 2 ? 12 : 10, reply.Usage.InputTokens);
        }
        Assert.Equal("disabled", profile.Thinking);
    }

    private static string Event(string type, object? response = null) => "event: " + type + "\r\ndata: " +
        JsonSerializer.Serialize(response is null ? new Dictionary<string, object> { ["type"] = type } :
            new Dictionary<string, object> { ["type"] = type, ["response"] = response }) + "\r\n\r\n";
    private static HttpResponseMessage Streaming(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, System.Text.Encoding.UTF8, "text/event-stream") };

    [Fact]
    public async Task SearchStreamOnlyUsesTerminalObjectAndReportsSafePhases()
    {
        var phases = new List<string>();
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) => Task.FromResult(Streaming(
            Event("response.created") + Event("response.reasoning_text.delta") + Event("response.web_search_call.searching") +
            Event("response.output_text.delta") + Event("response.completed", JsonSerializer.Deserialize<JsonElement>(ResponsesReply))))));
        provider.Progress += phases.Add;
        var reply = await provider.SendImagesAsync("Return JSON", "image", Images, default, 2);
        Assert.Equal("{\"answer\":true}", reply.Content); Assert.Equal(12, reply.Usage.InputTokens);
        Assert.Equal(5, provider.LastTransport!.Events); Assert.Equal(1, provider.LastTransport.SearchEvents);
        Assert.Equal(1, provider.LastTransport.ReasoningEvents); Assert.Equal(200, provider.LastTransport.HttpStatus);
        Assert.Contains("模型正在联网查阅软件资料", phases);
    }

    [Theory]
    [InlineData("response.created", "INCOMPLETE_EVENT_STREAM")]
    [InlineData("response.incomplete", "STREAM_TERMINAL_MISMATCH")]
    [InlineData("response.failed", "STREAM_TERMINAL_MISMATCH")]
    public async Task SearchStreamNeverPromotesPartialOrMismatchedResult(string type, string code)
    {
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(Streaming(Event(type, JsonSerializer.Deserialize<JsonElement>(ResponsesReply))))));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("Return JSON", "image", Images, default, 2));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task SearchStreamingRemainsCancellableWithoutRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler(async (_, ct) =>
        { requests++; entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Streaming(""); }));
        using var cancellation = new CancellationTokenSource();
        var call = provider.SendImagesAsync("Return JSON", "image", Images, cancellation.Token, 2);
        await entered.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); Assert.Equal(1, requests);
        Assert.NotNull(provider.LastTransport); Assert.Null(provider.LastTransport.HeadersMs);
    }

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], ct);
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
    }

    [Fact]
    public async Task StreamStallTimesOutIndependentlyOfSearchTotalDeadline()
    {
        var profile = ProviderConfiguration.DefaultDeepSeek() with { RequestTimeoutMs = 10000 };
        using var provider = new ChatCompletionProvider(profile, new Secrets(), new Handler((_, _) =>
        {
            var content = new StreamContent(new StalledStream()); content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        var pending = provider.SendImagesAsync("Return JSON", "image", Images, default, 2);
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => pending.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("RESPONSE_IDLE_TIMEOUT", error.Code); Assert.Equal(200, provider.LastTransport!.HttpStatus);
        Assert.Equal(0, provider.LastTransport.BytesReceived);
    }

    [Fact]
    public async Task StreamHandlesSplitUtf8AndCrLfWithoutUsingDeltasAsFinal()
    {
        string wire = ": 注释\r\n" + Event("response.completed", JsonSerializer.Deserialize<JsonElement>(ResponsesReply));
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
        {
            var content = new StreamContent(new ChunkedStream(System.Text.Encoding.UTF8.GetBytes(wire)));
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        var reply = await provider.SendImagesAsync("Return JSON", "image", Images, default, 2);
        Assert.Equal("{\"answer\":true}", reply.Content); Assert.Equal(1, provider.LastTransport!.Events);
    }

    [Fact]
    public async Task CancellationFromProgressRejectsTerminalInSameNetworkChunk()
    {
        using var ct = new CancellationTokenSource();
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(Streaming(Event("response.web_search_call.searching") + Event("response.completed", JsonSerializer.Deserialize<JsonElement>(ResponsesReply))))));
        provider.Progress += phase => { if (phase == "模型正在联网查阅软件资料") ct.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SendImagesAsync("Return JSON", "image", Images, ct.Token, 2));
    }

    [Fact]
    public async Task SearchStopsAtFirstCompletedToolAndContinuationHasNoSearchTools()
    {
        const string reference = """{"type":"web_search_call","id":"ws_fixture","status":"completed","action":{"type":"search","queries":["Calculator help"]}}""";
        int calls = 0;
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler(async (http, ct) =>
        {
            calls++;
            if (calls == 1) return Streaming("event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"item\":" + reference + "}\n\n");
            using var wire = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("none", wire.RootElement.GetProperty("tool_choice").GetString());
            Assert.Empty(wire.RootElement.GetProperty("tools").EnumerateArray());
            Assert.Equal("ws_fixture", wire.RootElement.GetProperty("input")[0].GetProperty("id").GetString());
            Assert.Contains("NEW current screenshot", wire.RootElement.GetProperty("instructions").GetString());
            return Streaming(Event("response.completed", JsonSerializer.Deserialize<JsonElement>(ResponsesReply)));
        }));
        var first = await provider.SendImagesAsync("Return JSON", "image", Images, default, 2);
        Assert.Empty(first.Content); Assert.NotNull(first.SearchContinuation); Assert.Null(first.Usage.InputTokens);
        var final = await provider.SendImagesAsync("Return JSON", "fresh image", Images, default, 0, first.SearchContinuation);
        Assert.Equal("{\"answer\":true}", final.Content); Assert.Null(final.SearchContinuation); Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("\"status\":\"completed\"", "\"status\":\"incomplete\"", "INCOMPLETE_RESPONSE")]
    [InlineData("web_search_call", "function_call", "UNSUPPORTED_TOOL_CALLS")]
    [InlineData("output_text", "refusal", "RESPONSE_REFUSAL")]
    public async Task RecoverySearchRejectsIncompleteOrExecutableToolResponses(string from, string to, string code)
    {
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(),
            new Handler((_, _) => Task.FromResult(Reply(ResponsesReply.Replace(from, to)))));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("Return JSON", "image", Images, default, 2));
        Assert.Equal(code, error.Code);
        Assert.Equal(12, error.ResponseMetadata!.Usage.InputTokens);
    }

    private static string ResponsesOutput(string output, string status = "completed", object? incomplete = null, object? error = null) =>
        JsonSerializer.Serialize(new
        {
            status, output = JsonSerializer.Deserialize<JsonElement>(output), incomplete_details = incomplete, error,
            usage = new { input_tokens = 12, output_tokens = 41, output_tokens_details = new { reasoning_tokens = 30 } }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesCombinesOnlyFinalMessageTextPartsInWireOrder(bool streaming)
    {
        string response = ResponsesOutput("""[{"type":"reasoning","content":[{"type":"reasoning_text","text":"PRIVATE_REASONING_NOT_AN_ACTION"}]},{"type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"{\"an"},{"type":"output_text","text":""},{"type":"output_text","text":"swer\":true}"}]}]""");
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(streaming ? Streaming(Event("response.completed", JsonSerializer.Deserialize<JsonElement>(response))) : Reply(response))));
        var reply = await provider.SendImagesAsync("Return JSON", "image", Images, default, 2);
        Assert.Equal("{\"answer\":true}", reply.Content);
        Assert.Equal(new ProviderUsage(12, 41), reply.Usage);
        Assert.Equal(3, provider.LastOutputShape!.TextParts);
        Assert.Equal(1, provider.LastOutputShape.Messages);
    }

    [Theory]
    [InlineData("[]", "EMPTY_CONTENT")]
    [InlineData("[{\"type\":\"reasoning\",\"content\":[{\"type\":\"reasoning_text\",\"text\":\"PRIVATE_REASONING\"}]}]", "EMPTY_REASONING_ONLY")]
    [InlineData("[{\"type\":\"reasoning\"},{\"type\":\"web_search_call\",\"status\":\"completed\"}]", "EMPTY_SEARCH_ANSWER")]
    [InlineData("[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}]", "EMPTY_CONTENT")]
    [InlineData("[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"   \"}]}]", "EMPTY_CONTENT")]
    [InlineData("[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"reasoning_text\",\"text\":\"PRIVATE_REASONING\"}]}]", "INVALID_CONTENT_SHAPE")]
    [InlineData("[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"PRIVATE_REFUSAL\"}]}]", "RESPONSE_REFUSAL")]
    [InlineData("[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":null}]}]", "INVALID_CONTENT_SHAPE")]
    [InlineData("[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]},{\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}]", "INVALID_RESPONSE_SHAPE")]
    [InlineData("[{\"type\":\"function_call\",\"arguments\":\"PRIVATE_TOOL_ARGUMENTS\"}]", "UNSUPPORTED_TOOL_CALLS")]
    public async Task ResponsesEmptyOutputIsClassifiedWithoutPromotingReasoningRefusalOrTools(string output, string code)
    {
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(Reply(ResponsesOutput(output)))));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("Return JSON", "image", Images, default, 2));
        Assert.Equal(code, error.Code);
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.Equal("responses", metadata.Endpoint); Assert.Equal("search", metadata.RequestStage);
        Assert.Equal(2, metadata.RecoveryLevel); Assert.Equal(4096, metadata.RequestedOutputTokenLimit);
        Assert.Equal("completed", metadata.ResponseStatus); Assert.Equal(30, metadata.ReasoningTokens);
        Assert.Equal(new ProviderUsage(12, 41), metadata.Usage);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(metadata) + error);
    }

    [Theory]
    [InlineData("max_output_tokens", "OUTPUT_TOKEN_LIMIT")]
    [InlineData("content_filter", "CONTENT_FILTERED")]
    [InlineData("PRIVATE_REMOTE_REASON", "INCOMPLETE_RESPONSE")]
    public async Task ResponsesIncompleteReasonIsRetainedSafelyWithoutAcceptingPartialText(string reason, string code)
    {
        string body = ResponsesOutput("""[{"type":"reasoning","content":[{"type":"reasoning_text","text":"PRIVATE_REASONING"}]},{"type":"message","role":"assistant","status":"incomplete","content":[{"type":"output_text","text":"PRIVATE_PARTIAL"}]}]""", "incomplete", new { reason });
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(Streaming(Event("response.incomplete", JsonSerializer.Deserialize<JsonElement>(body))))));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("Return JSON", "image", Images, default, 2));
        Assert.Equal(code, error.Code);
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.Equal("incomplete", metadata.ResponseStatus); Assert.True(metadata.ReasoningPresent);
        Assert.Equal(reason.StartsWith("PRIVATE_", StringComparison.Ordinal) ? "other" : reason, metadata.IncompleteReason);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(metadata) + error);
    }

    [Fact]
    public async Task ResponsesFailureMetadataExcludesRemoteErrorMessageAndArbitraryCode()
    {
        string body = ResponsesOutput("[]", "failed", error: new { code = "PRIVATE_ERROR_CODE", message = "PRIVATE_ERROR_MESSAGE" });
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(Streaming(Event("response.failed", JsonSerializer.Deserialize<JsonElement>(body))))));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("Return JSON", "image", Images, default, 2));
        Assert.Equal("RESPONSE_FAILED", error.Code); Assert.Equal("other", error.ResponseMetadata!.RemoteErrorCode);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(error.ResponseMetadata) + error);
    }

    [Fact]
    public async Task EmptyTerminalDoesNotFallBackToEarlierStreamDeltas()
    {
        string delta = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"{\\\"answer\\\":true}\"}\n\n";
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(Streaming(delta + Event("response.completed", JsonSerializer.Deserialize<JsonElement>(ResponsesOutput("[]")))))));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("Return JSON", "image", Images, default, 2));
        Assert.Equal("EMPTY_CONTENT", error.Code);
    }

    [Fact]
    public async Task CombinedResponsePartsStillRespectUtf8ContentLimit()
    {
        var parts = new[] { new { type = "output_text", text = new string('测', 12000) }, new { type = "output_text", text = new string('测', 12000) } };
        string output = JsonSerializer.Serialize(new[] { new { type = "message", role = "assistant", status = "completed", content = parts } });
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(Reply(ResponsesOutput(output)))));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("Return JSON", "image", Images, default, 2));
        Assert.Equal("CONTENT_TOO_LARGE", error.Code);
    }

    [Fact]
    public async Task ChatReasoningOnlyReportsRealBudgetAndDoesNotUseReasoningAsFinalAnswer()
    {
        var profile = ProviderConfiguration.DefaultDeepSeek() with { Thinking = "disabled" };
        int calls = 0;
        using var provider = new ChatCompletionProvider(profile, new Secrets(), new Handler(async (http, ct) =>
        {
            calls++;
            using var wire = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal(profile.Model, wire.RootElement.GetProperty("model").GetString());
            Assert.Equal(4096, wire.RootElement.GetProperty("max_tokens").GetInt32());
            return Reply(EmptyResponse("null", usageJson: "{\"prompt_tokens\":12,\"completion_tokens\":41,\"completion_tokens_details\":{\"reasoning_tokens\":41}}",
                extraMessage: new() { ["reasoning_content"] = "PRIVATE_REASONING_JSON" }));
        }));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("Return JSON", "image", Images, default, 1));
        Assert.Equal("EMPTY_REASONING_ONLY", error.Code);
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.Equal("chat/completions", metadata.Endpoint); Assert.Equal("recovery", metadata.RequestStage);
        Assert.Equal(1, metadata.RecoveryLevel); Assert.Equal(4096, metadata.RequestedOutputTokenLimit);
        Assert.Equal(41, metadata.ReasoningTokens); Assert.Equal(1, calls); Assert.Equal("disabled", profile.Thinking);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(metadata) + error);
    }

    [Fact]
    public async Task LegacyFunctionCallAndRefusalCannotHideBesideNormalChatContent()
    {
        var legacy = await FailedResponse(EmptyResponse("\"{}\"", extraMessage: new() { ["function_call"] = new { name = "PRIVATE_TOOL", arguments = "{}" }, ["tool_calls"] = Array.Empty<object>() }), "UNSUPPORTED_TOOL_CALLS");
        Assert.Equal(1, legacy.ResponseMetadata!.ToolCallCount);
        await FailedResponse(EmptyResponse("\"{}\"", extraMessage: new() { ["refusal"] = "PRIVATE_REFUSAL" }), "RESPONSE_REFUSAL");
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
            Task.FromResult(Reply(EmptyResponse("\"{}\"", extraMessage: new() { ["refusal"] = "" })))));
        Assert.Equal("{}", (await provider.SendImagesAsync("JSON", "image", Images, default)).Content);
    }

    [Fact]
    public async Task RecoveryDoesNotSwitchCustomProviderOrItsThinkingSettings()
    {
        var profile = ProviderConfiguration.DefaultDeepSeek() with { BaseUrl = new("https://example.test/v1"), Thinking = "disabled" };
        using var provider = new ChatCompletionProvider(profile, new Secrets(), new Handler(async (http, ct) =>
        {
            Assert.Equal("https://example.test/v1/chat/completions", http.RequestUri!.AbsoluteUri);
            using var doc = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            Assert.Equal("disabled", doc.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(doc.RootElement.TryGetProperty("tools", out _));
            return Reply(ValidReply);
        }));
        await provider.SendImagesAsync("Return JSON", "image", Images, default, 2);
    }
    private static readonly ImmutableArray<FrameImage> Images = [new(10, 10, "image/png", [1, 2, 3])];
    private sealed class Secrets : ISecretStore
    {
        public Task<SecretValue?> ReadAsync(string secretRef, CancellationToken ct) => Task.FromResult<SecretValue?>(new("fixture-only-key"));
        public Task WriteAsync(string secretRef, SecretValue value, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string secretRef, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
    private static string ValidReply => """{"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":true}","reasoning_content":"never interpret this"}}],"usage":{"prompt_tokens":10,"completion_tokens":4}}""";

    [Fact]
    public async Task EachDecisionSuppliesANewConcreteProposalIdEvenForTheSameFrame()
    {
        var profile = ProviderConfiguration.DefaultDeepSeek();
        var ids = new List<string>();
        using var provider = new ChatCompletionProvider(profile, new Secrets(), new Handler(async (http, ct) =>
        {
            using var wire = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var messages = wire.RootElement.GetProperty("messages");
            using var context = JsonDocument.Parse(messages[1].GetProperty("content")[0].GetProperty("text").GetString()!);
            string id = context.RootElement.GetProperty("responseProposalId").GetString()!;
            Assert.True(Guid.TryParseExact(id, "N", out _));
            Assert.Contains("\"proposalId\":\"" + id + "\"", messages[0].GetProperty("content").GetString());
            Assert.DoesNotContain("new-unique-id-every-reply", messages[0].GetProperty("content").GetString());
            string prompt = messages[0].GetProperty("content").GetString()!;
            Assert.StartsWith("ASSISTED CORE MODE:", prompt);
            Assert.Contains("\"kind\":\"act_control\"", prompt);
            Assert.Contains("\"type\":\"text\"", prompt);
            Assert.Contains("\"type\":\"hotkey\"", prompt);
            Assert.DoesNotContain("use visual coordinates", prompt);
            foreach (string pointer in new[] { "move", "click", "scroll", "drag" })
                Assert.DoesNotContain("\"type\":\"" + pointer + "\"", prompt);
            ids.Add(id);
            return Reply(ValidReply);
        }));
        var lease = new Lease(Guid.NewGuid(), 0);
        var bounds = new PhysicalRect(0, 0, 10, 10);
        var frame = new Frame("frame", lease, DateTimeOffset.UtcNow, 1, "display", bounds, Images[0],
            FrameViewKind.Overview, new("1", 100, "fixture", bounds), []);
        var task = new ModelTaskSnapshot(lease, "Find a setting", TaskState.Running, ProviderConfiguration.Fingerprint(profile),
            "display", TaskBudget.Default, new(0, 0, 0, null, null));
        var request = new ModelRequest(task, frame, [], DesktopAgent.Core.Runtime.DesktopProtocolPrompt.V2Assisted,
            new(lease.TaskId, lease.Epoch, frame.Id, SchemaVersion: 2));
        await provider.DecideAsync(request, default);
        await provider.DecideAsync(request, default);
        Assert.Equal(2, ids.Count); Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task DecisionWireCarriesHistoricalReportsAsUntrustedDataWithCurrentFrameScopeUnchanged()
    {
        var profile = ProviderConfiguration.DefaultDeepSeek();
        var lease = new Lease(Guid.NewGuid(), 2);
        var bounds = new PhysicalRect(0, 0, 10, 10);
        var frame = new Frame("fresh-frame", lease, DateTimeOffset.UtcNow, 1, "display", bounds, Images[0], FrameViewKind.Overview, new("1", 100, "fixture", bounds), []);
        using var provider = new ChatCompletionProvider(profile, new Secrets(), new Handler(async (http, ct) =>
        {
            using var wire = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            using var context = JsonDocument.Parse(wire.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
            var report = Assert.Single(context.RootElement.GetProperty("untrustedModelObservations").EnumerateArray());
            Assert.Equal("historical-frame", report.GetProperty("frameId").GetString());
            Assert.Equal("reported-original", report.GetProperty("current").GetString());
            Assert.Equal("planned-restore", report.GetProperty("next").GetString());
            Assert.Equal("model_current_next_untrusted", report.GetProperty("source").GetString());
            Assert.False(report.GetProperty("verified").GetBoolean()); Assert.False(report.GetProperty("inputAuthority").GetBoolean());
            Assert.False(report.TryGetProperty("reasoning", out _));
            Assert.Equal("fresh-frame", context.RootElement.GetProperty("scope").GetProperty("frameId").GetString());
            return Reply(ValidReply);
        }));
        var task = new ModelTaskSnapshot(lease, "Find a setting", TaskState.Running, ProviderConfiguration.Fingerprint(profile), "display", TaskBudget.Default, new(0, 0, 0, null, null));
        await provider.DecideAsync(new(task, frame, [], DesktopAgent.Core.Runtime.DesktopProtocolPrompt.V2Assisted,
            new(lease.TaskId, lease.Epoch, frame.Id, SchemaVersion: 2), untrustedModelObservations:
            [new(new(lease.TaskId, 0), "historical-frame", "reported-original", "planned-restore")]), default);
    }

    private static string EmptyResponse(string? contentJson, object? finishReason = null, string? usageJson = null,
        Dictionary<string, object?>? extraMessage = null)
    {
        Dictionary<string, object?> message = extraMessage is null ? new() : new(extraMessage);
        if (contentJson is not null) message["content"] = JsonSerializer.Deserialize<JsonElement>(contentJson);
        return JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = finishReason ?? "stop", message } },
            usage = JsonSerializer.Deserialize<JsonElement>(usageJson ?? "null")
        });
    }

    private static async Task<ProviderCallException> FailedResponse(string response, string expected = "EMPTY_CONTENT")
    {
        int calls = 0;
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
        { calls++; return Task.FromResult(Reply(response)); }));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("JSON", "image", Images, default));
        Assert.Equal(expected, error.Code); Assert.Equal(1, calls);
        return error;
    }

    [Theory]
    [InlineData(null, "missing", null, null, "EMPTY_CONTENT")]
    [InlineData("null", "null", null, null, "EMPTY_CONTENT")]
    [InlineData("\"\"", "string", 0, true, "EMPTY_CONTENT")]
    [InlineData("\"   \"", "string", 3, true, "EMPTY_CONTENT")]
    [InlineData("[]", "array", null, null, "INVALID_CONTENT_SHAPE")]
    [InlineData("{\"text\":\"PRIVATE_BODY_TEXT\"}", "object", null, null, "INVALID_CONTENT_SHAPE")]
    [InlineData("17", "number", null, null, "INVALID_CONTENT_SHAPE")]
    [InlineData("false", "false", null, null, "INVALID_CONTENT_SHAPE")]
    public async Task EmptyContentMetadataContainsOnlyShapeAndCounts(string? contentJson, string kind, int? length, bool? whitespace, string code)
    {
        var error = await FailedResponse(EmptyResponse(contentJson), code);
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.Equal(200, metadata.HttpStatusCode); Assert.Equal(kind, metadata.ContentKind);
        Assert.Equal(length, metadata.ContentLength); Assert.Equal(whitespace, metadata.ContentIsWhiteSpace);
        Assert.Equal("stop", metadata.FinishReason); Assert.Null(metadata.Usage.InputTokens); Assert.Null(metadata.Usage.OutputTokens);
        string visible = JsonSerializer.Serialize(metadata) + error;
        Assert.DoesNotContain("PRIVATE_BODY_TEXT", visible); Assert.DoesNotContain("fixture-only-key", visible);
    }

    [Theory]
    [InlineData("reasoning_content")]
    [InlineData("reasoning")]
    public async Task ReasoningAndRefusalExposePresenceAndLengthButNeverTheirTextOrFallbackAnswer(string field)
    {
        const string privateReasoning = "PRIVATE_REASONING: {\"kind\":\"act\"}";
        const string privateRefusal = "PRIVATE_REFUSAL_TEXT";
        var message = new Dictionary<string, object?> { [field] = privateReasoning, ["refusal"] = privateRefusal };
        var error = await FailedResponse(EmptyResponse("\"\"", usageJson: "{\"prompt_tokens\":23,\"completion_tokens\":41}", extraMessage: message), "RESPONSE_REFUSAL");
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.True(metadata.ReasoningPresent); Assert.Equal(privateReasoning.Length, metadata.ReasoningLength);
        Assert.True(metadata.RefusalPresent); Assert.Equal(new ProviderUsage(23, 41), metadata.Usage);
        string visible = JsonSerializer.Serialize(metadata) + error;
        Assert.DoesNotContain(privateReasoning, visible); Assert.DoesNotContain("PRIVATE_REASONING", visible);
        Assert.DoesNotContain(privateRefusal, visible); Assert.DoesNotContain("fixture-only-key", visible);
    }

    [Fact]
    public async Task NullReasoningAndRefusalAreAbsentAndDoNotInventLengths()
    {
        var error = await FailedResponse(EmptyResponse("null", extraMessage: new() { ["reasoning_content"] = null, ["refusal"] = null }));
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.False(metadata.ReasoningPresent); Assert.Null(metadata.ReasoningLength); Assert.False(metadata.RefusalPresent);
    }

    [Theory]
    [InlineData("length", "OUTPUT_TOKEN_LIMIT")]
    [InlineData("tool_calls", "INCOMPLETE_RESPONSE")]
    [InlineData("content_filter", "CONTENT_FILTERED")]
    public async Task KnownFinishReasonIsPreservedWhileIncompleteResponsesStillFail(string reason, string code)
    {
        var error = await FailedResponse(EmptyResponse("\"PRIVATE_PARTIAL_CONTENT\"", reason), code);
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.Equal(reason, metadata.FinishReason); Assert.Equal(23, metadata.ContentLength); Assert.False(metadata.ContentIsWhiteSpace);
        Assert.DoesNotContain("PRIVATE_PARTIAL_CONTENT", JsonSerializer.Serialize(metadata) + error);
    }

    [Fact]
    public async Task ArbitraryFinishReasonCannotBeCopiedIntoMetadata()
    {
        string reason = "PRIVATE_REMOTE_REASON_" + new string('x', 500);
        var error = await FailedResponse(EmptyResponse("\"\"", reason), "INCOMPLETE_RESPONSE");
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.Equal("other", metadata.FinishReason);
        Assert.DoesNotContain("PRIVATE_REMOTE_REASON", JsonSerializer.Serialize(metadata) + error);
    }

    [Fact]
    public async Task NonStringFinishReasonKeepsShapeFailureAndSanitizedMetadata()
    {
        var error = await FailedResponse(EmptyResponse("null", new { privateValue = "PRIVATE_REMOTE_REASON" }), "INVALID_RESPONSE_SHAPE");
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.Equal("non_string", metadata.FinishReason);
        Assert.DoesNotContain("PRIVATE_REMOTE_REASON", JsonSerializer.Serialize(metadata) + error);
    }

    [Theory]
    [InlineData("{\"prompt_tokens\":5,\"completion_tokens\":9}", 5L, 9L)]
    [InlineData("{\"prompt_tokens\":0,\"completion_tokens\":0}", 0L, 0L)]
    [InlineData("{\"prompt_tokens\":5}", 5L, null)]
    [InlineData("{\"completion_tokens\":9}", null, 9L)]
    [InlineData("{\"prompt_tokens\":-1,\"completion_tokens\":9}", null, 9L)]
    [InlineData("{\"prompt_tokens\":\"5\",\"completion_tokens\":1.5}", null, null)]
    [InlineData("{\"prompt_tokens\":9223372036854775808,\"completion_tokens\":null}", null, null)]
    [InlineData("null", null, null)]
    [InlineData("[]", null, null)]
    public async Task ErrorUsagePreservesOnlyReliableNonnegativeIntegerCounts(string usage, long? input, long? output)
    {
        var error = await FailedResponse(EmptyResponse("\"\"", usageJson: usage));
        var metadata = Assert.IsType<ProviderResponseMetadata>(error.ResponseMetadata);
        Assert.Equal(new ProviderUsage(input, output), metadata.Usage);
    }

    [Fact]
    public async Task DuplicateResponseUsageIsNotTreatedAsReliableMetadata()
    {
        var response = """{"choices":[{"finish_reason":"stop","message":{"content":""}}],"usage":{"prompt_tokens":5,"prompt_tokens":900,"completion_tokens":9}}""";
        var error = await FailedResponse(response, "DUPLICATE_RESPONSE_FIELD");
        Assert.Null(error.ResponseMetadata);
    }

    [Theory]
    [InlineData(ProviderKind.DeepSeek, "https://api.deepseek.com", ImageEncoding.DataUrl)]
    [InlineData(ProviderKind.OpenAiCompatible, "https://configured.invalid/v1", ImageEncoding.DataUrl)]
    [InlineData(ProviderKind.Kimi, "https://api.moonshot.cn/v1", ImageEncoding.DataUrl)]
    [InlineData(ProviderKind.Glm, "https://open.bigmodel.cn/api/paas/v4", ImageEncoding.RawBase64)]
    public async Task ProfilesPreserveExactEndpointModelAndImageWireShape(ProviderKind kind, string baseUrl, ImageEncoding encoding)
    {
        var profile = ProviderConfiguration.DefaultDeepSeek() with
        { ProviderKind = kind, BaseUrl = new(baseUrl), ImageEncoding = encoding, JsonMode = kind == ProviderKind.DeepSeek };
        using var provider = new ChatCompletionProvider(profile, new Secrets(), new Handler(async (request, ct) =>
        {
            Assert.Equal(baseUrl + "/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture-only-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(profile.Model, json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            var content = json.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal(JsonValueKind.Array, content.ValueKind);
            Assert.Equal(encoding == ImageEncoding.DataUrl ? "data:image/png;base64,AQID" : "AQID",
                content[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal(kind == ProviderKind.DeepSeek, json.RootElement.TryGetProperty("response_format", out _));
            Assert.False(json.RootElement.TryGetProperty("tools", out _));
            Assert.False(json.RootElement.TryGetProperty("thinking", out _));
            return Reply(ValidReply);
        }));
        var result = await provider.SendImagesAsync("Return JSON.", "Read the synthetic image.", Images, default);
        Assert.Equal("{\"answer\":true}", result.Content);
        Assert.Equal(new ProviderUsage(10, 4), result.Usage);
    }

    [Theory]
    [InlineData(301, "REDIRECT_BLOCKED")]
    [InlineData(401, "AUTHENTICATION")]
    [InlineData(403, "PERMISSION")]
    [InlineData(404, "MODEL_OR_ENDPOINT_NOT_FOUND")]
    [InlineData(429, "RATE_LIMIT")]
    [InlineData(500, "SERVER_ERROR")]
    [InlineData(400, "REQUEST_OR_IMAGE_UNSUPPORTED")]
    public async Task FailureDoesNotRetryOrExposeRemoteBody(int status, string expected)
    {
        int calls = 0;
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler((_, _) =>
        {
            calls++;
            var response = Reply("fixture confidential remote body", (HttpStatusCode)status);
            response.Headers.Location = new Uri("https://other-origin.invalid");
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("JSON", "image", Images, default));
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("confidential", error.ToString());
        Assert.Null(error.ResponseMetadata);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{", "INVALID_RESPONSE_JSON")]
    [InlineData("{}", "INVALID_RESPONSE_SHAPE")]
    [InlineData("{\"choices\":[]}", "INVALID_CHOICES")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\"}]}", "OUTPUT_TOKEN_LIMIT")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"reasoning_content\":\"act\"}}]}", "EMPTY_REASONING_ONLY")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}", "EMPTY_CONTENT")]
    [InlineData("{\"choices\":[],\"choices\":[]}", "DUPLICATE_RESPONSE_FIELD")]
    public async Task InvalidResponsesCannotBecomeActions(string response, string expected)
    {
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(),
            new Handler((_, _) => Task.FromResult(Reply(response))));
        var error = await Assert.ThrowsAsync<ProviderCallException>(() => provider.SendImagesAsync("JSON", "image", Images, default));
        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public async Task ExternalCancellationReachesPendingHandlerAndRemainsCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new ChatCompletionProvider(ProviderConfiguration.DefaultDeepSeek(), new Secrets(), new Handler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(ValidReply);
        }));
        using var cancellation = new CancellationTokenSource();
        var pending = provider.SendImagesAsync("JSON", "image", Images, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void ConfigurationFingerprintChangesWithKeyReferenceAndModelAndRejectsCredentialUrls()
    {
        var original = ProviderConfiguration.DefaultDeepSeek();
        Assert.NotEqual(ProviderConfiguration.Fingerprint(original), ProviderConfiguration.Fingerprint(original with { SecretRef = Guid.NewGuid().ToString("N") }));
        Assert.NotEqual(ProviderConfiguration.Fingerprint(original), ProviderConfiguration.Fingerprint(original with { Model = "explicitly-selected-model" }));
        Assert.Throws<ArgumentException>(() => ProviderConfiguration.Endpoint(original with { BaseUrl = new("https://user:fixture-password@example.invalid") }));
        Assert.Throws<ArgumentException>(() => ProviderConfiguration.Endpoint(original with { SecretRef = "../outside" }));
    }
}
