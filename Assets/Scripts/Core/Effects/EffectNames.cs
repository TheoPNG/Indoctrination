namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// Names of the counters cards keep on themselves or on a player. Held in one
    /// place because two different cards reading the same counter have to agree on
    /// the spelling, and nothing complains if they do not.
    /// </summary>
    public static class Counters
    {
        // On a card.
        public const string Ritual = "ritual";      // Ritualist
        public const string Scheme = "scheme";      // Baal, The Manipulator
        public const string Swap = "swap";          // Soul Swapper
        public const string Blood = "blood";        // Blood Collector
        public const string Violence = "violence";  // Asmodeus
        public const string Static = "static";      // Ominous Eye
        public const string Meal = "meal";          // Suspicious Chef
        public const string Trash = "trash";        // Being of Heartlessness

        // On a player.
        public const string Flame = "flame";        // Arsonist, Fire Breather

        /// <summary>
        /// Extra activations owed to the next Ritual this player uses.
        /// Chief Sacrificer stacks these up.
        /// </summary>
        public const string RitualEcho = "ritual-echo";
    }

    /// <summary>
    /// One-turn switches on a player. Cleared by <see cref="PlayerState.EndTurn"/>.
    /// </summary>
    public static class TurnFlags
    {
        /// <summary>Virgin Sacrifice: all damage aimed at this player is prevented.</summary>
        public const string ImmuneToDamage = "immune";

        /// <summary>Bribery: this player's cards deal no damage.</summary>
        public const string CannotDealDamage = "pacified";

        /// <summary>Siege: this player cannot gain Block.</summary>
        public const string CannotGainBlock = "no-block";

        /// <summary>Masochist: every point of health this player loses deals 1 damage back.</summary>
        public const string VengefulHealthLoss = "vengeful";

        /// <summary>Bloody Mooner: this player's own cards hurt them one less.</summary>
        public const string SelfHarmSoftened = "softened";

        /// <summary>Compound Landmines: Block pinned at 1, and attackers take 2.</summary>
        public const string LandminesArmed = "landmines";

        /// <summary>Titanstopper: this player's Block survives the end of the round.</summary>
        public const string BlockPersists = "block-persists";
    }
}
