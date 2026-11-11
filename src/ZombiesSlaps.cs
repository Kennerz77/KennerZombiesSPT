using System;
using System.Reflection;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's health in a zombies match: a zombie's hit is a slap, not EFT damage (ZombiesRules catches it before EFT
    /// applies it). 3 slaps and you're down, 5 with Jugger-Nog (Perk Beverages); 4 s without a slap and they're all gone.
    /// Each slap reddens the screen's edges, deeper the closer you are to going down. Going down is the original's last stand
    /// (ZombiesDowned). Each Fika client counts the slaps on its own player.
    /// </summary>
    internal static class ZombiesSlaps
    {
        private const int BaseHits = 3, JuggHits = 5;
        private const float RegenAfter = 4f, MinGap = 0.8f; // slaps closer together than this count once (no gang-down)

        private static int _hits;
        private static float _lastHit = -100f;
        private static bool _down;
        private static Texture2D _edge;
        private static MethodInfo _perkHas;
        private static bool _perkLooked;

        public static int MaxHits => ZombiesPerks.Has("jugg") ? JuggHits : BaseHits;

        /// <summary>No slap lands before this time (a moment to move after the pause or a revive).</summary>
        public static float SafeUntil = -100f;

        public const float Grace = 1.5f;

        public static void Reset()
        {
            _hits = 0;
            _lastHit = -100f;
            _down = false;
        }

        /// <summary>A zombie hit the local player (forced: F12's "Take a slap", past god mode and the gap).</summary>
        public static void Hit(Player player, bool forced = false)
        {
            if (_down || !forced && (ZombiesDebug.NoSlaps?.Value == true || Time.time - _lastHit < MinGap))
                return;
            // Paused (solo), or the grace after the pause's countdown / being revived: nothing lands (user, 2026-11-11)
            if (!forced && (ZombiesPause.Paused || Time.time < SafeUntil))
                return;
            _hits++;
            _lastHit = Time.time;
            ZombiesDebug.Log($"slap {_hits}/{MaxHits}");
            ZombiesWidow.Slapped(player);
            if (_hits < MaxHits)
                return;
            _down = true;
            ZombiesPlugin.Log.LogInfo($"Zombies: down after {_hits} slaps");
            // The original's last stand: crawl with the pistol until revived or bled out
            ZombiesDowned.GoDown(player);
        }

        public static void Clear()
        {
            _hits = 0;
            _lastHit = -100f;
        }

        public static void DownNow(Player player)
        {
            _hits = MaxHits - 1;
            _lastHit = -100f;
            Hit(player, true);
        }

        public static void Tick()
        {
            if (_hits > 0 && !_down && Time.time - _lastHit > RegenAfter)
                _hits = 0;
        }

        /// <summary>A Perk Beverages perk the player has drunk ("jugg", "revive"...), if that mod is in (its PerkDrink.Has).</summary>
        public static bool HasPerk(string perk)
        {
            if (!_perkLooked)
            {
                _perkLooked = true;
                Type drink = Type.GetType("PerkBeverages.PerkDrink, PerkBeverages") ?? FindType("PerkBeverages.PerkDrink");
                _perkHas = drink?.GetMethod("Has", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            try
            {
                return _perkHas != null && (bool)_perkHas.Invoke(null, new object[] { perk });
            }
            catch
            {
                return false;
            }
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = a.GetType(name, false);
                if (t != null)
                    return t;
            }
            return null;
        }

        // ---------- The red edges ----------

        public static void Draw()
        {
            // Down, the last stand draws its own deepening red
            if (_hits <= 0 || _down)
                return;
            float since = Time.time - _lastHit;
            // A flash on the slap, settling to how hurt you are; fading as the regen nears
            float hurt = Mathf.Clamp01(_hits / (float)Mathf.Max(1, MaxHits - 1));
            float flash = Mathf.Clamp01(1f - since / 0.4f) * 0.35f;
            float fade = _down ? 1f : Mathf.Clamp01(1f - Mathf.Max(0f, since - (RegenAfter - 1f)));
            float alpha = Mathf.Clamp01((0.35f + 0.5f * hurt) * fade + flash);
            GUI.color = new Color(0.7f, 0f, 0f, alpha);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Edge());
            GUI.color = Color.white;
        }

        /// <summary>The red edges at this strength (0-1): the last stand's bleed-out.</summary>
        public static void DrawRed(float alpha)
        {
            GUI.color = new Color(0.7f, 0f, 0f, Mathf.Clamp01(alpha));
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Edge());
            GUI.color = Color.white;
        }

        // Clear in the middle, red towards the edges
        private static Texture2D Edge()
        {
            if (_edge != null)
                return _edge;
            const int size = 128;
            _edge = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / 1.4142f;
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1f, d));
                    _edge.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            _edge.Apply();
            return _edge;
        }
    }
}
