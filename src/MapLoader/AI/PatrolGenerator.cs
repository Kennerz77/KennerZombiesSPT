using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace KennerZombies.MapLoader.AI
{
    // Patrol routes. BotZone.IsValid() requires at least one PatrolWay, and a bot with no route
    // stands at its spawn forever. Authored ways (PatrolWay + child PatrolPoints in the SDK) are
    // kept and snapped to the navmesh; zones without one get a generated way.
    //
    // Generation ported from an earlier PatrolScanner: a greedy chain of 2-6 reachable
    // points (retail's range) picked from the cover graph near the zone's spawn markers.
    internal static class PatrolGenerator
    {
        private const int MinPointsPerWay = 2;
        private const int MaxPointsPerWay = 6;
        private const float MinSpacing = 7f;
        private const float MaxSpacing = 45f;
        private const float SnapRadius = 3f;
        private const float ZonePad = 18f;
        private const int MaxCandidates = 70;
        private static readonly float[] RingRadii = { 8f, 16f, 26f };
        private const int RingSamples = 10;

        public static void Generate(AICoversData covers)
        {
            var library = new List<Vector3>();
            if (covers.Points != null)
                foreach (var p in covers.Points)
                    library.Add(p.Position);

            GameObject root = null;
            int kept = 0, generated = 0, failed = 0;
            foreach (var zone in Object.FindObjectsOfType<BotZone>())
            {
                if (zone == null || zone.SpawnPointMarkers == null || zone.SpawnPointMarkers.Count == 0)
                    continue;

                var authored = zone.PatrolWays?.Where(w => w != null && w.Points != null && w.Points.Count >= MinPointsPerWay).ToArray();
                if (authored != null && authored.Length > 0)
                {
                    foreach (var way in authored)
                        SnapToNavMesh(way);
                    zone.PatrolWays = authored;
                    kept += authored.Length;
                    continue;
                }

                var route = BuildRoute(zone, library);
                if (route.Count < MinPointsPerWay)
                {
                    failed++;
                    Loader.Log.LogWarning($"Bot zone '{zone.name}': only {route.Count} reachable patrol point(s), no patrol generated. " +
                                                "Bots in this zone will not move; add a PatrolWay in the SDK.");
                    continue;
                }

                if (root == null)
                    root = new GameObject("SDK_GeneratedPatrols");
                // the hierarchy is the data: EditorInitPoints builds Points from child transforms
                var wayGo = new GameObject($"Patrol_{zone.name}");
                wayGo.transform.SetParent(root.transform, false);
                wayGo.transform.position = route[0];
                var patrol = wayGo.AddComponent<PatrolWay>();
                patrol.PatrolType = PatrolType.patrolling;
                patrol.CoefSubPoints = 1f;
                foreach (var pos in route)
                {
                    var pointGo = new GameObject("PatrolPoint");
                    pointGo.transform.SetParent(wayGo.transform, false);
                    pointGo.transform.position = pos;
                    pointGo.AddComponent<PatrolPoint>().CanUseByBoss = true;
                }
                try
                {
                    patrol.EditorInitPoints(); // Points from children, ids
                    patrol.InitPoints();       // back references
                }
                catch (System.Exception e)
                {
                    failed++;
                    Loader.Log.LogWarning($"Bot zone '{zone.name}': patrol init failed: {e.Message}");
                    Object.Destroy(wayGo);
                    continue;
                }
                zone.PatrolWays = new[] { patrol };
                generated++;
            }
            Loader.Verbose($"[AI] patrols: {kept} authored way(s) kept, {generated} generated, {failed} zone(s) without a route");
        }

        // PatrollingData ignores the path status when walking to a point, so an unreachable point
        // reads as "walking" while the bot stands still. Pull authored points onto the navmesh.
        private static void SnapToNavMesh(PatrolWay way)
        {
            foreach (var point in way.Points)
            {
                if (point == null)
                    continue;
                if (NavMesh.SamplePosition(point.transform.position, out var hit, SnapRadius, NavMesh.AllAreas))
                    point.transform.position = hit.position;
                else
                    Loader.Log.LogWarning($"Patrol point {way.name}/{point.name} is more than {SnapRadius} m from the navmesh");
            }
        }

        private static List<Vector3> BuildRoute(BotZone zone, List<Vector3> library)
        {
            var seeds = zone.SpawnPointMarkers.Where(m => m != null).Select(m => m.transform.position).ToList();
            if (seeds.Count == 0)
                return new List<Vector3>();

            var centre = Vector3.zero;
            foreach (var s in seeds)
                centre += s;
            centre /= seeds.Count;
            float spread = seeds.Max(s => Vector3.Distance(s, centre));
            float reach = spread + ZonePad;

            var pool = new List<Vector3>(seeds);
            pool.AddRange(library.Where(p => (p - centre).sqrMagnitude <= reach * reach));
            if (pool.Count < 8)
                foreach (var r in RingRadii)
                    for (int i = 0; i < RingSamples; i++)
                    {
                        float a = i * Mathf.PI * 2f / RingSamples;
                        pool.Add(centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r);
                    }

            var snapped = new List<Vector3>();
            foreach (var c in pool.OrderBy(p => (p - centre).sqrMagnitude))
            {
                if (snapped.Count >= MaxCandidates)
                    break;
                if (!NavMesh.SamplePosition(c, out var hit, SnapRadius, NavMesh.AllAreas))
                    continue;
                if (snapped.Any(s => (s - hit.position).sqrMagnitude < 1f))
                    continue;
                snapped.Add(hit.position);
            }
            if (snapped.Count == 0)
                return new List<Vector3>();

            // greedy nearest reachable chain; an unreachable candidate is on another navmesh
            // island, so it is discarded for good
            var route = new List<Vector3> { snapped[0] };
            var used = new HashSet<int> { 0 };
            var path = new NavMeshPath();
            while (route.Count < MaxPointsPerWay)
            {
                var from = route[route.Count - 1];
                int best = -1;
                float bestD = float.MaxValue;
                for (int i = 0; i < snapped.Count; i++)
                {
                    if (used.Contains(i))
                        continue;
                    float d = Vector3.Distance(from, snapped[i]);
                    if (d < MinSpacing || d > MaxSpacing || d >= bestD)
                        continue;
                    if (!NavMesh.CalculatePath(from, snapped[i], NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    {
                        used.Add(i);
                        continue;
                    }
                    best = i;
                    bestD = d;
                }
                if (best < 0)
                    break;
                used.Add(best);
                route.Add(snapped[best]);
            }
            return route;
        }
    }
}
