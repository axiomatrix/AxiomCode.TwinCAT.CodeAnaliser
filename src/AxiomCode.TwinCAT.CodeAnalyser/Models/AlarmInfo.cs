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
}
