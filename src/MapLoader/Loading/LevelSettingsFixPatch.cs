using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // LevelSettings.ApplySettings copies its fields to RenderSettings in Awake and again after
    // loading. Two values break SDK maps built before the SDK set them:
    //
    //  - fogMode: the camera's GlobalFog image effect fogs every opaque pixel using RenderSettings.fogMode,
    //    fogDensity and fogColor, whether or not RenderSettings.fog is on. Its shader only computes a
    //    fog factor for Linear, Exponential and ExponentialSquared and starts from 0 (fully fogged), so
    //    an unset mode (0) paints all opaque geometry in fogColor, black by default. Replaced with
    //    exponential fog of density 0 (no fog).
    //  - skybox: none means a black sky. Replaced with the Unity default skybox the scene bundle carries.
    //  - default reflection: Unity scenes reflect their skybox at full intensity on every surface, a
    //    static daytime cubemap that makes the map shine at night. Retail scenes set Custom with no
    //    cubemap and intensity 0; their reflections come from AmbientLight's sky probe and the map's
    //    own reflection probes, which follow the time of day. Always applied.
    //
    // Maps built with the current SDK set both.
    internal class LevelSettingsFixPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LevelSettings), nameof(LevelSettings.ApplySettings));
        }

        [PatchPostfix]
        private static void Postfix(LevelSettings __instance)
        {
            if (!MapRegistry.IsCustomScene(__instance.gameObject.scene))
                return;

            RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = null;
            RenderSettings.reflectionIntensity = 0f;

            var mode = (int)__instance.fogMode;
            if (mode < (int)FogMode.Linear || mode > (int)FogMode.ExponentialSquared)
            {
                __instance.fogMode = FogMode.Exponential;
                __instance.fogDensity = 0f;
                RenderSettings.fogMode = FogMode.Exponential;
                RenderSettings.fogDensity = 0f;
                Loader.Log.LogWarning($"LevelSettings in {__instance.gameObject.scene.name} has no fog mode, which makes GlobalFog black out the map; using exponential fog with density 0. Rebuild the map with the current SDK.");
            }

            // the full weather system's TOD sky draws the sky dome itself, as on retail maps: a skybox
            // is drawn after it and its clouds and would cover both
            if (HasFullWeather(__instance.gameObject.scene))
            {
                if (__instance.skybox != null)
                {
                    __instance.skybox = null;
                    RenderSettings.skybox = null;
                    Loader.Verbose("Full weather system: LevelSettings skybox cleared so the TOD sky shows");
                }
            }
            else if (__instance.skybox == null)
            {
                var fallback = FindMaterial("Default-Skybox");
                if (fallback != null)
                {
                    __instance.skybox = fallback;
                    RenderSettings.skybox = fallback;
                    Loader.Log.LogWarning($"LevelSettings in {__instance.gameObject.scene.name} has no skybox; using Unity's default. Set LevelSettings.skybox in the SDK.");
                }
            }
        }

        private static bool HasFullWeather(UnityEngine.SceneManagement.Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "SDK_Weather")
                    return true;
            return false;
        }

        private static Material FindMaterial(string name)
        {
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                if (material != null && material.name == name && material.shader != null && material.shader.isSupported)
                    return material;
            return null;
        }
    }
}
