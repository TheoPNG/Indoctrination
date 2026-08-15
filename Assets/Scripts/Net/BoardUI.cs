using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// The game's on-screen presentation: an overhead camera over a felt table,
    /// with a proper heads-up display over it - health/follower bars for every
    /// player top and bottom, an organized battlefield showing every compound in
    /// play, and a collapsible tray for your own hand. Replaces the old flat,
    /// single-column IMGUI screen from early testing.
    ///
    /// Built entirely from code rather than a prefab, in keeping with the rest of
    /// the Net layer's "no scene wiring required" approach - see
    /// <see cref="Indoctrination.EditorTools.MultiplayerSceneSetup"/>.
    /// </summary>
    public class BoardUI : MonoBehaviour
    {
        [Tooltip("Address clients connect to. 127.0.0.1 is this same machine.")]
        public string address = "127.0.0.1";

        public ushort port = 7777;

        private const float DockTopHeight = StatBar.BarHeight + 4f;

        /// <summary>Smallest a card may shrink to and still be recognisable.</summary>
        private const float MinCardWidth = 72f;

        /// <summary>Gap between cards in a battlefield grid.</summary>
        private const float CardGap = 6f;

        /// <summary>Diameter of a resource-picker disc.</summary>
        private const float ResourceButtonSize = 44f;

        /// <summary>The small caption above each row of cards.</summary>
        private const float RowHeaderHeight = 16f;

        /// <summary>Breathing room between one row of the board and the next.</summary>
        private const float RowGap = 8f;

        /// <summary>The one-line counters chip at the top.</summary>
        private const float StatusRowHeight = 22f;

        private const int BoardSafeInset = 10;

        /// <summary>The permanent resource HUD's column width.</summary>
        private const float ResourceHudWidth = 62f;

        /// <summary>How tall the hand tray is while collapsed - just enough to say "there is a hand here".</summary>
        private const float HandPeekHeight = 34f;

        /// <summary>
        /// The tallest the hand may grow to when hovered. Capped so it never
        /// reaches the popup floating above it, and so it can never swallow the
        /// whole board on a short window.
        /// </summary>
        private const float MaxHandHeight = 318f;

        private const float PopupWidth = 460f;
        private const float PopupHeight = 400f;

        // Rolling only needs one button. Its frame fits that control instead of
        // borrowing the full question window used by card choices.
        private const float RollingPopupWidth = 300f;
        private const float RollingPopupHeight = 94f;
        private const float RollButtonWidth = 260f;
        private const float RollButtonHeight = 54f;
        private const float RecycleBinWidth = 78f;
        private const float RecycleBinHeight = 96f;
        private const float HandFanOverlap = 0.82f;

        /// <summary>
        /// Deliberately shallow. A pronounced fan looks like a hand of cards but
        /// costs height at the outer edges, which is where the tops were getting
        /// cut off - and the tilt makes the titles harder to read for no gain.
        /// </summary>
        private const float HandFanMaxAngle = 4f;

        private const float HandFanCenterLift = 8f;
        private const float HandFanPadding = 10f;

        /// <summary>
        /// Clearance above the tallest card in the fan. The fan's own height maths
        /// is exact, which leaves nothing for the outline, the hover lift, or a
        /// rounding error - and any of those clips the top of a card.
        /// </summary>
        private const float HandFanTopMargin = 14f;
        private const float DraftDropZoneHeight = 112f;
        private const float DraftDropZoneMaxWidth = 620f;

        /// <summary>
        /// How far above centre the popup sits. It clears the hand at full
        /// height, so a question can be read and answered while your own cards
        /// are open in front of you.
        /// </summary>
        private const float PopupLift = 46f;

        // --------------------------------------------------------------- Panels
        private RectTransform _connectPanel;
        private RectTransform _lobbyPanel;
        private RectTransform _gameRoot;

        private InputField _gameNameField;
        private InputField _joinCodeField;
        private Text _onlineStatus;
        private RectTransform _browserPanel;
        private RectTransform _browserList;
        private OnlineSession _online;
        private Text _joinCodeLabel;
        private InputField _nameField;
        private Text _lobbyPlayersText;
        private Button _startGameButton;
        private Button _addBotButton;
        private Button _timerToggle;
        private bool _timersOn;

        private Text _statusText;
        private Text _timerText;
        private Text _errorText;

        private RectTransform _topBar;
        private RectTransform _battlefield;
        private RectTransform _battlefieldViewport;
        private string _battlefieldSignature;
        private ResourceHud _resourceHud;

        /// <summary>
        /// The floating box that stands in for the old side panel, shown only
        /// while something actually needs an answer. It neither dims nor blocks
        /// the board behind it - a card's question is usually answered by
        /// looking at your own hand or somebody's compound first. Empty phases
        /// show nothing here at all.
        /// </summary>
        private RectTransform _popupPanel;
        private RectTransform _actionViewport;
        private RectTransform _actionPanel;
        private ScrollRect _actionScroll;
        private Button _readyButton;
        private Text _readyLabel;
        private Text _waitingLabel;

        /// <summary>
        /// The hand tray. Floats above the bottom edge rather than taking a row
        /// in the layout, so hovering it open never moves the board.
        /// </summary>
        private RectTransform _handRow;
        private RectTransform _handDropZone;
        private Image _handDropArc;
        private Text _handDropLabel;
        private RectTransform _recycleBin;
        private LayoutElement _dockPin;
        private Text _handCountLabel;
        private RectTransform _dragLayer;
        private StatBar _viewerStatBar;
        private ActivationStage _activationStage;
        private ShoutBanner _shoutBanner;
        private DieRoller _dieRoller;
        private RectTransform _shoutRow;
        private InputField _shoutField;
        private Button _shoutButton;

        // ---------------------------------------------------------------- State
        private NetworkGameManager _subscribedManager;
        private GameView _lastView;
        private float _secondsLeft;
        private float _choiceSecondsLeft;
        private bool _handExpanded;
        private string _renderedPhase;

        /// <summary>Set when a card that deals damage fired, which is what earns a shake.</summary>
        private bool _somethingHitThisRefresh;

        /// <summary>
        /// Health as of the last refresh, so a drop can be shown travelling into
        /// the bar rather than the number simply being different next time you look.
        /// </summary>
        private readonly Dictionary<int, int> _healthLastSeen = new();

        /// <summary>Each player's bar, for damage to fly into.</summary>
        private readonly Dictionary<int, StatBar> _statBars = new();

        /// <summary>Rituals seen resolve, so a new one can be told from a repeated view.</summary>
        private int _ritualsSeen = -1;

        /// <summary>
        /// Cards that have already been dealt onto the table, so a rebuild only
        /// animates what is genuinely new rather than re-dealing the whole board.
        /// </summary>
        private readonly HashSet<int> _cardsDealtIn = new();

        private Button _discardButton;
        private PhaseBanner _phaseBanner;
        private Button _statusToggle;
        private bool _statusExpanded;
        private Button _drawButton;
        private Button _resignButton;
        private Text _resignLabel;

        /// <summary>
        /// Set once Resign has been pressed and is waiting to be confirmed.
        /// Resigning cannot be taken back, so it is never one click away.
        /// </summary>
        private bool _resignArmed;

        private readonly List<ResourceColor> _pendingResources = new();
        private readonly List<ResourceColor> _pendingMealPayment = new();
        private string _amountInput = "0";
        private int _baalTargetPlayerId = -1;

        private bool _built;

        private void Awake() => BuildInterface();

        /// <summary>
        /// Builds the whole interface, once. Play mode reaches this through
        /// Awake; the smoke test calls it directly, because the Editor does not
        /// run Awake outside play mode and would otherwise be handed a board with
        /// no widgets in it.
        /// </summary>
        public void BuildInterface()
        {
            if (_built)
            {
                return;
            }

            _built = true;

            // A turn-based card game has no reason to render as fast as the
            // hardware can manage. Uncapped, this pins a laptop GPU at 100% and
            // drains the battery to draw a board that changes a few times a
            // minute; 60 is far more than enough for the animations.
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;

            SetUpOverheadCamera();

            var canvas = UIFactory.CreateCanvas("Board Canvas");

            // Behind everything, so the panels read as parts of one surface
            // rather than boxes floating on a flat colour.
            var backdrop = UIFactory.Panel("Backdrop", canvas.transform, Color.white);
            UIFactory.Stretch(backdrop);
            var backdropImage = backdrop.GetComponent<Image>();
            backdropImage.sprite = BoardArt.Backdrop;
            backdropImage.raycastTarget = false;

            _online = OnlineSession.CreateOn(transform);

            _connectPanel = BuildConnectPanel(canvas.transform);
            _browserPanel = BuildBrowserPanel(canvas.transform);
            _lobbyPanel = BuildLobbyPanel(canvas.transform);
            _gameRoot = BuildGameRoot(canvas.transform);
            BuildErrorLabel(canvas.transform);

            // Built last so they sit above the board they cover.
            _phaseBanner = PhaseBanner.CreateOn(canvas.transform);
            CardPreview.CreateOn(canvas.transform);
            _activationStage = ActivationStage.CreateOn(canvas.transform);
            _shoutBanner = ShoutBanner.CreateOn(canvas.transform);
            _dieRoller = DieRoller.CreateOn(canvas.transform);

            _playerPeek = PlayerPeek.CreateOn(canvas.transform);

            // Above everything, including the dice and the activation stage.
            // Nothing should be able to cover the way out.
            _quitPrompt = QuitPrompt.CreateOn(canvas.transform);

            ShowOnly(_connectPanel);
        }

        /// <summary>
        /// Points the scene camera straight down at the table and gives it the
        /// dark felt the board sits on. The board itself is a screen-space
        /// overlay, so the camera's only job is that backdrop - but pointing it
        /// down means anything later given a physical presence on the table
        /// (real card meshes, dice) lands in frame without re-rigging the shot.
        /// </summary>
        private static void SetUpOverheadCamera()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            camera.transform.position = new Vector3(0f, 12f, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic = true;
            camera.orthographicSize = 6f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = UITheme.Void;
        }

        private void Update()
        {
            var network = NetworkManager.Singleton;
            var manager = NetworkGameManager.Instance;

            SubscribeIfNeeded(manager);
            UpdateVisibility(network, manager);
            TickSoloStart();
            TickTimer(manager);

            // No mouse at all - a gamepad-only machine, or a test running in
            // batchmode. Whatever the hand is doing, leave it alone.
            if (_gameRoot.gameObject.activeSelf && Mouse.current != null)
            {
                var pointer = Mouse.current.position.ReadValue();
                PollHandHover(pointer);
                PollOpponentPeek(pointer);
            }

            PollDiceSettling();
        }

        /// <summary>
        /// Redraws once, when the dice stop.
        ///
        /// Nothing arrives from the server at that moment - the roll finished on
        /// this machine, in an animation - so without this the high roller's
        /// choice would not appear until the next message happened to come in.
        /// </summary>
        private void PollDiceSettling()
        {
            if (_dieRoller == null)
            {
                return;
            }

            var settled = _dieRoller.Settled;
            if (settled == _diceWereSettled)
            {
                return;
            }

            _diceWereSettled = settled;

            if (settled && _gameRoot.gameObject.activeSelf)
            {
                Refresh();
            }
        }

        private bool _diceWereSettled = true;

        private void OnDestroy()
        {
            if (_subscribedManager != null)
            {
                _subscribedManager.Changed -= Refresh;
                _subscribedManager.Shouted -= ShowShout;
            }

            // The effects driver outlives this board, so anything still animating
            // has to be stopped before the widgets it is animating disappear.
            BoardEffects.Instance.CancelAll();
        }

        private void SubscribeIfNeeded(NetworkGameManager manager)
        {
            if (ReferenceEquals(manager, _subscribedManager))
            {
                return;
            }

            if (_subscribedManager != null)
            {
                _subscribedManager.Changed -= Refresh;
                _subscribedManager.Shouted -= ShowShout;
            }

            _subscribedManager = manager;

            if (_subscribedManager != null)
            {
                _subscribedManager.Changed += Refresh;
                _subscribedManager.Shouted += ShowShout;
                Refresh();
            }
        }

        private bool _wasConnected;
        private bool _wasInGame;

        private void UpdateVisibility(NetworkManager network, NetworkGameManager manager)
        {
            var connected = network != null && (network.IsClient || network.IsServer);
            var inGame = connected && manager != null && manager.View != null;

            if (connected == _wasConnected && inGame == _wasInGame)
            {
                return;
            }

            _wasConnected = connected;
            _wasInGame = inGame;

            if (!connected)
            {
                ShowOnly(_connectPanel);
            }
            else if (!inGame)
            {
                ShowOnly(_lobbyPanel);
                Refresh();
            }
            else
            {
                ShowOnly(_gameRoot);
                Refresh();
            }
        }

        private void ShowOnly(RectTransform visible)
        {
            // The browser only belongs over the title screen. Leaving it up
            // through a connect would put a list of other games over the lobby.
            if (visible != _connectPanel)
            {
                CloseBrowser();
            }

            _connectPanel.gameObject.SetActive(visible == _connectPanel);
            _lobbyPanel.gameObject.SetActive(visible == _lobbyPanel);
            _gameRoot.gameObject.SetActive(visible == _gameRoot);
        }

        /// <summary>
        /// Counts the phase timer down locally between the infrequent full
        /// refreshes, and resets any half-finished local pick (resources, a meal
        /// payment, a Baal target) the moment the phase changes out from under it.
        /// </summary>
        private void TickTimer(NetworkGameManager manager)
        {
            var view = manager == null ? null : manager.View;

            if (view == null)
            {
                _lastView = null;
                _secondsLeft = 0f;
                _choiceSecondsLeft = 0f;
                return;
            }

            if (!ReferenceEquals(view, _lastView))
            {
                if (_lastView == null || view.phase != _lastView.phase)
                {
                    _pendingResources.Clear();
                    _pendingMealPayment.Clear();
                    _baalTargetPlayerId = -1;
                }

                _lastView = view;
                _secondsLeft = view.phaseSecondsRemaining;
                _choiceSecondsLeft = view.choiceSecondsRemaining;
            }
            else
            {
                if (view.hasPendingChoice)
                {
                    _choiceSecondsLeft = Mathf.Max(0f, _choiceSecondsLeft - Time.deltaTime);
                }
                else
                {
                    _secondsLeft = Mathf.Max(0f, _secondsLeft - Time.deltaTime);
                }
            }

            if (_timerText != null && _gameRoot.gameObject.activeSelf)
            {
                // Nothing is decided for anybody without the board saying so first,
                // and with the clocks off there is nothing to say.
                _timerText.text = !view.timersEnabled || view.isGameOver
                    ? ""
                    : view.hasPendingChoice
                        ? $"{_choiceSecondsLeft:0}s until this is answered for you"
                        : view.phase == nameof(TurnPhase.Draft)
                            ? $"{_secondsLeft:0}s until a pick is made for you"
                            : $"{_secondsLeft:0}s until the phase moves on";
            }
        }

        // ------------------------------------------------------------- Connect

        private RectTransform BuildConnectPanel(Transform parent)
        {
            var panel = UIFactory.Panel("Connect Panel", parent, new Color(
                UITheme.Void.r, UITheme.Void.g, UITheme.Void.b, 0.72f));
            UIFactory.Stretch(panel);

            var box = UIFactory.Panel("Connect Box", panel, UITheme.SurfaceRaised);
            UITheme.Frame(box.GetComponent<Image>(), 1.25f);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(box, 420, 260);
            var layout = UIFactory.VerticalLayout(box, 12, new RectOffset(20, 20, 20, 20), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            AddFixedHeight(UIFactory.Label(
                "Title", box, "I N D O C T R I N A T I O N", 26,
                TextAnchor.MiddleCenter, UITheme.Signal), 40);
            AddFixedHeight(UIFactory.Label(
                "Subtitle", box, "Open a game for others to find, or join one with a code.", 14,
                TextAnchor.MiddleCenter, UITheme.BoneDim), 22);

            // Hosting: a name for the noticeboard entry, and the button that
            // opens the game. No address and no port - the whole point of going
            // through Relay is that neither exists any more.
            var hostRow = UIFactory.Group("Host Row", box);
            AddFixedHeight(hostRow, 34);
            UIFactory.HorizontalLayout(hostRow, 8, new RectOffset(0, 0, 0, 0));
            AddFixedWidth(UIFactory.Label("Game Label", hostRow, "Game", 14), 52);
            _gameNameField = UIFactory.TextInput("Game Name Field", hostRow, "");
            AddFixedWidthHeight(_gameNameField.GetComponent<RectTransform>(), 190, 30);
            UIFactory.ButtonWithLabel(
                "Host Button", hostRow, "Host Online", HostOnline, UITheme.Affirm, 150, 30);

            var joinRow = UIFactory.Group("Join Row", box);
            AddFixedHeight(joinRow, 34);
            UIFactory.HorizontalLayout(joinRow, 8, new RectOffset(0, 0, 0, 0));
            AddFixedWidth(UIFactory.Label("Code Label", joinRow, "Code", 14), 52);
            _joinCodeField = UIFactory.TextInput("Join Code Field", joinRow, "");
            AddFixedWidthHeight(_joinCodeField.GetComponent<RectTransform>(), 190, 30);
            UIFactory.ButtonWithLabel(
                "Join Button", joinRow, "Join", () => JoinOnline(_joinCodeField.text),
                UITheme.Button, 150, 30);

            UIFactory.ButtonWithLabel(
                "Browse Button", box, "Browse Games", ToggleBrowser, UITheme.Button, 200, 32);

            _onlineStatus = UIFactory.Label(
                "Online Status", box, "", 13, TextAnchor.MiddleCenter, UITheme.Signal);
            AddFixedHeight(_onlineStatus.rectTransform, 20);

            // One press to a playable game on your own. Hosting, seating a bot
            // and starting are three steps that are always taken together when
            // the point is to try something out rather than to play somebody.
            UIFactory.ButtonWithLabel(
                "Solo Button", box, "Solo Playtest", StartSolo, UITheme.Affirm, 200, 34);

            // The title screen is the only screen with no other way out of the
            // application, so the way out lives here too.
            UIFactory.ButtonWithLabel(
                "Quit Button", box, "Quit", OpenQuitPrompt, UITheme.ButtonQuiet, 200, 30);

            return panel;
        }

        /// <summary>
        /// Leaves the game, and takes it off the noticeboard on the way out if
        /// this machine was the one hosting it. An entry left behind advertises
        /// a game nobody can join until the service times it out.
        /// </summary>
        private void LeaveGame()
        {
            _online?.CloseAsync();
            NetworkManager.Singleton?.Shutdown();
            SetOnlineStatus("");
        }

        // ---------------------------------------------------------- Online

        /// <summary>
        /// Opens a game others can reach, and says so.
        ///
        /// Deliberately not awaited into the caller: a button press cannot block
        /// while a network round trip happens, so the status line is the only
        /// report and it is written from both ends of the call.
        /// </summary>
        private async void HostOnline()
        {
            if (_online == null || _online.Busy)
            {
                return;
            }

            SetOnlineStatus("Opening a game...");

            if (await _online.HostAsync(_gameNameField.text, GameSettings.MaxPlayers))
            {
                SetOnlineStatus($"Open. Code: {_online.JoinCode}");
            }
            else
            {
                SetOnlineStatus(_online.LastError);
            }
        }

        private async void JoinOnline(string code)
        {
            if (_online == null || _online.Busy)
            {
                return;
            }

            SetOnlineStatus("Joining...");

            if (await _online.JoinAsync(code))
            {
                SetOnlineStatus("Connected.");
                CloseBrowser();
            }
            else
            {
                SetOnlineStatus(_online.LastError);
            }
        }

        private void SetOnlineStatus(string message)
        {
            if (_onlineStatus != null)
            {
                _onlineStatus.text = message ?? "";
            }
        }

        private void ToggleBrowser()
        {
            if (_browserPanel == null)
            {
                return;
            }

            if (_browserPanel.gameObject.activeSelf)
            {
                CloseBrowser();
                return;
            }

            _browserPanel.gameObject.SetActive(true);
            _browserPanel.SetAsLastSibling();
            RefreshBrowser();
        }

        private void CloseBrowser()
        {
            if (_browserPanel != null)
            {
                _browserPanel.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Reads the noticeboard and draws one row per open game. Joining a row
        /// does exactly what typing its code would - the list is a convenience
        /// over the code, not a second way in.
        /// </summary>
        private async void RefreshBrowser()
        {
            if (_online == null || _browserList == null)
            {
                return;
            }

            UIFactory.DestroyChildren(_browserList);
            var searching = UIFactory.Label(
                "Searching", _browserList, "Looking for games...", 14,
                TextAnchor.MiddleCenter, UITheme.BoneDim);
            AddFixedHeight(searching.rectTransform, 30);

            var games = await _online.BrowseAsync();

            // The panel can be closed, or the whole board torn down, while the
            // query is in flight. Neither is an error; there is simply nothing
            // left to draw into.
            if (_browserList == null || !_browserList.gameObject.activeInHierarchy)
            {
                return;
            }

            UIFactory.DestroyChildren(_browserList);

            if (games.Count == 0)
            {
                var empty = UIFactory.Label(
                    "No Games", _browserList,
                    string.IsNullOrEmpty(_online.LastError)
                        ? "No open games right now. Host one, or ask for a code."
                        : _online.LastError,
                    14, TextAnchor.MiddleCenter, UITheme.BoneDim);
                AddFixedHeight(empty.rectTransform, 40);
                return;
            }

            foreach (var game in games)
            {
                var row = UIFactory.Group($"Game {game.Id}", _browserList);
                AddFixedHeight(row, 34);
                UIFactory.HorizontalLayout(row, 8, new RectOffset(0, 0, 0, 0));

                var label = UIFactory.Label(
                    "Game Name", row, $"{game.Name}   {game.Players}/{game.MaxPlayers}", 14,
                    TextAnchor.MiddleLeft);
                AddFlexibleWidth(label.rectTransform);

                var code = game.JoinCode;
                UIFactory.ButtonWithLabel(
                    "Join Game", row, "Join", () => JoinOnline(code), UITheme.Affirm, 90, 30);
            }
        }

        /// <summary>
        /// Hosts a game, seats a bot, and starts - all as one press.
        ///
        /// The seating and the start cannot happen here: the NetworkGameManager
        /// only exists once the host is actually up, which is a frame or two
        /// away. This arms the sequence and <see cref="TickSoloStart"/> finishes
        /// it when the table is there to be sat at.
        /// </summary>
        private void StartSolo()
        {
            StartAs(host: true);
            _soloStartPending = true;
        }

        private QuitPrompt _quitPrompt;
        private PlayerPeek _playerPeek;

        private bool _soloStartPending;

        private void TickSoloStart()
        {
            if (!_soloStartPending)
            {
                return;
            }

            var manager = NetworkGameManager.Instance;
            if (manager == null || manager.Lobby == null)
            {
                return;
            }

            // Seat bots up to the minimum table, then begin. Waiting for the
            // lobby to report the seats back means this works whatever the
            // minimum happens to be rather than assuming it is two.
            if (manager.Lobby.playerNames.Length < manager.Lobby.minPlayers)
            {
                manager.RequestAddBotRpc();
                return;
            }

            _soloStartPending = false;
            manager.RequestStartGameRpc();
        }

        private void StartAs(bool host)
        {
            var network = NetworkManager.Singleton;
            if (network == null)
            {
                Debug.LogError("No NetworkManager in the scene. Run Indoctrination > Set Up Multiplayer Scene.");
                return;
            }

            var transport = network.GetComponent<UnityTransport>();
            if (transport != null)
            {
                // Loopback. This path is no longer how anybody reaches anybody
                // else - online play goes through Relay and sets its own
                // connection data - so all this has to do is let a host and its
                // bots run inside one process for Solo Playtest.
                transport.SetConnectionData(address, port, "0.0.0.0");
            }

            if (host)
            {
                network.StartHost();
            }
            else
            {
                network.StartClient();
            }
        }

        /// <summary>
        /// The list of open games, over the title screen. Its own panel rather
        /// than part of the box, because it grows with however many games there
        /// are and the box is a fixed shape.
        /// </summary>
        private RectTransform BuildBrowserPanel(Transform parent)
        {
            var panel = UIFactory.Panel("Browser Panel", parent, new Color(
                UITheme.Void.r, UITheme.Void.g, UITheme.Void.b, 0.86f));
            UIFactory.Stretch(panel);

            var box = UIFactory.Panel("Browser Box", panel, UITheme.SurfaceRaised);
            UITheme.Frame(box.GetComponent<Image>(), 1.25f);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(box, 520, 420);

            var layout = UIFactory.VerticalLayout(
                box, 10, new RectOffset(18, 18, 16, 16), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            AddFixedHeight(UIFactory.Label(
                "Browser Title", box, "OPEN GAMES", 20,
                TextAnchor.MiddleCenter, UITheme.Signal), 28);

            _browserList = UIFactory.Group("Game List", box);
            var listLayout = UIFactory.VerticalLayout(
                _browserList, 6, new RectOffset(0, 0, 0, 0), controlHeight: true);
            listLayout.childAlignment = TextAnchor.UpperCenter;
            AddFlexibleHeight(_browserList);

            var buttons = UIFactory.Group("Browser Buttons", box);
            AddFixedHeight(buttons, 36);
            var buttonLayout = UIFactory.HorizontalLayout(buttons, 10, new RectOffset(0, 0, 0, 0));
            buttonLayout.childAlignment = TextAnchor.MiddleCenter;

            UIFactory.ButtonWithLabel(
                "Refresh Games", buttons, "Refresh", RefreshBrowser, UITheme.Button, 150, 34);
            UIFactory.ButtonWithLabel(
                "Close Browser", buttons, "Back", CloseBrowser, UITheme.ButtonQuiet, 150, 34);

            panel.gameObject.SetActive(false);
            return panel;
        }

        // --------------------------------------------------------------- Lobby

        private RectTransform BuildLobbyPanel(Transform parent)
        {
            var panel = UIFactory.Panel("Lobby Panel", parent, new Color(
                UITheme.Void.r, UITheme.Void.g, UITheme.Void.b, 0.72f));
            UIFactory.Stretch(panel);

            var box = UIFactory.Panel("Lobby Box", panel, UITheme.SurfaceRaised);
            UITheme.Frame(box.GetComponent<Image>(), 1.25f);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(box, 420, 320);
            var layout = UIFactory.VerticalLayout(box, 10, new RectOffset(20, 20, 20, 20), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            AddFixedHeight(UIFactory.Label(
                "Title", box, "THE GATHERING", 24, TextAnchor.MiddleCenter, UITheme.Signal), 34);
            // Your name, chosen here rather than being assigned. It travels with
            // the seat, so it is fixed once the game starts.
            var nameRow = UIFactory.Group("Name Row", box);
            AddFixedHeight(nameRow, 34);
            UIFactory.HorizontalLayout(nameRow, 8, new RectOffset(0, 0, 0, 0));
            AddFixedWidth(UIFactory.Label("Name Label", nameRow, "Your name", 14), 90);

            _nameField = UIFactory.TextInput("Name Field", nameRow, "");
            AddFixedWidthHeight(_nameField.GetComponent<RectTransform>(), 190, 30);
            _nameField.characterLimit = NetworkGameManager.MaxNameLength;
            _nameField.onEndEdit.AddListener(SubmitName);

            UIFactory.ButtonWithLabel("Set Name", nameRow, "Set",
                () => SubmitName(_nameField.text), UITheme.ButtonQuiet, 60, 30);

            // The code to pass on. Only the host has one, and only when the game
            // was opened online - a solo playtest has nothing to share.
            _joinCodeLabel = UIFactory.Label(
                "Join Code", box, "", 18, TextAnchor.MiddleCenter, UITheme.Signal);
            _joinCodeLabel.fontStyle = FontStyle.Bold;
            AddFixedHeight(_joinCodeLabel.rectTransform, 26);

            _lobbyPlayersText = UIFactory.Label("Players", box, "", 15, TextAnchor.UpperCenter);
            AddFixedHeight(_lobbyPlayersText.rectTransform, 100);

            // The host's settings. Timers are off unless a table asks for them,
            // because a clock that takes your draft pick is worse than waiting.
            _timerToggle = UIFactory.ButtonWithLabel(
                "Timers", box, "Timers: off", ToggleTimers, UITheme.ButtonQuiet, 240, 32);

            // Bots fill out a table that is short of players, whether that is a
            // solo playtest or three friends wanting a fourth.
            _addBotButton = UIFactory.ButtonWithLabel(
                "Add Bot", box, "Add Bot",
                () => NetworkGameManager.Instance?.RequestAddBotRpc(),
                UITheme.ButtonQuiet, 240, 32);

            _startGameButton = UIFactory.ButtonWithLabel(
                "Start Button", box, "Start Game",
                () => NetworkGameManager.Instance?.RequestStartGameRpc(),
                width: 200, height: 40);

            var leaveButton = UIFactory.ButtonWithLabel(
                "Leave Button", box, "Leave", LeaveGame,
                UITheme.Blood, 200, 32);
            leaveButton.gameObject.name = "Leave Button";

            return panel;
        }

        private void ToggleTimers()
        {
            _timersOn = !_timersOn;
            _timerToggle.GetComponentInChildren<Text>().text = _timersOn
                ? "Timers: 25s"
                : "Timers: off";

            _timerToggle.targetGraphic.color = _timersOn
                ? UITheme.Affirm
                : UITheme.ButtonQuiet;

            NetworkGameManager.Instance?.RequestSetTimersRpc(_timersOn);
        }

        /// <summary>
        /// Sends whatever is typed. Until the passcode has been given, every
        /// entry is offered to the server as a passcode instead - so the same
        /// one box unlocks shouting and then does the shouting, and a table that
        /// does not know the word sees nothing but a box that ignores them.
        /// </summary>
        private void SubmitShout(string text)
        {
            var manager = NetworkGameManager.Instance;
            if (manager == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (manager.CanShout)
            {
                manager.RequestShoutRpc(text);
            }
            else
            {
                manager.RequestUnlockShoutRpc(text);
            }

            _shoutField.text = "";
        }

        private void SubmitName(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                NetworkGameManager.Instance?.RequestSetNameRpc(name);
            }
        }

        private void RefreshLobby(NetworkGameManager manager)
        {
            var lobby = manager.Lobby;
            var network = NetworkManager.Singleton;

            if (lobby == null)
            {
                _lobbyPlayersText.text = "Joining...";
                _startGameButton.gameObject.SetActive(false);
                return;
            }

            _lobbyPlayersText.text =
                $"Players ({lobby.playerNames.Length}/{lobby.maxPlayers}):\n" +
                string.Join("\n", lobby.playerNames.Select(n => $"  {n}"));

            var isHost = network != null && network.IsHost;
            _startGameButton.gameObject.SetActive(isHost);
            _timerToggle.gameObject.SetActive(isHost);
            _addBotButton.gameObject.SetActive(isHost);
            if (_joinCodeLabel != null)
            {
                var code = _online == null ? "" : _online.JoinCode;
                _joinCodeLabel.text = string.IsNullOrEmpty(code) ? "" : $"Code:  {code}";
            }

            _addBotButton.interactable = lobby.playerNames.Length < lobby.maxPlayers;
            _startGameButton.interactable = lobby.playerNames.Length >= lobby.minPlayers;
        }

        // ---------------------------------------------------------------- Game

        private RectTransform BuildGameRoot(Transform parent)
        {
            var root = UIFactory.Panel("Game Root", parent, Color.clear);
            UIFactory.Stretch(root);
            var layout = UIFactory.VerticalLayout(
                root, 6,
                new RectOffset(BoardSafeInset, BoardSafeInset, BoardSafeInset, BoardSafeInset),
                controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            // Status strip: phase/turn/deck info, plus the phase timer.
            var status = UIFactory.Group("Status Row", root);
            AddFixedHeight(status, StatusRowHeight);
            UIFactory.HorizontalLayout(status, 8, new RectOffset(4, 4, 0, 0), controlWidth: true);
            // The turn counters are reference material, not something to read every
            // turn, so they live small in the corner and expand only when asked.
            _statusToggle = UIFactory.ButtonWithLabel(
                "Status Toggle", status, "i", ToggleStatusDetail,
                UITheme.ButtonQuiet, 24, 22);

            _statusText = UIFactory.Label("Status", status, "", 12, TextAnchor.MiddleLeft,
                UITheme.BoneDim);
            AddFlexibleWidth(_statusText.rectTransform);

            // Conceding and offering a draw live behind the same chip as the
            // counters. Both are rare, and one of them ends your game - neither
            // belongs next to the controls you press every turn.
            _drawButton = UIFactory.ButtonWithLabel(
                "Offer Draw", status, "Offer draw", ToggleDrawOffer,
                UITheme.ButtonQuiet, 110, StatusRowHeight);

            _resignButton = UIFactory.ButtonWithLabel(
                "Resign", status, "Resign", PressResign,
                UITheme.Blood, 90, StatusRowHeight);
            _resignLabel = _resignButton.GetComponentInChildren<Text>();

            // Beside Resign, because it is the same decision wearing a different
            // hat - and unlike Resign it stays put once you are out, since
            // somebody who has lost still needs to be able to close the game.
            UIFactory.ButtonWithLabel(
                "Quit", status, "Quit", OpenQuitPrompt,
                UITheme.ButtonQuiet, 70, StatusRowHeight);
            _timerText = UIFactory.Label("Timer", status, "", 13, TextAnchor.MiddleRight, UITheme.Signal);
            AddResponsiveWidth(_timerText.rectTransform, 150, 230, 0);

            // Opponents across the top of the board. This row scrolls rather than
            // shrinking or clipping stat bars when several players share a small
            // Multiplayer Player window.
            _topBar = UIFactory.HorizontalScroll(
                "Top Bar", root, StatBar.BarHeight + (UIFactory.ScrollContentPadding * 2f) + 4f);
            _topBar.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;

            // The resource HUD and the battlefield share the flexible middle
            // area. Both are force-expanded vertically: neither reports a
            // preferred height of its own, so without this the layout would
            // size them to nothing.
            var middle = UIFactory.Group("Middle Area", root);
            AddFlexibleHeight(middle);
            var middleLayout = UIFactory.HorizontalLayout(
                middle, 10, new RectOffset(0, 0, 0, 0),
                controlWidth: true, controlHeight: true);
            middleLayout.childForceExpandHeight = true;

            // Your own resources, always in the same place on the left edge -
            // never a panel you open, just a running count that lights up the
            // moment there is something to take.
            var hudColumn = UIFactory.Group("Resource HUD Column", middle);
            var hudPin = hudColumn.gameObject.AddComponent<LayoutElement>();
            hudPin.minWidth = hudPin.preferredWidth = ResourceHudWidth;
            hudPin.flexibleWidth = 0;
            _resourceHud = ResourceHud.Create(hudColumn);
            UIFactory.Stretch((RectTransform)_resourceHud.transform);

            // Battlefield is a vertical stack of rows, not a horizontal strip, so
            // it gets its own scroll rect rather than reusing the horizontal
            // helper. The compounds are the main event, so it carries no
            // background box or border of its own - it sits directly on the felt.
            var battlefieldPanel = UIFactory.Group("Battlefield Panel", middle);
            AddResponsiveWidth(battlefieldPanel, 360, 1600, 6);
            var battlefieldViewport = UIFactory.Panel("Battlefield Viewport", battlefieldPanel, Color.clear);
            battlefieldViewport.gameObject.AddComponent<RectMask2D>();
            UIFactory.Stretch(battlefieldViewport);
            _battlefieldViewport = battlefieldViewport;
            // Deliberately not scrollable. Every row is sized to fit the space
            // available before anything is built, so a board you have to scroll
            // means the sizing was wrong - hiding that behind a scrollbar makes
            // it somebody's problem mid-game instead of a bug.
            var battlefieldScroll = battlefieldPanel.gameObject.AddComponent<ScrollRect>();
            battlefieldScroll.horizontal = false;
            battlefieldScroll.vertical = false;
            battlefieldScroll.movementType = ScrollRect.MovementType.Clamped;
            _battlefield = UIFactory.Group("Battlefield Content", battlefieldViewport);
            _battlefield.anchorMin = new Vector2(0, 1);
            _battlefield.anchorMax = new Vector2(1, 1);
            _battlefield.pivot = new Vector2(0.5f, 1);
            // A newly-created RectTransform starts 100 units wide. Once its
            // horizontal anchors are stretched, retaining that size delta makes
            // the content 50 units wider off each side of the viewport and clips
            // the first cards on the left. The fitter owns height; width must be
            // exactly the viewport width.
            _battlefield.sizeDelta = Vector2.zero;
            UIFactory.VerticalLayout(_battlefield, 8, new RectOffset(6, 6, 6, 6), controlHeight: true);
            UIFactory.FitToContent(_battlefield, ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize);
            battlefieldScroll.viewport = battlefieldViewport;
            battlefieldScroll.content = _battlefield;

            // Bottom dock: your own stat bar and the controls that end a phase.
            // The hand is no longer inside it - see below.
            var dock = UIFactory.Group("Bottom Dock", root);
            UIFactory.VerticalLayout(dock, 4, new RectOffset(0, 0, 0, 0), controlHeight: true);
            _dockPin = dock.gameObject.AddComponent<LayoutElement>();
            _dockPin.preferredHeight = DockTopHeight;
            _dockPin.minHeight = DockTopHeight;

            var dockTop = UIFactory.Group("Dock Top", dock);
            AddFixedHeight(dockTop, 44);
            UIFactory.HorizontalLayout(dockTop, 10, new RectOffset(0, 0, 0, 0));
            dockTop.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;

            _viewerStatBar = StatBar.Create(dockTop);

            // Ready sits in the dock rather than in a popup - the one control
            // that ends a phase should never be something you have to go looking
            // for.
            _readyButton = UIFactory.ButtonWithLabel(
                "Ready", dockTop, "Ready", ToggleReady, UITheme.Affirm, 130, 34);
            _readyLabel = _readyButton.GetComponentInChildren<Text>();

            _waitingLabel = UIFactory.Label("Waiting", dockTop, "", 12, TextAnchor.MiddleLeft,
                UITheme.BoneDim);
            AddFlexibleWidth(_waitingLabel.rectTransform);

            // The hand tray only peeks by default, so this is the one place its
            // size is still visible without hovering over it.
            _handCountLabel = UIFactory.Label("Hand Count", dockTop, "", 12, TextAnchor.MiddleRight,
                UITheme.BoneDim);
            AddFixedWidth(_handCountLabel.rectTransform, 70);

            // Shouting across the table. Locked until somebody types the word,
            // so a table that does not know about it never sees a chat box.
            _shoutRow = UIFactory.Group("Shout Row", dockTop);
            var shoutPin = _shoutRow.gameObject.AddComponent<LayoutElement>();
            shoutPin.minWidth = shoutPin.preferredWidth = 250;
            UIFactory.HorizontalLayout(_shoutRow, 4, new RectOffset(0, 0, 0, 0));

            _shoutField = UIFactory.TextInput("Shout Field", _shoutRow, "");
            AddFixedWidthHeight(_shoutField.GetComponent<RectTransform>(), 170, 30);
            _shoutField.characterLimit = NetworkGameManager.MaxShoutLength;
            _shoutField.onEndEdit.AddListener(SubmitShout);

            _shoutButton = UIFactory.ButtonWithLabel(
                "Shout", _shoutRow, "Say", () => SubmitShout(_shoutField.text),
                UITheme.ButtonQuiet, 70, 30);

            // The discard is public information and Rituals fly into it, so it is
            // a real place on the board rather than a number in the status line.
            _discardButton = UIFactory.ButtonWithLabel(
                "Discard", dockTop, "Discard", ShowDiscard, UITheme.ButtonQuiet, 100, 34);

            // Pips and drag ghosts are drawn above everything, so one crossing
            // the board is never hidden behind a panel it passes over.
            var flightLayer = UIFactory.Group("Flight Layer", root.parent);
            UIFactory.Stretch(flightLayer);
            flightLayer.SetAsLastSibling();
            BoardEffects.Instance.SetFlightLayer(flightLayer);
            _dragLayer = flightLayer;

            // The popup that stands in for the old always-on side panel. It does
            // NOT dim or block the board: a card's question is very often
            // answered by looking at your own hand or somebody's compound
            // first, and a scrim over all of it made that impossible. It floats
            // just above centre so the bottom of the screen - your compound and
            // your hand - stays clear, and it only blocks clicks inside its own
            // rect. Empty until something actually needs an answer.
            _popupPanel = UIFactory.Panel("Popup Panel", root, UITheme.SurfaceRaised);
            UITheme.Frame(_popupPanel.GetComponent<Image>(), 1f, UITheme.SignalSoft);
            _popupPanel.anchorMin = _popupPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _popupPanel.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_popupPanel, PopupWidth, PopupHeight);
            _popupPanel.anchoredPosition = new Vector2(0f, PopupLift);
            _popupPanel.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _popupPanel.gameObject.SetActive(false);

            // Without a scrim behind it the popup has to earn its own attention,
            // so it gets the accent as a bar across its top edge.
            var popupAccent = UIFactory.Panel("Accent", _popupPanel, UITheme.Signal);
            popupAccent.anchorMin = new Vector2(0f, 1f);
            popupAccent.anchorMax = new Vector2(1f, 1f);
            popupAccent.pivot = new Vector2(0.5f, 1f);
            popupAccent.sizeDelta = new Vector2(0f, 2f);
            popupAccent.GetComponent<Image>().raycastTarget = false;

            _actionViewport = UIFactory.Panel("Action Viewport", _popupPanel, Color.clear);
            _actionViewport.gameObject.AddComponent<RectMask2D>();
            UIFactory.Stretch(_actionViewport);
            _actionViewport.offsetMin = new Vector2(16, 16);
            _actionViewport.offsetMax = new Vector2(-16, -16);

            _actionScroll = _popupPanel.gameObject.AddComponent<ScrollRect>();
            _actionScroll.horizontal = false;
            _actionScroll.vertical = true;
            _actionScroll.scrollSensitivity = 34f;
            _actionScroll.movementType = ScrollRect.MovementType.Clamped;

            _actionPanel = UIFactory.Group("Action Content", _actionViewport);
            _actionPanel.anchorMin = new Vector2(0, 1);
            _actionPanel.anchorMax = new Vector2(1, 1);
            _actionPanel.pivot = new Vector2(0.5f, 1);
            _actionPanel.sizeDelta = Vector2.zero;
            var actionLayout = UIFactory.VerticalLayout(
                _actionPanel, 8, new RectOffset(4, 4, 4, 4), controlHeight: true);
            actionLayout.childAlignment = TextAnchor.UpperCenter;
            UIFactory.FitToContent(
                _actionPanel,
                ContentSizeFitter.FitMode.Unconstrained,
                ContentSizeFitter.FitMode.PreferredSize);
            _actionScroll.viewport = _actionViewport;
            _actionScroll.content = _actionPanel;

            // The hand, built last so it draws over everything including the
            // popup - you can always pull your own cards up to read them, even
            // while a question is open.
            //
            // It floats rather than taking a row in the layout. As a laid-out
            // row, expanding it resized the dock, which reflowed the board,
            // which rebuilt every card on it - so opening your hand made the
            // whole screen jump. Anchored to the bottom, expanding changes
            // nothing but the hand itself.
            // A clipped ellipse behind it supplies the one visible affordance:
            // the upper semicircle that catches a drafted card. The actual hand
            // remains the larger transparent hit surface in front of it.
            _handDropZone = UIFactory.Panel("Hand Drop Zone", root, Color.clear);
            _handDropZone.anchorMin = _handDropZone.anchorMax = new Vector2(0.5f, 0f);
            _handDropZone.pivot = new Vector2(0.5f, 0f);
            _handDropZone.anchoredPosition = new Vector2(0f, DockTopHeight + BoardSafeInset);
            UIFactory.SetSize(_handDropZone, DraftDropZoneMaxWidth, DraftDropZoneHeight);
            _handDropZone.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _handDropZone.gameObject.AddComponent<RectMask2D>();
            _handDropZone.GetComponent<Image>().raycastTarget = false;

            // A flat shelf, not a shape. This was a full ellipse stretched to
            // twice the zone's height and clipped to its top half, which read as
            // a large blue bubble rising out of the floor - the roundest, most
            // decorative thing on an otherwise hard-edged board.
            var dropArc = UIFactory.Panel(
                "Drop Arc", _handDropZone,
                new Color(UITheme.Signal.r, UITheme.Signal.g, UITheme.Signal.b, 0.10f));
            dropArc.anchorMin = Vector2.zero;
            dropArc.anchorMax = Vector2.one;
            dropArc.offsetMin = new Vector2(4f, 0f);
            dropArc.offsetMax = new Vector2(-4f, 0f);
            _handDropArc = dropArc.GetComponent<Image>();
            _handDropArc.raycastTarget = false;

            // The whole affordance is one lit edge along the top - the line the
            // card is being dropped across.
            var dropEdge = UIFactory.Panel("Drop Edge", dropArc, UITheme.Signal);
            dropEdge.anchorMin = new Vector2(0f, 1f);
            dropEdge.anchorMax = new Vector2(1f, 1f);
            dropEdge.pivot = new Vector2(0.5f, 1f);
            dropEdge.sizeDelta = new Vector2(0f, 2f);
            dropEdge.GetComponent<Image>().raycastTarget = false;

            _handDropLabel = UIFactory.Label(
                "Drop Label", _handDropZone, "DROP TO DRAFT", 13,
                TextAnchor.MiddleCenter, UITheme.BoneDim);
            _handDropLabel.fontStyle = FontStyle.Bold;
            _handDropLabel.raycastTarget = false;
            _handDropLabel.rectTransform.anchorMin = new Vector2(0f, 0.10f);
            _handDropLabel.rectTransform.anchorMax = new Vector2(1f, 0.52f);
            _handDropLabel.rectTransform.offsetMin = new Vector2(14f, 0f);
            _handDropLabel.rectTransform.offsetMax = new Vector2(-14f, 0f);
            _handDropZone.gameObject.SetActive(false);

            _handRow = UIFactory.Panel("Hand Row", root, Color.clear);
            _handRow.anchorMin = new Vector2(0f, 0f);
            _handRow.anchorMax = new Vector2(1f, 0f);
            _handRow.pivot = new Vector2(0.5f, 0f);
            _handRow.sizeDelta = new Vector2(-(HandLeftInset + BoardSafeInset), HandPeekHeight);
            _handRow.anchoredPosition = new Vector2(
                (HandLeftInset - BoardSafeInset) / 2f, DockTopHeight + BoardSafeInset);
            _handRow.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            // Recycling is a place, not a button repeated under every card. It
            // sits above the right edge of the open hand during Buy and receives
            // the same drag ghost used to play a card onto the battlefield.
            _recycleBin = UIFactory.Panel(
                "Recycle Bin", root, new Color(0.145f, 0.118f, 0.094f, 0.97f));
            UITheme.Frame(_recycleBin.GetComponent<Image>(), 1.5f, UITheme.SignalSoft);
            _recycleBin.anchorMin = _recycleBin.anchorMax = new Vector2(1f, 0f);
            _recycleBin.pivot = new Vector2(1f, 0.5f);
            _recycleBin.anchoredPosition = new Vector2(
                -BoardSafeInset, DockTopHeight + BoardSafeInset + (MaxHandHeight / 2f));
            UIFactory.SetSize(_recycleBin, RecycleBinWidth, RecycleBinHeight);
            _recycleBin.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;

            var recycleIcon = UIFactory.Label(
                "Recycle Icon", _recycleBin, "↻", 34, TextAnchor.MiddleCenter, UITheme.Signal);
            recycleIcon.fontStyle = FontStyle.Bold;
            recycleIcon.rectTransform.anchorMin = new Vector2(0f, 0.42f);
            recycleIcon.rectTransform.anchorMax = Vector2.one;
            recycleIcon.rectTransform.offsetMin = Vector2.zero;
            recycleIcon.rectTransform.offsetMax = Vector2.zero;

            var recycleLabel = UIFactory.Label(
                "Recycle Label", _recycleBin, "RECYCLE\nDROP CARD", 10,
                TextAnchor.MiddleCenter, UITheme.BoneDim);
            recycleLabel.fontStyle = FontStyle.Bold;
            recycleLabel.rectTransform.anchorMin = Vector2.zero;
            recycleLabel.rectTransform.anchorMax = new Vector2(1f, 0.46f);
            recycleLabel.rectTransform.offsetMin = new Vector2(4f, 4f);
            recycleLabel.rectTransform.offsetMax = new Vector2(-4f, 0f);
            _recycleBin.gameObject.SetActive(false);

            return root;
        }

        /// <summary>
        /// Opens and closes the hand from where the pointer actually is, tested
        /// once a frame against the tray's own rectangle.
        ///
        /// Deliberately NOT PointerEnter/PointerExit. Those fire in response to
        /// what is under the pointer, and opening the hand rebuilds the cards
        /// that were under the pointer - so the exit that rebuild provoked
        /// closed the hand, which rebuilt it, which opened it, every frame. The
        /// pointer's position is an input from outside the game; rebuilding the
        /// tray cannot change it, so polling it cannot feed back on itself.
        ///
        /// The hysteresis falls out for free: collapsed, the rect being tested
        /// is only the peek strip, so the pointer has to come right down to the
        /// bottom of the screen to open it; open, the rect is the whole tray,
        /// so it stays open across all of it. Crossing either edge moves the
        /// far edge away from the pointer, so it cannot oscillate.
        /// </summary>
        private void PollHandHover(Vector2 screenPoint)
        {
            if (_handRow == null || !_handRow.gameObject.activeInHierarchy)
            {
                return;
            }

            // A card being dragged out of the hand must not close it out from
            // under the drag the moment the pointer leaves the tray.
            if (_draggingFromHand)
            {
                return;
            }

            SetHandExpanded(
                RectTransformUtility.RectangleContainsScreenPoint(_handRow, screenPoint, UIFactory.UiCamera));
        }

        /// <summary>
        /// Set while a card is being dragged out of the hand, so the tray stays
        /// open under the drag even once the pointer has left it.
        /// </summary>
        private bool _draggingFromHand;

        /// <summary>
        /// Expands or collapses the hand on hover. Only re-renders when the
        /// state actually changes, so a pointer drifting across an already-open
        /// hand does not restart every card's deal-in animation.
        /// </summary>
        private void SetHandExpanded(bool expanded)
        {
            if (_handExpanded == expanded)
            {
                return;
            }

            _handExpanded = expanded;

            var manager = NetworkGameManager.Instance;
            if (manager?.View == null)
            {
                return;
            }

            // Only the hand. The board is deliberately left alone: it is laid
            // out with room for the hand at full height already, so opening one
            // never resizes the other. Rebuilding the battlefield here is what
            // used to make the whole screen flicker on every hover.
            RefreshHand(manager.View);
        }

        private void BuildErrorLabel(Transform parent)
        {
            var panel = UIFactory.Panel("Error Banner", parent, new Color(
                UITheme.Blood.r, UITheme.Blood.g, UITheme.Blood.b, 0.94f));
            UITheme.Frame(panel.GetComponent<Image>(), 1f, UITheme.SignalSoft);
            panel.anchorMin = new Vector2(0.5f, 1f);
            panel.anchorMax = new Vector2(0.5f, 1f);
            panel.pivot = new Vector2(0.5f, 1f);
            panel.anchoredPosition = new Vector2(0, -4);
            UIFactory.SetSize(panel, 700, 34);
            _errorText = UIFactory.Label("Error Text", panel, "", 15, TextAnchor.MiddleCenter);
            UIFactory.Stretch(_errorText.rectTransform);
            panel.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------ Refresh

        /// <summary>
        /// Renders a supplied view straight into the board with no live
        /// connection behind it, so the smoke test can drive a whole game through
        /// every layout path and catch anything that throws while building.
        /// Nothing in normal play calls this.
        /// </summary>
        public void RenderForTesting(GameView view)
        {
            BuildInterface();
            ShowOnly(_gameRoot);

            // Button callbacks capture the manager but only run on a click, so a
            // board built without one is safe to construct and inspect.
            RefreshGame(NetworkGameManager.Instance, view);
        }

        private void Refresh()
        {
            var manager = NetworkGameManager.Instance;
            if (manager == null)
            {
                return;
            }

            var errorBanner = _errorText.transform.parent.gameObject;
            if (!string.IsNullOrEmpty(manager.LastError))
            {
                _errorText.text = manager.LastError;
                errorBanner.SetActive(true);
            }
            else
            {
                errorBanner.SetActive(false);
            }

            if (manager.View == null)
            {
                if (manager.Lobby != null)
                {
                    RefreshLobby(manager);
                }

                return;
            }

            RefreshGame(manager, manager.View);
        }

        private void RefreshGame(NetworkGameManager manager, GameView view)
        {
            // Captured before _renderedPhase moves on, so "we have just arrived in
            // this phase" is still answerable further down.
            var previousPhase = _renderedPhase;

            if (!string.Equals(_renderedPhase, view.phase, StringComparison.Ordinal))
            {
                // The hand is deliberately left as it is. It used to snap shut on
                // every phase change, which fought the player whenever they were
                // holding it open to read something across a phase boundary.
                // Nothing needs to force it closed: the pointer decides, and if
                // it is not on the tray the next poll closes it anyway.
                _renderedPhase = view.phase;
            }

            if (!string.Equals(previousPhase, view.phase, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(previousPhase)
                && !view.isGameOver)
            {
                _phaseBanner.Announce(view.phase, PhaseHint(view), PhaseTint(view.phase));
            }

            _statusText.text = StatusLine(view);

            _somethingHitThisRefresh = false;

            RefreshTopBar(view);
            RefreshDie(view);
            RefreshShoutControls(manager);
            RefreshResourceHud(manager, view);
            RefreshBattlefield(manager, view);
            RefreshActionPanel(manager, view);
            RefreshReadyControl(view);
            RefreshConcessionControls(view);
            ShowHealthLosses(view);
            ShowRitualIfOneJustResolved(view);

            // Shaken only for a blow that actually landed, so the board is still
            // when nothing happened and the jolt means something when it comes.
            if (_somethingHitThisRefresh)
            {
                BoardEffects.Instance.Shake(_gameRoot);
            }
            // Dynamic phase contents replace the old children in-place. Always
            // return the scroll position to the primary action at the top.
            _actionPanel.anchoredPosition = new Vector2(_actionPanel.anchoredPosition.x, 0f);
            _actionScroll.verticalNormalizedPosition = 1f;
            RefreshHand(view);
            _activationStage.Present(
                view,
                (parent, prompt) => BuildActivationChoice(parent, prompt, manager, view),
                BoardCardPosition);
        }

        /// <summary>One line saying what this phase wants, for the banner.</summary>
        private static string PhaseHint(GameView view) => view.phase switch
        {
            nameof(TurnPhase.Draft) => "Pick a card",
            nameof(TurnPhase.Rolling) => "Roll your die",
            nameof(TurnPhase.Activation) => "Your units are firing",
            nameof(TurnPhase.Resource) =>
                $"Take {view.Viewer?.resourceAllowance ?? GameSettings.ResourcesPerTurn} resources",
            nameof(TurnPhase.Buy) => "Play or recycle from your hand",
            _ => ""
        };

        private static Color PhaseTint(string phase) => phase switch
        {
            nameof(TurnPhase.Draft) => new Color(0.588f, 0.573f, 0.827f),
            nameof(TurnPhase.Rolling) => UITheme.Bone,
            nameof(TurnPhase.Activation) => new Color(0.925f, 0.322f, 0.388f),
            nameof(TurnPhase.Resource) => new Color(0.290f, 0.831f, 0.588f),
            nameof(TurnPhase.Buy) => UITheme.Signal,
            _ => UITheme.Bone
        };

        /// <summary>
        /// Updates the opponents' bars, rebuilding them only when the set of
        /// players has actually changed. Destroying and recreating a bar throws
        /// away the animation running inside it, so doing it on every refresh
        /// made the bars restart constantly instead of sliding.
        /// </summary>
        private void RefreshTopBar(GameView view)
        {
            var opponents = view.players.Where(p => p.playerId != view.viewerPlayerId).ToList();

            if (_statBars.Count != opponents.Count
                || opponents.Any(p => !_statBars.ContainsKey(p.playerId)))
            {
                UIFactory.DestroyChildren(_topBar);
                _statBars.Clear();

                foreach (var player in opponents)
                {
                    _statBars[player.playerId] = StatBar.Create(_topBar);
                }
            }

            foreach (var player in opponents)
            {
                _statBars[player.playerId].Populate(player, isViewer: false, DiceRevealed);
            }
        }

        /// <summary>
        /// Whether the numbers rolled may be shown yet.
        ///
        /// False while the dice are in the air. The whole point of throwing them
        /// is that the number arrives when a die stops, so printing it next to
        /// everybody's name the instant the server says so answers the question
        /// before the throw does.
        /// </summary>
        private bool DiceRevealed => _dieRoller == null || _dieRoller.Settled;

        /// <summary>
        /// Shows an opponent's whole position while the pointer is on their
        /// strip at the top of the board.
        ///
        /// Polled once a frame rather than driven by pointer-enter events, for
        /// the same reason the hand is: a rebuild underneath the pointer sends a
        /// spurious exit, and the hand spent a whole session flickering because
        /// of it. The pointer's position is an external fact that no rebuild can
        /// perturb, so reading it directly cannot flicker.
        /// </summary>
        private void PollOpponentPeek(Vector2 pointer)
        {
            if (_playerPeek == null)
            {
                return;
            }

            var view = NetworkGameManager.Instance?.View;
            if (view == null || !_gameRoot.gameObject.activeSelf)
            {
                _playerPeek.Hide();
                return;
            }

            var camera = UIFactory.UiCamera;

            foreach (var (playerId, bar) in _statBars)
            {
                if (bar == null)
                {
                    continue;
                }

                // The whole strip, not only the name text. It is a single line
                // that reads as one label, and a 90-pixel target inside a
                // 380-pixel row is a target players miss.
                var rect = (RectTransform)bar.transform;
                if (!RectTransformUtility.RectangleContainsScreenPoint(rect, pointer, camera))
                {
                    continue;
                }

                var player = view.players.FirstOrDefault(p => p.playerId == playerId);
                if (player != null)
                {
                    _playerPeek.Show(player, rect);
                    return;
                }
            }

            _playerPeek.Hide();
        }

        /// <summary>
        /// Updates the permanent resource HUD: the running counts always show,
        /// and the circles only answer clicks - and only light up - while there
        /// are resources actually waiting to be taken this turn.
        /// </summary>
        private void RefreshResourceHud(NetworkGameManager manager, GameView view)
        {
            var you = view.Viewer;
            var allowance = you?.resourceAllowance ?? GameSettings.ResourcesPerTurn;
            var playing = you is { isAlive: true } && !view.isGameOver;

            var collecting = playing
                             && view.phase == nameof(TurnPhase.Resource)
                             && !you.collectedResources;

            // The high roller's prize is a resource of their choosing, so it is
            // taken the way every other resource is: off the circles on the
            // left, which light up for it exactly as they do for the phase's own
            // collection. Waits for the dice to stop for the same reason the
            // offer itself does - lighting them announces the winner.
            var claimingHighRoll = playing
                                   && view.diceRolled
                                   && !view.highRollResourceClaimed
                                   && DiceRevealed
                                   && HighestUniqueRoller(view) == view.viewerPlayerId;

            if (!collecting)
            {
                _pendingResources.Clear();
            }

            _resourceHud.Populate(you, collecting || claimingHighRoll, color =>
            {
                BoardEffects.Instance.Pop(_resourceHud.Pip(color));
                _resourceHud.ShowResourceGain(color);

                // The phase's own collection wins while it is running: taking
                // your resources is what the board is waiting on, and the prize
                // keeps until it is not.
                if (!collecting)
                {
                    manager.RequestClaimHighRollResourceRpc((int)color);
                    return;
                }

                _pendingResources.Add(color);

                if (_pendingResources.Count >= allowance)
                {
                    manager.RequestCollectResourcesRpc(_pendingResources.ConvertAll(c => (int)c).ToArray());
                    _pendingResources.Clear();
                }
            });
        }

        /// <summary>
        /// Sends a mote into the health bar of anybody who just lost health, from
        /// the middle of the board, so a hit is something you watch land.
        /// Deliberately after the bars are rebuilt, so their positions are current.
        /// </summary>
        /// <summary>
        /// Puts a Ritual up over the board when one resolves. Counted rather than
        /// compared by name, so the same Ritual twice in a row still reads as two
        /// events.
        /// </summary>
        private void ShowRitualIfOneJustResolved(GameView view)
        {
            if (_ritualsSeen < 0)
            {
                // First view of the game: catch up silently rather than replaying
                // whatever happened before this client was looking.
                _ritualsSeen = view.ritualCount;
                return;
            }

            if (view.ritualCount == _ritualsSeen || string.IsNullOrEmpty(view.lastRitualId))
            {
                return;
            }

            _ritualsSeen = view.ritualCount;

            if (CardDatabase.Instance.TryGet(view.lastRitualId, out var definition))
            {
                CardPreview.FlashRitual(definition, _discardButton.transform.position);
            }
        }

        private void ShowHealthLosses(GameView view)
        {
            foreach (var player in view.players)
            {
                if (!_healthLastSeen.TryGetValue(player.playerId, out var before))
                {
                    _healthLastSeen[player.playerId] = player.health;
                    continue;
                }

                _healthLastSeen[player.playerId] = player.health;

                var lost = before - player.health;
                if (lost <= 0)
                {
                    continue;
                }

                var bar = player.playerId == view.viewerPlayerId
                    ? _viewerStatBar
                    : _statBars.GetValueOrDefault(player.playerId);

                if (bar == null)
                {
                    continue;
                }

                // One mote per point, briefly staggered, so three damage reads as
                // three hits rather than one bigger blob.
                for (var i = 0; i < Mathf.Min(lost, 6); i++)
                {
                    BoardEffects.Instance.FlyPip(
                        _battlefield.position, bar.HealthBarPosition,
                        BoardArt.ColorOfCategory(ActivationCategory.Damage),
                        delay: i * 0.07f, size: 22f, landsIn: bar.HealthBar);
                }

                _somethingHitThisRefresh = true;
            }
        }

        // ------------------------------------------------------- Battlefield

        /// <summary>
        /// Rebuilds the whole board. Every row that will be shown is worked out
        /// first, then they are all sized together against the height available:
        /// seeing your own and every opponent's compound at once is what the game
        /// is played on, so nothing here is allowed to scroll out of sight.
        /// </summary>
        private void RefreshBattlefield(NetworkGameManager manager, GameView view)
        {
            var signature = BattlefieldSignature(view);
            if (signature == _battlefieldSignature)
            {
                RefreshBattlefieldCardState(manager, view);
                return;
            }

            _battlefieldSignature = signature;

            // Anything no longer anywhere on the board is forgotten, so a card
            // that genuinely leaves and comes back is dealt in again.
            var present = view.players.SelectMany(player => player.compound)
                .Concat(view.draftZone)
                .Concat(view.discardPile)
                .Select(card => card.instanceId)
                .ToHashSet();
            _cardsDealtIn.IntersectWith(present);

            UIFactory.DestroyChildren(_battlefield);

            var rows = new List<PlannedRow>();

            // A card question whose options are all sitting in the draft zone is
            // answered by clicking them there. Reproducing them in the narrow side
            // panel asked the player to pick from a second, smaller copy of cards
            // already in front of them.
            var choosingOnBoard = view.hasPendingChoice
                                  && view.pendingChoice.kind == nameof(ChoiceKind.Card)
                                  && view.pendingChoice.askedOfPlayerId == view.viewerPlayerId
                                  && view.pendingChoice.cardOptions.Length > 0
                                  && view.pendingChoice.cardOptions.All(
                                      id => view.draftZone.Any(c => c.instanceId == id));

            if (view.phase == nameof(TurnPhase.Draft))
            {
                var isMyPick = view.currentDrafterId == view.viewerPlayerId;
                var options = view.hasPendingChoice
                    ? view.pendingChoice.cardOptions
                    : System.Array.Empty<int>();

                rows.Add(choosingOnBoard
                    ? new PlannedRow
                    {
                        Label = view.pendingChoice.prompt,
                        Cards = view.draftZone,
                        IsClickable = card => options.Contains(card.instanceId),
                        OnClick = card => manager.RequestAnswerCardRpc(card.instanceId),
                        TagFor = card => DraftMarkTag(view, card),
                        ActionLabel = "Choose this card",
                        IsAwaitingYourPick = card => options.Contains(card.instanceId)
                    }
                    : new PlannedRow
                    {
                        Label = $"Draft Zone ({view.draftZone.Length})",
                        Cards = view.draftZone,
                        IsDraggable = card => isMyPick && IsDraftable(view, card),
                        OnDragMoved = eventData => SetHandDropZoneHot(
                            RectTransformUtility.RectangleContainsScreenPoint(
                                _handRow, eventData.position, eventData.pressEventCamera)),
                        IsAwaitingYourPick = card => isMyPick && IsDraftable(view, card),
                        OnDragFinished = () => SetHandDropZoneHot(false),
                        OnDropped = (card, eventData) =>
                        {
                            if (RectTransformUtility.RectangleContainsScreenPoint(
                                    _handRow, eventData.position, eventData.pressEventCamera))
                            {
                                manager.RequestDraftRpc(card.instanceId);
                            }
                        },
                        TagFor = card => DraftMarkTag(view, card),
                    });
            }

            foreach (var player in view.players.Where(p => p.playerId != view.viewerPlayerId))
            {
                rows.Add(new PlannedRow
                {
                    Label = $"{player.name} ({player.compound.Length})",
                    Cards = OrderedForBoard(player.compound)
                });
            }

            var you = view.Viewer;
            if (you != null)
            {
                rows.Add(new PlannedRow
                {
                    Label = $"You ({you.compound.Length})",
                    Cards = OrderedForBoard(you.compound),
                    IsOwnCompound = true
                });
            }

            if (_discardOpen && view.discardPile.Length > 0)
            {
                rows.Add(new PlannedRow
                {
                    Label = $"Discard ({view.discardPile.Length})",
                    Cards = view.discardPile.Reverse().ToArray()
                });
            }

            var cardWidth = CardWidthForBoard(rows);

            foreach (var row in rows)
            {
                BuildCardRow(_battlefield, row, cardWidth, view, manager);
            }
        }

        /// <summary>
        /// Everything that changes the table's card hierarchy. Dice and ready
        /// flags are deliberately absent: they decorate the existing table and
        /// must not destroy and deal every card again when a player rolls.
        /// </summary>
        private string BattlefieldSignature(GameView view)
        {
            static string Cards(IEnumerable<CardView> cards) => string.Join(",",
                cards.Select(card => $"{card.instanceId}:{card.definitionId}"));

            var players = string.Join("|", view.players.Select(player =>
                $"{player.playerId}:{player.name}:{player.isAlive}:{Cards(player.compound)}"));
            var marks = string.Join(",", view.draftMarks.Select(mark =>
                $"{mark.marker}:{mark.cardInstanceId}:{mark.playerId}"));
            var choice = view.hasPendingChoice
                ? $"{view.pendingChoice.kind}:{view.pendingChoice.prompt}:"
                  + $"{view.pendingChoice.askedOfPlayerId}:"
                  + string.Join(",", view.pendingChoice.cardOptions)
                : "none";

            return $"{view.phase}|{view.viewerPlayerId}|{view.currentDrafterId}|"
                   + $"{_discardOpen}|{players}|{Cards(view.draftZone)}|"
                   + $"{Cards(view.discardPile)}|{marks}|{choice}";
        }

        /// <summary>
        /// Updates roll-dependent outlines and card mini-menus in place. The
        /// hierarchy stays alive, so its entrance fades do not restart.
        /// </summary>
        private void RefreshBattlefieldCardState(NetworkGameManager manager, GameView view)
        {
            var ownCards = view.Viewer?.compound.ToDictionary(card => card.instanceId)
                           ?? new Dictionary<int, CardView>();
            var allCards = view.players.SelectMany(player => player.compound)
                .ToDictionary(card => card.instanceId);

            foreach (var cardView in _battlefield.GetComponentsInChildren<BoardCardView>())
            {
                MarkIfDueToActivate(cardView, view);
                if (cardView.Card != null
                    && allCards.TryGetValue(cardView.Card.instanceId, out var currentCard))
                {
                    cardView.UpdateCounters(currentCard);
                }

                if (manager != null
                    && cardView.Card != null
                    && ownCards.TryGetValue(cardView.Card.instanceId, out var ownCard))
                {
                    cardView.SetExtraContent(null);
                    WireCompoundCardExtras(cardView, ownCard, manager, view);
                }
            }
        }

        /// <summary>One row of the board, planned before anything is built.</summary>
        private class PlannedRow
        {
            public string Label;
            public CardView[] Cards;
            public Func<CardView, bool> IsClickable;
            public Action<CardView> OnClick;
            public Func<CardView, bool> IsDraggable;
            public Action<PointerEventData> OnDragMoved;
            public Action OnDragFinished;
            public Action<CardView, PointerEventData> OnDropped;
            public Func<CardView, string> TagFor;
            public string ActionLabel;

            /// <summary>
            /// Cards this row is waiting for the viewer to choose between. They
            /// are lit and left gently breathing, so whose turn it is to pick is
            /// something the board says rather than something you work out.
            /// </summary>
            public Func<CardView, bool> IsAwaitingYourPick;

            /// <summary>
            /// Whether this is the viewer's own compound - the only row where
            /// units can be dragged to reorder them, and where a card's own
            /// ability (Suspicious Chef's payment, Baal's die) offers itself.
            /// </summary>
            public bool IsOwnCompound;
        }

        /// <summary>
        /// Units first, then blessings - a stable split rather than a resort, so
        /// a glance down a compound separates what fires on the dice from what
        /// simply sits there, without disturbing the order a player chose for
        /// their own units by dragging them.
        /// </summary>
        private static CardView[] OrderedForBoard(CardView[] cards)
        {
            return cards
                .OrderBy(card => DefinitionOf(card)?.Type == CardType.Unit ? 0 : 1)
                .ToArray();
        }

        private static CardDefinition DefinitionOf(CardView card) =>
            CardDatabase.Instance.TryGet(card.definitionId, out var definition) ? definition : null;

        /// <summary>
        /// The largest card width at which every row fits in the space available,
        /// both across and down. Cards shrink rather than the board scrolling,
        /// because a compound you cannot see is a compound you cannot plan against.
        /// </summary>
        private float CardWidthForBoard(List<PlannedRow> rows)
        {
            var width = Mathf.Max(200f, _battlefield.rect.width - 12f);

            // The resting hand floats over the bottom of the board, so that
            // strip is kept clear. Only the peek is reserved, not the whole
            // open tray: an expanded hand is something you are looking at on
            // purpose, and it drops away again the moment you look elsewhere.
            var height = Mathf.Max(
                200f, _battlefieldViewport.rect.height - 8f - HandPeekHeight - 6f);

            for (var candidate = BoardCardView.Width; candidate >= MinCardWidth; candidate -= 4f)
            {
                var cardHeight = candidate * (BoardCardView.Height / BoardCardView.Width);
                var perRow = Mathf.Max(1, Mathf.FloorToInt((width + CardGap) / (candidate + CardGap)));

                var total = 0f;
                foreach (var row in rows)
                {
                    var lines = Mathf.Max(1, Mathf.CeilToInt(row.Cards.Length / (float)perRow));
                    total += (lines * cardHeight) + ((lines - 1) * CardGap) + RowHeaderHeight + RowGap;
                }

                if (total <= height)
                {
                    return candidate;
                }
            }

            return MinCardWidth;
        }

        /// <summary>
        /// Builds one planned row at the width the whole board agreed on, so
        /// every row uses the same card size and the board reads as one table
        /// rather than a stack of differently-scaled shelves.
        /// </summary>
        private void BuildCardRow(
            Transform parent, PlannedRow plan, float cardWidth, GameView view,
            NetworkGameManager manager = null, float availableWidth = 0f)
        {
            var row = UIFactory.Group(plan.Label, parent);
            var rowLayout = UIFactory.VerticalLayout(row, 2, new RectOffset(0, 0, 0, 0), controlHeight: true);
            rowLayout.childAlignment = TextAnchor.UpperLeft;

            var header = UIFactory.Label("Header", row, plan.Label, 13, TextAnchor.MiddleLeft,
                UITheme.BoneDim);
            header.fontStyle = FontStyle.Bold;
            AddFixedHeight(header.rectTransform, RowHeaderHeight);

            var cardHeight = cardWidth * (BoardCardView.Height / BoardCardView.Width);
            var available = availableWidth > 0f
                ? availableWidth
                : Mathf.Max(200f, _battlefield.rect.width - 12f);
            var perRow = Mathf.Max(1, Mathf.FloorToInt((available + CardGap) / (cardWidth + CardGap)));
            var lines = Mathf.Max(1, Mathf.CeilToInt(plan.Cards.Length / (float)perRow));

            var grid = UIFactory.Group("Cards", row);
            var gridLayout = grid.gameObject.AddComponent<GridLayoutGroup>();
            gridLayout.cellSize = new Vector2(cardWidth, cardHeight);
            gridLayout.spacing = new Vector2(CardGap, CardGap);
            gridLayout.childAlignment = TextAnchor.UpperLeft;

            var gridHeight = (lines * cardHeight) + ((lines - 1) * CardGap);
            AddFixedHeight(grid, gridHeight);

            var rowPin = row.gameObject.AddComponent<LayoutElement>();
            rowPin.preferredHeight = gridHeight + RowHeaderHeight + RowGap;
            rowPin.minHeight = gridHeight + RowHeaderHeight + RowGap;
            rowPin.flexibleWidth = 1;

            var dealt = 0;

            // Every unit cell in this row, in display order - what a drag drop
            // is measured against to decide where a re-ordered unit lands.
            var unitCells = new List<RectTransform>();

            int ResolveDropIndex(Vector2 screenPosition)
            {
                if (unitCells.Count == 0)
                {
                    return 0;
                }

                var best = 0;
                var bestDistance = float.MaxValue;

                for (var i = 0; i < unitCells.Count; i++)
                {
                    var cellScreenPos = RectTransformUtility.WorldToScreenPoint(UIFactory.UiCamera, unitCells[i].position);
                    var distance = Vector2.Distance(cellScreenPos, screenPosition);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }

                return best;
            }

            foreach (var card in plan.Cards)
            {
                var cell = UIFactory.Group("Cell", grid);
                var cardView = BoardCardView.Create(cell);
                var cardRect = (RectTransform)cardView.transform;
                cardRect.anchorMin = cardRect.anchorMax = new Vector2(0.5f, 0.5f);
                cardRect.pivot = new Vector2(0.5f, 0.5f);

                var clickable = plan.IsClickable != null && plan.IsClickable(card);
                var tag = plan.TagFor?.Invoke(card);
                cardView.Populate(card, tag, clickable ? () => plan.OnClick(card) : null);
                cardView.SetAction(clickable ? plan.ActionLabel : null,
                                   clickable ? () => plan.OnClick(card) : null);
                cardView.ScaleTo(cardWidth);

                MarkIfDueToActivate(cardView, view);

                if (plan.IsAwaitingYourPick != null && plan.IsAwaitingYourPick(card))
                {
                    cardView.SetAwaitingYourPick(true);
                }

                var draggable = plan.IsDraggable != null && plan.IsDraggable(card);
                if (draggable && plan.OnDropped != null)
                {
                    var ghostCard = card;
                    var ghostTag = tag;
                    var ghostWidth = cardWidth;
                    var handle = cardView.gameObject.AddComponent<DragHandle>();
                    handle.DragLayer = _dragLayer;
                    handle.GhostFactory = () => DragHandle.CardGhost(
                        ghostCard, ghostTag, ghostWidth);
                    handle.OnDragMoved = plan.OnDragMoved;
                    handle.OnDragFinished = plan.OnDragFinished;
                    handle.OnDropped = eventData => plan.OnDropped(ghostCard, eventData);
                }

                var isUnit = DefinitionOf(card)?.Type == CardType.Unit;

                // Only the owner may pick their own units up, and only units have
                // an activation order worth dragging into place - a Blessing just
                // hangs out wherever it already is.
                if (plan.IsOwnCompound && isUnit && manager != null)
                {
                    unitCells.Add(cell);

                    var instanceId = card.instanceId;
                    var ghostCard = card;
                    var ghostTag = tag;
                    var ghostWidth = cardWidth;

                    var handle = cardView.gameObject.AddComponent<DragHandle>();
                    handle.DragLayer = _dragLayer;
                    handle.GhostFactory = () => DragHandle.CardGhost(ghostCard, ghostTag, ghostWidth);
                    handle.OnDropped = eventData =>
                        manager.RequestReorderUnitRpc(instanceId, ResolveDropIndex(eventData.position));
                }

                if (plan.IsOwnCompound && manager != null)
                {
                    WireCompoundCardExtras(cardView, card, manager, view);
                }

                // Dealt out rather than appearing all at once. Alpha only, so the
                // card is where the layout put it from the first frame and nothing
                // measuring the board catches it part-way through moving.
                // Only cards the player has not seen on the table before are
                // dealt in. A rebuild is triggered by any change to the board at
                // all, and fading every card back in each time made one card
                // arriving look like the whole table being re-dealt.
                if (_cardsDealtIn.Add(card.instanceId))
                {
                    BoardEffects.Instance.FadeIn(cell.gameObject, delay: dealt * 0.025f);
                }
                dealt++;
            }
        }

        /// <summary>
        /// Gives a card in the viewer's own compound its own mini-menu, if its
        /// ability is the kind that asks for one - Suspicious Chef's payment,
        /// Baal's die, Try Again's reroll. Offered from the card itself rather
        /// than a panel that shows for every card, so it only ever appears next
        /// to the one card it actually belongs to.
        /// </summary>
        private void WireCompoundCardExtras(BoardCardView cardView, CardView card, NetworkGameManager manager, GameView view)
        {
            if (card.definitionId == CardIds.SuspiciousChef)
            {
                var instanceId = card.instanceId;
                cardView.SetExtraContent(content => BuildSuspiciousChefExtra(content, manager, instanceId, cardView));
            }
            else if (card.definitionId == CardIds.BaalTheManipulator && view.phase == nameof(TurnPhase.Rolling))
            {
                cardView.SetExtraContent(content => BuildBaalExtra(content, manager, view, cardView));
            }
            else if (card.definitionId == CardIds.TryAgain && view.Viewer is { canReroll: true })
            {
                cardView.SetExtraContent(content => BuildTryAgainExtra(content, manager));
            }
        }


        /// <summary>
        /// Marks a card the dice have already promised to wake, so the board
        /// itself says what is about to fire rather than a list of card names in
        /// the side panel. Standing highlight, not a pulse - it is a statement
        /// about what is queued, not an event.
        /// </summary>
        private void ShowShout(string from, string message) => _shoutBanner.Show(from, message);

        /// <summary>
        /// Throws the die when the viewer's own roll comes back, and clears it
        /// away once the turn has moved past Rolling.
        ///
        /// Driven off the authoritative number rather than off pressing the
        /// button: the die that lands is showing what the server rolled, not
        /// what this machine hoped for. Only the viewer's own die is thrown -
        /// every roll at the table would be four dice landing at once, and the
        /// opponents' faces are already on their stat bars.
        /// </summary>
        private void RefreshDie(GameView view)
        {
            var you = view.Viewer;

            if (you == null || view.isGameOver)
            {
                _dieRoller.Rearm();
                return;
            }

            // Waits for the whole table, not just you. The dice are thrown
            // together, so a player who rolled early would otherwise watch their
            // own die land and then sit next to it while everyone else's arrived
            // one at a time.
            //
            // Deliberately `diceRolled` rather than the Rolling phase. Rolling
            // the last die readies the table and advances the phase inside the
            // same server call, so the view that carries the result usually
            // already says Activation - gating on the phase meant the dice were
            // thrown almost never, and dismissed a frame later when they were.
            if (!view.diceRolled)
            {
                // Still waiting on somebody. Clear last turn's dice and arm the
                // next throw.
                _dieRoller.Rearm();
                return;
            }

            // Every living player's die, not just yours - the table's whole
            // roll decides which units wake, so an opponent's number matters as
            // much as your own.
            //
            // Private dice are thrown too. Standardized Uniforms grants a die
            // that only its owner's units answer to, and it used to exist solely
            // as a small number beside their name - a die that decides
            // activations but that nobody ever sees rolled.
            var rolls = new List<DieRoller.Roll>();

            foreach (var player in view.players)
            {
                if (!player.isAlive || !player.hasRolled || player.primaryDie <= 0)
                {
                    continue;
                }

                var yours = player.playerId == view.viewerPlayerId;
                rolls.Add(new DieRoller.Roll(player.name, player.primaryDie, yours));

                foreach (var face in player.privateDice ?? Array.Empty<int>())
                {
                    rolls.Add(new DieRoller.Roll(player.name, face, yours, isPrivate: true));
                }
            }

            if (rolls.Count == 0)
            {
                _dieRoller.Rearm();
                return;
            }

            _dieRoller.Show(rolls);
        }

        /// <summary>
        /// The shout box says nothing about itself until it is unlocked. A table
        /// that does not know the word gets a box that quietly ignores them
        /// rather than a prompt advertising that a passcode exists.
        /// </summary>
        private void RefreshShoutControls(NetworkGameManager manager)
        {
            var unlocked = manager != null && manager.CanShout;

            _shoutButton.GetComponentInChildren<Text>().text = unlocked ? "Say" : "•••";
            _shoutButton.targetGraphic.color = unlocked ? UITheme.Button : UITheme.ButtonQuiet;
        }

        /// <summary>
        /// Where a card is sitting on the board right now, so the activation
        /// stage can lift it out of its own compound rather than materialising it
        /// mid-screen. Read live rather than cached: the board rebuilds often,
        /// and a remembered RectTransform would be pointing at a destroyed card
        /// as often as not.
        /// </summary>
        private Vector3? BoardCardPosition(int cardInstanceId)
        {
            if (_battlefield == null)
            {
                return null;
            }

            foreach (var card in _battlefield.GetComponentsInChildren<BoardCardView>())
            {
                if (card.Card != null && card.Card.instanceId == cardInstanceId)
                {
                    return card.transform.position;
                }
            }

            return null;
        }

        private void MarkIfDueToActivate(BoardCardView card, GameView view)
        {
            if (view.phase == nameof(TurnPhase.Activation))
            {
                var stillQueued = card.Card != null && view.activations
                    .Skip(Mathf.Clamp(view.activationCompletedCount, 0, view.activations.Length))
                    .Any(activation => activation.cardInstanceId == card.Card.instanceId
                                       && !activation.skipped);
                card.SetActivationState(presenting: true, queued: stillQueued);
                return;
            }

            card.ClearDueToActivate();

            // Before the dice land there is nothing to promise, so every card
            // sits at rest.
            if (view.phase != nameof(TurnPhase.Rolling) || !view.diceRolled || card.Definition == null)
            {
                card.SetActivationState(presenting: false, queued: false);
                return;
            }

            // The moment the dice are down, the board sorts itself into what is
            // about to fire and what is not: the woken units light white, and
            // everything else falls away. Deliberately the same treatment the
            // Activation phase then uses, so the roll and the sequence that
            // follows it read as one continuous statement rather than two
            // different highlights meaning the same thing.
            // Shared dice wake everybody's units; a private die wakes only its
            // owner's. Leaving private dice out here meant a unit woken solely by
            // Standardized Uniforms sat dull and then activated anyway, which
            // read as the card not working.
            var shared = view.players.Where(p => p.isAlive && p.primaryDie > 0)
                .Select(p => p.primaryDie).ToHashSet();

            var owner = view.players.FirstOrDefault(p => p.compound
                .Any(c => card.Card != null && c.instanceId == card.Card.instanceId));

            var faces = owner?.privateDice == null
                ? shared
                : shared.Concat(owner.privateDice).ToHashSet();

            var willActivate = card.Definition.Type == CardType.Unit
                               && card.Definition.ActivationNumbers.Any(faces.Contains);

            card.SetActivationState(presenting: true, queued: willActivate);
        }

        private static bool IsDraftable(GameView view, CardView card)
        {
            foreach (var mark in view.draftMarks)
            {
                if (mark.cardInstanceId != card.instanceId)
                {
                    continue;
                }

                if (mark.marker == nameof(DraftMarker.Blocked))
                {
                    return false;
                }

                if (mark.marker == nameof(DraftMarker.Reserved) && mark.playerId != view.viewerPlayerId)
                {
                    return false;
                }
            }

            return true;
        }

        private static string DraftMarkTag(GameView view, CardView card)
        {
            foreach (var mark in view.draftMarks)
            {
                if (mark.cardInstanceId != card.instanceId)
                {
                    continue;
                }

                var owner = FindPlayer(view, mark.playerId)?.name ?? $"player {mark.playerId}";
                if (mark.marker == nameof(DraftMarker.Blocked))
                {
                    return "BLOCKED";
                }

                if (mark.marker == nameof(DraftMarker.Reserved))
                {
                    return $"RESERVED ({owner})";
                }

                if (mark.marker == nameof(DraftMarker.Trapped))
                {
                    return $"TRAPPED ({owner})";
                }
            }

            return null;
        }

        // ---------------------------------------------------------- Hand tray

        private void RefreshHand(GameView view)
        {
            var you = view.Viewer;
            var count = you?.hand.Length ?? 0;
            var canDraftToHand = you is { isAlive: true }
                                 && view.phase == nameof(TurnPhase.Draft)
                                 && view.currentDrafterId == view.viewerPlayerId
                                 && !view.hasPendingChoice;
            RefreshHandDropZone(canDraftToHand);
            _handCountLabel.text = you == null ? "" : $"Hand: {count}";
            var canHandleCards = you is { isAlive: true }
                                 && _handExpanded
                                 && view.phase == nameof(TurnPhase.Buy)
                                 && !view.hasPendingChoice;
            _recycleBin.gameObject.SetActive(canHandleCards && count > 0);

            _viewerStatBar.gameObject.SetActive(you != null);
            if (you != null)
            {
                _viewerStatBar.Populate(you, isViewer: true, DiceRevealed);
            }

            // Rebuilding the tray restarts every card's deal-in animation, so a
            // hand that has not actually changed is left exactly as it is. The
            // board refreshes on every message from the server, and rebuilding
            // regardless made the hand flicker continuously while nothing about
            // it was different.
            var signature = HandSignature(view, you);
            if (signature == _handSignature)
            {
                return;
            }

            _handSignature = signature;

            UIFactory.DestroyChildren(_handRow);

            if (you == null)
            {
                SetHandHeight(0f);
                _handRow.gameObject.SetActive(false);
                return;
            }

            if (count == 0)
            {
                if (view.phase == nameof(TurnPhase.Draft) && !view.isGameOver)
                {
                    SetHandHeight(canDraftToHand ? DraftDropZoneHeight : HandPeekHeight);
                    _handRow.gameObject.SetActive(true);
                }
                else
                {
                    SetHandHeight(0f);
                    _handRow.gameObject.SetActive(false);
                }

                return;
            }

            _handRow.gameObject.SetActive(true);

            // Never hidden outright - it peeks until the pointer finds it, then
            // opens to its full playable size. Only the expanded state offers
            // dragging; a card peeking above the fold is there to be
            // recognised, not acted on by accident.
            if (!_handExpanded)
            {
                BuildHandPeek(you, canDraftToHand);
                return;
            }

            var canBuy = canHandleCards;

            // Overlap makes room for a larger, held-card silhouette. The span is
            // measured from the actual hand count rather than the maximum seven,
            // so a small hand does not shrink merely because it could grow later.
            // Measured from the tray's own width, which now stops clear of the
            // resource HUD, rather than from the whole window.
            var available = Mathf.Max(
                240f,
                _gameRoot.rect.width - HandLeftInset - BoardSafeInset
                - (canBuy ? RecycleBinWidth + 24f : 0f));

            var angle = HandFanMaxAngle * Mathf.Deg2Rad;
            var aspect = BoardCardView.Height / BoardCardView.Width;
            var rotatedWidthUnits = Mathf.Cos(angle) + (aspect * Mathf.Sin(angle));
            var rotatedHeightUnits = (aspect * Mathf.Cos(angle)) + Mathf.Sin(angle);
            var spanUnits = rotatedWidthUnits + (HandFanOverlap * Mathf.Max(0, count - 1));
            var widthAllows = available / spanUnits;

            // The top margin is budgeted here as well as added below, so the
            // cards are sized to leave room for it rather than sized to fill the
            // tray and then pushed out through the top of it.
            var chrome = HandFanCenterLift + (HandFanPadding * 2f) + HandFanTopMargin;
            var heightAllows = (MaxHandHeight - chrome) / rotatedHeightUnits;

            var handCardWidth = Mathf.Clamp(
                Mathf.Min(widthAllows, heightAllows), MinCardWidth, BoardCardView.Width);

            var handCardHeight = handCardWidth * (BoardCardView.Height / BoardCardView.Width);
            var rotatedHeight = handCardWidth * rotatedHeightUnits;
            var handStripHeight = Mathf.Min(MaxHandHeight, rotatedHeight + chrome);
            var fanCenterX = canBuy ? -((RecycleBinWidth + 24f) / 2f) : 0f;

            SetHandHeight(handStripHeight);

            // A card preview or a Ritual covers the whole board, so it reclaims
            // the top after anything else has been added to the canvas.
            CardPreview.BringToFront();

            var fanSlots = new List<(RectTransform Slot, float DistanceFromCenter)>();
            for (var index = 0; index < count; index++)
            {
                var card = you.hand[index];
                var normalized = count == 1 ? 0f : ((index / (count - 1f)) * 2f) - 1f;
                var slot = UIFactory.Group("Card Slot", _handRow);
                UIFactory.SetSize(slot, handCardWidth, handCardHeight);
                slot.anchorMin = slot.anchorMax = new Vector2(0.5f, 0f);
                slot.pivot = new Vector2(0.5f, 0.5f);
                slot.anchoredPosition = new Vector2(
                    fanCenterX
                    + ((index - ((count - 1f) / 2f)) * handCardWidth * HandFanOverlap),
                    HandFanPadding + (rotatedHeight / 2f)
                    + ((1f - Mathf.Abs(normalized)) * HandFanCenterLift));
                slot.localEulerAngles = new Vector3(0f, 0f, -normalized * HandFanMaxAngle);
                fanSlots.Add((slot, Mathf.Abs(normalized)));

                var handCard = BoardCardView.Create(slot);
                var handCardRect = (RectTransform)handCard.transform;
                handCardRect.anchorMin = handCardRect.anchorMax = new Vector2(0.5f, 0.5f);
                handCardRect.pivot = new Vector2(0.5f, 0.5f);

                handCard.Populate(card, null, null);
                handCard.ScaleTo(handCardWidth);

                if (!canBuy)
                {
                    continue;
                }

                var instanceId = card.instanceId;

                // Affordability still says whether the battlefield will accept
                // the card. Recycling accepts every card, so they all drag.
                handCard.SetAffordable(card.canAfford);

                var ghostWidth = handCardWidth;
                var handle = handCard.gameObject.AddComponent<DragHandle>();
                handle.DragLayer = _dragLayer;
                handle.GhostFactory = () =>
                {
                    _draggingFromHand = true;
                    return DragHandle.CardGhost(card, null, ghostWidth);
                };
                handle.OnDragFinished = () => _draggingFromHand = false;
                handle.OnDropped = eventData =>
                {
                    if (_recycleBin.gameObject.activeInHierarchy
                        && RectTransformUtility.RectangleContainsScreenPoint(
                            _recycleBin, eventData.position, eventData.pressEventCamera))
                    {
                        ShowRecycledResource(card);
                        NetworkGameManager.Instance?.RequestRecycleRpc(instanceId);
                        return;
                    }

                    if (card.canAfford
                        && RectTransformUtility.RectangleContainsScreenPoint(
                            _battlefieldViewport, eventData.position, eventData.pressEventCamera))
                    {
                        NetworkGameManager.Instance?.RequestBuyRpc(instanceId);
                    }
                };
            }

            // Paint from the outside inward. The raised centre cards therefore
            // overlap only the bottoms of their neighbours instead of a later
            // right-hand card slicing across everybody else's upper corner.
            foreach (var fanSlot in fanSlots.OrderByDescending(item => item.DistanceFromCenter))
            {
                fanSlot.Slot.SetAsLastSibling();
            }
        }

        /// <summary>
        /// Shows the payment leaving the bin for the permanent resource HUD.
        /// The server remains authoritative; the count is only predicted so the
        /// drop answers immediately, and the next view overwrites it.
        /// </summary>
        private void ShowRecycledResource(CardView card)
        {
            var definition = DefinitionOf(card);
            if (definition == null)
            {
                return;
            }

            var color = definition.Color;
            _resourceHud.ShowResourceGain(color);
            BoardEffects.Instance.FlyResource(
                _recycleBin.position, _resourceHud.PipPosition(color), color,
                landsIn: _resourceHud.Pip(color));
        }

        /// <summary>
        /// The hand collapsed to a sliver above the bottom edge: small enough to
        /// stay out of the way, present enough to say what is in your hand and
        /// invite the hover that opens it.
        /// </summary>
        private void BuildHandPeek(PlayerView you, bool canDraftToHand)
        {
            var peekCardHeight = HandPeekHeight + 6f;
            var peekCardWidth = peekCardHeight * (BoardCardView.Width / BoardCardView.Height);

            SetHandHeight(canDraftToHand ? DraftDropZoneHeight : HandPeekHeight);

            for (var index = 0; index < you.hand.Length; index++)
            {
                var card = you.hand[index];
                var normalized = you.hand.Length == 1
                    ? 0f
                    : ((index / (you.hand.Length - 1f)) * 2f) - 1f;
                var slot = UIFactory.Group("Card Slot", _handRow);
                UIFactory.SetSize(slot, peekCardWidth, peekCardHeight);
                slot.anchorMin = slot.anchorMax = new Vector2(0.5f, 0f);
                slot.pivot = new Vector2(0.5f, 0f);
                slot.anchoredPosition = new Vector2(
                    (index - ((you.hand.Length - 1f) / 2f)) * peekCardWidth * 0.58f,
                    -5f + (Mathf.Abs(normalized) * 3f));
                slot.localEulerAngles = new Vector3(0f, 0f, -normalized * 6f);

                var peek = BoardCardView.Create(slot);
                var rect = (RectTransform)peek.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                peek.Populate(card, null, null);
                peek.ScaleTo(peekCardWidth);
            }
        }

        private void RefreshHandDropZone(bool active)
        {
            _handDropZone.gameObject.SetActive(active);
            if (!active)
            {
                SetHandDropZoneHot(false);
                return;
            }

            var width = Mathf.Min(DraftDropZoneMaxWidth, Mathf.Max(300f, _gameRoot.rect.width - 80f));
            _handDropZone.sizeDelta = new Vector2(width, DraftDropZoneHeight);

            // The band stretches with its zone now, so there is no second size
            // to keep in step with it.
            SetHandDropZoneHot(false);
        }

        /// <summary>Lights the shelf while the carried draft card is over it.</summary>
        private void SetHandDropZoneHot(bool hot)
        {
            if (_handDropArc == null)
            {
                return;
            }

            _handDropArc.color = hot
                ? new Color(UITheme.Signal.r, UITheme.Signal.g, UITheme.Signal.b, 0.26f)
                : new Color(UITheme.Signal.r, UITheme.Signal.g, UITheme.Signal.b, 0.10f);
            _handDropLabel.color = hot ? UITheme.Bone : UITheme.BoneDim;
            _handDropZone.localScale = hot ? Vector3.one * 1.035f : Vector3.one;
        }

        /// <summary>
        /// Resizes the floating hand tray. It grows upward from the bottom edge,
        /// so nothing else on the board has to move to make room for it.
        /// </summary>
        private void SetHandHeight(float height)
        {
            // Stops short of the resource HUD rather than spanning the whole
            // width. The tray is opaque and answers the pointer, so covering the
            // HUD did not merely hide the circles - it swallowed the clicks
            // meant for them, and resources could not be taken with the hand open.
            var left = HandLeftInset;
            _handRow.sizeDelta = new Vector2(-(left + BoardSafeInset), height);
            _handRow.anchoredPosition = new Vector2(
                (left - BoardSafeInset) / 2f, DockTopHeight + BoardSafeInset);
        }

        /// <summary>How far the hand keeps clear of the permanent resource HUD.</summary>
        private const float HandLeftInset = BoardSafeInset + ResourceHudWidth + 10f;

        /// <summary>
        /// Everything about the hand that would change what is drawn: which
        /// cards are in it, whether each is affordable, whether it is open, and
        /// whether it is offering its buttons. Anything not in here is a
        /// difference the tray does not need rebuilding for.
        /// </summary>
        private string HandSignature(GameView view, PlayerView you)
        {
            if (you == null || you.hand.Length == 0)
            {
                return $"empty|{view.phase}|{view.currentDrafterId}|{view.viewerPlayerId}";
            }

            var canBuy = view.phase == nameof(TurnPhase.Buy) && !view.hasPendingChoice;
            var canDraft = view.phase == nameof(TurnPhase.Draft)
                           && view.currentDrafterId == view.viewerPlayerId
                           && !view.hasPendingChoice;
            var cards = string.Join(",", you.hand.Select(card => $"{card.instanceId}:{card.canAfford}"));

            // The width matters because the cards are sized from it, so a
            // resized window still rebuilds.
            return $"{_handExpanded}|{canBuy}|{canDraft}|"
                   + $"{Mathf.RoundToInt(_gameRoot.rect.width)}|{cards}";
        }

        private string _handSignature;

        // ------------------------------------------------------- Action panel

        /// <summary>How the game ended, said plainly enough to read at a glance.</summary>
        private static string GameOverHeadline(GameView view)
        {
            if (view.isDraw)
            {
                return view.players.Any(p => p.isAlive)
                    ? "The table agreed to a draw."
                    : "Everyone is out. The game is a draw.";
            }

            var winner = FindPlayer(view, view.winnerPlayerId);
            var name = winner?.name ?? "Somebody";

            return winner != null && winner.followers >= GameSettings.FollowersToWin
                ? $"{name} wins with {winner.followers} followers."
                : $"{name} wins - last leader standing.";
        }

        /// <summary>
        /// The popup that stands in for the old always-on side panel. Most
        /// phases show nothing at all here - the board, the hand, and a card's
        /// own preview are where the game actually happens, and this only
        /// interrupts them for something that genuinely needs an answer right
        /// now: a pending choice, a die to roll, a high-roll bonus to take, or
        /// the end of the game.
        /// </summary>
        private void RefreshActionPanel(NetworkGameManager manager, GameView view)
        {
            UIFactory.DestroyChildren(_actionPanel);
            UIFactory.SetSize(_popupPanel, PopupWidth, PopupHeight);

            var show = DecidePopup(manager, view);

            _popupPanel.gameObject.SetActive(show);
        }

        private bool DecidePopup(NetworkGameManager manager, GameView view)
        {
            if (view.isGameOver)
            {
                RenderGameOver(manager, view);
                return true;
            }

            // During activation the question belongs to the stage, put under the
            // card that is asking it. Answering it here as well would offer the
            // same decision twice, in two places, with the card only visible in
            // one of them.
            if (view.phase == nameof(TurnPhase.Activation))
            {
                return false;
            }

            if (view.hasPendingChoice
                && view.pendingChoice.askedOfPlayerId == view.viewerPlayerId
                && !ChoiceIsAnsweredOnTheBoard(view))
            {
                RenderPendingChoice(manager, view);
                return true;
            }

            return view.phase == nameof(TurnPhase.Rolling) && RenderRolling(manager, view);
        }

        /// <summary>
        /// The end of the game: how it finished, the final standings, and the
        /// host's offer of another one. Without this the table is left staring at
        /// a board nothing will ever change again.
        /// </summary>
        /// <summary>
        /// The end of the game, given the room it deserves: who won and how, then
        /// a real scoreboard with each leader's final followers and health drawn
        /// as bars, and the host's offer of another game.
        /// </summary>
        private void RenderGameOver(NetworkGameManager manager, GameView view)
        {
            var winner = view.isDraw ? null : FindPlayer(view, view.winnerPlayerId);
            var youWon = winner != null && winner.playerId == view.viewerPlayerId;

            var banner = ActionLabel(
                view.isDraw ? "DRAW" : youWon ? "YOU WIN" : "DEFEATED", 34);
            banner.alignment = TextAnchor.MiddleCenter;
            banner.fontStyle = FontStyle.Bold;
            banner.color = view.isDraw
                ? UITheme.BoneDim
                : youWon
                    ? new Color(0.361f, 0.878f, 0.647f)
                    : new Color(0.902f, 0.361f, 0.416f);
            SetRowHeight(banner.rectTransform, 48);

            var subtitle = ActionLabel(GameOverHeadline(view), 15);
            subtitle.alignment = TextAnchor.MiddleCenter;
            subtitle.color = UITheme.BoneDim;
            SetRowHeight(subtitle.rectTransform, 40);

            foreach (var player in view.players
                         .OrderByDescending(p => p.followers)
                         .ThenByDescending(p => p.health))
            {
                BuildFinalStanding(player, player.playerId == view.winnerPlayerId,
                                   player.playerId == view.viewerPlayerId);
            }

            var network = NetworkManager.Singleton;
            if (network != null && network.IsHost)
            {
                UIFactory.ButtonWithLabel("Play Again", _actionPanel, "Play Again",
                    () => manager.RequestPlayAgainRpc(), UITheme.Affirm, ActionButtonWidth(), 44);
            }
            else
            {
                ActionLabel("Waiting for the host to start another game.", 13);
            }

            UIFactory.ButtonWithLabel("Leave", _actionPanel, "Leave",
                LeaveGame, UITheme.Blood, ActionButtonWidth(), 34);
        }

        /// <summary>One leader's final line: name, and their two tracks as bars.</summary>
        private void BuildFinalStanding(PlayerView player, bool won, bool isViewer)
        {
            var row = UIFactory.Panel($"Standing {player.playerId}", _actionPanel,
                won ? new Color(0.098f, 0.216f, 0.184f, 0.96f) : UITheme.SurfaceSoft);
            SetRowHeight(row, 62);

            var layout = UIFactory.VerticalLayout(row, 2, new RectOffset(8, 8, 5, 5), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandWidth = true;

            var name = UIFactory.Label("Name", row, 
                $"{(won ? "★ " : "")}{player.name}{(isViewer ? " (you)" : "")}"
                + $"{(player.isAlive ? "" : player.hasResigned ? "  -  resigned" : "  -  out")}",
                14, TextAnchor.MiddleLeft);
            name.fontStyle = FontStyle.Bold;
            SetRowHeight(name.rectTransform, 18);

            FinalBar(row, "Followers", player.followers, GameSettings.FollowersToWin,
                     UITheme.Signal);
            FinalBar(row, "Health", player.health, GameSettings.MaxHealth,
                     new Color(0.800f, 0.247f, 0.318f));
        }

        private void FinalBar(Transform parent, string label, int value, int max, Color color)
        {
            var track = UIFactory.Panel(label, parent, new Color(0f, 0f, 0f, 0.5f));
            SetRowHeight(track, 14);

            var fill = UIFactory.FillBar($"{label} Fill", track, color);
            UIFactory.Stretch(fill.rectTransform);
            fill.fillAmount = Mathf.Clamp01((float)value / max);

            var text = UIFactory.Label($"{label} Text", track, $"{value} {label.ToLowerInvariant()}",
                                       11, TextAnchor.MiddleCenter);
            UIFactory.Stretch(text.rectTransform);
        }

        private static void SetRowHeight(RectTransform rect, float height)
        {
            var element = rect.gameObject.GetComponent<LayoutElement>()
                          ?? rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleWidth = 1;
        }

        /// <summary>
        /// How wide a control in the side panel may be. The panel is deliberately
        /// narrow - the compounds are the main event - so its controls are sized
        /// from it rather than assuming a width it no longer has.
        /// </summary>
        private float ActionButtonWidth()
        {
            var available = _actionViewport.rect.width - 24f;
            return Mathf.Clamp(available, 120f, 260f);
        }

        private Text ActionLabel(string text, int fontSize = 15, RectTransform parent = null)
        {
            var label = UIFactory.Label("Info", (Transform)(parent != null ? parent : _actionPanel),
                text, fontSize, TextAnchor.UpperLeft);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            var element = label.gameObject.AddComponent<LayoutElement>();
            element.flexibleWidth = 1;
            return label;
        }

        /// <summary>
        /// The one thing Rolling ever needs a popup for: the die itself, then -
        /// once it is down - the high-roll bonus if one is owed. Returns whether
        /// there was anything to show; once both are settled the popup has
        /// nothing left to say and disappears.
        /// </summary>
        private bool RenderRolling(NetworkGameManager manager, GameView view)
        {
            var you = view.Viewer;
            if (you is { isAlive: true, hasRolled: false })
            {
                UIFactory.SetSize(_popupPanel, RollingPopupWidth, RollingPopupHeight);
                UIFactory.ButtonWithLabel(
                    "Roll", _actionPanel, "ROLL DIE", () => manager.RequestRollRpc(),
                    UITheme.Affirm, width: RollButtonWidth, height: RollButtonHeight);
                return true;
            }

            if (!view.diceRolled || view.highRollResourceClaimed)
            {
                return false;
            }

            // Not until the dice have actually stopped. Being handed the prize
            // while they are still in the air says who won before the roll does,
            // which makes the whole throw decorative.
            if (!_dieRoller.Settled)
            {
                return false;
            }

            var highRoller = HighestUniqueRoller(view);
            if (highRoller < 0 || highRoller != view.viewerPlayerId)
            {
                return false;
            }

            // Taken from the circles on the left, the same as every other
            // resource this game hands out. A second set of colour buttons in a
            // popup taught a different way to pick a colour for the one case
            // that is not the resource phase.
            ActionLabel("Highest roll - take one from the left", 16);
            return true;
        }

        /// <summary>Suspicious Chef's paid meal counter, offered from the card itself.</summary>
        private void BuildSuspiciousChefExtra(
            RectTransform content, NetworkGameManager manager, int cardInstanceId, BoardCardView cardView)
        {
            ActionLabel(
                $"Pay {GameSettings.MealCounterCost} of any colour: {string.Join(", ", _pendingMealPayment)}",
                13, content);

            RenderColorButtons(color =>
            {
                _pendingMealPayment.Add(color);
                if (_pendingMealPayment.Count == GameSettings.MealCounterCost)
                {
                    manager.RequestBuyMealCounterRpc(
                        cardInstanceId, _pendingMealPayment.ConvertAll(c => (int)c).ToArray());
                    _pendingMealPayment.Clear();
                }

                CardPreview.RefreshIfShowing(cardView);
            }, parent: content);

            if (_pendingMealPayment.Count > 0)
            {
                UIFactory.ButtonWithLabel("Clear Meal", content, "Clear", () =>
                {
                    _pendingMealPayment.Clear();
                    CardPreview.RefreshIfShowing(cardView);
                }, width: 100);
            }
        }

        /// <summary>
        /// Baal's Scheme-counter reroll: pick a player, then a face, offered
        /// from the card itself.
        /// </summary>
        private void BuildBaalExtra(RectTransform content, NetworkGameManager manager, GameView view, BoardCardView cardView)
        {
            ActionLabel("Spend a Scheme counter to set a die:", 13, content);

            var row = UIFactory.Group("Baal Targets", content);
            AddFixedHeight(row, 32);
            UIFactory.HorizontalLayout(row, 4, new RectOffset(0, 0, 0, 0));
            foreach (var player in view.players.Where(p => p.isAlive))
            {
                var targetId = player.playerId;
                UIFactory.ButtonWithLabel(player.name, row, player.name, () =>
                {
                    _baalTargetPlayerId = targetId;
                    CardPreview.RefreshIfShowing(cardView);
                }, width: 90, height: 28);
            }

            if (_baalTargetPlayerId < 0)
            {
                return;
            }

            ActionLabel($"Set {FindPlayer(view, _baalTargetPlayerId)?.name}'s die to:", 13, content);
            var faces = UIFactory.Group("Baal Faces", content);
            AddFixedHeight(faces, 32);
            UIFactory.HorizontalLayout(faces, 4, new RectOffset(0, 0, 0, 0));
            for (var face = 1; face <= GameSettings.DieSides; face++)
            {
                var chosenFace = face;
                UIFactory.ButtonWithLabel($"Face {face}", faces, face.ToString(), () =>
                {
                    manager.RequestSpendSchemeCounterRpc(_baalTargetPlayerId, chosenFace);
                    _baalTargetPlayerId = -1;
                    CardPreview.Hide();
                }, width: 36, height: 28);
            }
        }

        /// <summary>Try Again's reroll, offered from the card itself.</summary>
        private void BuildTryAgainExtra(RectTransform content, NetworkGameManager manager)
        {
            UIFactory.ButtonWithLabel("Reroll", content, "Try Again", () =>
            {
                CardPreview.Hide();
                manager.RequestRerollRpc();
            }, width: 160, height: 36);
        }

        /// <summary>
        /// What this player still has to do before readying up makes sense, or
        /// null when readying is the only move left to them.
        /// </summary>
        private static string WhatThePhaseStillWants(GameView view, PlayerView you)
        {
            if (view.phase == nameof(TurnPhase.Rolling) && !you.hasRolled)
            {
                return "Roll your die first";
            }

            // The phase deliberately waits rather than closing the instant the
            // last die lands, or the reroll could never be reached.
            if (you.canReroll)
            {
                return "Try again is open - reroll from the card, or ready up";
            }

            if (view.phase == nameof(TurnPhase.Resource) && !you.collectedResources)
            {
                return "Take your resources first";
            }

            return null;
        }

        /// <summary>Opens the discard pile for reading. Everything in it is public.</summary>
        private void ShowDiscard()
        {
            var view = NetworkGameManager.Instance?.View;
            if (view == null)
            {
                return;
            }

            _discardOpen = !_discardOpen;

            // Only the board changes. Rebuilding the stat bars as well restarted
            // every bar animation from wherever it had got to, which read as the
            // health bars flickering every time a menu was opened.
            RefreshBattlefield(NetworkGameManager.Instance, view);
        }

        private bool _discardOpen;

        /// <summary>
        /// Resigning takes two presses. It ends your game with no way back, so a
        /// misclick must not be able to do it.
        /// </summary>
        private void PressResign()
        {
            if (!_resignArmed)
            {
                _resignArmed = true;
                _resignLabel.text = "Sure?";
                _resignButton.targetGraphic.color = new Color(0.906f, 0.267f, 0.310f);
                return;
            }

            _resignArmed = false;
            NetworkGameManager.Instance?.RequestResignRpc();
        }

        /// <summary>
        /// Opens the quit warning. Never quits on the press itself: leaving is
        /// a resignation once a game is on, and that is not something a stray
        /// click gets to do.
        /// </summary>
        private void OpenQuitPrompt()
        {
            _quitPrompt?.Open();
        }

        private void ToggleDrawOffer()
        {
            var you = NetworkGameManager.Instance?.View?.Viewer;
            if (you != null)
            {
                NetworkGameManager.Instance.RequestOfferDrawRpc(!you.offeringDraw);
            }
        }

        /// <summary>
        /// Updates the two concession controls. A draw needs the whole table, so
        /// it shows how many have agreed; resigning needs nobody, so it does not.
        /// </summary>
        private void RefreshConcessionControls(GameView view)
        {
            var you = view.Viewer;
            var usable = you is { isAlive: true } && !view.isGameOver;

            _drawButton.gameObject.SetActive(usable);
            _resignButton.gameObject.SetActive(usable);

            if (!usable)
            {
                return;
            }

            var offering = view.players.Count(p => p.isAlive && p.offeringDraw);
            var alive = view.players.Count(p => p.isAlive);

            _drawButton.GetComponentInChildren<Text>().text = you.offeringDraw
                ? $"Draw {offering}/{alive}"
                : "Offer draw";

            _drawButton.targetGraphic.color = you.offeringDraw
                ? UITheme.Affirm
                : UITheme.ButtonQuiet;

            // A confirmation left armed from an earlier turn is stale, and would
            // let a single press end the game much later.
            if (_resignArmed && !ReferenceEquals(view, _armedForView))
            {
                _resignArmed = false;
            }

            _armedForView = _resignArmed ? view : null;

            _resignLabel.text = _resignArmed ? "Sure?" : "Resign";
            _resignButton.targetGraphic.color = _resignArmed
                ? new Color(0.906f, 0.267f, 0.310f)
                : UITheme.Blood;
        }

        private GameView _armedForView;

        private void ToggleStatusDetail()
        {
            _statusExpanded = !_statusExpanded;

            // Local only - the game has not changed, so nothing is rebuilt but
            // this one label.
            var view = NetworkGameManager.Instance?.View;
            if (view != null)
            {
                _statusText.text = StatusLine(view);
            }
        }

        /// <summary>
        /// The counters, kept to one short line unless expanded. Everything here
        /// is reference material - the phase itself is announced by the banner
        /// and shown by the controls.
        /// </summary>
        private string StatusLine(GameView view)
        {
            if (view.isGameOver)
            {
                return GameOverHeadline(view);
            }

            var line = _statusExpanded
                ? $"{view.phase}   draft {view.draftNumber}, turn {view.turnInRound}/{GameSettings.TurnsPerRound}   "
                  + $"deck {view.deckCount}, discard {view.discardCount}"
                : $"{view.phase}   T{view.turnInRound}";

            // A popup only ever interrupts the player it is actually asking, so
            // this is the only trace anyone else sees of a question being open.
            if (view.hasPendingChoice && view.pendingChoice.askedOfPlayerId != view.viewerPlayerId)
            {
                return line + $"   waiting on {FindPlayer(view, view.pendingChoice.askedOfPlayerId)?.name} to decide";
            }

            if (view.phase == nameof(TurnPhase.Draft))
            {
                return line + (view.currentDrafterId == view.viewerPlayerId
                    ? "   your pick"
                    : $"   {FindPlayer(view, view.currentDrafterId)?.name} is picking");
            }

            return line;
        }

        private void ToggleReady()
        {
            var view = NetworkGameManager.Instance?.View;
            if (view?.Viewer != null)
            {
                NetworkGameManager.Instance.RequestSetReadyRpc(!view.Viewer.isReady);
            }
        }

        /// <summary>
        /// Updates the dock's Ready control. It is always present during play, so
        /// the control that ends a phase can never be scrolled or covered away.
        /// </summary>
        private void RefreshReadyControl(GameView view)
        {
            var you = view.Viewer;
            var usable = you is { isAlive: true }
                         && view.phase != nameof(TurnPhase.Draft)
                         && !view.isGameOver;

            _readyButton.gameObject.SetActive(usable);
            _waitingLabel.gameObject.SetActive(usable || you is { isAlive: false });

            if (you is { isAlive: false })
            {
                _waitingLabel.text = "You are out. Watching the rest play out.";
                return;
            }

            if (!usable)
            {
                return;
            }

            // Greyed out while the player still owes the phase something, so the
            // button never invites a press that would be refused - and pulsing
            // when it is the only move left, so it is obvious the table is on them.
            var owes = WhatThePhaseStillWants(view, you);
            var actionable = owes == null && !you.isReady;

            _readyButton.interactable = owes == null;
            _readyLabel.text = you.isReady ? "Not Ready" : "Ready";
            _readyLabel.color = owes == null ? UITheme.Bone : new Color(
                UITheme.Bone.r, UITheme.Bone.g, UITheme.Bone.b, 0.45f);

            _readyButton.targetGraphic.color = owes != null
                ? UITheme.ButtonQuiet
                : you.isReady
                    ? new Color(0.278f, 0.208f, 0.129f)
                    : UITheme.Affirm;

            BoardEffects.Instance.SetPulsing(_readyButton.targetGraphic, actionable);

            var waitingOn = view.players.Where(p => p.isAlive && !p.isReady).Select(p => p.name).ToList();
            _waitingLabel.text = owes ?? (waitingOn.Count > 0
                ? $"Waiting on {string.Join(", ", waitingOn)}"
                : "Everyone ready.");
        }

        // -------------------------------------------------------- Pending choice

        /// <summary>
        /// A card's question as it appears on the activation stage: the options
        /// and nothing else.
        ///
        /// No prompt, because the card asking is on screen at full size directly
        /// above these buttons and its own text says what it does. The only
        /// exception is an amount, where the legal range is not visible anywhere
        /// else and the field is meaningless without it.
        /// </summary>
        private void BuildActivationChoice(
            RectTransform parent, Text prompt, NetworkGameManager manager, GameView view)
        {
            if (!view.hasPendingChoice)
            {
                return;
            }

            var choice = view.pendingChoice;

            if (choice.askedOfPlayerId != view.viewerPlayerId)
            {
                var waiting = UIFactory.Label("Waiting", parent,
                    $"Waiting on {FindPlayer(view, choice.askedOfPlayerId)?.name}", 17,
                    TextAnchor.MiddleCenter, UITheme.BoneDim);
                AddFlexibleWidth(waiting.rectTransform);
                return;
            }

            switch (choice.kind)
            {
                case nameof(ChoiceKind.Player):
                    // Answered by pressing the player's own track rather than by
                    // picking their name out of a list down here. Everything the
                    // decision needs - their health, their block, their
                    // followers - is already drawn on that track.
                    _activationStage.OfferPlayerTargets(
                        choice.playerOptions, id => manager.RequestAnswerPlayerRpc(id));

                    if (_activationStage.HasPlayerTargets)
                    {
                        var pick = UIFactory.Label("Pick A Player", parent,
                            "Click whose track to use", 17, TextAnchor.MiddleCenter, UITheme.Signal);
                        AddFlexibleWidth(pick.rectTransform);
                        break;
                    }

                    // No track to press - a player who is not on the stage, or a
                    // stage that failed to build one. The list is still the way
                    // through, and a choice with no way to answer it stalls the
                    // whole game.
                    foreach (var optionId in choice.playerOptions)
                    {
                        var id = optionId;
                        UIFactory.ButtonWithLabel($"Player {id}", parent,
                            FindPlayer(view, id)?.name ?? id.ToString(),
                            () => manager.RequestAnswerPlayerRpc(id), UITheme.Button, 150, 44);
                    }

                    break;

                case nameof(ChoiceKind.Card):
                    foreach (var cardId in choice.cardOptions)
                    {
                        var id = cardId;
                        var option = FindCard(view, id);
                        var title = option != null && CardDatabase.Instance.TryGet(option.definitionId, out var found)
                            ? found.Title
                            : $"Card {id}";

                        UIFactory.ButtonWithLabel($"Card {id}", parent, title,
                            () => manager.RequestAnswerCardRpc(id), UITheme.Button, 170, 44);
                    }

                    break;

                case nameof(ChoiceKind.Color):
                    var offered = choice.colorOptions.Length > 0
                        ? choice.colorOptions.Select(c => (ResourceColor)c)
                        : Enum.GetValues(typeof(ResourceColor)).Cast<ResourceColor>();
                    RenderColorButtons(color => manager.RequestAnswerColorRpc((int)color), offered, parent);
                    break;

                case nameof(ChoiceKind.Option):
                    foreach (var option in choice.options)
                    {
                        var chosen = option;
                        UIFactory.ButtonWithLabel(chosen, parent, chosen,
                            () => manager.RequestAnswerOptionRpc(chosen), UITheme.Button, 170, 44);
                    }

                    break;

                case nameof(ChoiceKind.YesNo):
                    // The one kind that cannot speak for itself. "Yes" is only
                    // meaningful next to what is being offered, so this is the
                    // "unless it's unclear" case and the prompt comes with it.
                    prompt.text = choice.prompt;

                    UIFactory.ButtonWithLabel("Yes", parent, "Yes",
                        () => manager.RequestAnswerYesNoRpc(true), UITheme.Affirm, 130, 44);
                    UIFactory.ButtonWithLabel("No", parent, "No",
                        () => manager.RequestAnswerYesNoRpc(false), UITheme.Blood, 130, 44);
                    break;

                case nameof(ChoiceKind.Amount):
                    var field = UIFactory.TextInput("Amount Field", parent, _amountInput);
                    AddFixedWidthHeight(field.GetComponent<RectTransform>(), 90, 44);
                    field.onValueChanged.AddListener(value => _amountInput = value);

                    UIFactory.ButtonWithLabel("Confirm", parent,
                        $"{choice.minAmount}-{choice.maxAmount}", () =>
                        {
                            if (int.TryParse(_amountInput, out var amount)
                                && amount >= choice.minAmount && amount <= choice.maxAmount)
                            {
                                manager.RequestAnswerAmountRpc(amount);
                            }
                        }, UITheme.Affirm, 130, 44);
                    break;
            }
        }

        private void RenderPendingChoice(NetworkGameManager manager, GameView view)
        {
            var choice = view.pendingChoice;
            var activationChoice = view.phase == nameof(TurnPhase.Activation);

            // The card behind the question, shown at full size rather than only
            // described - a popup asking what to do about a card should let you
            // look at it.
            if (!activationChoice
                && !string.IsNullOrEmpty(view.resolvingCardId)
                && CardDatabase.Instance.TryGet(view.resolvingCardId, out _))
            {
                var sourceCell = UIFactory.Group("Source Card", _actionPanel);
                UIFactory.SetSize(sourceCell, 130, 181);
                var sourcePin = sourceCell.gameObject.AddComponent<LayoutElement>();
                sourcePin.preferredWidth = sourcePin.minWidth = 130;
                sourcePin.preferredHeight = sourcePin.minHeight = 181;

                var sourceCard = BoardCardView.Create(sourceCell);
                var sourceRect = (RectTransform)sourceCard.transform;
                sourceRect.anchorMin = sourceRect.anchorMax = new Vector2(0.5f, 0.5f);
                sourceRect.pivot = new Vector2(0.5f, 0.5f);
                sourceCard.Populate(new CardView { definitionId = view.resolvingCardId, instanceId = -1 }, null, null);
                sourceCard.ScaleTo(130);
            }

            if (!activationChoice && !string.IsNullOrEmpty(view.resolvingDescription))
            {
                ActionLabel(view.resolvingDescription, 13);
            }

            // During the automatic sequence, the options themselves are the UI.
            // Keep wording only where bare values would genuinely be ambiguous.
            if (!activationChoice
                || choice.kind == nameof(ChoiceKind.Amount)
                || choice.kind == nameof(ChoiceKind.YesNo)
                || (choice.kind == nameof(ChoiceKind.Option) && choice.options.Length < 2))
            {
                ActionLabel(choice.prompt);
            }

            switch (choice.kind)
            {
                case nameof(ChoiceKind.Player):
                    foreach (var optionId in choice.playerOptions)
                    {
                        var id = optionId;
                        var option = FindPlayer(view, id);
                        UIFactory.ButtonWithLabel($"Player {id}", _actionPanel, option?.name ?? id.ToString(),
                            () => manager.RequestAnswerPlayerRpc(id), width: ActionButtonWidth());
                    }

                    break;

                case nameof(ChoiceKind.Card) when ChoiceIsAnsweredOnTheBoard(view):
                    ActionLabel("Pick one from the board.", 15);
                    break;

                case nameof(ChoiceKind.Card):
                    var options = choice.cardOptions
                        .Select(id => FindCard(view, id))
                        .Where(card => card != null)
                        .ToArray();

                    if (activationChoice)
                    {
                        foreach (var option in options)
                        {
                            var chosen = option;
                            var definition = DefinitionOf(option);
                            UIFactory.ButtonWithLabel(
                                $"Card {option.instanceId}", _actionPanel,
                                definition?.Title ?? option.definitionId,
                                () => manager.RequestAnswerCardRpc(chosen.instanceId),
                                width: ActionButtonWidth(), height: 36);
                        }

                        break;
                    }

                    // The side panel is narrow, so these are laid out at whatever
                    // width fits rather than at the board's shared card size.
                    BuildCardRow(_actionPanel, new PlannedRow
                    {
                        Label = "Choose one:",
                        Cards = options,
                        IsClickable = _ => true,
                        OnClick = card => manager.RequestAnswerCardRpc(card.instanceId),
                        ActionLabel = "Choose"
                    },
                    Mathf.Min(BoardCardView.Width, Mathf.Max(90f, _actionViewport.rect.width - 24f)),
                    view,
                    availableWidth: Mathf.Max(120f, _actionViewport.rect.width - 24f));
                    break;

                case nameof(ChoiceKind.Color):
                    var offered = choice.colorOptions.Length > 0
                        ? choice.colorOptions.Select(c => (ResourceColor)c)
                        : Enum.GetValues(typeof(ResourceColor)).Cast<ResourceColor>();
                    RenderColorButtons(color => manager.RequestAnswerColorRpc((int)color), offered);
                    break;

                case nameof(ChoiceKind.Option):
                    foreach (var option in choice.options)
                    {
                        var chosen = option;
                        UIFactory.ButtonWithLabel(chosen, _actionPanel, chosen,
                            () => manager.RequestAnswerOptionRpc(chosen), width: ActionButtonWidth());
                    }

                    break;

                case nameof(ChoiceKind.YesNo):
                    var yesNoRow = UIFactory.Group("Yes No", _actionPanel);
                    AddFixedHeight(yesNoRow, 36);
                    UIFactory.HorizontalLayout(yesNoRow, 8, new RectOffset(0, 0, 0, 0));
                    UIFactory.ButtonWithLabel("Yes", yesNoRow, "Yes", () => manager.RequestAnswerYesNoRpc(true),
                        UITheme.Affirm, 90, 32);
                    UIFactory.ButtonWithLabel("No", yesNoRow, "No", () => manager.RequestAnswerYesNoRpc(false),
                        UITheme.Blood, 90, 32);
                    break;

                case nameof(ChoiceKind.Amount):
                    ActionLabel($"Between {choice.minAmount} and {choice.maxAmount}.", 13);
                    var amountRow = UIFactory.Group("Amount Row", _actionPanel);
                    AddFixedHeight(amountRow, 36);
                    UIFactory.HorizontalLayout(amountRow, 8, new RectOffset(0, 0, 0, 0));
                    var amountField = UIFactory.TextInput("Amount Field", amountRow, _amountInput);
                    AddFixedWidthHeight(amountField.GetComponent<RectTransform>(), 80, 32);
                    amountField.onValueChanged.AddListener(value => _amountInput = value);
                    UIFactory.ButtonWithLabel("Confirm", amountRow, "Confirm", () =>
                    {
                        if (int.TryParse(_amountInput, out var amount)
                            && amount >= choice.minAmount && amount <= choice.maxAmount)
                        {
                            manager.RequestAnswerAmountRpc(amount);
                        }
                    }, width: 100, height: 32);
                    break;
            }
        }

        /// <summary>
        /// Throws a resource across the board from the button that granted it to
        /// the HUD circle that now counts it, so a collected resource is
        /// something you watch arrive rather than a number that changed while
        /// you looked away.
        /// </summary>
        private void FlyResourceToHud(ResourceColor color, Component from)
        {
            if (from == null || _resourceHud == null)
            {
                return;
            }

            BoardEffects.Instance.FlyResource(
                from.transform.position, _resourceHud.PipPosition(color), color,
                landsIn: _resourceHud.Pip(color));

            // The count goes up now rather than when the server replies, so the
            // pick feels immediate. The next view corrects it regardless.
            _resourceHud.ShowResourceGain(color);
        }

        /// <summary>Whether the open card question is being answered on the board itself.</summary>
        private static bool ChoiceIsAnsweredOnTheBoard(GameView view) =>
            view.hasPendingChoice
            && view.pendingChoice.kind == nameof(ChoiceKind.Card)
            && view.pendingChoice.cardOptions.Length > 0
            && view.pendingChoice.cardOptions.All(
                id => view.draftZone.Any(c => c.instanceId == id));

        /// <summary>
        /// A row of colour discs, one per resource, wherever a card's question
        /// wants one - inside the popup by default, or inside a card's own
        /// preview when the question is that card's own ability.
        /// </summary>
        private void RenderColorButtons(
            Action<ResourceColor> onPicked, IEnumerable<ResourceColor> colors = null, RectTransform parent = null)
        {
            var row = UIFactory.Group("Colors", parent != null ? parent : _actionPanel);
            AddFixedHeight(row, ResourceButtonSize + 6f);
            var layout = UIFactory.HorizontalLayout(row, 10, new RectOffset(0, 0, 0, 0));
            layout.childAlignment = TextAnchor.MiddleCenter;

            foreach (var color in colors ?? BoardArt.Colors.AsEnumerable())
            {
                var chosen = color;

                var button = UIFactory.Group(color.ToString(), row);
                UIFactory.SetSize(button, ResourceButtonSize, ResourceButtonSize);
                var pin = button.gameObject.AddComponent<LayoutElement>();
                pin.minWidth = pin.preferredWidth = ResourceButtonSize;
                pin.minHeight = pin.preferredHeight = ResourceButtonSize;

                var disc = button.gameObject.AddComponent<Image>();
                disc.sprite = BoardArt.Disc;
                disc.color = BoardArt.ColorOf(chosen);

                var press = button.gameObject.AddComponent<Button>();
                press.targetGraphic = disc;
                press.onClick.AddListener(() =>
                {
                    FlyResourceToHud(chosen, button);
                    onPicked(chosen);
                });

                var label = UIFactory.Label("Name", button, chosen.ToString()[..1], 18,
                    TextAnchor.MiddleCenter, Color.white);
                label.fontStyle = FontStyle.Bold;
                UIFactory.Stretch(label.rectTransform);
                var outline = label.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
                outline.effectDistance = new Vector2(1f, -1f);
            }
        }

        // --------------------------------------------------------------- Helpers

        private static int HighestUniqueRoller(GameView view)
        {
            var highest = 0;
            var leader = -1;
            var tied = false;

            foreach (var player in view.players)
            {
                if (!player.isAlive)
                {
                    continue;
                }

                if (player.primaryDie > highest)
                {
                    highest = player.primaryDie;
                    leader = player.playerId;
                    tied = false;
                }
                else if (player.primaryDie == highest)
                {
                    tied = true;
                }
            }

            return tied ? -1 : leader;
        }

        private static PlayerView FindPlayer(GameView view, int playerId) =>
            view.players.FirstOrDefault(p => p.playerId == playerId);

        private static CardView FindCard(GameView view, int instanceId)
        {
            foreach (var player in view.players)
            {
                var inHand = player.hand.FirstOrDefault(c => c.instanceId == instanceId);
                if (inHand != null)
                {
                    return inHand;
                }

                var inCompound = player.compound.FirstOrDefault(c => c.instanceId == instanceId);
                if (inCompound != null)
                {
                    return inCompound;
                }
            }

            var inZone = view.draftZone.FirstOrDefault(c => c.instanceId == instanceId);
            if (inZone != null)
            {
                return inZone;
            }

            // Worshiper of the Bone God picks a Ritual out of the discard, so a
            // card being offered is not always one that is in play. Leaving the
            // discard out of this search made its prompt come up empty.
            return view.discardPile.FirstOrDefault(c => c.instanceId == instanceId);
        }

        private static void AddFixedHeight(Component rect, float height)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
            element.flexibleWidth = 1;
        }

        private static void AddFixedWidth(Component rect, float width)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.minWidth = width;
        }

        private static void AddFixedWidthHeight(Component rect, float width, float height)
        {
            var element = rect.gameObject.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = rect.gameObject.AddComponent<LayoutElement>();
            }

            element.preferredWidth = width;
            element.minWidth = width;
            element.preferredHeight = height;
            element.minHeight = height;
        }

        private static void AddFlexibleWidth(Component rect)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.flexibleWidth = 1;
        }

        private static void AddResponsiveWidth(Component rect, float minWidth, float preferredWidth, float flexGrow)
        {
            var element = rect.gameObject.GetComponent<LayoutElement>();
            if (element == null)
            {
                element = rect.gameObject.AddComponent<LayoutElement>();
            }

            element.minWidth = minWidth;
            element.preferredWidth = preferredWidth;
            element.flexibleWidth = flexGrow;
        }

        private static void AddFlexibleHeight(Component rect)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.flexibleHeight = 1;
            element.flexibleWidth = 1;
        }
    }
}
