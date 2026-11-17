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
            // EFT's on-screen notifier itself: notifications sent through NotificationManager's event (not the calls above)
            // still reached it, e.g. "weapon mastering level 1" in a match (user, 2026-11-13: players would think their
            // character is levelling up). In zombies mode it shows nothing; our toasts are our own, not EFT's notifier
            var show = AccessTools.Method(typeof(EFT.UI.NotifierView), nameof(EFT.UI.NotifierView.ShowNotification));
            if (show != null)
            {
                harmony.Patch(show, prefix: new HarmonyMethod(typeof(ZombiesNotices), nameof(NotifierShow)));
                n++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies notices: {n} of EFT's notification calls go to the zombies toasts in zombies mode");
        }

        // EFT's notifier in zombies mode: nothing shown (XP, skills, mastering, items, quests...)
        private static bool NotifierShow(Notification notification, ref bool __result)
        {
            if (!Zombies)
                return true;
            ZombiesDebug.Log($"notice dropped (EFT's notifier, zombies mode): {notification?.GetType().Name}");
            __result = false;
            return false;
        }

        // A text notification: shown as our toast
        private static bool Message(string message)
        {
            if (!Zombies)
                return true;
            if (FromOurOtherMods())
            {
                ZombiesDebug.Log($"notice dropped (a hint from one of our gun/perk mods, zombies match): {message}");
                return false;
            }
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
        private static bool FromEft() => FirstCaller() == Game;

        // Our gun and perk mods (and their Fika add-ons): their hints are for normal play (user, 2026-11-12: players
        // work out the Thundergun's "fire every charge first" themselves in zombies). The mods stay as they are
        private static readonly string[] OtherMods = { "Thundergun", "Raygun", "PaPHideoutModule", "PerkBeverages" };

        /// <summary>A message or warning from one of our other mods, in a zombies match: never shown.</summary>
        private static bool FromOurOtherMods()
        {
            if (!ZombiesMatch.Active)
                return false;
            string name = FirstCaller()?.GetName().Name;
            if (name == null)
                return false;
            foreach (string mod in OtherMods)
                if (name == mod || name == mod + "Fika")
                    return true;
            return false;
        }

        /// <summary>The assembly of the first caller outside the notification code, Harmony and this mod.</summary>
        private static System.Reflection.Assembly FirstCaller()
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                if (frames == null)
                    return null;
                foreach (var f in frames)
                {
                    Type t = f.GetMethod()?.DeclaringType;
                    if (t == null || t == typeof(NotificationManager) || t.Assembly == Ours || t.Assembly == typeof(Harmony).Assembly
                        || t.Namespace == "EFT.Communications")
                        continue;
                    return t.Assembly;
                }
            }
            catch
            {
            }
            return null;
        }

        // Every other kind (XP, skills, items, quests...): not shown in zombies mode
        private static bool Other() => !Zombies;
    }
}
