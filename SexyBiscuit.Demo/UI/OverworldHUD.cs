using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Demo.Actors;
using SexyBiscuit.Demo.Systems;

namespace SexyBiscuit.Demo.UI;

/// <summary>
/// In-game heads-up display shown during overworld traversal: the player's HP and MP,
/// party portraits, a minimap stub, an area-name toast and an interaction prompt.
/// </summary>
/// <remarks>
/// Built as a document rather than assembled widget by widget. Rows and columns size
/// themselves, so the bars line up without anybody choosing a pixel for each one, and
/// the portraits stay a row whatever their count.
/// </remarks>
public class OverworldHUD : Actor
{
    private const string Document = """
    {
      "layout": "None",
      "children": [
        {
          "name": "vitals", "absolute": true, "anchor": "topleft", "x": 20, "y": 20,
          "layout": "column", "gap": 8,
          "children": [
            {
              "layout": "row", "gap": 8, "crossAlign": "center",
              "children": [
                { "kind": "label", "text": "HP", "width": 40, "tint": "#ffffff" },
                { "name": "hp", "kind": "bar", "width": 200, "height": 20,
                  "tint": "#3cc83c", "background": "#1e3c1e" }
              ]
            },
            {
              "layout": "row", "gap": 8, "crossAlign": "center",
              "children": [
                { "kind": "label", "text": "MP", "width": 40, "tint": "#ffffff" },
                { "name": "mp", "kind": "bar", "width": 200, "height": 20,
                  "tint": "#3c78dc", "background": "#141e3c" }
              ]
            }
          ]
        },

        {
          "name": "party", "absolute": true, "anchor": "bottomleft", "x": 20, "y": -20,
          "layout": "row", "gap": 10,
          "children": [
            { "name": "pet0", "width": 120, "height": 80, "background": "#141428c8",
              "layout": "column", "gap": 4, "padding": 4,
              "children": [
                { "name": "pet0name", "kind": "label", "text": "---", "tint": "#d3d3d3" },
                { "kind": "spacer", "grow": 1 },
                { "name": "pet0hp", "kind": "bar", "height": 14, "value": 0,
                  "tint": "#3cc83c", "background": "#1e3c1e" }
              ]
            },
            { "name": "pet1", "width": 120, "height": 80, "background": "#141428c8",
              "layout": "column", "gap": 4, "padding": 4,
              "children": [
                { "name": "pet1name", "kind": "label", "text": "---", "tint": "#d3d3d3" },
                { "kind": "spacer", "grow": 1 },
                { "name": "pet1hp", "kind": "bar", "height": 14, "value": 0,
                  "tint": "#3cc83c", "background": "#1e3c1e" }
              ]
            },
            { "name": "pet2", "width": 120, "height": 80, "background": "#141428c8",
              "layout": "column", "gap": 4, "padding": 4,
              "children": [
                { "name": "pet2name", "kind": "label", "text": "---", "tint": "#d3d3d3" },
                { "kind": "spacer", "grow": 1 },
                { "name": "pet2hp", "kind": "bar", "height": 14, "value": 0,
                  "tint": "#3cc83c", "background": "#1e3c1e" }
              ]
            }
          ]
        },

        {
          "name": "minimap", "absolute": true, "anchor": "topright", "x": -20, "y": 20,
          "width": 160, "height": 160, "background": "#0a140ac8",
          "layout": "column", "mainAlign": "center",
          "children": [
            { "kind": "label", "text": "MAP", "tint": "#90ee90", "align": "center" }
          ]
        },

        { "name": "areaToast", "kind": "label", "text": "", "visible": false, "opacity": 0,
          "absolute": true, "anchor": "top", "y": 200, "width": "100%", "align": "center",
          "tint": "#ffffff" },

        { "name": "interact", "kind": "label", "text": "Press E to interact", "visible": false,
          "absolute": true, "anchor": "bottom", "y": -240, "width": "100%", "align": "center",
          "tint": "#ffff00" }
    ]
    }
    """;

    private readonly PlayerActor _player;

    private UiNode _hpBar = null!;
    private UiNode _mpBar = null!;
    private readonly UiNode[] _petHpBars = new UiNode[3];
    private readonly UiNode[] _petLabels = new UiNode[3];

    private UiNode _areaLabel = null!;
    private float  _areaToastTimer;
    private Tween? _areaToastTween;

    private UiNode _interactPrompt = null!;

    /// <summary>Whether the "press E" prompt is on screen.</summary>
    public bool ShowInteractPrompt
    {
        get => _interactPrompt.Visible;
        set => _interactPrompt.Visible = value;
    }

    public OverworldHUD(PlayerActor player) : base("OverworldHUD")
    {
        _player = player;

        var canvas = AddComponent<UiCanvas>();
        canvas.ScaleMode = UiScaleMode.Match;
        canvas.ReferenceResolution = new Microsoft.Xna.Framework.Vector2(1920f, 1080f);
        canvas.Adopt(UiDocument.FromJson(Document));

        _hpBar = canvas.Find("hp")!;
        _mpBar = canvas.Find("mp")!;

        for (int i = 0; i < 3; i++)
        {
            _petLabels[i] = canvas.Find($"pet{i}name")!;
            _petHpBars[i] = canvas.Find($"pet{i}hp")!;
        }

        _areaLabel = canvas.Find("areaToast")!;
        _interactPrompt = canvas.Find("interact")!;
    }

    /// <summary>Shows the area-name toast for three seconds, then fades it out.</summary>
    public void ShowAreaName(string areaName)
    {
        _areaLabel.Text = areaName;
        _areaLabel.Visible = true;
        _areaLabel.Opacity = 1f;
        _areaToastTimer = 3f;

        _areaToastTween?.Kill();
        _areaToastTween = null;
    }

    protected override void Update(float dt)
    {
        SyncPlayerBars();
        SyncPartyPortraits();
        TickAreaToast(dt);
    }

    /// <summary>A bar is a fraction of itself, so the maximum is divided out here.</summary>
    private static float Fraction(float value, float max) => max <= 0f ? 0f : Math.Clamp(value / max, 0f, 1f);

    private void SyncPlayerBars()
    {
        _hpBar.Value = Fraction(_player.Hp, _player.MaxHp);
        _mpBar.Value = Fraction(_player.Mp, _player.MaxMp);
    }

    private void SyncPartyPortraits()
    {
        var party = PartyManager.Party;

        for (int i = 0; i < 3; i++)
        {
            if (i < party.Count)
            {
                var pet = party[i];
                _petLabels[i].Text = pet.PetName;
                _petHpBars[i].Value = Fraction(pet.Hp, pet.MaxHp);
                _petHpBars[i].Tint = pet.IsFainted
                    ? Microsoft.Xna.Framework.Color.DarkRed
                    : new Microsoft.Xna.Framework.Color(60, 200, 60);
            }
            else
            {
                _petLabels[i].Text = "---";
                _petHpBars[i].Value = 0f;
            }
        }
    }

    private void TickAreaToast(float dt)
    {
        if (_areaToastTimer <= 0f) return;

        _areaToastTimer -= dt;
        if (_areaToastTimer > 0f) return;

        _areaToastTween = Tween.Create()
            .TweenFloat(_areaLabel, "Opacity", 0f, 0.8f, EaseType.Linear)
            .OnComplete(() => _areaLabel.Visible = false)
            .Play();
    }
}
