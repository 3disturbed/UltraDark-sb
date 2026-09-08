using System.Text.Json;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// The shared glyph table, and the silent failure it exists to prevent.
/// </summary>
/// <remarks>
/// Text that draws as nothing is not hypothetical here: the editor's old widgets opened with
/// <c>if (font == null) return;</c> and there has never been a font asset, so every label they
/// drew was invisible and no test noticed. The UI draws from a glyph table embedded out of the
/// browser's own file, and if the resource name in the csproj is wrong it fails the same silent
/// way — so the load is asserted rather than assumed.
/// </remarks>
public class UiFontTests
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

    [Fact]
    public void TheSharedGlyphTableIsActuallyEmbeddedAndLoads()
    {
        // If this fails, text renders as nothing and no other test notices.
        Assert.True(BitmapFont.IsLoaded,
            "BitmapFont found no embedded font5x7.json — check the EmbeddedResource LogicalName");
        Assert.Equal(95, BitmapFont.GlyphCount);
    }

    [Fact]
    public void BothEnginesMeasureTextIdentically()
    {
        // The browser computes length * (width + spacing) - spacing. Same arithmetic
        // here, or a centred label sits in a different place on each engine.
        Assert.Equal(5f, BitmapFont.MeasureLine("A"));
        Assert.Equal(11f, BitmapFont.MeasureLine("AB"));
        Assert.Equal(22f, BitmapFont.MeasureLine("AB", 2f));
        Assert.Equal(0f, BitmapFont.MeasureLine(""));
        Assert.Equal(BitmapFont.MeasureLine("longer"), BitmapFont.MeasureWidest("a\nlonger"));
        Assert.Equal(16f, BitmapFont.MeasureHeight("one\ntwo"));
    }

    [Fact]
    public void TheGlyphTableOnDiskIsTheOneTheBrowserImports()
    {
        string json = File.ReadAllText(Path.Combine(RepoRoot, "html5/src/ui/font5x7.json"));
        using var document = JsonDocument.Parse(json);

        int onDisk = 0;
        foreach (var _ in document.RootElement.GetProperty("glyphs").EnumerateObject()) onDisk++;

        Assert.Equal(onDisk, BitmapFont.GlyphCount);
        Assert.Equal(BitmapFont.GlyphWidth, document.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(BitmapFont.GlyphHeight, document.RootElement.GetProperty("height").GetInt32());
    }

    /// <summary>
    /// The layout engine must ask the shared measurer rather than a rasteriser of its own.
    /// </summary>
    /// <remarks>
    /// One seam is the whole reason a wrapped label breaks in the same place on both engines.
    /// A widget that measures text any other way is a parity bug that no member-name check
    /// could see.
    /// </remarks>
    [Fact]
    public void TheLayoutEngineMeasuresThroughTheSharedSeam()
    {
        Assert.Equal(BitmapFont.MeasureWidest("AB", 1f), UiTextMeasure.Width("AB", 1f));
        Assert.Equal(BitmapFont.MeasureHeight("one\ntwo", 1f), UiTextMeasure.Height("one\ntwo", 1f));
    }
}

/// <summary>
/// Straight alpha, everywhere, because premultiplied looks nearly right.
/// </summary>
/// <remarks>
/// Under premultiplied blending a sprite added its whole colour and only attenuated the
/// destination: a night overlay of (6, 8, 20) at alpha ZERO lifted every pixel in the game by
/// exactly (6, 8, 20). It reads as "the palette looks a bit off", never as a bug, which is why
/// it lasted. The browser uses ctx.globalAlpha — straight alpha — so this is also what keeps a
/// prototype and its native build looking the same.
/// </remarks>
public class BlendModeTests
{
    [Fact]
    public void TheSceneBatchUsesStraightAlphaRatherThanPremultiplied()
    {
        var renderer = new SexyBiscuit.Engine.Rendering.RenderSystem2D();
        Assert.Equal(Microsoft.Xna.Framework.Graphics.BlendState.NonPremultiplied, renderer.BlendState);
    }

    [Fact]
    public void ThePainterOpensItsOwnBatchWithTheSameBlendMode()
    {
        // Read from source: constructing a painter needs a graphics device, and the
        // question is only which constant the call site names.
        var repo = EngineRepoLocator.Find();
        Assert.NotNull(repo);

        string painter = File.ReadAllText(Path.Combine(repo!.Root, "SexyBiscuit.Engine/UI/UiPainter.cs"));
        int begin = painter.IndexOf("_sb.Begin", StringComparison.Ordinal);
        Assert.True(begin > 0, "the UI painter should still open its own batch");
        Assert.Contains("BlendState.NonPremultiplied", painter[begin..(begin + 200)]);
    }
}
