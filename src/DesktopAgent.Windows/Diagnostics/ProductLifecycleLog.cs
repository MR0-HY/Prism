using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DesktopAgent.Core.Runtime;
using DesktopAgent.Core.Providers;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Bounded technical diagnostics. Never records user/model text, images, credentials or exception messages.</summary>
internal sealed class ProductLifecycleLog
{
    private const long MaximumBytes = 256 * 1024;
    private readonly object _sync = new();
    private readonly string _directory;
    internal string FilePath { get; }
    internal static ProductLifecycleLog Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAgent", "lifecycle"));

    internal ProductLifecycleLog(string directory)
    {
        _directory = Path.GetFullPath(directory);
        FilePath = Path.Combine(_directory, $"DesktopAgent-{Environment.ProcessId}.jsonl");
    }

    internal void Startup(bool diagnostic) => Write(new { atUtc = DateTimeOffset.UtcNow, eventName = "startup",
        processId = Environment.ProcessId, version = typeof(App).Assembly.GetName().Version?.ToString(), diagnostic });
    internal void Exit(int code) => Write(new { atUtc = DateTimeOffset.UtcNow, eventName = "exit", processId = Environment.ProcessId, code });
    internal void Progress(AgentProgress p, bool mainVisible, bool hudVisible, bool mainMinimized = false, bool hudMinimized = false) => Write(new
    {
        atUtc = DateTimeOffset.UtcNow, eventName = "task_state", processId = Environment.ProcessId,
        taskId = p.Task.Id, epoch = p.Task.Epoch, state = p.Task.State.ToString(), p.Task.CleanupComplete, p.Task.HighRiskEnabled,
        code = TechnicalCode(p.Task.LastError?.Code), p.Task.Usage.ApiAttempts, p.Task.Usage.ActionsAttempted,
        pendingQuestion = p.Task.PendingQuestion is not null, pendingApproval = p.Task.PendingActionApproval is not null,
        mainVisible, hudVisible, mainMinimized, hudMinimized
    });
    internal void Fault(string stage, Exception exception) => Write(new
    {
        atUtc = DateTimeOffset.UtcNow, eventName = "exception", processId = Environment.ProcessId,
        stage = TechnicalCode(stage), exceptionType = exception.GetType().FullName, exception.HResult,
        provider = exception is ProviderCallException providerError ? ProviderDetails(providerError) : null,
        methods = new StackTrace(exception, false).GetFrames()?.Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType?.FullName?.StartsWith("DesktopAgent.", StringComparison.Ordinal) == true)
            .Take(12).Select(m => m!.DeclaringType!.FullName + "." + m.Name).ToArray() ?? []
    });
    internal void TechnicalEvent(string code) => Write(new
    { atUtc = DateTimeOffset.UtcNow, eventName = "lifecycle", processId = Environment.ProcessId, code = TechnicalCode(code) });

    private static string? TechnicalCode(string? code) => code is null ? null :
        code.Length is > 0 and <= 80 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? code : "UNCLASSIFIED";

    private static object ProviderDetails(ProviderCallException error)
    {
        var m = error.ResponseMetadata;
        return new { code = ProviderErrorCode(error.Code), m?.HttpStatusCode,
            endpoint = m?.Endpoint is "responses" or "chat/completions" ? m.Endpoint : "unknown",
            requestStage = ProviderValue(m?.RequestStage, ["interpretation", "search_continuation", "search", "recovery", "reconsideration", "decision"]),
            m?.RecoveryLevel, m?.RequestedOutputTokenLimit,
            status = ProviderValue(m?.ResponseStatus, ["completed", "incomplete", "failed", "in_progress", "missing", "null", "other", "non_string"]),
            finishReason = ProviderValue(m?.FinishReason, ["stop", "length", "tool_calls", "function_call", "content_filter", "insufficient_system_resource",
                "completed", "incomplete", "failed", "in_progress", "missing", "null", "other", "non_string"]),
            incompleteReason = ProviderValue(m?.IncompleteReason, ["max_output_tokens", "content_filter", "missing", "null", "other", "non_string"]),
            m?.ContentLength, m?.ReasoningLength, m?.ReasoningTokens,
            m?.MessageCount, m?.TextPartCount, m?.SearchCallCount, m?.ToolCallCount, m?.RefusalPresent,
            inputTokens = m?.Usage.InputTokens, outputTokens = m?.Usage.OutputTokens };
    }
    // Keep response metadata on explicit vocabularies from ChatCompletionProvider/ResponsesEventStream.
    // An arbitrary ASCII-only string can still be private text; character filtering is not sufficient here.
    private static string? ProviderValue(string? value, string[] allowed) => value is null ? null :
        allowed.Contains(value, StringComparer.Ordinal) ? value : "unknown";
    private static string ProviderErrorCode(string code) => code is
        "PROFILE_CHANGED" or "INVALID_RECOVERY_LEVEL" or "INVALID_SEARCH_REFERENCE" or "INVALID_REQUEST" or "MISSING_KEY" or "INVALID_KEY" or
        "REQUEST_TOO_LARGE" or "REDIRECT_BLOCKED" or "AUTHENTICATION" or "PERMISSION" or "MODEL_OR_ENDPOINT_NOT_FOUND" or "RATE_LIMIT" or
        "SERVER_ERROR" or "REQUEST_OR_IMAGE_UNSUPPORTED" or "HTTP_ERROR" or "RESPONSE_TOO_LARGE" or "RESPONSE_HEADERS_TIMEOUT" or
        "RESPONSE_TOTAL_TIMEOUT" or "TIMEOUT" or "NETWORK_ERROR" or "INVALID_RESPONSE_ENCODING" or "INVALID_RESPONSE_JSON" or "INVALID_RESPONSE_SHAPE" or
        "OUTPUT_TOKEN_LIMIT" or "CONTENT_FILTERED" or "RESPONSE_FAILED" or "INCOMPLETE_RESPONSE" or "RESPONSE_REFUSAL" or "UNSUPPORTED_TOOL_CALLS" or
        "INVALID_CONTENT_SHAPE" or "CONTENT_TOO_LARGE" or "INVALID_CHOICES" or "EMPTY_SEARCH_ANSWER" or "EMPTY_REASONING_ONLY" or "EMPTY_CONTENT" or
        "DUPLICATE_RESPONSE_FIELD" or "STREAM_EVENT_MISMATCH" or "STREAM_TERMINAL_MISMATCH" or "STREAM_SERVER_ERROR" or "RESPONSE_IDLE_TIMEOUT" or
        "INCOMPLETE_EVENT_STREAM" ? code : "UNCLASSIFIED";

    private void Write(object row)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_directory);
                if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) return;
                if (File.Exists(FilePath) && (File.GetAttributes(FilePath) & FileAttributes.ReparsePoint) != 0) return;
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaximumBytes)
                    File.Move(FilePath, FilePath + ".previous", true);
                // Only this component's named log files are retained; other application data is untouched.
                foreach (var old in new DirectoryInfo(_directory).EnumerateFiles("DesktopAgent-*.jsonl*")
                    .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0 && f.FullName != FilePath && f.FullName != FilePath + ".previous")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(14)) old.Delete();
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                JsonSerializer.Serialize(stream, row);
                stream.WriteByte((byte)'\n');
            }
        }
        catch { } // Diagnostics must never terminate the app or block input cancellation.
    }
}
