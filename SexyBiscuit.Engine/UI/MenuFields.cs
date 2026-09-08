using System.Globalization;
using System.Reflection;

namespace SexyBiscuit.Engine.UI;

/// <summary>
/// Reads and writes a settings field named by the shared menu description.
/// </summary>
/// <remarks>
/// <para>
/// Reflection, because the alternative is a switch with one arm per row that has to be
/// kept in step with a JSON file by hand — which is the drift the shared description
/// exists to prevent. The browser engine indexes the object directly, which is the same
/// thing in a language that has it for free.
/// </para>
/// <para>
/// The names in the file are the browser's, so they are camelCase and are mapped to the
/// C# property here. One field does not follow the rule — <c>vsync</c> against
/// <c>VSync</c> — and that is this repository's established spelling either side of the
/// seam, so it is written down rather than renamed.
/// </para>
/// </remarks>
public static class MenuFields
{
    /// <summary>Fields whose C# name is not the JSON key with a capital first letter.</summary>
    private static readonly Dictionary<string, string> Exceptions = new(StringComparer.Ordinal)
    {
        ["vsync"] = "VSync",
    };

    /// <summary>The value of a named field, or null when there is no such field.</summary>
    public static object? Read(object target, string field)
        => Property(target, field)?.GetValue(target);

    /// <summary>Writes a value onto a named field, converting it to the field's type.</summary>
    public static bool Write(object target, string field, object value)
    {
        PropertyInfo? property = Property(target, field);
        if (property is null || !property.CanWrite) return false;

        object? converted = Convert(value, property.PropertyType);
        if (converted is null) return false;

        property.SetValue(target, converted);
        return true;
    }

    /// <summary>
    /// Whether two values mean the same setting.
    /// </summary>
    /// <remarks>
    /// Loose on purpose. A dropdown's options come out of JSON as doubles, the property
    /// behind one may be an int, and an enum arrives as its name — so a strict
    /// <c>Equals</c> would report every dropdown as changed on every single frame, and
    /// the menu would rewrite the settings and re-lay itself out sixty times a second.
    /// </remarks>
    public static bool Same(object? a, object? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);

        if (IsNumeric(a) && IsNumeric(b))
            return Math.Abs(System.Convert.ToDouble(a, CultureInfo.InvariantCulture)
                          - System.Convert.ToDouble(b, CultureInfo.InvariantCulture)) < 0.0001;

        if (a is bool || b is bool)
            return Equals(System.Convert.ToBoolean(a), System.Convert.ToBoolean(b));

        return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The readout beside a slider.</summary>
    public static string Format(float value, string format) => format switch
    {
        "percent" => $"{MathF.Round(value * 100f)}%",
        _ when MathF.Abs(value - MathF.Round(value)) < 0.001f
                  => MathF.Round(value).ToString("0", CultureInfo.InvariantCulture),
        _         => value.ToString("0.00", CultureInfo.InvariantCulture),
    };

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private static PropertyInfo? Property(object target, string field)
    {
        if (string.IsNullOrEmpty(field)) return null;

        string name = Exceptions.TryGetValue(field, out string? mapped)
            ? mapped
            : char.ToUpperInvariant(field[0]) + field[1..];

        return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
    }

    private static object? Convert(object value, Type type)
    {
        try
        {
            if (type.IsEnum)
                return value is string name
                    ? Enum.Parse(type, name, ignoreCase: true)
                    : Enum.ToObject(type, System.Convert.ToInt32(value, CultureInfo.InvariantCulture));

            return System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is InvalidCastException or FormatException
                                    or OverflowException or ArgumentException)
        {
            // A description that names a value the field cannot hold is a bad file, not
            // a reason to take the game down mid-menu.
            return null;
        }
    }

    private static bool IsNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint
                 or long or ulong or float or double or decimal;
}
