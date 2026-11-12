# YARG Netplay installer: installs BepInEx 5 (if needed) and the newest YargNetplay plugin from GitHub into YARG.
# Run it through "Install YARG Netplay.bat", or: powershell -ExecutionPolicy Bypass -File install.ps1 [-YargPath <folder>]
# Run it again any time to update the mod.
param(
    # A YARG folder (the one with YARG.exe). Skips the search and the menu.
    [string]$YargPath,
    # Install this YargNetplay.dll instead of downloading the newest release (offline installs, testing a build).
    [string]$PluginPath
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# BepInEx 5 for 64-bit Windows Mono games. Pinned and checksum-verified; this is the version the mod is tested with.
$BepInExVersion = '5.4.23.6'
$BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
$BepInExSha256 = '6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755'

function Write-Step($text) { Write-Host "`n$text" -ForegroundColor Cyan }
function Fail($text) { Write-Host "`n$text" -ForegroundColor Red; exit 1 }

$PluginUrl = 'https://github.com/Johnnycyan/YargNetplay/releases/latest/download/YargNetplay.dll'

# ------------------------------------------------------------ find YARG

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
            $kind = if ($tag -match '^b\d+') { 'Nightly' } elseif ($tag -match '^v\d') { 'Stable' } else { 'YARG' }
            [pscustomobject]@{ Name = "$kind $tag"; Path = $folder }
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

$targets = @()
if ($YargPath) {
    $targets = @([pscustomobject]@{ Name = 'YARG'; Path = $YargPath })
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
        $targets = @([pscustomobject]@{ Name = 'YARG'; Path = $picked })
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

$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest very slow

# ------------------------------------------------------------ the plugin (the newest GitHub release)

if ($PluginPath) {
    if (-not (Test-Path $PluginPath)) { Fail "$PluginPath doesn't exist." }
    $plugin = (Resolve-Path $PluginPath).Path
    $downloadedPlugin = $false
} else {
    Write-Step 'Downloading the newest YARG Netplay...'
    $plugin = Join-Path ([IO.Path]::GetTempPath()) 'YargNetplay.dll'
    try { Invoke-WebRequest -Uri $PluginUrl -OutFile $plugin -UseBasicParsing }
    catch { Fail "Couldn't download YARG Netplay from GitHub: $($_.Exception.Message)" }
    $downloadedPlugin = $true
}
$version = (Get-Item $plugin).VersionInfo.ProductVersion -replace '\+.*'

# ------------------------------------------------------------ BepInEx (downloaded once, only if some target lacks it)

function Test-BepInEx($folder) {
    (Test-Path (Join-Path $folder 'winhttp.dll')) -and (Test-Path (Join-Path $folder 'BepInEx\core\BepInEx.Preloader.dll'))
}

$zip = $null
if ($targets | Where-Object { -not (Test-BepInEx $_.Path) }) {
    Write-Step "Downloading BepInEx $BepInExVersion..."
    $zip = Join-Path ([IO.Path]::GetTempPath()) "BepInEx_win_x64_$BepInExVersion.zip"
    try { Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing }
    catch { Fail "Couldn't download BepInEx: $($_.Exception.Message)" }

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $BepInExSha256) {
        Remove-Item $zip -Force
        Fail "The BepInEx download didn't match its published checksum, so it wasn't installed. Try again later."
    }
}

# ------------------------------------------------------------ install

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
    Copy-Item $plugin (Join-Path $pluginDir 'YargNetplay.dll') -Force
    Write-Host "  Installed YARG Netplay $version."
}

if ($zip) { Remove-Item $zip -Force -ErrorAction SilentlyContinue }
if ($downloadedPlugin) { Remove-Item $plugin -Force -ErrorAction SilentlyContinue }

Write-Host "`nDone. Start YARG and choose Online in the main menu." -ForegroundColor Green
Write-Host 'If the YARC Launcher updates or verifies YARG, it may remove BepInEx. Run this installer again if Online disappears.'
