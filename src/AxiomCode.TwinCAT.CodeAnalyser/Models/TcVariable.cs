namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public enum VarScope
{
    Local,
    Constant,
    Input,
    Output,
    InOut,
    Persistent,
    Stat,
    Temp,
    Global
}

public class TcVariable
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public VarScope Scope { get; set; } = VarScope.Local;
    public string? InitialValue { get; set; }
    public string? Comment { get; set; }
    public string? LeadingComment { get; set; }

    // Type qualifiers
    public bool IsReference { get; set; }
    public bool IsPointer { get; set; }
    public bool IsArray { get; set; }
    public string? ArrayBounds { get; set; }

    // AT binding (IO mapping)
    public string? AtBinding { get; set; }

    // FB constructor args: e.g. DM_StateMachine('Name', InitState, TransState)
    public string? ConstructorArgs { get; set; }

    /// <summary>The unwrapped type for REFERENCE TO / POINTER TO.</summary>
    public string BaseType => IsReference || IsPointer ? DataType : DataType;
}
