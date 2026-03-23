namespace AxiomCode.TwinCAT.CodeAnaliser.Models;

public enum IsaLayer
{
    UM,
    EM,
    CM,
    DM,
    Other
}

public class ObjectTreeNode
{
    public string InstanceName { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string FullPath { get; set; } = "";
    public string ExtendsChain { get; set; } = "";
    public IsaLayer Layer { get; set; } = IsaLayer.Other;
    public bool IsReference { get; set; }
    public bool HasAlarmsMethod { get; set; }

    public List<ObjectTreeNode> Children { get; set; } = new();
    public List<AlarmInfo> Alarms { get; set; } = new();
    public List<StateMachine> StateMachines { get; set; } = new();
    public List<IoMapping> IoMappings { get; set; } = new();
}
