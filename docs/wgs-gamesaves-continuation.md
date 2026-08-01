# WGS game saves — implementation and Claude handoff

This is the canonical research handoff for continuing Campaign Evolved checkpoint work.

## Product status (2026-07-29)

Structured checkpoint editing has been removed from the Halo Meister Game saves UI.
Although the codec and individual field mutations passed offline structural and byte-level
verification, in-game acceptance was not reliable. In particular, a class-only
Assault-Rifle-to-Fuel-Rod weapon edit crashed during resume; the game later regenerated the
checkpoint and restored the original Assault Rifle class. Until native simulation identity
and cloud reconciliation are understood well enough for controlled acceptance tests, the
product surface is limited to read-only metadata, full WGS snapshots, portable archives,
and guarded checkpoint restore.

The codec, editor services, CLI commands, offsets, validation results, and the full
historical implementation notes below are intentionally retained for a future revisit.
Treat all editing sections as research history, not a description of the current UI. Do
not re-enable field editing solely because an edited stream parses or recompresses
successfully; require repeatable in-game resume validation and recovery tests first.

## Confirmed storage and sync model

```text
Package family: Microsoft.198377053870B_8wekyb3d8bbwe
Title ID:       7C27BAE7
Launch URI:     ms-xbl-7c27bae7:

%LOCALAPPDATA%\Packages\Microsoft.198377053870B_8wekyb3d8bbwe\
  SystemAppData\wgs\<account-and-title>\<container-guid>\
    container.<revision>
    <opaque-guid>          # WGS stream named Data
```

Do not rename any of those files or directories. Halo Meister is unpackaged and cannot
impersonate the title to call its private `XGameSave` cloud API. The supported sync flow
is: close game, full WGS backup, atomically replace only the existing Data stream, launch
the game, and let Gaming Services reconcile it. Cloud-conflict UI must remain a user
choice.

## Save kinds

- Full checkpoint/resume streams begin `HALOCEVO` and contain a compressed GVAS plus the
  native Blam simulation.
- Local progression begins directly with `GVAS` and identifies
  `/Script/BlamEngine.BlamProgressLocalPlayerSaveGame`.
- Saved films under `%LOCALAPPDATA%\Meteorite\Saved\BlamData` are separate.

The checkpoint metadata identifies `/Script/BlamEngine.BlamSaveSlotSaveGame`. Its
`BlamSaveGame` ObjectProperty is `/Script/BlamEngine.BlamDataSaveGame`, whose large native
payload contains the Blam object state. A generic GVAS parser alone cannot decode that
custom native serialization.

## Decoded property dialect

Campaign Evolved does **not** use stock Unreal property serialization. The layout is:

```text
Property = FString Name
           TypeDescriptor Type
           int32  Size
           uint8  Flags
           byte[Size] Value

TypeDescriptor = FString Name
                 int32 ParameterCount
                 TypeDescriptor[ParameterCount]
```

A property list is terminated by the bare name `None`. Notes:

- The root list starts one byte after the save class name.
- `ObjectProperty` has two encodings: an inline subobject (`int32 1`, class path
  FString, one marker byte, nested list) or a plain reference (just a path FString).
- Container values are followed by a remainder that must be preserved verbatim: four
  bytes for an inline object, several hundred for `BlamScenarioGameOptions`.
- `BoolProperty` has `Size` 0 and carries its state in `Flags`: `0x10` set, `0x00` clear.
- `SoftObjectProperty` is asset path FString, asset name FString, trailing int32.

`BlamSaveDocument` implements this and round-trips all sample checkpoints byte-for-byte
(10-, 57- and 268-actor saves). Only three top-level objects exist: `MetaData`,
`BlamSaveGame` (the opaque native simulation) and `UnrealWorldSaveGame`.

## Object identity lives in the actor table

`UnrealWorldSaveGame.ActorState.SavedActors` is an int32 count followed by records:

```text
Record = PropertyList     # Class, BlamObjectGameStateIdentifier, Components
         'None'
         int32 ActorDataSize
         byte[ActorDataSize]
```

This is the **only** place object identity appears as readable data. The 12.6 MB native
blob contains no weapon strings at all, so the Blam side must refer to objects by an
integer. `BlamObjectGameStateIdentifier` (int16) is the shared key.

Weapon actors carry a blueprint class path and a 14-byte data block; all weapon state
lives on the native side. Array position tracks inventory order: a controlled before/after
pair differing only by an equipped-weapon change showed gsid 7 (Magnum) and gsid 68
(Assault Rifle) swapping array slots #2 and #3 with no other actor change.

**Still unmapped:** the native weapon definition reference. Aligned field comparison across
three Assault Rifle objects versus a Magnum found no constant-differing dword within
±0x600 of the ammo record, and no adjacent int16/int32 reference pair swapped between the
before/after checkpoints. Until that field is located, changing an actor's blueprint class
alone would desync the simulation. Do not ship weapon type replacement before it is mapped.

## HALOCEVO codec — confirmed

The format is Oodle Kraken with independently compressed chunks:

```text
0x00  "HALOCEVO"
0x30  repeated 16-byte descriptors
       +0x01 uint24 LE compressed size
       +0x09 uint24 LE uncompressed size (normally 0x20000 / 128 KiB)
after table: observed one prefix byte, then concatenated Oodle streams
```

The codec parameters are:

```text
OodleLZ_Compressor_Kraken = 8
OodleLZ_CompressionLevel_Fast = 3
```

This was proven with a locally installed licensed `oo2core_8_win64.dll`:

- every observed chunk decompresses to its descriptor's expected size;
- the combined payload begins `GVAS`;
- 97-, 99-, and 102-chunk live saves re-encode byte-for-byte with no changes;
- modified payloads re-encode, decode again, and reproduce the intended payload exactly.

Halo Meister does **not** redistribute Oodle. The UI asks the user to select an Oodle 2.8
DLL from software they are licensed to use and checks for both required exports. Do not
silently bundle a proprietary DLL. Do not replace it with GPL code without a deliberate
licensing decision.

Codec implementation:

```text
src/HaloMeister.Core/HaloCevoCheckpoint.cs
```

`HaloCevoCheckpoint.Decode` validates the table and every decompressed chunk.
`Encode` uses Kraken/Fast, rewrites each descriptor's compressed uint24, immediately
decompresses every generated chunk, and rejects any mismatch. A final whole-payload
decode comparison is also performed by the UI before WGS replacement.

## Confirmed player ammunition schema

UE4SS reflection exposes:

```text
BlamUnitInventoryComponent:GetWeapon(EBlamUnitWeaponIndex)
  0 Primary
  1 Secondary
  2 Backpack
  3 BackpackOther

BlamWeaponComponent:GetMagazineCount()
BlamWeaponMagazine:
  +0x00 int32 RoundsInventory
  +0x04 int32 RoundsInventoryMaximum
  +0x08 int32 RoundsLoaded
  +0x0C int32 RoundsLoadedMaximum
```

`GetMagazine` is awkward through this UE4SS build because it requires generated out
parameters. The live capture instead gets each `BlamWeaponComponent` address through Lua,
then reads its synchronized magazine array from the game process:

```text
BlamWeaponComponent + 0x3C0  pointer to 24-byte magazine structs
BlamWeaponComponent + 0x3C8  int32 magazine count
```

This synchronized array is read-only for our purposes. A test write was overwritten by
the native simulation within 250 ms, so do not pretend that patching the UE cache changes
the save.

In the decompressed native checkpoint, each observed weapon datum stores:

```text
ammoOffset + 0x00  int32 reserve / rounds inventory
ammoOffset + 0x04  int32 rounds loaded
```

The guarded framing used by `FindAmmoStates` is:

```text
ammoOffset - 4: FF FF 00 00
ammoOffset + 8: 00 00 FF FF FF FF FF FF
```

Controlled A30 validation:

```text
Primary/equipped Magnum: reserve 108, loaded 6  -> unique payload offset 0x7CE3AB
Backpack Assault Rifle: reserve 324, loaded 35 -> unique payload offset 0x7D4CEB
```

Offsets are **not** hard-coded; they change with the checkpoint. The UI resolves each
captured weapon by its original reserve/loaded pair plus framing and requires exactly one
match. Zero or multiple matches abort without replacing WGS. This deliberately means two
player weapons with identical ammo pairs may be rejected until stronger object identity
mapping is implemented.

## Implemented UI workflow

Files:

```text
src/HaloMeister.App/Pages/GameSavesPage.xaml
src/HaloMeister.App/Pages/GameSavesPage.xaml.cs
src/HaloMeister.App/Services/LiveGameSaveEditorService.cs
src/HaloMeister.App/Services/WgsGameSaveStore.cs
```

User flow:

1. Select a checkpoint slot and resume that checkpoint in the game.
2. In Game saves, select a licensed Oodle 2.8 DLL once.
3. Choose **Capture live loadout**. The UE4SS bridge finds the controlled player unit,
   reads Primary/Secondary/Backpack weapon names and magazine values, and checks that the
   live scenario matches the selected save.
4. Close Campaign Evolved and refresh the page.
5. Edit Loaded and Reserve values, bounded by the reflected weapon maxima.
6. Choose **Back up and apply**.
7. Halo Meister reloads the current Data bytes, requires unique native records, patches
   only the two int32 values per changed weapon, recompresses/verifies all chunks, backs
   up the complete WGS title tree, and atomically replaces the selected Data stream.
8. Launch the game and allow Gaming Services to sync.

The loadout capture is intentionally tied to the selected WGS container. Switching slots
invalidates apply eligibility. Writes remain disabled while the game is running.

## Storage safeguards

`WgsGameSaveStore`:

- discovers account/container directories rather than hard-coding them;
- backs up the complete WGS tree under
  `%LOCALAPPDATA%\HaloMeister\GameSaveBackups`;
- exports `.halo-wgs` archives with Data, container metadata, and manifest;
- validates checkpoint magic;
- refuses all replacement while `HaloCampaignEvolved.exe` runs;
- writes through a same-directory temporary file and atomically moves it over Data;
- preserves container identity and metadata.

`ReplaceSlotData` is the structured-editor entry point. Do not weaken the close-game,
backup, validation, or atomic-write checks.

## CLI diagnostics

```powershell
halomeister gamesave-info <data-file>
halomeister gamesaves-scan <wgs-root>

# A no-change save must be byte-identical.
halomeister gamesave-codec-verify <data-file> <oo2core_8_win64.dll>

# Structure.
halomeister gamesave-payload <save> <oodle.dll> <out.bin>
halomeister gamesave-tree    <save> <oodle.dll> [depth]
halomeister gamesave-actors  <save> <oodle.dll> [filter]
halomeister gamesave-diff    <a> <b> <oodle.dll>

# Ammunition. ammo-list needs no live capture and no running game.
halomeister gamesave-ammo-list <save> <oodle.dll>
halomeister gamesave-ammo-at   <in> <out> <oodle.dll> <hex-offset> <reserve> <loaded>
halomeister gamesave-ammo-find <data-file> <oodle.dll> <reserve> <loaded>
halomeister gamesave-ammo-set  <in> <out> <oodle.dll> `
  <old-reserve> <old-loaded> <new-reserve> <new-loaded>

# Campaign settings. Changing difficulty resizes the payload.
halomeister gamesave-settings <save> <oodle.dll>
halomeister gamesave-set <in> <out> <oodle.dll> `
  <difficulty|insertion|scenario|laso|friendlyfire> <value>
```

`gamesave-tree` and `gamesave-actors` both assert a byte-exact re-serialization and exit
non-zero if it fails, so they double as regression tests for the parser.

## Wrapper header — must stay consistent

The 16 bytes at `0x20` describe the whole stream using the same shape as a chunk
descriptor: uint24 total compressed at `+1`, uint24 total uncompressed at `+9`. Offset
`0x0C` repeats the total uncompressed size as a uint32. `0x10` is the constant magic
`c1 83 2a 9e` and `0x14` is `22 22 22 22`; neither is a checksum, and no payload checksum
exists anywhere in the wrapper.

`Encode` previously rewrote only the per-chunk compressed sizes, so any edit that changed
the compressed length left `0x21` stale — reproduced at 1 and 2 bytes off. It now rewrites
all totals and the per-chunk uncompressed sizes.

Payload length may change. `ReplacePayload` keeps the chunk count fixed, because the
descriptor table is part of the wrapper prefix and the compressed data must start where
the table says. Every leading chunk keeps its original size and the final chunk absorbs
the difference, which bounds a single edit to +/- 128 KiB.

Build and regression:

```powershell
dotnet build HaloMeister.sln -c Release -p:Platform=x64
dotnet run --project src\HaloMeister.Cli -c Release --no-build -- `
  verify samples\sample-save.json
```

## Known limitations and next work

- Loaded/reserve ammunition is the first verified field-level checkpoint edit.
- Ammunition, campaign difficulty, insertion point, scenario index, LASO and friendly fire
  are editable from the CLI without the game running. The UI still requires a live capture
  and has not been rewired onto `BlamSaveDocument` yet.
- The UI displays which weapon is Primary/equipped and which is Backpack, but does not
  yet replace a weapon definition or change the equipped inventory reference. Those need
  object-identity mapping in the native Blam unit inventory; do not fake this by changing
  UE actor classes or tag indices independently. The blueprint class is now trivially
  rewritable via `BlamActorRecord.SetClass`, which makes it *easier* to fake — the native
  definition field must be mapped first.
- Next step for weapon switching: with the game at a checkpoint, read the native Blam
  weapon object through the UE4SS bridge and `ReadProcessMemory`, recover its definition
  identifier, then find that value in the decoded payload near the known player weapon
  offsets (A30: `0x7CE3AB` Magnum, `0x7D4CEB` Assault Rifle — stable across checkpoints of
  the same scenario).
- Battery/heat weapons need a separately validated native representation.
- Two weapons with identical original reserve/loaded pairs are safely rejected as
  ambiguous.
- Grenades, health, shields, position, and checkpoint metadata still need controlled
  before/after mapping.
- The current live reader depends on UE4SS and the existing Halo Meister scripting
  bridge. The game must be at a resumed campaign checkpoint.
- The component offsets were validated for build `++Meteorite+Rel-i343--2606-CU2`.
  Add a build/version gate or signature-based reflection before claiming compatibility
  with later game updates.
- A controlled in-game acceptance test of a modified expendable slot is still required.
  Retain the automatically created out-of-tree backup and verify local/cloud conflict
  behavior manually.
- There is no cloud-sync status API; the launch button only hands control to Gaming
  Services.

Recommended next schema work: capture two checkpoints differing by exactly one weapon
switch, then correlate the player unit's weapon datum references to the already located
weapon records. Only after a repeatable mapping across multiple saves should the UI add a
weapon replacement/equip control.
# 2026-07-28 offline editor and experimental weapon swapping addendum

The implementation has moved beyond the original capture-gated workflow described later
in this research log:

- **Load checkpoint** now decodes campaign settings, the saved actor table, and all framed
  magazine records directly from the selected WGS data file. Reading works with the game
  open; only replacement remains gated on the game being closed.
- Native magazine records are joined to saved weapon actors through the game-state id at
  `ammoOffset - 240`, so ammo rows have offline weapon names without a live capture.
- The live capture is optional. When its ammo pair resolves uniquely, it marks the exact
  saved actor as Primary/Secondary/Backpack in the weapon editor.
- A catalog of 22 player-usable weapon blueprint classes was resolved against the running
  game. The app and CLI can repoint a saved weapon actor's `Class` soft-object property.
- This class-only swap is deliberately marked **experimental**: the opaque native
  simulation record retains the original weapon-specific layout and size. The rebuilt
  checkpoint is structurally valid and codec-verified, but each weapon pairing still needs
  an in-game resume test. A complete WGS backup is mandatory before replacement.
- CLI validation on an A30 checkpoint changed gsid 7 from Magnum to Rocket Launcher,
  parsed the new class correctly, passed all 99 Kraken chunk checks, then changed it back
  to Magnum and recovered the original file byte-for-byte.

## 2026-07-28 runtime-tag correlation, equipped weapon, and vitality

The runtime tag table and native WGS simulation records now have a direct, repeatable join:

- Each native weapon record contains the same `weap` reference datum shown by the live tag
  editor at `ammoOffset - 698`.
- The native record-size field is at `ammoOffset - 714`. Assault Rifle, Magnum, and Needler
  records are different sizes, proving that a datum-only cross-type replacement is unsafe.
- The first two weapon actors are the player's carried pair. Controlled checkpoints show
  that the later record is equipped; swapping the two complete actor records changes that
  ordering without mixing their type-specific native data. The app and
  `gamesave-equip` expose this with an experimental warning.

The player Spartan biped is game-state id 1 and uses runtime `bipd` datum `0xFBB2195C` in
the tested build. Its guarded native record stores body vitality at `bipd + 0x80` and
shield vitality at `bipd + 0x84`. The same two-field layout was checked across every
saved Marine, Grunt, Elite, and Spartan record in the A30 sample. The app exposes both as
0–100 percent fields; the core refuses ambiguous records, changed guards, non-finite
values, or values outside the normal 0–1 range.

CLI verification changed a 100%/100% checkpoint to 75%/25%, decoded the rebuilt checkpoint,
confirmed both exact floats, restored 100%/100%, and recovered the original WGS stream
byte-for-byte.

Weapon damage is not a per-player WGS field. It is defined through the live weapon,
projectile, and damage-effect tag graph, so damage tuning belongs in the realtime tag
editor/preset system rather than this checkpoint editor.
