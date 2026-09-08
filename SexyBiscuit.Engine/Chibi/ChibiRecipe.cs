using System.Text.Json;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Chibi;

/// <summary>
/// The thirty-odd numbers and colours that describe a character.
/// </summary>
/// <remarks>
/// Small and legible on purpose: a <c>.chibi</c> file is meant to be edited by hand as
/// readily as by a panel, which is why colours are hex strings rather than
/// <see cref="Microsoft.Xna.Framework.Color"/> objects, and why every field has a default
/// so <c>{}</c> is a valid character.
///
/// Mirrored in <c>html5/src/chibi/ChibiRecipe.js</c>, including the random number
/// generator, so a seed names the same character on both engines.
/// </remarks>
public sealed class ChibiRecipe
{
    /// <summary>Proportion multipliers, all 1 at the default build.</summary>
    public static readonly string[] Proportions =
        { "height", "headSize", "bodyWidth", "limbThickness", "legLength", "armLength" };

    /// <summary>A proportion below this reads as a bug rather than a style choice.</summary>
    public const float MinProportion = 0.5f;

    /// <summary>And above this the parts pull away from the joints they hang on.</summary>
    public const float MaxProportion = 2.0f;

    /// <summary>An accessory hung on a socket, optionally in a colour of its own.</summary>
    public sealed record Accessory(string Part, string? Colour);

    /// <summary>A name for the character. Also the root actor's name.</summary>
    public string Name { get; set; } = "Chibi";

    /// <summary>The seed this character came from, or 0 if it was authored by hand.</summary>
    public int Seed { get; set; }

    /// <summary>Proportion name to multiplier.</summary>
    public Dictionary<string, float> ProportionValues { get; } = new(StringComparer.Ordinal);

    /// <summary>Style slot name to the variant chosen for it.</summary>
    public Dictionary<string, string> Style { get; } = new(StringComparer.Ordinal);

    /// <summary>Colour slot name to a hex string.</summary>
    public Dictionary<string, string> Colours { get; } = new(StringComparer.Ordinal);

    /// <summary>Whatever the character is carrying or wearing on a socket.</summary>
    public List<Accessory> Accessories { get; } = new();

    private static readonly (string Slot, string Variant)[] DefaultStyle =
    {
        ("head", "Round"), ("hair", "Bob"), ("eyes", "Dot"),
        ("body", "Tunic"), ("legs", "Trousers"), ("feet", "Shoes"),
    };

    private static readonly (string Slot, string Hex)[] DefaultColours =
    {
        ("skin", "#F2C6A0"), ("hair", "#4A2E1E"), ("eyes", "#241C18"), ("top", "#5B8C5A"),
        ("bottom", "#3A4A6B"), ("shoes", "#2E2A28"), ("accent", "#D9A441"),
    };

    /// <summary>A complete recipe with every field at its default.</summary>
    public static ChibiRecipe Default()
    {
        var recipe = new ChibiRecipe();
        foreach (string name in Proportions) recipe.ProportionValues[name] = 1f;
        foreach (var (slot, variant) in DefaultStyle) recipe.Style[slot] = variant;
        foreach (var (slot, hex) in DefaultColours) recipe.Colours[slot] = hex;
        return recipe;
    }

    /// <summary>One proportion, or 1 when it is not set.</summary>
    public float Proportion(string? name)
        => name != null && ProportionValues.TryGetValue(name, out float value) ? value : 1f;

    /// <summary>A copy that shares nothing with this one.</summary>
    public ChibiRecipe Clone()
    {
        var copy = new ChibiRecipe { Name = Name, Seed = Seed };
        foreach (var pair in ProportionValues) copy.ProportionValues[pair.Key] = pair.Value;
        foreach (var pair in Style) copy.Style[pair.Key] = pair.Value;
        foreach (var pair in Colours) copy.Colours[pair.Key] = pair.Value;
        copy.Accessories.AddRange(Accessories);
        return copy;
    }

    // -------------------------------------------------------------------------
    // Reading and writing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fills in everything the JSON left out and drops anything the part table does not
    /// recognise.
    /// </summary>
    /// <remarks>
    /// A style name that no longer exists falls back to the default rather than throwing:
    /// renaming a hairstyle should not make every saved character unloadable, and
    /// <paramref name="onWarning"/> gives a caller somewhere to say so.
    /// </remarks>
    public static ChibiRecipe Normalise(JsonElement raw, Action<string>? onWarning = null)
    {
        var recipe = Default();
        if (raw.ValueKind != JsonValueKind.Object) return recipe;

        if (raw.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(name.GetString()))
        {
            recipe.Name = name.GetString()!.Trim();
        }

        if (raw.TryGetProperty("seed", out var seed) && seed.ValueKind == JsonValueKind.Number)
            recipe.Seed = (int)seed.GetDouble();

        if (raw.TryGetProperty("proportions", out var proportions)
            && proportions.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in Proportions)
            {
                if (!proportions.TryGetProperty(key, out var value)) continue;
                if (value.ValueKind != JsonValueKind.Number) continue;
                recipe.ProportionValues[key] =
                    Math.Clamp((float)value.GetDouble(), MinProportion, MaxProportion);
            }
        }

        if (raw.TryGetProperty("style", out var style) && style.ValueKind == JsonValueKind.Object)
        {
            foreach (string slot in ChibiParts.Slots)
            {
                if (!style.TryGetProperty(slot, out var wanted)) continue;
                string? variant = wanted.GetString();
                if (variant != null && ChibiParts.VariantsFor(slot).Contains(variant))
                    recipe.Style[slot] = variant;
                else
                    onWarning?.Invoke($"'{variant}' is not a {slot} style; using '{recipe.Style[slot]}'.");
            }
        }

        if (raw.TryGetProperty("colours", out var colours) && colours.ValueKind == JsonValueKind.Object)
        {
            foreach (string slot in ChibiParts.ColourSlots)
            {
                if (!colours.TryGetProperty(slot, out var wanted)) continue;
                string? hex = wanted.GetString();
                if (!string.IsNullOrWhiteSpace(hex)) recipe.Colours[slot] = hex!.Trim();
            }
        }

        if (raw.TryGetProperty("accessories", out var accessories)
            && accessories.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in accessories.EnumerateArray())
            {
                string? part = entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("part", out var p) ? p.GetString() : null;

                if (part == null || !ChibiParts.AccessoryNames.Contains(part))
                {
                    onWarning?.Invoke($"'{part}' is not an accessory; skipping it.");
                    continue;
                }

                string? colour = entry.TryGetProperty("colour", out var c)
                    && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                recipe.Accessories.Add(new Accessory(part, colour));
            }
        }

        return recipe;
    }

    /// <summary>Parses a <c>.chibi</c> file's text. Invalid JSON gives the default character.</summary>
    public static ChibiRecipe Parse(string text, Action<string>? onWarning = null)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return Normalise(document.RootElement, onWarning);
        }
        catch (JsonException ex)
        {
            onWarning?.Invoke($"could not parse the recipe: {ex.Message}");
            return Default();
        }
    }

    /// <summary>The text to write to a <c>.chibi</c> file, in the shape the reader expects.</summary>
    public string ToJson()
    {
        var root = new JsonObject
        {
            ["version"] = ChibiParts.Version,
            ["name"] = Name,
            ["seed"] = Seed,
        };

        var proportions = new JsonObject();
        foreach (string key in Proportions) proportions[key] = Proportion(key);
        root["proportions"] = proportions;

        var style = new JsonObject();
        foreach (string slot in ChibiParts.Slots) style[slot] = Style.GetValueOrDefault(slot);
        root["style"] = style;

        var colours = new JsonObject();
        foreach (string slot in ChibiParts.ColourSlots) colours[slot] = Colours.GetValueOrDefault(slot);
        root["colours"] = colours;

        var accessories = new JsonArray();
        foreach (var accessory in Accessories)
        {
            accessories.Add(new JsonObject { ["part"] = accessory.Part, ["colour"] = accessory.Colour });
        }
        root["accessories"] = accessories;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    // -------------------------------------------------------------------------
    // The random character
    // -------------------------------------------------------------------------

    /// <summary>
    /// mulberry32, in 32-bit integer arithmetic that JavaScript reproduces exactly.
    /// </summary>
    /// <remarks>
    /// <see cref="Random"/> would give a different village on every engine and every run,
    /// which is the opposite of what a seed is for.
    /// </remarks>
    public sealed class SeededRandom
    {
        private uint _state;

        /// <summary>Starts the sequence for one seed.</summary>
        public SeededRandom(int seed) => _state = unchecked((uint)seed);

        /// <summary>The next value, in [0, 1).</summary>
        public double Next()
        {
            unchecked
            {
                _state += 0x6D2B79F5u;
                uint t = _state;
                t = (t ^ (t >> 15)) * (t | 1u);
                t ^= t + (t ^ (t >> 7)) * (t | 61u);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }

    /// <summary>
    /// A coordinated character from one integer.
    /// </summary>
    /// <remarks>
    /// Colours come from the curated palettes rather than from random bytes, because random
    /// bytes give forty characters the colour of mud; drawing a top and a bottom as a pair
    /// keeps the outfit from fighting itself.
    /// </remarks>
    public static ChibiRecipe Random(int seed)
    {
        var next = new SeededRandom(seed);
        string Pick(IReadOnlyList<string> list) => list.Count == 0
            ? "#FFFFFF" : list[Math.Min(list.Count - 1, (int)(next.Next() * list.Count))];

        var recipe = Default();
        recipe.Name = $"Chibi {seed}";
        recipe.Seed = seed;

        foreach (string key in Proportions)
        {
            // +/-15%: enough that no two are the same height, little enough that the parts
            // still line up with the joints they hang on.
            recipe.ProportionValues[key] = (float)(Math.Round((0.85 + next.Next() * 0.3) * 1000) / 1000);
        }

        foreach (string slot in ChibiParts.Slots)
        {
            var variants = ChibiParts.VariantsFor(slot);
            if (variants.Count > 0)
                recipe.Style[slot] = variants[Math.Min(variants.Count - 1, (int)(next.Next() * variants.Count))];
        }

        var outfits = ChibiParts.Outfits;
        string[] outfit = outfits.Count == 0
            ? new[] { "#5B8C5A", "#3A4A6B" }
            : outfits[Math.Min(outfits.Count - 1, (int)(next.Next() * outfits.Count))];

        recipe.Colours["skin"] = Pick(ChibiParts.Palette("skin"));
        recipe.Colours["hair"] = Pick(ChibiParts.Palette("hair"));
        recipe.Colours["eyes"] = Pick(ChibiParts.Palette("eyes"));
        recipe.Colours["top"] = outfit[0];
        recipe.Colours["bottom"] = outfit.Length > 1 ? outfit[1] : outfit[0];
        recipe.Colours["shoes"] = Pick(ChibiParts.Palette("shoes"));
        recipe.Colours["accent"] = Pick(ChibiParts.Palette("accent"));

        if (next.Next() < 0.4)
        {
            var names = ChibiParts.AccessoryNames;
            if (names.Count > 0)
                recipe.Accessories.Add(new Accessory(
                    names[Math.Min(names.Count - 1, (int)(next.Next() * names.Count))], null));
        }

        return recipe;
    }
}
