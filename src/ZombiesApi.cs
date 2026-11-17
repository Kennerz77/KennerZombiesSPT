using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;

namespace KennerZombies
{
    /// <summary>
    /// For other mods: put your gun in the zombies mystery box.
    ///
    ///   KennerZombies.ZombiesApi.RegisterBoxWeapon("your weapon template id", "Display name");
    ///
    /// Call it once your item exists (e.g. from your plugin's Awake or Start). The gun comes out of the box as its
    /// template's default preset (attachments included), with a full mag of its default ammo. Soft-depend on
    /// "com.kennerz.zmcore" (BepInDependency with SoftDependency) and only call this when it's loaded.
    /// The F12 switch "Other mods guns in the box" (off by default) adds every modded gun without any call.
    /// </summary>
    public static class ZombiesApi
    {
        internal static readonly List<BoxGuns.Gun> Registered = new List<BoxGuns.Gun>();

        /// <summary>
        /// A zombie shocked by your weapon (the Wunderwaffe DG-2's lightning): the original's tesla death played on it
        /// (stood up jerking, then down), held where it is, no swings. Returns the seconds until its body is down: kill
        /// it then (this only holds it), so its fall carries on as the ragdoll. <paramref name="seconds"/> is the hold
        /// when the clips aren't there (a jerking hold instead).
        /// </summary>
        public static float Shock(Player zombie, float seconds) => ZombiesShock.Shock(zombie, seconds);

        /// <summary>
        /// In a zombies match with teammates (Fika): true. Your mod's own messages then reach every other game in the match
        /// through <see cref="SendToTeam"/>, picked up by whatever it registered with <see cref="OnTeamMessage"/>.
        /// </summary>
        public static bool TeamOnline => ZombiesMatch.Active && ZombiesNet.Online;

        /// <summary>Sends <paramref name="data"/> on <paramref name="channel"/> to every other game in this zombies match.</summary>
        public static void SendToTeam(string channel, byte[] data)
        {
            if (TeamOnline)
                ZombiesNet.SendMod(channel, data);
        }

        /// <summary>Calls <paramref name="handler"/>(sender's profile id, data) for each message on <paramref name="channel"/>.</summary>
        public static void OnTeamMessage(string channel, Action<string, byte[]> handler) => ZombiesNet.ListenMod(channel, handler);

        /// <summary>Adds a weapon to the mystery box. Set <paramref name="wonderWeapon"/> for a rare one.</summary>
        public static void RegisterBoxWeapon(string templateId, string displayName = null, bool wonderWeapon = false) =>
            RegisterBoxWeapon(templateId, displayName, wonderWeapon, null);

        /// <summary>
        /// Adds a weapon to the mystery box with its own magazine: the gun is built as just the weapon with that magazine
        /// (filled, loaded and racked by the box, with a reserve), not its template's preset. Use this when the preset
        /// has no magazine (it came out with one round chambered and nothing to reload).
        /// </summary>
        public static void RegisterBoxWeapon(string templateId, string displayName, bool wonderWeapon, string magazineTemplateId)
        {
            if (string.IsNullOrEmpty(templateId) || Registered.Any(g => g.PresetTemplate == templateId || (string)g.Base?["tpl"] == templateId))
                return;
            var gun = new BoxGuns.Gun
            {
                Id = "api_" + templateId,
                Name = displayName ?? templateId,
                Role = wonderWeapon ? "box_wonder" : "box",
                PresetTemplate = string.IsNullOrEmpty(magazineTemplateId) ? templateId : null,
            };
            if (!string.IsNullOrEmpty(magazineTemplateId))
            {
                gun.Base = new Newtonsoft.Json.Linq.JObject
                {
                    ["tpl"] = templateId,
                    ["parts"] = new Newtonsoft.Json.Linq.JArray
                    {
                        new Newtonsoft.Json.Linq.JObject { ["slot"] = "mod_magazine", ["tpl"] = magazineTemplateId, ["parts"] = new Newtonsoft.Json.Linq.JArray() },
                    },
                };
                gun.Pap = (Newtonsoft.Json.Linq.JObject)gun.Base.DeepClone();
            }
            Registered.Add(gun);
            BoxGuns.Reload();
            ZombiesPlugin.Log?.LogInfo($"Mystery box: {displayName ?? templateId} registered by another mod");
        }

        /// <summary>
        /// Every weapon whose model comes from a mod's bundle (SPT's list of mod-served bundles): guns other mods add.
        /// </summary>
        internal static IEnumerable<BoxGuns.Gun> DetectModdedWeapons()
        {
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            if (factory == null)
                yield break;
            var modBundles = SPT.Custom.Utils.BundleManager.Bundles;
            foreach (var pair in factory.ItemTemplates)
            {
                ItemTemplate template = pair.Value;
                string path = null;
                try
                {
                    if (!(template is WeaponTemplate))
                        continue;
                    path = template.Prefab?.path;
                }
                catch (Exception)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(path) || !modBundles.ContainsKey(path))
                    continue;
                string id = pair.Key.ToString();
                yield return new BoxGuns.Gun { Id = "mod_" + id, Name = template.ShortNameLocalizationKey ?? id, Role = "box", PresetTemplate = id };
            }
        }
    }
}
