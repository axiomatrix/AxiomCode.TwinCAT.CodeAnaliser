using System.Text;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Renders a Structured Text (ST) body as a Mermaid <b>flowchart</b> (control-flow
/// graph) — the "program flow" view. IF/ELSIF/ELSE and CASE become decision nodes;
/// FOR/WHILE/REPEAT become loop-back structures; RETURN/EXIT are terminals; runs of
/// simple statements collapse into process boxes.
///
/// <para>Uses a small purpose-built ST tokenizer + recursive-descent control-flow
/// parser (comments and string literals are handled so keywords inside them don't
/// fool the scan). It models control STRUCTURE faithfully; expression text is kept
/// verbatim (truncated) for the node labels rather than fully parsed.</para>
/// </summary>
public static class StFlowchartRenderer
{
    public static string ToMermaid(TcPou pou)  => ToMermaid(pou.RawImplementation, pou.Name);
    public static string ToMermaid(TcMethod m) => ToMermaid(m.Body, m.Name);

    public static string ToMermaid(string st, string title = "")
    {
        var toks = Lex(st ?? "");
        var body = new Parser(toks).ParseBlock();
        return new Emitter(title).Render(body);
    }

    // ── Tokenizer ─────────────────────────────────────────────────────────────
    private enum TT { Word, Sym, Lit, Eof }
    private readonly record struct Tok(TT Type, string Text, bool Kw);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF","THEN","ELSIF","ELSE","END_IF","CASE","OF","END_CASE","FOR","TO","BY","DO",
        "END_FOR","WHILE","END_WHILE","REPEAT","UNTIL","END_REPEAT","RETURN","EXIT","CONTINUE",
    };

    private static List<Tok> Lex(string s)
    {
        var toks = new List<Tok>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            // line comment
            if (c == '/' && i + 1 < n && s[i + 1] == '/') { while (i < n && s[i] != '\n') i++; continue; }
            // block comment (nestable)
            if (c == '(' && i + 1 < n && s[i + 1] == '*')
            {
                int depth = 1; i += 2;
                while (i < n && depth > 0)
                {
                    if (s[i] == '(' && i + 1 < n && s[i + 1] == '*') { depth++; i += 2; }
                    else if (s[i] == '*' && i + 1 < n && s[i + 1] == ')') { depth--; i += 2; }
                    else i++;
                }
                continue;
            }
            // string literal '...'  ('' = embedded quote)
            if (c == '\'')
            {
                var sb = new StringBuilder(); i++;
                while (i < n)
                {
                    if (s[i] == '\'') { if (i + 1 < n && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; } i++; break; }
                    sb.Append(s[i++]);
                }
                toks.Add(new Tok(TT.Lit, "'" + sb + "'", false));
                continue;
            }
            // identifier / keyword
            if (char.IsLetter(c) || c == '_')
            {
                int st = i; while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                var w = s[st..i];
                toks.Add(new Tok(TT.Word, w, Keywords.Contains(w)));
                continue;
            }
            // number / typed literal (16#FF, T#1s, 3.14)
            if (char.IsDigit(c))
            {
                int st = i; while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] is '.' or '#' or '_')) i++;
                toks.Add(new Tok(TT.Lit, s[st..i], false));
                continue;
            }
            // multi-char symbols
            if (i + 1 < n)
            {
                var two = s.Substring(i, 2);
                if (two is ":=" or "<=" or ">=" or "<>") { toks.Add(new Tok(TT.Sym, two, false)); i += 2; continue; }
            }
            toks.Add(new Tok(TT.Sym, c.ToString(), false)); i++;
        }
        toks.Add(new Tok(TT.Eof, "", false));
        return toks;
    }

    // ── AST ───────────────────────────────────────────────────────────────────
    private abstract class Stmt;
    private sealed class Seq : Stmt { public readonly List<Stmt> Items = new(); }
    private sealed class Simple : Stmt { public string Text = ""; }
    private sealed class IfS : Stmt { public readonly List<(string Cond, Seq Body)> Branches = new(); public Seq? Else; }
    private sealed class CaseS : Stmt { public string Selector = ""; public readonly List<(string Labels, Seq Body)> Cases = new(); public Seq? Else; }
    private sealed class LoopS : Stmt { public string Kind = ""; public string Header = ""; public Seq Body = new(); public string Until = ""; }
    private sealed class JumpS : Stmt { public string Kind = ""; }

    // ── Parser ────────────────────────────────────────────────────────────────
    private sealed class Parser(List<Tok> toks)
    {
        private int _p;
        private Tok Peek => toks[_p];
        private bool Eof => Peek.Type == TT.Eof;
        private Tok Next() => toks[_p++];
        private bool PeekKw(string kw) => Peek.Kw && string.Equals(Peek.Text, kw, StringComparison.OrdinalIgnoreCase);
        private bool PeekKwAny(params string[] kws) => Peek.Kw && kws.Any(k => string.Equals(Peek.Text, k, StringComparison.OrdinalIgnoreCase));
        private void Eat(string kw) { if (PeekKw(kw)) _p++; }
        private void EatSemi() { if (Peek.Type == TT.Sym && Peek.Text == ";") _p++; }

        public Seq ParseBlock(params string[] stop)
        {
            var seq = new Seq();
            var simple = new StringBuilder();
            void Flush() { if (simple.Length > 0) { seq.Items.Add(new Simple { Text = simple.ToString().Trim() }); simple.Clear(); } }

            while (!Eof)
            {
                if (Peek.Kw && stop.Any(s => string.Equals(Peek.Text, s, StringComparison.OrdinalIgnoreCase))) break;
                if (Peek.Kw)
                {
                    switch (Peek.Text.ToUpperInvariant())
                    {
                        case "IF":     Flush(); seq.Items.Add(ParseIf());   continue;
                        case "CASE":   Flush(); seq.Items.Add(ParseCase()); continue;
                        case "FOR": case "WHILE": case "REPEAT": Flush(); seq.Items.Add(ParseLoop()); continue;
                        case "RETURN": case "EXIT": case "CONTINUE":
                            Flush(); var k = Next().Text.ToUpperInvariant(); EatSemi();
                            seq.Items.Add(new JumpS { Kind = k }); continue;
                    }
                }
                var s = CollectStatement();
                if (!string.IsNullOrWhiteSpace(s)) { if (simple.Length > 0) simple.Append('\n'); simple.Append(s); }
            }
            Flush();
            return seq;
        }

        // Collect tokens up to (and consuming) the next depth-0 ';'. Returns the text.
        private string CollectStatement()
        {
            var sb = new StringBuilder(); int depth = 0;
            while (!Eof)
            {
                var t = Peek;
                if (t.Kw && depth == 0 && IsBlockKw(t.Text)) break;   // safety: don't run past a block keyword
                _p++;
                if (t.Type == TT.Sym)
                {
                    if (t.Text is "(" or "[") depth++;
                    else if (t.Text is ")" or "]") depth = Math.Max(0, depth - 1);
                    else if (t.Text == ";" && depth == 0) break;
                }
                Append(sb, t);
            }
            return sb.ToString().Trim().TrimEnd(';').Trim();
        }

        private string CollectUntilKw(string kw)
        {
            var sb = new StringBuilder();
            while (!Eof && !PeekKw(kw)) Append(sb, Next());
            Eat(kw);
            return sb.ToString().Trim();
        }

        private IfS ParseIf()
        {
            Eat("IF");
            var node = new IfS();
            node.Branches.Add((CollectUntilKw("THEN"), ParseBlock("ELSIF", "ELSE", "END_IF")));
            while (PeekKw("ELSIF")) { Eat("ELSIF"); node.Branches.Add((CollectUntilKw("THEN"), ParseBlock("ELSIF", "ELSE", "END_IF"))); }
            if (PeekKw("ELSE")) { Eat("ELSE"); node.Else = ParseBlock("END_IF"); }
            Eat("END_IF"); EatSemi();
            return node;
        }

        private LoopS ParseLoop()
        {
            var kind = Next().Text.ToUpperInvariant();
            var loop = new LoopS { Kind = kind };
            if (kind == "REPEAT")
            {
                loop.Body = ParseBlock("UNTIL");
                Eat("UNTIL");
                loop.Until = CollectUntilKw("END_REPEAT");
                EatSemi();
            }
            else
            {
                loop.Header = CollectUntilKw("DO");
                loop.Body = ParseBlock("END_FOR", "END_WHILE");
                if (PeekKw("END_FOR")) Eat("END_FOR"); else Eat("END_WHILE");
                EatSemi();
            }
            return loop;
        }

        private CaseS ParseCase()
        {
            Eat("CASE");
            var node = new CaseS { Selector = CollectUntilKw("OF") };
            while (!Eof && !PeekKwAny("ELSE", "END_CASE"))
            {
                var labels = CollectUntilSymColon();
                node.Cases.Add((labels, ParseCaseBody()));
            }
            if (PeekKw("ELSE")) { Eat("ELSE"); node.Else = ParseBlock("END_CASE"); }
            Eat("END_CASE"); EatSemi();
            return node;
        }

        // Collect a case label list up to the bare ':' (not ':='); consume the ':'.
        private string CollectUntilSymColon()
        {
            var sb = new StringBuilder(); int depth = 0;
            while (!Eof)
            {
                var t = Peek;
                if (t.Type == TT.Sym)
                {
                    if (t.Text is "(" or "[") depth++;
                    else if (t.Text is ")" or "]") depth = Math.Max(0, depth - 1);
                    else if (t.Text == ":" && depth == 0) { _p++; break; }
                }
                Append(sb, Next());
            }
            return sb.ToString().Trim();
        }

        // Parse the body of one CASE entry: statements until the next label, ELSE, or END_CASE.
        private Seq ParseCaseBody()
        {
            var seq = new Seq();
            var simple = new StringBuilder();
            void Flush() { if (simple.Length > 0) { seq.Items.Add(new Simple { Text = simple.ToString().Trim() }); simple.Clear(); } }

            while (!Eof && !PeekKwAny("ELSE", "END_CASE"))
            {
                if (Peek.Kw)
                {
                    switch (Peek.Text.ToUpperInvariant())
                    {
                        case "IF":     Flush(); seq.Items.Add(ParseIf());   continue;
                        case "CASE":   Flush(); seq.Items.Add(ParseCase()); continue;
                        case "FOR": case "WHILE": case "REPEAT": Flush(); seq.Items.Add(ParseLoop()); continue;
                        case "RETURN": case "EXIT": case "CONTINUE":
                            Flush(); var k = Next().Text.ToUpperInvariant(); EatSemi();
                            seq.Items.Add(new JumpS { Kind = k }); continue;
                    }
                }
                if (NextDelimiterIsColon()) break;   // upcoming "<labels> :" begins the next case
                var s = CollectStatement();
                if (!string.IsNullOrWhiteSpace(s)) { if (simple.Length > 0) simple.Append('\n'); simple.Append(s); }
            }
            Flush();
            return seq;
        }

        // Lookahead: is the first depth-0 delimiter a bare ':' (label) rather than ';' (statement)?
        private bool NextDelimiterIsColon()
        {
            int p = _p, depth = 0;
            while (p < toks.Count)
            {
                var t = toks[p];
                if (t.Type == TT.Eof) return false;
                if (t.Kw && depth == 0 && IsBlockKw(t.Text)) return false;
                if (t.Type == TT.Sym)
                {
                    if (t.Text is "(" or "[") depth++;
                    else if (t.Text is ")" or "]") depth = Math.Max(0, depth - 1);
                    else if (depth == 0 && t.Text == ";") return false;
                    else if (depth == 0 && t.Text == ":") return true;
                    else if (depth == 0 && t.Text == ":=") return false;
                }
                p++;
            }
            return false;
        }

        private static bool IsBlockKw(string w) => w.ToUpperInvariant() is
            "IF" or "ELSIF" or "ELSE" or "END_IF" or "CASE" or "END_CASE" or "FOR" or "END_FOR" or
            "WHILE" or "END_WHILE" or "REPEAT" or "UNTIL" or "END_REPEAT";

        private static void Append(StringBuilder sb, Tok t)
        {
            if (sb.Length > 0) { var last = sb[^1]; var glue = !(t.Text is "." or "(" or ")" or "[" or "]" or "," or ";" or ":") && last is not '.' and not '('; if (glue) sb.Append(' '); }
            sb.Append(t.Text);
        }
    }

    // ── Emitter ───────────────────────────────────────────────────────────────
    private sealed class Emitter(string title)
    {
        private int _id;
        private readonly StringBuilder _sb = new();
        private string NewId() => "n" + _id++;

        public string Render(Seq root)
        {
            _sb.AppendLine("flowchart TD");
            if (!string.IsNullOrEmpty(title)) _sb.AppendLine($"  %% {title.Replace('\n', ' ')} — control flow");
            var start = NewId(); _sb.AppendLine($"  {start}([\"Start\"])");
            var end = Emit(root, start, null);
            if (end != null) { var e = NewId(); _sb.AppendLine($"  {e}([\"End\"])"); Edge(end, e, null); }
            return _sb.ToString().TrimEnd();
        }

        private void Edge(string a, string b, string? label)
            => _sb.AppendLine(label == null ? $"  {a} --> {b}" : $"  {a} -->|{Esc(label)}| {b}");

        private string? Emit(Stmt stmt, string prev, string? inLabel)
        {
            switch (stmt)
            {
                case Seq seq:
                {
                    string? cur = prev; var lbl = inLabel;
                    foreach (var s in seq.Items) { if (cur == null) break; cur = Emit(s, cur, lbl); lbl = null; }
                    return cur;
                }
                case Simple sp:
                {
                    var id = NewId();
                    _sb.AppendLine($"  {id}[\"{SimpleLabel(sp.Text)}\"]");
                    Edge(prev, id, inLabel);
                    return id;
                }
                case JumpS j:
                {
                    var id = NewId();
                    _sb.AppendLine($"  {id}([\"{Esc(j.Kind)}\"])");
                    Edge(prev, id, inLabel);
                    return j.Kind is "RETURN" or "EXIT" ? null : id;
                }
                case IfS iff:
                {
                    var merge = NewId(); _sb.AppendLine($"  {merge}(( ))");
                    string? cur = prev; var curLbl = inLabel;
                    foreach (var (cond, body) in iff.Branches)
                    {
                        var d = NewId(); _sb.AppendLine($"  {d}{{\"{Esc(Trunc(cond, 60))}\"}}");
                        Edge(cur!, d, curLbl);
                        var ex = Emit(body, d, "yes"); if (ex != null) Edge(ex, merge, null);
                        cur = d; curLbl = "no";
                    }
                    if (iff.Else != null) { var ex = Emit(iff.Else, cur!, curLbl); if (ex != null) Edge(ex, merge, null); }
                    else Edge(cur!, merge, curLbl);
                    return merge;
                }
                case CaseS cs:
                {
                    var merge = NewId(); _sb.AppendLine($"  {merge}(( ))");
                    var c = NewId(); _sb.AppendLine($"  {c}{{\"CASE {Esc(Trunc(cs.Selector, 40))}\"}}");
                    Edge(prev, c, inLabel);
                    foreach (var (labels, body) in cs.Cases)
                    {
                        var ex = Emit(body, c, Trunc(labels, 22));
                        if (ex != null) Edge(ex, merge, null);
                    }
                    if (cs.Else != null) { var ex = Emit(cs.Else, c, "else"); if (ex != null) Edge(ex, merge, null); }
                    return merge;
                }
                case LoopS lp when lp.Kind == "REPEAT":
                {
                    var entry = NewId(); _sb.AppendLine($"  {entry}[\"REPEAT\"]");
                    Edge(prev, entry, inLabel);
                    var ex = Emit(lp.Body, entry, null) ?? entry;
                    var u = NewId(); _sb.AppendLine($"  {u}{{\"UNTIL {Esc(Trunc(lp.Until, 50))}\"}}");
                    Edge(ex, u, null);
                    Edge(u, entry, "false");
                    return u;
                }
                case LoopS lp:
                {
                    var d = NewId();
                    var hdr = lp.Kind == "FOR" ? "FOR " + lp.Header : lp.Header;
                    _sb.AppendLine($"  {d}{{\"{Esc(Trunc(hdr, 55))}\"}}");
                    Edge(prev, d, inLabel);
                    var ex = Emit(lp.Body, d, "do");
                    if (ex != null) Edge(ex, d, null);
                    return d;
                }
                default: return prev;
            }
        }

        private static string SimpleLabel(string text)
        {
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var shown = lines.Take(4).Select(l => Esc(Trunc(l, 48)));
            return string.Join("<br>", shown) + (lines.Count > 4 ? "<br>…" : "");
        }

        private static string Trunc(string s, int max)
        {
            s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= max ? s : s[..(max - 1)] + "…";
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
}
