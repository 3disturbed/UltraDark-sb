using System.Reflection;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// RpcSystem.cs
// Handles [ServerRpc] and [ClientRpc] dispatch.
//
// DESIGN NOTE — IL Weaving vs. Manual Shims
// ------------------------------------------
// In a production engine, RPC interception would be implemented via a
// compile-time source generator (Roslyn ISourceGenerator) or a post-build
// IL rewriting pass (e.g. Mono.Cecil / Fody) that wraps every method marked
// [ServerRpc] / [ClientRpc] to automatically call CallServerRpc /
// CallClientRpc instead of executing the body locally when the conditions
// (server / ownership) are not met.
//
// Because SexyBiscuit.Engine does not yet include that tooling, the manual
// shim pattern is used:
//
//   // Instead of calling myActor.Fire() directly, write:
//   RpcSystem.CallServerRpc(myActor.GetComponent<NetworkObject>()!, nameof(myActor.Fire), arg1, arg2);
//
// The engine team should treat every [ServerRpc] / [ClientRpc]-decorated
// method as if its body begins with an implicit intercept guard; the manual
// call to CallServerRpc / CallClientRpc is that guard until weaving is
// introduced.
// ---------------------------------------------------------------------------

/// <summary>
/// Manages registration, serialisation, dispatch and invocation of network
/// Remote Procedure Calls (RPCs) marked with <see cref="ServerRpcAttribute"/>
/// or <see cref="ClientRpcAttribute"/>.
/// </summary>
public class RpcSystem
{
    // -------------------------------------------------------------------------
    // Registration data
    // -------------------------------------------------------------------------

    private sealed class RpcEntry
    {
        public Actor       Actor  { get; }
        public NetworkObject NetObj { get; }

        // Method name -> (MethodInfo, attribute) for each RPC type
        public Dictionary<string, (MethodInfo Method, ServerRpcAttribute Attr)> ServerRpcs { get; }
            = new(StringComparer.Ordinal);

        public Dictionary<string, (MethodInfo Method, ClientRpcAttribute Attr)> ClientRpcs { get; }
            = new(StringComparer.Ordinal);

        public RpcEntry(Actor actor, NetworkObject netObj)
        {
            Actor  = actor;
            NetObj = netObj;
        }
    }

    // Keyed by NetworkId
    private readonly Dictionary<uint, RpcEntry> _entries = new();
    private readonly object _lock = new();

    // -------------------------------------------------------------------------
    // Registration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Scans <paramref name="actor"/> and all its Components for methods
    /// decorated with <see cref="ServerRpcAttribute"/> or
    /// <see cref="ClientRpcAttribute"/> and registers them for dispatch.
    /// </summary>
    public void Register(Actor actor, NetworkObject netObj)
    {
        var entry = new RpcEntry(actor, netObj);

        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // Scan Actor class itself and every Component attached to it
        var targets = new List<object> { actor };
        targets.AddRange(actor.GetAllComponents().Cast<object>());

        foreach (var target in targets)
        {
            foreach (var method in target.GetType().GetMethods(flags))
            {
                var serverAttr = method.GetCustomAttribute<ServerRpcAttribute>();
                if (serverAttr != null)
                    entry.ServerRpcs.TryAdd(method.Name, (method, serverAttr));

                var clientAttr = method.GetCustomAttribute<ClientRpcAttribute>();
                if (clientAttr != null)
                    entry.ClientRpcs.TryAdd(method.Name, (method, clientAttr));
            }
        }

        lock (_lock)
        {
            _entries[netObj.NetworkId] = entry;
        }
    }

    /// <summary>Removes all RPC registrations for the given actor.</summary>
    public void Unregister(Actor actor)
    {
        var netObj = actor.GetComponent<NetworkObject>();
        if (netObj == null) return;

        lock (_lock)
        {
            _entries.Remove(netObj.NetworkId);
        }
    }

    // -------------------------------------------------------------------------
    // Server-side: receive an RPC request from a client
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by <see cref="NetworkManager"/> on the server when an Rpc packet
    /// arrives from a client.
    /// </summary>
    /// <param name="senderId">The client ID that sent the request.</param>
    /// <param name="networkId">Target NetworkObject NetworkId.</param>
    /// <param name="methodName">Name of the [ServerRpc]-decorated method.</param>
    /// <param name="args">Serialised argument payload.</param>
    public void HandleIncomingServerRpc(int senderId, uint networkId, string methodName, byte[] args)
    {
        RpcEntry? entry;
        lock (_lock)
        {
            _entries.TryGetValue(networkId, out entry);
        }

        if (entry == null)
        {
            Console.Error.WriteLine(
                $"[RpcSystem] HandleIncomingServerRpc: no entry for NetworkId={networkId}");
            return;
        }

        if (!entry.ServerRpcs.TryGetValue(methodName, out var rpc))
        {
            Console.Error.WriteLine(
                $"[RpcSystem] HandleIncomingServerRpc: unknown method '{methodName}' on NetworkId={networkId}");
            return;
        }

        // Ownership check
        if (rpc.Attr.RequireOwnership && entry.NetObj.OwnerClientId != senderId)
        {
            Console.Error.WriteLine(
                $"[RpcSystem] HandleIncomingServerRpc: client {senderId} does not own NetworkId={networkId} " +
                $"(owner={entry.NetObj.OwnerClientId}) — rejecting '{methodName}'.");
            return;
        }

        // Deserialise arguments and invoke
        object?[] parameters = DeserialiseArgs(args, rpc.Method.GetParameters());
        InvokeOnActorOrComponent(entry, rpc.Method, parameters);
    }

    // -------------------------------------------------------------------------
    // Client-side: receive an RPC dispatch from the server
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by <see cref="NetworkManager"/> on the client when an Rpc packet
    /// arrives from the server.
    /// </summary>
    /// <param name="networkId">Target NetworkObject NetworkId.</param>
    /// <param name="methodName">Name of the [ClientRpc]-decorated method.</param>
    /// <param name="args">Serialised argument payload.</param>
    /// <param name="targetClientId">
    /// When non-null the RPC is targeted; the local client should only execute
    /// it if <c>targetClientId</c> matches <see cref="NetworkManager.LocalClientId"/>.
    /// </param>
    public void HandleIncomingClientRpc(uint networkId, string methodName, byte[] args,
                                        int? targetClientId = null)
    {
        var nm = NetworkManager.Instance;

        // If targeted, filter to this client only
        if (targetClientId.HasValue && nm != null && nm.LocalClientId != targetClientId.Value)
            return;

        RpcEntry? entry;
        lock (_lock)
        {
            _entries.TryGetValue(networkId, out entry);
        }

        if (entry == null)
        {
            Console.Error.WriteLine(
                $"[RpcSystem] HandleIncomingClientRpc: no entry for NetworkId={networkId}");
            return;
        }

        if (!entry.ClientRpcs.TryGetValue(methodName, out var rpc))
        {
            Console.Error.WriteLine(
                $"[RpcSystem] HandleIncomingClientRpc: unknown method '{methodName}' on NetworkId={networkId}");
            return;
        }

        object?[] parameters = DeserialiseArgs(args, rpc.Method.GetParameters());
        InvokeOnActorOrComponent(entry, rpc.Method, parameters);
    }

    // -------------------------------------------------------------------------
    // Manual shim helpers — see file-level design note
    // -------------------------------------------------------------------------

    /// <summary>
    /// Manual shim to send a [ServerRpc] call from client to server.
    /// The server will validate ownership (if required) then invoke the method.
    /// </summary>
    /// <remarks>
    /// This replaces the intercepted direct method call until IL weaving /
    /// source generation is introduced. Call this instead of invoking the
    /// [ServerRpc] method directly on the client.
    /// </remarks>
    public static void CallServerRpc(NetworkObject obj, string methodName, params object[] args)
    {
        var nm = NetworkManager.Instance;
        if (nm == null || !nm.IsRunning)
        {
            Console.Error.WriteLine("[RpcSystem] CallServerRpc: NetworkManager not running.");
            return;
        }

        if (nm.IsServer)
        {
            // Already on the server — invoke directly
            nm.Rpc.InvokeDirectly(obj.NetworkId, methodName, args, isServer: true);
            return;
        }

        byte[] argBytes  = SerialiseArgs(args);
        byte[] packet    = EncodeRpcPacket(obj.NetworkId, methodName, argBytes,
                                           targetClientId: null, isServerRpc: true);
        nm.SendToServer(packet);
    }

    /// <summary>
    /// Manual shim to dispatch a [ClientRpc] call from the server to clients.
    /// </summary>
    /// <remarks>
    /// This replaces the intercepted direct method call until IL weaving /
    /// source generation is introduced. Call this instead of invoking the
    /// [ClientRpc] method directly on the server.
    /// </remarks>
    public static void CallClientRpc(NetworkObject obj, string methodName,
                                     RpcTarget target, params object[] args)
    {
        var nm = NetworkManager.Instance;
        if (nm == null || !nm.IsRunning)
        {
            Console.Error.WriteLine("[RpcSystem] CallClientRpc: NetworkManager not running.");
            return;
        }

        if (!nm.IsServer)
        {
            Console.Error.WriteLine("[RpcSystem] CallClientRpc: must be called from the server.");
            return;
        }

        byte[] argBytes = SerialiseArgs(args);

        switch (target)
        {
            case RpcTarget.All:
                {
                    byte[] packet = EncodeRpcPacket(obj.NetworkId, methodName, argBytes,
                                                    targetClientId: null, isServerRpc: false);
                    nm.SendToAll(packet);

                    // Execute locally on server too
                    nm.Rpc.InvokeDirectly(obj.NetworkId, methodName, args, isServer: false);
                    break;
                }

            case RpcTarget.Owner:
                {
                    if (obj.OwnerClientId >= 0)
                    {
                        byte[] packet = EncodeRpcPacket(obj.NetworkId, methodName, argBytes,
                                                        targetClientId: obj.OwnerClientId,
                                                        isServerRpc: false);
                        nm.SendToClient(obj.OwnerClientId, packet);
                    }
                    else
                    {
                        // Server-owned — execute locally
                        nm.Rpc.InvokeDirectly(obj.NetworkId, methodName, args, isServer: false);
                    }
                    break;
                }

            case RpcTarget.Others:
                {
                    byte[] packet = EncodeRpcPacket(obj.NetworkId, methodName, argBytes,
                                                    targetClientId: null, isServerRpc: false);
                    nm.SendToAll(packet, excludeClientId: obj.OwnerClientId);
                    break;
                }
        }
    }

    // -------------------------------------------------------------------------
    // Internal direct invocation (server calling on itself)
    // -------------------------------------------------------------------------

    internal void InvokeDirectly(uint networkId, string methodName, object[] args, bool isServer)
    {
        RpcEntry? entry;
        lock (_lock)
        {
            _entries.TryGetValue(networkId, out entry);
        }

        if (entry == null) return;

        MethodInfo? method = null;
        if (isServer && entry.ServerRpcs.TryGetValue(methodName, out var sRpc))
            method = sRpc.Method;
        else if (!isServer && entry.ClientRpcs.TryGetValue(methodName, out var cRpc))
            method = cRpc.Method;

        if (method == null) return;

        InvokeOnActorOrComponent(entry, method, args.Cast<object?>().ToArray());
    }

    // -------------------------------------------------------------------------
    // Invocation helper — finds the correct host object (Actor or Component)
    // -------------------------------------------------------------------------

    private static void InvokeOnActorOrComponent(RpcEntry entry, MethodInfo method, object?[] parameters)
    {
        // The method may be declared on the Actor subclass or on one of its components.
        // Walk both to find the right instance.
        object? host = null;

        if (method.DeclaringType?.IsAssignableFrom(entry.Actor.GetType()) == true)
        {
            host = entry.Actor;
        }
        else
        {
            foreach (var component in entry.Actor.GetAllComponents())
            {
                if (method.DeclaringType?.IsAssignableFrom(component.GetType()) == true)
                {
                    host = component;
                    break;
                }
            }
        }

        if (host == null)
        {
            Console.Error.WriteLine(
                $"[RpcSystem] InvokeOnActorOrComponent: could not find host for method '{method.Name}'.");
            return;
        }

        try
        {
            method.Invoke(host, parameters);
        }
        catch (TargetInvocationException ex)
        {
            Console.Error.WriteLine(
                $"[RpcSystem] Exception in RPC '{method.Name}': {ex.InnerException}");
        }
    }

    // -------------------------------------------------------------------------
    // Packet encoding / decoding
    // -------------------------------------------------------------------------

    // The frame, as NetMessage.Rpc defines it and the browser engine reads it:
    // [id u8][networkId u32][isServerRpc u8][targetClientId i32][method str][args bytes]
    //
    // The target is -1 rather than a present/absent pair: a fixed layout is one less thing for
    // the two implementations to disagree about, and four bytes on a rare frame is nothing.

    internal static byte[] EncodeRpcPacket(uint networkId, string methodName, byte[] argBytes,
                                           int? targetClientId, bool isServerRpc)
        => new NetWriter(NetMessage.Rpc)
            .UInt(networkId)
            .Bool(isServerRpc)
            .Int(targetClientId ?? -1)
            .String(methodName)
            .Bytes(argBytes)
            .ToArray();

    /// <summary>
    /// Decodes an Rpc packet body (after the PacketType byte has been consumed).
    /// </summary>
    internal static void DecodeRpcPacket(ref NetReader reader,
                                         out uint networkId, out bool isServerRpc,
                                         out int? targetClientId, out string methodName,
                                         out byte[] argBytes)
    {
        networkId   = reader.UInt();
        isServerRpc = reader.Bool();
        int target  = reader.Int();
        targetClientId = target < 0 ? null : target;
        methodName  = reader.String();
        argBytes    = reader.Bytes();
    }

    // -------------------------------------------------------------------------
    // Argument serialisation
    // -------------------------------------------------------------------------
    // Supported argument types: bool, int, float, string, Vector2, Vector3.
    // Each argument is prefixed with a 1-byte type tag so the deserialiser can
    // reconstruct the correct CLR type without schema information.

    private enum ArgTag : byte
    {
        Null    = 0,
        Bool    = 1,
        Int32   = 2,
        Float   = 3,
        String  = 4,
        Vector2 = 5,
        Vector3 = 6,
        UInt32  = 7,
        Int64   = 8,
        Double  = 9,
        Byte    = 10,
    }

    internal static byte[] SerialiseArgs(object[] args)
    {
        if (args == null || args.Length == 0) return Array.Empty<byte>();

        var w = new NetWriter();
        w.Byte((byte)args.Length);

        foreach (var arg in args)
        {
            switch (arg)
            {
                case null:        w.Byte((byte)ArgTag.Null); break;
                case bool b:      w.Byte((byte)ArgTag.Bool).Bool(b); break;
                case byte by:     w.Byte((byte)ArgTag.Byte).Byte(by); break;
                case int i:       w.Byte((byte)ArgTag.Int32).Int(i); break;
                case uint u:      w.Byte((byte)ArgTag.UInt32).UInt(u); break;
                // Int64 rides as a double, as it does in a replicated member: JavaScript has
                // no 64-bit integer in a Number, and a value that does not survive the round
                // trip is worse than one documented to carry 53 bits.
                case long l:      w.Byte((byte)ArgTag.Int64).Double(l); break;
                case float f:     w.Byte((byte)ArgTag.Float).Float(f); break;
                case double d:    w.Byte((byte)ArgTag.Double).Double(d); break;
                case string s:    w.Byte((byte)ArgTag.String).String(s); break;
                case Vector2 v2:  w.Byte((byte)ArgTag.Vector2).Float(v2.X).Float(v2.Y); break;
                case Microsoft.Xna.Framework.Vector3 v3:
                    w.Byte((byte)ArgTag.Vector3).Float(v3.X).Float(v3.Y).Float(v3.Z);
                    break;
                default:
                    Console.Error.WriteLine(
                        $"[RpcSystem] SerialiseArgs: unsupported type '{arg.GetType().Name}' — encoding as null.");
                    w.Byte((byte)ArgTag.Null);
                    break;
            }
        }

        return w.ToArray();
    }

    internal static object?[] DeserialiseArgs(byte[] data, ParameterInfo[] parameters)
    {
        if (data == null || data.Length == 0)
            return Array.Empty<object?>();

        var reader = new NetReader(data);
        int count  = reader.Byte();
        var result = new object?[count];

        for (int i = 0; i < count; i++)
        {
            var tag = (ArgTag)reader.Byte();
            result[i] = tag switch
            {
                ArgTag.Null    => null,
                ArgTag.Bool    => (object)reader.Bool(),
                ArgTag.Byte    => reader.Byte(),
                ArgTag.Int32   => reader.Int(),
                ArgTag.UInt32  => reader.UInt(),
                ArgTag.Int64   => (long)reader.Double(),
                ArgTag.Float   => reader.Float(),
                ArgTag.Double  => reader.Double(),
                ArgTag.String  => reader.String(),
                ArgTag.Vector2 => new Vector2(reader.Float(), reader.Float()),
                ArgTag.Vector3 => new Microsoft.Xna.Framework.Vector3(
                                      reader.Float(), reader.Float(), reader.Float()),
                _              => null,
            };

            // Attempt coercion to the declared parameter type where possible
            if (result[i] != null && i < parameters.Length)
            {
                var targetType = parameters[i].ParameterType;
                try
                {
                    if (!targetType.IsAssignableFrom(result[i]!.GetType()))
                        result[i] = Convert.ChangeType(result[i], targetType);
                }
                catch
                {
                    // Leave as-is and let the method invocation surface any type errors
                }
            }
        }

        return result;
    }
}
