using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// 3D transform component — position (Vector3), rotation (Quaternion), scale (Vector3),
/// with full parent/child hierarchy and world/local space helpers.
/// </summary>
public sealed class Transform3D : Component
{
    // -------------------------------------------------------------------------
    // Local space
    // -------------------------------------------------------------------------
    private Vector3    _localPosition = Vector3.Zero;
    private Quaternion _localRotation = Quaternion.Identity;
    private Vector3    _localScale    = Vector3.One;

    public Vector3 LocalPosition
    {
        get => _localPosition;
        set { _localPosition = value; MarkDirty(); }
    }

    public Quaternion LocalRotation
    {
        get => _localRotation;
        set { _localRotation = value; MarkDirty(); }
    }

    public Vector3 LocalScale
    {
        get => _localScale;
        set { _localScale = value; MarkDirty(); }
    }

    public Vector3 LocalEulerAngles
    {
        get => QuaternionToEuler(_localRotation);
        set { _localRotation = EulerToQuaternion(value); MarkDirty(); }
    }

    // -------------------------------------------------------------------------
    // World space (lazy)
    // -------------------------------------------------------------------------
    private Vector3    _worldPosition;
    private Quaternion _worldRotation;
    private Vector3    _worldScale;
    private bool       _dirty = true;

    public Vector3 Position
    {
        get { Recalculate(); return _worldPosition; }
        set
        {
            // The world point must be expressed in the PARENT's space to become a local
            // offset. Inverting through this transform would be circular — it would use
            // the very position being assigned.
            _localPosition = _parent == null ? value : _parent.InverseTransformPoint(value);
            MarkDirty();
        }
    }

    public Quaternion Rotation
    {
        get { Recalculate(); return _worldRotation; }
        set
        {
            _localRotation = _parent == null
                ? value
                : Quaternion.Inverse(_parent.Rotation) * value;
            MarkDirty();
        }
    }

    public Vector3 Scale
    {
        get { Recalculate(); return _worldScale; }
        set
        {
            if (_parent == null)
                _localScale = value;
            else
            {
                var ps = _parent.Scale;
                _localScale = ps.X != 0 && ps.Y != 0 && ps.Z != 0
                    ? new Vector3(value.X / ps.X, value.Y / ps.Y, value.Z / ps.Z)
                    : value;
            }
            MarkDirty();
        }
    }

    public Vector3 EulerAngles
    {
        get => QuaternionToEuler(Rotation);
        set => Rotation = EulerToQuaternion(value);
    }

    // -------------------------------------------------------------------------
    // Direction vectors
    // -------------------------------------------------------------------------
    public Vector3 Forward => Vector3.Transform(Vector3.Forward, Rotation);
    public Vector3 Right   => Vector3.Transform(Vector3.Right,   Rotation);
    public Vector3 Up      => Vector3.Transform(Vector3.Up,      Rotation);

    // -------------------------------------------------------------------------
    // Hierarchy
    // -------------------------------------------------------------------------
    private Transform3D?       _parent;
    private List<Transform3D>  _children = new();

    public Transform3D? Parent => _parent;
    public IReadOnlyList<Transform3D> Children => _children;

    public void SetParent(Transform3D? newParent, bool keepWorldTransform = true)
    {
        if (_parent == newParent) return;

        Vector3    wp = keepWorldTransform ? Position : LocalPosition;
        Quaternion wr = keepWorldTransform ? Rotation : LocalRotation;
        Vector3    ws = keepWorldTransform ? Scale    : LocalScale;

        _parent?.RemoveChild(this);
        _parent = newParent;
        _parent?.AddChild(this);

        if (keepWorldTransform)
        {
            Position = wp;
            Rotation = wr;
            Scale    = ws;
        }

        MarkDirty();
    }

    private void AddChild(Transform3D c)    => _children.Add(c);
    private void RemoveChild(Transform3D c) => _children.Remove(c);

    // -------------------------------------------------------------------------
    // Matrix
    // -------------------------------------------------------------------------
    public Matrix GetLocalMatrix()
        => Matrix.CreateScale(_localScale)
         * Matrix.CreateFromQuaternion(_localRotation)
         * Matrix.CreateTranslation(_localPosition);

    public Matrix GetWorldMatrix()
    {
        Recalculate();
        return Matrix.CreateScale(_worldScale)
             * Matrix.CreateFromQuaternion(_worldRotation)
             * Matrix.CreateTranslation(_worldPosition);
    }

    // -------------------------------------------------------------------------
    // Space conversion
    // -------------------------------------------------------------------------
    public Vector3 TransformPoint(Vector3 localPoint)
    {
        Recalculate();
        return Vector3.Transform(localPoint * _worldScale, _worldRotation) + _worldPosition;
    }

    public Vector3 InverseTransformPoint(Vector3 worldPoint)
    {
        Recalculate();
        var delta = worldPoint - _worldPosition;
        var invRot = Quaternion.Inverse(_worldRotation);
        var local = Vector3.Transform(delta, invRot);
        var s = _worldScale;
        return s.X != 0 && s.Y != 0 && s.Z != 0
            ? new Vector3(local.X / s.X, local.Y / s.Y, local.Z / s.Z)
            : local;
    }

    public Vector3 TransformDirection(Vector3 localDir)
        => Vector3.Transform(localDir, Rotation);

    // -------------------------------------------------------------------------
    // Look-at
    // -------------------------------------------------------------------------
    public void LookAt(Vector3 target, Vector3? up = null)
    {
        var dir = target - Position;
        if (dir.LengthSquared() < 1e-6f) return;
        var matrix = Matrix.CreateWorld(Position, Vector3.Normalize(dir), up ?? Vector3.Up);
        Rotation = Quaternion.CreateFromRotationMatrix(matrix);
    }

    // -------------------------------------------------------------------------
    // Recalculation
    // -------------------------------------------------------------------------
    private void Recalculate()
    {
        if (!_dirty) return;
        _dirty = false;

        if (_parent == null)
        {
            _worldPosition = _localPosition;
            _worldRotation = _localRotation;
            _worldScale    = _localScale;
        }
        else
        {
            _worldScale    = _parent.Scale * _localScale;
            _worldRotation = _parent.Rotation * _localRotation;
            _worldPosition = Vector3.Transform(_localPosition * _parent.Scale, _parent.Rotation)
                           + _parent.Position;
        }

    }

    /// <summary>
    /// Marks this transform and its whole subtree as needing recalculation.
    /// </summary>
    /// <remarks>
    /// Every local mutation goes through here rather than setting <c>_dirty</c> alone.
    /// Marking only self was not enough: a descendant whose own flag was already clear
    /// returned its cached world transform without ever consulting the ancestor that
    /// moved, so moving a root left the rest of the chain behind. Recursion stops at a
    /// subtree that is already dirty, so a burst of edits in one frame costs one walk.
    /// </remarks>
    private void MarkDirty()
    {
        if (_dirty) return;
        _dirty = true;

        foreach (var child in _children)
            child.MarkDirty();
    }

    // -------------------------------------------------------------------------
    // Euler conversions
    // -------------------------------------------------------------------------
    /// <summary>Builds a rotation from Euler angles in degrees: X pitch, Y yaw, Z roll.</summary>
    public static Quaternion EulerToQuaternion(Vector3 eulerDegrees)
    {
        float rx = MathHelper.ToRadians(eulerDegrees.X);
        float ry = MathHelper.ToRadians(eulerDegrees.Y);
        float rz = MathHelper.ToRadians(eulerDegrees.Z);
        return Quaternion.CreateFromYawPitchRoll(ry, rx, rz);
    }

    /// <summary>The inverse of <see cref="EulerToQuaternion"/>: pitch, yaw and roll in degrees.</summary>
    public static Vector3 QuaternionToEuler(Quaternion q)
    {
        float sinr = 2f * (q.W * q.X + q.Y * q.Z);
        float cosr = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float pitch = MathF.Atan2(sinr, cosr);

        float sinp = 2f * (q.W * q.Y - q.Z * q.X);
        float yaw = MathF.Abs(sinp) >= 1f ? MathF.CopySign(MathF.PI / 2f, sinp) : MathF.Asin(sinp);

        float siny = 2f * (q.W * q.Z + q.X * q.Y);
        float cosy = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
        float roll = MathF.Atan2(siny, cosy);

        return new Vector3(
            MathHelper.ToDegrees(pitch),
            MathHelper.ToDegrees(yaw),
            MathHelper.ToDegrees(roll));
    }
}
