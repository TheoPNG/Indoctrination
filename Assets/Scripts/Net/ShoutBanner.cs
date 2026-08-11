using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Somebody's message, thrown across the board large enough to read from
    /// across the room and gone again a few seconds later.
    ///
    /// Deliberately not a chat log. There is no history, nothing to scroll, and
    /// nothing to miss by looking away for a turn - it is closer to shouting
    /// across a table than to messaging, which is the point of it.
    /// </summary>
    public class ShoutBanner : MonoBehaviour
    {
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

            _panel = UIFactory.Panel("Shout", root, new Color(
                UITheme.Void.r, UITheme.Void.g, UITheme.Void.b, 0.92f));
            UITheme.Frame(_panel.GetComponent<Image>(), 1f, UITheme.Signal);
            _panel.anchorMin = new Vector2(0.08f, 0.58f);
            _panel.anchorMax = new Vector2(0.92f, 0.78f);
            _panel.offsetMin = _panel.offsetMax = Vector2.zero;

            var layout = UIFactory.VerticalLayout(_panel, 6, new RectOffset(20, 20, 12, 12), controlHeight: true);
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = true;

            _fromLabel = UIFactory.Label("From", _panel, "", 16, TextAnchor.MiddleCenter, UITheme.Signal);
            _fromLabel.fontStyle = FontStyle.Bold;
            FixedRow(_fromLabel.rectTransform, 20);

            _messageLabel = UIFactory.Label("Message", _panel, "", 40, TextAnchor.MiddleCenter, UITheme.Bone);
            _messageLabel.fontStyle = FontStyle.Bold;
            var messageRow = _messageLabel.gameObject.AddComponent<LayoutElement>();
            messageRow.flexibleHeight = 1;
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
                _panel.localScale = Vector3.one * Mathf.Lerp(0.88f, 1f, t);
                yield return null;
            }

            _group.alpha = 1f;
            _panel.localScale = Vector3.one;
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
