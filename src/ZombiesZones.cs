using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's zones in a zombies match: the layout's Zone boxes (F8), numbered. Zones 1 and 2 are where players start and
    /// are open from the start; any other opens when a door or debris touching it is bought (or cleared by the power).
    /// Zombie spawns in a closed zone never spawn: that's how the game knows nobody has been there yet. ZoneAt tells
    /// which zone a point is in (0: none).
    /// </summary>
    internal static class ZombiesZones
    {
        private class Zone
        {
            public int Id;
            public Vector3 Centre;
            public Quaternion Rotation;
            public Vector3 Half;

            public bool Contains(Vector3 p, float margin = 0f)
            {
                Vector3 local = Quaternion.Inverse(Rotation) * (p - Centre);
                return Mathf.Abs(local.x) <= Half.x + margin && Mathf.Abs(local.y) <= Half.y + margin && Mathf.Abs(local.z) <= Half.z + margin;
            }
        }

        public static readonly int[] StartZones = { 1, 2 };
        private static readonly List<Zone> Zones = new List<Zone>();
        private static readonly HashSet<int> Open = new HashSet<int>();

        private static readonly List<MapEditor.ZoneShape> Shapes = new List<MapEditor.ZoneShape>();

        public static bool Any => Zones.Count > 0 || Shapes.Count > 0;

        public static void Build(IEnumerable<MapEditor.Entry> entries, IEnumerable<MapEditor.ZoneShape> shapes = null)
        {
            Zones.Clear();
            Shapes.Clear();
            Open.Clear();
            // Outlined zones (F8 "Zones"), as well as the older boxes
            if (shapes != null)
                Shapes.AddRange(shapes.Where(s => s != null && s.Points.Count >= 3));
            foreach (MapEditor.Entry e in entries.Where(e => e.Type == MapEditor.Kind.Zone && e.Size != null))
            {
                int.TryParse(e.What, out int id);
                Quaternion rot = MapEditor.Rotation(e);
                var half = new Vector3(e.Size[0], e.Size[1], e.Size[2]) * 0.5f;
                // A zone stands on its spot, like every sized thing
                Zones.Add(new Zone { Id = Mathf.Max(1, id), Rotation = rot, Half = half, Centre = MapEditor.Pos(e) + rot * Vector3.up * half.y });
            }
            foreach (int z in StartZones)
                Open.Add(z);
            ZombiesPlugin.Log.LogInfo($"Zombies zones: {Zones.Select(z => z.Id).Concat(Shapes.Select(s => s.Id)).Distinct().Count()} zones ({Shapes.Count} outlines, {Zones.Count} boxes), open: {string.Join(", ", Open)}");
        }

        /// <summary>The middle of a zone's first box (its floor height), or none.</summary>
        public static Vector3? Middle(int zone)
        {
            MapEditor.ZoneShape shape = Shapes.FirstOrDefault(s => s.Id == zone);
            if (shape != null)
                return shape.Middle;
            Zone z = Zones.FirstOrDefault(x => x.Id == zone);
            return z == null ? (Vector3?)null : z.Centre - z.Rotation * Vector3.up * z.Half.y;
        }

        /// <summary>The zone a point is in (the first box holding it), or 0.</summary>
        public static int ZoneAt(Vector3 p) => Shapes.FirstOrDefault(s => s.Contains(p))?.Id ?? Zones.FirstOrDefault(z => z.Contains(p))?.Id ?? 0;

        /// <summary>
        /// Open. Outside every zone (0) is open only on a map without zones; on a map with zones it's never open (a
        /// zombie spawn there would spawn before anyone has been near it).
        /// </summary>
        public static bool IsOpen(int zone) => zone == 0 ? !Any : Open.Contains(zone);

        public static bool IsOpenAt(Vector3 p) => IsOpen(ZoneAt(p));

        /// <summary>A door or debris at this place was bought or cleared: every zone it touches opens.</summary>
        public static void OpenAround(Vector3 p, float reach = 2.5f)
        {
            foreach (int id in Zones.Where(z => z.Contains(p, reach)).Select(z => z.Id).Concat(Shapes.Where(s => s.Contains(p, reach)).Select(s => s.Id)))
                if (Open.Add(id))
                    ZombiesPlugin.Log.LogInfo($"Zombies zones: zone {id} open");
        }
    }
}
