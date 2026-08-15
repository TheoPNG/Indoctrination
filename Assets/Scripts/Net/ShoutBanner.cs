using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Somebody's message, in a small box above the chat corner, gone again a
    /// few seconds later.
    ///
    /// It used to be thrown across the middle of the board at forty point, which
    /// is fine for one word and covers the game for a sentence. It sits with the
    /// box it was typed into now: still impossible to miss, because it appears
    /// and moves, but no longer standing between a player and the board.
    ///
    /// Deliberately not a chat log. There is no history, nothing to scroll, and
    /// nothing to miss by looking away for a turn - it is closer to a remark
    /// across a table than to messaging, which is the point of it.
    /// </summary>
    public class ShoutBanner : MonoBehaviour
    {
        /// <summary>Wide enough for a sentence, narrow enough to stay in its corner.</summary>
        private const float PanelWidth = 300f;

        private RectTransform _panel;
        private CanvasGroup _group;
        private Text _fromLabel;
        private Text _messageLabel;
        private Coroutine _showing;

        public static ShoutBanner CreateOn(Transform canvas)
        {
            var go = new GameObject("Shout Banner", typeof(RectTransform));
            go.transform.SetParent(canvas, false);

            var banner = go.AddComponent<ShoutBanner>();
            banner.Build();
            return banner;
        }

        private void Build()
        {
            var root = (RectTransform)transform;
            UIFactory.Stretch(root);

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;

            // Never in the way: a message is not something to be answered, so it
            // must not intercept a click meant for the board underneath it.
            _group.blocksRaycasts = false;
            _group.interactable = false;

            // Bottom right, sitting just above the box it was typed into, and
            // sized to its own content rather than to a slab of the screen.
            _panel = UIFactory.Panel("Shout", root, UITheme.SurfaceRaised);
            UITheme.Frame(_panel.GetComponent<Image>(), 1f, UITheme.Signal);
            _panel.anchorMin = _panel.anchorMax = new Vector2(1f, 0f);
            _panel.pivot = new Vector2(1f, 0f);
            UIFactory.SetSize(_panel, PanelWidth, 10f);
            _panel.anchoredPosition = new Vector2(-16f, 54f);

            var layout = UIFactory.VerticalLayout(
                _panel, 2, new RectOffset(12, 12, 8, 8), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandWidth = true;
            UIFactory.FitToContent(
                _panel,
                ContentSizeFitter.FitMode.Unconstrained,
                ContentSizeFitter.FitMode.PreferredSize);

            _fromLabel = UIFactory.Label("From", _panel, "", 12, TextAnchor.MiddleLeft, UITheme.Signal);
            _fromLabel.fontStyle = FontStyle.Bold;
            FixedRow(_fromLabel.rectTransform, 15);

            _messageLabel = UIFactory.Label("Message", _panel, "", 16, TextAnchor.UpperLeft, UITheme.Bone);
            var messageRow = _messageLabel.gameObject.AddComponent<LayoutElement>();
            messageRow.flexibleWidth = 1;

            gameObject.SetActive(false);
        }

        private static void FixedRow(RectTransform rect, float height)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height;
            element.flexibleWidth = 1;
        }

        /// <summary>Throws a message up. A new one replaces whatever is showing.</summary>
        public void Show(string from, string message)
        {
            if (!Application.isPlaying)
            {
                return;
            }

            _fromLabel.text = (from ?? "").ToUpperInvariant();
            _messageLabel.text = message ?? "";

            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            if (_showing != null)
            {
                StopCoroutine(_showing);
            }

            _showing = StartCoroutine(Play());
        }

        private IEnumerator Play()
        {
            const float rise = 0.16f;
            const float hold = 2.6f;
            const float fall = 0.45f;

            var elapsed = 0f;
            while (elapsed < rise)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / rise);
                _group.alpha = t;
                _panel.anchoredPosition = new Vector2(-16f, Mathf.Lerp(34f, 54f, t));
                yield return null;
            }

            _group.alpha = 1f;
            _panel.anchoredPosition = new Vector2(-16f, 54f);
            yield return new WaitForSeconds(hold);

            elapsed = 0f;
            while (elapsed < fall)
            {
                elapsed += Time.deltaTime;
                _group.alpha = 1f - Mathf.SmoothStep(0f, 1f, elapsed / fall);
                yield return null;
            }

            _group.alpha = 0f;
            gameObject.SetActive(false);
            _showing = null;
        }
    }
}
