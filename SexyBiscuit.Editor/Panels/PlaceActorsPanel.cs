using System.Numerics;
using ImGuiNET;
using SexyBiscuit.Engine.AI;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scene;
using SexyBiscuit.Engine.Gameplay;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.UI;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;
using Scene = SexyBiscuit.Engine.Core.Scene;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// A searchable palette of ready-made actors, in the spirit of Unreal's Place Actors tab.
/// </summary>
/// <remarks>
/// The same presets are on the Create menu, but a docked palette is a different tool: it
/// stays open while you block out a level, and it can be searched. Building a lit 3D scene
/// from the menu means four separate trips; here it is three clicks in one place.
/// </remarks>
public sealed class PlaceActorsPanel
{
    /// <summary>One entry in the palette.</summary>
    private sealed record Entry(string Category, string Name, string Tooltip, Func<Actor> Build);

    private readonly List<Entry> _entries;
    private string _searchText = string.Empty;

    public PlaceActorsPanel()
    {
        // One list for the palette, the Create menu and the MCP place_actor tool.
        _entries = ActorPresets.All
            .Select(p => new Entry(p.Category, p.Name, p.Description, p.Build))
            .ToList();
    }

    public void Draw(Scene? scene)
    {
        if (!ImGui.Begin("Place Actors"))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##search", "Search\u2026", ref _searchText, 128);
        string filter = _searchText;

        ImGui.Separator();

        if (scene == null)
        {
            ImGui.TextDisabled("No scene open.");
            ImGui.End();
            return;
        }

        var matches = _entries.Where(e =>
            filter.Length == 0 ||
            e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            e.Category.Contains(filter, StringComparison.OrdinalIgnoreCase));

        // A search collapses the categories away — when you have typed a filter you want
        // the hits, not the taxonomy.
        bool searching = filter.Length > 0;

        foreach (var group in matches.GroupBy(e => e.Category))
        {
            if (!searching && !ImGui.CollapsingHeader(group.Key, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            if (searching) ImGui.TextDisabled(group.Key);

            foreach (var entry in group)
            {
                if (ImGui.Selectable($"  {entry.Name}"))
                    Place(scene, entry);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(entry.Tooltip);
            }

            if (searching) ImGui.Separator();
        }

        ImGui.End();
    }

    private static void Place(Scene scene, Entry entry)
    {
        var actor = entry.Build();
        scene.AddActor(actor, EditorState.SelectedLayer?.Name ?? "default");
        EditorState.SelectActor(actor);
        ConsoleLog.Add($"Placed {entry.Name}", LogLevel.Info);
    }
}
