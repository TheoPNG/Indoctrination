using System;

namespace Indoctrination.Net
{
    /// <summary>
    /// What one player is allowed to see of the game. The server builds a separate
    /// GameView per player and sends each one only to its owner, so opponents never
    /// receive the contents of a hand they are not holding.
    ///
    /// These are sent as JSON through a string RPC parameter. The state of a card
    /// game is tiny and only changes when someone acts, so the simplicity is worth
    /// more here than the bytes a hand-packed binary format would save.
    /// </summary>
    [Serializable]
    public class GameView
    {
        /// <summary>Which player this view was built for.</summary>
        public int viewerPlayerId;

        public string phase;
        public int turnInRound;
        public int draftNumber;
        public int deckCount;
        public int discardCount;

        /// <summary>Whose pick it is, or -1 outside the draft.</summary>
        public int currentDrafterId = -1;

        /// <summary>The winner, or -1 while the game is still going.</summary>
        public int winnerPlayerId = -1;

        public CardView[] draftZone = Array.Empty<CardView>();
        public PlayerView[] players = Array.Empty<PlayerView>();

        public PlayerView Viewer
        {
            get
            {
                foreach (var player in players)
                {
                    if (player.playerId == viewerPlayerId)
                    {
                        return player;
                    }
                }

                return null;
            }
        }
    }

    [Serializable]
    public class PlayerView
    {
        public int playerId;
        public string name;
        public int health;
        public int followers;
        public int primaryDie;
        public bool isAlive;

        public int red;
        public int green;
        public int blue;
        public int yellow;

        /// <summary>Always set, so opponents can see how many cards you are holding.</summary>
        public int handCount;

        /// <summary>Only filled in on the holder's own view; empty for everyone else.</summary>
        public CardView[] hand = Array.Empty<CardView>();

        /// <summary>Cards in play, which are public information.</summary>
        public CardView[] compound = Array.Empty<CardView>();
    }

    /// <summary>
    /// A card by reference. Every client already has the full card text loaded from
    /// Resources/Data/Cards.json, so only the id needs to travel.
    /// </summary>
    [Serializable]
    public class CardView
    {
        public int instanceId;
        public string definitionId;
    }

    /// <summary>Who is in the room before the game starts.</summary>
    [Serializable]
    public class LobbyView
    {
        public string[] playerNames = Array.Empty<string>();
        public int minPlayers;
        public int maxPlayers;
    }
}
