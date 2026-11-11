using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KennerZombies
{
    /// <summary>
    /// A zombies map's projected decals (floor and wall dirt, stains, puddles), put back at raid start. They're EFT's
    /// StaticDeferredDecal objects drawn by a StaticDeferredDecalRenderer; the map SDK has neither script, so the map's
    /// bundle carries only the objects and their materials (held by the map's KZ_DecalMaterials), and the map's Unity tool
    /// (Kennerz > Keep Projected Decals) writes the rest to maps\&lt;location&gt;_decals.json. Here the renderer is made (with
    /// the map's settings) unless one is already there, then each decal object gets its script back with its material,
    /// clip and tint. Only on zombies maps; it all goes with the raid's scenes (the renderer at teardown).
    /// </summary>
    internal static class ZombiesDecals
    {
        private const string HolderName = "KZ_DecalMaterials";
        private static GameObject _renderer;

        public static void Restore(string location)
        {
            try
            {
                string file = MapEditor.MapsDirs().Select(d => Path.Combine(d, location + "_decals.json")).FirstOrDefault(File.Exists);
                if (file == null)
                    return;
                JObject data = JObject.Parse(File.ReadAllText(file));
                Material[] materials = Materials();
                if (materials == null)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies decals: no {HolderName} in the map, decals left off (rebuild the map)");
                    return;
                }
                MakeRenderer(data["Renderer"] as JObject);
                int placed = 0, missing = 0;
                var scenes = new Dictionary<string, GameObject[]>();
                foreach (JObject d in data["Decals"].Cast<JObject>())
                {
                    GameObject go = Find(scenes, (string)d["Scene"], (string)d["Index"], (string)d["Path"]);
                    int m = (int)d["Mat"];
                    if (go == null || m < 0 || m >= materials.Length || materials[m] == null || go.GetComponent<StaticDeferredDecal>() != null)
                    {
                        missing++;
                        continue;
                    }
                    // Added with no material, it switches itself off in Awake; set up, then on (that registers it)
                    StaticDeferredDecal decal = go.AddComponent<StaticDeferredDecal>();
                    decal._material = materials[m];
                    decal._decalQueue = (int)d["Queue"];
                    decal._normalClip = (float)d["NClip"];
                    decal._normalAlphaFadeoff = (float)d["NFade"];
                    decal._edgeClip = (float)d["EClip"];
                    decal._edgeAlphaFadeoff = (float)d["EFade"];
                    JArray tint = (JArray)d["Tint"];
                    decal._tint = new Color((float)tint[0], (float)tint[1], (float)tint[2], (float)tint[3]);
                    decal.LowerLeftPixel = new Vector2((float)d["LL"][0], (float)d["LL"][1]);
                    decal.UpperRightPixel = new Vector2((float)d["UR"][0], (float)d["UR"][1]);
                    decal.Refresh();
                    if (decal.Tex == null)
                    {
                        UnityEngine.Object.Destroy(decal);
                        missing++;
                        continue;
                    }
                    decal.enabled = true;
                    placed++;
                }
                ZombiesPlugin.Log.LogInfo($"Zombies decals: {placed} put back{(missing > 0 ? $", {missing} not found" : "")}");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies decals failed: {e}");
            }
        }

        public static void Clear()
        {
            if (_renderer != null)
                UnityEngine.Object.Destroy(_renderer);
            _renderer = null;
        }

        // The materials, from the map's holder (the custom map loader has already given them the game's shaders)
        private static Material[] Materials()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded)
                    continue;
                GameObject holder = s.GetRootGameObjects().FirstOrDefault(g => g.name == HolderName);
                if (holder != null)
                    return holder.GetComponent<MeshRenderer>()?.sharedMaterials;
            }
            return null;
        }

        private static void MakeRenderer(JObject settings)
        {
            if (StaticDeferredDecalRenderer.Instance != null)
                return;
            // Set up while off, so its Awake runs with the map's settings
            _renderer = new GameObject("KZ_DecalRenderer");
            _renderer.SetActive(false);
            StaticDeferredDecalRenderer r = _renderer.AddComponent<StaticDeferredDecalRenderer>();
            if (settings != null)
                foreach (JProperty p in settings.Properties())
                {
                    FieldInfo f = typeof(StaticDeferredDecalRenderer).GetField(p.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f == null)
                        continue;
                    if (f.FieldType == typeof(bool))
                        f.SetValue(r, (int)p.Value != 0);
                    else if (f.FieldType == typeof(int))
                        f.SetValue(r, (int)p.Value);
                    else if (f.FieldType == typeof(float))
                        f.SetValue(r, (float)p.Value);
                }
            _renderer.SetActive(true);
        }

        // By its place in the hierarchy, checked against its name (the path when that place has changed)
        private static GameObject Find(Dictionary<string, GameObject[]> scenes, string scene, string index, string path)
        {
            if (!scenes.TryGetValue(scene, out GameObject[] roots))
            {
                Scene s = SceneManager.GetSceneByName(scene);
                scenes[scene] = roots = s.IsValid() && s.isLoaded ? s.GetRootGameObjects() : new GameObject[0];
            }
            string name = path.Substring(path.LastIndexOf('/') + 1);
            Transform t = null;
            string[] steps = index.Split('/');
            for (int i = 0; i < steps.Length; i++)
            {
                int n = int.Parse(steps[i]);
                if (i == 0)
                    t = n < roots.Length ? roots[n].transform : null;
                else
                    t = t != null && n < t.childCount ? t.GetChild(n) : null;
                if (t == null)
                    break;
            }
            if (t != null && t.name == name)
                return t.gameObject;
            string[] names = path.Split('/');
            GameObject root = roots.FirstOrDefault(g => g.name == names[0]);
            Transform found = root != null && names.Length > 1 ? root.transform.Find(string.Join("/", names.Skip(1))) : root?.transform;
            return found != null ? found.gameObject : null;
        }
    }
}
