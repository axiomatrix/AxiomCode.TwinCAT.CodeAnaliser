namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

public class TcProject
{
    public string Name { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public DateTime AnalysisDate { get; set; } = DateTime.UtcNow;

    public Dictionary<string, TcPou> POUs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TcDut> DUTs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TcGvl> GVLs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> UnresolvedTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Analysis results
    public List<ObjectTreeNode> ObjectTree { get; set; } = new();
    public List<AlarmInfo> AllAlarms { get; set; } = new();
    public List<StateMachine> AllStateMachines { get; set; } = new();
    public List<IoMapping> AllIoMappings { get; set; } = new();

    // Project-surface artifacts (beyond the core PLC source files)
    public List<LibraryDependency> LibraryDependencies { get; set; } = new();
    public List<SafetyArtifact> SafetyArtifacts { get; set; } = new();
    public List<DriveManagerArtifact> DriveManagerArtifacts { get; set; } = new();
    public List<ScopeArtifact> ScopeArtifacts { get; set; } = new();

    // Summary
    public ProjectSummary Summary { get; set; } = new();
}

public class ProjectSummary
{
    public int PouCount { get; set; }
    public int DutCount { get; set; }
    public int GvlCount { get; set; }
    public int TotalAlarms { get; set; }
    public int CriticalAlarms { get; set; }
    public int ProcessAlarms { get; set; }
    public int AdvisoryAlarms { get; set; }
    public int InformationAlarms { get; set; }
    public int UnresolvedAlarms { get; set; }
    public int StateMachineCount { get; set; }
    public int IoPointCount { get; set; }
    public int UnresolvedTypeCount { get; set; }
    public int TreeDepth { get; set; }

    // Project-surface artifact counts
    public int LibraryDependencyCount { get; set; }
    public int SafetyArtifactCount { get; set; }
    public int DriveManagerArtifactCount { get; set; }
    public int ScopeArtifactCount { get; set; }
}
