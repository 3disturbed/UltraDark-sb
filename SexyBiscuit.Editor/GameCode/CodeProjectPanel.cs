using System.Numerics;
using ImGuiNET;
using SexyBiscuit.Editor.Panels;
using SexyBiscuit.Engine.Code;

namespace SexyBiscuit.Editor.GameCode;

/// <summary>
/// The "C# Project" panel: the project's code status, build and reload buttons, the live build
/// log and clickable diagnostics.
/// </summary>
public sealed class CodeProjectPanel
{
    private readonly GameCodeHost _code;
    private string _status = "";
    private DateTime _statusUntil;

    public CodeProjectPanel(GameCodeHost code) => _code = code;

    public void Draw()
    {
        if (!EditorState.ShowCodeProject) return;

        bool open = EditorState.ShowCodeProject;
        if (!ImGui.Begin("C# Project", ref open))
        {
            ImGui.End();
            EditorState.ShowCodeProject = open;
            return;
        }

        if (EditorState.CurrentProject == null)
        {
            ImGui.TextDisabled("Open a project first.");
        }
        else if (_code.Project == null)
        {
            DrawNoProject();
        }
        else
        {
            DrawProject(_code.Project);
        }

        ImGui.End();
        EditorState.ShowCodeProject = open;
    }

    private void DrawNoProject()
    {
        ImGui.TextWrapped("This project has no C# project. Add one to write gameplay code in C#: a GameMode, a PlayerController, a Character, an example component and an example MCP tool are generated as starting points.");
        ImGui.Spacing();

        if (ImGui.Button("Add C# Project", new Vector2(160f, 28f)))
        {
            try
            {
                var result = _code.CreateProject();
                SetStatus($"Created {result.Created.Count} file(s). Building…");
                _ = _code.ReloadAsync(build: true, reason: "panel: add project").ContinueWith(t =>
                {
                    if (t.IsFaulted) ConsoleLog.Add($"First build failed: {t.Exception?.GetBaseException().Message}", LogLevel.Error);
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                SetStatus("Failed: " + ex.Message);
                ConsoleLog.Add(ex.Message, LogLevel.Error);
            }
        }

        DrawStatus();
    }

    private void DrawProject(CodeProject project)
    {
        var last  = _code.LastBuild;
        var types = _code.Types;

        ImGui.TextUnformatted(Path.GetFileName(project.CsprojPath));
        ImGui.SameLine();
        ImGui.TextDisabled($"({project.SourceFiles().Count} source files)");

        if (types == null)
        {
            ImGui.TextDisabled("Assembly: not loaded");
        }
        else
        {
            ImGui.TextUnformatted($"Assembly: generation {types.Generation} — {types.ComponentTypes.Count} component(s), {types.ActorTypes.Count} actor class(es), {types.ToolHolders.Count} tool class(es)");
            if (!_code.EngineMatches)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.55f, 0.2f, 1f));
                ImGui.TextWrapped("Compiled against a different engine build than this editor runs. Rebuild the engine and restart (Tools menu, or ask the assistant).");
                ImGui.PopStyleColor();
            }
        }

        var dotnet = DotnetLocator.Find();
        ImGui.TextDisabled(dotnet != null ? $"dotnet SDK {dotnet.SdkVersion}" : "dotnet SDK not found — install .NET 8+ or set DOTNET_ROOT");

        ImGui.Separator();

        bool busy = _code.IsReloading || (last != null && !last.IsFinished);
        if (busy) ImGui.BeginDisabled();

        if (ImGui.Button("Build")) StartBuild();
        ImGui.SameLine();
        if (ImGui.Button("Build && Reload")) StartReload();
        ImGui.SameLine();

        if (_code.Standalone.IsRunning)
        {
            if (ImGui.Button("Stop Game")) _code.Standalone.Stop();
        }
        else if (ImGui.Button("Run Standalone"))
        {
            StartStandalone();
        }

        if (busy) ImGui.EndDisabled();

        ImGui.SameLine();
        bool auto = _code.AutoReload;
        if (ImGui.Checkbox("Auto-reload on save", ref auto)) _code.AutoReload = auto;

        DrawStatus();
        ImGui.Separator();

        if (last == null)
        {
            ImGui.TextDisabled("No build yet.");
            return;
        }

        var colour = last.State switch
        {
            BuildJobState.Succeeded or BuildJobState.Restarting => new Vector4(0.5f, 1f, 0.5f, 1f),
            BuildJobState.Running or BuildJobState.Queued      => new Vector4(0.6f, 0.8f, 1f, 1f),
            _                                                  => new Vector4(1f, 0.4f, 0.4f, 1f),
        };
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted($"Last build {last.Id}: {last.State} ({last.Elapsed.TotalSeconds:F1} s)");
        ImGui.PopStyleColor();

        if (last.Result is { } result)
        {
            ImGui.TextDisabled($"{result.ErrorCount} error(s), {result.WarningCount} warning(s)");

            if (result.Diagnostics.Count > 0)
            {
                ImGui.BeginChild("##Diagnostics", new Vector2(0f, 140f), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
                foreach (var d in result.Diagnostics.Take(200))
                {
                    var c = d.Severity == BuildSeverity.Error ? new Vector4(1f, 0.4f, 0.4f, 1f) : new Vector4(1f, 0.85f, 0.1f, 1f);
                    ImGui.PushStyleColor(ImGuiCol.Text, c);
                    string label = d.File != null ? $"{Path.GetFileName(d.File)}({d.Line},{d.Column}): {d.Code} {d.Message}" : $"{d.Code} {d.Message}";
                    if (ImGui.Selectable(label) && d.File != null && File.Exists(d.File))
                    {
                        EditorState.ShowCodeEditor = true;
                        CodeEditorPanel.OpenFile(d.File);
                    }
                    ImGui.PopStyleColor();
                }
                ImGui.EndChild();
            }
        }

        if (last.Reload is { } reload)
        {
            ImGui.TextDisabled($"Reload: generation {reload.Generation}, +{reload.Appeared.Count} / -{reload.Disappeared.Count} type(s), {reload.ToolsAdded.Count} tool(s)"
                + (reload.UnloadCollected ? "" : ", previous generation still in memory"));
        }

        ImGui.TextDisabled("Build log");
        ImGui.BeginChild("##BuildLog", new Vector2(0f, 0f), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar);
        foreach (var line in last.Tail(60))
            ImGui.TextUnformatted(line);
        if (!last.IsFinished) ImGui.SetScrollHereY(1f);
        ImGui.EndChild();
    }

    private void StartBuild()
    {
        try
        {
            var job = _code.BuildGame();
            SetStatus($"Building ({job.Id})…");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void StartReload()
    {
        SetStatus("Building and reloading…");
        _ = _code.ReloadAsync(build: true, reason: "panel: build and reload").ContinueWith(t =>
        {
            if (t.IsFaulted) ConsoleLog.Add($"Reload failed: {t.Exception?.GetBaseException().Message}", LogLevel.Error);
        }, TaskScheduler.Default);
    }

    private void StartStandalone()
    {
        var project = _code.Project;
        var dotnet  = DotnetLocator.Find();
        if (project == null || dotnet == null) return;

        SetStatus("Building for a standalone run…");
        var job = _code.BuildGame();
        _ = job.Completion.ContinueWith(_ =>
        {
            if (job.State != BuildJobState.Succeeded) return;
            string dll = job.Result?.OutputAssemblyPath ?? project.OutputAssemblyPath(_code.Configuration);
            _code.Standalone.Start(dotnet.Path, dll, project.Root);
        }, TaskScheduler.Default);
    }

    private void SetStatus(string text)
    {
        _status      = text;
        _statusUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private void DrawStatus()
    {
        if (DateTime.UtcNow > _statusUntil || string.IsNullOrEmpty(_status)) return;
        ImGui.TextDisabled(_status);
    }
}
