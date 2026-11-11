using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZombiesMonkeyHands.ModelFormats;

namespace ZombiesMonkeyHands.Editor
{
    /// <summary>
    /// Pictures of the baked monkey arm clips, so the the original retarget can be checked without the game: the M67's hands rig
    /// posed by each clip (arms drawn as sticks: green right, yellow left, palms as balls), the monkey as the game shows
    /// it (zm_monkey on the weapon bone at the original's grip, ZombiesMonkeyHands.ClassicGrip), and the original's own wrists (red balls)
    /// where the retarget aimed the hands. Seen from the rig's camera (Camera_animated, looking down the rig's +Z).
    /// Writes logs\monkeyhands_&lt;clip&gt;_&lt;n&gt;.png.
    /// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod ZombiesMonkeyHands.Editor.MonkeyHandsPreview.BakeAndRenderBatch
    /// </summary>
    public static class MonkeyHandsPreview
    {
        // Same as the client's ZombiesMonkeyHands
        private static readonly Quaternion ClassicGrip = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
        public static float MonkeySize = 0.49f;     // the client's the original arms size: the original's view model is ~0.49 m tall with its wires
        private const float PrefabHeight = 1.24f;   // zm_monkey's own height (export units)
        public static float VerticalFov = 55f;

        public static void BakeAndRenderBatch()
        {
            MonkeyHandsImporter.Import();
            Render();
        }

        [MenuItem("Zombies/10. Preview monkey hands")]
        public static void Render()
        {
            var made = new List<Object>();
            try
            {
                var lightGo = new GameObject("light");
                made.Add(lightGo);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightGo.transform.rotation = Quaternion.Euler(35f, 20f, 0f);
                RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.45f);

                var rig = new Dictionary<string, Transform>();
                GameObject root = RgRig.Build(RgPaths.BaseModel, rig);
                made.Add(root);

                // The retarget's placement (for the original's own wrists)
                SrcModel gun = RgClassic.LoadModel();
                SrcModel bind = SrcModel.Load(Path.Combine(RgPaths.ModelDir, "am_t5_thundergun.xmodel_bin"));
                Transform weapon = rig["weapon"];
                var jgun = new GameObject(gun.Bones[0].Name).transform;
                jgun.SetParent(weapon, false);
                Quaternion jgunLocal = Quaternion.Inverse(weapon.rotation) * RgSpace.Rot(gun.Bones[0].Rest);
                jgun.localRotation = jgunLocal;
                var gunBones = new Dictionary<string, Transform> { [gun.Bones[0].Name] = jgun };
                SrcAnim idle = RgClassic.LoadAnim("vm_monkey_bomb_idle", gun, bind, out _);
                MonkeyHandsImporter.Configure();
                var retarget = new RgRetarget(root.transform, rig, gun, gunBones, jgunLocal, bind, idle);
                Vector3 offset = retarget.Offset;
                // The original's own view model (rest pose) on the gun bone, see-through blue: where the original holds the monkey
                {
                    Quaternion inv = Quaternion.Inverse(RgSpace.Rot(gun.Bones[0].Rest));
                    Vector3 o = RgSpace.Pos(gun.Bones[0].Rest.Pos);
                    var verts = gun.Vertices.Select(v => inv * (RgSpace.Pos(v.Pos) - o)).ToArray();
                    var tris = new List<int>();
                    foreach (SrcFace fc in gun.Faces)
                        tris.AddRange(new[] { fc.Corners[0].Vertex, fc.Corners[2].Vertex, fc.Corners[1].Vertex });
                    var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, vertices = verts, triangles = tris.ToArray() };
                    mesh.RecalculateNormals();
                    made.Add(mesh);
                    var vm = new GameObject("source_view_model");
                    vm.transform.SetParent(jgun, false);
                    vm.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var blue = new Material(Shader.Find("Standard")) { color = new Color(0.2f, 0.45f, 1f) };
                    made.Add(blue);
                    vm.AddComponent<MeshRenderer>().sharedMaterial = blue;
                    if (System.Environment.GetCommandLineArgs().Contains("-nobo3model"))
                        vm.SetActive(false);
                    Bounds b = mesh.bounds;
                    Debug.Log($"[Zombies] Monkey hands preview: view model bounds {b.size * 100f} cm, centre {b.center * 100f} cm (gun bone space)");
                }
                {
                    string Up(string bone) { int i = bind.BoneIndex(bone); return i < 0 ? "(none)" : bind.Bones[i].Parent < 0 ? "(root)" : bind.Bones[bind.Bones[i].Parent].Name; }
                    Debug.Log("[Zombies] Monkey hands preview: source rig parents: " + string.Join(", ",
                        new[] { "j_shoulder_le", "j_shoulder_ri", "j_clavicle_le", "tag_torso", "tag_view", "tag_camera", "tag_weapon_right", "tag_weapon_left" }.Select(b => $"{b} <- {Up(b)}")));
                    Vector3 eyeRest = root.transform.InverseTransformPoint(rig["Camera_animated"].position);
                    string W(string part) => idle.PartIndex(part) is int i && i >= 0 ? (RgSpace.Pos(idle.Frames[0][i].Pos) + offset - eyeRest).ToString("F3") : "-";
                    Debug.Log($"[Zombies] Monkey hands preview: idle, from the eye (right, up, forward m): right wrist {W("j_wrist_ri")}, left wrist {W("j_wrist_le")}, " +
                              $"right shoulder {W("j_shoulder_ri")}, left shoulder {W("j_shoulder_le")}, monkey {W("tag_weapon")}; EFT shoulders R " +
                              $"{(root.transform.InverseTransformPoint(rig["Base HumanRUpperarm"].position) - eyeRest):F3} L {(root.transform.InverseTransformPoint(rig["Base HumanLUpperarm"].position) - eyeRest):F3}, " +
                              $"palms R {(root.transform.InverseTransformPoint(rig["Base HumanRPalm"].position) - eyeRest):F3} L {(root.transform.InverseTransformPoint(rig["Base HumanLPalm"].position) - eyeRest):F3}");
                }

                // The monkey as the game shows it with the original's arms on
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BoxImporter.MonkeyPrefabPath);
                if (prefab != null)
                {
                    GameObject m = Object.Instantiate(prefab, weapon, false);
                    m.transform.localPosition = Vector3.zero;
                    m.transform.localRotation = ClassicGrip;
                    m.transform.localScale = Vector3.one * (MonkeySize / PrefabHeight) / Mathf.Max(1e-4f, weapon.lossyScale.y);
                }

                var camGo = new GameObject("cam");
                made.Add(camGo);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.16f, 0.17f, 0.2f);
                cam.fieldOfView = VerticalFov;
                cam.nearClipPlane = 0.01f;
                var rt = new RenderTexture(960, 540, 24);
                cam.targetTexture = rt;

                Material Mat(Color c)
                {
                    var mat = new Material(Shader.Find("Standard")) { color = c };
                    made.Add(mat);
                    return mat;
                }
                Material right = Mat(new Color(0.2f, 0.85f, 0.3f)), left = Mat(new Color(0.95f, 0.85f, 0.2f)), bo = Mat(new Color(0.95f, 0.15f, 0.1f));
                var sticks = new List<GameObject>();

                string[] clips = { "zm_monkey_vm_raise", "zm_monkey_vm_wind", "zm_monkey_vm_throw", "zm_monkey_vm_idle" };
                var anims = new Dictionary<string, SrcAnim> { ["vm_monkey_bomb_idle"] = idle };
                string logs = RgPaths.LogsDir;
                Directory.CreateDirectory(logs);
                foreach (string name in clips)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>($"{RgPaths.Clips}/{name}.anim");
                    if (clip == null)
                    {
                        Debug.LogWarning($"[Zombies] Monkey hands preview: no clip {name}");
                        continue;
                    }
                    var (animName, first, last) = MonkeyHandsImporter.Source(name);
                    if (!anims.TryGetValue(animName, out SrcAnim anim))
                    {
                        anims[animName] = anim = RgClassic.LoadAnim(animName, gun, bind, out _);
                        var sb = new System.Text.StringBuilder($"[Zombies] Monkey hands preview: {animName} cm from tags to wrists (wR-tR, wL-tL, wL-tR, wR-tL):");
                        float D(int fr, string a, string b) => Vector3.Distance(RgSpace.Pos(anim.Frames[fr][anim.PartIndex(a)].Pos), RgSpace.Pos(anim.Frames[fr][anim.PartIndex(b)].Pos)) * 100f;
                        for (int fr = 0; fr < anim.Frames.Count; fr += 5)
                            sb.Append($" f{fr} {D(fr, "j_wrist_ri", "tag_weapon_right"):0}/{D(fr, "j_wrist_le", "tag_weapon_left"):0}/{D(fr, "j_wrist_le", "tag_weapon_right"):0}/{D(fr, "j_wrist_ri", "tag_weapon_left"):0}");
                        Debug.Log(sb.ToString());
                    }
                    int shots = name.EndsWith("idle") ? 1 : name.EndsWith("throw") ? 9 : 5;
                    for (int k = 0; k < shots; k++)
                    {
                        float u = shots == 1 ? 0f : k / (float)(shots - 1);
                        clip.SampleAnimation(root, u * clip.length);
                        foreach (GameObject s in sticks)
                            Object.DestroyImmediate(s);
                        sticks.Clear();
                        foreach (string e in new[] { "L", "R" })
                        {
                            Material mat = e == "R" ? right : left;
                            string P(string part) => $"Base Human{e}{part}";
                            Stick(sticks, rig[P("Upperarm")].position, rig[P("Forearm1")].position, 0.035f, mat);
                            Stick(sticks, rig[P("Forearm1")].position, rig[P("Palm")].position, 0.03f, mat);
                            foreach (string d in new[] { "Digit11", "Digit21", "Digit31", "Digit41", "Digit51" })
                                Stick(sticks, rig[P("Palm")].position, rig[P(d)].position, 0.012f, mat);
                            Ball(sticks, rig[P("Palm")].position, 0.03f, mat);
                        }
                        // The original's own wrists for this frame, where the retarget aimed (before its clamps)
                        int frame = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(first, last, u)), 0, anim.Frames.Count - 1);
                        Vector3 BoAt(string part)
                        {
                            int i = anim.PartIndex(part);
                            return i >= 0 ? root.transform.TransformPoint(RgSpace.Pos(anim.Frames[frame][i].Pos) + offset) : Vector3.zero;
                        }
                        foreach (string sd in new[] { "le", "ri" })
                        {
                            Ball(sticks, BoAt($"j_wrist_{sd}"), 0.02f, bo);
                            Stick(sticks, BoAt($"j_shoulder_{sd}"), BoAt($"j_elbow_{sd}"), 0.012f, bo);
                            Stick(sticks, BoAt($"j_elbow_{sd}"), BoAt($"j_wrist_{sd}"), 0.012f, bo);
                            Stick(sticks, BoAt($"j_wrist_{sd}"), BoAt($"j_mid_{sd}_1"), 0.008f, bo);
                        }
                        Transform eye = rig["Camera_animated"];
                        cam.transform.position = eye.position;
                        cam.transform.rotation = root.transform.rotation;
                        string file = Path.Combine(logs, $"monkeyhands_{name.Substring("zm_monkey_vm_".Length)}_{k}.png");
                        Shoot(cam, rt, file);
                    }
                }
                foreach (GameObject s in sticks)
                    Object.DestroyImmediate(s);
                Debug.Log($"[Zombies] Monkey hands preview: pictures in {logs} (offset {offset}, shoulders moved {retarget.ShoulderShifts})");
                rt.Release();
            }
            finally
            {
                foreach (Object o in made)
                    if (o != null)
                        Object.DestroyImmediate(o);
            }
        }

        private static void Stick(List<GameObject> list, Vector3 a, Vector3 b, float width, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position = (a + b) * 0.5f;
            Vector3 d = b - a;
            if (d.sqrMagnitude > 1e-8f)
                go.transform.rotation = Quaternion.FromToRotation(Vector3.up, d);
            go.transform.localScale = new Vector3(width, d.magnitude * 0.5f, width);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            list.Add(go);
        }

        private static void Ball(List<GameObject> list, Vector3 p, float size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position = p;
            go.transform.localScale = Vector3.one * size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            list.Add(go);
        }

        private static void Shoot(Camera cam, RenderTexture rt, string file)
        {
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(file, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
