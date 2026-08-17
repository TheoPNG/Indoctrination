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
            // Big enough to hit without aiming, small enough not to be the
            // largest thing on the board. It was a 620x112 slab for a target you
            // use once a turn.
            Assert.That(dropZone.rect.width, Is.InRange(180f, 340f),
                "the draft target should be an easy target, not a shelf");
            Assert.That(dropZone.rect.height, Is.InRange(40f, 70f),
                "and no taller than it needs to be");

            // A small framed slot that fits its zone, not an oversized ellipse
            // clipped down to a semicircle and not a shelf across the board.
            var dropArc = dropZone.Find("Drop Arc").GetComponent<Image>();
            Assert.IsNull(dropArc.sprite, "the drop target should be a plain band, not a disc");
            Assert.LessOrEqual(dropArc.rectTransform.rect.height, dropZone.rect.height + 0.5f,
                "the band should fit its zone rather than overflow and be clipped");
            Assert.IsNotNull(dropArc.GetComponent<Outline>(),
                "the affordance is the frame around the slot");

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
        /// Roll button on it.
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

            var roll = FindButtonLabelled("Roll");
            Assert.IsNotNull(roll, WhyUnusable("Roll"));
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
                position = RectTransformUtility.WorldToScreenPoint(UIFactory.UiCamera, bin.position)
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
                position = RectTransformUtility.WorldToScreenPoint(UIFactory.UiCamera, battlefieldViewport.position)
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
                position = RectTransformUtility.WorldToScreenPoint(UIFactory.UiCamera, firstCardView.transform.position)
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

            FindButtonLabelled("Roll").onClick.Invoke();
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

            var discard = FindButtonLabelled("View discard");
            Assert.IsNotNull(discard, WhyUnusable("View discard"));
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

            FindButtonLabelled("Roll").onClick.Invoke();
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

            FindButtonLabelled("Roll").onClick.Invoke();
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
            // Opponents across the top, you along the bottom - two bands, one
            // track between them per player.
            Assert.AreEqual(
                2,
                stage.transform.Find("All Player Tracks").childCount
                + stage.transform.Find("Your Track").childCount,
                "every player's large tracks must remain visible during the animation");
            Assert.AreEqual(1, stage.transform.Find("Your Track").childCount,
                "and your own belongs at your own edge of the table");

            // Seated the way a table is seated. A damage card throws itself at
            // whoever it hit, so which way it travels only means anything if
            // opponents are one way and you are the other.
            Canvas.ForceUpdateCanvases();
            var card = (RectTransform)stage.transform.Find("Locked Card");
            var yours = (RectTransform)stage.transform.Find("Your Track").GetChild(0);
            var theirs = (RectTransform)stage.transform.Find("All Player Tracks").GetChild(0);

            Assert.Less(TopOf(yours), card.position.y,
                "your own track belongs below the card, at your edge of the table");
            Assert.Greater(BottomOf(theirs), card.position.y,
                "and everybody else's above it");

            // World corners, not position and rect.height together: `position`
            // is world space and `rect` is the untransformed local rectangle, so
            // mixing them measures a box that is not on screen anywhere.
            var menu = (RectTransform)stage.transform.Find("Choice");
            Assert.Greater(BottomOf(yours), TopOf(menu),
                "and it must not sit on top of the menu underneath it");
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

            // Nested under the name row now that the strip is stacked rather
            // than strung out along one line.
            var box = bar == null
                ? null
                : bar.GetComponentsInChildren<RectTransform>(true)
                    .FirstOrDefault(child => child.gameObject.name == "Die");
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

            var resign = FindButtonNamed("Resign");
            Assert.IsNotNull(resign, WhyUnusable("Resign"));

            resign.onClick.Invoke();
            yield return WaitForFrames(3);

            Assert.IsTrue(_manager.View.Viewer.isAlive,
                          "one press must not resign - it only asks for confirmation");
            Assert.IsNotNull(FindButtonLabelled("?"), "and the button should ask");

            FindButtonLabelled("?").onClick.Invoke();
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

            var offer = FindButtonNamed("Offer Draw");
            Assert.IsNotNull(offer, WhyUnusable("Offer Draw"));

            offer.onClick.Invoke();
            yield return WaitForFrames(4);

            Assert.IsFalse(_manager.View.isGameOver,
                           "one player offering a draw must not end the game");
            Assert.IsTrue(_manager.View.Viewer.offeringDraw, "but the offer stands");
            Assert.IsNotNull(FindButtonLabelled("1/2"),
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
            var peek = RectTransformUtility.WorldToScreenPoint(UIFactory.UiCamera, handRow.position)
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

            Assert.IsTrue(roller.gameObject.activeSelf, "rolling should throw the dice");

            // The dice are real objects in the scene now, in front of the
            // board's own plane, which is the whole reason they can be seen.
            var stage = GameObject.Find("Die Stage");
            Assert.IsNotNull(stage, "the dice should be thrown onto a real table in the scene");
            Assert.IsTrue(stage.activeInHierarchy, "which is only up while dice are on it");

            var bodies = stage.GetComponentsInChildren<Rigidbody>();
            Assert.AreEqual(_manager.View.players.Count(p => p.isAlive && p.hasRolled), bodies.Length,
                "one physical die per player who rolled");

            var canvas = Object.FindAnyObjectByType<Canvas>();
            Assert.AreNotEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode,
                "the board has to be drawn through the camera, or nothing in the scene "
                + "can ever appear in front of it");

            foreach (var body in bodies)
            {
                var toDice = Vector3.Distance(Camera.main.transform.position, body.transform.position);
                Assert.Less(toDice, UIFactory.CanvasPlaneDistance,
                    $"a die at {body.transform.position} is further from the camera than the board "
                    + $"itself, so it would be drawn behind it");
            }

            // One die per player who rolled, not just the viewer's.
            var labels = roller.GetComponentsInChildren<Text>(includeInactive: true)
                .Where(t => t.name.StartsWith("Die Owner") && t.gameObject.activeSelf)
                .ToList();
            Assert.AreEqual(_manager.View.players.Count(p => p.isAlive && p.hasRolled), labels.Count,
                "every player who rolled should have a die on the table");
            Assert.IsTrue(labels.Any(t => t.text.StartsWith("YOU")),
                "and the viewer's own die should be marked as theirs");

            Assert.IsNull(_manager.LastError, "and none of this touches the game");
        }

        /// <summary>
        /// The die model is a die, and nothing else.
        ///
        /// The modelling file has a camera and a light sitting next to the cube,
        /// the way a modelling file usually does, and Unity will import them as
        /// real components unless the importer is told not to. That is not a
        /// cosmetic problem: the camera comes in as part of the die, so it
        /// tumbles with the physics and renders the world - skybox included -
        /// from inside a spinning die, over the top of the entire board. The
        /// board disappears and all you see is sky going round.
        ///
        /// This reads the imported asset rather than a thrown die, so it holds
        /// in batchmode where there is no graphics device and no dice are ever
        /// built. It fails if the importer settings are ever reverted.
        /// </summary>
        [Test]
        public void TheDieModelCarriesNoCameraOrLightOfItsOwn()
        {
            var model = Resources.Load<GameObject>("Models/Die");
            Assert.IsNotNull(model, "the die model should be loadable from Resources");

            Assert.IsEmpty(model.GetComponentsInChildren<Camera>(true),
                "an imported camera rides the die through its own physics and renders the "
                + "skybox over the whole board - turn off Import Cameras on Die.fbx");
            Assert.IsEmpty(model.GetComponentsInChildren<Light>(true),
                "the table lights the dice itself; a light imported from the model "
                + "relights the whole scene from wherever the die happens to land");
        }

        /// <summary>
        /// The title screen offers online play and nothing that asks for an
        /// address.
        ///
        /// Relay exists precisely so that no player ever sees an IP or a port,
        /// so leaving those fields on the screen would be offering a way in that
        /// no longer connects anybody to anybody.
        /// </summary>
        [Test]
        public void TheTitleScreenOffersOnlinePlayAndNoAddressFields()
        {
            var named = Object.FindObjectsByType<RectTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Select(rect => rect.gameObject.name)
                .ToHashSet();

            foreach (var control in new[]
                     {
                         "Host Button", "Join Button", "Browse Button",
                         "Game Name Field", "Join Code Field", "Solo Button", "Quit Button"
                     })
            {
                Assert.Contains(control, named.ToList(), $"the title screen should offer {control}");
            }

            foreach (var gone in new[] { "Address Field", "Port Field", "Address Row" })
            {
                Assert.IsFalse(named.Contains(gone),
                    $"{gone} connects nobody to anybody now that play goes through Relay");
            }

            // The browser is built but stays out of the way until it is asked for.
            var browser = GameObject.Find("Browser Box");
            Assert.IsTrue(browser == null || !browser.activeInHierarchy,
                "the game list should not be covering the title screen");
        }

        /// <summary>
        /// The high roller takes their prize off the circles on the left, the
        /// same place every other resource in this game comes from.
        ///
        /// It used to be a second set of colour buttons in a popup, which taught
        /// a different way to pick a colour for the one case that is not the
        /// resource phase.
        /// </summary>
        [UnityTest]
        public IEnumerator TheHighRollPrizeIsTakenFromTheResourceCircles()
        {
            yield return StartGame();

            var game = ServerGame();
            while (game.Phase == TurnPhase.Draft)
            {
                var drafter = game.CurrentDrafterId.Value;
                ApplyAsHost(_ => game.DraftCard(drafter, game.DraftZone[0].InstanceId));
                yield return WaitForFrames(2);
            }

            foreach (var player in game.LivingPlayers.ToList())
            {
                if (!game.HasRolled(player.PlayerId))
                {
                    ApplyAsHost(_ => game.RollPrimaryDie(player.PlayerId));
                }
            }

            // An outright winner, so there is a prize to take at all.
            var viewer = _manager.View.viewerPlayerId;
            ApplyAsHost(_ =>
            {
                foreach (var player in game.LivingPlayers.ToList())
                {
                    player.SetPrimaryDie(player.PlayerId == viewer ? 6 : 1);
                }
            });

            yield return WaitForFrames(3);
            Canvas.ForceUpdateCanvases();

            Assert.IsFalse(_manager.View.highRollResourceClaimed);

            var red = FindButtonNamed("Red Slot");
            Assert.IsNotNull(red, WhyUnusable("Red Slot"));
            Assert.IsTrue(red.interactable,
                "the circles should light up for the high roller's pick");

            var before = _manager.View.Viewer.red;
            red.onClick.Invoke();
            yield return WaitForFrames(4);

            Assert.IsTrue(_manager.View.highRollResourceClaimed,
                "pressing a circle should take the prize");
            Assert.AreEqual(before + 1, _manager.View.Viewer.red,
                "and it should be the colour that was pressed");
            Assert.IsNull(_manager.LastError);
        }

        /// <summary>
        /// The draft clock runs, and starts again for whoever is asked next.
        ///
        /// Two separate faults made it read "0s until a pick is made for you"
        /// for the whole draft. The view forced the remaining time to zero
        /// during Draft, even though the server has always enforced a draft
        /// timeout - so a real clock was running and simply never sent. And the
        /// clock was only restarted by a phase change, while a draft is a run of
        /// individual picks inside one phase, so the whole table shared a single
        /// phase's worth of time between them.
        ///
        /// This waits real seconds on purpose. The second fault is only visible
        /// once enough time has passed to see it not being given back.
        /// </summary>
        [UnityTest]
        public IEnumerator TheDraftClockRestartsForEachPlayerAsked()
        {
            yield return StartGame();
            yield return WaitForFrames(2);

            var game = ServerGame();
            Assert.AreEqual(TurnPhase.Draft, game.Phase);

            // Tables start with the clocks off, and this is a test about the
            // clock.
            _manager.RequestSetTimersRpc(true);
            yield return WaitForFrames(2);
            Assert.IsTrue(_manager.View.timersEnabled);

            Assert.Greater(_manager.View.phaseSecondsRemaining, 0f,
                "the draft has a real time limit, so the board has to be told what it is");

            var samples = 0;
            while (game.Phase == TurnPhase.Draft && samples < 3)
            {
                // Long enough that a clock which was never restarted would be
                // visibly short by the next pick.
                yield return new WaitForSeconds(1.1f);

                var drafter = game.CurrentDrafterId.Value;
                var took = false;

                // Whichever card is actually legal. Blocked by Games and a
                // reserved pick both make the first card in the zone illegal for
                // this player, and which cards are where depends on the shuffle
                // - taking the first one regardless would fail on some seeds and
                // not others.
                foreach (var card in game.DraftZone.Select(c => c.InstanceId).ToList())
                {
                    ApplyAsHost(_ => game.DraftCard(drafter, card));
                    yield return WaitForFrames(2);

                    if (game.CurrentDrafterId != drafter || game.Phase != TurnPhase.Draft)
                    {
                        took = true;
                        break;
                    }
                }

                Assert.IsTrue(took, "the drafter should have been able to take something");

                if (game.Phase != TurnPhase.Draft)
                {
                    break;
                }

                samples++;
                Assert.Greater(
                    _manager.View.phaseSecondsRemaining,
                    GameSettings.PhaseTimeoutSeconds - 1f,
                    "the player being waited on should get the whole clock, not "
                    + "whatever the last player left of it");
            }

            Assert.Greater(samples, 0, "the draft should have run long enough to check");
        }

        /// <summary>
        /// The cards you are being asked to pick from are lit; nobody else's
        /// turn lights anything.
        ///
        /// The draft row used to look identical whether it was your pick or
        /// somebody else's - the only difference was whether dragging happened
        /// to work, which is something you find out by trying it.
        /// </summary>
        [UnityTest]
        public IEnumerator TheDraftLightsUpOnlyOnYourOwnPick()
        {
            yield return StartGame();

            var game = ServerGame();
            Assert.AreEqual(TurnPhase.Draft, game.Phase, "the game opens on the draft");

            // Walk the whole draft, reading the board at every pick. Nothing may
            // ever be lit on somebody else's turn, and something has to be lit
            // on at least one of yours.
            var litOnYourTurn = false;
            var sawSomebodyElse = false;

            while (game.Phase == TurnPhase.Draft)
            {
                yield return WaitForFrames(2);
                Canvas.ForceUpdateCanvases();

                var mine = _manager.View.currentDrafterId == _manager.View.viewerPlayerId;
                var lit = Object.FindObjectsByType<BoardCardView>(FindObjectsSortMode.None)
                    .Count(card => card.AwaitingYourPick);

                if (mine)
                {
                    litOnYourTurn |= lit > 0;
                }
                else
                {
                    sawSomebodyElse = true;
                    Assert.AreEqual(0, lit,
                        "nothing should be lit while somebody else is picking");
                }

                var drafter = game.CurrentDrafterId.Value;
                var card = game.DraftZone[0].InstanceId;
                ApplyAsHost(_ => game.DraftCard(drafter, card));
            }

            Assert.IsTrue(litOnYourTurn,
                "the cards you can take should be lit while the table waits on you");
            Assert.IsTrue(sawSomebodyElse, "and somebody else should have picked too");
        }

        /// <summary>
        /// Hovering an opponent shows what they have: their resources, which are
        /// public and were shown nowhere, and their compound, which is on the
        /// battlefield but scrolls off it on a full table.
        ///
        /// Driven directly rather than by moving a mouse, because batchmode has
        /// no pointer at all.
        /// </summary>
        [UnityTest]
        public IEnumerator HoveringAnOpponentShowsTheirResourcesAndCompound()
        {
            yield return StartGame();
            yield return WaitForFrames(2);

            var game = ServerGame();
            var opponentId = _manager.View.players
                .First(p => p.playerId != _manager.View.viewerPlayerId).playerId;

            // Something to look at: resources they own and a card in play.
            ApplyAsHost(_ =>
            {
                var seat = game.Players.First(p => p.PlayerId == opponentId);
                seat.Resources.Add(ResourceColor.Red);
                seat.Resources.Add(ResourceColor.Red);
                seat.Resources.Add(ResourceColor.Blue);
                seat.Compound.Add(new CardInstance(
                    -900, CardDatabase.Instance.Get(CardIds.Mindstone)));
            });

            yield return WaitForFrames(2);
            Canvas.ForceUpdateCanvases();

            var peek = Object.FindAnyObjectByType<PlayerPeek>(FindObjectsInactive.Include);
            Assert.IsNotNull(peek, "the board should carry an opponent peek");

            var them = _manager.View.players.First(p => p.playerId == opponentId);
            peek.Show(them, (RectTransform)peek.transform);
            yield return WaitForFrames(2);

            Assert.AreEqual(opponentId, peek.ShowingFor);

            var counts = GameObject.Find("Peek Resources").GetComponentsInChildren<Text>()
                .Select(t => t.text).ToList();
            CollectionAssert.Contains(counts, "2", "their two Red should be shown");
            CollectionAssert.Contains(counts, "1", "and their one Blue");

            var shown = GameObject.Find("Peek Cards").GetComponentsInChildren<BoardCardView>();
            Assert.AreEqual(them.compound.Length, shown.Length,
                "every card they have in play should be in the strip");

            // Big enough to actually read. A card laid out at full size and then
            // scaled is the only arrangement that survives; putting the card
            // straight into the grid makes the grid resize its rect as well as
            // the scale being applied, and it comes out a fraction of the size
            // with its innards laid out for a shape it is not.
            foreach (var view in shown)
            {
                var rect = (RectTransform)view.transform;
                Assert.AreEqual(BoardCardView.Width, rect.rect.width, 0.5f,
                    "the card should keep its own layout size and be scaled, not resized");
                Assert.That(rect.rect.width * rect.localScale.x, Is.InRange(60f, 130f),
                    "and be drawn at a size somebody can read");
            }

            // Every card opens full size. At 112 pixels wide the text on a card
            // is a suggestion of text, and reading an opponent's compound is the
            // whole point of this.
            foreach (var view in shown)
            {
                Assert.IsNotNull(view.GetComponent<Button>(),
                    "a card in the peek should open in full view when clicked");
            }

            // And the panel holds while the pointer is on it. The board hides
            // this by asking whether the pointer is still on the player's strip,
            // so without this, moving toward a card to click it closes the panel
            // being reached for.
            var panel = (RectTransform)GameObject.Find("Peek Panel").transform;
            var corners = new Vector3[4];
            panel.GetWorldCorners(corners);
            var middle = (corners[0] + corners[2]) * 0.5f;

            Assert.IsTrue(
                peek.ContainsPointer(
                    RectTransformUtility.WorldToScreenPoint(UIFactory.UiCamera, middle),
                    UIFactory.UiCamera),
                "the peek should count the pointer as still on it");

            peek.Hide();
            yield return WaitForFrames(1);
            Assert.AreEqual(-1, peek.ShowingFor, "and it goes away again");
        }

        /// <summary>
        /// The number is not printed beside a player's name while their die is
        /// still in the air.
        ///
        /// The whole point of throwing a die is that the number arrives when it
        /// stops. Writing it into the strips the instant the server says so
        /// answers the question before the throw does, and makes the roll
        /// decorative.
        ///
        /// Checked through StatBar directly, because whether the dice are still
        /// rolling depends on an animation that never runs in batchmode - where
        /// the board correctly shows the numbers at once, there being nothing to
        /// wait for.
        /// </summary>
        [UnityTest]
        public IEnumerator ARolledNumberIsHiddenWhileTheDieIsStillRolling()
        {
            yield return StartGame();
            yield return WaitForFrames(2);

            var bar = StatBar.Create(_board.transform);
            var player = _manager.View.players.First();
            player.hasRolled = true;
            player.primaryDie = 5;
            player.privateDice = new[] { 3 };

            bar.Populate(player, isViewer: false, revealDice: false);
            yield return WaitForFrames(1);

            var faces = bar.GetComponentsInChildren<Text>(true)
                .Where(t => t.name is "Die Face" or "Face")
                .Select(t => t.text)
                .ToList();

            Assert.IsNotEmpty(faces, "the die box should be showing while a player is rolling");
            CollectionAssert.DoesNotContain(faces, "5",
                "the rolled number must not be readable before the die lands");
            CollectionAssert.DoesNotContain(faces, "3",
                "and neither must a private die");
            CollectionAssert.AreEquivalent(new[] { "?", "?" }, faces,
                "a player who is rolling should read as rolling, not as not having rolled");

            bar.Populate(player, isViewer: false, revealDice: true);
            yield return WaitForFrames(1);

            faces = bar.GetComponentsInChildren<Text>(true)
                .Where(t => t.name is "Die Face" or "Face")
                .Select(t => t.text)
                .ToList();

            CollectionAssert.Contains(faces, "5", "and once it lands, the number is shown");
            CollectionAssert.Contains(faces, "3");

            Object.Destroy(bar.gameObject);
        }

        /// <summary>
        /// The board never waits on a die animation that is not running.
        ///
        /// The high roller's resource is deliberately held back until the dice
        /// stop, so that being handed the prize does not give the roll away. On
        /// a machine that cannot show dice - batchmode, and anything without a
        /// graphics device - there is no animation to wait for, and a flourish
        /// that is not running must never be the reason a game cannot continue.
        /// </summary>
        [UnityTest]
        public IEnumerator ADieAnimationThatCannotRunNeverHoldsTheGameUp()
        {
            yield return StartGame();

            var roller = Object.FindAnyObjectByType<DieRoller>(FindObjectsInactive.Include);
            Assert.IsNotNull(roller);

            var game = ServerGame();
            while (game.Phase == TurnPhase.Draft)
            {
                var drafter = game.CurrentDrafterId.Value;
                ApplyAsHost(_ => game.DraftCard(drafter, game.DraftZone[0].InstanceId));
                yield return WaitForFrames(2);
            }

            foreach (var player in game.LivingPlayers.ToList())
            {
                if (!game.HasRolled(player.PlayerId))
                {
                    ApplyAsHost(_ => game.RollPrimaryDie(player.PlayerId));
                }
            }

            yield return WaitForFrames(4);

            Assert.IsTrue(roller.Settled,
                "with no dice to show, the roll has to count as finished");
            Assert.IsNull(_manager.LastError);
        }

        /// <summary>
        /// Quitting warns before it does anything.
        ///
        /// Leaving a game in progress is a resignation - there is no rejoining -
        /// so the press that opens the way out must not be the press that takes
        /// it. This checks the warning appears, says what it costs, changes
        /// nothing on its own, and can be backed out of.
        ///
        /// It deliberately stops short of the confirm button: that one closes
        /// the application, which in here would take the test run with it.
        /// </summary>
        [UnityTest]
        public IEnumerator QuittingWarnsThatItResignsYourGame()
        {
            yield return StartGame();
            yield return WaitForFrames(2);
            Canvas.ForceUpdateCanvases();

            var quit = FindButtonNamed("Quit");
            Assert.IsNotNull(quit, WhyUnusable("Quit"));

            var box = GameObject.Find("Quit Box");
            Assert.IsTrue(box == null || !box.activeInHierarchy,
                "the warning should stay out of the way until it is asked for");

            quit.onClick.Invoke();
            yield return WaitForFrames(2);

            box = GameObject.Find("Quit Box");
            Assert.IsNotNull(box, "pressing Quit should raise the warning");
            Assert.IsTrue(box.activeInHierarchy);

            var warning = GameObject.Find("Quit Warning")?.GetComponent<Text>();
            Assert.IsNotNull(warning, "the warning needs to say something");
            StringAssert.Contains("resigns", warning.text,
                "a player mid-game has to be told that leaving concedes it");

            // The press that opens the warning must not be the press that acts
            // on it.
            Assert.IsTrue(_manager.View.Viewer.isAlive,
                "opening the warning must not resign anybody");
            Assert.IsFalse(_manager.View.Viewer.hasResigned);
            Assert.IsNull(_manager.LastError);

            var keepPlaying = FindButtonNamed("Quit Cancel");
            Assert.IsNotNull(keepPlaying, WhyUnusable("Quit Cancel"));
            keepPlaying.onClick.Invoke();
            yield return WaitForFrames(2);

            // GameObject.Find only sees active objects, so gone means closed.
            Assert.IsNull(GameObject.Find("Quit Box"),
                "backing out should put the board back exactly as it was");
            Assert.IsTrue(_manager.View.Viewer.isAlive);
        }

        /// <summary>
        /// However a die lands, it shows the number the game rolled.
        ///
        /// The dice are thrown for real, so where they stop is not decided in
        /// advance; what is decided in advance is the number. The two are
        /// reconciled by turning the model inside its own cube before the throw
        /// is shown, which only works if the map of which number is printed on
        /// which side is right and the turn is exact. Both are checked here
        /// against every landing and every value, which is something no amount
        /// of watching dice roll would cover.
        ///
        /// It is also pure arithmetic, so it holds in batchmode where no die is
        /// ever built.
        /// </summary>
        [Test]
        public void ADieAlwaysComesToRestOnTheNumberTheGameRolled()
        {
            Random.InitState(20260814);

            for (var landing = 0; landing < 400; landing++)
            {
                // Any way up at all, including the awkward ones: dead level,
                // balanced on an edge, and everything between.
                var landed = landing < 24
                    ? Quaternion.Euler(90f * (landing % 4), 90f * (landing / 4 % 4), 90f * (landing / 16))
                    : Random.rotation;

                for (var value = 1; value <= 6; value++)
                {
                    var facing = DieRoller.TurnOnto(landed, value);

                    Assert.AreEqual(value, DieRoller.NumberShowing(landed, facing),
                        $"a die landing at {landed.eulerAngles} should show {value}");

                    // The turn has to be one of the ways a cube sits on itself.
                    // Anything else leaves the model at an angle inside its own
                    // collider, and the die reads as having stopped crooked.
                    foreach (var axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
                    {
                        var turned = facing * axis;
                        var squareness = Mathf.Max(
                            Mathf.Abs(turned.x), Mathf.Max(Mathf.Abs(turned.y), Mathf.Abs(turned.z)));

                        Assert.Greater(squareness, 0.9999f,
                            $"showing {value} should be a quarter turn, but {axis} went to {turned}");
                    }
                }
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

            // Sideways as well, and against the tray rather than the screen -
            // the tray stops clear of the resource circles on the left, so a
            // card that runs past its edge is over the top of them. The card
            // size is clamped to a minimum, so on a narrow window or a full hand
            // the fan has to tighten its overlap rather than overflow, and this
            // is the check that it does.
            var tray = WorldRect(handRow);

            foreach (var card in cards)
            {
                var rect = WorldRect((RectTransform)card.transform);

                Assert.GreaterOrEqual(rect.xMin, tray.xMin - 0.5f,
                    $"the leftmost hand card runs {tray.xMin - rect.xMin:0.#}px past the "
                    + $"left of the tray (card {rect}, tray {tray})");
                Assert.LessOrEqual(rect.xMax, tray.xMax + 0.5f,
                    $"the rightmost hand card runs {rect.xMax - tray.xMax:0.#}px past the "
                    + $"right of the tray (card {rect}, tray {tray})");
            }
        }

        /// <summary>
        /// Version comparison, which is the whole update check.
        ///
        /// Compared part by part as numbers. Comparing them as text says 0.10.0
        /// is older than 0.9.0, which is the classic way to ship an update
        /// nobody is ever offered - and it would fail silently, because "no
        /// update" looks exactly like "up to date".
        /// </summary>
        [Test]
        public void ANewerVersionIsRecognisedEvenPastNine()
        {
            Assert.IsTrue(UpdateCheck.IsNewer("0.2.0", "0.1.0"));
            Assert.IsTrue(UpdateCheck.IsNewer("0.1.1", "0.1.0"));
            Assert.IsTrue(UpdateCheck.IsNewer("1.0.0", "0.9.9"));

            // The one text comparison gets wrong.
            Assert.IsTrue(UpdateCheck.IsNewer("0.10.0", "0.9.0"),
                "0.10 is later than 0.9 - as text it reads as earlier");
            Assert.IsTrue(UpdateCheck.IsNewer("0.1.10", "0.1.9"));

            Assert.IsFalse(UpdateCheck.IsNewer("0.1.0", "0.1.0"), "the same build is not an update");
            Assert.IsFalse(UpdateCheck.IsNewer("0.1.0", "0.2.0"), "nor is an older one");
            Assert.IsFalse(UpdateCheck.IsNewer("0.9.0", "0.10.0"));

            // Missing parts count as zero, so a two-part version still compares.
            Assert.IsTrue(UpdateCheck.IsNewer("0.2", "0.1.9"));
            Assert.IsFalse(UpdateCheck.IsNewer("0.1", "0.1.0"));

            // Nothing here may throw on rubbish - a malformed feed must read as
            // "no update", never as a crash on startup.
            Assert.IsFalse(UpdateCheck.IsNewer("", "0.1.0"));
            Assert.IsFalse(UpdateCheck.IsNewer(null, "0.1.0"));
            Assert.IsFalse(UpdateCheck.IsNewer("garbage", "0.1.0"));
            Assert.IsTrue(UpdateCheck.IsNewer("0.2.0-rc1", "0.1.0"),
                "a suffix should not stop the numbers being read");
        }

        /// <summary>
        /// Try again can be turned down, and turning it down lets the table go.
        ///
        /// The offer holds the Rolling phase open for everybody, because the
        /// reroll only becomes legal once every die is down - which is the same
        /// instant the phase would otherwise close. That is right, but it left
        /// the holder no way to say no: the table waited on them until the clock
        /// ran out, every turn the card was in play.
        /// </summary>
        [UnityTest]
        public IEnumerator TryAgainCanBeDeclinedSoThePhaseMovesOn()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Rolling);

            var game = ServerGame();
            game.Players[0].Compound.Add(
                new CardInstance(-701, CardDatabase.Instance.Get(CardIds.TryAgain)));

            ApplyAsHost(_ => game.RollPrimaryDice());
            yield return WaitForFrames(3);

            Assert.IsTrue(_manager.View.Viewer.canReroll,
                "holding Try again should leave the offer open");
            Assert.AreEqual(nameof(TurnPhase.Rolling), _manager.View.phase,
                "and the phase should wait rather than closing over it");

            // It has to be reachable from the phase's own controls, not only
            // from a popup inside the card in your compound.
            Canvas.ForceUpdateCanvases();
            Assert.IsNotNull(FindButtonNamed("Reroll"), WhyUnusable("Reroll"));

            var keep = FindButtonNamed("Keep Roll");
            Assert.IsNotNull(keep, WhyUnusable("Keep Roll"));

            keep.onClick.Invoke();
            yield return WaitForFrames(4);

            Assert.IsFalse(_manager.View.Viewer.canReroll,
                "keeping your roll should close the offer");
            Assert.IsNull(_manager.LastError);

            // And with nothing owed, the phase is free to move.
            Assert.IsFalse(game.CanReroll(0), "the rules should agree it is spent");
        }

        /// <summary>
        /// Nothing activates while the dice are still in the air.
        ///
        /// The server advances into Activation the instant the last die is
        /// rolled, which on this machine is while the dice are still tumbling -
        /// so without this a unit rises over the top of a roll nobody has seen
        /// the end of.
        ///
        /// Driven through a stage of its own, because on a machine that cannot
        /// show dice the board correctly never waits, so the board's own stage
        /// would never exercise the gate.
        /// </summary>
        [UnityTest]
        public IEnumerator NothingActivatesWhileTheDiceAreStillRolling()
        {
            yield return StartGame();

            yield return AdvanceTo(TurnPhase.Rolling);

            // Something in play to wake up, and a roll that wakes it.
            var game = ServerGame();
            game.Players[0].Compound.Add(
                new CardInstance(-601, CardDatabase.Instance.Get(CardIds.SolarPanels)));

            ApplyAsHost(_ => game.RollPrimaryDice());
            ApplyAsHost(_ =>
            {
                foreach (var player in game.LivingPlayers.ToList())
                {
                    game.SetPrimaryDie(player, 6);
                }

                game.AdvancePhase();
            });

            yield return WaitForFrames(2);
            ExpireNextActivation();
            yield return WaitForFrames(2);

            Assert.Greater(_manager.View.activationCompletedCount, 0,
                "something should have activated to have anything to show");

            var canvas = Object.FindAnyObjectByType<Canvas>();
            var stage = ActivationStage.CreateOn(canvas.transform);

            stage.Present(_manager.View, diceStillRolling: () => true);
            yield return WaitForFrames(3);

            Assert.IsFalse(stage.gameObject.activeSelf,
                "a unit must not rise while the dice are still rolling");

            stage.Present(_manager.View, diceStillRolling: () => false);
            yield return WaitForFrames(3);

            Assert.IsTrue(stage.gameObject.activeSelf,
                "and it should go ahead once they have stopped");

            Object.Destroy(stage.gameObject);
        }

        /// <summary>
        /// The hand fan tightens rather than overflowing.
        ///
        /// The visible check on the open hand only ever sees the test window,
        /// which is wide enough that the fan never has to make this decision -
        /// it passes with the fix removed. The failure needs a narrow tray or a
        /// full hand, so it is checked here as arithmetic, across the sizes a
        /// real window actually reaches.
        /// </summary>
        [Test]
        public void TheHandFanTightensInsteadOfRunningOffTheTray()
        {
            // The proportions a rotated card occupies, from the board's own
            // constants - a 4 degree tilt on a card 1.4 times as tall as it is
            // wide.
            const float rotatedWidthUnits = 1.096f;
            const float minCardWidth = 96f;

            foreach (var tray in new[] { 320f, 480f, 700f, 900f, 1400f })
            {
                for (var count = 1; count <= 7; count++)
                {
                    var overlap = BoardUI.HandFanOverlapFor(
                        tray, minCardWidth, rotatedWidthUnits, count);

                    var span = minCardWidth * (rotatedWidthUnits + (overlap * (count - 1)));

                    // Either it fits, or the fan is already as tight as it is
                    // allowed to get and the cards themselves are simply wider
                    // than the tray - which is a sizing problem, not a fan one.
                    var tightest = Mathf.Approximately(overlap, 0.30f);
                    Assert.IsTrue(span <= tray + 0.5f || tightest,
                        $"{count} cards in a {tray}px tray span {span:0.#}px at overlap {overlap:0.00}");

                    Assert.That(overlap, Is.InRange(0.30f, 0.82f),
                        "the overlap should stay between readable and tightest");
                }
            }
        }

        /// <summary>
        /// The card under the pointer comes out from under its neighbours.
        ///
        /// The fan overlaps on purpose, so without this the card being looked at
        /// is the one half-covered by the card next to it.
        /// </summary>
        [UnityTest]
        public IEnumerator HoveringAHandCardBringsItToTheFront()
        {
            yield return StartGame();
            yield return AdvanceTo(TurnPhase.Buy);
            yield return ExpandHand();
            Canvas.ForceUpdateCanvases();

            var handRow = (RectTransform)typeof(BoardUI)
                .GetField("_handRow", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_board);

            var cards = handRow.GetComponentsInChildren<BoardCardView>();
            Assert.Greater(cards.Length, 2, "this needs a hand with cards behind others");

            // The leftmost card is painted first, so it is the one buried.
            var buried = cards
                .OrderBy(card => card.transform.position.x)
                .First();

            var slot = (RectTransform)buried.transform.parent;
            Assert.Less(slot.GetSiblingIndex(), handRow.childCount - 1,
                "the outermost card should start underneath the others");

            buried.OnHoverChanged?.Invoke(true);
            yield return WaitForFrames(1);

            Assert.AreEqual(handRow.childCount - 1, slot.GetSiblingIndex(),
                "hovering a card should bring it out in front");

            buried.OnHoverChanged?.Invoke(false);
            yield return WaitForFrames(1);

            Assert.Less(slot.GetSiblingIndex(), handRow.childCount - 1,
                "and it should drop back when the pointer leaves");
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

            // A player question is answered by pressing the player's own track,
            // not by picking their name out of a list - everything the decision
            // needs is already drawn there.
            var offered = stage.transform.Find("All Player Tracks")
                .GetComponentsInChildren<Button>()
                .Concat(stage.transform.Find("Your Track").GetComponentsInChildren<Button>())
                .ToList();

            Assert.GreaterOrEqual(offered.Count, 2,
                                  "both opponents should be offered as targets");
            Assert.IsTrue(choiceRow.gameObject.activeInHierarchy,
                          "and the menu should say so rather than sitting empty");

            // No prompt text of its own - the card is on screen saying what it does.
            var popup = GameObject.Find("Popup Panel");
            Assert.IsTrue(popup == null || !popup.activeInHierarchy,
                          "the board popup must not offer the same decision as the stage");

            // Answering it lets the sequence carry on.
            offered[0].onClick.Invoke();
            yield return WaitForFrames(6);

            Assert.IsFalse(_manager.View.hasPendingChoice,
                           "answering on the stage has to actually answer the card");
        }

        /// <summary>The bottom edge of a rect, on screen.</summary>
        private static float BottomOf(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return corners[0].y;
        }

        /// <summary>The top edge of a rect, on screen.</summary>
        private static float TopOf(RectTransform rect)
        {
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return corners[1].y;
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
