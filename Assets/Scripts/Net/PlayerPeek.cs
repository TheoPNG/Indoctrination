using System.Linq;
using Indoctrination.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Everything an opponent has, on hover: their resources, their state, and
    /// their compound laid out where you are already looking.
    ///
    /// Their compound is on the battlefield too, but the battlefield scrolls and
    /// a four-player table pushes rows off the bottom, so checking what somebody
    /// is holding meant hunting for their row. Their resources were nowhere at
    /// all - the resource HUD only ever shows your own - even though the server
    /// has always sent them, because they are public information.
    ///
    /// Reads only what is already in the view. Nothing here can leak a hand: the
    /// server never puts another player's cards in your copy in the first place,
    /// so the most this can show is the number of them.
    /// </summary>
    public class PlayerPeek : MonoBehaviour
    {
        /// <summary>How wide a card is drawn in the strip, at most.</summary>
        private const float CardWidth = 74f;

        /// <summary>Card height per unit of width, from the card's own proportions.</summary>
        private const float CardAspect = BoardCardView.Height / BoardCardView.Width;

        private const float PanelWidth = 470f;

        private RectTransform _panel;
        private Text _nameText;
        private Text _stateText;
        private RectTransform _resourceRow;
        private RectTransform _cardRow;
        private GridLayoutGroup _cardGrid;
        private Text _emptyText;

        /// <summary>Which player is being shown, or -1 for nobody.</summary>
        public int ShowingFor { get; private set; } = -1;

        public static PlayerPeek CreateOn(Transform canvas)
        {
            var go = new GameObject("Player Peek", typeof(RectTransform));
            go.transform.SetParent(canvas, false);

            var peek = go.AddComponent<PlayerPeek>();
            peek.Build();
            return peek;
        }

        private void Build()
        {
            var root = (RectTransform)transform;
            UIFactory.Stretch(root);

            // Never takes a click. It appears under the pointer by definition, so
            // anything it intercepted would be a click meant for the board.
            var group = gameObject.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            _panel = UIFactory.Panel("Peek Panel", root, UITheme.SurfaceRaised);
            UITheme.Frame(_panel.GetComponent<Image>(), 1f, UITheme.Border);
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 1f);
            UIFactory.SetSize(_panel, PanelWidth, 10f);

            var layout = UIFactory.VerticalLayout(
                _panel, 6, new RectOffset(12, 12, 10, 10), controlHeight: true);
            layout.childForceExpandWidth = true;
            UIFactory.FitToContent(
                _panel,
                ContentSizeFitter.FitMode.Unconstrained,
                ContentSizeFitter.FitMode.PreferredSize);

            _nameText = UIFactory.Label("Peek Name", _panel, "", 15, TextAnchor.MiddleLeft, UITheme.Signal);
            _nameText.fontStyle = FontStyle.Bold;
            Row(_nameText.rectTransform, 20f);

            _resourceRow = UIFactory.Group("Peek Resources", _panel);
            Row(_resourceRow, 26f);
            var resourceLayout = UIFactory.HorizontalLayout(_resourceRow, 8, new RectOffset(0, 0, 0, 0));
            resourceLayout.childAlignment = TextAnchor.MiddleLeft;

            _stateText = UIFactory.Label("Peek State", _panel, "", 12, TextAnchor.MiddleLeft, UITheme.BoneDim);
            Row(_stateText.rectTransform, 18f);

            // A grid rather than a row, because a card is laid out at full size
            // and then scaled down - a layout group would still reserve its
            // unscaled width, and the strip would be four times too wide.
            _cardRow = UIFactory.Group("Peek Cards", _panel);
            _cardGrid = _cardRow.gameObject.AddComponent<GridLayoutGroup>();
            _cardGrid.spacing = new Vector2(5f, 5f);
            _cardGrid.childAlignment = TextAnchor.UpperLeft;
            _cardGrid.cellSize = new Vector2(CardWidth, CardWidth * CardAspect);
            Row(_cardRow, (CardWidth * CardAspect) + 4f);

            _emptyText = UIFactory.Label(
                "Peek Empty", _panel, "Nothing in play", 12, TextAnchor.MiddleLeft, UITheme.BoneDim);
            Row(_emptyText.rectTransform, 18f);

            gameObject.SetActive(false);
        }

        private static void Row(RectTransform rect, float height)
        {
            var element = rect.gameObject.GetComponent<LayoutElement>()
                          ?? rect.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = height;
            element.flexibleWidth = 1f;
        }

        /// <summary>
        /// Shows one player, hung under the strip that was hovered. Rebuilds only
        /// when the player or their board has actually changed, so holding the
        /// pointer still does not rebuild a card strip sixty times a second.
        /// </summary>
        public void Show(PlayerView player, RectTransform anchor)
        {
            if (player == null)
            {
                Hide();
                return;
            }

            var changed = ShowingFor != player.playerId;
            ShowingFor = player.playerId;

            gameObject.SetActive(true);
            transform.SetAsLastSibling();

            _nameText.text = player.isAlive
                ? player.name
                : $"{player.name}  ({(player.hasResigned ? "resigned" : "out")})";

            BuildResources(player);

            var state = $"{player.health}/{GameSettings.MaxHealth} health"
                        + (player.block > 0 ? $"  +{player.block} block" : "")
                        + $"    {player.followers}/{GameSettings.FollowersToWin} followers"
                        + $"    {player.handCount} in hand";
            _stateText.text = state;

            var signature = string.Join(",", player.compound.Select(c => c.instanceId));
            if (changed || signature != _cardSignature)
            {
                _cardSignature = signature;
                BuildCards(player);
            }

            Place(anchor);
        }

        private string _cardSignature = "";

        private void BuildResources(PlayerView player)
        {
            var counts = new (ResourceColor Colour, int Count)[]
            {
                (ResourceColor.Red, player.red),
                (ResourceColor.Green, player.green),
                (ResourceColor.Blue, player.blue),
                (ResourceColor.Yellow, player.yellow)
            };

            // Rebuilt every time rather than kept: four chips is nothing, and a
            // cached row is one more thing that can be left showing the wrong
            // player's numbers.
            UIFactory.DestroyChildren(_resourceRow);

            foreach (var (colour, count) in counts)
            {
                var chip = UIFactory.Panel($"Peek {colour}", _resourceRow, BoardArt.ColorOf(colour));
                UIFactory.SetSize(chip, 40f, 22f);
                var pin = chip.gameObject.AddComponent<LayoutElement>();
                pin.minWidth = pin.preferredWidth = 40f;
                pin.minHeight = pin.preferredHeight = 22f;
                pin.flexibleWidth = 0f;

                var label = UIFactory.Label(
                    "Count", chip, count.ToString(), 13, TextAnchor.MiddleCenter, UITheme.Void);
                label.fontStyle = FontStyle.Bold;
                UIFactory.Stretch(label.rectTransform);
            }
        }

        private void BuildCards(PlayerView player)
        {
            UIFactory.DestroyChildren(_cardRow);

            var cards = player.compound;
            _cardRow.gameObject.SetActive(cards.Length > 0);
            _emptyText.gameObject.SetActive(cards.Length == 0);

            if (cards.Length == 0)
            {
                return;
            }

            // Narrower as more of them arrive, so a full compound still fits the
            // panel rather than running off the side of it.
            var width = Mathf.Min(
                CardWidth, (PanelWidth - 24f - (5f * (cards.Length - 1))) / cards.Length);

            _cardGrid.cellSize = new Vector2(width, width * CardAspect);

            foreach (var card in cards)
            {
                var view = BoardCardView.Create(_cardRow);
                view.Populate(card, null, null);
                view.ScaleTo(width);
                view.SetPreviewEnabled(false);
            }

            Row(_cardRow, (width * CardAspect) + 4f);
        }

        /// <summary>
        /// Hangs the panel under whatever was hovered, kept inside the screen.
        /// A panel that runs off the edge is worse than no panel: the player it
        /// belongs to is the one whose strip is at the edge.
        /// </summary>
        private void Place(RectTransform anchor)
        {
            var root = (RectTransform)transform;
            var local = root.InverseTransformPoint(anchor.position);

            LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);

            var halfWidth = _panel.rect.width * 0.5f;
            var limit = (root.rect.width * 0.5f) - halfWidth - 8f;

            var top = local.y - (StatBar.BarHeight * 0.5f) - 6f;
            var floor = (-root.rect.height * 0.5f) + _panel.rect.height + 8f;

            _panel.anchoredPosition = new Vector2(
                Mathf.Clamp(local.x, -limit, limit),
                Mathf.Max(top, floor));
        }

        public void Hide()
        {
            ShowingFor = -1;
            gameObject.SetActive(false);
        }
    }
}
