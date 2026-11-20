# Build the mod: dotnet build -c Release -> dist\Pathologic2Dlss.dll
# and a release archive dist\Pathologic2Dlss-<version>.zip that is extracted into the game folder
# (it contains BepInEx\plugins\pathologic2-dlss\...).
# -Bundle also builds dist\Pathologic2Dlss-<version>-with-dlls.zip: the same plus NVUnityPlugin.dll and nvngx_dlss.dll
# (taken from -GameDir, placed next to Pathologic.exe) and the third-party notices.
# Example: powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-GameDir "D:\Games\Pathologic"] [-Bundle]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Pathologic",
    [switch]$Bundle
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if (-not (Test-Path "$GameDir\Pathologic_Data\Managed\Assembly-CSharp.dll")) { throw "Game not found: $GameDir" }
if (-not (Test-Path "$GameDir\BepInEx\core\BepInEx.dll")) { throw "BepInEx not found in the game folder: $GameDir\BepInEx\core" }

& dotnet build "$root\Pathologic2Dlss.csproj" -c Release -nologo "-p:GameDir=$GameDir" "-p:OutDir=$root\bin\Release\"
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)" }

New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\bin\Release\Pathologic2Dlss.dll" $dist -Force
$f = Get-Item "$dist\Pathologic2Dlss.dll"
"Done: $($f.FullName) ($($f.Length) bytes)"

# Release archives
$version = $f.VersionInfo.ProductVersion -replace '\+.*$', ''
# ZipArchive instead of Compress-Archive: PS 5.1 writes entry names with backslashes, which some unpackers mishandle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# $items: array of @(source file, entry name inside the archive)
function New-Zip([string]$path, [object[]]$items) {
    if (Test-Path $path) { Remove-Item $path -Force -Confirm:$false }
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $items) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item[0], $item[1]) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }
}

$prefix = 'BepInEx/plugins/pathologic2-dlss/'
$modItems = @(
    , @($f.FullName, "${prefix}Pathologic2Dlss.dll")
    , @("$root\README.md", "${prefix}README.md")
    , @("$root\LICENSE", "${prefix}LICENSE")
)
$zip = Join-Path $dist "Pathologic2Dlss-$version.zip"
New-Zip $zip $modItems
"Release archive: $zip"

if ($Bundle) {
    $nvup = "$GameDir\NVUnityPlugin.dll"
    $ngx = "$GameDir\nvngx_dlss.dll"
    foreach ($p in $nvup, $ngx) { if (-not (Test-Path $p)) { throw "$p not found; it is needed for -Bundle" } }
    $tested = 'FB1C32303372259D143638D81513525EC0E19AF3659155BA9703329C15065813'
    if ((Get-FileHash $nvup -Algorithm SHA256).Hash -ne $tested) {
        Write-Warning "NVUnityPlugin.dll differs from the tested build (Unity 6000.2.6f2) described in THIRD-PARTY-NOTICES.md."
    }
    $bundleItems = $modItems + @(
        , @("$root\third-party\THIRD-PARTY-NOTICES.md", "${prefix}THIRD-PARTY-NOTICES.md")
        , @("$root\third-party\NVIDIA-RTX-SDKs-LICENSE.txt", "${prefix}NVIDIA-RTX-SDKs-LICENSE.txt")
        , @($nvup, 'NVUnityPlugin.dll')
        , @($ngx, 'nvngx_dlss.dll')
    )
    $bundleZip = Join-Path $dist "Pathologic2Dlss-$version-with-dlls.zip"
    New-Zip $bundleZip $bundleItems
    "Release archive with DLLs: $bundleZip (nvngx_dlss.dll $((Get-Item $ngx).VersionInfo.FileVersion))"
}
