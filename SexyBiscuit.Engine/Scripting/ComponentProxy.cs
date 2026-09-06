using System.Reflection;
using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;
using SexyBiscuit.Engine.Physics;

namespace SexyBiscuit.Engine.Scripting;

/// <summary>
/// The object a script gets back from <c>actor.getComponent("Rigidbody2D")</c>.
/// </summary>
/// <remarks>
/// Only the component's schema-declared properties are reachable — the same set the editor's
/// inspector and the MCP tools edit — under both their C# name and its camelCase form, so
/// <c>rb.GravityScale</c> and <c>rb.gravityScale</c> are one property. That keeps a script from
/// reaching into engine internals, which is the intent of Jint's "no CLR access by default",
/// and matches what the browser's <c>wrapComponent</c> exposes. Public methods whose parameters
/// the value converter understands are callable too. Rigidbody2D gains the
/// <c>velocityX</c>/<c>velocityY</c>/<c>velocity</c> shorthands the bundled scripts use, and a
/// ScriptComponent gains <c>invoke(name, ...args)</c> for cross-script calls.
/// </remarks>
internal static class ComponentProxy
{
    private static readonly HashSet<string> LifecycleNames = new(StringComparer.Ordinal)
    {
        "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "Draw", "OnDestroy",
        "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit",
        "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit",
        "GetComponent", "AddComponent", "Equals", "GetHashCode", "GetType", "ToString",
    };

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------

    /// <summary>The proxy for <paramref name="component"/> on <paramref name="bridge"/>'s engine, built once and cached.</summary>
    internal static ObjectInstance Wrap(Component component, ScriptBridge bridge)
    {
        if (bridge.ComponentProxies.TryGetValue(component, out var existing)) return existing;

        var proxy = Build(component, bridge);
        bridge.ComponentProxies.AddOrUpdate(component, proxy);
        return proxy;
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    private static ObjectInstance Build(Component component, ScriptBridge bridge)
    {
        var engine = bridge.JsEngine;
        var type   = component.GetType();
        var obj    = JsValueConverter.NewObject(engine);

        // ---- Identity ----------------------------------------------------------
        ScriptBridge.Accessor(obj, "type",  (_, _) => new JsString(type.Name), null, engine);
        ScriptBridge.Accessor(obj, "actor", (_, _) => bridge.WrapActorAsProxy(component.Actor), null, engine);

        DefineBoth(obj, "Enabled",
            getter: (_, _) => component.Enabled ? JsBoolean.True : JsBoolean.False,
            setter: (_, args) => { component.Enabled = TypeConverter.ToBoolean(args.At(0)); return JsValue.Undefined; },
            engine);

        // ---- Properties, in both spellings -------------------------------------
        foreach (var property in ComponentReflection.EditableProperties(type))
        {
            var p = property;
            DefineBoth(obj, p.Name,
                getter: (_, _) => JsValueConverter.ToJs(engine, p.GetValue(component), bridge),
                setter: (_, args) => { p.SetValue(component, JsValueConverter.ToClr(args.At(0), p.PropertyType)); return JsValue.Undefined; },
                engine);
        }

        // Read-only properties of supported types are still worth reading: a collider's
        // world bounds, a body's IsAwake, a renderer's frame count.
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            if (property.Name is "Actor" || obj.HasOwnProperty(property.Name)) continue;
            if (!ValueConverter.IsSupportedType(property.PropertyType)) continue;

            var p = property;
            DefineBoth(obj, p.Name,
                getter: (_, _) =>
                {
                    try { return JsValueConverter.ToJs(engine, p.GetValue(component), bridge); }
                    catch (TargetInvocationException) { return JsValue.Undefined; }
                },
                setter: null,
                engine);
        }

        // ---- Methods -------------------------------------------------------------
        foreach (var group in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                  .Where(IsScriptable)
                                  .GroupBy(m => m.Name))
        {
            var overloads = group.OrderBy(m => m.GetParameters().Length).ToArray();
            string name   = group.Key;

            JsValue Call(JsValue thisObj, JsValue[] args)
            {
                var method = overloads.FirstOrDefault(m => m.GetParameters().Length == args.Length)
                          ?? overloads.FirstOrDefault(m => m.GetParameters().Length >= args.Length)
                          ?? overloads[0];

                var parameters = method.GetParameters();
                var clrArgs    = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    clrArgs[i] = i < args.Length && !args[i].IsUndefined()
                        ? JsValueConverter.ToClr(args[i], parameters[i].ParameterType)
                        : parameters[i].HasDefaultValue ? parameters[i].DefaultValue : DefaultOf(parameters[i].ParameterType);
                }

                try
                {
                    return JsValueConverter.ToJs(engine, method.Invoke(component, clrArgs), bridge);
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw ex.InnerException;
                }
            }

            var fn = new ClrFunction(engine, name, Call, overloads[0].GetParameters().Length);
            obj.Set(name, fn);
            string camel = JsValueConverter.LowerFirst(name);
            if (camel != name && !obj.HasOwnProperty(camel)) obj.Set(camel, fn);
        }

        // ---- Shorthands the bundled scripts rely on --------------------------------
        if (component is Rigidbody2D body)
        {
            ScriptBridge.Accessor(obj, "velocityX",
                (_, _) => new JsNumber(body.LinearVelocity.X),
                (_, args) => { body.LinearVelocity = new Vector2(JsValueConverter.Number(args.At(0)), body.LinearVelocity.Y); return JsValue.Undefined; },
                engine);
            ScriptBridge.Accessor(obj, "velocityY",
                (_, _) => new JsNumber(body.LinearVelocity.Y),
                (_, args) => { body.LinearVelocity = new Vector2(body.LinearVelocity.X, JsValueConverter.Number(args.At(0))); return JsValue.Undefined; },
                engine);
            ScriptBridge.Accessor(obj, "velocity",
                (_, _) => JsValueConverter.ToJs(engine, body.LinearVelocity),
                (_, args) => { body.LinearVelocity = JsValueConverter.ReadVector2(args.At(0)); return JsValue.Undefined; },
                engine);
        }

        if (component is ScriptComponent script)
        {
            // rb.getComponent("ScriptComponent").invoke("takeDamage", 5): call any top-level
            // function the other script defines. `call` is the same thing under the name the
            // browser bridge first shipped with.
            JsValue Invoke(JsValue thisObj, JsValue[] args)
            {
                if (args.Length == 0) return JsValue.Undefined;
                return script.Invoke(args[0].ToString(), args.Skip(1).ToArray());
            }
            var invoke = new ClrFunction(engine, "invoke", Invoke, 1);
            obj.Set("invoke", invoke);
            obj.Set("call",   invoke);
        }

        return obj;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Defines an accessor under the C# name and, when it differs, the camelCase name.</summary>
    private static void DefineBoth(
        ObjectInstance target,
        string name,
        Func<JsValue, JsValue[], JsValue> getter,
        Func<JsValue, JsValue[], JsValue>? setter,
        Jint.Engine engine)
    {
        ScriptBridge.Accessor(target, name, getter, setter, engine);

        string camel = JsValueConverter.LowerFirst(name);
        if (camel != name && !target.HasOwnProperty(camel))
            ScriptBridge.Accessor(target, camel, getter, setter, engine);
    }

    private static bool IsScriptable(MethodInfo method)
    {
        if (method.IsSpecialName || method.IsGenericMethod || method.IsStatic) return false;
        if (method.DeclaringType == typeof(object) || method.DeclaringType == typeof(Component)) return false;
        if (LifecycleNames.Contains(method.Name)) return false;

        var parameters = method.GetParameters();
        if (parameters.Length > 4) return false;
        if (parameters.Any(p => p.IsOut || p.ParameterType.IsByRef || !ValueConverter.IsSupportedType(p.ParameterType)))
            return false;

        var returns = method.ReturnType;
        return returns == typeof(void)
            || ValueConverter.IsSupportedType(returns)
            || typeof(Actor).IsAssignableFrom(returns)
            || typeof(Component).IsAssignableFrom(returns);
    }

    private static object? DefaultOf(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;
}
