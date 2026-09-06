using System.Numerics;
using System.Text;
using ImGuiNET;
using SexyBiscuit.Engine.Build;
using SexyBiscuit.Engine.Build.Upload;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Build Settings panel — configure platform, scenes, Steam, and trigger export.
/// </summary>
public sealed class BuildSettingsPanel
{
    // -------------------------------------------------------------------------
    // Config
    // -------------------------------------------------------------------------
    private PlatformConfig _config = PlatformConfig.Default(BuildPlatform.Windows_x64);

    // String buffers
    private byte[] _appNameBuf    = new byte[128];
    private byte[] _versionBuf    = new byte[64];
    private byte[] _bundleIdBuf   = new byte[128];
    private byte[] _outputDirBuf  = new byte[512];
    private byte[] _startSceneBuf = new byte[512];

    // Steam buffers
    private byte[] _steamBranchBuf = new byte[64];

    // Publish buffers
    private byte[] _appSlugBuf      = new byte[80];
    private string _notesText       = "";
    private byte[] _requirementsBuf = new byte[256];

    // Publish state
    private bool               _publishing;
    private string             _publishStatus = "";
    private List<UploadResult> _published     = new();
    private BuildReport?       _publishReport;
    private DateTime           _reportChecked = DateTime.MinValue;

    // Scene list editing
    private int  _selectedSceneIdx = -1;
    private byte[] _sceneAddBuf    = new byte[512];

    // Build state
    private bool   _building;
    private string _buildStatus = "";
    private bool   _showBuildLog;
    private List<string> _buildLog    = new();
    private List<string> _buildErrors = new();

    // Config file path for load/save. The project's own BuildSettings.json, so the CLI, the
    // MCP tools and this panel all read and write one file.
    private byte[] _configPathBuf = new byte[512];
    private string _configPath    = PlatformConfig.FileName;
    private string _loadedFor     = "";

    // -------------------------------------------------------------------------
    // Constructor — initialise buffers from default config
    // -------------------------------------------------------------------------

    public BuildSettingsPanel()
    {
        SyncBuffersFromConfig();
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw()
    {
        if (!ImGui.Begin("Build Settings"))
        {
            ImGui.End();
            return;
        }

        SyncToOpenProject();
        DrawConfigLoadSave();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##BuildTabs"))
        {
            if (ImGui.BeginTabItem("General"))
            {
                DrawGeneralTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Scenes"))
            {
                DrawScenesTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Publish"))
            {
                DrawPublishTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Steam"))
            {
                DrawSteamTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Build Log"))
            {
                DrawBuildLogTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.Separator();
        DrawBuildButtons();

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Config load / save
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads the open project's <c>BuildSettings.json</c> the first time the panel is drawn for
    /// that project. The panel used to read a file in the editor's working directory, so nothing
    /// typed here was ever seen by the build CLI or the MCP tools.
    /// </summary>
    private void SyncToOpenProject()
    {
        string root = EditorState.ProjectPath;
        if (root == _loadedFor) return;
        _loadedFor = root;

        try
        {
            _config = PlatformConfig.ForProject(root, _config.Platform);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Build settings: {ex.Message}", LogLevel.Warning);
            _config = PlatformConfig.Default(_config.Platform);
            _config.ProjectRoot = root;
        }

        _configPath    = Path.Combine(root, PlatformConfig.FileName);
        _publishReport = null;
        _reportChecked = DateTime.MinValue;
        _published     = new List<UploadResult>();
        SyncBuffersFromConfig();
    }

    private void DrawConfigLoadSave()
    {
        ImGui.Text("Config:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280f);
        EncodeToBuffer(_configPath, _configPathBuf);
        if (ImGui.InputText("##ConfigPath", _configPathBuf, (uint)_configPathBuf.Length))
            _configPath = DecodeBuffer(_configPathBuf);

        ImGui.SameLine();
        if (ImGui.Button("Load##cfg"))
        {
            try
            {
                _config = PlatformConfig.Load(_configPath);
                SyncBuffersFromConfig();
                ConsoleLog.Add($"Loaded build config from '{_configPath}'.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Failed to load config: {ex.Message}", LogLevel.Error);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Save##cfg"))
        {
            SyncConfigFromBuffers();
            try
            {
                _config.Save(_configPath);
                ConsoleLog.Add($"Saved build config to '{_configPath}'.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Failed to save config: {ex.Message}", LogLevel.Error);
            }
        }
    }

    // -------------------------------------------------------------------------
    // General tab
    // -------------------------------------------------------------------------

    private void DrawGeneralTab()
    {
        ImGui.Spacing();

        // Platform
        ImGui.Text("Platform");
        ImGui.SameLine(120f);
        ImGui.SetNextItemWidth(200f);
        var platforms = Enum.GetNames<BuildPlatform>();
        int platIdx   = (int)_config.Platform;
        if (ImGui.Combo("##Platform", ref platIdx, platforms, platforms.Length))
            _config.Platform = (BuildPlatform)platIdx;

        // Configuration
        ImGui.Text("Configuration");
        ImGui.SameLine(120f);
        ImGui.SetNextItemWidth(200f);
        var configs  = Enum.GetNames<BuildConfiguration>();
        int cfgIdx   = (int)_config.Configuration;
        if (ImGui.Combo("##Config", ref cfgIdx, configs, configs.Length))
            _config.Configuration = (BuildConfiguration)cfgIdx;

        ImGui.Separator();

        // App identity
        DrawTextRow("App Name",   _appNameBuf,   120f, s => _config.AppName  = s);
        DrawTextRow("Version",    _versionBuf,    120f, s => _config.Version  = s);
        DrawTextRow("Bundle ID",  _bundleIdBuf,   120f, s => _config.BundleId = s);

        ImGui.Separator();

        // Output directory
        ImGui.Text("Output Dir");
        ImGui.SameLine(120f);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 70f);
        if (ImGui.InputText("##OutDir", _outputDirBuf, (uint)_outputDirBuf.Length))
            _config.OutputDirectory = DecodeBuffer(_outputDirBuf);
        ImGui.SameLine();
        if (ImGui.Button("Browse"))
        {
            FileDialog.PickFolder("Select Output Directory", _config.OutputDirectory, chosen =>
            {
                _config.OutputDirectory = chosen;
                EncodeToBuffer(_config.OutputDirectory, _outputDirBuf);
            });
        }

        ImGui.Separator();

        // Build flags
        bool cookAssets = _config.CookAssets;
        if (ImGui.Checkbox("Cook Assets",        ref cookAssets)) _config.CookAssets = cookAssets;
        bool minify = _config.MinifyScripts;
        if (ImGui.Checkbox("Minify Scripts",     ref minify))     _config.MinifyScripts = minify;
        bool debugOverlay = _config.IncludeDebugOverlay;
        if (ImGui.Checkbox("Include Debug Overlay", ref debugOverlay)) _config.IncludeDebugOverlay = debugOverlay;
    }

    // -------------------------------------------------------------------------
    // Scenes tab
    // -------------------------------------------------------------------------

    private void DrawScenesTab()
    {
        ImGui.Spacing();
        ImGui.Text("Start Scene:");
        ImGui.SameLine(90f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##StartScene", _startSceneBuf, (uint)_startSceneBuf.Length))
            _config.StartScene = DecodeBuffer(_startSceneBuf);

        ImGui.Separator();
        ImGui.Text("Scene List:");

        // List box
        ImGui.BeginChild("##SceneList", new Vector2(0f, 160f), ImGuiChildFlags.Border);
        for (int i = 0; i < _config.Scenes.Count; i++)
        {
            bool sel = i == _selectedSceneIdx;
            if (ImGui.Selectable(_config.Scenes[i], sel))
                _selectedSceneIdx = i;
        }
        ImGui.EndChild();

        // Reorder / remove
        if (ImGui.Button("Up") && _selectedSceneIdx > 0)
        {
            (_config.Scenes[_selectedSceneIdx], _config.Scenes[_selectedSceneIdx - 1]) =
                (_config.Scenes[_selectedSceneIdx - 1], _config.Scenes[_selectedSceneIdx]);
            _selectedSceneIdx--;
        }
        ImGui.SameLine();
        if (ImGui.Button("Down") && _selectedSceneIdx >= 0 && _selectedSceneIdx < _config.Scenes.Count - 1)
        {
            (_config.Scenes[_selectedSceneIdx], _config.Scenes[_selectedSceneIdx + 1]) =
                (_config.Scenes[_selectedSceneIdx + 1], _config.Scenes[_selectedSceneIdx]);
            _selectedSceneIdx++;
        }
        ImGui.SameLine();
        if (ImGui.Button("Remove") && _selectedSceneIdx >= 0 && _selectedSceneIdx < _config.Scenes.Count)
        {
            _config.Scenes.RemoveAt(_selectedSceneIdx);
            _selectedSceneIdx = Math.Min(_selectedSceneIdx, _config.Scenes.Count - 1);
        }

        // Add scene
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 50f);
        ImGui.InputText("##SceneAdd", _sceneAddBuf, (uint)_sceneAddBuf.Length);
        ImGui.SameLine();
        if (ImGui.Button("Add"))
        {
            var scenePath = DecodeBuffer(_sceneAddBuf);
            if (!string.IsNullOrWhiteSpace(scenePath) && !_config.Scenes.Contains(scenePath))
            {
                _config.Scenes.Add(scenePath);
                _sceneAddBuf = new byte[512];
            }
        }

        // Drag-in from asset browser
        if (ImGui.BeginDragDropTarget())
        {
            unsafe
            {
                var payload = ImGui.AcceptDragDropPayload("ASSET_PATH");
                if (payload.NativePtr != null)
                {
                    int dataSize = payload.DataSize;
                    byte* data   = (byte*)payload.Data;
                    var path     = Encoding.UTF8.GetString(data, dataSize).TrimEnd('\0');
                    if (!_config.Scenes.Contains(path))
                        _config.Scenes.Add(path);
                }
            }
            ImGui.EndDragDropTarget();
        }
    }

    // -------------------------------------------------------------------------
    // Steam tab
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Publish tab
    // -------------------------------------------------------------------------

    /// <summary>
    /// Publishing a finished build to DarksGames. The token is never shown or stored here: it is
    /// read at publish time from the environment or the token file, and this tab only reports
    /// whether one was found.
    /// </summary>
    private void DrawPublishTab()
    {
        var status = UploadTargets.DescribeToken(_config.Upload);
        if (status.Found)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), $"Token found in {status.Source}.");
            if (status.Hint != null) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), status.Hint);
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "No publish token.");
            ImGui.TextDisabled(status.Hint ?? "");
        }

        ImGui.Separator();

        ImGui.Text("App slug");
        ImGui.SameLine(120f);
        ImGui.SetNextItemWidth(240f);
        if (ImGui.InputText("##AppSlug", _appSlugBuf, (uint)_appSlugBuf.Length))
            _config.Upload.AppSlug = DecodeBuffer(_appSlugBuf).Trim();
        ImGui.SameLine();
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(_config.Upload.AppSlug)
            ? $"(defaults to '{ExportPipeline.Slugify(_config.AppName)}')"
            : "must match the game's slug in the catalogue");

        ImGui.Text("Publishing as");
        ImGui.SameLine(120f);
        ImGui.TextDisabled($"{_config.AppName} {_config.Version} ({_config.Configuration})");

        ImGui.Text("Channel");
        ImGui.SameLine(120f);
        ImGui.SetNextItemWidth(160f);
        string[] channels = DarksGamesUploadTarget.Channels;
        int channelIdx = Array.FindIndex(channels, c => string.Equals(c, _config.Upload.Channel, StringComparison.OrdinalIgnoreCase));
        if (channelIdx < 0) channelIdx = 0;
        if (ImGui.Combo("##Channel", ref channelIdx, channels, channels.Length))
            _config.Upload.Channel = channels[channelIdx];
        ImGui.SameLine();
        ImGui.TextDisabled(channelIdx switch { 0 => "nightlies", 1 => "playtest candidates", _ => "public releases" });

        ImGui.Text("Requirements");
        ImGui.SameLine(120f);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##Requirements", _requirementsBuf, (uint)_requirementsBuf.Length))
            _config.Upload.Requirements = DecodeBuffer(_requirementsBuf);

        ImGui.Text("Notes");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextMultiline("##Notes", ref _notesText, 4000, new Vector2(-1f, 70f)))
            _config.Upload.Notes = _notesText;
        ImGui.TextDisabled("A line naming the runtime, configuration and commit is always appended.");

        bool replace = _config.Upload.Replace;
        if (ImGui.Checkbox("Replace a build with the same version and platform", ref replace)) _config.Upload.Replace = replace;
        bool visible = _config.Upload.Publish;
        if (ImGui.Checkbox("Visible on the downloads page", ref visible)) _config.Upload.Publish = visible;

        ImGui.Separator();
        DrawPublishArtifacts(status.Found);
        DrawPublishResults();
    }

    /// <summary>The archives the last build left, and the button that sends them.</summary>
    private void DrawPublishArtifacts(bool hasToken)
    {
        var report = LoadPublishReport();
        var artifacts = report == null
            ? new List<UploadArtifact>()
            : BuildPublisher.ArtifactsFrom(report, _config.Upload, out _);

        if (report == null)
        {
            ImGui.TextDisabled("No build report yet. Build with packaging on, or run the sbengine CLI.");
        }
        else if (artifacts.Count == 0)
        {
            ImGui.TextDisabled("The last build produced nothing publishable. Only native builds are published.");
        }
        else
        {
            ImGui.Text($"Ready to publish ({report.Version}):");
            foreach (var artifact in artifacts)
            {
                string platform = DarksGamesUploadTarget.PlatformFor(artifact.Platform, _config.Upload.PlatformMap) ?? "other";
                ImGui.BulletText($"{platform,-8} {Path.GetFileName(artifact.FilePath)}  ({artifact.Bytes / 1048576.0:F1} MB)");
            }
        }

        ImGui.Spacing();

        bool canPublish = hasToken && artifacts.Count > 0 && !_publishing && !_building;
        if (!canPublish) ImGui.BeginDisabled();
        if (ImGui.Button("Publish to DarksGames", new Vector2(200f, 30f)))
        {
            SyncConfigFromBuffers();
            StartPublish(artifacts);
        }
        if (!canPublish) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Refresh", new Vector2(80f, 30f))) _reportChecked = DateTime.MinValue;

        if (_publishStatus.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_publishStatus);
        }
    }

    /// <summary>What came back, with the links a person actually needs.</summary>
    private void DrawPublishResults()
    {
        if (_published.Count == 0) return;

        ImGui.Separator();
        foreach (var result in _published)
        {
            if (result.Success)
            {
                ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), $"{result.Platform}: {(result.Replaced ? "replaced" : "published")}{(result.Published ? "" : " (hidden)")}");
                if (result.Url != null)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Copy link##{result.Platform}")) ImGui.SetClipboardText(result.Url);
                    ImGui.TextDisabled(result.Url);
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"{result.Platform}: {result.Error}");
            }
        }
    }

    /// <summary>The build report beside the output folder, re-read at most once a second.</summary>
    private BuildReport? LoadPublishReport()
    {
        if ((DateTime.UtcNow - _reportChecked).TotalSeconds < 1) return _publishReport;
        _reportChecked = DateTime.UtcNow;

        try
        {
            string file = Path.Combine(ExportPipeline.ResolveOutputRoot(_config), BuildReport.FileName);
            _publishReport = File.Exists(file) ? BuildReport.Load(file) : null;
        }
        catch (Exception)
        {
            _publishReport = null;
        }
        return _publishReport;
    }

    private void StartPublish(List<UploadArtifact> artifacts)
    {
        var target = UploadTargets.FromConfig(_config.Upload, out var why);
        if (target == null)
        {
            _publishStatus = why ?? "no publish target";
            ConsoleLog.Add($"Publish: {why}", LogLevel.Error);
            return;
        }

        _publishing    = true;
        _published     = new List<UploadResult>();
        _publishStatus = "Publishing...";

        var config   = _config;
        var metadata = BuildPublisher.MetadataFor(config, config.Version, GitInfo.TryReadHeadSha(config.EffectiveProjectRoot));
        var progress = new Progress<string>(line => ConsoleLog.Add("[publish] " + line, LogLevel.Info));

        Task.Run(async () =>
        {
            try
            {
                var results = await BuildPublisher.PublishAsync(target, artifacts, metadata, config.Upload, progress);
                int ok = results.Count(r => r.Success);
                _published     = results;
                _publishStatus = ok == results.Count ? $"Published {ok} build(s)." : $"Published {ok} of {results.Count}.";
                ConsoleLog.Add($"[publish] {_publishStatus}", ok == results.Count ? LogLevel.Info : LogLevel.Warning);
            }
            catch (Exception ex)
            {
                _publishStatus = $"Publish failed: {ex.Message}";
                ConsoleLog.Add(_publishStatus, LogLevel.Error);
            }
            finally
            {
                _publishing = false;
            }
        });
    }

    private void DrawSteamTab()
    {
        ImGui.Spacing();

        uint appId = _config.SteamAppId;
        ImGui.Text("App ID");
        ImGui.SameLine(90f);
        ImGui.SetNextItemWidth(120f);
        int appIdInt = (int)appId;
        if (ImGui.DragInt("##AppId", ref appIdInt, 1f, 0, int.MaxValue))
            _config.SteamAppId = (uint)Math.Max(0, appIdInt);

        uint depotId = _config.SteamDepotId;
        ImGui.Text("Depot ID");
        ImGui.SameLine(90f);
        ImGui.SetNextItemWidth(120f);
        int depotIdInt = (int)depotId;
        if (ImGui.DragInt("##DepotId", ref depotIdInt, 1f, 0, int.MaxValue))
            _config.SteamDepotId = (uint)Math.Max(0, depotIdInt);

        ImGui.Text("Branch");
        ImGui.SameLine(90f);
        ImGui.SetNextItemWidth(160f);
        EncodeToBuffer(_config.SteamBranch, _steamBranchBuf);
        EncodeToBuffer(_config.Upload.AppSlug ?? "", _appSlugBuf);
        _notesText = _config.Upload.Notes;
        EncodeToBuffer(_config.Upload.Requirements, _requirementsBuf);
        if (ImGui.InputText("##Branch", _steamBranchBuf, (uint)_steamBranchBuf.Length))
            _config.SteamBranch = DecodeBuffer(_steamBranchBuf);

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("Upload to Steam", new Vector2(160f, 28f)))
        {
            SyncConfigFromBuffers();
            ConsoleLog.Add($"Steam upload initiated for App ID {_config.SteamAppId} branch '{_config.SteamBranch}'.", LogLevel.Info);
            // SteamCmd upload would be invoked here via Process.Start
            TriggerSteamUpload();
        }
    }

    // -------------------------------------------------------------------------
    // Build log tab
    // -------------------------------------------------------------------------

    private void DrawBuildLogTab()
    {
        if (_building)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0f, 1f), "Building...");
        }
        else if (!string.IsNullOrEmpty(_buildStatus))
        {
            bool ok = _buildErrors.Count == 0;
            ImGui.TextColored(ok ? new Vector4(0.3f, 1f, 0.3f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f),
                _buildStatus);
        }

        ImGui.Separator();
        ImGui.BeginChild("##BuildLog", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);

        foreach (var line in _buildLog)
        {
            bool isErr = line.StartsWith("ERROR");
            ImGui.TextColored(isErr ? new Vector4(1f, 0.3f, 0.3f, 1f) : new Vector4(0.85f, 0.85f, 0.85f, 1f),
                line);
        }

        ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
    }

    // -------------------------------------------------------------------------
    // Build buttons
    // -------------------------------------------------------------------------

    private void DrawBuildButtons()
    {
        bool canBuild = !_building;

        if (!canBuild) ImGui.BeginDisabled();

        if (ImGui.Button("Build", new Vector2(120f, 30f)))
        {
            SyncConfigFromBuffers();
            StartBuild(runAfter: false);
        }
        ImGui.SameLine();
        if (ImGui.Button("Build && Run", new Vector2(120f, 30f)))
        {
            SyncConfigFromBuffers();
            StartBuild(runAfter: true);
        }

        if (!canBuild) ImGui.EndDisabled();
    }

    // -------------------------------------------------------------------------
    // Build implementation
    // -------------------------------------------------------------------------

    private void StartBuild(bool runAfter)
    {
        _building   = true;
        _buildLog   = new List<string>();
        _buildErrors = new List<string>();
        _buildStatus = "Building...";

        // The export resolves every path against the open project, never the editor's
        // working directory — which is what used to stage the editor's own assets.
        _config.ProjectRoot = EditorState.ProjectPath;

        Task.Run(() =>
        {
            try
            {
                var pipeline = new ExportPipeline { Output = null };
                var result   = pipeline.ExportAsync(_config, new ExportOptions { Publish = runAfter, Package = true })
                                       .GetAwaiter().GetResult();

                _buildLog    = result.Log;
                _buildErrors = result.Errors;
                _building    = false;
                _buildStatus = result.Success
                    ? $"Build succeeded in {result.Duration.TotalSeconds:F2}s  ->  {result.OutputPath}"
                    : $"Build FAILED in {result.Duration.TotalSeconds:F2}s  ({result.Errors.Count} error(s))";

                ConsoleLog.Add(_buildStatus, result.Success ? LogLevel.Info : LogLevel.Error);

                if (result.Success && runAfter)
                    LaunchBuild(result.ExecutablePath ?? result.OutputPath);
            }
            catch (Exception ex)
            {
                _building    = false;
                _buildStatus = $"Build exception: {ex.Message}";
                ConsoleLog.Add(_buildStatus, LogLevel.Error);
            }
        });
    }

    private static void LaunchBuild(string outputPath)
    {
        try
        {
            // A published binary is passed straight in; otherwise look for one in the folder.
            var exes = File.Exists(outputPath)
                ? new[] { outputPath }
                : Directory.GetFiles(outputPath, "*.exe", SearchOption.TopDirectoryOnly);
            if (exes.Length > 0)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = exes[0],
                    UseShellExecute = true,
                    WorkingDirectory = outputPath,
                });
                ConsoleLog.Add($"Launched: {exes[0]}", LogLevel.Info);
            }
            else
            {
                ConsoleLog.Add($"No executable found in '{outputPath}'.", LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Launch failed: {ex.Message}", LogLevel.Error);
        }
    }

    private static void TriggerSteamUpload()
    {
        // Looks for steamcmd in PATH; user is responsible for having it installed.
        ConsoleLog.Add("Steam upload: ensure steamcmd is in PATH and VDF was generated.", LogLevel.Warning);
    }

    // -------------------------------------------------------------------------
    // Buffer sync helpers
    // -------------------------------------------------------------------------

    private void SyncBuffersFromConfig()
    {
        EncodeToBuffer(_config.AppName,         _appNameBuf);
        EncodeToBuffer(_config.Version,         _versionBuf);
        EncodeToBuffer(_config.BundleId,        _bundleIdBuf);
        EncodeToBuffer(_config.OutputDirectory, _outputDirBuf);
        EncodeToBuffer(_config.StartScene,      _startSceneBuf);
        EncodeToBuffer(_config.SteamBranch,     _steamBranchBuf);
    }

    private void SyncConfigFromBuffers()
    {
        _config.AppName         = DecodeBuffer(_appNameBuf);
        _config.Version         = DecodeBuffer(_versionBuf);
        _config.BundleId        = DecodeBuffer(_bundleIdBuf);
        _config.OutputDirectory = DecodeBuffer(_outputDirBuf);
        _config.StartScene      = DecodeBuffer(_startSceneBuf);
        _config.SteamBranch     = DecodeBuffer(_steamBranchBuf);
        _config.Upload.AppSlug      = DecodeBuffer(_appSlugBuf).Trim();
        _config.Upload.Notes        = _notesText;
        _config.Upload.Requirements = DecodeBuffer(_requirementsBuf);
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    private void DrawTextRow(string label, byte[] buf, float labelWidth, Action<string> onChanged)
    {
        ImGui.Text(label);
        ImGui.SameLine(labelWidth);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##" + label, buf, (uint)buf.Length))
            onChanged(DecodeBuffer(buf));
    }

    private static void EncodeToBuffer(string text, byte[] buf)
    {
        Array.Clear(buf, 0, buf.Length);
        var encoded = Encoding.UTF8.GetBytes(text ?? "");
        Buffer.BlockCopy(encoded, 0, buf, 0, Math.Min(encoded.Length, buf.Length - 1));
    }

    private static string DecodeBuffer(byte[] buf)
        => Encoding.UTF8.GetString(buf).TrimEnd('\0');
}
