using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Protocol;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Windows.Configuration;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit, bounded public-language wire diagnostic. No desktop observation, windows, hotkeys or input.</summary>
internal static class IntentDiagnostic
{
    private const string Scope = "INTENT_AND_FIRST_PROPOSAL_ONLY_NO_DESKTOP_ACCEPTANCE";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private sealed record Fixture(string Id, string Goal);
    private sealed record StageResult(string Stage, long ElapsedMs, bool FormatValid, string? Error,
        ProviderUsage? Usage, string? PublicOutputJson, string? DecisionType = null, bool? EligibleGlobalNavigationShape = null);

    internal static async Task<int> RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        // Do not overwrite interruption evidence or silently replay requests from a previous run.
        if (Directory.EnumerateFileSystemEntries(directory).Any()) return 2;
        var total = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var results = new List<object>();
        int requestCount = 0;
        bool formatValidAll = true;
        string? topError = null;
        string? model = null;
        await WriteAsync(Path.Combine(directory, "before.json"), new
        {
            atUtc = DateTimeOffset.UtcNow, scope = Scope, deadlineSeconds = 150, plannedMaximumRequests = 6,
            automaticRetries = 0, syntheticImagesOnly = true, desktopCapture = false, desktopInput = false,
            windowsShown = false, hotkeysRegistered = false, state = "STARTED"
        });
        try
        {
            var store = new LocalConfigurationStore(LocalConfigurationStore.DefaultRoot);
            var profile = (await store.ReadAsync(deadline.Token)).Single(p => p.ProviderKind == ProviderKind.DeepSeek &&
                p.BaseUrl.AbsoluteUri.TrimEnd('/') == "https://api.deepseek.com" && p.Model == "deepseek-v4-flash-vision-exp");
            model = profile.Model;
            using var provider = new ChatCompletionProvider(profile, store);
            FrameImage image = CreateSyntheticDesktop();
            await File.WriteAllBytesAsync(Path.Combine(directory, "synthetic-context.png"), image.Bytes.ToArray(), deadline.Token);
            Fixture[] fixtures =
            [
                new("system-appearance", "打开设置把系统从黑的变成白的"),
                new("random-calculator", "打开计算器抽取两个随机数帮我相乘"),
                new("notepad-wrap", "记事本一行太长了，帮我让它自动折到下一行")
            ];
            foreach (var fixture in fixtures)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string caseDirectory = Path.Combine(directory, fixture.Id);
                Directory.CreateDirectory(caseDirectory);
                var lease = new Lease(Guid.NewGuid(), 0);
                var stages = new List<StageResult>();
                TaskInterpretation? interpretation = null;
                ProviderUsage? interpretationUsage = null;
                var task = new ModelTaskSnapshot(lease, fixture.Goal, TaskState.Running,
                    ProviderConfiguration.Fingerprint(profile), "synthetic-monitor", TaskBudget.Default,
                    new TaskUsage(0, 0, 0, 0, 0));
                for (int stage = 0; stage < 2; stage++)
                {
                    if (stage == 1 && interpretation is null) break;
                    deadline.Token.ThrowIfCancellationRequested();
                    string stageName = stage == 0 ? "interpretation" : "first-proposal";
                    if (stage == 1)
                        task = task with { Interpretation = interpretation,
                            Usage = new TaskUsage(0, 1, stages[0].ElapsedMs, interpretationUsage?.InputTokens, interpretationUsage?.OutputTokens) };
                    var frame = CreateFrame(lease, image);
                    var scope = new ProposalScope(lease.TaskId, lease.Epoch, frame.Id, SchemaVersion: 2);
                    const string diagnosticContext = "\nDRY-RUN PROPOSAL TEST: This is an explicitly authorized simulation using generated Explorer pixels, not an actual desktop. Your task in this test is to PROPOSE the hypothetical next action for the depicted starting state using the ordinary protocol; no input will be executed. An act JSON object is an expected test output and does not itself perform an action. Do not refuse merely because the fixture is synthetic or ask for a real screenshot. No application task has been completed. Controls are unavailable in this starting fixture. Only global navigation WIN, WIN+I, WIN+E and ALT+TAB is eligible as input; coordinate input remains disabled. Retain every other permission and safety rule.";
                    var request = new ModelRequest(task, frame, [], DesktopProtocolPrompt.V2Assisted + diagnosticContext,
                        scope, controls: ControlSnapshot.Empty(frame, ControlSnapshotStatus.Unavailable));
                    await WriteAsync(Path.Combine(caseDirectory, stageName + "-before.json"), new
                    {
                        atUtc = DateTimeOffset.UtcNow, fixture.Goal, profile.Model, stage = stageName,
                        attempt = 1, totalAttempt = requestCount + 1, originalTaskId = lease.TaskId,
                        frameId = frame.Id, scope = Scope, syntheticImagesOnly = true, desktopInput = false
                    });
                    requestCount++;
                    var clock = Stopwatch.StartNew();
                    ProviderUsage? usage = null;
                    string? output = null, error = null, decisionType = null;
                    bool valid = false;
                    bool? navigation = null;
                    try
                    {
                        ProviderReply reply = stage == 0
                            ? await provider.InterpretAsync(request, deadline.Token)
                            : await provider.DecideAsync(request, deadline.Token);
                        usage = reply.Usage;
                        // This entry point accepts only the fixed public fixture goals and invented pixels.
                        // Final output can be retained; raw reasoning/HTTP bodies and secrets cannot.
                        output = reply.Content;
                        if (stage == 0)
                        {
                            interpretation = TaskInterpretation.Parse(reply.Content);
                            interpretationUsage = usage;
                        }
                        else
                        {
                            var proposal = ProposalParser.Parse(reply.Content, scope);
                            decisionType = proposal.Decision.GetType().Name;
                            navigation = IsGlobalNavigation(proposal.Decision);
                        }
                        valid = true;
                    }
                    catch (ProviderCallException failure) { error = failure.Code; usage = failure.ResponseMetadata?.Usage; }
                    catch (TaskInterpretationException failure) { error = failure.Code; }
                    catch (ProtocolValidationException failure) { error = failure.Code; }
                    catch (OperationCanceledException) { error = "DEADLINE"; }
                    catch (Exception) { error = "DIAGNOSTIC_STAGE_FAILED"; }
                    var result = new StageResult(stageName, clock.ElapsedMilliseconds, valid, error, usage, output, decisionType, navigation);
                    stages.Add(result);
                    formatValidAll &= valid;
                    await WriteAsync(Path.Combine(caseDirectory, stageName + "-completed.json"), new
                    {
                        atUtc = DateTimeOffset.UtcNow, fixture.Goal, profile.Model, result,
                        transport = provider.LastTransport, outputShape = provider.LastOutputShape,
                        scope = Scope, semanticAssessment = "MANUAL_REVIEW_REQUIRED", desktopInput = false
                    });
                }
                results.Add(new { fixture.Id, fixture.Goal, interpretation, stages, semanticAssessment = "MANUAL_REVIEW_REQUIRED" });
            }
        }
        catch (OperationCanceledException) { topError = "DEADLINE"; formatValidAll = false; }
        catch (ProviderCallException failure) { topError = failure.Code; formatValidAll = false; }
        catch (Exception) { topError = "DIAGNOSTIC_SETUP_FAILED"; formatValidAll = false; }
        await WriteAsync(Path.Combine(directory, "completed.json"), new
        {
            atUtc = DateTimeOffset.UtcNow, model, elapsedMs = total.ElapsedMilliseconds,
            requestCount, automaticRetries = 0, formatValidAll, error = topError, results,
            scope = Scope, semanticAssessment = "MANUAL_REVIEW_REQUIRED",
            syntheticImagesOnly = true, desktopCapture = false, desktopInput = false,
            windowsShown = false, hotkeysRegistered = false
        });
        return formatValidAll ? 0 : 1;
    }

    private static Frame CreateFrame(Lease lease, FrameImage image)
    {
        var bounds = new PhysicalRect(0, 0, image.Width, image.Height);
        return new Frame("synthetic-" + Guid.NewGuid().ToString("N"), lease, DateTimeOffset.UtcNow, 1,
            "synthetic-monitor", bounds, image, FrameViewKind.Overview,
            new ForegroundIdentity("0x1", 1, "explorer", bounds), []);
    }

    private static bool IsGlobalNavigation(Decision decision)
    {
        if (decision is not ActDecision { Action: HotkeyAction action }) return false;
        return action.Keys.SequenceEqual([AgentKey.WIN]) || action.Keys.SequenceEqual([AgentKey.WIN, AgentKey.I]) ||
            action.Keys.SequenceEqual([AgentKey.WIN, AgentKey.E]) || action.Keys.SequenceEqual([AgentKey.ALT, AgentKey.TAB]);
    }

    private static Task WriteAsync(string path, object value) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));

    private static FrameImage CreateSyntheticDesktop()
    {
        const int width = 960, height = 540;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(Brushes.LightGray, null, new Rect(0, 0, width, 42));
            DrawText(dc, "文件资源管理器 — 此电脑", 22, 10, 20);
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(185, 95, 745, 380));
            DrawText(dc, "主页", 30, 115, 18);
            DrawText(dc, "此电脑", 30, 155, 18);
            DrawText(dc, "设备和驱动器", 210, 120, 18);
            dc.DrawRectangle(Brushes.Gainsboro, null, new Rect(0, 500, width, 40));
            DrawText(dc, "开始", 20, 507, 18);
            DrawText(dc, "合成诊断画面 · 未捕获真实桌面", 530, 507, 16);
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new FrameImage(width, height, "image/png", stream.ToArray());
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size) =>
        dc.DrawText(new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, Brushes.Black, 1), new Point(x, y));
}
