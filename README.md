# Halo Meister

Halo Meister is a Windows companion and offline runtime modding toolbox for
**Halo: Campaign Evolved**. It brings campaign progress, local game files,
checkpoint backups, customization, live gameplay experiments, runtime tags, and
a phone-friendly remote into one WinUI 3 app.

> [!WARNING]
> Halo Meister is intended for offline campaign use and modding research. Back
> up important data, close the game before file or setup operations, and never
> force a live action when build verification fails.

[Download the latest release](https://github.com/NicmeisteR/halo-meister/releases) ·
[Nexus Mods](https://www.nexusmods.com/halocampaignevolved/mods/115?tab=description) ·
[Report a bug](https://github.com/NicmeisteR/halo-meister/issues) ·
[Join the Discord](https://discord.gg/tyQvGDCEvG) ·
[Follow @NicmeistaR on X](https://x.com/NicmeistaR) ·
[Visit @NicmeisteR on GitHub](https://github.com/NicmeisteR)

## What Halo Meister can do

### Progress and profile

- Authenticate with Campaign Evolved's PlayFab session and download
  `BlamProgressSave`.
- Inspect mission completion, difficulty progress, skulls, collectibles,
  insertion points, profile flags, and entitlements.
- Preserve unknown save fields and verify byte-exact round trips.
- Keep PlayFab data and entitlement ownership read-only in packaged releases.

### Local game files

- Change locally equipped armor and weapon appearances while retaining unknown
  customization entries. Retail builds always allow Mark VI and game-default
  appearances, and require verified ownership for other selectable cosmetics.
  Ownership-sensitive live variant tools are restricted when they cannot map a
  selected appearance back to the account's entitlements. Armor Mixer filters
  player foundations and donors by ownership; Spartan NPC spawning is unchanged.
- Edit Meteorite configuration through categorized controls without flattening
  unrelated comments or formatting.
- Discover, inspect, export, import, back up, and restore Windows Gaming
  Services checkpoint containers.

### Live offline tools

- Spawn AI and weapons, swap projectiles, and experiment with vehicles.
- Change the player biped, mix armor, apply character appearances, and control
  player movement or position.
- Toggle skulls and gameplay modifiers, adjust boundaries, and use machinima
  camera/world tools.
- Browse and edit loaded tags with schema-aware realtime controls.
- Run trusted UE4SS Lua or HaloScript in retail and developer builds.

Live tools are grouped by task in the app: **Gameplay**, **Spawn & equip**,
**Player & appearance**, and **Camera & world**. Availability depends on the
current mission, installed bridge, and supported game build.

### Phone remote

- Start an opt-in local web remote for a trusted phone or tablet.
- Pair through a temporary QR code or link.
- Use allowlisted live actions without exposing raw Lua or process-memory access.
- Restrict the firewall rule to the executable, private networks, and local or
  Tailscale addresses.

Do not port-forward the phone remote or expose it to an untrusted network.

## Download and first run

1. Download `HaloMeister-<version>-win-x64.zip` and its `.sha256` file from
   [Releases](https://github.com/NicmeisteR/halo-meister/releases).
2. Extract the complete ZIP. Keep `Assets` and the runtime files beside
   `HaloMeister.exe`.
3. Run `HaloMeister.exe`.

The release is self-contained; end users do not need Visual Studio, the .NET
SDK, or a separate .NET runtime. Community releases are currently unsigned, so
Windows may show an unknown-publisher warning.

### Requirements

For progress and local-file tools:

- Windows 10 version 1809 or newer.
- The complete extracted Halo Meister release folder.

For live tools:

- Halo: Campaign Evolved on a supported x64 game build.
- The Halo Meister bridge and pinned UE4SS loader installed through **Setup**.
- An offline campaign mission loaded before connecting.

## Set up live tools

1. Close Campaign Evolved.
2. Open **Setup** in Halo Meister and select the game installation folder.
3. Select **Install** or **Repair / update**.
4. Halo Meister downloads the pinned official RE-UE4SS build, verifies its
   SHA-256 checksum, applies the supported settings and signatures, and installs
   the Halo Meister bridge.
5. Restart Campaign Evolved, load an offline campaign mission, and select
   **Connect** in Halo Meister.

Existing UE4SS installations are preserved where possible. Replaced files are
backed up under `%LOCALAPPDATA%\HaloMeister\UE4SSBackups`.

Connecting and installing the bridge solve different parts of the live workflow:
direct tag-memory tools need the game connection, while scripting, spawning,
skulls, cheats, and several world controls also need the in-game bridge.

## Runtime scripting

Open **Advanced → Scripting** in retail or developer builds. HaloScript is
selected by default. The editor accepts the game's `hs:` console form,
command-style calls, and parenthesized HaloScript expressions:

```text
hs:chud_show 0
unit_kill (player0)
(fade_out 0 0 0 0)
```

HaloScript completions appear after two command characters. Use the arrow keys
to choose an overload, **Tab** or **Enter** to accept it, **Esc** to close the
list, or **Ctrl+Space** to show common commands. The reference pane searches the
function/global catalog extracted from the supported game build; **Full
reference** opens the complete bundled signature list. The same syntax guide is
available on the in-app **Help** page.

The game console does not return evaluation output to the bridge, so HaloScript
submissions are reported as unverified and must be checked in game. Catalog
presence does not guarantee that a command is valid or safe in every mission.
Run scripts only in an offline campaign and save important progress first.

## Safety, backups, and persistence

Use a simple loop when experimenting:

**Back up → make one focused change → verify it → test offline → restore or keep it.**

Halo Meister creates targeted safety copies during destructive workflows:

- PlayFab reads and developer uploads preserve the current progress blob.
- Config and customization writes snapshot the editable configuration set.
- Checkpoint restores snapshot the relevant Windows Gaming Services title cache.
- Bridge installation backs up files it replaces.

Local configuration, installed overlays, and restored checkpoint containers are
persistent. Most live tag, skull, character, cheat, and boundary changes are
session-only and normally reset when the mission unloads or the game restarts.

If a native tool rejects the current game build, stop there. Build locks exist to
keep known addresses and layouts from being used after an incompatible update.

## Common problems

| Problem | What to try |
|---|---|
| Game disconnected | Start Campaign Evolved, load a campaign mission, then select **Connect**. Reconnect after every game restart. |
| Bridge not ready | Close the game, run **Setup → Repair / update**, then restart the game. |
| Live list is empty | Load an offline mission, reconnect, and scan again. Only resident mission data can be listed. |
| PlayFab download unavailable | Select **Authenticate** and let Campaign Evolved make a fresh PlayFab request. |
| Local save or restore blocked | Close Campaign Evolved so it cannot lock or overwrite the data. |
| Build verification failed | Wait for a compatible Halo Meister update; do not force the action. |

The in-app **Help** page contains the current quick-start and recovery guidance.
Use **Community & links** for releases, repository links, issue reporting,
project information, and credits.

## Build from source

Developer builds require Windows and the .NET 10 SDK. NuGet restore supplies the
Windows App SDK dependency.

```cmd
git clone https://github.com/NicmeisteR/halo-meister.git
cd halo-meister

:: Build the solution
build.cmd

:: Build and launch the desktop app
run.cmd

:: Produce the self-contained release folder, ZIP, and SHA-256 file
release.cmd

```

Release packages target x64 to match Campaign Evolved and the native bridge.
When building in Visual Studio, select the **x64** solution platform.

### Versioning and releases

`Directory.Build.props` is the single source of truth for the app and package
version. To publish a release, update `<Version>`, commit the change, and tag that
exact commit `v<Version>`. Pushing the tag builds the self-contained package and
publishes it to this repository's GitHub Releases page. The workflow rejects a
tag that does not match the committed version.

### Repository layout

| Path | Purpose |
|---|---|
| `src/HaloMeister.Core` | Dependency-free save container, parser, serializer, and catalog library. |
| `src/HaloMeister.Cli` | Command-line save inspection and verification tools. |
| `src/HaloMeister.App` | WinUI 3 desktop application and bundled app assets. |
| `native/HaloMeister.BlamBridge` | Build-locked native game bridge. |
| `native/HaloMeister.TagModExporter` | Structured tag-mod and IoStore export support. |
| `docs` | Focused research, maintenance, and implementation notes. |
| `samples` | Save and bridge probes used during development. |

Useful technical notes:

- [Runtime tag injection](docs/runtime-tag-injection.md)
- [Game update playbook](docs/game-update-playbook.md)
- [Saved-film MP4 roadmap](docs/film-mp4-roadmap.md)
- [Windows Gaming Services continuation notes](docs/wgs-gamesaves-continuation.md)
- [Repository and release policy](docs/repository-policy.md)

## Reporting issues and contributing

Use [GitHub Issues](https://github.com/NicmeisteR/halo-meister/issues) for
reproducible bugs and focused feature requests. A useful report includes:

- Halo Meister version and whether it is a packaged or developer build.
- Campaign Evolved platform and game build.
- The page and action you used.
- Exact error text and the smallest reliable reproduction sequence.
- Logs or screenshots with credentials, session tickets, and personal data removed.

Research notes, build-profile updates, safe test results, documentation fixes,
and focused code contributions are welcome. Never include copyrighted game data,
credentials, or private save data in an issue or contribution.

## Credits

Halo Meister is created and maintained by **Nicolaas Nel**
([NicmeisteR on GitHub](https://github.com/NicmeisteR) ·
[NicmeistaR on X](https://x.com/NicmeistaR)).

Special thanks to everyone who helped make the project possible:

- The Baboon team, especially Camden and Zoephie.
- Alexis (`gruntdotapi`).
- Zed (`Zeddikens`).
- Deadman.
- The [RE-UE4SS team](https://github.com/UE4SS-RE/RE-UE4SS).
- Everyone who shares research, tests experimental builds, reports bugs, or
  helps other players get started.

## Disclaimer

Halo Meister is an independent, community-built fan project. It is not
affiliated with, endorsed by, or supported by Microsoft, Xbox, Halo Studios, or
the developers of Halo: Campaign Evolved. Halo and related names are trademarks
of their respective owners.
