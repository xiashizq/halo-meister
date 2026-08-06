# UE4SS loader source

Halo Meister downloads the pinned upstream archive
`UE4SS_v3.0.1-1018-g662df915.zip` directly from the
[RE-UE4SS experimental-latest release](https://github.com/UE4SS-RE/RE-UE4SS/releases/tag/experimental-latest).
The expected archive SHA-256 is
`590AE4C6463DB61497123B9ED35373596C39FB27F736E2078A02B476599671BA`.

RE-UE4SS is distributed under the MIT License. Halo Meister does not rehost its
archive; it downloads the pinned release from the upstream GitHub repository,
verifies it, applies Campaign Evolved settings/signatures, and installs it only
after explicit confirmation.

This pinned build includes UE4SS's FName-constructor verification guard. Halo
Meister also raises the scanner timeout to 90 seconds for Campaign Evolved's
variable startup time.
