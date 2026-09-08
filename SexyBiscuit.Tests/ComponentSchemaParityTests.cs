using System.Text.RegularExpressions;
using SexyBiscuit.Engine.Code;
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
    /// The components a scene file or a prototype script actually names. Adding a component to
    /// both engines means adding it here too; the whole point is that the list is explicit.
    /// </summary>
    public static TheoryData<string, string, string> Pairs => new()
    {
        { "SpriteRenderer", "html5/src/rendering/SpriteRenderer.js", "SexyBiscuit.Engine/Rendering/SpriteRenderer.cs" },
        { "Camera2D",       "html5/src/rendering/Camera2D.js",       "SexyBiscuit.Engine/Rendering/Camera2D.cs" },
        { "Rigidbody2D",    "html5/src/physics/Rigidbody2D.js",      "SexyBiscuit.Engine/Physics/Rigidbody2D.cs" },
        { "Collider2D",     "html5/src/physics/Collider2D.js",       "SexyBiscuit.Engine/Physics/Collider2D.cs" },
        { "ChibiCharacter", "html5/src/chibi/ChibiCharacter.js",      "SexyBiscuit.Engine/Chibi/ChibiCharacter.cs" },
        { "ChibiAnimator",  "html5/src/chibi/ChibiAnimator.js",       "SexyBiscuit.Engine/Chibi/ChibiAnimator.cs" },
        { "UiCanvas",       "html5/src/ui/UiCanvas.js",              "SexyBiscuit.Engine/UI/UiCanvas.cs" },
    };

    // -------------------------------------------------------------------------
    // Reading the other side
    // -------------------------------------------------------------------------

    /// <summary>Property names out of a browser component's <c>static schema = { … }</c> block.</summary>
    private static IReadOnlyList<string> BrowserSchema(string jsPath)
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, jsPath));
        var names = new List<string>();

        foreach (Match block in Regex.Matches(source, @"static schema = \{(.*?)\n    \};", RegexOptions.Singleline))
            foreach (Match property in Regex.Matches(block.Groups[1].Value, @"^\s*([A-Za-z_]\w*)\s*:", RegexOptions.Multiline))
                names.Add(property.Groups[1].Value);

        return names;
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
        var schema = BrowserSchema(jsPath);
        Assert.NotEmpty(schema);   // a regex that matched nothing would pass every case below

        var properties = CSharpProperties(csPath);
        var missing = schema
            .Where(name => !properties.Contains(name, StringComparer.OrdinalIgnoreCase))
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
        Assert.Contains("size", BrowserSchema("html5/src/rendering/SpriteRenderer.js"), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Size", CSharpProperties("SexyBiscuit.Engine/Rendering/SpriteRenderer.cs"), StringComparer.OrdinalIgnoreCase);

        // And C# must actually draw it: an early return on a null texture is the bug this pins.
        string draw = File.ReadAllText(Path.Combine(RepoRoot, "SexyBiscuit.Engine/Rendering/SpriteRenderer.cs"));
        Assert.Contains("DrawUntextured", draw);
    }
}
