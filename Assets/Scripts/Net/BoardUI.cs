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

        private const float DockTopHeight = 82f;
        private const float BattlefieldRowHeight = BoardCardView.Height + 40f;

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
        private RectTransform _actionPanel;
        private RectTransform _handRow;
        private LayoutElement _handRowPin;
        private LayoutElement _dockPin;
        private Button _handToggleButton;
        private Text _handToggleLabel;
        private StatBar _viewerStatBar;

        // ---------------------------------------------------------------- State
        private NetworkGameManager _subscribedManager;
        private GameView _lastView;
        private float _secondsLeft;
        private bool _handExpanded = true;

        private readonly List<ResourceColor> _pendingResources = new();
        private readonly List<ResourceColor> _pendingMealPayment = new();
        private string _amountInput = "0";
        private int _baalTargetPlayerId = -1;

        private void Awake()
        {
            SetUpOverheadCamera();

            var canvas = UIFactory.CreateCanvas("Board Canvas");

            _connectPanel = BuildConnectPanel(canvas.transform);
            _lobbyPanel = BuildLobbyPanel(canvas.transform);
            _gameRoot = BuildGameRoot(canvas.transform);
            BuildErrorLabel(canvas.transform);

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
            }
            else
            {
                _secondsLeft = Mathf.Max(0f, _secondsLeft - Time.deltaTime);
            }

            if (_timerText != null && _gameRoot.gameObject.activeSelf)
            {
                _timerText.text = view.phase is nameof(TurnPhase.Draft) or nameof(TurnPhase.GameOver)
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
            var layout = UIFactory.VerticalLayout(root, 6, new RectOffset(10, 10, 10, 10), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperCenter;

            // Status strip: phase/turn/deck info, plus the phase timer.
            var status = UIFactory.Group("Status Row", root);
            AddFixedHeight(status, 24);
            UIFactory.HorizontalLayout(status, 12, new RectOffset(4, 4, 0, 0), controlWidth: true);
            _statusText = UIFactory.Label("Status", status, "", 15, TextAnchor.MiddleLeft);
            AddFlexibleWidth(_statusText.rectTransform);
            _timerText = UIFactory.Label("Timer", status, "", 13, TextAnchor.MiddleRight, new Color(0.8f, 0.8f, 0.6f));
            AddFixedWidth(_timerText.rectTransform, 260);

            // Opponents across the top of the board.
            _topBar = UIFactory.Group("Top Bar", root);
            AddFixedHeight(_topBar, 84);
            var topLayout = UIFactory.HorizontalLayout(_topBar, 10, new RectOffset(0, 0, 0, 0));
            topLayout.childAlignment = TextAnchor.MiddleCenter;

            // Battlefield + action panel share the flexible middle area. Both are
            // force-expanded vertically: neither reports a preferred height of its
            // own, so without this the layout would size them to nothing.
            var middle = UIFactory.Group("Middle Area", root);
            AddFlexibleHeight(middle);
            var middleLayout = UIFactory.HorizontalLayout(middle, 10, new RectOffset(0, 0, 0, 0), controlHeight: true);
            middleLayout.childForceExpandHeight = true;

            // Battlefield is a vertical stack of rows, not a horizontal strip, so
            // it gets its own scroll rect rather than reusing the horizontal helper.
            var battlefieldPanel = UIFactory.Panel("Battlefield Panel", middle, new Color(1, 1, 1, 0.04f));
            AddFlexibleWidth(battlefieldPanel);
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
            UIFactory.VerticalLayout(_battlefield, 8, new RectOffset(6, 6, 6, 6), controlHeight: true);
            UIFactory.FitToContent(_battlefield, ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize);
            battlefieldScroll.viewport = battlefieldViewport;
            battlefieldScroll.content = _battlefield;

            _actionPanel = UIFactory.Panel("Action Panel", middle, new Color(0.12f, 0.12f, 0.16f, 0.9f));
            var actionPin = _actionPanel.gameObject.AddComponent<LayoutElement>();
            actionPin.preferredWidth = 380;
            actionPin.minWidth = 380;
            var actionLayout = UIFactory.VerticalLayout(_actionPanel, 8, new RectOffset(12, 12, 12, 12), controlHeight: true);
            actionLayout.childAlignment = TextAnchor.UpperLeft;

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
                "Hand Toggle", dockTop, "Hand", ToggleHand, new Color(0.2f, 0.2f, 0.26f), 200, 36);
            _handToggleLabel = _handToggleButton.GetComponentInChildren<Text>();

            _handRow = UIFactory.Group("Hand Row", dock);
            _handRowPin = _handRow.gameObject.AddComponent<LayoutElement>();
            _handRowPin.flexibleWidth = 1;

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
            var winner = view.winnerPlayerId >= 0 ? FindPlayer(view, view.winnerPlayerId) : null;
            _statusText.text = winner != null
                ? $"{view.phase}  -  draft {view.draftNumber}, turn {view.turnInRound}/{GameSettings.TurnsPerRound}   <b>{winner.name} wins.</b>"
                : $"{view.phase}   draft {view.draftNumber}, turn {view.turnInRound}/{GameSettings.TurnsPerRound}   " +
                  $"deck {view.deckCount}, discard {view.discardCount}";

            RefreshTopBar(view);
            RefreshBattlefield(manager, view);
            RefreshActionPanel(manager, view);
            RefreshHand(view);
        }

        private void RefreshTopBar(GameView view)
        {
            UIFactory.DestroyChildren(_topBar);
            foreach (var player in view.players.Where(p => p.playerId != view.viewerPlayerId))
            {
                StatBar.Create(_topBar).Populate(player, isViewer: false);
            }
        }

        // ------------------------------------------------------- Battlefield

        private void RefreshBattlefield(NetworkGameManager manager, GameView view)
        {
            UIFactory.DestroyChildren(_battlefield);

            if (view.phase == nameof(TurnPhase.Draft))
            {
                var isMyPick = view.currentDrafterId == view.viewerPlayerId;
                BuildCardRow(
                    _battlefield, $"Draft Zone ({view.draftZone.Length})", view.draftZone,
                    card => isMyPick && IsDraftable(view, card),
                    card => manager.RequestDraftRpc(card.instanceId),
                    card => DraftMarkTag(view, card));
            }

            foreach (var player in view.players.Where(p => p.playerId != view.viewerPlayerId))
            {
                BuildCardRow(
                    _battlefield, $"{player.name}'s compound ({player.compound.Length})",
                    player.compound, null, null, null);
            }

            var you = view.Viewer;
            if (you != null)
            {
                BuildCardRow(_battlefield, $"Your compound ({you.compound.Length})", you.compound, null, null, null);
            }
        }

        private void BuildCardRow(
            Transform parent, string label, CardView[] cards,
            Func<CardView, bool> isClickable, Action<CardView> onClick, Func<CardView, string> tagFor)
        {
            var row = UIFactory.Group(label, parent);
            var rowLayout = UIFactory.VerticalLayout(row, 4, new RectOffset(0, 0, 0, 0), controlHeight: true);
            rowLayout.childAlignment = TextAnchor.UpperLeft;
            var rowPin = row.gameObject.AddComponent<LayoutElement>();
            rowPin.preferredHeight = BattlefieldRowHeight;
            rowPin.flexibleWidth = 1;

            var header = UIFactory.Label("Header", row, label, 14, TextAnchor.MiddleLeft);
            header.fontStyle = FontStyle.Bold;
            AddFixedHeight(header.rectTransform, 20);

            var content = UIFactory.HorizontalScroll("Scroll", row, BoardCardView.Height + 6);
            foreach (var card in cards)
            {
                var clickable = isClickable != null && isClickable(card);
                var cardView = BoardCardView.Create(content);
                cardView.Populate(card, tagFor?.Invoke(card), clickable ? () => onClick(card) : null);
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

            var canBuy = view.phase == nameof(TurnPhase.Buy) && view.pendingChoice == null;
            var handRowHeight = expanded ? BoardCardView.Height + (canBuy ? 44 : 6) + 10 : 0;
            _handRowPin.preferredHeight = handRowHeight;
            _dockPin.preferredHeight = DockTopHeight + handRowHeight;

            if (!expanded)
            {
                return;
            }

            var content = UIFactory.HorizontalScroll("Hand Scroll", _handRow, BoardCardView.Height + (canBuy ? 44 : 6));
            UIFactory.Stretch(UIFactory.Child(_handRow, "Hand Scroll"));

            foreach (var card in you.hand)
            {
                if (!canBuy)
                {
                    BoardCardView.Create(content).Populate(card, null, null);
                    continue;
                }

                var wrapper = UIFactory.Group("Hand Card", content);
                var wrapperLayout = UIFactory.VerticalLayout(wrapper, 4, new RectOffset(0, 0, 0, 0), controlHeight: true);
                var wrapperPin = wrapper.gameObject.AddComponent<LayoutElement>();
                wrapperPin.preferredWidth = BoardCardView.Width;
                wrapperPin.minWidth = BoardCardView.Width;

                BoardCardView.Create(wrapper).Populate(card, null, null);

                var buttons = UIFactory.Group("Buttons", wrapper);
                AddFixedHeight(buttons, 30);
                UIFactory.HorizontalLayout(buttons, 4, new RectOffset(0, 0, 0, 0));
                var instanceId = card.instanceId;
                UIFactory.ButtonWithLabel("Play", buttons, "Play",
                    () => NetworkGameManager.Instance?.RequestBuyRpc(instanceId),
                    new Color(0.2f, 0.4f, 0.2f), BoardCardView.Width / 2 - 3, 28);
                UIFactory.ButtonWithLabel("Recycle", buttons, "Recycle",
                    () => NetworkGameManager.Instance?.RequestRecycleRpc(instanceId),
                    new Color(0.4f, 0.35f, 0.15f), BoardCardView.Width / 2 - 3, 28);
            }
        }

        // ------------------------------------------------------- Action panel

        private void RefreshActionPanel(NetworkGameManager manager, GameView view)
        {
            UIFactory.DestroyChildren(_actionPanel);

            if (view.pendingChoice != null)
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
                    ActionLabel("Play cards from your hand below, or recycle them for a resource.");
                    break;
            }

            RenderCardActions(manager, view);
            RenderReadyCheck(manager, view);
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
                ? "Your pick - choose a card from the draft zone below."
                : $"Waiting for {FindPlayer(view, view.currentDrafterId)?.name} to draft.");
        }

        private void RenderRolling(NetworkGameManager manager, GameView view)
        {
            if (!view.diceRolled)
            {
                UIFactory.ButtonWithLabel("Roll", _actionPanel, "Roll Dice", () => manager.RequestRollRpc(), width: 200);
                return;
            }

            var you = view.Viewer;
            if (you != null && you.compound.Any(c => c.definitionId == CardIds.TryAgain))
            {
                UIFactory.ButtonWithLabel("Reroll", _actionPanel, "Try Again (reroll)",
                    () => manager.RequestRerollRpc(), width: 220);
            }

            if (view.highRollResourceClaimed)
            {
                ActionLabel("The high roll bonus has been taken.");
                return;
            }

            var highRoller = HighestUniqueRoller(view);
            if (highRoller < 0)
            {
                ActionLabel("The top roll was tied, so nobody takes the bonus resource.");
                return;
            }

            if (highRoller != view.viewerPlayerId)
            {
                ActionLabel($"{FindPlayer(view, highRoller)?.name} rolled highest.");
                return;
            }

            ActionLabel("You rolled highest. Take one resource:");
            RenderColorButtons(color => manager.RequestClaimHighRollResourceRpc((int)color));
        }

        private void RenderActivation(GameView view)
        {
            var values = view.players.Where(p => p.isAlive && p.primaryDie > 0).Select(p => p.primaryDie).ToList();
            ActionLabel($"Dice showing: {string.Join(", ", values)}. Every player's units on those numbers activate.");
        }

        private void RenderResource(NetworkGameManager manager, GameView view)
        {
            if (view.Viewer is { collectedResources: true })
            {
                ActionLabel("You have taken your resources for this turn.");
                return;
            }

            ActionLabel($"Choose {GameSettings.ResourcesPerTurn} resources: {string.Join(", ", _pendingResources)}");
            RenderColorButtons(color =>
            {
                _pendingResources.Add(color);
                if (_pendingResources.Count == GameSettings.ResourcesPerTurn)
                {
                    manager.RequestCollectResourcesRpc(_pendingResources.ConvertAll(c => (int)c).ToArray());
                    _pendingResources.Clear();
                }

                RefreshActionPanel(manager, view);
            });

            if (_pendingResources.Count > 0)
            {
                UIFactory.ButtonWithLabel("Clear", _actionPanel, "Clear", () =>
                {
                    _pendingResources.Clear();
                    RefreshActionPanel(manager, view);
                }, width: 100);
            }
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

        private void RenderReadyCheck(NetworkGameManager manager, GameView view)
        {
            if (view.phase == nameof(TurnPhase.Draft) || view.winnerPlayerId >= 0)
            {
                return;
            }

            var you = view.Viewer;
            if (you == null)
            {
                return;
            }

            var waitingOn = view.players.Where(p => p.isAlive && !p.isReady).Select(p => p.name).ToList();

            var spacer = UIFactory.Group("Spacer", _actionPanel);
            var spacerElement = spacer.gameObject.AddComponent<LayoutElement>();
            spacerElement.flexibleHeight = 1;

            UIFactory.ButtonWithLabel(
                "Ready", _actionPanel, you.isReady ? "Not Ready" : "Ready",
                () => manager.RequestSetReadyRpc(!you.isReady),
                you.isReady ? new Color(0.4f, 0.3f, 0.15f) : new Color(0.2f, 0.4f, 0.2f), 200, 36);

            ActionLabel(waitingOn.Count > 0 ? $"Waiting on: {string.Join(", ", waitingOn)}" : "Everyone is ready.", 13);
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
                    BuildCardRow(_actionPanel, "Choose one:", options,
                        _ => true, card => manager.RequestAnswerCardRpc(card.instanceId), null);
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

        private void RenderColorButtons(Action<ResourceColor> onPicked, IEnumerable<ResourceColor> colors = null)
        {
            var row = UIFactory.Group("Colors", _actionPanel);
            AddFixedHeight(row, 34);
            UIFactory.HorizontalLayout(row, 6, new RectOffset(0, 0, 0, 0));
            foreach (var color in colors ?? Enum.GetValues(typeof(ResourceColor)).Cast<ResourceColor>())
            {
                var chosen = color;
                UIFactory.ButtonWithLabel(color.ToString(), row, color.ToString(),
                    () => onPicked(chosen), ColorSwatch(chosen), 84, 30);
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

        private static void AddFlexibleHeight(Component rect)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.flexibleHeight = 1;
            element.flexibleWidth = 1;
        }
    }
}
