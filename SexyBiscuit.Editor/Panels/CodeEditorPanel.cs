using System.Numerics;
using System.Reflection;
using System.Text;
using ImGuiNET;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Integrated code editor panel for editing .js script files within the engine editor.
/// Supports multiple open files with tabs, basic syntax-highlighted preview, API hints,
/// and a new-script template generator.
/// </summary>
public sealed class CodeEditorPanel
{
    public CodeEditorPanel()
    {
        GameCode.GameCodeHost.TypesChanged += () => _hintsBuilt = false;
    }

    // -------------------------------------------------------------------------
    // Nested types
    // -------------------------------------------------------------------------

    private class EditorFile
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Content { get; set; } = "";
        public byte[] Buffer { get; set; } = new byte[65536]; // 64KB for InputText
        public bool IsDirty { get; set; }
        public int ContentLength { get; set; }
    }

    // -------------------------------------------------------------------------
    // Singleton access
    // -------------------------------------------------------------------------

    public static CodeEditorPanel? Instance { get; private set; }

    public static void OpenFile(string path) => Instance?._OpenFile(path);

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly List<EditorFile> _openFiles = new();
    private int _activeFileIndex;
    private bool _showSyntaxPreview;
    private Dictionary<string, List<string>> _apiHints = new();
    private bool _hintsBuilt;
    private bool _showNewScriptDialog;
    private byte[] _newScriptName = new byte[256];

    // -------------------------------------------------------------------------
    // Syntax highlighting data
    // -------------------------------------------------------------------------

    private static readonly HashSet<string> JsKeywords = new(StringComparer.Ordinal)
    {
        "function", "var", "let", "const", "if", "else", "return", "for",
        "while", "class", "this", "new", "true", "false", "null", "undefined"
    };

    private static readonly HashSet<string> EngineApiNames = new(StringComparer.Ordinal)
    {
        "Transform", "Input", "Physics", "Audio", "Scene", "Actor", "Component"
    };

    private static readonly Vector4 ColorKeyword = new(0.4f, 0.6f, 1.0f, 1.0f);     // blue
    private static readonly Vector4 ColorString  = new(0.4f, 0.9f, 0.4f, 1.0f);      // green
    private static readonly Vector4 ColorNumber  = new(0.4f, 0.9f, 0.9f, 1.0f);      // cyan
    private static readonly Vector4 ColorComment = new(0.6f, 0.6f, 0.6f, 1.0f);      // gray
    private static readonly Vector4 ColorApi     = new(1.0f, 0.9f, 0.3f, 1.0f);      // yellow
    private static readonly Vector4 ColorDefault = new(0.9f, 0.9f, 0.9f, 1.0f);      // white

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw()
    {
        if (!EditorState.ShowCodeEditor) return;

        Instance = this;

        if (!_hintsBuilt)
            BuildApiHints();

        bool open = EditorState.ShowCodeEditor;
        if (!ImGui.Begin("Code Editor", ref open))
        {
            EditorState.ShowCodeEditor = open;
            ImGui.End();
            return;
        }
        EditorState.ShowCodeEditor = open;

        DrawToolbar();
        ImGui.Separator();

        if (_openFiles.Count == 0)
        {
            ImGui.TextDisabled("No files open. Click 'New Script' or open a .js file from the Asset Browser.");
        }
        else
        {
            DrawTabBar();
            DrawEditorArea();
        }

        // Popups
        DrawNewScriptDialog();
        DrawApiHintPopup();

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Toolbar
    // -------------------------------------------------------------------------

    private void DrawToolbar()
    {
        if (ImGui.Button("New Script"))
        {
            _newScriptName = new byte[256];
            _showNewScriptDialog = true;
            ImGui.OpenPopup("New Script##Dialog");
        }

        ImGui.SameLine();

        bool hasActiveFile = _activeFileIndex >= 0 && _activeFileIndex < _openFiles.Count;

        if (ImGui.Button("Save (Ctrl+S)") && hasActiveFile)
            SaveActiveFile();

        // Ctrl+S shortcut
        if (hasActiveFile && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S))
            SaveActiveFile();

        ImGui.SameLine();
        ImGui.Checkbox("Syntax Preview", ref _showSyntaxPreview);

        ImGui.SameLine();
        if (ImGui.Button("Close") && hasActiveFile)
            CloseFile(_activeFileIndex);
    }

    // -------------------------------------------------------------------------
    // Tab bar
    // -------------------------------------------------------------------------

    private void DrawTabBar()
    {
        if (!ImGui.BeginTabBar("##CodeEditorTabs", ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.AutoSelectNewTabs))
            return;

        for (int i = 0; i < _openFiles.Count; i++)
        {
            var file = _openFiles[i];
            string label = file.IsDirty ? file.FileName + " *" : file.FileName;
            label += "##" + file.FilePath;

            bool tabOpen = true;
            var flags = (_activeFileIndex == i) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

            if (ImGui.BeginTabItem(label, ref tabOpen, flags))
            {
                _activeFileIndex = i;
                ImGui.EndTabItem();
            }

            if (!tabOpen)
            {
                CloseFile(i);
                if (i <= _activeFileIndex && _activeFileIndex > 0)
                    _activeFileIndex--;
                i--;
            }
        }

        ImGui.EndTabBar();
    }

    // -------------------------------------------------------------------------
    // Editor area
    // -------------------------------------------------------------------------

    private void DrawEditorArea()
    {
        if (_activeFileIndex < 0 || _activeFileIndex >= _openFiles.Count) return;

        var file = _openFiles[_activeFileIndex];
        var availSize = ImGui.GetContentRegionAvail();

        float previewWidth = _showSyntaxPreview ? availSize.X * 0.45f : 0f;
        float editorWidth = availSize.X - previewWidth - (_showSyntaxPreview ? 8f : 0f);

        // Left side: line numbers + editor
        ImGui.BeginChild("##EditorLeft", new Vector2(editorWidth, availSize.Y), ImGuiChildFlags.None);

        float lineNumWidth = 40f;
        string content = GetBufferContent(file);

        // Line numbers
        ImGui.BeginChild("##LineNumbers", new Vector2(lineNumWidth, ImGui.GetContentRegionAvail().Y), ImGuiChildFlags.None);
        int lineCount = CountLines(content);
        for (int ln = 1; ln <= lineCount; ln++)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColorComment);
            ImGui.TextUnformatted(ln.ToString());
            ImGui.PopStyleColor();
        }
        ImGui.EndChild();

        ImGui.SameLine();

        // Text input
        var inputSize = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetContentRegionAvail().Y);
        var inputFlags = ImGuiInputTextFlags.AllowTabInput;

        string editContent = file.Content;
        if (ImGui.InputTextMultiline(
            "##CodeInput",
            ref editContent,
            65536,
            inputSize,
            inputFlags
        ))
        {
            file.Content = editContent;
            file.IsDirty = true;
            file.ContentLength = System.Text.Encoding.UTF8.GetByteCount(editContent);
        }

        ImGui.EndChild();

        // Right side: syntax preview
        if (_showSyntaxPreview)
        {
            ImGui.SameLine();
            ImGui.BeginChild("##SyntaxPreview", new Vector2(previewWidth, availSize.Y), ImGuiChildFlags.Border,
                ImGuiWindowFlags.HorizontalScrollbar);

            ImGui.TextDisabled("--- Syntax Preview (read-only) ---");
            ImGui.Separator();

            DrawSyntaxHighlighted(content);

            ImGui.EndChild();
        }
    }

    // -------------------------------------------------------------------------
    // Syntax highlighted preview
    // -------------------------------------------------------------------------

    private void DrawSyntaxHighlighted(string content)
    {
        var lines = content.Split('\n');
        bool inBlockComment = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                ImGui.TextUnformatted("");
                continue;
            }

            int i = 0;
            bool firstToken = true;

            while (i < line.Length)
            {
                // Block comment continuation
                if (inBlockComment)
                {
                    int endIdx = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (endIdx >= 0)
                    {
                        string commentPart = line.Substring(i, endIdx - i + 2);
                        EmitToken(commentPart, ColorComment, ref firstToken);
                        i = endIdx + 2;
                        inBlockComment = false;
                    }
                    else
                    {
                        EmitToken(line.Substring(i), ColorComment, ref firstToken);
                        i = line.Length;
                    }
                    continue;
                }

                // Skip whitespace
                if (char.IsWhiteSpace(line[i]))
                {
                    int start = i;
                    while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                    EmitToken(line.Substring(start, i - start), ColorDefault, ref firstToken);
                    continue;
                }

                // Line comment
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                {
                    EmitToken(line.Substring(i), ColorComment, ref firstToken);
                    i = line.Length;
                    continue;
                }

                // Block comment start
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    int endIdx = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (endIdx >= 0)
                    {
                        string commentPart = line.Substring(i, endIdx - i + 2);
                        EmitToken(commentPart, ColorComment, ref firstToken);
                        i = endIdx + 2;
                    }
                    else
                    {
                        EmitToken(line.Substring(i), ColorComment, ref firstToken);
                        i = line.Length;
                        inBlockComment = true;
                    }
                    continue;
                }

                // String literals
                if (line[i] == '"' || line[i] == '\'')
                {
                    char quote = line[i];
                    int start = i;
                    i++;
                    while (i < line.Length && line[i] != quote)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length) i++; // skip escaped
                        i++;
                    }
                    if (i < line.Length) i++; // closing quote
                    EmitToken(line.Substring(start, i - start), ColorString, ref firstToken);
                    continue;
                }

                // Numbers
                if (char.IsDigit(line[i]) || (line[i] == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
                {
                    int start = i;
                    while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                    EmitToken(line.Substring(start, i - start), ColorNumber, ref firstToken);
                    continue;
                }

                // Identifiers / keywords
                if (char.IsLetter(line[i]) || line[i] == '_')
                {
                    int start = i;
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                    string word = line.Substring(start, i - start);

                    Vector4 color;
                    if (JsKeywords.Contains(word))
                        color = ColorKeyword;
                    else if (EngineApiNames.Contains(word))
                        color = ColorApi;
                    else
                        color = ColorDefault;

                    EmitToken(word, color, ref firstToken);
                    continue;
                }

                // Punctuation / operators — emit one char at a time
                EmitToken(line[i].ToString(), ColorDefault, ref firstToken);
                i++;
            }

            // End the line — if no tokens were emitted, ensure a blank line
            ImGui.NewLine();
        }
    }

    private static void EmitToken(string text, Vector4 color, ref bool firstToken)
    {
        if (!firstToken)
            ImGui.SameLine(0f, 0f);
        firstToken = false;

        ImGui.TextColored(color, text);
    }

    // -------------------------------------------------------------------------
    // _OpenFile (instance implementation)
    // -------------------------------------------------------------------------

    private void _OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            ConsoleLog.Add($"File not found: {path}", LogLevel.Error);
            return;
        }

        // Check if already open
        for (int i = 0; i < _openFiles.Count; i++)
        {
            if (string.Equals(_openFiles[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _activeFileIndex = i;
                return;
            }
        }

        // Load file
        try
        {
            var fileContent = File.ReadAllText(path);
            var file = new EditorFile
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Content = fileContent,
                IsDirty = false,
                ContentLength = Encoding.UTF8.GetByteCount(fileContent),
            };

            _openFiles.Add(file);
            _activeFileIndex = _openFiles.Count - 1;

            ConsoleLog.Add($"Opened: {path}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Failed to open file: {ex.Message}", LogLevel.Error);
        }
    }

    // -------------------------------------------------------------------------
    // Save
    // -------------------------------------------------------------------------

    private void SaveActiveFile()
    {
        if (_activeFileIndex < 0 || _activeFileIndex >= _openFiles.Count) return;

        var file = _openFiles[_activeFileIndex];
        try
        {
            string content = GetBufferContent(file);
            File.WriteAllText(file.FilePath, content);
            file.IsDirty = false;
            ConsoleLog.Add($"Saved: {file.FilePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Save failed: {ex.Message}", LogLevel.Error);
        }
    }

    // -------------------------------------------------------------------------
    // Close
    // -------------------------------------------------------------------------

    private void CloseFile(int index)
    {
        if (index < 0 || index >= _openFiles.Count) return;

        _openFiles.RemoveAt(index);
        if (_activeFileIndex >= _openFiles.Count)
            _activeFileIndex = _openFiles.Count - 1;
    }

    // -------------------------------------------------------------------------
    // New Script dialog
    // -------------------------------------------------------------------------

    private void DrawNewScriptDialog()
    {
        if (!_showNewScriptDialog) return;

        ImGui.SetNextWindowSize(new Vector2(400f, 160f), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("New Script##Dialog", ref _showNewScriptDialog,
            ImGuiWindowFlags.NoResize))
        {
            ImGui.Text("Script name (without extension):");
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            ImGui.InputText("##NewScriptName", _newScriptName, (uint)_newScriptName.Length);

            ImGui.Spacing();

            if (ImGui.Button("Create", new Vector2(100f, 0f)))
            {
                string name = Encoding.UTF8.GetString(_newScriptName).TrimEnd('\0').Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    CreateNewScript(name);
                    _showNewScriptDialog = false;
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();

            if (ImGui.Button("Cancel", new Vector2(100f, 0f)))
            {
                _showNewScriptDialog = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void CreateNewScript(string name)
    {
        string scriptsDir = Path.Combine(EditorState.ProjectPath, "Assets", "Scripts");
        try
        {
            Directory.CreateDirectory(scriptsDir);
        }
        catch { }

        string filePath = Path.Combine(scriptsDir, name + ".js");

        string content = "// " + name + ".js\n\n" +
            "function onStart() {\n" +
            "    // Called when the actor starts\n" +
            "}\n\n" +
            "function onUpdate(dt) {\n" +
            "    // Called every frame\n" +
            "    // dt = delta time in seconds\n" +
            "}\n\n" +
            "function onDestroy() {\n" +
            "    // Called when the actor is destroyed\n" +
            "}\n";

        try
        {
            File.WriteAllText(filePath, content);
            ConsoleLog.Add($"Created new script: {filePath}", LogLevel.Info);
            _OpenFile(filePath);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Failed to create script: {ex.Message}", LogLevel.Error);
        }
    }

    // -------------------------------------------------------------------------
    // API Hints
    // -------------------------------------------------------------------------

    private void BuildApiHints()
    {
        _hintsBuilt = true;
        _apiHints.Clear();

        try
        {
            var assembly = typeof(SexyBiscuit.Engine.Core.Component).Assembly;

            foreach (var type in assembly.SafeGetTypes().Where(t => t.IsPublic))
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName)
                    .Select(m => m.Name)
                    .Distinct()
                    .ToList();

                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(p => p.Name)
                    .Distinct()
                    .ToList();

                var members = new List<string>();
                members.AddRange(properties);
                members.AddRange(methods);
                members.Sort(StringComparer.OrdinalIgnoreCase);

                if (members.Count > 0)
                    _apiHints[type.Name] = members;
            }

            ConsoleLog.Add($"Built API hints for {_apiHints.Count} types.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ConsoleLog.Add($"Failed to build API hints: {ex.Message}", LogLevel.Warning);
        }
    }

    private void DrawApiHintPopup()
    {
        if (_activeFileIndex < 0 || _activeFileIndex >= _openFiles.Count) return;

        var file = _openFiles[_activeFileIndex];
        string content = GetBufferContent(file);

        // Find word before the last '.' in the content up to the cursor
        // Simple heuristic: look at the last line for a pattern like TypeName.
        var lines = content.Split('\n');
        if (lines.Length == 0) return;

        // Check the last non-empty line for a trailing "TypeName." pattern
        string lastLine = lines[^1].TrimEnd('\r');
        if (!lastLine.EndsWith('.')) return;

        // Extract the word before the dot
        int dotIdx = lastLine.Length - 1;
        int wordStart = dotIdx - 1;
        while (wordStart >= 0 && (char.IsLetterOrDigit(lastLine[wordStart]) || lastLine[wordStart] == '_'))
            wordStart--;
        wordStart++;

        if (wordStart >= dotIdx) return;

        string typeName = lastLine.Substring(wordStart, dotIdx - wordStart);

        if (!_apiHints.TryGetValue(typeName, out var hints)) return;

        ImGui.SetNextWindowSize(new Vector2(250f, 200f), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(ImGui.GetCursorScreenPos() + new Vector2(0f, -210f), ImGuiCond.Appearing);

        if (ImGui.Begin("##ApiHints", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextDisabled($"{typeName} members:");
            ImGui.Separator();

            foreach (var hint in hints)
            {
                ImGui.TextUnformatted(hint);
            }
        }
        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string GetBufferContent(EditorFile file)
    {
        return file.Content;
    }

    private static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return 1;
        int count = 1;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n') count++;
        }
        return count;
    }
}
