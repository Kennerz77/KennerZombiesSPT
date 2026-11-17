using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.AI;

namespace KennerZombies.MapLoader.AI
{
    // AI loot points for lootable containers and AI exfil points for extracts, so bots can loot
    // and leave. Loose-loot AI points are not generated yet.
    //
    // Ported from an earlier LootScanner.
    internal static class AIPointGenerator
    {
        // baked container points stand at most ~2.45 m from the container
        private const float MaxStandOff = 2.5f;

        public static List<AILootPoint> Containers(List<AICorePoint> cores)
        {
            var result = new List<AILootPoint>();
            int id = 1;
            foreach (var c in LocationScene.GetAllObjects<LootableContainer>(false))
            {
                if (c == null || !TryStandPos(c.transform.position, out var stay))
                    continue;
                result.Add(new AILootPoint(id++, stay, c.transform.position, c, NavGraphBuilder.NearestCore(cores, stay)));
            }
            return result;
        }

        // PatrolExfiltrationPointsData filters by exfil status and requirements at runtime, so
        // generating one per exfil is safe. Extra positions keep grouped bots from stacking.
        public static List<AIExfiltrationPoint> Exfils(List<AICorePoint> cores)
        {
            var result = new List<AIExfiltrationPoint>();
            int id = 1;
            foreach (var exfil in LocationScene.GetAllObjects<ExfiltrationPoint>(false))
            {
                if (exfil == null || exfil.Settings == null)
                    continue;
                if (!NavMesh.SamplePosition(exfil.transform.position, out var hit, 3f, NavMesh.AllAreas))
                    continue;
                var p = new AIExfiltrationPoint(id++, hit.position, NavGraphBuilder.NearestCore(cores, hit.position), exfil);
                p.LinkExfiltration(exfil);
                for (int i = 0; i < 4 && p._posAdditional.Count < 3; i++)
                {
                    var offset = Quaternion.Euler(0f, 90f * i, 0f) * Vector3.forward * 1.5f;
                    if (NavMesh.SamplePosition(hit.position + offset, out var extra, 1f, NavMesh.AllAreas)
                        && (extra.position - hit.position).sqrMagnitude > 0.5f)
                        p.AddPositionAdditional(extra.position);
                }
                result.Add(p);
            }
            return result;
        }

        private static bool TryStandPos(Vector3 lootPos, out Vector3 stay)
        {
            stay = default;
            if (!NavMesh.SamplePosition(lootPos, out var hit, 2f, NavMesh.AllAreas))
                return false;
            float dx = hit.position.x - lootPos.x, dz = hit.position.z - lootPos.z;
            if (dx * dx + dz * dz > MaxStandOff * MaxStandOff)
                return false;
            stay = hit.position;
            return true;
        }
    }
}
