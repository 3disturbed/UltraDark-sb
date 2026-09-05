using System.Diagnostics;
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
///   <item><c>onCollisionEnter(data)</c> — data = <c>{ other, contactPoint:{x,y}, normal:{x,y}, relativeVelocity }</c></item>
///   <item><c>onTriggerEnter(other)</c> — other = actor proxy object</item>
/// </list>
///
/// JavaScript errors are caught and written to the diagnostic log; they never crash the engine.
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
    /// script that only ran when the path was set before attach never ran at all.
    /// </remarks>
    public string ScriptPath
    {
        get => _scriptPath;
        set
        {
            value ??= string.Empty;
            if (_scriptPath == value) return;
            _scriptPath = value;

            if (Actor == null) return;

            InitialiseRuntime();
            TryCall("onAwake");
            if (_started) TryCall("onStart");
        }
    }
    private string _scriptPath = string.Empty;
    private bool   _started;

    /// <summary>
    /// The active Jint runtime for this component. Null until <see cref="Awake"/> completes
    /// successfully. Replaced with a fresh instance on each hot-reload.
    /// </summary>
    public JintRuntime? Runtime { get; private set; }

    // -------------------------------------------------------------------------
    // Lifecycle — Component overrides
    // -------------------------------------------------------------------------

    public override void Awake()
    {
        // No path yet is the normal case for a component restored from a scene file; the
        // setter picks it up a moment later.
        if (string.IsNullOrWhiteSpace(_scriptPath)) return;

        InitialiseRuntime();
        TryCall("onAwake");
    }

    public override void Start()
    {
        _started = true;
        if (Runtime == null && !string.IsNullOrWhiteSpace(_scriptPath)) InitialiseRuntime();
        TryCall("onStart");
    }

    public override void Update(float dt)
        => TryCall("onUpdate", dt);

    public override void FixedUpdate(float dt)
        => TryCall("onFixedUpdate", dt);

    public override void LateUpdate(float dt)
        => TryCall("onLateUpdate", dt);

    public override void OnDestroy()
        => TryCall("onDestroy");

    public override void OnCollisionEnter(CollisionData data)
    {
        if (Runtime == null || !Runtime.HasFunction("onCollisionEnter")) return;

        try
        {
            // Build a structured JS data object so the script never needs to
            // know about any C# types.
            var dataObj = Runtime.NewObject();

            // other — wrap the colliding actor as a proxy
            var otherProxy = Runtime.Bridge.WrapActorAsProxy(data.Other);
            dataObj.Set("other", otherProxy);

            // contactPoint — {x, y}
            var cpObj = Runtime.NewObject();
            cpObj.Set("x", new JsNumber(data.ContactPoint.X));
            cpObj.Set("y", new JsNumber(data.ContactPoint.Y));
            dataObj.Set("contactPoint", cpObj);

            // normal — {x, y}
            var nObj = Runtime.NewObject();
            nObj.Set("x", new JsNumber(data.Normal.X));
            nObj.Set("y", new JsNumber(data.Normal.Y));
            dataObj.Set("normal", nObj);

            // relativeVelocity
            dataObj.Set("relativeVelocity", new JsNumber(data.RelativeVelocity));

            Runtime.CallFunctionWithJsArgs("onCollisionEnter", dataObj);
        }
        catch (JavaScriptException ex)
        {
            LogScriptError("onCollisionEnter", ex.Message);
        }
        catch (Exception ex)
        {
            LogScriptError("onCollisionEnter", ex.Message);
        }
    }

    public override void OnTriggerEnter(Actor other)
    {
        if (Runtime == null || !Runtime.HasFunction("onTriggerEnter")) return;

        try
        {
            var otherProxy = Runtime.Bridge.WrapActorAsProxy(other);
            Runtime.CallFunctionWithJsArgs("onTriggerEnter", otherProxy);
        }
        catch (JavaScriptException ex)
        {
            LogScriptError("onTriggerEnter", ex.Message);
        }
        catch (Exception ex)
        {
            LogScriptError("onTriggerEnter", ex.Message);
        }
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

    private void InitialiseRuntime()
    {
        if (string.IsNullOrWhiteSpace(ScriptPath))
        {
            Runtime = null;
            return;
        }

        // Read the JS source. Surface file-system errors as debug log entries
        // so a missing script does not crash the engine.
        string source;
        try
        {
            source = File.ReadAllText(Core.ProjectPaths.Resolve(ScriptPath));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Script Error] {ScriptPath}: could not read file — {ex.Message}");
            Runtime = null;
            return;
        }

        // Construct a fresh runtime. JintRuntime creates its own Jint.Engine
        // and ScriptBridge, ensuring they always share the same engine instance.
        try
        {
            Runtime = new JintRuntime(Actor);
            Runtime.LoadScript(source);
        }
        catch (JavaScriptException ex)
        {
            // Parse / execution error in the script itself.
            System.Diagnostics.Debug.WriteLine($"[Script Error] {ScriptPath}: {ex.Message}");
            Runtime = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Script Error] {ScriptPath}: {ex.Message}");
            Runtime = null;
        }
    }

    /// <summary>
    /// Safely calls a lifecycle function by name, catching and logging any
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
            // MaxStatements limit was reached — log and continue.
            LogScriptError(functionName, "Execution cancelled — statement limit exceeded (100 000).");
        }
        catch (Exception ex)
        {
            LogScriptError(functionName, ex.Message);
        }
    }

    private void LogScriptError(string functionName, string message)
        => System.Diagnostics.Debug.WriteLine($"[Script Error] {ScriptPath} ({functionName}): {message}");
}
