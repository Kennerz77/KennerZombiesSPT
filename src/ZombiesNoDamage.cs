using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// In a zombies match nothing hurts a player's EFT body (no red legs, no damage screen): every ApplyDamage on a
    /// player's health is dropped (zombies' slaps are counted by ZombiesSlaps instead). A fall from a height (upstairs
    /// down) instead gives the original's knock: a grunt, the screen's edges red for a moment and a second of walking slowly.
    /// Zombies (AI) still take their damage.
    /// </summary>
    internal static class ZombiesNoDamage
    {
        private const float RedSeconds = 1.2f, SlowSeconds = 1.1f;
        private static float _fellAt = -100f, _strength;

        public static void Patch(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.ApplyDamage),
                    new[] { typeof(EBodyPart), typeof(float), typeof(DamageInfo) }),
                prefix: new HarmonyMethod(typeof(ZombiesNoDamage), nameof(NoDamage)));

        private static bool NoDamage(ActiveHealthController __instance, float damage, DamageInfo damageInfo, ref float __result)
        {
            if (!ZombiesMatch.Active)
                return true;
            Player p = __instance?.Player;
            if (p == null)
                return true;
            // Falls never hurt zombies: they drop 4.5 m through the floor hole into zm_stronghold's basement on a
            // one-way nav link (2026-11-08), and EFT's fall damage would break their legs or kill them
            if (damageInfo.DamageType == EDamageType.Fall && ZombiesTeam.IsZombie(p))
            {
                __result = 0f;
                return false;
            }
            if (p.IsAI)
                return true;
            __result = 0f;
            if (damageInfo.DamageType == EDamageType.Fall && p.IsYourPlayer && Time.time - _fellAt > 0.5f)
                Fell(p, damage);
            return false;
        }

        private static void Fell(Player me, float damage)
        {
            _fellAt = Time.time;
            _strength = Mathf.Clamp01(0.35f + damage / 30f);
            ZombiesControls.SlowUntil = Time.time + SlowSeconds;
            try
            {
                me.Say(EPhraseTrigger.OnBeingHurt, true);
            }
            catch
            {
            }
            ZombiesDebug.Log($"fell (EFT would have done {damage:0.0} to each leg): knocked");
        }

        public static void Draw()
        {
            float t = (Time.time - _fellAt) / RedSeconds;
            if (t >= 0f && t < 1f)
                ZombiesSlaps.DrawRed(_strength * (1f - t));
        }
    }
}
