use crate::ensure_demo_squads;
use anyhow::{Context, Result, anyhow, bail};
use blam_tags::fields::{TagFieldData, TagReferenceData};
use blam_tags::iostore::IoStoreArchive;
use blam_tags::TagFile;
use std::collections::{BTreeSet, HashSet};
use std::path::Path;

const MAX_PALETTE_ENTRIES: usize = 256;
const VEHICLE_GROUP: u32 = u32::from_be_bytes(*b"vehi");
const WEAPON_GROUP: u32 = u32::from_be_bytes(*b"weap");

pub struct ExpandReport {
    pub scenarios_seen: usize,
    pub scenarios_changed: usize,
    pub vehicle_catalog: usize,
    pub weapon_catalog: usize,
    pub vehicle_added_total: usize,
    pub weapon_added_total: usize,
    pub ally_added: usize,
    pub ally_from_hostile_fallback: usize,
    pub hostile_added: usize,
    pub lines: Vec<String>,
}

pub fn expand_all_mission_palettes(
    archives: &[IoStoreArchive],
    output: &Path,
    dry_run: bool,
) -> Result<ExpandReport> {
    let vehicles = collect_tag_paths(archives, "vehicle")?;
    let weapons = collect_tag_paths(archives, "weapon")?;
    let scenarios = collect_scenario_entries(archives)?;
    if vehicles.is_empty() {
        bail!("no vehicle tags found under Meteorite/Content/Tags");
    }
    if weapons.is_empty() {
        bail!("no weapon tags found under Meteorite/Content/Tags");
    }
    if scenarios.is_empty() {
        bail!("no scenario tags found under Meteorite/Content/Tags");
    }

    let mut report = ExpandReport {
        scenarios_seen: scenarios.len(),
        scenarios_changed: 0,
        vehicle_catalog: vehicles.len(),
        weapon_catalog: weapons.len(),
        vehicle_added_total: 0,
        weapon_added_total: 0,
        ally_added: 0,
        ally_from_hostile_fallback: 0,
        hostile_added: 0,
        lines: Vec::new(),
    };
    report.lines.push(format!(
        "Catalog: {} vehicle(s), {} weapon(s); {} scenario(s) to process (palettes + hm_ally/hm_hostile)",
        vehicles.len(),
        weapons.len(),
        scenarios.len()
    ));

    let mut edited: Vec<(usize, String, Vec<u8>)> = Vec::new();
    for (archive_index, rel_path, tag_path) in &scenarios {
        let bytes = archives[*archive_index]
            .read(rel_path)
            .with_context(|| format!("could not read {rel_path}"))?;
        let mut tag = TagFile::read_from_bytes(&bytes)
            .with_context(|| format!("could not parse {rel_path}"))?;
        let vehicle_added =
            ensure_palette(&mut tag, "vehicle palette", VEHICLE_GROUP, &vehicles)?;
        let weapon_added =
            ensure_palette(&mut tag, "weapon palette", WEAPON_GROUP, &weapons)?;
        let squads = ensure_demo_squads::ensure_demo_squads_on_tag(&mut tag, tag_path)?;
        if vehicle_added == 0 && weapon_added == 0 && !squads.changed {
            continue;
        }
        report.scenarios_changed += 1;
        report.vehicle_added_total += vehicle_added;
        report.weapon_added_total += weapon_added;
        report.ally_added += squads.ally_added;
        report.ally_from_hostile_fallback += squads.ally_from_hostile_fallback;
        report.hostile_added += squads.hostile_added;
        report.lines.push(format!(
            "{tag_path}: +{vehicle_added} vehicle(s), +{weapon_added} weapon(s)"
        ));
        report.lines.extend(squads.lines);
        if dry_run {
            continue;
        }
        let serialized = tag
            .write_to_bytes()
            .with_context(|| format!("could not serialize {rel_path}"))?;
        TagFile::read_from_bytes(&serialized)
            .with_context(|| format!("serialized verification failed for {rel_path}"))?;
        edited.push((*archive_index, rel_path.clone(), serialized));
    }

    if dry_run {
        report.lines.push("Dry run: no overlay files written.".to_owned());
        return Ok(report);
    }
    if edited.is_empty() {
        report.lines.push(
            "Every scenario already contained the full catalogs and demo squads.".to_owned(),
        );
        return Ok(report);
    }

    let overrides: Vec<(&IoStoreArchive, &str, &[u8])> = edited
        .iter()
        .map(|(archive, path, bytes)| (&archives[*archive], path.as_str(), bytes.as_slice()))
        .collect();
    blam_tags::iostore::writer::write_mod_container_ex(&overrides, &[], output)
        .with_context(|| format!("could not write {}", output.display()))?;
    report.lines.push(format!(
        "Wrote {} edited scenario(s) to {}",
        edited.len(),
        output.display()
    ));
    Ok(report)
}

fn ensure_palette(
    tag: &mut TagFile,
    block_name: &str,
    group_tag: u32,
    catalog: &BTreeSet<String>,
) -> Result<usize> {
    let existing = read_palette_paths(tag, block_name)?;
    let missing: Vec<&String> = catalog
        .iter()
        .filter(|path| !existing.contains(path.as_str()))
        .collect();
    if missing.is_empty() {
        return Ok(0);
    }
    if existing.len() + missing.len() > MAX_PALETTE_ENTRIES {
        bail!(
            "{block_name} would exceed {MAX_PALETTE_ENTRIES} entries (have {}, need {})",
            existing.len(),
            missing.len()
        );
    }

    let mut added = 0usize;
    for path in missing {
        let index = {
            let mut root = tag.root_mut();
            let mut field = root
                .field_path_mut(block_name)
                .ok_or_else(|| anyhow!("{block_name} block was not found"))?;
            let mut block = field
                .as_block_mut()
                .ok_or_else(|| anyhow!("{block_name} is not a block"))?;
            block.add_element()
        };
        let reference_path = format!("{block_name}[{index}]/name");
        let mut root = tag.root_mut();
        let mut field = root
            .field_path_mut(&reference_path)
            .ok_or_else(|| anyhow!("{reference_path} was not found after add_element"))?;
        field
            .set(TagFieldData::TagReference(TagReferenceData {
                group_tag_and_name: Some((group_tag, path.clone())),
            }))
            .map_err(|error| anyhow!("failed to set {reference_path}: {error:?}"))?;
        added += 1;
    }
    Ok(added)
}

fn read_palette_paths(tag: &TagFile, block_name: &str) -> Result<HashSet<String>> {
    let root = tag.root();
    let field = root
        .field_path(block_name)
        .ok_or_else(|| anyhow!("{block_name} block was not found"))?;
    let block = field
        .as_block()
        .ok_or_else(|| anyhow!("{block_name} is not a block"))?;
    let mut paths = HashSet::new();
    for index in 0..block.len() {
        let Some(element) = block.element(index) else {
            continue;
        };
        let Some(name_field) = element.field("name") else {
            continue;
        };
        let Some(TagFieldData::TagReference(reference)) = name_field.value() else {
            continue;
        };
        let Some((_, path)) = reference.group_tag_and_name else {
            continue;
        };
        paths.insert(normalize_tag_path(&path));
    }
    Ok(paths)
}

fn collect_tag_paths(archives: &[IoStoreArchive], group_name: &str) -> Result<BTreeSet<String>> {
    let suffix = format!("-{group_name}.ubulk");
    let mut paths = BTreeSet::new();
    for archive in archives {
        for entry in archive.ublock_entries() {
            let normalized = entry.path.replace('\\', "/").to_ascii_lowercase();
            let Some(tag_path) = tag_path_from_ubulk(&normalized, &suffix) else {
                continue;
            };
            paths.insert(tag_path);
        }
    }
    Ok(paths)
}

fn collect_scenario_entries(archives: &[IoStoreArchive]) -> Result<Vec<(usize, String, String)>> {
    let suffix = "-scenario.ubulk";
    let mut found = Vec::new();
    for (archive_index, archive) in archives.iter().enumerate() {
        for entry in archive.ublock_entries() {
            let normalized = entry.path.replace('\\', "/").to_ascii_lowercase();
            let Some(tag_path) = tag_path_from_ubulk(&normalized, suffix) else {
                continue;
            };
            // Prefer later containers (higher pakchunk / _P overlays) by
            // replacing earlier hits for the same scenario identity.
            if let Some(existing) = found
                .iter()
                .position(|(_, _, path): &(usize, String, String)| path == &tag_path)
            {
                found[existing] = (archive_index, entry.path.clone(), tag_path);
            } else {
                found.push((archive_index, entry.path.clone(), tag_path));
            }
        }
    }
    found.sort_by(|left, right| left.2.cmp(&right.2));
    Ok(found)
}

fn tag_path_from_ubulk(normalized_path: &str, suffix: &str) -> Option<String> {
    const PREFIX: &str = "meteorite/content/tags/";
    let rest = normalized_path.strip_prefix(PREFIX)?;
    let stem = rest.strip_suffix(suffix)?;
    if stem.is_empty() {
        return None;
    }
    Some(normalize_tag_path(stem))
}

fn normalize_tag_path(path: &str) -> String {
    path.trim_matches(['\\', '/', '.'])
        .replace('/', "\\")
        .to_ascii_lowercase()
}
