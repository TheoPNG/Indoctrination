using System;
using System.Collections.Generic;
using UnityEngine;

namespace Indoctrination.Core
{
    /// <summary>
    /// Loads and provides lookup access to every card definition in the game,
    /// sourced from Assets/Resources/Data/Cards.json.
    /// </summary>
    public class CardDatabase
    {
        [Serializable]
        private class CardListWrapper
        {
            public CardDefinition[] cards;
        }

        private static CardDatabase _instance;
        public static CardDatabase Instance => _instance ??= new CardDatabase("Data/Cards");

        public IReadOnlyList<CardDefinition> All { get; }
        private readonly Dictionary<string, CardDefinition> _byId;

        public CardDatabase(string resourcePath)
        {
            var textAsset = Resources.Load<TextAsset>(resourcePath);
            if (textAsset == null)
            {
                throw new InvalidOperationException($"Could not find card data at Resources/{resourcePath}.json");
            }

            var wrapper = JsonUtility.FromJson<CardListWrapper>(textAsset.text);
            All = wrapper.cards;

            _byId = new Dictionary<string, CardDefinition>(All.Count);
            foreach (var card in All)
            {
                _byId[card.Id] = card;
            }
        }

        public CardDefinition Get(string id)
        {
            if (!_byId.TryGetValue(id, out var card))
            {
                throw new KeyNotFoundException($"No card with id '{id}' exists.");
            }

            return card;
        }

        public bool TryGet(string id, out CardDefinition card) => _byId.TryGetValue(id, out card);
    }
}
