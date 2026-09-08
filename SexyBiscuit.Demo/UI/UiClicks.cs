using SexyBiscuit.Engine.UI;

namespace SexyBiscuit.Demo.UI;

/// <summary>
/// Runs a handler when a node is clicked, so C# game code can wire a button up once
/// instead of polling for it.
/// </summary>
/// <remarks>
/// <para>
/// The tree reports a click as <see cref="UiNode.Clicked"/>, true for the frame a press
/// completed on the node. That is polled rather than a callback because the contract has
/// to hand the same shape to a JavaScript game, and a JS function held by the C# side is
/// the kind of thing that marshals differently on the two engines.
/// </para>
/// <para>
/// None of that applies to a C# delegate held by C# code, so this is just the ergonomic
/// half put back for native games. It is deliberately in the demo rather than the engine:
/// a game that wants it can copy fifteen lines, and the engine keeps one way of saying
/// what a click is.
/// </para>
/// </remarks>
public sealed class UiClicks
{
    private readonly List<(UiNode Node, Action Handler)> _handlers = new();

    public void On(UiNode node, Action handler) => _handlers.Add((node, handler));

    /// <summary>Forgets every handler, for a menu that is about to be rebuilt.</summary>
    public void Clear() => _handlers.Clear();

    /// <summary>Fires whatever was clicked this frame. Call once per update.</summary>
    public void Tick()
    {
        // Over a copy: a handler is allowed to rebuild the very menu it was clicked in,
        // which would otherwise mutate the list this loop is walking.
        foreach ((UiNode node, Action handler) in _handlers.ToArray())
            if (node.Clicked) handler();
    }
}
