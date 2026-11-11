using System;
using EFT.Interactive;
using EFT.UI.BattleTimer;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// The exits list (EFT's exit panels) in a zombies match: ZB-013 shows as "Basement Exfil", and until the rounds open
    /// it (round 15, ZombiesRounds.ExfilRound) its timer reads "Round 15+" instead of a time.
    /// </summary>
    internal static class ZombiesExfilUi
    {
        private const string Name = "Basement Exfil";
        private static readonly AccessTools.FieldRef<ExitTimerPanel, ExfiltrationPoint> Point = AccessTools.FieldRefAccess<ExitTimerPanel, ExfiltrationPoint>("_point");

        public static void Patch(Harmony harmony)
        {
            var post = new HarmonyMethod(typeof(ZombiesExfilUi), nameof(Relabel));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ExitTimerPanel), nameof(ExitTimerPanel.OnLocaleUpdatedHandler)), postfix: post);
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ExitTimerPanel), nameof(ExitTimerPanel.SetTimerText)), postfix: post);
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ExitTimerPanel), nameof(ExitTimerPanel.UpdateTimer)), postfix: post);
        }

        private static void Relabel(ExitTimerPanel __instance)
        {
            try
            {
                if (!ZombiesMatch.Active || !IsBasement(Point(__instance)))
                    return;
                if (__instance._pointName != null && __instance._pointName.text != Name)
                    __instance._pointName.text = Name;
                string ours = ZombiesExfil.PanelText();
                if (ours != null && __instance.TimerText != null)
                    __instance.TimerText.text = ours;
                else if (!ZombiesPower.ExfilsOpen && __instance.TimerText != null)
                    __instance.TimerText.text = $"Round {ZombiesRounds.ExfilRound}+";
            }
            catch
            {
                // Never in the way of EFT's own panel
            }
        }

        private static bool IsBasement(ExfiltrationPoint p)
        {
            if (p == null)
                return false;
            string n = ((p.Settings?.Name ?? "") + p.name).ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "");
            return n.Contains("zb013") || n.Contains("zb1013");
        }
    }
}
