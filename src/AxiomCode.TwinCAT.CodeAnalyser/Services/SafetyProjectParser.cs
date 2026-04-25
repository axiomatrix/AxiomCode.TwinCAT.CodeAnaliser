using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Discovers and parses TwinSAFE project artifacts:
/// .splcproj (safety project), .sal (safety logic), .sal.diagram (layout),
/// .sds (alias device), and generic safety XML (target system config, etc.).
/// </summary>
public static class SafetyProjectParser
{
    private static readonly XNamespace MsbuildNs =
        "http://schemas.microsoft.com/developer/msbuild/2003";

    public static List<SafetyArtifact> Discover(string projectRoot)
    {
        var results = new List<SafetyArtifact>();
        if (!Directory.Exists(projectRoot)) return results;

        foreach (var file in XmlArtifactScanner.EnumerateAllFiles(projectRoot))
        {
            var category = ClassifyByExtension(file);
            if (category == SafetyArtifactCategory.Unknown) continue;

            if (category == SafetyArtifactCategory.SafetyConfiguration && !LooksLikeSafetyXml(file))
                continue;

            results.Add(Parse(projectRoot, file, category));
        }

        return results;
    }

    private static SafetyArtifact Parse(string projectRoot, string filePath, SafetyArtifactCategory category)
    {
        var rel = XmlArtifactScanner.RelativeOrRaw(filePath, projectRoot);
        var suffix = GetLogicalSuffix(filePath);

        XDocument? doc;
        try { doc = XDocument.Load(filePath); }
        catch (Exception ex)
        {
            return new SafetyArtifact
            {
                RelativePath = rel,
                Suffix = suffix,
                Category = category,
                ArtifactName = Path.GetFileNameWithoutExtension(filePath),
                ExtractionStatus = ArtifactExtractionStatus.ParseFailed,
                StatusDetail = ex.Message,
            };
        }

        return category switch
        {
            SafetyArtifactCategory.SafetyProject => ParseSplcProj(doc, rel, suffix, category, filePath),
            SafetyArtifactCategory.SafetyLogic => ParseSal(doc, rel, suffix, category, filePath),
            SafetyArtifactCategory.SafetyDiagram => ParseSalDiagram(doc, rel, suffix, category, filePath),
            SafetyArtifactCategory.SafetyAliasDevice => ParseSds(doc, rel, suffix, category, filePath),
            SafetyArtifactCategory.SafetyConfiguration => ParseSafetyXml(doc, rel, suffix, category, filePath),
            _ => new SafetyArtifact
            {
                RelativePath = rel,
                Suffix = suffix,
                Category = category,
                ArtifactName = Path.GetFileNameWithoutExtension(filePath),
                ExtractionStatus = ArtifactExtractionStatus.Unsupported,
            }
        };
    }

    // ── .splcproj ─────────────────────────────────────────────────────────
    private static SafetyArtifact ParseSplcProj(XDocument doc, string rel, string suffix,
        SafetyArtifactCategory category, string filePath)
    {
        var included = doc.Descendants(MsbuildNs + "None")
            .Select(n => XmlArtifactScanner.NormalizePath(n.Attribute("Include")?.Value ?? ""))
            .Where(p => p.Length > 0)
            .ToList();

        var salCount = included.Count(p => p.EndsWith(".sal", StringComparison.OrdinalIgnoreCase));
        var sdsCount = included.Count(p => p.EndsWith(".sds", StringComparison.OrdinalIgnoreCase));
        var xmlCount = included.Count(p => p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        return new SafetyArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = XmlArtifactScanner.FirstDescendantText(doc, "IntProjName")
                          ?? Path.GetFileNameWithoutExtension(filePath),
            MetadataItems = new List<string>
            {
                $"schema-version={XmlArtifactScanner.FirstDescendantText(doc, "SchemaVersion") ?? "unknown"}",
                $"target-system={XmlArtifactScanner.FirstDescendantText(doc, "TargetSystem") ?? "unknown"}",
                $"programming-language={XmlArtifactScanner.FirstDescendantText(doc, "ProgrammingLanguage") ?? "unknown"}",
                $"logic-file-count={salCount}",
                $"alias-device-count={sdsCount}",
                $"configuration-file-count={xmlCount}",
            },
            RelatedPaths = XmlArtifactScanner.DistinctPreserveOrder(included),
            StatusDetail = "Safety project metadata and referenced logic, alias-device, and configuration files extracted.",
        };
    }

    // ── .sal ───────────────────────────────────────────────────────────────
    private static SafetyArtifact ParseSal(XDocument doc, string rel, string suffix,
        SafetyArtifactCategory category, string filePath)
    {
        var root = doc.Root!;
        var networks = XmlArtifactScanner.DescendantsByLocalName(root, "Network").Count();
        var fbContainers = XmlArtifactScanner.DescendantsByLocalName(root, "functionBlocks").Count();
        var inPorts = XmlArtifactScanner.DescendantsByLocalName(root, "inPort").Count();
        var outPorts = XmlArtifactScanner.DescendantsByLocalName(root, "outPort").Count();

        return new SafetyArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = (root.Attribute("name")?.Value ?? "").Trim() is { Length: > 0 } n
                ? n : Path.GetFileNameWithoutExtension(filePath),
            NetworkCount = networks,
            FunctionBlockCount = fbContainers,
            InPortCount = inPorts,
            OutPortCount = outPorts,
            MetadataItems = new List<string>
            {
                $"dsl-version={root.Attribute("dslVersion")?.Value ?? "unknown"}",
                $"network-count={networks}",
                $"function-block-container-count={fbContainers}",
                $"in-port-count={inPorts}",
                $"out-port-count={outPorts}",
            },
            StatusDetail = "Safety logic application metadata and network-level counts extracted.",
        };
    }

    // ── .sal.diagram ──────────────────────────────────────────────────────
    private static SafetyArtifact ParseSalDiagram(XDocument doc, string rel, string suffix,
        SafetyArtifactCategory category, string filePath)
    {
        var root = doc.Root!;
        var networkShapes = XmlArtifactScanner.DescendantsByLocalName(root, "networkSwimLane").Count();
        var portShapes = root.Descendants()
            .Count(e => e.Name.LocalName.EndsWith("portShape", StringComparison.OrdinalIgnoreCase));
        var fbShapes = root.Descendants().Count(e =>
        {
            var ln = e.Name.LocalName;
            return ln.EndsWith("Shape", StringComparison.OrdinalIgnoreCase)
                   && !ln.Contains("moniker", StringComparison.OrdinalIgnoreCase)
                   && !ln.EndsWith("portShape", StringComparison.OrdinalIgnoreCase);
        });

        var relatedLogicPath = "";
        if (filePath.EndsWith(".sal.diagram", StringComparison.OrdinalIgnoreCase))
        {
            var logicPath = filePath.Substring(0, filePath.Length - ".diagram".Length);
            relatedLogicPath = XmlArtifactScanner.RelativeOrRaw(logicPath, Path.GetDirectoryName(filePath) ?? "");
        }

        return new SafetyArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = (root.Attribute("name")?.Value ?? "").Trim() is { Length: > 0 } n
                ? n : Path.GetFileNameWithoutExtension(filePath),
            MetadataItems = new List<string>
            {
                $"dsl-version={root.Attribute("dslVersion")?.Value ?? "unknown"}",
                $"network-shape-count={networkShapes}",
                $"function-block-shape-count={fbShapes}",
                $"port-shape-count={portShapes}",
            },
            RelatedPaths = string.IsNullOrEmpty(relatedLogicPath) ? new() : new List<string> { relatedLogicPath },
            StatusDetail = "Safety diagram layout metadata and shape counts extracted.",
        };
    }

    // ── .sds ───────────────────────────────────────────────────────────────
    private static SafetyArtifact ParseSds(XDocument doc, string rel, string suffix,
        SafetyArtifactCategory category, string filePath)
    {
        var root = doc.Root!;
        var ioNodes = XmlArtifactScanner.DescendantsByLocalName(root, "IO").ToList();
        var ios = ioNodes.Select(io => new SafetyAliasDeviceIo
        {
            IoName = XmlArtifactScanner.FirstDescendantText(io, "Name") ?? "Unnamed IO",
            DataType = XmlArtifactScanner.FirstDescendantText(io, "DataType") ?? "",
            BitSize = XmlArtifactScanner.FirstDescendantText(io, "BitSize") ?? "",
            BitOffsetMessage = XmlArtifactScanner.FirstDescendantText(io, "BitOffsMessage") ?? "",
        }).ToList();

        return new SafetyArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = XmlArtifactScanner.FirstDescendantText(root, "Name")
                          ?? Path.GetFileNameWithoutExtension(filePath),
            AliasDeviceId = XmlArtifactScanner.FirstDescendantText(root, "SDSID") ?? "",
            AliasDeviceIos = ios,
            MetadataItems = new List<string>
            {
                $"file-format-version={root.Attribute("FileFormatVersion")?.Value ?? "unknown"}",
                $"alias-type={XmlArtifactScanner.FirstDescendantText(root, "Type") ?? "unknown"}",
                $"alias-subtype={XmlArtifactScanner.FirstDescendantText(root, "SubType") ?? "unknown"}",
                $"vendor-id={XmlArtifactScanner.FirstDescendantText(root, "VendorId") ?? "unknown"}",
                $"io-count={ioNodes.Count}",
            },
            StatusDetail = "Safety alias-device type metadata and IO count extracted.",
        };
    }

    // ── generic safety XML (TargetSystemConfig etc.) ──────────────────────
    private static SafetyArtifact ParseSafetyXml(XDocument doc, string rel, string suffix,
        SafetyArtifactCategory category, string filePath)
    {
        var root = doc.Root!;
        var rootName = root.Name.LocalName;

        if (rootName.Equals("TargetSystemConfig", StringComparison.OrdinalIgnoreCase))
        {
            return new SafetyArtifact
            {
                RelativePath = rel,
                Suffix = suffix,
                Category = category,
                ArtifactName = XmlArtifactScanner.FirstDescendantText(root, "TargetSystemObjectName")
                              ?? Path.GetFileNameWithoutExtension(filePath),
                MetadataItems = new List<string>
                {
                    $"target-system-type={XmlArtifactScanner.FirstDescendantText(root, "TargetSystemType") ?? "unknown"}",
                    $"target-system-subtype={XmlArtifactScanner.FirstDescendantText(root, "TargetSystemSubType") ?? "unknown"}",
                    $"software-version={XmlArtifactScanner.FirstDescendantText(root, "SoftwareVersion") ?? "unknown"}",
                    $"fsoe-address={XmlArtifactScanner.FirstDescendantText(root, "FSOEAddress") ?? "unknown"}",
                    $"ams-net-id={XmlArtifactScanner.FirstDescendantText(root, "AmsNetID") ?? "unknown"}",
                },
                StatusDetail = "Safety target-system configuration metadata extracted.",
            };
        }

        return new SafetyArtifact
        {
            RelativePath = rel,
            Suffix = suffix,
            Category = category,
            ArtifactName = Path.GetFileNameWithoutExtension(filePath),
            ExtractionStatus = ArtifactExtractionStatus.Unsupported,
            StatusDetail = $"Unsupported safety XML root tag: {rootName}",
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static SafetyArtifactCategory ClassifyByExtension(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".splcproj")) return SafetyArtifactCategory.SafetyProject;
        if (lower.EndsWith(".sal.diagram")) return SafetyArtifactCategory.SafetyDiagram;
        if (lower.EndsWith(".sal")) return SafetyArtifactCategory.SafetyLogic;
        if (lower.EndsWith(".sds")) return SafetyArtifactCategory.SafetyAliasDevice;
        if (lower.EndsWith(".xml"))
        {
            // Only treat as safety XML if it lives under a safety project directory
            var dir = Path.GetDirectoryName(lower) ?? "";
            if (dir.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
                dir.Contains("splc", StringComparison.OrdinalIgnoreCase) ||
                dir.Contains("twinsafe", StringComparison.OrdinalIgnoreCase))
                return SafetyArtifactCategory.SafetyConfiguration;
        }
        return SafetyArtifactCategory.Unknown;
    }

    private static bool LooksLikeSafetyXml(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = System.Xml.XmlReader.Create(stream,
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element)
                {
                    return reader.LocalName.Equals("TargetSystemConfig", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch { }
        return false;
    }

    private static string GetLogicalSuffix(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".sal.diagram")) return ".sal.diagram";
        return Path.GetExtension(path);
    }
}
