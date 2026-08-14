using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// The always-on cards. A Blessing like Halphas Wings never "activates" - it
    /// just quietly changes what other cards do, so instead of each card checking
    /// for it, every damage, heal, follower change and so on is routed through
    /// <see cref="GameState"/> and lands in one of the hooks below.
    ///
    /// Hooks named Modify* return the adjusted number. Hooks named After* react to
    /// something that already happened, and queue their own effect rather than
    /// running it inline - a trigger must not interrupt the effect that set it off.
    /// </summary>
    public static class EffectModifiers
    {
        // -------------------------------------------------------------- Damage

        public static int ModifyDamage(GameState game, PlayerState source, PlayerState target, int amount)
        {
            // Virgin Sacrifice.
            if (target.HasFlag(TurnFlags.ImmuneToDamage))
            {
                return 0;
            }

            // Bribery: the price of the healing is a turn of pacifism.
            if (source != null && source.HasFlag(TurnFlags.CannotDealDamage))
            {
                return 0;
            }

            // Halphas Wings blunts both ends, but only of real blows.
            if (amount >= 2 && source != null && source.HasInPlay(CardIds.HalphasWings))
            {
                amount--;
            }

            if (amount >= 2 && target.HasInPlay(CardIds.HalphasWings))
            {
                amount--;
            }

            // Bloody Mooner softens the damage your own cards do to you.
            if (source == target && target.HasFlag(TurnFlags.SelfHarmSoftened))
            {
                amount--;
            }

            return amount;
        }

        /// <summary>
        /// <paramref name="amount"/> is the damage dealt; <paramref name="healthLost"/>
        /// is how much of it got past Block to actually reach health. Most cards
        /// care about the blow that was struck, but First Line of Defense can only
        /// buy back a wound that landed.
        /// </summary>
        public static void AfterDamage(
            GameState game, PlayerState source, PlayerState target, int amount, int healthLost)
        {
            // Asmodeus counts every wound at the table, not just its owner's.
            foreach (var player in game.Players)
            {
                foreach (var asmodeus in player.Compound.Where(c => c.Definition.Id == CardIds.Asmodeus).ToList())
                {
                    asmodeus.AddCounter(Counters.Violence, ModifyCounterGain(game, player, 1));
                }
            }

            // Pain Lovers pays out when you hurt yourself.
            if (source == target && target.HasInPlay(CardIds.PainLovers))
            {
                game.EnqueueEffect(target.FindInPlay(CardIds.PainLovers), target,
                    CommonEffects.DrawCards(1), "Pain Lovers");
            }

            // Jormugandr's Fan Club: an opponent who is hurt loses that much
            // faith as well. Only opponents of its owner, and never on damage
            // its owner took themselves - the follower loss is applied directly
            // rather than queued, so it lands with the wound that caused it and
            // cannot be read as a separate event by anything counting effects.
            foreach (var player in game.Players)
            {
                if (player == target || !player.HasInPlay(CardIds.JormugandrsFanClub))
                {
                    continue;
                }

                target.LoseFollowers(healthLost);
            }

            // Blood Collector fills up on its owner's misfortune.
            foreach (var collector in target.Compound
                         .Where(c => c.Definition.Id == CardIds.BloodCollector).ToList())
            {
                collector.AddCounter(Counters.Blood, ModifyCounterGain(game, target, 1));
            }

            // Masochist turns this turn's health loss into damage. The damage it
            // deals is retaliation, so it cannot set another Masochist off and
            // start the two of them trading blows forever.
            if (target.HasFlag(TurnFlags.VengefulHealthLoss))
            {
                game.EnqueueEffect(target.FindInPlay(CardIds.Masochist), target,
                    CommonEffects.RetaliateAgainstChosenOpponent(
                        amount, $"Masochist - deal {amount} damage to:"), "Masochist");
            }

            // Compound Landmines bites whoever set them off.
            if (source != null && source != target && target.HasFlag(TurnFlags.LandminesArmed))
            {
                game.EnqueueEffect(target.FindInPlay(CardIds.CompoundLandmines), target,
                    CommonEffects.Retaliate(source.PlayerId, 2), "Compound Landmines");
            }

            // First Line of Defense offers to pay for the wound in followers. Only
            // offered when they can actually cover the bill, or it would be a free
            // cancel for anyone who had run their follower track down to nothing.
            if (healthLost > 0
                && target.HasInPlay(CardIds.FirstLineOfDefense)
                && target.Followers >= healthLost * 2)
            {
                game.EnqueueEffect(target.FindInPlay(CardIds.FirstLineOfDefense), target,
                    BlessingEffects.FirstLineOfDefense(healthLost), "First Line of Defense");
            }
        }

        // ------------------------------------------------------------- Healing

        public static int ModifyHealing(GameState game, PlayerState player, int amount)
        {
            // Wondrous Blood.
            return player.HasInPlay(CardIds.WondrousBlood) ? amount + 1 : amount;
        }

        // ----------------------------------------------------------- Followers

        /// <summary>
        /// Clown Cult reads every follower change backwards, and adds insult to it.
        /// This is why nothing calls PlayerState.GainFollowers directly.
        /// </summary>
        public static int ModifyFollowerChange(GameState game, PlayerState player, int amount)
        {
            if (!player.HasInPlay(CardIds.ClownCult))
            {
                return amount;
            }

            // Gain n becomes lose n+1; lose n becomes gain n+1.
            return amount > 0 ? -(amount + 1) : -amount + 1;
        }

        public static void AfterFollowersGained(GameState game, PlayerState player, int amount)
        {
            var before = player.FollowersGainedThisTurn - amount;

            // Powered by the People fires on the first gain of the turn only.
            if (before == 0 && player.HasInPlay(CardIds.PoweredByThePeople))
            {
                game.EnqueueEffect(player.FindInPlay(CardIds.PoweredByThePeople), player,
                    CommonEffects.DamageChosenOpponent(1, "Powered by the People - deal 1 damage to:"),
                    "Powered by the People");
            }

            // Suffering from Success sits in a rival's compound and punishes success.
            if (before < 2 && player.FollowersGainedThisTurn >= 2 &&
                player.HasInPlay(CardIds.SufferingFromSuccess))
            {
                game.EnqueueEffect(player.FindInPlay(CardIds.SufferingFromSuccess), player,
                    CommonEffects.Retaliate(player.PlayerId, 1), "Suffering from Success");
            }
        }

        // --------------------------------------------------------------- Block

        public static int ModifyBlockGain(GameState game, PlayerState player, int amount)
        {
            // Siege, and Compound Landmines pinning your own Block at 1.
            if (player.HasFlag(TurnFlags.CannotGainBlock) || player.HasFlag(TurnFlags.LandminesArmed))
            {
                return 0;
            }

            // Boon of.
            return player.HasInPlay(CardIds.BoonOf) ? amount + 1 : amount;
        }

        // --------------------------------------------------------------- Cards

        public static void AfterCardDrawn(GameState game, PlayerState player)
        {
            if (!player.HasInPlay(CardIds.KnowledgeIsPower))
            {
                return;
            }

            game.EnqueueEffect(player.FindInPlay(CardIds.KnowledgeIsPower), player,
                CommonEffects.DamageChosenOpponent(1, "Knowledge is Power - deal 1 damage to:"),
                "Knowledge is Power");
        }

        // ---------------------------------------------------------------- Dice

        public static void AfterAuxiliaryRoll(GameState game, PlayerState roller, int value)
        {
            // Casino pays on the second black die of the turn, and only that one.
            if (roller.AuxiliaryDiceRolledThisTurn != 2 || !roller.HasInPlay(CardIds.Casino))
            {
                return;
            }

            game.EnqueueEffect(roller.FindInPlay(CardIds.Casino), roller,
                CommonEffects.DamageChosenOpponent(1, "Casino - deal 1 damage to:"), "Casino");
        }

        // ------------------------------------------------------------ Counters

        /// <summary>
        /// Cthulu's Maw doubles up on counters. The card says "you can choose to",
        /// but a free counter is never unwelcome, so it is taken automatically.
        /// </summary>
        public static int ModifyCounterGain(GameState game, PlayerState owner, int amount)
        {
            return amount > 0 && owner != null && owner.HasInPlay(CardIds.CthuluSMaw)
                ? amount + 1
                : amount;
        }

        // ------------------------------------------------------------ End of turn

        /// <summary>
        /// The Blessings that wait until the turn is over and then look back at
        /// what happened. Queued in a fixed order so the same board always plays
        /// out the same way.
        /// </summary>
        public static void QueueEndOfTurnTriggers(GameState game)
        {
            foreach (var player in game.LivingPlayers.ToList())
            {
                foreach (var card in player.Compound.ToList())
                {
                    var routine = EndOfTurnRoutine(card, player);
                    if (routine != null)
                    {
                        game.EnqueueEffect(card, player, routine, card.Title);
                    }
                }
            }
        }

        private static EffectRoutine EndOfTurnRoutine(CardInstance card, PlayerState player)
        {
            switch (card.Definition.Id)
            {
                // Medicine Cabinet: if you took damage, heal 1.
                case CardIds.MedicineCabinet when player.DamageTakenThisTurn > 0:
                    return context => Heal(context, 1);

                // Masochism: if you took damage, gain 1 follower.
                case CardIds.Masochism when player.DamageTakenThisTurn > 0:
                    return context => GainFollowers(context, 1);

                // Star Eyed: if you gained a follower, gain another.
                case CardIds.StarEyed when player.FollowersGainedThisTurn > 0:
                    return context => GainFollowers(context, 1);

                // Mongol Mythology: more than three damage dealt earns a follower.
                case CardIds.MongolMythology when player.DamageDealtThisTurn > 3:
                    return context => GainFollowers(context, 1);

                // Whore's Revenge: if you were hurt, hurt somebody back.
                case CardIds.WhoreSRevenge when player.DamageTakenThisTurn > 0:
                    return CommonEffects.DamageChosenOpponent(1, "Whore's Revenge - deal 1 damage to:");

                // Bloodthirst: if you drew blood, draw more.
                case CardIds.Bloodthirst when player.DamageDealtThisTurn > 0:
                    return CommonEffects.DamageChosenOpponent(1, "Bloodthirst - deal 1 damage to:");

                // Three's a Crowd.
                case CardIds.ThreeSACrowd when player.UnitsTriggeredThisTurn >= 3:
                    return CommonEffects.DamageChosenOpponent(1, "Three's a Crowd - deal 1 damage to:");

                // Meatshield spends whatever Block is left over.
                case CardIds.Meatshield when player.Block > 0:
                    return CommonEffects.DamageChosenOpponent(
                        player.Block, $"Meatshield - deal {player.Block} damage to:");

                default:
                    return null;
            }
        }

        private static IEnumerator<ChoiceRequest> Heal(EffectContext context, int amount)
        {
            context.Heal(amount);
            yield break;
        }

        private static IEnumerator<ChoiceRequest> GainFollowers(EffectContext context, int amount)
        {
            context.GainFollowers(amount);
            yield break;
        }
    }
}
