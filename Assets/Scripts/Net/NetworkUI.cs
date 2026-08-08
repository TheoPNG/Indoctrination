using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Indoctrination.Net
{
    /// <summary>
    /// A deliberately plain on-screen interface for hosting, joining, and playing.
    /// It is drawn with IMGUI, which needs no prefabs, canvases, or scene wiring -
    /// so the whole multiplayer loop can be tested before any real art exists.
    /// Replace this wholesale when the proper UI is built; nothing else depends on it.
    /// </summary>
    public class NetworkUI : MonoBehaviour
    {
        [Tooltip("Address clients connect to. 127.0.0.1 is this same machine.")]
        public string address = "127.0.0.1";

        public ushort port = 7777;

        // Every card is drawn at the same size, poker-card proportions, so a hand
        // and a draft zone read the same way at a glance.
        private const float CardWidth = 150f;
        private const float CardHeight = 210f;

        private Vector2 _scroll;
        private readonly List<ResourceColor> _pendingResources = new();

        private GameView _lastSeenView;
        private float _secondsLeft;
        private string _amountInput = "0";
        private readonly List<ResourceColor> _pendingMealPayment = new();
        private int _baalTargetPlayerId = -1;

        /// <summary>
        /// Counts the phase timer down locally. The server sends how long is left
        /// only when something else changes, so ticking it here avoids a stream of
        /// once-a-second updates just to animate a number.
        /// </summary>
        private void Update()
        {
            var view = NetworkGameManager.Instance == null ? null : NetworkGameManager.Instance.View;

            if (view == null)
            {
                _lastSeenView = null;
                _secondsLeft = 0f;
                return;
            }

            if (!ReferenceEquals(view, _lastSeenView))
            {
                // A phase change invalidates a half-finished resource pick, meal
                // payment, or Baal target - each belongs to the phase it was
                // started in.
                if (_lastSeenView == null || view.phase != _lastSeenView.phase)
                {
                    _pendingResources.Clear();
                    _pendingMealPayment.Clear();
                    _baalTargetPlayerId = -1;
                }

                _lastSeenView = view;
                _secondsLeft = view.phaseSecondsRemaining;
                return;
            }

            _secondsLeft = Mathf.Max(0f, _secondsLeft - Time.deltaTime);
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
            GUILayout.Label("<b>INDOCTRINATION</b>", RichLabel());

            var network = NetworkManager.Singleton;
            if (network == null)
            {
                GUILayout.Label("No NetworkManager in the scene. Run Indoctrination > Set Up Multiplayer Scene.");
                GUILayout.EndArea();
                return;
            }

            if (!network.IsClient && !network.IsServer)
            {
                DrawConnectMenu(network);
            }
            else
            {
                DrawSession(network);
            }

            GUILayout.EndArea();
        }

        private void DrawConnectMenu(NetworkManager network)
        {
            GUILayout.Label("Host a game, or join one that is already running.");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Address", GUILayout.Width(60));
            address = GUILayout.TextField(address, GUILayout.Width(160));
            GUILayout.Label("Port", GUILayout.Width(35));
            if (ushort.TryParse(GUILayout.TextField(port.ToString(), GUILayout.Width(60)), out var typedPort))
            {
                port = typedPort;
            }

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host", GUILayout.Width(120)))
            {
                Configure(network);
                network.StartHost();
            }

            if (GUILayout.Button("Join", GUILayout.Width(120)))
            {
                Configure(network);
                network.StartClient();
            }

            GUILayout.EndHorizontal();
        }

        private void Configure(NetworkManager network)
        {
            // Listening on 0.0.0.0 lets other machines on the network reach the host;
            // binding to the address itself would only ever accept local connections.
            var transport = network.GetComponent<UnityTransport>();
            if (transport != null)
            {
                transport.SetConnectionData(address, port, "0.0.0.0");
            }
        }

        private void DrawSession(NetworkManager network)
        {
            var manager = NetworkGameManager.Instance;

            GUILayout.BeginHorizontal();
            GUILayout.Label(network.IsHost ? "Hosting" : network.IsServer ? "Server" : "Connected");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Leave", GUILayout.Width(80)))
            {
                network.Shutdown();
            }

            GUILayout.EndHorizontal();

            if (manager == null)
            {
                GUILayout.Label("Waiting for the game object to spawn...");
                return;
            }

            if (!string.IsNullOrEmpty(manager.LastError))
            {
                GUILayout.Label($"<color=#ff6666>{manager.LastError}</color>", RichLabel());
            }

            if (manager.View == null)
            {
                DrawLobby(network, manager);
                return;
            }

            _scroll = GUILayout.BeginScrollView(_scroll);
            DrawGame(manager);
            GUILayout.EndScrollView();
        }

        private void DrawLobby(NetworkManager network, NetworkGameManager manager)
        {
            var lobby = manager.Lobby;
            if (lobby == null)
            {
                GUILayout.Label("Joining...");
                return;
            }

            GUILayout.Label($"Players ({lobby.playerNames.Length}/{lobby.maxPlayers}):");
            foreach (var name in lobby.playerNames)
            {
                GUILayout.Label($"  {name}");
            }

            if (!network.IsHost)
            {
                GUILayout.Label("Waiting for the host to start.");
                return;
            }

            GUI.enabled = lobby.playerNames.Length >= lobby.minPlayers;
            if (GUILayout.Button("Start Game", GUILayout.Width(160)))
            {
                manager.RequestStartGameRpc();
            }

            GUI.enabled = true;

            if (lobby.playerNames.Length < lobby.minPlayers)
            {
                GUILayout.Label($"Needs {lobby.minPlayers} players.");
            }
        }

        private void DrawGame(NetworkGameManager manager)
        {
            var view = manager.View;

            GUILayout.Label(
                $"<b>{view.phase}</b>   draft {view.draftNumber}, turn {view.turnInRound}/{GameSettings.TurnsPerRound}   " +
                $"deck {view.deckCount}, discard {view.discardCount}",
                RichLabel());

            if (view.winnerPlayerId >= 0)
            {
                var winner = FindPlayer(view, view.winnerPlayerId);
                GUILayout.Label($"<b>{winner?.name} wins.</b>", RichLabel());
            }

            DrawScoreboard(view);
            GUILayout.Space(8);

            if (view.pendingChoice != null)
            {
                // A card is waiting on an answer, which blocks every other action
                // at the table until it is resolved - so nothing else is drawn.
                DrawPendingChoice(manager, view);
                GUILayout.Space(8);
                DrawHand(view);
                return;
            }

            switch (view.phase)
            {
                case nameof(TurnPhase.Draft):
                    DrawDraft(manager, view);
                    break;
                case nameof(TurnPhase.Rolling):
                    DrawRolling(manager, view);
                    break;
                case nameof(TurnPhase.Activation):
                    DrawActivation(view);
                    break;
                case nameof(TurnPhase.Resource):
                    DrawResourceCollection(manager, view);
                    break;
                case nameof(TurnPhase.Buy):
                    DrawBuy(manager, view);
                    break;
            }

            GUILayout.Space(8);
            DrawHand(view);
            GUILayout.Space(8);
            DrawCardActions(manager, view);
            GUILayout.Space(8);
            DrawReadyCheck(manager, view);
        }

        /// <summary>
        /// The phase only moves on when everyone agrees, so this shows who the
        /// table is still waiting on and how long until it gives up waiting.
        /// </summary>
        private void DrawReadyCheck(NetworkGameManager manager, GameView view)
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

            var waitingOn = view.players
                .Where(player => player.isAlive && !player.isReady)
                .Select(player => player.name)
                .ToList();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(you.isReady ? "Not Ready" : "Ready", GUILayout.Width(160)))
            {
                manager.RequestSetReadyRpc(!you.isReady);
            }

            GUILayout.Label(waitingOn.Count > 0
                ? $"Waiting on: {string.Join(", ", waitingOn)}   ({_secondsLeft:0}s until it moves on anyway)"
                : "Everyone is ready.");
            GUILayout.EndHorizontal();
        }

        private void DrawScoreboard(GameView view)
        {
            foreach (var player in view.players)
            {
                var you = player.playerId == view.viewerPlayerId ? " (you)" : "";
                var die = player.primaryDie > 0 ? $"   die {player.primaryDie}" : "";
                GUILayout.Label(
                    $"{player.name}{you}   HP {player.health}   followers {player.followers}/{GameSettings.FollowersToWin}   " +
                    $"R{player.red} G{player.green} B{player.blue} Y{player.yellow}   " +
                    $"hand {player.handCount}   compound {player.compound.Length}{die}");
            }
        }

        private void DrawDraft(NetworkGameManager manager, GameView view)
        {
            if (view.currentDrafterId != view.viewerPlayerId)
            {
                var drafter = FindPlayer(view, view.currentDrafterId);
                GUILayout.Label($"Waiting for {drafter?.name} to draft.");
                DrawCardGrid(view.draftZone, tagFor: card => DraftMarkTag(view, card));
                return;
            }

            GUILayout.Label("<b>Your pick:</b>", RichLabel());
            DrawCardGrid(
                view.draftZone,
                card => IsDraftable(view, card),
                card => manager.RequestDraftRpc(card.instanceId),
                card => DraftMarkTag(view, card));
        }

        /// <summary>Blocked by Games and the Parking Spot both take a card off the table.</summary>
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

        /// <summary>
        /// All three draft Blessings mark their card in the open, so this is
        /// shown to every viewer rather than only the player who set the mark.
        /// </summary>
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

        private void DrawRolling(NetworkGameManager manager, GameView view)
        {
            if (!view.diceRolled)
            {
                if (GUILayout.Button("Roll Dice", GUILayout.Width(160)))
                {
                    manager.RequestRollRpc();
                }

                return;
            }

            var you = view.Viewer;
            if (you != null && you.compound.Any(card => card.definitionId == CardIds.TryAgain)
                             && GUILayout.Button("Try Again (reroll)", GUILayout.Width(200)))
            {
                manager.RequestRerollRpc();
            }

            if (view.highRollResourceClaimed)
            {
                GUILayout.Label("The high roll bonus has been taken.");
                return;
            }

            var highRoller = HighestUniqueRoller(view);
            if (highRoller < 0)
            {
                GUILayout.Label("The top roll was tied, so nobody takes the bonus resource.");
                return;
            }

            if (highRoller != view.viewerPlayerId)
            {
                GUILayout.Label($"{FindPlayer(view, highRoller)?.name} rolled highest.");
                return;
            }

            GUILayout.Label("You rolled highest. Take one resource:");
            DrawColorButtons(color => manager.RequestClaimHighRollResourceRpc((int)color));
        }

        private void DrawActivation(GameView view)
        {
            var values = new List<int>();
            foreach (var player in view.players)
            {
                if (player.isAlive && player.primaryDie > 0)
                {
                    values.Add(player.primaryDie);
                }
            }

            GUILayout.Label($"Dice showing: {string.Join(", ", values)}. " +
                            "Every player's units on those numbers activate.");

            foreach (var player in view.players)
            {
                foreach (var card in player.compound)
                {
                    var definition = Definition(card);
                    if (definition == null || definition.Type != CardType.Unit)
                    {
                        continue;
                    }

                    foreach (var value in values)
                    {
                        if (definition.ActivationNumbers.Contains(value))
                        {
                            GUILayout.Label($"  {player.name}: {definition.Title} - {definition.Effect}");
                            break;
                        }
                    }
                }
            }
        }

        private void DrawResourceCollection(NetworkGameManager manager, GameView view)
        {
            if (view.Viewer is { collectedResources: true })
            {
                GUILayout.Label("You have taken your resources for this turn.");
                return;
            }

            GUILayout.Label($"Choose {GameSettings.ResourcesPerTurn} resources: " +
                            $"{string.Join(", ", _pendingResources)}");

            DrawColorButtons(color =>
            {
                _pendingResources.Add(color);
                if (_pendingResources.Count == GameSettings.ResourcesPerTurn)
                {
                    manager.RequestCollectResourcesRpc(_pendingResources.ConvertAll(c => (int)c).ToArray());
                    _pendingResources.Clear();
                }
            });

            if (_pendingResources.Count > 0 && GUILayout.Button("Clear", GUILayout.Width(80)))
            {
                _pendingResources.Clear();
            }
        }

        private void DrawBuy(NetworkGameManager manager, GameView view)
        {
            var you = view.Viewer;
            if (you == null || you.hand.Length == 0)
            {
                GUILayout.Label("Nothing in hand to play.");
                return;
            }

            GUILayout.Label("<b>Play from hand, or recycle for a resource:</b>", RichLabel());

            var perRow = CardsPerRow();
            for (var i = 0; i < you.hand.Length; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (var j = i; j < Mathf.Min(i + perRow, you.hand.Length); j++)
                {
                    var card = you.hand[j];

                    GUILayout.BeginVertical(GUILayout.Width(CardWidth));
                    CardBox(card, clickable: false);

                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Play", GUILayout.Width(CardWidth / 2 - 3)))
                    {
                        manager.RequestBuyRpc(card.instanceId);
                    }

                    if (GUILayout.Button("Recycle", GUILayout.Width(CardWidth / 2 - 3)))
                    {
                        manager.RequestRecycleRpc(card.instanceId);
                    }

                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                    GUILayout.Space(6);
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(6);
            }
        }

        private void DrawHand(GameView view)
        {
            var you = view.Viewer;
            if (you == null)
            {
                return;
            }

            GUILayout.Label($"<b>Your hand ({you.hand.Length})</b>", RichLabel());
            DrawCardGrid(you.hand);

            GUILayout.Label($"<b>Your compound ({you.compound.Length})</b>", RichLabel());
            DrawCardGrid(you.compound);
        }

        /// <summary>
        /// Actions a card lets its owner take on their own initiative, rather than
        /// in answer to a question - Suspicious Chef's paid meal counter and
        /// Baal's Scheme-counter reroll.
        /// </summary>
        private void DrawCardActions(NetworkGameManager manager, GameView view)
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
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{Describe(card)} - pay {GameSettings.MealCounterCost} of any colour: " +
                                    $"{string.Join(", ", _pendingMealPayment)}");
                    DrawColorButtons(color =>
                    {
                        _pendingMealPayment.Add(color);
                        if (_pendingMealPayment.Count == GameSettings.MealCounterCost)
                        {
                            manager.RequestBuyMealCounterRpc(
                                card.instanceId, _pendingMealPayment.ConvertAll(c => (int)c).ToArray());
                            _pendingMealPayment.Clear();
                        }
                    });
                    if (_pendingMealPayment.Count > 0 && GUILayout.Button("Clear", GUILayout.Width(60)))
                    {
                        _pendingMealPayment.Clear();
                    }

                    GUILayout.EndHorizontal();
                }

                if (card.definitionId == CardIds.BaalTheManipulator && view.phase == nameof(TurnPhase.Rolling))
                {
                    GUILayout.Label($"{Describe(card)} - spend a Scheme counter to set a die:");
                    GUILayout.BeginHorizontal();
                    foreach (var player in view.players.Where(p => p.isAlive))
                    {
                        if (GUILayout.Button(player.name, GUILayout.Width(100)))
                        {
                            _baalTargetPlayerId = player.playerId;
                        }
                    }

                    GUILayout.EndHorizontal();

                    if (_baalTargetPlayerId >= 0)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label($"Set {FindPlayer(view, _baalTargetPlayerId)?.name}'s die to:", GUILayout.Width(160));
                        for (var face = 1; face <= GameSettings.DieSides; face++)
                        {
                            if (GUILayout.Button(face.ToString(), GUILayout.Width(30)))
                            {
                                manager.RequestSpendSchemeCounterRpc(_baalTargetPlayerId, face);
                                _baalTargetPlayerId = -1;
                            }
                        }

                        GUILayout.EndHorizontal();
                    }
                }
            }
        }

        /// <summary>
        /// A card has stopped to ask a question. Only the player it asked can
        /// answer; everyone else just sees what is being waited on.
        /// </summary>
        private void DrawPendingChoice(NetworkGameManager manager, GameView view)
        {
            var choice = view.pendingChoice;

            if (!string.IsNullOrEmpty(view.resolvingDescription))
            {
                GUILayout.Label($"<i>{view.resolvingDescription}</i>", RichLabel());
            }

            if (choice.askedOfPlayerId != view.viewerPlayerId)
            {
                var asked = FindPlayer(view, choice.askedOfPlayerId);
                GUILayout.Label($"Waiting on {asked?.name} to decide: {choice.prompt}");
                return;
            }

            GUILayout.Label($"<b>{choice.prompt}</b>", RichLabel());

            switch (choice.kind)
            {
                case nameof(ChoiceKind.Player):
                    foreach (var optionId in choice.playerOptions)
                    {
                        var option = FindPlayer(view, optionId);
                        if (GUILayout.Button(option?.name ?? optionId.ToString(), GUILayout.Width(160)))
                        {
                            manager.RequestAnswerPlayerRpc(optionId);
                        }
                    }

                    break;

                case nameof(ChoiceKind.Card):
                    DrawCardGrid(
                        choice.cardOptions.Select(id => FindCard(view, id)).Where(card => card != null),
                        onClick: card => manager.RequestAnswerCardRpc(card.instanceId));
                    break;

                case nameof(ChoiceKind.Color):
                    var offered = choice.colorOptions.Length > 0
                        ? choice.colorOptions.Select(c => (ResourceColor)c)
                        : Enum.GetValues(typeof(ResourceColor)).Cast<ResourceColor>();

                    DrawColorButtons(color => manager.RequestAnswerColorRpc((int)color), offered);
                    break;

                case nameof(ChoiceKind.Option):
                    foreach (var option in choice.options)
                    {
                        if (GUILayout.Button(option, GUILayout.Width(160)))
                        {
                            manager.RequestAnswerOptionRpc(option);
                        }
                    }

                    break;

                case nameof(ChoiceKind.YesNo):
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Yes", GUILayout.Width(80)))
                    {
                        manager.RequestAnswerYesNoRpc(true);
                    }

                    if (GUILayout.Button("No", GUILayout.Width(80)))
                    {
                        manager.RequestAnswerYesNoRpc(false);
                    }

                    GUILayout.EndHorizontal();
                    break;

                case nameof(ChoiceKind.Amount):
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Between {choice.minAmount} and {choice.maxAmount}:", GUILayout.Width(160));
                    _amountInput = GUILayout.TextField(_amountInput, GUILayout.Width(60));
                    if (GUILayout.Button("Confirm", GUILayout.Width(80))
                        && int.TryParse(_amountInput, out var amount)
                        && amount >= choice.minAmount && amount <= choice.maxAmount)
                    {
                        manager.RequestAnswerAmountRpc(amount);
                    }

                    GUILayout.EndHorizontal();
                    break;
            }
        }

        private static CardView FindCard(GameView view, int instanceId)
        {
            foreach (var player in view.players)
            {
                foreach (var card in player.hand)
                {
                    if (card.instanceId == instanceId)
                    {
                        return card;
                    }
                }

                foreach (var card in player.compound)
                {
                    if (card.instanceId == instanceId)
                    {
                        return card;
                    }
                }
            }

            foreach (var card in view.draftZone)
            {
                if (card.instanceId == instanceId)
                {
                    return card;
                }
            }

            return null;
        }

        // ----------------------------------------------------------- Card boxes

        /// <summary>How many fixed-size cards fit across the window at once.</summary>
        private static int CardsPerRow() => Mathf.Max(1, Mathf.FloorToInt((Screen.width - 40) / (CardWidth + 10)));

        /// <summary>
        /// Lays a set of cards out in rows of fixed-size boxes. Optionally
        /// clickable, with an optional tag drawn at the top of the box - used for
        /// the draft markers, which are public information rather than hidden state.
        /// </summary>
        private void DrawCardGrid(
            IEnumerable<CardView> cards,
            Func<CardView, bool> isClickable = null,
            Action<CardView> onClick = null,
            Func<CardView, string> tagFor = null)
        {
            var list = cards.ToList();
            if (list.Count == 0)
            {
                return;
            }

            var perRow = CardsPerRow();
            for (var i = 0; i < list.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (var j = i; j < Mathf.Min(i + perRow, list.Count); j++)
                {
                    var card = list[j];
                    var clickable = isClickable == null || isClickable(card);
                    if (CardBox(card, clickable, tagFor?.Invoke(card)) && clickable)
                    {
                        onClick?.Invoke(card);
                    }

                    GUILayout.Space(6);
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(6);
            }
        }

        /// <summary>
        /// Draws one card as a fixed-size rectangle carrying everything needed to
        /// evaluate it at a glance - colour, type, cost, activation numbers, and
        /// full effect text - rather than the single line of shorthand this used
        /// to be. Returns true on the frame the box was clicked, if it is clickable.
        /// </summary>
        private bool CardBox(CardView card, bool clickable, string tag = null)
        {
            var definition = Definition(card);

            GUILayout.BeginVertical(CardBoxStyle(), GUILayout.Width(CardWidth), GUILayout.Height(CardHeight));

            if (!string.IsNullOrEmpty(tag))
            {
                GUILayout.Label($"<color=#ff9955><b>{tag}</b></color>", CardMetaStyle());
            }

            if (definition == null)
            {
                // A card id the client's copy of Cards.json does not recognise -
                // still drawn as a box rather than silently dropped, so a data
                // mismatch is obvious instead of an empty gap in the row.
                GUILayout.FlexibleSpace();
                GUILayout.Label(card.definitionId, CardTitleStyle());
                GUILayout.FlexibleSpace();
            }
            else
            {
                GUILayout.Label(
                    $"<color={ColorHex(definition.Color)}><b>{definition.Color}</b></color>  ·  {definition.Type}",
                    CardMetaStyle());
                GUILayout.Label(definition.Title, CardTitleStyle());
                GUILayout.Label($"Cost: {(definition.Cost.IsSpecial ? "special" : definition.costRaw)}", CardMetaStyle());

                if (definition.Type == CardType.Unit && definition.ActivationNumbers.Count > 0)
                {
                    GUILayout.Label($"Activates: {string.Join(", ", definition.ActivationNumbers)}", CardMetaStyle());
                }

                GUILayout.Label(definition.Effect, CardEffectStyle());
                GUILayout.FlexibleSpace();
            }

            GUILayout.EndVertical();

            var rect = GUILayoutUtility.GetLastRect();
            if (!clickable)
            {
                return false;
            }

            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || !rect.Contains(e.mousePosition))
            {
                return false;
            }

            e.Use();
            return true;
        }

        private static string ColorHex(ResourceColor color)
        {
            return color switch
            {
                ResourceColor.Red => "#e05a5a",
                ResourceColor.Green => "#4fae55",
                ResourceColor.Blue => "#5588e0",
                ResourceColor.Yellow => "#d1a83d",
                _ => "#ffffff"
            };
        }

        private GUIStyle _cardBoxStyle;
        private GUIStyle _cardTitleStyle;
        private GUIStyle _cardMetaStyle;
        private GUIStyle _cardEffectStyle;

        private GUIStyle CardBoxStyle()
        {
            return _cardBoxStyle ??= new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(6, 6, 6, 6)
            };
        }

        private GUIStyle CardTitleStyle()
        {
            return _cardTitleStyle ??= new GUIStyle(GUI.skin.label)
            {
                richText = true,
                wordWrap = true,
                fontStyle = FontStyle.Bold,
                fontSize = 13
            };
        }

        private GUIStyle CardMetaStyle()
        {
            return _cardMetaStyle ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 10 };
        }

        private GUIStyle CardEffectStyle()
        {
            return _cardEffectStyle ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 10 };
        }

        private void DrawColorButtons(Action<ResourceColor> onPicked, IEnumerable<ResourceColor> colors = null)
        {
            GUILayout.BeginHorizontal();
            foreach (var color in colors ?? Enum.GetValues(typeof(ResourceColor)).Cast<ResourceColor>())
            {
                if (GUILayout.Button(color.ToString(), GUILayout.Width(80)))
                {
                    onPicked(color);
                }
            }

            GUILayout.EndHorizontal();
        }

        // --------------------------------------------------------------- Helpers

        /// <summary>Who rolled highest, or -1 if the top roll was tied.</summary>
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

        private static PlayerView FindPlayer(GameView view, int playerId)
        {
            foreach (var player in view.players)
            {
                if (player.playerId == playerId)
                {
                    return player;
                }
            }

            return null;
        }

        private static CardDefinition Definition(CardView card)
        {
            return CardDatabase.Instance.TryGet(card.definitionId, out var definition) ? definition : null;
        }

        private static string Describe(CardView card)
        {
            var definition = Definition(card);
            if (definition == null)
            {
                return card.definitionId;
            }

            var numbers = definition.Type == CardType.Unit && definition.ActivationNumbers.Count > 0
                ? $" [{string.Join("/", definition.ActivationNumbers)}]"
                : "";

            return $"{definition.Title}{numbers} - {definition.Type}, {definition.costRaw}, {definition.color}";
        }

        private GUIStyle _richLabel;

        /// <summary>
        /// Built lazily rather than in Awake because GUI.skin only exists during
        /// OnGUI, and cached because OnGUI runs several times a frame.
        /// </summary>
        private GUIStyle RichLabel()
        {
            return _richLabel ??= new GUIStyle(GUI.skin.label) { richText = true };
        }
    }
}
