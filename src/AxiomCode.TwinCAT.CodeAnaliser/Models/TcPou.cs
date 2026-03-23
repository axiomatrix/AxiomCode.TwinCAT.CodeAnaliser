namespace AxiomCode.TwinCAT.CodeAnaliser.Models;

public enum PouType
{
    FunctionBlock,
    Program,
    Function,
    Interface
}

public class TcPou
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public PouType PouType { get; set; } = PouType.FunctionBlock;
    public bool IsAbstract { get; set; }

    // Inheritance
    public string? ExtendsType { get; set; }
    public List<string> ImplementsList { get; set; } = new();

    // Members
    public List<TcVariable> Variables { get; set; } = new();
    public List<TcMethod> Methods { get; set; } = new();
    public List<TcProperty> Properties { get; set; } = new();

    // Raw text
    public string RawDeclaration { get; set; } = "";
    public string RawImplementation { get; set; } = "";

    // Resolved inheritance chain (populated by InheritanceResolver)
    public List<string> InheritanceChain { get; set; } = new();
    public List<TcVariable> AllVariables { get; set; } = new(); // Including inherited
}
