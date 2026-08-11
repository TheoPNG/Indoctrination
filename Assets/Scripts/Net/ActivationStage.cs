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

            /// <summary>Resource counts by colour, so a card that pays out can show what it paid.</summary>
            public readonly Dictionary<ResourceColor, int> Resources = new();
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
        private Text _choicePrompt;
        private Text _ownerLabel;
        private Text _detailLabel;
        private CanvasGroup _group;

        /// <summary>
        /// Builds the menu for a question a card is asking mid-activation. The
        /// stage owns *when* the question is put - before that card's animation,
        /// never during it - but not what answering it means, which stays with
        /// the board and its RPCs.
        /// </summary>
        private Action<RectTransform, Text> _choiceBuilder;

        /// <summary>The activation currently waiting on an answer, or null.</summary>
        private ActivationView _choiceActivation;
        private readonly Queue<Playback> _pending = new();
        private readonly Dictionary<int, HudRow> _rows = new();
        private Snapshot _lastSnapshot;
        private int _batch = -1;
        private int _seen;
        private bool _playing;
        private const float StageCardScale = 1.25f;

        /// <summary>
        /// Where a card sits on the board, so it can rise from its own place.
        /// Null when the board cannot find it - a card that has already left play
        /// by the time its activation is presented.
        /// </summary>
        private Func<int, Vector3?> _originResolver;

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

            // Above the card, not below it. A damage card jolts upward, and the
            // bars are what it is jolting at - the hit and the health it takes
            // off should be the same gesture, read in one place.
            _hud = UIFactory.Group("All Player Tracks", root);
            _hud.anchorMin = new Vector2(0f, 1f);
            _hud.anchorMax = new Vector2(1f, 1f);
            _hud.pivot = new Vector2(0.5f, 1f);
            _hud.offsetMin = new Vector2(22f, -152f);
            _hud.offsetMax = new Vector2(-22f, -14f);
            var hudLayout = UIFactory.HorizontalLayout(
                _hud, 14, new RectOffset(8, 8, 8, 8), controlWidth: true, controlHeight: true);
            hudLayout.childForceExpandWidth = true;
            hudLayout.childForceExpandHeight = true;

            _ownerLabel = UIFactory.Label(
                "Controller", root, "", 24, TextAnchor.MiddleCenter, UITheme.Bone);
            _ownerLabel.fontStyle = FontStyle.Bold;
            _ownerLabel.rectTransform.anchorMin = new Vector2(0.15f, 0.17f);
            _ownerLabel.rectTransform.anchorMax = new Vector2(0.85f, 0.23f);
            _ownerLabel.rectTransform.offsetMin = _ownerLabel.rectTransform.offsetMax = Vector2.zero;

            _cardCell = UIFactory.Group("Locked Card", root);
            _cardCell.anchorMin = _cardCell.anchorMax = new Vector2(0.5f, 0.47f);
            _cardCell.pivot = new Vector2(0.5f, 0.5f);
            UIFactory.SetSize(_cardCell, 225f, 312f);

            _detailLabel = UIFactory.Label(
                "Activation Detail", root, "", 16, TextAnchor.MiddleCenter, UITheme.BoneDim);
            _detailLabel.rectTransform.anchorMin = new Vector2(0.15f, 0.12f);
            _detailLabel.rectTransform.anchorMax = new Vector2(0.85f, 0.165f);
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

            // Most questions need no prompt - the options say what they do and
            // the card is on screen above them. A plain yes/no offer is the
            // exception: "Yes" alone means nothing, so those fill this in.
            _choicePrompt = UIFactory.Label(
                "Choice Prompt", root, "", 17, TextAnchor.MiddleCenter, UITheme.Bone);
            _choicePrompt.rectTransform.anchorMin = new Vector2(0.1f, 0.11f);
            _choicePrompt.rectTransform.anchorMax = new Vector2(0.9f, 0.16f);
            _choicePrompt.rectTransform.offsetMin = _choicePrompt.rectTransform.offsetMax = Vector2.zero;
            _choicePrompt.gameObject.SetActive(false);

            // The question a card asks arrives here, directly under it, so the
            // decision is made looking at the card it belongs to.
            _choiceRow = UIFactory.Group("Choice", root);
            _choiceRow.anchorMin = new Vector2(0.12f, 0.025f);
            _choiceRow.anchorMax = new Vector2(0.88f, 0.105f);
            _choiceRow.offsetMin = _choiceRow.offsetMax = Vector2.zero;
            var choiceLayout = UIFactory.HorizontalLayout(_choiceRow, 10, new RectOffset(0, 0, 0, 0));
            choiceLayout.childAlignment = TextAnchor.MiddleCenter;
            _choiceRow.gameObject.SetActive(false);
        }

        private const float ChipSize = 30f;

        /// <summary>Consumes newly completed entries from a server view.</summary>
        public void Present(
            GameView view,
            Action<RectTransform, Text> choiceBuilder = null,
            Func<int, Vector3?> originResolver = null)
        {
            _choiceBuilder = choiceBuilder;
            _originResolver = originResolver;
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
            _heldCardInstanceId = -1;
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
                    _choicePrompt.gameObject.SetActive(false);
                }

                _root.gameObject.SetActive(false);
            }
        }

        private IEnumerator PlayPending()
        {
            _playing = true;
            while (_pending.Count > 0)
            {
                // A unit woken by two matching dice fires twice. That is the
                // rule and it stays, but it is one card doing its thing twice -
                // so it comes up once and strikes twice, rather than the same
                // card appearing again a moment later as though a second copy
                // of it had gone off.
                var first = _pending.Dequeue();
                var repeats = 1;

                while (_pending.Count > 0
                       && _pending.Peek().Activation.cardInstanceId == first.Activation.cardInstanceId)
                {
                    // The last one's After carries the full result of every
                    // firing, so the bars move once, to where they end up.
                    first.After = _pending.Dequeue().After;
                    repeats++;
                }

                yield return Play(first, repeats);
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

            // Remembered so that when the answer comes back and this card's
            // effect finally resolves, it animates from where it already is
            // rather than being rebuilt and flown in again.
            _heldCardInstanceId = _choiceActivation.cardInstanceId;

            UIFactory.DestroyChildren(_choiceRow);
            _choiceRow.gameObject.SetActive(true);

            _choicePrompt.text = "";
            _choiceBuilder?.Invoke(_choiceRow, _choicePrompt);
            _choicePrompt.gameObject.SetActive(!string.IsNullOrEmpty(_choicePrompt.text));
        }

        /// <summary>
        /// The card currently held on screen waiting for an answer, or -1. Its
        /// activation animation continues from here instead of starting over.
        /// </summary>
        private int _heldCardInstanceId = -1;

        private IEnumerator Play(Playback playback, int repeats = 1)
        {
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();

            // This card may already be on screen, held up while it asked its
            // question. If so it stays exactly where it is: it should appear,
            // ask, act, and leave - one continuous visit. Tearing it down and
            // flying it back in made answering look like the card activating
            // twice.
            var held = _heldCardInstanceId == playback.Activation.cardInstanceId && _cardRect != null;

            // Either way the menu comes down; the animation has the stage to itself.
            _choiceRow.gameObject.SetActive(false);
            _choicePrompt.gameObject.SetActive(false);

            BuildHud(playback.Before);

            var home = _cardCell.position;

            // Where this card lives on the board. It is both where it rises from
            // and where it sinks back toward, so it is resolved once for both.
            var origin = _originResolver?.Invoke(playback.Activation.cardInstanceId) ?? home;

            if (!held)
            {
                _group.alpha = 0f;
                BuildCard(playback.Activation, playback.After);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_root);

                // Rises out of its own place on the table rather than fading in
                // at the middle of the screen. Which compound it came from is
                // the first thing you need to know about an activation, and
                // watching it leave says it without a label.
                _cardRect.position = origin;
                _cardRect.localScale = Vector3.one * (StageCardScale * 0.35f);

                yield return Tween(0.55f, t =>
                {
                    var eased = Smooth(t);
                    _group.alpha = Mathf.Min(1f, eased * 1.6f);
                    _cardRect.position = Vector3.Lerp(origin, home, eased);
                    _cardRect.localScale = Vector3.one
                                           * (StageCardScale * Mathf.Lerp(0.35f, 1f, eased));
                });

                _cardRect.position = home;
                _cardRect.anchoredPosition = Vector2.zero;
            }
            else
            {
                _group.alpha = 1f;
                LayoutRebuilder.ForceRebuildLayoutImmediate(_root);
            }

            _heldCardInstanceId = -1;

            _detailLabel.text = $"Die {playback.Activation.dieValue}  •  {playback.Activation.category}"
                                + (repeats > 1 ? $"   ×{repeats}" : "");

            Enum.TryParse(playback.Activation.category, out ActivationCategory category);

            // The strike happens once per firing; the bars move once, afterwards,
            // to where every firing left them. Moving them per strike would mean
            // animating to a number the server never actually reported.
            for (var strike = 0; strike < repeats; strike++)
            {
                switch (category)
                {
                    case ActivationCategory.Damage:
                        yield return Jolt(46f, 0.55f);
                        break;

                    case ActivationCategory.Followers:
                        yield return Jolt(-22f, 0.60f);
                        break;

                    case ActivationCategory.Health:
                        yield return ShakeCard(0.55f);
                        break;

                    case ActivationCategory.Block:
                        yield return ShakeCard(0.50f);
                        break;

                    default:
                        yield return GrowAndSettle();
                        break;
                }
            }

            switch (category)
            {
                case ActivationCategory.Health:
                    FlyChangedGlyphs(playback, health: true);
                    break;

                case ActivationCategory.Block:
                    FlyChangedGlyphs(playback, health: false);
                    break;

                default:
                    FlyChangedResources(playback);
                    break;
            }

            yield return AnimateStats(playback.Before, playback.After, category switch
            {
                ActivationCategory.Damage => 1.30f,
                ActivationCategory.Followers => 1.20f,
                ActivationCategory.Health => 1.35f,
                ActivationCategory.Block => 1.25f,
                _ => 1.00f
            });

            yield return new WaitForSeconds(0.40f);
            yield return Tween(0.35f, t =>
            {
                var eased = Smooth(t);
                _group.alpha = 1f - eased;
                _cardRect.position = Vector3.Lerp(home, origin, eased * 0.55f);
                _cardRect.localScale = Vector3.one
                                       * (StageCardScale * Mathf.Lerp(1f, 0.55f, eased));
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

                // Block is green track welded onto the right-hand end of health,
                // to the same scale, exactly as the board's own stat bars draw
                // it - not a line of text saying how much there is.
                var healthRow = UIFactory.Group("Health Row", panel);
                var healthLayout = UIFactory.HorizontalLayout(
                    healthRow, 0, new RectOffset(0, 0, 0, 0), controlWidth: true, controlHeight: true);
                healthLayout.childAlignment = TextAnchor.MiddleLeft;
                PinHeight(healthRow, 25f);

                var health = BuildBar(healthRow, "Health", new Color(0.88f, 0.16f, 0.27f),
                    player.Health, GameSettings.MaxHealth, out var healthText);
                health.rectTransform.parent.gameObject.GetComponent<LayoutElement>().flexibleWidth = 1f;

                var blockTrack = UIFactory.Panel(
                    "Block", healthRow, new Color(0.247f, 0.722f, 0.502f, 0.95f));
                var blockPin = blockTrack.gameObject.AddComponent<LayoutElement>();
                blockPin.minHeight = blockPin.preferredHeight = 25f;
                blockPin.flexibleWidth = 0f;
                blockPin.minWidth = blockPin.preferredWidth =
                    Mathf.Clamp01(player.Block / (float)GameSettings.MaxHealth) * 150f;
                blockTrack.gameObject.SetActive(player.Block > 0);

                var followers = BuildBar(panel, "Followers", UITheme.Signal,
                    player.Followers, GameSettings.FollowersToWin, out var followerText);

                var block = UIFactory.Label("Block Value", blockTrack, "", 12,
                    TextAnchor.MiddleCenter, UITheme.Void);
                block.fontStyle = FontStyle.Bold;
                UIFactory.Stretch(block.rectTransform);

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
                    var block = Mathf.RoundToInt(Mathf.Lerp(from.Block, to.Block, eased));
                    pair.Value.BlockText.text = block > 0 ? block.ToString() : "";

                    // The green segment grows and shrinks with the number, so
                    // Block reads as the health bar getting longer.
                    var blockTrack = (RectTransform)pair.Value.BlockText.transform.parent;
                    blockTrack.gameObject.SetActive(block > 0);
                    var blockPin = blockTrack.GetComponent<LayoutElement>();
                    if (blockPin != null)
                    {
                        blockPin.minWidth = blockPin.preferredWidth =
                            Mathf.Clamp01(block / (float)GameSettings.MaxHealth) * 150f;
                    }
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

        /// <summary>
        /// Resources a card just paid out, thrown off it as coloured pips. The
        /// board's own resource HUD is not on screen during the sequence, so
        /// they fly down toward where it lives rather than at nothing.
        /// </summary>
        private void FlyChangedResources(Playback playback)
        {
            foreach (var pair in playback.After.Players)
            {
                if (!playback.Before.Players.TryGetValue(pair.Key, out var before))
                {
                    continue;
                }

                foreach (var color in BoardArt.Colors)
                {
                    var gained = pair.Value.Resources.GetValueOrDefault(color)
                                 - before.Resources.GetValueOrDefault(color);

                    for (var i = 0; i < Mathf.Min(gained, 5); i++)
                    {
                        var target = _root.TransformPoint(
                            new Vector3(-_root.rect.width * 0.42f, -_root.rect.height * 0.34f, 0f));

                        StartCoroutine(FlyGlyph("●", BoardArt.ColorOf(color), target, i * 0.07f));
                    }
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
                var captured = new PlayerSnapshot
                {
                    Id = player.playerId,
                    Name = player.name,
                    Health = player.health,
                    Followers = player.followers,
                    Block = player.block,
                    Alive = player.isAlive
                };

                captured.Resources[ResourceColor.Red] = player.red;
                captured.Resources[ResourceColor.Green] = player.green;
                captured.Resources[ResourceColor.Blue] = player.blue;
                captured.Resources[ResourceColor.Yellow] = player.yellow;

                snapshot.Players[player.playerId] = captured;

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
