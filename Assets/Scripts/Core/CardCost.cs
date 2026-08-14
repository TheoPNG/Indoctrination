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

        /// <summary>
        /// Followers this card also costs, written as "+7F" on the end of the
        /// resource letters. Followers are a currency the player is otherwise
        /// trying to accumulate to win, so a card priced in them is spending
        /// progress rather than materials - kept separate from
        /// <see cref="Amounts"/> for that reason, and never discounted by the
        /// stones, which reduce resources.
        /// </summary>
        public int Followers { get; }

        /// <summary>Resource total only. Followers are not resources and do not count here.</summary>
        public int Total => Amounts.Values.Sum();

        private CardCost(bool isSpecial, IReadOnlyDictionary<ResourceColor, int> amounts, int followers = 0)
        {
            IsSpecial = isSpecial;
            Amounts = amounts;
            Followers = followers;
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

            // A follower price is appended as "+<n>F", so "G+7F" is one Green
            // and seven followers. Resource letters keep their existing meaning,
            // which leaves every card already written still parsing the same way.
            var followers = 0;
            var plus = raw.IndexOf('+');

            if (plus >= 0)
            {
                var tail = raw[(plus + 1)..].Trim();

                if (tail.Length < 2
                    || char.ToUpperInvariant(tail[^1]) != 'F'
                    || !int.TryParse(tail[..^1], out followers)
                    || followers <= 0)
                {
                    throw new ArgumentException(
                        $"Unrecognized follower cost '{raw}'. Expected something like \"G+7F\".");
                }

                raw = raw[..plus].Trim();
            }

            var amounts = new Dictionary<ResourceColor, int>();
            foreach (var c in raw)
            {
                var color = CharToColor(c);
                amounts[color] = amounts.GetValueOrDefault(color) + 1;
            }

            return new CardCost(false, amounts, followers);
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

        /// <summary>
        /// This cost with some of one colour knocked off, never below zero. The
        /// stone Blessings stack, so each returns a fresh cost for the next to
        /// work on rather than editing the card's printed cost.
        /// </summary>
        public CardCost Reduced(ResourceColor color, int amount)
        {
            if (IsSpecial || amount <= 0 || Amounts.GetValueOrDefault(color) == 0)
            {
                return this;
            }

            var reduced = Amounts.ToDictionary(kv => kv.Key, kv => kv.Value);
            reduced[color] = Math.Max(0, reduced[color] - amount);

            if (reduced[color] == 0)
            {
                reduced.Remove(color);
            }

            // Followers ride through untouched: the stones discount resources,
            // and a follower price is a different kind of payment entirely.
            return new CardCost(false, reduced, Followers);
        }

        public override string ToString()
        {
            if (IsSpecial)
            {
                return "*";
            }

            var resources = string.Concat(
                Amounts.SelectMany(kv => Enumerable.Repeat(kv.Key.ToString()[0], kv.Value)));

            if (Followers <= 0)
            {
                return resources;
            }

            // Round-trips through Parse, so a priced view of a card can be read
            // back as a cost without a second format to keep in step.
            return resources.Length == 0 ? $"{Followers}F" : $"{resources}+{Followers}F";
        }
    }
}
