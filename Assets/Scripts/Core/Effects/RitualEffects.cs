using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// Rituals resolve the moment they are bought and then stay in the discard
    /// pile, where Worshiper of the Bone God and The Second Coming can find them.
    /// </summary>
    internal static class RitualEffects
    {
        // =================================================================== Blue

        /// <summary>Flattens the board's defences and keeps them flat for a turn.</summary>
        internal static IEnumerator<ChoiceRequest> Siege(EffectContext c)
        {
            foreach (var player in c.LivingPlayers.ToList())
            {
                player.ClearBlock();
            }

            c.SetFlagNextTurnForEveryone(TurnFlags.CannotGainBlock);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> Bribery(EffectContext c)
        {
            c.Heal(4);
            c.SetFlagNextTurn(TurnFlags.CannotDealDamage);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> TheSecondComing(EffectContext c)
        {
            var choice = c.ChooseCard("The Second Coming - return which card to your hand?", c.DiscardPile);
            if (choice == null)
            {
                yield break;
            }

            yield return choice;
            c.Game.ReturnFromDiscard(c.Controller.PlayerId, choice.ChosenCardId);
        }

        /// <summary>
        /// Nudges a die by one. Only the shifted value counts, so this is a way to
        /// switch a player's units off as much as to switch your own on.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> CloseEnough(EffectContext c)
        {
            var whose = c.ChooseAnyPlayer("Close Enough - shift whose die?");
            if (whose == null)
            {
                yield break;
            }

            yield return whose;

            var player = c.PlayerById(whose.ChosenPlayerId);
            var canRaise = player.PrimaryDie < GameSettings.DieSides;
            var canLower = player.PrimaryDie > 1;

            if (!canRaise && !canLower)
            {
                yield break;
            }

            var up = true;
            if (canRaise && canLower)
            {
                // Named directions, not yes/no. "No" is not declining here, it is
                // the opposite move, and a menu offering it as a refusal reads as
                // though nothing will happen.
                var raise = $"Up to {player.PrimaryDie + 1}";
                var lower = $"Down to {player.PrimaryDie - 1}";

                var direction = c.ChooseOption("Close Enough", new[] { raise, lower });
                yield return direction;
                up = direction.ChosenOption == raise;
            }
            else
            {
                up = canRaise;
            }

            c.Game.SetPrimaryDie(player, player.PrimaryDie + (up ? 1 : -1));
        }

        internal static IEnumerator<ChoiceRequest> VirginSacrifice(EffectContext c)
        {
            c.SetFlag(TurnFlags.ImmuneToDamage);
            yield break;
        }

        // ================================================================== Green

        internal static IEnumerator<ChoiceRequest> RadicalTactics(EffectContext c)
        {
            c.GainFollowers(3);
            c.LoseHealth(2);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> CompoundWalls(EffectContext c)
        {
            c.GainBlock(4);
            yield break;
        }

        /// <summary>
        /// All or nothing: five followers, and if that does not carry you over the
        /// line you are out of the game.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> Ascension(EffectContext c)
        {
            c.GainFollowers(5);

            if (!c.Controller.HasWon)
            {
                c.Kill(c.Controller);
            }

            yield break;
        }

        internal static IEnumerator<ChoiceRequest> GainThreeFollowers(EffectContext c)
        {
            c.GainFollowers(3);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> GainTwoFollowers(EffectContext c)
        {
            c.GainFollowers(2);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> SmearCampaign(EffectContext c)
        {
            var target = c.ChooseOpponent("Smear Campaign - take 3 followers from:");
            if (target == null)
            {
                yield break;
            }

            yield return target;
            c.PlayerLosesFollowers(target.ChosenPlayerId, 3);
        }

        // ==================================================================== Red

        internal static IEnumerator<ChoiceRequest> SupernaturalEvent(EffectContext c)
        {
            return CommonEffects.DamageChosenOpponent(3, "Supernatural Event - deal 3 damage to:")(c);
        }

        internal static IEnumerator<ChoiceRequest> ChemicalWeapons(EffectContext c)
        {
            c.DealDamageToAllOpponents(2);
            yield break;
        }

        /// <summary>
        /// Everybody pays, but everybody picks their own currency. Each player is
        /// asked in turn, so the questions queue up one at a time.
        /// </summary>
        internal static IEnumerator<ChoiceRequest> Equality(EffectContext c)
        {
            foreach (var player in c.LivingPlayers.ToList())
            {
                if (!player.IsAlive)
                {
                    continue;
                }

                var followerCost = player.Followers / 2;
                var healthCost = player.Health / 2;

                // Two plain offers rather than a yes/no whose "no" has to be
                // explained in the question. The card is on screen saying what
                // it does; the buttons only have to say what they cost.
                var followers = $"{followerCost} followers";
                var health = $"{healthCost} health";

                var choice = c.AskPlayerOption("Equality", player.PlayerId,
                    new[] { followers, health });
                yield return choice;

                if (choice.ChosenOption == followers)
                {
                    c.PlayerLosesFollowers(player.PlayerId, followerCost);
                }
                else
                {
                    c.Game.LoseHealth(player, healthCost);
                }
            }
        }

        internal static IEnumerator<ChoiceRequest> SummonGoneWrong(EffectContext c)
        {
            c.DealDamageToEveryone(3);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> BloodyMargarita(EffectContext c)
        {
            c.Heal(2);
            yield break;
        }

        internal static IEnumerator<ChoiceRequest> Assassinate(EffectContext c)
        {
            var target = c.ChoosePlayer(
                "Assassinate - destroy a Unit belonging to:",
                c.Opponents.Where(p => p.Compound.Any(card => card.Type == CardType.Unit)));
            if (target == null)
            {
                yield break;
            }

            yield return target;

            var choice = c.ChooseCard("Assassinate - destroy which Unit?", c.UnitsOf(target.ChosenPlayerId));
            if (choice == null)
            {
                yield break;
            }

            yield return choice;
            c.Game.SacrificeCard(target.ChosenPlayerId, choice.ChosenCardId);
        }

        /// <summary>Pays a point of health to replay anything in the discard pile.</summary>
        internal static IEnumerator<ChoiceRequest> ReviveTheForgotten(EffectContext c)
        {
            c.LoseHealth(1);

            var choice = c.ChooseCard("Revive the Forgotten - activate which card?", c.DiscardPile);
            if (choice == null)
            {
                yield break;
            }

            yield return choice;
            c.ActivateFromDiscard(choice.ChosenCardId);
        }

        // ================================================================= Yellow

        internal static IEnumerator<ChoiceRequest> VaccinesForAll(EffectContext c)
        {
            c.LoseFollowers(5);
            c.Heal(5);
            yield break;
        }
    }
}
