using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Persistent, content-fingerprinted store for the canonical TwinCAT
/// <see cref="ProjectKnowledgeModel"/> — the "single source of truth" both the
/// chat analysis and the documentation generator read from. Parses a project
/// once (via <see cref="AnalyserService"/>), caches the lossless model to disk
/// keyed by a source fingerprint, and returns it instantly on subsequent calls
/// until a source file changes.
///
/// <para>Storage layout: <c>{StoreRoot}/{projectId}/{fingerprint}.json</c>, where
/// projectId is a hash of the project path and the fingerprint a hash of every
/// source file's relative path + length + last-write time. Old fingerprints per
/// project are pruned to <see cref="KeepPerProject"/>.</para>
/// </summary>
public sealed class ProjectKnowledgeStore
{
    private static readonly string[] SourceExtensions =
        { ".TcPOU", ".TcDUT", ".TcGVL", ".TcIO", ".plcproj", ".tsproj", ".xti", ".tmc" };

    private static readonly string[] ExcludedDirs =
        { "_Boot", "_CompileInfo", "bin", "obj", ".git", ".svn", ".vs" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<string, TcProject> _build;

    /// <summary>Base directory for the on-disk cache.</summary>
    public string StoreRoot { get; }

    /// <summary>How many fingerprints to keep per project before pruning the oldest.</summary>
    public int KeepPerProject { get; init; } = 3;

    /// <summary>Construct over an <see cref="AnalyserService"/> (the usual case).</summary>
    public ProjectKnowledgeStore(AnalyserService analyser, string? storeRoot = null)
        : this(analyser.AnalyseProject, storeRoot) { }

    /// <summary>Construct over a custom build delegate (testing / alternate parsers).</summary>
    public ProjectKnowledgeStore(Func<string, TcProject> build, string? storeRoot = null)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        StoreRoot = storeRoot ?? DefaultStoreRoot();
    }

    private static string DefaultStoreRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AxiomCode", "TwinCAT.CodeAnalyser", "pkm");

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Return the cached model when the project's fingerprint is unchanged;
    /// otherwise (re)build it via the parser and persist. The single entry point
    /// every consumer should use.</summary>
    public ProjectKnowledgeModel GetOrBuild(string projectRoot, bool forceRebuild = false)
    {
        var normalized = Path.GetFullPath(projectRoot);
        var fp = ComputeFingerprint(normalized);
        var path = CachePath(normalized, fp);

        if (!forceRebuild && File.Exists(path))
        {
            var cached = TryDeserialize(path);
            if (cached is { SchemaVersion: ProjectKnowledgeModel.CurrentSchemaVersion })
                return cached;
        }

        var project = _build(normalized);   // throws on missing/empty project (by design)
        var model = new ProjectKnowledgeModel
        {
            ProjectPath     = normalized,
            Fingerprint     = fp,
            SourceFileCount = EnumerateSourceFiles(normalized).Count,
            BuiltUtc        = DateTime.UtcNow,
            Project         = project,
        };
        Save(model);
        return model;
    }

    /// <summary>True if a cached model for the project's CURRENT fingerprint exists
    /// (i.e. <see cref="GetOrBuild"/> would be an instant cache hit).</summary>
    public bool IsFresh(string projectRoot)
    {
        var normalized = Path.GetFullPath(projectRoot);
        return File.Exists(CachePath(normalized, ComputeFingerprint(normalized)));
    }

    /// <summary>Compute the source fingerprint without parsing — fast change detection.</summary>
    public string ComputeFingerprint(string projectRoot)
    {
        var normalized = Path.GetFullPath(projectRoot);
        var files = EnumerateSourceFiles(normalized)
            .Select(f => new FileInfo(f))
            .OrderBy(fi => Path.GetRelativePath(normalized, fi.FullName), StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append('v').Append(ProjectKnowledgeModel.CurrentSchemaVersion).Append('\n');
        foreach (var fi in files)
            sb.Append(Path.GetRelativePath(normalized, fi.FullName)).Append('|')
              .Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
        return Sha(sb.ToString());
    }

    public IReadOnlyList<string> EnumerateSourceFiles(string projectRoot)
    {
        var list = new List<string>();
        if (!Directory.Exists(projectRoot)) return list;
        foreach (var f in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f);
            if (!SourceExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))) continue;
            if (IsExcluded(f, projectRoot)) continue;
            list.Add(f);
        }
        return list;
    }

    public void Save(ProjectKnowledgeModel model)
    {
        var dir = ProjectDir(model.ProjectPath);
        Directory.CreateDirectory(dir);
        File.WriteAllText(CachePath(model.ProjectPath, model.Fingerprint), JsonSerializer.Serialize(model, JsonOpts));
        Prune(dir, KeepPerProject);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static bool IsExcluded(string file, string root)
    {
        var rel = Path.GetRelativePath(root, file);
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (ExcludedDirs.Any(d => string.Equals(d, seg, StringComparison.OrdinalIgnoreCase)))
                return true;
        return false;
    }

    private ProjectKnowledgeModel? TryDeserialize(string path)
    {
        try
        {
            var model = JsonSerializer.Deserialize<ProjectKnowledgeModel>(File.ReadAllText(path), JsonOpts);
            if (model?.Project != null) NormalizeAfterLoad(model.Project);
            return model;
        }
        catch { return null; }
    }

    /// <summary>System.Text.Json deserializes dictionaries with the default (ordinal,
    /// case-sensitive) comparer; the analysis code relies on OrdinalIgnoreCase
    /// lookups, so rebuild the maps with the right comparer after load.</summary>
    private static void NormalizeAfterLoad(TcProject p)
    {
        p.POUs = new Dictionary<string, TcPou>(p.POUs, StringComparer.OrdinalIgnoreCase);
        p.DUTs = new Dictionary<string, TcDut>(p.DUTs, StringComparer.OrdinalIgnoreCase);
        p.GVLs = new Dictionary<string, TcGvl>(p.GVLs, StringComparer.OrdinalIgnoreCase);
        p.UnresolvedTypes = new HashSet<string>(p.UnresolvedTypes, StringComparer.OrdinalIgnoreCase);
    }

    private string ProjectDir(string normalizedRoot)
        => Path.Combine(StoreRoot, Sha(normalizedRoot.ToLowerInvariant())[..16]);

    private string CachePath(string normalizedRoot, string fingerprint)
        => Path.Combine(ProjectDir(normalizedRoot), fingerprint[..16] + ".json");

    private static void Prune(string dir, int keep)
    {
        try
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
                try { f.Delete(); } catch { /* best-effort */ }
        }
        catch { /* best-effort */ }
    }

    private static string Sha(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
