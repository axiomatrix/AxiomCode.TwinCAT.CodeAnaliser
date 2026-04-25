namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

/// <summary>
/// Per-module PackML conformance report. Goes beyond simple state-name matching
/// to validate transitions, operator-command bindings, mode coverage, and error
/// propagation against the canonical PackML / OMAC PackTags v3 specification.
///
/// Produced by <see cref="Services.PackMlAnalyzer"/>; consumed by:
///   - <see cref="ComplianceChecker"/> as a fifth standard
///   - The dedicated PackML tab in TwinStack's Analysis page
///   - The SDS Appendix D conformance matrix
///   - The SMDS §10 expanded coverage section
/// </summary>
public sealed class PackMlComplianceResult
{
    public required string ModuleName { get; init; }

    public StateCoverageSection      States      { get; init; } = new();
    public TransitionSection         Transitions { get; init; } = new();
    public OperatorCommandSection    Commands    { get; init; } = new();
    public ModeCoverageSection       Modes       { get; init; } = new();
    public ErrorHandlingSection      Errors      { get; init; } = new();

    /// <summary>0..100 conformance score: weighted sum across the five sections.</summary>
    public int ConformancePercent { get; set; }

    /// <summary>"Conformant" / "Partial" / "Non-conformant" / "Not Applicable".</summary>
    public string Status { get; set; } = "Not Applicable";

    /// <summary>True when no PackML state machine was detected and the report is informational only.</summary>
    public bool IsApplicable { get; set; }
}

public sealed class StateCoverageSection
{
    /// <summary>Implemented: state-handler method present in the FB (`_<State>`).</summary>
    public List<StateCoverageEntry> Entries { get; init; } = new();
    public int ImplementedCount => Entries.Count(e => e.Implemented);
    public int RequiredCount    => Entries.Count;
    public int CorePresent      => Entries.Count(e => e.IsCore && e.Implemented);
    public int CoreRequired     => Entries.Count(e => e.IsCore);
}

public sealed class StateCoverageEntry
{
    public required string State        { get; init; }   // "Idle" / "Starting" / ...
    public required bool   Implemented  { get; init; }
    public required bool   IsCore       { get; init; }   // Idle / Execute / Stopped / Resetting
    public string?         MethodName   { get; init; }
    public bool            CallsSuper   { get; init; }
}

public sealed class TransitionSection
{
    public List<TransitionEntry> Entries { get; init; } = new();
    public int PresentCount  => Entries.Count(e => e.Present);
    public int RequiredCount => Entries.Count;
}

public sealed class TransitionEntry
{
    public required string From          { get; init; }
    public required string To            { get; init; }
    public required string TriggerLabel  { get; init; }   // e.g. "CmdStart" / "auto/done"
    public required bool   IsAutoTransition { get; init; } // automatic transition on completion
    public required bool   Present       { get; init; }
    public string?         DetectedCondition { get; init; } // raw condition from source if found
}

public sealed class OperatorCommandSection
{
    public List<CommandEntry> Entries { get; init; } = new();
    public int BoundCount    => Entries.Count(e => e.Bound);
    public int RequiredCount => Entries.Count;
}

public sealed class CommandEntry
{
    public required string Name       { get; init; }   // CmdStart / CmdStop / ...
    public required bool   IsRequired { get; init; }
    public required bool   Bound      { get; init; }
    public string?         BoundVariable { get; init; }
}

public sealed class ModeCoverageSection
{
    public List<ModeEntry> Entries { get; init; } = new();
    public int ImplementedCount => Entries.Count(e => e.Implemented);
    public int RequiredCount    => Entries.Count;
}

public sealed class ModeEntry
{
    public required string Name        { get; init; }   // Production / Manual / Maintenance
    public required bool   IsRequired  { get; init; }
    public required bool   Implemented { get; init; }
    public string?         Detail      { get; init; }
}

public sealed class ErrorHandlingSection
{
    public bool HasAbortingState        { get; set; }
    public bool HasErrorAggregation     { get; set; }   // _AlarmsPresent → transition
    public bool HasResetPathFromError   { get; set; }
    public List<string> Findings        { get; set; } = new();
    public int Score => (HasAbortingState ? 1 : 0)
                       + (HasErrorAggregation ? 1 : 0)
                       + (HasResetPathFromError ? 1 : 0);
}
