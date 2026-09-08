using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Audio;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.DarksGames;
using SexyBiscuit.Engine.Input;
using SexyBiscuit.Engine.Networking;
using SexyBiscuit.Engine.Physics;
using System.Text.Json.Nodes;

using JintEngine = Jint.Engine;

using SexyBiscuit.Engine.UI;

namespace SexyBiscuit.Engine.Scripting;

/// <summary>
/// Constructs and owns all JavaScript proxy objects that expose the C# engine API
/// as clean, ergonomic globals inside a Jint script environment.
///
/// Each proxy is a native Jint <see cref="ObjectInstance"/> populated with
/// C# delegates wrapped as <see cref="ClrFunction"/> values and
/// get/set accessor properties backed by <see cref="GetSetPropertyDescriptor"/>.
///
/// One <see cref="ScriptBridge"/> is created per <see cref="ScriptComponent"/>
/// and reused across hot-reloads — only the JS source changes, not the bridge.
/// </summary>
/// <remarks>
/// The surface here is the scripting contract shared with the HTML5 runtime
/// (<c>html5/src/scripting/ScriptBridge.js</c>), pinned member for member by
/// <c>html5/src/scripting/bridge-api.json</c> and a test on each side. A script that runs in
/// the browser runs here unchanged; add to both sides or neither.
/// </remarks>
public sealed class ScriptBridge
{
    // -------------------------------------------------------------------------
    // Owning references
    // -------------------------------------------------------------------------
    private readonly Actor      _actor;
    private readonly JintEngine _engine;

    // Proxies handed to scripts, mapped back to the actor they stand for so a script can pass
    // one to Scene.destroy or Scene.addComponent. Weak on the proxy: entries die with it.
    private readonly ConditionalWeakTable<ObjectInstance, Actor> _proxyToActor = new();

    // One proxy per component per bridge, so `rb === actor.getComponent("Rigidbody2D")` holds
    // across frames and a script can keep state on it.
    internal readonly ConditionalWeakTable<Component, ObjectInstance> ComponentProxies = new();

    private Transform3D?    _transform3D;
    private ObjectInstance? _transform3DProxy;

    /// <summary>The script the bridge serves, for diagnostics. Set by <see cref="ScriptComponent"/>.</summary>
    public string ScriptPath { get; set; } = string.Empty;

    // -------------------------------------------------------------------------
    // Exposed proxy objects — read by JintRuntime.RegisterGlobals()
    // -------------------------------------------------------------------------
    public ObjectInstance ActorProxy     { get; }
    public ObjectInstance TransformProxy { get; }
    public ObjectInstance InputProxy     { get; }
    public ObjectInstance AudioProxy     { get; }
    public ObjectInstance SceneProxy     { get; }
    public ObjectInstance DebugProxy     { get; }
    public ObjectInstance Vector2Proxy   { get; }
    public ObjectInstance PhysicsProxy   { get; }
    public ObjectInstance TimeProxy      { get; }
    public ObjectInstance NetworkProxy   { get; }
    public ObjectInstance DarksGamesProxy { get; }
    public ObjectInstance UiProxy        { get; }
    public ObjectInstance ChibiProxy     { get; }

    /// <summary>The engine every proxy allocates on. Used by <see cref="ComponentProxy"/>.</summary>
    internal JintEngine JsEngine => _engine;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds all proxy objects for <paramref name="actor"/> using the supplied
    /// <paramref name="engine"/> instance to allocate native Jint objects.
    /// </summary>
    public ScriptBridge(Actor actor, JintEngine engine)
    {
        _actor  = actor  ?? throw new ArgumentNullException(nameof(actor));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        TransformProxy = BuildTransformProxy();
        ActorProxy     = BuildActorProxy();
        InputProxy     = BuildInputProxy();
        AudioProxy     = BuildAudioProxy();
        SceneProxy     = BuildSceneProxy();
        DebugProxy     = BuildDebugProxy();
        Vector2Proxy   = BuildVector2Proxy();
        PhysicsProxy   = BuildPhysicsProxy();
        TimeProxy      = BuildTimeProxy();
        NetworkProxy   = BuildNetworkProxy();
        DarksGamesProxy = BuildDarksGamesProxy();
        UiProxy        = BuildUiProxy();
        ChibiProxy     = BuildChibiProxy();

        // The `actor` global is a proxy too: Scene.destroy(actor) must find its way back.
        _proxyToActor.Add(ActorProxy, _actor);
    }

    // =========================================================================
    // Helpers — allocate a fresh plain JS object and define accessor properties
    // =========================================================================

    /// <summary>Allocates a new, empty plain JS object on the engine's heap.</summary>
    private ObjectInstance NewObj() => JsValueConverter.NewObject(_engine);

    /// <summary>A JS array of <paramref name="items"/>.</summary>
    private JsValue NewArray(IEnumerable<JsValue> items) => JsValueConverter.NewArray(_engine, items);

    /// <summary>
    /// Wraps a C# delegate as a JS function that is called as <c>body(thisObject, arguments)</c>.
    /// </summary>
    /// <remarks>
    /// This has to be a <see cref="ClrFunction"/>. Jint's <see cref="DelegateWrapper"/> — what
    /// <c>JsValue.FromObject</c> makes of a delegate — maps the JavaScript arguments onto the
    /// delegate's parameters one by one, so the first argument landed in <c>thisObject</c> and
    /// <c>arguments</c> was null. Every bridge call that took an argument failed that way, which
    /// is one reason no bundled script had ever run natively.
    /// </remarks>
    private JsValue Fn(string name, Func<JsValue, JsValue[], JsValue> body, int length = 0)
        => new ClrFunction(_engine, name, body, length);

    /// <summary>
    /// Defines a JavaScript accessor property (get + optional set) on
    /// <paramref name="target"/> using Jint's <see cref="GetSetPropertyDescriptor"/>.
    /// </summary>
    internal static void Accessor(
        ObjectInstance target,
        string name,
        Func<JsValue, JsValue[], JsValue> getter,
        Func<JsValue, JsValue[], JsValue>? setter,
        JintEngine engine)
    {
        JsValue getterFn = new ClrFunction(engine, "get " + name, getter);
        JsValue setterFn = setter != null
            ? new ClrFunction(engine, "set " + name, setter, 1)
            : JsValue.Undefined;

        target.DefineOwnProperty(name,
            new GetSetPropertyDescriptor(getterFn, setterFn, enumerable: true, configurable: true));
    }

    private static JsValue Bool(bool value) => value ? JsBoolean.True : JsBoolean.False;

    private static float Num(JsValue value, float fallback = 0f)
        => value.IsNumber() ? (float)value.AsNumber() : fallback;

    private JsValue Vec2(double x, double y)
    {
        var v = NewObj();
        v.Set("x", new JsNumber(x));
        v.Set("y", new JsNumber(y));
        return v;
    }

    private static InputManager? GetInput() => EngineHost.Current?.Input;
    private static AudioManager? GetAudio() => EngineHost.Current?.Audio;

    /// <summary>
    /// The scene the actor lives in. The actor's own scene comes first: a script ticked in a
    /// test, a headless tool or a scene being previewed has no engine host, and the host's
    /// active scene is not necessarily the one the actor is in.
    /// </summary>
    private Core.Scene? GetScene() => _actor.Scene ?? EngineHost.Current?.SceneManager.ActiveScene;

    private void Warn(string message)
        => ScriptDiagnostics.Report(ScriptDiagnosticLevel.Warning, ScriptPath, null, message);

    // =========================================================================
    // actor proxy
    // =========================================================================

    private ObjectInstance BuildActorProxy()
    {
        var obj = NewObj();

        // actor.name (get/set)
        Accessor(obj, "name",
            getter: (_, _) => new JsString(_actor.Name),
            setter: (_, args) => { _actor.Name = args.At(0).ToString(); return JsValue.Undefined; },
            _engine);

        // actor.tag (get/set)
        Accessor(obj, "tag",
            getter: (_, _) => new JsString(_actor.Tag),
            setter: (_, args) => { _actor.Tag = args.At(0).ToString(); return JsValue.Undefined; },
            _engine);

        // actor.active (get/set)
        // `IsActive && !IsDestroyed`, which is what every internal check in the
        // engine already uses. See WrapActorAsProxy for why.
        Accessor(obj, "active",
            getter: (_, _) => Bool(_actor.IsActive && !_actor.IsDestroyed),
            setter: (_, args) => { _actor.IsActive = TypeConverter.ToBoolean(args.At(0)); return JsValue.Undefined; },
            _engine);

        // actor.id (read-only)
        Accessor(obj, "id", getter: (_, _) => new JsNumber(_actor.Id), setter: null, _engine);

        // actor.transform — the same object as the `transform` global; the bundled scripts
        // write `actor.transform.x` as often as `transform.x`.
        Accessor(obj, "transform", getter: (_, _) => TransformProxy, setter: null, _engine);

        // actor.transform3d — null until the actor has a Transform3D.
        Accessor(obj, "transform3d", getter: (_, _) => Transform3DValue(), setter: null, _engine);

        // actor.destroy()
        obj.Set("destroy", Fn("destroy", (_, _) =>
        {
            _actor.Destroy();
            return JsValue.Undefined;
        }));

        // actor.getComponent("Rigidbody2D") — a proxy over the component's properties, or null.
        obj.Set("getComponent", Fn("getComponent", (_, args) => GetComponentValue(_actor, args.At(0)), length: 1));

        // actor.addComponent("BoxCollider2D")
        obj.Set("addComponent", Fn("addComponent", (_, args) => AddComponentValue(_actor, args.At(0), JsValue.Undefined), length: 1));

        // ---- hierarchy -------------------------------------------------------

        // actor.parent — null at the scene root.
        Accessor(obj, "parent",
            getter: (_, _) => _actor.Parent is { } p ? WrapActorAsProxy(p) : JsValue.Null,
            setter: null, _engine);

        // actor.children — a fresh array each read, so a script cannot mutate the engine's list.
        Accessor(obj, "children",
            getter: (_, _) => NewArray(_actor.Children.Select(WrapActorAsProxy)),
            setter: null, _engine);

        // actor.attachTo(other, keep = true). `keep` defaults to true: the common case is
        // "hold this pickup where it is and make it follow the player", not "snap it to the
        // player's origin".
        obj.Set("attachTo", Fn("attachTo", (_, args) =>
        {
            var parent = args.At(0).IsNull() || args.At(0).IsUndefined() ? null : Unwrap(args.At(0));
            if (parent == null && !(args.At(0).IsNull() || args.At(0).IsUndefined()))
            {
                Warn("actor.attachTo: the first argument is not an actor.");
                return JsValue.Undefined;
            }

            bool keep = args.Length < 2 || args.At(1).IsUndefined() || TypeConverter.ToBoolean(args.At(1));
            try { _actor.AttachTo(parent, keep); }
            catch (InvalidOperationException ex) { Warn($"actor.attachTo: {ex.Message}"); }
            return JsValue.Undefined;
        }, length: 2));

        // actor.detach(keep = true)
        obj.Set("detach", Fn("detach", (_, args) =>
        {
            bool keep = args.Length < 1 || args.At(0).IsUndefined() || TypeConverter.ToBoolean(args.At(0));
            _actor.AttachTo(null, keep);
            return JsValue.Undefined;
        }, length: 1));

        // actor.findChild(name, recursive = false)
        obj.Set("findChild", Fn("findChild", (_, args) =>
        {
            var found = _actor.FindChild(args.At(0).ToString(), TypeConverter.ToBoolean(args.At(1)));
            return found != null ? WrapActorAsProxy(found) : JsValue.Null;
        }, length: 2));

        return obj;
    }

    // =========================================================================
    // transform proxy
    // =========================================================================

    private ObjectInstance BuildTransformProxy()
    {
        var t   = _actor.Transform;
        var obj = NewObj();

        // transform.x (get/set)
        Accessor(obj, "x",
            getter: (_, _) => new JsNumber(t.Position.X),
            setter: (_, args) => { t.Position = new Vector2(Num(args.At(0)), t.Position.Y); return JsValue.Undefined; },
            _engine);

        // transform.y (get/set)
        Accessor(obj, "y",
            getter: (_, _) => new JsNumber(t.Position.Y),
            setter: (_, args) => { t.Position = new Vector2(t.Position.X, Num(args.At(0))); return JsValue.Undefined; },
            _engine);

        // transform.rotation (get/set, radians)
        Accessor(obj, "rotation",
            getter: (_, _) => new JsNumber(t.Rotation),
            setter: (_, args) => { t.Rotation = Num(args.At(0)); return JsValue.Undefined; },
            _engine);

        // transform.scaleX (get/set)
        Accessor(obj, "scaleX",
            getter: (_, _) => new JsNumber(t.Scale.X),
            setter: (_, args) => { t.Scale = new Vector2(Num(args.At(0), 1f), t.Scale.Y); return JsValue.Undefined; },
            _engine);

        // transform.scaleY (get/set)
        Accessor(obj, "scaleY",
            getter: (_, _) => new JsNumber(t.Scale.Y),
            setter: (_, args) => { t.Scale = new Vector2(t.Scale.X, Num(args.At(0), 1f)); return JsValue.Undefined; },
            _engine);

        // transform.lookAt(x, y)
        obj.Set("lookAt", Fn("lookAt", (_, args) =>
        {
            t.LookAt(new Vector2(Num(args.At(0)), Num(args.At(1))));
            return JsValue.Undefined;
        }, length: 2));

        // transform.distanceTo(otherTransformProxy)
        // The "other" argument is expected to be a transform proxy object with x/y.
        obj.Set("distanceTo", Fn("distanceTo", (_, args) =>
        {
            if (args.At(0) is not ObjectInstance other) return new JsNumber(0);
            float ox = Num(other.Get("x"));
            float oy = Num(other.Get("y"));
            return new JsNumber(Vector2.Distance(t.Position, new Vector2(ox, oy)));
        }, length: 1));

        return obj;
    }

    // =========================================================================
    // transform3d proxy
    // =========================================================================

    /// <summary>The <c>transform3d</c> value: a proxy over the actor's <see cref="Transform3D"/>, or null without one.</summary>
    internal JsValue Transform3DValue()
    {
        var t = _actor.GetComponent<Transform3D>();
        if (t == null) return JsValue.Null;

        if (!ReferenceEquals(t, _transform3D) || _transform3DProxy == null)
        {
            _transform3D      = t;
            _transform3DProxy = BuildTransform3DProxy(t);
        }

        return _transform3DProxy;
    }

    private ObjectInstance BuildTransform3DProxy(Transform3D t)
    {
        var obj = NewObj();

        Accessor(obj, "x",
            getter: (_, _) => new JsNumber(t.Position.X),
            setter: (_, args) => { var p = t.Position; t.Position = new Vector3(Num(args.At(0)), p.Y, p.Z); return JsValue.Undefined; },
            _engine);
        Accessor(obj, "y",
            getter: (_, _) => new JsNumber(t.Position.Y),
            setter: (_, args) => { var p = t.Position; t.Position = new Vector3(p.X, Num(args.At(0)), p.Z); return JsValue.Undefined; },
            _engine);
        Accessor(obj, "z",
            getter: (_, _) => new JsNumber(t.Position.Z),
            setter: (_, args) => { var p = t.Position; t.Position = new Vector3(p.X, p.Y, Num(args.At(0))); return JsValue.Undefined; },
            _engine);

        Accessor(obj, "rotX",
            getter: (_, _) => new JsNumber(t.EulerAngles.X),
            setter: (_, args) => { var e = t.EulerAngles; t.EulerAngles = new Vector3(Num(args.At(0)), e.Y, e.Z); return JsValue.Undefined; },
            _engine);
        Accessor(obj, "rotY",
            getter: (_, _) => new JsNumber(t.EulerAngles.Y),
            setter: (_, args) => { var e = t.EulerAngles; t.EulerAngles = new Vector3(e.X, Num(args.At(0)), e.Z); return JsValue.Undefined; },
            _engine);
        Accessor(obj, "rotZ",
            getter: (_, _) => new JsNumber(t.EulerAngles.Z),
            setter: (_, args) => { var e = t.EulerAngles; t.EulerAngles = new Vector3(e.X, e.Y, Num(args.At(0))); return JsValue.Undefined; },
            _engine);

        // Scale, which the 2D transform has had all along and this one did not.
        // Without it a script can place a primitive but not size one, and every
        // mesh in the contract is a UNIT cube, sphere or cylinder -- so a world
        // built from script was a field of one-metre boxes and no way to make a
        // wall out of them.
        Accessor(obj, "scaleX",
            getter: (_, _) => new JsNumber(t.LocalScale.X),
            setter: (_, args) => { var v = t.LocalScale; t.LocalScale = new Vector3(Num(args.At(0)), v.Y, v.Z); return JsValue.Undefined; },
            _engine);
        Accessor(obj, "scaleY",
            getter: (_, _) => new JsNumber(t.LocalScale.Y),
            setter: (_, args) => { var v = t.LocalScale; t.LocalScale = new Vector3(v.X, Num(args.At(0)), v.Z); return JsValue.Undefined; },
            _engine);
        Accessor(obj, "scaleZ",
            getter: (_, _) => new JsNumber(t.LocalScale.Z),
            setter: (_, args) => { var v = t.LocalScale; t.LocalScale = new Vector3(v.X, v.Y, Num(args.At(0))); return JsValue.Undefined; },
            _engine);

        obj.Set("lookAt", Fn("lookAt", (_, args) =>
        {
            t.LookAt(new Vector3(Num(args.At(0)), Num(args.At(1)), Num(args.At(2))));
            return JsValue.Undefined;
        }, length: 3));

        // Everything at once, which is what building a world actually does. Three
        // separate setters each rebuild the world matrix; this rebuilds it once.
        obj.Set("set", Fn("set", (_, args) =>
        {
            t.Position   = new Vector3(Num(args.At(0)), Num(args.At(1)), Num(args.At(2)));
            t.LocalScale = new Vector3(Num(args.At(3), 1f), Num(args.At(4), 1f), Num(args.At(5), 1f));
            return JsValue.Undefined;
        }, length: 6));

        return obj;
    }

    // =========================================================================
    // Input proxy
    // =========================================================================

    private ObjectInstance BuildInputProxy()
    {
        var obj = NewObj();

        // ---- Actions ---------------------------------------------------------
        obj.Set("isPressed",  Fn("isPressed",  (_, args) => Bool(GetInput()?.IsPressed(args.At(0).ToString()) == true), length: 1));
        obj.Set("isHeld",     Fn("isHeld",     (_, args) => Bool(GetInput()?.IsHeld(args.At(0).ToString()) == true), length: 1));
        obj.Set("isReleased", Fn("isReleased", (_, args) => Bool(GetInput()?.IsReleased(args.At(0).ToString()) == true), length: 1));
        obj.Set("getAxis",    Fn("getAxis",    (_, args) => new JsNumber(GetInput()?.GetAxis(args.At(0).ToString()) ?? 0f), length: 1));

        // ---- Mouse position --------------------------------------------------
        Accessor(obj, "mouseX", getter: (_, _) => new JsNumber(GetInput()?.MousePosition.X ?? 0f), setter: null, _engine);
        Accessor(obj, "mouseY", getter: (_, _) => new JsNumber(GetInput()?.MousePosition.Y ?? 0f), setter: null, _engine);
        Accessor(obj, "mouseDeltaX", getter: (_, _) => new JsNumber(GetInput()?.MouseDelta.X ?? 0f), setter: null, _engine);
        Accessor(obj, "mouseDeltaY", getter: (_, _) => new JsNumber(GetInput()?.MouseDelta.Y ?? 0f), setter: null, _engine);
        Accessor(obj, "scrollDelta", getter: (_, _) => new JsNumber(GetInput()?.ScrollDelta ?? 0f), setter: null, _engine);

        // ---- Keys: "A", "a", "KeyA", "Space", "ArrowLeft" all name the same key ----
        static bool Key(JsValue name, Func<InputManager, Microsoft.Xna.Framework.Input.Keys, bool> query)
        {
            var input = GetInput();
            return input != null && KeyNames.TryParse(name.ToString(), out var key) && query(input, key);
        }

        obj.Set("isKeyDown",     Fn("isKeyDown",     (_, args) => Bool(Key(args.At(0), (i, k) => i.IsKeyDown(k))), length: 1));
        obj.Set("isKeyHeld",     Fn("isKeyHeld",     (_, args) => Bool(Key(args.At(0), (i, k) => i.IsKeyDown(k))), length: 1));
        obj.Set("isKeyPressed",  Fn("isKeyPressed",  (_, args) => Bool(Key(args.At(0), (i, k) => i.IsKeyPressed(k))), length: 1));
        obj.Set("isKeyReleased", Fn("isKeyReleased", (_, args) => Bool(Key(args.At(0), (i, k) => i.IsKeyReleased(k))), length: 1));

        // ---- Mouse buttons: 0 left, 1 middle, 2 right, or a name ---------------
        static bool Mouse(JsValue button, Func<InputManager, MouseButton, bool> query)
        {
            var input = GetInput();
            if (input == null) return false;

            MouseButton parsed;
            if (button.IsNumber())
            {
                int index = (int)button.AsNumber();
                if (!Enum.IsDefined(typeof(MouseButton), index)) return false;
                parsed = (MouseButton)index;
            }
            else if (!Enum.TryParse(button.ToString(), ignoreCase: true, out parsed))
            {
                return false;
            }

            return query(input, parsed);
        }

        obj.Set("isMouseDown",     Fn("isMouseDown",     (_, args) => Bool(Mouse(args.At(0), (i, b) => i.IsMouseButtonDown(b))), length: 1));
        obj.Set("isMouseHeld",     Fn("isMouseHeld",     (_, args) => Bool(Mouse(args.At(0), (i, b) => i.IsMouseButtonDown(b))), length: 1));
        obj.Set("isMousePressed",  Fn("isMousePressed",  (_, args) => Bool(Mouse(args.At(0), (i, b) => i.IsMouseButtonPressed(b))), length: 1));
        obj.Set("isMouseReleased", Fn("isMouseReleased", (_, args) => Bool(Mouse(args.At(0), (i, b) => i.IsMouseButtonReleased(b))), length: 1));

        // ---- Touch ---------------------------------------------------------------
        Accessor(obj, "touchCount", getter: (_, _) => new JsNumber(GetInput()?.Touch.Touches.Count ?? 0), setter: null, _engine);

        obj.Set("getTouch", Fn("getTouch", (_, args) =>
        {
            var touches = GetInput()?.Touch.Touches;
            int index   = (int)Num(args.At(0), -1f);
            if (touches == null || index < 0 || index >= touches.Count) return JsValue.Null;

            var touch = touches[index];
            var result = NewObj();
            result.Set("id",    new JsNumber(touch.Id));
            result.Set("x",     new JsNumber(touch.Position.X));
            result.Set("y",     new JsNumber(touch.Position.Y));
            result.Set("phase", new JsString(touch.Phase.ToString()));
            return result;
        }, length: 1));

        Accessor(obj, "joystickX", getter: (_, _) => new JsNumber(GetInput()?.Touch.LeftJoystick.Value.X ?? 0f), setter: null, _engine);
        Accessor(obj, "joystickY", getter: (_, _) => new JsNumber(GetInput()?.Touch.LeftJoystick.Value.Y ?? 0f), setter: null, _engine);

        return obj;
    }

    // =========================================================================
    // Audio proxy
    // =========================================================================

    private ObjectInstance BuildAudioProxy()
    {
        var obj = NewObj();

        // Audio.play(path, loop = false) — returns a handle object {id}; id is 0 when nothing played.
        obj.Set("play", Fn("play", (_, args) =>
        {
            var audio  = GetAudio();
            bool loop  = args.Length > 1 && TypeConverter.ToBoolean(args.At(1));
            uint id    = audio?.Play(args.At(0).ToString(), loop).Id ?? 0;

            var handleObj = NewObj();
            handleObj.Set("id", new JsNumber(id));
            return handleObj;
        }, length: 2));

        // Audio.playOneShot(path, volume = 1)
        obj.Set("playOneShot", Fn("playOneShot", (_, args) =>
        {
            float volume = args.Length > 1 ? Num(args.At(1), 1f) : 1f;
            GetAudio()?.PlayOneShot(args.At(0).ToString(), volume);
            return JsValue.Undefined;
        }, length: 2));

        // Audio.stop(handle) — the object returned by play(), or a raw numeric id.
        obj.Set("stop", Fn("stop", (_, args) =>
        {
            var audio = GetAudio();
            if (audio == null) return JsValue.Undefined;

            var handleVal = args.At(0);
            uint id = 0;

            if (handleVal is ObjectInstance h)
            {
                var idVal = h.Get("id");
                if (idVal.IsNumber()) id = (uint)idVal.AsNumber();
            }
            else if (handleVal.IsNumber())
            {
                id = (uint)handleVal.AsNumber();
            }

            if (id != 0) audio.Stop(new AudioHandle { Id = id });
            return JsValue.Undefined;
        }, length: 1));

        // Audio.setVolume(volume) — the master bus.
        obj.Set("setVolume", Fn("setVolume", (_, args) =>
        {
            var audio = GetAudio();
            if (audio != null) audio.Master.Volume = Num(args.At(0), 1f);
            return JsValue.Undefined;
        }, length: 1));

        return obj;
    }

    // =========================================================================
    // Scene proxy
    // =========================================================================

    private ObjectInstance BuildSceneProxy()
    {
        var obj = NewObj();

        // Scene.name
        Accessor(obj, "name", getter: (_, _) => new JsString(GetScene()?.Name ?? string.Empty), setter: null, _engine);

        // Scene.find(name)
        obj.Set("find", Fn("find", (_, args) =>
        {
            var actor = GetScene()?.FindByName(args.At(0).ToString());
            return actor != null ? WrapActorAsProxy(actor) : JsValue.Null;
        }, length: 1));

        // Scene.findAll(name) — every actor with that name, which the bundled scripts use for
        // a batch of identical enemies.
        obj.Set("findAll", Fn("findAll", (_, args) =>
        {
            var scene = GetScene();
            if (scene == null) return NewArray(Array.Empty<JsValue>());

            string name = args.At(0).ToString();
            var actors = scene.Layers.SelectMany(l => l.Actors)
                                     .Where(a => a.Name == name && !a.IsDestroyed)
                                     .Select(WrapActorAsProxy);
            return NewArray(actors);
        }, length: 1));

        // Scene.findByTag(tag) — a JS array of actor proxies. Empty is still truthy.
        obj.Set("findByTag", Fn("findByTag", (_, args) =>
        {
            var scene = GetScene();
            if (scene == null) return NewArray(Array.Empty<JsValue>());
            return NewArray(scene.FindByTag(args.At(0).ToString()).Select(WrapActorAsProxy));
        }, length: 1));

        // Scene.findFirstByTag(tag) — the single-actor form the bundled scripts want.
        obj.Set("findFirstByTag", Fn("findFirstByTag", (_, args) =>
        {
            var actor = GetScene()?.FindByTag(args.At(0).ToString()).FirstOrDefault();
            return actor != null ? WrapActorAsProxy(actor) : JsValue.Null;
        }, length: 1));

        // Scene.createActor(name, x = 0, y = 0)
        obj.Set("createActor", Fn("createActor", (_, args) =>
        {
            var scene = GetScene();
            if (scene == null) return JsValue.Null;

            var actor = new Actor(args.Length > 0 && !args.At(0).IsUndefined() ? args.At(0).ToString() : "Actor");
            actor.Transform.Position = new Vector2(Num(args.At(1)), Num(args.At(2)));
            scene.AddActor(actor);
            return WrapActorAsProxy(actor);
        }, length: 3));

        // Scene.addComponent(actor, "BoxCollider2D", { Size: [32, 32] })
        obj.Set("addComponent", Fn("addComponent", (_, args) =>
        {
            var target = Unwrap(args.At(0));
            if (target == null)
            {
                Warn("Scene.addComponent: the first argument is not an actor.");
                return JsValue.Null;
            }
            return AddComponentValue(target, args.At(1), args.At(2));
        }, length: 3));

        // Scene.destroy(actor) / Scene.destroyActor(actor)
        JsValue Destroy(JsValue thisObj, JsValue[] args)
        {
            var target = Unwrap(args.At(0));
            if (target == null) Warn("Scene.destroy: the argument is not an actor.");
            else target.Destroy();
            return JsValue.Undefined;
        }
        obj.Set("destroy",      Fn("destroy",      Destroy, length: 1));
        obj.Set("destroyActor", Fn("destroyActor", Destroy, length: 1));

        // Scene.instantiate(prefabPath, x = 0, y = 0) — a prefab file when one exists at the
        // path, otherwise a bare actor named after it.
        obj.Set("instantiate", Fn("instantiate", (_, args) =>
        {
            var scene = GetScene();
            if (scene == null) return JsValue.Null;

            string prefabPath = args.At(0).ToString();
            var    position   = new Vector2(Num(args.At(1)), Num(args.At(2)));

            string resolved = ProjectPaths.Resolve(prefabPath);
            Actor actor;
            if (File.Exists(resolved))
            {
                actor = global::SexyBiscuit.Engine.Scene.Prefab.Instantiate(resolved, scene, position);
            }
            else
            {
                actor = new Actor(Path.GetFileNameWithoutExtension(prefabPath));
                actor.Transform.Position = position;
                scene.AddActor(actor);
            }
            return WrapActorAsProxy(actor);
        }, length: 3));

        // Scene.load(scenePath)
        obj.Set("load", Fn("load", (_, args) =>
        {
            EngineHost.Current?.SceneManager.LoadScene(args.At(0).ToString());
            return JsValue.Undefined;
        }, length: 1));

        return obj;
    }

    // =========================================================================
    // Debug proxy
    // =========================================================================

    private ObjectInstance BuildDebugProxy()
    {
        var obj = NewObj();

        static string ArgsToString(JsValue[] args)
            => string.Join(" ", args.Select(a => a.IsObject() && a is not ArrayInstance ? Stringify(a) : a.ToString()));

        obj.Set("log",   Fn("log",   (_, args) => { ScriptDiagnostics.Report(ScriptDiagnosticLevel.Log,     ScriptPath, null, ArgsToString(args)); return JsValue.Undefined; }));
        obj.Set("warn",  Fn("warn",  (_, args) => { ScriptDiagnostics.Report(ScriptDiagnosticLevel.Warning, ScriptPath, null, ArgsToString(args)); return JsValue.Undefined; }));
        obj.Set("error", Fn("error", (_, args) => { ScriptDiagnostics.Report(ScriptDiagnosticLevel.Error,   ScriptPath, null, ArgsToString(args)); return JsValue.Undefined; }));

        return obj;
    }

    /// <summary>
    /// A string as a script means it, not as JSON means it.
    /// </summary>
    /// <remarks>
    /// <see cref="Stringify"/> exists for diagnostics and JSON-encodes, so a label
    /// set to <c>Hello</c> came back as <c>"Hello"</c> — quote marks and all — and
    /// would have rendered that way in every native build. The browser does no such
    /// thing, so this is the conversion anything script-facing wants.
    /// </remarks>
    private static string Text(JsValue value)
        => value.IsUndefined() || value.IsNull() ? string.Empty
         : value.IsString() ? value.AsString()
         : value.ToString();

    private static string Stringify(JsValue value)
    {
        try { return JsValueConverter.ToJsonNode(value)?.ToJsonString() ?? "null"; }
        catch { return value.ToString(); }
    }

    // =========================================================================
    // Vector2 proxy
    // =========================================================================

    private ObjectInstance BuildVector2Proxy()
    {
        var obj = NewObj();

        static (double x, double y) Unpack(JsValue v)
        {
            var vec = JsValueConverter.ReadVector2(v);
            return (vec.X, vec.Y);
        }

        // Vector2.create(x, y)
        obj.Set("create", Fn("create", (_, args) => Vec2(Num(args.At(0)), Num(args.At(1))), length: 2));

        // Vector2.add(a, b)
        obj.Set("add", Fn("add", (_, args) =>
        {
            var (ax, ay) = Unpack(args.At(0));
            var (bx, by) = Unpack(args.At(1));
            return Vec2(ax + bx, ay + by);
        }, length: 2));

        // Vector2.sub(a, b)
        obj.Set("sub", Fn("sub", (_, args) =>
        {
            var (ax, ay) = Unpack(args.At(0));
            var (bx, by) = Unpack(args.At(1));
            return Vec2(ax - bx, ay - by);
        }, length: 2));

        // Vector2.scale(v, s)
        obj.Set("scale", Fn("scale", (_, args) =>
        {
            var (vx, vy) = Unpack(args.At(0));
            double s     = Num(args.At(1));
            return Vec2(vx * s, vy * s);
        }, length: 2));

        // Vector2.normalize(v)
        obj.Set("normalize", Fn("normalize", (_, args) =>
        {
            var (vx, vy) = Unpack(args.At(0));
            double len   = Math.Sqrt(vx * vx + vy * vy);
            return len < 1e-10 ? Vec2(0, 0) : Vec2(vx / len, vy / len);
        }, length: 1));

        // Vector2.dot(a, b)
        obj.Set("dot", Fn("dot", (_, args) =>
        {
            var (ax, ay) = Unpack(args.At(0));
            var (bx, by) = Unpack(args.At(1));
            return new JsNumber(ax * bx + ay * by);
        }, length: 2));

        // Vector2.distance(a, b)
        obj.Set("distance", Fn("distance", (_, args) =>
        {
            var (ax, ay) = Unpack(args.At(0));
            var (bx, by) = Unpack(args.At(1));
            double dx = ax - bx, dy = ay - by;
            return new JsNumber(Math.Sqrt(dx * dx + dy * dy));
        }, length: 2));

        // Vector2.length(v)
        obj.Set("length", Fn("length", (_, args) =>
        {
            var (vx, vy) = Unpack(args.At(0));
            return new JsNumber(Math.Sqrt(vx * vx + vy * vy));
        }, length: 1));

        return obj;
    }

    // =========================================================================
    // Physics proxy — 2D queries
    // =========================================================================

    private ObjectInstance BuildPhysicsProxy()
    {
        var obj = NewObj();

        // Physics.raycast(originX, originY, dirX, dirY, maxDistance = Infinity)
        //   -> { actor, x, y, normalX, normalY, distance } or null
        obj.Set("raycast", Fn("raycast", (_, args) =>
        {
            var origin    = new Vector2(Num(args.At(0)), Num(args.At(1)));
            var direction = new Vector2(Num(args.At(2)), Num(args.At(3)));
            if (direction.LengthSquared() < 1e-12f) return JsValue.Null;

            // Infinity is not a length Aether can walk; a hundred thousand units is past any level.
            float distance = args.Length > 4 && args.At(4).IsNumber() && !double.IsInfinity(args.At(4).AsNumber())
                ? (float)args.At(4).AsNumber()
                : 100_000f;

            if (!PhysicsSystem2D.Instance.Raycast(origin, direction, distance, out var hit) || hit.Actor == null)
                return JsValue.Null;

            var result = NewObj();
            result.Set("actor",    WrapActorAsProxy(hit.Actor));
            result.Set("x",        new JsNumber(hit.Point.X));
            result.Set("y",        new JsNumber(hit.Point.Y));
            result.Set("normalX",  new JsNumber(hit.Normal.X));
            result.Set("normalY",  new JsNumber(hit.Normal.Y));
            result.Set("distance", new JsNumber(hit.Distance));
            return result;
        }, length: 5));

        // Physics.overlapCircle(x, y, radius) -> actor[]
        obj.Set("overlapCircle", Fn("overlapCircle", (_, args) =>
        {
            PhysicsSystem2D.Instance.CircleCast(new Vector2(Num(args.At(0)), Num(args.At(1))), Num(args.At(2)), -1, out var results);
            return NewArray(results.Select(WrapActorAsProxy));
        }, length: 3));

        // Physics.overlapBox(x, y, width, height) -> actor[]
        obj.Set("overlapBox", Fn("overlapBox", (_, args) =>
        {
            var centre      = new Vector2(Num(args.At(0)), Num(args.At(1)));
            var halfExtents = new Vector2(Num(args.At(2)) * 0.5f, Num(args.At(3)) * 0.5f);
            PhysicsSystem2D.Instance.BoxCast(centre, halfExtents, 0f, -1, out var results);
            return NewArray(results.Select(WrapActorAsProxy));
        }, length: 4));

        return obj;
    }

    // =========================================================================
    // Time proxy
    // =========================================================================

    private ObjectInstance BuildTimeProxy()
    {
        var obj = NewObj();

        Accessor(obj, "deltaTime",         getter: (_, _) => new JsNumber(Core.Time.DeltaTime),         setter: null, _engine);
        Accessor(obj, "unscaledDeltaTime", getter: (_, _) => new JsNumber(Core.Time.UnscaledDeltaTime), setter: null, _engine);
        Accessor(obj, "time",              getter: (_, _) => new JsNumber(Core.Time.TimeSinceStartup),  setter: null, _engine);
        Accessor(obj, "frameCount",        getter: (_, _) => new JsNumber(Core.Time.FrameCount),        setter: null, _engine);
        Accessor(obj, "fps",               getter: (_, _) => new JsNumber(Core.Time.Fps),               setter: null, _engine);
        Accessor(obj, "timeScale",
            getter: (_, _) => new JsNumber(Core.Time.TimeScale),
            setter: (_, args) => { Core.Time.TimeScale = Num(args.At(0), 1f); return JsValue.Undefined; },
            _engine);

        return obj;
    }

    // =========================================================================
    // UI proxy — the retained widget tree, in screen space
    // =========================================================================

    /// <summary>
    /// The canvas this script owns, made on first use.
    /// </summary>
    /// <remarks>
    /// One canvas per script is stronger than the ownership list the flat UI kept: a script
    /// cannot reach another's nodes at all, and two scripts are two canvases sorted by
    /// <see cref="UiCanvas.Order"/> rather than "whichever started first", which is the
    /// draw-order hazard the screen-effects cookie had to document.
    /// </remarks>
    private UiCanvas? _uiCanvas;

    /// <summary>Node handles, cached so <c>UI.find("hp") === UI.find("hp")</c> holds.</summary>
    private readonly Dictionary<UiNode, ObjectInstance> _uiHandles = new();

    /// <summary>Drops this script's UI. Called when the component goes away.</summary>
    internal void DisposeUi()
    {
        _uiCanvas?.OnDestroy();
        _uiCanvas = null;
        _uiHandles.Clear();
    }

    private UiCanvas Canvas()
    {
        if (_uiCanvas != null) return _uiCanvas;

        _uiCanvas = new UiCanvas();

        // The host sets this every frame, but onStart runs before the first one and a script
        // that reads UI.width to size something would otherwise get the 1280x720 default.
        var viewport = Core.EngineHost.Current?.GraphicsDevice?.Viewport;
        if (viewport is { } v) _uiCanvas.SetViewport(v.Width, v.Height);

        return _uiCanvas;
    }

    private ObjectInstance BuildUiProxy()
    {
        var obj = NewObj();

        void Prop(string name, Func<JsValue> get, Action<JsValue>? set = null)
            => Accessor(obj, name,
                getter: (_, _) => get(),
                setter: set == null ? null : (_, a) => { set(a.At(0)); return JsValue.Undefined; },
                _engine);

        Prop("width",  () => new JsNumber(Canvas().CanvasSize.X));
        Prop("height", () => new JsNumber(Canvas().CanvasSize.Y));

        // The area a notch or a television's overscan leaves usable.
        Prop("safeLeft",   () => new JsNumber(Canvas().SafeArea.X));
        Prop("safeTop",    () => new JsNumber(Canvas().SafeArea.Y));
        Prop("safeRight",  () => new JsNumber(Canvas().SafeArea.Z));
        Prop("safeBottom", () => new JsNumber(Canvas().SafeArea.W));

        obj.Set("build", Fn("build", (_, a) =>
        {
            UiCanvas canvas = Canvas();
            canvas.Adopt(UiDocument.FromJsValue(a.At(0)));
            return WrapUiNode(canvas.Root);
        }, 1));

        Prop("root", () => WrapUiNode(Canvas().Root));

        obj.Set("find", Fn("find", (_, a) => WrapUiNode(Canvas().Find(Text(a.At(0)))), 1));

        // Only this script's nodes, so one script cannot wipe another's HUD.
        obj.Set("clear", Fn("clear", (_, _) =>
        {
            if (_uiCanvas != null)
                foreach (UiNode child in new List<UiNode>(_uiCanvas.Root.Children))
                    _uiCanvas.Root.Remove(child);

            return JsValue.Undefined;
        }));

        obj.Set("measure", Fn("measure", (_, a) =>
            new JsNumber(UiTextMeasure.Width(Text(a.At(0)), Num(a.At(1), 1f))), 2));

        // Paint order against the canvases other scripts own. Higher is in front.
        Prop("order", () => new JsNumber(Canvas().Order), v => Canvas().Order = (int)Num(v));

        // Focus is what lets a script's menu work on a pad, a D-pad or a TV remote.
        Prop("focused", () => WrapUiNode(Canvas().Input.Focus.Focused));

        obj.Set("setFocus", Fn("setFocus", (_, a) =>
        {
            UiFocus focus = Canvas().Input.Focus;
            UiNode? target = a.At(0).IsString() ? Canvas().Find(a.At(0).AsString()) : NodeOf(a.At(0));
            return Bool(focus.Focus(target));
        }, 1));

        obj.Set("navigate", Fn("navigate", (_, a) =>
        {
            string name = Text(a.At(0)).ToLowerInvariant();
            NavDirection? direction = name switch
            {
                "up"    => NavDirection.Up,
                "down"  => NavDirection.Down,
                "left"  => NavDirection.Left,
                "right" => NavDirection.Right,
                _       => null,
            };
            return Bool(direction is { } d && Canvas().Input.Focus.Navigate(d));
        }, 1));

        /* "pointer", "directional" or "touch" — which device the player is driving with. */
        Prop("inputMode", () => new JsString(Canvas().Input.Focus.Mode.ToString().ToLowerInvariant()));

        return obj;
    }

    /// <summary>
    /// The handle a script holds on one node.
    /// </summary>
    /// <remarks>
    /// Every value member comes from <see cref="UiDocument.ScriptProperties"/>, wiring
    /// <c>get</c> to the codec's reader and <c>set</c> to its writer. A property added to the
    /// codec therefore reaches both engines' script API at once, rather than needing the five
    /// hand-mirrored edits the flat UI's handle did.
    /// </remarks>
    private JsValue WrapUiNode(UiNode? node)
    {
        if (node == null) return JsValue.Null;
        if (_uiHandles.TryGetValue(node, out ObjectInstance? cached)) return cached;

        var obj = NewObj();

        foreach (string key in UiDocument.ScriptProperties)
        {
            string name = key;

            Func<JsValue, JsValue[], JsValue>? setter = null;
            if (UiDocument.IsWritable(name))
                setter = (_, a) =>
                {
                    UiDocument.Apply(node, name, JsValueConverter.ToJsonNode(a.At(0)));
                    return JsValue.Undefined;
                };

            Accessor(obj, name,
                getter: (_, _) => JsValueConverter.FromJsonNode(_engine, UiDocument.Read(node, name)),
                setter: setter,
                _engine);
        }

        Accessor(obj, "parent",   getter: (_, _) => WrapUiNode(node.Parent), setter: null, _engine);
        Accessor(obj, "children", getter: (_, _) => NewArray(node.Children.Select(WrapUiNode)), setter: null, _engine);

        obj.Set("add", Fn("add", (_, a) => WrapUiNode(node.Add(UiDocument.FromJsValue(a.At(0)))), 1));
        obj.Set("find", Fn("find", (_, a) => WrapUiNode(node.Find(Text(a.At(0)))), 1));
        obj.Set("remove", Fn("remove", (_, _) => { node.Detach(); return JsValue.Undefined; }));
        obj.Set("focus", Fn("focus", (_, _) => { Canvas().Input.Focus.Focus(node); return JsValue.Undefined; }));

        _uiHandles[node] = obj;
        return obj;
    }

    /// <summary>The node behind a handle a script passed back in, or null.</summary>
    private UiNode? NodeOf(JsValue value)
    {
        if (value is not ObjectInstance handle) return null;

        foreach (KeyValuePair<UiNode, ObjectInstance> pair in _uiHandles)
            if (ReferenceEquals(pair.Value, handle)) return pair.Key;

        return null;
    }

    // =========================================================================
    // Network proxy
    // =========================================================================

    /// <summary>Handlers this script registered, so they go away with the script.</summary>
    private readonly List<Action> _networkUnsubscribes = new();

    /// <summary>Drops this script's network handlers. Called when the component goes away.</summary>
    internal void DisposeNetwork()
    {
        foreach (var unsubscribe in _networkUnsubscribes) unsubscribe();
        _networkUnsubscribes.Clear();
    }

    private static NetworkManager? Net => NetworkManager.Instance;

    /// <summary>
    /// The manager a start call should act on, created if there is none.
    /// </summary>
    /// <remarks>
    /// Only the three explicit calls -- startServer, startSolo, connect -- reach this. A send
    /// with no session is a no-op instead: an earlier version started a loopback session on any
    /// send, which meant a script broadcasting a position every frame silently hosted a game
    /// nobody had asked for, and left it running. "Runs alone" is already covered without that,
    /// because isLocalPlayer(0) is true before a server has said otherwise.
    /// </remarks>
    private static NetworkManager StartableNet() => NetworkManager.Instance ?? new NetworkManager();

    private ObjectInstance BuildNetworkProxy()
    {
        var obj = NewObj();

        // Player 0 is the local player before a server has said otherwise, so a lobby script
        // that asks `Network.isLocalPlayer(id)` behaves as a one-player game rather than a
        // dead one.
        Accessor(obj, "localId",     getter: (_, _) => new JsNumber(Net?.LocalClientId is int id and >= 0 ? id : 0), setter: null, _engine);
        Accessor(obj, "isServer",    getter: (_, _) => Bool(Net?.IsServer ?? false),    setter: null, _engine);
        Accessor(obj, "isHost",      getter: (_, _) => Bool(Net?.IsHost ?? false),      setter: null, _engine);
        Accessor(obj, "isConnected", getter: (_, _) => Bool(Net?.IsConnected ?? false), setter: null, _engine);
        Accessor(obj, "ping",        getter: (_, _) => new JsNumber(Net?.Ping ?? 0),    setter: null, _engine);
        Accessor(obj, "room",        getter: (_, _) => new JsString(Net?.Room ?? string.Empty), setter: null, _engine);

        Accessor(obj, "playerName",
            getter: (_, _) => new JsString(Net?.PlayerName ?? "Player"),
            setter: (_, args) => { EnsureNetName(args.At(0).ToString()); return JsValue.Undefined; },
            _engine);

        // A fresh array each read, so a script cannot mutate the engine's roster.
        Accessor(obj, "players", getter: (_, _) =>
        {
            var manager = Net;
            if (manager == null) return NewArray(Array.Empty<JsValue>());
            return NewArray(manager.Players.Select(p =>
            {
                var entry = NewObj();
                entry.Set("id",   new JsNumber(p.Key));
                entry.Set("name", new JsString(p.Value));
                return (JsValue)entry;
            }));
        }, setter: null, _engine);

        obj.Set("isLocalPlayer", Fn("isLocalPlayer", (_, args) =>
        {
            int local = Net?.LocalClientId is int id and >= 0 ? id : 0;
            return Bool((int)Num(args.At(0), -1f) == local);
        }, length: 1));

        // Network.startServer(port) -- WebSocket by default, because that is the only wire a
        // browser can join and the same script has to work there.
        obj.Set("startServer", Fn("startServer", (_, args) =>
        {
            try
            {
                var manager = StartableNet();
                if (manager.IsRunning) return Bool(true);
                manager.StartServer((int)Num(args.At(0), 7777f));
                return Bool(true);
            }
            catch (Exception ex) { Warn($"Network.startServer: {ex.Message}"); return Bool(false); }
        }, length: 2));

        // Network.startSolo() -- host with no socket at all.
        obj.Set("startSolo", Fn("startSolo", (_, _) =>
        {
            try
            {
                var manager = StartableNet();
                if (!manager.IsRunning) manager.StartSolo();
                return Bool(true);
            }
            catch (Exception ex) { Warn($"Network.startSolo: {ex.Message}"); return Bool(false); }
        }));

        // Network.connect(address, port) and Network.connect("ws://...").
        obj.Set("connect", Fn("connect", (_, args) =>
        {
            try
            {
                var manager = StartableNet();
                if (manager.IsRunning) manager.Disconnect();

                string address = args.At(0).ToString();
                if (address.StartsWith("ws://", StringComparison.Ordinal) ||
                    address.StartsWith("wss://", StringComparison.Ordinal))
                {
                    manager.ConnectToUrl(address, args.At(1).IsString() ? args.At(1).ToString() : "");
                }
                else
                {
                    manager.ConnectToServer(address, (int)Num(args.At(1), 7777f));
                }
                return Bool(true);
            }
            catch (Exception ex) { Warn($"Network.connect: {ex.Message}"); return Bool(false); }
        }, length: 2));

        obj.Set("disconnect", Fn("disconnect", (_, _) =>
        {
            Net?.Disconnect();
            return JsValue.Undefined;
        }));

        // Network.sendToAll(type, data) -- and `broadcast`, which the bundled templates use
        // for the same thing.
        JsValue SendToAll(JsValue thisObj, JsValue[] args)
        {
            Net?.SendMessageToAll(args.At(0).ToString(), ToJsonNode(args.At(1)));
            return JsValue.Undefined;
        }
        obj.Set("sendToAll", Fn("sendToAll", SendToAll, length: 2));
        obj.Set("broadcast", Fn("broadcast", SendToAll, length: 2));

        obj.Set("sendTo", Fn("sendTo", (_, args) =>
        {
            Net?.SendMessageTo((int)Num(args.At(0), -1f), args.At(1).ToString(), ToJsonNode(args.At(2)));
            return JsValue.Undefined;
        }, length: 3));

        // Network.on("message", (sender, type, data) => …)
        obj.Set("on", Fn("on", (_, args) =>
        {
            string eventName = args.At(0).ToString();
            if (!args.At(1).IsObject() || args.At(1) is not Jint.Native.Function.Function handler)
            {
                Warn($"Network.on('{eventName}'): the second argument must be a function.");
                return JsValue.Undefined;
            }

            // A subscription needs something to subscribe to. Nothing is started here: a script
            // that wants events before a session exists uses the onNetworkMessage hook, which
            // attaches itself the frame a session appears.
            if (NetworkManager.Instance is not { } manager)
            {
                Warn($"Network.on('{eventName}'): no session is running. "
                   + "Start one first, or use the onNetworkMessage hook, which waits for one.");
                return JsValue.Undefined;
            }

            Action? unsubscribe = eventName switch
            {
                "message" => Subscribe<int, string, JsonNode?>(
                    h => manager.OnMessage += h, h => manager.OnMessage -= h,
                    (sender, type, payload) => Call(handler, new JsNumber(sender), new JsString(type), FromJsonNode(payload))),
                "playerJoined" => Subscribe<int, string>(
                    h => manager.OnPlayerJoined += h, h => manager.OnPlayerJoined -= h,
                    (id, name) => Call(handler, new JsNumber(id), new JsString(name))),
                "playerLeft" => Subscribe<int>(
                    h => manager.OnPlayerLeft += h, h => manager.OnPlayerLeft -= h,
                    id => Call(handler, new JsNumber(id))),
                "connected" => Subscribe(
                    h => manager.OnConnectedToServer += h, h => manager.OnConnectedToServer -= h,
                    () => Call(handler)),
                "disconnected" => Subscribe<string>(
                    h => manager.OnDisconnectedFromServer += h, h => manager.OnDisconnectedFromServer -= h,
                    reason => Call(handler, new JsString(reason))),
                _ => null,
            };

            if (unsubscribe == null)
            {
                Warn($"Network.on: unknown event '{eventName}'. Try message, playerJoined, playerLeft, connected or disconnected.");
                return JsValue.Undefined;
            }

            _networkUnsubscribes.Add(unsubscribe);
            return Fn("off", (_, _) => { unsubscribe(); _networkUnsubscribes.Remove(unsubscribe); return JsValue.Undefined; });
        }, length: 2));

        return obj;
    }

    private static void EnsureNetName(string name) => StartableNet().PlayerName = name;

    private static Action Subscribe(Action<Action> add, Action<Action> remove, Action handler)
    {
        add(handler);
        return () => remove(handler);
    }

    private static Action Subscribe<T>(Action<Action<T>> add, Action<Action<T>> remove, Action<T> handler)
    {
        add(handler);
        return () => remove(handler);
    }

    private static Action Subscribe<T1, T2>(Action<Action<T1, T2>> add, Action<Action<T1, T2>> remove, Action<T1, T2> handler)
    {
        add(handler);
        return () => remove(handler);
    }

    private static Action Subscribe<T1, T2, T3>(Action<Action<T1, T2, T3>> add, Action<Action<T1, T2, T3>> remove, Action<T1, T2, T3> handler)
    {
        add(handler);
        return () => remove(handler);
    }

    /// <summary>Calls a script handler, isolating a throw so one bad listener cannot kill the session.</summary>
    private void Call(Jint.Native.Function.Function handler, params JsValue[] args)
    {
        try { handler.Call(JsValue.Undefined, args); }
        catch (Exception ex) { Warn($"Network handler threw: {ex.Message}"); }
    }

    /// <summary>A script value as JSON, for the message channel. Undefined and null both become null.</summary>
    /// <remarks>
    /// Jint's own serialiser rather than evaluating <c>JSON.stringify</c> in the script's engine.
    /// The evaluated form looked equivalent and was not: it re-enters the engine while a frame is
    /// being dispatched, and a script that had shadowed <c>JSON</c> would decide what the wire
    /// carried.
    /// </remarks>
    private JsonNode? ToJsonNode(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull()) return null;
        try
        {
            var json = new Jint.Native.Json.JsonSerializer(_engine)
                .Serialize(value, JsValue.Undefined, JsValue.Undefined);
            return json.IsUndefined() ? null : JsonNode.Parse(json.ToString());
        }
        catch (Exception)
        {
            // A cyclic object, or something JSON cannot express. Dropping the payload beats
            // taking the send down: the message type still arrives.
            Warn("Network: the message payload could not be converted to JSON; sending it empty.");
            return null;
        }
    }

    /// <summary>JSON back into a script value. Internal so ScriptComponent can dispatch a message.</summary>
    internal JsValue JsonToScript(JsonNode? node) => FromJsonNode(node);

    /// <summary>JSON back into a script value.</summary>
    private JsValue FromJsonNode(JsonNode? node)
    {
        if (node == null) return JsValue.Null;
        try
        {
            return new Jint.Native.Json.JsonParser(_engine).Parse(node.ToJsonString());
        }
        catch (Exception) { return JsValue.Null; }
    }

    // =========================================================================
    // DG proxy — the Darks Games account and social layer
    // =========================================================================

    /// <summary>Handlers this script registered with the DG runtime.</summary>
    private readonly List<Action> _dgUnsubscribes = new();

    /// <summary>Drops this script's DG handlers. Called when the component goes away.</summary>
    internal void DisposeDarksGames()
    {
        foreach (var unsubscribe in _dgUnsubscribes) unsubscribe();
        _dgUnsubscribes.Clear();
    }

    private static DarksGamesRuntime? Dg => DarksGamesRuntime.Instance;

    private ObjectInstance BuildDarksGamesProxy()
    {
        var obj = NewObj();

        // Every read is safe signed out, and safe with no runtime at all. A game that never
        // ships to DarksGames still runs every line of a script that uses this.
        Accessor(obj, "available", getter: (_, _) => Bool(Dg != null),          setter: null, _engine);
        Accessor(obj, "signedIn",  getter: (_, _) => Bool(Dg?.SignedIn ?? false), setter: null, _engine);
        Accessor(obj, "userId",    getter: (_, _) => Str(Dg?.User.Id),          setter: null, _engine);
        Accessor(obj, "userName",  getter: (_, _) => Str(Dg?.User.Name),        setter: null, _engine);
        Accessor(obj, "handle",    getter: (_, _) => Str(Dg?.User.Handle),      setter: null, _engine);
        Accessor(obj, "displayName", getter: (_, _) => new JsString(Dg?.User.DisplayName ?? "Player"), setter: null, _engine);
        Accessor(obj, "game",      getter: (_, _) => new JsString(Dg?.Game ?? string.Empty), setter: null, _engine);

        // DG.presence({ state: "wave 7", detail: "…", joinCode: "ABC234", players: 3, max: 4 })
        obj.Set("presence", Fn("presence", (_, args) =>
        {
            if (Dg == null) return JsValue.Undefined;
            if (args.At(0) is not ObjectInstance fields)
            {
                Warn("DG.presence: pass an object, e.g. { state: 'lobby', joinCode: room }.");
                return JsValue.Undefined;
            }

            Dg.Presence(
                Text(fields.Get("state"), "playing"),
                Text(fields.Get("detail"), string.Empty),
                fields.Get("joinCode") is { } code && !code.IsUndefined() && !code.IsNull() ? code.ToString() : null,
                fields.Get("joinable") is { } joinable && !joinable.IsUndefined()
                    ? TypeConverter.ToBoolean(joinable) : true,
                Count(fields.Get("players")),
                Count(fields.Get("max")));
            return JsValue.Undefined;
        }, length: 1));

        obj.Set("clearPresence", Fn("clearPresence", (_, _) => { Dg?.ClearPresence(); return JsValue.Undefined; }));

        // DG.achievement("first_clear") / DG.achievement("kills", 10)
        obj.Set("achievement", Fn("achievement", (_, args) =>
        {
            Dg?.Achievement(args.At(0).ToString(), Count(args.At(1)));
            return JsValue.Undefined;
        }, length: 2));

        // DG.loadSave() -- the save arrives on the "save" event, because a game loop cannot wait.
        obj.Set("loadSave", Fn("loadSave", (_, _) => { Dg?.LoadSave(); return JsValue.Undefined; }));

        obj.Set("saveCloud", Fn("saveCloud", (_, args) =>
        {
            Dg?.WriteSave(ToJsonNode(args.At(0)), (int)Num(args.At(1), 1f));
            return JsValue.Undefined;
        }, length: 2));

        obj.Set("on", Fn("on", (_, args) =>
        {
            string eventName = args.At(0).ToString();
            if (Dg == null) return JsValue.Undefined;
            if (args.At(1) is not Jint.Native.Function.Function handler)
            {
                Warn($"DG.on('{eventName}'): the second argument must be a function.");
                return JsValue.Undefined;
            }

            var runtime = Dg;
            Action? unsubscribe = eventName switch
            {
                "user" => Subscribe<DarksGamesUser>(
                    h => runtime.UserChanged += h, h => runtime.UserChanged -= h,
                    user => Call(handler, Bool(user.SignedIn), Str(user.Id), new JsString(user.DisplayName))),
                "save" => Subscribe<DarksGamesSave?>(
                    h => runtime.SaveLoaded += h, h => runtime.SaveLoaded -= h,
                    save => Call(handler, save == null ? JsValue.Null : FromJsonNode(save.Data))),
                "saveConflict" => Subscribe<DarksGamesSave>(
                    h => runtime.SaveConflict += h, h => runtime.SaveConflict -= h,
                    save => Call(handler, FromJsonNode(save.Data))),
                "achievement" => Subscribe<string, bool>(
                    h => runtime.AchievementReported += h, h => runtime.AchievementReported -= h,
                    (key, ok) => Call(handler, new JsString(key), Bool(ok))),
                _ => null,
            };

            if (unsubscribe == null)
            {
                Warn($"DG.on: unknown event '{eventName}'. Try user, save, saveConflict or achievement.");
                return JsValue.Undefined;
            }

            _dgUnsubscribes.Add(unsubscribe);
            return Fn("off", (_, _) => { unsubscribe(); _dgUnsubscribes.Remove(unsubscribe); return JsValue.Undefined; });
        }, length: 2));

        return obj;
    }

    private static JsValue Str(string? value) => value == null ? JsValue.Null : new JsString(value);

    private static string Text(JsValue value, string fallback)
        => value.IsUndefined() || value.IsNull() ? fallback : value.ToString();

    /// <summary>A seat count from a script, or null when it did not give one.</summary>
    private static int? Count(JsValue value)
        => value.IsUndefined() || value.IsNull() ? null : (int)TypeConverter.ToNumber(value);

    // =========================================================================
    // Components
    // =========================================================================

    /// <summary>Resolves a component type by any name a scene file accepts, or null.</summary>
    internal static Type? ResolveComponentType(string name)
        => string.IsNullOrWhiteSpace(name) ? null : global::SexyBiscuit.Engine.Scene.SceneSerializer.ResolveComponentType(name.Trim());

    private JsValue GetComponentValue(Actor actor, JsValue typeName)
    {
        var type = ResolveComponentType(typeName.ToString());
        if (type == null) return JsValue.Null;

        var component = actor.GetAllComponents().FirstOrDefault(type.IsInstanceOfType);
        return component != null ? ComponentProxy.Wrap(component, this) : JsValue.Null;
    }

    private JsValue AddComponentValue(Actor actor, JsValue typeName, JsValue properties)
    {
        var type = ResolveComponentType(typeName.ToString());
        if (type == null)
        {
            Warn($"Unknown component type '{typeName}'.");
            return JsValue.Null;
        }

        var component = actor.AddComponent(type);
        if (properties is ObjectInstance props) ApplyProperties(component, props);
        return ComponentProxy.Wrap(component, this);
    }

    /// <summary>Applies a <c>{ Name: value }</c> object to a component, in either spelling of each name.</summary>
    private void ApplyProperties(Component component, ObjectInstance properties)
    {
        var type = component.GetType();
        foreach (var pair in properties.GetOwnProperties())
        {
            if (!pair.Value.Enumerable) continue;
            string key = pair.Key.ToString();

            var property = Mcp.ComponentReflection.FindProperty(type, key);
            if (property == null)
            {
                Warn($"{type.Name} has no property '{key}'.");
                continue;
            }

            try
            {
                property.SetValue(component, JsValueConverter.ToClr(properties.Get(pair.Key), property.PropertyType));
            }
            catch (Exception ex)
            {
                Warn($"{type.Name}.{property.Name}: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // Chibi — MakeChibi's characters
    // =========================================================================

    /// <summary>The character component behind an actor proxy, or null.</summary>
    private Chibi.ChibiCharacter? Character(JsValue value)
        => Unwrap(value)?.GetComponent<Chibi.ChibiCharacter>();

    private JsValue SpawnChibi(string recipePath, int seed, JsValue[] args, int firstAxis)
    {
        var scene = GetScene();
        if (scene == null) return JsValue.Null;

        var actor = new Actor(recipePath.Length > 0 ? "Chibi" : $"Chibi {seed}");
        actor.AddComponent<Transform3D>().LocalPosition = new Vector3(
            Num(args.At(firstAxis)), Num(args.At(firstAxis + 1)), Num(args.At(firstAxis + 2)));

        var character = actor.AddComponent<Chibi.ChibiCharacter>();
        character.RecipePath = recipePath;
        character.Seed = seed;
        actor.AddComponent<Chibi.ChibiAnimator>();

        scene.AddActor(actor);
        return WrapActorAsProxy(actor);
    }

    /// <summary>
    /// The <c>Chibi</c> global: spawn a character, dress it, animate it, hang things off it.
    /// </summary>
    /// <remarks>
    /// Everything here is a namespace function rather than a member of the actor a script
    /// holds, because the proxy <c>Scene.createActor</c> hands back has no <c>attachTo</c>,
    /// <c>addComponent</c> or <c>children</c> — only the running script's own <c>actor</c>
    /// global gained those. Building the body natively also keeps forty actors' worth of
    /// construction on this side of the boundary.
    /// </remarks>
    private ObjectInstance BuildChibiProxy()
    {
        var obj = NewObj();

        obj.Set("spawn", Fn("spawn", (_, args) =>
        {
            return SpawnChibi(Text(args.At(0), string.Empty), 0, args, 1);
        }, length: 4));

        obj.Set("random", Fn("random", (_, args) =>
        {
            return SpawnChibi(string.Empty, (int)Num(args.At(0)), args, 1);
        }, length: 4));

        obj.Set("play", Fn("play", (_, args) =>
        {
            var animator = Unwrap(args.At(0))?.GetComponent<Chibi.ChibiAnimator>();
            if (animator == null) return Bool(false);

            float? blend = args.At(2).IsUndefined() ? null : Num(args.At(2));
            return Bool(animator.Play(Text(args.At(1), "idle"), blend));
        }, length: 3));

        obj.Set("stop", Fn("stop", (_, args) =>
        {
            Unwrap(args.At(0))?.GetComponent<Chibi.ChibiAnimator>()?.Stop();
            return JsValue.Undefined;
        }, length: 1));

        obj.Set("setColour", Fn("setColour", (_, args) =>
        {
            var built = Character(args.At(0))?.Chibi;
            return Bool(built != null
                && built.SetColour(Text(args.At(1), string.Empty), Text(args.At(2), "#FFFFFF")));
        }, length: 3));

        obj.Set("setStyle", Fn("setStyle", (_, args) =>
        {
            var character = Character(args.At(0));
            var built = character?.Chibi;
            if (character == null || built == null) return Bool(false);

            string slot = Text(args.At(1), string.Empty);
            if (!built.Recipe.Style.ContainsKey(slot)) return Bool(false);

            Chibi.ChibiRecipe recipe = built.Recipe;
            recipe.Style[slot] = Text(args.At(2), string.Empty);
            character.Rebuild(recipe);
            // The animator cached transforms that have just been destroyed.
            Unwrap(args.At(0))?.GetComponent<Chibi.ChibiAnimator>()?.Resolve();
            return Bool(true);
        }, length: 3));

        obj.Set("attach", Fn("attach", (_, args) =>
        {
            var built = Character(args.At(0))?.Chibi;
            Actor? target = Unwrap(args.At(2));
            if (built == null || target == null) return Bool(false);
            if (!built.Sockets.TryGetValue(Text(args.At(1), string.Empty), out Actor? socket))
                return Bool(false);

            if (target.GetComponent<Transform3D>() == null) target.AddComponent<Transform3D>();
            // false: a sword's offset means "in the hand", not "where it was standing".
            target.AttachTo(socket, keepWorldTransform: false);
            return Bool(true);
        }, length: 3));

        obj.Set("socket", Fn("socket", (_, args) =>
        {
            var built = Character(args.At(0))?.Chibi;
            if (built != null && built.Sockets.TryGetValue(Text(args.At(1), string.Empty), out Actor? socket))
                return WrapActorAsProxy(socket);
            return JsValue.Null;
        }, length: 2));

        return obj;
    }

    // =========================================================================
    // Actors: proxies in, actors out
    // =========================================================================

    /// <summary>The actor a script-facing proxy stands for, or null for anything else.</summary>
    internal Actor? Unwrap(JsValue value)
        => value is ObjectInstance obj && _proxyToActor.TryGetValue(obj, out var actor) ? actor : null;

    /// <summary>
    /// Creates a lightweight JS proxy for <paramref name="actor"/> exposing id, name, tag,
    /// active, transform.{x, y, rotation}, getComponent() and destroy().
    /// Used by Scene.find, Scene.findByTag, Scene.instantiate, and collision / trigger callbacks.
    /// Returns null for a destroyed actor, as the browser bridge does.
    /// </summary>
    internal JsValue WrapActorAsProxy(Actor actor)
    {
        if (actor == null || actor.IsDestroyed) return JsValue.Null;

        var obj = NewObj();

        Accessor(obj, "id", getter: (_, _) => new JsNumber(actor.Id), setter: null, _engine);

        Accessor(obj, "name",
            getter: (_, _) => new JsString(actor.Name),
            setter: (_, args) => { actor.Name = args.At(0).ToString(); return JsValue.Undefined; },
            _engine);

        Accessor(obj, "tag",
            getter: (_, _) => new JsString(actor.Tag),
            setter: (_, args) => { actor.Tag = args.At(0).ToString(); return JsValue.Undefined; },
            _engine);

        // A DESTROYED actor is not active, whatever its IsActive flag still says.
        //
        // `IsActive` is an ordinary field and Destroy() does not clear it, so a
        // script holding a reference to something that has since been destroyed
        // read `active === true` forever. Every internal check in the engine is
        // `!IsActive || _destroyed`; the script API exposed only half of that,
        // and `active` is the ONLY liveness signal a script has -- there is no
        // `isDestroyed` in the contract.
        //
        // What that costs is not a crash. A script caches a reference, guards it
        // with `if (!thing || thing.active !== true) refresh()`, and the guard
        // never fires: it keeps talking to a corpse. In UltraDark-sb every boss
        // after the first took no damage from the player's gun, because the
        // projectile pool's cached boss script belonged to the boss before it.
        // It read exactly like a balance problem.
        Accessor(obj, "active",
            getter: (_, _) => Bool(actor.IsActive && !actor.IsDestroyed),
            setter: (_, args) => { actor.IsActive = TypeConverter.ToBoolean(args.At(0)); return JsValue.Undefined; },
            _engine);

        // actor.transform3d, exactly as the script's OWN actor has had it.
        //
        // Without this a script could move itself in 3D but not anything it
        // created, so a world built from script -- which is how any world worth
        // tuning is built -- could not be placed in 3D at all. The proxy is
        // cached per actor rather than per call, because setting x, y and z
        // reads this property three times.
        Transform3D? cached3D = null;
        ObjectInstance? cached3DProxy = null;
        Accessor(obj, "transform3d",
            getter: (_, _) =>
            {
                var t3 = actor.GetComponent<Transform3D>();
                if (t3 == null) return JsValue.Null;
                if (!ReferenceEquals(t3, cached3D) || cached3DProxy == null)
                {
                    cached3D = t3;
                    cached3DProxy = BuildTransform3DProxy(t3);
                }
                return cached3DProxy;
            },
            setter: null, _engine);

        // Nested transform sub-object with x/y/rotation accessors
        var tfObj = NewObj();
        var tf    = actor.Transform;

        Accessor(tfObj, "x",
            getter: (_, _) => new JsNumber(tf.Position.X),
            setter: (_, args) => { tf.Position = new Vector2(Num(args.At(0)), tf.Position.Y); return JsValue.Undefined; },
            _engine);

        Accessor(tfObj, "y",
            getter: (_, _) => new JsNumber(tf.Position.Y),
            setter: (_, args) => { tf.Position = new Vector2(tf.Position.X, Num(args.At(0))); return JsValue.Undefined; },
            _engine);

        Accessor(tfObj, "rotation",
            getter: (_, _) => new JsNumber(tf.Rotation),
            setter: (_, args) => { tf.Rotation = Num(args.At(0)); return JsValue.Undefined; },
            _engine);

        obj.Set("transform", tfObj);

        obj.Set("getComponent", Fn("getComponent", (_, args) => GetComponentValue(actor, args.At(0)), length: 1));

        obj.Set("destroy", Fn("destroy", (_, _) =>
        {
            actor.Destroy();
            return JsValue.Undefined;
        }));

        _proxyToActor.AddOrUpdate(obj, actor);
        return obj;
    }

    /// <summary>
    /// The object a collision hook receives:
    /// <c>{ other, contactPoint: {x, y}, normal: {x, y}, relativeVelocity, tag, name, getComponent() }</c>,
    /// where tag, name and getComponent forward to the other actor as they do in the browser.
    /// </summary>
    internal ObjectInstance WrapCollisionData(CollisionData data)
    {
        var dataObj = NewObj();

        dataObj.Set("other", WrapActorAsProxy(data.Other));
        dataObj.Set("contactPoint", Vec2(data.ContactPoint.X, data.ContactPoint.Y));
        dataObj.Set("normal", Vec2(data.Normal.X, data.Normal.Y));
        dataObj.Set("relativeVelocity", new JsNumber(data.RelativeVelocity));

        var other = data.Other;
        Accessor(dataObj, "tag",  getter: (_, _) => new JsString(other?.Tag ?? string.Empty),  setter: null, _engine);
        Accessor(dataObj, "name", getter: (_, _) => new JsString(other?.Name ?? string.Empty), setter: null, _engine);

        // The bundled scripts name the parameter `other` and reach for the other actor's script
        // through it: onCollisionEnter(other) { other.getComponent("ScriptComponent").invoke(...) }.
        dataObj.Set("getComponent", Fn("getComponent", (_, args) =>
            other == null || other.IsDestroyed ? JsValue.Null : GetComponentValue(other, args.At(0)), length: 1));

        return dataObj;
    }
}
