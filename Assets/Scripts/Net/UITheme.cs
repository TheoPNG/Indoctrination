using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// The board's visual language. Keeping the palette and typography here makes
    /// the runtime-built UI feel like one ritual table instead of a collection of
    /// unrelated grey widgets.
    /// </summary>
    public static class UITheme
    {
        public static readonly Color RitualBlack = new(0.025f, 0.014f, 0.040f, 1f);
        public static readonly Color DeepPlum = new(0.090f, 0.040f, 0.120f, 1f);
        public static readonly Color Surface = new(0.075f, 0.043f, 0.095f, 0.94f);
        public static readonly Color SurfaceRaised = new(0.125f, 0.070f, 0.145f, 0.97f);
        public static readonly Color SurfaceSoft = new(0.180f, 0.115f, 0.195f, 0.72f);

        public static readonly Color Parchment = new(0.925f, 0.855f, 0.710f, 1f);
        public static readonly Color ParchmentMuted = new(0.690f, 0.610f, 0.650f, 1f);
        public static readonly Color RitualGold = new(0.745f, 0.570f, 0.250f, 1f);
        public static readonly Color RitualGoldSoft = new(0.745f, 0.570f, 0.250f, 0.46f);
        public static readonly Color Blood = new(0.470f, 0.105f, 0.160f, 1f);
        public static readonly Color Moss = new(0.225f, 0.390f, 0.270f, 1f);
        public static readonly Color AshBlue = new(0.290f, 0.390f, 0.510f, 1f);

        public static readonly Color Button = new(0.205f, 0.115f, 0.235f, 1f);
        public static readonly Color ButtonQuiet = new(0.135f, 0.085f, 0.165f, 1f);
        public static readonly Color Border = new(0.660f, 0.490f, 0.220f, 0.58f);

        private static Font _bodyFont;
        private static Font _titleFont;

        public static Font BodyFont => _bodyFont ??= ResolveFont(
            "Baskerville", "Palatino", "Palatino Linotype", "Georgia", "Times New Roman");

        public static Font TitleFont => _titleFont ??= ResolveFont(
            "Copperplate", "Baskerville", "Palatino", "Palatino Linotype", "Georgia");

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

        public static void Frame(Graphic graphic, float weight = 1f, Color? color = null)
        {
            if (graphic == null)
            {
                return;
            }

            var outline = graphic.gameObject.GetComponent<Outline>()
                          ?? graphic.gameObject.AddComponent<Outline>();
            outline.effectColor = color ?? Border;
            outline.effectDistance = new Vector2(weight, -weight);
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
            colors.highlightedColor = new Color(1.18f, 1.12f, 1.20f, 1f);
            colors.pressedColor = new Color(0.72f, 0.68f, 0.74f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(0.42f, 0.38f, 0.44f, 0.62f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }
    }
}
