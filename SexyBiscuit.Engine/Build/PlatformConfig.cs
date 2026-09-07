using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SexyBiscuit.Engine.Build.Upload;
using SexyBiscuit.Engine.Core;

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
    // Where the project is
    // -------------------------------------------------------------------------

    /// <summary>
    /// The project folder every relative path below resolves against. Not saved: a settings
    /// file lives in its project, and the pipeline sets this from where it found the file.
    /// </summary>
    /// <remarks>
    /// The pipeline used to read <c>Assets</c>, <c>Scripts</c> and <c>Scenes</c> against the
    /// process working directory, which for the editor is wherever it was launched from — so an
    /// export from the editor staged the editor's own folder.
    /// </remarks>
    [JsonIgnore]
    public string? ProjectRoot { get; set; }

    /// <summary>The project root in force: <see cref="ProjectRoot"/>, else the engine's <see cref="ProjectPaths"/>.</summary>
    [JsonIgnore]
    public string EffectiveProjectRoot => Path.GetFullPath(ProjectRoot ?? ProjectPaths.EffectiveRoot);

    // -------------------------------------------------------------------------
    // Output
    // -------------------------------------------------------------------------

    /// <summary>
    /// Where builds go, relative to the project root. The platform's own folder is added
    /// underneath: <c>dist/Web</c>, <c>dist/macOS_ARM64</c>. A value that already ends in a
    /// platform name (older files saved <c>dist\Windows_x64</c>) is treated as that folder.
    /// </summary>
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
    // Web
    // -------------------------------------------------------------------------

    /// <summary>Write a web manifest, icons and a service worker so the web build installs to a home screen and runs offline.</summary>
    public bool WebInstallable { get; set; } = true;

    /// <summary>A PNG for the app icon, relative to the project root. Empty: a flat square in the theme colour.</summary>
    public string WebIconPath { get; set; } = "";

    /// <summary>
    /// The Darks Games catalogue slug. Set it and the web build carries the account and social
    /// layer — identity, friends, presence and Join; leave it empty and the build loads nothing
    /// from the hub.
    /// </summary>
    /// <remarks>
    /// The slug is also the token audience the hub enforces, so it has to match the catalogue
    /// entry exactly. Getting it wrong shows up as <c>presence_app_mismatch</c> on every
    /// presence update rather than as a failure to sign in.
    /// </remarks>
    public string DarksGamesSlug { get; set; } = "";

    // -------------------------------------------------------------------------
    // Upload
    // -------------------------------------------------------------------------

    /// <summary>Where builds are sent with <c>--upload</c>. Read by <c>html5/tools/upload.js</c> as well.</summary>
    public UploadSettings Upload { get; set; } = new();

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

    /// <summary>The conventional settings file name in a project folder.</summary>
    public const string FileName = "BuildSettings.json";

    /// <summary>A copy of this config aimed at another platform. The lists are shared.</summary>
    public PlatformConfig With(BuildPlatform platform)
    {
        var copy = (PlatformConfig)MemberwiseClone();
        copy.Platform = platform;
        return copy;
    }

    /// <summary>
    /// The config for a project folder: its <c>BuildSettings.json</c> when it has one, else the
    /// defaults, with the name, version and start scene read from <c>ProjectSettings.json</c>
    /// and every scene under <c>Scenes/</c> listed when the file names none.
    /// </summary>
    public static PlatformConfig ForProject(string projectRoot, BuildPlatform platform)
    {
        string root = Path.GetFullPath(projectRoot);
        string settingsFile = Path.Combine(root, FileName);

        var config = File.Exists(settingsFile) ? Load(settingsFile) : Default(platform);
        config.ProjectRoot = root;
        config.Platform    = platform;

        string projectSettings = Path.Combine(root, "ProjectSettings.json");
        if (File.Exists(projectSettings))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(projectSettings).TrimStart('\uFEFF')) as JsonObject;
                string? Read(params string[] keys)
                {
                    if (node == null) return null;
                    foreach (var key in keys)
                    {
                        var match = node.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
                        if (match is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                            return text;
                    }
                    return null;
                }

                if (!File.Exists(settingsFile) || config.AppName == "MyGame")
                    config.AppName = Read("WindowTitle", "appName") ?? Path.GetFileName(root);
                if (!File.Exists(settingsFile) || config.Version == "1.0.0")
                    config.Version = Read("Version") ?? config.Version;
                if (!File.Exists(settingsFile) || config.StartScene == "Scenes/Main.scene")
                    config.StartScene = Read("StartScene") ?? config.StartScene;
            }
            catch (JsonException) { /* the export validates it later */ }
        }
        else if (config.AppName == "MyGame")
        {
            config.AppName = Path.GetFileName(root);
        }

        string scenesDir = Path.Combine(root, "Scenes");
        if ((config.Scenes.Count == 0 || config.Scenes.SequenceEqual(new[] { "Scenes/Main.scene" })) && Directory.Exists(scenesDir))
        {
            config.Scenes = Directory.EnumerateFiles(scenesDir, "*.scene", SearchOption.AllDirectories)
                                     .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                                     .OrderBy(f => f, StringComparer.Ordinal)
                                     .ToList();
        }

        return config;
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

            OutputDirectory = "dist",

            StartScene = "Scenes/Main.scene",
            Scenes     = new List<string> { "Scenes/Main.scene" },

            // Steam defaults
            SteamAppId   = isSteam ? 480u : 0u,  // 480 = Spacewar (test app)
            SteamDepotId = isSteam ? 481u : 0u,
            SteamBranch  = "default",

            // Android defaults
            // Empty means "sign with the SDK's debug key", which is right for a playtest
            // build installed by hand. Name a keystore only to sign for Play.
            AndroidKeystorePath     = "",
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
