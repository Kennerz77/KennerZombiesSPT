using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unity 2022.3.43f1 editor helpers for Kennerz Zombies Mode.
/// Menu: Zombies > 1. Import mystery box, Zombies > 3. Import barricade, then Zombies > 2. Build zombies.bundle.
/// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod ZombiesBundleTools.RunAllBatch
/// </summary>
public static class ZombiesBundleTools
{
    private const string BundleName = "zombies.bundle";
    private const string BuildDir = "AssetBundles";
    // Only this install. Do NOT point at D:\OTHER LAUNCHERS\BSTATE GAMES.
    private const string PluginDir = @"D:\OTHER LAUNCHERS\BUTTERSTATE\BepInEx\plugins\Kennerz_ZM_Core";

    [MenuItem("Zombies/2. Build zombies.bundle")]
    public static void BuildBundleMenu()
    {
        string dest = BuildBundle();
        if (dest != null)
            EditorUtility.RevealInFinder(dest);
    }

    public static string BuildBundle()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(BoxImporter.PrefabPath) == null)
        {
            Debug.LogError($"[Zombies] No prefab at {BoxImporter.PrefabPath}. Run step 1 first.");
            return null;
        }
        // Every sound too, so the plugin can load any of them by name
        string[] sounds = Directory.GetFiles("Assets/Zombies/Audio", "*.wav").Select(p => p.Replace('\\', '/')).ToArray();
        Directory.CreateDirectory(BuildDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(BuildDir,
            new[] { new AssetBundleBuild { assetBundleName = BundleName, assetNames = new[] { BoxImporter.PrefabPath, BoxImporter.FakePrefabPath }
                .Concat(File.Exists(BoxImporter.BarricadePrefabPath) ? new[] { BoxImporter.BarricadePrefabPath } : new string[0])
                .Concat(File.Exists(BoxImporter.PowerSwitchPrefabPath) ? new[] { BoxImporter.PowerSwitchPrefabPath } : new string[0])
                .Concat(BoxImporter.LookPrefabPaths)
                .Concat(ZombiesMonkeyHands.Editor.MonkeyHandsImporter.ClipPaths)
                .Concat(sounds).ToArray() } },
            BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            Debug.LogError("[Zombies] Bundle build failed; see console.");
            return null;
        }
        Directory.CreateDirectory(PluginDir);
        string dest = Path.Combine(PluginDir, BundleName);
        try
        {
            File.Copy(Path.Combine(BuildDir, BundleName), dest, true);
            Debug.Log($"[Zombies] Built and copied bundle -> {dest}");
        }
        catch (IOException e)
        {
            // The game has the old bundle open: built, copy it over once the game is closed
            Debug.LogWarning($"[Zombies] Bundle built in {BuildDir} but not copied (is the game running?): {e.Message}");
            return Path.GetFullPath(Path.Combine(BuildDir, BundleName));
        }
        return dest;
    }

    public static void RunAllBatch()
    {
        try
        {
            BoxImporter.Import();
            BoxImporter.ImportBarricade();
            BoxImporter.ImportPowerSwitch();
            BoxImporter.ImportLooks();
            BoxImporter.ImportPowerUps();
            BoxImporter.ImportRock();
            BoxImporter.ImportMonkey();
            try { ZombiesMonkeyHands.Editor.MonkeyHandsImporter.Import(); }
            catch (Exception e) { Debug.LogError("[Zombies] Monkey hands not made: " + e); }
            if (BuildBundle() == null)
                EditorApplication.Exit(1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
