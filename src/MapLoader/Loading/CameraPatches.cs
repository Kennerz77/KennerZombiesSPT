using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // Retail maps set LevelSettings.CameraPrefab to a modern FPS camera. SDK maps leave it empty, so
    // CameraManager falls back to the built-in "Cam2" from Resources, which predates parts of the
    // camera stack:
    //  - EffectsController.Init ends with an unchecked GetComponent<FrostbiteEffect>(); Cam2 has none,
    //    so Init throws before it adds SSAAImpl.
    //  - the graphics settings binding then calls CameraManager.SetFSR2 etc., which dereference
    //    SSAA components Cam2 may still lack.
    //  - Cam2's PostProcessLayer has no PostProcessResources, so DLSS/FSR output can be black.
    //
    // Fixes from an earlier RaidFixPatches (Patch_EffectsControllerFrostbite,
    // Patch_CameraGraphicsSetters, HealPostProcessLayer), used with the author's permission.
    internal class CameraEffectsPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EffectsController), nameof(EffectsController.Awake));
        }

        [PatchPrefix]
        private static void Prefix(EffectsController __instance)
        {
            if (!MapRegistry.IsCustomMapActive())
                return;

            // FrostbiteEffect.Awake only caches a component reference; Init disables it straight away
            if (__instance.GetComponent<FrostbiteEffect>() == null)
            {
                __instance.gameObject.AddComponent<FrostbiteEffect>();
                Loader.Verbose("Added FrostbiteEffect to the fallback camera");
            }
            HealPostProcessLayer(__instance.gameObject);
            GraphicsModsCompat.PrepareCamera(__instance.gameObject);
            // before EffectsController caches RainScreenDrops/ScreenWater and WeatherController looks for TOD_Scattering
            WeatherActivation.CopyCameraEffects(__instance.gameObject);
        }

        private static void HealPostProcessLayer(GameObject go)
        {
            try
            {
                var layerType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
                var resourcesType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessResources");
                if (layerType == null || resourcesType == null)
                    return;
                var layer = go.GetComponent(layerType) as Behaviour;
                var field = AccessTools.Field(layerType, "m_Resources");
                if (layer == null || field == null || field.GetValue(layer) as UnityEngine.Object != null)
                    return;

                var found = Resources.FindObjectsOfTypeAll(resourcesType);
                if (found.Length == 0)
                {
                    Loader.Log.LogWarning("Fallback camera has no post-processing resources and none are loaded; DLSS/FSR may show a black screen.");
                    return;
                }
                // re-enable so OnEnable initialises the layer with the resources
                bool wasEnabled = layer.enabled;
                layer.enabled = false;
                var init = AccessTools.Method(layerType, "Init", new[] { resourcesType });
                if (init != null)
                    init.Invoke(layer, new object[] { found[0] });
                else
                    field.SetValue(layer, found[0]);
                layer.enabled = wasEnabled;
            }
            catch (Exception e)
            {
                Loader.Log.LogWarning($"Post-processing layer setup failed: {e.Message}");
            }
        }
    }

    // Upscaling, anti-aliasing, super-sampling, aspect and SSR setters are cosmetic. On a custom map,
    // a failure in one of them must not abort player camera creation; the raid runs at native
    // resolution instead.
    internal class CameraSettingSetterPatch : ModulePatch
    {
        private static readonly string[] Setters =
        {
            "SetFSR", "SetFSR2", "SetFSR3", "SetDLSSPreset", "SetAntiAliasing", "SetSuperSampling", "SetAspectRatio", "SetSSR",
        };

        private readonly MethodBase _target;

        public CameraSettingSetterPatch(MethodBase target)
        {
            _target = target;
        }

        protected override MethodBase GetTargetMethod() => _target;

        public static List<CameraSettingSetterPatch> ForAllSetters()
        {
            var patches = new List<CameraSettingSetterPatch>();
            foreach (var method in typeof(CameraManager).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (Array.IndexOf(Setters, method.Name) >= 0 && !method.IsGenericMethodDefinition)
                    patches.Add(new CameraSettingSetterPatch(method));
            return patches;
        }

        [PatchFinalizer]
        private static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null || !MapRegistry.IsCustomMapActive())
                return __exception;
            Loader.Log.LogWarning($"Custom map: CameraManager.{__originalMethod.Name} failed on the fallback camera, ignored ({__exception.Message})");
            return null;
        }
    }
}
