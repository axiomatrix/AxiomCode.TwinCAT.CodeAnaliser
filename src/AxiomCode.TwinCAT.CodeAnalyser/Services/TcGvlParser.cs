using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Parses TwinCAT .TcGVL files into <see cref="TcGvl"/> model objects.
/// Delegates variable parsing to <see cref="TcPouParser.ParseVarBlocks"/>
/// to ensure consistent handling across all source file types.
/// </summary>
public static class TcGvlParser
{
    /// <summary>
    /// Parse a .TcGVL file and return a <see cref="TcGvl"/> model.
    /// </summary>
    /// <param name="filePath">Absolute path to the .TcGVL file.</param>
    /// <param name="projectBasePath">Project root for relative path computation.</param>
    public static TcGvl? Parse(string filePath, string projectBasePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var doc = XDocument.Load(filePath);
            var gvlElement = doc.Root?.Element("GVL");
            if (gvlElement == null)
                return null;

            var name = gvlElement.Attribute("Name")?.Value
                       ?? Path.GetFileNameWithoutExtension(filePath);

            var declElement = gvlElement.Element("Declaration");
            var rawDeclaration = TcPouParser.ExtractCdata(declElement);

            var gvl = new TcGvl
            {
                Name = name,
                FilePath = GetRelativePath(filePath, projectBasePath),
                QualifiedOnly = rawDeclaration.Contains(
                    "{attribute 'qualified_only'}",
                    StringComparison.OrdinalIgnoreCase),
                RawDeclaration = rawDeclaration
            };

            // Reuse TcPouParser's variable block parser for consistent handling
            // of REFERENCE TO, POINTER TO, ARRAY, AT bindings, constructor args, etc.
            var variables = TcPouParser.ParseVarBlocks(rawDeclaration);

            // Override scope to Global for all GVL variables
            foreach (var v in variables)
            {
                v.Scope = VarScope.Global;
            }

            gvl.Variables = variables;

            return gvl;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Compute a project-relative path for display.</summary>
    private static string GetRelativePath(string filePath, string basePath)
    {
        try
        {
            return Path.GetRelativePath(basePath, filePath).Replace('\\', '/');
        }
        catch
        {
            return filePath;
        }
    }
}
