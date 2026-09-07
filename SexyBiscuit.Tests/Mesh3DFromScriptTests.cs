using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Scripting;
using Xunit;

namespace SexyBiscuit.Tests;

/// <summary>
/// A script can build a coloured 3D world out of actors it creates.
/// </summary>
/// <remarks>
/// <para>
/// Every piece of this is reachable only through the property bag, and the bag
/// has a trap in it. The OUTER key is matched case-insensitively against the
/// component's schema, so <c>MeshType</c> and <c>meshType</c> both work — but
/// the contents of a material are handed to <c>Material3D</c> as-is, and
/// <b>those keys are case-sensitive</b>. Writing <c>AlbedoColor</c> where
/// <c>albedoColor</c> was meant does not warn and does not throw: it builds a
/// material and leaves it white. A whole city comes out the colour of nothing.
/// </para>
/// <para>
/// Both engines behave the same way, which is the only reason this is a trap
/// rather than a divergence. This test and its browser twin in
/// <c>tests/bridge.test.js</c> hold them there.
/// </para>
/// </remarks>
public class Mesh3DFromScriptTests
{
    private static Actor BuildFromScript(Engine.Core.Scene scene, ScriptProject project, string body)
    {
        var host = scene.AddActor(new Actor("Host"));
        var script = host.AddComponent<ScriptComponent>();
        script.ScriptPath = project.Write("Host.js", "function onStart() {" + body + "}");

        scene.FlushPendingActors();
        scene.Update(0f);
        scene.FlushPendingActors();
        return scene.FindByName("Box")!;
    }

    [Fact]
    public void AScriptCanCreateAPlacedScaledColouredMesh()
    {
        using var project = new ScriptProject();
        var scene = new Engine.Core.Scene("Mesh3D");
        try
        {
            var box = BuildFromScript(scene, project, @"
                var a = Scene.createActor('Box', 0, 0);
                Scene.addComponent(a, 'Transform3D', {});
                Scene.addComponent(a, 'MeshRenderer',
                    { meshType: 'Cube', materials: [{ albedoColor: '#2060A0FF' }] });
                a.transform3d.set(10, 2, -30, 4, 6, 8);");

            Assert.NotNull(box);

            var mesh = box.GetComponent<MeshRenderer>();
            Assert.NotNull(mesh);
            Assert.Equal(MeshPrimitive.Cube, mesh!.MeshType);

            var material = Assert.Single(mesh.Materials);
            Assert.Equal(new Microsoft.Xna.Framework.Color(32, 96, 160, 255), material.AlbedoColor);

            // Placed AND sized. A world is built out of unit primitives, so a
            // cube that cannot be scaled is not a wall, it is a crate.
            var t = box.GetComponent<Transform3D>();
            Assert.NotNull(t);
            Assert.Equal(new Microsoft.Xna.Framework.Vector3(10f, 2f, -30f), t!.Position);
            Assert.Equal(new Microsoft.Xna.Framework.Vector3(4f, 6f, 8f), t.LocalScale);
        }
        finally { scene.Destroy(); }
    }

    [Fact]
    public void MaterialKeysAreCaseSensitiveAndTheWrongCaseIsSilentlyWhite()
    {
        // Pinned deliberately as the CURRENT behaviour, not as behaviour anyone
        // wants. It cost real time; if either engine ever starts warning about an
        // unknown material key, this test should be the thing that notices.
        using var project = new ScriptProject();
        var scene = new Engine.Core.Scene("Mesh3DCase");
        try
        {
            var box = BuildFromScript(scene, project, @"
                var a = Scene.createActor('Box', 0, 0);
                Scene.addComponent(a, 'MeshRenderer',
                    { meshType: 'Cube', materials: [{ AlbedoColor: '#2060A0FF' }] });");

            var material = Assert.Single(box.GetComponent<MeshRenderer>()!.Materials);
            Assert.Equal(Microsoft.Xna.Framework.Color.White, material.AlbedoColor);
        }
        finally { scene.Destroy(); }
    }
}
