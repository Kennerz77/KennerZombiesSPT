using System;
using System.Collections;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.Ballistics;
using EFT.HealthSystem;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// Teammates' fire doesn't knock you about in a zombies match (user, 2026-11-10):
    /// - a bullet flying past no longer darkens the screen's edges (EFT's flyby vignette), and its whizz and supersonic
    ///   crack aren't played (the gun's own shot is a separate sound and still heard). In a zombies match every bullet
    ///   is a player's (zombies are empty-handed).
    /// - another player's explosion (grenades, launchers, the War Machine, the Ray Gun's bolt: all EFT's one Explosion)
    ///   no longer shakes your camera, wobbles you (contusion) or stuns you. Your own still do, as in the original, unless you
    ///   have PhD Flopper (then none do).
    /// - a player's bullet (or anything else of a player's) hitting a player: no flinch (EFT's ShotReactions) and no blood
    ///   on the hit player's screen (ReceiveDamage, its damage-received event). Fika calls both itself before the damage
    ///   we already block (ZombiesRules), so the hit was still felt. Zombies' hits are untouched.
    /// Nothing else about the explosion changes (its damage rules are elsewhere).
    /// </summary>
    internal static class ZombiesBlastFeel
    {
        private static string _thrower;
        private static bool _inExplosion;
        private static int _logs;

        public static void Patch(Harmony harmony)
        {
            var explosion = AccessTools.GetDeclaredMethods(typeof(ExplosionSharedMethods)).FirstOrDefault(m => m.Name == "Explosion");
            harmony.Patch(explosion, prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(ExplosionPrefix)),
                finalizer: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(ExplosionFinalizer)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ForceEffector), "GrenadeShake_CO"),
                prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(ShakePrefix)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ActiveHealthController), nameof(ActiveHealthController.DoContusion)),
                prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(HealthPrefix)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ActiveHealthController), nameof(ActiveHealthController.DoStun)),
                prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(HealthPrefix)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(FlyingBulletSoundPlayer), nameof(FlyingBulletSoundPlayer.StartVignetteEffect)),
                prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(VignettePrefix)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(FlyingBulletSoundPlayer), nameof(FlyingBulletSoundPlayer.PlaySound)),
                prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(VignettePrefix)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(SonicBulletSoundPlayer), nameof(SonicBulletSoundPlayer.FireBullet)),
                prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(VignettePrefix)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.ShotReactions)),
                prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(ReactionsPrefix)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.ReceiveDamage)),
                prefix: new HarmonyMethod(typeof(ZombiesBlastFeel), nameof(ReceivePrefix)));
        }

        private static void ExplosionPrefix(string playerProfileIDWhoThrew)
        {
            _thrower = playerProfileIDWhoThrew;
            _inExplosion = true;
        }

        private static Exception ExplosionFinalizer(Exception __exception)
        {
            _inExplosion = false;
            _thrower = null;
            return __exception;
        }

        private static Player Me => Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;

        // In someone else's explosion (or any, with PhD Flopper), in a zombies match
        private static bool Others()
        {
            Player me = Me;
            return _inExplosion && ZombiesMatch.Active && me != null && (_thrower != me.ProfileId || ZombiesPhd.Has);
        }

        private static bool ShakePrefix(ref IEnumerator __result)
        {
            if (!Others())
                return true;
            __result = Nothing();
            Note("camera shake");
            return false;
        }

        private static bool HealthPrefix(ActiveHealthController __instance)
        {
            if (!Others() || !ReferenceEquals(__instance, Me?.ActiveHealthController))
                return true;
            Note("contusion/stun");
            return false;
        }

        private static bool VignettePrefix() => !ZombiesMatch.Active;

        // The player whose friendly hit was just dropped, and the frame: Fika's ReceiveDamage comes straight after
        private static Player _friendlyHit;
        private static int _friendlyFrame = -1;

        private static bool ReactionsPrefix(Player __instance, DamageInfo shot)
        {
            if (!ZombiesMatch.Active || __instance == null || ZombiesTeam.IsZombie(__instance))
                return true;
            if (!(shot.Player?.iPlayer is Player from) || ZombiesTeam.IsZombie(from))
                return true;
            _friendlyHit = __instance;
            _friendlyFrame = UnityEngine.Time.frameCount;
            if (_logs++ < 6)
                ZombiesPlugin.Log.LogInfo($"Zombies blasts: {from.Profile?.Nickname}'s hit on {__instance.Profile?.Nickname}: no flinch or blood");
            return false;
        }

        private static bool ReceivePrefix(Player __instance) =>
            !(ZombiesMatch.Active && ReferenceEquals(__instance, _friendlyHit) && UnityEngine.Time.frameCount == _friendlyFrame);

        private static IEnumerator Nothing()
        {
            yield break;
        }

        private static void Note(string what)
        {
            if (_logs++ < 6)
                ZombiesPlugin.Log.LogInfo($"Zombies blasts: {(_thrower == Me?.ProfileId ? "own explosion (PhD)" : "a teammate's explosion")}: {what} skipped here (thrown by {_thrower ?? "nobody known"})");
        }

        public static void Clear() => _logs = 0;
    }
}
