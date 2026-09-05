using System.Numerics;
using System.Text;
using ImGuiNET;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Asset Browser panel — dual-pane file browser rooted at Assets/,
/// with filter, drag-drop, context menu operations, and OS integration.
/// </summary>
public sealed class AssetBrowserPanel
{
    private string _currentDirectory = "";
    private string _filter           = "";
    private byte[] _filterBuf        = new byte[128];

    // Selection
    private string? _selectedFile;
    private bool    _pendingDeleteConfirm;
    private string? _pendingDeletePath;

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw()
    {
        if (!ImGui.Begin("Content Browser"))
        {
            ImGui.End();
            return;
        }

        // Ensure root exists
        string root = Path.Combine(EditorState.ProjectPath, "Assets");
        if (!Directory.Exists(root))
        {
            try { Directory.CreateDirectory(root); }
            catch { }
        }

        if (string.IsNullOrEmpty(_currentDirectory) || !Directory.Exists(_currentDirectory))
            _currentDirectory = root;

        // Filter bar
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60f);
        if (ImGui.InputText("##Filter", _filterBuf, (uint)_filterBuf.Length))
            _filter = Encoding.UTF8.GetString(_filterBuf).TrimEnd('\0');
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            _filterBuf = new byte[128];
            _filter    = "";
        }

        ImGui.Separator();

        // Two-column layout: directory tree | file list
        float totalWidth = ImGui.GetContentRegionAvail().X;
        float leftWidth  = totalWidth * 0.30f;
        float rightWidth = totalWidth - leftWidth - 8f;

        ImGui.BeginChild("##DirTree", new Vector2(leftWidth, 0f), ImGuiChildFlags.Border);
        DrawDirectoryTree(root);
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##FileList", new Vector2(rightWidth, 0f), ImGuiChildFlags.Border);
        DrawFileList();
        ImGui.EndChild();

        // Delete confirmation modal
        DrawDeleteConfirm();

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Directory tree
    // -------------------------------------------------------------------------

    private void DrawDirectoryTree(string dirPath)
    {
        var dirName = Path.GetFileName(dirPath);
        if (string.IsNullOrEmpty(dirName)) dirName = dirPath;

        bool isSelected = _currentDirectory == dirPath;
        bool hasSubDirs = false;
        try { hasSubDirs = Directory.GetDirectories(dirPath).Length > 0; }
        catch { }

        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanFullWidth;
        if (isSelected) flags |= ImGuiTreeNodeFlags.Selected;
        if (!hasSubDirs) flags |= ImGuiTreeNodeFlags.Leaf;

        bool open = ImGui.TreeNodeEx(dirName + "##" + dirPath, flags);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            _currentDirectory = dirPath;
            _selectedFile     = null;
        }

        if (open)
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dirPath).OrderBy(d => d))
                    DrawDirectoryTree(sub);
            }
            catch { }
            ImGui.TreePop();
        }
    }

    // -------------------------------------------------------------------------
    // File list
    // -------------------------------------------------------------------------

    private void DrawFileList()
    {
        // Current path breadcrumb
        string rel = Path.GetRelativePath(Path.Combine(EditorState.ProjectPath, "Assets"), _currentDirectory);
        ImGui.TextDisabled(rel == "." ? "Assets/" : "Assets/" + rel);
        ImGui.Separator();

        // Up button
        string root = Path.Combine(EditorState.ProjectPath, "Assets");
        if (_currentDirectory != root)
        {
            if (ImGui.Selectable("[..]", false))
            {
                var parent = Path.GetDirectoryName(_currentDirectory);
                if (parent != null) _currentDirectory = parent;
                _selectedFile = null;
            }
        }

        string[] files;
        string[] subdirs;
        try
        {
            files   = Directory.GetFiles(_currentDirectory).OrderBy(f => f).ToArray();
            subdirs = Directory.GetDirectories(_currentDirectory).OrderBy(d => d).ToArray();
        }
        catch
        {
            ImGui.TextDisabled("(error reading directory)");
            return;
        }

        // Subdirectory entries
        foreach (var dir in subdirs)
        {
            var dirName = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(_filter) &&
                !dirName.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.85f, 1.0f, 1f));
            if (ImGui.Selectable("[dir] " + dirName, false, ImGuiSelectableFlags.AllowDoubleClick))
            {
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    _currentDirectory = dir;
            }
            ImGui.PopStyleColor();
        }

        // File entries
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (!string.IsNullOrEmpty(_filter) &&
                !fileName.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;

            bool isSelected = _selectedFile == file;

            if (ImGui.Selectable(fileName, isSelected, ImGuiSelectableFlags.AllowDoubleClick))
            {
                _selectedFile = file;
                EditorState.SelectedAssetPath = file;

                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    OpenFile(file);
            }

            // Drag source
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(file);
                unsafe
                {
                    fixed (byte* ptr = pathBytes)
                        ImGui.SetDragDropPayload("ASSET_PATH", (IntPtr)ptr, (uint)pathBytes.Length);
                }
                ImGui.Text("Asset: " + fileName);
                ImGui.EndDragDropSource();
            }

            // Context menu
            DrawFileContextMenu(file);
        }
    }

    // -------------------------------------------------------------------------
    // Context menu
    // -------------------------------------------------------------------------

    private void DrawFileContextMenu(string filePath)
    {
        if (!ImGui.BeginPopupContextItem("##FileCtx" + filePath)) return;

        if (ImGui.MenuItem("Open"))
            OpenFile(filePath);

        if (ImGui.MenuItem("Reveal in File Manager"))
            DesktopShell.RevealInFileManager(filePath);

        if (ImGui.MenuItem("Copy Path"))
        {
            DesktopShell.SetClipboardText(filePath);
            ConsoleLog.Add($"Copied path: {filePath}", LogLevel.Info);
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Delete"))
        {
            _pendingDeletePath    = filePath;
            _pendingDeleteConfirm = true;
            ImGui.OpenPopup("##DeleteConfirm");
        }

        ImGui.EndPopup();
    }

    // -------------------------------------------------------------------------
    // Delete confirmation
    // -------------------------------------------------------------------------

    private void DrawDeleteConfirm()
    {
        if (!_pendingDeleteConfirm) return;

        ImGui.SetNextWindowSize(new Vector2(360f, 130f), ImGuiCond.Always);
        if (ImGui.BeginPopupModal("##DeleteConfirm", ref _pendingDeleteConfirm,
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.TextWrapped($"Delete '{Path.GetFileName(_pendingDeletePath)}'?");
            ImGui.Spacing();
            if (ImGui.Button("Delete", new Vector2(80f, 0f)))
            {
                try
                {
                    File.Delete(_pendingDeletePath!);
                    ConsoleLog.Add($"Deleted: {_pendingDeletePath}", LogLevel.Info);
                    if (_selectedFile == _pendingDeletePath) _selectedFile = null;
                }
                catch (Exception ex)
                {
                    ConsoleLog.Add($"Delete failed: {ex.Message}", LogLevel.Error);
                }
                _pendingDeleteConfirm = false;
                _pendingDeletePath    = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(80f, 0f)))
            {
                _pendingDeleteConfirm = false;
                _pendingDeletePath    = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    // -------------------------------------------------------------------------
    // File operations
    // -------------------------------------------------------------------------

    private static void OpenFile(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool isText = ext is ".txt" or ".js" or ".json" or ".scene" or ".md" or ".csv" or ".log" or ".glsl" or ".hlsl";

            if (isText)
            {
                // Open in OS default text editor
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = path,
                    UseShellExecute = true,
                });
            }
            else
            {
                // Open with default associated program
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = path,
                    UseShellExecute = true,
                });
            }
            ConsoleLog.Add($"Opened: {path}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not open '{path}': {ex.Message}", LogLevel.Error);
        }
    }

}
