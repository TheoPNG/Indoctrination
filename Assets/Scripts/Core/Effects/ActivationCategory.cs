namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// The fixed order activating Units resolve in in the Activation phase,
    /// regardless of whose seat or which die triggered them. Declared in
    /// resolution order, so casting to int gives the priority directly.
    ///
    /// Block matters most here: it is temporary health that only lasts the turn
    /// it was granted (<see cref="PlayerState.AddBlock"/> is cleared at end of
    /// turn), so it has to be on the board before any Damage this same
    /// activation round can be reduced by it. Draw and Followers do not depend
    /// on anything else resolving first, so they are free to go early; Health
    /// (healing) goes last so it lands after the round's Damage rather than
    /// being wiped out by it.
    /// </summary>
    public enum ActivationCategory
    {
        Draw,
        Block,
        Followers,
        Damage,
        Health,

        /// <summary>
        /// Everything that is not one of the five named categories - mostly
        /// resource gains and counter bookkeeping. Last because nothing else on
        /// the board depends on when it happens.
        /// </summary>
        Other
    }
}
