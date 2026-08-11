# Editor Handoff Log

Use this file as a running handoff between editors. Add a dated entry after each editing session, identify the editor, list the exact files and behavior changed, record verification performed, and note any incomplete work. Keep newest entries first.

> Standing rules that do not change between sessions - which checks to run, the
> architecture invariants, the Unity traps, and the UI shapes that look odd on
> purpose - live in [`AGENTS.md`](../AGENTS.md) at the repo root. Read that
> first; this file is the chronological record, not the rulebook.

## 2026-08-09 — Codex (semicircle draft target and unclipped hand fan)

The current drafter now gets a 620x112 maximum drop affordance behind the hand:
the upper half of a clipped glowing ellipse labelled `DROP TO DRAFT`. The hand's
transparent input rectangle grows to cover it, so the first pick and later picks
have the same generous target. Dragging a legal draft card into the target
brightens the arc and slightly enlarges it; every drag exit/disable restores its
resting glow through `DragHandle`'s new live-move callback.

The expanded fan now sizes against the rotated card bounds rather than the
upright PDF. Its maximum height grew to 318 pixels, card centres are separated a
little further, the middle rises instead of the angled outside cards, and cards
paint from the outside toward the centre. Together these keep all four PDF
corners inside the hand surface and stop later cards from visually slicing across
their neighbours' upper corners.

### Files

- Updated `Assets/Scripts/Net/BoardUI.cs` and
  `Assets/Scripts/Net/DragHandle.cs`.
- Added semicircle/hot-state and rotated-corner regression assertions to
  `Assets/Tests/PlayMode/BoardRenderTests.cs` and
  `Assets/Scripts/Editor/AlphaSmokeTest.cs`.

### Verification

- CompileCheck clean.
- RulesCheck all green.
- `git diff --check` clean.
- PlayModeTests and SmokeTest were not runnable because the local Unity
  6000.5.7/Hub licensing protocol mismatch still prevents the Editor from
  reaching project import. Their new regressions remain unexecuted until that
  external issue is cleared.

## 2026-08-09 — Codex (authoritative paced Unit activation)

Network games now opt into paced Activation. `GameState` still builds the same
first-drafter, round-the-table sequence from each player's chosen compound
order, including one entry for every matching shared/private die, but it exposes
that sequence and resolves only one living Unit per server beat. Rules-only
callers keep synchronous resolution. A paced Activation cannot be advanced while
effects remain, choices interrupt before their Unit is marked complete, and dead
controllers are skipped without consuming an empty presentation beat.

`GameView` now carries the activation batch, completion cursor, exact card/player/
die/category entries, and public card counters. This keeps every client on the
server's real order instead of deriving animations from dice after state has
already jumped to the end.

The board dims everything outside the remaining queue and gives queued Units a
white edge; repeated Units stay bright until their final matching die is spent.
Each completed Unit then opens a raycast-blocking, non-dismissible full-screen
stage with a 1.5x card and every player's large health/follower tracks visible
along the bottom. Damage jolts upward before track loss, follower effects move
down more gently, healing shakes and sends red hearts down, Block sends green
pluses, and other effects swell and settle. Stat bars interpolate only after the
card motion. Lethal final activations still play before the game-over view takes
over. Card counters now render as physical chip stacks and pop when their count
changes. Activation choices omit source-card/prompt chrome when the options are
self-explanatory; card choices become compact title buttons.

### Files

- Added `Assets/Scripts/Net/ActivationStage.cs` and its meta file.
- Updated `Assets/Scripts/Core/GameSettings.cs`,
  `Assets/Scripts/Core/GameState.cs`, `Assets/Scripts/Net/GameView.cs`,
  `Assets/Scripts/Net/GameViewBuilder.cs`,
  `Assets/Scripts/Net/NetworkGameManager.cs`,
  `Assets/Scripts/Net/BoardCardView.cs`, and `Assets/Scripts/Net/BoardUI.cs`.
- Added paced-order/cursor coverage to `Tools/RulesCheck/RulesCheck.cs` and a
  live host/stage regression to `Assets/Tests/PlayMode/BoardRenderTests.cs`.

### Verification

- CompileCheck clean.
- RulesCheck all green, including paced duplicate order, one-Unit resolution,
  cursor replication, and the no-skip guard.
- Fuzz: 800/800 games reached a legal end state (seed 1234).
- PlayModeTests and SmokeTest could not run: Unity 6000.5.7 repeatedly failed
  before compilation with licensing protocol 505 (`Unsupported protocol version
  '1.18.1'`) and no result XML. The hung batch process was stopped and its empty
  stale lock removed. The new PlayMode test is present but remains unexecuted.
- No known implementation work is intentionally left incomplete; Unity visual
  verification is still required once the local licensing client is healthy.

## 2026-08-09 — Codex (fanned hand, drag drafting, discount stamps)

The hand no longer draws a tray or uses a horizontal scroll layout. Its Image
is transparent but still receives hover and draft drops. Expanded cards scale
from the actual hand count up to their full 180x252 print, overlap at 62% of a
card width, rise slightly toward the outside, and fan between +9 and -9 degrees.
The collapsed peek uses the same silhouette at a smaller scale. During Buy the
fan shifts left enough to reserve the recycle bin on narrow player windows.

Ordinary draft cards no longer offer `Draft this card` from their enlarged
preview. Legal picks receive drag handles and draft only when dropped into the
hand. An empty hand remains a 34-pixel invisible drop surface with a quiet
`YOUR HAND · DROP DRAFT HERE` caption, so the first pick has a destination.
Card-choice effects in the draft zone retain their explicit Choose action.

Discounted printed cards now show one centered circular `−1` stamp per resource
actually removed from their cost. Each stamp uses that resource's color, so
stacked stones and multi-point reductions remain legible without rewriting the
PDF's printed price. The same stamp layer appears on the board/hand card and its
enlarged PDF popup. Code-built fallback cards keep their detailed adjusted-cost
text until their printed art exists.

The card text overlap test now measures rows in card-local coordinates. Its old
axis-aligned world bounds reported false overlaps as soon as the hand cards were
intentionally rotated; the underlying no-overflow guarantee is unchanged.

### Files

- Updated `Assets/Scripts/Net/BoardUI.cs`,
  `Assets/Scripts/Net/BoardCardView.cs`,
  `Assets/Scripts/Net/CardPreview.cs`,
  `Assets/Tests/PlayMode/BoardRenderTests.cs`, and
  `Assets/Scripts/Editor/AlphaSmokeTest.cs`.

### Verification

- CompileCheck clean.
- RulesCheck all green.
- PlayModeTests **35/35**, including the fanned-hand shape, drag-to-hand draft,
  thumbnail and enlarged-PDF discount stamps, and rotation-safe text geometry.
- SmokeTest passing through the complete board lifecycle.
- No fuzz run: no `GameState` or effect behavior changed.
- No incomplete work from this pass.

## 2026-08-09 — Codex (click-away previews, recycler, stable rolls)

Four related interaction cleanups landed together:

- Card previews no longer build a Close button. Their existing backdrop remains
  the dismiss target, and a plain printed card now appears without an empty
  control tray. Cards with a real action or mini-menu still receive only the
  controls they need below the print.
- The Roll Die button is back at 260x54; the oversized object was its generic
  460x400 question window. Rolling now uses a dedicated 300x94 frame that hugs
  the button, while card questions and game-over screens keep the full window.
- The repeated Recycle row was removed from every hand card. During Buy, one
  78x96 recycle bin appears at the right edge of the open hand. Every hand card
  drags: dropping it on the battlefield buys it only when affordable, while
  dropping it in the bin always recycles it through the existing server RPC.
  The client immediately predicts the earned resource and flies a color-matched
  pip from the bin into the permanent resource HUD; the server's next view
  remains authoritative.
- Battlefield cards now rebuild only when their actual hierarchy changes.
  Dice and ready updates refresh roll outlines and live card mini-menus in place,
  so rolling no longer destroys the table and restarts every entrance fade.

### Files

- Updated `Assets/Scripts/Net/BoardUI.cs`,
  `Assets/Scripts/Net/BoardCardView.cs`,
  `Assets/Scripts/Net/CardPreview.cs`,
  `Assets/Tests/PlayMode/BoardRenderTests.cs`, and
  `Assets/Scripts/Editor/AlphaSmokeTest.cs`.

### Verification

- CompileCheck clean.
- RulesCheck all green.
- PlayModeTests **34/34**, including click-away/no-Close, drag-to-recycle with
  resource flight, compact rolling frame, and stable card identity after a roll.
- SmokeTest passing through the complete board lifecycle; Rolling renders a
  260x54 button inside its 300x94 frame for every player perspective.
- No fuzz run: no `GameState` or effect behavior changed.
- No incomplete work from this pass.

## 2026-08-09 — Codex (compact Roll Die control)

The Rolling popup was using the generic maximum action-button size, making its
single control dominate the panel at 260x54. Roll Die now has its own compact
160x40 dimensions; larger decision and game-over controls keep their existing
responsive sizing. PlayMode and SmokeTest assertions pin both a usable minimum
and compact maximum so the button cannot silently stretch back across the panel.

### Files

- Updated `Assets/Scripts/Net/BoardUI.cs`,
  `Assets/Tests/PlayMode/BoardRenderTests.cs`, and
  `Assets/Scripts/Editor/AlphaSmokeTest.cs`.

### Verification

- CompileCheck clean.
- RulesCheck all green.
- PlayModeTests **33/33**.
- SmokeTest passing; Roll Die rendered at **160x40** for all three perspectives.
- No incomplete work from this fix.

## 2026-08-09 — Codex (first hand card no longer clipped)

Playable hand cards were being centred over zero-width `Card Slot` transforms.
The wrapper's vertical layout controlled child width, but a plain slot reports
no preferred width, so Unity collapsed it despite the explicit `SetSize`. This
put half of every card to the left of its intended position and clipped the
first card through the hand's scroll mask.

The wrapper now respects the explicit widths of its card slot and button row.
Horizontal scroll content is also explicitly reset to the viewport's left edge
after its pivot changes, and carries an 8-unit horizontal inset so the standard
6% card hover swell remains inside the mask. A PlayMode regression opens the
hand during Buy, hovers its first card, and asserts that the complete left edge
stays within the viewport.

### Files

- Updated `Assets/Scripts/Net/BoardUI.cs`,
  `Assets/Scripts/Net/UIFactory.cs`, and
  `Assets/Tests/PlayMode/BoardRenderTests.cs`.

### Verification

- CompileCheck clean.
- RulesCheck all green.
- PlayModeTests **33/33**, including the new first-card hover containment test.
- SmokeTest passing through the complete board lifecycle.
- No incomplete work from this fix.

## 2026-08-09 — Codex (printed-card popups)

Blue cards now open as a large 320x448 rendering of their imported 5:7 face
instead of the code-built title/effect popup. The existing live actions are not
painted over the card: `CardPreview` turns its old panel into a compact control
tray immediately below the print, growing it only when a card such as Baal has
extra controls. Cards without imported art keep the old text preview.

Ritual flashes animate the printed card itself and hide the control tray during
the fall to the discard. PlayMode's preview test now chooses a card with art and
pins the popup sprite, active state, aspect preservation, and 5:7 dimensions.

### Files

- Updated `Assets/Scripts/Net/CardPreview.cs` and
  `Assets/Tests/PlayMode/BoardRenderTests.cs`.

### Verification

- CompileCheck clean.
- RulesCheck all green.
- PlayModeTests **32/32**; the preview test asserts the exact imported sprite,
  active printed popup, aspect preservation, and 5:7 dimensions.
- SmokeTest passing through the complete board lifecycle.

## 2026-08-09 — Codex (the Blue printed-card pass)

Imported the complete first printed-art set from `AllBlue cards`: 33 one-page
PDFs matched one-to-one with the 33 Blue definitions and rendered into
`Assets/Resources/CardArt/` as 700x980 PNGs. Every source page is 5:7, so
`BoardCardView` moved from 180x250 to an exact 180x252 logical card. The image
preserves its aspect instead of stretching, and the old code-built title,
cost, activation, and effect rows remain the automatic fallback for every card
whose printed face has not been imported yet.

`CardArt.cs` loads and caches a face by the existing definition id. This means
the art appears everywhere backed by `BoardCardView`: draft, compounds, hand,
discard, and drag ghosts. Draft markers stay above the face, and affordability
and activation outlines still belong to the card itself. `CardPreview` remains
the canonical text/action view when a card is clicked; it was deliberately not
replaced because several cards build live controls there.

The four nonliteral filename matches were made explicit in
`Tools/import_card_art.py`: `Baal` -> `Baal_The_Manipulator`, `Double Agent` ->
`Double_Agent_Japanese_Art`, `Worshipper of the Bone God` -> the existing
single-p `Worshiper_of_the_Bone_God` id, and `Brain Washer` -> `Hydro_Plant`.
The last id stays stable so effects and network views do not change, but its
displayed title in `Cards.json` is now **Brain Washer**. RulesCheck pins that
rename.

The importer requires every PDF and every definition in the requested color to
match exactly once, renders only page one, and writes deterministic Unity meta
GUIDs. SmokeTest now proves every Blue face loads through `Resources` and is
5:7. The draft rendering checks in SmokeTest and PlayMode accept either a valid
printed face or the visible code-built title fallback.

### Files

- Added `Assets/Resources/CardArt.meta`, 33 PNGs and their 33 `.meta` files.
- Added `Assets/Scripts/Net/CardArt.cs` and `.meta`.
- Added `Tools/import_card_art.py`.
- Updated `Assets/Resources/Data/Cards.json`,
  `Assets/Scripts/Net/BoardCardView.cs`, `Assets/Scripts/Net/BoardUI.cs`,
  `Assets/Scripts/Editor/AlphaSmokeTest.cs`,
  `Assets/Tests/PlayMode/BoardRenderTests.cs`, and
  `Tools/RulesCheck/RulesCheck.cs`.
- Existing uncommitted `BoardEffects.cs` / `DragHandle.cs` work was preserved
  and is not part of this pass.

### Verification

- Visual QA of all 33 rendered faces: complete, upright, uncropped, 700x980.
- CompileCheck clean.
- RulesCheck all green, including the Brain Washer title assertion.
- PlayModeTests **32/32**.
- SmokeTest passing, including all Blue art/resource/aspect checks.
- No fuzz run: no `GameState` or effect behavior changed.

## 2026-08-09 — Claude (solo play against a bot)

One person can now play a whole game alone. `Solo Playtest` on the connect
screen hosts, seats bots up to the minimum table, and starts - all one press.
`Add Bot` in the lobby does the same thing a seat at a time, so bots can also
fill out a table that is short a player.

The bot is deliberately witless: it drafts the first legal card, takes a
rotating spread of resources, buys the first thing it can afford, readies up,
and answers questions with the same default the clock would have used. It is
not an opponent to beat, it is a second pair of hands so the turn loop,
activation order and board can be exercised without a second machine.

Three things about it are load-bearing rather than incidental:

- **`Seat.IsBot` is explicit, not inferred from having no connection.** A
  player who drops also leaves `ClientId` null, and their board has to sit
  untouched waiting for them. `TakeSeat` also skips bot seats when looking for
  a vacancy, or a joining player would be handed a bot's compound.
- **One action per beat, not one phase.** The bot pauses `BotThinkSeconds`
  between moves. A bot that finished its whole turn in a single frame would
  make the activation sequence - the thing most worth watching - impossible to
  see.
- **A refused move is an ordinary outcome.** The bot guesses, the rules engine
  throws on anything illegal, and that simply means it tries something else on
  the next beat. Nothing it does can put the game in a state the rules did not
  authorise.

Bots are never sent views (`BroadcastState` only writes to occupied seats), so
none of this touches the hidden-information path.

### The resource spread is not cosmetic

The bot originally took red every turn, which meant it could almost never
afford anything, never built a compound, and left activation with nothing to
show - which defeats the point of having an opponent. It now rotates colours.
Caught by the test asserting the bot ends up holding cards *and* resources
rather than merely that phases advanced.

### Verification

PlayModeTests 42/42 with `ABotPlaysAWholeTurnByItself`, which drives a solo
game through Draft, Rolling, Activation, Resource and Buy with only the human
seat scripted and everything else left to the bot. RulesCheck plus 600 fuzzed
games, SmokeTest, CompileCheck.

**Note for anyone writing tests against the bot or the activation sequence:**
the server's pauses are wall-clock, and batchmode runs frames far faster than
seconds, so a frame-counted loop starves them. `ClearPacingClocks` in the test
file pushes them into the past each iteration. Setting them to zero is not
enough - they are compared against `Time.time`, which is only a few seconds old
during a run.

### The art flake, now confirmed

`ClickingACardOpensItsPreview` has now failed on two separate runs with "no card
with imported art on the board" and passed on three others, with no relevant
change between them. It is a real intermittent, not a one-off. It belongs to
the in-progress card-art work and looks like art import ordering in batchmode
rather than anything about the preview. Left alone deliberately - it is not
mine to fix mid-flight - but it should not be dismissed as noise next time it
appears.

## 2026-08-09 — Claude (seven more playtest reports, and shouting)

**Hand clipping, third attempt - now measured.** The maths was adjusted twice by
reasoning about it and was wrong twice. `TheOpenHandIsFullyOnScreen` now asserts
the thing that actually matters: no part of any hand card is off the top of the
screen, and nothing masks it. That cannot be satisfied by arithmetic that merely
looks correct, which is what the previous two attempts were.

**Block reads as one bar now.** Two separate problems wearing the same
description: on the stat bar it sat in the parent row and inherited that row's
6px gap, so it looked like a detached box parked nearby - it lives in its own
zero-spacing row with health now, welded to the end of the red. On the
activation stage it was not a bar at all, just a line of text saying "+2 block";
it is a green track appended to health there too, growing and shrinking with the
number.

**Resource payouts animate.** Snapshots carry resource counts, so the stage can
diff them and throw coloured pips off the card. They fly toward where the
resource HUD lives even though the HUD is not on screen during the sequence, so
the direction still means something.

**Repeat activations collapse into one pop-up.** Asked about this rather than
guessing, because the two readings change balance: a unit woken by two matching
dice really does fire twice, and that stays. What changed is that both firings
are now taken together (`QueueActivations` dequeues a unit's whole run before
passing on) and the stage merges consecutive same-card entries into a single
presentation that strikes N times with a `×N` marker. The table still alternates
- it is one *unit* each, not one activation each. Bars move once, at the end, to
where every firing left them; animating per strike would mean easing toward
numbers the server never reported.

**Cards stopped re-dealing themselves.** The battlefield signature was already
suppressing needless rebuilds, but a rebuild that did happen faded in every card
on the board, so one card arriving looked like the whole table being re-dealt.
`_cardsDealtIn` tracks what has already been seen; only genuinely new cards
animate. Cards that leave the board entirely are forgotten, so one that comes
back is dealt in again.

**The hand no longer snaps shut on a phase change.** That reset was mine and it
was wrong - it fought anyone holding the hand open to read across a phase
boundary. Nothing needs to force it closed: the pointer decides, and the next
poll closes it if nobody is hovering.

**Shouting.** A message box in the dock that does nothing until somebody types
the passcode, after which it broadcasts to the table as a large banner
(`ShoutBanner`). The gate is **server-side** (`_shoutUnlocked` per seat) rather
than in the interface, because a gate the client keeps is one anybody can walk
through by editing their own copy. Capped at 80 characters and rate limited to
one every 1.5s per seat. The banner never blocks raycasts - a message is not
something to answer.

### Verification

PlayModeTests 41/41 (two new), RulesCheck plus 800 fuzzed games, SmokeTest,
CompileCheck. Two rules-order assertions were updated to the new grouping rather
than worked around.

### Flake worth watching

`ClickingACardOpensItsPreview` failed once ("no card with imported art on the
board") and passed on the two runs either side of it, with no change in
between. It belongs to the in-progress card-art work and looks like an
import-timing race in batchmode rather than a board bug. Not chased; if it
recurs, suspect art import order rather than the preview.

## 2026-08-09 — Claude (five playtest reports)

**Resources could not be taken with the hand open.** The tray is opaque and
answers the pointer, and it spanned the full width - so it was not merely
sitting over the resource HUD, it was eating the clicks meant for it. It now
stops clear of the HUD (`HandLeftInset`), and the fan measures its width from
the tray rather than the window. Checked as geometry in
`TheOpenHandLeavesTheResourceHudClickable`: the two rects must not overlap at
all, which is the actual invariant - anything softer passes while the bug is
present at a different screen size.

**Hand cards were clipped at the top.** Fan angle 9° -> 4°, centre lift 18 ->
8, and a `HandFanTopMargin` that is budgeted into the card sizing as well as
added to the tray height. The old maths was exact, which left nothing for the
outline, the hover lift, or a rounding error - and any of those clips a card.

**The extra-die card "did nothing".** It was working perfectly. Standardized
Uniforms granted the die, kept it private, and woke only its owner's units -
all confirmed by a new end-to-end RulesCheck. The card was invisible: the die
was never carried in the view or drawn anywhere, so its units woke on a number
that was not on the table. `PlayerView.privateDice` now carries it and the
stat bar shows it accented beside the shared die. This also fixed a real bug
downstream - `MarkIfDueToActivate` only looked at `primaryDie`, so a unit woken
solely by a private die sat dull and then activated anyway.

Worth keeping the check: it asserts the die reaches the *view*, not just the
rules, precisely because "works but cannot be seen" is what this was.

**Activation pacing.** `ActivationStepSeconds` 1.65 -> 3.4 and every tween
roughly doubled, with the bar animation given the largest share (1.2-1.35s) -
the strike is punctuation, the number moving is the point, and it used to be
over before it registered. The card now rises out of its own place on the
board (`BoardCardPosition` resolves it live from the battlefield, uncached,
because the board rebuilds constantly) so whose it is reads without a label.
The all-player HUD moved from the bottom of the stage to the top, so a damage
card jolting upward is jolting at the bars it empties.

**The blue half-circle** was a `BoardArt.Disc` stretched to twice its zone's
height and clipped to its upper half - the one round thing on a hard-edged
board. Replaced with a flat shelf: a faint band with a single lit edge along
the top, the line the card is dropped across. Both the PlayMode and smoke
assertions were pinned to the semicircle shape and now pin the shelf instead.

### Verification

All five green. PlayModeTests 39/39 (two new), RulesCheck with the new
Standardized Uniforms block plus 800 fuzzed games, SmokeTest, CompileCheck.

## 2026-08-09 — Claude (finishing the activation sequence Codex started)

Codex had built most of this and left it uncommitted. The server-side design
was sound and I kept all of it: `GameState.PaceActivations` +
`ResolveEffects(stopAfterOneActivation)` so a live game resolves **one** unit
per broadcast, `ActivationSequenceEntry` recording the planned order, and
`ActivationView[]` carrying it to clients. `ActivationStage` presents them by
diffing consecutive server views - it never decides anything, it only gives
already-decided changes room to be understood. That separation is worth
keeping.

What was wrong or missing:

**The crash.** `Present` called `StartCoroutine` while the stage root was still
inactive - trap #5 in AGENTS.md, and the one failing test. The root is woken
before the coroutine starts now, not inside it.

**Questions were asked in the wrong place.** A card that stops to ask something
mid-activation was routed to the board's popup, which is nowhere near the card
asking. The entry sitting at the `activationCompletedCount` mark is by
definition the one still resolving, so that is the card the question belongs
to. The stage now holds that card up with its options directly beneath it and
**no prompt** - the card is on screen at full size saying what it does. Order
is guaranteed by construction: the effect has not finished, so there is nothing
to animate yet; answering completes it, and only then does it animate.
`DecidePopup` returns false outright during Activation so the same decision
cannot be offered twice.

**Glow only applied during Activation**, so the roll and the sequence that
followed it used two different highlights for the same statement. Units woken
by the dice now light white and everything else falls away from the moment the
dice land, carrying straight through into the sequence.

**Counters had nowhere to go.** `CardView.counters` was already plumbed but
unused; they render as chips stacked on the card, one per kind with its count,
coloured stably by name hash so a counter is the same chip every time.

Also: healing hearts and block plus-signs now fly at the bar they change
rather than the panel around it.

Not changed, because it already worked: repeated die values repeat the
animation. `QueueActivations` enqueues one entry per (unit, matching die), so
two players rolling the same face genuinely produces two entries - the
sequence, not the presentation, is what repeats.

### Verification

All five green. PlayModeTests **37/37**, including Codex's pacing/repeat/glow
test (which the crash had been failing) and a new
`AQuestionMidActivationIsAskedOnTheStage` covering the stage-vs-popup routing.
RulesCheck clean plus 1200 fuzzed games - worth running here because Core's
resolution loop was modified, and a stepping bug there would be a hang rather
than a wrong pixel.

### Left undone

The stage's HUD rebuilds every activation rather than animating between them,
so bars jump to their starting value at each step before easing. Fine at the
current pace, would show if the dwell got shorter.

## 2026-08-09 — Claude (the hand flicker, actually fixed)

The tray was still flickering after the entry below, and never opened on hover.
The `Image` fix there was real but treated the symptom.

**The actual cause is architectural: hover was driven by `PointerEnter` /
`PointerExit`, and those fire in response to what is under the pointer - which
is exactly what opening the hand changes.** Open the tray, and `RefreshHand`
destroys and rebuilds the cards the pointer was over; the rebuild provokes a
pointer event; the event closes the tray; closing rebuilds it again. Every
frame. Any fix that keeps that loop shape is a fix to how *loudly* it rings,
not to whether it rings.

So the loop is gone rather than damped. `BoardUI.PollHandHover(Vector2)` is
called once a frame from `Update` with the mouse position and tests it against
the tray's rectangle. **The pointer's position is an input from outside the
game - rebuilding the tray cannot change it, so polling it cannot feed back on
itself.** Do not put an `EventTrigger` back on this row.

The hysteresis falls out for free, which is why there is no explicit slack or
timer anywhere: collapsed, the rect being tested *is* the peek strip, so the
pointer has to come right down to open it; open, the rect is the whole tray, so
it stays open across all of it. Crossing either edge moves the far edge away
from the pointer, so it cannot oscillate.

Two supporting changes:

- `RefreshHand` now early-returns when nothing about the hand has changed,
  keyed on `HandSignature` (which cards, affordability, open/shut, whether
  buttons show, board width). The board refreshes on every message from the
  server and was rebuilding the tray each time, restarting every card's deal-in
  fade - a second, independent source of visible flicker.
- `_draggingFromHand` holds the tray open for the whole of a drag, so pulling a
  card out onto the board does not close the tray out from under the gesture.
  Deliberately a flag rather than "is anything in the drag layer", because the
  drag layer is shared with flying resource pips.

### Testing this properly

`TheHandOpensOnHoverAndStaysOpen` holds the pointer on the tray and asserts it
is open on **all 30** of the next 30 frames, then off it and asserts shut on
all 30. Asserting "it is open" once is worthless here - the old bug passed
through "open" every other frame; only stability over time distinguishes them.

It drives `PollHandHover` directly rather than through a synthetic mouse. Both
`InputSystem.QueueStateEvent` and `InputState.Change` were tried first and
neither reported a position at all under batchmode (`mouse reads (0.00, 0.00)`,
while the tray's own rect was correct) - the device layer is not what is worth
testing here, and fighting it was costing more than it was proving.

### Verification

CompileCheck clean. PlayModeTests **32/32**, with the new hover test. SmokeTest
passing; one of its checks was reworded, since the row's `Image` is now there
to be a background and block click-through rather than to receive hover.

## 2026-08-09 — Claude (cold restyle, and popups that do not block)

### The look

The fantasy theme is gone. `UITheme` was rewritten rather than retinted, and
every member renamed, so nothing is left calling a cool grey "Parchment":

| was | is |
| --- | --- |
| `RitualBlack` / `DeepPlum` | `Void` / `Fog` |
| `Parchment` / `ParchmentMuted` | `Bone` / `BoneDim` |
| `RitualGold` / `RitualGoldSoft` | `Signal` / `SignalSoft` |
| `Moss` | `Affirm` |
| `AshBlue` | (gone - folded into `Signal`) |

Near-black surfaces with a faint blue-green cast, a cool off-white for text,
and **one** accent - a cold cyan (`Signal`) - deliberately rationed so it still
means something. It marks exactly three things now: a card you can afford, the
activation numbers on a unit, and a cost that has been discounted. Full-price
costs went to `BoneDim`, because "ordinary" should look ordinary.

Type is a clean grotesque throughout (Helvetica Neue / Inter / Avenir Next),
no serifs anywhere. `UITheme.Frame` is a one-pixel hairline now regardless of
the weight it is passed - the argument survives because callers use it to mean
"how important is this edge", which now reads as colour rather than thickness.
Buttons dropped their gold edging for the same hairline.

`BoardArt.Backdrop` lost the occult seal (two rings, inverted triangle, axis)
entirely. It is now an empty dark room: a soft pool of cold light low and
centre, corners falling away hard, fine grain to stop a flat dark field
banding on a big display. Resource and category colours were re-picked to sit
against near-black without glaring.

### The hand flicker (first attempt - see the newest entry, this did not fix it)

The row was a `UIFactory.Group`, which has no `Image`, and a RectTransform with
no `Graphic` receives no raycasts at all - so `PointerEnter`/`PointerExit` only
ever fired for its child cards, which the rebuild then destroyed. Giving the row
its own `Image` was **necessary but not sufficient**; the tray still flickered.
What actually fixed it is in the entry above this one.

The hand also stopped sitting in the layout. It is anchored to the bottom of
`Game Root` with `ignoreLayout` and grows upward. As a laid-out row, expanding
it resized the dock, which reflowed the board, which rebuilt every card on it -
so opening your hand made the whole screen jump. `SetHandExpanded` now touches
only the hand; `RefreshBattlefield` is no longer called from it.

`CardWidthForBoard` reserves `HandPeekHeight` at the bottom so the resting
hand never covers the bottom compound row. The *expanded* hand is not
reserved - it overlays, and drops away the moment you look elsewhere. Hand
height is capped by `MaxHandHeight` so it can never swallow the board or reach
the popup.

Dead after this: `MinBattlefieldHeight`, `HandCardHeight`, `CardStripHeight`,
`BattlefieldRowHeight`, `DraftZoneLeftInset`, `ColorSwatch`, `_handRowPin` -
all removed.

### Popups no longer block

The scrim is gone outright. A card's question is very often answered by
looking at your own hand or somebody's compound first, and grey-ing all of it
out made that impossible. The popup floats slightly above centre
(`PopupLift`), leaves the bottom of the screen clear for the hand and your own
compound, and only blocks clicks inside its own rect. It earns attention with
an accent bar across its top edge instead of by dimming everything else. The
hand is built last so it draws *over* the popup - you can always pull your own
cards up to read them mid-question.

`CardPreview`'s backdrop went from 0.84 to 0.62 alpha for the same reason. It
still swallows clicks so clicking outside closes it.

### Also

Fixed the `GetComponent<T>() ?? AddComponent<T>()` bug in three more places
while touching those lines - `BoardCardView.SetAffordable`/`SetDueToActivate`
(now share an `Edge()` helper) and `MultiplayerSceneSetup.EnsureNetworkManager`,
where an existing NetworkManager with no transport would have reached
`SetConnectionData` on nothing. There are now **no** instances of that pattern
left in the codebase.

### Verification

CompileCheck clean. RulesCheck all green, `--fuzz 800` clean. PlayModeTests
31/31. SmokeTest updated and passing, with four new structural checks that
pin the things this session fixed: no scrim is built, the hand ignores layout,
the hand has a raycast-target graphic of its own, and the popup clears the
bottom of the screen.

### Still for a later pass

The popup is still a fixed 460x400 regardless of content. The hand's collapsed
peek is a uniform scale-down rather than a true "just the tops poke out" clip.

## 2026-08-09 — Claude (the compounds take the screen: HUD, popup, and drag)

A stale Unity Licensing Client left over from a previous session was blocking
the Editor from opening at all (`ResponseCode: 505, Unsupported protocol
version`). Killed it and let Hub respawn a fresh one - unrelated to any code,
worth noting only because it looked like a code problem at first.

The rest of this session was a layout overhaul. The board's shape changed:

### Stat bars

`StatBar` is one horizontal line now (`BarHeight` 112 -> 34): name, die, a
health bar (red) with Block appended directly to its right end **to the same
per-point scale** rather than a separate boxed counter, and a followers bar
(now blue, was gold). Resource pips are gone from the stat bar entirely - see
the HUD below. Opponents' resource counts are consequently no longer visible
anywhere; only your own show, on the HUD. Worth a second look if that turns
out to matter for reading the table.

### Resource HUD (`ResourceHud.cs`, new)

Four circles fixed on the left edge of the screen, always showing the
viewer's own counts. During the Resource phase, before you've collected, they
pulse and become clickable - clicking one picks it, same accumulate-then-submit
behavior the old side panel had. This replaces the color-button picker that
used to live in the action panel.

### The blue side panel is gone

`_actionPanel`/`_actionViewport`/`_actionScroll` still exist, but they are no
longer a permanent column next to the battlefield - they are the content
inside a new floating `Popup Panel` (scrim + centered box, built as the last
children of `Game Root` so they draw above everything, `ignoreLayout` so
root's own VerticalLayoutGroup leaves them alone). It only shows when there is
something that actually needs an answer: a pending choice, the roll button,
the high-roll bonus, or the game-over screen. Draft/Activation/Buy show
nothing here at all now - the board, the hand, and a card's own preview are
where those phases happen. `RenderDraftHint`/`RenderActivation`/
`RenderResource`/`RenderCardActions` are gone; `RefreshActionPanel` now calls
`DecidePopup`, which returns whether there was anything to show.

A pending choice's popup now shows the card behind the question when one is
known (`GameState.ResolvingCardId` -> `GameView.resolvingCardId`, threaded
through `GameViewBuilder`), not just its description text.

Suspicious Chef's meal payment, Baal's Scheme-counter reroll, and Try Again's
reroll moved out of the side panel and onto their own cards: click the card,
its preview grows a small menu beneath the effect text
(`BoardCardView.SetExtraContent` / `CardPreview`'s new `_extraContent` region,
wired per-card in `BoardUI.WireCompoundCardExtras`). `CardPreview.RefreshIfShowing`
re-renders the open preview in place after a pick, the same way the old panel
re-rendered itself.

Battlefield lost its background panel and border - just the felt underneath
now. It takes the whole middle row except a slim, fixed-width resource HUD
column on the left.

### Compounds: units before blessings, drag to reorder

`OrderedForBoard` no longer sorts by activation number (that regressed back
in at some point after the 08-08 entry below - Codex, most likely, in the
course of other work; the UI wiring for player-ordered activation had been
dropped even though `GameState`'s backing logic was untouched). It now does a
*stable* partition: units first, then everything else, each group keeping
whatever relative order it already had. This is deliberately independent of
whatever the true stored order is - see next.

`GameState.MoveInCompound` (adjacent-swap) is gone, replaced by
`GameState.ReorderUnit(playerId, cardInstanceId, newIndex)`, which removes the
unit from the compound's unit-subsequence, reinserts it at `newIndex`, then
rebuilds the compound as `[units..., everything else...]` - so the compound
is now *structurally* units-first after any reorder, not just displayed that
way. Blessings never need to move for this to work; their relative order
among themselves is preserved untouched.

Reordering is real click-and-drag now, not Earlier/Later buttons (removed
from `BoardCardView`/`CardPreview` along with `MoveInCompound`'s RPC). New
`DragHandle.cs`: a light ghost card follows the pointer on a top-level "drag
layer" (the same layer pips fly across) while the real card stays put and
dims; on drop, the row's `BuildCardRow` computes the nearest unit cell by
screen distance and fires `RequestReorderUnitRpc`. Only wired for the
viewer's own row (`PlannedRow.IsOwnCompound`) and only for Unit-type cards.

**Watch for this one**: `DragHandle` originally did
`GetComponent<CanvasGroup>() ?? AddComponent<CanvasGroup>()` - the exact
`?? AddComponent` bug this file already warns about, since Unity's fake-null
defeats `??`. Caught by `DraggingAHandCardOntoTheBoardBuysIt` throwing
`MissingComponentException`. Fixed with an explicit `== null` check. Flagged
(via spawn_task, not fixed) two pre-existing occurrences of the same pattern
in `BoardCardView.SetAffordable`/`SetDueToActivate` and
`BoardUI.AddFixedWidthHeight`/`AddResponsiveWidth` - they happen to work today
because something upstream already added the component first, but they are
the same landmine waiting for a call order that doesn't hold.

### Hand: hover, not a toggle button

The "Hand (N) [show/hide]" button is gone. The hand row is always active now;
it peeks a small sliver (`HandPeekHeight` = 30, cards rendered small rather
than clipped) and expands to full size on `PointerEnter`, collapses on
`PointerExit` (`EventTrigger` on `_handRow` -> `BoardUI.SetHandExpanded`). It
also collapses on every phase change, so a hand left open while reading a
card doesn't sit over the board for turns afterward with nobody hovering it.

Playing a card from hand is drag-onto-the-battlefield now, not a Play button -
only affordable cards get a `DragHandle` (that's the "illuminated" a player
should look for; `SetAffordable` still does the visual tint). Drop is
accepted if the pointer lands inside the battlefield viewport
(`RectTransformUtility.RectangleContainsScreenPoint`). Recycle is still a
button under the card - dragging only replaces Play.

### Verification

CompileCheck clean. RulesCheck: `CheckActivationOrder` rewritten for
`ReorderUnit` instead of `MoveInCompound`, all checks pass. RulesCheck
`--fuzz 1500`: 1500/1500 clean. PlayModeTests: 31 pass, including two new
ones - `DraggingAHandCardOntoTheBoardBuysIt` and
`DraggingAUnitInYourCompoundReordersIt` - that simulate real drags via
`DragHandle.OnBeginDrag/OnDrag/OnEndDrag` with a constructed
`PointerEventData`, and rewrote the ones that depended on the old panel
(`FindVisibleResourceRow`/`FindVisibleDieFace` now read the HUD and the
flattened stat-bar hierarchy; the resource-picking tests find the HUD circle
by GameObject name, `"Red Slot"`, since it's labeled with a running count now
instead of a letter; `ExpandHand` calls `SetHandExpanded` via reflection
instead of clicking a button that no longer exists). SmokeTest:
`AlphaSmokeTest.cs` updated for the new paths (`Popup Panel/Action Viewport`
instead of `Middle Area/Action Panel/Action Viewport`; the hand-collapsed
check now reads `_handExpanded` via reflection since the row is never
inactive any more; `CheckBoardLayout` now measures the resource HUD column
against the battlefield instead of the old side panel) - passes.

### Left for the styling pass

Explicitly out of scope this round per direction: colors, spacing, and polish
throughout. The popup's size is a fixed 480x460 regardless of content - fine
for a YesNo prompt, probably too big; fine for the card-choice/standings
lists, sometimes tight enough to need its scrollbar. The hand's collapsed
peek is a small uniform scale-down rather than a true clipped "just the tops
poke out" effect.

## 2026-08-08 — Claude (timers off by default, and player-ordered activation)

### Timers

Off unless the host turns them on, from a toggle in the lobby. A clock that
takes your draft pick or answers a card's question for you is worse than a game
that waits, and there was no warning that it was about to. With them on, the
countdown says what will actually happen ("12s until a pick is made for you")
rather than a bare number.

Activation still closes itself either way - nothing in that phase is a player's
move, so there is nobody to wait for. It keeps its own dwell clock
(`_activationEnteredAt`), separate from the phase clock which is frozen when
timers are off.

### Activation order

Now round the table from whoever drafts first, and within each player in the
order they have arranged their own compound. A player whose units are spent is
skipped rather than the round stalling on them.

`GameState.MoveInCompound` moves one of your own cards earlier or later; the
compound's order *is* the activation order. `BoardUI.OrderedForBoard` therefore
no longer sorts compounds by activation number - doing so would have silently
overruled the arrangement the player chose. Re-ordering is offered from a card's
own preview, so a card can be read and repositioned in the same place.

Still worth stating: a Block from one player can land after another player's
Damage in the same round. The old category grouping guaranteed it never could.

### Hand sizing

Sized against both directions now. Width alone was not enough - during Buy the
cards carry a row of buttons underneath, and on a short window the tray ran past
the bottom of the screen, which is what kept cutting the hand off. It is now
sized from whatever height is left after the board's own minimum.

### Verification

- `./Tools/RulesCheck/run.sh` — 8 new checks on activation order and re-ordering.
- `./Tools/PlayModeTests/run.sh` — 30 pass, one new: nothing is taken for a
  player while the clocks are off.
- CompileCheck, fuzz 1200, and the smoke test all pass.


## 2026-08-08 — Claude (resigning and draws)

### Direction

Both should be available but tucked away, in the metadata menu or similar.
Draws are mutual; resignation is not.

### Edits

- `Assets/Scripts/Core/GameState.cs` — `Resign(playerId)` and
  `SetDrawOffer(playerId, offering)`.
  - Resigning is one player's call and consults nobody. It takes the player out
    the same way being reduced to nothing does, but through `LoseHealth` rather
    than damage, so nothing that pays out on wounds pays out because somebody
    conceded. It is recorded separately from being knocked out, so the board can
    say which happened.
  - A draw ends the game only once **every living player** is offering one.
    Offers can be withdrawn, and are cleared at the end of each turn - an offer
    is about the position as it stands, and carrying one into a turn that has
    changed the board would be agreeing to something else.
  - Resigning mid-question abandons that question. Nothing else at the table may
    happen while one is open, so a question left behind by a player leaving
    would have stopped the game for everybody.
- `Assets/Scripts/Net/NetworkGameManager.cs` — `RequestResignRpc` and
  `RequestOfferDrawRpc`. Resigning can leave a phase nobody is waiting on any
  more, so it re-checks whether the phase can now advance.
- `Assets/Scripts/Net/BoardUI.cs` — both controls sit in the status chip beside
  the turn counters. Resigning takes two presses ("Resign" then "Sure?") and the
  confirmation goes stale when the view changes, so a press armed on an earlier
  turn cannot end the game much later. The draw button doubles as the tally
  ("Draw 1/2"). Stat bars mark who is offering and who resigned.

### Verification

- `./Tools/RulesCheck/run.sh` — 17 new checks covering both, including that a
  majority is not enough for a draw, that an out player is not waited on, and
  that resigning mid-question does not strand the table.
- `./Tools/PlayModeTests/run.sh` — 29 pass, two new: resigning takes two
  presses, and one player offering a draw ends nothing.
- CompileCheck, fuzz 1200, and the smoke test all pass.


## 2026-08-08 — Claude (bars that actually fill, and a build for LAN testing)

### The bar bug

The health and follower bars never filled. `UIFactory.FillBar` set
`type = Filled` and `fillAmount`, but gave the Image no sprite - and Unity's
`Image.OnPopulateMesh` short-circuits to a plain quad when `sprite == null`,
discarding the fill entirely. They rendered full at every value from the day
they were written, and the animation driving them was moving a number nothing
read.

Fixed by generating a solid rectangle sprite in `BoardArt.Solid` and assigning
it. `HealthAndFollowerBarsReallyFill` now asserts the sprite is present, the
type is Filled, and the bar converges on the right fraction after damage - it
fails without the sprite.

**Anything that fills must have a sprite.** There is no warning for this; it
just silently looks like a full bar.

### Running it on another machine

- `Assets/Scripts/Editor/PlayerBuild.cs` (new) — builds a standalone player,
  driven from the command line or the Indoctrination menu.
- `Tools/Build/run.sh` (new) — `./Tools/Build/run.sh` for this Mac,
  `./Tools/Build/run.sh win` for Windows. Output lands in `Build/`, gitignored.
  Verified: produces a 117 MB universal macOS app (x86_64 + arm64).
- `Tools/Build/address.sh` (new) — prints this machine's LAN address, and checks
  the two things that usually stop a LAN game connecting: being on different
  networks, and the macOS firewall prompt.

The transport already binds `0.0.0.0` on the host, so no networking change was
needed - only a way to build and a way to find the address.

### Verification

- `./Tools/PlayModeTests/run.sh` — 27 pass.
- `./Tools/CompileCheck/run.sh`, `./Tools/RulesCheck/run.sh`,
  `./Tools/SmokeTest/run.sh` all pass.
- `./Tools/Build/run.sh` produces a running player.


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
