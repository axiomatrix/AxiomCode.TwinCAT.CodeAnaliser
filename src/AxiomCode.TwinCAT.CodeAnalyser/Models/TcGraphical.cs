namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

/// <summary>
/// The implementation language of a POU/method/property/action body.
/// TwinCAT stores Structured Text under <c>&lt;Implementation&gt;&lt;ST&gt;</c>;
/// FBD/LD/IL under <c>&lt;Implementation&gt;&lt;NWL&gt;</c> (a serialized network/
/// box-tree object graph); SFC under <c>&lt;SFC&gt;</c>; CFC under <c>&lt;CFC&gt;</c>.
/// </summary>
public enum ImplLanguage
{
    /// <summary>Body absent (e.g. an interface method or abstract method stub).</summary>
    None,
    /// <summary>Structured Text — the only language the legacy parser understood.</summary>
    ST,
    /// <summary>Function Block Diagram (stored as an NWL network list, DefaultViewMode "Fbd").</summary>
    FBD,
    /// <summary>Ladder Diagram (NWL network list, DefaultViewMode "Ld").</summary>
    LD,
    /// <summary>Instruction List (NWL network list, DefaultViewMode "Il").</summary>
    IL,
    /// <summary>Sequential Function Chart.</summary>
    SFC,
    /// <summary>Continuous Function Chart.</summary>
    CFC,
    /// <summary>An NWL body whose DefaultViewMode wasn't one of the known tokens.</summary>
    Graphical,
}

/// <summary>
/// A graphical (non-ST) POU/method body, decoded from the TwinCAT <c>&lt;NWL&gt;</c>
/// (or SFC/CFC) XML into a language-neutral network/box-tree model. Captures the
/// full logic — every network, box (FB/operator call), operand (variable), and
/// formal-parameter binding — so a graphical POU is no longer a black box to the
/// analysis + documentation pipelines. <see cref="StEquivalent"/> holds a readable
/// Structured-Text rendering of the same logic for LLM consumption and diffing.
/// </summary>
public sealed class TcGraphicalImpl
{
    /// <summary>The concrete graphical language (FBD/LD/IL/SFC/CFC).</summary>
    public ImplLanguage Language { get; set; } = ImplLanguage.Graphical;

    /// <summary>The raw <c>DefaultViewMode</c> token from the NWL archive (e.g. "Fbd").</summary>
    public string ViewMode { get; set; } = "";

    /// <summary>The networks (rungs / FBD sheets) in document order.</summary>
    public List<TcNetwork> Networks { get; set; } = new();

    /// <summary>A best-effort Structured-Text rendering of the whole body, one block
    /// per network. Never authoritative TwinCAT source — a faithful, readable
    /// projection for the LLM, docs, and semantic diff.</summary>
    public string StEquivalent { get; set; } = "";

    /// <summary>Distinct FB/operator box types referenced (e.g. TON, AND, MC_Power) —
    /// a quick dependency/IO surface without walking the tree.</summary>
    public List<string> ReferencedBoxTypes { get; set; } = new();

    /// <summary>Distinct operand (variable) names read or written across all networks.</summary>
    public List<string> ReferencedOperands { get; set; } = new();
}

/// <summary>One network (an FBD sheet / LD rung / IL block).</summary>
public sealed class TcNetwork
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Label { get; set; } = "";
    public string Comment { get; set; } = "";
    public bool OutCommented { get; set; }

    /// <summary>Top-level items of the network (assignments, boxes, operands).</summary>
    public List<TcGraphNode> Items { get; set; } = new();
}

/// <summary>The kind of a node in a graphical network's box tree.</summary>
public enum TcGraphNodeKind
{
    /// <summary>An assignment: <c>output(s) := rvalue</c> (NWL BoxTreeAssign).</summary>
    Assign,
    /// <summary>A function / FB / operator box (NWL BoxTreeBox), e.g. TON, AND, MC_MoveAbsolute.</summary>
    Box,
    /// <summary>A leaf operand — a variable, member access, or literal (NWL BoxTreeOperand).</summary>
    Operand,
}

/// <summary>
/// A node in a graphical network's box tree. A single recursive shape models all
/// three NWL node kinds (assign / box / operand) so the tree can be walked
/// uniformly and rendered to ST.
/// </summary>
public sealed class TcGraphNode
{
    public TcGraphNodeKind Kind { get; set; }

    // ── Box (function / FB / operator call) ──────────────────────────────────
    /// <summary>For <see cref="TcGraphNodeKind.Box"/>: the box type (TON, AND, MC_Power…).</summary>
    public string? BoxType { get; set; }
    /// <summary>For an FB box: the instance variable name (e.g. "fbontimer"). Null for stateless operators.</summary>
    public string? InstanceName { get; set; }
    /// <summary>NWL CallType — "FunctionBlock", "Function", or an operator name ("And", "Add"…).</summary>
    public string? CallType { get; set; }
    /// <summary>Ordered input nodes wired into the box (recursive: boxes or operands).</summary>
    public List<TcGraphNode> Inputs { get; set; } = new();
    /// <summary>Formal input parameter names (e.g. IN, PT) aligned to <see cref="Inputs"/> where available.</summary>
    public List<string> InputParamNames { get; set; } = new();
    /// <summary>Formal input parameter types (e.g. BOOL, TIME).</summary>
    public List<string> InputParamTypes { get; set; } = new();
    /// <summary>Formal output parameter names (e.g. Q, ET).</summary>
    public List<string> OutputParamNames { get; set; } = new();
    /// <summary>Formal output parameter types.</summary>
    public List<string> OutputParamTypes { get; set; } = new();

    // ── Assign ───────────────────────────────────────────────────────────────
    /// <summary>For <see cref="TcGraphNodeKind.Assign"/>: the left-hand-side target operands.</summary>
    public List<string> AssignTargets { get; set; } = new();
    /// <summary>For an assign / box: the value node feeding the output(s).</summary>
    public TcGraphNode? RValue { get; set; }

    // ── Operand (leaf) ───────────────────────────────────────────────────────
    /// <summary>For <see cref="TcGraphNodeKind.Operand"/>: the variable/member/literal text.</summary>
    public string? Operand { get; set; }
    /// <summary>Operand has a NOT/negate flag on its pin.</summary>
    public bool Negated { get; set; }
}
