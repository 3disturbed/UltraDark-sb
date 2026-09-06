using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Networking;

namespace Cookies.RemotePlayers;

/// <summary>
/// Replicates where an actor is, and smooths it out on the machines that do not own it.
/// </summary>
/// <remarks>
/// Updates arrive at the replication system's send rate, which is twenty a second. Drawn straight,
/// a pawn steps twenty times a second; drawn a fixed delay behind the newest update and
/// interpolated towards it, it moves.
/// </remarks>
public sealed class NetTransform : Component
{
    /// <summary>The replicated position. Written by the owner, read by everybody else.</summary>
    [Replicated]
    public Vector3 NetPosition { get; set; }

    /// <summary>The replicated orientation, as pitch, yaw and roll in degrees.</summary>
    [Replicated]
    public Vector3 NetEuler { get; set; }

    /// <summary>How far behind the newest update a non-owner is drawn, in seconds.</summary>
    public float InterpolationDelay { get; set; } = 0.1f;

    /// <summary>Beyond this distance the actor is moved rather than eased, so it cannot lag forever.</summary>
    public float SnapDistance { get; set; } = 8f;

    /// <summary>Whether orientation is replicated as well as position.</summary>
    public bool SyncRotation { get; set; } = true;

    private Transform3D? _transform;
    private Vector3      _from, _to;
    private Vector3      _fromEuler, _toEuler;
    private float        _elapsed, _span;

    public override void Start()
    {
        _transform = Actor.GetComponent<Transform3D>();
        if (_transform == null) return;

        NetPosition = _to = _from = _transform.Position;
        NetEuler    = _toEuler = _fromEuler = _transform.EulerAngles;

        if (NetSession.Active is { } session) InterpolationDelay = session.InterpolationDelay;
    }

    public override void Update(float dt)
    {
        if (_transform == null) return;

        if (IsOwner)
        {
            // The owner is the truth: publish, do not interpolate.
            NetPosition = _transform.Position;
            if (SyncRotation) NetEuler = _transform.EulerAngles;
            return;
        }

        // A new value arrived: start easing towards it from wherever we are now.
        if (NetPosition != _to)
        {
            _from      = _transform.Position;
            _fromEuler = _transform.EulerAngles;
            _to        = NetPosition;
            _toEuler   = NetEuler;
            _span      = MathF.Max(InterpolationDelay, 0.016f);
            _elapsed   = 0f;

            if (Vector3.Distance(_from, _to) > SnapDistance)
            {
                _transform.Position = _to;
                if (SyncRotation) _transform.EulerAngles = _toEuler;
                _from      = _to;
                _fromEuler = _toEuler;
                return;
            }
        }

        if (_span <= 0f) return;

        _elapsed += dt;
        float t = MathF.Min(1f, _elapsed / _span);

        _transform.Position = Vector3.Lerp(_from, _to, t);
        if (SyncRotation) _transform.EulerAngles = Vector3.Lerp(_fromEuler, _toEuler, t);
    }

    /// <summary>True when this machine is the one that moves the actor.</summary>
    private bool IsOwner
    {
        get
        {
            if (Actor.GetComponent<NetworkObject>() is not { } netObj) return true;   // not networked at all
            if (NetworkManager.Instance == null) return true;                          // offline

            return NetSession.Active?.Authority == NetAuthority.ServerAuthoritative
                ? NetworkManager.Instance.IsServer
                : netObj.IsOwner;
        }
    }
}
