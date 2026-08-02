# Live tools capability contract

Each real-time feature is independently declared in
`Assets/GameBuildProfiles.json`. A matching DLL fingerprint only proves that the
process layout is known; it does not automatically enable every native operation.

## Validation levels

| Level | Meaning | User-facing behavior |
| --- | --- | --- |
| `Unsupported` | No profile evidence. | Disabled. |
| `Cataloged` | Exact DLL fingerprint is known. | Read-only diagnostics only. |
| `StaticVerified` | Analyzer found and checked required static anchors. | Disabled until integration and live validation. |
| `Integrated` | Generated bridge constants and per-operation prologue checks cover it. | Available with an experimental warning. |
| `LiveValidated` | Reversible offline-campaign smoke test is recorded for this build. | Available. |

## Capability inventory

| Capability | Main consumers | Risk | Fallback |
| --- | --- | --- | --- |
| `RuntimeTags` | Realtime tags, tag-derived customizers | Medium | Offline tag mod export/overlay |
| `ObjectSpawn` / `WeaponLoad` | Weapon workshop | High | None; disable only this action |
| `ObjectAppearance` | Customization, armor mixer | Medium | Persist preference; apply on a validated build |
| `BipedPossession` | Change Biped | High | Runtime tag representation redirect |
| `AiPlacement` | Spawner | Very high | None; preserve scenario state and disable |
| `GameplayCheats` | Cheat Globals, Skulls | Medium | Read-only state |
| `SoftCeilings` / `RuntimeBoundaries` | Player tools, boundaries | High | Disable overrides and retain restore data |
| `PlayerTools` / `PlayerAllegiance` | Player Tools | High | Read-only position where available |
| `Machinima` | Advanced Machinima | Medium | UE4SS-only controls |
| `SavedFilm` | Film tools | Very high | Archive/validate only; do not attempt playback |

## Promotion checklist

1. Add the exact SHA-256, PE timestamp, and image size to the profile catalog.
2. Use the analyzer report to prove every required function signature is unique,
   or document a reviewed disambiguation.
3. Generate the native constants and ensure the operation has prologue checks.
4. Run the feature's least-invasive offline-campaign smoke test.
5. Record the result by raising only that capability's validation level.

No level permits remote allocation, block resizing, pointer rewrites, or writes
on an unknown build.

## Native bridge maintenance boundary

The release bridge is intentionally limited to the capabilities declared in the
catalog. `AiPlacement`, runtime boundaries, player allegiance, and saved-film
playback remain high-risk feature sets: a failed check disables that feature
alone and must not delay Runtime Tags or offline tooling.

`game_build_analyzer.py` reports native assumptions that are still manually
reviewed (AI/HaloScript hooks and structure offsets). A future feature may only
add a new hard-coded native RVA after it has either gained a unique analyzer
signature or been added to this explicit review report with a smoke test.
