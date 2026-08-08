using System.Collections;
using System.Linq;
using System.Reflection;
using Indoctrination.Core;
using Indoctrination.Net;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Indoctrination.Tests
{
    /// <summary>
    /// Drives the board the way a player does: a live host, a real BoardUI
    /// running its own Awake and Update, and state arriving through the network
    /// layer's Changed event rather than being pushed in by hand.
    ///
    /// This exists because the Editor smoke test renders views directly into the
    /// board, which turned out to prove far less than it looked like it did - it
    /// could pass while the actual game showed blank cards and sat stuck in the
    /// draft. Anything asserted here is asserted against what a player would see.
    /// </summary>
    public class BoardRenderTests
    {
        private NetworkManager _network;
        private NetworkGameManager _manager;
        private BoardUI _board;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            var networkObject = new GameObject("NetworkManager");
            _network = networkObject.AddComponent<NetworkManager>();

            var transport = networkObject.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", 7791, "127.0.0.1");

            _network.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                ConnectionApproval = false,
                EnableSceneManagement = false
            };

            var managerObject = new GameObject("Game Manager");
            var managerNetworkObject = managerObject.AddComponent<NetworkObject>();
            managerObject.AddComponent<NetworkGameManager>();
            managerObject.SetActive(false);
            _network.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = managerObject });

            Assert.IsTrue(_network.StartHost(), "the host should start");

            managerObject.SetActive(true);
            managerNetworkObject.Spawn();
            _manager = managerObject.GetComponent<NetworkGameManager>();

            // A real board, building itself through Awake exactly as it does in play.
            _board = new GameObject("Board UI").AddComponent<BoardUI>();

            yield return WaitForFrames(5);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_board != null)
            {
                Object.DestroyImmediate(_board.gameObject);
            }

            foreach (var canvas in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(canvas.gameObject);
            }

            if (_network != null)
            {
                _network.Shutdown();
                yield return WaitForFrames(2);
                Object.DestroyImmediate(_network.gameObject);
            }

            foreach (var leftover in Object.FindObjectsByType<NetworkGameManager>(FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(leftover.gameObject);
            }
        }

        private static IEnumerator WaitForFrames(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Every card on the board must show its title as something a player can
        /// actually read: non-empty text, laid out with real height, and inside
        /// the card that clips it.
        /// </summary>
        [UnityTest]
        public IEnumerator DraftCardsShowTheirTitlesOnScreen()
        {
            yield return StartGame();

            Assert.AreEqual(nameof(TurnPhase.Draft), _manager.View.phase, "the game should open at the draft");

            yield return WaitForFrames(3);
            Canvas.ForceUpdateCanvases();

            var cards = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None);
            Assert.Greater(cards.Length, 0, "the draft zone should have produced card widgets");

            var visibleCards = 0;

            foreach (var card in cards)
            {
                var title = FindTitle(card);
                Assert.IsNotNull(title, "every card needs a title row");
                Assert.IsNotEmpty(title.text, $"card {card.name} rendered an empty title");

                Assert.IsNotNull(title.font, $"'{title.text}' has no font, so nothing draws");

                var titleRect = title.rectTransform.rect;
                Assert.Greater(titleRect.width, 20f, $"'{title.text}' was laid out {titleRect.width} wide");
                Assert.Greater(titleRect.height, 8f, $"'{title.text}' was laid out {titleRect.height} tall");

                Assert.IsTrue(title.enabled && title.gameObject.activeInHierarchy,
                              $"'{title.text}' is switched off");
                Assert.Greater(title.color.a, 0.1f, $"'{title.text}' is transparent");

                // The card clips its own contents, and the scroll strip clips the
                // card - a title has to survive every mask between it and the screen.
                // A card scrolled off the side of its strip is fine - that is what
                // scrolling is for. A card sitting in view with its title sliced
                // off by the strip's top edge is the bug this test exists for.
                if (IsHorizontallyWithinStrip(card))
                {
                    visibleCards++;
                    Assert.IsTrue(IsFullyVisibleThroughEveryMask(title),
                                  $"'{title.text}' is on screen but its title is clipped away");
                }
            }
            Assert.Greater(visibleCards, 0, "at least one draft card has to be on screen to pick from");
        }

        /// <summary>
        /// The exact failure a player reported: the draft finishes and the board
        /// never moves on. Every pick is made through the real RPC, and the
        /// client's own replicated view has to reach Rolling with a working
        /// ROLL DIE button on it.
        /// </summary>
        [UnityTest]
        public IEnumerator FinishingTheDraftGivesTheClientARollButton()
        {
            yield return StartGame();

            var game = ServerGame();
            var guard = 0;

            while (_manager.View.phase == nameof(TurnPhase.Draft) && guard++ < 40)
            {
                var drafter = game.CurrentDrafterId;
                Assert.IsNotNull(drafter, "the draft must always have somebody to pick");

                if (drafter == _manager.View.viewerPlayerId)
                {
                    // The host's own pick goes through the real request path.
                    _manager.RequestDraftRpc(_manager.View.draftZone[0].instanceId);
                }
                else
                {
                    // The other seat has no client in this test, so the server
                    // takes its pick the way the timeout fallback would.
                    ApplyAsHost(_ => game.DraftCard(drafter.Value, game.DraftZone[0].InstanceId));
                }

                yield return WaitForFrames(3);
            }

            Assert.AreEqual(nameof(TurnPhase.Rolling), _manager.View.phase,
                            "finishing the draft must move the client's view to Rolling");

            yield return WaitForFrames(3);
            Canvas.ForceUpdateCanvases();

            Assert.IsFalse(_manager.View.Viewer.hasRolled, "the player has not rolled yet");

            var roll = FindButtonLabelled("ROLL DIE");
            Assert.IsNotNull(roll, "the player needs a Roll Die button once Rolling begins");
            Assert.IsTrue(roll.interactable, "and it has to be clickable");

            var rect = ((RectTransform)roll.transform).rect;
            Assert.Greater(rect.width, 100f, $"the roll button was laid out {rect.width} wide");
            Assert.Greater(rect.height, 20f, $"the roll button was laid out {rect.height} tall");

            // Press it exactly as a player would.
            roll.onClick.Invoke();
            yield return WaitForFrames(4);

            Assert.IsTrue(_manager.View.Viewer.hasRolled, "pressing Roll Die must roll this player's die");
            Assert.GreaterOrEqual(_manager.View.Viewer.primaryDie, 1, "and produce a real face");
            Assert.LessOrEqual(_manager.View.Viewer.primaryDie, GameSettings.DieSides);
        }

        /// <summary>
        /// A draft nobody can finish is the worst kind of stuck, because unlike
        /// every other phase there is no clock running. A seat that never picks
        /// must not be able to hold the table forever.
        /// </summary>
        [UnityTest]
        public IEnumerator ADraftNobodyAdvancesStillTimesOut()
        {
            yield return StartGame();

            Assert.AreEqual(nameof(TurnPhase.Draft), _manager.View.phase);

            var zoneBefore = _manager.View.draftZone.Length;

            // Wind the phase clock back past the timeout rather than waiting it out.
            ExpirePhaseClock();
            yield return WaitForFrames(3);

            Assert.Less(_manager.View.draftZone.Length, zoneBefore,
                        "a pick nobody made has to be taken for them");

            // Each timeout takes one pick, so an entirely abandoned table has to
            // keep moving until the draft is genuinely finished.
            var guard = 0;
            while (_manager.View.phase == nameof(TurnPhase.Draft) && guard++ < 30)
            {
                ExpirePhaseClock();
                yield return WaitForFrames(3);
            }

            Assert.AreEqual(nameof(TurnPhase.Rolling), _manager.View.phase,
                            "an abandoned draft has to finish rather than stop the game for good");
        }

        // ------------------------------------------------------------- Helpers

        /// <summary>
        /// Whether a graphic reaches the screen whole. Every RectMask2D above it
        /// clips what falls outside, and a title half cut off by the top of its
        /// strip is unreadable - so this demands full containment, not just
        /// overlap. Testing for overlap is what let sliced titles pass before.
        /// </summary>
        private static bool IsFullyVisibleThroughEveryMask(Graphic graphic)
        {
            var rect = WorldRect(graphic.rectTransform);

            foreach (var mask in graphic.GetComponentsInParent<RectMask2D>(includeInactive: false))
            {
                var maskRect = WorldRect(mask.rectTransform);
                const float slack = 0.5f;

                if (rect.yMin < maskRect.yMin - slack || rect.yMax > maskRect.yMax + slack
                    || rect.xMin < maskRect.xMin - slack || rect.xMax > maskRect.xMax + slack)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether a card sits inside the strip that scrolls it, side to side.</summary>
        private static bool IsHorizontallyWithinStrip(BoardCardView card)
        {
            var cardRect = WorldRect((RectTransform)card.transform);
            var strip = card.GetComponentInParent<RectMask2D>();

            foreach (var mask in card.GetComponentsInParent<RectMask2D>(includeInactive: false))
            {
                if (mask == strip)
                {
                    continue;
                }

                var maskRect = WorldRect(mask.rectTransform);
                if (cardRect.xMin < maskRect.xMin || cardRect.xMax > maskRect.xMax)
                {
                    return false;
                }
            }

            return true;
        }

        private static Rect WorldRect(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
        }

        private static Text FindTitle(BoardCardView card)
        {
            var titleTransform = card.transform.Find("Title");
            return titleTransform == null ? null : titleTransform.GetComponent<Text>();
        }

        private static Button FindButtonLabelled(string label)
        {
            return Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
                .FirstOrDefault(button =>
                {
                    var text = button.GetComponentInChildren<Text>();
                    return text != null && text.text == label;
                });
        }

        private IEnumerator StartGame()
        {
            _manager.AddTestSeat("Test Opponent");
            _manager.RequestStartGameRpc();
            yield return WaitForFrames(6);
            Assert.IsNotNull(_manager.View, "a game view should have arrived");
        }

        private GameState ServerGame()
        {
            var field = typeof(NetworkGameManager).GetField("_game", BindingFlags.Instance | BindingFlags.NonPublic);
            return (GameState)field.GetValue(_manager);
        }

        private void ApplyAsHost(System.Action<int> operation)
        {
            var method = typeof(NetworkGameManager).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic);
            method.Invoke(_manager, new object[] { default(RpcParams), operation });
        }

        private void ExpirePhaseClock()
        {
            var field = typeof(NetworkGameManager).GetField(
                "_phaseStartedAt", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_manager, Time.time - GameSettings.PhaseTimeoutSeconds - 1f);
        }
    }
}
