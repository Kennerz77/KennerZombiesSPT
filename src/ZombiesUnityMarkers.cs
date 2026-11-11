using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Things placed in Unity work in SPT: any object in the map's scenes named <c>KZM_&lt;Kind&gt;[_&lt;what&gt;][_&lt;price&gt;][_power][_zoneN]</c>
    /// becomes a layout entry when a zombies match is built (never saved to the layout file), exactly as if it had been
    /// placed with F8. Kind is a MapEditor.Kind (PerkMachine, WallBuy, BoxSpot, PackAPunch, PowerSwitch, Window, Door, Debris,
    /// ZombieSpawn, Blocker, PowerLight, Lamp, Decal, Prop...). Examples: KZM_PerkMachine_jugg, KZM_WallBuy_m1a, KZM_Door_metal_rusted_750,
    /// KZM_Debris_concrete_rubble_1000_power, KZM_Window_zone2, KZM_Prop_Pallete_wood, KZM_Decal_blood_hand_01.
    /// The object's feet (pivot, at floor level) are the spot, its yaw is the facing (forward is out of a wall / the way a window
    /// looks outside), and for sized kinds (window, door, debris, blocker, decal) its scale is the size in metres. The marker itself
    /// is switched off in the match. A marker within 0.3 m of an entry of the same kind in the layout file is left out.
    /// </summary>
    internal static class ZombiesUnityMarkers
    {
        public const string Prefix = "KZM_";
        private static readonly Regex Zone = new Regex(@"^zone(\d+)$", RegexOptions.IgnoreCase);
        private static readonly HashSet<MapEditor.Kind> Sized = new HashSet<MapEditor.Kind>
        {
            MapEditor.Kind.Window, MapEditor.Kind.Door, MapEditor.Kind.Debris, MapEditor.Kind.Blocker, MapEditor.Kind.PlayAreaWall, MapEditor.Kind.Decal,
        };

        /// <summary>Adds the Unity markers to the map's entries; returns how many.</summary>
        public static int Adopt(MapEditor.MapFile map)
        {
            if (map == null)
                return 0;
            int added = 0;
            foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null || !t.name.StartsWith(Prefix, StringComparison.Ordinal))
                    continue;
                MapEditor.Entry e = Parse(t);
                if (e == null)
                    continue;
                Vector3 p = MapEditor.Pos(e);
                bool duplicate = map.Entries.Any(o => o.Type == e.Type && o.What == e.What && Vector3.Distance(MapEditor.Pos(o), p) < 0.3f);
                t.gameObject.SetActive(false);
                if (duplicate)
                    continue;
                map.Entries.Add(e);
                added++;
                ZombiesPlugin.Log.LogInfo($"Unity marker {t.name} at {p}: {e.Type} {e.What} (zone {e.Zone}, price {e.Price}{(e.OpensWithPower ? ", power" : "")})");
            }
            if (added > 0)
                ZombiesPlugin.Log.LogInfo($"Unity markers: {added} things from the scenes added to the layout");
            return added;
        }

        private static MapEditor.Entry Parse(Transform t)
        {
            string[] tokens = t.name.Substring(Prefix.Length).Split('_');
            // A copy suffix from Unity ("KZM_Window (1)") is ignored
            tokens = tokens.Select(s => Regex.Replace(s, @"\s*\(\d+\)$", "")).Where(s => s.Length > 0).ToArray();
            if (tokens.Length == 0 || !Enum.TryParse(tokens[0], true, out MapEditor.Kind kind))
            {
                ZombiesPlugin.Log.LogWarning($"Unity marker {t.name}: not a layout kind");
                return null;
            }
            var rest = tokens.Skip(1).ToList();
            var e = new MapEditor.Entry { Type = kind, What = "", Scale = 1f, Locked = true };
            for (int i = rest.Count - 1; i >= 0; i--)
            {
                Match z = Zone.Match(rest[i]);
                if (z.Success)
                {
                    e.Zone = int.Parse(z.Groups[1].Value, CultureInfo.InvariantCulture);
                    rest.RemoveAt(i);
                }
                else if (rest[i].Equals("power", StringComparison.OrdinalIgnoreCase))
                {
                    e.OpensWithPower = true;
                    rest.RemoveAt(i);
                }
                else if (rest[i].Equals("noboard", StringComparison.OrdinalIgnoreCase))
                {
                    e.NoBoard = true;
                    rest.RemoveAt(i);
                }
            }
            if ((kind == MapEditor.Kind.Door || kind == MapEditor.Kind.Debris || kind == MapEditor.Kind.WallBuy) && rest.Count > 0
                && int.TryParse(rest[rest.Count - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int price))
            {
                e.Price = price;
                rest.RemoveAt(rest.Count - 1);
            }
            e.What = string.Join("_", rest);
            if (kind == MapEditor.Kind.Prop && !e.What.StartsWith(AssetCatalog.ScenePrefix) && !e.What.StartsWith(AssetCatalog.ItemPrefix))
                e.What = AssetCatalog.ScenePrefix + e.What;
            if (kind == MapEditor.Kind.Zone)
            {
                ZombiesPlugin.Log.LogWarning($"Unity marker {t.name}: zones are outlined in F8 (layout \"Zones\"), not by markers");
                return null;
            }
            Vector3 position = t.position;
            e.Position = new[] { position.x, position.y, position.z };
            e.Turn = t.eulerAngles.y;
            e.Tilt = 0f;
            e.Roll = 0f;
            if (Sized.Contains(kind))
            {
                Vector3 s = t.lossyScale;
                e.Size = new[] { Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z) };
            }
            else
                e.Scale = Mathf.Abs(t.lossyScale.x) > 0.01f ? Mathf.Abs(t.lossyScale.x) : 1f;
            // A 115 rock stands as placed: tilted and rolled too, and stretched unevenly if its marker is
            if (kind == MapEditor.Kind.Rock)
            {
                Vector3 euler = t.eulerAngles;
                e.Tilt = euler.x;
                e.Roll = euler.z;
                Vector3 s = t.lossyScale;
                if (Mathf.Abs(s.y - s.x) > 0.01f || Mathf.Abs(s.z - s.x) > 0.01f)
                    e.Stretch = new[] { 1f, Mathf.Abs(s.y) / e.Scale, Mathf.Abs(s.z) / e.Scale };
            }
            return e;
        }
    }
}
