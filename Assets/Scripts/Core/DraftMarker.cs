namespace Indoctrination.Core
{
    /// <summary>
    /// The three Blessings that put a mark on a card in the draft zone before
    /// anybody picks. All three are visible to the whole table - none of them
    /// works by hiding information, so the marks travel in the public game view.
    /// </summary>
    public enum DraftMarker
    {
        /// <summary>
        /// Cult Leader's Parking Spot. Only the player who reserved it may draft
        /// it, and doing so uses up one of their normal picks.
        /// </summary>
        Reserved,

        /// <summary>
        /// Blocked by Games. Nobody may draft it, so it is guaranteed to be one
        /// of the leftovers discarded when the draft ends.
        /// </summary>
        Blocked,

        /// <summary>
        /// Human Trap. Drafting it is allowed; leaving it in the zone when the
        /// draft ends costs every one of the trapper's opponents 2 damage.
        /// </summary>
        Trapped
    }
}
