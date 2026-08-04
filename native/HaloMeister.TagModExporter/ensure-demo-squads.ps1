# Compatibility wrapper: demo squads are now built into MMYJ_FULL_VEHI_WAP_P
# together with Full Palettes, so there is only one _P overlay and no mount-order fight.
param(
    [string]$Paks,
    [switch]$DryRun,
    [switch]$Install
)

$ErrorActionPreference = "Stop"
Write-Host "Demo squads are merged into MMYJ_FULL_VEHI_WAP_P via expand-palettes.ps1."
Write-Host "Forwarding to expand-palettes.ps1 (palettes + hm_ally/hm_hostile)."

$forward = @{
    DryRun = $DryRun
    Install = $Install
}
if ($Paks) { $forward.Paks = $Paks }

& (Join-Path $PSScriptRoot "expand-palettes.ps1") @forward
exit $LASTEXITCODE
