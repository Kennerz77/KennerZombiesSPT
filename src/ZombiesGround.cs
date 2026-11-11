using BepInEx.Configuration;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// How a zombies map is drawn:
    /// - its ground (Unity terrains): close up a terrain is drawn from its texture layers, further off from a "base map"
    ///   Unity bakes from them. The ripped Customs terrains have that distance at 0 (all base map), and the base map comes
    ///   out as one flat brown, so the ground had no texture at all. The layers are drawn all the way out instead (the
    ///   map is small and fogged, so it costs little).
    /// - its level of detail: the map is small, so objects keep their full model much further out before swapping to a
    ///   simpler one (no popping close by). Unity's LOD bias is raised (F12 "LOD distance x"), and EFT's own value is put
    ///   back when the match ends.
    /// Only on zombies maps; the terrains go with the raid's scenes.
    /// </summary>
    internal static class ZombiesGround
    {
        private const float LayersOutTo = 2000f;
        private static ConfigEntry<float> _lodDistance;
        private static float? _origLodBias;

        public static void Bind(ConfigFile config, string section)
        {
            _lodDistance = config.Bind(section, "LOD distance x", 2f, new ConfigDescription(
                "How much further out objects keep their full model in a zombies match (1 = the game's own). Higher stops close-by popping; it costs some frames.",
                new AcceptableValueRange<float>(1f, 10f)));
            _lodDistance.SettingChanged += (s, e) =>
            {
                if (_origLodBias.HasValue)
                    QualitySettings.lodBias = _origLodBias.Value * _lodDistance.Value;
            };
        }

        public static void Apply()
        {
            int n = 0;
            foreach (Terrain t in Terrain.activeTerrains)
            {
                if (t == null)
                    continue;
                // The rip's terrains have it at 0 (all base map); EFT's quality settings mustn't put it back
                t.ignoreQualitySettings = true;
                t.basemapDistance = LayersOutTo;
                n++;
            }
            if (!_origLodBias.HasValue)
                _origLodBias = QualitySettings.lodBias;
            QualitySettings.lodBias = _origLodBias.Value * (_lodDistance?.Value ?? 2f);
            ZombiesPlugin.Log.LogInfo($"Zombies ground: {n} terrains drawn from their layers out to {LayersOutTo} m; LOD bias {_origLodBias:0.##} -> {QualitySettings.lodBias:0.##}");
        }

        /// <summary>EFT's own LOD bias back (the match is over).</summary>
        public static void Restore()
        {
            if (!_origLodBias.HasValue)
                return;
            QualitySettings.lodBias = _origLodBias.Value;
            _origLodBias = null;
        }
    }
}
