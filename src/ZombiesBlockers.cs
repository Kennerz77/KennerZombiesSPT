using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The map's player blockers (Unity's Kennerz > Blocker Tool: KZ_Blockers, and any NoClimb_* box) stop players only:
    /// each zombie is told to ignore them (Physics.IgnoreCollision with every collider of its body), so zombies walk
    /// and climb through where players can't, up to and over the props under a barricade, and never get caught on a
    /// blocker their navmesh route doesn't know about (the user, 2026-11-08).
    /// </summary>
    internal static class ZombiesBlockers
    {
        private static List<Collider> _blockers;
        private static readonly HashSet<Player> Done = new HashSet<Player>();

        public static void Clear()
        {
            _blockers = null;
            Done.Clear();
        }

        private static List<Collider> Blockers()
        {
            if (_blockers != null)
                return _blockers;
            // Once per match (FindObjectsOfType is far too slow for anything that repeats)
            _blockers = Object.FindObjectsOfType<Collider>()
                .Where(c => c != null && (c.name.StartsWith("NoClimb_") || c.transform.root.name == "KZ_Blockers" || c.transform.root.name == "KZ_NoClimb"))
                .ToList();
            ZombiesPlugin.Log.LogInfo($"Zombies blockers: {_blockers.Count} player-only blocker(s) zombies pass through");
            return _blockers;
        }

        /// <summary>A zombie (once): it passes through every blocker.</summary>
        public static void Ignore(Player zombie)
        {
            if (zombie == null || !Done.Add(zombie))
                return;
            List<Collider> blockers = Blockers();
            if (blockers.Count == 0)
                return;
            foreach (Collider mine in zombie.GetComponentsInChildren<Collider>(true))
                foreach (Collider b in blockers)
                    if (b != null && mine != null)
                        Physics.IgnoreCollision(mine, b, true);
        }
    }
}
