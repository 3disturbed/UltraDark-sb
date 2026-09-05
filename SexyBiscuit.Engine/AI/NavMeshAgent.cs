using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.AI;

/// <summary>
/// Steers an actor along a <see cref="NavMesh"/> path — the component that turns
/// "go there" into per-frame movement.
/// </summary>
/// <remarks>
/// <para>
/// The agent moves its own <see cref="Transform3D"/> by default. Set
/// <see cref="DriveCharacter"/> when the actor is a <see cref="Gameplay.Character"/> so the
/// agent pushes movement intent through the character controller instead, keeping collision
/// and gravity in play.
/// </para>
/// <para>
/// Paths are recomputed on a timer rather than every frame — a full A* per agent per frame is
/// the usual reason navigation shows up in a profile. Lower <see cref="RepathInterval"/> for
/// agents chasing fast targets.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var agent = enemy.AddComponent&lt;NavMeshAgent&gt;();
/// agent.Speed = 3.5f;
/// agent.SetDestination(player.Transform3D.Position);
///
/// if (agent.HasReachedDestination) Attack();
/// </code>
/// </example>
public sealed class NavMeshAgent : Component
{
    /// <summary>Nav mesh to path over. Falls back to <see cref="NavMesh.Active"/>.</summary>
    public NavMesh? NavMesh { get; set; }

    /// <summary>Movement speed in world units per second.</summary>
    public float Speed { get; set; } = 3.5f;

    /// <summary>Turn rate in degrees per second when facing the direction of travel.</summary>
    public float AngularSpeed { get; set; } = 540f;

    /// <summary>Distance from the destination at which the agent stops.</summary>
    public float StoppingDistance { get; set; } = 0.4f;

    /// <summary>Distance at which a path corner counts as reached and the agent advances to the next.</summary>
    public float CornerTolerance { get; set; } = 0.3f;

    /// <summary>Seconds between automatic repaths while a destination is set. Zero disables repathing.</summary>
    public float RepathInterval { get; set; } = 0.5f;

    /// <summary>How far the destination may be off the mesh and still snap onto it.</summary>
    public float SnapDistance { get; set; } = 2f;

    /// <summary>Rotates the actor to face the direction it is moving.</summary>
    public bool FaceMovementDirection { get; set; } = true;

    /// <summary>
    /// Pushes movement through the actor's <see cref="Gameplay.Character"/> rather than
    /// writing the transform directly, so the character controller handles collision.
    /// </summary>
    public bool DriveCharacter { get; set; }

    /// <summary>Suspends movement without discarding the current path.</summary>
    public bool IsStopped { get; set; }

    /// <summary>The current path's corner points. Empty when the agent has no path.</summary>
    public IReadOnlyList<Vector3> Path => _path;

    /// <summary>Index of the corner the agent is currently heading toward.</summary>
    public int CurrentCorner { get; private set; }

    /// <summary>The position the agent is trying to reach, or null when it has none.</summary>
    public Vector3? Destination { get; private set; }

    /// <summary>True once the agent is within <see cref="StoppingDistance"/> of its destination.</summary>
    public bool HasReachedDestination { get; private set; }

    /// <summary>True when the last <see cref="SetDestination"/> could not find a route.</summary>
    public bool PathFailed { get; private set; }

    /// <summary>Straight-line distance remaining along the path corners.</summary>
    public float RemainingDistance
    {
        get
        {
            if (_path.Count == 0) return 0f;

            float total = Vector3.Distance(CurrentPosition, _path[Math.Min(CurrentCorner, _path.Count - 1)]);
            for (int i = CurrentCorner; i < _path.Count - 1; i++)
                total += Vector3.Distance(_path[i], _path[i + 1]);
            return total;
        }
    }

    /// <summary>Raised when the agent arrives at its destination.</summary>
    public SBEvent DestinationReached { get; } = new();

    /// <summary>Raised when a path request fails.</summary>
    public SBEvent PathfindingFailed { get; } = new();

    private readonly List<Vector3> _path = new();
    private Transform3D?           _t3d;
    private Gameplay.Character?    _character;
    private float                  _repathTimer;

    private Transform3D T3D => _t3d ??= Actor.GetComponent<Transform3D>() ?? Actor.AddComponent<Transform3D>();
    private Vector3 CurrentPosition => T3D.Position;

    public override void Start()
    {
        _character = Actor as Gameplay.Character;
    }

    // -------------------------------------------------------------------------
    // Destination
    // -------------------------------------------------------------------------

    /// <summary>
    /// Requests a path to <paramref name="target"/>. Returns false when no route exists,
    /// leaving the agent's previous path intact.
    /// </summary>
    public bool SetDestination(Vector3 target)
    {
        var mesh = NavMesh ?? AI.NavMesh.Active;
        if (mesh == null)
        {
            PathFailed = true;
            PathfindingFailed.Broadcast();
            return false;
        }

        var candidate = new List<Vector3>();
        if (!mesh.FindPath(CurrentPosition, target, candidate, SnapDistance))
        {
            PathFailed = true;
            PathfindingFailed.Broadcast();
            return false;
        }

        _path.Clear();
        _path.AddRange(candidate);

        Destination           = target;
        CurrentCorner         = _path.Count > 1 ? 1 : 0;
        HasReachedDestination = false;
        PathFailed            = false;
        _repathTimer          = 0f;
        return true;
    }

    /// <summary>Clears the path and stops the agent where it stands.</summary>
    public void ResetPath()
    {
        _path.Clear();
        Destination           = null;
        CurrentCorner         = 0;
        HasReachedDestination = false;
    }

    /// <summary>Replaces the path with one computed elsewhere. Corners must already be on the mesh.</summary>
    public void SetPath(IEnumerable<Vector3> corners)
    {
        _path.Clear();
        _path.AddRange(corners);
        Destination           = _path.Count > 0 ? _path[^1] : null;
        CurrentCorner         = _path.Count > 1 ? 1 : 0;
        HasReachedDestination = false;
        PathFailed            = false;
    }

    // -------------------------------------------------------------------------
    // Steering
    // -------------------------------------------------------------------------

    public override void Update(float dt)
    {
        if (IsStopped || _path.Count == 0 || Destination == null) return;

        TickRepath(dt);

        var position = CurrentPosition;

        // Arrival is measured against the final destination, not the next corner,
        // so StoppingDistance means what it says even on a long path.
        if (HorizontalDistance(position, Destination.Value) <= StoppingDistance)
        {
            if (!HasReachedDestination)
            {
                HasReachedDestination = true;
                DestinationReached.Broadcast();
            }
            return;
        }

        AdvanceCorner(position);
        if (CurrentCorner >= _path.Count) return;

        var toCorner = _path[CurrentCorner] - position;
        toCorner.Y = 0f;

        var direction = SBMath.SafeNormalize(toCorner);
        if (direction == Vector3.Zero) return;

        if (DriveCharacter && _character != null)
            _character.AddMovementInput(direction);
        else
            T3D.Position = position + direction * Speed * dt;

        if (FaceMovementDirection)
        {
            float targetYaw = MathF.Atan2(direction.X, direction.Z) * SBMath.Rad2Deg;
            float yaw = SBMath.MoveTowardsAngle(T3D.EulerAngles.Y, targetYaw, AngularSpeed * dt);
            T3D.EulerAngles = T3D.EulerAngles with { Y = yaw };
        }
    }

    private void TickRepath(float dt)
    {
        if (RepathInterval <= 0f || Destination == null) return;

        _repathTimer += dt;
        if (_repathTimer < RepathInterval) return;

        _repathTimer = 0f;
        SetDestination(Destination.Value);
    }

    private void AdvanceCorner(Vector3 position)
    {
        while (CurrentCorner < _path.Count &&
               HorizontalDistance(position, _path[CurrentCorner]) <= CornerTolerance)
        {
            CurrentCorner++;
        }
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.Y = b.Y = 0f;
        return Vector3.Distance(a, b);
    }
}
