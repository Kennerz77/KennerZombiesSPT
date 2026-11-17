using System.Reflection;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.Audio;

namespace KennerZombies.MapLoader.Loading
{
    // Sounds a mapper left empty. Retail objects always have theirs, so the game never checks:
    //
    //  - NullClipPatch / NullClipQueuedPatch: BetterAudio.PlayAtPoint(.., AudioClip, ..) borrows a source
    //    and then reads clip.length, so a KeycardDoor without its granted or unlock sound threw on every
    //    unlock and leaked the source
    //  - BarbedWireSoundPatch: BarbedWire.PlaySound plays its SoundBank unchecked on every touch
    internal class NullClipPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BetterAudio), nameof(BetterAudio.PlayAtPoint), new[]
        {
            typeof(Vector3), typeof(AudioClip), typeof(float), typeof(BetterAudio.AudioSourceGroupType), typeof(int), typeof(float),
            typeof(EOcclusionTest), typeof(AudioMixerGroup), typeof(bool),
        });

        [PatchPrefix]
        private static bool Prefix(AudioClip clip, ref BetterSource __result)
        {
            if (clip != null || !MapRegistry.IsCustomMapActive())
                return true;
            __result = null;
            return false;
        }
    }

    internal class NullClipQueuedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BetterAudio), nameof(BetterAudio.PlayAtPoint), new[]
        {
            typeof(Vector3), typeof(AudioClip), typeof(BetterAudio.AudioSourceGroupType), typeof(int), typeof(float), typeof(EOcclusionTest),
            typeof(AudioMixerGroup), typeof(bool), typeof(bool), typeof(bool), typeof(bool),
        });

        [PatchPrefix]
        private static bool Prefix(AudioClip clip, ref BetterSource __result)
        {
            if (clip != null || !MapRegistry.IsCustomMapActive())
                return true;
            __result = null;
            return false;
        }
    }

    internal class BarbedWireSoundPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BarbedWire), nameof(BarbedWire.PlaySound));

        [PatchPrefix]
        private static bool Prefix(BarbedWire __instance) => __instance._soundBank != null;
    }
}
