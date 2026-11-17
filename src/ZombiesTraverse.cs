using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original zombies' own traversals (user, 2026-11-15: "drop from ledges: jump down, stumble, back to walking,
    /// smooth" and the window vaults): clips from the original game retargeted onto the zombies' bodies
    /// (tools/retarget_traversal.py -> data/zombie_traverse.json, every limb within 2 deg of the source).
    /// - Climb(): in through a barricade's window (traverse_v6; a crawler: crawl_mantle_over_36; a sprinter: run_mantle_over_36)
    /// - Drop(): down a nav link or a Map Kit ledge (a ledge, a hole in a floor): jump_down_36..160 picked by the height,
    ///   run_ and crawl_ versions.
    /// - JumpUp(): up a Map Kit ledge (user 2026-11-15: zombies jump up to a player above): jump_up_36..160 picked by the
    ///   height, run_ and crawl_ versions, the clip's climb put on the ledge's real edge.
    /// The host moves the body along the clip's own root motion fitted between the two spots (its forward travel and
    /// its drop stretched to the real ones), and every game plays the pose (Msg.Traverse), blended in and out of the
    /// game's own walk (ZombieAnimPlayer.Traverse).
    /// </summary>
    internal static class ZombiesTraverse
    {
        private const string Section = "21. Zombie animations (dev)";
        public static ConfigEntry<bool> WindowClimb, LedgeDrops, LedgeClimbs;
        public static ConfigEntry<float> Clearance, ClimbSpeed, LedgeClimbSpeed;

        // The original's jump-down heights (inches in its names) in metres, as retargeted
        private static readonly (string clip, float drop)[] Drops =
        {
            ("jump_down_36", 1.01f), ("jump_down_48", 1.35f), ("jump_down_72", 2.02f), ("jump_down_96", 2.69f),
            ("jump_down_128", 3.59f), ("jump_down_160", 4.50f),
        };
        private static readonly (string clip, float drop)[] RunDrops =
        {
            ("run_jump_down_36", 1.01f), ("run_jump_down_48", 1.35f), ("run_jump_down_72", 2.02f), ("run_jump_down_96", 2.69f),
        };
        private static readonly (string clip, float drop)[] CrawlDrops =
        {
            ("crawl_jump_down_36", 1.02f), ("crawl_jump_down_48", 1.37f), ("crawl_jump_down_72", 2.03f), ("crawl_jump_down_96", 2.69f),
        };

        // The original's jump-ups (inches in its names) in metres, as retargeted (crawl_jump_up_48 rises only 1.05 m: left out)
        private static readonly (string clip, float drop)[] Ups =
        {
            ("jump_up_36", 1.01f), ("jump_up_48", 1.35f), ("jump_up_72", 2.02f), ("jump_up_96", 2.69f),
            ("jump_up_128", 3.59f), ("jump_up_160", 4.49f),
        };
        private static readonly (string clip, float drop)[] RunUps =
        {
            ("run_jump_up_36", 1.02f), ("run_jump_up_48", 1.35f), ("run_jump_up_72", 2.02f), ("run_jump_up_96", 2.70f),
        };
        private static readonly (string clip, float drop)[] CrawlUps =
        {
            ("crawl_jump_up_36", 1.01f), ("crawl_jump_up_72", 2.02f), ("crawl_jump_up_96", 2.67f), ("crawl_jump_up_128", 3.59f),
            ("crawl_jump_up_160", 4.30f),
        };

        public static void Bind(ConfigFile config)
        {
            WindowClimb = config.Bind(Section, "Original window climb", true,
                "Zombies climb in through the barricades with the original's own climb animation (off: the game's vault, else the scripted arc).");
            LedgeDrops = config.Bind(Section, "Original ledge drops", true,
                "Zombies jump down ledges (Map Kit ledges and nav links) with the original's own jump-down animations.");
            LedgeClimbs = config.Bind(Section, "Original ledge jump-ups", true,
                "Zombies jump up Map Kit ledges to a player above with the original's own jump-up animations (off: they glide up).");
            LedgeClimbSpeed = config.Bind(Section, "Ledge jump-up speed", 1f, new ConfigDescription(
                "How fast the ledge jump-up plays (1 = the original's own speed; the high ones take 5-7 s).",
                new AcceptableValueRange<float>(0.5f, 2f)));
            // User, 2026-11-15 (the vaultingexample.mp4 reference): "they vault very high": the window clip clears 1.42 m (the
            // original's sill), the sandbags are 0.75-1.11 m: the lift is now cut to the sill each window measures
            Clearance = config.Bind(Section, "Window climb clearance", 0.12f, new ConfigDescription(
                "How far (m) the zombies' legs clear a window's sill when they climb over (the climb's height follows each window's own sill).",
                new AcceptableValueRange<float>(0f, 0.6f)));
            ClimbSpeed = config.Bind(Section, "Window climb speed", 1f, new ConfigDescription(
                "How fast the window climb plays (1 = the original's own speed; lower is slower and smoother).",
                new AcceptableValueRange<float>(0.5f, 1.5f)));
        }

        public static bool CanClimb => WindowClimb?.Value != false && ZombiesAnim.TraverseReady;
        public static bool CanDrop => LedgeDrops?.Value != false && ZombiesAnim.TraverseReady;
        public static bool CanJumpUp => LedgeClimbs?.Value != false && ZombiesAnim.TraverseReady;

        /// <summary>In through a barricade: from the outside spot to the landing inside, the window's colliders off meanwhile.</summary>
        public static IEnumerator Climb(BotOwner z, ZombiesBarricade b, Vector3 from, Vector3 to, bool sprinter, Action<bool> done)
        {
            Player body = z?.GetPlayer;
            if (body == null || !CanClimb)
            {
                done(false);
                yield break;
            }
            string clip = ZombiesCrawlers.Is(body) ? "crawl_mantle_over_36" : sprinter ? "run_mantle_over_36" : "traverse_v6";
            List<Collider> off = b != null ? b.GetComponentsInChildren<Collider>(false).Where(c => c.enabled).ToList() : new List<Collider>();
            foreach (Collider c in off)
                c.enabled = false;
            float sill = Sill(b, from, to);
            bool ok = false;
            try
            {
                yield return Play(z, body, clip, from, to, r => ok = r, sill, ClimbSpeed?.Value ?? 1f);
            }
            finally
            {
                foreach (Collider c in off)
                    if (c != null)
                        c.enabled = true;
            }
            if (ok)
                ZombiesDebug.Log($"[traverse] climbed in with {clip} at {to} over a {sill:0.00} m sill");
            done(ok);
        }

        /// <summary>Down a ledge (a one-way link's high end to its low end): the jump-down nearest the height.</summary>
        public static IEnumerator Drop(BotOwner z, Vector3 from, Vector3 to, bool sprinter, Action<bool> done)
        {
            Player body = z?.GetPlayer;
            float height = from.y - to.y;
            if (body == null || !CanDrop || height < 0.6f)
            {
                done(false);
                yield break;
            }
            var set = ZombiesCrawlers.Is(body) ? CrawlDrops : sprinter && height < 3f ? RunDrops : Drops;
            var pick = set.OrderBy(d => Mathf.Abs(d.drop - height)).First();
            string clip = pick.clip;
            // Taller than the tallest clip (the user's 6 m bridge drops, 2026-11-16): played slower, as a fall takes
            // longer the higher it is (time goes with the square root of the height), so it doesn't drop at 1.5x speed
            float speed = height > pick.drop && pick.drop > 0.1f ? Mathf.Sqrt(pick.drop / height) : 1f;
            bool ok = false;
            yield return Play(z, body, clip, from, to, r => ok = r, 0f, speed);
            if (ok)
                ZombiesDebug.Log($"[traverse] dropped {height:0.0} m with {clip}{(speed < 1f ? $" at {speed:0.00}x (taller than the clip)" : "")}");
            done(ok);
        }

        /// <summary>Up a ledge (a link's low end to its high end): the jump-up nearest the height, its climb on the real edge.</summary>
        public static IEnumerator JumpUp(BotOwner z, Vector3 from, Vector3 to, bool sprinter, Action<bool> done)
        {
            Player body = z?.GetPlayer;
            float height = to.y - from.y;
            if (body == null || !CanJumpUp || height < 0.6f)
            {
                done(false);
                yield break;
            }
            var set = ZombiesCrawlers.Is(body) ? CrawlUps : sprinter && height < 3f ? RunUps : Ups;
            string clip = set.OrderBy(d => Mathf.Abs(d.drop - height)).First().clip;
            Vector3? edge = Edge(from, to);
            bool ok = false;
            yield return Play(z, body, clip, from, to, r => ok = r, 0f, LedgeClimbSpeed?.Value ?? 1f, edge);
            if (ok)
                ZombiesDebug.Log($"[traverse] jumped up {height:0.0} m with {clip}{(edge.HasValue ? "" : " (no edge found)")}");
            done(ok);
        }

        private static int _edgeMask = -1;

        /// <summary>
        /// Where a ledge's edge is between its low and high spots (at the high floor's height): the wall below it (a ray
        /// along the floor below), else where the high floor ends (rays down, walked back from the high spot: a bridge or
        /// a floor with nothing under its edge). Null when neither is found.
        /// </summary>
        private static Vector3? Edge(Vector3 low, Vector3 high)
        {
            try
            {
                if (_edgeMask == -1)
                    _edgeMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain", "Default");
                Vector3 flat = new Vector3(high.x - low.x, 0f, high.z - low.z);
                float dist = flat.magnitude;
                if (dist < 0.05f)
                    return null;
                Vector3 dir = flat / dist;
                if (Physics.Raycast(low + Vector3.up * 0.5f, dir, out RaycastHit wall, dist + 0.5f, _edgeMask, QueryTriggerInteraction.Ignore)
                    && wall.normal.y < 0.5f)
                    return new Vector3(wall.point.x, high.y, wall.point.z);
                Vector3 last = high;
                for (float back = 0f; back <= dist; back += 0.1f)
                {
                    Vector3 p = high - dir * back;
                    if (!Physics.Raycast(new Vector3(p.x, high.y + 0.5f, p.z), Vector3.down, 0.9f, _edgeMask, QueryTriggerInteraction.Ignore))
                        return back > 0f ? last : (Vector3?)null;
                    last = new Vector3(p.x, high.y, p.z);
                }
            }
            catch
            {
            }
            return null;
        }

        // The clip on every game, the body moved along its root (here, on the host) between the two spots: its forward
        // travel stretched to the real distance and its fall to the real drop, facing the way it goes. With an edge (a
        // jump-up): the clip's climb (its root at half the rise) put just off the real edge, the travel before and after
        // stretched to fit on each side of it.
        private static IEnumerator Play(BotOwner z, Player body, string name, Vector3 from, Vector3 to, Action<bool> done,
            float sill = 0f, float speed = 1f, Vector3? edge = null)
        {
            ZombiesAnim.Clip c = ZombiesAnim.PlayTraverse(body, name, sill, speed);
            if (c == null)
            {
                done(false);
                yield break;
            }
            if (ZombiesNet.Online)
                ZombiesNet.Traverse(body.ProfileId, name, sill, speed);
            Vector4 end = c.Root[c.Root.Length - 1];
            Vector3 flat = new Vector3(to.x - from.x, 0f, to.z - from.z);
            Vector3 facing = flat.sqrMagnitude > 0.0001f ? flat.normalized : body.Transform.forward;
            // The climb's spot: the clip's travel when it's half way up, and the real one just off the edge
            float clipKnee = -1f;
            Vector3 knee = Vector3.zero;
            if (edge.HasValue && Mathf.Abs(end.y) > 0.1f && end.z > 0.3f)
            {
                for (int i = 0; i < c.Root.Length; i++)
                    if (Mathf.Abs(c.Root[i].y) >= 0.5f * Mathf.Abs(end.y))
                    {
                        clipKnee = Mathf.Clamp(c.Root[i].z, 0.1f, end.z - 0.1f);
                        break;
                    }
                // Never behind where it starts or past where it lands
                float along = Vector3.Dot(edge.Value - facing * 0.3f - from, facing), total = Vector3.Dot(to - from, facing);
                knee = from + facing * Mathf.Clamp(along, 0f, Mathf.Max(0f, total));
            }
            try
            {
                z.Mover?.Stop();
                z.Sprint(false);
            }
            catch
            {
            }
            speed = Mathf.Clamp(speed, 0.3f, 2f);
            for (float t = 0f; t < c.Length; t += Time.deltaTime * speed)
            {
                if (body == null || !body.HealthController.IsAlive)
                {
                    done(false);
                    yield break;
                }
                Vector4 r = ZombiesAnim.RootAt(c, t);
                float across = Mathf.Abs(end.z) > 0.05f ? Mathf.Clamp01(r.z / end.z) : t / c.Length;
                // Its fall: the clip's own drop (the stumble is in the pose), or along with the travel when it has none
                float down = Mathf.Abs(end.y) > 0.1f ? Mathf.Clamp01(r.y / end.y) : across;
                Vector3 at = new Vector3(Mathf.Lerp(from.x, to.x, across), Mathf.Lerp(from.y, to.y, down), Mathf.Lerp(from.z, to.z, across));
                if (clipKnee > 0f)
                {
                    Vector3 flatAt = r.z <= clipKnee ? Vector3.Lerp(from, knee, Mathf.Clamp01(r.z / clipKnee))
                        : Vector3.Lerp(knee, to, Mathf.Clamp01((r.z - clipKnee) / (end.z - clipKnee)));
                    at = new Vector3(flatAt.x, at.y, flatAt.z);
                }
                body.Teleport(at, false);
                ZombiesRounds.Face(z, at + facing * 3f + Vector3.up * 1.2f);
                yield return null;
            }
            if (body != null && body.HealthController.IsAlive)
            {
                // Through the bot's own mover: it lets the move stand and links the bot to the navmesh where it landed. A
                // plain teleport left the mover's last linked spot up on the ledge, and with a path to follow it put the
                // zombie straight back there (user, 2026-11-17: "once a zombie drops it teleports them right back up")
                try
                {
                    if (z?.Mover != null)
                    {
                        // The mover takes the height of its last good link for a second after it (still the bridge
                        // above, 6 m up and under a metre aside): re-linked there, the zombie went straight back up
                        // (user, 2026-11-17, after the first fix). Its last good link is where it landed now
                        z.Mover._lastGoodCastPoint = to;
                        z.Mover._lastGoodCastPointTime = Time.time;
                        z.Mover.Teleport(to);
                        z.Mover.SetPlayerToNavMesh(to);
                    }
                    else
                        body.Teleport(to, false);
                }
                catch
                {
                    body.Teleport(to, false);
                }
            }
            done(true);
        }

        /// <summary>
        /// A window's sill above the floor outside it (the window's own colliders already off): straight down through the
        /// window's middle onto whatever's there (sandbags, a wall, a ledge). 1 m if nothing sensible is found.
        /// </summary>
        private static float Sill(ZombiesBarricade b, Vector3 from, Vector3 to)
        {
            try
            {
                float floor = from.y;
                if (Physics.Raycast(from + Vector3.up * 0.5f, Vector3.down, out RaycastHit ground, 2f, ~0, QueryTriggerInteraction.Ignore))
                    floor = ground.point.y;
                Vector3 mid = b != null ? b.transform.position : (from + to) * 0.5f;
                mid.y = floor + 1.6f;
                float best = -1f;
                foreach (RaycastHit hit in Physics.RaycastAll(mid, Vector3.down, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || hit.collider.GetComponentInParent<Player>() != null)
                        continue;
                    best = Mathf.Max(best, hit.point.y - floor);
                }
                return best >= 0.2f && best <= 1.5f ? best : 1f;
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>The host said this zombie is climbing or dropping now: the pose here (its moves come from the network).</summary>
        public static void PlayRemote(string profileId, string clip, float sill, float speed)
        {
            GameWorld world = Comfort.Common.Singleton<GameWorld>.Instance;
            if (world == null || string.IsNullOrEmpty(profileId))
                return;
            foreach (Player p in world.AllAlivePlayersList)
                if (p != null && p.ProfileId == profileId)
                {
                    ZombiesAnim.PlayTraverse(p, clip, sill, speed);
                    return;
                }
        }
    }
}
