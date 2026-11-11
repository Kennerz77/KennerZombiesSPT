using System.Linq;
using EFT.InventoryLogic;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// No gun malfunctions in a zombies match (the user, 2026-11-08): EFT's malfunction roll (the firearm controller's
    /// GetMalfunctionState, and any override of it, Fika's included) answers "none" while a match runs. Normal raids
    /// are untouched. (BoxGive's rack-only fake misfire sets the state directly, not through this roll.)
    /// </summary>
    internal static class ZombiesNoJams
    {
        public static void Patch(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(ZombiesNoJams), nameof(NoJam));
            int n = 0;
            foreach (var t in AccessTools.AllTypes())
            {
                System.Reflection.MethodInfo[] methods;
                try { methods = AccessTools.GetDeclaredMethods(t).ToArray(); }
                catch { continue; }
                foreach (var m in methods)
                {
                    if (m.Name != "GetMalfunctionState" || m.ReturnType != typeof(Weapon.EMalfunctionState) || m.IsAbstract)
                        continue;
                    try
                    {
                        harmony.Patch(m, prefix: prefix);
                        n++;
                    }
                    catch (System.Exception e)
                    {
                        ZombiesPlugin.Log.LogWarning($"Zombies no jams: {t.FullName}.{m.Name} not patched ({e.Message})");
                    }
                }
            }
            ZombiesPlugin.Log.LogInfo($"Zombies no jams: {n} malfunction roll(s) patched");
        }

        private static bool NoJam(ref Weapon.EMalfunctionState __result)
        {
            if (!ZombiesMatch.Active)
                return true;
            __result = Weapon.EMalfunctionState.None;
            return false;
        }
    }
}
