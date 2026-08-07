using System;

namespace Indoctrination.Core
{
    /// <summary>
    /// A single card's static data, as loaded from Assets/Resources/Data/Cards.json.
    /// The raw fields mirror the JSON exactly (JsonUtility needs public fields with
    /// matching names); the properties below expose parsed, game-ready values.
    /// </summary>
    [Serializable]
    public class CardDefinition
    {
        public string id;
        public string title;
        public string type;
        public string costRaw;
        public string color;
        public string effect;
        public int count;

        private CardType? _type;
        private ResourceColor? _color;
        private CardCost _cost;

        public string Id => id;
        public string Title => title;
        public string Effect => effect;

        /// <summary>How many physical copies of this card exist in the deck.</summary>
        public int Count => count;

        public CardType Type => _type ??= Enum.Parse<CardType>(type);

        /// <summary>The card's own color, used for the "recycle" mechanic.</summary>
        public ResourceColor Color => _color ??= Enum.Parse<ResourceColor>(color);

        public CardCost Cost => _cost ??= CardCost.Parse(costRaw);
    }
}
