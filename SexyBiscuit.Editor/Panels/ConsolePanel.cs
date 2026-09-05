using System.Numerics;
using System.Text;
using ImGuiNET;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scripting;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Console panel — tabbed view with a log tab (colour-coded, filterable, auto-scroll)
/// and a JS REPL tab backed by a Jint runtime on the active scene.
/// </summary>
public sealed class ConsolePanel
{
    // -------------------------------------------------------------------------
    // Log tab state
    // -------------------------------------------------------------------------
    private bool _showInfo    = true;
    private bool _showWarning = true;
    private bool _showError   = true;
    private bool _autoScroll  = true;
    private int  _prevEntryCount;

    // -------------------------------------------------------------------------
    // REPL tab state
    // -------------------------------------------------------------------------
    private byte[]  _replInputBuf  = new byte[512];
    private string  _replInput     = "";
    private readonly List<(string text, bool isResult)> _replHistory = new();
    private JintRuntime? _replRuntime;
    private Actor?       _replRuntimeActor;  // actor the runtime is bound to (null = scene REPL)

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw(Scene scene)
    {
        if (!ImGui.Begin("Output Log"))
        {
            ImGui.End();
            return;
        }

        if (ImGui.BeginTabBar("##ConsoleTabs"))
        {
            if (ImGui.BeginTabItem("Log"))
            {
                DrawLogTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("JS REPL"))
            {
                DrawReplTab(scene);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Log tab
    // -------------------------------------------------------------------------

    private void DrawLogTab()
    {
        // Toolbar
        if (ImGui.Button("Clear")) ConsoleLog.Clear();
        ImGui.SameLine();
        ImGui.Checkbox("Info",    ref _showInfo);
        ImGui.SameLine();
        ImGui.Checkbox("Warning", ref _showWarning);
        ImGui.SameLine();
        ImGui.Checkbox("Error",   ref _showError);
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);

        ImGui.Separator();

        float footerHeight = ImGui.GetStyle().ItemSpacing.Y + ImGui.GetFrameHeightWithSpacing();
        ImGui.BeginChild("##LogScroll", new Vector2(0f, -footerHeight), ImGuiChildFlags.None,
            ImGuiWindowFlags.HorizontalScrollbar);

        var entries = ConsoleLog.Entries;

        foreach (var entry in entries)
        {
            if (entry.Level == LogLevel.Info    && !_showInfo)    continue;
            if (entry.Level == LogLevel.Warning && !_showWarning) continue;
            if (entry.Level == LogLevel.Error   && !_showError)   continue;

            Vector4 color = entry.Level switch
            {
                LogLevel.Warning => new Vector4(1.0f, 0.85f, 0.1f, 1f),
                LogLevel.Error   => new Vector4(1.0f, 0.3f,  0.3f, 1f),
                _                => new Vector4(0.9f, 0.9f,  0.9f, 1f),
            };

            string prefix = entry.Level switch
            {
                LogLevel.Warning => "[WARN] ",
                LogLevel.Error   => "[ERR]  ",
                _                => "[INFO] ",
            };

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted($"{entry.Timestamp:HH:mm:ss}  {prefix}{entry.Message}");
            ImGui.PopStyleColor();
        }

        // Auto-scroll to bottom on new entries
        if (_autoScroll && entries.Count != _prevEntryCount)
        {
            ImGui.SetScrollHereY(1.0f);
            _prevEntryCount = entries.Count;
        }

        ImGui.EndChild();
    }

    // -------------------------------------------------------------------------
    // JS REPL tab
    // -------------------------------------------------------------------------

    private void DrawReplTab(Scene scene)
    {
        // Status / help text
        ImGui.TextDisabled("Evaluate JavaScript against the active scene. 'actor' and 'scene' globals are available.");
        ImGui.Separator();

        // Output area
        float footerHeight = ImGui.GetStyle().ItemSpacing.Y + ImGui.GetFrameHeightWithSpacing() + 4f;
        ImGui.BeginChild("##ReplOutput", new Vector2(0f, -footerHeight), ImGuiChildFlags.None,
            ImGuiWindowFlags.HorizontalScrollbar);

        foreach (var (text, isResult) in _replHistory)
        {
            if (isResult)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 1.0f, 0.5f, 1f));
                ImGui.TextUnformatted("  => " + text);
                ImGui.PopStyleColor();
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 1.0f, 1f));
                ImGui.TextUnformatted("> " + text);
                ImGui.PopStyleColor();
            }
        }

        ImGui.SetScrollHereY(1.0f);
        ImGui.EndChild();

        // Input row
        bool execute = false;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60f);
        var inputFlags = ImGuiInputTextFlags.EnterReturnsTrue;
        if (ImGui.InputText("##ReplIn", _replInputBuf, (uint)_replInputBuf.Length, inputFlags))
        {
            execute = true;
            ImGui.SetKeyboardFocusHere(-1);
        }
        ImGui.SameLine();
        if (ImGui.Button("Run", new Vector2(50f, 0f)))
            execute = true;

        if (execute)
        {
            _replInput = Encoding.UTF8.GetString(_replInputBuf).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(_replInput))
            {
                ExecuteRepl(_replInput, scene);
                _replInputBuf = new byte[512];
            }
        }
    }

    private void ExecuteRepl(string expression, Scene scene)
    {
        _replHistory.Add((expression, isResult: false));

        // Lazily create a minimal REPL runtime
        if (_replRuntime == null)
        {
            // Use a disposable actor as the REPL host
            _replRuntimeActor = new Actor("__REPL__");
            _replRuntime = new JintRuntime(_replRuntimeActor);

            // Inject scene reference as a JS global object (name list)
            _replRuntime.SetGlobal("scene", scene);
        }

        try
        {
            var result = _replRuntime.Evaluate(expression);
            string resultText = result?.ToString() ?? "(null)";
            _replHistory.Add((resultText, isResult: true));
        }
        catch (Exception ex)
        {
            _replHistory.Add(($"Error: {ex.Message}", isResult: true));
            ConsoleLog.Add($"[REPL] {ex.Message}", LogLevel.Error);
        }
    }
}
