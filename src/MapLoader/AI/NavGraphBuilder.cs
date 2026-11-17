using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace KennerZombies.MapLoader.AI
{
    // Builds everything AICoversData.RestoreData would normally restore from baked data:
    //   voxel grid -> cover points + ways + core points -> door links, loot and exfil AI points
    //   -> patrol routes for zones without authored ones -> cover cache
    //
    // Generation approach ported from earlier scanners (CoverScanner, PatrolScanner, DoorScanner,
    // LootScanner and the RestoreData fallback in RaidFixPatches), used with the author's permission.
    internal static class NavGraphBuilder
    {
        private static readonly FieldInfo CacheField = AccessTools.Field(typeof(AICoversData), "_cache");

        public static bool Build(AICoversData covers)
        {
            var sw = Stopwatch.StartNew();
            if (NavMesh.CalculateTriangulation().indices.Length == 0)
            {
                Loader.Log.LogError("Custom map has no navmesh. Bake one in the SDK before building the map; bots will not move.");
                HealHolders(covers);
                EnsureCollections(covers);
                return false;
            }

            HealHolders(covers);
            EnsureCollections(covers);

            if (!BuildVoxelGrid(covers))
                return false;

            CoverGenerator.Generate(covers);
            PatrolGenerator.Generate(covers);

            // the last thing RestoreData does; every bot activation reads this cache
            CacheField.SetValue(covers, new AICoversDataCache(covers));
            covers._lastId = MaxPointId(covers) + 1;

            Loader.Verbose($"AI navigation data generated in {sw.ElapsedMilliseconds} ms: {covers.Points.Count} cover points, " +
                                     $"{covers.AICorePointsHolder.CorePoints.Count} core points, {covers.Voxels.VoxelsList.Count} voxels");
            return true;
        }

        private static int MaxPointId(AICoversData covers)
        {
            int max = 0;
            foreach (var p in covers.Points)
                if (p.Id > max)
                    max = p.Id;
            return max;
        }

        private static void EnsureCollections(AICoversData covers)
        {
            if (covers.Points == null) covers.Points = new List<GroupPoint>();
            if (covers.Ways == null) covers.Ways = new List<GroupPointWay>();
            if (covers.Pathes == null) covers.Pathes = new List<GroupPointPath>();
        }

        // Holders created by CreateOrFind, or deserialized from SDK stubs, can carry null lists that
        // game code iterates without checks (an AIPlaceInfoHolder with null Places stalls every
        // rogue brain). Replace every null List<>/array field with an empty one.
        private static void HealHolders(AICoversData covers)
        {
            var holders = new UnityEngine.Object[]
            {
                covers.AICorePointsHolder, covers.AIPlaceInfoHolder, covers.AIManualPointsHolder, covers.AIMinesPositions,
                covers.AIDangerPlacesHolder, covers.Patrols, covers.Voxels,
                UnityEngine.Object.FindObjectOfType<AIDoorsHolder>(), UnityEngine.Object.FindObjectOfType<BotZoneEntranceInfo>(),
            };
            foreach (var holder in holders)
            {
                if (holder == null)
                    continue;
                foreach (var f in holder.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.IsInitOnly || f.GetValue(holder) != null)
                        continue;
                    if (f.FieldType.IsArray && f.FieldType.GetArrayRank() == 1)
                        f.SetValue(holder, Array.CreateInstance(f.FieldType.GetElementType(), 0));
                    else if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>))
                        f.SetValue(holder, Activator.CreateInstance(f.FieldType));
                }
            }
            if (covers.AIPlaceInfoHolder != null && covers.AIPlaceInfoHolder.Places == null)
                covers.AIPlaceInfoHolder.Places = new List<AIPlaceInfo>();
            if (covers.EntranceInfo == null)
                covers.EntranceInfo = UnityEngine.Object.FindObjectOfType<BotZoneEntranceInfo>();
        }

        // Bots index the grid by position on every activation (cell = (pos - min) / (10, 5, 10));
        // a missing grid throws there and the bot never activates. Cover generation buckets into
        // the grid, so it is built first and starts with empty cells.
        private static bool BuildVoxelGrid(AICoversData covers)
        {
            var vox = covers.Voxels;
            if (vox == null)
            {
                Loader.Log.LogError("No AIVoxelesData on the map; cannot build AI data.");
                return false;
            }

            var tri = NavMesh.CalculateTriangulation();
            Vector3 min = tri.vertices[0], max = tri.vertices[0];
            foreach (var v in tri.vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            min -= new Vector3(10f, 5f, 10f);
            max += new Vector3(10f, 5f, 10f);
            int nx = Mathf.Max(1, Mathf.CeilToInt((max.x - min.x) / 10f));
            int ny = Mathf.Max(1, Mathf.CeilToInt((max.y - min.y) / 5f));
            int nz = Mathf.Max(1, Mathf.CeilToInt((max.z - min.z) / 10f));
            if (nx * ny * nz > 60000)
            {
                // voxel ids are ushort-sized in the game
                Loader.Log.LogError($"Navmesh bounds too large for the AI voxel grid ({nx}x{ny}x{nz} cells). Keep maps within ~2.4 km² and ~150 m of height.");
                return false;
            }

            vox.MinVoxelesValues = min;
            // not a world position: RestoreData sizes the array from these, so they are cell counts
            vox.MaxVoxelesValues = new Vector3(nx, ny, nz);
            vox.MaxX = nx;
            vox.MaxY = ny;
            vox.MaxZ = nz;
            vox.VoxelesArray = new NavGraphVoxelSimple[nx, ny, nz];
            vox.VoxelsList = new List<NavGraphVoxelSimple>(nx * ny * nz);
            int id = 1;
            for (int x = 0; x < nx; x++)
                for (int y = 0; y < ny; y++)
                    for (int z = 0; z < nz; z++)
                    {
                        // cell origin; the baked convention puts the centre at origin + (5, 2.5, 5)
                        var cell = new NavGraphVoxelSimple(min + new Vector3(x * 10f, y * 5f, z * 10f), x, y, z, id++);
                        cell.DoorLinks = new List<NavMeshDoorLink>();
                        vox.VoxelesArray[x, y, z] = cell;
                        vox.VoxelsList.Add(cell);
                    }
            Loader.Verbose($"[AI] voxel grid {nx}x{ny}x{nz} over navmesh bounds {min}..{max}");
            return true;
        }

        // same double-int-cast floor the game uses in GetIndexes, clamped like GetVoxelSafe
        internal static (int, int, int) CellIndex(AIVoxelesData vox, Vector3 pos)
        {
            int ix = Mathf.Clamp((int)((float)(int)(pos.x - vox.MinVoxelesValues.x) / 10f), 0, vox.MaxX - 1);
            int iy = Mathf.Clamp((int)((float)(int)(pos.y - vox.MinVoxelesValues.y) / 5f), 0, vox.MaxY - 1);
            int iz = Mathf.Clamp((int)((float)(int)(pos.z - vox.MinVoxelesValues.z) / 10f), 0, vox.MaxZ - 1);
            return (ix, iy, iz);
        }

        internal static AICorePoint NearestCore(List<AICorePoint> cores, Vector3 pos)
        {
            AICorePoint best = null;
            float bestD = float.MaxValue;
            foreach (var c in cores)
            {
                if (c == null)
                    continue;
                float d = (c.Position - pos).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = c;
                }
            }
            return best;
        }

        internal static float PathLength(NavMeshPath path)
        {
            float len = 0f;
            var c = path.corners;
            for (int i = 1; i < c.Length; i++)
                len += Vector3.Distance(c[i - 1], c[i]);
            return len;
        }
    }
}
