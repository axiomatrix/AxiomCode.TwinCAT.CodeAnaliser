using System.Text;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Renders a decoded graphical POU/action body (<see cref="TcGraphicalImpl"/> —
/// FBD / LD / IL networks) as a <b>Mermaid flowchart</b> (data-flow graph): boxes
/// (FB / operator calls) and operands (variables / literals) become nodes wired by
/// their inputs, outputs, and formal parameter names — one subgraph per network.
///
/// <para>Mermaid is the render target because DTD already displays it end-to-end
/// (WebView2 + vendored mermaid.min.js), and DocumentGen can embed the same string.
/// Operands are merged by name within a network so a variable read in two places
/// shows once; boxes stay distinct per instance. Flow reads left→right like FBD:
/// operands → box → assigned targets.</para>
/// </summary>
public static class FbdDiagramRenderer
{
    /// <summary>Render a POU's graphical body, or "" if it isn't graphical.</summary>
    public static string ToMermaid(TcPou pou)
        => pou.Graphical is { } g ? ToMermaid(g, pou.Name) : "";

    /// <summary>Render a decoded graphical body as a Mermaid flowchart.</summary>
    public static string ToMermaid(TcGraphicalImpl graphical, string pouName = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        if (!string.IsNullOrEmpty(pouName))
            sb.AppendLine($"  %% {pouName.Replace('\n', ' ')} — {graphical.Language} ({graphical.Networks.Count} network(s))");

        for (int i = 0; i < graphical.Networks.Count; i++)
        {
            var net = graphical.Networks[i];
            var ctx = new Ctx(i);
            foreach (var item in net.Items) RenderNode(item, ctx);

            var title = string.IsNullOrWhiteSpace(net.Title) ? "" : ": " + Esc(net.Title);
            sb.AppendLine($"  subgraph nw{i}[\"Network {i}{title}\"]");
            sb.AppendLine("    direction LR");
            foreach (var n in ctx.Nodes) sb.AppendLine("    " + n);
            if (ctx.Nodes.Count == 0) sb.AppendLine($"    nw{i}_e[\"(empty)\"]");
            foreach (var e in ctx.Edges) sb.AppendLine("    " + e);
            sb.AppendLine("  end");
        }
        return sb.ToString().TrimEnd();
    }

    private sealed class Ctx(int net)
    {
        public readonly int Net = net;
        private int _counter;
        public readonly List<string> Nodes = new();
        public readonly List<string> Edges = new();
        public readonly Dictionary<string, string> Operands = new();
        public string NewId(string prefix) => $"{prefix}{Net}_{_counter++}";
    }

    /// <summary>Render a box-tree node; returns the id of the node that REPRESENTS
    /// the value this subtree produces (so the caller can wire it onward).</summary>
    private static string RenderNode(TcGraphNode node, Ctx ctx)
    {
        switch (node.Kind)
        {
            case TcGraphNodeKind.Operand:
                return OperandNode(ctx, node.Operand ?? "", node.Negated);

            case TcGraphNodeKind.Box:
            {
                var id = ctx.NewId("box");
                var label = string.IsNullOrEmpty(node.InstanceName)
                    ? Esc(node.BoxType ?? "CALL")
                    : $"{Esc(node.BoxType ?? "")}<br>{Esc(node.InstanceName!)}";
                ctx.Nodes.Add($"{id}[\"{label}\"]");
                for (int k = 0; k < node.Inputs.Count; k++)
                {
                    var inId = RenderNode(node.Inputs[k], ctx);
                    var pname = k < node.InputParamNames.Count ? node.InputParamNames[k] : null;
                    ctx.Edges.Add(string.IsNullOrEmpty(pname)
                        ? $"{inId} --> {id}"
                        : $"{inId} -->|{Esc(pname!)}| {id}");
                }
                return id;
            }

            case TcGraphNodeKind.Assign:
            {
                var rv = node.RValue != null ? RenderNode(node.RValue, ctx) : null;
                string? first = null;
                foreach (var t in node.AssignTargets)
                {
                    var tid = OperandNode(ctx, t, false);
                    first ??= tid;
                    if (rv != null) ctx.Edges.Add($"{rv} --> {tid}");
                }
                return rv ?? first ?? ctx.NewId("nil");
            }

            default:
                return ctx.NewId("nil");
        }
    }

    private static string OperandNode(Ctx ctx, string name, bool negated)
    {
        if (string.IsNullOrEmpty(name)) name = "?";
        var key = (negated ? "¬" : "") + name;
        if (ctx.Operands.TryGetValue(key, out var existing)) return existing;
        var id = ctx.NewId("op");
        ctx.Nodes.Add($"{id}([\"{Esc((negated ? "NOT " : "") + name)}\"])");  // stadium = variable/literal
        ctx.Operands[key] = id;
        return id;
    }

    /// <summary>Escape dynamic text for use inside a Mermaid <c>"..."</c> label
    /// (characters that would otherwise break the parser become HTML entities).</summary>
    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c switch
            {
                '"' => "#quot;",
                '(' => "#40;",
                ')' => "#41;",
                '[' => "#91;",
                ']' => "#93;",
                '{' => "#123;",
                '}' => "#125;",
                '<' => "#lt;",
                '>' => "#gt;",
                '|' => "#124;",
                ';' => "#59;",
                _   => c.ToString(),
            });
        return sb.ToString();
    }
}
