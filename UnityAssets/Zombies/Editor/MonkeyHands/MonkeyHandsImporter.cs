using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZombiesMonkeyHands.ModelFormats;

namespace ZombiesMonkeyHands.Editor
{
    /// <summary>
    /// The Cymbal Monkey in first person, the original's way: the original's own viewmodel animations of the monkey (GrayWolf
    /// vm_monkey_bomb_*, converted with every arm bone by 99_Tools\cast_to_se.py --full-anims into
    /// Zombies/MonkeyView/full_anims) retargeted onto EFT's arms, the way the perk bottle's drink is, for the M67's
    /// hands rig (the monkey is an M67 clone, so EFT gives it the M67's hands). Only clips are made: the client plays
    /// them over the M67's own animation while a monkey is in the hands (ZombiesMonkeyHands), timed to EFT's grenade
    /// states, and the monkey model already sits in place of the M67's.
    ///   zm_monkey_vm_raise  pull-pin 0-45: up out of the pocket, the key wound (the original's key click at 43)
    ///   zm_monkey_vm_wind   pull-pin 45-90: held up, cymbals going, looped while it's held
    ///   zm_monkey_vm_throw  throw 0-15: the throw
    ///   zm_monkey_vm_idle   idle: held still
    /// </summary>
    public static class MonkeyHandsImporter
    {
        private static readonly (string clip, string anim, int first, int last, bool loop)[] Clips =
        {
            ("zm_monkey_vm_raise", "vm_monkey_bomb_pullpin", 0, 45, false),
            ("zm_monkey_vm_wind", "vm_monkey_bomb_pullpin", 45, 90, true),
            ("zm_monkey_vm_throw", "vm_monkey_bomb_throw", 0, 15, false),
            ("zm_monkey_vm_idle", "vm_monkey_bomb_idle", 0, 1, true),
        };

        /// <summary>Which the original anim and frames a clip was baked from (for the preview).</summary>
        public static (string anim, int first, int last) Source(string clip)
        {
            var c = Clips.First(x => x.clip == clip);
            return (c.anim, c.first, c.last);
        }

        /// <summary>The clips' asset paths, for the bundle.</summary>
        public static string[] ClipPaths => Clips.Select(c => $"{RgPaths.Clips}/{c.clip}.anim").Where(File.Exists).ToArray();

        /// <summary>
        /// The monkey's placement: the original's monkey anims keep their hands close to the original's camera (the shoulders are keyed
        /// far back and down), so the whole the original view is put on EFT's camera (eye anchor only). The gun anchor the Ray
        /// Gun uses (the original's right wrist on the M67's resting palm) pushed it all 71 cm forward, the arms straight out.
        /// </summary>
        public static Vector3 AnchorBlend = Vector3.zero, ViewNudge = Vector3.zero;

        public static void Configure()
        {
            RgRetarget.AnchorBlend = AnchorBlend;
            RgRetarget.ViewNudge = ViewNudge;
            // The original's idle holds the left hand far down out of view: no shoulder moved to reach it (it bent every clip)
            RgRetarget.MaxShoulderShift = 0f;
        }

        [MenuItem("Zombies/9. Import monkey hands (first person)")]
        public static void Import()
        {
            RgPaths.EnsureDirs();
            Configure();
            SrcModel gun = RgClassic.LoadModel();
            SrcModel bind = SrcModel.Load(Path.Combine(RgPaths.ModelDir, "am_t5_thundergun.xmodel_bin"));
            Debug.Log($"[Zombies] Monkey hands: view model {gun.Bones.Count} bones, rig from reference/{RgPaths.BaseGun}_rig.json");
            var rig = new Dictionary<string, Transform>();
            GameObject root = RgRig.Build(RgPaths.BaseModel, rig);
            try
            {
                Transform weapon = rig["weapon"];
                var jgun = new GameObject(gun.Bones[0].Name).transform;
                jgun.SetParent(weapon, false);
                jgun.gameObject.layer = weapon.gameObject.layer;
                Quaternion jgunLocal = Quaternion.Inverse(weapon.rotation) * RgSpace.Rot(gun.Bones[0].Rest);
                jgun.localRotation = jgunLocal;
                Dictionary<string, Transform> gunBones = BuildGunSkeleton(gun, jgun);

                var anims = new Dictionary<string, SrcAnim>();
                SrcAnim Load(string name)
                {
                    if (!anims.TryGetValue(name, out SrcAnim a))
                        anims[name] = a = RgClassic.LoadAnim(name, gun, bind, out _);
                    return a;
                }
                SrcAnim idle = Load("vm_monkey_bomb_idle");
                var retarget = new RgRetarget(root.transform, rig, gun, gunBones, jgunLocal, bind, idle);
                Debug.Log($"[Zombies] Monkey hands: view offset {retarget.Offset}, shoulders moved {retarget.ShoulderShifts}");
                retarget.PlaceMarkers(idle, null);

                var warnings = new List<string>();
                foreach (var c in Clips)
                {
                    SrcAnim a = Load(c.anim);
                    var clipWarnings = new List<string>();
                    AnimationClip clip = retarget.BakeClip(c.clip, a, c.first, Math.Min(c.last, a.Frames.Count - 1), c.loop, clipWarnings);
                    string path = $"{RgPaths.Clips}/{c.clip}.anim";
                    AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(clip, path);
                    Debug.Log($"[Zombies] Monkey hands clip {c.clip}: {clip.length:0.00} s, {AnimationUtility.GetCurveBindings(clip).Length} curves");
                    if (clipWarnings.Count > 0)
                        warnings.Add($"{c.clip}: {clipWarnings.Count} frame(s) out of reach, first: {clipWarnings[0]}");
                }
                foreach (string w in warnings)
                    Debug.LogWarning("[Zombies] Monkey hands: " + w);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static Dictionary<string, Transform> BuildGunSkeleton(SrcModel gun, Transform jgun)
        {
            var bones = new Dictionary<string, Transform> { [gun.Bones[0].Name] = jgun };
            Quaternion[] world = gun.Bones.Select(b => RgSpace.Rot(b.Rest)).ToArray();
            Vector3[] pos = gun.Bones.Select(b => RgSpace.Pos(b.Rest.Pos)).ToArray();
            for (int i = 1; i < gun.Bones.Count; i++)
            {
                SrcBone b = gun.Bones[i];
                var t = new GameObject(b.Name).transform;
                t.SetParent(bones[gun.Bones[b.Parent].Name], false);
                t.gameObject.layer = jgun.gameObject.layer;
                Quaternion parentRot = world[b.Parent];
                t.localPosition = Quaternion.Inverse(parentRot) * (pos[i] - pos[b.Parent]);
                t.localRotation = Quaternion.Inverse(parentRot) * world[i];
                bones[b.Name] = t;
            }
            return bones;
        }
    }
}
