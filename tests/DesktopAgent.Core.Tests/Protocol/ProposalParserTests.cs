using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Tests.Protocol;

public sealed class ProposalParserTests
{
    private static readonly Guid TaskId = Guid.Parse("aabbccdd-1111-4222-8333-1234567890ab");
    private static ProposalScope Scope(ProposalRequestKind kind = ProposalRequestKind.General) =>
        new(TaskId, 7, "frame-current", kind);
    private static string FixtureDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocol");
    private static string ReadCanonical(string name) => File.ReadAllText(Path.Combine(FixtureDirectory, "Canonical", name + ".json"));

    public static IEnumerable<object[]> CanonicalCases() =>
        Directory.EnumerateFiles(Path.Combine(FixtureDirectory, "Canonical"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { Path.GetFileNameWithoutExtension(path) });

    public static IEnumerable<object[]> RejectionCases() =>
        Directory.EnumerateFiles(Path.Combine(FixtureDirectory, "Rejections"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { Path.GetFileNameWithoutExtension(path) });

    private static ProposalRequestKind RequestKind(string name) => name switch
    {
        _ when name.StartsWith("draft-focus-check", StringComparison.Ordinal) => ProposalRequestKind.DraftFocusCheck,
        _ when name.StartsWith("draft-check", StringComparison.Ordinal) => ProposalRequestKind.DraftCheck,
        _ when name.StartsWith("commit-check", StringComparison.Ordinal) => ProposalRequestKind.CommitCheck,
        _ when name.StartsWith("commit-result", StringComparison.Ordinal) => ProposalRequestKind.CommitResult,
        _ => ProposalRequestKind.General
    };

    [Theory]
    [MemberData(nameof(CanonicalCases))]
    public void Canonical_documents_parse_into_the_expected_union(string name)
    {
        Proposal proposal = ProposalParser.Parse(ReadCanonical(name), Scope(RequestKind(name)));
        Assert.Equal(1, proposal.SchemaVersion);
        Assert.Equal(TaskId, proposal.TaskId);
        Assert.Equal(7, proposal.Epoch);
        Assert.Equal("frame-current", proposal.FrameId);
        Assert.Equal("p-canonical", proposal.ProposalId);
        string expectedType = name switch
        {
            _ when name.StartsWith("act-", StringComparison.Ordinal) => nameof(ActDecision),
            "inspect" => nameof(InspectDecision),
            "select-monitor" => nameof(SelectMonitorDecision),
            "wait" => nameof(WaitDecision),
            "ask-user" => nameof(AskUserDecision),
            _ when name.StartsWith("prepare-message-", StringComparison.Ordinal) => nameof(PrepareMessageDecision),
            _ when name.StartsWith("finish-", StringComparison.Ordinal) => nameof(FinishDecision),
            "fail" => nameof(FailDecision),
            "draft-focus-check" => nameof(DraftFocusCheckDecision),
            "draft-check" => nameof(DraftCheckDecision),
            _ when name.StartsWith("commit-check-", StringComparison.Ordinal) => nameof(CommitCheckDecision),
            _ when name.StartsWith("commit-result-", StringComparison.Ordinal) => nameof(CommitResultDecision),
            _ => throw new InvalidOperationException("Canonical fixture has no expected branch.")
        };
        Assert.Equal(expectedType, proposal.Decision.GetType().Name);
    }

    [Fact]
    public void Canonical_action_fields_retain_the_exact_requested_values()
    {
        Assert.Equal(new NormalizedPoint(0, 1000), Action<MoveAction>("act-move").Point);
        ClickAction click = Action<ClickAction>("act-click");
        Assert.Equal(new NormalizedPoint(510.25, 145), click.Point);
        Assert.Equal(MouseButton.Left, click.Button);
        Assert.Equal(1, click.ClickCount);
        Assert.Equal(MouseButton.Right, Action<ClickAction>("act-click-right-double").Button);
        Assert.Equal(2, Action<ClickAction>("act-click-right-double").ClickCount);
        DragAction drag = Action<DragAction>("act-drag");
        Assert.Equal(new NormalizedPoint(0, 0), drag.From);
        Assert.Equal(new NormalizedPoint(1000, 1000), drag.To);
        Assert.Equal(2000, drag.DurationMs);
        Assert.Equal(-5, Action<ScrollAction>("act-scroll").Delta);
        Assert.Equal(new[] { AgentKey.CTRL, AgentKey.SHIFT, AgentKey.P }, Action<HotkeyAction>("act-hotkey").Keys);
        Assert.Equal("中文输入与 emoji 🐈", Action<TextAction>("act-text").Text);
    }

    private static T Action<T>(string name) where T : AgentAction =>
        Assert.IsType<T>(Assert.IsType<ActDecision>(ProposalParser.Parse(ReadCanonical(name), Scope()).Decision).Action);

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public void Reusable_rejection_documents_fail_with_stable_codes(string name)
    {
        string json = File.ReadAllText(Path.Combine(FixtureDirectory, "Rejections", name + ".json"));
        string expectedCode = name[(name.LastIndexOf("__", StringComparison.Ordinal) + 2)..];
        ProtocolValidationException error = Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(json, Scope()));
        Assert.Equal(expectedCode, error.Code);
        Assert.StartsWith("$", error.Path);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [MemberData(nameof(CanonicalCases))]
    public void Every_object_rejects_an_extra_field_without_echoing_it(string name)
    {
        string json = JsonNode.Parse(ReadCanonical(name))!.ToJsonString();
        foreach ((string _, JsonObject item) in Objects(JsonNode.Parse(json)))
        {
            string original = item.ToJsonString();
            var modified = (JsonObject)item.DeepClone();
            modified["private-unexpected-field"] = "private-data-must-not-escape";
            string invalid = json.Replace(original, modified.ToJsonString(), StringComparison.Ordinal);
            ProtocolValidationException error = Assert.Throws<ProtocolValidationException>(() =>
                ProposalParser.Parse(invalid, Scope(RequestKind(name))));
            Assert.Equal("UNKNOWN_FIELD", error.Code);
            Assert.DoesNotContain("private-", error.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalCases))]
    public void Every_object_rejects_duplicate_fields(string name)
    {
        string json = JsonNode.Parse(ReadCanonical(name))!.ToJsonString();
        foreach ((string _, JsonObject item) in Objects(JsonNode.Parse(json)))
        {
            string original = item.ToJsonString();
            KeyValuePair<string, JsonNode?> property = item.First();
            string repeated = JsonSerializer.Serialize(property.Key) + ":" + (property.Value?.ToJsonString() ?? "null");
            string duplicate = "{" + repeated + "," + original[1..];
            string invalid = json.Replace(original, duplicate, StringComparison.Ordinal);
            ProtocolValidationException error = Assert.Throws<ProtocolValidationException>(() =>
                ProposalParser.Parse(invalid, Scope(RequestKind(name))));
            Assert.Equal("DUPLICATE_FIELD", error.Code);
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalCases))]
    public void Every_required_field_rejects_omission_and_null(string name)
    {
        string json = JsonNode.Parse(ReadCanonical(name))!.ToJsonString();
        foreach ((string path, JsonObject item) in Objects(JsonNode.Parse(json)))
        {
            foreach (string key in item.Select(property => property.Key))
            {
                if (IsOptional(path, key, item))
                    continue;
                var omitted = (JsonObject)item.DeepClone();
                omitted.Remove(key);
                string missingJson = json.Replace(item.ToJsonString(), omitted.ToJsonString(), StringComparison.Ordinal);
                Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(missingJson, Scope(RequestKind(name))));

                var nulled = (JsonObject)item.DeepClone();
                nulled[key] = null;
                string nullJson = json.Replace(item.ToJsonString(), nulled.ToJsonString(), StringComparison.Ordinal);
                Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(nullJson, Scope(RequestKind(name))));
            }
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalCases))]
    public void Every_present_field_rejects_a_different_JSON_type(string name)
    {
        string json = JsonNode.Parse(ReadCanonical(name))!.ToJsonString();
        foreach ((string _, JsonObject item) in Objects(JsonNode.Parse(json)))
        {
            foreach ((string key, JsonNode? value) in item)
            {
                var modified = (JsonObject)item.DeepClone();
                JsonValueKind kind = value?.GetValueKind() ?? JsonValueKind.Null;
                modified[key] = kind switch
                {
                    JsonValueKind.Object => new JsonArray(),
                    JsonValueKind.Array => new JsonObject(),
                    JsonValueKind.String or JsonValueKind.Null => JsonValue.Create(false),
                    _ => JsonValue.Create("wrong-type")
                };
                string invalid = json.Replace(item.ToJsonString(), modified.ToJsonString(), StringComparison.Ordinal);
                Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(invalid, Scope(RequestKind(name))));
            }
        }
    }

    private static bool IsOptional(string path, string key, JsonObject item)
    {
        if (path.Contains(".evidence[", StringComparison.Ordinal) && key is "region" or "sourceUrl" or "uncertainty")
            return true;
        if (item["kind"]?.GetValue<string>() == "commit_check" && key is "sendRegion" or "sendAction")
            return item["recipientMatches"]!.GetValue<bool>() == false || item["draftMatches"]!.GetValue<bool>() == false;
        return false;
    }

    private static IEnumerable<(string Path, JsonObject Object)> Objects(JsonNode? node, string path = "$")
    {
        if (node is JsonObject obj)
        {
            yield return (path, obj);
            foreach ((string key, JsonNode? child) in obj)
                foreach (var item in Objects(child, path + "." + key))
                    yield return item;
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
                foreach (var item in Objects(array[i], path + "[" + i + "]"))
                    yield return item;
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalCases))]
    public void Locally_requested_reply_shape_cannot_be_changed_by_the_model(string name)
    {
        ProposalRequestKind expected = RequestKind(name);
        foreach (ProposalRequestKind attempted in Enum.GetValues<ProposalRequestKind>())
        {
            if (attempted == expected)
                continue;
            ProtocolValidationException error = Assert.Throws<ProtocolValidationException>(() =>
                ProposalParser.Parse(ReadCanonical(name), Scope(attempted)));
            Assert.Equal("STAGE_MISMATCH", error.Code);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1000.0000001")]
    [InlineData("1000.0000000000000001")]
    [InlineData("1.0000000000000000000000000000000001e3")]
    [InlineData("-1e-9999")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("\"500\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Invalid_coordinate_values_are_rejected_without_clamping(string numericJson)
    {
        string json = Compact("act-move").Replace("\"x\":0", "\"x\":" + numericJson, StringComparison.Ordinal);
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(json, Scope()));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1000", 1000)]
    [InlineData("5e2", 500)]
    [InlineData("500.25", 500.25)]
    [InlineData("1.0000000000000000000000000000e3", 1000)]
    [InlineData("0.00000000000000000000000000001e32", 1000)]
    [InlineData("-0.0000e3", 0)]
    [InlineData("1e-9999", 0)]
    public void Finite_normalized_boundary_and_fractional_points_are_retained(string numericJson, double expected)
    {
        string json = Compact("act-move").Replace("\"x\":0", "\"x\":" + numericJson, StringComparison.Ordinal);
        var decision = Assert.IsType<ActDecision>(ProposalParser.Parse(json, Scope()).Decision);
        Assert.Equal(expected, Assert.IsType<MoveAction>(decision.Action).Point.X);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("7.0")]
    [InlineData("7e0")]
    [InlineData("\"7\"")]
    [InlineData("null")]
    public void Epoch_is_a_nonnegative_Int64_json_integer(string value)
    {
        string json = Compact("act-click").Replace("\"epoch\":7", "\"epoch\":" + value, StringComparison.Ordinal);
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(json, Scope()));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void Epoch_accepts_the_full_nonnegative_Int64_range(long epoch)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("act-click"))!.AsObject();
        root["epoch"] = epoch;
        Assert.Equal(epoch, ProposalParser.Parse(root.ToJsonString(), Scope() with { Epoch = epoch }).Epoch);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("aabbccdd1111422283331234567890ab")]
    [InlineData("not-a-guid")]
    [InlineData("aabbccdd-1111-4222-8333-1234567890ac")]
    public void Missing_malformed_or_other_task_identity_is_rejected(string value)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("act-click"))!.AsObject();
        root["taskId"] = value;
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"CTRL\"]")]
    [InlineData("[\"ALT\"]")]
    [InlineData("[\"SHIFT\"]")]
    [InlineData("[\"CTRL\",\"ALT\"]")]
    [InlineData("[\"WIN\",\"SHIFT\"]")]
    [InlineData("[\"CTRL\",\"CTRL\",\"A\"]")]
    [InlineData("[\"ENTER\",\"CTRL\"]")]
    [InlineData("[\"S\",\"ALT\"]")]
    [InlineData("[\"A\",\"CTRL\"]")]
    [InlineData("[\"CTRL\",\"ALT\",\"DELETE\"]")]
    [InlineData("[\"DELETE\",\"SHIFT\",\"ALT\",\"CTRL\"]")]
    [InlineData("[\"CTRL\",\"ALT\",\"F8\"]")]
    [InlineData("[\"F9\",\"ALT\",\"CTRL\"]")]
    [InlineData("[\"CTRL\",\"SHIFT\",\"ALT\",\"F9\"]")]
    [InlineData("[\"CTRL\",\"SHIFT\",\"ALT\",\"WIN\",\"A\"]")]
    [InlineData("[\"ctrl\",\"A\"]")]
    [InlineData("[\"CTRL+A\"]")]
    [InlineData("[\"LWIN\"]")]
    [InlineData("[\"D0\"]")]
    [InlineData("[\"12\"]")]
    [InlineData("[\"F13\"]")]
    [InlineData("[\"CTRL, A\"]")]
    [InlineData("[\" CTRL\",\"A\"]")]
    [InlineData("[0]")]
    [InlineData("[null]")]
    public void Hotkey_whitelist_duplicates_reserved_combinations_and_modifier_holds_are_rejected(string keys)
    {
        Assert.Throws<ProtocolValidationException>(() => ParseActionJson("{\"type\":\"hotkey\",\"keys\":" + keys + "}"));
    }

    [Theory]
    [InlineData("[\"WIN\"]", AgentKey.WIN)]
    [InlineData("[\"0\"]", AgentKey.D0)]
    [InlineData("[\"9\"]", AgentKey.D9)]
    [InlineData("[\"F12\"]", AgentKey.F12)]
    [InlineData("[\"Z\"]", AgentKey.Z)]
    [InlineData("[\"TAB\"]", AgentKey.TAB)]
    public void Supported_single_keys_have_exact_wire_names(string keys, AgentKey expected)
    {
        HotkeyAction action = Assert.IsType<HotkeyAction>(ParseActionJson("{\"type\":\"hotkey\",\"keys\":" + keys + "}"));
        Assert.Equal(new[] { expected }, action.Keys.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(27)]
    [InlineData(127)]
    [InlineData(133)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void Text_and_message_body_reject_control_and_line_separator_characters(int character)
    {
        string value = "text" + (char)character + "text";
        Assert.Throws<ProtocolValidationException>(() => ParseActionJson(JsonSerializer.Serialize(new { type = "text", text = value })));
        JsonObject root = JsonNode.Parse(ReadCanonical("prepare-message-click"))!.AsObject();
        root["decision"]!["text"] = value;
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope()));
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800x")]
    [InlineData("\\uD800\\uD800")]
    public void Invalid_escaped_UTF16_is_rejected(string escaped)
    {
        Assert.Throws<ProtocolValidationException>(() => ParseActionJson("{\"type\":\"text\",\"text\":\"" + escaped + "\"}"));
    }

    [Fact]
    public void Invalid_literal_UTF16_is_rejected_before_JSON_decoding()
    {
        // Insert the literal directly; serializing it first could replace the invalid code unit.
        string json = Compact("act-move").Replace("A visible target", "bad" + '\uD800', StringComparison.Ordinal);
        Assert.Equal("INVALID_UNICODE", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(json, Scope())).Code);
    }

    [Fact]
    public void Text_and_identifier_limits_are_measured_in_UTF16_characters()
    {
        string maximum = new('x', ProtocolLimits.Text);
        Assert.Equal(maximum, Assert.IsType<TextAction>(ParseActionJson(JsonSerializer.Serialize(new { type = "text", text = maximum }))).Text);
        foreach (string value in new[] { "", " ", maximum + "x" })
            Assert.Throws<ProtocolValidationException>(() => ParseActionJson(JsonSerializer.Serialize(new { type = "text", text = value })));

        JsonObject root = JsonNode.Parse(ReadCanonical("act-click"))!.AsObject();
        root["proposalId"] = new string('p', ProtocolLimits.Id);
        root["current"] = new string('c', ProtocolLimits.StatusText);
        root["next"] = "";
        ProposalParser.Parse(root.ToJsonString(), Scope());
        root["proposalId"] = new string('p', ProtocolLimits.Id + 1);
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope()));
        root["proposalId"] = "p";
        root["current"] = new string('c', ProtocolLimits.StatusText + 1);
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope()));
    }

    [Theory]
    [InlineData("{\"type\":\"click\",\"x\":1,\"y\":1,\"button\":\"middle\",\"clickCount\":1}")]
    [InlineData("{\"type\":\"click\",\"x\":1,\"y\":1,\"button\":\"left\",\"clickCount\":3}")]
    [InlineData("{\"type\":\"click\",\"x\":1,\"y\":1,\"button\":\"left\",\"clickCount\":1.0}")]
    [InlineData("{\"type\":\"scroll\",\"x\":1,\"y\":1,\"delta\":0}")]
    [InlineData("{\"type\":\"scroll\",\"x\":1,\"y\":1,\"delta\":6}")]
    [InlineData("{\"type\":\"scroll\",\"x\":1,\"y\":1,\"delta\":-6}")]
    [InlineData("{\"type\":\"drag\",\"fromX\":1,\"fromY\":1,\"toX\":2,\"toY\":2,\"durationMs\":199}")]
    [InlineData("{\"type\":\"drag\",\"fromX\":1,\"fromY\":1,\"toX\":2,\"toY\":2,\"durationMs\":2001}")]
    public void Action_event_counts_durations_and_nonzero_wheel_steps_are_bounded(string action)
    {
        Assert.Throws<ProtocolValidationException>(() => ParseActionJson(action));
    }

    [Theory]
    [InlineData(-1, 0, 1000, 1000)]
    [InlineData(0, 0, 1001, 1000)]
    [InlineData(100, 0, 100, 1000)]
    [InlineData(200, 0, 100, 1000)]
    [InlineData(0, 100, 1000, 100)]
    [InlineData(0, 200, 1000, 100)]
    public void Rectangles_reject_outside_empty_or_reversed_boundaries(double x0, double y0, double x1, double y1)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("inspect"))!.AsObject();
        root["decision"]!["rect"] = JsonSerializer.SerializeToNode(new { x0, y0, x1, y1 });
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope()));
    }

    [Theory]
    [InlineData("recipientRegion", "draftRegion")]
    [InlineData("recipientRegion", "sendRegion")]
    [InlineData("draftRegion", "sendRegion")]
    public void All_message_regions_must_be_pairwise_disjoint(string first, string second)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("prepare-message-click"))!.AsObject();
        JsonNode decision = root["decision"]!;
        decision[first] = decision[second]!.DeepClone();
        Assert.Equal("OVERLAPPING_MESSAGE_REGIONS", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
    }

    [Fact]
    public void Message_draft_focus_point_must_be_inside_its_draft_region()
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("prepare-message-click"))!.AsObject();
        root["decision"]!["draftPoint"]!["x"] = 750;
        Assert.Equal("DRAFT_POINT_OUTSIDE_REGION", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
    }

    [Theory]
    [InlineData(700, 500)]
    [InlineData(350, 800)]
    public void Interior_draft_region_upper_boundaries_are_excluded(double x, double y)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("prepare-message-click"))!.AsObject();
        root["decision"]!["draftPoint"] = JsonSerializer.SerializeToNode(new { x, y });
        Assert.Equal("DRAFT_POINT_OUTSIDE_REGION", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope())).Code);
    }

    [Fact]
    public void Full_image_upper_boundary_points_remain_usable()
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("prepare-message-click"))!.AsObject();
        root["decision"]!["sendAction"]!["x"] = 1000;
        root["decision"]!["sendAction"]!["y"] = 1000;
        Assert.IsType<PrepareMessageDecision>(ProposalParser.Parse(root.ToJsonString(), Scope()).Decision);
    }

    [Theory]
    [InlineData("{\"type\":\"click\",\"x\":750,\"y\":900,\"button\":\"left\",\"clickCount\":1}")]
    [InlineData("{\"type\":\"click\",\"x\":900,\"y\":900,\"button\":\"right\",\"clickCount\":1}")]
    [InlineData("{\"type\":\"click\",\"x\":900,\"y\":900,\"button\":\"left\",\"clickCount\":2}")]
    [InlineData("{\"type\":\"hotkey\",\"keys\":[\"SHIFT\",\"ENTER\"]}")]
    [InlineData("{\"type\":\"hotkey\",\"keys\":[\"CTRL\",\"ALT\",\"ENTER\"]}")]
    [InlineData("{\"type\":\"text\",\"text\":\"anything\"}")]
    [InlineData("{\"type\":\"move\",\"x\":900,\"y\":900}")]
    public void Both_message_preparation_and_commit_use_only_the_limited_send_action(string action)
    {
        foreach (string name in new[] { "prepare-message-click", "commit-check-approved" })
        {
            JsonObject root = JsonNode.Parse(ReadCanonical(name))!.AsObject();
            root["decision"]!["sendAction"] = JsonNode.Parse(action);
            Assert.Equal("INVALID_SEND_ACTION", Assert.Throws<ProtocolValidationException>(() =>
                ProposalParser.Parse(root.ToJsonString(), Scope(RequestKind(name)))).Code);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Failed_commit_checks_may_omit_targets_but_cannot_supply_half_a_target(bool recipient, bool draft)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("commit-check-approved"))!.AsObject();
        JsonObject decision = root["decision"]!.AsObject();
        decision["recipientMatches"] = recipient;
        decision["draftMatches"] = draft;
        decision.Remove("sendAction");
        Assert.Equal("INCOMPLETE_SEND_TARGET", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(ProposalRequestKind.CommitCheck))).Code);
        decision.Remove("sendRegion");
        CommitCheckDecision parsed = Assert.IsType<CommitCheckDecision>(ProposalParser.Parse(root.ToJsonString(), Scope(ProposalRequestKind.CommitCheck)).Decision);
        Assert.Null(parsed.SendRegion);
        Assert.Null(parsed.SendAction);
    }

    [Fact]
    public void Commit_target_uses_the_new_frame_and_can_move_from_the_prepared_region()
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("commit-check-approved"))!.AsObject();
        root["frameId"] = "frame-after-approval";
        root["decision"]!["sendRegion"] = JsonSerializer.SerializeToNode(new { x0 = 10, y0 = 10, x1 = 100, y1 = 100 });
        root["decision"]!["sendAction"]!["x"] = 50;
        root["decision"]!["sendAction"]!["y"] = 50;
        Proposal parsed = ProposalParser.Parse(root.ToJsonString(), Scope(ProposalRequestKind.CommitCheck) with { FrameId = "frame-after-approval" });
        Assert.Equal(new NormalizedRect(10, 10, 100, 100), Assert.IsType<CommitCheckDecision>(parsed.Decision).SendRegion);
        Assert.Equal("FRAME_MISMATCH", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(ProposalRequestKind.CommitCheck))).Code);
    }

    [Theory]
    [InlineData("finish-succeeded")]
    [InlineData("commit-result-sent")]
    [InlineData("commit-result-not-sent")]
    [InlineData("commit-result-uncertain")]
    public void Completion_and_result_evidence_are_bounded_and_bound_to_the_current_frame(string name)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical(name))!.AsObject();
        JsonObject original = (JsonObject)root["decision"]!["evidence"]![0]!.DeepClone();
        root["decision"]!["evidence"]![0]!["frameId"] = "frame-old";
        Assert.Equal("EVIDENCE_FRAME_MISMATCH", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(RequestKind(name)))).Code);
        root["decision"]!["evidence"] = new JsonArray();
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(RequestKind(name))));
        root["decision"]!["evidence"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => original.DeepClone()).ToArray());
        ProposalParser.Parse(root.ToJsonString(), Scope(RequestKind(name)));
        root["decision"]!["evidence"]!.AsArray().Add(original.DeepClone());
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(RequestKind(name))));
        root["decision"]!["evidence"] = original.DeepClone();
        Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(RequestKind(name))));
    }

    [Theory]
    [InlineData("recipientMatches")]
    [InlineData("draftEmpty")]
    [InlineData("focusInDraft")]
    public void Visual_boolean_flags_do_not_accept_truthy_strings_or_numbers(string property)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("draft-focus-check"))!.AsObject();
        foreach (JsonNode value in new JsonNode[] { JsonValue.Create("true"), JsonValue.Create(1), new JsonArray(), new JsonObject() })
        {
            root["decision"]![property] = value;
            Assert.Equal("EXPECTED_BOOLEAN", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope(ProposalRequestKind.DraftFocusCheck))).Code);
        }
    }

    [Fact]
    public void Optional_evidence_fields_accept_missing_or_null_and_still_reject_wrong_types()
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("finish-succeeded"))!.AsObject();
        foreach (string name in new[] { "region", "sourceUrl", "uncertainty" })
        {
            JsonObject evidence = root["decision"]!["evidence"]![0]!.AsObject();
            evidence[name] = null;
            ProposalParser.Parse(root.ToJsonString(), Scope());
            evidence[name] = false;
            Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(root.ToJsonString(), Scope()));
            evidence.Remove(name);
        }
    }

    [Fact]
    public void UTF8_byte_limit_accounts_for_multibyte_text_and_whitespace()
    {
        string json = Compact("act-click");
        string exact = json + new string(' ', ProtocolLimits.JsonUtf8Bytes - System.Text.Encoding.UTF8.GetByteCount(json));
        ProposalParser.Parse(exact, Scope());
        Assert.Equal("DOCUMENT_TOO_LARGE", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(exact + " ", Scope())).Code);
        string multibyte = new string('界', ProtocolLimits.JsonUtf8Bytes / 2);
        Assert.Equal("DOCUMENT_TOO_LARGE", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(multibyte, Scope())).Code);
    }

    [Fact]
    public void Excessive_depth_multiple_documents_comments_and_code_fences_are_not_repaired()
    {
        string json = Compact("act-click");
        foreach (string invalid in new[]
        {
            new string('[', ProtocolLimits.JsonDepth + 1) + "0" + new string(']', ProtocolLimits.JsonDepth + 1),
            json + json, "```json\n" + json + "\n```", "// model comment\n" + json,
            "Model answer: " + json, json[..^1] + ",}", "", " ", "null", "[]", "true"
        })
            Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(invalid, Scope()));
    }

    [Fact]
    public void Escaped_equivalent_field_names_are_still_duplicates_and_case_changes_are_extra_fields()
    {
        string json = Compact("act-click");
        string duplicate = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"\\u0073chemaVersion\":1", StringComparison.Ordinal);
        Assert.Equal("DUPLICATE_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(duplicate, Scope())).Code);
        string wrongCase = json.Replace("\"frameId\"", "\"FrameId\"", StringComparison.Ordinal);
        Assert.Equal("UNKNOWN_FIELD", Assert.Throws<ProtocolValidationException>(() => ProposalParser.Parse(wrongCase, Scope())).Code);
    }

    private static string Compact(string name) => JsonNode.Parse(ReadCanonical(name))!.ToJsonString();

    private static AgentAction ParseActionJson(string action)
    {
        JsonObject root = JsonNode.Parse(ReadCanonical("act-click"))!.AsObject();
        // Keep raw invalid JSON/UTF16 intact instead of normalizing it through JsonNode.
        string original = root["decision"]!["action"]!.ToJsonString();
        string json = root.ToJsonString().Replace(original, action, StringComparison.Ordinal);
        return Assert.IsType<ActDecision>(ProposalParser.Parse(json, Scope()).Decision).Action;
    }
}
