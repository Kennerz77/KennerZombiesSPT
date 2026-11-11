using System.Reflection;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's stamina in a zombies match: short sprint bursts. The green bar (sprint) runs out quickly but comes back very
    /// fast, and running it empty costs nothing (no fatigue, no heavy breathing, you just walk until it's back). The blue
    /// bar (arms: aiming, melee) and the lungs never drain. Jumps are a little lower. Recovery is the player's own (their
    /// Physical's restore rates, set on your own player); the sprint drain is EFT's shared setting, so it's put back
    /// exactly the moment the match ends (normal raids never see it). F12 "5. Zombies match (dev)".
    /// </summary>
    internal static class ZombiesStamina
    {
        private static ConfigEntry<float> _regen, _drain, _jump, _swap;

        /// <summary>How much quicker guns are put away and drawn in a zombies match (everyone; Speed Cola on top).</summary>
        public static float SwapSpeed => Mathf.Max(0.5f, _swap?.Value ?? 1.6f);
        private static bool _jumpPatched;
        private static float? _origDrain;
        private static object _physical;
        private static float _origRestore, _origHands;

        private static ConfigEntry<float> _speed, _refill;

        /// <summary>Everyone's walk/sprint speed factor in a match (ZombiesPerks' speed postfixes).</summary>
        public static float PlayerSpeed => _speed?.Value ?? 1.15f;

        public static void Bind(ConfigFile config, string section)
        {
            _regen = config.Bind(section, "Stamina recovery x", 47f, new ConfigDescription(
                "How much faster stamina comes back in a zombies match.", new AcceptableValueRange<float>(1f, 60f)));
            _drain = config.Bind(section, "Sprint drain x", 8f, new ConfigDescription(
                "How much faster sprinting uses stamina in a zombies match (short bursts of a few seconds).", new AcceptableValueRange<float>(0.5f, 10f)));
            _jump = config.Bind(section, "Jump height x", 0.7f, new ConfigDescription(
                "Jump height in a zombies match (1 = the game's own).", new AcceptableValueRange<float>(0.5f, 1f)));
            _speed = config.Bind(section, "Player speed x", 1.15f, new ConfigDescription(
                "How much faster everyone walks and sprints in a zombies match (Stamin-Up adds its own on top).", new AcceptableValueRange<float>(1f, 2f)));
            _refill = config.Bind(section, "Stamina refill (s)", 1.84f, new ConfigDescription(
                "Seconds from an empty stamina bar to full once you stop sprinting, with no wait first (Stamin-Up: twice as quick).",
                new AcceptableValueRange<float>(0.5f, 20f)));
            _swap = config.Bind(section, "Weapon swap speed x", 1.6f, new ConfigDescription(
                "How much quicker guns are put away and drawn in a zombies match, for everyone (Speed Cola adds its own on top).",
                new AcceptableValueRange<float>(1f, 3f)));
        }

        public static void Apply(Player player)
        {
            Restore();
            try
            {
                var stamina = Singleton<GlobalConfiguration>.Instance.Stamina;
                _origDrain = stamina.SprintDrainRate;
                stamina.SprintDrainRate = _origDrain.Value * _drain.Value;
                object physical = player?.Physical;
                FieldInfo restore = physical == null ? null : AccessTools.Field(physical.GetType(), "StaminaRestoreRate");
                FieldInfo hands = physical == null ? null : AccessTools.Field(physical.GetType(), "HandsRestoreRate");
                if (restore != null && hands != null)
                {
                    _physical = physical;
                    _origRestore = (float)restore.GetValue(physical);
                    _origHands = (float)hands.GetValue(physical);
                    restore.SetValue(physical, _origRestore * _regen.Value);
                    hands.SetValue(physical, _origHands * _regen.Value);
                }
                ZombiesPlugin.Log.LogInfo($"Zombies stamina: recovery x{_regen.Value}, sprint drain x{_drain.Value}");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies stamina: {e.Message}");
            }
        }

        /// <summary>
        /// Every frame on your own player: the arms and lungs stay full, and running the green bar empty leaves no
        /// fatigue or overuse behind.
        /// </summary>
        public static void Tick(Player player)
        {
            if (!ZombiesInertia.Tweaks)
                return;
            var physical = player?.Physical;
            if (physical == null)
                return;
            // Stamin-Up: sprinting lasts twice as long while it's had
            if (_origDrain.HasValue && Singleton<GlobalConfiguration>.Instantiated)
                Singleton<GlobalConfiguration>.Instance.Stamina.SprintDrainRate = _origDrain.Value * _drain.Value * ZombiesPerks.SprintDrainFactor;
            Top(physical.HandsStamina);
            Top(physical.Oxygen);
            // Our own refill whenever you're not sprinting, at once: EFT held an empty bar a while before refilling
            // (the user, 2026-11-08: "a delay before refill, remove that")
            Stamina st = physical.Stamina;
            if (st != null && !physical.Sprinting && st.Current < st.TotalCapacity)
            {
                float full = Mathf.Max(0.5f, _refill?.Value ?? 4f) * (ZombiesPerks.SprintDrainFactor < 1f ? 0.5f : 1f);
                st.Current = Mathf.Min(st.TotalCapacity, st.Current + st.TotalCapacity * Time.deltaTime / full);
            }
            if (physical.Stamina != null)
                physical.Stamina.Overuse = 0f;
            physical.Fatigue = 0f;
        }

        private static void Top(Stamina s)
        {
            if (s != null && s.Current < s.TotalCapacity)
                s.Current = s.TotalCapacity;
        }

        /// <summary>The jump's lift, lowered for your own player in a zombies match (JumpPlayerState.Enter postfix).</summary>
        public static void PatchJump(Harmony harmony)
        {
            if (_jumpPatched)
                return;
            _jumpPatched = true;
            harmony.Patch(AccessTools.Method(typeof(JumpPlayerState), nameof(JumpPlayerState.Enter)),
                postfix: new HarmonyMethod(typeof(ZombiesStamina), nameof(LowerJump)));
        }

        private static readonly AccessTools.FieldRef<MovementContext, Player> ContextPlayer = AccessTools.FieldRefAccess<MovementContext, Player>("_player");

        private static void LowerJump(JumpPlayerState __instance)
        {
            if (!ZombiesMatch.Active || _jump == null || __instance.MovementContext == null)
                return;
            Player p = ContextPlayer(__instance.MovementContext);
            if (p != null && p.IsYourPlayer)
                __instance._skilledBoost *= _jump.Value;
        }

        /// <summary>EFT's own stamina again (match over).</summary>
        public static void Restore()
        {
            try
            {
                if (_origDrain.HasValue && Singleton<GlobalConfiguration>.Instantiated)
                    Singleton<GlobalConfiguration>.Instance.Stamina.SprintDrainRate = _origDrain.Value;
                if (_physical != null)
                {
                    AccessTools.Field(_physical.GetType(), "StaminaRestoreRate")?.SetValue(_physical, _origRestore);
                    AccessTools.Field(_physical.GetType(), "HandsRestoreRate")?.SetValue(_physical, _origHands);
                }
            }
            catch
            {
            }
            _origDrain = null;
            _physical = null;
        }
    }
}
