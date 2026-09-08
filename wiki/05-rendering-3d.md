# 5. 3D Rendering

Namespace: `SexyBiscuit.Engine.Rendering`

`RenderSystem3D` is a forward renderer owned by `SBEngine`. It runs once per
frame **before** the 2D sprite pass, so sprites and UI composite on top of the
3D scene.

```
SBEngine.Draw
├── GraphicsDevice.Clear(Config.ClearColour)
├── if (Config.Enable3D)  Renderer3D.Render(activeScene)
│   ├── camera = OverrideCamera ?? Camera3D.Main      (returns early if null)
│   ├── GatherLights()          → Light3D.All
│   ├── GatherVisible()         → frustum culling + sorting
│   ├── shadow pass             (when EnableShadows)
│   ├── DrawScene()             → every MeshRenderer
│   └── post-process chain      (when any pass has a Shader)
└── SceneManager.Draw(SpriteBatch)   ← the 2D pass; you must batch this yourself
```

Set `Config.Enable3D = false` in a pure 2D game to skip the whole pass.

---

## Setting up a 3D actor

Every 3D component reads its pose from a `Transform3D`, and each will
**auto-add one** if the actor has none. Add it explicitly so you can position
the actor before anything else runs:

```csharp
var actor = new Actor("Crate");
var t3d = actor.AddComponent<Transform3D>();
t3d.Position    = new Vector3(0, 0.5f, -6);
t3d.EulerAngles = new Vector3(0, 30, 0);        // degrees
t3d.Scale       = Vector3.One;
scene.AddActor(actor);
```

Remember the actor still has its 2D `Transform` at index 0; the two are
independent. 3D code reads `Transform3D`; 2D code reads `Transform`.

---

## Camera3D

```csharp
var cameraActor = new Actor("Main Camera") { Tag = "MainCamera3D" };
var camT = cameraActor.AddComponent<Transform3D>();
camT.Position = new Vector3(0, 3, 8);
camT.LookAt(Vector3.Zero);

var cam = cameraActor.AddComponent<Camera3D>();
cam.FieldOfView = 60f;      // degrees, vertical
cam.NearClip    = 0.1f;
cam.FarClip     = 1000f;
scene.AddActor(cameraActor);
```

```csharp
cam.IsOrthographic = true;
cam.OrthoSize      = 5f;    // half-height of the ortho volume
```

### `Camera3D.Main` — Verified

```csharp
public static Camera3D? Main { get; }
```

Returns the first registered camera whose component is enabled, whose actor is
active, **and whose `Actor.Tag == "MainCamera3D"`**. That exact tag string is
required — a camera tagged `"MainCamera"` (the 2D convention used by the scene
templates) will not be found.

```csharp
var cam = Camera3D.Main;                 // null unless Tag == "MainCamera3D"
```

Cameras register themselves in `Awake` and unregister in `OnDestroy`.

### Matrices and picking

```csharp
float aspect = GraphicsDevice.Viewport.AspectRatio;
Matrix view = cam.GetViewMatrix();
Matrix proj = cam.GetProjectionMatrix(aspect);

var (origin, direction) = cam.ScreenToWorldRay(Input.MousePosition, GraphicsDevice);
if (PhysicsSystem3D.Instance.Raycast(origin, direction, 100f, out var hit))
    Debug.WriteLine($"Clicked {hit.Actor?.Name} at {hit.Point}");
```

`ScreenToWorldRay` pairs directly with `PhysicsSystem3D.Raycast` — that is the
standard mouse-picking recipe.

---

## MeshRenderer

Loads models through **AssimpNet**, so anything Assimp reads works: `.fbx`,
`.obj`, `.gltf`, `.glb`, `.dae`, `.blend`, and more.

```csharp
var mr = actor.AddComponent<MeshRenderer>();
mr.LoadModel("Assets/Models/crate.fbx", GraphicsDevice);
```

Call `LoadModel` from `OnEngineReady` or later — it needs a live
`GraphicsDevice`, so it cannot run from a constructor.

Import post-processing applied automatically:

- `Triangulate`
- `GenerateNormals`
- `GenerateUVCoords`
- `CalculateTangentSpace`
- `FlipUVs`

Each Assimp mesh becomes one `SubMesh` with its own vertex and index buffer.
Vertices are `VertexPositionNormalTexture`; index buffers are 16-bit when the
mesh has ≤ 65535 vertices and 32-bit otherwise. **Vertex colours, bone weights
and second UV sets are not uploaded** — the vertex format has no room for them.

### Drawing

`RenderSystem3D` calls this for you once per frame. Call it directly only for a
custom pass:

```csharp
mr.Draw(GraphicsDevice, view, projection);
```

Per sub-mesh, the renderer picks `Materials[sub.MaterialIndex]`, falling back to
`Material3D.Default`:

- If the material has a **`Shader`**, it is used. The renderer sets whichever of
  `World`, `View`, `Projection`, `WorldViewProjection` exist as parameters, calls
  `Material3D.Apply(effect)`, then draws every pass of the current technique.
- Otherwise a shared **`BasicEffect`** is used, with `AlbedoMap` as the texture
  when present, or `AlbedoColor` as a flat diffuse colour when not.

If no model is loaded, `Draw` renders a **unit cube** via `BasicEffect`. That is
useful for blocking out a scene before art exists.

Buffers are released in `OnDestroy`.

---

## Material3D

A plain data object — not a component.

```csharp
var mat = new Material3D
{
    AlbedoMap    = Assets.Load<Texture2D>("Assets/Models/crate_albedo.png"),
    NormalMap    = Assets.Load<Texture2D>("Assets/Models/crate_normal.png"),
    MetallicMap  = null,
    RoughnessMap = null,
    EmissiveMap  = null,
    AlbedoColor  = Color.White,
    Metallic     = 0f,
    Roughness    = 0.6f,
    EmissiveIntensity = 0f,
    Shader       = myPbrEffect,     // Effect?, optional
};
mr.Materials.Add(mat);
```

`Material3D.Apply(Effect)` writes every non-null field into same-named effect
parameters. `Material3D.Default` is a shared white, non-metallic,
roughness-0.5 material.

> PBR maps are only meaningful if your `Shader` consumes them. The
> `BasicEffect` fallback uses `AlbedoMap` / `AlbedoColor` and ignores the rest.

---

## Light3D

```csharp
var sun = new Actor("Sun");
var sunT = sun.AddComponent<Transform3D>();
sunT.LookAt(new Vector3(0.3f, -1f, 0.2f));

var light = sun.AddComponent<Light3D>();
light.Type      = LightType.Directional;
light.Color     = Color.White;
light.Intensity = 1.2f;
scene.AddActor(sun);
```

| Property | Applies to | Meaning |
|---|---|---|
| `Type` | all | `Directional`, `Point`, `Spot` (see `LightType`) |
| `Color` | all | light colour |
| `Intensity` | all | multiplier |
| `Range` | Point, Spot | radius in world units, default 10 |
| `SpotAngle` | Spot | cone half-angle in degrees, default 30 |
| `CastsShadows` | all | flag only |
| `ShadowMapSize` | all | 1024 by default |

`Light3D.GetDirection()` returns `Transform3D.Forward`.

### How lights reach a shader

Lights register themselves into `Light3D.All` in `Awake` and remove themselves
in `OnDestroy`. `RenderSystem3D.GatherLights` reads that list each frame,
selects up to `MaxLightsPerObject` per object, and uploads them through
`LightDirections` / `LightPositions` / `LightColors` / `LightParams` — see
[the shader parameter contract](#the-shader-parameter-contract).

You only touch `Light3D.All` directly if you are writing a custom pass.

`CastsShadows` and `ShadowMapSize` feed the shadow pass, which runs only when
`Renderer3D.EnableShadows` is true **and** `Renderer3D.ShadowDepthEffect` is a
compiled depth-writing effect.

---

## Skybox

```csharp
var sky = cameraActor.AddComponent<Skybox>();

// Option 1 — a vertical gradient (no assets needed)
sky.GradientTop    = new Color(0.1f, 0.3f, 0.8f);
sky.GradientBottom = new Color(0.6f, 0.7f, 0.9f);

// Option 2 — a cubemap from six face images
sky.LoadCubemap(new[]
{
    "Assets/Sky/px.png", "Assets/Sky/nx.png",
    "Assets/Sky/py.png", "Assets/Sky/ny.png",
    "Assets/Sky/pz.png", "Assets/Sky/nz.png",
}, GraphicsDevice);
```

Draw it first, before opaque geometry:

```csharp
sky.Draw(GraphicsDevice, cam);
```

---

## LODGroup

Swaps `MeshRenderer` detail levels by screen-relative size.

```csharp
var lod = actor.AddComponent<LODGroup>();
lod.BoundingRadius = 1.5f;
lod.Levels.Add(new LODLevel { ScreenRelativeTransitionHeight = 0.5f,  Renderer = highDetail });
lod.Levels.Add(new LODLevel { ScreenRelativeTransitionHeight = 0.15f, Renderer = midDetail  });
lod.Levels.Add(new LODLevel { ScreenRelativeTransitionHeight = 0.02f, Renderer = lowDetail  });
```

Drive it yourself once per frame:

```csharp
if (Camera3D.Main is { } cam) lod.Update(cam);
```

It enables exactly one renderer and disables the rest, so make each level a
separate `MeshRenderer` on the same actor or on child actors.

---

## ParticleSystem3D

The 3D counterpart to `ParticleEmitter`, with emitter shapes and billboard or
mesh rendering.

```csharp
using SexyBiscuit.Engine.Rendering;

var fx = actor.AddComponent<ParticleSystem3D>();
fx.MaxParticles      = 400;
fx.EmissionRate      = 60f;
fx.Looping           = true;
fx.Duration          = 0f;                       // 0 = no fixed duration

fx.Shape             = EmitterShape3D.Cone;      // Point, Sphere, Box, Cone, …
fx.ShapeRadius       = 0.5f;
fx.ShapeSize         = Vector3.One;
fx.ConeAngle         = 25f;

fx.MinLifetime = 1f;   fx.MaxLifetime = 2f;
fx.MinSpeed    = 1f;   fx.MaxSpeed    = 3f;
fx.MinSize     = 0.2f; fx.MaxSize     = 0.5f;
fx.EndSizeMultiplier = 0f;
```

Systems register into the static `ParticleSystem3D.All` list, the same pattern
as `Light3D`.

Units are **metres**, like everything else in 3D — a `MaxSpeed` of 3 is a
walking pace, not a projectile.

---

## CameraShake

A component, in contrast to `Camera2D`'s built-in `Shake` method.

```csharp
var shake = camActor.AddComponent<CameraShake>();
shake.DecayPerSecond  = 1.2f;
shake.Exponent        = 2f;        // trauma² gives the punch
shake.MaxOffset       = 0.5f;      // metres
shake.MaxRoll         = 3f;        // degrees
shake.Frequency       = 22f;
shake.UseUnscaledTime = true;      // keeps shaking through a hit-stop

shake.AddTrauma(0.6f);             // additive, clamped to 1
shake.Reset();

float t          = shake.Trauma;
Vector3 offset   = shake.CurrentOffset;
```

`UseUnscaledTime = true` is the default and the right one: a shake that freezes
during a `Time.TimeScale = 0` hit-stop looks broken, when the whole point is
that the two land together.

Runs in `LateUpdate`, after the camera controller has placed the camera.

---

## PostProcessing3D

A component holding an ordered pass list, applied to a `RenderTarget2D`.

```csharp
var post = cameraActor.AddComponent<PostProcessing3D>();
post.Passes.Add(PostProcessing3D.Bloom(threshold: 0.8f, intensity: 1.2f));
post.Passes.Add(PostProcessing3D.Vignette(radius: 0.75f, softness: 0.35f));
post.Passes.Add(PostProcessing3D.ColourGrade(brightness: 1.0f, contrast: 1.1f, saturation: 1.15f));
```

```csharp
RenderTarget2D result = post.Process(GraphicsDevice, sceneTarget);
```

Each pass exposes `Shader`, `Enabled` and `Name`; the three factory helpers
produce configured pass objects. Supply your own `Effect` for a pass to do real
work — the factories describe the pass, they do not ship HLSL.

---

## RenderSystem3D

The engine constructs and initialises it; reach it through
`SBEngine.Instance.Renderer3D`.

```csharp
var r = Renderer3D;
r.Enabled              = true;
r.AmbientLight         = new Color(40, 44, 52);
r.EnableFrustumCulling = true;
r.MaxLightsPerObject   = 4;
r.SortOpaqueFrontToBack= true;
r.RenderSkybox         = true;
r.EnableShadows        = false;
r.ShadowDepthEffect    = null;       // required for the shadow pass
r.ShadowDistance       = 50f;
r.OverrideCamera       = null;       // null → Camera3D.Main
r.PostProcess.Add(PostProcessing3D.Bloom(0.8f, 1.2f));
```

Per-frame counters:

```csharp
RenderStats st = Renderer3D.Stats;
Debug.WriteLine($"{st.RenderersDrawn}/{st.RenderersTotal} drawn, " +
                $"{st.RenderersCulled} culled, {st.DrawCalls} calls, " +
                $"{st.Triangles} tris, {st.LightsActive} lights");
```

`Render` returns immediately when `Enabled` is false **or when there is no
camera** — `OverrideCamera` if set, otherwise `Camera3D.Main`, which requires an
active actor tagged `"MainCamera3D"`. A black 3D scene is nearly always a
missing or mistagged camera.

### The shader parameter contract

Materials with no `Shader` are drawn with MonoGame's `BasicEffect` — three
directional lights, no shadows, no setup. Materials **with** a `Shader` receive
the full parameter set; every parameter is optional, so a minimal effect can
take just `WorldViewProjection`.

| Parameter | Type | Meaning |
|---|---|---|
| `World`, `View`, `Projection` | `float4x4` | the individual transforms |
| `WorldViewProjection` | `float4x4` | the three combined |
| `WorldInverseTranspose` | `float4x4` | correct normals under non-uniform scale |
| `CameraPosition` | `float3` | world-space eye position |
| `AmbientColor` | `float3` | ambient contribution |
| `LightCount` | `int` | valid entries in the light arrays |
| `LightDirections` | `float3[N]` | directional and spot lights |
| `LightPositions` | `float3[N]` | point and spot lights |
| `LightColors` | `float3[N]` | colour premultiplied by intensity |
| `LightParams` | `float4[N]` | `(type, range, cosSpotAngle, unused)`; type 0 = directional, 1 = point, 2 = spot |
| `ShadowMap` | `texture2D` | depth map for the primary shadow-casting light |
| `LightViewProjection` | `float4x4` | world space → shadow-map space |

`Material3D.Apply` pushes the material's own properties before these, so
`AlbedoMap`, `Metallic`, `Roughness` and the rest are already bound.

Shadows need `EnableShadows = true` **and** a compiled `ShadowDepthEffect`; the
renderer has no built-in depth shader.

---

## Camera controllers

Ready-made `Component` controllers, all deriving from `CameraControllerBase`
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
orbit.MinPitch           = -30f;
orbit.MaxPitch           = 75f;
orbit.SmoothingHalfLife  = 0.06f;
orbit.CollisionAvoidance = true;       // raycasts so the camera does not clip walls
orbit.CollisionPadding   = 0.3f;

// First person
var fps = camActor.AddComponent<FirstPersonController>();
fps.Body      = playerActor;
fps.EyeHeight = 1.7f;
fps.HeadBob   = true;
```

`OrbitCamController.SmoothingHalfLife` uses `SBMath.Damp`, which is
framerate-independent — the same camera feel at 30 and 144 fps.

---

## A complete 3D scene

Because the engine drives the 3D pass, setting up a 3D game is mostly scene
construction:

```csharp
public class Game3D : SBEngine
{
    private Camera2D? _uiCamera;

    public Game3D() : base(new EngineConfig
    {
        WindowTitle = "3D Test",
        Enable3D    = true,
    }) { }

    protected override void OnEngineReady()
    {
        Renderer3D.AmbientLight = new Color(38, 40, 48);

        var scene = SceneManager.CreateScene("Main3D");

        // Camera — the tag is what Camera3D.Main looks for.
        var camActor = new Actor("Main Camera") { Tag = "MainCamera3D" };
        var camT = camActor.AddComponent<Transform3D>();
        camT.Position = new Vector3(0, 3, 8);
        camT.LookAt(Vector3.Zero);
        camActor.AddComponent<Camera3D>();
        camActor.AddComponent<Skybox>();
        camActor.AddComponent<FlyCamController>();
        scene.AddActor(camActor);

        // Sun
        var sun = new Actor("Sun");
        sun.AddComponent<Transform3D>().LookAt(new Vector3(0.3f, -1f, 0.2f));
        var light = sun.AddComponent<Light3D>();
        light.Type      = LightType.Directional;
        light.Intensity = 1.1f;
        scene.AddActor(sun);

        // A crate
        var crate = new Actor("Crate");
        crate.AddComponent<Transform3D>().Position = new Vector3(0, 0.5f, 0);
        var mr = crate.AddComponent<MeshRenderer>();
        mr.LoadModel("Assets/Models/crate.fbx", GraphicsDevice);
        scene.AddActor(crate);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Config.ClearColour);

        var scene = SceneManager.ActiveScene;
        if (scene == null) return;

        Renderer3D.Render(scene);                     // 3D

        Renderer2D.RenderScene(SpriteBatch, scene, _uiCamera);   // 2D on top
    }
}
```

Note there is no manual mesh loop and no matrix plumbing — `Renderer3D.Render`
finds every `MeshRenderer` in the scene itself.

---

## Mixing 2D and 3D

The engine's ordering — 3D pass, then 2D pass — is the right one: the 3D scene
is the backdrop and sprites/UI composite over it. `RenderSystem3D` sets the
depth and rasteriser states it needs at the start of its pass, so you do not
have to restore them between frames.

You **do** have to restore them if you issue your own 3D draw calls *after* a
`SpriteBatch` pass in the same frame, because `SpriteBatch` leaves
`DepthStencilState.None` behind:

```csharp
GraphicsDevice.DepthStencilState = DepthStencilState.Default;
GraphicsDevice.BlendState        = BlendState.Opaque;
GraphicsDevice.RasterizerState   = RasterizerState.CullCounterClockwise;
GraphicsDevice.SamplerStates[0]  = SamplerState.LinearWrap;
```

That is the usual cause of "my model renders as a flat silhouette after I added
the HUD".

---

## Next

- [6. Physics](06-physics.md) — including Bepu 3D.
- [30. MakeChibi](30-makechibi.md) — characters built from these primitives, with no model files.
- [Tutorial 13: 3D Basics](../tutorials/13-3d-basics.md)
