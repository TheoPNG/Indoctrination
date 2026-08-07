using System;
using System.Collections.Generic;
using System.Linq;

namespace Indoctrination.Core
{
    /// <summary>
    /// A parsed card cost. Most cards cost a combination of resource colors,
    /// e.g. "YYRG" means 2 Yellow + 1 Red + 1 Green. A small number of cards
    /// use a special cost (raw value "*") whose requirements are described
    /// entirely in the card's effect text (e.g. sacrifice a Unit).
    /// </summary>
    public class CardCost
    {
        public bool IsSpecial { get; }
        public IReadOnlyDictionary<ResourceColor, int> Amounts { get; }
        public int Total => Amounts.Values.Sum();

        private CardCost(bool isSpecial, IReadOnlyDictionary<ResourceColor, int> amounts)
        {
            IsSpecial = isSpecial;
            Amounts = amounts;
        }

        public static CardCost Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("Card cost cannot be empty.", nameof(raw));
            }

            raw = raw.Trim();

            if (raw == "*")
            {
                return new CardCost(true, new Dictionary<ResourceColor, int>());
            }

            if (raw.Equals("free", StringComparison.OrdinalIgnoreCase))
            {
                return new CardCost(false, new Dictionary<ResourceColor, int>());
            }

            var amounts = new Dictionary<ResourceColor, int>();
            foreach (var c in raw)
            {
                var color = CharToColor(c);
                amounts[color] = amounts.GetValueOrDefault(color) + 1;
            }

            return new CardCost(false, amounts);
        }

        private static ResourceColor CharToColor(char c)
        {
            switch (char.ToUpperInvariant(c))
            {
                case 'R': return ResourceColor.Red;
                case 'G': return ResourceColor.Green;
                case 'B': return ResourceColor.Blue;
                case 'Y': return ResourceColor.Yellow;
                default:
                    throw new ArgumentException($"Unrecognized cost letter '{c}'.");
            }
        }

        public override string ToString()
        {
            return IsSpecial
                ? "*"
                : string.Concat(Amounts.SelectMany(kv => Enumerable.Repeat(kv.Key.ToString()[0], kv.Value)));
        }
    }
}
