using System.Text.RegularExpressions;
using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Scans a TwinCAT project for .plcproj files and extracts library dependencies
/// from MSBuild PlaceholderReference / PlaceholderResolution elements.
/// </summary>
public static class LibraryDependencyParser
{
    private static readonly XNamespace MsbuildNs =
        "http://schemas.microsoft.com/developer/msbuild/2003";

    // "Tc2_MC2, 3.3.68.0 (Beckhoff Automation GmbH)" → version, vendor
    private static readonly Regex ResolutionRegex = new(
        @"^\s*(?<name>[^,]+?)\s*,\s*(?<ver>[\d.]+)\s*(?:\((?<vendor>[^)]+)\))?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Discover library dependencies across every .plcproj below the project root.
    /// Returns one record per unique (name, namespace, resolution) triple.
    /// </summary>
    public static List<LibraryDependency> Discover(string projectRoot)
    {
        var results = new Dictionary<string, LibraryDependency>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(projectRoot))
            return new List<LibraryDependency>();

        foreach (var plcProjPath in SafeEnumerateFiles(projectRoot, "*.plcproj"))
        {
            XDocument? doc;
            try { doc = XDocument.Load(plcProjPath); }
            catch { continue; }
            if (doc.Root == null) continue;

            var relativeSource = GetRelativePath(plcProjPath, projectRoot);

            foreach (var refEl in doc.Descendants(MsbuildNs + "PlaceholderReference"))
            {
                var name = (refEl.Attribute("Include")?.Value ?? "").Trim();
                if (string.IsNullOrEmpty(name)) continue;

                var ns = (refEl.Element(MsbuildNs + "DefaultNamespace")?.Value
                          ?? refEl.Element(MsbuildNs + "Namespace")?.Value
                          ?? "").Trim();
                var defaultRes = (refEl.Element(MsbuildNs + "DefaultResolution")?.Value ?? "").Trim();

                var key = $"{name}|{ns}|{defaultRes}";
                if (!results.TryGetValue(key, out var dep))
                {
                    dep = new LibraryDependency
                    {
                        LibraryName = name,
                        SourceRelativePath = relativeSource,
                        Namespace = ns,
                        DefaultResolution = defaultRes,
                        Vendor = ParseVendor(defaultRes),
                    };
                    results[key] = dep;
                }
            }

            foreach (var resEl in doc.Descendants(MsbuildNs + "PlaceholderResolution"))
            {
                var name = (resEl.Attribute("Include")?.Value ?? "").Trim();
                if (string.IsNullOrEmpty(name)) continue;

                var resolution = (resEl.Element(MsbuildNs + "Resolution")?.Value ?? "").Trim();
                if (string.IsNullOrEmpty(resolution)) continue;

                var (version, vendor) = ParseResolution(resolution);

                // Merge into any existing record for this library name regardless of namespace
                var existing = results.Values.FirstOrDefault(d =>
                    string.Equals(d.LibraryName, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.DefaultResolution = string.IsNullOrEmpty(existing.DefaultResolution)
                        ? resolution : existing.DefaultResolution;
                    if (!string.IsNullOrEmpty(version)) existing.ResolvedVersion = version;
                    if (!string.IsNullOrEmpty(vendor)) existing.Vendor = vendor;
                }
                else
                {
                    results[$"{name}||{resolution}"] = new LibraryDependency
                    {
                        LibraryName = name,
                        SourceRelativePath = relativeSource,
                        DefaultResolution = resolution,
                        ResolvedVersion = version,
                        Vendor = vendor,
                    };
                }
            }
        }

        return results.Values
            .OrderBy(d => d.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Namespace, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (string version, string vendor) ParseResolution(string resolution)
    {
        var m = ResolutionRegex.Match(resolution);
        if (!m.Success) return ("", "");
        return (m.Groups["ver"].Value.Trim(), m.Groups["vendor"].Value.Trim());
    }

    private static string ParseVendor(string defaultResolution)
    {
        if (string.IsNullOrEmpty(defaultResolution)) return "";
        var m = ResolutionRegex.Match(defaultResolution);
        return m.Success ? m.Groups["vendor"].Value.Trim() : "";
    }

    internal static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { yield break; }

        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(root, f);
            if (IsExcludedPath(rel)) continue;
            yield return f;
        }
    }

    internal static bool IsExcludedPath(string relativePath)
    {
        foreach (var part in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("_Boot", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("_CompileInfo", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".git", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static string GetRelativePath(string path, string root)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch { return path; }
    }
}
