using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Small building blocks for assembling uGUI by hand in code. The whole board
    /// is built this way rather than from prefabs, so the layout lives in source
    /// control as readable C# instead of scene/prefab YAML nobody can diff.
    ///
    /// Widgets use a shared, asset-free ritual theme and resolve an installed
    /// serif face with a built-in fallback, so none of this depends on an asset
    /// import step inside the Editor. Sprites that are genuinely needed -
    /// discs, the glow, the solid rect a fill bar requires - are generated at
    /// runtime by BoardArt.
    /// </summary>
    public static class UIFactory
    {
        public static Font DefaultFont => UITheme.BodyFont;

        /// <summary>
        /// How far in front of the camera the board is drawn. Anything nearer
        /// than this renders over the board; anything further renders behind it.
        /// </summary>
        public const float CanvasPlaneDistance = 50f;

        private static Canvas _canvas;

        /// <summary>
        /// The camera the board is drawn through, or null while it is an
        /// overlay. Screen-point maths against the board must pass this.
        /// </summary>
        public static Camera UiCamera =>
            _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? _canvas.worldCamera
                : null;

        /// <summary>The Canvas plus the EventSystem it needs to receive clicks under the new Input System.</summary>
        public static Canvas CreateCanvas(string name)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();

            // Rendered through the camera rather than as an overlay, which is
            // what lets real 3D objects appear in front of the board. An overlay
            // canvas is composited after every camera in the game, so nothing in
            // the scene can be drawn over one by any means - dice included.
            //
            // The consequence to remember: screen-point maths against this
            // canvas needs the camera passed in. Anywhere that used to pass null
            // now uses UiCamera, and getting that wrong shows up as clicks and
            // hover landing in the wrong place rather than as an error.
            if (Camera.main != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = Camera.main;
                canvas.planeDistance = CanvasPlaneDistance;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }

            _canvas = canvas;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            // Treat the reference resolution like a responsive CSS viewport.
            // Balancing width and height keeps controls readable in both the main
            // Game view and Unity Multiplayer Player windows; the board itself
            // now flexes and scrolls instead of assuming a 1920-wide desktop.
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var events = new GameObject("EventSystem", typeof(EventSystem));
                // The project's Active Input Handling is set to the new Input
                // System package, which the legacy StandaloneInputModule cannot
                // drive - this is the module built for it.
                events.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            }

            return canvas;
        }

        public static RectTransform Panel(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return rect;
        }

        /// <summary>A RectTransform with no visual of its own, purely for grouping/layout.</summary>
        public static RectTransform Group(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        public static Text Label(
            string name, Transform parent, string text, int fontSize = 16,
            TextAnchor anchor = TextAnchor.MiddleLeft, Color? color = null, bool wrap = true)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.GetComponent<RectTransform>().SetParent(parent, false);

            var label = go.GetComponent<Text>();
            label.font = fontSize >= 20 ? UITheme.TitleFont : DefaultFont;
            label.fontSize = fontSize;
            label.alignment = anchor;
            label.color = color ?? UITheme.Bone;
            label.text = text;
            label.lineSpacing = 1.05f;
            label.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        public static Button ButtonWithLabel(
            string name, Transform parent, string text, Action onClick,
            Color? background = null, float width = 160, float height = 32)
        {
            var rect = Panel(name, parent, background ?? UITheme.Button);
            SetSize(rect, width, height);

            var pin = rect.gameObject.AddComponent<LayoutElement>();
            pin.preferredWidth = width;
            pin.preferredHeight = height;
            pin.minWidth = width;
            pin.minHeight = height;

            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = rect.GetComponent<Image>();
            UITheme.StyleButton(button);
            UITheme.Frame(rect.GetComponent<Image>(), 1f, UITheme.Border);
            if (onClick != null)
            {
                button.onClick.AddListener(() => onClick());
            }

            var label = Label($"{name} Text", rect, text, 15, TextAnchor.MiddleCenter, UITheme.Bone);
            label.fontStyle = FontStyle.Bold;
            Stretch(Child(rect, $"{name} Text"));
            return button;
        }

        /// <summary>A single-line text field, e.g. for the host address or an amount.</summary>
        public static InputField TextInput(string name, Transform parent, string startingValue)
        {
            var rect = Panel(name, parent, UITheme.SurfaceSoft);
            UITheme.Frame(rect.GetComponent<Image>(), 1f, UITheme.Border);
            var field = rect.gameObject.AddComponent<InputField>();

            var text = Label($"{name} Text", rect, startingValue, 15, TextAnchor.MiddleLeft);
            var textRect = text.rectTransform;
            Stretch(textRect);
            textRect.offsetMin = new Vector2(6, 2);
            textRect.offsetMax = new Vector2(-6, -2);

            field.textComponent = text;
            field.text = startingValue;
            field.selectionColor = new Color(
                UITheme.Signal.r, UITheme.Signal.g, UITheme.Signal.b, 0.42f);
            return field;
        }

        /// <summary>
        /// A bar that genuinely fills and empties from the left.
        ///
        /// The sprite is not decoration - Unity's Image ignores Filled entirely
        /// when it has none, drawing a plain full quad and quietly discarding
        /// fillAmount. Without it this is a coloured rectangle wearing the name
        /// of a progress bar.
        /// </summary>
        public static Image FillBar(string name, Transform parent, Color color)
        {
            var rect = Panel(name, parent, color);
            var image = rect.GetComponent<Image>();
            image.sprite = BoardArt.Solid;
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = (int)Image.OriginHorizontal.Left;
            image.fillAmount = 1f;
            image.raycastTarget = false;
            return image;
        }

        public static HorizontalLayoutGroup HorizontalLayout(
            RectTransform rect, int spacing = 6, RectOffset padding = null,
            bool controlWidth = false, bool controlHeight = true)
        {
            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = padding ?? new RectOffset(6, 6, 6, 6);
            layout.childControlWidth = controlWidth;
            layout.childControlHeight = controlHeight;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return layout;
        }

        public static VerticalLayoutGroup VerticalLayout(
            RectTransform rect, int spacing = 6, RectOffset padding = null,
            bool controlWidth = true, bool controlHeight = false)
        {
            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = padding ?? new RectOffset(6, 6, 6, 6);
            layout.childControlWidth = controlWidth;
            layout.childControlHeight = controlHeight;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            return layout;
        }

        public static ContentSizeFitter FitToContent(
            RectTransform rect,
            ContentSizeFitter.FitMode horizontal = ContentSizeFitter.FitMode.PreferredSize,
            ContentSizeFitter.FitMode vertical = ContentSizeFitter.FitMode.Unconstrained)
        {
            var fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = horizontal;
            fitter.verticalFit = vertical;
            return fitter;
        }

        /// <summary>
        /// A horizontally-scrolling row of a fixed height: a masked viewport with
        /// a content strip inside it that grows to fit whatever is added as its
        /// children. Returns the content strip - add children to that.
        /// </summary>
        /// <summary>
        /// Vertical padding inside a horizontal strip's content. Public because
        /// the strip has to be built tall enough to hold its cards plus this, or
        /// the cards overflow and whatever sits at the top of them is clipped off.
        /// </summary>
        public const float ScrollContentPadding = 4f;

        // Cards swell around their centre on hover. The horizontal inset has to
        // clear half of that growth or the scroll mask shaves the first card's
        // left edge precisely when the player points at it.
        private const float ScrollHorizontalPadding = 8f;

        public static RectTransform HorizontalScroll(string name, Transform parent, float height)
        {
            var root = Panel(name, parent, Color.clear);
            var rootPin = root.gameObject.AddComponent<LayoutElement>();
            rootPin.preferredHeight = height;
            rootPin.flexibleWidth = 1;
            rootPin.minHeight = height;

            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            var viewport = Panel($"{name} Viewport", root, Color.clear);
            viewport.gameObject.AddComponent<RectMask2D>();
            Stretch(viewport);

            var content = Group($"{name} Content", viewport);
            content.anchorMin = new Vector2(0, 0);
            content.anchorMax = new Vector2(0, 1);
            content.pivot = new Vector2(0, 0.5f);
            // Changing a fresh RectTransform from its centred default pivot to
            // a left pivot preserves its old visual position, which leaves the
            // content anchoredPosition at -50 and clips the first child. The
            // scroll strip itself must begin exactly at the viewport's left edge.
            content.anchoredPosition = Vector2.zero;

            var verticalPad = (int)ScrollContentPadding;
            var horizontalPad = (int)ScrollHorizontalPadding;

            // childControlHeight is deliberately off: the cards carry their own
            // pinned height, and letting the strip resize them squeezed them
            // until they overflowed the viewport that clips them.
            var layout = HorizontalLayout(
                content, 8,
                new RectOffset(horizontalPad, horizontalPad, verticalPad, verticalPad),
                controlHeight: false);
            layout.childAlignment = TextAnchor.MiddleLeft;

            FitToContent(content);

            scroll.viewport = viewport;
            scroll.content = content;
            return content;
        }

        /// <summary>Finds a direct child by name - used right after creating one, to size it.</summary>
        public static RectTransform Child(Transform parent, string name) =>
            (RectTransform)parent.Find(name);

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        public static void SetSize(RectTransform rect, float width, float height)
        {
            rect.sizeDelta = new Vector2(width, height);
        }

        /// <summary>
        /// Clears a container's children. They are unparented before being
        /// destroyed because Destroy only takes effect at the end of the frame -
        /// leaving them attached would let the old contents and the freshly-built
        /// replacement both sit in the layout for a frame.
        /// </summary>
        public static void DestroyChildren(Transform parent)
        {
            for (var i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i).gameObject;
                child.transform.SetParent(null, false);

                // Destroy only takes effect at the end of the frame, and outside
                // play mode there is no frame - the old widgets would pile up
                // forever. Detaching first keeps the layout correct either way.
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(child);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(child);
                }
            }
        }
    }
}
