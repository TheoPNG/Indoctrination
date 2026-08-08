using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core.Effects;

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

        /// <summary>
        /// Marks the draft Blessings put on cards this draft, and who put them
        /// there. Cleared and re-chosen at the start of every draft, so a card
        /// reserved last time is fair game this time.
        /// </summary>
        private readonly Dictionary<DraftMarker, (int CardInstanceId, int PlayerId)> _draftMarks = new();

        // Turn-scoped flags, all cleared by AdvancePhase. Without these a player
        // can simply ask for the same free resource over and over.
        private bool _diceRolled;
        private bool _highRollClaimed;
        private readonly HashSet<int> _resourcesCollected = new();
        private readonly HashSet<int> _playersReady = new();

        // Card effects waiting to run, oldest first. See ResolveEffects.
        private readonly Queue<PendingEffect> _effectQueue = new();
        private PendingEffect _resolving;

        /// <summary>
        /// Set when the Buy phase ends: the turn cannot actually close until the
        /// end-of-turn Blessings have finished, and those can stop to ask questions.
        /// </summary>
        private bool _endOfTurnPending;

        /// <summary>
        /// Cards with a "once a turn" clause of their own - Being of Heartlessness,
        /// Suspicious Chef, the reroll Blessings. Keyed by whatever the card wants
        /// to limit, usually its own instance id. Emptied at the end of the turn.
        /// </summary>
        private readonly HashSet<string> _oncePerTurn = new();

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
            _draftMarks.Clear();

            // Overzealous jumps its owner to the front of the queue. If two people
            // have it the seat order between them is untouched, so it stays fair.
            var eager = _players.FirstOrDefault(p => p.IsAlive && p.HasInPlay(CardIds.Overzealous));
            if (eager != null)
            {
                FirstDrafterIndex = _players.IndexOf(eager);
            }

            var zoneSize = GameSettings.DraftZoneSize(_players.Count);
            for (var i = 0; i < zoneSize; i++)
            {
                _draftZone.Add(DrawFromDeck());
            }

            _draftOrder = BuildSnakeOrder();

            QueueDraftSetupTriggers();
            ResolveEffects();
        }

        /// <summary>
        /// The three Blessings that mark a card before the first pick. They run in
        /// a fixed order so the table sees the same sequence every draft: what is
        /// unbuyable is settled first, then what is reserved, then what is trapped -
        /// which means the trapper picks last and with full information.
        /// </summary>
        private void QueueDraftSetupTriggers()
        {
            QueueForEachHolder(CardIds.BlockedByGames, BlessingEffects.BlockedByGames);
            QueueForEachHolder(CardIds.CultLeaderSParkingSpot, BlessingEffects.CultLeadersParkingSpot);
            QueueForEachHolder(CardIds.HumanTrap, BlessingEffects.HumanTrap);
        }

        private void QueueForEachHolder(string definitionId, EffectRoutine routine)
        {
            foreach (var player in LivingPlayers.ToList())
            {
                var card = player.FindInPlay(definitionId);
                if (card != null)
                {
                    EnqueueEffect(card, player, routine, card.Title);
                }
            }
        }

        /// <summary>
        /// Every draft marker currently set. All three draft Blessings mark their
        /// card in the open - nothing about them is hidden information - so this
        /// is safe to put straight into the public game view.
        /// </summary>
        public IReadOnlyDictionary<DraftMarker, (int CardInstanceId, int PlayerId)> DraftMarks => _draftMarks;

        /// <summary>
        /// The card carrying a draft marker this draft, or null if nobody set one.
        /// </summary>
        public CardInstance MarkedInDraft(DraftMarker marker)
        {
            return _draftMarks.TryGetValue(marker, out var mark)
                ? _draftZone.FirstOrDefault(c => c.InstanceId == mark.CardInstanceId)
                : null;
        }

        /// <summary>Who set a draft marker, or -1 if it is not set.</summary>
        public int MarkedInDraftBy(DraftMarker marker) =>
            _draftMarks.TryGetValue(marker, out var mark) ? mark.PlayerId : -1;

        public void MarkInDraft(DraftMarker marker, int cardInstanceId, int playerId)
        {
            if (_draftZone.All(c => c.InstanceId != cardInstanceId))
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in the draft zone.");
            }

            _draftMarks[marker] = (cardInstanceId, playerId);
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
            RequireNoPendingChoice();

            if (CurrentDrafterId != playerId)
            {
                throw new InvalidOperationException($"It is not player {playerId}'s turn to draft.");
            }

            var card = _draftZone.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in the draft zone.", nameof(cardInstanceId));
            }

            if (MarkedInDraft(DraftMarker.Blocked) == card)
            {
                throw new InvalidOperationException("Blocked by Games has turned that card over. Nobody can take it.");
            }

            if (MarkedInDraft(DraftMarker.Reserved) == card
                && MarkedInDraftBy(DraftMarker.Reserved) != playerId)
            {
                throw new InvalidOperationException("That card is in the Cult Leader's Parking Spot.");
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
            // The trap is checked the moment the last pick is taken. All Parts of
            // the Animal rummages through the leftovers afterwards, and salvaging
            // a card off the discard heap is not the same as drafting it.
            SpringHumanTrap();

            // All Parts of the Animal picks over the leftovers before they go.
            foreach (var player in LivingPlayers.ToList())
            {
                if (_draftZone.Count == 0 || !player.HasInPlay(CardIds.AllPartsOfTheAnimal))
                {
                    continue;
                }

                var scrap = _draftZone[_random.Next(_draftZone.Count)];
                _draftZone.Remove(scrap);
                player.Hand.Add(scrap);
            }

            _discard.AddRange(_draftZone);
            _draftZone.Clear();

            TurnInRound = 1;
            Phase = TurnPhase.Rolling;
            QueueStartOfTurnTriggers();
            ResolveEffects();
        }

        /// <summary>
        /// Human Trap pays out if the card it marked is still sitting in the zone
        /// when the picks run out. No choice of victim here - the card says all
        /// opponents, so it hits all of them.
        /// </summary>
        private void SpringHumanTrap()
        {
            var trapped = MarkedInDraft(DraftMarker.Trapped);
            if (trapped == null)
            {
                return;
            }

            var trapper = _players.FirstOrDefault(p => p.PlayerId == MarkedInDraftBy(DraftMarker.Trapped));
            if (trapper == null || !trapper.IsAlive)
            {
                return;
            }

            EnqueueEffect(
                trapper.FindInPlay(CardIds.HumanTrap),
                trapper,
                CommonEffects.DamageAllOpponents(GameSettings.HumanTrapDamage),
                $"Human Trap - {trapped.Title} went undrafted");
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

                // Standardized Uniforms buys a die nobody else's units answer to.
                if (player.HasInPlay(CardIds.StandardizedUniforms))
                {
                    AddPrivateDie(player);
                }
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

        /// <summary>
        /// How many free resources this player takes each turn. Resourceful is the
        /// only thing that changes it.
        /// </summary>
        public int ResourcesPerTurnFor(int playerId) =>
            GameSettings.ResourcesPerTurn + (GetPlayer(playerId).HasInPlay(CardIds.Resourceful) ? 1 : 0);

        /// <summary>Collects the player's free resources for the Resource phase, once per turn.</summary>
        public void CollectResources(int playerId, IReadOnlyList<ResourceColor> choices)
        {
            RequirePhase(TurnPhase.Resource);

            var allowance = ResourcesPerTurnFor(playerId);
            if (choices.Count != allowance)
            {
                throw new ArgumentException(
                    $"Must choose exactly {allowance} resources.", nameof(choices));
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

        /// <summary>
        /// What this card actually costs this player right now. The stone
        /// Blessings shave a resource off, and Belle of the Ball gets cheaper the
        /// more units you already have out.
        /// </summary>
        public CardCost CostFor(PlayerState player, CardInstance card)
        {
            var cost = card.Cost;
            if (cost.IsSpecial)
            {
                return cost;
            }

            var unitsOnly = card.Type == CardType.Unit;

            // "all cards in your hand"
            if (player.HasInPlay(CardIds.Mindstone))
            {
                cost = cost.Reduced(ResourceColor.Blue, 1);
            }

            if (player.HasInPlay(CardIds.Shieldstone))
            {
                cost = cost.Reduced(ResourceColor.Green, 1);
            }

            // "all Units in your hand"
            if (unitsOnly)
            {
                if (player.HasInPlay(CardIds.Bloodstone))
                {
                    cost = cost.Reduced(ResourceColor.Red, 1);
                }

                if (player.HasInPlay(CardIds.Wealthstone))
                {
                    cost = cost.Reduced(ResourceColor.Yellow, 1);
                }

                if (player.HasInPlay(CardIds.CursedMindstone))
                {
                    cost = cost.Reduced(ResourceColor.Blue, 1);
                }

                if (player.HasInPlay(CardIds.CursedShieldstone))
                {
                    cost = cost.Reduced(ResourceColor.Green, 1);
                }

                if (player.HasInPlay(CardIds.CursedBloodstone))
                {
                    cost = cost.Reduced(ResourceColor.Red, 1);
                }

                if (player.HasInPlay(CardIds.CursedWealthstone))
                {
                    cost = cost.Reduced(ResourceColor.Yellow, 1);
                }
            }

            if (card.Definition.Id == CardIds.BelleOfTheBall)
            {
                cost = cost.Reduced(ResourceColor.Red, player.Compound.Count(c => c.Type == CardType.Unit));
            }

            return cost;
        }

        public void BuyCard(int playerId, int cardInstanceId)
        {
            RequirePhase(TurnPhase.Buy);
            RequireNoPendingChoice();

            var player = GetPlayer(playerId);
            var card = player.Hand.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in player {playerId}'s hand.");
            }

            if (card.Cost.IsSpecial)
            {
                BuyForSpecialCost(player, card);
                ResolveEffects();
                return;
            }

            player.Resources.Pay(CostFor(player, card));
            player.Hand.Remove(card);

            if (card.Type == CardType.Ritual)
            {
                // Rituals are used once and gone. The card goes to the discard
                // straight away so effects that read the discard - Worshiper of the
                // Bone God, The Second Coming - can see it.
                _discard.Add(card);
                PlayRitual(card, player);
            }
            else
            {
                player.Compound.Add(card);
                EnqueueEffect(card, player, CardEffects.OnEnterPlay(card.Definition.Id), $"{card.Title} enters play");
            }

            ResolveEffects();
        }

        /// <summary>
        /// A card whose printed cost is "*" is paid for with things rather than
        /// resources. It Who Consumes is the only one. The bill is checked here
        /// and collected by the queued effect, so a player who cannot pay is
        /// turned away before the card leaves their hand.
        /// </summary>
        private void BuyForSpecialCost(PlayerState player, CardInstance card)
        {
            if (card.Definition.Id != CardIds.ItWhoConsumes)
            {
                throw new InvalidOperationException($"{card.Title} has no rules for its special cost yet.");
            }

            if (!player.Compound.Any(c => c.Type == CardType.Unit))
            {
                throw new InvalidOperationException("It Who Consumes needs a Unit to sacrifice.");
            }

            if (!player.Compound.Any(c => c.Type == CardType.Blessing))
            {
                throw new InvalidOperationException("It Who Consumes needs a Blessing to sacrifice.");
            }

            if (!player.Hand.Any(c => c.Type == CardType.Ritual && c != card))
            {
                throw new InvalidOperationException("It Who Consumes needs a Ritual in hand to activate.");
            }

            player.Hand.Remove(card);
            player.Compound.Add(card);

            EnqueueEffect(card, player, UnitEffects.ItWhoConsumesCost, "It Who Consumes - paying the cost");
            EnqueueEffect(card, player, CardEffects.OnEnterPlay(card.Definition.Id), $"{card.Title} enters play");
        }

        /// <summary>
        /// Resolves a Ritual, plus the two cards that care about Rituals being
        /// used: Ritualist counts them, and Chief Sacrificer makes one repeat.
        ///
        /// It Who Consumes activates a Ritual whose "effect is null", which is
        /// still an activation - Ritualist counts it - so the effect is what gets
        /// suppressed rather than the whole act of playing it.
        /// </summary>
        public void PlayRitual(CardInstance ritual, PlayerState player, bool runEffect = true)
        {
            foreach (var ritualist in player.Compound.Where(c => c.Definition.Id == CardIds.Ritualist).ToList())
            {
                ritualist.AddCounter(Counters.Ritual, EffectModifiers.ModifyCounterGain(this, player, 1));
            }

            var repeats = 1 + player.GetCounter(Counters.RitualEcho);
            player.ClearCounter(Counters.RitualEcho);

            if (!runEffect)
            {
                return;
            }

            for (var i = 0; i < repeats; i++)
            {
                EnqueueEffect(ritual, player, CardEffects.For(ritual.Definition.Id, 0), ritual.Title);
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

            // Being of Heartlessness eats one recycled card per turn, no more.
            foreach (var being in player.Compound
                         .Where(c => c.Definition.Id == CardIds.BeingOfHearthlessness).ToList())
            {
                if (TakeOncePerTurn($"trash:{being.InstanceId}"))
                {
                    being.AddCounter(Counters.Trash, EffectModifiers.ModifyCounterGain(this, player, 1));
                }
            }
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

            if (PendingChoice != null)
            {
                throw new InvalidOperationException("A card is still waiting on a decision.");
            }

            if (CheckForGameOver())
            {
                return;
            }

            _playersReady.Clear();

            switch (Phase)
            {
                case TurnPhase.Draft:
                    Phase = TurnPhase.Rolling;
                    QueueStartOfTurnTriggers();
                    break;

                case TurnPhase.Rolling:
                    Phase = TurnPhase.Activation;
                    QueueActivations();
                    break;

                case TurnPhase.Activation:
                    Phase = TurnPhase.Resource;
                    break;

                case TurnPhase.Resource:
                    Phase = TurnPhase.Buy;
                    break;

                case TurnPhase.Buy:
                    // The turn does not close here. End-of-turn Blessings look back
                    // at this turn's tallies, and some of them stop to ask who to
                    // hit, so the tallies have to survive until the queue drains.
                    EffectModifiers.QueueEndOfTurnTriggers(this);
                    _endOfTurnPending = true;
                    break;

                default:
                    throw new InvalidOperationException($"Cannot advance from {Phase}.");
            }

            ResolveEffects();
        }

        private void EndOfTurn()
        {
            _endOfTurnPending = false;
            _diceRolled = false;
            _highRollClaimed = false;
            _resourcesCollected.Clear();
            _playersReady.Clear();
            _oncePerTurn.Clear();

            foreach (var player in _players)
            {
                // Titanstopper is the one thing that keeps Block across the break.
                if (!player.HasInPlay(CardIds.TitanstopperChurchOfWalls))
                {
                    player.ClearBlock();
                }

                // Flame counters from Arsonist and Fire Breather burn one point
                // and go out.
                var flames = player.GetCounter(Counters.Flame);
                if (flames > 0 && player.IsAlive)
                {
                    DealDamage(null, player, 1);
                    player.AddCounter(Counters.Flame, -1);
                }

                player.EndTurn();
            }

            if (CheckForGameOver())
            {
                return;
            }

            if (TurnInRound < GameSettings.TurnsPerRound)
            {
                TurnInRound++;
                Phase = TurnPhase.Rolling;
                QueueStartOfTurnTriggers();
                return;
            }

            Phase = TurnPhase.Draft;
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

        // -------------------------------------------------- Effect building blocks

        /// <summary>
        /// The one route damage takes. Block, damage modifiers, and the tallies
        /// cards ask about all live here, so nothing can quietly bypass them by
        /// calling PlayerState.TakeDamage itself.
        /// </summary>
        public void DealDamage(PlayerState source, PlayerState target, int amount)
        {
            if (target == null || amount <= 0 || !target.IsAlive)
            {
                return;
            }

            amount = EffectModifiers.ModifyDamage(this, source, target, amount);
            if (amount <= 0)
            {
                return;
            }

            var healthBefore = target.Health;
            target.TakeDamage(amount);
            source?.RecordDamageDealt(amount);

            EffectModifiers.AfterDamage(this, source, target, amount, healthBefore - target.Health);
        }

        /// <summary>
        /// Healing of zero is still healing: Alternative Medicine's "heal 0 health"
        /// exists so Wondrous Blood can turn it into one, so a zero is passed
        /// through the modifiers rather than short-circuited.
        /// </summary>
        public void Heal(PlayerState player, int amount)
        {
            if (player == null || amount < 0)
            {
                return;
            }

            player.Heal(EffectModifiers.ModifyHealing(this, player, amount));
        }

        /// <summary>
        /// Follower gains and losses go through here because Clown Cult turns each
        /// one into the other, which only works if there is a single place to do it.
        /// </summary>
        public void ChangeFollowers(PlayerState player, int amount)
        {
            if (player == null || amount == 0)
            {
                return;
            }

            amount = EffectModifiers.ModifyFollowerChange(this, player, amount);

            if (amount > 0)
            {
                player.GainFollowers(amount);
                EffectModifiers.AfterFollowersGained(this, player, amount);
            }
            else
            {
                player.LoseFollowers(-amount);
            }
        }

        /// <summary>
        /// Health spent rather than health lost. Block does not stop it and it
        /// does not count as damage taken - Radical Tactics is a bargain, not a wound.
        /// </summary>
        public void LoseHealth(PlayerState player, int amount)
        {
            player?.LoseHealth(amount);
        }

        /// <summary>Removes a player from the game outright. Ascension's forfeit.</summary>
        public void Kill(PlayerState player)
        {
            player?.LoseHealth(player.Health);
        }

        /// <summary>
        /// Puts a card into play without anybody paying for it - It Who Consumes
        /// and Revive the Forgotten both do this.
        /// </summary>
        public void PlayCardForFree(CardInstance card, PlayerState player)
        {
            if (card == null || player == null)
            {
                return;
            }

            if (card.Type == CardType.Ritual)
            {
                _discard.Add(card);
                PlayRitual(card, player);
                return;
            }

            player.Compound.Add(card);
            EnqueueEffect(card, player, CardEffects.OnEnterPlay(card.Definition.Id), $"{card.Title} enters play");
        }

        public void GainBlock(PlayerState player, int amount)
        {
            if (player == null || amount <= 0)
            {
                return;
            }

            player.AddBlock(EffectModifiers.ModifyBlockGain(this, player, amount));
        }

        /// <summary>Draws to hand, reshuffling the discard if the deck runs dry.</summary>
        public CardInstance DrawCard(int playerId)
        {
            var player = GetPlayer(playerId);
            var card = DrawFromDeck();
            player.Hand.Add(card);

            EffectModifiers.AfterCardDrawn(this, player);
            return card;
        }

        /// <summary>
        /// The black die. Certain cards call for extra rolls; this is separate
        /// from the primary die so it never activates anybody's units.
        /// </summary>
        public int RollAuxiliaryDie(PlayerState roller)
        {
            var value = _random.Next(1, GameSettings.DieSides + 1);
            if (roller != null)
            {
                roller.AuxiliaryDiceRolledThisTurn++;
                EffectModifiers.AfterAuxiliaryRoll(this, roller, value);
            }

            return value;
        }

        /// <summary>
        /// Moves a card between compounds. Double Agent gets played into a rival's
        /// compound; Soul Swapper trades places with one.
        /// </summary>
        public void MoveToCompound(CardInstance card, PlayerState from, PlayerState to)
        {
            if (card == null || from == null || to == null || from == to)
            {
                return;
            }

            if (!from.Compound.Remove(card))
            {
                return;
            }

            to.Compound.Add(card);
        }

        /// <summary>
        /// Trades two cards between the compounds they are sitting in. Both are
        /// lifted before either lands, so swapping with the card next to it in the
        /// same compound cannot end up dropping one of them.
        /// </summary>
        public void SwapBetweenCompounds(CardInstance first, CardInstance second)
        {
            if (first == null || second == null || first == second)
            {
                return;
            }

            var firstOwner = _players.FirstOrDefault(p => p.Compound.Contains(first));
            var secondOwner = _players.FirstOrDefault(p => p.Compound.Contains(second));
            if (firstOwner == null || secondOwner == null)
            {
                return;
            }

            firstOwner.Compound.Remove(first);
            secondOwner.Compound.Remove(second);

            firstOwner.Compound.Add(second);
            secondOwner.Compound.Add(first);
        }

        /// <summary>
        /// Takes the top card of the deck without anybody drawing it. It Who
        /// Consumes plays whatever it finds.
        /// </summary>
        public CardInstance TakeTopOfDeck() => DrawFromDeck();

        /// <summary>Removes a card from its owner's compound and discards it.</summary>
        public void SacrificeCard(int playerId, int cardInstanceId)
        {
            var player = GetPlayer(playerId);
            var card = player.Compound.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in player {playerId}'s compound.");
            }

            player.Compound.Remove(card);
            _discard.Add(card);
        }

        /// <summary>Drops a card straight onto the discard pile from wherever it was.</summary>
        public void DiscardDirectly(CardInstance card)
        {
            if (card != null)
            {
                _discard.Add(card);
            }
        }

        public void DiscardFromHand(int playerId, int cardInstanceId)
        {
            var player = GetPlayer(playerId);
            var card = player.Hand.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in player {playerId}'s hand.");
            }

            player.Hand.Remove(card);
            _discard.Add(card);
        }

        /// <summary>Moves a card out of the discard pile and into a hand.</summary>
        public void ReturnFromDiscard(int playerId, int cardInstanceId)
        {
            var card = _discard.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in the discard pile.");
            }

            _discard.Remove(card);
            GetPlayer(playerId).Hand.Add(card);
        }

        /// <summary>A random living player other than the one given.</summary>
        public PlayerState RandomOpponentOf(PlayerState player)
        {
            var opponents = LivingPlayers.Where(p => p != player).ToList();
            return opponents.Count == 0 ? null : opponents[_random.Next(opponents.Count)];
        }

        public int RandomBelow(int exclusiveMax) => _random.Next(exclusiveMax);

        /// <summary>
        /// Claims a card's once-a-turn allowance. Returns false if it has already
        /// been spent this turn.
        /// </summary>
        public bool TakeOncePerTurn(string key) => _oncePerTurn.Add(key);

        /// <summary>
        /// Suspicious Chef's paid meal counter. Buying a counter is not an attack
        /// and does not wait for a turn, so it is allowed in any phase; the damage
        /// still only happens when the Chef activates on its own number.
        /// One counter per Chef per turn.
        /// </summary>
        public void BuyMealCounter(int playerId, int cardInstanceId, IReadOnlyList<ResourceColor> payment)
        {
            RequireNoPendingChoice();

            var player = GetPlayer(playerId);
            var chef = player.Compound.FirstOrDefault(
                c => c.InstanceId == cardInstanceId && c.Definition.Id == CardIds.SuspiciousChef);

            if (chef == null)
            {
                throw new ArgumentException($"Player {playerId} has no Suspicious Chef with id {cardInstanceId}.");
            }

            if (payment.Count != GameSettings.MealCounterCost)
            {
                throw new ArgumentException(
                    $"A meal counter costs exactly {GameSettings.MealCounterCost} resources of any colour.");
            }

            // Affordability is checked before the once-a-turn allowance is claimed,
            // so an attempt that gets rejected does not quietly use up the turn's go.
            foreach (var color in payment.Distinct())
            {
                var owed = payment.Count(c => c == color);
                if (player.Resources[color] < owed)
                {
                    throw new InvalidOperationException($"Not enough {color} to pay for a meal counter.");
                }
            }

            if (!TakeOncePerTurn($"meal:{chef.InstanceId}"))
            {
                throw new InvalidOperationException("That Chef has already been paid this turn.");
            }

            foreach (var color in payment)
            {
                player.Resources.Remove(color);
            }

            chef.AddCounter(Counters.Meal, EffectModifiers.ModifyCounterGain(this, player, 1));
        }

        /// <summary>
        /// Baal's Scheme counter: spend one to set any player's die to any face.
        /// Only during the Rolling phase, which is the window between the dice
        /// landing and units answering to them.
        /// </summary>
        public void SpendSchemeCounter(int playerId, int targetPlayerId, int dieValue)
        {
            RequirePhase(TurnPhase.Rolling);
            RequireNoPendingChoice();

            if (!_diceRolled)
            {
                throw new InvalidOperationException("Nobody has rolled yet.");
            }

            var player = GetPlayer(playerId);
            var baal = player.Compound.FirstOrDefault(
                c => c.Definition.Id == CardIds.BaalTheManipulator && c.GetCounter(Counters.Scheme) > 0);

            if (baal == null)
            {
                throw new InvalidOperationException("You have no Baal with a Scheme counter to spend.");
            }

            var target = GetPlayer(targetPlayerId);
            if (!target.IsAlive)
            {
                throw new InvalidOperationException("That leader is out of the game.");
            }

            baal.RemoveCounter(Counters.Scheme);
            target.SetPrimaryDie(dieValue);
        }

        /// <summary>
        /// Try again's reroll. Only available in the Rolling phase, before any
        /// unit has looked at the result, and only once per turn.
        /// </summary>
        public void RerollPrimaryDie(int playerId)
        {
            RequirePhase(TurnPhase.Rolling);
            RequireNoPendingChoice();

            var player = GetPlayer(playerId);

            if (!player.HasInPlay(CardIds.TryAgain))
            {
                throw new InvalidOperationException("You have no card that lets you reroll.");
            }

            if (!_diceRolled)
            {
                throw new InvalidOperationException("Nobody has rolled yet.");
            }

            if (!TakeOncePerTurn($"reroll:{playerId}"))
            {
                throw new InvalidOperationException("You have already rerolled this turn.");
            }

            player.SetPrimaryDie(_random.Next(1, GameSettings.DieSides + 1));
        }

        /// <summary>Gives a player a die only their own units will answer to.</summary>
        public void AddPrivateDie(PlayerState player)
        {
            player.PrivateDice.Add(_random.Next(1, GameSettings.DieSides + 1));
        }

        /// <summary>
        /// Forces a die to a value. Baal changes dice outright; Close Enough
        /// nudges one by a point.
        /// </summary>
        public void SetPrimaryDie(PlayerState player, int value) => player.SetPrimaryDie(value);

        // -------------------------------------------------------- Effect queue

        private class PendingEffect
        {
            public CardInstance Source;
            public PlayerState Controller;
            public EffectRoutine Routine;
            public string Description;
            public IEnumerator<ChoiceRequest> Steps;
        }

        /// <summary>
        /// The question a card is currently waiting on, or null if nothing is.
        /// Nothing else in the game may happen while this is set.
        /// </summary>
        public ChoiceRequest PendingChoice { get; private set; }

        /// <summary>The card currently resolving, for the log and the UI.</summary>
        public string ResolvingDescription => _resolving?.Description;

        public bool HasEffectsPending => _resolving != null || _effectQueue.Count > 0;

        /// <summary>
        /// Lines an effect up to run. Triggered abilities queue rather than run
        /// inline so they cannot cut into the middle of whatever set them off.
        /// </summary>
        public void EnqueueEffect(CardInstance source, PlayerState controller, EffectRoutine routine, string description)
        {
            if (routine == null || controller == null)
            {
                return;
            }

            _effectQueue.Enqueue(new PendingEffect
            {
                Source = source,
                Controller = controller,
                Routine = routine,
                Description = description
            });
        }

        /// <summary>
        /// Runs queued effects until one asks a question or there is nothing left.
        /// Safe to call at any time; does nothing if a choice is already pending.
        /// </summary>
        public void ResolveEffects()
        {
            // Two cards that retaliate against each other would otherwise trade
            // blows forever. The board is in a legal state at every step, so
            // stopping early is survivable in a way that hanging the server is not.
            var budget = GameSettings.MaxEffectStepsPerResolution;

            while (PendingChoice == null && Phase != TurnPhase.GameOver)
            {
                if (budget-- <= 0)
                {
                    _effectQueue.Clear();
                    _resolving = null;
                    return;
                }

                if (_resolving == null)
                {
                    if (_effectQueue.Count == 0)
                    {
                        if (!_endOfTurnPending)
                        {
                            return;
                        }

                        EndOfTurn();
                        continue;
                    }

                    _resolving = _effectQueue.Dequeue();

                    // A card whose controller died before it got its turn does nothing.
                    if (!_resolving.Controller.IsAlive)
                    {
                        _resolving = null;
                        continue;
                    }

                    _resolving.Steps = _resolving.Routine(
                        new EffectContext(this, _resolving.Controller, _resolving.Source));
                }

                if (!_resolving.Steps.MoveNext())
                {
                    _resolving = null;
                    CheckForGameOver();
                    continue;
                }

                var request = _resolving.Steps.Current;

                // ChoosePlayer answers itself when there is only one candidate, so
                // two-player games are not pestered with pointless prompts.
                if (request == null || request.Answered)
                {
                    continue;
                }

                PendingChoice = request;
            }
        }

        /// <summary>
        /// Runs a card's effect right now, as if it had been activated. Used by
        /// It Who Consumes and Revive the Forgotten, which play other people's cards.
        /// </summary>
        public void ActivateCard(CardInstance card, PlayerState controller, int dieValue = 0)
        {
            var routine = CardEffects.For(card.Definition.Id, dieValue);
            EnqueueEffect(card, controller, routine, card.Title);
        }

        // ------------------------------------------------------------- Answers

        private ChoiceRequest RequireChoiceFrom(int playerId, ChoiceKind kind)
        {
            if (PendingChoice == null)
            {
                throw new InvalidOperationException("Nothing is waiting on a decision.");
            }

            if (PendingChoice.AskedOfPlayerId != playerId)
            {
                throw new InvalidOperationException($"That decision belongs to player {PendingChoice.AskedOfPlayerId}.");
            }

            if (PendingChoice.Kind != kind)
            {
                throw new InvalidOperationException($"The card is asking for a {PendingChoice.Kind}, not a {kind}.");
            }

            return PendingChoice;
        }

        private void Answered()
        {
            PendingChoice = null;
            ResolveEffects();
        }

        public void AnswerPlayerChoice(int playerId, int chosenPlayerId)
        {
            RequireChoiceFrom(playerId, ChoiceKind.Player).AnswerPlayer(chosenPlayerId);
            Answered();
        }

        public void AnswerCardChoice(int playerId, int chosenCardId)
        {
            RequireChoiceFrom(playerId, ChoiceKind.Card).AnswerCard(chosenCardId);
            Answered();
        }

        public void AnswerColorChoice(int playerId, ResourceColor color)
        {
            RequireChoiceFrom(playerId, ChoiceKind.Color).AnswerColor(color);
            Answered();
        }

        public void AnswerYesNo(int playerId, bool yes)
        {
            RequireChoiceFrom(playerId, ChoiceKind.YesNo).AnswerYesNo(yes);
            Answered();
        }

        public void AnswerOptionChoice(int playerId, string option)
        {
            RequireChoiceFrom(playerId, ChoiceKind.Option).AnswerOption(option);
            Answered();
        }

        public void AnswerAmount(int playerId, int amount)
        {
            RequireChoiceFrom(playerId, ChoiceKind.Amount).AnswerAmount(amount);
            Answered();
        }

        // --------------------------------------------------------- Activations

        /// <summary>
        /// Queues every unit that matches a die rolled this turn. All players'
        /// units answer to every primary die, so a 4 rolled by two people fires
        /// matching units twice. Private dice from Standardized Uniforms only ever
        /// wake their owner's units.
        ///
        /// Everyone's activating Units are gathered first and then queued in a
        /// fixed order - Draw, Block, Followers, Damage, Health, then everything
        /// else - rather than seat by seat, so a Block granted by one unit is on
        /// the board before Damage from another unit this same round can be
        /// reduced by it. See <see cref="ActivationCategory"/>.
        /// </summary>
        private void QueueActivations()
        {
            var shared = LivingPlayers.Select(p => p.PrimaryDie).ToList();
            var activating = new List<(PlayerState Player, CardInstance Unit, int DieValue)>();

            foreach (var player in LivingPlayers.ToList())
            {
                foreach (var value in shared)
                {
                    CollectActivating(player, value, activating);
                }

                foreach (var value in player.PrivateDice.ToList())
                {
                    CollectActivating(player, value, activating);
                }
            }

            // OrderBy is a stable sort, so within a category units still queue in
            // the same seat-then-die order they were collected in.
            foreach (var entry in activating.OrderBy(
                         e => (int)CardEffects.CategoryFor(e.Unit.Definition.Id, e.DieValue)))
            {
                entry.Player.UnitsTriggeredThisTurn++;
                EnqueueEffect(entry.Unit, entry.Player,
                    CardEffects.For(entry.Unit.Definition.Id, entry.DieValue), entry.Unit.Title);
            }
        }

        private static void CollectActivating(
            PlayerState player, int dieValue, List<(PlayerState Player, CardInstance Unit, int DieValue)> activating)
        {
            foreach (var unit in player.UnitsActivatingOn(dieValue).ToList())
            {
                // Ominous Eye's static counters swallow an activation each.
                if (unit.GetCounter(Counters.Static) > 0)
                {
                    unit.AddCounter(Counters.Static, -1);
                    continue;
                }

                activating.Add((player, unit, dieValue));
            }
        }

        /// <summary>Human Zoo's die roll, and anything else that opens a turn.</summary>
        private void QueueStartOfTurnTriggers()
        {
            foreach (var player in LivingPlayers.ToList())
            {
                foreach (var card in player.Compound.Where(c => c.Definition.Id == CardIds.HumanZoo).ToList())
                {
                    EnqueueEffect(card, player, CardEffects.For(CardIds.HumanZoo, 0), card.Title);
                }
            }
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

        /// <summary>
        /// Nothing may happen while a card is waiting on an answer. The client
        /// hides the rest of the interface at that point, so reaching this is
        /// either a stale click or a client that was not asked nicely.
        /// </summary>
        private void RequireNoPendingChoice()
        {
            if (PendingChoice != null)
            {
                throw new InvalidOperationException("A card is still waiting on a decision.");
            }
        }
    }
}
