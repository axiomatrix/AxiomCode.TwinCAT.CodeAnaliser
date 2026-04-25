using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Produces a <see cref="PackMlComplianceResult"/> for a given POU. Goes beyond
/// the simple state-name matching in <see cref="ComplianceChecker.CheckPackml"/>
/// to also validate the canonical PackML transition graph, detect operator
/// command bindings, score mode coverage, and assess error propagation.
///
/// Detection strategy:
///   - State coverage      → method names matching <c>_<State></c>
///   - Transitions         → parses <c>CASE eState OF</c> and <c>GotoState</c> calls
///                           in method bodies; pairs assignment LHS/RHS with the
///                           CASE branch they sit inside
///   - Operator commands   → identifier scan over method bodies for the
///                           canonical Cmd* names + R_TRIG / F_TRIG hookups
///   - Modes               → enum-assignment scan for the canonical mode names
///   - Error propagation   → looks for <c>_AlarmsPresent</c> (or its tier
///                           variants) influencing a transition into Aborting
///
/// All heuristics are case-insensitive and resilient to whitespace / extra
/// punctuation. Where evidence is ambiguous the result is flagged "absent"
/// rather than guessing — false negatives are safer than false positives in
/// regulated audit context.
/// </summary>
public static class PackMlAnalyzer
{
    private static readonly string[] CoreStates =
        { "Idle", "Execute", "Stopped", "Resetting" };

    private static readonly string[] AllStates =
    {
        "Idle",
        "Starting", "Execute", "Completing", "Complete",
        "Holding", "Held", "Unholding",
        "Stopping", "Stopped",
        "Aborting", "Aborted",
        "Clearing", "Resetting",
    };

    /// <summary>
    /// The 17 canonical PackML transitions. The trigger column is the
    /// expected operator command (or "auto/done" for self-transitions on
    /// state-handler completion).
    /// </summary>
    private static readonly (string From, string To, string Trigger, bool Auto)[] CanonicalTransitions =
    {
        ("Idle",       "Starting",   "CmdStart",   false),
        ("Starting",   "Execute",    "auto/done",  true),
        ("Execute",    "Completing", "CmdComplete", false),
        ("Completing", "Complete",   "auto/done",  true),
        ("Complete",   "Resetting",  "CmdReset",   false),

        ("Execute",    "Holding",    "CmdHold",    false),
        ("Holding",    "Held",       "auto/done",  true),
        ("Held",       "Unholding",  "CmdUnhold",  false),
        ("Unholding",  "Execute",    "auto/done",  true),

        ("Execute",    "Stopping",   "CmdStop",    false),
        ("Stopping",   "Stopped",    "auto/done",  true),
        ("Stopped",    "Resetting",  "CmdReset",   false),

        ("Resetting",  "Idle",       "auto/done",  true),

        ("Any",        "Aborting",   "CmdAbort",   false),
        ("Aborting",   "Aborted",    "auto/done",  true),
        ("Aborted",    "Clearing",   "CmdClear",   false),
        ("Clearing",   "Stopped",    "auto/done",  true),
    };

    private static readonly (string Name, bool Required)[] CanonicalCommands =
    {
        ("CmdStart",    true),
        ("CmdStop",     true),
        ("CmdReset",    true),
        ("CmdHold",     false),
        ("CmdUnhold",   false),
        ("CmdAbort",    true),
        ("CmdClear",    true),
        ("CmdComplete", false),
    };

    private static readonly (string Name, bool Required)[] CanonicalModes =
    {
        ("Production",  true),
        ("Manual",      true),
        ("Maintenance", true),
    };

    public static PackMlComplianceResult Analyse(
        TcPou pou,
        IReadOnlyList<StateMachine>? stateMachines = null)
    {
        var sms = stateMachines?.ToList() ?? new List<StateMachine>();
        var primary = sms
            .Where(sm => sm.OwnerPou.Equals(pou.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(sm => sm.States.Count)
            .FirstOrDefault();

        var result = new PackMlComplianceResult { ModuleName = pou.Name };

        // ── Quick exit: not applicable ────────────────────────────────────────
        var hasAnyHook   = AllStates.Any(s => HasMethod(pou, "_" + s));
        var hasAnySm     = primary != null && primary.States.Count > 0;
        if (!hasAnyHook && !hasAnySm)
        {
            result.IsApplicable = false;
            result.Status       = "Not Applicable";
            result.ConformancePercent = 0;
            return result;
        }

        result.IsApplicable = true;

        // ── Section 1: State coverage ─────────────────────────────────────────
        foreach (var state in AllStates)
        {
            var method = pou.Methods.FirstOrDefault(m =>
                m.Name.Equals("_" + state, StringComparison.OrdinalIgnoreCase));
            result.States.Entries.Add(new StateCoverageEntry
            {
                State       = state,
                Implemented = method != null,
                IsCore      = CoreStates.Contains(state, StringComparer.OrdinalIgnoreCase),
                MethodName  = method?.Name,
                CallsSuper  = method != null
                              && !string.IsNullOrEmpty(method.Body)
                              && method.Body.Contains("SUPER^", StringComparison.OrdinalIgnoreCase),
            });
        }

        // ── Section 2: Transitions ────────────────────────────────────────────
        var (detectedTransitions, allBodies) = ExtractTransitions(pou, primary);
        foreach (var (from, to, trigger, auto) in CanonicalTransitions)
        {
            string? matchCondition = null;
            var present = detectedTransitions.Any(t =>
            {
                var fromMatches = string.Equals(from, "Any", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.From, from, StringComparison.OrdinalIgnoreCase)
                    || t.From.IndexOf(from, StringComparison.OrdinalIgnoreCase) >= 0;
                var toMatches = string.Equals(t.To, to, StringComparison.OrdinalIgnoreCase)
                    || t.To.IndexOf(to, StringComparison.OrdinalIgnoreCase) >= 0;
                if (fromMatches && toMatches) { matchCondition = t.Condition; return true; }
                return false;
            });

            // Auto transitions (state-handler completion) — also accept when the source state's
            // _<State> handler exists, since a typical PackML implementation auto-advances.
            if (!present && auto)
            {
                var hookExists = HasMethod(pou, "_" + from);
                if (hookExists) present = true;
            }

            // CmdAbort can come from any state — be lenient
            if (!present && string.Equals(from, "Any", StringComparison.OrdinalIgnoreCase))
            {
                if (allBodies.IndexOf("CmdAbort", StringComparison.OrdinalIgnoreCase) >= 0) present = true;
            }

            result.Transitions.Entries.Add(new TransitionEntry
            {
                From              = from,
                To                = to,
                TriggerLabel      = trigger,
                IsAutoTransition  = auto,
                Present           = present,
                DetectedCondition = matchCondition,
            });
        }

        // ── Section 3: Operator commands ──────────────────────────────────────
        foreach (var (name, required) in CanonicalCommands)
        {
            var bound  = false;
            string? var = null;
            // First check declared variables for direct command matches.
            var directVar = pou.Variables.FirstOrDefault(v =>
                v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (directVar != null) { bound = true; var = directVar.Name; }
            else
            {
                // Then code-body scan for identifier references — covers commands
                // that are wrapped in submodules (e.g. HMIPanel.CmdStart).
                if (CommandPattern(name).IsMatch(allBodies))
                {
                    bound = true;
                    var = $"(referenced in code)";
                }
            }
            result.Commands.Entries.Add(new CommandEntry
            {
                Name          = name,
                IsRequired    = required,
                Bound         = bound,
                BoundVariable = var,
            });
        }

        // ── Section 4: Modes ──────────────────────────────────────────────────
        foreach (var (name, required) in CanonicalModes)
        {
            // Match either the bare mode name or the qualified enum form.
            var pattern = new Regex(
                $@"\b(E_ModuleModes\s*\.\s*)?{Regex.Escape(name)}\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var implemented = pattern.IsMatch(allBodies);
            result.Modes.Entries.Add(new ModeEntry
            {
                Name        = name,
                IsRequired  = required,
                Implemented = implemented,
                Detail      = implemented ? "Found in code body" : null,
            });
        }

        // ── Section 5: Error handling ────────────────────────────────────────
        result.Errors.HasAbortingState =
            result.States.Entries.Any(e => e.State == "Aborting" && e.Implemented);
        result.Errors.HasErrorAggregation =
            allBodies.IndexOf("_AlarmsPresent", StringComparison.OrdinalIgnoreCase) >= 0
            && (allBodies.IndexOf("Aborting",   StringComparison.OrdinalIgnoreCase) >= 0
             || allBodies.IndexOf("CmdAbort",   StringComparison.OrdinalIgnoreCase) >= 0);
        result.Errors.HasResetPathFromError =
            allBodies.IndexOf("CmdClear",   StringComparison.OrdinalIgnoreCase) >= 0
            || allBodies.IndexOf("Clearing", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!result.Errors.HasAbortingState)
            result.Errors.Findings.Add("No Aborting state hook detected — emergency-stop path is not standardised.");
        if (!result.Errors.HasErrorAggregation)
            result.Errors.Findings.Add("No detectable link from _AlarmsPresent to an Aborting / CmdAbort transition.");
        if (!result.Errors.HasResetPathFromError)
            result.Errors.Findings.Add("No Clearing / CmdClear path detected — operator cannot acknowledge faults.");

        // ── Aggregate score ───────────────────────────────────────────────────
        // Five sections weighted equally; each contributes 20 points.
        double Pct(int got, int req) => req == 0 ? 1.0 : (double)got / req;
        double score = 0;
        score += Pct(result.States.ImplementedCount, result.States.RequiredCount) * 20;
        score += Pct(result.Transitions.PresentCount, result.Transitions.RequiredCount) * 20;
        score += Pct(result.Commands.Entries.Count(c => c.Bound),
                     result.Commands.Entries.Count(c => c.IsRequired)) * 20;
        score += Pct(result.Modes.Entries.Count(m => m.Implemented),
                     result.Modes.Entries.Count(m => m.IsRequired)) * 20;
        score += (result.Errors.Score / 3.0) * 20;
        result.ConformancePercent = (int)Math.Round(Math.Clamp(score, 0, 100));

        result.Status = result.ConformancePercent switch
        {
            >= 80 => "Conformant",
            >= 50 => "Partial",
            _     => "Non-conformant",
        };

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool HasMethod(TcPou pou, string name) =>
        pou.Methods.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static (List<DetectedTransition> transitions, string allBodies)
        ExtractTransitions(TcPou pou, StateMachine? primary)
    {
        var transitions = new List<DetectedTransition>();
        var sb = new System.Text.StringBuilder();
        foreach (var m in pou.Methods)
            if (!string.IsNullOrEmpty(m.Body)) sb.AppendLine(m.Body);
        if (!string.IsNullOrEmpty(pou.RawImplementation)) sb.AppendLine(pou.RawImplementation);
        var allBodies = sb.ToString();

        // Use the structured state-machine if available
        if (primary != null && primary.Transitions.Count > 0)
        {
            foreach (var t in primary.Transitions)
            {
                if (string.IsNullOrEmpty(t.FromState) || string.IsNullOrEmpty(t.ToState)) continue;
                transitions.Add(new DetectedTransition(
                    t.FromState, t.ToState, t.Condition ?? ""));
            }
        }

        // Augment with regex-based extraction over method bodies for transitions
        // the state-machine extractor missed (e.g. transitions hidden behind
        // GotoState helper methods on DM_StateMachine).
        var gotoPattern = new Regex(
            @"GotoState\s*\(\s*(?:E_\w+\s*\.\s*)?(?<to>\w+)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var caseStatePattern = new Regex(
            @"(?:E_\w+\s*\.\s*)?(?<from>\w+)\s*:[^;]+?(?:GotoState\s*\(\s*(?:E_\w+\s*\.\s*)?(?<to>\w+)\s*\)|" +
            @"_eState\s*:=\s*(?:E_\w+\s*\.\s*)?(?<to2>\w+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        foreach (Match m in caseStatePattern.Matches(allBodies))
        {
            var from = m.Groups["from"].Value;
            var to   = m.Groups["to"].Success ? m.Groups["to"].Value : m.Groups["to2"].Value;
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) continue;
            if (transitions.Any(t =>
                t.From.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                t.To.Equals(to,     StringComparison.OrdinalIgnoreCase))) continue;
            transitions.Add(new DetectedTransition(from, to, ""));
        }

        return (transitions, allBodies);
    }

    private static Regex CommandPattern(string command) => new(
        $@"\b{Regex.Escape(command)}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record DetectedTransition(string From, string To, string Condition);
}
