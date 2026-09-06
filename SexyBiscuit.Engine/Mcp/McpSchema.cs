using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// Builds a tool's JSON schema from its method signature and binds incoming arguments to it.
/// The C# signature is the single source of truth: parameter names become property names,
/// defaults become optional, <c>[McpParam]</c> text becomes the description.
/// </summary>
public static class McpSchema
{
    /// <summary>Parameters the registry supplies itself; never part of the schema.</summary>
    public static bool IsInjected(ParameterInfo p)
        => p.ParameterType == typeof(CancellationToken) || p.ParameterType == typeof(McpCallContext);

    public static JsonObject ForMethod(MethodInfo method)
    {
        var properties = new JsonObject();
        var required   = new JsonArray();

        foreach (var p in method.GetParameters())
        {
            if (IsInjected(p)) continue;

            properties[p.Name!] = ForParameter(p, out bool isRequired);
            if (isRequired) required.Add(p.Name!);
        }

        var schema = new JsonObject
        {
            ["type"]       = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0) schema["required"] = required;
        // No additionalProperties:false — Bind ignores unknown names anyway, and the line costs
        // every tool a few tokens of context for the life of the session.
        return schema;
    }

    public static JsonObject ForParameter(ParameterInfo p, out bool required)
    {
        var type       = p.ParameterType;
        var underlying = Nullable.GetUnderlyingType(type);
        bool nullable  = underlying != null || IsNullableReference(p);

        var schema = ForType(underlying ?? type);
        var attr   = p.GetCustomAttribute<McpParamAttribute>();

        var parts = new List<string>();
        if (attr != null) parts.Add(attr.Description.TrimEnd('.') + ".");
        if (schema["description"] is JsonValue hint)
        {
            parts.Add(hint.ToString());
            schema.Remove("description");
        }
        if (attr?.Example != null) parts.Add("e.g. " + attr.Example);
        if (parts.Count > 0) schema["description"] = string.Join(" ", parts);

        if (p.HasDefaultValue)
        {
            // A default the model would assume anyway (false, 0, empty) is not worth its tokens.
            var def = NormaliseDefault(p);
            if (def != null && !IsUninformativeDefault(def)) schema["default"] = ValueConverter.ToJson(def);
        }

        required = !p.HasDefaultValue && !nullable;
        return schema;
    }

    /// <summary>The schema fragment for a bare type (no description, default or nullability).</summary>
    private static bool IsUninformativeDefault(object value) => value switch
    {
        bool b   => !b,
        string s => s.Length == 0,
        int i    => i == 0,
        long l   => l == 0,
        float f  => f == 0f,
        double d => d == 0d,
        _        => false,
    };

    public static JsonObject ForType(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying != null) return ForType(underlying);

        if (t == typeof(string))          return new JsonObject { ["type"] = "string" };
        if (t == typeof(bool))            return new JsonObject { ["type"] = "boolean" };
        if (ValueConverter.IsIntegral(t)) return new JsonObject { ["type"] = "integer" };
        if (ValueConverter.IsNumeric(t))  return new JsonObject { ["type"] = "number" };

        if (t.IsEnum)
        {
            var names = new JsonArray();
            foreach (var name in Enum.GetNames(t)) names.Add(name);
            return new JsonObject { ["type"] = "string", ["enum"] = names };
        }

        if (t == typeof(Vector2)) return NumberArray(2, 2, "[x, y]");
        if (t == typeof(Vector3)) return NumberArray(3, 3, "[x, y, z] in world units");
        if (t == typeof(Vector4)) return NumberArray(4, 4, "[x, y, z, w]");
        if (t == typeof(Quaternion)) return NumberArray(3, 4, "[pitch, yaw, roll] in degrees (or [x, y, z, w])");

        if (t == typeof(Color))
            return new JsonObject { ["type"] = "string", ["description"] = "'#RRGGBB', '#RRGGBBAA' or a colour name such as 'CornflowerBlue'" };

        if (t == typeof(JsonElement) || t == typeof(object))
            return new JsonObject { ["description"] = "Any JSON value." };

        if (t.IsArray && t.GetArrayRank() == 1)
            return new JsonObject { ["type"] = "array", ["items"] = ForType(t.GetElementType()!) };

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            return new JsonObject { ["type"] = "array", ["items"] = ForType(t.GetGenericArguments()[0]) };

        return new JsonObject { ["type"] = "object" };
    }

    private static JsonObject NumberArray(int min, int max, string hint) => new()
    {
        ["type"]        = "array",
        ["items"]       = new JsonObject { ["type"] = "number" },
        ["minItems"]    = min,
        ["maxItems"]    = max,
        ["description"] = hint,
    };

    /// <summary>
    /// Binds a JSON arguments object to the method's parameters. Names match exactly, then
    /// case-insensitively, then ignoring underscores and hyphens; a missing required argument
    /// or an unconvertible value is an <see cref="McpToolException"/> naming the parameter.
    /// </summary>
    public static object?[] Bind(MethodInfo method, JsonElement? arguments, McpCallContext context)
    {
        var parameters = method.GetParameters();
        var values     = new object?[parameters.Length];

        bool hasArgs = false;
        JsonElement args = default;

        if (arguments is { } a && a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (a.ValueKind != JsonValueKind.Object)
                throw new McpToolException("Tool arguments must be a JSON object.");

            args    = a;
            hasArgs = true;
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            var p    = parameters[i];
            var type = p.ParameterType;

            if (type == typeof(CancellationToken)) { values[i] = context.Cancellation; continue; }
            if (type == typeof(McpCallContext))    { values[i] = context;              continue; }

            if (hasArgs && TryGetArgument(args, p.Name!, out var element))
            {
                if (!ValueConverter.TryFromJson(element, type, out var converted, out var error))
                    throw new McpToolException($"Argument '{p.Name}': {error}");

                values[i] = converted;
                continue;
            }

            if (p.HasDefaultValue)
            {
                values[i] = NormaliseDefault(p);
                continue;
            }

            if (Nullable.GetUnderlyingType(type) != null || (!type.IsValueType && IsNullableReference(p)))
            {
                values[i] = null;
                continue;
            }

            throw new McpToolException($"Missing required argument '{p.Name}' ({ValueConverter.Describe(type)}).");
        }

        return values;
    }

    private static bool TryGetArgument(JsonElement args, string name, out JsonElement value)
    {
        if (args.TryGetProperty(name, out value)) return true;

        string wanted = Normalise(name);
        foreach (var prop in args.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) || Normalise(prop.Name) == wanted)
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Normalise(string name)
        => name.Replace("_", "").Replace("-", "").ToLowerInvariant();

    /// <summary>
    /// Reflection reports <c>= default</c> on a struct as null and may box an enum default as
    /// its underlying integer; both need fixing up before use.
    /// </summary>
    private static object? NormaliseDefault(ParameterInfo p)
    {
        var type = p.ParameterType;
        var def  = p.DefaultValue;

        if (def is null || def is DBNull)
            return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : null;

        if (type.IsEnum && def is not Enum) return Enum.ToObject(type, def);
        return def;
    }

    private static bool IsNullableReference(ParameterInfo p)
    {
        if (p.ParameterType.IsValueType) return false;

        try
        {
            return new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable;
        }
        catch
        {
            return false;
        }
    }
}
