namespace SexyBiscuit.Engine.Networking;

// ---------------------------------------------------------------------------
// Attributes.cs
// Network attribute definitions for the SexyBiscuit Engine networking layer.
// ---------------------------------------------------------------------------

/// <summary>
/// Mark a field or property for automatic state replication across the network.
/// The ReplicationSystem scans for this attribute each tick to detect dirty values
/// and transmit them to relevant peers.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class ReplicatedAttribute : Attribute
{
    /// <summary>
    /// Determines under what conditions this member's value is sent.
    /// </summary>
    public ReplicateCondition Condition { get; }

    /// <summary>
    /// Higher priority members are written first into the packet when bandwidth
    /// is constrained and the ReplicationSystem must shed lower-priority data.
    /// </summary>
    public int Priority { get; }

    /// <param name="condition">
    /// When to replicate this member. Defaults to <see cref="ReplicateCondition.Always"/>.
    /// </param>
    /// <param name="priority">
    /// Replication priority. Higher values are transmitted first. Default 0.
    /// </param>
    public ReplicatedAttribute(ReplicateCondition condition = ReplicateCondition.Always, int priority = 0)
    {
        Condition = condition;
        Priority  = priority;
    }
}

/// <summary>
/// Controls when a <see cref="ReplicatedAttribute"/> member is included in outgoing packets.
/// </summary>
public enum ReplicateCondition
{
    /// <summary>Replicate every dirty tick to all observers within interest radius.</summary>
    Always,

    /// <summary>
    /// Only the owning client receives this value from the server;
    /// only the owning client sends this value to the server.
    /// Useful for authoritative health, inventory, etc.
    /// </summary>
    OwnerOnly,

    /// <summary>
    /// Sent exactly once when the object first spawns on a client.
    /// Subsequent changes are not transmitted automatically.
    /// </summary>
    InitialOnly,
}

// ---------------------------------------------------------------------------

/// <summary>
/// Decorate a method to indicate it should execute on the server when invoked
/// from a client. The client calls the method locally via
/// <see cref="RpcSystem.CallServerRpc"/>; the RpcSystem serialises the
/// arguments and sends them to the server for authoritative execution.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ServerRpcAttribute : Attribute
{
    /// <summary>
    /// When <c>true</c> (default), only the owner of the NetworkObject may call
    /// this RPC. Non-owners are silently rejected by the server.
    /// </summary>
    public bool RequireOwnership { get; }

    /// <param name="requireOwnership">
    /// Enforce ownership check on the server before executing. Default <c>true</c>.
    /// </param>
    public ServerRpcAttribute(bool requireOwnership = true)
    {
        RequireOwnership = requireOwnership;
    }
}

// ---------------------------------------------------------------------------

/// <summary>
/// Decorate a method to indicate it should execute on one or more clients when
/// invoked from the server. The server calls the method locally via
/// <see cref="RpcSystem.CallClientRpc"/>; the RpcSystem serialises the
/// arguments and dispatches them to the appropriate targets.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ClientRpcAttribute : Attribute
{
    /// <summary>
    /// Which clients receive this RPC dispatch.
    /// </summary>
    public RpcTarget Target { get; }

    /// <param name="target">
    /// Audience for this RPC. Defaults to <see cref="RpcTarget.All"/>.
    /// </param>
    public ClientRpcAttribute(RpcTarget target = RpcTarget.All)
    {
        Target = target;
    }
}

/// <summary>
/// Specifies which connected clients are targeted by a <see cref="ClientRpcAttribute"/> method.
/// </summary>
public enum RpcTarget
{
    /// <summary>All connected clients receive the call.</summary>
    All,

    /// <summary>Only the client that owns the NetworkObject receives the call.</summary>
    Owner,

    /// <summary>Every client except the owner receives the call.</summary>
    Others,
}
