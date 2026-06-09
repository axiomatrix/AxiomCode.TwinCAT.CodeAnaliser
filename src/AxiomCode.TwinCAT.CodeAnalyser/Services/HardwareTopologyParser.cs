using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;
using Microsoft.Extensions.Logging;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Parses the real EtherCAT hardware topology from a TwinCAT project's <c>.xti</c>
/// box descriptions (and the <c>.tsproj</c> system project), then links each physical
/// channel to the PLC symbol it is mapped to via the IO <c>&lt;Link&gt;</c> elements.
/// Strictly source-driven: nothing is inferred — a channel only carries a PLC symbol
/// when the project explicitly links it.
/// </summary>
public static class HardwareTopologyParser
{
    private static readonly string[] SafetySkuPrefixes =
        { "EL1904", "EL2904", "EL6910", "EL1918", "EL2911", "EL6900", "EL1957" };

    /// <summary>
    /// Discover and parse every EtherCAT box in the project, with each channel annotated
    /// with the PLC symbol it is linked to. Returns an empty list (never throws) when the
    /// project contains no hardware description files.
    /// </summary>
    public static List<HardwareBox> Discover(string projectRoot, ILogger? logger = null)
    {
        var boxes = new List<HardwareBox>();
        if (!Directory.Exists(projectRoot)) return boxes;

        // The PLC project root passed in is often the XAE *project* folder, while the EtherCAT
        // hardware descriptions ("Hardware XTIs/") live in the *solution* folder one or two levels
        // up. Search from the project root first, then ascend (bounded) to the nearest ancestor that
        // actually contains .xti files — never fabricating, just widening the search.
        var hardwareRoot = ResolveHardwareRoot(projectRoot);

        var xtiFiles = XmlArtifactScanner.EnumerateAllFiles(hardwareRoot)
            .Where(f => f.EndsWith(".xti", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tsprojFiles = XmlArtifactScanner.EnumerateAllFiles(hardwareRoot)
            .Where(f => f.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (xtiFiles.Count == 0 && tsprojFiles.Count == 0) return boxes;

        // (boxName, channelName, signalName) -> (symbol, owner). Built from every <Link>. The triple is
        // exact: a link maps exactly one PDO entry, so we never broaden the match to a whole channel
        // (which would wrongly tag an analog channel's diagnostic bits with the value's variable).
        var linkMap = new Dictionary<string, (string Symbol, string Owner)>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: parse boxes from every .xti (a coupler .xti nests its child terminals).
        foreach (var file in xtiFiles)
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (Exception ex) { logger?.LogWarning("Hardware topology: failed to load {File}: {Msg}", file, ex.Message); continue; }

            var rel = XmlArtifactScanner.RelativeOrRaw(file, projectRoot);
            foreach (var boxEl in XmlArtifactScanner.DescendantsByLocalName(doc.Root!, "Box"))
            {
                var box = ParseBox(boxEl, file, rel);
                if (box is not null) boxes.Add(box);
            }
        }

        var knownBoxNames = new HashSet<string>(boxes.Select(b => b.BoxName), StringComparer.OrdinalIgnoreCase);

        // Pass 2: parse links from every .xti and .tsproj.
        foreach (var file in xtiFiles.Concat(tsprojFiles))
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch { continue; }

            // Box names defined inside THIS file (for the "__THIS__" / single-box link form).
            var fileBoxNames = XmlArtifactScanner.DescendantsByLocalName(doc.Root!, "Box")
                .Select(b => ResolveBoxName(b, file))
                .Where(n => n.Length > 0)
                .ToList();

            foreach (var linkEl in XmlArtifactScanner.DescendantsByLocalName(doc.Root!, "Link"))
            {
                var varA = linkEl.Attribute("VarA")?.Value ?? "";
                var varB = linkEl.Attribute("VarB")?.Value ?? "";
                if (varA.Length == 0 || varB.Length == 0) continue;

                if (!ResolveLinkChannel(varA, fileBoxNames, knownBoxNames, out var boxName, out var channel, out var signal))
                    continue;

                var owner = linkEl.Ancestors().FirstOrDefault(a =>
                    a.Name.LocalName.Equals("OwnerB", StringComparison.OrdinalIgnoreCase))?.Attribute("Name")?.Value ?? "";
                var symbol = ExtractSymbol(varB);
                if (symbol.Length == 0) continue;

                if (signal.Length > 0)
                    linkMap[Key(boxName, channel, signal)] = (symbol, owner);
            }
        }

        // Dedupe boxes that appear in both a coupler file and a standalone file: keep the
        // richest channel list, union the source files.
        var deduped = DedupeBoxes(boxes);

        // Annotate channels with their linked PLC symbol.
        foreach (var box in deduped)
        {
            foreach (var ch in box.Channels)
            {
                if (linkMap.TryGetValue(Key(box.BoxName, ch.ChannelName, ch.SignalName), out var hit))
                {
                    ch.LinkedPlcSymbol = hit.Symbol;
                    ch.LinkOwner = hit.Owner;
                }
            }
        }

        logger?.LogInformation("Hardware topology: {Boxes} boxes, {Channels} channels from {Files} .xti file(s)",
            deduped.Count, deduped.Sum(b => b.Channels.Count), xtiFiles.Count);
        return deduped;
    }

    /// <summary>Return the project root if it contains any .xti beneath it; otherwise the nearest
    /// ancestor (up to 3 levels) that does. Falls back to the project root when none is found.</summary>
    private static string ResolveHardwareRoot(string projectRoot)
    {
        bool HasXti(string dir)
        {
            try { return Directory.EnumerateFiles(dir, "*.xti", SearchOption.AllDirectories).Any(); }
            catch { return false; }
        }

        if (HasXti(projectRoot)) return projectRoot;

        var dir = new DirectoryInfo(projectRoot);
        for (int i = 0; i < 3 && dir.Parent is not null; i++)
        {
            dir = dir.Parent;
            if (HasXti(dir.FullName)) return dir.FullName;
        }
        return projectRoot;
    }

    // ── box parsing ──────────────────────────────────────────────────────────

    private static HardwareBox? ParseBox(XElement boxEl, string file, string rel)
    {
        var name = ResolveBoxName(boxEl, file);
        if (name.Length == 0) return null;

        // The box's own EtherCAT description is a direct child; nested child boxes have their own.
        var ecat = boxEl.Elements().FirstOrDefault(e =>
            e.Name.LocalName.Equals("EtherCAT", StringComparison.OrdinalIgnoreCase));

        var typeDesc = ecat?.Attribute("Type")?.Value ?? "";
        var desc = ecat?.Attribute("Desc")?.Value ?? "";
        var sku = desc.Length > 0 ? desc : LeadingSku(typeDesc);

        var box = new HardwareBox
        {
            BoxName = name,
            Sku = sku,
            TypeDescription = typeDesc,
            ProductCode = ecat?.Attribute("ProductCode")?.Value ?? "",
            RevisionNo = ecat?.Attribute("RevisionNo")?.Value ?? "",
            SourceFile = rel,
        };

        // Channels: PDO entries under the box's own EtherCAT element only.
        int order = 0;
        if (ecat is not null)
        {
            foreach (var pdo in XmlArtifactScanner.ChildrenByLocalName(ecat, "Pdo"))
            {
                var channelName = pdo.Attribute("Name")?.Value ?? "";
                var pdoIndex = pdo.Attribute("Index")?.Value ?? "";
                var pdoIsOutput = (pdo.Attribute("InOut")?.Value ?? "") == "1";

                foreach (var entry in XmlArtifactScanner.ChildrenByLocalName(pdo, "Entry"))
                {
                    var coeIndex = entry.Attribute("Index")?.Value ?? "";
                    var typeEl = entry.Elements().FirstOrDefault(e =>
                        e.Name.LocalName.Equals("Type", StringComparison.OrdinalIgnoreCase));
                    var dataType = (typeEl?.Value ?? "").Trim();

                    // Skip array-padding fillers (no real CoE index, just reserved bits).
                    if (coeIndex.Length == 0 && dataType.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var signal = entry.Attribute("Name")?.Value ?? "";
                    var ch = new HardwareChannel
                    {
                        ChannelName = channelName,
                        SignalName = signal,
                        DataType = dataType,
                        CoeIndex = coeIndex,
                        CoeSubIndex = entry.Attribute("Sub")?.Value ?? "",
                        PdoIndex = pdoIndex,
                        BitLength = BitLength(entry, dataType),
                        Direction = DirectionOf(coeIndex, signal, pdoIsOutput),
                        Order = order++,
                        IsPrimarySignal = IsPrimary(signal, coeIndex),
                    };
                    box.Channels.Add(ch);
                }
            }

            box.OrderCode = box.Channels.Count > 0 || !HasNestedBox(boxEl)
                ? (XmlArtifactScanner.FirstDescendantText(boxEl, "OrderCode") ?? "")
                : "";
        }

        Classify(box);
        return box;
    }

    private static string ResolveBoxName(XElement boxEl, string file)
    {
        var nameEl = boxEl.Elements().FirstOrDefault(e =>
            e.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase));
        var name = (nameEl?.Value ?? "").Trim();
        if (name.Length == 0 || name == "__FILENAME__")
            name = Path.GetFileNameWithoutExtension(file);
        return name;
    }

    private static bool HasNestedBox(XElement boxEl) =>
        boxEl.Elements().Any(e => e.Name.LocalName.Equals("Box", StringComparison.OrdinalIgnoreCase));

    private static void Classify(HardwareBox box)
    {
        var sku = box.Sku;
        bool hasFsoe = box.Channels.Any(c =>
            c.SignalName.Contains("FSOE", StringComparison.OrdinalIgnoreCase) ||
            c.ChannelName.Contains("FSOE", StringComparison.OrdinalIgnoreCase));

        box.IsCoupler = sku.StartsWith("EK", StringComparison.OrdinalIgnoreCase);
        box.IsEndTerminal = sku.StartsWith("EL9011", StringComparison.OrdinalIgnoreCase) ||
            (box.Channels.Count == 0 && sku.StartsWith("EL90", StringComparison.OrdinalIgnoreCase));
        box.IsSafety = hasFsoe ||
            SafetySkuPrefixes.Any(p => sku.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    // ── link resolution ──────────────────────────────────────────────────────

    /// <summary>Resolve a link's VarA ("Channel 1^Input" or "Box^Channel^Signal") to a
    /// (box, channel, signal) triple. Returns false when the box can't be resolved (never guesses).</summary>
    private static bool ResolveLinkChannel(string varA, List<string> fileBoxNames,
        HashSet<string> knownBoxNames, out string boxName, out string channel, out string signal)
    {
        boxName = channel = signal = "";
        var tokens = varA.Split('^');
        if (tokens.Length < 2) return false;

        // Form A: "Box (SKU)^Channel^Signal" — the box is the first segment.
        if (knownBoxNames.Contains(tokens[0]))
        {
            boxName = tokens[0];
            channel = tokens[1];
            signal = tokens.Length >= 3 ? string.Join("^", tokens.Skip(2)) : "";
            return true;
        }

        // Form B: "Channel^Signal" with an implicit "__THIS__" box — use the single box in this file.
        if (fileBoxNames.Count == 1)
        {
            boxName = fileBoxNames[0];
            channel = tokens[0];
            signal = string.Join("^", tokens.Skip(1));
            return true;
        }

        return false;
    }

    private static string ExtractSymbol(string varB)
    {
        var parts = varB.Split('^');
        if (parts.Length >= 2 && parts[0].StartsWith("PlcTask", StringComparison.OrdinalIgnoreCase))
            return string.Join(".", parts.Skip(1));
        return string.Join(".", parts);
    }

    // ── dedupe ─────────────────────────────────────────────────────────────────

    private static List<HardwareBox> DedupeBoxes(List<HardwareBox> boxes)
    {
        var best = new Dictionary<string, HardwareBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var box in boxes)
        {
            var key = $"{box.Sku}|{box.ProductCode}|{box.BoxName}";
            if (!best.TryGetValue(key, out var existing))
            {
                best[key] = box;
                continue;
            }
            // Prefer the instance with the richer channel list; union source files.
            var keep = box.Channels.Count > existing.Channels.Count ? box : existing;
            var other = ReferenceEquals(keep, box) ? existing : box;
            if (!keep.SourceFile.Contains(other.SourceFile, StringComparison.OrdinalIgnoreCase) && other.SourceFile.Length > 0)
                keep.SourceFile = $"{keep.SourceFile}; {other.SourceFile}";
            best[key] = keep;
        }
        return best.Values
            .OrderBy(b => b.BoxName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── small helpers ──────────────────────────────────────────────────────────

    private static string Key(string a, string b, string c) => $"{a}␟{b}␟{c}";
    private static string Key(string a, string b) => $"{a}␟{b}";

    private static string LeadingSku(string typeDesc)
    {
        var m = System.Text.RegularExpressions.Regex.Match(typeDesc, @"^([A-Za-z]{2}\d{3,4}(?:-\d+)?)");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static ChannelDirection DirectionOf(string coeIndex, string signal, bool pdoIsOutput)
    {
        // CoE convention: 0x6xxx = TxPDO (input to PLC), 0x7xxx = RxPDO (output from PLC).
        var hex = coeIndex.Replace("#x", "", StringComparison.OrdinalIgnoreCase).Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (hex.StartsWith("6", StringComparison.OrdinalIgnoreCase)) return ChannelDirection.Input;
        if (hex.StartsWith("7", StringComparison.OrdinalIgnoreCase)) return ChannelDirection.Output;
        if (signal.Contains("Output", StringComparison.OrdinalIgnoreCase)) return ChannelDirection.Output;
        if (signal.Contains("Input", StringComparison.OrdinalIgnoreCase)) return ChannelDirection.Input;
        return pdoIsOutput ? ChannelDirection.Output : ChannelDirection.Unknown;
    }

    private static bool IsPrimary(string signal, string coeIndex)
    {
        if (coeIndex.Length == 0) return false;
        if (signal.StartsWith("Status", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var noise in new[] { "Sync error", "TxPDO Toggle", "TxPDO State", "WcState", "InfoData", "Toggle" })
            if (signal.Contains(noise, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static int? BitLength(XElement entry, string dataType)
    {
        var bl = entry.Attribute("BitLen")?.Value;
        if (int.TryParse(bl, out var n)) return n;
        return dataType.ToUpperInvariant() switch
        {
            "BIT" or "BOOL" => 1,
            "BIT2" => 2,
            "BYTE" or "USINT" or "SINT" => 8,
            "WORD" or "UINT" or "INT" => 16,
            "DWORD" or "UDINT" or "DINT" => 32,
            "LWORD" or "ULINT" or "LINT" => 64,
            _ => null,
        };
    }
}
