using System.Reflection;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Generators.Loot;
using SPTarkov.Server.Core.Models.Eft.Common;

namespace KennerZombiesServer;

/// <summary>
/// No map loot is made for a zombies map. A zombies match uses no map loot (our maps ship no loot spawns and no
/// containers), so SPT's own result there is already empty: this only makes sure no other mod's loot generator runs
/// on it. Why (player report, 2026-11-12, reproduced on the server): a loot mod replaces SPT's loose loot generator
/// (LocationLootGenerator.GenerateDynamicLoot) for every map, reads its own per-map table holding only the base maps,
/// and threw "The given key 'zm_stronghold' was not present in the dictionary": the raid never loaded.
/// A prefix on GenerateDynamicLoot itself was not enough (the loaded Harmony still ran the other mod's prefix after
/// ours returned false), so this skips one level up: SPT's GenerateLocationLoot, the caller of both the static and the
/// loose loot generators, returns an empty list for zombies maps and never calls them. Every other map is untouched.
/// No other mod is referenced or changed: this patches SPT's own method.
/// </summary>
internal sealed class ZombiesNoLootPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        typeof(LocationLootGenerator).GetMethod(nameof(LocationLootGenerator.GenerateLocationLoot));

    [PatchPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(string locationId, ref List<SpawnpointTemplate> __result)
    {
        if (!ZombiesRaid.IsZombiesMap(locationId))
            return true;
        __result = new List<SpawnpointTemplate>();
        ZombiesRaid.Log?.Info($"[Zombies] {locationId}: no map loot on a zombies map (loot generators skipped)");
        return false;
    }
}

/// <summary>
/// A zombies match is only its 1-4 players and the core's own zombies (user, 2026-11-12). Our maps ship no boss
/// spawns and no waves, but other mods add them to every map (a team of bosses, extra waves): the raid's location
/// handed to the game has them cleared for zombies maps, so none of them reach a match. The client refuses and
/// removes any AI that still turns up. Every other map is untouched; no other mod is referenced or changed.
/// </summary>
internal sealed class ZombiesNoBotsPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        typeof(SPTarkov.Server.Core.Services.InRaid.LocationLifecycleService).GetMethod(
            nameof(SPTarkov.Server.Core.Services.InRaid.LocationLifecycleService.GenerateLocationAndLoot));

    [PatchPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(string name, LocationBase __result)
    {
        if (__result == null || !ZombiesRaid.IsZombiesMap(name))
            return;
        int bosses = __result.BossLocationSpawn?.Count ?? 0, waves = __result.Waves?.Count ?? 0;
        if (bosses == 0 && waves == 0)
            return;
        __result.BossLocationSpawn = new List<BossLocationSpawn>();
        __result.Waves = new List<Wave>();
        ZombiesRaid.Log?.Info($"[Zombies] {name}: {bosses} boss spawn(s) and {waves} wave(s) added by other mods cleared (zombies matches have only our zombies)");
    }
}
