namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

/// <summary>Standards that TwinStack can evaluate a module or project against.</summary>
public enum ComplianceStandard
{
    IEC61131_3,
    OOP_SOLID,
    ISA88,
    PackML,
    GAMP5,
    /// <summary>Architectural integrity: cycles, override discipline, abstract contracts, dispatch consistency.</summary>
    ArchitecturalIntegrity,
    /// <summary>Identifier naming conventions (ISA-88 prefixes, casing, reserved words).</summary>
    NamingConventions,
    /// <summary>Source-file structure: file ending, property VAR blocks, element ordering.</summary>
    CodeStructure,
    /// <summary>Source style: tabs, indentation, excessive blank lines, CDATA formatting.</summary>
    CodeStyle,
    /// <summary>XML / GUID integrity: GUID format and uniqueness across the project.</summary>
    XmlIntegrity,
}

/// <summary>Outcome of a single compliance rule check.</summary>
public enum ComplianceLevel
{
    Pass,
    Warning,
    Fail,
    NotApplicable
}

/// <summary>Result of evaluating a single compliance rule.</summary>
public class ComplianceRule
{
    public required string RuleId      { get; init; }
    public required string Description { get; init; }
    public required ComplianceLevel Level { get; init; }
    /// <summary>Optional detail explaining pass/fail evidence.</summary>
    public string? Detail { get; init; }
}

/// <summary>Aggregated results for one standard against one module or project.</summary>
public class StandardCompliance
{
    public required ComplianceStandard Standard    { get; init; }
    public required string Label       { get; init; }  // "IEC 61131-3"
    public required string Description { get; init; }  // one-line summary
    public List<ComplianceRule> Rules  { get; init; } = [];

    /// <summary>
    /// Optional rich PackML breakdown. Non-null only for the PackML standard.
    /// When set, the Compliance UI renders the five-section detail (states,
    /// transitions, commands, modes, errors) in the expander body instead of
    /// the flat rule list.
    /// </summary>
    public PackMlComplianceResult? PackMlDetail { get; init; }

    public int PassCount    => Rules.Count(r => r.Level == ComplianceLevel.Pass);
    public int WarningCount => Rules.Count(r => r.Level == ComplianceLevel.Warning);
    public int FailCount    => Rules.Count(r => r.Level == ComplianceLevel.Fail);
    public int TotalChecked => Rules.Count(r => r.Level != ComplianceLevel.NotApplicable);

    /// <summary>0.0–1.0. Warnings count as 0.5 of a pass.</summary>
    public double Score => TotalChecked == 0 ? 1.0
        : (PassCount + WarningCount * 0.5) / TotalChecked;

    /// <summary>0–100 integer percent for UI display.</summary>
    public int ScorePercent => (int)Math.Round(Score * 100);

    /// <summary>Compliant when score ≥ 0.8 with zero hard failures.</summary>
    public bool IsCompliant => Score >= 0.8 && FailCount == 0;

    /// <summary>
    /// RAG verdict for the row-level badge:
    /// NotApplicable when nothing was checked; Fail if any hard failure;
    /// Pass ≥ 90%; Warning 60–89%; otherwise Fail.
    /// </summary>
    public ComplianceLevel OverallLevel => TotalChecked == 0 ? ComplianceLevel.NotApplicable
        : FailCount > 0   ? ComplianceLevel.Fail
        : Score >= 0.9    ? ComplianceLevel.Pass
        : Score >= 0.6    ? ComplianceLevel.Warning
        :                   ComplianceLevel.Fail;
}

/// <summary>All compliance results for one POU/module.</summary>
public class ModuleCompliance
{
    public required string PouName        { get; init; }
    public List<StandardCompliance> Standards { get; init; } = [];

    public double OverallScore => Standards.Count == 0 ? 1.0
        : Standards.Average(s => s.Score);

    public bool IsOverallCompliant => Standards.All(s => s.IsCompliant);
}

/// <summary>Project-level compliance aggregated across all modules.</summary>
public class ProjectCompliance
{
    public List<StandardCompliance> Standards { get; init; } = [];
    public double OverallScore => Standards.Count == 0 ? 1.0
        : Standards.Average(s => s.Score);
}
