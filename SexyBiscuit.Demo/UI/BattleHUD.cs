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
/// HUD for the ATB battle screen.
/// Displays ATB gauges, the action menu, move/item popups,
/// damage numbers, status icons, turn order and a battle log.
/// </summary>
public class BattleHUD : Actor
{
    // -------------------------------------------------------------------------
    // References
    // -------------------------------------------------------------------------
    private Canvas _canvas = null!;

    // -------------------------------------------------------------------------
    // ATB gauge bars (one per combatant)
    // -------------------------------------------------------------------------
    private readonly List<(Label nameLabel, ProgressBar atbBar)> _atbRows = new();

    // -------------------------------------------------------------------------
    // Action menu
    // -------------------------------------------------------------------------
    private Panel  _actionMenu    = null!;
    private Panel  _moveListPanel = null!;
    private Panel  _itemListPanel = null!;

    // -------------------------------------------------------------------------
    // Battle log
    // -------------------------------------------------------------------------
    private Label _battleLog = null!;
    private readonly Queue<string> _logLines = new();
    private const int MaxLogLines = 6;

    // -------------------------------------------------------------------------
    // Turn order display
    // -------------------------------------------------------------------------
    private Panel _turnOrderPanel = null!;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public BattleHUD() : base("BattleHUD")
    {
        BuildUI();
    }

    // -------------------------------------------------------------------------
    // UI Construction
    // -------------------------------------------------------------------------
    private void BuildUI()
    {
        _canvas = AddComponent<Canvas>();
        _canvas.ReferenceResolution = new Vector2(1920, 1080);

        BuildAtbSection();
        BuildActionMenu();
        BuildMoveListPanel();
        BuildItemListPanel();
        BuildBattleLog();
        BuildTurnOrder();

        // Initially hide action menus
        _actionMenu.Visible    = false;
        _moveListPanel.Visible = false;
        _itemListPanel.Visible = false;
    }

    private void BuildAtbSection()
    {
        // ATB gauges drawn along the right side of the screen
        var atbHeader = _canvas.AddWidget<Label>();
        atbHeader.Text      = "ATB";
        atbHeader.TextColor = Color.LightGray;
        atbHeader.Position  = new Vector2(1700, 20);
        atbHeader.Size      = new Vector2(200, 28);
        atbHeader.Alignment = TextAlignment.Center;
    }

    private void BuildActionMenu()
    {
        _actionMenu              = _canvas.AddWidget<Panel>();
        _actionMenu.Position     = new Vector2(60, 800);
        _actionMenu.Size         = new Vector2(520, 240);
        _actionMenu.LayoutMode   = PanelLayoutMode.Vertical;
        _actionMenu.BackgroundColor = new Color(10, 10, 30, 220);
        _actionMenu.Padding      = 10f;

        string[] actions = { "Attack", "Skill", "Item", "Flee", "Capture" };
        foreach (var action in actions)
        {
            string captured = action;
            var btn = new Button
            {
                Text      = captured,
                TextColor = Color.White,
                Size      = new Vector2(500, 40),
            };
            btn.OnClick += () => OnActionMenuChoice(captured);
            _actionMenu.AddChild(btn);
        }
    }

    private void BuildMoveListPanel()
    {
        _moveListPanel              = _canvas.AddWidget<Panel>();
        _moveListPanel.Position     = new Vector2(620, 800);
        _moveListPanel.Size         = new Vector2(500, 240);
        _moveListPanel.LayoutMode   = PanelLayoutMode.Vertical;
        _moveListPanel.BackgroundColor = new Color(10, 20, 10, 220);
        _moveListPanel.Padding      = 8f;
    }

    private void BuildItemListPanel()
    {
        _itemListPanel              = _canvas.AddWidget<Panel>();
        _itemListPanel.Position     = new Vector2(620, 800);
        _itemListPanel.Size         = new Vector2(500, 240);
        _itemListPanel.LayoutMode   = PanelLayoutMode.Vertical;
        _itemListPanel.BackgroundColor = new Color(20, 10, 10, 220);
        _itemListPanel.Padding      = 8f;
    }

    private void BuildBattleLog()
    {
        var logBg = _canvas.AddWidget<Panel>();
        logBg.Position        = new Vector2(60, 680);
        logBg.Size            = new Vector2(1060, 110);
        logBg.BackgroundColor = new Color(5, 5, 20, 200);

        _battleLog           = _canvas.AddWidget<Label>();
        _battleLog.Position  = new Vector2(70, 686);
        _battleLog.Size      = new Vector2(1040, 100);
        _battleLog.TextColor = Color.LightYellow;
        _battleLog.WordWrap  = true;
        _battleLog.Text      = "";
    }

    private void BuildTurnOrder()
    {
        _turnOrderPanel              = _canvas.AddWidget<Panel>();
        _turnOrderPanel.Position     = new Vector2(1700, 60);
        _turnOrderPanel.Size         = new Vector2(200, 400);
        _turnOrderPanel.LayoutMode   = PanelLayoutMode.Vertical;
        _turnOrderPanel.BackgroundColor = new Color(10, 10, 30, 180);
        _turnOrderPanel.Padding      = 6f;
    }

    // -------------------------------------------------------------------------
    // Public API — update state
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds ATB gauge rows from the current combatant list.
    /// Call once when the battle starts.
    /// </summary>
    public void InitialiseCombatants(IEnumerable<BattleCombatantActor> combatants)
    {
        _atbRows.Clear();

        // Clear old widgets from ATB section (rebuild from scratch each time)
        int rowY = 50;
        foreach (var combatant in combatants)
        {
            if (combatant.Pet == null) continue;

            var nameLabel = _canvas.AddWidget<Label>();
            nameLabel.Text      = combatant.Pet.PetName;
            nameLabel.TextColor = combatant.IsPlayer ? Color.LightBlue : Color.OrangeRed;
            nameLabel.Position  = new Vector2(1700, rowY);
            nameLabel.Size      = new Vector2(100, 24);

            var atbBar = _canvas.AddWidget<ProgressBar>();
            atbBar.Position        = new Vector2(1808, rowY + 4);
            atbBar.Size            = new Vector2(92, 16);
            atbBar.MaxValue        = 100f;
            atbBar.Value           = 0f;
            atbBar.FillColor       = combatant.IsPlayer ? Color.CornflowerBlue : Color.OrangeRed;
            atbBar.BackgroundColor = new Color(30, 30, 30);

            _atbRows.Add((nameLabel, atbBar));
            rowY += 36;
        }
    }

    /// <summary>
    /// Syncs ATB bar values to the current combatant gauges.
    /// Call each frame from the battle scene.
    /// </summary>
    public void UpdateAtbBars(IList<BattleCombatantActor> combatants)
    {
        for (int i = 0; i < _atbRows.Count && i < combatants.Count; i++)
        {
            _atbRows[i].atbBar.Value = combatants[i].AtbGauge;
        }
    }

    /// <summary>
    /// Shows the action menu for the player's active combatant.
    /// </summary>
    public void ShowActionMenu(BattleCombatantActor playerCombatant)
    {
        _actionMenu.Visible    = true;
        _moveListPanel.Visible = false;
        _itemListPanel.Visible = false;
        _activeCombatant       = playerCombatant;
    }

    /// <summary>
    /// Hides all action menus (used after a choice is made or during enemy turn).
    /// </summary>
    public void HideActionMenu()
    {
        _actionMenu.Visible    = false;
        _moveListPanel.Visible = false;
        _itemListPanel.Visible = false;
    }

    /// <summary>
    /// Appends a line to the scrolling battle log.
    /// </summary>
    public void Log(string message)
    {
        _logLines.Enqueue(message);
        while (_logLines.Count > MaxLogLines)
            _logLines.Dequeue();

        _battleLog.Text = string.Join("\n", _logLines);
    }

    // -------------------------------------------------------------------------
    // Damage number display
    // -------------------------------------------------------------------------

    /// <summary>
    /// Spawns a floating damage number that tweens upward and fades over 1.2 seconds.
    /// <paramref name="worldPos"/> is in UI (reference-resolution) coordinates.
    /// </summary>
    public void ShowDamageNumber(Vector2 worldPos, int amount, Color color)
    {
        var floatLabel = _canvas.AddWidget<Label>();
        floatLabel.Text      = amount.ToString();
        floatLabel.TextColor = color;
        floatLabel.Position  = worldPos;
        floatLabel.Size      = new Vector2(120, 48);
        floatLabel.Alignment = TextAlignment.Center;
        floatLabel.Opacity   = 1f;

        // Tween: float upward 80 pixels while fading opacity to 0
        Tween.Create()
            .TweenFloat(floatLabel, nameof(floatLabel.Opacity), 0f, 1.2f, EaseType.Linear)
            .TweenValue(
                () => floatLabel.Position.Y,
                y  => floatLabel.Position = new Vector2(floatLabel.Position.X, y),
                worldPos.Y - 80f,
                1.2f,
                EaseType.OutQuad)
            .OnComplete(() => _canvas.RemoveWidget(floatLabel))
            .Play();
    }

    // -------------------------------------------------------------------------
    // Internal action handling
    // -------------------------------------------------------------------------
    private BattleCombatantActor? _activeCombatant;

    private void OnActionMenuChoice(string choice)
    {
        if (_activeCombatant == null) return;

        switch (choice)
        {
            case "Attack":
            case "Skill":
                ShowMoveList(_activeCombatant);
                break;

            case "Item":
                ShowItemList();
                break;

            case "Flee":
                HideActionMenu();
                BattleManager.EndBattle(playerWon: false);
                break;

            case "Capture":
            {
                HideActionMenu();
                var target = BattleManager.Combatants.FirstOrDefault(c => !c.IsPlayer);
                if (target != null && BattleManager.ActiveCombatant != null)
                {
                    // Use default Biscuit Ball (multiplier 1)
                    var player = Scene?.FindActorsOfType<SexyBiscuit.Demo.Actors.PlayerActor>()
                                      .FirstOrDefault();
                    var wild   = Scene?.FindActorsOfType<WildPetActor>().FirstOrDefault();
                    if (player != null && wild != null)
                        BattleManager.AttemptCapture(player, wild, 1f);
                }
                break;
            }
        }
    }

    private void ShowMoveList(BattleCombatantActor combatant)
    {
        _actionMenu.Visible    = false;
        _moveListPanel.Visible = true;

        // Clear and rebuild move buttons
        while (_moveListPanel.Children.Count > 0)
            _moveListPanel.RemoveChild(_moveListPanel.Children[0]);

        if (combatant.Pet == null) return;

        var target = BattleManager.Combatants.FirstOrDefault(c => !c.IsPlayer);
        if (target == null) return;

        foreach (var move in combatant.Pet.Moves)
        {
            var m   = move; // capture
            bool hasMp = combatant.Pet.Mp >= move.MpCost;

            var btn = new Button
            {
                Text         = $"{m.Name}  [{m.MpCost}MP]",
                TextColor    = hasMp ? Color.White : Color.Gray,
                Size         = new Vector2(480, 44),
                Interactable = hasMp,
            };
            btn.OnClick += () =>
            {
                _moveListPanel.Visible = false;
                BattleManager.ExecuteMove(m, combatant, target);
                Log($"{combatant.Pet?.PetName} used {m.Name}!");
            };
            _moveListPanel.AddChild(btn);
        }

        // Back button
        var backBtn = new Button { Text = "Back", TextColor = Color.LightGray, Size = new Vector2(480, 40) };
        backBtn.OnClick += () =>
        {
            _moveListPanel.Visible = false;
            _actionMenu.Visible    = true;
        };
        _moveListPanel.AddChild(backBtn);
    }

    private void ShowItemList()
    {
        _actionMenu.Visible    = false;
        _itemListPanel.Visible = true;

        while (_itemListPanel.Children.Count > 0)
            _itemListPanel.RemoveChild(_itemListPanel.Children[0]);

        // List consumables and capture items (inventory would be tracked in a real game;
        // here we seed a minimal default set)
        var itemIds = new[] { "potion", "super_potion", "biscuit_ball", "great_ball" };
        foreach (var id in itemIds)
        {
            var def = ItemDatabase.Get(id);
            if (def == null) continue;
            var capturedDef = def;

            var btn = new Button
            {
                Text      = capturedDef.Name,
                TextColor = Color.White,
                Size      = new Vector2(480, 44),
            };
            btn.OnClick += () => UseItem(capturedDef);
            _itemListPanel.AddChild(btn);
        }

        var backBtn = new Button { Text = "Back", TextColor = Color.LightGray, Size = new Vector2(480, 40) };
        backBtn.OnClick += () =>
        {
            _itemListPanel.Visible = false;
            _actionMenu.Visible    = true;
        };
        _itemListPanel.AddChild(backBtn);
    }

    private void UseItem(ItemDef def)
    {
        _itemListPanel.Visible = false;

        var lead = PartyManager.GetLead();
        if (lead == null) return;

        if (def.Type == ItemType.Consumable && def.HealAmount > 0)
        {
            lead.Hp = Math.Min(lead.MaxHp, lead.Hp + def.HealAmount);
            Log($"Used {def.Name} — {lead.PetName} recovered {def.HealAmount} HP!");
        }
        else if (def.Type == ItemType.CaptureItem)
        {
            var wild = Scene?.FindActorsOfType<WildPetActor>().FirstOrDefault();
            var player = Scene?.FindActorsOfType<PlayerActor>().FirstOrDefault();
            if (wild != null && player != null)
            {
                Log($"Threw a {def.Name}!");
                BattleManager.AttemptCapture(player, wild, def.BallMultiplier);
            }
        }

        if (_activeCombatant != null)
        {
            _activeCombatant.ResetGauge();
            BattleManager.ActiveCombatant = null;
        }
    }
}
