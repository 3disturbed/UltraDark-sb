using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Steam;
using SexyBiscuit.Engine.UI;

namespace SexyBiscuit.Demo.UI;

/// <summary>
/// Steam lobby browser and host UI: create, discover, join, ready up and start.
/// Works without STEAMWORKS defined — the buttons are shown and do nothing.
/// </summary>
/// <remarks>
/// The two panels are one document with one of them hidden. Rows size themselves, so a
/// lobby list is built by adding nodes rather than by choosing a height for each one.
/// </remarks>
public class LobbyUI : Actor
{
    private const string Document = """
    {
      "children": [
        { "name": "dim", "width": "*", "height": "*", "background": "#000000a0" },

        {
          "name": "main", "absolute": true, "anchor": "center",
          "width": 800, "height": 680, "background": "#0f0a1ef0",
          "layout": "column", "gap": 12, "padding": 16, "crossAlign": "stretch",
          "children": [
            { "kind": "label", "text": "MULTIPLAYER", "tint": "#ffdc50", "align": "center", "scale": 2 },
            { "name": "status", "kind": "label", "text": "Find or create a lobby to play online.",
              "tint": "#d3d3d3", "align": "center" },
            {
              "layout": "row", "gap": 16, "height": 50,
              "children": [
                { "name": "host", "kind": "button", "text": "Host Game", "grow": 1,
                  "tint": "#ffffff", "background": "#ffffff14", "align": "center" },
                { "name": "find", "kind": "button", "text": "Join Game", "grow": 1,
                  "tint": "#ffffff", "background": "#ffffff14", "align": "center" }
              ]
            },
            { "name": "lobbyList", "grow": 1, "background": "#050514b4",
              "layout": "column", "gap": 4, "padding": 8, "crossAlign": "stretch",
              "scroll": "Vertical" },
            { "name": "back", "kind": "button", "text": "Back", "height": 44,
              "tint": "#d3d3d3", "background": "#ffffff14", "align": "center" }
          ]
        },

        {
          "name": "lobby", "visible": false, "absolute": true, "anchor": "center",
          "width": 800, "height": 680, "background": "#0a0f1ef0",
          "layout": "column", "gap": 12, "padding": 16, "crossAlign": "stretch",
          "children": [
            { "name": "lobbyCode", "kind": "label", "text": "Lobby Code: --------",
              "tint": "#ffdc50", "align": "center", "scale": 2 },
            { "name": "members", "grow": 1, "background": "#050514a0",
              "layout": "column", "gap": 4, "padding": 8, "crossAlign": "stretch" },
            {
              "layout": "row", "gap": 16, "height": 50,
              "children": [
                { "name": "ready", "kind": "button", "text": "Ready", "grow": 1,
                  "tint": "#ffffff", "background": "#ffffff14", "align": "center" },
                { "name": "start", "kind": "button", "text": "Start Game", "grow": 1, "visible": false,
                  "tint": "#ffffff", "background": "#ffffff14", "align": "center" }
              ]
            },
            { "name": "leave", "kind": "button", "text": "Leave Lobby", "height": 44,
              "tint": "#d3d3d3", "background": "#ffffff14", "align": "center" }
          ]
        }
      ]
    }
    """;

    /// <summary>Raised when the user clicks Back.</summary>
    public event Action? OnBack;

    private readonly UiClicks _clicks = new();

    private UiNode _mainPanel = null!;
    private UiNode _lobbyPanel = null!;
    private UiNode _lobbyList = null!;
    private UiNode _statusLabel = null!;
    private UiNode _lobbyCodeLabel = null!;
    private UiNode _memberPanel = null!;
    private UiNode _readyBtn = null!;
    private UiNode _startBtn = null!;

    private bool _inLobby;
    private bool _isHost;
    private bool _localReady;

    public LobbyUI() : base("LobbyUI")
    {
        var canvas = AddComponent<UiCanvas>();
        canvas.ScaleMode = UiScaleMode.Match;
        canvas.ReferenceResolution = new Vector2(1920f, 1080f);
        canvas.Adopt(UiDocument.FromJson(Document));

        _mainPanel = canvas.Find("main")!;
        _lobbyPanel = canvas.Find("lobby")!;
        _lobbyList = canvas.Find("lobbyList")!;
        _statusLabel = canvas.Find("status")!;
        _lobbyCodeLabel = canvas.Find("lobbyCode")!;
        _memberPanel = canvas.Find("members")!;
        _readyBtn = canvas.Find("ready")!;
        _startBtn = canvas.Find("start")!;

        _clicks.On(canvas.Find("host")!, OnHostClicked);
        _clicks.On(canvas.Find("find")!, OnFindClicked);
        _clicks.On(_readyBtn, OnReadyClicked);
        _clicks.On(_startBtn, OnStartGameClicked);
        _clicks.On(canvas.Find("back")!, () => OnBack?.Invoke());
        _clicks.On(canvas.Find("leave")!, () =>
        {
            SteamLobby.LeaveLobby();
            ShowMainPanel();
        });

        SteamLobby.OnLobbyCreated += OnLobbyCreated;
        SteamLobby.OnLobbyJoined += OnLobbyJoined;
        SteamLobby.OnLobbiesFound += OnLobbiesFound;
    }

    protected override void OnDestroy()
    {
        SteamLobby.OnLobbyCreated -= OnLobbyCreated;
        SteamLobby.OnLobbyJoined -= OnLobbyJoined;
        SteamLobby.OnLobbiesFound -= OnLobbiesFound;
    }

    protected override void Update(float dt)
    {
        _clicks.Tick();
        if (_inLobby) RefreshMemberList();
    }

    // -------------------------------------------------------------------------
    // Buttons
    // -------------------------------------------------------------------------

    private void OnHostClicked()
    {
        _statusLabel.Text = "Creating lobby...";
        _isHost = true;
        SteamLobby.CreateLobby(maxPlayers: 4);
    }

    private void OnFindClicked()
    {
        _statusLabel.Text = "Searching for lobbies...";
        ClearLobbyList();
        SteamLobby.FindLobbies("BiscuitChronicles");
    }

    private void OnReadyClicked()
    {
        _localReady = !_localReady;
        _readyBtn.Text = _localReady ? "Unready" : "Ready";
        _readyBtn.Tint = _localReady ? Color.LightGreen : Color.White;
    }

    private void OnStartGameClicked()
    {
        if (!_isHost) return;

        // Everybody loads the overworld; replication handles position sync.
        SBEngine.Instance.SceneManager.LoadScene("Scenes/Overworld");
    }

    // -------------------------------------------------------------------------
    // Steam callbacks
    // -------------------------------------------------------------------------

    private void OnLobbyCreated(bool success, ulong lobbyId)
    {
        if (!success)
        {
            _statusLabel.Text = "Failed to create lobby.";
            return;
        }

        // Tag the lobby so others can filter by game.
        SteamLobby.SetLobbyData("game", "BiscuitChronicles");
        ShowLobbyPanel(lobbyId, isHost: true);
    }

    private void OnLobbyJoined(bool success)
    {
        if (!success)
        {
            _statusLabel.Text = "Failed to join lobby.";
            return;
        }

        ShowLobbyPanel(0UL, isHost: false);
    }

    private void OnLobbiesFound(List<ulong> lobbyIds)
    {
        ClearLobbyList();

        if (lobbyIds.Count == 0)
        {
            _lobbyList.Add(new UiNode
            {
                Kind = UiKind.Label,
                Text = "No lobbies found. Try hosting instead.",
                Tint = Color.Gray,
                TextAlign = AlignMode.Center,
            });
            return;
        }

        foreach (ulong lobbyId in lobbyIds)
        {
            ulong captured = lobbyId;

            UiNode row = _lobbyList.Add(new UiNode
            {
                Layout = LayoutMode.Row,
                Gap = new Vector2(8f, 0f),
                HeightMode = SizeMode.Fixed, Height = 50f,
                Padding = new Vector4(8f, 8f, 8f, 8f),
                Background = new Color(20, 20, 50, 180),
            });

            row.Add(new UiNode
            {
                Kind = UiKind.Label,
                Text = $"Lobby {captured & 0xFFFF:X4}",
                Grow = 1f,
                Tint = Color.White,
            });

            UiNode join = row.Add(new UiNode
            {
                Kind = UiKind.Button,
                Text = "Join",
                WidthMode = SizeMode.Fixed, Width = 130f,
                Tint = Color.LightGreen,
                Background = new Color(255, 255, 255, 20),
                TextAlign = AlignMode.Center,
            });

            _clicks.On(join, () => SteamLobby.JoinLobby(new Steamworks.CSteamID(captured)));
        }
    }

    // -------------------------------------------------------------------------
    // Panel switching
    // -------------------------------------------------------------------------

    private void ShowMainPanel()
    {
        _mainPanel.Visible = true;
        _lobbyPanel.Visible = false;
        _inLobby = false;
        _isHost = false;
        _localReady = false;
        _readyBtn.Text = "Ready";
    }

    private void ShowLobbyPanel(ulong lobbyId, bool isHost)
    {
        _mainPanel.Visible = false;
        _lobbyPanel.Visible = true;
        _inLobby = true;
        _isHost = isHost;

        _lobbyCodeLabel.Text = lobbyId != 0
            ? $"Lobby Code: {lobbyId & 0xFFFF:X4}"
            : "Lobby Code: ----";

        _startBtn.Visible = isHost;
        RefreshMemberList();
    }

    private void RefreshMemberList()
    {
        foreach (UiNode child in new List<UiNode>(_memberPanel.Children)) _memberPanel.Remove(child);

        // Member info would come from Steamworks callbacks in a full implementation;
        // the local user stands in for the list.
        string localName = SteamManager.PersonaName;
        if (string.IsNullOrEmpty(localName)) localName = "You";

        _memberPanel.Add(new UiNode
        {
            Kind = UiKind.Label,
            Text = $"{localName} — {(_localReady ? "Ready" : "Not Ready")}",
            Tint = _localReady ? Color.LightGreen : Color.White,
        });
    }

    private void ClearLobbyList()
    {
        foreach (UiNode child in new List<UiNode>(_lobbyList.Children)) _lobbyList.Remove(child);
    }
}
