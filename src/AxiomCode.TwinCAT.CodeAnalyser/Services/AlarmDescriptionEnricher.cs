using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Populates <see cref="AlarmInfo.Description"/> with a human-readable
/// explanation of what each alarm actually means.
///
/// Two-stage strategy:
///   1. <see cref="ApplyHeuristic"/> runs at analysis time. It builds the
///      best description it can from the alarm's name (CamelCase split),
///      trigger condition (translated to plain English), severity, and
///      module context. Every alarm gets a description even before any
///      AI runs.
///   2. <see cref="ApplyOverrides"/> runs later (e.g. after an LLM-driven
///      interpretation pass) with a lookup of AI-supplied descriptions
///      keyed by alarm name. Where a richer description is available it
///      replaces the heuristic one.
///
/// The pipeline records where each description came from in
/// <see cref="AlarmInfo.DescriptionSource"/> so the UI can flag
/// AI-derived prose distinctly from the deterministic baseline.
/// </summary>
public static class AlarmDescriptionEnricher
{
    /// <summary>Run after AlarmAnalyzer + DeepAlarmResolver — pure deterministic.</summary>
    public static void ApplyHeuristic(TcProject project)
    {
        foreach (var alarm in project.AllAlarms)
        {
            if (!string.IsNullOrEmpty(alarm.Description)) continue;
            alarm.Description       = BuildHeuristicDescription(alarm);
            alarm.DescriptionSource = "heuristic";
        }
    }

    /// <summary>
    /// Apply an external lookup (typically from AI-interpreted module dossiers)
    /// over the heuristic descriptions. The lookup is keyed by alarm instance
    /// name (case-insensitive); when a match is found and carries non-empty
    /// description text, it replaces the heuristic version. Optional action
    /// text and severity hint are also captured.
    /// </summary>
    public sealed record AlarmOverride(
        string? Description,
        string? RecommendedAction,
        string? AiSeverity);

    public static int ApplyOverrides(
        TcProject project,
        IReadOnlyDictionary<string, AlarmOverride> overrides)
    {
        if (overrides.Count == 0) return 0;
        int applied = 0;
        foreach (var alarm in project.AllAlarms)
        {
            if (!overrides.TryGetValue(alarm.InstanceName, out var ov)) continue;
            if (!string.IsNullOrWhiteSpace(ov.Description))
            {
                alarm.Description       = ov.Description.Trim();
                alarm.DescriptionSource = "ai-interpretation";
                applied++;
            }
            if (!string.IsNullOrWhiteSpace(ov.RecommendedAction))
                alarm.RecommendedAction = ov.RecommendedAction.Trim();
        }
        return applied;
    }

    /// <summary>
    /// Build a one-sentence description from name, trigger condition, severity,
    /// and module type. Designed to be useful even when an AI hasn't run yet —
    /// the heuristic should at least answer "what is this and when does it raise".
    /// </summary>
    private static string BuildHeuristicDescription(AlarmInfo alarm)
    {
        var name = StripPrefix(alarm.InstanceName);
        var humanName = SplitCamelCase(name);
        var sb = new System.Text.StringBuilder();

        // Lead phrase derived from severity tier.
        sb.Append(alarm.Severity switch
        {
            AlarmSeverity.Critical    => "Critical fault",
            AlarmSeverity.Process     => "Process condition",
            AlarmSeverity.Advisory    => "Advisory",
            AlarmSeverity.Information => "Information event",
            _                         => "Alarm",
        });
        sb.Append($" — {humanName.ToLower()}.");

        // Trigger summary
        var triggerSummary = SummariseTrigger(alarm);
        if (!string.IsNullOrEmpty(triggerSummary))
            sb.Append(" Raised when ").Append(triggerSummary).Append(".");

        // Delay note
        if (alarm.TriggerDelayMs.HasValue && alarm.TriggerDelayMs.Value > 0)
            sb.Append($" The condition must persist for at least {alarm.TriggerDelayMs}\u00a0ms before the alarm latches.");

        // Module context
        if (!string.IsNullOrEmpty(alarm.ModuleType))
            sb.Append($" Owned by `{alarm.ModuleType}`.");

        // Resolution status caveat
        if (alarm.UnresolvedReason == UnresolvedReason.BaseClass)
            sb.Append(" Severity inferred from inherited framework — confirm during review.");
        else if (alarm.UnresolvedReason == UnresolvedReason.Missing)
            sb.Append(" Severity binding not located in source — review required.");
        else if (alarm.UnresolvedReason == UnresolvedReason.NoMethod)
            sb.Append(" No `_Alarms` method present in the owning module — severity cannot be determined statically.");
        else if (alarm.UnresolvedReason == UnresolvedReason.Dead)
            sb.Append(" Variable declared but never referenced in any method body — likely dead code.");

        return sb.ToString().Trim();
    }

    /// <summary>Translate a TwinCAT ST trigger condition into plain English where feasible.</summary>
    private static string SummariseTrigger(AlarmInfo alarm)
    {
        var cond = (alarm.TriggerCondition ?? "").Trim();
        if (string.IsNullOrEmpty(cond)) return "";

        // Compress whitespace
        cond = Regex.Replace(cond, @"\s+", " ");
        var lower = cond.ToLowerInvariant();

        // Common patterns we can summarise
        if (lower.Contains("i_inverted") || Regex.IsMatch(lower, @"\bnot\b\s+[a-z_][a-z0-9_]*\.i\b"))
            return "the digital input feeding this alarm reads inactive";
        if (Regex.IsMatch(lower, @"\.i\b") && !lower.Contains("not "))
            return "the digital input feeding this alarm reads active";
        if (lower.Contains("> ") && lower.Contains("targetflowrate"))
            return "the measured flow rate deviates from the configured target by more than the tolerance";
        if (lower.Contains("alarmspresent"))
            return "any of the supervised submodules report an alarm";
        if (lower.Contains("guards") || lower.Contains("safety"))
            return "the safety guards are open or interlocks are broken";
        if (lower.Contains("timeout") || lower.Contains("time out"))
            return "an operation exceeds its allowed time window";
        if (lower.Contains("notreadyto") || lower.Contains("noterror") || lower.Contains("not ready"))
            return "an upstream module reports a not-ready condition";

        // Generic fallback: quote the original condition truncated
        return "the condition `" + Truncate(cond, 80) + "` evaluates true";
    }

    private static string SplitCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var spaced = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return spaced.Replace('_', ' ').Trim();
    }

    private static string StripPrefix(string name)
    {
        if (name.StartsWith("ALM_WARNING_", StringComparison.OrdinalIgnoreCase)) return name.Substring(12);
        if (name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)) return name.Substring(4);
        return name;
    }

    private static string Truncate(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n) + "…";
}
