using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// A Ray Gun bolt's blast knocks only the shooter's screen in a zombies match (user, 2026-11-09). The bolt is an
    /// explosive round, and EFT's explosion code (ExplosionSharedMethods.AffectPlayersShared, run on every game) shakes
    /// the local player's view (ForceReact.GrenadeShake_CO) and stuns players near it (ActiveHealthController
    /// .DoContusion). While a Ray Gun cell's blast is being worked out, both are skipped for everyone but its shooter.
    /// Other explosions and normal raids are untouched; the Ray Gun mod itself is unchanged.
    /// </summary>
    internal static class ZombiesRayBlast
    {
        private static bool _inRayBlast;
        private static string _shooter;

        public static void Patch(Harmony harmony)
        {
            MethodInfo affect = AccessTools.Method(typeof(ExplosionSharedMethods), nameof(ExplosionSharedMethods.AffectPlayersShared));
            harmony.Patch(affect, prefix: new HarmonyMethod(typeof(ZombiesRayBlast), nameof(Before)),
                finalizer: new HarmonyMethod(typeof(ZombiesRayBlast), nameof(After)));
            harmony.Patch(AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DoContusion)),
                prefix: new HarmonyMethod(typeof(ZombiesRayBlast), nameof(NoContusion)));
            // The shake's class has an obfuscated home: found by its method's name
            MethodInfo shake = typeof(Player).Assembly.GetTypes()
                .Select(t => t.GetMethod("GrenadeShake_CO", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .FirstOrDefault(m => m != null && m.ReturnType == typeof(IEnumerator));
            if (shake != null)
                harmony.Patch(shake, prefix: new HarmonyMethod(typeof(ZombiesRayBlast), nameof(NoShake)));
            ZombiesPlugin.Log.LogInfo($"Zombies: Ray Gun blasts knock only their shooter (shake {(shake != null ? "found" : "NOT found")})");
        }

        private static void Before(IExplosiveItem grenadeItem, Func<DamageInfo> getDamageInfo)
        {
            _inRayBlast = false;
            _shooter = null;
            if (!ZombiesMatch.Active || !(grenadeItem is Item item) || ZombiesAmmo.RaygunCellLevel(item) <= 0 && !IsRayCell(item))
                return;
            try
            {
                _shooter = getDamageInfo?.Invoke().Player?.iPlayer?.ProfileId;
            }
            catch
            {
                _shooter = null;
            }
            _inRayBlast = true;
        }

        private static Exception After(Exception __exception)
        {
            _inRayBlast = false;
            _shooter = null;
            return __exception;
        }

        // The Ray Gun's cells (the round in a bolt), by template id prefix
        private static bool IsRayCell(Item item) => item.TemplateId.ToString().StartsWith("6a7e5a71d9a11600000000");

        private static bool Spared(string profileId) => _inRayBlast && (string.IsNullOrEmpty(_shooter) || profileId != _shooter);

        private static bool NoContusion(ActiveHealthController __instance) => !Spared(__instance?.Player?.ProfileId);

        private static bool NoShake(ref IEnumerator __result)
        {
            string me = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
            if (!Spared(me))
                return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty()
        {
            yield break;
        }
    }
}
