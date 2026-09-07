using System.Buffers.Binary;
using System.Text;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// NetProtocol.cs
// The wire, shared with the browser engine.
// ---------------------------------------------------------------------------

/// <summary>
/// Message ids. The first byte of every frame.
/// </summary>
/// <remarks>
/// These are explicit numbers, never derived from a type name at runtime: renumbering one
/// breaks every build already in players' hands. The same table lives in
/// <c>html5/src/net/protocol.js</c> and a test on each side reads the other's source, so the
/// two cannot drift.
/// </remarks>
public static class NetMessage
{
    /// <summary>C to S, JSON: <c>{proto, name, room, token}</c>. The first frame a client sends.</summary>
    public const byte Hello = 0x01;

    /// <summary>S to C, JSON: <c>{proto, clientId, room, players:[{id, name}]}</c>.</summary>
    public const byte Welcome = 0x02;

    /// <summary>S to C: <c>[netId u32][name str][owner i32][state bytes]</c>.</summary>
    public const byte Spawn = 0x03;

    /// <summary>S to C: <c>[netId u32]</c>.</summary>
    public const byte Despawn = 0x04;

    /// <summary>Either way: <c>[netId u32][state bytes]</c>. The replication hot path.</summary>
    public const byte State = 0x05;

    /// <summary>Either way: <c>[netId u32][flags u8][target i32][method str][args bytes]</c>.</summary>
    public const byte Rpc = 0x06;

    /// <summary>
    /// Either way: <c>[sender i32][type str][json str]</c>. The channel a game script reaches
    /// through <c>Network.sendToAll(type, data)</c>; the engine never interprets the payload.
    /// </summary>
    public const byte Message = 0x07;

    /// <summary>C to S: <c>[clientTime f64]</c>.</summary>
    public const byte Ping = 0x08;

    /// <summary>S to C: <c>[clientTime f64][serverTimeMs f64]</c>.</summary>
    public const byte Pong = 0x09;

    /// <summary>S to C: <c>[clientId i32][name str]</c>. Another player joined.</summary>
    public const byte PeerJoined = 0x0A;

    /// <summary>S to C: <c>[clientId i32]</c>. Another player left.</summary>
    public const byte PeerLeft = 0x0B;

    /// <summary>S to C: <c>[reason str]</c>. Sent immediately before the connection is closed.</summary>
    public const byte Kick = 0x0C;
}

/// <summary>
/// The protocol version, carried in <see cref="NetMessage.Hello"/> and
/// <see cref="NetMessage.Welcome"/>.
/// </summary>
/// <remarks>
/// Bump this whenever a frame's layout changes. A server refuses a client whose number differs
/// rather than mis-decoding its frames forever, which is the failure that is impossible to
/// diagnose from a bug report.
/// </remarks>
public static class NetProtocolVersion
{
    public const int Current = 1;
}

/// <summary>How a frame should be delivered.</summary>
/// <remarks>
/// A transport that cannot honour a mode satisfies it with a stronger one. WebSocket delivers
/// everything reliably ordered, which is a valid — if wasteful — way to meet
/// <see cref="Unreliable"/>, and is why both transports exist.
/// </remarks>
public enum NetDelivery
{
    /// <summary>
    /// Drop rather than delay. State updates go this way: a lost one is a skipped frame the
    /// next update replaces, whereas a re-sent one arrives already stale and holds up the
    /// fresher ones behind it.
    /// </summary>
    Unreliable,

    /// <summary>Must arrive, and in order. Handshakes, spawns, RPCs, script messages.</summary>
    ReliableOrdered,

    /// <summary>Must arrive, order irrelevant. Cheaper than ordering when nothing depends on it.</summary>
    ReliableUnordered,
}

/// <summary>
/// Writes the wire format: little-endian numbers and <c>[u16 byte length][UTF-8]</c> strings.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="System.IO.BinaryWriter"/>, which the first version of this used.
/// Its <c>Write(string)</c> prefixes a 7-bit-encoded length — a .NET detail with no natural
/// JavaScript counterpart — and its endianness is whatever the machine is. Both would have made
/// a browser client that agrees with a native server impossible to write correctly.
/// </remarks>
public sealed class NetWriter
{
    private byte[] _buffer;
    private int    _length;

    public NetWriter(int capacity = 256) => _buffer = new byte[capacity];

    /// <summary>Bytes written so far.</summary>
    public int Length => _length;

    /// <summary>Starts a frame with its message id.</summary>
    public NetWriter(byte messageId, int capacity = 256) : this(capacity) => Byte(messageId);

    public NetWriter Byte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
        return this;
    }

    public NetWriter Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    public NetWriter Int(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public NetWriter UInt(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public NetWriter Float(float value)
    {
        Ensure(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
        return this;
    }

    public NetWriter Double(double value)
    {
        Ensure(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
        return this;
    }

    /// <summary>A UTF-8 string, length-prefixed with a u16. Longer strings are truncated.</summary>
    public NetWriter String(string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > ushort.MaxValue) bytes = bytes[..ushort.MaxValue];

        Ensure(2 + bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), (ushort)bytes.Length);
        _length += 2;
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        return this;
    }

    /// <summary>A byte block, length-prefixed with a u32.</summary>
    public NetWriter Bytes(ReadOnlySpan<byte> value)
    {
        Ensure(4 + value.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), (uint)value.Length);
        _length += 4;
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
        return this;
    }

    /// <summary>Bytes with no length prefix; only valid as the last field of a frame.</summary>
    public NetWriter Raw(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
        return this;
    }

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        int size = Math.Max(_buffer.Length * 2, _length + extra);
        Array.Resize(ref _buffer, size);
    }
}

/// <summary>
/// Reads what <see cref="NetWriter"/> wrote. Every read is bounds-checked: the bytes come off a
/// socket, so a truncated or hostile frame must be a caught exception rather than a crash.
/// </summary>
public ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    public NetReader(ReadOnlySpan<byte> data)
    {
        _data   = data;
        _offset = 0;
    }

    /// <summary>Bytes not yet read.</summary>
    public int Remaining => _data.Length - _offset;

    public byte Byte()
    {
        Need(1);
        return _data[_offset++];
    }

    public bool Bool() => Byte() != 0;

    public int Int()
    {
        Need(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(_data[_offset..]);
        _offset += 4;
        return value;
    }

    public uint UInt()
    {
        Need(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
        _offset += 4;
        return value;
    }

    public float Float()
    {
        Need(4);
        float value = BinaryPrimitives.ReadSingleLittleEndian(_data[_offset..]);
        _offset += 4;
        return value;
    }

    public double Double()
    {
        Need(8);
        double value = BinaryPrimitives.ReadDoubleLittleEndian(_data[_offset..]);
        _offset += 8;
        return value;
    }

    public string String()
    {
        Need(2);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(_data[_offset..]);
        _offset += 2;
        Need(length);
        string value = Encoding.UTF8.GetString(_data.Slice(_offset, length));
        _offset += length;
        return value;
    }

    public byte[] Bytes()
    {
        Need(4);
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data[_offset..]);
        _offset += 4;
        Need(length);
        var value = _data.Slice(_offset, length).ToArray();
        _offset += length;
        return value;
    }

    /// <summary>Everything left, with no length prefix.</summary>
    public byte[] Rest()
    {
        var value = _data[_offset..].ToArray();
        _offset = _data.Length;
        return value;
    }

    private void Need(int count)
    {
        if (_offset + count > _data.Length)
            throw new NetProtocolException(
                $"frame is {_data.Length} bytes; reading {count} more at offset {_offset} runs past the end");
    }
}

/// <summary>The JSON frames: an id byte, then the object as a length-prefixed UTF-8 string.</summary>
public static class NetProtocol
{
    /// <summary>Encodes a JSON frame.</summary>
    public static byte[] EncodeJson(byte messageId, System.Text.Json.Nodes.JsonNode? value)
        => new NetWriter(messageId).String(value?.ToJsonString() ?? "{}").ToArray();

    /// <summary>Reads the object out of a JSON frame, positioned just after the id byte.</summary>
    public static System.Text.Json.Nodes.JsonObject? DecodeJson(ref NetReader reader)
    {
        string json = reader.String();
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new NetProtocolException($"frame does not carry JSON: {ex.Message}");
        }
    }
}

/// <summary>A frame that could not be decoded. Always caught: it means a peer, not a bug.</summary>
public sealed class NetProtocolException : Exception
{
    public NetProtocolException(string message) : base(message) { }
}
