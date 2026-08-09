# Editor Handoff Log

Use this file as a running handoff between editors. Add a dated entry after each editing session, identify the editor, list the exact files and behavior changed, record verification performed, and note any incomplete work. Keep newest entries first.

## 2026-08-08 — Claude (presentation polish: motion, entrances, phase banner)

### Edits

- `Assets/Scripts/Net/PhaseBanner.cs` (new) — each phase is announced as it
  begins: the name sweeps across the board, holds, and fades, with a line saying
  what the phase wants. The status line already named the phase, but a line of
  text that quietly changes is easy to play straight past.
- `Assets/Scripts/Net/BoardEffects.cs` — added `FadeIn`, `Pop`, `Hover`, and
  `CancelAll`. Cards are dealt in with a staggered fade; a resource or a wound
  landing knocks the pip or bar it lands in, so a count changing has a visible
  cause; cards lift under the pointer.
- `Assets/Scripts/Net/BoardArt.cs` — a generated vertical gradient behind the
  whole board, darker at the edges, so the panels read as one surface rather
  than boxes on flat colour.
- `Assets/Scripts/Net/BoardUI.cs` — tighter margins and a larger share of width
  to the battlefield.

### Two traps worth recording

- **Entrance animations are alpha only, deliberately.** Sliding or scaling a card
  in would move it while the tests are measuring whether it is fully visible
  inside its mask, and those tests are the only thing standing between us and
  another round of "passing tests, broken board". A fade changes nothing the
  layout decided.
- **`??` does not work on Unity objects.** `GetComponent<T>() ?? AddComponent<T>()`
  uses reference equality, so a destroyed component is treated as present and
  handed back, throwing the moment it is touched. Only Unity's own `== null`
  understands destroyed objects. This produced a `MissingComponentException` in
  eighteen tests at once.
- `BoardEffects` survives scene changes, so it outlives any one board. `BoardUI`
  now calls `CancelAll` as it is destroyed; without it the driver's coroutines
  keep reaching for widgets that have gone.

### Verification

- `./Tools/PlayModeTests/run.sh` — 26 pass. They fail on any logged exception,
  which is what caught the `??` bug.
- `./Tools/CompileCheck/run.sh`, `./Tools/RulesCheck/run.sh`,
  `--fuzz 1200`, `./Tools/SmokeTest/run.sh` all pass.


## 2026-08-08 — Claude (nine refinements: fewer confirmations, priced hands, visible dice)

### Rules

- The first pick moves one seat round the table each draft, so choosing first is
  shared out rather than belonging to whoever was drawn for it at the start.
- Every stone discounts every card in hand, not only Units. The cursed ones do
  the same and still charge a point of maximum health.
- A hand limit of seven, enforced as the turn closes: anybody over it chooses
  which cards to throw away. Deliberately not enforced at draw or draft time -
  the draft hands out a fixed number of picks, so a player at the limit
  mid-draft would have nowhere to put them and would stall the table.

### Presentation

- The Activation phase closes itself once its effects have resolved, after a
  short dwell. Nothing there is a player's move.
- Cards in hand are priced for their holder: a discount shows the printed cost
  struck through beside what it actually costs, and anything affordable right now
  is marked. `CardView` carries `costForYou`, `isDiscounted`, and `canAfford`,
  filled in only for the viewer's own hand - the discount depends on their own
  compound, so it is theirs to know.
- Each player's die is shown on their bar once they have rolled, which is what
  makes Try Again and Baal decisions rather than guesses.
- A Ritual dims the board harder than an ordinary preview and reclaims the top of
  the canvas, so the hand tray cannot be drawn over it.
- Hand cards are sized so a full hand of seven fits across without scrolling.

### Verification

- `./Tools/PlayModeTests/run.sh` — 26 pass; three new ones cover the dice being
  visible, discounts and affordability being marked, and Activation closing
  itself.
- `./Tools/RulesCheck/run.sh` — three new checks cover draft rotation, a stone
  discounting a Ritual, and a hand over the limit being cut back.
- `./Tools/CompileCheck/run.sh`, `--fuzz 800`, `./Tools/SmokeTest/run.sh` pass.

### Still outstanding

Instant-speed Rituals (Close Enough after the dice land, before they resolve)
remains unimplemented - it needs a priority and response system rather than a
card fix.


## 2026-08-08 — Claude (thirteen refinements; one deferred)

### Edits

- **Rules.** Health now caps at 20 (a point of headroom above the starting 19),
  and followers floor at 1 - the follower track is a race to twenty, not a second
  health bar. `GameSettings.MaxHealth` and `MinFollowers`.
- **Board sizing.** `RefreshBattlefield` plans every row it will show, then sizes
  them all together against the height available, so the draft and every
  compound are on screen at once. Units are ordered by activation number.
- **Ready control.** Disabled while the player still owes the phase something
  ("Roll your die first"), and pulsing when readying is their only remaining
  move. Taking the high roll bonus already finished the phase; that was in the
  previous pass and is covered by a test now.
- **Motion.** Shake only fires when a card that actually deals damage activates.
  Activation glow is coloured by what the card does - red damage, green
  followers, blue draw, yellow healing - via `BoardArt.ColorOfCategory`, reusing
  the `ActivationCategory` the rules engine already computes. Health losses send
  motes into the bar they emptied.
- **Bars.** Real tracks with a dark bed and a coloured fill, labelled over the top.
- **Hand.** Now a canvas-level overlay sized to the cards it holds, sitting above
  the dock. It used to be a full-width opaque block that shoved the battlefield
  upward every time it opened.
- **Rituals.** A resolving Ritual is thrown up over the board for a beat and then
  falls to the discard. `GameView` carries `lastRitualId` and a `ritualCount`,
  because the id alone cannot distinguish the same Ritual twice from a view that
  has not changed.
- **Discard.** Now a real place: public in the view, opened as a board row.
- **Win screen.** A large verdict, a subtitle explaining how it ended, and a
  scoreboard drawing each leader's final followers and health as bars.

### Deferred, deliberately

Instant-speed Rituals - playing Close Enough after the dice land but before they
resolve - is not implemented. The effect engine resolves a queue to completion
and has no notion of a window in which a player may respond, so this is a
priority/response system rather than a card fix. Flagged as understood, not done.

### One trap worth recording

`CardPreview.FlashRitual` started a coroutine on an inactive GameObject, which
Unity refuses outright. The panel is now shown before the coroutine starts. The
smoke test caught it because it fails on any logged error, not just a throw.

### Verification

- `./Tools/PlayModeTests/run.sh` — 23 tests pass; three new ones cover every
  compound being on screen, Ready being disabled until the phase is dealt with,
  and the discard opening.
- `./Tools/CompileCheck/run.sh`, `./Tools/RulesCheck/run.sh`,
  `./Tools/RulesCheck/run.sh --fuzz 2000`, `./Tools/SmokeTest/run.sh` all pass.


## 2026-08-08 — Claude (presentation pass: sizing, preview, fewer confirms, motion)

### Direction from playtest

Seven refinements, the loop otherwise working: all cards visible at once, a
card preview on click, less text in the action panel, fewer confirmation steps
when collecting resources, a Ready button that is always visible, animated bars
with card glow and camera shake, and resources drawn as coloured discs that fly
from the picker to the player.

### Edits

- `Assets/Scripts/Net/BoardArt.cs` (new) — the resource palette, and two sprites
  generated at runtime rather than imported: a hard disc for resource pips and a
  soft one for the glow behind an activating card. Keeps the interface free of
  asset dependencies, like the rest of the Net layer.
- `Assets/Scripts/Net/BoardEffects.cs` (new) — bar slides, card swell-and-glow,
  arcing resource pips, and screen shake. Presentation only; it reacts to state
  the server has already decided, so a skipped frame never costs a player
  anything.
- `Assets/Scripts/Net/CardPreview.cs` (new) — a card blown up over the board with
  its rules text readable and its action offered. This is what makes shrinking
  the cards acceptable.
- `Assets/Scripts/Net/BoardUI.cs` — card rows are now wrapping grids sized so
  every card fits on screen (`CardWidthThatFits`), cards scale rather than
  scroll. Ready moved out of the scrolling action panel into the dock, so the
  control that ends a phase can never be covered by the hand tray. Action text
  cut down to the phase's action and nothing already shown elsewhere. The
  resource picker is now coloured discs that throw a pip toward the player's own
  pips.
- `Assets/Scripts/Net/StatBar.cs` — resources are coloured discs with the count
  inside; both bars animate to their new value.
- `Assets/Scripts/Net/NetworkGameManager.cs` — collecting resources, and the last
  die landing, now finish the phase for that player rather than needing a
  separate Ready press.

### One trap worth recording

`GameState.SetReady` throws during Rolling until every die is down. The first
version of the auto-ready called it unconditionally, so the exception aborted
the enclosing request and the roll that had already succeeded was never
broadcast - the player pressed Roll and nothing happened. Auto-ready now only
runs when it is known to be legal. Anything added inside `Apply` after a
successful mutation must not be able to throw, or it discards the mutation.

### Verification

- `./Tools/PlayModeTests/run.sh` — 20 tests pass. Four are new: every draft card
  is on screen at once, clicking a card opens its preview, Ready stays reachable
  with the hand open, and taking the last resource ends the phase with no
  confirmation.
- `./Tools/CompileCheck/run.sh`, `./Tools/RulesCheck/run.sh`,
  `./Tools/RulesCheck/run.sh --fuzz 1500`, `./Tools/SmokeTest/run.sh` all pass.

### Incomplete work

- Card art is still a coloured rectangle with text. The preview is where a real
  card face would go first.


## 2026-08-08 — Claude (unreachable buttons, invisible resources, overlapping card text)

### Player report

- Resource management, purchasing, and recycling did not work at all.
- Card text sometimes overlapped in the hand.

### Why the tests missed it, again

The first pass of these tests looked controls up by name with
`FindObjectsByType` and pressed them. That finds a button whether or not a
player could ever reach it, so a board with its buttons laid out beyond the
edge of the panel that clips them passed a test that clicked those buttons.
`FindButtonLabelled` now only returns controls that are interactable and fully
inside every scroll viewport above them, and reports why a control was
unusable when one is missing. Both purchasing bugs failed immediately after
that change.

### Root causes

- **Play and Recycle were off-screen.** The wrapper holding a card and its two
  buttons declared a width but no height. Card strips position children without
  resizing them, so a `LayoutElement` height is ignored there - the rect itself
  has to be sized, exactly as `BoardCardView` does. The buttons were laid out
  below the strip that clips them: built, interactable, and permanently out of
  reach.
- **Resources were never displayed anywhere.** Not on the stat bars, not in the
  action panel. Players could collect them and spend them but never see them, so
  there was no way to tell what was affordable.
- **Card text overlapped.** Labels are created set to overflow, so a long effect
  drew straight over the row beneath it whenever a card was too short to hold
  everything.

### Edits

- `Assets/Scripts/Net/BoardUI.cs` — hand card wrappers are sized explicitly
  (`HandCardHeight`), and the hand strip is measured from what it holds.
- `Assets/Scripts/Net/StatBar.cs` — added a resource row, coloured per resource,
  shown for every player. `BarHeight` is now the single source of truth for the
  bar's size and the board sizes its top row and dock from it.
- `Assets/Scripts/Net/BoardCardView.cs` — card text clips instead of overflowing,
  and the effect row takes the space the fixed rows leave.

### Verification

- `./Tools/PlayModeTests/run.sh` — 16 tests pass. Five are new and fail without
  these fixes: pressing colour buttons collects resources, Recycle trades a card
  for a resource, Play buys a card, your resources are on screen and stay up to
  date, and no card text row can overflow onto another.
- `./Tools/CompileCheck/run.sh`, `./Tools/RulesCheck/run.sh`,
  `./Tools/RulesCheck/run.sh --fuzz 1500`, `./Tools/SmokeTest/run.sh` all pass.

### Note for the next editor

Never assert against a control found by name alone. If the test does not check
that the control is inside every mask above it, the test is not checking
anything a player experiences - that mistake has now produced three separate
rounds of "passing tests, broken game".


## 2026-08-08 — Claude (the draft dead-end and the missing card titles)

### Player report

- Card titles were never visible.
- The game could not progress past the draft: once everything was drafted it sat
  static and the dice never rolled.

### Why the existing tests missed both

`Tools/SmokeTest` rendered views into the board by calling `RenderForTesting`
directly, with a `GameView` built in-process. The real client never does that -
it receives a view that has been through `JsonUtility`. Every assertion the
smoke test made was true of a view that never crossed the wire, so it passed
while the actual game was unusable. Added `Assets/Tests/PlayMode/BoardRenderTests.cs`,
which drives a live host, a real `BoardUI` running its own `Awake`, and state
arriving through the network layer. Both bugs reproduced immediately.

### Root causes

- **Nothing past the draft rendered.** `JsonUtility` cannot represent a null
  nested object: it revives `GameView.pendingChoice` as a blank instance on the
  client. Every client therefore believed a card was permanently waiting for an
  answer, took the pending-choice branch in `RefreshActionPanel`, drew one empty
  label, and never rendered a phase interface again. The draft still worked only
  because the draft zone is drawn by `RefreshBattlefield`, which does not consult
  `pendingChoice`.
- **Titles were sliced off.** Card strips were built `BoardCardView.Height + 6`
  tall, but had to hold a 250px card plus 8px of content padding. The card
  overflowed its own scroll viewport upward, and the title sits at the top of the
  card, so the mask cut it away on every card on the board.

### Edits

- `Assets/Scripts/Net/GameView.cs` — added `hasPendingChoice`, since the
  reference itself cannot be trusted to be null after serialization.
- `Assets/Scripts/Net/GameViewBuilder.cs` — sets it.
- `Assets/Scripts/Net/BoardUI.cs` — every consumer now tests the flag. Card
  strips are sized from the card plus its padding (`CardStripHeight`), and the
  hand strip uses the same measurement.
- `Assets/Scripts/Net/UIFactory.cs` — `ScrollContentPadding` is public so strips
  can be sized around it; strip content no longer controls child height, which
  was squeezing cards until they overflowed.
- `Assets/Scripts/Net/NetworkGameManager.cs` — the draft now has a timeout. It
  was the one phase with no clock, so a single player who never picked held the
  table forever with no way out. A pick nobody makes is taken for them, and the
  rules engine still decides what is legal, so blocked and reserved cards are
  never handed out by mistake.

### Verification

- `./Tools/PlayModeTests/run.sh` — 11 tests pass, including three new ones that
  fail without these fixes: titles visible through every mask that clips them,
  a working ROLL DIE button after the draft, and an abandoned draft that still
  finishes.
- `./Tools/CompileCheck/run.sh`, `./Tools/RulesCheck/run.sh`,
  `./Tools/RulesCheck/run.sh --fuzz 2000`, `./Tools/SmokeTest/run.sh` all pass.

### Note for the next editor

Do not assert against a `GameView` you built in-process. Anything that travels
to a client goes through `JsonUtility`, which drops nulls and revives them as
blank objects - test through the network layer or you are testing nothing.


## 2026-08-08 — Claude (fuzzing the rules, and proving the app actually runs)

### Direction

Take the game to a bare-bones but fully playable alpha, prioritising logic
correctness over appearance. The Unity Editor was closed for this session, so
batchmode was available throughout.

### What was missing

Nothing had ever verified that the game *ran*. CompileCheck type-checks and
RulesCheck exercises the rules engine, but neither can construct a Canvas,
start a NetworkManager, or play a game to its end. Several whole-game states
had therefore never been reached even once.

### Edits

- `Tools/RulesCheck/Fuzz.cs` (new), run via `./Tools/RulesCheck/run.sh --fuzz N`
  - Plays complete games with random legal moves across 2-4 player tables,
    checking after every action that every card is in exactly one place, stats
    are in range, and any open question is answerable by a living player.
  - Reports how games end, which is design feedback the rules checks cannot give.
- `Assets/Scripts/Core/GameState.cs` — six rules faults the fuzzer found:
  - The engine could not deal a new draft by itself; only NetworkGameManager
    knew to call `BeginDraft`, so any other driver deadlocked after three turns.
  - Players dying together left no winner, no survivors, and no way to end.
    That is now a draw (`IsDraw`).
  - An effect could kill the player it was about to question, then wait forever.
  - Dead leaders stayed in the draft order and could still buy and collect.
  - End-of-turn damage resolves after the draft order is built, so a flame
    counter could take out someone already in the running order.
  - A long four-player game can exhaust 138 cards; running dry crashed.
- `Assets/Scripts/Net/GameViewBuilder.cs` (new) — the per-player view filter,
  pulled out of the NetworkBehaviour and made Unity-free so RulesCheck can prove
  no view carries another player's hand.
- `Tools/SmokeTest/run.sh` + `Assets/Scripts/Editor/AlphaSmokeTest.cs` (new) —
  runs inside real Unity; found that BoardUI, StatBar and BoardCardView all
  built themselves in `Awake`, which Unity does not call outside play mode, and
  that `DestroyChildren` used `Object.Destroy`, which never runs without a frame.
- `Assets/Scripts/{Core,Net,Editor}/*.asmdef` (new) — three assemblies, so a
  test assembly can reference the game at all.
- `Tools/PlayModeTests/run.sh` + `Assets/Tests/PlayMode/MultiplayerTests.cs`
  (new) — a real NetworkManager with RPCs over the wire.
- `Assets/Scripts/Net/NetworkGameManager.cs`
  - Seats are now a seat rather than a client id, so a connection can come and
    go without the board going with it. A player who dropped can rejoin and
    reclaim their seat; previously the code claimed this worked but did not.
  - Game-over is a real state: standings, and the host can start another game.
  - A question whose player has gone quiet answers itself after a timeout,
    rather than stopping the table for good.

### Verification

- `./Tools/CompileCheck/run.sh` — clean.
- `./Tools/RulesCheck/run.sh` — all checks pass, including new sections for the
  nine settled cards, activation order, end states, and per-player views.
- `./Tools/RulesCheck/run.sh --fuzz` — 100,000 games clean before the per-seat
  rolling change, then 50,000 clean after it.
- `./Tools/SmokeTest/run.sh` — 28 checks pass in a real Unity process.
- `./Tools/PlayModeTests/run.sh` — 8 tests pass.

### Design note

Across 5,000 random games: 20% ended on followers, 76% by elimination, 4% drawn,
averaging 6.9 drafts. Both win conditions are reachable and games terminate at a
sensible length. This is random play, not skilled play - a real table pursuing
the follower win deliberately should reach it more often than 20%.

### Incomplete work

- Players cannot set their own name; seats are "Leader 1", "Leader 2" in order.
- A reconnecting player claims the first empty seat rather than being recognised
  as themselves. Netcode issues a fresh client id per connection, so telling
  them apart needs a connection-approval token. Fine for a friends playtest,
  not for strangers.


## 2026-08-07 — Codex (responsive flex layout and visible Rolling action)

### Player report

- The replicated phase reached `Rolling`, but the window only displayed the phase name and no usable action.
- Existing fixed spacing did not fit the main Game view and smaller Multiplayer Player windows.

### Confirmed presentation failure

- Gameplay state was already entering `Rolling`; the failure was in presentation, not dice rules.
- The 380-pixel non-scrollable action column could be vertically clipped by the open 250-pixel-card hand tray.
- The entire board was scaled against a 1920x1080 reference and matched only width, making controls and card text too small in common playtest windows.

### Edits

- `Assets/Scripts/Net/UIFactory.cs`
  - Changed the canvas reference from 1920x1080 to 1280x720.
  - Balanced width and height scaling at `0.5` so text and controls remain readable across window aspect ratios.
- `Assets/Scripts/Net/BoardUI.cs`
  - Rebuilt the middle row as a flex-style layout: battlefield minimum/preferred/flex widths are `360/760/3`, while controls use `280/340/1`.
  - Replaced the fixed action column with a masked, vertically scrollable viewport and content container.
  - Reset the controls viewport to its top whenever state is redrawn, keeping the current primary action in view.
  - Made the opponent stat-bar row horizontally scrollable and retained horizontal scrolling for battlefield card rows.
  - Reduced outer/draft insets to recover usable space.
  - The hand now starts collapsed, collapses automatically on entry to Draft/Rolling/Activation/Resource, and opens automatically only on entry to Buy; the existing hand toggle still works.
  - Moved a large green `ROLL DIE` button to the first position in Rolling controls. It is 240x54 and still invokes the existing per-player Roll RPC.
  - No dice, phase, activation, resource, draft, or card-effect logic changed in this pass.
- `Assets/Scripts/Editor/AlphaSmokeTest.cs`
  - Strengthened the exact draft-to-Rolling regression so it measures each seat's Roll button against the visible action viewport, requires at least 200x50, and fails if clipped on any edge.
  - Requires the hand tray to be collapsed for every player perspective on entry to Rolling.
  - Replaced the old fixed-380px assertion with checks for flex proportions, readable minimum width, vertical scrolling, mask wiring, and containment inside the middle frame.

### Verification

- `./Tools/CompileCheck/run.sh` passed: `Compiles clean.`
- `./Tools/RulesCheck/run.sh` passed every rules check, including draft closure, individual rolls, ready gating, activation, and the full three-turn loop.
- `./Tools/SmokeTest/run.sh` passed in a real Unity 6000.5.7f1 process.
- Smoke measurements: 12/12 draft titles visible; three leftovers produced `Rolling`, zone 0, discard 3; players 0, 1, and 2 each had a fully visible 240x54 Roll button at viewport y `230..284` inside `-296..296`; every Rolling perspective had its hand collapsed.
- Final flex measurements were battlefield 734 pixels and controls 336 pixels; the controls viewport was 336x591, vertically scrollable, masked, and both columns remained inside the middle frame.
- `git diff --check` passed.

### Incomplete work

- None in this pass. The Unity Editor and all existing Multiplayer Player processes must be fully quit and relaunched before manual retesting so they load the rebuilt runtime UI.

## 2026-08-07 — Codex (explicit player rolling and guaranteed title row)

### Direction from playtest

- Stop automatically rolling for the whole table.
- Give every player their own visible **Roll Die** action during Rolling.
- Make card titles visible without relying on the failing title layout field.

### Edits

- `GameState` now records which living players have rolled. `RollPrimaryDie(playerId)` rolls only that player's die and rejects a second roll.
- The network Roll RPC now rolls the requesting player only.
- Each `PlayerView` exposes `hasRolled`, allowing every client to see completed results and who is still waiting.
- Rolling UI shows all results so far, gives the viewer a **Roll Die** button until used, and withholds Ready until everyone has rolled.
- Once all players have rolled, Rolling previews each Unit that the primary-die results will activate, including repeated activations from duplicate matching rolls.
- Once all dice are down, players ready up and Activation shows the rolled values and activates matching units through the existing rules.
- If the Rolling phase itself times out, only missing dice are rolled before Activation so a disconnected player cannot deadlock the table.
- Card titles were moved into the first card header row—the same row that was already successfully displaying colour/type. Colour/type moved into the smaller detail row underneath. The title uses a fixed 52-pixel wrapped area and no Best Fit.
- After the runtime test proved titles existed but the playtest still found them unreadable, temporary cards were enlarged from 150x210 to 180x250 and the title band from 14-point/52 pixels to outlined 20-point/76 pixels. This compensates for CanvasScaler reduction in narrow Game and Multiplayer Player windows.
- The earlier automatic roll in `ResolveEffects()` was removed.

### Preserved behavior

- High-roll/tie logic waits until all living players have rolled.
- Rerolls and Baal manipulation remain in Rolling after all initial dice are down.
- The pending-choice timeout fixes from the previous entry remain in place.

### Verification

- `./Tools/CompileCheck/run.sh` passed after the final activation-preview change; only the pre-existing `CardDatabase` unused-field warning remains.
- `./Tools/RulesCheck/run.sh` passed every check, including per-player rolling, roll-before-ready enforcement, high-roll handling, activation ordering, and three-turn phase flow.
- `./Tools/RulesCheck/run.sh --fuzz 200` passed; all 200 games reached legal end states.
- Added a real-Unity smoke regression that renders every dealt draft card, inspects its `Title` Text component and dimensions, finishes the draft, and requires a visible `Roll Die` control from every player perspective.
- Added a Netcode PlayMode regression for the exact two-player transition: final pick, three leftovers discarded, replicated Rolling state, host-only roll, then opponent-only roll.
- Corrected `Indoctrination.PlayModeTests.asmdef`: it was restricted to the Editor platform, causing the PlayMode runner to report success with zero tests. The test assembly now targets runtime PlayMode and explicitly overrides its NUnit reference like Unity's own runtime test assemblies.
- Final real-Unity smoke result: 12/12 dealt cards carried their exact full title in an opaque 20-point, 76-pixel row whose bounds remained inside the card mask; three leftovers produced `Rolling`, draft zone 0, discard 3; player perspectives 0, 1, and 2 each rendered an active `Roll Die` control.
- Final Netcode result: six PlayMode tests actually executed and passed, including `FinishingDraftEntersRollingAndEachSeatRollsIndividually`.
- `Tools/PlayModeTests/run.sh` now fails if Unity reports zero test cases, preventing the previous false-positive `ALL PLAYMODE TESTS PASSED` result.
- The stricter runner exposed a randomized-first-drafter assumption in `RequestsReachTheServerAndViewsComeBack`; the test now advances legal placeholder-seat picks until the host genuinely owns the next RPC pick. All six tests passed on the final rerun.

## 2026-08-07 — Codex (confirmed draft-choice deadlock)

### Player report

- Card titles were still invisible.
- The table remained stuck after drafting with `0s until the card decides for itself`.

### Confirmed cause and fix

- `Assets/Scripts/Net/NetworkGameManager.cs`
  - Confirmed that `Update()` returned immediately for `TurnPhase.Draft` before it processed `PendingChoice`.
  - A draft-related card decision could therefore count down to zero in the UI but never call `AnswerPendingChoiceWithDefault()`; because unresolved choices block all other rules operations, rolling and later phases could not begin.
  - Moved pending-choice timeout processing ahead of the Draft/GameOver early return.
  - Made `BroadcastState()` start the server choice clock before building any outbound view, including choices created by timeout-driven phase advances.
- `Assets/Scripts/Net/BoardUI.cs`
  - Added a real local countdown for `choiceSecondsRemaining`; the UI previously displayed a frozen value from the last server snapshot.
  - Pending-choice countdowns now remain visible during Draft; only the ordinary draft phase clock stays hidden.
- `Assets/Scripts/Net/BoardCardView.cs`
  - Removed Unity legacy Text's `Best Fit` mode, which can calculate an invisible size during the first layout pass.
  - Titles now use deterministic 14-point bold wrapped text, a high-contrast warm-white color, and a fixed 52-pixel title block.

### Expected result

- A pending card decision can be answered normally or defaults when its timer expires, including during Draft.
- Once the final blocking choice clears, the authoritative game-state flow performs the automatic roll already documented below.
- Every temporary text card displays its title without waiting for a later layout rebuild.

### Verification

- `./Tools/CompileCheck/run.sh` passed; all Unity scripts compile cleanly apart from the pre-existing unused-field warning in `CardDatabase`.
- `./Tools/RulesCheck/run.sh` passed all checks, including immediate dice rolls after 2-, 3-, and 4-player drafts.
- `./Tools/RulesCheck/run.sh --fuzz 200` passed; all 200 games reached a legal end state.

## 2026-08-07 — Codex (basic-run fixes)

### Reported issues

1. Long card titles were clipped on the temporary text-only cards in the draft.
2. The game still did not reliably leave the draft and perform the first roll.

### Edits made

- `Assets/Scripts/Net/BoardCardView.cs`
  - Reserved a 42-pixel, two-line layout block for card titles.
  - Enabled best-fit sizing from 16 down to 11 points for unusually long titles.
  - This keeps the full title visible without changing card dimensions ahead of the planned illustrated card assets.
- `Assets/Scripts/Core/GameState.cs`
  - Moved the automatic roll handoff into the authoritative effect/phase resolver.
  - Whenever all start-of-turn effects and choices have finished and the game is in `Rolling`, the existing `RollPrimaryDice()` operation now runs exactly once.
  - This covers the end of the initial draft, later drafts, subsequent turns, and transitions that resume after a card choice.
- `Assets/Scripts/Net/NetworkGameManager.cs`
  - Removed the earlier network-layer rolling hook. It was dependent on a particular RPC path noticing the transition and was therefore not authoritative.
- `Tools/RulesCheck/RulesCheck.cs`
  - Updated checks to require valid rolled dice immediately after every 2-, 3-, and 4-player draft.
  - Updated later checks to use the automatic roll rather than manually starting Rolling.

### Logic preserved

- Dice values still come from `GameState.RollPrimaryDice()`.
- Tie handling, high-roll resource claims, rerolls, dice manipulation, start-of-turn effect order, activation, and every later phase retain their existing rules.
- A pending start-of-turn decision is still resolved before dice are rolled.

### Status

- Implementation complete.
- `./Tools/CompileCheck/run.sh` passed. The existing `CardDatabase.CardListWrapper.cards` unused-field warning remains.
- `./Tools/RulesCheck/run.sh` passed all checks, including immediate valid rolls after 2-, 3-, and 4-player drafts.
- `./Tools/RulesCheck/run.sh --fuzz 200` passed; all 200 games reached a legal end state.
- `./Tools/PlayModeTests/run.sh` could not start because `Temp/UnityLockfile` still reported the project as open. The Unity log showed an Editor shutdown/hang sequence; Codex did not delete the lock or force a second Editor process.
- Recommended manual confirmation: reopen Unity normally, complete the last draft pick, and verify that the status changes to Rolling with visible die values immediately. Also scan the longest title, `Titanstopper (Church of Walls)`, in the draft row.

## 2026-08-07 — Codex

### Request

Connect the end of drafting directly to the Rolling phase described in `Blind Playtesting.docx`, without changing the underlying game rules.

### Edits made

- Modified `Assets/Scripts/Net/NetworkGameManager.cs`.
- Added `RollWhenRollingBegins()`, a network-flow helper that invokes the existing `GameState.RollPrimaryDice()` operation when:
  - the game is in `TurnPhase.Rolling`;
  - no card choice is pending; and
  - the dice have not already been rolled this turn.
- Called that helper after a normal phase advance, after an RPC-backed game operation, and after a timed-out card choice resolves with its default answer.
- Result: taking the final draft pick now discards the three remaining cards through the existing rules flow, enters Rolling, rolls all living players' primary dice, and broadcasts the rolled state without waiting for a player to click **Roll Dice**.
- The same handoff applies when later turns enter Rolling, keeping the documented `Rolling → Activation → Resource → Buy` loop consistent.
- If a start-of-turn card effect requires a choice, the automatic roll waits until the final pending choice has resolved. This preserves the existing effect order and avoids bypassing decisions.

### Logic deliberately left unchanged

- No changes were made to `GameState`, dice generation, high-roll/tie handling, bonus-resource claiming, rerolls, dice manipulation, activation, resource collection, buying, recycling, the three-turn round length, or draft rules.
- The existing manual roll RPC and UI fallback remain available; ordinary play should no longer display the roll button because the server broadcasts an already-rolled state.
- Other modified and untracked files already present in the working tree were not edited as part of this request.

### Verification

- `./Tools/CompileCheck/run.sh` — passed; all Unity game scripts compiled cleanly. One existing `CardDatabase.CardListWrapper.cards` unused-field warning remained.
- `./Tools/RulesCheck/run.sh` — passed; all existing rules checks passed.
- `git diff --check` — passed.
- Unity PlayMode tests were not launched because the Unity Editor had the project open (`Temp/UnityLockfile`).

### Follow-up status

- Requested flow change is complete.
- Recommended next check: after closing the Unity Editor, run `./Tools/PlayModeTests/run.sh`, then manually finish a draft in a hosted game and confirm that rolled dice appear immediately.
