using HarmonyLib;
using System.Collections;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Underbarrel grenade launchers in zombies (the PaP M16's M203, guns.json pap.ubgl): the launcher comes out of the
    /// machine loaded, with its reserve of 40 mm rounds in the gear (guns.json: 8), and a Max Ammo fills both again.
    /// The secondary fire key (F12 "3. Zombies controls" > "Secondary fire (launcher)", B) switches between the gun and
    /// its launcher (EFT's own toggle); EFT's reload then takes a round from the gear into the launcher.
    /// </summary>
    internal static class ZombiesUbgl
    {
        // The M203's round: M381 (EFT's own default for it; ZombiesSally's arming fix makes it go off close in too)
        private const string Round = "5ede474b0c226a66f5402622";
        public const int Reserve = 8;

        /// <summary>The gun's underbarrel launcher (an item with a "patron_in_weapon" chamber fitted to it), or null.</summary>
        public static CompoundItem LauncherOf(Weapon gun)
        {
            if (gun == null)
                return null;
            foreach (Item it in gun.GetAllItems())
            {
                if (it == gun || !(it is CompoundItem c) || it.TemplateId.ToString() == gun.TemplateId.ToString())
                    continue;
                if (ChamberOf(c) != null)
                    return c;
            }
            return null;
        }

        /// <summary>
        /// The launcher's "patron_in_weapon": EFT keeps it in the item's Chambers, not its Slots (the M203 has no slots
        /// at all), so the old slots-only look never found the PaP'd M16's launcher: B did nothing and it was never
        /// loaded (user, 2026-11-09). Chambers read by reflection (whatever EFT's launcher class is called).
        /// </summary>
        public static Slot ChamberOf(CompoundItem c)
        {
            if (c == null)
                return null;
            Slot s = c.Slots?.FirstOrDefault(x => x.ID == "patron_in_weapon");
            if (s != null)
                return s;
            try
            {
                if (AccessTools.Property(c.GetType(), "Chambers")?.GetValue(c) is System.Collections.IEnumerable chambers)
                    foreach (object o in chambers)
                        if (o is Slot slot && slot.ID == "patron_in_weapon")
                            return slot;
            }
            catch
            {
            }
            return null;
        }

        /// <summary>The launcher loaded and the gear's 40 mm rounds topped up to the reserve (PaP, Max Ammo).</summary>
        public static IEnumerator Fill(Player me, Weapon gun, string why)
        {
            CompoundItem launcher = LauncherOf(gun);
            if (me?.InventoryController == null || launcher == null || Singleton<ItemFactory>.Instance == null)
                yield break;
            Slot chamber = ChamberOf(launcher);
            if (chamber == null)
                yield break;
            Item proto = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), Round, null);
            if (proto == null)
                yield break;
            yield return ZombiesItems.LoadModels(proto);
            if (chamber.ContainedItem == null && Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), Round, null) is Ammo inside)
            {
                inside.StackObjectsCount = 1;
                chamber.AddWithoutRestrictions(inside);
            }
            InventoryController inventory = me.InventoryController;
            int have = inventory.Inventory.Equipment.GetAllItems().Count(i => i.TemplateId.ToString() == Round && i.Parent?.Container != chamber);
            int added = 0;
            for (int i = have; i < Reserve; i++)
            {
                Item r = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), Round, null);
                ItemAddress place = r == null ? null : ZombiesItems.FindGrid(inventory, r);
                if (place == null || !ZombiesItems.Add(inventory, r, place, out _))
                    break;
                added++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies launcher: {gun.LocalizedName()}'s {launcher.LocalizedName()} loaded, {have + added}/{Reserve} spare rounds ({why})");
        }

        /// <summary>Every gun of yours with a launcher filled (Max Ammo).</summary>
        public static void FillAll(Player me)
        {
            if (me?.InventoryController == null)
                return;
            foreach (Weapon w in me.InventoryController.Inventory.Equipment.GetAllItems().OfType<Weapon>().ToList())
                if (LauncherOf(w) != null)
                    ZombiesPlugin.Instance.StartCoroutine(Fill(me, w, "Max Ammo"));
        }

        /// <summary>The underbarrel launcher is the one selected on the gun in the hands.</summary>
        public static bool LauncherUp(Player me)
        {
            try
            {
                return me?.HandsController is Player.FirearmController fc && fc.IsInLauncherMode();
            }
            catch
            {
                return false;
            }
        }

        private static System.Collections.IEnumerator SpeedsAfterToggle(Player me)
        {
            for (int i = 0; i < 3; i++)
            {
                yield return new UnityEngine.WaitForSeconds(0.4f);
                (me?.HandsAnimator as FirearmsAnimator)?.SetSpeedParameters();
            }
        }

        /// <summary>The secondary fire key: the gun in the hands to its launcher and back (EFT's own toggle).</summary>
        public static void Toggle(Player me)
        {
            if (!(me?.HandsController is Player.FirearmController fc) || !(fc.Item is Weapon w) || LauncherOf(w) == null)
                return;
            bool ok = fc.ToggleLauncher(null);
            // The launcher's quicker reload (ZombiesPerks.SlowReloaders) set on the hands as it comes up / goes away
            ZombiesPlugin.Instance.StartCoroutine(SpeedsAfterToggle(me));
            // (logged always: it once did nothing silently)
            ZombiesPlugin.Log.LogInfo($"Zombies launcher: B on the {w.LocalizedName()}: {(ok ? "switched" : "EFT refused")}");
            ZombiesDebug.Log($"launcher: toggle on the {w.LocalizedName()} {(ok ? "done" : "refused")}");
        }
    }
}
