using System.Collections.Generic;
using Indoctrination.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// One player's strip at the top or bottom of the board: name, a health bar,
    /// a follower bar, and their resources. The bars fill toward zero and the win
    /// line, so a glance across the row tells you who is in trouble and who is
    /// close to winning.
    ///
    /// The resource row matters more than it looks: without it a player has no
    /// way to know what they are holding, and so no way to tell what they can
    /// afford to buy. Resource counts are already public in the game view.
    /// </summary>
    public class StatBar : MonoBehaviour
    {
        /// <summary>Name, health, followers, and the resource row.</summary>
        public const float BarHeight = 112f;

        /// <summary>Size of one resource disc.</summary>
        public const float PipDiameter = 26f;

        /// <summary>Height of the health and follower bars.</summary>
        private const float BarRowHeight = 22f;

        /// <summary>The die face shown beside a player's name.</summary>
        private const float DieSize = 20f;

        private Text _nameText;
        private Image _healthFill;
        private Text _healthText;
        private Image _followerFill;
        private Text _followerText;

        /// <summary>One pip per resource colour, in <see cref="BoardArt.Colors"/> order.</summary>
        private readonly Dictionary<ResourceColor, Text> _resourcePips = new();
        private RectTransform _resourceRow;
        private RectTransform _dieBox;
        private Text _dieText;

        private bool _built;

        private void Awake() => Build();

        /// <summary>
        /// Builds the widget, once. Called from the factory rather than left to
        /// Awake, which the Editor does not run outside play mode - relying on it
        /// made this impossible to build in a test.
        /// </summary>
        private void Build()
        {
            if (_built)
            {
                return;
            }

            _built = true;

            var rect = (RectTransform)transform;
            UIFactory.SetSize(rect, 260, BarHeight);
            gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.35f);

            // Pins this bar's own size when it sits inside another layout group
            // (the top row of opponents), the same way BoardCardView pins itself.
            var pin = gameObject.AddComponent<LayoutElement>();
            pin.minWidth = 260;
            pin.preferredWidth = 260;
            pin.minHeight = BarHeight;
            pin.preferredHeight = BarHeight;

            var layout = UIFactory.VerticalLayout(rect, 3, new RectOffset(10, 10, 6, 6), controlHeight: true);
            layout.childAlignment = TextAnchor.UpperLeft;

            // Name on the left, this turn's die on the right. The die has to be
            // visible for Try Again and Baal to mean anything - you cannot decide
            // to change a roll you were never shown.
            var nameRow = UIFactory.Group("Name Row", transform);
            SizeRow(nameRow, 20);
            var nameLayout = UIFactory.HorizontalLayout(nameRow, 6, new RectOffset(0, 0, 0, 0));
            nameLayout.childAlignment = TextAnchor.MiddleLeft;

            _nameText = UIFactory.Label("Name", nameRow, "", 15, TextAnchor.MiddleLeft);
            _nameText.fontStyle = FontStyle.Bold;
            var nameFlex = _nameText.gameObject.AddComponent<LayoutElement>();
            nameFlex.flexibleWidth = 1;

            _dieBox = UIFactory.Panel("Die", nameRow, new Color(0.95f, 0.95f, 0.98f));
            UIFactory.SetSize(_dieBox, DieSize, DieSize);
            var diePin = _dieBox.gameObject.AddComponent<LayoutElement>();
            diePin.minWidth = diePin.preferredWidth = DieSize;
            diePin.minHeight = diePin.preferredHeight = DieSize;

            _dieText = UIFactory.Label("Die Face", _dieBox, "", 14, TextAnchor.MiddleCenter,
                new Color(0.1f, 0.1f, 0.12f));
            _dieText.fontStyle = FontStyle.Bold;
            UIFactory.Stretch(_dieText.rectTransform);

            (_healthFill, _healthText) = MakeBar("Health", new Color(0.8f, 0.25f, 0.25f));
            (_followerFill, _followerText) = MakeBar("Followers", new Color(0.75f, 0.6f, 0.2f));

            // Coloured discs with the count inside, rather than letters and
            // numbers - the colour is the thing being read, so it should be the
            // thing that is seen.
            _resourceRow = UIFactory.Group("Resources", transform);
            SizeRow(_resourceRow, PipDiameter);
            var pipLayout = UIFactory.HorizontalLayout(_resourceRow, 6, new RectOffset(0, 0, 0, 0));
            pipLayout.childAlignment = TextAnchor.MiddleLeft;

            foreach (var color in BoardArt.Colors)
            {
                _resourcePips[color] = BoardArt.ResourcePip(_resourceRow, color, PipDiameter);
            }
        }

        /// <summary>
        /// A real bar: a dark track with a coloured fill that grows and shrinks
        /// across it, and the number laid over the top so the exact value is
        /// readable without counting pixels.
        /// </summary>
        private (Image Fill, Text Label) MakeBar(string name, Color color)
        {
            var track = UIFactory.Panel(name, transform, new Color(0f, 0f, 0f, 0.55f));
            SizeRow(track, BarRowHeight);

            var fill = UIFactory.FillBar($"{name} Fill", track, color);
            UIFactory.Stretch(fill.rectTransform);
            fill.rectTransform.offsetMin = new Vector2(2f, 2f);
            fill.rectTransform.offsetMax = new Vector2(-2f, -2f);

            var label = UIFactory.Label($"{name} Label", track, "", 13, TextAnchor.MiddleCenter);
            label.fontStyle = FontStyle.Bold;
            UIFactory.Stretch(label.rectTransform);

            var outline = label.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1f, -1f);

            return (fill, label);
        }

        /// <summary>Where the health bar sits on screen, for damage flying into it.</summary>
        public Vector3 HealthBarPosition => _healthFill != null
            ? _healthFill.rectTransform.position
            : transform.position;

        /// <summary>Where a resource pip sits on screen, for pips flying into it.</summary>
        public Vector3 PipPosition(ResourceColor color) =>
            _resourcePips.TryGetValue(color, out var pip) ? pip.rectTransform.position : transform.position;

        private static void SizeRow(RectTransform rect, float height)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.flexibleWidth = 1;
        }

        public void Populate(PlayerView player, bool isViewer)
        {
            _nameText.text = player.isAlive
                ? $"{player.name}{(isViewer ? " (you)" : "")}"
                : $"{player.name} (out)";
            _nameText.color = player.isAlive ? Color.white : new Color(0.6f, 0.6f, 0.6f);

            // Blank until they have actually rolled, so an old face never lingers
            // as though this turn's roll had already happened.
            _dieBox.gameObject.SetActive(player.hasRolled && player.primaryDie > 0);
            _dieText.text = player.primaryDie.ToString();

            // The bars slide to their new value rather than jumping, so damage
            // and recruitment are visible as they happen.
            _healthText.text = $"{player.health} / {GameSettings.MaxHealth} HP";
            BoardEffects.Instance.FillTo(
                _healthFill, (float)player.health / GameSettings.MaxHealth);

            _followerText.text = $"{player.followers}/{GameSettings.FollowersToWin} followers";
            BoardEffects.Instance.FillTo(
                _followerFill, (float)player.followers / GameSettings.FollowersToWin);

            _resourcePips[ResourceColor.Red].text = player.red.ToString();
            _resourcePips[ResourceColor.Green].text = player.green.ToString();
            _resourcePips[ResourceColor.Blue].text = player.blue.ToString();
            _resourcePips[ResourceColor.Yellow].text = player.yellow.ToString();
        }

        public static StatBar Create(Transform parent)
        {
            // RectTransform up front: AddComponent<StatBar> runs Awake straight
            // away, and the layout code there needs it to already be there.
            var go = new GameObject("StatBar", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var bar = go.AddComponent<StatBar>();
            bar.Build();
            return bar;
        }
    }
}
