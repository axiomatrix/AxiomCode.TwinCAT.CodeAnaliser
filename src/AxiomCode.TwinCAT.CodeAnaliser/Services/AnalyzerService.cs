using Microsoft.Extensions.Logging;
using AxiomCode.TwinCAT.CodeAnaliser.Models;

namespace AxiomCode.TwinCAT.CodeAnaliser.Services;

/// <summary>
/// Orchestrates TwinCAT project parsing and analysis.
/// Caches results per project path for efficiency.
/// </summary>
public class AnalyzerService
{
    private readonly ILogger<AnalyzerService> _logger;
    private readonly Dictionary<string, TcProject> _cache = new();

    public AnalyzerService(ILogger<AnalyzerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze a TwinCAT PLC project. Returns cached result if available.
    /// </summary>
    public TcProject AnalyzeProject(string projectPath)
    {
        var normalizedPath = Path.GetFullPath(projectPath);

        if (_cache.TryGetValue(normalizedPath, out var cached))
        {
            _logger.LogInformation("Returning cached analysis for {Path}", normalizedPath);
            return cached;
        }

        _logger.LogInformation("Starting analysis of {Path}", normalizedPath);

        // Step 1: Discover files
        var project = TcProjectParser.Parse(normalizedPath, _logger);

        // Step 2: Parse all source files
        foreach (var file in TcProjectParser.FindTcPouFiles(normalizedPath))
        {
            var pou = TcPouParser.Parse(file, normalizedPath);
            if (pou != null)
                project.POUs[pou.Name] = pou;
        }

        foreach (var file in TcProjectParser.FindTcDutFiles(normalizedPath))
        {
            var dut = TcDutParser.Parse(file, normalizedPath);
            if (dut != null)
                project.DUTs[dut.Name] = dut;
        }

        foreach (var file in TcProjectParser.FindTcGvlFiles(normalizedPath))
        {
            var gvl = TcGvlParser.Parse(file, normalizedPath);
            if (gvl != null)
                project.GVLs[gvl.Name] = gvl;
        }

        _logger.LogInformation("Parsed {P} POUs, {D} DUTs, {G} GVLs",
            project.POUs.Count, project.DUTs.Count, project.GVLs.Count);

        // Step 3: Resolve inheritance
        InheritanceResolver.Resolve(project);

        // Step 4: Build object tree
        ObjectTreeBuilder.Build(project);

        // Step 5: Analyze alarms
        AlarmAnalyzer.Analyze(project);

        // Step 6: Extract state machines
        StateMachineExtractor.Extract(project);

        // Step 7: Extract IO mappings
        IoMappingParser.Extract(project);

        // Step 7b: Distribute IO mappings to tree nodes
        DistributeIoToTree(project);

        // Step 8: Build summary
        BuildSummary(project);

        _cache[normalizedPath] = project;

        _logger.LogInformation("Analysis complete: {A} alarms, {S} state machines, {I} IO points",
            project.AllAlarms.Count, project.AllStateMachines.Count, project.AllIoMappings.Count);

        return project;
    }

    /// <summary>Force re-analysis by clearing cache.</summary>
    public void ClearCache(string? projectPath = null)
    {
        if (projectPath != null)
            _cache.Remove(Path.GetFullPath(projectPath));
        else
            _cache.Clear();
    }

    private static void BuildSummary(TcProject project)
    {
        var s = project.Summary;
        s.PouCount = project.POUs.Count;
        s.DutCount = project.DUTs.Count;
        s.GvlCount = project.GVLs.Count;
        s.TotalAlarms = project.AllAlarms.Count;
        s.CriticalAlarms = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Critical);
        s.ProcessAlarms = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Process);
        s.AdvisoryAlarms = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Advisory);
        s.InformationAlarms = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Information);
        s.UnresolvedAlarms = project.AllAlarms.Count(a => a.Severity == AlarmSeverity.Unresolved);
        s.StateMachineCount = project.AllStateMachines.Count;
        s.IoPointCount = project.AllIoMappings.Count;
        s.UnresolvedTypeCount = project.UnresolvedTypes.Count;
        s.TreeDepth = CalcTreeDepth(project.ObjectTree, 0);
    }

    /// <summary>
    /// Distribute IO mappings from the flat list to their matching tree nodes.
    /// Matches by TypeName and its inheritance chain (e.g. CM_Axis extends CM_Axis_V5).
    /// </summary>
    private static void DistributeIoToTree(TcProject project)
    {
        // Build lookup: POU type name -> list of IO mappings
        var ioByPou = new Dictionary<string, List<IoMapping>>(StringComparer.OrdinalIgnoreCase);
        foreach (var io in project.AllIoMappings)
        {
            var key = io.SourcePou ?? io.SourceGvl ?? "";
            if (string.IsNullOrEmpty(key)) continue;
            if (!ioByPou.ContainsKey(key))
                ioByPou[key] = new List<IoMapping>();
            ioByPou[key].Add(io);
        }

        // Walk tree and assign
        void Walk(List<ObjectTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                // Direct match on type name
                if (!string.IsNullOrEmpty(node.TypeName) && ioByPou.TryGetValue(node.TypeName, out var direct))
                    node.IoMappings.AddRange(direct);

                // Also check inheritance chain types
                if (project.POUs.TryGetValue(node.TypeName ?? "", out var pou))
                {
                    foreach (var ancestor in pou.InheritanceChain)
                    {
                        if (ioByPou.TryGetValue(ancestor, out var inherited))
                        {
                            foreach (var io in inherited)
                            {
                                if (!node.IoMappings.Any(existing =>
                                    existing.VariableName == io.VariableName && existing.AtBinding == io.AtBinding))
                                    node.IoMappings.Add(io);
                            }
                        }
                    }
                }

                Walk(node.Children);
            }
        }

        Walk(project.ObjectTree);
    }

    private static int CalcTreeDepth(List<ObjectTreeNode> nodes, int depth)
    {
        if (nodes.Count == 0) return depth;
        return nodes.Max(n => CalcTreeDepth(n.Children, depth + 1));
    }
}
