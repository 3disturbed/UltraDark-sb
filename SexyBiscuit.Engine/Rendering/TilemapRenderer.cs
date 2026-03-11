using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Rendering;

// =============================================================================
// Data model
// =============================================================================

/// <summary>
/// Represents a parsed Tiled tilemap. Load via <see cref="TilemapData.LoadFromJson"/>.
/// </summary>
public class TilemapData
{
    /// <summary>Map width in tiles.</summary>
    public int Width { get; set; }

    /// <summary>Map height in tiles.</summary>
    public int Height { get; set; }

    /// <summary>Tile width in pixels.</summary>
    public int TileWidth { get; set; }

    /// <summary>Tile height in pixels.</summary>
    public int TileHeight { get; set; }

    /// <summary>All tile layers parsed from the Tiled JSON.</summary>
    public List<TileLayer> Layers { get; set; } = new();

    /// <summary>Tileset sprite atlas. Must be assigned after loading.</summary>
    public Texture2D? Tileset { get; set; }

    /// <summary>Number of tile columns in the tileset texture.</summary>
    public int TilesetColumns { get; set; }

    // -------------------------------------------------------------------------
    // Loading
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses a Tiled JSON export file (version 1.x format).
    /// Sets <see cref="Tileset"/> and <see cref="TilesetColumns"/> after loading.
    /// Supports infinite maps and CSV-encoded data arrays.
    /// </summary>
    /// <param name="jsonPath">Absolute or relative path to the .tmj / .json file.</param>
    /// <returns>Populated <see cref="TilemapData"/> — assign Tileset before rendering.</returns>
    public static TilemapData LoadFromJson(string jsonPath)
    {
        string json = File.ReadAllText(jsonPath);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        var raw = JsonSerializer.Deserialize<TiledMapRaw>(json, options)
                  ?? throw new InvalidDataException($"Failed to parse Tiled JSON at '{jsonPath}'.");

        var mapData = new TilemapData
        {
            Width      = raw.Width,
            Height     = raw.Height,
            TileWidth  = raw.Tilewidth,
            TileHeight = raw.Tileheight,
        };

        // Derive column count from first tileset entry if present
        if (raw.Tilesets is { Count: > 0 })
        {
            var ts = raw.Tilesets[0];
            if (ts.Columns > 0)
                mapData.TilesetColumns = ts.Columns;
        }

        if (raw.Layers != null)
        {
            foreach (var rawLayer in raw.Layers)
            {
                // Only process tile layers (type == "tilelayer")
                if (!string.Equals(rawLayer.Type, "tilelayer", StringComparison.OrdinalIgnoreCase))
                    continue;

                int[] tiles = ParseLayerData(rawLayer, raw.Width, raw.Height);

                mapData.Layers.Add(new TileLayer
                {
                    Name    = rawLayer.Name ?? "Layer",
                    Tiles   = tiles,
                    Visible = rawLayer.Visible,
                });
            }
        }

        return mapData;
    }

    private static int[] ParseLayerData(TiledLayerRaw raw, int mapWidth, int mapHeight)
    {
        int count = mapWidth * mapHeight;
        int[] result = new int[count];

        if (raw.Data != null)
        {
            // Standard integer array from JsonElement
            int idx = 0;
            foreach (var element in raw.Data)
            {
                if (idx >= count) break;
                result[idx++] = element.ValueKind == JsonValueKind.Number
                    ? element.GetInt32()
                    : 0;
            }
        }
        else if (!string.IsNullOrEmpty(raw.DataStr))
        {
            // CSV encoding
            string[] parts = raw.DataStr.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, count); i++)
            {
                if (int.TryParse(parts[i].Trim(), out int id))
                    result[i] = id;
            }
        }

        return result;
    }
}

/// <summary>A single tile layer from a Tiled map.</summary>
public class TileLayer
{
    /// <summary>Layer name as defined in Tiled.</summary>
    public string Name { get; set; } = "Layer";

    /// <summary>
    /// Flat array of tile IDs, width × height. Index = y * mapWidth + x.
    /// 0 = empty. IDs are 1-based (raw Tiled GID).
    /// </summary>
    public int[] Tiles { get; set; } = Array.Empty<int>();

    /// <summary>Whether this layer should be rendered.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Layer-level opacity (0..1). Multiplied with draw colour.</summary>
    public float Opacity { get; set; } = 1f;
}

// =============================================================================
// TilemapRenderer component
// =============================================================================

/// <summary>
/// Component that renders a <see cref="TilemapData"/> to the screen via SpriteBatch.
/// Attach to an Actor; the actor's Transform.Position is the world-space origin of tile (0,0).
/// </summary>
public class TilemapRenderer : Component
{
    // -------------------------------------------------------------------------
    // Data
    // -------------------------------------------------------------------------

    /// <summary>The tilemap data to render. Assign before the first Draw call.</summary>
    public TilemapData? Map { get; set; }

    /// <summary>
    /// Layer depth passed to SpriteBatch.Draw for every tile.
    /// 0 = front, 1 = back (when using BackToFront sort mode).
    /// </summary>
    public float LayerDepth { get; set; } = 0.5f;

    /// <summary>Tint applied to all tiles.</summary>
    public Color Tint { get; set; } = Color.White;

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public override void Draw(SpriteBatch sb)
    {
        if (Map == null || Map.Tileset == null) return;
        if (Map.TileWidth <= 0 || Map.TileHeight <= 0) return;

        int columns = Map.TilesetColumns > 0
            ? Map.TilesetColumns
            : Map.Tileset.Width / Map.TileWidth;

        if (columns < 1) columns = 1;

        Vector2 origin = Actor.Transform.Position;
        float   scale  = 1f; // Could be driven by Transform.Scale.X if uniform; kept simple

        foreach (var layer in Map.Layers)
        {
            if (!layer.Visible) continue;

            Color drawColor = Tint * layer.Opacity;

            for (int y = 0; y < Map.Height; y++)
            {
                for (int x = 0; x < Map.Width; x++)
                {
                    int gid = GetTileFromLayer(layer, x, y);
                    if (gid <= 0) continue;   // 0 = empty

                    // Convert 1-based GID to 0-based tileset index
                    int tileIndex = gid - 1;
                    int srcCol    = tileIndex % columns;
                    int srcRow    = tileIndex / columns;

                    Rectangle srcRect = new Rectangle(
                        srcCol * Map.TileWidth,
                        srcRow * Map.TileHeight,
                        Map.TileWidth,
                        Map.TileHeight);

                    Vector2 worldPos = new Vector2(
                        origin.X + x * Map.TileWidth  * scale,
                        origin.Y + y * Map.TileHeight * scale);

                    sb.Draw(
                        Map.Tileset,
                        worldPos,
                        srcRect,
                        drawColor,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        LayerDepth);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Coordinate helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts a world-space position to tile grid coordinates.
    /// The returned vector's components are floor-divided; they may be out of map range.
    /// </summary>
    public Vector2 WorldToTile(Vector2 worldPos)
    {
        if (Map == null || Map.TileWidth <= 0 || Map.TileHeight <= 0)
            return Vector2.Zero;

        Vector2 local = worldPos - Actor.Transform.Position;
        return new Vector2(
            MathF.Floor(local.X / Map.TileWidth),
            MathF.Floor(local.Y / Map.TileHeight));
    }

    /// <summary>
    /// Returns the tile GID at grid position (x, y) in the given layer index.
    /// Returns 0 if the coordinates are out of range or the layer does not exist.
    /// </summary>
    /// <param name="layerIndex">Zero-based layer index.</param>
    /// <param name="x">Tile column.</param>
    /// <param name="y">Tile row.</param>
    public int GetTile(int layerIndex, int x, int y)
    {
        if (Map == null) return 0;
        if (layerIndex < 0 || layerIndex >= Map.Layers.Count) return 0;
        return GetTileFromLayer(Map.Layers[layerIndex], x, y);
    }

    /// <summary>
    /// Returns the tile GID at grid position (x, y) in the layer with the given name.
    /// Returns 0 if no such layer exists or coordinates are out of range.
    /// </summary>
    public int GetTile(string layerName, int x, int y)
    {
        if (Map == null) return 0;
        var layer = Map.Layers.FirstOrDefault(l => l.Name == layerName);
        return layer == null ? 0 : GetTileFromLayer(layer, x, y);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private int GetTileFromLayer(TileLayer layer, int x, int y)
    {
        if (Map == null) return 0;
        if (x < 0 || y < 0 || x >= Map.Width || y >= Map.Height) return 0;

        int idx = y * Map.Width + x;
        if (idx < 0 || idx >= layer.Tiles.Length) return 0;

        return layer.Tiles[idx];
    }
}

// =============================================================================
// Internal raw JSON model (Tiled export format)
// =============================================================================

// These internal classes mirror the Tiled JSON schema enough for reliable parsing.
// They are not part of the public API.

internal sealed class TiledMapRaw
{
    [JsonPropertyName("width")]       public int Width      { get; set; }
    [JsonPropertyName("height")]      public int Height     { get; set; }
    [JsonPropertyName("tilewidth")]   public int Tilewidth  { get; set; }
    [JsonPropertyName("tileheight")]  public int Tileheight { get; set; }
    [JsonPropertyName("layers")]      public List<TiledLayerRaw>?   Layers   { get; set; }
    [JsonPropertyName("tilesets")]    public List<TiledTilesetRaw>? Tilesets { get; set; }
}

internal sealed class TiledLayerRaw
{
    [JsonPropertyName("name")]    public string? Name    { get; set; }
    [JsonPropertyName("type")]    public string? Type    { get; set; }
    [JsonPropertyName("visible")] public bool    Visible { get; set; } = true;
    [JsonPropertyName("opacity")] public float   Opacity { get; set; } = 1f;
    [JsonPropertyName("width")]   public int     Width   { get; set; }
    [JsonPropertyName("height")]  public int     Height  { get; set; }

    /// <summary>Integer array in standard Tiled JSON (non-compressed, non-encoded).</summary>
    [JsonPropertyName("data")]
    public List<JsonElement>? Data { get; set; }

    /// <summary>CSV string — populated when encoding is "csv".</summary>
    [JsonPropertyName("datastr")]
    public string? DataStr { get; set; }
}

internal sealed class TiledTilesetRaw
{
    [JsonPropertyName("firstgid")]  public int    FirstGid { get; set; }
    [JsonPropertyName("columns")]   public int    Columns  { get; set; }
    [JsonPropertyName("source")]    public string? Source  { get; set; }
    [JsonPropertyName("image")]     public string? Image   { get; set; }
    [JsonPropertyName("tilewidth")] public int    TileWidth  { get; set; }
    [JsonPropertyName("tileheight")]public int    TileHeight { get; set; }
}
