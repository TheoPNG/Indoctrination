using System;
using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core
{
    /// <summary>
    /// A player's stock of coloured resource tokens.
    /// </summary>
    public class ResourcePool
    {
        private readonly Dictionary<ResourceColor, int> _amounts = new()
        {
            { ResourceColor.Red, 0 },
            { ResourceColor.Green, 0 },
            { ResourceColor.Blue, 0 },
            { ResourceColor.Yellow, 0 }
        };

        public int this[ResourceColor color] => _amounts[color];

        public int Total => _amounts.Values.Sum();

        public void Add(ResourceColor color, int amount = 1)
        {
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Use Remove to subtract resources.");
            }

            _amounts[color] += amount;
        }

        public void Remove(ResourceColor color, int amount = 1)
        {
            if (amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amount), amount, "Use Add to add resources.");
            }

            if (_amounts[color] < amount)
            {
                throw new InvalidOperationException($"Cannot remove {amount} {color}; only {_amounts[color]} available.");
            }

            _amounts[color] -= amount;
        }

        /// <summary>
        /// Whether this pool covers the cost. Special ("*") costs are never
        /// affordable through resources alone - they are paid with the
        /// additional requirements written on the card.
        /// </summary>
        public bool CanAfford(CardCost cost)
        {
            if (cost.IsSpecial)
            {
                return false;
            }

            return cost.Amounts.All(pair => _amounts[pair.Key] >= pair.Value);
        }

        public void Pay(CardCost cost)
        {
            if (!CanAfford(cost))
            {
                throw new InvalidOperationException($"Cannot afford cost {cost}.");
            }

            foreach (var (color, amount) in cost.Amounts)
            {
                _amounts[color] -= amount;
            }
        }

        public override string ToString()
        {
            return string.Join(" ", _amounts.Select(pair => $"{pair.Key}:{pair.Value}"));
        }
    }
}
