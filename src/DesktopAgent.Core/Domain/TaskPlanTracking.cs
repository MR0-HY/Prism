using System.Collections.Immutable;
using DesktopAgent.Core.Protocol;

namespace DesktopAgent.Core.Domain;

/// <summary>Model reports tied to observations, not independent proof or authority.</summary>
public sealed record TaskStepReport(string StepId, string Status, string Observation, Lease Lease, string FrameId)
{
    public bool Verified => false;
    public override string ToString() => $"TaskStepReport({StepId}, {Status}, contents omitted)";
}
public static class TaskPlanTracking
{
    public static void Validate(TaskInterpretation? plan, ImmutableArray<TaskStepReport> reports, Lease lease)
    {
        if (reports.IsDefault || reports.Length > 8 || reports.Select(r => r?.StepId).Distinct().Count() != reports.Length ||
            reports.Any(r => r is null || r.Lease.TaskId != lease.TaskId || r.Lease.Epoch < 0 || r.Lease.Epoch > lease.Epoch ||
                !Known(plan, r.StepId) || r.Status is not ("pending" or "active" or "completed") ||
                !ValidText(r.Observation, 300) || !ValidText(r.FrameId, 80)))
            throw new ArgumentException("INVALID_PLAN_PROGRESS");
    }
    private static bool Known(TaskInterpretation? plan, string id) => plan is not null && plan.Steps.Any(s => s.Id == id);
    private static bool ValidText(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit) return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]) || value[i] is '\u2028' or '\u2029') return false;
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
    public static ImmutableArray<TaskStepReport> Apply(TaskInterpretation? plan, ImmutableArray<TaskStepReport> current,
        ImmutableArray<PlanStepUpdate> updates, Frame frame)
    {
        Validate(plan, current, frame.Lease);
        if (updates.IsDefault || updates.Length > 8 || updates.Select(u => u?.StepId).Distinct().Count() != updates.Length ||
            updates.Any(u => u is null || !Known(plan, u.StepId) || u.Status is not ("pending" or "active" or "completed") ||
                !ValidText(u.Observation, 300))) throw new ArgumentException("INVALID_PLAN_UPDATE");
        var result = current.ToDictionary(r => r.StepId, StringComparer.Ordinal);
        foreach (var update in updates) result[update.StepId] = new(update.StepId, update.Status, update.Observation, frame.Lease, frame.Id);
        return [.. result.Values.OrderBy(r => r.StepId, StringComparer.Ordinal)];
    }
    public static bool AllCompleted(TaskInterpretation? plan, ImmutableArray<TaskStepReport> reports) =>
        plan is { Steps.IsEmpty: false } && plan.Steps.All(s => reports.Any(r => r.StepId == s.Id && r.Status == "completed"));
    public static string Describe(TaskInterpretation? plan, ImmutableArray<TaskStepReport> reports)
    {
        if (plan is null) return "";
        if (plan.Steps.IsEmpty) return plan.Reply;
        return plan.Reply + "\n\n任务计划\n" + string.Join("\n", plan.Steps.Select(step =>
        {
            var report = reports.FirstOrDefault(r => r.StepId == step.Id);
            string state = report?.Status switch { "completed" => "已核对", "active" => "进行中", _ => "待完成" };
            return $"{step.Id[1..]}. [{state}] {step.Title}" + (report is null ? "" : "\n   " + report.Observation);
        }));
    }
}
