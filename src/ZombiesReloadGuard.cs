using System;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// Two EFT throws that left a player gunless for good in Fika (user, 2026-11-10: a box M870 vanished on every draw,
    /// then nothing worked on that player; seen on the headless host):
    /// 1. The host's copy of a teammate's shotgun reload hit an error making a shell's 3D model in the chamber
    ///    (Firearms.SetRoundIntoWeapon: ObjectsFactory.CreateItem NRE), so that reload never finished there.
    /// 2. Each later draw, Fika fast-forwards that unfinished reload; its commit unlocks the shells it took
    ///    (AmmoPack.UnlockItems), one of which no longer has a place (our reserve top-ups move shells about), and
    ///    Item.Parent throws. The draw never happens on the host and the player's hands go empty on their own game.
    /// Here: a shell model that can't be made is skipped (the round still goes in, only its look is missing), and a shell
    /// with no place is skipped while unlocking the others. Nothing changes when nothing is wrong.
    /// </summary>
    internal static class ZombiesReloadGuard
    {
        private static int _roundLogs, _unlockLogs;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(AmmoPack), nameof(AmmoPack.UnlockItems)),
                prefix: new HarmonyMethod(typeof(ZombiesReloadGuard), nameof(UnlockPrefix)));
            harmony.Patch(AccessTools.Method(typeof(Firearms), nameof(Firearms.SetRoundIntoWeapon)),
                finalizer: new HarmonyMethod(typeof(ZombiesReloadGuard), nameof(RoundFinalizer)));
        }

        // EFT's loop, one shell at a time, so one without a place no longer stops the rest (and the reload) dead
        private static bool UnlockPrefix(AmmoPack __instance)
        {
            if (__instance?._ammoPacks == null)
                return true;
            for (int i = 0; i < __instance._ammoPacks.Size; i++)
            {
                Ammo ammo = __instance._ammoPacks[i];
                try
                {
                    if (ammo?.CurrentAddress == null || ammo.Owner == null)
                    {
                        if (_unlockLogs++ < 10)
                            ZombiesPlugin.Log.LogWarning($"Zombies reload guard: a reload's shell had no place any more ({ammo?.Id}): skipped, the reload goes on");
                        continue;
                    }
                    ammo.Owner.RaiseRemoveEvent(new RemoveItemEventArgs(ammo, ammo.Parent, CommandStatus.Failed, ammo.Owner));
                }
                catch (Exception e)
                {
                    if (_unlockLogs++ < 10)
                        ZombiesPlugin.Log.LogWarning($"Zombies reload guard: a reload's shell couldn't be unlocked ({e.Message}): skipped, the reload goes on");
                }
            }
            return false;
        }

        private static Exception RoundFinalizer(Exception __exception)
        {
            if (__exception == null)
                return null;
            if (_roundLogs++ < 10)
                ZombiesPlugin.Log.LogWarning($"Zombies reload guard: a round's model in the chamber couldn't be made ({__exception.GetType().Name}: {__exception.Message}): left out, the reload goes on");
            return null;
        }

        public static void Clear()
        {
            _roundLogs = 0;
            _unlockLogs = 0;
        }
    }
}
