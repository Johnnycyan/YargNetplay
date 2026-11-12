#!/usr/bin/env bash
# YARG Netplay installer for Linux: installs BepInEx 5 (if needed) and the newest YargNetplay plugin from GitHub into YARG.
# Run: bash install.sh [<YARG folder>]
#  or: curl -fsSL https://github.com/Johnnycyan/YargNetplay/releases/latest/download/install.sh | bash
# Run it again any time to update the mod. YARGNETPLAY_DLL=/path/to/YargNetplay.dll installs that file instead of
# downloading the newest release (offline installs, testing a build).
#
# Native Linux YARG: BepInEx is started by run_bepinex.sh, so this also adds a "YARG (Netplay)" app shortcut.
# YARG.exe under Wine/Proton: installs the Windows BepInEx; Wine must load its winhttp.dll (see the message at the end).
set -euo pipefail

BEPINEX_VERSION="5.4.23.6"
BASE_URL="https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VERSION"
# Pinned and checksum-verified; this is the version the mod is tested with.
LINUX_ZIP="BepInEx_linux_x64_$BEPINEX_VERSION.zip"
LINUX_SHA256="f8ac87a97fb1ef85451c04ace1fddb7dd7eb0b079c179b989d6bc462c98bd365"
WIN_ZIP="BepInEx_win_x64_$BEPINEX_VERSION.zip"
WIN_SHA256="6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755"
# One build per YARG channel: YargNetplay-Stable.dll and YargNetplay-Nightly.dll.
PLUGIN_URL="https://github.com/Johnnycyan/YargNetplay/releases/latest/download/YargNetplay-%s.dll"

step() { printf '\n\033[36m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31m%s\033[0m\n' "$*" >&2; exit 1; }

# Prompts read the terminal, so they still work when the script itself is piped in (curl ... | bash).
ask() { read -r -p "$1" "$2" < /dev/tty; }

for tool in unzip sha256sum; do
    command -v "$tool" >/dev/null || fail "This installer needs '$tool'. Install it with your package manager and run it again."
done
if command -v curl >/dev/null; then download() { curl -fsSL "$1" -o "$2"; }
elif command -v wget >/dev/null; then download() { wget -q "$1" -O "$2"; }
else fail "This installer needs curl or wget."; fi

# ------------------------------------------------------------ find YARG

# The native Unity player (YARG.x86_64), or YARG.exe for the Windows build under Wine/Proton.
game_kind() {
    if [ -f "$1/YARG.x86_64" ]; then echo native
    elif [ -f "$1/YARG.exe" ]; then echo windows
    else echo none; fi
}

# The YARC Launcher keeps each version in "<download location>/YARG Installs/<id>/installation", with the version
# name in "<id>/tag.txt". The download location is in the launcher's settings.json and can be changed there.
find_installs() {
    local data="${XDG_DATA_HOME:-$HOME/.local/share}" config="${XDG_CONFIG_HOME:-$HOME/.config}"
    local roots=("$data/YARC" "$config/YARC" "$HOME/YARC")
    local settings location
    for settings in "$data/YARC/Launcher/settings.json" "$config/YARC/Launcher/settings.json"; do
        if [ -f "$settings" ]; then
            location="$(sed -n 's/.*"download_location" *: *"\([^"]*\)".*/\1/p' "$settings" | head -n 1)"
            [ -n "$location" ] && roots=("$location" "${roots[@]}")
        fi
    done
    local root dir
    for root in "${roots[@]}"; do
        for dir in "$root/YARG Installs"/*/installation; do
            [ -d "$dir" ] && [ "$(game_kind "$dir")" != none ] && echo "$dir"
        done
    done | awk '!seen[$0]++'
}

# Stable or Nightly, from the launcher's tag.txt (stable tags are v1.2.3, nightlies b1234); asks when there's none.
channel_of() {
    local tag answer
    tag="$(cat "$(dirname "$1")/tag.txt" 2>/dev/null || true)"
    case "$tag" in v[0-9]*) echo Stable; return ;; b[0-9]*) echo Nightly; return ;; esac
    while true; do
        ask "Is $1 YARG Stable or Nightly? (S/N) " answer
        case "$answer" in [sS]*) echo Stable; return ;; [nN]*) echo Nightly; return ;; esac
    done
}

label() {
    local tag
    tag="$(cat "$(dirname "$1")/tag.txt" 2>/dev/null || basename "$(dirname "$1")")"
    case "$tag" in b[0-9]*) echo "Nightly $tag" ;; v[0-9]*) echo "Stable $tag" ;; *) echo "YARG $tag" ;; esac
}

targets=()
if [ $# -ge 1 ]; then
    targets=("$(cd "$1" && pwd)")
else
    mapfile -t found < <(find_installs)
    if [ ${#found[@]} -eq 1 ]; then
        targets=("${found[0]}")
    elif [ ${#found[@]} -gt 1 ]; then
        echo "Found these YARG versions:"
        for i in "${!found[@]}"; do echo "  $((i + 1)). $(label "${found[$i]}")"; done
        echo "  A. All of them"
        ask "Install into which one? (press Enter for all) " choice
        if [ -z "$choice" ] || [[ "$choice" =~ ^[aA] ]]; then targets=("${found[@]}")
        elif [[ "$choice" =~ ^[0-9]+$ ]] && [ "$choice" -ge 1 ] && [ "$choice" -le ${#found[@]} ]; then
            targets=("${found[$((choice - 1))]}")
        else fail "\"$choice\" isn't one of the choices. Nothing was installed."; fi
    else
        fail "No YARC Launcher installs of YARG were found. Run: bash install.sh \"/path/to/your/YARG folder\""
    fi
fi

for t in "${targets[@]}"; do
    [ "$(game_kind "$t")" != none ] || fail "$t doesn't contain YARG.x86_64 or YARG.exe."
done

if pgrep -f 'YARG\.(x86_64|exe)' >/dev/null 2>&1; then
    ask "YARG is running. Close it, then press Enter to continue. " _
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# ------------------------------------------------------------ the plugin (the newest GitHub release, per channel)

[ -z "${YARGNETPLAY_DLL:-}" ] || [ -f "$YARGNETPLAY_DLL" ] || fail "$YARGNETPLAY_DLL doesn't exist."

plugin_for() { # channel -> path of the plugin to install (downloaded once per channel)
    if [ -n "${YARGNETPLAY_DLL:-}" ]; then echo "$YARGNETPLAY_DLL"; return; fi
    local file="$tmp/YargNetplay-$1.dll" url
    if [ ! -f "$file" ]; then
        step "Downloading the newest YARG Netplay for YARG $1..." >&2
        # shellcheck disable=SC2059
        url="$(printf "$PLUGIN_URL" "$1")"
        download "$url" "$file" || fail "Couldn't download YARG Netplay from GitHub."
    fi
    echo "$file"
}

# Ask about channels up front (before anything is installed).
channels=()
for t in "${targets[@]}"; do channels+=("$(channel_of "$t")"); done

# ------------------------------------------------------------ BepInEx (downloaded only when needed, verified)

fetch() { # zip name, sha256 -> path of the verified zip
    local zip="$tmp/$1"
    if [ ! -f "$zip" ]; then
        step "Downloading BepInEx $BEPINEX_VERSION ($1)..." >&2
        download "$BASE_URL/$1" "$zip" || fail "Couldn't download BepInEx."
        echo "$2  $zip" | sha256sum -c --quiet - >/dev/null 2>&1 ||
            fail "The BepInEx download didn't match its published checksum, so it wasn't installed. Try again later."
    fi
    echo "$zip"
}

wine_note=0
native_note=0
for i in "${!targets[@]}"; do
    t="${targets[$i]}"
    plugin="$(plugin_for "${channels[$i]}")"
    step "Installing into $(label "$t"): $t"
    kind="$(game_kind "$t")"

    if [ "$kind" = native ]; then
        if [ -f "$t/run_bepinex.sh" ] && [ -f "$t/BepInEx/core/BepInEx.Preloader.dll" ]; then
            echo "  BepInEx is already installed."
        else
            unzip -oq "$(fetch "$LINUX_ZIP" "$LINUX_SHA256")" -d "$t"
            echo "  Installed BepInEx $BEPINEX_VERSION."
        fi
        # run_bepinex.sh starts the game with BepInEx loaded; point it at YARG.
        sed -i 's/^executable_name=.*/executable_name="YARG.x86_64"/' "$t/run_bepinex.sh"
        chmod +x "$t/run_bepinex.sh" "$t/YARG.x86_64"

        # A menu shortcut, since the YARC Launcher starts YARG without BepInEx.
        apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
        mkdir -p "$apps"
        id="$(basename "$(dirname "$t")")"
        cat > "$apps/yarg-netplay-$id.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=YARG (Netplay) $(label "$t")
Comment=YARG with the YARG Netplay mod
Exec="$t/run_bepinex.sh"
Path=$t
Icon=$t/YARG_Data/Resources/UnityPlayer.png
Terminal=false
Categories=Game;
EOF
        native_note=1
        echo "  Added the app shortcut \"YARG (Netplay) $(label "$t")\"."
    else
        if [ -f "$t/winhttp.dll" ] && [ -f "$t/BepInEx/core/BepInEx.Preloader.dll" ]; then
            echo "  BepInEx is already installed."
        else
            unzip -oq "$(fetch "$WIN_ZIP" "$WIN_SHA256")" -d "$t"
            echo "  Installed BepInEx $BEPINEX_VERSION (Windows build, for Wine/Proton)."
        fi
        wine_note=1
    fi

    mkdir -p "$t/BepInEx/plugins/YargNetplay"
    cp -f "$plugin" "$t/BepInEx/plugins/YargNetplay/YargNetplay.dll"
    echo "  Installed YARG Netplay."
done

printf '\n\033[32mDone.\033[0m\n'
if [ "$native_note" = 1 ]; then
    echo "Start YARG from the \"YARG (Netplay)\" shortcut (or run_bepinex.sh in the YARG folder), then choose Online."
    echo "The YARC Launcher's Play button starts YARG without the mod."
fi
if [ "$wine_note" = 1 ]; then
    echo "Wine/Proton: Wine must load BepInEx's winhttp.dll. Start YARG with WINEDLLOVERRIDES=\"winhttp=n,b\""
    echo "(Steam/Lutris launch options: WINEDLLOVERRIDES=\"winhttp=n,b\" %command%)."
fi
echo "If the YARC Launcher updates or verifies YARG, it may remove BepInEx. Run this installer again if Online disappears."
