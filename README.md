# YARG Netplay (prototype)

A BepInEx 5 plugin that adds online 2-player sessions to YARG without modifying the game.
Everything runs inside the game, including the UI (press **F8**).

## How it works

- **Connection:** one host and one joiner, direct TCP (default port `47820`). The host's clock is the reference, synced with ping/pong.
- **Queue:** each player adds songs from their own library. Songs are matched by the chart hash YARG already computes (`SongEntry.Hash`). The peer reports whether it has each song.
- **Starting:** the host presses Play. Both sides exchange their player setup (serialized `YargProfile` plus engine preset), then load gameplay through the normal path. A start gate holds `GameManager.Update` until both are loaded, then opens at the same host-clock time on both machines.
- **Remote player:** added to `GameManager.YargPlayers` as a replay-style player (no bindings). Its engine is built through the game's own `CreateEngine` using the peer's engine preset. Each frame the mod feeds it the peer's inputs and updates it at `songTime - delay`. Its note pools are shifted by `delay * noteSpeed`, so notes reach the strike line when the delayed engine judges them. The scoring engine is deterministic, so this reproduces the peer's real result.
- **Local player:** every input the local engine accepts (`BasePlayer.OnGameInput`) is sent to the peer.
- **Scores:** the peer's score is never written to your score database. Saved replays include both players.
- **Song transfer:** when the peer queues a song you don't have, the mod asks for it automatically.
  - Loose song folders are packed into a `.sng` on the sender's side. Background videos are left out, since they can be hundreds of MB. Songs that are already `.sng` (or `.yargsong`) are sent as they are. Rock Band CON songs can't be sent yet.
  - Received songs are stored in `%USERPROFILE%\AppData\LocalLow\YARC\YARG\NetplaySongs`. That is not a song folder, so they never enter your library. To keep a song, move its `.sng` into one of your song folders. To discard songs, delete them from that folder. The window's **Open folder** button opens it.
  - Received songs are loaded straight into memory with YARG's own scanner code, so no library rescan is needed, and they are remembered between sessions.
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
2. In instance A, press F8 and choose **Host**. In instance B, press F8, enter `127.0.0.1`, and choose **Join**.
3. Pick an instrument and difficulty in the window, add a song both have, and press **Play** on the host.

Both players must run the same YARG build and mod version. The window warns when the YARG versions differ.

Two instances on one PC share one library, so neither is ever missing a song. To test song transfer locally, start one instance with the environment variable `YARGNETPLAY_IGNORE_LIBRARY=1`. That instance then only "has" songs it has received.

## Not done yet

- **Vocals:** pitch input is a per-frame stream and needs its own handling.
- **More than one local player** per machine.
- **Difficulty check:** the selected difficulty is not yet validated against the chart.
- **Pause/resume** is forwarded both ways, but each side's resume rewind can leave the clocks slightly apart. A re-sync after resume is needed.
- **Band mechanics** (band multiplier, unison star power, fail meter) see the remote player `delay` late.
- **Tested so far:** two instances on one PC (b3912 nightly), with keyboard input on one side. Connect, queue sync, synced start, pause/resume sync, the delayed remote highway, input relay, and song transfer of a loose folder (received, played, and remembered after a restart) all work. Not yet tested across two PCs over the internet.
- **Networking:** NAT/port forwarding is required for the host. Tailscale or ZeroTier avoids it.
