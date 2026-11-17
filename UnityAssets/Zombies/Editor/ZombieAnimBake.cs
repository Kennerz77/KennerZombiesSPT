using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Retargets the zombie animation pack (Anims/Source/*.fbx, an Unreal-mannequin rig, imported as Humanoid) onto the
/// game's body skeleton and writes the result as plain data: data/zombie_anims.json, every clip's bone rotations per
/// frame under the game's own bone names. The plugin plays that onto a zombie's bones (ZombiesAnim), so nothing goes
/// into a bundle.
/// How: the game's skeleton (Anims/eft_skeleton.json, dumped from its skeleton.bundle) is rebuilt here, turned from its
/// weapon-holding rest pose into a T-pose (arms out, legs straight) and given a Humanoid avatar. Each clip is sampled on
/// its own model, read as a human pose (HumanPoseHandler) and set on the game skeleton, whose bones are then recorded.
/// Run: Zombies > Animations > Bake zombie animations, or ZombieAnimBake.BakeBatch from the command line.
/// </summary>
public static class ZombieAnimBake
{
    private const string SourceDir = "Assets/Zombies/Anims/Source";
    private const string SkeletonJson = "Assets/Zombies/Anims/eft_skeleton.json";
    private const float Fps = 30f;

    // The game's bones the Humanoid avatar maps (twist, gear and IK bones are left to follow)
    private static readonly (string human, string bone)[] HumanMap =
    {
        ("Hips", "Base HumanPelvis"), ("Spine", "Base HumanSpine1"), ("Chest", "Base HumanSpine2"), ("UpperChest", "Base HumanSpine3"),
        ("Neck", "Base HumanNeck"), ("Head", "Base HumanHead"),
        ("LeftShoulder", "Base HumanLCollarbone"), ("LeftUpperArm", "Base HumanLUpperarm"), ("LeftLowerArm", "Base HumanLForearm1"), ("LeftHand", "Base HumanLPalm"),
        ("RightShoulder", "Base HumanRCollarbone"), ("RightUpperArm", "Base HumanRUpperarm"), ("RightLowerArm", "Base HumanRForearm1"), ("RightHand", "Base HumanRPalm"),
        ("LeftUpperLeg", "Base HumanLThigh1"), ("LeftLowerLeg", "Base HumanLCalf"), ("LeftFoot", "Base HumanLFoot"), ("LeftToes", "Base HumanLToe"),
        ("RightUpperLeg", "Base HumanRThigh1"), ("RightLowerLeg", "Base HumanRCalf"), ("RightFoot", "Base HumanRFoot"), ("RightToes", "Base HumanRToe"),
    };

    // Recorded: the mapped bones plus the ones between them that move with them (twist bones, the ribcage)
    private static readonly string[] Recorded =
    {
        "Base HumanPelvis", "Base HumanSpine1", "Base HumanSpine2", "Base HumanSpine3", "Base HumanRibcage", "Base HumanNeck", "Base HumanHead",
        "Base HumanLCollarbone", "Base HumanLUpperarm", "Base HumanLForearm1", "Base HumanLForearm2", "Base HumanLForearm3", "Base HumanLPalm",
        "Base HumanRCollarbone", "Base HumanRUpperarm", "Base HumanRForearm1", "Base HumanRForearm2", "Base HumanRForearm3", "Base HumanRPalm",
        "Base HumanLThigh1", "Base HumanLThigh2", "Base HumanLCalf", "Base HumanLFoot", "Base HumanLToe",
        "Base HumanRThigh1", "Base HumanRThigh2", "Base HumanRCalf", "Base HumanRFoot", "Base HumanRToe",
    };

    [MenuItem("Zombies/Animations/Bake zombie animations")]
    public static void Bake() => Run(false);

    public static void BakeBatch()
    {
        try
        {
            Run(true);
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[Zombies] anim bake failed: " + e);
            EditorApplication.Exit(1);
        }
    }

    private static void Run(bool batch)
    {
        AssetDatabase.Refresh();
        var bones = BuildSkeleton(out GameObject root);
        try
        {
            TPose(bones);
            Avatar avatar = BuildAvatar(root, bones);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
                throw new Exception("the game skeleton's Humanoid avatar isn't valid");
            var target = new HumanPoseHandler(avatar, root.transform);
            Transform[] recorded = Recorded.Select(n => bones[n]).ToArray();
            Transform pelvis = bones["Base HumanPelvis"];
            Transform rootJoint = bones["Root_Joint"];
            Vector3 restPelvis = rootJoint.InverseTransformPoint(pelvis.position);

            var json = new StringBuilder();
            json.Append("{\"bones\":[").Append(string.Join(",", Recorded.Select(n => "\"" + n + "\""))).Append("],\"fps\":").Append(F(Fps));
            // Each bone's parent in the full skeleton: the zombies' simplified AI skeleton lacks a few (Spine1, Ribcage,
            // Thigh2, Toes), and the plugin folds a missing parent's rotation into its child
            json.Append(",\"parents\":[").Append(string.Join(",", Recorded.Select(n => "\"" + (bones[n].parent != null ? bones[n].parent.name : "") + "\""))).Append(']');
            json.Append(",\"restPelvis\":[").Append(F(restPelvis.x)).Append(',').Append(F(restPelvis.y)).Append(',').Append(F(restPelvis.z)).Append("],\"clips\":[");
            bool firstClip = true;
            foreach (string path in Directory.GetFiles(SourceDir, "*.fbx").OrderBy(p => p))
            {
                string file = path.Replace('\\', '/');
                Avatar srcAvatar = AssetDatabase.LoadAllAssetsAtPath(file).OfType<Avatar>().FirstOrDefault();
                AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(file).OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(file);
                if (srcAvatar == null || !srcAvatar.isHuman || clip == null || model == null)
                {
                    Debug.LogWarning($"[Zombies] anim bake: {file} skipped (avatar {(srcAvatar == null ? "none" : srcAvatar.isHuman ? "human" : "not human")}, clip {(clip == null ? "none" : clip.name)})");
                    continue;
                }
                GameObject src = UnityEngine.Object.Instantiate(model);
                try
                {
                    var animator = src.GetComponent<Animator>() ?? src.AddComponent<Animator>();
                    animator.avatar = srcAvatar;
                    animator.applyRootMotion = false;
                    var source = new HumanPoseHandler(srcAvatar, src.transform);
                    int frames = Mathf.Max(2, Mathf.RoundToInt(clip.length * Fps) + 1);
                    var pose = new HumanPose();
                    var rot = new StringBuilder();
                    var hips = new StringBuilder();
                    Vector3 firstBody = Vector3.zero, lastBody = Vector3.zero;
                    for (int f = 0; f < frames; f++)
                    {
                        float t = Mathf.Min(clip.length, f / Fps);
                        clip.SampleAnimation(src, t);
                        source.GetHumanPose(ref pose);
                        if (f == 0)
                            firstBody = pose.bodyPosition;
                        lastBody = pose.bodyPosition;
                        // In place: the clip's travel is taken out (the plugin moves the zombie), its height and sway kept
                        Vector3 body = pose.bodyPosition;
                        pose.bodyPosition = new Vector3(0f, body.y, 0f);
                        pose.bodyRotation = Quaternion.Euler(0f, 0f, 0f) * pose.bodyRotation;
                        target.SetHumanPose(ref pose);
                        if (f > 0)
                        {
                            rot.Append(',');
                            hips.Append(',');
                        }
                        rot.Append('[');
                        for (int b = 0; b < recorded.Length; b++)
                        {
                            Quaternion q = recorded[b].localRotation;
                            if (b > 0)
                                rot.Append(',');
                            rot.Append(F(q.x)).Append(',').Append(F(q.y)).Append(',').Append(F(q.z)).Append(',').Append(F(q.w));
                        }
                        rot.Append(']');
                        Vector3 p = rootJoint.InverseTransformPoint(pelvis.position);
                        hips.Append('[').Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append(']');
                    }
                    Vector3 travel = lastBody - firstBody;
                    travel.y = 0f;
                    float speed = clip.length > 0f ? travel.magnitude / clip.length : 0f;
                    string name = Path.GetFileNameWithoutExtension(file).Replace("AS_NAAT_Zombie_", "").Replace("AS_NAAT_", "");
                    if (!firstClip)
                        json.Append(',');
                    firstClip = false;
                    json.Append("{\"name\":\"").Append(name).Append("\",\"length\":").Append(F(clip.length)).Append(",\"frames\":").Append(frames)
                        .Append(",\"loop\":").Append(clip.isLooping || name.Contains("Idle") || name.Contains("Walk") || name.Contains("Run") || name.Contains("Crawl") && !name.Contains("Attack") ? "true" : "false")
                        .Append(",\"speed\":").Append(F(speed))
                        .Append(",\"rot\":[").Append(rot).Append("],\"hips\":[").Append(hips).Append("]}");
                    Debug.Log($"[Zombies] anim bake: {name} {clip.length:0.00} s, {frames} frames, travels {speed:0.00} m/s");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(src);
                }
            }
            json.Append("]}");
            string outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "data", "zombie_anims.json"));
            File.WriteAllText(outPath, json.ToString());
            Debug.Log($"[Zombies] anim bake: wrote {outPath} ({new FileInfo(outPath).Length / 1024} KB)");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    // ---------- The game's skeleton ----------

    [Serializable]
    private class Bone
    {
        public string name;
        public long parent;
        public float[] pos, rot, scale;
    }

    private static Dictionary<string, Transform> BuildSkeleton(out GameObject root)
    {
        // eft_skeleton.json: { "<pathId>": { name, parent, pos[3], rot[4], scale[3] } }
        string text = File.ReadAllText(SkeletonJson);
        var bones = new Dictionary<long, Bone>();
        foreach (var entry in ParseObject(text))
            bones[long.Parse(entry.Key, CultureInfo.InvariantCulture)] = entry.Value;
        var made = new Dictionary<long, Transform>();
        root = null;
        Transform Make(long id)
        {
            if (made.TryGetValue(id, out Transform t))
                return t;
            Bone b = bones[id];
            var go = new GameObject(b.name);
            t = go.transform;
            if (b.parent != 0 && bones.ContainsKey(b.parent))
                t.SetParent(Make(b.parent), false);
            t.localPosition = new Vector3(b.pos[0], b.pos[1], b.pos[2]);
            t.localRotation = new Quaternion(b.rot[0], b.rot[1], b.rot[2], b.rot[3]);
            t.localScale = new Vector3(b.scale[0], b.scale[1], b.scale[2]);
            made[id] = t;
            return t;
        }
        foreach (long id in bones.Keys)
            Make(id);
        var byName = new Dictionary<string, Transform>();
        foreach (Transform t in made.Values)
        {
            byName[t.name] = t;
            if (t.parent == null)
                root = t.gameObject;
        }
        return byName;
    }

    // A tiny reader for the dump's flat shape (JsonUtility can't read a dictionary)
    private static IEnumerable<KeyValuePair<string, Bone>> ParseObject(string text)
    {
        int i = text.IndexOf('{') + 1;
        while (true)
        {
            int k0 = text.IndexOf('"', i);
            if (k0 < 0)
                yield break;
            int k1 = text.IndexOf('"', k0 + 1);
            string key = text.Substring(k0 + 1, k1 - k0 - 1);
            int o0 = text.IndexOf('{', k1);
            int depth = 0, o1 = o0;
            for (; o1 < text.Length; o1++)
            {
                if (text[o1] == '{')
                    depth++;
                else if (text[o1] == '}' && --depth == 0)
                    break;
            }
            yield return new KeyValuePair<string, Bone>(key, JsonUtility.FromJson<Bone>(text.Substring(o0, o1 - o0 + 1)));
            i = o1 + 1;
        }
    }

    // ---------- T-pose ----------

    // The rest pose holds a gun (arms forward and bent, knees apart): arms straight out to the sides and legs straight
    // down, which is what Unity's Humanoid expects. Only these bones turn; their children keep their own rotations.
    private static void TPose(Dictionary<string, Transform> b)
    {
        // Facing +Z (the skeleton's toes point that way); left is -X
        Aim(b["Base HumanLUpperarm"], b["Base HumanLForearm1"], Vector3.left);
        Aim(b["Base HumanLForearm1"], b["Base HumanLPalm"], Vector3.left);
        Aim(b["Base HumanLPalm"], b["Base HumanLDigit31"], Vector3.left);
        Aim(b["Base HumanRUpperarm"], b["Base HumanRForearm1"], Vector3.right);
        Aim(b["Base HumanRForearm1"], b["Base HumanRPalm"], Vector3.right);
        Aim(b["Base HumanRPalm"], b["Base HumanRDigit31"], Vector3.right);
        Aim(b["Base HumanLThigh1"], b["Base HumanLCalf"], Vector3.down);
        Aim(b["Base HumanLCalf"], b["Base HumanLFoot"], Vector3.down);
        Aim(b["Base HumanRThigh1"], b["Base HumanRCalf"], Vector3.down);
        Aim(b["Base HumanRCalf"], b["Base HumanRFoot"], Vector3.down);
    }

    private static void Aim(Transform bone, Transform toward, Vector3 dir)
    {
        Vector3 now = toward.position - bone.position;
        if (now.sqrMagnitude < 1e-8f)
            return;
        bone.rotation = Quaternion.FromToRotation(now.normalized, dir) * bone.rotation;
    }

    private static Avatar BuildAvatar(GameObject root, Dictionary<string, Transform> bones)
    {
        var human = HumanMap.Where(m => bones.ContainsKey(m.bone)).Select(m => new HumanBone
        {
            humanName = HumanTrait.BoneName.First(n => n.Replace(" ", "") == m.human),
            boneName = m.bone,
            limit = new HumanLimit { useDefaultValues = true },
        }).ToArray();
        var skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
        {
            name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
        }).ToArray();
        var description = new HumanDescription
        {
            human = human, skeleton = skeleton,
            upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
            armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false,
        };
        Avatar avatar = AvatarBuilder.BuildHumanAvatar(root, description);
        Debug.Log($"[Zombies] anim bake: game skeleton avatar valid {avatar.isValid}, human {avatar.isHuman}");
        return avatar;
    }
}

/// <summary>The pack's clips come in as Humanoid (their Unreal-mannequin rig maps itself), looping where they loop.</summary>
public class ZombieAnimImport : AssetPostprocessor
{
    private void OnPreprocessModel()
    {
        if (!assetPath.Replace('\\', '/').StartsWith("Assets/Zombies/Anims/Source/"))
            return;
        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = true;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
    }
}
