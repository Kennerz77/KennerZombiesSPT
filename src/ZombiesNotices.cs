using System;
using EFT.Communications;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// EFT's bottom-right notifications in zombies mode (user, 2026-11-11): the XP and skill pop-ups ("Searching
    /// experience (5)") and the like are dropped; the ones with something to say (Fika's "X left", warnings) come up
    /// as the zombies menus' own toast at the top middle instead. Normal mode is untouched. Only EFT's notification calls
    /// are stepped in front of; Fika and EFT themselves are not changed.
    /// </summary>
    internal static class ZombiesNotices
    {
        private static bool Zombies => ZombiesMatch.Active || ZombiesMapSelect.Zombies && ZombiesPlugin.AtMenus;

        public static void Patch(Harmony harmony)
        {
            int n = 0;
            foreach (string name in new[] { nameof(NotificationManager.DisplayMessageNotification), nameof(NotificationManager.DisplayWarningNotification),
                         nameof(NotificationManager.DisplaySingletonWarningNotification), nameof(NotificationManager.DisplaySingletonNotification) })
            {
                var m = AccessTools.Method(typeof(NotificationManager), name);
                if (m == null)
                    continue;
                bool warning = name.Contains("Warning");
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesNotices), warning ? nameof(Message) : nameof(PlainMessage)));
                n++;
            }
            var any = AccessTools.Method(typeof(NotificationManager), nameof(NotificationManager.DisplayNotification));
            if (any != null)
            {
                harmony.Patch(any, prefix: new HarmonyMethod(typeof(ZombiesNotices), nameof(Other)));
                n++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies notices: {n} of EFT's notification calls go to the zombies toasts in zombies mode");
        }

        // A text notification: shown as our toast
        private static bool Message(string message)
        {
            if (!Zombies)
                return true;
            try
            {
                if (!string.IsNullOrEmpty(message))
                    ZombiesParty.Toast(message);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies notices: {e.Message}");
            }
            return false;
        }

        // A plain text notification: EFT's own (gun mastery, "Searching experience", skills: user 2026-11-11) are dropped
        // in zombies mode; another mod's (Fika's "X left") still come up as our toast
        private static bool PlainMessage(string message)
        {
            if (!Zombies)
                return true;
            if (FromEft())
            {
                ZombiesDebug.Log($"notice dropped (the game's own): {message}");
                return false;
            }
            return Message(message);
        }

        private static readonly System.Reflection.Assembly Game = typeof(NotificationManager).Assembly, Ours = typeof(ZombiesNotices).Assembly;

        /// <summary>The notification was asked for by the game itself (the first caller outside the notification code).</summary>
        private static bool FromEft()
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                if (frames == null)
                    return false;
                foreach (var f in frames)
                {
                    Type t = f.GetMethod()?.DeclaringType;
                    if (t == null || t == typeof(NotificationManager) || t.Assembly == Ours || t.Assembly == typeof(Harmony).Assembly
                        || t.Namespace == "EFT.Communications")
                        continue;
                    return t.Assembly == Game;
                }
            }
            catch
            {
            }
            return false;
        }

        // Every other kind (XP, skills, items, quests...): not shown in zombies mode
        private static bool Other() => !Zombies;
    }
}
