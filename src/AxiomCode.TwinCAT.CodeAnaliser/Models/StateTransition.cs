namespace AxiomCode.TwinCAT.CodeAnaliser.Models;

public class StateTransition
{
    public string FromState { get; set; } = "";
    public string ToState { get; set; } = "";
    public string? Condition { get; set; }
    public string? TimeoutValue { get; set; }
    public string? MethodName { get; set; }
    public int? LineNumber { get; set; }
}
