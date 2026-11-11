using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;

namespace KennerZombies
{
    /// <summary>
    /// Which table gun (box_builds.json / guns.json) each gun handed out in a match is: the box, a wall buy, the
    /// starting M1911. EFT's own names don't say (the M16 is EFT's "M4A1" template), so the guns are remembered by item
    /// id as they're made. From the table: each gun's fire modes, plain and Pack-a-Punched (guns.json "fireModes" and
    /// "pap.fireModes"), and its Pack-a-Punch build (box_builds.json "pap").
    /// </summary>
    internal static class ZombiesGunTable
    {
        private static readonly Dictionary<string, BoxGuns.Gun> ById = new Dictionary<string, BoxGuns.Gun>();
        private static Dictionary<string, (string[] plain, string[] papped)> _modes;

        /// <summary>This item is this table gun (it was just made from it).</summary>
        public static void Remember(Item weapon, BoxGuns.Gun gun)
        {
            if (weapon != null && gun != null)
                ById[weapon.Id] = gun;
        }

        private static Dictionary<string, string> _classes;

        /// <summary>The gun's class in the table (pistol, smg, shotgun, ar, lmg, sniper, launcher, wonder), or null.</summary>
        public static string Class(BoxGuns.Gun gun)
        {
            if (gun?.Id == null)
                return null;
            if (_classes == null)
            {
                _classes = new Dictionary<string, string>();
                try
                {
                    foreach (JObject b in JArray.Parse(File.ReadAllText(Path.Combine(ZombiesPlugin.Dir, "box_builds.json"))).OfType<JObject>())
                        if ((string)b["id"] is string id)
                            _classes[id] = (string)b["class"];
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies gun table: classes unread ({e.Message})");
                }
            }
            return _classes.TryGetValue(gun.Id, out string c) ? c : null;
        }

        /// <summary>The table gun this item was made as, or null (a gun of another kind).</summary>
        public static BoxGuns.Gun Of(Item weapon) => weapon != null && ById.TryGetValue(weapon.Id, out BoxGuns.Gun g) ? g : null;

        /// <summary>
        /// A teammate's gun (never handed out here, so not in <see cref="ById"/>): the table gun of the same template
        /// whose base build shares the most parts with it (the M16 and M4 are both EFT's M4A1).
        /// </summary>
        public static BoxGuns.Gun Guess(Item weapon)
        {
            if (weapon == null)
                return Of(weapon);
            BoxGuns.Gun known = Of(weapon);
            if (known != null)
                return known;
            string tpl = weapon.TemplateId.ToString();
            var parts = new System.Collections.Generic.HashSet<string>(weapon.GetAllItems().Select(i => i.TemplateId.ToString()));
            BoxGuns.Gun best = null;
            int bestScore = -1;
            foreach (BoxGuns.Gun g in BoxGuns.BoxGunList() ?? new System.Collections.Generic.List<BoxGuns.Gun>())
            {
                if (g?.Base == null || (string)g.Base["tpl"] != tpl)
                    continue;
                int score = System.Text.RegularExpressions.Regex.Matches(g.Base.ToString(), "[0-9a-f]{24}")
                    .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).Distinct().Count(parts.Contains);
                if (score > bestScore)
                {
                    best = g;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>
        /// In a match a table gun goes by the table's name (the M16 is EFT's "M4A1" template, so EFT called it that):
        /// the HUD, prompts, the inventory. Runs before Pack-a-Punch's own name postfix, so a PaP'd gun still gets
        /// PaP's upgraded name. Other mods' guns (no logged build) keep EFT's name.
        /// </summary>
        public static void PatchNames(HarmonyLib.Harmony harmony)
        {
            var first = new HarmonyLib.HarmonyMethod(typeof(ZombiesGunTable), nameof(NamePostfix)) { priority = HarmonyLib.Priority.First };
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(LocalizationExtensions), nameof(LocalizationExtensions.LocalizedName), new[] { typeof(Item) }), postfix: first);
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(LocalizationExtensions), nameof(LocalizationExtensions.LocalizedShortName), new[] { typeof(Item) }), postfix: first);
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(ItemFactory), nameof(ItemFactory.BriefItemName)), postfix: first);
        }

        private static void NamePostfix(Item item, ref string __result)
        {
            if (item == null || ById.Count == 0 || !ZombiesMatch.Active)
                return;
            BoxGuns.Gun gun = Of(item);
            if (gun?.Base != null && !string.IsNullOrEmpty(gun.Name))
                __result = gun.Name;
        }

        /// <summary>The fire mode the table gives this gun now (plain or Pack-a-Punched), or null (the table says nothing).</summary>
        public static Weapon.EFireMode? Mode(Weapon weapon)
        {
            BoxGuns.Gun gun = Of(weapon);
            if (gun == null)
                return null;
            Load();
            if (!_modes.TryGetValue(gun.Id, out var m))
                return null;
            string[] list = ZombiesPaP.Infused(weapon) ? m.papped ?? m.plain : m.plain;
            string first = list?.FirstOrDefault();
            if (first == null)
                return null;
            return Enum.TryParse(first, true, out Weapon.EFireMode mode) ? mode : (Weapon.EFireMode?)null;
        }

        private static void Load()
        {
            if (_modes != null)
                return;
            _modes = new Dictionary<string, (string[], string[])>();
            try
            {
                string file = Path.Combine(ZombiesPlugin.Dir, "guns.json");
                if (!File.Exists(file))
                    return;
                foreach (JObject g in (JObject.Parse(File.ReadAllText(file))["guns"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    string id = (string)g["id"];
                    if (id == null)
                        continue;
                    string[] plain = (g["fireModes"] as JArray)?.Select(t => (string)t).ToArray();
                    string[] papped = (g["pap"]?["fireModes"] as JArray)?.Select(t => (string)t).ToArray();
                    _modes[id] = (plain, papped);
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies gun table: couldn't read the fire modes: {e.Message}");
            }
        }
    }
}
