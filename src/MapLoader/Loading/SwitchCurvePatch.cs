using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // A switch animates by its own CustomProgressCurve (doors use the game's EFTHardSettings.DoorCurve),
    // and InteractionState.InitSmoothOpen reads its keys unchecked. Switches the SDK made before it set
    // that curve have an empty one: interacting threw IndexOutOfRangeException before the switch changed
    // state, so it made no sound and never reached its door or lamps. An empty curve now falls back to
    // the door curve; rebuilt maps carry their own.
    internal class SwitchCurvePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.PropertyGetter(typeof(Switch), "ProgressCurve");

        [PatchPostfix]
        private static void Postfix(ref AnimationCurve __result)
        {
            if (__result != null && __result.length > 0)
                return;
            __result = EFTHardSettings.Instance.DoorCurve;
        }
    }
}
