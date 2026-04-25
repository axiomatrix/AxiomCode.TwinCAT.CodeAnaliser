namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

/// <summary>
/// A TwinCAT PLC library dependency extracted from a .plcproj file.
/// Combines PlaceholderReference (abstract requirement) with PlaceholderResolution
/// (concrete version lock) when both are present.
/// </summary>
public class LibraryDependency
{
    public string LibraryName { get; set; } = "";
    public string SourceRelativePath { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string DefaultResolution { get; set; } = "";
    public string ResolvedVersion { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string DependencyCategory { get; set; } = "plc-library";
}
