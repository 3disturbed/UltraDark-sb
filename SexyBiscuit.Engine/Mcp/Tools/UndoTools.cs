using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>Undo and redo over the snapshot stack every mutating tool feeds.</summary>
public sealed class UndoTools
{
    private readonly SceneUndoStack _undo;

    public UndoTools(SceneUndoStack undo) => _undo = undo;

    [McpTool("undo",
        "Undo the last N scene changes made through these tools (also Edit > Undo in the editor). The scene is " +
        "restored from a snapshot, so actor ids are regenerated — re-query them.",
        Label = "Undo")]
    public McpToolResult Undo([McpParam("How many changes to undo")] int steps = 1)
        => Run(steps, undo: true);

    [McpTool("redo", "Redo N undone changes. Actor ids are regenerated.", Label = "Redo")]
    public McpToolResult Redo([McpParam("How many changes to redo")] int steps = 1)
        => Run(steps, undo: false);

    private McpToolResult Run(int steps, bool undo)
    {
        if (steps < 1) throw new McpToolException("steps must be at least 1.");

        var labels = new JsonArray();
        for (int i = 0; i < steps; i++)
        {
            bool ok = undo ? _undo.Undo(out var label) : _undo.Redo(out label);
            if (!ok) break;
            labels.Add(label);
        }

        string verb = undo ? "Undid" : "Redid";
        var result = new JsonObject
        {
            [undo ? "undone" : "redone"] = labels,
            ["undoRemaining"] = _undo.UndoCount,
            ["redoRemaining"] = _undo.RedoCount,
            ["note"]          = SceneToolSupport.IdsRegeneratedNote,
        };

        if (labels.Count == 0)
            return McpToolResult.Json(result, undo ? "Nothing to undo." : "Nothing to redo.");

        return McpToolResult.Json(result, $"{verb} {labels.Count} change(s).");
    }
}
