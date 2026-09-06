using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Providers;

namespace DesktopAgent.Windows.Diagnostics;

/// <summary>Explicit owned-window diagnostics only. Records the unchanged inner provider's calls; never chooses a provider or sends input.</summary>
internal sealed class RecordingModelProvider : IModelProvider, ITaskIntentProvider
{
    private readonly IModelProvider _inner;
    private readonly string _directory;
    private readonly int _expectedProcessId;
    private readonly ulong _expectedHwnd;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private int _apiAttempts;
    private bool _evidenceFaulted;
    private static readonly JsonSerializerOptions ContextOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public RecordingModelProvider(IModelProvider inner, string evidenceDir, int expectedProcessId, string expectedHwndHex)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (expectedProcessId <= 0 || !TryHwnd(expectedHwndHex, out _expectedHwnd) || _expectedHwnd == 0)
            throw new ArgumentException("INVALID_DIAGNOSTIC_WINDOW_IDENTITY");
        _inner = inner;
        _expectedProcessId = expectedProcessId;
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(evidenceDir));
        Directory.CreateDirectory(_directory);
        CheckDirectory();
    }

    public int ApiAttempts => Volatile.Read(ref _apiAttempts);

    public Task<ProviderReply> DecideAsync(ModelRequest request, CancellationToken ct)
        => CallAsync(request, ct, readOnlyStage: false);

    public Task<ProviderReply> InterpretAsync(ModelRequest request, CancellationToken ct)
        => CallAsync(request, ct, readOnlyStage: true);

    private async Task<ProviderReply> CallAsync(ModelRequest request, CancellationToken ct, bool readOnlyStage)
    {
        ArgumentNullException.ThrowIfNull(request);
        var intentProvider = _inner as ITaskIntentProvider;
        if (readOnlyStage && intentProvider is null)
            throw new ProviderCallException("TASK_INTERPRETATION_UNSUPPORTED");
        await _serial.WaitAsync(ct);
        try
        {
            if (_evidenceFaulted) throw new ProviderCallException("DIAGNOSTIC_EVIDENCE_FAILED");
            EnsureOwned(request.CurrentFrame);
            if (request.OverviewContext is { } overview) EnsureOwned(overview);
            ct.ThrowIfCancellationRequested();
            int index = checked(ApiAttempts + 1);
            string prefix = $"request-{index:D3}";
            string currentName = prefix + "-current." + Extension(request.CurrentFrame.Image);
            string? overviewName = request.OverviewContext is null ? null : prefix + "-overview." + Extension(request.OverviewContext.Image);
            // Record the submitted context. The inner provider owns the interpretation preset and
            // adds the per-request proposal ID only for decisions. No fixture truth enters either call.
            string modelStage = readOnlyStage ? "interpretation" : "decision";
            string userContext = JsonSerializer.Serialize(new
            {
                request.Task, request.Scope, currentFrame = request.CurrentFrame,
                overviewContext = request.OverviewContext, request.RecentResults, request.Controls,
                request.UntrustedModelObservations, request.RecoveryLevel, request.Reconsidering
            }, ContextOptions);
            try
            {
                CheckDirectory();
                if (Directory.EnumerateFileSystemEntries(_directory, prefix + "-*").Any())
                    throw new IOException("DIAGNOSTIC_REQUEST_EVIDENCE_ALREADY_EXISTS");
                await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(_directory, currentName), request.CurrentFrame.Image.Bytes.ToArray(), ct);
                if (request.OverviewContext is { } contextFrame)
                    await HybridDiagnosticEvidence.WriteBytesAsync(Path.Combine(_directory, overviewName!), contextFrame.Image.Bytes.ToArray(), ct);
                await HybridDiagnosticEvidence.WriteAsync(Path.Combine(_directory, prefix + "-before-send.json"), new
                {
                    schemaVersion = 1, index, stage = "before-send", modelStage, atUtc = DateTimeOffset.UtcNow,
                    evidenceMode = "EXPLICIT_OWNED_WINDOW_MODEL_CALL", currentImage = currentName, overviewImage = overviewName,
                    systemPrompt = readOnlyStage ? null : request.ProtocolPrompt,
                    promptSource = readOnlyStage ? "provider-owned-task-interpretation-preset" : "request-protocol", userContext,
                    note = "This checkpoint does not prove server receipt or billing."
                }, ct);
            }
            catch
            {
                _evidenceFaulted = true;
                throw;
            }
            ct.ThrowIfCancellationRequested();
            ProviderReply? reply = null;
            ProviderResponseMetadata? responseMetadata = null;
            string? errorCode = null;
            bool cancelled = false;
            var timer = Stopwatch.StartNew();
            Interlocked.Increment(ref _apiAttempts);
            try
            {
                reply = readOnlyStage ? await intentProvider!.InterpretAsync(request, ct) : await _inner.DecideAsync(request, ct);
                // Keep a late reply as evidence, but do not return it to a cancelled task.
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                errorCode = "CANCELLED";
                throw;
            }
            catch (ProviderCallException error)
            {
                errorCode = SafeCode(error.Code);
                responseMetadata = error.ResponseMetadata;
                throw;
            }
            catch
            {
                errorCode = "INNER_PROVIDER_FAILURE";
                throw;
            }
            finally
            {
                timer.Stop();
                string? finalContent = reply is null ? null : BoundedFinalContent(reply.Content);
                try
                {
                    CheckDirectory();
                    await HybridDiagnosticEvidence.WriteAsync(Path.Combine(_directory, prefix + "-completed.json"), new
                    {
                        schemaVersion = 1, index, stage = "completed", modelStage, atUtc = DateTimeOffset.UtcNow,
                        elapsedMs = timer.ElapsedMilliseconds, cancelled = cancelled || ct.IsCancellationRequested,
                        errorCode, finalContent, finalContentTruncated = reply is not null && finalContent!.Length < reply.Content.Length,
                        usage = reply?.Usage ?? responseMetadata?.Usage ?? new ProviderUsage(null, null), responseMetadata, apiAttempts = ApiAttempts
                    }, CancellationToken.None);
                }
                catch
                {
                    _evidenceFaulted = true;
                    throw new ProviderCallException("DIAGNOSTIC_EVIDENCE_FAILED");
                }
            }
            ct.ThrowIfCancellationRequested();
            return reply ?? throw new ProviderCallException("EMPTY_REPLY");
        }
        finally { _serial.Release(); }
    }

    public Task<ProbeReport> ProbeAsync(ProviderProfile profile, ImmutableArray<GeneratedProbeImage> images, CancellationToken ct)
        => throw new NotSupportedException("RecordingModelProvider is only for explicit owned-window task diagnostics.");

    private void EnsureOwned(Frame frame)
    {
        if (frame.Foreground.ProcessId != _expectedProcessId || !TryHwnd(frame.Foreground.HwndHex, out ulong hwnd) || hwnd != _expectedHwnd ||
            !frame.Foreground.WindowRect.Contains(frame.PhysicalRegion))
            throw new ProviderCallException("DIAGNOSTIC_FRAME_OUTSIDE_OWNED_WINDOW");
    }

    private void CheckDirectory()
    {
        for (var directory = new DirectoryInfo(_directory); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ProviderCallException("DIAGNOSTIC_EVIDENCE_REPARSE_POINT");
    }

    private static bool TryHwnd(string? text, out ulong hwnd)
    {
        hwnd = 0;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32) return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hwnd);
    }

    private static string Extension(FrameImage image) => image.Mime == "image/png" ? "png" : "jpg";
    private static string SafeCode(string code) => code.Length is > 0 and <= 100 &&
        code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? code : "PROVIDER_CALL_FAILED";

    private static string BoundedFinalContent(string content)
    {
        const int maximum = 65536;
        int bytes = 0, characters = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximum || characters + rune.Utf16SequenceLength > maximum) break;
            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }
        return content[..characters];
    }
}
