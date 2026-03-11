using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// 2D transform component — position, rotation (radians), scale, parent/child hierarchy.
/// Every Actor has exactly one Transform, always at index 0 in its component list.
/// </summary>
public sealed class Transform : Component
{
    // -------------------------------------------------------------------------
    // Local space
    // -------------------------------------------------------------------------
    private Vector2 _localPosition;
    private float   _localRotation;
    private Vector2 _localScale = Vector2.One;

    public Vector2 LocalPosition
    {
        get => _localPosition;
        set { _localPosition = value; _dirty = true; }
    }

    public float LocalRotation
    {
        get => _localRotation;
        set { _localRotation = value; _dirty = true; }
    }

    public Vector2 LocalScale
    {
        get => _localScale;
        set { _localScale = value; _dirty = true; }
    }

    // -------------------------------------------------------------------------
    // World space (computed lazily)
    // -------------------------------------------------------------------------
    private Vector2 _worldPosition;
    private float   _worldRotation;
    private Vector2 _worldScale;
    private bool    _dirty = true;

    public Vector2 Position
    {
        get { Recalculate(); return _worldPosition; }
        set
        {
            if (_parent == null)
                _localPosition = value;
            else
                _localPosition = InverseTransformPoint(value);
            _dirty = true;
        }
    }

    public float Rotation
    {
        get { Recalculate(); return _worldRotation; }
        set
        {
            _localRotation = _parent == null ? value : value - _parent.Rotation;
            _dirty = true;
        }
    }

    public Vector2 Scale
    {
        get { Recalculate(); return _worldScale; }
        set
        {
            if (_parent == null)
                _localScale = value;
            else
            {
                var ps = _parent.Scale;
                _localScale = ps.X != 0 && ps.Y != 0
                    ? new Vector2(value.X / ps.X, value.Y / ps.Y)
                    : value;
            }
            _dirty = true;
        }
    }

    // -------------------------------------------------------------------------
    // Direction helpers
    // -------------------------------------------------------------------------
    public Vector2 Right => new Vector2(MathF.Cos(Rotation), MathF.Sin(Rotation));
    public Vector2 Up    => new Vector2(-MathF.Sin(Rotation), MathF.Cos(Rotation));

    // -------------------------------------------------------------------------
    // Hierarchy
    // -------------------------------------------------------------------------
    private Transform?       _parent;
    private List<Transform>  _children = new();

    public Transform? Parent => _parent;
    public IReadOnlyList<Transform> Children => _children;

    public void SetParent(Transform? newParent, bool keepWorldPosition = true)
    {
        if (_parent == newParent) return;

        Vector2 worldPos = keepWorldPosition ? Position : LocalPosition;
        float   worldRot = keepWorldPosition ? Rotation : LocalRotation;
        Vector2 worldScl = keepWorldPosition ? Scale    : LocalScale;

        _parent?.RemoveChild(this);
        _parent = newParent;
        _parent?.AddChild(this);

        if (keepWorldPosition)
        {
            Position = worldPos;
            Rotation = worldRot;
            Scale    = worldScl;
        }

        _dirty = true;
    }

    private void AddChild(Transform c)    => _children.Add(c);
    private void RemoveChild(Transform c) => _children.Remove(c);

    // -------------------------------------------------------------------------
    // Matrix
    // -------------------------------------------------------------------------
    public Matrix GetLocalMatrix()
        => Matrix.CreateScale(_localScale.X, _localScale.Y, 1f)
         * Matrix.CreateRotationZ(_localRotation)
         * Matrix.CreateTranslation(_localPosition.X, _localPosition.Y, 0f);

    public Matrix GetWorldMatrix()
    {
        Recalculate();
        return Matrix.CreateScale(_worldScale.X, _worldScale.Y, 1f)
             * Matrix.CreateRotationZ(_worldRotation)
             * Matrix.CreateTranslation(_worldPosition.X, _worldPosition.Y, 0f);
    }

    // -------------------------------------------------------------------------
    // Space conversion helpers
    // -------------------------------------------------------------------------
    public Vector2 TransformPoint(Vector2 localPoint)
    {
        float cos = MathF.Cos(Rotation);
        float sin = MathF.Sin(Rotation);
        var scaled = localPoint * Scale;
        return new Vector2(
            Position.X + scaled.X * cos - scaled.Y * sin,
            Position.Y + scaled.X * sin + scaled.Y * cos);
    }

    public Vector2 InverseTransformPoint(Vector2 worldPoint)
    {
        var delta = worldPoint - Position;
        float cos = MathF.Cos(-Rotation);
        float sin = MathF.Sin(-Rotation);
        var rotated = new Vector2(delta.X * cos - delta.Y * sin, delta.X * sin + delta.Y * cos);
        var s = Scale;
        return s.X != 0 && s.Y != 0 ? rotated / s : rotated;
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
            _worldScale    = _parent.Scale    * _localScale;
            _worldRotation = _parent.Rotation + _localRotation;

            float cos = MathF.Cos(_parent.Rotation);
            float sin = MathF.Sin(_parent.Rotation);
            var scaled = _localPosition * _parent.Scale;
            _worldPosition = _parent.Position + new Vector2(
                scaled.X * cos - scaled.Y * sin,
                scaled.X * sin + scaled.Y * cos);
        }

        // Propagate dirty to children
        foreach (var child in _children)
            child._dirty = true;
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------
    public void LookAt(Vector2 target)
    {
        var dir = target - Position;
        if (dir.LengthSquared() > 0)
            Rotation = MathF.Atan2(dir.Y, dir.X);
    }

    public float DistanceTo(Transform other) => Vector2.Distance(Position, other.Position);

    public override string ToString() =>
        $"Transform(pos={_localPosition}, rot={_localRotation:F2}, scale={_localScale})";
}
