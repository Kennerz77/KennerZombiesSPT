using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace KennerZombies
{
    /// <summary>
    /// Walk-across links on the map's navmesh, added when a zombies match is built: &lt;map&gt;_navlinks.txt beside the
    /// layout, one link per line "x1 y1 z1 x2 y2 z2 [width] [oneway]" (both ends on the baked navmesh, two-way unless
    /// "oneway": then only first point -> second, e.g. a drop through a hole in the floor). For short gaps
    /// the bake leaves where pieces meet (zm_stronghold: plate ramp onto the lower container roof, and the ramp top
    /// onto the upper container roof on the way to window #8, 2026-11-08: a container's door end pokes through and
    /// the bake leaves a 1.2 m hole). Zombies path across a link like normal ground. Removed when the match clears.
    /// </summary>
    internal static class ZombiesNavLinks
    {
        private static readonly List<NavMeshLinkInstance> Added = new List<NavMeshLinkInstance>();

        /// <summary>The links in this match (their ends on the navmesh), for walking zombies across them (ZombiesRounds).</summary>
        public static readonly List<(Vector3 a, Vector3 b, bool oneWay)> Ends = new List<(Vector3, Vector3, bool)>();

        // The ledges (ends 0.6 m or more apart in height): their place in Added and what they were made from
        private static readonly List<(int index, NavMeshLinkData data)> Ledges = new List<(int, NavMeshLinkData)>();

        /// <summary>
        /// <paramref name="check"/> worked out with the ledges lifted off the navmesh for that moment, then put back (all on
        /// this frame: nothing else paths in between). The ledges are shortcuts down for zombies already inside chasing a
        /// player; a spawn whose only way to a player is over one doesn't count as leading there (user, 2026-11-17: zombies
        /// came from the still-closed upstairs from round 1 once the bridge ledges were in).
        /// </summary>
        public static bool WithoutLedges(System.Func<bool> check)
        {
            if (Ledges.Count == 0)
                return check();
            foreach (var (index, _) in Ledges)
                if (Added[index].valid)
                    NavMesh.RemoveLink(Added[index]);
            try
            {
                return check();
            }
            finally
            {
                foreach (var (index, data) in Ledges)
                    Added[index] = NavMesh.AddLink(data);
            }
        }

        public static void Add(string location)
        {
            Remove();
            Ends.Clear();
            string layout = MapEditor.FileFor(location);
            string file = string.IsNullOrEmpty(layout) ? null : Path.Combine(Path.GetDirectoryName(layout) ?? "", location + "_navlinks.txt");
            if (file == null || !File.Exists(file))
                return;
            int n = 0;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                float[] v = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : float.NaN).ToArray();
                if (v.Length < 6 || v.Take(6).Any(float.IsNaN))
                    continue;
                Vector3 a = new Vector3(v[0], v[1], v[2]), b = new Vector3(v[3], v[4], v[5]);
                // "oneway" anywhere after the numbers: only from the first point to the second (a drop through a hole
                // in the floor, zm_stronghold's basement 2026-11-08: zombies can fall down it but never climb back up)
                bool oneWay = line.IndexOf("oneway", System.StringComparison.OrdinalIgnoreCase) >= 0;
                // Each end snapped onto the navmesh it should join (a link off the navmesh joins nothing)
                bool okA = NavMesh.SamplePosition(a, out NavMeshHit ha, 1f, NavMesh.AllAreas);
                bool okB = NavMesh.SamplePosition(b, out NavMeshHit hb, 1f, NavMesh.AllAreas);
                if (!okA || !okB)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies nav links: {line}: {(okA ? "" : "start ")}{(okB ? "" : "end ")}not on the navmesh (within 1 m), skipped");
                    continue;
                }
                var data = new NavMeshLinkData
                {
                    startPosition = ha.position,
                    endPosition = hb.position,
                    width = v.Length > 6 && !float.IsNaN(v[6]) ? v[6] : 1f,
                    bidirectional = !oneWay,
                    costModifier = -1f,
                    area = 0,
                    agentTypeID = 0, // Humanoid, the agent the map is baked for
                };
                NavMeshLinkInstance link = NavMesh.AddLink(data);
                if (!link.valid)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies nav links: {line}: Unity refused the link");
                    continue;
                }
                if (Mathf.Abs(hb.position.y - ha.position.y) >= 0.6f)
                    Ledges.Add((Added.Count, data));
                Added.Add(link);
                Ends.Add((ha.position, hb.position, oneWay));
                n++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies nav links: {n} added from {Path.GetFileName(file)}");
        }

        public static void Remove()
        {
            foreach (NavMeshLinkInstance l in Added)
                if (l.valid)
                    NavMesh.RemoveLink(l);
            Added.Clear();
            Ledges.Clear();
            Ends.Clear();
        }
    }
}
