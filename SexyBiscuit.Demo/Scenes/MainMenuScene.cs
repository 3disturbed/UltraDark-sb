using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Demo.UI;

namespace SexyBiscuit.Demo.Scenes;

/// <summary>
/// Code-first loader for the Main Menu scene.
/// </summary>
public static class MainMenuScene
{
    public static void Load(SceneManager sm)
    {
        var scene = sm.CreateScene("MainMenu");

        var background = new Actor("Background");
        background.Transform.Position = new Vector2(960f, 540f);
        background.AddComponent<SpriteRenderer>().Tint = new Color(24, 10, 48, 255);
        scene.AddActor(background, "background");

        scene.AddActor(new MainMenuUI(sm), "ui");
    }
}
