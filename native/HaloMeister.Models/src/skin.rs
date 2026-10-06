//! GPU skinning for Campaign Evolved characters.
//!
//! Vertices stay in the UE bind pose the preview already draws. Each UE bone
//! is matched to a `skeleton_model` node by name. A frame's skin matrix is the
//! tag-space motion of that node, conjugated back into UE space, so the bind
//! pose is an exact no-op and playback only uploads bone rows.

use std::collections::HashMap;

use blam_tags::extract::animation::{
    additional_node_data_is_object_space, build_defaults, jma_kind_for,
};
use blam_tags::iostore::skeletal_mesh::SkeletalMesh;
use blam_tags::jms::ue_bind_world;
use blam_tags::math::{Matrix4, RealPoint3d};
use blam_tags::{
    Animation, AnimationGraph, JmaKind, JmsFile, NodeTransform, Pose, Skeleton, TagFile,
};

pub const MAX_SKIN_BONES: usize = 1024;
const JMS_SCALE: f32 = 100.0;
const CM_TO_JMS: f32 = 100.0 / 304.8;

#[derive(Clone)]
pub struct CachedBone {
    pub name: String,
    pub tag_node: usize,
    pub rest_bake: Matrix4,
    /// This bone's bind pose in UE component space (centimeters). Rigid static
    /// pieces are baked with it so they sit on the bone instead of the origin.
    pub ue_bind: Matrix4,
}

pub struct CachedRig {
    pub bones: Vec<CachedBone>,
    pub tag_names: Vec<String>,
    pub tag_parents: Vec<i16>,
    pub tag_locals: Vec<NodeTransform>,
    pub tag_world: Vec<Matrix4>,
    name_to_node: HashMap<String, usize>,
}

impl CachedRig {
    pub fn empty() -> Self {
        Self {
            bones: Vec::new(),
            tag_names: Vec::new(),
            tag_parents: Vec::new(),
            tag_locals: Vec::new(),
            tag_world: Vec::new(),
            name_to_node: HashMap::new(),
        }
    }

    pub fn from_skeleton(tag: &TagFile) -> Self {
        let world_nodes = JmsFile::skeleton_rest_pose(tag).unwrap_or_default();
        let tag_world: Vec<Matrix4> = world_nodes
            .iter()
            .map(|node| Matrix4::from_loc_rot_scale(node.translation, node.rotation, 1.0))
            .collect();
        let mut tag_names = Vec::with_capacity(world_nodes.len());
        let mut tag_parents = Vec::with_capacity(world_nodes.len());
        let mut tag_locals = Vec::with_capacity(world_nodes.len());
        let mut name_to_node = HashMap::new();
        for (index, node) in world_nodes.iter().enumerate() {
            name_to_node.insert(node.name.to_ascii_lowercase(), index);
            tag_names.push(node.name.clone());
            tag_parents.push(node.parent);
            let local = if node.parent >= 0 && (node.parent as usize) < index {
                tag_world[node.parent as usize].inverse() * tag_world[index]
            } else {
                tag_world[index]
            };
            let (translation, rotation, scale) = local.decompose();
            tag_locals.push(NodeTransform {
                translation: RealPoint3d {
                    x: translation.x / JMS_SCALE,
                    y: translation.y / JMS_SCALE,
                    z: translation.z / JMS_SCALE,
                },
                rotation,
                scale,
            });
        }
        Self {
            bones: Vec::new(),
            tag_names,
            tag_parents,
            tag_locals,
            tag_world,
            name_to_node,
        }
    }

    pub fn is_empty(&self) -> bool {
        self.tag_names.is_empty()
    }

    /// Append one skeletal mesh. Returns four joints and four weights per vertex,
    /// joint indices into this rig's bone palette.
    pub fn push_mesh(&mut self, mesh: &SkeletalMesh) -> (Vec<[u16; 4]>, Vec<[f32; 4]>) {
        if self.is_empty() || self.bones.len() >= MAX_SKIN_BONES {
            return (vec![[0; 4]; mesh.vertices.len()], vec![[0.0; 4]; mesh.vertices.len()]);
        }
        let x = space_x();
        let ue_bind = ue_bind_world(&mesh.bones);
        let mut local_to_palette = vec![u16::MAX; mesh.bones.len()];
        for (index, bone) in mesh.bones.iter().enumerate() {
            if self.bones.len() >= MAX_SKIN_BONES {
                break;
            }
            let tag_node = self
                .name_to_node
                .get(&bone.name.to_ascii_lowercase())
                .copied()
                .unwrap_or(0);
            let tag_world = self.tag_world.get(tag_node).copied().unwrap_or(Matrix4::IDENTITY);
            let inverse_bind = ue_bind.get(index).copied().unwrap_or(Matrix4::IDENTITY).inverse();
            local_to_palette[index] = self.bones.len() as u16;
            self.bones.push(CachedBone {
                name: bone.name.clone(),
                tag_node,
                rest_bake: tag_world * x * inverse_bind,
                ue_bind: ue_bind.get(index).copied().unwrap_or(Matrix4::IDENTITY),
            });
        }

        let mut joints = Vec::with_capacity(mesh.vertices.len());
        let mut weights = Vec::with_capacity(mesh.vertices.len());
        for vertex in &mesh.vertices {
            let mut influences: Vec<(u16, f32)> = vertex
                .influences
                .iter()
                .filter_map(|influence| {
                    let slot = *local_to_palette.get(influence.bone as usize)?;
                    (slot != u16::MAX && influence.weight > 0.0).then_some((slot, influence.weight))
                })
                .collect();
            influences.sort_by(|a, b| b.1.total_cmp(&a.1));
            influences.truncate(4);
            let mut joint = [0u16; 4];
            let mut weight = [0.0f32; 4];
            for (slot, (bone, value)) in influences.into_iter().enumerate() {
                joint[slot] = bone;
                weight[slot] = value;
            }
            let sum = weight.iter().sum::<f32>();
            if sum > 1.0e-6 {
                for value in &mut weight {
                    *value /= sum;
                }
            }
            joints.push(joint);
            weights.push(weight);
        }
        (joints, weights)
    }

    /// Palette slot and UE bind matrix for a bone name. Static pieces bind to
    /// the first skeletal mesh that actually has that bone.
    pub fn rigid_bind(&self, bone_name: &str) -> Option<(u16, Matrix4)> {
        if bone_name.is_empty() {
            return None;
        }
        self.bones.iter().enumerate().find_map(|(index, bone)| {
            bone.name
                .eq_ignore_ascii_case(bone_name)
                .then_some((index as u16, bone.ue_bind))
        })
    }

    /// Frame-major skin rows: 12 floats (three matrix rows) per bone.
    pub fn skin_rows(&self, pose: &Pose, jmad_names: &[String]) -> Vec<f32> {
        let mut jmad_of_name = HashMap::new();
        for (index, name) in jmad_names.iter().enumerate() {
            jmad_of_name.insert(name.to_ascii_lowercase(), index);
        }
        let mut rows = Vec::with_capacity(pose.frames.len() * self.bones.len() * 12);
        for frame in &pose.frames {
            let mut locals = self.tag_locals.clone();
            for (index, name) in self.tag_names.iter().enumerate() {
                let Some(&jmad_index) = jmad_of_name.get(&name.to_ascii_lowercase()) else {
                    continue;
                };
                if let Some(transform) = frame.get(jmad_index) {
                    locals[index] = *transform;
                }
            }
            let animated = compose_world(&locals, &self.tag_parents);
            for bone in &self.bones {
                let skin = match (animated.get(bone.tag_node), self.tag_world.get(bone.tag_node)) {
                    (Some(world), Some(rest)) => {
                        bone.rest_bake.inverse() * *world * rest.inverse() * bone.rest_bake
                    }
                    _ => Matrix4::IDENTITY,
                };
                for row in 0..3 {
                    rows.extend_from_slice(&skin.m[row]);
                }
            }
        }
        rows
    }
}

pub fn decode_pose(
    jmad: &TagFile,
    skeleton_model: Option<&TagFile>,
    index: usize,
) -> Result<(Pose, Vec<String>), String> {
    let animation = Animation::new(jmad).map_err(|error| error.to_string())?;
    let group = animation
        .get(index)
        .ok_or("The graph no longer lists this animation.")?;
    if group.blob.is_empty() {
        return Err("This animation has no payload.".to_string());
    }
    let skeleton = Skeleton::from_tag(jmad);
    if skeleton.is_empty() {
        return Err("This animation graph has no skeleton.".to_string());
    }
    let names: Vec<String> = skeleton.nodes.iter().map(|node| node.name.clone()).collect();
    let object_space = additional_node_data_is_object_space(&animation);
    let defaults = build_defaults(&skeleton, jmad, skeleton_model, object_space);
    let clip = group.decode().map_err(|error| error.to_string())?;
    let kind = jma_kind_for(group);
    let graph = AnimationGraph::from_tag(jmad);
    let base = match kind {
        JmaKind::Jmo | JmaKind::Jmr => animation
            .overlay_base_pose(&graph, group, &skeleton, &defaults)
            .unwrap_or_else(|| defaults.clone()),
        _ => defaults.clone(),
    };
    let pose = match kind {
        JmaKind::Jmo => {
            let (mut reference, mut body) = clip.overlay_pose(&skeleton, &base);
            body.apply_object_space_corrections(
                &mut reference,
                &skeleton,
                &base,
                &group.object_space_parents,
            );
            body
        }
        JmaKind::Jmr => {
            let mut body = clip.replacement_pose(&skeleton, &base);
            let mut reference = base.clone();
            body.apply_object_space_corrections(
                &mut reference,
                &skeleton,
                &base,
                &group.object_space_parents,
            );
            body
        }
        _ => clip.pose(&skeleton, Some(&defaults)),
    };
    if pose.frames.is_empty() {
        return Err("This animation decoded to no frames.".to_string());
    }
    Ok((pose, names))
}

fn space_x() -> Matrix4 {
    Matrix4 {
        m: [
            [CM_TO_JMS, 0.0, 0.0, 0.0],
            [0.0, -CM_TO_JMS, 0.0, 0.0],
            [0.0, 0.0, CM_TO_JMS, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ],
    }
}

fn compose_world(locals: &[NodeTransform], parents: &[i16]) -> Vec<Matrix4> {
    let mut world = Vec::with_capacity(locals.len());
    for (index, local) in locals.iter().enumerate() {
        let matrix = Matrix4::from_loc_rot_scale(
            RealPoint3d {
                x: local.translation.x * JMS_SCALE,
                y: local.translation.y * JMS_SCALE,
                z: local.translation.z * JMS_SCALE,
            },
            local.rotation,
            local.scale,
        );
        let parent = parents.get(index).copied().unwrap_or(-1);
        let combined = if parent >= 0 && (parent as usize) < world.len() {
            world[parent as usize] * matrix
        } else {
            matrix
        };
        world.push(combined);
    }
    world
}

#[cfg(test)]
mod tests {
    use super::*;
    use blam_tags::math::RealQuaternion;

    #[test]
    fn rest_pose_skin_is_identity() {
        let local = NodeTransform {
            translation: RealPoint3d { x: 0.2, y: -0.4, z: 1.5 },
            rotation: RealQuaternion { i: 0.0, j: 0.0, k: 0.0, w: 1.0 },
            scale: 1.0,
        };
        let mut rig = CachedRig {
            bones: Vec::new(),
            tag_names: vec!["pelvis".to_string()],
            tag_parents: vec![-1],
            tag_locals: vec![local],
            tag_world: Vec::new(),
            name_to_node: HashMap::new(),
        };
        rig.tag_world = compose_world(&rig.tag_locals, &rig.tag_parents);
        rig.bones.push(CachedBone {
            name: "pelvis".to_string(),
            tag_node: 0,
            rest_bake: Matrix4::from_loc_rot_scale(
                RealPoint3d { x: 3.0, y: 4.0, z: 5.0 },
                RealQuaternion::IDENTITY,
                1.0,
            ),
            ue_bind: Matrix4::IDENTITY,
        });
        let pose = Pose { frames: vec![vec![local]] };
        let rows = rig.skin_rows(&pose, &["pelvis".to_string()]);
        let identity = [
            1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0,
        ];
        for (got, expect) in rows.iter().zip(identity) {
            assert!((got - expect).abs() < 1.0e-4, "skin drifted: {rows:?}");
        }
    }
}
