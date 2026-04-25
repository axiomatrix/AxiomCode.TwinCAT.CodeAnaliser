namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public enum AlarmSeverity
{
    Critical,
    Process,
    Advisory,
    Information,
    Unresolved
}

public enum UnresolvedReason
{
    None,
    BaseClass,
    NoMethod,
    Missing,
    Dead
}

public class AlarmInfo
{
    public string InstanceName { get; set; } = "";
    public AlarmSeverity Severity { get; set; } = AlarmSeverity.Unresolved;
    public UnresolvedReason UnresolvedReason { get; set; } = UnresolvedReason.None;
    public string UnresolvedReasonText { get; set; } = "";

    // Module context
    public string ModulePath { get; set; } = "";
    public string ModuleType { get; set; } = "";
    public string VariablePath { get; set; } = "";

    // Parsed from code
    public string? TriggerCondition { get; set; }
    public int? TriggerDelayMs { get; set; }
    public string Condition { get; set; } = "";

    /// <summary>
    /// Human-readable description of what this alarm actually means — what the
    /// runtime situation is when it raises, and what action it implies. Populated
    /// by <see cref="Services.AlarmDescriptionEnricher"/> using a heuristic
    /// fallback at parse time and overridden by AI-interpreted prose when an
    /// upstream interpretation pass has produced one.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Where the description came from — useful for transparency in the UI:
    /// "heuristic", "ai-interpretation", "manual-override".
    /// </summary>
    public string DescriptionSource { get; set; } = "";

    /// <summary>
    /// Operator action implied by the alarm (e.g. "operator confirms compressed-air
    /// supply, then resets via the alarm-reset workflow"). Populated only when an
    /// AI interpretation supplies it.
    /// </summary>
    public string? RecommendedAction { get; set; }

    /// <summary>
    /// True if this alarm was found in a POU definition that is NOT instantiated
    /// in the object tree. These are alarm definitions from templates, base classes,
    /// or FBs not yet wired into the machine hierarchy.
    /// </summary>
    public bool IsDefinitionOnly { get; set; } = false;
}
