//! Extracts Campaign Evolved locres text into one JSON file per language.
//!
//! Packed paths look like
//! `Meteorite/Content/Localization/<catalog>/<language>/<catalog>.locres`.
//!
//! ```text
//! halomeister-text --paks <Meteorite/Content/Paks> --out <json-dir> --catalog Game
//! halomeister-text --paks <Meteorite/Content/Paks> --out <json-dir> --catalog Subtitles
//! ```
//!
//! Each output file is `<language>.json`:
//! `{"language":"zh-TW","entries":[{"namespace":"","key":"...","text":"..."}]}`

mod locres;

use std::fs::File;
use std::io::Write;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use blam_tags::iostore::pak::PakSet;
use serde_json::{json, Value};

struct FileRow {
    language: String,
    path: String,
}

struct Session {
    paks: PakSet,
    files: Vec<FileRow>,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("{error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let mut args = std::env::args().skip(1);
    let mut paks = None;
    let mut out = None;
    let mut catalog = "Game".to_string();
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--paks" => paks = args.next().map(PathBuf::from),
            "--out" => out = args.next().map(PathBuf::from),
            "--catalog" => {
                catalog = args.next().context("--catalog needs a name")?;
            }
            other => bail!("unknown argument '{other}'"),
        }
    }
    if catalog.is_empty() || catalog.contains(['/', '\\', '.']) {
        bail!("catalog name is not a single folder name");
    }
    let paks = paks.context("--paks is required")?;
    let out = out.context("--out is required")?;
    let mut session = Session::open(&paks, &catalog)?;
    let written = session.export(&out)?;
    println!("wrote {written} language files to {}", out.display());
    Ok(())
}

impl Session {
    fn open(paks_root: &Path, catalog: &str) -> Result<Self> {
        let paks = PakSet::open_dir(paks_root)
            .with_context(|| format!("could not read {}", paks_root.display()))?;
        let mut files = Vec::new();
        for path in paks.paths() {
            let Some(language) = catalog_language(path, catalog) else {
                continue;
            };
            files.push(FileRow {
                language,
                path: path.clone(),
            });
        }
        files.sort_by(|left, right| {
            left.language
                .cmp(&right.language)
                .then_with(|| left.path.cmp(&right.path))
        });
        files.dedup_by(|left, right| left.path == right.path);
        if files.is_empty() {
            bail!("no {catalog}.locres files found under {}", paks_root.display());
        }
        Ok(Self { paks, files })
    }

    fn export(&mut self, out_dir: &Path) -> Result<usize> {
        std::fs::create_dir_all(out_dir)
            .with_context(|| format!("could not create {}", out_dir.display()))?;
        let jobs: Vec<(String, String)> = self
            .files
            .iter()
            .map(|file| (file.language.clone(), file.path.clone()))
            .collect();
        for (language, path) in &jobs {
            let bytes = self.paks.read(path)?;
            let entries = locres::parse(&bytes)
                .with_context(|| format!("could not parse {path}"))?;
            let rows: Vec<Value> = entries
                .iter()
                .map(|entry| {
                    json!({
                        "namespace": entry.namespace,
                        "key": entry.key,
                        "text": entry.text,
                    })
                })
                .collect();
            let body = json!({"language": language, "entries": rows});
            let dest = out_dir.join(format!("{language}.json"));
            let temporary = out_dir.join(format!("{language}.json.tmp"));
            {
                let mut file = File::create(&temporary)
                    .with_context(|| format!("could not write {}", temporary.display()))?;
                serde_json::to_writer(&mut file, &body)?;
                file.flush()?;
            }
            std::fs::rename(&temporary, &dest)
                .with_context(|| format!("could not replace {}", dest.display()))?;
            println!("{language}: {} entries", rows.len());
        }
        Ok(jobs.len())
    }
}

/// `.../Localization/Subtitles/zh-TW/Subtitles.locres` -> `zh-TW`.
fn catalog_language(path: &str, catalog: &str) -> Option<String> {
    let normalized = path.replace('\\', "/");
    let marker = format!("/Localization/{catalog}/");
    let at = normalized.find(&marker)?;
    let rest = &normalized[at + marker.len()..];
    let (language, file) = rest.split_once('/')?;
    let expected = format!("{catalog}.locres");
    if !file.eq_ignore_ascii_case(&expected) || language.is_empty() || language.contains('/') {
        return None;
    }
    Some(language.to_string())
}
