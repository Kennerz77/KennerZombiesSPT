using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace KennerZombies.MapLoader.AI
{
    // Generates the game's own shape of cover data from the live navmesh and collision geometry: GroupPoints
    // along walls, GroupPointWays between them along real navmesh paths, and AICorePoints as a
    // spaced skeleton whose connection groups are the navmesh islands.
    //
    // Ported from an earlier CoverScanner. Its parity notes with the game's baked data:
    //  - defence scoring and look-side/tilt/light fields come from the game's own GroupPoint methods
    //  - raycasts use LayersMaskController.HighPolyWithTerrainMask, like AIUtility
    //  - dedup uses CoverPointCreatorPreset's cluster constants
    //  - wallDirection is horizontal and unit length; firePosition = position + 1.272 up
    internal static class CoverGenerator
    {
        // sampling
        private const float SampleStep = 0.75f;
        private const int RayDirections = 16;
        private const float DetectDist = 1.3f;
        private const float DetectHeight = 1.0f;   // must block a crouched torso to count as cover
        private const float StayHeight = 1.5f;     // also blocks a standing torso -> CoverLevel.Stay
        private const float StandOff = 0.45f;
        // dedup (CoverPointCreatorPreset)
        private const float ClusterNearDist = 0.4f;
        private const float ClusterLargeDist = 0.9f;
        private const float ClusterLargeAngleCos = 0.6428f; // cos 50°
        // ways graph: baked data has mean degree ~4, ways mostly < 8 m
        private const float NeighborRadius = 6f;
        private const int MaxDegree = 4;
        private const float BridgeRadius = 40f;
        private const int MinComponentSize = 20;
        private const float CoreSpacing = 15f;

        private sealed class GenPoint
        {
            public int Id;
            public Vector3 Pos;
            public Vector3 WallDir;
            public bool Tall;
            public bool Indoor;
            public readonly List<int> WayIds = new List<int>();
        }

        private struct GenWay
        {
            public int Id, From, To;
            public float Dist;
        }

        public static void Generate(AICoversData covers)
        {
            var sw = Stopwatch.StartNew();
            var gen = ScanCandidates();
            if (gen.Count < MinComponentSize)
            {
                // an empty graph leaves every voxel without a search seed and bots never activate
                Loader.Log.LogWarning($"Only {gen.Count} cover point(s) found along the navmesh; adding open-ground points. " +
                                            "Check that walls use the HighPolyCollider or Terrain layer.");
                gen.AddRange(OpenGroundPoints(gen.Count));
            }
            var ways = BuildWays(gen);
            BridgeAndPrune(ref gen, ways);
            Loader.Verbose($"[AI] {gen.Count} cover points, {ways.Count} ways after bridge/prune ({sw.ElapsedMilliseconds} ms)");

            var cores = SynthesizeCores(gen, ways);
            covers.AICorePointsHolder.CorePoints = cores;

            var points = BuildGroupPoints(gen, cores);
            var byId = new Dictionary<int, GroupPoint>(points.Count);
            foreach (var p in points)
                byId[p.Id] = p;
            var groupWays = new List<GroupPointWay>(ways.Count);
            foreach (var w in ways)
            {
                // RestoreData normally rewires Target from IdTarget; set both
                var way = new GroupPointWay(w.Id, byId[w.To], w.Dist);
                way.IdTarget = w.To;
                groupWays.Add(way);
            }
            covers.Points = points;
            covers.Ways = groupWays;
            covers.Pathes = new List<GroupPointPath>();

            var doorLinks = DoorLinkGenerator.Generate();
            List<AILootPoint> loot = null;
            List<AIExfiltrationPoint> exfils = null;
            if (covers.Patrols != null)
            {
                loot = AIPointGenerator.Containers(cores);
                exfils = AIPointGenerator.Exfils(cores);
                covers.Patrols.ContainerLootPoints = loot;
                covers.Patrols.SimpleLootPoints = new List<AILootPoint>();
                covers.Patrols.LootPointClusters = new List<AILootPointsCluster>();
                covers.Patrols.ExfiltrationPoints = exfils;
            }

            BucketVoxels(covers.Voxels, points, doorLinks, loot, exfils);

            // what RestoreData would do last: resolve each point's core (bots read CorePointInGame)
            foreach (var p in points)
                p.InitForGame(covers.AICorePointsHolder);

            Loader.Verbose($"[AI] cover graph: {points.Count} points, {groupWays.Count} ways, {cores.Count} cores, " +
                           $"{doorLinks.Count} door links, {loot?.Count ?? 0} container points, {exfils?.Count ?? 0} exfil points ({sw.ElapsedMilliseconds} ms)");
        }

        // ------------------------------------------------------------------ stage 1: candidates

        private static List<GenPoint> ScanCandidates()
        {
            var tri = NavMesh.CalculateTriangulation();
            Vector3 min = tri.vertices[0], max = tri.vertices[0];
            foreach (var v in tri.vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            var accepted = new List<GenPoint>();
            var hash = new SpatialHash<GenPoint>(2f);
            int mask = LayersMaskController.HighPolyWithTerrainMask;

            for (float x = min.x; x <= max.x; x += SampleStep)
            for (float z = min.z; z <= max.z; z += SampleStep)
            for (float y = min.y; y <= max.y + 2f; y += 4f) // maps are multi-storey
            {
                if (!NavMesh.SamplePosition(new Vector3(x, y, z), out var navHit, 1.9f, NavMesh.AllAreas))
                    continue;
                var basePos = navHit.position;

                for (int i = 0; i < RayDirections; i++)
                {
                    float ang = i * 2f * Mathf.PI / RayDirections;
                    var dir = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang));
                    if (!Physics.Raycast(basePos + Vector3.up * DetectHeight, dir, out var hit, DetectDist, mask))
                        continue;

                    // wallDirection points off the wall into open space
                    var wallDir = new Vector3(hit.normal.x, 0f, hit.normal.z);
                    if (wallDir.sqrMagnitude < 0.25f)
                        continue; // floor or ramp, not a wall
                    wallDir.Normalize();

                    // CoverPointCreatorPreset SMALL_EDGE_SIZE = 1: the wall must continue ±0.5 m
                    var tangent = Vector3.Cross(Vector3.up, wallDir);
                    var probeBase = hit.point + wallDir * 0.3f;
                    if (!Physics.Raycast(probeBase + tangent * 0.5f, -wallDir, 0.6f, mask)
                        || !Physics.Raycast(probeBase - tangent * 0.5f, -wallDir, 0.6f, mask))
                        continue;

                    var candidate = new Vector3(hit.point.x, basePos.y, hit.point.z) + wallDir * StandOff;
                    if (!NavMesh.SamplePosition(candidate, out var candHit, 0.6f, NavMesh.AllAreas))
                        continue;
                    var pos = candHit.position;

                    // still covered from the final position?
                    if (!Physics.Raycast(pos + Vector3.up * DetectHeight, -wallDir, StandOff + 0.5f, mask))
                        continue;

                    var gp = new GenPoint
                    {
                        Pos = pos,
                        WallDir = wallDir,
                        Tall = Physics.Raycast(pos + Vector3.up * StayHeight, -wallDir, StandOff + 0.5f, mask),
                    };
                    if (!PassesClusterRules(gp, hash))
                        continue;
                    accepted.Add(gp);
                    hash.Add(pos, gp);
                }
            }

            for (int i = 0; i < accepted.Count; i++)
            {
                accepted[i].Id = i + 1;
                accepted[i].Indoor = Physics.Raycast(accepted[i].Pos + Vector3.up * 0.5f, Vector3.up, 15f, mask);
            }
            return accepted;
        }

        // navmesh-snapped points on a coarse grid, used when a map has (almost) no walls
        private static List<GenPoint> OpenGroundPoints(int firstId)
        {
            const float spacing = 6f;
            var tri = NavMesh.CalculateTriangulation();
            Vector3 min = tri.vertices[0], max = tri.vertices[0];
            foreach (var v in tri.vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            var result = new List<GenPoint>();
            var seen = new HashSet<(int, int, int)>();
            for (float x = min.x; x <= max.x; x += spacing)
            for (float z = min.z; z <= max.z; z += spacing)
            for (float y = min.y; y <= max.y + 2f; y += 4f)
            {
                if (!NavMesh.SamplePosition(new Vector3(x, y, z), out var hit, 2f, NavMesh.AllAreas))
                    continue;
                var key = (Mathf.RoundToInt(hit.position.x / 2f), Mathf.RoundToInt(hit.position.y / 2f), Mathf.RoundToInt(hit.position.z / 2f));
                if (!seen.Add(key))
                    continue;
                result.Add(new GenPoint { Id = firstId + result.Count + 1, Pos = hit.position, WallDir = Vector3.forward });
            }
            return result;
        }

        private static bool PassesClusterRules(GenPoint p, SpatialHash<GenPoint> hash)
        {
            foreach (var q in hash.Near(p.Pos))
            {
                float d = Vector3.Distance(p.Pos, q.Pos);
                if (d < ClusterNearDist)
                    return false;
                if (d < ClusterLargeDist && Vector3.Dot(p.WallDir, q.WallDir) > ClusterLargeAngleCos)
                    return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ stage 2: ways

        private static List<GenWay> BuildWays(List<GenPoint> points)
        {
            var hash = new SpatialHash<GenPoint>(NeighborRadius);
            foreach (var p in points)
                hash.Add(p.Pos, p);

            var ways = new List<GenWay>();
            var linked = new HashSet<(int, int)>();
            var path = new NavMeshPath();
            int nextId = 1;

            foreach (var p in points)
            {
                if (p.WayIds.Count >= MaxDegree)
                    continue;
                var neighbors = hash.Near(p.Pos)
                    .Where(q => q.Id != p.Id && !linked.Contains((p.Id, q.Id)))
                    .OrderBy(q => (q.Pos - p.Pos).sqrMagnitude)
                    .ToList();
                foreach (var q in neighbors)
                {
                    if (p.WayIds.Count >= MaxDegree)
                        break;
                    if (q.WayIds.Count >= MaxDegree)
                        continue;
                    float euclid = Vector3.Distance(p.Pos, q.Pos);
                    if (euclid > NeighborRadius)
                        continue;
                    if (!NavMesh.CalculatePath(p.Pos, q.Pos, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                        continue;
                    float dist = NavGraphBuilder.PathLength(path);
                    if (dist > euclid * 3f + 1f)
                        continue; // a wildly indirect neighbour isn't one
                    Link(ways, p, q, dist, ref nextId);
                    linked.Add((p.Id, q.Id));
                    linked.Add((q.Id, p.Id));
                }
            }
            return ways;
        }

        private static void Link(List<GenWay> ways, GenPoint a, GenPoint b, float dist, ref int nextId)
        {
            int ab = nextId++, ba = nextId++;
            ways.Add(new GenWay { Id = ab, From = a.Id, To = b.Id, Dist = dist });
            ways.Add(new GenWay { Id = ba, From = b.Id, To = a.Id, Dist = dist });
            a.WayIds.Add(ab);
            b.WayIds.Add(ba);
        }

        // greedy linking leaves reachable pockets disconnected; bridge fragments ignoring the degree
        // cap, then drop fragments that still can't connect and are too small to be real islands
        private static void BridgeAndPrune(ref List<GenPoint> points, List<GenWay> ways)
        {
            var byId = points.ToDictionary(p => p.Id);
            int nextId = ways.Count > 0 ? ways.Max(w => w.Id) + 1 : 1;
            var path = new NavMeshPath();

            for (int round = 0; round < 6; round++)
            {
                var comps = Components(points, ways);
                if (comps.Count <= 1)
                    break;
                var main = comps.OrderByDescending(c => c.Count).First();
                var hash = new SpatialHash<GenPoint>(BridgeRadius / 2f);
                foreach (var p in points)
                    hash.Add(p.Pos, p);
                var compOf = new Dictionary<int, int>();
                for (int i = 0; i < comps.Count; i++)
                    foreach (var id in comps[i])
                        compOf[id] = i;

                bool merged = false;
                for (int ci = 0; ci < comps.Count; ci++)
                {
                    if (comps[ci] == main)
                        continue;
                    int tried = 0;
                    var pairs = comps[ci].Select(id => byId[id])
                        .SelectMany(p => hash.Near(p.Pos).Where(q => compOf[q.Id] != ci).Select(q => (p, q, d: (p.Pos - q.Pos).sqrMagnitude)))
                        .Where(t => t.d < BridgeRadius * BridgeRadius)
                        .OrderBy(t => t.d);
                    foreach (var (p, q, _) in pairs)
                    {
                        if (tried++ >= 24)
                            break;
                        if (!NavMesh.CalculatePath(p.Pos, q.Pos, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                            continue;
                        Link(ways, p, q, NavGraphBuilder.PathLength(path), ref nextId);
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    break;
            }

            var final = Components(points, ways);
            var keep = new HashSet<int>(final.Where(c => c.Count >= MinComponentSize).SelectMany(c => c));
            if (keep.Count == 0 && final.Count > 0)
                keep = new HashSet<int>(final.OrderByDescending(c => c.Count).First()); // small map: keep the largest island
            if (keep.Count == points.Count)
                return;
            points = points.Where(p => keep.Contains(p.Id)).ToList();
            var dead = new HashSet<int>(ways.Where(w => !keep.Contains(w.From) || !keep.Contains(w.To)).Select(w => w.Id));
            ways.RemoveAll(w => dead.Contains(w.Id));
            foreach (var p in points)
                p.WayIds.RemoveAll(dead.Contains);
        }

        private static List<List<int>> Components(List<GenPoint> points, List<GenWay> ways)
        {
            var parent = points.ToDictionary(p => p.Id, p => p.Id);
            int Find(int a)
            {
                while (parent[a] != a)
                {
                    parent[a] = parent[parent[a]];
                    a = parent[a];
                }
                return a;
            }
            foreach (var w in ways)
                if (parent.ContainsKey(w.From) && parent.ContainsKey(w.To))
                    parent[Find(w.From)] = Find(w.To);
            return points.GroupBy(p => Find(p.Id)).Select(g => g.Select(p => p.Id).ToList()).ToList();
        }

        // ------------------------------------------------------------------ stage 3: cores

        // core points are the coarse anchors bots group around: a spaced subset of cover points,
        // with ConnectionGroupId = connected component of the ways graph (a navmesh island)
        private static List<AICorePoint> SynthesizeCores(List<GenPoint> gen, List<GenWay> ways)
        {
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                while (parent.TryGetValue(x, out var p) && p != x)
                    x = p;
                return x;
            }
            foreach (var g in gen)
                parent[g.Id] = g.Id;
            foreach (var w in ways)
            {
                int a = Find(w.From), b = Find(w.To);
                if (a != b)
                    parent[a] = b;
            }

            var root = new GameObject("SDK_GeneratedAICorePoints");
            var cores = new List<AICorePoint>();
            var accepted = new List<Vector3>();
            var groupIds = new Dictionary<int, int>();
            int nextGroup = 1, coreId = 1;
            foreach (var g in gen)
            {
                bool tooClose = false;
                foreach (var a in accepted)
                    if ((a - g.Pos).sqrMagnitude < CoreSpacing * CoreSpacing)
                    {
                        tooClose = true;
                        break;
                    }
                if (tooClose)
                    continue;
                accepted.Add(g.Pos);

                int r = Find(g.Id);
                if (!groupIds.TryGetValue(r, out var group))
                    groupIds[r] = group = nextGroup++;

                var go = new GameObject($"Core_{coreId}");
                go.transform.SetParent(root.transform);
                go.transform.position = g.Pos;
                var core = go.AddComponent<AICorePoint>();
                core.SetIds(coreId++, group);
                if (core.ConnectionsAtNet == null)
                    core.ConnectionsAtNet = new List<AICorePoint>();
                cores.Add(core);
            }

            // a map with no cover at all still needs one anchor for spawn/patrol lookups
            if (cores.Count == 0)
            {
                var tri = NavMesh.CalculateTriangulation();
                var go = new GameObject("Core_1");
                go.transform.SetParent(root.transform);
                go.transform.position = tri.vertices[0];
                var core = go.AddComponent<AICorePoint>();
                core.SetIds(1, 1);
                core.ConnectionsAtNet = new List<AICorePoint>();
                cores.Add(core);
            }
            return cores;
        }

        // ------------------------------------------------------------------ stage 4: GroupPoints

        private static List<GroupPoint> BuildGroupPoints(List<GenPoint> gen, List<AICorePoint> cores)
        {
            var points = new List<GroupPoint>(gen.Count);
            foreach (var g in gen)
            {
                // FirePosition must not be zero: TaclicalMoveNode lerps towards it without a zero
                // check. Retail uses a purely vertical offset of CHECK_CAN_HIDE_STAY.
                var firePos = g.Pos + Vector3.up * GroupPoint.CHECK_CAN_HIDE_STAY;
                var p = new GroupPoint(g.Id, null, g.Pos, null, NavGraphBuilder.NearestCore(cores, g.Pos),
                    g.Tall ? CoverLevel.Stay : CoverLevel.Sit, false, g.WallDir, firePos, PointWithNeighborType.cover);
                p.CoverType = CoverType.Wall;
                p.EnvironmentType = g.Indoor ? EnvironmentType.Indoor : EnvironmentType.Outdoor;
                p.NeighbourhoodsWaysIds = new List<int>(g.WayIds);
                p.CalcDefenceLevel();
                // let the game compute the rest; these read FirePosition and CoverLevel, so run last
                try { p.InitLightBorders(); } catch { }
                try { p.InitLookSides(); } catch { }
                try { p.InitTilt(); } catch { }
                points.Add(p);
            }
            return points;
        }

        // ------------------------------------------------------------------ stage 5: voxels

        private static void BucketVoxels(AIVoxelesData vox, List<GroupPoint> points, List<NavMeshDoorLink> doorLinks,
            List<AILootPoint> loot, List<AIExfiltrationPoint> exfils)
        {
            var pointsByCell = new Dictionary<(int, int, int), List<GroupPoint>>();
            foreach (var p in points)
                Add(pointsByCell, NavGraphBuilder.CellIndex(vox, p.Position), p);

            var lootByCell = new Dictionary<(int, int, int), List<int>>();
            if (loot != null)
                foreach (var lp in loot)
                    Add(lootByCell, NavGraphBuilder.CellIndex(vox, lp.Position), lp.Id);

            var exfilByCell = new Dictionary<(int, int, int), List<int>>();
            if (exfils != null)
                foreach (var ex in exfils)
                    Add(exfilByCell, NavGraphBuilder.CellIndex(vox, ex.Position), ex.Id);

            // baked convention: each door link id is listed in the 3x3x3 neighbourhood of its cell
            var doorByCell = new Dictionary<(int, int, int), List<NavMeshDoorLink>>();
            foreach (var dl in doorLinks)
            {
                var (cx, cy, cz) = NavGraphBuilder.CellIndex(vox, dl.MidOpen);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    int x = cx + dx, y = cy + dy, z = cz + dz;
                    if (x < 0 || y < 0 || z < 0 || x >= vox.MaxX || y >= vox.MaxY || z >= vox.MaxZ)
                        continue;
                    Add(doorByCell, (x, y, z), dl);
                }
            }

            foreach (var cell in vox.VoxelsList)
            {
                var idx = ((int)cell.IndexX, (int)cell.IndexY, (int)cell.IndexZ);
                cell.PointsIds = new List<int>();
                cell.Points = new List<GroupPoint>();
                cell._closetsPointId = 0;
                cell.PointStartSearch = null;
                if (pointsByCell.TryGetValue(idx, out var inside))
                {
                    var centre = cell.Position + new Vector3(5f, 2.5f, 5f);
                    var closest = inside.OrderBy(p => (p.Position - centre).sqrMagnitude).First();
                    cell.PointsIds = inside.Select(p => p.Id).ToList();
                    cell.Points = inside;
                    cell._closetsPointId = closest.Id;
                    cell.PointStartSearch = closest;
                }
                cell.LootPointsIds = lootByCell.TryGetValue(idx, out var lootIds) ? lootIds : new List<int>();
                cell.ExfiltrationPointsIds = exfilByCell.TryGetValue(idx, out var exfilIds) ? exfilIds : new List<int>();
                // bots read the resolved list (BotNearDoorData.CurrentDoorLinks), not just the ids
                cell.DoorLinks = doorByCell.TryGetValue(idx, out var links) ? links : new List<NavMeshDoorLink>();
                cell.DoorLinksIds = cell.DoorLinks.Select(l => l.Id).ToList();
            }

            // empty cells still need a search seed: bots standing in one dereference
            // PointStartSearch in GetLastConnectionId
            if (points.Count == 0)
                return;
            foreach (var cell in vox.VoxelsList)
            {
                if (cell.PointStartSearch != null)
                    continue;
                var centre = cell.Position + new Vector3(5f, 2.5f, 5f);
                GroupPoint nearest = null;
                float best = float.MaxValue;
                foreach (var p in points)
                {
                    float d = (p.Position - centre).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        nearest = p;
                    }
                }
                cell.PointStartSearch = nearest;
                cell._closetsPointId = nearest.Id;
            }
        }

        private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<TValue>();
            list.Add(value);
        }

        // uniform-cell spatial hash; Near() scans the 3x3x3 neighbourhood
        private sealed class SpatialHash<T>
        {
            private readonly float _cell;
            private readonly Dictionary<(int, int, int), List<T>> _map = new Dictionary<(int, int, int), List<T>>();

            public SpatialHash(float cell) { _cell = cell; }

            private (int, int, int) Key(Vector3 p) =>
                (Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));

            public void Add(Vector3 pos, T item)
            {
                var k = Key(pos);
                if (!_map.TryGetValue(k, out var list))
                    _map[k] = list = new List<T>();
                list.Add(item);
            }

            public IEnumerable<T> Near(Vector3 pos)
            {
                var (kx, ky, kz) = Key(pos);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_map.TryGetValue((kx + dx, ky + dy, kz + dz), out var list))
                        foreach (var item in list)
                            yield return item;
            }
        }
    }
}
