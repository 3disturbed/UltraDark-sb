using System.Text.Json;
using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Chibi;

/// <summary>One primitive a character is built from: a shape, where it hangs, and its colour.</summary>
/// <param name="Mesh">A <see cref="Rendering.MeshPrimitive"/> name.</param>
/// <param name="Joint">The joint it hangs on, or empty for an accessory.</param>
/// <param name="Socket">The socket it hangs on, or empty for a body part.</param>
/// <param name="Pos">Its offset from that joint or socket, before proportions.</param>
/// <param name="Scale">Its size. A Capsule is one unit wide and two tall.</param>
/// <param name="Rot">Its euler rotation in degrees.</param>
/// <param name="Colour">A colour slot name, looked up in the recipe.</param>
/// <param name="WidthScale">A recipe proportion multiplying x and z, or null.</param>
/// <param name="LengthScale">A recipe proportion multiplying y, or null.</param>
public sealed record ChibiPiece(
    string Mesh, string Joint, string Socket,
    Vector3 Pos, Vector3 Scale, Vector3 Rot,
    string Colour, string? WidthScale, string? LengthScale);

/// <summary>One joint of the rig, already expanded to a concrete left or right name.</summary>
public sealed record ChibiJoint(string Name, string Parent, Vector3 Pos, string? PosScale);

/// <summary>An attachment point a game can hang a hat or a sword from.</summary>
public sealed record ChibiSocket(string Name, string Joint, Vector3 Pos);

/// <summary>
/// The part table: every joint, socket, style variant, accessory and palette.
/// </summary>
/// <remarks>
/// <para>
/// The data is <c>html5/src/chibi/chibi-parts.json</c>, embedded in this assembly and read
/// by the JavaScript engine directly. One file, so the two engines cannot build different
/// characters — the same arrangement <see cref="UI.BitmapFont"/> uses for its glyphs, and
/// for the same reason.
/// </para>
/// <para>
/// A name ending in <c>*</c> in the file stands for a mirrored pair and is expanded here
/// into <c>L</c> and <c>R</c>, with x and the two rotation axes negated on the right.
/// Writing both sides into the data instead would let them drift apart.
/// </para>
/// </remarks>
public static class ChibiParts
{
    /// <summary>The two sides a mirrored name expands to, left first.</summary>
    public static readonly string[] Sides = { "L", "R" };

    private static readonly JsonDocument? _document = Load();

    /// <summary>True when the part table loaded. False means no character can be built.</summary>
    public static bool IsLoaded => _document != null;

    /// <summary>The table's format version.</summary>
    public static int Version { get; } = Root("version").GetInt32OrDefault(1);

    /// <summary>Every joint, parents before children, so a builder can attach in one pass.</summary>
    public static IReadOnlyList<ChibiJoint> Joints { get; } = ReadJoints();

    /// <summary>Every joint name, in the same order. This is the contract a clip writes to.</summary>
    public static IReadOnlyList<string> JointNames { get; } = Joints.Select(j => j.Name).ToArray();

    /// <summary>Every attachment point, named without the <c>Socket_</c> prefix its actor carries.</summary>
    public static IReadOnlyList<ChibiSocket> Sockets { get; } = ReadSockets();

    /// <summary>The style slots a recipe chooses from: head, hair, eyes, body, legs, feet.</summary>
    public static IReadOnlyList<string> Slots { get; } = ReadStrings("slots");

    /// <summary>The colour names a piece may paint itself from.</summary>
    public static IReadOnlyList<string> ColourSlots { get; } = ReadStrings("colourSlots");

    private static readonly Dictionary<string, Dictionary<string, ChibiPiece[]>> _parts = ReadParts();
    private static readonly Dictionary<string, ChibiPiece[]> _accessories = ReadAccessories();
    private static readonly Dictionary<string, Vector3> _headFit = ReadFit("headFit");
    private static readonly Dictionary<string, Vector3> _headEyeFit = ReadFit("headEyeFit");
    private static readonly Dictionary<string, string[]> _palettes = ReadPalettes();
    private static readonly string[][] _outfits = ReadOutfits();

    /// <summary>The variant names available for one style slot.</summary>
    public static IReadOnlyList<string> VariantsFor(string slot)
        => _parts.TryGetValue(slot, out var variants) ? variants.Keys.ToArray() : Array.Empty<string>();

    /// <summary>The pieces one variant is drawn from. Empty for an unknown name, and for Bald.</summary>
    public static IReadOnlyList<ChibiPiece> Pieces(string slot, string variant)
        => _parts.TryGetValue(slot, out var variants) && variants.TryGetValue(variant, out var pieces)
            ? pieces : Array.Empty<ChibiPiece>();

    /// <summary>Every accessory name.</summary>
    public static IReadOnlyList<string> AccessoryNames { get; } = _accessories.Keys.ToArray();

    /// <summary>The pieces one accessory is drawn from.</summary>
    public static IReadOnlyList<ChibiPiece> Accessory(string name)
        => _accessories.TryGetValue(name, out var pieces) ? pieces : Array.Empty<ChibiPiece>();

    /// <summary>
    /// How a head shape differs from the reference one, so hair authored against a round
    /// skull fits a square one instead of swallowing it.
    /// </summary>
    public static Vector3 HeadFit(string head)
        => _headFit.TryGetValue(head, out var fit) ? fit : Vector3.One;

    /// <summary>
    /// The same for eyes, and it also carries how far forward that head's face is — a cube's
    /// face does not fall away towards the cheek as a sphere's does, so one depth buries the
    /// eyes of one head and floats those of another.
    /// </summary>
    public static Vector3 HeadEyeFit(string head)
        => _headEyeFit.TryGetValue(head, out var fit) ? fit : HeadFit(head);

    /// <summary>A curated colour palette, by slot name. Empty for an unknown one.</summary>
    public static IReadOnlyList<string> Palette(string name)
        => _palettes.TryGetValue(name, out var colours) ? colours : Array.Empty<string>();

    /// <summary>Top and bottom colours that were chosen to go together.</summary>
    public static IReadOnlyList<string[]> Outfits => _outfits;

    // -------------------------------------------------------------------------
    // Mirroring
    // -------------------------------------------------------------------------

    /// <summary>True when the name describes a mirrored pair rather than a single joint.</summary>
    public static bool IsMirrored(string name) => name.EndsWith('*');

    /// <summary><c>Arm*</c> and <c>L</c> give <c>ArmL</c>; a name without a star is unchanged.</summary>
    public static string Expand(string name, string side) => name.Replace("*", side);

    /// <summary>Mirrors an offset for the right-hand side: x flips.</summary>
    public static Vector3 MirrorVector(Vector3 v, string side)
        => side == "R" ? new Vector3(-v.X, v.Y, v.Z) : v;

    /// <summary>Mirrors a rotation: the two axes that would send a limb the wrong way round.</summary>
    public static Vector3 MirrorEuler(Vector3 r, string side)
        => side == "R" ? new Vector3(r.X, -r.Y, -r.Z) : r;

    /// <summary>The sides a name expands to: both, or a single unmirrored entry.</summary>
    public static string[] SidesOf(string name) => IsMirrored(name) ? Sides : new[] { "L" };

    // -------------------------------------------------------------------------
    // Reading the file
    // -------------------------------------------------------------------------

    private static JsonDocument? Load()
    {
        try
        {
            using Stream? stream = typeof(ChibiParts).Assembly
                .GetManifestResourceStream("SexyBiscuit.Engine.chibi-parts.json");
            return stream == null ? null : JsonDocument.Parse(stream);
        }
        catch (Exception ex)
        {
            // A missing table must not take the game down: no chibi is built, and the
            // reason is on the console rather than nowhere.
            Console.Error.WriteLine($"[ChibiParts] could not load the shared part table: {ex.Message}");
            return null;
        }
    }

    private static JsonElement Root(string property)
        => _document != null && _document.RootElement.TryGetProperty(property, out var value)
            ? value : default;

    private static int GetInt32OrDefault(this JsonElement element, int fallback)
        => element.ValueKind == JsonValueKind.Number ? element.GetInt32() : fallback;

    private static Vector3 ReadVector(JsonElement element, Vector3 fallback)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() < 3) return fallback;
        return new Vector3(
            (float)element[0].GetDouble(), (float)element[1].GetDouble(), (float)element[2].GetDouble());
    }

    private static string? ReadString(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string[] ReadStrings(string property)
    {
        var element = Root(property);
        return element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray()
            : Array.Empty<string>();
    }

    private static ChibiJoint[] ReadJoints()
    {
        var element = Root("joints");
        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<ChibiJoint>();

        var joints = new List<ChibiJoint>();
        foreach (JsonElement entry in element.EnumerateArray())
        {
            string name = ReadString(entry, "name") ?? string.Empty;
            string parent = ReadString(entry, "parent") ?? string.Empty;
            Vector3 pos = ReadVector(entry.GetProperty("pos"), Vector3.Zero);

            foreach (string side in SidesOf(name))
            {
                joints.Add(new ChibiJoint(
                    Expand(name, side), Expand(parent, side),
                    IsMirrored(name) ? MirrorVector(pos, side) : pos,
                    ReadString(entry, "posScale")));
            }
        }
        return joints.ToArray();
    }

    private static ChibiSocket[] ReadSockets()
    {
        var element = Root("sockets");
        if (element.ValueKind != JsonValueKind.Array) return Array.Empty<ChibiSocket>();

        var sockets = new List<ChibiSocket>();
        foreach (JsonElement entry in element.EnumerateArray())
        {
            string name = ReadString(entry, "name") ?? string.Empty;
            string joint = ReadString(entry, "joint") ?? string.Empty;
            Vector3 pos = ReadVector(entry.GetProperty("pos"), Vector3.Zero);

            foreach (string side in SidesOf(name))
            {
                sockets.Add(new ChibiSocket(
                    Expand(name, side), Expand(joint, side),
                    IsMirrored(name) ? MirrorVector(pos, side) : pos));
            }
        }
        return sockets.ToArray();
    }

    private static ChibiPiece ReadPiece(JsonElement entry)
        => new(
            ReadString(entry, "mesh") ?? "Cube",
            ReadString(entry, "joint") ?? string.Empty,
            ReadString(entry, "socket") ?? string.Empty,
            ReadVector(entry.GetProperty("pos"), Vector3.Zero),
            ReadVector(entry.GetProperty("scale"), Vector3.One),
            entry.TryGetProperty("rot", out var rot) ? ReadVector(rot, Vector3.Zero) : Vector3.Zero,
            ReadString(entry, "colour") ?? "skin",
            ReadString(entry, "widthScale"),
            ReadString(entry, "lengthScale"));

    private static Dictionary<string, Dictionary<string, ChibiPiece[]>> ReadParts()
    {
        var parts = new Dictionary<string, Dictionary<string, ChibiPiece[]>>(StringComparer.Ordinal);
        var element = Root("parts");
        if (element.ValueKind != JsonValueKind.Object) return parts;

        foreach (JsonProperty slot in element.EnumerateObject())
        {
            var variants = new Dictionary<string, ChibiPiece[]>(StringComparer.Ordinal);
            foreach (JsonProperty variant in slot.Value.EnumerateObject())
                variants[variant.Name] = variant.Value.EnumerateArray().Select(ReadPiece).ToArray();
            parts[slot.Name] = variants;
        }
        return parts;
    }

    private static Dictionary<string, ChibiPiece[]> ReadAccessories()
    {
        var accessories = new Dictionary<string, ChibiPiece[]>(StringComparer.Ordinal);
        var element = Root("accessories");
        if (element.ValueKind != JsonValueKind.Object) return accessories;

        foreach (JsonProperty entry in element.EnumerateObject())
            accessories[entry.Name] = entry.Value.EnumerateArray().Select(ReadPiece).ToArray();
        return accessories;
    }

    private static Dictionary<string, Vector3> ReadFit(string property)
    {
        var fits = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        var element = Root(property);
        if (element.ValueKind != JsonValueKind.Object) return fits;

        foreach (JsonProperty entry in element.EnumerateObject())
            fits[entry.Name] = ReadVector(entry.Value, Vector3.One);
        return fits;
    }

    private static Dictionary<string, string[]> ReadPalettes()
    {
        var palettes = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var element = Root("palettes");
        if (element.ValueKind != JsonValueKind.Object) return palettes;

        foreach (JsonProperty entry in element.EnumerateObject())
        {
            if (entry.Name == "outfit") continue;      // pairs, read separately
            palettes[entry.Name] = entry.Value.EnumerateArray()
                .Select(v => v.GetString() ?? "#FFFFFF").ToArray();
        }
        return palettes;
    }

    private static string[][] ReadOutfits()
    {
        var element = Root("palettes");
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("outfit", out var outfits)) return Array.Empty<string[]>();

        return outfits.EnumerateArray()
            .Select(pair => pair.EnumerateArray().Select(v => v.GetString() ?? "#FFFFFF").ToArray())
            .ToArray();
    }
}
