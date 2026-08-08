using System.Collections.Generic;

namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// Small effect bodies that more than one card - or a triggered ability -
    /// needs. Each returns an <see cref="EffectRoutine"/> ready to be enqueued.
    /// </summary>
    public static class CommonEffects
    {
        /// <summary>
        /// Lets one effect body run another and pass its questions along:
        /// <c>foreach (var step in Run(Other(context))) yield return step;</c>
        /// </summary>
        public static IEnumerable<ChoiceRequest> Run(IEnumerator<ChoiceRequest> inner)
        {
            while (inner.MoveNext())
            {
                yield return inner.Current;
            }
        }

        /// <summary>Retaliation aimed at whoever the wounded player picks.</summary>
        public static EffectRoutine RetaliateAgainstChosenOpponent(int amount, string prompt)
        {
            return context => RetaliateChosenSteps(context, amount, prompt);
        }

        private static IEnumerator<ChoiceRequest> RetaliateChosenSteps(
            EffectContext context, int amount, string prompt)
        {
            if (amount <= 0)
            {
                yield break;
            }

            var target = context.ChooseOpponent(prompt);
            if (target == null)
            {
                yield break;
            }

            yield return target;
            context.Game.DealDamage(null, context.PlayerById(target.ChosenPlayerId), amount);
        }

        /// <summary>
        /// The house rule for unaimed damage: whoever controls the card picks who
        /// it hits. Nearly every "deal N damage" on a card comes through here.
        /// </summary>
        public static EffectRoutine DamageChosenOpponent(int amount, string prompt = null)
        {
            return context => DamageChosenOpponentSteps(context, amount, prompt);
        }

        private static IEnumerator<ChoiceRequest> DamageChosenOpponentSteps(
            EffectContext context, int amount, string prompt)
        {
            if (amount <= 0 || context.Opponents.Count == 0)
            {
                yield break;
            }

            var target = context.ChooseOpponent(prompt ?? $"Deal {amount} damage to:");
            if (target == null)
            {
                yield break;
            }

            yield return target;
            context.DealDamage(target.ChosenPlayerId, amount);
        }

        /// <summary>
        /// Damage that names its victims itself, so there is nothing to ask.
        /// "All opponents" is not the house rule's unaimed damage.
        /// </summary>
        public static EffectRoutine DamageAllOpponents(int amount)
        {
            return context => DamageAllOpponentsSteps(context, amount);
        }

        private static IEnumerator<ChoiceRequest> DamageAllOpponentsSteps(EffectContext context, int amount)
        {
            context.DealDamageToAllOpponents(amount);
            yield break;
        }

        /// <summary>Damage aimed at somebody specific, with nothing to ask.</summary>
        public static EffectRoutine DamagePlayer(int targetPlayerId, int amount)
        {
            return context => DamagePlayerSteps(context, targetPlayerId, amount);
        }

        private static IEnumerator<ChoiceRequest> DamagePlayerSteps(
            EffectContext context, int targetPlayerId, int amount)
        {
            context.DealDamage(targetPlayerId, amount);
            yield break;
        }

        /// <summary>
        /// Damage that answers back rather than attacks - Compound Landmines and
        /// Masochist. It has no attacker, so it cannot set off another card's
        /// retaliation and start a loop.
        /// </summary>
        public static EffectRoutine Retaliate(int targetPlayerId, int amount)
        {
            return context => RetaliateSteps(context, targetPlayerId, amount);
        }

        private static IEnumerator<ChoiceRequest> RetaliateSteps(
            EffectContext context, int targetPlayerId, int amount)
        {
            context.Game.DealDamage(null, context.PlayerById(targetPlayerId), amount);
            yield break;
        }

        public static EffectRoutine DrawCards(int count)
        {
            return context => DrawCardsSteps(context, count);
        }

        private static IEnumerator<ChoiceRequest> DrawCardsSteps(EffectContext context, int count)
        {
            context.DrawCards(count);
            yield break;
        }
    }
}
