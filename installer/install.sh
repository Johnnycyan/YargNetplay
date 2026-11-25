#!/usr/bin/env bash
# YARG Netplay installer for Linux and macOS: installs BepInEx 5 (if needed) and the newest YargNetplay plugin from GitHub
# into YARG.
# Run: bash install.sh [<YARG folder>]
#  or: curl -fsSL https://github.com/Johnnycyan/YargNetplay/releases/latest/download/install.sh | bash
# Run it again any time to update the mod. YARGNETPLAY_DLL=/path/to/YargNetplay.dll installs that file instead of
# downloading the newest release (offline installs, testing a build); a YargMidiFix.dll next to it is installed too.
# The YargMidiFix plugin released with the mod is installed alongside it.
#
# Native Linux YARG: BepInEx is started by run_bepinex.sh, so this also adds a "YARG (Netplay)" app shortcut.
# macOS (YARG.app): the same, with a small "YARG (Netplay)" app in ~/Applications that starts run_bepinex.sh.
# YARG.exe under Wine/Proton: installs the Windows BepInEx; Wine must load its winhttp.dll (see the message at the end).
#
# A YARC Launcher update replaces the YARG folder, which removes BepInEx and the mod. The installer can set up an
# auto-reinstall: a copy of this script in the YargNetplay data folder, run with --auto by a systemd user path unit
# (Linux) or a launchd agent (macOS) when the launcher changes an install it was set up for.
# bash install.sh --remove-auto-reinstall turns it off again.
#
# macOS ships bash 3.2, so this sticks to what it has (no mapfile, no sed -i, no sha256sum).
set -euo pipefail

BEPINEX_VERSION="5.4.23.6"
BASE_URL="https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VERSION"
# Pinned and checksum-verified; this is the version the mod is tested with.
LINUX_ZIP="BepInEx_linux_x64_$BEPINEX_VERSION.zip"
LINUX_SHA256="f8ac87a97fb1ef85451c04ace1fddb7dd7eb0b079c179b989d6bc462c98bd365"
MAC_ZIP="BepInEx_macos_universal_$BEPINEX_VERSION.zip"
MAC_SHA256="2e711e21f6570da964fce4654b1976500dbe043f61778bce0008500078564638"
WIN_ZIP="BepInEx_win_x64_$BEPINEX_VERSION.zip"
WIN_SHA256="6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755"
# One build per YARG channel: YargNetplay-Stable.dll and YargNetplay-Nightly.dll. The same DLL runs on every OS.
PLUGIN_URL="https://github.com/Johnnycyan/YargNetplay/releases/latest/download/YargNetplay-%s.dll"
# The MIDI fix plugin, installed alongside. One DLL for both channels.
MIDIFIX_URL="https://github.com/Johnnycyan/YargNetplay/releases/latest/download/YargMidiFix.dll"
# This script, for the auto-reinstall copy when it was piped in and has no file of its own.
SCRIPT_URL="https://github.com/Johnnycyan/YargNetplay/releases/latest/download/install.sh"

step() { printf '\n\033[36m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
warn() { printf '\033[33m%s\033[0m\n' "$*"; }

# Prompts read the terminal, so they still work when the script itself is piped in (curl ... | bash).
ask() { read -r -p "$1" "$2" < /dev/tty; }

case "$(uname -s)" in Darwin*) OS=mac ;; *) OS=linux ;; esac

# The installer's own folder: the download cache (so a reinstall still works offline) and the auto-reinstall's files.
if [ "$OS" = mac ]; then DATA_DIR="$HOME/Library/Application Support/YargNetplay"
else DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/YargNetplay"; fi
CACHE_DIR="$DATA_DIR/cache"
SCRIPT_COPY="$DATA_DIR/install.sh"
WATCH_LIST="$DATA_DIR/auto-reinstall.txt"   # one YARG folder per line
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
UNIT=yarg-netplay-reinstall
AGENT_ID=io.github.johnnycyan.yargnetplay.reinstall
AGENT="$HOME/Library/LaunchAgents/$AGENT_ID.plist"

MODE=install
case "${1:-}" in
    --auto) MODE=auto; shift ;;
    --remove-auto-reinstall) MODE=remove; shift ;;
esac

command -v unzip >/dev/null || fail "This installer needs 'unzip'. Install it with your package manager and run it again."
if command -v sha256sum >/dev/null; then sha256_check() { sha256sum -c -; }
elif command -v shasum >/dev/null; then sha256_check() { shasum -a 256 -c -; }
else fail "This installer needs 'sha256sum' or 'shasum'."; fi
if command -v curl >/dev/null; then download() { curl -fsSL "$1" -o "$2"; }
elif command -v wget >/dev/null; then download() { wget -q "$1" -O "$2"; }
else fail "This installer needs curl or wget."; fi

# ------------------------------------------------------------ find YARG

# The native Unity player (YARG.x86_64 on Linux, YARG.app on macOS), or YARG.exe for the Windows build under Wine/Proton.
game_kind() {
    if [ -f "$1/YARG.x86_64" ]; then echo native
    elif [ -d "$1/YARG.app" ]; then echo mac
    elif [ -f "$1/YARG.exe" ]; then echo windows
    else echo none; fi
}

# The YARC Launcher keeps each version in "<download location>/YARG Installs/<id>/installation", with the version
# name in "<id>/tag.txt". The download location is in the launcher's settings.json and can be changed there.
# The launcher's own folder is "YARC" in the OS's local data folder (~/.local/share on Linux,
# ~/Library/Application Support on macOS).
find_installs() {
    local roots settings_dirs
    if [ "$OS" = mac ]; then
        roots=("$HOME/Library/Application Support/YARC" "$HOME/YARC")
        settings_dirs=("$HOME/Library/Application Support/YARC")
    else
        local data="${XDG_DATA_HOME:-$HOME/.local/share}" config="${XDG_CONFIG_HOME:-$HOME/.config}"
        roots=("$data/YARC" "$config/YARC" "$HOME/YARC")
        settings_dirs=("$data/YARC" "$config/YARC")
    fi
    local dir settings location
    for dir in "${settings_dirs[@]}"; do
        settings="$dir/Launcher/settings.json"
        if [ -f "$settings" ]; then
            location="$(sed -n 's/.*"download_location" *: *"\([^"]*\)".*/\1/p' "$settings" | head -n 1)"
            [ -n "$location" ] && roots=("$location" "${roots[@]}")
        fi
    done
    local root
    for root in "${roots[@]}"; do
        for dir in "$root/YARG Installs"/*/installation; do
            [ -d "$dir" ] && [ "$(game_kind "$dir")" != none ] && echo "$dir"
        done
    done | awk '!seen[$0]++'
}

# Stable or Nightly from the launcher's tag.txt (stable tags are v1.2.3, nightlies b1234), or nothing when there's no
# known tag (a folder not from the launcher).
tag_channel() {
    case "$(cat "$(dirname "$1")/tag.txt" 2>/dev/null || true)" in
        v[0-9]*) echo Stable ;; b[0-9]*) echo Nightly ;;
    esac
}

# The channel, asking when there's no tag.txt to tell.
channel_of() {
    local channel answer
    channel="$(tag_channel "$1")"
    if [ -n "$channel" ]; then echo "$channel"; return; fi
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

# ------------------------------------------------------------ downloads (kept in the cache)

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

# Downloads into the cache. When GitHub can't be reached, an earlier download of the same file is used instead, so
# the auto-reinstall still works offline (it may be an older version; the in-game update check offers the newest).
# Prints the file's path, or nothing for an optional file that couldn't be had.
cached_download() { # url, name, optional (1 or empty)
    local file="$CACHE_DIR/$2"
    mkdir -p "$CACHE_DIR"
    if download "$1" "$file.part"; then
        mv -f "$file.part" "$file"
    else
        rm -f "$file.part"
        if [ -f "$file" ]; then warn "  Couldn't download it, using the copy downloaded earlier." >&2
        elif [ -n "${3:-}" ]; then warn "  Couldn't download it, skipping." >&2; return 0
        else fail "Couldn't download $2 from GitHub."; fi
    fi
    echo "$file"
}

[ -z "${YARGNETPLAY_DLL:-}" ] || [ -f "$YARGNETPLAY_DLL" ] || fail "$YARGNETPLAY_DLL doesn't exist."

plugin_for() { # channel -> path of the plugin to install (downloaded once per run and channel)
    if [ -n "${YARGNETPLAY_DLL:-}" ]; then echo "$YARGNETPLAY_DLL"; return; fi
    if [ ! -f "$tmp/fetched-$1" ]; then
        step "Downloading the newest YARG Netplay for YARG $1..." >&2
        # shellcheck disable=SC2059
        cached_download "$(printf "$PLUGIN_URL" "$1")" "YargNetplay-$1.dll" > "$tmp/fetched-$1"
    fi
    cat "$tmp/fetched-$1"
}

# The MIDI fix: a YargMidiFix.dll next to YARGNETPLAY_DLL, or the newest release's. Optional, so failing to get it only
# warns (releases before it was split out don't have it).
midifix_path() {
    if [ -n "${YARGNETPLAY_DLL:-}" ]; then
        [ -f "$(dirname "$YARGNETPLAY_DLL")/YargMidiFix.dll" ] && echo "$(dirname "$YARGNETPLAY_DLL")/YargMidiFix.dll"
        return 0
    fi
    if [ ! -f "$tmp/fetched-midifix" ]; then
        step "Downloading the newest YARG MIDI Fix..." >&2
        cached_download "$MIDIFIX_URL" YargMidiFix.dll 1 > "$tmp/fetched-midifix"
    fi
    cat "$tmp/fetched-midifix"
}

# The pinned BepInEx zip, from the cache when it's there and still matches the checksum.
fetch() { # zip name, sha256 -> path of the verified zip
    local zip="$CACHE_DIR/$1"
    mkdir -p "$CACHE_DIR"
    if [ -f "$zip" ] && echo "$2  $zip" | sha256_check >/dev/null 2>&1; then echo "$zip"; return; fi
    step "Downloading BepInEx $BEPINEX_VERSION ($1)..." >&2
    download "$BASE_URL/$1" "$zip" || { rm -f "$zip"; fail "Couldn't download BepInEx."; }
    echo "$2  $zip" | sha256_check >/dev/null 2>&1 || {
        rm -f "$zip"
        fail "The BepInEx download didn't match its published checksum, so it wasn't installed. Try again later."
    }
    echo "$zip"
}

# ------------------------------------------------------------ install

# Points run_bepinex.sh (which starts the game with BepInEx loaded) at YARG. Rewritten through a temp file because
# sed -i differs between GNU and macOS.
set_executable() { # folder, executable name
    local script="$1/run_bepinex.sh"
    sed "s/^executable_name=.*/executable_name=\"$2\"/" "$script" > "$script.tmp"
    mv -f "$script.tmp" "$script"
    chmod +x "$script"
}

# macOS: an app in ~/Applications that starts YARG through run_bepinex.sh, since the YARC Launcher opens YARG.app
# without BepInEx. It's a bundle whose executable is a shell script; it starts the game in the background and exits,
# so YARG runs as its own app (as when run_bepinex.sh is started from Terminal). Made here, so it isn't quarantined.
make_mac_app() { # folder
    local t="$1" apps="$HOME/Applications" name old id
    name="YARG (Netplay) $(label "$t")"
    id="$(basename "$(dirname "$t")")"
    mkdir -p "$apps"
    # Replace the app made for this folder by an earlier run (its name changes when YARG updates).
    for old in "$apps"/"YARG (Netplay)"*.app; do
        if [ -f "$old/Contents/MacOS/launch" ] && grep -qF "cd $(printf %q "$t") " "$old/Contents/MacOS/launch"; then
            rm -rf "$old"
        fi
    done
    local app="$apps/$name.app"
    rm -rf "$app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>launch</string>
    <key>CFBundleIdentifier</key><string>io.github.johnnycyan.yargnetplay.launch.$(printf %s "$id" | tr -c 'A-Za-z0-9.-' '-')</string>
    <key>CFBundleName</key><string>$name</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleIconFile</key><string>PlayerIcon</string>
    <key>LSUIElement</key><true/>
</dict>
</plist>
EOF
    cp -f "$t/YARG.app/Contents/Resources/PlayerIcon.icns" "$app/Contents/Resources/" 2>/dev/null || true
    {
        echo '#!/bin/bash'
        echo "cd $(printf %q "$t") || exit 1"
        echo './run_bepinex.sh </dev/null >/dev/null 2>&1 &'
    } > "$app/Contents/MacOS/launch"
    chmod +x "$app/Contents/MacOS/launch"
    echo "  Added the app \"$name\" to $apps."
}

# Whether BepInEx (with what starts it on this kind of build) and the mod are in a YARG folder.
mod_installed() { # folder
    local t="$1"
    [ -f "$t/BepInEx/core/BepInEx.Preloader.dll" ] && [ -f "$t/BepInEx/plugins/YargNetplay/YargNetplay.dll" ] || return 1
    case "$(game_kind "$t")" in
        native) [ -f "$t/run_bepinex.sh" ] ;;
        mac) [ -f "$t/run_bepinex.sh" ] && [ -f "$t/libdoorstop.dylib" ] ;;
        windows) [ -f "$t/winhttp.dll" ] ;;
        *) return 1 ;;
    esac
}

wine_note=0
native_note=0
mac_note=0
install_into() { # folder, channel
    local t="$1" plugin midifix kind apps id
    plugin="$(plugin_for "$2")"
    midifix="$(midifix_path)"
    step "Installing into $(label "$t"): $t"
    kind="$(game_kind "$t")"

    if [ "$kind" = native ]; then
        if [ -f "$t/run_bepinex.sh" ] && [ -f "$t/BepInEx/core/BepInEx.Preloader.dll" ]; then
            echo "  BepInEx is already installed."
        else
            unzip -oq "$(fetch "$LINUX_ZIP" "$LINUX_SHA256")" -d "$t"
            echo "  Installed BepInEx $BEPINEX_VERSION."
        fi
        set_executable "$t" "YARG.x86_64"
        chmod +x "$t/YARG.x86_64"

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
    elif [ "$kind" = mac ]; then
        if [ -f "$t/run_bepinex.sh" ] && [ -f "$t/libdoorstop.dylib" ] && [ -f "$t/BepInEx/core/BepInEx.Preloader.dll" ]; then
            echo "  BepInEx is already installed."
        else
            unzip -oq "$(fetch "$MAC_ZIP" "$MAC_SHA256")" -d "$t"
            echo "  Installed BepInEx $BEPINEX_VERSION."
        fi
        set_executable "$t" "YARG.app"
        # Only files a browser downloaded are quarantined, but a quarantined libdoorstop.dylib would silently not load.
        xattr -d com.apple.quarantine "$t/libdoorstop.dylib" "$t/run_bepinex.sh" 2>/dev/null || true
        make_mac_app "$t"
        mac_note=1
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

    if [ -n "$midifix" ]; then
        mkdir -p "$t/BepInEx/plugins/YargMidiFix"
        cp -f "$midifix" "$t/BepInEx/plugins/YargMidiFix/YargMidiFix.dll"
        echo "  Installed YARG MIDI Fix."
    fi
}

# ------------------------------------------------------------ auto-reinstall

watched_folders() { [ -f "$WATCH_LIST" ] && grep -v '^[[:space:]]*$' "$WATCH_LIST" || true; }

# The XML-escaped text for a plist string.
xml_escape() { printf %s "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

# systemd unit files take paths with spaces as they are, but % starts a specifier.
unit_path() { printf %s "$1" | sed 's/%/%%/g'; }

enable_auto() { # launcher folders to add
    mkdir -p "$DATA_DIR"
    # Add these installs to the ones set up before (another run may have set up the other channel), dropping folders
    # whose install is gone.
    { watched_folders; printf '%s\n' "$@"; } | awk '!seen[$0]++' | while IFS= read -r dir; do
        [ -d "$(dirname "$dir")" ] && echo "$dir"
    done > "$WATCH_LIST.tmp"
    mv -f "$WATCH_LIST.tmp" "$WATCH_LIST"

    # The auto-reinstall runs its own copy of this script, refreshed on every install so it matches the installed mod.
    local src="${BASH_SOURCE[0]:-}"
    if [ -n "$src" ] && [ -f "$src" ]; then
        [ "$(cd "$(dirname "$src")" && pwd)/$(basename "$src")" = "$SCRIPT_COPY" ] || cp -f "$src" "$SCRIPT_COPY"
    else
        # Into a temp file first, so a failed download doesn't leave a half-written script for the running setup.
        download "$SCRIPT_URL" "$SCRIPT_COPY.new" && mv -f "$SCRIPT_COPY.new" "$SCRIPT_COPY" ||
            { rm -f "$SCRIPT_COPY.new"; return 1; }
    fi

    # It's started when the launcher changes the folder holding an install ("YARG Installs/<id>", which gets a new
    # tag.txt and installation folder on every update), and every 30 minutes and at login in case a change was missed.
    local dir
    if [ "$OS" = mac ]; then
        mkdir -p "$(dirname "$AGENT")"
        {
            cat <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>$AGENT_ID</string>
    <key>ProgramArguments</key>
    <array><string>/bin/bash</string><string>$(xml_escape "$SCRIPT_COPY")</string><string>--auto</string></array>
    <key>WatchPaths</key>
    <array>
EOF
            watched_folders | while IFS= read -r dir; do
                echo "        <string>$(xml_escape "$(dirname "$dir")")</string>"
            done
            cat <<EOF
    </array>
    <key>RunAtLoad</key><true/>
    <key>StartInterval</key><integer>1800</integer>
    <key>StandardOutPath</key><string>$(xml_escape "$DATA_DIR/auto-reinstall.log")</string>
    <key>StandardErrorPath</key><string>$(xml_escape "$DATA_DIR/auto-reinstall.log")</string>
</dict>
</plist>
EOF
        } > "$AGENT"
        launchctl unload "$AGENT" 2>/dev/null || true
        launchctl load -w "$AGENT"
    else
        command -v systemctl >/dev/null && systemctl --user show-environment >/dev/null 2>&1 || return 1
        mkdir -p "$UNIT_DIR"
        cat > "$UNIT_DIR/$UNIT.service" <<EOF
[Unit]
Description=Reinstall BepInEx and YARG Netplay after a YARG update

[Service]
Type=oneshot
ExecStart=/bin/bash "$(unit_path "$SCRIPT_COPY")" --auto
EOF
        {
            printf '[Unit]\nDescription=Watch YARG installs for updates\n\n[Path]\n'
            watched_folders | while IFS= read -r dir; do
                echo "PathModified=$(unit_path "$(dirname "$dir")")"
                echo "PathChanged=$(unit_path "$(dirname "$dir")/tag.txt")"
            done
            printf 'Unit=%s.service\n\n[Install]\nWantedBy=default.target\n' "$UNIT"
        } > "$UNIT_DIR/$UNIT.path"
        cat > "$UNIT_DIR/$UNIT.timer" <<EOF
[Unit]
Description=Check YARG installs for a missing YARG Netplay

[Timer]
OnStartupSec=1min
OnUnitActiveSec=30min
Unit=$UNIT.service

[Install]
WantedBy=timers.target
EOF
        systemctl --user daemon-reload
        systemctl --user enable "$UNIT.path" "$UNIT.timer" >/dev/null 2>&1
        systemctl --user restart "$UNIT.path" "$UNIT.timer"
    fi
}

auto_enabled() {
    if [ "$OS" = mac ]; then [ -f "$AGENT" ]; else [ -f "$UNIT_DIR/$UNIT.path" ]; fi
}

disable_auto() {
    if [ "$OS" = mac ]; then
        launchctl unload "$AGENT" 2>/dev/null || true
        rm -f "$AGENT"
    elif command -v systemctl >/dev/null; then
        systemctl --user disable --now "$UNIT.path" "$UNIT.timer" >/dev/null 2>&1 || true
        rm -f "$UNIT_DIR/$UNIT.service" "$UNIT_DIR/$UNIT.path" "$UNIT_DIR/$UNIT.timer"
        systemctl --user daemon-reload 2>/dev/null || true
    fi
    rm -rf "$DATA_DIR"
}

# Run by systemd or launchd. Waits until the launcher has stopped writing to the watched installs (changes under
# BepInEx/ are the mod's own and don't count), then reinstalls into every one that has YARG but lost the mod.
run_auto() {
    local folders=() dir stamp="$tmp/stamp" busy tries=0
    while IFS= read -r dir; do folders+=("$dir"); done < <(watched_folders)
    [ ${#folders[@]} -gt 0 ] || exit 0

    while [ $tries -lt 60 ]; do
        touch "$stamp"
        sleep 10
        busy=""
        for dir in "${folders[@]}"; do
            [ -d "$(dirname "$dir")" ] || continue
            # -quit instead of "| head": with pipefail, find killed by SIGPIPE would fail the line and end the script.
            # "|| true" because find also exits non-zero on files it can't read.
            busy="$(find "$(dirname "$dir")" -path '*/BepInEx' -prune -o -newer "$stamp" -print -quit 2>/dev/null || true)"
            [ -z "$busy" ] || break
        done
        [ -n "$busy" ] || break
        tries=$((tries + 1))
    done

    local channel status
    for dir in "${folders[@]}"; do
        [ "$(game_kind "$dir")" != none ] || continue
        mod_installed "$dir" && continue
        channel="$(tag_channel "$dir")"
        if [ -z "$channel" ]; then echo "Skipping $dir (no Stable or Nightly tag.txt next to it)."; continue; fi
        echo "$(date '+%Y-%m-%d %H:%M:%S') Reinstalling into $dir"
        # In a subshell so a failure (fail, or set -e) only skips this install. Not "( ... ) || ...": set -e is ignored
        # inside a command whose status is tested.
        set +e
        (set -e; install_into "$dir" "$channel")
        status=$?
        set -e
        [ "$status" = 0 ] || echo "  Failed to reinstall into $dir."
    done
}

if [ "$MODE" = auto ]; then run_auto; exit 0; fi

if [ "$MODE" = remove ]; then
    disable_auto
    printf '\033[32mThe auto-reinstall is off.\033[0m YARG Netplay stays installed until the next YARG update.\n'
    exit 0
fi

# ------------------------------------------------------------ interactive install

targets=()
if [ $# -ge 1 ]; then
    targets=("$(cd "$1" && pwd)")
else
    found=()
    while IFS= read -r dir; do found+=("$dir"); done < <(find_installs)
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
    [ "$(game_kind "$t")" != none ] || fail "$t doesn't contain YARG.x86_64, YARG.app or YARG.exe."
done

if pgrep -f 'YARG\.(x86_64|exe)|YARG\.app/Contents/MacOS/' >/dev/null 2>&1; then
    ask "YARG is running. Close it, then press Enter to continue. " _
fi

# Ask about channels up front (before anything is installed).
channels=()
for t in "${targets[@]}"; do channels+=("$(channel_of "$t")"); done

for i in "${!targets[@]}"; do
    install_into "${targets[$i]}" "${channels[$i]}"
done

# Auto-reinstall: only for launcher installs (a tag.txt next to the folder), since only the launcher replaces them.
# Asked once; later runs add their installs to it and refresh its copy of the script.
launcher_targets=()
for t in "${targets[@]}"; do [ -z "$(tag_channel "$t")" ] || launcher_targets+=("$t"); done
auto=0
if [ ${#launcher_targets[@]} -gt 0 ]; then
    if auto_enabled; then auto=1
    else
        echo
        echo "A YARG update from the YARC Launcher removes BepInEx and the mod."
        ask "Reinstall them automatically after every YARG update? (Y/n) " answer
        case "$answer" in ""|[yY]*) auto=1 ;; esac
    fi
    if [ "$auto" = 1 ]; then
        if enable_auto "${launcher_targets[@]}"; then
            echo "  The auto-reinstall is on. To turn it off, run: bash $(printf %q "$SCRIPT_COPY") --remove-auto-reinstall"
        else
            auto=0
            warn "  Couldn't set up the auto-reinstall (it needs systemd's user session on Linux)."
        fi
    fi
fi

printf '\n\033[32mDone.\033[0m\n'
if [ "$native_note" = 1 ]; then
    echo "Start YARG from the \"YARG (Netplay)\" shortcut (or run_bepinex.sh in the YARG folder), then choose Online."
    echo "The YARC Launcher's Play button starts YARG without the mod."
fi
if [ "$mac_note" = 1 ]; then
    echo "Start YARG from the \"YARG (Netplay)\" app in your Applications folder (~/Applications), then choose Online."
    echo "From Terminal: cd into the YARG folder and run ./run_bepinex.sh"
    echo "The YARC Launcher's Play button starts YARG without the mod."
fi
if [ "$wine_note" = 1 ]; then
    echo "Wine/Proton: Wine must load BepInEx's winhttp.dll. Start YARG with WINEDLLOVERRIDES=\"winhttp=n,b\""
    echo "(Steam/Lutris launch options: WINEDLLOVERRIDES=\"winhttp=n,b\" %command%)."
fi
if [ "$auto" = 0 ]; then
    echo "If the YARC Launcher updates or verifies YARG, it may remove BepInEx. Run this installer again if Online disappears."
fi
