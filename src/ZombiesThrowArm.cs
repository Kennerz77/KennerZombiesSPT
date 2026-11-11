using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// A weaker throwing arm in a zombies match (the user, 2026-11-07): your grenades and monkeys thrown with less force
    /// (F12 "18. Zombie health and damage" > "Throw strength"). Scaled where the throw is made, before Fika sends it on, so
    /// every game sees the same arc; only once per throw (Fika's override calls EFT's own after it).
    /// </summary>
    internal static class ZombiesThrowArm
    {
        private static ConfigEntry<float> _strength;
        private static int _scaledFrame = -1;

        public static void Bind(ConfigFile config, string section) =>
            _strength = config.Bind(section, "Throw strength", 0.75f, new ConfigDescription(
                "How hard grenades and monkeys are thrown in a zombies match (1 = EFT's own).", new AcceptableValueRange<float>(0.3f, 1.5f)));

        public static void Patch(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(ZombiesThrowArm), nameof(ThrowPrefix));
            int n = 0;
            foreach (System.Type t in AccessTools.AllTypes().Where(t => typeof(Player.BaseGrenadeHandsController).IsAssignableFrom(t)))
            {
                MethodInfo m = t.GetMethod("ThrowGrenade", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(float), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(bool) }, null);
                if (m == null || m.IsAbstract)
                    continue;
                try
                {
                    harmony.Patch(m, prefix: prefix);
                    n++;
                }
                catch (System.Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies throw arm: {t.Name} not patched ({e.Message})");
                }
            }
            ZombiesPlugin.Log.LogInfo($"Zombies throw arm: {n} throw methods");
        }

        private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(Player.ItemHandsController), "_player");

        private static void ThrowPrefix(object __instance, ref Vector3 __3)
        {
            if (!ZombiesMatch.Active || _scaledFrame == Time.frameCount)
                return;
            if (!(PlayerField?.GetValue(__instance) is Player p) || !p.IsYourPlayer)
                return;
            _scaledFrame = Time.frameCount;
            __3 *= _strength?.Value ?? 0.75f;
        }
    }
}
