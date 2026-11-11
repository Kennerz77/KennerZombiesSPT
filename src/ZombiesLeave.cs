using System.Collections;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Leaving a solo zombies match from the Esc menu (EFT's Disconnect / Leave: the game's Stop with ExitStatus.Left)
    /// keeps the points and stats so far and shows the match report, solo or in a team (the others play on).
    /// </summary>
    internal static class ZombiesLeave
    {
        public static void Patch(Harmony harmony)
        {
            // EFT's LocalGame and any mod's game over it (Fika's CoopGame overrides Stop: solo through Fika never
            // reached the game over)
            var prefix = new HarmonyMethod(typeof(ZombiesLeave), nameof(StopPrefix)) { priority = Priority.First };
            foreach (System.Type t in AccessTools.AllTypes().Where(t => typeof(LocalGame).IsAssignableFrom(t)))
            {
                var stop = AccessTools.DeclaredMethod(t, nameof(LocalGame.Stop));
                if (stop == null || stop.IsAbstract || stop.GetParameters().Length < 2)
                    continue;
                try { harmony.Patch(stop, prefix: prefix); }
                catch (System.Exception e) { ZombiesPlugin.Log.LogWarning($"Zombies leave: {t.Name}.Stop not patched ({e.Message})"); }
            }
            harmony.Patch(AccessTools.Method(typeof(ReconnectionScreen), nameof(ReconnectionScreen.Show),
                    new[] { typeof(ReconnectionScreen.ReconnectionScreenController) }),
                postfix: new HarmonyMethod(typeof(ZombiesLeave), nameof(ReconnectShown)));
        }

        // ---------- EFT's "Reconnect" screen skipped after the zombies LEAVE MATCH? prompt ----------
        // EFT's in-raid leave opens a screen with your character, the map and a warning about leaving, with its own
        // Leave button. Our prompt already asked (user, 2026-11-11: skip it), so that screen stays invisible and its
        // Leave is pressed for you. Only right after our prompt's LEAVE; any other time EFT's screen works as always.
        private static float _skipUntil = -1f;
        private static CanvasGroup _hiddenScreen;

        /// <summary>Called by the zombies LEAVE MATCH? prompt just before EFT's leave: the next Reconnect screen is skipped.</summary>
        public static void SkipNextReconnectScreen() => _skipUntil = Time.unscaledTime + 10f;

        private static void ReconnectShown(ReconnectionScreen __instance)
        {
            try
            {
                bool skip = Time.unscaledTime < _skipUntil;
                if (!skip)
                {
                    // The screen is kept and reused: shown normally again if we hid it before
                    if (_hiddenScreen != null)
                    {
                        _hiddenScreen.alpha = 1f;
                        _hiddenScreen.interactable = true;
                        _hiddenScreen.blocksRaycasts = true;
                        _hiddenScreen = null;
                    }
                    return;
                }
                _skipUntil = -1f;
                _hiddenScreen = __instance.GetComponent<CanvasGroup>() ?? __instance.gameObject.AddComponent<CanvasGroup>();
                _hiddenScreen.alpha = 0f;
                _hiddenScreen.interactable = false;
                _hiddenScreen.blocksRaycasts = false;
                ZombiesPlugin.Instance.StartCoroutine(PressLeave(__instance));
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies leave: EFT's reconnect screen couldn't be skipped ({e.Message})");
            }
        }

        // A frame later, once EFT has finished showing it
        private static IEnumerator PressLeave(ReconnectionScreen screen)
        {
            yield return null;
            if (screen == null || screen._exitButton == null)
                yield break;
            ZombiesPlugin.Log.LogInfo("Zombies: EFT's reconnect screen skipped, leaving the match");
            screen._exitButton.OnClick.Invoke();
        }

        private static bool StopPrefix(string __0, ExitStatus __1)
        {
            string profileId = __0;
            ExitStatus exitStatus = __1;
            if (exitStatus != ExitStatus.Left || !ZombiesMatch.Active || ZombiesGameOver.Active)
                return true;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null || me.ProfileId != profileId || !me.HealthController.IsAlive)
                return true;
            // Left from the menu, solo or a team: the points and stats so far are kept and the report shows, the same
            // with or without Fika (user, 2026-11-11; it was "no rewards" before, which only ever happened without Fika)
            ZombiesPlugin.Log.LogInfo($"Zombies: left the {(ZombiesTeam.List().Count > 1 ? "team" : "solo")} match from the menu: points and stats kept");
            return true;
        }
    }
}
