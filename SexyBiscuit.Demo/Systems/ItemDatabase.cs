namespace SexyBiscuit.Demo.Systems;

// ---------------------------------------------------------------------------
// Item types
// ---------------------------------------------------------------------------

public enum ItemType
{
    Consumable,
    CaptureItem,
    KeyItem
}

// ---------------------------------------------------------------------------
// ItemDef record
// ---------------------------------------------------------------------------

/// <summary>
/// Immutable definition of a single item.
/// </summary>
public record ItemDef(
    string   Id,
    string   Name,
    string   Description,
    ItemType Type,
    int      BallMultiplier = 1,
    int      HealAmount     = 0);

// ---------------------------------------------------------------------------
// ItemDatabase
// ---------------------------------------------------------------------------

/// <summary>
/// Seed database for all in-game items. Keyed by string ID.
/// </summary>
public static class ItemDatabase
{
    // -------------------------------------------------------------------------
    // Registry
    // -------------------------------------------------------------------------
    public static Dictionary<string, ItemDef> Items { get; } = new(StringComparer.Ordinal);

    // -------------------------------------------------------------------------
    // Initialisation
    // -------------------------------------------------------------------------
    static ItemDatabase()
    {
        Register(new ItemDef(
            Id:             "biscuit_ball",
            Name:           "Biscuit Ball",
            Description:    "A standard capture ball. Smells faintly of butter.",
            Type:           ItemType.CaptureItem,
            BallMultiplier: 1));

        Register(new ItemDef(
            Id:             "great_ball",
            Name:           "Great Ball",
            Description:    "A higher-quality capture ball with an improved success rate.",
            Type:           ItemType.CaptureItem,
            BallMultiplier: 2));  // ~1.5x expressed as integer; BattleManager treats it as float

        Register(new ItemDef(
            Id:             "potion",
            Name:           "Potion",
            Description:    "Restores 30 HP to a single pet.",
            Type:           ItemType.Consumable,
            HealAmount:     30));

        Register(new ItemDef(
            Id:             "super_potion",
            Name:           "Super Potion",
            Description:    "Restores 60 HP to a single pet.",
            Type:           ItemType.Consumable,
            HealAmount:     60));

        Register(new ItemDef(
            Id:             "biscuit",
            Name:           "Biscuit",
            Description:    "A tasty biscuit. Restores a small amount of HP. Crumbs everywhere.",
            Type:           ItemType.Consumable,
            HealAmount:     10));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void Register(ItemDef item) => Items[item.Id] = item;

    /// <summary>
    /// Returns the item definition for <paramref name="id"/>, or null if not found.
    /// </summary>
    public static ItemDef? Get(string id)
        => Items.TryGetValue(id, out var def) ? def : null;

    /// <summary>
    /// Returns all items of the given type.
    /// </summary>
    public static IEnumerable<ItemDef> GetByType(ItemType type)
        => Items.Values.Where(i => i.Type == type);
}
