using System;
using System.Collections.Generic;
using Indoctrination.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Your own resources, always on screen: four circles down the left edge of
    /// the board. The rest of the time they are just a running count of what you
    /// are holding; the moment there is something to take, they light up and
    /// answer clicks. No panel, no popup - the pick happens right where the
    /// count already lives.
    /// </summary>
    public class ResourceHud : MonoBehaviour
    {
        private const float Diameter = 46f;

        private readonly Dictionary<ResourceColor, Text> _counts = new();
        private readonly Dictionary<ResourceColor, Image> _discs = new();
        private readonly Dictionary<ResourceColor, Button> _buttons = new();
        private readonly Dictionary<ResourceColor, RectTransform> _roots = new();

        private bool _built;

        private void Awake() => Build();

        /// <summary>
        /// Builds the widget, once. Called from the factory rather than left to
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
            var layout = UIFactory.VerticalLayout(rect, 14, new RectOffset(4, 4, 8, 8));
            layout.childAlignment = TextAnchor.MiddleCenter;

            foreach (var color in BoardArt.Colors)
            {
                var root = UIFactory.Group($"{color} Slot", rect);
                UIFactory.SetSize(root, Diameter, Diameter);
                var pin = root.gameObject.AddComponent<LayoutElement>();
                pin.minWidth = pin.preferredWidth = Diameter;
                pin.minHeight = pin.preferredHeight = Diameter;

                var disc = root.gameObject.AddComponent<Image>();
                disc.sprite = BoardArt.Disc;
                disc.color = BoardArt.ColorOf(color);

                var button = root.gameObject.AddComponent<Button>();
                button.targetGraphic = disc;
                button.interactable = false;

                var label = UIFactory.Label("Count", root, "0", 18, TextAnchor.MiddleCenter, Color.white);
                label.fontStyle = FontStyle.Bold;
                UIFactory.Stretch(label.rectTransform);
                var outline = label.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
                outline.effectDistance = new Vector2(1f, -1f);

                _roots[color] = root;
                _discs[color] = disc;
                _counts[color] = label;
                _buttons[color] = button;
            }
        }

        /// <summary>
        /// Updates every count, and whether the circles are inviting a pick right
        /// now. Pickable only while there are resources left to take this turn -
        /// the moment they light up is the only warning a player needs that
        /// something is waiting on them.
        /// </summary>
        public void Populate(PlayerView you, bool pickable, Action<ResourceColor> onPicked)
        {
            gameObject.SetActive(you != null);
            if (you == null)
            {
                return;
            }

            SetCount(ResourceColor.Red, you.red);
            SetCount(ResourceColor.Green, you.green);
            SetCount(ResourceColor.Blue, you.blue);
            SetCount(ResourceColor.Yellow, you.yellow);

            foreach (var color in BoardArt.Colors)
            {
                var button = _buttons[color];
                button.onClick.RemoveAllListeners();
                button.interactable = pickable;

                if (pickable)
                {
                    var chosen = color;
                    button.onClick.AddListener(() => onPicked(chosen));
                }

                // Size, not colour. A colour pulse on a coloured disc is nearly
                // invisible; growing and shrinking is what reads across the
                // board as "these are waiting for you".
                BoardEffects.Instance.SetPulsing(_discs[color], pickable);
                BoardEffects.Instance.SetBreathing(_roots[color], pickable);
            }
        }

        private void SetCount(ResourceColor color, int value) => _counts[color].text = value.ToString();

        /// <summary>Where this colour's circle sits on screen, for a picked resource to fly into.</summary>
        public Vector3 PipPosition(ResourceColor color) =>
            _roots.TryGetValue(color, out var root) ? root.position : transform.position;

        public RectTransform Pip(ResourceColor color) => _roots.GetValueOrDefault(color);

        /// <summary>
        /// Bumps a circle's count straight away, before the server has replied.
        /// Picking a resource should look instant; the authoritative number
        /// arrives a moment later and overwrites this either way, so the worst a
        /// wrong guess can do is be corrected.
        /// </summary>
        public void ShowResourceGain(ResourceColor color)
        {
            if (_counts.TryGetValue(color, out var label) && int.TryParse(label.text, out var current))
            {
                label.text = (current + 1).ToString();
            }
        }

        public static ResourceHud Create(Transform parent)
        {
            var go = new GameObject("Resource HUD", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var hud = go.AddComponent<ResourceHud>();
            hud.Build();
            return hud;
        }
    }
}
