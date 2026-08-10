using System;

namespace Indoctrination.Core
{
    /// <summary>
    /// The fixed rules numbers, gathered in one place so they are easy to tune
    /// during playtesting.
    /// </summary>
    public static class GameSettings
    {
        public const int MinPlayers = 2;
        public const int MaxPlayers = 4;

        public const int StartingHealth = 19;

        /// <summary>
        /// The ceiling healing cannot pass. One above the starting health, so
        /// there is a point of headroom to heal into rather than healing being
        /// wasted from the first turn.
        /// </summary>
        public const int MaxHealth = 20;

        public const int StartingFollowers = 1;

        /// <summary>
        /// A leader always keeps one follower. The follower track is a race to
        /// <see cref="FollowersToWin"/>, not a second health bar, so it cannot be
        /// driven to nothing.
        /// </summary>
        public const int MinFollowers = 1;

        /// <summary>Reaching this many followers wins the game.</summary>
        public const int FollowersToWin = 20;

        /// <summary>Cards left in the draft zone that get discarded instead of drafted.</summary>
        public const int UndraftedCardsDiscarded = 3;

        /// <summary>Free resources every player collects during the Resource phase.</summary>
        public const int ResourcesPerTurn = 2;

        /// <summary>
        /// How many cards a leader may hold once the turn closes. Anything over
        /// this is thrown away, so a hand cannot be hoarded indefinitely.
        /// </summary>
        public const int HandLimit = 7;

        /// <summary>Turns played between drafts.</summary>
        public const int TurnsPerRound = 3;

        public const int DieSides = 6;

        /// <summary>Damage every opponent takes when Human Trap's card goes undrafted.</summary>
        public const int HumanTrapDamage = 2;

        /// <summary>Resources of any colour Suspicious Chef charges for a meal counter.</summary>
        public const int MealCounterCost = 3;

        /// <summary>Swap counters Soul Swapper starts with, and drops back to after a swap.</summary>
        public const int SoulSwapperBaseCounters = 3;

        /// <summary>
        /// How long a phase waits for every player to say they are done before it
        /// moves on regardless. Stops one player who has stepped away from
        /// stalling the table indefinitely.
        /// </summary>
        public const float PhaseTimeoutSeconds = 25f;

        /// <summary>
        /// How long a card waits for the player it questioned before answering
        /// itself. Nothing else at the table may happen while a question is open,
        /// so one player walking away would otherwise stop the game for good.
        /// </summary>
        public const float ChoiceTimeoutSeconds = 25f;

        /// <summary>
        /// How long the board sits on the Activation phase once every unit has
        /// resolved. Nothing there is a player's move, so it closes itself - but
        /// not before there has been time to watch what the dice did.
        /// </summary>
        public const float ActivationDwellSeconds = 2.5f;

        /// <summary>
        /// The minimum screen time between two Unit activations. The rules have
        /// already resolved when this expires; this is presentation pacing so a
        /// table can follow the round-robin sequence instead of seeing one jump.
        /// </summary>
        public const float ActivationStepSeconds = 1.65f;

        /// <summary>
        /// A ceiling on how much work one batch of card effects may do. Two cards
        /// that retaliate against each other would otherwise trade blows forever;
        /// this stops the server hanging on a board nobody expected.
        /// </summary>
        public const int MaxEffectStepsPerResolution = 500;

        /// <summary>How many cards fill the draft zone, based on player count.</summary>
        public static int DraftZoneSize(int playerCount)
        {
            if (playerCount < MinPlayers || playerCount > MaxPlayers)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(playerCount), playerCount, $"Indoctrination supports {MinPlayers}-{MaxPlayers} players.");
            }

            // 2 players: 9, 3 players: 12, 4 players: 15
            return playerCount * 3 + 3;
        }
    }
}
