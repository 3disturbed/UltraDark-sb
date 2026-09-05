# Engine shaders

HLSL source for the engine's built-in effects. These are **not** compiled as part of
`dotnet build` — MonoGame effects go through the content pipeline, which produces a
binary `.xnb` for the target platform.

## What's here

| File | What it does |
|---|---|
| `StandardPBR.fx` | The default lit surface shader. Implements the parameter contract documented on `RenderSystem3D`: metallic/roughness PBR with up to 4 lights and a shadow lookup. |
| `ShadowDepth.fx` | Depth-only pass for `RenderSystem3D.ShadowDepthEffect`. |
| `PostProcess.fx` | Bloom, Vignette, ColourGrade and Scanline as four techniques in one effect. |
| `Content.mgcb` | Content pipeline manifest that builds all three. |

## Building them

Install the MonoGame content builder once:

```bash
dotnet tool install -g dotnet-mgcb
```

Then build from this directory:

```bash
dotnet mgcb /@:Content.mgcb
```

That writes `bin/DesktopGL/*.xnb`. Copy them into your game's content directory (or
point `Content.RootDirectory` at it) and load them by name:

```csharp
var pbr = Content.Load<Effect>("StandardPBR");
material.Shader = pbr;

Renderer3D.EnableShadows     = true;
Renderer3D.ShadowDepthEffect = Content.Load<Effect>("ShadowDepth");
```

For a platform other than DesktopGL, change `/platform:` in `Content.mgcb`
(`Windows`, `Android`, `iOS`) and rebuild.

## Why shaders aren't precompiled in the repo

An `.xnb` is platform- and profile-specific, so a checked-in binary would be wrong for
most people who clone this. Shipping the HLSL and a one-line build command keeps the
source readable and the output correct for whatever you target.

## Without compiled shaders

Everything still runs. `RenderSystem3D` falls back to MonoGame's built-in
`BasicEffect` for any material with no `Shader`, which gives you three directional
lights, textures and vertex colours — enough to see and iterate on a scene. What you
lose is metallic/roughness response, point and spot light attenuation, and shadows.
