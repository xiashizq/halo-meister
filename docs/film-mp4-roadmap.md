# Saved film to MP4 roadmap

## Goal

Turn a finalized Campaign Evolved `.film` into an H.264/AAC `.mp4` without
misrepresenting the replay payload as encoded video.

The required pipeline is:

```text
.film → validate/archive → load in matching game build → render → capture → encode → verify
```

## What is implemented

- Strict parsing and validation of the observed BLF chunk chain.
- Metadata extraction for map, difficulty, timestamps, author, scenario, rally point,
  build/session, replay size, and signature state.
- SHA-256 deduplication.
- Lossless extraction of the opaque `flmd` replay payload.
- A stable archive with a JSON index, independent of Meteorite's rotating cache.

## Playback evidence in the retail package

Although Campaign Evolved does not expose a supported Theater menu, its shipped
IoStore package index contains:

- `BlamSavedFilm`
- `WBP_SavedFilmTimeline.uasset`
- `BP_SavedFilmHUDComponent.uasset`
- `saved_film-chud_definition.uasset`

The shipped Unreal global metadata also exposes a `/Script/BlamSavedFilm` module with:

- `BlamSavedFilmBlueprintLibrary`
- `GetCurrentFilmLengthSeconds`
- `GetCurrentPlaybackSpeed`
- `GetCurrentPlaybackTimeSeconds`
- `IsFilmEnded`
- `IsPlayback`
- `IsPlaybackPaused`
- `RequestEndSavedFilm`
- `RequestPlaybackRevert`
- `SetFilmPlaybackPaused`
- `SetPlaybackGameSpeed`
- `BlamSavedFilmGameInstanceSubsystem`
- `IsFilmPlaybackSupported`
- saved-film opened/closed delegates

This is strong evidence that playback support exists in some form. The exposed metadata
does not reveal a start/open function because film initiation is implemented as a
non-reflected native virtual method.

The saved-film assets were also extracted read-only from the retail IoStore container.
`BP_SavedFilmHUDComponent` subscribes to:

- `On Saved Film Opened For Read`
- `On Saved Film Closed`

When a film opens, it creates `WBP_SavedFilmTimeline`. The timeline calls
`IsPlayback`, `GetCurrentFilmLengthSeconds`, and `GetCurrentPlaybackTimeSeconds` every
tick. This confirms the shipped UI is wired to a functioning native playback lifecycle.

## Recovered native launch route

The supported Campaign Evolved executable build (`TimeDateStamp 0x7DAEF44C`,
`SizeOfImage 0x0D104000`) retains a native
`BlamGameInstance.LaunchSavedFilm` command registration and handler.

Reverse engineering and a live vtable check established that:

- `UBlamSavedFilmGameInstanceSubsystem::Initialize` is at RVA `0x077E80D0`
  (vtable offset `0x2D0`). It registers an engine command named
  `BlamGameInstance.LaunchSavedFilm`; it is not itself the playback launcher.
- The registered one-argument command handler is at RVA `0x077E87A0`.
- The handler resolves a short film key against both `BlamData\autosave` and
  `UnrealBlamSavedFilms`.
- Finalized recordings use `asq_<key>.film`, while the eight-byte companion record
  containing the scenario code uses `<key>.film-unreal`.

The native bridge validates the executable identity and command-handler prologue, then
passes a native one-element `TArray<FString>` containing the companion key directly to
the handler. An earlier diagnostic mistakenly called `Initialize` as though it were
the launcher; supplying the game instance to that method caused a deferred access
violation and must not be repeated.

`CEModeMenu` keeps the second-last `THEATER` button and opens its in-game film list.
The obsolete Unreal handler is no longer used by the experimental launch path.

## Native handler limitation and Reach comparison

Disassembly of the complete handler shows that after resolving the `.film-unreal`
companion and scenario name it calls Unreal's level-opening routine with an empty
options string. It does not set `BlamScenarioGameOptions.SavedFilmName` and does not
open the finalized film for read. This explains the observed black travel screen and
the permanent `IsPlayback=false`, `time=0`, `length=0` telemetry.

Controlled browser-free probes tested `BlamCampaignFlowGameSubsystem:SetAndBeginCampaign`
with the A30 recording in both of the plausible name forms:

- `campaign_a30_a29a1e85_dist-server_6A6921FE`
- `asq_campaign_a30_a29a1e85_dist-server_6A6921FE.film`

Both calls returned success and persisted the supplied name into
`BlamMetaDataSaveGame.SavedScenarioGameOptions.SavedFilmName`, but both still loaded an
ordinary A30 session with playback false at `0/0`. Playback support and both shipping
and debug enablement flags reported true. The missing operation is therefore the native
Blam open-for-read/theater-session transition, not filename normalization, Unreal
travel, or a disabled shipping setting.

The installed MCC Reach build was used as a behavioral reference. Its
`saved_film_play` HaloScript descriptor is also connected only to generic script
evaluation machinery; Reach Theater is established by the MCC lobby/session flow
before map load. Its binaries cannot be copied into Campaign Evolved because the ABI
and engine builds differ.

An EAC-disabled live trace captured the selected Reach film in two phases:

- `haloreach.dll+0xDF6C` opens the selected path through the wrapper at `+0x422B0`,
  reads `0x1F8A8` bytes, and parses the film/session header.
- The asynchronous stream wrapper at `+0xAB114` then performs repeated `0x20000`-byte
  reads from a worker callback.

Searching Campaign Evolved for the corresponding header-size and open/parse shape
located its native route:

- `HaloSimulation_tag_release.dll+0x2057B0` accepts a request whose first field is
  mode `1` and whose following bytes contain the full film path.
- It opens the finalized `.film`, reads CE's `0x1F9B8`-byte header, copies the decoded
  game options and film metadata into the native global session structures, and
  advances the playback state machine.
- Its normal wrapper is `HaloSimulation_tag_release.dll+0x2054E0`. The game's own
  command queue constructs the same request structure before calling it.

The native bridge now validates the exact simulation DLL build and wrapper prologue,
restricts input to finalized `.film` files under Meteorite's autosave directory, and
submits this native request from Campaign Evolved's main game thread. The menu waits
for the native result asynchronously and then monitors reflected playback telemetry.

The first experimental build incorrectly submitted the wrapper re-entrantly from a
hook on the simulation-context getter. It returned normally but later corrupted
deferred state: captured dumps failed at executable RVAs `0x65F3B79` (null session
owner) and `0x343EF57` (invalid virtual object). The request's `0x204`-byte layout was
verified against the retail command builder; the mismatch was thread affinity. The
replacement build calls the wrapper synchronously inside UE4SS's game-thread dispatch,
matching the native command-queue consumer, and does not use the simulation hook for
saved films.

That game-thread-only build no longer failed immediately, but it remained at the main
menu, created a resumable solo-session shell, and later crash-looped. Pairing it
immediately with executable scenario travel also failed. The paired dumps repeated the
invalid deferred-object failures and added simulation RVA `0x430A2`, an intentional
fatal path reached after the native allocator at `0x4310F` returned null.

The retail caller resolves the remaining distinction: simulation RVA `0x2054E0` is
normally invoked inside the dedicated Blam command pump at RVA `0xE670`, not from the
UE game thread and not re-entrantly from the simulation-context getter. A candidate
bridge now hooks that exact pump, lets the original drain finish, and submits the film
between pump ticks on the same thread. It does not force executable travel; the native
state machine and its normal callbacks retain ownership of the transition. This bridge
is installed but the menu remains selection-only pending a controlled validation.

The menu injector also stopped moving the container's last child through
`ReplaceButtonContainerChildAt`, which could duplicate the shipped Resume button when
the main widget rebuilt. It now removes stale configured entries and uses
`InsertChildAt` to place Theater before the final shipped item without reparenting it.

## Playback work still required

1. Prove the newly installed native CE path transitions `IsPlayback` to true and
   recorded actions replay.
2. Detect desynchronization or early termination.
3. Add automated OBS capture start/stop around playback telemetry.
4. Expose the verified pipeline as `export-mp4`.

## Capture backend

This machine currently has:

- FFmpeg 7.1.1 with `gdigrab`, DirectShow, `libx264`, NVIDIA NVENC, and AMD AMF.
- OBS Studio at `C:\Program Files\obs-studio\bin\64bit\obs64.exe`.

OBS is the preferred final backend because Game Capture and per-application audio are
more robust than FFmpeg GDI desktop capture. FFmpeg remains useful for final validation
and optional encoding:

```text
ffprobe -v error -show_streams -show_format output.mp4
```

The exporter should not start capture until the saved-film subsystem reports playback,
and should stop only after `IsFilmEnded`, a user cancellation, or a timeout. The output
should be written to a temporary name and renamed to `.mp4` only after `ffprobe`
confirms both video and audio streams.

## Current product boundary

Live gameplay can be captured today, but that is not conversion of an archived film.
True `.film` to MP4 export is now gated on validating the recovered native
open-for-read/session transition, then automating capture around the playback lifecycle.

Official release material confirms that Campaign Evolved has no supported Theater mode:

- https://support.halowaypoint.com/hc/en-us/articles/51174525772564-Halo-Campaign-Evolved-Release-Notes-Early-Access
