using System.Collections;
using System.Linq;
using System.Reflection;
using Indoctrination.Core;
using Indoctrination.Core.Effects;
using Indoctrination.Net;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.EventSystems;
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
            Assert.IsNotNull(roll, WhyUnusable("ROLL DIE"));
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

            // Timers are off unless the host asks for them, and nothing is ever
            // taken for a player without one running.
            _manager.RequestSetTimersRpc(true);
            yield return WaitForFrames(2);
            Assert.IsTrue(_manager.View.timersEnabled, "the host turned the clocks on");

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
        /// Collecting the turn's free resources, done the way a player does it:
        /// press a colour, press another, and expect the resources to arrive.
        /// </summary>
        [UnityTest]
        public IEnumerator PressingColourButtonsCollectsResources()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Resource);

            Assert.AreEqual(nameof(TurnPhase.Resource), _manager.View.phase);
            Assert.IsFalse(_manager.View.Viewer.collectedResources, "nothing collected yet");

            var before = TotalResources(_manager.View.Viewer);

            for (var i = 0; i < GameSettings.ResourcesPerTurn; i++)
            {
                var button = FindButtonNamed("Red Slot");
                Assert.IsNotNull(button, $"pick {i + 1}: {WhyButtonNamedUnusable("Red Slot")}");
                Assert.IsTrue(button.interactable, "and it has to be clickable");

                button.onClick.Invoke();
                yield return WaitForFrames(3);
            }

            Assert.IsTrue(_manager.View.Viewer.collectedResources,
                          "picking the full allowance has to collect them");
            Assert.AreEqual(before + GameSettings.ResourcesPerTurn, TotalResources(_manager.View.Viewer),
                            "and the resources have to actually arrive");
        }

        /// <summary>Recycling a card from hand, by pressing the button on it.</summary>
        [UnityTest]
        public IEnumerator PressingRecycleTradesACardForAResource()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);
            yield return ExpandHand();

            var you = _manager.View.Viewer;
            Assert.Greater(you.hand.Length, 0, "there should be cards in hand to recycle");

            var handBefore = you.hand.Length;
            var resourcesBefore = TotalResources(you);

            var recycle = FindButtonLabelled("Recycle");
            Assert.IsNotNull(recycle, WhyUnusable("Recycle"));

            recycle.onClick.Invoke();
            yield return WaitForFrames(4);

            Assert.AreEqual(handBefore - 1, _manager.View.Viewer.hand.Length,
                            "recycling has to remove the card from hand");
            Assert.AreEqual(resourcesBefore + 1, TotalResources(_manager.View.Viewer),
                            "and pay a resource for it");
        }

        /// <summary>Buying a card from hand, by dragging it onto the battlefield.</summary>
        [UnityTest]
        public IEnumerator DraggingAHandCardOntoTheBoardBuysIt()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);

            // Give the player enough of everything that something in hand is affordable.
            var game = ServerGame();
            ApplyAsHost(_ =>
            {
                foreach (var color in EffectContext.AllColors)
                {
                    game.Players[0].Resources.Add(color, 8);
                }
            });
            yield return WaitForFrames(2);
            yield return ExpandHand();

            var compoundBefore = _manager.View.Viewer.compound.Length;
            var handBefore = _manager.View.Viewer.hand.Length;

            // Only an affordable card is draggable at all - that is the "lit up"
            // a player is told to look for.
            var handle = Object.FindObjectsByType<DragHandle>(FindObjectsSortMode.None)
                .FirstOrDefault(h => h.GetComponent<BoardCardView>() != null
                                     && h.GetComponentInParent<ScrollRect>()?.gameObject.name == "Hand Scroll");
            Assert.IsNotNull(handle, "an affordable hand card should be draggable during Buy");

            var battlefieldViewport = (RectTransform)typeof(BoardUI)
                .GetField("_battlefieldViewport", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);
            Assert.IsNotNull(battlefieldViewport, "the board should track its own battlefield viewport");

            var drop = new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(null, battlefieldViewport.position)
            };

            handle.OnBeginDrag(drop);
            handle.OnDrag(drop);
            handle.OnEndDrag(drop);
            yield return WaitForFrames(4);

            Assert.AreEqual(handBefore - 1, _manager.View.Viewer.hand.Length,
                            "dragging a card onto the board has to take it out of hand");
            Assert.IsNull(_manager.LastError, $"and not be refused: {_manager.LastError}");

            // Rituals resolve and go to the discard; everything else stays in play.
            var landed = _manager.View.Viewer.compound.Length > compoundBefore
                         || _manager.View.discardCount > 0;
            Assert.IsTrue(landed, "the card has to go somewhere - the compound or the discard");
        }

        /// <summary>
        /// Text rows inside a card must not sit on top of each other. Long effect
        /// text overflowing its own row draws straight over the row beneath it.
        /// </summary>
        [UnityTest]
        public IEnumerator CardTextRowsDoNotOverlapEachOther()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);
            yield return ExpandHand();

            Canvas.ForceUpdateCanvases();

            var cards = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None);
            Assert.Greater(cards.Length, 0, "there should be cards on screen");

            foreach (var card in cards)
            {
                var rows = card.GetComponentsInChildren<Text>()
                    .Where(t => t.gameObject.activeInHierarchy && !string.IsNullOrEmpty(t.text))
                    .OrderByDescending(t => WorldRect(t.rectTransform).yMax)
                    .ToList();

                foreach (var row in rows)
                {
                    // Text set to overflow draws outside its own rect and lands on
                    // the row beneath. Clipping is what keeps rows apart, so no row
                    // may be allowed to spill in the first place.
                    Assert.AreNotEqual(VerticalWrapMode.Overflow, row.verticalOverflow,
                        $"'{row.text}' can overflow its row and draw over the next one");
                }

                for (var i = 0; i + 1 < rows.Count; i++)
                {
                    var above = WorldRect(rows[i].rectTransform);
                    var below = WorldRect(rows[i + 1].rectTransform);

                    Assert.LessOrEqual(below.yMax, above.yMin + 0.5f,
                        $"'{rows[i + 1].text}' overlaps '{rows[i].text}' on card " +
                        $"'{rows.First().text}' (rows {above} and {below})");
                }
            }
        }

        /// <summary>
        /// A player has to be able to see what they are holding, or they cannot
        /// tell what they can afford and resource management is guesswork.
        /// </summary>
        [UnityTest]
        public IEnumerator YourResourcesAreShownOnScreenAndKeepUpToDate()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Resource);
            Canvas.ForceUpdateCanvases();

            var row = FindVisibleResourceRow();
            Assert.IsNotNull(row, "your own resources have to be visible somewhere on the board");
            StringAssert.Contains("R", row, "and show every colour");

            // Collect a known amount and watch the row follow.
            var game = ServerGame();
            ApplyAsHost(_ => game.CollectResources(
                0, Enumerable.Repeat(ResourceColor.Green, GameSettings.ResourcesPerTurn).ToList()));
            yield return WaitForFrames(3);
            Canvas.ForceUpdateCanvases();

            var updated = FindVisibleResourceRow();
            Assert.IsNotNull(updated, "the row must still be on screen after collecting");
            StringAssert.Contains($"G {GameSettings.ResourcesPerTurn}", updated,
                                  $"the pips should show the collected Green. They read: '{updated}'");
        }

        /// <summary>The text as a player reads it, with the colour markup taken out.</summary>
        private static string WithoutMarkup(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text, "<.*?>", "");

        /// <summary>
        /// The viewer's own resources, as a player would read them off the
        /// permanent left-side HUD: the colour's initial from its slot, then
        /// the count inside it.
        /// </summary>
        private string FindVisibleResourceRow()
        {
            var hud = Object.FindAnyObjectByType<ResourceHud>();
            if (hud == null)
            {
                return null;
            }

            var parts = new System.Collections.Generic.List<string>();
            foreach (Transform slot in hud.transform)
            {
                var count = slot.GetComponentInChildren<Text>();
                if (count == null || !IsFullyVisibleThroughEveryMask(count))
                {
                    return null;
                }

                parts.Add($"{slot.name[..1]} {count.text}");
            }

            return parts.Count == 0 ? null : string.Join("  ", parts);
        }


        /// <summary>
        /// Every draft card has to be on screen at once. Choosing from a draft you
        /// can only see a third of is not a choice.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryDraftCardIsOnScreenAtOnce()
        {
            yield return StartGame();
            yield return WaitForFrames(3);
            Canvas.ForceUpdateCanvases();

            var expected = _manager.View.draftZone.Length;
            Assert.Greater(expected, 0, "the draft should have cards in it");

            var onScreen = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .Count(card => IsFullyVisibleThroughEveryMask(card.GetComponent<Image>()));

            Assert.GreaterOrEqual(onScreen, expected,
                $"only {onScreen} of {expected} draft cards are fully on screen");
        }

        /// <summary>
        /// Clicking any card opens its preview, so a card too small to read on the
        /// board can still be read. This is what makes shrinking them acceptable.
        /// </summary>
        [UnityTest]
        public IEnumerator ClickingACardOpensItsPreview()
        {
            yield return StartGame();
            yield return WaitForFrames(3);

            Assert.IsFalse(CardPreview.IsOpen, "nothing should be previewed to begin with");

            var card = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .FirstOrDefault(c => c.Definition != null);
            Assert.IsNotNull(card, "there should be a recognisable card on the board");

            card.GetComponent<Button>().onClick.Invoke();
            yield return WaitForFrames(2);

            Assert.IsTrue(CardPreview.IsOpen, "clicking a card has to open its preview");

            var shown = Object.FindObjectsByType<Text>(FindObjectsSortMode.None)
                .Any(t => t.text == card.Definition.Title && t.fontSize >= 24);
            Assert.IsTrue(shown, $"the preview should show '{card.Definition.Title}' at a readable size");

            CardPreview.Hide();
            yield return WaitForFrames(2);
            Assert.IsFalse(CardPreview.IsOpen, "and close again");
        }

        /// <summary>
        /// The control that ends a phase must never be scrolled or covered away,
        /// including with the hand tray open over the bottom of the board.
        /// </summary>
        [UnityTest]
        public IEnumerator ReadyStaysReachableWithTheHandOpen()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);
            yield return ExpandHand();

            Assert.IsNotNull(FindButtonLabelled("Ready") ?? FindButtonLabelled("Not Ready"),
                             $"Ready has to stay reachable with the hand open: {WhyUnusable("Ready")}");
        }

        /// <summary>
        /// Taking the last resource finishes the phase by itself. The player asked
        /// for one interaction, not a pick followed by a confirmation.
        /// </summary>
        [UnityTest]
        public IEnumerator TakingTheLastResourceEndsThePhaseWithoutConfirming()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Resource);

            var game = ServerGame();

            // The other seat has no client behind it, so its collection is done
            // server-side - including the ready-up its own request would have
            // triggered, so the table is genuinely waiting only on the host.
            ApplyAsHost(_ =>
            {
                game.CollectResources(
                    1, Enumerable.Repeat(ResourceColor.Blue, game.ResourcesPerTurnFor(1)).ToList());
                game.SetReady(1, true);
            });
            yield return WaitForFrames(2);

            for (var i = 0; i < GameSettings.ResourcesPerTurn; i++)
            {
                var disc = FindButtonNamed("Red Slot");
                Assert.IsNotNull(disc, $"pick {i + 1}: {WhyButtonNamedUnusable("Red Slot")}");
                disc.onClick.Invoke();
                yield return WaitForFrames(3);
            }

            Assert.AreNotEqual(nameof(TurnPhase.Resource), _manager.View.phase,
                               "picking the last resource should move the game on by itself, "
                               + "with no separate confirmation step");
        }


        /// <summary>
        /// Every compound has to be on screen at once, not just the draft. Planning
        /// against an opponent's board is impossible if you cannot see it.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryCompoundIsOnScreenAtOnce()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);

            // Put a real spread of cards into both compounds.
            var game = ServerGame();
            ApplyAsHost(_ =>
            {
                foreach (var player in game.Players)
                {
                    foreach (var card in player.Hand.ToList())
                    {
                        player.Hand.Remove(card);
                        player.Compound.Add(card);
                    }
                }
            });

            yield return WaitForFrames(3);
            Canvas.ForceUpdateCanvases();

            var expected = _manager.View.players.Sum(p => p.compound.Length);
            Assert.Greater(expected, 0, "both compounds should hold cards");

            var onScreen = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .Count(card => IsFullyVisibleThroughEveryMask(card.GetComponent<Image>()));

            Assert.GreaterOrEqual(onScreen, expected,
                $"only {onScreen} of {expected} compound cards are fully on screen");
        }

        /// <summary>
        /// Dragging a unit in your own compound decides which of your units
        /// fires first - the whole point of letting a player reorder them.
        /// </summary>
        [UnityTest]
        public IEnumerator DraggingAUnitInYourCompoundReordersIt()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);

            var units = CardDatabase.Instance.All.Where(c => c.Type == CardType.Unit).Take(2).ToList();
            Assert.AreEqual(2, units.Count, "need at least two unit cards in the database to test reordering");

            var game = ServerGame();
            var first = new CardInstance(-9001, units[0]);
            var second = new CardInstance(-9002, units[1]);
            ApplyAsHost(_ =>
            {
                game.Players[0].Compound.Add(first);
                game.Players[0].Compound.Add(second);
            });
            yield return WaitForFrames(3);
            Canvas.ForceUpdateCanvases();

            Assert.AreEqual(0,
                _manager.View.Viewer.compound.ToList().FindIndex(c => c.instanceId == first.InstanceId),
                "the first unit should start at the front");

            // Drag the second unit onto the first one's cell to move it ahead.
            var secondHandle = Object.FindObjectsByType<DragHandle>(FindObjectsSortMode.None)
                .FirstOrDefault(h => h.GetComponent<BoardCardView>()?.Card?.instanceId == second.InstanceId);
            Assert.IsNotNull(secondHandle, "the second unit in your own compound should be draggable");

            var firstCardView = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .First(c => c.Card?.instanceId == first.InstanceId);

            var drop = new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(null, firstCardView.transform.position)
            };

            secondHandle.OnBeginDrag(drop);
            secondHandle.OnDrag(drop);
            secondHandle.OnEndDrag(drop);
            yield return WaitForFrames(4);

            var order = _manager.View.Viewer.compound.ToList();
            Assert.Less(
                order.FindIndex(c => c.instanceId == second.InstanceId),
                order.FindIndex(c => c.instanceId == first.InstanceId),
                "dragging the second unit onto the first should move it ahead in activation order");
        }

        /// <summary>
        /// The Ready button must not invite a press that the rules would refuse.
        /// While the player still owes the phase something, it is disabled.
        /// </summary>
        [UnityTest]
        public IEnumerator ReadyIsDisabledUntilThePhaseHasBeenDealtWith()
        {
            yield return StartGame();

            var game = ServerGame();
            while (game.Phase == TurnPhase.Draft)
            {
                var drafter = game.CurrentDrafterId.Value;
                var card = game.DraftZone[0].InstanceId;
                ApplyAsHost(_ => game.DraftCard(drafter, card));
                yield return WaitForFrames(2);
            }

            Assert.AreEqual(nameof(TurnPhase.Rolling), _manager.View.phase);
            Assert.IsFalse(_manager.View.Viewer.hasRolled);

            var ready = FindButtonAnywhere("Ready");
            Assert.IsNotNull(ready, "the Ready control should be present during play");
            Assert.IsFalse(ready.interactable, "and disabled while the player still has to roll");

            FindButtonLabelled("ROLL DIE").onClick.Invoke();
            yield return WaitForFrames(4);

            Assert.IsTrue(FindButtonAnywhere("Ready").interactable,
                          "and enabled once there is nothing left to do but agree");
        }

        /// <summary>
        /// The discard is public information and Rituals fly into it, so it has to
        /// be somewhere a player can actually look.
        /// </summary>
        [UnityTest]
        public IEnumerator TheDiscardPileCanBeOpenedAndRead()
        {
            yield return StartGame();

            var game = ServerGame();
            while (game.Phase == TurnPhase.Draft)
            {
                var drafter = game.CurrentDrafterId.Value;
                var card = game.DraftZone[0].InstanceId;
                ApplyAsHost(_ => game.DraftCard(drafter, card));
                yield return WaitForFrames(2);
            }

            // The draft leaves its three undrafted cards in the discard.
            Assert.Greater(_manager.View.discardPile.Length, 0, "the discard should not be empty");

            var before = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None).Length;

            var discard = FindButtonLabelled("Discard");
            Assert.IsNotNull(discard, WhyUnusable("Discard"));
            discard.onClick.Invoke();
            yield return WaitForFrames(3);

            var after = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None).Length;
            Assert.Greater(after, before, "opening the discard should put its cards on the board");
        }


        /// <summary>
        /// A player has to be able to see the dice, or Try Again and Baal are
        /// decisions about a number they were never shown.
        /// </summary>
        [UnityTest]
        public IEnumerator RolledDiceAreShownOnScreen()
        {
            yield return StartGame();

            var game = ServerGame();
            while (game.Phase == TurnPhase.Draft)
            {
                var drafter = game.CurrentDrafterId.Value;
                var card = game.DraftZone[0].InstanceId;
                ApplyAsHost(_ => game.DraftCard(drafter, card));
                yield return WaitForFrames(2);
            }

            Assert.IsNull(FindVisibleDieFace(), "no die should show before rolling");

            FindButtonLabelled("ROLL DIE").onClick.Invoke();
            yield return WaitForFrames(4);
            Canvas.ForceUpdateCanvases();

            var face = FindVisibleDieFace();
            Assert.IsNotNull(face, "the die you rolled has to be visible somewhere");
            Assert.AreEqual(_manager.View.Viewer.primaryDie.ToString(), face,
                            "and show the face the server actually rolled");
        }

        /// <summary>
        /// A discounted card shows what it really costs, and a card you can
        /// afford is marked, so a hand can be read without pricing each card.
        /// </summary>
        [UnityTest]
        public IEnumerator DiscountsAndAffordableCardsAreMarked()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);

            var game = ServerGame();
            ApplyAsHost(_ =>
            {
                // A stone in play discounts everything in hand.
                game.Players[0].Compound.Add(new CardInstance(
                    -70, CardDatabase.Instance.Get(CardIds.Wealthstone)));

                foreach (var color in EffectContext.AllColors)
                {
                    game.Players[0].Resources.Add(color, 8);
                }
            });

            yield return WaitForFrames(3);

            var hand = _manager.View.Viewer.hand;
            Assert.Greater(hand.Length, 0, "there should be cards in hand");
            Assert.IsTrue(hand.Any(c => c.canAfford), "with eight of everything, something is affordable");

            var discounted = hand.FirstOrDefault(c => c.isDiscounted);
            if (discounted != null)
            {
                Assert.IsNotEmpty(discounted.costForYou,
                                  "a discounted card has to say what it now costs");
            }
        }

        /// <summary>The Activation phase has no player input, so it closes itself.</summary>
        [UnityTest]
        public IEnumerator ActivationClosesItselfWithoutAReadyPress()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Activation);

            Assert.AreEqual(nameof(TurnPhase.Activation), _manager.View.phase);

            // Activation keeps its own short dwell whether or not the clocks are
            // running - nothing there is a player's move, so there is nobody to
            // wait for. Wind that clock rather than the phase one.
            ExpireActivationDwell();
            yield return WaitForFrames(4);

            Assert.AreNotEqual(nameof(TurnPhase.Activation), _manager.View.phase,
                               "activation resolves itself and should not wait to be confirmed");
        }

        /// <summary>The face on the viewer's own die box, if a player could see it.</summary>
        private string FindVisibleDieFace()
        {
            var bar = Object.FindObjectsByType<StatBar>(FindObjectsSortMode.None)
                .FirstOrDefault(b => (b.GetComponentInChildren<Text>()?.text ?? "").Contains("(you)"));

            var box = bar == null ? null : bar.transform.Find("Die");
            if (box == null || !box.gameObject.activeInHierarchy)
            {
                return null;
            }

            var face = box.GetComponentInChildren<Text>();
            return face != null && IsFullyVisibleThroughEveryMask(face) ? face.text : null;
        }


        /// <summary>
        /// The health and follower bars have to actually fill and empty, not just
        /// look like bars. Unity's Image discards fillAmount entirely when it has
        /// no sprite, so a bar without one renders full at every value - which is
        /// exactly how these shipped until this test existed.
        /// </summary>
        [UnityTest]
        public IEnumerator HealthAndFollowerBarsReallyFill()
        {
            yield return StartGame();
            yield return WaitForFrames(3);

            var bar = Object.FindObjectsByType<StatBar>(FindObjectsSortMode.None)
                .FirstOrDefault(b => (b.GetComponentInChildren<Text>()?.text ?? "").Contains("(you)"));
            Assert.IsNotNull(bar, "the viewer should have a stat bar");

            foreach (var name in new[] { "Health", "Followers" })
            {
                // Health lives inside a row it shares with the Block box.
                var fill = FindFill(bar, name);
                Assert.IsNotNull(fill, $"the {name} bar needs a fill");
                Assert.IsNotNull(fill.sprite,
                    $"the {name} fill has no sprite, so Unity ignores fillAmount and it renders full always");
                Assert.AreEqual(Image.Type.Filled, fill.type, $"the {name} fill must be a Filled image");
            }

            // Drive health down and watch the bar follow it.
            var game = ServerGame();
            var healthFill = FindFill(bar, "Health");
            var before = healthFill.fillAmount;

            ApplyAsHost(_ => game.DealDamage(null, game.Players[0], 10));
            yield return WaitForFrames(3);

            // The bar eases toward its target, so wait for it to arrive rather
            // than sampling it mid-slide.
            var expected = (float)_manager.View.Viewer.health / GameSettings.MaxHealth;
            var waited = 0f;

            while (Mathf.Abs(healthFill.fillAmount - expected) > 0.02f && waited < 4f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            Assert.Less(healthFill.fillAmount, before,
                        $"losing 10 health should empty the bar; it sat at {healthFill.fillAmount}");
            Assert.AreEqual(expected, healthFill.fillAmount, 0.02f,
                            $"and settle at the fraction of health left after {waited:0.00}s");
        }

        /// <summary>A stat bar's fill by name, wherever it sits in the bar.</summary>
        private static Image FindFill(StatBar bar, string name) =>
            bar.GetComponentsInChildren<Image>(includeInactive: true)
                .FirstOrDefault(image => image.name == $"{name} Fill");


        /// <summary>
        /// Resigning ends your game with no way back, so it must never be one
        /// click away - the first press only arms it.
        /// </summary>
        [UnityTest]
        public IEnumerator ResigningTakesTwoPresses()
        {
            yield return StartGame();
            yield return WaitForFrames(3);

            var resign = FindButtonLabelled("Resign");
            Assert.IsNotNull(resign, WhyUnusable("Resign"));

            resign.onClick.Invoke();
            yield return WaitForFrames(3);

            Assert.IsTrue(_manager.View.Viewer.isAlive,
                          "one press must not resign - it only asks for confirmation");
            Assert.IsNotNull(FindButtonLabelled("Sure?"), "and the button should ask");

            FindButtonLabelled("Sure?").onClick.Invoke();
            yield return WaitForFrames(4);

            Assert.IsFalse(_manager.View.Viewer.isAlive, "confirming resigns");
            Assert.IsTrue(_manager.View.Viewer.hasResigned, "recorded as giving up");
        }

        /// <summary>
        /// A draw needs the whole table, so one player offering does not end
        /// anything - the button just reports how many have agreed.
        /// </summary>
        [UnityTest]
        public IEnumerator OfferingADrawWaitsForEverybody()
        {
            yield return StartGame();
            yield return WaitForFrames(3);

            var offer = FindButtonLabelled("Offer draw");
            Assert.IsNotNull(offer, WhyUnusable("Offer draw"));

            offer.onClick.Invoke();
            yield return WaitForFrames(4);

            Assert.IsFalse(_manager.View.isGameOver,
                           "one player offering a draw must not end the game");
            Assert.IsTrue(_manager.View.Viewer.offeringDraw, "but the offer stands");
            Assert.IsNotNull(FindButtonLabelled("Draw 1/2"),
                             "and the button says how many have agreed");

            // The other seat agrees, and only then is it a draw.
            var game = ServerGame();
            ApplyAsHost(_ => game.SetDrawOffer(1, true));
            yield return WaitForFrames(4);

            Assert.IsTrue(_manager.View.isGameOver, "everybody agreeing ends it");
            Assert.IsTrue(_manager.View.isDraw, "as a draw");
        }


        /// <summary>
        /// Nothing is taken or answered for a player unless the host has turned
        /// the clocks on. An autopick that arrives without warning is worse than
        /// a game that waits.
        /// </summary>
        [UnityTest]
        public IEnumerator NothingIsTakenForYouWhileTheClocksAreOff()
        {
            yield return StartGame();

            Assert.IsFalse(_manager.View.timersEnabled, "clocks are off unless asked for");

            var zoneBefore = _manager.View.draftZone.Length;
            var drafterBefore = _manager.View.currentDrafterId;

            // However far past any timeout, an untouched draft stays untouched.
            ExpirePhaseClock();
            yield return WaitForFrames(6);

            Assert.AreEqual(zoneBefore, _manager.View.draftZone.Length,
                            "no pick may be taken for anybody with the clocks off");
            Assert.AreEqual(drafterBefore, _manager.View.currentDrafterId,
                            "and the draft stays with whoever it was waiting on");
            Assert.IsEmpty(_timerTextValue(), "with no countdown shown, since nothing is counting down");
        }

        private string _timerTextValue()
        {
            return Object.FindObjectsByType<Text>(FindObjectsSortMode.None)
                .FirstOrDefault(t => t.name == "Timer")?.text ?? "";
        }

        private static int TotalResources(PlayerView player) =>
            player.red + player.green + player.blue + player.yellow;

        /// <summary>Opens the hand tray if it is collapsed, the way its button does.</summary>
        /// <summary>
        /// The hand is hover-based now rather than a button, so a test expands
        /// it the same way BoardUI's own hover handler does - by flipping the
        /// state it drives from, not by simulating a pointer crossing the strip.
        /// </summary>
        private IEnumerator ExpandHand()
        {
            var method = typeof(BoardUI).GetMethod("SetHandExpanded", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(_board, new object[] { true });

            yield return WaitForFrames(3);
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>
        /// Walks the server's game forward to a phase, finishing the draft and
        /// rolling for everybody, so a test can start from the phase it cares about.
        /// </summary>
        private IEnumerator AdvanceTo(TurnPhase target)
        {
            var game = ServerGame();
            var guard = 0;

            while (game.Phase != target && guard++ < 60)
            {
                if (game.Phase == TurnPhase.Draft)
                {
                    var drafter = game.CurrentDrafterId.Value;
                    var card = game.DraftZone[0].InstanceId;
                    ApplyAsHost(_ => game.DraftCard(drafter, card));
                }
                else if (game.Phase == TurnPhase.Rolling && !game.DiceRolled)
                {
                    ApplyAsHost(_ => game.RollPrimaryDice());
                }
                else
                {
                    ApplyAsHost(_ => game.AdvancePhase());
                }

                yield return WaitForFrames(2);
            }

            Assert.AreEqual(target, game.Phase, $"could not reach {target}");
            yield return WaitForFrames(2);
        }

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

        /// <summary>
        /// A button the player could actually press: on screen, not clipped away
        /// by any scroll viewport above it, and interactable. Looking a button up
        /// by name alone finds controls sitting outside the visible panel, which
        /// is how a board the player cannot use passes a test that clicks it.
        /// </summary>
        private static Button FindButtonLabelled(string label)
        {
            return Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
                .FirstOrDefault(button => LabelOf(button) == label
                                          && button.interactable
                                          && button.targetGraphic is Graphic graphic
                                          && IsFullyVisibleThroughEveryMask(graphic));
        }

        /// <summary>The same lookup ignoring visibility, for reporting what went wrong.</summary>
        private static Button FindButtonAnywhere(string label)
        {
            return Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
                .FirstOrDefault(button => LabelOf(button) == label);
        }

        private static string LabelOf(Button button) =>
            button.GetComponentInChildren<Text>(includeInactive: true)?.text ?? "";

        /// <summary>
        /// The same lookup as <see cref="FindButtonLabelled"/>, but by the
        /// GameObject's own name rather than its visible text - for a button
        /// like a resource HUD circle, whose label is a running count rather
        /// than a fixed word.
        /// </summary>
        private static Button FindButtonNamed(string gameObjectName)
        {
            return Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
                .FirstOrDefault(button => button.gameObject.name == gameObjectName
                                          && button.interactable
                                          && button.targetGraphic is Graphic graphic
                                          && IsFullyVisibleThroughEveryMask(graphic));
        }

        private static string WhyButtonNamedUnusable(string gameObjectName)
        {
            var anywhere = Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
                .FirstOrDefault(button => button.gameObject.name == gameObjectName);

            if (anywhere == null)
            {
                return $"no '{gameObjectName}' button was built at all";
            }

            if (!anywhere.interactable)
            {
                return $"'{gameObjectName}' exists but is not interactable";
            }

            var rect = WorldRect((RectTransform)anywhere.transform);
            var masks = string.Join(" ", anywhere.GetComponentsInParent<RectMask2D>()
                .Select(m => $"{m.name}{WorldRect(m.rectTransform)}"));

            return $"'{gameObjectName}' exists at {rect} but is clipped away by a scroll viewport. Masks: {masks}";
        }

        /// <summary>Explains why a control the test needed was not usable.</summary>
        private static string WhyUnusable(string label)
        {
            var anywhere = FindButtonAnywhere(label);
            if (anywhere == null)
            {
                return $"no '{label}' button was built at all";
            }

            if (!anywhere.interactable)
            {
                return $"'{label}' exists but is not interactable";
            }

            var graphic = anywhere.targetGraphic;
            var rect = WorldRect(((RectTransform)anywhere.transform));
            var masks = string.Join(" ", anywhere.GetComponentsInParent<RectMask2D>()
                .Select(m => $"{m.name}{WorldRect(m.rectTransform)}"));

            return $"'{label}' exists at {rect} but is clipped away by a scroll viewport. Masks: {masks}";
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

        private void ExpireActivationDwell()
        {
            var field = typeof(NetworkGameManager).GetField(
                "_activationEnteredAt", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_manager, Time.time - GameSettings.ActivationDwellSeconds - 1f);
        }

        private void ExpirePhaseClock()
        {
            var field = typeof(NetworkGameManager).GetField(
                "_phaseStartedAt", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_manager, Time.time - GameSettings.PhaseTimeoutSeconds - 1f);
        }
    }
}
