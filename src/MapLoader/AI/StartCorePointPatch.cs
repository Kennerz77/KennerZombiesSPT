using System.Reflection;
using EFT;
using HarmonyLib;
using KennerZombies.MapLoader.Loading;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.AI
{
    // Every bot keeps the AICorePoint it spawned at (BotOwner.StartCorePoint) and passes its
    // ConnectionGroupId to the path finder on its first move. The core is looked up from the spawn
    // point's CorePointId, which retail maps bake to a real core. SDK spawn points carry 0, while
    // the generated cores are numbered from 1, so the lookup returns null and
    // BotPathFinderCorePoints.GoToPosition throws inside BotOwner activation: the bot ends in
    // BotState.ActiveFail and stands still.
    //
    // On a custom map a missing core is replaced with the generated core nearest the bot.
    internal class StartCorePointPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotOwner), nameof(BotOwner.Create));
        }

        [PatchPrefix]
        private static void Prefix(Player player, BotsController botsController, ref AICorePoint corePointId)
        {
            if (corePointId != null || !MapRegistry.IsCustomMapActive())
                return;

            var holder = botsController?.CoversData?.AICorePointsHolder;
            if (holder == null || holder.CorePoints == null || holder.CorePoints.Count == 0)
            {
                Loader.Log.LogWarning("Custom map has no AI core points; the bot will not be able to move.");
                return;
            }

            Vector3 position = player != null ? player.Position : Vector3.zero;
            corePointId = NavGraphBuilder.NearestCore(holder.CorePoints, position);
            Loader.Verbose($"Bot at {position} had no start core point; using core {corePointId?.Id} (group {corePointId?.ConnectionGroupId})");
        }
    }
}
