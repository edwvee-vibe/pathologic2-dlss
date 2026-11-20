# Install: copies dist\Pathologic2Dlss.dll to <Game>\BepInEx\plugins\pathologic2-dlss\.
# Game files are not modified. -Uninstall removes the plugin folder (the config BepInEx\config\pathologic2.dlss.cfg stays).
# Example: powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 [-GameDir ...] [-Uninstall]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Pathologic",
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$target = Join-Path $GameDir 'BepInEx\plugins\pathologic2-dlss'

# Until 2026-11-14 the mod was called pathologic2-dlaa. Two copies must not run at once: remove the old folder
# and rename the old config if there is no new one yet.
$oldTarget = Join-Path $GameDir 'BepInEx\plugins\pathologic2-dlaa'
if (Test-Path $oldTarget) {
    Remove-Item -Recurse -Force $oldTarget -Confirm:$false
    "Removed old plugin folder: $oldTarget"
}
$oldCfg = Join-Path $GameDir 'BepInEx\config\pathologic2.dlaa.cfg'
$newCfg = Join-Path $GameDir 'BepInEx\config\pathologic2.dlss.cfg'
if (-not $Uninstall -and (Test-Path $oldCfg) -and -not (Test-Path $newCfg)) {
    Move-Item $oldCfg $newCfg
    "Config renamed: $newCfg"
}

if ($Uninstall) {
    if (Test-Path $target) {
        Remove-Item -Recurse -Force $target -Confirm:$false
        "Removed: $target"
    } else {
        "Not installed: $target"
    }
    return
}

if (-not (Test-Path "$GameDir\BepInEx\core\BepInEx.dll")) { throw "BepInEx 5 not found in the game folder: $GameDir" }
$dll = Join-Path $root 'dist\Pathologic2Dlss.dll'
if (-not (Test-Path $dll)) { throw "$dll not found; run build.ps1 first" }

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $dll $target -Force
"Installed: $target\Pathologic2Dlss.dll"

# Native libraries: Unity looks for the plugin in Pathologic_Data\Plugins\x86_64 and next to the exe, NGX next to the exe.
foreach ($name in 'NVUnityPlugin.dll', 'nvngx_dlss.dll') {
    $hits = @(@("$GameDir\$name", "$GameDir\Pathologic_Data\Plugins\x86_64\$name") | Where-Object { Test-Path $_ })
    if ($hits) {
        $v = (Get-Item $hits[0]).VersionInfo.FileVersion
        "  found $name ($($hits[0])$(if ($v) { ", version $v" }))"
    } else {
        Write-Warning "$name not found next to Pathologic.exe; the mod will not turn on and the game will keep its own TAA."
    }
}
