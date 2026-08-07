using System;
using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core
{
    /// <summary>
    /// The authoritative state of a single game of Indoctrination, and the rules
    /// operations that move it forward. Deliberately free of Unity and netcode
    /// types: the server owns one of these and replicates the results.
    /// </summary>
    public class GameState
    {
        private readonly Random _random;
        private readonly List<CardInstance> _deck = new();
        private readonly List<CardInstance> _discard = new();
        private readonly List<CardInstance> _draftZone = new();
        private readonly List<PlayerState> _players;

        private int _nextInstanceId;
        private int _draftPickIndex;
        private List<int> _draftOrder = new();

        // Turn-scoped flags, all cleared by AdvancePhase. Without these a player
        // can simply ask for the same free resource over and over.
        private bool _diceRolled;
        private bool _highRollClaimed;
        private readonly HashSet<int> _resourcesCollected = new();
        private readonly HashSet<int> _playersReady = new();

        public IReadOnlyList<PlayerState> Players => _players;
        public IReadOnlyList<CardInstance> DraftZone => _draftZone;
        public IReadOnlyList<CardInstance> Discard => _discard;
        public int DeckCount => _deck.Count;

        public TurnPhase Phase { get; private set; } = TurnPhase.Draft;

        /// <summary>Which of the three turns between drafts we are on (1-based).</summary>
        public int TurnInRound { get; private set; } = 1;

        /// <summary>How many drafts have happened, including the current one.</summary>
        public int DraftNumber { get; private set; }

        /// <summary>Index into Players of whoever drafts first this game.</summary>
        public int FirstDrafterIndex { get; set; }

        public GameState(IEnumerable<string> playerNames, IEnumerable<CardDefinition> cards, int randomSeed)
        {
            _players = playerNames
                .Select((name, index) => new PlayerState(index, name))
                .ToList();

            if (_players.Count < GameSettings.MinPlayers || _players.Count > GameSettings.MaxPlayers)
            {
                throw new ArgumentException(
                    $"Indoctrination supports {GameSettings.MinPlayers}-{GameSettings.MaxPlayers} players, got {_players.Count}.");
            }

            _random = new Random(randomSeed);

            foreach (var definition in cards)
            {
                for (var copy = 0; copy < definition.Count; copy++)
                {
                    _deck.Add(new CardInstance(_nextInstanceId++, definition));
                }
            }

            Shuffle(_deck);
        }

        public PlayerState GetPlayer(int playerId)
        {
            var player = _players.FirstOrDefault(p => p.PlayerId == playerId);
            if (player == null)
            {
                throw new ArgumentException($"No player with id {playerId}.", nameof(playerId));
            }

            return player;
        }

        // ---------------------------------------------------------------- Draft

        /// <summary>
        /// Fills the draft zone and works out the snake order for this draft.
        /// </summary>
        public void BeginDraft()
        {
            RequirePhase(TurnPhase.Draft);

            DraftNumber++;
            _draftPickIndex = 0;

            var zoneSize = GameSettings.DraftZoneSize(_players.Count);
            for (var i = 0; i < zoneSize; i++)
            {
                _draftZone.Add(DrawFromDeck());
            }

            _draftOrder = BuildSnakeOrder();
        }

        /// <summary>
        /// The snake draft order: the starting player's seat order reverses after
        /// every pass, e.g. A B C, C B A, A B C.
        /// </summary>
        private List<int> BuildSnakeOrder()
        {
            var seats = Enumerable.Range(0, _players.Count)
                .Select(offset => _players[(FirstDrafterIndex + offset) % _players.Count].PlayerId)
                .ToList();

            var picksAvailable = _draftZone.Count - GameSettings.UndraftedCardsDiscarded;
            var order = new List<int>(picksAvailable);

            for (var pass = 0; order.Count < picksAvailable; pass++)
            {
                var passOrder = pass % 2 == 0 ? seats : Enumerable.Reverse(seats);
                foreach (var playerId in passOrder)
                {
                    if (order.Count == picksAvailable)
                    {
                        break;
                    }

                    order.Add(playerId);
                }
            }

            return order;
        }

        /// <summary>Whose turn it is to draft, or null when the draft is over.</summary>
        public int? CurrentDrafterId =>
            _draftPickIndex < _draftOrder.Count ? _draftOrder[_draftPickIndex] : null;

        public void DraftCard(int playerId, int cardInstanceId)
        {
            RequirePhase(TurnPhase.Draft);

            if (CurrentDrafterId != playerId)
            {
                throw new InvalidOperationException($"It is not player {playerId}'s turn to draft.");
            }

            var card = _draftZone.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in the draft zone.", nameof(cardInstanceId));
            }

            _draftZone.Remove(card);
            GetPlayer(playerId).Hand.Add(card);
            _draftPickIndex++;

            if (CurrentDrafterId == null)
            {
                EndDraft();
            }
        }

        /// <summary>The last three cards go to the discard pile and play begins.</summary>
        private void EndDraft()
        {
            _discard.AddRange(_draftZone);
            _draftZone.Clear();

            TurnInRound = 1;
            Phase = TurnPhase.Rolling;
        }

        // -------------------------------------------------------------- Rolling

        /// <summary>
        /// Rolls every living player's primary die and returns the player who rolled
        /// the highest, or null if the highest roll was tied. That player takes one
        /// resource of their choice via <see cref="ClaimHighRollResource"/>.
        /// </summary>
        /// <summary>Whether this turn's dice have already been rolled.</summary>
        public bool DiceRolled => _diceRolled;

        public PlayerState RollPrimaryDice()
        {
            RequirePhase(TurnPhase.Rolling);

            if (_diceRolled)
            {
                throw new InvalidOperationException("The dice have already been rolled this turn.");
            }

            _diceRolled = true;

            foreach (var player in LivingPlayers)
            {
                player.SetPrimaryDie(_random.Next(1, GameSettings.DieSides + 1));
            }

            return HighestUniqueRoller();
        }

        private PlayerState HighestUniqueRoller()
        {
            var highest = LivingPlayers.Max(p => p.PrimaryDie);
            var tiedAtTop = LivingPlayers.Where(p => p.PrimaryDie == highest).ToList();
            return tiedAtTop.Count == 1 ? tiedAtTop[0] : null;
        }

        /// <summary>Whether the high roller has already taken their bonus resource.</summary>
        public bool HighRollResourceClaimed => _highRollClaimed;

        public void ClaimHighRollResource(int playerId, ResourceColor color)
        {
            RequirePhase(TurnPhase.Rolling);

            if (!_diceRolled)
            {
                throw new InvalidOperationException("Nobody has rolled yet.");
            }

            if (_highRollClaimed)
            {
                throw new InvalidOperationException("The high roll bonus has already been taken this turn.");
            }

            var winner = HighestUniqueRoller();
            if (winner == null || winner.PlayerId != playerId)
            {
                throw new InvalidOperationException($"Player {playerId} did not win the roll.");
            }

            _highRollClaimed = true;
            winner.Resources.Add(color);
        }

        /// <summary>
        /// Every die value rolled this turn. All players' units activate on these,
        /// so a value rolled by two players activates matching units twice.
        /// </summary>
        public IReadOnlyList<int> RolledValues => LivingPlayers.Select(p => p.PrimaryDie).ToList();

        // ------------------------------------------------------------- Resources

        /// <summary>Whether this player has already taken their free resources this turn.</summary>
        public bool HasCollectedResources(int playerId) => _resourcesCollected.Contains(playerId);

        /// <summary>Collects the player's free resources for the Resource phase, once per turn.</summary>
        public void CollectResources(int playerId, IReadOnlyList<ResourceColor> choices)
        {
            RequirePhase(TurnPhase.Resource);

            if (choices.Count != GameSettings.ResourcesPerTurn)
            {
                throw new ArgumentException(
                    $"Must choose exactly {GameSettings.ResourcesPerTurn} resources.", nameof(choices));
            }

            var player = GetPlayer(playerId);

            if (!_resourcesCollected.Add(playerId))
            {
                throw new InvalidOperationException("You have already collected resources this turn.");
            }

            foreach (var color in choices)
            {
                player.Resources.Add(color);
            }
        }

        // ------------------------------------------------------------------ Buy

        public void BuyCard(int playerId, int cardInstanceId)
        {
            RequirePhase(TurnPhase.Buy);

            var player = GetPlayer(playerId);
            var card = player.Hand.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in player {playerId}'s hand.");
            }

            player.Resources.Pay(card.Cost);
            player.Hand.Remove(card);

            if (card.Type == CardType.Ritual)
            {
                // Rituals are used once and gone; their effect resolves separately.
                _discard.Add(card);
            }
            else
            {
                player.Compound.Add(card);
            }
        }

        /// <summary>
        /// Discards a card from hand to gain one resource of that card's colour.
        /// </summary>
        public void RecycleCard(int playerId, int cardInstanceId)
        {
            RequirePhase(TurnPhase.Buy);

            var player = GetPlayer(playerId);
            var card = player.Hand.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in player {playerId}'s hand.");
            }

            player.Hand.Remove(card);
            _discard.Add(card);
            player.Resources.Add(card.Color);
        }

        // ---------------------------------------------------------------- Phases

        /// <summary>Players who have said they are finished with the current phase.</summary>
        public IReadOnlyCollection<int> PlayersReady => _playersReady;

        /// <summary>
        /// Whether every living player has finished. Dead players are not waited
        /// on, and neither is a table where everyone has already moved on.
        /// </summary>
        public bool AllPlayersReady =>
            LivingPlayers.All(player => _playersReady.Contains(player.PlayerId));

        /// <summary>
        /// Marks a player finished with this phase. Returns true once everyone is,
        /// which is the caller's cue to <see cref="AdvancePhase"/>. Toggling back to
        /// not-ready is allowed so a player can undo a misclick.
        /// </summary>
        public bool SetReady(int playerId, bool ready)
        {
            if (Phase is TurnPhase.Draft or TurnPhase.GameOver)
            {
                throw new InvalidOperationException($"The {Phase} phase does not wait on ready checks.");
            }

            // Confirms the player exists.
            GetPlayer(playerId);

            if (ready)
            {
                _playersReady.Add(playerId);
            }
            else
            {
                _playersReady.Remove(playerId);
            }

            return AllPlayersReady;
        }

        /// <summary>
        /// Moves to the next phase, looping Rolling -> Activation -> Resource -> Buy
        /// for three turns before returning to the draft.
        ///
        /// Deliberately not gated on <see cref="AllPlayersReady"/>: the caller
        /// decides whether everyone agreed or the phase timer ran out, and both
        /// need to land here.
        /// </summary>
        public void AdvancePhase()
        {
            if (Phase == TurnPhase.GameOver)
            {
                throw new InvalidOperationException("The game is over.");
            }

            if (CheckForGameOver())
            {
                return;
            }

            _playersReady.Clear();

            Phase = Phase switch
            {
                TurnPhase.Draft => TurnPhase.Rolling,
                TurnPhase.Rolling => TurnPhase.Activation,
                TurnPhase.Activation => TurnPhase.Resource,
                TurnPhase.Resource => TurnPhase.Buy,
                TurnPhase.Buy => EndOfTurn(),
                _ => throw new InvalidOperationException($"Cannot advance from {Phase}.")
            };
        }

        private TurnPhase EndOfTurn()
        {
            _diceRolled = false;
            _highRollClaimed = false;
            _resourcesCollected.Clear();

            if (TurnInRound < GameSettings.TurnsPerRound)
            {
                TurnInRound++;
                return TurnPhase.Rolling;
            }

            return TurnPhase.Draft;
        }

        // ------------------------------------------------------------- Game over

        public IEnumerable<PlayerState> LivingPlayers => _players.Where(p => p.IsAlive);

        /// <summary>
        /// The game ends when someone reaches the follower target or is the last
        /// leader standing.
        /// </summary>
        public PlayerState Winner
        {
            get
            {
                var byFollowers = _players.FirstOrDefault(p => p.HasWon);
                if (byFollowers != null)
                {
                    return byFollowers;
                }

                var survivors = LivingPlayers.ToList();
                return survivors.Count == 1 ? survivors[0] : null;
            }
        }

        private bool CheckForGameOver()
        {
            if (Winner == null)
            {
                return false;
            }

            Phase = TurnPhase.GameOver;
            return true;
        }

        // ------------------------------------------------------------- Card flow

        private CardInstance DrawFromDeck()
        {
            if (_deck.Count == 0)
            {
                ReshuffleDiscardIntoDeck();
            }

            var card = _deck[^1];
            _deck.RemoveAt(_deck.Count - 1);
            return card;
        }

        private void ReshuffleDiscardIntoDeck()
        {
            if (_discard.Count == 0)
            {
                throw new InvalidOperationException("No cards left in the deck or discard pile.");
            }

            _deck.AddRange(_discard);
            _discard.Clear();
            Shuffle(_deck);
        }

        private void Shuffle(List<CardInstance> cards)
        {
            for (var i = cards.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (cards[i], cards[j]) = (cards[j], cards[i]);
            }
        }

        private void RequirePhase(TurnPhase expected)
        {
            if (Phase != expected)
            {
                throw new InvalidOperationException($"Expected the {expected} phase, but the game is in {Phase}.");
            }
        }
    }
}
