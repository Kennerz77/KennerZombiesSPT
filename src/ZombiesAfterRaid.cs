using System;
using System.Collections.Generic;
using EFT.UI;
using TMPro;
using UnityEngine.UI;
using EFT;
using EFT.UI.SessionEnd;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Out of a zombies match, EFT's own after-raid screens (RAID ENDED / KILLED, the kill list, statistics, experience,
    /// health treatment) are not shown: their first screen comes up as always (so EFT's blurred background is there) but
    /// its own content is made invisible, and the zombies after action report (ZombiesReport) is drawn over it. Its NEXT
    /// runs EFT's own GoToMainMenu (what the last after-raid screen's "next" does), skipping the rest (the user,
    /// 2026-11-08). Any other raid goes through EFT's screens as always.
    /// </summary>
    internal static class ZombiesAfterRaid
    {
        private static SessionResultShowOperation _op;
        private static CanvasGroup _hidden;
        private static bool _patched;

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            harmony.Patch(AccessTools.Method(typeof(SessionResultShowOperation), nameof(SessionResultShowOperation.Init)),
                prefix: new HarmonyMethod(typeof(ZombiesAfterRaid), nameof(Remember)));
            // Show has two overloads (the screen controller one and the long one it calls): the controller one is patched
            harmony.Patch(AccessTools.Method(typeof(SessionResultExitStatus), nameof(SessionResultExitStatus.Show),
                    new[] { typeof(SessionResultExitStatus.ExitStatusScreenController) }),
                postfix: new HarmonyMethod(typeof(ZombiesAfterRaid), nameof(ShownForZombies)));
            ZombiesPlugin.Log.LogInfo("Zombies: EFT's after-raid screens replaced by the report after zombies matches");
        }

        private static void Remember(SessionResultShowOperation __instance) => _op = __instance;

        private static void ShownForZombies(SessionResultExitStatus __instance)
        {
            try
            {
                // Left the match from the menu: no report, EFT's screens skipped, straight to the main menu
                if (ZombiesReport.Forfeited && _op != null)
                {
                    SessionResultShowOperation leave = _op;
                    _op = null;
                    ZombiesReport.ForfeitDone();
                    leave.GoToMainMenu();
                    return;
                }
                if (!ZombiesReport.Waiting || _op == null)
                    return;
                // EFT's own screen stays (its background), its content hidden and not clickable
                _hidden = __instance.GetComponent<CanvasGroup>();
                if (_hidden == null)
                    _hidden = __instance.gameObject.AddComponent<CanvasGroup>();
                _hidden.alpha = 0f;
                _hidden.interactable = false;
                _hidden.blocksRaycasts = false;
                SessionResultShowOperation op = _op;
                _op = null;
                ZombiesReport.AfterRaidStarted(() => op.GoToMainMenu());
                ZombiesPlugin.Log.LogInfo("Zombies: after a zombies match: the report over EFT's after-raid background");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: EFT's after-raid screen couldn't be replaced ({e.Message})");
            }
        }

        // EFT's after-raid header, texts and buttons around the hidden screen (RAID ENDED, MAIN MENU, the lost items
        // banner): switched off while the report shows, put back with the screen
        private static readonly List<Behaviour> Off = new List<Behaviour>();
        private static float _nextSweep;

        /// <summary>
        /// Each frame while the report waits: EFT's screen kept invisible (its own fade-in set it visible again, the
        /// user saw it through the report, 2026-11-08), and every text and button of EFT's after-raid UI switched off.
        /// </summary>
        public static void Tick()
        {
            if (!ZombiesReport.Waiting)
                return;
            if (_hidden != null)
            {
                _hidden.alpha = 0f;
                _hidden.interactable = false;
                _hidden.blocksRaycasts = false;
            }
            if (Time.unscaledTime < _nextSweep)
                return;
            _nextSweep = Time.unscaledTime + 0.25f;
            try
            {
                SessionEndUI ui = MonoBehaviourSingleton<SessionEndUI>.Instance;
                if (ui == null)
                    return;
                foreach (Graphic g in ui.GetComponentsInChildren<Graphic>(false))
                {
                    if (g == null || !g.enabled)
                        continue;
                    bool text = g is TMP_Text;
                    bool button = g.GetComponentInParent<Selectable>() != null || g.GetComponentInParent<DefaultUIButton>() != null;
                    if (!text && !button)
                        continue;
                    g.enabled = false;
                    Off.Add(g);
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: EFT's after-raid texts couldn't be hidden ({e.Message})");
                _nextSweep = Time.unscaledTime + 5f;
            }
        }

        /// <summary>
        /// The report's NEXT: EFT's hidden screen and texts are put back as they were once the main menu is up (so they
        /// never flash on the way, and the next normal raid's screens show).
        /// </summary>
        public static void ShowEftScreens()
        {
            ZombiesPlugin.Instance.StartCoroutine(PutBack(_hidden, Off.ToArray()));
            Off.Clear();
            _hidden = null;
        }

        private static System.Collections.IEnumerator PutBack(CanvasGroup group, Behaviour[] off)
        {
            float until = Time.realtimeSinceStartup + 20f;
            while (!ZombiesReport.InMenuNow() && Time.realtimeSinceStartup < until)
                yield return null;
            foreach (Behaviour b in off)
                if (b != null)
                    b.enabled = true;
            if (group == null)
                yield break;
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;
        }
    }
}
