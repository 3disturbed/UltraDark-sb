# 30. MakeChibi

Namespace: `SexyBiscuit.Engine.Chibi` · `html5/src/chibi/`

MakeChibi builds a character out of primitives at runtime: a recipe of thirty numbers and
colours, a rig of sixteen named joints, and nine clips. It is not a model importer and it does
not touch `SkeletalAnimator` — see [Why it is not skinned](#why-it-is-not-skinned) for the
three reasons that would cost it its second engine.

A chibi costs no art assets, no import step and no shader work, and the same recipe builds the
same character in MonoGame and in the browser.

---

## The shortest thing that works

Place a **Characters → Chibi** actor, or from a script:

```js
var villager = Chibi.random(1001, 0, 0, 0);
Chibi.play(villager, "walk");
```

That is a whole character, walking. There is nothing to load.

---

## Why it is not skinned

A chibi's proportions hide its joints — that is what the style *is* — so nothing has to bend,
so nothing needs a bone palette or a skinning shader. That is the cheap route, and it is also
the only honest one:

- **`html5/src/assets/GltfLoader.js` does not exist.** `AssetManager.loadModel` imports it
  dynamically, so every model load in the browser throws and `MeshRenderer` falls back to a
  primitive. The web engine draws primitives and nothing else.
- **`SkeletalAnimator` is a shell.** Nothing in the repository ever fills its `Skeleton` or
  `Clips`; `SkinnedMeshRenderer.LoadModel` never reads Assimp's `OffsetMatrix` or
  `scene.Animations`. A skinned mesh loads and renders in bind pose for ever. It cannot be
  saved either — the serialiser's type whitelist rejects `List<Bone>` and `Matrix[]`, so the
  component writes an empty property bag. And `StandardPBR.fx` has no bone semantics.
- **JavaScript has no skinning at all.** `html5/src/animation/` is `Tween.js` and
  `TweenEasing.js`.

So a chibi is a hierarchy of `MeshRenderer` actors, built on the actor parenting from
[2. Core Architecture](02-core-architecture.md). Do not put a `SkeletalAnimator` on one; the
names invite it and it does nothing.

---

## The recipe

One small JSON file, `Assets/Characters/<Name>.chibi`. Every field defaults, so `{}` is a valid
character.

```json
{
  "version": 1,
  "name": "Villager",
  "seed": 0,
  "proportions": { "height": 1.0, "headSize": 1.0, "bodyWidth": 1.0,
                   "limbThickness": 1.0, "legLength": 1.0, "armLength": 1.0 },
  "style":   { "head": "Round", "hair": "Bob", "eyes": "Dot",
               "body": "Tunic", "legs": "Trousers", "feet": "Shoes" },
  "colours": { "skin": "#F2C6A0", "hair": "#4A2E1E", "eyes": "#241C18",
               "top": "#5B8C5A", "bottom": "#3A4A6B", "shoes": "#2E2A28",
               "accent": "#D9A441" },
  "accessories": [ { "part": "StrawHat", "colour": null } ]
}
```

Colours are hex strings rather than the engine's `{R,G,B,A}` because a person edits this file.
Proportions are clamped to 0.5–2.0. A style name the part table no longer has **falls back to
the default with a warning** rather than throwing — renaming a hairstyle must not make every
saved character unloadable.

**It has to live under `Assets/`.** Both export pipelines copy exactly `Assets`, `Scripts` and
`Scenes`; a top-level `Characters/` folder would build fine and ship nothing.

### What is on the rail

| Slot | Variants |
|---|---|
| `head` | Round, Square, Oval, Wide, Tall, Bean, Chubby, Slim |
| `hair` | Bald, Bob, Spiky, Ponytail, Bun, Long, Mohawk, Braids |
| `eyes` | Dot, Wide, Sleepy, Happy, Angry, Star, Wink, Round |
| `body` | Tunic, Jacket, Dress, Overalls, Robe, Vest, Armour, Bare |
| `legs` | Trousers, Shorts, Bare, Skirt, Baggy, Striped |
| `feet` | Shoes, Boots, Sandals, Barefoot, Heels, Paws, Skates, Slippers |
| accessories | StrawHat, Cap, TopHat, Horns, Ears, Halo, Glasses, Backpack, Cape, Wings, Sword, Wand |

---

## The rig

Sixteen joints, and the names are a contract: a clip authored against one character plays on
every character.

```
Chibi                       ← the actor you spawn and move
└─ Hips
   ├─ Torso ─ Neck ─ Head ─┬─ hair / eyes            (mesh only, never animated)
   │  │                    └─ Socket_Head, Socket_Face
   │  ├─ ArmL ─ ForearmL ─ HandL ─ Socket_Hand_L
   │  └─ ArmR ─ ForearmR ─ HandR ─ Socket_Hand_R
   ├─ ThighL ─ ShinL ─ FootL
   └─ ThighR ─ ShinR ─ FootR
```

A joint actor carries **rotation only**; the parts hanging off it are separate children holding
the offset and the scale. That is what puts a limb's pivot at the shoulder rather than half-way
down the upper arm, and it means animation writes nothing but `localEulerAngles`.

A whole character is **35 to 55 actors** depending on its wardrobe. Fine for a player and a
dozen NPCs; for a crowd, measure it. `InstancedMeshRenderer` is C#-only and needs a custom
effect, so it is not the answer — sharing materials and cutting the accessory count is.

---

## Putting one in a scene

`ChibiCharacter` is one component. A scene file says "a villager stands here" in three lines
rather than carrying forty nested actors, and the wardrobe is edited in one place.

```json
{
  "name": "Villager",
  "components": [
    { "type": "ChibiCharacter", "properties": { "RecipePath": "Assets/Characters/Villager.chibi" } },
    { "type": "ChibiAnimator",  "properties": { "Clip": "idle" } }
  ],
  "transform3d": { "x": 0, "y": 0, "z": 0 }
}
```

| Property | Meaning |
|---|---|
| `RecipePath` | A `.chibi` under `Assets/`. Empty falls back to `Seed`. |
| `Seed` | Non-zero picks a coordinated random character, reproducibly. |
| `BuildOnStart` | False leaves the building to the game. |

In the browser the recipe is read asynchronously, so the body appears a frame or two after the
actor does — the same bargain `MeshRenderer.modelPath` makes.

---

## The `Chibi` scripting global

Eight functions. They are namespace functions rather than members of the actor you get back,
because a proxy from `Scene.createActor` has no `attachTo`, `addComponent` or `children` — only
the running script's own `actor` global gained those.

```js
var v = Chibi.spawn("Assets/Characters/Villager.chibi", 0, 0, 0);
var w = Chibi.random(1001, 2, 0, 0);        // the same seed is the same character

Chibi.play(v, "walk", 0.2);                 // cross-fade over 0.2s; false for an unknown clip
Chibi.stop(v);

Chibi.setColour(v, "top", "#8C3A3A");       // repaints, no rebuild
Chibi.setStyle(v, "hair", "Mohawk");        // rebuilds the body

var torch = Scene.createActor("Torch", 0, 0);
Chibi.attach(v, "Hand_R", torch);           // Head, Face, Hand_L, Hand_R, Back
var head = Chibi.socket(v, "Head");         // the socket's own actor, for reading where it is
```

Everything crossing the boundary is a scalar or an actor proxy. There is no call that hands a
script an object, deliberately.

---

## Animation

Two halves, both producing the same pose, so the animator cross-fades between them without
caring which is which.

**Procedural** — `idle`, `walk`, `run`. Functions of phase, so a walk cycle scales with how fast
the actor is actually moving rather than playing the same cycle slowly:

```js
animator.intensity = speed / walkSpeed;     // 1 at the natural speed
```

**Keyed** — `wave`, `hit`, `jump`, `cheer`, `sit`, `die`, from `chibi-clips.json`. A wave is a
performance and a sine wave is not.

```csharp
var animator = actor.AddComponent<ChibiAnimator>();
animator.Play("walk", blend: 0.2f);
animator.ClipFinished += name => animator.Play("idle");
```

`sit` and `die` are marked `hold`: they keep their last pose instead of returning to rest.

A joint the clip does not mention **goes back to rest**, not to wherever the last clip left it —
otherwise a wave leaves the legs frozen mid-stride.

### A performance note

`ChibiAnimator` resolves its joints once, in `Start`, and keeps the `Transform3D` references.
`Actor.Transform3D` is a `GetComponent` search on every access; sixteen of those a frame per
character is exactly where that bites. **If you rebuild a character's body, call `Resolve()`
afterwards** — the animator is holding transforms that have just been destroyed.

---

## The MakeChibi panel

In the HTML5 editor, a tab beside Place Actors and Hierarchy. It edits **the selected
character**: select an actor with a `ChibiCharacter` and every slider rebuilds that actor in the
viewport, so what you are looking at is the thing you will ship.

- **Body** — the six proportion sliders.
- **Style** — a dropdown per slot, filled from the part table's own keys, so content added to
  `chibi-parts.json` appears here without a panel edit.
- **Colour** — a picker per slot, plus the curated palette as swatches.
- **Wardrobe** — accessories and their colours.
- **Motion** — every clip as a button, and a speed slider.

**Randomise** draws a whole new coordinated character. **Save** writes
`Assets/Characters/<name>.chibi`. **Place** adds a character actor and links the panel to it.
**Bake** expands the character into plain actors — the component is the right default, but a
character that needs one arm moved by hand needs the arm to be in the file.

---

## Adding content

Everything a character is made of lives in **one file**: `html5/src/chibi/chibi-parts.json`,
imported by JavaScript and embedded into the C# assembly by the csproj, exactly as
`font5x7.json` is. Neither engine can invent a hairstyle the other has not got.

A part variant is a list of primitive pieces:

```json
"Bun": [
  { "mesh": "Sphere", "joint": "Head", "pos": [0, 0.24, -0.045],
    "scale": [0.43, 0.33, 0.42], "colour": "hair",
    "widthScale": "headSize", "lengthScale": "headSize" },
  { "mesh": "Sphere", "joint": "Head", "pos": [0, 0.48, -0.05],
    "scale": [0.19, 0.18, 0.19], "colour": "hair",
    "widthScale": "headSize", "lengthScale": "headSize" }
]
```

- A joint or socket name ending in `*` is emitted twice, as `L` and `R`, with `pos.x` and
  `rot.y`/`rot.z` negated on the right.
- `widthScale` and `lengthScale` name a recipe proportion multiplying x/z and y respectively.
- **A Capsule is one unit wide and two tall**, so a limb's y scale is half the length it wants.
- Hair and eyes are authored against the `Round` head and scaled onto the others by `headFit`
  and `headEyeFit`. The second also carries how far forward that head's face is: a cube's face
  does not fall away towards the cheek as a sphere's does, so one eye depth would bury the eyes
  of one head and float those of another.

Three tests guard the content, and all three were written because the mistake had already been
made on screen:

| Test | What it caught |
|---|---|
| every part variant builds without a warning | a piece naming a joint that is not in the rig |
| no hairstyle swallows the head, on any of the eight | a bob sized for a round skull rendering a slim one as a hair-coloured box with eyes on the front |
| eyes sit on the face of every head shape | eyes placed for a sphere sinking inside a cube head, leaving a blank face |

---

## Gotchas

- **A material's keys are case-sensitive.** `{ AlbedoColor: "#2060A0FF" }` in a scene's
  `Materials` array silently produces white on both engines; it is `albedoColor`. The outer
  property bag is *not* case-sensitive, which is what makes this a trap. See
  [21. Gotchas](21-gotchas.md).
- **The camera needs `Tag = "MainCamera3D"`**, or nothing draws.
- **Rebuilding invalidates an animator's joints.** Call `Resolve()` after `Rebuild()`.
- **`Chibi.spawn` is asynchronous in the browser.** The actor is real at once; the body is not.
- **A `.chibi` outside `Assets/` will not ship.** The export copies three folders and that is
  not one of them.

---

## Next

- [10. Animation](10-animation.md) — sprite animation, tweens, and the skeletal path this
  deliberately avoids
- [11. JavaScript Scripting](11-scripting.md) — the contract the `Chibi` global belongs to
- [28. The CookieJar](28-the-cookiejar.md) — `make-chibi` is the movement and crowd layer on top
