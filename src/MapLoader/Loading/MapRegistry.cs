using System;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KennerZombies.MapLoader.Loading
{
    // Indexes the map bundles in every installed map's own folder (MapEditor.MapFolders):
    //
    //   BepInEx/plugins/SPZombies_Maps/ZM_<Name>/bundles/maps/<id>.bundle          (scenes preset)
    //   BepInEx/plugins/SPZombies_Maps/ZM_<Name>/bundles/maps/<id>_scenes.bundle   (scenes)
    //   (or BepInEx/plugins/Kennerz_ZM_<Map>/bundles/...)
    //
    // Each file is registered under its path relative to its bundles/ folder, lowercase with forward slashes,
    // which is the key the game asks BundlesManager for (base.json Scene.path). A map the original
    // CustomMapLoader mod already serves (its own plugin folder has <id>.bundle) is left to it.
    internal static class MapRegistry
    {
        private const string ScenesSuffix = "_scenes";
        private const string OriginalLoaderGuid = "com.mizmii.custommaploader";

        public static readonly List<string> BundleRoots = new List<string>();

        public static readonly Dictionary<string, string> BundleFiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> MapIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Map ids installed with us that the original CustomMapLoader mod also has, so it loads them.</summary>
        public static readonly HashSet<string> LeftToOriginal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Init(IEnumerable<string> mapFolders)
        {
            BundleRoots.Clear();
            BundleFiles.Clear();
            MapIds.Clear();
            LeftToOriginal.Clear();
            HashSet<string> theirs = OriginalLoaderMaps();
            foreach (string folder in mapFolders)
            {
                string root = Path.Combine(folder, "bundles");
                if (!Directory.Exists(root))
                    continue;
                BundleRoots.Add(root);
                foreach (var file in Directory.GetFiles(root, "*.bundle", SearchOption.AllDirectories))
                {
                    var key = file.Substring(root.Length + 1).Replace('\\', '/').ToLowerInvariant();
                    if (!key.StartsWith("maps/", StringComparison.Ordinal))
                        continue;
                    var name = Path.GetFileNameWithoutExtension(key);
                    var id = name.EndsWith(ScenesSuffix, StringComparison.Ordinal) ? name.Substring(0, name.Length - ScenesSuffix.Length) : name;
                    if (theirs.Contains(id))
                    {
                        if (LeftToOriginal.Add(id))
                            Loader.Log.LogInfo($"Map {id}: the CustomMapLoader mod is installed and has it too, so it loads it");
                        continue;
                    }
                    if (BundleFiles.ContainsKey(key))
                    {
                        Loader.Log.LogWarning($"Map bundle {key} is installed twice: using {BundleFiles[key]}, not {file}");
                        continue;
                    }
                    BundleFiles[key] = file;
                    if (id == name)
                        MapIds.Add(id);
                }
            }
        }

        // the map ids the original CustomMapLoader mod serves from its own plugin folder, when it's loaded
        private static HashSet<string> OriginalLoaderMaps()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(OriginalLoaderGuid, out var info) || info?.Location == null)
                    return ids;
                string maps = Path.Combine(Path.GetDirectoryName(info.Location) ?? ".", "bundles", "maps");
                if (Directory.Exists(maps))
                    foreach (var file in Directory.GetFiles(maps, "*.bundle"))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (!name.EndsWith(ScenesSuffix, StringComparison.OrdinalIgnoreCase))
                            ids.Add(name);
                    }
            }
            catch (Exception e)
            {
                Loader.Log.LogWarning($"Couldn't read the CustomMapLoader mod's maps ({e.Message})");
            }
            return ids;
        }

        public static bool TryGetBundleFile(string key, out string file)
        {
            file = null;
            if (string.IsNullOrEmpty(key))
                return false;
            return BundleFiles.TryGetValue(key.Replace('\\', '/'), out file);
        }

        // scene names contained in our scene bundles, recorded as the bundles are loaded
        private static readonly HashSet<string> SceneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void RegisterScenes(AssetBundle bundle)
        {
            foreach (var path in bundle.GetAllScenePaths())
                SceneNames.Add(Path.GetFileNameWithoutExtension(path));
        }

        public static bool IsCustomScene(Scene scene) => SceneNames.Contains(scene.name);

        // true while a raid on one of our maps is loading or running
        public static bool IsCustomMapActive()
        {
            try
            {
                // GameWorld.LocationId is only assigned in BaseLocalGame.Run, after the map has loaded
                var world = Singleton<GameWorld>.Instance;
                var id = world != null ? world.LocationId : null;
                if (!string.IsNullOrEmpty(id))
                    return MapIds.Contains(id);

                // during loading, recognise the map by its scenes
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && SceneNames.Contains(scene.name))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        // a map's baked audio (spatial audio routes, Meta acoustics) sits in bundles/maps/<id>_audio/; its
        // files are named after the map's scenes, so a name is enough to find one
        public static string FindAudioFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;
            foreach (var root in BundleRoots)
            {
                var maps = Path.Combine(root, "maps");
                if (!Directory.Exists(maps))
                    continue;
                foreach (var folder in Directory.GetDirectories(maps, "*_audio"))
                {
                    var file = Path.Combine(folder, fileName);
                    if (File.Exists(file))
                        return file;
                }
            }
            return null;
        }
    }
}
