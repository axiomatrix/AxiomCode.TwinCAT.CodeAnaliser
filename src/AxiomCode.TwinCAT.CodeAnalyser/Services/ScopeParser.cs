using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Discovers and parses Scope View artifacts: .tcmproj (project container)
/// and .tcscopex (scope configuration with signal list).
/// </summary>
public static class ScopeParser
{
    public static List<ScopeArtifact> Discover(string projectRoot)
    {
        var results = new List<ScopeArtifact>();
        if (!Directory.Exists(projectRoot)) return results;

        foreach (var file in XmlArtifactScanner.EnumerateAllFiles(projectRoot))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var category = ext switch
            {
                ".tcmproj" => ScopeArtifactCategory.ScopeProject,
                ".tcscopex" => ScopeArtifactCategory.ScopeConfiguration,
                _ => ScopeArtifactCategory.Unknown,
            };
            if (category == ScopeArtifactCategory.Unknown) continue;

            results.Add(Parse(projectRoot, file, category));
        }

        return results;
    }

    private static ScopeArtifact Parse(string projectRoot, string filePath, ScopeArtifactCategory category)
    {
        var rel = XmlArtifactScanner.RelativeOrRaw(filePath, projectRoot);
        var suffix = Path.GetExtension(filePath);

        XDocument? doc;
        try { doc = XDocument.Load(filePath); }
        catch (Exception ex)
        {
            return new ScopeArtifact
            {
                RelativePath = rel,
                Suffix = suffix,
                Category = category,
                ArtifactName = Path.GetFileNameWithoutExtension(filePath),
                ExtractionStatus = ArtifactExtractionStatus.ParseFailed,
                StatusDetail = ex.Message,
            };
        }

        return category == ScopeArtifactCategory.ScopeProject
            ? ParseTcmproj(doc, rel, suffix, category, filePath)
            : ParseTcscopex(doc, rel, suffix, category, filePath);
    }

    private static ScopeArtifact ParseTcmproj(XDocument doc, string rel, string suffix,
        ScopeArtifactCategory category, string filePath)
    {
        var root = doc.Root!;
        var included = XmlArtifactScanner.DescendantsByLocalName(root, "Content")
            .Select(c => XmlArtifactScanner.NormalizePath(c.Attribute("Include")?.Value ?? ""))
            .Where(p => p.Length > 0)
            .ToList();

        return new ScopeArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = XmlArtifactScanner.FirstDescendantText(root, "Name")
                          ?? Path.GetFileNameWithoutExtension(filePath),
            MetadataItems = new List<string>
            {
                $"assembly-name={XmlArtifactScanner.FirstDescendantText(root, "AssemblyName") ?? "unknown"}",
                $"scope-entry-count={included.Count}",
            },
            RelatedPaths = XmlArtifactScanner.DistinctPreserveOrder(included),
            StatusDetail = "Scope project metadata and referenced scope-configuration files extracted.",
        };
    }

    private static ScopeArtifact ParseTcscopex(XDocument doc, string rel, string suffix,
        ScopeArtifactCategory category, string filePath)
    {
        var root = doc.Root!;
        var symbolCount = XmlArtifactScanner.DescendantsByLocalName(root, "Symbol").Count();
        var subMemberCount = XmlArtifactScanner.DescendantsByLocalName(root, "SubMember").Count();
        var transformationCount = XmlArtifactScanner.DescendantsByLocalName(root, "Transformation").Count();
        var enabledCount = XmlArtifactScanner.DescendantsByLocalName(root, "Enabled")
            .Count(e => (e.Value ?? "").Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

        var signalNames = XmlArtifactScanner.DistinctPreserveOrder(
            XmlArtifactScanner.DescendantsByLocalName(root, "AdsAcquisition")
                .Select(n => XmlArtifactScanner.FirstDescendantText(n, "Name") ?? "")
                .Where(n => !string.IsNullOrWhiteSpace(n)));

        var description = BuildDescription(signalNames, symbolCount, transformationCount, enabledCount);

        var title = (XmlArtifactScanner.FirstDescendantText(root, "Name") ?? "").Trim();
        if (string.IsNullOrEmpty(title))
            title = (XmlArtifactScanner.FirstDescendantText(root, "Title") ?? "").Trim();
        if (string.IsNullOrEmpty(title))
            title = Path.GetFileNameWithoutExtension(filePath);

        return new ScopeArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = title,
            SignalNames = signalNames,
            Description = description,
            MetadataItems = new List<string>
            {
                $"assembly-name={root.Attribute("AssemblyName")?.Value ?? "unknown"}",
                $"autosave-mode={XmlArtifactScanner.FirstDescendantText(root, "AutoSaveMode") ?? "unknown"}",
                $"autodelete-mode={XmlArtifactScanner.FirstDescendantText(root, "AutoDeleteMode") ?? "unknown"}",
                $"symbol-count={symbolCount}",
                $"submember-count={subMemberCount}",
                $"transformation-count={transformationCount}",
                $"enabled-flag-count={enabledCount}",
            },
            StatusDetail = "Scope configuration metadata and signal-structure counts extracted.",
        };
    }

    private static string BuildDescription(List<string> signalNames, int symbolCount,
        int transformationCount, int enabledCount)
    {
        var parts = new List<string>();
        if (signalNames.Count > 0)
            parts.Add($"{signalNames.Count} ADS acquisition signal(s)");
        if (symbolCount > 0)
            parts.Add($"{symbolCount} symbol(s)");
        if (transformationCount > 0)
            parts.Add($"{transformationCount} transformation(s)");
        if (enabledCount > 0)
            parts.Add($"{enabledCount} enabled flag(s)");
        return parts.Count == 0 ? "Empty scope configuration." : string.Join(", ", parts) + ".";
    }
}
