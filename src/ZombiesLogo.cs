using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT.UI.Matchmaker;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace KennerZombies
{
    /// <summary>
    /// The zombies logo on EFT's raid loading screen ("Loading map..."): while it shows for a zombies map, every EFT logo
    /// image on screen (a UI Image whose sprite is named like a logo) shows hud/zm_logo.png instead (EFT's own Halloween
    /// zombies logo, cut out on a clear background), its shape kept. Put back for normal raids. The first time, every
    /// image on the screen is logged with its sprite, so the right one can be named if the guess misses.
    /// </summary>
    internal static class ZombiesLogo
    {
        private static readonly Dictionary<Image, (Sprite sprite, bool aspect)> Swapped = new Dictionary<Image, (Sprite, bool)>();
        private static Sprite _logo;
        private static bool _logged;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MatchmakerTimeHasCome), nameof(MatchmakerTimeHasCome.Show),
                    new[] { typeof(EFT.IEftSession), typeof(EFT.RaidSettings), typeof(MatchmakerPlayersController) }),
                postfix: new HarmonyMethod(typeof(ZombiesLogo), nameof(Shown)));
        }

        private static void Shown(MatchmakerTimeHasCome __instance, EFT.RaidSettings raidSettings)
        {
            try
            {
                if (raidSettings?.SelectedLocation != null && ZombiesMaps.Is(raidSettings.SelectedLocation.Id))
                    Swap(__instance);
                else
                    Restore();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies logo: {e.Message}");
            }
        }

        /// <summary>Every EFT logo on screen now shows the zombies logo (the main menu in zombies mode).</summary>
        public static void SwapAll() => Swap(null);

        private static void Swap(MatchmakerTimeHasCome screen)
        {
            if (_logo == null)
            {
                string file = Path.Combine(ZombiesPlugin.Dir, "hud", "zm_logo.png");
                if (!File.Exists(file))
                    return;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "zm_logo" };
                tex.LoadImage(File.ReadAllBytes(file));
                _logo = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                _logo.name = "zm_logo";
            }
            List<Image> images = UnityEngine.Object.FindObjectsOfType<Image>().Where(i => i.sprite != null && i.isActiveAndEnabled).ToList();
            if (!_logged)
            {
                _logged = true;
                foreach (Image i in images)
                    ZombiesPlugin.Log.LogInfo($"Zombies logo: image '{i.name}' sprite '{i.sprite.name}' size {i.rectTransform.rect.size}");
            }
            int n = 0;
            foreach (Image i in images.Where(i => i.sprite != _logo && (i.sprite.name.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0
                                                                        || i.name.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                if (!Swapped.ContainsKey(i))
                    Swapped[i] = (i.sprite, i.preserveAspect);
                i.sprite = _logo;
                i.preserveAspect = true;
                n++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies logo: {n} logo image(s) swapped on the {(screen != null ? "loading screen" : "main menu")}");
            // None found here: the big images on screen named, so the right one can be matched
            if (n == 0)
                foreach (Image i in images.Where(i => i.rectTransform.rect.width > 300f).Take(25))
                    ZombiesPlugin.Log.LogInfo($"Zombies logo: (none matched) image '{i.name}' sprite '{i.sprite.name}' size {i.rectTransform.rect.size}");
        }

        /// <summary>EFT's own logo again.</summary>
        public static void Restore()
        {
            foreach (var kv in Swapped)
                if (kv.Key != null)
                {
                    kv.Key.sprite = kv.Value.sprite;
                    kv.Key.preserveAspect = kv.Value.aspect;
                }
            Swapped.Clear();
        }
    }
}
