using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Extracts state machines from TwinCAT PLC code using two strategies:
///
///   Strategy A — DM_StateMachine wrapper (3P Innovation base class)
///     Detects variables typed as DM_StateMachine (or subclass).
///     Parses CASE smVar.State OF blocks and smVar.GotoState() transitions.
///     Walks the ISA-88 ObjectTree only.
///
///   Strategy B — Direct enum CASE (universal pattern)
///     Detects ANY variable typed as E_* used as a CASE selector.
///     Parses CASE eVar OF blocks and eVar := E_xxx.State transitions.
///     Walks ALL POUs in the project, not just those in the ObjectTree.
///     Extracts condition hints from IF/ELSIF guards before assignments.
/// </summary>
public static class StateMachineExtractor
{
    // ── Strategy A patterns ───────────────────────────────────────────────────
    private static readonly Regex RxCaseOnState = new(
        @"CASE\s+(\w+)\.State\s+OF\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RxGotoState = new(
        @"(\w+)\.GotoState\s*\(\s*StateNext\s*:=\s*([\w.]+)\s*(?:,\s*TimeoutNext\s*:=\s*([^)]+))?\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Strategy B patterns ───────────────────────────────────────────────────
    // CASE varName OF  (varName is a plain identifier, not varName.State)
    private static readonly Regex RxCaseOnVar = new(
        @"CASE\s+(\w+)\s+OF\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // State label inside a CASE:  E_xxx.StateName :  or  StateName :  (not :=)
    private static readonly Regex RxStateLabel = new(
        @"^([\w.]+)\s*:\s*$", RegexOptions.Compiled);

    // Direct assignment transition:  varName := E_xxx.StateName ;
    private static readonly Regex RxDirectAssign = new(
        @"(\w+)\s*:=\s*([\w.]+)\s*;", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Entry point ───────────────────────────────────────────────────────────

    public static void Extract(TcProject project)
    {
        // Strategy A: DM_StateMachine wrappers, ObjectTree only
        foreach (var node in project.ObjectTree)
            ExtractDmSmFromNode(node, project);

        // Strategy B: Direct enum CASE blocks, ALL POUs
        // Collect enum types defined in DUTs for validation
        var knownEnumTypes = project.DUTs
            .Where(kv => kv.Value.DutType == DutType.Enum)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var pou in project.POUs.Values)
            ExtractDirectEnumSMs(pou, project, knownEnumTypes);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Strategy A — DM_StateMachine wrappers
    // ══════════════════════════════════════════════════════════════════════════

    private static void ExtractDmSmFromNode(ObjectTreeNode node, TcProject project)
    {
        if (project.POUs.TryGetValue(node.TypeName, out var pou))
        {
            var smVars = pou.AllVariables
                .Where(v => v.DataType.StartsWith("DM_StateMachine", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var smVar in smVars)
            {
                var sm = new StateMachine { InstanceName = smVar.Name, OwnerPou = pou.Name };
                ParseDmSmConstructorArgs(smVar, sm,
                    pou.AllVariables.Count > 0 ? pou.AllVariables : pou.Variables);

                var bodies = CollectBodies(pou);
                foreach (var (method, body) in bodies)
                    ExtractDmSmFromBody(sm, smVar.Name, method, body);

                CrossReferenceEnum(sm, project);
                MarkSpecialStates(sm);

                if (sm.States.Count > 0 || sm.Transitions.Count > 0)
                {
                    node.StateMachines.Add(sm);
                    project.AllStateMachines.Add(sm);
                }
            }
        }

        foreach (var child in node.Children)
            ExtractDmSmFromNode(child, project);
    }

    private static void ParseDmSmConstructorArgs(TcVariable smVar, StateMachine sm,
        IReadOnlyList<TcVariable> allVars)
    {
        var args = smVar.ConstructorArgs;
        if (string.IsNullOrEmpty(args) && smVar.DataType.Contains('('))
            args = smVar.DataType[smVar.DataType.IndexOf('(')..];
        if (string.IsNullOrEmpty(args)) return;

        var nameMatch = Regex.Match(args, @"'([^']+)'");
        if (nameMatch.Success) sm.DisplayName = nameMatch.Groups[1].Value;

        var allEnumRefs = new List<string>();
        foreach (Match er in Regex.Matches(args, @"(E_\w+\.\w+)"))
            allEnumRefs.Add(er.Groups[1].Value);

        foreach (var v in allVars)
        {
            if (v == smVar) continue;
            if (v.Name.Equals("InitialState", StringComparison.OrdinalIgnoreCase) ||
                v.Name.Equals("TransitionState", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match er in Regex.Matches(v.DataType, @"(E_\w+\.\w+)"))
                    allEnumRefs.Add(er.Groups[1].Value);
            }
        }

        if (allEnumRefs.Count >= 1)
        {
            sm.InitialState = allEnumRefs[0];
            var dot = sm.InitialState.LastIndexOf('.');
            if (dot > 0) sm.EnumTypeName = sm.InitialState[..dot];
        }
        if (allEnumRefs.Count >= 2)
            sm.TransitionState = allEnumRefs[1];
    }

    private static void ExtractDmSmFromBody(StateMachine sm, string smName,
        string methodName, string body)
    {
        foreach (Match cm in RxCaseOnState.Matches(body))
        {
            if (!cm.Groups[1].Value.Equals(smName, StringComparison.OrdinalIgnoreCase))
                continue;

            var blockStart = cm.Index + cm.Length;
            var endIdx = FindEndCase(body, blockStart);
            if (endIdx < 0) continue;

            var caseBody = body[blockStart..endIdx];
            ParseCaseBodyDmSm(sm, smName, methodName, caseBody);
        }
    }

    private static void ParseCaseBodyDmSm(StateMachine sm, string smName,
        string methodName, string caseBody)
    {
        ParseStateLabels(sm, methodName, caseBody);

        foreach (Match gm in RxGotoState.Matches(caseBody))
        {
            if (!gm.Groups[1].Value.Equals(smName, StringComparison.OrdinalIgnoreCase))
                continue;

            var fromState = FindEnclosingState(caseBody, gm.Index);
            sm.Transitions.Add(new StateTransition
            {
                FromState = fromState ?? "?",
                ToState   = gm.Groups[2].Value,
                TimeoutValue = gm.Groups[3].Success ? gm.Groups[3].Value.Trim() : null,
                MethodName   = methodName
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Strategy B — Direct enum CASE
    // ══════════════════════════════════════════════════════════════════════════

    private static void ExtractDirectEnumSMs(TcPou pou, TcProject project,
        HashSet<string> knownEnumTypes)
    {
        // Already-known SM instance names from Strategy A (avoid duplicates)
        var alreadyDetected = project.AllStateMachines
            .Where(sm => sm.OwnerPou.Equals(pou.Name, StringComparison.OrdinalIgnoreCase))
            .Select(sm => sm.InstanceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Candidate variables: typed as E_* and either known enum or looks like one
        var candidates = pou.Variables
            .Concat(pou.AllVariables)
            .Where(v => v.DataType.StartsWith("E_", StringComparison.OrdinalIgnoreCase)
                     && v.Scope is VarScope.Local or VarScope.Stat or VarScope.Input
                     && !alreadyDetected.Contains(v.Name))
            .GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (!candidates.Any()) return;

        var bodies = CollectBodies(pou);
        if (!bodies.Any()) return;

        // Identify which enum variables actually appear as CASE selectors
        var allCode = string.Join("\n", bodies.Select(b => b.Body));
        foreach (var enumVar in candidates)
        {
            // Quick pre-check: does this variable name appear after CASE ... OF?
            if (!Regex.IsMatch(allCode,
                $@"CASE\s+{Regex.Escape(enumVar.Name)}\s+OF\b",
                RegexOptions.IgnoreCase))
                continue;

            var enumType = enumVar.DataType;

            var sm = new StateMachine
            {
                InstanceName  = enumVar.Name,
                EnumTypeName  = enumType,
                OwnerPou      = pou.Name,
                DisplayName   = !string.IsNullOrEmpty(enumVar.Comment) ? enumVar.Comment : null,
                DetectedBy    = SmDetectionStrategy.DirectEnumCase
            };

            foreach (var (method, body) in bodies)
                ExtractDirectCaseFromBody(sm, enumVar.Name, enumType, method, body);

            // Cross-reference DUT enum for the complete state list
            CrossReferenceEnum(sm, project);
            MarkSpecialStates(sm);

            if (sm.States.Count < 2) continue;   // Noise filter — need at least 2 states

            // Add to project and distribute to ObjectTree node (if matched)
            project.AllStateMachines.Add(sm);
            DistributeSmToTreeNode(sm, project);
        }
    }

    private static void ExtractDirectCaseFromBody(StateMachine sm, string varName,
        string enumType, string method, string body)
    {
        foreach (Match cm in RxCaseOnVar.Matches(body))
        {
            if (!cm.Groups[1].Value.Equals(varName, StringComparison.OrdinalIgnoreCase))
                continue;

            var blockStart = cm.Index + cm.Length;
            var endIdx = FindEndCase(body, blockStart);
            if (endIdx < 0) continue;

            var caseBody = body[blockStart..endIdx];
            ParseCaseBodyDirect(sm, varName, enumType, method, caseBody);
        }
    }

    private static void ParseCaseBodyDirect(StateMachine sm, string varName,
        string enumType, string method, string caseBody)
    {
        ParseStateLabels(sm, method, caseBody);

        // Find direct assignment transitions:  varName := E_xxx.State;
        foreach (Match tm in RxDirectAssign.Matches(caseBody))
        {
            if (!tm.Groups[1].Value.Equals(varName, StringComparison.OrdinalIgnoreCase))
                continue;

            var toRef   = tm.Groups[2].Value;
            var toState = toRef.Contains('.') ? toRef[(toRef.LastIndexOf('.') + 1)..] : toRef;

            // Filter out self-assignments and assignments to unrelated types
            if (string.IsNullOrEmpty(toState) || toState.Equals(varName, StringComparison.OrdinalIgnoreCase))
                continue;

            var fromState  = FindEnclosingState(caseBody, tm.Index);
            var condition  = ExtractConditionHint(caseBody, tm.Index);

            // Deduplicate — same (from, to, condition) triple is one transition
            var duplicate = sm.Transitions.Any(t =>
                t.FromState.Equals(fromState ?? "?", StringComparison.OrdinalIgnoreCase) &&
                t.ToState.Equals(toState, StringComparison.OrdinalIgnoreCase));

            if (!duplicate)
            {
                sm.Transitions.Add(new StateTransition
                {
                    FromState  = fromState ?? "?",
                    ToState    = toState,
                    Condition  = condition,
                    MethodName = method
                });
            }
        }
    }

    /// <summary>
    /// Look backward from a position in the CASE body for the nearest IF/ELSIF guard.
    /// Returns a short condition hint string, or null if none found before the next state label.
    /// </summary>
    private static string? ExtractConditionHint(string body, int pos)
    {
        var before = body[..pos];
        var lines  = before.Split('\n');

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].Trim().TrimEnd('\r');

            // Stop at a state label (we've gone past the start of this state's block)
            if (RxStateLabel.IsMatch(t) && !t.Contains(":=")) break;
            if (t.Equals("ELSE", StringComparison.OrdinalIgnoreCase)) break;

            var ifM = Regex.Match(t, @"^(IF|ELSIF)\s+(.+?)\s+THEN\s*$",
                RegexOptions.IgnoreCase);
            if (ifM.Success)
            {
                var kw   = ifM.Groups[1].Value;
                var cond = ifM.Groups[2].Value.Trim();
                // Tidy up: strip leading underscores from identifiers
                cond = Regex.Replace(cond, @"\b_+([A-Z])", "$1");
                // Truncate very long conditions
                return cond.Length > 80 ? $"{kw} {cond[..77]}…" : $"{kw} {cond}";
            }
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Shared helpers
    // ══════════════════════════════════════════════════════════════════════════

    private static List<(string Method, string Body)> CollectBodies(TcPou pou)
    {
        var result = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(pou.RawImplementation))
            result.Add(("Main", pou.RawImplementation));
        foreach (var m in pou.Methods)
            if (!string.IsNullOrWhiteSpace(m.Body))
                result.Add((m.Name, m.Body));
        return result;
    }

    /// <summary>
    /// Parse the state-label lines within a CASE body and add any new states to the SM.
    /// </summary>
    private static void ParseStateLabels(StateMachine sm, string method, string caseBody)
    {
        var lines = caseBody.Split('\n');
        string? currentState = null;
        var codeLines = new List<string>();

        foreach (var rawLine in lines)
        {
            var line    = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            var lm = RxStateLabel.Match(trimmed);
            if (lm.Success && !trimmed.Contains(":="))
            {
                if (currentState != null)
                    AddState(sm, currentState, string.Join("\n", codeLines).Trim(), method);
                currentState = lm.Groups[1].Value;
                codeLines.Clear();
                continue;
            }

            if (trimmed.Equals("ELSE", StringComparison.OrdinalIgnoreCase))
            {
                if (currentState != null)
                    AddState(sm, currentState, string.Join("\n", codeLines).Trim(), method);
                currentState = "ELSE";
                codeLines.Clear();
                continue;
            }

            if (currentState != null) codeLines.Add(line);
        }

        if (currentState != null)
            AddState(sm, currentState, string.Join("\n", codeLines).Trim(), method);
    }

    private static void CrossReferenceEnum(StateMachine sm, TcProject project)
    {
        if (string.IsNullOrEmpty(sm.EnumTypeName)) return;
        if (!project.DUTs.TryGetValue(sm.EnumTypeName, out var dut)) return;

        foreach (var ev in dut.EnumValues)
        {
            var existing = sm.States.FirstOrDefault(s =>
                s.Name.Equals(ev.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                existing.Value = ev.Value;
            else
                sm.States.Add(new StateMachineState
                {
                    Name = ev.Name, Value = ev.Value,
                    CodeBody = "// No CASE branch for this state"
                });
        }
    }

    private static void MarkSpecialStates(StateMachine sm)
    {
        foreach (var state in sm.States)
        {
            var lower = state.Name.ToLowerInvariant();
            state.IsInitial   = sm.InitialState?.EndsWith("." + state.Name,
                StringComparison.OrdinalIgnoreCase) == true;
            state.IsError     = lower.Contains("error") || lower.Contains("fault")
                             || lower.Contains("abort");
            state.IsTimeout   = lower.Contains("timeout") || lower.Contains("timedout");
            state.IsTransition = sm.TransitionState?.EndsWith("." + state.Name,
                StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    /// <summary>
    /// Distribute a Strategy-B SM to the matching ObjectTreeNode so it appears
    /// in the per-node SM list (used by AnalysisPageViewModel.BuildDiagramsForNode).
    /// </summary>
    private static void DistributeSmToTreeNode(StateMachine sm, TcProject project)
    {
        static void Walk(List<ObjectTreeNode> nodes, StateMachine sm)
        {
            foreach (var node in nodes)
            {
                if (node.TypeName.Equals(sm.OwnerPou, StringComparison.OrdinalIgnoreCase))
                {
                    if (!node.StateMachines.Any(x =>
                        x.EnumTypeName.Equals(sm.EnumTypeName, StringComparison.OrdinalIgnoreCase)))
                    {
                        node.StateMachines.Add(sm);
                    }
                }
                Walk(node.Children, sm);
            }
        }
        Walk(project.ObjectTree, sm);
    }

    private static int FindEndCase(string body, int from)
    {
        int depth = 1;
        var rx = new Regex(@"\b(CASE|END_CASE)\b", RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(body, from))
        {
            depth += m.Value.Equals("CASE", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
            if (depth == 0) return m.Index;
        }
        return -1;
    }

    private static string? FindEnclosingState(string body, int pos)
    {
        var before = body[..pos];
        var lines  = before.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].Trim().TrimEnd('\r');
            var m = RxStateLabel.Match(t);
            if (m.Success && !t.Contains(":=")) return m.Groups[1].Value;
            if (t.Equals("ELSE", StringComparison.OrdinalIgnoreCase)) return "ELSE";
        }
        return null;
    }

    private static void AddState(StateMachine sm, string fullName, string code, string method)
    {
        var shortName = fullName;
        var dot = fullName.LastIndexOf('.');
        if (dot >= 0) shortName = fullName[(dot + 1)..];

        var existing = sm.States.FirstOrDefault(s =>
            s.Name.Equals(shortName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            if (!string.IsNullOrEmpty(code))
                existing.CodeBody += $"\n// --- From {method} ---\n{code}";
        }
        else
        {
            sm.States.Add(new StateMachineState
                { Name = shortName, CodeBody = code, MethodName = method });
        }
    }
}
