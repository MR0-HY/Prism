using System.Collections.Immutable;
using System.Text.Json;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Providers;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Core.Tests.Providers;

public sealed class HybridVisualProbeTests
{
    private const string ExpectedCode = "K9R2VC";
    private const string ExpectedControl = "c1";

    private static (Frame Frame, ControlSnapshot Controls) Scene()
    {
        var region = new PhysicalRect(-900, 120, 1000, 720);
        var frame = new Frame("hybrid-frame-1", new(Guid.NewGuid(), 7), DateTimeOffset.UtcNow, 1, "display",
            region, new(1000, 720, "image/png", [1, 2, 3]), FrameViewKind.Overview,
            new("1", 100, "SyntheticFixture", region), []);
        var controls = new ControlSnapshot(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow,
            ControlSnapshotStatus.Available,
            [new("c1", null, "Option Alpha", "Button", new(-650, 300, 80, 28), true, false, true, null, null),
             new("c2", null, "Option Beta", "CheckBox", new(-650, 345, 80, 28), true, false, true, "off", null),
             new("c3", null, "Option Gamma", "Button", new(-650, 390, 80, 28), false, false, true, null, null)]);
        return (frame, controls);
    }

    private static Dictionary<string, object?> Fields(Frame frame, ControlSnapshot controls) => new()
    {
        ["frameId"] = frame.Id, ["type"] = "control", ["code"] = ExpectedCode,
        ["snapshotId"] = controls.Id, ["controlId"] = ExpectedControl
    };

    private static string Answer(Frame frame, ControlSnapshot controls, string? field = null, object? value = null)
    {
        var fields = Fields(frame, controls);
        if (field is not null) fields[field] = value;
        return JsonSerializer.Serialize(fields);
    }

    private static ProbeAttempt Completed(string id) => new(id, ProbeKind.GroundPoint, true, true, null, 12,
        new(10, 4), new(1000, 720, ExpectedCode, new(250, 180, 80, 28))
        {
            ActualCode = ExpectedCode, CodeMatched = true, ExpectedControlId = ExpectedControl,
            ActualControlId = ExpectedControl, ControlMatched = true, MappedX = 290, MappedY = 194, PointInsideHitBox = true
        }, HybridVisualProbe.Variant);

    private static void AssertNotFullyPassed(ProbeReport report)
        => Assert.Contains(report.Results, r => r.Status != CapabilityStatus.ProbePassed);

    [Theory]
    [InlineData(ControlSnapshotStatus.Available)]
    [InlineData(ControlSnapshotStatus.Partial)]
    public void RequestCarriesActualFirstImageAndCurrentCandidatesWithoutLocalTruth(ControlSnapshotStatus status)
    {
        var (frame, snapshot) = Scene();
        snapshot = snapshot with { Status = status };
        var request = HybridVisualProbe.CreateRequest(frame, snapshot);
        Assert.Same(frame.Image, Assert.Single(request.Images));
        Assert.DoesNotContain(ExpectedCode, request.SystemPrompt + request.UserPrompt);
        Assert.DoesNotContain("expectedControlId", request.UserPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expectedHitBox", request.UserPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pointInsideHitBox", request.UserPrompt, StringComparison.OrdinalIgnoreCase);
        using var metadata = JsonDocument.Parse(request.UserPrompt[(request.UserPrompt.LastIndexOf('\n') + 1)..]);
        var root = metadata.RootElement;
        Assert.Equal(frame.Id, root.GetProperty("frameId").GetString());
        Assert.Equal(frame.Image.Width, root.GetProperty("width").GetInt32());
        Assert.Equal(frame.Image.Height, root.GetProperty("height").GetInt32());
        Assert.Equal(frame.PhysicalRegion.Left, root.GetProperty("physicalRegion").GetProperty("left").GetInt32());
        var controls = root.GetProperty("controls");
        Assert.Equal(snapshot.Id, controls.GetProperty("id").GetString());
        Assert.Equal(frame.Id, controls.GetProperty("frameId").GetString());
        var candidates = controls.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(snapshot.Candidates.Length, candidates.Length);
        for (int i = 0; i < candidates.Length; i++)
        {
            Assert.Equal(snapshot.Candidates[i].Id, candidates[i].GetProperty("id").GetString());
            Assert.Equal(snapshot.Candidates[i].Name, candidates[i].GetProperty("name").GetString());
            Assert.Equal(snapshot.Candidates[i].Bounds.Left, candidates[i].GetProperty("bounds").GetProperty("left").GetInt32());
        }
    }

    [Theory]
    [InlineData(ControlSnapshotStatus.Available)]
    [InlineData(ControlSnapshotStatus.Partial)]
    [InlineData(ControlSnapshotStatus.Unavailable)]
    [InlineData(ControlSnapshotStatus.TimedOut)]
    [InlineData(ControlSnapshotStatus.Disabled)]
    public void RequestWithoutCandidatesFailsBeforeTransport(ControlSnapshotStatus status)
    {
        var (frame, controls) = Scene();
        Assert.Throws<ArgumentException>(() => HybridVisualProbe.CreateRequest(frame, controls with { Status = status, Candidates = [] }));
    }

    [Theory]
    [InlineData("frame")]
    [InlineData("lease")]
    [InlineData("outside")]
    public void RequestRejectsCandidatesFromAnotherObservation(string mismatch)
    {
        var (frame, controls) = Scene();
        controls = mismatch switch
        {
            "frame" => controls with { FrameId = "older-frame" },
            "lease" => controls with { Lease = new(Guid.NewGuid(), frame.Lease.Epoch) },
            _ => controls with { Candidates = [controls.Candidates[0] with { Bounds = new(5000, 5000, 80, 28) }] }
        };
        Assert.Throws<ArgumentException>(() => HybridVisualProbe.CreateRequest(frame, controls));
    }

    [Fact]
    public void CorrectVisualSelectionDoesNotClaimPhysicalResolution()
    {
        var (frame, controls) = Scene();
        var verdict = HybridVisualProbe.Evaluate(frame, controls, ExpectedCode, ExpectedControl, Answer(frame, controls));
        Assert.True(verdict.SchemaValid); Assert.True(verdict.Passed);
        Assert.True(verdict.Diagnostic.CodeMatched); Assert.True(verdict.Diagnostic.ControlMatched);
        Assert.Equal(ExpectedCode, verdict.Diagnostic.ActualCode);
        Assert.Equal(ExpectedControl, verdict.Diagnostic.ActualControlId);
        Assert.Equal(new PhysicalRect(250, 180, 80, 28), verdict.Diagnostic.ExpectedHitBox);
        Assert.Null(verdict.Diagnostic.PointInsideHitBox);
        Assert.Null(verdict.Diagnostic.MappedX); Assert.Null(verdict.Diagnostic.MappedY);
        Assert.Null(verdict.Diagnostic.NormalizedX); Assert.Null(verdict.Diagnostic.NormalizedY);
        var attempts = new[] { "first", "second" }.Select(id => new ProbeAttempt(id, ProbeKind.GroundPoint,
            verdict.SchemaValid, verdict.Passed, null, 1, new(10, 4), verdict.Diagnostic, HybridVisualProbe.Variant)).ToImmutableArray();
        var report = HybridVisualProbe.BuildReport(ProviderConfiguration.DefaultDeepSeek(), attempts, false);
        Assert.Equal(CapabilityStatus.Unknown, report.Results.Single(r => r.Name == "hybrid_grounding").Status);
        AssertNotFullyPassed(report);
    }

    [Theory]
    [InlineData("frameId", "older-frame", "FRAME_ID_MISMATCH")]
    [InlineData("snapshotId", "older-snapshot", "SNAPSHOT_ID_MISMATCH")]
    [InlineData("type", "point", "TYPE_MISMATCH")]
    [InlineData("controlId", "unknown", "CONTROL_ID_UNAVAILABLE")]
    [InlineData("controlId", "c3", "CONTROL_ID_UNAVAILABLE")]
    [InlineData("code", "SHORT", "CODE_LENGTH")]
    public void MismatchedIdentityUnavailableControlAndInvalidCodeFail(string field, string value, string issue)
    {
        var (frame, controls) = Scene();
        var verdict = HybridVisualProbe.Evaluate(frame, controls, ExpectedCode, ExpectedControl, Answer(frame, controls, field, value));
        Assert.False(verdict.SchemaValid); Assert.False(verdict.Passed);
        Assert.Equal(issue, verdict.Diagnostic.SchemaIssue); Assert.Null(verdict.Diagnostic.PointInsideHitBox);
    }

    [Theory]
    [InlineData("frameId")]
    [InlineData("type")]
    [InlineData("code")]
    [InlineData("snapshotId")]
    [InlineData("controlId")]
    public void AllFiveFieldsMustBeStrings(string field)
    {
        var (frame, controls) = Scene();
        foreach (object? value in new object?[] { null, 1, true, new[] { "text" } })
        {
            var verdict = HybridVisualProbe.Evaluate(frame, controls, ExpectedCode, ExpectedControl, Answer(frame, controls, field, value));
            Assert.False(verdict.SchemaValid); Assert.False(verdict.Passed);
            Assert.Equal("FIELD_NOT_STRING", verdict.Diagnostic.SchemaIssue);
        }
    }

    [Theory]
    [InlineData("duplicate", "DUPLICATE_FIELD")]
    [InlineData("extra", "FIELD_SET")]
    [InlineData("missing", "FIELD_SET")]
    [InlineData("case", "FIELD_SET")]
    [InlineData("array", "ROOT_NOT_OBJECT")]
    [InlineData("malformed", "JSON_INVALID")]
    [InlineData("trailing", "JSON_INVALID")]
    public void OnlyOneExactFiveFieldObjectIsAccepted(string problem, string issue)
    {
        var (frame, controls) = Scene();
        string correct = Answer(frame, controls);
        var fields = Fields(frame, controls);
        fields.Remove("code");
        string content = problem switch
        {
            "duplicate" => correct[..^1] + ",\"controlId\":\"c1\"}",
            "extra" => Answer(frame, controls, "x", 250),
            "missing" => JsonSerializer.Serialize(fields),
            "case" => correct.Replace("\"code\"", "\"Code\"", StringComparison.Ordinal),
            "array" => "[" + correct + "]",
            "malformed" => "{",
            _ => correct + correct
        };
        var verdict = HybridVisualProbe.Evaluate(frame, controls, ExpectedCode, ExpectedControl, content);
        Assert.False(verdict.SchemaValid); Assert.False(verdict.Passed); Assert.Equal(issue, verdict.Diagnostic.SchemaIssue);
    }

    [Theory]
    [InlineData("code", "WRONG1", false, true)]
    [InlineData("controlId", "c2", true, false)]
    public void CorrectFormatDoesNotHideWrongVisualCodeOrWrongEnabledNeighbor(string field, string value, bool codeMatches, bool controlMatches)
    {
        var (frame, controls) = Scene();
        var verdict = HybridVisualProbe.Evaluate(frame, controls, ExpectedCode, ExpectedControl, Answer(frame, controls, field, value));
        Assert.True(verdict.SchemaValid); Assert.False(verdict.Passed);
        Assert.Equal(codeMatches, verdict.Diagnostic.CodeMatched); Assert.Equal(controlMatches, verdict.Diagnostic.ControlMatched);
        Assert.Null(verdict.Diagnostic.PointInsideHitBox); Assert.Null(verdict.Diagnostic.SchemaIssue);
    }

    [Fact]
    public void OversizeResponseIsRejectedAndEvidenceDoesNotSplitSurrogatePair()
    {
        var (frame, controls) = Scene();
        string content = new string('x', 4095) + "😀";
        var verdict = HybridVisualProbe.Evaluate(frame, controls, ExpectedCode, ExpectedControl, content);
        Assert.False(verdict.Passed); Assert.False(verdict.SchemaValid);
        Assert.Equal("CONTENT_TOO_LONG", verdict.Diagnostic.SchemaIssue);
        Assert.True(verdict.Diagnostic.FinalContentTruncated);
        Assert.Equal(4095, verdict.Diagnostic.FinalContent!.Length);
        Assert.False(char.IsHighSurrogate(verdict.Diagnostic.FinalContent[^1]));
    }

    [Fact]
    public void TwoCompleteCurrentAttemptsProduceOnlySeparateHybridResultsAndDoNotPromoteProfile()
    {
        var profile = ProviderConfiguration.DefaultDeepSeek();
        ImmutableArray<ProbeAttempt> attempts = [Completed("first"), Completed("second")];
        var report = HybridVisualProbe.BuildReport(profile, attempts, false);
        Assert.Equal(new[] { "hybrid_vision", "hybrid_grounding", "hybrid_schema" }, report.Results.Select(r => r.Name).ToArray());
        Assert.All(report.Results, r => Assert.Equal(CapabilityStatus.ProbePassed, r.Status));
        Assert.Equal(ProviderConfiguration.Fingerprint(profile), report.ProfileFingerprint);
        Assert.Equal(2, report.ApiAttempts); Assert.Equal(new ProviderUsage(20, 8), report.Usage);
        Assert.False(report.Cancelled); Assert.False(report.DiagnosticOnly);
        Assert.Null(profile.ProbeFingerprint); Assert.Empty(profile.Capabilities);
        Assert.False(DesktopTaskCoordinator.IsProfileVerified(profile));
        Assert.False(DesktopTaskCoordinator.IsProfileVerified(profile, controlsRequired: true));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("default")]
    [InlineData("empty")]
    [InlineData("one")]
    [InlineData("three")]
    [InlineData("duplicate")]
    [InlineData("old_variant")]
    [InlineData("missing_variant")]
    public void IncompleteCancelledOrUnboundRunCannotPromoteAnyHybridCapability(string problem)
    {
        ImmutableArray<ProbeAttempt> attempts = [Completed("first"), Completed("second")];
        attempts = problem switch
        {
            "default" => default,
            "empty" => [],
            "one" => [attempts[0]],
            "three" => [attempts[0], attempts[1], Completed("third")],
            "duplicate" => [attempts[0], attempts[0]],
            "old_variant" => attempts.SetItem(1, attempts[1] with { PromptVariant = "baseline" }),
            "missing_variant" => attempts.SetItem(1, attempts[1] with { PromptVariant = null }),
            _ => attempts
        };
        var report = HybridVisualProbe.BuildReport(ProviderConfiguration.DefaultDeepSeek(), attempts, problem == "cancelled");
        Assert.All(report.Results, r => Assert.Equal(CapabilityStatus.Unknown, r.Status));
        Assert.Equal(attempts.IsDefault ? 0 : attempts.Length, report.ApiAttempts);
        Assert.Equal(problem == "cancelled", report.Cancelled);
        if (attempts.IsDefaultOrEmpty) { Assert.Null(report.Usage.InputTokens); Assert.Null(report.Usage.OutputTokens); }
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("passed")]
    [InlineData("code")]
    [InlineData("control")]
    [InlineData("outside")]
    [InlineData("unresolved")]
    [InlineData("diagnostic_missing")]
    public void EachRequiredEvidenceDimensionPreventsFullHybridApprovalWhenMissing(string problem)
    {
        var incomplete = Completed("second");
        incomplete = problem switch
        {
            "schema" => incomplete with { SchemaValid = false },
            "passed" => incomplete with { Passed = false },
            "code" => incomplete with { Diagnostic = incomplete.Diagnostic! with { CodeMatched = false } },
            "control" => incomplete with { Diagnostic = incomplete.Diagnostic! with { ControlMatched = false } },
            "outside" => incomplete with { Diagnostic = incomplete.Diagnostic! with { PointInsideHitBox = false } },
            "unresolved" => incomplete with { Diagnostic = incomplete.Diagnostic! with { PointInsideHitBox = null } },
            _ => incomplete with { Diagnostic = null }
        };
        var report = HybridVisualProbe.BuildReport(ProviderConfiguration.DefaultDeepSeek(), [Completed("first"), incomplete], false);
        AssertNotFullyPassed(report);
        if (problem is "control" or "outside" or "unresolved" or "diagnostic_missing")
            Assert.Equal(CapabilityStatus.Unknown, report.Results.Single(r => r.Name == "hybrid_grounding").Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnknownUsageRemainsUnavailableWithoutInventingZeroCost(bool inputUnknown)
    {
        var second = Completed("second") with { Usage = inputUnknown ? new(null, 4) : new(10, null) };
        var report = HybridVisualProbe.BuildReport(ProviderConfiguration.DefaultDeepSeek(), [Completed("first"), second], false);
        if (inputUnknown) { Assert.Null(report.Usage.InputTokens); Assert.Equal(8, report.Usage.OutputTokens); }
        else { Assert.Equal(20, report.Usage.InputTokens); Assert.Null(report.Usage.OutputTokens); }
        Assert.Equal(2, report.ApiAttempts);
        Assert.All(report.Results, r => Assert.Equal(CapabilityStatus.ProbePassed, r.Status));
    }
}
