namespace Indoctrination.Core
{
    /// <summary>
    /// The phases of play. A game opens in Draft, then cycles
    /// Rolling -> Activation -> Resource -> Buy for three turns before
    /// returning to Draft.
    /// </summary>
    public enum TurnPhase
    {
        /// <summary>Snake draft to fill hands from the draft zone.</summary>
        Draft,

        /// <summary>Everyone rolls their primary die; highest unique roll takes a resource.</summary>
        Rolling,

        /// <summary>Units activate on the numbers rolled this turn.</summary>
        Activation,

        /// <summary>Everyone collects free resources from the bank.</summary>
        Resource,

        /// <summary>Everyone spends resources to buy or recycle cards.</summary>
        Buy,

        GameOver
    }
}
