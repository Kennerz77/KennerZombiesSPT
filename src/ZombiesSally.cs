using System;
using System.Linq;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The Pack-a-Punched M1911 (Mustang &amp; Sally, from the PaP mod) in a zombies match: its grenade goes off wherever
    /// it lands, however close (the 40mm round's arming time is skipped), and the blast is smaller (F12 "Sally blast x":
    /// its reach and its explosion graphic, the launcher's own instead of the hand grenade's). Outside zombies, the PaP
    /// mod's Sally is as it was.
    /// </summary>
    internal static class ZombiesSally
    {
        private const string M1911A1 = "5e81c3cbac2bb513793cdc75";
        private const string M381Grenade = "5ede474b0c226a66f5402622";
        private const float BlastScale = 0.6f;

        private static int _depth, _depthFrame = -1;
        private static float _nextTeamLog;
        private static bool _inShot, _sally;
        private const string M32 = "6275303a9f372d6ea97f9ec7";
        private static float _armTime;
        private static object _template;
        private static string _ownEffect;

        public static void Patch(Harmony harmony)
        {
            // Every game world's own ShotDelegate: Fika's client and host worlds override EFT's (no base call), so a
            // patch on EFT's alone never ran in a Fika match: other players' Sally shells landed as duds (user 2026-11-09)
            foreach (var shot in ShotDelegates())
                harmony.Patch(shot, prefix: new HarmonyMethod(typeof(ZombiesSally), nameof(Before)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(ZombiesSally), nameof(After)) { priority = Priority.Last });
            // After the PaP mod's own swaps (they widen the blast and pick the hand grenade's graphic)
            harmony.Patch(AccessTools.GetDeclaredMethods(typeof(Grenade)).First(m => m.Name == nameof(Grenade.Explosion) && m.IsStatic
                    && m.GetParameters().Any(p => p.Name == "grenadeItem")),
                prefix: new HarmonyMethod(typeof(ZombiesSally), nameof(Smaller)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Systems.Effects.Effects), nameof(Systems.Effects.Effects.EmitGrenade)),
                prefix: new HarmonyMethod(typeof(ZombiesSally), nameof(OwnEffect)) { priority = Priority.Last });
        }

        /// <summary>Every game world type's own ShotDelegate (EFT's and any mod's subclasses, Fika's included).</summary>
        internal static System.Collections.Generic.List<System.Reflection.MethodInfo> ShotDelegates()
        {
            var found = new System.Collections.Generic.List<System.Reflection.MethodInfo>();
            foreach (Type t in AccessTools.AllTypes().Where(t => typeof(GameWorld).IsAssignableFrom(t)))
            {
                var m = AccessTools.DeclaredMethod(t, nameof(ClientGameWorld.ShotDelegate), new[] { typeof(Shot) });
                if (m != null && !m.IsAbstract && m.GetMethodBody() != null)
                    found.Add(m);
            }
            ZombiesPlugin.Log.LogInfo($"Zombies shells: shot handling hooked in {found.Count} game world(s): {string.Join(", ", found.Select(m => m.DeclaringType?.Name))}");
            return found;
        }

        private static void Before(Shot shotResult)
        {
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
                // Every 40mm M381 shell in zombies goes off where it lands, however close: Sally (PaP M1911) and the PaP'd
                // M16's M203 alike (user 2026-11-09). The War Machine (M32) has its own rule (ZombiesWarMachine).
                if (!ZombiesMatch.Active || shotResult?.Ammo == null || shotResult.Ammo.TemplateId.ToString() != M381Grenade
                    || shotResult.Weapon?.TemplateId.ToString() == M32)
                    return;
                _sally = shotResult.Weapon?.TemplateId.ToString() == M1911A1;
                object boom = shotResult.Ammo.GetItemComponent<ExplosiveAmmoComponent>();
                object template = boom == null ? null : Traverse.Create(boom).Property("Template").GetValue();
                if (template == null)
                    return;
                Traverse t = Traverse.Create(template);
                _inShot = true;
                _template = template;
                _armTime = t.Field("FuzeArmTimeSec").GetValue<float>();
                _ownEffect = t.Field("ExplosionType").GetValue<string>();
                t.Field("FuzeArmTimeSec").SetValue(0f);
                // A teammate's shell landing here (their Fika copy): logged now and then, to see it's handled
                string by = shotResult.Player?.iPlayer?.ProfileId;
                if (by != null && by != Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId && Time.time > _nextTeamLog)
                {
                    _nextTeamLog = Time.time + 5f;
                    ZombiesDebug.Log($"shells: a teammate's 40mm shell landed here, set off on impact ({(_sally ? "Sally" : shotResult.Weapon?.TemplateId.ToString() ?? "no gun")})");
                }
            }
            catch
            {
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

        private static void Smaller(ref IExplosiveItem grenadeItem)
        {
            if (_inShot && _sally && grenadeItem != null && !(grenadeItem is Scaled))
                grenadeItem = new Scaled(grenadeItem, BlastScale);
        }

        private static void OwnEffect(ref string ename)
        {
            if (_inShot && _sally && !string.IsNullOrEmpty(_ownEffect))
                ename = _ownEffect;
        }

        private sealed class Scaled : IExplosiveItem
        {
            private readonly IExplosiveItem _inner;
            private readonly float _k;

            public Scaled(IExplosiveItem inner, float k)
            {
                _inner = inner;
                _k = k;
            }

            public Vector3 Blindness => _inner.Blindness;
            public Vector3 Contusion => _inner.Contusion;
            public Vector3 ArmorDistanceDistanceDamage => _inner.ArmorDistanceDistanceDamage;
            public float MinExplosionDistance => _inner.MinExplosionDistance * _k;
            public float MaxExplosionDistance => _inner.MaxExplosionDistance * _k;
            public int FragmentsCount => 0;
            public float GetStrength => _inner.GetStrength;
            public bool IsDummy => _inner.IsDummy;
            public Ammo CreateFragment() => _inner.CreateFragment();
        }
    }
}
