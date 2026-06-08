using System.Text;
using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Builds a project CALL GRAPH (who-calls-whom across POUs) and renders it as a
/// Mermaid directed graph. Resolves: function calls to project FUNCTIONs, FB
/// instance calls (<c>inst(...)</c> / <c>inst.Method(...)</c>) to the instance's
/// FB type (local VARs + global GVL instances), <c>SUPER^.M()</c> to the base
/// class, and graphical (FBD) box instances. Library/built-in calls (TON, MC_*,
/// TO_INT, …) are excluded by default so the graph is about the user's own code.
/// Nodes are coloured by POU kind (FB / FUNCTION / PROGRAM / INTERFACE).
/// </summary>
public static class CallGraphRenderer
{
    public static string ToMermaid(TcProject project, string? root = null, bool includeLibraries = false, int maxEdges = 300)
    {
        var edges = BuildEdges(project, includeLibraries);
        if (!string.IsNullOrWhiteSpace(root)) edges = FilterFromRoot(edges, root!);
        return Render(project, edges, root, maxEdges);
    }

    private static Dictionary<(string From, string To), int> BuildEdges(TcProject project, bool includeLib)
    {
        var pous = project.POUs.Values.ToList();
        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pous) nameMap[p.Name] = p.Name;
        var funcs = new HashSet<string>(pous.Where(p => p.PouType == PouType.Function).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        var globalInst = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in project.GVLs.Values)
            foreach (var v in g.Variables)
                if (nameMap.TryGetValue(v.DataType, out var t)) globalInst[v.Name] = t;

        var edges = new Dictionary<(string, string), int>();
        void AddEdge(string from, string to)
        {
            if (string.IsNullOrEmpty(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
            var k = (from, to);
            edges[k] = edges.GetValueOrDefault(k) + 1;
        }

        var callRe = new Regex(@"([A-Za-z_]\w*(?:\s*\^?\s*\.\s*[A-Za-z_]\w*)*)\s*\(", RegexOptions.Compiled);

        foreach (var caller in pous)
        {
            var inst = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in caller.AllVariables.Count > 0 ? caller.AllVariables : caller.Variables)
                if (nameMap.TryGetValue(v.DataType, out var t)) inst[v.Name] = t;

            string Resolve(string chain)
            {
                var segs = chain.Replace("^", "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (segs.Length == 0) return "";
                var first = segs[0];
                if (first.Equals("THIS", StringComparison.OrdinalIgnoreCase)) return "";
                if (first.Equals("SUPER", StringComparison.OrdinalIgnoreCase)) return caller.ExtendsType ?? "";
                if (segs.Length == 1)
                {
                    if (funcs.Contains(first)) return nameMap[first];
                    if (inst.TryGetValue(first, out var ft)) return ft;
                    if (globalInst.TryGetValue(first, out var gft)) return gft;
                    return includeLib ? first : "";
                }
                if (inst.TryGetValue(first, out var t)) return t;
                if (globalInst.TryGetValue(first, out var gt)) return gt;
                // GVL-qualified instance: GVL.inst.Method -> try the segment before the (method) tail
                if (segs.Length >= 3 && globalInst.TryGetValue(segs[1], out var g2)) return g2;
                return "";
            }

            void Scan(string body)
            {
                if (string.IsNullOrWhiteSpace(body)) return;
                foreach (Match m in callRe.Matches(Strip(body)))
                {
                    var target = Resolve(m.Groups[1].Value);
                    if (string.IsNullOrEmpty(target)) continue;
                    if (!includeLib && !nameMap.ContainsKey(target)) continue;
                    AddEdge(caller.Name, nameMap.GetValueOrDefault(target, target));
                }
            }

            Scan(caller.RawImplementation);
            foreach (var mth in caller.Methods) Scan(mth.Body);
            foreach (var act in caller.Actions) Scan(act.Body);
            foreach (var prop in caller.Properties) { Scan(prop.GetBody ?? ""); Scan(prop.SetBody ?? ""); }

            void Graphical(TcGraphicalImpl? g)
            {
                if (g == null) return;
                void Walk(TcGraphNode n)
                {
                    if (n.Kind == TcGraphNodeKind.Box)
                    {
                        if (!string.IsNullOrEmpty(n.InstanceName) && inst.TryGetValue(n.InstanceName!, out var it)) AddEdge(caller.Name, it);
                        else if (!string.IsNullOrEmpty(n.BoxType) && nameMap.TryGetValue(n.BoxType!, out var bt)) AddEdge(caller.Name, bt);
                    }
                    if (n.RValue != null) Walk(n.RValue);
                    foreach (var i in n.Inputs) Walk(i);
                }
                foreach (var net in g.Networks) foreach (var node in net.Items) Walk(node);
            }
            Graphical(caller.Graphical);
            foreach (var act in caller.Actions) Graphical(act.Graphical);
        }
        return edges;
    }

    private static Dictionary<(string From, string To), int> FilterFromRoot(Dictionary<(string From, string To), int> edges, string root)
    {
        var adj = edges.Keys.GroupBy(k => k.From, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.Select(k => k.To).ToList(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var q = new Queue<string>(); q.Enqueue(root);
        while (q.Count > 0) { var c = q.Dequeue(); if (adj.TryGetValue(c, out var outs)) foreach (var o in outs) if (seen.Add(o)) q.Enqueue(o); }
        return edges.Where(e => seen.Contains(e.Key.From) && seen.Contains(e.Key.To)).ToDictionary(e => e.Key, e => e.Value);
    }

    private static string Render(TcProject project, Dictionary<(string From, string To), int> edges, string? root, int maxEdges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("graph LR");
        var title = string.IsNullOrEmpty(root) ? $"Call graph — {project.Name}" : $"Call graph from {root}";
        sb.AppendLine($"  %% {title} ({edges.Count} edge(s))");
        if (edges.Count == 0) { sb.AppendLine("  empty[\"(no project-internal calls resolved)\"]"); return sb.ToString().TrimEnd(); }

        var ordered = edges.OrderByDescending(e => e.Value).Take(maxEdges).ToList();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Id(string name) { if (!ids.TryGetValue(name, out var id)) { id = "p" + ids.Count; ids[name] = id; } return id; }
        var pouType = project.POUs.Values.ToDictionary(p => p.Name, p => p.PouType, StringComparer.OrdinalIgnoreCase);

        var nodes = ordered.SelectMany(e => new[] { e.Key.From, e.Key.To }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var nme in nodes) sb.AppendLine($"  {Id(nme)}[\"{Esc(nme)}\"]");
        foreach (var nme in nodes)
        {
            var cls = pouType.GetValueOrDefault(nme, PouType.FunctionBlock) switch
            {
                PouType.Function  => "fn",
                PouType.Program   => "prog",
                PouType.Interface => "itf",
                _                 => "fb",
            };
            sb.AppendLine($"  class {Id(nme)} {cls};");
        }
        foreach (var e in ordered)
            sb.AppendLine($"  {Id(e.Key.From)} -->{(e.Value > 1 ? $"|{e.Value}|" : "")} {Id(e.Key.To)}");
        if (edges.Count > maxEdges) sb.AppendLine($"  %% showing top {maxEdges} of {edges.Count} edges");

        sb.AppendLine("  classDef fb fill:#1f3a5f,stroke:#3a7bd5,color:#fff;");
        sb.AppendLine("  classDef fn fill:#3a5f1f,stroke:#5fd53a,color:#fff;");
        sb.AppendLine("  classDef prog fill:#5f1f3a,stroke:#d53a7b,color:#fff;");
        sb.AppendLine("  classDef itf fill:#3a3a3a,stroke:#888,color:#fff;");
        return sb.ToString().TrimEnd();
    }

    private static string Strip(string s)
    {
        s = Regex.Replace(s, @"\(\*.*?\*\)", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"//[^\n]*", " ");
        s = Regex.Replace(s, @"'(?:[^']|'')*'", "''");
        return s;
    }

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c switch
            {
                '"' => "#quot;", '(' => "#40;", ')' => "#41;", '[' => "#91;", ']' => "#93;",
                '{' => "#123;", '}' => "#125;", '<' => "#lt;", '>' => "#gt;", '|' => "#124;", ';' => "#59;",
                _ => c.ToString(),
            });
        return sb.ToString();
    }
}
