using System.Linq;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// The cultist knife's charges do nothing in a zombies match and are never spent there (the user, 2026-11-09).
    /// EFT spends a charge (SideEffectComponent.Value--) and poisons in the HIT player's
    /// ActiveHealthController.TryApplySideEffects: answered "no side effect" while a match runs, on every game (Fika's
    /// overrides too), so the knife comes back from the match with the charges it went in with. Normal raids untouched.
    /// </summary>
    internal static class ZombiesKnifeCharges
    {
        public static void Patch(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(ZombiesKnifeCharges), nameof(NoSideEffect));
            int n = 0;
            foreach (var t in AccessTools.AllTypes().Where(t => t.Assembly.GetName().Name == "Assembly-CSharp" || t.Assembly.GetName().Name.StartsWith("Fika")))
            {
                System.Reflection.MethodInfo[] methods;
                try { methods = AccessTools.GetDeclaredMethods(t).ToArray(); }
                catch { continue; }
                foreach (var m in methods)
                {
                    if (m.Name != "TryApplySideEffects" || m.ReturnType != typeof(bool) || m.IsAbstract)
                        continue;
                    try
                    {
                        harmony.Patch(m, prefix: prefix);
                        n++;
                    }
                    catch (System.Exception e)
                    {
                        ZombiesPlugin.Log.LogWarning($"Zombies knife charges: {t.FullName}.{m.Name} not patched ({e.Message})");
                    }
                }
            }
            ZombiesPlugin.Log.LogInfo($"Zombies knife charges: {n} side effect method(s) patched");
        }

        private static bool NoSideEffect(ref bool __result)
        {
            if (!ZombiesMatch.Active)
                return true;
            __result = false;
            return false;
        }
    }
}
