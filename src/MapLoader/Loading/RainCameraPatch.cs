using System;
using System.Reflection;
using EFT.CameraControl;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KennerZombies.MapLoader.Loading
{
    // The full weather system's RainController grabs the camera's screen rain effects when the
    // player camera is created: Controller._rainScreenDrops = EffectsController.RainScreenDrops,
    // then calls Init() on it unchecked. Retail maps set LevelSettings.CameraPrefab to a camera that
    // has RainScreenDrops; SDK maps use the game's fallback camera, which does not, so the call
    // throws and aborts the raid start. Every later use of the field is null-checked, so with the
    // exception swallowed rain simply runs without drops on the screen.
    internal class RainCameraPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            var state = AccessTools.Inner(typeof(RainController), "AbstractStateSummer");
            return AccessTools.Method(state, "CameraChanged", new[] { typeof(CameraManager) });
        }

        [PatchFinalizer]
        private static Exception Finalizer(Exception __exception, CameraManager cameraManager)
        {
            if (__exception is NullReferenceException
                && cameraManager?.EffectsController != null
                && cameraManager.EffectsController.RainScreenDrops == null)
            {
                Loader.Verbose("Camera has no RainScreenDrops; rain runs without drops on the screen");
                return null;
            }
            return __exception;
        }
    }
}
