using System.Diagnostics;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Git integration panel — status/staging, commit history, and branch management.
/// </summary>
public sealed class GitPanel
{
    // -------------------------------------------------------------------------
    // Cached data
    // -------------------------------------------------------------------------
    private List<GitFileStatus> _statusCache = new();
    private List<GitLogEntry> _logCache = new();
    private List<string> _branchCache = new();
    private string _currentBranch = "";

    // -------------------------------------------------------------------------
    // Refresh
    // -------------------------------------------------------------------------
    private readonly Stopwatch _refreshTimer = Stopwatch.StartNew();
    private const double RefreshIntervalSeconds = 5.0;
    private bool _needsRefresh = true;

    // -------------------------------------------------------------------------
    // UI state
    // -------------------------------------------------------------------------
    private byte[] _commitMessage = new byte[1024];
    private byte[] _newBranchName = new byte[128];
    private string _selectedDiff = "";
    private int _selectedTab;
    private bool _gitAvailable;
    private bool _isRepo;
    private string _statusMessage = "";
    private double _statusMessageTime;

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw()
    {
        if (!EditorState.ShowGitPanel)
            return;

        bool open = EditorState.ShowGitPanel;
        if (!ImGui.Begin("Git", ref open))
        {
            EditorState.ShowGitPanel = open;
            ImGui.End();
            return;
        }
        EditorState.ShowGitPanel = open;

        // Check git availability on first run or after refresh
        if (_needsRefresh || _refreshTimer.Elapsed.TotalSeconds >= RefreshIntervalSeconds)
        {
            _gitAvailable = GitHelper.IsGitInstalled();
            if (_gitAvailable)
            {
                _isRepo = GitHelper.IsGitRepo(EditorState.ProjectPath);
                if (_isRepo)
                    RefreshData();
            }
            _needsRefresh = false;
            _refreshTimer.Restart();
        }

        // Git not installed
        if (!_gitAvailable)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.3f, 0.3f, 1f));
            ImGui.TextWrapped("Git is not installed or not found in PATH.");
            ImGui.PopStyleColor();
            ImGui.End();
            return;
        }

        // Not a git repo
        if (!_isRepo)
        {
            ImGui.TextWrapped("This project is not a Git repository.");
            ImGui.Spacing();
            if (ImGui.Button("Initialize Repository"))
            {
                var err = GitHelper.InitRepo(EditorState.ProjectPath);
                if (err == null)
                {
                    ConsoleLog.Add("Git repository initialized.", LogLevel.Info);
                    _needsRefresh = true;
                }
                else
                {
                    ConsoleLog.Add($"Failed to init repo: {err}", LogLevel.Error);
                }
            }
            ImGui.End();
            return;
        }

        // Refresh button
        if (ImGui.Button("Refresh"))
            _needsRefresh = true;

        ImGui.Separator();

        // Tab bar
        if (ImGui.BeginTabBar("##GitTabs"))
        {
            if (ImGui.BeginTabItem("Status"))
            {
                _selectedTab = 0;
                DrawStatusTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("History"))
            {
                _selectedTab = 1;
                DrawHistoryTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Branches"))
            {
                _selectedTab = 2;
                DrawBranchesTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Data refresh
    // -------------------------------------------------------------------------

    private void RefreshData()
    {
        string dir = EditorState.ProjectPath;
        _statusCache = GitHelper.GetStatus(dir);
        _logCache = GitHelper.GetLog(dir, 100);
        _branchCache = GitHelper.GetBranches(dir);
        _currentBranch = GitHelper.GetCurrentBranch(dir);
    }

    // -------------------------------------------------------------------------
    // Status tab
    // -------------------------------------------------------------------------

    private void DrawStatusTab()
    {
        // Current branch
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.7f, 1.0f, 1f));
        ImGui.Text($"Branch: {_currentBranch}");
        ImGui.PopStyleColor();

        ImGui.Separator();

        // Stage All button
        if (ImGui.Button("Stage All"))
        {
            var err = GitHelper.StageAll(EditorState.ProjectPath);
            if (err == null)
            {
                ConsoleLog.Add("Staged all changes.", LogLevel.Info);
                _needsRefresh = true;
            }
            else
            {
                ConsoleLog.Add($"Stage all failed: {err}", LogLevel.Error);
            }
        }

        ImGui.Separator();

        // File list
        float commitSectionHeight = 120f;
        ImGui.BeginChild("##FileList", new Vector2(0f, -commitSectionHeight), ImGuiChildFlags.Border,
            ImGuiWindowFlags.HorizontalScrollbar);

        for (int i = 0; i < _statusCache.Count; i++)
        {
            var file = _statusCache[i];
            ImGui.PushID(i);

            // Color based on status
            Vector4 color;
            if (file.IsStaged)
                color = new Vector4(0.3f, 0.9f, 0.3f, 1f); // green — staged
            else if (file.StatusCode.Contains('?'))
                color = new Vector4(0.9f, 0.3f, 0.3f, 1f); // red — untracked
            else
                color = new Vector4(0.9f, 0.9f, 0.3f, 1f); // yellow — modified/unstaged

            // Stage/Unstage button
            if (file.IsStaged)
            {
                if (ImGui.SmallButton("Unstage"))
                {
                    var err = GitHelper.Unstage(EditorState.ProjectPath, file.FilePath);
                    if (err == null)
                        _needsRefresh = true;
                    else
                        ConsoleLog.Add($"Unstage failed: {err}", LogLevel.Error);
                }
            }
            else
            {
                if (ImGui.SmallButton("Stage"))
                {
                    var err = GitHelper.Stage(EditorState.ProjectPath, file.FilePath);
                    if (err == null)
                        _needsRefresh = true;
                    else
                        ConsoleLog.Add($"Stage failed: {err}", LogLevel.Error);
                }
            }

            ImGui.SameLine();

            // File path with color
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted($"[{file.StatusCode}] {file.FilePath}");
            ImGui.PopStyleColor();

            // Click to view diff
            if (ImGui.IsItemClicked())
            {
                _selectedDiff = GitHelper.GetDiff(EditorState.ProjectPath, file.FilePath);
                if (string.IsNullOrEmpty(_selectedDiff) && file.IsStaged)
                    _selectedDiff = GitHelper.GetStagedDiff(EditorState.ProjectPath);
            }

            ImGui.PopID();
        }

        if (_statusCache.Count == 0)
        {
            ImGui.TextDisabled("Working tree clean.");
        }

        ImGui.EndChild();

        // Commit section
        ImGui.Separator();
        ImGui.Text("Commit Message:");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputText("##CommitMsg", _commitMessage, (uint)_commitMessage.Length);

        if (ImGui.Button("Commit"))
        {
            string msg = Encoding.UTF8.GetString(_commitMessage).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(msg))
            {
                SetStatusMessage("Commit message cannot be empty.");
            }
            else
            {
                var err = GitHelper.Commit(EditorState.ProjectPath, msg);
                if (err == null)
                {
                    ConsoleLog.Add("Commit successful.", LogLevel.Info);
                    SetStatusMessage("Commit successful.");
                    _commitMessage = new byte[1024];
                    _needsRefresh = true;
                }
                else
                {
                    ConsoleLog.Add($"Commit failed: {err}", LogLevel.Error);
                    SetStatusMessage($"Commit failed: {err}");
                }
            }
        }

        // Status message (fades after a few seconds)
        if (!string.IsNullOrEmpty(_statusMessage))
        {
            double elapsed = _refreshTimer.Elapsed.TotalSeconds - _statusMessageTime;
            if (elapsed < 5.0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(_statusMessage);
            }
            else
            {
                _statusMessage = "";
            }
        }
    }

    // -------------------------------------------------------------------------
    // History tab
    // -------------------------------------------------------------------------

    private void DrawHistoryTab()
    {
        float diffPanelHeight = string.IsNullOrEmpty(_selectedDiff) ? 0f : 200f;
        float listHeight = diffPanelHeight > 0 ? -diffPanelHeight - ImGui.GetStyle().ItemSpacing.Y : 0f;

        ImGui.BeginChild("##LogList", new Vector2(0f, listHeight), ImGuiChildFlags.Border,
            ImGuiWindowFlags.HorizontalScrollbar);

        for (int i = 0; i < _logCache.Count; i++)
        {
            var entry = _logCache[i];
            ImGui.PushID(i);

            string shortHash = entry.Hash.Length >= 7 ? entry.Hash[..7] : entry.Hash;

            // Short hash in monospace-style color
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.8f, 1.0f, 1f));
            ImGui.TextUnformatted(shortHash);
            ImGui.PopStyleColor();

            ImGui.SameLine();

            // Message
            ImGui.TextUnformatted(entry.Message);

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
            ImGui.TextUnformatted($"  {entry.Author} - {entry.RelativeTime}");
            ImGui.PopStyleColor();

            // Click to show diff
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                // Show diff for this commit
                _selectedDiff = GitHelper.GetDiff(EditorState.ProjectPath);
            }

            ImGui.PopID();
        }

        if (_logCache.Count == 0)
        {
            ImGui.TextDisabled("No commits yet.");
        }

        ImGui.EndChild();

        // Diff child panel
        if (!string.IsNullOrEmpty(_selectedDiff))
        {
            ImGui.Separator();
            ImGui.Text("Diff:");
            ImGui.SameLine();
            if (ImGui.SmallButton("Close"))
                _selectedDiff = "";

            if (!string.IsNullOrEmpty(_selectedDiff))
            {
                ImGui.BeginChild("##DiffView", new Vector2(0f, 0f), ImGuiChildFlags.Border,
                    ImGuiWindowFlags.HorizontalScrollbar);

                foreach (var line in _selectedDiff.Split('\n'))
                {
                    Vector4 lineColor;
                    if (line.StartsWith('+') && !line.StartsWith("+++"))
                        lineColor = new Vector4(0.3f, 0.9f, 0.3f, 1f);
                    else if (line.StartsWith('-') && !line.StartsWith("---"))
                        lineColor = new Vector4(0.9f, 0.3f, 0.3f, 1f);
                    else if (line.StartsWith("@@"))
                        lineColor = new Vector4(0.3f, 0.7f, 0.9f, 1f);
                    else
                        lineColor = new Vector4(0.8f, 0.8f, 0.8f, 1f);

                    ImGui.PushStyleColor(ImGuiCol.Text, lineColor);
                    ImGui.TextUnformatted(line);
                    ImGui.PopStyleColor();
                }

                ImGui.EndChild();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Branches tab
    // -------------------------------------------------------------------------

    private void DrawBranchesTab()
    {
        ImGui.BeginChild("##BranchList", new Vector2(0f, -ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().ItemSpacing.Y),
            ImGuiChildFlags.Border, ImGuiWindowFlags.None);

        for (int i = 0; i < _branchCache.Count; i++)
        {
            var branch = _branchCache[i];
            ImGui.PushID(i);

            bool isCurrent = branch == _currentBranch;

            if (isCurrent)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.3f, 1f));
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted(branch + " (current)");
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.TextUnformatted("  " + branch);
                ImGui.SameLine();
                if (ImGui.SmallButton("Checkout"))
                {
                    var err = GitHelper.Checkout(EditorState.ProjectPath, branch);
                    if (err == null)
                    {
                        ConsoleLog.Add($"Checked out branch: {branch}", LogLevel.Info);
                        _needsRefresh = true;
                    }
                    else
                    {
                        ConsoleLog.Add($"Checkout failed: {err}", LogLevel.Error);
                    }
                }
            }

            ImGui.PopID();
        }

        if (_branchCache.Count == 0)
        {
            ImGui.TextDisabled("No branches found.");
        }

        ImGui.EndChild();

        // New branch
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 110f);
        ImGui.InputText("##NewBranch", _newBranchName, (uint)_newBranchName.Length);
        ImGui.SameLine();
        if (ImGui.Button("Create Branch", new Vector2(100f, 0f)))
        {
            string name = Encoding.UTF8.GetString(_newBranchName).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(name))
            {
                var err = GitHelper.CreateBranch(EditorState.ProjectPath, name);
                if (err == null)
                {
                    ConsoleLog.Add($"Created and checked out branch: {name}", LogLevel.Info);
                    _newBranchName = new byte[128];
                    _needsRefresh = true;
                }
                else
                {
                    ConsoleLog.Add($"Branch creation failed: {err}", LogLevel.Error);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SetStatusMessage(string message)
    {
        _statusMessage = message;
        _statusMessageTime = _refreshTimer.Elapsed.TotalSeconds;
    }
}
