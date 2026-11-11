using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// What the map editor's asset list offers, sorted into categories: the props of the map that's loaded (fences,
    /// crates, cars... found by their LOD groups and copied from the map itself, so only on that map) and every EFT
    /// item (made from its template, any map). A placed prop is saved as "scene:&lt;name&gt;" or "item:&lt;template&gt;".
    /// </summary>
    internal static class AssetCatalog
    {
        public const string ScenePrefix = "scene:", ItemPrefix = "item:";

        public class Asset
        {
            public string Key, Name, Category;
        }

        // ---------- Map props ----------

        private static string _scannedFor;
        private static readonly Dictionary<string, GameObject> SceneTemplates = new Dictionary<string, GameObject>();
        private static List<Asset> _sceneAssets = new List<Asset>();

        // Words in a prop's name that say what it is (first match wins)
        private static readonly (string category, string[] words)[] PropCategories =
        {
            ("Vehicles", new[] { "car", "truck", "bus", "btr", "vaz", "van", "tractor", "vehicle", "uaz", "zil", "kamaz", "lada", "moskvich", "trailer", "wagon", "train" }),
            ("Barriers & fences", new[] { "fence", "barrier", "gate", "jersey", "hesco", "sandbag", "barricade", "railing", "concrete_block", "blockpost", "bollard", "wire" }),
            ("Crates & barrels", new[] { "box", "crate", "container", "barrel", "pallet", "drum", "tank", "locker", "safe", "bag", "canister" }),
            ("Furniture", new[] { "chair", "table", "bed", "sofa", "desk", "shelf", "cabinet", "bench", "couch", "rack", "wardrobe", "stool", "matress", "mattress" }),
            ("Debris & junk", new[] { "debris", "rubble", "trash", "garbage", "junk", "pile", "brick", "plank", "tire", "tyre", "wreck", "scrap", "board", "broken", "paper" }),
            ("Lights & electrics", new[] { "lamp", "light", "pole", "cable", "generator", "transformer", "electric", "socket", "switch" }),
            ("Building parts", new[] { "door", "window", "stair", "roof", "pipe", "column", "beam", "floor", "wall", "ladder", "frame", "panel", "plate", "slab" }),
            ("Nature", new[] { "tree", "bush", "rock", "stone", "grass", "log", "plant", "fern", "branch", "stump" }),
        };

        private static readonly Regex CopySuffix = new Regex(@"\s*\(\d+\)$", RegexOptions.Compiled);

        private static string PropName(string name) => CopySuffix.Replace(name, "").Trim();

        /// <summary>The loaded map's props (scanned once per map).</summary>
        public static List<Asset> SceneAssets()
        {
            string location = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.LocationId : null;
            if (location != _scannedFor)
                ScanScene(location);
            return _sceneAssets;
        }

        /// <summary>Scans the map's props again next time they're asked for.</summary>
        public static void Rescan() => _scannedFor = null;

        /// <summary>A map prop to copy, by its name (null when this map hasn't got it).</summary>
        public static GameObject SceneTemplate(string name)
        {
            SceneAssets();
            return SceneTemplates.TryGetValue(name, out GameObject go) ? go : null;
        }

        private static void ScanScene(string location)
        {
            _scannedFor = location;
            SceneTemplates.Clear();
            var assets = new List<Asset>();
            foreach (LODGroup group in UnityEngine.Object.FindObjectsOfType<LODGroup>())
            {
                GameObject go = group.gameObject;
                if (go.name.Contains("(KennerZombies)") || go.GetComponentInParent<Player>() != null)
                    continue;
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                // Statically batched meshes can't be copied (they're drawn from one big combined mesh)
                if (renderers.Length == 0 || renderers.Any(r => r.isPartOfStaticBatch))
                    continue;
                string name = PropName(go.name);
                if (name.Length == 0 || SceneTemplates.ContainsKey(name))
                    continue;
                // Not whole buildings
                Bounds b = renderers[0].bounds;
                foreach (Renderer r in renderers)
                    b.Encapsulate(r.bounds);
                if (b.size.magnitude > 40f)
                    continue;
                SceneTemplates[name] = go;
                assets.Add(new Asset { Key = ScenePrefix + name, Name = name, Category = PropCategory(name) });
            }
            _sceneAssets = assets.OrderBy(a => a.Category).ThenBy(a => a.Name).ToList();
            ZombiesPlugin.Log.LogInfo($"Asset list: {_sceneAssets.Count} props on {location}");
        }

        private static string PropCategory(string name)
        {
            string lower = name.ToLowerInvariant();
            foreach ((string category, string[] words) in PropCategories)
                if (words.Any(lower.Contains))
                    return category;
            return "Other";
        }

        // ---------- Our models ----------

        public const string DecalPrefix = "decal:";

        /// <summary>Our own things (the original wall decals so far), any map. Picking one places that kind with that look.</summary>
        public static List<Asset> OurAssets() =>
            MapEditor.DecalLooks.Select(n => new Asset { Key = DecalPrefix + n, Name = n, Category = "Wall decals" }).ToList();

        // ---------- Items ----------

        private static List<Asset> _items;

        /// <summary>Every item template, by its parent type (Ammo, AssaultRifle, BarterItem...).</summary>
        public static List<Asset> ItemAssets()
        {
            if (_items != null)
                return _items;
            var list = new List<Asset>();
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            if (factory == null)
                return list;
            foreach (var pair in factory.ItemTemplates)
            {
                try
                {
                    ItemTemplate t = pair.Value;
                    if (t == null || string.IsNullOrEmpty(t.Prefab?.path))
                        continue;
                    list.Add(new Asset { Key = ItemPrefix + pair.Key, Name = t._name ?? pair.Key.ToString(), Category = t.Parent?._name ?? "Item" });
                }
                catch (Exception)
                {
                    // A template without a model or parent: not placeable
                }
            }
            _items = list.OrderBy(a => a.Category).ThenBy(a => a.Name).ToList();
            ZombiesPlugin.Log.LogInfo($"Asset list: {_items.Count} items");
            return _items;
        }

        /// <summary>A new item of the template, to make its model from; null if it can't be made.</summary>
        public static Item MakeItem(string template)
        {
            try
            {
                return Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), template, null);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Asset {template}: {e.Message}");
                return null;
            }
        }

        /// <summary>A short name for a saved key ("scene:fence_01" shows "fence_01").</summary>
        public static string Short(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "";
            if (key.StartsWith(ItemPrefix))
            {
                string tpl = key.Substring(ItemPrefix.Length);
                Asset a = _items?.FirstOrDefault(i => i.Key == key);
                return a != null ? a.Name : tpl;
            }
            return key.StartsWith(ScenePrefix) ? key.Substring(ScenePrefix.Length) : key;
        }
    }
}
