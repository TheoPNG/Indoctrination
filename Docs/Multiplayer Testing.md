# Testing multiplayer

## One-time setup

1. Open the project in Unity. It will download the Multiplayer Play Mode package
   (added to `Packages/manifest.json`), which lets one editor run several copies
   of the game at once.
2. Open `Assets/Scenes/SampleScene.unity`.
3. Menu bar: **Indoctrination > Set Up Multiplayer Scene**. This adds three
   objects to the scene and saves it:
   - `NetworkManager` - the Netcode connection layer
   - `Game Manager` - runs the rules, server-side only
   - `Game UI` - the temporary on-screen buttons

## Running two players

1. Menu bar: **Window > Multiplayer > Multiplayer Play Mode**.
2. Tick **Player 2** in that window and wait for the extra instance to launch.
   It appears as a second window that mirrors your project.
3. Press **Play** in the main editor.
4. In the main editor's Game view, click **Host**.
5. In the Player 2 window, click **Join**.
6. Both windows now show the lobby. In the host window, click **Start Game**.

The address defaults to `127.0.0.1` (this machine). To play across a local
network, the joining machine types the host's LAN IP instead.

## What to expect

- The draft goes around the table in snake order. Only the player whose pick it
  is gets buttons; everyone else is told to wait.
- **Your own hand is only ever sent to you.** Opponents see a card count. This
  is enforced on the server, not hidden in the UI, so a modified client cannot
  see it either.
- Illegal moves come back as a red message to the player who tried it, and
  change nothing.
- Card effects do not do anything yet. Playing a card puts it in your compound
  and the Activation phase lists which units would trigger, but the effect text
  is not executed - that system is still to be built.

## Checking your work without opening Unity

```bash
./Tools/CompileCheck/run.sh   # does everything still compile?
./Tools/RulesCheck/run.sh     # do the game rules still behave?
```
