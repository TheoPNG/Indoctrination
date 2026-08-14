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

        /// <summary>
        /// The four resources. Saturated enough to tell apart instantly against
        /// a near-black board, dark enough not to glare out of it.
        /// </summary>
        public static Color ColorOf(ResourceColor color) => color switch
        {
            ResourceColor.Red => new Color(0.839f, 0.278f, 0.353f),
            ResourceColor.Green => new Color(0.247f, 0.722f, 0.502f),
            ResourceColor.Blue => new Color(0.290f, 0.561f, 0.902f),
            ResourceColor.Yellow => new Color(0.882f, 0.686f, 0.251f),
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
            ActivationCategory.Damage => new Color(0.925f, 0.322f, 0.388f),
            ActivationCategory.Followers => new Color(0.290f, 0.831f, 0.588f),
            ActivationCategory.Draw => new Color(0.361f, 0.639f, 0.965f),
            ActivationCategory.Health => new Color(0.961f, 0.769f, 0.318f),
            ActivationCategory.Block => new Color(0.573f, 0.643f, 0.741f),
            _ => new Color(0.741f, 0.776f, 0.831f)
        };

        /// <summary>
        /// A stable colour per counter kind, derived from its name so every
        /// counter in the game has one without a table to maintain.
        ///
        /// The colour is how a kind is told apart, because the chip itself is
        /// only big enough for a number. It used to carry the kind's initial as
        /// well - a single meal counter rendered as "M1", which reads as eleven
        /// rather than as one meal.
        /// </summary>
        public static Color ColorOfCounter(string name)
        {
            var hue = Mathf.Abs((name ?? "").GetHashCode() % 360) / 360f;
            return Color.HSVToRGB(hue, 0.55f, 0.85f);
        }

        public static IReadOnlyList<ResourceColor> Colors { get; } = new[]
        {
            ResourceColor.Red, ResourceColor.Green, ResourceColor.Blue, ResourceColor.Yellow
        };

        private static Sprite _backdrop;

        /// <summary>
        /// The surface the board sits on: near-black, with cold light bleeding
        /// up from the bottom and the corners falling away into nothing.
        ///
        /// Deliberately featureless. The old backdrop carried an occult seal,
        /// which made the screen read as a fantasy rulebook; an empty dark room
        /// is both more modern and more unsettling, and it stops competing with
        /// the cards for attention.
        /// </summary>
        public static Sprite Backdrop => _backdrop ??= BuildGradientSprite(512, 256);

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
                for (var x = 0; x < width; x++)
                {
                    var uv = new Vector2(x / (float)(width - 1), y / (float)(height - 1));
                    var centred = (uv - new Vector2(0.5f, 0.5f)) * 2f;
                    centred.x *= width / (float)height;

                    // A soft pool of light low and centre, as though the table
                    // is lit by something just off the bottom of the screen.
                    var toGlow = new Vector2(centred.x * 0.75f, (centred.y + 0.85f) * 1.15f);
                    var glow = Mathf.Pow(Mathf.Clamp01(1f - (toGlow.magnitude / 2.1f)), 2.2f);

                    // Corners fall away hard, so the play area is the only part
                    // of the screen that is genuinely lit.
                    var vignette = Mathf.Pow(Mathf.Clamp01(1f - (centred.magnitude / 1.95f)), 1.5f);

                    // Fine grain keeps a flat dark field from banding on a big
                    // display, and reads as film rather than as a gradient.
                    var grain = (Mathf.PerlinNoise(x * 0.9f, y * 0.9f) - 0.5f) * 0.022f;

                    var lift = Mathf.Clamp01((glow * 0.85f) + (vignette * 0.30f) + grain);
                    var colour = Color.Lerp(UITheme.Void, UITheme.Fog, lift);

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
