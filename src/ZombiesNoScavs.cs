using Comfort.Common;
using EFT;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// No scavs on a zombies map (user, 2026-11-09: scavs popped into existence on the player spawns). The map's
    /// settings had EFT's "new spawn" system on (NewSpawn / OfflineNewSpawn): its NonWavesSpawnScenario spawns scavs
    /// over time even with no waves, and with no bot spawn points it used the player spawns. On a zombies map it never
    /// runs; normal maps are untouched. ZombiesRounds.RemoveStrays still takes off anything that gets through.
    /// </summary>
    internal static class ZombiesNoScavs
    {
        public static void Patch(Harmony harmony)
        {
            var run = AccessTools.Method(typeof(NonWavesSpawnScenario), nameof(NonWavesSpawnScenario.Run));
            var update = AccessTools.Method(typeof(NonWavesSpawnScenario), nameof(NonWavesSpawnScenario.Update));
            var skip = new HarmonyMethod(typeof(ZombiesNoScavs), nameof(Skip));
            if (run != null)
                harmony.Patch(run, prefix: skip);
            if (update != null)
                harmony.Patch(update, prefix: skip);
            ZombiesPlugin.Log.LogInfo($"Zombies: EFT's scav spawning held off on zombies maps ({(run != null ? "run" : "-")}, {(update != null ? "update" : "-")})");
        }

        private static bool _logged;

        private static bool Skip()
        {
            string location = Singleton<GameWorld>.Instance?.LocationId;
            if (!ZombiesMaps.Is(location))
                return true;
            if (!_logged)
            {
                _logged = true;
                ZombiesPlugin.Log.LogInfo($"Zombies: EFT's own scav spawning is off on {location}");
            }
            return false;
        }
    }
}
