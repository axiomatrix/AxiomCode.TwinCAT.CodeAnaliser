using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnaliser.Models;

namespace AxiomCode.TwinCAT.CodeAnaliser.Services;

/// <summary>
/// Extracts and categorises all DM_TriggeredLatch alarms from the object tree.
/// Populates <see cref="TcProject.AllAlarms"/> and each <see cref="ObjectTreeNode.Alarms"/> list.
/// </summary>
public static class AlarmAnalyzer
{
    // ── Severity assignment regex ──────────────────────────────────────
    // Matches _AlarmsPresentCritical := ...; blocks (multiline, terminated by semicolon)
    private static readonly Regex SeverityBlockRegex = new(
        @"_AlarmsPresent(Critical|Process|Advisory|Information)\s*:=\s*([\s\S]*?);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches ALM_Xxx.Latched within a severity block
    private static readonly Regex LatchedRefRegex = new(
        @"(ALM_\w+)\.Latched",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Trigger / delay extraction ─────────────────────────────────────
    // ALM_Xxx.Trigger := <expression>;
    private static readonly Regex TriggerRegex = new(
        @"(ALM_\w+)\.Trigger\s*:=\s*([\s\S]*?);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ALM_Xxx.TriggerDelay_ms := <value>;
    private static readonly Regex DelayRegex = new(
        @"(ALM_\w+)\.TriggerDelay_ms\s*:=\s*(\d+)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── CamelCase splitter ─────────────────────────────────────────────
    private static readonly Regex CamelSplitRegex = new(
        @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
        RegexOptions.Compiled);

    // ── Base class detection ───────────────────────────────────────────
    private static readonly HashSet<string> BaseClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CM_BASECLASS",
        "DM_BASECLASS",
        "EM_BASECLASS",
        "UM_BASECLASS"
    };

    /// <summary>
    /// Main entry point. Call after <see cref="ObjectTreeBuilder.Build"/> and
    /// <see cref="InheritanceResolver.Resolve"/> have run.
    /// </summary>
    public static void Analyze(TcProject project)
    {
        project.AllAlarms.Clear();

        foreach (var root in project.ObjectTree)
            WalkTree(root, project);
    }

    // ── Tree walker ────────────────────────────────────────────────────

    private static void WalkTree(ObjectTreeNode node, TcProject project)
    {
        node.Alarms.Clear();

        if (project.POUs.TryGetValue(node.TypeName, out var pou))
            ExtractAlarms(node, pou, project);

        foreach (var child in node.Children)
            WalkTree(child, project);
    }

    // ── Core extraction ────────────────────────────────────────────────

    private static void ExtractAlarms(ObjectTreeNode node, TcPou pou, TcProject project)
    {
        // Find all ALM_ variables of type DM_TriggeredLatch
        var alarmVars = pou.AllVariables
            .Where(v => v.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)
                        && v.DataType.Equals("DM_TriggeredLatch", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (alarmVars.Count == 0)
            return;

        // Gather _Alarms method bodies from this POU and its inheritance chain
        var alarmsMethodBodies = CollectAlarmsMethodBodies(pou, project);
        var allMethodBodies = CollectAllMethodBodies(pou, project);

        // Build severity map from _Alarms method(s)
        var severityMap = BuildSeverityMap(alarmsMethodBodies);

        // Build trigger / delay maps from all method bodies
        var triggerMap = BuildTriggerMap(allMethodBodies);
        var delayMap = BuildDelayMap(allMethodBodies);

        // Determine if this is a bare base class (extends CM_BASECLASS etc. directly)
        bool isDirectBaseClass = IsDirectBaseClassExtension(pou);

        // Check if _Alarms method exists at all (on this POU or inherited)
        bool hasAlarmsMethod = alarmsMethodBodies.Count > 0;
        node.HasAlarmsMethod = hasAlarmsMethod;

        foreach (var v in alarmVars)
        {
            var alarm = new AlarmInfo
            {
                InstanceName = v.Name,
                ModulePath = node.FullPath,
                ModuleType = node.TypeName,
                VariablePath = node.FullPath + "." + v.Name + "._Latched",
                Condition = FormatConditionName(v.Name),
            };

            // Try to resolve severity
            if (severityMap.TryGetValue(v.Name, out var severity))
            {
                alarm.Severity = severity;
                alarm.UnresolvedReason = UnresolvedReason.None;
            }
            else
            {
                alarm.Severity = AlarmSeverity.Unresolved;
                alarm.UnresolvedReason = DetermineUnresolvedReason(
                    v.Name, pou, isDirectBaseClass, hasAlarmsMethod, allMethodBodies);
                alarm.UnresolvedReasonText = FormatUnresolvedReason(alarm.UnresolvedReason, pou.Name);
            }

            // Extract trigger condition
            if (triggerMap.TryGetValue(v.Name, out var trigger))
                alarm.TriggerCondition = trigger.Trim();

            // Extract delay
            if (delayMap.TryGetValue(v.Name, out var delayMs))
                alarm.TriggerDelayMs = delayMs;

            node.Alarms.Add(alarm);
            project.AllAlarms.Add(alarm);
        }
    }

    // ── Severity map builder ───────────────────────────────────────────

    /// <summary>
    /// Parses all _AlarmsPresentXxx := ...; blocks and maps each ALM_ name to its severity.
    /// </summary>
    private static Dictionary<string, AlarmSeverity> BuildSeverityMap(List<string> alarmsMethodBodies)
    {
        var map = new Dictionary<string, AlarmSeverity>(StringComparer.OrdinalIgnoreCase);

        foreach (var body in alarmsMethodBodies)
        {
            foreach (Match blockMatch in SeverityBlockRegex.Matches(body))
            {
                var severityName = blockMatch.Groups[1].Value;
                var block = blockMatch.Groups[2].Value;

                if (!Enum.TryParse<AlarmSeverity>(severityName, ignoreCase: true, out var severity))
                    continue;

                foreach (Match alarmMatch in LatchedRefRegex.Matches(block))
                {
                    var almName = alarmMatch.Groups[1].Value;
                    // First match wins (don't overwrite if already categorised at a higher level)
                    map.TryAdd(almName, severity);
                }
            }
        }

        return map;
    }

    // ── Trigger / delay map builders ───────────────────────────────────

    private static Dictionary<string, string> BuildTriggerMap(List<string> methodBodies)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var body in methodBodies)
        {
            foreach (Match m in TriggerRegex.Matches(body))
            {
                var almName = m.Groups[1].Value;
                var expression = m.Groups[2].Value.Trim();
                // Keep last assignment (in case of overrides in derived class)
                map[almName] = expression;
            }
        }
        return map;
    }

    private static Dictionary<string, int> BuildDelayMap(List<string> methodBodies)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var body in methodBodies)
        {
            foreach (Match m in DelayRegex.Matches(body))
            {
                var almName = m.Groups[1].Value;
                if (int.TryParse(m.Groups[2].Value, out var ms))
                    map[almName] = ms;
            }
        }
        return map;
    }

    // ── Collect method bodies ──────────────────────────────────────────

    /// <summary>
    /// Collects the body text of all methods named "_Alarms" across the POU
    /// and its full inheritance chain.
    /// </summary>
    private static List<string> CollectAlarmsMethodBodies(TcPou pou, TcProject project)
    {
        var bodies = new List<string>();

        // This POU's _Alarms method
        var localMethod = pou.Methods.FirstOrDefault(
            m => m.Name.Equals("_Alarms", StringComparison.OrdinalIgnoreCase));
        if (localMethod != null)
            bodies.Add(localMethod.Body);

        // Walk inheritance chain (base classes)
        foreach (var ancestorName in pou.InheritanceChain)
        {
            if (project.POUs.TryGetValue(ancestorName, out var ancestorPou))
            {
                var ancestorMethod = ancestorPou.Methods.FirstOrDefault(
                    m => m.Name.Equals("_Alarms", StringComparison.OrdinalIgnoreCase));
                if (ancestorMethod != null)
                    bodies.Add(ancestorMethod.Body);
            }
        }

        return bodies;
    }

    /// <summary>
    /// Collects all method bodies (including RawImplementation) across
    /// the POU and its inheritance chain, for trigger/delay extraction.
    /// </summary>
    private static List<string> CollectAllMethodBodies(TcPou pou, TcProject project)
    {
        var bodies = new List<string>();

        // This POU
        AddPouBodies(pou, bodies);

        // Inheritance chain
        foreach (var ancestorName in pou.InheritanceChain)
        {
            if (project.POUs.TryGetValue(ancestorName, out var ancestorPou))
                AddPouBodies(ancestorPou, bodies);
        }

        return bodies;
    }

    private static void AddPouBodies(TcPou pou, List<string> bodies)
    {
        if (!string.IsNullOrWhiteSpace(pou.RawImplementation))
            bodies.Add(pou.RawImplementation);

        foreach (var method in pou.Methods)
        {
            if (!string.IsNullOrWhiteSpace(method.Body))
                bodies.Add(method.Body);
        }
    }

    // ── Unresolved reason determination ────────────────────────────────

    private static bool IsDirectBaseClassExtension(TcPou pou)
    {
        if (string.IsNullOrEmpty(pou.ExtendsType))
            return false;

        return BaseClassNames.Contains(pou.ExtendsType);
    }

    private static UnresolvedReason DetermineUnresolvedReason(
        string alarmName,
        TcPou pou,
        bool isDirectBaseClass,
        bool hasAlarmsMethod,
        List<string> allMethodBodies)
    {
        // 1. Direct base class extension — only has _AlarmsPresent, no severity split
        if (isDirectBaseClass)
            return UnresolvedReason.BaseClass;

        // 2. No _Alarms method at all
        if (!hasAlarmsMethod)
            return UnresolvedReason.NoMethod;

        // 3. Check if alarm name appears anywhere in method bodies (not just declarations)
        bool referencedAnywhere = allMethodBodies
            .Any(body => body.Contains(alarmName, StringComparison.OrdinalIgnoreCase));

        if (!referencedAnywhere)
            return UnresolvedReason.Dead;

        // 4. Referenced in code but not in any severity block
        return UnresolvedReason.Missing;
    }

    private static string FormatUnresolvedReason(UnresolvedReason reason, string pouName)
    {
        return reason switch
        {
            UnresolvedReason.BaseClass =>
                $"{pouName} extends a base class directly — severity blocks not split",
            UnresolvedReason.NoMethod =>
                $"No _Alarms method found in {pouName} or its base classes",
            UnresolvedReason.Dead =>
                "Alarm variable declared but never referenced in any method body",
            UnresolvedReason.Missing =>
                "Alarm is referenced in code but not assigned to any severity block",
            _ => ""
        };
    }

    // ── Condition name formatting ──────────────────────────────────────

    /// <summary>
    /// Strips "ALM_" prefix and splits CamelCase:
    /// "ALM_CommandAborted" → "Command Aborted"
    /// "ALM_PowderLevelLowLow" → "Powder Level Low Low"
    /// </summary>
    private static string FormatConditionName(string alarmName)
    {
        var stripped = alarmName.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)
            ? alarmName[4..]
            : alarmName;

        return CamelSplitRegex.Replace(stripped, " ");
    }
}
