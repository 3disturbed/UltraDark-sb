using System.Numerics;
using ImGuiNET;

namespace SexyBiscuit.Editor;

/// <summary>
/// A modal file browser drawn with ImGui.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the WinForms common dialogs the editor used to call. Those were the only
/// thing tying the editor to Windows — everything else is MonoGame and ImGui, both of
/// which run on macOS and Linux. Drawing our own browser costs a few hundred lines and
/// makes the editor cross-platform, and it also lives inside the editor window rather
/// than opening a detached OS dialog behind it.
/// </para>
/// <para>
/// One dialog is open at a time, which matches how the editor uses them: a dialog is
/// always a response to a menu item or a button, and those are unreachable while a modal
/// is up. The result is delivered through a callback rather than returned, because ImGui
/// is immediate-mode — the call that opens the dialog has long since returned by the time
/// the user picks a file.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// FileDialog.OpenFile("Open Scene", projectPath, new[] { ".scene" },
///     path =&gt; LoadScene(path));
///
/// // Once per frame, after the panels:
/// FileDialog.Draw();
/// </code>
/// </example>
public static class FileDialog
{
    private enum Mode { Open, Save, Folder }

    private static bool   _open;
    private static bool   _needsOpenCall;
    private static Mode   _mode;
    private static string _title       = "";
    private static string _currentDir  = "";
    private static string[] _filters   = Array.Empty<string>();
    private static Action<string>? _onPicked;

    private static readonly byte[] _fileNameBuf = new byte[512];
    private static string _selected = "";
    private static string? _error;

    // Cached listing, refreshed when the directory changes rather than every frame —
    // an immediate-mode UI would otherwise stat the whole directory 60 times a second.
    private static string[] _dirs  = Array.Empty<string>();
    private static string[] _files = Array.Empty<string>();
    private static string   _listedDir = "";

    /// <summary>True while a dialog is showing.</summary>
    public static bool IsOpen => _open;

    /// <summary>
    /// Prompts for an existing file.
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="startDirectory">Directory to open in. Falls back to the user's home.</param>
    /// <param name="extensions">Extensions to show, including the dot. Empty shows everything.</param>
    /// <param name="onPicked">Runs with the chosen path. Not called if the user cancels.</param>
    public static void OpenFile(string title, string? startDirectory, string[] extensions,
                                Action<string> onPicked)
        => Show(Mode.Open, title, startDirectory, extensions, "", onPicked);

    /// <summary>Prompts for a path to write to, warning before overwriting.</summary>
    public static void SaveFile(string title, string? startDirectory, string[] extensions,
                                string defaultName, Action<string> onPicked)
        => Show(Mode.Save, title, startDirectory, extensions, defaultName, onPicked);

    /// <summary>Prompts for a directory.</summary>
    public static void PickFolder(string title, string? startDirectory, Action<string> onPicked)
        => Show(Mode.Folder, title, startDirectory, Array.Empty<string>(), "", onPicked);

    private static void Show(Mode mode, string title, string? startDirectory,
                             string[] extensions, string defaultName, Action<string> onPicked)
    {
        _mode      = mode;
        _title     = title;
        _filters   = extensions;
        _onPicked  = onPicked;
        _selected  = "";
        _error     = null;

        _currentDir = ResolveStartDirectory(startDirectory);
        _listedDir  = "";      // force a refresh

        Array.Clear(_fileNameBuf);
        WriteBuffer(defaultName, _fileNameBuf);

        _open          = true;
        _needsOpenCall = true;
    }

    private static string ResolveStartDirectory(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Draws the dialog. Call once per frame, after the rest of the UI.</summary>
    public static void Draw()
    {
        if (!_open) return;

        if (_needsOpenCall)
        {
            ImGui.OpenPopup(_title);
            _needsOpenCall = false;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(720, 460), ImGuiCond.Appearing);

        bool stayOpen = true;
        if (!ImGui.BeginPopupModal(_title, ref stayOpen, ImGuiWindowFlags.NoCollapse))
        {
            if (!stayOpen) Close();
            return;
        }

        DrawPathBar();
        ImGui.Separator();
        DrawListing();
        ImGui.Separator();
        DrawFooter();

        ImGui.EndPopup();

        if (!stayOpen) Close();
    }

    // -------------------------------------------------------------------------
    // Sections
    // -------------------------------------------------------------------------

    private static void DrawPathBar()
    {
        if (ImGui.Button("Up"))
        {
            var parent = Directory.GetParent(_currentDir);
            if (parent != null) Navigate(parent.FullName);
        }

        ImGui.SameLine();
        if (ImGui.Button("Home"))
            Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        ImGui.SameLine();
        ImGui.TextDisabled(_currentDir);
    }

    private static void DrawListing()
    {
        RefreshListing();

        float footerHeight = ImGui.GetFrameHeightWithSpacing() * 2f + 12f;
        ImGui.BeginChild("##listing", new Vector2(0, -footerHeight), ImGuiChildFlags.Border);

        foreach (var dir in _dirs)
        {
            string name = Path.GetFileName(dir);
            if (ImGui.Selectable($"[dir]  {name}", false, ImGuiSelectableFlags.AllowDoubleClick))
            {
                if (_mode == Mode.Folder) _selected = dir;

                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    Navigate(dir);
                    ImGui.EndChild();
                    return;
                }
            }
        }

        if (_mode != Mode.Folder)
        {
            foreach (var file in _files)
            {
                string name = Path.GetFileName(file);
                bool selected = _selected == file;

                if (ImGui.Selectable($"       {name}", selected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _selected = file;
                    WriteBuffer(name, _fileNameBuf);

                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        Confirm();
                        ImGui.EndChild();
                        return;
                    }
                }
            }
        }

        ImGui.EndChild();
    }

    private static void DrawFooter()
    {
        if (_mode != Mode.Folder)
        {
            ImGui.SetNextItemWidth(-160f);
            ImGui.InputText("##filename", _fileNameBuf, (uint)_fileNameBuf.Length);
            ImGui.SameLine();
        }
        else
        {
            ImGui.TextDisabled(string.IsNullOrEmpty(_selected)
                ? "Select a folder, or use the current one."
                : Path.GetFileName(_selected));
            ImGui.SameLine();
        }

        string confirmLabel = _mode switch
        {
            Mode.Save   => "Save",
            Mode.Folder => "Choose",
            _           => "Open",
        };

        if (ImGui.Button(confirmLabel, new Vector2(70, 0))) Confirm();

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(70, 0))) Close();

        if (_error != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.45f, 0.4f, 1f));
            ImGui.TextWrapped(_error);
            ImGui.PopStyleColor();
        }
    }

    // -------------------------------------------------------------------------
    // Behaviour
    // -------------------------------------------------------------------------

    private static void Navigate(string directory)
    {
        if (!Directory.Exists(directory)) return;
        _currentDir = directory;
        _selected   = "";
        _error      = null;
    }

    private static void RefreshListing()
    {
        if (_listedDir == _currentDir) return;
        _listedDir = _currentDir;

        try
        {
            _dirs = Directory.GetDirectories(_currentDir)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _files = Directory.GetFiles(_currentDir)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .Where(MatchesFilter)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            // An unreadable directory is normal — a permissions-protected system folder,
            // a disconnected network share. Show it rather than crashing the editor.
            _dirs  = Array.Empty<string>();
            _files = Array.Empty<string>();
            _error = $"Cannot read this folder: {ex.Message}";
        }
    }

    private static bool MatchesFilter(string path)
    {
        if (_filters.Length == 0) return true;
        string ext = Path.GetExtension(path);
        return _filters.Any(f => string.Equals(f, ext, StringComparison.OrdinalIgnoreCase));
    }

    private static void Confirm()
    {
        string result;

        if (_mode == Mode.Folder)
        {
            result = string.IsNullOrEmpty(_selected) ? _currentDir : _selected;
        }
        else
        {
            string name = ReadBuffer(_fileNameBuf).Trim();
            if (name.Length == 0)
            {
                _error = "Enter a file name.";
                return;
            }

            // Add the expected extension when the user typed a bare name.
            if (_filters.Length > 0 && string.IsNullOrEmpty(Path.GetExtension(name)))
                name += _filters[0];

            result = Path.Combine(_currentDir, name);

            if (_mode == Mode.Open && !File.Exists(result))
            {
                _error = $"'{name}' does not exist.";
                return;
            }
        }

        var callback = _onPicked;
        Close();
        callback?.Invoke(result);
    }

    private static void Close()
    {
        _open     = false;
        _onPicked = null;
        _error    = null;
        ImGui.CloseCurrentPopup();
    }

    // -------------------------------------------------------------------------
    // Buffer helpers
    // -------------------------------------------------------------------------

    private static void WriteBuffer(string value, byte[] buffer)
    {
        Array.Clear(buffer);
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, 0, Math.Min(bytes.Length, buffer.Length - 1));
    }

    private static string ReadBuffer(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0) end = buffer.Length;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end);
    }
}

/// <summary>
/// Small cross-platform shims for the desktop integrations the editor used to get
/// from WinForms.
/// </summary>
public static class DesktopShell
{
    /// <summary>Puts text on the system clipboard, via ImGui's own clipboard backend.</summary>
    public static void SetClipboardText(string text) => ImGui.SetClipboardText(text);

    /// <summary>Reads the system clipboard.</summary>
    public static string GetClipboardText() => ImGui.GetClipboardText();

    /// <summary>
    /// Opens the platform's file manager at <paramref name="path"/>, selecting the file
    /// when one is given.
    /// </summary>
    public static void RevealInFileManager(string path)
    {
        try
        {
            bool isFile = File.Exists(path);
            string dir  = isFile ? Path.GetDirectoryName(path)! : path;

            if (OperatingSystem.IsWindows())
            {
                // /select, highlights the file itself rather than just opening the folder.
                System.Diagnostics.Process.Start("explorer.exe",
                    isFile ? $"/select,\"{path}\"" : $"\"{dir}\"");
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open",
                    isFile ? new[] { "-R", path } : new[] { dir });
            }
            else
            {
                System.Diagnostics.Process.Start("xdg-open", dir);
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Could not open the file manager: {ex.Message}", LogLevel.Error);
        }
    }
}
