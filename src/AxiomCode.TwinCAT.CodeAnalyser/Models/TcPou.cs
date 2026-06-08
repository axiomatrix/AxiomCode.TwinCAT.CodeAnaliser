namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

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

    /// <summary>The implementation language of the POU body. ST bodies keep their
    /// text in <see cref="RawImplementation"/>; graphical bodies (FBD/LD/IL/SFC/CFC)
    /// are decoded into <see cref="Graphical"/> and were previously dropped entirely
    /// (the legacy parser read only &lt;Implementation&gt;&lt;ST&gt;).</summary>
    public ImplLanguage Language { get; set; } = ImplLanguage.None;

    /// <summary>Decoded graphical body (networks/boxes/operands) when
    /// <see cref="Language"/> is FBD/LD/IL/SFC/CFC; null for ST or empty bodies.</summary>
    public TcGraphicalImpl? Graphical { get; set; }

    // Resolved inheritance chain (populated by InheritanceResolver)
    public List<string> InheritanceChain { get; set; } = new();
    public List<TcVariable> AllVariables { get; set; } = new(); // Including inherited
}
