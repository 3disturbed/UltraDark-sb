using System.Text.Json;
using System.Text.RegularExpressions;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Core;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Holds each engine's components to the other's serialised property set.
/// </summary>
/// <remarks>
/// <see cref="ScriptBridgeParityTests"/> pins the scripting globals, and that closed one hole:
/// a script may only name members both bridges implement. It says nothing about the properties
/// on a component, which a scene file and <c>Scene.addComponent</c> both set by name — and a
/// name that exists on one engine and not the other is applied on one and dropped with a
/// warning on the other, at runtime, in a build nobody has opened yet.
///
/// That is not hypothetical. The browser's SpriteRenderer grew a <c>size</c> property so a
/// textureless actor draws a tinted box, which is what makes every bundled template visible
/// before it has any art. C# had neither the property nor the box: it returned early when
/// Texture was null. A project that looked correct in the browser exported to a native build
/// that was an empty cornflower-blue window, and the validator could not see it because the
/// validator checks globals, not component properties.
/// </remarks>
public class ComponentSchemaParityTests
{
    private static string RepoRoot
    {
        get
        {
            var repo = EngineRepoLocator.Find();
            Assert.NotNull(repo);
            return repo!.Root;
        }
    }

    // -------------------------------------------------------------------------
    // The pairs
    // -------------------------------------------------------------------------

    /// <summary>
    /// Every pair in <c>/mirrors.json</c> whose check is <c>schema</c>: a component both engines
    /// have. Adding a component to both engines means adding it to the map; the sweep below fails
    /// on one that is registered and mapped nowhere.
    /// </summary>
    public static TheoryData<string, string, string> Pairs
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            using var mirrors = Mirrors();
            foreach (var pair in mirrors.RootElement.GetProperty("pairs").EnumerateArray())
            {
                if (pair.GetProperty("check").GetString() != "schema") continue;
                data.Add(pair.GetProperty("name").GetString()!, pair.GetProperty("js").GetString()!, pair.GetProperty("cs").GetString()!);
            }
            return data;
        }
    }

    private static JsonDocument Mirrors()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "mirrors.json")));

    // -------------------------------------------------------------------------
    // Reading the other side
    // -------------------------------------------------------------------------

    /// <summary>
    /// Property names out of a browser component's <c>static schema = { … }</c> block: the block
    /// inside <paramref name="component"/>'s own class, since a file such as Collider3D.js holds
    /// several classes, or the file's first block when the class inherits its schema.
    /// </summary>
    private static IReadOnlyList<string> BrowserSchema(string jsPath, string component)
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, jsPath));
        int start = Regex.Match(source, @"\bclass " + component + @"\b").Index;
        string scope = source.Substring(start);
        int next = Regex.Match(scope.Substring(1), @"\nexport class ").Index;
        if (next > 0) scope = scope.Substring(0, next + 1);

        var block = Regex.Match(scope, @"static schema = \{(.*?)\n\s*\};", RegexOptions.Singleline);
        if (!block.Success) block = Regex.Match(source, @"static schema = \{(.*?)\n\s*\};", RegexOptions.Singleline);

        // A schema that only spreads the base (`...Component.schema`) inherits its keys.
        if (block.Success && !Regex.IsMatch(block.Groups[1].Value, @"^\s*[A-Za-z_]\w*\s*:", RegexOptions.Multiline)
            && !jsPath.EndsWith("Component.js", StringComparison.Ordinal))
            return BrowserSchema("html5/src/core/Component.js", "Component");

        // Only the keys at the block's own indentation: an entry's option object spans lines too
        // (`{ type: P.Enum,` / `values: [...],`), and those inner keys are not properties.
        var names = new List<string>();
        int indent = -1;
        foreach (Match property in Regex.Matches(block.Groups[1].Value, @"^([ \t]*)([A-Za-z_]\w*)\s*:", RegexOptions.Multiline))
        {
            if (indent < 0) indent = property.Groups[1].Length;
            if (property.Groups[1].Length == indent) names.Add(property.Groups[2].Value);
        }
        return names;
    }

    /// <summary>The properties the map records as missing natively on purpose, for one component.</summary>
    private static HashSet<string> KnownGaps(string component)
    {
        using var mirrors = Mirrors();
        var gaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mirrors.RootElement.GetProperty("components").TryGetProperty("knownGaps", out var known)
            && known.TryGetProperty(component, out var list))
            foreach (var name in list.EnumerateArray()) gaps.Add(name.GetString()!);
        return gaps;
    }

    /// <summary>
    /// Public property names on the C# component, read from the source rather than by
    /// reflection: reflection would need the type registry and a graphics device, and the
    /// question here is only what a file can name.
    /// </summary>
    /// <remarks>
    /// Both a block body (<c>public Vector2 Size { get; set; }</c>) and an expression body
    /// (<c>public BodyType BodyType =&gt; …</c>) count. Missing the second form is how this
    /// check first reported Rigidbody2D.bodyType as absent when it is merely computed.
    /// </remarks>
    private static IReadOnlyList<string> CSharpProperties(string csPath)
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, csPath));
        return Regex.Matches(source, @"public\s+[\w<>?\[\]\.]+\s+([A-Z]\w*)\s*(?:\{\s*get|=>)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // -------------------------------------------------------------------------
    // The check
    // -------------------------------------------------------------------------

    /// <summary>
    /// Every property the browser serialises exists on the C# component, so a scene file or an
    /// <c>addComponent</c> call written against one engine is not silently half-applied by the
    /// other.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public void EveryBrowserSchemaPropertyExistsOnTheCSharpComponent(string component, string jsPath, string csPath)
    {
        var schema = BrowserSchema(jsPath, component);
        Assert.NotEmpty(schema);   // a regex that matched nothing would pass every case below

        // Enabled and the rest of the base class are every component's, and live in Component.cs.
        var properties = CSharpProperties(csPath).Concat(CSharpProperties("SexyBiscuit.Engine/Core/Component.cs")).ToList();
        var known = KnownGaps(component);
        var missing = schema
            .Where(name => !properties.Contains(name, StringComparer.OrdinalIgnoreCase) && !known.Contains(name))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{component}: the browser serialises {string.Join(", ", missing)}, which C# has no property for. " +
            "A scene file or Scene.addComponent naming it is applied in the browser and dropped natively.");
    }

    /// <summary>
    /// The placeholder box is drawable on both engines, which is what makes a project visible
    /// before it has art. A regression here is an empty native window, so it is named
    /// explicitly rather than left to the sweep above.
    /// </summary>
    [Fact]
    public void ATexturelessSpriteRendererCanBeSizedOnBothEngines()
    {
        Assert.Contains("size", BrowserSchema("html5/src/rendering/SpriteRenderer.js", "SpriteRenderer"), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Size", CSharpProperties("SexyBiscuit.Engine/Rendering/SpriteRenderer.cs"), StringComparer.OrdinalIgnoreCase);

        // And C# must actually draw it: an early return on a null texture is the bug this pins.
        string draw = File.ReadAllText(Path.Combine(RepoRoot, "SexyBiscuit.Engine/Rendering/SpriteRenderer.cs"));
        Assert.Contains("DrawUntextured", draw);
    }

    /// <summary>
    /// Every concrete component the engine assembly declares is in the map, or is listed as
    /// native-only on purpose. A component one engine has and the other does not is a scene
    /// that half-loads there, and this is the only place that says which ones those are.
    /// </summary>
    [Fact]
    public void EveryEngineComponentIsMappedOrListedAsNativeOnly()
    {
        using var mirrors = Mirrors();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in mirrors.RootElement.GetProperty("pairs").EnumerateArray())
            if (pair.GetProperty("kind").GetString() == "component") known.Add(pair.GetProperty("name").GetString()!);
        var components = mirrors.RootElement.GetProperty("components");
        foreach (string list in new[] { "ignore", "csOnly" })
            foreach (var name in components.GetProperty(list).EnumerateArray()) known.Add(name.GetString()!);

        var engine = typeof(Engine.Core.Component).Assembly;
        var unmapped = ReflectionUtil.FindComponentTypes()
            .Where(t => t.Assembly == engine)
            .Select(t => t.Name)
            .Where(n => !known.Contains(n))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(unmapped.Count == 0, "add a pair or a components.csOnly entry in mirrors.json for: " + string.Join(", ", unmapped));
    }
}
