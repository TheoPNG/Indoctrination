using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.EventSystems;
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
        /// <summary>
        /// A card strip has to clear the card itself plus the padding inside it.
        /// Undersizing this clips the top of every card, which is exactly where
        /// the title sits - the whole board looked title-less because of it.
        /// </summary>
        private const float CardStripHeight = BoardCardView.Height + (UIFactory.ScrollContentPadding * 2f) + 6f;

        /// <summary>The strip, plus its header row and the spacing between them.</summary>
        private const float BattlefieldRowHeight = CardStripHeight + 30f;

        /// <summary>Height of the Play/Recycle row under a card in hand.</summary>
        private const float HandCardButtonHeight = 28f;

        /// <summary>A card in hand plus the buttons beneath it, and the gap between.</summary>
        private const float HandCardHeight = BoardCardView.Height + 4f + HandCardButtonHeight;
        /// <summary>Smallest a card may shrink to and still be recognisable.</summary>
        private const float MinCardWidth = 72f;

        /// <summary>Gap between cards in a battlefield grid.</summary>
        private const float CardGap = 6f;

        /// <summary>
        /// The least the board itself may be squeezed to. The hand is sized around
        /// whatever is left after this, so opening it can never push the board out
        /// of the window - nor be pushed out of it itself.
        /// </summary>
        private const float MinBattlefieldHeight = 250f;

        /// <summary>Diameter of a resource-picker disc.</summary>
        private const float ResourceButtonSize = 44f;

        /// <summary>The small caption above each row of cards.</summary>
        private const float RowHeaderHeight = 16f;

        /// <summary>Breathing room between one row of the board and the next.</summary>
        private const float RowGap = 8f;

        /// <summary>The one-line counters chip at the top.</summary>
        private const float StatusRowHeight = 22f;

        private const int BoardSafeInset = 10;
        private const int DraftZoneLeftInset = 12;

        /// <summary>The permanent resource HUD's column width.</summary>
        private const float ResourceHudWidth = 62f;

        /// <summary>How tall the hand tray is while collapsed - just enough to say "there is a hand here".</summary>
        private const float HandPeekHeight = 30f;

        // --------------------------------------------------------------- Panels
        private RectTransform _connectPanel;
        private RectTransform _lobbyPanel;
        private RectTransform _gameRoot;

        private InputField _addressField;
        private InputField _nameField;
        private InputField _portField;
        private Text _lobbyPlayersText;
        private Button _startGameButton;
        private Button _timerToggle;
        private bool _timersOn;

        private Text _statusText;
        private Text _timerText;
        private Text _errorText;

        private RectTransform _topBar;
        private RectTransform _battlefield;
        private RectTransform _battlefieldViewport;
        private ResourceHud _resourceHud;

        /// <summary>
        /// The popup that stands in for the old side panel: a scrim over the
        /// whole board and a centred box, shown only while something actually
        /// needs an answer. Empty phases show nothing here at all - the
        /// compounds are the only thing on screen.
        /// </summary>
        private RectTransform _popupScrim;
        private RectTransform _popupPanel;
        private RectTransform _actionViewport;
        private RectTransform _actionPanel;
        private ScrollRect _actionScroll;
        private Button _readyButton;
        private Text _readyLabel;
        private Text _waitingLabel;
        private RectTransform _handRow;
        private LayoutElement _handRowPin;
        private LayoutElement _dockPin;
        private Text _handCountLabel;
        private RectTransform _dragLayer;
        private StatBar _viewerStatBar;

        // ---------------------------------------------------------------- State
        private NetworkGameManager _subscribedManager;
        private GameView _lastView;
        private float _secondsLeft;
        private float _choiceSecondsLeft;
        private bool _handExpanded;
        private string _renderedPhase;

        /// <summary>Die faces showing this turn, for lighting up the units they wake.</summary>
        private readonly HashSet<int> _rolledThisTurn = new();

        /// <summary>Set for the one refresh that enters the Activation phase.</summary>
        private bool _pulseActivationsThisRefresh;

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

            _connectPanel = BuildConnectPanel(canvas.transform);
            _lobbyPanel = BuildLobbyPanel(canvas.transform);
            _gameRoot = BuildGameRoot(canvas.transform);
            BuildErrorLabel(canvas.transform);

            // Built last so they sit above the board they cover.
            _phaseBanner = PhaseBanner.CreateOn(canvas.transform);
            CardPreview.CreateOn(canvas.transform);

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
            camera.backgroundColor = UITheme.RitualBlack;
        }

        private void Update()
        {
            var network = NetworkManager.Singleton;
            var manager = NetworkGameManager.Instance;

            SubscribeIfNeeded(manager);
            UpdateVisibility(network, manager);
            TickTimer(manager);
        }

        private void OnDestroy()
        {
            if (_subscribedManager != null)
            {
                _subscribedManager.Changed -= Refresh;
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
            }

            _subscribedManager = manager;

            if (_subscribedManager != null)
            {
                _subscribedManager.Changed += Refresh;
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
                UITheme.RitualBlack.r, UITheme.RitualBlack.g, UITheme.RitualBlack.b, 0.72f));
            UIFactory.Stretch(panel);

            var box = UIFactory.Panel("Connect Box", panel, UITheme.SurfaceRaised);
            UITheme.Frame(box.GetComponent<Image>(), 1.25f);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(box, 420, 260);
            var layout = UIFactory.VerticalLayout(box, 12, new RectOffset(20, 20, 20, 20), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            AddFixedHeight(UIFactory.Label(
                "Title", box, "I N D O C T R I N A T I O N", 26,
                TextAnchor.MiddleCenter, UITheme.RitualGold), 40);
            AddFixedHeight(UIFactory.Label(
                "Subtitle", box, "Host a game, or join one that is already running.", 14,
                TextAnchor.MiddleCenter, UITheme.ParchmentMuted), 22);

            var addressRow = UIFactory.Group("Address Row", box);
            AddFixedHeight(addressRow, 34);
            UIFactory.HorizontalLayout(addressRow, 8, new RectOffset(0, 0, 0, 0));
            AddFixedWidth(UIFactory.Label("Address Label", addressRow, "Address", 14), 70);
            _addressField = UIFactory.TextInput("Address Field", addressRow, address);
            AddFixedWidthHeight(_addressField.GetComponent<RectTransform>(), 180, 30);
            AddFixedWidth(UIFactory.Label("Port Label", addressRow, "Port", 14), 40);
            _portField = UIFactory.TextInput("Port Field", addressRow, port.ToString());
            AddFixedWidthHeight(_portField.GetComponent<RectTransform>(), 80, 30);

            var buttonRow = UIFactory.Group("Button Row", box);
            AddFixedHeight(buttonRow, 40);
            UIFactory.HorizontalLayout(buttonRow, 12, new RectOffset(0, 0, 0, 0));
            UIFactory.ButtonWithLabel("Host Button", buttonRow, "Host", () => StartAs(host: true), width: 160, height: 36);
            UIFactory.ButtonWithLabel("Join Button", buttonRow, "Join", () => StartAs(host: false), width: 160, height: 36);

            return panel;
        }

        private void StartAs(bool host)
        {
            var network = NetworkManager.Singleton;
            if (network == null)
            {
                Debug.LogError("No NetworkManager in the scene. Run Indoctrination > Set Up Multiplayer Scene.");
                return;
            }

            address = string.IsNullOrWhiteSpace(_addressField.text) ? address : _addressField.text;
            port = ushort.TryParse(_portField.text, out var typedPort) ? typedPort : port;

            var transport = network.GetComponent<UnityTransport>();
            if (transport != null)
            {
                // 0.0.0.0 lets other machines on the network reach the host;
                // binding to the address itself would only accept local connections.
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

        // --------------------------------------------------------------- Lobby

        private RectTransform BuildLobbyPanel(Transform parent)
        {
            var panel = UIFactory.Panel("Lobby Panel", parent, new Color(
                UITheme.RitualBlack.r, UITheme.RitualBlack.g, UITheme.RitualBlack.b, 0.72f));
            UIFactory.Stretch(panel);

            var box = UIFactory.Panel("Lobby Box", panel, UITheme.SurfaceRaised);
            UITheme.Frame(box.GetComponent<Image>(), 1.25f);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(box, 420, 320);
            var layout = UIFactory.VerticalLayout(box, 10, new RectOffset(20, 20, 20, 20), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            AddFixedHeight(UIFactory.Label(
                "Title", box, "THE GATHERING", 24, TextAnchor.MiddleCenter, UITheme.RitualGold), 34);
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

            _lobbyPlayersText = UIFactory.Label("Players", box, "", 15, TextAnchor.UpperCenter);
            AddFixedHeight(_lobbyPlayersText.rectTransform, 120);

            // The host's settings. Timers are off unless a table asks for them,
            // because a clock that takes your draft pick is worse than waiting.
            _timerToggle = UIFactory.ButtonWithLabel(
                "Timers", box, "Timers: off", ToggleTimers, UITheme.ButtonQuiet, 240, 32);

            _startGameButton = UIFactory.ButtonWithLabel(
                "Start Button", box, "Start Game",
                () => NetworkGameManager.Instance?.RequestStartGameRpc(),
                width: 200, height: 40);

            var leaveButton = UIFactory.ButtonWithLabel(
                "Leave Button", box, "Leave", () => NetworkManager.Singleton?.Shutdown(),
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
                ? UITheme.Moss
                : UITheme.ButtonQuiet;

            NetworkGameManager.Instance?.RequestSetTimersRpc(_timersOn);
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
                UITheme.ParchmentMuted);
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
            _timerText = UIFactory.Label("Timer", status, "", 13, TextAnchor.MiddleRight, UITheme.RitualGold);
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

            // Bottom dock: your own stat bar, then the collapsible hand. Its
            // height is recomputed in RefreshHand every time the tray opens or
            // closes - a size fixed once at build time would either waste space
            // collapsed or clip the hand open.
            var dock = UIFactory.Group("Bottom Dock", root);
            UIFactory.VerticalLayout(dock, 4, new RectOffset(0, 0, 0, 0), controlHeight: true);
            _dockPin = dock.gameObject.AddComponent<LayoutElement>();

            var dockTop = UIFactory.Group("Dock Top", dock);
            AddFixedHeight(dockTop, 44);
            UIFactory.HorizontalLayout(dockTop, 10, new RectOffset(0, 0, 0, 0));
            dockTop.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;

            _viewerStatBar = StatBar.Create(dockTop);

            // Ready sits in the dock rather than in a popup - the one control
            // that ends a phase should never be something you have to go looking
            // for.
            _readyButton = UIFactory.ButtonWithLabel(
                "Ready", dockTop, "Ready", ToggleReady, UITheme.Moss, 130, 34);
            _readyLabel = _readyButton.GetComponentInChildren<Text>();

            _waitingLabel = UIFactory.Label("Waiting", dockTop, "", 12, TextAnchor.MiddleLeft,
                UITheme.ParchmentMuted);
            AddFlexibleWidth(_waitingLabel.rectTransform);

            // The hand tray only peeks by default, so this is the one place its
            // size is still visible without hovering over it.
            _handCountLabel = UIFactory.Label("Hand Count", dockTop, "", 12, TextAnchor.MiddleRight,
                UITheme.ParchmentMuted);
            AddFixedWidth(_handCountLabel.rectTransform, 70);

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

            // The hand is always present, never hidden outright - it just peeks a
            // sliver above the bottom edge until the pointer finds it, then
            // expands to its full playable size. Hovering off it collapses it
            // again, so the compounds get the screen back the moment you look away.
            _handRow = UIFactory.Group("Hand Row", dock);
            _handRowPin = _handRow.gameObject.AddComponent<LayoutElement>();
            _handRowPin.flexibleWidth = 1;

            var handHover = _handRow.gameObject.AddComponent<EventTrigger>();
            var handEnter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            handEnter.callback.AddListener(_ => SetHandExpanded(true));
            handHover.triggers.Add(handEnter);
            var handExit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            handExit.callback.AddListener(_ => SetHandExpanded(false));
            handHover.triggers.Add(handExit);

            // The popup that stands in for the old always-on side panel: a scrim
            // over the whole board and a centred box, built last among root's
            // children so it draws above everything else in it, and ignored by
            // root's own layout so it can freely stretch to cover the board
            // rather than becoming another row. Empty until something actually
            // needs an answer - most phases show nothing here at all.
            _popupScrim = UIFactory.Panel("Popup Scrim", root, new Color(
                UITheme.RitualBlack.r, UITheme.RitualBlack.g, UITheme.RitualBlack.b, 0.72f));
            UIFactory.Stretch(_popupScrim);
            _popupScrim.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _popupScrim.gameObject.SetActive(false);

            _popupPanel = UIFactory.Panel("Popup Panel", root, UITheme.SurfaceRaised);
            UITheme.Frame(_popupPanel.GetComponent<Image>(), 1.25f);
            _popupPanel.anchorMin = _popupPanel.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_popupPanel, 480, 460);
            _popupPanel.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            _popupPanel.gameObject.SetActive(false);

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

            return root;
        }

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

            // The hand's own row, and the board resized around it. Neither the
            // stat bars nor their animations are touched - opening a tray is not
            // a change to the game.
            RefreshHand(manager.View);
            RefreshBattlefield(manager, manager.View);
        }

        private void BuildErrorLabel(Transform parent)
        {
            var panel = UIFactory.Panel("Error Banner", parent, new Color(
                UITheme.Blood.r, UITheme.Blood.g, UITheme.Blood.b, 0.94f));
            UITheme.Frame(panel.GetComponent<Image>(), 1f, UITheme.RitualGoldSoft);
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
                // A new phase collapses the hand back down. Otherwise a hand
                // left open while reading a card would still be covering the
                // board turns later, with nobody hovering it any more to close it.
                _handExpanded = false;
                _renderedPhase = view.phase;
            }

            if (!string.Equals(previousPhase, view.phase, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(previousPhase)
                && !view.isGameOver)
            {
                _phaseBanner.Announce(view.phase, PhaseHint(view), PhaseTint(view.phase));
            }

            _statusText.text = StatusLine(view);

            // Entering Activation is the moment the board should react: the dice
            // are settled and the units they woke are about to resolve.
            var enteringActivation = view.phase == nameof(TurnPhase.Activation)
                                     && previousPhase != nameof(TurnPhase.Activation);

            _pulseActivationsThisRefresh = enteringActivation;
            _somethingHitThisRefresh = false;

            if (enteringActivation)
            {
                _rolledThisTurn.Clear();
                foreach (var player in view.players.Where(p => p.isAlive && p.primaryDie > 0))
                {
                    _rolledThisTurn.Add(player.primaryDie);
                }

            }

            RefreshTopBar(view);
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
            nameof(TurnPhase.Draft) => new Color(0.58f, 0.55f, 0.82f),
            nameof(TurnPhase.Rolling) => UITheme.Parchment,
            nameof(TurnPhase.Activation) => new Color(0.82f, 0.34f, 0.38f),
            nameof(TurnPhase.Resource) => new Color(0.43f, 0.68f, 0.47f),
            nameof(TurnPhase.Buy) => UITheme.RitualGold,
            _ => UITheme.Parchment
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
                _statBars[player.playerId].Populate(player, isViewer: false);
            }
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
            var pickable = you is { isAlive: true }
                           && !view.isGameOver
                           && view.phase == nameof(TurnPhase.Resource)
                           && !you.collectedResources;

            if (!pickable)
            {
                _pendingResources.Clear();
            }

            _resourceHud.Populate(you, pickable, color =>
            {
                BoardEffects.Instance.Pop(_resourceHud.Pip(color));
                _resourceHud.ShowResourceGain(color);
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
                        ActionLabel = "Choose this card"
                    }
                    : new PlannedRow
                    {
                        Label = $"Draft Zone ({view.draftZone.Length})",
                        Cards = view.draftZone,
                        IsClickable = card => isMyPick && IsDraftable(view, card),
                        OnClick = card => manager.RequestDraftRpc(card.instanceId),
                        TagFor = card => DraftMarkTag(view, card),
                        ActionLabel = "Draft this card"
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

        /// <summary>One row of the board, planned before anything is built.</summary>
        private class PlannedRow
        {
            public string Label;
            public CardView[] Cards;
            public Func<CardView, bool> IsClickable;
            public Action<CardView> OnClick;
            public Func<CardView, string> TagFor;
            public string ActionLabel;

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
            var height = Mathf.Max(200f, _battlefieldViewport.rect.height - 8f);

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
                new Color(0.75f, 0.75f, 0.8f));
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
                    var cellScreenPos = RectTransformUtility.WorldToScreenPoint(null, unitCells[i].position);
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
                RegisterForActivationPulse(cardView);

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
                BoardEffects.Instance.FadeIn(cell.gameObject, delay: dealt * 0.025f);
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
            else if (card.definitionId == CardIds.TryAgain
                     && view.phase == nameof(TurnPhase.Rolling) && view.diceRolled)
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
        private void MarkIfDueToActivate(BoardCardView card, GameView view)
        {
            if (view.phase != nameof(TurnPhase.Rolling) || !view.diceRolled || card.Definition == null)
            {
                return;
            }

            var faces = view.players.Where(p => p.isAlive && p.primaryDie > 0).Select(p => p.primaryDie).ToHashSet();

            if (card.Definition.Type == CardType.Unit && card.Definition.ActivationNumbers.Any(faces.Contains))
            {
                card.SetDueToActivate(BoardArt.ColorOfCategory(
                    CardEffects.CategoryFor(card.Definition.Id, card.Definition.ActivationNumbers.First(faces.Contains))));
            }
        }

        /// <summary>
        /// Lights up a card if the dice just woke it. Only during Activation, and
        /// only once per entry into that phase, so the board reacts to the roll
        /// rather than flashing on every incidental refresh.
        /// </summary>
        private void RegisterForActivationPulse(BoardCardView card)
        {
            if (!_pulseActivationsThisRefresh || card.Definition == null)
            {
                return;
            }

            if (card.Definition.Type != CardType.Unit
                || !card.Definition.ActivationNumbers.Any(_rolledThisTurn.Contains))
            {
                return;
            }

            var face = card.Definition.ActivationNumbers.First(_rolledThisTurn.Contains);
            var category = CardEffects.CategoryFor(card.Definition.Id, face);

            BoardEffects.Instance.PulseCard(
                (RectTransform)card.transform, BoardArt.ColorOfCategory(category));

            // Only a blow actually landing is worth shaking the board for.
            if (category == ActivationCategory.Damage)
            {
                _somethingHitThisRefresh = true;
            }
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
            _handCountLabel.text = you == null ? "" : $"Hand: {count}";

            _viewerStatBar.gameObject.SetActive(you != null);
            if (you != null)
            {
                _viewerStatBar.Populate(you, isViewer: true);
            }

            UIFactory.DestroyChildren(_handRow);

            if (you == null || count == 0)
            {
                _handRowPin.preferredHeight = 0f;
                _handRowPin.minHeight = 0f;
                _dockPin.preferredHeight = DockTopHeight;
                return;
            }

            // Never hidden outright - it peeks until the pointer finds it, then
            // opens to its full playable size. Only the expanded state offers
            // buttons or dragging; a card peeking above the fold is there to be
            // recognised, not acted on by accident.
            if (!_handExpanded)
            {
                BuildHandPeek(you);
                return;
            }

            var canBuy = view.phase == nameof(TurnPhase.Buy) && !view.hasPendingChoice;

            // Sized so a full hand fits across without scrolling. The hand limit
            // is what makes that possible: seven is the widest it can ever be.
            // Sized against both directions. Width alone was not enough: during Buy
            // the cards carry a row of buttons underneath, and on a short window
            // the whole tray ran past the bottom of the screen - which is what was
            // cutting the hand off.
            var across = Mathf.Max(240f, _gameRoot.rect.width - 40f);
            var widthAllows = (across - ((GameSettings.HandLimit - 1) * CardGap) - 24f)
                              / GameSettings.HandLimit;

            var chrome = (UIFactory.ScrollContentPadding * 2f) + 6f
                         + (canBuy ? HandCardButtonHeight + 4f : 0f);

            var heightForTray = _gameRoot.rect.height
                                - StatusRowHeight
                                - (StatBar.BarHeight + (UIFactory.ScrollContentPadding * 2f) + 4f)
                                - MinBattlefieldHeight
                                - DockTopHeight
                                - (BoardSafeInset * 2f) - 24f;

            var heightAllows = (Mathf.Max(80f, heightForTray) - chrome)
                               * (BoardCardView.Width / BoardCardView.Height);

            var handCardWidth = Mathf.Clamp(
                Mathf.Min(widthAllows, heightAllows), MinCardWidth, BoardCardView.Width);

            var handCardHeight = handCardWidth * (BoardCardView.Height / BoardCardView.Width);
            var handStripHeight = handCardHeight + chrome;

            _handRowPin.preferredHeight = handStripHeight;
            _handRowPin.minHeight = handStripHeight;
            _dockPin.preferredHeight = DockTopHeight + handStripHeight + 4f;

            // Sized to the cards it holds and no wider, so the board stays visible
            // either side of it. It sits just above the dock.
            var content = UIFactory.HorizontalScroll("Hand Scroll", _handRow, handStripHeight);
            UIFactory.Stretch(UIFactory.Child(_handRow, "Hand Scroll"));

            // A card preview or a Ritual covers the whole board, so it reclaims
            // the top after anything else has been added to the canvas.
            CardPreview.BringToFront();

            foreach (var card in you.hand)
            {
                if (!canBuy)
                {
                    var idleSlot = UIFactory.Group("Card Slot", content);
                    UIFactory.SetSize(idleSlot, handCardWidth, handCardHeight);
                    var idlePin = idleSlot.gameObject.AddComponent<LayoutElement>();
                    idlePin.minWidth = idlePin.preferredWidth = handCardWidth;
                    idlePin.minHeight = idlePin.preferredHeight = handCardHeight;

                    var idle = BoardCardView.Create(idleSlot);
                    var idleRect = (RectTransform)idle.transform;
                    idleRect.anchorMin = idleRect.anchorMax = new Vector2(0.5f, 0.5f);
                    idleRect.pivot = new Vector2(0.5f, 0.5f);

                    idle.Populate(card, null, null);
                    idle.ScaleTo(handCardWidth);
                    continue;
                }

                var wrapper = UIFactory.Group("Hand Card", content);
                var wrapperLayout = UIFactory.VerticalLayout(wrapper, 4, new RectOffset(0, 0, 0, 0), controlHeight: true);
                wrapperLayout.childAlignment = TextAnchor.UpperLeft;

                // The strip positions its children without resizing them, so the
                // wrapper's own rect has to be the right size up front - exactly
                // as BoardCardView sizes itself. A LayoutElement alone is ignored
                // here, which left the buttons laid out below the strip that
                // clips them: present, but permanently out of the player's reach.
                UIFactory.SetSize(wrapper, handCardWidth, handCardHeight + 4f + HandCardButtonHeight);
                wrapperLayout.childControlHeight = false;

                var wrapperPin = wrapper.gameObject.AddComponent<LayoutElement>();
                wrapperPin.preferredWidth = handCardWidth;
                wrapperPin.minWidth = handCardWidth;
                wrapperPin.preferredHeight = handCardHeight + 4f + HandCardButtonHeight;
                wrapperPin.minHeight = handCardHeight + 4f + HandCardButtonHeight;

                // ScaleTo shrinks the card visually, but its LayoutElement still
                // reports the full 250 to any layout that asks. The card therefore
                // gets a slot the size it will actually occupy, and sits inside it.
                var slot = UIFactory.Group("Card Slot", wrapper);
                UIFactory.SetSize(slot, handCardWidth, handCardHeight);

                var handCard = BoardCardView.Create(slot);
                var handCardRect = (RectTransform)handCard.transform;
                handCardRect.anchorMin = handCardRect.anchorMax = new Vector2(0.5f, 0.5f);
                handCardRect.pivot = new Vector2(0.5f, 0.5f);

                handCard.Populate(card, null, null);
                handCard.ScaleTo(handCardWidth);

                var instanceId = card.instanceId;

                // A card only lights up, and only picks up, once it is actually
                // playable - dragging an unaffordable card onto the compounds
                // would just be refused, so it never invites the attempt.
                handCard.SetAffordable(card.canAfford);

                if (card.canAfford)
                {
                    var ghostTag = (string)null;
                    var ghostWidth = handCardWidth;
                    var handle = handCard.gameObject.AddComponent<DragHandle>();
                    handle.DragLayer = _dragLayer;
                    handle.GhostFactory = () => DragHandle.CardGhost(card, ghostTag, ghostWidth);
                    handle.OnDropped = eventData =>
                    {
                        if (RectTransformUtility.RectangleContainsScreenPoint(
                                _battlefieldViewport, eventData.position, eventData.pressEventCamera))
                        {
                            NetworkGameManager.Instance?.RequestBuyRpc(instanceId);
                        }
                    };
                }

                var buttons = UIFactory.Group("Buttons", wrapper);
                UIFactory.SetSize(buttons, handCardWidth, HandCardButtonHeight);
                UIFactory.HorizontalLayout(buttons, 4, new RectOffset(0, 0, 0, 0));
                UIFactory.ButtonWithLabel("Recycle", buttons, "Recycle",
                    () => NetworkGameManager.Instance?.RequestRecycleRpc(instanceId),
                    new Color(0.39f, 0.27f, 0.11f), handCardWidth, HandCardButtonHeight);
            }
        }

        /// <summary>
        /// The hand collapsed to a sliver above the bottom edge: small enough to
        /// stay out of the way, present enough to say what is in your hand and
        /// invite the hover that opens it.
        /// </summary>
        private void BuildHandPeek(PlayerView you)
        {
            var peekCardHeight = HandPeekHeight + 6f;
            var peekCardWidth = peekCardHeight * (BoardCardView.Width / BoardCardView.Height);

            _handRowPin.preferredHeight = HandPeekHeight;
            _handRowPin.minHeight = HandPeekHeight;
            _dockPin.preferredHeight = DockTopHeight + HandPeekHeight + 4f;

            var content = UIFactory.HorizontalScroll("Hand Scroll", _handRow, HandPeekHeight);
            UIFactory.Stretch(UIFactory.Child(_handRow, "Hand Scroll"));

            foreach (var card in you.hand)
            {
                var slot = UIFactory.Group("Card Slot", content);
                UIFactory.SetSize(slot, peekCardWidth, peekCardHeight);
                var pin = slot.gameObject.AddComponent<LayoutElement>();
                pin.minWidth = pin.preferredWidth = peekCardWidth;
                pin.minHeight = pin.preferredHeight = peekCardHeight;

                var peek = BoardCardView.Create(slot);
                var rect = (RectTransform)peek.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                peek.Populate(card, null, null);
                peek.ScaleTo(peekCardWidth);
            }
        }

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

            var show = DecidePopup(manager, view);

            _popupPanel.gameObject.SetActive(show);
            _popupScrim.gameObject.SetActive(show);
        }

        private bool DecidePopup(NetworkGameManager manager, GameView view)
        {
            if (view.isGameOver)
            {
                RenderGameOver(manager, view);
                return true;
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
                ? UITheme.ParchmentMuted
                : youWon
                    ? new Color(0.48f, 0.72f, 0.46f)
                    : new Color(0.78f, 0.31f, 0.36f);
            SetRowHeight(banner.rectTransform, 48);

            var subtitle = ActionLabel(GameOverHeadline(view), 15);
            subtitle.alignment = TextAnchor.MiddleCenter;
            subtitle.color = UITheme.ParchmentMuted;
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
                    () => manager.RequestPlayAgainRpc(), UITheme.Moss, ActionButtonWidth(), 44);
            }
            else
            {
                ActionLabel("Waiting for the host to start another game.", 13);
            }

            UIFactory.ButtonWithLabel("Leave", _actionPanel, "Leave",
                () => NetworkManager.Singleton?.Shutdown(), UITheme.Blood, ActionButtonWidth(), 34);
        }

        /// <summary>One leader's final line: name, and their two tracks as bars.</summary>
        private void BuildFinalStanding(PlayerView player, bool won, bool isViewer)
        {
            var row = UIFactory.Panel($"Standing {player.playerId}", _actionPanel,
                won ? new Color(0.16f, 0.25f, 0.17f, 0.94f) : UITheme.SurfaceSoft);
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
                     UITheme.RitualGold);
            FinalBar(row, "Health", player.health, GameSettings.MaxHealth,
                     new Color(0.68f, 0.15f, 0.20f));
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
                UIFactory.ButtonWithLabel(
                    "Roll", _actionPanel, "ROLL DIE", () => manager.RequestRollRpc(),
                    UITheme.Moss, width: ActionButtonWidth(), height: 54);
                return true;
            }

            if (!view.diceRolled || view.highRollResourceClaimed)
            {
                return false;
            }

            var highRoller = HighestUniqueRoller(view);
            if (highRoller < 0 || highRoller != view.viewerPlayerId)
            {
                return false;
            }

            ActionLabel("Highest roll - take one:", 16);
            RenderColorButtons(color => manager.RequestClaimHighRollResourceRpc((int)color));
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
                _resignButton.targetGraphic.color = new Color(0.62f, 0.10f, 0.15f);
                return;
            }

            _resignArmed = false;
            NetworkGameManager.Instance?.RequestResignRpc();
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
                ? UITheme.Moss
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
                ? new Color(0.62f, 0.10f, 0.15f)
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
            _readyLabel.color = owes == null ? UITheme.Parchment : new Color(
                UITheme.Parchment.r, UITheme.Parchment.g, UITheme.Parchment.b, 0.45f);

            _readyButton.targetGraphic.color = owes != null
                ? UITheme.ButtonQuiet
                : you.isReady
                    ? new Color(0.39f, 0.27f, 0.11f)
                    : UITheme.Moss;

            BoardEffects.Instance.SetPulsing(_readyButton.targetGraphic, actionable);

            var waitingOn = view.players.Where(p => p.isAlive && !p.isReady).Select(p => p.name).ToList();
            _waitingLabel.text = owes ?? (waitingOn.Count > 0
                ? $"Waiting on {string.Join(", ", waitingOn)}"
                : "Everyone ready.");
        }

        // -------------------------------------------------------- Pending choice

        private void RenderPendingChoice(NetworkGameManager manager, GameView view)
        {
            var choice = view.pendingChoice;

            // The card behind the question, shown at full size rather than only
            // described - a popup asking what to do about a card should let you
            // look at it.
            if (!string.IsNullOrEmpty(view.resolvingCardId)
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

            if (!string.IsNullOrEmpty(view.resolvingDescription))
            {
                ActionLabel(view.resolvingDescription, 13);
            }

            ActionLabel(choice.prompt);

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
                        UITheme.Moss, 90, 32);
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

        private static Color ColorSwatch(ResourceColor color)
        {
            return color switch
            {
                ResourceColor.Red => new Color(0.6f, 0.2f, 0.2f),
                ResourceColor.Green => new Color(0.2f, 0.5f, 0.25f),
                ResourceColor.Blue => new Color(0.2f, 0.35f, 0.6f),
                ResourceColor.Yellow => new Color(0.55f, 0.45f, 0.15f),
                _ => new Color(0.3f, 0.3f, 0.3f)
            };
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
            var element = rect.gameObject.GetComponent<LayoutElement>() ?? rect.gameObject.AddComponent<LayoutElement>();
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
            var element = rect.gameObject.GetComponent<LayoutElement>() ?? rect.gameObject.AddComponent<LayoutElement>();
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
