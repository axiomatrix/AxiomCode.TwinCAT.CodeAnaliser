using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Second-pass alarm severity resolver.
///
/// The first-pass <see cref="AlarmAnalyzer"/> analyses each POU in isolation. Many
/// alarms therefore fall into the <see cref="UnresolvedReason.BaseClass"/> bucket
/// because the POU declaring the alarm extends a base class directly while the
/// actual <c>_AlarmsPresent...</c> severity blocks live in a descendant or in a
/// composing module further down the ISA-88 tree.
///
/// This resolver builds a project-wide index of every <c>_AlarmsPresent...</c>
/// assignment by walking <em>all</em> method bodies in <em>all</em> POUs, then
/// re-binds unresolved alarms whose name appears unambiguously in exactly one
/// severity bucket project-wide.
///
/// The result populates <see cref="UnresolvedReason.None"/> for the resolved
/// alarms and tags the <see cref="AlarmInfo.UnresolvedReasonText"/> with a
/// "(resolved by deep scan)" hint so reviewers can see the resolution path.
///
/// Ambiguous bindings (same alarm name appearing in multiple severity buckets)
/// are deliberately left Unresolved — flagging an ambiguity is more useful than
/// silently picking one.
/// </summary>
public static class DeepAlarmResolver
{
    private static readonly Regex SeverityBlockRegex = new(
        @"_AlarmsPresent(Critical|Process|Advisory|Information)\s*:=\s*([\s\S]*?);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlarmRefRegex = new(
        @"\b(ALM_[A-Za-z_]\w*)\.Latched\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Run after <see cref="AlarmAnalyzer.Analyze"/>. Walks every POU's methods
    /// for <c>_AlarmsPresentX :=</c> blocks, builds a name-to-severity index,
    /// then rebinds eligible Unresolved alarms.
    /// </summary>
    public static void Resolve(TcProject project)
    {
        // 1. Build a project-wide name → severity index.
        // For each alarm name we keep the set of severities where it appears;
        // unique-set means we can safely rebind.
        var globalIndex = new Dictionary<string, HashSet<AlarmSeverity>>(StringComparer.OrdinalIgnoreCase);

        foreach (var pou in project.POUs.Values)
        {
            // Collect every method body in this POU
            var bodies = new List<string>();
            foreach (var m in pou.Methods)
                if (!string.IsNullOrEmpty(m.Body)) bodies.Add(m.Body);
            if (!string.IsNullOrEmpty(pou.RawImplementation)) bodies.Add(pou.RawImplementation);

            foreach (var body in bodies)
            {
                foreach (Match sev in SeverityBlockRegex.Matches(body))
                {
                    if (!TryParseSeverity(sev.Groups[1].Value, out var severity)) continue;
                    var blockText = sev.Groups[2].Value;

                    foreach (Match aref in AlarmRefRegex.Matches(blockText))
                    {
                        var name = aref.Groups[1].Value;
                        if (!globalIndex.TryGetValue(name, out var set))
                        {
                            set = new HashSet<AlarmSeverity>();
                            globalIndex[name] = set;
                        }
                        set.Add(severity);
                    }
                }
            }
        }

        // 2. Rebind eligible alarms.
        int resolvedCount = 0;
        int ambiguousCount = 0;

        foreach (var alarm in project.AllAlarms)
        {
            if (alarm.UnresolvedReason == UnresolvedReason.None) continue;
            if (alarm.UnresolvedReason != UnresolvedReason.BaseClass &&
                alarm.UnresolvedReason != UnresolvedReason.Missing) continue;

            if (!globalIndex.TryGetValue(alarm.InstanceName, out var severities)) continue;
            if (severities.Count == 0) continue;

            if (severities.Count == 1)
            {
                var resolved = severities.Single();
                var prevReason = alarm.UnresolvedReason;
                alarm.Severity = resolved;
                alarm.UnresolvedReason = UnresolvedReason.None;
                alarm.UnresolvedReasonText = $"(was {prevReason}; resolved by deep scan to {resolved})";
                resolvedCount++;
            }
            else
            {
                // Multiple distinct severities — leave as Unresolved but enrich the reason text.
                var seen = string.Join(", ", severities.OrderBy(s => s));
                alarm.UnresolvedReasonText =
                    $"{alarm.UnresolvedReasonText} | deep scan ambiguous (appears as: {seen})";
                ambiguousCount++;
            }
        }

        // 3. Refresh the project summary counts.
        var s = project.Summary;
        s.TotalAlarms       = project.AllAlarms.Count;
        s.CriticalAlarms    = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Critical);
        s.ProcessAlarms     = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Process);
        s.AdvisoryAlarms    = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Advisory);
        s.InformationAlarms = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Information);
        s.UnresolvedAlarms  = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Unresolved);
        s.AlarmsResolvedByDeepScan = resolvedCount;
        s.AlarmsAmbiguousAfterDeepScan = ambiguousCount;
        s.SeverityBlocksDetected = globalIndex.Values.Sum(set => set.Count);
        s.ProjectUsesSeveritySplit = globalIndex.Count > 0;
    }

    private static bool TryParseSeverity(string token, out AlarmSeverity severity)
    {
        switch (token.ToUpperInvariant())
        {
            case "CRITICAL":    severity = AlarmSeverity.Critical;    return true;
            case "PROCESS":     severity = AlarmSeverity.Process;     return true;
            case "ADVISORY":    severity = AlarmSeverity.Advisory;    return true;
            case "INFORMATION": severity = AlarmSeverity.Information; return true;
            default:            severity = AlarmSeverity.Unresolved;  return false;
        }
    }
}
