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

namespace Indoctrination.Tests
{
    /// <summary>
    /// Stands the multiplayer game up for real - a running NetworkManager, a
    /// spawned NetworkGameManager, RPCs going over the wire - and plays through
    /// it. Everything else in the test suite runs against the rules engine
    /// directly; this is the only place the networking itself is exercised.
    ///
    /// Runs as a host, which is a server and a client in one process. That covers
    /// the whole request/apply/broadcast path: an RPC really is sent, the server
    /// really does run the rules, and a filtered view really does come back.
    /// </summary>
    public class MultiplayerTests
    {
        private NetworkManager _network;
        private NetworkGameManager _manager;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            var networkObject = new GameObject("NetworkManager");
            _network = networkObject.AddComponent<NetworkManager>();

            var transport = networkObject.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", 7787, "127.0.0.1");

            _network.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = transport,
                ConnectionApproval = false,
                EnableSceneManagement = false
            };

            // The manager needs a NetworkObject and has to be registered as a
            // spawnable prefab before the host starts, or Netcode will not know
            // how to bring it into existence.
            var managerObject = new GameObject("Game Manager");
            var managerNetworkObject = managerObject.AddComponent<NetworkObject>();
            managerObject.AddComponent<NetworkGameManager>();
            managerObject.SetActive(false);

            _network.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = managerObject });

            Assert.IsTrue(_network.StartHost(), "the host should start");

            managerObject.SetActive(true);
            managerNetworkObject.Spawn();
            _manager = managerObject.GetComponent<NetworkGameManager>();

            yield return WaitForFrames(5);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
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

        [UnityTest]
        public IEnumerator TheHostIsSeatedAndSeesTheLobby()
        {
            Assert.IsTrue(_network.IsHost, "the host should be running");
            Assert.IsNotNull(_manager, "the game manager should have spawned");

            yield return WaitForFrames(3);

            Assert.IsNotNull(_manager.Lobby, "the lobby should have reached this client");
            Assert.AreEqual(1, _manager.Lobby.playerNames.Length, "the host should hold a seat");
        }

        [UnityTest]
        public IEnumerator AGameWillNotStartBelowTheMinimumTable()
        {
            _manager.RequestStartGameRpc();
            yield return WaitForFrames(5);

            Assert.IsNull(_manager.View, "one player is not a table");
            Assert.IsNotNull(_manager.LastError, "and the host should be told why");
        }

        /// <summary>
        /// The full round trip: a request goes out as an RPC, the server runs the
        /// rules, and a view built for this player comes back. Everything the
        /// game does travels this path, so if it works the plumbing is sound.
        /// </summary>
        [UnityTest]
        public IEnumerator RequestsReachTheServerAndViewsComeBack()
        {
            yield return StartTwoPlayerGame();

            Assert.IsNotNull(_manager.View, "a game view should have arrived");
            Assert.AreEqual(0, _manager.View.viewerPlayerId, "built for the host");
            Assert.AreEqual(2, _manager.View.players.Length, "with both players in it");
            Assert.AreEqual(nameof(TurnPhase.Draft), _manager.View.phase, "and the game open at the draft");
            Assert.Greater(_manager.View.draftZone.Length, 0, "with cards on offer");

            // First drafter is randomized. Advance legitimate placeholder-seat
            // picks until the host genuinely owns the next RPC-backed pick.
            var game = ServerGame();
            while (_manager.View.currentDrafterId != _manager.View.viewerPlayerId)
            {
                var drafter = game.CurrentDrafterId.Value;
                var opponentCard = game.DraftZone[0].InstanceId;
                ApplyAsHost(_ => game.DraftCard(drafter, opponentCard));
                yield return WaitForFrames(2);
            }

            var beforeCount = _manager.View.Viewer.hand.Length;
            var card = _manager.View.draftZone[0].instanceId;

            _manager.RequestDraftRpc(card);
            yield return WaitForFrames(5);

            Assert.AreEqual(beforeCount + 1, _manager.View.Viewer.hand.Length,
                            "the drafted card should be in hand");
            Assert.IsFalse(_manager.View.draftZone.Any(c => c.instanceId == card),
                           "and gone from the zone");
        }

        /// <summary>
        /// The server is the only authority. A request the rules refuse must come
        /// back as a message to the one player who tried it, with the game
        /// untouched - not as a desynced table.
        /// </summary>
        [UnityTest]
        public IEnumerator IllegalRequestsAreRefusedWithoutChangingTheGame()
        {
            yield return StartTwoPlayerGame();

            var handBefore = _manager.View.Viewer.hand.Length;
            var zoneBefore = _manager.View.draftZone.Length;

            // Buying is a Buy-phase action, and the game is in the draft.
            _manager.RequestBuyRpc(_manager.View.draftZone[0].instanceId);
            yield return WaitForFrames(5);

            Assert.IsNotNull(_manager.LastError, "the player should be told it was refused");
            Assert.AreEqual(handBefore, _manager.View.Viewer.hand.Length, "hand unchanged");
            Assert.AreEqual(zoneBefore, _manager.View.draftZone.Length, "draft zone unchanged");
        }

        /// <summary>
        /// Exact regression for the playable handoff: after the sixth two-player
        /// pick the three leftovers are discarded, the replicated view is Rolling,
        /// and each seat rolls only its own die.
        /// </summary>
        [UnityTest]
        public IEnumerator FinishingDraftEntersRollingAndEachSeatRollsIndividually()
        {
            yield return StartTwoPlayerGame();

            var game = ServerGame();
            while (game.Phase == TurnPhase.Draft)
            {
                var drafter = game.CurrentDrafterId.Value;
                var card = game.DraftZone[0].InstanceId;
                ApplyAsHost(_ => game.DraftCard(drafter, card));
                yield return WaitForFrames(2);
            }

            Assert.AreEqual(nameof(TurnPhase.Rolling), _manager.View.phase,
                            "the final pick must replicate Rolling");
            Assert.AreEqual(0, _manager.View.draftZone.Length,
                            "the three leftovers leave the draft zone");
            Assert.AreEqual(GameSettings.UndraftedCardsDiscarded, _manager.View.discardCount,
                            "the three leftovers are discarded");
            Assert.IsFalse(_manager.View.Viewer.hasRolled,
                           "the host should receive its own Roll Die action");

            _manager.RequestRollRpc();
            yield return WaitForFrames(3);

            Assert.IsTrue(_manager.View.Viewer.hasRolled, "the host rolled its own die");
            Assert.That(_manager.View.Viewer.primaryDie, Is.InRange(1, GameSettings.DieSides));
            Assert.IsFalse(_manager.View.diceRolled, "the host did not roll for the other seat");

            ApplyAsHost(_ => game.RollPrimaryDie(1));
            yield return WaitForFrames(3);

            Assert.IsTrue(_manager.View.diceRolled, "the table is ready after both seats roll");
            var opponentView = BuildViewFor(1);
            Assert.IsTrue(opponentView.Viewer.hasRolled, "the opponent perspective owns its roll result");
            Assert.That(opponentView.Viewer.primaryDie, Is.InRange(1, GameSettings.DieSides));
        }

        /// <summary>
        /// Hands are hidden information. The host's own view must carry its own
        /// cards and nobody else's, however many are actually in play.
        /// </summary>
        [UnityTest]
        public IEnumerator TheViewNeverCarriesAnotherPlayersHand()
        {
            yield return StartTwoPlayerGame();

            // Draft a few cards so the hands are not all empty.
            for (var i = 0; i < 4 && _manager.View.currentDrafterId >= 0; i++)
            {
                if (_manager.View.currentDrafterId == _manager.View.viewerPlayerId)
                {
                    _manager.RequestDraftRpc(_manager.View.draftZone[0].instanceId);
                    yield return WaitForFrames(3);
                }
                else
                {
                    // Seat 1 has no real client behind it in this test, so its
                    // picks are made server-side by the phase timeout in play.
                    break;
                }
            }

            var view = _manager.View;
            foreach (var player in view.players)
            {
                if (player.playerId == view.viewerPlayerId)
                {
                    Assert.AreEqual(player.handCount, player.hand.Length,
                                    "the viewer should see their own hand in full");
                }
                else
                {
                    Assert.AreEqual(0, player.hand.Length,
                                    $"{player.name}'s cards must not travel to another player");
                }
            }
        }

        /// <summary>
        /// Seats a second player server-side and starts the game. A real second
        /// client would need a second process; what matters for these tests is
        /// that the server has a full table and the RPC path is live.
        /// </summary>
        private IEnumerator StartTwoPlayerGame()
        {
            _manager.AddTestSeat("Test Opponent");
            _manager.RequestStartGameRpc();
            yield return WaitForFrames(6);
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

        private GameView BuildViewFor(int playerId)
        {
            var method = typeof(NetworkGameManager).GetMethod(
                "BuildViewFor", BindingFlags.Instance | BindingFlags.NonPublic);
            return (GameView)method.Invoke(_manager, new object[] { playerId });
        }
    }
}
