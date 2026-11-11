using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace KennerZombies
{
    /// <summary>
    /// A more forgiving knife in a zombies match (the user, 2026-11-07): every melee weapon reaches further and hits a
    /// wider area (EFT's knife templates: PrimaryDistance / SecondryDistance and knifeHitRadius, scaled while a match
    /// runs, EFT's own values back after). F12 "18. Zombie health and damage" > "Melee reach" / "Melee hit width".
    /// </summary>
    internal static class ZombiesMeleeReach
    {
        private static ConfigEntry<float> _reach, _width;
        private static readonly Dictionary<KnifeTemplate, (float primary, float secondary, float radius, float delay)> Original = new Dictionary<KnifeTemplate, (float, float, float, float)>();
        private static float _applied = -1f, _appliedWidth = -1f;

        public static void Bind(ConfigFile config, string section)
        {
            _reach = config.Bind(section, "Melee reach", 2.54f, new ConfigDescription("How much further the knife reaches in a zombies match (1 = EFT's own).", new AcceptableValueRange<float>(1f, 3f)));
            _width = config.Bind(section, "Melee hit width", 2.36f, new ConfigDescription("How much wider the knife's hit is in a zombies match (1 = EFT's own).", new AcceptableValueRange<float>(1f, 3f)));
        }

        /// <summary>Each frame of a match (cheap once set): the knives scaled.</summary>
        public static void Tick()
        {
            float reach = _reach?.Value ?? 2.2f, width = _width?.Value ?? 2f;
            if (UnityEngine.Mathf.Approximately(reach, _applied) && UnityEngine.Mathf.Approximately(width, _appliedWidth))
                return;
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            if (factory == null)
                return;
            foreach (KnifeTemplate t in factory.ItemTemplates.Values.OfType<KnifeTemplate>())
            {
                if (!Original.TryGetValue(t, out var o))
                    Original[t] = o = (t.PrimaryDistance, t.SecondryDistance, t.knifeHitRadius, t.knifeHitDelay);
                t.PrimaryDistance = o.primary * reach;
                t.SecondryDistance = o.secondary * reach;
                t.knifeHitRadius = o.radius * width;
                // The hit lands sooner in the swing (more responsive)
                t.knifeHitDelay = o.delay * 0.6f;
            }
            _applied = reach;
            _appliedWidth = width;
            ZombiesPlugin.Log.LogInfo($"Zombies melee: reach x{reach}, hit width x{width} on {Original.Count} knives");
        }

        /// <summary>The match is over: EFT's own knives again.</summary>
        public static void Restore()
        {
            foreach (var pair in Original)
            {
                pair.Key.PrimaryDistance = pair.Value.primary;
                pair.Key.SecondryDistance = pair.Value.secondary;
                pair.Key.knifeHitRadius = pair.Value.radius;
                pair.Key.knifeHitDelay = pair.Value.delay;
            }
            _applied = _appliedWidth = -1f;
        }
    }
}
