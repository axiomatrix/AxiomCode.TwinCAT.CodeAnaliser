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

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 1: twincat_analyze
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 2: twincat_generate_html
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 3: twincat_alarm_list
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 4: twincat_state_machines
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 5: twincat_module_info
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
            Variables = pou.Variables.Select(v => new { v.Name, v.DataType, v.Scope, v.IsReference, v.IsArray, v.AtBinding, v.ConstructorArgs, v.Comment, v.LeadingComment }),
            Methods = pou.Methods.Select(m => new { m.Name, m.Visibility, m.ReturnType, m.FolderPath }),
            Properties = pou.Properties.Select(p => new { p.Name, p.DataType, p.HasGetter, p.HasSetter, p.FolderPath }),
            Alarms = alarms,
            StateMachines = stateMachines,
            IoMappings = ioMappings
        }, JsonOpts);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 6: twincat_io_map
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Helpers
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static int CountAlarms(ObjectTreeNode node)
    {
        return node.Alarms.Count + node.Children.Sum(c => CountAlarms(c));
    }

    // ──────────────────────────────────────────────────────────────────
    // Tool 7: twincat_libraries
    // ──────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_libraries"), Description(
        "List PLC library dependencies extracted from .plcproj files. " +
        "Returns library name, namespace, default resolution, resolved version lock, " +
        "and vendor for every PlaceholderReference / PlaceholderResolution discovered.")]
    public static string Libraries(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        var project = analyzer.AnalyzeProject(project_path);
        return JsonSerializer.Serialize(project.LibraryDependencies, JsonOpts);
    }

    // ──────────────────────────────────────────────────────────────────
    // Tool 8: twincat_safety
    // ──────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_safety"), Description(
        "List TwinSAFE project artifacts discovered in the project. Covers " +
        ".splcproj (safety project), .sal (safety logic), .sal.diagram (layout), " +
        ".sds (alias device with IO list), and TargetSystemConfig safety XML. " +
        "Returns per-artifact metadata, related-file references, and for alias devices " +
        "the SDSID and IO entries.")]
    public static string Safety(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        var project = analyzer.AnalyzeProject(project_path);
        return JsonSerializer.Serialize(project.SafetyArtifacts, JsonOpts);
    }

    // ──────────────────────────────────────────────────────────────────
    // Tool 9: twincat_drives
    // ──────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_drives"), Description(
        "List Drive Manager artifacts discovered in the project. Covers .tcdmproj " +
        "(project container, referenced .tcdmdrv files) and .tcdmdrv (per-drive " +
        "configuration with recursive parameter tree, linked TwinCAT project path, " +
        "and IO item path name).")]
    public static string Drives(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        var project = analyzer.AnalyzeProject(project_path);
        return JsonSerializer.Serialize(project.DriveManagerArtifacts, JsonOpts);
    }

    // ──────────────────────────────────────────────────────────────────
    // Tool 10: twincat_scopes
    // ──────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_scopes"), Description(
        "List Scope View artifacts discovered in the project. Covers .tcmproj " +
        "(project container) and .tcscopex (scope configuration with ADS acquisition " +
        "signal list, symbol/transformation/enabled counts, and a human-readable " +
        "description of the configuration).")]
    public static string Scopes(
        AnalyzerService analyzer,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        var project = analyzer.AnalyzeProject(project_path);
        return JsonSerializer.Serialize(project.ScopeArtifacts, JsonOpts);
    }
}
