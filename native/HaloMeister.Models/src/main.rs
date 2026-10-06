//! Character model browser for Halo: Campaign Evolved.
//!
//! Lists `objects/characters` `.model`, `.physics_model`, and `.collision_model`
//! tags out of the IoStore, then builds a preview the same way Baboon does:
//! mesh-sync regions drive which skeletal meshes a `.model` shows, and only
//! that tag carries variants and the animation graph. Collision and physics
//! are the tag's own geometry, posed with the sibling skeleton.

use std::collections::{HashMap, HashSet};
use std::io::{BufRead, Cursor, Write};
use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use base64::Engine;
use blam_tags::{Animation, AnimationGraph};

mod skin;
use blam_tags::iostore::container_header::EIoContainerHeaderVersion;
use blam_tags::iostore::skeletal_mesh::SkeletalMesh;
use blam_tags::iostore::static_mesh::StaticMesh;
use blam_tags::iostore::ue_types::{EIoStoreTocVersion, FPackageObjectIndex};
use blam_tags::iostore::unversioned::{
    MeshRef, MeshSyncRegions, MeshTransform, Permutation,
};
use blam_tags::iostore::usmap::Usmap;
use blam_tags::iostore::zen::FZenPackageHeader;
use blam_tags::iostore::IoStoreArchive;
use blam_tags::math::{Matrix4, RealPoint3d, RealQuaternion, RealVector3d};
use blam_tags::{JmsFile, JmsNode, Model, TagFile};
use serde::Serialize;
use serde_json::{json, Value};

const TOC_VERSION: EIoStoreTocVersion = EIoStoreTocVersion::ReplaceIoChunkHashWithIoHash;
const HEADER_VERSION: EIoContainerHeaderVersion = EIoContainerHeaderVersion::SoftPackageReferences;
/// Safety cap for meshes far past a character LOD. Under this, every triangle is kept.
/// Above it, vertices are welded on a grid so the surface stays closed instead of turning into holes.
const MAX_PREVIEW_TRIS: usize = 200_000;
const MESHSYNC_COMP_CLASS: &str = "/Script/BlamSynchronization.BlamMeshSynchronizationComponent";
const MESHSYNC_COMP_BASE_CLASS: &str =
    "/Script/BlamSynchronization.BlamMeshSynchronizationComponentBase";

#[derive(Clone)]
struct Located {
    archive: usize,
    path: String,
}

struct Session {
    archives: Vec<IoStoreArchive>,
    /// Lowercase tag key after `tags/`, without `.ubulk`.
    ubulks: HashMap<String, Located>,
    /// Lowercase `/game/...` package name.
    uassets: HashMap<String, Located>,
    sync_das: Vec<Located>,
    actors: Vec<Located>,
    usmap: Option<Usmap>,
    /// model id -> decoded world mesh-sync, after the first successful lookup.
    sync_cache: HashMap<String, MeshSyncRegions>,
    sync_miss: HashSet<String>,
    /// Skin palette from the last model inspect. Pose playback uses the same bones.
    rig: Option<skin::CachedRig>,
    rig_id: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FileJson {
    id: String,
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
    let files = session.files();
    eprintln!("character models: {}", files.len());
    if !emit(&json!({"ok": true, "ready": true, "files": files})) {
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
        let response = match command.get("cmd").and_then(Value::as_str) {
            Some("inspect") => session.inspect(&command),
            Some("pose") => session.pose(&command),
            Some(other) => Err(anyhow::anyhow!("unknown command '{other}'")),
            None => Err(anyhow::anyhow!("missing command")),
        };
        let body = match response {
            Ok(value) => value,
            Err(error) => json!({"ok": false, "error": format!("{error:#}")}),
        };
        if !emit(&body) {
            break;
        }
    }
    Ok(())
}

fn emit(value: &Value) -> bool {
    let mut out = std::io::stdout().lock();
    serde_json::to_writer(&mut out, value).is_ok() && writeln!(out).is_ok() && out.flush().is_ok()
}

impl Session {
    fn open(paks_root: &Path) -> Result<Self> {
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
        let mut ubulks = HashMap::new();
        let mut uassets = HashMap::new();
        let mut sync_das = Vec::new();
        let mut actors = Vec::new();
        for utoc in &utocs {
            let Ok(archive) = IoStoreArchive::open(utoc) else {
                eprintln!("skipped {}", utoc.display());
                continue;
            };
            let archive_index = archives.len();
            for entry in archive.entries() {
                let path = entry.path.replace('\\', "/");
                let lower = path.to_ascii_lowercase();
                if lower.ends_with(".ubulk") {
                    if let Some(key) = tag_key(&lower) {
                        ubulks.insert(key, Located { archive: archive_index, path: path.clone() });
                    }
                } else if lower.ends_with(".uasset") {
                    if let Some(package) = package_key(&lower) {
                        if is_mesh_sync_asset(&lower) {
                            sync_das.push(Located { archive: archive_index, path: path.clone() });
                        } else if lower.ends_with("actor.uasset") {
                            actors.push(Located { archive: archive_index, path: path.clone() });
                        }
                        uassets.insert(package, Located { archive: archive_index, path });
                    }
                }
            }
            archives.push(archive);
        }
        if archives.is_empty() {
            bail!("no readable IoStore containers in {}", paks_root.display());
        }
        let usmap = Usmap::meteorite().ok();
        eprintln!(
            "indexed {} tags, {} packages, {} mesh-sync assets",
            ubulks.len(),
            uassets.len(),
            sync_das.len()
        );
        Ok(Self {
            archives,
            ubulks,
            uassets,
            sync_das,
            actors,
            usmap,
            sync_cache: HashMap::new(),
            sync_miss: HashSet::new(),
            rig: None,
            rig_id: String::new(),
        })
    }

    fn files(&self) -> Vec<FileJson> {
        let mut files = Vec::new();
        for key in self.ubulks.keys() {
            let Some(file) = file_from_key(key) else { continue };
            files.push(file);
        }
        files.sort_by(|a, b| {
            a.folder
                .cmp(&b.folder)
                .then(a.name.cmp(&b.name))
                .then(kind_rank(&a.kind).cmp(&kind_rank(&b.kind)))
        });
        files
    }

    fn inspect(&mut self, command: &Value) -> Result<Value> {
        let id = command
            .get("id")
            .and_then(Value::as_str)
            .context("inspect requires id")?
            .to_ascii_lowercase();
        let kind = kind_of(&id).context("not a character model tag")?;
        let variant = command.get("variant").and_then(Value::as_str).unwrap_or("");
        let picks = command.get("picks").cloned().unwrap_or(Value::Null);
        match kind {
            "model" => self.inspect_model(&id, variant, &picks),
            "physics_model" => self.inspect_physics(&id),
            "collision_model" => self.inspect_collision(&id),
            _ => bail!("unsupported kind {kind}"),
        }
    }

    fn inspect_model(&mut self, id: &str, variant: &str, picks: &Value) -> Result<Value> {
        let bytes = self.read_tag(id)?;
        let tag = TagFile::read_from_bytes(&bytes).context("reading model tag")?;
        let model = Model::from_tag(&tag).map_err(|error| anyhow::anyhow!("{error}"))?;
        let chosen = model.variant(variant);
        let variant_name = chosen.map(|item| item.name.clone()).unwrap_or_default();

        let mut variant_regions: HashMap<String, String> = HashMap::new();
        if let Some(item) = chosen {
            for region in &item.regions {
                if let Some(perm) = region.permutation_names.first() {
                    if !perm.is_empty() {
                        variant_regions.insert(region.name.clone(), perm.clone());
                    }
                }
            }
        }

        let sync = self.mesh_sync_for(id);
        let skeleton_regions = self
            .sibling_skeleton(id)
            .as_ref()
            .map(skeleton_region_json)
            .unwrap_or_default();
        let (regions, mesh) = if let Some(sync) = sync {
            let regions = if skeleton_regions.is_empty() {
                sync_region_json(&sync, &variant_regions, picks)
            } else {
                select_region_json(&skeleton_regions, &variant_regions, picks)
            };
            let mesh = self.meshes_for_sync(id, &sync, &regions, picks);
            (regions, mesh)
        } else if !skeleton_regions.is_empty() {
            let regions = select_region_json(&skeleton_regions, &variant_regions, picks);
            let mesh = self.meshes_for_sync(id, &MeshSyncRegions::default(), &regions, picks);
            (regions, mesh)
        } else {
            let regions = variant_region_json(&model, &variant_name, &variant_regions, picks);
            let mesh = self.fallback_meshes(id);
            (regions, mesh)
        };

        let animations = self.animations_of(&tag);
        let variants: Vec<Value> = model
            .variants
            .iter()
            .map(|item| json!({"name": item.name}))
            .collect();
        Ok(preview_json("model", id, &variant_name, &variants, &regions, &animations, mesh))
    }

    fn inspect_physics(&mut self, id: &str) -> Result<Value> {
        let bytes = self.read_tag(id)?;
        let tag = TagFile::read_from_bytes(&bytes).context("reading physics model")?;
        let skeleton = self.sibling_skeleton(id);
        let mut jms = match &skeleton {
            Some(skel) => JmsFile::from_physics_model_with_skeleton(
                &tag,
                &JmsFile::skeleton_rest_pose(skel).unwrap_or_default(),
            ),
            None => JmsFile::from_physics_model(&tag),
        }
        .map_err(|error| anyhow::anyhow!("{error}"))?;
        if let Some(skel) = &skeleton {
            jms.reorient_for_campaign_evolved(skel);
        }
        Ok(preview_json("physics_model", id, "", &[], &[], &[], physics_mesh(&jms)))
    }

    fn inspect_collision(&mut self, id: &str) -> Result<Value> {
        let bytes = self.read_tag(id)?;
        let tag = TagFile::read_from_bytes(&bytes).context("reading collision model")?;
        let skeleton = self.sibling_skeleton(id);
        let nodes: Option<Vec<JmsNode>> = skeleton
            .as_ref()
            .and_then(|skel| JmsFile::skeleton_rest_pose(skel).ok());
        let jms = match &nodes {
            Some(nodes) => JmsFile::from_collision_model_with_skeleton(&tag, nodes),
            None => JmsFile::from_collision_model(&tag),
        }
        .map_err(|error| anyhow::anyhow!("{error}"))?;
        let positions: Vec<[f32; 3]> = jms
            .vertices
            .iter()
            .map(|vertex| [vertex.position.x, vertex.position.y, vertex.position.z])
            .collect();
        let indices: Vec<u32> = jms.triangles.iter().flat_map(|tri| tri.v).collect();
        Ok(preview_json(
            "collision_model",
            id,
            "",
            &[],
            &[],
            &[],
            pack_mesh(&positions, &[], &indices, &[], &[]),
        ))
    }

    fn animations_of(&self, model: &TagFile) -> Vec<Value> {
        let Some(path) = model.root().read_tag_ref_path("animation") else {
            return Vec::new();
        };
        let Some(bytes) = self.read_ref(&path, "model_animation_graph") else {
            return Vec::new();
        };
        let Ok(tag) = TagFile::read_from_bytes(&bytes) else {
            return Vec::new();
        };
        animation_rows(&tag, self, 0)
    }

    fn sibling_skeleton(&self, id: &str) -> Option<TagFile> {
        let model_id = sibling_model_id(id)?;
        let model_bytes = self.read_tag(&model_id).ok()?;
        let model = TagFile::read_from_bytes(&model_bytes).ok()?;
        let path = model.root().read_tag_ref_path("skeleton model")?;
        let bytes = self.read_ref(&path, "skeleton_model")?;
        TagFile::read_from_bytes(&bytes).ok()
    }

    fn mesh_sync_for(&mut self, model_id: &str) -> Option<MeshSyncRegions> {
        if let Some(found) = self.sync_cache.get(model_id) {
            return Some(found.clone());
        }
        if self.sync_miss.contains(model_id) {
            return None;
        }
        let regions = self.mesh_sync_by_name(model_id);
        match regions {
            Some(regions) => {
                self.sync_cache.insert(model_id.to_string(), regions.clone());
                Some(regions)
            }
            None => {
                self.sync_miss.insert(model_id.to_string());
                None
            }
        }
    }

    /// Data assets whose filename contains a mesh-sync spelling
    /// (`MeshSynchronization`, `MeshSyncronisation`, `Mesh_Sync`, …) that import
    /// this model, then an `*actor.uasset` that imports that data asset.
    fn mesh_sync_by_name(&self, model_id: &str) -> Option<MeshSyncRegions> {
        let token = folder_token(model_id);
        let actor_tokens = model_tokens(model_id);
        let usmap = self.usmap.as_ref()?;
        let mut matched_da: Option<String> = None;
        for located in &self.sync_das {
            let lower = located.path.to_ascii_lowercase();
            if !token.is_empty() && !lower.contains(&token) {
                continue;
            }
            let Ok(bytes) = self.archives[located.archive].read(&located.path) else {
                continue;
            };
            let Ok(header) = header_of(&bytes) else { continue };
            let imports_model = header.imported_package_names.iter().any(|package| {
                package
                    .replace('\\', "/")
                    .to_ascii_lowercase()
                    .ends_with(model_id)
            });
            if imports_model {
                matched_da = Some(file_stem(&lower));
                break;
            }
        }
        let da_name = matched_da?;
        let comp_class = FPackageObjectIndex::create_script_import(MESHSYNC_COMP_CLASS);
        let base_class = FPackageObjectIndex::create_script_import(MESHSYNC_COMP_BASE_CLASS);

        let mut best: Option<(bool, usize, MeshSyncRegions)> = None;
        for located in &self.actors {
            let lower = located.path.to_ascii_lowercase();
            if !actor_tokens.is_empty() && !actor_tokens.iter().any(|token| lower.contains(token)) {
                continue;
            }
            let Ok(bytes) = self.archives[located.archive].read(&located.path) else {
                continue;
            };
            let Ok(header) = header_of(&bytes) else { continue };
            let imports_da = header.imported_package_names.iter().any(|package| {
                file_stem(&package.replace('\\', "/").to_ascii_lowercase()) == da_name
            });
            if !imports_da {
                continue;
            }
            let Some(export) = header
                .export_map
                .iter()
                .find(|entry| header.name_map.get(entry.object_name).contains("MeshSynchronization"))
                .or_else(|| header.find_export_of_class(comp_class))
                .or_else(|| header.find_export_of_class(base_class))
            else {
                continue;
            };
            let start = header.summary.header_size as usize + export.cooked_serial_offset as usize;
            let end = start + export.cooked_serial_size as usize;
            let Some(body) = bytes.get(start..end) else { continue };
            let names = header.name_map.copy_raw_names();
            let Ok(regions) = MeshSyncRegions::from_component_export(body, &names, usmap) else {
                continue;
            };
            if regions.regions.is_empty() {
                continue;
            }
            let meshes = regions
                .regions
                .iter()
                .flat_map(|region| &region.permutations)
                .map(|perm| perm.skeletal_meshes.len() + perm.static_meshes.len())
                .sum();
            let score = (regions.is_world(), meshes);
            if best.as_ref().is_none_or(|current| score > (current.0, current.1)) {
                best = Some((regions.is_world(), meshes, regions));
            }
        }
        best.map(|(_, _, regions)| regions)
    }

    /// Skeletal meshes named for a permutation the mesh-sync list doesn't bind,
    /// such as `SK_Marine_Torso_01` for `torso_01`.
    fn packages_for_perm(&self, perm: &str) -> Vec<String> {
        let perm = perm.to_ascii_lowercase();
        if perm.is_empty() || perm == "default" || perm == "base" {
            return Vec::new();
        }
        let suffix = format!("_{perm}");
        let female = perm.contains("female");
        let mut hits: Vec<String> = self
            .uassets
            .keys()
            .filter(|package| {
                let stem = package.rsplit('/').next().unwrap_or("");
                stem.starts_with("sk_")
                    && stem.ends_with(&suffix)
                    && (female || !stem.contains("female"))
                    && !package.contains("/metahumans/")
                    && !package.contains("/developers/")
                    && !package.contains("/tags/")
                    && !stem_excluded(stem)
            })
            .cloned()
            .collect();
        hits.sort();
        hits.truncate(4);
        hits
    }


    fn meshes_for_sync(&mut self, id: &str, sync: &MeshSyncRegions, regions: &[Value], picks: &Value) -> PackedMesh {
        let mut rig = self
            .sibling_skeleton(id)
            .map(|tag| skin::CachedRig::from_skeleton(&tag))
            .unwrap_or_else(skin::CachedRig::empty);
        let mut positions = Vec::new();
        let mut normals = Vec::new();
        let mut indices = Vec::new();
        let mut joints = Vec::new();
        let mut weights = Vec::new();
        let mut seen = HashSet::new();
        let mut skeletal = Vec::new();
        let mut named = Vec::new();
        let mut rigid = Vec::new();
        for region in regions {
            let name = region.get("name").and_then(Value::as_str).unwrap_or("");
            let selected = region.get("selected").and_then(Value::as_str).unwrap_or("");
            let first = region
                .get("permutations")
                .and_then(Value::as_array)
                .and_then(|items| items.first())
                .and_then(Value::as_str)
                .unwrap_or("");
            let bound = bound_perm(sync, name, selected).or_else(|| {
                (selected.eq_ignore_ascii_case(first)).then(|| first_bound_perm(sync, name)).flatten()
            });
            if let Some(perm) = bound {
                for mesh in &perm.skeletal_meshes {
                    if preview_mesh_excluded(mesh) || !seen.insert(mesh_key(mesh)) {
                        continue;
                    }
                    if mesh_is_static(mesh) {
                        rigid.push(mesh);
                    } else {
                        skeletal.push(mesh);
                    }
                }
                for mesh in &perm.static_meshes {
                    if preview_mesh_excluded(mesh) || !seen.insert(mesh_key(mesh)) {
                        continue;
                    }
                    rigid.push(mesh);
                }
            } else if picked(picks, name).is_some() {
                for package in self.packages_for_perm(selected) {
                    if seen.insert(package.to_ascii_lowercase()) {
                        named.push(package);
                    }
                }
            }
        }
        for mesh in skeletal {
            self.append_skeletal(
                &mesh.package,
                &mut rig,
                &mut positions,
                &mut normals,
                &mut indices,
                &mut joints,
                &mut weights,
            );
        }
        for package in &named {
            self.append_skeletal(
                package,
                &mut rig,
                &mut positions,
                &mut normals,
                &mut indices,
                &mut joints,
                &mut weights,
            );
        }
        for mesh in rigid {
            self.append_rigid(
                mesh,
                &rig,
                &mut positions,
                &mut normals,
                &mut indices,
                &mut joints,
                &mut weights,
            );
        }
        self.store_rig(id, rig);
        pack_mesh(&positions, &normals, &indices, &joints, &weights)
    }

    fn fallback_meshes(&mut self, id: &str) -> PackedMesh {
        let stem = file_stem(id.trim_end_matches("-model"));
        let token = stem.split('_').next().unwrap_or(stem.as_str()).to_string();
        let mut hits: Vec<(i32, String)> = self
            .uassets
            .keys()
            .filter_map(|package| {
                let name = package.rsplit('/').next().unwrap_or("");
                if !name.starts_with("sk_") {
                    return None;
                }
                let score = mesh_score(name, &stem, &token);
                (score > 0).then_some((score, package.clone()))
            })
            .collect();
        hits.sort_by(|a, b| b.0.cmp(&a.0).then(a.1.cmp(&b.1)));
        hits.truncate(4);
        let mut rig = self
            .sibling_skeleton(id)
            .map(|tag| skin::CachedRig::from_skeleton(&tag))
            .unwrap_or_else(skin::CachedRig::empty);
        let mut positions = Vec::new();
        let mut normals = Vec::new();
        let mut indices = Vec::new();
        let mut joints = Vec::new();
        let mut weights = Vec::new();
        for (_, package) in hits {
            self.append_skeletal(
                &package,
                &mut rig,
                &mut positions,
                &mut normals,
                &mut indices,
                &mut joints,
                &mut weights,
            );
        }
        self.store_rig(id, rig);
        pack_mesh(&positions, &normals, &indices, &joints, &weights)
    }

    fn store_rig(&mut self, id: &str, rig: skin::CachedRig) {
        self.rig_id = id.to_string();
        self.rig = Some(rig);
    }

    fn pose(&mut self, command: &Value) -> Result<Value> {
        let id = command
            .get("id")
            .and_then(Value::as_str)
            .context("pose requires id")?
            .to_ascii_lowercase();
        let index = command
            .get("index")
            .and_then(Value::as_u64)
            .context("pose requires index")? as usize;
        if self.rig_id != id || self.rig.as_ref().is_none_or(|rig| rig.bones.is_empty()) {
            bail!("preview this model before playing an animation");
        }
        let model_bytes = self.read_tag(&id)?;
        let model = TagFile::read_from_bytes(&model_bytes).context("reading model tag")?;
        let jmad = self
            .animation_source(&model)
            .context("this model has no animation graph")?;
        let skeleton = self.sibling_skeleton(&id);
        let (pose, names) = skin::decode_pose(&jmad, skeleton.as_ref(), index)
            .map_err(anyhow::Error::msg)?;
        let rig = self.rig.as_ref().context("preview this model before playing an animation")?;
        let rows = rig.skin_rows(&pose, &names);
        let bones = rig.bones.len();
        Ok(json!({
            "ok": true,
            "frames": pose.frames.len(),
            "bones": bones,
            "matrices": encode_f32(&rows),
        }))
    }

    fn animation_source(&self, model: &TagFile) -> Option<TagFile> {
        let path = model.root().read_tag_ref_path("animation")?;
        let mut bytes = self.read_ref(&path, "model_animation_graph")?;
        for _ in 0..4 {
            let tag = TagFile::read_from_bytes(&bytes).ok()?;
            if Animation::new(&tag).ok().is_some_and(|animation| !animation.is_empty()) {
                return Some(tag);
            }
            let Some(parent) = tag.root().read_tag_ref_path("parent animation graph") else {
                return Some(tag);
            };
            bytes = self.read_ref(&parent, "model_animation_graph")?;
        }
        TagFile::read_from_bytes(&bytes).ok()
    }

    fn append_skeletal(
        &self,
        package: &str,
        rig: &mut skin::CachedRig,
        positions: &mut Vec<[f32; 3]>,
        normals: &mut Vec<[f32; 3]>,
        indices: &mut Vec<u32>,
        joints: &mut Vec<[u16; 4]>,
        weights: &mut Vec<[f32; 4]>,
    ) {
        let Some(bytes) = self.read_package(package) else {
            return;
        };
        let Ok(header) = header_of(&bytes) else {
            return;
        };
        let names = header.name_map.copy_raw_names();
        let Ok(mesh) = SkeletalMesh::from_package(&bytes, &names, header.summary.header_size as usize) else {
            return;
        };
        let (mesh_joints, mesh_weights) = rig.push_mesh(&mesh);
        let base = positions.len() as u32;
        for vertex in &mesh.vertices {
            positions.push(vertex.position);
            normals.push(vertex.normal);
        }
        joints.extend(mesh_joints);
        weights.extend(mesh_weights);
        indices.extend(mesh.indices.iter().map(|index| index + base));
    }

    fn append_rigid(
        &self,
        mesh_ref: &MeshRef,
        rig: &skin::CachedRig,
        positions: &mut Vec<[f32; 3]>,
        normals: &mut Vec<[f32; 3]>,
        indices: &mut Vec<u32>,
        joints: &mut Vec<[u16; 4]>,
        weights: &mut Vec<[f32; 4]>,
    ) {
        // A static piece is stored in its parent bone's local space. Drawing it
        // with no bone leaves the whole mesh piled on the origin, under the feet.
        let Some((slot, bind)) = rig.rigid_bind(&mesh_ref.parent_bone) else {
            return;
        };
        let Some(bytes) = self.read_package(&mesh_ref.package) else {
            return;
        };
        let Ok(header) = header_of(&bytes) else {
            return;
        };
        let Ok(mesh) = StaticMesh::from_package(&bytes, header.summary.header_size as usize) else {
            return;
        };
        let placed = bind * rel_matrix(&mesh_ref.rel_transform);
        let base = positions.len() as u32;
        for vertex in &mesh.vertices {
            positions.push(transform_point(&placed, vertex.position));
            normals.push(normalize_dir(transform_dir(&placed, vertex.normal)));
            joints.push([slot, 0, 0, 0]);
            weights.push([1.0, 0.0, 0.0, 0.0]);
        }
        indices.extend(mesh.indices.iter().map(|index| index + base));
    }

    fn read_tag(&self, id: &str) -> Result<Vec<u8>> {
        let located = self
            .ubulks
            .get(id)
            .with_context(|| format!("tag `{id}` is not in the game containers"))?;
        self.archives[located.archive]
            .read(&located.path)
            .with_context(|| format!("reading {}", located.path))
    }

    fn read_ref(&self, tag_path: &str, group: &str) -> Option<Vec<u8>> {
        let normal = tag_path.replace('\\', "/").trim_matches('/').to_ascii_lowercase();
        let key = format!("{normal}-{group}");
        let located = self.ubulks.get(&key)?;
        self.archives[located.archive].read(&located.path).ok()
    }

    fn read_package(&self, package: &str) -> Option<Vec<u8>> {
        let key = package.replace('\\', "/").to_ascii_lowercase();
        let key = key.strip_suffix(".uasset").unwrap_or(&key);
        let located = self.uassets.get(key)?;
        self.archives[located.archive].read(&located.path).ok()
    }
}


fn bound_perm<'a>(sync: &'a MeshSyncRegions, region: &str, perm: &str) -> Option<&'a Permutation> {
    sync.regions
        .iter()
        .find(|item| item.name.eq_ignore_ascii_case(region))?
        .permutations
        .iter()
        .find(|item| item.name.eq_ignore_ascii_case(perm))
}

fn first_bound_perm<'a>(sync: &'a MeshSyncRegions, region: &str) -> Option<&'a Permutation> {
    sync.regions
        .iter()
        .find(|item| item.name.eq_ignore_ascii_case(region))?
        .permutations
        .iter()
        .find(|perm| {
            perm.skeletal_meshes
                .iter()
                .chain(perm.static_meshes.iter())
                .any(|mesh| !preview_mesh_excluded(mesh))
        })
}

fn skeleton_region_json(skeleton: &TagFile) -> Vec<(String, Vec<String>)> {
    let Some(block) = skeleton.root().field_path("regions").and_then(|field| field.as_block()) else {
        return Vec::new();
    };
    (0..block.len())
        .filter_map(|index| {
            let element = block.element(index)?;
            let name = element.read_string_id("name")?;
            if name.is_empty() {
                return None;
            }
            let perms = element
                .field_path("permutations")
                .and_then(|field| field.as_block())
                .map(|perms| {
                    (0..perms.len())
                        .filter_map(|index| perms.element(index))
                        .filter_map(|perm| perm.read_string_id("name"))
                        .filter(|name| !name.is_empty())
                        .collect::<Vec<_>>()
                })
                .unwrap_or_default();
            (!perms.is_empty()).then_some((name, perms))
        })
        .collect()
}

fn select_region_json(
    regions: &[(String, Vec<String>)],
    variant_regions: &HashMap<String, String>,
    picks: &Value,
) -> Vec<Value> {
    regions
        .iter()
        .map(|(name, perms)| {
            let refs: Vec<&str> = perms.iter().map(String::as_str).collect();
            let requested = picked(picks, name).or_else(|| map_get(variant_regions, name));
            json!({
                "name": name,
                "permutations": perms,
                "selected": choose_name(&refs, requested),
            })
        })
        .collect()
}

fn stem_excluded(stem: &str) -> bool {
    [
        "shield",
        "shadow",
        "animdynamics",
        "destroyed",
        "_dmg",
        "damage",
        "collision",
        "physics",
        "imposter",
        "impostor",
        "skeleton",
        "_fp",
        "firstperson",
        "_cine",
    ]
    .iter()
    .any(|key| stem.contains(key))
}

fn mesh_is_static(mesh: &MeshRef) -> bool {
    mesh.class.to_ascii_lowercase().contains("static") || mesh.asset.to_ascii_lowercase().starts_with("sm_")
}

fn mesh_key(mesh: &MeshRef) -> String {
    format!(
        "{}|{}",
        mesh.package.to_ascii_lowercase(),
        mesh.parent_bone.to_ascii_lowercase()
    )
}

/// Shadow, shield, cloth, and damage proxies are in the mesh-sync list but are
/// not the third-person body. Baboon skips the same set.
fn preview_mesh_excluded(mesh: &MeshRef) -> bool {
    let asset = mesh.asset.to_ascii_lowercase();
    let package = mesh.package.replace('\\', "/").to_ascii_lowercase();
    let base = package.rsplit('/').next().unwrap_or(&asset);
    [
        "shield",
        "shadow",
        "animdynamics",
        "destroyed",
        "_dmg",
        "damage",
        "collision",
        "physics",
        "imposter",
        "impostor",
        "skeleton",
    ]
    .iter()
    .any(|key| base.contains(key) || asset.contains(key))
}

fn rel_matrix(transform: &MeshTransform) -> Matrix4 {
    let rotation = Matrix4::from_loc_rot_scale(
        RealPoint3d { x: 0.0, y: 0.0, z: 0.0 },
        RealQuaternion {
            i: transform.rotation[0],
            j: transform.rotation[1],
            k: transform.rotation[2],
            w: transform.rotation[3],
        },
        1.0,
    );
    let scale = Matrix4 {
        m: [
            [transform.scale[0], 0.0, 0.0, 0.0],
            [0.0, transform.scale[1], 0.0, 0.0],
            [0.0, 0.0, transform.scale[2], 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ],
    };
    let translation = Matrix4::from_loc_rot_scale(
        RealPoint3d {
            x: transform.translation[0],
            y: transform.translation[1],
            z: transform.translation[2],
        },
        RealQuaternion { i: 0.0, j: 0.0, k: 0.0, w: 1.0 },
        1.0,
    );
    translation * rotation * scale
}

fn transform_point(matrix: &Matrix4, point: [f32; 3]) -> [f32; 3] {
    let m = &matrix.m;
    [
        m[0][0] * point[0] + m[0][1] * point[1] + m[0][2] * point[2] + m[0][3],
        m[1][0] * point[0] + m[1][1] * point[1] + m[1][2] * point[2] + m[1][3],
        m[2][0] * point[0] + m[2][1] * point[1] + m[2][2] * point[2] + m[2][3],
    ]
}

fn transform_dir(matrix: &Matrix4, direction: [f32; 3]) -> [f32; 3] {
    let m = &matrix.m;
    [
        m[0][0] * direction[0] + m[0][1] * direction[1] + m[0][2] * direction[2],
        m[1][0] * direction[0] + m[1][1] * direction[1] + m[1][2] * direction[2],
        m[2][0] * direction[0] + m[2][1] * direction[1] + m[2][2] * direction[2],
    ]
}

fn normalize_dir(direction: [f32; 3]) -> [f32; 3] {
    let length = (direction[0] * direction[0] + direction[1] * direction[1] + direction[2] * direction[2]).sqrt();
    if length > 1.0e-6 {
        [direction[0] / length, direction[1] / length, direction[2] / length]
    } else {
        [0.0, 0.0, 1.0]
    }
}

fn header_of(bytes: &[u8]) -> Result<FZenPackageHeader> {
    FZenPackageHeader::deserialize(&mut Cursor::new(bytes), None, TOC_VERSION, HEADER_VERSION, None)
        .context("zen package header")
}

fn animation_rows(tag: &TagFile, session: &Session, depth: u8) -> Vec<Value> {
    let graph = AnimationGraph::from_tag(tag);
    let meta = animation_meta(tag);
    let mut rows = Vec::new();
    let mut seen = HashSet::new();
    for mode in &graph.modes {
        for class in &mode.weapon_classes {
            for weapon in &class.weapon_types {
                for set in &weapon.sets {
                    for action in &set.actions {
                        let index = action.animation.animation_index;
                        if index < 0 {
                            continue;
                        }
                        let name = animation_label(
                            &mode.label,
                            &class.label,
                            &weapon.label,
                            &set.label,
                            &action.label,
                        );
                        if !seen.insert(name.clone()) {
                            continue;
                        }
                        rows.push(animation_json(&name, index as usize, meta_at(&meta, index as usize)));
                    }
                }
            }
        }
    }
    if rows.is_empty() {
        for (index, item) in meta.iter().enumerate() {
            let name = item
                .name
                .clone()
                .filter(|name| !name.is_empty())
                .unwrap_or_else(|| format!("animation {index}"));
            if seen.insert(name.clone()) {
                rows.push(animation_json(&name, item.index, Some(item)));
            }
        }
    }
    if rows.is_empty() && depth < 3 {
        if let Some(parent) = tag.root().read_tag_ref_path("parent animation graph") {
            if let Some(bytes) = session.read_ref(&parent, "model_animation_graph") {
                if let Ok(parent_tag) = TagFile::read_from_bytes(&bytes) {
                    return animation_rows(&parent_tag, session, depth + 1);
                }
            }
        }
    }
    rows
}

struct AnimMeta {
    index: usize,
    name: Option<String>,
    kind: String,
    frames: i32,
}

fn meta_at(meta: &[AnimMeta], index: usize) -> Option<&AnimMeta> {
    meta.iter().find(|item| item.index == index)
}

fn animation_meta(tag: &TagFile) -> Vec<AnimMeta> {
    let root = tag.root();
    let block = root
        .field_path("definitions/animations")
        .and_then(|field| field.as_block())
        .or_else(|| {
            root.field_path("resources/animations")
                .and_then(|field| field.as_block())
        });
    let Some(block) = block else { return Vec::new() };
    let mut meta = Vec::with_capacity(block.len());
    for index in 0..block.len() {
        let Some(element) = block.element(index) else { continue };
        let shared = element
            .field("shared animation data")
            .and_then(|field| field.as_block())
            .and_then(|block| block.element(0));
        let metadata = shared.as_ref().unwrap_or(&element);
        let kind = jma_kind(
            metadata
                .read_enum_name("animation type")
                .or_else(|| metadata.read_enum_name("type"))
                .or_else(|| element.read_enum_name("animation type"))
                .as_deref(),
        );
        let frames = metadata
            .read_int_any("frame count")
            .or_else(|| element.read_int_any("frame count"))
            .unwrap_or(0)
            .clamp(0, i32::MAX as i128) as i32;
        meta.push(AnimMeta {
            index,
            name: element.read_string_id("name"),
            kind: kind.to_string(),
            frames,
        });
    }
    meta
}

fn animation_label(mode: &str, class: &str, weapon: &str, set: &str, action: &str) -> String {
    let mode = if mode.is_empty() { "any" } else { mode };
    let action = if action.is_empty() { "idle" } else { action };
    if !is_any(class) {
        format!("{mode}:{class}:{action}")
    } else if !is_any(weapon) {
        format!("{mode}:{weapon}:{action}")
    } else if !is_any(set) {
        format!("{mode}:{set}:{action}")
    } else {
        format!("{mode}:{action}")
    }
}

fn is_any(value: &str) -> bool {
    value.is_empty() || value.eq_ignore_ascii_case("any")
}

fn animation_json(name: &str, index: usize, meta: Option<&AnimMeta>) -> Value {
    json!({
        "name": name,
        "index": index,
        "kind": meta.map(|item| item.kind.as_str()).unwrap_or("JMM"),
        "frames": meta.map(|item| item.frames).unwrap_or(0),
    })
}

fn jma_kind(animation_type: Option<&str>) -> &'static str {
    match animation_type.unwrap_or("base") {
        "overlay" => "JMO",
        "replacement" => "JMR",
        "world" | "world relative" => "JMW",
        _ => "JMM",
    }
}

fn sync_region_json(
    sync: &MeshSyncRegions,
    variant_regions: &HashMap<String, String>,
    picks: &Value,
) -> Vec<Value> {
    sync.regions
        .iter()
        .map(|region| {
            let names: Vec<&str> = region.permutations.iter().map(|perm| perm.name.as_str()).collect();
            let requested = picked(picks, &region.name)
                .or_else(|| map_get(variant_regions, &region.name));
            let selected = choose_name(&names, requested);
            json!({
                "name": region.name,
                "permutations": names,
                "selected": selected,
            })
        })
        .collect()
}

fn variant_region_json(
    model: &Model,
    _variant_name: &str,
    variant_regions: &HashMap<String, String>,
    picks: &Value,
) -> Vec<Value> {
    let mut names: HashMap<String, Vec<String>> = HashMap::new();
    let mut order = Vec::new();
    for variant in &model.variants {
        for region in &variant.regions {
            if region.name.is_empty() {
                continue;
            }
            let entry = names.entry(region.name.clone()).or_insert_with(|| {
                order.push(region.name.clone());
                Vec::new()
            });
            for perm in &region.permutation_names {
                if !perm.is_empty() && !entry.iter().any(|have| have.eq_ignore_ascii_case(perm)) {
                    entry.push(perm.clone());
                }
            }
        }
    }
    order
        .into_iter()
        .map(|name| {
            let perms = names.remove(&name).unwrap_or_default();
            let requested = picked(picks, &name).or_else(|| map_get(variant_regions, &name));
            let perm_refs: Vec<&str> = perms.iter().map(String::as_str).collect();
            let selected = choose_name(&perm_refs, requested);
            json!({"name": name, "permutations": perms, "selected": selected})
        })
        .collect()
}

fn map_get<'a>(map: &'a HashMap<String, String>, key: &str) -> Option<&'a str> {
    map.iter()
        .find(|(name, _)| name.eq_ignore_ascii_case(key))
        .map(|(_, value)| value.as_str())
}

fn choose_name<'a>(names: &'a [&'a str], requested: Option<&str>) -> &'a str {
    requested
        .and_then(|wanted| names.iter().copied().find(|name| name.eq_ignore_ascii_case(wanted)))
        .or_else(|| {
            names.iter().copied().find(|name| {
                name.eq_ignore_ascii_case("default") || name.eq_ignore_ascii_case("base")
            })
        })
        .or_else(|| names.first().copied())
        .unwrap_or("")
}

fn picked<'a>(picks: &'a Value, region: &str) -> Option<&'a str> {
    picks.as_object()?.iter().find_map(|(key, value)| {
        key.eq_ignore_ascii_case(region)
            .then(|| value.as_str())
            .flatten()
            .filter(|name| !name.is_empty())
    })
}

struct PackedMesh {
    vertices: usize,
    triangles: usize,
    bones: usize,
    positions: String,
    normals: String,
    indices: String,
    joints: String,
    weights: String,
}

fn pack_mesh(
    positions: &[[f32; 3]],
    normals: &[[f32; 3]],
    indices: &[u32],
    joints: &[[u16; 4]],
    weights: &[[f32; 4]],
) -> PackedMesh {
    let generated = if normals.len() == positions.len() {
        None
    } else {
        Some(smooth_normals(positions, indices))
    };
    let normals = generated.as_deref().unwrap_or(normals);
    let triangles = indices.len() / 3;
    let skinned = joints.len() == positions.len() && weights.len() == positions.len() && !joints.is_empty();
    let (positions, normals, indices, joints, weights) = if triangles <= MAX_PREVIEW_TRIS || skinned {
        (
            positions.to_vec(),
            normals.to_vec(),
            indices.to_vec(),
            if skinned { joints.to_vec() } else { Vec::new() },
            if skinned { weights.to_vec() } else { Vec::new() },
        )
    } else {
        let (positions, normals, indices) = weld_surface(positions, normals, indices, MAX_PREVIEW_TRIS);
        (positions, normals, indices, Vec::new(), Vec::new())
    };
    let vertices = positions.len();
    let triangles = indices.len() / 3;
    let bones = joints.iter().flat_map(|joint| joint).copied().max().map(|index| index as usize + 1).unwrap_or(0);
    let mut position_bytes = Vec::with_capacity(positions.len() * 12);
    for position in &positions {
        position_bytes.extend(position[0].to_le_bytes());
        position_bytes.extend(position[1].to_le_bytes());
        position_bytes.extend(position[2].to_le_bytes());
    }
    let mut index_bytes = Vec::with_capacity(indices.len() * 4);
    for index in &indices {
        index_bytes.extend(index.to_le_bytes());
    }
    PackedMesh {
        vertices,
        triangles,
        bones,
        positions: base64::engine::general_purpose::STANDARD.encode(position_bytes),
        normals: encode_vec3(&normals),
        indices: base64::engine::general_purpose::STANDARD.encode(index_bytes),
        joints: encode_joints(&joints),
        weights: encode_weights(&weights),
    }
}

fn encode_joints(values: &[[u16; 4]]) -> String {
    let mut bytes = Vec::with_capacity(values.len() * 8);
    for joint in values {
        for bone in joint {
            bytes.extend(bone.to_le_bytes());
        }
    }
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

fn encode_weights(values: &[[f32; 4]]) -> String {
    let mut bytes = Vec::with_capacity(values.len() * 16);
    for weight in values {
        for value in weight {
            bytes.extend(value.to_le_bytes());
        }
    }
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

fn encode_f32(values: &[f32]) -> String {
    let mut bytes = Vec::with_capacity(values.len() * 4);
    for value in values {
        bytes.extend(value.to_le_bytes());
    }
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

fn encode_vec3(values: &[[f32; 3]]) -> String {
    let mut bytes = Vec::with_capacity(values.len() * 12);
    for value in values {
        bytes.extend(value[0].to_le_bytes());
        bytes.extend(value[1].to_le_bytes());
        bytes.extend(value[2].to_le_bytes());
    }
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

fn smooth_normals(positions: &[[f32; 3]], indices: &[u32]) -> Vec<[f32; 3]> {
    let mut normals = vec![[0.0f32; 3]; positions.len()];
    for tri in indices.chunks_exact(3) {
        let Some(a) = positions.get(tri[0] as usize) else { continue };
        let Some(b) = positions.get(tri[1] as usize) else { continue };
        let Some(c) = positions.get(tri[2] as usize) else { continue };
        let edge_b = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
        let edge_c = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
        let face = [
            edge_b[1] * edge_c[2] - edge_b[2] * edge_c[1],
            edge_b[2] * edge_c[0] - edge_b[0] * edge_c[2],
            edge_b[0] * edge_c[1] - edge_b[1] * edge_c[0],
        ];
        for index in tri {
            if let Some(normal) = normals.get_mut(*index as usize) {
                normal[0] += face[0];
                normal[1] += face[1];
                normal[2] += face[2];
            }
        }
    }
    for normal in &mut normals {
        let length = (normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]).sqrt();
        if length > 1.0e-8 {
            normal[0] /= length;
            normal[1] /= length;
            normal[2] /= length;
        } else {
            *normal = [0.0, 0.0, 1.0];
        }
    }
    normals
}

/// Collapse nearby vertices so a huge mesh stays a solid surface under the triangle budget.
fn weld_surface(
    positions: &[[f32; 3]],
    normals: &[[f32; 3]],
    indices: &[u32],
    target: usize,
) -> (Vec<[f32; 3]>, Vec<[f32; 3]>, Vec<u32>) {
    if positions.is_empty() || indices.len() < 3 {
        return (Vec::new(), Vec::new(), Vec::new());
    }
    let mut min = [f32::MAX; 3];
    let mut max = [f32::MIN; 3];
    for position in positions {
        for axis in 0..3 {
            min[axis] = min[axis].min(position[axis]);
            max[axis] = max[axis].max(position[axis]);
        }
    }
    let span = [
        (max[0] - min[0]).max(1.0e-4),
        (max[1] - min[1]).max(1.0e-4),
        (max[2] - min[2]).max(1.0e-4),
    ];
    let longest = span[0].max(span[1]).max(span[2]);
    let ratio = (target as f32 / (indices.len() / 3).max(1) as f32).clamp(0.02, 1.0);
    let cells = (96.0 / ratio.sqrt()).clamp(32.0, 256.0);
    let cell = longest / cells;
    let mut slots: HashMap<(i32, i32, i32), u32> = HashMap::new();
    let mut compact = Vec::new();
    let mut compact_normals = Vec::new();
    let mut remap = vec![u32::MAX; positions.len()];
    for (index, position) in positions.iter().enumerate() {
        let key = (
            ((position[0] - min[0]) / cell) as i32,
            ((position[1] - min[1]) / cell) as i32,
            ((position[2] - min[2]) / cell) as i32,
        );
        let normal = normals.get(index).copied().unwrap_or([0.0, 0.0, 1.0]);
        let slot = if let Some(slot) = slots.get(&key).copied() {
            let accumulated: &mut [f32; 3] = &mut compact_normals[slot as usize];
            accumulated[0] += normal[0];
            accumulated[1] += normal[1];
            accumulated[2] += normal[2];
            slot
        } else {
            let slot = compact.len() as u32;
            compact.push(*position);
            compact_normals.push(normal);
            slots.insert(key, slot);
            slot
        };
        remap[index] = slot;
    }
    for normal in &mut compact_normals {
        let length = (normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]).sqrt();
        if length > 1.0e-8 {
            normal[0] /= length;
            normal[1] /= length;
            normal[2] /= length;
        }
    }
    let mut kept = Vec::new();
    for tri in indices.chunks_exact(3) {
        let a = remap.get(tri[0] as usize).copied().unwrap_or(u32::MAX);
        let b = remap.get(tri[1] as usize).copied().unwrap_or(u32::MAX);
        let c = remap.get(tri[2] as usize).copied().unwrap_or(u32::MAX);
        if a == u32::MAX || b == u32::MAX || c == u32::MAX || a == b || b == c || a == c {
            continue;
        }
        kept.extend([a, b, c]);
    }
    if kept.len() / 3 > target && kept.len() + 3 < indices.len() {
        return weld_surface(&compact, &compact_normals, &kept, target);
    }
    (compact, compact_normals, kept)
}

fn preview_json(
    kind: &str,
    id: &str,
    variant: &str,
    variants: &[Value],
    regions: &[Value],
    animations: &[Value],
    mesh: PackedMesh,
) -> Value {
    json!({
        "ok": true,
        "kind": kind,
        "id": id,
        "variant": variant,
        "name": file_from_key(id).map(|file| file.name).unwrap_or_else(|| id.to_string()),
        "variants": variants,
        "regions": regions,
        "animations": animations,
        "vertices": mesh.vertices,
        "triangles": mesh.triangles,
        "positions": mesh.positions,
        "normals": mesh.normals,
        "indices": mesh.indices,
        "joints": mesh.joints,
        "weights": mesh.weights,
        "bones": mesh.bones,
    })
}

fn physics_mesh(jms: &JmsFile) -> PackedMesh {
    let mut positions = Vec::new();
    let mut indices = Vec::new();
    for sphere in &jms.spheres {
        add_sphere(
            &mut positions,
            &mut indices,
            &jms.nodes,
            sphere.parent,
            sphere.translation,
            sphere.radius,
        );
    }
    for capsule in &jms.capsules {
        add_capsule(
            &mut positions,
            &mut indices,
            &jms.nodes,
            capsule.parent,
            capsule.rotation,
            capsule.translation,
            capsule.height,
            capsule.radius,
        );
    }
    for bx in &jms.boxes {
        add_box(
            &mut positions,
            &mut indices,
            &jms.nodes,
            bx.parent,
            bx.rotation,
            bx.translation,
            bx.width,
            bx.length,
            bx.height,
        );
    }
    for hull in &jms.convex_shapes {
        add_fan(
            &mut positions,
            &mut indices,
            &jms.nodes,
            hull.parent,
            hull.rotation,
            hull.translation,
            &hull.vertices,
        );
    }
    pack_mesh(&positions, &[], &indices, &[], &[])
}

fn add_sphere(
    positions: &mut Vec<[f32; 3]>,
    indices: &mut Vec<u32>,
    nodes: &[JmsNode],
    parent: i32,
    center: RealPoint3d,
    radius: f32,
) {
    if radius <= 0.0 {
        return;
    }
    const LAT: usize = 6;
    const LON: usize = 8;
    let base = positions.len() as u32;
    for lat in 0..=LAT {
        let v = std::f32::consts::PI * lat as f32 / LAT as f32;
        for lon in 0..LON {
            let u = std::f32::consts::TAU * lon as f32 / LON as f32;
            let local = RealPoint3d {
                x: center.x + radius * v.sin() * u.cos(),
                y: center.y + radius * v.sin() * u.sin(),
                z: center.z + radius * v.cos(),
            };
            positions.push(world_point(nodes, parent, local));
        }
    }
    for lat in 0..LAT {
        for lon in 0..LON {
            let a = base + (lat * LON + lon) as u32;
            let b = base + (lat * LON + (lon + 1) % LON) as u32;
            let c = base + ((lat + 1) * LON + lon) as u32;
            let d = base + ((lat + 1) * LON + (lon + 1) % LON) as u32;
            indices.extend([a, c, b, b, c, d]);
        }
    }
}

fn add_capsule(
    positions: &mut Vec<[f32; 3]>,
    indices: &mut Vec<u32>,
    nodes: &[JmsNode],
    parent: i32,
    rotation: RealQuaternion,
    translation: RealPoint3d,
    height: f32,
    radius: f32,
) {
    if radius <= 0.0 {
        return;
    }
    const LAT: usize = 4;
    const LON: usize = 8;
    let base = positions.len() as u32;
    let place = |local: [f32; 3]| {
        let turned = rotation.rotate(RealVector3d { i: local[0], j: local[1], k: local[2] });
        world_point(
            nodes,
            parent,
            RealPoint3d {
                x: translation.x + turned.i,
                y: translation.y + turned.j,
                z: translation.z + turned.k,
            },
        )
    };
    // Anchor is the outer bottom tip. Local +Z runs toward the top.
    for lat in 0..=LAT {
        let v = std::f32::consts::PI * lat as f32 / LAT as f32;
        let z = if lat <= LAT / 2 {
            radius + radius * (v - std::f32::consts::FRAC_PI_2).cos()
        } else {
            radius + height + radius * (v - std::f32::consts::FRAC_PI_2).cos()
        };
        let ring = radius * v.sin();
        for lon in 0..LON {
            let u = std::f32::consts::TAU * lon as f32 / LON as f32;
            positions.push(place([ring * u.cos(), ring * u.sin(), z]));
        }
    }
    for lat in 0..LAT {
        for lon in 0..LON {
            let a = base + (lat * LON + lon) as u32;
            let b = base + (lat * LON + (lon + 1) % LON) as u32;
            let c = base + ((lat + 1) * LON + lon) as u32;
            let d = base + ((lat + 1) * LON + (lon + 1) % LON) as u32;
            indices.extend([a, c, b, b, c, d]);
        }
    }
}

fn add_box(
    positions: &mut Vec<[f32; 3]>,
    indices: &mut Vec<u32>,
    nodes: &[JmsNode],
    parent: i32,
    rotation: RealQuaternion,
    translation: RealPoint3d,
    width: f32,
    length: f32,
    height: f32,
) {
    let hx = width * 0.5;
    let hy = length * 0.5;
    let hz = height * 0.5;
    let corners = [
        [-hx, -hy, -hz],
        [hx, -hy, -hz],
        [hx, hy, -hz],
        [-hx, hy, -hz],
        [-hx, -hy, hz],
        [hx, -hy, hz],
        [hx, hy, hz],
        [-hx, hy, hz],
    ];
    let base = positions.len() as u32;
    for corner in corners {
        let turned = rotation.rotate(RealVector3d { i: corner[0], j: corner[1], k: corner[2] });
        positions.push(world_point(
            nodes,
            parent,
            RealPoint3d {
                x: translation.x + turned.i,
                y: translation.y + turned.j,
                z: translation.z + turned.k,
            },
        ));
    }
    let faces = [
        [0, 2, 1],
        [0, 3, 2],
        [4, 5, 6],
        [4, 6, 7],
        [0, 1, 5],
        [0, 5, 4],
        [1, 2, 6],
        [1, 6, 5],
        [2, 3, 7],
        [2, 7, 6],
        [3, 0, 4],
        [3, 4, 7],
    ];
    for face in faces {
        indices.extend(face.map(|index| base + index));
    }
}

fn add_fan(
    positions: &mut Vec<[f32; 3]>,
    indices: &mut Vec<u32>,
    nodes: &[JmsNode],
    parent: i32,
    rotation: RealQuaternion,
    translation: RealPoint3d,
    vertices: &[RealPoint3d],
) {
    if vertices.len() < 3 {
        return;
    }
    let base = positions.len() as u32;
    for vertex in vertices {
        let turned = rotation.rotate(vertex.as_vector());
        positions.push(world_point(
            nodes,
            parent,
            RealPoint3d {
                x: translation.x + turned.i,
                y: translation.y + turned.j,
                z: translation.z + turned.k,
            },
        ));
    }
    for index in 1..vertices.len() - 1 {
        indices.extend([base, base + index as u32, base + index as u32 + 1]);
    }
}

fn world_point(nodes: &[JmsNode], parent: i32, local: RealPoint3d) -> [f32; 3] {
    let Some(node) = usize::try_from(parent).ok().and_then(|index| nodes.get(index)) else {
        return [local.x, local.y, local.z];
    };
    let turned = node.rotation.rotate(local.as_vector());
    [
        node.translation.x + turned.i,
        node.translation.y + turned.j,
        node.translation.z + turned.k,
    ]
}

fn mesh_score(name: &str, stem: &str, token: &str) -> i32 {
    if name.contains("_fp") || name.contains("firstperson") || name.contains("_cine") {
        return 0;
    }
    let mut score = 0;
    if !stem.is_empty() && name.contains(stem) {
        score += 100;
    }
    if name.contains(&format!("{token}_common"))
        || name.contains(&format!("{token}_default"))
        || name.contains(&format!("{token}_base"))
    {
        score += 40;
    }
    if score == 0 {
        return 0;
    }
    if name.contains("body") || name.contains("torso") {
        score += 20;
    }
    if name.contains("head") {
        score += 10;
    }
    score
}

fn is_mesh_sync_asset(lower_path: &str) -> bool {
    let file = lower_path.rsplit('/').next().unwrap_or(lower_path);
    let flat: String = file.chars().filter(|ch| *ch != '_').collect();
    flat.contains("meshsync")
}

fn model_tokens(model_id: &str) -> Vec<String> {
    let relative = model_id
        .strip_prefix("objects/characters/")
        .unwrap_or(model_id);
    let file = relative.rsplit('/').next().unwrap_or(relative);
    let stem = file.strip_suffix("-model").unwrap_or(file);
    stem.split('_')
        .filter(|part| part.len() >= 3)
        .map(|part| part.to_string())
        .collect()
}

fn folder_token(model_id: &str) -> String {
    let relative = model_id
        .strip_prefix("objects/characters/")
        .unwrap_or(model_id);
    let file = relative.rsplit('/').next().unwrap_or(relative);
    let stem = file.strip_suffix("-model").unwrap_or(file);
    stem.split('_').next().unwrap_or(stem).to_string()
}

fn sibling_model_id(id: &str) -> Option<String> {
    let stem = id
        .strip_suffix("-physics_model")
        .or_else(|| id.strip_suffix("-collision_model"))
        .or_else(|| id.strip_suffix("-model"))?;
    Some(format!("{stem}-model"))
}

fn file_stem(path: &str) -> String {
    path.rsplit(['/', '\\'])
        .next()
        .unwrap_or(path)
        .trim_end_matches(".uasset")
        .trim_end_matches(".ubulk")
        .to_string()
}

fn tag_key(lower_path: &str) -> Option<String> {
    let rest = if let Some(index) = lower_path.find("/tags/") {
        &lower_path[index + "/tags/".len()..]
    } else {
        lower_path.strip_prefix("tags/")?
    };
    rest.strip_suffix(".ubulk").map(str::to_string)
}

fn package_key(lower_path: &str) -> Option<String> {
    let rest = lower_path
        .split_once("/content/")
        .map(|(_, rest)| rest)?;
    let stem = rest.strip_suffix(".uasset")?;
    Some(format!("/game/{stem}"))
}

fn kind_of(key: &str) -> Option<&'static str> {
    if key.ends_with("-physics_model") {
        Some("physics_model")
    } else if key.ends_with("-collision_model") {
        Some("collision_model")
    } else if key.ends_with("-model") {
        Some("model")
    } else {
        None
    }
}

fn kind_rank(kind: &str) -> u8 {
    match kind {
        "model" => 0,
        "physics_model" => 1,
        "collision_model" => 2,
        _ => 9,
    }
}

fn file_from_key(key: &str) -> Option<FileJson> {
    let relative = key.strip_prefix("objects/characters/")?;
    let kind = kind_of(key)?;
    let (folder, file) = match relative.rfind('/') {
        Some(split) => (&relative[..split], &relative[split + 1..]),
        None => ("", relative),
    };
    let stem = file
        .strip_suffix("-physics_model")
        .or_else(|| file.strip_suffix("-collision_model"))
        .or_else(|| file.strip_suffix("-model"))?;
    let extension = match kind {
        "physics_model" => "physics_model",
        "collision_model" => "collision_model",
        _ => "model",
    };
    Some(FileJson {
        id: key.to_string(),
        name: format!("{stem}.{extension}"),
        folder: folder.to_string(),
        kind: kind.to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classifies_character_files() {
        let model = file_from_key("objects/characters/covenant/elite_ai/elite_ai-model").unwrap();
        assert_eq!(model.folder, "covenant/elite_ai");
        assert_eq!(model.name, "elite_ai.model");
        assert_eq!(model.kind, "model");
        assert!(file_from_key("objects/characters/elite/elite-skeleton_model").is_none());
        let physics =
            file_from_key("objects/characters/elite/elite_ai-physics_model").unwrap();
        assert_eq!(physics.kind, "physics_model");
        assert_eq!(physics.name, "elite_ai.physics_model");
    }

    #[test]
    fn animation_labels_match_the_preview_list() {
        assert_eq!(animation_label("any", "any", "any", "any", "go_berserk"), "any:go_berserk");
        assert_eq!(animation_label("banshee_b_d_l", "any", "any", "any", "board"), "banshee_b_d_l:board");
        assert_eq!(jma_kind(Some("base")), "JMM");
        assert_eq!(jma_kind(Some("replacement")), "JMR");
    }
}
