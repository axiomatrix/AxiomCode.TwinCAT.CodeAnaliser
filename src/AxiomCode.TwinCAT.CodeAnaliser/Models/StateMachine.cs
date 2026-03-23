namespace AxiomCode.TwinCAT.CodeAnaliser.Models;

public class StateMachineState
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
    public string CodeBody { get; set; } = "";
    public string? MethodName { get; set; }
    public bool IsInitial { get; set; }
    public bool IsError { get; set; }
    public bool IsTimeout { get; set; }
    public bool IsTransition { get; set; }
}

public class StateMachine
{
    public string InstanceName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string EnumTypeName { get; set; } = "";
    public string? InitialState { get; set; }
    public string? TransitionState { get; set; }
    public string OwnerPou { get; set; } = "";

    public List<StateMachineState> States { get; set; } = new();
    public List<StateTransition> Transitions { get; set; } = new();
}
