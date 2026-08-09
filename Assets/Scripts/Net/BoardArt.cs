using System.Collections.Generic;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Shared look-and-feel for the board: the resource colours, and the two
    /// sprites the interface needs that no art pass has produced yet.
    ///
    /// Both sprites are generated once at runtime rather than imported, keeping
    /// the whole interface free of asset dependencies - the same reason the
    /// layout is built in code. Swap these for real art later without touching
    /// anything that uses them.
    /// </summary>
    public static class BoardArt
    {
        private static Sprite _disc;
        private static Sprite _glow;

        public static Color ColorOf(ResourceColor color) => color switch
        {
            ResourceColor.Red => new Color(0.88f, 0.35f, 0.35f),
            ResourceColor.Green => new Color(0.31f, 0.69f, 0.35f),
            ResourceColor.Blue => new Color(0.33f, 0.53f, 0.88f),
            ResourceColor.Yellow => new Color(0.90f, 0.74f, 0.28f),
            _ => Color.white
        };

        /// <summary>
        /// What an activation looks like, by what it does: red for damage, green
        /// for followers, blue for drawing, yellow for healing. The colour is the
        /// explanation - a player should know what a card did from the flash
        /// without reading it.
        /// </summary>
        public static Color ColorOfCategory(ActivationCategory category) => category switch
        {
            ActivationCategory.Damage => new Color(0.92f, 0.28f, 0.28f),
            ActivationCategory.Followers => new Color(0.34f, 0.80f, 0.38f),
            ActivationCategory.Draw => new Color(0.36f, 0.60f, 0.95f),
            ActivationCategory.Health => new Color(0.95f, 0.82f, 0.30f),
            ActivationCategory.Block => new Color(0.65f, 0.72f, 0.85f),
            _ => new Color(0.80f, 0.80f, 0.85f)
        };

        public static IReadOnlyList<ResourceColor> Colors { get; } = new[]
        {
            ResourceColor.Red, ResourceColor.Green, ResourceColor.Blue, ResourceColor.Yellow
        };

        private static Sprite _backdrop;

        /// <summary>
        /// A soft vertical gradient, laid behind the whole board. Flat colour
        /// makes every panel read as a separate box; a gradient gives them all
        /// one surface to sit on.
        /// </summary>
        public static Sprite Backdrop => _backdrop ??= BuildGradientSprite(4, 128);

        private static Sprite BuildGradientSprite(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[width * height];

            for (var y = 0; y < height; y++)
            {
                // Darker at the edges than the middle, which draws the eye to the
                // table rather than to the corners of the window.
                var distance = Mathf.Abs((y / (float)(height - 1)) - 0.5f) * 2f;
                var shade = Mathf.Lerp(1f, 0.55f, distance * distance);
                var colour = new Color(0.09f * shade, 0.15f * shade, 0.11f * shade, 1f);

                for (var x = 0; x < width; x++)
                {
                    pixels[(y * width) + x] = colour;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f));
        }

        private static Sprite _solid;

        /// <summary>
        /// A plain white rectangle.
        ///
        /// Needed because Unity's Image ignores <c>type = Filled</c> outright when
        /// it has no sprite: OnPopulateMesh short-circuits to a simple quad, so
        /// fillAmount has no effect at all and a "progress bar" silently renders
        /// full at every value. Any bar that fills must have a sprite.
        /// </summary>
        public static Sprite Solid => _solid ??= BuildSolidSprite();

        private static Sprite BuildSolidSprite()
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[16];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(255, 255, 255, 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            return Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
        }

        /// <summary>A flat filled circle, for the resource pips.</summary>
        public static Sprite Disc => _disc ??= BuildRadialSprite(64, hard: true);

        /// <summary>A soft circle that fades out, for the glow behind an activating card.</summary>
        public static Sprite Glow => _glow ??= BuildRadialSprite(64, hard: false);

        /// <summary>
        /// Draws a circle into a texture. A hard edge gives a solid disc; a soft
        /// one fades from the centre out, which reads as a glow when tinted and
        /// laid behind something.
        /// </summary>
        private static Sprite BuildRadialSprite(int size, bool hard)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var centre = (size - 1) / 2f;
            var maxDistance = centre;
            var pixels = new Color32[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector2.Distance(new Vector2(x, y), new Vector2(centre, centre)) / maxDistance;

                    float alpha;
                    if (hard)
                    {
                        // One pixel of feathering at the rim, so the disc is not jagged.
                        alpha = Mathf.Clamp01((1f - distance) * maxDistance);
                    }
                    else
                    {
                        alpha = Mathf.Clamp01(1f - distance);
                        alpha *= alpha;
                    }

                    pixels[(y * size) + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        /// <summary>
        /// A coloured disc with a number in it - one player's stock of a single
        /// resource. Returns the label so the count can be updated in place.
        /// </summary>
        public static Text ResourcePip(Transform parent, ResourceColor color, float diameter)
        {
            var root = UIFactory.Group($"{color} Pip", parent);
            UIFactory.SetSize(root, diameter, diameter);

            var pin = root.gameObject.AddComponent<LayoutElement>();
            pin.minWidth = diameter;
            pin.preferredWidth = diameter;
            pin.minHeight = diameter;
            pin.preferredHeight = diameter;

            var disc = root.gameObject.AddComponent<Image>();
            disc.sprite = Disc;
            disc.color = ColorOf(color);

            var label = UIFactory.Label(
                "Count", root, "0", Mathf.RoundToInt(diameter * 0.55f), TextAnchor.MiddleCenter, Color.white);
            label.fontStyle = FontStyle.Bold;
            UIFactory.Stretch(label.rectTransform);

            // Dark outline so a white number stays legible on the lighter discs.
            var outline = label.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1f, -1f);

            return label;
        }
    }
}
