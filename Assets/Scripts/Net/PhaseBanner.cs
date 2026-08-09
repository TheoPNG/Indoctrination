using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Announces each phase as it begins: the name sweeps across the middle of
    /// the board, holds for a moment, and fades.
    ///
    /// The phase is already written in the status line, but a line of text that
    /// quietly changes is easy to play straight past. This makes the turn feel
    /// like it has a rhythm - something starts, rather than something is now
    /// different.
    /// </summary>
    public class PhaseBanner : MonoBehaviour
    {
        private CanvasGroup _group;
        private RectTransform _panel;
        private Text _label;
        private Text _subtitle;
        private Coroutine _playing;

        public static PhaseBanner CreateOn(Transform canvas)
        {
            var go = new GameObject("Phase Banner", typeof(RectTransform));
            go.transform.SetParent(canvas, false);

            var banner = go.AddComponent<PhaseBanner>();
            banner.Build();
            return banner;
        }

        private void Build()
        {
            var root = (RectTransform)transform;
            UIFactory.Stretch(root);

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;

            // Nothing here is clickable - it passes over the board while the
            // player is still free to act underneath it.
            _group.blocksRaycasts = false;
            _group.interactable = false;

            _panel = UIFactory.Panel("Banner", root, new Color(
                UITheme.RitualBlack.r, UITheme.RitualBlack.g, UITheme.RitualBlack.b, 0.82f));
            UITheme.Frame(_panel.GetComponent<Image>(), 0.8f, UITheme.RitualGoldSoft);
            _panel.anchorMin = new Vector2(0f, 0.5f);
            _panel.anchorMax = new Vector2(1f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.sizeDelta = new Vector2(0f, 96f);

            var layout = UIFactory.VerticalLayout(_panel, 0, new RectOffset(0, 0, 8, 8), controlHeight: true);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = true;

            _label = UIFactory.Label("Phase", _panel, "", 44, TextAnchor.MiddleCenter);
            _label.fontStyle = FontStyle.Bold;
            FixedRow(_label.rectTransform, 52);

            _subtitle = UIFactory.Label("Detail", _panel, "", 16, TextAnchor.MiddleCenter,
                UITheme.ParchmentMuted);
            FixedRow(_subtitle.rectTransform, 22);

            gameObject.SetActive(false);
        }

        private static void FixedRow(RectTransform rect, float height)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleWidth = 1;
        }

        /// <summary>Announces a phase. Repeated calls replace whatever was playing.</summary>
        public void Announce(string phase, string detail, Color tint)
        {
            if (!Application.isPlaying)
            {
                return;
            }

            _label.text = phase.ToUpperInvariant();
            _label.color = tint;
            _subtitle.text = detail ?? "";

            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            if (_playing != null)
            {
                StopCoroutine(_playing);
            }

            _playing = StartCoroutine(Sweep());
        }

        private IEnumerator Sweep()
        {
            const float rise = 0.18f;
            const float hold = 0.75f;
            const float fall = 0.35f;

            var width = ((RectTransform)transform).rect.width;

            // Slides in from the left and drifts on out to the right, so the
            // announcement passes over the board rather than sitting on it.
            var travel = width * 0.06f;
            var elapsed = 0f;

            while (elapsed < rise)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / rise);
                _group.alpha = t;
                _panel.anchoredPosition = new Vector2(Mathf.Lerp(-travel, 0f, t), 0f);
                yield return null;
            }

            _group.alpha = 1f;
            _panel.anchoredPosition = Vector2.zero;
            yield return new WaitForSeconds(hold);

            elapsed = 0f;
            while (elapsed < fall)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / fall);
                _group.alpha = 1f - t;
                _panel.anchoredPosition = new Vector2(Mathf.Lerp(0f, travel, t), 0f);
                yield return null;
            }

            _group.alpha = 0f;
            gameObject.SetActive(false);
            _playing = null;
        }
    }
}
