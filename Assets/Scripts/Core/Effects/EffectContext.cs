using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// Everything a card effect is allowed to do, in one place.
    ///
    /// Effects are written as iterators that yield a <see cref="ChoiceRequest"/>
    /// whenever they need a decision from a player:
    ///
    ///     static IEnumerator&lt;ChoiceRequest&gt; DealOne(EffectContext c)
    ///     {
    ///         var target = c.ChooseOpponent("Deal 1 damage to:");
    ///         yield return target;
    ///         c.DealDamage(target.ChosenPlayerId, 1);
    ///     }
    ///
    /// Yielding suspends the effect until the server has an answer, so an effect
    /// can ask, look at the reply, and ask again.
    /// </summary>
    public class EffectContext
    {
        public GameState Game { get; }

        /// <summary>The player who owns the card that is resolving.</summary>
        public PlayerState Controller { get; }

        /// <summary>The card that is resolving. Null for effects with no card.</summary>
        public CardInstance Source { get; }

        public EffectContext(GameState game, PlayerState controller, CardInstance source)
        {
            Game = game;
            Controller = controller;
            Source = source;
        }

        // ------------------------------------------------------------- Players

        public IReadOnlyList<PlayerState> LivingPlayers => Game.LivingPlayers.ToList();

        public IReadOnlyList<PlayerState> Opponents =>
            Game.LivingPlayers.Where(p => p != Controller).ToList();

        public PlayerState PlayerById(int playerId) => Game.GetPlayer(playerId);

        // ------------------------------------------------------------- Choices

        /// <summary>
        /// Asks the controller to pick an opponent. With only one opponent alive
        /// the question answers itself, so no prompt reaches the player.
        /// </summary>
        public ChoiceRequest ChooseOpponent(string prompt)
        {
            return ChoosePlayer(prompt, Opponents);
        }

        /// <summary>Asks the controller to pick any living player, themselves included.</summary>
        public ChoiceRequest ChooseAnyPlayer(string prompt)
        {
            return ChoosePlayer(prompt, LivingPlayers);
        }

        /// <summary>
        /// Returns null when nobody is a legal target - the last opponent may have
        /// died before this card resolved. Effects must treat null as "do nothing".
        /// </summary>
        public ChoiceRequest ChoosePlayer(string prompt, IEnumerable<PlayerState> candidates)
        {
            var options = candidates.Select(p => p.PlayerId).ToList();
            if (options.Count == 0)
            {
                return null;
            }

            var request = ChoiceRequest.Player(prompt, Controller.PlayerId, options);

            if (options.Count == 1)
            {
                request.AnswerPlayer(options[0]);
            }

            return request;
        }

        public ChoiceRequest ChooseColor(string prompt, IReadOnlyList<ResourceColor> candidates = null)
        {
            var request = ChoiceRequest.Color(prompt, Controller.PlayerId, candidates);

            if (candidates != null && candidates.Count == 1)
            {
                request.AnswerColor(candidates[0]);
            }

            return request;
        }

        /// <summary>
        /// Asks the controller to pick a card. Returns null when there is nothing
        /// to pick, which effects should treat as "this part does nothing".
        /// </summary>
        public ChoiceRequest ChooseCard(string prompt, IEnumerable<CardInstance> candidates)
        {
            var options = candidates.Select(c => c.InstanceId).ToList();
            if (options.Count == 0)
            {
                return null;
            }

            var request = ChoiceRequest.Card(prompt, Controller.PlayerId, options);

            if (options.Count == 1)
            {
                request.AnswerCard(options[0]);
            }

            return request;
        }

        /// <summary>Finds a card anywhere in play or in the discard, by instance id.</summary>
        public CardInstance CardById(int instanceId)
        {
            foreach (var player in Game.Players)
            {
                var inPlay = player.Compound.FirstOrDefault(c => c.InstanceId == instanceId);
                if (inPlay != null)
                {
                    return inPlay;
                }
            }

            return Game.Discard.FirstOrDefault(c => c.InstanceId == instanceId);
        }

        public ChoiceRequest AskYesNo(string prompt) =>
            ChoiceRequest.YesNo(prompt, Controller.PlayerId);

        /// <summary>
        /// Asks the controller to pick from a named list. Returns null when the
        /// list is empty, and answers itself when there is only one thing to pick.
        /// </summary>
        public ChoiceRequest ChooseOption(string prompt, IEnumerable<string> options)
        {
            var list = options.Distinct().ToList();
            if (list.Count == 0)
            {
                return null;
            }

            var request = ChoiceRequest.Option(prompt, Controller.PlayerId, list);

            if (list.Count == 1)
            {
                request.AnswerOption(list[0]);
            }

            return request;
        }

        public ChoiceRequest AskAmount(string prompt, int min, int max) =>
            ChoiceRequest.Amount(prompt, Controller.PlayerId, min, max);

        /// <summary>Asks a player other than the controller. Used by Equality.</summary>
        public ChoiceRequest AskPlayerYesNo(string prompt, int playerId) =>
            ChoiceRequest.YesNo(prompt, playerId);

        /// <summary>
        /// Offers a player other than the controller a set of plain choices.
        /// Used by Equality, where "yes" and "no" name two different losses and
        /// neither word says which.
        /// </summary>
        public ChoiceRequest AskPlayerOption(string prompt, int playerId, IReadOnlyList<string> options) =>
            ChoiceRequest.Option(prompt, playerId, options);

        // -------------------------------------------------------------- Damage

        public void DealDamage(int targetPlayerId, int amount) =>
            Game.DealDamage(Controller, Game.GetPlayer(targetPlayerId), amount);

        public void DealDamageToAllOpponents(int amount)
        {
            foreach (var opponent in Opponents.ToList())
            {
                Game.DealDamage(Controller, opponent, amount);
            }
        }

        public void DealDamageToEveryone(int amount)
        {
            foreach (var player in LivingPlayers.ToList())
            {
                Game.DealDamage(Controller, player, amount);
            }
        }

        /// <summary>Damage the controller inflicts on themselves.</summary>
        public void TakeDamage(int amount) => Game.DealDamage(Controller, Controller, amount);

        public PlayerState RandomOpponent() => Game.RandomOpponentOf(Controller);

        /// <summary>
        /// Health paid as a price rather than lost to an attack. Block does not
        /// stop it, and cards that reward you for being hurt stay quiet.
        /// </summary>
        public void LoseHealth(int amount) => Game.LoseHealth(Controller, amount);

        public void SetHealthTo(int playerId, int health)
        {
            var player = Game.GetPlayer(playerId);
            Game.LoseHealth(player, player.Health - health);
        }

        public void Kill(PlayerState player) => Game.Kill(player);

        /// <summary>
        /// Un-takes damage the controller just took. First Line of Defense pays
        /// for it in followers, so this is not healing and nothing that watches
        /// for healing should see it.
        /// </summary>
        public void RestoreHealth(int amount) => Controller.RestoreHealth(amount);

        // ------------------------------------------------- Health and followers

        public void Heal(int amount) => Game.Heal(Controller, amount);

        public void HealPlayer(int playerId, int amount) => Game.Heal(Game.GetPlayer(playerId), amount);

        public void GainFollowers(int amount) => Game.ChangeFollowers(Controller, amount);

        public void LoseFollowers(int amount) => Game.ChangeFollowers(Controller, -amount);

        public void PlayerGainsFollowers(int playerId, int amount) =>
            Game.ChangeFollowers(Game.GetPlayer(playerId), amount);

        public void PlayerLosesFollowers(int playerId, int amount) =>
            Game.ChangeFollowers(Game.GetPlayer(playerId), -amount);

        /// <summary>
        /// Moves a follower from an opponent to the controller. Does nothing if
        /// the opponent has none to give.
        /// </summary>
        public void StealFollower(int fromPlayerId)
        {
            var victim = Game.GetPlayer(fromPlayerId);
            if (victim.Followers <= 0)
            {
                return;
            }

            Game.ChangeFollowers(victim, -1);
            GainFollowers(1);
        }

        // --------------------------------------------------------------- Block

        public void GainBlock(int amount) => Game.GainBlock(Controller, amount);

        // ----------------------------------------------------------- Resources

        public void GainResource(ResourceColor color, int amount = 1) =>
            Controller.Resources.Add(color, amount);

        public int ResourceCount(ResourceColor color) => Controller.Resources[color];

        /// <summary>Spends resources the controller has. Returns how many it managed.</summary>
        public int SpendResource(ResourceColor color, int amount)
        {
            var spent = System.Math.Min(amount, Controller.Resources[color]);
            Controller.Resources.Remove(color, spent);
            return spent;
        }

        /// <summary>The colours a player actually holds, for "take a resource" effects.</summary>
        public IReadOnlyList<ResourceColor> ColorsHeldBy(int playerId)
        {
            var pool = PlayerById(playerId).Resources;
            return AllColors.Where(color => pool[color] > 0).ToList();
        }

        public static readonly IReadOnlyList<ResourceColor> AllColors = new[]
        {
            ResourceColor.Red, ResourceColor.Green, ResourceColor.Blue, ResourceColor.Yellow
        };

        /// <summary>
        /// Takes one resource of a colour from another player. Does nothing if
        /// they have none of it.
        /// </summary>
        public void StealResource(int fromPlayerId, ResourceColor color)
        {
            var victim = Game.GetPlayer(fromPlayerId);
            if (victim.Resources[color] <= 0)
            {
                return;
            }

            victim.Resources.Remove(color);
            GainResource(color);
        }

        // --------------------------------------------------------------- Cards

        public CardInstance DrawCard() => Game.DrawCard(Controller.PlayerId);

        public void DrawCards(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Game.DrawCard(Controller.PlayerId);
            }
        }

        /// <summary>Sends the resolving card itself to the discard pile.</summary>
        public void SacrificeSource()
        {
            if (Source != null && Controller.Compound.Contains(Source))
            {
                Game.SacrificeCard(Controller.PlayerId, Source.InstanceId);
            }
        }

        public void Sacrifice(int cardInstanceId) =>
            Game.SacrificeCard(Controller.PlayerId, cardInstanceId);

        public IReadOnlyList<CardInstance> MyUnits =>
            Controller.Compound.Where(c => c.Type == CardType.Unit).ToList();

        public IReadOnlyList<CardInstance> MyBlessings =>
            Controller.Compound.Where(c => c.Type == CardType.Blessing).ToList();

        public IReadOnlyList<CardInstance> DiscardedRituals =>
            Game.Discard.Where(c => c.Type == CardType.Ritual).ToList();

        public IReadOnlyList<CardInstance> DiscardPile => Game.Discard;

        public int HandSize => Controller.Hand.Count;

        /// <summary>Whether nobody at the table is holding more cards than you.</summary>
        public bool HasMostCardsInHand =>
            Opponents.All(opponent => opponent.Hand.Count < Controller.Hand.Count);

        public IReadOnlyList<CardInstance> UnitsOf(int playerId) =>
            PlayerById(playerId).Compound.Where(c => c.Type == CardType.Unit).ToList();

        public IReadOnlyList<CardInstance> CompoundOf(int playerId) =>
            PlayerById(playerId).Compound.ToList();

        /// <summary>How many copies of a card the controller has in play.</summary>
        public int CopiesInPlay(string definitionId) =>
            Controller.Compound.Count(c => c.Definition.Id == definitionId);

        /// <summary>Hands the resolving card over to another player's compound.</summary>
        public void GiveSourceTo(int playerId) =>
            Game.MoveToCompound(Source, Controller, Game.GetPlayer(playerId));

        /// <summary>Every card on the table, in every compound.</summary>
        public IReadOnlyList<CardInstance> AllCardsInPlay =>
            Game.Players.SelectMany(p => p.Compound).ToList();

        /// <summary>Whose compound a card is sitting in, or null if it is not in play.</summary>
        public PlayerState OwnerOf(CardInstance card) =>
            card == null ? null : Game.Players.FirstOrDefault(p => p.Compound.Contains(card));

        /// <summary>
        /// Trades the resolving card for one in somebody else's compound. Soul
        /// Swapper is the only card that does this.
        /// </summary>
        public void SwapSourceWith(CardInstance other) => Game.SwapBetweenCompounds(Source, other);

        /// <summary>The cards still on offer in the draft zone.</summary>
        public IReadOnlyList<CardInstance> DraftZone => Game.DraftZone;

        /// <summary>Sets this draft's marker for one of the three draft Blessings.</summary>
        public void MarkInDraft(DraftMarker marker, int cardInstanceId) =>
            Game.MarkInDraft(marker, cardInstanceId, Controller.PlayerId);

        /// <summary>Plays the top card of the deck at no cost.</summary>
        public void PlayTopOfDeckFree() => Game.PlayCardForFree(Game.TakeTopOfDeck(), Controller);

        /// <summary>
        /// Plays a Ritual out of hand for free and throws its effect away. It Who
        /// Consumes eats the Ritual rather than casting it, but the act still
        /// counts as an activation for anything keeping score.
        /// </summary>
        public void ActivateRitualWithoutEffect(int cardInstanceId)
        {
            var ritual = Controller.Hand.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (ritual == null || ritual.Type != CardType.Ritual)
            {
                return;
            }

            Controller.Hand.Remove(ritual);
            Game.DiscardDirectly(ritual);
            Game.PlayRitual(ritual, Controller, runEffect: false);
        }

        /// <summary>Resolves a card out of the discard pile without paying for it.</summary>
        public void ActivateFromDiscard(int cardInstanceId)
        {
            var card = Game.Discard.FirstOrDefault(c => c.InstanceId == cardInstanceId);
            if (card != null)
            {
                Game.ActivateCard(card, Controller);
            }
        }

        // ------------------------------------------------------------ Counters

        public int Counter(string name) => Source?.GetCounter(name) ?? 0;

        public void AddCounter(string name, int amount = 1)
        {
            if (Source == null)
            {
                return;
            }

            Source.AddCounter(name, EffectModifiers.ModifyCounterGain(Game, Controller, amount));
        }

        /// <summary>Puts counters on some other card, e.g. Ominous Eye's static counters.</summary>
        public void AddCounterTo(CardInstance card, string name, int amount = 1)
        {
            card?.AddCounter(name, EffectModifiers.ModifyCounterGain(Game, Controller, amount));
        }

        public void RemoveCounterFrom(CardInstance card, string name, int amount = 1)
        {
            card?.RemoveCounter(name, amount);
        }

        /// <summary>Forces a counter to an exact value. Soul Swapper resets to 3 after a swap.</summary>
        public void SetCounter(string name, int value) => Source?.SetCounter(name, value);

        public int PlayerCounter(int playerId, string name) => PlayerById(playerId).GetCounter(name);

        public void AddPlayerCounter(int playerId, string name, int amount = 1)
        {
            var player = PlayerById(playerId);
            player.AddCounter(name, EffectModifiers.ModifyCounterGain(Game, Controller, amount));
        }

        // --------------------------------------------------------------- Flags

        public void SetFlag(string name) => Controller.SetFlag(name);

        public void SetFlagNextTurn(string name) => Controller.SetFlagNextTurn(name);

        public void SetFlagNextTurnForEveryone(string name)
        {
            foreach (var player in LivingPlayers)
            {
                player.SetFlagNextTurn(name);
            }
        }

        /// <summary>Claims a card's once-a-turn allowance; false if already spent.</summary>
        public bool TakeOncePerTurn(string key) => Game.TakeOncePerTurn(key);

        // ---------------------------------------------------------------- Dice

        /// <summary>Rolls the black die. Does not activate anybody's units.</summary>
        public int RollAuxiliaryDie() => Game.RollAuxiliaryDie(Controller);

        // --------------------------------------------------------------- Queue

        /// <summary>
        /// Schedules another effect to resolve after this one finishes, rather
        /// than nesting inside it. Triggered abilities use this so they cannot
        /// interrupt the effect that set them off half-way through.
        /// </summary>
        public void Enqueue(CardInstance source, PlayerState controller, EffectRoutine routine, string description)
        {
            Game.EnqueueEffect(source, controller, routine, description);
        }
    }
}
