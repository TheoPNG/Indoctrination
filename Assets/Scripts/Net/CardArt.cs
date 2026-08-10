using System.Collections.Generic;
using UnityEngine;

namespace Indoctrination.Net
{
    /// <summary>
    /// Resolves printed card faces by their stable definition id. A missing face
    /// is ordinary while the art rollout is incomplete, so callers fall back to
    /// the code-built card rather than logging an error for every red, green, or
    /// yellow card on the board.
    /// </summary>
    public static class CardArt
    {
        private const string ResourceFolder = "CardArt";

        private static readonly Dictionary<string, Sprite> Faces = new();
        private static readonly HashSet<string> Missing = new();

        public static Sprite FaceFor(string definitionId)
        {
            if (string.IsNullOrEmpty(definitionId) || Missing.Contains(definitionId))
            {
                return null;
            }

            if (Faces.TryGetValue(definitionId, out var known))
            {
                return known;
            }

            var texture = Resources.Load<Texture2D>($"{ResourceFolder}/{definitionId}");
            if (texture == null)
            {
                Missing.Add(definitionId);
                return null;
            }

            var face = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            face.name = $"{definitionId} Printed Face";
            Faces[definitionId] = face;
            return face;
        }
    }
}
