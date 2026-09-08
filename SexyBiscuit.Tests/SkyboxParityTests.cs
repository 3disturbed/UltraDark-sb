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
}
