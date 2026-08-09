using System;
using System.Collections.Generic;
using System.Linq;
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
                CardDatabase.Instance.TryGet(card.Card.definitionId, out var definition);
                var expected = definition?.Title ?? card.Card.definitionId;
                var cardRect = ((RectTransform)card.transform).rect;
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

            Check("every draft card renders its full title in a visible text row",
                  renderedCards.Count == game.DraftZone.Count && titleFailures.Count == 0,
                  titleFailures.Count == 0
                      ? $"{renderedCards.Count} titles"
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
                var actionViewport = gameRoot?.Find("Middle Area/Action Panel/Action Viewport") as RectTransform;
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
                    && roll.rect.width >= 200f
                    && roll.rect.height >= 50f
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

                // The hand is back inside the dock: it reserves real space in the
                // layout so it can never overlap the board or be clipped at the
                // window edge.
                var handRow = gameRoot?.Find("Bottom Dock/Hand Row");
                Check($"player {player.PlayerId} enters Rolling with the hand collapsed",
                      handRow != null && !handRow.gameObject.activeSelf,
                      handRow == null ? "hand row not found" : "still showing");
            }
        }

        /// <summary>
        /// Proves the middle row is using the widths its LayoutElements request.
        /// If its HorizontalLayoutGroup stops controlling child width, Unity gives
        /// both panels their 100-unit RectTransform defaults and piles the entire
        /// playable board against the left edge.
        /// </summary>
        private static void CheckBoardLayout()
        {
            var canvas = UnityEngine.Object.FindAnyObjectByType<Canvas>();
            var gameRoot = canvas?.transform.Find("Game Root") as RectTransform;
            var middle = gameRoot?.Find("Middle Area") as RectTransform;
            var battlefield = middle?.Find("Battlefield Panel") as RectTransform;
            var actions = middle?.Find("Action Panel") as RectTransform;
            var actionViewport = actions?.Find("Action Viewport") as RectTransform;
            var actionContent = actionViewport?.Find("Action Content") as RectTransform;
            var actionScroll = actions?.GetComponent<ScrollRect>();

            if (gameRoot == null || middle == null || battlefield == null || actions == null
                || actionViewport == null || actionContent == null || actionScroll == null)
            {
                Check("the battlefield and controls have a measurable layout", false);
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(gameRoot);
            Canvas.ForceUpdateCanvases();

            Check("the battlefield receives the larger flex share",
                  battlefield.rect.width > actions.rect.width
                  && battlefield.rect.width >= 360f,
                  $"battlefield {battlefield.rect.width:0}, actions {actions.rect.width:0}");
            // Deliberately narrow now: the compounds are the main event, and the
            // side panel only has to fit one column of controls. Its buttons size
            // themselves to it rather than assuming a fixed width.
            Check("the action panel keeps a readable responsive width",
                  actions.rect.width >= 230f,
                  $"{actions.rect.width:0} wide");
            Check("the action panel scrolls vertically inside a clipped viewport",
                  actionScroll.vertical
                  && !actionScroll.horizontal
                  && actionViewport.GetComponent<RectMask2D>() != null
                  && actionScroll.viewport == actionViewport
                  && actionScroll.content == actionContent,
                  $"viewport {actionViewport.rect.width:0}x{actionViewport.rect.height:0}");

            var battlefieldBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(middle, battlefield);
            var actionBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(middle, actions);
            Check("the battlefield and controls remain inside the middle frame",
                  battlefieldBounds.min.x >= middle.rect.xMin - 0.1f
                  && actionBounds.max.x <= middle.rect.xMax + 0.1f,
                  $"frame {middle.rect.xMin:0}..{middle.rect.xMax:0}, " +
                  $"content {battlefieldBounds.min.x:0}..{actionBounds.max.x:0}");
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
