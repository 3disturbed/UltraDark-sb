using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Rendering;
using SexyBiscuit.Engine.Save;
using SexyBiscuit.Engine.Steam;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;
using SexyBiscuit.Demo.Systems;
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

        // -----------------------------------------------------------------
        // Background — solid colour actor (dark purple, evocative)
        // -----------------------------------------------------------------
        var bgActor = new Actor("Background");
        bgActor.Transform.Position = new Vector2(960, 540); // screen centre
        var bgRenderer = bgActor.AddComponent<SpriteRenderer>();
        bgRenderer.Tint = new Color(24, 10, 48, 255);
        scene.AddActor(bgActor, "background");

        // -----------------------------------------------------------------
        // Title canvas
        // -----------------------------------------------------------------
        var uiActor = new Actor("MainMenuUI");
        var canvas  = uiActor.AddComponent<Canvas>();
        canvas.ReferenceResolution = new Vector2(1920, 1080);

        // Title label
        var title = canvas.AddWidget<Label>();
        title.Text      = "BISCUIT CHRONICLES";
        title.TextColor = new Color(255, 220, 80, 255);
        title.Position  = new Vector2(0, 180);
        title.Size      = new Vector2(1920, 120);
        title.Alignment = TextAlignment.Center;

        // Subtitle
        var subtitle = canvas.AddWidget<Label>();
        subtitle.Text      = "A SexyBiscuit Engine Demo";
        subtitle.TextColor = new Color(200, 180, 255, 200);
        subtitle.Position  = new Vector2(0, 310);
        subtitle.Size      = new Vector2(1920, 50);
        subtitle.Alignment = TextAlignment.Center;

        // -----------------------------------------------------------------
        // Button panel — centred column
        // -----------------------------------------------------------------
        var panel = canvas.AddWidget<Panel>();
        panel.Position      = new Vector2(760, 400);
        panel.Size          = new Vector2(400, 400);
        panel.LayoutMode    = PanelLayoutMode.Vertical;
        panel.BackgroundColor = new Color(0, 0, 0, 140);
        panel.Padding       = 12f;

        // Helper to build a menu button
        Button MakeButton(string text)
        {
            var btn = new Button
            {
                Text      = text,
                TextColor = Color.White,
                Size      = new Vector2(376, 60),
            };
            panel.AddChild(btn);
            return btn;
        }

        var btnNewGame     = MakeButton("New Game");
        var btnContinue    = MakeButton("Continue");
        var btnMultiplayer = MakeButton("Multiplayer");
        var btnOptions     = MakeButton("Options");
        var btnQuit        = MakeButton("Quit");

        // Gray-out Continue when no save exists
        bool hasSave = SaveManager.SlotExists(0);
        if (!hasSave)
        {
            btnContinue.Interactable = false;
            btnContinue.TextColor    = Color.Gray;
        }

        // -----------------------------------------------------------------
        // Button actions
        // -----------------------------------------------------------------
        btnNewGame.OnClick += () =>
        {
            // Start a fresh game — clear any existing save state
            PartyManager.Party.Clear();
            PartyManager.Storage.Clear();
            sm.LoadScene("Scenes/Overworld");
        };

        btnContinue.OnClick += () =>
        {
            if (!hasSave) return;
            PartyManager.Load("slot0");
            sm.LoadScene("Scenes/Overworld");
        };

        btnMultiplayer.OnClick += () =>
        {
            // Show lobby UI — swap UI panel for the lobby browser
            panel.Visible = false;
            ShowLobbyUI(canvas, sm, panel);
        };

        btnOptions.OnClick += () =>
        {
            // Placeholder: options not implemented in this wave
        };

        btnQuit.OnClick += () =>
        {
            Environment.Exit(0);
        };

        scene.AddActor(uiActor, "ui");
    }

    // -------------------------------------------------------------------------
    // Lobby UI helper
    // -------------------------------------------------------------------------
    private static void ShowLobbyUI(Canvas canvas, SceneManager sm, Panel mainPanel)
    {
        var lobbyActor = new LobbyUI();
        // LobbyUI is a standalone actor; we add it to the scene directly
        var scene = SBEngine.Instance.SceneManager.ActiveScene;
        scene?.AddActor(lobbyActor, "ui");

        // Provide a back button inside LobbyUI to restore the main panel
        // (LobbyUI exposes a public back callback)
        lobbyActor.OnBack += () =>
        {
            mainPanel.Visible = true;
            lobbyActor.Destroy();
        };
    }
}
