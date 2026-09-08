using System.Text.Json;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Code;
using SexyBiscuit.Engine.UI;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// Holds the input router to the golden cases the browser engine reads from the same file.
/// </summary>
/// <remarks>
/// Layout is pinned by <c>layout-cases.json</c> and navigation scoring by
/// <c>nav-cases.json</c>; this pins what happens when somebody actually uses the thing —
/// what counts as a click, when a slider snaps, which way a wheel scrolls, what a pad does
/// to an open list. All of it is state machines and numbers, so two hand-written suites
/// would drift the moment one engine's author made a judgement call the other's did not.
/// </remarks>
public class UiInteractionTests
{
    private const float Tolerance = 0.01f;

    private static string RepoRoot
    {
        get
        {
            var repo = EngineRepoLocator.Find();
            Assert.NotNull(repo);
            return repo!.Root;
        }
    }

    private static JsonElement Cases()
    {
        string path = Path.Combine(RepoRoot, "html5", "tests", "fixtures", "ui-interaction-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("cases").Clone();
    }

    public static TheoryData<string> CaseNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (JsonElement c in Cases().EnumerateArray())
                names.Add(c.GetProperty("name").GetString()!);
            return names;
        }
    }

    // -------------------------------------------------------------------------
    // The fixture
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void AnInteractionCaseEndsInTheStateBothEnginesAgreeOn(string name)
    {
        JsonElement test = FindCase(name);
        UiCanvas.ClearAll();

        try
        {
            var canvas = new UiCanvas { ScaleMode = UiScaleMode.ConstantPixel };
            canvas.Interactive = !test.TryGetProperty("interactive", out JsonElement live) || live.GetBoolean();
            canvas.SetViewport(test.GetProperty("viewport")[0].GetSingle(),
                               test.GetProperty("viewport")[1].GetSingle());
            canvas.Adopt(UiDocument.FromElement(test.GetProperty("tree")));

            Vector2? previous = null;
            foreach (JsonElement spec in test.GetProperty("frames").EnumerateArray())
            {
                UiInputFrame frame = ReadFrame(spec, ref previous);

                // Laid out every frame: scrolling and a changed value both move
                // rectangles, and the hit test has to read this frame's.
                canvas.Layout();
                canvas.Input.Update(frame);
            }

            foreach (JsonProperty expectation in test.GetProperty("expect").EnumerateObject())
            {
                UiNode? node = expectation.Name == "root" ? canvas.Root : canvas.Find(expectation.Name);
                Assert.True(node != null, $"The case names \"{expectation.Name}\", which the tree does not contain.");

                foreach (JsonProperty want in expectation.Value.EnumerateObject())
                    AssertState(node!, expectation.Name, want);
            }
        }
        finally
        {
            // A leaked canvas stays in the static paint list and poisons the next case.
            UiCanvas.ClearAll();
        }
    }

    // -------------------------------------------------------------------------
    // Reading the fixture
    // -------------------------------------------------------------------------

    /// <summary>
    /// One frame. The pointer delta is derived from the previous frame rather than
    /// written down, exactly as a host derives it, so a case never has to state it.
    /// </summary>
    private static UiInputFrame ReadFrame(JsonElement spec, ref Vector2? previous)
    {
        Vector2 pointer = spec.TryGetProperty("pointer", out JsonElement p)
            ? new Vector2(p[0].GetSingle(), p[1].GetSingle())
            : new Vector2(-1f, -1f);

        Vector2 delta = previous is { } was ? pointer - was : Vector2.Zero;
        previous = pointer;

        return new UiInputFrame
        {
            DeltaTime      = Single(spec, "deltaTime"),
            Pointer        = pointer,
            PointerDelta   = delta,
            PointerDown    = Bool(spec, "pointerDown"),
            PointerIsTouch = Bool(spec, "pointerIsTouch"),
            Wheel          = Single(spec, "wheel"),
            NavAxis        = spec.TryGetProperty("navAxis", out JsonElement a)
                                 ? new Vector2(a[0].GetSingle(), a[1].GetSingle())
                                 : Vector2.Zero,
            NavUp          = Bool(spec, "navUp"),
            NavDown        = Bool(spec, "navDown"),
            NavLeft        = Bool(spec, "navLeft"),
            NavRight       = Bool(spec, "navRight"),
            Confirm        = Bool(spec, "confirm"),
            Cancel         = Bool(spec, "cancel"),
            Typed          = spec.TryGetProperty("typed", out JsonElement t) ? t.GetString() ?? "" : "",
            Backspace      = Bool(spec, "backspace"),
        };
    }

    private static bool Bool(JsonElement spec, string name)
        => spec.TryGetProperty(name, out JsonElement value) && value.GetBoolean();

    private static float Single(JsonElement spec, string name)
        => spec.TryGetProperty(name, out JsonElement value) ? value.GetSingle() : 0f;

    /// <summary>The state keys a case may assert on, named the same on both engines.</summary>
    private static void AssertState(UiNode node, string who, JsonProperty want)
    {
        string where = $"{who}.{want.Name}";

        switch (want.Name)
        {
            case "hovered":       Assert.Equal(want.Value.GetBoolean(), node.Hovered);  break;
            case "pressed":       Assert.Equal(want.Value.GetBoolean(), node.Pressed);  break;
            case "clicked":       Assert.Equal(want.Value.GetBoolean(), node.Clicked);  break;
            case "focused":       Assert.Equal(want.Value.GetBoolean(), node.Focused);  break;
            case "expanded":      Assert.Equal(want.Value.GetBoolean(), node.Expanded); break;
            case "checked":       Assert.Equal(want.Value.GetBoolean(), node.Checked);  break;
            case "text":          Assert.Equal(want.Value.GetString(), node.Text);      break;
            case "selectedIndex": Assert.Equal(want.Value.GetInt32(), node.SelectedIndex); break;

            case "value":         AssertClose(want.Value.GetSingle(), node.Value, where);              break;
            case "scrollOffsetX": AssertClose(want.Value.GetSingle(), node.ScrollOffset.X, where);     break;
            case "scrollOffsetY": AssertClose(want.Value.GetSingle(), node.ScrollOffset.Y, where);     break;

            default: throw new InvalidOperationException($"Unknown state key \"{want.Name}\" in the interaction fixture.");
        }
    }

    private static void AssertClose(float expected, float actual, string where)
        => Assert.True(MathF.Abs(expected - actual) <= Tolerance,
                       $"{where} was {actual}, expected {expected}.");

    private static JsonElement FindCase(string name)
    {
        foreach (JsonElement c in Cases().EnumerateArray())
            if (c.GetProperty("name").GetString() == name) return c;

        throw new InvalidOperationException($"No interaction case named \"{name}\".");
    }
}
