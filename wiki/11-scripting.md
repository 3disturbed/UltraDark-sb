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

## The API that actually exists

This is the complete set of globals registered by
`JintRuntime.RegisterGlobals()`. It is the source of truth; the `.d.ts` file
produced by `TypeScriptDefinitions.Generate()` matches it exactly.

### `actor` — the actor owning this script

```js
actor.name      // string, get/set
actor.tag       // string, get/set
actor.active    // boolean, get/set
actor.destroy() // queue removal at end of frame
```

> `actor` has **no `transform` property**. The transform is a separate global.

### `transform` — this actor's Transform

```js
transform.x           // number, world X   (get/set)
transform.y           // number, world Y   (get/set)
transform.rotation    // number, radians   (get/set)
transform.scaleX      // number           (get/set)
transform.scaleY      // number           (get/set)
transform.lookAt(x, y);
transform.distanceTo({ x: 10, y: 20 });   // number
```

### `Input`

```js
Input.isPressed("Jump");     // boolean — went down this frame
Input.isHeld("MoveX");       // boolean — active now
Input.isReleased("Attack");  // boolean — came up this frame
Input.getAxis("MoveX");      // number, −1..1
Input.mouseX;                // number, read-only, screen pixels
Input.mouseY;                // number, read-only
```

Action names come from the [action map](07-input.md#action-maps).

### `Audio`

```js
var h = Audio.play("Assets/Audio/theme.ogg");   // returns { id: number }
Audio.playOneShot("Assets/Audio/hit.wav");
Audio.stop(h);                                  // accepts the handle object or a raw id
```

`Audio.play` from script never loops and always uses the default bus.

### `Scene`

```js
var boss = Scene.find("Boss");            // ActorProxy | null
var coins = Scene.findByTag("Coin");      // ActorProxy[]  — always an array
var a = Scene.instantiate("Prefabs/Bullet.prefab", 100, 200);   // ActorProxy
Scene.load("Scenes/Level2");
```

An **ActorProxy** is a lightweight wrapper — a different shape from the `actor`
global:

```js
proxy.name          // string, get/set
proxy.tag           // string, get/set
proxy.active        // boolean, get/set
proxy.transform.x   // number, get/set
proxy.transform.y   // number, get/set
proxy.destroy()
```

> `Scene.instantiate` **ignores the prefab file**. It creates a bare `Actor`
> named after the file's base name at `(x, y)` and adds it to the active scene —
> no components. Use it as "spawn an empty actor at a position"; do real prefab
> instantiation from C# with `Prefab.Instantiate`.

> `Scene.load` calls `SceneManager.LoadScene`, which
> [creates an empty scene](02-core-architecture.md#loadscene-does-not-read-the-file--verified)
> rather than reading the file.

### `Debug`

```js
Debug.log("hello", 42);
Debug.warn("careful");
Debug.error("broken");
```

Output goes to `Console.WriteLine` and `System.Diagnostics.Debug.WriteLine`,
prefixed with `[Script]`, `[Script WARN]`, `[Script ERROR]`. In the editor this
lands in the Console panel via the trace listener.

### `Vector2`

Value-object helpers. Vectors are plain `{x, y}` objects.

```js
var a = Vector2.create(3, 4);
var b = Vector2.create(1, 0);

Vector2.add(a, b);          // {x, y}
Vector2.sub(a, b);
Vector2.scale(a, 2);
Vector2.normalize(a);       // {0,0} for a zero-length input
Vector2.dot(a, b);          // number
Vector2.distance(a, b);     // number
Vector2.length(a);          // number
```

Standard JavaScript built-ins (`Math`, `JSON`, `Array`, `String`, …) are all
available — this is a real ECMAScript engine.

---

## Lifecycle hooks

Define any of these as top-level functions. Only these eight names are scanned
and cached; anything else is never called by the engine.

| Function | When |
|---|---|
| `onAwake()` | when the component attaches (immediately on `AddComponent`) |
| `onStart()` | on the first update after the actor is added to a layer |
| `onUpdate(dt)` | every frame |
| `onFixedUpdate(dt)` | every fixed step |
| `onLateUpdate(dt)` | every frame, after all updates |
| `onDestroy()` | when the component or actor is destroyed |
| `onCollisionEnter(data)` | on physics contact begin |
| `onTriggerEnter(other)` | on sensor overlap begin |

```js
var speed = 200;
var elapsed = 0;

function onStart() {
    Debug.log("spawned at", transform.x, transform.y);
}

function onUpdate(dt) {
    elapsed += dt;
    transform.x += Input.getAxis("MoveX") * speed * dt;
    transform.y -= Input.getAxis("MoveY") * speed * dt;

    if (Input.isPressed("Attack")) {
        Audio.playOneShot("Assets/Audio/swing.wav");
    }
}

function onCollisionEnter(data) {
    // data = { other, contactPoint: {x,y}, normal: {x,y}, relativeVelocity }
    if (data.other.tag === "Hazard") {
        Debug.warn("ouch, impact " + data.relativeVelocity);
        actor.destroy();
    }
}

function onTriggerEnter(other) {
    if (other.tag === "Coin") other.destroy();
}
```

### Hooks that do **not** exist

`onCollisionStay`, `onCollisionExit`, `onTriggerStay` and `onTriggerExit` are
**never dispatched to scripts**, even though the underlying `Component` supports
them. `ScriptComponent` overrides only `OnCollisionEnter` and `OnTriggerEnter`.

To get exit events in script, subclass `ScriptComponent`… you can't — it is
`sealed`. Write a small C# component that forwards instead:

```csharp
public sealed class TriggerExitBridge : Component
{
    public override void OnTriggerExit(Actor other)
    {
        var script = GetComponent<ScriptComponent>();
        script?.Runtime?.CallFunction("onTriggerExitCustom", other.Name);
    }
}
```

```js
function onTriggerExitCustom(otherName) { /* ... */ }
```

`JintRuntime.HasFunction` only knows the eight canonical names, and
`CallFunction` early-outs on unknown ones — so route custom callbacks through
`Runtime.CallFunctionWithJsArgs` after checking existence yourself, or simply
call `Runtime.Evaluate("typeof onTriggerExitCustom === 'function'")`.

---

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

### Error handling

Every call is wrapped. A JS syntax error, a runtime exception, or a CLR
exception is caught, written to the diagnostic log as
`[Script Error] <path> (<function>): <message>`, and the game continues. A file
that fails to parse leaves `Runtime == null` and the component silently does
nothing — check the log if a script "isn't running".

---

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

## What the bundled templates assume — and what breaks

The 15 project templates in `Templates/` were written against a **larger API
than the bridge implements**. If you copy a template script, expect these calls
to be `undefined` and to throw a `TypeError` at runtime:

| Used in templates | Status | Use instead |
|---|---|---|
| `log(...)` | ✗ not a global | `Debug.log(...)` |
| `actor.transform.x` | ✗ `actor` has no `transform` | the `transform` global |
| `actor.getComponent("Rigidbody2D")` | ✗ not implemented | move physics to C#, or extend the bridge |
| `Input.isKeyPressed("E")` | ✗ not implemented | define an action; `Input.isPressed("Interact")` |
| `Input.isKeyHeld("A")` / `isKeyDown` | ✗ not implemented | `Input.isHeld("MoveX")` / `getAxis` |
| `Input.isMouseHeld` / `isMousePressed` | ✗ not implemented | bind a mouse action in the action map |
| `Input.scrollDelta` | ✗ not implemented | extend the bridge |
| `Scene.createActor(...)` | ✗ not implemented | `Scene.instantiate(name, x, y)` |
| `Scene.destroy(a)` / `destroyActor` | ✗ not implemented | `a.destroy()` |
| `Scene.addComponent(...)` | ✗ not implemented | add components in C# |
| `Scene.findAll(...)` | ✗ not implemented | `Scene.findByTag(...)` |
| `Scene.findByTag(t)` used as one actor | ⚠ returns an **array** | `Scene.findByTag(t)[0]` |
| `Network.*` | ✗ no network bridge | drive networking from C# |
| custom fields on `actor` (`actor.hp = 5`) | ⚠ writes a JS property on a fresh proxy object; not persisted to the C# actor | keep state in module-level `var`s |
| `onTriggerExit` | ✗ never dispatched | forward from a C# component |

`Templates/Top-Down RPG/Scripts/CameraFollow.js` shows two of these at once:

```js
// As shipped — does not work:
var target = Scene.findByTag("Player");        // an ARRAY, never falsy
if (!target) return;
var targetX = target.transform.x;              // undefined → NaN
```

```js
// Corrected:
var found = Scene.findByTag("Player");
if (found.length === 0) return;
var target = found[0];
var targetX = target.transform.x + offsetX;
```

Treat the templates as **scene-layout references**, and write scripts against
the table at the top of this page.

---

## Extending the bridge

The clean way to add missing API is to widen `ScriptBridge`. It builds each
proxy as a plain JS object and hangs C# delegates off it.

Two helpers do all the work:

```csharp
// A callable JS function from a C# lambda:
private JsValue Fn(string name, Func<JsValue, JsValue[], JsValue> body, int length = 0)
    => JsValue.FromObject(_engine, (Delegate)body);

// A get/set accessor property:
private static void Accessor(ObjectInstance target, string name,
    Func<JsValue, JsValue[], JsValue> getter,
    Func<JsValue, JsValue[], JsValue>? setter,
    JintEngine engine);
```

To add `Input.isKeyPressed(name)`, edit `BuildInputProxy` in
`SexyBiscuit.Engine/Scripting/ScriptBridge.cs`:

```csharp
obj.Set("isKeyPressed", Fn("isKeyPressed", (_, args) =>
{
    var input = GetInput();
    if (input == null) return JsBoolean.False;
    return Enum.TryParse<Keys>(args.At(0).ToString(), ignoreCase: true, out var key)
           && input.IsKeyPressed(key)
        ? JsBoolean.True : JsBoolean.False;
}, length: 1));
```

To add a `Physics` global, build a proxy and register it:

```csharp
// in ScriptBridge
public ObjectInstance PhysicsProxy { get; }

private ObjectInstance BuildPhysicsProxy()
{
    var obj = NewObj();

    obj.Set("setVelocity", Fn("setVelocity", (_, args) =>
    {
        var rb = _actor.GetComponent<Rigidbody2D>();
        if (rb != null)
            rb.LinearVelocity = new Vector2(
                (float)args.At(0).AsNumber(),
                (float)args.At(1).AsNumber());
        return JsValue.Undefined;
    }, length: 2));

    obj.Set("raycast", Fn("raycast", (_, args) =>
    {
        var origin = new Vector2((float)args.At(0).AsNumber(), (float)args.At(1).AsNumber());
        var dir    = new Vector2((float)args.At(2).AsNumber(), (float)args.At(3).AsNumber());
        float dist = (float)args.At(4).AsNumber();

        if (!PhysicsSystem2D.Instance.Raycast(origin, dir, dist, out var hit) || hit.Actor == null)
            return JsValue.Null;

        var result = NewObj();
        result.Set("actor",    WrapActorAsProxy(hit.Actor));
        result.Set("distance", new JsNumber(hit.Distance));
        return result;
    }, length: 5));

    return obj;
}
```

```csharp
// in JintRuntime.RegisterGlobals()
_engine.SetValue("Physics", Bridge.PhysicsProxy);
```

Then add the same names to `TypeScriptDefinitions.DtsContent` and to
`RefreshFunctionCache`'s `knownEntryPoints` if you added a lifecycle hook, so
IntelliSense and dispatch stay in sync.

### The lightweight alternative: `SetGlobal`

If you only need to expose one value or callback to one script, you do not have
to touch the bridge:

```csharp
var sc = actor.AddScript("Scripts/Boss.js");
sc.Runtime?.SetGlobal("bossConfig", new { Hp = 500, Phases = 3 });
sc.Runtime?.SetGlobal("dealDamage", (Action<int>)(dmg => player.Hp -= dmg));
```

```js
function onStart() {
    Debug.log("boss hp " + bossConfig.Hp);
}
function onUpdate(dt) {
    if (someCondition) dealDamage(10);
}
```

`SetGlobal` uses `_engine.SetValue`, which marshals CLR objects through Jint's
interop — the script gets a real wrapper, not a hand-built proxy. Do this after
`Awake()` has built the runtime, and re-apply after a hot reload.

---

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
- [Tutorial 5: JavaScript Scripting](../tutorials/05-javascript-scripting.md)
