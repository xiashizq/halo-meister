# UE4SS loader source

Halo Meister ships the pinned upstream archive as
`UE4SS_v3.0.1_1018_g662df915.zip` under `Assets/UE4SSLoader` so live-tools
setup works offline (including networks that cannot reach GitHub). The
underscored filename is the same bytes as upstream
`UE4SS_v3.0.1-1018-g662df915.zip` (renamed only to avoid WinAppSDK PRI
qualifier parsing).

If the bundled archive is missing or fails SHA-256 verification, Halo Meister
falls back to downloading the same pinned build from the
[RE-UE4SS experimental-latest release](https://github.com/UE4SS-RE/RE-UE4SS/releases/tag/experimental-latest)
(`UE4SS_v3.0.1-1018-g662df915.zip`) and shows download progress in the UI.
A verified copy is cached under `%LOCALAPPDATA%\HaloMeister\Downloads`.

The expected archive SHA-256 is
`590AE4C6463DB61497123B9ED35373596C39FB27F736E2078A02B476599671BA`.
Halo Meister verifies that checksum before installing, applies Campaign
Evolved settings/signatures, and installs only after explicit confirmation.

RE-UE4SS is distributed under the MIT License.

This pinned build includes UE4SS's FName-constructor verification guard. Halo
Meister also raises the scanner timeout to 90 seconds for Campaign Evolved's
variable startup time.
