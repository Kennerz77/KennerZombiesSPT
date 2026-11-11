using System.Collections.Generic;
using BepInEx.Configuration;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace KennerZombies
{
    /// <summary>
    /// The original-style hunting for zombies that are in with the players, smooth and cheap, and trainable:
    ///  - every player leaves a trail of crumbs on the navmesh (one each 1.2 m they move, the last 80);
    ///  - a zombie close (15 m) with a clear walk straight at its player goes straight at them;
    ///  - else it follows that player's trail: the newest crumb it can walk straight to (NavMesh.Raycast, a few per
    ///    look), so everyone chasing a player takes the same line behind them and bunches up: a train;
    ///  - only when neither works (another floor, round a lot of walls) a full route, and those are rationed
    ///    (FullRoutesPerFrame across all zombies), so a big round never spikes a frame.
    /// The 2026-11-08 ask: "hunting smooth like the original/classic zombies, without crazy resource use, players can train them".
    /// </summary>
    internal static class ZombiesTrails
    {
        public enum Kind { Direct, Trail, Path }

        public static ConfigEntry<bool> On;
        private const float Spacing = 1.2f, DirectWithin = 15f, Reached = 1.0f;
        private const int MaxCrumbs = 80, LookAhead = 8, FullRoutesPerFrame = 4;

        private class Trail
        {
            public readonly List<Vector3> Crumbs = new List<Vector3>();
            public int First;          // serial number of Crumbs[0]
            public int Next => First + Crumbs.Count; // serial the next crumb gets
        }

        private static readonly Dictionary<string, Trail> Trails = new Dictionary<string, Trail>();
        private static readonly Dictionary<BotOwner, (string who, int serial)> Following = new Dictionary<BotOwner, (string, int)>();
        private static int _budgetFrame = -1, _budgetUsed;

        public static void Bind(ConfigFile config, string section)
        {
            On = config.Bind(section, "Zombies follow your trail", true,
                "Zombies in with you follow the path you ran (and come straight at you when there's a clear line), so they bunch up behind you and can be trained. Off: a full route to you, re-planned as you move.");
        }

        /// <summary>Each frame (host): a crumb wherever a player has moved 1.2 m from their last one.</summary>
        public static void Record(List<Player> players)
        {
            if (players == null)
                return;
            foreach (Player p in players)
            {
                if (p == null || !p.HealthController.IsAlive)
                    continue;
                if (!Trails.TryGetValue(p.ProfileId, out Trail t))
                    Trails[p.ProfileId] = t = new Trail();
                Vector3 at = p.Transform.position;
                if (t.Crumbs.Count > 0 && (t.Crumbs[t.Crumbs.Count - 1] - at).sqrMagnitude < Spacing * Spacing)
                    continue;
                // On the navmesh only (a jump, a ledge the navmesh doesn't cover: no crumb there)
                if (!NavMesh.SamplePosition(at, out NavMeshHit h, 0.6f, NavMesh.AllAreas))
                    continue;
                t.Crumbs.Add(h.position);
                if (t.Crumbs.Count > MaxCrumbs)
                {
                    t.Crumbs.RemoveAt(0);
                    t.First++;
                }
            }
        }

        /// <summary>Where a zombie that's in with the players heads now, and how it got there.</summary>
        /// <summary>Two points on one floor (a step or a ramp's worth apart, not a floor above or below).</summary>
        /// <summary>
        /// A zombie's body fits along the straight line too, not only the navmesh's thin one (user, 2026-11-10: zombies
        /// walked into walls instead of going round them): a body-wide sphere at hip and chest height (above steps), the map's solid
        /// colliders only. Blocked: a full route instead (the navmesh's own way round).
        /// </summary>
        private const float BodyRadius = 0.3f;

        public static bool BodyClear(Vector3 from, Vector3 to)
        {
            Vector3 flat = new Vector3(to.x - from.x, 0f, to.z - from.z);
            float d = flat.magnitude;
            if (d < 0.5f)
                return true;
            Vector3 dir = flat / d;
            int mask = LayersMaskController.HighPolyWithTerrainMask;
            foreach (float up in new[] { 0.9f, 1.4f })
                if (Physics.SphereCast(from + Vector3.up * up, BodyRadius, dir, out RaycastHit hit, d - 0.3f, mask, QueryTriggerInteraction.Ignore)
                    && hit.collider.GetComponentInParent<Player>() == null)
                    return false;
            return true;
        }

        public static bool SameFloor(Vector3 a, Vector3 b) => Mathf.Abs(a.y - b.y) < 1.2f;

        public static Vector3 GoalFor(BotOwner z, Vector3 at, Player target, out Kind kind)
        {
            Vector3 goal = target.Transform.position;
            kind = Kind.Path;
            if (!NavMesh.SamplePosition(at, out NavMeshHit me, 1f, NavMesh.AllAreas))
                return goal;
            // Close and nothing in the way on the navmesh: straight at them. Same floor only: NavMesh.Raycast walks the
            // zombie's own floor and ignores height, so a player in the basement read as "in a straight line" from
            // right above, and the zombies stood on the floor over them instead of taking the stairs (the user,
            // 2026-11-08)
            if ((goal - at).sqrMagnitude < DirectWithin * DirectWithin && SameFloor(at, goal)
                && NavMesh.SamplePosition(goal, out NavMeshHit them, 1f, NavMesh.AllAreas)
                && !NavMesh.Raycast(me.position, them.position, out _, NavMesh.AllAreas) && BodyClear(me.position, them.position))
            {
                kind = Kind.Direct;
                Following.Remove(z);
                return them.position;
            }
            if (!Trails.TryGetValue(target.ProfileId, out Trail t) || t.Crumbs.Count == 0)
                return goal;
            int newest = t.Next - 1;
            int cur = Following.TryGetValue(z, out var f) && f.who == target.ProfileId && f.serial >= t.First ? f.serial : -1;
            if (cur < 0)
            {
                // Onto the trail: the nearest crumbs it can walk straight to (a handful of looks)
                float best = float.MaxValue;
                for (int i = 0; i < t.Crumbs.Count; i++)
                {
                    float d = (t.Crumbs[i] - me.position).sqrMagnitude;
                    if (d < best && d < 30f * 30f && SameFloor(me.position, t.Crumbs[i]))
                    {
                        best = d;
                        cur = t.First + i;
                    }
                }
                if (cur < 0 || NavMesh.Raycast(me.position, t.Crumbs[cur - t.First], out _, NavMesh.AllAreas) || !BodyClear(me.position, t.Crumbs[cur - t.First]))
                    return goal; // nowhere near the trail, or it's behind a wall: a full route
            }
            // Further along the trail if it can see that far (newest first, a few looks)
            int looks = 0;
            for (int s = newest; s > cur && looks < LookAhead; s--, looks++)
                if (SameFloor(me.position, t.Crumbs[s - t.First]) && !NavMesh.Raycast(me.position, t.Crumbs[s - t.First], out _, NavMesh.AllAreas)
                    && BodyClear(me.position, t.Crumbs[s - t.First]))
                {
                    cur = s;
                    break;
                }
            // At its crumb with nothing newer in sight: the next one
            if (cur < newest && (t.Crumbs[cur - t.First] - me.position).sqrMagnitude < Reached * Reached)
                cur++;
            Following[z] = (target.ProfileId, cur);
            kind = Kind.Trail;
            return t.Crumbs[cur - t.First];
        }

        /// <summary>A full route may be planned this frame (rationed across all zombies).</summary>
        public static bool Budget()
        {
            if (_budgetFrame != Time.frameCount)
            {
                _budgetFrame = Time.frameCount;
                _budgetUsed = 0;
            }
            if (_budgetUsed >= FullRoutesPerFrame)
                return false;
            _budgetUsed++;
            return true;
        }

        public static void Forget(BotOwner z) => Following.Remove(z);

        public static void Clear()
        {
            Trails.Clear();
            Following.Clear();
        }
    }
}
