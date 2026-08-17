using System;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core.Effects;

namespace Indoctrination.Core
{
    /// <summary>
    /// One die-triggered Unit in the exact round-robin order it will resolve.
    /// The network layer exposes this public table information so every client
    /// can present the same activation rather than guessing from the final state.
    /// </summary>
    public sealed class ActivationSequenceEntry
    {
        public int Index { get; }
        public CardInstance Source { get; }
        public PlayerState Controller { get; }
        public int DieValue { get; }
        public ActivationCategory Category { get; }
        public bool Completed { get; internal set; }
        public bool Skipped { get; internal set; }

        public ActivationSequenceEntry(
            int index, CardInstance source, PlayerState controller,
            int dieValue, ActivationCategory category)
        {
            Index = index;
            Source = source;
            Controller = controller;
            DieValue = dieValue;
            Category = category;
        }
    }

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
        private readonly HashSet<int> _playersWhoRolled = new();
        private bool _highRollClaimed;
        private readonly HashSet<int> _resourcesCollected = new();
        private readonly HashSet<int> _playersReady = new();

        // Card effects waiting to run, oldest first. See ResolveEffects.
        private readonly Queue<PendingEffect> _effectQueue = new();
        private PendingEffect _resolving;
        private readonly List<ActivationSequenceEntry> _activationSequence = new();

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
        public IReadOnlyList<ActivationSequenceEntry> ActivationSequence => _activationSequence;
        public int ActivationCompletedCount { get; private set; }
        public int ActivationBatch { get; private set; }

        /// <summary>
        /// The live network game resolves one activation per broadcast so clients
        /// can show it. Rules tests and other in-process callers keep the original
        /// synchronous behavior unless they explicitly opt in.
        /// </summary>
        public bool PaceActivations { get; set; }

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
        /// Only needed to open the game - every later draft is dealt by the turn
        /// loop itself when it comes back around.
        /// </summary>
        public void BeginDraft()
        {
            DealDraft();
            ResolveEffects();
        }

        /// <summary>
        /// Deals the zone and queues the draft Blessings, without draining the
        /// effect queue. Kept separate from <see cref="BeginDraft"/> because the
        /// turn loop reaches a new draft from inside <see cref="ResolveEffects"/>,
        /// which must not be re-entered - the queued effects are picked up by the
        /// loop that is already running.
        /// </summary>
        private void DealDraft()
        {
            RequirePhase(TurnPhase.Draft);

            // The first pick moves one seat round the table each draft, so the
            // advantage of choosing first is shared out rather than belonging to
            // whoever happened to be drawn for it at the start.
            if (DraftNumber > 0)
            {
                FirstDrafterIndex = (FirstDrafterIndex + 1) % _players.Count;
            }

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

            // Sized by who is still playing, not who started, so a table that has
            // lost a leader keeps the same three picks each rather than drowning
            // the survivors in cards. The floor matches the minimum table size;
            // a game down to one leader has already ended before reaching here.
            var livingCount = Math.Max(GameSettings.MinPlayers, LivingPlayers.Count());
            var zoneSize = GameSettings.DraftZoneSize(livingCount);
            for (var i = 0; i < zoneSize; i++)
            {
                var card = DrawFromDeck();
                if (card == null)
                {
                    break;
                }

                _draftZone.Add(card);
            }

            _draftOrder = BuildSnakeOrder();

            // Too few cards left to give anybody a pick. The round goes ahead
            // with the boards people already have rather than stalling on a
            // draft that cannot happen.
            if (_draftOrder.Count == 0)
            {
                CloseDraft();
                return;
            }

            QueueDraftSetupTriggers();
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
        ///
        /// Only leaders still in the game get picks. A dead player left in the
        /// order would stop the draft dead, since the table waits on whoever's
        /// turn it is and they can never take one.
        /// </summary>
        private List<int> BuildSnakeOrder()
        {
            var seats = Enumerable.Range(0, _players.Count)
                .Select(offset => _players[(FirstDrafterIndex + offset) % _players.Count])
                .Where(player => player.IsAlive)
                .Select(player => player.PlayerId)
                .ToList();

            if (seats.Count == 0)
            {
                return new List<int>();
            }

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
        /// <summary>
        /// Whose turn it is to draft, or null when the draft is over.
        ///
        /// Dead leaders are stepped over rather than waited on. The order is
        /// fixed when the zone is dealt, but damage queued at the end of the
        /// previous turn - a flame counter, a retaliation - resolves afterwards
        /// and can take somebody out who is already in the running order.
        /// </summary>
        public int? CurrentDrafterId
        {
            get
            {
                var index = CurrentDraftIndex();
                return index < 0 ? null : _draftOrder[index];
            }
        }

        private int CurrentDraftIndex()
        {
            for (var i = _draftPickIndex; i < _draftOrder.Count; i++)
            {
                if (GetPlayer(_draftOrder[i]).IsAlive)
                {
                    return i;
                }
            }

            return -1;
        }

        public void DraftCard(int playerId, int cardInstanceId)
        {
            RequirePhase(TurnPhase.Draft);
            RequireNoPendingChoice();
            RequireAlive(playerId);

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
            _draftPickIndex = CurrentDraftIndex() + 1;

            if (CurrentDrafterId == null)
            {
                EndDraft();
            }
        }

        /// <summary>The last three cards go to the discard pile and play begins.</summary>
        private void EndDraft()
        {
            CloseDraft();
            ResolveEffects();
        }

        /// <summary>
        /// Clears the zone away and opens the first turn. Separate from
        /// <see cref="EndDraft"/> so it can be reached from inside
        /// <see cref="ResolveEffects"/>, which must not be re-entered.
        /// </summary>
        private void CloseDraft()
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

        /// <summary>Whether every living player has rolled this turn.</summary>
        public bool DiceRolled => LivingPlayers.All(player => HasRolled(player.PlayerId));

        public bool HasRolled(int playerId) => _playersWhoRolled.Contains(playerId);

        /// <summary>Rolls one player's die when they press Roll Die.</summary>
        public PlayerState RollPrimaryDie(int playerId)
        {
            RequirePhase(TurnPhase.Rolling);
            RequireNoPendingChoice();
            var player = RequireAlive(playerId);

            if (HasRolled(playerId))
            {
                throw new InvalidOperationException("You have already rolled this turn.");
            }

            RollPrimaryDieFor(player);
            return player;
        }

        /// <summary>
        /// Rolls every living player who has not rolled yet. Used by rules tests
        /// and by the phase-timeout fallback so an absent player cannot stall the
        /// table forever.
        /// </summary>
        public PlayerState RollPrimaryDice()
        {
            RequirePhase(TurnPhase.Rolling);

            if (DiceRolled)
            {
                throw new InvalidOperationException("The dice have already been rolled this turn.");
            }

            foreach (var player in LivingPlayers.Where(player => !HasRolled(player.PlayerId)).ToList())
            {
                RollPrimaryDieFor(player);
            }

            return HighestUniqueRoller();
        }

        private void RollPrimaryDieFor(PlayerState player)
        {
            player.SetPrimaryDie(_random.Next(1, GameSettings.DieSides + 1));
            _playersWhoRolled.Add(player.PlayerId);

            // Standardized Uniforms buys a die nobody else's units answer to.
            if (player.HasInPlay(CardIds.StandardizedUniforms))
            {
                AddPrivateDie(player);
            }
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
            RequireNoPendingChoice();
            RequireAlive(playerId);

            if (!DiceRolled)
            {
                throw new InvalidOperationException("Not everyone has rolled yet.");
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
            RequireNoPendingChoice();
            RequireAlive(playerId);

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

            // Every stone discounts every card in hand, Units and otherwise. The
            // cursed ones do the same and charge a point of maximum health for it.
            if (player.HasInPlay(CardIds.Mindstone) || player.HasInPlay(CardIds.CursedMindstone))
            {
                cost = cost.Reduced(ResourceColor.Blue, 1);
            }

            if (player.HasInPlay(CardIds.Shieldstone) || player.HasInPlay(CardIds.CursedShieldstone))
            {
                cost = cost.Reduced(ResourceColor.Green, 1);
            }

            if (player.HasInPlay(CardIds.Bloodstone) || player.HasInPlay(CardIds.CursedBloodstone))
            {
                cost = cost.Reduced(ResourceColor.Red, 1);
            }

            if (player.HasInPlay(CardIds.Wealthstone) || player.HasInPlay(CardIds.CursedWealthstone))
            {
                cost = cost.Reduced(ResourceColor.Yellow, 1);
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

            var player = RequireAlive(playerId);
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

            // Checked as a whole before anything is taken. Paying the resources
            // first and then discovering the followers are short would leave the
            // player charged for a card they did not get.
            var cost = CostFor(player, card);
            if (!player.CanAfford(cost))
            {
                throw new InvalidOperationException($"Cannot afford {card.Title} ({cost}).");
            }

            player.Resources.Pay(cost);

            if (cost.Followers > 0)
            {
                player.LoseFollowers(cost.Followers);
            }

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
        /// <summary>
        /// Whether this player could pay a card's special cost right now.
        ///
        /// The board needs this as a question, not only as an exception. A card
        /// priced "*" costs no resources at all, so asking the resource pool
        /// whether it is affordable always answers no - and the board lights and
        /// unlocks cards by exactly that answer, which left It Who Consumes
        /// impossible to buy however ready for it you were.
        /// </summary>
        public bool CanPaySpecialCost(PlayerState player, CardInstance card)
        {
            return SpecialCostShortfall(player, card) == null;
        }

        /// <summary>
        /// What is missing before a special cost can be paid, or null if nothing
        /// is. One list, read both by the board deciding whether to offer the
        /// card and by the buy refusing it, so the two can never disagree.
        /// </summary>
        private static string SpecialCostShortfall(PlayerState player, CardInstance card)
        {
            if (card.Definition.Id != CardIds.ItWhoConsumes)
            {
                return $"{card.Title} has no rules for its special cost yet.";
            }

            if (!player.Compound.Any(c => c.Type == CardType.Unit))
            {
                return "It Who Consumes needs a Unit to sacrifice.";
            }

            if (!player.Compound.Any(c => c.Type == CardType.Blessing))
            {
                return "It Who Consumes needs a Blessing to sacrifice.";
            }

            if (!player.Hand.Any(c => c.Type == CardType.Ritual && c != card))
            {
                return "It Who Consumes needs a Ritual in hand to activate.";
            }

            return null;
        }

        private void BuyForSpecialCost(PlayerState player, CardInstance card)
        {
            var shortfall = SpecialCostShortfall(player, card);
            if (shortfall != null)
            {
                throw new InvalidOperationException(shortfall);
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
        /// <summary>The Ritual that resolved most recently, for the board to show.</summary>
        public CardInstance LastRitualPlayed { get; private set; }

        /// <summary>How many Rituals have resolved this game.</summary>
        public int RitualsPlayed { get; private set; }

        public void PlayRitual(CardInstance ritual, PlayerState player, bool runEffect = true)
        {
            LastRitualPlayed = ritual;
            RitualsPlayed++;

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
            RequireNoPendingChoice();

            var player = RequireAlive(playerId);
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

            // Confirms the player is still in the game; the dead are not waited on.
            RequireAlive(playerId);

            if (Phase == TurnPhase.Rolling && ready && !DiceRolled)
            {
                throw new InvalidOperationException("Every living player must roll before the table can activate units.");
            }

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

            if (Phase == TurnPhase.Activation && PaceActivations && HasEffectsPending)
            {
                throw new InvalidOperationException("Unit activations are still resolving.");
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
                    // A phase timeout rolls only the missing dice so a disconnected
                    // player cannot strand everybody in Rolling.
                    if (!DiceRolled)
                    {
                        RollPrimaryDice();
                    }

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
                    QueueHandLimitDiscards();

                    // The turn does not close here. End-of-turn Blessings look back
                    // at this turn's tallies, and some of them stop to ask who to
                    // hit, so the tallies have to survive until the queue drains.
                    EffectModifiers.QueueEndOfTurnTriggers(this);
                    _endOfTurnPending = true;
                    break;

                default:
                    throw new InvalidOperationException($"Cannot advance from {Phase}.");
            }

            // Live games reveal one activation at a time. Other phases, and the
            // in-process rules harness, retain the original synchronous drain.
            if (Phase != TurnPhase.Activation || !PaceActivations)
            {
                ResolveEffects();
            }
        }

        /// <summary>
        /// Asks anybody holding more than the hand limit to throw the extras away
        /// as the turn closes. Done here rather than by refusing draws or picks:
        /// the draft hands out a fixed number of cards, so a player at the limit
        /// mid-draft would otherwise have nowhere to put them and stall the table.
        /// </summary>
        private void QueueHandLimitDiscards()
        {
            foreach (var player in LivingPlayers.ToList())
            {
                var excess = player.Hand.Count - GameSettings.HandLimit;
                if (excess > 0)
                {
                    EnqueueEffect(null, player, CommonEffects.DiscardDownToHandLimit(excess),
                                  $"{player.Name} is over the hand limit");
                }
            }
        }

        private void EndOfTurn()
        {
            _endOfTurnPending = false;
            _playersWhoRolled.Clear();
            _highRollClaimed = false;
            _resourcesCollected.Clear();
            _playersReady.Clear();
            _oncePerTurn.Clear();

            // A draw offer is about the position as it stands. Carrying one into
            // a turn that has changed the board would agree to something else.
            _drawOffers.Clear();

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

            // Straight into the next draft. Dealing it here rather than leaving
            // the game sitting in a Draft phase with an empty zone means the rules
            // engine never needs an outside caller to get it unstuck.
            Phase = TurnPhase.Draft;
            DealDraft();
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

        /// <summary>
        /// Whether the game ended with no winner - everybody knocked out together,
        /// or the table agreeing to a draw. Cards that hit the whole table at once
        /// (Friend of the Beasts, Bloody Mooner, a flame counter burning out) can
        /// take the last two leaders down in the same instant.
        /// </summary>
        public bool IsDraw => Phase == TurnPhase.GameOver && Winner == null;

        // ------------------------------------------------- Conceding and draws

        private readonly HashSet<int> _resigned = new();
        private readonly HashSet<int> _drawOffers = new();

        /// <summary>Players who walked away rather than being knocked out.</summary>
        public IReadOnlyCollection<int> Resigned => _resigned;

        /// <summary>Players currently offering a draw.</summary>
        public IReadOnlyCollection<int> DrawOffers => _drawOffers;

        public bool HasResigned(int playerId) => _resigned.Contains(playerId);

        public bool HasOfferedDraw(int playerId) => _drawOffers.Contains(playerId);

        /// <summary>
        /// Gives up. One player's decision alone - nobody else is consulted,
        /// because staying in a game you have decided is lost is not something
        /// the table should be able to insist on.
        ///
        /// The board stays where it is and other cards can still read it; the
        /// player is simply out, exactly as if they had been reduced to nothing.
        /// </summary>
        public void Resign(int playerId)
        {
            if (Phase == TurnPhase.GameOver)
            {
                throw new InvalidOperationException("The game is over.");
            }

            var player = RequireAlive(playerId);

            _resigned.Add(playerId);
            _drawOffers.Remove(playerId);
            _playersReady.Remove(playerId);

            // Not damage: nothing that pays out on wounds should pay out because
            // somebody conceded.
            player.LoseHealth(player.Health);

            AbandonPendingChoiceIfAskedOfSomeoneOut();
            ResolveEffects();
        }

        /// <summary>
        /// Offers, or takes back, a draw. Unlike resigning this needs everybody:
        /// a draw is a result the whole table has to accept, so the game only
        /// ends once every living player is offering one.
        /// </summary>
        public void SetDrawOffer(int playerId, bool offering)
        {
            if (Phase == TurnPhase.GameOver)
            {
                throw new InvalidOperationException("The game is over.");
            }

            RequireAlive(playerId);

            if (offering)
            {
                _drawOffers.Add(playerId);
            }
            else
            {
                _drawOffers.Remove(playerId);
            }

            if (LivingPlayers.All(player => _drawOffers.Contains(player.PlayerId)))
            {
                Phase = TurnPhase.GameOver;
            }
        }

        /// <summary>
        /// Drops a question whose player has just left the game. Nothing else at
        /// the table may happen while a question is open, so one left behind by a
        /// player resigning would stop the game for everybody.
        /// </summary>
        private void AbandonPendingChoiceIfAskedOfSomeoneOut()
        {
            if (PendingChoice == null || GetPlayer(PendingChoice.AskedOfPlayerId).IsAlive)
            {
                return;
            }

            PendingChoice = null;
            _resolving = null;
        }

        private bool CheckForGameOver()
        {
            // No survivors is just as final as a winner, and has to end the game
            // too: with nobody alive there is no one left to roll, draft, or be
            // asked anything, so play cannot continue.
            if (Winner == null && LivingPlayers.Any())
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
            if (card == null)
            {
                return null;
            }

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

            var player = RequireAlive(playerId);
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

            if (!DiceRolled)
            {
                throw new InvalidOperationException("Not everyone has rolled yet.");
            }

            var player = RequireAlive(playerId);
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
        /// <summary>
        /// Whether this player still has Try again's reroll available.
        ///
        /// Non-consuming, unlike the reroll itself, because the phase has to ask
        /// this before deciding whether it may end. Rolling used to close the
        /// instant the last die landed, which is exactly the moment the reroll
        /// becomes legal - so the card could never be used at all.
        /// </summary>
        public bool CanReroll(int playerId)
        {
            if (Phase != TurnPhase.Rolling || PendingChoice != null || !DiceRolled)
            {
                return false;
            }

            var player = _players.FirstOrDefault(p => p.PlayerId == playerId);

            return player is { IsAlive: true }
                   && player.HasInPlay(CardIds.TryAgain)
                   && !_oncePerTurn.Contains($"reroll:{playerId}");
        }

        /// <summary>
        /// Turns the reroll down for this turn.
        ///
        /// Try again's offer holds the Rolling phase open for everybody, because
        /// the reroll only becomes legal once every die is down - which is the
        /// same instant the phase would otherwise close. That is right, but it
        /// left the holder no way to say "no thanks": the table waited on them
        /// every turn until the clock ran out. Declining spends the same
        /// once-per-turn slot the reroll does, so the offer closes and the phase
        /// can move.
        /// </summary>
        public void DeclineReroll(int playerId)
        {
            RequirePhase(TurnPhase.Rolling);
            RequireAlive(playerId);
            TakeOncePerTurn($"reroll:{playerId}");
        }

        public void RerollPrimaryDie(int playerId)
        {
            RequirePhase(TurnPhase.Rolling);
            RequireNoPendingChoice();

            var player = RequireAlive(playerId);

            if (!player.HasInPlay(CardIds.TryAgain))
            {
                throw new InvalidOperationException("You have no card that lets you reroll.");
            }

            if (!DiceRolled)
            {
                throw new InvalidOperationException("Not everyone has rolled yet.");
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
            public ActivationSequenceEntry Activation;
        }

        /// <summary>
        /// The question a card is currently waiting on, or null if nothing is.
        /// Nothing else in the game may happen while this is set.
        /// </summary>
        public ChoiceRequest PendingChoice { get; private set; }

        /// <summary>The card currently resolving, for the log and the UI.</summary>
        public string ResolvingDescription => _resolving?.Description;

        /// <summary>
        /// The id of the card currently resolving, so a popup asking its question
        /// can show the card itself rather than only its description.
        /// </summary>
        public string ResolvingCardId => _resolving?.Source?.Definition.Id;

        public bool HasEffectsPending => _resolving != null || _effectQueue.Count > 0;

        /// <summary>
        /// Lines an effect up to run. Triggered abilities queue rather than run
        /// inline so they cannot cut into the middle of whatever set them off.
        /// </summary>
        /// <summary>
        /// Raised as each effect joins the queue, with the card and whose it is.
        /// Exists so the order activations are dealt out in can be observed and
        /// checked - it is the one thing about activation a player can feel but
        /// no amount of reading the final board state can confirm.
        /// </summary>
        public event Action<CardInstance, PlayerState> EffectQueued;

        public void EnqueueEffect(CardInstance source, PlayerState controller, EffectRoutine routine, string description)
        {
            EnqueueEffect(source, controller, routine, description, null);
        }

        private void EnqueueEffect(
            CardInstance source, PlayerState controller, EffectRoutine routine,
            string description, ActivationSequenceEntry activation)
        {
            if (routine == null || controller == null)
            {
                return;
            }

            EffectQueued?.Invoke(source, controller);

            _effectQueue.Enqueue(new PendingEffect
            {
                Source = source,
                Controller = controller,
                Routine = routine,
                Description = description,
                Activation = activation
            });
        }

        /// <summary>
        /// Runs queued effects until one asks a question or there is nothing left.
        /// Safe to call at any time; does nothing if a choice is already pending.
        /// </summary>
        public void ResolveEffects()
        {
            ResolveEffects(stopAfterOneActivation: false);
        }

        /// <summary>
        /// Resolves through one die-triggered Unit, or until that Unit asks a
        /// question. Used only by the paced network presentation.
        /// </summary>
        public void ResolveNextActivation()
        {
            ResolveEffects(stopAfterOneActivation: true);
        }

        private void ResolveEffects(bool stopAfterOneActivation)
        {
            // Two cards that retaliate against each other would otherwise trade
            // blows forever. The board is in a legal state at every step, so
            // stopping early is survivable in a way that hanging the server is not.
            var budget = GameSettings.MaxEffectStepsPerResolution;

            // The game-over test runs every time round, and on the way in. Damage
            // dealt outside an effect - a flame counter burning out, a direct hit
            // - would otherwise go unnoticed until something else happened to ask.
            while (PendingChoice == null && !CheckForGameOver())
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
                        // Damage that resolved after the zone was dealt can leave
                        // nobody alive who is still owed a pick.
                        if (Phase == TurnPhase.Draft && _draftOrder.Count > 0 && CurrentDrafterId == null)
                        {
                            CloseDraft();
                            continue;
                        }

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
                        CompleteActivation(_resolving, skipped: true);
                        _resolving = null;
                        // Dead players consume no presentation beat. Continue
                        // through every skipped entry until a living Unit fires.
                        continue;
                    }

                    _resolving.Steps = _resolving.Routine(
                        new EffectContext(this, _resolving.Controller, _resolving.Source));
                }

                if (!_resolving.Steps.MoveNext())
                {
                    var completedActivation = _resolving.Activation != null;
                    CompleteActivation(_resolving, skipped: false);
                    _resolving = null;
                    CheckForGameOver();
                    if (stopAfterOneActivation && completedActivation)
                    {
                        return;
                    }
                    continue;
                }

                var request = _resolving.Steps.Current;

                // ChoosePlayer answers itself when there is only one candidate, so
                // two-player games are not pestered with pointless prompts.
                if (request == null || request.Answered)
                {
                    continue;
                }

                // An effect can kill the very player it is about to question -
                // Revive the Forgotten replays a card out of the discard, and that
                // card can finish its own controller off. A leader who is out of
                // the game makes no more decisions, so the rest of the effect is
                // abandoned rather than left waiting for an answer that can never come.
                if (!GetPlayer(request.AskedOfPlayerId).IsAlive)
                {
                    var completedActivation = _resolving.Activation != null;
                    CompleteActivation(_resolving, skipped: false);
                    _resolving = null;
                    CheckForGameOver();
                    if (stopAfterOneActivation && completedActivation)
                    {
                        return;
                    }
                    continue;
                }

                PendingChoice = request;
            }
        }

        private void CompleteActivation(PendingEffect effect, bool skipped)
        {
            if (effect?.Activation == null || effect.Activation.Completed)
            {
                return;
            }

            effect.Activation.Skipped = skipped;
            effect.Activation.Completed = true;
            ActivationCompletedCount = Math.Max(
                ActivationCompletedCount, effect.Activation.Index + 1);
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
            if (PaceActivations && Phase == TurnPhase.Activation)
            {
                ResolveNextActivation();
            }
            else
            {
                ResolveEffects();
            }
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

        /// <summary>
        /// Answers the open question on behalf of whoever it was put to, for a
        /// player who has stopped responding - otherwise one dropped connection
        /// stops the table forever, since nothing may happen while a card waits.
        ///
        /// Every default is the passive one: decline the offer, take the smallest
        /// number, pick the first target. An absent player should never have an
        /// aggressive move or a spend made in their name.
        /// </summary>
        public void AnswerPendingChoiceWithDefault()
        {
            var choice = PendingChoice;
            if (choice == null)
            {
                return;
            }

            switch (choice.Kind)
            {
                case ChoiceKind.Player:
                    choice.AnswerPlayer(choice.PlayerOptions[0]);
                    break;

                case ChoiceKind.Card:
                    choice.AnswerCard(choice.CardOptions[0]);
                    break;

                case ChoiceKind.Color:
                    choice.AnswerColor(choice.ColorOptions.Count > 0 ? choice.ColorOptions[0] : ResourceColor.Red);
                    break;

                case ChoiceKind.Option:
                    choice.AnswerOption(choice.Options[0]);
                    break;

                case ChoiceKind.YesNo:
                    choice.AnswerYesNo(false);
                    break;

                case ChoiceKind.Amount:
                    choice.AnswerAmount(choice.MinAmount);
                    break;
            }

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
        /// Everyone's activating Units are gathered first and then dealt one per
        /// player around the table, beginning with the first drafter. A player's
        /// own compound order is preserved, so dragging is their activation-order
        /// decision. Categories describe the animation only; they never reorder
        /// rules resolution.
        /// </summary>
        private void QueueActivations()
        {
            _activationSequence.Clear();
            ActivationCompletedCount = 0;
            ActivationBatch++;

            var shared = LivingPlayers.Select(p => p.PrimaryDie).ToList();
            var queues = new List<Queue<(PlayerState Player, CardInstance Unit, int DieValue)>>();

            // Round the table from whoever drafted first, and within each player
            // in the order they have arranged their own compound. Both halves
            // matter: the table order is fair, and the order inside it is theirs.
            foreach (var player in SeatOrderFromFirstDrafter())
            {
                var queue = new Queue<(PlayerState, CardInstance, int)>();

                foreach (var unit in player.Compound.ToList())
                {
                    foreach (var value in shared.Concat(player.PrivateDice).ToList())
                    {
                        if (!unit.ActivatesOn(value))
                        {
                            continue;
                        }

                        // Ominous Eye's static counters swallow an activation each.
                        if (unit.GetCounter(Counters.Static) > 0)
                        {
                            unit.AddCounter(Counters.Static, -1);
                            continue;
                        }

                        queue.Enqueue((player, unit, value));
                    }
                }

                if (queue.Count > 0)
                {
                    queues.Add(queue);
                }
            }

            // One unit each, round and round, skipping anybody who has run out.
            //
            // A unit woken by two dice showing the same number fires twice, and
            // both firings are taken in the same turn rather than being split
            // across two passes. The table still alternates - it is one unit
            // each, not one activation each - and a unit that fires twice now
            // reads as one card doing its thing twice instead of the same card
            // reappearing later in the round.
            while (queues.Any(queue => queue.Count > 0))
            {
                foreach (var queue in queues.Where(queue => queue.Count > 0))
                {
                    var unit = queue.Peek().Unit;

                    while (queue.Count > 0 && ReferenceEquals(queue.Peek().Unit, unit))
                    {
                        var (player, firing, dieValue) = queue.Dequeue();

                        player.UnitsTriggeredThisTurn++;
                        var activation = new ActivationSequenceEntry(
                            _activationSequence.Count, firing, player, dieValue,
                            CardEffects.CategoryFor(firing.Definition.Id, dieValue));
                        _activationSequence.Add(activation);
                        EnqueueEffect(
                            firing, player, CardEffects.For(firing.Definition.Id, dieValue),
                            firing.Title, activation);
                    }
                }
            }
        }

        /// <summary>
        /// Living players in seat order, beginning with whoever drafts first this
        /// round. Activation follows the same order as the draft, so "you picked
        /// first, so you go first" holds throughout the turn.
        /// </summary>
        private IEnumerable<PlayerState> SeatOrderFromFirstDrafter()
        {
            return Enumerable.Range(0, _players.Count)
                .Select(offset => _players[(FirstDrafterIndex + offset) % _players.Count])
                .Where(player => player.IsAlive);
        }

        /// <summary>
        /// Moves one of a player's own units to a new position among their other
        /// units - the order units activate in. Blessings carry no die number, so
        /// where they sit makes no difference to the game; this always leaves
        /// them after every unit, in whatever order they were already in, so a
        /// player reordering their units can never scatter their blessings.
        /// </summary>
        public void ReorderUnit(int playerId, int cardInstanceId, int newIndex)
        {
            var player = RequireAlive(playerId);
            var card = player.Compound.FirstOrDefault(c => c.InstanceId == cardInstanceId);

            if (card == null)
            {
                throw new ArgumentException($"Card {cardInstanceId} is not in player {playerId}'s compound.");
            }

            if (card.Definition.Type != CardType.Unit)
            {
                throw new ArgumentException($"{card.Title} is not a unit - it has no activation order to change.");
            }

            var units = player.Compound.Where(c => c.Definition.Type == CardType.Unit).ToList();
            var rest = player.Compound.Where(c => c.Definition.Type != CardType.Unit).ToList();

            units.Remove(card);
            newIndex = Math.Clamp(newIndex, 0, units.Count);
            units.Insert(newIndex, card);

            player.Compound.Clear();
            player.Compound.AddRange(units);
            player.Compound.AddRange(rest);
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

        /// <summary>
        /// Takes the top card, shuffling the discard back in when the deck runs
        /// out. Returns null once both are empty - with 138 cards and four
        /// players hoarding hands and compounds, a long game really can exhaust
        /// the supply, and that is a card nobody draws rather than a crash.
        /// </summary>
        private CardInstance DrawFromDeck()
        {
            if (_deck.Count == 0)
            {
                ReshuffleDiscardIntoDeck();
            }

            if (_deck.Count == 0)
            {
                return null;
            }

            var card = _deck[^1];
            _deck.RemoveAt(_deck.Count - 1);
            return card;
        }

        private void ReshuffleDiscardIntoDeck()
        {
            if (_discard.Count == 0)
            {
                return;
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
        /// A leader who has been knocked out takes no more actions. Their board
        /// stays on the table - other cards still read it - but they do not draft,
        /// buy, collect, or spend anything again.
        /// </summary>
        private PlayerState RequireAlive(int playerId)
        {
            var player = GetPlayer(playerId);
            if (!player.IsAlive)
            {
                throw new InvalidOperationException($"{player.Name} is out of the game.");
            }

            return player;
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
