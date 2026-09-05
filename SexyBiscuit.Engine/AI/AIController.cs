using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Gameplay;

namespace SexyBiscuit.Engine.AI;

/// <summary>
/// Drives a <see cref="Pawn"/> from a <see cref="BehaviorTree"/> instead of player input.
/// Modelled on Unreal's <c>AAIController</c>.
/// </summary>
/// <remarks>
/// The controller owns the blackboard and the nav agent, and keeps them wired to whichever
/// pawn it currently possesses — so a respawned body picks up the same brain without any
/// re-plumbing at the call site.
/// </remarks>
/// <example>
/// <code>
/// var ai = scene.AddActor(new AIController());
/// ai.BehaviorTree = new BehaviorTree(
///     new Selector(
///         new Sequence(
///             new ConditionNode(c =&gt; c.Controller!.CanSee(player), "CanSeePlayer"),
///             new ActionNode(c =&gt; c.Blackboard.Set("Target", player), "RememberTarget"),
///             new MoveToNode("Target", 2f)),
///         new WaitNode(1f)),
///     controller: ai);
/// ai.Possess(enemyPawn);
/// </code>
/// </example>
public class AIController : Controller
{
    /// <summary>State this controller's tree reasons over.</summary>
    public Blackboard Blackboard { get; } = new();

    /// <summary>The tree ticked each frame. Null means the controller does nothing.</summary>
    public BehaviorTree? BehaviorTree { get; set; }

    /// <summary>The nav agent on the possessed pawn, if it has one.</summary>
    public NavMeshAgent? NavAgent { get; private set; }

    /// <summary>Suspends tree evaluation without unpossessing the pawn.</summary>
    public bool BrainEnabled { get; set; } = true;

    /// <summary>How far this AI can see, in world units.</summary>
    public float SightRadius { get; set; } = 20f;

    /// <summary>Total field of view in degrees, centred on the pawn's forward direction.</summary>
    public float FieldOfView { get; set; } = 110f;

    /// <summary>
    /// Requires an unobstructed line of sight for <see cref="CanSee"/>. Needs a
    /// <see cref="Physics.PhysicsSystem3D"/> in the scene; without one, only range and
    /// angle are checked.
    /// </summary>
    public bool RequireLineOfSight { get; set; } = true;

    /// <summary>Eye height above the pawn origin used for line-of-sight tests.</summary>
    public float EyeHeight { get; set; } = 1.6f;

    public AIController() : base("AIController") { }

    protected override void OnPossess(Pawn pawn)
    {
        NavAgent = pawn.GetComponent<NavMeshAgent>();
        if (NavAgent != null) NavAgent.DriveCharacter = pawn is Character;

        Blackboard.Set("SelfActor", (Actor)pawn);
    }

    protected override void OnUnPossess(Pawn pawn)
    {
        NavAgent?.ResetPath();
        NavAgent = null;
        Blackboard.Clear("SelfActor");
    }

    protected override void Update(float dt)
    {
        if (!BrainEnabled || BehaviorTree == null || ControlledPawn == null) return;
        BehaviorTree.Tick(dt);
    }

    // -------------------------------------------------------------------------
    // Perception
    // -------------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="target"/> is within <see cref="SightRadius"/>, inside the
    /// field of view, and — when <see cref="RequireLineOfSight"/> is on — not behind geometry.
    /// </summary>
    public bool CanSee(Actor? target)
    {
        if (target == null || !target.IsActive) return false;

        var pawnT   = ControlledPawn?.GetComponent<Transform3D>();
        var targetT = target.GetComponent<Transform3D>();
        if (pawnT == null || targetT == null) return false;

        var eye   = pawnT.Position + Vector3.Up * EyeHeight;
        var to    = targetT.Position - eye;
        float distance = to.Length();

        if (distance > SightRadius || distance < SBMath.Epsilon) return false;

        var direction = to / distance;
        float angle = MathF.Acos(MathHelper.Clamp(
            Vector3.Dot(pawnT.Forward, direction), -1f, 1f)) * SBMath.Rad2Deg;

        if (angle > FieldOfView * 0.5f) return false;
        if (!RequireLineOfSight) return true;

        // A hit closer than the target means something is in the way.
        if (!Physics.PhysicsSystem3D.Instance.Raycast(eye, direction, distance, out var hit))
            return true;

        return ReferenceEquals(hit.Actor, target);
    }

    /// <summary>Sends the possessed pawn's nav agent to a world position.</summary>
    public bool MoveTo(Vector3 destination) => NavAgent?.SetDestination(destination) ?? false;

    /// <summary>Sends the possessed pawn's nav agent to another actor's position.</summary>
    public bool MoveTo(Actor target)
    {
        var t = target.GetComponent<Transform3D>();
        return t != null && MoveTo(t.Position);
    }

    /// <summary>Stops the pawn where it stands and discards its path.</summary>
    public void StopMovement() => NavAgent?.ResetPath();
}

/// <summary>
/// Behaviour-tree leaf that walks the pawn to a blackboard position and succeeds on arrival.
/// </summary>
/// <remarks>
/// The destination key may hold either a <see cref="Vector3"/> or an <see cref="Actor"/>, so
/// the same node works for a fixed waypoint and for chasing a moving target.
/// </remarks>
public sealed class MoveToNode : BehaviorNode
{
    /// <summary>Blackboard key holding the destination.</summary>
    public string DestinationKey { get; }

    /// <summary>Distance at which the node reports Success.</summary>
    public float AcceptanceRadius { get; set; }

    /// <summary>Seconds before the node gives up and reports Failure. Zero means never.</summary>
    public float Timeout { get; set; } = 15f;

    private float _elapsed;

    public MoveToNode(string destinationKey, float acceptanceRadius = 1f)
    {
        DestinationKey   = destinationKey;
        AcceptanceRadius = acceptanceRadius;
        Name             = $"MoveTo({destinationKey})";
    }

    protected override void OnEnter(BehaviorContext context) => _elapsed = 0f;

    protected override NodeStatus OnTick(BehaviorContext context)
    {
        var controller = context.Controller;
        var agent      = controller?.NavAgent;
        if (controller == null || agent == null) return NodeStatus.Failure;

        if (!context.Blackboard.TryGetPosition(DestinationKey, out var destination))
            return NodeStatus.Failure;

        _elapsed += context.DeltaTime;
        if (Timeout > 0f && _elapsed > Timeout) return NodeStatus.Failure;

        var pawnT = controller.ControlledPawn?.GetComponent<Transform3D>();
        if (pawnT == null) return NodeStatus.Failure;

        if (Vector3.Distance(pawnT.Position, destination) <= AcceptanceRadius)
        {
            agent.ResetPath();
            return NodeStatus.Success;
        }

        // Re-issue only when the goal actually moved; the agent handles its own repath timer.
        if (agent.Destination == null ||
            Vector3.DistanceSquared(agent.Destination.Value, destination) > AcceptanceRadius * AcceptanceRadius)
        {
            if (!agent.SetDestination(destination)) return NodeStatus.Failure;
        }

        return agent.PathFailed ? NodeStatus.Failure : NodeStatus.Running;
    }

    protected override void OnExit(BehaviorContext context, NodeStatus status)
    {
        if (status == NodeStatus.Failure) context.Controller?.StopMovement();
    }
}
