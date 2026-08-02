# Live tools smoke tests

Run these tests only in an offline campaign mission, from least invasive to most
invasive. Record the profile ID, analyzer report, capability, operator, and
result with the release candidate.

| Capability | Minimum test | Expected result |
| --- | --- | --- |
| `RuntimeTags` | Attach, enumerate tags, resolve a segmented reference. | No invalid pointers; read-only scan completes. |
| `ObjectAppearance` | Apply and restore one same-skeleton variant. | The controlled object changes and original bytes restore. |
| `ObjectSpawn` | Spawn one harmless object away from the player. | Object result is nonzero and game remains stable. |
| `WeaponLoad` | Spawn, pick up, and remove one supported weapon. | Engine pickup completes without an orphan object. |
| `GameplayCheats` | Read skull state, toggle one reversible modifier, read it again. | Readback matches and original mask restores. |
| `SoftCeilings` | Read, toggle, then restore the registered setting. | Readback and restoration succeed. |
| `PlayerTools` | Read position and perform a short safe teleport. | Confirmed result and stable player state. |
| `PlayerAllegiance` | Apply and restore one team while keeping the same unit datum. | Unit state is retained until restore. |
| `Machinima` | Query state and enable/disable camera controls. | State transition is reported and reversible. |
| `BipedPossession` | Spawn a supported biped, possess it, then disable possession. | No crash and cleanup is confirmed. |
| `AiPlacement` | Place one actor at a valid nearby scenario squad point. | Submission completes and mission remains stable. |
| `RuntimeBoundaries` | Read, disable, and restore one known boundary. | Bitset restores for the same active scenario. |
| `SavedFilm` | Open a finalized allowed film and observe playback telemetry. | Only promote after playback actually starts. |

`Integrated` is sufficient to ship a disabled-by-default experimental capability.
Only raise a capability to `LiveValidated` after its row succeeds. Any failed
row lowers or keeps only that capability's level; it does not invalidate the
rest of the profile.
