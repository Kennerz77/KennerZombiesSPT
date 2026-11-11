using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace KennerZombies
{
    /// <summary>
    /// The installed zombies maps: every layout file (&lt;location&gt;zombies.json in a map mod's maps\ or the core's,
    /// MapEditor.MapsDirs) marked "ZombiesMap": true. A map download (Kennerz_ZM_&lt;Map&gt;) ships its layout file, so a new map shows on the map select's Zombies page, and gets the moonlight
    /// and its power lights, without any change here. Layouts without the mark (a dev layout over Customs) stay normal maps.
    /// </summary>
    internal static class ZombiesMaps
    {
        private static Dictionary<string, MapEditor.MapFile> _maps;

        public static IEnumerable<MapEditor.MapFile> All => Maps.Values;

        public static bool Is(string location) => location != null && Maps.ContainsKey(location);

        /// <summary>The zombies map's settings, or null when the location isn't one.</summary>
        public static MapEditor.MapFile Get(string location) => location != null && Maps.TryGetValue(location, out MapEditor.MapFile m) ? m : null;

        /// <summary>Read again next time (a layout was saved).</summary>
        public static void Forget() => _maps = null;

        private static Dictionary<string, MapEditor.MapFile> Maps
        {
            get
            {
                if (_maps != null)
                    return _maps;
                _maps = new Dictionary<string, MapEditor.MapFile>(StringComparer.OrdinalIgnoreCase);
                foreach (string dir in MapEditor.MapsDirs())
                foreach (string file in Directory.Exists(dir) ? Directory.GetFiles(dir, "*zombies.json") : new string[0])
                {
                    try
                    {
                        MapEditor.MapFile map = JsonConvert.DeserializeObject<MapEditor.MapFile>(File.ReadAllText(file));
                        // A map mod's own copy wins over one in the core's folder
                        if (map != null && map.ZombiesMap && !string.IsNullOrEmpty(map.Map) && !_maps.ContainsKey(map.Map))
                            _maps[map.Map] = map;
                    }
                    catch (Exception e)
                    {
                        ZombiesPlugin.Log.LogError($"Zombies maps: couldn't read {file}: {e.Message}");
                    }
                }
                ZombiesPlugin.Log.LogInfo($"Zombies maps: {string.Join(", ", _maps.Keys)}");
                return _maps;
            }
        }
    }
}
