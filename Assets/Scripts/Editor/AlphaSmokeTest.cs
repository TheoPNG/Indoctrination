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

                DriveViewsThroughTheBoard(board);
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
