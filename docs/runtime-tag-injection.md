# Campaign Evolved runtime tag injection

This documents the live layout for the profiled July 29, 2026
`HaloSimulation_tag_release.dll` build and the implementation behind Halo Meister's
**Realtime tags** page.

## Confirmed runtime layout

All static addresses below are offsets from the loaded
`HaloSimulation_tag_release.dll` base, not preferred-base virtual addresses.

- Tag-table pointer: `+0x182D1E8`
- Segmented-offset arena bases: `+0x2C2CC90`
- Tag-table header:
  - entry size: `+0x20` (currently `0x28`)
  - first entry: `+0x50`
  - end pointer: `+0x58`
- Tag entry:
  - datum/index value: `+0x00`
  - reversed four-CC bytes: `+0x04`
  - direct UTF-8 name pointer: `+0x10`
  - root block count: `+0x18`
  - root data offset: `+0x1C`
  - block-definition offset: `+0x20`

A 32-bit tag offset is resolved as:

```text
arena_index = encoded >> 28
word_offset = encoded & 0x0fffffff
address = arena_bases[arena_index] + word_offset * 4
```

The `+0x2C2CCC0` segment-table research anchor points 0x30 bytes into the
surrounding data. The profiled `+0x2C2CC90` value is the start of the arena-base array that
resolves actual tag roots.

These addresses are no longer compiled into `RuntimeTagMemoryService`. The
service fingerprints the DLL and reads them from
`Assets/GameBuildProfiles.json`; unknown builds are rejected before memory is
opened for writes. See `game-update-playbook.md`.

## Schema layout

Halo Meister loads Campaign Evolved's Baboon definitions from:

```text
G:\Mods\baboon-windows-x86_64\definitions\haloce_evolved
```

The definitions omit explicit offsets, so offsets are calculated from serialized
field sizes. An audit against every declared structure size produced an exact
match for all 1,954 structures. Important modern Reach-layout sizes include:

- tag block: 12 bytes
- tag reference: 16 bytes
- tag data: 20 bytes
- tag resource: 8 bytes
- tag interop: 12 bytes

Inline structs and fixed arrays use their declared definition size.

Tag schemas also inherit their parent definitions. For example, `weap` inherits
`item`, which inherits `obje`. The runtime loader merges the complete parent
chain before calculating fields; loading `weapon.json` in isolation leaves its
first `item_struct_definition` unresolved and produces an empty editor.

## Runtime tag references

A 16-byte runtime tag reference contains:

- `+0x00`: reversed four-CC bytes;
- `+0x04`: segmented offset to the target tag name;
- `+0x08`: UTF-8 name length;
- `+0x0C`: datum, with the tag-entry salt in the high 16 bits and table slot in
  the low 16 bits.

Halo Meister creates a replacement reference from the selected live tag entry,
including inverse-mapping its direct name pointer into a segmented offset. The
builder was validated byte-for-byte against the running Magnum's
`magnum_bullet` projectile reference:

```text
6A6F7270 C0233F11 37000000 3206A7E7
```

## Current safety boundary

Implemented:

- guarded process attach and module-relative layout validation;
- full live tag enumeration, path/four-CC search, and segmented-offset resolution;
- Baboon-schema root-field decoding;
- inherited schema merging and nested block element traversal;
- collapsible runtime-path folder browsing and expanded matching branches;
- bounded deep-field indexing with nested breadcrumb search and quick filters;
- compatible tag-reference search and swapping (for example projectile fields);
- scalar, string, vector, color, enum, and flags writes;
- exact-length raw root-data writes;
- immediate read-back verification after every write;
- portable `.hmtagmod` export for schema-field edits, using tag identities and
  nested block traversal rather than process addresses;
- transactional `.hmtagmod` loading against the current mission, including
  semantic tag-reference rebuilding and rollback of earlier writes on failure;
- native `_P.utoc/.ucas/.pak` overlay generation through Baboon's pinned
  `blam-tags` engine. The exporter reopens the original cooked tag, applies
  patches through the structured `TagFile` field/block API, round-trip verifies
  serialization, and delegates IoStore writing to `write_mod_container_ex`;
- guarded overlay installation into `Meteorite/Content/Paks`, requiring the
  game to be closed, a complete triplet, an `_P` suffix, and unused destination
  filenames;
- build-locked native creation, engine-owned player pickup, and carried-weapon
  model-variant changes for selected `[weap]` tags through **Weapon workshop**.
- scenario-backed AI creation for selected `[char]` tags through the dedicated
  **Spawner** page.
- full player-biped redirection through **Change Biped**, including the loaded Elite,
  Grunt, Flood, Marine, Hunter, Jackal, Spartan, and other `[bipd]` tags. The tool updates
  the globals/scenario player representation's `third person unit` reference and
  `third person variant` string ID as one verified transaction, with same-session
  restoration. It deliberately does not rewrite the original player tag-table entry.
- live Master Chief armor selection through **Customization**. The tool resolves the
  selected cosmetic to the corresponding loaded Spartan `[hlmt]` model-variant block,
  copies that block's name `string_id` into the player representation, and applies it
  immediately to the controlled player through the engine's `object_set_variant`
  function. It supports same-session restoration and does not require or fabricate a
  player `[char]`.
- profile-specific armor and weapon auto-application through **Customization**. Selecting
  a choice records a Halo Meister runtime preference and applies it without an extra
  button. The bridge resolves matching carried weapons through the controlled
  `BlamUnitInventoryComponent`, obtains each weapon's native object datum, and invokes
  `object_set_variant`; unavailable configured weapons are retried when they appear.
- per-region Spartan appearance composition through the standalone **Armor mixer**.
  The live shared Spartan `[hlmt]` currently exposes 33 variants with the same three
  region `string_id` values. Region 1 is Body and region 2 is Helmet. Region 3's null-name
  permutation record is byte-identical across all 33 variants, so the UI filters it out as
  invariant. The mixer matches the remaining region IDs, temporarily substitutes each
  selected donor's 12-byte nested permutation-block descriptor in a chosen foundation
  variant, invokes `object_set_variant` for the controlled player, and restores the original
  descriptors with read-back verification. It does not resize tag blocks,
  retain redirected pointers, or cross skeletons.
- cross-species head permutations are not compatible merely because multiple species use
  the same Helmet region `string_id`. Spartan, Elite, Grunt, Brute, and Jackal `[hlmt]`
  tags reference distinct skeleton/render resources. The runtime variant API selects only
  geometry already authored for the current model, so a foreign head requires an offline
  mesh/skin retarget into the destination model.
- live MCP validation held foreign `[hlmt]` and `[skel]` descriptors in place while manually
  rebuilding the player, but the renderer still selected Spartan geometry. Armor Mixer no
  longer exposes cross-species donors or runtime colors. Foreign heads require an
  offline-authored mesh/skeleton retarget, and the sole authored Spartan-knife child remains
  unavailable because runtime `object_set_variant` calls do not instantiate it.
- experimental creation and engine-owned bump possession of any selected loaded `[bipd]`
  through **Change Biped**, with an explicit action to turn bump possession back off.
- live read/write control for all 15 registered boolean `cheat_*` globals through
  **Cheat Globals**, including a one-click action to clear every enabled value.

Not implemented:

- directly replacing an existing weapon's tag definition or root descriptor;
- adding or deleting block elements;
- resizing tag data;
- allocating new arena memory;
- changing block pointers or tag references through the schema editor;
- persistence after the game unloads/reloads the tag.

Those structural operations need the game's tag allocator and lifetime rules. Do
not emulate them with `VirtualAllocEx`: the engine owns and may relocate or free
its tag arenas.

## Experimental native object creation

Selected `[weap]` tags can be spawned through the optional native Blam bridge. The
bridge is invoked by UE4SS Lua inside `ExecuteInGameThread`, but that Unreal thread
does not own a Blam simulation context. Bridge v7 therefore queues the request and
uses a guarded trampoline on the simulation-context getter to claim it only from a
thread with a valid Blam world. On that thread it initializes the game's 0x330-byte
object-placement structure, writes a point 150 world units ahead of the local pawn,
and calls the native object allocator. Lua polls the native result asynchronously.
A successful result contains the created 32-bit object datum, rather than merely
confirming that a console string was submitted.
The request uses the selected tag's complete salted runtime datum, composed from
the live table entry salt and 16-bit slot; a bare table slot is not a valid
placement tag datum.

This path is deliberately restricted:

- the packaged DLL supports only the exact simulation DLL with SHA-256
  `6F34B317BB5CDDE87A1A0DB4D5CAFADC78C3C2C9EC6658819FAE11D9F666C595`;
- it validates the PE timestamp, image size, and both native function prologues;
- only selected `[weap]` live-table slots can reach the action in the UI;
- weapon loading uses the controlled player's reflected native
  `BlamObjectIndex`, then the engine's normal pickup route;
- if pickup is rejected, the temporary object is deleted through the verified
  object lifecycle function.

Use **Install / repair bridge** after updating Halo Meister, restart the game, load
an offline campaign mission, select a weapon tag, acknowledge the warning, and
choose **Pick up and equip** in **Weapon workshop**.

The workshop follows the selected `[weap]` tag's schema-defined `model` reference
to its loaded `[hlmt]`, enumerates every authored `model_variant_block`, and sends
the chosen variant's `string_id` through the build-verified `object_set_variant`
route. The bridge locates the matching weapon in the controlled player's reflected
inventory and applies the variant to that weapon's native object datum. The weapon
must already be carried, and the change lasts only for the current game session.

Directly repointing an existing equipped weapon remains deliberately unsupported.
That leaves the old object's type-specific native state attached to a different
definition and was confirmed to crash the game.

## Guarded Stanchion import

The installed IoStore contains the first-party Blam weapon data asset at
`/Game/Tags/objects/Weapons/Rifle/sniper_rifle/stanchion-weapon`, even in missions
that do not publish its `[weap]` entry. **Weapon loader > Import Stanchion** asks
UE4SS to load that exact asset on the game thread and keeps the returned
`BlamWeaponTagDataAsset` rooted for the rest of the mission. Bridge v14 performs
this operation; arbitrary asset paths and non-weapon tag assets are rejected.

After loading, Halo Meister waits for the game's cooked-tag subsystem to publish a
live Stanchion entry. If it does not appear, the operation stops without writing
the runtime table. Halo Meister does not fabricate an entry, copy arena pointers
from another mission, or inject an unrelocated binary blob.

When the entry appears, the importer recursively checks its non-null tag references
against the salted datums in the current runtime table. An unresolved Stanchion
field is paired with the same schema-relative field on the mission's loaded sniper
rifle. Live testing found four references without a currently published target:
melee response, pickup sound, zoom-in sound, and zoom-out sound. Melee and both zoom
references were already byte-for-byte identical to the working sniper rifle and are
accepted as covered by that template. Only Stanchion's pickup sound differed, so the
importer offers the sniper rifle's complete 16-byte pickup-sound reference as the
single substitution. Same-field comparison is important because inherited Baboon
allowed-group metadata for these `[jpt!]` and `[snd!]` fields is not emitted as usable
fourCC values.

For other unmatched fields, only a group-compatible, unambiguous live sniper target
is offered. The UI previews every missing reference and blocks the apply action if
even one has no safe match. Applying is transactional: both source and destination
bytes are rechecked, writes are read-back verified, the complete Stanchion is
rescanned, and all writes are rolled back if verification fails.

The first live pickup attempt still produced a clean native rejection after object
creation. A root-field comparison identified the exact cause: Stanchion weapon flags
were `0x01400400`, while the working sniper rifle used `0x01000400`. The differing
`0x00400000` bit is weapon flag 22, named **cannot be used by player** in the Baboon
schema. The importer now previews a player-eligibility fix and clears only that bit;
it does not copy the sniper's other flags. The write participates in the same
transaction and rollback verification as tag-reference substitutions.

Live verification after applying the fix succeeded: the engine created Stanchion
object datum `0xE3D70050`, accepted it through the normal unit-add-weapon path, and
reported it equipped. The game remained running and responsive.

## Deferred AI placement and vehicle spawning

The retail `ai_place` routine queues actor creation. Spawner therefore retains its
transactional scenario character reference, position, and actor-variant substitution
for 1.5 seconds after submission, then restores all three fields on a later eligible
simulation callback. Restoring them immediately could let deferred creation observe the
original template rather than the requested enemy.

The character detail pane also provides **Spawn team (5 AI)**. It requires any scenario
squad with at least five usable spawn-point records; it does not require that the authored
squad already be hostile. Spawner selects the nearest qualifying squad, temporarily
changes its team to Covenant (`global_campaign_team_enum` value `3`), patches five
character references, positions, and actor variants into a compact formation centered
ahead of the player, then calls `ai_place` once with a count of five. The native bridge
snapshots the original team and every touched placement field and restores them after
1.5 seconds. Placement snapshots are restored in reverse order because several points
can inherit the same character-palette reference.

Spawner also lists loaded `[vehi]` tags. Vehicles bypass the scenario AI path and use
the exact-build-guarded placement initializer and object allocator. The separate
**Vehicle workshop** presents the same loaded-tag catalog and selection workflow.

Live diagnostics on bridge v33 exposed false success from that generic allocator:
it reported object datum `0x00000000` at `(150, 0, 0)` while the actual player was
at `(113.74, 291.39, 183.14)` in Blam coordinates. Bridge v34/native v16 therefore
passes the controlled player datum into native code and resolves position/orientation
through the retail `object_get_position` and `object_get_orientation` routines on the
simulation thread. Zero object datums are rejected. Spawner character selections use
the confirmed working `[char] -> unit [bipd]` path as their primary action. Bridge
v36/native v19 routes these through a dedicated `biped_body` operation which shares
Change Biped's biped validation, controlled-player transform lookup, placement
initializer, and allocator, but keeps the safer forward/vertical offsets and never
enables bump possession. It creates a physical unit without attaching AI or player
control. The non-working scenario `ai_place` action is no longer presented as the
primary spawn control.

## Experimental biped bump possession

The supported simulation binary registers the built-in `cheat_bump_possession`
boolean in its global command table. Its writable one-byte value is at module RVA
`0x9A92F0` in the exact build identified above. Halo Meister does not replace the
controlled unit datum directly. Instead, bridge v32 resolves the controlled player's
native Blam object datum through the same Unreal synchronization component used by
live Customization, enables the engine-owned possession path, and creates the selected
loaded `[bipd]` through the verified placement initializer and object allocator used
by **Weapon loader**. The engine then owns any control transfer and related unit lifecycle.

Use **Install / repair bridge**, restart the game, and load an offline mission. In
**Change Biped**, select a biped and choose **Spawn character & enable switch**. The
object is created at the controlled player's exact position so both unit capsules
overlap on the next simulation update. After control transfers, choose
**Disable character switching**.
The disable request does not depend on resolving the current Unreal pawn, so it
remains available if the newly controlled biped has incomplete Unreal integration.

This is intentionally experimental. The flag address and object-creation calls are
accepted only for the exact supported DLL, and the bridge verifies that the flag lies
in committed writable memory before changing it. A failed biped creation restores
the flag's previous value. A successful creation leaves it enabled so the collision
can occur; the user must disable it afterward. AI-authored bipeds may not provide a
player camera, HUD, animations, weapons, seats, checkpoints, or mission scripting.

## Cheat Globals

The supported simulation DLL contains 15 boolean `cheat_*` registrations in its external
global table. Live testing disproved the original layout assumption: each record has a
name pointer at `+0x00`, type value `5` at `+0x08`, and a backing-value pointer at
`+0x10`. All 15 cheat registrations have a null backing pointer in the retail build.

| Global | Null backing-pointer field RVA |
|---|---:|
| `cheat_inhibit_input_only_when_activating` | `0x9A9218` |
| `cheat_infinite_equipment_energy` | `0x9A9230` |
| `cheat_controller` | `0x9A9278` |
| `cheat_omnipotent` | `0x9A9290` |
| `cheat_porcupine` | `0x9A91E8` |
| `cheat_chevy` | `0x9A9200` |
| `cheat_super_jump` | `0x9A92D8` |
| `cheat_bump_possession` | `0x9A92F0` |
| `cheat_medusa` | `0x9A9248` |
| `cheat_reflexive_damage_effects` | `0x9A9260` |
| `cheat_jetpack` | `0x9A9338` |
| `cheat_valhalla` | `0x9A9350` |
| `cheat_bottomless_clip` | `0x9A92A8` |
| `cheat_infinite_ammo` | `0x9A92C0` |
| `cheat_deathless_player` | `0x9A9308` |

The former implementation overwrote these pointer fields with `0` or `1`, then read back
the same metadata and incorrectly reported success. Gameplay never consumed those values;
live tests with infinite ammo and bottomless clip confirmed ammunition still decreased.
The direct Unreal console route also accepted the command string without applying it.
Those registrations remain disabled. The replacement **Gameplay cheats** screen uses an
independent `HMCHEAT1` mailbox and state machine, so a pending object/spawn request cannot
block it. Infinite Health / Invulnerability maps to the live `skull_superman` modifier
(runtime bit 11) and remains explicitly experimental pending a retail damage test.
Infinite Ammo maps to Bandanna (bit 18); Jetpack / Flight maps to Acrophobia /
`skull_boots_off_the_ground` (bit 36). All are applied by the engine's skull-mask routine
from an eligible simulation callback and read back before the request succeeds.

The same screen also exposes the loaded `[matg]` default-player-trait blocks as
selectable **Player modifiers**. Damage, health/damage resistance, movement speed,
jump height, shield strength and recharge, melee damage, gravity, double jump,
vampirism, and active camouflage are written through their typed tag fields. Each
write is read back, and Halo Meister retains the original bytes so **Restore changes**
can safely put back only values that still match the tool's last write. Most traits
are consumed immediately; a respawn or checkpoint reload can be required when the
current player body already holds a copied trait value.

**Player allegiance** changes the controlled unit's actual campaign-team byte,
which is the value read by AI target selection, and mirrors the choice into the
retail `object_set_allegiance` table for script systems that consult the
object-specific override. The bridge resolves the controlled unit through the
build-verified `object_get` function and does not change the scenario's global
allegiance matrix. Choices include the Player, Human/UNSC, Covenant, Brute, Mule,
Covenant Player, Flood, Sentinel, Heretic, Prophet, Guilty, and hostile-to-all
campaign teams. Covenant, Flood, or Sentinel therefore make that faction regard
the controlled body as friendly, while relationships with other factions follow
the normal campaign allegiance table. Campaign synchronization periodically
republishes the authored Player team, so the simulation hook maintains the
selected unit-team byte and object override on eligible ticks instead of relying
on a one-shot write. Halo Meister snapshots both original values and restores
them only while the same controlled unit datum is alive. A respawn or mission
unload replaces the player object, so apply the selection again after respawning.

## Experimental enemy spawning

The **Spawner** page enumerates loaded `[char]` tags while excluding stimulus-only
character definitions. Before each spawn, it reads the player's live Blam-space
position and selects the nearest hostile squad with a concrete spawn point in the
loaded `[scnr]`. This avoids borrowing a remote encounter that `ai_place` can
silently reject. It then sends the squad index, character-palette reference address,
spawn-position address, actor-variant-name address, selected live tag reference,
and selected `[char]` variant name to bridge v32 and
`halomeister_blam_v23.dll`.

On a callback with valid campaign TLS state, the native bridge validates the
exact module build and the `ai_place` prologue at RVA `0x0FD810`. It snapshots the
scenario character reference, position, and actor variant, substitutes the selected
`[char]`, selected character-variant name, and player-relative position, requests
exactly one actor with the encoded squad index, and restores all three fields. This
avoids the silent rejection caused when a borrowed spawn point retained a variant
name that did not belong to the injected character. Access violations are converted into an error
result when possible.

UE4SS exposes the pawn transform in Unreal centimeters, while a Blam scenario
point uses simulation world units. Bridge v10 converts the player-relative point
at 100:1 before writing it. The earlier direct copy produced coordinates roughly
100 times outside the BSP and could crash during deferred actor/pathfinding
initialization. AI placement is now reported as **submitted**, after a 1.5-second
stability delay, rather than as confirmed merely because `ai_place` returned.

This is scenario-backed spawning: a loaded hostile squad supplies the team and
encounter context, while the selected character tag supplies its biped, weapons,
and behavior. If the current mission area has no usable hostile squad spawn point,
the page refuses the request rather than creating an inert `[bipd]`.
