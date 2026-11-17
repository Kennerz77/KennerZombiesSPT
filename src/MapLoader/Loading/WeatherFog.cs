using System.Collections.Generic;
using System.Reflection;
using EFT.Weather;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // How the weather's fog reaches the screen:
    //
    //  - MBOIT fog (MBOIT_Scattering, volumetric) only runs where the scene has a WindowsManager, the
    //    OIT/MBOIT renderer. Only Shoreline and Labyrinth have one, and its shaders and compute shaders
    //    ship only in Shoreline's scene files, so SDK maps (like Customs, Woods or Factory) can't use it.
    //  - TOD_Scattering, the camera's world fog, takes the weather's fog density only in the weather
    //    debug mode; otherwise it keeps the camera's fixed density, so a foggy raid looks like a clear one.
    //
    // On SDK maps WeatherFogDensityPatch does what the debug mode does: the world fog follows the
    // weather, never thinner than the map's own fog.
    internal class WeatherFogDensityPatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<WeatherController, TOD_Scattering> Scattering =
            AccessTools.FieldRefAccess<WeatherController, TOD_Scattering>("tod_Scattering_0");
        private static readonly Dictionary<int, float> BaseDensity = new Dictionary<int, float>();

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(WeatherController), "ControlScattering");

        // density: the weather's fog scaled by FogMultyplyer for the sun's height, as the debug mode uses it
        [PatchPostfix]
        private static void Postfix(WeatherController __instance, float __0)
        {
            if (!MapRegistry.IsCustomMapActive() || WindowsManager.InstanceIsActive())
                return;
            var scattering = Scattering(__instance);
            if (scattering == null)
                return;
            int id = scattering.GetInstanceID();
            if (!BaseDensity.TryGetValue(id, out var baseDensity))
                BaseDensity[id] = baseDensity = scattering.GlobalDensity;
            scattering.GlobalDensity = Mathf.Max(baseDensity, __0);
        }
    }

    // WeatherController switches TOD_Scattering into MBOIT mode whenever it has MBOITFogRemapDataV2 (as on
    // Customs), and its per-frame update (method_4) then reads the raid clock, TOD_Time.GameDateTime,
    // unchecked. On SDK maps the weather wakes a few frames before the clock is set, so those frames
    // are skipped instead of throwing.
    internal class MboitFogTimePatch : ModulePatch
    {
        private static readonly AccessTools.FieldRef<WeatherController, TOD_Scattering> Scattering =
            AccessTools.FieldRefAccess<WeatherController, TOD_Scattering>("tod_Scattering_0");

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(WeatherController), "method_4");

        [PatchPrefix]
        private static bool Prefix(WeatherController __instance)
        {
            if (!MapRegistry.IsCustomMapActive())
                return true;
            var scattering = Scattering(__instance);
            if (scattering == null || !scattering.MBOIT)
                return true;
            var time = TODSkyProvider.IsAvailable ? TODSkyProvider.Instance.CurrentTime : null;
            return time != null && time.GameDateTime != null;
        }
    }
}
