namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public class TcProperty
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "";
    public string? FolderPath { get; set; }
    public bool HasGetter { get; set; }
    public bool HasSetter { get; set; }
    public string? GetBody { get; set; }
    public string? SetBody { get; set; }
    public string RawDeclaration { get; set; } = "";

    /// <summary>Property-level pragmas/attributes, e.g. <c>{attribute 'monitoring'}</c>.</summary>
    public List<string> Attributes { get; set; } = new();
}
