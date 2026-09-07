using System.Reflection;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// ReplicationSystem.cs
// Drives [Replicated] attribute state synchronisation per fixed tick.
// ---------------------------------------------------------------------------

/// <summary>
/// Accumulates time and, at the configured <see cref="SendRate"/>, collects
/// dirty state from every registered <see cref="NetworkObject"/> and dispatches
/// StateUpdate packets to relevant peers via <see cref="NetworkManager"/>.
/// </summary>
public class ReplicationSystem
{
    // -------------------------------------------------------------------------
    // Configuration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Number of state-update transmissions per second. Default 20 Hz.
    /// Increase for faster-paced games; decrease to reduce bandwidth.
    /// </summary>
    public float SendRate { get; set; } = 20f;

    /// <summary>
    /// Maximum world-space distance (in units) between a NetworkObject and a
    /// client's owned object before the server stops sending updates to that
    /// client. Default 2000 units.
    /// </summary>
    public float InterestRadius { get; set; } = 2000f;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private readonly List<NetworkObject> _objects = new();
    private readonly object _lock = new();

    private float _accumulator;

    // Per-NetworkObject: cache of [Replicated] members for performance.
    // Built lazily on first Tick and invalidated when objects are registered.
    private readonly Dictionary<NetworkObject, CachedMemberSet> _memberCache = new();

    // -------------------------------------------------------------------------
    // Registration
    // -------------------------------------------------------------------------

    /// <summary>Adds a NetworkObject to the replication set.</summary>
    public void RegisterObject(NetworkObject obj)
    {
        lock (_lock)
        {
            if (!_objects.Contains(obj))
            {
                _objects.Add(obj);
                _memberCache.Remove(obj); // Force cache rebuild on next tick
            }
        }
    }

    /// <summary>Removes a NetworkObject from the replication set.</summary>
    public void UnregisterObject(NetworkObject obj)
    {
        lock (_lock)
        {
            _objects.Remove(obj);
            _memberCache.Remove(obj);
        }
    }

    // -------------------------------------------------------------------------
    // Tick — called by NetworkManager each fixed-update
    // -------------------------------------------------------------------------

    /// <summary>
    /// Accumulates <paramref name="dt"/> and flushes dirty state to the
    /// network when the send-rate interval elapses.
    /// </summary>
    /// <param name="dt">Delta time in seconds since the last fixed-update.</param>
    /// <param name="nm">The active NetworkManager used to send packets.</param>
    public void Tick(float dt, NetworkManager nm)
    {
        _accumulator += dt;
        float interval = 1f / SendRate;

        if (_accumulator < interval) return;
        _accumulator -= interval;

        List<NetworkObject> snapshot;
        lock (_lock)
        {
            snapshot = new List<NetworkObject>(_objects);
        }

        if (nm.IsServer)
            TickServer(snapshot, nm);
        else if (nm.IsClient)
            TickClient(snapshot, nm);
    }

    // -------------------------------------------------------------------------
    // Server tick
    // -------------------------------------------------------------------------

    private void TickServer(List<NetworkObject> objects, NetworkManager nm)
    {
        // Build a map of clientId -> owner object world position for interest culling.
        // We use the NetworkObject's Actor.Transform.Position as the reference point.
        var clientPositions = BuildClientPositionMap(objects, nm);

        foreach (var netObj in objects)
        {
            byte[] dirtyState = netObj.CollectLocalState();

            // Nothing changed since last send — skip
            if (dirtyState.Length == 0 || HasZeroEntries(dirtyState)) continue;

            Vector3 objPos = PositionOf(netObj);

            // Encode StateUpdate packet: [type(1)] [networkId(4)] [state(rest)]
            byte[] packet = EncodeStateUpdate(netObj.NetworkId, dirtyState);

            // Determine which [Replicated] members are OwnerOnly to decide targeting
            bool hasOwnerOnlyData = HasOwnerOnlyMembers(netObj);

            foreach (int clientId in nm.ConnectedClientIds)
            {
                // Interest radius check
                if (clientPositions.TryGetValue(clientId, out Vector3 clientPos))
                {
                    float dist = Vector3.Distance(objPos, clientPos);
                    if (dist > InterestRadius) continue;
                }

                // For OwnerOnly objects, only send to the owner
                if (hasOwnerOnlyData && netObj.OwnerClientId != -1 && clientId != netObj.OwnerClientId)
                {
                    // Send a filtered packet that excludes OwnerOnly members
                    byte[] filtered = FilterOwnerOnlyMembers(netObj, dirtyState, forOwner: false);
                    if (!HasZeroEntries(filtered))
                        nm.SendToClient(clientId, EncodeStateUpdate(netObj.NetworkId, filtered),
                            NetDelivery.Unreliable);
                }
                else
                {
                    nm.SendToClient(clientId, packet, NetDelivery.Unreliable);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Client tick
    // -------------------------------------------------------------------------

    private void TickClient(List<NetworkObject> objects, NetworkManager nm)
    {
        // Clients only send state for objects they own
        foreach (var netObj in objects)
        {
            if (!netObj.IsOwner) continue;

            byte[] dirtyState = netObj.CollectLocalState();
            if (dirtyState.Length == 0 || HasZeroEntries(dirtyState)) continue;

            byte[] packet = EncodeStateUpdate(netObj.NetworkId, dirtyState);
            nm.SendToServer(packet, NetDelivery.Unreliable);
        }
    }

    // -------------------------------------------------------------------------
    // Inbound dispatch — called by NetworkManager when a StateUpdate arrives
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by <see cref="NetworkManager"/> when a StateUpdate packet is
    /// received. Looks up the target NetworkObject by NetworkId and applies
    /// the remote state.
    /// </summary>
    public void HandleIncomingStateUpdate(uint networkId, byte[] stateData)
    {
        NetworkObject? target = null;
        lock (_lock)
        {
            foreach (var obj in _objects)
            {
                if (obj.NetworkId == networkId)
                {
                    target = obj;
                    break;
                }
            }
        }

        target?.ApplyRemoteState(stateData);
    }

    // -------------------------------------------------------------------------
    // Packet encoding helpers
    // -------------------------------------------------------------------------

    private static byte[] EncodeStateUpdate(uint networkId, byte[] stateData)
        => new NetWriter(NetMessage.State).UInt(networkId).Bytes(stateData).ToArray();

    // Returns true when the count prefix in a CollectLocalState payload is 0.
    private static bool HasZeroEntries(byte[] data)
    {
        if (data.Length < 2) return true;
        ushort count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2));
        return count == 0;
    }

    // -------------------------------------------------------------------------
    // Interest-radius helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a clientId -> world position map using each client's owned
    /// NetworkObject's Actor.Transform.Position as the reference point.
    /// Clients with no owned object are omitted (they receive all updates).
    /// </summary>
    /// <summary>
    /// Where an object is, in three dimensions when it has a 3D transform and on the ground plane
    /// when it does not.
    /// </summary>
    /// <remarks>
    /// This read the 2D <c>Transform</c>, which every actor has and a 3D actor never moves, so in a
    /// 3D game every object reported the origin, every distance was zero and nothing was ever
    /// culled. It failed safe -- everything was broadcast -- so it cost bandwidth rather than
    /// correctness, and the interest radius was decorative.
    /// </remarks>
    internal static Vector3 PositionOf(NetworkObject netObj)
        => netObj.Actor.GetComponent<Transform3D>() is { } transform
            ? transform.Position
            : new Vector3(netObj.Actor.Transform.Position, 0f);

    private static Dictionary<int, Vector3> BuildClientPositionMap(
        List<NetworkObject> objects, NetworkManager nm)
    {
        var map = new Dictionary<int, Vector3>();
        foreach (var obj in objects)
        {
            if (obj.OwnerClientId >= 0)
                map[obj.OwnerClientId] = PositionOf(obj);
        }
        return map;
    }

    // -------------------------------------------------------------------------
    // OwnerOnly filtering helpers
    // -------------------------------------------------------------------------

    private static bool HasOwnerOnlyMembers(NetworkObject netObj)
    {
        foreach (var component in netObj.Actor.GetAllComponents())
        {
            if (HasOwnerOnlyOnType(component.GetType())) return true;
        }
        return HasOwnerOnlyOnType(netObj.Actor.GetType());
    }

    private static bool HasOwnerOnlyOnType(Type t)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (var fi in t.GetFields(flags))
        {
            var a = fi.GetCustomAttribute<ReplicatedAttribute>();
            if (a?.Condition == ReplicateCondition.OwnerOnly) return true;
        }
        foreach (var pi in t.GetProperties(flags))
        {
            var a = pi.GetCustomAttribute<ReplicatedAttribute>();
            if (a?.Condition == ReplicateCondition.OwnerOnly) return true;
        }
        return false;
    }

    /// <summary>
    /// Re-reads the raw state payload and strips out entries whose member has
    /// OwnerOnly condition when <paramref name="forOwner"/> is false.
    /// This is a best-effort filter over the serialised name-tagged format.
    /// </summary>
    private byte[] FilterOwnerOnlyMembers(NetworkObject netObj, byte[] data, bool forOwner)
    {
        // Build lookup of member name -> condition
        var conditions = BuildConditionLookup(netObj);

        using var inMs  = new System.IO.MemoryStream(data);
        using var br    = new System.IO.BinaryReader(inMs);
        using var outMs = new System.IO.MemoryStream();
        using var bw    = new System.IO.BinaryWriter(outMs);

        ushort inCount  = br.ReadUInt16();
        long   countPos = outMs.Position;
        bw.Write((ushort)0); // placeholder

        ushort outCount = 0;
        for (int i = 0; i < inCount; i++)
        {
            string name = br.ReadString();
            bool isOwnerOnly = conditions.TryGetValue(name, out var cond)
                               && cond == ReplicateCondition.OwnerOnly;

            if (isOwnerOnly && !forOwner)
            {
                // Skip the value bytes
                SkipSerializedValue(br);
                continue;
            }

            // Copy name + value bytes verbatim
            bw.Write(name);
            CopySerializedValue(br, bw);
            outCount++;
        }

        // Patch count
        long endPos = outMs.Position;
        outMs.Seek(countPos, System.IO.SeekOrigin.Begin);
        bw.Write(outCount);
        outMs.Seek(endPos, System.IO.SeekOrigin.Begin);

        return outMs.ToArray();
    }

    private static Dictionary<string, ReplicateCondition> BuildConditionLookup(NetworkObject netObj)
    {
        var result = new Dictionary<string, ReplicateCondition>(StringComparer.Ordinal);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        void Scan(Type t)
        {
            foreach (var fi in t.GetFields(flags))
            {
                var a = fi.GetCustomAttribute<ReplicatedAttribute>();
                if (a != null) result.TryAdd(fi.Name, a.Condition);
            }
            foreach (var pi in t.GetProperties(flags))
            {
                var a = pi.GetCustomAttribute<ReplicatedAttribute>();
                if (a != null) result.TryAdd(pi.Name, a.Condition);
            }
        }

        Scan(netObj.Actor.GetType());
        foreach (var c in netObj.Actor.GetAllComponents())
            Scan(c.GetType());

        return result;
    }

    // -------------------------------------------------------------------------
    // Low-level byte-copy helpers for the filter path
    // -------------------------------------------------------------------------

    private enum ValueTag : byte
    {
        Bool = 1, Int32 = 2, UInt32 = 3, Float = 4, String = 5,
        Vector2 = 6, Vector3 = 7, Byte = 8, Int64 = 9, Double = 10, Unknown = 255,
    }

    private static void SkipSerializedValue(System.IO.BinaryReader br)
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

    private static void CopySerializedValue(System.IO.BinaryReader br, System.IO.BinaryWriter bw)
    {
        byte tagByte = br.ReadByte();
        bw.Write(tagByte);
        var tag = (ValueTag)tagByte;
        switch (tag)
        {
            case ValueTag.Bool:    bw.Write(br.ReadBoolean()); break;
            case ValueTag.Byte:    bw.Write(br.ReadByte());    break;
            case ValueTag.Int32:   bw.Write(br.ReadInt32());   break;
            case ValueTag.UInt32:  bw.Write(br.ReadUInt32());  break;
            case ValueTag.Int64:   bw.Write(br.ReadInt64());   break;
            case ValueTag.Float:   bw.Write(br.ReadSingle());  break;
            case ValueTag.Double:  bw.Write(br.ReadDouble());  break;
            case ValueTag.String:  bw.Write(br.ReadString());  break;
            case ValueTag.Vector2:
                bw.Write(br.ReadSingle());
                bw.Write(br.ReadSingle());
                break;
            case ValueTag.Vector3:
                bw.Write(br.ReadSingle());
                bw.Write(br.ReadSingle());
                bw.Write(br.ReadSingle());
                break;
            case ValueTag.Unknown:
                int len = br.ReadInt32();
                bw.Write(len);
                if (len > 0) bw.Write(br.ReadBytes(len));
                break;
        }
    }

    // -------------------------------------------------------------------------
    // Member cache (reserved for future optimisation)
    // -------------------------------------------------------------------------

    private sealed class CachedMemberSet
    {
        public List<(MemberInfo Member, ReplicatedAttribute Attr)> Members { get; } = new();
    }
}
