using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Discovers TwinCAT source files within a project directory
/// and creates the initial <see cref="TcProject"/> container.
/// </summary>
public static class TcProjectParser
{
    /// <summary>
    /// Create a new <see cref="TcProject"/> for the given path.
    /// Attempts to extract the project name from a .plcproj file if present.
    /// </summary>
    public static TcProject Parse(string projectPath, ILogger logger)
    {
        var project = new TcProject
        {
            ProjectPath = projectPath,
            AnalysisDate = DateTime.UtcNow
        };

        // Try to find a .plcproj file to get the project name
        project.Name = ResolveProjectName(projectPath, logger);

        var pouCount = FindTcPouFiles(projectPath).Count;
        var dutCount = FindTcDutFiles(projectPath).Count;
        var gvlCount = FindTcGvlFiles(projectPath).Count;

        logger.LogInformation(
            "Project '{Name}' discovered: {Pou} POUs, {Dut} DUTs, {Gvl} GVLs",
            project.Name, pouCount, dutCount, gvlCount);

        return project;
    }

    /// <summary>
    /// Recursively find all .TcPOU files under the base path.
    /// </summary>
    public static List<string> FindTcPouFiles(string basePath)
    {
        return FindFilesByExtension(basePath, "*.TcPOU");
    }

    /// <summary>
    /// Recursively find all .TcDUT files under the base path.
    /// </summary>
    public static List<string> FindTcDutFiles(string basePath)
    {
        return FindFilesByExtension(basePath, "*.TcDUT");
    }

    /// <summary>
    /// Recursively find all .TcGVL files under the base path.
    /// </summary>
    public static List<string> FindTcGvlFiles(string basePath)
    {
        return FindFilesByExtension(basePath, "*.TcGVL");
    }

    /// <summary>
    /// Attempt to resolve the project name from a .plcproj file.
    /// Falls back to the directory name.
    /// </summary>
    private static string ResolveProjectName(string projectPath, ILogger logger)
    {
        try
        {
            var plcProjFiles = Directory.GetFiles(projectPath, "*.plcproj", SearchOption.AllDirectories);

            if (plcProjFiles.Length > 0)
            {
                var plcProjPath = plcProjFiles[0];

                // Try to parse the project name from XML
                try
                {
                    var doc = XDocument.Load(plcProjPath);
                    XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";

                    // Try <PropertyGroup><RootNamespace> or <PropertyGroup><AssemblyName>
                    var rootNs = doc.Descendants(ns + "RootNamespace").FirstOrDefault()?.Value;
                    if (!string.IsNullOrWhiteSpace(rootNs))
                        return rootNs;

                    var assemblyName = doc.Descendants(ns + "AssemblyName").FirstOrDefault()?.Value;
                    if (!string.IsNullOrWhiteSpace(assemblyName))
                        return assemblyName;
                }
                catch
                {
                    // XML parse failed, use filename
                }

                // Fall back to the .plcproj filename
                return Path.GetFileNameWithoutExtension(plcProjPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to discover .plcproj in {Path}", projectPath);
        }

        // Final fallback: directory name
        return new DirectoryInfo(projectPath).Name;
    }

    /// <summary>
    /// Generic recursive file finder with common exclusions.
    /// </summary>
    private static List<string> FindFilesByExtension(string basePath, string searchPattern)
    {
        var results = new List<string>();

        if (!Directory.Exists(basePath))
            return results;

        try
        {
            var files = Directory.GetFiles(basePath, searchPattern, SearchOption.AllDirectories);

            foreach (var file in files)
            {
                // Skip common non-source directories
                var relativePath = Path.GetRelativePath(basePath, file);
                if (IsExcludedPath(relativePath))
                    continue;

                results.Add(file);
            }
        }
        catch
        {
            // Silently handle access-denied or other IO exceptions
        }

        return results;
    }

    /// <summary>
    /// Exclude files in bin/obj/_Boot/_CompileInfo directories.
    /// </summary>
    private static bool IsExcludedPath(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (var part in parts)
        {
            if (part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("_Boot", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("_CompileInfo", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
