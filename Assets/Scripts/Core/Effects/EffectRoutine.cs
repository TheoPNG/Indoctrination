using System.Collections.Generic;

namespace Indoctrination.Core.Effects
{
    /// <summary>
    /// A card's effect. Written as an iterator so it can pause on a
    /// <see cref="ChoiceRequest"/> and pick up again once the player answers.
    /// </summary>
    public delegate IEnumerator<ChoiceRequest> EffectRoutine(EffectContext context);
}
