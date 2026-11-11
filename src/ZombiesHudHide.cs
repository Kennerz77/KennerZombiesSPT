using Comfort.Common;
using EFT.UI;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// EFT's battle HUD parts that don't belong in a zombies match (the user, 2026-11-07): the quick slots bar across
    /// the top (G U 1 2 3..., the gun in hand outlined red), the green health figure top left, the "COOPERATE [Y]"
    /// prompt when you look at someone (the tactical interaction panel) and the version watermark bottom left
    /// ("FIKA ... | SPT ... | PvE"). Kept switched off every frame of a match (EFT shows them again on its own); the
    /// watermark comes back once the match is over. A normal raid is untouched.
    /// </summary>
    internal static class ZombiesHudHide
    {
        private static EftBattleUIScreen _screen;
        private static float _nextLook;
        private static GameObject _watermark;

        public static void Tick()
        {
            if (!ZombiesMatch.Active)
                return;
            // Looked for at most once a second (a scene search every frame cost ~40 ms each time: the stutter)
            if (_screen == null && Time.unscaledTime >= _nextLook)
            {
                _nextLook = Time.unscaledTime + 1f;
                _screen = Object.FindObjectOfType<EftBattleUIScreen>();
            }
            if (_screen != null)
            {
                Hide(_screen._quickAccessPanel);
                Hide(_screen._characterHealthPanel);
                Hide(_screen._tacticalInteractionPanel);
                Hide(_screen._gesturesPanel); // the quick phrase box ("HURT [Y]")
            }
            if (_watermark == null)
                _watermark = MonoBehaviourSingleton<PreloaderUI>.Instantiated ? MonoBehaviourSingleton<PreloaderUI>.Instance._alphaVersionLabel?.gameObject : null;
            if (_watermark != null && _watermark.activeSelf)
                _watermark.SetActive(false);
        }

        /// <summary>The match is over: the watermark back as EFT had it.</summary>
        public static void Restore()
        {
            if (_watermark != null && !_watermark.activeSelf)
                _watermark.SetActive(true);
            _screen = null;
            ZombiesHud.RestoreEftActions();
        }

        private static void Hide(Component c)
        {
            if (c != null && c.gameObject.activeSelf)
                c.gameObject.SetActive(false);
        }
    }
}
