using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Save;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Demo.Systems;

namespace SexyBiscuit.Demo.UI;

/// <summary>
/// The title screen: a centred column of buttons over the game's name.
/// </summary>
/// <remarks>
/// An actor rather than something the scene loader assembles inline, because the tree
/// reports a click as a flag read each frame and something has to do the reading.
/// </remarks>
public class MainMenuUI : Actor
{
    private const string Document = """
    {
      "layout": "column", "mainAlign": "center", "crossAlign": "center", "gap": 24,
      "width": "*", "height": "*",
      "children": [
        { "kind": "label", "text": "BISCUIT CHRONICLES", "tint": "#ffdc50",
          "align": "center", "scale": 4 },
        { "kind": "label", "text": "A SexyBiscuit Engine Demo", "tint": "#c8b4ffc8",
          "align": "center", "scale": 2 },
        {
          "name": "menu", "width": 400, "background": "#0000008c",
          "layout": "column", "gap": 8, "padding": 12, "crossAlign": "stretch",
          "children": [
            { "name": "newGame",     "kind": "button", "text": "New Game",    "height": 56,
              "tint": "#ffffff", "background": "#ffffff14", "align": "center", "autoFocus": true },
            { "name": "continue",    "kind": "button", "text": "Continue",    "height": 56,
              "tint": "#ffffff", "background": "#ffffff14", "align": "center" },
            { "name": "multiplayer", "kind": "button", "text": "Multiplayer", "height": 56,
              "tint": "#ffffff", "background": "#ffffff14", "align": "center" },
            { "name": "options",     "kind": "button", "text": "Options",     "height": 56,
              "tint": "#ffffff", "background": "#ffffff14", "align": "center" },
            { "name": "quit",        "kind": "button", "text": "Quit",        "height": 56,
              "tint": "#ffffff", "background": "#ffffff14", "align": "center" }
          ]
        }
      ]
    }
    """;

    private readonly UiClicks _clicks = new();
    private readonly SceneManager _scenes;
    private UiNode _menu = null!;

    public MainMenuUI(SceneManager scenes) : base("MainMenuUI")
    {
        _scenes = scenes;

        var canvas = AddComponent<UiCanvas>();
        canvas.ScaleMode = UiScaleMode.Match;
        canvas.ReferenceResolution = new Vector2(1920f, 1080f);
        canvas.Adopt(UiDocument.FromJson(Document));

        _menu = canvas.Find("menu")!;

        // Continue is dimmed rather than hidden when there is nothing to continue:
        // a missing row moves everything below it and reads as a different menu.
        bool hasSave = SaveManager.SlotExists(0);
        UiNode continueButton = canvas.Find("continue")!;
        if (!hasSave)
        {
            continueButton.Interactive = false;
            continueButton.Tint = Color.Gray;
        }

        _clicks.On(canvas.Find("newGame")!, () =>
        {
            PartyManager.Party.Clear();
            PartyManager.Storage.Clear();
            _scenes.LoadScene("Scenes/Overworld");
        });

        _clicks.On(continueButton, () =>
        {
            if (!hasSave) return;
            PartyManager.Load("slot0");
            _scenes.LoadScene("Scenes/Overworld");
        });

        _clicks.On(canvas.Find("multiplayer")!, ShowLobby);
        _clicks.On(canvas.Find("options")!, () => { /* not in this wave */ });
        _clicks.On(canvas.Find("quit")!, () => Environment.Exit(0));
    }

    protected override void Update(float dt) => _clicks.Tick();

    private void ShowLobby()
    {
        _menu.Visible = false;

        var lobby = new LobbyUI();
        SBEngine.Instance.SceneManager.ActiveScene?.AddActor(lobby, "ui");

        lobby.OnBack += () =>
        {
            _menu.Visible = true;
            lobby.Destroy();
        };
    }
}
