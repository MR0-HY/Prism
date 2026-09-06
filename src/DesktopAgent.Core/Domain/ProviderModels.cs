using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace DesktopAgent.Core.Domain;

public enum ProviderKind { DeepSeek, OpenAiCompatible, Kimi, Glm }
public enum ImageEncoding { DataUrl, RawBase64 }
public enum CapabilityStatus { Documented, ProbePassed, Unsupported, Unknown }

public sealed record CapabilityRecord(string Name, CapabilityStatus Status, string? Source,
    DateTimeOffset? TestedAt, string ProfileFingerprint, string Notes);

/// <summary>Configuration metadata only. D04 validates endpoint/model capabilities and supplies the store.</summary>
public sealed record ProviderProfile(Guid Id, string Label, ProviderKind ProviderKind, Uri BaseUrl,
    string Model, string SecretRef, ImageEncoding ImageEncoding, int RequestTimeoutMs, int FrameTtlMs,
    string? Thinking, string? ReasoningEffort, bool JsonMode, ImmutableArray<CapabilityRecord> Capabilities,
    string? ProbeFingerprint);

[JsonConverter(typeof(SecretValueRedactedConverter))]
public sealed class SecretValue : IDisposable
{
    private char[]? _value;
    public SecretValue(ReadOnlySpan<char> value) => _value = value.ToArray();
    [JsonIgnore] public ReadOnlyMemory<char> Value => _value ?? throw new ObjectDisposedException(nameof(SecretValue));
    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value is not null) Array.Clear(value);
    }
    public override string ToString() => "SecretValue([redacted])";
}

internal sealed class SecretValueRedactedConverter : JsonConverter<SecretValue>
{
    public override SecretValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Secrets must come from the local secret store.");
    public override void Write(Utf8JsonWriter writer, SecretValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteEndObject();
    }
}

public sealed record ProviderUsage(long? InputTokens, long? OutputTokens);
public sealed record ProviderReply(string Content, ProviderUsage Usage)
{
    public string? SearchContinuation { get; init; }
    public override string ToString() => "ProviderReply(content omitted)";
}
public enum ProbeKind { ReadImage, GroundPoint }
// Ground truth is local verification data. It is never serialized into a model prompt.
public sealed record ProbeExpectation(ProbeKind Kind, string Code, string? OrangeSide, PhysicalRect? HitBox);
public sealed record GeneratedProbeImage(string Id, FrameImage Image,
    [property: JsonIgnore] ProbeExpectation? Expected = null, FrameImage? Overview = null);
public sealed record ProbeResult(string Name, CapabilityStatus Status, string PublicSummary);
/// <summary>Local synthetic-probe evidence only; never attach to a desktop task or model request.</summary>
public sealed record ProbeDiagnostic(int ImageWidth, int ImageHeight, string ExpectedCode, PhysicalRect? ExpectedHitBox)
{
    public string? SchemaIssue { get; init; }
    public string? ActualCode { get; init; }
    public bool? CodeMatched { get; init; }
    public int? NormalizedX { get; init; }
    public int? NormalizedY { get; init; }
    public double? MappedX { get; init; }
    public double? MappedY { get; init; }
    public bool? PointInsideHitBox { get; init; }
    public string? ExpectedControlId { get; init; }
    public string? ActualControlId { get; init; }
    public bool? ControlMatched { get; init; }
    public string? ExpectedOrangeSide { get; init; }
    public string? ActualOrangeSide { get; init; }
    public bool? OrangeSideMatched { get; init; }
    public string? FinalContent { get; init; }
    public bool FinalContentTruncated { get; init; }
}
public sealed record ProbeAttempt(string Id, ProbeKind Kind, bool SchemaValid, bool Passed, string? ErrorCode,
    long ElapsedMs, ProviderUsage Usage, ProbeDiagnostic? Diagnostic = null, string? PromptVariant = null);
/// <summary>Synthetic probe journal only. Before-send does not prove server receipt or billing.</summary>
public sealed record ProbeCheckpoint(int Index, string Id, string PromptVariant, DateTimeOffset RecordedAtUtc,
    ProbeAttempt? Attempt = null)
{
    public string Stage => Attempt is null ? "before-send" : "completed";
}
public sealed record ProbeReport(string ProfileFingerprint, ImmutableArray<ProbeResult> Results,
    int ApiAttempts, ProviderUsage Usage, DateTimeOffset TestedAtUtc,
    ImmutableArray<ProbeAttempt> Attempts = default, bool Cancelled = false, bool DiagnosticOnly = false);
