using System.Collections.Generic;
using System.Reflection;
using EFT.Weather;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KennerZombies.MapLoader.Loading
{
    // WeatherController's per-frame update (method_4) starts with ControlClouds, which hands the sky's
    // harmonics to CloudController.UpdateAmbient. That writes into a buffer the CloudController only
    // has while it's enabled (OnEnable makes it, OnDisable drops it), and nothing checks: with the
    // clouds switched off (WeatherActivation does that for a layer without its texture), their object
    // inactive or the reference empty, it throws every frame, and everything after it in method_4
    // (wind, fog, rain, wetting) never runs. On SDK maps the clouds' step is skipped instead.
    internal class CloudsNotRunningPatch : ModulePatch
    {
        private static readonly HashSet<int> Reported = new HashSet<int>();

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(WeatherController), "ControlClouds");

        [PatchPrefix]
        private static bool Prefix(WeatherController __instance)
        {
            if (!MapRegistry.IsCustomMapActive())
                return true;
            var clouds = __instance.CloudController;
            string problem =
                clouds == null ? "the WeatherController has no CloudController" :
                !clouds.isActiveAndEnabled ? $"the CloudController on '{clouds.name}' is {(clouds.enabled ? "on an inactive object" : "disabled")}" :
                !clouds.IsInitialized ? "the CloudController isn't initialised" :
                clouds.Settings == null ? "the CloudController has no cloud layer" :
                null;
            if (problem == null)
                return true;
            if (Reported.Add(__instance.GetInstanceID()))
                Loader.Log.LogWarning($"Clouds skipped: {problem}. The rest of the weather still runs. Rebuild the map's full weather system " +
                                            "(time of day / weather tools) if the map should have clouds.");
            return false;
        }
    }
}
