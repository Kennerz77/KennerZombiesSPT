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

        /// <summary>Adds a weapon to the mystery box. Set <paramref name="wonderWeapon"/> for a rare one.</summary>
        public static void RegisterBoxWeapon(string templateId, string displayName = null, bool wonderWeapon = false)
        {
            if (string.IsNullOrEmpty(templateId) || Registered.Any(g => g.PresetTemplate == templateId))
                return;
            Registered.Add(new BoxGuns.Gun
            {
                Id = "api_" + templateId,
                Name = displayName ?? templateId,
                Role = wonderWeapon ? "box_wonder" : "box",
                PresetTemplate = templateId,
            });
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
