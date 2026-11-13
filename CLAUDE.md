# YARG Netplay

BepInEx 5 + HarmonyX plugin (net472) that adds online 2-player sessions to YARG without modifying the game. README.md describes the features and how each one works; read it before changing behavior.

## Build

```bash
dotnet build                                          # Debug, against the default YARG install
dotnet build -c Release                               # also writes dist\ (DLL + installer zip)
dotnet build -c Release -p:YargDir="C:\path\to\YARG"  # against another YARG install
```

- The project compiles against the game's own assemblies in `$(YargDir)\YARG_Data\Managed` (including its Mono `mscorlib`). They aren't committed. The default `YargDir` is a nightly install under `%LOCALAPPDATA%\YARC\YARG Installs\`.
- If `$(YargDir)\BepInEx\plugins` exists, every build copies the DLL there (`DeployToYarg`). Building against an install overwrites the plugin in it.
- There are no unit tests. A build passing against each supported YARG is the minimum check; behavior is checked in-game (see Testing).
- `<Version>` in the csproj is only the local default. Releases take the version from the tag.

## Supporting several YARG versions

One source tree builds against YARG stable and nightly; CI builds one DLL per channel (`YargNetplay-Stable.dll`, `YargNetplay-Nightly.dll`).

- Where the two APIs differ, hide the difference behind one small wrapper and keep callers version-free. `src/Game/Show.cs` is the example: the csproj defines `YARG_STATIC_SETLIST` when the game assembly has the static `YARG.Playlists.Setlist` (nightly b4161 on), detected by searching `Assembly-CSharp.dll` for `ResolveSongs`.
- Prefer a compile constant detected from the game assembly when a type or member only exists in one version. Use reflection when only a signature changed (e.g. `GameBridge.NewReplayData` picks the `ReplayData` constructor at runtime).
- Every private member the mod touches is in `src/Game/Accessors.cs`, and patch targets are in `src/Game/GameHooks.cs` and the `[HarmonyPatch]` classes beside the code they serve. Targets named by string (`"OnEnable"`, `"_openMenus"`, …) aren't checked by the compiler: when porting to a new YARG, grep for them and confirm each still exists.
- To read YARG's code, decompile the managed DLLs with ilspycmd (`%USERPROFILE%\.dotnet\tools\ilspycmd.cmd`; it isn't on PATH and fails on paths with spaces, so copy the DLL somewhere first). YARG's source is also on GitHub (YARC-Official/YARG and YARG-BleedingEdge).

## Layout

- `src/Plugin.cs`, `src/NetplayManager.cs`: entry point, config, Harmony setup, per-frame driver.
- `src/Net/`: TCP connection, wire protocol (`Protocol.cs`), session state and messages (`Session.cs`), song transfer, saved servers.
- `src/Game/`: everything that touches YARG: Harmony hooks, the remote player (`RemoteDriver.cs`), setlist sync with YARG's own setlist (`NativeSetlist.cs`), following the peer through menus (`NativeMenus.cs`), part filtering, received songs (`.sng` packaging and in-memory library injection).
- `src/UI/`: UI built from YARG's own prefabs at runtime (Online page, dialogs, toasts, setlist overlay) plus the F8 IMGUI debug window.
- `installer/`: Windows (`install.ps1` + `.bat`) and Linux/macOS (`install.sh`, kept compatible with macOS's bash 3.2) installers. They download the release DLL for the YARG channel of each install.
- `.github/workflows/build.yml`: CI builds, tag releases, and the daily compatibility check.

## Conventions

- Reuse YARG's own menus, dialogs, toasts and input handling rather than building custom UI. The mod should feel like part of the game.
- Changes made because the peer did something must not be echoed back. Code uses guard flags for this (`NativeSetlist._applying`, `NativeMenus.Following`); keep them set across any YARG call that could fire a patched method.
- Comments explain why, in plain sentences. Keep existing comments when editing.
- Keep README.md in sync with user-visible behavior, including the "Tested so far" note.

## Testing

Run two YARG instances on one PC (README "Test locally"): one hosts, the other joins `127.0.0.1`. Both need the same YARG build and mod version. `YARGNETPLAY_IGNORE_LIBRARY=1` on one instance makes it receive songs, to test song transfer. Logs are in `BepInEx\LogOutput.log` of each install.

In the music library: with an empty setlist, hold Green (`1`) to add a song. With songs in it, a single press of Green adds a song and holding it starts the set. Clicking an already highlighted row plays that song, which sends both instances to difficulty select.

## Releases

Pushing a `v*` tag builds both channels and publishes the GitHub release that the installers and the in-game update check use. Never tag a release or merge a PR without the user's OK. `YARG_STABLE_PIN` / `YARG_NIGHTLY_PIN` repository variables pin a channel's CI build to a YARG tag while the newest one needs porting; delete the pin once the port lands.
