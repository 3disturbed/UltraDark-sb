using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Scene;

/// <summary>
/// Chunk-based world streaming component. Attach to any Actor (typically a camera or
/// player). Each Update it computes which grid chunk the tracked actor occupies and
/// loads/unloads surrounding chunk scene files to keep the world seamless without
/// loading everything at once.
///
/// Chunk files are named  chunk_{cx}_{cy}.json  (e.g. chunk_0_0.json, chunk_-1_2.json)
/// and live under <see cref="ChunksDirectory"/>. Each file is a normal scene JSON that
/// is merged into the active scene additively via
/// <see cref="Core.SceneManager.LoadSceneAdditive"/>.
///
/// Loading is spread across frames — at most <see cref="MaxLoadsPerFrame"/> new chunks
/// are kicked off each Update to avoid IO spikes.
/// </summary>
public class WorldStreamer : Component
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    /// <summary>Directory (relative or absolute) that contains chunk JSON files.</summary>
    public string ChunksDirectory { get; set; } = "Scenes/Chunks/";

    /// <summary>Width of a single chunk in world units (pixels).</summary>
    public int ChunkWidth { get; set; } = 1024;

    /// <summary>Height of a single chunk in world units (pixels).</summary>
    public int ChunkHeight { get; set; } = 1024;

    /// <summary>
    /// How many chunks in each direction from the tracked actor to keep loaded.
    /// A value of 2 loads a 5×5 grid centred on the current chunk.
    /// </summary>
    public int LoadRadius { get; set; } = 2;

    /// <summary>
    /// Actor whose position drives chunk loading.
    /// Defaults to the owning Actor when null (set during <see cref="Awake"/>).
    /// </summary>
    public Actor? TrackedActor { get; set; }

    /// <summary>
    /// Number of new chunk loads initiated per frame. Spreading IO over multiple frames
    /// prevents stalls.
    /// </summary>
    public int MaxLoadsPerFrame { get; set; } = 1;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    // Grid coordinates of the chunk the tracked actor was in last frame
    private (int cx, int cy) _lastChunk = (int.MinValue, int.MinValue);

    // Set of chunk coordinates that are currently loaded (or queued for loading)
    private readonly HashSet<(int, int)> _loadedChunks = new();

    // FIFO queue of chunks to load — drained at MaxLoadsPerFrame per Update
    private readonly Queue<(int, int)>   _loadQueue    = new();

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public override void Awake()
    {
        // Default the tracked actor to the owning Actor
        TrackedActor ??= Actor;
    }

    public override void Update(float dt)
    {
        if (TrackedActor is null) return;

        Vector2 pos = TrackedActor.Transform.Position;

        // 1. Compute the chunk grid coordinate the actor currently occupies
        int cx = FloorDiv((int)pos.X, ChunkWidth);
        int cy = FloorDiv((int)pos.Y, ChunkHeight);

        // 2. If the actor moved into a new chunk, recalculate desired / undesired chunks
        if ((cx, cy) != _lastChunk)
        {
            _lastChunk = (cx, cy);
            RecalculateChunks(cx, cy);
        }

        // 3. Drain the load queue: at most MaxLoadsPerFrame per frame
        int loads = 0;
        while (loads < MaxLoadsPerFrame && _loadQueue.Count > 0)
        {
            var chunk = _loadQueue.Dequeue();

            // Guard: may have been marked loaded while queued
            if (_loadedChunks.Contains(chunk))
            {
                continue;
            }

            string path = ChunkPath(chunk.Item1, chunk.Item2);

            if (File.Exists(path))
            {
                _loadedChunks.Add(chunk);
                SBEngine.Instance.SceneManager.LoadSceneAdditive(path);
            }
            else
            {
                // File doesn't exist — mark as "loaded" to avoid re-queuing
                _loadedChunks.Add(chunk);
            }

            loads++;
        }
    }

    // -------------------------------------------------------------------------
    // Load / unload logic
    // -------------------------------------------------------------------------

    private void RecalculateChunks(int cx, int cy)
    {
        // Build the set of chunks that should be loaded (within LoadRadius)
        var desired = new HashSet<(int, int)>();
        for (int dx = -LoadRadius; dx <= LoadRadius; dx++)
        for (int dy = -LoadRadius; dy <= LoadRadius; dy++)
            desired.Add((cx + dx, cy + dy));

        // Queue any desired chunk that is not already loaded or queued
        foreach (var chunk in desired)
        {
            if (!_loadedChunks.Contains(chunk) && !IsInQueue(chunk))
                _loadQueue.Enqueue(chunk);
        }

        // Unload chunks outside the hysteresis margin (LoadRadius + 1)
        int unloadRadius = LoadRadius + 1;
        var toUnload = _loadedChunks
            .Where(c => Math.Abs(c.Item1 - cx) > unloadRadius ||
                        Math.Abs(c.Item2 - cy) > unloadRadius)
            .ToList();

        foreach (var chunk in toUnload)
            UnloadChunk(chunk);
    }

    private void UnloadChunk((int cx, int cy) chunk)
    {
        _loadedChunks.Remove(chunk);

        // Remove from load queue if not yet processed
        // Queue does not support direct removal — rebuild without the target
        if (_loadQueue.Count > 0 && IsInQueue(chunk))
        {
            var temp = _loadQueue.ToArray();
            _loadQueue.Clear();
            foreach (var c in temp)
                if (c != chunk) _loadQueue.Enqueue(c);
        }

        // Unload the additive scene by the derived scene name
        string sceneName = ChunkSceneName(chunk.cx, chunk.cy);
        SBEngine.Instance.SceneManager.UnloadScene(sceneName);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private bool IsInQueue((int, int) chunk)
    {
        foreach (var c in _loadQueue)
            if (c == chunk) return true;
        return false;
    }

    private string ChunkPath(int cx, int cy)
        => Path.Combine(ChunksDirectory, $"chunk_{cx}_{cy}.json");

    private static string ChunkSceneName(int cx, int cy)
        => $"chunk_{cx}_{cy}";

    /// <summary>
    /// Floor-divides <paramref name="n"/> by <paramref name="d"/>, correctly handling
    /// negative numerators so that, e.g., -1 / 1024 == -1 (not 0).
    /// </summary>
    private static int FloorDiv(int n, int d)
        => n >= 0 ? n / d : (n - d + 1) / d;
}
