namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public enum ScopeArtifactCategory
{
    Unknown,
    ScopeProject,       // .tcmproj
    ScopeConfiguration, // .tcscopex
}

public class ScopeArtifact
{
    public string RelativePath { get; set; } = "";
    public string Suffix { get; set; } = "";
    public ScopeArtifactCategory Category { get; set; }
    public string ArtifactName { get; set; } = "";

    public ArtifactExtractionStatus ExtractionStatus { get; set; } = ArtifactExtractionStatus.Parsed;
    public string StatusDetail { get; set; } = "";

    public List<string> MetadataItems { get; set; } = new();
    public List<string> RelatedPaths { get; set; } = new();

    public List<string> SignalNames { get; set; } = new();
    public string Description { get; set; } = "";
}
