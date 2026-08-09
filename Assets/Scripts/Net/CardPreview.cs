using System;
using Indoctrination.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// A card blown up over the whole board, with its rules text at a size that
    /// can actually be read, and whatever action that card offers.
    ///
    /// This is what lets the cards on the table be small. Everything on the board
    /// is a thumbnail you can recognise at a glance; this is where you go to read
    /// one properly and decide what to do with it.
    /// </summary>
    public class CardPreview : MonoBehaviour
    {
        private static CardPreview _instance;

        private RectTransform _panel;
        private Text _titleText;
        private Text _metaText;
        private Text _effectText;
        private RectTransform _actionRow;
        private Image _accent;

        /// <summary>
        /// Creates the preview once, parented to the canvas, and keeps it hidden
        /// until a card is clicked.
        /// </summary>
        public static CardPreview CreateOn(Transform canvas)
        {
            var go = new GameObject("Card Preview", typeof(RectTransform));
            go.transform.SetParent(canvas, false);

            _instance = go.AddComponent<CardPreview>();
            _instance.Build();
            return _instance;
        }

        public static void Show(BoardCardView card)
        {
            if (_instance != null && card != null)
            {
                _instance.Display(card);
            }
        }

        /// <summary>
        /// Throws a Ritual up over the whole board for a moment, then lets it fall
        /// away to the discard. A Ritual resolves and is gone in the same instant;
        /// without this the only trace a player gets is the board being different.
        /// </summary>
        public static void FlashRitual(CardDefinition ritual, Vector3 discardPosition)
        {
            if (_instance == null || ritual == null)
            {
                return;
            }

            // The panel has to be showing before the coroutine starts: Unity
            // refuses to run one on an inactive object, so a flash asked for while
            // the preview was closed would simply never happen.
            _instance.ShowDefinition(ritual, "Ritual");
            _instance.transform.SetAsLastSibling();

            if (!Application.isPlaying)
            {
                // No frames outside play mode, so there is nothing to animate.
                _instance.gameObject.SetActive(false);
                return;
            }

            _instance.StartCoroutine(_instance.RitualRoutine(ritual, discardPosition));
        }

        private System.Collections.IEnumerator RitualRoutine(CardDefinition ritual, Vector3 discardPosition)
        {
            ShowDefinition(ritual, "Ritual");
            _actionRow.gameObject.SetActive(false);

            // Nothing behind this is actionable while it plays, so the board is
            // dimmed harder than for an ordinary preview.
            var backdrop = GetComponent<Image>();
            var restingDim = backdrop.color;
            backdrop.color = new Color(0f, 0f, 0f, 0.88f);

            var start = _panel.position;
            var startScale = _panel.localScale;

            // Held long enough to read, since a Ritual is often the biggest thing
            // that happens in a turn and there is nothing left on the board to
            // show for it afterwards.
            yield return new WaitForSeconds(1.15f);

            const float fall = 0.45f;
            var elapsed = 0f;

            while (elapsed < fall)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.SmoothStep(0f, 1f, elapsed / fall);

                _panel.position = Vector3.Lerp(start, discardPosition, t);
                _panel.localScale = startScale * (1f - (0.75f * t));
                yield return null;
            }

            _panel.position = start;
            _panel.localScale = startScale;
            _actionRow.gameObject.SetActive(true);
            backdrop.color = restingDim;
            gameObject.SetActive(false);
        }

        public static void Hide()
        {
            if (_instance != null)
            {
                _instance.gameObject.SetActive(false);
            }
        }

        /// <summary>Whether the preview is currently covering the board.</summary>
        public static bool IsOpen => _instance != null && _instance.gameObject.activeSelf;

        /// <summary>
        /// Puts the preview back on top. Anything else the board adds to the
        /// canvas afterwards - the hand tray, the flight layer - would otherwise
        /// be drawn over a Ritual that is meant to be covering everything.
        /// </summary>
        public static void BringToFront()
        {
            if (IsOpen)
            {
                _instance.transform.SetAsLastSibling();
            }
        }

        private void Build()
        {
            var root = (RectTransform)transform;
            UIFactory.Stretch(root);

            // A dimmed backdrop that also swallows clicks, so anywhere outside the
            // card closes the preview rather than acting on the board underneath.
            var backdrop = gameObject.AddComponent<Image>();
            backdrop.color = new Color(0f, 0f, 0f, 0.72f);

            var dismiss = gameObject.AddComponent<Button>();
            dismiss.targetGraphic = backdrop;
            dismiss.transition = Selectable.Transition.None;
            dismiss.onClick.AddListener(Hide);

            _panel = UIFactory.Panel("Preview Card", root, new Color(0.13f, 0.13f, 0.16f, 0.99f));
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_panel, 460, 420);

            // Clicks on the card itself must not fall through to the backdrop.
            _panel.gameObject.AddComponent<Button>().transition = Selectable.Transition.None;

            var layout = UIFactory.VerticalLayout(_panel, 10, new RectOffset(22, 22, 18, 18), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandWidth = true;

            _accent = UIFactory.Panel("Accent", _panel, Color.white).GetComponent<Image>();
            FixedRow(_accent.rectTransform, 5);

            _titleText = UIFactory.Label("Title", _panel, "", 30, TextAnchor.UpperLeft);
            _titleText.fontStyle = FontStyle.Bold;
            FixedRow(_titleText.rectTransform, 40);

            _metaText = UIFactory.Label("Meta", _panel, "", 16, TextAnchor.UpperLeft, new Color(0.8f, 0.85f, 0.95f));
            FixedRow(_metaText.rectTransform, 46);

            _effectText = UIFactory.Label("Effect", _panel, "", 18, TextAnchor.UpperLeft, new Color(0.93f, 0.93f, 0.93f));
            var effectRow = _effectText.gameObject.AddComponent<LayoutElement>();
            effectRow.flexibleHeight = 1;
            effectRow.flexibleWidth = 1;

            _actionRow = UIFactory.Group("Actions", _panel);
            FixedRow(_actionRow, 44);
            var actionLayout = UIFactory.HorizontalLayout(_actionRow, 10, new RectOffset(0, 0, 0, 0));
            actionLayout.childAlignment = TextAnchor.MiddleCenter;

            gameObject.SetActive(false);
        }

        private static void FixedRow(RectTransform rect, float height)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = height;
            element.preferredHeight = height;
            element.flexibleWidth = 1;
        }

        /// <summary>Fills the panel from a card definition. Shared by the preview and the Ritual flash.</summary>
        private void ShowDefinition(CardDefinition definition, string banner)
        {
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            _titleText.text = definition.Title;
            _accent.color = BoardArt.ColorOf(definition.Color);

            var cost = definition.Cost.IsSpecial ? "special" : definition.costRaw;
            var activates = definition.Type == CardType.Unit && definition.ActivationNumbers.Count > 0
                ? $"\nActivates on {string.Join(", ", definition.ActivationNumbers)}"
                : "";

            var lead = string.IsNullOrEmpty(banner) ? "" : $"{banner.ToUpperInvariant()}   ";
            _metaText.text = $"{lead}{definition.Color} {definition.Type}    Cost: {cost}{activates}";
            _effectText.text = definition.Effect;
        }

        private void Display(BoardCardView card)
        {
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            if (card.Definition == null)
            {
                _titleText.text = card.Card?.definitionId ?? "Unknown card";
                _metaText.text = "";
                _effectText.text = "This client does not recognise that card.";
                _accent.color = Color.grey;
            }
            else
            {
                ShowDefinition(card.Definition, null);
            }

            _actionRow.gameObject.SetActive(true);
            UIFactory.DestroyChildren(_actionRow);

            if (card.Action != null)
            {
                var label = string.IsNullOrEmpty(card.ActionLabel) ? "Confirm" : card.ActionLabel;
                var action = card.Action;

                UIFactory.ButtonWithLabel(label, _actionRow, label, () =>
                {
                    Hide();
                    action();
                }, new Color(0.22f, 0.5f, 0.24f), 200, 40);
            }

            UIFactory.ButtonWithLabel("Close", _actionRow, "Close", Hide,
                new Color(0.3f, 0.3f, 0.34f), 120, 40);
        }
    }
}
