using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SexyBiscuit.Engine.Build;

// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

public enum BuildPlatform
{
    Windows_x64,
    Windows_x86,
    Linux_x64,
    macOS_x64,
    macOS_ARM64,
    Android,
    iOS,
    Steam_Windows,
    Steam_Linux,
    Steam_macOS,

    /// <summary>
    /// A browser build: the HTML5 runtime under <c>html5/</c> plus the project's
    /// own scenes, scripts and assets, served as static files.
    /// </summary>
    /// <remarks>
    /// Added last on purpose. The editor's platform dropdown maps its selection
    /// by ordinal, so inserting a value anywhere else would silently retarget
    /// every project that had one selected.
    /// </remarks>
    Web,
}

public enum BuildConfiguration
{
    Debug,
    Development,
    Release,
}

// ---------------------------------------------------------------------------
// PlatformConfig — the full build descriptor serialised to/from JSON
// ---------------------------------------------------------------------------

/// <summary>
/// Describes everything needed to export / build the game for a given platform.
/// Serialise with <see cref="Save"/> and deserialise with <see cref="Load"/>.
/// </summary>
public class PlatformConfig
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------
    public BuildPlatform       Platform      { get; set; } = BuildPlatform.Windows_x64;
    public BuildConfiguration  Configuration { get; set; } = BuildConfiguration.Debug;

    public string AppName  { get; set; } = "MyGame";
    public string Version  { get; set; } = "1.0.0";
    public string BundleId { get; set; } = "com.studio.mygame";

    // -------------------------------------------------------------------------
    // Output
    // -------------------------------------------------------------------------
    public string OutputDirectory { get; set; } = "dist";

    // -------------------------------------------------------------------------
    // Scene configuration
    // -------------------------------------------------------------------------
    public string       StartScene { get; set; } = "Scenes/Main.scene";
    public List<string> Scenes     { get; set; } = new();

    // -------------------------------------------------------------------------
    // Steam
    // -------------------------------------------------------------------------
    public uint   SteamAppId   { get; set; } = 0;
    public uint   SteamDepotId { get; set; } = 0;
    public string SteamBranch  { get; set; } = "default";

    // -------------------------------------------------------------------------
    // Android
    // -------------------------------------------------------------------------
    public string AndroidKeystorePath     { get; set; } = "";
    public string AndroidKeystorePassword { get; set; } = "";

    // -------------------------------------------------------------------------
    // iOS
    // -------------------------------------------------------------------------
    public string IOSTeamId              { get; set; } = "";
    public string IOSProvisioningProfile { get; set; } = "";

    // -------------------------------------------------------------------------
    // Build options
    // -------------------------------------------------------------------------
    /// <summary>Include debug overlay code in the output build.</summary>
    public bool IncludeDebugOverlay { get; set; } = false;

    /// <summary>Strip comments and collapse whitespace from .js files.</summary>
    public bool MinifyScripts { get; set; } = false;

    /// <summary>Run the asset cooker on source assets before packaging.</summary>
    public bool CookAssets { get; set; } = true;

    // -------------------------------------------------------------------------
    // Serialisation
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters           = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Serialises this config to a JSON file at <paramref name="path"/>.</summary>
    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(this, _jsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Deserialises a <see cref="PlatformConfig"/> from the JSON file at <paramref name="path"/>.</summary>
    public static PlatformConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"PlatformConfig: config file not found: '{path}'");

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PlatformConfig>(json, _jsonOptions)
               ?? throw new InvalidDataException($"PlatformConfig: failed to parse '{path}'");
    }

    /// <summary>Creates a <see cref="PlatformConfig"/> with sensible defaults for the given platform.</summary>
    public static PlatformConfig Default(BuildPlatform platform)
    {
        bool isSteam = platform is BuildPlatform.Steam_Windows
                                or BuildPlatform.Steam_Linux
                                or BuildPlatform.Steam_macOS;
        bool isRelease = false; // default to Debug

        return new PlatformConfig
        {
            Platform      = platform,
            Configuration = BuildConfiguration.Debug,

            AppName   = "MyGame",
            Version   = "1.0.0",
            BundleId  = $"com.studio.mygame",

            OutputDirectory = Path.Combine("dist", platform.ToString()),

            StartScene = "Scenes/Main.scene",
            Scenes     = new List<string> { "Scenes/Main.scene" },

            // Steam defaults
            SteamAppId   = isSteam ? 480u : 0u,  // 480 = Spacewar (test app)
            SteamDepotId = isSteam ? 481u : 0u,
            SteamBranch  = "default",

            // Android defaults
            AndroidKeystorePath     = platform == BuildPlatform.Android ? "keystore.jks" : "",
            AndroidKeystorePassword = "",

            // iOS defaults
            IOSTeamId              = platform == BuildPlatform.iOS ? "XXXXXXXXXX" : "",
            IOSProvisioningProfile = platform == BuildPlatform.iOS ? "MyGame Distribution" : "",

            // Build flags
            IncludeDebugOverlay = !isRelease,
            MinifyScripts       = isRelease,
            CookAssets          = true,
        };
    }
}
