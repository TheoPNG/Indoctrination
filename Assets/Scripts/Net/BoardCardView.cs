using System;
using Indoctrination.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// One card's visual: a fixed-size rectangle carrying its colour, type, cost,
    /// activation numbers, and full effect text, with an optional click handler
    /// and an optional tag banner (used for the draft markers). The same widget
    /// backs the hand, the battlefield, the draft zone, and card-choice prompts,
    /// so a card looks the same everywhere it appears.
    /// </summary>
    public class BoardCardView : MonoBehaviour
    {
        public const float Width = 180f;
        public const float Height = 250f;

        private Image _background;
        private Text _tagText;
        private Text _headerText;
        private Text _titleText;
        private Text _costText;
        private Text _activatesText;
        private Text _effectText;
        private Button _button;

        public CardView Card { get; private set; }

        private bool _built;

        private void Awake() => Build();

        /// <summary>
        /// Builds the card, once. Called from the factory rather than left to
        /// Awake, which the Editor does not run outside play mode.
        /// </summary>
        private void Build()
        {
            if (_built)
            {
                return;
            }

            _built = true;

            var rect = (RectTransform)transform;
            UIFactory.SetSize(rect, Width, Height);

            // A parent HorizontalLayoutGroup (the hand/compound/draft-zone scroll
            // strips) recomputes each child's size from its own layout unless told
            // otherwise - this pins the card to its fixed poker-card size no
            // matter which strip it ends up in.
            var pin = gameObject.AddComponent<LayoutElement>();
            pin.minWidth = Width;
            pin.minHeight = Height;
            pin.preferredWidth = Width;
            pin.preferredHeight = Height;

            _background = gameObject.AddComponent<Image>();
            _background.color = new Color(0.15f, 0.15f, 0.17f);

            // Effect text can run long; clipping it at the card's edge keeps every
            // card the same rectangle instead of growing to fit its own content.
            gameObject.AddComponent<RectMask2D>();

            _button = gameObject.AddComponent<Button>();
            _button.targetGraphic = _background;
            _button.interactable = false;

            // controlHeight lets each label report its own wrapped-text height
            // (Text is a native ILayoutElement), so rows stack snugly instead of
            // every label claiming a default 100px.
            var layout = UIFactory.VerticalLayout(rect, 2, new RectOffset(8, 8, 8, 8), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperLeft;

            // Labels take the card's full inner width so their text wraps against
            // the card edge; without this each one is sized to its own unwrapped
            // single-line width and long effect text never wraps at all.
            layout.childForceExpandWidth = true;

            _tagText = UIFactory.Label("Tag", transform, "", 12, TextAnchor.UpperLeft, new Color(1f, 0.6f, 0.35f));
            // Put the title in the first permanent text row. The former separate
            // title label was the field disappearing during layout; the old header
            // (colour/type) was already rendering reliably in every card row.
            _headerText = UIFactory.Label(
                "Title", transform, "", 20, TextAnchor.UpperLeft, Color.white);
            _headerText.fontStyle = FontStyle.Bold;
            var titleOutline = _headerText.gameObject.AddComponent<Outline>();
            titleOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            titleOutline.effectDistance = new Vector2(1f, -1f);
            _titleText = UIFactory.Label("Details", transform, "", 13, TextAnchor.UpperLeft);
            _costText = UIFactory.Label("Cost", transform, "", 12, TextAnchor.UpperLeft, new Color(0.85f, 0.85f, 0.6f));
            _activatesText = UIFactory.Label("Activates", transform, "", 12, TextAnchor.UpperLeft, new Color(0.7f, 0.85f, 1f));
            _effectText = UIFactory.Label("Effect", transform, "", 12, TextAnchor.UpperLeft, new Color(0.9f, 0.9f, 0.9f));

            foreach (Transform child in transform)
            {
                var element = child.gameObject.AddComponent<LayoutElement>();
                element.flexibleWidth = 1;

                if (child == _headerText.transform)
                {
                    element.minHeight = 76;
                    element.preferredHeight = 76;
                }
                else if (child == _titleText.transform)
                {
                    element.minHeight = 18;
                    element.preferredHeight = 18;
                }
            }
        }

        /// <summary>
        /// Fills the card in. Reading card data can throw if a client's copy of
        /// Cards.json does not recognise the id - caught here so one bad card
        /// cannot break the rest of the board around it.
        /// </summary>
        public void Populate(CardView card, string tag, Action onClick)
        {
            Card = card;
            _tagText.text = tag ?? "";
            _tagText.gameObject.SetActive(!string.IsNullOrEmpty(tag));

            try
            {
                var definition = CardDatabase.Instance.TryGet(card.definitionId, out var found) ? found : null;
                if (definition == null)
                {
                    _headerText.text = card.definitionId;
                    _headerText.color = Color.white;
                    _titleText.text = "";
                    _costText.text = "";
                    _activatesText.gameObject.SetActive(false);
                    _effectText.text = "";
                }
                else
                {
                    _headerText.text = definition.Title;
                    _headerText.color = Color.white;
                    _titleText.text = $"{definition.Color}  -  {definition.Type}";
                    _titleText.color = ColorFor(definition.Color);
                    _costText.text = $"Cost: {(definition.Cost.IsSpecial ? "special" : definition.costRaw)}";

                    if (definition.Type == CardType.Unit && definition.ActivationNumbers.Count > 0)
                    {
                        _activatesText.gameObject.SetActive(true);
                        _activatesText.text = $"Activates: {string.Join(", ", definition.ActivationNumbers)}";
                    }
                    else
                    {
                        _activatesText.gameObject.SetActive(false);
                    }

                    _effectText.text = definition.Effect;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"BoardCardView could not read '{card?.definitionId}': {e}");
                _headerText.text = card?.definitionId ?? "?";
                _headerText.color = Color.white;
                _titleText.text = "";
                _costText.text = "(error - see Console)";
                _activatesText.gameObject.SetActive(false);
                _effectText.text = "";
            }

            _button.onClick.RemoveAllListeners();
            _button.interactable = onClick != null;
            if (onClick != null)
            {
                _button.onClick.AddListener(() => onClick());
            }
        }

        public static BoardCardView Create(Transform parent)
        {
            // Built with a RectTransform from the start rather than added in
            // Awake: AddComponent<BoardCardView> runs Awake immediately, and the
            // layout code there needs the RectTransform to already exist.
            var go = new GameObject("Card", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var card = go.AddComponent<BoardCardView>();
            card.Build();
            return card;
        }

        private static Color ColorFor(ResourceColor color)
        {
            return color switch
            {
                ResourceColor.Red => new Color(0.88f, 0.35f, 0.35f),
                ResourceColor.Green => new Color(0.31f, 0.69f, 0.35f),
                ResourceColor.Blue => new Color(0.33f, 0.53f, 0.88f),
                ResourceColor.Yellow => new Color(0.82f, 0.66f, 0.24f),
                _ => Color.white
            };
        }
    }
}
