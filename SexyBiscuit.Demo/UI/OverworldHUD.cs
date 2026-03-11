using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;
using SexyBiscuit.Demo.Actors;
using SexyBiscuit.Demo.Systems;

namespace SexyBiscuit.Demo.UI;

/// <summary>
/// In-game heads-up display shown during overworld traversal.
/// Displays player HP/MP bars, party pet portraits, a minimap stub,
/// an area-name toast and an interaction prompt.
/// </summary>
public class OverworldHUD : Actor
{
    // -------------------------------------------------------------------------
    // References
    // -------------------------------------------------------------------------
    private readonly PlayerActor _player;

    // -------------------------------------------------------------------------
    // HP / MP bars
    // -------------------------------------------------------------------------
    private ProgressBar _hpBar = null!;
    private ProgressBar _mpBar = null!;

    // -------------------------------------------------------------------------
    // Party pet portraits (up to 3 slots)
    // -------------------------------------------------------------------------
    private readonly ProgressBar[] _petHpBars  = new ProgressBar[3];
    private readonly Label[]       _petLabels  = new Label[3];

    // -------------------------------------------------------------------------
    // Area name toast
    // -------------------------------------------------------------------------
    private Label  _areaLabel    = null!;
    private float  _areaToastTimer = 0f;
    private Tween? _areaToastTween;

    // -------------------------------------------------------------------------
    // Interaction prompt
    // -------------------------------------------------------------------------
    private Label _interactPrompt = null!;
    public bool ShowInteractPrompt
    {
        get => _interactPrompt.Visible;
        set => _interactPrompt.Visible = value;
    }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public OverworldHUD(PlayerActor player) : base("OverworldHUD")
    {
        _player = player;
        BuildUI();
    }

    // -------------------------------------------------------------------------
    // UI Construction
    // -------------------------------------------------------------------------
    private void BuildUI()
    {
        var canvas = AddComponent<Canvas>();
        canvas.ReferenceResolution = new Vector2(1920, 1080);

        BuildPlayerBars(canvas);
        BuildPartyPortraits(canvas);
        BuildMinimap(canvas);
        BuildAreaToast(canvas);
        BuildInteractPrompt(canvas);
    }

    private void BuildPlayerBars(Canvas canvas)
    {
        // HP bar — top left
        var hpLabel = canvas.AddWidget<Label>();
        hpLabel.Text      = "HP";
        hpLabel.TextColor = Color.White;
        hpLabel.Position  = new Vector2(20, 20);
        hpLabel.Size      = new Vector2(40, 24);

        _hpBar = canvas.AddWidget<ProgressBar>();
        _hpBar.Position        = new Vector2(65, 22);
        _hpBar.Size            = new Vector2(200, 20);
        _hpBar.MaxValue        = _player.MaxHp;
        _hpBar.Value           = _player.Hp;
        _hpBar.FillColor       = new Color(60, 200, 60);
        _hpBar.BackgroundColor = new Color(30, 60, 30);

        // MP bar — below HP
        var mpLabel = canvas.AddWidget<Label>();
        mpLabel.Text      = "MP";
        mpLabel.TextColor = Color.White;
        mpLabel.Position  = new Vector2(20, 50);
        mpLabel.Size      = new Vector2(40, 24);

        _mpBar = canvas.AddWidget<ProgressBar>();
        _mpBar.Position        = new Vector2(65, 52);
        _mpBar.Size            = new Vector2(200, 20);
        _mpBar.MaxValue        = _player.MaxMp;
        _mpBar.Value           = _player.Mp;
        _mpBar.FillColor       = new Color(60, 120, 220);
        _mpBar.BackgroundColor = new Color(20, 30, 60);
    }

    private void BuildPartyPortraits(Canvas canvas)
    {
        // Three portrait slots at the bottom-left
        for (int i = 0; i < 3; i++)
        {
            int idx = i; // capture

            var portrait = canvas.AddWidget<Panel>();
            portrait.Position        = new Vector2(20 + i * 130, 950);
            portrait.Size            = new Vector2(120, 80);
            portrait.BackgroundColor = new Color(20, 20, 40, 200);

            var nameLabel = new Label
            {
                Position  = new Vector2(4, 4),
                Size      = new Vector2(112, 20),
                TextColor = Color.LightGray,
                Text      = "---"
            };
            portrait.AddChild(nameLabel);
            _petLabels[i] = nameLabel;

            var petHpBar = new ProgressBar
            {
                Position        = new Vector2(4, 52),
                Size            = new Vector2(112, 14),
                MaxValue        = 1f,
                Value           = 0f,
                FillColor       = new Color(60, 200, 60),
                BackgroundColor = new Color(30, 60, 30)
            };
            portrait.AddChild(petHpBar);
            _petHpBars[i] = petHpBar;
        }
    }

    private void BuildMinimap(Canvas canvas)
    {
        var mapPanel = canvas.AddWidget<Panel>();
        mapPanel.Position        = new Vector2(1740, 20);
        mapPanel.Size            = new Vector2(160, 160);
        mapPanel.BackgroundColor = new Color(10, 20, 10, 200);

        var mapLabel = canvas.AddWidget<Label>();
        mapLabel.Text      = "MAP";
        mapLabel.TextColor = Color.LightGreen;
        mapLabel.Position  = new Vector2(1740, 88);
        mapLabel.Size      = new Vector2(160, 24);
        mapLabel.Alignment = TextAlignment.Center;
    }

    private void BuildAreaToast(Canvas canvas)
    {
        _areaLabel             = canvas.AddWidget<Label>();
        _areaLabel.Text        = "";
        _areaLabel.TextColor   = Color.White;
        _areaLabel.Position    = new Vector2(0, 200);
        _areaLabel.Size        = new Vector2(1920, 60);
        _areaLabel.Alignment   = TextAlignment.Center;
        _areaLabel.Visible     = false;
        _areaLabel.Opacity     = 0f;
    }

    private void BuildInteractPrompt(Canvas canvas)
    {
        _interactPrompt           = canvas.AddWidget<Label>();
        _interactPrompt.Text      = "Press E to interact";
        _interactPrompt.TextColor = Color.Yellow;
        _interactPrompt.Position  = new Vector2(0, 800);
        _interactPrompt.Size      = new Vector2(1920, 40);
        _interactPrompt.Alignment = TextAlignment.Center;
        _interactPrompt.Visible   = false;
    }

    // -------------------------------------------------------------------------
    // Area toast public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shows the area name toast for 3 seconds then fades it out.
    /// </summary>
    public void ShowAreaName(string areaName)
    {
        _areaLabel.Text    = areaName;
        _areaLabel.Visible = true;
        _areaLabel.Opacity = 1f;
        _areaToastTimer    = 3f;

        // Kill any existing fade tween
        _areaToastTween?.Kill();
        _areaToastTween = null;
    }

    // -------------------------------------------------------------------------
    // Update — sync bars from player/party state
    // -------------------------------------------------------------------------
    protected override void Update(float dt)
    {
        SyncPlayerBars();
        SyncPartyPortraits();
        TickAreaToast(dt);
    }

    private void SyncPlayerBars()
    {
        _hpBar.MaxValue = _player.MaxHp;
        _hpBar.Value    = _player.Hp;
        _mpBar.MaxValue = _player.MaxMp;
        _mpBar.Value    = _player.Mp;
    }

    private void SyncPartyPortraits()
    {
        var party = PartyManager.Party;
        for (int i = 0; i < 3; i++)
        {
            if (i < party.Count)
            {
                var pet = party[i];
                _petLabels[i].Text        = pet.PetName;
                _petHpBars[i].MaxValue    = Math.Max(1f, pet.MaxHp);
                _petHpBars[i].Value       = pet.Hp;
                _petHpBars[i].FillColor   = pet.IsFainted
                    ? Color.DarkRed
                    : new Color(60, 200, 60);
            }
            else
            {
                _petLabels[i].Text     = "---";
                _petHpBars[i].MaxValue = 1f;
                _petHpBars[i].Value    = 0f;
            }
        }
    }

    private void TickAreaToast(float dt)
    {
        if (_areaToastTimer <= 0f) return;

        _areaToastTimer -= dt;

        if (_areaToastTimer <= 0f)
        {
            // Start fade-out tween
            _areaToastTween = Tween.Create()
                .TweenFloat(_areaLabel, "Opacity", 0f, 0.8f,
                            SexyBiscuit.Engine.Animation.EaseType.Linear)
                .OnComplete(() => _areaLabel.Visible = false)
                .Play();
        }
    }
}
