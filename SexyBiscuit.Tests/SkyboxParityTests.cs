using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.Rendering;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The two Skybox properties a scene stores that this engine used to drop.
/// </summary>
/// <remarks>
/// The browser has serialised <c>cubemapPath</c> and <c>exposure</c> since it was written
/// and this engine had neither, so a scene naming a sky loaded as the default gradient
/// natively and nobody found out until someone opened the build. <c>cubemapPath</c> is one
/// folder standing for six faces, which only works while both engines spell the six the
/// same way — <c>graphics.test.js</c> holds the browser to this list, and this holds the
/// list to the order <see cref="Microsoft.Xna.Framework.Graphics.TextureCube"/> wants.
/// The drawing itself needs a graphics device and is not testable here.
///
/// <para>
/// Which is why the last two facts read the source instead, the way
/// <see cref="ComponentSchemaParityTests.ATexturelessSpriteRendererCanBeSizedOnBothEngines"/>
/// does: both pin a shape the browser has always had and this engine did not, and both are
/// about what happens on the way to a graphics device this suite cannot make.
/// </para>
/// </remarks>
public class SkyboxParityTests
{
    [Fact]
    public void ACubemapFolderStandsForSixFacesInCubeFaceOrder()
    {
        Assert.Equal(
            new[]
            {
                "Assets/Sky/px.png", "Assets/Sky/nx.png",
                "Assets/Sky/py.png", "Assets/Sky/ny.png",
                "Assets/Sky/pz.png", "Assets/Sky/nz.png",
            },
            Skybox.CubemapFacePaths("Assets/Sky"));
    }

    [Fact]
    public void ATrailingSeparatorInAPathAPersonTypedChangesNothing()
    {
        var plain = Skybox.CubemapFacePaths("Assets/Sky");

        Assert.Equal(plain, Skybox.CubemapFacePaths("Assets/Sky/"));
        Assert.Equal(plain, Skybox.CubemapFacePaths(@"Assets/Sky\"));
    }

    [Fact]
    public void ASkyboxStartsAsAGradientAtFullExposure()
    {
        var sky = new Skybox();

        Assert.Equal("", sky.CubemapPath);
        Assert.Equal(1f, sky.Exposure, 3);
        Assert.Null(sky.CubemapTexture);
    }

    [Fact]
    public void ACubemapNeedsExactlySixFaces()
    {
        var sky = new Skybox();

        // The count is checked before anything reaches for the graphics device, so unlike
        // the rest of the load this much is genuinely executable here.
        Assert.Throws<ArgumentException>(() => sky.LoadCubemap(new[] { "Assets/Sky/px.png" }, null!));
    }

    // -------------------------------------------------------------------------
    // What happens on the way to a graphics device
    // -------------------------------------------------------------------------

    /// <summary>
    /// The gradient quad is rebuilt when either colour changes, not kept for the life of
    /// the component.
    /// </summary>
    /// <remarks>
    /// The colours live in the quad's vertices, so an early return on <c>_quadVB != null</c>
    /// alone meant every edit to <see cref="Skybox.GradientTop"/> after the first draw was
    /// ignored — while the browser recomputes its gradient every frame and showed the edit
    /// at once. The editor edits both colours live, so that was the two engines disagreeing
    /// in the one place a person would be looking straight at it.
    /// </remarks>
    [Fact]
    public void TheGradientQuadIsRebuiltWhenEitherColourChanges()
    {
        string body = MethodBody("private void EnsureGradientResources");

        Assert.Contains("GradientTop", body);
        Assert.Contains("GradientBottom", body);
        Assert.DoesNotContain("if (_quadVB != null) return;", body);
        Assert.Contains("_quadVB?.Dispose();", body);   // the old buffer goes with the old colours
    }

    /// <summary>
    /// A cubemap face is looked for in a mounted bundle before the file system.
    /// </summary>
    /// <remarks>
    /// <c>AssetManager.Load</c> tries mounted bundles first and the file system second, so
    /// reaching straight for <c>File.OpenRead</c> is why a sky that worked from a source
    /// tree fell back to the gradient in a bundled build — the one place a player would see
    /// it and nobody would be watching. The browser has always gone through its own asset
    /// manager for this.
    /// </remarks>
    [Fact]
    public void ACubemapFaceGoesThroughTheAssetManagerBeforeTheFileSystem()
    {
        string body = MethodBody("private static (Color[] Pixels, int Size)? ReadFace");

        int viaManager = body.IndexOf("AssetManager.Current", StringComparison.Ordinal);
        int viaFile    = body.IndexOf("File.OpenRead", StringComparison.Ordinal);

        Assert.True(viaManager >= 0, "a bundled cubemap is only found through the asset manager");
        Assert.True(viaFile > viaManager, "the file system is the fallback, not the first try");

        // A texture the manager hands over is cached and reference-counted by it, so the
        // reference is given back; disposing it would leave the cache holding a dead one.
        Assert.Contains("Unload", body);
        Assert.DoesNotContain("Dispose", body);
    }

    // -------------------------------------------------------------------------
    // Reading the source
    // -------------------------------------------------------------------------

    /// <summary>One method's body, braces included, found by matching from its signature.</summary>
    private static string MethodBody(string signature)
    {
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);

        string source = File.ReadAllText(Path.Combine(repo!.Root, "SexyBiscuit.Engine/Rendering/Skybox.cs"));
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Skybox.cs no longer declares '{signature}'");

        int open  = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"'{signature}' has no closing brace.");
    }
}
