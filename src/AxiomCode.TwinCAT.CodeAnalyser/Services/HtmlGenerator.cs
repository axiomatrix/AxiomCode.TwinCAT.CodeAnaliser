using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Generates a self-contained interactive HTML viewer from project analysis data.
/// Loads the embedded viewer.html template and injects serialized JSON data.
/// </summary>
public static class HtmlGenerator
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Generate(TcProject project, string outputPath)
    {
        var template = LoadTemplate();

        var data = new
        {
            projectName = project.Name,
            analysisDate = project.AnalysisDate.ToString("o"),
            summary = project.Summary,
            tree = project.ObjectTree,
            alarms = project.AllAlarms,
            stateMachines = project.AllStateMachines,
            ioMappings = project.AllIoMappings,
            unresolvedTypes = project.UnresolvedTypes.OrderBy(t => t).ToList(),
            // Full project data for programmer interrogation
            pous = project.POUs.Values.Select(p => new
            {
                p.Name, p.PouType, p.IsAbstract, p.ExtendsType, p.ImplementsList,
                p.InheritanceChain,
                variables = p.Variables.Select(v => VarDto(v)),
                allVariables = p.AllVariables.Select(v => VarDto(v)),
                methods = p.Methods.Select(m => new
                {
                    m.Name, m.Visibility, m.ReturnType, m.FolderPath, m.Body,
                    localVars = m.LocalVars.Select(v => VarDto(v)),
                    parameters = m.Parameters.Select(v => VarDto(v))
                }),
                properties = p.Properties.Select(pr => new
                {
                    pr.Name, pr.DataType, pr.HasGetter, pr.HasSetter, pr.GetBody, pr.SetBody
                })
            }).OrderBy(p => p.Name).ToList(),
            duts = project.DUTs.Values.Select(d => new
            {
                d.Name, d.DutType, d.BaseType, d.Attributes,
                enumValues = d.EnumValues.Select(e => new { e.Name, e.Value, e.Comment }),
                members = d.Members.Select(v => VarDto(v))
            }).OrderBy(d => d.Name).ToList(),
            gvls = project.GVLs.Values.Select(g => new
            {
                g.Name, g.QualifiedOnly,
                variables = g.Variables.Select(v => VarDto(v))
            }).OrderBy(g => g.Name).ToList()
        };

        var json = JsonSerializer.Serialize(data, JsonOpts);

        // Inject JSON into template, replacing the entire placeholder including fallback braces
        var html = template.Replace("/*DATA_PLACEHOLDER*/{}", json);

        // Ensure output directory exists
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outputPath, html);
    }

    private static object VarDto(TcVariable v) => new
    {
        v.Name, v.DataType, v.Scope, v.InitialValue, v.Comment,
        v.IsReference, v.IsPointer, v.IsArray, v.ArrayBounds,
        v.AtBinding, v.ConstructorArgs
    };

    private static string LoadTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("viewer.html"));

        if (resourceName != null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        // Fallback: try to load from file next to the exe
        var exeDir = Path.GetDirectoryName(assembly.Location) ?? ".";
        var templatePath = Path.Combine(exeDir, "Templates", "viewer.html");
        if (File.Exists(templatePath))
            return File.ReadAllText(templatePath);

        throw new FileNotFoundException(
            "Could not find viewer.html template. Ensure it is embedded as a resource or in the Templates directory.");
    }
}
