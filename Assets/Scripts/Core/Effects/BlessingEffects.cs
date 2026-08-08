using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// The few Blessings that actually do something at a moment in time. Most of
    /// them are standing rules and live in <see cref="EffectModifiers"/> instead.
    /// This file also holds the shared "when this card arrives" bodies.
    /// </summary>
    internal static class BlessingEffects
    {
        /// <summary>A cheap gamble on the crowd, resolved at the start of the turn.</summary>
        internal static IEnumerator<ChoiceRequest> HumanZoo(EffectContext c)
        {
            if (c.RollAuxiliaryDie() <= 2)
            {
                c.LoseFollowers(1);
            }
            else
            {
                c.GainFollowers(1);
            }

            yield break;
        }

        /// <summary>Cards that arrive with counters already on them.</summary>
        internal static EffectRoutine StartWithCounters(string name, int amount)
        {
            return c => StartWithCountersSteps(c, name, amount);
        }

        private static IEnumerator<ChoiceRequest> StartWithCountersSteps(
            EffectContext c, string name, int amount)
        {
            // Deliberately not routed through Cthulu's Maw: these are printed on
            // the card rather than gained, so there is nothing for it to double.
            c.Source?.AddCounter(name, amount);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> LoseMaxHealth(EffectContext c)
        {
            c.Controller.ModifyMaxHealth(-1);
            yield break;
        }

        /// <summary>
        /// Double Agent and Suffering from Success are bought by you but played
        /// into somebody else's compound, where they work against their new owner.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> PlantOnOpponent(EffectContext c)
        {
            var target = c.ChooseOpponent($"{c.Source?.Title} - play it into whose compound?");
            if (target == null)
            {
                yield break;
            }

            yield return target;
            c.GiveSourceTo(target.ChosenPlayerId);
        }

        // ------------------------------------------------------ Draft Blessings

        /// <summary>
        /// Turns a card face down. Nobody can buy it, which means it is certain to
        /// be one of the three left over when the picks run out - so this is a card
        /// removed from the draft, not a card saved for later.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> BlockedByGames(EffectContext c)
        {
            var choice = c.ChooseCard("Blocked by Games - turn which card over?", Unmarked(c));
            if (choice == null)
            {
                yield break;
            }

            yield return choice;
            c.MarkInDraft(DraftMarker.Blocked, choice.ChosenCardId);
        }

        /// <summary>
        /// Reserves a card that only its owner may take. It still costs them one of
        /// their normal picks, so the Parking Spot buys certainty rather than cards.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> CultLeadersParkingSpot(EffectContext c)
        {
            var choice = c.ChooseCard("Cult Leader's Parking Spot - reserve which card?", Unmarked(c));
            if (choice == null)
            {
                yield break;
            }

            yield return choice;
            c.MarkInDraft(DraftMarker.Reserved, choice.ChosenCardId);
        }

        /// <summary>
        /// Baits a card. If the table leaves it behind, every opponent pays for it.
        ///
        /// A card Blocked by Games cannot be drafted by anyone, which would turn
        /// this from a bluff into guaranteed damage every draft, so the blocked card
        /// is not offered here.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> HumanTrap(EffectContext c)
        {
            var blocked = c.Game.MarkedInDraft(DraftMarker.Blocked);
            var candidates = c.DraftZone.Where(card => card != blocked);

            var choice = c.ChooseCard("Human Trap - bait which card?", candidates);
            if (choice == null)
            {
                yield break;
            }

            yield return choice;
            c.MarkInDraft(DraftMarker.Trapped, choice.ChosenCardId);
        }

        /// <summary>Draft-zone cards no other draft Blessing has claimed yet.</summary>
        private static IEnumerable<CardInstance> Unmarked(EffectContext c)
        {
            var taken = new[]
            {
                c.Game.MarkedInDraft(DraftMarker.Blocked),
                c.Game.MarkedInDraft(DraftMarker.Reserved)
            };

            return c.DraftZone.Where(card => !taken.Contains(card));
        }

        // ------------------------------------------------------------- Defence

        /// <summary>
        /// First Line of Defense trades a wound for twice as many followers, and
        /// the offer is made on every hit separately.
        ///
        /// The damage has already landed by the time this runs - triggers queue
        /// rather than interrupt - so taking the offer puts the health back rather
        /// than stopping the hit. Anything that fired on the damage still fired.
        /// </summary>
        internal static EffectRoutine FirstLineOfDefense(int healthLost)
        {
            return c => FirstLineOfDefenseSteps(c, healthLost);
        }

        private static IEnumerator<ChoiceRequest> FirstLineOfDefenseSteps(EffectContext c, int healthLost)
        {
            var followers = healthLost * 2;

            // The board may have moved on between the hit and this question.
            if (healthLost <= 0 || c.Controller.Followers < followers)
            {
                yield break;
            }

            var ask = c.AskYesNo(
                $"First Line of Defense - take back {healthLost} damage and lose {followers} followers instead?");
            yield return ask;

            if (!ask.ChoseYes)
            {
                yield break;
            }

            c.RestoreHealth(healthLost);
            c.LoseFollowers(followers);
        }
    }
}
