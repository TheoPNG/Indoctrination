using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.Net
{
    /// <summary>
    /// Presents authoritative Unit completions one at a time. It never decides
    /// what activated or changes a stat; it merely compares consecutive server
    /// views and gives each already-decided change enough room to be understood.
    /// </summary>
    public sealed class ActivationStage : MonoBehaviour
    {
        private sealed class PlayerSnapshot
        {
            public int Id;
            public string Name;
            public int Health;
            public int Followers;
            public int Block;
            public bool Alive;
        }

        private sealed class Snapshot
        {
            public readonly Dictionary<int, PlayerSnapshot> Players = new();
            public readonly Dictionary<int, CardView> Cards = new();
        }

        private sealed class Playback
        {
            public ActivationView Activation;
            public Snapshot Before;
            public Snapshot After;
        }

        private sealed class HudRow
        {
            public RectTransform Root;
            public Image Health;
            public Image Followers;
            public Text HealthText;
            public Text FollowersText;
            public Text BlockText;
        }

        private RectTransform _root;
        private RectTransform _hud;
        private RectTransform _cardCell;
        private RectTransform _cardRect;
        private RectTransform _chipRow;
        private RectTransform _choiceRow;
        private Text _ownerLabel;
        private Text _detailLabel;
        private CanvasGroup _group;

        /// <summary>
        /// Builds the menu for a question a card is asking mid-activation. The
        /// stage owns *when* the question is put - before that card's animation,
        /// never during it - but not what answering it means, which stays with
        /// the board and its RPCs.
        /// </summary>
        private Action<RectTransform> _choiceBuilder;

        /// <summary>The activation currently waiting on an answer, or null.</summary>
        private ActivationView _choiceActivation;
        private readonly Queue<Playback> _pending = new();
        private readonly Dictionary<int, HudRow> _rows = new();
        private Snapshot _lastSnapshot;
        private int _batch = -1;
        private int _seen;
        private bool _playing;
        private const float StageCardScale = 1.5f;

        public static ActivationStage CreateOn(Transform parent)
        {
            var root = UIFactory.Panel("Activation Stage", parent,
                new Color(0.018f, 0.014f, 0.030f, 0.94f));
            UIFactory.Stretch(root);
            var stage = root.gameObject.AddComponent<ActivationStage>();
            stage.Build(root);
            root.gameObject.SetActive(false);
            return stage;
        }

        private void Build(RectTransform root)
        {
            _root = root;
            _root.GetComponent<Image>().raycastTarget = true;
            _group = root.gameObject.AddComponent<CanvasGroup>();

            _hud = UIFactory.Group("All Player Tracks", root);
            _hud.anchorMin = Vector2.zero;
            _hud.anchorMax = new Vector2(1f, 0f);
            _hud.pivot = new Vector2(0.5f, 0f);
            _hud.offsetMin = new Vector2(22f, 18f);
            _hud.offsetMax = new Vector2(-22f, 154f);
            var hudLayout = UIFactory.HorizontalLayout(
                _hud, 14, new RectOffset(8, 8, 8, 8), controlWidth: true, controlHeight: true);
            hudLayout.childForceExpandWidth = true;
            hudLayout.childForceExpandHeight = true;

            _ownerLabel = UIFactory.Label(
                "Controller", root, "", 28, TextAnchor.MiddleCenter, UITheme.Bone);
            _ownerLabel.fontStyle = FontStyle.Bold;
            _ownerLabel.rectTransform.anchorMin = new Vector2(0.2f, 0.86f);
            _ownerLabel.rectTransform.anchorMax = new Vector2(0.8f, 0.94f);
            _ownerLabel.rectTransform.offsetMin = _ownerLabel.rectTransform.offsetMax = Vector2.zero;

            _cardCell = UIFactory.Group("Locked Card", root);
            _cardCell.anchorMin = _cardCell.anchorMax = new Vector2(0.5f, 0.52f);
            _cardCell.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_cardCell, 270f, 378f);

            _detailLabel = UIFactory.Label(
                "Activation Detail", root, "", 18, TextAnchor.MiddleCenter, UITheme.BoneDim);
            _detailLabel.rectTransform.anchorMin = new Vector2(0.2f, 0.19f);
            _detailLabel.rectTransform.anchorMax = new Vector2(0.8f, 0.25f);
            _detailLabel.rectTransform.offsetMin = _detailLabel.rectTransform.offsetMax = Vector2.zero;

            // Counters sit on the card itself, stacked like chips pushed onto it.
            _chipRow = UIFactory.Group("Counter Chips", _cardCell);
            _chipRow.anchorMin = new Vector2(0f, 1f);
            _chipRow.anchorMax = new Vector2(1f, 1f);
            _chipRow.pivot = new Vector2(0.5f, 1f);
            _chipRow.sizeDelta = new Vector2(0f, ChipSize + 8f);
            _chipRow.anchoredPosition = new Vector2(0f, ChipSize * 0.6f);
            var chipLayout = UIFactory.HorizontalLayout(_chipRow, 6, new RectOffset(0, 0, 0, 0));
            chipLayout.childAlignment = TextAnchor.MiddleCenter;

            // The question a card asks arrives here, directly under it, so the
            // decision is made looking at the card it belongs to.
            _choiceRow = UIFactory.Group("Choice", root);
            _choiceRow.anchorMin = new Vector2(0.12f, 0.24f);
            _choiceRow.anchorMax = new Vector2(0.88f, 0.34f);
            _choiceRow.offsetMin = _choiceRow.offsetMax = Vector2.zero;
            var choiceLayout = UIFactory.HorizontalLayout(_choiceRow, 10, new RectOffset(0, 0, 0, 0));
            choiceLayout.childAlignment = TextAnchor.MiddleCenter;
            _choiceRow.gameObject.SetActive(false);
        }

        private const float ChipSize = 30f;

        /// <summary>Consumes newly completed entries from a server view.</summary>
        public void Present(GameView view, Action<RectTransform> choiceBuilder = null)
        {
            _choiceBuilder = choiceBuilder;
            var finishingLethalActivation = view != null
                                            && view.activationBatch == _batch
                                            && view.activationCompletedCount > _seen;
            if (view == null
                || (view.phase != nameof(TurnPhase.Activation) && !finishingLethalActivation))
            {
                ResetPresentation();
                return;
            }

            var snapshot = Capture(view);
            if (view.activationBatch != _batch)
            {
                _batch = view.activationBatch;
                _seen = 0;
                _pending.Clear();
                _lastSnapshot = snapshot;
            }

            var completed = Mathf.Clamp(view.activationCompletedCount, 0, view.activations.Length);
            if (completed < _seen)
            {
                _seen = 0;
                _pending.Clear();
                _lastSnapshot = snapshot;
            }

            var previousSeen = _seen;
            for (var index = previousSeen; index < completed; index++)
            {
                if (view.activations[index].skipped)
                {
                    continue;
                }

                _pending.Enqueue(new Playback
                {
                    Activation = view.activations[index],
                    Before = _lastSnapshot ?? snapshot,
                    After = snapshot
                });
                _lastSnapshot = snapshot;
            }

            _seen = completed;
            if (completed > previousSeen)
            {
                _lastSnapshot = snapshot;
            }

            // The entry sitting at the completed mark is the one still resolving,
            // so if a card is asking something right now, that is the card asking.
            // Its question is put before its animation, never during: the effect
            // has not finished, so there is nothing to animate yet.
            _choiceActivation = view.hasPendingChoice
                                && view.phase == nameof(TurnPhase.Activation)
                                && completed < view.activations.Length
                ? view.activations[completed]
                : null;

            _choiceSnapshot = snapshot;

            if (!_playing && (_pending.Count > 0 || _choiceActivation != null) && Application.isPlaying)
            {
                // Unity refuses to start a coroutine on an inactive GameObject,
                // and this component lives on the stage root, which is switched
                // off whenever nothing is being presented. Waking it here rather
                // than inside the routine is the difference between the sequence
                // running and an error every time a unit fires.
                _root.gameObject.SetActive(true);
                StartCoroutine(PlayPending());
            }
        }

        private Snapshot _choiceSnapshot;

        private void ResetPresentation()
        {
            _batch = -1;
            _seen = 0;
            _lastSnapshot = null;
            _choiceActivation = null;
            _pending.Clear();

            if (!_playing && _root != null)
            {
                // Cleared as well as hidden. A menu left built behind a hidden
                // stage comes back the next time it is shown, offering the
                // answer to a question that was settled turns ago.
                if (_choiceRow != null)
                {
                    UIFactory.DestroyChildren(_choiceRow);
                    _choiceRow.gameObject.SetActive(false);
                }

                _root.gameObject.SetActive(false);
            }
        }

        private IEnumerator PlayPending()
        {
            _playing = true;
            while (_pending.Count > 0)
            {
                yield return Play(_pending.Dequeue());
            }

            _playing = false;

            // Everything that already happened has now been shown. If the next
            // card is waiting on an answer, the stage stays up holding that card
            // and its menu rather than blinking out and back in.
            if (_choiceActivation != null)
            {
                ShowChoice();
                yield break;
            }

            _choiceRow.gameObject.SetActive(false);
            _root.gameObject.SetActive(false);
        }

        /// <summary>
        /// Holds the asking card on screen with nothing but its options beneath
        /// it. No prompt: the card is right there, and its own text says what it
        /// does far better than a restatement of it would.
        /// </summary>
        private void ShowChoice()
        {
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            _group.alpha = 1f;

            BuildHud(_choiceSnapshot);
            BuildCard(_choiceActivation, _choiceSnapshot);
            _cardRect.localScale = Vector3.one * StageCardScale;

            UIFactory.DestroyChildren(_choiceRow);
            _choiceRow.gameObject.SetActive(true);
            _choiceBuilder?.Invoke(_choiceRow);
        }

        private IEnumerator Play(Playback playback)
        {
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            _group.alpha = 0f;

            // Any question this card asked was answered before we got here, so
            // the menu comes down and the animation has the stage to itself.
            _choiceRow.gameObject.SetActive(false);

            BuildHud(playback.Before);
            BuildCard(playback.Activation, playback.After);
            LayoutRebuilder.ForceRebuildLayoutImmediate(_root);

            _cardRect.localScale = Vector3.one * (StageCardScale * 0.72f);
            yield return Tween(0.18f, t =>
            {
                _group.alpha = Smooth(t);
                _cardRect.localScale = Vector3.one
                                       * (StageCardScale * Mathf.Lerp(0.72f, 1f, Smooth(t)));
            });

            Enum.TryParse(playback.Activation.category, out ActivationCategory category);
            switch (category)
            {
                case ActivationCategory.Damage:
                    yield return Jolt(42f, 0.32f);
                    yield return AnimateStats(playback.Before, playback.After, 0.52f);
                    break;

                case ActivationCategory.Followers:
                    yield return Jolt(-20f, 0.38f);
                    yield return AnimateStats(playback.Before, playback.After, 0.48f);
                    break;

                case ActivationCategory.Health:
                    yield return ShakeCard(0.34f);
                    FlyChangedGlyphs(playback, health: true);
                    yield return AnimateStats(playback.Before, playback.After, 0.58f);
                    break;

                case ActivationCategory.Block:
                    yield return ShakeCard(0.30f);
                    FlyChangedGlyphs(playback, health: false);
                    yield return AnimateStats(playback.Before, playback.After, 0.52f);
                    break;

                default:
                    yield return GrowAndSettle();
                    yield return AnimateStats(playback.Before, playback.After, 0.4f);
                    break;
            }

            yield return new WaitForSeconds(0.18f);
            yield return Tween(0.20f, t =>
            {
                _group.alpha = 1f - Smooth(t);
                _cardRect.localScale = Vector3.one
                                       * (StageCardScale * Mathf.Lerp(1f, 0.78f, Smooth(t)));
            });
        }

        private void BuildCard(ActivationView activation, Snapshot after)
        {
            // The chip row is a permanent child of the cell, so it is detached
            // across the rebuild rather than destroyed along with the old card.
            _chipRow.SetParent(_root, false);
            UIFactory.DestroyChildren(_cardCell);
            _chipRow.SetParent(_cardCell, false);

            var card = after.Cards.TryGetValue(activation.cardInstanceId, out var current)
                ? current
                : new CardView
                {
                    instanceId = activation.cardInstanceId,
                    definitionId = activation.definitionId
                };

            var cardView = BoardCardView.Create(_cardCell);
            _cardRect = (RectTransform)cardView.transform;
            _cardRect.anchorMin = _cardRect.anchorMax = new Vector2(0.5f, 0.5f);
            _cardRect.pivot = new Vector2(0.5f, 0.5f);
            cardView.Populate(card, null, null);
            cardView.ScaleTo(BoardCardView.Width);
            _cardRect.localScale = Vector3.one * StageCardScale;
            cardView.SetPreviewEnabled(false);

            // Drawn after the card so they sit on top of it rather than under.
            _chipRow.SetAsLastSibling();
            BuildChips(card);

            var owner = after.Players.GetValueOrDefault(activation.controllerPlayerId);
            var title = CardDatabase.Instance.TryGet(activation.definitionId, out var definition)
                ? definition.Title
                : activation.definitionId;
            _ownerLabel.text = $"{owner?.Name ?? "Player"} — {title}";
            _detailLabel.text = $"Die {activation.dieValue}  •  {activation.category}";
        }

        /// <summary>
        /// Counters as a stack of chips pushed onto the card - one chip per kind,
        /// carrying its count. A counter is a physical thing sitting on a card in
        /// the paper game, so it reads better as an object on the card than as
        /// another line of text inside it.
        /// </summary>
        private void BuildChips(CardView card)
        {
            UIFactory.DestroyChildren(_chipRow);
            _chipRow.gameObject.SetActive(card.counters is { Length: > 0 });

            if (card.counters == null)
            {
                return;
            }

            foreach (var counter in card.counters)
            {
                var chip = UIFactory.Panel($"{counter.name} Chip", _chipRow, ChipColour(counter.name));
                UIFactory.SetSize(chip, ChipSize, ChipSize);
                var pin = chip.gameObject.AddComponent<LayoutElement>();
                pin.minWidth = pin.preferredWidth = ChipSize;
                pin.minHeight = pin.preferredHeight = ChipSize;

                var disc = chip.GetComponent<Image>();
                disc.sprite = BoardArt.Disc;
                disc.raycastTarget = false;

                var count = UIFactory.Label(
                    "Count", chip, counter.count.ToString(), 15, TextAnchor.MiddleCenter, Color.white);
                count.fontStyle = FontStyle.Bold;
                UIFactory.Stretch(count.rectTransform);
                var outline = count.gameObject.AddComponent<Outline>();
                outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
                outline.effectDistance = new Vector2(1f, -1f);
            }
        }

        /// <summary>
        /// A stable colour per counter kind, so the same counter is the same chip
        /// every time without needing a table of every counter in the game.
        /// </summary>
        private static Color ChipColour(string name)
        {
            var hue = Mathf.Abs((name ?? "").GetHashCode() % 360) / 360f;
            return Color.HSVToRGB(hue, 0.55f, 0.85f);
        }

        private void BuildHud(Snapshot snapshot)
        {
            UIFactory.DestroyChildren(_hud);
            _rows.Clear();

            foreach (var player in snapshot.Players.Values.OrderBy(player => player.Id))
            {
                var panel = UIFactory.Panel($"Player {player.Id} Tracks", _hud,
                    new Color(0.075f, 0.061f, 0.105f, 0.98f));
                UITheme.Frame(panel.GetComponent<Image>(), 1.3f, UITheme.Border);
                var pin = panel.gameObject.AddComponent<LayoutElement>();
                pin.minWidth = 230f;
                pin.preferredWidth = 300f;
                pin.preferredHeight = 118f;
                pin.flexibleWidth = 1f;

                var layout = UIFactory.VerticalLayout(
                    panel, 5, new RectOffset(9, 9, 7, 7), controlHeight: true);
                layout.childForceExpandWidth = true;
                var name = UIFactory.Label("Name", panel,
                    player.Name + (player.Alive ? "" : "  —  OUT"), 17,
                    TextAnchor.MiddleLeft, player.Alive ? UITheme.Bone : UITheme.BoneDim);
                name.fontStyle = FontStyle.Bold;
                PinHeight(name.rectTransform, 22f);

                var health = BuildBar(panel, "Health", new Color(0.88f, 0.16f, 0.27f),
                    player.Health, GameSettings.MaxHealth, out var healthText);
                var followers = BuildBar(panel, "Followers", UITheme.Signal,
                    player.Followers, GameSettings.FollowersToWin, out var followerText);
                var block = UIFactory.Label("Block", panel, $"+{player.Block} block", 13,
                    TextAnchor.MiddleRight, new Color(0.31f, 0.92f, 0.55f));
                PinHeight(block.rectTransform, 17f);

                _rows[player.Id] = new HudRow
                {
                    Root = panel,
                    Health = health,
                    Followers = followers,
                    HealthText = healthText,
                    FollowersText = followerText,
                    BlockText = block
                };
            }
        }

        private static Image BuildBar(
            Transform parent, string name, Color color, int value, int max, out Text label)
        {
            var track = UIFactory.Panel($"{name} Track", parent, new Color(0f, 0f, 0f, 0.62f));
            PinHeight(track, 25f);
            var fill = UIFactory.FillBar($"{name} Fill", track, color);
            UIFactory.Stretch(fill.rectTransform);
            fill.fillAmount = Mathf.Clamp01(value / (float)max);
            label = UIFactory.Label($"{name} Value", track, $"{name}  {value}/{max}", 14,
                TextAnchor.MiddleCenter, Color.white);
            label.fontStyle = FontStyle.Bold;
            UIFactory.Stretch(label.rectTransform);
            return fill;
        }

        private IEnumerator AnimateStats(Snapshot before, Snapshot after, float duration)
        {
            yield return Tween(duration, t =>
            {
                var eased = Smooth(t);
                foreach (var pair in _rows)
                {
                    if (!before.Players.TryGetValue(pair.Key, out var from)
                        || !after.Players.TryGetValue(pair.Key, out var to))
                    {
                        continue;
                    }

                    var health = Mathf.RoundToInt(Mathf.Lerp(from.Health, to.Health, eased));
                    var followers = Mathf.RoundToInt(Mathf.Lerp(from.Followers, to.Followers, eased));
                    pair.Value.Health.fillAmount = Mathf.Clamp01(health / (float)GameSettings.MaxHealth);
                    pair.Value.Followers.fillAmount = Mathf.Clamp01(followers / (float)GameSettings.FollowersToWin);
                    pair.Value.HealthText.text = $"Health  {health}/{GameSettings.MaxHealth}";
                    pair.Value.FollowersText.text = $"Followers  {followers}/{GameSettings.FollowersToWin}";
                    pair.Value.BlockText.text = $"+{Mathf.RoundToInt(Mathf.Lerp(from.Block, to.Block, eased))} block";
                }
            });
        }

        private IEnumerator Jolt(float height, float duration)
        {
            var origin = _cardRect.anchoredPosition;
            yield return Tween(duration, t =>
            {
                var movement = t < 0.32f
                    ? Mathf.SmoothStep(0f, height, t / 0.32f)
                    : Mathf.Lerp(height, 0f, Smooth((t - 0.32f) / 0.68f));
                _cardRect.anchoredPosition = origin + Vector2.up * movement;
            });
            _cardRect.anchoredPosition = origin;
        }

        private IEnumerator ShakeCard(float duration)
        {
            var origin = _cardRect.anchoredPosition;
            yield return Tween(duration, t =>
            {
                var fade = 1f - t;
                _cardRect.anchoredPosition = origin
                    + Vector2.right * (Mathf.Sin(t * Mathf.PI * 10f) * 9f * fade);
            });
            _cardRect.anchoredPosition = origin;
        }

        private IEnumerator GrowAndSettle()
        {
            yield return Tween(0.5f, t =>
            {
                var swell = Mathf.Sin(t * Mathf.PI);
                _cardRect.localScale = Vector3.one * (StageCardScale * (1f + 0.12f * swell));
            });
            _cardRect.localScale = Vector3.one * StageCardScale;
        }

        private void FlyChangedGlyphs(Playback playback, bool health)
        {
            foreach (var pair in playback.After.Players)
            {
                if (!playback.Before.Players.TryGetValue(pair.Key, out var before)
                    || !_rows.TryGetValue(pair.Key, out var row))
                {
                    continue;
                }

                var amount = health
                    ? pair.Value.Health - before.Health
                    : pair.Value.Block - before.Block;
                if (amount <= 0)
                {
                    continue;
                }

                var glyph = health ? "♥" : "+";
                var color = health
                    ? new Color(0.96f, 0.18f, 0.30f)
                    : new Color(0.25f, 0.94f, 0.48f);

                // Aimed at the bar that is about to move, not the panel around
                // it, so the glyph visibly lands on the thing it changes.
                var target = row.Health != null ? row.Health.rectTransform.position : row.Root.position;

                for (var i = 0; i < Mathf.Min(amount, 6); i++)
                {
                    StartCoroutine(FlyGlyph(glyph, color, target, i * 0.055f));
                }
            }
        }

        private IEnumerator FlyGlyph(string glyph, Color color, Vector3 target, float delay)
        {
            if (delay > 0f)
            {
                yield return new WaitForSeconds(delay);
            }

            var label = UIFactory.Label("Healing Glyph", _root, glyph, 36,
                TextAnchor.MiddleCenter, color);
            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(rect, 48f, 48f);
            rect.position = _cardRect.position;
            var start = rect.position;

            yield return Tween(0.55f, t =>
            {
                if (rect == null)
                {
                    return;
                }

                rect.position = Vector3.Lerp(start, target, Smooth(t))
                                + Vector3.up * (70f * Mathf.Sin(t * Mathf.PI));
                rect.localScale = Vector3.one * Mathf.Lerp(1f, 0.65f, t);
            });

            if (label != null)
            {
                Destroy(label.gameObject);
            }
        }

        private static Snapshot Capture(GameView view)
        {
            var snapshot = new Snapshot();
            foreach (var player in view.players)
            {
                snapshot.Players[player.playerId] = new PlayerSnapshot
                {
                    Id = player.playerId,
                    Name = player.name,
                    Health = player.health,
                    Followers = player.followers,
                    Block = player.block,
                    Alive = player.isAlive
                };

                foreach (var card in player.compound)
                {
                    snapshot.Cards[card.instanceId] = card;
                }
            }

            return snapshot;
        }

        private static IEnumerator Tween(float duration, Action<float> step)
        {
            var elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                step(Mathf.Clamp01(elapsed / duration));
                yield return null;
            }
            step(1f);
        }

        private static float Smooth(float value) => value * value * (3f - 2f * value);

        private static void PinHeight(RectTransform rect, float height)
        {
            var pin = rect.gameObject.GetComponent<LayoutElement>();
            if (pin == null)
            {
                pin = rect.gameObject.AddComponent<LayoutElement>();
            }
            pin.minHeight = pin.preferredHeight = height;
        }
    }
}
