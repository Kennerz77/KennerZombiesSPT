using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZombiesMonkeyHands.ModelFormats;
using ZombiesMonkeyHands.SEFormats;
using UnityEngine;

namespace ZombiesMonkeyHands.Editor
{
    /// <summary>
    /// A the original view model (here a perk bottle), exported with Greyhound: the view model and its viewmodel animations, turned into
    /// the same <see cref="SrcModel"/> / <see cref="SrcAnim"/> the the original port loads into, so the rest of the import (rig,
    /// retarget, controller) is shared.
    ///
    /// Greyhound's SEAnims hold each bone's transform relative to its parent but no hierarchy, and leave out the view
    /// bones above the arms. The hierarchy and the rest pose of anything an animation doesn't key come from the original's
    /// arms rig (am_t5_thundergun: same bone names) for the arms and view, and from the HD model for the gun's parts.
    /// All maths here is in the original game space; Unity's Quaternion and Vector3 are used as plain maths.
    /// </summary>
    public static class RgClassic
    {
        // The asset export folder on the author's PC: the KZ_EXPORT_ROOT user environment variable (kept out of the source)
        public static readonly string ExportRoot = System.Environment.GetEnvironmentVariable("KZ_EXPORT_ROOT", System.EnvironmentVariableTarget.User) ?? "";
        /// <summary>The view model being imported (the perk's bottle), set by the importer.</summary>
        public static string ModelName = "wpn_t7_zmb_monkey_bomb_view";
        // The Cymbal Monkey's view model and anims, converted from GrayWolf's casts by 99_Tools\cast_to_se.py (cm, as Greyhound's)
        public static string ModelDir => System.IO.Path.Combine(UnityEngine.Application.dataPath, "Zombies", "MonkeyView");
        public static string ModelFile => System.IO.Path.Combine(ModelDir, ModelName + "_LOD0.semodel");
        public static string AnimDir => System.IO.Path.Combine(ModelDir, "full_anims");

        /// <summary>The gun attaches to the right hand's weapon tag with no offset, as in every the original game viewmodel.</summary>
        private const string GunParent = "tag_weapon_right";

        /// <summary>
        /// Gun parts that move slower than the original keys them, per animation (bone, times slower): the part's own keys are
        /// stretched, so it still rides along with the gun.
        /// </summary>
        public static readonly Dictionary<(string anim, string bone), float> Slower = new Dictionary<(string, string), float>();

        public static SrcModel LoadModel()
        {
            SEModel se = SEModel.Load(ModelFile);
            var model = new SrcModel();
            foreach (SEBone b in se.Bones)
            {
                var q = new Quaternion(b.GlobalRotation.X, b.GlobalRotation.Y, b.GlobalRotation.Z, b.GlobalRotation.W);
                model.Bones.Add(new SrcBone { Name = b.Name, Parent = b.Parent, Rest = Pose(V(b.GlobalPosition), q) });
            }
            foreach (SEMesh m in se.Meshes)
            {
                int baseIndex = model.Vertices.Count;
                for (int v = 0; v < m.VertexCount; v++)
                {
                    var vertex = new SrcVertex { Pos = m.Positions[v].ToSrc() };
                    for (int i = 0; i < m.MaxInfluence; i++)
                    {
                        float w = m.WeightValues[v * m.MaxInfluence + i];
                        if (w > 0f)
                            vertex.Weights.Add((m.WeightBones[v * m.MaxInfluence + i], w));
                    }
                    if (vertex.Weights.Count == 0)
                        vertex.Weights.Add((0, 1f));
                    model.Vertices.Add(vertex);
                }
                int material = m.MaterialIndices.Length > 0 ? m.MaterialIndices[0] : 0;
                for (int f = 0; f < m.Faces.Length; f += 3)
                {
                    var face = new SrcFace { Material = material };
                    for (int c = 0; c < 3; c++)
                    {
                        int v = m.Faces[f + c];
                        face.Corners[c] = new SrcCorner
                        {
                            Vertex = baseIndex + v,
                            Normal = m.Normals != null ? new SrcVec { X = m.Normals[v].X, Y = m.Normals[v].Y, Z = m.Normals[v].Z } : new SrcVec { Z = 1 },
                            U = m.UVs != null ? m.UVs[v * 2] : 0f,
                            V = m.UVs != null ? m.UVs[v * 2 + 1] : 0f,
                        };
                    }
                    model.Faces.Add(face);
                }
            }
            foreach (SEMaterial m in se.Materials)
                model.Materials.Add(new SrcMaterial { Name = m.Name, Type = "", Image = m.DiffuseMap });
            return model;
        }

        /// <summary>An HD viewmodel animation as world poses per frame, with its notetracks.</summary>
        public static SrcAnim LoadAnim(string name, SrcModel gun, SrcModel bind, out List<(int frame, string note)> notes)
        {
            SEAnim se = SEAnim.Load(Path.Combine(AnimDir, name + ".seanim"));
            notes = se.Notes;

            // Hierarchy and rest (local) transforms: the original's rig for arms and view, the HD model for the gun
            var parent = new Dictionary<string, string>();
            var restLocal = new Dictionary<string, (Vector3 p, Quaternion q)>();
            AddRig(bind, parent, restLocal);
            AddRig(gun, parent, restLocal);
            string gunRoot = gun.Bones[0].Name;
            parent[gunRoot] = GunParent;
            restLocal[gunRoot] = (Vector3.zero, Quaternion.identity);

            var tracks = se.Bones.ToDictionary(b => b.Name, b => b);
            // The gun's moving parts (pump, drums, arms, switches...) have their positions keyed as offsets from their
            // rest position on the model (the pump's slide on a shot is -8 from ~0); everything else is keyed in full
            var offsetKeyed = new HashSet<string>(gun.Bones.Skip(1).Select(b => b.Name));
            List<string> parts = parent.Keys.Union(parent.Values).Union(tracks.Keys).Distinct().ToList();
            var anim = new SrcAnim { FrameRate = se.FrameRate > 0 ? se.FrameRate : 30f, Parts = parts };

            for (int f = 0; f < Mathf.Max(1, se.FrameCount); f++)
            {
                var world = new Dictionary<string, (Vector3 p, Quaternion q)>();
                (Vector3 p, Quaternion q) World(string bone)
                {
                    if (world.TryGetValue(bone, out var w))
                        return w;
                    (Vector3 p, Quaternion q) local = restLocal.TryGetValue(bone, out var rest) ? rest : (Vector3.zero, Quaternion.identity);
                    if (tracks.TryGetValue(bone, out SEAnimBone track))
                    {
                        bool offset = offsetKeyed.Contains(bone);
                        if (track.Positions.Count > 0)
                            local.p = offset ? local.p + Sample(track.Positions, f) : Sample(track.Positions, f);
                        // Rotations are full transforms for every bone (the angled ammo arms are keyed at their rest angle)
                        if (track.Rotations.Count > 0)
                            local.q = Sample(track.Rotations, Slower.TryGetValue((name, bone), out float slow) ? f / slow : f);
                    }
                    if (parent.TryGetValue(bone, out string up))
                    {
                        var pw = World(up);
                        w = (pw.p + pw.q * local.p, (pw.q * local.q).normalized);
                    }
                    else
                    {
                        w = local; // the view root
                    }
                    world[bone] = w;
                    return w;
                }
                anim.Frames.Add(parts.Select(b => { var w = World(b); return Pose(w.p, w.q); }).ToArray());
            }
            return anim;
        }

        private static void AddRig(SrcModel model, Dictionary<string, string> parent, Dictionary<string, (Vector3, Quaternion)> restLocal)
        {
            foreach (SrcBone b in model.Bones)
            {
                (Vector3 p, Quaternion q) = FromPose(b.Rest);
                if (b.Parent >= 0)
                {
                    SrcBone up = model.Bones[b.Parent];
                    (Vector3 pp, Quaternion pq) = FromPose(up.Rest);
                    Quaternion inv = Quaternion.Inverse(pq);
                    parent[b.Name] = up.Name;
                    restLocal[b.Name] = (inv * (p - pp), (inv * q).normalized);
                }
                else if (!restLocal.ContainsKey(b.Name))
                {
                    restLocal[b.Name] = (p, q);
                }
            }
        }

        private static Vector3 Sample(List<SEKey<SEVec3>> keys, int frame)
        {
            int i = keys.FindLastIndex(k => k.Frame <= frame);
            if (i < 0) return V(keys[0].Value);
            if (i == keys.Count - 1 || keys[i].Frame == frame) return V(keys[i].Value);
            float t = (frame - keys[i].Frame) / (float)(keys[i + 1].Frame - keys[i].Frame);
            return Vector3.Lerp(V(keys[i].Value), V(keys[i + 1].Value), t);
        }

        private static Quaternion Sample(List<SEKey<SEQuat>> keys, float frame)
        {
            int i = keys.FindLastIndex(k => k.Frame <= frame);
            if (i < 0) return Q(keys[0].Value);
            if (i == keys.Count - 1 || keys[i].Frame == frame) return Q(keys[i].Value);
            float t = (frame - keys[i].Frame) / (float)(keys[i + 1].Frame - keys[i].Frame);
            return Quaternion.Slerp(Q(keys[i].Value), Q(keys[i + 1].Value), t);
        }

        // Greyhound exports in centimetres; the original game (and the rest of this import) works in inches
        private const float CmToInch = 1f / 2.54f;

        private static Vector3 V(SEVec3 v) => new Vector3(v.X, v.Y, v.Z) * CmToInch;
        private static Quaternion Q(SEQuat q) => new Quaternion(q.X, q.Y, q.Z, q.W).normalized;
        private static SrcVec ToSrc(this SEVec3 v) => new SrcVec { X = v.X * CmToInch, Y = v.Y * CmToInch, Z = v.Z * CmToInch };

        /// <summary>the original game pose from a position and rotation: the rotated unit axes are the bone's X/Y/Z rows.</summary>
        private static SrcPose Pose(Vector3 p, Quaternion q) => new SrcPose
        {
            Pos = new SrcVec { X = p.x, Y = p.y, Z = p.z },
            AxisX = C(q * Vector3.right), AxisY = C(q * Vector3.up), AxisZ = C(q * Vector3.forward),
        };

        /// <summary>A the original game pose's rotation: the rotation taking the unit axes onto its (right-handed) X/Y/Z rows.</summary>
        private static (Vector3, Quaternion) FromPose(SrcPose pose)
        {
            Vector3 y = new Vector3(pose.AxisY.X, pose.AxisY.Y, pose.AxisY.Z), z = new Vector3(pose.AxisZ.X, pose.AxisZ.Y, pose.AxisZ.Z);
            return (new Vector3(pose.Pos.X, pose.Pos.Y, pose.Pos.Z), Quaternion.LookRotation(z, y));
        }

        private static SrcVec C(Vector3 v) => new SrcVec { X = v.x, Y = v.y, Z = v.z };
    }
}
