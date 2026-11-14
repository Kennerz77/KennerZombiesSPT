using EFT.InventoryLogic;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// In a zombies match every item counts as examined. EFT only hands back a gun the player has examined: after a
    /// quick knife, a drink or a med, Player.TrySetLastEquippedWeapon asks IsItemCanBeEquipped -> IsAtReachablePlace,
    /// which needs InventoryController.Examined(item). A gun that isn't examined by default (the Desert Eagle L6) and
    /// that the player's profile never examined was refused: after a perk drink the knife came out instead, and after a
    /// quick knife nothing came back at all (user, 2026-11-14, with the Desert Eagle as a map's starting gun; the same
    /// happened with any such gun from the box or a wall). The inventory is never opened in a match, so nothing is
    /// learnt from examining there; the player's profile is left as it is (only the answer is changed, nothing saved).
    /// </summary>
    internal static class ZombiesExamined
    {
        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(InventoryController), nameof(InventoryController.Examined), new[] { typeof(Item) }),
                postfix: new HarmonyMethod(typeof(ZombiesExamined), nameof(Postfix)));
        }

        private static void Postfix(ref bool __result)
        {
            if (!__result && ZombiesMatch.Active)
                __result = true;
        }
    }
}
