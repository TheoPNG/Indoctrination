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

        /// <summary>
        /// One seat at the table. The index into <see cref="_seats"/> is the Core
        /// player id, and stays fixed for the whole game - the connection sitting
        /// in it can come and go without the board behind it going anywhere.
        /// </summary>
        private class Seat
        {
            /// <summary>The Netcode client here now, or null if nobody is.</summary>
            public ulong? ClientId;

            public string Name;

            public bool IsOccupied => ClientId.HasValue;
        }

        private readonly List<Seat> _seats = new();

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
                TakeSeat(clientId);
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
            TakeSeat(clientId);
            BroadcastLobby();

            // A reconnecting or late-spawning client needs the current state.
            if (_game != null)
            {
                SendStateTo(clientId);
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            var seat = SeatIndexOf(clientId);
            if (seat < 0)
            {
                BroadcastLobby();
                return;
            }

            if (_game == null)
            {
                // Nothing has been built yet, so the seat goes with them.
                _seats.RemoveAt(seat);
            }
            else
            {
                // Mid-game the seat and its board are kept, and the next client
                // through the door takes it over. Netcode issues a fresh client
                // id on every connection, so a player who crashes and relaunches
                // cannot be recognised as themselves - claiming the empty seat is
                // what lets them back into their own game at all.
                _seats[seat].ClientId = null;
            }

            BroadcastLobby();
        }

        private int SeatIndexOf(ulong clientId) =>
            _seats.FindIndex(seat => seat.ClientId == clientId);

        /// <summary>
        /// Seats a client: their existing seat, an empty one left by somebody who
        /// dropped, or a new one if the game has not started. -1 if the table is
        /// full and every seat is occupied.
        /// </summary>
        private int TakeSeat(ulong clientId)
        {
            var existing = SeatIndexOf(clientId);
            if (existing >= 0)
            {
                return existing;
            }

            var vacant = _seats.FindIndex(seat => !seat.IsOccupied);
            if (vacant >= 0)
            {
                _seats[vacant].ClientId = clientId;
                return vacant;
            }

            if (_game != null || _seats.Count >= GameSettings.MaxPlayers)
            {
                return -1;
            }

            _seats.Add(new Seat { ClientId = clientId, Name = $"Leader {_seats.Count + 1}" });
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

            // No connection behind it, so it is never sent a view - it exists
            // purely to make the table big enough to start.
            _seats.Add(new Seat { ClientId = null, Name = name });
            BroadcastLobby();
        }

        private void BroadcastLobby()
        {
            var lobby = new LobbyView
            {
                playerNames = _seats.Select(seat => seat.Name).ToArray(),
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

            var names = _seats.Select(seat => seat.Name).ToList();
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
            Apply(rpcParams, playerId =>
            {
                _game.RollPrimaryDie(playerId);
                FinishPhaseIfNothingLeftToDo(playerId);
            });
        }

        [Rpc(SendTo.Server)]
        public void RequestClaimHighRollResourceRpc(int color, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId =>
            {
                _game.ClaimHighRollResource(playerId, (ResourceColor)color);
                FinishPhaseIfNothingLeftToDo(playerId);
            });
        }

        [Rpc(SendTo.Server)]
        public void RequestCollectResourcesRpc(int[] colors, RpcParams rpcParams = default)
        {
            Apply(rpcParams, playerId =>
            {
                _game.CollectResources(playerId, colors.Select(c => (ResourceColor)c).ToList());
                FinishPhaseIfNothingLeftToDo(playerId);
            });
        }

        /// <summary>
        /// Marks a player finished once they have done everything the current
        /// phase actually asks of them, so picking your resources is the whole
        /// interaction rather than picking them and then confirming it.
        ///
        /// Only phases with a definite "done" state qualify. The Buy phase has no
        /// natural end - a player may buy nothing, or keep buying - so it still
        /// waits for them to say so.
        /// </summary>
        private void FinishPhaseIfNothingLeftToDo(int playerId)
        {
            var everyoneReady = false;

            switch (_game.Phase)
            {
                // SetReady refuses a Rolling ready-up until every die is down, so
                // this can only run once the last one lands. At that point the
                // only thing still owed is the high roll bonus, and at most one
                // player can be owed it.
                case TurnPhase.Rolling when _game.DiceRolled:
                    foreach (var player in _game.LivingPlayers.ToList())
                    {
                        if (!IsOwedTheHighRollBonus(player.PlayerId))
                        {
                            everyoneReady |= _game.SetReady(player.PlayerId, true);
                        }
                    }

                    break;

                case TurnPhase.Resource when _game.HasCollectedResources(playerId):
                    everyoneReady = _game.SetReady(playerId, true);
                    break;
            }

            if (everyoneReady)
            {
                AdvancePhase();
            }
        }

        /// <summary>
        /// Whether this player still has the high roll bonus to take. They must
        /// not be marked finished before claiming it, or the phase could move on
        /// and take the bonus with it.
        /// </summary>
        private bool IsOwedTheHighRollBonus(int playerId)
        {
            if (_game.HighRollResourceClaimed || !_game.DiceRolled)
            {
                return false;
            }

            var living = _game.LivingPlayers.ToList();
            var highest = living.Max(player => player.PrimaryDie);
            var tiedAtTop = living.Where(player => player.PrimaryDie == highest).ToList();

            return tiedAtTop.Count == 1 && tiedAtTop[0].PlayerId == playerId;
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

        /// <summary>
        /// Drafts the first card whoever is holding things up could legally have
        /// taken. The rules engine decides what is legal, so a reserved or
        /// blocked card is never handed out by mistake.
        /// </summary>
        private void TakeDraftPickForAbsentPlayer()
        {
            if (_game.CurrentDrafterId is not int drafter)
            {
                return;
            }

            foreach (var card in _game.DraftZone.ToList())
            {
                try
                {
                    _game.DraftCard(drafter, card.InstanceId);
                    _phaseStartedAt = Time.time;
                    BroadcastState();
                    return;
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    // Blocked by Games, or somebody else's Parking Spot. Try the next.
                }
            }
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
        /// timeout the phase advances whether or not everyone pressed Ready, and
        /// a draft pick nobody makes is taken for them.
        ///
        /// The draft used to be exempt on the grounds that skipping a pick would
        /// leave the zone in a state the rules could not describe. That left the
        /// worst kind of stuck: the draft is the one phase with no clock, so a
        /// single player who never picks holds the whole table forever. Taking
        /// the pick for them keeps the zone legal and the game moving.
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

            // Nothing in Activation is a player's move - the dice already decided
            // everything and the effects resolve themselves. Once they have, the
            // phase closes on its own, after a beat long enough to watch it happen.
            if (_game.Phase == TurnPhase.Activation && !_game.HasEffectsPending)
            {
                if (Time.time - _phaseStartedAt >= GameSettings.ActivationDwellSeconds)
                {
                    AdvancePhase();
                    BroadcastState();
                }

                return;
            }

            // A draft pick nobody makes is taken for them once the clock runs
            // out. This check must stay below PendingChoice, or draft-related
            // card questions reach zero seconds and can never answer themselves.
            if (_game.Phase == TurnPhase.Draft)
            {
                if (Time.time - _phaseStartedAt >= GameSettings.PhaseTimeoutSeconds)
                {
                    TakeDraftPickForAbsentPlayer();
                }

                return;
            }

            if (_game.Phase == TurnPhase.GameOver)
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

            var playerId = SeatIndexOf(senderId);
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

            foreach (var seat in _seats.Where(seat => seat.IsOccupied))
            {
                SendStateTo(seat.ClientId.Value);
            }
        }

        private void SendStateTo(ulong clientId)
        {
            var playerId = SeatIndexOf(clientId);
            if (playerId < 0)
            {
                return;
            }

            // A seat can outlive its connection, and Netcode has nowhere to
            // deliver to a client that is no longer there.
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
