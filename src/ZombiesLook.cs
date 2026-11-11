using BepInEx.Configuration;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies match's dingy look (the user: "should be dingier"): EFT's daylight, ambient and sky brightness and
    /// its colours turned down while a match runs (TOD_Sky's day light, colour and ambient multipliers, the
    /// atmosphere's brightness), so an overcast morning reads grey and gloomy, not the bright white haze EFT gives full
    /// cloud. F12 "8. Rain" > "Dingy look" sets how strong (0 = EFT's own). Put back as it was when the match ends.
    /// The day multipliers are set every few seconds (EFT leaves them alone). The atmosphere's brightness EFT's
    /// ToDController.Update writes again every frame from its curves: that one is scaled in a postfix right after it,
    /// every frame. (Written every 2 s from a stored value, it dipped the sky for a frame on each beat: the sky
    /// "randomly flickers black", the user 2026-11-08.)
    /// </summary>
    internal class ZombiesLook : MonoBehaviour
    {
        private static ConfigEntry<float> _dingy, _fog;

        // The camera's world fog (TOD_Scattering) the weather drives. CustomMapLoader keeps it at least the map's own
        // density every frame (its WeatherFogDensityPatch: max(map's fog, weather's fog)), so the weather's fog could
        // only thicken it, never thin it (the user, 2026-11-08: "fog does nothing, it's one fog setting"). Our own
        // value is set after that each frame, in LateUpdate, without touching CustomMapLoader.
        private static readonly AccessTools.FieldRef<WeatherController, TOD_Scattering> Scattering =
            AccessTools.FieldRefAccess<WeatherController, TOD_Scattering>("tod_Scattering_0");

        /// <summary>The world fog's scattering component, or null.</summary>
        public static TOD_Scattering WorldFog => WeatherController.Instance != null ? Scattering(WeatherController.Instance) : null;
        private TOD_Sky _sky;
        private bool _applied;
        private float _next;
        // EFT's own values, kept to put back
        private float _light, _colour, _ambient;

        public static void Bind(ConfigFile config, string section)
        {
            _dingy = config.Bind(section, "Dingy look", 0f, new ConfigDescription(
                "How dingy a zombies match looks: EFT's daylight, ambient light, sky brightness and colour turned down (0 = EFT's own look, 1 = darkest).",
                new AcceptableValueRange<float>(0f, 1f)));
            // The weather reports (2026-11-08): Customs' own world fog is 0.0041 starting at its ground (-1.6);
            // zm_stronghold's level settings gave 0.064 from 78 m under the map (15 times thicker: the white haze)
            _fog = config.Bind(section, "World fog on a clear day", 0.0041f, new ConfigDescription(
                "The least haze in a zombies match (Customs' own is 0.0041); the weather's fog (F8) adds on top. 0 = the map's own setting.",
                new AcceptableValueRange<float>(0f, 0.1f)));
            _rainTest = config.Bind(section, "Rain cover test", 0, new ConfigDescription(
                "A test for the invisible rain: 0 off; 1 or 2 swap EFT's overhead cover map (what keeps rain off under roofs) for an all-black or all-white one. " +
                "If drops appear outdoors with one of them, the map's cover photo is what hides the rain.", new AcceptableValueRange<int>(0, 2)));
            _fogGround = config.Bind(section, "World fog ground height", 0f, new ConfigDescription(
                "Where the world fog starts, in metres (Customs: its own ground). The map's own setting put it 78 m under the ground.",
                new AcceptableValueRange<float>(-100f, 50f)));
            _fogPerSlider = config.Bind(section, "World fog per F8 fog", 0.15f, new ConfigDescription(
                "How much world fog the F8 fog slider adds (its 0.25 top end x this, on top of the clear-day haze). Higher = the slider fogs more.",
                new AcceptableValueRange<float>(0f, 1f)));
        }

        private static ConfigEntry<float> _fogGround, _fogPerSlider;
        private static readonly AccessTools.FieldRef<WeatherController, MBOIT_Scattering> Mboit =
            AccessTools.FieldRefAccess<WeatherController, MBOIT_Scattering>("mboit_Scattering_0");
        private static ConfigEntry<int> _rainTest;
        private static Texture2D _black, _white;
        private static Texture _realCover;
        private static float _weatherFog;
        private static bool _hooked;

        /// <summary>Our own hook on the weather's fog (after CustomMapLoader's, which stays as it is): what it asks for, kept.</summary>
        public static void Patch(Harmony harmony)
        {
            if (_hooked)
                return;
            _hooked = true;
            var m = AccessTools.Method(typeof(WeatherController), "ControlScattering");
            if (m == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies look: the weather's fog method wasn't found; the map's own fog stays");
                return;
            }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(ZombiesLook), nameof(WeatherFog)) { priority = Priority.Last });
            var tod = AccessTools.Method(typeof(ToDController), "Update");
            if (tod == null)
                ZombiesPlugin.Log.LogWarning("Zombies look: EFT's time-of-day update wasn't found; the sky keeps EFT's brightness");
            else
                harmony.Patch(tod, postfix: new HarmonyMethod(typeof(ZombiesLook), nameof(DingySky)) { priority = Priority.Last });
        }

        // Right after EFT sets the sky's brightness for this frame (from its curves): ours on top, every frame
        private static void DingySky()
        {
            float d = ZombiesMatch.Active ? _dingy?.Value ?? 0f : 0f;
            if (d <= 0.001f)
                return;
            TOD_Sky sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            if (sky == null || sky.Atmosphere == null)
                return;
            sky.Atmosphere.Brightness *= Mathf.Lerp(1f, 0.45f, d);
            sky.Atmosphere.ScatteringBrightness *= Mathf.Lerp(1f, 0.6f, d);
        }

        private static void WeatherFog(float __0) => _weatherFog = __0;

        private static int _lastTest;

        // F12 "Rain cover test": EFT's rain cover map (the global _WeatherDepthMap) swapped for a plain one each frame
        private static void RainTest()
        {
            int test = ZombiesMatch.Active ? _rainTest?.Value ?? 0 : 0;
            if (test == 0)
            {
                if (_lastTest != 0 && _realCover != null)
                    Shader.SetGlobalTexture("_WeatherDepthMap", _realCover);
                _lastTest = 0;
                return;
            }
            if (_black == null)
            {
                _black = new Texture2D(4, 4, TextureFormat.RGBAFloat, false);
                _white = new Texture2D(4, 4, TextureFormat.RGBAFloat, false);
                var b = new Color[16];
                var w = new Color[16];
                for (int i = 0; i < 16; i++)
                {
                    b[i] = new Color(0f, 0f, 0f, 0f);
                    w[i] = new Color(1f, 1f, 1f, 1f);
                }
                _black.SetPixels(b);
                _black.Apply();
                _white.SetPixels(w);
                _white.Apply();
            }
            Texture now = Shader.GetGlobalTexture("_WeatherDepthMap");
            if (now != null && now != _black && now != _white)
                _realCover = now;
            Shader.SetGlobalTexture("_WeatherDepthMap", test == 1 ? _black : _white);
            if (test != _lastTest)
                ZombiesPlugin.Log.LogInfo($"Zombies rain test: cover map {(test == 1 ? "all black" : "all white")} (real one: {_realCover?.name ?? "none"})");
            _lastTest = test;
        }

        // After everything else this frame: the clear-day floor (ours, not the map's) or the weather's fog if thicker,
        // from the ground up
        private void LateUpdate()
        {
            RainTest();
            float floor = _fog?.Value ?? 0f;
            if (floor <= 0f || !ZombiesMatch.Active)
                return;
            TOD_Scattering sc = WorldFog;
            if (sc == null)
                return;
            // The F8 fog slider straight into the world fog. EFT's own weather fog is the slider times a sun-height curve
            // that is about nothing by day (the map's held 13:00), so the slider "changed nothing" (user, 2026-11-09)
            float held = MapWeather.Held != null ? MapWeather.Held.Fog : 0f;
            float density = floor + Mathf.Max(0f, held) * (_fogPerSlider?.Value ?? 0.15f);
            sc.GlobalDensity = Mathf.Max(density, _weatherFog);
            sc.ZeroLevel = _fogGround?.Value ?? 0f;
            // EFT's other fog renderer (MBOIT), when the camera uses it, takes the same thickness
            if (sc.MBOIT && WeatherController.Instance != null)
            {
                MBOIT_Scattering m = Mboit(WeatherController.Instance);
                if (m != null)
                    m.GlobalDensity = sc.GlobalDensity;
            }
        }

        private void Update()
        {
            if (Time.time < _next)
                return;
            _next = Time.time + 2f;
            bool want = ZombiesMatch.Active && (_dingy?.Value ?? 0f) > 0.001f;
            TOD_Sky sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            if (sky != _sky)
            {
                // A new map's sky: the old one's values are gone with it
                _sky = sky;
                _applied = false;
            }
            if (_sky == null || _sky.Day == null || _sky.Atmosphere == null)
                return;
            if (want)
            {
                if (!_applied)
                {
                    _light = _sky.Day.LightIntensity;
                    _colour = _sky.Day.ColorMultiplier;
                    _ambient = _sky.Day.AmbientMultiplier;
                    _applied = true;
                    ZombiesPlugin.Log.LogInfo($"Zombies look: dingy {_dingy.Value:0.##} (EFT's day light {_light}, colour {_colour}, ambient {_ambient}; sky brightness scaled each frame)");
                }
                float d = _dingy.Value;
                _sky.Day.LightIntensity = _light * Mathf.Lerp(1f, 0.55f, d);
                _sky.Day.ColorMultiplier = _colour * Mathf.Lerp(1f, 0.7f, d);
                _sky.Day.AmbientMultiplier = _ambient * Mathf.Lerp(1f, 0.55f, d);
            }
            else if (_applied)
            {
                _sky.Day.LightIntensity = _light;
                _sky.Day.ColorMultiplier = _colour;
                _sky.Day.AmbientMultiplier = _ambient;
                _applied = false;
            }
        }
    }
}
