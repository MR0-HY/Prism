using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DesktopAgent.Core.Protocol;

/// <summary>
/// Parses one complete model reply. Parsing validates shape and identity; it never authorizes input,
/// proves visual claims, or replaces the policy validator's live window/frame/approval checks.
/// </summary>
public static class ProposalParser
{
    public static Proposal Parse(string json, ProposalScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ValidateScope(scope);
        if (json is null || string.IsNullOrWhiteSpace(json))
            throw Error("EMPTY_DOCUMENT", "$");
        // A UTF-16 string cannot need fewer UTF-8 bytes than code units. Bound this before encoding.
        if (json.Length > ProtocolLimits.JsonUtf8Bytes)
            throw Error("DOCUMENT_TOO_LARGE", "$");
        ValidateUnicode(json, "$", allowControls: true);
        if (Encoding.UTF8.GetByteCount(json) > ProtocolLimits.JsonUtf8Bytes)
            throw Error("DOCUMENT_TOO_LARGE", "$");

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = ProtocolLimits.JsonDepth
            });
            JsonElement root = document.RootElement;
            Object(root, "$", "schemaVersion", "proposalId", "taskId", "epoch", "frameId", "current", "next", "decision", "planUpdates");
            int version = Integer(Required(root, "schemaVersion", "$"), "$.schemaVersion", scope.SchemaVersion, scope.SchemaVersion);
            string proposalId = String(root, "proposalId", "$", ProtocolLimits.Id);
            string taskIdText = String(root, "taskId", "$", 36);
            if (!Guid.TryParseExact(taskIdText, "D", out Guid taskId) || taskId == Guid.Empty)
                throw Error("INVALID_TASK_ID", "$.taskId");
            long epoch = Long(Required(root, "epoch", "$"), "$.epoch", 0, long.MaxValue);
            string frameId = String(root, "frameId", "$", ProtocolLimits.FrameId);
            if (taskId != scope.TaskId)
                throw Error("TASK_MISMATCH", "$.taskId");
            if (epoch != scope.Epoch)
                throw Error("EPOCH_MISMATCH", "$.epoch");
            if (!StringComparer.Ordinal.Equals(frameId, scope.FrameId))
                throw Error("FRAME_MISMATCH", "$.frameId");
            string current = String(root, "current", "$", ProtocolLimits.StatusText, allowEmpty: true);
            string next = String(root, "next", "$", ProtocolLimits.StatusText, allowEmpty: true);
            Decision decision = ParseDecision(Required(root, "decision", "$"), "$.decision", scope);
            return new Proposal(version, proposalId, taskId, epoch, frameId, current, next, decision)
                { PlanUpdates = ReadPlanUpdates(root, scope) };
        }
        catch (ProtocolValidationException) { throw; }
        // System.Text.Json diagnostics can include input excerpts or dynamic field names; do not retain them.
        catch (JsonException) { throw Error("INVALID_JSON", "$"); }
        catch (InvalidOperationException) { throw Error("INVALID_JSON_VALUE", "$"); }
        catch (ArgumentException) { throw Error("INVALID_JSON_VALUE", "$"); }
    }

    private static ImmutableArray<PlanStepUpdate> ReadPlanUpdates(JsonElement root, ProposalScope scope)
    {
        if (!root.TryGetProperty("planUpdates", out var updates)) return [];
        if (scope.SchemaVersion != 2 || scope.RequestKind != ProposalRequestKind.General)
            throw Error("PLAN_UPDATES_NOT_ALLOWED", "$.planUpdates");
        Array(updates, "$.planUpdates", 0, 8);
        var result = ImmutableArray.CreateBuilder<PlanStepUpdate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in updates.EnumerateArray())
        {
            const string path = "$.planUpdates[]";
            Object(update, path, "stepId", "status", "observation");
            string id = String(update, "stepId", path, 2);
            if (id.Length != 2 || id[0] != 's' || id[1] is < '1' or > '8' || !ids.Add(id))
                throw Error("INVALID_PLAN_STEP", path);
            string status = String(update, "status", path, 12);
            if (status is not ("pending" or "active" or "completed")) throw Error("INVALID_PLAN_STATUS", path);
            result.Add(new(id, status, String(update, "observation", path, 300)));
        }
        return result.ToImmutable();
    }
    private static void ValidateScope(ProposalScope scope)
    {
        if (scope.TaskId == Guid.Empty || scope.Epoch < 0 ||
            string.IsNullOrWhiteSpace(scope.FrameId) || scope.FrameId.Length > ProtocolLimits.FrameId ||
            !Enum.IsDefined(scope.RequestKind) || scope.SchemaVersion is not (1 or 2))
            throw new ArgumentException("The locally supplied proposal scope is invalid.", nameof(scope));
        ValidateUnicode(scope.FrameId, "$scope.frameId", allowControls: false);
    }

    private static Decision ParseDecision(JsonElement element, string path, ProposalScope scope)
    {
        string kind = Discriminator(element, "kind", path);
        if (kind == "act_control")
        {
            if (scope.SchemaVersion != 2) throw Error("VERSION_MISMATCH", path + ".kind");
            if (scope.RequestKind != ProposalRequestKind.General) throw Error("STAGE_MISMATCH", path + ".kind");
            Object(element, path, "kind", "snapshotId", "controlId", "button", "clickCount", "target", "expected", "messageReview");
            var button = String(element, "button", path, 10) switch
            {
                "left" => MouseButton.Left, "right" => MouseButton.Right,
                _ => throw Error("UNKNOWN_MOUSE_BUTTON", path + ".button")
            };
            return new ControlActDecision(String(element, "snapshotId", path, ProtocolLimits.Id), String(element, "controlId", path, 32), button,
                Integer(Required(element, "clickCount", path), path + ".clickCount", 1, 2),
                String(element, "target", path, ProtocolLimits.StatusText), String(element, "expected", path, ProtocolLimits.StatusText))
                { MessageReview = ParseMessageReview(element, path, scope) };
        }
        if (!IsAllowedKind(kind, scope.RequestKind))
            throw Error(IsKnownKind(kind) ? "STAGE_MISMATCH" : "UNKNOWN_DECISION", path + ".kind");

        switch (kind)
        {
            case "act":
                Object(element, path, "kind", "action", "target", "expected", "messageReview");
                var action = ParseAction(Required(element, "action", path), path + ".action");
                if (action is ReplaceTextAction && scope.SchemaVersion != 2)
                    throw Error("VERSION_MISMATCH", path + ".action.type");
                return new ActDecision(action,
                    String(element, "target", path, ProtocolLimits.StatusText),
                    String(element, "expected", path, ProtocolLimits.StatusText))
                    { MessageReview = ParseMessageReview(element, path, scope) };
            case "inspect":
                Object(element, path, "kind", "rect");
                return new InspectDecision(Rect(Required(element, "rect", path), path + ".rect"));
            case "select_monitor":
                Object(element, path, "kind", "monitorId");
                return new SelectMonitorDecision(String(element, "monitorId", path, ProtocolLimits.MonitorId));
            case "wait":
                Object(element, path, "kind", "milliseconds", "reason");
                return new WaitDecision(Integer(Required(element, "milliseconds", path), path + ".milliseconds", 100, 2000),
                    String(element, "reason", path, ProtocolLimits.Reason));
            case "ask_user":
                Object(element, path, "kind", "reason", "question");
                return new AskUserDecision(String(element, "reason", path, ProtocolLimits.Reason),
                    String(element, "question", path, ProtocolLimits.Question));
            case "prepare_message":
                return PrepareMessage(element, path);
            case "finish":
                Object(element, path, "kind", "outcome", "summary", "evidence");
                FinishOutcome outcome = String(element, "outcome", path, 20) switch
                {
                    "succeeded" => FinishOutcome.Succeeded,
                    "partial" => FinishOutcome.Partial,
                    _ => throw Error("UNKNOWN_OUTCOME", path + ".outcome")
                };
                return new FinishDecision(outcome, String(element, "summary", path, ProtocolLimits.Summary),
                    EvidenceArray(Required(element, "evidence", path), path + ".evidence", scope.FrameId));
            case "fail":
                Object(element, path, "kind", "code", "reason");
                return new FailDecision(String(element, "code", path, ProtocolLimits.Id),
                    String(element, "reason", path, ProtocolLimits.Reason));
            case "draft_focus_check":
                Object(element, path, "kind", "recipientMatches", "draftEmpty", "focusInDraft", "reason");
                return new DraftFocusCheckDecision(Boolean(element, "recipientMatches", path),
                    Boolean(element, "draftEmpty", path), Boolean(element, "focusInDraft", path),
                    String(element, "reason", path, ProtocolLimits.Reason));
            case "draft_check":
                Object(element, path, "kind", "recipientMatches", "draftMatches", "reason");
                return new DraftCheckDecision(Boolean(element, "recipientMatches", path),
                    Boolean(element, "draftMatches", path), String(element, "reason", path, ProtocolLimits.Reason));
            case "commit_check":
                return CommitCheck(element, path);
            case "commit_result":
                Object(element, path, "kind", "status", "evidence", "reason");
                CommitStatus status = String(element, "status", path, 20) switch
                {
                    "sent" => CommitStatus.Sent,
                    "not_sent" => CommitStatus.NotSent,
                    "uncertain" => CommitStatus.Uncertain,
                    _ => throw Error("UNKNOWN_COMMIT_STATUS", path + ".status")
                };
                return new CommitResultDecision(status,
                    EvidenceArray(Required(element, "evidence", path), path + ".evidence", scope.FrameId),
                    String(element, "reason", path, ProtocolLimits.Reason));
            default:
                throw Error("UNKNOWN_DECISION", path + ".kind");
        }
    }

    private static MessageReview? ParseMessageReview(JsonElement element, string path, ProposalScope scope)
    {
        if (!element.TryGetProperty("messageReview", out _)) return null;
        if (scope.SchemaVersion != 2) throw Error("VERSION_MISMATCH", path + ".messageReview");
        if (scope.RequestKind != ProposalRequestKind.General) throw Error("STAGE_MISMATCH", path + ".messageReview");
        var review = Required(element, "messageReview", path);
        string reviewPath = path + ".messageReview";
        Object(review, reviewPath, "recipient", "message");
        return new(String(review, "recipient", reviewPath, ProtocolLimits.Recipient),
            String(review, "message", reviewPath, ProtocolLimits.Text));
    }

    private static PrepareMessageDecision PrepareMessage(JsonElement element, string path)
    {
        Object(element, path, "kind", "recipient", "text", "recipientRegion", "draftRegion", "sendRegion", "draftPoint", "sendAction");
        string recipient = String(element, "recipient", path, ProtocolLimits.Recipient);
        string text = String(element, "text", path, ProtocolLimits.Text);
        NormalizedRect recipientRegion = Rect(Required(element, "recipientRegion", path), path + ".recipientRegion");
        NormalizedRect draftRegion = Rect(Required(element, "draftRegion", path), path + ".draftRegion");
        NormalizedRect sendRegion = Rect(Required(element, "sendRegion", path), path + ".sendRegion");
        NormalizedPoint draftPoint = Point(Required(element, "draftPoint", path), path + ".draftPoint");
        AgentAction sendAction = ParseAction(Required(element, "sendAction", path), path + ".sendAction");
        if (recipientRegion.Overlaps(draftRegion) || recipientRegion.Overlaps(sendRegion) || draftRegion.Overlaps(sendRegion))
            throw Error("OVERLAPPING_MESSAGE_REGIONS", path);
        if (!draftRegion.Contains(draftPoint))
            throw Error("DRAFT_POINT_OUTSIDE_REGION", path + ".draftPoint");
        ValidateSendAction(sendAction, sendRegion, path + ".sendAction");
        return new PrepareMessageDecision(recipient, text, recipientRegion, draftRegion, sendRegion, draftPoint, sendAction);
    }

    private static CommitCheckDecision CommitCheck(JsonElement element, string path)
    {
        Object(element, path, "kind", "recipientMatches", "draftMatches", "sendRegion", "sendAction", "reason");
        bool recipientMatches = Boolean(element, "recipientMatches", path);
        bool draftMatches = Boolean(element, "draftMatches", path);
        NormalizedRect? region = Optional(element, "sendRegion") is { } regionElement
            ? Rect(regionElement, path + ".sendRegion") : null;
        AgentAction? action = Optional(element, "sendAction") is { } actionElement
            ? ParseAction(actionElement, path + ".sendAction") : null;
        if (recipientMatches && draftMatches && (region is null || action is null))
            throw Error("SEND_TARGET_REQUIRED", path);
        if ((region is null) != (action is null))
            throw Error("INCOMPLETE_SEND_TARGET", path);
        if (region is { } sendRegion && action is not null)
            ValidateSendAction(action, sendRegion, path + ".sendAction");
        return new CommitCheckDecision(recipientMatches, draftMatches, region, action,
            String(element, "reason", path, ProtocolLimits.Reason));
    }

    private static void ValidateSendAction(AgentAction action, NormalizedRect region, string path)
    {
        if (action is ClickAction { Button: MouseButton.Left, ClickCount: 1 } click && region.Contains(click.Point))
            return;
        if (action is HotkeyAction hotkey && IsSendHotkey(hotkey.Keys))
            return;
        throw Error("INVALID_SEND_ACTION", path);
    }

    private static bool IsSendHotkey(ImmutableArray<AgentKey> keys) =>
        (keys.Length == 1 && keys[0] == AgentKey.ENTER) ||
        (keys.Length == 2 && ((keys[0] == AgentKey.CTRL && keys[1] == AgentKey.ENTER) ||
                             (keys[0] == AgentKey.ALT && keys[1] == AgentKey.S)));

    private static AgentAction ParseAction(JsonElement element, string path)
    {
        string type = Discriminator(element, "type", path);
        switch (type)
        {
            case "move":
                Object(element, path, "type", "x", "y");
                return new MoveAction(Coordinates(element, path, "x", "y"));
            case "click":
                Object(element, path, "type", "x", "y", "button", "clickCount");
                MouseButton button = String(element, "button", path, 10) switch
                {
                    "left" => MouseButton.Left,
                    "right" => MouseButton.Right,
                    _ => throw Error("UNKNOWN_MOUSE_BUTTON", path + ".button")
                };
                return new ClickAction(Coordinates(element, path, "x", "y"), button,
                    Integer(Required(element, "clickCount", path), path + ".clickCount", 1, 2));
            case "drag":
                Object(element, path, "type", "fromX", "fromY", "toX", "toY", "durationMs");
                return new DragAction(Coordinates(element, path, "fromX", "fromY"),
                    Coordinates(element, path, "toX", "toY"),
                    Integer(Required(element, "durationMs", path), path + ".durationMs", 200, 2000));
            case "scroll":
                Object(element, path, "type", "x", "y", "delta");
                int delta = Integer(Required(element, "delta", path), path + ".delta", -5, 5);
                if (delta == 0)
                    throw Error("OUT_OF_RANGE", path + ".delta");
                return new ScrollAction(Coordinates(element, path, "x", "y"), delta);
            case "hotkey":
                Object(element, path, "type", "keys");
                return new HotkeyAction(Keys(Required(element, "keys", path), path + ".keys"));
            case "text":
                Object(element, path, "type", "text");
                return new TextAction(String(element, "text", path, ProtocolLimits.Text));
            case "replace_existing":
                Object(element, path, "type", "text", "expectedCurrent", "snapshotId", "controlId");
                return new ReplaceTextAction(String(element, "text", path, ProtocolLimits.ReplaceText),
                    String(element, "expectedCurrent", path, ProtocolLimits.ReplaceText, allowEmpty: true),
                    String(element, "snapshotId", path, ProtocolLimits.Id), String(element, "controlId", path, 32));
            default:
                throw Error("UNKNOWN_ACTION", path + ".type");
        }
    }

    private static ImmutableArray<AgentKey> Keys(JsonElement element, string path)
    {
        Array(element, path, 1, 4);
        var keys = ImmutableArray.CreateBuilder<AgentKey>(element.GetArrayLength());
        var distinct = new HashSet<AgentKey>();
        int index = 0;
        bool hasNonModifier = false;
        bool invalidModifierOrder = false;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string keyPath = path + "[" + (index++).ToString(CultureInfo.InvariantCulture) + "]";
            string value = String(item, keyPath, 20);
            AgentKey key;
            if (value.Length == 1 && value[0] is >= '0' and <= '9')
                key = AgentKey.D0 + (value[0] - '0');
            else if (value.Length == 2 && value[0] == 'D' && char.IsAsciiDigit(value[1]))
                throw Error("UNKNOWN_KEY", keyPath);
            else if (!Enum.TryParse(value, ignoreCase: false, out key) || !Enum.IsDefined(key) ||
                     !StringComparer.Ordinal.Equals(value, key.ToString()))
                throw Error("UNKNOWN_KEY", keyPath);
            if (!distinct.Add(key))
                throw Error("DUPLICATE_KEY", path);
            // The executor presses in this order. ENTER followed by CTRL must never be
            // mistaken for CTRL+ENTER: the first event may already submit a message.
            invalidModifierOrder |= IsModifier(key) && hasNonModifier;
            hasNonModifier |= !IsModifier(key);
            keys.Add(key);
        }
        if (keys.All(IsModifier) && !(keys.Count == 1 && keys[0] == AgentKey.WIN))
            throw Error("MODIFIER_ONLY_HOTKEY", path);
        // Block these combinations even if the model adds another modifier/key or reorders the chord.
        if (distinct.Contains(AgentKey.CTRL) && distinct.Contains(AgentKey.ALT) &&
            (distinct.Contains(AgentKey.DELETE) || distinct.Contains(AgentKey.F8) || distinct.Contains(AgentKey.F9)))
            throw Error("RESERVED_HOTKEY", path);
        if (invalidModifierOrder)
            throw Error("MODIFIER_ORDER", path);
        return keys.MoveToImmutable();
    }

    private static bool IsModifier(AgentKey key) => key is AgentKey.CTRL or AgentKey.ALT or AgentKey.SHIFT or AgentKey.WIN;

    private static ImmutableArray<Evidence> EvidenceArray(JsonElement element, string path, string currentFrameId)
    {
        Array(element, path, 1, 5);
        var evidence = ImmutableArray.CreateBuilder<Evidence>(element.GetArrayLength());
        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            string itemPath = path + "[" + (index++).ToString(CultureInfo.InvariantCulture) + "]";
            Object(item, itemPath, "frameId", "region", "appName", "observedText", "interpretation", "sourceUrl", "uncertainty");
            string frameId = String(item, "frameId", itemPath, ProtocolLimits.FrameId);
            if (!StringComparer.Ordinal.Equals(frameId, currentFrameId))
                throw Error("EVIDENCE_FRAME_MISMATCH", itemPath + ".frameId");
            NormalizedRect? region = Optional(item, "region") is { } regionElement
                ? Rect(regionElement, itemPath + ".region") : null;
            evidence.Add(new Evidence(frameId, region,
                String(item, "appName", itemPath, ProtocolLimits.EvidenceAppName),
                String(item, "observedText", itemPath, ProtocolLimits.EvidenceText, allowEmpty: true),
                String(item, "interpretation", itemPath, ProtocolLimits.EvidenceText),
                OptionalString(item, "sourceUrl", itemPath, ProtocolLimits.SourceUrl),
                OptionalString(item, "uncertainty", itemPath, ProtocolLimits.Uncertainty)));
        }
        return evidence.MoveToImmutable();
    }

    private static NormalizedPoint Coordinates(JsonElement element, string path, string x, string y) =>
        new(Number(Required(element, x, path), path + "." + x), Number(Required(element, y, path), path + "." + y));

    private static NormalizedPoint Point(JsonElement element, string path)
    {
        Object(element, path, "x", "y");
        return Coordinates(element, path, "x", "y");
    }

    private static NormalizedRect Rect(JsonElement element, string path)
    {
        Object(element, path, "x0", "y0", "x1", "y1");
        NormalizedPoint start = Coordinates(element, path, "x0", "y0");
        NormalizedPoint end = Coordinates(element, path, "x1", "y1");
        if (start.X >= end.X || start.Y >= end.Y)
            throw Error("EMPTY_OR_REVERSED_RECT", path);
        return new NormalizedRect(start.X, start.Y, end.X, end.Y);
    }

    private static string Discriminator(JsonElement element, string name, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Error("EXPECTED_OBJECT", path);
        // Enumerate before selecting a branch: GetProperty alone silently chooses the last duplicate.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            ValidateUnicode(property.Name, path, allowControls: false);
            if (!names.Add(property.Name))
                throw Error("DUPLICATE_FIELD", path);
        }
        return String(element, name, path, 40);
    }

    private static void Object(JsonElement element, string path, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Error("EXPECTED_OBJECT", path);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            ValidateUnicode(property.Name, path, allowControls: false);
            if (!names.Add(property.Name))
                throw Error("DUPLICATE_FIELD", path);
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Error("UNKNOWN_FIELD", path);
        }
    }

    private static JsonElement Required(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            throw Error("MISSING_FIELD", path + "." + name);
        if (value.ValueKind == JsonValueKind.Null)
            throw Error("NULL_FIELD", path + "." + name);
        return value;
    }

    private static JsonElement? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string String(JsonElement element, string name, string path, int maximum, bool allowEmpty = false) =>
        String(Required(element, name, path), path + "." + name, maximum, allowEmpty);

    private static string? OptionalString(JsonElement element, string name, string path, int maximum) =>
        Optional(element, name) is { } value ? String(value, path + "." + name, maximum, allowEmpty: true) : null;

    private static string String(JsonElement element, string path, int maximum, bool allowEmpty = false)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Error("EXPECTED_STRING", path);
        string value;
        try { value = element.GetString()!; }
        catch (InvalidOperationException) { throw Error("INVALID_UNICODE", path); }
        if (value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw Error("INVALID_STRING_LENGTH", path);
        ValidateUnicode(value, path, allowControls: false);
        return value;
    }

    private static void ValidateUnicode(string value, string path, bool allowControls)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (char.IsHighSurrogate(character))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[++i]))
                    throw Error("INVALID_UNICODE", path);
            }
            else if (char.IsLowSurrogate(character))
                throw Error("INVALID_UNICODE", path);
            // Unicode line/paragraph separators also break the single-line text/description contract.
            else if (!allowControls && (char.IsControl(character) || character is '\u2028' or '\u2029'))
                throw Error("CONTROL_CHARACTER", path);
        }
    }

    private static bool Boolean(JsonElement element, string name, string path)
    {
        JsonElement value = Required(element, name, path);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error("EXPECTED_BOOLEAN", path + "." + name);
        return value.GetBoolean();
    }

    private static int Integer(JsonElement element, string path, int min, int max) =>
        (int)Long(element, path, min, max);

    private static long Long(JsonElement element, string path, long min, long max)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long value))
            throw Error("EXPECTED_INTEGER", path);
        if (value < min || value > max)
            throw Error("OUT_OF_RANGE", path);
        return value;
    }

    private static double Number(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double value))
            throw Error("EXPECTED_NUMBER", path);
        if (!double.IsFinite(value) || value is < 0 or > 1000 || !NormalizedLiteralIsInRange(element.GetRawText()))
            throw Error("OUT_OF_RANGE", path);
        return value;
    }

    private static bool NormalizedLiteralIsInRange(ReadOnlySpan<char> literal)
    {
        // Double conversion rounds 1000.0000000000000001 to 1000 and -1e-9999 to -0.
        // Check the exact decimal magnitude before accepting either rounded boundary.
        int exponentPosition = literal.IndexOfAny('e', 'E');
        ReadOnlySpan<char> mantissa = exponentPosition < 0 ? literal : literal[..exponentPosition];
        ReadOnlySpan<char> exponent = exponentPosition < 0 ? [] : literal[(exponentPosition + 1)..];
        bool negative = mantissa[0] == '-';
        if (negative)
            mantissa = mantissa[1..];
        int decimalPosition = mantissa.IndexOf('.');
        int fractionalDigits = decimalPosition < 0 ? 0 : mantissa.Length - decimalPosition - 1;
        int significantDigits = 0;
        char firstSignificant = '0';
        bool nonzeroTail = false;
        foreach (char digit in mantissa)
        {
            if (digit == '.' || (significantDigits == 0 && digit == '0'))
                continue;
            if (significantDigits == 0)
                firstSignificant = digit;
            else
                nonzeroTail |= digit != '0';
            significantDigits++;
        }
        if (significantDigits == 0)
            return true;
        if (negative)
            return false;
        bool exponentNegative = !exponent.IsEmpty && exponent[0] == '-';
        if (!exponent.IsEmpty && exponent[0] is '+' or '-')
            exponent = exponent[1..];
        int power = 0;
        // Saturate beyond twice the bounded document length: no possible mantissa can offset it.
        foreach (char digit in exponent)
            power = Math.Min(ProtocolLimits.JsonUtf8Bytes * 2, power * 10 + digit - '0');
        if (exponentNegative)
            power = -power;
        int magnitude = power - fractionalDigits + significantDigits - 1;
        return magnitude < 3 || (magnitude == 3 && firstSignificant == '1' && !nonzeroTail);
    }

    private static void Array(JsonElement element, string path, int min, int max)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Error("EXPECTED_ARRAY", path);
        if (element.GetArrayLength() < min || element.GetArrayLength() > max)
            throw Error("INVALID_ARRAY_LENGTH", path);
    }

    private static bool IsKnownKind(string kind) => kind is "act" or "inspect" or "select_monitor" or "wait" or "ask_user" or
        "prepare_message" or "finish" or "fail" or "draft_focus_check" or "draft_check" or "commit_check" or "commit_result";

    private static bool IsAllowedKind(string kind, ProposalRequestKind requestKind) => requestKind switch
    {
        ProposalRequestKind.General => kind is "act" or "inspect" or "select_monitor" or "wait" or "ask_user" or "prepare_message" or "finish" or "fail",
        ProposalRequestKind.DraftFocusCheck => kind == "draft_focus_check",
        ProposalRequestKind.DraftCheck => kind == "draft_check",
        ProposalRequestKind.CommitCheck => kind == "commit_check",
        ProposalRequestKind.CommitResult => kind == "commit_result",
        _ => false
    };

    private static ProtocolValidationException Error(string code, string path) => new(code, path);
}
