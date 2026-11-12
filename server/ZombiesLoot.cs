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

