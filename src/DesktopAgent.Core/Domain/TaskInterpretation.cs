using System.Collections.Immutable;
using System.Text.Json;

namespace DesktopAgent.Core.Domain;

/// <summary>A model's tentative operational goal and public explanation, never user authorization or evidence of completion.</summary>
public sealed record TaskInterpretation(string Goal, string Reply)
{
    public const int MaximumGoalLength = 4000;
    public const int MaximumReplyLength = 1000;
    public const int MaximumSteps = 8;
    public const int MaximumStepTitleLength = 160;
    public const int MaximumStepCheckLength = 300;
    public const int MaximumCompletionCheckLength = 600;
    private const int MaximumJsonLength = 32 * 1024;
    public ImmutableArray<TaskPlanStep> Steps { get; init; } = [];
    public string? CompletionCheck { get; init; }

    public static TaskInterpretation Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonLength || !ValidCharacters(json))
            throw new TaskInterpretationException();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new TaskInterpretationException();
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("goal" or "reply" or "steps" or "completionCheck") || !fields.Add(property.Name))
                    throw new TaskInterpretationException();
            if (!fields.Contains("goal") || !fields.Contains("reply") || fields.Contains("steps") != fields.Contains("completionCheck"))
                throw new TaskInterpretationException();
            var result = new TaskInterpretation(Text(root, "goal", MaximumGoalLength), Text(root, "reply", MaximumReplyLength));
            if (fields.Contains("steps"))
            {
                var steps = root.GetProperty("steps");
                if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > MaximumSteps)
                    throw new TaskInterpretationException();
                var parsed = ImmutableArray.CreateBuilder<TaskPlanStep>();
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object) throw new TaskInterpretationException();
                    var stepFields = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in step.EnumerateObject())
                        if (property.Name is not ("id" or "title" or "completionCheck") || !stepFields.Add(property.Name))
                            throw new TaskInterpretationException();
                    if (stepFields.Count != 3) throw new TaskInterpretationException();
                    parsed.Add(new(Text(step, "id", 3), Text(step, "title", MaximumStepTitleLength), Text(step, "completionCheck", MaximumStepCheckLength)));
                }
                result = result with { Steps = parsed.ToImmutable(), CompletionCheck = Text(root, "completionCheck", MaximumCompletionCheckLength) };
            }
            result.Validate();
            return result;
        }
        // Parser diagnostics may quote private model output. Keep only the stable public code.
        catch (JsonException) { throw new TaskInterpretationException(); }
        catch (InvalidOperationException) { throw new TaskInterpretationException(); }
        catch (ArgumentException) { throw new TaskInterpretationException(); }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Goal) || Goal.Length > MaximumGoalLength || !ValidCharacters(Goal) ||
            string.IsNullOrWhiteSpace(Reply) || Reply.Length > MaximumReplyLength || !ValidCharacters(Reply))
            throw new TaskInterpretationException();
        if (Steps.IsDefault || Steps.Length > MaximumSteps || Steps.IsEmpty != (CompletionCheck is null))
            throw new TaskInterpretationException();
        if (CompletionCheck is not null && !ValidText(CompletionCheck, MaximumCompletionCheckLength))
            throw new TaskInterpretationException();
        for (int i = 0; i < Steps.Length; i++)
            if (Steps[i] is not { } step || step.Id != "s" + (i + 1) ||
                !ValidText(step.Title, MaximumStepTitleLength) || !ValidText(step.CompletionCheck, MaximumStepCheckLength))
                throw new TaskInterpretationException();
    }

    private static bool ValidText(string? text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && ValidCharacters(text);

    private static string Text(JsonElement root, string field, int maximum)
    {
        var value = root.GetProperty(field);
        if (value.ValueKind != JsonValueKind.String) throw new TaskInterpretationException();
        string text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || !ValidCharacters(text))
            throw new TaskInterpretationException();
        return text.Trim();
    }

    private static bool ValidCharacters(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t')) return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }

    public override string ToString() => "TaskInterpretation(untrusted model content omitted)";
}

/// <summary>An unverified model plan. Completion requires later observations, never the existence of this record.</summary>
public sealed record TaskPlanStep(string Id, string Title, string CompletionCheck)
{
    public override string ToString() => "TaskPlanStep(untrusted model content omitted)";
}

public sealed class TaskInterpretationException() : Exception("模型任务理解格式无效。")
{
    public string Code => "INVALID_TASK_INTERPRETATION";
}
