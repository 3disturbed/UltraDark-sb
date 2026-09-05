using System.Numerics;
using System.Text;
using ImGuiNET;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Startup/hub panel for managing projects — recent projects, new project creation,
/// and opening existing projects. Drawn as a centered modal-style window.
/// </summary>
public sealed class ProjectManagerPanel
{
    // Tab state
    private int _selectedTab = 0; // 0=Recent, 1=New, 2=Open

    // New project fields
    private byte[] _newProjectName = new byte[256];
    private string _newProjectDir = "";
    private int _selectedTemplate = 0;
    private string[] _templateNames = Array.Empty<string>();
    private string[] _templatePaths = Array.Empty<string>();

    private bool _initialized = false;

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw()
    {
        if (!EditorState.ShowProjectManager) return;

        // Center the window
        var viewport = ImGui.GetMainViewport();
        var windowSize = new Vector2(700, 500);
        ImGui.SetNextWindowPos(viewport.GetCenter() - windowSize * 0.5f, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(windowSize, ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.Begin("Project Manager", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            if (!open) EditorState.ShowProjectManager = false;
            return;
        }
        if (!open) EditorState.ShowProjectManager = false;

        if (!_initialized) { Initialize(); _initialized = true; }

        // Tab bar
        if (ImGui.BeginTabBar("##ProjectTabs"))
        {
            if (ImGui.BeginTabItem("Recent Projects"))
            {
                _selectedTab = 0;
                DrawRecentProjects();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("New Project"))
            {
                _selectedTab = 1;
                DrawNewProject();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Open Project"))
            {
                _selectedTab = 2;
                DrawOpenProject();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Initialize
    // -------------------------------------------------------------------------

    private void Initialize()
    {
        EditorState.LoadRecentProjects();

        // Default new project directory
        if (string.IsNullOrEmpty(_newProjectDir))
        {
            _newProjectDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SexyBiscuit Projects");
        }

        // Scan for templates
        ScanTemplates();
    }

    private void ScanTemplates()
    {
        var names = new List<string>();
        var paths = new List<string>();

        // Build a list of candidate directories to search for Templates/
        var candidates = new List<string>
        {
            AppDomain.CurrentDomain.BaseDirectory,
            Directory.GetCurrentDirectory(),
            EditorState.ProjectPath,
        };

        // Walk up from the app base directory to find the repo/solution root
        // (the editor runs from bin/Debug/net8.0-windows/ which is 4 levels deep)
        var walkUp = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var parent = Path.GetDirectoryName(walkUp);
            if (parent == null || parent == walkUp) break;
            walkUp = parent;
            candidates.Add(walkUp);
        }

        // Find the first candidate that contains a Templates directory
        string? templateRoot = null;
        foreach (var candidate in candidates)
        {
            var test = Path.Combine(candidate, "Templates");
            if (Directory.Exists(test))
            {
                templateRoot = test;
                break;
            }
        }

        if (templateRoot != null)
        {
            foreach (var dir in Directory.GetDirectories(templateRoot).OrderBy(Path.GetFileName))
            {
                var templateJson = Path.Combine(dir, "template.json");
                if (File.Exists(templateJson))
                {
                    names.Add(Path.GetFileName(dir));
                    paths.Add(dir);
                }
            }
        }

        // Always provide an "Empty" option
        if (names.Count == 0 || !names.Contains("Empty"))
        {
            names.Insert(0, "Empty (no template)");
            paths.Insert(0, "");
        }

        _templateNames = names.ToArray();
        _templatePaths = paths.ToArray();
    }

    // -------------------------------------------------------------------------
    // Recent Projects tab
    // -------------------------------------------------------------------------

    private void DrawRecentProjects()
    {
        ImGui.Spacing();

        if (EditorState.RecentProjects.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No recent projects.");
            ImGui.Spacing();
            ImGui.TextDisabled("Create a new project or open an existing one using the tabs above.");
            return;
        }

        ImGui.BeginChild("##RecentList", new Vector2(0, 0), ImGuiChildFlags.None);

        int removeIndex = -1;

        for (int i = 0; i < EditorState.RecentProjects.Count; i++)
        {
            var project = EditorState.RecentProjects[i];

            ImGui.PushID(i);

            // Project entry as a selectable spanning the full width
            var cursorPos = ImGui.GetCursorScreenPos();
            var avail = ImGui.GetContentRegionAvail();
            float entryHeight = ImGui.GetTextLineHeight() * 3 + ImGui.GetStyle().ItemSpacing.Y * 2;

            if (ImGui.Selectable($"##RecentEntry{i}", false, ImGuiSelectableFlags.None,
                    new Vector2(avail.X, entryHeight)))
            {
                EditorState.OpenProject(project.Path);
            }

            // Right-click context menu
            if (ImGui.BeginPopupContextItem($"##RecentCtx{i}"))
            {
                if (ImGui.MenuItem("Remove from list"))
                {
                    removeIndex = i;
                }
                ImGui.EndPopup();
            }

            // Draw the content overlay on top of the selectable
            var afterPos = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(cursorPos + new Vector2(8, 4));

            // Project name — larger/bold
            float originalScale = ImGui.GetFont().Scale;
            ImGui.GetFont().Scale = 1.15f;
            ImGui.PushFont(ImGui.GetFont());
            ImGui.Text(project.Name);
            ImGui.GetFont().Scale = originalScale;
            ImGui.PopFont();

            // Path — dimmed
            ImGui.SetCursorScreenPos(cursorPos + new Vector2(8, 4 + ImGui.GetTextLineHeight() * 1.3f));
            ImGui.TextDisabled(project.Path);

            // Last opened date
            ImGui.SetCursorScreenPos(cursorPos + new Vector2(8, 4 + ImGui.GetTextLineHeight() * 2.3f));
            ImGui.TextDisabled($"Last opened: {project.LastOpened:yyyy-MM-dd HH:mm}");

            // Restore cursor
            ImGui.SetCursorScreenPos(afterPos);

            ImGui.Separator();
            ImGui.PopID();
        }

        // Handle removal outside the loop to avoid modifying the list during iteration
        if (removeIndex >= 0)
        {
            EditorState.RecentProjects.RemoveAt(removeIndex);
            EditorState.SaveRecentProjects();
        }

        ImGui.EndChild();
    }

    // -------------------------------------------------------------------------
    // New Project tab
    // -------------------------------------------------------------------------

    private void DrawNewProject()
    {
        ImGui.Spacing();

        // Project name
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Project Name:");
        ImGui.SameLine(140);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ProjectName", _newProjectName, (uint)_newProjectName.Length);

        ImGui.Spacing();

        // Directory
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Location:");
        ImGui.SameLine(140);
        ImGui.SetNextItemWidth(-80);
        ImGui.TextDisabled(_newProjectDir);
        ImGui.SameLine();
        if (ImGui.Button("Browse...##Dir"))
        {
            FileDialog.PickFolder("Select project location", _newProjectDir,
                chosen => _newProjectDir = chosen);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Template selector
        ImGui.Text("Template:");
        ImGui.Spacing();

        ImGui.BeginChild("##TemplateList", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() - 8),
            ImGuiChildFlags.Border);

        for (int i = 0; i < _templateNames.Length; i++)
        {
            if (ImGui.RadioButton(_templateNames[i], ref _selectedTemplate, i))
            {
                // Selection handled by ref
            }
        }

        ImGui.EndChild();

        ImGui.Spacing();

        // Preview of full path
        string projectName = Encoding.UTF8.GetString(_newProjectName).TrimEnd('\0');
        string fullPath = string.IsNullOrWhiteSpace(projectName)
            ? ""
            : Path.Combine(_newProjectDir, projectName);

        if (!string.IsNullOrEmpty(fullPath))
        {
            ImGui.TextDisabled($"Project will be created at: {fullPath}");
        }

        // Create button
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 110);
        bool canCreate = !string.IsNullOrWhiteSpace(projectName) &&
                         !string.IsNullOrWhiteSpace(_newProjectDir);

        if (!canCreate) ImGui.BeginDisabled();

        if (ImGui.Button("Create Project", new Vector2(120, 0)))
        {
            try
            {
                string? templatePath = _selectedTemplate < _templatePaths.Length
                    ? _templatePaths[_selectedTemplate]
                    : null;
                if (string.IsNullOrEmpty(templatePath)) templatePath = null;

                string projectDir = Path.Combine(_newProjectDir, projectName);
                string resultPath = ProjectFile.CreateNew(projectName, projectDir, templatePath);
                ConsoleLog.Add($"Created new project: {projectName}", LogLevel.Info);
                EditorState.OpenProject(resultPath);

                // Reset fields
                _newProjectName = new byte[256];
                _selectedTemplate = 0;
            }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Failed to create project: {ex.Message}", LogLevel.Error);
            }
        }

        if (!canCreate) ImGui.EndDisabled();
    }

    // -------------------------------------------------------------------------
    // Open Project tab
    // -------------------------------------------------------------------------

    private void DrawOpenProject()
    {
        ImGui.Spacing();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Browse for an existing SexyBiscuit project file (.sbproject) to open it in the editor.");

        ImGui.Spacing();
        ImGui.Spacing();

        float buttonWidth = 250;
        float windowWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (windowWidth - buttonWidth) * 0.5f);

        if (ImGui.Button("Browse for .sbproject file...", new Vector2(buttonWidth, 40)))
        {
            FileDialog.OpenFile("Open SexyBiscuit Project", EditorState.ProjectPath,
                new[] { ".sbproject" }, EditorState.OpenProject);
        }

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Tip: You can also drag and drop a .sbproject file onto the editor window.");
        ImGui.TextDisabled("Project files are located in the root of each project folder.");
    }
}
