using System.Text;
using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Decodes a TwinCAT graphical POU/method body — the <c>&lt;Implementation&gt;&lt;NWL&gt;</c>
/// network list used for FBD / LD / IL — into the language-neutral
/// <see cref="TcGraphicalImpl"/> model, and renders a readable Structured-Text
/// equivalent. Before this, graphical bodies (~half of real-world POUs) were
/// dropped entirely because the parser only read <c>&lt;Implementation&gt;&lt;ST&gt;</c>.
///
/// <para>The NWL payload is a CODESYS/TwinCAT generic object archive:
/// <c>&lt;o&gt;</c>=object (optional <c>n=</c> field name, <c>t=</c> type),
/// <c>&lt;v&gt;</c>=scalar value (strings double-quoted), <c>&lt;l2&gt;</c>=list
/// (<c>cet=</c> gives the child element type), <c>&lt;n/&gt;</c>=null. Node kinds:
/// <c>BoxTreeAssign</c> (out := expr), <c>BoxTreeBox</c> (FB/operator call),
/// <c>BoxTreeOperand</c> (leaf variable).</para>
/// </summary>
public static class NwlGraphicalParser
{
    /// <summary>True if the &lt;Implementation&gt; element holds a graphical body we can decode.</summary>
    public static bool IsGraphical(XElement? implementation)
        => implementation?.Element("NWL") != null
        || implementation?.Element("SFC") != null
        || implementation?.Element("CFC") != null;

    /// <summary>
    /// Parse the &lt;Implementation&gt; element into a graphical model. Returns null
    /// if there's no graphical body (caller falls back to the ST path).
    /// </summary>
    public static TcGraphicalImpl? Parse(XElement? implementation)
    {
        if (implementation == null) return null;

        var nwl = implementation.Element("NWL");
        if (nwl != null) return ParseNwl(nwl);

        // SFC / CFC have their own archives — detect + mark the language now so the
        // body is never silently treated as empty; full step/box decode is staged.
        if (implementation.Element("SFC") != null)
            return new TcGraphicalImpl { Language = ImplLanguage.SFC, ViewMode = "Sfc" };
        if (implementation.Element("CFC") != null)
            return new TcGraphicalImpl { Language = ImplLanguage.CFC, ViewMode = "Cfc" };

        return null;
    }

    private static TcGraphicalImpl? ParseNwl(XElement nwl)
    {
        var data = nwl.Element("XmlArchive")?.Element("Data")?.Element("o");
        if (data == null) return null;

        var impl = new TcGraphicalImpl();
        var viewMode = Unquote(Val(data, "DefaultViewMode"));
        impl.ViewMode = viewMode;
        impl.Language = viewMode.ToUpperInvariant() switch
        {
            "FBD" => ImplLanguage.FBD,
            "LD"  => ImplLanguage.LD,
            "IL"  => ImplLanguage.IL,
            _      => ImplLanguage.Graphical,
        };

        var networkList = List(data, "NetworkList");
        if (networkList != null)
        {
            int idx = 0;
            foreach (var netEl in networkList.Elements("o"))
            {
                var net = ParseNetwork(netEl, idx++);
                if (net != null) impl.Networks.Add(net);
            }
        }

        // Roll up referenced box types + operands for a quick dependency surface.
        var boxTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var operands = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var net in impl.Networks)
            foreach (var node in net.Items)
                CollectRefs(node, boxTypes, operands);
        impl.ReferencedBoxTypes = boxTypes.ToList();
        impl.ReferencedOperands  = operands.ToList();

        impl.StEquivalent = RenderSt(impl);
        return impl;
    }

    private static TcNetwork? ParseNetwork(XElement netEl, int index)
    {
        var net = new TcNetwork
        {
            Index        = index,
            Title        = Unquote(Val(netEl, "Title")),
            Label        = Unquote(Val(netEl, "Label")),
            Comment      = Unquote(Val(netEl, "Comment")),
            OutCommented = string.Equals(Val(netEl, "OutCommented"), "true", StringComparison.OrdinalIgnoreCase),
        };

        var items = List(netEl, "NetworkItems");
        if (items != null)
        {
            var cet = items.Attribute("cet")?.Value;
            foreach (var itemEl in items.Elements("o"))
            {
                var node = ParseNode(itemEl, cet);
                if (node != null) net.Items.Add(node);
            }
        }
        return net;
    }

    /// <summary>Parse one box-tree node. Its kind is the element's own <c>t=</c>
    /// type, falling back to the containing list's <c>cet=</c> hint.</summary>
    private static TcGraphNode? ParseNode(XElement el, string? listCet)
    {
        var type = el.Attribute("t")?.Value ?? listCet ?? "";
        switch (type)
        {
            case "BoxTreeAssign": return ParseAssign(el);
            case "BoxTreeBox":    return ParseBox(el);
            case "BoxTreeOperand":return ParseTreeOperand(el);
            case "Operand":       return ParseLeafOperand(el);
            default:
                // Heuristic when the type token is absent: a BoxType ⇒ box, else operand.
                if (Val(el, "BoxType") != null) return ParseBox(el);
                if (Val(el, "Operand") != null || Obj(el, "Operand") != null) return ParseTreeOperand(el);
                return null;
        }
    }

    private static TcGraphNode ParseAssign(XElement el)
    {
        var node = new TcGraphNode { Kind = TcGraphNodeKind.Assign };

        // OutputItems → list of Operand objects = the LHS targets.
        var outputs = Obj(el, "OutputItems");
        var outList = outputs != null ? List(outputs, "OutputItems") : null;
        if (outList != null)
            foreach (var opEl in outList.Elements("o"))
            {
                var op = Unquote(Val(opEl, "Operand"));
                if (!string.IsNullOrEmpty(op)) node.AssignTargets.Add(op);
            }

        // RValue → the expression feeding the targets.
        var rvalue = Obj(el, "RValue");
        if (rvalue != null) node.RValue = ParseNode(rvalue, null);
        return node;
    }

    private static TcGraphNode ParseBox(XElement el)
    {
        var node = new TcGraphNode
        {
            Kind         = TcGraphNodeKind.Box,
            BoxType      = Unquote(Val(el, "BoxType")),
            CallType     = Val(el, "CallType"),
            InstanceName = Unquote(Val(Obj(el, "Instance"), "Operand")),
        };

        // Inputs (recursive: nested boxes or operands).
        var inputs = List(el, "InputItems");
        if (inputs != null)
        {
            var cet = inputs.Attribute("cet")?.Value;
            foreach (var inEl in inputs.Elements("o"))
            {
                var child = ParseNode(inEl, cet);
                if (child != null) node.Inputs.Add(child);
            }
        }

        // Formal parameter name/type lists.
        ReadParamList(Obj(el, "InputParam"),  node.InputParamNames,  node.InputParamTypes);
        ReadParamList(Obj(el, "OutputParam"), node.OutputParamNames, node.OutputParamTypes);
        return node;
    }

    private static TcGraphNode ParseTreeOperand(XElement el)
    {
        // BoxTreeOperand wraps an inner <o n="Operand" t="Operand"> OR carries the
        // operand fields directly.
        var inner = Obj(el, "Operand");
        var src = inner ?? el;
        return new TcGraphNode
        {
            Kind    = TcGraphNodeKind.Operand,
            Operand = Unquote(Val(src, "Operand")),
            Negated = Val(Obj(src, "Flags"), "Flags") == "1",
        };
    }

    private static TcGraphNode ParseLeafOperand(XElement el) => new()
    {
        Kind    = TcGraphNodeKind.Operand,
        Operand = Unquote(Val(el, "Operand")),
        Negated = Val(Obj(el, "Flags"), "Flags") == "1",
    };

    private static void ReadParamList(XElement? paramList, List<string> names, List<string> types)
    {
        if (paramList == null) return;
        var nameList = List(paramList, "Names");
        var typeList = List(paramList, "Types");
        if (nameList != null) foreach (var v in nameList.Elements("v")) names.Add(v.Value);
        if (typeList != null) foreach (var v in typeList.Elements("v")) types.Add(v.Value);
    }

    private static void CollectRefs(TcGraphNode node, SortedSet<string> boxTypes, SortedSet<string> operands)
    {
        if (node.Kind == TcGraphNodeKind.Box && !string.IsNullOrEmpty(node.BoxType)) boxTypes.Add(node.BoxType!);
        if (!string.IsNullOrEmpty(node.InstanceName)) operands.Add(node.InstanceName!);
        if (node.Kind == TcGraphNodeKind.Operand && !string.IsNullOrEmpty(node.Operand)) operands.Add(node.Operand!);
        foreach (var t in node.AssignTargets) operands.Add(t);
        if (node.RValue != null) CollectRefs(node.RValue, boxTypes, operands);
        foreach (var i in node.Inputs) CollectRefs(i, boxTypes, operands);
    }

    // ── ST rendering (best-effort, readable) ─────────────────────────────────

    private static readonly Dictionary<string, string> InfixOps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["And"]="AND", ["Or"]="OR", ["Xor"]="XOR", ["Add"]="+", ["Sub"]="-", ["Mul"]="*",
        ["Div"]="/", ["Mod"]="MOD", ["Eq"]="=", ["Ne"]="<>", ["Lt"]="<", ["Le"]="<=",
        ["Gt"]=">", ["Ge"]=">=",
    };

    private static string RenderSt(TcGraphicalImpl impl)
    {
        var sb = new StringBuilder();
        foreach (var net in impl.Networks)
        {
            if (!string.IsNullOrEmpty(net.Title))   sb.AppendLine($"// {net.Title}");
            if (!string.IsNullOrEmpty(net.Comment)) sb.AppendLine($"(* {net.Comment} *)");
            foreach (var item in net.Items)
                sb.AppendLine(RenderNode(item) + (item.Kind == TcGraphNodeKind.Assign ? "" : ";"));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderNode(TcGraphNode node)
    {
        switch (node.Kind)
        {
            case TcGraphNodeKind.Operand:
                return (node.Negated ? "NOT " : "") + (node.Operand ?? "");

            case TcGraphNodeKind.Assign:
                var rhs = node.RValue != null ? RenderNode(node.RValue) : "";
                if (node.AssignTargets.Count == 0) return rhs + ";";
                return string.Join("\n", node.AssignTargets.Select(t => $"{t} := {rhs};"));

            case TcGraphNodeKind.Box:
                // Operator boxes → infix; FB/function boxes → call form.
                if (node.CallType is { } ct && InfixOps.TryGetValue(ct, out var op) && node.Inputs.Count >= 2)
                    return "(" + string.Join($" {op} ", node.Inputs.Select(RenderNode)) + ")";
                if (string.Equals(node.CallType, "Not", StringComparison.OrdinalIgnoreCase) && node.Inputs.Count == 1)
                    return "NOT " + RenderNode(node.Inputs[0]);

                var callee = !string.IsNullOrEmpty(node.InstanceName) ? node.InstanceName! : (node.BoxType ?? "CALL");
                var args = new List<string>();
                for (int i = 0; i < node.Inputs.Count; i++)
                {
                    var argText = RenderNode(node.Inputs[i]);
                    var pname = i < node.InputParamNames.Count ? node.InputParamNames[i] : null;
                    args.Add(pname != null ? $"{pname} := {argText}" : argText);
                }
                return $"{callee}({string.Join(", ", args)})";

            default:
                return "";
        }
    }

    // ── generic archive helpers ──────────────────────────────────────────────
    private static string? Val(XElement? o, string name)
        => o?.Elements("v").FirstOrDefault(v => (string?)v.Attribute("n") == name)?.Value;

    private static XElement? Obj(XElement? o, string name)
        => o?.Elements("o").FirstOrDefault(e => (string?)e.Attribute("n") == name);

    private static XElement? List(XElement? o, string name)
        => o?.Elements("l2").FirstOrDefault(e => (string?)e.Attribute("n") == name);

    /// <summary>NWL string scalars are wrapped in double quotes (e.g. <c>"q"</c>).</summary>
    private static string Unquote(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s;
    }
}
