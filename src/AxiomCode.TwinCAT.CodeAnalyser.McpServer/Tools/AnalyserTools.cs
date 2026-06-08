using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using AxiomCode.TwinCAT.CodeAnalyser.Models;
using AxiomCode.TwinCAT.CodeAnalyser.Services;

namespace AxiomCode.TwinCAT.CodeAnalyser.Tools;

[McpServerToolType]
public class AnalyserTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Run a project-scoped tool body with structured error reporting.
    /// Wraps <see cref="AnalyserService.AnalyseProject"/> so misspelled or
    /// non-TwinCAT paths surface as JSON `{ "Error": ..., "Hint": ... }`
    /// rather than either (a) throwing an MCP framework-level exception
    /// that the LLM can't parse, or (b) silently succeeding with an empty
    /// 0/0/0 project that gets rendered as a valid result. Every tool that
    /// takes a project_path goes through this helper so the failure mode
    /// is uniform.
    /// </summary>
    private static string RunWithProject(
        AnalyserService analyser,
        string project_path,
        Func<TcProject, string> body)
    {
        try
        {
            // Read the canonical, fingerprinted source-of-truth model: parse once
            // and persist, so repeated tool calls (and the documentation generator,
            // which shares the same on-disk store) reuse one analysis instead of
            // re-parsing the whole tree. Cache miss falls through to AnalyseProject.
            var project = new ProjectKnowledgeStore(analyser).GetOrBuild(project_path).Project;
            return body(project);
        }
        catch (DirectoryNotFoundException ex)
        {
            return JsonSerializer.Serialize(new
            {
                Error = "DirectoryNotFound",
                ex.Message,
                ProjectPath = project_path,
                Hint = "Re-check separators in the path (underscore vs hyphen, " +
                       "dot vs dash). TwinCAT folder names often mix '_' and '-' " +
                       "asymmetrically — verify against the actual on-disk name " +
                       "before retrying."
            }, JsonOpts);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No TwinCAT source files", StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(new
            {
                Error = "NotATwinCatProject",
                ex.Message,
                ProjectPath = project_path,
                Hint = "Point at the folder containing the .plcproj file. " +
                       "TwinCAT projects nest as <Solution>/<Project>/<PlcProject>/<PlcProject>/, " +
                       "with the .TcPOU/.TcDUT/.TcGVL files in subfolders below."
            }, JsonOpts);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new
            {
                Error = "InvalidArgument",
                ex.Message,
                ProjectPath = project_path
            }, JsonOpts);
        }
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 1: twincat_analyse
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [McpServerTool(Name = "twincat_analyse"), Description(
        "Analyse a TwinCAT 3 PLC project. Returns summary statistics including " +
        "POU/DUT/GVL counts, alarm totals by severity, state machine count, IO point count, " +
        "and unresolved types. Point at the directory containing .TcPOU/.TcGVL files.")]
    public static string Analyse(
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory (containing .TcPOU, .TcDUT, .TcGVL files)")]
        string project_path)
    {
        return RunWithProject(analyser, project_path, project => JsonSerializer.Serialize(new
        {
            project.Name,
            project.ProjectPath,
            AnalysisDate = project.AnalysisDate.ToString("o"),
            project.Summary,
            UnresolvedTypes = project.UnresolvedTypes.OrderBy(t => t).ToList(),
            RootObjects = project.ObjectTree.Select(n => new { n.InstanceName, n.TypeName, n.Layer, AlarmCount = CountAlarms(n) })
        }, JsonOpts));
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
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path,
        [Description("Path where the HTML file should be saved (e.g. 'C:/output/project_analysis.html')")]
        string output_path)
    {
        return RunWithProject(analyser, project_path, project =>
        {
            HtmlGenerator.Generate(project, output_path);
            return $"HTML viewer generated successfully.\n" +
                   $"Output: {output_path}\n" +
                   $"Project: {project.Name}\n" +
                   $"POUs: {project.Summary.PouCount}, Alarms: {project.Summary.TotalAlarms}, " +
                   $"State Machines: {project.Summary.StateMachineCount}, IO Points: {project.Summary.IoPointCount}";
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tool: twincat_fbd_diagram — graphical (FBD/LD/IL) POU logic as Mermaid
    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "twincat_fbd_diagram"), Description(
        "Render the LOGIC of a graphical (FBD / LD / IL) POU as a Mermaid flowchart — " +
        "a data-flow graph where function-block/operator boxes and operands (variables) " +
        "are wired by their inputs, outputs and formal parameters, one subgraph per network. " +
        "Call WITHOUT 'pou' to list the project's graphical POUs; call WITH 'pou' to get its " +
        "diagram. The returned 'mermaid' field is ready to drop into a ```mermaid block.")]
    public static string FbdDiagram(
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path,
        [Description("Optional POU name to render; omit to list all graphical POUs in the project")]
        string? pou = null)
    {
        return RunWithProject(analyser, project_path, project =>
        {
            var graphical = project.POUs.Values
                .Where(p => p.Graphical != null && p.Graphical.Networks.Count > 0)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(pou))
            {
                return JsonSerializer.Serialize(new
                {
                    GraphicalPous = graphical.Select(p => new
                    {
                        p.Name,
                        Language = p.Language.ToString(),
                        Networks = p.Graphical!.Networks.Count,
                        Boxes    = p.Graphical!.ReferencedBoxTypes,
                    }),
                    Count = graphical.Count,
                    Hint  = "Call again with pou=<Name> to get that POU's Mermaid logic diagram.",
                }, JsonOpts);
            }

            var target = project.POUs.Values.FirstOrDefault(p =>
                string.Equals(p.Name, pou, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return JsonSerializer.Serialize(new { Error = "PouNotFound", Pou = pou }, JsonOpts);
            if (target.Graphical is null || target.Graphical.Networks.Count == 0)
                return JsonSerializer.Serialize(new
                {
                    Error    = "NotGraphical",
                    Pou      = target.Name,
                    Language = target.Language.ToString(),
                    Hint     = "This POU isn't FBD/LD/IL (or has no networks); use twincat.read_pou for its ST source.",
                }, JsonOpts);

            return JsonSerializer.Serialize(new
            {
                Pou      = target.Name,
                Language = target.Language.ToString(),
                Networks = target.Graphical.Networks.Count,
                Mermaid  = FbdDiagramRenderer.ToMermaid(target),
            }, JsonOpts);
        });
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 3: twincat_alarm_list
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [McpServerTool(Name = "twincat_alarm_list"), Description(
        "Extract all alarms from a TwinCAT 3 project as a flat JSON list. " +
        "Each alarm includes severity, trigger condition, delay, module path, " +
        "variable path, and reason if severity is unresolved.")]
    public static string AlarmList(
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        return RunWithProject(analyser, project_path, project =>
            JsonSerializer.Serialize(project.AllAlarms, JsonOpts));
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 4: twincat_state_machines
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [McpServerTool(Name = "twincat_state_machines"), Description(
        "Extract state machines from a TwinCAT 3 project. Returns states, " +
        "transitions with conditions and timeouts, and code bodies per state. " +
        "Optionally filter by module name.")]
    public static string StateMachines(
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path,
        [Description("Optional: filter by POU/module name (e.g. 'CM_Sealer')")]
        string? module_name = null)
    {
        return RunWithProject(analyser, project_path, project =>
        {
            var machines = project.AllStateMachines.AsEnumerable();
            if (!string.IsNullOrEmpty(module_name))
                machines = machines.Where(sm =>
                    sm.OwnerPou.Contains(module_name, StringComparison.OrdinalIgnoreCase));
            return JsonSerializer.Serialize(machines.ToList(), JsonOpts);
        });
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 5: twincat_module_info
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [McpServerTool(Name = "twincat_module_info"), Description(
        "Get detailed information about a specific TwinCAT module/POU. " +
        "Returns variables, methods, properties, inheritance chain, " +
        "alarms, state machines, and IO mappings.")]
    public static string ModuleInfo(
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path,
        [Description("Module/POU name (e.g. 'CM_Sealer', 'EM_Fill', 'UM_Machine')")]
        string module_name)
    {
        return RunWithProject(analyser, project_path, project =>
        {
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
        });
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Tool 6: twincat_io_map
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [McpServerTool(Name = "twincat_io_map"), Description(
        "Extract all IO mappings (AT bindings) from a TwinCAT 3 project. " +
        "Returns variable name, data type, AT address, direction (Input/Output/Memory), " +
        "and source GVL or POU.")]
    public static string IoMap(
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        return RunWithProject(analyser, project_path, project =>
            JsonSerializer.Serialize(project.AllIoMappings, JsonOpts));
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
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        return RunWithProject(analyser, project_path, project =>
            JsonSerializer.Serialize(project.LibraryDependencies, JsonOpts));
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
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        return RunWithProject(analyser, project_path, project =>
            JsonSerializer.Serialize(project.SafetyArtifacts, JsonOpts));
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
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        return RunWithProject(analyser, project_path, project =>
            JsonSerializer.Serialize(project.DriveManagerArtifacts, JsonOpts));
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
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory")]
        string project_path)
    {
        return RunWithProject(analyser, project_path, project =>
            JsonSerializer.Serialize(project.ScopeArtifacts, JsonOpts));
    }

    // ──────────────────────────────────────────────────────────────────
    // Tool 11: twincat_get_fingerprint_symbols
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scalar PLC primitives that are safe to probe via ads_read on any TwinCAT runtime.
    /// Composite/FB/structure types are excluded because they need ads_read_structure
    /// and add no extra discriminating power for fingerprinting.
    /// </summary>
    private static readonly HashSet<string> ScalarPlcTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL",
        "BYTE", "WORD", "DWORD", "LWORD",
        "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT",
        "REAL", "LREAL",
        "STRING", "WSTRING",
        "TIME", "LTIME", "TIME_OF_DAY", "TOD", "DATE", "DATE_AND_TIME", "DT"
    };

    /// <summary>
    /// GVL namespaces shipped by TwinCAT/Beckhoff libraries. These appear in *.library
    /// dependencies, not in the user's .TcGVL files, but they're filtered defensively
    /// in case a project ever surfaces one.
    /// </summary>
    private static readonly string[] FrameworkGvlPrefixes = new[]
    {
        "Tc_", "Tc2_", "Tc3_",
        "_3S_", "_Implicit_",
        "Constants",
        "Events",
        "GlobalAlarmEvents",
        "TwinCAT_SystemInfoVarList"
    };

    [McpServerTool(Name = "twincat_get_fingerprint_symbols"), Description(
        "Pick a set of project-specific ADS symbols suitable for verifying that a remote " +
        "PLC is running this codebase. Walks the user-authored GVLs, filters out TwinCAT/" +
        "Beckhoff framework namespaces, and selects scalar (BOOL/INT/REAL/STRING/...) entries " +
        "whose full instance path is `GvlName.VarName`. Pair the returned `Symbols` array with " +
        "ads_fingerprint_match to confirm 'this NetID is the PLC running my known TwinCAT project'. " +
        "More reliable than UDP/48899 discovery alone, and works through any successful ADS connect.")]
    public static string GetFingerprintSymbols(
        AnalyserService analyser,
        [Description("Path to the TwinCAT PLC project directory (containing .TcPOU, .TcDUT, .TcGVL files)")]
        string project_path,
        [Description("Maximum number of fingerprint symbols to return (default 10).")]
        int count = 10)
    {
        return RunWithProject(analyser, project_path, project =>
        {
            if (count <= 0) count = 10;

        var candidates = new List<object>();

        // Walk every GVL in the parsed project.
        foreach (var gvl in project.GVLs.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsFrameworkGvl(gvl.Name)) continue;

            foreach (var v in gvl.Variables)
            {
                // Skip constants and externals; they're stable but less useful since their
                // presence proves library identity, not project identity.
                if (v.Scope == VarScope.Constant) continue;

                // Reject reference / pointer / array bindings — ads_read can't dereference them directly.
                if (v.IsReference || v.IsPointer || v.IsArray) continue;
                if (string.IsNullOrWhiteSpace(v.Name)) continue;

                var bareType = v.DataType?.Trim() ?? "";
                if (string.IsNullOrEmpty(bareType)) continue;

                // Trim "STRING(80)" → "STRING" for the scalar test.
                int paren = bareType.IndexOf('(');
                var typeKey = paren > 0 ? bareType.Substring(0, paren) : bareType;
                if (!ScalarPlcTypes.Contains(typeKey)) continue;

                candidates.Add(new
                {
                    Symbol = $"{gvl.Name}.{v.Name}",
                    Gvl    = gvl.Name,
                    Var    = v.Name,
                    Type   = bareType,
                    Scope  = v.Scope.ToString(),
                    HasAt  = !string.IsNullOrEmpty(v.AtBinding),
                    Comment= v.Comment
                });
            }
        }

            var picked  = candidates.Take(count).ToList();
            var symbols = picked.Select(c => (string)c.GetType().GetProperty("Symbol")!.GetValue(c)!).ToList();

            return JsonSerializer.Serialize(new
            {
                Project       = project.Name,
                ProjectPath   = project.ProjectPath,
                Available     = candidates.Count,
                Returned      = picked.Count,
                Symbols       = symbols,   // pass straight into ads_fingerprint_match
                Detail        = picked,
                Usage         = "Pass `Symbols` as `fingerprintSymbols` to ads_fingerprint_match after ads_connect."
            }, JsonOpts);
        });
    }

    private static bool IsFrameworkGvl(string gvlName)
    {
        foreach (var p in FrameworkGvlPrefixes)
            if (gvlName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
