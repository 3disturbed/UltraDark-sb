using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// Turns the actor reference a tool receives — <c>"42"</c>, <c>"#42"</c> or a name — into an
/// actor, with errors that tell Claude what to do instead.
/// </summary>
public static class ActorRef
{
    public const string IdsChangeNote = "Actor ids change after undo, redo or load; call get_scene_summary for the current ids and names.";

    public static Actor Resolve(Core.Scene scene, string reference)
    {
        var actor = TryResolve(scene, reference, out var error);
        return actor ?? throw error!;
    }

    public static Actor? TryResolve(Core.Scene scene, string reference, out McpToolException? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(reference))
        {
            error = new McpToolException("An actor id or name is required.");
            return null;
        }

        string key = reference.Trim();
        var all = scene.Layers.SelectMany(l => l.Actors).ToList();

        string idText = key.StartsWith('#') ? key[1..] : key;
        if (uint.TryParse(idText, out uint id))
        {
            var byId = all.FirstOrDefault(a => a.Id == id);
            if (byId != null) return byId;
        }

        var exact = all.Where(a => a.Name == key).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1)
        {
            error = Ambiguous(key, exact);
            return null;
        }

        var loose = all.Where(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (loose.Count == 1) return loose[0];
        if (loose.Count > 1)
        {
            error = Ambiguous(key, loose);
            return null;
        }

        error = new McpToolException($"No actor '{reference}' in the scene.", IdsChangeNote);
        return null;
    }

    private static McpToolException Ambiguous(string reference, List<Actor> matches)
        => new($"{matches.Count} actors are named '{reference}' (ids {string.Join(", ", matches.Select(a => a.Id))}).",
               "Use the id instead of the name.");
}
