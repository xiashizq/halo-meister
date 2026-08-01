# Game update playbook

Halo Meister treats every `HaloSimulation_tag_release.dll` update as an unknown
build until its fingerprint and memory anchors have been verified. Unknown builds
are read/write blocked; do not bypass that guard by changing only the timestamp.

## Current supported build

The 2026-07-29 WinGDK update is catalogued as `2026-07-29-wingdk`:

- SHA-256: `6F34B317BB5CDDE87A1A0DB4D5CAFADC78C3C2C9EC6658819FAE11D9F666C595`
- PE timestamp: `0x6A641022`
- image size: `0x02CE1000`
- runtime tag-table pointer: `0x0182D1E8`
- segmented tag arena table: `0x02C2CC90`

The supplied community mappings for the pulse hook, pool/heap globals, tag and
segment tables, and string-ID globals are retained in
`Assets/GameBuildProfiles.json`. Halo Meister currently consumes the tag-table and
arena-table values; the other mappings are research anchors for later features.
The complete multiplayer, Survival, Sandbox, Megalo, cinematic, object, tag, and
allocator old-to-new migration table—including provisional entries and their
confidence labels—is preserved in
`docs/address-migrations/2026-07-29-wingdk.md` and is parsed by the analyzer.
Generation also emits `generated_research_hooks.h`, a namespaced C++ catalog
which future native features can consume after validating the relevant ABI.

Independent static analysis relocated every native function used by the bridge.
Most were unique wildcard-signature matches. Common prologues were selected by
proximity to the previous verified RVA, and their complete current prologues are
emitted into the generated native header. Cheat globals are not delta-adjusted:
the update reordered their table, so the analyzer finds each ASCII name, locates
the sole pointer to it in a type-5 registration record, and derives the writable
value at record `+0x10`.

`scenarioRootPointer` is `0x10C3558` in the additional exact migration table and
remains protected by runtime scenario-layout checks. It must still be exercised
in a loaded mission before boundary overrides are considered live validated.

## After the next game update

1. Keep the game closed and retain the previous profile/catalog entry.
2. Run the read-only analyzer:

   ```powershell
   python tools\game_build_analyzer.py `
     --dll "...\HaloSimulation_tag_release.dll" `
     --base current `
     --report ".analysis\game-build-report.json"
   ```

   Its report includes the prior migration catalog as named relocation seeds, so
   future updates can compare each multiplayer and Survival hook without relying
   on chat history.

3. Review the report:

   - record the new SHA-256, PE timestamp, and image size;
   - require a unique match for narrow function signatures;
   - inspect every ambiguous match and never accept proximity alone;
   - recover cheat registrations semantically, not by applying a section delta;
   - independently recover or obtain the tag-table, arena-table, TLS, scenario,
     and other data anchors because data subsections can move by different amounts.

4. Add a new entry to
   `src/HaloMeister.App/Assets/GameBuildProfiles.json`. Never overwrite an older
   entry: profiles are useful for users who have not received the update yet.
5. Once the installed DLL exactly matches the new catalog entry, regenerate the
   native constants and prologue guards:

   ```powershell
   python tools\game_build_analyzer.py --generate current
   ```

6. Build the native bridge, then the solution:

   ```powershell
   cmd /c native\HaloMeister.BlamBridge\build.cmd
   dotnet build HaloMeister.sln -c Release -p:Platform=x64
   ```

7. Perform live validation in an offline campaign mission, from least invasive to
   most invasive:

   - attach and enumerate runtime tags;
   - read tag fields and resolve segmented references;
   - read cheats, skulls, soft ceilings, and player position;
   - apply and restore one reversible tag-reference edit;
   - spawn and delete a harmless object;
   - test weapon pickup, biped possession, AI placement, boundaries, and saved
     films separately.

8. Record the exact build fingerprint, analyzer report, build results, and live
   outcomes. A successful compile is static validation, not proof that every ABI
   and structure layout is unchanged.

## Design rules

- The JSON catalog is the source of truth for managed runtime-tag addresses and
  for research anchors.
- `generated_game_build.h` is generated input for the native bridge.
- The managed app requires an exact SHA-256, timestamp, and image-size match.
- The native bridge additionally validates operation-specific machine-code
  prologues before calling or hooking a function.
- No unknown build may write game memory.
- No global address is accepted solely because a neighboring global moved by the
  same delta.
