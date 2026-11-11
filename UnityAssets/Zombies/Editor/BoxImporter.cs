using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Zombies.SEFormats;

/// <summary>
/// Builds Assets/Zombies/zm_box.prefab, the original's mystery box, straight from Greyhound's SEModel/SEAnim exports the way the
/// Pack-a-Punch machine is built: the skinned box (p7_zm_shi_magic_box: the one with the bones the original's box anims drive,
/// j_hinge / tag_animate / tag_origin, wearing the standard the original box textures), its anim states (arrive, open, close,
/// leave), a "WeaponPivot" driven by o_zombie_magic_box_weapon_rise (the weapon models are parented to it in game; the
/// anim's "switch" notetracks are the the original weapon-cycle timing the plugin uses), a "TeddyPivot" for the bear, colliders,
/// the glowing question marks, a light, and 3D audio objects the plugin drives.
/// </summary>
public static class BoxImporter
{
    // The asset export folder on the author's PC: the KZ_EXPORT_ROOT user environment variable (kept out of the source)
    private static readonly string ExportRoot = System.Environment.GetEnvironmentVariable("KZ_EXPORT_ROOT", System.EnvironmentVariableTarget.User) ?? "";
    private const string BoxModel = "p7_zm_shi_magic_box";
    private static readonly string AnimDir = ExportRoot + @"\xanims";

    private const string Root = "Assets/Zombies";
    private const string GeneratedDir = Root + "/Generated";
    private const string TextureDir = Root + "/Textures";
    private const string MaterialDir = Root + "/Materials";
    private const string AudioDir = Root + "/Audio";
    public const string PrefabPath = Root + "/zm_box.prefab";

    // The original game units are inches
    private const float Scale = 0.0254f;

    // The original game is right-handed Z-up; Unity is left-handed Y-up. The box's front is the original game -Y, mapped to Unity +Z, as on the
    // Pack-a-Punch. The mapping mirrors one axis (det -1), so quaternion xyz = -(M * xyz).
    private static Vector3 ToUnity(SEVec3 v) => new Vector3(-v.X, v.Z, -v.Y) * Scale;
    private static Vector3 ToUnityDir(SEVec3 v) => new Vector3(-v.X, v.Z, -v.Y);
    private static Quaternion ToUnity(SEQuat q) => new Quaternion(q.X, -q.Z, q.Y, q.W);

    // Loop: the whole anim, over and over (the power switch's idle shake)
    private enum ClipMode { RestPose, OneShot, HoldLastFrame, Loop }

    [MenuItem("Zombies/1. Import mystery box")]
    public static void Import()
    {
        foreach (string dir in new[] { GeneratedDir, TextureDir, MaterialDir })
            Directory.CreateDirectory(dir);

        string modelDir = Path.Combine(ExportRoot, "xmodels", BoxModel);
        SEModel model = SEModel.Load(Path.Combine(modelDir, BoxModel + "_LOD0.semodel"));
        Debug.Log($"[Zombies] Box: {model.Bones.Count} bones ({string.Join(", ", model.Bones.Select(b => b.Name))}), " +
                  $"{model.Meshes.Count} meshes, {model.Materials.Count} materials ({string.Join(", ", model.Materials.Select(m => m.Name))})");

        // Which bone each mesh follows (its vertices' strongest weight): the lid must be on j_hinge to open
        foreach (SEMesh m in model.Meshes)
        {
            string material = m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 ? model.Materials[m.MaterialIndices[0]].Name : "?";
            var byBone = Enumerable.Range(0, m.VertexCount).GroupBy(v => ToBoneWeight(m, v).boneIndex0)
                .Select(g => $"{model.Bones[g.Key].Name} {g.Count()}");
            Debug.Log($"[Zombies] Mesh {material}: {m.VertexCount} verts, max influence {m.MaxInfluence}, bones: {string.Join(", ", byBone)}");
        }

        var root = new GameObject("zm_box");
        try
        {
            Transform[] bones = BuildSkeleton(model, root.transform);
            Mesh mesh = BuildModel(model, modelDir, bones, root.transform, "zm_box_mesh");

            // The box's own states. Unlike the PaP machine, tag_origin is animated too: arrive and leave move the
            // whole box (it drops in from above, lifts away), and the prefab's root stays put
            AnimatorController controller = BuildController("zm_box", model, bones, root.transform, new[]
            {
                ("idle", (string)null, ClipMode.RestPose),
                ("arrive", "o_zombie_magic_box_arrive", ClipMode.OneShot),
                ("open", "o_zombie_magic_box_open", ClipMode.OneShot),
                ("opened", "o_zombie_magic_box_open", ClipMode.HoldLastFrame),
                ("close", "o_zombie_magic_box_close", ClipMode.OneShot),
                ("leave", "o_zombie_magic_box_leave", ClipMode.OneShot),
                ("gone", "o_zombie_magic_box_leave", ClipMode.HoldLastFrame),
            });
            AddAnimator(root, controller);

            // The weapon (and the bear) rise out of the box on their own tag_origin: a pivot each, driven by its anim
            AddPivot(root.transform, "WeaponPivot", new[]
            {
                ("down", "o_zombie_magic_box_weapon_rise", ClipMode.RestPose),
                ("rise", "o_zombie_magic_box_weapon_rise", ClipMode.OneShot),
                ("up", "o_zombie_magic_box_weapon_rise", ClipMode.HoldLastFrame),
            });
            Transform teddyPivot = AddPivot(root.transform, "TeddyPivot", new[]
            {
                ("down", "o_zombie_magic_box_teddy_rise", ClipMode.RestPose),
                ("rise", "o_zombie_magic_box_teddy_rise", ClipMode.OneShot),
            });
            AddTeddy(teddyPivot.Find("tag_origin"));
            AddHay(bones.First(b => b.name == "tag_animate"), mesh.bounds);
            AddStand(root.transform, mesh.bounds);

            var colliders = new GameObject("Colliders").transform;
            colliders.SetParent(root.transform, false);
            var box = colliders.gameObject.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size;

            // A warm glow rising out of the open box (the original's light), switched by the plugin
            var lightGo = new GameObject("BoxLight");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = mesh.bounds.center + Vector3.up * (mesh.bounds.extents.y + 0.3f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.85f, 0.55f);
            light.intensity = 0f;
            light.range = 7f; // the box is drawn at 0.35: about 2.5 m in game
            light.shadows = LightShadows.None;

            AddAudio(root.transform, "Audio_Open", "box_open.wav", false, 0.8f);
            AddAudio(root.transform, "Audio_Close", "box_close.wav", false, 0.8f);
            AddAudio(root.transform, "Audio_Music", "box_music.wav", false, 0.6f);
            AddAudio(root.transform, "Audio_Spin", "box_spin.wav", false, 0.6f);
            AddAudio(root.transform, "Audio_Loop", "box_loop.wav", true, 0.25f);
            AddAudio(root.transform, "Audio_Laugh", "box_laugh.wav", false, 0.8f);
            AddAudio(root.transform, "Audio_Whoosh", "box_whoosh.wav", false, 0.8f);
            AddAudio(root.transform, "Audio_Poof", "box_poof.wav", false, 0.8f);
            AddAudio(root.transform, "Audio_Disappear", "box_disappear.wav", false, 0.8f);
            AddAudio(root.transform, "Audio_Land", "box_land.wav", false, 0.8f);
            AddAudio(root.transform, "Audio_ByeBye", "box_byebye.wav", false, 0.9f);

            AssetDatabase.DeleteAsset(PrefabPath);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Zombies] Prefab saved: {PrefabPath} (bounds {mesh.bounds.size} m, centre {mesh.bounds.center})");
            BuildFake(mesh.bounds);
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    public const string FakePrefabPath = Root + "/zm_box_fake.prefab";

    // The empty spot's look (the user, 2026-11-01): smaller black cases, a plank twice as wide
    private const float CaseScale = 0.8f, PlankWiden = 2f;
    private static readonly Color CaseColour = new Color(0.12f, 0.12f, 0.13f);

    /// <summary>
    /// What stands at a box spot while the box is elsewhere (the original's Der Eisendrache): the cinder blocks with a plank on
    /// them, the old trunk cases (p7_zm_der_magic_box_fake_trunk) and the teddy sitting on them (p7_zm_der_magic_box_fake,
    /// the bear on j_bear) twitching now and then (o_zombie_magic_box_fake_idle_twitch_a / _b). Same size and origin as
    /// the box, with its own "StandHeight" so the plugin lifts it the same way.
    /// </summary>
    private static void BuildFake(Bounds box)
    {
        var root = new GameObject("zm_box_fake");
        try
        {
            float standHeight = AddStand(root.transform, box);
            var marker = root.transform.Find("StandHeight");

            // A plank along the blocks, the cases on it
            float plankTop = 0f;
            GameObject plank = AddProp("p7_zm_der_plank_wood_2x12x96", root.transform, "Plank");
            if (plank != null)
            {
                Bounds pb = plank.GetComponent<MeshFilter>().sharedMesh.bounds;
                // Its length along the box (X); stretched to the blocks' span, lying flat
                Vector3 size = pb.size;
                Quaternion lie = size.x >= size.y && size.x >= size.z ? Quaternion.identity
                    : size.z >= size.y ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.Euler(0f, 0f, 90f);
                plank.transform.localRotation = lie;
                Vector3 laid = lie * size;
                laid = new Vector3(Mathf.Abs(laid.x), Mathf.Abs(laid.y), Mathf.Abs(laid.z));
                // Thinnest side up
                if (laid.y > laid.z)
                {
                    plank.transform.localRotation = Quaternion.Euler(90f, 0f, 0f) * lie;
                    laid = new Vector3(laid.x, laid.z, laid.y);
                }
                float stretch = box.size.x * 0.9f / laid.x;
                // Twice as wide as one 2x12 (the user, 2026-11-01): the cases sit on it, not over its edges
                Vector3 plankScale = new Vector3(stretch, 1f, 1f);
                Vector3 inPlank = Quaternion.Inverse(plank.transform.localRotation) * Vector3.forward;
                plankScale += new Vector3(Mathf.Abs(inPlank.x), Mathf.Abs(inPlank.y), Mathf.Abs(inPlank.z)) * (PlankWiden - 1f);
                plank.transform.localScale = plankScale;
                Vector3 centre = plank.transform.localRotation * Vector3.Scale(pb.center, plankScale);
                plank.transform.localPosition = new Vector3(box.center.x - centre.x, laid.y * 0.5f - centre.y, box.center.z - centre.z);
                plankTop = laid.y;
                Debug.Log($"[Zombies] Fake plank: {laid} laid, stretched x{stretch:F2}");
            }

            var display = new GameObject("Display").transform;
            display.SetParent(root.transform, false);
            display.localPosition = new Vector3(0f, plankTop, 0f);
            string dir = Path.Combine(ExportRoot, "xmodels");
            float trunkTop = 0f;
            Vector3 trunkCentre = Vector3.zero;
            foreach (string name in new[] { "p7_zm_der_magic_box_fake_trunk", "p7_zm_der_magic_box_fake" })
            {
                string modelDir = Path.Combine(dir, name);
                SEModel model = SEModel.Load(Path.Combine(modelDir, name + "_LOD0.semodel"));
                var holder = new GameObject(name).transform;
                holder.SetParent(display, false);
                Transform[] bones = BuildSkeleton(model, holder);
                Mesh m = BuildModel(model, modelDir, bones, holder, "zm_fake_" + (name.EndsWith("trunk") ? "trunk" : "teddy") + "_mesh");
                Debug.Log($"[Zombies] Fake {name}: bones {string.Join(", ", model.Bones.Select(b => b.Name))}, bounds {m.bounds.size} centre {m.bounds.center}");
                if (name.EndsWith("trunk"))
                {
                    trunkTop = m.bounds.max.y * CaseScale;
                    trunkCentre = m.bounds.center * CaseScale;
                    holder.localScale = Vector3.one * CaseScale;
                    foreach (Renderer r in holder.GetComponentsInChildren<Renderer>())
                        foreach (Material mat in r.sharedMaterials)
                            if (mat != null)
                            {
                                // Black cases (the user, 2026-11-01): the export's maps came out pale grey
                                mat.color = CaseColour;
                                EditorUtility.SetDirty(mat);
                            }
                    // Three cases in a row along the plank, the teddy sits on the middle one (the model is one case)
                    float step = m.bounds.size.x * CaseScale * 1.04f;
                    float[] turns = { 4f, -3f };
                    for (int side = 0; side < 2; side++)
                    {
                        GameObject copy = Object.Instantiate(holder.gameObject, display);
                        copy.name = name + (side == 0 ? "_left" : "_right");
                        copy.transform.localPosition = new Vector3(side == 0 ? -step : step, 0f, 0f);
                        copy.transform.localRotation = Quaternion.Euler(0f, turns[side], 0f);
                    }
                }
                else
                {
                    // The bear's model sits it at the back corner of one big case: onto the middle of the smaller one
                    Vector3 bear = m.bounds.center;
                    float bearBottom = m.bounds.min.y;
                    holder.localPosition = new Vector3(trunkCentre.x - bear.x, trunkTop - bearBottom - 0.02f, trunkCentre.z - bear.z);
                }
                if (!name.EndsWith("trunk"))
                {
                    AnimatorController c = BuildController("zm_box_fake_teddy", model, bones.Where(b => b.name != "tag_origin").ToArray(), holder, new[]
                    {
                        ("idle", (string)null, ClipMode.RestPose),
                        ("twitch_a", "o_zombie_magic_box_fake_idle_twitch_a", ClipMode.OneShot),
                        ("twitch_b", "o_zombie_magic_box_fake_idle_twitch_b", ClipMode.OneShot),
                    });
                    AddAnimator(holder.gameObject, c);
                }
            }
            if (marker == null)
                Debug.LogWarning("[Zombies] Fake: no StandHeight marker");

            AssetDatabase.DeleteAsset(FakePrefabPath);
            PrefabUtility.SaveAsPrefabAsset(root, FakePrefabPath);
            Debug.Log($"[Zombies] Prefab saved: {FakePrefabPath}");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    private static string PropsDir(string name) => Path.GetFullPath(Path.Combine(Application.dataPath, "Zombies/Props", name));

    /// <summary>
    /// Hay inside the box: Der Eisendrache's hay pile made for a long crate (GrayWolf, converted with cast_to_se.py),
    /// fitted to the box's inside and laid on its floor, on tag_animate so it moves with the box.
    /// </summary>
    private static void AddHay(Transform body, Bounds box)
    {
        const string name = "p7_zm_ctl_hay_pile_crate_01_long_b";
        GameObject hay = AddProp(name, body, "Hay");
        if (hay == null)
            return;
        Bounds b = hay.GetComponent<MeshFilter>().sharedMesh.bounds;
        // Its long side along the box's (X), inside the walls, its top a little under the rim
        if (b.size.z > b.size.x)
            hay.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
        Vector3 size = hay.transform.localRotation * b.size;
        size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
        // Stretched to fill the box: its length along the box's, its width across, a little flatter
        float across = box.size.z * 0.8f / size.z, lengthwise = box.size.x * 0.9f / size.x, up = across * 0.8f;
        bool turned = b.size.z > b.size.x;
        hay.transform.localScale = turned ? new Vector3(across, up, lengthwise) : new Vector3(lengthwise, up, across);
        Vector3 centre = hay.transform.localRotation * Vector3.Scale(b.center, hay.transform.localScale);
        float top = box.max.y - box.size.y * 0.2f;
        // Under tag_animate, whose rest is at the box's origin: box-space numbers work as they are
        hay.transform.localPosition = new Vector3(box.center.x - centre.x, top - (centre.y + size.y * up * 0.5f), box.center.z - centre.z);
        Debug.Log($"[Zombies] Hay: model {b.size}, scaled {hay.transform.localScale}, top at {top:F2}");
    }

    /// <summary>
    /// The stand: four two-hole cinder blocks lying across under the box, as in the original (none in the exports, so built here
    /// from boxes at real cinder-block size for the box's 0.35 draw scale), in the original's weathered concrete. They go below
    /// the prefab's origin (the box's anims land it at 0); "StandHeight" tells the plugin how far to lift it all.
    /// </summary>
    private static float AddStand(Transform root, Bounds box)
    {
        const float drawScale = 0.35f;
        // A cinder block: 39 x 19 cm, 26 cm high (taller than a real one: lifts the box a little, the user 2026-11-01),
        // walls about 3 cm (in prefab units: real size over the draw scale)
        float length = 0.39f / drawScale, width = 0.19f / drawScale, height = 0.26f / drawScale, wall = 0.03f / drawScale;
        Material concrete = BuildPropMaterial("mtl_p7_zm_gen_concrete_pillar_damage", PropsDir("p7_zm_gen_concrete_pillar_block_01"));
        var stand = new GameObject("Stand").transform;
        stand.SetParent(root, false);
        float[] along = { -0.38f, -0.13f, 0.13f, 0.38f };
        float[] turn = { 3f, -4f, 2f, -3f };
        for (int i = 0; i < along.Length; i++)
        {
            var block = new GameObject($"CinderBlock_{i}").transform;
            block.SetParent(stand, false);
            block.localPosition = new Vector3(box.center.x + along[i] * box.size.x, -height * 0.5f, box.center.z);
            block.localRotation = Quaternion.Euler(0f, turn[i], 0f);
            // Two holes through it top to bottom: two long walls the block's full length, then the two end walls and the
            // middle web between them (no two parts share a face, which flickered at the corners)
            float inner = width - 2f * wall;
            Part(block, concrete, new Vector3((width - wall) * 0.5f, 0f, 0f), new Vector3(wall, height, length));
            Part(block, concrete, new Vector3(-(width - wall) * 0.5f, 0f, 0f), new Vector3(wall, height, length));
            Part(block, concrete, new Vector3(0f, 0f, (length - wall) * 0.5f), new Vector3(inner, height, wall));
            Part(block, concrete, new Vector3(0f, 0f, -(length - wall) * 0.5f), new Vector3(inner, height, wall));
            Part(block, concrete, Vector3.zero, new Vector3(inner, height, wall));
        }
        var marker = new GameObject("StandHeight").transform;
        marker.SetParent(root, false);
        marker.localPosition = new Vector3(0f, -height, 0f);
        Debug.Log($"[Zombies] Stand: 4 cinder blocks, {height:F2} high (prefab units)");
        return height;
    }

    private static void Part(Transform parent, Material material, Vector3 at, Vector3 size)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(cube.GetComponent<Collider>());
        cube.name = "part";
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = at;
        cube.transform.localScale = size;
        cube.GetComponent<MeshRenderer>().sharedMaterial = material;
    }

    /// <summary>A converted GrayWolf prop as a plain mesh (no bones) under <paramref name="parent"/>.</summary>
    private static GameObject AddProp(string name, Transform parent, string objectName)
    {
        string dir = PropsDir(name);
        string file = Path.Combine(dir, name + "_LOD0.semodel");
        if (!File.Exists(file))
        {
            Debug.LogWarning($"[Zombies] No prop model at {file}");
            return null;
        }
        SEModel model = SEModel.Load(file);
        Material[] materials = model.Materials.Select(m => BuildMaterial(m, dir)).ToArray();
        var holder = new GameObject("holder").transform;
        Transform[] bones = BuildSkeleton(model, holder);
        Mesh mesh = BuildMesh(model, bones, holder, "zm_" + objectName.ToLowerInvariant() + "_mesh");
        mesh.boneWeights = null;
        mesh.bindposes = new Matrix4x4[0];
        AssetDatabase.DeleteAsset($"{GeneratedDir}/{mesh.name}.asset");
        AssetDatabase.CreateAsset(mesh, $"{GeneratedDir}/{mesh.name}.asset");
        Object.DestroyImmediate(holder.gameObject);
        var go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = model.Meshes
            .Select(m => m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 ? materials[m.MaterialIndices[0]] : materials[0])
            .ToArray();
        return go;
    }

    /// <summary>A prop material from cast_to_se.py's baked maps (_images/&lt;material&gt;/&lt;material&gt;_c/_n/_s/_g.png).</summary>
    private static Material BuildPropMaterial(string name, string dir)
    {
        var se = new SEMaterial { Name = name };
        return BuildMaterial(se, dir);
    }

    // tag_origin heights (inches) at the start of o_zombie_magic_box_teddy_rise and the end of ..._weapon_rise
    private const float TeddyAnimStart = 161.27f, WeaponAnimTop = 100.13f;

    // The bear: GrayWolf's p7_zm_teddybear (.cast) converted by C:\Git\99_Tools\cast_to_se.py into Assets/Zombies/Teddy
    private static string TeddyDir => Path.GetFullPath(Path.Combine(Application.dataPath, "Zombies/Teddy"));

    /// <summary>The teddy bear as a plain mesh on the teddy pivot's tag_origin (the rise anim carries it), hidden until used.</summary>
    private static void AddTeddy(Transform tag)
    {
        string file = Path.Combine(TeddyDir, "p7_zm_teddybear_LOD0.semodel");
        if (tag == null || !File.Exists(file))
        {
            Debug.LogWarning($"[Zombies] No teddy model at {file}");
            return;
        }
        SEModel model = SEModel.Load(file);
        Material[] materials = model.Materials.Select(m => BuildMaterial(m, TeddyDir)).ToArray();
        var holder = new GameObject("teddy_root").transform;
        holder.SetParent(tag.parent, false);
        Transform[] bones = BuildSkeleton(model, holder);
        Mesh mesh = BuildMesh(model, bones, holder, "zm_teddy_mesh");
        mesh.boneWeights = null;
        mesh.bindposes = new Matrix4x4[0];
        AssetDatabase.DeleteAsset($"{GeneratedDir}/zm_teddy_mesh.asset");
        AssetDatabase.CreateAsset(mesh, $"{GeneratedDir}/zm_teddy_mesh.asset");
        Object.DestroyImmediate(holder.gameObject);

        var go = new GameObject("Teddy");
        go.transform.SetParent(tag, false);
        // The teddy rise anim starts at 161 in, the weapon rise ends at 100 in: the bear is lowered so it starts with its
        // middle where the last gun hung, and floats up from there
        go.transform.localPosition = new Vector3(0f, (TeddyAnimStart - WeaponAnimTop) * -Scale - mesh.bounds.center.y, 0f);
        // Its face is on the model's other side from the player's (the box's hinge, +Z, faces away from them)
        go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = model.Meshes
            .Select(m => m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 ? materials[m.MaterialIndices[0]] : materials[0])
            .ToArray();
        go.SetActive(false);
        Debug.Log($"[Zombies] Teddy: {mesh.vertexCount} verts, bounds {mesh.bounds.size} m");
    }

    /// <summary>A pivot whose own tag_origin the anim drives (the weapon or bear rising), with its own Animator.</summary>
    private static Transform AddPivot(Transform root, string name, (string state, string anim, ClipMode mode)[] states)
    {
        var pivot = new GameObject(name).transform;
        pivot.SetParent(root, false);
        var origin = new GameObject("tag_origin").transform;
        origin.SetParent(pivot, false);
        // A one-bone "model" so the clip builder can read a rest pose
        var model = new SEModel();
        model.Bones.Add(new SEBone { Name = "tag_origin", Parent = -1, LocalPosition = new SEVec3(), LocalRotation = new SEQuat { W = 1f } });
        AnimatorController controller = BuildController("zm_box_" + name.ToLowerInvariant(), model, new[] { origin }, pivot, states);
        AddAnimator(pivot.gameObject, controller);
        return pivot;
    }

    private static void AddAnimator(GameObject go, AnimatorController controller)
    {
        var animator = go.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
    }

    // ---------- Geometry ----------

    private static Transform[] BuildSkeleton(SEModel model, Transform root)
    {
        var bones = new Transform[model.Bones.Count];
        for (int i = 0; i < model.Bones.Count; i++)
        {
            SEBone b = model.Bones[i];
            var t = new GameObject(b.Name).transform;
            t.SetParent(b.Parent >= 0 ? bones[b.Parent] : root, false);
            t.localPosition = ToUnity(b.LocalPosition);
            t.localRotation = ToUnity(b.LocalRotation);
            bones[i] = t;
        }
        return bones;
    }

    private static Mesh BuildModel(SEModel model, string modelDir, Transform[] bones, Transform root, string name)
    {
        Material[] materials = model.Materials.Select(m => BuildMaterial(m, modelDir)).ToArray();
        Mesh mesh = BuildMesh(model, bones, root, name);
        AssetDatabase.DeleteAsset($"{GeneratedDir}/{name}.asset");
        AssetDatabase.CreateAsset(mesh, $"{GeneratedDir}/{name}.asset");

        var meshGo = new GameObject(name);
        meshGo.transform.SetParent(root, false);
        var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
        smr.rootBone = bones[0];
        smr.updateWhenOffscreen = true;
        smr.sharedMaterials = model.Meshes
            .Select(m => m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 ? materials[m.MaterialIndices[0]] : materials[0])
            .ToArray();
        return mesh;
    }

    private static Mesh BuildMesh(SEModel model, Transform[] bones, Transform root, string name)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var weights = new List<BoneWeight>();
        var submeshes = new List<int[]>();

        foreach (SEMesh m in model.Meshes)
        {
            int baseIndex = vertices.Count;
            for (int v = 0; v < m.VertexCount; v++)
            {
                vertices.Add(ToUnity(m.Positions[v]));
                normals.Add(m.Normals != null ? ToUnityDir(m.Normals[v]).normalized : Vector3.up);
                uvs.Add(m.UVs != null ? new Vector2(m.UVs[v * 2], 1f - m.UVs[v * 2 + 1]) : Vector2.zero);
                weights.Add(ToBoneWeight(m, v));
            }

            int[] tris = m.Faces.Select(i => i + baseIndex).ToArray();
            // Keep the winding whose face normals agree with the vertex normals
            int agree = 0;
            for (int f = 0; f < tris.Length; f += 3)
            {
                Vector3 a = vertices[tris[f]], b = vertices[tris[f + 1]], c = vertices[tris[f + 2]];
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normals[tris[f]]) > 0f)
                    agree++;
            }
            if (agree * 2 < tris.Length / 3)
            {
                for (int f = 0; f < tris.Length; f += 3)
                    (tris[f + 1], tris[f + 2]) = (tris[f + 2], tris[f + 1]);
            }
            submeshes.Add(tris);
        }

        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.boneWeights = weights.ToArray();
        mesh.subMeshCount = submeshes.Count;
        for (int i = 0; i < submeshes.Count; i++)
            mesh.SetTriangles(submeshes[i], i);
        mesh.bindposes = bones.Select(b => b.worldToLocalMatrix * root.localToWorldMatrix).ToArray();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static BoneWeight ToBoneWeight(SEMesh m, int v)
    {
        if (m.MaxInfluence == 0)
            return new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        var top = Enumerable.Range(0, m.MaxInfluence)
            .Select(i => (bone: m.WeightBones[v * m.MaxInfluence + i], weight: m.WeightValues[v * m.MaxInfluence + i]))
            .Where(x => x.weight > 0f)
            .OrderByDescending(x => x.weight)
            .Take(4)
            .ToArray();
        float sum = top.Sum(x => x.weight);
        if (top.Length == 0 || sum <= 0f)
            return new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        var bw = new BoneWeight();
        for (int i = 0; i < top.Length; i++)
        {
            float w = top[i].weight / sum;
            switch (i)
            {
                case 0: bw.boneIndex0 = top[i].bone; bw.weight0 = w; break;
                case 1: bw.boneIndex1 = top[i].bone; bw.weight1 = w; break;
                case 2: bw.boneIndex2 = top[i].bone; bw.weight2 = w; break;
                case 3: bw.boneIndex3 = top[i].bone; bw.weight3 = w; break;
            }
        }
        return bw;
    }

    // ---------- Barricade (Nacht window boards) ----------

    public const string BarricadePrefabPath = Root + "/zm_window.prefab";
    public const string PowerSwitchPrefabPath = Root + "/zm_power_switch.prefab";

    // The F8 editor's door and debris looks (MapEditor.DoorLooks / DebrisLooks) -> the the original model each is: GrayWolf casts
    // converted by C:/Git/99_Tools/cast_to_se.py into Assets/Zombies/Looks/<model>
    private static readonly (string prefab, string model)[] Looks =
    {
        ("zm_door_metal_rusted", "p7_zm_der_door_metal_rusted_left"),
        ("zm_door_wood_church", "p7_zm_ori_church_door_wood"),
        ("zm_door_cellar", "p7_zm_ori_cellar_door_01"),
        ("zm_debris_concrete_rubble", "p7_debris_concrete_rubble_pile_01"),
        ("zm_debris_wood_planks", "p7_debris_wood_plank_old_128_splintered"),
        ("zm_debris_barbed_wire", "p7_zm_ori_barbedwire_barricade_01_wet"),
        ("zm_debris_brick_pile", "p7_brick_broken_worn_dirty_pile_02_red_lt"),
        ("zm_debris_junk_pile", "p7_fxp_debris_metal_scrap_01"),
        ("zm_door_kino_wood", "p7_zm_kin_door_wood"),
        ("zm_door_kino_ornate", "p7_zm_kin_door_wood_deco_01b_single"),
        ("zm_door_bunker", "p7_zm_ori_door_bunker"),
        ("zm_door_barbed_gate", "p7_zm_ori_barbedwire_gate_wet"),
        ("zm_door_metal_rustic", "p7_cai_door_metal_rustic_02_a_l"),
        ("zm_door_sandbags", "p7_zm_ori_sandbags_door"),
        ("zm_door_bookcase", "p7_zm_kin_cabinet_bookcase_wood"),
        ("zm_debris_cave_rocks", "p7_zm_ori_rock_cave_rubble_pile_a"),
        ("zm_debris_concrete_pile", "p7_zm_ori_rubble_concrete_pile"),
        ("zm_debris_mud_rocks", "p7_zm_ori_rubble_mud_debris_concrete_rocks_lg"),
        ("zm_debris_burnt_wagon", "p7_zm_ori_wagon_vintage_wet_burned"),
        ("zm_debris_church_bricks", "p7_zm_ori_church_brick_pile_128x64"),
        ("zm_debris_mud_sandbags", "p7_zm_sta_rubble_mud_debris_sandbag"),
        ("zm_debris_burnt_wood", "p7_zm_sta_pile_wood_burnt_small_01"),
        ("zm_debris_trash_pile", "p7_zm_kin_pile_trash_2x2"),
        ("zm_debris_plank_heap", "p7_zm_isl_barricade_wood_debris_01"),
        ("zm_debris_gorod_rubble", "p7_zm_sta_debris_concrete_rubble_pile_01"),
        ("zm_debris_barbed_wire_2", "p7_zm_ori_barbedwire_barricade_02_wet"),
        ("zm_debris_kino_barbed_wire", "p7_zm_kin_barbed_wire_hallway_01"),
        ("zm_debris_corrugated_metal", "p7_corrugated_sheet_metal_rust_dmg_02"),
    };

    public static string[] LookPrefabPaths => Looks.Concat(PowerUps).Select(l => $"{Root}/{l.prefab}.prefab")
        .Append(RockPrefabPath).Append(MonkeyPrefabPath).Where(File.Exists).ToArray();

    // The original's Cymbal Monkey (wpn_t7_zmb_monkey_bomb_world) with its own cymbal-clapping anim (o_monkey_bomb), converted
    // by C:/Git/99_Tools/cast_to_se.py into Assets/Zombies/Monkey
    public const string MonkeyPrefabPath = Root + "/zm_monkey.prefab";
    private const string MonkeyModel = "wpn_t7_zmb_monkey_bomb_world";
    private static string MonkeyDir => Path.GetFullPath(Path.Combine(Application.dataPath, "Zombies/Monkey"));

    /// <summary>
    /// Builds zm_monkey.prefab: the skinned monkey with an Animator: "rest" (the anim's first frame) and "clap" (its
    /// cymbals going, looped). The plugin puts it on a thrown Cymbal Monkey and plays "clap" once it lands.
    /// </summary>
    [MenuItem("Zombies/8. Import cymbal monkey")]
    public static void ImportMonkey()
    {
        foreach (string dir in new[] { GeneratedDir, TextureDir, MaterialDir })
            Directory.CreateDirectory(dir);
        string file = Path.Combine(MonkeyDir, MonkeyModel + "_LOD0.semodel");
        if (!File.Exists(file))
        {
            Debug.LogError($"[Zombies] No cymbal monkey model at {file}");
            return;
        }
        SEModel model = SEModel.Load(file);
        Debug.Log($"[Zombies] Cymbal monkey: {model.Bones.Count} bones, {model.Meshes.Count} meshes, {model.Materials.Count} materials");
        var root = new GameObject("zm_monkey");
        try
        {
            Transform[] bones = BuildSkeleton(model, root.transform);
            Mesh mesh = BuildModel(model, MonkeyDir, bones, root.transform, "zm_monkey_mesh");
            AnimatorController controller = BuildController("zm_monkey", model, bones, root.transform, new[]
            {
                ("rest", "o_monkey_bomb", ClipMode.RestPose),
                ("clap", "o_monkey_bomb", ClipMode.Loop),
            }, Path.Combine(MonkeyDir, "anims"));
            AddAnimator(root, controller);
            AssetDatabase.DeleteAsset(MonkeyPrefabPath);
            PrefabUtility.SaveAsPrefabAsset(root, MonkeyPrefabPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Zombies] Cymbal monkey prefab saved: {MonkeyPrefabPath} (bounds {mesh.bounds.size} m, centre {mesh.bounds.center})");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // The original's power-ups (the plugin's ZombiesPowerUps: glowing green, spinning): GrayWolf casts converted by
    // C:/Git/99_Tools/cast_to_se.py into Assets/Zombies/PowerUps/<model>
    private static readonly (string prefab, string model)[] PowerUps =
    {
        ("zm_powerup_max_ammo", "p7_zm_power_up_max_ammo"),
        ("zm_powerup_insta_kill", "p7_zm_power_up_insta_kill"),
        ("zm_powerup_double_points", "p7_zm_power_up_double_points"),
        ("zm_powerup_nuke", "p7_zm_power_up_nuke"),
        ("zm_powerup_carpenter", "p7_zm_power_up_carpenter"),
        ("zm_powerup_fire_sale", "p7_zm_power_up_firesale"),
        ("zm_powerup_widows_wine", "p7_zm_power_up_widows_wine"),
    };

    /// <summary>The power-ups as plain prefabs (one static mesh each), like the door and debris looks.</summary>
    [MenuItem("Zombies/6. Import power-ups")]
    public static void ImportPowerUps() => ImportStatic(PowerUps, "PowerUps");

    /// <summary>
    /// Builds a plain prefab for each door and debris look: the model as one static mesh (its bones only place it) with
    /// its baked materials. The plugin fits it to the size set in the editor, so the model's own size doesn't matter.
    /// </summary>
    [MenuItem("Zombies/5. Import door and debris looks")]
    public static void ImportLooks() => ImportStatic(Looks, "Looks");

    private static void ImportStatic((string prefab, string model)[] list, string folder)
    {
        foreach (string d in new[] { GeneratedDir, TextureDir, MaterialDir })
            Directory.CreateDirectory(d);
        foreach (var (prefab, name) in list)
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "Zombies", folder, name));
            string file = Path.Combine(dir, name + "_LOD0.semodel");
            if (!File.Exists(file))
            {
                Debug.LogWarning($"[Zombies] Look {prefab}: no model at {file}");
                continue;
            }
            SEModel model = SEModel.Load(file);
            Material[] materials = model.Materials.Select(m => BuildMaterial(m, dir)).ToArray();
            var holder = new GameObject("holder").transform;
            Transform[] bones = BuildSkeleton(model, holder);
            Mesh mesh = BuildMesh(model, bones, holder, prefab + "_mesh");
            mesh.boneWeights = null;
            mesh.bindposes = new Matrix4x4[0];
            AssetDatabase.DeleteAsset($"{GeneratedDir}/{prefab}_mesh.asset");
            AssetDatabase.CreateAsset(mesh, $"{GeneratedDir}/{prefab}_mesh.asset");
            Object.DestroyImmediate(holder.gameObject);

            var root = new GameObject(prefab);
            try
            {
                root.AddComponent<MeshFilter>().sharedMesh = mesh;
                root.AddComponent<MeshRenderer>().sharedMaterials = model.Meshes
                    .Select(m => m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 ? materials[m.MaterialIndices[0]] : materials[0])
                    .ToArray();
                string path = $"{Root}/{prefab}.prefab";
                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Zombies] Look {prefab} ({name}): {mesh.vertexCount} verts, bounds {mesh.bounds.size}");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
        AssetDatabase.SaveAssets();
    }

    // The blue 115 rock: the original's Origins artifact piece (PaP's green hideout rock), its gem colour turned to 115 blue
    // (the textures in Assets/Zombies/Rock are hue-shifted copies of Greyhound's export)
    private const string RockModel = "p7_zm_ori_artifact_piece_sml_green";
    public const string RockPrefabPath = Root + "/zm_rock_115.prefab";
    private static readonly Color RockBlue = new Color(0.3f, 0.7f, 1f);
    private const float RockLength = 0.3f;

    /// <summary>
    /// Builds zm_rock_115.prefab like PaP's green rock: the carved stone and the gem, which glows 115 blue through the
    /// Pack-a-Punch crystal emissive with a soft blue light inside. The decal layer is left out (it would flicker
    /// without the original's decal shader). Pivot at the bottom centre, about 0.3 m long.
    /// </summary>
    [MenuItem("Zombies/7. Import blue 115 rock")]
    public static void ImportRock()
    {
        foreach (string d in new[] { GeneratedDir, TextureDir, MaterialDir })
            Directory.CreateDirectory(d);
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "Zombies/Rock", RockModel));
        string file = Path.Combine(dir, RockModel + "_LOD0.semodel");
        if (!File.Exists(file))
        {
            Debug.LogWarning($"[Zombies] No rock model at {file}");
            return;
        }
        string Image(string material, string name) => Path.Combine(dir, "_images", material, name);

        var stone = new Material(Shader.Find("Standard (Specular setup)")) { name = "zm_rock_115_stone" };
        stone.SetTexture("_MainTex", ImportTexture(Image("mtl_p7_zm_ori_artifact_piece_01", "i_t7_zm_isl_rock_carving_fingerprint_01_flat_c.png"), TextureImporterType.Default, true));
        stone.SetTexture("_BumpMap", ImportTexture(Image("mtl_p7_zm_ori_artifact_piece_01", "i_mtl_p7_zm_ori_artifact_piece_01_n.png"), TextureImporterType.NormalMap, false));
        stone.EnableKeyword("_NORMALMAP");
        stone.SetColor("_SpecColor", new Color(0.1f, 0.1f, 0.1f));
        stone.SetFloat("_Glossiness", 0.25f);
        AssetDatabase.DeleteAsset($"{MaterialDir}/zm_rock_115_stone.mat");
        AssetDatabase.CreateAsset(stone, $"{MaterialDir}/zm_rock_115_stone.mat");

        const string gemDir = "mtl_p7_zm_ori_artifact_piece_gem_green";
        var gem = new Material(Shader.Find("Standard (Specular setup)")) { name = "zm_rock_115_gem" };
        gem.SetTexture("_MainTex", ImportTexture(Image(gemDir, "i_mtl_p7_zm_ori_artifact_piece_gem_blue_c.png"), TextureImporterType.Default, true));
        gem.SetTexture("_BumpMap", ImportTexture(Image(gemDir, "i_mtl_p7_zm_isl_crystal_115_01_n.png"), TextureImporterType.NormalMap, false));
        gem.EnableKeyword("_NORMALMAP");
        gem.SetColor("_SpecColor", new Color(0.35f, 0.4f, 0.5f));
        gem.SetFloat("_Glossiness", 0.75f);
        gem.SetTexture("_EmissionMap", ImportTexture(Image(gemDir, "i_mtl_p7_zm_ctl_packapunch_crystal_e.png"), TextureImporterType.Default, true));
        gem.SetColor("_EmissionColor", RockBlue * 2.4f);
        gem.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        gem.EnableKeyword("_EMISSION");
        AssetDatabase.DeleteAsset($"{MaterialDir}/zm_rock_115_gem.mat");
        AssetDatabase.CreateAsset(gem, $"{MaterialDir}/zm_rock_115_gem.mat");

        SEModel model = SEModel.Load(file);
        var holder = new GameObject("holder").transform;
        Transform[] bones = BuildSkeleton(model, holder);
        Mesh mesh = BuildMesh(model, bones, holder, "zm_rock_115_mesh");
        mesh.boneWeights = null;
        mesh.bindposes = new Matrix4x4[0];
        Object.DestroyImmediate(holder.gameObject);
        // A material per sub-mesh; the decal's sub-mesh is emptied
        var materials = new Material[mesh.subMeshCount];
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            SEMesh part = i < model.Meshes.Count ? model.Meshes[i] : null;
            string name = part != null && part.MaterialIndices.Length > 0 && part.MaterialIndices[0] >= 0 ? model.Materials[part.MaterialIndices[0]].Name : "";
            if (name.Contains("decal"))
                mesh.SetTriangles(new int[0], i);
            materials[i] = name.Contains("gem") ? gem : stone;
        }
        // Pivot at the bottom centre, 0.3 m along its longest side (PaP's rock size; F8's scale changes it per rock)
        Vector3[] vertices = mesh.vertices;
        Bounds bounds = mesh.bounds;
        Vector3 shift = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
        float fit = RockLength / Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = (vertices[i] + shift) * fit;
        mesh.vertices = vertices;
        mesh.RecalculateBounds();
        AssetDatabase.DeleteAsset($"{GeneratedDir}/zm_rock_115_mesh.asset");
        AssetDatabase.CreateAsset(mesh, $"{GeneratedDir}/zm_rock_115_mesh.asset");

        var root = new GameObject("zm_rock_115");
        try
        {
            var visual = new GameObject("model");
            visual.transform.SetParent(root.transform, false);
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            visual.AddComponent<MeshRenderer>().sharedMaterials = materials;
            var glow = new GameObject("glow");
            glow.transform.SetParent(root.transform, false);
            glow.transform.localPosition = mesh.bounds.center + Vector3.up * 0.05f;
            Light light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = RockBlue;
            light.range = 0.8f;
            light.intensity = 0.5f;
            light.shadows = LightShadows.None;
            AssetDatabase.DeleteAsset(RockPrefabPath);
            PrefabUtility.SaveAsPrefabAsset(root, RockPrefabPath);
            Debug.Log($"[Zombies] Blue 115 rock: {mesh.vertexCount} verts, size {mesh.bounds.size} m");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
        AssetDatabase.SaveAssets();
    }

    // Kino der Toten's power switch (the original HD remaster): GrayWolf's p7_fxanim_zmhd_power_switch_mod and its two fxanims,
    // converted by C:\Git\99_Tools\cast_to_se.py into Assets/Zombies/PowerSwitch (model, baked materials, anims)
    private const string PowerSwitchModel = "p7_fxanim_zmhd_power_switch_mod";
    private static string PowerSwitchDir => Path.GetFullPath(Path.Combine(Application.dataPath, "Zombies/PowerSwitch"));

    /// <summary>
    /// Builds zm_power_switch.prefab: the skinned switch box with its lever, an Animator with "off" (the start anim's
    /// first frame: lever up), "pull" (p7_fxanim_zm_power_switch_start_anim, the throw) and "on" (the idle anim looping:
    /// the lever down, humming), a box collider, and 3D audio for the lever and Kino's power-on.
    /// </summary>
    [MenuItem("Zombies/4. Import power switch")]
    public static void ImportPowerSwitch()
    {
        foreach (string d in new[] { GeneratedDir, TextureDir, MaterialDir })
            Directory.CreateDirectory(d);
        string file = Path.Combine(PowerSwitchDir, PowerSwitchModel + "_LOD0.semodel");
        if (!File.Exists(file))
        {
            Debug.LogError($"[Zombies] No power switch model at {file}");
            return;
        }
        SEModel model = SEModel.Load(file);
        Debug.Log($"[Zombies] Power switch: {model.Bones.Count} bones ({string.Join(", ", model.Bones.Select(b => b.Name))}), " +
                  $"{model.Meshes.Count} meshes, {model.Materials.Count} materials");
        var root = new GameObject("zm_power_switch");
        try
        {
            Transform[] bones = BuildSkeleton(model, root.transform);
            Mesh mesh = BuildModel(model, PowerSwitchDir, bones, root.transform, "zm_power_switch_mesh");
            string anims = Path.Combine(PowerSwitchDir, "anims");
            AnimatorController controller = BuildController("zm_power_switch", model, bones, root.transform, new[]
            {
                ("off", "p7_fxanim_zm_power_switch_start_anim", ClipMode.RestPose),
                ("pull", "p7_fxanim_zm_power_switch_start_anim", ClipMode.OneShot),
                ("on", "p7_fxanim_zm_power_switch_idle_anim", ClipMode.Loop),
            }, anims);
            AddAnimator(root, controller);

            var colliders = new GameObject("Colliders").transform;
            colliders.SetParent(root.transform, false);
            var box = colliders.gameObject.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size;

            AddAudio(root.transform, "Audio_Lever", "pswitch_lever.wav", false, 0.9f);
            AddAudio(root.transform, "Audio_PowerOn", "pswitch_power_on.wav", false, 1f);
            foreach (AudioSource s in root.GetComponentsInChildren<AudioSource>())
                s.maxDistance = 60f;

            AssetDatabase.DeleteAsset(PowerSwitchPrefabPath);
            PrefabUtility.SaveAsPrefabAsset(root, PowerSwitchPrefabPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Zombies] Power switch prefab saved: {PowerSwitchPrefabPath} (bounds {mesh.bounds.size} m, centre {mesh.bounds.center})");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // Each board's rest pose in its window: the last frame of the original's o_zombie_board_N_repair (where the board ends up
    // once nailed back): tag_origin position (inches, the original game axes) and tag_animate turn. Read with 99_Tools\seanim_dump.py.
    private static readonly (float x, float y, float z, float qx, float qy, float qz, float qw)[] BoardPoses =
    {
        (70.944f, 0f, 128.106f, -0.112f, 0.112f, 0.698f, 0.698f),
        (75.735f, 23.158f, 77.297f, -0.531f, 0.531f, -0.466f, -0.466f),
        (75.965f, -43.933f, 45.997f, -0.485f, 0.485f, -0.514f, -0.514f),
        (85.903f, 3.547f, 125.77f, -0.013f, 0.013f, -0.707f, -0.707f),
        (80.584f, 0f, 96.124f, 0.016f, -0.016f, -0.707f, -0.707f),
        (85.725f, 0f, 26.017f, 0.066f, -0.066f, 0.704f, 0.704f),
    };

    /// <summary>
    /// Builds Assets/Zombies/zm_window.prefab: the original's six barricade boards (p7_anim_zm_barricade_board_01..06) nailed
    /// across a window as the original has them, each a child "Board1".."Board6" (so the plugin can pull them off one by one).
    /// The prefab's front (+Z) is the outside the zombies come from, as the map editor's Window.
    /// </summary>
    [MenuItem("Zombies/3. Import barricade")]
    public static void ImportBarricade()
    {
        foreach (string dir in new[] { GeneratedDir, TextureDir, MaterialDir })
            Directory.CreateDirectory(dir);
        var root = new GameObject("zm_window");
        try
        {
            var boards = new GameObject("Boards").transform;
            boards.SetParent(root.transform, false);
            // The boards stand across the original game's Y with the zombies on its -X side; turned so that side is the prefab's +Z
            boards.localRotation = Quaternion.Euler(0f, -90f, 0f);
            for (int i = 0; i < BoardPoses.Length; i++)
            {
                string name = $"p7_anim_zm_barricade_board_0{i + 1}";
                string dir = Path.Combine(ExportRoot, "xmodels", name);
                SEModel model = SEModel.Load(Path.Combine(dir, name + "_LOD0.semodel"));
                Material[] materials = model.Materials.Select(m => BuildMaterial(m, dir)).ToArray();
                var holder = new GameObject("holder").transform;
                Transform[] bones = BuildSkeleton(model, holder);
                Mesh mesh = BuildMesh(model, bones, holder, $"zm_window_board_{i + 1}_mesh");
                mesh.boneWeights = null;
                mesh.bindposes = new Matrix4x4[0];
                float volume = FaceOutward(mesh);
                AssetDatabase.DeleteAsset($"{GeneratedDir}/{mesh.name}.asset");
                AssetDatabase.CreateAsset(mesh, $"{GeneratedDir}/{mesh.name}.asset");
                Debug.Log($"[Zombies] Board {i + 1}: signed volume {volume:F4}{(volume > 0f ? " (was inside out: flipped)" : "")}");
                // The mesh is in its bind pose; the anim puts tag_animate at the rest pose: board = rest * bind^-1
                Transform animate = bones.FirstOrDefault(b => b.name == "tag_animate") ?? bones[0];
                Vector3 bindPos = animate.position;
                Quaternion bindRot = animate.rotation;
                Debug.Log($"[Zombies] Board {i + 1}: bones {string.Join(", ", model.Bones.Select(b => b.Name))}, " +
                          $"tag_animate bind {bindPos} {bindRot.eulerAngles}, mesh bounds {mesh.bounds.size}");
                Object.DestroyImmediate(holder.gameObject);

                var p = BoardPoses[i];
                Vector3 at = ToUnity(new SEVec3 { X = p.x, Y = p.y, Z = p.z });
                Quaternion turn = ToUnity(new SEQuat { X = p.qx, Y = p.qy, Z = p.qz, W = p.qw });
                Quaternion rotation = turn * Quaternion.Inverse(bindRot);
                var board = new GameObject($"Board{i + 1}");
                board.transform.SetParent(boards, false);
                board.transform.localRotation = rotation;
                board.transform.localPosition = at - rotation * bindPos;
                board.AddComponent<MeshFilter>().sharedMesh = mesh;
                board.AddComponent<MeshRenderer>().sharedMaterials = model.Meshes
                    .Select(m => m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 ? materials[m.MaterialIndices[0]] : materials[0])
                    .ToArray();
            }
            AssetDatabase.DeleteAsset(BarricadePrefabPath);
            PrefabUtility.SaveAsPrefabAsset(root, BarricadePrefabPath);
            AssetDatabase.SaveAssets();
            Bounds b = new Bounds(root.transform.position, Vector3.zero);
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
                b.Encapsulate(r.bounds);
            Debug.Log($"[Zombies] Barricade prefab saved: {BarricadePrefabPath} (bounds {b.size} m)");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    /// <summary>
    /// A closed mesh (a plank) turned to face outward by its shape, not its exported normals (one board's export had
    /// them inverted and drew black): Unity's front faces wind clockwise seen from outside, so the signed volume is
    /// negative; positive means inside out, and every triangle is flipped. Normals are then worked out from the faces.
    /// Returns the signed volume as found.
    /// </summary>
    private static float FaceOutward(Mesh mesh)
    {
        Vector3[] v = mesh.vertices;
        float volume = 0f;
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            int[] t = mesh.GetTriangles(sub);
            for (int f = 0; f < t.Length; f += 3)
                volume += Vector3.Dot(v[t[f]], Vector3.Cross(v[t[f + 1]], v[t[f + 2]])) / 6f;
        }
        if (volume > 0f)
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                int[] t = mesh.GetTriangles(sub);
                for (int f = 0; f < t.Length; f += 3)
                    (t[f + 1], t[f + 2]) = (t[f + 2], t[f + 1]);
                mesh.SetTriangles(t, sub);
            }
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        return volume;
    }

    // ---------- Materials ----------

    // The perk machines' approved look (Tools\CONTEXT.md): the original's colour maps a little more saturated, a touch more
    // contrast, the spec maps weaker
    private const float Saturation = 1.35f, Contrast = 1.08f, SpecStrength = 0.65f;

    private static Material BuildMaterial(SEMaterial se, string modelDir)
    {
        string path = $"{MaterialDir}/{se.Name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Standard (Specular setup)")) { name = se.Name };
            AssetDatabase.CreateAsset(mat, path);
        }

        string imageDir = Path.Combine(modelDir, "_images", se.Name);
        string diffuse = SourceImage(modelDir, se.DiffuseMap) ?? FindBySuffix(imageDir, "_c.png");
        string normal = SourceImage(modelDir, se.NormalMap) ?? FindBySuffix(imageDir, "_n.png");
        string spec = SourceImage(modelDir, se.SpecularMap) ?? FindBySuffix(imageDir, "_s.png");
        string gloss = FindBySuffix(imageDir, "_g.png");

        Texture2D albedo = null;
        if (diffuse != null)
        {
            albedo = Vibrant(ImportTexture(diffuse, TextureImporterType.Default, true));
            mat.SetTexture("_MainTex", albedo);
            mat.color = Color.white;
        }
        else
        {
            mat.SetTexture("_MainTex", null);
            mat.color = Color.gray;
        }
        if (normal != null)
        {
            mat.SetTexture("_BumpMap", ImportTexture(normal, TextureImporterType.NormalMap, false));
            mat.EnableKeyword("_NORMALMAP");
        }
        else
        {
            mat.SetTexture("_BumpMap", null);
            mat.DisableKeyword("_NORMALMAP");
        }
        if (spec != null)
        {
            mat.SetTexture("_SpecGlossMap", BuildSpecGloss(spec, gloss));
            mat.SetFloat("_GlossMapScale", 1f);
            mat.EnableKeyword("_SPECGLOSSMAP");
        }
        else
        {
            mat.SetTexture("_SpecGlossMap", null);
            mat.DisableKeyword("_SPECGLOSSMAP");
            mat.SetColor("_SpecColor", new Color(0.2f, 0.2f, 0.2f));
            mat.SetFloat("_Glossiness", 0.35f);
        }

        mat.DisableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", Color.black);
        // The question marks: the original's are a soft glowing "?" (its shape in the alpha of qmark_bg_c; the trim's four "?"s
        // are white on black). Drawn see-through ("Fade", which the game renders) and glowing warm yellow
        if (se.Name.Contains("qmark"))
        {
            // Only the soft glowing "?": the trim card (four "?"s in one picture) showed bits of other "?"s in each slot
            Texture2D glow = se.Name.Contains("trim") ? null : QuestionGlow(imageDir, false);
            if (glow == null)
            {
                mat.SetColor("_EmissionColor", Color.black);
                mat.DisableKeyword("_EMISSION");
                mat.SetFloat("_Mode", 2f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                mat.color = new Color(0f, 0f, 0f, 0f);
                EditorUtility.SetDirty(mat);
                return mat;
            }
            if (glow != null)
            {
                mat.SetTexture("_MainTex", glow);
                mat.color = new Color(1f, 0.85f, 0.45f, 1f);
                mat.SetTexture("_EmissionMap", glow);
                mat.SetColor("_EmissionColor", QuestionGlowColour);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                mat.EnableKeyword("_EMISSION");
                mat.SetTexture("_BumpMap", null);
                mat.DisableKeyword("_NORMALMAP");
                mat.SetFloat("_Mode", 2f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 5;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }
        // Rope, the bear's fur and the hay (a decal) have see-through gaps
        if ((se.Name.Contains("rope") || se.Name.Contains("fur") || se.Name.Contains("hay")) && albedo != null)
        {
            mat.SetFloat("_Mode", 1f);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Warm yellow (the user, 2026-11-01)
    private static readonly Color QuestionGlowColour = new Color(1f, 0.75f, 0.25f) * 1.5f;

    /// <summary>
    /// The glow picture: white, its alpha the "?" shape (qmark_bg_c's alpha; the trim's from its brightness), made
    /// stronger so the whole "?" reads, not just its brightest blobs.
    /// </summary>
    private static Texture2D QuestionGlow(string imageDir, bool trim)
    {
        string source = FindBySuffix(imageDir, trim ? "qmark_trim_c.png" : "qmark_bg_c.png");
        if (source == null)
            return null;
        string assetPath = $"{TextureDir}/{Path.GetFileNameWithoutExtension(source)}_glow.png";
        if (Stale(assetPath, source))
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            tex.LoadImage(File.ReadAllBytes(source));
            Color[] pixels = tex.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                float a = trim ? (c.r + c.g + c.b) / 3f * c.a : c.a;
                pixels[i] = new Color(1f, 1f, 1f, Mathf.Clamp01(a * 1.6f));
            }
            tex.SetPixels(pixels);
            File.WriteAllBytes(assetPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(assetPath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            importer.alphaIsTransparency = true;
            // Clamped: the "?" cards' UVs run past the picture, and repeating it put bits of other "?"s in the slots
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    private static string SourceImage(string modelDir, string relative)
    {
        if (string.IsNullOrEmpty(relative))
            return null;
        string rel = relative.Replace(@"\\", @"\");
        if (Path.GetFileName(rel).StartsWith("$"))
            return null;
        string full = Path.Combine(modelDir, rel);
        return File.Exists(full) ? full : null;
    }

    private static string FindBySuffix(string dir, string suffix) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).FirstOrDefault(f => !Path.GetFileName(f).StartsWith("$")) : null;

    private static Texture2D ImportTexture(string source, TextureImporterType type, bool srgb)
    {
        string assetPath = $"{TextureDir}/{Path.GetFileName(source)}";
        if (!File.Exists(assetPath) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(assetPath))
        {
            File.Copy(source, assetPath, true);
            AssetDatabase.ImportAsset(assetPath);
        }
        var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
        if (importer.textureType != type || importer.sRGBTexture != srgb)
        {
            importer.textureType = type;
            importer.sRGBTexture = srgb;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    // Derived maps are rebuilt when this script is newer than them, or their source is
    private static string VersionSource => Path.Combine(Application.dataPath, "Zombies/Editor/BoxImporter.cs");

    private static bool Stale(string derived, string source) =>
        !File.Exists(derived) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(derived)
        || File.GetLastWriteTimeUtc(VersionSource) > File.GetLastWriteTimeUtc(derived);

    private static Texture2D Vibrant(Texture2D source)
    {
        string sourcePath = AssetDatabase.GetAssetPath(source);
        string assetPath = $"{TextureDir}/{Path.GetFileNameWithoutExtension(sourcePath)}_v.png";
        if (Stale(assetPath, sourcePath))
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            tex.LoadImage(File.ReadAllBytes(sourcePath));
            Color[] pixels = tex.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                Color.RGBToHSV(c, out float h, out float s, out float v);
                Color o = Color.HSVToRGB(h, Mathf.Clamp01(s * Saturation), v);
                o.r = Mathf.Clamp01((o.r - 0.5f) * Contrast + 0.5f);
                o.g = Mathf.Clamp01((o.g - 0.5f) * Contrast + 0.5f);
                o.b = Mathf.Clamp01((o.b - 0.5f) * Contrast + 0.5f);
                o.a = c.a;
                pixels[i] = o;
            }
            tex.SetPixels(pixels);
            File.WriteAllBytes(assetPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(assetPath);
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    /// <summary>Standard (Specular setup) reads smoothness from the specular map's alpha, so pack _g into _s.a.</summary>
    private static Texture2D BuildSpecGloss(string specSource, string glossSource)
    {
        string assetPath = $"{TextureDir}/{Path.GetFileNameWithoutExtension(specSource)}_sg.png";
        if (!File.Exists(assetPath))
        {
            var spec = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            spec.LoadImage(File.ReadAllBytes(specSource));
            Texture2D gloss = null;
            if (glossSource != null)
            {
                gloss = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                gloss.LoadImage(File.ReadAllBytes(glossSource));
            }
            int w = spec.width, h = spec.height;
            Color[] pixels = spec.GetPixels();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    pixels[i].r *= SpecStrength;
                    pixels[i].g *= SpecStrength;
                    pixels[i].b *= SpecStrength;
                    pixels[i].a = gloss != null ? gloss.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r : 0.35f;
                }
            }
            spec.SetPixels(pixels);
            File.WriteAllBytes(assetPath, spec.EncodeToPNG());
            Object.DestroyImmediate(spec);
            if (gloss != null)
                Object.DestroyImmediate(gloss);
            AssetDatabase.ImportAsset(assetPath);
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    // ---------- Animation ----------

    private static AnimatorController BuildController(string name, SEModel model, Transform[] animated, Transform root,
        (string state, string anim, ClipMode mode)[] states, string animDir = null)
    {
        animDir = animDir ?? AnimDir;
        string controllerPath = $"{Root}/{name}.controller";
        AssetDatabase.DeleteAsset(controllerPath);
        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
        AnimatorStateMachine sm = controller.layers[0].stateMachine;
        foreach (var (state, animName, mode) in states)
        {
            SEAnim anim = animName != null ? SEAnim.Load(Path.Combine(animDir, animName + ".seanim")) : null;
            AnimatorState s = sm.AddState(state);
            s.motion = BuildClip($"{name}_{state}", anim, mode, model, animated, root);
            if (sm.defaultState == null)
                sm.defaultState = s;
        }
        return controller;
    }

    /// <summary>
    /// Every animated bone gets full position + rotation curves so switching states always restores the pose. A
    /// RestPose clip of an anim is its first frame (the pivots start where the anim does).
    /// </summary>
    private static AnimationClip BuildClip(string name, SEAnim anim, ClipMode mode, SEModel model, Transform[] animated, Transform root)
    {
        float fps = anim != null && anim.FrameRate > 0f ? anim.FrameRate : 30f;
        float length = anim != null && (mode == ClipMode.OneShot || mode == ClipMode.Loop) ? Mathf.Max(1, anim.FrameCount - 1) / fps : 1f;
        var clip = new AnimationClip { name = name, frameRate = fps, legacy = false };

        foreach (Transform bone in animated)
        {
            string path = AnimationUtility.CalculateTransformPath(bone, root);
            SEBone rest = model.Bones.First(b => b.Name == bone.name);
            SEAnimBone track = anim?.Bones.FirstOrDefault(b => b.Name == bone.name);

            // SEAnim "delta" keys are offsets from the rest pose
            bool delta = anim != null && anim.Type != SEAnimType.Absolute;
            Vector3 restPos = ToUnity(rest.LocalPosition);
            Quaternion restRot = ToUnity(rest.LocalRotation);

            var posKeys = track != null && track.Positions.Count > 0
                ? track.Positions.Select(k => (t: k.Frame / fps, v: delta ? restPos + ToUnity(k.Value) : ToUnity(k.Value))).ToList()
                : new List<(float t, Vector3 v)> { (0f, restPos) };
            var rotKeys = track != null && track.Rotations.Count > 0
                ? track.Rotations.Select(k => (t: k.Frame / fps, v: delta ? restRot * ToUnity(k.Value) : ToUnity(k.Value))).ToList()
                : new List<(float t, Quaternion v)> { (0f, restRot) };

            if (mode == ClipMode.HoldLastFrame)
            {
                posKeys = new List<(float t, Vector3 v)> { (0f, posKeys[posKeys.Count - 1].v) };
                rotKeys = new List<(float t, Quaternion v)> { (0f, rotKeys[rotKeys.Count - 1].v) };
            }
            else if (mode == ClipMode.RestPose)
            {
                posKeys = new List<(float t, Vector3 v)> { (0f, posKeys[0].v) };
                rotKeys = new List<(float t, Quaternion v)> { (0f, rotKeys[0].v) };
            }

            if (posKeys[posKeys.Count - 1].t < length) posKeys.Add((length, posKeys[posKeys.Count - 1].v));
            if (rotKeys[rotKeys.Count - 1].t < length) rotKeys.Add((length, rotKeys[rotKeys.Count - 1].v));

            SetCurve(clip, path, "m_LocalPosition.x", posKeys.Select(k => (k.t, k.v.x)));
            SetCurve(clip, path, "m_LocalPosition.y", posKeys.Select(k => (k.t, k.v.y)));
            SetCurve(clip, path, "m_LocalPosition.z", posKeys.Select(k => (k.t, k.v.z)));
            SetCurve(clip, path, "m_LocalRotation.x", rotKeys.Select(k => (k.t, k.v.x)));
            SetCurve(clip, path, "m_LocalRotation.y", rotKeys.Select(k => (k.t, k.v.y)));
            SetCurve(clip, path, "m_LocalRotation.z", rotKeys.Select(k => (k.t, k.v.z)));
            SetCurve(clip, path, "m_LocalRotation.w", rotKeys.Select(k => (k.t, k.v.w)));
        }

        clip.EnsureQuaternionContinuity();
        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = mode != ClipMode.OneShot;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        string clipPath = $"{GeneratedDir}/{name}.anim";
        AssetDatabase.DeleteAsset(clipPath);
        AssetDatabase.CreateAsset(clip, clipPath);
        Debug.Log($"[Zombies] Clip {name}: {clip.length:F2}s @ {fps}fps ({mode})");
        return clip;
    }

    private static void SetCurve(AnimationClip clip, string path, string property, IEnumerable<(float t, float v)> keys)
    {
        var curve = new AnimationCurve(keys.Select(k => new Keyframe(k.t, k.v)).ToArray());
        for (int i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }
        clip.SetCurve(path, typeof(Transform), property, curve);
    }

    // ---------- Audio ----------

    private static void AddAudio(Transform parent, string name, string file, bool loop, float volume)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{AudioDir}/{file}");
        if (clip == null)
        {
            Debug.LogError($"[Zombies] Missing audio clip {file} for {name}");
            return;
        }
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, 0.6f, 0f);
        var source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = loop;
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 1f;
        source.maxDistance = 20f;
        source.dopplerLevel = 0f;
        source.volume = volume;
    }
}
