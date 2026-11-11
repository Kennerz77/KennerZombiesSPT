using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Weather;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Nacht der Untoten style night on the zombies map: dark sky, but a bright, soft, slightly blue moonlight so the
    /// map can be seen, without hard moon shadows. Only on zombies maps (ZombiesMaps, their "Moonlight" setting), or every map while
    /// "Every map (dev)" is on; F12 "7. Moonlight" changes it live; turned off, the game's own night comes back.
    /// How EFT lights the night (decompiled): the sky light's colour comes from the weather's ToDController.LightColor
    /// gradient (near black at night), its strength from TOD_Sky.Night.LightIntensity, its shadows from
    /// Night.ShadowStrength; the ambient fill is that colour times ToDController.AmbientBrightness (cut by clouds and
    /// fog). So the gradient's night end becomes our moon colour, the ambient curve's night end is raised, and the
    /// night strength and shadows are set.
    /// </summary>
    internal class Moonlight : MonoBehaviour
    {
        private static readonly Color MoonBlue = new Color(0.62f, 0.72f, 1f);

        private static ConfigEntry<bool> _on, _everyMap;
        private static ConfigEntry<float> _brightness, _fill, _shadows, _blue;

        private TOD_Sky _sky;
        private ToDController _tod;
        private float _next;
        private string _applied; // the slider values last applied
        // The game's own, to put back
        private float _origIntensity, _origShadow;
        private Gradient _origColour;
        private AnimationCurve _origAmbient;

        public static void Bind(ConfigFile config, string section)
        {
            _on = config.Bind(section, "Moonlight on", false, new ConfigDescription(
                "Bright moonlit night on the zombies map (Stronghold): see fine, still night, no hard shadows.", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 6 }));
            _brightness = config.Bind(section, "Moon brightness", 0f, new ConfigDescription(
                "How strong the moonlight is (the game's own night is 0.1).", new AcceptableValueRange<float>(0f, 4f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 5 }));
            _fill = config.Bind(section, "Night fill light", 0.009f, new ConfigDescription(
                "Soft light from everywhere, so shadowed places aren't black (1 = the game's own). Clouds and fog lower it.", new AcceptableValueRange<float>(0f, 10f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 4 }));
            _shadows = config.Bind(section, "Moon shadows", 0.315f, new ConfigDescription(
                "0 = no moon shadows (Nacht look), 1 = full shadows.", new AcceptableValueRange<float>(0f, 1f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 3 }));
            _blue = config.Bind(section, "Blue tint", 0f, new ConfigDescription(
                "0 = white moonlight, 1 = cold blue.", new AcceptableValueRange<float>(0f, 1f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 2 }));
            _everyMap = config.Bind(section, "Every map (dev)", false, new ConfigDescription(
                "Use the moonlight on every map too (to try it out; normal raids are left alone when off).", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 1 }));
        }

        private void Update()
        {
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.25f;
            string location = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null && !ZombiesPlugin.InHideout
                ? Singleton<GameWorld>.Instance.LocationId : null;
            bool want = _on.Value && location != null && (ZombiesMaps.Get(location)?.Moonlight == true || _everyMap.Value);
            if (_sky == null)
            {
                if (!want)
                    return;
                _sky = MonoBehaviourSingleton<TOD_Sky>.Instance;
                _tod = WeatherController.Instance != null ? WeatherController.Instance.TimeOfDayController : null;
                if (_sky == null || _sky.Night == null || _tod == null || _tod.LightColor == null || _tod.AmbientBrightness == null)
                {
                    _sky = null;
                    return;
                }
                _origIntensity = _sky.Night.LightIntensity;
                _origShadow = _sky.Night.ShadowStrength;
                _origColour = _tod.LightColor;
                _origAmbient = _tod.AmbientBrightness;
                _applied = null;
                ZombiesPlugin.Log.LogInfo($"Moonlight: on ({location}); the game's night: strength {_origIntensity:0.###}, shadows {_origShadow:0.##}, " +
                                          $"colour at midnight {_origColour.Evaluate(0f)}, ambient at midnight {_origAmbient.Evaluate(-1f):0.###}");
            }
            if (want)
                _active = true;
            else
            {
                _active = false;
                Restore();
                _sky = null;
                _tod = null;
            }
        }

        private bool _active;

        // Every frame, after the weather has set the night for its clouds: set once in a while, the two took turns and the
        // light jumped (most with heavy cloud)
        private void LateUpdate()
        {
            if (_active && _sky != null && _tod != null)
                Apply();
        }

        private void Apply()
        {
            // Set every time (the weather may set them); the gradient and curve only rebuilt when a slider moved
            _sky.Night.LightIntensity = _brightness.Value;
            _sky.Night.ShadowStrength = _shadows.Value;
            string now = $"{_brightness.Value}|{_fill.Value}|{_blue.Value}";
            if (now == _applied)
                return;
            _applied = now;
            _tod.LightColor = NightColour(_origColour, Color.Lerp(Color.white, MoonBlue, _blue.Value));
            _tod.AmbientBrightness = NightAmbient(_origAmbient, _fill.Value);
        }

        private void Restore()
        {
            if (_sky != null && _sky.Night != null)
            {
                _sky.Night.LightIntensity = _origIntensity;
                _sky.Night.ShadowStrength = _origShadow;
            }
            if (_tod != null)
            {
                _tod.LightColor = _origColour;
                _tod.AmbientBrightness = _origAmbient;
            }
            ZombiesPlugin.Log.LogInfo("Moonlight: off, the game's own night back");
        }

        // The gradient runs over the sun's height (0 = sun straight down, 0.5 = horizon, 1 = noon): below the horizon
        // it's our moon colour, from the horizon up the game's own
        private static Gradient NightColour(Gradient original, Color moon)
        {
            float[] times = { 0f, 0.25f, 0.42f, 0.47f, 0.52f, 0.6f, 0.8f, 1f };
            var keys = new GradientColorKey[times.Length];
            for (int i = 0; i < times.Length; i++)
                keys[i] = new GradientColorKey(times[i] <= 0.42f ? moon : original.Evaluate(times[i]), times[i]);
            var g = new Gradient();
            g.SetKeys(keys, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        // The curve runs over the sun's height (-1 .. 1): its night half (sun below -0.1) is raised by fill
        private static AnimationCurve NightAmbient(AnimationCurve original, float fill)
        {
            var curve = new AnimationCurve();
            for (int i = 0; i <= 20; i++)
            {
                float t = -1f + i * 0.1f;
                float k = t <= -0.1f ? fill : t >= 0.1f ? 1f : Mathf.Lerp(fill, 1f, (t + 0.1f) / 0.2f);
                curve.AddKey(t, original.Evaluate(t) * k);
            }
            return curve;
        }
    }
}
