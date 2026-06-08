namespace AxiomCode.TwinCAT.CodeAnalyser.Models;

/// <summary>
/// A TwinCAT ACTION attached to a POU. Actions have no declaration of their own —
/// they execute in the owning POU's variable scope — and, like the POU body, may
/// be Structured Text or graphical (FBD/LD/IL/SFC/CFC). Previously dropped: the
/// parser only walked &lt;Method&gt; / &lt;Property&gt; elements, never &lt;Action&gt;.
/// </summary>
public class TcAction
{
    public string Name { get; set; } = "";

    /// <summary>The action body as ST (or the ST-equivalent of a graphical body).</summary>
    public string Body { get; set; } = "";

    public ImplLanguage Language { get; set; } = ImplLanguage.None;

    /// <summary>Decoded graphical body when the action is FBD/LD/IL/SFC/CFC.</summary>
    public TcGraphicalImpl? Graphical { get; set; }
}
