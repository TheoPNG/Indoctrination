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

        public static void Hide()
        {
            if (_instance != null)
            {
                _instance.gameObject.SetActive(false);
            }
        }

        /// <summary>Whether the preview is currently covering the board.</summary>
        public static bool IsOpen => _instance != null && _instance.gameObject.activeSelf;

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

        private void Display(BoardCardView card)
        {
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            var definition = card.Definition;

            if (definition == null)
            {
                _titleText.text = card.Card?.definitionId ?? "Unknown card";
                _metaText.text = "";
                _effectText.text = "This client does not recognise that card.";
                _accent.color = Color.grey;
            }
            else
            {
                _titleText.text = definition.Title;
                _accent.color = BoardArt.ColorOf(definition.Color);

                var cost = definition.Cost.IsSpecial ? "special" : definition.costRaw;
                var activates = definition.Type == CardType.Unit && definition.ActivationNumbers.Count > 0
                    ? $"\nActivates on {string.Join(", ", definition.ActivationNumbers)}"
                    : "";

                _metaText.text = $"{definition.Color} {definition.Type}    Cost: {cost}{activates}";
                _effectText.text = definition.Effect;
            }

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
