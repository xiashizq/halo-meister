[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$project = Join-Path $projectRoot 'src\HaloMeister.App\HaloMeister.App.csproj'
$versionProps = Join-Path $projectRoot 'Directory.Build.props'
[xml]$versionXml = Get-Content -LiteralPath $versionProps
$canonicalVersion = [string]$versionXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $canonicalVersion
} elseif ($Version -ne $canonicalVersion) {
    throw "Requested version $Version does not match the canonical project version $canonicalVersion. Update Directory.Build.props first."
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Release version must be SemVer-like (for example 1.2.3): $Version"
}

$releaseRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $projectRoot 'artifacts\release'))
$packageName = "CartographerToolkit-$Version-win-x64"
$packageDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $releaseRoot $packageName))
$archivePath = [System.IO.Path]::GetFullPath(
    (Join-Path $releaseRoot "$packageName.zip"))
$checksumPath = "$archivePath.sha256"
$buildOutput = [System.IO.Path]::GetFullPath(
    (Join-Path $releaseRoot '.build\'))

foreach ($path in @($packageDirectory, $archivePath, $checksumPath, $buildOutput)) {
    if (!$path.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the release directory: $path"
    }
}

$requiredSources = @(
    'src\HaloMeister.App\Assets\UE4SS\bridge.lua',
    'src\HaloMeister.App\Assets\UE4SS\halomeister_blam_v45.dll',
    'src\HaloMeister.App\Assets\UE4SSLoader\NOTICE.md',
    'src\HaloMeister.App\Assets\UE4SSLoader\Signatures\FName_Constructor.lua',
    'src\HaloMeister.App\Assets\Native\halomeister-tagmod-exporter.exe',
    'src\HaloMeister.App\Assets\Native\halomeister-music.exe',
    'src\HaloMeister.App\Assets\Native\halomeister-models.exe',
    'src\HaloMeister.App\Assets\Overlays\MMYJ_FULL_VEHI_WAP_P.utoc',
    'src\HaloMeister.App\Assets\Overlays\MMYJ_FULL_VEHI_WAP_P.ucas',
    'src\HaloMeister.App\Assets\Overlays\MMYJ_FULL_VEHI_WAP_P.pak',
    'src\HaloMeister.App\Assets\Overlays\MMYJ_FULL_CHAR_P.utoc',
    'src\HaloMeister.App\Assets\Overlays\MMYJ_FULL_CHAR_P.ucas',
    'src\HaloMeister.App\Assets\Overlays\MMYJ_FULL_CHAR_P.pak',
    'src\HaloMeister.App\Assets\Definitions\haloce_evolved\_meta.json'
)
foreach ($relative in $requiredSources) {
    $source = Join-Path $projectRoot $relative
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required release source is missing: $relative"
    }
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
foreach ($path in @($packageDirectory, $archivePath, $checksumPath, $buildOutput)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $packageDirectory `
    -p:Platform=x64 `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    "-p:BaseOutputPath=$buildOutput"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$requiredPackageFiles = @(
    'CartographerToolkit.exe',
    'CartographerToolkit.dll',
    'START_HERE.txt',
    'Assets\UE4SS\bridge.lua',
    'Assets\UE4SS\halomeister_blam_v45.dll',
    'Assets\UE4SSLoader\NOTICE.md',
    'Assets\UE4SSLoader\Signatures\FName_Constructor.lua',
    'Assets\Native\halomeister-tagmod-exporter.exe',
    'Assets\Native\halomeister-music.exe',
    'Assets\Native\halomeister-models.exe',
    'Assets\Overlays\MMYJ_FULL_VEHI_WAP_P.utoc',
    'Assets\Overlays\MMYJ_FULL_VEHI_WAP_P.ucas',
    'Assets\Overlays\MMYJ_FULL_VEHI_WAP_P.pak',
    'Assets\Overlays\MMYJ_FULL_CHAR_P.utoc',
    'Assets\Overlays\MMYJ_FULL_CHAR_P.ucas',
    'Assets\Overlays\MMYJ_FULL_CHAR_P.pak',
    'Assets\Definitions\haloce_evolved\_meta.json',
    'Assets\Definitions\haloce_evolved\weapon.json'
)
foreach ($relative in $requiredPackageFiles) {
    $packaged = Join-Path $packageDirectory $relative
    if (!(Test-Path -LiteralPath $packaged -PathType Leaf)) {
        throw "Published release is incomplete: $relative"
    }
}

# Directory.Build.props is canonical. Stamp the packaged Lua so a forgotten
# source bump cannot ship a bridge that refuses to install against this app.
$packagedBridge = Join-Path $packageDirectory 'Assets\UE4SS\bridge.lua'
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
$bridgeText = [System.IO.File]::ReadAllText($packagedBridge)
$versionMarker = '-- HALOMEISTER SCRIPTING BRIDGE:VERSION'
$markerMatch = [regex]::Match($bridgeText, [regex]::Escape($versionMarker) + '\s+\S+')
if (-not $markerMatch.Success) {
    throw "Packaged bridge.lua is missing the VERSION marker."
}
$bridgeText = $bridgeText.Remove($markerMatch.Index, $markerMatch.Length).Insert(
    $markerMatch.Index,
    "$versionMarker $Version")
$assignmentMatch = [regex]::Match($bridgeText, 'local\s+bridge_version\s*=\s*(["''])[^"'']*\1')
if (-not $assignmentMatch.Success) {
    throw "Packaged bridge.lua is missing local bridge_version."
}
$bridgeText = $bridgeText.Remove($assignmentMatch.Index, $assignmentMatch.Length).Insert(
    $assignmentMatch.Index,
    "local bridge_version = `"$Version`"")
[System.IO.File]::WriteAllText($packagedBridge, $bridgeText, $utf8NoBom)
Write-Host "Stamped packaged bridge.lua to $Version."

$definitionCount = @(
    Get-ChildItem `
        -LiteralPath (Join-Path $packageDirectory 'Assets\Definitions\haloce_evolved') `
        -Filter '*.json' `
        -File
).Count
if ($definitionCount -lt 100) {
    throw "Published release contains only $definitionCount definition files."
}

# Pure package: drop debug/symbol clutter (keep runtime XML assets untouched).
$removed = 0
Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Extension -ieq '.pdb' -or
        $_.Name -ieq 'createdump.exe'
    } |
    ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
        $removed++
    }
if ($removed -gt 0) {
    Write-Host "Purged $removed debug/symbol file(s) from the package."
}

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archivePath `
    -CompressionLevel Optimal
$sha256 = [System.Security.Cryptography.SHA256]::Create()
$archiveStream = [System.IO.File]::OpenRead($archivePath)
try {
    $hashBytes = $sha256.ComputeHash($archiveStream)
} finally {
    $archiveStream.Dispose()
    $sha256.Dispose()
}
$hashText = ([System.BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
"$hashText *$([System.IO.Path]::GetFileName($archivePath))" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

$files = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
if (Test-Path -LiteralPath $buildOutput) {
    Remove-Item -LiteralPath $buildOutput -Recurse -Force
}
Write-Host ""
Write-Host "Release ready:"
Write-Host "  Folder:   $packageDirectory"
Write-Host "  Archive:  $archivePath"
Write-Host "  SHA-256:  $checksumPath"
Write-Host ("  Payload:  {0} files, {1:N1} MiB" -f $files.Count, ($bytes / 1MB))
