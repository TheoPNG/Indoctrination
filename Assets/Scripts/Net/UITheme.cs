using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// The board's visual language: cold, dark, and modern.
    ///
    /// Deliberately not a fantasy table - no parchment, no gold leaf, no serif
    /// scrollwork. The mood comes from near-black surfaces, a lot of empty
    /// space, hairline borders, and one cold accent used sparingly enough that
    /// it still means something when it appears. Dread from restraint rather
    /// than from decoration.
    /// </summary>
    public static class UITheme
    {
        // Backgrounds. Near-black with a faint blue-green cast - cold rather
        // than the warm plum this replaced.
        public static readonly Color Void = new(0.031f, 0.035f, 0.043f, 1f);
        public static readonly Color Fog = new(0.063f, 0.075f, 0.090f, 1f);

        // Panels, in ascending order of how much they lift off the background.
        public static readonly Color Surface = new(0.075f, 0.086f, 0.102f, 0.96f);
        public static readonly Color SurfaceRaised = new(0.106f, 0.122f, 0.145f, 0.98f);
        public static readonly Color SurfaceSoft = new(0.145f, 0.165f, 0.196f, 0.80f);

        // Text. A cool off-white, never cream.
        public static readonly Color Bone = new(0.902f, 0.918f, 0.941f, 1f);
        public static readonly Color BoneDim = new(0.541f, 0.576f, 0.635f, 1f);

        /// <summary>
        /// The one accent. A cold sickly cyan - the only thing on screen that
        /// looks lit from within, so it is reserved for what the board actually
        /// wants you to look at.
        /// </summary>
        public static readonly Color Signal = new(0.431f, 0.906f, 0.843f, 1f);
        public static readonly Color SignalSoft = new(0.431f, 0.906f, 0.843f, 0.30f);

        /// <summary>Damage, resigning, anything that costs you something.</summary>
        public static readonly Color Blood = new(0.780f, 0.220f, 0.278f, 1f);

        /// <summary>Confirming, readying up, anything that moves the game on.</summary>
        public static readonly Color Affirm = new(0.180f, 0.478f, 0.400f, 1f);

        public static readonly Color Button = new(0.129f, 0.149f, 0.180f, 1f);
        public static readonly Color ButtonQuiet = new(0.094f, 0.106f, 0.129f, 1f);

        /// <summary>A hairline, not a frame. Modern panels are separated, not bordered.</summary>
        public static readonly Color Border = new(0.290f, 0.325f, 0.380f, 0.55f);

        private static Font _bodyFont;
        private static Font _titleFont;

        /// <summary>
        /// A clean grotesque for everything. The horror here is in the palette
        /// and the spacing; the type just has to get out of the way and be
        /// readable at small sizes on a dark background.
        /// </summary>
        public static Font BodyFont => _bodyFont ??= ResolveFont(
            "Helvetica Neue", "Inter", "SF Pro Text", "Avenir Next", "Arial");

        /// <summary>
        /// Titles use a geometric face, set uppercase by the widgets that use
        /// it. Wider and colder than the body text without being a different
        /// voice entirely.
        /// </summary>
        public static Font TitleFont => _titleFont ??= ResolveFont(
            "Avenir Next", "Futura", "Helvetica Neue", "Inter", "Arial");

        private static Font ResolveFont(params string[] preferredNames)
        {
            var installed = Font.GetOSInstalledFontNames();
            foreach (var preferred in preferredNames)
            {
                foreach (var available in installed)
                {
                    if (string.Equals(preferred, available, System.StringComparison.OrdinalIgnoreCase))
                    {
                        return Font.CreateDynamicFontFromOSFont(available, 18);
                    }
                }
            }

            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        /// <summary>
        /// Edges a panel. Kept to a hairline by default - a heavy border on
        /// every box is what made the old board read as a fantasy rulebook
        /// rather than an interface.
        /// </summary>
        public static void Frame(Graphic graphic, float weight = 1f, Color? color = null)
        {
            if (graphic == null)
            {
                return;
            }

            var outline = graphic.gameObject.GetComponent<Outline>();
            if (outline == null)
            {
                outline = graphic.gameObject.AddComponent<Outline>();
            }

            outline.effectColor = color ?? Border;

            // Always one pixel. The weight argument survives because callers
            // pass it to mean "how important is this edge", which now reads as
            // colour and alpha instead of thickness.
            outline.effectDistance = new Vector2(1f, -1f);
            outline.useGraphicAlpha = true;
        }

        public static void StyleButton(Button button)
        {
            if (button == null)
            {
                return;
            }

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.28f, 1f);
            colors.pressedColor = new Color(0.68f, 0.70f, 0.74f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.38f, 0.40f, 0.45f, 0.50f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }
    }
}
