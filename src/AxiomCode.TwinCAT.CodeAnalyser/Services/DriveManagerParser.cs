using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Discovers and parses Drive Manager artifacts: .tcdmproj (project container)
/// and .tcdmdrv (per-drive configuration with parameter tree).
/// </summary>
public static class DriveManagerParser
{
    private static readonly HashSet<string> SkippedChildTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "SortId", "DriveModuleType", "OrderCode", "Name", "FirmwareVersion",
        "BootloaderVersion", "CatalogNo", "HardwareVersion", "SerialNr",
        "ProductRevision", "HasDualUseInfo", "OperatingFrequencyLimitation",
    };

    public static List<DriveManagerArtifact> Discover(string projectRoot)
    {
        var results = new List<DriveManagerArtifact>();
        if (!Directory.Exists(projectRoot)) return results;

        foreach (var file in XmlArtifactScanner.EnumerateAllFiles(projectRoot))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var category = ext switch
            {
                ".tcdmproj" => DriveManagerArtifactCategory.DriveManagerProject,
                ".tcdmdrv" => DriveManagerArtifactCategory.DriveManagerDrive,
                _ => DriveManagerArtifactCategory.Unknown,
            };
            if (category == DriveManagerArtifactCategory.Unknown) continue;

            results.Add(Parse(projectRoot, file, category));
        }

        return results;
    }

    private static DriveManagerArtifact Parse(string projectRoot, string filePath, DriveManagerArtifactCategory category)
    {
        var rel = XmlArtifactScanner.RelativeOrRaw(filePath, projectRoot);
        var suffix = Path.GetExtension(filePath);

        XDocument? doc;
        try { doc = XDocument.Load(filePath); }
        catch (Exception ex)
        {
            return new DriveManagerArtifact
            {
                RelativePath = rel,
                Suffix = suffix,
                Category = category,
                ArtifactName = Path.GetFileNameWithoutExtension(filePath),
                ExtractionStatus = ArtifactExtractionStatus.ParseFailed,
                StatusDetail = ex.Message,
            };
        }

        return category == DriveManagerArtifactCategory.DriveManagerProject
            ? ParseTcdmproj(doc, rel, suffix, category, filePath)
            : ParseTcdmdrv(doc, rel, suffix, category, filePath);
    }

    private static DriveManagerArtifact ParseTcdmproj(XDocument doc, string rel, string suffix,
        DriveManagerArtifactCategory category, string filePath)
    {
        var root = doc.Root!;
        var included = new List<string>();
        var dependents = new List<string>();
        var ioItemPaths = new List<string>();

        foreach (var content in XmlArtifactScanner.DescendantsByLocalName(root, "Content"))
        {
            var include = XmlArtifactScanner.NormalizePath(content.Attribute("Include")?.Value ?? "");
            var dependent = XmlArtifactScanner.NormalizePath(
                XmlArtifactScanner.FirstDescendantText(content, "DependentUpon") ?? "");
            var ioItem = (XmlArtifactScanner.FirstDescendantText(content, "IoItemPathName") ?? "").Trim();

            if (include.Length > 0) included.Add(include);
            if (dependent.Length > 0) dependents.Add(dependent);
            if (ioItem.Length > 0) ioItemPaths.Add(ioItem);
        }

        return new DriveManagerArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = XmlArtifactScanner.FirstDescendantText(root, "Name")
                          ?? Path.GetFileNameWithoutExtension(filePath),
            MetadataItems = new List<string>
            {
                $"schema-version={XmlArtifactScanner.FirstDescendantText(root, "SchemaVersion") ?? "unknown"}",
                $"drive-entry-count={included.Count}",
                $"dependent-drive-count={dependents.Count}",
                $"io-item-path-count={ioItemPaths.Count}",
            },
            RelatedPaths = XmlArtifactScanner.DistinctPreserveOrder(included),
            StatusDetail = "Drive Manager project metadata and referenced drive-file entries extracted.",
        };
    }

    private static DriveManagerArtifact ParseTcdmdrv(XDocument doc, string rel, string suffix,
        DriveManagerArtifactCategory category, string filePath)
    {
        var root = doc.Root!;
        var treeItemName = XmlArtifactScanner.FirstDescendantText(root, "ItemName")
                          ?? Path.GetFileNameWithoutExtension(filePath);

        var tcDriveNode = XmlArtifactScanner.DescendantsByLocalName(root, "TcDMDrive").FirstOrDefault();
        var tcLinkNode = XmlArtifactScanner.DescendantsByLocalName(root, "TcLink").FirstOrDefault();

        var tcProjectPath = tcLinkNode == null
            ? ""
            : (XmlArtifactScanner.FirstDescendantText(tcLinkNode, "TcPrjPath") ?? "").Trim();
        var ioItemPath = tcLinkNode == null
            ? ""
            : (XmlArtifactScanner.FirstDescendantText(tcLinkNode, "IoItemPathName") ?? "").Trim();

        var relatedPaths = new List<string>();
        if (!string.IsNullOrEmpty(tcProjectPath))
            relatedPaths.Add(XmlArtifactScanner.NormalizePath(tcProjectPath));

        var sections = tcDriveNode == null
            ? new List<DriveManagerSection>()
            : BuildSections(tcDriveNode, "");

        return new DriveManagerArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = treeItemName,
            MetadataItems = new List<string>
            {
                $"item-id={XmlArtifactScanner.FirstDescendantText(root, "ItemId") ?? "unknown"}",
                $"item-subtype={XmlArtifactScanner.FirstDescendantText(root, "ItemSubTypeName") ?? "unknown"}",
                $"child-count={XmlArtifactScanner.FirstDescendantText(root, "ChildCount") ?? "unknown"}",
                $"drive-module-type={(tcDriveNode == null ? "unknown" : XmlArtifactScanner.FirstDescendantText(tcDriveNode, "DriveModuleType") ?? "unknown")}",
                $"order-code={(tcDriveNode == null ? "unknown" : XmlArtifactScanner.FirstDescendantText(tcDriveNode, "OrderCode") ?? "unknown")}",
                $"io-item-path={(string.IsNullOrEmpty(ioItemPath) ? "unknown" : ioItemPath)}",
            },
            RelatedPaths = XmlArtifactScanner.DistinctPreserveOrder(relatedPaths),
            LinkedTcProjectPath = string.IsNullOrEmpty(tcProjectPath) ? "" : XmlArtifactScanner.NormalizePath(tcProjectPath),
            IoItemPath = ioItemPath,
            Sections = sections,
            StatusDetail = "Drive Manager tree-item metadata and TwinCAT linkage fields extracted.",
        };
    }

    private static List<DriveManagerSection> BuildSections(XElement parent, string parentPath)
    {
        var result = new List<DriveManagerSection>();
        foreach (var child in parent.Elements())
        {
            var section = BuildSection(child, parentPath);
            if (section != null) result.Add(section);
        }
        return result;
    }

    private static DriveManagerSection? BuildSection(XElement node, string parentPath)
    {
        var localName = node.Name.LocalName;
        if (SkippedChildTags.Contains(localName)) return null;

        var sectionName = ResolveSectionName(node);
        var sectionPath = string.IsNullOrEmpty(parentPath) ? sectionName : $"{parentPath}/{sectionName}";

        var attributes = node.Attributes()
            .Where(a => !string.IsNullOrWhiteSpace(a.Value))
            .OrderBy(a => a.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .Select(a => $"{a.Name.LocalName}={a.Value}")
            .ToList();

        var parameters = new List<DriveManagerParameter>();
        var childSections = new List<DriveManagerSection>();

        foreach (var child in node.Elements())
        {
            var childLocal = child.Name.LocalName;
            var parameter = TryBuildParameter(child);
            if (parameter != null)
            {
                parameters.Add(parameter);
                continue;
            }

            if (childLocal.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;

            var childSection = BuildSection(child, sectionPath);
            if (childSection != null) childSections.Add(childSection);
        }

        var directText = (node.Nodes().OfType<XText>().FirstOrDefault()?.Value ?? "").Trim();
        if (directText.Length > 0 && childSections.Count == 0 && parameters.Count == 0)
        {
            parameters.Add(new DriveManagerParameter
            {
                Name = localName,
                Value = directText,
            });
        }

        if (attributes.Count == 0 && parameters.Count == 0 && childSections.Count == 0)
            return null;

        return new DriveManagerSection
        {
            Name = sectionName,
            Path = sectionPath,
            Attributes = attributes,
            Parameters = parameters,
            ChildSections = childSections,
        };
    }

    private static DriveManagerParameter? TryBuildParameter(XElement node)
    {
        var localName = node.Name.LocalName;
        if (localName.Equals("Parameter", StringComparison.OrdinalIgnoreCase))
        {
            var propName = (XmlArtifactScanner.FirstDescendantText(node, "PropertyName") ?? "").Trim();
            var propValue = (XmlArtifactScanner.FirstDescendantText(node, "Value") ?? "").Trim();
            if (string.IsNullOrEmpty(propName)) return null;

            return new DriveManagerParameter
            {
                Name = propName,
                Value = string.IsNullOrEmpty(propValue) ? "-" : propValue,
            };
        }

        if (node.HasElements) return null;

        var text = (node.Value ?? "").Trim();
        if (text.Length == 0) return null;

        return new DriveManagerParameter { Name = localName, Value = text };
    }

    private static string ResolveSectionName(XElement node)
    {
        var localName = node.Name.LocalName;

        if (localName.Equals("Channel", StringComparison.OrdinalIgnoreCase))
        {
            var chnId = (node.Attribute("ChnId")?.Value ?? "").Trim();
            if (chnId.Length > 0) return $"Channel {chnId}";
        }

        if (localName.Equals("Block", StringComparison.OrdinalIgnoreCase))
        {
            var id = (node.Attribute("Id")?.Value ?? "").Trim();
            if (id.Length > 0) return $"Block {id}";
        }

        var nodeName = (XmlArtifactScanner.FirstDescendantText(node, "Name") ?? "").Trim();
        if (nodeName.Length > 0 && !nodeName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            return $"{localName}: {nodeName}";

        return localName;
    }
}
