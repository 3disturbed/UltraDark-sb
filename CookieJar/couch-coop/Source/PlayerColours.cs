using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace Cookies.CouchCoop;

/// <summary>
/// Tints a pawn by player index, so four people sharing a screen can tell which one is theirs.
/// </summary>
public sealed class PlayerColours : Component
{
    /// <summary>The default four, in join order: blue, red, green, yellow.</summary>
    public static readonly Color[] Palette =
    {
        new(70, 140, 255), new(235, 80, 70), new(90, 200, 110), new(240, 200, 70),
    };

    /// <summary>Which colour this pawn takes.</summary>
    public int PlayerIndex { get; set; }

    /// <summary>Overrides the palette entry for this pawn.</summary>
    public Color? Override { get; set; }

    /// <summary>The colour this player is using.</summary>
    public Color Colour => Override ?? Palette[Math.Clamp(PlayerIndex, 0, Palette.Length - 1)];

    public override void Start() => Apply();

    /// <summary>Tints every mesh on this actor. Call again after swapping the pawn's model.</summary>
    public void Apply()
    {
        foreach (var mesh in Actor.GetComponents<MeshRenderer>())
            mesh.AlbedoColor = Colour;

        foreach (var sprite in Actor.GetComponents<SpriteRenderer>())
            sprite.Tint = Colour;
    }
}
