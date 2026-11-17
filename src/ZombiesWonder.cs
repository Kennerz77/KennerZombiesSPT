using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KennerZombies
{
    /// <summary>
    /// Which wonder weapons a match's box holds (user, 2026-11-16). The Ray Gun is always in (the base one: every
    /// player may have one). The special ones (Thundergun, Ray Gun Mark II, Wunderwaffe: whichever mods are installed)
    /// are picked by the host, up to two, on the SOLO map screen or in the lobby; everyone in the lobby sees the picks
    /// on the map card. The host may switch on ANY NUMBER, which costs everyone 30% of the match's Dr. K points. A special one
    /// is one per team (the box won't give it while a teammate holds it), and nobody carries two wonder weapons.
    /// </summary>
    internal static class ZombiesWonder
    {
        public const int MaxPicks = 2;
        public const float AnyNumberCut = 0.3f;

        public class Special
        {
            public string Key, Name, Plugin, Template, Magazine;
            public bool Installed => BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Plugin);
        }

        /// <summary>Every special wonder weapon the core knows (shown only when its mod is installed).</summary>
        public static readonly Special[] All =
        {
            new Special { Key = "thundergun", Name = "Thundergun", Plugin = "com.kennerz.thundergun", Template = ZombiesAmmo.ThundergunId },
            new Special { Key = "raygunmk2", Name = "Ray Gun Mark II", Plugin = "com.kennerz.raygunmk2", Template = ZombiesAmmo.RaygunMk2Id,
                Magazine = "6a7f2b4e8d11530000000002" },
            new Special { Key = "wunderwaffe", Name = "Wunderwaffe", Plugin = "com.kennerz.wunderwaffe", Template = ZombiesAmmo.WunderwaffeId,
                Magazine = "6a7f4d6f1b11550000000002" },
        };

        public static List<Special> Installed => All.Where(s => s.Installed).ToList();

        public static bool IsSpecial(string template) => template != null && All.Any(s => s.Template == template);

        public static Special ByKey(string key) => All.FirstOrDefault(s => s.Key == key);

        // ---------- This player's own picks (used when solo or hosting), kept between games ----------

        private static List<string> _picks;
        private static bool _any;

        private static string FilePath => Path.Combine(ZombiesPlugin.Dir, "stats", "wonder_picks.txt");

        private static void Load()
        {
            if (_picks != null)
                return;
            _picks = new List<string>();
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] lines = File.ReadAllLines(FilePath);
                    _any = lines.Any(l => l.Trim() == "any");
                    _picks = lines.Select(l => l.Trim()).Where(l => ByKey(l) != null).Distinct().ToList();
                }
                else
                    // First time: the first two installed
                    _picks = Installed.Take(MaxPicks).Select(s => s.Key).ToList();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Wonder weapons: couldn't read the picks ({e.Message})");
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, _picks.Concat(_any ? new[] { "any" } : new string[0]));
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Wonder weapons: couldn't save the picks ({e.Message})");
            }
        }

        /// <summary>This player's picks, installed ones only.</summary>
        public static List<string> MyPicks
        {
            get
            {
                Load();
                return _picks.Where(k => ByKey(k)?.Installed == true).ToList();
            }
        }

        public static bool MyAny
        {
            get
            {
                Load();
                return _any;
            }
        }

        /// <summary>Ticks or unticks one; false (with a toast) when two are already picked and ANY NUMBER is off.</summary>
        public static bool Toggle(string key)
        {
            Load();
            if (_picks.Remove(key))
            {
                Save();
                return true;
            }
            if (!_any && MyPicks.Count >= MaxPicks)
            {
                ZombiesParty.Toast($"Pick up to {MaxPicks} wonder weapons (or switch on ANY NUMBER).");
                return false;
            }
            _picks.Add(key);
            Save();
            return true;
        }

        public static void SetAny(bool any)
        {
            Load();
            _any = any;
            // Back to the limit: the last ones ticked go
            if (!any)
                _picks = MyPicks.Take(MaxPicks).ToList();
            Save();
        }

        // ---------- What counts right now ----------

        /// <summary>A lobby member who isn't its host sees (and plays with) the host's picks.</summary>
        private static bool FromHost => ZombiesParty.Mine != null && !ZombiesParty.IsHost && !ZombiesParty.Solo;

        /// <summary>The picks shown now: the host's in someone else's lobby, else this player's own.</summary>
        public static List<string> ShownPicks => FromHost ? (ZombiesParty.Mine.Wonder ?? new List<string>()) : MyPicks;

        public static bool ShownAny => FromHost ? ZombiesParty.Mine.WonderAny : MyAny;

        // Taken when the match is built (ZombiesMatch), so a party that changes or goes away later changes nothing
        private static List<string> _matchPicks;
        private static bool _matchAny;

        public static void NewMatch()
        {
            _matchPicks = ShownPicks.ToList();
            _matchAny = ShownAny;
            ZombiesPlugin.Log.LogInfo($"Wonder weapons this match: Ray Gun + {(_matchAny ? "every one installed (ANY NUMBER: -30% Dr. K points)" : _matchPicks.Count == 0 ? "none" : string.Join(", ", _matchPicks.Select(k => ByKey(k)?.Name ?? k)))}");
        }

        private static List<string> MatchPicks => _matchPicks ?? ShownPicks;

        /// <summary>ANY NUMBER was on for this match (its points are cut).</summary>
        public static bool MatchAny => _matchPicks != null ? _matchAny : ShownAny;

        /// <summary>A special wonder weapon this match's box doesn't hold (not picked).</summary>
        public static bool LeftOut(string template)
        {
            if (!IsSpecial(template) || MatchAny)
                return false;
            List<string> picks = MatchPicks;
            return !All.Any(s => s.Template == template && picks.Contains(s.Key));
        }

        /// <summary>The words for the lobby card and the SOLO screen: "Ray Gun, Thundergun, Wunderwaffe".</summary>
        public static string Describe(List<string> picks, bool any)
        {
            List<string> names = new List<string> { "Ray Gun" };
            if (any)
                names.AddRange(Installed.Select(s => s.Name));
            else
                names.AddRange(picks.Select(k => ByKey(k)?.Name).Where(n => n != null));
            return string.Join(", ", names);
        }

        // ---------- Into the box ----------

        /// <summary>The Mark II and the Wunderwaffe (with their magazines) into the box when their mods are installed.</summary>
        public static void RegisterInstalled()
        {
            foreach (Special s in All)
                if (s.Magazine != null && s.Installed)
                    ZombiesApi.RegisterBoxWeapon(s.Template, s.Name, true, s.Magazine);
        }
    }
}
