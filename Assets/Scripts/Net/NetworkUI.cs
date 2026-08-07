using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
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

        private Vector2 _scroll;
        private readonly List<ResourceColor> _pendingResources = new();

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
                    DrawResourceCollection(manager);
                    break;
                case nameof(TurnPhase.Buy):
                    DrawBuy(manager, view);
                    break;
            }

            GUILayout.Space(8);
            DrawHand(view);
            GUILayout.Space(8);

            if (view.phase != nameof(TurnPhase.Draft) && view.winnerPlayerId < 0
                && GUILayout.Button("Next Phase", GUILayout.Width(160)))
            {
                manager.RequestAdvancePhaseRpc();
            }
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
                return;
            }

            GUILayout.Label("<b>Your pick:</b>", RichLabel());
            foreach (var card in view.draftZone)
            {
                if (GUILayout.Button(Describe(card), GUILayout.Height(22)))
                {
                    manager.RequestDraftRpc(card.instanceId);
                }
            }
        }

        private void DrawRolling(NetworkGameManager manager, GameView view)
        {
            var rolled = false;
            foreach (var player in view.players)
            {
                rolled |= player.primaryDie > 0;
            }

            if (!rolled)
            {
                if (GUILayout.Button("Roll Dice", GUILayout.Width(160)))
                {
                    manager.RequestRollRpc();
                }

                return;
            }

            if (HighestUniqueRoller(view) != view.viewerPlayerId)
            {
                GUILayout.Label("Dice are rolled.");
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

        private void DrawResourceCollection(NetworkGameManager manager)
        {
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
            foreach (var card in you.hand)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Describe(card));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Play", GUILayout.Width(60)))
                {
                    manager.RequestBuyRpc(card.instanceId);
                }

                if (GUILayout.Button("Recycle", GUILayout.Width(70)))
                {
                    manager.RequestRecycleRpc(card.instanceId);
                }

                GUILayout.EndHorizontal();
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
            foreach (var card in you.hand)
            {
                GUILayout.Label($"  {Describe(card)}");
            }

            GUILayout.Label($"<b>Your compound ({you.compound.Length})</b>", RichLabel());
            foreach (var card in you.compound)
            {
                GUILayout.Label($"  {Describe(card)}");
            }
        }

        private void DrawColorButtons(Action<ResourceColor> onPicked)
        {
            GUILayout.BeginHorizontal();
            foreach (ResourceColor color in Enum.GetValues(typeof(ResourceColor)))
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
