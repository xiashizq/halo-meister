# Weapon icon sources

These PNG files are decoded from the user's installed copy of Halo: Campaign
Evolved. Their cooked source textures are under:

- `Meteorite/Content/ui/Hud/WeaponCradle/Textures`
- `Meteorite/Content/ui/Hud/GrenadeCradle/Textures`

The assets were converted from the game's IoStore containers with `retoc
to-legacy`, then their inline BC7 / DXT1 mip data was decoded to PNG without
changing the artwork. They are used only as local UI previews for loaded
runtime weapon tags.

## Coverage notes

`WeaponCradle/Textures` was re-scanned against `pakchunk0-Windows` (2026-08).
The game ships one cradle icon that was previously missing from Cartographer Toolkit:

- `T_UI_SeraphMissiles_Icons` (DXT1) — added for Seraph missile hardpoints

Every other `T_UI_*WeaponIcon*` / turret cradle texture was already present.
Close cousins (gravity hammer → energy sword, concussion → fuel rod, etc.)
still reuse nearby cradle art via `ProjectileSwapperService.WeaponIconUri`.

Weapons with no cradle icon, including the Brute Shot and Mauler, use
`T_UI_AssaultRifle_WeaponIcon.png`.
