//! Campaign Evolved music, dialogue, vehicle, weapon, and character audition and WAV export.
//!
//! Sound tags under `sound/music`, `sound/scripted`, `sound/vehicles`, `sound/weapons`, and `sound/characters` carry no samples. This follows the same
//! path Baboon uses: walk the cooked package imports to the Wwise event, read
//! the loose `.wem` (or the bank-embedded media) out of the legacy `.pak` set,
//! and decode it with `blam-tags`.
//!
//! The process stays up so the IoStore index is built once.
//!
//! ```text
//! halomeister-music --paks <Meteorite/Content/Paks>
//! ```
//!
//! Stdout is one JSON object per line. The first line is the track list.
//! Later lines answer JSON commands on stdin:
//! `{"cmd":"resolve","package":"...","language":"SFX"}`
//! `{"cmd":"export","package":"...","index":0,"language":"SFX","out":"a.wav"}`
//! `{"cmd":"exportAll","package":"...","language":"SFX","out":"C:\\out"}`

use std::collections::{BTreeSet, HashMap, VecDeque};
use std::io::{BufRead, Cursor, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;

use anyhow::{Context, Result, anyhow, bail};
use blam_tags::audio::wwise::{Bnk, HircIndex, SoundSource, decode_wem};
use blam_tags::audio::DecodedPcm;
use blam_tags::iostore::container_header::EIoContainerHeaderVersion;
use blam_tags::iostore::pak::PakSet;
use blam_tags::iostore::ue_types::{EIoStoreTocVersion, FPackageObjectIndex};
use blam_tags::iostore::usmap::Usmap;
use blam_tags::iostore::wwise_event::{EventCookedData, read_event_cooked_data};
use blam_tags::iostore::zen::FZenPackageHeader;
use blam_tags::iostore::IoStoreArchive;
use serde::Serialize;
use serde_json::{json, Value};

const TOC_VERSION: EIoStoreTocVersion = EIoStoreTocVersion::ReplaceIoChunkHashWithIoHash;
const HEADER_VERSION: EIoContainerHeaderVersion = EIoContainerHeaderVersion::SoftPackageReferences;
const WWISE_MOUNT: &str = "Meteorite/Content/WwiseAudio";
const AK_AUDIO_EVENT_CLASS: &str = "/Script/AkAudio.AkAudioEvent";
const MAX_PACKAGE_VISITS: usize = 512;

#[derive(Clone, Debug)]
enum MediaLocation {
    Loose(String),
    Bank(String),
}

#[derive(Clone, Debug)]
struct ResolvedMedia {
    name: String,
    language: String,
    media_id: u32,
    location: MediaLocation,
}

struct PackageIndex {
    archives: Vec<IoStoreArchive>,
    /// Lowercase `/game/...` package name -> (archive index, entry path).
    by_package: HashMap<String, (usize, String)>,
}

struct BankCache {
    bnk: Bnk,
    hirc: Arc<HircIndex>,
}

struct Session {
    paks_root: PathBuf,
    index: PackageIndex,
    usmap: Usmap,
    event_class: FPackageObjectIndex,
    paks: Option<PakSet>,
    banks: HashMap<String, BankCache>,
    /// package lowercase -> (language shown, media).
    resolved: HashMap<String, (String, Vec<ResolvedMedia>)>,
    /// `{package}|{requested language}` -> permutation indexes that have media.
    playable: HashMap<String, Vec<usize>>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct TrackJson {
    package: String,
    name: String,
    folder: String,
    kind: String,
}

fn main() {
    if let Err(error) = run() {
        let _ = emit(&json!({"ok": false, "error": format!("{error:#}")}));
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let mut args = std::env::args().skip(1);
    let mut paks = None;
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--paks" => paks = args.next().map(PathBuf::from),
            other => bail!("unknown argument '{other}'"),
        }
    }
    let paks = paks.context("--paks is required")?;
    let mut session = Session::open(&paks)?;
    let tracks = session.tracks();
    let music = tracks.iter().filter(|track| track.kind == "music").count();
    let dialogue = tracks.iter().filter(|track| track.kind == "dialogue").count();
    let vehicle = tracks.iter().filter(|track| track.kind == "vehicle").count();
    let weapon = tracks.iter().filter(|track| track.kind == "weapon").count();
    let character = tracks.iter().filter(|track| track.kind == "character").count();
    let dialog = tracks.iter().filter(|track| track.kind == "dialog").count();
    let sandbox = tracks.iter().filter(|track| track.kind == "sandbox").count();
    let device = tracks.iter().filter(|track| track.kind == "device").count();
    let levels = tracks.iter().filter(|track| track.kind == "levels").count();
    let materials = tracks.iter().filter(|track| track.kind == "materials").count();
    let ui = tracks.iter().filter(|track| track.kind == "ui").count();
    let visual_fx = tracks.iter().filter(|track| track.kind == "visual_fx").count();
    eprintln!(
        "audio tracks: {music} music, {dialogue} dialogue, {vehicle} vehicle, {weapon} weapon, {character} character, {dialog} dialog, {sandbox} sandbox, {device} device, {levels} levels, {materials} materials, {ui} ui, {visual_fx} visual_fx"
    );
    if !emit(&json!({
        "ok": true,
        "ready": true,
        "tracks": tracks,
    })) {
        return Ok(());
    }

    let stdin = std::io::stdin();
    for line in stdin.lock().lines() {
        let line = match line {
            Ok(line) => line,
            Err(_) => break,
        };
        if line.trim().is_empty() {
            continue;
        }
        let command: Value = match serde_json::from_str(&line) {
            Ok(value) => value,
            Err(error) => {
                if !emit(&json!({"ok": false, "error": error.to_string()})) {
                    break;
                }
                continue;
            }
        };
        let response = match handle(&mut session, &command) {
            Ok(value) => value,
            Err(error) => json!({"ok": false, "error": format!("{error:#}")}),
        };
        if !emit(&response) {
            break;
        }
    }
    Ok(())
}

fn handle(session: &mut Session, command: &Value) -> Result<Value> {
    let cmd = command.get("cmd").and_then(Value::as_str).unwrap_or("");
    let package = command.get("package").and_then(Value::as_str).unwrap_or("");
    let language = command.get("language").and_then(Value::as_str);
    match cmd {
        "playable" => {
            let packages: Vec<String> = command
                .get("packages")
                .and_then(Value::as_array)
                .map(|items| {
                    items
                        .iter()
                        .filter_map(Value::as_str)
                        .map(str::to_string)
                        .collect::<Vec<_>>()
                })
                .filter(|items| !items.is_empty())
                .unwrap_or_else(|| {
                    if package.is_empty() {
                        Vec::new()
                    } else {
                        vec![package.to_string()]
                    }
                });
            if packages.is_empty() {
                bail!("playable requires package");
            }
            let results = packages
                .iter()
                .map(|item| {
                    let indices = session
                        .playable_indices(item, language)
                        .unwrap_or_default();
                    json!({"package": item, "indices": indices})
                })
                .collect::<Vec<_>>();
            Ok(json!({"ok": true, "results": results}))
        }
        "resolve" => {
            if package.is_empty() {
                bail!("resolve requires package");
            }
            let (shown, languages, media) = session.resolve(package, language)?;
            Ok(json!({
                "ok": true,
                "package": package,
                "language": shown,
                "languages": languages,
                "permutations": perm_json(&media),
            }))
        }
        "export" => {
            let index = command
                .get("index")
                .and_then(Value::as_u64)
                .context("export requires index")? as usize;
            let out = command
                .get("out")
                .and_then(Value::as_str)
                .context("export requires out")?;
            if package.is_empty() {
                bail!("export requires package");
            }
            let file = session.export_one(package, language, index, Path::new(out))?;
            Ok(json!({"ok": true, "file": file}))
        }
        "exportAll" => {
            let out = command
                .get("out")
                .and_then(Value::as_str)
                .context("exportAll requires out")?;
            if package.is_empty() {
                bail!("exportAll requires package");
            }
            let (files, failed) = session.export_all(package, language, Path::new(out))?;
            Ok(json!({"ok": true, "files": files, "failed": failed}))
        }
        other => bail!("unknown command '{other}'"),
    }
}

fn perm_json(media: &[ResolvedMedia]) -> Vec<Value> {
    media
        .iter()
        .enumerate()
        .map(|(index, item)| {
            json!({
                "index": index,
                "name": item.name,
                "language": item.language,
                "mediaId": item.media_id,
                "location": item.location_label(),
            })
        })
        .collect()
}

impl ResolvedMedia {
    fn location_label(&self) -> String {
        match &self.location {
            MediaLocation::Loose(path) => path.clone(),
            MediaLocation::Bank(bank) => format!("{bank} → {}", self.media_id),
        }
    }
}

impl Session {
    fn open(paks_root: &Path) -> Result<Self> {
        eprintln!("indexing {}", paks_root.display());
        let index = PackageIndex::build(paks_root)?;
        if index.by_package.is_empty() {
            bail!("no cooked packages in {}", paks_root.display());
        }
        eprintln!("indexed {} packages", index.by_package.len());
        Ok(Self {
            paks_root: paks_root.to_path_buf(),
            index,
            usmap: Usmap::meteorite().context("bundled Campaign Evolved usmap")?,
            event_class: FPackageObjectIndex::create_script_import(AK_AUDIO_EVENT_CLASS),
            paks: None,
            banks: HashMap::new(),
            resolved: HashMap::new(),
            playable: HashMap::new(),
        })
    }

    fn tracks(&self) -> Vec<TrackJson> {
        let mut tracks: Vec<TrackJson> = self
            .index
            .by_package
            .keys()
            .filter_map(|package| track_from_package(package))
            .filter(|track| !links_only_when_playable(&track.kind) || self.has_wwise_event(&track.package))
            .collect();
        tracks.sort_by(|a, b| {
            a.folder
                .cmp(&b.folder)
                .then_with(|| a.name.cmp(&b.name))
        });
        tracks
    }

    /// Vehicle, weapon, character, and combat-dialog `.sound` tags carry no samples. Keep a row only when its
    /// imports reach a Wwise event. Empty tags, and audio shells with no event, cannot play.
    fn has_wwise_event(&self, package: &str) -> bool {
        self.event_packages(package)
            .is_ok_and(|events| !events.is_empty())
    }

    fn resolve(
        &mut self,
        package: &str,
        preferred: Option<&str>,
    ) -> Result<(String, Vec<String>, Vec<ResolvedMedia>)> {
        let key = package.to_ascii_lowercase();
        let binding = self.binding(&key)?;
        let languages = languages_of(&binding);
        let shown = language_to_show(&binding, preferred);
        let media = media_for_language(&binding, &shown);
        self.resolved
            .insert(key, (shown.clone(), media.clone()));
        Ok((shown, languages, media))
    }

    fn playable_indices(&mut self, package: &str, language: Option<&str>) -> Result<Vec<usize>> {
        let requested = language.unwrap_or("").to_ascii_lowercase();
        let key = format!("{}|{requested}", package.to_ascii_lowercase());
        if let Some(indices) = self.playable.get(&key) {
            return Ok(indices.clone());
        }
        self.ensure_resolved(package, language)?;
        let count = self
            .resolved
            .get(&package.to_ascii_lowercase())
            .map(|(_, media)| media.len())
            .unwrap_or(0);
        let indices: Vec<usize> = (0..count).collect();
        self.playable.insert(key, indices.clone());
        Ok(indices)
    }

    fn binding(&mut self, package: &str) -> Result<Vec<ResolvedMedia>> {
        // Resolve from packages first. Banks are only opened when an event
        // names them and lists no loose media.
        let events = self.event_packages(package)?;
        let mut binding = Vec::new();
        let mut need_banks = Vec::new();
        for (header, bytes) in &events {
            let Some(export) = header.find_export_of_class(self.event_class) else {
                continue;
            };
            let start = header.summary.header_size as usize + export.cooked_serial_offset as usize;
            let end = start + export.cooked_serial_size as usize;
            let Some(body) = bytes.get(start..end) else {
                continue;
            };
            let names = header.name_map.copy_raw_names();
            let cooked = match read_event_cooked_data(body, &names, &self.usmap) {
                Ok(cooked) => cooked,
                Err(error) => {
                    eprintln!("event decode skipped: {error:#}");
                    continue;
                }
            };
            if cooked.media.is_empty() {
                need_banks.push(cooked);
                continue;
            }
            for media in &cooked.media {
                binding.push(ResolvedMedia {
                    name: display_name(&media.source_name, media.media_id),
                    language: media.language.clone(),
                    media_id: media.media_id,
                    location: MediaLocation::Loose(media.path.clone()),
                });
            }
        }
        for cooked in &need_banks {
            binding.extend(self.bank_embedded_media(cooked)?);
        }
        Ok(binding)
    }

    fn event_packages(&self, package: &str) -> Result<Vec<(FZenPackageHeader, Vec<u8>)>> {
        if !self.index.by_package.contains_key(package) {
            bail!("sound tag is not in the mounted packages: {package}");
        }
        let mut seen = BTreeSet::new();
        let mut queue = VecDeque::new();
        let mut events = Vec::new();
        seen.insert(package.to_string());
        queue.push_back(package.to_string());
        let mut visits = 0usize;
        while let Some(current) = queue.pop_front() {
            visits += 1;
            if visits > MAX_PACKAGE_VISITS {
                break;
            }
            let Some((header, bytes)) = self.index.header(&current) else {
                continue;
            };
            if header.exports_class(self.event_class) {
                events.push((header, bytes));
                continue;
            }
            for import in &header.imported_package_names {
                let lower = import.to_ascii_lowercase();
                if !seen.insert(lower.clone()) {
                    continue;
                }
                if is_audio_package(&lower) {
                    queue.push_back(import.clone());
                }
            }
        }
        Ok(events)
    }

    fn bank_embedded_media(&mut self, event: &EventCookedData) -> Result<Vec<ResolvedMedia>> {
        let mut out = Vec::new();
        let mut seen = BTreeSet::new();
        for bank in &event.banks {
            let sources = match self.bank_event_sources(&bank.path, event.event_id) {
                Ok(sources) => sources,
                Err(error) => {
                    eprintln!("bank {}: {error:#}", bank.path);
                    continue;
                }
            };
            for source in sources {
                if !seen.insert((bank.language.clone(), source.source_id)) {
                    continue;
                }
                let loose = loose_media_path(source.source_id);
                let location = if source.streamed && self.has_media(&loose) {
                    MediaLocation::Loose(loose)
                } else {
                    MediaLocation::Bank(bank.path.clone())
                };
                out.push(ResolvedMedia {
                    name: source.source_id.to_string(),
                    language: bank.language.clone(),
                    media_id: source.source_id,
                    location,
                });
            }
        }
        Ok(out)
    }

    fn export_one(
        &mut self,
        package: &str,
        language: Option<&str>,
        index: usize,
        out: &Path,
    ) -> Result<Value> {
        self.ensure_resolved(package, language)?;
        let key = package.to_ascii_lowercase();
        let media = self
            .resolved
            .get(&key)
            .and_then(|(_, media)| media.get(index).cloned())
            .with_context(|| format!("permutation {index} is not in {package}"))?;
        let pcm = self.decode(&media)?;
        write_wav(out, &pcm)?;
        Ok(file_json(&media.name, out, &pcm))
    }

    fn export_all(
        &mut self,
        package: &str,
        language: Option<&str>,
        out_dir: &Path,
    ) -> Result<(Vec<Value>, Vec<Value>)> {
        self.ensure_resolved(package, language)?;
        let key = package.to_ascii_lowercase();
        let media = self
            .resolved
            .get(&key)
            .map(|(_, media)| media.clone())
            .unwrap_or_default();
        if media.is_empty() {
            bail!("this cue has no playable audio");
        }
        std::fs::create_dir_all(out_dir)?;
        let mut files = Vec::new();
        let mut failed = Vec::new();
        for (index, item) in media.iter().enumerate() {
            let file_name = format!("{:03}_{}.wav", index + 1, sanitize(&item.name));
            let path = out_dir.join(file_name);
            match self.decode(item).and_then(|pcm| {
                write_wav(&path, &pcm)?;
                Ok(file_json(&item.name, &path, &pcm))
            }) {
                Ok(file) => files.push(file),
                Err(error) => failed.push(json!({
                    "name": item.name,
                    "error": format!("{error:#}"),
                })),
            }
        }
        Ok((files, failed))
    }

    fn ensure_resolved(&mut self, package: &str, language: Option<&str>) -> Result<()> {
        let key = package.to_ascii_lowercase();
        let wanted = language.unwrap_or("");
        let fresh = match self.resolved.get(&key) {
            Some((shown, _)) if wanted.is_empty() || shown.eq_ignore_ascii_case(wanted) => false,
            _ => true,
        };
        if fresh {
            self.resolve(package, language.filter(|value| !value.is_empty()))?;
        }
        Ok(())
    }

    fn decode(&mut self, media: &ResolvedMedia) -> Result<DecodedPcm> {
        let bytes = self.fetch(media)?;
        decode_wem(&bytes).map_err(|error| anyhow!("decoding {}: {error}", media.location_label()))
    }

    fn fetch(&mut self, media: &ResolvedMedia) -> Result<Vec<u8>> {
        match &media.location {
            MediaLocation::Loose(path) => {
                let mounted = format!("{WWISE_MOUNT}/{path}");
                self.pak_set()?
                    .read(&mounted)
                    .with_context(|| format!("reading {mounted}"))
            }
            MediaLocation::Bank(bank) => {
                self.ensure_bank(bank)?;
                self.banks
                    .get(bank)
                    .and_then(|cache| cache.bnk.embedded_wem(media.media_id).map(|bytes| bytes.to_vec()))
                    .with_context(|| format!("{bank} holds no media {}", media.media_id))
            }
        }
    }

    fn has_media(&mut self, relative: &str) -> bool {
        let mounted = format!("{WWISE_MOUNT}/{relative}");
        self.pak_set()
            .is_ok_and(|set| set.contains(&mounted))
    }

    fn bank_event_sources(&mut self, bank_path: &str, event_id: u32) -> Result<Vec<SoundSource>> {
        self.ensure_bank(bank_path)?;
        Ok(self
            .banks
            .get(bank_path)
            .map(|cache| cache.hirc.resolve_event_id(event_id))
            .unwrap_or_default())
    }

    fn ensure_bank(&mut self, bank_path: &str) -> Result<()> {
        if self.banks.contains_key(bank_path) {
            return Ok(());
        }
        let mounted = format!("{WWISE_MOUNT}/{bank_path}");
        let bytes = self
            .pak_set()?
            .read(&mounted)
            .with_context(|| format!("reading {mounted}"))?;
        let bnk = Bnk::parse(bytes).map_err(|error| anyhow!("parsing {mounted}: {error}"))?;
        let mut hirc = HircIndex::new();
        if let Some(bytes) = bnk.hirc_bytes() {
            hirc.add_hirc(bytes, bnk.version);
        }
        hirc.finalize();
        self.banks.insert(
            bank_path.to_string(),
            BankCache {
                bnk,
                hirc: Arc::new(hirc),
            },
        );
        Ok(())
    }

    fn pak_set(&mut self) -> Result<&mut PakSet> {
        if self.paks.is_none() {
            eprintln!("opening pak set {}", self.paks_root.display());
            let set = PakSet::open_dir(&self.paks_root)
                .with_context(|| format!("opening pak set at {}", self.paks_root.display()))?;
            if set.is_empty() {
                bail!("no readable .pak containers in {}", self.paks_root.display());
            }
            eprintln!("opened {} pak containers", set.len());
            self.paks = Some(set);
        }
        Ok(self.paks.as_mut().expect("just populated"))
    }
}

impl PackageIndex {
    fn build(paks_root: &Path) -> Result<Self> {
        let mut utocs: Vec<PathBuf> = std::fs::read_dir(paks_root)
            .with_context(|| format!("could not read {}", paks_root.display()))?
            .filter_map(|entry| entry.ok().map(|entry| entry.path()))
            .filter(|path| {
                path.extension()
                    .is_some_and(|ext| ext.eq_ignore_ascii_case("utoc"))
            })
            .filter(|path| {
                !path
                    .file_name()
                    .is_some_and(|name| name.eq_ignore_ascii_case("global.utoc"))
            })
            .collect();
        utocs.sort();

        let mut archives = Vec::new();
        let mut by_package = HashMap::new();
        for utoc in &utocs {
            let Ok(archive) = IoStoreArchive::open(utoc) else {
                eprintln!("skipped {}", utoc.display());
                continue;
            };
            let archive_index = archives.len();
            for entry in archive.entries() {
                let path = entry.path.replace('\\', "/");
                if !path.to_ascii_lowercase().ends_with(".uasset") {
                    continue;
                }
                let Some(rest) = path.split_once("/Content/").map(|(_, rest)| rest) else {
                    continue;
                };
                let stem = rest.trim_end_matches(".uasset");
                let package = format!("/game/{stem}").to_ascii_lowercase();
                by_package.insert(package, (archive_index, entry.path.clone()));
            }
            archives.push(archive);
        }
        Ok(Self {
            archives,
            by_package,
        })
    }

    fn header(&self, package: &str) -> Option<(FZenPackageHeader, Vec<u8>)> {
        let (archive_index, path) = self.by_package.get(&package.to_ascii_lowercase())?;
        let bytes = self.archives[*archive_index].read(path).ok()?;
        let mut cursor = Cursor::new(&bytes);
        let header =
            FZenPackageHeader::deserialize(&mut cursor, None, TOC_VERSION, HEADER_VERSION, None)
                .ok()?;
        Some((header, bytes))
    }
}

fn track_from_package(package: &str) -> Option<TrackJson> {
    track_under(package, "/sound/music/", "music", "music")
        .or_else(|| track_under(package, "/sound/scripted/", "scripted", "dialogue"))
        .or_else(|| track_under(package, "/sound/vehicles/", "vehicles", "vehicle"))
        .or_else(|| track_under(package, "/sound/weapons/", "weapons", "weapon"))
        .or_else(|| track_under(package, "/sound/characters/", "characters", "character"))
        .or_else(|| track_under(package, "/sound/dialog/", "dialog", "dialog"))
        .or_else(|| track_under(package, "/sound/005_sandbox/", "005_sandbox", "sandbox"))
        .or_else(|| track_under(package, "/sound/device_machines/", "device_machines", "device"))
        .or_else(|| track_under(package, "/sound/levels/", "levels", "levels"))
        .or_else(|| track_under(package, "/sound/materials/", "materials", "materials"))
        .or_else(|| track_under(package, "/sound/ui/", "ui", "ui"))
        .or_else(|| track_under(package, "/sound/visual_fx/", "visual_fx", "visual_fx"))
}

fn track_under(package: &str, marker: &str, root: &str, kind: &str) -> Option<TrackJson> {
    let index = package.find(marker)?;
    let rest = &package[index + marker.len()..];
    let rest = strip_sound_suffix(rest);
    if rest.is_empty() {
        return None;
    }
    let (folder, name) = match rest.rfind('/') {
        Some(split) => (&rest[..split], &rest[split + 1..]),
        None => ("", rest),
    };
    if name.is_empty() {
        return None;
    }
    let folder = if folder.is_empty() {
        root.to_string()
    } else {
        format!("{root}/{folder}")
    };
    Some(TrackJson {
        package: package.to_string(),
        name: name.to_string(),
        folder,
        kind: kind.to_string(),
    })
}

fn strip_sound_suffix(rest: &str) -> &str {
    rest.strip_suffix("-sound_looping")
        .or_else(|| rest.strip_suffix("-sound"))
        .unwrap_or(rest)
}

fn links_only_when_playable(kind: &str) -> bool {
    matches!(kind, "vehicle" | "weapon" | "character" | "dialog" | "sandbox" | "device" | "levels" | "materials" | "ui" | "visual_fx")
}

fn is_audio_package(lower: &str) -> bool {
    lower.starts_with("/game/audio/")
        || lower.starts_with("/game/wwise/")
        || lower.starts_with("/game/wwiseaudio/")
}

fn display_name(source_name: &str, media_id: u32) -> String {
    let stem = source_name
        .rsplit(['\\', '/'])
        .next()
        .unwrap_or(source_name)
        .trim_end_matches(".wav")
        .trim_end_matches(".WAV");
    if stem.is_empty() {
        media_id.to_string()
    } else {
        stem.to_string()
    }
}

fn loose_media_path(media_id: u32) -> String {
    let id = media_id.to_string();
    let bucket = &id[..id.len().min(2)];
    format!("Media/{bucket}/{id}.wem")
}

fn languages_of(media: &[ResolvedMedia]) -> Vec<String> {
    let mut out = Vec::new();
    for item in media {
        if !out.iter().any(|language: &String| language.eq_ignore_ascii_case(&item.language)) {
            out.push(item.language.clone());
        }
    }
    out.sort_by_key(|language| !language.eq_ignore_ascii_case("SFX"));
    out
}

fn language_to_show(media: &[ResolvedMedia], preferred: Option<&str>) -> String {
    let languages = languages_of(media);
    let has = |name: &str| {
        languages
            .iter()
            .find(|language| language.eq_ignore_ascii_case(name))
            .cloned()
    };
    preferred
        .and_then(has)
        .or_else(|| has("SFX"))
        .or_else(|| has("English(US)"))
        .or_else(|| has("English(UK)"))
        .or_else(|| languages.first().cloned())
        .unwrap_or_else(|| "SFX".to_string())
}

fn media_for_language(media: &[ResolvedMedia], language: &str) -> Vec<ResolvedMedia> {
    let exact: Vec<ResolvedMedia> = media
        .iter()
        .filter(|item| item.language.eq_ignore_ascii_case(language))
        .cloned()
        .collect();
    if !exact.is_empty() {
        return exact;
    }
    let sfx: Vec<ResolvedMedia> = media
        .iter()
        .filter(|item| item.language.eq_ignore_ascii_case("SFX"))
        .cloned()
        .collect();
    if !sfx.is_empty() {
        return sfx;
    }
    media.to_vec()
}

fn sanitize(name: &str) -> String {
    let cleaned: String = name
        .chars()
        .map(|ch| match ch {
            '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|' => '_',
            ch => ch,
        })
        .collect();
    let trimmed = cleaned.trim().trim_matches('.');
    if trimmed.is_empty() {
        "sound".to_string()
    } else {
        trimmed.to_string()
    }
}

fn file_json(name: &str, path: &Path, pcm: &DecodedPcm) -> Value {
    let frames = pcm.frame_count();
    let seconds = if pcm.sample_rate == 0 {
        0.0
    } else {
        frames as f64 / f64::from(pcm.sample_rate)
    };
    json!({
        "name": name,
        "path": path.display().to_string(),
        "seconds": (seconds * 100.0).round() / 100.0,
        "channels": pcm.channels,
        "sampleRate": pcm.sample_rate,
    })
}

fn write_wav(path: &Path, pcm: &DecodedPcm) -> Result<()> {
    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            std::fs::create_dir_all(parent)?;
        }
    }
    let channels = pcm.channels.max(1);
    let block_align = u32::from(channels) * 2;
    let byte_rate = pcm.sample_rate.saturating_mul(block_align);
    let data_len = (pcm.samples.len() * 2) as u32;
    let mut buffer = Vec::with_capacity(44 + pcm.samples.len() * 2);
    buffer.extend_from_slice(b"RIFF");
    buffer.extend_from_slice(&(36 + data_len).to_le_bytes());
    buffer.extend_from_slice(b"WAVE");
    buffer.extend_from_slice(b"fmt ");
    buffer.extend_from_slice(&16u32.to_le_bytes());
    buffer.extend_from_slice(&1u16.to_le_bytes());
    buffer.extend_from_slice(&channels.to_le_bytes());
    buffer.extend_from_slice(&pcm.sample_rate.to_le_bytes());
    buffer.extend_from_slice(&byte_rate.to_le_bytes());
    buffer.extend_from_slice(&(block_align as u16).to_le_bytes());
    buffer.extend_from_slice(&16u16.to_le_bytes());
    buffer.extend_from_slice(b"data");
    buffer.extend_from_slice(&data_len.to_le_bytes());
    for sample in &pcm.samples {
        buffer.extend_from_slice(&sample.to_le_bytes());
    }
    std::fs::write(path, buffer)?;
    Ok(())
}

fn emit(value: &Value) -> bool {
    let mut out = std::io::stdout().lock();
    if serde_json::to_writer(&mut out, value).is_err() {
        return false;
    }
    if out.write_all(b"\n").is_err() {
        return false;
    }
    out.flush().is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn music_package_becomes_a_track() {
        let track = track_from_package(
            "/game/tags/sound/music/a10/mus_07_messhall_encounter_end-sound",
        )
        .expect("track");
        assert_eq!(track.name, "mus_07_messhall_encounter_end");
        assert_eq!(track.folder, "music/a10");
        assert_eq!(track.kind, "music");
    }

    #[test]
    fn scripted_voice_becomes_dialogue() {
        let track = track_from_package(
            "/game/tags/sound/scripted/vo_scr_m02halo/m02_00070_cortana-sound",
        )
        .expect("line");
        assert_eq!(track.name, "m02_00070_cortana");
        assert_eq!(track.folder, "scripted/vo_scr_m02halo");
        assert_eq!(track.kind, "dialogue");
    }

    #[test]
    fn vehicle_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/vehicles/warthog/engine/wh_engine_idle-sound",
        )
        .expect("vehicle");
        assert_eq!(track.name, "wh_engine_idle");
        assert_eq!(track.folder, "vehicles/warthog/engine");
        assert_eq!(track.kind, "vehicle");
    }

    #[test]
    fn looping_vehicle_tag_drops_its_group_suffix() {
        let track = track_from_package(
            "/game/tags/sound/vehicles/banshee/banshee_boost_left-sound_looping",
        )
        .expect("loop");
        assert_eq!(track.name, "banshee_boost_left");
        assert_eq!(track.folder, "vehicles/banshee");
        assert_eq!(track.kind, "vehicle");
    }

    #[test]
    fn weapon_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/weapons/assault_rifle/ar_fire-sound",
        )
        .expect("weapon");
        assert_eq!(track.name, "ar_fire");
        assert_eq!(track.folder, "weapons/assault_rifle");
        assert_eq!(track.kind, "weapon");
    }

    #[test]
    fn character_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/characters/elite/elite_death-sound",
        )
        .expect("character");
        assert_eq!(track.name, "elite_death");
        assert_eq!(track.folder, "characters/elite");
        assert_eq!(track.kind, "character");
    }

    #[test]
    fn dialog_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/dialog/combat/marine_idle-sound",
        )
        .expect("dialog");
        assert_eq!(track.name, "marine_idle");
        assert_eq!(track.folder, "dialog/combat");
        assert_eq!(track.kind, "dialog");
    }

    #[test]
    fn sandbox_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/005_sandbox/006_character/footstep-sound",
        )
        .expect("sandbox");
        assert_eq!(track.name, "footstep");
        assert_eq!(track.folder, "005_sandbox/006_character");
        assert_eq!(track.kind, "sandbox");
    }

    #[test]
    fn device_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/device_machines/doors_lifts/door_open-sound",
        )
        .expect("device");
        assert_eq!(track.name, "door_open");
        assert_eq!(track.folder, "device_machines/doors_lifts");
        assert_eq!(track.kind, "device");
    }

    #[test]
    fn levels_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/levels/a15/ambience-sound",
        )
        .expect("levels");
        assert_eq!(track.name, "ambience");
        assert_eq!(track.folder, "levels/a15");
        assert_eq!(track.kind, "levels");
    }

    #[test]
    fn materials_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/materials/brittle/impact-sound",
        )
        .expect("materials");
        assert_eq!(track.name, "impact");
        assert_eq!(track.folder, "materials/brittle");
        assert_eq!(track.kind, "materials");
    }

    #[test]
    fn ui_package_keeps_its_name_when_it_sits_in_the_root() {
        let track = track_from_package(
            "/game/tags/sound/ui/ui_seraph_fuelrodcannon_ready-sound",
        )
        .expect("ui");
        assert_eq!(track.name, "ui_seraph_fuelrodcannon_ready");
        assert_eq!(track.folder, "ui");
        assert_eq!(track.kind, "ui");
    }

    #[test]
    fn visual_fx_package_keeps_its_folder_path() {
        let track = track_from_package(
            "/game/tags/sound/visual_fx/gasoline_fire/loop-sound",
        )
        .expect("visual_fx");
        assert_eq!(track.name, "loop");
        assert_eq!(track.folder, "visual_fx/gasoline_fire");
        assert_eq!(track.kind, "visual_fx");
    }

    #[test]
    fn non_audio_packages_are_ignored() {
        assert!(track_from_package("/game/tags/sound/ambience/wind-sound").is_none());
    }

    #[test]
    fn sanitize_strips_path_characters() {
        assert_eq!(sanitize("ambient/expl:1"), "ambient_expl_1");
        assert_eq!(sanitize(" "), "sound");
    }

    #[test]
    fn sfx_is_preferred_for_music() {
        let media = vec![
            sample("Chinese(PRC)"),
            sample("SFX"),
            sample("English(US)"),
        ];
        assert_eq!(language_to_show(&media, None), "SFX");
        assert_eq!(language_to_show(&media, Some("English(US)")), "English(US)");
    }

    fn sample(language: &str) -> ResolvedMedia {
        ResolvedMedia {
            name: "cue".into(),
            language: language.into(),
            media_id: 1,
            location: MediaLocation::Loose("Media/1/1.wem".into()),
        }
    }
}
