using System.Net;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Providers;

public sealed class StructuredTaskPlanProviderTests
{
    [Fact]
    public async Task OneExistingIntentRequestProducesThePlanAndTheNextActionReceivesItWithWindows11Context()
    {
        const string plan = """{"goal":"创建 325.txt 并写入 325，保存后关闭","reply":"我会创建并保存文件，然后核对结果。","steps":[{"id":"s1","title":"创建目标文件","completionCheck":"目标文件名为 325.txt"},{"id":"s2","title":"写入并保存关闭","completionCheck":"正文为 325，文件已保存，目标编辑器已关闭"}],"completionCheck":"指定文件名与内容均正确，已保存并关闭"}""";
        var profile = ProviderConfiguration.DefaultDeepSeek() with { Thinking = "disabled" };
        int calls = 0;
        using var provider = new ChatCompletionProvider(profile, new Secrets(), new Handler(async (http, ct) =>
        {
            calls++;
            Assert.Equal("https://api.deepseek.com/chat/completions", http.RequestUri!.AbsoluteUri);
            using var wire = JsonDocument.Parse(await http.Content!.ReadAsStringAsync(ct));
            var body = wire.RootElement;
            Assert.Equal("deepseek-v4-flash-vision-exp", body.GetProperty("model").GetString());
            Assert.False(body.TryGetProperty("tools", out _));
            using var context = JsonDocument.Parse(body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.Equal("Windows 11", context.RootElement.GetProperty("environment").GetProperty("operatingSystem").GetString());
            if (calls == 1)
            {
                Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
                Assert.Equal("low", body.GetProperty("reasoning_effort").GetString());
                Assert.Equal(2048, body.GetProperty("max_tokens").GetInt32());
            }
            else
            {
                Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
                var interpretation = context.RootElement.GetProperty("task").GetProperty("interpretation");
                Assert.Equal(2, interpretation.GetProperty("steps").GetArrayLength());
                Assert.Equal("s2", interpretation.GetProperty("steps")[1].GetProperty("id").GetString());
                Assert.Contains("保存", interpretation.GetProperty("completionCheck").GetString());
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { finish_reason = "stop", message = new { content = plan } } }, usage = new { prompt_tokens = 10, completion_tokens = 20 } })) };
        }));
        var lease = new Lease(Guid.NewGuid(), 0);
        var bounds = new PhysicalRect(0, 0, 10, 10);
        var frame = new Frame("plan-frame", lease, DateTimeOffset.UtcNow, 1, "display", bounds, new(10, 10, "image/png", [1]),
            FrameViewKind.Overview, new("1", 100, "fixture", bounds), []);
        var task = new ModelTaskSnapshot(lease, "创建325.txt写入325保存退出", TaskState.Running,
            ProviderConfiguration.Fingerprint(profile), "display", TaskBudget.Default, new(0, 0, 0, null, null));
        ModelRequest Request(ModelTaskSnapshot snapshot) => new(snapshot, frame, [], DesktopProtocolPrompt.V2Assisted, new(lease.TaskId, lease.Epoch, frame.Id, SchemaVersion: 2));
        var reply = await provider.InterpretAsync(Request(task), default);
        var interpretation = TaskInterpretation.Parse(reply.Content);
        Assert.Equal(1, calls); Assert.Equal(2, interpretation.Steps.Length);
        await provider.DecideAsync(Request(task with { Interpretation = interpretation }), default);
        Assert.Equal(2, calls); Assert.Equal("disabled", profile.Thinking); Assert.Null(task.Interpretation);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct); }
    private sealed class Secrets : ISecretStore
    {
        public Task<SecretValue?> ReadAsync(string secretRef, CancellationToken ct) => Task.FromResult<SecretValue?>(new("fixture-only-key"));
        public Task WriteAsync(string secretRef, SecretValue value, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string secretRef, CancellationToken ct) => throw new NotSupportedException();
    }
}
