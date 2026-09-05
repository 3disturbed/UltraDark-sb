# Tutorial 5 — JavaScript Scripting

**You will build:** enemy behaviour in hot-reloadable `.js`, an extension helper
that makes attaching scripts actually work, and a widened script API.
**Time:** ~30 minutes.

Builds on [Tutorial 4](04-components.md).

---

## 1. What the script API actually is

Each `ScriptComponent` gets its own isolated Jint engine. There is no `require`,
no `import`, and no shared globals between scripts — one file, one actor.

Seven globals are registered. **This is the complete list.**

```js
actor.name        // string  (get/set)
actor.tag         // string  (get/set)
actor.active      // boolean (get/set)
actor.destroy()

transform.x          // number, world X (get/set)
transform.y          // number, world Y (get/set)
transform.rotation   // number, radians (get/set)
transform.scaleX     // number (get/set)
transform.scaleY     // number (get/set)
transform.lookAt(x, y)
transform.distanceTo({ x, y })        // number

Input.isPressed(action)   Input.isHeld(action)   Input.isReleased(action)
Input.getAxis(action)     Input.mouseX           Input.mouseY

Audio.play(path)          // → { id }
Audio.playOneShot(path)
Audio.stop(handle)

Scene.find(name)          // ActorProxy | null
Scene.findByTag(tag)      // ActorProxy[]  ← an ARRAY
Scene.instantiate(path, x, y)
Scene.load(scenePath)

Debug.log(...)   Debug.warn(...)   Debug.error(...)

Vector2.create(x, y)   .add(a,b)   .sub(a,b)   .scale(v,s)
       .normalize(v)   .dot(a,b)   .distance(a,b)   .length(v)
```

Plus everything standard: `Math`, `JSON`, `Array`, `String`.

**Two traps worth memorising now:**

- `actor` has **no** `transform` — they are separate globals. `actor.transform.x`
  is `undefined` → `NaN`. (Actor *proxies* from `Scene.find` do have
  `.transform`.)
- `Scene.findByTag` returns an **array**, which is never falsy.
  `if (!target) return;` never fires.

The bundled project templates in `Templates/` were written against a wider API
than this — `log()`, `Input.isKeyPressed`, `actor.getComponent`,
`Scene.createActor`, `Network.*`. None of those exist. Treat the templates as
scene-layout references, not runnable script.

## 2. Attaching a script — the ordering problem

`ScriptComponent.Awake()` reads the file and builds the runtime, and `Awake` runs
**inside `AddComponent`** — before you can set `ScriptPath`:

```csharp
var sc = actor.AddComponent<ScriptComponent>();   // Awake ran; path was ""
sc.ScriptPath = "Scripts/Enemy.js";               // too late; Runtime is null
```

`ScriptComponent.Reload()` is `internal`, so game code cannot call it. But
`Awake` is `public override`, so re-running it is legal and correct.

Create `MyGame/ScriptExtensions.cs`:

```csharp
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scripting;

namespace MyGame;

public static class ScriptExtensions
{
    /// <summary>
    /// Attaches a JavaScript file to the actor and initialises its runtime.
    /// Re-runs Awake because ScriptComponent reads ScriptPath there, and Awake
    /// fires during AddComponent before the path can be assigned.
    /// </summary>
    public static ScriptComponent AddScript(this Actor actor, string scriptPath)
    {
        var sc = actor.AddComponent<ScriptComponent>();
        sc.ScriptPath = scriptPath;
        sc.Awake();
        return sc;
    }
}
```

`onStart` still fires normally when the layer flushes the actor, so the lifecycle
comes out right.

`ScriptPath` is read with `File.ReadAllText`, so it is relative to the **working
directory**. Tutorial 1's `.csproj` already copies `Scripts/**/*.js` to output.

## 3. Your first script

`MyGame/Scripts/Wander.js`:

```js
// Wander.js — drifts in a random direction, changing every few seconds.

var speed      = 60;
var dirX       = 1;
var dirY       = 0;
var changeIn   = 0;

function onStart() {
    Debug.log(actor.name + " spawned at " + transform.x.toFixed(0) + "," + transform.y.toFixed(0));
    pickDirection();
}

function onUpdate(dt) {
    changeIn -= dt;
    if (changeIn <= 0) pickDirection();

    transform.x += dirX * speed * dt;
    transform.y += dirY * speed * dt;
}

function pickDirection() {
    var angle = Math.random() * Math.PI * 2;
    dirX = Math.cos(angle);
    dirY = Math.sin(angle);
    changeIn = 1.5 + Math.random() * 2.0;
}
```

Attach it:

```csharp
var enemy = CreateBox("Wanderer", new Vector2(500, 400), new Vector2(28, 28),
                      new Color(230, 140, 90));
enemy.Tag = "Enemy";
enemy.AddScript("Scripts/Wander.js");
scene.AddActor(enemy);
```

```bash
dotnet run --project MyGame
```

## 4. Lifecycle hooks

Only these eight names are scanned and dispatched:

| Function | When |
|---|---|
| `onAwake()` | at attach |
| `onStart()` | first update after the actor joins a layer |
| `onUpdate(dt)` | every frame |
| `onFixedUpdate(dt)` | every fixed step |
| `onLateUpdate(dt)` | every frame, after updates |
| `onDestroy()` | on destruction |
| `onCollisionEnter(data)` | physics contact begins |
| `onTriggerEnter(other)` | sensor overlap begins |

There is **no** `onTriggerExit`, `onCollisionExit`, `onCollisionStay` or
`onTriggerStay` in script. Section 8 shows how to forward those from C#.

```js
function onCollisionEnter(data) {
    // data = { other, contactPoint:{x,y}, normal:{x,y}, relativeVelocity }
    if (data.other.tag === "Wall" && data.relativeVelocity > 5) {
        Audio.playOneShot("Assets/Audio/thud.wav");
    }
}

function onTriggerEnter(other) {
    if (other.tag === "Player") Debug.log("spotted " + other.name);
}
```

## 5. A chaser — using `Scene` correctly

`MyGame/Scripts/Chase.js`:

```js
// Chase.js — moves toward the nearest Player, with an aggro radius.

var speed        = 90;
var aggroRadius  = 260;
var target       = null;
var retargetIn   = 0;

function onStart() { findTarget(); }

function onUpdate(dt) {
    retargetIn -= dt;
    if (retargetIn <= 0 || target === null) findTarget();
    if (target === null) return;

    var dx = target.transform.x - transform.x;
    var dy = target.transform.y - transform.y;
    var dist = Math.sqrt(dx * dx + dy * dy);

    if (dist > aggroRadius || dist < 1) return;

    transform.x += (dx / dist) * speed * dt;
    transform.y += (dy / dist) * speed * dt;
    transform.lookAt(target.transform.x, target.transform.y);
}

function findTarget() {
    retargetIn = 0.5;

    // findByTag returns an ARRAY. An empty array is truthy, so check length.
    var candidates = Scene.findByTag("Player");
    if (candidates.length === 0) { target = null; return; }

    target = candidates[0];
}
```

Re-finding the target twice a second rather than every frame keeps the cost
sane. Scripts run inside the frame budget like everything else.

## 6. Hot reload

Edit a script while the game runs and see the change immediately.

```csharp
using SexyBiscuit.Engine.Scripting;

public class Game : SBEngine
{
    private ScriptHotReload? _hotReload;

    protected override void OnEngineReady()
    {
        if (Config.HotReload)
            _hotReload = new ScriptHotReload("Scripts");

        BuildScene();
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        _hotReload?.Update();          // required — nothing calls this for you
        // … the rest of Tutorial 1's Update …
    }

    protected override void UnloadContent()
    {
        _hotReload?.Dispose();
        base.UnloadContent();
    }
}
```

Register each script you want watched. Extend the helper:

```csharp
public static ScriptComponent AddScript(this Actor actor, string scriptPath,
                                        ScriptHotReload? watcher = null)
{
    var sc = actor.AddComponent<ScriptComponent>();
    sc.ScriptPath = scriptPath;
    sc.Awake();
    watcher?.Register(sc);
    return sc;
}
```

```csharp
enemy.AddScript("Scripts/Chase.js", _hotReload);
```

Saving the file rebuilds the Jint engine and re-fires `onStart`. **Module-level
state is lost** — the new engine starts fresh — so keep authoritative state in
C# and re-derive script state in `onStart`.

In `Release` builds `ScriptHotReload` compiles to no-op stubs, so leaving these
calls in shipping code costs nothing.

## 7. Editor IntelliSense

```csharp
#if DEBUG
SexyBiscuit.Engine.Scripting.TypeScriptDefinitions.WriteToFile("Scripts/sb-engine.d.ts");
#endif
```

`MyGame/Scripts/jsconfig.json`:

```json
{
  "compilerOptions": { "checkJs": true, "target": "es2017" },
  "include": ["**/*.js", "sb-engine.d.ts"]
}
```

VS Code now completes `transform.`, `Input.`, `Scene.` and the rest, with hover
docs and parameter hints. Worth the two minutes: it makes the "does this method
exist?" question disappear.

## 8. Bridging C# and script

### Push values into a script with `SetGlobal`

```csharp
var sc = enemy.AddScript("Scripts/Boss.js");
sc.Runtime?.SetGlobal("config", new { Hp = 500, Phases = 3, Name = "Crumbler" });
sc.Runtime?.SetGlobal("dealDamage", (Action<int>)(dmg => player.GetComponent<Health>()?.Damage(dmg)));
```

```js
function onStart() {
    Debug.log("Boss " + config.Name + " with " + config.Hp + " hp");
}

function onUpdate(dt) {
    if (shouldStrike()) dealDamage(12);
}
```

Call `SetGlobal` **after** `Awake()` has built the runtime, and re-apply it after
a hot reload.

### Call script functions from C#

```csharp
var sc = enemy.GetComponent<ScriptComponent>();
sc?.Runtime?.CallFunction("onPhaseChange", 2);
```

`CallFunction` early-outs on names outside the eight canonical hooks, because
`HasFunction` only caches those. For custom names, check first:

```csharp
var runtime = sc?.Runtime;
if (runtime?.Evaluate("typeof onPhaseChange === 'function'")?.AsBoolean() == true)
    runtime.CallFunction("onPhaseChange", 2);
```

### Forwarding the missing hooks

```csharp
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Scripting;

namespace MyGame.Components;

/// <summary>Forwards the trigger/collision callbacks ScriptComponent does not dispatch.</summary>
public sealed class ScriptCallbackBridge : Component
{
    private ScriptComponent? _script;

    public override void Start() => _script = GetComponent<ScriptComponent>();

    public override void OnTriggerExit(Actor other)     => Call("onTriggerExitCustom", other.Name);
    public override void OnCollisionExit(CollisionData d) => Call("onCollisionExitCustom", d.Other.Name);

    private void Call(string fn, params object[] args)
    {
        var rt = _script?.Runtime;
        if (rt == null) return;
        if (rt.Evaluate($"typeof {fn} === 'function'")?.AsBoolean() != true) return;
        rt.CallFunction(fn, args);
    }
}
```

## 9. Extending the bridge

The permanent fix for a missing API is to widen `ScriptBridge`. Two helpers do
all the work:

```csharp
private JsValue Fn(string name, Func<JsValue, JsValue[], JsValue> body, int length = 0);
private static void Accessor(ObjectInstance target, string name, getter, setter, engine);
```

To add `Input.isKeyPressed`, edit `BuildInputProxy` in
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

To add a whole `Physics` global:

```csharp
public ObjectInstance PhysicsProxy { get; }        // add to the property list
// … and in the constructor: PhysicsProxy = BuildPhysicsProxy();

private ObjectInstance BuildPhysicsProxy()
{
    var obj = NewObj();

    obj.Set("setVelocity", Fn("setVelocity", (_, args) =>
    {
        var rb = _actor.GetComponent<Rigidbody2D>();
        if (rb != null)
            rb.LinearVelocity = new Vector2((float)args.At(0).AsNumber(),
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

Register it in `JintRuntime.RegisterGlobals`:

```csharp
_engine.SetValue("Physics", Bridge.PhysicsProxy);
```

Then add the same declarations to `TypeScriptDefinitions.DtsContent` so
IntelliSense stays honest, and — if you added a lifecycle hook — to
`RefreshFunctionCache`'s `knownEntryPoints`, or it will never be dispatched.

## 10. Errors and limits

Every call is wrapped in try/catch. A parse error, a runtime exception or a CLR
exception is logged as:

```
[Script Error] Scripts/Chase.js (onUpdate): <message>
```

…and the game continues. A file that fails to parse leaves `Runtime == null` and
the component silently does nothing.

`Debug.WriteLine` is compiled out in `Release`, so add a trace listener if you
want script errors in a shipping build:

```csharp
System.Diagnostics.Trace.Listeners.Add(new ConsoleTraceListener());
```

Sandbox limits, set in `JintRuntime`:

- **No CLR access** — scripts cannot reach arbitrary .NET types.
- **100 000 statements per `Invoke`** — an infinite loop costs one frame and
  logs `Execution cancelled — statement limit exceeded`, rather than hanging.
- **Recursion limited to 512.**

## 11. When to use script

| JavaScript | C# |
|---|---|
| per-enemy behaviour tuning | physics, rendering, networking |
| dialogue, quests, level triggers | tight per-frame loops over many objects |
| designer-facing knobs | anything needing components or the asset system |
| rapid iteration with hot reload | shipping-critical logic that must not silently no-op |

A good split: C# components own state and expose a couple of `SetGlobal`
callbacks; the script decides *when* to call them.

---

## Checkpoint

You have:

- `AddScript`, the extension that makes attachment work
- Two working scripts, and the correct `Scene.findByTag` idiom
- Hot reload wired in
- IntelliSense via the generated `.d.ts`
- Both ways to extend the API: `SetGlobal` and `ScriptBridge`

## Exercises

1. Write `Patrol.js` that walks between two points stored as module-level vars.
2. Add `Input.isKeyHeld` to the bridge and update the `.d.ts`.
3. Use `SetGlobal` to give a script a `spawnBullet(x, y, angle)` callback.

---

**Next:** [Tutorial 6 — A Physics Platformer](06-physics-platformer.md)
