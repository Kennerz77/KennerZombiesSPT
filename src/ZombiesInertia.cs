using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// No movement inertia in a zombies match (the original: you start, stop and change direction at once). EFT smooths your walking
    /// speed so you ramp up and slide to a halt, heavier gear more so. Every frame on your own player: the walk smoothing
    /// time (WalkInertia) and the sprint brake (SprintBrakeInertia) are zero, the side and diagonal smoothing are zero, the
    /// view tilt from changing direction is off, and EFT's shared "sudden changes" smoothing, which eases the speed your
    /// animation sees, is made instant for the match (put back exactly when it ends: normal raids never see it).
    /// F12 "5. Zombies match (dev)": "No movement inertia".
    /// </summary>
    internal static class ZombiesInertia
    {
        private static ConfigEntry<bool> _on;
        private static float? _origSmoothness, _origBlend;
        private static Vector2? _origMoveTime, _origPreserve, _origBrake, _origSprintAcc, _origPreSprintAcc;
        private static float? _origLerp;

        /// <summary>The walk's direction smoothing (MovePlayerState.ProcessDirection's smooth time) off in a match.</summary>
        public static void Patch(Harmony harmony)
        {
            var m = AccessTools.Method(typeof(MovePlayerState), "ProcessDirection");
            if (m != null)
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesInertia), nameof(NoSmoothing)));
        }

        private static void NoSmoothing(ref float smoothTime)
        {
            // Quick but not instant: a zero smoothing time made EFT's speed snap and overshoot each frame (the player
            // stepped about, as if teleporting)
            if (_on?.Value == true && Tweaks && ZombiesMatch.Active)
                smoothTime = Mathf.Min(smoothTime, 0.04f);
        }

        public static void Bind(ConfigFile config, string section)
        {
            _tweaks = config.Bind(section, "Movement tweaks (test)", true, new ConfigDescription(
                "Off: every zombies-only movement change is left out (inertia, our WASD handling, the stamina and gear fixes each frame), to test what makes walking step about. On: as normal."));
            _on = config.Bind(section, "No movement inertia", true, new ConfigDescription(
                "In a zombies match you start, stop and turn at once: EFT's movement smoothing is off for you."));
        }

        private static bool _applied;

        public static void Apply()
        {
            Restore();
            _applied = true;
            if (_on?.Value != true || !Singleton<GlobalConfiguration>.Instantiated)
                return;
            var inertia = Singleton<GlobalConfiguration>.Instance.Inertia;
            _origSmoothness = inertia.SuddenChangesSmoothness;
            inertia.SuddenChangesSmoothness = Mathf.Max(_origSmoothness.Value, 20f);
            // The direction blend (0.1 s) and the stop before reversing (0.15-0.5 s by weight): gone, the original turns on a dime
            _origBlend = inertia.MinDirectionBlendTime;
            inertia.MinDirectionBlendTime = 0.04f;
            _origMoveTime = inertia.MoveTimeRange;
            inertia.MoveTimeRange = Vector2.zero;
            // Out of a sprint: EFT keeps 70-90% of the run's motion (the slide on stopping), brakes it with inertia and
            // eases the speed up and down: all off (stop on a dime)
            _origPreserve = inertia.SprintTransitionMotionPreservation;
            inertia.SprintTransitionMotionPreservation = Vector2.zero;
            _origBrake = inertia.SprintBrakeInertia;
            inertia.SprintBrakeInertia = Vector2.zero;
            _origSprintAcc = inertia.SprintAccelerationLimits;
            inertia.SprintAccelerationLimits = _origSprintAcc.Value * 4f;
            _origPreSprintAcc = inertia.PreSprintAccelerationLimits;
            inertia.PreSprintAccelerationLimits = _origPreSprintAcc.Value * 4f;
            // The sprint's turn of direction (lerped at 10 a second): at once
            if (EFTHardSettings.Instance != null)
            {
                _origLerp = EFTHardSettings.Instance.DIRECTION_LERP_SPEED;
                EFTHardSettings.Instance.DIRECTION_LERP_SPEED = Mathf.Max(_origLerp.Value, 30f);
            }
            ZombiesPlugin.Log.LogInfo("Zombies inertia: movement smoothing off");
        }

        /// <summary>Every frame on your own player.</summary>
        /// <summary>F12 "Movement tweaks (test)": our movement changes on (normal) or all left out.</summary>
        public static bool Tweaks => _tweaks?.Value != false;
        private static ConfigEntry<bool> _tweaks;

        public static void Tick(Player player)
        {
            // The test switch flipped mid-match: EFT's own settings back, or ours again
            if (!Tweaks && _applied)
            {
                Restore();
                _applied = false;
            }
            else if (Tweaks && !_applied && ZombiesMatch.Active)
                Apply();
            if (_on?.Value != true || !Tweaks || player == null)
                return;
            MovementContext ctx = player.MovementContext;
            if (ctx != null)
            {
                ctx.WalkInertia = Mathf.Min(ctx.WalkInertia, 0.06f);
                ctx.SprintBrakeInertia = 0f;
                ctx.TiltInertia = 0f;
            }
            var physical = player.Physical;
            if (physical != null)
            {
                physical.MoveSideInertia = 0f;
                physical.MoveDiagonalInertia = 0f;
            }
        }

        /// <summary>EFT's own smoothing again (match over).</summary>
        public static void Restore()
        {
            if (Singleton<GlobalConfiguration>.Instantiated)
            {
                var inertia = Singleton<GlobalConfiguration>.Instance.Inertia;
                if (_origSmoothness.HasValue)
                    inertia.SuddenChangesSmoothness = _origSmoothness.Value;
                if (_origBlend.HasValue)
                    inertia.MinDirectionBlendTime = _origBlend.Value;
                if (_origMoveTime.HasValue)
                    inertia.MoveTimeRange = _origMoveTime.Value;
                if (_origPreserve.HasValue)
                    inertia.SprintTransitionMotionPreservation = _origPreserve.Value;
                if (_origBrake.HasValue)
                    inertia.SprintBrakeInertia = _origBrake.Value;
                if (_origSprintAcc.HasValue)
                    inertia.SprintAccelerationLimits = _origSprintAcc.Value;
                if (_origPreSprintAcc.HasValue)
                    inertia.PreSprintAccelerationLimits = _origPreSprintAcc.Value;
            }
            _origSmoothness = null;
            _origBlend = null;
            _origMoveTime = null;
            _origPreserve = _origBrake = _origSprintAcc = _origPreSprintAcc = null;
            if (_origLerp.HasValue && EFTHardSettings.Instance != null)
                EFTHardSettings.Instance.DIRECTION_LERP_SPEED = _origLerp.Value;
            _origLerp = null;
        }
    }
}
