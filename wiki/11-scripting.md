# 11. JavaScript Scripting

Namespace: `SexyBiscuit.Engine.Scripting`

Attach a `.js` file to any `Actor` and it runs as gameplay logic — no compile
step, hot-reloadable in dev builds.

```
ScriptComponent  (a Component; holds ScriptPath)
└── JintRuntime   (one Jint.Engine per component)
    └── ScriptBridge  (builds the JS globals: actor, transform, Input, …)
```

Each `ScriptComponent` gets its **own isolated Jint engine**. Scripts do not
share globals, and there is no `require` / `import` — one file, one actor.

---

## The scripting contract

The globals below are registered by `JintRuntime.RegisterGlobals()` and, member for member,
by the HTML5 runtime's `html5/src/scripting/ScriptBridge.js`. The list itself lives in
`html5/src/scripting/bridge-api.json`; a test on each side (`ScriptBridgeParityTests` and
`html5/tests/bridge.test.js`) holds its bridge to that file in both directions, so the two can
only drift apart by failing a build. A script that uses only what is here runs unchanged in the
browser and natively. The `.d.ts` produced by `TypeScriptDefinitions.Generate()` matches it.

### `actor` — the actor owning this script

```js
actor.id                 // number, read-only
actor.name               // string, get/set
actor.tag                // string, get/set
actor.active             // boolean, get/set
actor.transform          // the same object as the `transform` global
actor.transform3d        // Transform3D proxy, or null in a 2D scene
actor.getComponent("Rigidbody2D")   // a component proxy, or null
actor.addComponent("BoxCollider2D") // adds and returns a component proxy
actor.destroy()          // queue removal at end of frame (takes its children with it)

actor.parent             // the actor it is attached to, or null
actor.children           // an array of actor proxies, a fresh copy each read
actor.attachTo(other)             // keeps its world position
actor.attachTo(other, false)      // treats its transform as a local offset
actor.detach()                    // back to the scene root
actor.findChild("Muzzle")         // direct children
actor.findChild("Muzzle", true)   // the whole subtree
```

### `transform` and `transform3d`

```js
transform.x, transform.y          // world position (get/set)
transform.rotation                // radians (get/set)
transform.scaleX, transform.scaleY
transform.lookAt(x, y);
transform.distanceTo({ x: 10, y: 20 });   // number

transform3d.x, .y, .z             // world position; null without a Transform3D
transform3d.rotX, .rotY, .rotZ    // Euler angles in degrees
transform3d.lookAt(x, y, z);
```

### Component proxies

`getComponent` returns a proxy over the component's declared properties — the same set the
editor's inspector edits — under both the C# name and its camelCase form, so `rb.GravityScale`
and `rb.gravityScale` are one property. Vectors read as `{x, y}`, colours as `{r, g, b, a}`, and
either form (or `[x, y]`, `"#FF8040"`, `{R, G, B, A}`) is accepted on assignment. Public methods
whose parameters are simple values are callable. Two extras the templates rely on:

```js
var rb = actor.getComponent("Rigidbody2D");
rb.velocityX = 120;                 // shorthand over LinearVelocity
rb.velocity = { x: 120, y: 0 };

var other = Scene.find("Boss").getComponent("ScriptComponent");
other.invoke("takeDamage", 5);      // any top-level function the other script defines
```

### `Input`

```js
Input.isPressed("Jump"); Input.isHeld("MoveX"); Input.isReleased("Attack");
Input.getAxis("MoveX");                       // number, −1..1
Input.mouseX, Input.mouseY                    // screen pixels
Input.mouseDeltaX, Input.mouseDeltaY, Input.scrollDelta
Input.isKeyDown("A"); Input.isKeyHeld("a"); Input.isKeyPressed("KeyA"); Input.isKeyReleased("Space");
Input.isMouseDown(0); Input.isMouseHeld("Right"); Input.isMousePressed(2); Input.isMouseReleased(1);
Input.touchCount; Input.getTouch(0);          // { id, x, y, phase } or null
Input.joystickX, Input.joystickY              // the left on-screen stick
```

Key names accept XNA's spelling (`"A"`, `"D1"`, `"Left"`) and the browser's (`"KeyA"`,
`"Digit1"`, `"ArrowLeft"`); `Input/KeyNames.cs` and `html5/src/input/Keys.js` share the table.
Mouse buttons are 0 left, 1 middle, 2 right on both engines. Action names come from the
[action map](07-input.md#action-maps).

### `Audio`

```js
var music = Audio.play("Assets/Audio/theme.ogg", true);   // { id }, id 0 when nothing played
Audio.playOneShot("Assets/Audio/hit.wav", 0.8);
Audio.stop(music);
Audio.setVolume(0.5);                                     // master bus
```

### `Scene`

```js
Scene.name
Scene.find("Player")             // actor proxy or null
Scene.findAll("Coin")            // every actor with that name
Scene.findByTag("Enemy")         // an array — an empty one is still truthy
Scene.findFirstByTag("Player")   // one actor or null
Scene.createActor("Spark", x, y)
Scene.addComponent(proxy, "SpriteRenderer", { Tint: "#FF0000" })
Scene.destroy(proxy)             // or Scene.destroyActor(proxy)
Scene.instantiate("Prefabs/Bullet.json", x, y)   // a prefab file, else an empty actor
Scene.load("Scenes/Level2")
```

Queries resolve through the scene the actor is in, so they work in a test, a headless tool or
a scene that is not the active one. A found actor is `{ id, name, tag, active,
transform: { x, y, rotation }, getComponent(), destroy() }`, and null once destroyed.

### `Physics`, `Time`, `Network`

```js
Physics.raycast(ox, oy, dx, dy, maxDistance)   // { actor, x, y, normalX, normalY, distance } or null
Physics.overlapCircle(x, y, r)                 // actor proxies
Physics.overlapBox(x, y, w, h)

Time.deltaTime, Time.unscaledDeltaTime, Time.time, Time.frameCount, Time.fps
Time.timeScale = 0.5;

Network.localId, Network.isServer, Network.isHost, Network.isConnected, Network.ping
Network.room, Network.players, Network.playerName
Network.isLocalPlayer(id)                       // true for id 0 before a server says otherwise
Network.startServer(port), Network.startSolo(), Network.connect(address, port), Network.disconnect()
Network.sendToAll(type, data), Network.broadcast(type, data), Network.sendTo(id, type, data)
Network.on("message" | "playerJoined" | "playerLeft" | "connected" | "disconnected", fn)
```

`Network` is real on both engines and drives the same wire — see
[15. Networking](15-networking.md). Two things worth knowing before you use it:

- **A script that sends with no session running starts a solo one.** A multiplayer game played
  alone is the single-player case, not an error, and going through the same encode, dispatch and
  replication path is what stops "works alone, breaks with two players".
- **`isHost` means "should I run the simulation?"** — the server when there is one, and otherwise
  the lowest client id in the room.

Messages arrive on a lifecycle hook, like collisions do:

```js
function onNetworkMessage(type, data, sender) {
    if (type === "playerMove") movePeer(sender, data);
}
```

`sender` is the id the **server** assigned, never the one in the payload: a peer that can name
itself can name anybody.

### `DG` — the Darks Games account and social layer

```js
DG.available, DG.signedIn, DG.userId, DG.userName, DG.handle, DG.displayName, DG.game
DG.presence({ state: "wave 7", detail: "Gatehold", joinCode: room, players: 3, max: 4 })
DG.clearPresence()
DG.achievement("first_clear"), DG.achievement("kills", 10)
DG.loadSave(), DG.saveCloud(data, version)
DG.on("user" | "save" | "saveConflict" | "achievement", fn)
```

Every read is safe signed out and safe with no hub at all, so a game that never ships to
DarksGames still runs every line of a script that uses this. Reads are properties and writes are
fire-and-forget, so a cloud save arrives on `DG.on("save")` rather than as a return value — Jint
cannot await, and one contract has to describe both engines. See
[29. Darks Games](29-darksgames.md).

### `UI` — screen space

Everything above is world space. `UI` is the screen, and it is the only global that knows how big
the window is.

```js
UI.width, UI.height                       // the viewport in pixels, which nothing else can ask for

UI.panel(x, y, w, h, options);            // a filled box
UI.label(x, y, "text", options);          // real text, from the shared 5x7 bitmap font
UI.bar(x, y, w, h, value01, options);     // a track and a fill
UI.button(x, y, w, h, "text", options);   // panel + centred text + hover + click
UI.image(x, y, w, h, "Assets/hud.png", options);

UI.clear();                               // this script's elements only
UI.measure("text", scale);                // width in pixels, for laying a panel out around it
```

Each returns a handle:

```js
var hp = UI.bar(12, 12, 200, 10, 1, { anchor: "topleft", tint: "#c63832", background: "#2a2d34" });
hp.value = health / maxHealth;            // the one you will write every frame

hp.x; hp.y; hp.width; hp.height; hp.text; hp.scale; hp.visible;
hp.tint; hp.background; hp.anchor; hp.align; hp.padding; hp.texturePath;
hp.hovered; hp.clicked;                   // read-only; `clicked` is true for one frame
hp.destroy();
```

**The anchor is the point.** It is both where on the screen the element hangs *and* which of its
own corners hangs there, so this stays twelve pixels in from the bottom-right at any window size,
on a phone included:

```js
UI.label(-12, -12, "v1.0.1", { anchor: "bottomright" });
```

The names are `topleft top topright left center right bottomleft bottom bottomright`.

A **label with no width measures itself**, so a right- or centre-anchored one positions correctly
without you measuring the text by hand every time it changes. Give it an explicit width when you
are laying out a column and want the number to be yours.

`clicked` is polled rather than a callback: a JS function held by the C# side is the kind of thing
that marshals differently on the two engines, and a boolean does not.

```js
if (startButton.clicked) { Scene.load("Scenes/Level1"); }
```

Colours take the forms the rest of the engine takes — `"#ff8040"`, `"#ff8040c0"`, `[255,128,64]`,
`{R:255,G:128,B:64}`. Prefer eight-digit hex for translucency: `rgba()` is browser-only and would
draw nothing natively.

Text is a 5x7 bitmap font defined in `html5/src/ui/font5x7.json`, which the browser imports and
the C# engine embeds — one file, so the two cannot render different text. `scale` is a whole
multiple of that cell and is rounded; the font has no half pixels.

### `Chibi` — MakeChibi's characters

Spawns and drives a character built from primitives. Nothing here takes or returns an object;
every argument is a scalar or an actor proxy. See [29. MakeChibi](29-makechibi.md).

```js
var v = Chibi.spawn("Assets/Characters/Villager.chibi", 0, 0, 0);
var w = Chibi.random(1001, 2, 0, 0);        // the same seed is the same character

Chibi.play(v, "walk", 0.2);                 // false for a clip name that does not exist
Chibi.stop(v);

Chibi.setColour(v, "top", "#8C3A3A");       // repaints; no rebuild
Chibi.setStyle(v, "hair", "Mohawk");        // rebuilds the body

Chibi.attach(v, "Hand_R", torch);           // Head, Face, Hand_L, Hand_R, Back
Chibi.socket(v, "Head");                    // the socket's actor, or null
```

These are namespace functions rather than members of the actor you get back because an actor
proxy from `Scene.createActor` carries only `id`, `name`, `tag`, `active`, `transform`,
`transform3d`, `getComponent` and `destroy` — the `attachTo` and `addComponent` above belong to
the running script's own `actor` global, not to every proxy.

Clips: `idle`, `walk`, `run` are procedural and scale with the animator's `intensity`; `wave`,
`hit`, `jump`, `cheer`, `sit`, `die` are keyed.

### `Debug`, `log`, `warn`, `error`, `Vector2`

```js
Debug.log("hp", hp); log("same thing"); warn("careful"); error("bad");
Vector2.create(x, y); Vector2.add(a, b); Vector2.sub(a, b); Vector2.scale(v, s);
Vector2.normalize(v); Vector2.dot(a, b); Vector2.distance(a, b); Vector2.length(v);
```

Log output goes through `ScriptDiagnostics` (below), not `Debug.WriteLine`, so it survives a
Release build.

## Lifecycle hooks

A script defines any of these at top level; the engine calls the ones it finds
(`JintRuntime.KnownHooks`, the same thirteen as the browser's `SCRIPT_HOOKS`):

```js
function onAwake() {}            // once, when the script loads — after the actor has joined its scene
function onStart() {}            // once, on the first frame
function onUpdate(dt) {}
function onFixedUpdate(dt) {}
function onLateUpdate(dt) {}
function onDestroy() {}
function onCollisionEnter(data) {}   // data = { other, contactPoint, normal, relativeVelocity, tag, name }
function onCollisionStay(data) {}
function onCollisionExit(data) {}
function onTriggerEnter(other) {}    // other = actor proxy
function onTriggerStay(other) {}
function onTriggerExit(other) {}
function onNetworkMessage(type, data, sender) {}   // sender = the id the server assigned
```

`data.tag` and `data.name` forward to `data.other`, so `if (data.tag === "Ground")` works.
Anything else a script defines at top level is reachable from another script through
`getComponent("ScriptComponent").invoke(name, ...args)`, and from C# through
`ScriptComponent.Invoke`.

The load waits until the actor is in a scene: components awake when attached, which is before
the actor joins a scene, so `onAwake` fires from `Start` and can use `Scene.*`. A component added
to an actor already in the scene loads at once.

## Attaching a script

### The `ScriptPath` ordering problem

`ScriptComponent.Awake()` reads the file and builds the Jint runtime. `Awake`
runs **inside `AddComponent`**, before you can assign `ScriptPath`:

```csharp
var script = actor.AddComponent<ScriptComponent>();   // Awake runs here, path is ""
script.ScriptPath = "Scripts/Enemy.js";               // too late — Runtime is null
```

`ScriptComponent.Reload()` is `internal`, so game code cannot call it. The
working fix is to re-run `Awake`, which is `public override`:

```csharp
var script = actor.AddComponent<ScriptComponent>();
script.ScriptPath = "Scripts/Enemy.js";
script.Awake();          // reads the file, builds the runtime, fires onAwake
```

`onStart` still fires normally when the layer flushes the actor. Wrap it once:

```csharp
public static class ActorScriptExtensions
{
    public static ScriptComponent AddScript(this Actor actor, string scriptPath)
    {
        var sc = actor.AddComponent<ScriptComponent>();
        sc.ScriptPath = scriptPath;
        sc.Awake();
        return sc;
    }
}
```

```csharp
var enemy = new Actor("Enemy") { Tag = "Enemy" };
enemy.Transform.Position = new Vector2(300, 200);
enemy.AddScript("Scripts/EnemyAI.js");
scene.AddActor(enemy);
```

`ScriptPath` is resolved by `File.ReadAllText`, so it is **relative to the
process working directory** — the same rule as [assets](13-assets.md#paths).
Copy `Scripts/` to the output directory in your `.csproj`.

### Error handling and diagnostics

A JavaScript error in any hook is caught, reported and the frame continues; a parse error or a
missing file leaves `Runtime` null and sets `ScriptComponent.Error`. Everything a script says —
`log()`, `warn()`, `error()` and the runtime's own errors — goes through one sink:

```csharp
ScriptDiagnostics.Reported += d => Console.WriteLine(d);   // level, script path, hook, message

var seen = new List<ScriptDiagnostic>();
using (ScriptDiagnostics.Capture(seen)) scene.Update(dt);  // a test reads what the scripts said
```

With no subscriber the messages go to the process console, so a shipped Release game still
reports a failing script (they used to go through `Debug.WriteLine`, which Release drops). The
editor subscribes and shows them in its console with their own level.

## Sandbox limits

`JintRuntime` configures Jint with:

```csharp
options.CatchClrExceptions();     // CLR exceptions surface as JS exceptions
options.MaxStatements(100_000);   // per Invoke() call
options.LimitRecursion(512);
```

and **no `AllowClr()`** — scripts cannot reach arbitrary .NET types. Everything
they can touch goes through the bridge.

Exceeding the statement limit throws `ExecutionCanceledException`, which
`ScriptComponent` catches and logs as
`"Execution cancelled — statement limit exceeded (100 000)."` An infinite loop
in a script costs you one frame, not the process.

100 000 statements is per `onUpdate` call, which is generous — but a script that
loops over thousands of actors every frame can hit it. Prefer to do heavy work
in C#.

---

## Hot reload

```csharp
private ScriptHotReload? _hotReload;

protected override void OnEngineReady()
{
    if (Config.HotReload)
        _hotReload = new ScriptHotReload("Scripts");
}

protected override void Update(GameTime gameTime)
{
    base.Update(gameTime);
    _hotReload?.Update();        // required — nothing calls this for you
}

protected override void UnloadContent()
{
    _hotReload?.Dispose();
    base.UnloadContent();
}
```

Register each component you want watched:

```csharp
var sc = actor.AddScript("Scripts/EnemyAI.js");
_hotReload?.Register(sc);
// _hotReload?.Unregister(sc);   on teardown
```

A `FileSystemWatcher` queues changed paths; `Update()` matches them against
registered components by full path and calls `Reload()`, which rebuilds the Jint
engine from the new source and re-fires `onStart`.

**Module-level state is lost on reload** — the new engine starts fresh. Keep
authoritative state on C# components, or re-derive it in `onStart`.

In `Release` builds `ScriptHotReload` compiles to no-op stubs (`#if DEBUG ||
DEVELOPMENT`), so leaving these calls in shipping code is free.

---

## Editor IntelliSense

```csharp
TypeScriptDefinitions.WriteToFile("Scripts/sb-engine.d.ts");
```

Then in `Scripts/jsconfig.json`:

```json
{
  "compilerOptions": { "checkJs": true, "target": "es2017" },
  "include": ["**/*.js", "sb-engine.d.ts"]
}
```

VS Code then gives full completion and hover types for `actor`, `transform`,
`Input`, `Audio`, `Scene`, `Debug` and `Vector2`, with no build step.

---

## The bundled templates run on both engines

The 15 project templates in `Templates/` are the reference scripts for this contract. Two
tests run every one of them for sixty frames and fail on the first script error:
`ProjectTemplateTests.EveryTemplateSceneRunsItsScriptsWithoutErrors` under Jint and
`html5/tests/templates.test.js` in the browser engine. Before the contract existed they used a
wider browser-only surface and had never run natively; `log()`, `actor.getComponent`,
`Input.isKeyHeld`, `Scene.createActor` and the Stay/Exit hooks are all part of the contract now.

Two habits from the old scripts are worth unlearning:

- `Scene.findByTag(t)` returns an array; use `Scene.findFirstByTag(t)` for one actor.
- Putting a function on an actor proxy (`actor.takeDamage = ...`) and calling it from another
  script through `other.takeDamage` does nothing on either engine: proxies are fresh objects.
  Define `function takeDamage(amount)` at top level and call
  `other.getComponent("ScriptComponent").invoke("takeDamage", amount)`.

## Extending the bridge

Add to both sides or neither: the member in `ScriptBridge.cs`, the same member in
`html5/src/scripting/ScriptBridge.js`, and its entry in `html5/src/scripting/bridge-api.json`.
The parity tests fail until all three agree, and the `.d.ts` in `TypeScriptDefinitions.cs` and
this page should follow.

On the C# side each proxy is a plain JS object with delegates hung off it:

```csharp
// A callable: body(thisObject, arguments)
obj.Set("shout", Fn("shout", (_, args) => { Console.WriteLine(args.At(0)); return JsValue.Undefined; }, length: 1));

// A property with a getter and an optional setter
Accessor(obj, "hp", getter: (_, _) => new JsNumber(_hp), setter: (_, args) => { _hp = (float)args.At(0).AsNumber(); return JsValue.Undefined; }, _engine);
```

`Fn` builds a `Jint.Runtime.Interop.ClrFunction`, which is what makes `arguments` an array of
the call's arguments. `JsValue.FromObject(engine, delegate)` looks similar but maps each
JavaScript argument onto a delegate parameter in turn, so the first argument lands in
`thisObject` and `arguments` is null — the bug that kept every bridge call with an argument from
working before the contract existed.

For a one-off value a game wants to hand a script, `JintRuntime.SetGlobal(name, value)` is the
lightweight alternative: it exposes a C# value or delegate as a global without touching the
bridge (and without appearing in the contract, so the browser will not have it).

## When to use script and when to use C#

| Use JavaScript for | Use C# for |
|---|---|
| Per-enemy behaviour tweaks | Physics, rendering, networking |
| Dialogue, quests, level triggers | Anything in a tight per-frame loop over many objects |
| Designer-facing tuning | Anything needing components, prefabs, or the asset system |
| Rapid iteration with hot reload | Shipping-critical logic that must not silently no-op |

A good split: C# components own state and expose a couple of `SetGlobal`
callbacks; the script decides *when* to call them.

---

## Next

- [12. Scenes & Prefabs](12-scenes-prefabs.md)
- [29. MakeChibi](29-makechibi.md) — the `Chibi` global, and what a namespace addition looks like.
- [Tutorial 5: JavaScript Scripting](../tutorials/05-javascript-scripting.md)
