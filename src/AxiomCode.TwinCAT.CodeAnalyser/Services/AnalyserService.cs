using Microsoft.Extensions.Logging;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Orchestrates TwinCAT project parsing and analysis.
/// Caches results per project path for efficiency.
/// </summary>
public class AnalyserService
{
    private readonly ILogger<AnalyserService> _logger;
    private readonly Dictionary<string, TcProject> _cache = new();

    public AnalyserService(ILogger<AnalyserService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyse a TwinCAT PLC project. Returns cached result if available.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// Thrown when <paramref name="projectPath"/> does not exist on disk.
    /// Prevents the silent empty-success result that caused downstream
    /// consumers (LLM tool callers, doc generators) to render a misspelled
    /// path's "0 POUs" reply as a valid empty project.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the path exists but contains no TwinCAT source files
    /// (no .TcPOU, .TcDUT, or .TcGVL anywhere in the subtree). Distinct
    /// from DirectoryNotFoundException so callers can give a different
    /// hint ("path resolves but isn't a TwinCAT project — point at the
    /// folder containing the .plcproj / .TcPOU files").
    /// </exception>
    public TcProject AnalyseProject(string projectPath)
    {
        // ── Guard #1: input must be non-null / non-empty. Catches null
        // arguments before Path.GetFullPath throws an unhelpful ArgumentNullException.
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException(
                "project_path is empty. Pass the directory containing the " +
                "TwinCAT plcproject (.TcPOU/.TcDUT/.TcGVL files).",
                nameof(projectPath));

        var normalizedPath = Path.GetFullPath(projectPath);

        // ── Guard #2: directory must exist on disk. Without this guard a
        // misspelled path silently returns an empty-success result with
        // zero POUs/DUTs/GVLs, which downstream consumers interpret as
        // "valid empty project" — propagating the typo into reports.
        // Real-world repro: model emitted "beckhoff-training-rig-elevator"
        // (all hyphens) for a folder actually named
        // "beckhoff-training_rig-elevator" (underscore between training and
        // rig); analyser cheerfully returned 0 POUs instead of failing.
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException(
                $"TwinCAT project directory not found: '{normalizedPath}'. " +
                $"Check the path — TwinCAT projects typically nest the " +
                $".TcPOU/.TcDUT/.TcGVL files under " +
                $"<Solution>/<Project>/<PlcProject>/<PlcProject>/.");
        }

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

        // ── Guard #3: at least one TwinCAT source file must have been
        // found. If we got here with 0/0/0 the path resolves but doesn't
        // point at a TwinCAT project (common cause: caller passed the
        // build output folder, a docs folder, or the repo root from a
        // multi-project solution). Cache the empty project before throwing
        // so a follow-up call with the same wrong path doesn't repeat
        // the disk walk.
        if (project.POUs.Count == 0 && project.DUTs.Count == 0 && project.GVLs.Count == 0)
        {
            throw new InvalidOperationException(
                $"No TwinCAT source files found under '{normalizedPath}'. " +
                $"The directory exists but contains no .TcPOU, .TcDUT, or .TcGVL " +
                $"files. Point at the folder containing the .plcproj — typically " +
                $"two levels above where the source folders (POUs/, DUTs/, GVLs/) live.");
        }

        // Step 3: Resolve inheritance
        InheritanceResolver.Resolve(project);

        // Step 4: Build object tree
        ObjectTreeBuilder.Build(project);

        // Step 5: Analyse alarms (per-POU first pass)
        AlarmAnalyser.Analyse(project);

        // Step 5b: Cross-POU deep alarm severity resolution.
        // Resolves the BaseClass-driven Unresolved bucket by scanning every
        // _AlarmsPresentX block project-wide and binding alarms that appear
        // unambiguously in exactly one severity tier.
        DeepAlarmResolver.Resolve(project);

        // Step 5c: Heuristic alarm descriptions. Every alarm gets a baseline
        // human-readable description from name + trigger + severity even before
        // any AI interpretation runs. Higher-quality AI-derived descriptions can
        // be overlaid later via AlarmDescriptionEnricher.ApplyOverrides.
        AlarmDescriptionEnricher.ApplyHeuristic(project);

        // Step 6: Extract state machines
        StateMachineExtractor.Extract(project);

        // Step 7: Extract IO mappings
        IoMappingParser.Extract(project);

        // Step 7b: Distribute IO mappings to tree nodes
        DistributeIoToTree(project);

        // Step 7c: Parse the EtherCAT hardware topology (.xti boxes + IO links).
        project.HardwareBoxes = HardwareTopologyParser.Discover(normalizedPath, _logger);

        // Step 7d: Reconcile software IO + hardware topology into the unified IO map.
        project.UnifiedIo = UnifiedIoReconciler.Build(project);

        // Step 8: Project-surface artifact discovery
        project.LibraryDependencies = LibraryDependencyParser.Discover(normalizedPath);
        project.SafetyArtifacts = SafetyProjectParser.Discover(normalizedPath);
        project.DriveManagerArtifacts = DriveManagerParser.Discover(normalizedPath);
        project.ScopeArtifacts = ScopeParser.Discover(normalizedPath);

        // Step 9: Build summary
        BuildSummary(project);

        _cache[normalizedPath] = project;

        _logger.LogInformation(
            "Analysis complete: {A} alarms, {S} state machines, {I} IO points, {Hb} HW boxes, {Ui} unified IO rows, {L} libraries, {Sf} safety, {D} drives, {Sc} scopes",
            project.AllAlarms.Count, project.AllStateMachines.Count, project.AllIoMappings.Count,
            project.HardwareBoxes.Count, project.UnifiedIo.Count,
            project.LibraryDependencies.Count, project.SafetyArtifacts.Count,
            project.DriveManagerArtifacts.Count, project.ScopeArtifacts.Count);

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
        s.HardwareBoxCount = project.HardwareBoxes.Count;
        s.HardwareChannelCount = project.HardwareBoxes.Sum(b => b.Channels.Count);
        s.UnifiedIoRowCount = project.UnifiedIo.Count;
        s.UnresolvedTypeCount = project.UnresolvedTypes.Count;
        s.TreeDepth = CalcTreeDepth(project.ObjectTree, 0);
        s.LibraryDependencyCount = project.LibraryDependencies.Count;
        s.SafetyArtifactCount = project.SafetyArtifacts.Count;
        s.DriveManagerArtifactCount = project.DriveManagerArtifacts.Count;
        s.ScopeArtifactCount = project.ScopeArtifacts.Count;
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
