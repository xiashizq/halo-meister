# Extracts Game.locres and Subtitles.locres from a Campaign Evolved install.
# Overwrites:
#   src/HaloMeister.App/Assets/GameText/<language>.json
#   src/HaloMeister.App/Assets/Subtitles/<language>.json
#
# Usage:
#   .\export-game-text.ps1
#   .\export-game-text.ps1 -Paks "D:\Games\Halo Campaign Evolved\Meteorite\Content\Paks"

param(
    [string]$Paks
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$gameOut = Join-Path $repo "src\HaloMeister.App\Assets\GameText"
$subtitlesOut = Join-Path $repo "src\HaloMeister.App\Assets\Subtitles"

function Find-PaksDirectory {
    $roots = @()
    if ($env:HALO_CAMPAIGN_EVOLVED_ROOT) {
        $roots += $env:HALO_CAMPAIGN_EVOLVED_ROOT
    }
    $remembered = Join-Path $env:LOCALAPPDATA "CartographerToolkit\game-binary-directory.txt"
    if (Test-Path -LiteralPath $remembered) {
        $roots += (Get-Content -LiteralPath $remembered -Raw).Trim()
    }

    foreach ($root in $roots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $dir = $root
        for ($depth = 0; $depth -lt 8 -and $dir; $depth++) {
            foreach ($relative in @(
                    "Meteorite\Content\Paks",
                    "Content\Meteorite\Content\Paks",
                    "Content\Paks")) {
                $candidate = Join-Path $dir $relative
                if (Test-Path -LiteralPath (Join-Path $candidate "pakchunk0-Windows.pak")) {
                    return $candidate
                }
            }
            $dir = Split-Path -Path $dir -Parent
        }
    }
    return $null
}

if ([string]::IsNullOrWhiteSpace($Paks)) {
    $Paks = Find-PaksDirectory
}
if ([string]::IsNullOrWhiteSpace($Paks) -or -not (Test-Path -LiteralPath $Paks)) {
    throw "Pass -Paks, or set the game folder in Cartographer Toolkit first."
}

Push-Location $PSScriptRoot
try {
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
}
finally {
    Pop-Location
}

$exe = Join-Path $PSScriptRoot "target\release\halomeister-text.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Extractor was not built: $exe"
}

New-Item -ItemType Directory -Force -Path $gameOut, $subtitlesOut | Out-Null
& $exe --paks $Paks --out $gameOut --catalog Game
if ($LASTEXITCODE -ne 0) { throw "game text extract failed" }
& $exe --paks $Paks --out $subtitlesOut --catalog Subtitles
if ($LASTEXITCODE -ne 0) { throw "subtitles extract failed" }
Write-Host "Updated $gameOut"
Write-Host "Updated $subtitlesOut"
