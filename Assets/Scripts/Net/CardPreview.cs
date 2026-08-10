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
        private const float PrintedWidth = 320f;
        private const float PrintedHeight = PrintedWidth * 7f / 5f;
        private const float PrintedY = 100f;
        private const float PrintedControlGap = 10f;
        private const float MaxPrintedControlHeight = 200f;

        private static CardPreview _instance;

        private RectTransform _panel;
        private RectTransform _printedPanel;
        private Image _printedFace;
        private RectTransform _printedDiscountStamps;
        private Text _titleText;
        private Text _metaText;
        private Text _effectText;
        private RectTransform _actionRow;
        private RectTransform _extraContent;
        private Image _accent;
        private bool _printedMode;

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

            // The printed card is the thing that falls away. Its separate control
            // tray is hidden for the flash, just as the text card hides its buttons.
            var animated = _printedMode ? _printedPanel : _panel;
            if (_printedMode)
            {
                _panel.gameObject.SetActive(false);
            }

            // Nothing behind this is actionable while it plays, so the board is
            // dimmed harder than for an ordinary preview.
            var backdrop = GetComponent<Image>();
            var restingDim = backdrop.color;
            backdrop.color = new Color(0f, 0f, 0f, 0.88f);

            var start = animated.position;
            var startScale = animated.localScale;

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

                animated.position = Vector3.Lerp(start, discardPosition, t);
                animated.localScale = startScale * (1f - (0.75f * t));
                yield return null;
            }

            animated.position = start;
            animated.localScale = startScale;
            _panel.gameObject.SetActive(true);
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

            // A backdrop that swallows clicks, so anywhere outside the card
            // closes the preview rather than acting on the board underneath.
            // Deliberately light: you often open a card to decide something
            // about the board, and blacking the board out to read one card in
            // front of it defeats the point.
            var backdrop = gameObject.AddComponent<Image>();
            backdrop.color = new Color(
                UITheme.Void.r, UITheme.Void.g, UITheme.Void.b, 0.62f);

            var dismiss = gameObject.AddComponent<Button>();
            dismiss.targetGraphic = backdrop;
            dismiss.transition = Selectable.Transition.None;
            dismiss.onClick.AddListener(Hide);

            // Imported cards get a literal enlarged print, kept separate from the
            // control tray so live card actions never cover their rules text.
            _printedPanel = UIFactory.Panel("Printed Card", root, Color.white);
            UITheme.Frame(_printedPanel.GetComponent<Image>(), 1.5f, UITheme.Border);
            _printedPanel.anchorMin = _printedPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _printedPanel.anchoredPosition = new Vector2(0f, PrintedY);
            UIFactory.SetSize(_printedPanel, PrintedWidth, PrintedHeight);

            _printedFace = _printedPanel.GetComponent<Image>();
            _printedFace.preserveAspect = true;

            _printedDiscountStamps = UIFactory.Group("Discount Stamps", _printedPanel);
            _printedDiscountStamps.anchorMin = _printedDiscountStamps.anchorMax = new Vector2(0.5f, 0.5f);
            _printedDiscountStamps.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_printedDiscountStamps, 240f, 48f);
            var discountLayout = UIFactory.HorizontalLayout(
                _printedDiscountStamps, 7, new RectOffset(0, 0, 0, 0),
                controlWidth: false, controlHeight: false);
            discountLayout.childAlignment = TextAnchor.MiddleCenter;
            _printedDiscountStamps.gameObject.SetActive(false);

            _printedPanel.gameObject.AddComponent<Button>().transition = Selectable.Transition.None;
            _printedPanel.gameObject.SetActive(false);

            _panel = UIFactory.Panel("Preview Card", root, UITheme.SurfaceRaised);
            UITheme.Frame(_panel.GetComponent<Image>(), 1.5f);
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_panel, 460, 420);

            // Clicks on the card itself must not fall through to the backdrop.
            _panel.gameObject.AddComponent<Button>().transition = Selectable.Transition.None;

            var layout = UIFactory.VerticalLayout(_panel, 10, new RectOffset(22, 22, 18, 18), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childForceExpandWidth = true;

            _accent = UIFactory.Panel("Accent", _panel, Color.white).GetComponent<Image>();
            FixedRow(_accent.rectTransform, 5);

            _titleText = UIFactory.Label("Title", _panel, "", 30, TextAnchor.UpperLeft, UITheme.Bone);
            _titleText.fontStyle = FontStyle.Bold;
            FixedRow(_titleText.rectTransform, 40);

            _metaText = UIFactory.Label("Meta", _panel, "", 16, TextAnchor.UpperLeft, UITheme.BoneDim);
            FixedRow(_metaText.rectTransform, 46);

            _effectText = UIFactory.Label("Effect", _panel, "", 18, TextAnchor.UpperLeft, UITheme.Bone);
            var effectRow = _effectText.gameObject.AddComponent<LayoutElement>();
            effectRow.flexibleHeight = 1;
            effectRow.flexibleWidth = 1;

            // A card whose ability is a small menu of its own builds it here,
            // between what the card says and any action it offers.
            _extraContent = UIFactory.Group("Extra", _panel);
            UIFactory.VerticalLayout(_extraContent, 6, new RectOffset(0, 0, 0, 0), controlHeight: true);
            UIFactory.FitToContent(
                _extraContent, ContentSizeFitter.FitMode.Unconstrained, ContentSizeFitter.FitMode.PreferredSize);

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

            SetPrintedFace(CardArt.FaceFor(definition.Id));
        }

        private void Display(BoardCardView card)
        {
            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            if (card.Definition == null)
            {
                SetPrintedFace(null);
                _titleText.text = card.Card?.definitionId ?? "Unknown card";
                _metaText.text = "";
                _effectText.text = "This client does not recognise that card.";
                _accent.color = Color.grey;
            }
            else
            {
                ShowDefinition(card.Definition, null);
            }

            ShowDiscountStamps(card);

            UIFactory.DestroyChildren(_extraContent);
            card.ExtraContentBuilder?.Invoke(_extraContent);

            UIFactory.DestroyChildren(_actionRow);

            if (card.Action != null)
            {
                var label = string.IsNullOrEmpty(card.ActionLabel) ? "Confirm" : card.ActionLabel;
                var action = card.Action;

                UIFactory.ButtonWithLabel(label, _actionRow, label, () =>
                {
                    Hide();
                    action();
                }, UITheme.Affirm, 200, 40);
            }

            _actionRow.gameObject.SetActive(_actionRow.childCount > 0);

            SizePrintedControlTray();
        }

        private void SetPrintedFace(Sprite face)
        {
            _printedMode = face != null;
            _printedFace.sprite = face;
            UIFactory.DestroyChildren(_printedDiscountStamps);
            _printedDiscountStamps.gameObject.SetActive(false);
            _printedPanel.gameObject.SetActive(_printedMode);
            _printedPanel.anchoredPosition = new Vector2(0f, PrintedY);
            _printedPanel.localScale = Vector3.one;

            _accent.gameObject.SetActive(!_printedMode);
            _titleText.gameObject.SetActive(!_printedMode);
            _metaText.gameObject.SetActive(!_printedMode);
            _effectText.gameObject.SetActive(!_printedMode);

            _panel.gameObject.SetActive(true);
            if (!_printedMode)
            {
                UIFactory.SetSize(_panel, 460f, 420f);
                _panel.anchoredPosition = Vector2.zero;
            }
        }

        private void ShowDiscountStamps(BoardCardView card)
        {
            if (!_printedMode || card?.Definition == null)
            {
                return;
            }

            foreach (var color in BoardCardView.DiscountStampColors(card.Card, card.Definition))
            {
                BoardCardView.CreateDiscountStamp(_printedDiscountStamps, color, 44f, 20);
            }

            _printedDiscountStamps.gameObject.SetActive(_printedDiscountStamps.childCount > 0);
        }

        /// <summary>
        /// Keeps controls below the printed card rather than covering its effect
        /// text. A plain printed preview needs no tray because clicking the
        /// backdrop dismisses it; live controls grow one only as much as needed.
        /// </summary>
        private void SizePrintedControlTray()
        {
            var hasExtra = _extraContent.childCount > 0;
            var hasActions = _actionRow.childCount > 0;

            if (!_printedMode)
            {
                _extraContent.gameObject.SetActive(hasExtra);
                _actionRow.gameObject.SetActive(hasActions);
                return;
            }

            _extraContent.gameObject.SetActive(hasExtra);
            _actionRow.gameObject.SetActive(hasActions);

            if (!hasExtra && !hasActions)
            {
                _panel.gameObject.SetActive(false);
                return;
            }

            _panel.gameObject.SetActive(true);
            if (hasExtra)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_extraContent);
            }

            var extraHeight = hasExtra ? LayoutUtility.GetPreferredHeight(_extraContent) : 0f;
            var height = 36f
                         + (hasActions ? 44f : 0f)
                         + (hasExtra ? PrintedControlGap + extraHeight : 0f);
            height = Mathf.Clamp(height, 52f, MaxPrintedControlHeight);

            UIFactory.SetSize(_panel, 460f, height);
            var printedBottom = PrintedY - (PrintedHeight / 2f);
            var trayTop = printedBottom - PrintedControlGap;
            _panel.anchoredPosition = new Vector2(0f, trayTop - (height / 2f));
        }

        /// <summary>
        /// Refreshes the preview in place for the card already showing - used
        /// after a click inside its own extra content (a colour picked, a target
        /// chosen) changes what that content should say next.
        /// </summary>
        public static void RefreshIfShowing(BoardCardView card)
        {
            if (_instance != null && card != null && IsOpen)
            {
                _instance.Display(card);
            }
        }
    }
}
