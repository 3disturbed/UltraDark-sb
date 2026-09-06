using System.Text;
using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.Code;

/// <summary>What a generated class should be.</summary>
public enum ClassKind
{
    Component,
    Actor,
    GameMode,
    PlayerController,
    Character,
    Tool,
}

/// <summary>Where the engine is on this machine, for the per-machine props file.</summary>
public sealed record EngineLocation(string? EngineCsproj, string? EngineBinDir, Guid? EngineMvid);

/// <summary>What <see cref="CodeProjectGenerator.Generate"/> did.</summary>
public sealed record GenerationResult(IReadOnlyList<string> Created, IReadOnlyList<string> Skipped, string CsprojPath);

/// <summary>
/// Creates the C# side of a SexyBiscuit project: a csproj at the project root, a per-machine
/// props file naming the engine, a .gitignore, and Unreal-style starter classes under
/// <c>Source/</c>. Never overwrites a file that exists unless asked.
/// </summary>
public static class CodeProjectGenerator
{
    public const string PropsFileName = "SexyBiscuit.props";

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal",
        "default","delegate","do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for",
        "foreach","goto","if","implicit","in","int","interface","internal","is","lock","long","namespace","new","null","object",
        "operator","out","override","params","private","protected","public","readonly","ref","return","sbyte","sealed","short",
        "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked",
        "unsafe","ushort","using","virtual","void","volatile","while",
    };

    // -------------------------------------------------------------------------
    // Names
    // -------------------------------------------------------------------------

    /// <summary>
    /// A project or class name as a C# identifier: "My Game 2" → MyGame2, "3D Demo" → _3DDemo,
    /// a keyword gets a suffix.
    /// </summary>
    public static string SanitiseIdentifier(string name)
    {
        var sb = new StringBuilder();
        bool upperNext = true;

        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(upperNext && char.IsLetter(c) ? char.ToUpperInvariant(c) : c);
                upperNext = false;
            }
            else
            {
                upperNext = true;
            }
        }

        string id = sb.ToString();
        if (id.Length == 0) id = "Game";
        if (char.IsDigit(id[0])) id = "_" + id;
        if (Keywords.Contains(id)) id += "Game";
        return id;
    }

    public static string FolderFor(ClassKind kind) => kind switch
    {
        ClassKind.Component => "Components",
        ClassKind.Actor     => "Actors",
        ClassKind.Tool      => "Tools",
        _                   => "Gameplay",
    };

    // -------------------------------------------------------------------------
    // Generation
    // -------------------------------------------------------------------------

    public static GenerationResult Generate(string projectRoot, string projectName, EngineLocation engine, bool overwrite = false)
    {
        string ns   = SanitiseIdentifier(projectName);
        string root = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(root);

        var created = new List<string>();
        var skipped = new List<string>();

        void Write(string relative, string content)
        {
            string full = Path.Combine(root, relative);
            if (File.Exists(full) && !overwrite)
            {
                skipped.Add(relative);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            created.Add(relative);
        }

        string csproj = ns + ".csproj";
        Write(csproj,                                   RenderCsproj(ns));
        Write(".gitignore",                             RenderGitignore());
        Write(Path.Combine("Source", "Program.cs"),     RenderProgram(ns));
        Write(Path.Combine("Source", "Gameplay", ns + "GameMode.cs"),         RenderClass(ClassKind.GameMode, ns + "GameMode", ns));
        Write(Path.Combine("Source", "Gameplay", ns + "PlayerController.cs"), RenderClass(ClassKind.PlayerController, ns + "PlayerController", ns));
        Write(Path.Combine("Source", "Gameplay", ns + "Character.cs"),        RenderClass(ClassKind.Character, ns + "Character", ns));
        Write(Path.Combine("Source", "Components", "Spinner.cs"),             RenderClass(ClassKind.Component, "Spinner", ns));
        Write(Path.Combine("Source", "Tools", "ProjectTools.cs"),             RenderClass(ClassKind.Tool, "ProjectTools", ns));

        // The props file is per machine: always refreshed, never counted as "created".
        File.WriteAllText(Path.Combine(root, PropsFileName), RenderProps(engine));

        return new GenerationResult(created, skipped, Path.Combine(root, csproj));
    }

    /// <summary>Rewrites the per-machine props file so a project moved between machines heals itself.</summary>
    public static void WriteProps(string projectRoot, EngineLocation engine)
        => File.WriteAllText(Path.Combine(projectRoot, PropsFileName), RenderProps(engine));

    // -------------------------------------------------------------------------
    // Templates
    // -------------------------------------------------------------------------

    public static string RenderCsproj(string rootNamespace) => $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <!-- Where the engine is on this machine. Written by the editor, never committed. -->
          <Import Project="{PropsFileName}" Condition="Exists('{PropsFileName}')" />

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <RollForward>LatestMajor</RollForward>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>{rootNamespace}</RootNamespace>
            <AssemblyName>{rootNamespace}</AssemblyName>
            <Configurations>Debug;Release;Development</Configurations>
            <GenerateDocumentationFile>true</GenerateDocumentationFile>
            <NoWarn>$(NoWarn);CS1591</NoWarn>
            <!-- Only Source/ is code; Assets/, Scenes/ and Scripts/ are data the game reads at runtime. -->
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <EnableDefaultNoneItems>false</EnableDefaultNoneItems>
            <!-- Full paths in diagnostics, so the editor and the assistant can open the file. -->
            <GenerateFullPaths>true</GenerateFullPaths>
            <DebugType>portable</DebugType>
            <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
            <!-- The desktop targets the build CLI publishes for. -->
            <RuntimeIdentifiers>win-x64;win-x86;linux-x64;osx-x64;osx-arm64</RuntimeIdentifiers>
          </PropertyGroup>

          <ItemGroup>
            <Compile Include="Source/**/*.cs" />
          </ItemGroup>

          <!-- Normal case: the engine source is on this machine. -->
          <ItemGroup Condition="'$(SexyBiscuitEngineProject)' != '' And Exists('$(SexyBiscuitEngineProject)')">
            <ProjectReference Include="$(SexyBiscuitEngineProject)" />
          </ItemGroup>

          <!-- Fallback: an installed editor without source. Compile against, and ship, its binaries. -->
          <ItemGroup Condition="('$(SexyBiscuitEngineProject)' == '' Or !Exists('$(SexyBiscuitEngineProject)')) And '$(SexyBiscuitEngineDir)' != ''">
            <Reference Include="SexyBiscuit.Engine">
              <HintPath>$(SexyBiscuitEngineDir)/SexyBiscuit.Engine.dll</HintPath>
            </Reference>
            <None Include="$(SexyBiscuitEngineDir)/**/*.dll;$(SexyBiscuitEngineDir)/runtimes/**"
                  Exclude="$(SexyBiscuitEngineDir)/SexyBiscuit.Editor.dll;$(SexyBiscuitEngineDir)/ImGui.NET.dll;$(SexyBiscuitEngineDir)/**/cimgui*"
                  Link="%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>

        </Project>

        """;

    /// <summary>
    /// The project the build CLI publishes for a game that has no C#: one <c>Program.cs</c>
    /// that boots the engine from the folder the binary is in. Lives under
    /// <c>.sexybiscuit/player/</c>, two levels below the project root, so the engine location
    /// props file is reached with <c>../../</c>.
    /// </summary>
    public static string RenderPlayerCsproj(string assemblyName) => $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <!-- Generated by the build CLI; rewritten on every publish. Do not edit. -->
          <Import Project="../../{PropsFileName}" Condition="Exists('../../{PropsFileName}')" />

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <RollForward>LatestMajor</RollForward>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>{assemblyName}</RootNamespace>
            <AssemblyName>{assemblyName}</AssemblyName>
            <Configurations>Debug;Release;Development</Configurations>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <EnableDefaultNoneItems>false</EnableDefaultNoneItems>
            <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
            <RuntimeIdentifiers>win-x64;win-x86;linux-x64;osx-x64;osx-arm64</RuntimeIdentifiers>
          </PropertyGroup>

          <!-- CI or a bare machine can name the engine checkout with SEXYBISCUIT_REPO. -->
          <PropertyGroup Condition="'$(SexyBiscuitEngineProject)' == '' And '$(SEXYBISCUIT_REPO)' != ''">
            <SexyBiscuitEngineProject>$(SEXYBISCUIT_REPO)/SexyBiscuit.Engine/SexyBiscuit.Engine.csproj</SexyBiscuitEngineProject>
          </PropertyGroup>

          <ItemGroup>
            <Compile Include="Program.cs" />
          </ItemGroup>

          <ItemGroup Condition="'$(SexyBiscuitEngineProject)' != '' And Exists('$(SexyBiscuitEngineProject)')">
            <ProjectReference Include="$(SexyBiscuitEngineProject)" />
          </ItemGroup>

          <ItemGroup Condition="('$(SexyBiscuitEngineProject)' == '' Or !Exists('$(SexyBiscuitEngineProject)')) And '$(SexyBiscuitEngineDir)' != ''">
            <Reference Include="SexyBiscuit.Engine">
              <HintPath>$(SexyBiscuitEngineDir)/SexyBiscuit.Engine.dll</HintPath>
            </Reference>
            <None Include="$(SexyBiscuitEngineDir)/**/*.dll;$(SexyBiscuitEngineDir)/runtimes/**"
                  Exclude="$(SexyBiscuitEngineDir)/SexyBiscuit.Editor.dll;$(SexyBiscuitEngineDir)/ImGui.NET.dll;$(SexyBiscuitEngineDir)/**/cimgui*"
                  Link="%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>

        </Project>

        """;

    /// <summary>The player's entry point: the project root is wherever the binary is, and ProjectSettings.json sits beside it.</summary>
    public static string RenderPlayerProgram(string ns) => $$"""
        // Generated by the build CLI; rewritten on every publish. Do not edit.
        using SexyBiscuit.Engine;
        using SexyBiscuit.Engine.Core;

        namespace {{ns}};

        internal static class Program
        {
            [STAThread]
            private static void Main()
            {
                // The staged scenes, scripts, assets and settings are published beside the binary.
                ProjectPaths.Root = AppContext.BaseDirectory;
                var config = EngineConfig.FromProjectSettings(Path.Combine(AppContext.BaseDirectory, "ProjectSettings.json"));
                SBEngine.Run(config);
            }
        }

        """;

    // -------------------------------------------------------------------------
    // Android head project
    //
    // Android is not a runtime identifier, so it cannot go through DesktopPublisher:
    // an APK needs its own application project with an Activity, a manifest and the
    // game shipped as Android assets. These render that head, and the build CLI writes
    // them under .sexybiscuit/android/ on every publish.
    // -------------------------------------------------------------------------

    /// <summary>The Android application project that wraps a staged game as an APK.</summary>
    /// <param name="assemblyName">The sanitised app name; also the assembly and namespace.</param>
    /// <param name="applicationId">Reverse-DNS package id, e.g. <c>app.darksgames.jake01</c>.</param>
    /// <param name="displayVersion">The game's version string, shown in Android's app info.</param>
    /// <param name="versionCode">Monotonic integer Android uses to order upgrades.</param>
    public static string RenderAndroidCsproj(string assemblyName, string applicationId, string displayVersion, int versionCode) => $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <!-- Generated by the build CLI; rewritten on every publish. Do not edit. -->
          <Import Project="../../{PropsFileName}" Condition="Exists('../../{PropsFileName}')" />

          <PropertyGroup>
            <!-- net10.0, not net8.0: the installed android workload is the .NET 10 one, and
                 asking it for net8.0-android packs gets an end-of-life error and a compile
                 SDK that falls back to API 21. -->
            <TargetFramework>net10.0-android</TargetFramework>
            <SupportedOSPlatformVersion>24</SupportedOSPlatformVersion>
            <OutputType>Exe</OutputType>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>{assemblyName}</RootNamespace>
            <AssemblyName>{assemblyName}</AssemblyName>
            <ApplicationId>{applicationId}</ApplicationId>
            <ApplicationVersion>{versionCode}</ApplicationVersion>
            <ApplicationDisplayVersion>{displayVersion}</ApplicationDisplayVersion>
            <Configurations>Debug;Release;Development</Configurations>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <AndroidPackageFormat>apk</AndroidPackageFormat>
            <!-- A playtest build is side-loaded from a download, never installed through
                 Play, so the debug signing key the SDK provides is the right one. -->
            <AndroidUseAapt2>true</AndroidUseAapt2>
            <AndroidEnableProfiledAot>false</AndroidEnableProfiledAot>
            <!-- The engine only grows its Android target framework when asked. -->
            <SexyBiscuitAndroid>true</SexyBiscuitAndroid>
          </PropertyGroup>

          <PropertyGroup Condition="'$(SexyBiscuitEngineProject)' == '' And '$(SEXYBISCUIT_REPO)' != ''">
            <SexyBiscuitEngineProject>$(SEXYBISCUIT_REPO)/SexyBiscuit.Engine/SexyBiscuit.Engine.csproj</SexyBiscuitEngineProject>
          </PropertyGroup>

          <ItemGroup>
            <Compile Include="MainActivity.cs" />
            <AndroidResource Include="Resources/**/*.png" />
          </ItemGroup>

          <ItemGroup>
            <ProjectReference Include="$(SexyBiscuitEngineProject)" />
          </ItemGroup>

          <!-- The staged game rides inside the APK and is unpacked on first run. -->
          <ItemGroup>
            <AndroidAsset Include="content/**/*" Link="%(RecursiveDir)%(Filename)%(Extension)" />
          </ItemGroup>

        </Project>

        """;

    /// <summary>The Activity that hosts the engine and unpacks the game out of the APK.</summary>
    public static string RenderAndroidActivity(string ns, string label) => $$"""
        // Generated by the build CLI; rewritten on every publish. Do not edit.
        using Android.App;
        using Android.Content.PM;
        using Android.OS;
        using Android.Views;
        using Microsoft.Xna.Framework;
        using SexyBiscuit.Engine;
        using SexyBiscuit.Engine.Core;

        namespace {{ns}};

        [Activity(
            Label = "{{label}}",
            MainLauncher = true,
            Icon = "@mipmap/ic_launcher",
            AlwaysRetainTaskState = true,
            LaunchMode = LaunchMode.SingleInstance,
            ScreenOrientation = ScreenOrientation.SensorLandscape,
            ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.Keyboard
                                 | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenSize
                                 | ConfigChanges.UiMode)]
        public class MainActivity : AndroidGameActivity
        {
            private SBEngine? _game;
            private View? _view;

            protected override void OnCreate(Bundle? savedInstanceState)
            {
                base.OnCreate(savedInstanceState);

                // The engine reads its project off the filesystem, and assets inside an APK
                // are not files. Unpacking once into app-private storage and pointing
                // ProjectPaths there keeps every File.ReadAllText in the engine working as
                // it does on desktop, rather than threading a stream provider through all
                // of it for one platform.
                string root = System.IO.Path.Combine(FilesDir!.AbsolutePath, "project");
                ExtractAssets(root);
                ProjectPaths.Root = root;

                var config = EngineConfig.FromProjectSettings(
                    System.IO.Path.Combine(root, "ProjectSettings.json"));

                _game = new SBEngine(config);
                _view = (View?)_game.Services.GetService(typeof(View));
                SetContentView(_view);

                // SBEngine declares a static Run(config) for desktop, which hides Game.Run();
                // on Android the activity owns the loop and needs the instance one.
                ((Game)_game).Run();
            }

            /// <summary>
            /// Copies the packaged project out of the APK, once per version. The stamp file is
            /// what makes it once: re-copying on every launch would add seconds to startup for
            /// a build that cannot have changed.
            /// </summary>
            private void ExtractAssets(string root)
            {
                string stamp = System.IO.Path.Combine(root, ".unpacked");
                string version = PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName ?? "0";

                if (System.IO.File.Exists(stamp) && System.IO.File.ReadAllText(stamp) == version) return;

                if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
                System.IO.Directory.CreateDirectory(root);

                CopyAssetDirectory("", root);
                System.IO.File.WriteAllText(stamp, version);
            }

            private void CopyAssetDirectory(string assetPath, string targetDirectory)
            {
                string[] entries = Assets!.List(assetPath) ?? [];

                foreach (string entry in entries)
                {
                    string child = assetPath.Length == 0 ? entry : $"{assetPath}/{entry}";

                    if ((Assets.List(child) ?? []).Length > 0)
                    {
                        string directory = System.IO.Path.Combine(targetDirectory, entry);
                        System.IO.Directory.CreateDirectory(directory);
                        CopyAssetDirectory(child, directory);
                        continue;
                    }

                    // A file, or an empty directory: the asset manager cannot tell them
                    // apart, so a failed open is the answer.
                    try
                    {
                        using var source = Assets.Open(child);
                        using var destination = System.IO.File.Create(
                            System.IO.Path.Combine(targetDirectory, entry));
                        source.CopyTo(destination);
                    }
                    catch (Java.IO.FileNotFoundException)
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(targetDirectory, entry));
                    }
                }
            }
        }

        """;

    public static string RenderProps(EngineLocation engine)
    {
        string engineProject = engine.EngineCsproj ?? "";
        string engineDir     = engine.EngineBinDir ?? "";
        string mvid          = engine.EngineMvid?.ToString() ?? "";

        return $"""
            <Project>
              <!-- Generated by the SexyBiscuit editor for this machine. Do not commit; it is rewritten on open. -->
              <PropertyGroup>
                <SexyBiscuitEngineProject Condition="'$(SexyBiscuitEngineProject)' == ''">{Escape(engineProject)}</SexyBiscuitEngineProject>
                <SexyBiscuitEngineDir Condition="'$(SexyBiscuitEngineDir)' == ''">{Escape(engineDir)}</SexyBiscuitEngineDir>
                <SexyBiscuitEditorBuildId>{mvid}</SexyBiscuitEditorBuildId>
              </PropertyGroup>
              <!-- CI or another machine can point at an engine checkout with the SEXYBISCUIT_ENGINE variable. -->
              <PropertyGroup Condition="'$(SEXYBISCUIT_ENGINE)' != '' And '$(SexyBiscuitEngineProject)' == ''">
                <SexyBiscuitEngineProject>$(SEXYBISCUIT_ENGINE)/SexyBiscuit.Engine/SexyBiscuit.Engine.csproj</SexyBiscuitEngineProject>
              </PropertyGroup>
              <!-- The game compiles against the engine binary the editor runs, never against the engine
                   project's own output: an engine built separately (with -warnaserror, say) has a different
                   build id, and the editor refuses to load a game compiled against it. The engine is therefore
                   never built through this project; the editor's build is the engine. -->
              <PropertyGroup Condition="'$(SexyBiscuitEngineDir)' != '' And '$(BuildProjectReferences)' == ''">
                <BuildProjectReferences>false</BuildProjectReferences>
              </PropertyGroup>
              <Target Name="SexyBiscuitPinEngineReference" BeforeTargets="AssignProjectConfiguration;ResolveProjectReferences"
                      Condition="'$(SexyBiscuitEngineDir)' != '' And Exists('$(SexyBiscuitEngineDir)/SexyBiscuit.Engine.dll')">
                <ItemGroup>
                  <ProjectReference Condition="'%(Filename)%(Extension)' == 'SexyBiscuit.Engine.csproj'">
                    <AdditionalProperties>OutDir=$(SexyBiscuitEngineDir)/</AdditionalProperties>
                  </ProjectReference>
                </ItemGroup>
              </Target>
            </Project>

            """;
    }

    public static string RenderGitignore() => """
        bin/
        obj/
        SexyBiscuit.props
        .sexybiscuit/
        *.user
        .DS_Store

        """;

    public static string RenderProgram(string rootNamespace) => $$"""
        using SexyBiscuit.Engine;

        namespace {{rootNamespace}};

        // The standalone game. The editor never runs this: it loads the built assembly for the
        // classes inside it and leaves Main alone. Keep type-load side effects out of this file.
        internal static class Program
        {
            [STAThread]
            private static void Main()
            {
                // ProjectSettings.json is the same file the editor reads. Its StartScene is loaded
                // once the graphics device exists.
                var config = EngineConfig.FromProjectSettings("ProjectSettings.json");
                SBEngine.Run(config);
            }
        }

        """;

    public static string RenderClass(ClassKind kind, string className, string rootNamespace)
    {
        string name = SanitiseIdentifier(className);
        string ns   = SanitiseIdentifier(rootNamespace);

        return kind switch
        {
            ClassKind.Component => $$"""
                using Microsoft.Xna.Framework;
                using SexyBiscuit.Engine.Core;

                namespace {{ns}}.Components;

                /// <summary>
                /// Spins its actor around the Y axis. Add it to any 3D actor: DegreesPerSecond shows up
                /// in the Details panel and saves with the scene.
                /// </summary>
                /// <remarks>
                /// Public read/write properties of these types are editable and saved: numbers, bools,
                /// strings, enums, Vector2/3/4, Quaternion, Color. Lifecycle: Awake (attached), Start
                /// (first frame), Update(dt) every frame, OnDestroy.
                /// </remarks>
                public class {{name}} : Component
                {
                    public float DegreesPerSecond { get; set; } = 90f;

                    private Transform3D? _transform;

                    public override void Start()
                    {
                        _transform = Actor.GetComponent<Transform3D>();
                    }

                    public override void Update(float dt)
                    {
                        if (_transform == null) return;

                        var turn = Quaternion.CreateFromAxisAngle(Vector3.Up, MathHelper.ToRadians(DegreesPerSecond * dt));
                        _transform.Rotation = turn * _transform.Rotation;
                    }
                }

                """,

            ClassKind.Actor => $$"""
                using SexyBiscuit.Engine.Core;

                namespace {{ns}}.Actors;

                /// <summary>
                /// An actor with its own behaviour. Place it from the Place Actors palette (Project
                /// category) or with spawn_actor class="{{name}}"; it saves with the scene by class name.
                /// </summary>
                public class {{name}} : Actor
                {
                    public {{name}}() : base("{{name}}")
                    {
                        // Components added here are restored, not duplicated, when the scene loads.
                        AddComponent<Transform3D>();
                    }

                    protected override void OnStart()
                    {
                    }

                    protected override void Update(float dt)
                    {
                    }
                }

                """,

            ClassKind.GameMode => $$"""
                using SexyBiscuit.Engine.Gameplay;

                namespace {{ns}};

                /// <summary>
                /// Match rules for {{ns}}: which pawn and controller each player gets, spawning, scoring.
                /// One of these in the scene makes Play spawn a player at a Player Start.
                /// </summary>
                public class {{name}} : GameMode
                {
                    public {{name}}()
                    {
                        PawnFactory             = () => new {{ns}}Character();
                        PlayerControllerFactory = () => new {{ns}}PlayerController();
                    }
                }

                """,

            ClassKind.PlayerController => $$"""
                using Microsoft.Xna.Framework;
                using SexyBiscuit.Engine.Core;
                using SexyBiscuit.Engine.Gameplay;
                using SexyBiscuit.Engine.Input;

                namespace {{ns}};

                /// <summary>
                /// Turns input into movement for the possessed pawn. MoveX, MoveY and Jump are the
                /// engine's default actions: WASD and Space, or the left stick and A.
                /// </summary>
                public class {{name}} : PlayerController
                {
                    protected override void SetupInput(InputManager input)
                    {
                        // Bind extra actions here, e.g. input.Actions.Bind(...).
                    }

                    protected override void OnPlayerTick(Pawn pawn, InputManager input, float dt)
                    {
                        var transform = pawn.GetComponent<Transform3D>();
                        var forward   = transform?.Forward ?? -Vector3.UnitZ;
                        var right     = transform?.Right   ?? Vector3.UnitX;

                        // Move on the ground plane regardless of where the pawn is looking.
                        forward.Y = 0f;
                        right.Y   = 0f;
                        if (forward.LengthSquared() > 0f) forward.Normalize();
                        if (right.LengthSquared()   > 0f) right.Normalize();

                        pawn.AddMovementInput(forward, input.GetAxis("MoveY"));
                        pawn.AddMovementInput(right,   input.GetAxis("MoveX"));

                        if (input.IsPressed("Jump") && pawn is Character character)
                            character.Jump();
                    }
                }

                """,

            ClassKind.Character => $$"""
                using SexyBiscuit.Engine.Gameplay;

                namespace {{ns}};

                /// <summary>
                /// The player's pawn: a walking character with a capsule controller. Tune the speeds
                /// here or in the Details panel once one is placed.
                /// </summary>
                public class {{name}} : Character
                {
                    public {{name}}() : base("Player")
                    {
                        WalkSpeed        = 6f;
                        OrientToMovement = true;
                    }
                }

                """,

            ClassKind.Tool => $$"""
                using SexyBiscuit.Engine.Mcp;

                namespace {{ns}}.Tools;

                /// <summary>
                /// Project-specific MCP tools. Every public static [McpTool] method here becomes a tool
                /// the assistant can call (prefixed game_) after the next reload_game_code. Tools run
                /// on the game thread and may use the scene freely; throw McpToolException to report
                /// a problem the assistant can act on.
                /// </summary>
                public static class {{name}}
                {
                    [McpTool("hello", "Example project tool: returns a greeting. Copy this pattern to expose game-specific operations.")]
                    public static string Hello([McpParam("Who to greet")] string name = "world")
                        => $"Hello, {name}, from {{ns}}.";
                }

                """,

            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>A file name for a class of a kind, under its conventional folder.</summary>
    public static string RelativePathFor(ClassKind kind, string className)
        => Path.Combine("Source", FolderFor(kind), SanitiseIdentifier(className) + ".cs");

    /// <summary>True when the text is a plausible C# identifier.</summary>
    public static bool IsIdentifier(string text) => Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_]*$");
}
