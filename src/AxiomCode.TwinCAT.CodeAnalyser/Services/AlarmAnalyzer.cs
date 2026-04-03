using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

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

        // Phase 1: Walk the object tree (instantiated alarms with full paths)
        foreach (var root in project.ObjectTree)
            WalkTree(root, project);

        // Phase 2: Scan ALL POUs for alarm definitions not found via the tree.
        // This catches alarms in POUs that aren't instantiated in GVL_Objects,
        // templates, base classes, and utility FBs.
        var treePouTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectTreeTypes(project.ObjectTree, treePouTypes);

        foreach (var kvp in project.POUs)
        {
            var pou = kvp.Value;
            if (treePouTypes.Contains(pou.Name))
                continue; // Already processed via tree

            var alarmVars = pou.AllVariables
                .Where(v => v.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)
                            && v.DataType.Equals("DM_TriggeredLatch", StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Also scan struct members for ALM_ variables
            var structAlarms = FindStructAlarms(pou, project, pou.Name);
            alarmVars.AddRange(structAlarms.Select(sa => sa.Variable));

            if (alarmVars.Count == 0)
                continue;

            var alarmsMethodBodies = CollectAlarmsMethodBodies(pou, project);
            var allMethodBodies = CollectAllMethodBodies(pou, project);
            var severityMap = BuildSeverityMap(alarmsMethodBodies);
            var triggerMap = BuildTriggerMap(allMethodBodies);
            var delayMap = BuildDelayMap(allMethodBodies);
            bool isDirectBaseClass = IsDirectBaseClassExtension(pou);
            bool hasAlarmsMethod = alarmsMethodBodies.Count > 0;

            foreach (var v in alarmVars)
            {
                // Skip if this exact alarm name+type combo is already in AllAlarms
                // (could happen if a derived class alarm was already captured via tree)
                if (project.AllAlarms.Any(a =>
                    a.InstanceName.Equals(v.Name, StringComparison.OrdinalIgnoreCase) &&
                    a.ModuleType.Equals(pou.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var alarm = new AlarmInfo
                {
                    InstanceName = v.Name,
                    ModulePath = pou.Name + " (definition)",
                    ModuleType = pou.Name,
                    VariablePath = pou.Name + "." + v.Name + "._Latched",
                    Condition = FormatConditionName(v.Name),
                    IsDefinitionOnly = true // Not instantiated in tree
                };

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

                if (triggerMap.TryGetValue(v.Name, out var trigger))
                    alarm.TriggerCondition = trigger.Trim();
                if (delayMap.TryGetValue(v.Name, out var delayMs))
                    alarm.TriggerDelayMs = delayMs;

                project.AllAlarms.Add(alarm);
            }
        }

        // Phase 3: Usage-inferred alarms — scan all method bodies in tree nodes
        // for patterns like memberVar.ALM_xxx.Trigger/Latched/Update to find
        // alarm instances on child FBs that aren't in the project source
        // (e.g. from compiled libraries).
        InferAlarmsFromUsage(project);
    }

    // ── Phase 3: Usage-inferred alarm scanner ────────────────────────

    // Matches: somePrefix.ALM_Name.Property  (captures prefix and ALM_Name)
    private static readonly Regex UsageAlarmRegex = new(
        @"([\w.]+)\.(ALM_\w+)\s*\.\s*(?:Trigger|Latched|Update|Inhibit|ResetLatch|TriggerDelay_ms|TriggerImmediately)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches: somePrefix.ALM_Name.Trigger := expression;
    private static readonly Regex UsageTriggerRegex = new(
        @"([\w.]+)\.(ALM_\w+)\s*\.\s*Trigger\s*:=\s*([\s\S]*?);",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches: somePrefix.ALM_Name.TriggerDelay_ms := value;
    private static readonly Regex UsageDelayRegex = new(
        @"([\w.]+)\.(ALM_\w+)\s*\.\s*TriggerDelay_ms\s*:=\s*(\d+)\s*;",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Scan all method bodies for alarm usage patterns (memberVar.ALM_xxx.Property)
    /// to discover alarm instances on child FBs from external libraries.
    /// For each tree node, finds alarm references not already captured and adds them.
    /// </summary>
    private static void InferAlarmsFromUsage(TcProject project)
    {
        // Build a set of already-known alarm paths for deduplication
        var knownPaths = new HashSet<string>(
            project.AllAlarms.Select(a => a.VariablePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var root in project.ObjectTree)
            InferFromNode(root, project, knownPaths);
    }

    private static void InferFromNode(ObjectTreeNode node, TcProject project, HashSet<string> knownPaths)
    {
        if (project.POUs.TryGetValue(node.TypeName, out var pou))
        {
            // Collect all method bodies for this POU
            var allBodies = CollectAllMethodBodies(pou, project);
            var alarmsMethodBodies = CollectAlarmsMethodBodies(pou, project);
            var severityMap = BuildSeverityMap(alarmsMethodBodies);

            // Find all memberVar.ALM_xxx references in method bodies
            var discovered = new Dictionary<string, (string prefix, string almName)>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var body in allBodies)
            {
                foreach (Match m in UsageAlarmRegex.Matches(body))
                {
                    var prefix = m.Groups[1].Value;
                    var almName = m.Groups[2].Value;

                    // Skip direct ALM_ references (no prefix) — already handled by Phase 1
                    if (string.IsNullOrEmpty(prefix) || prefix.Equals(almName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var key = prefix + "." + almName;
                    discovered.TryAdd(key, (prefix, almName));
                }
            }

            // Build trigger/delay maps from usage patterns
            var usageTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usageDelays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var body in allBodies)
            {
                foreach (Match m in UsageTriggerRegex.Matches(body))
                {
                    var key = m.Groups[1].Value + "." + m.Groups[2].Value;
                    usageTriggers[key] = m.Groups[3].Value.Trim();
                }
                foreach (Match m in UsageDelayRegex.Matches(body))
                {
                    var key = m.Groups[1].Value + "." + m.Groups[2].Value;
                    if (int.TryParse(m.Groups[3].Value, out var ms))
                        usageDelays[key] = ms;
                }
            }

            foreach (var kvp in discovered)
            {
                var (prefix, almName) = kvp.Value;
                var varPath = node.FullPath + "." + prefix + "." + almName + "._Latched";

                // Skip if already known from Phase 1/2
                if (knownPaths.Contains(varPath))
                    continue;

                // Try to determine severity from the _Alarms method
                // The _Alarms method might reference it as prefix.ALM_xxx.Latched
                AlarmSeverity severity = AlarmSeverity.Unresolved;
                UnresolvedReason reason = UnresolvedReason.Missing;
                string reasonText = $"Inferred from code usage in {pou.Name}";

                if (severityMap.TryGetValue(almName, out var sev))
                {
                    severity = sev;
                    reason = UnresolvedReason.None;
                    reasonText = "";
                }

                var alarm = new AlarmInfo
                {
                    InstanceName = almName,
                    ModulePath = node.FullPath + "." + prefix,
                    ModuleType = node.TypeName,
                    VariablePath = varPath,
                    Condition = FormatConditionName(almName),
                    Severity = severity,
                    UnresolvedReason = reason,
                    UnresolvedReasonText = reasonText,
                    IsDefinitionOnly = false
                };

                var key = kvp.Key;
                if (usageTriggers.TryGetValue(key, out var trigger))
                    alarm.TriggerCondition = trigger;
                if (usageDelays.TryGetValue(key, out var delayMs))
                    alarm.TriggerDelayMs = delayMs;

                node.Alarms.Add(alarm);
                project.AllAlarms.Add(alarm);
                knownPaths.Add(varPath);
            }
        }

        foreach (var child in node.Children)
            InferFromNode(child, project, knownPaths);
    }

    /// <summary>
    /// Collect all POU type names that are represented in the object tree.
    /// </summary>
    private static void CollectTreeTypes(List<ObjectTreeNode> nodes, HashSet<string> types)
    {
        foreach (var node in nodes)
        {
            types.Add(node.TypeName);
            CollectTreeTypes(node.Children, types);
        }
    }

    // ── Struct alarm record ────────────────────────────────────────────
    /// <param name="Variable">The ALM_ variable</param>
    /// <param name="PathPrefix">Full dot-path to the containing struct/FB</param>
    /// <param name="SourceType">The FB or struct type that declares this alarm (for ModuleType attribution)</param>
    private record StructAlarmEntry(TcVariable Variable, string PathPrefix, string? SourceType = null);

    // ── Struct alarm finder ──────────────────────────────────────────
    /// <summary>
    /// Find ALM_ variables of type DM_TriggeredLatch inside STRUCT-typed
    /// member variables (e.g. _Alarms : ST_DM_Tracker_Alarms has 19 ALM_ members).
    /// Recurses up to 3 levels deep into nested structs.
    /// </summary>
    private static List<StructAlarmEntry> FindStructAlarms(
        TcPou pou, TcProject project, string nodePath, int depth = 0)
    {
        var results = new List<StructAlarmEntry>();
        if (depth > 5) return results; // Guard against infinite recursion

        var allVars = pou.AllVariables.Count > 0 ? pou.AllVariables : pou.Variables;

        foreach (var v in allVars)
        {
            // Skip direct ALM_ vars (handled by Phase 1)
            if (v.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase))
                continue;

            // Look up the type as a STRUCT DUT
            if (project.DUTs.TryGetValue(v.DataType, out var dut) && dut.DutType == DutType.Struct)
            {
                var structPath = nodePath + "." + v.Name;
                ScanStructForAlarms(dut, structPath, project, results, depth + 1);
            }
        }

        return results;
    }

    /// <summary>
    /// Recursively scan a STRUCT for DM_TriggeredLatch ALM_ members,
    /// including nested structs and FB instances within structs.
    /// </summary>
    private static void ScanStructForAlarms(
        TcDut dut, string parentPath, TcProject project, List<StructAlarmEntry> results, int depth)
    {
        if (depth > 5) return;

        foreach (var member in dut.Members)
        {
            // Direct ALM_ in struct
            if (member.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)
                && member.DataType.Equals("DM_TriggeredLatch", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new StructAlarmEntry(member, parentPath));
                continue;
            }

            // Member is a nested struct — recurse
            if (project.DUTs.TryGetValue(member.DataType, out var nestedDut)
                && nestedDut.DutType == DutType.Struct)
            {
                var nestedPath = parentPath + "." + member.Name;
                ScanStructForAlarms(nestedDut, nestedPath, project, results, depth + 1);
                continue;
            }

            // Member is a FB instance inside a struct (e.g. ST_DM_Tracker_Devices
            // contains PrintDetectorLnA : CM_SICK_KTM) — find alarms on that FB
            if (project.POUs.TryGetValue(member.DataType, out var memberPou)
                && memberPou.PouType == PouType.FunctionBlock)
            {
                var fbPath = parentPath + "." + member.Name;

                // Direct ALM_ vars on the FB — attribute to the FB's type
                foreach (var fbVar in memberPou.AllVariables)
                {
                    if (fbVar.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)
                        && fbVar.DataType.Equals("DM_TriggeredLatch", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new StructAlarmEntry(fbVar, fbPath, memberPou.Name));
                    }
                }

                // Recurse into the FB's own struct members
                var fbStructAlarms = FindStructAlarms(memberPou, project, fbPath, depth + 1);
                results.AddRange(fbStructAlarms);
            }
        }
    }

    // ── Tree walker ────────────────────────────────────────────────────

    private static void WalkTree(ObjectTreeNode node, TcProject project)
    {
        node.Alarms.Clear();

        if (project.POUs.TryGetValue(node.TypeName, out var pou))
            ExtractAlarms(node, pou, project);

        // Recurse into tree children first (standard tree walk)
        foreach (var child in node.Children)
            WalkTree(child, project);

        // AFTER the tree walk (so all tree-based alarms are registered),
        // do a deep scan for alarms on FB members not in the tree.
        if (project.POUs.TryGetValue(node.TypeName, out var pouForDeep))
        {
            DeepScanFbMembers(node, pouForDeep, project, node.FullPath,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        }
    }

    /// <summary>
    /// Recursively scan a POU's FB-typed member variables for alarms.
    /// This expands the alarm search into FB instances that the tree builder
    /// skipped (e.g. because the parent was a reference node).
    /// Does NOT create tree nodes — only discovers and attaches alarms.
    /// </summary>
    private static void DeepScanFbMembers(
        ObjectTreeNode parentNode, TcPou pou, TcProject project,
        string basePath, HashSet<string> visited, int depth)
    {
        if (depth > 10) return;

        var allVars = pou.AllVariables.Count > 0 ? pou.AllVariables : pou.Variables;

        foreach (var v in allVars)
        {
            // Skip ALM_ vars (already handled by ExtractAlarms)
            if (v.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase))
                continue;
            // Skip references at this level (prevent infinite loops)
            if (v.IsReference)
                continue;

            var childPath = basePath + "." + v.Name;

            // Check if member is a known FB
            if (project.POUs.TryGetValue(v.DataType, out var childPou)
                && childPou.PouType == PouType.FunctionBlock)
            {
                if (!visited.Add(childPath)) continue; // Cycle guard

                // Extract alarms from this FB instance
                ExtractAlarmsForPath(parentNode, childPou, project, childPath);

                // Recurse deeper
                DeepScanFbMembers(parentNode, childPou, project, childPath, visited, depth + 1);

                visited.Remove(childPath);
            }
            // Check if member is a struct containing FBs or alarms
            else if (project.DUTs.TryGetValue(v.DataType, out var dut) && dut.DutType == DutType.Struct)
            {
                DeepScanStructMembers(parentNode, dut, project, childPath, visited, depth + 1);
            }
        }
    }

    /// <summary>
    /// Recursively scan struct members for FB instances and alarm variables.
    /// </summary>
    private static void DeepScanStructMembers(
        ObjectTreeNode parentNode, TcDut dut, TcProject project,
        string basePath, HashSet<string> visited, int depth)
    {
        if (depth > 10) return;

        foreach (var member in dut.Members)
        {
            var memberPath = basePath + "." + member.Name;

            // Direct alarm in struct
            if (member.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)
                && member.DataType.Equals("DM_TriggeredLatch", StringComparison.OrdinalIgnoreCase))
            {
                // Already handled by FindStructAlarms in ExtractAlarms — skip
                continue;
            }

            // FB instance inside struct
            if (project.POUs.TryGetValue(member.DataType, out var memberPou)
                && memberPou.PouType == PouType.FunctionBlock)
            {
                if (!visited.Add(memberPath)) continue;

                ExtractAlarmsForPath(parentNode, memberPou, project, memberPath);
                DeepScanFbMembers(parentNode, memberPou, project, memberPath, visited, depth + 1);

                visited.Remove(memberPath);
            }
            // Nested struct
            else if (project.DUTs.TryGetValue(member.DataType, out var nestedDut)
                     && nestedDut.DutType == DutType.Struct)
            {
                DeepScanStructMembers(parentNode, nestedDut, project, memberPath, visited, depth + 1);
            }
        }
    }

    /// <summary>
    /// Extract alarms from a POU at a specific path and attach them to a parent node.
    /// Used by DeepScanFbMembers for alarm discovery without tree node creation.
    /// </summary>
    private static void ExtractAlarmsForPath(
        ObjectTreeNode parentNode, TcPou pou, TcProject project, string fullPath)
    {
        // Direct ALM_ variables
        var alarmVars = pou.AllVariables
            .Where(v => v.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)
                        && v.DataType.Equals("DM_TriggeredLatch", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Struct-embedded alarms
        var structAlarms = FindStructAlarms(pou, project, fullPath);

        if (alarmVars.Count == 0 && structAlarms.Count == 0)
            return;

        var alarmsMethodBodies = CollectAlarmsMethodBodies(pou, project);
        var allMethodBodies = CollectAllMethodBodies(pou, project);
        var severityMap = BuildSeverityMap(alarmsMethodBodies);
        var triggerMap = BuildTriggerMap(allMethodBodies);
        var delayMap = BuildDelayMap(allMethodBodies);

        // Check for existing alarms at this path to avoid duplicates
        var existingPaths = new HashSet<string>(
            project.AllAlarms.Select(a => a.VariablePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var v in alarmVars)
        {
            var varPath = fullPath + "." + v.Name + "._Latched";
            if (existingPaths.Contains(varPath)) continue;

            var alarm = new AlarmInfo
            {
                InstanceName = v.Name,
                ModulePath = fullPath,
                ModuleType = pou.Name,
                VariablePath = varPath,
                Condition = FormatConditionName(v.Name),
            };

            if (severityMap.TryGetValue(v.Name, out var severity))
            {
                alarm.Severity = severity;
                alarm.UnresolvedReason = UnresolvedReason.None;
            }
            else
            {
                alarm.Severity = AlarmSeverity.Unresolved;
                alarm.UnresolvedReason = UnresolvedReason.BaseClass;
                alarm.UnresolvedReasonText = $"Deep scan from {pou.Name}";
            }

            if (triggerMap.TryGetValue(v.Name, out var trigger))
                alarm.TriggerCondition = trigger.Trim();
            if (delayMap.TryGetValue(v.Name, out var delayMs))
                alarm.TriggerDelayMs = delayMs;

            parentNode.Alarms.Add(alarm);
            project.AllAlarms.Add(alarm);
            existingPaths.Add(varPath);
        }

        // Struct-embedded alarms
        foreach (var sa in structAlarms)
        {
            var varPath = sa.PathPrefix + "." + sa.Variable.Name + "._Latched";
            if (existingPaths.Contains(varPath)) continue;

            var moduleType = sa.SourceType ?? pou.Name;
            var alarm = new AlarmInfo
            {
                InstanceName = sa.Variable.Name,
                ModulePath = sa.PathPrefix,
                ModuleType = moduleType,
                VariablePath = varPath,
                Condition = FormatConditionName(sa.Variable.Name),
            };

            if (severityMap.TryGetValue(sa.Variable.Name, out var severity))
            {
                alarm.Severity = severity;
                alarm.UnresolvedReason = UnresolvedReason.None;
            }
            else
            {
                alarm.Severity = AlarmSeverity.Unresolved;
                alarm.UnresolvedReason = UnresolvedReason.BaseClass;
                alarm.UnresolvedReasonText = $"Deep scan from {moduleType}";
            }

            if (triggerMap.TryGetValue(sa.Variable.Name, out var trigger))
                alarm.TriggerCondition = trigger.Trim();
            if (delayMap.TryGetValue(sa.Variable.Name, out var delayMs))
                alarm.TriggerDelayMs = delayMs;

            parentNode.Alarms.Add(alarm);
            project.AllAlarms.Add(alarm);
            existingPaths.Add(varPath);
        }
    }

    // ── Core extraction ────────────────────────────────────────────────

    private static void ExtractAlarms(ObjectTreeNode node, TcPou pou, TcProject project)
    {
        // Find all ALM_ variables of type DM_TriggeredLatch (direct on POU)
        var alarmVars = pou.AllVariables
            .Where(v => v.Name.StartsWith("ALM_", StringComparison.OrdinalIgnoreCase)
                        && v.DataType.Equals("DM_TriggeredLatch", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Also find alarms inside STRUCT-typed member variables (e.g. _Alarms : ST_xxx_Alarms)
        var structAlarms = FindStructAlarms(pou, project, node.FullPath);
        alarmVars.AddRange(structAlarms.Select(sa => sa.Variable));

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

        // Build lookups for struct alarm paths and source types
        var structAlarmLookup = new Dictionary<string, StructAlarmEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var sa in structAlarms)
        {
            // Use pathPrefix + almName as key to handle same ALM_Name on different FBs
            var key = sa.PathPrefix + "." + sa.Variable.Name;
            structAlarmLookup.TryAdd(key, sa);
        }

        foreach (var v in alarmVars)
        {
            // Determine the variable path and source type — direct ALM_ or via struct/FB accessor
            string varPath;
            string moduleType = node.TypeName;
            string modulePath = node.FullPath;

            // Check if this alarm came from a struct scan
            var matchingEntry = structAlarms.FirstOrDefault(sa =>
                sa.Variable.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase));

            if (matchingEntry != null)
            {
                varPath = matchingEntry.PathPrefix + "." + v.Name + "._Latched";
                modulePath = matchingEntry.PathPrefix;
                if (!string.IsNullOrEmpty(matchingEntry.SourceType))
                    moduleType = matchingEntry.SourceType;
            }
            else
            {
                varPath = node.FullPath + "." + v.Name + "._Latched";
            }

            var alarm = new AlarmInfo
            {
                InstanceName = v.Name,
                ModulePath = modulePath,
                ModuleType = moduleType,
                VariablePath = varPath,
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
