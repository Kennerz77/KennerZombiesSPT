using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;

namespace KennerZombies
{
    /// <summary>
    /// One set of match rules for everyone (user, 2026-11-09): in a team match the host (a Fika headless, or the
    /// leader's game) sends its gameplay settings (damage, rounds, drops, perks, stamina, hipfire, grenades...) and
    /// every player's game uses them for that match only. Their own cfg file is never written with them: saving is
    /// held off while they're in, their own values go back at the match's end, then the file is saved (so a volume
    /// changed mid-match still sticks). Personal settings stay each player's own: volumes and music, controls, the
    /// HUD layout, the main menu, logging, the map editor and the monkey's fit in the hand.
    /// </summary>
    internal static class ZombiesSettings
    {
        private static readonly HashSet<string> PersonalSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "0. Main menu", "2. Zombies sounds", ZombiesSounds.MusicSection, "3. Mystery box volume", "3. Zombies controls",
            "4. Map editor (dev)", "6. Scores on screen", "10. Zombies menu sounds", "19. Debug: logging",
            "22. Debug: all sounds", "21. Monkey in the hand",
        };

        // The player's own values of every setting the host's replaced, put back at the match's end
        private static readonly Dictionary<ConfigDefinition, string> Own = new Dictionary<ConfigDefinition, string>();
        private static bool _saveWas = true, _holding;

        private static ConfigFile Config => ZombiesPlugin.Instance?.Config;

        /// <summary>A setting that is the same for everyone in a match (not a volume, a key or a personal section).</summary>
        private static bool Shared(ConfigEntryBase e) =>
            e != null && !PersonalSections.Contains(e.Definition.Section)
            && e.SettingType != typeof(KeyboardShortcut)
            && e.Definition.Key.IndexOf("volume", StringComparison.OrdinalIgnoreCase) < 0
            && e.Definition.Key.IndexOf("music", StringComparison.OrdinalIgnoreCase) < 0;

        /// <summary>The host: its shared settings, written into its State message.</summary>
        public static void Write(BinaryWriter w)
        {
            var list = Config == null ? new List<ConfigEntryBase>() : Config.Keys.Select(k => Config[k]).Where(Shared).ToList();
            w.Write(list.Count);
            foreach (ConfigEntryBase e in list)
            {
                w.Write(e.Definition.Section);
                w.Write(e.Definition.Key);
                w.Write(e.GetSerializedValue() ?? "");
            }
        }

        /// <summary>A player: the host's settings, used for this match.</summary>
        public static void Read(BinaryReader r)
        {
            int n = r.ReadInt32();
            var got = new List<(string s, string k, string v)>(n);
            for (int i = 0; i < n; i++)
                got.Add((r.ReadString(), r.ReadString(), r.ReadString()));
            ConfigFile config = Config;
            if (config == null || ZombiesNet.IsHost)
                return;
            if (!_holding)
            {
                _saveWas = config.SaveOnConfigSet;
                config.SaveOnConfigSet = false;
                _holding = true;
            }
            int changed = 0, missing = 0;
            foreach (var (s, k, v) in got)
            {
                var def = new ConfigDefinition(s, k);
                if (!config.ContainsKey(def))
                {
                    missing++;
                    continue;
                }
                ConfigEntryBase e = config[def];
                if (!Shared(e))
                    continue;
                string mine = e.GetSerializedValue();
                if (mine == v)
                    continue;
                if (!Own.ContainsKey(def))
                    Own[def] = mine;
                try
                {
                    e.SetSerializedValue(v);
                    changed++;
                }
                catch (Exception ex)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies settings: couldn't use the host's {s} > {k} ({ex.Message})");
                }
            }
            ZombiesPlugin.Log.LogInfo($"Zombies settings: using the host's match rules ({got.Count} sent, {changed} differed from mine{(missing > 0 ? $", {missing} unknown here (other version?)" : "")}); my own come back at the end");
        }

        /// <summary>The match is over: the player's own values back, and the file saved as it was plus any personal change.</summary>
        public static void Restore()
        {
            ConfigFile config = Config;
            if (config == null || !_holding)
                return;
            foreach (var pair in Own)
                try
                {
                    config[pair.Key].SetSerializedValue(pair.Value);
                }
                catch
                {
                }
            int n = Own.Count;
            Own.Clear();
            _holding = false;
            config.SaveOnConfigSet = _saveWas;
            try
            {
                config.Save();
            }
            catch
            {
            }
            ZombiesPlugin.Log.LogInfo($"Zombies settings: my own match rules back ({n} put back)");
        }
    }
}
