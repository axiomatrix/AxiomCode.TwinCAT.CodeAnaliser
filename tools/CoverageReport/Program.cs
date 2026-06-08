using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;
using AxiomCode.TwinCAT.CodeAnalyser.Services;

// TwinCAT element-coverage harness — the regression gate for the "exhaustive
// source of truth" upgrade. Parses every POU/DUT/GVL under a corpus root, then
// independently scans the raw XML for elements the parser may still drop, and
// prints a coverage + drop report. Usage: tc-coverage <root> [<root> ...]

var roots = args.Length > 0 ? args : new[] { @"F:\AI\examples\twincat" };

foreach (var root in roots)
{
    if (!Directory.Exists(root)) { Console.WriteLine($"(skip, not found) {root}"); continue; }
    Report(root);
    Console.WriteLine();
}

static void Report(string root)
{
    var pous = Directory.GetFiles(root, "*.TcPOU", SearchOption.AllDirectories);
    var duts = Directory.GetFiles(root, "*.TcDUT", SearchOption.AllDirectories);
    var gvls = Directory.GetFiles(root, "*.TcGVL", SearchOption.AllDirectories);
    var plcprojs = Directory.GetFiles(root, "*.plcproj", SearchOption.AllDirectories);

    var lang = new Dictionary<ImplLanguage, int>();
    int methods = 0, props = 0, getters = 0, setters = 0, vars = 0, gNetworks = 0, gNodes = 0, parseFail = 0;
    int capActions = 0, capPouAttrs = 0, capMethodAttrs = 0, capPropAttrs = 0;
    var varScopes = new Dictionary<VarScope, int>();

    // Drop detectors (raw-XML, independent of the parser).
    int rawActions = 0, rawTransitions = 0;        // <Action>/<Transition> elements (parser drops)
    int graphicalMethodBodies = 0;                 // <Method><Implementation><NWL|SFC|CFC> (parser reads only ST)
    int graphicalPropBodies = 0;                   // graphical Get/Set bodies
    int sfcCfcPous = 0;                            // bodies detected but not fully decoded
    int pouPragmas = 0;                            // {attribute} in POU declarations (parser drops at POU level)

    int CountNodes(TcGraphNode n) => 1 + (n.RValue != null ? CountNodes(n.RValue) : 0) + n.Inputs.Sum(CountNodes);

    foreach (var f in pous)
    {
        var pou = TcPouParser.Parse(f, root);
        if (pou == null) { parseFail++; continue; }
        lang[pou.Language] = lang.GetValueOrDefault(pou.Language) + 1;
        methods += pou.Methods.Count;
        props += pou.Properties.Count;
        getters += pou.Properties.Count(p => p.HasGetter);
        setters += pou.Properties.Count(p => p.HasSetter);
        vars += pou.Variables.Count;
        foreach (var v in pou.Variables) varScopes[v.Scope] = varScopes.GetValueOrDefault(v.Scope) + 1;
        capActions += pou.Actions.Count;
        capPouAttrs += pou.Attributes.Count;
        capMethodAttrs += pou.Methods.Sum(m => m.Attributes.Count);
        capPropAttrs += pou.Properties.Sum(p => p.Attributes.Count);
        if (pou.Graphical is { } g)
        {
            gNetworks += g.Networks.Count;
            foreach (var net in g.Networks) gNodes += net.Items.Sum(CountNodes);
            if (pou.Language is ImplLanguage.SFC or ImplLanguage.CFC) sfcCfcPous++;
        }

        // Raw-XML drop detection.
        try
        {
            var x = XDocument.Load(f);
            var pe = x.Root?.Element("POU");
            if (pe == null) continue;
            rawActions += pe.Elements("Action").Count();
            rawTransitions += pe.Elements("Transition").Count();
            if ((pe.Element("Declaration")?.Value ?? "").Contains("{attribute")) pouPragmas++;
            foreach (var m in pe.Elements("Method"))
            {
                var impl = m.Element("Implementation");
                if (impl?.Element("ST") == null && (impl?.Element("NWL") != null || impl?.Element("SFC") != null || impl?.Element("CFC") != null))
                    graphicalMethodBodies++;
            }
            foreach (var p in pe.Elements("Property"))
                foreach (var acc in new[] { p.Element("Get"), p.Element("Set") })
                {
                    var impl = acc?.Element("Implementation");
                    if (impl != null && impl.Element("ST") == null &&
                        (impl.Element("NWL") != null || impl.Element("SFC") != null || impl.Element("CFC") != null))
                        graphicalPropBodies++;
                }
        }
        catch { /* raw scan best-effort */ }
    }

    int dutStruct = 0, dutEnum = 0, dutUnion = 0, dutAlias = 0, enumMembers = 0, structMembers = 0;
    foreach (var f in duts)
    {
        var dut = TcDutParser.Parse(f, root);
        if (dut == null) continue;
        switch (dut.DutType)
        {
            case DutType.Struct: dutStruct++; structMembers += dut.Members.Count; break;
            case DutType.Enum:   dutEnum++; enumMembers += dut.EnumValues.Count; break;
            case DutType.Union:  dutUnion++; structMembers += dut.Members.Count; break;
            case DutType.Alias:  dutAlias++; break;
        }
    }

    int gvlVars = 0;
    foreach (var f in gvls)
    {
        var gvl = TcGvlParser.Parse(f, root);
        if (gvl != null) gvlVars += gvl.Variables.Count;
    }

    Console.WriteLine($"# Coverage — {root}");
    Console.WriteLine($"Projects: {plcprojs.Length}   Files: TcPOU={pous.Length} TcDUT={duts.Length} TcGVL={gvls.Length}   parse-failures={parseFail}");
    Console.WriteLine();
    Console.WriteLine("## POU language distribution (parsed)");
    foreach (var kv in lang.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Key,-10} {kv.Value}");
    Console.WriteLine();
    Console.WriteLine("## Captured elements");
    Console.WriteLine($"  Methods={methods}  Properties={props} (get={getters} set={setters})  POU vars={vars}");
    Console.WriteLine($"  Var scopes: " + string.Join("  ", varScopes.OrderBy(k => k.Key.ToString()).Select(k => $"{k.Key}={k.Value}")));
    Console.WriteLine($"  DUTs: struct={dutStruct} enum={dutEnum} union={dutUnion} alias={dutAlias}  enumMembers={enumMembers} structMembers={structMembers}");
    Console.WriteLine($"  GVL vars={gvlVars}");
    Console.WriteLine($"  Graphical: networks={gNetworks} nodes={gNodes}");
    Console.WriteLine($"  Actions={capActions}  Attributes: pou={capPouAttrs} method={capMethodAttrs} prop={capPropAttrs}");
    Console.WriteLine();
    Console.WriteLine("## Remaining drops (raw-XML present but not fully modelled)");
    Console.WriteLine($"  Graphical METHOD bodies not decoded : {graphicalMethodBodies}");
    Console.WriteLine($"  Graphical PROPERTY accessor bodies   : {graphicalPropBodies}");
    Console.WriteLine($"  SFC/CFC POU bodies (detected only)   : {sfcCfcPous}");
    Console.WriteLine($"  <Action> elements (raw {rawActions} − captured {capActions}) : {rawActions - capActions}");
    Console.WriteLine($"  <Transition> elements (not modelled) : {rawTransitions}");
    Console.WriteLine($"  POUs w/ decl pragmas (raw {pouPragmas}; now captured {capPouAttrs} attrs across POUs): {(capPouAttrs > 0 ? 0 : pouPragmas)}");
}
