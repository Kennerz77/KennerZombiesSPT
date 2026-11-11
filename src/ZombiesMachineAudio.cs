using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// A perk machine's sounds in a zombies match, as Perk Beverages' own machines do them: its clips (PerkAssets:
    /// "perk_hum", "&lt;perk&gt;_jingle", "&lt;perk&gt;_sting", "perk_power_on", "perk_broken_N", "bottle_dispense") at
    /// Perk Beverages' own F12 volumes (Jingle, Sting, Hum, Power-on, Sparks, Purchase), so its sliders set the machines
    /// here too. Off: a spark now and then. The power coming on: the start-up sound, then the hum, the jingle now and then.
    /// Solo, Quick Revive always has power. The drink, burp and perk come from Perk Beverages' bottle (ZombiesPerks.Drink).
    /// </summary>
    internal class ZombiesMachineAudio : MonoBehaviour
    {
        private const float JingleMin = 40f, JingleMax = 110f;
        // How far the machines carry (user 2026-11-09: 25 m was heard right across the map). F12 "2. Zombies sounds"
        public static ConfigEntry<float> HumRangeSetting, JingleRangeSetting;
        public static float HumRange => HumRangeSetting?.Value ?? 7f;
        public static float JingleRange => JingleRangeSetting?.Value ?? 12f;
        private AudioSource _hum, _jingle, _sting, _oneShots;
        private string _perk;
        private float _nextJingle, _nextSpark;
        private bool _powered, _started;

        public void Init(string perk)
        {
            _perk = perk;
            _hum = Find("Audio_Hum", "perk_hum", true);
            _jingle = Find("Audio_Jingle", perk + "_jingle", false);
            _sting = Find("Audio_Sting", perk + "_sting", false);
            _oneShots = gameObject.AddComponent<AudioSource>();
            Set3D(_oneShots, JingleRange);
            _nextJingle = Time.time + Random.Range(10f, JingleMin);
            _nextSpark = Time.time + Random.Range(3f, 12f);
        }

        private bool Powered => ZombiesMatch.PowerIsOn || _perk == "revive" && ZombiesTeam.List().Count <= 1 && !ZombiesDowned.QuickReviveGone;

        private AudioSource Find(string name, string clip, bool loop)
        {
            AudioSource s = GetComponentsInChildren<AudioSource>(true).FirstOrDefault(a => a.name == name);
            if (s == null)
                return null;
            if (s.clip == null)
                s.clip = Beverages.Clip(clip);
            s.loop = loop;
            s.playOnAwake = false;
            s.enabled = true;
            Set3D(s, loop ? HumRange : JingleRange);
            return s;
        }

        private static void Set3D(AudioSource s, float range)
        {
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 1f;
            s.maxDistance = range;
            s.dopplerLevel = 0f;
        }

        private void Update()
        {
            if (!ZombiesMatch.Active)
                return;
            // Perk Beverages' sliders, live
            float master = ZombiesSounds.Master?.Value ?? 1f;
            if (_hum != null)
                _hum.volume = Beverages.Volume("HumVolume", 0.15f) * master;
            if (_jingle != null)
                _jingle.volume = Beverages.Volume("JingleVolume", 0.3f) * master;
            if (_sting != null)
                _sting.volume = Beverages.Volume("StingVolume", 0.5f) * master;

            bool powered = Powered;
            if (powered != _powered)
            {
                _powered = powered;
                if (powered)
                {
                    // The start-up (not for Quick Revive already on at the start, solo)
                    if (_started)
                        Shot("perk_power_on", Beverages.Volume("PowerOnVolume", 0.4f));
                    if (_hum != null && _hum.clip != null)
                        _hum.Play();
                    _nextJingle = Time.time + Random.Range(15f, 45f);
                }
                else if (_hum != null)
                    _hum.Stop();
            }
            _started = true;
            if (!powered)
            {
                // No power: a crackle now and then
                if (Time.time >= _nextSpark)
                {
                    _nextSpark = Time.time + Random.Range(6f, 20f);
                    if (Near())
                        Shot("perk_broken_" + Random.Range(0, 5), Beverages.Volume("SparkVolume", 0.25f));
                }
                return;
            }
            if (_jingle == null || _jingle.clip == null || _jingle.isPlaying || Time.time < _nextJingle)
                return;
            _nextJingle = Time.time + Random.Range(JingleMin, JingleMax);
            if (Near())
                MachineJingles.TryPlay(_jingle);
        }

        private bool Near()
        {
            Transform me = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer?.Transform?.Original;
            return me != null && Vector3.Distance(me.position, transform.position) <= JingleRange;
        }

        private void Shot(string clip, float volume)
        {
            AudioClip c = Beverages.Clip(clip);
            if (c != null && _oneShots != null && volume > 0f)
                _oneShots.PlayOneShot(c, volume * (ZombiesSounds.Master?.Value ?? 1f));
        }

        /// <summary>Bought: the jingle stops, the sting plays, the bottle drops out.</summary>
        public void Bought()
        {
            if (_jingle != null)
                _jingle.Stop();
            if (_sting != null && _sting.clip != null)
                _sting.Play();
            Shot("bottle_dispense", Beverages.Volume("PurchaseVolume", 0.5f));
            _nextJingle = Time.time + JingleMax;
        }
    }

    /// <summary>Perk Beverages (Kennerz's perk mod), read by reflection: its clips and its F12 volumes.</summary>
    internal static class Beverages
    {
        private static System.Type _plugin, _assets;
        private static bool _looked;

        private static void Look()
        {
            if (_looked)
                return;
            _looked = true;
            _plugin = AccessTools.TypeByName("PerkBeverages.PerkPlugin");
            _assets = AccessTools.TypeByName("PerkBeverages.PerkAssets");
        }

        public static AudioClip Clip(string name)
        {
            Look();
            try
            {
                return _assets?.GetMethod("Clip")?.Invoke(null, new object[] { name }) as AudioClip;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>One of Perk Beverages' volume settings (PerkPlugin.HumVolume...), or the fallback.</summary>
        public static float Volume(string field, float fallback)
        {
            Look();
            return _plugin != null && AccessTools.Field(_plugin, field)?.GetValue(null) is ConfigEntry<float> e ? e.Value : fallback;
        }
    }

    /// <summary>
    /// The machines' full jingles, shared out: one machine at a time (never two songs over each other) and rarely, a
    /// few minutes of quiet between any two (perk machines and Pack-a-Punch alike). A machine asks now and then when
    /// someone is near it; the first to ask once the quiet is over plays.
    /// </summary>
    internal static class MachineJingles
    {
        private const float QuietMin = 240f, QuietMax = 480f;
        private static AudioSource _playing;
        private static float _quietUntil = -1f;

        public static bool TryPlay(AudioSource jingle)
        {
            if (jingle == null || jingle.clip == null)
                return false;
            // The first quiet spell starts with the match
            if (_quietUntil < 0f || _quietUntil - Time.time > QuietMax + 600f)
                _quietUntil = Time.time + Random.Range(60f, QuietMin);
            if (_playing != null && _playing.isPlaying || Time.time < _quietUntil)
                return false;
            _playing = jingle;
            _quietUntil = Time.time + jingle.clip.length + Random.Range(QuietMin, QuietMax);
            jingle.Play();
            return true;
        }

        /// <summary>A new match: the quiet starts again.</summary>
        public static void Reset()
        {
            _playing = null;
            _quietUntil = -1f;
        }
    }
}
