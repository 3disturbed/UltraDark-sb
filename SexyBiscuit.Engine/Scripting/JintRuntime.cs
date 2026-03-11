using System.Diagnostics;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using SexyBiscuit.Engine.Core;

using JintEngine = Jint.Engine;

namespace SexyBiscuit.Engine.Scripting;

/// <summary>
/// Wraps a single <see cref="Jint.Engine"/> instance for one <see cref="ScriptComponent"/>.
///
/// <para>
/// Owns the Jint engine, constructs the <see cref="ScriptBridge"/> for that engine,
/// and registers all engine-API globals. This ensures the bridge and the runtime
/// always share the same Jint engine instance.
/// </para>
///
/// <para>
/// Usage:
/// <code>
///   var runtime = new JintRuntime(actor);
///   runtime.LoadScript(File.ReadAllText("Scripts/Enemy.js"));
///   runtime.CallFunction("onUpdate", 0.016f);
/// </code>
/// </para>
/// </summary>
public sealed class JintRuntime
{
    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------
    private readonly JintEngine _engine;
    private readonly Actor  _actor;

    /// <summary>
    /// The bridge that maps C# engine API onto JS globals.
    /// Exposed so <see cref="ScriptComponent"/> can call
    /// <see cref="ScriptBridge.WrapActorAsProxy"/> for collision callbacks.
    /// </summary>
    public ScriptBridge Bridge { get; }

    // Cached set of lifecycle function names confirmed to be callable in the
    // loaded script. Refreshed after every LoadScript call.
    private readonly HashSet<string> _definedFunctions = new(StringComparer.Ordinal);

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a new runtime for <paramref name="actor"/>. The Jint engine and
    /// script bridge are both constructed here so they share the same engine instance.
    /// </summary>
    public JintRuntime(Actor actor)
    {
        _actor  = actor ?? throw new ArgumentNullException(nameof(actor));

        _engine = new JintEngine(options =>
        {
            // Deny all arbitrary CLR type access from JS code.
            // CLR access is denied by default — no AllowClr() call needed.

            // Surface CLR exceptions as JS exceptions rather than propagating them
            // directly. ScriptComponent.TryCall catches JavaScriptException.
            options.CatchClrExceptions();

            // Hard statement cap per Invoke() call — prevents infinite JS loops
            // from stalling the game loop.
            options.MaxStatements(100_000);

            // Sane recursion limit to prevent stack overflows in deeply nested JS.
            options.LimitRecursion(512);
        });

        // ScriptBridge receives the engine it should use to allocate objects.
        Bridge = new ScriptBridge(_actor, _engine);

        RegisterGlobals();
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses and executes <paramref name="jsSource"/> inside this engine instance.
    /// After execution, the set of defined lifecycle functions is refreshed so that
    /// <see cref="HasFunction"/> returns accurate results with zero per-frame cost.
    /// </summary>
    public void LoadScript(string jsSource)
    {
        if (jsSource == null) throw new ArgumentNullException(nameof(jsSource));

        _engine.Execute(jsSource);
        RefreshFunctionCache();
    }

    /// <summary>
    /// Invokes a JS function by name, forwarding <paramref name="args"/> as arguments.
    /// Guards with <see cref="HasFunction"/> so Jint never throws for undefined functions.
    /// Does nothing silently if the function is not defined.
    /// </summary>
    public void CallFunction(string name, params object[] args)
    {
        if (!HasFunction(name)) return;

        // Convert C# arguments to JsValue so Jint does not attempt CLR marshalling
        // of engine types it cannot access.
        var jsArgs = new JsValue[args.Length];
        for (int i = 0; i < args.Length; i++)
            jsArgs[i] = JsValue.FromObject(_engine, args[i]);

        _engine.Invoke(name, jsArgs);
    }

    /// <summary>
    /// Invokes a JS function by name with pre-converted <see cref="JsValue"/> arguments.
    /// Intended for internal use by <see cref="ScriptComponent"/> when constructing
    /// complex argument objects (e.g. collision data) that are already JsValues.
    /// </summary>
    public void CallFunctionWithJsArgs(string name, params JsValue[] args)
    {
        if (!HasFunction(name)) return;
        _engine.Invoke(name, args);
    }

    /// <summary>
    /// Returns true if a callable function with <paramref name="name"/> is defined
    /// at the global scope of the loaded script.
    /// </summary>
    public bool HasFunction(string name)
        => _definedFunctions.Contains(name);

    /// <summary>
    /// Evaluates an arbitrary JS expression and returns its result as a
    /// <see cref="JsValue"/>. Intended for REPL / editor tooling. Returns null on error.
    /// </summary>
    public JsValue? Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return JsValue.Undefined;

        try
        {
            return _engine.Evaluate(expression);
        }
        catch (JavaScriptException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JintRuntime] Evaluate JS error: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[JintRuntime] Evaluate CLR error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Exposes a C# value or delegate as a named global inside the JS engine.
    /// Can be used by the hot-reload system or editor tooling to inject state
    /// before (re-)running a script.
    /// </summary>
    public void SetGlobal(string name, object value)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));

        _engine.SetValue(name, value);
    }

    /// <summary>
    /// Allocates a new empty plain JS object on this runtime's engine heap.
    /// Used by <see cref="ScriptComponent"/> to build structured argument objects
    /// (e.g. collision data) before passing them to JS callbacks.
    /// </summary>
    public ObjectInstance NewObject()
        => _engine.Evaluate("({})").AsObject();

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers all engine-API globals from the bridge onto the Jint engine.
    /// The bridge objects are plain JS <see cref="ObjectInstance"/> values;
    /// <c>SetValue</c> passes them through without CLR wrapping.
    /// </summary>
    private void RegisterGlobals()
    {
        _engine.SetValue("actor",     Bridge.ActorProxy);
        _engine.SetValue("transform", Bridge.TransformProxy);
        _engine.SetValue("Input",     Bridge.InputProxy);
        _engine.SetValue("Audio",     Bridge.AudioProxy);
        _engine.SetValue("Scene",     Bridge.SceneProxy);
        _engine.SetValue("Debug",     Bridge.DebugProxy);
        _engine.SetValue("Vector2",   Bridge.Vector2Proxy);
    }

    /// <summary>
    /// Scans the engine's global scope for the known lifecycle entry-point names
    /// and caches which ones are actually callable. Called once after each
    /// <see cref="LoadScript"/> so per-frame <see cref="HasFunction"/> checks are O(1).
    /// </summary>
    private void RefreshFunctionCache()
    {
        _definedFunctions.Clear();

        ReadOnlySpan<string> knownEntryPoints = new[]
        {
            "onAwake", "onStart", "onUpdate", "onFixedUpdate",
            "onLateUpdate", "onDestroy", "onCollisionEnter", "onTriggerEnter"
        };

        foreach (var name in knownEntryPoints)
        {
            try
            {
                var val = _engine.GetValue(name);
                if (!val.IsUndefined() && !val.IsNull() && val.IsObject())
                    _definedFunctions.Add(name);
            }
            catch
            {
                // Jint can throw for completely absent identifiers in strict mode.
                // Treat as not defined.
            }
        }
    }
}
