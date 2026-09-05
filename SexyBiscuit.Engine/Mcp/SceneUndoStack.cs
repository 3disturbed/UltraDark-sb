using SexyBiscuit.Engine.Scene;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// Snapshot-based undo for scene edits: the whole scene is serialised before every mutating
/// tool call and restored on undo.
/// </summary>
/// <remarks>
/// A command system would be finer-grained, but the editor has none and the serialiser
/// already round-trips everything a tool can change. Restoring replaces every actor instance,
/// so runtime ids change — results say so, and the previously selected actor is re-selected
/// by name. Snapshots identical to the top of the stack (a tool that changed nothing) are not
/// pushed, and the stack is capped by count and by bytes.
/// </remarks>
public sealed class SceneUndoStack
{
    private sealed record Entry(string Label, string Json, string? ScenePath, string? SelectedName);

    private readonly IMcpSceneHost _host;
    private readonly List<Entry>   _undo = new();
    private readonly List<Entry>   _redo = new();
    private readonly int           _maxEntries;
    private readonly long          _maxBytes;

    public SceneUndoStack(IMcpSceneHost host, int maxEntries = 50, long maxBytes = 64L * 1024 * 1024)
    {
        _host       = host;
        _maxEntries = Math.Max(1, maxEntries);
        _maxBytes   = Math.Max(1024, maxBytes);
    }

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    public IReadOnlyList<string> UndoLabels => _undo.Select(e => e.Label).ToList();
    public IReadOnlyList<string> RedoLabels => _redo.Select(e => e.Label).ToList();

    /// <summary>Records the current scene under a label. Main thread only.</summary>
    public void Snapshot(string label)
    {
        var scene = _host.ActiveScene;
        if (scene == null) return;

        scene.FlushPendingActors();
        string json = SceneSerializer.Serialize(scene);

        if (_undo.Count > 0 && _undo[^1].Json == json) return;

        _undo.Add(new Entry(label, json, _host.CurrentScenePath, _host.SelectedActor?.Name));
        _redo.Clear();
        Trim();
    }

    public bool Undo(out string? label) => Step(_undo, _redo, out label);

    public bool Redo(out string? label) => Step(_redo, _undo, out label);

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private bool Step(List<Entry> from, List<Entry> to, out string? label)
    {
        label = null;
        if (from.Count == 0) return false;

        var entry = from[^1];
        from.RemoveAt(from.Count - 1);

        // The state being left becomes the other stack's top, under the same label.
        var current = _host.ActiveScene;
        if (current != null)
        {
            current.FlushPendingActors();
            to.Add(new Entry(entry.Label, SceneSerializer.Serialize(current), _host.CurrentScenePath, _host.SelectedActor?.Name));
        }

        var restored = SceneSerializer.Deserialize(entry.Json);
        _host.AdoptScene(restored);
        restored.FlushPendingActors();

        _host.CurrentScenePath = entry.ScenePath;
        _host.SceneDirty       = true;

        if (entry.SelectedName != null)
            _host.SelectActor(restored.FindByName(entry.SelectedName));

        label = entry.Label;
        return true;
    }

    private void Trim()
    {
        while (_undo.Count > _maxEntries)
            _undo.RemoveAt(0);

        long bytes = _undo.Sum(e => (long)e.Json.Length);
        while (_undo.Count > 1 && bytes > _maxBytes)
        {
            bytes -= _undo[0].Json.Length;
            _undo.RemoveAt(0);
        }
    }
}
