using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using Indoctrination.Net;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Indoctrination.EditorTools
{
    /// <summary>
    /// Checks the things only a real Unity process can answer: that the scene
    /// carries the objects the game needs, that the card database loads through
    /// Resources, and that the whole board interface builds without throwing.
    ///
    /// RulesCheck covers the rules and CompileCheck covers the types, but neither
    /// can construct a Canvas - so a UI that threw on its first frame would look
    /// exactly like a UI that worked until somebody pressed Play.
    ///
    ///     Tools/SmokeTest/run.sh
    /// </summary>
    public static class AlphaSmokeTest
    {
        private static readonly List<string> Failures = new();
        private static readonly List<string> Errors = new();

        [MenuItem("Indoctrination/Run Smoke Test")]
        public static void RunFromMenu() => Run();

        /// <summary>Entry point for -executeMethod. Exits non-zero if anything failed.</summary>
        public static void RunBatch()
        {
            var ok = Run();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool Run()
        {
            Failures.Clear();
            Errors.Clear();

            // Batchmode attaches a full stack trace to every Debug.Log, which
            // buries the actual results under twelve lines of Unity internals.
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);

            // Anything logged as an error by code under test counts as a failure,
            // even when it did not throw - a swallowed exception inside a Unity
            // callback still means something is broken.
            Application.logMessageReceived += CollectErrors;

            try
            {
                CheckScene();
                CheckCardDatabase();
                CheckBoardUiBuilds();
            }
            finally
            {
                Application.logMessageReceived -= CollectErrors;
            }

            foreach (var error in Errors)
            {
                Failures.Add($"logged an error: {error}");
            }

            Debug.Log(Failures.Count == 0
                ? "SMOKE TEST PASSED"
                : $"SMOKE TEST FAILED\n  {string.Join("\n  ", Failures)}");

            return Failures.Count == 0;
        }

        private static void CollectErrors(string condition, string stackTrace, LogType type)
        {
            if (type is LogType.Error or LogType.Exception or LogType.Assert)
            {
                Errors.Add(condition);
            }
        }

        private static void Check(string label, bool condition, string detail = "")
        {
            Debug.Log($"  {(condition ? "PASS" : "FAIL")}  {label}{(detail == "" ? "" : $"  [{detail}]")}");
            if (!condition)
            {
                Failures.Add(label);
            }
        }

        private static void CheckScene()
        {
            Debug.Log("Scene wiring:");

            var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            Check("the scene opens", scene.IsValid());

            var networkManager = UnityEngine.Object.FindAnyObjectByType<NetworkManager>();
            Check("a NetworkManager is present", networkManager != null);
            Check("with a transport configured",
                  networkManager != null && networkManager.NetworkConfig?.NetworkTransport != null);

            var gameManager = UnityEngine.Object.FindAnyObjectByType<NetworkGameManager>();
            Check("a NetworkGameManager is present", gameManager != null);
            Check("carrying a NetworkObject, as Netcode requires",
                  gameManager != null && gameManager.GetComponent<NetworkObject>() != null);

            Check("a BoardUI is present", UnityEngine.Object.FindAnyObjectByType<BoardUI>() != null);
            Check("a camera is tagged MainCamera", Camera.main != null);
        }

        private static void CheckCardDatabase()
        {
            Debug.Log("Card database:");

            var database = CardDatabase.Instance;
            Check("loads through Resources", database != null && database.All.Count > 0,
                  $"{database?.All.Count ?? 0} definitions");

            // Every id the code refers to by constant has to resolve at runtime,
            // or the card silently never finds what it is looking for.
            var missing = typeof(CardIds)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Select(f => (string)f.GetRawConstantValue())
                .Where(id => !database.TryGet(id, out _))
                .ToList();

            Check("every CardIds constant resolves at runtime", missing.Count == 0, string.Join(", ", missing));

            var parsed = 0;
            foreach (var card in database.All)
            {
                _ = card.Type;
                _ = card.Color;
                _ = card.Cost;
                parsed++;
            }

            Check("every card's type, colour, and cost parse", parsed == database.All.Count);

            var blueCards = database.All.Where(card => card.Color == ResourceColor.Blue).ToList();
            var missingBlueFaces = blueCards
                .Where(card => CardArt.FaceFor(card.Id) == null)
                .Select(card => card.Title)
                .ToList();
            Check("every Blue card has a printed face", missingBlueFaces.Count == 0,
                  string.Join(", ", missingBlueFaces));

            var wrongAspect = blueCards
                .Select(card => new { card.Title, Face = CardArt.FaceFor(card.Id) })
                .Where(entry => entry.Face != null
                                && !Mathf.Approximately(
                                    entry.Face.rect.width / entry.Face.rect.height, 5f / 7f))
                .Select(entry => entry.Title)
                .ToList();
            Check("printed faces preserve the PDF 5:7 aspect", wrongAspect.Count == 0,
                  string.Join(", ", wrongAspect));
        }

        /// <summary>
        /// Builds the board interface for real and drives a full game view through
        /// it. BoardUI constructs its entire canvas in Awake, so simply adding the
        /// component exercises every widget the game ever builds.
        /// </summary>
        private static void CheckBoardUiBuilds()
        {
            Debug.Log("Board interface:");

            GameObject host = null;
            try
            {
                host = new GameObject("Smoke Test BoardUI");
                var board = host.AddComponent<BoardUI>();

                // Awake does not run outside play mode, so the build is asked for
                // explicitly - the same call Awake itself makes.
                board.BuildInterface();

                Check("the whole interface builds without throwing", board != null);
                Check("and produced a canvas", UnityEngine.Object.FindAnyObjectByType<Canvas>() != null);

                CheckDraftTitlesAndRollingControls(board);
                DriveViewsThroughTheBoard(board);
                CheckBoardLayout();
            }
            catch (Exception e)
            {
                Check($"the whole interface builds without throwing ({e.GetType().Name}: {e.Message})", false);
                Debug.Log($"SMOKE TRACE\n{e}");
            }
            finally
            {
                if (host != null)
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }
            }
        }

        /// <summary>
        /// Reproduces the reported path exactly: render the dealt draft, verify
        /// every card carries readable title text, take picks until three cards
        /// remain, and verify every player perspective receives Roll Die.
        /// </summary>
        private static void CheckDraftTitlesAndRollingControls(BoardUI board)
        {
            var game = new GameState(new[] { "A", "B", "C" }, CardDatabase.Instance.All, randomSeed: 197);
            game.BeginDraft();
            board.RenderForTesting(GameViewBuilder.Build(game, 0));

            var canvas = UnityEngine.Object.FindAnyObjectByType<Canvas>();
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)canvas.transform);
            Canvas.ForceUpdateCanvases();

            var renderedCards = UnityEngine.Object.FindObjectsByType<BoardCardView>()
                .Where(card => card.Card != null)
                .ToList();
            var titleFailures = new List<string>();

            foreach (var card in renderedCards)
            {
                var title = card.transform.Find("Title")?.GetComponent<Text>();
                var printedFace = card.transform.Find("Printed Face")?.GetComponent<Image>();
                CardDatabase.Instance.TryGet(card.Card.definitionId, out var definition);
                var expected = definition?.Title ?? card.Card.definitionId;
                var cardRect = ((RectTransform)card.transform).rect;

                if (printedFace != null && printedFace.gameObject.activeSelf)
                {
                    var faceRect = printedFace.rectTransform.rect;
                    if (printedFace.sprite == null
                        || !printedFace.preserveAspect
                        || faceRect.width <= 0f
                        || faceRect.height <= 0f)
                    {
                        titleFailures.Add($"{expected}: printed face is missing or has no size");
                    }

                    continue;
                }

                var titleBounds = title == null
                    ? new Bounds()
                    : RectTransformUtility.CalculateRelativeRectTransformBounds(
                        card.transform, title.rectTransform);

                if (title == null
                    || title.text != expected
                    || title.font == null
                    || title.fontSize < 18
                    || title.color.a < 0.99f
                    || title.rectTransform.rect.width <= 0f
                    || title.rectTransform.rect.height < 72f
                    || titleBounds.min.x < cardRect.xMin - 0.1f
                    || titleBounds.max.x > cardRect.xMax + 0.1f
                    || titleBounds.min.y < cardRect.yMin - 0.1f
                    || titleBounds.max.y > cardRect.yMax + 0.1f)
                {
                    titleFailures.Add($"{expected}: '{title?.text ?? "missing"}' " +
                                      $"{title?.rectTransform.rect.width ?? 0:0}x" +
                                      $"{title?.rectTransform.rect.height ?? 0:0}");
                }
            }

            Check("every draft card renders a printed face or a visible title row",
                  renderedCards.Count == game.DraftZone.Count && titleFailures.Count == 0,
                  titleFailures.Count == 0
                      ? $"{renderedCards.Count} cards"
                      : string.Join(" | ", titleFailures));

            while (game.CurrentDrafterId is int drafter)
            {
                game.DraftCard(drafter, game.DraftZone[0].InstanceId);
            }

            Check("three leftovers close the draft into Rolling",
                  game.Phase == TurnPhase.Rolling
                  && game.DraftZone.Count == 0
                  && game.Discard.Count == GameSettings.UndraftedCardsDiscarded,
                  $"{game.Phase}, zone {game.DraftZone.Count}, discard {game.Discard.Count}");

            foreach (var player in game.Players)
            {
                board.RenderForTesting(GameViewBuilder.Build(game, player.PlayerId));
                var gameRoot = canvas.transform.Find("Game Root") as RectTransform;
                var popupPanel = gameRoot?.Find("Popup Panel") as RectTransform;
                var actionViewport = popupPanel?.Find("Action Viewport") as RectTransform;
                var actionContent = actionViewport?.Find("Action Content") as RectTransform;
                var roll = actionContent?.Find("Roll") as RectTransform;
                LayoutRebuilder.ForceRebuildLayoutImmediate(gameRoot);
                Canvas.ForceUpdateCanvases();

                var rollBounds = roll == null || actionViewport == null
                    ? new Bounds()
                    : RectTransformUtility.CalculateRelativeRectTransformBounds(actionViewport, roll);
                var viewportRect = actionViewport?.rect ?? new Rect();
                var rollIsVisible = roll != null
                    && roll.gameObject.activeInHierarchy
                    && roll.rect.width >= 240f
                    && roll.rect.width <= 280f
                    && roll.rect.height >= 48f
                    && roll.rect.height <= 58f
                    && popupPanel.rect.width <= 310f
                    && popupPanel.rect.height <= 100f
                    && rollBounds.min.x >= viewportRect.xMin - 0.1f
                    && rollBounds.max.x <= viewportRect.xMax + 0.1f
                    && rollBounds.min.y >= viewportRect.yMin - 0.1f
                    && rollBounds.max.y <= viewportRect.yMax + 0.1f;
                Check($"player {player.PlayerId} perspective gets Roll Die immediately",
                      rollIsVisible,
                      roll == null
                          ? "button missing"
                          : $"{roll.rect.width:0}x{roll.rect.height:0}, " +
                            $"viewport y {viewportRect.yMin:0}..{viewportRect.yMax:0}, " +
                            $"button y {rollBounds.min.y:0}..{rollBounds.max.y:0}");

                // The hand is hover-based now: always present, floating above
                // the bottom edge, but only reads as "open" while something is
                // actually pointing at it. A fresh phase should start collapsed.
                var handRow = gameRoot?.Find("Hand Row");
                var handExpanded = typeof(BoardUI)
                    .GetField("_handExpanded", BindingFlags.NonPublic | BindingFlags.Instance)?
                    .GetValue(board) as bool?;
                Check($"player {player.PlayerId} enters Rolling with the hand collapsed",
                      handRow != null && handExpanded == false,
                      handRow == null ? "hand row not found" : "still showing");
            }
        }

        /// <summary>
        /// Proves the middle row is using the widths its LayoutElements request.
        /// If its HorizontalLayoutGroup stops controlling child width, Unity gives
        /// both panels their 100-unit RectTransform defaults and piles the entire
        /// playable board against the left edge.
        /// </summary>
        /// <summary>
        /// Proves the middle row is using the widths its LayoutElements request,
        /// and that the popup which replaced the old side panel is wired up
        /// correctly wherever it now lives - floating over the board rather than
        /// occupying a permanent column next to it.
        /// </summary>
        private static void CheckBoardLayout()
        {
            var canvas = UnityEngine.Object.FindAnyObjectByType<Canvas>();
            var gameRoot = canvas?.transform.Find("Game Root") as RectTransform;
            var middle = gameRoot?.Find("Middle Area") as RectTransform;
            var battlefield = middle?.Find("Battlefield Panel") as RectTransform;
            var hudColumn = middle?.Find("Resource HUD Column") as RectTransform;
            var popupPanel = gameRoot?.Find("Popup Panel") as RectTransform;
            var actionViewport = popupPanel?.Find("Action Viewport") as RectTransform;
            var actionContent = actionViewport?.Find("Action Content") as RectTransform;
            var actionScroll = popupPanel?.GetComponent<ScrollRect>();

            if (gameRoot == null || middle == null || battlefield == null || hudColumn == null
                || popupPanel == null || actionViewport == null || actionContent == null || actionScroll == null)
            {
                Check("the battlefield and controls have a measurable layout", false);
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(gameRoot);
            Canvas.ForceUpdateCanvases();

            // The compounds are the main event: everything that used to be a
            // permanent side column is gone but the resource HUD, which stays
            // just wide enough for its four circles.
            Check("the battlefield takes almost all of the middle row, beside a slim resource column",
                  battlefield.rect.width > hudColumn.rect.width
                  && battlefield.rect.width >= 360f,
                  $"battlefield {battlefield.rect.width:0}, HUD column {hudColumn.rect.width:0}");

            Check("the popup scrolls vertically inside a clipped viewport",
                  actionScroll.vertical
                  && !actionScroll.horizontal
                  && actionViewport.GetComponent<RectMask2D>() != null
                  && actionScroll.viewport == actionViewport
                  && actionScroll.content == actionContent,
                  $"viewport {actionViewport.rect.width:0}x{actionViewport.rect.height:0}");

            var battlefieldBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(middle, battlefield);
            var hudBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(middle, hudColumn);
            Check("the battlefield and resource HUD remain inside the middle frame",
                  hudBounds.min.x >= middle.rect.xMin - 0.1f
                  && battlefieldBounds.max.x <= middle.rect.xMax + 0.1f,
                  $"frame {middle.rect.xMin:0}..{middle.rect.xMax:0}, " +
                  $"content {hudBounds.min.x:0}..{battlefieldBounds.max.x:0}");

            // A question is usually answered by looking at your own hand or
            // somebody's compound first, so the popup must never cover the board
            // the way the old scrim did. Nothing full-screen may sit behind it,
            // and it has to leave the bottom of the screen - the hand, and your
            // own compound - alone.
            var scrim = gameRoot.Find("Popup Scrim");
            Check("no scrim greys the board out behind the popup", scrim == null,
                  scrim == null ? "" : "a full-screen scrim is still being built");

            var handRow = gameRoot.Find("Hand Row") as RectTransform;
            Check("the hand floats free of the layout, so opening it cannot move the board",
                  handRow != null
                  && handRow.parent == gameRoot
                  && handRow.GetComponent<LayoutElement>() != null
                  && handRow.GetComponent<LayoutElement>().ignoreLayout,
                  handRow == null ? "hand row not found" : "hand row is still laid out in a row");

            // The hand has no visible tray, but its transparent Graphic remains
            // the hover and draft-drop surface instead of letting input fall
            // through to the compound behind it.
            Check("the fanned hand has an invisible input surface, not a box",
                  handRow != null
                  && handRow.GetComponent<Image>() != null
                  && handRow.GetComponent<Image>().raycastTarget
                  && handRow.GetComponent<Image>().color.a <= 0.001f,
                  "the hand row should be transparent while still receiving input");

            // A flat shelf spanning the zone, deliberately not the stretched-and-
            // clipped ellipse this used to be - that read as a blue bubble
            // rising out of the floor, the one round thing on a hard-edged board.
            var dropZone = gameRoot.Find("Hand Drop Zone") as RectTransform;
            var dropArc = dropZone?.Find("Drop Arc") as RectTransform;
            var dropEdge = dropArc?.Find("Drop Edge") as RectTransform;
            Check("drafting has a wide flat drop shelf behind the hand",
                  dropZone != null
                  && dropArc != null
                  && dropEdge != null
                  && !dropZone.GetComponent<Image>().raycastTarget
                  && dropZone.rect.width > BoardCardView.Width * 2f
                  && dropArc.rect.height <= dropZone.rect.height + 0.5f
                  && dropArc.GetComponent<Image>().sprite == null,
                  dropZone == null ? "drop zone not found" : $"drop zone {dropZone.rect.size}");

            var popupBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(gameRoot, popupPanel);
            Check("the popup leaves the bottom of the screen clear for the hand",
                  handRow != null
                  && popupBounds.min.y > gameRoot.rect.yMin + handRow.rect.height,
                  $"popup reaches down to {popupBounds.min.y:0}, " +
                  $"floor is {gameRoot.rect.yMin:0}");
        }

        /// <summary>
        /// Plays a game headlessly and pushes its views into the board, so every
        /// rendering path - draft zone, hand tray, battlefield, choice prompts,
        /// game over - is actually built rather than assumed.
        ///
        /// Each distinct situation is rendered exactly once. A full board rebuild
        /// creates hundreds of objects, so re-rendering every phase of every turn
        /// would take longer than the whole rest of the test put together without
        /// covering anything new.
        /// </summary>
        private static void DriveViewsThroughTheBoard(BoardUI board)
        {
            var game = new GameState(new[] { "A", "B", "C" }, CardDatabase.Instance.All, randomSeed: 99);
            game.BeginDraft();

            var rendered = new HashSet<string>();
            var phasesSeen = new HashSet<string>();
            var choicesSeen = new HashSet<string>();

            for (var step = 0; step < 600 && game.Phase != TurnPhase.GameOver; step++)
            {
                var situation = game.PendingChoice != null
                    ? $"choice:{game.PendingChoice.Kind}"
                    : $"phase:{game.Phase}";

                phasesSeen.Add(game.Phase.ToString());
                if (game.PendingChoice != null)
                {
                    choicesSeen.Add(game.PendingChoice.Kind.ToString());
                }

                if (rendered.Add(situation))
                {
                    foreach (var player in game.Players)
                    {
                        board.RenderForTesting(GameViewBuilder.Build(game, player.PlayerId, 30f, 20f));
                    }
                }

                if (game.PendingChoice != null)
                {
                    game.AnswerPendingChoiceWithDefault();
                    continue;
                }

                AdvanceOneStep(game);
            }

            Check("every phase of play renders",
                  phasesSeen.Contains(nameof(TurnPhase.Draft))
                  && phasesSeen.Contains(nameof(TurnPhase.Rolling))
                  && phasesSeen.Contains(nameof(TurnPhase.Activation))
                  && phasesSeen.Contains(nameof(TurnPhase.Resource))
                  && phasesSeen.Contains(nameof(TurnPhase.Buy)),
                  string.Join(", ", phasesSeen.OrderBy(p => p)));

            Check("along with the card questions it asked",
                  choicesSeen.Count > 0, string.Join(", ", choicesSeen.OrderBy(k => k)));

            RenderFinishedBoard(board);
        }

        /// <summary>
        /// The end-of-game board has a layout of its own - the standings and the
        /// host's offer of another game - so it is built from a game deliberately
        /// played to a finish rather than left to chance.
        /// </summary>
        private static void RenderFinishedBoard(BoardUI board)
        {
            var game = new GameState(new[] { "A", "B" }, CardDatabase.Instance.All, randomSeed: 100);
            game.BeginDraft();
            while (game.CurrentDrafterId is int drafter)
            {
                game.DraftCard(drafter, game.DraftZone[0].InstanceId);
            }

            game.DealDamage(null, game.Players[1], GameSettings.StartingHealth);
            game.ResolveEffects();

            Check("a game can be played to a finish", game.Phase == TurnPhase.GameOver, game.Phase.ToString());

            foreach (var player in game.Players)
            {
                board.RenderForTesting(GameViewBuilder.Build(game, player.PlayerId));
            }

            Check("and the finished board renders for winner and loser alike", true);
        }

        private static void AdvanceOneStep(GameState game)
        {
            switch (game.Phase)
            {
                case TurnPhase.Draft when game.CurrentDrafterId is int drafter:
                    game.DraftCard(drafter, game.DraftZone[0].InstanceId);
                    break;

                case TurnPhase.Rolling:
                    if (!game.DiceRolled)
                    {
                        game.RollPrimaryDice();
                    }
                    else
                    {
                        game.AdvancePhase();
                    }

                    break;

                case TurnPhase.Resource:
                    // One of each colour, so cards of every cost become affordable
                    // and the Buy phase below actually gets to play them.
                    foreach (var player in game.LivingPlayers.ToList())
                    {
                        if (game.HasCollectedResources(player.PlayerId))
                        {
                            continue;
                        }

                        var allowance = game.ResourcesPerTurnFor(player.PlayerId);
                        game.CollectResources(player.PlayerId,
                            Enumerable.Range(0, allowance)
                                .Select(i => EffectContext.AllColors[i % EffectContext.AllColors.Count])
                                .ToList());
                    }

                    game.AdvancePhase();
                    break;

                case TurnPhase.Buy:
                    // Cards have to actually reach the table for their effects -
                    // and the questions they ask - to be rendered at all.
                    foreach (var player in game.LivingPlayers.ToList())
                    {
                        foreach (var card in player.Hand.ToList())
                        {
                            if (game.PendingChoice != null)
                            {
                                return;
                            }

                            try
                            {
                                game.BuyCard(player.PlayerId, card.InstanceId);
                            }
                            catch (Exception)
                            {
                                // Unaffordable, which is the rules working.
                            }
                        }
                    }

                    game.AdvancePhase();
                    break;

                default:
                    game.AdvancePhase();
                    break;
            }
        }
    }
}
