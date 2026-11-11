using System;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Dev commands from a file, for testing a running match without clicking through F12 (Claude's live sessions):
    /// BepInEx/config/zm_dev.txt, read every half second in a match, each line run once, then the file is deleted.
    ///   give &lt;gun id&gt;                         a box gun into your hands (raygun, thundergun, m1911, ...)
    ///   weather &lt;clouds&gt; &lt;rain&gt; &lt;fog&gt; &lt;wind&gt;  the map weather held there
    ///   points &lt;n&gt;                           points added
    ///   cfg &lt;section&gt;|&lt;key&gt;|&lt;value&gt;          any of this mod's F12 settings set
    /// Nothing happens without the file.
    /// </summary>
    internal static class ZombiesDevFile
    {
        private static float _next;

        public static void Tick()
        {
#if RELEASE
            // Release builds: the map editor (F8) and the dev hooks are not there at all
            return;
#endif
            if (Time.unscaledTime < _next)
                return;
            _next = Time.unscaledTime + 0.5f;
            string file = Path.Combine(BepInEx.Paths.ConfigPath, "zm_dev.txt");
            if (!File.Exists(file))
                return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
                File.Delete(file);
            }
            catch
            {
                return; // still being written: next time
            }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                try
                {
                    Run(line);
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"[dev] {line}: {e.Message}");
                }
            }
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static void Run(string line)
        {
            string[] p = line.Split(new[] { ' ' }, 2);
            string cmd = p[0].ToLowerInvariant(), rest = p.Length > 1 ? p[1].Trim() : "";
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            switch (cmd)
            {
                case "give":
                    BoxGuns.Gun g = BoxGuns.FindGun(rest);
                    if (g == null || me == null)
                        throw new Exception("no such gun or no player");
                    ZombiesPlugin.Instance.StartCoroutine(BoxGive.Give(g, me));
                    break;
                case "weather":
                    float[] w = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(F).ToArray();
                    MapWeather.Hold(new MapWeather.Settings { Clouds = w[0], Rain = w[1], Fog = w[2], Wind = w[3] });
                    break;
                case "points":
                    ZombiesMatch.AddPoints(int.Parse(rest, CultureInfo.InvariantCulture));
                    break;
                case "cfg":
                    string[] c = rest.Split('|');
                    ConfigFile config = ZombiesPlugin.Instance.Config;
                    var def = config.Keys.FirstOrDefault(k => k.Section == c[0] && k.Key == c[1]);
                    if (def == null)
                        throw new Exception("no such setting");
                    ConfigEntryBase entry = config[def];
                    entry.BoxedValue = TomlTypeConverter.ConvertToValue(c[2], entry.SettingType);
                    break;
                default:
                    throw new Exception("unknown command");
            }
            ZombiesPlugin.Log.LogInfo($"[dev] {line}");
        }
    }
}
