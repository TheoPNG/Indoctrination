using System;
using Indoctrination.Core;
using UnityEngine;
using UnityEngine.EventSystems;
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

        /// <summary>The card's definition, or null if this client does not know it.</summary>
        public CardDefinition Definition { get; private set; }

        /// <summary>Set when this card has an action behind it, for the preview to offer.</summary>
        public Action Action { get; private set; }

        public string ActionLabel { get; private set; }

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

            // The board answers the pointer, not only the click. Handled through
            // EventTrigger rather than the Button's own transition, because the
            // lift is a transform change and Button only tints its graphic.
            var hover = gameObject.AddComponent<EventTrigger>();

            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => BoardEffects.Instance.Hover(rect, hovering: true));
            hover.triggers.Add(enter);

            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => BoardEffects.Instance.Hover(rect, hovering: false));
            hover.triggers.Add(exit);

            // controlHeight lets each label report its own wrapped-text height
            // (Text is a native ILayoutElement), so rows stack snugly instead of
            // every label claiming a default 100px.
            var layout = UIFactory.VerticalLayout(rect, 2, new RectOffset(8, 8, 8, 8), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandHeight = false;

            // Labels take the card's full inner width so their text wraps against
            // the card edge; without this each one is sized to its own unwrapped
            // single-line width and long effect text never wraps at all.
            layout.childForceExpandWidth = true;

            // A draft marker decides whether a card can be taken at all, so it is
            // the loudest thing on the card rather than a caption above the title.
            _tagText = UIFactory.Label("Tag", transform, "", 16, TextAnchor.MiddleCenter, Color.white);
            _tagText.fontStyle = FontStyle.Bold;

            var tagPlate = _tagText.gameObject.AddComponent<Outline>();
            tagPlate.effectColor = new Color(0f, 0f, 0f, 0.95f);
            tagPlate.effectDistance = new Vector2(1.5f, -1.5f);
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
            // The struck-through printed price on a discounted card is markup.
            _costText.supportRichText = true;
            _activatesText = UIFactory.Label("Activates", transform, "", 12, TextAnchor.UpperLeft, new Color(0.7f, 0.85f, 1f));
            _effectText = UIFactory.Label("Effect", transform, "", 12, TextAnchor.UpperLeft, new Color(0.9f, 0.9f, 0.9f));

            foreach (Transform child in transform)
            {
                // Text is created set to overflow, which lets a long effect draw
                // straight over the row beneath it when the card is too short to
                // hold everything. Clipping instead keeps every row inside its own
                // space, and the card's mask hides whatever does not fit.
                var text = child.GetComponent<Text>();
                if (text != null)
                {
                    text.verticalOverflow = VerticalWrapMode.Truncate;
                }

                var element = child.gameObject.AddComponent<LayoutElement>();
                element.flexibleWidth = 1;

                if (child == _tagText.transform)
                {
                    element.minHeight = 22;
                    element.preferredHeight = 22;
                }
                else if (child == _headerText.transform)
                {
                    element.minHeight = 76;
                    element.preferredHeight = 76;
                }
                else if (child == _titleText.transform)
                {
                    element.minHeight = 18;
                    element.preferredHeight = 18;
                }
                else if (child == _effectText.transform)
                {
                    // The effect takes whatever room the fixed rows leave, and
                    // clips inside it rather than pushing the card out of shape.
                    element.flexibleHeight = 1;
                    element.minHeight = 0;
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
            SetAction(null, onClick);
            _tagText.text = tag ?? "";
            _tagText.gameObject.SetActive(!string.IsNullOrEmpty(tag));

            // The whole card takes the marker's colour, so a blocked or reserved
            // card is obvious from across the board rather than on inspection.
            if (!string.IsNullOrEmpty(tag))
            {
                var marked = tag.StartsWith("BLOCKED")
                    ? new Color(0.55f, 0.16f, 0.16f)
                    : tag.StartsWith("RESERVED")
                        ? new Color(0.18f, 0.34f, 0.55f)
                        : new Color(0.5f, 0.34f, 0.12f);

                _background.color = marked;
                _tagText.color = Color.white;
            }

            try
            {
                var definition = CardDatabase.Instance.TryGet(card.definitionId, out var found) ? found : null;
                Definition = definition;
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
                    _costText.text = CostLine(card, definition);
                    _costText.color = card.isDiscounted
                        ? new Color(0.55f, 0.95f, 0.6f)
                        : new Color(0.85f, 0.85f, 0.6f);

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

            // Every card opens its own preview, whether or not it can be acted on -
            // reading a card should never depend on it being your turn. The action,
            // when there is one, is offered from inside the preview.
            _button.onClick.RemoveAllListeners();
            _button.interactable = true;
            _button.onClick.AddListener(() => CardPreview.Show(this));
        }

        /// <summary>
        /// Gives this card an action, shown as the primary button on its preview.
        /// </summary>
        public void SetAction(string label, Action action)
        {
            ActionLabel = label;
            Action = action;
        }

        /// <summary>
        /// The cost as it applies to whoever is holding it. A discounted card
        /// shows the printed price struck through next to what it actually costs,
        /// so the saving is visible rather than something to work out.
        /// </summary>
        private static string CostLine(CardView card, CardDefinition definition)
        {
            var printed = definition.Cost.IsSpecial ? "special" : definition.costRaw;

            if (!card.isDiscounted)
            {
                return $"Cost: {(string.IsNullOrEmpty(printed) ? "free" : printed)}";
            }

            // Only the price that actually applies, in green. Unity's Text has no
            // strikethrough - <s> is not one of its tags and printed straight
            // through as literal angle brackets.
            var actual = string.IsNullOrEmpty(card.costForYou) ? "free" : card.costForYou;
            return $"Cost: {actual}  (down from {printed})";
        }

        /// <summary>
        /// Marks this card as one its holder could play right now: a card you can
        /// afford should be findable in a hand without pricing each one yourself.
        /// </summary>
        public void SetAffordable(bool affordable)
        {
            _background.color = affordable
                ? new Color(0.17f, 0.26f, 0.18f)
                : new Color(0.15f, 0.15f, 0.17f);

            if (!affordable)
            {
                return;
            }

            var edge = gameObject.GetComponent<Outline>() ?? gameObject.AddComponent<Outline>();
            edge.effectColor = new Color(0.4f, 0.85f, 0.45f, 0.9f);
            edge.effectDistance = new Vector2(2f, -2f);
        }

        /// <summary>
        /// Gives the card a standing highlight, for a unit the dice have already
        /// promised to wake. Coloured by what the card will do, so the board says
        /// what is coming without needing a list beside it.
        /// </summary>
        public void SetDueToActivate(Color tint)
        {
            _background.color = Color.Lerp(new Color(0.15f, 0.15f, 0.17f), tint, 0.28f);

            var edge = gameObject.GetComponent<Outline>() ?? gameObject.AddComponent<Outline>();
            edge.effectColor = new Color(tint.r, tint.g, tint.b, 0.9f);
            edge.effectDistance = new Vector2(2.5f, -2.5f);
        }

        /// <summary>
        /// Scales the card to fit the space available. The card is laid out once
        /// at its natural size and then scaled, so every card shrinks by the same
        /// amount and the text keeps its proportions instead of reflowing into a
        /// different shape at every size.
        /// </summary>
        public void ScaleTo(float width)
        {
            var factor = Mathf.Clamp(width / Width, 0.1f, 1f);
            transform.localScale = new Vector3(factor, factor, 1f);

            // This is the card's resting size now, so a hover that ends knows
            // what to return to rather than snapping back to full size.
            BoardEffects.Instance.ForgetRestingScale((RectTransform)transform);
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
