# Expand campaign vehicle/weapon/scenery/machine palettes and hm_ally/hm_hostile
# scaffolds into MMYJ_FULL_VEHI_WAP_P, the fuller character roster into
# MMYJ_FULL_CHAR_P, a merged pair for when both are installed, and one
# combat-behavior overlay per character. Engine palette limits: objects 256
# entries and characters 64.
param(
    [string]$Paks,
    [string]$Output = (Join-Path $PSScriptRoot "out\MMYJ_FULL_VEHI_WAP_P.utoc"),
    [switch]$DryRun,
    [switch]$Install,
    [switch]$UpdateBundledAssets
)

$ErrorActionPreference = "Stop"

function Resolve-PaksDirectory {
    param([string]$Explicit)
    if ($Explicit) {
        $full = (Resolve-Path $Explicit).Path
        if (-not (Get-ChildItem $full -Filter *.utoc -ErrorAction SilentlyContinue)) {
            throw "No .utoc files in $full"
        }
        return $full
    }
    if ($env:HALO_CAMPAIGN_EVOLVED_ROOT) {
        $candidates = @(
            (Join-Path $env:HALO_CAMPAIGN_EVOLVED_ROOT "Meteorite\Content\Paks"),
            (Join-Path $env:HALO_CAMPAIGN_EVOLVED_ROOT "Content\Meteorite\Content\Paks")
        )
        foreach ($candidate in $candidates) {
            if ((Test-Path $candidate) -and (Get-ChildItem $candidate -Filter *.utoc -ErrorAction SilentlyContinue)) {
                return (Resolve-Path $candidate).Path
            }
        }
    }
    $roots = Get-ChildItem "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if ($props.DisplayName -match 'Halo.*Campaign Evolved' -and $props.InstallLocation) {
                $props.InstallLocation
            }
        }
    foreach ($root in $roots) {
        $candidate = Join-Path $root "Meteorite\Content\Paks"
        if ((Test-Path $candidate) -and (Get-ChildItem $candidate -Filter *.utoc -ErrorAction SilentlyContinue)) {
            return (Resolve-Path $candidate).Path
        }
    }
    throw "Could not find Meteorite\Content\Paks. Pass -Paks or set HALO_CAMPAIGN_EVOLVED_ROOT."
}

function Remove-OverlayStem {
    param([string]$PaksDir, [string]$Stem)
    foreach ($ext in @(".utoc", ".ucas", ".pak")) {
        $path = Join-Path $PaksDir ($Stem + $ext)
        if (Test-Path $path) {
            Remove-Item -Force $path
            Write-Host "Removed $path"
        }
    }
}

$exeCandidates = @(
    (Join-Path $PSScriptRoot "target\release\halomeister-tagmod-exporter.exe"),
    (Join-Path $PSScriptRoot "..\..\src\HaloMeister.App\Assets\Native\halomeister-tagmod-exporter.exe")
)
$exe = $exeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    Push-Location $PSScriptRoot
    try { cargo build --release }
    finally { Pop-Location }
    $exe = Join-Path $PSScriptRoot "target\release\halomeister-tagmod-exporter.exe"
}
if (-not (Test-Path $exe)) {
    throw "halomeister-tagmod-exporter.exe was not found. Build native/HaloMeister.TagModExporter first."
}

$paksDir = Resolve-PaksDirectory -Explicit $Paks
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null

# Build from base + non-conflicting packs: drop previous scenario overlays
# so the merged package is not reading its own prior output.
$overlayStems = @(
    "ZZ_HM_DemoSquads_P",
    "HM_DemoSquads_P",
    "HM_FullPalettes_P",
    "MMYJ_FULL_VEHI_WAP_P",
    "MMYJ_FULL_CHAR_P",
    "MMYJ_CHAR_TROOPER_P",
    "MMYJ_CHAR_ELITE_P",
    "MMYJ_CHAR_GRUNT_P",
    "MMYJ_CHAR_JACKAL_P",
    "MMYJ_CHAR_BRUTE_P",
    "MMYJ_CHAR_HUNTER_P"
)
$backupDir = Join-Path $env:TEMP "halomeister-overlay-backup"
if (-not $DryRun) {
    if (Get-Process -Name "HaloCampaignEvolved" -ErrorAction SilentlyContinue) {
        throw "Close Halo: Campaign Evolved before rebuilding/installing the overlay."
    }
    if (Test-Path $backupDir) { Remove-Item -Recurse -Force $backupDir }
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    foreach ($stem in $overlayStems) {
        foreach ($ext in @(".utoc", ".ucas", ".pak")) {
            $path = Join-Path $paksDir ($stem + $ext)
            if (Test-Path $path) {
                Copy-Item -Force $path (Join-Path $backupDir ($stem + $ext))
            }
        }
        Remove-OverlayStem -PaksDir $paksDir -Stem $stem
    }
}

function Update-BundledFingerprint {
    param(
        [string]$ServicePath,
        [string]$ConstantName,
        [string]$UtocPath,
        [string]$BundleDir = (Join-Path $PSScriptRoot "..\..\src\HaloMeister.App\Assets\Overlays")
    )
    $fingerprintParts = @()
    New-Item -ItemType Directory -Force -Path $BundleDir | Out-Null
    foreach ($ext in @(".utoc", ".ucas", ".pak")) {
        $source = [IO.Path]::ChangeExtension($UtocPath, $ext)
        if (-not (Test-Path $source)) { throw "Missing $source" }
        $destination = Join-Path $BundleDir (Split-Path $source -Leaf)
        Copy-Item -Force $source $destination
        Write-Host "Updated bundled asset $destination"
        $bytes = [IO.File]::ReadAllBytes($destination)
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "")
        }
        finally {
            $sha.Dispose()
        }
        $fingerprintParts += "$($bytes.Length):$hash"
    }

    $fingerprint = $fingerprintParts -join "|"
    $serviceText = [IO.File]::ReadAllText($ServicePath)
    $replacement = "public const string $ConstantName =`r`n        `"$fingerprint`";"
    $updated = [regex]::Replace(
        $serviceText,
        "public const string $ConstantName\s*=\s*`"[^`"]*`";",
        { param($m) $replacement },
        1)
    if ($updated -eq $serviceText) {
        if ($serviceText -match [regex]::Escape("public const string $ConstantName") -and
            $serviceText.Contains($fingerprint)) {
            Write-Host "$ConstantName already matches; fingerprint unchanged."
            return $fingerprint
        }
        throw "Failed to rewrite $ConstantName in FullPalettesOverlayService.cs"
    }
    [IO.File]::WriteAllText($ServicePath, $updated)
    Write-Host "Updated $ConstantName in FullPalettesOverlayService.cs"
    Write-Host "Fingerprint: $fingerprint"
    return $fingerprint
}

function Get-ModVersionCode {
    param([string]$Fingerprint)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Fingerprint))
    }
    finally {
        $sha.Dispose()
    }
    return ([BitConverter]::ToString($hash[0..3])).Replace("-", "")
}

$outDir = Split-Path $Output
$splitArgs = @("--paks", $paksDir, "--expand-split", "--output", $outDir)
if ($DryRun) { $splitArgs += "--dry-run" }

try {
    & $exe @splitArgs
    if ($LASTEXITCODE -ne 0) { throw "expand-split failed with exit code $LASTEXITCODE" }

    if ($UpdateBundledAssets -and -not $DryRun) {
        $servicePath = Join-Path $PSScriptRoot `
            "..\..\src\HaloMeister.App\Services\FullPalettesOverlayService.cs"
        if (-not (Test-Path $servicePath)) {
            throw "Missing FullPalettesOverlayService.cs for fingerprint update."
        }
        $bundleRoot = Join-Path $PSScriptRoot "..\..\src\HaloMeister.App\Assets\Overlays"
        $combinedDir = Join-Path $bundleRoot "Combined"
        $fingerprints = [ordered]@{}
        $fingerprints.campaign = Update-BundledFingerprint -ServicePath $servicePath `
            -ConstantName "ExpectedBundledFingerprint" `
            -UtocPath (Join-Path $outDir "MMYJ_FULL_VEHI_WAP_P.utoc") -BundleDir $bundleRoot
        $fingerprints.characters = Update-BundledFingerprint -ServicePath $servicePath `
            -ConstantName "ExpectedCharacterFingerprint" `
            -UtocPath (Join-Path $outDir "MMYJ_FULL_CHAR_P.utoc") -BundleDir $bundleRoot
        $null = Update-BundledFingerprint -ServicePath $servicePath `
            -ConstantName "ExpectedCombinedCampaignFingerprint" `
            -UtocPath (Join-Path $outDir "combined\MMYJ_FULL_VEHI_WAP_P.utoc") -BundleDir $combinedDir
        $null = Update-BundledFingerprint -ServicePath $servicePath `
            -ConstantName "ExpectedCombinedCharacterFingerprint" `
            -UtocPath (Join-Path $outDir "combined\MMYJ_FULL_CHAR_P.utoc") -BundleDir $combinedDir
        foreach ($pair in @(
                @("char_trooper", "ExpectedTrooperFingerprint", "MMYJ_CHAR_TROOPER_P.utoc"),
                @("char_elite", "ExpectedEliteFingerprint", "MMYJ_CHAR_ELITE_P.utoc"),
                @("char_grunt", "ExpectedGruntFingerprint", "MMYJ_CHAR_GRUNT_P.utoc"),
                @("char_jackal", "ExpectedJackalFingerprint", "MMYJ_CHAR_JACKAL_P.utoc"),
                @("char_brute", "ExpectedBruteFingerprint", "MMYJ_CHAR_BRUTE_P.utoc"),
                @("char_hunter", "ExpectedHunterFingerprint", "MMYJ_CHAR_HUNTER_P.utoc")
            )) {
            $fingerprints[$pair[0]] = Update-BundledFingerprint -ServicePath $servicePath `
                -ConstantName $pair[1] `
                -UtocPath (Join-Path $outDir $pair[2]) -BundleDir $bundleRoot
        }

        $versionPath = Join-Path $PSScriptRoot "..\..\version.json"
        $version = Get-Content -Raw $versionPath | ConvertFrom-Json
        $mods = [ordered]@{}
        foreach ($key in $fingerprints.Keys) {
            $mods[$key] = Get-ModVersionCode $fingerprints[$key]
        }
        $version | Add-Member -NotePropertyName mods -NotePropertyValue $mods -Force
        $json = $version | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($versionPath, $json.TrimEnd() + "`r`n")
        Write-Host "Updated version.json mod codes."
    }

    if ($Install -and -not $DryRun) {
        $installUtocs = @(
            (Join-Path $outDir "MMYJ_FULL_VEHI_WAP_P.utoc"),
            (Join-Path $outDir "MMYJ_FULL_CHAR_P.utoc"),
            (Join-Path $outDir "MMYJ_CHAR_TROOPER_P.utoc"),
            (Join-Path $outDir "MMYJ_CHAR_ELITE_P.utoc"),
            (Join-Path $outDir "MMYJ_CHAR_GRUNT_P.utoc"),
            (Join-Path $outDir "MMYJ_CHAR_JACKAL_P.utoc"),
            (Join-Path $outDir "MMYJ_CHAR_BRUTE_P.utoc"),
            (Join-Path $outDir "MMYJ_CHAR_HUNTER_P.utoc")
        )
        foreach ($utoc in $installUtocs) {
            foreach ($ext in @(".utoc", ".ucas", ".pak")) {
                $source = [IO.Path]::ChangeExtension($utoc, $ext)
                $destination = Join-Path $paksDir (Split-Path $source -Leaf)
                if (-not (Test-Path $source)) { throw "Missing $source" }
                Copy-Item -Force $source $destination
                Write-Host "Installed $destination"
            }
        }
        Remove-OverlayStem -PaksDir $paksDir -Stem "ZZ_HM_DemoSquads_P"
        Remove-OverlayStem -PaksDir $paksDir -Stem "HM_DemoSquads_P"
        Write-Host "Validating dedicated hm_ally/hm_hostile scaffolds across all scenarios..."
        & $exe --paks $paksDir --dump-demo-squads
        if ($LASTEXITCODE -ne 0) {
            throw "Dedicated scaffold validation failed after install."
        }
        Write-Host "Restart the game so the split built-in mods mount."
    }
}
finally {
    if (-not $DryRun -and -not $Install -and (Test-Path $backupDir)) {
        Get-ChildItem $backupDir -File | ForEach-Object {
            Copy-Item -Force $_.FullName (Join-Path $paksDir $_.Name)
            Write-Host "Restored $($_.Name)"
        }
    }
}
