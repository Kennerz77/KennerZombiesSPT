using System;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's hipfire, for your own shots in a zombies match: from the hip a shot leaves somewhere inside a wide cone
    /// round where the gun points, and the cone blooms with every shot (falling back once you stop), widens while you
    /// move and a lot in the air, and narrows crouched or prone. Aiming down the sights is unchanged (tight). Deadshot
    /// Daiquiri tightens the hipfire (the original: 35% less spread). The crosshair opens and closes with it
    /// (<see cref="CurrentDegrees"/>). Applied to the shot's direction in Player.FirearmController.InitiateShot (and
    /// the client controller's override, which records the direction first), once per shot.
    /// </summary>
    internal static class ZombiesHipfire
    {
        private static ConfigEntry<float> _base, _perShot, _most, _moving;
        private static float _bloom, _lastShot;
        [ThreadStatic] private static bool _done;

        private const float BloomHold = 0.12f, BloomFall = 7f, AirExtra = 4f;

        public static void Bind(ConfigFile config, string section)
        {
            _base = config.Bind(section, "Hipfire spread (degrees)", 1.2f, new ConfigDescription(
                "How wide shots from the hip spread at rest. Aiming down the sights stays tight.", new AcceptableValueRange<float>(0f, 15f)));
            _perShot = config.Bind(section, "Hipfire bloom per shot (degrees)", 0.6f, new ConfigDescription(
                "How much wider each shot from the hip makes the next.", new AcceptableValueRange<float>(0f, 5f)));
            _most = config.Bind(section, "Hipfire bloom most (degrees)", 4.5f, new ConfigDescription(
                "The most the bloom adds on top of the rest spread.", new AcceptableValueRange<float>(0f, 20f)));
            _moving = config.Bind(section, "Hipfire moving extra (degrees)", 2.5f, new ConfigDescription(
                "Extra spread while walking.", new AcceptableValueRange<float>(0f, 10f)));
        }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot)),
                prefix: new HarmonyMethod(typeof(ZombiesHipfire), nameof(Spread)) { priority = Priority.First });
            var client = AccessTools.DeclaredMethod(typeof(ClientFirearmController), nameof(ClientFirearmController.InitiateShot));
            if (client != null)
                harmony.Patch(client, prefix: new HarmonyMethod(typeof(ZombiesHipfire), nameof(SpreadClient)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(ZombiesHipfire), nameof(ClientDone)));
        }

        private static Player Me => Singleton<GameWorld>.Instance?.MainPlayer;

        // The client's override runs first (it records the direction for the network), then EFT's own: spread once
        private static void SpreadClient(Player.FirearmController __instance, ref Vector3 shotDirection)
        {
            Apply(__instance, ref shotDirection);
            _done = true;
        }

        private static Exception ClientDone(Exception __exception)
        {
            _done = false;
            return __exception;
        }

        private static void Spread(Player.FirearmController __instance, ref Vector3 shotDirection)
        {
            if (!_done)
                Apply(__instance, ref shotDirection);
        }

        private static void Apply(Player.FirearmController controller, ref Vector3 dir)
        {
            try
            {
                Player me = Me;
                if (!ZombiesMatch.Active || me == null || controller == null || !ReferenceEquals(me.HandsController, controller))
                    return;
                float cone = CurrentDegrees(me);
                // The next shot blooms (aimed shots too: let go of the sights mid-burst and it's already wide)
                _bloom = Mathf.Min(_most?.Value ?? 4.5f, _bloom + (_perShot?.Value ?? 0.6f));
                _lastShot = Time.time;
                if (cone <= 0.01f || dir.sqrMagnitude < 0.0001f)
                    return;
                // A point inside the cone, evenly over its area
                float r = cone * Mathf.Sqrt(UnityEngine.Random.value), a = UnityEngine.Random.value * Mathf.PI * 2f;
                Vector3 d = dir.normalized;
                Vector3 right = Vector3.Cross(Vector3.up, d);
                if (right.sqrMagnitude < 0.0001f)
                    right = Vector3.right;
                right.Normalize();
                Vector3 up = Vector3.Cross(d, right);
                dir = Quaternion.AngleAxis(r * Mathf.Cos(a), up) * Quaternion.AngleAxis(r * Mathf.Sin(a), right) * d;
            }
            catch
            {
                // EFT's own shot
            }
        }

        /// <summary>Each frame of a match: the bloom falls back once you stop shooting.</summary>
        public static void Tick()
        {
            if (Time.time - _lastShot > BloomHold)
                _bloom = Mathf.MoveTowards(_bloom, 0f, BloomFall * Time.deltaTime);
        }

        /// <summary>The hipfire cone's half-angle right now in degrees (0 aiming down the sights).</summary>
        public static float CurrentDegrees(Player me)
        {
            if (me == null || me.ProceduralWeaponAnimation == null || me.ProceduralWeaponAnimation.IsAiming)
                return 0f;
            if (!(me.HandsController?.Item is Weapon gun) || ZombiesAmmo.IsWonderWeapon(gun))
                return 0f;
            float cone = (_base?.Value ?? 1.2f) + _bloom;
            MovementContext m = me.MovementContext;
            if (m != null)
            {
                if (!m.IsGrounded)
                    cone += AirExtra;
                else if (m.ActualLinearVelocity > 0.5f)
                    cone += (_moving?.Value ?? 2.5f) * Mathf.Clamp01(m.ActualLinearVelocity / 3f);
                if (m.IsInPronePose)
                    cone *= 0.6f;
                else if (m.PoseLevel < 0.5f)
                    cone *= 0.8f;
            }
            // Shotguns already throw their pellets wide: a little less on top
            if (gun.Template?.ammoCaliber == "Caliber12g" || gun.Template?.ammoCaliber == "Caliber20g")
                cone *= 0.6f;
            return cone * ZombiesPerks.HipFactor;
        }

        /// <summary>The crosshair's gap for the current spread, in pixels from the centre.</summary>
        public static float CrosshairGap(Player me, Camera cam)
        {
            float deg = CurrentDegrees(me);
            if (deg <= 0f || cam == null)
                return 0f;
            return Mathf.Tan(deg * Mathf.Deg2Rad) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * Screen.height * 0.5f;
        }
    }
}
