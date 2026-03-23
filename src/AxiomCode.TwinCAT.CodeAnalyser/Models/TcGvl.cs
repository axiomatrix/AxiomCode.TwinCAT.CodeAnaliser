namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public class TcGvl
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool QualifiedOnly { get; set; }
    public List<TcVariable> Variables { get; set; } = new();
    public string RawDeclaration { get; set; } = "";
}
