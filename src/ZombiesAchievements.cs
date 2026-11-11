using System.Linq;
using EFT;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// No achievements in a zombies match: EFT's in-raid achievement counting (AchievementsControllerClientGame) is
    /// switched off while the match runs, so kills, hits and the rest there never move an achievement on or finish one.
    /// Normal raids are untouched.
    /// </summary>
    internal static class ZombiesAchievements
    {
        private static bool _patched;
        private static int _blocked;

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            // EFT's in-raid achievement controller (by name: its namespace differs between builds)
            var type = AccessTools.TypeByName("AchievementsControllerClientGame");
            int n = 0;
            if (type == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies: EFT's achievement controller wasn't found, achievements stay on");
                return;
            }
            foreach (string name in new[] { "AchievementConditionValueChanged", "SetConditionalStatus" })
            {
                var m = AccessTools.Method(type, name);
                if (m == null)
                    continue;
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesAchievements), nameof(NotInZombies)));
                n++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies: achievements off in zombies matches ({n} methods)");

            // Their pop-ups too: during a match and for a while after it (the profile comes back from the server after
            // the raid and EFT can announce one then)
            // Every Init the view declares (it has more than one: asking by name alone threw 'Ambiguous match' and the
            // whole patch failed, 2026-11-09)
            foreach (var init in AccessTools.GetDeclaredMethods(typeof(EFT.UI.AchievementNotificationView)).Where(m => m.Name == "Init" && !m.IsAbstract))
                harmony.Patch(init, postfix: new HarmonyMethod(typeof(ZombiesAchievements), nameof(HidePopup)));
            // The notification itself, before any view is made for it (the view hook alone missed "Welcome to Tarkov")
            foreach (string name in new[] { "NotificationReceivedHandler", "ShowNotification" })
            {
                var m = AccessTools.Method(typeof(EFT.UI.NotifierView), name);
                if (m == null)
                    continue;
                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesAchievements),
                        m.ReturnType == typeof(bool) ? nameof(NoAchievementNotice) : nameof(NoAchievementNoticeVoid)));
                }
                catch (System.Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies: achievement notices not caught in {name} ({e.Message})");
                }
            }
            // EFT's raid-end sounds (the session end sting, the end-game sound) after a zombies match
            foreach (string name in new[] { "PlaySessionEnd", "PlayEndGameSound" })
            {
                var m = AccessTools.Method(typeof(EFT.UI.GUISounds), name);
                if (m != null)
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesAchievements), nameof(NoEndSound)));
            }
        }

        private static float _quietUntil = -1f;

        /// <summary>Achievement pop-ups and EFT's raid-end sounds stay off for this long from now (a zombies match runs or just ended).</summary>
        public static void QuietFor(float seconds) => _quietUntil = UnityEngine.Mathf.Max(_quietUntil, UnityEngine.Time.realtimeSinceStartup + seconds);

        private static bool Quiet => ZombiesMatch.Active || ZombiesReport.Waiting || UnityEngine.Time.realtimeSinceStartup < _quietUntil;

        private static void HidePopup(EFT.UI.AchievementNotificationView __instance)
        {
            if (!Quiet || __instance == null)
                return;
            __instance.gameObject.SetActive(false);
            ZombiesDebug.Log("achievement pop-up hidden (zombies)");
        }

        private static bool NoEndSound() => !Quiet;

        private static bool NoAchievementNotice(System.Reflection.MethodBase __originalMethod, object __0, ref bool __result)
        {
            if (!(__0 is EFT.Communications.NotificationAchievement) || !Quiet)
                return true;
            __result = false;
            ZombiesDebug.Log($"achievement notification dropped (zombies, {__originalMethod?.Name})");
            return false;
        }

        private static bool NoAchievementNoticeVoid(object __0)
        {
            if (!(__0 is EFT.Communications.NotificationAchievement) || !Quiet)
                return true;
            ZombiesDebug.Log("achievement notification dropped (zombies, received)");
            return false;
        }

        private static bool NotInZombies()
        {
            if (!ZombiesMatch.Active)
                return true;
            if (_blocked++ % 50 == 0)
                ZombiesDebug.Log($"achievement progress blocked in the zombies match ({_blocked} so far)");
            return false;
        }
    }
}
