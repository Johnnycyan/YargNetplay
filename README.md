# YARG Netplay (prototype)

A BepInEx 5 plugin that adds online 2-player sessions to YARG without modifying the game.
Everything runs inside the game and uses YARG's own menus: an **Online** entry in the main menu, YARG's dialogs and toasts, the music library, the setlist and difficulty select. **F8** opens a small debug overlay.

## How it works

- **Online page:** Main menu > Online opens a full-screen YARG page, built at runtime from YARG's own Profiles page (header, list, sidebar, buttons) and registered with YARG's menu manager, so Back and Escape work like any other page.
  - When not connected, it lists **saved servers** (Join or delete per row; rename with the ✎ icon), plus Host Game, Join Address and Add Server. Servers you join are saved automatically and named after the host's profile. They're stored in `BepInEx\config\yargnetplay-servers.json`.
  - When hosting or connected, it lists the players with ping and Ready state. The joiner has a Ready button. The host's **Go to Song Select** unlocks once the joiner is ready and sends both players to Quickplay.
  - The remote-player delay is set on this page.
  - So is the **received songs folder**, shown with its song count and size. **Change Folder** opens YARG's own folder picker. You can also type a path into the field, or clear it to go back to the default. Songs already received move to the new folder, across drives too. The old folder is removed if it ends up empty. **Open Folder** opens the folder in your file manager. The folder can't be changed during a song or while a song is being transferred.
- **Connection:** one host and one joiner, direct TCP (default port `47820`; the port is set in `BepInEx\config\com.johnnycyan.yargnetplay.cfg`). The host's clock is the reference, synced with ping/pong.
- **Shared setlist:** both players browse YARG's own music library. A song either player adds to the native setlist (hold Play Song) is added to the other player's setlist too. Removing a song or clearing the setlist syncs the same way. Songs are matched by the chart hash YARG already computes (`SongEntry.Hash`).
- **Picking difficulty:** when either player opens difficulty select, for one song or by starting the setlist, the other player's game opens it too, for the same songs. Each player picks their own instrument, difficulty and modifiers.
- **Starting:** every load of the gameplay scene goes through `GlobalVariables.LoadScene`.
  - The mod holds it with a native "Waiting for … to be ready" dialog until both players are ready for the same song, and the players' setups (serialized `YargProfile` plus engine preset) are exchanged as they confirm.
  - This covers the first song, the setlist's Continue on the score screen, and score-screen restarts.
  - A second start gate holds `GameManager.Update` until both games are loaded, then opens at the same host-clock time on both machines.
  - Song speed is fixed at 100%. Restart and skip in the pause menu are disabled online.
- **Remote player:** added to `GameManager.YargPlayers` as a replay-style player (no bindings). Its engine is built through the game's own `CreateEngine` using the peer's engine preset. Each frame the mod feeds it the peer's inputs and updates it at `songTime - delay`. Its note pools are shifted by `delay * noteSpeed`, so notes reach the strike line when the delayed engine judges them. The scoring engine is deterministic, so this reproduces the peer's real result.
- **Local player:** every input the local engine accepts (`BasePlayer.OnGameInput`) is sent to the peer.
- **Scores:** the peer's score is never written to your score database. Saved replays include both players.
- **Song transfer:** when the peer queues a song you don't have, the mod asks for it automatically.
  - Loose song folders are packed into a `.sng` on the sender's side. Background videos are left out, since they can be hundreds of MB. Songs that are already `.sng` (or `.yargsong`) are sent as they are. Rock Band CON songs can't be sent yet.
  - Received songs are stored in their own folder, which is not a song folder. By default it's `%USERPROFILE%\AppData\LocalLow\YARC\YARG\NetplaySongs`, and it can be changed on the Online page (saved as `ReceivedSongsFolder` in the plugin config). If the chosen folder is unavailable at startup, for example on an unplugged drive, the default is used for that session. To keep a song, move its `.sng` into one of your song folders. To discard songs, delete them from that folder.
  - Received songs are loaded straight into memory with YARG's own scanner code, so no library rescan is needed, and they are remembered between sessions.
  - While you're connected, received songs appear in the music library like any other song (metadata, previews, sorting), under the folder "Netplay".
  - They're added to YARG's in-memory song list right before it sorts it, so they survive rescans and are never written to `songcache.bin`. They leave the library when you disconnect.
  - Transfers pause while a song is being played, so they don't delay gameplay inputs.

Every private member the mod touches is listed in `src/Game/Accessors.cs` and the patch targets in `src/Game/GameHooks.cs`. Check those first when a YARG update breaks the mod.

## Build

Requires the .NET SDK. By default the build references the YARG nightly install at
`%LOCALAPPDATA%\YARC\YARG Installs\cd3bd653-...\installation`. To use another install:

```bash
dotnet build -c Release -p:YargDir="C:\path\to\YARG"
```

If BepInEx is installed in that YARG folder, the build copies the plugin to `BepInEx\plugins\YargNetplay\` automatically.

## Install BepInEx (once per YARG install)

1. Download BepInEx 5.4.x **win_x64** (Mono, not IL2CPP) from the BepInEx GitHub releases.
2. Extract it into the folder that contains `YARG.exe`.
3. Run YARG once so BepInEx creates its folders, then build this project (or copy `YargNetplay.dll` into `BepInEx\plugins\`).
4. Optional: set `[Logging.Console] Enabled = true` in `BepInEx\config\BepInEx.cfg` to see logs.

The YARC Launcher may remove extra files when it updates or verifies YARG. If so, reinstall BepInEx afterwards.

## Test locally (two instances on one PC)

1. Start two copies of YARG. Each needs one active profile with an assigned input device.
2. In instance A choose **Online > Host Game**. In instance B choose **Online > Join Address** and enter `127.0.0.1`.
3. In instance B press **Ready**, then in instance A press **Go to Song Select**.
4. Add songs to the setlist, or pick one, and choose a difficulty. Both players confirm Ready.

Both players must run the same YARG build and mod version. The window warns when the YARG versions differ.

Two instances on one PC share one library, so neither is ever missing a song. To test song transfer locally, start one instance with the environment variable `YARGNETPLAY_IGNORE_LIBRARY=1`. That instance then only "has" songs it has received.

## Not done yet

- **Vocals:** pitch input is a per-frame stream and needs its own handling.
- **More than one local player** per machine.
- **Difficulty check:** the selected difficulty is not yet validated against the chart.
- **Pause/resume** is forwarded both ways, but each side's resume rewind can leave the clocks slightly apart. A re-sync after resume is needed.
- **Band mechanics** (band multiplier, unison star power, fail meter) see the remote player `delay` late.
- **Tested so far:** two instances on one PC (b3912 nightly), with keyboard input on one side. Connect, queue sync, synced start, pause/resume sync, the delayed remote highway, input relay, the native menus (Online button, dialogs, setlist sync, following into difficulty select, the Ready gate), and song transfer of a loose folder (received, played, and remembered after a restart) all work. Not yet tested across two PCs over the internet.
- **Networking:** NAT/port forwarding is required for the host. Tailscale or ZeroTier avoids it.
