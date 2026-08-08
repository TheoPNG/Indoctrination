using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
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

        /// <summary>
        /// Time.time when the open card question was put, or -1 when none is.
        /// A player who drops mid-question would otherwise stop the table
        /// permanently, since nothing may happen while a card is waiting.
        /// </summary>
        private float _choiceStartedAt = -1f;

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

        /// <summary>
        /// Seats a player with no client behind them, so a test can fill a table
        /// without a second process. The seat takes a client id that no real
        /// connection can hold, and so is never sent a view - it exists purely to
        /// make the table big enough to start.
        /// </summary>
        public void AddTestSeat(string name)
        {
            if (_game != null || _seats.Count >= GameSettings.MaxPlayers)
            {
                return;
            }

            var placeholderId = ulong.MaxValue - (ulong)_seats.Count;
            _seats.Add(placeholderId);
            _names[placeholderId] = name;
            BroadcastLobby();
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

            StartGame(rpcParams.Receive.SenderClientId);
        }

        private void StartGame(ulong requestedBy)
        {
            if (_seats.Count < GameSettings.MinPlayers)
            {
                ReportError($"Need at least {GameSettings.MinPlayers} players to start.", requestedBy);
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

        /// <summary>
        /// Starts a fresh game with everyone still seated. Only offered once the
        /// current one has finished - this is "play again", not a reset button
        /// somebody can pull on a game they are losing.
        /// </summary>
        [Rpc(SendTo.Server)]
        public void RequestPlayAgainRpc(RpcParams rpcParams = default)
        {
            if (rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId)
            {
                return;
            }

            if (_game == null || _game.Phase != TurnPhase.GameOver)
            {
                ReportError("The game is still going.", rpcParams.Receive.SenderClientId);
                return;
            }

            _game = null;
            StartGame(rpcParams.Receive.SenderClientId);
        }

        [Rpc(SendTo.Server)]
        public void RequestDraftRpc(int cardInstanceId, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.DraftCard(playerId, cardInstanceId));
        }

        [Rpc(SendTo.Server)]
        public void RequestRollRpc(RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.RollPrimaryDie(playerId));
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

        [Rpc(SendTo.Server)]
        public void RequestAnswerPlayerRpc(int chosenPlayerId, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.AnswerPlayerChoice(playerId, chosenPlayerId));
        }

        [Rpc(SendTo.Server)]
        public void RequestAnswerCardRpc(int chosenCardInstanceId, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.AnswerCardChoice(playerId, chosenCardInstanceId));
        }

        [Rpc(SendTo.Server)]
        public void RequestAnswerColorRpc(int color, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.AnswerColorChoice(playerId, (ResourceColor)color));
        }

        [Rpc(SendTo.Server)]
        public void RequestAnswerYesNoRpc(bool yes, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.AnswerYesNo(playerId, yes));
        }

        [Rpc(SendTo.Server)]
        public void RequestAnswerAmountRpc(int amount, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.AnswerAmount(playerId, amount));
        }

        [Rpc(SendTo.Server)]
        public void RequestAnswerOptionRpc(string option, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.AnswerOptionChoice(playerId, option));
        }

        [Rpc(SendTo.Server)]
        public void RequestRerollRpc(RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.RerollPrimaryDie(playerId));
        }

        [Rpc(SendTo.Server)]
        public void RequestBuyMealCounterRpc(int cardInstanceId, int[] colors, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId =>
                _game.BuyMealCounter(playerId, cardInstanceId, colors.Select(c => (ResourceColor)c).ToList()));
        }

        [Rpc(SendTo.Server)]
        public void RequestSpendSchemeCounterRpc(int targetPlayerId, int dieValue, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId => _game.SpendSchemeCounter(playerId, targetPlayerId, dieValue));
        }

        private void AdvancePhase()
        {
            // The rules engine deals each new draft itself as the turn loop comes
            // back around, so there is nothing to arrange here.
            _game.AdvancePhase();
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

            if (_game.PendingChoice != null)
            {
                // The phase clock does not run while a card is waiting - nobody
                // else can act anyway. The question gets its own clock instead,
                // and answers itself if whoever was asked has gone quiet.
                _phaseStartedAt = Time.time;

                if (_choiceStartedAt < 0f)
                {
                    _choiceStartedAt = Time.time;
                }
                else if (Time.time - _choiceStartedAt >= GameSettings.ChoiceTimeoutSeconds)
                {
                    _game.AnswerPendingChoiceWithDefault();
                    _choiceStartedAt = _game.PendingChoice == null ? -1f : Time.time;
                    BroadcastState();
                }

                return;
            }

            _choiceStartedAt = -1f;

            // Draft picks do not time out, but draft-related card choices do.
            // This check must stay below PendingChoice or those choices reach
            // zero seconds and can never answer themselves.
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
            var hadPendingChoice = _game.PendingChoice != null;

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
            // advance, so the timer has to be restarted from whatever moved it. An
            // answered choice restarts it too - the player should not lose time to
            // a question a card interrupted them with.
            if (_game.Phase != phaseBefore || _game.TurnInRound != turnBefore
                || (hadPendingChoice && _game.PendingChoice == null))
            {
                _phaseStartedAt = Time.time;
            }

            // A new question starts its own clock rather than inheriting the
            // remaining time of the one just answered.
            if (hadPendingChoice != (_game.PendingChoice != null) || !hadPendingChoice)
            {
                _choiceStartedAt = _game.PendingChoice == null ? -1f : Time.time;
            }

            BroadcastState();
        }

        // ------------------------------------------------------- State outbound

        private void BroadcastState()
        {
            // Every outbound choice needs a running server clock before its view
            // is built. This also covers choices opened by a timeout-driven phase
            // advance rather than by an RPC operation.
            if (_game.PendingChoice != null && _choiceStartedAt < 0f)
            {
                _choiceStartedAt = Time.time;
            }
            else if (_game.PendingChoice == null)
            {
                _choiceStartedAt = -1f;
            }

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

            // A seat can outlive its connection - a player who dropped keeps their
            // board in case they come back, and tests seat opponents with no
            // client at all. Netcode has nowhere to deliver either.
            if (!NetworkManager.ConnectedClients.ContainsKey(clientId))
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
            if (!NetworkManager.ConnectedClients.ContainsKey(clientId))
            {
                return;
            }

            ShowErrorRpc(message, RpcTarget.Single(clientId, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void ShowErrorRpc(string message, RpcParams rpcParams)
        {
            LastError = message;
            Changed?.Invoke();
        }

        /// <summary>
        /// The game as one player is allowed to see it, with the clocks filled in
        /// from this machine's frame time. The filtering itself lives in
        /// <see cref="GameViewBuilder"/>, away from anything Unity-specific, so it
        /// can be proved correct outside the Editor.
        /// </summary>
        private GameView BuildViewFor(int viewerPlayerId)
        {
            var phaseRemaining = _game.Phase is TurnPhase.Draft or TurnPhase.GameOver
                ? 0f
                : Mathf.Max(0f, GameSettings.PhaseTimeoutSeconds - (Time.time - _phaseStartedAt));

            var choiceRemaining = _game.PendingChoice == null || _choiceStartedAt < 0f
                ? 0f
                : Mathf.Max(0f, GameSettings.ChoiceTimeoutSeconds - (Time.time - _choiceStartedAt));

            return GameViewBuilder.Build(_game, viewerPlayerId, phaseRemaining, choiceRemaining);
        }
    }
}
