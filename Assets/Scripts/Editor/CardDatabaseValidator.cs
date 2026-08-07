using Indoctrination.Core;
using UnityEditor;
using UnityEngine;

namespace Indoctrination.EditorTools
{
    /// <summary>
    /// Click "Indoctrination > Validate Card Database" any time you edit
    /// Assets/Resources/Data/Cards.json to make sure every card still loads
    /// and parses without errors.
    /// </summary>
    public static class CardDatabaseValidator
    {
        [MenuItem("Indoctrination/Validate Card Database")]
        public static void Validate()
        {
            var db = new CardDatabase("Data/Cards");
            var errorCount = 0;

            foreach (var card in db.All)
            {
                try
                {
                    _ = card.Type;
                    _ = card.Color;
                    _ = card.Cost;
                }
                catch (System.Exception e)
                {
                    errorCount++;
                    Debug.LogError($"[CardDatabaseValidator] '{card.Title}' ({card.Id}) failed to parse: {e.Message}");
                }
            }

            var totalPhysicalCards = 0;
            foreach (var card in db.All)
            {
                totalPhysicalCards += card.Count;
            }

            if (errorCount == 0)
            {
                Debug.Log($"[CardDatabaseValidator] OK - {db.All.Count} card definitions ({totalPhysicalCards} physical cards) loaded and parsed successfully.");
            }
            else
            {
                Debug.LogError($"[CardDatabaseValidator] FAILED - {errorCount} card(s) had errors. See above.");
            }
        }
    }
}
