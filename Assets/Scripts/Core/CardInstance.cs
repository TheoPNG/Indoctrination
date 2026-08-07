using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core
{
    /// <summary>
    /// One physical copy of a card in a game. Distinct from CardDefinition
    /// because copies are tracked separately once in play - several cards keep
    /// their own counters (Ritualist's Ritual counters, Baal's Scheme counters,
    /// Soul Swapper's swap counters).
    /// </summary>
    public class CardInstance
    {
        public int InstanceId { get; }
        public CardDefinition Definition { get; }
        private readonly Dictionary<string, int> _counters = new();

        public CardInstance(int instanceId, CardDefinition definition)
        {
            InstanceId = instanceId;
            Definition = definition;
        }

        public string Title => Definition.Title;
        public CardType Type => Definition.Type;
        public CardCost Cost => Definition.Cost;
        public ResourceColor Color => Definition.Color;

        public int GetCounter(string name) => _counters.GetValueOrDefault(name);

        public void AddCounter(string name, int amount = 1)
        {
            _counters[name] = GetCounter(name) + amount;
        }

        /// <summary>Whether this Unit activates on the given die value.</summary>
        public bool ActivatesOn(int dieValue)
        {
            return Type == CardType.Unit && Definition.ActivationNumbers.Contains(dieValue);
        }

        public override string ToString() => $"{Title} #{InstanceId}";
    }
}
