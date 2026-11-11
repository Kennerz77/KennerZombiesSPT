using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// A blue 115 rock placed in F8 (PaP's hideout rock, its gem turned 115 blue; zombies.bundle zm_rock_115). It glows
    /// and hums with PaP's own rock glow. An Easter egg rock works as in PaP's hideout: look at it and pick "Investigate",
    /// the pickup sound plays and it falls silent; once every Easter egg rock on the map is investigated, the original's "115"
    /// plays for everyone. A lore rock only glows and hums. Found rocks are the team's (ZombiesNet); a new match starts
    /// them over.
    /// </summary>
    internal class ZombiesRock : MonoBehaviour
    {
        private const float InteractDistance = 2.5f;
        private static readonly List<ZombiesRock> All = new List<ZombiesRock>();
        private static readonly HashSet<int> Found = new HashSet<int>();
        private static bool _songPlayed;
        private static AudioSource _song;

        /// <summary>The easter egg song is playing right now (the exfil's music waits for it).</summary>
        public static bool SongPlaying => _song != null && _song.isPlaying;

        /// <summary>Stops the easter egg song (the game over's own music takes over).</summary>
        public static void StopSong()
        {
            if (_song != null)
            {
                if (_song.isPlaying)
                    ZombiesPlugin.Log.LogInfo("Zombies rock: the easter egg song stopped (game over: its own music plays)");
                _song.Stop();
            }
        }

        private readonly ZombiesInteraction _menu = new ZombiesInteraction();
        private int _entry = -1;
        private bool _easterEgg;

        public void Init(MapEditor.Entry e)
        {
            _entry = ZombiesMatch.EntryIndex(e);
            _easterEgg = e.What != "lore";
        }

        private void OnEnable() => All.Add(this);

        private void OnDestroy()
        {
            All.Remove(this);
            _menu.Reset();
        }

        private void Update()
        {
            if (!_easterEgg || Found.Contains(_entry))
                return;
            if (ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
        }

        private IEnumerable<(string, Action)> Options()
        {
            yield return (ZombiesHud.Hidden + "Investigate", Investigate);
        }

        private void Investigate()
        {
            if (Find(_entry))
                ZombiesNet.RockFound(_entry);
        }

        /// <summary>A teammate investigated one.</summary>
        public static void FoundRemote(int entry) => Find(entry);

        private static bool Find(int entry)
        {
            if (!Found.Add(entry))
                return false;
            ZombiesRock rock = All.FirstOrDefault(r => r != null && r._entry == entry);
            if (rock != null)
            {
                rock._menu.Reset();
                AudioClip pickup = PaPClip("pap_rock_pickup");
                if (pickup != null)
                    AudioSource.PlayClipAtPoint(pickup, rock.transform.position, PaPVolume("RockPickupVolume", 1f));
                Silence(rock.gameObject);
            }
            var eggs = All.Where(r => r != null && r._easterEgg).ToList();
            ZombiesPlugin.Log.LogInfo($"Zombies: 115 rock investigated ({eggs.Count(r => Found.Contains(r._entry))}/{eggs.Count})");
            if (!_songPlayed && eggs.Count > 0 && eggs.All(r => Found.Contains(r._entry)))
                StartSong();
            return true;
        }

        // Every rock found: the map's own easter egg song (<map>_eesong.wav beside its layout, e.g. Stronghold's), else
        // The original's "115", for the whole map
        private static void StartSong()
        {
            AudioClip clip = MapSong() ?? PaPClip("pap_115_song");
            if (clip == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies: no easter egg song (no <map>_eesong.wav, and the 115 song isn't in PaP's bundle).");
                return;
            }
            _songPlayed = true;
            var go = new GameObject("Zombies_115Song (KennerZombies)");
            _song = go.AddComponent<AudioSource>();
            _song.clip = clip;
            ZombiesHeadAudio.Setup(_song, 16);
            ZombiesSounds.Follow(_song, ZombiesSounds.Group.EasterEgg); // F12 "2b. Zombies music", live
            _song.Play();
            Destroy(go, clip.length + 1f);
            ZombiesPlugin.Log.LogInfo($"Zombies: every 115 rock investigated, the song plays ({clip.name}, {clip.length:0} s)");
        }

        /// <summary>A new match: the rocks hum again and the song can play again.</summary>
        public static void Clear()
        {
            Found.Clear();
            _songPlayed = false;
            if (_song != null)
                Destroy(_song.gameObject);
            _song = null;
        }

        // ---------- PaP's rock glow, hum and sounds (PaP is a dependency; its types are internal) ----------

        private static Type GlowType => AccessTools.TypeByName("PaPHideoutModule.PaPRockGlow");

        /// <summary>PaP's rock glow on a placed rock: the gem breathes, its light pulses, it hums (hideout volume).</summary>
        public static void Glow(GameObject model, bool hum = true)
        {
            Type type = GlowType;
            if (model == null || type == null)
                return;
            try
            {
                Component glow = model.AddComponent(type);
                type.GetProperty("InHideout")?.SetValue(glow, true);
                type.GetMethod("SetLive")?.Invoke(glow, new object[] { true, 1f });
                // Only the easter egg rocks hum; the lore rocks just glow
                if (!hum)
                    type.GetProperty("Silenced")?.SetValue(glow, true);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: 115 rock glow failed: {e.Message}");
            }
        }

        private static void Silence(GameObject rock)
        {
            Type type = GlowType;
            if (type == null)
                return;
            foreach (Component glow in rock.GetComponentsInChildren(type, true))
                type.GetProperty("Silenced")?.SetValue(glow, true);
        }

        private static AudioClip MapSong()
        {
            string location = ZombiesMatch.LocationHere;
            string layout = location == null ? null : MapEditor.FileFor(location);
            string dir = string.IsNullOrEmpty(layout) ? null : System.IO.Path.GetDirectoryName(layout);
            return dir != null && System.IO.File.Exists(System.IO.Path.Combine(dir, location + "_eesong.wav")) ? ZombiesWav.Load(dir, location + "_eesong") : null;
        }

        private static AudioClip PaPClip(string name) =>
            AccessTools.Method(AccessTools.TypeByName("PaPHideoutModule.PaPAssets"), "Clip")?.Invoke(null, new object[] { name }) as AudioClip;

        // PaP's own F12 volume for it, so the two match
        private static float PaPVolume(string field, float fallback) =>
            AccessTools.Field(AccessTools.TypeByName("PaPHideoutModule.PaPHideoutModulePlugin"), field)?.GetValue(null) is ConfigEntry<float> entry
                ? entry.Value
                : fallback;
    }
}
