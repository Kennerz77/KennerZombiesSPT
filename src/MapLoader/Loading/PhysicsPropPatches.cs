using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // Physics props (SmallPhysicsObject, from Arena) apply a push only to a rigidbody that is asleep:
    // PlayCollision skips any that is awake, so a prop already flying isn't pushed again. Awake puts
    // them to sleep, so the first push always works; after that a prop must fall asleep again, and one
    // that has landed on a mesh collider, uneven ground or another prop keeps jittering just above
    // Unity's sleep threshold and then ignores every bullet, touch and grenade.
    //
    //  - PhysicsPropAwakePatch: a higher sleep threshold, so a landed prop actually comes to rest
    //  - PhysicsPropPushPatch: just before a push, a prop that's awake but has all but stopped is put
    //    to sleep, so the push applies; one still moving is left alone, as the game intends
    internal class PhysicsPropAwakePatch : ModulePatch
    {
        // mass-normalised kinetic energy; Unity's default is 0.005, too low for small props on mesh colliders
        private const float SleepThreshold = 0.05f;

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SmallPhysicsObject), "Awake");

        [PatchPostfix]
        private static void Postfix(SmallPhysicsObject __instance)
        {
            if (!MapRegistry.IsCustomMapActive() || __instance.Rigidbodies == null)
                return;
            foreach (var body in __instance.Rigidbodies)
                if (body != null)
                    body.sleepThreshold = Mathf.Max(body.sleepThreshold, SleepThreshold);
        }
    }

    internal class PhysicsPropPushPatch : ModulePatch
    {
        private const float RestingSpeed = 0.5f;        // m/s
        private const float RestingSpin = 1f;           // rad/s

        protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(SmallPhysicsObject), "PlayCollision");

        [PatchPrefix]
        private static void Prefix(SmallPhysicsObject __instance)
        {
            if (!MapRegistry.IsCustomMapActive() || __instance.Rigidbodies == null)
                return;
            foreach (var body in __instance.Rigidbodies)
            {
                if (body == null || body.isKinematic || body.IsSleeping())
                    continue;
                if (body.velocity.sqrMagnitude < RestingSpeed * RestingSpeed && body.angularVelocity.sqrMagnitude < RestingSpin * RestingSpin)
                    body.Sleep();
            }
        }
    }
}
