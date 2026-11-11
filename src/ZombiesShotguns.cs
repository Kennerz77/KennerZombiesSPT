using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// Shotguns in a zombies match spread their pellets tighter (the user, 2026-11-08: "more effective"): EFT's
    /// TotalShotgunDispersion (the weapon's / barrel's pellet spread) times F12 "18. Zombie health and damage" >
    /// "Shotgun pellet spread x" while a match runs. Patched on every type that has it (found by name, EFT's own
    /// weapon class included). Normal raids untouched.
    /// </summary>
    internal static class ZombiesShotguns
    {
        private static ConfigEntry<float> _spread;

        public static void Bind(ConfigFile config, string section)
        {
            _spread = config.Bind(section, "Shotgun pellet spread x", 0.45f, new ConfigDescription(
                "How wide shotgun pellets spread in a zombies match (1 = EFT's own, lower = tighter).", new AcceptableValueRange<float>(0.1f, 1f)));
        }

        public static void Patch(Harmony harmony)
        {
            var post = new HarmonyMethod(typeof(ZombiesShotguns), nameof(Tighter));
            int n = 0;
            // EFT's own code only (Assembly-CSharp): never another mod's types (the old scan of every loaded type touched
            // them all and logged a HarmonyX warning for each one without the property)
            System.Type[] types;
            try { types = typeof(EFT.Player).Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
            foreach (var t in types)
            {
                System.Reflection.PropertyInfo p;
                try { p = t.GetProperty("TotalShotgunDispersion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly); }
                catch { continue; }
                if (p?.GetMethod == null || p.PropertyType != typeof(float) || p.GetMethod.IsAbstract)
                    continue;
                try
                {
                    harmony.Patch(p.GetMethod, postfix: post);
                    n++;
                }
                catch (System.Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies shotguns: {t.Name} not patched ({e.Message})");
                }
            }
            ZombiesPlugin.Log.LogInfo($"Zombies shotguns: pellet spread patched on {n} type(s)");
        }

        private static void Tighter(ref float __result)
        {
            if (ZombiesMatch.Active)
                __result *= _spread?.Value ?? 0.45f;
        }
    }
}
