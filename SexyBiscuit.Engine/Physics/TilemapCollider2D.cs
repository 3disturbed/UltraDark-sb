using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using nkast.Aether.Physics2D.Collision.Shapes;
using nkast.Aether.Physics2D.Common;
using nkast.Aether.Physics2D.Dynamics;
using AetherVec2 = nkast.Aether.Physics2D.Common.Vector2;

namespace SexyBiscuit.Engine.Physics;

/// <summary>
/// Generates static collision geometry from a <see cref="TilemapRenderer"/> layer.
/// </summary>
/// <remarks>
/// <para>
/// A naive one-body-per-tile approach makes a 200×200 map cost 40,000 fixtures, most of them
/// interior edges nothing can ever touch. This component instead merges solid tiles into
/// maximal rectangles with a greedy sweep, which typically collapses a level's floors and
/// walls into a few dozen boxes.
/// </para>
/// <para>
/// The geometry is static, so rebuild it with <see cref="Rebuild"/> after changing tiles —
/// destructible terrain, a level editor, or a procedurally extended map.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var collider = mapActor.AddComponent&lt;TilemapCollider2D&gt;();
/// collider.LayerName    = "collision";
/// collider.IsSolid      = tileId =&gt; tileId &gt; 0;      // any non-empty tile blocks
/// collider.Rebuild();
/// </code>
/// </example>
public sealed class TilemapCollider2D : Component
{
    /// <summary>Name of the tile layer to read. Empty means the first layer.</summary>
    public string LayerName { get; set; } = "";

    /// <summary>
    /// Decides whether a tile id blocks movement. Defaults to "any non-zero tile is solid".
    /// </summary>
    public Func<int, bool> IsSolid { get; set; } = id => id > 0;

    /// <summary>Friction applied to every generated fixture.</summary>
    public float Friction { get; set; } = 0.3f;

    /// <summary>Bounciness applied to every generated fixture.</summary>
    public float Restitution { get; set; }

    /// <summary>Generated shapes act as triggers rather than solid geometry.</summary>
    public bool IsTrigger { get; set; }

    /// <summary>
    /// Merges adjacent solid tiles into larger rectangles. Turn off only when debugging —
    /// it costs one fixture per solid tile.
    /// </summary>
    public bool MergeTiles { get; set; } = true;

    /// <summary>Number of fixtures produced by the most recent build.</summary>
    public int FixtureCount { get; private set; }

    private Body? _body;
    private TilemapRenderer? _tilemap;

    public override void Start() => Rebuild();

    /// <summary>
    /// Discards any existing collision geometry and regenerates it from the current tile data.
    /// Safe to call at runtime.
    /// </summary>
    public void Rebuild()
    {
        Clear();

        _tilemap ??= Actor.GetComponent<TilemapRenderer>();
        var map = _tilemap?.Map;
        if (map == null || map.Layers.Count == 0) return;

        var layer = string.IsNullOrEmpty(LayerName)
            ? map.Layers[0]
            : map.Layers.FirstOrDefault(l => l.Name == LayerName);

        if (layer == null)
        {
            Console.Error.WriteLine($"[TilemapCollider2D] No layer named '{LayerName}' on the tilemap.");
            return;
        }

        var rectangles = MergeTiles
            ? MergeSolidTiles(layer.Tiles, map.Width, map.Height)
            : EnumerateSolidTiles(layer.Tiles, map.Width, map.Height);

        _body = PhysicsSystem2D.Instance.CreateBody(Actor, BodyType.Static);

        var origin = Actor.Transform.Position;
        foreach (var rect in rectangles)
        {
            float w = rect.Width  * map.TileWidth;
            float h = rect.Height * map.TileHeight;

            // Aether takes half-extents around a local centre offset.
            var centre = new AetherVec2(
                origin.X + rect.X * map.TileWidth  + w * 0.5f,
                origin.Y + rect.Y * map.TileHeight + h * 0.5f);

            var vertices = PolygonTools.CreateRectangle(w * 0.5f, h * 0.5f, centre, 0f);
            var fixture  = _body.CreateFixture(new PolygonShape(vertices, 1f));

            fixture.Friction    = Friction;
            fixture.Restitution = Restitution;
            fixture.IsSensor    = IsTrigger;
            FixtureCount++;
        }
    }

    /// <summary>Removes all generated collision geometry.</summary>
    public void Clear()
    {
        if (_body != null)
        {
            PhysicsSystem2D.Instance.UnregisterBody(Actor);
            PhysicsSystem2D.Instance.World.Remove(_body);
            _body = null;
        }

        FixtureCount = 0;
    }

    public override void OnDestroy() => Clear();

    // -------------------------------------------------------------------------
    // Rectangle merging
    // -------------------------------------------------------------------------

    private IEnumerable<Rectangle> EnumerateSolidTiles(int[] tiles, int width, int height)
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (index < tiles.Length && IsSolid(tiles[index]))
                    yield return new Rectangle(x, y, 1, 1);
            }
    }

    /// <summary>
    /// Collapses solid tiles into maximal rectangles.
    /// </summary>
    /// <remarks>
    /// Greedy two-pass sweep: extend a run as far right as it stays solid, then extend that
    /// run downward while every row below matches it exactly. Not the theoretical minimum
    /// rectangle cover, but linear in tile count and it removes the interior edges that
    /// actually cost simulation time.
    /// </remarks>
    private List<Rectangle> MergeSolidTiles(int[] tiles, int width, int height)
    {
        var consumed = new bool[width * height];
        var result   = new List<Rectangle>();

        bool Solid(int x, int y)
        {
            int i = y * width + x;
            return i < tiles.Length && !consumed[i] && IsSolid(tiles[i]);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!Solid(x, y)) continue;

                // Extend right.
                int runWidth = 1;
                while (x + runWidth < width && Solid(x + runWidth, y)) runWidth++;

                // Extend down while the whole run stays solid.
                int runHeight = 1;
                while (y + runHeight < height)
                {
                    bool rowMatches = true;
                    for (int i = 0; i < runWidth; i++)
                    {
                        if (Solid(x + i, y + runHeight)) continue;
                        rowMatches = false;
                        break;
                    }

                    if (!rowMatches) break;
                    runHeight++;
                }

                for (int dy = 0; dy < runHeight; dy++)
                    for (int dx = 0; dx < runWidth; dx++)
                        consumed[(y + dy) * width + x + dx] = true;

                result.Add(new Rectangle(x, y, runWidth, runHeight));
                x += runWidth - 1;
            }
        }

        return result;
    }
}
