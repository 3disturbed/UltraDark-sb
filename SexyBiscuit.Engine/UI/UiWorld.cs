using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// The geometry that puts a canvas on a plane in the world: where its quad sits, which way
/// it faces, and how a pointer ray becomes a point in canvas units.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="UiCanvas"/> and free of any graphics device on purpose. Every
/// function here is pure arithmetic over numbers a test can write down, which is what lets
/// the browser and this engine read one fixture and prove they agree — the only check that
/// catches the two drifting apart by a fraction of a degree.
/// </para>
/// <para>
/// One convention runs through all of it, taken from the unit quad the renderer already
/// has (<c>PrimitiveMesh.Quad</c>): local <c>+X</c> is canvas right, local <c>+Y</c> is
/// canvas <em>up</em>, and the plane's normal is local <c>+Z</c>. Canvas Y grows downwards
/// and the quad's does not, so exactly one flip happens, here, and nowhere else.
/// </para>
/// </remarks>
public static class UiWorld
{
    /// <summary>
    /// A canvas's plane in world space: where its centre is and which way its axes point.
    /// </summary>
    /// <param name="Origin">The centre of the plane, being the canvas's anchor.</param>
    /// <param name="Right">Unit vector along canvas +X.</param>
    /// <param name="Up">Unit vector along canvas −Y, because canvas Y grows downwards.</param>
    /// <param name="Normal">Unit vector out of the front face.</param>
    /// <param name="Size">The plane's extent in world units, width then height.</param>
    public readonly record struct Basis(Vector3 Origin, Vector3 Right, Vector3 Up, Vector3 Normal, Vector2 Size);

    /// <summary>
    /// Builds the plane a canvas occupies.
    /// </summary>
    /// <remarks>
    /// The billboard bases come out of the inverted view matrix rather than from a look-at
    /// per canvas, which is the trick <c>ParticleSystem3D</c> already uses: the camera's own
    /// right and up vectors are three rows of that matrix, so facing the camera costs a
    /// matrix inverse for the whole frame instead of a normalise per canvas.
    /// </remarks>
    public static Basis Build(
        Vector3 anchor, UiFacing facing, Quaternion planeRotation,
        Matrix view, Vector3 cameraPosition, Vector2 worldSize)
    {
        Vector3 right, up, normal;

        switch (facing)
        {
            case UiFacing.Plane:
            {
                Matrix m = Matrix.CreateFromQuaternion(planeRotation);
                right  = new Vector3(m.M11, m.M12, m.M13);
                up     = new Vector3(m.M21, m.M22, m.M23);
                normal = new Vector3(m.M31, m.M32, m.M33);
                break;
            }

            case UiFacing.VerticalBillboard:
            {
                // Turn about the world's up axis only, so a label over a character's head
                // stays level when the camera rolls or looks down at it.
                up = Vector3.Up;

                Vector3 toCamera = cameraPosition - anchor;
                toCamera.Y = 0f;

                normal = Normalise(toCamera, Vector3.Backward);
                right  = Normalise(Vector3.Cross(up, normal), Vector3.Right);
                break;
            }

            default:
            {
                Matrix inv = Matrix.Invert(view);
                right  = new Vector3(inv.M11, inv.M12, inv.M13);
                up     = new Vector3(inv.M21, inv.M22, inv.M23);
                normal = new Vector3(inv.M31, inv.M32, inv.M33);
                break;
            }
        }

        return new Basis(anchor, Normalise(right, Vector3.Right), Normalise(up, Vector3.Up),
                         Normalise(normal, Vector3.Backward), worldSize);
    }

    /// <summary>The transform that puts the unit quad where this basis says.</summary>
    public static Matrix QuadTransform(in Basis basis)
    {
        Matrix rotation = Matrix.Identity;
        rotation.M11 = basis.Right.X;  rotation.M12 = basis.Right.Y;  rotation.M13 = basis.Right.Z;
        rotation.M21 = basis.Up.X;     rotation.M22 = basis.Up.Y;     rotation.M23 = basis.Up.Z;
        rotation.M31 = basis.Normal.X; rotation.M32 = basis.Normal.Y; rotation.M33 = basis.Normal.Z;

        return Matrix.CreateScale(basis.Size.X, basis.Size.Y, 1f)
             * rotation
             * Matrix.CreateTranslation(basis.Origin);
    }

    /// <summary>
    /// Where a ray crosses this canvas, in canvas units, or null when it misses.
    /// </summary>
    /// <remarks>
    /// A miss is a genuine null rather than a clamped edge point. The caller turns it into
    /// a coordinate far outside the canvas, which every rectangle test in the layout then
    /// rejects on its own — that is why hit-testing a canvas on a wall needs no new code in
    /// the input router at all.
    /// </remarks>
    public static Vector2? RayToCanvas(in Basis basis, Vector3 rayOrigin, Vector3 rayDirection, Vector2 canvasSize)
    {
        float facing = Vector3.Dot(rayDirection, basis.Normal);

        // Parallel to the plane, or so nearly so that the intersection is meaningless.
        if (MathF.Abs(facing) < 1e-6f) return null;

        float distance = Vector3.Dot(basis.Origin - rayOrigin, basis.Normal) / facing;
        if (distance < 0f) return null;   // The plane is behind the pointer.

        Vector3 local = rayOrigin + rayDirection * distance - basis.Origin;

        float x = Vector3.Dot(local, basis.Right);
        float y = Vector3.Dot(local, basis.Up);

        if (basis.Size.X <= 0f || basis.Size.Y <= 0f) return null;

        return new Vector2(
            (x / basis.Size.X + 0.5f) * canvasSize.X,
            (0.5f - y / basis.Size.Y) * canvasSize.Y);
    }

    /// <summary>How far along a ray this canvas's plane is, or null when the ray misses it.</summary>
    /// <remarks>
    /// What the host sorts by when several world canvases overlap on screen, so the one in
    /// front takes the click and the ones behind it are told the pointer went elsewhere.
    /// </remarks>
    public static float? RayDistance(in Basis basis, Vector3 rayOrigin, Vector3 rayDirection, Vector2 canvasSize)
    {
        if (RayToCanvas(basis, rayOrigin, rayDirection, canvasSize) is not { } point) return null;
        if (point.X < 0f || point.Y < 0f || point.X > canvasSize.X || point.Y > canvasSize.Y) return null;

        float facing = Vector3.Dot(rayDirection, basis.Normal);
        if (MathF.Abs(facing) < 1e-6f) return null;

        return Vector3.Dot(basis.Origin - rayOrigin, basis.Normal) / facing;
    }

    /// <summary>A point in canvas units placed back into world space.</summary>
    public static Vector3 CanvasToWorld(in Basis basis, Vector2 canvasPoint, Vector2 canvasSize)
    {
        float u = canvasSize.X > 0f ? canvasPoint.X / canvasSize.X : 0f;
        float v = canvasSize.Y > 0f ? canvasPoint.Y / canvasSize.Y : 0f;

        return basis.Origin
             + basis.Right * ((u - 0.5f) * basis.Size.X)
             + basis.Up    * ((0.5f - v) * basis.Size.Y);
    }

    private static Vector3 Normalise(Vector3 v, Vector3 fallback)
        => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;
}
