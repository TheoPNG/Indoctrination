using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
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

        /// <summary>Diameter of a resource-picker disc.</summary>
        private const float ResourceButtonSize = 44f;

        /// <summary>The small caption above each row of cards.</summary>
        private const float RowHeaderHeight = 16f;

        /// <summary>Breathing room between one row of the board and the next.</summary>
        private const float RowGap = 8f;

        private const int BoardSafeInset = 14;
        private const int DraftZoneLeftInset = 12;

        // --------------------------------------------------------------- Panels
        private RectTransform _connectPanel;
        private RectTransform _lobbyPanel;
        private RectTransform _gameRoot;

        private InputField _addressField;
        private InputField _portField;
        private Text _lobbyPlayersText;
        private Button _startGameButton;

        private Text _statusText;
        private Text _timerText;
        private Text _errorText;

        private RectTransform _topBar;
        private RectTransform _battlefield;
        private RectTransform _actionViewport;
        private RectTransform _actionPanel;
        private ScrollRect _actionScroll;
        private Button _readyButton;
        private Text _readyLabel;
        private Text _waitingLabel;
        private RectTransform _handRow;
        private LayoutElement _dockPin;
        private Button _handToggleButton;
        private Text _handToggleLabel;
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

            SetUpOverheadCamera();

            var canvas = UIFactory.CreateCanvas("Board Canvas");

            _connectPanel = BuildConnectPanel(canvas.transform);
            _lobbyPanel = BuildLobbyPanel(canvas.transform);
            _gameRoot = BuildGameRoot(canvas.transform);
            BuildErrorLabel(canvas.transform);

            // Built last so it sits above the board it covers.
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
            camera.backgroundColor = new Color(0.07f, 0.13f, 0.09f);
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
                _timerText.text = view.isGameOver
                    ? ""
                    : view.hasPendingChoice
                        ? $"{_choiceSecondsLeft:0}s until the card decides for itself"
                        : view.phase == nameof(TurnPhase.Draft)
                            ? ""
                            : $"{_secondsLeft:0}s until phase advances";
            }
        }

        // ------------------------------------------------------------- Connect

        private RectTransform BuildConnectPanel(Transform parent)
        {
            var panel = UIFactory.Panel("Connect Panel", parent, new Color(0.08f, 0.1f, 0.09f, 0.95f));
            UIFactory.Stretch(panel);

            var box = UIFactory.Group("Connect Box", panel);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(box, 420, 260);
            var layout = UIFactory.VerticalLayout(box, 12, new RectOffset(20, 20, 20, 20), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            AddFixedHeight(UIFactory.Label("Title", box, "INDOCTRINATION", 28, TextAnchor.MiddleCenter), 40);
            AddFixedHeight(UIFactory.Label(
                "Subtitle", box, "Host a game, or join one that is already running.", 14,
                TextAnchor.MiddleCenter, new Color(0.8f, 0.8f, 0.8f)), 22);

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
            var panel = UIFactory.Panel("Lobby Panel", parent, new Color(0.08f, 0.1f, 0.09f, 0.95f));
            UIFactory.Stretch(panel);

            var box = UIFactory.Group("Lobby Box", panel);
            box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(box, 420, 320);
            var layout = UIFactory.VerticalLayout(box, 10, new RectOffset(20, 20, 20, 20), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            AddFixedHeight(UIFactory.Label("Title", box, "Waiting to start", 24, TextAnchor.MiddleCenter), 34);
            _lobbyPlayersText = UIFactory.Label("Players", box, "", 15, TextAnchor.UpperCenter);
            AddFixedHeight(_lobbyPlayersText.rectTransform, 140);

            _startGameButton = UIFactory.ButtonWithLabel(
                "Start Button", box, "Start Game",
                () => NetworkGameManager.Instance?.RequestStartGameRpc(),
                width: 200, height: 40);

            var leaveButton = UIFactory.ButtonWithLabel(
                "Leave Button", box, "Leave", () => NetworkManager.Singleton?.Shutdown(),
                new Color(0.4f, 0.2f, 0.2f), 200, 32);
            leaveButton.gameObject.name = "Leave Button";

            return panel;
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
            AddFixedHeight(status, 28);
            UIFactory.HorizontalLayout(status, 8, new RectOffset(4, 4, 0, 0), controlWidth: true);
            _statusText = UIFactory.Label("Status", status, "", 15, TextAnchor.MiddleLeft);
            AddFlexibleWidth(_statusText.rectTransform);
            _timerText = UIFactory.Label("Timer", status, "", 13, TextAnchor.MiddleRight, new Color(0.8f, 0.8f, 0.6f));
            AddResponsiveWidth(_timerText.rectTransform, 150, 230, 0);

            // Opponents across the top of the board. This row scrolls rather than
            // shrinking or clipping stat bars when several players share a small
            // Multiplayer Player window.
            _topBar = UIFactory.HorizontalScroll(
                "Top Bar", root, StatBar.BarHeight + (UIFactory.ScrollContentPadding * 2f) + 4f);
            _topBar.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;

            // Battlefield + action panel share the flexible middle area. Both are
            // force-expanded vertically: neither reports a preferred height of its
            // own, so without this the layout would size them to nothing.
            var middle = UIFactory.Group("Middle Area", root);
            AddFlexibleHeight(middle);
            var middleLayout = UIFactory.HorizontalLayout(
                middle, 10, new RectOffset(0, 0, 0, 0),
                controlWidth: true, controlHeight: true);
            middleLayout.childForceExpandHeight = true;

            // Battlefield is a vertical stack of rows, not a horizontal strip, so
            // it gets its own scroll rect rather than reusing the horizontal helper.
            var battlefieldPanel = UIFactory.Panel("Battlefield Panel", middle, new Color(1, 1, 1, 0.04f));
            AddResponsiveWidth(battlefieldPanel, 360, 760, 3);
            var battlefieldViewport = UIFactory.Panel("Battlefield Viewport", battlefieldPanel, Color.clear);
            battlefieldViewport.gameObject.AddComponent<RectMask2D>();
            UIFactory.Stretch(battlefieldViewport);
            var battlefieldScroll = battlefieldPanel.gameObject.AddComponent<ScrollRect>();
            battlefieldScroll.horizontal = false;
            battlefieldScroll.vertical = true;
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

            // This behaves like a flexbox side column: it has a readable minimum
            // width, receives a share of extra width, and independently scrolls
            // vertically when the window is short. Phase controls can therefore
            // never disappear underneath the hand dock.
            var actionShell = UIFactory.Panel("Action Panel", middle, new Color(0.12f, 0.12f, 0.16f, 0.9f));
            AddResponsiveWidth(actionShell, 280, 340, 1);

            _actionViewport = UIFactory.Panel("Action Viewport", actionShell, Color.clear);
            _actionViewport.gameObject.AddComponent<RectMask2D>();
            UIFactory.Stretch(_actionViewport);

            _actionScroll = actionShell.gameObject.AddComponent<ScrollRect>();
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
                _actionPanel, 8, new RectOffset(12, 12, 12, 12), controlHeight: true);
            actionLayout.childAlignment = TextAnchor.UpperLeft;
            UIFactory.FitToContent(
                _actionPanel,
                ContentSizeFitter.FitMode.Unconstrained,
                ContentSizeFitter.FitMode.PreferredSize);
            _actionScroll.viewport = _actionViewport;
            _actionScroll.content = _actionPanel;

            // Bottom dock: your own stat bar, then the collapsible hand. Its
            // height is recomputed in RefreshHand every time the tray opens or
            // closes - a size fixed once at build time would either waste space
            // collapsed or clip the hand open.
            var dock = UIFactory.Group("Bottom Dock", root);
            UIFactory.VerticalLayout(dock, 4, new RectOffset(0, 0, 0, 0), controlHeight: true);
            _dockPin = dock.gameObject.AddComponent<LayoutElement>();

            var dockTop = UIFactory.Group("Dock Top", dock);
            AddFixedHeight(dockTop, 82);
            UIFactory.HorizontalLayout(dockTop, 10, new RectOffset(0, 0, 0, 0));
            dockTop.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleLeft;

            _viewerStatBar = StatBar.Create(dockTop);

            _handToggleButton = UIFactory.ButtonWithLabel(
                "Hand Toggle", dockTop, "Hand", ToggleHand, new Color(0.2f, 0.2f, 0.26f), 190, 36);
            _handToggleLabel = _handToggleButton.GetComponentInChildren<Text>();

            // Ready sits in the dock rather than the action panel. The panel
            // scrolls, so the one control that ends a phase could be pushed out of
            // sight behind the hand tray exactly when it was needed.
            _readyButton = UIFactory.ButtonWithLabel(
                "Ready", dockTop, "Ready", ToggleReady, new Color(0.2f, 0.4f, 0.2f), 190, 36);
            _readyLabel = _readyButton.GetComponentInChildren<Text>();

            _waitingLabel = UIFactory.Label("Waiting", dockTop, "", 13, TextAnchor.MiddleLeft,
                new Color(0.8f, 0.8f, 0.7f));
            AddFlexibleWidth(_waitingLabel.rectTransform);

            // The discard is public information and Rituals fly into it, so it is
            // a real place on the board rather than a number in the status line.
            _discardButton = UIFactory.ButtonWithLabel(
                "Discard", dockTop, "Discard", ShowDiscard, new Color(0.24f, 0.2f, 0.26f), 130, 36);

            // Pips in flight are drawn above everything, so one crossing the board
            // is never hidden behind a panel it passes over.
            var flightLayer = UIFactory.Group("Flight Layer", root.parent);
            UIFactory.Stretch(flightLayer);
            flightLayer.SetAsLastSibling();
            BoardEffects.Instance.SetFlightLayer(flightLayer);

            // The hand floats over the board instead of living in the dock. It
            // used to be a full-width opaque block that shoved the battlefield
            // upward every time it opened; now it is just the cards, sitting above
            // the dock, and the board underneath stays exactly where it was.
            _handRow = UIFactory.Group("Hand Row", root.parent);
            _handRow.anchorMin = new Vector2(0.5f, 0f);
            _handRow.anchorMax = new Vector2(0.5f, 0f);
            _handRow.pivot = new Vector2(0.5f, 0f);

            return root;
        }

        private void ToggleHand()
        {
            _handExpanded = !_handExpanded;
            var manager = NetworkGameManager.Instance;
            if (manager?.View != null)
            {
                RefreshHand(manager.View);
            }
        }

        private void BuildErrorLabel(Transform parent)
        {
            var panel = UIFactory.Panel("Error Banner", parent, new Color(0.5f, 0.1f, 0.1f, 0.9f));
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
                // Keep the battlefield and the phase action usable by default.
                // The hand opens automatically only when it becomes actionable.
                _handExpanded = view.phase == nameof(TurnPhase.Buy);
                _renderedPhase = view.phase;
            }

            _statusText.text = view.isGameOver
                ? $"<b>{GameOverHeadline(view)}</b>"
                : $"{view.phase}   draft {view.draftNumber}, turn {view.turnInRound}/{GameSettings.TurnsPerRound}   " +
                  $"deck {view.deckCount}, discard {view.discardCount}";

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
            RefreshBattlefield(manager, view);
            RefreshActionPanel(manager, view);
            RefreshReadyControl(view);
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

        private void RefreshTopBar(GameView view)
        {
            UIFactory.DestroyChildren(_topBar);
            _statBars.Clear();

            foreach (var player in view.players.Where(p => p.playerId != view.viewerPlayerId))
            {
                var bar = StatBar.Create(_topBar);
                bar.Populate(player, isViewer: false);
                _statBars[player.playerId] = bar;
            }
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
                        delay: i * 0.07f, size: 22f);
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

            if (view.phase == nameof(TurnPhase.Draft))
            {
                var isMyPick = view.currentDrafterId == view.viewerPlayerId;
                rows.Add(new PlannedRow
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
                    Cards = OrderedForBoard(you.compound)
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
                BuildCardRow(_battlefield, row, cardWidth, view);
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
        }

        /// <summary>
        /// Units in the order the dice will wake them, so a glance down a
        /// compound reads as "what happens on a 1, then a 2, then a 3". Blessings
        /// have no number and sit at the end.
        /// </summary>
        private static CardView[] OrderedForBoard(CardView[] cards)
        {
            return cards
                .OrderBy(card =>
                {
                    var definition = DefinitionOf(card);
                    if (definition == null || definition.ActivationNumbers.Count == 0)
                    {
                        return int.MaxValue;
                    }

                    return definition.ActivationNumbers.Min();
                })
                .ThenBy(card => DefinitionOf(card)?.Title ?? card.definitionId)
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
            var height = Mathf.Max(200f, _actionViewport.rect.height - 8f);

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
            Transform parent, PlannedRow plan, float cardWidth, GameView view, float availableWidth = 0f)
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

            foreach (var card in plan.Cards)
            {
                var cell = UIFactory.Group("Cell", grid);
                var cardView = BoardCardView.Create(cell);
                var cardRect = (RectTransform)cardView.transform;
                cardRect.anchorMin = cardRect.anchorMax = new Vector2(0.5f, 0.5f);
                cardRect.pivot = new Vector2(0.5f, 0.5f);

                var clickable = plan.IsClickable != null && plan.IsClickable(card);
                cardView.Populate(card, plan.TagFor?.Invoke(card), clickable ? () => plan.OnClick(card) : null);
                cardView.SetAction(clickable ? plan.ActionLabel : null,
                                   clickable ? () => plan.OnClick(card) : null);
                cardView.ScaleTo(cardWidth);

                MarkIfDueToActivate(cardView, view);
                RegisterForActivationPulse(cardView);
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
            _handToggleLabel.text = $"Hand ({count})  {(_handExpanded ? "[hide]" : "[show]")}";

            _viewerStatBar.gameObject.SetActive(you != null);
            if (you != null)
            {
                _viewerStatBar.Populate(you, isViewer: true);
            }

            UIFactory.DestroyChildren(_handRow);
            var expanded = _handExpanded && you != null;
            _handRow.gameObject.SetActive(expanded);

            // The dock keeps its own height whatever the hand is doing, so opening
            // the hand never resizes the board behind it.
            _dockPin.preferredHeight = DockTopHeight;

            if (!expanded)
            {
                return;
            }

            var canBuy = view.phase == nameof(TurnPhase.Buy) && !view.hasPendingChoice;
            var handStripHeight = canBuy
                ? HandCardHeight + (UIFactory.ScrollContentPadding * 2f) + 6f
                : CardStripHeight;

            // Sized to the cards it holds and no wider, so the board stays visible
            // either side of it. It sits just above the dock.
            // Sized so a full hand fits across without scrolling. The limit is
            // what makes that possible: seven is the widest a hand can ever be.
            var widest = Mathf.Min(_gameRoot.rect.width - 60f, 1100f);
            var handCardWidth = Mathf.Min(
                BoardCardView.Width,
                (widest - ((GameSettings.HandLimit - 1) * CardGap)) / GameSettings.HandLimit);

            var handWidth = Mathf.Min(
                _gameRoot.rect.width - 40f,
                (you.hand.Length * (handCardWidth + CardGap)) + 24f);

            UIFactory.SetSize(_handRow, Mathf.Max(220f, handWidth), handStripHeight);
            _handRow.anchoredPosition = new Vector2(0f, DockTopHeight + BoardSafeInset);
            _handRow.SetAsLastSibling();

            var content = UIFactory.HorizontalScroll("Hand Scroll", _handRow, handStripHeight);
            UIFactory.Stretch(UIFactory.Child(_handRow, "Hand Scroll"));

            // The hand has just taken the top of the canvas. A Ritual or a card
            // preview covers the whole board and has to sit over it, so it
            // reclaims the top here rather than being buried by the tray.
            CardPreview.BringToFront();

            foreach (var card in you.hand)
            {
                if (!canBuy)
                {
                    var idle = BoardCardView.Create(content);
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
                UIFactory.SetSize(wrapper, BoardCardView.Width, HandCardHeight);

                var wrapperPin = wrapper.gameObject.AddComponent<LayoutElement>();
                wrapperPin.preferredWidth = BoardCardView.Width;
                wrapperPin.minWidth = BoardCardView.Width;
                wrapperPin.preferredHeight = HandCardHeight;
                wrapperPin.minHeight = HandCardHeight;

                var handCard = BoardCardView.Create(wrapper);
                handCard.Populate(card, null, null);
                handCard.ScaleTo(handCardWidth);

                // Only worth marking when playing is actually the move on offer.
                if (canBuy)
                {
                    handCard.SetAffordable(card.canAfford);
                }

                // The same action as the button beneath it, so a card read in the
                // preview can be played without closing it first.
                var playing = card.instanceId;
                handCard.SetAction("Play", () => NetworkGameManager.Instance?.RequestBuyRpc(playing));

                var buttons = UIFactory.Group("Buttons", wrapper);
                AddFixedHeight(buttons, HandCardButtonHeight);
                UIFactory.HorizontalLayout(buttons, 4, new RectOffset(0, 0, 0, 0));
                var instanceId = card.instanceId;
                UIFactory.ButtonWithLabel("Play", buttons, "Play",
                    () => NetworkGameManager.Instance?.RequestBuyRpc(instanceId),
                    new Color(0.2f, 0.4f, 0.2f), BoardCardView.Width / 2 - 3, HandCardButtonHeight);
                UIFactory.ButtonWithLabel("Recycle", buttons, "Recycle",
                    () => NetworkGameManager.Instance?.RequestRecycleRpc(instanceId),
                    new Color(0.4f, 0.35f, 0.15f), BoardCardView.Width / 2 - 3, HandCardButtonHeight);
            }
        }

        // ------------------------------------------------------- Action panel

        /// <summary>How the game ended, said plainly enough to read at a glance.</summary>
        private static string GameOverHeadline(GameView view)
        {
            if (view.isDraw)
            {
                return "Everyone is out. The game is a draw.";
            }

            var winner = FindPlayer(view, view.winnerPlayerId);
            var name = winner?.name ?? "Somebody";

            return winner != null && winner.followers >= GameSettings.FollowersToWin
                ? $"{name} wins with {winner.followers} followers."
                : $"{name} wins - last leader standing.";
        }

        private void RefreshActionPanel(NetworkGameManager manager, GameView view)
        {
            UIFactory.DestroyChildren(_actionPanel);

            if (view.isGameOver)
            {
                RenderGameOver(manager, view);
                return;
            }

            if (view.hasPendingChoice)
            {
                RenderPendingChoice(manager, view);
                return;
            }

            switch (view.phase)
            {
                case nameof(TurnPhase.Draft):
                    RenderDraftHint(view);
                    break;
                case nameof(TurnPhase.Rolling):
                    RenderRolling(manager, view);
                    break;
                case nameof(TurnPhase.Activation):
                    RenderActivation(view);
                    break;
                case nameof(TurnPhase.Resource):
                    RenderResource(manager, view);
                    break;
                case nameof(TurnPhase.Buy):
                    ActionLabel("Play or recycle from your hand.", 16);
                    break;
            }

            RenderCardActions(manager, view);
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
                ? new Color(0.8f, 0.8f, 0.85f)
                : youWon
                    ? new Color(0.45f, 0.85f, 0.45f)
                    : new Color(0.9f, 0.4f, 0.4f);
            SetRowHeight(banner.rectTransform, 48);

            var subtitle = ActionLabel(GameOverHeadline(view), 15);
            subtitle.alignment = TextAnchor.MiddleCenter;
            subtitle.color = new Color(0.85f, 0.85f, 0.85f);
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
                    () => manager.RequestPlayAgainRpc(), new Color(0.2f, 0.45f, 0.22f), 220, 44);
            }
            else
            {
                ActionLabel("Waiting for the host to start another game.", 13);
            }

            UIFactory.ButtonWithLabel("Leave", _actionPanel, "Leave",
                () => NetworkManager.Singleton?.Shutdown(), new Color(0.4f, 0.2f, 0.2f), 220, 34);
        }

        /// <summary>One leader's final line: name, and their two tracks as bars.</summary>
        private void BuildFinalStanding(PlayerView player, bool won, bool isViewer)
        {
            var row = UIFactory.Panel($"Standing {player.playerId}", _actionPanel,
                won ? new Color(0.18f, 0.28f, 0.18f, 0.9f) : new Color(1f, 1f, 1f, 0.05f));
            SetRowHeight(row, 62);

            var layout = UIFactory.VerticalLayout(row, 2, new RectOffset(8, 8, 5, 5), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandWidth = true;

            var name = UIFactory.Label("Name", row, 
                $"{(won ? "★ " : "")}{player.name}{(isViewer ? " (you)" : "")}"
                + $"{(player.isAlive ? "" : "  -  out")}",
                14, TextAnchor.MiddleLeft);
            name.fontStyle = FontStyle.Bold;
            SetRowHeight(name.rectTransform, 18);

            FinalBar(row, "Followers", player.followers, GameSettings.FollowersToWin,
                     new Color(0.75f, 0.6f, 0.2f));
            FinalBar(row, "Health", player.health, GameSettings.MaxHealth,
                     new Color(0.8f, 0.25f, 0.25f));
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

        private Text ActionLabel(string text, int fontSize = 15)
        {
            var label = UIFactory.Label("Info", _actionPanel, text, fontSize, TextAnchor.UpperLeft);
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            var element = label.gameObject.AddComponent<LayoutElement>();
            element.flexibleWidth = 1;
            return label;
        }

        private void RenderDraftHint(GameView view)
        {
            ActionLabel(view.currentDrafterId == view.viewerPlayerId
                ? "Your pick."
                : $"{FindPlayer(view, view.currentDrafterId)?.name} is picking.", 17);
        }

        private void RenderRolling(NetworkGameManager manager, GameView view)
        {
            var you = view.Viewer;
            if (you is { isAlive: true, hasRolled: false })
            {
                UIFactory.ButtonWithLabel(
                    "Roll", _actionPanel, "ROLL DIE", () => manager.RequestRollRpc(),
                    new Color(0.22f, 0.5f, 0.24f), width: 240, height: 54);
            }

            // Who has rolled what is on the dice row above; who the table is
            // waiting on is on the dock. Neither belongs here as well.
            if (!view.diceRolled)
            {
                return;
            }


            if (you != null && you.compound.Any(c => c.definitionId == CardIds.TryAgain))
            {
                UIFactory.ButtonWithLabel("Reroll", _actionPanel, "Try Again",
                    () => manager.RequestRerollRpc(), width: 200);
            }

            if (view.highRollResourceClaimed)
            {
                return;
            }

            var highRoller = HighestUniqueRoller(view);
            if (highRoller < 0 || highRoller != view.viewerPlayerId)
            {
                return;
            }

            ActionLabel("Highest roll - take one:", 16);
            RenderColorButtons(color => manager.RequestClaimHighRollResourceRpc((int)color));
        }

        private void RenderActivation(GameView view)
        {
            var values = view.players.Where(p => p.isAlive && p.primaryDie > 0).Select(p => p.primaryDie).ToList();
            ActionLabel($"Units on {string.Join(", ", values.Distinct().OrderBy(v => v))} are firing.", 16);
        }

        private void RenderResource(NetworkGameManager manager, GameView view)
        {
            if (view.Viewer is { collectedResources: true })
            {
                return;
            }

            // The last pick submits and finishes the phase, so there is no
            // confirm step and nothing to undo - just take what you want.
            var left = GameSettings.ResourcesPerTurn - _pendingResources.Count;
            ActionLabel(left == 1 ? "Take 1 more" : $"Take {left}", 17);

            RenderColorButtons(color =>
            {
                _pendingResources.Add(color);

                if (_pendingResources.Count < GameSettings.ResourcesPerTurn)
                {
                    RefreshActionPanel(manager, view);
                    return;
                }

                manager.RequestCollectResourcesRpc(_pendingResources.ConvertAll(c => (int)c).ToArray());
                _pendingResources.Clear();
            });
        }

        /// <summary>Suspicious Chef's paid meal counter, and Baal's Scheme-counter reroll.</summary>
        private void RenderCardActions(NetworkGameManager manager, GameView view)
        {
            var you = view.Viewer;
            if (you == null)
            {
                return;
            }

            foreach (var card in you.compound)
            {
                if (card.definitionId == CardIds.SuspiciousChef)
                {
                    ActionLabel(
                        $"Suspicious Chef - pay {GameSettings.MealCounterCost} of any colour: " +
                        $"{string.Join(", ", _pendingMealPayment)}", 13);

                    var instanceId = card.instanceId;
                    RenderColorButtons(color =>
                    {
                        _pendingMealPayment.Add(color);
                        if (_pendingMealPayment.Count == GameSettings.MealCounterCost)
                        {
                            manager.RequestBuyMealCounterRpc(
                                instanceId, _pendingMealPayment.ConvertAll(c => (int)c).ToArray());
                            _pendingMealPayment.Clear();
                        }

                        RefreshActionPanel(manager, view);
                    });

                    if (_pendingMealPayment.Count > 0)
                    {
                        UIFactory.ButtonWithLabel("Clear Meal", _actionPanel, "Clear", () =>
                        {
                            _pendingMealPayment.Clear();
                            RefreshActionPanel(manager, view);
                        }, width: 100);
                    }
                }

                if (card.definitionId == CardIds.BaalTheManipulator && view.phase == nameof(TurnPhase.Rolling))
                {
                    ActionLabel("Baal - spend a Scheme counter to set a die:", 13);

                    var row = UIFactory.Group("Baal Targets", _actionPanel);
                    AddFixedHeight(row, 32);
                    UIFactory.HorizontalLayout(row, 4, new RectOffset(0, 0, 0, 0));
                    foreach (var player in view.players.Where(p => p.isAlive))
                    {
                        var targetId = player.playerId;
                        UIFactory.ButtonWithLabel(player.name, row, player.name, () =>
                        {
                            _baalTargetPlayerId = targetId;
                            RefreshActionPanel(manager, view);
                        }, width: 100, height: 28);
                    }

                    if (_baalTargetPlayerId >= 0)
                    {
                        ActionLabel($"Set {FindPlayer(view, _baalTargetPlayerId)?.name}'s die to:", 13);
                        var faces = UIFactory.Group("Baal Faces", _actionPanel);
                        AddFixedHeight(faces, 32);
                        UIFactory.HorizontalLayout(faces, 4, new RectOffset(0, 0, 0, 0));
                        for (var face = 1; face <= GameSettings.DieSides; face++)
                        {
                            var chosenFace = face;
                            UIFactory.ButtonWithLabel($"Face {face}", faces, face.ToString(), () =>
                            {
                                manager.RequestSpendSchemeCounterRpc(_baalTargetPlayerId, chosenFace);
                                _baalTargetPlayerId = -1;
                            }, width: 40, height: 28);
                        }
                    }
                }
            }
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
            RefreshGame(NetworkGameManager.Instance, view);
        }

        private bool _discardOpen;

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
            _readyLabel.color = owes == null ? Color.white : new Color(1f, 1f, 1f, 0.45f);

            _readyButton.targetGraphic.color = owes != null
                ? new Color(0.22f, 0.22f, 0.24f)
                : you.isReady
                    ? new Color(0.4f, 0.3f, 0.15f)
                    : new Color(0.2f, 0.4f, 0.2f);

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

            if (!string.IsNullOrEmpty(view.resolvingDescription))
            {
                ActionLabel(view.resolvingDescription, 13);
            }

            if (choice.askedOfPlayerId != view.viewerPlayerId)
            {
                ActionLabel($"Waiting on {FindPlayer(view, choice.askedOfPlayerId)?.name} to decide: {choice.prompt}");
                return;
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
                            () => manager.RequestAnswerPlayerRpc(id), width: 200);
                    }

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
                            () => manager.RequestAnswerOptionRpc(chosen), width: 200);
                    }

                    break;

                case nameof(ChoiceKind.YesNo):
                    var yesNoRow = UIFactory.Group("Yes No", _actionPanel);
                    AddFixedHeight(yesNoRow, 36);
                    UIFactory.HorizontalLayout(yesNoRow, 8, new RectOffset(0, 0, 0, 0));
                    UIFactory.ButtonWithLabel("Yes", yesNoRow, "Yes", () => manager.RequestAnswerYesNoRpc(true),
                        new Color(0.2f, 0.4f, 0.2f), 90, 32);
                    UIFactory.ButtonWithLabel("No", yesNoRow, "No", () => manager.RequestAnswerYesNoRpc(false),
                        new Color(0.4f, 0.2f, 0.2f), 90, 32);
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
        /// the pip that now counts it, so a collected resource is something you
        /// watch arrive rather than a number that changed while you looked away.
        /// </summary>
        private void FlyResourceToStatBar(ResourceColor color, Component from)
        {
            if (from == null || _viewerStatBar == null)
            {
                return;
            }

            BoardEffects.Instance.FlyResource(
                from.transform.position, _viewerStatBar.PipPosition(color), color);
        }

        /// <summary>
        /// The resource picker: one disc per colour, in the colour it grants. The
        /// pip that flies out of a pressed disc is the same shape as the pip it
        /// lands in, so where a resource went is never in doubt.
        /// </summary>
        private void RenderColorButtons(Action<ResourceColor> onPicked, IEnumerable<ResourceColor> colors = null)
        {
            var row = UIFactory.Group("Colors", _actionPanel);
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
                    FlyResourceToStatBar(chosen, button);
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

            return view.draftZone.FirstOrDefault(c => c.instanceId == instanceId);
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
