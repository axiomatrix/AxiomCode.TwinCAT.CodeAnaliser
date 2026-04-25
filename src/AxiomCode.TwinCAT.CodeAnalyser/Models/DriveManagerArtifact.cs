namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public enum DriveManagerArtifactCategory
{
    Unknown,
    DriveManagerProject,  // .tcdmproj
    DriveManagerDrive,    // .tcdmdrv
}

public class DriveManagerParameter
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public class DriveManagerSection
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Attributes { get; set; } = new();
    public List<DriveManagerParameter> Parameters { get; set; } = new();
    public List<DriveManagerSection> ChildSections { get; set; } = new();
}

public class DriveManagerArtifact
{
    public string RelativePath { get; set; } = "";
    public string Suffix { get; set; } = "";
    public DriveManagerArtifactCategory Category { get; set; }
    public string ArtifactName { get; set; } = "";

    public ArtifactExtractionStatus ExtractionStatus { get; set; } = ArtifactExtractionStatus.Parsed;
    public string StatusDetail { get; set; } = "";

    public List<string> MetadataItems { get; set; } = new();
    public List<string> RelatedPaths { get; set; } = new();

    public string LinkedTcProjectPath { get; set; } = "";
    public string IoItemPath { get; set; } = "";

    public List<DriveManagerSection> Sections { get; set; } = new();
}
