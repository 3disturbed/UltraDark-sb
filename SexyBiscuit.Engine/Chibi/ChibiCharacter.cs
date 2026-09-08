using SexyBiscuit.Engine.Assets;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Chibi;

/// <summary>
/// Builds a chibi under its actor, from a recipe file or from a seed.
/// </summary>
/// <remarks>
/// A scene could store a built chibi as the subtree it is, but then every scene file
/// carries forty nested actors per character and every read of it costs forty actors'
/// worth of tokens. Storing the recipe path instead means a scene says "a villager stands
/// here" in three lines, and the wardrobe is edited in one place rather than in every scene
/// that used it.
///
/// Mirrored in <c>html5/src/chibi/ChibiCharacter.js</c>.
/// </remarks>
[RequireComponent(typeof(Transform3D))]
public sealed class ChibiCharacter : Component
{
    /// <summary>Every live character, so a tool can rebuild them all after a wardrobe edit.</summary>
    public static readonly List<ChibiCharacter> All = new();

    /// <summary>A <c>.chibi</c> under <c>Assets/</c>. Empty means use the seed, or the default.</summary>
    public string RecipePath { get; set; } = string.Empty;

    /// <summary>Non-zero picks a coordinated random character, reproducibly.</summary>
    public int Seed { get; set; }

    /// <summary>Whether to build the body automatically. False leaves it to the game.</summary>
    public bool BuildOnStart { get; set; } = true;

    /// <summary>The built body, or null before it is built.</summary>
    [SceneIgnore]
    public ChibiBuild? Chibi { get; private set; }

    /// <inheritdoc />
    public override void Awake() => All.Add(this);

    /// <inheritdoc />
    public override void OnDestroy()
    {
        All.Remove(this);
        Chibi = null;
    }

    /// <inheritdoc />
    public override void Start()
    {
        if (BuildOnStart && Chibi == null) Rebuild();
    }

    /// <summary>The recipe this component's own fields describe, before any file is read.</summary>
    public ChibiRecipe LocalRecipe() => Seed != 0 ? ChibiRecipe.Random(Seed) : ChibiRecipe.Default();

    /// <summary>Rebuilds the character, destroying whatever was there.</summary>
    /// <param name="recipe">A recipe to use, or null to read <see cref="RecipePath"/>.</param>
    public ChibiBuild Rebuild(ChibiRecipe? recipe = null)
    {
        DestroySubtree();
        return Apply(recipe ?? ReadRecipe());
    }

    private ChibiRecipe ReadRecipe()
    {
        if (string.IsNullOrWhiteSpace(RecipePath)) return LocalRecipe();

        try
        {
            string? text = AssetManager.Current?.Load<string>(RecipePath);
            if (text == null) return LocalRecipe();
            return ChibiRecipe.Parse(text, Warn);
        }
        catch (Exception ex)
        {
            Warn($"could not read '{RecipePath}': {ex.Message}");
            return LocalRecipe();
        }
    }

    private ChibiBuild Apply(ChibiRecipe recipe)
    {
        ChibiBuild built = ChibiBuilder.Build(recipe, Warn);
        // false: the parts were placed in the rig's own frame, not the world's.
        built.Actor.AttachTo(Actor, keepWorldTransform: false);
        Actor.Scene?.AddActor(built.Actor);
        Chibi = built;
        return built;
    }

    private void DestroySubtree()
    {
        Chibi?.Actor.Destroy();
        Chibi = null;
    }

    private void Warn(string message)
        => Console.Error.WriteLine($"[ChibiCharacter] {Actor?.Name ?? "?"}: {message}");
}
