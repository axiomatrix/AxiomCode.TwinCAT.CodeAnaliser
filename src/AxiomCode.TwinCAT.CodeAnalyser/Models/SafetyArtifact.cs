namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public enum SafetyArtifactCategory
{
    Unknown,
    SafetyProject,        // .splcproj
    SafetyLogic,          // .sal
    SafetyDiagram,        // .sal.diagram
    SafetyAliasDevice,    // .sds
    SafetyConfiguration,  // safety XML (target system config, etc.)
}

public enum ArtifactExtractionStatus
{
    Parsed,
    ParseFailed,
    Unsupported,
}

public class SafetyAliasDeviceIo
{
    public string IoName { get; set; } = "";
    public string DataType { get; set; } = "";
    public string BitSize { get; set; } = "";
    public string BitOffsetMessage { get; set; } = "";
}

public class SafetyArtifact
{
    public string RelativePath { get; set; } = "";
    public string Suffix { get; set; } = "";
    public SafetyArtifactCategory Category { get; set; }
    public string ArtifactName { get; set; } = "";

    public ArtifactExtractionStatus ExtractionStatus { get; set; } = ArtifactExtractionStatus.Parsed;
    public string StatusDetail { get; set; } = "";

    public List<string> MetadataItems { get; set; } = new();
    public List<string> RelatedPaths { get; set; } = new();

    // Aggregated counts — populated when parsed from .sal
    public int NetworkCount { get; set; }
    public int FunctionBlockCount { get; set; }
    public int InPortCount { get; set; }
    public int OutPortCount { get; set; }

    // Alias-device (.sds) specifics
    public string AliasDeviceId { get; set; } = "";
    public List<SafetyAliasDeviceIo> AliasDeviceIos { get; set; } = new();
}
