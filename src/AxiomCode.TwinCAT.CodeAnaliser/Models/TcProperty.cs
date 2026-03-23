namespace AxiomCode.TwinCAT.CodeAnaliser.Models;

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
}
