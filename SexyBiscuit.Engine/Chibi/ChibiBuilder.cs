using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.Chibi;

/// <summary>One built character: the actor, plus the lookups a game and a panel need.</summary>
public sealed class ChibiBuild
{
    /// <summary>The root. Move this, not the parts.</summary>
    public Actor Actor { get; }

    /// <summary>The recipe as it was normalised, not as it was passed.</summary>
    public ChibiRecipe Recipe { get; }

    /// <summary>Joint name to its transform, resolved once so nothing searches per frame.</summary>
    public Dictionary<string, Transform3D> Joints { get; } = new(StringComparer.Ordinal);

    /// <summary>Socket name, without the <c>Socket_</c> prefix its actor carries, to that actor.</summary>
    public Dictionary<string, Actor> Sockets { get; } = new(StringComparer.Ordinal);

    /// <summary>Every drawn piece and the colour slot it takes its colour from.</summary>
    public List<(MeshRenderer Renderer, string? Colour)> Parts { get; } = new();

    internal ChibiBuild(Actor actor, ChibiRecipe recipe)
    {
        Actor = actor;
        Recipe = recipe;
    }

    /// <summary>Repaints every part drawn in one colour slot. False for an unknown slot.</summary>
    public bool SetColour(string slot, string hex)
    {
        if (!Recipe.Colours.ContainsKey(slot)) return false;

        Recipe.Colours[slot] = hex;
        Color colour = ChibiBuilder.ParseColour(hex);
        foreach (var (renderer, name) in Parts)
        {
            if (name == slot) renderer.AlbedoColor = colour;
        }
        return true;
    }
}

/// <summary>
/// A recipe becomes a subtree of primitive-mesh actors.
/// </summary>
/// <remarks>
/// <para>
/// The whole feature rests on one observation: a chibi's proportions hide its joints, so
/// nothing has to bend, so nothing needs a skinning shader, a bone palette or a model file.
/// A character is a hierarchy of cubes, spheres and capsules, which is what both engines
/// can already draw.
/// </para>
/// <para>
/// A joint actor carries rotation only; the parts hanging off it are separate children
/// holding the offset and the scale. That is what puts a limb's pivot at the shoulder
/// rather than half-way down the upper arm, and it means animation writes nothing but
/// <see cref="Transform3D.LocalEulerAngles"/> on sixteen actors.
/// </para>
/// <para>Mirrored in <c>html5/src/chibi/ChibiBuilder.js</c>.</para>
/// </remarks>
public static class ChibiBuilder
{
    /// <summary>Chibis are flat-shaded toys, not car paint.</summary>
    private const float Roughness = 0.78f;

    /// <summary>Slots authored against the reference head, and so scaled onto the real one.</summary>
    private static readonly HashSet<string> FittedToHead = new(StringComparer.Ordinal) { "hair", "eyes" };

    /// <summary>Sockets on the head, which have to move with it for the same reason.</summary>
    private static readonly HashSet<string> FittedSockets = new(StringComparer.Ordinal) { "Head", "Face" };

    /// <summary>Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. Anything else is white.</summary>
    internal static Color ParseColour(string? value)
    {
        string hex = (value ?? string.Empty).TrimStart('#');
        if (hex.Length < 6) return Color.White;

        try
        {
            byte Channel(int i) => Convert.ToByte(hex.Substring(i, 2), 16);
            return new Color(Channel(0), Channel(2), Channel(4), hex.Length >= 8 ? Channel(6) : (byte)255);
        }
        catch (FormatException) { return Color.White; }
    }

    /// <summary>Builds a character.</summary>
    /// <param name="recipe">A recipe, already normalised.</param>
    /// <param name="onWarning">Told about anything the part table could not place.</param>
    public static ChibiBuild Build(ChibiRecipe recipe, Action<string>? onWarning = null)
    {
        float height = recipe.Proportion("height");

        var root = new Actor(string.IsNullOrWhiteSpace(recipe.Name) ? "Chibi" : recipe.Name);
        root.AddComponent<Transform3D>().LocalScale = new Vector3(height, height, height);

        var build = new ChibiBuild(root, recipe);
        var actors = new Dictionary<string, Actor>(StringComparer.Ordinal) { [string.Empty] = root };

        Vector3 headFit = ChibiParts.HeadFit(recipe.Style.GetValueOrDefault("head", "Round"));
        // Eyes get their own fit: it carries how far forward that head's face is, which
        // differs by shape in a way the skull's proportions alone do not describe.
        Vector3 eyeFit = ChibiParts.HeadEyeFit(recipe.Style.GetValueOrDefault("head", "Round"));

        // ---- Joints, parents first so every attach finds its parent -----------------
        foreach (ChibiJoint joint in ChibiParts.Joints)
        {
            Actor parent = actors.GetValueOrDefault(joint.Parent, root);
            Actor actor = MakeActor(joint.Name, parent);

            float scale = recipe.Proportion(joint.PosScale);
            actor.GetComponent<Transform3D>()!.LocalPosition =
                new Vector3(joint.Pos.X, joint.Pos.Y * scale, joint.Pos.Z);

            actors[joint.Name] = actor;
            build.Joints[joint.Name] = actor.GetComponent<Transform3D>()!;
        }

        // ---- Sockets: empty actors a game hangs a hat or a sword from ---------------
        foreach (ChibiSocket socket in ChibiParts.Sockets)
        {
            Actor actor = MakeActor($"Socket_{socket.Name}", actors.GetValueOrDefault(socket.Joint, root));
            Vector3 fit = FittedSockets.Contains(socket.Name) ? headFit : Vector3.One;
            actor.GetComponent<Transform3D>()!.LocalPosition = socket.Pos * fit;
            build.Sockets[socket.Name] = actor;
        }

        // ---- The parts themselves ---------------------------------------------------
        foreach (string slot in ChibiParts.Slots)
        {
            string variant = recipe.Style.GetValueOrDefault(slot, string.Empty);
            Vector3 fit = slot == "eyes" ? eyeFit
                : FittedToHead.Contains(slot) ? headFit : Vector3.One;

            foreach (ChibiPiece piece in ChibiParts.Pieces(slot, variant))
            {
                foreach (string side in ChibiParts.SidesOf(piece.Joint))
                {
                    string jointName = ChibiParts.Expand(piece.Joint, side);
                    if (!actors.TryGetValue(jointName, out Actor? jointActor))
                    {
                        onWarning?.Invoke(
                            $"{slot}/{variant} names joint '{jointName}', which the rig has not got.");
                        continue;
                    }
                    AddPiece(piece, jointActor, side, recipe, build, $"{slot}_{piece.Mesh}", null, fit);
                }
            }
        }

        // ---- Accessories, which hang off sockets rather than joints ------------------
        foreach (ChibiRecipe.Accessory entry in recipe.Accessories)
        {
            foreach (ChibiPiece piece in ChibiParts.Accessory(entry.Part))
            {
                foreach (string side in ChibiParts.SidesOf(piece.Socket))
                {
                    string name = ChibiParts.Expand(piece.Socket, side);
                    if (!build.Sockets.TryGetValue(name, out Actor? socket))
                    {
                        onWarning?.Invoke(
                            $"accessory '{entry.Part}' names socket '{name}', which the rig has not got.");
                        continue;
                    }

                    // A hat is placed relative to the socket, which has already moved with
                    // the head, so only its size needs fitting -- and only on the head,
                    // where a mismatch is a hat that does not sit on the skull.
                    Vector3 fit = FittedSockets.Contains(name) ? headFit : Vector3.One;
                    AddPiece(piece, socket, side, recipe, build,
                        $"{entry.Part}_{piece.Mesh}", entry.Colour, fit);
                }
            }
        }

        return build;
    }

    private static Actor MakeActor(string name, Actor? parent)
    {
        var actor = new Actor(name);
        actor.AddComponent<Transform3D>();
        // false, not the default: a rig's offsets are already local, so keeping the world
        // transform would drag every joint back to where it was standing.
        if (parent != null) actor.AttachTo(parent, keepWorldTransform: false);
        return actor;
    }

    private static void AddPiece(ChibiPiece piece, Actor jointActor, string side, ChibiRecipe recipe,
                                 ChibiBuild build, string name, string? colourOverride, Vector3 fit)
    {
        float width = recipe.Proportion(piece.WidthScale);
        float length = recipe.Proportion(piece.LengthScale);

        Actor actor = MakeActor(name, jointActor);
        var transform = actor.GetComponent<Transform3D>()!;

        Vector3 pos = ChibiParts.MirrorVector(piece.Pos, side);
        transform.LocalPosition = new Vector3(
            pos.X * width * fit.X, pos.Y * length * fit.Y, pos.Z * width * fit.Z);
        transform.LocalScale = new Vector3(
            piece.Scale.X * width * fit.X, piece.Scale.Y * length * fit.Y, piece.Scale.Z * width * fit.Z);
        if (piece.Rot != Vector3.Zero)
            transform.LocalEulerAngles = ChibiParts.MirrorEuler(piece.Rot, side);

        var renderer = actor.AddComponent<MeshRenderer>();
        renderer.MeshType = Enum.TryParse(piece.Mesh, out MeshPrimitive primitive)
            ? primitive : MeshPrimitive.Cube;
        renderer.AlbedoColor = ParseColour(
            colourOverride ?? recipe.Colours.GetValueOrDefault(piece.Colour, "#FFFFFF"));
        renderer.Roughness = Roughness;

        // An overridden accessory colour is not in any slot, so SetColour must not repaint
        // it later when the slot it borrowed its name from changes.
        build.Parts.Add((renderer, colourOverride != null ? null : piece.Colour));
    }
}
