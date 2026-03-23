namespace AxiomCode.TwinCAT.CodeAnaliser.Models;

public enum DutType
{
    Enum,
    Struct,
    Union,
    Alias
}

public class TcEnumValue
{
    public string Name { get; set; } = "";
    public string? Value { get; set; }
    public string? Comment { get; set; }
}

public class TcDut
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DutType DutType { get; set; } = DutType.Enum;
    public string? BaseType { get; set; }
    public List<string> Attributes { get; set; } = new();

    // For ENUMs
    public List<TcEnumValue> EnumValues { get; set; } = new();

    // For STRUCTs/UNIONs
    public List<TcVariable> Members { get; set; } = new();

    public string RawDeclaration { get; set; } = "";
}
