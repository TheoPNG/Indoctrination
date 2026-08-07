namespace Indoctrination.Core
{
    /// <summary>
    /// The three card types from the rules:
    /// Blessings (static, always active), Rituals (single use),
    /// and Units (activate on their die roll numbers every turn).
    /// </summary>
    public enum CardType
    {
        Unit,
        Blessing,
        Ritual
    }
}
