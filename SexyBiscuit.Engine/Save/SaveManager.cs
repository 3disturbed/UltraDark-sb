using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SexyBiscuit.Engine.Save;

/// <summary>
/// Save-slot management supporting JSON saves (with optional AES-256-CBC encryption)
/// and binary saves (caller-supplied <see cref="IBinarySerializable"/> format).
///
/// JSON encryption scheme:
///   - Key derivation: PBKDF2 / SHA-256 / 10 000 iterations / 32-byte output.
///   - Salt:           16 bytes stored at offset 0 of the file.
///   - IV:             16 bytes stored at offset 16 of the file.
///   - Cipher text:    AES-256-CBC from offset 32 onwards.
///
/// File layout:
///   - JSON saves  → <see cref="SaveDirectory"/>save_{slot}.json  (or .enc when encrypted)
///   - Binary saves → <see cref="SaveDirectory"/>save_{slot}.bin
/// </summary>
public static class SaveManager
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    public static string SaveDirectory { get; set; } = "Saves/";

    // -------------------------------------------------------------------------
    // Encryption state
    // -------------------------------------------------------------------------

    private static byte[]? _encryptionKey;   // null ⟹ no encryption

    private const int KeyBytes        = 32;  // AES-256
    private const int SaltBytes       = 16;
    private const int IvBytes         = 16;
    private const int Pbkdf2Iters     = 10_000;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented          = true,
        PropertyNameCaseInsensitive = true,
    };

    // -------------------------------------------------------------------------
    // Encryption key
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enables AES-256 encryption for all subsequent JSON save/load operations.
    /// Pass <see langword="null"/> or an empty string to disable encryption.
    /// </summary>
    public static void SetEncryptionKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            _encryptionKey = null;
            return;
        }

        // We store the raw passphrase bytes; the per-save salt is mixed in at write time.
        _encryptionKey = Encoding.UTF8.GetBytes(key);
    }

    // -------------------------------------------------------------------------
    // JSON saves
    // -------------------------------------------------------------------------

    /// <summary>Serialises <paramref name="data"/> as JSON and writes it to the slot file.</summary>
    public static void Save<T>(int slot, T data)
    {
        EnsureDirectory();
        string json  = JsonSerializer.Serialize(data, _jsonOptions);
        string path  = JsonSlotPath(slot);

        if (_encryptionKey is not null)
            WriteEncrypted(path, Encoding.UTF8.GetBytes(json));
        else
            File.WriteAllText(path, json, Encoding.UTF8);
    }

    /// <summary>
    /// Reads the slot file and deserialises it as <typeparamref name="T"/>.
    /// Returns <see langword="null"/> when the slot does not exist.
    /// </summary>
    public static T? Load<T>(int slot)
    {
        string path = JsonSlotPath(slot);
        if (!File.Exists(path)) return default;

        string json;
        if (_encryptionKey is not null)
            json = Encoding.UTF8.GetString(ReadEncrypted(path));
        else
            json = File.ReadAllText(path, Encoding.UTF8);

        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    /// <summary>Returns <see langword="true"/> when a save file exists for <paramref name="slot"/>.</summary>
    public static bool SlotExists(int slot) => File.Exists(JsonSlotPath(slot));

    /// <summary>Deletes the save file for <paramref name="slot"/> (if it exists).</summary>
    public static void Delete(int slot)
    {
        string path = JsonSlotPath(slot);
        if (File.Exists(path)) File.Delete(path);
    }

    // -------------------------------------------------------------------------
    // Binary saves
    // -------------------------------------------------------------------------

    /// <summary>
    /// Serialises <paramref name="data"/> via its <see cref="IBinarySerializable.Write"/>
    /// implementation and writes the binary stream to the slot file.
    /// No encryption is applied — the caller owns the binary format.
    /// </summary>
    public static void SaveBinary<T>(int slot, T data) where T : IBinarySerializable
    {
        EnsureDirectory();
        string path = BinarySlotPath(slot);

        using var fs  = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw  = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        // Prefix with a Unix timestamp so GetSaveSlots can report SavedAt
        bw.Write(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        data.Write(bw);
    }

    /// <summary>
    /// Reads the binary slot file and deserialises it into a new instance of
    /// <typeparamref name="T"/> via <see cref="IBinarySerializable.Read"/>.
    /// Returns <see langword="null"/> when the slot does not exist.
    /// </summary>
    public static T? LoadBinary<T>(int slot) where T : IBinarySerializable, new()
    {
        string path = BinarySlotPath(slot);
        if (!File.Exists(path)) return default;

        using var fs  = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br  = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

        // Skip the timestamp prefix written by SaveBinary
        br.ReadInt64();

        var instance = new T();
        instance.Read(br);
        return instance;
    }

    // -------------------------------------------------------------------------
    // Slot enumeration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns metadata for every save file (JSON and binary) found in
    /// <see cref="SaveDirectory"/>, ordered by slot number then by file type.
    /// </summary>
    public static IEnumerable<SaveSlotInfo> GetSaveSlots()
    {
        string dir = SaveDirectory;
        if (!Directory.Exists(dir)) yield break;

        // Match save_{slot}.json, save_{slot}.enc, save_{slot}.bin
        foreach (var file in Directory.EnumerateFiles(dir, "save_*.*")
                                      .OrderBy(f => f))
        {
            string name = Path.GetFileNameWithoutExtension(file); // e.g. "save_0"
            string ext  = Path.GetExtension(file).ToLowerInvariant();

            if (!name.StartsWith("save_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(name.AsSpan(5), out int slot)) continue;
            if (ext is not (".json" or ".enc" or ".bin")) continue;

            var fi = new FileInfo(file);
            yield return new SaveSlotInfo
            {
                Slot      = slot,
                SavedAt   = fi.LastWriteTimeUtc,
                ByteSize  = fi.Length,
                FilePath  = file,
            };
        }
    }

    // -------------------------------------------------------------------------
    // Encryption helpers
    // -------------------------------------------------------------------------

    private static void WriteEncrypted(string path, byte[] plainText)
    {
        // Generate a fresh random salt and IV for every write
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] iv   = RandomNumberGenerator.GetBytes(IvBytes);
        byte[] key  = DeriveKey(salt);

        using var aes   = CreateAes(key, iv);
        using var enc   = aes.CreateEncryptor();
        byte[] cipher   = enc.TransformFinalBlock(plainText, 0, plainText.Length);

        // Layout: [salt(16)] [iv(16)] [ciphertext(n)]
        using var fs    = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(salt,   0, salt.Length);
        fs.Write(iv,     0, iv.Length);
        fs.Write(cipher, 0, cipher.Length);
    }

    private static byte[] ReadEncrypted(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < SaltBytes + IvBytes)
            throw new CryptographicException($"Encrypted save file '{path}' is too short to be valid.");

        byte[] salt   = raw[..SaltBytes];
        byte[] iv     = raw[SaltBytes..(SaltBytes + IvBytes)];
        byte[] cipher = raw[(SaltBytes + IvBytes)..];
        byte[] key    = DeriveKey(salt);

        using var aes = CreateAes(key, iv);
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    private static byte[] DeriveKey(byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            _encryptionKey!,
            salt,
            Pbkdf2Iters,
            HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeyBytes);
    }

    private static Aes CreateAes(byte[] key, byte[] iv)
    {
        var aes    = Aes.Create();
        aes.Key    = key;
        aes.IV     = iv;
        aes.Mode   = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    // -------------------------------------------------------------------------
    // Path helpers
    // -------------------------------------------------------------------------

    private static string JsonSlotPath(int slot)
    {
        string ext = _encryptionKey is not null ? ".enc" : ".json";
        return Path.Combine(SaveDirectory, $"save_{slot}{ext}");
    }

    private static string BinarySlotPath(int slot)
        => Path.Combine(SaveDirectory, $"save_{slot}.bin");

    private static void EnsureDirectory()
    {
        if (!Directory.Exists(SaveDirectory))
            Directory.CreateDirectory(SaveDirectory);
    }
}

// =============================================================================
// Supporting types
// =============================================================================

/// <summary>
/// Implement this interface on your save-data struct/class to opt into the
/// binary save/load path via <see cref="SaveManager.SaveBinary{T}"/> and
/// <see cref="SaveManager.LoadBinary{T}"/>.
/// </summary>
public interface IBinarySerializable
{
    /// <summary>Write all fields into <paramref name="writer"/>.</summary>
    void Write(BinaryWriter writer);

    /// <summary>Read all fields from <paramref name="reader"/> in the same order as <see cref="Write"/>.</summary>
    void Read(BinaryReader reader);
}

/// <summary>Metadata about a save slot returned by <see cref="SaveManager.GetSaveSlots"/>.</summary>
public class SaveSlotInfo
{
    /// <summary>The slot index (e.g. 0, 1, 2).</summary>
    public int      Slot     { get; set; }

    /// <summary>UTC timestamp when the file was last written.</summary>
    public DateTime SavedAt  { get; set; }

    /// <summary>File size in bytes.</summary>
    public long     ByteSize { get; set; }

    /// <summary>Absolute or relative path to the save file.</summary>
    public string   FilePath { get; set; } = "";
}
