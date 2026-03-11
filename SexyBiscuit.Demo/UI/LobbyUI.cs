using Microsoft.Xna.Framework;
using SexyBiscuit.Engine;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Steam;
using SexyBiscuit.Engine.UI;
using SexyBiscuit.Engine.UI.Widgets;

namespace SexyBiscuit.Demo.UI;

/// <summary>
/// Steam lobby browser and host UI.
/// Provides lobby creation, discovery, joining, ready-up and game start.
/// Works gracefully without STEAMWORKS defined — buttons are shown but do nothing.
/// </summary>
public class LobbyUI : Actor
{
    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------
    /// <summary>Raised when the user clicks the Back button.</summary>
    public event Action? OnBack;

    // -------------------------------------------------------------------------
    // Panels
    // -------------------------------------------------------------------------
    private Panel  _mainPanel    = null!;
    private Panel  _lobbyPanel   = null!;
    private Panel  _lobbyList    = null!;
    private Label  _statusLabel  = null!;
    private Label  _lobbyCodeLabel = null!;
    private Button _startBtn     = null!;
    private Button _readyBtn     = null!;
    private Panel  _memberPanel  = null!;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private bool   _inLobby     = false;
    private bool   _isHost      = false;
    private bool   _localReady  = false;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------
    public LobbyUI() : base("LobbyUI")
    {
        BuildUI();
        SubscribeEvents();
    }

    // -------------------------------------------------------------------------
    // UI Construction
    // -------------------------------------------------------------------------
    private void BuildUI()
    {
        var canvas = AddComponent<Canvas>();
        canvas.ReferenceResolution = new Vector2(1920, 1080);

        // Background overlay
        var overlay = canvas.AddWidget<Panel>();
        overlay.Position        = Vector2.Zero;
        overlay.Size            = new Vector2(1920, 1080);
        overlay.BackgroundColor = new Color(0, 0, 0, 160);

        // Main panel — lobby browser
        _mainPanel              = canvas.AddWidget<Panel>();
        _mainPanel.Position     = new Vector2(560, 200);
        _mainPanel.Size         = new Vector2(800, 680);
        _mainPanel.LayoutMode   = PanelLayoutMode.Vertical;
        _mainPanel.BackgroundColor = new Color(15, 10, 30, 240);
        _mainPanel.Padding      = 16f;

        var title = new Label
        {
            Text      = "MULTIPLAYER",
            TextColor = new Color(255, 220, 80),
            Size      = new Vector2(768, 50),
            Alignment = TextAlignment.Center
        };
        _mainPanel.AddChild(title);

        _statusLabel = new Label
        {
            Text      = "Find or create a lobby to play online.",
            TextColor = Color.LightGray,
            Size      = new Vector2(768, 34),
            Alignment = TextAlignment.Center
        };
        _mainPanel.AddChild(_statusLabel);

        // Host / Find buttons
        var hostBtn = new Button
        {
            Text      = "Host Game",
            TextColor = Color.White,
            Size      = new Vector2(368, 50),
        };
        hostBtn.OnClick += OnHostClicked;

        var findBtn = new Button
        {
            Text      = "Join Game",
            TextColor = Color.White,
            Size      = new Vector2(368, 50),
        };
        findBtn.OnClick += OnFindClicked;

        var btnRow = new Panel
        {
            Size        = new Vector2(768, 60),
            LayoutMode  = PanelLayoutMode.Horizontal,
            BackgroundColor = Color.Transparent,
            Padding     = 16f
        };
        btnRow.AddChild(hostBtn);
        btnRow.AddChild(findBtn);
        _mainPanel.AddChild(btnRow);

        // Lobby list (visible when searching)
        _lobbyList              = new Panel
        {
            Size            = new Vector2(768, 280),
            LayoutMode      = PanelLayoutMode.Vertical,
            BackgroundColor = new Color(5, 5, 20, 180),
            Padding         = 8f
        };
        _mainPanel.AddChild(_lobbyList);

        // Back button
        var backBtn = new Button
        {
            Text      = "Back",
            TextColor = Color.LightGray,
            Size      = new Vector2(200, 44),
        };
        backBtn.OnClick += () => OnBack?.Invoke();
        _mainPanel.AddChild(backBtn);

        // ── In-lobby panel (shown once inside a lobby) ────────────────────
        _lobbyPanel              = canvas.AddWidget<Panel>();
        _lobbyPanel.Position     = new Vector2(560, 200);
        _lobbyPanel.Size         = new Vector2(800, 680);
        _lobbyPanel.LayoutMode   = PanelLayoutMode.Vertical;
        _lobbyPanel.BackgroundColor = new Color(10, 15, 30, 240);
        _lobbyPanel.Padding      = 16f;
        _lobbyPanel.Visible      = false;

        _lobbyCodeLabel = new Label
        {
            Text      = "Lobby Code: --------",
            TextColor = new Color(255, 220, 80),
            Size      = new Vector2(768, 40),
            Alignment = TextAlignment.Center
        };
        _lobbyPanel.AddChild(_lobbyCodeLabel);

        _memberPanel             = new Panel
        {
            Size            = new Vector2(768, 300),
            LayoutMode      = PanelLayoutMode.Vertical,
            BackgroundColor = new Color(5, 5, 20, 160),
            Padding         = 8f
        };
        _lobbyPanel.AddChild(_memberPanel);

        _readyBtn = new Button
        {
            Text      = "Ready",
            TextColor = Color.White,
            Size      = new Vector2(240, 50)
        };
        _readyBtn.OnClick += OnReadyClicked;

        _startBtn = new Button
        {
            Text      = "Start Game",
            TextColor = Color.White,
            Size      = new Vector2(240, 50)
        };
        _startBtn.OnClick += OnStartGameClicked;

        var lobbyBtnRow = new Panel
        {
            Size            = new Vector2(768, 60),
            LayoutMode      = PanelLayoutMode.Horizontal,
            BackgroundColor = Color.Transparent,
            Padding         = 16f
        };
        lobbyBtnRow.AddChild(_readyBtn);
        lobbyBtnRow.AddChild(_startBtn);
        _lobbyPanel.AddChild(lobbyBtnRow);

        var leaveLobbyBtn = new Button
        {
            Text      = "Leave Lobby",
            TextColor = Color.LightGray,
            Size      = new Vector2(200, 44)
        };
        leaveLobbyBtn.OnClick += () =>
        {
            SteamLobby.LeaveLobby();
            ShowMainPanel();
        };
        _lobbyPanel.AddChild(leaveLobbyBtn);

        // Initially only host is visible in start button
        _startBtn.Visible = false;
    }

    // -------------------------------------------------------------------------
    // Event subscription
    // -------------------------------------------------------------------------
    private void SubscribeEvents()
    {
        SteamLobby.OnLobbyCreated += OnLobbyCreated;
        SteamLobby.OnLobbyJoined  += OnLobbyJoined;
        SteamLobby.OnLobbiesFound += OnLobbiesFound;
    }

    protected override void OnDestroy()
    {
        SteamLobby.OnLobbyCreated -= OnLobbyCreated;
        SteamLobby.OnLobbyJoined  -= OnLobbyJoined;
        SteamLobby.OnLobbiesFound -= OnLobbiesFound;
    }

    // -------------------------------------------------------------------------
    // Button handlers
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
        _readyBtn.TextColor = _localReady ? Color.LightGreen : Color.White;
    }

    private void OnStartGameClicked()
    {
        if (!_isHost) return;
        // All players load the overworld; replication handles position sync
        SBEngine.Instance.SceneManager.LoadScene("Scenes/Overworld");
    }

    // -------------------------------------------------------------------------
    // Steam event callbacks
    // -------------------------------------------------------------------------
    private void OnLobbyCreated(bool success, ulong lobbyId)
    {
        if (!success)
        {
            _statusLabel.Text = "Failed to create lobby.";
            return;
        }

        // Tag the lobby so others can filter by game
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
            var emptyLabel = new Label
            {
                Text      = "No lobbies found. Try hosting instead.",
                TextColor = Color.Gray,
                Size      = new Vector2(752, 36),
                Alignment = TextAlignment.Center
            };
            _lobbyList.AddChild(emptyLabel);
            return;
        }

        foreach (var lobbyId in lobbyIds)
        {
            ulong capturedId = lobbyId;

            var row = new Panel
            {
                Size            = new Vector2(752, 50),
                LayoutMode      = PanelLayoutMode.Horizontal,
                BackgroundColor = new Color(20, 20, 50, 180),
                Padding         = 8f
            };

            // Host name label — in a full implementation we'd query lobby metadata
            var nameLabel = new Label
            {
                Text      = $"Lobby {capturedId & 0xFFFF:X4}",
                TextColor = Color.White,
                Size      = new Vector2(480, 36)
            };
            row.AddChild(nameLabel);

            var joinBtn = new Button
            {
                Text      = "Join",
                TextColor = Color.LightGreen,
                Size      = new Vector2(130, 38)
            };
#if STEAMWORKS
            joinBtn.OnClick += () => SteamLobby.JoinLobby(new Steamworks.CSteamID(capturedId));
#else
            joinBtn.OnClick += () => SteamLobby.JoinLobby(new Steamworks.CSteamID(capturedId));
#endif
            row.AddChild(joinBtn);

            _lobbyList.AddChild(row);
        }
    }

    // -------------------------------------------------------------------------
    // Panel switching helpers
    // -------------------------------------------------------------------------
    private void ShowMainPanel()
    {
        _mainPanel.Visible  = true;
        _lobbyPanel.Visible = false;
        _inLobby            = false;
        _isHost             = false;
        _localReady         = false;
        _readyBtn.Text      = "Ready";
    }

    private void ShowLobbyPanel(ulong lobbyId, bool isHost)
    {
        _mainPanel.Visible  = false;
        _lobbyPanel.Visible = true;
        _inLobby            = true;
        _isHost             = isHost;

        _lobbyCodeLabel.Text = lobbyId != 0
            ? $"Lobby Code: {lobbyId & 0xFFFF:X4}"
            : "Lobby Code: ----";

        _startBtn.Visible = isHost;

        RefreshMemberList();
    }

    private void RefreshMemberList()
    {
        while (_memberPanel.Children.Count > 0)
            _memberPanel.RemoveChild(_memberPanel.Children[0]);

        // In a full implementation member info would come from Steamworks callbacks.
        // Showing the local user as a placeholder.
        string localName = SteamManager.PersonaName;
        if (string.IsNullOrEmpty(localName)) localName = "You";

        var memberLabel = new Label
        {
            Text      = $"{localName} — {(_localReady ? "Ready" : "Not Ready")}",
            TextColor = _localReady ? Color.LightGreen : Color.White,
            Size      = new Vector2(752, 36)
        };
        _memberPanel.AddChild(memberLabel);
    }

    private void ClearLobbyList()
    {
        while (_lobbyList.Children.Count > 0)
            _lobbyList.RemoveChild(_lobbyList.Children[0]);
    }

    // -------------------------------------------------------------------------
    // Update — refresh member list while in lobby
    // -------------------------------------------------------------------------
    protected override void Update(float dt)
    {
        if (_inLobby)
            RefreshMemberList();
    }
}
