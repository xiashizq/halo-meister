# Difficulty icon sources

These thumbnails are static composites of the game's own difficulty UI components:

- `T_DifficultyIcon_Shield_Small`
- `T_DifficultyIcon_Skul_Small`
- `T_DifficultyIcon_Knife_Small`

The source textures were extracted from the locally installed Halo: Campaign Evolved
`pakchunk0-WinGDK` IO Store with `retoc`, decoded from BC7, and composed into the four
layouts used by the campaign difficulty UI. They are retained only as Halo Meister UI
assets for the user's locally installed game.

Layout mapping:

- Easy: shield
- Normal: knife and shield
- Heroic: crossed knives and shield
- Legendary / LASO: crossed knives, shield, and skull
- Unknown: dimmed shield fallback
