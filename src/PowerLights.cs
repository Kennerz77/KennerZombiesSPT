using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies map's power lights: the layout's PowerLight entries (placed in game with the map editor, F8) and any
    /// bar lights under "KZ_PowerLights" in the map itself start dark in a zombies match; PowerOn() flickers each one to life at its own moment, then they
    /// stay lit (their light and their bulbs' glow). The power switch will call PowerOn; until then F12 "9. Power (dev)"
    /// has a test toggle.
    /// </summary>
    internal class PowerLights : MonoBehaviour
    {
        private const string HolderName = "KZ_PowerLights";

        private static PowerLights _instance;
        private static ConfigEntry<bool> _test;

        private string _location;
        private readonly List<Fixture> _fixtures = new List<Fixture>();
        private bool _on;

        private class Fixture
        {
            public Light[] Lights;
            public Renderer[] Glows;
            public MaterialPropertyBlock Block = new MaterialPropertyBlock();
        }

        public static bool IsOn => _instance != null && _instance._on;

        public static void Bind(ConfigFile config, string section)
        {
            _test = config.Bind(section, "Power on (test)", false,
                "Turns the zombies map's power lights on (they flicker to life) or off, to try them until the power switch exists.");
            _test.SettingChanged += (_, __) =>
            {
                if (_test.Value)
                    PowerOn();
                else
                    PowerOff();
            };
        }

        /// <summary>The power comes on: every power light flickers to life.</summary>
        public static void PowerOn()
        {
            if (_instance == null || _instance._on || _instance._fixtures.Count == 0)
                return;
            _instance._on = true;
            foreach (Fixture f in _instance._fixtures)
                _instance.StartCoroutine(_instance.Flicker(f));
            ZombiesPlugin.Log.LogInfo($"Power lights: on ({_instance._fixtures.Count})");
        }

        /// <summary>On at once, no flicker (a map powered from the start); lights found later come on lit too.</summary>
        public static void AllOn()
        {
            if (_instance == null)
                return;
            _instance.StopAllCoroutines();
            _instance._on = true;
            foreach (Fixture f in _instance._fixtures)
                Set(f, true);
            ZombiesPlugin.Log.LogInfo($"Power lights: on from the start ({_instance._fixtures.Count})");
        }

        public static void PowerOff()
        {
            if (_instance == null)
                return;
            _instance.StopAllCoroutines();
            _instance._on = false;
            foreach (Fixture f in _instance._fixtures)
                Set(f, false);
        }

        private void Awake() => _instance = this;

        /// <summary>A power light the match built from the layout: dark until the power is on (lit at once if it is).</summary>
        public static void Register(GameObject light)
        {
            if (_instance == null)
                return;
            var f = new Fixture { Lights = light.GetComponentsInChildren<Light>(true), Glows = light.GetComponentsInChildren<Renderer>(true) };
            _instance._fixtures.Add(f);
            Set(f, _instance._on);
        }

        private void Update()
        {
            string location = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null && !ZombiesPlugin.InHideout
                ? Singleton<GameWorld>.Instance.LocationId : null;
            if (location == _location)
                return;
            _location = location;
            StopAllCoroutines();
            // Layout lights the match registered stay (it may build first); the map's own are looked up again
            _fixtures.RemoveAll(f => f.Lights.Length == 0 || f.Lights[0] == null);
            _on = false;
            foreach (Fixture f in _fixtures)
                Set(f, false);
            if (!ZombiesMaps.Is(location))
                return;
            GameObject holder = GameObject.Find(HolderName);
            if (holder == null)
                return;
            foreach (Transform t in holder.transform)
            {
                var f = new Fixture { Lights = t.GetComponentsInChildren<Light>(true), Glows = t.GetComponentsInChildren<Renderer>(true) };
                _fixtures.Add(f);
                Set(f, false);
            }
            ZombiesPlugin.Log.LogInfo($"Power lights: {_fixtures.Count} found, dark until the power is on");
            // The match may already have the power on (a map powered from the start built before these were found)
            if (ZombiesMatch.PowerIsOn)
                AllOn();
            else if (_test.Value)
                PowerOn();
        }

        // A short wait, a few stuttering flashes, then on for good
        private IEnumerator Flicker(Fixture f)
        {
            yield return new WaitForSeconds(Random.Range(0f, 1.5f));
            int flashes = Random.Range(3, 7);
            for (int i = 0; i < flashes; i++)
            {
                Set(f, true);
                yield return new WaitForSeconds(Random.Range(0.03f, 0.15f));
                Set(f, false);
                yield return new WaitForSeconds(Random.Range(0.05f, 0.35f));
            }
            Set(f, true);
        }

        // Light on/off, and the bulbs' glow (EFT's lit materials dim _EmissionVisibility, others _EmissionColor)
        private static void Set(Fixture f, bool on)
        {
            foreach (Light l in f.Lights)
                if (l != null)
                    l.enabled = on;
            foreach (Renderer r in f.Glows)
            {
                if (r == null || r.sharedMaterial == null)
                    continue;
                r.GetPropertyBlock(f.Block);
                if (r.sharedMaterial.HasProperty("_EmissionVisibility"))
                    f.Block.SetFloat("_EmissionVisibility", on ? 1f : 0f);
                else if (r.sharedMaterial.HasProperty("_EmissionColor"))
                    f.Block.SetColor("_EmissionColor", on ? r.sharedMaterial.GetColor("_EmissionColor") : Color.black);
                else
                    continue;
                r.SetPropertyBlock(f.Block);
            }
        }
    }
}
