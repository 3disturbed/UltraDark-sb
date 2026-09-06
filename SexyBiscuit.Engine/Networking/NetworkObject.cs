using System.Reflection;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// NetworkObject.cs
// Component that makes an Actor network-aware. Attach to any Actor to opt it
// into the replication and RPC systems.
// ---------------------------------------------------------------------------

/// <summary>
/// Component that gives an Actor a network identity, enabling state replication
/// and RPC dispatch. Must be added before registering with
/// <see cref="NetworkManager"/>.
/// </summary>
public class NetworkObject : Component
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------

    /// <summary>Unique identifier assigned by the server at spawn time.</summary>
    public uint NetworkId { get; internal set; }

    /// <summary>
    /// <c>true</c> on the client that owns (controls) this object.
    /// Always <c>true</c> on the server for server-owned objects.
    /// </summary>
    public bool IsOwner { get; internal set; }

    /// <summary><c>true</c> when running inside a server-mode NetworkManager.</summary>
    public bool IsServer { get; internal set; }

    /// <summary>
    /// The client ID that owns this object. <c>-1</c> means server-owned.
    /// </summary>
    public int OwnerClientId { get; internal set; } = -1;

    /// <summary>
    /// What the other machines look this object up by when they are told to spawn it. Falls back to
    /// the actor's name, which is neither unique nor stable once an author renames something.
    /// </summary>
    public string SpawnKey { get; set; } = "";

    /// <summary>The spawn key, or the actor's name when none was set.</summary>
    public string SpawnKeyOrName => string.IsNullOrWhiteSpace(SpawnKey) ? Actor.Name : SpawnKey;

    /// <summary>
    /// Hands this object to a client, before it is spawned. The server decides ownership, so this
    /// does nothing anywhere else.
    /// </summary>
    /// <remarks>
    /// The setter is internal because the transport assigns ownership when a spawn packet arrives.
    /// A game mode that spawns a pawn for a joining client has to say who it belongs to, though,
    /// and it lives outside this assembly.
    /// </remarks>
    public void AssignOwner(int clientId)
    {
        if (NetworkManager.Instance is not { IsServer: true }) return;

        OwnerClientId = clientId;
        IsOwner       = NetworkManager.Instance.LocalClientId == clientId;
    }

    // -------------------------------------------------------------------------
    // Dirty tracking — one entry per [Replicated] member on Actor + Components
    // -------------------------------------------------------------------------

    /// <summary>
    /// Snapshot of the last-sent values for dirty detection.
    /// Key: MemberInfo (FieldInfo or PropertyInfo) decorated with [Replicated].
    /// Value: deep-copied value at last send.
    /// </summary>
    private readonly Dictionary<MemberInfo, object?> _lastSentValues = new();

    /// <summary>
    /// Whether this is the first collection (forces a full send regardless of dirty state,
    /// needed for <see cref="ReplicateCondition.InitialOnly"/> and initial snapshots).
    /// </summary>
    private bool _initialSendPending = true;

    // -------------------------------------------------------------------------
    // Static spawn / despawn helpers (server-side)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Server-side: registers the actor's NetworkObject with the active
    /// <see cref="NetworkManager"/> and broadcasts a SpawnObject packet to all
    /// connected clients so they can instantiate a matching actor on their end.
    /// </summary>
    /// <param name="actor">The actor to spawn across the network.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when called outside of a running server context, or when the
    /// actor does not have a <see cref="NetworkObject"/> component.
    /// </exception>
    public static void Spawn(Actor actor)
    {
        var nm = NetworkManager.Instance
            ?? throw new InvalidOperationException("NetworkManager is not running.");

        if (!nm.IsServer)
            throw new InvalidOperationException("NetworkObject.Spawn must be called on the server.");

        var netObj = actor.GetComponent<NetworkObject>()
            ?? throw new InvalidOperationException(
                $"Actor '{actor.Name}' does not have a NetworkObject component.");

        // Assign a new network ID and register with the replication system
        netObj.NetworkId = nm.AllocateNetworkId();
        netObj.IsServer  = true;
        netObj.IsOwner   = true;

        nm.Replication.RegisterObject(netObj);
        nm.Rpc.Register(actor, netObj);

        // Build the initial state snapshot (full send)
        byte[] initialState = netObj.CollectLocalState(forceAll: true);

        // Encode spawn packet: [type(1)] [networkId(4)] [actorName(len-prefixed)] [state(rest)]
        using var ms  = new System.IO.MemoryStream();
        using var bw  = new System.IO.BinaryWriter(ms);
        bw.Write((byte)PacketType.SpawnObject);
        bw.Write(netObj.NetworkId);
        bw.Write(actor.Name);
        bw.Write(netObj.OwnerClientId);
        bw.Write(initialState.Length);
        bw.Write(initialState);
        bw.Flush();

        nm.SendToAll(ms.ToArray());
    }

    /// <summary>
    /// Server-side: unregisters the actor's NetworkObject and broadcasts a
    /// DespawnObject packet so all clients remove the corresponding actor.
    /// </summary>
    /// <param name="actor">The actor to despawn across the network.</param>
    public static void Despawn(Actor actor)
    {
        var nm = NetworkManager.Instance
            ?? throw new InvalidOperationException("NetworkManager is not running.");

        if (!nm.IsServer)
            throw new InvalidOperationException("NetworkObject.Despawn must be called on the server.");

        var netObj = actor.GetComponent<NetworkObject>()
            ?? throw new InvalidOperationException(
                $"Actor '{actor.Name}' does not have a NetworkObject component.");

        nm.Replication.UnregisterObject(netObj);
        nm.Rpc.Unregister(actor);

        // Encode despawn packet: [type(1)] [networkId(4)]
        using var ms  = new System.IO.MemoryStream();
        using var bw  = new System.IO.BinaryWriter(ms);
        bw.Write((byte)PacketType.DespawnObject);
        bw.Write(netObj.NetworkId);
        bw.Flush();

        nm.SendToAll(ms.ToArray());

        actor.Destroy();
    }

    // -------------------------------------------------------------------------
    // State collection — called by ReplicationSystem
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scans the Actor and all its Components for fields and properties marked
    /// with <see cref="ReplicatedAttribute"/>, compares them to the last-sent
    /// snapshot, and serialises any dirty (changed) values into a byte array.
    /// </summary>
    /// <returns>
    /// A byte array containing a count-prefixed sequence of dirty member entries.
    /// Returns an empty payload (just a zero count) when nothing has changed.
    /// </returns>
    internal byte[] CollectLocalState() => CollectLocalState(forceAll: false);

    /// <summary>
    /// Internal overload used by <see cref="Spawn"/> for the initial full snapshot.
    /// </summary>
    internal byte[] CollectLocalState(bool forceAll)
    {
        bool isFirstSend = _initialSendPending;
        _initialSendPending = false;

        // Gather all [Replicated] members across Actor and every Component,
        // sorted descending by Priority so high-priority data is written first.
        var members = GatherReplicatedMembers();

        using var ms      = new System.IO.MemoryStream();
        using var bw      = new System.IO.BinaryWriter(ms);

        // Reserve space for the member count (written at the end)
        long countPos = ms.Position;
        bw.Write((ushort)0); // placeholder

        ushort writtenCount = 0;

        foreach (var (memberInfo, attr, target) in members)
        {
            // Respect replication conditions
            if (attr.Condition == ReplicateCondition.InitialOnly && !isFirstSend && !forceAll)
                continue;

            if (attr.Condition == ReplicateCondition.OwnerOnly && !IsOwner && !IsServer)
                continue;

            object? currentValue = GetMemberValue(memberInfo, target);

            bool isDirty = forceAll
                || isFirstSend
                || !_lastSentValues.TryGetValue(memberInfo, out object? lastValue)
                || !ValuesEqual(currentValue, lastValue);

            if (!isDirty) continue;

            // Write: [memberName (string)] [value (typed)]
            bw.Write(memberInfo.Name);
            WriteMemberValue(bw, currentValue);

            _lastSentValues[memberInfo] = DeepCopyValue(currentValue);
            writtenCount++;
        }

        // Patch the count
        long endPos = ms.Position;
        ms.Seek(countPos, System.IO.SeekOrigin.Begin);
        bw.Write(writtenCount);
        ms.Seek(endPos, System.IO.SeekOrigin.Begin);

        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // State application — called by ReplicationSystem after receive
    // -------------------------------------------------------------------------

    /// <summary>
    /// Deserialises a state payload produced by <see cref="CollectLocalState()"/>
    /// and applies each value to the appropriate field or property on the Actor
    /// or one of its Components.
    /// </summary>
    /// <param name="data">Raw byte array as received from the network.</param>
    internal void ApplyRemoteState(byte[] data)
    {
        if (data == null || data.Length == 0) return;

        // Build a lookup of all [Replicated] members for fast access
        var memberLookup = BuildMemberLookup();

        using var ms = new System.IO.MemoryStream(data);
        using var br = new System.IO.BinaryReader(ms);

        ushort count = br.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            string memberName = br.ReadString();

            if (!memberLookup.TryGetValue(memberName, out var entry))
            {
                // Unknown member — skip by attempting to read based on the embedded type tag
                SkipValue(br);
                continue;
            }

            var (memberInfo, target) = entry;
            object? value = ReadMemberValue(br, GetMemberType(memberInfo));
            SetMemberValue(memberInfo, target, value);
        }
    }

    // -------------------------------------------------------------------------
    // Reflection helpers
    // -------------------------------------------------------------------------

    private const BindingFlags MemberFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// Returns all (MemberInfo, ReplicatedAttribute, target-object) triples
    /// on the Actor and all its Components, sorted by Priority descending.
    /// </summary>
    private List<(MemberInfo Member, ReplicatedAttribute Attr, object Target)> GatherReplicatedMembers()
    {
        var result = new List<(MemberInfo, ReplicatedAttribute, object)>();

        // Actor itself
        ScanObject(Actor, result);

        // Every Component (including this NetworkObject)
        foreach (var component in Actor.GetAllComponents())
            ScanObject(component, result);

        // Sort by priority descending
        result.Sort((a, b) => b.Item2.Priority.CompareTo(a.Item2.Priority));
        return result;
    }

    private static void ScanObject(
        object target,
        List<(MemberInfo, ReplicatedAttribute, object)> result)
    {
        Type t = target.GetType();

        foreach (FieldInfo fi in t.GetFields(MemberFlags))
        {
            var attr = fi.GetCustomAttribute<ReplicatedAttribute>();
            if (attr != null)
                result.Add((fi, attr, target));
        }

        foreach (PropertyInfo pi in t.GetProperties(MemberFlags))
        {
            if (!pi.CanRead || !pi.CanWrite) continue;
            var attr = pi.GetCustomAttribute<ReplicatedAttribute>();
            if (attr != null)
                result.Add((pi, attr, target));
        }
    }

    /// <summary>
    /// Builds a <c>Dictionary&lt;memberName, (memberInfo, target)&gt;</c> across all objects
    /// for fast lookup during ApplyRemoteState.
    /// </summary>
    private Dictionary<string, (MemberInfo, object)> BuildMemberLookup()
    {
        var lookup = new Dictionary<string, (MemberInfo, object)>(StringComparer.Ordinal);

        void Index(object target)
        {
            Type t = target.GetType();
            foreach (FieldInfo fi in t.GetFields(MemberFlags))
                if (fi.GetCustomAttribute<ReplicatedAttribute>() != null)
                    lookup.TryAdd(fi.Name, (fi, target));

            foreach (PropertyInfo pi in t.GetProperties(MemberFlags))
                if (pi.CanRead && pi.CanWrite && pi.GetCustomAttribute<ReplicatedAttribute>() != null)
                    lookup.TryAdd(pi.Name, (pi, target));
        }

        Index(Actor);
        foreach (var component in Actor.GetAllComponents())
            Index(component);

        return lookup;
    }

    // -------------------------------------------------------------------------
    // Member value accessors
    // -------------------------------------------------------------------------

    private static object? GetMemberValue(MemberInfo mi, object target) => mi switch
    {
        FieldInfo fi    => fi.GetValue(target),
        PropertyInfo pi => pi.GetValue(target),
        _               => null
    };

    private static void SetMemberValue(MemberInfo mi, object target, object? value)
    {
        switch (mi)
        {
            case FieldInfo fi:    fi.SetValue(target, value); break;
            case PropertyInfo pi: pi.SetValue(target, value); break;
        }
    }

    private static Type GetMemberType(MemberInfo mi) => mi switch
    {
        FieldInfo fi    => fi.FieldType,
        PropertyInfo pi => pi.PropertyType,
        _               => typeof(object)
    };

    // -------------------------------------------------------------------------
    // Serialisation helpers
    // -------------------------------------------------------------------------

    // Type tags embedded in the packet for each value so we can skip unknowns.
    private enum ValueTag : byte
    {
        Bool    = 1,
        Int32   = 2,
        UInt32  = 3,
        Float   = 4,
        String  = 5,
        Vector2 = 6,
        Vector3 = 7,
        Byte    = 8,
        Int64   = 9,
        Double  = 10,
        Unknown = 255,
    }

    private static void WriteMemberValue(System.IO.BinaryWriter bw, object? value)
    {
        switch (value)
        {
            case bool b:
                bw.Write((byte)ValueTag.Bool);
                bw.Write(b);
                break;
            case byte by:
                bw.Write((byte)ValueTag.Byte);
                bw.Write(by);
                break;
            case int i:
                bw.Write((byte)ValueTag.Int32);
                bw.Write(i);
                break;
            case uint u:
                bw.Write((byte)ValueTag.UInt32);
                bw.Write(u);
                break;
            case long l:
                bw.Write((byte)ValueTag.Int64);
                bw.Write(l);
                break;
            case float f:
                bw.Write((byte)ValueTag.Float);
                bw.Write(f);
                break;
            case double d:
                bw.Write((byte)ValueTag.Double);
                bw.Write(d);
                break;
            case string s:
                bw.Write((byte)ValueTag.String);
                bw.Write(s);
                break;
            case Vector2 v2:
                bw.Write((byte)ValueTag.Vector2);
                bw.Write(v2.X);
                bw.Write(v2.Y);
                break;
            case Microsoft.Xna.Framework.Vector3 v3:
                bw.Write((byte)ValueTag.Vector3);
                bw.Write(v3.X);
                bw.Write(v3.Y);
                bw.Write(v3.Z);
                break;
            default:
                // Unknown / unsupported type — write a zero-length sentinel
                bw.Write((byte)ValueTag.Unknown);
                bw.Write(0); // length = 0, no payload
                break;
        }
    }

    private static object? ReadMemberValue(System.IO.BinaryReader br, Type expectedType)
    {
        var tag = (ValueTag)br.ReadByte();
        return tag switch
        {
            ValueTag.Bool    => (object)br.ReadBoolean(),
            ValueTag.Byte    => br.ReadByte(),
            ValueTag.Int32   => br.ReadInt32(),
            ValueTag.UInt32  => br.ReadUInt32(),
            ValueTag.Int64   => br.ReadInt64(),
            ValueTag.Float   => br.ReadSingle(),
            ValueTag.Double  => br.ReadDouble(),
            ValueTag.String  => br.ReadString(),
            ValueTag.Vector2 => new Vector2(br.ReadSingle(), br.ReadSingle()),
            ValueTag.Vector3 => new Microsoft.Xna.Framework.Vector3(
                                    br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
            ValueTag.Unknown => SkipUnknown(br),
            _                => SkipUnknown(br),
        };
    }

    private static void SkipValue(System.IO.BinaryReader br)
    {
        var tag = (ValueTag)br.ReadByte();
        switch (tag)
        {
            case ValueTag.Bool:    br.ReadBoolean(); break;
            case ValueTag.Byte:    br.ReadByte();    break;
            case ValueTag.Int32:   br.ReadInt32();   break;
            case ValueTag.UInt32:  br.ReadUInt32();  break;
            case ValueTag.Int64:   br.ReadInt64();   break;
            case ValueTag.Float:   br.ReadSingle();  break;
            case ValueTag.Double:  br.ReadDouble();  break;
            case ValueTag.String:  br.ReadString();  break;
            case ValueTag.Vector2: br.ReadSingle(); br.ReadSingle(); break;
            case ValueTag.Vector3: br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); break;
            case ValueTag.Unknown:
                int len = br.ReadInt32();
                if (len > 0) br.ReadBytes(len);
                break;
        }
    }

    private static object? SkipUnknown(System.IO.BinaryReader br)
    {
        int len = br.ReadInt32();
        if (len > 0) br.ReadBytes(len);
        return null;
    }

    // -------------------------------------------------------------------------
    // Value comparison helpers
    // -------------------------------------------------------------------------

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        // Struct equality for MonoGame types
        if (a is Vector2 va && b is Vector2 vb)
            return va == vb;
        if (a is Microsoft.Xna.Framework.Vector3 v3a && b is Microsoft.Xna.Framework.Vector3 v3b)
            return v3a == v3b;

        return a.Equals(b);
    }

    private static object? DeepCopyValue(object? value) => value switch
    {
        // Value types and strings are already immutable / copied by assignment
        null    => null,
        string  => value,
        Vector2 => value,
        Microsoft.Xna.Framework.Vector3 => value,
        _       => value  // For primitive value types this is sufficient
    };
}
