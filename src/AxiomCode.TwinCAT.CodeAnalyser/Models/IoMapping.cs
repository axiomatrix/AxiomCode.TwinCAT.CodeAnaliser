namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public enum IoDirection
{
    Input,
    Output,
    Memory,
    Unknown
}

public class IoMapping
{
    public string VariableName { get; set; } = "";
    public string DataType { get; set; } = "";
    public string AtBinding { get; set; } = "";
    public IoDirection Direction { get; set; } = IoDirection.Unknown;
    public string SourceGvl { get; set; } = "";
    public string? SourcePou { get; set; }
    public string? Comment { get; set; }
}
