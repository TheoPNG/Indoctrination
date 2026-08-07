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
        public const int StartingFollowers = 1;

        /// <summary>Reaching this many followers wins the game.</summary>
        public const int FollowersToWin = 20;

        /// <summary>Cards left in the draft zone that get discarded instead of drafted.</summary>
        public const int UndraftedCardsDiscarded = 3;

        /// <summary>Free resources every player collects during the Resource phase.</summary>
        public const int ResourcesPerTurn = 2;

        /// <summary>Turns played between drafts.</summary>
        public const int TurnsPerRound = 3;

        public const int DieSides = 6;

        /// <summary>
        /// How long a phase waits for every player to say they are done before it
        /// moves on regardless. Stops one player who has stepped away from
        /// stalling the table indefinitely.
        /// </summary>
        public const float PhaseTimeoutSeconds = 90f;

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
