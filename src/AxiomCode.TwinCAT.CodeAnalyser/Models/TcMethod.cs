namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public enum Visibility
{
    Public,
    Protected,
    Private,
    Internal
}

public class TcMethod
{
    public string Name { get; set; } = "";
    public Visibility Visibility { get; set; } = Visibility.Public;
    public string? ReturnType { get; set; }
    public string? FolderPath { get; set; }
    public string RawDeclaration { get; set; } = "";
    public string Body { get; set; } = "";
    public List<TcVariable> LocalVars { get; set; } = new();
    public List<TcVariable> Parameters { get; set; } = new();

    /// <summary>Method-level pragmas/attributes, e.g. <c>{attribute 'call_after_init'}</c>.</summary>
    public List<string> Attributes { get; set; } = new();
}
