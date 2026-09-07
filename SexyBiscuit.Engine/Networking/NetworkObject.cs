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

        nm.BroadcastSpawn(netObj.NetworkId, actor.Name, netObj.OwnerClientId, initialState);
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

        nm.BroadcastDespawn(netObj.NetworkId);

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

        // The body is written first and the count prepended, rather than reserving two bytes
        // and seeking back. NetWriter cannot seek by design: the blob crosses to the browser
        // engine, and a format that needs random access to encode is one more thing the two
        // implementations can disagree about.
        var body = new NetWriter();
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

            // Write: [memberName str][valueTag u8][value]
            body.String(memberInfo.Name);
            WriteMemberValue(body, currentValue);

            _lastSentValues[memberInfo] = DeepCopyValue(currentValue);
            writtenCount++;
        }

        var payload = new NetWriter(2 + body.Length);
        payload.Byte((byte)(writtenCount & 0xFF));
        payload.Byte((byte)(writtenCount >> 8));
        payload.Raw(body.ToArray());
        return payload.ToArray();
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

        var reader = new NetReader(data);
        int count  = reader.Byte() | (reader.Byte() << 8);

        for (int i = 0; i < count; i++)
        {
            string memberName = reader.String();

            if (!memberLookup.TryGetValue(memberName, out var entry))
            {
                // A member this build does not have. The tag says how long the value is, so
                // the rest of the blob still decodes -- which is what lets an older client
                // stay in a session with a newer server instead of dropping every update.
                SkipValue(ref reader);
                continue;
            }

            var (memberInfo, target) = entry;
            object? value = ReadMemberValue(ref reader, GetMemberType(memberInfo));
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

    private static void WriteMemberValue(NetWriter w, object? value)
    {
        switch (value)
        {
            case bool b:      w.Byte((byte)ValueTag.Bool).Bool(b); break;
            case byte by:     w.Byte((byte)ValueTag.Byte).Byte(by); break;
            case int i:       w.Byte((byte)ValueTag.Int32).Int(i); break;
            case uint u:      w.Byte((byte)ValueTag.UInt32).UInt(u); break;
            case long l:      w.Byte((byte)ValueTag.Int64).Double(l); break;
            case float f:     w.Byte((byte)ValueTag.Float).Float(f); break;
            case double d:    w.Byte((byte)ValueTag.Double).Double(d); break;
            case string s:    w.Byte((byte)ValueTag.String).String(s); break;
            case Vector2 v2:  w.Byte((byte)ValueTag.Vector2).Float(v2.X).Float(v2.Y); break;
            case Microsoft.Xna.Framework.Vector3 v3:
                w.Byte((byte)ValueTag.Vector3).Float(v3.X).Float(v3.Y).Float(v3.Z);
                break;
            default:
                // Anything else: a tag and an empty block, so a reader can step over it.
                w.Byte((byte)ValueTag.Unknown).Bytes(ReadOnlySpan<byte>.Empty);
                break;
        }
    }

    private static object? ReadMemberValue(ref NetReader r, Type expectedType)
    {
        var tag = (ValueTag)r.Byte();
        return tag switch
        {
            ValueTag.Bool    => (object)r.Bool(),
            ValueTag.Byte    => r.Byte(),
            ValueTag.Int32   => r.Int(),
            ValueTag.UInt32  => r.UInt(),
            // Int64 rides as a double: JavaScript has no 64-bit integer in a Number, and a
            // value that does not survive the round trip is worse than one that is documented
            // to carry 53 bits.
            ValueTag.Int64   => (long)r.Double(),
            ValueTag.Float   => r.Float(),
            ValueTag.Double  => r.Double(),
            ValueTag.String  => r.String(),
            ValueTag.Vector2 => new Vector2(r.Float(), r.Float()),
            ValueTag.Vector3 => new Microsoft.Xna.Framework.Vector3(r.Float(), r.Float(), r.Float()),
            _                => SkipUnknown(ref r),
        };
    }

    private static void SkipValue(ref NetReader r)
    {
        var tag = (ValueTag)r.Byte();
        switch (tag)
        {
            case ValueTag.Bool:    r.Bool();   break;
            case ValueTag.Byte:    r.Byte();   break;
            case ValueTag.Int32:   r.Int();    break;
            case ValueTag.UInt32:  r.UInt();   break;
            case ValueTag.Int64:   r.Double(); break;
            case ValueTag.Float:   r.Float();  break;
            case ValueTag.Double:  r.Double(); break;
            case ValueTag.String:  r.String(); break;
            case ValueTag.Vector2: r.Float(); r.Float(); break;
            case ValueTag.Vector3: r.Float(); r.Float(); r.Float(); break;
            default:               r.Bytes();  break;
        }
    }

    private static object? SkipUnknown(ref NetReader r)
    {
        r.Bytes();
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
