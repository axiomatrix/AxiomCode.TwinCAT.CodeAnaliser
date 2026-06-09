namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

/// <summary>
/// The persisted, content-fingerprinted "single source of truth" for a TwinCAT
/// project: the full parsed <see cref="TcProject"/> plus identity + freshness
/// metadata. Built once per source <see cref="Fingerprint"/> and reused by every
/// consumer (chat analysis, documentation) instead of each re-parsing the tree.
/// </summary>
public sealed class ProjectKnowledgeModel
{
    /// <summary>Bump when the serialized shape OR the parser's extraction logic changes,
    /// so stale caches are discarded and the project is re-parsed rather than served from
    /// a cache built by an older parser. (v2: fixed EXTENDS resolution for ABSTRACT FBs and
    /// method/property access-modifier parsing when a leading comment/pragma precedes the
    /// declaration. v3: added EtherCAT hardware topology (.xti boxes/channels) and the
    /// reconciled software↔hardware unified IO map.)</summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Absolute, normalized project root the model was built from.</summary>
    public string ProjectPath { get; set; } = "";

    /// <summary>Content fingerprint of all source files — the cache key + freshness
    /// check. Changes whenever any source file is added, removed, or edited.</summary>
    public string Fingerprint { get; set; } = "";

    public int SourceFileCount { get; set; }
    public DateTime BuiltUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The complete parsed project model — the canonical detail.</summary>
    public TcProject Project { get; set; } = new();
}
