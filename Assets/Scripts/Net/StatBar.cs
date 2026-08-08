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
        public const float BarHeight = 98f;

        private Text _nameText;
        private Image _healthFill;
        private Text _healthText;
        private Image _followerFill;
        private Text _followerText;
        private Text _resourceText;

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

            _nameText = UIFactory.Label("Name", transform, "", 15, TextAnchor.MiddleLeft);
            _nameText.fontStyle = FontStyle.Bold;
            SizeRow(_nameText.rectTransform, 18);

            (_healthFill, _healthText) = MakeBar("Health", new Color(0.8f, 0.25f, 0.25f));
            (_followerFill, _followerText) = MakeBar("Followers", new Color(0.75f, 0.6f, 0.2f));

            _resourceText = UIFactory.Label("Resources", transform, "", 13, TextAnchor.MiddleLeft);
            // The colour tags are markup, so this row has to render rich text.
            _resourceText.supportRichText = true;
            SizeRow(_resourceText.rectTransform, 17);
        }

        private (Image Fill, Text Label) MakeBar(string name, Color color)
        {
            var track = UIFactory.Panel(name, transform, new Color(1, 1, 1, 0.12f));
            SizeRow(track, 18);

            var fill = UIFactory.FillBar($"{name} Fill", track, color);
            UIFactory.Stretch(fill.rectTransform);

            var label = UIFactory.Label($"{name} Label", track, "", 12, TextAnchor.MiddleCenter);
            UIFactory.Stretch(label.rectTransform);

            return (fill, label);
        }

        private static string Swatch(string letter, int amount, string hex) =>
            $"<color=#{hex}><b>{letter}</b></color> {amount}";

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

            var healthFraction = Mathf.Clamp01((float)player.health / GameSettings.StartingHealth);
            _healthFill.fillAmount = healthFraction;
            _healthText.text = $"{player.health} HP";

            var followerFraction = Mathf.Clamp01((float)player.followers / GameSettings.FollowersToWin);
            _followerFill.fillAmount = followerFraction;
            _followerText.text = $"{player.followers}/{GameSettings.FollowersToWin} followers";

            // Coloured so a glance tells you which pile is short, without having
            // to read the letters.
            _resourceText.text =
                $"{Swatch("R", player.red, "e05a5a")}  {Swatch("G", player.green, "4fae55")}  " +
                $"{Swatch("B", player.blue, "5588e0")}  {Swatch("Y", player.yellow, "d1a83d")}";
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
