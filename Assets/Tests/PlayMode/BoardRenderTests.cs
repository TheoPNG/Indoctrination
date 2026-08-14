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
using UnityEngine.Rendering;
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
        /// Every card on the board must show either its printed face or a title a
        /// player can actually read. The fallback still needs non-empty text laid
        /// out inside the card that clips it; imported art has to survive the same
        /// masks without being stretched or switched off.
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
                var printedFace = card.transform.Find("Printed Face")?.GetComponent<Image>();
                if (printedFace != null && printedFace.gameObject.activeInHierarchy)
                {
                    Assert.IsNotNull(printedFace.sprite, "an active printed face needs a texture");
                    Assert.IsTrue(printedFace.preserveAspect, "printed card art must not be stretched");

                    if (IsHorizontallyWithinStrip(card))
                    {
                        visibleCards++;
                        Assert.IsTrue(IsFullyVisibleThroughEveryMask(printedFace),
                                      $"'{card.Definition?.Title}' is on screen but its printed face is clipped away");
                    }

                    continue;
                }

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
        /// Drafting is a table gesture: pick the card up and put it in your hand.
        /// Clicking remains available for reading, but the preview offers no
        /// second confirmation button.
        /// </summary>
        [UnityTest]
        public IEnumerator DraggingADraftCardToTheHandDraftsIt()
        {
            yield return StartGame();
            yield return WaitForFrames(3);

            var game = ServerGame();
            while (_manager.View.currentDrafterId != _manager.View.viewerPlayerId)
            {
                var drafter = game.CurrentDrafterId.Value;
                ApplyAsHost(_ => game.DraftCard(drafter, game.DraftZone[0].InstanceId));
                yield return WaitForFrames(3);
            }

            var handBefore = _manager.View.Viewer.hand.Length;
            var zoneBefore = _manager.View.draftZone.Length;
            var handle = Object.FindObjectsByType<DragHandle>(FindObjectsSortMode.None)
                .FirstOrDefault(candidate => candidate.GetComponent<BoardCardView>()?.Card != null);
            Assert.IsNotNull(handle, "a legal draft card should be draggable");

            var handRow = GameObject.Find("Hand Row")?.GetComponent<RectTransform>();
            Assert.IsNotNull(handRow, "even an empty hand needs a first-pick drop target");
            Assert.IsTrue(handRow.gameObject.activeInHierarchy, "the hand drop target should be active");

            var dropZone = GameObject.Find("Hand Drop Zone")?.GetComponent<RectTransform>();
            Assert.IsNotNull(dropZone, "a legal pick should expose the draft target");
            Assert.Greater(dropZone.rect.width, 400f, "the draft target should be substantially wider than one card");

            // A flat shelf that fills its zone, not an oversized ellipse clipped
            // down to a semicircle - that read as a blue bubble on a board with
            // no other round shapes on it.
            var dropArc = dropZone.Find("Drop Arc").GetComponent<Image>();
            Assert.IsNull(dropArc.sprite, "the drop target should be a plain band, not a disc");
            Assert.LessOrEqual(dropArc.rectTransform.rect.height, dropZone.rect.height + 0.5f,
                "the band should fit its zone rather than overflow and be clipped");
            Assert.IsNotNull(dropArc.transform.Find("Drop Edge"),
                "the affordance is a lit edge along the top of the shelf");

            var restingGlow = dropArc.color.a;

            var drop = new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(
                    null, dropZone.TransformPoint(new Vector3(0f, dropZone.rect.height * 0.5f, 0f)))
            };
            handle.OnBeginDrag(drop);
            handle.OnDrag(drop);
            Assert.Greater(dropArc.color.a, restingGlow + 0.15f,
                "carrying a draft card over the semicircle should make it light up clearly");
            handle.OnEndDrag(drop);
            yield return WaitForFrames(4);

            Assert.AreEqual(handBefore + 1, _manager.View.Viewer.hand.Length,
                            "dropping the card into the hand should draft it");
            Assert.AreEqual(zoneBefore - 1, _manager.View.draftZone.Length,
                            "and remove it from the draft zone");
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
            Assert.That(rect.width, Is.InRange(240f, 280f),
                        $"the roll button was laid out {rect.width} wide");
            Assert.That(rect.height, Is.InRange(48f, 58f),
                        $"the roll button was laid out {rect.height} tall");

            var popup = (RectTransform)roll.GetComponentInParent<ScrollRect>().transform;
            Assert.LessOrEqual(popup.rect.width, 310f,
                               $"the rolling window should hug its button, but was {popup.rect.width} wide");
            Assert.LessOrEqual(popup.rect.height, 100f,
                               $"the rolling window should hug its button, but was {popup.rect.height} tall");

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

        /// <summary>Recycling a card by dragging it into the shared bin.</summary>
        [UnityTest]
        public IEnumerator DraggingToRecycleTradesACardForAResource()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);
            yield return ExpandHand();

            var you = _manager.View.Viewer;
            Assert.Greater(you.hand.Length, 0, "there should be cards in hand to recycle");

            var handBefore = you.hand.Length;
            var resourcesBefore = TotalResources(you);

            Assert.IsNull(FindButtonLabelled("Recycle"),
                          "recycling should not repeat a button under every card");

            var bin = GameObject.Find("Recycle Bin")?.GetComponent<RectTransform>();
            Assert.IsNotNull(bin, "the Buy hand should expose one recycle bin");
            Assert.IsTrue(bin.gameObject.activeInHierarchy, "the recycle bin should be visible");

            var handle = Object.FindObjectsByType<DragHandle>(FindObjectsSortMode.None)
                .FirstOrDefault(h => h.GetComponent<BoardCardView>() != null
                                     && IsInHand(h));
            Assert.IsNotNull(handle, "every hand card should be draggable to the recycle bin");

            var drop = new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(null, bin.position)
            };

            handle.OnBeginDrag(drop);
            handle.OnDrag(drop);
            handle.OnEndDrag(drop);

            Assert.IsNotNull(GameObject.Find("Pip In Flight"),
                             "recycling should send the card's resource from the bin to the HUD");
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
                                     && h.GetComponent<BoardCardView>().Card.canAfford
                                     && IsInHand(h));
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
                var cardRect = (RectTransform)card.transform;
                var rows = card.GetComponentsInChildren<Text>()
                    .Where(t => t.gameObject.activeInHierarchy && !string.IsNullOrEmpty(t.text))
                    .OrderByDescending(t => RectRelativeTo(t.rectTransform, cardRect).yMax)
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
                    var above = RectRelativeTo(rows[i].rectTransform, cardRect);
                    var below = RectRelativeTo(rows[i + 1].rectTransform, cardRect);

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

            // A card with art is put on the board deliberately rather than hoped
            // for. The draft zone is dealt from a shuffled deck with a clock
            // seed, and art covers only part of the set, so whether an arted card
            // happened to be dealt was a coin toss - this test failed on roughly
            // a third of runs for that reason alone.
            var arted = CardDatabase.Instance.All.FirstOrDefault(d => CardArt.FaceFor(d.Id) != null);
            Assert.IsNotNull(arted, "at least one card needs imported art for this to mean anything");

            var game = ServerGame();
            ApplyAsHost(_ => game.Players[0].Compound.Add(new CardInstance(-777, arted)));
            yield return WaitForFrames(4);

            var card = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .FirstOrDefault(c => c.Card != null && c.Card.instanceId == -777);
            Assert.IsNotNull(card, "the arted card should be rendered on the board");

            card.GetComponent<Button>().onClick.Invoke();
            yield return WaitForFrames(2);

            Assert.IsTrue(CardPreview.IsOpen, "clicking a card has to open its preview");

            var printed = Object.FindObjectsByType<Image>(FindObjectsSortMode.None)
                .FirstOrDefault(image => image.gameObject.name == "Printed Card"
                                         && image.gameObject.activeInHierarchy);
            Assert.IsNotNull(printed, $"the preview should show the printed '{card.Definition.Title}' card");
            Assert.AreSame(CardArt.FaceFor(card.Definition.Id), printed.sprite,
                           "the popup has to use the same printed face as the card");
            Assert.IsTrue(printed.preserveAspect, "the popup must preserve the PDF aspect ratio");
            Assert.That(printed.rectTransform.rect.width / printed.rectTransform.rect.height,
                        Is.EqualTo(5f / 7f).Within(0.001f),
                        "the popup itself should remain 5:7");

            var preview = GameObject.Find("Card Preview");
            Assert.IsNotNull(preview, "the open preview should have a click-away backdrop");
            Assert.IsFalse(preview.GetComponentsInChildren<Text>()
                                   .Any(text => text.text == "Close"),
                           "card previews should not spend space on a Close button");
            Assert.IsFalse(preview.GetComponentsInChildren<Text>()
                                   .Any(text => text.text == "Draft this card"),
                           "reading a draft card should not add a second confirmation button");

            preview.GetComponent<Button>().onClick.Invoke();
            yield return WaitForFrames(2);
            Assert.IsFalse(CardPreview.IsOpen, "clicking outside the card should close it");
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
        /// A die result decorates cards already on the table. It must not destroy
        /// and deal them all again before their activation animation runs.
        /// </summary>
        [UnityTest]
        public IEnumerator RollingADieDoesNotReloadCardsOnTheTable()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Rolling);

            const int instanceId = -8080;
            var definition = CardDatabase.Instance.All.First(card => card.Type == CardType.Unit);
            var game = ServerGame();
            ApplyAsHost(_ => game.Players[0].Compound.Add(new CardInstance(instanceId, definition)));
            yield return WaitForFrames(3);

            var before = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .FirstOrDefault(card => card.Card?.instanceId == instanceId);
            Assert.IsNotNull(before, "the test unit should be visible before rolling");

            FindButtonLabelled("ROLL DIE").onClick.Invoke();
            yield return WaitForFrames(4);

            var after = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .FirstOrDefault(card => card.Card?.instanceId == instanceId);
            Assert.AreSame(before, after,
                           "rolling should update the existing card instead of rebuilding it");
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
            const int discountedInstanceId = -71;
            var printedDefinition = CardDatabase.Instance.All.First(card =>
                card.Color == ResourceColor.Blue
                && CardArt.FaceFor(card.Id) != null
                && card.Cost.Amounts.TryGetValue(ResourceColor.Yellow, out var yellow)
                && yellow > 0);
            ApplyAsHost(_ =>
            {
                // A stone in play discounts everything in hand.
                game.Players[0].Compound.Add(new CardInstance(
                    -70, CardDatabase.Instance.Get(CardIds.Wealthstone)));
                game.Players[0].Hand.Add(new CardInstance(
                    discountedInstanceId, printedDefinition));

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
            Assert.IsNotNull(discounted, "the Wealthstone should discount a card in hand");
            Assert.IsNotEmpty(discounted.costForYou,
                              "a discounted card has to carry its actual cost");

            yield return ExpandHand();
            var printedCard = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .FirstOrDefault(card => card.Card?.instanceId == discountedInstanceId && IsInHand(card));
            Assert.IsNotNull(printedCard, "the deterministic discounted PDF card should be in the hand");

            var stamps = printedCard.transform.Find("Discount Stamps");
            Assert.IsNotNull(stamps, "printed cards need a discount-stamp layer");
            Assert.IsTrue(stamps.gameObject.activeInHierarchy, "a discounted PDF needs a visible stamp");
            var yellowStamp = stamps.Find("Discount Yellow")?.GetComponent<Image>();
            Assert.IsNotNull(yellowStamp, "a Yellow reduction needs a Yellow circled stamp");
            Assert.AreSame(BoardArt.Disc, yellowStamp.sprite, "the -1 stamp should be circular");
            Assert.AreEqual("−1", yellowStamp.GetComponentInChildren<Text>().text);
            Assert.That(yellowStamp.color, Is.EqualTo(BoardArt.ColorOf(ResourceColor.Yellow)));

            printedCard.GetComponent<Button>().onClick.Invoke();
            yield return WaitForFrames(2);
            var enlargedStamp = GameObject.Find("Card Preview")?.transform
                .Find("Printed Card/Discount Stamps/Discount Yellow")
                ?.GetComponent<Image>();
            Assert.IsNotNull(enlargedStamp,
                             "the enlarged PDF should carry the same centered Yellow -1 stamp");
            GameObject.Find("Card Preview").GetComponent<Button>().onClick.Invoke();
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

        /// <summary>
        /// The live server reveals the exact round-robin queue one completion at
        /// a time, while the board locks attention on that card and keeps every
        /// leader's two win/loss tracks visible.
        /// </summary>
        [UnityTest]
        public IEnumerator UnitActivationsArePacedLockedAndRepeatInTableOrder()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Rolling);

            var game = ServerGame();
            game.FirstDrafterIndex = 1;
            var p1Unit = new CardInstance(-501, CardDatabase.Instance.Get(CardIds.SolarPanels));
            var p2Unit = new CardInstance(-502, CardDatabase.Instance.Get(CardIds.MoneyTree));
            var dullBlessing = new CardInstance(-503, CardDatabase.Instance.Get(CardIds.WondrousBlood));
            game.Players[1].Compound.Add(p1Unit);
            game.Players[0].Compound.Add(p2Unit);
            game.Players[0].Compound.Add(dullBlessing);

            ApplyAsHost(_ => game.RollPrimaryDice());
            ApplyAsHost(_ =>
            {
                game.SetPrimaryDie(game.Players[0], 6);
                game.SetPrimaryDie(game.Players[1], 6);
                game.AdvancePhase();
            });
            DelayNextActivation();
            yield return WaitForFrames(2);

            Assert.AreEqual(nameof(TurnPhase.Activation), _manager.View.phase);
            Assert.AreEqual(0, _manager.View.activationCompletedCount,
                "entering Activation must show the queue before resolving its first Unit");
            CollectionAssert.AreEqual(
                // Both dice show the same face, so each Unit fires twice - and
                // takes both firings together. The table still alternates; it is
                // one Unit each, not one activation each. A card that fires twice
                // reads as one card doing its thing twice, rather than the same
                // card reappearing after the opponent has had a turn.
                new[] { p1Unit.InstanceId, p1Unit.InstanceId, p2Unit.InstanceId, p2Unit.InstanceId },
                _manager.View.activations.Select(entry => entry.cardInstanceId).ToArray(),
                "a Unit woken twice should take both firings before the table moves on");

            var boardCards = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                .Where(card => card.Card != null).ToArray();
            var bright = boardCards.Single(card => card.Card.instanceId == p1Unit.InstanceId);
            var dull = boardCards.Single(card => card.Card.instanceId == dullBlessing.InstanceId);
            Assert.Greater(bright.GetComponent<CanvasGroup>().alpha, 0.95f,
                "a Unit still waiting to activate should glow at full strength");
            Assert.Less(dull.GetComponent<CanvasGroup>().alpha, 0.4f,
                "cards outside the activation queue should become dull");

            ExpireNextActivation();
            yield return WaitForFrames(2);

            Assert.AreEqual(1, _manager.View.activationCompletedCount,
                "one server beat must complete one Unit, not drain the queue");
            var stage = GameObject.Find("Activation Stage");
            Assert.IsNotNull(stage, "a completed Unit should open the locked full-screen stage");
            Assert.IsNull(stage.GetComponent<Button>(), "the stage itself must not be dismissible");
            Assert.AreEqual(2, stage.transform.Find("All Player Tracks").childCount,
                "every player's large tracks must remain visible during the animation");
            var stagedCard = stage.transform.Find("Locked Card").GetComponentInChildren<BoardCardView>();
            Assert.IsFalse(stagedCard.GetComponent<Button>().interactable,
                "the full-screen activation card must not open a collapsible preview");

            // The first Unit appears twice. It stays bright after its first turn,
            // then dims only after the third entry spends its duplicate activation.
            Assert.Greater(bright.GetComponent<CanvasGroup>().alpha, 0.95f);
            ExpireNextActivation();
            yield return WaitForFrames(2);
            ExpireNextActivation();
            yield return WaitForFrames(2);
            Assert.AreEqual(3, _manager.View.activationCompletedCount);
            Assert.Less(bright.GetComponent<CanvasGroup>().alpha, 0.4f,
                "a Unit should dim once no repeated activation remains for it");
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

        /// <summary>
        /// The hand opens when the pointer reaches it, stays open while it is
        /// there, and closes once - not repeatedly - when the pointer leaves.
        ///
        /// This exists because the tray shipped twice in a state where it
        /// flickered open and shut every single frame. Both times the cause was
        /// the same shape of mistake: opening the hand rebuilt the cards under
        /// the pointer, the rebuild provoked a pointer event, and that event
        /// closed the hand again. Asserting it is open is not enough - the bug
        /// passes through "open" every other frame. The state has to be stable
        /// across many frames, which is what this measures.
        /// </summary>
        [UnityTest]
        public IEnumerator TheHandOpensOnHoverAndStaysOpen()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);

            var handRow = (RectTransform)typeof(BoardUI)
                .GetField("_handRow", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);
            Assert.IsNotNull(handRow, "the board should have a hand tray");
            Assert.Greater(_manager.View.Viewer.hand.Length, 0, "and cards to put in it");

            // The tray's pivot sits on its bottom edge, so this is a point just
            // inside the sliver that peeks above the bottom of the screen.
            var peek = RectTransformUtility.WorldToScreenPoint(null, handRow.position)
                       + new Vector2(0f, 4f);

            PointAt(peek);
            yield return WaitForFrames(2);

            Assert.IsTrue(HandIsExpanded(),
                $"reaching the hand with the pointer has to open it. aimed at {peek}, "
                + $"row rect {WorldRect(handRow)}, screen {Screen.width}x{Screen.height}");

            // The heart of it: hold the pointer still and count. A tray fighting
            // its own rebuild reads as open on some frames and shut on others,
            // so merely being open once proves nothing.
            const int framesHeld = 30;
            var openFrames = 0;

            for (var i = 0; i < framesHeld; i++)
            {
                PointAt(peek);
                yield return null;

                if (HandIsExpanded())
                {
                    openFrames++;
                }
            }

            Assert.AreEqual(framesHeld, openFrames,
                $"the hand has to stay open while the pointer rests on it - it was open on "
                + $"only {openFrames} of {framesHeld} frames, so it is flickering");

            // And it still closes when the pointer genuinely leaves.
            var away = new Vector2(Screen.width / 2f, Screen.height * 0.8f);
            PointAt(away);
            yield return WaitForFrames(2);

            Assert.IsFalse(HandIsExpanded(), "moving off the hand has to close it again");

            var shutFrames = 0;
            for (var i = 0; i < framesHeld; i++)
            {
                PointAt(away);
                yield return null;

                if (!HandIsExpanded())
                {
                    shutFrames++;
                }
            }

            Assert.AreEqual(framesHeld, shutFrames,
                $"and it has to stay shut - it was shut on only {shutFrames} of {framesHeld} frames");
        }

        /// <summary>
        /// The hand should read as cards held in a fan: no visible tray, larger
        /// overlapping cards, and opposite angles on its two outside edges.
        /// </summary>
        [UnityTest]
        public IEnumerator TheExpandedHandIsAVisibleCardFanWithoutABox()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);
            yield return ExpandHand();

            var handRow = (RectTransform)typeof(BoardUI)
                .GetField("_handRow", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);
            Assert.That(handRow.GetComponent<Image>().color.a, Is.EqualTo(0f).Within(0.001f),
                        "the hand's hover surface must not draw a surrounding box");

            var slots = handRow.Cast<Transform>()
                .Where(child => child.name == "Card Slot")
                .Cast<RectTransform>()
                .OrderBy(slot => slot.anchoredPosition.x)
                .ToArray();
            Assert.GreaterOrEqual(slots.Length, 2, "a drafted hand should contain a fan of cards");

            var displayedWidth = BoardCardView.Width
                                 * slots[0].GetComponentInChildren<BoardCardView>().transform.localScale.x;
            Assert.Greater(displayedWidth, 160f,
                           $"the overlapping hand should let cards stay large, but they were {displayedWidth}");
            Assert.Less(slots[1].anchoredPosition.x - slots[0].anchoredPosition.x, displayedWidth,
                        "adjacent hand cards should overlap");
            Assert.Greater(slots.First().localEulerAngles.z, 0f, "the left card should fan outward");
            Assert.Greater(slots.Last().localEulerAngles.z, 180f,
                           "the right card should fan in the opposite direction");

            foreach (var slot in slots)
            {
                var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(handRow, slot);
                Assert.GreaterOrEqual(bounds.min.x, handRow.rect.xMin - 0.5f,
                    $"{slot.name}'s rotated left corner leaves the hand surface");
                Assert.LessOrEqual(bounds.max.x, handRow.rect.xMax + 0.5f,
                    $"{slot.name}'s rotated right corner leaves the hand surface");
                Assert.GreaterOrEqual(bounds.min.y, handRow.rect.yMin - 0.5f,
                    $"{slot.name}'s rotated bottom corner leaves the hand surface");
                Assert.LessOrEqual(bounds.max.y, handRow.rect.yMax + 0.5f,
                    $"{slot.name}'s rotated top corner would be clipped");
            }
        }

        /// <summary>
        /// A bot actually plays, so one person can run a whole game alone.
        ///
        /// Asserts the turn loop keeps moving rather than any particular
        /// decision: the bot is deliberately witless, and the thing that would
        /// make it useless is not a bad pick but a phase it never leaves. It
        /// drafts, rolls, takes resources and readies up, and the game comes back
        /// round to a second Rolling phase without a human touching it.
        /// </summary>
        [UnityTest]
        public IEnumerator ABotPlaysAWholeTurnByItself()
        {
            _manager.RequestAddBotRpc();
            _manager.RequestStartGameRpc();
            yield return WaitForFrames(6);

            Assert.IsNotNull(_manager.View, "a solo game should start");
            Assert.AreEqual(nameof(TurnPhase.Draft), _manager.View.phase);

            var game = ServerGame();
            var seenPhases = new System.Collections.Generic.HashSet<string>();

            // The human seat still has to be played, so this stands in for one:
            // whenever the game is waiting on seat 0, do the minimum to move on.
            var guard = 0;
            while (guard++ < 3000 && game.Phase != TurnPhase.GameOver)
            {
                seenPhases.Add(game.Phase.ToString());

                if (game.PendingChoice is { AskedOfPlayerId: 0 })
                {
                    ApplyAsHost(_ => game.AnswerPendingChoiceWithDefault());
                }
                else if (game.Phase == TurnPhase.Draft && game.CurrentDrafterId == 0)
                {
                    var card = game.DraftZone.FirstOrDefault();
                    if (card != null)
                    {
                        ApplyAsHost(_ => game.DraftCard(0, card.InstanceId));
                    }
                }
                else if (game.Phase == TurnPhase.Rolling && !game.HasRolled(0))
                {
                    ApplyAsHost(_ => game.RollPrimaryDie(0));
                }
                else if (game.Phase == TurnPhase.Resource && !game.HasCollectedResources(0))
                {
                    ApplyAsHost(_ => game.CollectResources(
                        0, Enumerable.Repeat(ResourceColor.Blue, game.ResourcesPerTurnFor(0)).ToList()));
                }
                else if (game.Phase is TurnPhase.Rolling or TurnPhase.Resource or TurnPhase.Buy
                         && !game.PlayersReady.Contains(0)
                         && game.PendingChoice == null)
                {
                    ApplyAsHost(_ =>
                    {
                        if (game.SetReady(0, true))
                        {
                            game.AdvancePhase();
                        }
                    });
                }

                // Every pause here is presentation pacing measured in wall-clock
                // seconds - the bot's think time, and the beat between
                // activations. Batchmode runs frames far faster than it runs
                // seconds, so they are cleared each iteration. This is testing
                // that a solo game plays itself through, not how long it dwells.
                ClearPacingClocks();

                // Everything else is the bot's move, and it takes it on its own.
                yield return null;

                if (seenPhases.Contains(nameof(TurnPhase.Buy))
                    && game.TurnInRound > 1)
                {
                    break;
                }
            }

            Assert.IsNull(_manager.LastError, $"nothing should have been refused: {_manager.LastError}");

            foreach (var phase in new[] { TurnPhase.Draft, TurnPhase.Rolling, TurnPhase.Resource, TurnPhase.Buy })
            {
                Assert.Contains(phase.ToString(), seenPhases.ToArray(),
                    $"a solo game has to reach {phase} - the bot is stuck before it");
            }

            // And the bot genuinely acted rather than the human seat carrying it.
            // Drafting is the clearest proof: the draft cannot advance until it
            // takes its own picks, so cards in its hand or compound could only
            // have got there by the bot playing.
            var bot = game.Players[1];
            Assert.Greater(bot.Hand.Count + bot.Compound.Count, 0,
                           "the bot should have drafted cards of its own");
            Assert.Greater(EffectContext.AllColors.Sum(c => bot.Resources[c]), 0,
                           "and taken its resources");
        }

        /// <summary>
        /// The die model loads, is thrown when the viewer rolls, and clears away
        /// on a click without taking anything else with it.
        ///
        /// The board is a ScreenSpaceOverlay canvas over an opaque backdrop, so
        /// a die in the scene is invisible behind it - it is filmed by its own
        /// camera and shown as a picture. That indirection is easy to break
        /// silently, so this checks the picture exists and has something in it
        /// rather than merely that the component was constructed.
        /// </summary>
        [UnityTest]
        public IEnumerator RollingThrowsADieThatCanBeClickedAway()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Rolling);

            var roller = Object.FindAnyObjectByType<DieRoller>(FindObjectsInactive.Include);
            Assert.IsNotNull(roller, "the board should build a die roller");

            var game = ServerGame();
            ApplyAsHost(_ => game.RollPrimaryDie(0));
            yield return WaitForFrames(4);

            // These tests run in batchmode with no graphics device, where a
            // render texture cannot exist. The die is a flourish and is built to
            // sit the whole thing out rather than take the board down with it,
            // so that is what is checked when there is nothing to render into.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.IsFalse(roller.gameObject.activeSelf,
                    "with no graphics device the die must stay out of the way entirely");
                Assert.IsNull(_manager.LastError, "and must not disturb the game");
                yield break;
            }

            Assert.IsTrue(roller.gameObject.activeSelf, "rolling should throw the die");

            var picture = roller.GetComponent<RawImage>();
            Assert.IsNotNull(picture, "the die is composited over the board as a picture");
            Assert.IsNotNull(picture.texture, "with a render texture behind it");
            Assert.IsFalse(picture.raycastTarget,
                "the picture covers the whole board, so it must never take a click");

            Assert.IsNull(_manager.LastError, "and none of this touches the game");
        }

        /// <summary>
        /// The die shows the number it was told to.
        ///
        /// Proved from the geometry rather than by looking, which matters
        /// because nothing renders in batchmode and this is the one thing about
        /// a die that is actually wrong if it is wrong. For each value it turns
        /// the mesh the way the roller would, finds which face ends up pointing
        /// upward, and reads that face's texture coordinates back to a number.
        /// A mis-built mesh, a mis-laid atlas or a wrong rotation all fail here.
        /// </summary>
        [Test]
        public void EveryDieFaceLandsShowingItsOwnNumber()
        {
            var mesh = (Mesh)typeof(DieRoller)
                .GetMethod("BuildDieMesh", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);

            var faceUp = typeof(DieRoller).GetMethod("FaceUp", BindingFlags.NonPublic | BindingFlags.Static);

            var normals = mesh.normals;
            var uvs = mesh.uv;

            for (var value = 1; value <= 6; value++)
            {
                var rotation = (Quaternion)faceUp.Invoke(null, new object[] { value });

                // The face whose normal ends up pointing most nearly straight up.
                var bestFace = -1;
                var bestUpness = -2f;

                for (var face = 0; face < 6; face++)
                {
                    var upness = (rotation * normals[face * 4]).y;
                    if (upness > bestUpness)
                    {
                        bestUpness = upness;
                        bestFace = face;
                    }
                }

                Assert.Greater(bestUpness, 0.99f,
                    $"turning the die to {value} should leave a face squarely up, not at an angle");

                // Read that face's atlas cell back to the number printed on it.
                var centre = (uvs[bestFace * 4] + uvs[(bestFace * 4) + 2]) * 0.5f;
                var shown = -1;

                for (var candidate = 1; candidate <= 6; candidate++)
                {
                    if (BoardArt.DieAtlasCell(candidate).Contains(centre))
                    {
                        shown = candidate;
                    }
                }

                Assert.AreEqual(value, shown,
                    $"rolling a {value} would show a {shown} - the face that ends up on top "
                    + $"carries the wrong number (uv {centre})");
            }

            // And the die is a real die: opposite faces add to seven.
            for (var face = 0; face < 6; face += 2)
            {
                var centre = (uvs[face * 4] + uvs[(face * 4) + 2]) * 0.5f;
                var opposite = (uvs[(face + 1) * 4] + uvs[((face + 1) * 4) + 2]) * 0.5f;

                var a = 0;
                var b = 0;
                for (var candidate = 1; candidate <= 6; candidate++)
                {
                    if (BoardArt.DieAtlasCell(candidate).Contains(centre)) a = candidate;
                    if (BoardArt.DieAtlasCell(candidate).Contains(opposite)) b = candidate;
                }

                Assert.AreEqual(7, a + b, $"opposite faces should add to seven, but {a} is opposite {b}");
            }
        }

        /// <summary>
        /// The Rolling phase waits for a player holding Try again.
        ///
        /// The rules were always right about the reroll; the server was not. It
        /// readied everybody the instant the last die landed, and that is the
        /// same instant the reroll becomes legal - so the phase was already over
        /// before the card could be used, and nothing in the rules engine could
        /// see anything wrong. The window has to be checked where it was closed.
        /// </summary>
        [UnityTest]
        public IEnumerator TryAgainKeepsTheRollingPhaseOpen()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Rolling);

            var game = ServerGame();
            game.Players[0].Compound.Add(
                new CardInstance(-950, CardDatabase.Instance.Get(CardIds.TryAgain)));

            // Every die down - which is exactly when this used to end the phase.
            ApplyAsHost(_ => game.RollPrimaryDice());
            yield return WaitForFrames(6);

            Assert.AreEqual(nameof(TurnPhase.Rolling), _manager.View.phase,
                "the phase must wait for a player who can still reroll");
            Assert.IsTrue(_manager.View.Viewer.canReroll,
                "and the board has to be told the offer is open");

            var before = _manager.View.Viewer.primaryDie;
            _manager.RequestRerollRpc();
            yield return WaitForFrames(6);

            Assert.IsFalse(_manager.View.Viewer.canReroll,
                "taking the reroll closes the offer");
            Assert.Greater(_manager.View.Viewer.primaryDie, 0,
                $"and leaves a legal die (was {before})");

            // A unique high roller is still owed their bonus, which also holds
            // the phase open - correctly, and by a different mechanism than the
            // one under test. Pinned to the viewer rather than left to the dice,
            // so this does not depend on what the shuffle happened to deal.
            ApplyAsHost(_ =>
            {
                game.SetPrimaryDie(game.Players[0], GameSettings.DieSides);
                game.SetPrimaryDie(game.Players[1], 1);
            });
            yield return WaitForFrames(2);

            // Claimed through the real request, because that is what re-examines
            // whether the phase still has anything to wait for. Reaching into
            // the game directly settles the bonus without ever asking.
            _manager.RequestClaimHighRollResourceRpc((int)ResourceColor.Red);

            // With nothing left owed it closes on its own, exactly as it did
            // before the card existed. Spending the reroll has to hand the phase
            // back, or holding Try again would mean the whole table pressing
            // Ready by hand every turn.
            yield return WaitForFrames(8);

            Assert.AreNotEqual(nameof(TurnPhase.Rolling), _manager.View.phase,
                "once nothing is owed the phase should close on its own again");
        }

        /// <summary>
        /// Zeroes the server's presentation pacing so a test can run a game at
        /// frame speed instead of at the speed it is meant to be watched.
        /// </summary>
        private void ClearPacingClocks()
        {
            // Well into the past rather than zero: these are compared against
            // Time.time, which is only a few seconds old during a test run, so
            // zero is not necessarily long enough ago to satisfy a dwell.
            foreach (var name in new[] { "_nextBotActionAt", "_nextActivationAt", "_activationEnteredAt" })
            {
                typeof(NetworkGameManager)
                    .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?
                    .SetValue(_manager, -1000f);
            }
        }

        /// <summary>
        /// Every card in the open hand is fully on screen, top edge included.
        ///
        /// Measured rather than eyeballed: the fan's height maths has been
        /// adjusted twice by reasoning about it and been wrong twice, because the
        /// card's own outline and the rotation of the outermost slots both add
        /// height that the arithmetic did not account for. This asserts the thing
        /// that actually matters - no part of any card is off the top of the
        /// screen - so it cannot be satisfied by maths that merely looks right.
        /// </summary>
        [UnityTest]
        public IEnumerator TheOpenHandIsFullyOnScreen()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);
            yield return ExpandHand();
            Canvas.ForceUpdateCanvases();

            var handRow = (RectTransform)typeof(BoardUI)
                .GetField("_handRow", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);

            var cards = handRow.GetComponentsInChildren<BoardCardView>();
            Assert.Greater(cards.Length, 0, "there should be cards in the open hand");

            var screen = new Rect(0f, 0f, Screen.width, Screen.height);

            foreach (var card in cards)
            {
                var rect = WorldRect((RectTransform)card.transform);

                Assert.LessOrEqual(rect.yMax, screen.yMax,
                    $"a hand card's top is off screen by {rect.yMax - screen.yMax:0.#}px "
                    + $"(card {rect}, screen {screen})");
                Assert.GreaterOrEqual(rect.yMin, screen.yMin,
                    $"a hand card's bottom is off screen (card {rect})");

                // And nothing above it clips it either.
                Assert.IsTrue(IsFullyVisibleThroughEveryMask(card.GetComponent<Image>()),
                    "a hand card is being clipped by a mask above it");
            }
        }

        /// <summary>
        /// Resources stay takeable with the hand open.
        ///
        /// The tray is opaque and answers the pointer, so when it spanned the
        /// whole width it did not merely sit over the resource HUD - it ate the
        /// clicks meant for it, and a turn's resources could not be collected
        /// without closing the hand first. Geometry, so it is checked as
        /// geometry: the two must not overlap at all.
        /// </summary>
        [UnityTest]
        public IEnumerator TheOpenHandLeavesTheResourceHudClickable()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Resource);
            yield return ExpandHand();
            Canvas.ForceUpdateCanvases();

            var handRow = (RectTransform)typeof(BoardUI)
                .GetField("_handRow", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);
            Assert.IsNotNull(handRow, "the board should have a hand tray");

            var hud = Object.FindAnyObjectByType<ResourceHud>();
            Assert.IsNotNull(hud, "the resource HUD should be on screen");

            var handRect = WorldRect(handRow);
            foreach (Transform slot in hud.transform)
            {
                var slotRect = WorldRect((RectTransform)slot);
                Assert.IsFalse(handRect.Overlaps(slotRect),
                    $"the open hand covers the {slot.name} resource circle, which swallows its clicks "
                    + $"(hand {handRect}, circle {slotRect})");
            }

            // And the circles genuinely answer a click while it is open.
            var before = TotalResources(_manager.View.Viewer);
            var disc = FindButtonNamed("Red Slot");
            Assert.IsNotNull(disc, $"with the hand open: {WhyButtonNamedUnusable("Red Slot")}");

            disc.onClick.Invoke();
            yield return WaitForFrames(3);

            Assert.AreEqual(before + 1, TotalResources(_manager.View.Viewer) + _pendingPickCount(),
                            "clicking a resource with the hand open has to register");
        }

        /// <summary>How many picks are staged locally but not yet submitted.</summary>
        private int _pendingPickCount()
        {
            var pending = typeof(BoardUI)
                .GetField("_pendingResources", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);
            return ((System.Collections.ICollection)pending).Count;
        }

        /// <summary>
        /// Feeds the board a pointer position, the same way its own Update does
        /// from the real mouse.
        ///
        /// Driven directly rather than through a simulated input device: a
        /// synthetic mouse never reported its position at all under batchmode,
        /// and the device layer is not what this is testing. What matters is
        /// the loop behind it - position in, rebuild out, and whether that
        /// rebuild disturbs the next frame's answer.
        /// </summary>
        private void PointAt(Vector2 screenPoint)
        {
            typeof(BoardUI)
                .GetMethod("PollHandHover", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_board, new object[] { screenPoint });
        }

        private bool HandIsExpanded() =>
            (bool)typeof(BoardUI)
                .GetField("_handExpanded", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);

        private bool IsInHand(Component component)
        {
            var handRow = (RectTransform)typeof(BoardUI)
                .GetField("_handRow", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);
            return component != null && component.transform.IsChildOf(handRow);
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
                if (game.PendingChoice != null)
                {
                    ApplyAsHost(_ => game.AnswerPendingChoiceWithDefault());
                }
                else if (game.Phase == TurnPhase.Draft)
                {
                    var drafter = game.CurrentDrafterId.Value;
                    var card = game.DraftZone[0].InstanceId;
                    ApplyAsHost(_ => game.DraftCard(drafter, card));
                }
                else if (game.Phase == TurnPhase.Rolling && !game.DiceRolled)
                {
                    ApplyAsHost(_ => game.RollPrimaryDice());
                }
                else if (game.Phase == TurnPhase.Activation && game.HasEffectsPending)
                {
                    ApplyAsHost(_ => game.ResolveNextActivation());
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

        private static Rect RectRelativeTo(RectTransform rect, RectTransform ancestor)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] = ancestor.InverseTransformPoint(corners[i]);
            }

            return Rect.MinMaxRect(corners.Min(point => point.x), corners.Min(point => point.y),
                                   corners.Max(point => point.x), corners.Max(point => point.y));
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

        private IEnumerator StartGame(int opponents = 1)
        {
            for (var i = 0; i < opponents; i++)
            {
                _manager.AddTestSeat($"Test Opponent {i + 1}");
            }

            _manager.RequestStartGameRpc();
            yield return WaitForFrames(6);
            Assert.IsNotNull(_manager.View, "a game view should have arrived");
        }

        /// <summary>
        /// A card that stops to ask something mid-activation puts its question on
        /// the stage, under the card that is asking, and puts it there *before*
        /// that card's animation rather than during it.
        ///
        /// The board's own popup must stay out of it. Offering the same decision
        /// in two places is bad enough; offering it in the one place where the
        /// card asking is not visible defeats the point of staging it at all.
        /// </summary>
        [UnityTest]
        public IEnumerator AQuestionMidActivationIsAskedOnTheStage()
        {
            // Two opponents, so an unaimed damage effect has a genuine choice to
            // put rather than answering itself against the only candidate.
            yield return StartGame(opponents: 2);
            yield return AdvanceTo(TurnPhase.Rolling);

            var game = ServerGame();
            var asker = new CardInstance(-601, CardDatabase.Instance.Get(CardIds.ResearcherOfTheOldWays));
            game.Players[0].Compound.Add(asker);

            var face = asker.Definition.ActivationNumbers.First();
            ApplyAsHost(_ => game.RollPrimaryDice());
            ApplyAsHost(_ =>
            {
                foreach (var player in game.Players)
                {
                    game.SetPrimaryDie(player, face);
                }

                game.AdvancePhase();
            });

            // Let the stage work through anything queued ahead of the question.
            var guard = 0;
            while (!_manager.View.hasPendingChoice && guard++ < 240)
            {
                yield return null;
            }

            Assert.IsTrue(_manager.View.hasPendingChoice,
                          "an unaimed damage Unit should stop to ask who it hits");
            Assert.AreEqual(nameof(TurnPhase.Activation), _manager.View.phase);

            yield return WaitForFrames(4);
            Canvas.ForceUpdateCanvases();

            var stage = Object.FindAnyObjectByType<ActivationStage>();
            Assert.IsNotNull(stage, "the activation stage should exist");

            var choiceRow = stage.transform.Find("Choice");
            Assert.IsNotNull(choiceRow, "the stage should own a row for the question");
            Assert.IsTrue(choiceRow.gameObject.activeInHierarchy,
                          "the question has to be on the stage while it is pending");

            var options = choiceRow.GetComponentsInChildren<Button>();
            Assert.GreaterOrEqual(options.Length, 2,
                                  "both opponents should be offered as targets");

            // No prompt text of its own - the card is on screen saying what it does.
            var popup = GameObject.Find("Popup Panel");
            Assert.IsTrue(popup == null || !popup.activeInHierarchy,
                          "the board popup must not offer the same decision as the stage");

            // Answering it lets the sequence carry on.
            options[0].onClick.Invoke();
            yield return WaitForFrames(6);

            Assert.IsFalse(_manager.View.hasPendingChoice,
                           "answering on the stage has to actually answer the card");
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

        private void DelayNextActivation()
        {
            var field = typeof(NetworkGameManager).GetField(
                "_nextActivationAt", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_manager, Time.time + 100f);
        }

        private void ExpireNextActivation()
        {
            var field = typeof(NetworkGameManager).GetField(
                "_nextActivationAt", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_manager, Time.time - 1f);
        }

        private void ExpirePhaseClock()
        {
            var field = typeof(NetworkGameManager).GetField(
                "_phaseStartedAt", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_manager, Time.time - GameSettings.PhaseTimeoutSeconds - 1f);
        }
    }
}
