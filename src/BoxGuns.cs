using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The guns the mystery box shows: the builds logged with the F12 gun logger (box_builds.json, written by
    /// data\make_guns.py), made into display models the way the hideout's weapon stands make theirs: the item built
    /// part by part as logged, its bundles loaded, made by the game's factory, purely visual (colliders off).
    /// </summary>
    internal static class BoxGuns
    {
        public class Gun
        {
            public string Id, Name, Role;
            public JObject Base, Pap;
            /// <summary>A gun without a logged build (another mod's): made from its template's default preset.</summary>
            public string PresetTemplate;
        }

        /// <summary>A built display model, with how it lies in the box: turned so its length runs along the box.</summary>
        public class Model
        {
            public Gun Gun;
            public GameObject Go;
            public Quaternion Align;   // the model's turn relative to the box
            public Vector3 Centre;     // its middle, from its root, with that turn applied (box unturned)
        }

        private static List<Gun> _guns;

        /// <summary>The box's guns (role box or box_wonder), read once.</summary>
        public static List<Gun> BoxGunList()
        {
            if (_guns != null)
                return _guns;
            _guns = new List<Gun>();
            string path = Path.Combine(ZombiesPlugin.Dir, "box_builds.json");
            try
            {
                foreach (JObject o in JArray.Parse(File.ReadAllText(path)).OfType<JObject>())
                {
                    string role = (string)o["role"];
                    if (role != "box" && role != "box_wonder")
                        continue;
                    _guns.Add(new Gun { Id = (string)o["id"], Name = (string)o["name"], Role = role, Base = o["base"] as JObject, Pap = o["pap"] as JObject });
                }
                ZombiesPlugin.Log.LogInfo($"Mystery box: {_guns.Count} guns from box_builds.json");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Couldn't read {path}: {e.Message}");
            }
            // The original's Cymbal Monkey: a box offer like a gun, taking it fills the tactical slots (ZombiesMonkey)
            _guns.Add(new Gun { Id = MonkeyId, Name = "Cymbal Monkey", Role = "box_wonder", PresetTemplate = null,
                Base = new JObject { ["tpl"] = ZombiesMonkey.Template } });
            // Guns other mods registered through ZombiesApi, and (F12, off by default) every other mod's gun found
            foreach (Gun gun in ZombiesApi.Registered)
                if (_guns.All(g => g.PresetTemplate != gun.PresetTemplate && (string)g.Base?["tpl"] != gun.PresetTemplate))
                    _guns.Add(gun);
            if (ZombiesPlugin.OtherModsGuns.Value)
            {
                int found = 0;
                foreach (Gun gun in ZombiesApi.DetectModdedWeapons())
                    if (_guns.All(g => g.PresetTemplate != gun.PresetTemplate && (string)g.Base?["tpl"] != gun.PresetTemplate))
                    {
                        _guns.Add(gun);
                        found++;
                    }
                ZombiesPlugin.Log.LogInfo($"Mystery box: {found} guns from other mods added");
            }
            return _guns;
        }

        /// <summary>Any gun in box_builds.json by its id (box, wall or starter), for wall buys; null if there's none.</summary>
        public static Gun FindGun(string id)
        {
            try
            {
                string path = Path.Combine(ZombiesPlugin.Dir, "box_builds.json");
                JObject o = JArray.Parse(File.ReadAllText(path)).OfType<JObject>().FirstOrDefault(g => (string)g["id"] == id);
                return o == null ? null
                    : new Gun { Id = id, Name = (string)o["name"], Role = (string)o["role"], Base = o["base"] as JObject, Pap = o["pap"] as JObject };
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Couldn't read box_builds.json: {e.Message}");
                return null;
            }
        }

        /// <summary>The gun's weapon template (its logged build's, or its preset's).</summary>
        public static string Template(Gun gun) => gun.PresetTemplate ?? (string)gun.Base?["tpl"];

        /// <summary>Forgets the list, so the next box set out reads it again (F12 changes, newly registered guns).</summary>
        public static void Reload() => _guns = null;

        /// <summary>The gun's item: its logged build, or its default preset for another mod's gun.</summary>
        public static Item BuildItem(Gun gun) =>
            gun.PresetTemplate != null ? Singleton<ItemFactory>.Instance?.GetPresetItem(gun.PresetTemplate) : BuildItem(gun.Base);

        /// <summary>Builds the gun's item as logged: the weapon, then every part in its slot, nested.</summary>
        public static Item BuildItem(JObject build) => BuildItem(build, out _, false);

        /// <summary>The same without its magazine: that's handed back to be filled and loaded separately.</summary>
        public static Item BuildItem(JObject build, out JObject magazine) => BuildItem(build, out magazine, true);

        private static Item BuildItem(JObject build, out JObject magazine, bool leaveMagazineOut)
        {
            magazine = null;
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            Item weapon = factory?.CreateItem(MongoID.Generate(), (string)build["tpl"], null);
            if (weapon != null)
            {
                var parts = build["parts"] as JArray;
                if (leaveMagazineOut && parts != null)
                {
                    JObject mag = parts.OfType<JObject>().FirstOrDefault(p => (string)p["slot"] == "mod_magazine");
                    if (mag != null)
                        parts = new JArray(parts.Where(p => p != mag));
                    magazine = mag;
                }
                Fill(factory, weapon, parts);
            }
            return weapon;
        }

        private static void Fill(ItemFactory factory, Item parent, JArray parts)
        {
            if (parts == null || !(parent is CompoundItem compound) || compound.Slots == null)
                return;
            foreach (JObject p in parts.OfType<JObject>())
            {
                Slot slot = compound.Slots.FirstOrDefault(s => s.ID == (string)p["slot"]);
                Item part = slot == null ? null : factory.CreateItem(MongoID.Generate(), (string)p["tpl"], null);
                if (part == null)
                {
                    ZombiesPlugin.Log.LogWarning($"Box gun part {(string)p["name"]} ({(string)p["slot"]}) couldn't be fitted");
                    continue;
                }
                slot.AddWithoutRestrictions(part);
                Fill(factory, part, p["parts"] as JArray);
            }
        }

        public const string MonkeyId = "cymbal_monkey";

        /// <summary>A display model of the gun's base build, measured for the box; null if it couldn't be made.</summary>
        public static IEnumerator MakeModel(Gun gun, Action<Model> done)
        {
            // The monkey shows as itself (zombies.bundle), about the size of a gun in the box
            if (gun.Id == MonkeyId)
            {
                GameObject prefab = ZombiesAssets.Model("zm_monkey");
                if (prefab == null)
                    yield break;
                GameObject m = UnityEngine.Object.Instantiate(prefab);
                UnityEngine.Object.DontDestroyOnLoad(m);
                Bounds mb = WorldBounds(m);
                if (mb.size.y > 0.01f)
                    m.transform.localScale *= 0.35f / mb.size.y;
                m.GetComponentInChildren<Animator>()?.Play("rest", 0, 0f);
                // Sitting upright, its front (the cymbals) out of the box's front to the player, not laid along the box
                // like a gun (F12 "21. Monkey bomb" > "In the box: turn" corrects the facing)
                Quaternion upright = Quaternion.Euler(0f, ZombiesMonkey.BoxTurn, 0f);
                m.transform.SetPositionAndRotation(Vector3.zero, upright);
                done(new Model { Gun = gun, Go = m, Align = upright, Centre = WorldBounds(m).center });
                yield break;
            }
            Item item = null;
            try
            {
                item = BuildItem(gun);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Box gun {gun.Name}: {e.Message}");
            }
            if (item == null)
                yield break;
            GameObject go = null;
            yield return MakeItemModel(item, made => go = made);
            if (go == null)
                yield break;
            int renderers = go.GetComponentsInChildren<Renderer>(false).Count(r => r.enabled);
            ZombiesPlugin.Log.LogInfo($"Box gun {gun.Name}: {item.GetAllItems().Count() - 1} parts fitted, {renderers} visible meshes, " +
                                      $"{go.transform.childCount} children");
            done(Measure(gun, go));
        }

        /// <summary>Any item's world model (its bundles loaded first), still, with nothing on it colliding; null if it couldn't be made.</summary>
        public static IEnumerator MakeItemModel(Item item, Action<GameObject> done)
        {
            Task bundles = EasyAssetsExtensions.WaitForAllBundlesJob(item.GetAllBundleTokens(), () => { }, CancellationToken.None, null);
            while (!bundles.IsCompleted)
                yield return null;
            if (bundles.IsFaulted)
                yield break;
            GameObject pooled = Singleton<ObjectsFactory>.Instance?.CreateItem(item, false);
            if (pooled == null)
                yield break;
            // Our own copy, the game's pooled one handed straight back: we keep these models for the whole session,
            // and EFT reuses its pooled objects, so a kept pooled model ended up under another item's picture (an
            // RGD-5 at Prapor's showed our zombies SV-98 build, user 2026-11-09). The copy is made switched off (no
            // script wakes) and keeps only what draws: every script on it is taken off
            bool wasActive = pooled.activeSelf;
            pooled.SetActive(false);
            GameObject go = UnityEngine.Object.Instantiate(pooled);
            go.name = pooled.name.Replace("(Clone)", "") + " (zombies copy)";
            pooled.SetActive(wasActive);
            try
            {
                EFT.AssetsManager.AssetPoolObject.ReturnToPool(pooled, true);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: {item.LocalizedName()}'s pooled model couldn't go back ({e.Message}); destroyed");
                UnityEngine.Object.Destroy(pooled);
            }
            foreach (MonoBehaviour script in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                try
                {
                    UnityEngine.Object.DestroyImmediate(script);
                }
                catch
                {
                }
            }
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                c.enabled = false;
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(true);
            done(go);
        }

        /// <summary>
        /// Lies the model along the box: its longest side (at its own turn) is turned onto the box's length (X), then
        /// its middle is measured so it can hang centred on the box's weapon pivot.
        /// </summary>
        private static Model Measure(Gun gun, GameObject go)
        {
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Bounds b = WorldBounds(go);
            Quaternion align = b.size.x >= b.size.y && b.size.x >= b.size.z ? Quaternion.identity
                : b.size.z >= b.size.y ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.Euler(0f, 0f, 90f);
            go.transform.SetPositionAndRotation(Vector3.zero, align);
            Vector3 centre = WorldBounds(go).center;
            return new Model { Gun = gun, Go = go, Align = align, Centre = centre };
        }

        public static Bounds VisibleBounds(GameObject go) => WorldBounds(go);

        /// <summary>
        /// The visible model only, measured from its meshes themselves (renderer bounds can be stale on a model just
        /// moved, or cover LODs and effects that aren't drawn): active, enabled meshes, the nearest LOD only.
        /// </summary>
        private static Bounds WorldBounds(GameObject go)
        {
            var skip = new HashSet<Renderer>();
            foreach (LODGroup group in go.GetComponentsInChildren<LODGroup>(false))
            {
                LOD[] lods = group.GetLODs();
                for (int i = 1; i < lods.Length; i++)
                    foreach (Renderer r in lods[i].renderers)
                        if (r != null)
                            skip.Add(r);
            }
            bool any = false;
            var bounds = new Bounds(go.transform.position, Vector3.zero);
            Renderer[] all = go.GetComponentsInChildren<Renderer>(false);
            // EFT's weapon parts name their drawn mesh "..._LOD0": when there are such, nothing else counts (pistols
            // carry other meshes off to one side that pulled their middle away from the gun)
            bool lodNamed = all.Any(r => r.name.IndexOf("LOD0", StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (Renderer r in all)
            {
                // Shadow-only stand-ins (map props carry big ones) aren't what you see
                if (!r.enabled || r.forceRenderingOff || skip.Contains(r) || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                    continue;
                if (lodNamed && r.name.IndexOf("LOD0", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r is MeshRenderer ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
                if (mesh == null)
                    continue;
                Bounds local = mesh.bounds;
                Matrix4x4 m = r.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    Vector3 w = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        bounds = new Bounds(w, Vector3.zero);
                        any = true;
                    }
                    else
                        bounds.Encapsulate(w);
                }
            }
            return any ? bounds : new Bounds(go.transform.position, Vector3.one * 0.3f);
        }
    }
}
