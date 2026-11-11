using System.Collections;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using Systems.Effects;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The War Machine (the Milkor M32A1 from the box) in a zombies match:
    /// - Pack-a-Punched: every shell goes off where it lands, however close (its arming time skipped), as the Sally.
    /// - Plain: an M67 on a 4 s fuse (M67 damage, ZombiesShells). The shell doesn't go off on landing: it lies there blinking red and goes off
    ///   <see cref="FuseTime"/> after it was fired, with EFT's own explosion (damage, points).
    /// Hooked on ClientGameWorld.ShotDelegate (a projectile hitting something), before EFT checks the arming time.
    /// </summary>
    internal static class ZombiesWarMachine
    {
        private const string M32 = "6275303a9f372d6ea97f9ec7";
        // The plain War Machine's M67s: 4 s from the shot (user, 2026-11-09; hand grenades stay 5 s)
        private const float FuseTime = 4f;

        private static int _depth, _depthFrame = -1;
        private static bool _inShot;
        private static float _armTime;
        private static object _template;

        public static void Patch(Harmony harmony)
        {
            foreach (var shot in ZombiesSally.ShotDelegates())
                harmony.Patch(shot, prefix: new HarmonyMethod(typeof(ZombiesWarMachine), nameof(Before)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(ZombiesWarMachine), nameof(After)) { priority = Priority.Last });
        }

        private static void Before(GameWorld __instance, Shot shotResult)
        {
            // A world calling its base ShotDelegate runs this twice: only the outer one counts (else the arming time
            // would be "restored" to the 0 set by the outer one and stay there)
            if (Time.frameCount != _depthFrame)
            {
                _depthFrame = Time.frameCount;
                _depth = 0;
            }
            if (_depth++ > 0)
                return;
            _inShot = false;
            try
            {
                if (!ZombiesMatch.Active || shotResult?.Ammo == null || shotResult.Weapon?.TemplateId.ToString() != M32)
                    return;
                ExplosiveAmmoComponent boom = shotResult.Ammo.GetItemComponent<ExplosiveAmmoComponent>();
                object template = boom == null ? null : Traverse.Create(boom).Property("Template").GetValue();
                if (template == null)
                    return;
                Traverse t = Traverse.Create(template);
                _inShot = true;
                _template = template;
                _armTime = t.Field("FuzeArmTimeSec").GetValue<float>();
                bool papped = ZombiesPaP.Infused(shotResult.Weapon);
                // Papped: off on impact; plain: never on impact (a dud there), ours goes off on its fuse instead
                t.Field("FuzeArmTimeSec").SetValue(papped ? 0f : 99999f);
                if (!papped && shotResult.IsFirstHit)
                {
                    float left = Mathf.Max(0.15f, FuseTime - shotResult.TimeSinceShot);
                    var calc = Traverse.Create(__instance).Property("SharedBallisticsCalculator").GetValue<IBallisticsCalculator>()
                               ?? Traverse.Create(__instance).Field("SharedBallisticsCalculator").GetValue<IBallisticsCalculator>();
                    string who = shotResult.Player?.iPlayer?.ProfileId;
                    string effect = t.Field("ExplosionType").GetValue<string>();
                    ZombiesPlugin.Instance.StartCoroutine(Fuse(boom, shotResult.HitPoint, shotResult.HitNormal, who, calc, shotResult.Weapon, effect, left));
                }
            }
            catch (System.Exception e)
            {
                ZombiesDebug.Log($"war machine: {e.Message}");
            }
        }

        private static void After()
        {
            if (_depth > 0 && --_depth > 0)
                return;
            if (!_inShot)
                return;
            _inShot = false;
            try
            {
                Traverse.Create(_template).Field("FuzeArmTimeSec").SetValue(_armTime);
            }
            catch
            {
            }
            _template = null;
        }

        // The shell lying where it landed, a red light blinking faster as the fuse runs down, then the bang
        private static IEnumerator Fuse(ExplosiveAmmoComponent boom, Vector3 at, Vector3 normal, string who, IBallisticsCalculator calc,
            Item weapon, string effect, float seconds)
        {
            var go = new GameObject("Zombies_WarMachineShell");
            go.transform.position = at + normal * 0.08f;
            Light blink = go.AddComponent<Light>();
            blink.color = new Color(1f, 0.15f, 0.1f);
            blink.range = 1.5f;
            float end = Time.time + seconds;
            while (Time.time < end && ZombiesMatch.Active)
            {
                float left = end - Time.time;
                blink.intensity = Mathf.Repeat(Time.time * Mathf.Lerp(8f, 2f, left / FuseTime), 1f) < 0.5f ? 1.2f : 0f;
                yield return null;
            }
            Object.Destroy(go);
            if (!ZombiesMatch.Active)
                yield break;
            try
            {
                if (Singleton<Effects>.Instantiated && !string.IsNullOrEmpty(effect))
                    Singleton<Effects>.Instance.EmitGrenade(effect, at, normal, 0);
                if (calc != null)
                    Grenade.Explosion(null, boom, at, who, calc, weapon, normal * 0.08f);
                ZombiesDebug.Log($"war machine: shell went off at {at} after its fuse");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: the War Machine shell's blast failed ({e.Message})");
            }
        }
    }
}
