# Working on Indoctrination

Notes for any editor (human or agent) touching this repo. `Docs/Editor Handoff
Log.md` is the *chronological* record of what changed and why; this file is the
*standing* rules that do not change between sessions. Read this first.

Everything below is here because it has already broken the game at least once.

---

## 1. Run the checks. This is the whole safety net.

There is no CI. These five scripts are it. Run them **before committing**, not
after — the two Unity ones take a couple of minutes each and catch things
nothing else can.

| Script | Proves | Speed |
| --- | --- | --- |
| `./Tools/CompileCheck/run.sh` | Every script compiles the way Unity compiles it | seconds |
| `./Tools/RulesCheck/run.sh` | The rules engine still obeys the rules | seconds |
| `./Tools/RulesCheck/run.sh --fuzz 800 --seed 1234` | No random game reaches an illegal state or hangs | ~20s |
| `./Tools/PlayModeTests/run.sh` | Real Netcode host + client, real RPCs, real board | ~2 min |
| `./Tools/SmokeTest/run.sh` | The whole UI actually builds and renders a full game | ~2 min |

Minimum before any commit: **CompileCheck + RulesCheck**. Touched anything in
`Assets/Scripts/Net/` or the UI: **also PlayModeTests + SmokeTest**. Touched
`GameState` or effects: **also a fuzz run**.

**The two Unity scripts need the Editor closed** — they refuse to start while
`Temp/UnityLockfile` exists. A clean quit sometimes leaves an empty lockfile
behind; if `ps aux | grep "Unity.app/Contents/MacOS/Unity"` shows nothing, it is
safe to `rm -f Temp/UnityLockfile`.

`./Tools/Build/run.sh` produces a standalone player for playtesting on another
machine. Not part of verification.

---

## 2. Architecture invariants

**Only the server holds `GameState`.** Clients receive a per-player filtered
`GameView` and ask for things via `[Rpc(SendTo.Server)]`. A client that could
mutate state could deal itself cards. Never move rules decisions client-side —
the client may *predict* (see `ShowResourceGain`), but the server's next view
overwrites it.

**`GameViewBuilder` is the only thing standing between a hidden hand and every
client at the table.** Treat changes there as security-sensitive. `RulesCheck`
asserts no view carries another player's hand; keep that passing.

**Effects queue, they do not nest.** Use `GameState.EnqueueEffect`; never call
an effect routine inline from inside another. `ResolveEffects()` drains the
queue with a step budget so two cards that retaliate against each other cannot
hang the server.

**Keep these files Unity-free**, or `RulesCheck` stops compiling and you lose
the fast rules feedback loop entirely:

- everything in `Assets/Scripts/Core/` **except `CardDatabase.cs`** (which is
  excluded from RulesCheck precisely because it uses `Resources`)
- `Assets/Scripts/Net/GameView.cs` and `GameViewBuilder.cs`

**Assembly layout** (`.asmdef` boundaries are load-bearing — PlayMode tests
cannot reference game code without them): `Core` (no references) → `Net` →
`Editor` / `PlayModeTests`.

---

## 3. Unity traps that have actually bitten this project

**`GetComponent<T>() ?? AddComponent<T>()` is broken.** Unity's "fake null" for
a missing component is not a C# null, so `??` never fires and the component is
never added. Always:

```csharp
var thing = go.GetComponent<T>();
if (thing == null) { thing = go.AddComponent<T>(); }
```

There are currently **zero** instances of the bad pattern in the codebase. Keep
it that way.

**`JsonUtility` cannot represent null for a nested serializable field.** It
revives the field as a blank instance on the far side. This is why `GameView`
has `hasPendingChoice` — always test the bool, never null-check `pendingChoice`.
Any new nested view object needs the same treatment.

**`Image` with `type = Filled` silently ignores `fillAmount` when `sprite` is
null**, rendering a full quad at every value. Any bar that fills needs
`BoardArt.Solid`. Use `UIFactory.FillBar`, which does this for you.

**A `RectTransform` with no `Graphic` receives no raycasts at all.** Pointer
events only fire for descendants that have one. If a container needs to answer
the pointer, give it an `Image` (transparent is fine).

**Never drive UI state from `PointerEnter`/`PointerExit` when reacting rebuilds
the thing under the pointer.** The rebuild fires another pointer event, which
re-triggers the reaction, every frame. The hand tray shipped broken twice this
way. `BoardUI.PollHandHover` reads the mouse position once a frame instead —
position is an input from outside the game, so a rebuild cannot perturb it. **Do
not put an `EventTrigger` back on the hand row.**

**Unity refuses `StartCoroutine` on an inactive GameObject.** Activate first
(see `CardPreview.FlashRitual`).

**`BoardEffects` is a `DontDestroyOnLoad` singleton and outlives any one
`BoardUI`.** `BoardUI.OnDestroy` must call `BoardEffects.Instance.CancelAll()`
or its coroutines reach for destroyed widgets.

**`Destroy` only takes effect at end of frame, and outside play mode there is no
frame.** Use `UIFactory.DestroyChildren`, which unparents first and picks
`Destroy`/`DestroyImmediate` correctly.

---

## 4. Deliberate UI shapes — do not "tidy" these

These look odd and are not. Changing them re-opens a fixed bug.

- **The hand tray is `ignoreLayout` and floats**, anchored to the bottom of
  `Game Root`. In the layout, expanding it reflowed the board and rebuilt every
  card, so opening your hand made the screen jump. It is also built **last** so
  it draws over the popup.
- **The hand rebuilds only when `HandSignature` changes.** The board refreshes on
  every server message; rebuilding regardless restarted every card's fade-in and
  read as flicker.
- **Popups have no scrim and must not block the board.** Questions are usually
  answered by looking at your own hand or somebody's compound first.
- **`OrderedForBoard` does a stable units-then-blessings partition and must not
  sort.** A compound's stored order *is* its activation order, chosen by the
  player by dragging. Sorting silently overrules them. (This regressed once
  already.)
- **The battlefield does not scroll.** Rows are sized to fit before anything is
  built; a board you have to scroll means the sizing is wrong, and a scrollbar
  hides that as somebody's mid-game problem instead of a bug.
- **Activation self-closes on its own clock** (`_activationEnteredAt`), separate
  from the phase clock, because it must close even when timers are off.
- **Timers are off by default.** Nothing may be taken or answered for a player
  without the board counting down in the open first.

---

## 5. Writing tests that are worth having

**Never assert against a `GameView` you built in-process.** Go through the real
network layer so it round-trips through `JsonUtility`. This exact shortcut
produced "passing tests, broken game" at least three times.

**Never find a UI control by name alone.** Check it is not clipped by an
ancestor `RectMask2D` — see `FindButtonLabelled` / `IsFullyVisibleThroughEveryMask`.
A test can otherwise "click" a button no player could reach.

**For anything that could flicker, assert stability over many frames, not once.**
A tray toggling every frame is "open" on half of them. See
`TheHandOpensOnHoverAndStaysOpen`.

**Synthetic input devices do not work under batchmode.** Both
`InputSystem.QueueStateEvent` and `InputState.Change` report no position. Drive
the method under test directly instead.

---

## 6. Conventions

- **Comments say *why*, not *what*.** Most comments here record a decision or a
  trap. Match that; do not add narration.
- **Add a dated entry to `Docs/Editor Handoff Log.md`** at the end of a session:
  what changed, what it broke, what you left undone. Newest first. It is the
  handoff between editors — including future you.
- **Say when you break a guarantee.** Round-the-table activation gave up the old
  "Block always resolves before Damage" ordering. That is written down rather
  than discovered later in a playtest.
- If a check fails and the fix is not obvious, **the failing check is usually
  right.** All five exist because something shipped broken.
