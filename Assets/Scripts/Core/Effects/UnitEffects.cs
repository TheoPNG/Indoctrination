using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// What every Unit does when a die wakes it up. Bodies are grouped by card
    /// colour, in the same order as the design spreadsheet, so a card is easy to
    /// find from its row.
    ///
    /// House rule throughout: a card that says "deal damage" without naming a
    /// victim lets its controller pick one. That is what <see cref="Damage"/> does.
    /// </summary>
    internal static class UnitEffects
    {
        /// <summary>Deal damage to an opponent of the controller's choosing.</summary>
        private static IEnumerator<ChoiceRequest> Damage(EffectContext c, int amount, string prompt = null)
        {
            return CommonEffects.DamageChosenOpponent(amount, prompt)(c);
        }

        // =================================================================== Blue

        /// <summary>Lose 1 follower and take 1 damage. Its owner is the victim.</summary>
        internal static IEnumerator<ChoiceRequest> DoubleAgent(EffectContext c)
        {
            c.LoseFollowers(1);
            c.TakeDamage(1);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> ItWhoConsumes(EffectContext c)
        {
            c.PlayTopOfDeckFree();
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> Slanderist(EffectContext c)
        {
            var target = c.ChooseOpponent("Slanderist - which opponent loses a follower?");
            if (target == null)
            {
                yield break;
            }

            yield return target;
            c.PlayerLosesFollowers(target.ChosenPlayerId, 1);
        }

        /// <summary>Damage equal to 1 plus the Rituals this card has watched.</summary>
        internal static IEnumerator<ChoiceRequest> Ritualist(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 1 + c.Counter(Counters.Ritual))))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> WisdomHoarder(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, c.HasMostCardsInHand ? 2 : 1)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> ResearcherOfTheOldWays(EffectContext c)
        {
            c.DrawCard();
            foreach (var step in CommonEffects.Run(Damage(c, 1)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> HeWhoRemembers(EffectContext c)
        {
            c.GainResource(ResourceColor.Blue, c.Game.DraftNumber);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> HydroPlant(EffectContext c)
        {
            c.GainResource(ResourceColor.Blue);
            yield break;
        }

        /// <summary>Pays out only when two people rolled a 1.</summary>
        internal static IEnumerator<ChoiceRequest> SnakeEyes(EffectContext c)
        {
            if (c.Game.RolledValues.Count(value => value == 1) >= 2)
            {
                c.DrawCards(2);
            }

            yield break;
        }

        internal static IEnumerator<ChoiceRequest> OneWithTheChildren(EffectContext c)
        {
            var target = c.ChooseOpponent("One With the Children - which opponent loses a follower?");
            if (target == null)
            {
                yield break;
            }

            yield return target;
            c.PlayerLosesFollowers(target.ChosenPlayerId, 1);
        }

        internal static IEnumerator<ChoiceRequest> Baal(EffectContext c)
        {
            c.AddCounter(Counters.Scheme);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> Propaganda(EffectContext c)
        {
            var target = c.ChooseOpponent("Propaganda - steal a follower from:");
            if (target == null)
            {
                yield break;
            }

            yield return target;
            c.StealFollower(target.ChosenPlayerId);
        }

        /// <summary>
        /// Fishes a Ritual back out of the discard. A note is kept on the card
        /// itself of which ones it has already taken, so it cannot keep recycling
        /// the same one.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> WorshiperOfTheBoneGod(EffectContext c)
        {
            var candidates = c.DiscardedRituals
                .Where(card => c.Counter(TakenKey(card)) == 0)
                .ToList();

            var choice = c.ChooseCard("Worshiper of the Bone God - take which Ritual?", candidates);
            if (choice == null)
            {
                yield break;
            }

            yield return choice;

            var taken = candidates.First(card => card.InstanceId == choice.ChosenCardId);
            c.Source?.AddCounter(TakenKey(taken));
            c.Game.ReturnFromDiscard(c.Controller.PlayerId, taken.InstanceId);
        }

        private static string TakenKey(CardInstance card) => "taken:" + card.Definition.Id;

        internal static IEnumerator<ChoiceRequest> Initiate4(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, c.CopiesInPlay(CardIds.Initiate4))))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> ChiefSacrificer(EffectContext c)
        {
            c.AddPlayerCounter(c.Controller.PlayerId, Counters.RitualEcho);
            yield break;
        }

        // ================================================================== Green

        /// <summary>Damage that also costs the victim a follower.</summary>
        internal static IEnumerator<ChoiceRequest> JormTrustEater(EffectContext c)
        {
            var target = c.ChooseOpponent("Jorm, Trust Eater - deal 1 damage to:");
            if (target == null)
            {
                yield break;
            }

            yield return target;

            var before = c.PlayerById(target.ChosenPlayerId).Health;
            c.DealDamage(target.ChosenPlayerId, 1);

            if (c.PlayerById(target.ChosenPlayerId).Health < before)
            {
                c.PlayerLosesFollowers(target.ChosenPlayerId, 1);
            }
        }

        /// <summary>Followers traded for force, two followers to the point.</summary>
        internal static IEnumerator<ChoiceRequest> ManipulatorOfTheMasses(EffectContext c)
        {
            var spend = c.AskAmount(
                "Manipulator of the Masses - spend how many followers?",
                0, System.Math.Min(6, c.Controller.Followers));
            yield return spend;

            c.LoseFollowers(spend.ChosenAmount);

            foreach (var step in CommonEffects.Run(Damage(c, 1 + spend.ChosenAmount / 2)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> Communist(EffectContext c)
        {
            foreach (var player in c.LivingPlayers.ToList())
            {
                c.PlayerGainsFollowers(player.PlayerId, 1);
                c.Game.LoseHealth(player, 1);
            }

            yield break;
        }

        internal static IEnumerator<ChoiceRequest> WallBuilder(EffectContext c)
        {
            c.GainBlock(1);
            yield break;
        }

        /// <summary>Keeps stacking Block for as long as the black die stays low.</summary>
        internal static IEnumerator<ChoiceRequest> TheBeastmaster(EffectContext c)
        {
            while (true)
            {
                c.GainBlock(1);

                if (c.RollAuxiliaryDie() > 2)
                {
                    yield break;
                }
            }
        }

        internal static IEnumerator<ChoiceRequest> FilipinaNurse(EffectContext c)
        {
            c.Heal(1);
            c.GainFollowers(1);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> Bodyguard(EffectContext c)
        {
            c.GainBlock(2);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> MasterMarketer(EffectContext c)
        {
            c.GainFollowers(2);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> AngryDrunkMonkey(EffectContext c)
        {
            c.GainFollowers(2);
            c.TakeDamage(1);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> GainOneFollower(EffectContext c)
        {
            c.GainFollowers(1);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> GainTwoFollowers(EffectContext c)
        {
            c.GainFollowers(2);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> Security(EffectContext c)
        {
            c.GainBlock(1);
            foreach (var step in CommonEffects.Run(Damage(c, 1)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> TheAllmother(EffectContext c)
        {
            c.Heal(2);
            c.GainFollowers(1);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> HealOne(EffectContext c)
        {
            c.Heal(1);
            yield break;
        }

        /// <summary>Trade a Blessing you no longer need for three followers.</summary>
        internal static IEnumerator<ChoiceRequest> IsHeOnMeth(EffectContext c)
        {
            if (c.MyBlessings.Count == 0)
            {
                yield break;
            }

            var offer = c.AskYesNo("Is he on meth? - sacrifice a Blessing to gain 3 followers?");
            yield return offer;

            if (!offer.ChoseYes)
            {
                yield break;
            }

            var choice = c.ChooseCard("Sacrifice which Blessing?", c.MyBlessings);
            if (choice == null)
            {
                yield break;
            }

            yield return choice;

            c.Sacrifice(choice.ChosenCardId);
            c.GainFollowers(3);
        }

        // ==================================================================== Red

        internal static IEnumerator<ChoiceRequest> ConfusedMan(EffectContext c, int dieValue)
        {
            if (dieValue == 1)
            {
                c.TakeDamage(1);
                yield break;
            }

            foreach (var step in CommonEffects.Run(Damage(c, 1)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> QuestionableDoctor(EffectContext c, int dieValue)
        {
            if (dieValue == 3)
            {
                c.Heal(2);
            }
            else
            {
                c.TakeDamage(1);
            }

            yield break;
        }

        internal static IEnumerator<ChoiceRequest> AxeLicker(EffectContext c, int dieValue)
        {
            if (dieValue == 5)
            {
                c.TakeDamage(1);
                yield break;
            }

            foreach (var step in CommonEffects.Run(Damage(c, 2)))
            {
                yield return step;
            }
        }

        /// <summary>Too drunk to aim - the game picks the target, and it may be you.</summary>
        internal static IEnumerator<ChoiceRequest> DrunkenFollower(EffectContext c)
        {
            var everyone = c.LivingPlayers;
            if (everyone.Count > 0)
            {
                c.DealDamage(everyone[c.Game.RandomBelow(everyone.Count)].PlayerId, 1);
            }

            yield break;
        }

        /// <summary>
        /// Two damage, then a coin-flip chain: every even roll finds a new victim
        /// and buys another roll. It runs out of victims before it runs out of luck.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> TheBeast(EffectContext c)
        {
            var first = c.ChooseOpponent("The Beast - deal 2 damage to:");
            if (first == null)
            {
                yield break;
            }

            yield return first;
            c.DealDamage(first.ChosenPlayerId, 2);

            var alreadyHit = new HashSet<int> { first.ChosenPlayerId };

            while (c.RollAuxiliaryDie() % 2 == 0)
            {
                var remaining = c.Opponents.Where(p => !alreadyHit.Contains(p.PlayerId)).ToList();
                var next = c.ChoosePlayer("The Beast - deal 1 damage to:", remaining);
                if (next == null)
                {
                    yield break;
                }

                yield return next;

                c.DealDamage(next.ChosenPlayerId, 1);
                alreadyHit.Add(next.ChosenPlayerId);
            }
        }

        internal static IEnumerator<ChoiceRequest> DealTwo(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 2)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> DealThree(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 3)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> DealFour(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 4)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> DealOne(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 1)))
            {
                yield return step;
            }
        }

        /// <summary>One damage now, and two more spread over the coming turns.</summary>
        internal static IEnumerator<ChoiceRequest> Arsonist(EffectContext c)
        {
            var target = c.ChooseOpponent("Arsonist - deal 1 damage and set alight:");
            if (target == null)
            {
                yield break;
            }

            yield return target;

            c.DealDamage(target.ChosenPlayerId, 1);
            c.AddPlayerCounter(target.ChosenPlayerId, Counters.Flame, 2);
        }

        internal static IEnumerator<ChoiceRequest> Vampire(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 1)))
            {
                yield return step;
            }

            c.Heal(1);
        }

        /// <summary>A second swing once your compound is busy enough.</summary>
        internal static IEnumerator<ChoiceRequest> BeekeeperCultist(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 1)))
            {
                yield return step;
            }

            if (c.Controller.UnitsTriggeredThisTurn < 5)
            {
                yield break;
            }

            foreach (var step in CommonEffects.Run(Damage(c, 1, "Beekeeper Cultist - deal 1 more damage to:")))
            {
                yield return step;
            }
        }

        /// <summary>The flame can be laid on anybody, including yourself.</summary>
        internal static IEnumerator<ChoiceRequest> FireBreather(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 1)))
            {
                yield return step;
            }

            var burn = c.ChooseAnyPlayer("Fire Breather - place a flame counter on:");
            if (burn == null)
            {
                yield break;
            }

            yield return burn;
            c.AddPlayerCounter(burn.ChosenPlayerId, Counters.Flame);
        }

        internal static IEnumerator<ChoiceRequest> FriendOfTheBeasts(EffectContext c)
        {
            c.DealDamageToEveryone(1);
            yield break;
        }

        /// <summary>
        /// Gain 1 follower, or deal 1 damage to any player.
        ///
        /// Deliberately a named pair rather than a yes/no. The card offers two
        /// different things, and "yes" and "no" are only meaningful next to a
        /// question - which the board no longer shows, because the card itself
        /// is on screen saying what it does. The options have to carry their own
        /// meaning.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> Pentagram(EffectContext c)
        {
            const string follower = "+1 follower";
            const string damage = "1 damage";

            var choice = c.ChooseOption("Pentagram", new[] { follower, damage });
            yield return choice;

            if (choice.ChosenOption == follower)
            {
                c.GainFollowers(1);
                yield break;
            }

            var target = c.ChooseAnyPlayer("Pentagram - deal 1 damage to:");
            if (target == null)
            {
                yield break;
            }

            yield return target;
            c.DealDamage(target.ChosenPlayerId, 1);
        }

        internal static IEnumerator<ChoiceRequest> GainRed(EffectContext c)
        {
            c.GainResource(ResourceColor.Red);
            yield break;
        }

        /// <summary>Only speaks when three sixes come up at once.</summary>
        internal static IEnumerator<ChoiceRequest> Satanist(EffectContext c)
        {
            if (c.Game.RolledValues.Count(value => value == 6) < 3)
            {
                yield break;
            }

            var target = c.ChooseAnyPlayer("Satanist - reduce which player to 1 health?");
            if (target == null)
            {
                yield break;
            }

            yield return target;
            c.SetHealthTo(target.ChosenPlayerId, 1);
        }

        internal static IEnumerator<ChoiceRequest> KoolAid(EffectContext c)
        {
            var available = System.Math.Min(3, c.ResourceCount(ResourceColor.Red));
            if (available == 0)
            {
                yield break;
            }

            var spend = c.AskAmount("Kool Aid - spend how many Red to heal?", 0, available);
            yield return spend;

            var spent = c.SpendResource(ResourceColor.Red, spend.ChosenAmount);
            c.Heal(spent);
        }

        internal static IEnumerator<ChoiceRequest> BloodCollector(EffectContext c)
        {
            c.GainResource(ResourceColor.Red, c.Counter(Counters.Blood));
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> HierophantsFavorite(EffectContext c)
        {
            c.TakeDamage(1);
            c.GainFollowers(2);
            yield break;
        }

        /// <summary>Opens the wound that everything else this turn pours through.</summary>
        internal static IEnumerator<ChoiceRequest> Masochist(EffectContext c)
        {
            c.SetFlag(TurnFlags.VengefulHealthLoss);
            c.TakeDamage(1);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> BloodyMooner(EffectContext c)
        {
            c.SetFlag(TurnFlags.SelfHarmSoftened);
            c.DealDamageToEveryone(2);
            yield break;
        }

        /// <summary>Pays out everything the table has suffered since it arrived.</summary>
        internal static IEnumerator<ChoiceRequest> Asmodeus(EffectContext c)
        {
            var amount = c.Counter(Counters.Violence) / 2;
            if (amount > 0)
            {
                c.DealDamageToEveryone(amount);
            }

            yield break;
        }

        /// <summary>Trades your own defences for a trap under them.</summary>
        internal static IEnumerator<ChoiceRequest> CompoundLandmines(EffectContext c)
        {
            c.SetFlag(TurnFlags.LandminesArmed);
            c.Controller.ClearBlock();
            c.Controller.AddBlock(1);
            yield break;
        }

        // ================================================================= Yellow

        internal static IEnumerator<ChoiceRequest> Bop(EffectContext c, int dieValue)
        {
            if (dieValue == 4)
            {
                c.GainFollowers(1);
            }
            else
            {
                c.TakeDamage(1);
            }

            yield break;
        }

        /// <summary>Every point of healing this turn sharpens the bite.</summary>
        internal static IEnumerator<ChoiceRequest> AlmostAVampire(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, 1 + c.Controller.HealthGainedThisTurn)))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> GainBlue(EffectContext c)
        {
            c.GainResource(ResourceColor.Blue);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> GainGreen(EffectContext c)
        {
            c.GainResource(ResourceColor.Green);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> GainYellow(EffectContext c)
        {
            c.GainResource(ResourceColor.Yellow);
            yield break;
        }

        /// <summary>Take N resources, one colour choice at a time.</summary>
        internal static IEnumerator<ChoiceRequest> GainChosenResources(EffectContext c, int count, string label)
        {
            for (var i = 0; i < count; i++)
            {
                var choice = c.ChooseColor($"{label} - pick a resource ({i + 1} of {count}):");
                yield return choice;
                c.GainResource(choice.ChosenColor);
            }
        }

        internal static IEnumerator<ChoiceRequest> SacrificialLamb(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(GainChosenResources(c, 4, "Sacrificial Lamb")))
            {
                yield return step;
            }

            c.SacrificeSource();
        }

        /// <summary>
        /// "Heal 0 health" is not a typo - it is there so Wondrous Blood can turn
        /// nothing into one, which is why the heal is called at all.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> AlternativeMedicine(EffectContext c)
        {
            c.Heal(0);
            c.GainFollowers(2);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> MasterShaman(EffectContext c)
        {
            c.Heal(2);
            foreach (var step in CommonEffects.Run(Damage(c, 1)))
            {
                yield return step;
            }
        }

        /// <summary>Jams a rival's card so its next activation does nothing.</summary>
        internal static IEnumerator<ChoiceRequest> OminousEye(EffectContext c)
        {
            var target = c.ChooseOpponent("Ominous Eye - jam a card belonging to:");
            if (target == null)
            {
                yield break;
            }

            yield return target;

            var choice = c.ChooseCard(
                "Ominous Eye - put a static counter on:", c.UnitsOf(target.ChosenPlayerId));
            if (choice == null)
            {
                yield break;
            }

            yield return choice;
            c.AddCounterTo(c.CardById(choice.ChosenCardId), Counters.Static);
        }

        internal static IEnumerator<ChoiceRequest> SuspiciousChef(EffectContext c)
        {
            foreach (var step in CommonEffects.Run(Damage(c, c.Counter(Counters.Meal))))
            {
                yield return step;
            }
        }

        internal static IEnumerator<ChoiceRequest> Valefar(EffectContext c)
        {
            var target = c.ChoosePlayer(
                "Valefar - take a resource from:",
                c.Opponents.Where(p => p.Resources.Total > 0));
            if (target == null)
            {
                yield break;
            }

            yield return target;

            var colors = c.ColorsHeldBy(target.ChosenPlayerId);
            var color = c.ChooseColor("Valefar - take which resource?", colors);
            yield return color;

            c.StealResource(target.ChosenPlayerId, color.ChosenColor);
        }

        internal static IEnumerator<ChoiceRequest> BeingOfHeartlessness(EffectContext c)
        {
            c.GainResource(ResourceColor.Yellow, c.Counter(Counters.Trash));
            yield break;
        }

        /// <summary>
        /// Soul Swapper collects swap counters until it can afford to trade places
        /// with something worth having. Cost is compared as a plain total of
        /// resources, so BBB and RGY are both a three.
        ///
        /// Swapping spends the whole stack: the counters drop back to three and it
        /// has to climb again from its new home.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> SoulSwapper(EffectContext c)
        {
            c.AddCounter(Counters.Swap);

            var budget = c.Counter(Counters.Swap);
            var reachable = c.Opponents
                .SelectMany(opponent => opponent.Compound)
                .Where(card => card.Type == CardType.Unit
                               && !card.Cost.IsSpecial
                               && card.Cost.Total <= budget)
                .ToList();

            if (reachable.Count == 0)
            {
                yield break;
            }

            var ask = c.AskYesNo($"Soul Swapper has {budget} swap counters. Swap it for a Unit?");
            yield return ask;

            if (!ask.ChoseYes)
            {
                yield break;
            }

            var choice = c.ChooseCard("Soul Swapper - trade places with:", reachable);
            if (choice == null)
            {
                yield break;
            }

            yield return choice;

            c.SwapSourceWith(c.CardById(choice.ChosenCardId));
            c.SetCounter(Counters.Swap, GameSettings.SoulSwapperBaseCounters);
        }

        /// <summary>
        /// Cthulu, the Cosmic moves a counter that already exists. It cannot invent
        /// a kind of counter that is not in the target's compound, so in practice it
        /// doubles up on something already being collected - or takes one away.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> CthuluTheCosmic(EffectContext c)
        {
            var targets = c.AllCardsInPlay
                .Where(card => c.OwnerOf(card).Compound.Any(other => other.CounterNames.Any()))
                .ToList();

            var choice = c.ChooseCard("Cthulu, the Cosmic - target which card?", targets);
            if (choice == null)
            {
                yield break;
            }

            yield return choice;

            var target = c.CardById(choice.ChosenCardId);
            var owner = c.OwnerOf(target);
            if (target == null || owner == null)
            {
                yield break;
            }

            // Named actions, not yes/no. "No" here is removing a counter, which
            // is a move in its own right rather than a refusal to make one.
            const string add = "Add a counter";
            const string remove = "Remove a counter";

            var adding = c.ChooseOption($"{target.Title}", new[] { add, remove });
            yield return adding;

            var isAdding = adding.ChosenOption == add;

            // Adding may use any counter type loose in that compound; removing is
            // limited to what is actually sitting on the card.
            var available = isAdding
                ? owner.Compound.SelectMany(card => card.CounterNames)
                : target.CounterNames;

            var kind = c.ChooseOption(
                isAdding ? "Add which counter?" : "Remove which counter?", available);

            if (kind == null)
            {
                yield break;
            }

            yield return kind;

            if (isAdding)
            {
                c.AddCounterTo(target, kind.ChosenOption);
            }
            else
            {
                c.RemoveCounterFrom(target, kind.ChosenOption);
            }
        }

        /// <summary>
        /// It Who Consumes' printed cost of "*": a Unit and a Blessing off your own
        /// board, and a Ritual out of your hand that is played for nothing.
        /// GameState checks all three are available before the card ever leaves
        /// hand, so none of these choices can come up empty.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> ItWhoConsumesCost(EffectContext c)
        {
            var unit = c.ChooseCard(
                "It Who Consumes - sacrifice which Unit?",
                c.MyUnits.Where(card => card != c.Source));

            if (unit != null)
            {
                yield return unit;
                c.Sacrifice(unit.ChosenCardId);
            }

            var blessing = c.ChooseCard("It Who Consumes - sacrifice which Blessing?", c.MyBlessings);
            if (blessing != null)
            {
                yield return blessing;
                c.Sacrifice(blessing.ChosenCardId);
            }

            var ritual = c.ChooseCard(
                "It Who Consumes - activate which Ritual? (its effect does nothing)",
                c.Controller.Hand.Where(card => card.Type == CardType.Ritual));

            if (ritual != null)
            {
                yield return ritual;
                c.ActivateRitualWithoutEffect(ritual.ChosenCardId);
            }
        }
    }
}
