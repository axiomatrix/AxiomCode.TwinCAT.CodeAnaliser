using System.Xml.Linq;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Shared helpers used by the Safety / Drive Manager / Scope artifact parsers.
/// All XML lookups are local-name based so we don't care about the artifact's XML namespace.
/// </summary>
internal static class XmlArtifactScanner
{
    public static IEnumerable<string> EnumerateAllFiles(string root)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(root, f);
            if (LibraryDependencyParser.IsExcludedPath(rel)) continue;
            yield return f;
        }
    }

    public static string RelativeOrRaw(string path, string root)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch { return path; }
    }

    public static string NormalizePath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw.Replace('\\', '/').Trim();
    }

    public static string? FirstDescendantText(XContainer container, string localName)
    {
        var el = container.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(el?.Value) ? null : el!.Value.Trim();
    }

    public static IEnumerable<XElement> DescendantsByLocalName(XContainer container, string localName) =>
        container.Descendants().Where(e =>
            e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<XElement> ChildrenByLocalName(XContainer container, string localName) =>
        container.Elements().Where(e =>
            e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    public static List<string> DistinctPreserveOrder(IEnumerable<string> input)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var s in input)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (seen.Add(s)) result.Add(s);
        }
        return result;
    }
}
