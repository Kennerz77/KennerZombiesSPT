using System;
using System.Reflection;
using Comfort.Common;
using EFT.Weather;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // Simple time of day for the SDK's static lighting rig (Factory's TODSkySimple).
    //
    // The raid clock already reaches the rig: BaseLocalGame hands TOD_Time the GameDateTime, and
    // TOD_Time copies it into TODSkySimple.Cycle. But TODSkySimple locks the clock after the first
    // update and never moves the sun; it only reads the sun light's rotation. This driver:
    //
    //  - keeps the clock following the raid's time, unless the map froze it (TOD_Time "Lock Current
    //    Time" ticked in the editor = the time stays at the raid's start time)
    //  - places the sun for Cycle's hour and date at the rig's Latitude, facing the map's north
    //    (LevelSettings north direction)
    //  - fades the sun, its colour and the fog between day and night; TODSkySimple then derives the
    //    ambient light from the sun's height as it always does, and Unity's procedural skybox
    //    follows RenderSettings.sun
    internal class SimpleTimeOfDayPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(TODSkySimple), nameof(TODSkySimple.Awake));
        }

        // prefix: the authored freeze flag must be read before TODSkySimple's first update locks it
        [PatchPrefix]
        private static void Prefix(TODSkySimple __instance)
        {
            if (!MapRegistry.IsCustomScene(__instance.gameObject.scene) || __instance.GetComponent<SimpleTimeOfDay>() != null)
                return;
            var time = __instance.GetComponent<TOD_Time>();
            var driver = __instance.gameObject.AddComponent<SimpleTimeOfDay>();
            driver.Freeze = time != null && time.LockCurrentTime;
            Loader.Verbose($"Simple time of day on {__instance.name}: {(driver.Freeze ? "frozen at the raid's start time" : "follows the raid clock")}");
        }
    }

    internal class SimpleTimeOfDay : MonoBehaviour
    {
        public bool Freeze;

        private TODSkySimple _sky;
        private TOD_Time _time;
        private Light _sun;
        private float _sunIntensity;
        private Color _fogColor;
        private bool _fogCaptured;

        private void Start()
        {
            _sky = GetComponent<TODSkySimple>();
            _time = GetComponent<TOD_Time>();
            _sun = _sky != null ? _sky.LightSource : null;
            if (_sun == null)
            {
                enabled = false;
                return;
            }
            _sunIntensity = _sun.intensity;
            RenderSettings.sun = _sun;
        }

        private void LateUpdate()
        {
            if (_time != null && !Freeze)
                _time.LockCurrentTime = false;

            var cycle = _sky.Cycle;
            if (cycle == null)
                return;

            float north = Singleton<LevelSettings>.Instantiated ? Singleton<LevelSettings>.Instance.NorthDirection : 0f;
            var toSun = SunDirection(cycle, _sky.Latitude, north);
            _sun.transform.rotation = Quaternion.LookRotation(-toSun);

            // 0 below about -2 degrees, 1 above about 10
            float daylight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.035f, 0.17f, toSun.y));
            _sun.intensity = _sunIntensity * daylight;
            _sun.enabled = daylight > 0.001f;
            if (_sky.LightColorGradient != null)
                _sun.color = _sky.LightColorGradient.Evaluate(toSun.y * 0.5f + 0.5f);

            // LevelSettings applies its fog colour once; dim it at night so it doesn't glow grey
            if (!_fogCaptured)
            {
                _fogColor = RenderSettings.fogColor;
                _fogCaptured = true;
            }
            RenderSettings.fogColor = _fogColor * Mathf.Lerp(0.08f, 1f, daylight);
        }

        // direction towards the sun; +Z is the map's north rotated by northDegrees, +X east
        internal static Vector3 SunDirection(TOD_CycleParameters cycle, float latitude, float northDegrees)
        {
            int dayOfYear;
            try
            {
                dayOfYear = cycle.DateTime.DayOfYear;
            }
            catch (ArgumentOutOfRangeException)
            {
                dayOfYear = 172; // midsummer
            }
            float declination = 23.44f * Mathf.Deg2Rad * Mathf.Sin(2f * Mathf.PI * (284 + dayOfYear) / 365f);
            float hourAngle = (cycle.Hour - 12f) * 15f * Mathf.Deg2Rad;
            float lat = latitude * Mathf.Deg2Rad;

            float altitude = Mathf.Asin(Mathf.Sin(lat) * Mathf.Sin(declination) + Mathf.Cos(lat) * Mathf.Cos(declination) * Mathf.Cos(hourAngle));
            // azimuth from north, clockwise
            float azimuth = Mathf.Atan2(Mathf.Sin(hourAngle), Mathf.Cos(hourAngle) * Mathf.Sin(lat) - Mathf.Tan(declination) * Mathf.Cos(lat)) + Mathf.PI;

            var local = new Vector3(Mathf.Cos(altitude) * Mathf.Sin(azimuth), Mathf.Sin(altitude), Mathf.Cos(altitude) * Mathf.Cos(azimuth));
            return Quaternion.Euler(0f, northDegrees, 0f) * local;
        }
    }
}
