using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.AssetsManager;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // Serves custom map bundles straight from the plugin folder.
    //
    // BundlesManager.BundleLoadOperation.Run checks the loaded-bundle dictionary (_bundles) before
    // anything else and only falls back to downloading from StreamingAssets on a miss. Loading our
    // file with AssetBundle.LoadFromFile and inserting it under the requested key makes that first
    // lookup hit, so nothing is written into the game's data folder and no manifest entry is needed.
    // Scenes are then loaded by name, which works for any loaded bundle.
    //
    // Approach from an earlier IcebreakerBundleHost.
    internal class BundleHostPatch : ModulePatch
    {
        // one AssetBundle per file, shared by every spelling of its key: the preset is requested
        // lowercased, scene bundles are requested as written in the preset
        private static readonly Dictionary<string, AssetBundle> Loaded =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BundlesManager), nameof(BundlesManager.LoadBundleAsync));
        }

        [PatchPrefix]
        private static void Prefix(BundlesManager __instance, string bundleName)
        {
            try
            {
                if (!MapRegistry.TryGetBundleFile(bundleName, out var file))
                    return;
                if (__instance._bundles.ContainsKey(bundleName))
                    return;

                // a cached bundle the game has since unloaded compares equal to null
                if (!Loaded.TryGetValue(file, out var bundle) || bundle == null)
                {
                    var started = Time.realtimeSinceStartup;
                    bundle = AssetBundle.LoadFromFile(file);
                    if (bundle == null)
                    {
                        Loader.Log.LogError($"Failed to load bundle '{bundleName}' from {file}");
                        return;
                    }
                    Loaded[file] = bundle;
                    if (bundle.isStreamedSceneAssetBundle)
                        MapRegistry.RegisterScenes(bundle);
                    Loader.Verbose($"Loaded '{bundleName}' from the plugin folder in {(Time.realtimeSinceStartup - started) * 1000f:F0} ms");
                }

                // the reference starts with Counter = 1, matching a fresh download; when the game
                // unloads it the AssetBundle is unloaded too, and the next raid loads it again
                __instance._bundles[bundleName] = new BundlesManager.AssetBundleReference(bundle);
            }
            catch (Exception e)
            {
                // fall through to the game's own loader instead of breaking the raid
                Loader.Log.LogError($"Serving '{bundleName}' failed: {e}");
            }
        }
    }
}
