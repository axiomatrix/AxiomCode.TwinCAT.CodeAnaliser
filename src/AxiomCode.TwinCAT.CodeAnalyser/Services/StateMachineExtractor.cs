using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Extracts state machines from CASE blocks on DM_StateMachine instances.
/// Parses states, transitions, timeouts, and code bodies.
/// </summary>
public static class StateMachineExtractor
{
    private static readonly Regex RxCaseBlock = new(
        @"CASE\s+(\w+)\.State\s+OF\b", RegexOptions.IgnoreCase);

    private static readonly Regex RxGotoState = new(
        @"(\w+)\.GotoState\s*\(\s*StateNext\s*:=\s*([\w.]+)\s*(?:,\s*TimeoutNext\s*:=\s*([^)]+))?\s*\)",
        RegexOptions.IgnoreCase);

    public static void Extract(TcProject project)
    {
        foreach (var node in project.ObjectTree)
            ExtractFromNode(node, project);
    }

    private static void ExtractFromNode(ObjectTreeNode node, TcProject project)
    {
        if (project.POUs.TryGetValue(node.TypeName, out var pou))
        {
            var smVars = pou.AllVariables
                .Where(v => v.DataType.StartsWith("DM_StateMachine", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var smVar in smVars)
            {
                var sm = new StateMachine { InstanceName = smVar.Name, OwnerPou = pou.Name };
                ParseConstructorArgs(smVar, sm, pou.AllVariables.Count > 0 ? pou.AllVariables : pou.Variables);

                // Collect all method bodies
                var bodies = new List<(string Method, string Body)>();
                if (!string.IsNullOrWhiteSpace(pou.RawImplementation))
                    bodies.Add(("Main", pou.RawImplementation));
                foreach (var m in pou.Methods)
                    if (!string.IsNullOrWhiteSpace(m.Body))
                        bodies.Add((m.Name, m.Body));

                foreach (var (method, body) in bodies)
                    ExtractFromBody(sm, smVar.Name, method, body);

                // Cross-reference enum
                if (!string.IsNullOrEmpty(sm.EnumTypeName) &&
                    project.DUTs.TryGetValue(sm.EnumTypeName, out var dut))
                {
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

                // Mark special states
                foreach (var state in sm.States)
                {
                    var lower = state.Name.ToLowerInvariant();
                    state.IsInitial = sm.InitialState?.EndsWith("." + state.Name, StringComparison.OrdinalIgnoreCase) == true;
                    state.IsError = lower.Contains("error");
                    state.IsTimeout = lower.Contains("timeout") || lower.Contains("timedout");
                    state.IsTransition = sm.TransitionState?.EndsWith("." + state.Name, StringComparison.OrdinalIgnoreCase) == true;
                }

                if (sm.States.Count > 0 || sm.Transitions.Count > 0)
                {
                    node.StateMachines.Add(sm);
                    project.AllStateMachines.Add(sm);
                }
            }
        }

        foreach (var child in node.Children)
            ExtractFromNode(child, project);
    }

    private static void ParseConstructorArgs(TcVariable smVar, StateMachine sm, IReadOnlyList<TcVariable> allVars)
    {
        var args = smVar.ConstructorArgs;

        // Fallback: if ConstructorArgs is empty but DataType contains constructor call
        // (parser didn't separate them), extract from DataType
        if (string.IsNullOrEmpty(args) && smVar.DataType.Contains("("))
            args = smVar.DataType[smVar.DataType.IndexOf('(')..];

        if (string.IsNullOrEmpty(args)) return;

        var nameMatch = Regex.Match(args, @"'([^']+)'");
        if (nameMatch.Success) sm.DisplayName = nameMatch.Groups[1].Value;

        // Collect all enum refs from args
        var allEnumRefs = new List<string>();
        foreach (Match er in Regex.Matches(args, @"(E_\w+\.\w+)"))
            allEnumRefs.Add(er.Groups[1].Value);

        // Multi-line constructors: parser may have created separate variables for
        // continuation lines like "InitialState" with DataType "= E_xxx.Inactive,"
        // and "TransitionState" with DataType "= E_xxx.TransitionState)"
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

    private static void ExtractFromBody(StateMachine sm, string smName, string methodName, string body)
    {
        foreach (Match cm in RxCaseBlock.Matches(body))
        {
            if (!cm.Groups[1].Value.Equals(smName, StringComparison.OrdinalIgnoreCase))
                continue;

            var blockStart = cm.Index + cm.Length;
            var endIdx = FindEndCase(body, blockStart);
            if (endIdx < 0) continue;

            var caseBody = body[blockStart..endIdx];
            ParseCaseBody(sm, smName, methodName, caseBody);
        }
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

    private static void ParseCaseBody(StateMachine sm, string smName, string methodName, string caseBody)
    {
        var lines = caseBody.Split('\n');
        string? currentState = null;
        var codeLines = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();

            // State label: E_xxx.StateName :  (not :=)
            var lm = Regex.Match(trimmed, @"^([\w.]+)\s*:\s*$");
            if (lm.Success && !trimmed.Contains(":="))
            {
                if (currentState != null)
                    AddState(sm, currentState, string.Join("\n", codeLines).Trim(), methodName);
                currentState = lm.Groups[1].Value;
                codeLines.Clear();
                continue;
            }

            if (trimmed.Equals("ELSE", StringComparison.OrdinalIgnoreCase))
            {
                if (currentState != null)
                    AddState(sm, currentState, string.Join("\n", codeLines).Trim(), methodName);
                currentState = "ELSE";
                codeLines.Clear();
                continue;
            }

            if (currentState != null) codeLines.Add(line);
        }

        if (currentState != null)
            AddState(sm, currentState, string.Join("\n", codeLines).Trim(), methodName);

        // Extract transitions
        foreach (Match gm in RxGotoState.Matches(caseBody))
        {
            if (!gm.Groups[1].Value.Equals(smName, StringComparison.OrdinalIgnoreCase))
                continue;

            var fromState = FindEnclosingState(caseBody, gm.Index);
            sm.Transitions.Add(new StateTransition
            {
                FromState = fromState ?? "?",
                ToState = gm.Groups[2].Value,
                TimeoutValue = gm.Groups[3].Success ? gm.Groups[3].Value.Trim() : null,
                MethodName = methodName
            });
        }
    }

    private static string? FindEnclosingState(string body, int pos)
    {
        var before = body[..pos];
        var lines = before.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].Trim().TrimEnd('\r');
            var m = Regex.Match(t, @"^([\w.]+)\s*:\s*$");
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
            {
                Name = shortName, CodeBody = code, MethodName = method
            });
        }
    }
}
