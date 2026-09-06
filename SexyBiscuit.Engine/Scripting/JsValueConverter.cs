using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;

using JintEngine = Jint.Engine;

namespace SexyBiscuit.Engine.Scripting;

/// <summary>
/// Converts between Jint values and the CLR values component properties hold.
/// </summary>
/// <remarks>
/// The MCP tools already accept every lenient spelling of a value from JSON — <c>[x, y]</c> or
/// <c>{x, y}</c> for a vector, <c>"#FF8040"</c> or <c>{R, G, B, A}</c> for a colour, an enum by
/// name — through <see cref="ValueConverter"/>. A script assigning <c>rb.linearVelocity = {x: 1, y: 0}</c>
/// wants exactly that leniency, so a JS value is turned into JSON and handed to the same code
/// rather than growing a second converter. Going the other way, vectors become <c>{x, y}</c>
/// objects rather than arrays because scripts read <c>.x</c>.
/// </remarks>
internal static class JsValueConverter
{
    // -------------------------------------------------------------------------
    // JS -> JSON -> CLR
    // -------------------------------------------------------------------------

    /// <summary>A JSON tree with the same shape as <paramref name="value"/>. Undefined and null both become null.</summary>
    internal static JsonNode? ToJsonNode(JsValue value)
    {
        if (value.IsUndefined() || value.IsNull()) return null;
        if (value.IsBoolean()) return JsonValue.Create(value.AsBoolean());
        if (value.IsNumber())  return JsonValue.Create(value.AsNumber());
        if (value.IsString())  return JsonValue.Create(value.AsString());

        if (value is ArrayInstance)
        {
            var obj   = value.AsObject();
            var array = new JsonArray();
            int length = (int)obj.Get("length").AsNumber();
            for (int i = 0; i < length; i++)
                array.Add(ToJsonNode(obj.Get(i.ToString())));
            return array;
        }

        if (value.IsObject())
        {
            var obj    = value.AsObject();
            var result = new JsonObject();
            foreach (var pair in obj.GetOwnProperties())
            {
                if (!pair.Value.Enumerable) continue;
                result[pair.Key.ToString()] = ToJsonNode(obj.Get(pair.Key));
            }
            return result;
        }

        return JsonValue.Create(value.ToString());
    }

    /// <summary>
    /// Converts a JS value to <paramref name="targetType"/>, with every leniency the MCP tools have.
    /// Throws <see cref="InvalidOperationException"/> with the converter's message when it cannot; Jint
    /// surfaces that to the script as a JavaScript error.
    /// </summary>
    internal static object? ToClr(JsValue value, Type targetType)
    {
        if (targetType == typeof(JsValue)) return value;
        if (targetType == typeof(object))  return ToJsonNode(value);

        var node = ToJsonNode(value);
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        var element = document.RootElement.Clone();

        if (ValueConverter.TryFromJson(element, targetType, out var converted, out var error))
            return converted;

        throw new InvalidOperationException(error ?? $"Cannot convert '{value}' to {ValueConverter.Describe(targetType)}.");
    }

    // -------------------------------------------------------------------------
    // CLR -> JS
    // -------------------------------------------------------------------------

    /// <summary>
    /// A JS value for <paramref name="value"/>. Vectors become <c>{x, y[, z[, w]]}</c>, colours
    /// <c>{r, g, b, a}</c> (0-255, as the browser's Color reads), enums their name, actors and
    /// components their script proxies.
    /// </summary>
    internal static JsValue ToJs(JintEngine engine, object? value, ScriptBridge? bridge = null)
    {
        switch (value)
        {
            case null:            return JsValue.Null;
            case JsValue js:      return js;
            case bool b:          return b ? JsBoolean.True : JsBoolean.False;
            case string s:        return new JsString(s);
            case Enum e:          return new JsString(e.ToString());
            case float f:         return new JsNumber(f);
            case double d:        return new JsNumber(d);
            case int i:           return new JsNumber(i);
            case long l:          return new JsNumber(l);
            case uint ui:         return new JsNumber(ui);
            case short sh:        return new JsNumber(sh);
            case ushort us:       return new JsNumber(us);
            case byte by:         return new JsNumber(by);
            case sbyte sb:        return new JsNumber(sb);
            case ulong ul:        return new JsNumber(ul);
            case decimal m:       return new JsNumber((double)m);
            case Vector2 v2:      return Vector(engine, ("x", v2.X), ("y", v2.Y));
            case Vector3 v3:      return Vector(engine, ("x", v3.X), ("y", v3.Y), ("z", v3.Z));
            case Vector4 v4:      return Vector(engine, ("x", v4.X), ("y", v4.Y), ("z", v4.Z), ("w", v4.W));
            case Quaternion q:    return Vector(engine, ("x", q.X), ("y", q.Y), ("z", q.Z), ("w", q.W));
            case Color c:         return Vector(engine, ("r", c.R), ("g", c.G), ("b", c.B), ("a", c.A));
            case Actor actor when bridge != null:         return bridge.WrapActorAsProxy(actor);
            case Component component when bridge != null: return ComponentProxy.Wrap(component, bridge);
            case JsonNode node:   return FromJsonNode(engine, node);
            case IEnumerable list:
            {
                var items = new List<JsValue>();
                foreach (var item in list) items.Add(ToJs(engine, item, bridge));
                return NewArray(engine, items);
            }
        }

        return FromJsonNode(engine, ValueConverter.ToJson(value));
    }

    /// <summary>A JS value with the same shape as a JSON tree.</summary>
    internal static JsValue FromJsonNode(JintEngine engine, JsonNode? node)
    {
        switch (node)
        {
            case null:
                return JsValue.Null;

            case JsonArray array:
                return NewArray(engine, array.Select(item => FromJsonNode(engine, item)));

            case JsonObject obj:
            {
                var result = NewObject(engine);
                foreach (var pair in obj)
                    result.Set(pair.Key, FromJsonNode(engine, pair.Value));
                return result;
            }

            case JsonValue leaf:
                if (leaf.TryGetValue<bool>(out var b))     return b ? JsBoolean.True : JsBoolean.False;
                if (leaf.TryGetValue<double>(out var d))   return new JsNumber(d);
                if (leaf.TryGetValue<string>(out var s))   return new JsString(s);
                return new JsString(leaf.ToJsonString());
        }

        return JsValue.Undefined;
    }

    // -------------------------------------------------------------------------
    // Allocation helpers
    // -------------------------------------------------------------------------

    /// <summary>A fresh plain object on the engine's heap.</summary>
    internal static ObjectInstance NewObject(JintEngine engine)
        => engine.Evaluate("({})").AsObject();

    /// <summary>A fresh array holding <paramref name="items"/>.</summary>
    internal static JsValue NewArray(JintEngine engine, IEnumerable<JsValue> items)
    {
        // ArrayInstance keeps its length in step when numeric keys are set.
        var array = engine.Evaluate("[]").AsObject();
        int index = 0;
        foreach (var item in items) array.Set((index++).ToString(), item);
        return array;
    }

    private static JsValue Vector(JintEngine engine, params (string Name, float Value)[] parts)
    {
        var obj = NewObject(engine);
        foreach (var (name, value) in parts) obj.Set(name, new JsNumber(value));
        return obj;
    }

    /// <summary>Reads a <c>{x, y}</c> object or an <c>[x, y]</c> array; anything else is the origin.</summary>
    internal static Vector2 ReadVector2(JsValue value)
    {
        if (value is ArrayInstance)
        {
            var array = value.AsObject();
            return new Vector2(Number(array.Get("0")), Number(array.Get("1")));
        }
        if (value.IsObject())
        {
            var obj = value.AsObject();
            return new Vector2(Number(obj.Get("x")), Number(obj.Get("y")));
        }
        return Vector2.Zero;
    }

    /// <summary>A number, or 0 for anything that is not one.</summary>
    internal static float Number(JsValue value)
        => value.IsNumber() ? (float)value.AsNumber() : 0f;

    /// <summary>The camelCase spelling of a PascalCase member name: "LinearVelocity" to "linearVelocity".</summary>
    internal static string LowerFirst(string name)
        => string.IsNullOrEmpty(name) || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
