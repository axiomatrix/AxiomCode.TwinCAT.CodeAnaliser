using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Evaluates a TcPou or TcProject against IEC 61131-3, OOP/SOLID, ISA-88,
/// PackML and GAMP 5 Category 5 compliance rules.
/// </summary>
public static class ComplianceChecker
{
    // ── PackML canonical state names ────────────────────────────────────────
    private static readonly HashSet<string> PackmlStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle","Starting","Execute","Running","Completing","Complete",
        "Stopping","Stopped","Aborting","Aborted","Clearing","Resetting",
        "Holding","Held","Unholding","Suspending","Suspended","Unsuspending"
    };

    // ── Hungarian-notation type prefixes ────────────────────────────────────
    private static readonly string[] HungarianPrefixes =
        ["b","n","f","lf","s","t","dt","a","st","e","fb","i","p","ref","dw","w","by","di","_"];

    // ── ISA-88 layer prefixes ───────────────────────────────────────────────
    private static readonly string[] IsaPrefixes = ["UM_","EM_","CM_","DM_","FB_","PRG_","FC_"];

    // ═══════════════════════════════════════════════════════════════════════
    //  Module-level compliance
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Evaluate all applicable standards for one POU.
    /// <paramref name="layer"/> is the ISA-88 layer resolved from the object tree (null if not in tree).
    /// <paramref name="nodeSms"/> are state machines detected for this module's ObjectTreeNode.
    /// </summary>
    public static ModuleCompliance EvaluateModule(
        TcPou pou,
        IsaLayer? layer,
        IReadOnlyList<StateMachine>? nodeSms)
    {
        var standards = new List<StandardCompliance>
        {
            CheckIec61131(pou),
            CheckOop(pou),
            CheckIsa88Module(pou, layer),
            CheckPackml(pou, nodeSms),
            CheckGamp5Module(pou, layer),
        };

        return new ModuleCompliance { PouName = pou.Name, Standards = standards };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Project-level compliance
    // ═══════════════════════════════════════════════════════════════════════

    public static ProjectCompliance EvaluateProject(TcProject project)
    {
        var standards = new List<StandardCompliance>
        {
            CheckIec61131Project(project),
            CheckIsa88Project(project),
            CheckGamp5Project(project),
        };
        return new ProjectCompliance { Standards = standards };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  IEC 61131-3
    // ═══════════════════════════════════════════════════════════════════════

    private static StandardCompliance CheckIec61131(TcPou pou)
    {
        var rules = new List<ComplianceRule>();

        // IEC-001 — Type name uses a recognised prefix
        var iecPrefix = IsaPrefixes.Any(p => pou.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || pou.Name.StartsWith("I_", StringComparison.OrdinalIgnoreCase)
            || pou.Name.StartsWith("E_", StringComparison.OrdinalIgnoreCase)
            || pou.Name.StartsWith("ST_", StringComparison.OrdinalIgnoreCase);
        rules.Add(Rule("IEC-001", "Name uses a recognised IEC type prefix (FB_/UM_/EM_/CM_/DM_/E_/ST_/I_)",
            iecPrefix ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            iecPrefix ? null : $"Name '{pou.Name}' has no standard prefix"));

        // IEC-002 — Variable naming follows Hungarian notation (≥80% of locals)
        var namedVars = pou.Variables.Where(v =>
            v.Scope is VarScope.Local or VarScope.Input or VarScope.Output or VarScope.InOut or VarScope.Stat
            && !string.IsNullOrEmpty(v.Name)).ToList();
        if (namedVars.Count == 0)
        {
            rules.Add(Rule("IEC-002", "Variable names follow Hungarian notation", ComplianceLevel.NotApplicable));
        }
        else
        {
            var prefixedCount = namedVars.Count(v => HungarianPrefixes.Any(p =>
                v.Name.StartsWith(p) && v.Name.Length > p.Length && char.IsUpper(v.Name[p.Length])));
            var ratio = (double)prefixedCount / namedVars.Count;
            rules.Add(Rule("IEC-002", "Variable names follow Hungarian notation",
                ratio >= 0.8 ? ComplianceLevel.Pass : ratio >= 0.5 ? ComplianceLevel.Warning : ComplianceLevel.Fail,
                $"{prefixedCount}/{namedVars.Count} variables correctly prefixed ({ratio:P0})"));
        }

        // IEC-003 — Methods have return types declared (non-void methods only)
        var methodsWithBody = pou.Methods.Where(m => m.Name is not "FB_init" and not "FB_exit").ToList();
        if (methodsWithBody.Count == 0)
            rules.Add(Rule("IEC-003", "Methods declare return types", ComplianceLevel.NotApplicable));
        else
        {
            var withReturn = methodsWithBody.Count(m =>
                !string.IsNullOrWhiteSpace(m.ReturnType) || m.ReturnType == null);
            rules.Add(Rule("IEC-003", "Methods declare return types",
                ComplianceLevel.Pass, $"{withReturn}/{methodsWithBody.Count} methods with return type"));
        }

        // IEC-004 — No excessive method body length (>150 lines is a warning; >300 is a fail)
        var longMethods = pou.Methods.Where(m =>
        {
            var lines = m.Body.Split('\n').Length;
            return lines > 150;
        }).ToList();
        var veryLong = longMethods.Where(m => m.Body.Split('\n').Length > 300).ToList();
        rules.Add(Rule("IEC-004", "Method bodies are reasonably sized (≤150 lines recommended)",
            veryLong.Count > 0 ? ComplianceLevel.Fail
            : longMethods.Count > 0 ? ComplianceLevel.Warning : ComplianceLevel.Pass,
            longMethods.Count == 0 ? null :
            $"{longMethods.Count} method(s) exceed 150 lines: {string.Join(", ", longMethods.Select(m => m.Name))}"));

        // IEC-005 — VAR_INPUT/VAR_OUTPUT used for FB interface (FBs only)
        if (pou.PouType == PouType.FunctionBlock)
        {
            var hasInterface = pou.Variables.Any(v => v.Scope is VarScope.Input or VarScope.Output or VarScope.InOut);
            rules.Add(Rule("IEC-005", "Function Block declares VAR_INPUT / VAR_OUTPUT interface",
                hasInterface ? ComplianceLevel.Pass : ComplianceLevel.Warning,
                hasInterface ? null : "No VAR_INPUT or VAR_OUTPUT variables found"));
        }
        else
            rules.Add(Rule("IEC-005", "Function Block declares VAR_INPUT / VAR_OUTPUT interface", ComplianceLevel.NotApplicable));

        return Std(ComplianceStandard.IEC61131_3, "IEC 61131-3",
            "International PLC programming language standard — naming, structure, and typing rules", rules);
    }

    private static StandardCompliance CheckIec61131Project(TcProject project)
    {
        var rules = new List<ComplianceRule>();

        // Project has MAIN program
        var hasMain = project.POUs.Values.Any(p =>
            p.PouType == PouType.Program &&
            (p.Name.Equals("MAIN", StringComparison.OrdinalIgnoreCase) ||
             p.Name.StartsWith("PRG_", StringComparison.OrdinalIgnoreCase)));
        rules.Add(Rule("IEC-P01", "Project has a MAIN / PRG_ entry-point program",
            hasMain ? ComplianceLevel.Pass : ComplianceLevel.Fail,
            hasMain ? null : "No MAIN or PRG_ program found"));

        // Project has DUTs
        rules.Add(Rule("IEC-P02", "Project defines typed DUT data types (E_/ST_/I_)",
            project.DUTs.Any() ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            project.DUTs.Any() ? $"{project.DUTs.Count} DUT(s) defined" : "No DUT files found"));

        // Project has GVLs
        rules.Add(Rule("IEC-P03", "Project uses Global Variable Lists",
            project.GVLs.Any() ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            project.GVLs.Any() ? $"{project.GVLs.Count} GVL(s) found" : "No GVL files found"));

        // No unresolved types
        rules.Add(Rule("IEC-P04", "All variable types resolve to known declarations",
            project.UnresolvedTypes.Count == 0 ? ComplianceLevel.Pass
            : project.UnresolvedTypes.Count <= 5 ? ComplianceLevel.Warning : ComplianceLevel.Fail,
            project.UnresolvedTypes.Count == 0 ? null :
            $"{project.UnresolvedTypes.Count} unresolved types: {string.Join(", ", project.UnresolvedTypes.Take(5))}"));

        return Std(ComplianceStandard.IEC61131_3, "IEC 61131-3",
            "International PLC programming language standard — project structure", rules);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  OOP / SOLID
    // ═══════════════════════════════════════════════════════════════════════

    private static StandardCompliance CheckOop(TcPou pou)
    {
        var rules = new List<ComplianceRule>();

        if (pou.PouType != PouType.FunctionBlock)
        {
            // OOP rules don't apply to Programs or Functions
            return Std(ComplianceStandard.OOP_SOLID, "OOP / SOLID",
                "Object-oriented design principles for TwinCAT Function Blocks",
                [Rule("OOP-000", "OOP rules apply to Function Blocks only", ComplianceLevel.NotApplicable)]);
        }

        // OOP-001 — Has constructor (FB_init)
        var hasInit = pou.Methods.Any(m => m.Name.Equals("FB_init", StringComparison.OrdinalIgnoreCase));
        rules.Add(Rule("OOP-001", "Declares FB_init constructor for proper initialisation",
            hasInit ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            hasInit ? null : "FB_init not found — instance variables may not be initialised"));

        // OOP-002 — Has destructor (FB_exit)
        var hasExit = pou.Methods.Any(m => m.Name.Equals("FB_exit", StringComparison.OrdinalIgnoreCase));
        rules.Add(Rule("OOP-002", "Declares FB_exit destructor for safe shutdown",
            hasExit ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            hasExit ? null : "FB_exit not found — no guaranteed safe-state on unload"));

        // OOP-003 — Extends a base class (Open/Closed Principle)
        rules.Add(Rule("OOP-003", "Extends a base class (Open/Closed Principle — open for extension)",
            !string.IsNullOrEmpty(pou.ExtendsType) ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            !string.IsNullOrEmpty(pou.ExtendsType) ? $"EXTENDS {pou.ExtendsType}" : "No EXTENDS declaration"));

        // OOP-004 — Implements at least one interface (Dependency Inversion / Liskov)
        rules.Add(Rule("OOP-004", "Implements at least one interface (Dependency Inversion Principle)",
            pou.ImplementsList.Any() ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            pou.ImplementsList.Any()
                ? $"IMPLEMENTS {string.Join(", ", pou.ImplementsList)}"
                : "No IMPLEMENTS declaration — not substitutable via interface"));

        // OOP-005 — Private members use underscore prefix (Encapsulation / SRP)
        var localVars = pou.Variables.Where(v => v.Scope == VarScope.Local).ToList();
        if (localVars.Count == 0)
            rules.Add(Rule("OOP-005", "Private variables use _ prefix for encapsulation", ComplianceLevel.NotApplicable));
        else
        {
            var privateCount = localVars.Count(v => v.Name.StartsWith("_"));
            rules.Add(Rule("OOP-005", "Private variables use _ prefix for encapsulation",
                privateCount > 0 ? ComplianceLevel.Pass : ComplianceLevel.Warning,
                $"{privateCount}/{localVars.Count} local variables use _ prefix"));
        }

        // OOP-006 — Has Properties (Encapsulation over raw VAR_OUTPUT)
        rules.Add(Rule("OOP-006", "Exposes typed Properties for encapsulated access",
            pou.Properties.Any() ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            pou.Properties.Any() ? $"{pou.Properties.Count} property(ies) declared"
            : "No properties — raw VAR_OUTPUT limits encapsulation"));

        // OOP-007 — Has error outputs (I_ErrorHandler pattern)
        var hasErrorOut = pou.Variables.Any(v =>
            v.Scope == VarScope.Output &&
            (v.Name.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
             v.Name.Equals("bError", StringComparison.OrdinalIgnoreCase)));
        rules.Add(Rule("OOP-007", "Exposes error status output (bError / nErrorId pattern)",
            hasErrorOut ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            hasErrorOut ? null : "No error output found — callers cannot detect failures"));

        // OOP-008 — Has non-public methods (SRP — internal logic hidden)
        var nonPublic = pou.Methods.Where(m =>
            m.Visibility is Visibility.Private or Visibility.Protected or Visibility.Internal).ToList();
        if (pou.Methods.Count == 0)
            rules.Add(Rule("OOP-008", "Uses access modifiers (private/protected) for internal methods", ComplianceLevel.NotApplicable));
        else
            rules.Add(Rule("OOP-008", "Uses access modifiers (private/protected) for internal methods",
                nonPublic.Any() ? ComplianceLevel.Pass : ComplianceLevel.Warning,
                nonPublic.Any() ? $"{nonPublic.Count} non-public method(s)"
                : "All methods are PUBLIC — internal logic not encapsulated"));

        return Std(ComplianceStandard.OOP_SOLID, "OOP / SOLID",
            "SOLID object-oriented design principles applied to TwinCAT Structured Text", rules);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ISA-88
    // ═══════════════════════════════════════════════════════════════════════

    private static StandardCompliance CheckIsa88Module(TcPou pou, IsaLayer? layer)
    {
        var rules = new List<ComplianceRule>();

        // ISA-001 — Module uses an ISA-88 layer prefix
        var resolvedLayer = layer ?? ResolveLayer(pou.Name);
        var hasIsaPrefix  = resolvedLayer != IsaLayer.Other;
        rules.Add(Rule("ISA-001", "Module name uses ISA-88 layer prefix (UM_/EM_/CM_/DM_)",
            hasIsaPrefix ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            hasIsaPrefix ? $"Layer: {resolvedLayer}" : $"'{pou.Name}' has no ISA-88 layer prefix"));

        // ISA-002 — Module has _Alarms method (UM/EM only)
        if (resolvedLayer is IsaLayer.UM or IsaLayer.EM)
        {
            var hasAlarms = pou.Methods.Any(m =>
                m.Name.Equals("_Alarms", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals("Alarms",  StringComparison.OrdinalIgnoreCase));
            rules.Add(Rule("ISA-002", "UM/EM declares _Alarms method for alarm aggregation",
                hasAlarms ? ComplianceLevel.Pass : ComplianceLevel.Fail,
                hasAlarms ? null : "_Alarms method missing — alarms will not propagate up the hierarchy"));
        }
        else
            rules.Add(Rule("ISA-002", "UM/EM declares _Alarms method", ComplianceLevel.NotApplicable));

        // ISA-003 — CM has _Simulate method (simulation support)
        if (resolvedLayer == IsaLayer.CM)
        {
            var hasSim = pou.Methods.Any(m =>
                m.Name.Contains("Simulat", StringComparison.OrdinalIgnoreCase));
            rules.Add(Rule("ISA-003", "CM declares simulation method (_Simulate) for hardware-free testing",
                hasSim ? ComplianceLevel.Pass : ComplianceLevel.Warning,
                hasSim ? null : "No simulation method — CM cannot run without physical hardware"));
        }
        else
            rules.Add(Rule("ISA-003", "CM declares simulation method", ComplianceLevel.NotApplicable));

        // ISA-004 — CM I/O encapsulation: AT-linked vars only inside CM_
        if (resolvedLayer != IsaLayer.CM && pou.PouType == PouType.FunctionBlock)
        {
            var ioVars = pou.Variables.Where(v => !string.IsNullOrEmpty(v.AtBinding)).ToList();
            rules.Add(Rule("ISA-004", "I/O only in CM modules — no AT-linked vars in UM/EM/DM/FB",
                ioVars.Count == 0 ? ComplianceLevel.Pass : ComplianceLevel.Fail,
                ioVars.Count == 0 ? null :
                $"{ioVars.Count} AT-linked variable(s) outside a CM: {string.Join(", ", ioVars.Select(v => v.Name).Take(5))}"));
        }
        else
            rules.Add(Rule("ISA-004", "I/O encapsulation (AT links only in CM)", ComplianceLevel.NotApplicable));

        // ISA-005 — UM/EM has mode execution methods (_AutoMode / _ManualMode / similar)
        if (resolvedLayer is IsaLayer.UM or IsaLayer.EM)
        {
            var hasModeMethod = pou.Methods.Any(m =>
                m.Name.Contains("Auto",   StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Manual", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Mode",   StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Phase",  StringComparison.OrdinalIgnoreCase) ||
                m.Name.Contains("Operation", StringComparison.OrdinalIgnoreCase));
            rules.Add(Rule("ISA-005", "UM/EM implements mode/phase execution methods",
                hasModeMethod ? ComplianceLevel.Pass : ComplianceLevel.Warning,
                hasModeMethod ? null : "No Auto/Manual/Mode/Phase methods found"));
        }
        else
            rules.Add(Rule("ISA-005", "UM/EM mode/phase methods", ComplianceLevel.NotApplicable));

        // ISA-006 — Module extends the appropriate base class
        var expectedBase = resolvedLayer switch
        {
            IsaLayer.UM or IsaLayer.EM => "BaseClass",  // UM_BASECLASS / MODE_PHASE_BASECLASS
            IsaLayer.CM                => "CM_BASECLASS",
            _                          => null
        };
        if (expectedBase != null)
        {
            var extendsBase = !string.IsNullOrEmpty(pou.ExtendsType) &&
                pou.ExtendsType.Contains("BASECLASS", StringComparison.OrdinalIgnoreCase);
            rules.Add(Rule("ISA-006", $"Module extends the appropriate ISA-88 base class",
                extendsBase ? ComplianceLevel.Pass : ComplianceLevel.Warning,
                extendsBase ? $"EXTENDS {pou.ExtendsType}"
                : string.IsNullOrEmpty(pou.ExtendsType)
                    ? $"No EXTENDS found — expected a *_BASECLASS for {resolvedLayer}"
                    : $"EXTENDS {pou.ExtendsType} — does not appear to be a standard BASECLASS"));
        }
        else
            rules.Add(Rule("ISA-006", "Base class inheritance", ComplianceLevel.NotApplicable));

        return Std(ComplianceStandard.ISA88, "ISA-88",
            "ISA-88 modular machine hierarchy — UM/EM/CM/DM layer responsibilities and patterns", rules);
    }

    private static StandardCompliance CheckIsa88Project(TcProject project)
    {
        var rules = new List<ComplianceRule>();

        // Project has Objects GVL
        var hasObjects = project.GVLs.Values.Any(g =>
            g.Name.Contains("Objects", StringComparison.OrdinalIgnoreCase));
        rules.Add(Rule("ISA-P01", "Objects GVL exists as root of ISA-88 object tree",
            hasObjects ? ComplianceLevel.Pass : ComplianceLevel.Fail,
            hasObjects ? null : "No 'Objects' GVL found — ISA-88 tree cannot be built"));

        // Project has UM
        var hasUm = project.POUs.Values.Any(p => p.Name.StartsWith("UM_", StringComparison.OrdinalIgnoreCase));
        rules.Add(Rule("ISA-P02", "At least one Unit Module (UM_) exists",
            hasUm ? ComplianceLevel.Pass : ComplianceLevel.Fail,
            hasUm ? null : "No UM_ module found"));

        // Project has CMs
        var cmCount = project.POUs.Values.Count(p => p.Name.StartsWith("CM_", StringComparison.OrdinalIgnoreCase));
        rules.Add(Rule("ISA-P03", "Control Modules (CM_) encapsulate all device I/O",
            cmCount > 0 ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            $"{cmCount} CM_ module(s) found"));

        // Project has EMs
        var emCount = project.POUs.Values.Count(p => p.Name.StartsWith("EM_", StringComparison.OrdinalIgnoreCase));
        rules.Add(Rule("ISA-P04", "Equipment Modules (EM_) organise subsystems",
            emCount > 0 ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            $"{emCount} EM_ module(s) found"));

        // No AT-linked vars outside CM
        var ioOutsideCm = project.POUs.Values
            .Where(p => ResolveLayer(p.Name) != IsaLayer.CM)
            .SelectMany(p => p.Variables.Where(v => !string.IsNullOrEmpty(v.AtBinding))
                .Select(v => (Pou: p.Name, Var: v.Name)))
            .Take(10).ToList();
        rules.Add(Rule("ISA-P05", "Hardware I/O (AT-linked variables) confined to CM modules only",
            ioOutsideCm.Count == 0 ? ComplianceLevel.Pass : ComplianceLevel.Fail,
            ioOutsideCm.Count == 0 ? null :
            $"I/O outside CMs: {string.Join(", ", ioOutsideCm.Select(x => $"{x.Pou}.{x.Var}"))}"));

        return Std(ComplianceStandard.ISA88, "ISA-88",
            "ISA-88 project-level hierarchy and I/O encapsulation", rules);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PackML
    // ═══════════════════════════════════════════════════════════════════════

    private static StandardCompliance CheckPackml(TcPou pou, IReadOnlyList<StateMachine>? sms)
    {
        var dmSms = sms?.Where(sm => sm.DetectedBy == SmDetectionStrategy.DmStateMachine).ToList()
                   ?? [];

        if (dmSms.Count == 0)
        {
            return Std(ComplianceStandard.PackML, "PackML",
                "OMAC / ISA-88 machine state model — 17 canonical states and transitions",
                [Rule("PKM-000", "PackML rules apply when DM_StateMachine instances are detected",
                    ComplianceLevel.NotApplicable)]);
        }

        var rules = new List<ComplianceRule>();

        foreach (var sm in dmSms.Take(3))   // Check up to 3 SMs per module
        {
            var stateNames = sm.States.Select(s => s.Name).ToList();
            var matchCount = stateNames.Count(n =>
                PackmlStates.Any(p => n.Contains(p, StringComparison.OrdinalIgnoreCase)));

            // PKM-001 — ≥10 of 17 canonical states present
            rules.Add(Rule($"PKM-001",
                $"'{sm.DisplayName ?? sm.EnumTypeName}' covers ≥10 PackML canonical states",
                matchCount >= 10 ? ComplianceLevel.Pass
                : matchCount >= 6 ? ComplianceLevel.Warning : ComplianceLevel.Fail,
                $"{matchCount}/17 PackML states matched ({stateNames.Count} total states)"));

            // PKM-002 — Abort path
            var hasAbort = stateNames.Any(n => n.Contains("Abort", StringComparison.OrdinalIgnoreCase));
            rules.Add(Rule("PKM-002", $"'{sm.DisplayName ?? sm.EnumTypeName}' includes Abort/Aborting state",
                hasAbort ? ComplianceLevel.Pass : ComplianceLevel.Fail,
                hasAbort ? null : "No Abort state — machine cannot meet PackML emergency stop requirements"));

            // PKM-003 — Execute / Running state
            var hasExecute = stateNames.Any(n =>
                n.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Running", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Operation", StringComparison.OrdinalIgnoreCase));
            rules.Add(Rule("PKM-003", $"'{sm.DisplayName ?? sm.EnumTypeName}' includes Execute/Running state",
                hasExecute ? ComplianceLevel.Pass : ComplianceLevel.Fail,
                hasExecute ? null : "No Execute or Running state detected"));

            // PKM-004 — Error/fault handling state
            var hasError = sm.States.Any(s => s.IsError) ||
                stateNames.Any(n => n.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                                    n.Contains("Fault", StringComparison.OrdinalIgnoreCase));
            rules.Add(Rule("PKM-004", $"'{sm.DisplayName ?? sm.EnumTypeName}' includes error/fault state",
                hasError ? ComplianceLevel.Pass : ComplianceLevel.Warning,
                hasError ? null : "No error or fault state detected"));
        }

        return Std(ComplianceStandard.PackML, "PackML",
            "OMAC / ISA-88 machine state model — 17 canonical states and required transitions", rules);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GAMP 5 Category 5
    // ═══════════════════════════════════════════════════════════════════════

    private static StandardCompliance CheckGamp5Module(TcPou pou, IsaLayer? layer)
    {
        var rules = new List<ComplianceRule>();

        // GAMP-001 — Module has documented inputs
        var inputs = pou.Variables.Where(v => v.Scope is VarScope.Input or VarScope.InOut).ToList();
        rules.Add(Rule("GAMP-001", "Module API has documented inputs (VAR_INPUT/VAR_IN_OUT)",
            inputs.Any() ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            inputs.Any() ? $"{inputs.Count} input(s) declared" : "No inputs — module may have undocumented dependencies"));

        // GAMP-002 — Module has documented outputs
        var outputs = pou.Variables.Where(v => v.Scope == VarScope.Output).ToList();
        rules.Add(Rule("GAMP-002", "Module API has documented outputs (VAR_OUTPUT)",
            outputs.Any() ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            outputs.Any() ? $"{outputs.Count} output(s) declared" : "No outputs — status/results not surfaced"));

        // GAMP-003 — Module has alarm handling method
        var hasAlarms = pou.Methods.Any(m =>
            m.Name.Contains("Alarm", StringComparison.OrdinalIgnoreCase));
        rules.Add(Rule("GAMP-003", "Module implements alarm handling (_Alarms method)",
            hasAlarms ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            hasAlarms ? null : "No alarm method — faults may be untracked in validation records"));

        // GAMP-004 — Traceable hierarchy (ISA-88 prefix)
        var resolvedLayer = layer ?? ResolveLayer(pou.Name);
        rules.Add(Rule("GAMP-004", "Module has ISA-88 layer prefix for traceability (UM_/EM_/CM_/DM_)",
            resolvedLayer != IsaLayer.Other ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            resolvedLayer != IsaLayer.Other ? $"Layer: {resolvedLayer}"
            : "No ISA-88 prefix — module is harder to trace in validation documentation"));

        // GAMP-005 — Extends a base class (traceable inheritance chain)
        rules.Add(Rule("GAMP-005", "Extends a base class to ensure traceable inheritance chain",
            !string.IsNullOrEmpty(pou.ExtendsType) ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            !string.IsNullOrEmpty(pou.ExtendsType) ? $"EXTENDS {pou.ExtendsType}"
            : "No base class — inheritance chain is not traceable in SMDS"));

        // GAMP-006 — Has non-trivial implementation
        var bodyLength = pou.RawImplementation.Trim().Length
                       + pou.Methods.Sum(m => m.Body.Trim().Length);
        rules.Add(Rule("GAMP-006", "Module contains non-trivial implementation suitable for SMDS documentation",
            bodyLength > 100 ? ComplianceLevel.Pass
            : bodyLength > 0 ? ComplianceLevel.Warning : ComplianceLevel.Fail,
            bodyLength > 100 ? null : "Minimal implementation — SMDS content will be sparse"));

        // GAMP-007 — Module name and prefix make it uniquely identifiable
        rules.Add(Rule("GAMP-007", "Module name is unique and identifiable (no generic names)",
            !IsGenericName(pou.Name) ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            IsGenericName(pou.Name) ? $"'{pou.Name}' is a generic name — not uniquely identifiable" : null));

        return Std(ComplianceStandard.GAMP5, "GAMP 5 Cat.5",
            "GAMP 5 Category 5 custom software — documentation and traceability requirements for regulated environments", rules);
    }

    private static StandardCompliance CheckGamp5Project(TcProject project)
    {
        var rules = new List<ComplianceRule>();

        // All UM/EM/CM modules have alarm methods
        var ismLayers = new[] { "UM_", "EM_", "CM_" };
        var modulesWithoutAlarms = project.POUs.Values
            .Where(p => ismLayers.Any(l => p.Name.StartsWith(l, StringComparison.OrdinalIgnoreCase))
                     && !p.Methods.Any(m => m.Name.Contains("Alarm", StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name).ToList();
        rules.Add(Rule("GAMP-P01", "All UM/EM/CM modules implement alarm handling",
            modulesWithoutAlarms.Count == 0 ? ComplianceLevel.Pass
            : modulesWithoutAlarms.Count <= 3 ? ComplianceLevel.Warning : ComplianceLevel.Fail,
            modulesWithoutAlarms.Count == 0 ? null :
            $"{modulesWithoutAlarms.Count} module(s) missing alarms: {string.Join(", ", modulesWithoutAlarms.Take(5))}"));

        // Project has enumerated types (E_ DUTs) — needed for state machine traceability
        var enumCount = project.DUTs.Values.Count(d => d.DutType == DutType.Enum);
        rules.Add(Rule("GAMP-P02", "Project defines enumerated types (E_) for state machine traceability",
            enumCount > 0 ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            $"{enumCount} E_ enumeration(s) found"));

        // Project has structured types (ST_ DUTs) for settings/data documentation
        var structCount = project.DUTs.Values.Count(d => d.DutType == DutType.Struct);
        rules.Add(Rule("GAMP-P03", "Project defines structure types (ST_) for settings and data documentation",
            structCount > 0 ? ComplianceLevel.Pass : ComplianceLevel.Warning,
            $"{structCount} ST_ structure(s) found"));

        // State machines formally declared
        var formalSmCount = project.AllStateMachines.Count(sm =>
            sm.DetectedBy == SmDetectionStrategy.DmStateMachine);
        var informalSmCount = project.AllStateMachines.Count(sm =>
            sm.DetectedBy == SmDetectionStrategy.DirectEnumCase);
        rules.Add(Rule("GAMP-P04", "Operational sequences use formal DM_StateMachine declarations",
            formalSmCount > 0 ? ComplianceLevel.Pass
            : informalSmCount > 0 ? ComplianceLevel.Warning : ComplianceLevel.NotApplicable,
            formalSmCount > 0 ? $"{formalSmCount} formal SM(s) + {informalSmCount} direct CASE-on-enum flow(s)"
            : informalSmCount > 0 ? $"Only {informalSmCount} informal CASE-on-enum flow(s) — consider DM_StateMachine"
            : null));

        return Std(ComplianceStandard.GAMP5, "GAMP 5 Cat.5",
            "GAMP 5 Category 5 custom software — project-level documentation and traceability", rules);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static IsaLayer ResolveLayer(string name) =>
        name.StartsWith("UM_", StringComparison.OrdinalIgnoreCase) ? IsaLayer.UM :
        name.StartsWith("EM_", StringComparison.OrdinalIgnoreCase) ? IsaLayer.EM :
        name.StartsWith("CM_", StringComparison.OrdinalIgnoreCase) ? IsaLayer.CM :
        name.StartsWith("DM_", StringComparison.OrdinalIgnoreCase) ? IsaLayer.DM : IsaLayer.Other;

    private static bool IsGenericName(string name)
    {
        var lower = name.ToLower().TrimStart('_');
        return lower is "test" or "main" or "module" or "base" or "temp" or "helper"
                     or "utils" or "utility" or "misc" or "common";
    }

    private static ComplianceRule Rule(string id, string desc, ComplianceLevel level, string? detail = null) =>
        new() { RuleId = id, Description = desc, Level = level, Detail = detail };

    private static StandardCompliance Std(ComplianceStandard std, string label, string desc,
        List<ComplianceRule> rules) =>
        new() { Standard = std, Label = label, Description = desc, Rules = rules };
}
