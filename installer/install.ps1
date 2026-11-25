# YARG Netplay installer: installs BepInEx 5 (if needed) and the newest YargNetplay plugin, plus the YargMidiFix plugin
# released with it, from GitHub into YARG.
# Run it through "Install YARG Netplay.bat", or: powershell -ExecutionPolicy Bypass -File install.ps1 [-YargPath <folder>]
# or straight from GitHub: irm https://github.com/Johnnycyan/YargNetplay/releases/latest/download/install.ps1 | iex
# Run it again any time to update the mod.
#
# A YARC Launcher update replaces the YARG folder, which removes BepInEx and the mod. The installer can set up an
# auto-reinstall: a copy of this script in %LOCALAPPDATA%\YargNetplay runs hidden from a per-user scheduled task at
# logon (-Watch), watches the launcher's install folders and reinstalls into the installs it was set up for as soon as
# an update has finished. -RemoveAutoReinstall turns it off again.
param(
    # A YARG folder (the one with YARG.exe). Skips the search and the menu.
    [string]$YargPath,
    # Install this YargNetplay.dll instead of downloading the newest release (offline installs, testing a build).
    # A YargMidiFix.dll in the same folder is installed too.
    [string]$PluginPath,
    # Run as the auto-reinstall watcher (started by the scheduled task, not meant to be run by hand).
    [switch]$Watch,
    # Turn the auto-reinstall off: stop the watcher, remove its scheduled task and its folder.
    [switch]$RemoveAutoReinstall
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest very slow

# BepInEx 5 for 64-bit Windows Mono games. Pinned and checksum-verified; this is the version the mod is tested with.
$BepInExVersion = '5.4.23.6'
$BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
$BepInExSha256 = '6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755'

# One build per YARG channel: YargNetplay-Stable.dll and YargNetplay-Nightly.dll.
$PluginUrl = 'https://github.com/Johnnycyan/YargNetplay/releases/latest/download/YargNetplay-{0}.dll'
# The MIDI fix plugin, installed alongside. One DLL for both channels.
$MidiFixUrl = 'https://github.com/Johnnycyan/YargNetplay/releases/latest/download/YargMidiFix.dll'
# This script, for the auto-reinstall copy when it was run through "irm | iex" and has no file of its own.
$ScriptUrl = 'https://github.com/Johnnycyan/YargNetplay/releases/latest/download/install.ps1'

# The installer's own folder: the download cache (so a reinstall still works offline) and the auto-reinstall's files.
$DataDir = Join-Path $env:LOCALAPPDATA 'YargNetplay'
$CacheDir = Join-Path $DataDir 'cache'
$ScriptCopy = Join-Path $DataDir 'install.ps1'
$WatchList = Join-Path $DataDir 'auto-reinstall.txt'   # one YARG folder per line
$LogFile = Join-Path $DataDir 'auto-reinstall.log'
$TaskName = 'YARG Netplay auto-reinstall'

function Write-Step($text) { Write-Host "`n$text" -ForegroundColor Cyan }
# Run through "irm | iex" the script runs inside the user's own PowerShell session (no $PSCommandPath), where exit
# would close their window, so it stops with an error there instead. The watcher catches the error and keeps running.
function Fail($text) {
    if ($PSCommandPath -and -not $Watch) { Write-Host "`n$text" -ForegroundColor Red; exit 1 }
    throw $text
}

# ------------------------------------------------------------ find YARG

# Stable releases are tagged v1.2.3 and nightlies b1234 (in the launcher's tag.txt).
function Get-Channel($tag) {
    if ($tag -match '^v\d') { 'Stable' } elseif ($tag -match '^b\d+') { 'Nightly' } else { $null }
}

# The channel from the launcher's tag.txt next to a YARG folder, or $null when there's none (a folder not from the
# launcher) or it isn't a known tag.
function Get-FolderChannel($folder) {
    $tagFile = Join-Path (Split-Path $folder -Parent) 'tag.txt'
    # Get-Content returns $null for an empty file (the launcher creates tag.txt before writing it), hence the "$()".
    if (Test-Path $tagFile) { Get-Channel "$(Get-Content $tagFile -Raw)".Trim() } else { $null }
}

# For a folder picked by hand: the launcher's tag.txt next to it if there is one, otherwise ask.
function New-Target($folder) {
    # Resolved against PowerShell's current folder ($PWD), which .NET's GetFullPath doesn't know about, so the
    # auto-reinstall saves the same folder the install went into.
    $folder = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($folder)
    $channel = Get-FolderChannel $folder
    while (-not $channel) {
        $answer = Read-Host "Is $folder YARG Stable or Nightly? (S/N)"
        if ($answer -match '^[sS]') { $channel = 'Stable' } elseif ($answer -match '^[nN]') { $channel = 'Nightly' }
    }
    [pscustomobject]@{ Name = "YARG $channel"; Path = $folder; Channel = $channel }
}

function Get-YargInstalls {
    # The YARC Launcher keeps each version in "<download location>\YARG Installs\<id>\installation", with the
    # version name in "<id>\tag.txt". The download location can be changed in the launcher.
    $root = Join-Path $env:LOCALAPPDATA 'YARC'
    $settings = Join-Path $root 'Launcher\settings.json'
    if (Test-Path $settings) {
        try {
            $location = (Get-Content $settings -Raw | ConvertFrom-Json).download_location
            if ($location) { $root = $location }
        } catch { }
    }

    $installs = Join-Path $root 'YARG Installs'
    if (-not (Test-Path $installs)) { return @() }

    Get-ChildItem $installs -Directory | ForEach-Object {
        $folder = Join-Path $_.FullName 'installation'
        if (Test-Path (Join-Path $folder 'YARG.exe')) {
            $tagFile = Join-Path $_.FullName 'tag.txt'
            $tag = if (Test-Path $tagFile) { (Get-Content $tagFile -Raw).Trim() } else { $_.Name }
            $channel = Get-Channel $tag
            if ($channel) { [pscustomobject]@{ Name = "$channel $tag"; Path = $folder; Channel = $channel } }
            else { New-Target $folder }
        }
    }
}

function Select-YargFolder {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select your YARG folder (the one that contains YARG.exe)'
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
    return $dialog.SelectedPath
}

# ------------------------------------------------------------ downloads (kept in the cache)

# Downloads into the cache. When GitHub can't be reached, an earlier download of the same file is used instead, so
# the auto-reinstall still works offline (it may be an older version; the in-game update check offers the newest).
function Get-CachedDownload($url, $name, [switch]$Optional) {
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $file = Join-Path $CacheDir $name
    try {
        Invoke-WebRequest -Uri $url -OutFile "$file.part" -UseBasicParsing
        Move-Item "$file.part" $file -Force
    } catch {
        Remove-Item "$file.part" -Force -ErrorAction SilentlyContinue
        $reason = $_.Exception.Message
        if (Test-Path $file) { Write-Host "  Couldn't download it ($reason), using the copy downloaded earlier." -ForegroundColor Yellow }
        elseif ($Optional) { Write-Host "  Couldn't download it, skipping: $reason" -ForegroundColor Yellow; return $null }
        else { Fail "Couldn't download $name from GitHub: $reason" }
    }
    return $file
}

# The pinned BepInEx zip, from the cache when it's there and still matches the checksum.
function Get-BepInExZip {
    New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null
    $zip = Join-Path $CacheDir "BepInEx_win_x64_$BepInExVersion.zip"
    if ((Test-Path $zip) -and (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() -eq $BepInExSha256) { return $zip }

    Write-Step "Downloading BepInEx $BepInExVersion..."
    try { Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing }
    catch { Remove-Item $zip -Force -ErrorAction SilentlyContinue; Fail "Couldn't download BepInEx: $($_.Exception.Message)" }

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $BepInExSha256) {
        Remove-Item $zip -Force
        Fail "The BepInEx download didn't match its published checksum, so it wasn't installed. Try again later."
    }
    return $zip
}

# ------------------------------------------------------------ install

function Test-BepInEx($folder) {
    (Test-Path (Join-Path $folder 'winhttp.dll')) -and (Test-Path (Join-Path $folder 'BepInEx\core\BepInEx.Preloader.dll'))
}

function Test-ModInstalled($folder) {
    (Test-BepInEx $folder) -and (Test-Path (Join-Path $folder 'BepInEx\plugins\YargNetplay\YargNetplay.dll'))
}

function Install-Targets($targets) {
    # The plugin: the newest GitHub release, per channel.
    $plugins = @{}
    if ($PluginPath) {
        if (-not (Test-Path $PluginPath)) { Fail "$PluginPath doesn't exist." }
        foreach ($t in $targets) { $plugins[$t.Channel] = (Resolve-Path $PluginPath).Path }
    } else {
        foreach ($channel in @($targets | ForEach-Object { $_.Channel } | Select-Object -Unique)) {
            Write-Step "Downloading the newest YARG Netplay for YARG $channel..."
            $plugins[$channel] = Get-CachedDownload ($PluginUrl -f $channel) "YargNetplay-$channel.dll"
        }
    }

    # The MIDI fix: a YargMidiFix.dll next to -PluginPath, or the newest release's. Optional, so failing to get it only
    # warns (releases before it was split out don't have it).
    $midiFix = $null
    if ($PluginPath) {
        $local = Join-Path (Split-Path (Resolve-Path $PluginPath).Path -Parent) 'YargMidiFix.dll'
        if (Test-Path $local) { $midiFix = $local }
    } else {
        Write-Step 'Downloading the newest YARG MIDI Fix...'
        $midiFix = Get-CachedDownload $MidiFixUrl 'YargMidiFix.dll' -Optional
    }

    # BepInEx: only fetched if some target lacks it.
    $zip = $null
    if ($targets | Where-Object { -not (Test-BepInEx $_.Path) }) { $zip = Get-BepInExZip }

    foreach ($t in $targets) {
        Write-Step "Installing into $($t.Name): $($t.Path)"

        if (Test-BepInEx $t.Path) {
            Write-Host '  BepInEx is already installed.'
        } else {
            Expand-Archive -Path $zip -DestinationPath $t.Path -Force
            Write-Host "  Installed BepInEx $BepInExVersion."
        }

        $pluginDir = Join-Path $t.Path 'BepInEx\plugins\YargNetplay'
        New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
        $plugin = $plugins[$t.Channel]
        Copy-Item $plugin (Join-Path $pluginDir 'YargNetplay.dll') -Force
        $version = (Get-Item $plugin).VersionInfo.ProductVersion -replace '\+.*'
        Write-Host "  Installed YARG Netplay $version."

        if ($midiFix) {
            $midiDir = Join-Path $t.Path 'BepInEx\plugins\YargMidiFix'
            New-Item -ItemType Directory -Force -Path $midiDir | Out-Null
            Copy-Item $midiFix (Join-Path $midiDir 'YargMidiFix.dll') -Force
            Write-Host '  Installed YARG MIDI Fix.'
        }
    }
}

# ------------------------------------------------------------ auto-reinstall

function Get-WatchedFolders {
    if (Test-Path $WatchList) { @(Get-Content $WatchList | Where-Object { $_.Trim() }) } else { @() }
}

# The watcher processes running this script's auto-reinstall copy. Stopping the scheduled task doesn't end them
# (the task's process is conhost, which only starts PowerShell), so they're found by command line.
function Stop-Watcher {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $TaskName }
    $ids = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like "*$ScriptCopy*-Watch*" } | ForEach-Object { $_.ProcessId })
    foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    # Stop-Process doesn't wait. A watcher started while the old one still holds the mutex would see it taken and quit.
    if ($ids) { Wait-Process -Id $ids -Timeout 10 -ErrorAction SilentlyContinue }
}

function Enable-AutoReinstall($targets) {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

    # Add these installs to the ones set up before (another run may have set up the other channel), dropping folders
    # that no longer exist.
    $folders = @(Get-WatchedFolders) + @($targets | ForEach-Object { [IO.Path]::GetFullPath($_.Path) }) |
        Where-Object { Test-Path (Split-Path $_ -Parent) } | Select-Object -Unique
    Set-Content -Path $WatchList -Value $folders

    # The watcher runs from its own copy of this script, refreshed on every install so it matches the installed mod.
    # The new copy is fetched before the running watcher is stopped, so a failed download leaves the old setup
    # working instead of a stopped watcher and a half-written script.
    $newCopy = $null
    if (-not $PSCommandPath) {
        $newCopy = "$ScriptCopy.new"
        try { Invoke-WebRequest -Uri $ScriptUrl -OutFile $newCopy -UseBasicParsing }
        catch { Remove-Item $newCopy -Force -ErrorAction SilentlyContinue; throw }
    }
    Stop-Watcher
    if ($newCopy) {
        Move-Item $newCopy $ScriptCopy -Force
    } elseif ([IO.Path]::GetFullPath($PSCommandPath) -ne $ScriptCopy) {
        Copy-Item $PSCommandPath $ScriptCopy -Force
    }

    # A per-user logon task needs no admin rights. conhost --headless runs PowerShell without a console window
    # (-WindowStyle Hidden would still flash one at every logon).
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute 'conhost.exe' `
        -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$ScriptCopy`" -Watch"
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
        -Principal $principal -Description 'Reinstalls BepInEx and YARG Netplay when the YARC Launcher updates YARG.' `
        -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName
}

function Disable-AutoReinstall {
    Stop-Watcher
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
}

function Write-Log($text) {
    Add-Content -Path $LogFile -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $text"
}

# Reinstalls into every watched folder that has YARG but lost BepInEx or the mod. Folders mid-update (no YARG.exe
# yet) and ones whose channel can't be told are left for the next check.
function Invoke-Reinstall {
    $targets = @(Get-WatchedFolders | Where-Object { (Test-Path (Join-Path $_ 'YARG.exe')) -and -not (Test-ModInstalled $_) } |
        ForEach-Object {
            $channel = Get-FolderChannel $_
            if ($channel) { [pscustomobject]@{ Name = "YARG $channel"; Path = $_; Channel = $channel } }
            else { Write-Log "Skipping $_ (no Stable or Nightly tag.txt next to it)." }
        })
    if (-not $targets) { return }

    Write-Log "Reinstalling into $($targets.Path -join ', ')"
    try {
        Install-Targets $targets *>&1 | ForEach-Object { if ("$_".Trim()) { Write-Log "  $("$_".Trim())" } }
    } catch {
        Write-Log "  Failed: $($_.Exception.Message)"
    }
}

function Start-Watching {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    # Only one watcher at a time, however it was started.
    $mutex = New-Object Threading.Mutex($false, 'Local\YargNetplayAutoReinstall')
    # A watcher killed while another process had the mutex open leaves it abandoned: WaitOne then throws, but the
    # mutex is ours.
    try { if (-not $mutex.WaitOne(0)) { return } } catch [Threading.AbandonedMutexException] { }

    # Keep the log short.
    if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 256KB) {
        Get-Content $LogFile -Tail 500 | Set-Content "$LogFile.tmp"; Move-Item "$LogFile.tmp" $LogFile -Force
    }
    Write-Log 'Watcher started.'

    # Watch the folder holding each install's "<id>" folder (the launcher's "YARG Installs"), so the watch survives the
    # launcher deleting and recreating the install. Changes under BepInEx\ are the mod's own (logs, config) and ignored.
    $roots = @(Get-WatchedFolders | ForEach-Object { Split-Path (Split-Path $_ -Parent) -Parent } |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique)
    $i = 0
    foreach ($root in $roots) {
        $watcher = New-Object IO.FileSystemWatcher $root
        $watcher.IncludeSubdirectories = $true
        $watcher.NotifyFilter = [IO.NotifyFilters]'FileName, DirectoryName, LastWrite'
        $watcher.InternalBufferSize = 64KB
        foreach ($name in 'Created', 'Changed', 'Deleted', 'Renamed', 'Error') {
            Register-ObjectEvent $watcher $name -SourceIdentifier "YargNetplay$i$name" | Out-Null
        }
        $watcher.EnableRaisingEvents = $true
        $i++
    }

    # Reinstall once the folder has been quiet for a while, so the launcher has finished extracting. The periodic
    # check catches anything the watcher missed (an overflowing buffer, an install folder that didn't exist yet).
    $quiet = [TimeSpan]::FromSeconds(10)
    $fallback = [TimeSpan]::FromMinutes(30)
    try { Invoke-Reinstall } catch { try { Write-Log "  Check failed: $($_.Exception.Message)" } catch { } }
    $lastCheck = Get-Date
    $pendingSince = $null
    while ($true) {
        $timeout = if ($pendingSince) { 2 } else { 60 }
        if (Wait-Event -Timeout $timeout) {
            foreach ($e in @(Get-Event)) {
                $path = $e.SourceEventArgs.FullPath
                if ($e.SourceEventArgs -is [IO.ErrorEventArgs] -or ($path -and $path -notmatch '\\BepInEx(\\|$)')) {
                    $pendingSince = Get-Date
                }
                Remove-Event -EventIdentifier $e.EventIdentifier
            }
        }
        $now = Get-Date
        # The periodic check waits too while changes are pending, so it never installs into a half-extracted folder.
        if ($pendingSince) { $due = $now - $pendingSince -ge $quiet } else { $due = $now - $lastCheck -ge $fallback }
        if ($due) {
            $pendingSince = $null
            # An error here (a file the launcher is still writing, a locked log) must not end the watcher.
            try { Invoke-Reinstall } catch { try { Write-Log "  Check failed: $($_.Exception.Message)" } catch { } }
            $lastCheck = Get-Date
        }
    }
}

if ($Watch) { Start-Watching; return }

if ($RemoveAutoReinstall) {
    Disable-AutoReinstall
    Write-Host 'The auto-reinstall is off. YARG Netplay stays installed until the next YARG update.' -ForegroundColor Green
    return
}

# ------------------------------------------------------------ interactive install

$targets = @()
if ($YargPath) {
    $targets = @(New-Target $YargPath)
} else {
    $found = @(Get-YargInstalls)
    if ($found.Count -eq 1) {
        $targets = $found
    } elseif ($found.Count -gt 1) {
        Write-Host 'Found these YARG versions:'
        for ($i = 0; $i -lt $found.Count; $i++) { Write-Host "  $($i + 1). $($found[$i].Name)" }
        Write-Host '  A. All of them'
        $choice = Read-Host 'Install into which one? (press Enter for all)'
        if ([string]::IsNullOrWhiteSpace($choice) -or $choice -match '^[aA]') { $targets = $found }
        elseif ($choice -match '^\d+$' -and [int]$choice -ge 1 -and [int]$choice -le $found.Count) { $targets = @($found[[int]$choice - 1]) }
        else { Fail "`"$choice`" isn't one of the choices. Nothing was installed." }
    } else {
        Write-Host 'No YARC Launcher installs of YARG were found. Pick your YARG folder.'
        $picked = Select-YargFolder
        if (-not $picked) { Fail 'No folder picked. Nothing was installed.' }
        $targets = @(New-Target $picked)
    }
}

foreach ($t in $targets) {
    if (-not (Test-Path (Join-Path $t.Path 'YARG.exe'))) { Fail "$($t.Path) doesn't contain YARG.exe." }
}

# YARG keeps its files open while running, and BepInEx only loads at startup.
$running = Get-Process YARG -ErrorAction SilentlyContinue | Where-Object {
    $exe = $_.Path
    $exe -and ($targets | Where-Object { [IO.Path]::GetFullPath((Join-Path $_.Path 'YARG.exe')) -eq [IO.Path]::GetFullPath($exe) })
}
if ($running) {
    Write-Host 'YARG is running. Close it, then press Enter to continue.' -ForegroundColor Yellow
    [void](Read-Host)
}

Install-Targets $targets

# Auto-reinstall: only for launcher installs (a tag.txt next to the folder), since only the launcher replaces them.
# Asked once; later runs add their installs to it and refresh the watcher's copy of the script.
$launcherTargets = @($targets | Where-Object { Get-FolderChannel $_.Path })
$auto = $false
if ($launcherTargets) {
    $auto = [bool](Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
    if (-not $auto) {
        Write-Host "`nA YARG update from the YARC Launcher removes BepInEx and the mod."
        $answer = Read-Host 'Reinstall them automatically after every YARG update? (Y/n)'
        $auto = [string]::IsNullOrWhiteSpace($answer) -or $answer -match '^[yY]'
    }
    if ($auto) {
        try {
            Enable-AutoReinstall $launcherTargets
            Write-Host '  The auto-reinstall is on. To turn it off, run:'
            Write-Host "  powershell -ExecutionPolicy Bypass -File `"$ScriptCopy`" -RemoveAutoReinstall"
        } catch {
            $auto = $false
            Write-Host "  Couldn't set up the auto-reinstall: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

Write-Host "`nDone. Start YARG and choose Online in the main menu." -ForegroundColor Green
if (-not $auto) {
    Write-Host 'If the YARC Launcher updates or verifies YARG, it may remove BepInEx. Run this installer again if Online disappears.'
}
