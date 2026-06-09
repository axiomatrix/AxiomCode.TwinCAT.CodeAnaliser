namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

/// <summary>Physical signal direction of an EtherCAT process-data channel, derived
/// from the CoE index range (0x6xxx = TxPDO/input, 0x7xxx = RxPDO/output) or the
/// entry name — never guessed.</summary>
public enum ChannelDirection
{
    Input,
    Output,
    Unknown,
}

/// <summary>
/// One process-data channel (a single PDO entry) on an EtherCAT terminal, parsed
/// verbatim from the project's <c>.xti</c> hardware description. Carries the real
/// terminal-side signal name, direction, datatype and CoE address, plus the PLC
/// symbol it is linked to (if the project's IO mapping links it).
/// </summary>
public class HardwareChannel
{
    /// <summary>PDO name, e.g. "Channel 1", "AI Standard Channel 1", "DRV Statusword".</summary>
    public string ChannelName { get; set; } = "";

    /// <summary>PDO entry name, e.g. "Input", "Output", "Value", "Statusword".</summary>
    public string SignalName { get; set; } = "";

    public ChannelDirection Direction { get; set; } = ChannelDirection.Unknown;

    /// <summary>CoE entry datatype verbatim, e.g. "BIT", "INT", "UDINT".</summary>
    public string DataType { get; set; } = "";

    public int? BitLength { get; set; }

    /// <summary>CoE object index verbatim, e.g. "#x6000".</summary>
    public string CoeIndex { get; set; } = "";

    /// <summary>CoE sub-index verbatim, e.g. "#x01".</summary>
    public string CoeSubIndex { get; set; } = "";

    /// <summary>Owning PDO index verbatim, e.g. "#x1a00".</summary>
    public string PdoIndex { get; set; } = "";

    /// <summary>Declaration order within the box (for deterministic output).</summary>
    public int Order { get; set; }

    /// <summary>True for an allocatable signal (Input/Output/Value/control-or-status word) —
    /// false for diagnostic/padding/sync entries that should not appear as IO-allocation rows.</summary>
    public bool IsPrimarySignal { get; set; }

    /// <summary>PLC symbol this channel is mapped to (verbatim from the IO <c>&lt;Link&gt;</c>),
    /// e.g. "GVL_IO.bCallButton_F0". Empty when the channel is unmapped.</summary>
    public string LinkedPlcSymbol { get; set; } = "";

    /// <summary>The link owner (e.g. PLC task instance or NC axis), verbatim. Empty when unmapped.</summary>
    public string LinkOwner { get; set; } = "";
}

/// <summary>
/// An EtherCAT box (terminal / coupler / drive) parsed from an <c>.xti</c> file:
/// SKU, type description and its ordered channels. Couplers and end terminals carry
/// no channels; safety terminals are flagged. Everything here comes from source XML.
/// </summary>
public class HardwareBox
{
    /// <summary>Box designator + type as it appears in the project, e.g. "DC1 (EL1859)".</summary>
    public string BoxName { get; set; } = "";

    /// <summary>Terminal SKU, e.g. "EL1859" (from EtherCAT @Desc, else the leading token of @Type).</summary>
    public string Sku { get; set; } = "";

    /// <summary>Full EtherCAT type string, e.g. "EL1859 8Ch. Dig. Input 24V, 3ms, 8Ch. Dig. Output...".</summary>
    public string TypeDescription { get; set; } = "";

    public string OrderCode { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string RevisionNo { get; set; } = "";

    public bool IsCoupler { get; set; }
    public bool IsEndTerminal { get; set; }
    public bool IsSafety { get; set; }

    public List<HardwareChannel> Channels { get; set; } = new();

    /// <summary>Relative path(s) of the .xti file(s) this box was parsed from.</summary>
    public string SourceFile { get; set; } = "";
}

/// <summary>
/// One row of the unified IO map — the reconciled join of the software IO variable
/// and the physical hardware terminal/port. Every populated field traces to a parsed
/// source artifact; any field with no source is left empty (never inferred).
/// </summary>
public class UnifiedIoRow
{
    /// <summary>PLC symbol, e.g. "GVL_IO.bCallButton_F0". Empty on an unused-channel row.</summary>
    public string PlcVariable { get; set; } = "";

    public string DataType { get; set; } = "";

    /// <summary>AT binding from the PLC declaration, e.g. "%I*" / "%Q*". Empty when not AT-bound.</summary>
    public string Address { get; set; } = "";

    public string Direction { get; set; } = "";

    /// <summary>Terminal SKU, e.g. "EL1859". Empty on a software-only row with no hardware link.</summary>
    public string TerminalSku { get; set; } = "";

    /// <summary>Box designator, e.g. "DC1 (EL1859)".</summary>
    public string BoxOrSlave { get; set; } = "";

    /// <summary>Channel name, e.g. "Channel 1".</summary>
    public string Channel { get; set; } = "";

    /// <summary>Channel signal, e.g. "Input" / "Output" / "Value".</summary>
    public string ChannelSignal { get; set; } = "";

    public string CoeIndex { get; set; } = "";

    /// <summary>Engineer-facing comment from the PLC declaration (slave path / pin / EPLAN tag).</summary>
    public string Comment { get; set; } = "";

    /// <summary>"linked" | "software-only" | "unused-channel".</summary>
    public string RowKind { get; set; } = "";

    /// <summary>Source file(s) / note explaining where this row came from.</summary>
    public string Provenance { get; set; } = "";
}
