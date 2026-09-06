using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Scene;

/// <summary>
/// JSON-backed prefab system. A Prefab is a serialised single <see cref="Actor"/>
/// stored on disk. Prefab JSON is cached in memory after the first load so repeated
/// instantiation does not hit the filesystem.
/// </summary>
public static class Prefab
{
    // -------------------------------------------------------------------------
    // JSON cache — keyed by absolute (or relative) prefab path
    // -------------------------------------------------------------------------
    private static readonly ConcurrentDictionary<string, string> _jsonCache = new();

    // -------------------------------------------------------------------------
    // Instantiate overloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Instantiates a prefab at the world origin and adds it to the active scene.
    /// </summary>
    public static Actor Instantiate(string prefabPath)
        => InstantiateInternal(prefabPath, null, null);

    /// <summary>
    /// Instantiates a prefab at <paramref name="position"/> and adds it to the active scene.
    /// </summary>
    public static Actor Instantiate(string prefabPath, Vector2 position)
        => InstantiateInternal(prefabPath, position, null);

    /// <summary>
    /// Instantiates a prefab at <paramref name="position"/> with <paramref name="rotation"/>
    /// (in radians) and adds it to the active scene.
    /// </summary>
    public static Actor Instantiate(string prefabPath, Vector2 position, float rotation)
        => InstantiateInternal(prefabPath, position, rotation);

    /// <summary>
    /// Instantiates a prefab and returns it cast to <typeparamref name="T"/>.
    /// Throws <see cref="InvalidCastException"/> if the actor is not of that type.
    /// Note: the Actor type written to JSON must match <typeparamref name="T"/> for a
    /// successful cast; for plain <see cref="Actor"/> prefabs, prefer subclassing via
    /// a custom loader.
    /// </summary>
    public static T InstantiateAs<T>(string prefabPath) where T : Actor
    {
        var actor = Instantiate(prefabPath);
        if (actor is T typed) return typed;
        throw new InvalidCastException(
            $"Prefab '{prefabPath}' deserialised to '{actor.GetType().Name}', cannot cast to '{typeof(T).Name}'.");
    }

    // -------------------------------------------------------------------------
    // Save
    // -------------------------------------------------------------------------

    /// <summary>Serialises <paramref name="actor"/> to <paramref name="prefabPath"/> (synchronous).</summary>
    public static void Save(Actor actor, string prefabPath)
    {
        string json = SerializeActor(actor);
        string dir  = Path.GetDirectoryName(prefabPath)!;
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(prefabPath, json);

        // Invalidate / update cache
        _jsonCache[NormalisePath(prefabPath)] = json;
    }

    /// <summary>
    /// Asynchronously serialises <paramref name="actor"/> to <paramref name="prefabPath"/>
    /// on a background thread. Fire-and-forget; errors are written to stderr.
    /// </summary>
    public static void SaveAsync(Actor actor, string prefabPath)
    {
        string json     = SerializeActor(actor);
        string normPath = NormalisePath(prefabPath);

        // Update cache immediately so subsequent Instantiate calls see the new data
        _jsonCache[normPath] = json;

        Task.Run(async () =>
        {
            try
            {
                string dir = Path.GetDirectoryName(prefabPath)!;
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(prefabPath, json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Prefab.SaveAsync] Failed to write '{prefabPath}': {ex.Message}");
            }
        });
    }

    // -------------------------------------------------------------------------
    // Cache management
    // -------------------------------------------------------------------------

    /// <summary>Removes the cached JSON for <paramref name="prefabPath"/>, forcing a re-read next instantiation.</summary>
    public static void InvalidateCache(string prefabPath)
        => _jsonCache.TryRemove(NormalisePath(prefabPath), out _);

    /// <summary>Clears the entire prefab JSON cache.</summary>
    public static void ClearCache() => _jsonCache.Clear();

    // -------------------------------------------------------------------------
    // Internal implementation
    // -------------------------------------------------------------------------

    private static Actor InstantiateInternal(string prefabPath, Vector2? position, float? rotation)
    {
        string json  = GetOrLoadJson(prefabPath);
        Actor  actor = DeserializeActor(json);

        if (position.HasValue)
            actor.Transform.LocalPosition = position.Value;

        if (rotation.HasValue)
            actor.Transform.LocalRotation = rotation.Value;

        // Add to the active scene. Fall back gracefully when no scene is active.
        var activeScene = EngineHost.Current?.SceneManager.ActiveScene;
        if (activeScene is not null)
        {
            activeScene.AddActor(actor);
        }
        else
        {
            Console.Error.WriteLine(
                $"[Prefab] No active scene — actor '{actor.Name}' was created but not added to a scene.");
        }

        return actor;
    }

    private static string GetOrLoadJson(string prefabPath)
    {
        string key = NormalisePath(prefabPath);

        if (_jsonCache.TryGetValue(key, out string? cached))
            return cached;

        if (!File.Exists(prefabPath))
            throw new FileNotFoundException($"Prefab file not found: '{prefabPath}'", prefabPath);

        string json = File.ReadAllText(prefabPath);
        _jsonCache[key] = json;
        return json;
    }

    private static string SerializeActor(Actor actor)
    {
        ActorDto dto = SceneSerializer.BuildActorDto(actor);
        return JsonSerializer.Serialize(dto, SceneSerializer.Options);
    }

    private static Actor DeserializeActor(string json)
    {
        var dto = JsonSerializer.Deserialize<ActorDto>(json, SceneSerializer.Options)
                  ?? throw new JsonException("Failed to deserialise prefab JSON.");
        return SceneSerializer.BuildActor(dto);
    }

    private static string NormalisePath(string path)
        => Path.GetFullPath(path).ToLowerInvariant();
}
