using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Animation;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Demo.Actors;
using SexyBiscuit.Demo.Systems;

namespace SexyBiscuit.Demo.UI;

/// <summary>
/// HUD for the ATB battle screen: gauges, the action menu, move and item popups,
/// damage numbers, turn order and a battle log.
/// </summary>
/// <remarks>
/// The static shape is a document; the lists that change with the fight are built as
/// nodes at the moment they change. Columns lay themselves out, so adding a move to a
/// pet does not mean choosing a Y for it.
/// </remarks>
public class BattleHUD : Actor
{
    private const string Document = """
    {
      "children": [
        {
          "name": "atb", "absolute": true, "anchor": "topright", "x": -20, "y": 20,
          "width": 200, "layout": "column", "gap": 8,
          "children": [
            { "kind": "label", "text": "ATB", "tint": "#d3d3d3", "align": "center" },
            { "name": "atbRows", "layout": "column", "gap": 12 }
          ]
        },

        {
          "name": "turnOrder", "absolute": true, "anchor": "topright", "x": -20, "y": 200,
          "width": 200, "height": 400, "background": "#0a0a1eb4",
          "layout": "column", "gap": 4, "padding": 6
        },

        {
          "name": "logBg", "absolute": true, "anchor": "bottomleft", "x": 60, "y": -290,
          "width": 1060, "height": 110, "background": "#05051ec8", "padding": 10,
          "children": [
            { "name": "battleLog", "kind": "label", "text": "", "wrapText": true,
              "width": "*", "tint": "#ffffe0" }
          ]
        },

        {
          "name": "actionMenu", "visible": false,
          "absolute": true, "anchor": "bottomleft", "x": 60, "y": -40,
          "width": 520, "height": "auto", "background": "#0a0a1edc",
          "layout": "column", "gap": 4, "padding": 10, "crossAlign": "stretch"
        },

        {
          "name": "moveList", "visible": false,
          "absolute": true, "anchor": "bottomleft", "x": 620, "y": -40,
          "width": 500, "height": "auto", "background": "#0a140adc",
          "layout": "column", "gap": 4, "padding": 8, "crossAlign": "stretch"
        },

        {
          "name": "itemList", "visible": false,
          "absolute": true, "anchor": "bottomleft", "x": 620, "y": -40,
          "width": 500, "height": "auto", "background": "#140a0adc",
          "layout": "column", "gap": 4, "padding": 8, "crossAlign": "stretch"
        }
      ]
    }
    """;

    private const int MaxLogLines = 6;

    private UiCanvas _canvas = null!;
    private readonly UiClicks _clicks = new();

    private UiNode _atbRows = null!;
    private UiNode _actionMenu = null!;
    private UiNode _moveList = null!;
    private UiNode _itemList = null!;
    private UiNode _battleLog = null!;

    private readonly List<UiNode> _atbBars = new();
    private readonly Queue<string> _logLines = new();

    private BattleCombatantActor? _activeCombatant;

    public BattleHUD() : base("BattleHUD")
    {
        _canvas = AddComponent<UiCanvas>();
        _canvas.ScaleMode = UiScaleMode.Match;
        _canvas.ReferenceResolution = new Vector2(1920f, 1080f);
        _canvas.Adopt(UiDocument.FromJson(Document));

        _atbRows = _canvas.Find("atbRows")!;
        _actionMenu = _canvas.Find("actionMenu")!;
        _moveList = _canvas.Find("moveList")!;
        _itemList = _canvas.Find("itemList")!;
        _battleLog = _canvas.Find("battleLog")!;

        foreach (string action in new[] { "Attack", "Skill", "Item", "Flee", "Capture" })
        {
            string choice = action;
            UiNode button = AddButton(_actionMenu, choice, Color.White);
            _clicks.On(button, () => OnActionMenuChoice(choice));
        }
    }

    protected override void Update(float dt) => _clicks.Tick();

    /// <summary>A menu row, in the one shape every menu in this HUD uses.</summary>
    private static UiNode AddButton(UiNode parent, string text, Color tint, bool enabled = true)
        => parent.Add(new UiNode
        {
            Kind = UiKind.Button,
            Text = text,
            Tint = tint,
            HeightMode = SizeMode.Fixed,
            Height = 42f,
            Interactive = enabled,
            Background = new Color(255, 255, 255, 20),
            TextAlign = AlignMode.Center,
        });

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Rebuilds the gauge rows. Call once when the battle starts.</summary>
    public void InitialiseCombatants(IEnumerable<BattleCombatantActor> combatants)
    {
        _atbBars.Clear();
        foreach (UiNode child in new List<UiNode>(_atbRows.Children)) _atbRows.Remove(child);

        foreach (BattleCombatantActor combatant in combatants)
        {
            if (combatant.Pet == null) continue;

            UiNode row = _atbRows.Add(new UiNode { Layout = LayoutMode.Column, Gap = new Vector2(2f, 2f) });

            row.Add(new UiNode
            {
                Kind = UiKind.Label,
                Text = combatant.Pet.PetName,
                Tint = combatant.IsPlayer ? Color.LightBlue : Color.OrangeRed,
            });

            UiNode bar = row.Add(new UiNode
            {
                Kind = UiKind.Bar,
                WidthMode = SizeMode.Stretch,
                HeightMode = SizeMode.Fixed, Height = 16f,
                Value = 0f,
                Tint = combatant.IsPlayer ? Color.CornflowerBlue : Color.OrangeRed,
                Background = new Color(30, 30, 30),
            });

            _atbBars.Add(bar);
        }
    }

    /// <summary>Syncs the gauges. Call each frame from the battle scene.</summary>
    public void UpdateAtbBars(IList<BattleCombatantActor> combatants)
    {
        for (int i = 0; i < _atbBars.Count && i < combatants.Count; i++)
            _atbBars[i].Value = Math.Clamp(combatants[i].AtbGauge / 100f, 0f, 1f);
    }

    public void ShowActionMenu(BattleCombatantActor playerCombatant)
    {
        _actionMenu.Visible = true;
        _moveList.Visible = false;
        _itemList.Visible = false;
        _activeCombatant = playerCombatant;
    }

    public void HideActionMenu()
    {
        _actionMenu.Visible = false;
        _moveList.Visible = false;
        _itemList.Visible = false;
    }

    /// <summary>Appends a line to the scrolling battle log.</summary>
    public void Log(string message)
    {
        _logLines.Enqueue(message);
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();

        _battleLog.Text = string.Join("\n", _logLines);
    }

    /// <summary>
    /// A damage number that floats up and fades. <paramref name="position"/> is in
    /// reference-resolution coordinates.
    /// </summary>
    public void ShowDamageNumber(Vector2 position, int amount, Color color)
    {
        UiNode label = _canvas.Root.Add(new UiNode
        {
            Kind = UiKind.Label,
            Text = amount.ToString(),
            Tint = color,
            Positioning = PositionMode.Absolute,
            Offset = position,
            TextAlign = AlignMode.Center,
        });

        Tween.Create()
            .TweenFloat(label, nameof(label.Opacity), 0f, 1.2f, EaseType.Linear)
            .TweenValue(
                () => label.Offset.Y,
                y => label.Offset = new Vector2(label.Offset.X, y),
                position.Y - 80f,
                1.2f,
                EaseType.OutQuad)
            .OnComplete(label.Detach)
            .Play();
    }

    // -------------------------------------------------------------------------
    // Menus
    // -------------------------------------------------------------------------

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
                    var player = Scene?.FindActorsOfType<PlayerActor>().FirstOrDefault();
                    var wild = Scene?.FindActorsOfType<WildPetActor>().FirstOrDefault();
                    if (player != null && wild != null)
                        BattleManager.AttemptCapture(player, wild, 1f);
                }
                break;
            }
        }
    }

    /// <summary>
    /// Rebuilds a popup's rows, forgetting the handlers the old ones carried.
    /// </summary>
    private void Repopulate(UiNode panel)
    {
        foreach (UiNode child in new List<UiNode>(panel.Children)) panel.Remove(child);

        // The action menu's own handlers are re-registered below, because clearing is
        // the only way to be sure a removed row cannot still fire.
        _clicks.Clear();

        foreach (UiNode button in _actionMenu.Children)
        {
            string choice = button.Text;
            _clicks.On(button, () => OnActionMenuChoice(choice));
        }
    }

    private void ShowMoveList(BattleCombatantActor combatant)
    {
        _actionMenu.Visible = false;
        _moveList.Visible = true;
        Repopulate(_moveList);

        if (combatant.Pet == null) return;

        var target = BattleManager.Combatants.FirstOrDefault(c => !c.IsPlayer);
        if (target == null) return;

        foreach (var move in combatant.Pet.Moves)
        {
            var m = move;
            bool hasMp = combatant.Pet.Mp >= move.MpCost;

            UiNode button = AddButton(_moveList, $"{m.Name}  [{m.MpCost}MP]",
                                      hasMp ? Color.White : Color.Gray, hasMp);
            if (!hasMp) continue;

            _clicks.On(button, () =>
            {
                _moveList.Visible = false;
                BattleManager.ExecuteMove(m, combatant, target);
                Log($"{combatant.Pet?.PetName} used {m.Name}!");
            });
        }

        AddBackButton(_moveList);
    }

    private void ShowItemList()
    {
        _actionMenu.Visible = false;
        _itemList.Visible = true;
        Repopulate(_itemList);

        foreach (string id in new[] { "potion", "super_potion", "biscuit_ball", "great_ball" })
        {
            var def = ItemDatabase.Get(id);
            if (def == null) continue;

            var captured = def;
            UiNode button = AddButton(_itemList, captured.Name, Color.White);
            _clicks.On(button, () => UseItem(captured));
        }

        AddBackButton(_itemList);
    }

    private void AddBackButton(UiNode panel)
    {
        UiNode back = AddButton(panel, "Back", Color.LightGray);
        _clicks.On(back, () =>
        {
            panel.Visible = false;
            _actionMenu.Visible = true;
        });
    }

    private void UseItem(ItemDef def)
    {
        _itemList.Visible = false;

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
