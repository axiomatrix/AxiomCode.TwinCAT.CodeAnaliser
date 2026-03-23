using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using AxiomCode.TwinCAT.CodeAnalyser.Models;
using AxiomCode.TwinCAT.CodeAnalyser.Services;

namespace AxiomCode.TwinCAT.CodeAnalyser.Tools;

[McpServerToolType]
public class AnalyzerTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // ──────────────────────────────────────────────────────
    // Tool 1: twincat_analyze
    // ──────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_analyze"), Description(
        "Analyze a TwinCAT 3 PLC project. Returns summary statistics including " +
        "POU/DUT/GVL counts, alarm totals by severity, state machine count, IO point count, " +
        "and unresolved types. Point at the directory containing .TcPOU/.TcGVL files.")]
    public static string Analyze(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory (containing .TcPOU, .TcDUT, .TcGVL files)")]
        string project_path)
    {
        var project = analyzer.AnalyzeProject(project_path);
        return JsonSerializer.Serialize(new
        {
            project.Name,
            project.ProjectPath,
            AnalysisDate = project.AnalysisDate.ToString("o"),
            project.Summary,
            UnresolvedTypes = project.UnresolvedTypes.OrderBy(t => t).ToList(),
            RootObjects = project.ObjectTree.Select(n => new { n.InstanceName, n.TypeName, n.Layer, AlarmCount = CountAlarms(n) })
        }, JsonOpts);
    }

    // ──────────────────────────────────────────────────────
    // Tool 2: twincat_generate_html
    // ──────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_generate_html"), Description(
        "Generate an interactive HTML viewer for a TwinCAT 3 PLC project. " +
        "The output is a self-contained HTML file with collapsible object tree, " +
        "alarm analysis, state machine diagrams with code drill-down, IO mappings, " +
        "search, filtering, and documentation.")]
    public static string GenerateHtml(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path,
        [Description("Path where the HTML file should be saved (e.g. 'C:/output/project_analysis.html')")]
        string output_path)
    {
        var project = analyzer.AnalyzeProject(project_path);
        HtmlGenerator.Generate(project, output_path);
        return $"HTML viewer generated successfully.\n" +
               $"Output: {output_path}\n" +
               $"Project: {project.Name}\n" +
               $"POUs: {project.Summary.PouCount}, Alarms: {project.Summary.TotalAlarms}, " +
               $"State Machines: {project.Summary.StateMachineCount}, IO Points: {project.Summary.IoPointCount}";
    }

    // ──────────────────────────────────────────────────────
    // Tool 3: twincat_alarm_list
    // ──────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_alarm_list"), Description(
        "Extract all alarms from a TwinCAT 3 project as a flat JSON list. " +
        "Each alarm includes severity, trigger condition, delay, module path, " +
        "variable path, and reason if severity is unresolved.")]
    public static string AlarmList(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        var project = analyzer.AnalyzeProject(project_path);
        return JsonSerializer.Serialize(project.AllAlarms, JsonOpts);
    }

    // ──────────────────────────────────────────────────────
    // Tool 4: twincat_state_machines
    // ──────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_state_machines"), Description(
        "Extract state machines from a TwinCAT 3 project. Returns states, " +
        "transitions with conditions and timeouts, and code bodies per state. " +
        "Optionally filter by module name.")]
    public static string StateMachines(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path,
        [Description("Optional: filter by POU/module name (e.g. 'CM_Sealer')")]
        string? module_name = null)
    {
        var project = analyzer.AnalyzeProject(project_path);
        var machines = project.AllStateMachines.AsEnumerable();

        if (!string.IsNullOrEmpty(module_name))
            machines = machines.Where(sm =>
                sm.OwnerPou.Contains(module_name, StringComparison.OrdinalIgnoreCase));

        return JsonSerializer.Serialize(machines.ToList(), JsonOpts);
    }

    // ──────────────────────────────────────────────────────
    // Tool 5: twincat_module_info
    // ──────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_module_info"), Description(
        "Get detailed information about a specific TwinCAT module/POU. " +
        "Returns variables, methods, properties, inheritance chain, " +
        "alarms, state machines, and IO mappings.")]
    public static string ModuleInfo(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path,
        [Description("Module/POU name (e.g. 'CM_Sealer', 'EM_Fill', 'UM_Machine')")]
        string module_name)
    {
        var project = analyzer.AnalyzeProject(project_path);

        if (!project.POUs.TryGetValue(module_name, out var pou))
            return $"Module '{module_name}' not found. Available: {string.Join(", ", project.POUs.Keys.OrderBy(k => k))}";

        var alarms = project.AllAlarms.Where(a =>
            a.ModuleType.Equals(module_name, StringComparison.OrdinalIgnoreCase)).ToList();

        var stateMachines = project.AllStateMachines.Where(sm =>
            sm.OwnerPou.Equals(module_name, StringComparison.OrdinalIgnoreCase)).ToList();

        var ioMappings = project.AllIoMappings.Where(io =>
            (io.SourcePou ?? "").Equals(module_name, StringComparison.OrdinalIgnoreCase)).ToList();

        return JsonSerializer.Serialize(new
        {
            pou.Name,
            pou.PouType,
            pou.IsAbstract,
            pou.ExtendsType,
            pou.InheritanceChain,
            pou.ImplementsList,
            Variables = pou.Variables.Select(v => new { v.Name, v.DataType, v.Scope, v.IsReference, v.IsArray, v.AtBinding, v.ConstructorArgs }),
            Methods = pou.Methods.Select(m => new { m.Name, m.Visibility, m.ReturnType, m.FolderPath }),
            Properties = pou.Properties.Select(p => new { p.Name, p.DataType, p.HasGetter, p.HasSetter, p.FolderPath }),
            Alarms = alarms,
            StateMachines = stateMachines,
            IoMappings = ioMappings
        }, JsonOpts);
    }

    // ──────────────────────────────────────────────────────
    // Tool 6: twincat_io_map
    // ──────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_io_map"), Description(
        "Extract all IO mappings (AT bindings) from a TwinCAT 3 project. " +
        "Returns variable name, data type, AT address, direction (Input/Output/Memory), " +
        "and source GVL or POU.")]
    public static string IoMap(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        var project = analyzer.AnalyzeProject(project_path);
        return JsonSerializer.Serialize(project.AllIoMappings, JsonOpts);
    }

    // ──────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────

    private static int CountAlarms(ObjectTreeNode node)
    {
        return node.Alarms.Count + node.Children.Sum(c => CountAlarms(c));
    }
}
