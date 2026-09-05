# Tutorial 13 — 3D Basics

**You will build:** a lit 3D scene with meshes, a skybox, an orbit camera, 3D
physics, and a 2D HUD composited on top. **Time:** ~40 minutes.

Independent of Tutorials 6–12; assumes [Tutorial 4](04-components.md).

---

## 1. The 3D pass is driven for you

Unlike the 2D pass, `SBEngine.Draw` runs the 3D renderer itself:

```csharp
protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Config.ClearColour);

    if (Config.Enable3D && SceneManager.ActiveScene != null)
        Renderer3D.Render(SceneManager.ActiveScene);      // 3D first

    SceneManager.Draw(SpriteBatch);                       // then 2D — still unbatched
    base.Draw(gameTime);
}
```

`RenderSystem3D.Render` finds every `MeshRenderer` in the scene, culls, sorts,
gathers lights, draws, and runs the post-process chain. You do not write a mesh
loop.

The 2D half still needs your `SpriteBatch`, so the `Draw` override stays:

```csharp
protected override void Draw(GameTime gameTime)
{
    GraphicsDevice.Clear(Config.ClearColour);

    var scene = SceneManager.ActiveScene;
    if (scene == null) return;

    Renderer3D.Render(scene);                             // 3D world
    Renderer2D.RenderScene(SpriteBatch, scene, null);     // 2D sprites, no camera transform

    SpriteBatch.Begin();                                  // HUD
    scene.GetLayer("ui")?.Draw(SpriteBatch);
    SpriteBatch.End();
}
```

Set `Config.Enable3D = true` (the default). In a pure 2D game set it `false` to
skip the pass entirely.

## 2. Transform3D

3D components read their pose from a `Transform3D`, which each will auto-add if
missing. Add it explicitly so you can set the pose first.

```csharp
using SexyBiscuit.Engine.Core;

var actor = new Actor("Crate");
var t = actor.AddComponent<Transform3D>();
t.Position    = new Vector3(0, 0.5f, -6);
t.EulerAngles = new Vector3(0, 30, 0);          // DEGREES
t.Scale       = Vector3.One;
```

The actor still has its 2D `Transform` at index 0 — the two are independent. 3D
code reads `Transform3D`; 2D code reads `Transform`.

```csharp
t.Forward;  t.Right;  t.Up;                     // direction vectors
t.LookAt(target, up: null);
t.SetParent(parentT3d, keepWorldTransform: true);
t.TransformPoint(local);  t.InverseTransformPoint(world);
t.GetWorldMatrix();
```

Rotation is a `Quaternion`; `EulerAngles` are **degrees**. Note the asymmetry
with 2D, where `Transform.Rotation` is **radians** — a reliable source of
confusion in a mixed 2D/3D project.

## 3. Units

3D uses BepuPhysics with gravity `(0, −9.81, 0)`, **Y up**, metre scale. Unlike
2D, there is no reason to deviate: a character is ~1.8 units tall, a crate ~1,
a room ~4 tall. Stay in metres.

## 4. A scene

```csharp
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Physics;
using SexyBiscuit.Engine.Rendering;

public class Game3D : SBEngine
{
    public Game3D() : base(new EngineConfig
    {
        WindowTitle  = "3D Test",
        WindowWidth  = 1280,
        WindowHeight = 720,
        ClearColour  = new Color(16, 18, 26),
        Enable3D     = true,
    }) { }

    protected override void OnEngineReady()
    {
        Renderer3D.AmbientLight         = new Color(40, 44, 56);
        Renderer3D.EnableFrustumCulling = true;
        Renderer3D.MaxLightsPerObject   = 4;

        BuildScene();
    }

    private void BuildScene()
    {
        var scene = SceneManager.CreateScene("Main3D");

        // --- Camera. The tag is what Camera3D.Main looks for. --------------
        var camActor = new Actor("Main Camera") { Tag = "MainCamera3D" };
        var camT = camActor.AddComponent<Transform3D>();
        camT.Position = new Vector3(0, 3, 8);
        camT.LookAt(Vector3.Zero);

        var cam = camActor.AddComponent<Camera3D>();
        cam.FieldOfView = 60f;
        cam.NearClip    = 0.1f;
        cam.FarClip     = 500f;

        var sky = camActor.AddComponent<Skybox>();
        sky.GradientTop    = new Color(30, 45, 100);
        sky.GradientBottom = new Color(140, 160, 200);

        camActor.AddComponent<FlyCamController>();
        scene.AddActor(camActor);

        // --- Sun ------------------------------------------------------------
        var sun = new Actor("Sun");
        sun.AddComponent<Transform3D>().LookAt(new Vector3(0.4f, -1f, 0.3f));
        var light = sun.AddComponent<Light3D>();
        light.Type      = LightType.Directional;
        light.Color     = new Color(255, 245, 225);
        light.Intensity = 1.2f;
        scene.AddActor(sun);

        // --- Ground: a static physics box, and a visual for it --------------
        PhysicsSystem3D.Instance.AddStaticBox(
            position:    new Vector3(0, -0.5f, 0),
            rotation:    Quaternion.Identity,
            halfExtents: new Vector3(30f, 0.5f, 30f));

        var floor = new Actor("Floor");
        var floorT = floor.AddComponent<Transform3D>();
        floorT.Position = new Vector3(0, -0.5f, 0);
        floorT.Scale    = new Vector3(60f, 1f, 60f);
        floor.AddComponent<MeshRenderer>();          // no model → a unit cube, scaled
        scene.AddActor(floor);

        // --- Falling crates -------------------------------------------------
        for (int i = 0; i < 12; i++)
        {
            var crate = new Actor($"Crate{i}");
            var ct = crate.AddComponent<Transform3D>();
            ct.Position = new Vector3(
                SBMath.RandomRange(-4f, 4f),
                3f + i * 1.4f,
                SBMath.RandomRange(-4f, 4f));

            crate.AddComponent<MeshRenderer>();

            // Shape first, then rebuild the body so the shape actually registers.
            var col = crate.AddComponent<BoxCollider3D>();
            col.HalfExtents = new Vector3(0.5f);
            var rb = crate.GetComponent<Rigidbody3D>()!;    // added by the collider
            PhysicsSystem3D.Instance.RemoveBody(crate);
            rb.Awake();
            rb.Mass = 5f;

            scene.AddActor(crate);
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);

        var scene = SceneManager.ActiveScene;
        if (scene == null) return;

        Renderer3D.Render(scene);
        Renderer2D.RenderScene(SpriteBatch, scene, null);
    }
}
```

Crates fall onto a floor. The `FlyCamController` gives you WASD + mouse-look to
inspect the result.

## 5. Camera3D and `Main`

```csharp
public static Camera3D? Main { get; }
```

`Main` returns the first camera whose component is enabled, whose actor is
active, **and whose `Actor.Tag` is exactly `"MainCamera3D"`**. `RenderSystem3D`
falls back to it when `OverrideCamera` is null, and **returns immediately when
both are null** — a black 3D scene is almost always a missing or mistagged
camera.

```csharp
cam.IsOrthographic = true;
cam.OrthoSize      = 5f;              // half-height of the ortho volume

Matrix view = cam.GetViewMatrix();
Matrix proj = cam.GetProjectionMatrix(GraphicsDevice.Viewport.AspectRatio);

var (origin, direction) = cam.ScreenToWorldRay(Input.MousePosition, GraphicsDevice);
```

## 6. Camera controllers

Three ready-made controllers, all deriving from `CameraControllerBase`
(`LookSensitivity`, `InvertY`, `InputEnabled`).

```csharp
// Free-flying debug camera
var fly = camActor.AddComponent<FlyCamController>();
fly.MoveSpeed               = 12f;
fly.SprintMultiplier        = 3f;
fly.SprintKey               = Keys.LeftShift;
fly.RequireRightMouseToLook = true;

// Third-person orbit
var orbit = camActor.AddComponent<OrbitCamController>();
orbit.Target             = player;
orbit.TargetOffset       = new Vector3(0f, 1.5f, 0f);
orbit.Distance           = 6f;
orbit.MinDistance        = 1.5f;
orbit.MaxDistance        = 25f;
orbit.ZoomSpeed          = 2f;
orbit.MinPitch           = -30f;
orbit.MaxPitch           = 75f;
orbit.SmoothingHalfLife  = 0.06f;
orbit.CollisionAvoidance = true;      // raycasts so the camera does not clip geometry
orbit.CollisionPadding   = 0.3f;

// First person
var fps = camActor.AddComponent<FirstPersonController>();
fps.Body      = playerActor;
fps.EyeHeight = 1.7f;
fps.HeadBob   = true;
```

`SmoothingHalfLife` uses `SBMath.Damp`, which is framerate-independent — the
same camera feel at 30 and 144 fps. Prefer it to `Lerp(a, b, 0.1f)` anywhere you
smooth per frame.

## 7. Meshes and materials

```csharp
var mr = actor.AddComponent<MeshRenderer>();
mr.LoadModel("Assets/Models/crate.fbx", GraphicsDevice);
```

Anything AssimpNet reads works: `.fbx`, `.obj`, `.gltf`, `.glb`, `.dae`,
`.blend`. Import post-processing (triangulate, generate normals and UVs,
calculate tangents, flip UVs) is applied automatically.

Call `LoadModel` from `OnEngineReady` or later — it needs a live
`GraphicsDevice`, so it cannot run from a constructor.

**With no model, `MeshRenderer` draws a unit cube.** That is genuinely useful
for blocking out a level before art exists — every crate above is one.

```csharp
using SexyBiscuit.Engine.Rendering;

var mat = new Material3D
{
    AlbedoMap    = Assets.Load<Texture2D>("Assets/Models/crate_albedo.png"),
    NormalMap    = Assets.Load<Texture2D>("Assets/Models/crate_normal.png"),
    AlbedoColor  = Color.White,
    Metallic     = 0f,
    Roughness    = 0.6f,
    Shader       = null,          // null → MonoGame's BasicEffect
};
mr.Materials.Add(mat);
```

With `Shader = null` the renderer uses `BasicEffect`: three directional lights,
no shadows, `AlbedoMap` or `AlbedoColor`, and everything else ignored. That is
enough to see your scene without shipping a content pipeline.

Supply a `Shader` and the renderer binds a full parameter set — every parameter
optional, so a minimal effect can take just `WorldViewProjection`:

| Parameter | Meaning |
|---|---|
| `World`, `View`, `Projection`, `WorldViewProjection` | transforms |
| `WorldInverseTranspose` | correct normals under non-uniform scale |
| `CameraPosition` | world-space eye |
| `AmbientColor` | ambient contribution |
| `LightCount` | valid entries in the light arrays |
| `LightDirections`, `LightPositions`, `LightColors` | per-light data |
| `LightParams` | `(type, range, cosSpotAngle, unused)`; 0 = directional, 1 = point, 2 = spot |
| `ShadowMap`, `LightViewProjection` | shadow pass output |

`Material3D.Apply` pushes the material's own properties first, so `AlbedoMap`,
`Metallic` and friends are already bound.

## 8. Lights

```csharp
var light = actor.AddComponent<Light3D>();
light.Type         = LightType.Point;      // Directional, Point, Spot
light.Color        = new Color(255, 200, 150);
light.Intensity    = 2f;
light.Range        = 12f;                  // Point and Spot
light.SpotAngle    = 35f;                  // Spot, half-angle in degrees
light.CastsShadows = true;
light.ShadowMapSize = 1024;
```

Lights register themselves in `Light3D.All`, and `RenderSystem3D` gathers up to
`MaxLightsPerObject` per object and uploads them. You do not touch that list
unless you are writing a custom pass.

Shadows need **both** `Renderer3D.EnableShadows = true` and a compiled
`Renderer3D.ShadowDepthEffect` — the renderer has no built-in depth shader.

## 9. 3D physics

```csharp
using SexyBiscuit.Engine.Physics;

var rb = actor.AddComponent<Rigidbody3D>();
rb.Mass            = 70f;
rb.LinearDamping   = 0.05f;
rb.AngularDamping  = 0.05f;
rb.IsKinematic     = false;

rb.AddForce(new Vector3(0, 500, 0));
rb.AddImpulse(new Vector3(0, 6, 0));
rb.AddTorque(new Vector3(0, 2, 0));
```

Shapes:

```csharp
actor.AddComponent<BoxCollider3D>().HalfExtents = new Vector3(0.5f, 0.9f, 0.5f);
actor.AddComponent<SphereCollider3D>().Radius   = 0.5f;

var cap = actor.AddComponent<CapsuleCollider3D>();
cap.Radius = 0.4f;
cap.Length = 1.2f;                    // cylindrical section, excluding caps
```

**The shape is registered during `Awake`**, from the defaults, before you can set
`Radius`/`HalfExtents`. Rebuild:

```csharp
var col = actor.AddComponent<CapsuleCollider3D>();     // registers a 0.5 × 1 capsule
col.Radius = 0.4f;
col.Length = 1.2f;

var rb = actor.GetComponent<Rigidbody3D>()!;           // added by the collider
PhysicsSystem3D.Instance.RemoveBody(actor);            // drop the stale body
rb.Awake();                                            // re-register with the new shape
rb.Mass = 70f;                                         // body props apply live
```

And note: **a `Rigidbody3D` with no collider falls back to a 0.5-radius sphere.**
Boxes that roll are this.

Static geometry:

```csharp
PhysicsSystem3D.Instance.AddStaticBox(position, Quaternion.Identity, halfExtents);
PhysicsSystem3D.Instance.AddStaticMesh(vertices, indices, position);
```

## 10. Character movement

```csharp
var cc = player.AddComponent<CharacterController3D>();   // auto-adds Rigidbody3D
cc.MoveSpeed    = 5f;
cc.JumpSpeed    = 8f;
cc.StepUpHeight = 0.3f;
cc.SlopeLimit   = 45f;
cc.SnapDistance = 0.1f;
```

```csharp
public sealed class ThirdPersonInput : Component
{
    private CharacterController3D _cc = null!;
    private Transform3D           _t  = null!;
    private Camera3D?             _cam;

    public override void Start()
    {
        _cc  = GetComponent<CharacterController3D>()!;
        _t   = GetComponent<Transform3D>()!;
        _cam = Camera3D.Main;
    }

    public override void Update(float dt)
    {
        var input = SBEngine.Instance.Input;
        float x = input.GetAxis("MoveX");
        float y = input.GetAxis("MoveY");

        // Move relative to the camera's heading, flattened to the ground plane.
        Vector3 forward = Vector3.UnitZ * -1f, right = Vector3.UnitX;
        if (_cam != null)
        {
            var ct = _cam.Actor.GetComponent<Transform3D>()!;
            forward = SBMath.SafeNormalize(new Vector3(ct.Forward.X, 0f, ct.Forward.Z));
            right   = SBMath.SafeNormalize(new Vector3(ct.Right.X,   0f, ct.Right.Z));
        }

        var move = (forward * y + right * x);
        if (move.LengthSquared() > 1f) move = SBMath.SafeNormalize(move);

        _cc.Move(move * _cc.MoveSpeed);        // a VELOCITY — do not multiply by dt

        if (input.IsPressed("Jump") && _cc.IsGrounded) _cc.Jump();

        if (move.LengthSquared() > 0.01f)
            _t.LookAt(_t.Position + move);
    }
}
```

`Move` takes a **velocity**, not a displacement. Multiplying by `dt` gives a
character that moves at a sixtieth of the intended speed.

## 11. Mouse picking

```csharp
public sealed class Picker : Component
{
    public override void Update(float dt)
    {
        var engine = SBEngine.Instance;
        if (!engine.Input.IsMouseButtonPressed(MouseButton.Left)) return;

        var cam = Camera3D.Main;
        if (cam == null) return;

        var (origin, direction) = cam.ScreenToWorldRay(engine.Input.MousePosition,
                                                       engine.GraphicsDevice);

        if (PhysicsSystem3D.Instance.Raycast(origin, direction, 200f, out var hit))
        {
            Debug.WriteLine($"picked {hit.Actor?.Name} at {hit.Point}");
            hit.Actor?.GetComponent<Rigidbody3D>()?.AddImpulse(direction * 30f);
        }
    }
}
```

`ScreenToWorldRay` + `PhysicsSystem3D.Raycast` is the standard picker.

## 12. 3D particles and camera shake

```csharp
var fx = actor.AddComponent<ParticleSystem3D>();
fx.MaxParticles = 400;
fx.EmissionRate = 60f;
fx.Shape        = EmitterShape3D.Cone;
fx.ConeAngle    = 25f;
fx.MinSpeed = 1f;  fx.MaxSpeed = 3f;      // metres/second
fx.MinSize  = 0.2f; fx.MaxSize = 0.5f;    // metres
```

```csharp
var shake = camActor.AddComponent<CameraShake>();
shake.MaxOffset       = 0.5f;    // metres
shake.MaxRoll         = 3f;      // degrees
shake.UseUnscaledTime = true;    // keeps shaking through a hit-stop
shake.AddTrauma(0.6f);           // additive, clamped to 1
```

Trauma decays and is raised to `Exponent` (default 2) before being applied, so
a hit reads as a sharp jolt that settles. Runs in `LateUpdate`, after the camera
controller has placed the camera.

## 13. Skybox and LOD

```csharp
var sky = camActor.AddComponent<Skybox>();

// Gradient — no assets needed
sky.GradientTop    = new Color(30, 45, 100);
sky.GradientBottom = new Color(140, 160, 200);

// Or a cubemap
sky.LoadCubemap(new[]
{
    "Assets/Sky/px.png", "Assets/Sky/nx.png",
    "Assets/Sky/py.png", "Assets/Sky/ny.png",
    "Assets/Sky/pz.png", "Assets/Sky/nz.png",
}, GraphicsDevice);
```

`Renderer3D.RenderSkybox` controls whether the pass draws it.

```csharp
var lod = actor.AddComponent<LODGroup>();
lod.BoundingRadius = 1.5f;
lod.Levels.Add(new LODLevel { ScreenRelativeTransitionHeight = 0.5f,  Renderer = high });
lod.Levels.Add(new LODLevel { ScreenRelativeTransitionHeight = 0.15f, Renderer = mid  });
lod.Levels.Add(new LODLevel { ScreenRelativeTransitionHeight = 0.02f, Renderer = low  });
```

Drive it yourself — the engine does not:

```csharp
public sealed class LodDriver : Component
{
    public override void Update(float dt)
    {
        if (Camera3D.Main is { } cam) GetComponent<LODGroup>()?.Update(cam);
    }
}
```

## 14. Mixing 2D and 3D

The engine's order — 3D pass, then 2D — is the right one: 3D is the world,
sprites and UI composite over it. `RenderSystem3D` sets its own depth and
rasteriser states each pass, so you do not have to restore them between frames.

You **do** have to restore them if you issue your own 3D draws *after* a
`SpriteBatch` pass in the same frame, because `SpriteBatch` leaves
`DepthStencilState.None` behind:

```csharp
GraphicsDevice.DepthStencilState = DepthStencilState.Default;
GraphicsDevice.BlendState        = BlendState.Opaque;
GraphicsDevice.RasterizerState   = RasterizerState.CullCounterClockwise;
GraphicsDevice.SamplerStates[0]  = SamplerState.LinearWrap;
```

Forgetting this is the usual cause of "my model became a flat silhouette after I
added the HUD".

## 15. Render stats

```csharp
RenderStats s = Renderer3D.Stats;
Debug.WriteLine($"{s.RenderersDrawn}/{s.RenderersTotal} drawn, {s.RenderersCulled} culled, " +
                $"{s.DrawCalls} calls, {s.Triangles} tris, {s.LightsActive} lights");
```

`RenderersCulled` staying at zero means frustum culling is not helping — check
that your meshes have sensible bounds and that `EnableFrustumCulling` is on.

---

## Checkpoint

You have:

- A lit 3D scene with a skybox, meshes, and falling physics bodies
- The `Camera3D.Main` tag requirement, and why a black scene usually means it
- Three camera controllers, including framerate-independent orbit smoothing
- 3D physics, including the shape-rebuild dance
- Mouse picking, LOD, and a 2D HUD on top

## Troubleshooting

| Symptom | Cause |
|---|---|
| Black screen | no camera tagged `"MainCamera3D"`, or `Enable3D = false` |
| Everything is a cube | `MeshRenderer` with no model — that is the fallback |
| Nothing falls | `Config.EnablePhysics3D` is false |
| Boxes roll like balls | no `Collider3D` when `Rigidbody3D.Awake` ran |
| Collider is the wrong size | shape set after `AddComponent`; rebuild the body |
| Character crawls | `Move` called with velocity × `dt` |
| Model is a flat silhouette | depth state not restored after a `SpriteBatch` pass |
| No shadows | `EnableShadows` without a `ShadowDepthEffect` |

## Exercises

1. Swap `FlyCamController` for `OrbitCamController` on a character.
2. Add point lights on a timer and watch `Stats.LightsActive`.
3. Add a `Constraint3D` — hang a crate from an anchor with
   `DistanceLimitConstraint`.

---

**Next:** [Tutorial 14 — The Gameplay Framework](14-gameplay-framework.md)
