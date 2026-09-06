using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;

namespace Cookies.InputMapping;

/// <summary>
/// Where a player's changed bindings live, and how one slot of an action is replaced.
/// </summary>
/// <remarks>
/// The engine's own <c>RebindAction</c> replaces every binding an action has. That is right for
/// "bind this to that" and wrong for a rebinding screen: rebinding Jump to K would silently drop
/// its gamepad and touch bindings with it. <see cref="SetBinding"/> edits the live map in place
/// instead, which is possible because the map is now reachable.
/// </remarks>
public static class BindingStore
{
    /// <summary>Where changed bindings are written, relative to the project.</summary>
    public const string DefaultPath = "Saves/bindings.json";

    /// <summary>Applies saved bindings over the current map. Missing file: nothing happens.</summary>
    public static bool Load(InputManager input, string path = DefaultPath)
    {
        string full = ProjectPaths.Resolve(path);
        if (!File.Exists(full)) return false;

        try
        {
            input.LoadBindings(full);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[input-mapping] {path} could not be read: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes the current bindings, creating the folder if it is missing.</summary>
    public static bool Save(InputManager input, string path = DefaultPath)
    {
        try
        {
            string full = ProjectPaths.Resolve(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            input.SaveBindings(full);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[input-mapping] {path} could not be written: {ex.Message}");
            return false;
        }
    }

    /// <summary>Forgets every change, and deletes the file so a restart agrees.</summary>
    public static void Reset(InputManager input, string path = DefaultPath)
    {
        input.ResetBindings();

        try
        {
            string full = ProjectPaths.Resolve(path);
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception)
        {
            // The bindings are already back to their defaults in memory; a stale file is not worth failing over.
        }
    }

    /// <summary>
    /// Replaces one slot of an action, leaving its other bindings alone. Out-of-range slots append.
    /// </summary>
    public static void SetBinding(ActionMap map, string action, int slot, InputBinding binding)
    {
        if (!map.Actions.TryGetValue(action, out var existing))
            map.Actions[action] = existing = new InputAction(action);

        if (slot >= 0 && slot < existing.Bindings.Count) existing.Bindings[slot] = binding;
        else                                             existing.Bindings.Add(binding);
    }

    /// <summary>Removes one slot of an action.</summary>
    public static void ClearBinding(ActionMap map, string action, int slot)
    {
        if (map.Actions.TryGetValue(action, out var existing) && slot >= 0 && slot < existing.Bindings.Count)
            existing.Bindings.RemoveAt(slot);
    }

    /// <summary>Every action in the map, in a stable order for a settings screen.</summary>
    public static IReadOnlyList<string> ActionNames(ActionMap map)
        => map.Actions.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
}
