using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Chibi;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scripting;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// MakeChibi: the recipe, the rig it builds, and the clips that drive it.
/// </summary>
/// <remarks>
/// Mirrors <c>html5/tests/chibi.test.js</c>. Several tests here assert the same literal
/// numbers that suite does, which is what stops the two engines building different
/// characters from the same recipe — they read the same part table, but nothing else
/// forces the two interpreters of it to agree.
/// </remarks>
public class ChibiTests
{
    // -------------------------------------------------------------------------
    // The rig contract
    // -------------------------------------------------------------------------

    [Fact]
    public void ThePartTableLoadsFromTheEmbeddedResource()
    {
        // Everything below is vacuous if it does not, and a missing resource is a csproj
        // edit away rather than a code change, so it needs saying out loud.
        Assert.True(ChibiParts.IsLoaded);
        Assert.NotEmpty(ChibiParts.Joints);
        Assert.NotEmpty(ChibiClips.Names);
    }

    [Fact]
    public void TheRigHasTheJointsAClipIsAllowedToName()
    {
        Assert.Equal(new[]
        {
            "Hips", "Torso", "Neck", "Head",
            "ArmL", "ArmR", "ForearmL", "ForearmR", "HandL", "HandR",
            "ThighL", "ThighR", "ShinL", "ShinR", "FootL", "FootR",
        }, ChibiParts.JointNames);

        Assert.Equal(new[] { "Head", "Face", "Hand_L", "Hand_R", "Back" },
            ChibiParts.Sockets.Select(s => s.Name));
    }

    [Fact]
    public void TheRigListsEveryJointParentsFirst()
    {
        // The builder attaches in one pass, so a joint whose parent comes later would be
        // attached to nothing and stand at the world origin.
        var seen = new HashSet<string> { string.Empty };
        foreach (ChibiJoint joint in ChibiParts.Joints)
        {
            Assert.True(seen.Contains(joint.Parent),
                $"{joint.Name} comes before its parent {joint.Parent}");
            seen.Add(joint.Name);
        }
    }

    // -------------------------------------------------------------------------
    // The recipe
    // -------------------------------------------------------------------------

    [Fact]
    public void AnEmptyRecipeIsAValidCharacter()
    {
        // "{}" has to work: it is what a new file contains, and what a panel starts from.
        ChibiRecipe recipe = ChibiRecipe.Parse("{}");
        Assert.Equal(ChibiParts.Slots.OrderBy(s => s), recipe.Style.Keys.OrderBy(s => s));
        Assert.Equal(ChibiParts.ColourSlots.OrderBy(s => s), recipe.Colours.Keys.OrderBy(s => s));
        Assert.All(recipe.ProportionValues.Values, v => Assert.Equal(1f, v));
    }

    [Fact]
    public void ARecipeRoundTripsThroughTheTextAChibiFileHolds()
    {
        ChibiRecipe before = ChibiRecipe.Random(4242);
        ChibiRecipe after = ChibiRecipe.Parse(before.ToJson());

        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Style, after.Style);
        Assert.Equal(before.Colours, after.Colours);
        Assert.Equal(before.ProportionValues, after.ProportionValues);
        Assert.Equal(before.Accessories, after.Accessories);
    }

    [Fact]
    public void AStyleThatNoLongerExistsFallsBackInsteadOfThrowing()
    {
        // Renaming a hairstyle must not make every saved character unloadable.
        var warnings = new List<string>();
        ChibiRecipe recipe = ChibiRecipe.Parse("{\"style\":{\"hair\":\"Beehive\"}}", warnings.Add);

        Assert.Equal("Bob", recipe.Style["hair"]);
        Assert.Contains(warnings, w => w.Contains("Beehive"));
    }

    [Fact]
    public void ProportionsAreClampedRatherThanTrusted()
    {
        ChibiRecipe recipe = ChibiRecipe.Parse(
            "{\"proportions\":{\"height\":40,\"headSize\":-3,\"bodyWidth\":\"wide\"}}");

        Assert.Equal(2f, recipe.Proportion("height"));
        Assert.Equal(0.5f, recipe.Proportion("headSize"));
        Assert.Equal(1f, recipe.Proportion("bodyWidth"));
    }

    [Fact]
    public void TheSeededGeneratorProducesTheSequenceTheJavaScriptSideDoes()
    {
        // mulberry32 in 32-bit integer arithmetic. These four numbers are the pin:
        // html5/tests/chibi.test.js asserts the same ones, so a change to either
        // implementation that would give the two engines different villages fails here.
        var random = new ChibiRecipe.SeededRandom(7);
        Assert.Equal(new[] { "0.011704753", "0.061958258", "0.976907633", "0.699028706" },
            new[] { random.Next(), random.Next(), random.Next(), random.Next() }
                .Select(v => v.ToString("F9", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void OneSeedNamesTheSameCharacterOnBothEngines()
    {
        // The literal answer html5/tests/chibi.test.js asserts for the same seed. Anything
        // that changes the order the generator draws in moves this, which is the point:
        // Chibi.random(1001) has to be one character, not two.
        ChibiRecipe recipe = ChibiRecipe.Random(1001);

        Assert.Equal("Tall", recipe.Style["head"]);
        Assert.Equal("Mohawk", recipe.Style["hair"]);
        Assert.Equal("Star", recipe.Style["eyes"]);
        Assert.Equal("Dress", recipe.Style["body"]);
        Assert.Equal("Trousers", recipe.Style["legs"]);
        Assert.Equal("Shoes", recipe.Style["feet"]);

        Assert.Equal("#A2694A", recipe.Colours["skin"]);
        Assert.Equal("#5C7A4A", recipe.Colours["hair"]);
        Assert.Equal("#4A6B8C", recipe.Colours["top"]);
        Assert.Equal("#8C7A5A", recipe.Colours["bottom"]);

        Assert.Equal(0.892f, recipe.Proportion("height"), 3);
        Assert.Equal(1.143f, recipe.Proportion("armLength"), 3);
        Assert.Empty(recipe.Accessories);
    }

    [Fact]
    public void ARandomCharacterDrawsItsColoursFromTheCuratedPalettes()
    {
        // Random bytes give forty characters the colour of mud; that is the whole reason
        // the palettes exist, so nothing may bypass them.
        for (int seed = 1; seed <= 60; seed++)
        {
            ChibiRecipe recipe = ChibiRecipe.Random(seed);
            Assert.Contains(recipe.Colours["skin"], ChibiParts.Palette("skin"));
            Assert.Contains(recipe.Colours["hair"], ChibiParts.Palette("hair"));
            Assert.Contains(ChibiParts.Outfits,
                pair => pair[0] == recipe.Colours["top"] && pair[1] == recipe.Colours["bottom"]);
        }
    }

    // -------------------------------------------------------------------------
    // The content
    // -------------------------------------------------------------------------

    [Fact]
    public void EveryPartVariantBuildsWithoutAWarning()
    {
        // chibi-parts.json names joints, meshes and colour slots as strings. A typo gives a
        // character with a missing arm, not an error -- unless this runs.
        foreach (string slot in ChibiParts.Slots)
        {
            foreach (string variant in ChibiParts.VariantsFor(slot))
            {
                var warnings = new List<string>();
                ChibiRecipe recipe = ChibiRecipe.Default();
                recipe.Style[slot] = variant;

                ChibiBuild built = ChibiBuilder.Build(recipe, warnings.Add);
                Assert.True(warnings.Count == 0, $"{slot}/{variant}: {string.Join("; ", warnings)}");
                Assert.NotEmpty(built.Parts);
            }
        }
    }

    [Fact]
    public void EveryAccessoryBuildsWithoutAWarning()
    {
        foreach (string part in ChibiParts.AccessoryNames)
        {
            var warnings = new List<string>();
            ChibiRecipe recipe = ChibiRecipe.Default();
            recipe.Accessories.Add(new ChibiRecipe.Accessory(part, null));

            ChibiBuilder.Build(recipe, warnings.Add);
            Assert.True(warnings.Count == 0, $"{part}: {string.Join("; ", warnings)}");
        }
    }

    [Fact]
    public void EveryPieceNamesAMeshAndAColourSlotThatExist()
    {
        // A shape only one engine has renders as a cube in the other, silently.
        IEnumerable<ChibiPiece> All()
        {
            foreach (string slot in ChibiParts.Slots)
                foreach (string variant in ChibiParts.VariantsFor(slot))
                    foreach (ChibiPiece piece in ChibiParts.Pieces(slot, variant)) yield return piece;
            foreach (string name in ChibiParts.AccessoryNames)
                foreach (ChibiPiece piece in ChibiParts.Accessory(name)) yield return piece;
        }

        foreach (ChibiPiece piece in All())
        {
            Assert.True(Enum.TryParse(piece.Mesh, out MeshPrimitive _), $"'{piece.Mesh}' is not a MeshPrimitive");
            Assert.Contains(piece.Colour, ChibiParts.ColourSlots);
        }
    }

    // -------------------------------------------------------------------------
    // The build
    // -------------------------------------------------------------------------

    [Fact]
    public void ABuiltChibiStandsWhereTheJavaScriptEnginePutsIt()
    {
        // The same four positions html5/tests/chibi.test.js measures. The two builders read
        // one part table but interpret it separately, so only this stops them diverging.
        ChibiBuild built = ChibiBuilder.Build(ChibiRecipe.Default());
        Vector3 At(string joint) => built.Joints[joint].Position;

        Assert.Equal(0.36f, At("Hips").Y, 4);
        Assert.Equal(0.62f, At("Head").Y, 4);
        Assert.Equal(-0.155f, At("ArmR").X, 4);
        Assert.Equal(0.062f, At("FootL").X, 4);
        Assert.Equal(0.03f, At("FootL").Y, 4);
    }

    [Fact]
    public void TheTwoSidesAreMirroredNotCopied()
    {
        ChibiBuild built = ChibiBuilder.Build(ChibiRecipe.Default());
        Vector3 left = built.Joints["ArmL"].Position;
        Vector3 right = built.Joints["ArmR"].Position;

        Assert.True(left.X > 0 && right.X < 0);
        Assert.Equal(left.X, -right.X, 5);
        Assert.Equal(left.Y, right.Y, 5);
    }

    [Fact]
    public void LongerLegsRaiseTheHipsSoTheFeetStayOnTheFloor()
    {
        ChibiRecipe leggy = ChibiRecipe.Default();
        leggy.ProportionValues["legLength"] = 1.5f;

        ChibiBuild built = ChibiBuilder.Build(leggy);
        Assert.True(built.Joints["Hips"].Position.Y > 0.4f);
        Assert.True(built.Joints["FootL"].Position.Y > 0f);
    }

    [Fact]
    public void RecolouringASlotRepaintsEveryPartThatUsesIt()
    {
        ChibiBuild built = ChibiBuilder.Build(ChibiRecipe.Default());
        Assert.True(built.SetColour("top", "#123456"));

        var tops = built.Parts.Where(p => p.Colour == "top").ToList();
        Assert.NotEmpty(tops);
        Assert.All(tops, p => Assert.Equal(new Color(0x12, 0x34, 0x56), p.Renderer.AlbedoColor));
    }

    [Fact]
    public void AnAccessoryWithItsOwnColourIsNotRepaintedByItsSlot()
    {
        // A red sword stays red when the accent colour changes; that is what asking for a
        // colour on the accessory means.
        ChibiRecipe recipe = ChibiRecipe.Default();
        recipe.Accessories.Add(new ChibiRecipe.Accessory("Sword", "#FF0000"));

        ChibiBuild built = ChibiBuilder.Build(recipe);
        built.SetColour("accent", "#00FF00");

        var overridden = built.Parts.Where(p => p.Colour == null).ToList();
        Assert.Equal(3, overridden.Count);
        Assert.All(overridden, p => Assert.Equal(Color.Red, p.Renderer.AlbedoColor));
    }

    // -------------------------------------------------------------------------
    // The clips
    // -------------------------------------------------------------------------

    [Fact]
    public void EveryClipPosesOnlyJointsTheRigHas()
    {
        // A clip naming a joint that does not exist is silently ignored by the animator, so
        // a limb simply never moves and nobody knows why.
        var pose = new ChibiPose();
        foreach (string name in ChibiClips.Names)
        {
            ChibiClip clip = ChibiClips.Find(name)!;
            foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                pose.Clear();
                clip.Sample(t, pose, 1f);
                foreach (var (joint, rotation) in pose.Joints)
                {
                    Assert.Contains(joint, ChibiParts.JointNames);
                    Assert.False(float.IsNaN(rotation.X + rotation.Y + rotation.Z), $"{name}/{joint}");
                }
            }
        }
    }

    [Fact]
    public void TheClipsSampleToTheSameNumbersTheJavaScriptSideProduces()
    {
        // One procedural and one keyed, sampled where interpolation is actually doing work.
        var pose = new ChibiPose();
        ChibiClips.Find("walk")!.Sample(0.25f, pose, 1f);
        Assert.Equal(30f, pose.Get("ThighL").X, 3);
        Assert.Equal(-30f, pose.Get("ThighR").X, 3);

        pose.Clear();
        ChibiClips.Find("wave")!.Sample(0.5f, pose, 1f);
        Assert.Equal(-8f, pose.Get("ForearmR").X, 3);
        Assert.Equal(7.2f, pose.Get("ForearmR").Z, 3);
    }

    [Fact]
    public void WalkingSwingsTheLegsInOpposition()
    {
        // The one thing a walk cycle has to get right. Both legs forward at once is the
        // failure this catches.
        var pose = new ChibiPose();
        ChibiClips.Find("walk")!.Sample(0.25f, pose, 1f);

        Assert.True(pose.Get("ThighL").X * pose.Get("ThighR").X < 0);
        Assert.True(pose.Get("ArmL").X * pose.Get("ThighL").X < 0);
    }

    // -------------------------------------------------------------------------
    // The components
    // -------------------------------------------------------------------------

    private static (Engine.Core.Scene Scene, Actor Actor, ChibiCharacter Character) SceneWithChibi(int seed = 0)
    {
        var scene = new Engine.Core.Scene("chibi");
        var actor = new Actor("Villager");
        actor.AddComponent<Transform3D>();
        var character = actor.AddComponent<ChibiCharacter>();
        character.Seed = seed;
        scene.AddActor(actor);
        scene.FlushPendingActors();
        return (scene, actor, character);
    }

    [Fact]
    public void ChibiCharacterBuildsItsBodyUnderItsOwnActor()
    {
        var (scene, actor, character) = SceneWithChibi();
        try
        {
            character.Rebuild();
            scene.FlushPendingActors();

            Assert.NotNull(character.Chibi);
            Assert.Same(actor, character.Chibi!.Actor.Parent);
            Assert.Equal(ChibiParts.JointNames.Count, character.Chibi.Joints.Count);
            Assert.True(actor.Descendants().Count() > 30);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void RebuildingReplacesTheBodyRatherThanStackingASecondOne()
    {
        var (scene, actor, character) = SceneWithChibi();
        try
        {
            character.Rebuild();
            scene.FlushPendingActors();
            int first = actor.Descendants().Count();

            character.Rebuild();
            scene.FlushPendingActors();
            Assert.Equal(first, actor.Descendants().Count());
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void TheAnimatorPosesTheJointsItResolved()
    {
        var (scene, actor, character) = SceneWithChibi();
        try
        {
            character.Rebuild();
            scene.FlushPendingActors();

            var animator = actor.AddComponent<ChibiAnimator>();
            animator.Clip = "walk";
            animator.Start();
            animator.Update(0.2f);

            float left = character.Chibi!.Joints["ThighL"].LocalEulerAngles.X;
            float right = character.Chibi.Joints["ThighR"].LocalEulerAngles.X;
            Assert.True(MathF.Abs(left) > 1f, $"ThighL did not move: {left}");
            Assert.True(left * right < 0, "the legs should oppose");
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void SwitchingClipsLeavesNoJointMidStride()
    {
        // A wave poses one arm. Without resetting the rest, the legs keep whatever the walk
        // left them at and the character waves while frozen in a stride.
        var (scene, actor, character) = SceneWithChibi();
        try
        {
            character.Rebuild();
            scene.FlushPendingActors();

            var animator = actor.AddComponent<ChibiAnimator>();
            animator.Start();
            animator.Play("walk", 0f);
            animator.Update(0.2f);
            Assert.True(MathF.Abs(character.Chibi!.Joints["ThighL"].LocalEulerAngles.X) > 1f);

            animator.Play("wave", 0f);
            animator.Update(0.05f);
            Assert.True(MathF.Abs(character.Chibi.Joints["ThighL"].LocalEulerAngles.X) < 0.01f,
                "the legs should have gone back to rest");
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AClipThatEndsTellsWhoeverAskedOnce()
    {
        var (scene, actor, character) = SceneWithChibi();
        try
        {
            character.Rebuild();
            scene.FlushPendingActors();

            var animator = actor.AddComponent<ChibiAnimator>();
            animator.Start();

            var finished = new List<string>();
            animator.ClipFinished += finished.Add;
            animator.Play("hit", 0f);

            for (int i = 0; i < 20; i++) animator.Update(0.05f);
            Assert.Equal(new[] { "hit" }, finished);
        }
        finally { scene.Destroy(); }
    }

    // -------------------------------------------------------------------------
    // The Chibi global, under Jint
    // -------------------------------------------------------------------------

    private static (Engine.Core.Scene Scene, JintRuntime Runtime) ScriptRuntime()
    {
        var scene = new Engine.Core.Scene("chibi-script");
        Actor host = scene.AddActor(new Actor("Host"));
        scene.FlushPendingActors();
        return (scene, new JintRuntime(host));
    }

    [Fact]
    public void AScriptCanSpawnACharacterAndPlayAClip()
    {
        // ScriptBridgeParityTests only pins the member names. This is what proves the
        // members do anything -- and it is the shape every bundled script would use.
        var (scene, runtime) = ScriptRuntime();
        try
        {
            runtime.Evaluate("var villager = Chibi.random(1001, 2, 0, -3);");
            scene.FlushPendingActors();

            Actor? spawned = scene.FindByName("Chibi 1001");
            Assert.NotNull(spawned);
            Assert.Equal(2f, spawned!.GetComponent<Transform3D>()!.LocalPosition.X, 4);

            var character = spawned.GetComponent<ChibiCharacter>()!;
            character.Rebuild();
            scene.FlushPendingActors();

            Assert.Equal("true", runtime.Evaluate("Chibi.play(villager, 'walk')")!.ToString());
            Assert.Equal("walk", spawned.GetComponent<ChibiAnimator>()!.Clip);
            Assert.Equal("false", runtime.Evaluate("Chibi.play(villager, 'moonwalk')")!.ToString());
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AScriptCanRecolourAndRedressACharacter()
    {
        var (scene, runtime) = ScriptRuntime();
        try
        {
            runtime.Evaluate("var v = Chibi.random(7, 0, 0, 0);");
            scene.FlushPendingActors();
            Actor spawned = scene.FindByName("Chibi 7")!;
            spawned.GetComponent<ChibiCharacter>()!.Rebuild();
            scene.FlushPendingActors();

            Assert.Equal("true", runtime.Evaluate("Chibi.setColour(v, 'top', '#123456')")!.ToString());
            Assert.Equal("#123456", spawned.GetComponent<ChibiCharacter>()!.Chibi!.Recipe.Colours["top"]);

            Assert.Equal("false", runtime.Evaluate("Chibi.setColour(v, 'eyebrows', '#000')")!.ToString());
            Assert.Equal("true", runtime.Evaluate("Chibi.setStyle(v, 'hair', 'Mohawk')")!.ToString());
            scene.FlushPendingActors();
            Assert.Equal("Mohawk", spawned.GetComponent<ChibiCharacter>()!.Chibi!.Recipe.Style["hair"]);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void AScriptCanHangSomethingOffASocket()
    {
        // The reason the sockets exist, and the first real use of last week's attachment
        // API from a script: an actor a script created has no attachTo of its own.
        var (scene, runtime) = ScriptRuntime();
        try
        {
            runtime.Evaluate("var v = Chibi.random(3, 0, 0, 0);");
            scene.FlushPendingActors();
            scene.FindByName("Chibi 3")!.GetComponent<ChibiCharacter>()!.Rebuild();
            scene.FlushPendingActors();

            runtime.Evaluate("var torch = Scene.createActor('Torch', 0, 0);");
            scene.FlushPendingActors();

            Assert.Equal("true", runtime.Evaluate("Chibi.attach(v, 'Hand_R', torch)")!.ToString());
            Assert.Equal("Socket_Hand_R", scene.FindByName("Torch")!.Parent?.Name);

            Assert.Equal("false", runtime.Evaluate("Chibi.attach(v, 'Tail', torch)")!.ToString());
            Assert.Equal("object", runtime.Evaluate("typeof Chibi.socket(v, 'Head')")!.ToString());
            Assert.Equal("null", runtime.Evaluate("String(Chibi.socket(v, 'Tail'))")!.ToString());
        }
        finally { scene.Destroy(); }
    }
}
