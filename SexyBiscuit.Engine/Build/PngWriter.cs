using System.IO.Compression;

namespace SexyBiscuit.Engine.Build;

/// <summary>
/// Writes a solid-colour PNG, for a placeholder app icon.
/// </summary>
/// <remarks>
/// A PWA needs 192 and 512 pixel icons to be installable, and a prototype does not have
/// artwork yet. A flat square in the theme colour is enough for a home screen until someone
/// draws one. Mirrors <c>html5/tools/lib/png.js</c>.
/// </remarks>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes a <paramref name="size"/> pixel square of one colour to <paramref name="path"/>.</summary>
    public static void WriteSolid(string path, int size, byte r, byte g, byte b, byte a = 255)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Solid(size, r, g, b, a));
    }

    /// <summary>The bytes of a <paramref name="size"/> pixel square of one colour.</summary>
    public static byte[] Solid(int size, byte r, byte g, byte b, byte a = 255)
    {
        // One filter byte (0 = none) then RGBA pixels, per row.
        var raw = new byte[size * (1 + size * 4)];
        for (int y = 0; y < size; y++)
        {
            int row = y * (1 + size * 4);
            for (int x = 0; x < size; x++)
            {
                int i = row + 1 + x * 4;
                raw[i] = r; raw[i + 1] = g; raw[i + 2] = b; raw[i + 3] = a;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, (uint)size);
        WriteBigEndian(ihdr, 4, (uint)size);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>Parses <c>#RRGGBB</c> or <c>#RRGGBBAA</c>; anything else is the runtime's dark theme colour.</summary>
    public static (byte R, byte G, byte B, byte A) ParseHex(string? text)
    {
        string hex = (text ?? string.Empty).TrimStart('#');
        if (hex.Length is 6 or 8 && hex.All(Uri.IsHexDigit))
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            byte a = hex.Length == 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
            return (r, g, b, a);
        }
        return (0x12, 0x14, 0x1a, 0xff);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = 0xffffffffu;
        foreach (var b in typeBytes) crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        foreach (var b in data)      crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc ^ 0xffffffffu);
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset]     = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
