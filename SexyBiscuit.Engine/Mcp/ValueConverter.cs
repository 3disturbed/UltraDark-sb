using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// Converts between the JSON Claude sends and the engine's value types, in both directions.
/// </summary>
/// <remarks>
/// Reading is lenient about shape — a <see cref="Vector3"/> may arrive as <c>[x, y, z]</c> or
/// <c>{x, y, z}</c>, a <see cref="Quaternion"/> as <c>[pitch, yaw, roll]</c> degrees or
/// <c>[x, y, z, w]</c>, a <see cref="Color"/> as <c>"#RRGGBB"</c>, a colour name or a channel
/// object — and strict about meaning: a wrong enum name or a two-element vector for a 3D
/// position is an error with the valid alternatives spelled out. Writing produces the readable
/// forms: rotations as Euler degrees, colours as hex, floats rounded to four places.
/// </remarks>
public static class ValueConverter
{
    // -------------------------------------------------------------------------
    // JSON → value
    // -------------------------------------------------------------------------

    /// <summary>Converts, throwing an <see cref="McpToolException"/> with an actionable message on failure.</summary>
    public static object? FromJson(JsonElement element, Type targetType)
    {
        if (!TryFromJson(element, targetType, out var value, out var error))
            throw new McpToolException(error ?? $"Could not convert to {Describe(targetType)}.");
        return value;
    }

    public static bool TryFromJson(JsonElement element, Type targetType, out object? value, out string? error)
    {
        value = null;
        error = null;

        var underlying = Nullable.GetUnderlyingType(targetType);
        bool isNull = element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

        if (underlying != null)
        {
            if (isNull) return true;
            targetType = underlying;
        }

        if (isNull)
        {
            if (targetType == typeof(JsonElement)) { value = element; return true; }
            if (!targetType.IsValueType) return true;
            error = $"expected {Describe(targetType)} but got null";
            return false;
        }

        try
        {
            if (targetType == typeof(JsonElement)) { value = element; return true; }
            if (targetType == typeof(object))      { value = ToPlainObject(element); return true; }
            if (targetType == typeof(string))      { value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(); return true; }
            if (targetType == typeof(bool))        return TryBool(element, out value, out error);
            if (targetType.IsEnum)                 return TryEnum(element, targetType, out value, out error);
            if (IsNumeric(targetType))             return TryNumber(element, targetType, out value, out error);
            if (targetType == typeof(Vector2))     return TryVector(element, 2, out value, out error);
            if (targetType == typeof(Vector3))     return TryVector(element, 3, out value, out error);
            if (targetType == typeof(Vector4))     return TryVector(element, 4, out value, out error);
            if (targetType == typeof(Quaternion))  return TryQuaternion(element, out value, out error);
            if (targetType == typeof(Color))       return TryColor(element, out value, out error);

            if (targetType.IsArray && targetType.GetArrayRank() == 1)
                return TryArray(element, targetType.GetElementType()!, out value, out error);

            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
                return TryList(element, targetType.GetGenericArguments()[0], out value, out error);

            // Anything else (Material3D, DTOs) goes through System.Text.Json with the engine's
            // converters attached.
            value = JsonSerializer.Deserialize(element.GetRawText(), targetType, McpJson.Compact);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException
                                   or ArgumentException or InvalidCastException or NotSupportedException)
        {
            error = $"could not convert {Summarise(element)} to {Describe(targetType)}: {ex.Message}";
            return false;
        }
    }

    private static bool TryBool(JsonElement e, out object? value, out string? error)
    {
        value = null;
        error = null;

        switch (e.ValueKind)
        {
            case JsonValueKind.True:  value = true;  return true;
            case JsonValueKind.False: value = false; return true;
            case JsonValueKind.String when bool.TryParse(e.GetString(), out bool b): value = b; return true;
            case JsonValueKind.Number when e.TryGetInt32(out int i): value = i != 0; return true;
        }

        error = $"expected true or false but got {Summarise(e)}";
        return false;
    }

    private static bool TryEnum(JsonElement e, Type enumType, out object? value, out string? error)
    {
        value = null;
        error = null;

        if (e.ValueKind == JsonValueKind.String)
        {
            string text = e.GetString() ?? "";
            if (Enum.TryParse(enumType, text, ignoreCase: true, out var parsed) && Enum.IsDefined(enumType, parsed!))
            {
                value = parsed;
                return true;
            }
        }
        else if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long n))
        {
            var boxed = Enum.ToObject(enumType, n);
            if (Enum.IsDefined(enumType, boxed))
            {
                value = boxed;
                return true;
            }
        }

        error = $"{Summarise(e)} is not a {enumType.Name}. Valid values: {string.Join(", ", Enum.GetNames(enumType))}.";
        return false;
    }

    private static bool TryNumber(JsonElement e, Type type, out object? value, out string? error)
    {
        value = null;
        error = null;

        double d;
        if (e.ValueKind == JsonValueKind.Number)
        {
            d = e.GetDouble();
        }
        else if (e.ValueKind == JsonValueKind.String
              && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            d = parsed;
        }
        else
        {
            error = $"expected a number for {Describe(type)} but got {Summarise(e)}";
            return false;
        }

        if (IsIntegral(type) && Math.Abs(d - Math.Round(d)) > 1e-9)
        {
            error = $"expected a whole number for {Describe(type)} but got {d.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        value = type == typeof(decimal)
            ? (object)Convert.ToDecimal(d, CultureInfo.InvariantCulture)
            : Convert.ChangeType(d, type, CultureInfo.InvariantCulture);
        return true;
    }

    private static readonly string[] VectorNames = { "x", "y", "z", "w" };

    private static bool TryVector(JsonElement e, int count, out object? value, out string? error)
    {
        value = null;
        if (!TryFloats(e, count, count, VectorNames, out var f, out var detail))
        {
            error = $"expected a {count}-element vector {VectorHint(count)} but got {Summarise(e)}"
                  + (detail != null ? $" ({detail})" : "");
            return false;
        }

        error = null;
        value = count switch
        {
            2 => new Vector2(f[0], f[1]),
            3 => new Vector3(f[0], f[1], f[2]),
            _ => new Vector4(f[0], f[1], f[2], f[3]),
        };
        return true;
    }

    private static readonly string[] EulerNames = { "pitch", "yaw", "roll" };

    private static bool TryQuaternion(JsonElement e, out object? value, out string? error)
    {
        value = null;

        if (e.ValueKind == JsonValueKind.Array)
        {
            int n = e.GetArrayLength();
            if (n == 4 && TryFloats(e, 4, 4, VectorNames, out var q, out _))
            {
                value = new Quaternion(q[0], q[1], q[2], q[3]);
                error = null;
                return true;
            }
            if (n == 3 && TryFloats(e, 3, 3, EulerNames, out var euler, out _))
            {
                value = Transform3D.EulerToQuaternion(new Vector3(euler[0], euler[1], euler[2]));
                error = null;
                return true;
            }
        }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            if (HasProperty(e, "w") && TryFloats(e, 4, 4, VectorNames, out var q, out _))
            {
                value = new Quaternion(q[0], q[1], q[2], q[3]);
                error = null;
                return true;
            }

            var names = HasProperty(e, "pitch") || HasProperty(e, "yaw") || HasProperty(e, "roll") ? EulerNames : VectorNames;
            if (TryFloats(e, 3, 3, names, out var euler, out _, missingAsZero: true))
            {
                value = Transform3D.EulerToQuaternion(new Vector3(euler[0], euler[1], euler[2]));
                error = null;
                return true;
            }
        }

        error = $"expected a rotation as [pitch, yaw, roll] degrees or [x, y, z, w] but got {Summarise(e)}";
        return false;
    }

    private static readonly string[] ColorNames = { "r", "g", "b", "a" };
    private static readonly float[]  ColorDefaults = { 0f, 0f, 0f, 255f };

    private static bool TryColor(JsonElement e, out object? value, out string? error)
    {
        value = null;

        switch (e.ValueKind)
        {
            case JsonValueKind.String:
            {
                string text = (e.GetString() ?? "").Trim();
                if (TryParseHexColor(text, out var hex))
                {
                    value = hex;
                    error = null;
                    return true;
                }

                var prop = typeof(Color).GetProperty(text, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                if (prop != null && prop.PropertyType == typeof(Color))
                {
                    value = prop.GetValue(null);
                    error = null;
                    return true;
                }

                error = $"'{text}' is not a colour. Use '#RRGGBB', '#RRGGBBAA' or an XNA colour name such as CornflowerBlue.";
                return false;
            }

            case JsonValueKind.Array:
            case JsonValueKind.Object:
            {
                if (TryFloats(e, 3, 4, ColorNames, out var c, out _, missingAsZero: false, defaults: ColorDefaults))
                {
                    value = ChannelsToColor(c, e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 4);
                    error = null;
                    return true;
                }
                break;
            }
        }

        error = $"expected a colour ('#RRGGBB', a colour name, [r, g, b, a] or {{r, g, b, a}}) but got {Summarise(e)}";
        return false;
    }

    private static Color ChannelsToColor(float[] c, int provided)
    {
        // Integers are 0–255; a fractional component anywhere means the whole colour is 0–1.
        bool unit  = c.Take(provided).Any(v => v != MathF.Floor(v));
        float scale = unit ? 255f : 1f;

        byte r = (byte)Math.Clamp(MathF.Round(c[0] * scale), 0f, 255f);
        byte g = (byte)Math.Clamp(MathF.Round(c[1] * scale), 0f, 255f);
        byte b = (byte)Math.Clamp(MathF.Round(c[2] * scale), 0f, 255f);
        byte a = provided > 3 ? (byte)Math.Clamp(MathF.Round(c[3] * scale), 0f, 255f) : (byte)255;
        return new Color(r, g, b, a);
    }

    /// <summary>Parses "#RGB", "#RRGGBB", "#RRGGBBAA", with or without the hash.</summary>
    public static bool TryParseHexColor(string text, out Color color)
    {
        color = Color.White;
        string hex = text.StartsWith('#') ? text[1..] : text;
        if (hex.Length is not (3 or 6 or 8) || !hex.All(Uri.IsHexDigit)) return false;

        static byte Pair(char hi, char lo) => Convert.ToByte($"{hi}{lo}", 16);

        color = hex.Length switch
        {
            3 => new Color(Pair(hex[0], hex[0]), Pair(hex[1], hex[1]), Pair(hex[2], hex[2]), (byte)255),
            6 => new Color(Pair(hex[0], hex[1]), Pair(hex[2], hex[3]), Pair(hex[4], hex[5]), (byte)255),
            _ => new Color(Pair(hex[0], hex[1]), Pair(hex[2], hex[3]), Pair(hex[4], hex[5]), Pair(hex[6], hex[7])),
        };
        return true;
    }

    private static bool TryArray(JsonElement e, Type elementType, out object? value, out string? error)
    {
        value = null;
        error = null;

        if (e.ValueKind != JsonValueKind.Array)
        {
            error = $"expected an array of {Describe(elementType)} but got {Summarise(e)}";
            return false;
        }

        var array = Array.CreateInstance(elementType, e.GetArrayLength());
        int i = 0;
        foreach (var item in e.EnumerateArray())
        {
            if (!TryFromJson(item, elementType, out var converted, out error))
            {
                error = $"element {i}: {error}";
                return false;
            }
            array.SetValue(converted, i++);
        }

        value = array;
        return true;
    }

    private static bool TryList(JsonElement e, Type elementType, out object? value, out string? error)
    {
        if (!TryArray(e, elementType, out var array, out error))
        {
            value = null;
            return false;
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (var item in (Array)array!) list.Add(item);
        value = list;
        return true;
    }

    /// <summary>
    /// Reads between <paramref name="min"/> and <paramref name="max"/> floats from an array, or
    /// from an object by the given component names (case-insensitive).
    /// </summary>
    private static bool TryFloats(JsonElement e, int min, int max, string[] names, out float[] values,
                                  out string? error, bool missingAsZero = false, float[]? defaults = null)
    {
        values = new float[max];
        error  = null;
        if (defaults != null) Array.Copy(defaults, values, Math.Min(defaults.Length, max));

        if (e.ValueKind == JsonValueKind.Array)
        {
            int n = e.GetArrayLength();
            if (n < min || n > max)
            {
                error = $"{n} elements";
                return false;
            }

            int i = 0;
            foreach (var item in e.EnumerateArray())
            {
                if (!TryNumber(item, typeof(float), out var f, out error)) return false;
                values[i++] = (float)f!;
            }
            return true;
        }

        if (e.ValueKind == JsonValueKind.Object)
        {
            int found = 0;
            for (int i = 0; i < max; i++)
            {
                if (TryGetPropertyIgnoreCase(e, names[i], out var item))
                {
                    if (!TryNumber(item, typeof(float), out var f, out error)) return false;
                    values[i] = (float)f!;
                    found++;
                }
                else if (i < min && !missingAsZero && defaults == null)
                {
                    error = $"missing '{names[i]}'";
                    return false;
                }
            }

            if (found == 0)
            {
                error = $"no {string.Join("/", names.Take(max))} components";
                return false;
            }
            return true;
        }

        error = "not an array or object";
        return false;
    }

    private static bool HasProperty(JsonElement e, string name) => TryGetPropertyIgnoreCase(e, name, out _);

    private static bool TryGetPropertyIgnoreCase(JsonElement e, string name, out JsonElement value)
    {
        if (e.TryGetProperty(name, out value)) return true;

        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static object? ToPlainObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : e.GetDouble(),
        _                    => e,
    };

    // -------------------------------------------------------------------------
    // value → JSON
    // -------------------------------------------------------------------------

    /// <summary>The readable JSON view of a value: Euler degrees, hex colours, rounded floats.</summary>
    public static JsonNode? ToJson(object? value)
    {
        switch (value)
        {
            case null:           return null;
            case JsonNode node:  return node.Parent is null ? node : node.DeepClone();
            case JsonElement el: return el.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(el.GetRawText());
            case string s:       return JsonValue.Create(s);
            case bool b:         return JsonValue.Create(b);
            case Enum en:        return JsonValue.Create(en.ToString());
            case float f:        return JsonValue.Create(Round(f));
            case double d:       return JsonValue.Create(Math.Round(d, 4));
            case decimal m:      return JsonValue.Create(m);
            case int i:          return JsonValue.Create(i);
            case long l:         return JsonValue.Create(l);
            case short sh:       return JsonValue.Create(sh);
            case byte by:        return JsonValue.Create(by);
            case sbyte sb:       return JsonValue.Create(sb);
            case ushort us:      return JsonValue.Create(us);
            case uint ui:        return JsonValue.Create(ui);
            case ulong ul:       return JsonValue.Create(ul);
            case Vector2 v2:     return new JsonArray(Round(v2.X), Round(v2.Y));
            case Vector3 v3:     return new JsonArray(Round(v3.X), Round(v3.Y), Round(v3.Z));
            case Vector4 v4:     return new JsonArray(Round(v4.X), Round(v4.Y), Round(v4.Z), Round(v4.W));
            case Quaternion q:
            {
                var euler = Transform3D.QuaternionToEuler(q);
                return new JsonArray(Round(euler.X), Round(euler.Y), Round(euler.Z));
            }
            case Color c:        return JsonValue.Create($"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}");
            case Material3D m3:  return JsonSerializer.SerializeToNode(m3, McpJson.Compact);
            case IEnumerable list:
            {
                var array = new JsonArray();
                foreach (var item in list) array.Add(ToJson(item));
                return array;
            }
        }

        try
        {
            return JsonSerializer.SerializeToNode(value, McpJson.Compact);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return JsonValue.Create(value.ToString());
        }
    }

    private static float Round(float f) => MathF.Round(f, 4);

    // -------------------------------------------------------------------------
    // Type descriptions
    // -------------------------------------------------------------------------

    /// <summary>True for every type <see cref="TryFromJson"/> handles without the serialiser fallback.</summary>
    public static bool IsSupportedType(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying != null) return IsSupportedType(underlying);
        if (t == typeof(string) || t == typeof(bool) || t.IsEnum || IsNumeric(t)) return true;
        if (t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) || t == typeof(Quaternion) || t == typeof(Color)) return true;
        if (t == typeof(Material3D) || t == typeof(JsonElement) || t == typeof(object)) return true;
        if (t.IsArray && t.GetArrayRank() == 1) return IsSupportedType(t.GetElementType()!);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return IsSupportedType(t.GetGenericArguments()[0]);
        return false;
    }

    /// <summary>A short human description of a type, for schemas, errors and catalogues.</summary>
    public static string Describe(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying != null) return Describe(underlying) + " (optional)";

        if (t == typeof(string))     return "string";
        if (t == typeof(bool))       return "boolean";
        if (IsIntegral(t))           return "integer";
        if (IsNumeric(t))            return "number";
        if (t == typeof(Vector2))    return "Vector2 [x, y]";
        if (t == typeof(Vector3))    return "Vector3 [x, y, z]";
        if (t == typeof(Vector4))    return "Vector4 [x, y, z, w]";
        if (t == typeof(Quaternion)) return "rotation [pitch, yaw, roll] degrees";
        if (t == typeof(Color))      return "colour '#RRGGBB[AA]' or name";
        if (t == typeof(Material3D)) return "material {albedoColor, metallic, roughness, emissiveIntensity, …}";
        if (t == typeof(JsonElement) || t == typeof(object)) return "any JSON value";
        if (t.IsEnum)                return $"{t.Name} ({string.Join("|", Enum.GetNames(t))})";
        if (t.IsArray)               return $"array of {Describe(t.GetElementType()!)}";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return $"list of {Describe(t.GetGenericArguments()[0])}";
        return t.Name;
    }

    public static string VectorHint(int count) => count switch { 2 => "[x, y]", 3 => "[x, y, z]", _ => "[x, y, z, w]" };

    public static bool IsNumeric(Type t)
        => IsIntegral(t) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    public static bool IsIntegral(Type t)
        => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
        || t == typeof(sbyte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong);

    /// <summary>A short excerpt of a JSON value for error messages.</summary>
    public static string Summarise(JsonElement e)
    {
        string raw = e.ValueKind == JsonValueKind.Undefined ? "undefined" : e.GetRawText();
        return raw.Length <= 60 ? raw : raw[..57] + "...";
    }
}
