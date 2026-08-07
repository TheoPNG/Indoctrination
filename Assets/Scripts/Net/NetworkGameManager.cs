using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
using Unity.Netcode;
using UnityEngine;

namespace Indoctrination.Net
{
    /// <summary>
    /// The bridge between Netcode and the rules engine.
    ///
    /// Only the server ever holds a <see cref="GameState"/>. Clients ask for things
    /// and are told what happened; they cannot change the game themselves. That
    /// matters for a game with hidden hands - a client that owned its own state
    /// could deal itself whatever cards it liked, and would have to be sent the
    /// whole game to render it, hands included.
    /// </summary>
    public class NetworkGameManager : NetworkBehaviour
    {
        public static NetworkGameManager Instance { get; private set; }

        // ------------------------------------------------------------ Server only
        private GameState _game;

        /// <summary>Seat index is the Core player id; the value is the Netcode client id.</summary>
        private readonly List<ulong> _seats = new();

        private readonly Dictionary<ulong, string> _names = new();

        /// <summary>Time.time when the current phase began, for the timeout.</summary>
        private float _phaseStartedAt;

        // ---------------------------------------------------------- Every machine
        /// <summary>This machine's view of the game, or null before it starts.</summary>
        public GameView View { get; private set; }

        public LobbyView Lobby { get; private set; }

        /// <summary>The last rules error this machine's own action produced.</summary>
        public string LastError { get; private set; }

        /// <summary>Raised whenever View, Lobby, or LastError changes.</summary>
        public event Action Changed;

        public override void OnNetworkSpawn()
        {
            Instance = this;

            if (!IsServer)
            {
                return;
            }

            NetworkManager.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;

            // The host's own client is already connected by the time this runs.
            foreach (var clientId in NetworkManager.ConnectedClientsIds)
            {
                Seat(clientId);
            }

            BroadcastLobby();
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            if (Instance == this)
            {
                Instance = null;
            }

            View = null;
            Lobby = null;
        }

        // ----------------------------------------------------------------- Lobby

        private void OnClientConnected(ulong clientId)
        {
            Seat(clientId);
            BroadcastLobby();

            // A reconnecting or late-spawning client needs the current state.
            if (_game != null)
            {
                SendStateTo(clientId);
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            // Mid-game the seat is kept, so the player keeps their board if they
            // come back. Before the game starts there is nothing to keep.
            if (_game == null)
            {
                var seat = _seats.IndexOf(clientId);
                if (seat >= 0)
                {
                    _seats.RemoveAt(seat);
                    _names.Remove(clientId);
                }
            }

            BroadcastLobby();
        }

        /// <summary>Gives a client a seat, or returns its existing one. -1 if there is no room.</summary>
        private int Seat(ulong clientId)
        {
            var existing = _seats.IndexOf(clientId);
            if (existing >= 0)
            {
                return existing;
            }

            if (_game != null || _seats.Count >= GameSettings.MaxPlayers)
            {
                return -1;
            }

            _seats.Add(clientId);
            _names[clientId] = $"Leader {_seats.Count}";
            return _seats.Count - 1;
        }

        private void BroadcastLobby()
        {
            var lobby = new LobbyView
            {
                playerNames = _seats.Select(id => _names[id]).ToArray(),
                minPlayers = GameSettings.MinPlayers,
                maxPlayers = GameSettings.MaxPlayers
            };

            SyncLobbyRpc(JsonUtility.ToJson(lobby));
        }

        [Rpc(SendTo.ClientsAndHost)]
        private void SyncLobbyRpc(string json)
        {
            Lobby = JsonUtility.FromJson<LobbyView>(json);
            Changed?.Invoke();
        }

        // ------------------------------------------------------- Requests inbound

        [Rpc(SendTo.Server)]
        public void RequestStartGameRpc(RpcParams rpcParams = default)
        {
            // Only the host decides when the table is full enough to begin.
            if (rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId)
            {
                return;
            }

            if (_game != null)
            {
                return;
            }

            if (_seats.Count < GameSettings.MinPlayers)
            {
                ReportError($"Need at least {GameSettings.MinPlayers} players to start.", rpcParams.Receive.SenderClientId);
                return;
            }

            var names = _seats.Select(id => _names[id]).ToList();
            var seed = Environment.TickCount;

            _game = new GameState(names, CardDatabase.Instance.All, seed)
            {
                FirstDrafterIndex = new System.Random(seed).Next(names.Count)
            };
            _game.BeginDraft();
            _phaseStartedAt = Time.time;

            BroadcastState();
        }

        [Rpc(SendTo.Server)]
        public void RequestDraftRpc(int cardInstanceId, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.DraftCard(playerId, cardInstanceId));
        }

        [Rpc(SendTo.Server)]
        public void RequestRollRpc(RpcParams rpcParams = default)
        {
            // Everyone's dice are rolled at once, so this is not tied to a seat -
            // any player at the table can call for the roll.
            Apply(rpcParams, _ => _game.RollPrimaryDice());
        }

        [Rpc(SendTo.Server)]
        public void RequestClaimHighRollResourceRpc(int color, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.ClaimHighRollResource(playerId, (ResourceColor)color));
        }

        [Rpc(SendTo.Server)]
        public void RequestCollectResourcesRpc(int[] colors, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId =>
                _game.CollectResources(playerId, colors.Select(c => (ResourceColor)c).ToList()));
        }

        [Rpc(SendTo.Server)]
        public void RequestBuyRpc(int cardInstanceId, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.BuyCard(playerId, cardInstanceId));
        }

        [Rpc(SendTo.Server)]
        public void RequestRecycleRpc(int cardInstanceId, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.RecycleCard(playerId, cardInstanceId));
        }

        /// <summary>
        /// Says this player is finished with the current phase. The phase only
        /// moves on once everyone has said so - one player should not be able to
        /// skip the table past actions the others have not taken yet.
        /// </summary>
        [Rpc(SendTo.Server)]
        public void RequestSetReadyRpc(bool ready, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId =>
            {
                if (_game.SetReady(playerId, ready))
                {
                    AdvancePhase();
                }
            });
        }

        private void AdvancePhase()
        {
            _game.AdvancePhase();

            // A new draft needs its zone dealt before anyone can pick.
            if (_game.Phase == TurnPhase.Draft && _game.CurrentDrafterId == null)
            {
                _game.BeginDraft();
            }

            _phaseStartedAt = Time.time;
        }

        /// <summary>
        /// Server-side fallback for a player who has stepped away: after the
        /// timeout the phase advances whether or not everyone pressed Ready.
        /// The draft is exempt, since it is one player at a time and skipping a
        /// pick would leave the zone in a state the rules do not describe.
        /// </summary>
        private void Update()
        {
            if (!IsServer || _game == null)
            {
                return;
            }

            if (_game.Phase is TurnPhase.Draft or TurnPhase.GameOver)
            {
                return;
            }

            if (Time.time - _phaseStartedAt < GameSettings.PhaseTimeoutSeconds)
            {
                return;
            }

            AdvancePhase();
            BroadcastState();
        }

        /// <summary>
        /// Runs a rules operation on behalf of whoever sent the RPC, then tells
        /// everyone what the game looks like now. The rules engine throws on an
        /// illegal move, which becomes a message back to the one player who tried
        /// it rather than a crash or a desynced table.
        /// </summary>
        private void Apply(RpcParams rpcParams, Action<int> operation)
        {
            var senderId = rpcParams.Receive.SenderClientId;

            if (_game == null)
            {
                ReportError("The game has not started.", senderId);
                return;
            }

            var playerId = _seats.IndexOf(senderId);
            if (playerId < 0)
            {
                ReportError("You are not seated at this table.", senderId);
                return;
            }

            var phaseBefore = _game.Phase;
            var turnBefore = _game.TurnInRound;

            try
            {
                operation(playerId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                ReportError(exception.Message, senderId);
                return;
            }

            // The draft ends by someone taking the last pick rather than by a phase
            // advance, so the timer has to be restarted from whatever moved it.
            if (_game.Phase != phaseBefore || _game.TurnInRound != turnBefore)
            {
                _phaseStartedAt = Time.time;
            }

            BroadcastState();
        }

        // ------------------------------------------------------- State outbound

        private void BroadcastState()
        {
            foreach (var clientId in _seats)
            {
                SendStateTo(clientId);
            }
        }

        private void SendStateTo(ulong clientId)
        {
            var playerId = _seats.IndexOf(clientId);
            if (playerId < 0)
            {
                return;
            }

            SyncStateRpc(
                JsonUtility.ToJson(BuildViewFor(playerId)),
                RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void SyncStateRpc(string json, RpcParams rpcParams)
        {
            View = JsonUtility.FromJson<GameView>(json);
            LastError = null;
            Changed?.Invoke();
        }

        private void ReportError(string message, ulong clientId)
        {
            ShowErrorRpc(message, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void ShowErrorRpc(string message, RpcParams rpcParams)
        {
            LastError = message;
            Changed?.Invoke();
        }

        /// <summary>
        /// The game as one player is allowed to see it. Hands belonging to anyone
        /// else are reduced to a count.
        /// </summary>
        private GameView BuildViewFor(int viewerPlayerId)
        {
            var view = new GameView
            {
                viewerPlayerId = viewerPlayerId,
                phase = _game.Phase.ToString(),
                turnInRound = _game.TurnInRound,
                draftNumber = _game.DraftNumber,
                deckCount = _game.DeckCount,
                discardCount = _game.Discard.Count,
                currentDrafterId = _game.CurrentDrafterId ?? -1,
                winnerPlayerId = _game.Winner?.PlayerId ?? -1,
                diceRolled = _game.DiceRolled,
                highRollResourceClaimed = _game.HighRollResourceClaimed,
                playersReady = _game.PlayersReady.ToArray(),
                phaseSecondsRemaining = _game.Phase is TurnPhase.Draft or TurnPhase.GameOver
                    ? 0f
                    : Mathf.Max(0f, GameSettings.PhaseTimeoutSeconds - (Time.time - _phaseStartedAt)),
                draftZone = _game.DraftZone.Select(ToCardView).ToArray(),
                players = _game.Players.Select(player => new PlayerView
                {
                    playerId = player.PlayerId,
                    name = player.Name,
                    health = player.Health,
                    followers = player.Followers,
                    primaryDie = player.PrimaryDie,
                    isAlive = player.IsAlive,
                    red = player.Resources[ResourceColor.Red],
                    green = player.Resources[ResourceColor.Green],
                    blue = player.Resources[ResourceColor.Blue],
                    yellow = player.Resources[ResourceColor.Yellow],
                    handCount = player.Hand.Count,
                    collectedResources = _game.HasCollectedResources(player.PlayerId),
                    isReady = _game.PlayersReady.Contains(player.PlayerId),
                    hand = player.PlayerId == viewerPlayerId
                        ? player.Hand.Select(ToCardView).ToArray()
                        : Array.Empty<CardView>(),
                    compound = player.Compound.Select(ToCardView).ToArray()
                }).ToArray()
            };

            return view;
        }

        private static CardView ToCardView(CardInstance card)
        {
            return new CardView { instanceId = card.InstanceId, definitionId = card.Definition.Id };
        }
    }
}
