using Indoctrination.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// One player's whole presence on the board, reduced to a single line: name,
    /// health, and followers. The compounds are the main event, so this is kept
    /// to the smallest strip that still says who is winning and who is in
    /// trouble - not a panel players read, just one they glance at.
    /// </summary>
    public class StatBar : MonoBehaviour
    {
        /// <summary>The whole bar, one line tall.</summary>
        public const float BarHeight = 34f;

        /// <summary>The die face shown beside a player's name.</summary>
        private const float DieSize = 22f;

        /// <summary>Width of the health track at full health - the scale block is measured against.</summary>
        private const float HealthTrackWidth = 130f;

        /// <summary>Width of the follower track at the win line.</summary>
        private const float FollowerTrackWidth = 100f;

        private Text _nameText;
        private Image _healthFill;
        private Text _healthText;
        private RectTransform _blockTrack;
        private Image _blockFill;
        private Image _followerFill;
        private Text _followerText;
        private RectTransform _dieBox;
        private Text _dieText;
        private RectTransform _privateDice;

        /// <summary>Health-bar pixels per point, so Block can extend it by exactly as much as it is worth.</summary>
        private float _pointWidth;

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
            UIFactory.SetSize(rect, HealthTrackWidth + FollowerTrackWidth + 150, BarHeight);

            // Pins this bar's own size when it sits inside another layout group
            // (the top row of opponents), the same way BoardCardView pins itself.
            var pin = gameObject.AddComponent<LayoutElement>();
            pin.minHeight = pin.preferredHeight = BarHeight;
            pin.flexibleWidth = 0;

            var layout = UIFactory.HorizontalLayout(rect, 6, new RectOffset(8, 8, 4, 4));
            layout.childAlignment = TextAnchor.MiddleLeft;

            _nameText = UIFactory.Label("Name", rect, "", 14, TextAnchor.MiddleLeft);
            _nameText.fontStyle = FontStyle.Bold;
            var namePin = _nameText.gameObject.AddComponent<LayoutElement>();
            namePin.minWidth = 60;
            namePin.preferredWidth = 90;
            namePin.flexibleWidth = 0;

            _dieBox = UIFactory.Panel("Die", rect, UITheme.Bone);
            UIFactory.SetSize(_dieBox, DieSize, DieSize);
            var diePin = _dieBox.gameObject.AddComponent<LayoutElement>();
            diePin.minWidth = diePin.preferredWidth = DieSize;
            diePin.minHeight = diePin.preferredHeight = DieSize;

            _dieText = UIFactory.Label("Die Face", _dieBox, "", 14, TextAnchor.MiddleCenter,
                UITheme.Void);
            _dieText.fontStyle = FontStyle.Bold;
            UIFactory.Stretch(_dieText.rectTransform);

            // Dice only this player's units answer to. Standardized Uniforms
            // grants one, and without somewhere to show it the card reads as
            // doing nothing at all - its units wake on a number that is not on
            // the table anywhere.
            _privateDice = UIFactory.Group("Private Dice", rect);
            var privatePin = _privateDice.gameObject.AddComponent<LayoutElement>();
            privatePin.minHeight = privatePin.preferredHeight = DieSize;
            privatePin.flexibleWidth = 0;
            var privateLayout = UIFactory.HorizontalLayout(_privateDice, 3, new RectOffset(0, 0, 0, 0));
            privateLayout.childAlignment = TextAnchor.MiddleLeft;
            UIFactory.FitToContent(_privateDice);

            // Health and Block share one visual run: Block is armour standing in
            // front of health rather than a separate pool, so it reads as the
            // health bar simply continuing on past its usual end.
            // Health and Block share a row with no spacing between them, so the
            // green butts directly onto the end of the red and reads as the same
            // bar running further. Sitting in the parent row it inherited that
            // row's gap and looked like a separate box parked nearby.
            var healthRow = UIFactory.Group("Health Row", rect);
            var healthLayout = UIFactory.HorizontalLayout(healthRow, 0, new RectOffset(0, 0, 0, 0));
            healthLayout.childAlignment = TextAnchor.MiddleLeft;
            UIFactory.FitToContent(healthRow);
            var healthRowPin = healthRow.gameObject.AddComponent<LayoutElement>();
            healthRowPin.minHeight = healthRowPin.preferredHeight = 18;

            (_healthFill, _healthText) = MakeBar(
                "Health", new Color(0.800f, 0.247f, 0.318f), healthRow, HealthTrackWidth);
            _pointWidth = HealthTrackWidth / GameSettings.MaxHealth;

            _blockTrack = UIFactory.Panel("Block", healthRow, new Color(0.247f, 0.722f, 0.502f, 0.95f));
            var blockPin = _blockTrack.gameObject.AddComponent<LayoutElement>();
            blockPin.minHeight = blockPin.preferredHeight = 18;
            blockPin.minWidth = blockPin.preferredWidth = 0;
            blockPin.flexibleWidth = 0;
            _blockFill = _blockTrack.GetComponent<Image>();
            _blockTrack.gameObject.SetActive(false);

            (_followerFill, _followerText) = MakeBar(
                "Followers", new Color(0.290f, 0.561f, 0.902f), rect, FollowerTrackWidth);
        }

        /// <summary>
        /// A real bar: a dark track with a coloured fill that grows and shrinks
        /// across it, and the number laid over the top so the exact value is
        /// readable without counting pixels.
        /// </summary>
        private (Image Fill, Text Label) MakeBar(string name, Color color, Transform parent, float width)
        {
            var track = UIFactory.Panel(name, parent, new Color(
                UITheme.Void.r, UITheme.Void.g, UITheme.Void.b, 0.76f));

            var pin = track.gameObject.AddComponent<LayoutElement>();
            pin.minWidth = pin.preferredWidth = width;
            pin.minHeight = pin.preferredHeight = 18;
            pin.flexibleWidth = 0;

            var fill = UIFactory.FillBar($"{name} Fill", track, color);
            UIFactory.Stretch(fill.rectTransform);
            fill.rectTransform.offsetMin = new Vector2(1f, 1f);
            fill.rectTransform.offsetMax = new Vector2(-1f, -1f);

            var label = UIFactory.Label($"{name} Label", track, "", 11, TextAnchor.MiddleCenter);
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

        /// <summary>The health bar, so a wound landing can knock it.</summary>
        public RectTransform HealthBar => _healthFill == null ? null : _healthFill.rectTransform;

        /// <summary>
        /// Fills the strip in. <paramref name="revealDice"/> is false while the
        /// dice are still rolling: the number is the answer to the throw, and
        /// printing it beside a player's name while their die is still in the
        /// air gives it away before the die does.
        /// </summary>
        public void Populate(PlayerView player, bool isViewer, bool revealDice = true)
        {
            _nameText.text = player.isAlive
                ? $"{player.name}{(isViewer ? " (you)" : "")}{(player.offeringDraw ? "  ✋" : "")}"
                : $"{player.name} ({(player.hasResigned ? "resigned" : "out")})";
            _nameText.color = player.isAlive ? UITheme.Bone : UITheme.BoneDim;

            // Blank until they have actually rolled, so an old face never lingers
            // as though this turn's roll had already happened. Once they have,
            // the box appears straight away but holds a question mark until the
            // dice have finished rolling - "rolling" and "has not rolled" are
            // different things, and the strip should say which.
            _dieBox.gameObject.SetActive(player.hasRolled && player.primaryDie > 0);
            _dieText.text = revealDice ? player.primaryDie.ToString() : "?";
            _dieText.color = revealDice ? UITheme.Void : new Color(0.35f, 0.35f, 0.38f);

            BuildPrivateDice(player, revealDice);

            _healthText.text = $"{player.health}/{GameSettings.MaxHealth}";
            BoardEffects.Instance.FillTo(_healthFill, (float)player.health / GameSettings.MaxHealth);

            // Directly appended to the end of the health bar, to the same
            // per-point scale, rather than a separate boxed counter - Block reads
            // as the health bar simply running on a little further.
            var blockWidth = player.block * _pointWidth;
            var blockPin = _blockTrack.gameObject.GetComponent<LayoutElement>();
            blockPin.minWidth = blockPin.preferredWidth = blockWidth;
            _blockTrack.gameObject.SetActive(player.block > 0);
            if (player.block > 0)
            {
                BoardEffects.Instance.Pop(_blockTrack);
            }

            _followerText.text = $"{player.followers}/{GameSettings.FollowersToWin}";
            BoardEffects.Instance.FillTo(
                _followerFill, (float)player.followers / GameSettings.FollowersToWin);
        }

        /// <summary>
        /// Draws this player's private dice beside their shared one, accented so
        /// the difference is visible at a glance: these numbers wake only their
        /// units, and reading the board depends on knowing that.
        /// </summary>
        private void BuildPrivateDice(PlayerView player, bool revealDice)
        {
            var dice = player.privateDice ?? System.Array.Empty<int>();
            var show = player.hasRolled && dice.Length > 0;

            _privateDice.gameObject.SetActive(show);
            if (!show)
            {
                return;
            }

            UIFactory.DestroyChildren(_privateDice);

            foreach (var face in dice)
            {
                var box = UIFactory.Panel($"Private Die {face}", _privateDice, UITheme.Signal);
                UIFactory.SetSize(box, DieSize, DieSize);
                var pin = box.gameObject.AddComponent<LayoutElement>();
                pin.minWidth = pin.preferredWidth = DieSize;
                pin.minHeight = pin.preferredHeight = DieSize;

                var text = UIFactory.Label(
                    "Face", box, revealDice ? face.ToString() : "?", 14,
                    TextAnchor.MiddleCenter, UITheme.Void);
                text.fontStyle = FontStyle.Bold;
                UIFactory.Stretch(text.rectTransform);
            }
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
