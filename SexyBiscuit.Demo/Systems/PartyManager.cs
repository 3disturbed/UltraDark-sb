using SexyBiscuit.Engine.Save;
using SexyBiscuit.Demo.Actors;

namespace SexyBiscuit.Demo.Systems;

// ---------------------------------------------------------------------------
// Save data DTO
// ---------------------------------------------------------------------------

/// <summary>
/// Plain serialisable snapshot of party / storage state used by SaveManager.
/// </summary>
public class PartySnapshot
{
    public List<PetSnapshot> Party   { get; set; } = new();
    public List<PetSnapshot> Storage { get; set; } = new();
}

public class PetSnapshot
{
    public string     PetName  { get; set; } = "";
    public string     Species  { get; set; } = "";
    public PetElement Element  { get; set; } = PetElement.Normal;
    public int        Level    { get; set; } = 1;
    public int        Hp       { get; set; } = 1;
    public int        MaxHp    { get; set; } = 1;
    public int        Mp       { get; set; } = 1;
    public int        MaxMp    { get; set; } = 1;
    public int        Attack   { get; set; } = 1;
    public int        Defense  { get; set; } = 1;
    public int        Speed    { get; set; } = 1;
}

// ---------------------------------------------------------------------------
// PartyManager
// ---------------------------------------------------------------------------

/// <summary>
/// Manages the player's team of up to 3 active pets and an unlimited storage box.
/// </summary>
public static class PartyManager
{
    // -------------------------------------------------------------------------
    // Collections
    // -------------------------------------------------------------------------
    public static List<PetActor> Party   { get; } = new(3);
    public static List<PetActor> Storage { get; } = new();

    // -------------------------------------------------------------------------
    // Party management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adds <paramref name="pet"/> to the party if there is a free slot (max 3),
    /// otherwise sends it to Storage.
    /// Returns true if added to party, false if sent to storage.
    /// </summary>
    public static bool AddToParty(PetActor pet)
    {
        if (Party.Count < 3)
        {
            Party.Add(pet);
            return true;
        }

        Storage.Add(pet);
        return false;
    }

    /// <summary>
    /// Swaps a pet between the active party and storage by index.
    /// </summary>
    public static void SwapPets(int partyIndex, int storageIndex)
    {
        if (partyIndex  < 0 || partyIndex  >= Party.Count)   return;
        if (storageIndex < 0 || storageIndex >= Storage.Count) return;

        var partyPet   = Party[partyIndex];
        var storagePet = Storage[storageIndex];

        Party[partyIndex]     = storagePet;
        Storage[storageIndex] = partyPet;
    }

    /// <summary>
    /// Returns the first non-fainted pet in the party, or null if all are fainted.
    /// </summary>
    public static PetActor? GetLead()
    {
        foreach (var pet in Party)
        {
            if (!pet.IsFainted) return pet;
        }
        return null;
    }

    /// <summary>
    /// Returns true when every party pet is fainted (Hp == 0).
    /// </summary>
    public static bool AllFainted()
    {
        if (Party.Count == 0) return true;
        return Party.All(p => p.IsFainted);
    }

    /// <summary>
    /// Fully restores HP and MP of all party and storage pets.
    /// </summary>
    public static void HealAll()
    {
        foreach (var pet in Party)
        {
            pet.Hp = pet.MaxHp;
            pet.Mp = pet.MaxMp;
        }

        foreach (var pet in Storage)
        {
            pet.Hp = pet.MaxHp;
            pet.Mp = pet.MaxMp;
        }
    }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    /// <summary>
    /// Serialises party and storage to the given save slot via SaveManager.
    /// </summary>
    public static void Save(string slot)
    {
        if (!int.TryParse(slot.Replace("slot", ""), out int slotIndex))
            slotIndex = 0;

        var snapshot = new PartySnapshot
        {
            Party   = Party.Select(ToSnapshot).ToList(),
            Storage = Storage.Select(ToSnapshot).ToList()
        };

        SaveManager.Save(slotIndex, snapshot);
    }

    /// <summary>
    /// Deserialises party and storage from the given save slot via SaveManager.
    /// </summary>
    public static void Load(string slot)
    {
        if (!int.TryParse(slot.Replace("slot", ""), out int slotIndex))
            slotIndex = 0;

        var snapshot = SaveManager.Load<PartySnapshot>(slotIndex);
        if (snapshot == null) return;

        Party.Clear();
        Storage.Clear();

        foreach (var ps in snapshot.Party)
            Party.Add(FromSnapshot(ps));

        foreach (var ps in snapshot.Storage)
            Storage.Add(FromSnapshot(ps));
    }

    // -------------------------------------------------------------------------
    // Snapshot helpers
    // -------------------------------------------------------------------------

    private static PetSnapshot ToSnapshot(PetActor pet) => new()
    {
        PetName  = pet.PetName,
        Species  = pet.Species,
        Element  = pet.Element,
        Level    = pet.Level,
        Hp       = pet.Hp,
        MaxHp    = pet.MaxHp,
        Mp       = pet.Mp,
        MaxMp    = pet.MaxMp,
        Attack   = pet.Attack,
        Defense  = pet.Defense,
        Speed    = pet.Speed,
    };

    private static PetActor FromSnapshot(PetSnapshot ps)
    {
        var pet = new PetActor(ps.PetName, ps.Species, ps.Element, ps.Level)
        {
            Hp      = ps.Hp,
            MaxHp   = ps.MaxHp,
            Mp      = ps.Mp,
            MaxMp   = ps.MaxMp,
            Attack  = ps.Attack,
            Defense = ps.Defense,
            Speed   = ps.Speed,
        };
        return pet;
    }
}
