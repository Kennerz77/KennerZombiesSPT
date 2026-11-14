using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's ammo in a zombies match: each gun has a reserve (guns.json's "reserve", the M1911 80) on top of its mag, shown
    /// The original-style "mag / reserve" bottom right. One spare mag waits in the pockets and is kept topped up from the reserve, so
    /// EFT's own reload (normal or quick) just works: the spare goes in. The mag that comes out becomes the new spare, its
    /// rounds still counted. A quick reload drops the mag: its rounds go back to the reserve and a new spare is made. With
    /// the reserve and spare both empty there's no spare, so the gun can't be reloaded. Mag contents are the shooter's own,
    /// so with Fika each player's client keeps its own guns' ammo.
    /// </summary>
    internal static class ZombiesAmmo
    {
        private class GunAmmo
        {
            public int Reserve;
            public string MagTemplate, AmmoTemplate;
            public string InGun;   // the mag in the gun last frame
            public string Spare;   // the spare mag in the pockets
            public string Out;     // the mag that came out of the gun, not seen in the pockets yet
            public float OutSince;
            public bool Pool;      // a reload swapped the mags: pool every round once it's done (the original's 18/20 -> 30/8)
        }

        private static readonly Dictionary<string, GunAmmo> Guns = new Dictionary<string, GunAmmo>();
        private static Dictionary<string, int> _reserves; // weapon template -> the original reserve
        private static float _nextSpare;
        private static int _shownMag = -1, _shownReserve;
        private static bool _cylReloading;
        private static string _shownGun;

        public static void Reset()
        {
            Guns.Clear();
            _shownMag = -1;
        }

        /// <summary>The gun in your hands: its full reserve back (F12, later Max Ammo).</summary>
        public static void Fill(Player player)
        {
            if (player?.HandsController?.Item is Weapon w)
            {
                if (!Guns.TryGetValue(w.Id, out GunAmmo gun))
                    Guns[w.Id] = gun = new GunAmmo { AmmoTemplate = w.Template.defAmmo };
                gun.Reserve = ReserveFor(w);
                ZombiesPlugin.Log.LogInfo($"Zombies ammo: {w.LocalizedName()} reserve filled to {gun.Reserve}");
            }
        }

        /// <summary>This gun's full reserve back (a wall buy's ammo).</summary>
        public static void FillGun(Weapon w)
        {
            if (w == null)
                return;
            if (!Guns.TryGetValue(w.Id, out GunAmmo gun))
                Guns[w.Id] = gun = new GunAmmo { AmmoTemplate = w.Template.defAmmo };
            gun.Reserve = ReserveFor(w);
            ZombiesPlugin.Log.LogInfo($"Zombies ammo: {w.LocalizedName()} reserve filled to {gun.Reserve}");
        }

        /// <summary>The gun in your hands: no reserve, spare emptied (F12).</summary>
        public static void Empty(Player player)
        {
            if (player?.HandsController?.Item is Weapon w && Guns.TryGetValue(w.Id, out GunAmmo gun))
            {
                gun.Reserve = 0;
                if (gun.Spare != null && Find<Magazine>(player.InventoryController, gun.Spare) is Magazine spare)
                    SetRounds(spare, 0, gun.AmmoTemplate);
                ZombiesPlugin.Log.LogInfo($"Zombies ammo: {w.LocalizedName()} reserve emptied");
            }
        }

        private class Held
        {
            public int Reserve, Spare, Mag;
        }

        /// <summary>
        /// The last stand: this gun gets exactly this many rounds (one in the chamber, the rest in its mag) and no reserve
        /// or spare. What it had is returned, to give back with ReleaseLastStand.
        /// </summary>
        public static object HoldForLastStand(Player player, Weapon w, int rounds)
        {
            if (!Guns.TryGetValue(w.Id, out GunAmmo gun))
                Guns[w.Id] = gun = new GunAmmo { Reserve = ReserveFor(w), AmmoTemplate = w.Template.defAmmo };
            Magazine inGun = w.GetCurrentMagazine();
            Magazine spare = gun.Spare != null ? Find<Magazine>(player.InventoryController, gun.Spare) : null;
            var held = new Held { Reserve = gun.Reserve, Spare = spare?.Count ?? 0, Mag = (inGun?.Count ?? 0) + (w.Chambers.Length > 0 && w.Chambers[0].ContainedItem != null ? 1 : 0) };
            gun.Reserve = 0;
            if (spare != null)
                SetRounds(spare, 0, gun.AmmoTemplate);
            if (inGun != null)
                SetRounds(inGun, Mathf.Min(rounds - 1, inGun.MaxCount), gun.AmmoTemplate);
            if (w.Chambers.Length > 0)
            {
                Slot chamber = w.Chambers[0];
                if (chamber.ContainedItem != null)
                    chamber.RemoveItem();
                if (Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), gun.AmmoTemplate, null) is Ammo round)
                    chamber.AddWithoutRestrictions(round);
            }
            return held;
        }

        private class RaygunHeld
        {
            public int Reserve, Cells;
        }

        /// <summary>
        /// The Ray Gun in the last stand (the user, 2026-11-08): only the 20 shots in its two cells, no reserve and no
        /// spare cells (the loose cells taken out of the gear, so there's nothing to reload with). Given back on revive
        /// (<see cref="ReleaseRaygunLastStand"/>): the reserve plus those cells' shots; the cells come back from it.
        /// </summary>
        public static object HoldRaygunForLastStand(Player player, Weapon w)
        {
            if (!Guns.TryGetValue(w.Id, out GunAmmo gun))
                Guns[w.Id] = gun = new GunAmmo { Reserve = ReserveFor(w), AmmoTemplate = w.Template.defAmmo };
            var held = new RaygunHeld { Reserve = gun.Reserve };
            InventoryController inventory = player.InventoryController;
            Ammo stack = gun.Spare != null ? Find<Ammo>(inventory, gun.Spare) : null;
            if (stack != null)
            {
                held.Cells = stack.StackObjectsCount;
                var removed = ItemManipulator.Remove(stack, inventory, false, true);
                ZombiesItems.Removed(inventory, removed, stack);
            }
            gun.Spare = null;
            gun.Reserve = 0;
            ZombiesPlugin.Log.LogInfo($"Zombies ammo: Ray Gun last stand: 20 in the cells, {held.Reserve} reserve + {held.Cells} spare cell(s) held");
            return held;
        }

        /// <summary>Revived with the Ray Gun: its reserve back, the spare cells' shots with it.</summary>
        public static void ReleaseRaygunLastStand(Weapon w, object state)
        {
            if (!(state is RaygunHeld held) || !Guns.TryGetValue(w.Id, out GunAmmo gun))
                return;
            gun.Reserve = held.Reserve + held.Cells * RaygunCellShots;
            ZombiesPlugin.Log.LogInfo($"Zombies ammo: Ray Gun back from the last stand: reserve {gun.Reserve} shots");
        }

        /// <summary>Revived: the gun's reserve, spare and mag as they were before the last stand.</summary>
        public static void ReleaseLastStand(Player player, Weapon w, object state)
        {
            if (!(state is Held held) || !Guns.TryGetValue(w.Id, out GunAmmo gun))
                return;
            gun.Reserve = held.Reserve + held.Spare;
            Magazine inGun = w.GetCurrentMagazine();
            if (inGun != null)
            {
                int inChamber = w.Chambers.Length > 0 && w.Chambers[0].ContainedItem != null ? 1 : 0;
                SetRounds(inGun, Mathf.Max(0, held.Mag - inChamber), gun.AmmoTemplate);
            }
        }

        /// <summary>This gun's reserve set (the original's start: the M1911 has 32 on top of its 8; Max Ammo fills it to its full 80).</summary>
        public static void SetReserve(Weapon w, int reserve)
        {
            if (w == null)
                return;
            if (!Guns.TryGetValue(w.Id, out GunAmmo gun))
                Guns[w.Id] = gun = new GunAmmo { AmmoTemplate = w.Template.defAmmo };
            gun.Reserve = reserve;
        }

        /// <summary>
        /// Max Ammo's extra (the user, 2026-11-08): the gun in your hands loaded full too, not only its reserve. Never in
        /// the middle of a reload (EFT is swapping mags). The Ray Gun gets two full cells; the Thundergun a full canister
        /// and a charge in the chamber (it was skipped before: the user held the Ray Gun and nothing happened).
        /// </summary>
        public static void FillHeldGun(Player me)
        {
            try
            {
                if (!(me?.HandsController?.Item is Weapon w))
                    return;
                if (me.HandsController is Player.FirearmController fc && fc.IsInReloadOperation())
                {
                    ZombiesPlugin.Log.LogInfo($"Zombies ammo: Max Ammo: {w.LocalizedName()} is reloading, only the reserve filled");
                    return;
                }
                if (w.TemplateId.ToString() == RaygunId)
                {
                    foreach (Slot chamber in w.Chambers)
                        if (RaygunCellLevel(chamber.ContainedItem) < RaygunCellShots)
                            ZombiesDowned.SetChamber(chamber, ZombiesDowned.FullCellTpl);
                }
                else if (w.TemplateId.ToString() == ThundergunId)
                {
                    Ready(w); // chamber first (Ready also fills the canister), then the canister to what's left
                    TopUpMag(w);
                }
                else if (IsLoose(w))
                    Ready(w);
                else
                    TopUpMag(w);
                _shownMag = -1;
                ZombiesPlugin.Log.LogInfo($"Zombies ammo: Max Ammo loaded {w.LocalizedName()} full");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies ammo: Max Ammo couldn't load the held gun ({e.Message})");
            }
        }

        /// <summary>The gun's mag full again (its reserve untouched): the starting M1911 racked, then 7 in the mag + 1 = 8.</summary>
        public static void TopUpMag(Weapon w)
        {
            Magazine inGun = w?.GetCurrentMagazine();
            if (inGun != null)
                SetRounds(inGun, Capacity(w, inGun, true), w.Template.defAmmo);
        }

        public static void LogState()
        {
            if (Guns.Count == 0)
                ZombiesPlugin.Log.LogInfo("Zombies ammo: no guns counted yet");
            foreach (var kv in Guns)
                ZombiesPlugin.Log.LogInfo($"Zombies ammo: gun {kv.Key}: reserve {kv.Value.Reserve}, in gun {kv.Value.InGun ?? "-"} ({LastCount(kv.Value.InGun ?? "")}), " +
                                          $"spare {kv.Value.Spare ?? "-"} ({LastCount(kv.Value.Spare ?? "")}), mag {kv.Value.MagTemplate}, ammo {kv.Value.AmmoTemplate}");
        }

        public static void Tick(Player player)
        {
            if (!(player?.HandsController?.Item is Weapon weapon))
            {
                _shownMag = -1;
                return;
            }
            // The counter keeps its numbers through a reload and changes once the reload is done 
            bool reloading = player.HandsController is Player.FirearmController firearm && firearm.IsInReloadOperation();
            if (!reloading || _shownGun != weapon.Id)
                _shownMag = -1;
            InventoryController inventory = player.InventoryController;
            if (!reloading)
                SweepSpares(inventory);
            Magazine inGun = weapon.GetCurrentMagazine();
            if (!Guns.TryGetValue(weapon.Id, out GunAmmo gun))
            {
                gun = new GunAmmo { Reserve = ReserveFor(weapon), AmmoTemplate = weapon.Template.defAmmo };
                Guns[weapon.Id] = gun;
            }
            if (ShowLauncher(player))
            {
                _shownGun = weapon.Id;
                return;
            }
            if (IsLoose(weapon))
            {
                TickLoose(player, weapon, gun, reloading);
                return;
            }
            if (inGun != null)
                gun.MagTemplate = inGun.TemplateId;

            // The mag that was in the gun came out: in the pockets it's the spare now; gone a moment later (a quick
            // reload drops it), its rounds go back to the reserve. Mid-reload it can be in neither for a moment.
            if (gun.InGun != null && gun.InGun != inGun?.Id)
            {
                gun.Out = gun.InGun;
                gun.OutSince = Time.time;
                gun.Pool = true;
            }
            gun.InGun = inGun?.Id;
            if (gun.Out != null && gun.Out != gun.InGun)
            {
                Magazine old = Find<Magazine>(inventory, gun.Out);
                if (old != null)
                {
                    // A second spare goes back to the reserve; the old spare that's just gone into the gun isn't one
                    // (never mid-reload: EFT still holds both mags and throws if one goes, the hands stuck for good)
                    if (!reloading && gun.Spare != null && gun.Spare != old.Id && gun.Spare != gun.InGun && Find<Magazine>(inventory, gun.Spare) is Magazine extra)
                        Merge(extra, gun);
                    gun.Spare = old.Id;
                    gun.Out = null;
                }
                else if (Time.time - gun.OutSince > 1.2f)
                {
                    gun.Reserve += LastCount(gun.Out);
                    gun.Out = null;
                }
            }
            else if (gun.Out != null)
                gun.Out = null;
            if (inGun != null)
                Counts[inGun.Id] = inGun.Count;

            // The spare: kept topped up from the reserve, made again when there's none
            Magazine spare = gun.Spare != null ? Find<Magazine>(inventory, gun.Spare) : null;
            if (spare != null && spare.Id == gun.InGun)
                spare = null;
            if (spare == null)
            {
                gun.Spare = null;
                // Only once the gun has a mag in (a gun just handed over brings its own mag to load first)
                if (!reloading && gun.Reserve > 0 && inGun != null && gun.Out == null && gun.MagTemplate != null && Time.time >= _nextSpare)
                {
                    _nextSpare = Time.time + 1f;
                    spare = MakeSpare(inventory, gun, weapon);
                }
            }
            // Mid-reload the spare may be the mag EFT is swapping out: leave it alone until the reload is done (taking
            // the last empty mag away here threw "Trying to get parent of an item that doesn't have a parent" in EFT's
            // reload and the hands never came back)
            // The original's reload: everything left (the mag that came out, the gun's new one and the reserve) is one pool; the
            // gun's mag is filled from it and the rest is reserve (18 in the mag + 20 left reloads to 30 / 8, not two
            // separate mags each keeping its own rounds)
            if (gun.Pool && !reloading && spare != null && inGun != null && !IsWonderWeapon(weapon))
            {
                gun.Pool = false;
                int total = inGun.Count + spare.Count + gun.Reserve;
                int inGunNow = Mathf.Min(Capacity(weapon, inGun, true), total);
                int rest = total - inGunNow;
                int spareNow = Mathf.Min(Capacity(weapon, spare, false), rest);
                if (SetRounds(inGun, inGunNow, gun.AmmoTemplate) && SetRounds(spare, spareNow, gun.AmmoTemplate))
                {
                    TellHost(spare);
                    gun.Reserve = rest - spareNow;
                    Counts[inGun.Id] = inGun.Count;
                    ZombiesDebug.Log($"ammo: reload pooled: {inGunNow} in the gun, {rest} left ({spareNow} in the spare, {gun.Reserve} reserve)");
                }
            }
            if (spare != null && !reloading)
            {
                int room = Capacity(weapon, spare, false) - spare.Count;
                int take = Mathf.Min(room, gun.Reserve);
                if (take > 0 && SetRounds(spare, spare.Count + take, gun.AmmoTemplate))
                {
                    if (ZombiesDebug.InfiniteAmmo?.Value != true)
                        gun.Reserve -= take;
                    ZombiesDebug.Log($"ammo: spare topped up by {take}, reserve {gun.Reserve}");
                    TellHost(spare);
                }
                Counts[spare.Id] = spare.Count;
                // Nothing left anywhere: no spare, so the gun can't be reloaded with an empty mag
                if (spare.Count == 0 && gun.Reserve == 0)
                {
                    var removed = ItemManipulator.Remove(spare, inventory, false, true);
                    ZombiesItems.Removed(inventory, removed, spare);
                    gun.Spare = null;
                    spare = null;
                }
            }

            if (reloading && _shownMag >= 0)
                return;
            _shownGun = weapon.Id;
            _shownMag = inGun?.Count ?? 0;
            if (weapon.Chambers.Length > 0 && weapon.Chambers[0].ContainedItem != null)
                _shownMag++;
            _shownReserve = gun.Reserve + (spare?.Count ?? 0);
        }

        // ---------- Guns without a mag to swap: barrels (double-barrel), tubes (pump shotguns), cylinders ----------

        /// <summary>
        /// A gun that is reloaded with loose rounds, not a spare mag: its barrels (OnlyBarrel) or its built-in tube or
        /// cylinder (InternalMagazine). EFT's R key finds ammo stacks lying in the rig, pockets or backpack and loads
        /// them, so for these guns the reserve is kept as one stack of the gun's round (see <see cref="TickLoose"/>).
        /// </summary>
        /// <summary>
        /// The original's reload rule (the user, 2026-11-08): no reload with the gun already full, and none with nothing left to
        /// load (reserve 0, no loaded spare mag, no loose rounds of its calibre): only the dry click when fired.
        /// Returns why not, or null when a reload is fine.
        /// </summary>
        public static string NoReload(Player me)
        {
            try
            {
                if (!(me?.HandsController?.Item is Weapon w) || me.InventoryController == null)
                    return null;
                // On the underbarrel launcher (the M16's M203): its own barrel counts, not the rifle's mag (the rule
                // said "the gun is full" with the rifle full and the launcher empty, so it never reloaded: user,
                // 2026-11-10). Loaded = full; empty = EFT's own launcher reload decides (it finds the 40mm rounds)
                if (me.HandsController is Player.FirearmController lfc && SafeLauncherMode(lfc))
                    return (lfc.UnderbarrelWeapon?.ChamberAmmoCount ?? 0) > 0 ? "the launcher is loaded" : null;
                Magazine mag = w.GetCurrentMagazine();
                bool chambered = w.Chambers.Length == 0 || w.Chambers.All(Live);
                bool full;
                if (mag != null)
                    full = mag.Count >= Capacity(w, mag, true) && (chambered || w.TemplateId.ToString() == ThundergunId);
                else if (w.TemplateId.ToString() == RaygunId)
                    // The Ray Gun's chambers always hold a cell: full = both cells at 10 shots (it said "full" with
                    // spent cells in, so R never reloaded: user, 2026-11-09)
                    full = w.Chambers.Length > 0 && w.Chambers.All(c => c.ContainedItem != null && RaygunCellLevel(c.ContainedItem) >= RaygunCellShots);
                else
                    full = w.Chambers.Length > 0 && w.Chambers.All(Live);
                if (full)
                    return "the gun is full";
                int reserve = Guns.TryGetValue(w.Id, out GunAmmo gun) ? gun.Reserve : 0;
                if (reserve > 0)
                    return null;
                string caliber = w.AmmoCaliber;
                foreach (Item it in me.InventoryController.Inventory.Equipment.GetAllItems())
                {
                    if (it == w || it.Parent?.Container?.ParentItem == w || it.GetAllParentItems().Contains(w))
                        continue;
                    if (it is Magazine m && m.Count > 0 && w.GetMagazineSlot()?.CanAccept(m) == true)
                        return null;
                    if (it is Ammo a && a.Caliber == caliber && !(a.Parent?.Container?.ParentItem is Magazine))
                        return null;
                }
                return "nothing left to load";
            }
            catch
            {
                return null; // never stop a reload over a check that failed
            }
        }

        public static bool IsLoose(Weapon w)
        {
            try
            {
                Weapon.EReloadMode mode = w.ReloadMode;
                return mode == Weapon.EReloadMode.OnlyBarrel || mode == Weapon.EReloadMode.InternalMagazine;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A gun made to be handed over, ready to fire like a new one in the original: a fitted mag, tube or cylinder full, and a
        /// round in every empty chamber (both barrels of a double-barrel). Its rounds are the gun's standard ammo.
        /// </summary>
        public static void Ready(Weapon w)
        {
            string ammo = w?.Template.defAmmo;
            if (w == null || string.IsNullOrEmpty(ammo) || Singleton<ItemFactory>.Instance == null)
                return;
            Magazine inGun = w.GetCurrentMagazine();
            if (inGun != null)
                SetRounds(inGun, Capacity(w, inGun, true), ammo);
            foreach (Slot chamber in w.Chambers)
            {
                if (chamber.ContainedItem != null || !(Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), ammo, null) is Ammo round))
                    continue;
                round.StackObjectsCount = 1;
                chamber.AddWithoutRestrictions(round);
            }
        }

        // The reserve of a loose-rounds gun: one stack of its round in the gear, topped up from the reserve whenever EFT's
        // reload has used some (the R key picks it up), made again when it's gone. The counter adds the stack to the reserve.
        private static void TickLoose(Player player, Weapon weapon, GunAmmo gun, bool reloading)
        {
            InventoryController inventory = player.InventoryController;
            Magazine tube = weapon.GetCurrentMagazine();
            Ammo stack = gun.Spare != null ? Find<Ammo>(inventory, gun.Spare) : null;
            // Only a stack still lying in a grid is the pile: a stack loaded whole into a barrel is the chamber's round now
            if (stack != null && (!(stack.Parent is GridItemAddress) || stack.StackObjectsCount <= 0))
                stack = null;
            if (stack == null)
            {
                gun.Spare = null;
                if (gun.Reserve > 0 && !string.IsNullOrEmpty(gun.AmmoTemplate) && Time.time >= _nextSpare)
                {
                    _nextSpare = Time.time + 1f;
                    stack = MakeLooseStack(inventory, gun);
                }
            }
            // The War Machine (a revolving cylinder): EFT loads one shell a reload; as one ends, every empty camora is
            // filled from the pile and the reserve (the original's reload fills it up, user 2026-11-09)
            bool wasReloading = _cylReloading;
            _cylReloading = reloading;
            if (wasReloading && !reloading && tube is CylinderMagazine cyl && cyl.Camoras != null)
            {
                int loaded = cyl.Camoras.Count(c => c.ContainedItem != null);
                int want = cyl.Camoras.Length - loaded;
                int fromPile = Mathf.Min(want, stack?.StackObjectsCount ?? 0);
                int fromReserve = Mathf.Min(want - fromPile, gun.Reserve);
                int add = fromPile + fromReserve;
                string round = (stack?.TemplateId.ToString()) ?? gun.AmmoTemplate;
                if (add > 0 && SetCylinder(cyl, loaded + add, round))
                {
                    if (ZombiesDebug.InfiniteAmmo?.Value != true)
                    {
                        if (stack != null && fromPile > 0)
                        {
                            if (stack.StackObjectsCount > fromPile)
                            {
                                stack.StackObjectsCount -= fromPile;
                                TellHost(stack);
                            }
                            else
                            {
                                var removed = ItemManipulator.Remove(stack, inventory, false, true);
                                ZombiesItems.Removed(inventory, removed, stack);
                                gun.Spare = null;
                                stack = null;
                            }
                        }
                        gun.Reserve -= fromReserve;
                    }
                    ZombiesDebug.Log($"ammo: {weapon.LocalizedName()} cylinder filled to {loaded + add}/{cyl.Camoras.Length} after the reload");
                }
            }
            bool raygun = weapon.TemplateId.ToString() == RaygunId;
            if (stack != null && raygun)
            {
                // The Ray Gun's reserve is shots (the original's 160); the pile is full cells of ten taken from it
                int cells = Mathf.Min(Mathf.Max(stack.StackMaxSize, 1) - stack.StackObjectsCount, gun.Reserve / RaygunCellShots);
                // Never mid-reload: the host's copy of the pile is replaced on a top-up (TellHost), and replacing it
                // while its reload was taking rounds from it aborted the reload there (the pump shotgun vanished from
                // the hands after a reload, user 2026-11-10)
                if (cells > 0 && !reloading)
                {
                    stack.StackObjectsCount += cells;
                    if (ZombiesDebug.InfiniteAmmo?.Value != true)
                        gun.Reserve -= cells * RaygunCellShots;
                    TellHost(stack);
                    ZombiesDebug.Log($"ammo: Ray Gun cells topped up by {cells}, reserve {gun.Reserve} shots");
                }
            }
            else if (stack != null)
            {
                int take = Mathf.Min(Mathf.Max(stack.StackMaxSize, 1) - stack.StackObjectsCount, gun.Reserve);
                if (take > 0 && !reloading)
                {
                    stack.StackObjectsCount += take;
                    if (ZombiesDebug.InfiniteAmmo?.Value != true)
                        gun.Reserve -= take;
                    TellHost(stack);
                    ZombiesDebug.Log($"ammo: loose stack topped up by {take}, reserve {gun.Reserve}");
                }
            }
            // Mid-reload the counter waits (a swapped mag is half in), except a pump's tube: it counts up shell by shell
            // as each goes in (user, 2026-11-10: it only moved once the reload was done)
            bool shellByShell = tube != null && !(tube is CylinderMagazine) && weapon.ReloadMode == Weapon.EReloadMode.InternalMagazine;
            if (reloading && _shownMag >= 0 && !shellByShell)
                return;
            _shownGun = weapon.Id;
            if (raygun)
            {
                // Shots left in its two cells, and the reserve in shots (the pile's cells ten each). Porter's X2 fires
                // twice per charge: shown in shots (the Ray Gun mod's own count), the reserve doubled too. Shown in
                // charges, the counter ran at half the firing speed (user, 2026-11-09: "outpacing the counter")
                bool x2 = ZombiesPaP.Infused(weapon);
                int shots = 0;
                foreach (Slot chamber in weapon.Chambers)
                    shots += RaygunCellLevel(chamber.ContainedItem);
                if (x2)
                    shots = RaygunShotsLeft(weapon) ?? shots * 2;
                _shownMag = shots;
                _shownReserve = (gun.Reserve + (stack?.StackObjectsCount ?? 0) * RaygunCellShots) * (x2 ? 2 : 1);
                return;
            }
            _shownMag = (tube?.Count ?? 0) + weapon.ChamberAmmoCount;
            _shownReserve = gun.Reserve + (stack?.StackObjectsCount ?? 0);
        }

        private const int RaygunCellShots = 10;
        private const string LauncherRound = "5ede474b0c226a66f5402622"; // M381, the M203's round

        // The underbarrel launcher selected (the PaP'd M16's M203): its own round and the spare 40mm shells on the
        // counter (user, 2026-11-09: it stayed on the rifle's mag; the check only ran for loose-round guns)
        private static bool ShowLauncher(Player player)
        {
            if (!(player.HandsController is Player.FirearmController lfc) || !SafeLauncherMode(lfc))
            {
                _launcherMag = -1;
                return false;
            }
            // EFT moves the shell into the launcher as its reload starts: the counter keeps the old numbers until the
            // reload is over (user, 2026-11-10: it went 0 -> 1 at the start of the animation)
            if (lfc.CurrentOperation is Player.FirearmController.LauncherReload && _launcherMag >= 0)
            {
                _shownMag = _launcherMag;
                _shownReserve = _launcherReserve;
                return true;
            }
            _shownMag = _launcherMag = lfc.UnderbarrelWeapon?.ChamberAmmoCount ?? 0;
            _shownReserve = _launcherReserve = player.InventoryController.Inventory.Equipment.GetAllItems().OfType<Ammo>()
                .Where(a => a.TemplateId.ToString() == LauncherRound && !(a.CurrentAddress?.Container is Slot slot && slot.ParentItem is Weapon))
                .Sum(a => a.StackObjectsCount);
            return true;
        }

        private static int _launcherMag = -1, _launcherReserve;

        private static bool SafeLauncherMode(Player.FirearmController fc)
        {
            try
            {
                return fc.IsInLauncherMode();
            }
            catch
            {
                return false;
            }
        }
        private static float _nextNoRoomLog;

        private static System.Reflection.MethodInfo _shotsLeft;
        private static bool _shotsLooked;

        // The Ray Gun mod's own shots-left count (knows Porter's X2's half-spent charges)
        private static int? RaygunShotsLeft(Weapon weapon)
        {
            if (!_shotsLooked)
            {
                _shotsLooked = true;
                _shotsLeft = HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("Raygun.RaygunCells"), "ShotsLeft");
            }
            try
            {
                return _shotsLeft != null ? (int?)(int)_shotsLeft.Invoke(null, new object[] { weapon }) : null;
            }
            catch
            {
                return null;
            }
        }
        private const string RaygunCellPrefix = "6a7e5a71d9a11600000000";

        /// <summary>Shots in a Ray Gun cell (its template is the charge level: ...c1 one shot to ...ca ten), else 0.</summary>
        public static int RaygunCellLevel(Item item)
        {
            string tpl = item?.TemplateId.ToString();
            if (tpl == null || tpl.Length != 24 || !tpl.StartsWith(RaygunCellPrefix))
                return 0;
            int level = System.Convert.ToInt32(tpl.Substring(22), 16) - 0xC0;
            return level >= 1 && level <= RaygunCellShots ? level : 0;
        }

        /// <summary>
        /// A Ray Gun reload throws the old cells out as loot with their charges (the Ray Gun mod's rule outside zombies).
        /// In a match it's the original's reload: what's left in them goes back into the reserve, nothing lands on the floor (no
        /// free cells to pick up again). Prefix on EFT's Firearms.ThrowPatronAsLoot.
        /// </summary>
        public static void PatchRaygunCells(HarmonyLib.Harmony harmony)
        {
            var m = HarmonyLib.AccessTools.Method(typeof(EFT.Firearms), nameof(EFT.Firearms.ThrowPatronAsLoot));
            if (m != null)
                harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(ZombiesAmmo), nameof(CellBackToReserve)));
        }

        // ---------- The War Machine: always the original's open-and-dump reload (user, 2026-11-10) ----------
        // EFT plays the whole-cylinder dump only on a quick reload or an empty cylinder, and only with some mastering of
        // the gun; otherwise it picks the spent shells out one by one. In a match each War Machine reload is made one with
        // an empty cylinder: the live shells still in it go back to the reserve first (the fill at the reload's end puts
        // them back in), and the gun's mastering reads at least 1.
        private const string WarMachineId = "6275303a9f372d6ea97f9ec7";

        public static void PatchWarMachine(HarmonyLib.Harmony harmony)
        {
            // Its own Start(ammoPack, callback, quickReload) only: a plain "Start" lookup found the base
            // ObjectInHandsOperation.Start() that every hands operation runs, and the prefix read an empty-hands
            // operation as a cylinder reload: both games crashed on spawning (2026-11-10 20:34)
            System.Reflection.MethodInfo start = HarmonyLib.AccessTools.GetDeclaredMethods(typeof(Player.FirearmController.ReloadCylinderMagOperation))
                .FirstOrDefault(m => m.Name == "Start" && m.GetParameters().Length == 3);
            if (start == null)
                ZombiesPlugin.Log.LogWarning("Zombies War Machine: its reload's own Start wasn't found: the all-shell dump is off");
            else
                harmony.Patch(start, prefix: new HarmonyLib.HarmonyMethod(typeof(ZombiesAmmo), nameof(WarMachineReloadStart)));
            harmony.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(Player.FirearmController), nameof(Player.FirearmController.CurrentMasteringLevel)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(ZombiesAmmo), nameof(WarMachineMastering)));
        }

        private static void WarMachineMastering(Player.FirearmController __instance, ref int __result)
        {
            if (__result < 1 && ZombiesMatch.Active && __instance?.Item?.TemplateId.ToString() == WarMachineId)
                __result = 1;
        }

        private static void WarMachineReloadStart(Player.FirearmController.ReloadCylinderMagOperation __instance)
        {
            try
            {
                Weapon w = __instance?.Weapon;
                if (!ZombiesMatch.Active || w?.TemplateId.ToString() != WarMachineId || __instance.Player == null || !__instance.Player.IsYourPlayer)
                    return;
                if (!(w.GetCurrentMagazine() is CylinderMagazine cyl) || cyl.Camoras == null)
                    return;
                int live = 0;
                foreach (Slot camora in cyl.Camoras)
                    if (camora.ContainedItem is Ammo a && !a.IsUsed)
                    {
                        camora.RemoveItemWithoutRestrictions();
                        live++;
                    }
                if (live > 0 && Guns.TryGetValue(w.Id, out GunAmmo gun))
                    gun.Reserve += live;
                ZombiesDebug.Log($"ammo: War Machine reload: {live} live shell(s) back in the reserve, the cylinder dumped whole");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies ammo: War Machine dump reload not set up ({e.Message})");
            }
        }

        private static bool CellBackToReserve(Item patron, IPlayer player)
        {
            try
            {
                string tpl = patron?.TemplateId.ToString();
                bool cell = tpl != null && tpl.Length == 24 && tpl.StartsWith(RaygunCellPrefix);
                if (!cell || !ZombiesMatch.Active || player == null || !player.IsYourPlayer)
                    return true;
                int level = RaygunCellLevel(patron);
                // Nothing falls out (the dropped models hung in mid-air, the user 2026-11-07): the cell just goes
                Player me = Singleton<GameWorld>.Instance?.MainPlayer;
                if (me?.HandsController?.Item is Weapon w && Guns.TryGetValue(w.Id, out GunAmmo gun))
                {
                    gun.Reserve += level;
                    ZombiesDebug.Log($"ammo: Ray Gun cell with {level} back in the reserve ({gun.Reserve} shots)");
                }
                return false;
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies ammo: Ray Gun cell not taken back ({e.Message})");
                return true;
            }
        }

        private static Ammo MakeLooseStack(InventoryController inventory, GunAmmo gun)
        {
            if (!(Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), gun.AmmoTemplate, null) is Ammo round))
                return null;
            round.StackObjectsCount = 1;
            ItemAddress place = ZombiesItems.FindGrid(inventory, round);
            string error = null;
            if (place == null || !ZombiesItems.Add(inventory, round, place, out error))
            {
                if (Time.time >= _nextNoRoomLog)
                {
                    _nextNoRoomLog = Time.time + 10f;
                    ZombiesDebug.Log($"ammo: no room for the loose stack ({error ?? "no free cell"})");
                }
                return null;
            }
            gun.Spare = round.Id;
            return round;
        }

        // Rounds last seen in each mag (a dropped mag can't be read once it's gone from the inventory)
        private static readonly Dictionary<string, int> Counts = new Dictionary<string, int>();

        private static int LastCount(string magId) => Counts.TryGetValue(magId, out int n) ? n : 0;

        /// <summary>A gun's rounds as the original's counter shows them: in the mag, and in reserve (the spare mag included).</summary>
        /// <summary>
        /// A loose pile whose count we changed (topped up from the reserve): the host's copy is replaced with ours. EFT
        /// only tells the host about its own moves; with a short or empty pile there, a reload from it was applied on our
        /// game only: teammates never saw it, and the host's copy of the gun stayed empty, so our shots with it did
        /// nothing to zombies (the wall-buy MP-43, user 2026-11-09). Only loose piles in the gear: never a mag or a round
        /// inside the gun in the hands (the host waits for the hands to let go of those).
        /// </summary>
        private static void TellHost(Item stack)
        {
            if (stack?.CurrentAddress == null || !ZombiesNet.Online || ZombiesNet.IsHost)
                return;
            try
            {
                ZombiesNet.ItemRemoved(stack);
                ZombiesNet.ItemAdded(stack, stack.CurrentAddress);
            }
            catch (System.Exception e)
            {
                ZombiesDebug.Log($"ammo: couldn't tell the host about the pile ({e.Message})");
            }
        }

        // ---------- What's loaded in the held gun, told to the other games (user, 2026-11-10) ----------
        // Our reserve rules change a mag's rounds on this game only (pooling after a reload, top-ups, the Thundergun's
        // charges): the host's copy ran dry early (shots did nothing) and teammates never saw the Thundergun's charge
        // lights change. The held gun's mag count is sent when it changes (not mid-reload) and set on every other game.
        private static string _roundsSent;
        private static float _nextRoundsSend;

        public static void SyncLoaded(Player me)
        {
            if (!ZombiesNet.Online || Time.time < _nextRoundsSend || !(me?.HandsController?.Item is Weapon w))
                return;
            if (me.HandsController is Player.FirearmController fc && fc.IsInReloadOperation())
                return;
            Magazine mag = w.GetCurrentMagazine();
            if (mag == null || mag is CylinderMagazine)
                return;
            string key = w.Id + "|" + mag.Count;
            if (key == _roundsSent)
                return;
            _roundsSent = key;
            _nextRoundsSend = Time.time + 0.3f;
            ZombiesNet.GunRounds(w.Id.ToString(), mag.Count);
        }

        /// <summary>A teammate's held gun has this many rounds in its mag: set here too.</summary>
        public static void RoundsRemote(Player owner, string gunId, int count)
        {
            try
            {
                if (owner == null || owner.IsYourPlayer)
                    return;
                Weapon w = owner.InventoryController?.Inventory?.Equipment?.GetAllItems().OfType<Weapon>().FirstOrDefault(x => x.Id.ToString() == gunId);
                Magazine mag = w?.GetCurrentMagazine();
                if (mag == null || mag is CylinderMagazine || mag.Count == count)
                    return;
                // Not while their reload plays here (the mag is being swapped)
                if (owner.HandsController is Player.FirearmController fc && fc.IsInReloadOperation())
                    return;
                int was = mag.Count;
                SetRounds(mag, Mathf.Clamp(count, 0, Mathf.Max(count, mag.MaxCount)), w.Template.defAmmo);
                // The Thundergun's charge lights (its own view) redrawn for the new count
                GameObject hands = owner.HandsController?.ControllerGameObject;
                if (w.TemplateId.ToString() == ThundergunId)
                {
                    int views = 0;
                    if (hands != null)
                        foreach (MonoBehaviour view in hands.GetComponentsInChildren<MonoBehaviour>(true))
                            if (view != null && view.GetType().Name == "ThundergunView")
                            {
                                view.SendMessage("Refresh", SendMessageOptions.DontRequireReceiver);
                                views++;
                            }
                    // Logger (user to confirm the other player sees the Thundergun's reload + charge lights, 2026-11-10)
                    ZombiesPlugin.Log.LogInfo($"Zombies ammo: {owner.Profile?.Nickname ?? "teammate"}'s Thundergun {was} -> {mag.Count} charges here, lights redrawn on {views} view(s){(hands == null ? " (no hands yet)" : "")}");
                }
            }
            catch (System.Exception e)
            {
                ZombiesDebug.Log($"ammo: a teammate's rounds couldn't be set ({e.Message})");
            }
        }

        // ---------- The gun's name over the counter for a moment on a swap (user, 2026-11-10) ----------
        private static string _nameShown, _nameFor;
        private static float _nameUntil;
        private static GUIStyle _nameStyle;
        private const float NameSeconds = 2.5f, NameFade = 0.6f;

        private static void DrawName(float s, float right, float top)
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me?.HandsController?.Item is Weapon held)
            {
                string id = held.Id.ToString() + (ZombiesPaP.Infused(held) ? "+" : "");
                if (id != _nameFor)
                {
                    _nameFor = id;
                    _nameShown = held.ShortName.Localized();
                    string full = held.LocalizedName();
                    if (!string.IsNullOrEmpty(full) && full.Length <= 26)
                        _nameShown = full;
                    _nameUntil = Time.time + NameSeconds;
                }
            }
            float left = _nameUntil - Time.time;
            if (left <= 0f || string.IsNullOrEmpty(_nameShown))
                return;
            if (_nameStyle == null)
                _nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight, fontStyle = FontStyle.Bold };
            _nameStyle.fontSize = Mathf.RoundToInt(24 * s);
            float a = Mathf.Clamp01(left / NameFade);
            var r = new Rect(right - 600 * s, top - 34 * s, 600 * s, 32 * s);
            Shadowed(r, new GUIContent(_nameShown.ToUpperInvariant()), _nameStyle, new Color(1f, 1f, 1f, a), s);
        }

        public static void Shown(Weapon w, out int mag, out int reserve)
        {
            // The gun in the hands: exactly what the counter shows (chambered rounds, a shotgun's barrels, the loose
            // pile all counted). The old sums missed those, so "out of ammo" played with rounds left (user, 2026-11-09)
            if (w != null && _shownGun == w.Id && _shownMag >= 0)
            {
                mag = _shownMag;
                reserve = _shownReserve;
                return;
            }
            mag = w?.GetCurrentMagazine()?.Count ?? 0;
            reserve = 0;
            if (w != null && Guns.TryGetValue(w.Id, out GunAmmo gun))
                reserve = gun.Reserve + (gun.Spare != null ? LastCount(gun.Spare) : 0);
        }

        private static void Merge(Magazine extra, GunAmmo gun)
        {
            gun.Reserve += extra.Count;
            SetRounds(extra, 0, gun.AmmoTemplate);
        }

        private static T Find<T>(InventoryController inventory, string id) where T : Item =>
            inventory.Inventory.Equipment.GetAllItems().FirstOrDefault(i => i.Id == id) as T;

        private static readonly HashSet<string> SpareFailLogged = new HashSet<string>();

        private static Magazine MakeSpare(InventoryController inventory, GunAmmo gun, Weapon weapon)
        {
            if (!(Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), gun.MagTemplate, null) is Magazine mag))
                return null;
            ItemAddress place = ZombiesItems.FindGrid(inventory, mag);
            string why = null;
            if (place == null)
                why = "no room in the rig or pockets";
            else if (!ZombiesItems.Add(inventory, mag, place, out string error))
                why = error;
            if (why != null)
            {
                if (SpareFailLogged.Add(weapon.Id + why))
                    ZombiesPlugin.Log.LogWarning($"Zombies ammo: no spare {mag.LocalizedName()} for the {weapon.LocalizedName()}: {why}");
                return null;
            }
            gun.Spare = mag.Id;
            return mag;
        }

        private static bool SetCylinder(CylinderMagazine cylinder, int count, string ammoTemplate)
        {
            Slot[] camoras = cylinder.Camoras;
            if (camoras == null)
                return false;
            count = Mathf.Clamp(count, 0, camoras.Length);
            int have = camoras.Count(c => c.ContainedItem != null);
            for (int i = 0; i < camoras.Length && have > count; i++)
                if (camoras[i].ContainedItem != null)
                {
                    camoras[i].RemoveItemWithoutRestrictions();
                    have--;
                }
            for (int i = 0; i < camoras.Length && have < count; i++)
            {
                if (camoras[i].ContainedItem != null || string.IsNullOrEmpty(ammoTemplate))
                    continue;
                if (!(Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), ammoTemplate, null) is Ammo round))
                    return false;
                round.StackObjectsCount = 1;
                if (camoras[i].AddWithoutRestrictions(round).Succeeded)
                    have++;
            }
            return have == count;
        }

        /// <summary>
        /// A chamber with a round that can still fire. A double-barrel (MP-43-1C) keeps its spent shells in the barrels
        /// until reloaded, so "something in it" read as full and R did nothing after a shot (user, 2026-11-09).
        /// </summary>
        private static bool Live(Slot chamber) => chamber.ContainedItem != null && !(chamber.ContainedItem is Ammo a && a.IsUsed);

        // Our special guns: their own rules on how much a mag holds
        internal const string ThundergunId = "6a7e5a71d9a1150000000001", RaygunId = "6a7e5a71d9a1160000000001";

        private static System.Reflection.MethodInfo _raygunBootUp, _thundergunBootUp;

        /// <summary>
        /// The Ray Gun's next draw is the original's boot-up (Ray Gun mod's RaygunBootUp.Next): only when it's newly in the hands,
        /// out of the box or Pack-a-Punch; every other draw is the plain pullout. Nothing for other guns or an older Ray Gun.
        /// </summary>
        public static void BootUpIfRaygun(Item w)
        {
            if (w == null)
                return;
            string tpl = w.TemplateId.ToString();
            if (tpl != RaygunId && tpl != ThundergunId)
                return;
            try
            {
                // The Thundergun the same way (Thundergun mod's ThundergunBootUp.Next: its first raise only when new)
                if (tpl == ThundergunId)
                {
                    _thundergunBootUp = _thundergunBootUp ?? AccessTools.Method(AccessTools.TypeByName("Thundergun.ThundergunBootUp"), "Next");
                    _thundergunBootUp?.Invoke(null, null);
                    return;
                }
                _raygunBootUp = _raygunBootUp ?? AccessTools.Method(AccessTools.TypeByName("Raygun.RaygunBootUp"), "Next");
                _raygunBootUp?.Invoke(null, null);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: the Ray Gun boot-up couldn't be asked for ({e.Message})");
            }
        }

        public static bool IsThundergun(Item w) => w != null && w.TemplateId.ToString() == ThundergunId;

        public static bool IsWonderWeapon(Item w) => w != null && (w.TemplateId.ToString() == ThundergunId || w.TemplateId.ToString() == RaygunId);

        /// <summary>
        /// How many rounds this mag of this gun holds full: the Thundergun mod's canister holds 2 charges (4 on a Zeus
        /// Cannon, Pack-a-Punched), counting the one in the chamber; every other mag its own capacity.
        /// </summary>
        public static int Capacity(Weapon w, Magazine mag, bool inGun)
        {
            if (mag == null)
                return 0;
            ForceMagSize(w, mag);
            if (w != null && w.TemplateId.ToString() == ThundergunId)
            {
                int charges = ZombiesPaP.Infused(w) ? 4 : 2;
                bool chambered = inGun && w.Chambers.Length > 0 && w.Chambers[0].ContainedItem != null;
                return Mathf.Min(mag.MaxCount, charges - (chambered ? 1 : 0));
            }
            if (mag is CylinderMagazine c && c.Camoras != null)
                return c.Camoras.Length;
            // A pump's tube (built-in mag): full is the whole tube plus the chamber, as EFT's own shell-by-shell reload and
            // a fresh gun both load it (user, 2026-11-10: 8 shells in the M870, but shot to 7 it wouldn't reload: the flat
            // count below called a 6 + 1 tube full)
            if (w != null && w.ReloadMode == Weapon.EReloadMode.InternalMagazine)
                return mag.MaxCount;
            // Flat numbers (the original, user 2026-11-09): a 30-round gun holds 30 in all, the chambered round counted in, not 30 + 1
            // (the extra one left burst guns on a lone round and read wrong on the counter)
            bool loaded = inGun && w != null && w.Chambers.Length > 0 && w.Chambers[0].ContainedItem != null;
            return Mathf.Max(1, mag.MaxCount - (loaded ? 1 : 0));
        }

        /// <summary>
        /// A box or wall gun swapped away leaves its spare mag behind in the rig or pockets: taken away (its gun is
        /// gone), or the pockets fill with old mags and the next gun gets no spare (no reload: Saiga, Thundergun).
        /// </summary>
        private static float _nextSweep;

        private static void SweepSpares(InventoryController inventory)
        {
            if (Time.time < _nextSweep)
                return;
            _nextSweep = Time.time + 2f;
            foreach (var kv in Guns.ToList())
            {
                if (kv.Value.Spare == null || Find<Weapon>(inventory, kv.Key) != null)
                    continue;
                Magazine stale = Find<Magazine>(inventory, kv.Value.Spare);
                if (stale != null && stale.Parent != null)
                {
                    var removed = ItemManipulator.Remove(stale, inventory, false, true);
                    ZombiesItems.Removed(inventory, removed, stale);
                    ZombiesPlugin.Log.LogInfo($"Zombies ammo: spare {stale.LocalizedName()} taken away (its gun is gone)");
                }
                Guns.Remove(kv.Key);
            }
        }

        /// <summary>This mag full for this gun (a cylinder camora by camora).</summary>
        public static bool FillMag(Weapon w, Magazine mag) => mag != null && SetRounds(mag, Capacity(w, mag, false), w.Template.defAmmo);

        /// <summary>Puts exactly this many rounds of the gun's standard ammo in the mag.</summary>
        private static bool SetRounds(Magazine mag, int count, string ammoTemplate)
        {
            // A revolving cylinder (the M32's) holds one round in each camora, not one stack
            if (mag is CylinderMagazine cylinder)
                return SetCylinder(cylinder, count, ammoTemplate);
            count = Mathf.Clamp(count, 0, mag.MaxCount);
            Ammo stack = mag.Cartridges.Items.OfType<Ammo>().FirstOrDefault();
            if (stack != null)
            {
                if (count == 0)
                {
                    mag.Cartridges.Remove(stack, false);
                    return true;
                }
                stack.StackObjectsCount = count - (mag.Count - stack.StackObjectsCount);
                return true;
            }
            if (count == 0 || string.IsNullOrEmpty(ammoTemplate))
                return count == 0;
            if (!(Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), ammoTemplate, null) is Ammo round))
                return false;
            round.StackObjectsCount = count;
            return mag.Cartridges.Add(round, false).Succeeded;
        }

        // ---------- The gun table ----------

        // Guns whose mag size is set by the table, whatever mag is fitted (the Saiga: 16, PaP 24): template -> (mag, PaP mag)
        private static readonly Dictionary<string, (int mag, int pap)> ForcedMags = new Dictionary<string, (int, int)>();

        /// <summary>The size the table forces on this gun's mags, or 0.</summary>
        private static int ForcedMag(Weapon w)
        {
            if (w == null)
                return 0;
            if (_reserves == null)
                LoadReserves();
            return ForcedMags.TryGetValue(w.TemplateId, out var m) ? (ZombiesPaP.Infused(w) ? m.pap : m.mag) : 0;
        }

        /// <summary>A mag of this gun made to hold the table's forced size (EFT's own capacity on it, so reloads agree).</summary>
        private static void ForceMagSize(Weapon w, Magazine mag)
        {
            int want = ForcedMag(w);
            if (want <= 0 || mag?.Cartridges == null || SlotMax == null || mag is CylinderMagazine || mag.Cartridges.MaxCount == want)
                return;
            int before = mag.Cartridges.MaxCount;
            SlotMax.SetValue(mag.Cartridges, want);
            ZombiesPlugin.Log.LogInfo($"Zombies ammo: {mag.LocalizedName()} on the {w.LocalizedName()} holds {want} (was {before})");
        }

        /// <summary>A map's own starting gun: four mags in reserve (the M1911's 8 + 32), never more than its full reserve.</summary>
        public static int StartReserveFor(Weapon weapon)
        {
            int mag = weapon.GetCurrentMagazine()?.MaxCount ?? 0;
            if (mag <= 0)
                mag = 8;
            return Mathf.Min(4 * mag, ReserveFor(weapon));
        }

        private static int ReserveFor(Weapon weapon)
        {
            if (_reserves == null)
                LoadReserves();
            int r = _reserves.TryGetValue(weapon.TemplateId, out int known) ? known : 4 * Mathf.Max(1, weapon.GetMagazineSlot()?.ContainedItem is Magazine m ? m.MaxCount : 30);
            // A forced mag size has its reserve set for it already, PaP'd too (the table's own pap reserve isn't loaded: x1.5)
            // Pack-a-Punched: a bigger stock behind the bigger mag
            return ZombiesPaP.Infused(weapon) ? Mathf.RoundToInt(r * PaPMore) : r;
        }

        // ---------- Pack-a-Punch's bigger mags ----------

        public const float PaPMore = 1.5f;
        private static readonly HashSet<string> Grown = new HashSet<string>();
        private static readonly System.Reflection.FieldInfo SlotMax = AccessTools.Field(typeof(StackSlot), "MaxCount");

        /// <summary>
        /// The Pack-a-Punched guns' mags (the one in the gun and its spare) hold half as many again: EFT's own capacity
        /// on the mag, so its reloads and counts agree. Each mag once; checked every half second in a match.
        /// </summary>
        public static void GrowPaPMags(Player me)
        {
            if (me == null || SlotMax == null)
                return;
            foreach (Weapon w in me.InventoryController.Inventory.Equipment.GetAllItems().OfType<Weapon>())
            {
                if (ForcedMag(w) > 0)
                {
                    ForceMagSize(w, w.GetCurrentMagazine());
                    if (Guns.TryGetValue(w.Id, out GunAmmo forced) && forced.Spare != null)
                        ForceMagSize(w, Find<Magazine>(me.InventoryController, forced.Spare));
                    continue;
                }
                if (!ZombiesPaP.Infused(w) || IsWonderWeapon(w))
                    continue;
                Grow(w.GetCurrentMagazine());
                if (Guns.TryGetValue(w.Id, out GunAmmo gun) && gun.Spare != null)
                    Grow(Find<Magazine>(me.InventoryController, gun.Spare));
            }
        }

        private static void Grow(Magazine mag)
        {
            if (mag?.Cartridges == null || !Grown.Add(mag.Id))
                return;
            int before = mag.Cartridges.MaxCount;
            SlotMax.SetValue(mag.Cartridges, Mathf.RoundToInt(before * PaPMore));
            ZombiesPlugin.Log.LogInfo($"Zombies PaP: {mag.LocalizedName()} holds {mag.Cartridges.MaxCount} now (was {before})");
        }

        // guns.json's reserves, by the weapon template of each gun's logged build in box_builds.json
        private static void LoadReserves()
        {
            _reserves = new Dictionary<string, int>();
            try
            {
                var tpls = new Dictionary<string, string>();
                foreach (JObject b in JArray.Parse(File.ReadAllText(Path.Combine(ZombiesPlugin.Dir, "box_builds.json"))).OfType<JObject>())
                    if ((string)b["id"] is string id && (string)b["base"]?["tpl"] is string tpl)
                        tpls[id] = tpl;
                string guns = Path.Combine(ZombiesPlugin.Dir, "guns.json");
                if (File.Exists(guns))
                    foreach (JObject g in (JObject.Parse(File.ReadAllText(guns))["guns"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        if ((string)g["id"] is string id && tpls.TryGetValue(id, out string tpl) && g["reserve"]?.Type == JTokenType.Integer)
                        {
                            _reserves[tpl] = (int)g["reserve"];
                            if ((bool?)g["forceMag"] == true && g["mag"]?.Type == JTokenType.Integer)
                                ForcedMags[tpl] = ((int)g["mag"], g["pap"]?["mag"]?.Type == JTokenType.Integer ? (int)g["pap"]["mag"] : (int)g["mag"]);
                        }
                    }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies ammo: couldn't read the gun table: {e.Message}");
            }
        }

        // ---------- the original's counter ----------

        private static GUIStyle _big, _small;

        public static void Draw()
        {
            if (_shownMag < 0)
                return;
            if (_big == null)
            {
                _big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight, fontStyle = FontStyle.Bold };
                _small = new GUIStyle(_big);
            }
            // Labels draw only on Repaint; the Layout pass did all the work twice a frame for nothing
            if (Event.current != null && Event.current.type != EventType.Repaint)
                return;
            float s = Screen.height / 1080f;
            // Texts and their sizes only remade when a number or the screen changes (was ~1.2 MB/s of strings)
            if (_drawnMag != _shownMag || _drawnReserve != _shownReserve || _drawnHeight != Screen.height)
            {
                _drawnMag = _shownMag;
                _drawnReserve = _shownReserve;
                _drawnHeight = Screen.height;
                _big.fontSize = Mathf.RoundToInt(54 * s);
                _small.fontSize = Mathf.RoundToInt(30 * s);
                Font f = ZombiesUi.DisplayImguiFont();
                if (f != null)
                    _big.font = _small.font = f;
                _reserveText.text = " / " + _shownReserve;
                _magText.text = _shownMag.ToString();
                _reserveSize = _small.CalcSize(_reserveText);
                _magSize = _big.CalcSize(_magText);
            }
            float right = Screen.width - 60 * s, bottom = Screen.height - 50 * s;
            Vector2 rs = _reserveSize, ms = _magSize;
            // The zombies HUD's plate: dark glass fading out to the left, a red line under it (user, 2026-11-11)
            ZombiesHud.AmmoPlate(right + 40 * s, bottom + 6 * s, 520 * s, ms.y + 26 * s, s);
            Shadowed(new Rect(right - rs.x, bottom - rs.y, rs.x, rs.y), _reserveText, _small, _shownReserve == 0 ? new Color(1f, 0.3f, 0.3f) : new Color(0.82f, 0.8f, 0.76f), s);
            Shadowed(new Rect(right - rs.x - ms.x, bottom - ms.y + 4 * s, ms.x, ms.y), _magText, _big, _shownMag == 0 ? new Color(1f, 0.3f, 0.3f) : Color.white, s);
            // Above the counter and the grenade slots
            DrawName(s, right, bottom - ms.y - 44 * s);
            // The grenades, left of the count
            ZombiesGrenades.Draw(Singleton<GameWorld>.Instance?.MainPlayer, right - rs.x - ms.x - 18 * s, bottom, s);
            LowAmmoPrompt(s);
        }

        // "Press [R] to reload" just under the crosshair once the mag is down to a quarter (user, 2026-11-11): the gun's
        // full mag is the most seen in it since it came out; not with no reserve, not while reloading, not when down
        private static string _fullFor;
        private static int _fullMag;

        private static void LowAmmoPrompt(float s)
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (!(me?.HandsController?.Item is Weapon w) || ZombiesDowned.Mine != ZombiesDowned.State.Up || ZombiesFrontEnd.InMatchMenu
                || ZombiesControls.ReloadPrompt != null && !ZombiesControls.ReloadPrompt.Value)
                return;
            string id = w.Id.ToString();
            if (id != _fullFor)
            {
                _fullFor = id;
                _fullMag = 0;
            }
            // Only numbers the counter shows for THIS gun: just after a swap it still holds the last gun's, and a 10-round
            // gun took a 40-round gun's "full" (user, 2026-11-11: "Press R to reload" on a full mag)
            if (_shownGun != w.Id)
                return;
            // The underbarrel launcher selected: one shell at a time, never "low" (user, 2026-11-11: the M16's M203 had the
            // reminder up all the time); its shell isn't the rifle's mag either, so it doesn't count to the full mag
            if (me.HandsController is Player.FirearmController lfc && SafeLauncherMode(lfc))
                return;
            _fullMag = Mathf.Max(_fullMag, _shownMag);
            if (_fullMag < 4 || _shownReserve <= 0 || _shownMag > Mathf.Max(1, Mathf.FloorToInt(_fullMag * 0.25f)))
                return;
            if (me.HandsController is Player.FirearmController fc && fc.IsInReloadOperation())
                return;
            ZombiesHud.Prompt("Press", ZombiesControls.KeyName(ZombiesControls.Act.Reload), "to reload", null, true, false, 0.535f, s * 0.8f, 0.9f);
        }

        private static readonly GUIContent _reserveText = new GUIContent(), _magText = new GUIContent();
        private static Vector2 _reserveSize, _magSize;
        private static int _drawnMag = int.MinValue, _drawnReserve = int.MinValue, _drawnHeight = -1;

        private static void Shadowed(Rect r, GUIContent text, GUIStyle style, Color colour, float s)
        {
            style.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(r.x + 2 * s, r.y + 2 * s, r.width, r.height), text, style);
            style.normal.textColor = colour;
            GUI.Label(r, text, style);
        }
    }
}
