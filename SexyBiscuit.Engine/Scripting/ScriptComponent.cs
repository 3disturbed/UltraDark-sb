using Jint.Native;
using Jint.Runtime;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Scripting;

/// <summary>
/// A <see cref="Component"/> that attaches a JavaScript file to an <see cref="Actor"/>.
///
/// The JS file may define any or all of the following lifecycle functions:
/// <list type="bullet">
///   <item><c>onAwake()</c></item>
///   <item><c>onStart()</c></item>
///   <item><c>onUpdate(dt)</c></item>
///   <item><c>onFixedUpdate(dt)</c></item>
///   <item><c>onLateUpdate(dt)</c></item>
///   <item><c>onDestroy()</c></item>
///   <item><c>onCollisionEnter/Stay/Exit(data)</c> — data = <c>{ other, contactPoint:{x,y}, normal:{x,y}, relativeVelocity, tag, name }</c></item>
///   <item><c>onTriggerEnter/Stay/Exit(other)</c> — other = actor proxy object</item>
/// </list>
///
/// JavaScript errors are caught and reported through <see cref="ScriptDiagnostics"/>; they never crash the engine.
/// </summary>
public sealed class ScriptComponent : Component
{
    // -------------------------------------------------------------------------
    // Public state
    // -------------------------------------------------------------------------

    /// <summary>
    /// Path to the <c>.js</c> script file to run, relative to the project root.
    /// Example: <c>"Scripts/EnemyAI.js"</c>
    /// </summary>
    /// <remarks>
    /// Changing it on an attached component reloads the script immediately: the scene loader
    /// attaches first and sets properties second, and the editor's tools do the same, so a
    /// script that only ran when the path was set before attach never ran at all. When the actor
    /// is not yet in a scene the load waits for <see cref="Start"/>, so <c>onAwake</c> can use
    /// <c>Scene.*</c> — the order the browser runtime uses.
    /// </remarks>
    public string ScriptPath
    {
        get => _scriptPath;
        set
        {
            value ??= string.Empty;
            if (_scriptPath == value) return;
            _scriptPath = value;

            if (Actor == null || Actor.Scene == null) return;

            InitialiseRuntime();
            TryCall("onAwake");
            _awoken = true;
            if (_started) TryCall("onStart");
        }
    }
    private string _scriptPath = string.Empty;
    private bool   _started;
    private bool   _awoken;

    // invoke() calls made before the script loaded — a spawner configuring a script it has
    // just attached to an actor that is not yet in a scene. Replayed after onAwake, before onStart.
    private List<(string Function, JsValue[] Args)>? _pendingInvokes;

    /// <summary>
    /// The active Jint runtime for this component. Null until the script has loaded
    /// successfully. Replaced with a fresh instance on each hot-reload.
    /// </summary>
    public JintRuntime? Runtime { get; private set; }

    /// <summary>The last load error, or null when the script loaded. Surfaced by the editor's inspector.</summary>
    public string? Error { get; private set; }

    // -------------------------------------------------------------------------
    // Lifecycle — Component overrides
    // -------------------------------------------------------------------------

    public override void Awake()
    {
        // No path yet is the normal case for a component restored from a scene file; the
        // setter picks it up a moment later. No scene yet means the actor is still being
        // built, and Start is the first moment Scene.* can answer.
        if (string.IsNullOrWhiteSpace(_scriptPath) || Actor.Scene == null) return;

        InitialiseRuntime();
        TryCall("onAwake");
        _awoken = true;
    }

    public override void Start()
    {
        _started = true;

        if (Runtime == null && !string.IsNullOrWhiteSpace(_scriptPath))
        {
            InitialiseRuntime();
            if (Runtime != null && !_awoken)
            {
                TryCall("onAwake");
                _awoken = true;
            }
        }

        ReplayPendingInvokes();
        SubscribeToNetwork();
        TryCall("onStart");
    }

    public override void Update(float dt)
    {
        // A session may start after this component did -- a lobby script calls
        // Network.startServer from onUpdate -- so the subscription is retried until it
        // takes. It costs one null check per frame on a script that has no such hook.
        if (!_networkSubscribed) SubscribeToNetwork();
        TryCall("onUpdate", dt);
    }

    public override void FixedUpdate(float dt)
        => TryCall("onFixedUpdate", dt);

    public override void LateUpdate(float dt)
        => TryCall("onLateUpdate", dt);

    /// <summary>
    /// Subscribes to the message channel, but only for a script that declares the hook.
    /// </summary>
    /// <remarks>
    /// Subscribing unconditionally would put every scripted actor in the scene on a handler
    /// that does nothing, and a message-heavy game has hundreds. Called from Start, which is
    /// after the script has loaded and its functions are known.
    /// </remarks>
    private void SubscribeToNetwork()
    {
        if (_networkSubscribed || Runtime == null || !Runtime.HasFunction("onNetworkMessage")) return;

        var manager = Networking.NetworkManager.Instance;
        if (manager == null) return;

        _onNetworkMessage = (sender, type, payload) =>
        {
            if (Runtime == null) return;
            Runtime.CallFunctionWithJsArgs("onNetworkMessage",
                new Jint.Native.JsString(type),
                Runtime.Bridge.JsonToScript(payload),
                new Jint.Native.JsNumber(sender));
        };

        manager.OnMessage += _onNetworkMessage;
        _networkSubscribed = true;
    }

    private bool _networkSubscribed;
    private Action<int, string, System.Text.Json.Nodes.JsonNode?>? _onNetworkMessage;

    public override void OnDestroy()
    {
        if (_onNetworkMessage != null && Networking.NetworkManager.Instance is { } manager)
            manager.OnMessage -= _onNetworkMessage;
        _onNetworkMessage = null;
        _networkSubscribed = false;

        TryCall("onDestroy");

        // The script's own network handlers go with it. Without this a destroyed actor's
        // Network.on callback keeps firing into a dead Jint object every time a message
        // arrives, which is a leak that only shows up as a growing stall.
        Runtime?.Bridge.DisposeNetwork();
        Runtime?.Bridge.DisposeDarksGames();
    }

    public override void OnCollisionEnter(CollisionData data) => DispatchCollision("onCollisionEnter", data);
    public override void OnCollisionStay(CollisionData data)  => DispatchCollision("onCollisionStay",  data);
    public override void OnCollisionExit(CollisionData data)  => DispatchCollision("onCollisionExit",  data);
    public override void OnTriggerEnter(Actor other)          => DispatchTrigger("onTriggerEnter", other);
    public override void OnTriggerStay(Actor other)           => DispatchTrigger("onTriggerStay",  other);
    public override void OnTriggerExit(Actor other)           => DispatchTrigger("onTriggerExit",  other);

    // -------------------------------------------------------------------------
    // Cross-script calls
    // -------------------------------------------------------------------------

    /// <summary>
    /// Calls a top-level function the script defines and returns its result, or <c>undefined</c>
    /// when there is no such function or it throws. This is what another script reaches through
    /// <c>getComponent("ScriptComponent").invoke(name, ...args)</c>.
    /// </summary>
    public JsValue Invoke(string function, params JsValue[] args)
    {
        if (Runtime == null)
        {
            // Not loaded yet but about to be (the actor has no scene, so the load waits for
            // Start): keep the call, as the browser runtime does while it fetches the file.
            if (!string.IsNullOrWhiteSpace(_scriptPath) && Actor != null && Actor.Scene == null)
                (_pendingInvokes ??= new()).Add((function, args));
            return JsValue.Undefined;
        }

        try
        {
            return Runtime.Invoke(function, args);
        }
        catch (JavaScriptException ex)
        {
            LogScriptError(function, ex.Message);
        }
        catch (ExecutionCanceledException)
        {
            LogScriptError(function, "Execution cancelled — statement limit exceeded (100 000).");
        }
        catch (Exception ex)
        {
            LogScriptError(function, ex.Message);
        }

        return JsValue.Undefined;
    }

    // -------------------------------------------------------------------------
    // Hot-reload entry point
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called by <see cref="ScriptHotReload"/> to reload the script in-place.
    /// Creates a fresh <see cref="JintRuntime"/> from the updated source file, then
    /// re-fires <c>onStart()</c> so the script can re-initialise its state.
    /// </summary>
    public void Reload()
    {
        InitialiseRuntime();

        // The component is already past Awake/Start — re-fire onStart so the
        // script can re-initialise any state that was set there.
        TryCall("onStart");
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private void ReplayPendingInvokes()
    {
        if (_pendingInvokes == null) return;
        var pending = _pendingInvokes;
        _pendingInvokes = null;
        foreach (var (function, args) in pending) Invoke(function, args);
    }

    private void DispatchCollision(string hook, CollisionData data)
    {
        if (Runtime == null || !Runtime.HasFunction(hook)) return;

        try
        {
            Runtime.CallFunctionWithJsArgs(hook, Runtime.Bridge.WrapCollisionData(data));
        }
        catch (JavaScriptException ex)
        {
            LogScriptError(hook, ex.Message);
        }
        catch (Exception ex)
        {
            LogScriptError(hook, ex.Message);
        }
    }

    private void DispatchTrigger(string hook, Actor other)
    {
        if (Runtime == null || !Runtime.HasFunction(hook)) return;

        try
        {
            Runtime.CallFunctionWithJsArgs(hook, Runtime.Bridge.WrapActorAsProxy(other));
        }
        catch (JavaScriptException ex)
        {
            LogScriptError(hook, ex.Message);
        }
        catch (Exception ex)
        {
            LogScriptError(hook, ex.Message);
        }
    }

    private void InitialiseRuntime()
    {
        Error = null;

        if (string.IsNullOrWhiteSpace(ScriptPath))
        {
            Runtime = null;
            return;
        }

        // Read the JS source. A missing script is reported, never thrown.
        string source;
        try
        {
            source = File.ReadAllText(ProjectPaths.Resolve(ScriptPath));
        }
        catch (Exception ex)
        {
            Fail(null, $"could not read file — {ex.Message}");
            return;
        }

        // Construct a fresh runtime. JintRuntime creates its own Jint.Engine
        // and ScriptBridge, ensuring they always share the same engine instance.
        try
        {
            var runtime = new JintRuntime(Actor);
            runtime.Bridge.ScriptPath = ScriptPath;
            runtime.LoadScript(source);
            Runtime = runtime;
        }
        catch (JavaScriptException ex)
        {
            // Parse / execution error in the script itself.
            Fail("load", ex.Message);
        }
        catch (Exception ex)
        {
            Fail("load", ex.Message);
        }
    }

    private void Fail(string? hook, string message)
    {
        Runtime = null;
        Error   = message;
        ScriptDiagnostics.Report(ScriptDiagnosticLevel.Error, ScriptPath, hook, message);
    }

    /// <summary>
    /// Safely calls a lifecycle function by name, catching and reporting any
    /// JS or CLR exception without crashing the engine.
    /// </summary>
    private void TryCall(string functionName, params object[] args)
    {
        if (Runtime == null) return;

        try
        {
            Runtime.CallFunction(functionName, args);
        }
        catch (JavaScriptException ex)
        {
            LogScriptError(functionName, ex.Message);
        }
        catch (ExecutionCanceledException)
        {
            // MaxStatements limit was reached — report and continue.
            LogScriptError(functionName, "Execution cancelled — statement limit exceeded (100 000).");
        }
        catch (Exception ex)
        {
            LogScriptError(functionName, ex.Message);
        }
    }

    private void LogScriptError(string functionName, string message)
        => ScriptDiagnostics.Report(ScriptDiagnosticLevel.Error, ScriptPath, functionName, message);
}
