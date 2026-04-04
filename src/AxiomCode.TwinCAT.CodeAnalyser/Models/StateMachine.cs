namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

/// <summary>Indicates how a state machine was detected by StateMachineExtractor.</summary>
public enum SmDetectionStrategy
{
    /// <summary>Explicitly declared as a DM_StateMachine instance with GotoState API.</summary>
    DmStateMachine,
    /// <summary>Detected as a direct CASE-on-enum-variable pattern (implicit flow).</summary>
    DirectEnumCase
}

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

    /// <summary>How this state machine was detected — affects which tab it appears in.</summary>
    public SmDetectionStrategy DetectedBy { get; set; } = SmDetectionStrategy.DmStateMachine;

    public List<StateMachineState> States { get; set; } = new();
    public List<StateTransition> Transitions { get; set; } = new();
}
