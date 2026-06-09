using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Reconciles the software IO view (PLC variables with AT bindings) with the parsed
/// hardware topology (terminals + channels + their PLC-symbol links) into one unified
/// IO map. Produces three row kinds: <c>linked</c> (variable ↔ channel both known),
/// <c>software-only</c> (an AT-bound variable with no hardware link), and
/// <c>unused-channel</c> (a hardware channel with no PLC link). Every value comes from a
/// parsed source; missing data is left blank — nothing is inferred.
/// </summary>
public static class UnifiedIoReconciler
{
    public static List<UnifiedIoRow> Build(TcProject project)
    {
        var rows = new List<UnifiedIoRow>();

        // Symbol -> declaration (datatype, comment, AT binding) over ALL variables, so a
        // hardware-linked variable resolves even when it carries no AT binding. Keyed by both
        // the qualified "GVL.var" and the bare "var".
        var decls = new Dictionary<string, TcVariable>(StringComparer.OrdinalIgnoreCase);
        foreach (var gvl in project.GVLs.Values)
            foreach (var v in gvl.Variables)
            {
                decls.TryAdd($"{gvl.Name}.{v.Name}", v);
                decls.TryAdd(v.Name, v);
            }
        foreach (var pou in project.POUs.Values)
            foreach (var v in (pou.AllVariables.Count > 0 ? pou.AllVariables : pou.Variables))
            {
                decls.TryAdd($"{pou.Name}.{v.Name}", v);
                decls.TryAdd(v.Name, v);
            }

        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Suppress exact-duplicate linked rows that arise when a terminal exposes the same signal in
        // two alternative PDOs (e.g. an encoder's "Counter value" in Standard + Compact mappings).
        var seenLinked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Walk hardware channels: linked rows + unused-channel rows.
        foreach (var box in project.HardwareBoxes)
        {
            foreach (var ch in box.Channels)
            {
                if (ch.LinkedPlcSymbol.Length > 0)
                {
                    if (!seenLinked.Add($"{ch.LinkedPlcSymbol}␟{box.BoxName}␟{ch.ChannelName}␟{ch.SignalName}"))
                        continue;
                    var sw = Resolve(decls, ch.LinkedPlcSymbol);
                    rows.Add(new UnifiedIoRow
                    {
                        RowKind = "linked",
                        PlcVariable = ch.LinkedPlcSymbol,
                        DataType = sw?.DataType ?? ch.DataType,
                        Address = sw?.AtBinding ?? "",
                        Direction = sw is not null ? DirLabel(sw.AtBinding) : DirLabel(ch.Direction),
                        TerminalSku = box.Sku,
                        BoxOrSlave = box.BoxName,
                        Channel = ch.ChannelName,
                        ChannelSignal = ch.SignalName,
                        CoeIndex = ch.CoeIndex,
                        Comment = (sw?.Comment ?? "").Trim(),
                        Provenance = box.SourceFile,
                    });
                    consumed.Add(ch.LinkedPlcSymbol);
                    consumed.Add(LastSegment(ch.LinkedPlcSymbol));
                }
                else if (IsDigitalSpare(ch))
                {
                    rows.Add(new UnifiedIoRow
                    {
                        RowKind = "unused-channel",
                        PlcVariable = "",
                        DataType = ch.DataType,
                        Address = "",
                        Direction = DirLabel(ch.Direction),
                        TerminalSku = box.Sku,
                        BoxOrSlave = box.BoxName,
                        Channel = ch.ChannelName,
                        ChannelSignal = ch.SignalName,
                        CoeIndex = ch.CoeIndex,
                        Comment = "",
                        Provenance = box.IsSafety
                            ? $"{box.SourceFile} (safety / FSOE-mapped)"
                            : box.SourceFile,
                    });
                }
            }
        }

        // 2) Software-only rows: AT-bound variables never matched by a hardware link.
        foreach (var io in project.AllIoMappings)
        {
            var qualified = string.IsNullOrEmpty(io.SourceGvl)
                ? (string.IsNullOrEmpty(io.SourcePou) ? io.VariableName : $"{io.SourcePou}.{io.VariableName}")
                : $"{io.SourceGvl}.{io.VariableName}";

            if (consumed.Contains(qualified) || consumed.Contains(io.VariableName))
                continue;

            rows.Add(new UnifiedIoRow
            {
                RowKind = "software-only",
                PlcVariable = qualified,
                DataType = io.DataType,
                Address = io.AtBinding,
                Direction = io.Direction.ToString(),
                TerminalSku = "",
                BoxOrSlave = "",
                Channel = "",
                ChannelSignal = "",
                CoeIndex = "",
                Comment = (io.Comment ?? "").Trim(),
                Provenance = "PLC declaration (no hardware link)",
            });
        }

        return rows;
    }

    /// <summary>A genuine spare wireable port: a digital input/output bit with no PLC link.
    /// Analog and drive PDOs expose multiple alternative/diagnostic entries per physical channel,
    /// so listing their unmapped entries would misrepresent the port count — those are shown via
    /// the terminal inventory instead, and appear here only when actually linked to a variable.</summary>
    private static bool IsDigitalSpare(HardwareChannel ch)
    {
        if (!ch.IsPrimarySignal) return false;
        bool digitalSignal = ch.SignalName.Equals("Input", StringComparison.OrdinalIgnoreCase)
            || ch.SignalName.Equals("Output", StringComparison.OrdinalIgnoreCase);
        bool digitalType = ch.DataType.Equals("BIT", StringComparison.OrdinalIgnoreCase)
            || ch.DataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase);
        return digitalSignal && digitalType;
    }

    private static TcVariable? Resolve(Dictionary<string, TcVariable> decls, string symbol)
    {
        if (decls.TryGetValue(symbol, out var v)) return v;
        return decls.TryGetValue(LastSegment(symbol), out v) ? v : null;
    }

    private static string LastSegment(string symbol)
    {
        var i = symbol.LastIndexOf('.');
        return i >= 0 ? symbol[(i + 1)..] : symbol;
    }

    private static string DirLabel(ChannelDirection d) => d switch
    {
        ChannelDirection.Input => "Input",
        ChannelDirection.Output => "Output",
        _ => "",
    };

    private static string DirLabel(string? atBinding)
    {
        if (string.IsNullOrWhiteSpace(atBinding)) return "";
        var t = atBinding.TrimStart('%');
        if (t.StartsWith("I", StringComparison.OrdinalIgnoreCase)) return "Input";
        if (t.StartsWith("Q", StringComparison.OrdinalIgnoreCase)) return "Output";
        if (t.StartsWith("M", StringComparison.OrdinalIgnoreCase)) return "Memory";
        return "";
    }
}
