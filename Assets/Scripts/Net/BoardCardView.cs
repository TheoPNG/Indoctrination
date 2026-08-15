using System;
using System.Linq;
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
        public const float Height = 252f;

        private Image _background;
        private Image _printedFace;
        private Outline _frame;
        private Text _tagText;
        private Text _headerText;
        private Text _titleText;
        private Text _costText;
        private Text _activatesText;
        private Text _effectText;
        private RectTransform _discountStamps;
        private RectTransform _counterStack;
        private Button _button;
        private EventTrigger _hover;

        /// <summary>
        /// Raised as the pointer arrives and leaves. The hand uses it to bring
        /// the card being looked at out from under its neighbours; the fan
        /// overlaps on purpose, so the card under the pointer is otherwise the
        /// one half-covered.
        /// </summary>
        public Action<bool> OnHoverChanged;
        private string _counterSignature = "";

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
            _background.color = UITheme.SurfaceRaised;
            UITheme.Frame(_background, 1.15f);
            _frame = gameObject.GetComponent<Outline>();

            // Effect text can run long; clipping it at the card's edge keeps every
            // card the same rectangle instead of growing to fit its own content.
            gameObject.AddComponent<RectMask2D>();

            _button = gameObject.AddComponent<Button>();
            _button.targetGraphic = _background;
            _button.interactable = false;

            // The board answers the pointer, not only the click. Handled through
            // EventTrigger rather than the Button's own transition, because the
            // lift is a transform change and Button only tints its graphic.
            _hover = gameObject.AddComponent<EventTrigger>();

            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ =>
            {
                BoardEffects.Instance.Hover(rect, hovering: true);
                OnHoverChanged?.Invoke(true);
            });
            _hover.triggers.Add(enter);

            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ =>
            {
                BoardEffects.Instance.Hover(rect, hovering: false);
                OnHoverChanged?.Invoke(false);
            });
            _hover.triggers.Add(exit);

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
            _tagText = UIFactory.Label("Tag", transform, "", 16, TextAnchor.MiddleCenter, UITheme.Bone);
            _tagText.fontStyle = FontStyle.Bold;

            var tagPlate = _tagText.gameObject.AddComponent<Outline>();
            tagPlate.effectColor = new Color(0f, 0f, 0f, 0.95f);
            tagPlate.effectDistance = new Vector2(1.5f, -1.5f);
            // Put the title in the first permanent text row. The former separate
            // title label was the field disappearing during layout; the old header
            // (colour/type) was already rendering reliably in every card row.
            _headerText = UIFactory.Label(
                "Title", transform, "", 20, TextAnchor.UpperLeft, UITheme.Bone);
            _headerText.fontStyle = FontStyle.Bold;
            var titleOutline = _headerText.gameObject.AddComponent<Outline>();
            titleOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            titleOutline.effectDistance = new Vector2(1f, -1f);
            _titleText = UIFactory.Label("Details", transform, "", 13, TextAnchor.UpperLeft);
            _costText = UIFactory.Label("Cost", transform, "", 12, TextAnchor.UpperLeft, UITheme.BoneDim);
            // The struck-through printed price on a discounted card is markup.
            _costText.supportRichText = true;
            _activatesText = UIFactory.Label(
                "Activates", transform, "", 12, TextAnchor.UpperLeft, UITheme.Signal);
            _effectText = UIFactory.Label("Effect", transform, "", 12, TextAnchor.UpperLeft, UITheme.Bone);

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

            // Printed faces sit behind the tag and the code-built fallback. They
            // ignore the text layout because the PDF page already is the whole
            // card, and preserving its 5:7 aspect keeps the border from stretching.
            var printedFace = UIFactory.Panel("Printed Face", transform, Color.white);
            printedFace.SetSiblingIndex(0);
            UIFactory.Stretch(printedFace);
            var printedFaceLayout = printedFace.gameObject.AddComponent<LayoutElement>();
            printedFaceLayout.ignoreLayout = true;

            _printedFace = printedFace.GetComponent<Image>();
            _printedFace.preserveAspect = true;
            _printedFace.raycastTarget = false;
            _printedFace.gameObject.SetActive(false);

            // Printed art already contains its cost. A live discount therefore
            // belongs over the print as a small tabletop stamp, not as a second
            // rewritten cost line competing with the PDF.
            _discountStamps = UIFactory.Group("Discount Stamps", transform);
            _discountStamps.anchorMin = _discountStamps.anchorMax = new Vector2(0.5f, 0.5f);
            _discountStamps.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_discountStamps, 160f, 34f);
            var stampLayout = UIFactory.HorizontalLayout(
                _discountStamps, 5, new RectOffset(0, 0, 0, 0),
                controlWidth: false, controlHeight: false);
            stampLayout.childAlignment = TextAnchor.MiddleCenter;
            var stampLayoutElement = _discountStamps.gameObject.AddComponent<LayoutElement>();
            stampLayoutElement.ignoreLayout = true;
            _discountStamps.gameObject.SetActive(false);

            // Counters are physical pieces on top of a card, not another line of
            // rules text. The stack ignores the card layout and sits over its
            // upper-right corner like chips placed on a tabletop card.
            _counterStack = UIFactory.Group("Counter Stack", transform);
            _counterStack.anchorMin = _counterStack.anchorMax = new Vector2(1f, 1f);
            _counterStack.pivot = new Vector2(1f, 1f);
            _counterStack.anchoredPosition = new Vector2(-5f, -5f);
            UIFactory.SetSize(_counterStack, 58f, 90f);
            var counterPin = _counterStack.gameObject.AddComponent<LayoutElement>();
            counterPin.ignoreLayout = true;
            _counterStack.gameObject.SetActive(false);
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
            SetExtraContent(null);
            _tagText.text = tag ?? "";
            _tagText.gameObject.SetActive(!string.IsNullOrEmpty(tag));
            SetCodeBuiltFaceVisible(true);
            _printedFace.sprite = null;
            _printedFace.gameObject.SetActive(false);
            UIFactory.DestroyChildren(_discountStamps);
            _discountStamps.gameObject.SetActive(false);
            _background.color = UITheme.SurfaceRaised;
            _frame.effectColor = UITheme.Border;
            _frame.effectDistance = new Vector2(1f, -1f);

            // The whole card takes the marker's colour, so a blocked or reserved
            // card is obvious from across the board rather than on inspection.
            if (!string.IsNullOrEmpty(tag))
            {
                var marked = tag.StartsWith("BLOCKED")
                    ? new Color(0.361f, 0.106f, 0.145f)
                    : tag.StartsWith("RESERVED")
                        ? new Color(0.129f, 0.192f, 0.290f)
                        : new Color(0.290f, 0.196f, 0.098f);

                _background.color = marked;
                _tagText.color = UITheme.Bone;
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
                    _headerText.color = UITheme.Bone;
                    _frame.effectColor = Color.Lerp(UITheme.Border, BoardArt.ColorOf(definition.Color), 0.52f);
                    _titleText.text = $"{definition.Color}  -  {definition.Type}";
                    _titleText.color = ColorFor(definition.Color);
                    _costText.text = CostLine(card, definition);

                    // The accent means "something is different about this" -
                    // here, that the card is cheaper than it says it is. A full
                    // price is ordinary, so it reads as ordinary text.
                    _costText.color = card.isDiscounted ? UITheme.Signal : UITheme.BoneDim;

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

                    var printedFace = CardArt.FaceFor(definition.Id);
                    if (printedFace != null)
                    {
                        _printedFace.sprite = printedFace;
                        _printedFace.gameObject.SetActive(true);
                        SetCodeBuiltFaceVisible(false);
                        BuildDiscountStamps(card, definition);
                    }


                    UpdateCounters(card);
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

        /// <summary>Disables previews and hover motion on a locked presentation card.</summary>
        public void SetPreviewEnabled(bool enabled)
        {
            _button.interactable = enabled;
            if (_hover != null)
            {
                _hover.enabled = enabled;
            }
        }

        /// <summary>Refreshes the visible chip stack without rebuilding the card.</summary>
        public void UpdateCounters(CardView card)
        {
            Card = card;
            var counters = card?.counters ?? Array.Empty<CounterView>();
            var signature = string.Join("|", counters.Select(counter => $"{counter.name}:{counter.count}"));
            var changed = !string.Equals(signature, _counterSignature, StringComparison.Ordinal);
            _counterSignature = signature;

            UIFactory.DestroyChildren(_counterStack);
            _counterStack.gameObject.SetActive(counters.Length > 0);

            for (var i = 0; i < counters.Length; i++)
            {
                var counter = counters[i];
                var chip = UIFactory.Panel(counter.name, _counterStack,
                    BoardArt.ColorOfCounter(counter.name));
                chip.anchorMin = chip.anchorMax = new Vector2(1f, 1f);
                chip.pivot = new Vector2(1f, 1f);
                chip.anchoredPosition = new Vector2(-(i % 2) * 17f, -i * 20f);
                UIFactory.SetSize(chip, 34f, 34f);
                var image = chip.GetComponent<Image>();
                image.sprite = BoardArt.Disc;
                image.raycastTarget = false;
                UITheme.Frame(image, 1.2f, UITheme.Bone);

                // The number alone. Prefixing the kind's initial made a single
                // meal counter read as "M1" - which looks like a quantity of
                // eleven, or a code, rather than one meal. The kind is carried
                // by the chip's colour, and spelled out in the card's preview.
                var label = UIFactory.Label("Count", chip, counter.count.ToString(),
                    15, TextAnchor.MiddleCenter, UITheme.Void);
                label.fontStyle = FontStyle.Bold;
                label.raycastTarget = false;
                UIFactory.Stretch(label.rectTransform);
            }

            if (changed && counters.Length > 0)
            {
                BoardEffects.Instance.Pop(_counterStack, 1.22f, 0.3f);
            }
        }

        private void SetCodeBuiltFaceVisible(bool visible)
        {
            _headerText.gameObject.SetActive(visible);
            _titleText.gameObject.SetActive(visible);
            _costText.gameObject.SetActive(visible);
            _activatesText.gameObject.SetActive(visible);
            _effectText.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Draws one circled -1 for each resource removed from the printed cost,
        /// colored by the resource that was actually reduced.
        /// </summary>
        private void BuildDiscountStamps(CardView card, CardDefinition definition)
        {
            if (!card.isDiscounted || string.IsNullOrEmpty(card.costForYou)
                                   || definition.Cost.IsSpecial)
            {
                return;
            }

            foreach (var color in DiscountStampColors(card, definition))
            {
                CreateDiscountStamp(_discountStamps, color, 32f, 15);
            }

            _discountStamps.gameObject.SetActive(_discountStamps.childCount > 0);
        }

        /// <summary>The resource color of every single point removed from a printed cost.</summary>
        public static System.Collections.Generic.IEnumerable<ResourceColor> DiscountStampColors(
            CardView card, CardDefinition definition)
        {
            if (card == null || definition == null || !card.isDiscounted
                || string.IsNullOrEmpty(card.costForYou) || definition.Cost.IsSpecial)
            {
                yield break;
            }

            var actual = CardCost.Parse(card.costForYou);
            var colors = new[]
            {
                ResourceColor.Red, ResourceColor.Green,
                ResourceColor.Blue, ResourceColor.Yellow
            };

            foreach (var color in colors)
            {
                var printedAmount = definition.Cost.Amounts.TryGetValue(color, out var printed)
                    ? printed
                    : 0;
                var actualAmount = actual.Amounts.TryGetValue(color, out var paid)
                    ? paid
                    : 0;

                for (var i = 0; i < printedAmount - actualAmount; i++)
                {
                    yield return color;
                }
            }
        }

        /// <summary>Builds the circled -1 used over both a card and its enlarged PDF.</summary>
        public static RectTransform CreateDiscountStamp(
            Transform parent, ResourceColor color, float size, int fontSize)
        {
            var badge = UIFactory.Panel($"Discount {color}", parent, BoardArt.ColorOf(color));
            UIFactory.SetSize(badge, size, size);
            var image = badge.GetComponent<Image>();
            image.sprite = BoardArt.Disc;
            image.raycastTarget = false;
            UITheme.Frame(image, 1.4f, UITheme.Bone);

            var pin = badge.gameObject.AddComponent<LayoutElement>();
            pin.minWidth = pin.preferredWidth = size;
            pin.minHeight = pin.preferredHeight = size;

            var label = UIFactory.Label(
                "Value", badge, "−1", fontSize, TextAnchor.MiddleCenter, UITheme.Bone);
            label.fontStyle = FontStyle.Bold;
            label.raycastTarget = false;
            UIFactory.Stretch(label.rectTransform);
            return badge;
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
        /// A card whose ability is a small menu of its own - Suspicious Chef's
        /// payment, Baal's die - builds it here rather than crowding a fixed
        /// panel that shows for every card. Only offered while it is actually
        /// this card's move to make.
        /// </summary>
        public Action<RectTransform> ExtraContentBuilder { get; private set; }

        public void SetExtraContent(Action<RectTransform> builder) => ExtraContentBuilder = builder;

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
            // Barely lifted off the resting surface, then edged in the accent.
            // A playable card should look lit, not painted a different colour -
            // the old green wash made half a hand look like a different game.
            _background.color = affordable
                ? Color.Lerp(UITheme.SurfaceRaised, UITheme.Signal, 0.10f)
                : UITheme.SurfaceRaised;

            var edge = Edge();

            if (!affordable)
            {
                edge.effectColor = UITheme.Border;
                edge.effectDistance = new Vector2(1f, -1f);
                return;
            }

            edge.effectColor = new Color(UITheme.Signal.r, UITheme.Signal.g, UITheme.Signal.b, 0.85f);
            edge.effectDistance = new Vector2(2f, -2f);
        }

        /// <summary>
        /// Gives the card a standing highlight, for a unit the dice have already
        /// promised to wake. Coloured by what the card will do, so the board says
        /// what is coming without needing a list beside it.
        /// </summary>
        /// <summary>
        /// Lights the card while it is one of the ones you are being asked to
        /// take, and lets it breathe so the row reads as waiting on you.
        ///
        /// The draft row otherwise looks the same whether it is your pick or
        /// somebody else's - the only difference was whether dragging happened
        /// to work, which is a thing you find out by trying it.
        /// </summary>
        public void SetAwaitingYourPick(bool waiting)
        {
            AwaitingYourPick = waiting;

            // The tint goes on first, so the pulse takes it as its resting
            // colour and returns to it rather than to the plain background.
            if (waiting)
            {
                _background.color = Color.Lerp(UITheme.SurfaceRaised, UITheme.Signal, 0.20f);

                var edge = Edge();
                edge.effectColor = new Color(UITheme.Signal.r, UITheme.Signal.g, UITheme.Signal.b, 0.85f);
                edge.effectDistance = new Vector2(2f, -2f);
            }

            BoardEffects.Instance.SetPulsing(_background, waiting);
        }

        /// <summary>Whether this card is lit as one of yours to pick.</summary>
        public bool AwaitingYourPick { get; private set; }

        public void SetDueToActivate(Color tint)
        {
            _background.color = Color.Lerp(UITheme.SurfaceRaised, tint, 0.22f);

            var edge = Edge();
            edge.effectColor = new Color(tint.r, tint.g, tint.b, 0.9f);
            edge.effectDistance = new Vector2(2f, -2f);
        }

        /// <summary>
        /// Removes the standing dice highlight without rebuilding the card. Die
        /// results are presentation layered over a stable table, so changing a
        /// roll should not deal every card onto the board again.
        /// </summary>
        public void ClearDueToActivate()
        {
            _background.color = UITheme.SurfaceRaised;

            var edge = Edge();
            edge.effectColor = Definition == null
                ? UITheme.Border
                : Color.Lerp(UITheme.Border, BoardArt.ColorOf(Definition.Color), 0.52f);
            edge.effectDistance = new Vector2(1f, -1f);
        }

        /// <summary>
        /// During Activation, queued Units burn white while everything else is
        /// deliberately dulled. A Unit stays bright when duplicate dice still
        /// owe it another turn.
        /// </summary>
        public void SetActivationState(bool presenting, bool queued)
        {
            var group = gameObject.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = gameObject.AddComponent<CanvasGroup>();
            }

            group.alpha = presenting ? (queued ? 1f : 0.30f) : 1f;
            if (!presenting)
            {
                return;
            }

            var edge = Edge();
            edge.effectColor = queued ? Color.white : new Color(0.15f, 0.14f, 0.18f, 0.55f);
            edge.effectDistance = queued ? new Vector2(3f, -3f) : new Vector2(1f, -1f);
        }

        /// <summary>
        /// This card's outline, added if it is not already there.
        ///
        /// Deliberately not `GetComponent() ?? AddComponent()`: Unity's fake-null
        /// for a missing component is not a C# null, so `??` never fires and the
        /// component is never added. It only appeared to work here because
        /// UITheme.Frame had already added one during Build.
        /// </summary>
        private Outline Edge()
        {
            var edge = gameObject.GetComponent<Outline>();
            if (edge == null)
            {
                edge = gameObject.AddComponent<Outline>();
            }

            return edge;
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

        private static Color ColorFor(ResourceColor color) => BoardArt.ColorOf(color);
    }
}
