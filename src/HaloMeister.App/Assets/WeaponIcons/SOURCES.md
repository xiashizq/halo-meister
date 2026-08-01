# Weapon icon sources

These PNG files are decoded from the user's installed copy of Halo: Campaign
Evolved. Their cooked source textures are under:

- `Meteorite/Content/ui/Hud/WeaponCradle/Textures`
- `Meteorite/Content/ui/Hud/GrenadeCradle/Textures`

The assets were converted from the game's IoStore containers with `retoc
to-legacy`, then their inline BC7 mip data was decoded to PNG without changing
the artwork. They are used only as local UI previews for loaded runtime weapon
tags.
