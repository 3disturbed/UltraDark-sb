using System.Diagnostics;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Descriptors.Specialized;
using Jint.Runtime.Interop;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Audio;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;

using JintEngine = Jint.Engine;

namespace SexyBiscuit.Engine.Scripting;

/// <summary>
/// Constructs and owns all JavaScript proxy objects that expose the C# engine API
/// as clean, ergonomic globals inside a Jint script environment.
///
/// Each proxy is a native Jint <see cref="ObjectInstance"/> populated with
/// C# delegates wrapped as <see cref="DelegateWrapper"/> values and
/// get/set accessor properties backed by <see cref="GetSetPropertyDescriptor"/>.
///
/// One <see cref="ScriptBridge"/> is created per <see cref="ScriptComponent"/>
/// and reused across hot-reloads — only the JS source changes, not the bridge.
/// </summary>
public sealed class ScriptBridge
{
    // -------------------------------------------------------------------------
    // Owning references
    // -------------------------------------------------------------------------
    private readonly Actor  _actor;
    private readonly JintEngine _engine;

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

        ActorProxy     = BuildActorProxy();
        TransformProxy = BuildTransformProxy();
        InputProxy     = BuildInputProxy();
        AudioProxy     = BuildAudioProxy();
        SceneProxy     = BuildSceneProxy();
        DebugProxy     = BuildDebugProxy();
        Vector2Proxy   = BuildVector2Proxy();
    }

    // =========================================================================
    // Helpers — allocate a fresh plain JS object and define accessor properties
    // =========================================================================

    /// <summary>Allocates a new, empty plain JS object on the engine's heap.</summary>
    private ObjectInstance NewObj()
        => _engine.Evaluate("({})").AsObject();

    /// <summary>
    /// Wraps a C# delegate as a Jint function value using
    /// <see cref="DelegateWrapper"/>, which is the Jint 3.1.1 way
    /// to expose arbitrary C# lambdas as JS callable values.
    /// </summary>
    private JsValue Fn(string name, Func<JsValue, JsValue[], JsValue> body, int length = 0)
        => JsValue.FromObject(_engine, (Delegate)body);

    /// <summary>
    /// Defines a JavaScript accessor property (get + optional set) on
    /// <paramref name="target"/> using Jint's <see cref="GetSetPropertyDescriptor"/>.
    /// </summary>
    private static void Accessor(
        ObjectInstance target,
        string name,
        Func<JsValue, JsValue[], JsValue> getter,
        Func<JsValue, JsValue[], JsValue>? setter,
        JintEngine engine)
    {
        JsValue getterFn = JsValue.FromObject(engine, (Delegate)getter);
        JsValue setterFn = setter != null
            ? JsValue.FromObject(engine, (Delegate)setter)
            : JsValue.Undefined;

        target.DefineOwnProperty(name,
            new GetSetPropertyDescriptor(getterFn, setterFn, enumerable: true, configurable: true));
    }

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
        Accessor(obj, "active",
            getter: (_, _) => _actor.IsActive ? JsBoolean.True : JsBoolean.False,
            setter: (_, args) => { _actor.IsActive = TypeConverter.ToBoolean(args.At(0)); return JsValue.Undefined; },
            _engine);

        // actor.destroy()
        obj.Set("destroy", Fn("destroy", (_, _) =>
        {
            _actor.Destroy();
            return JsValue.Undefined;
        }));

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
            setter: (_, args) => { t.Position = new Vector2((float)args.At(0).AsNumber(), t.Position.Y); return JsValue.Undefined; },
            _engine);

        // transform.y (get/set)
        Accessor(obj, "y",
            getter: (_, _) => new JsNumber(t.Position.Y),
            setter: (_, args) => { t.Position = new Vector2(t.Position.X, (float)args.At(0).AsNumber()); return JsValue.Undefined; },
            _engine);

        // transform.rotation (get/set, radians)
        Accessor(obj, "rotation",
            getter: (_, _) => new JsNumber(t.Rotation),
            setter: (_, args) => { t.Rotation = (float)args.At(0).AsNumber(); return JsValue.Undefined; },
            _engine);

        // transform.scaleX (get/set)
        Accessor(obj, "scaleX",
            getter: (_, _) => new JsNumber(t.Scale.X),
            setter: (_, args) => { t.Scale = new Vector2((float)args.At(0).AsNumber(), t.Scale.Y); return JsValue.Undefined; },
            _engine);

        // transform.scaleY (get/set)
        Accessor(obj, "scaleY",
            getter: (_, _) => new JsNumber(t.Scale.Y),
            setter: (_, args) => { t.Scale = new Vector2(t.Scale.X, (float)args.At(0).AsNumber()); return JsValue.Undefined; },
            _engine);

        // transform.lookAt(x, y)
        obj.Set("lookAt", Fn("lookAt", (_, args) =>
        {
            float x = (float)args.At(0).AsNumber();
            float y = (float)args.At(1).AsNumber();
            t.LookAt(new Vector2(x, y));
            return JsValue.Undefined;
        }, length: 2));

        // transform.distanceTo(otherTransformProxy)
        // The "other" argument is expected to be a transform proxy object with x/y.
        obj.Set("distanceTo", Fn("distanceTo", (_, args) =>
        {
            var other = args.At(0) as ObjectInstance;
            if (other == null) return new JsNumber(0);
            float ox = (float)other.Get("x").AsNumber();
            float oy = (float)other.Get("y").AsNumber();
            return new JsNumber(Vector2.Distance(t.Position, new Vector2(ox, oy)));
        }, length: 1));

        return obj;
    }

    // =========================================================================
    // Input proxy
    // =========================================================================

    private ObjectInstance BuildInputProxy()
    {
        var obj = NewObj();

        InputManager? GetInput() => EngineHost.Current?.Input;

        // Input.isPressed(action)
        obj.Set("isPressed", Fn("isPressed", (_, args) =>
            (JsValue)(GetInput()?.IsPressed(args.At(0).ToString()) == true
                ? JsBoolean.True : JsBoolean.False), length: 1));

        // Input.isHeld(action)
        obj.Set("isHeld", Fn("isHeld", (_, args) =>
            (JsValue)(GetInput()?.IsHeld(args.At(0).ToString()) == true
                ? JsBoolean.True : JsBoolean.False), length: 1));

        // Input.isReleased(action)
        obj.Set("isReleased", Fn("isReleased", (_, args) =>
            (JsValue)(GetInput()?.IsReleased(args.At(0).ToString()) == true
                ? JsBoolean.True : JsBoolean.False), length: 1));

        // Input.getAxis(action)
        obj.Set("getAxis", Fn("getAxis", (_, args) =>
            new JsNumber(GetInput()?.GetAxis(args.At(0).ToString()) ?? 0f), length: 1));

        // Input.mouseX (read-only accessor)
        Accessor(obj, "mouseX",
            getter: (_, _) => new JsNumber(GetInput()?.MousePosition.X ?? 0f),
            setter: null,
            _engine);

        // Input.mouseY (read-only accessor)
        Accessor(obj, "mouseY",
            getter: (_, _) => new JsNumber(GetInput()?.MousePosition.Y ?? 0f),
            setter: null,
            _engine);

        return obj;
    }

    // =========================================================================
    // Audio proxy
    // =========================================================================

    private ObjectInstance BuildAudioProxy()
    {
        var obj = NewObj();

        AudioManager? GetAudio() => EngineHost.Current?.Audio;

        // Audio.play(path) — returns an opaque handle object {id: number}
        obj.Set("play", Fn("play", (_, args) =>
        {
            var audio = GetAudio();
            if (audio == null) return JsValue.Null;

            var handle    = audio.Play(args.At(0).ToString());
            var handleObj = NewObj();
            handleObj.Set("id", new JsNumber(handle.Id));
            return handleObj;
        }, length: 1));

        // Audio.playOneShot(path)
        obj.Set("playOneShot", Fn("playOneShot", (_, args) =>
        {
            GetAudio()?.PlayOneShot(args.At(0).ToString());
            return JsValue.Undefined;
        }, length: 1));

        // Audio.stop(handle)
        // handle is the object returned by play(), or a raw numeric id.
        obj.Set("stop", Fn("stop", (_, args) =>
        {
            var audio = GetAudio();
            if (audio == null) return JsValue.Undefined;

            var handleVal = args.At(0);
            uint id = 0;

            if (handleVal is ObjectInstance h)
            {
                var idVal = h.Get("id");
                if (!idVal.IsUndefined() && idVal.IsNumber())
                    id = (uint)idVal.AsNumber();
            }
            else if (handleVal.IsNumber())
            {
                id = (uint)handleVal.AsNumber();
            }

            if (id != 0)
                audio.Stop(new AudioHandle { Id = id });

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

        Core.Scene? GetScene() => EngineHost.Current?.SceneManager.ActiveScene;

        // Scene.find(name)
        obj.Set("find", Fn("find", (_, args) =>
        {
            var actor = GetScene()?.FindByName(args.At(0).ToString());
            return actor != null ? WrapActorAsProxy(actor) : JsValue.Null;
        }, length: 1));

        // Scene.findByTag(tag) — returns a JS array of actor proxies
        obj.Set("findByTag", Fn("findByTag", (_, args) =>
        {
            var scene = GetScene();
            if (scene == null) return _engine.Evaluate("[]");

            var actors = scene.FindByTag(args.At(0).ToString()).ToArray();
            // Build a JS array by evaluating "[]" and pushing items via indexed Set.
            // ArrayInstance in Jint 3.1.1 auto-updates length when numeric keys are set.
            var jsArray = (Jint.Native.Array.ArrayInstance)_engine.Evaluate("[]");
            for (int i = 0; i < actors.Length; i++)
                jsArray.Set(i.ToString(), WrapActorAsProxy(actors[i]));
            return jsArray;
        }, length: 1));

        // Scene.instantiate(prefabPath, x, y) — creates a new actor, returns proxy
        obj.Set("instantiate", Fn("instantiate", (_, args) =>
        {
            var scene = GetScene();
            if (scene == null) return JsValue.Null;

            string prefabPath = args.At(0).ToString();
            float  x          = (float)args.At(1).AsNumber();
            float  y          = (float)args.At(2).AsNumber();

            var actor = new Actor(Path.GetFileNameWithoutExtension(prefabPath));
            actor.Transform.Position = new Vector2(x, y);
            scene.AddActor(actor);
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
            => string.Join(" ", args.Select(a => a.ToString()));

        obj.Set("log", Fn("log", (_, args) =>
        {
            var msg = ArgsToString(args);
            Console.WriteLine($"[Script] {msg}");
            System.Diagnostics.Debug.WriteLine($"[Script] {msg}");
            return JsValue.Undefined;
        }));

        obj.Set("warn", Fn("warn", (_, args) =>
        {
            var msg = ArgsToString(args);
            Console.WriteLine($"[Script WARN] {msg}");
            System.Diagnostics.Debug.WriteLine($"[Script WARN] {msg}");
            return JsValue.Undefined;
        }));

        obj.Set("error", Fn("error", (_, args) =>
        {
            var msg = ArgsToString(args);
            Console.WriteLine($"[Script ERROR] {msg}");
            System.Diagnostics.Debug.WriteLine($"[Script ERROR] {msg}");
            return JsValue.Undefined;
        }));

        return obj;
    }

    // =========================================================================
    // Vector2 proxy
    // =========================================================================

    private ObjectInstance BuildVector2Proxy()
    {
        var obj = NewObj();

        // Helper: build a {x, y} JS value object
        JsValue MakeVec(double x, double y)
        {
            var v = NewObj();
            v.Set("x", new JsNumber(x));
            v.Set("y", new JsNumber(y));
            return v;
        }

        static (double x, double y) Unpack(JsValue v)
        {
            if (v is ObjectInstance o)
                return (o.Get("x").AsNumber(), o.Get("y").AsNumber());
            return (0, 0);
        }

        // Vector2.create(x, y)
        obj.Set("create", Fn("create", (_, args) =>
            MakeVec(args.At(0).AsNumber(), args.At(1).AsNumber()), length: 2));

        // Vector2.add(a, b)
        obj.Set("add", Fn("add", (_, args) =>
        {
            var (ax, ay) = Unpack(args.At(0));
            var (bx, by) = Unpack(args.At(1));
            return MakeVec(ax + bx, ay + by);
        }, length: 2));

        // Vector2.sub(a, b)
        obj.Set("sub", Fn("sub", (_, args) =>
        {
            var (ax, ay) = Unpack(args.At(0));
            var (bx, by) = Unpack(args.At(1));
            return MakeVec(ax - bx, ay - by);
        }, length: 2));

        // Vector2.scale(v, s)
        obj.Set("scale", Fn("scale", (_, args) =>
        {
            var (vx, vy) = Unpack(args.At(0));
            double s     = args.At(1).AsNumber();
            return MakeVec(vx * s, vy * s);
        }, length: 2));

        // Vector2.normalize(v)
        obj.Set("normalize", Fn("normalize", (_, args) =>
        {
            var (vx, vy) = Unpack(args.At(0));
            double len   = Math.Sqrt(vx * vx + vy * vy);
            return len < 1e-10 ? MakeVec(0, 0) : MakeVec(vx / len, vy / len);
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
    // Helper: wrap any Actor as a lightweight JS proxy object
    // =========================================================================

    /// <summary>
    /// Creates a minimal JS object proxy for <paramref name="actor"/> exposing
    /// name, tag, active, transform.{x,y}, and destroy().
    /// Used by Scene.find, Scene.findByTag, Scene.instantiate, and
    /// collision / trigger callbacks.
    /// </summary>
    internal JsValue WrapActorAsProxy(Actor actor)
    {
        if (actor == null) return JsValue.Null;

        var obj = NewObj();

        Accessor(obj, "name",
            getter: (_, _) => new JsString(actor.Name),
            setter: (_, args) => { actor.Name = args.At(0).ToString(); return JsValue.Undefined; },
            _engine);

        Accessor(obj, "tag",
            getter: (_, _) => new JsString(actor.Tag),
            setter: (_, args) => { actor.Tag = args.At(0).ToString(); return JsValue.Undefined; },
            _engine);

        Accessor(obj, "active",
            getter: (_, _) => actor.IsActive ? JsBoolean.True : JsBoolean.False,
            setter: (_, args) => { actor.IsActive = TypeConverter.ToBoolean(args.At(0)); return JsValue.Undefined; },
            _engine);

        // Nested transform sub-object with x/y accessors
        var tfObj = NewObj();
        var tf    = actor.Transform;

        Accessor(tfObj, "x",
            getter: (_, _) => new JsNumber(tf.Position.X),
            setter: (_, args) => { tf.Position = new Vector2((float)args.At(0).AsNumber(), tf.Position.Y); return JsValue.Undefined; },
            _engine);

        Accessor(tfObj, "y",
            getter: (_, _) => new JsNumber(tf.Position.Y),
            setter: (_, args) => { tf.Position = new Vector2(tf.Position.X, (float)args.At(0).AsNumber()); return JsValue.Undefined; },
            _engine);

        obj.Set("transform", tfObj);

        obj.Set("destroy", Fn("destroy", (_, _) =>
        {
            actor.Destroy();
            return JsValue.Undefined;
        }));

        return obj;
    }
}
