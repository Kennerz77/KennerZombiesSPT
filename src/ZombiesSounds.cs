using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace KennerZombies
{
    /// <summary>
    /// The zombies mode's own the original sounds (sounds\*.ogg beside the DLL, from the game's banks with our sound bank extractor):
    /// loaded once at startup, played in the head (2D: the cha-ching, the deny) or at a place (3D: a board slammed back
    /// on, a board torn off). A name with "#" picks one of its numbered takes at random ("zm_board_slam_#": 0-5).
    /// </summary>
    internal static class ZombiesSounds
    {
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();

        // ---------- Volumes (F12 "2. Zombies sounds") ----------

        public enum Group { Power, Doors, Barricades, Zombies, PowerUps, Purchases, PackAPunch, Widow, Lore, Ambience, Rounds, Loading, GameOver, EasterEgg, FireSale, Monkey }

        /// <summary>F12's music section: every song and music track of the mode in one place (the user, 2026-11-08).</summary>
        public const string MusicSection = "2b. Zombies music";

        public static ConfigEntry<float> Master;
        private static readonly Dictionary<Group, ConfigEntry<float>> Groups = new Dictionary<Group, ConfigEntry<float>>();

        public static void Bind(ConfigFile config)
        {
            const string section = "2. Zombies sounds";
            ConfigDescription Range(string text, int order) => new ConfigDescription(text, new AcceptableValueRange<float>(0f, 1f),
                new ZombiesPlugin.ConfigurationManagerAttributes { Order = order });
            Master = config.Bind(section, "All zombies sounds", 1f, Range("Every zombies mode sound together (the box has its own section, the perk machines follow Perk Beverages' own volumes).", 100));
            ZombiesMachineAudio.HumRangeSetting = config.Bind(section, "Machine hum range (m)", 7f, new ConfigDescription(
                "How far a perk machine's and Pack-a-Punch's hum carries.", new AcceptableValueRange<float>(2f, 30f),
                new ZombiesPlugin.ConfigurationManagerAttributes { Order = 95 }));
            ZombiesMachineAudio.JingleRangeSetting = config.Bind(section, "Machine jingle range (m)", 12f, new ConfigDescription(
                "How far a perk machine's jingle, sting and sparks carry (and how near you must be for a jingle to play at all).",
                new AcceptableValueRange<float>(3f, 40f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 94 }));
            Groups[Group.Power] = config.Bind(section, "Power switch", 0.35f, Range("The power coming on: the switch, the surge and the lights' hum.", 99));
            Groups[Group.Doors] = config.Bind(section, "Doors and debris", 0.497f, Range("Doors sliding open and debris clearing, with the lightning and slam.", 98));
            Groups[Group.Barricades] = config.Bind(section, "Barricades", 0.8f, Range("Boards torn off and slammed back on.", 97));
            Groups[Group.Zombies] = config.Bind(section, "Zombies", 0.848f, Range("Zombies rising out of the dirt.", 96));
            Groups[Group.PowerUps] = config.Bind(section, "Power-ups", 0.496f, Range("Power-ups: the hum, the grab, the announcer.", 95));
            Groups[Group.Purchases] = config.Bind(section, "Purchases", 0.702f, Range("The cha-ching and the wall buy's gun sliding out.", 94));
            Groups[Group.PackAPunch] = config.Bind(section, "Pack-a-Punch", 0.7f, Range("Pack-a-Punch's machine: jingle, crunch, process, sting.", 93));
            Groups[Group.Widow] = config.Bind(section, "Widows Wine", 0.8f, Range("The web bursting and the cocoon.", 92));
            Groups[Group.Lore] = config.Bind(section, "Lore lightning", 0.402f, Range("The placed lightning's crackle and thunder.", 91));
            // The music, its own section (each also follows "All zombies sounds"); a value the player had set in
            // "2. Zombies sounds" before it moved is carried over
            const string music = MusicSection;
            Groups[Group.Loading] = Moved(config, music, "Loading screen music", 0.097f, Range("The music on the loading screen before the match.", 90), "Loading music");
            Groups[Group.Rounds] = Moved(config, music, "Round start and end music", 0.298f, Range("The round start and round end music.", 89), "Round music");
            Groups[Group.GameOver] = Moved(config, music, "Game over song", 0.15f, Range("The song under the game over and its camera shots.", 88), "Round music");
            Groups[Group.EasterEgg] = config.Bind(music, "Easter egg song", 0.154f, Range("The map's easter egg song once every 115 rock is found.", 87));
            Groups[Group.FireSale] = Moved(config, music, "Fire Sale music", 0.846f, Range("The Fire Sale's tune at the box while it lasts.", 86), "Power-ups");
            Groups[Group.Monkey] = Moved(config, music, "Cymbal Monkey song", 0.496f, Range("The monkey's song while it plays on the ground.", 85), "Power-ups");
            Groups[Group.Ambience] = Moved(config, music, "Map ambience", 0.152f, Range("The map's own ambience track, looped quietly under the whole match.", 84), "Map ambience");

            // Every sound the mode has, to listen to one by one (filter by name; plays in your head at its group's volume)
            config.Bind("22. Debug: all sounds", "Sound list", false, new ConfigDescription(
                "Every zombies sound (sounds\\zm_*.wav) and the mystery box's own: type to filter, click to play.", null,
                new ZombiesPlugin.ConfigurationManagerAttributes { HideDefaultButton = true, HideSettingName = true, CustomDrawer = DrawList }));
        }

        /// <summary>A playing song kept at its F12 slider every frame (moving the slider mid-song changes it at once).</summary>
        public static void Follow(AudioSource source, Group group, float scale = 1f)
        {
            if (source == null)
                return;
            source.volume = scale * Volume(group);
            var f = source.gameObject.AddComponent<FollowVolume>();
            f.Source = source;
            f.Group = group;
            f.Scale = scale;
        }

        internal class FollowVolume : MonoBehaviour
        {
            public AudioSource Source;
            public Group Group;
            public float Scale = 1f;

            private void Update()
            {
                if (Source != null)
                    Source.volume = Scale * Volume(Group);
            }
        }

        // A slider moved here from "2. Zombies sounds": the value the player had there, if any and this one is new
        private static ConfigEntry<float> Moved(ConfigFile config, string section, string key, float value, ConfigDescription d, string oldKey)
        {
            Dictionary<ConfigDefinition, string> orphans = null;
            bool inFile = false;
            try
            {
                orphans = HarmonyLib.AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config) as Dictionary<ConfigDefinition, string>;
                inFile = orphans != null && orphans.ContainsKey(new ConfigDefinition(section, key));
            }
            catch
            {
            }
            ConfigEntry<float> e = config.Bind(section, key, value, d);
            try
            {
                // Only the first time (the new slider not in the file yet)
                if (!inFile && orphans != null && orphans.TryGetValue(new ConfigDefinition("2. Zombies sounds", oldKey), out string old)
                    && float.TryParse(old, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                    e.Value = Mathf.Clamp01(v);
            }
            catch
            {
            }
            return e;
        }

        private static string _filter = "";
        private static Vector2 _scroll;
        private static AudioSource _tester;

        private static void DrawList(ConfigEntryBase _)
        {
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Filter", GUILayout.Width(50));
            _filter = GUILayout.TextField(_filter ?? "", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Stop", GUILayout.Width(60)) && _tester != null)
                _tester.Stop();
            GUILayout.EndHorizontal();
            var names = new List<(string name, Func<AudioClip> clip)>();
            foreach (string n in Clips.Keys.OrderBy(k => k))
            {
                string key = n;
                names.Add((key, () => Clips.TryGetValue(key, out AudioClip c) ? ZombiesWav.Fresh(c) : null));
            }
            GameObject box = ZombiesAssets.Box();
            if (box != null)
                foreach (AudioSource a in box.GetComponentsInChildren<AudioSource>(true).Where(a => a.clip != null).OrderBy(a => a.name))
                {
                    AudioSource src = a;
                    names.Add(("box: " + src.name.Replace("Audio_", ""), () => src.clip));
                }
            string f = (_filter ?? "").Trim().ToLowerInvariant();
            var shown = names.Where(n => f.Length == 0 || n.name.ToLowerInvariant().Contains(f)).ToList();
            GUILayout.Label($"{shown.Count} of {names.Count} sounds{(Clips.Count == 0 ? " (they load once a match starts)" : "")}");
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(320));
            foreach (var (name, clip) in shown)
                if (GUILayout.Button(name))
                    Test(name, clip());
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private static void Test(string name, AudioClip clip)
        {
            if (clip == null)
            {
                ZombiesPlugin.Log.LogInfo($"Sound test: {name} has no clip");
                return;
            }
            if (_tester == null)
            {
                var go = new GameObject("Zombies_SoundTest");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _tester = go.AddComponent<AudioSource>();
                _tester.playOnAwake = false;
                _tester.spatialBlend = 0f;
            }
            _tester.Stop();
            _tester.clip = clip;
            _tester.volume = name.StartsWith("box: ") ? 1f : Volume(GroupOf(name));
            _tester.Play();
            ZombiesPlugin.Log.LogInfo($"Sound test: {name} ({clip.length:0.0} s)");
        }

        /// <summary>This group's volume times the master (1 before the config is bound).</summary>
        public static float Volume(Group g) => (Master?.Value ?? 1f) * (Groups.TryGetValue(g, out ConfigEntry<float> e) ? e.Value : 1f);

        // Which group a sound is in, by its name
        private static Group GroupOf(string name)
        {
            if (name.StartsWith("zm_power"))
                return Group.Power;
            if (name.StartsWith("zm_door") || name.StartsWith("zm_debris"))
                return Group.Doors;
            if (name.StartsWith("zm_board"))
                return Group.Barricades;
            if (name.StartsWith("zm_pu_"))
                return Group.PowerUps;
            if (name.StartsWith("zm_ww_"))
                return Group.Widow;
            if (name.StartsWith("zm_lightning"))
                return Group.Lore;
            if (name.StartsWith("zm_cha_ching") || name.StartsWith("zm_purchase") || name.StartsWith("zm_wallbuy") || name.StartsWith("zm_deny"))
                return Group.Purchases;
            return Group.Zombies;
        }
        private static AudioSource _head;
        private static bool _loading;

        public static void Preload(MonoBehaviour host)
        {
            if (_loading)
                return;
            _loading = true;
            host.StartCoroutine(LoadAll());
        }

        private static IEnumerator LoadAll()
        {
            string dir = Path.Combine(ZombiesPlugin.Dir, "sounds");
            if (!Directory.Exists(dir))
                yield break;
            // WAV, read ourselves (ZombiesWav): clips from .ogg through Unity's web request came out empty in raids
            float seconds = 0f;
            foreach (string file in Directory.GetFiles(dir, "zm_*.wav"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                AudioClip clip = ZombiesWav.Load(dir, name);
                if (clip != null)
                {
                    Clips[name] = clip;
                    seconds += clip.length;
                }
                yield return null;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies sounds: {Clips.Count} loaded ({seconds:0} s of sound in all)");
        }

        private static AudioClip Pick(string name)
        {
            if (!name.Contains("#"))
                return Clips.TryGetValue(name, out AudioClip c) ? ZombiesWav.Fresh(c) : null;
            var takes = new List<AudioClip>();
            for (int i = 0; i < 10; i++)
                if (Clips.TryGetValue(name.Replace("#", i.ToString()), out AudioClip c))
                    takes.Add(ZombiesWav.Fresh(c));
            return takes.Count == 0 ? null : takes[UnityEngine.Random.Range(0, takes.Count)];
        }

        /// <summary>A loaded clip by name (for a looping source of its own), or null.</summary>
        public static AudioClip Clip(string name) => Pick(name);

        /// <summary>In the player's head (2D).</summary>
        public static void Head(string name, float volume = 1f)
        {
            AudioClip clip = Pick(name);
            if (clip == null)
                return;
            if (_head == null)
            {
                var go = new GameObject("Zombies_HeadSounds");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _head = go.AddComponent<AudioSource>();
                ZombiesHeadAudio.Setup(_head, 24);
                _head.playOnAwake = false;
            }
            _head.PlayOneShot(clip, volume * Volume(GroupOf(name)));
            ZombiesHeadAudio.Report(_head, name);
        }

        /// <summary>At a place in the world (3D).</summary>
        public static void At(string name, Vector3 position, float volume = 1f, float range = 25f)
        {
            AudioClip clip = Pick(name);
            if (clip == null)
                return;
            var go = new GameObject("Zombies_Sound");
            go.transform.position = position;
            AudioSource s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 1.5f;
            s.maxDistance = range;
            s.volume = volume * Volume(GroupOf(name));
            s.Play();
            UnityEngine.Object.Destroy(go, clip.length + 0.1f);
        }

        /// <summary>Riding on a moving thing (3D, follows it until the clip ends or it's gone). The source, or null.</summary>
        public static AudioSource On(string name, Transform follow, float volume = 1f, float range = 25f)
        {
            AudioClip clip = Pick(name);
            if (clip == null || follow == null)
                return null;
            var go = new GameObject("Zombies_Sound");
            go.transform.SetParent(follow, false);
            AudioSource s = go.AddComponent<AudioSource>();
            s.clip = clip;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 1.5f;
            s.maxDistance = range;
            s.volume = volume * Volume(GroupOf(name));
            s.Play();
            UnityEngine.Object.Destroy(go, clip.length + 0.1f);
            return s;
        }
    }
}
