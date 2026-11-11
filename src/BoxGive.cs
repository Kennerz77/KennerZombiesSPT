using System.Collections;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Hands a mystery box gun to the player in a raid (EFT won't hand items over in the hideout): the gun as logged,
    /// with a full mag of its own standard round. It's given empty with the mag in the rig or pockets, and once it's in
    /// the hands EFT's own reload loads it, so the character puts the mag in and racks it, like a fresh gun in the original.
    /// The original's two guns: the two rifle slots are the zombies mode's two gun slots and take any gun (two rifles, a rifle and
    /// a pistol, two pistols). A third takes the place of the gun in the hands, as in the original (not in the hands: the first).
    /// The holster is only for the last stand's own M1911 (<paramref name="lastStand"/>).
    /// For now this works in any raid (testing); the zombies mode will keep it to zombies matches only.
    /// </summary>
    internal static class BoxGive
    {
        /// <summary>A gun's models (its parts and mag) loaded ahead of time, e.g. the starting M1911 as the map is
        /// built, so giving it later is instant.</summary>
        public static IEnumerator Preload(BoxGuns.Gun gun)
        {
            if (gun == null)
                yield break;
            JObject magPart = null;
            Item item = null;
            try
            {
                item = gun.PresetTemplate != null ? BoxGuns.BuildItem(gun) : BoxGuns.BuildItem(gun.Base, out magPart);
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Preloading the {gun.Name} failed: {e.Message}");
            }
            if (!(item is Weapon weapon))
                yield break;
            float started = Time.realtimeSinceStartup;
            yield return ZombiesItems.LoadModels(weapon, MakeMagazine(weapon, magPart));
            ZombiesPlugin.Log.LogInfo($"Preloaded the {gun.Name}'s models ({Time.realtimeSinceStartup - started:0.0} s)");
        }

        // One give at a time: a second one (two debug gives, a box gun and a wall buy together) waits for the first's
        // draw to finish. Two at once kept refusing each other's draw ("skipped _completed") and left the hands
        // stuck (user, 2026-11-09)
        private static bool _giving;

        public static IEnumerator Give(BoxGuns.Gun gun, Player player, bool lastStand = false)
        {
            float waitUntil = Time.time + 20f;
            while (_giving && Time.time < waitUntil)
                yield return null;
            _giving = true;
            try
            {
                yield return GiveOne(gun, player, lastStand);
            }
            finally
            {
                _giving = false;
            }
        }

        private static IEnumerator GiveOne(BoxGuns.Gun gun, Player player, bool lastStand)
        {
            // The Cymbal Monkey: three into the rig, no gun swapped out
            if (gun?.Id == BoxGuns.MonkeyId)
            {
                yield return ZombiesMonkey.Give(player);
                yield break;
            }
            InventoryController inventory = player?.InventoryController;
            if (inventory == null)
            {
                ZombiesPlugin.Log.LogWarning($"Mystery box: no inventory to give the {gun.Name} to");
                yield break;
            }
            // Never under a running reload: it's skipped to its end first (ZombiesHands)
            yield return ZombiesHands.Settle(player, $"the {gun.Name}");

            JObject magPart = null;
            Item item = gun.PresetTemplate != null ? BoxGuns.BuildItem(gun) : BoxGuns.BuildItem(gun.Base, out magPart);
            if (!(item is Weapon weapon))
            {
                ZombiesPlugin.Notify($"Couldn't make the {gun.Name}.", true);
                yield break;
            }
            ZombiesGunTable.Remember(weapon, gun);
            // Another mod's gun comes as its preset, mag fitted: filled where it is (no rack on the draw)
            if (gun.PresetTemplate != null)
                Fill(weapon, weapon.GetCurrentMagazine());
            Magazine mag = gun.PresetTemplate != null ? null : MakeMagazine(weapon, magPart);
            // No mag to load through EFT's reload (a double-barrel, a pump's tube, a fitted preset mag): it comes ready,
            // tube/mag full and a round in every chamber, as a new gun in the original does
            if (mag == null)
                ZombiesAmmo.Ready(weapon);

            // Room for it: a free weapon slot, or the gun in the hands goes
            // Its models (and its mag's and rounds') loaded first, or EFT's reload can't show the rounds
            // The round's own model only for guns loaded with loose rounds (the M32's cylinder, a pump's tube): EFT's
            // reload shows the round. For the rest it only slowed the load (the starting M1911 took 15-48 s)
            Item sampleRound = string.IsNullOrEmpty(weapon.Template.defAmmo) || !ZombiesAmmo.IsLoose(weapon) ? null
                : Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), weapon.Template.defAmmo, null);
            yield return ZombiesItems.LoadModels(weapon, mag, sampleRound);
            Slot slot = lastStand ? inventory.Inventory.Equipment.GetSlot(EquipmentSlot.Holster) : null;
            if (slot != null && slot.ContainedItem != null)
                slot = null;
            if (!lastStand)
            {
                // Mule Kick: the holster is a third gun slot
                Slot[] slots = GunSlots.Concat(ZombiesPerks.Has("mule") ? new[] { EquipmentSlot.Holster } : new EquipmentSlot[0])
                    .Select(s => inventory.Inventory.Equipment.GetSlot(s)).ToArray();
                slot = slots.FirstOrDefault(s => s.ContainedItem == null);
                if (slot == null)
                {
                    Item inHands = player.HandsController?.Item;
                    slot = slots.FirstOrDefault(s => s.ContainedItem == inHands) ?? slots[0];
                    Item old = slot.ContainedItem;
                    ZombiesPlugin.Log.LogInfo($"Mystery box: {old.LocalizedName()} disappears for the {gun.Name}");
                    // The old gun out of the hands first (EFT won't take away the gun being held)
                    // Never out of its slot while still in the hands (ZombiesHands.PutAway waits for the hands to be
                    // really empty, and puts away past EFT's queue if they never answer)
                    if (player.HandsController?.Item == old)
                    {
                        yield return ZombiesHands.Settle(player, $"the {old.LocalizedName()} going");
                        var away = new ZombiesHands.Outcome();
                        yield return ZombiesHands.PutAway(player, $"the {old.LocalizedName()} going for the {gun.Name}", away);
                    }
                    var removed = ItemManipulator.Remove(old, inventory, false, true);
                    if (removed.Succeeded)
                        ZombiesItems.Removed(inventory, removed, old);
                    else
                        ZombiesPlugin.Log.LogWarning($"Mystery box: EFT won't remove the {old.LocalizedName()}: {removed.Error}");
                    yield return new WaitForSecondsRealtime(0.3f);
                    // Still there: taken out straight (and gone from the teammates' games too)
                    if (slot.ContainedItem != null)
                    {
                        ZombiesNet.ItemRemoved(slot.ContainedItem);
                        var forced = slot.RemoveItemWithoutRestrictions();
                        slot.ApplyContainedItem(); // the body's model of the slot follows
                        ZombiesPlugin.Log.LogInfo($"Mystery box: {old.LocalizedName()} taken out straight ({(forced.Failed ? "failed: " + forced.Error : "done")})");
                    }
                    if (slot.ContainedItem != null)
                    {
                        ZombiesPlugin.Log.LogWarning($"Mystery box: {slot.ID} still holds the {slot.ContainedItem.LocalizedName()}, the {gun.Name} can't go in");
                        slot = null;
                    }
                }
            }
            if (slot == null)
            {
                ZombiesPlugin.Log.LogWarning($"Mystery box: no room for the {gun.Name}");
                ZombiesPlugin.Notify($"No room for the {gun.Name}.", true);
                yield break;
            }

            // The original's gun out of the box is cocked as it comes up: the mag goes in with EFT's own reload from empty (its
            // rack is the only one EFT has), then is topped back to full. Without the rack (F12 off, a wonder weapon):
            // the full mag straight in and a round chambered.
            // Revolvers (the RSh-12's cylinder is its mag) never: EFT can't reload a cylinder that way, and the forced
            // load after left the gun drawn without its cylinder
            bool rack = mag != null && RackOnDraw && !ZombiesAmmo.IsWonderWeapon(weapon) && !(weapon is Revolver) && !(mag is CylinderMagazine);
            // Rack only: the mag already in and chambered as it comes up, then EFT's misfire clear racks it
            bool clear = rack && RackOnly && weapon.Chambers.Length > 0 && !(weapon is Revolver);
            if (clear)
                rack = false;
            // No chamber (open-bolt belt-fed guns: the M60): there's nothing to rack, and the reload from empty left the
            // gun without its box for ~7 s (the user, 2026-11-08): the box straight in instead
            if (rack && weapon.Chambers.Length == 0)
                rack = false;
            if (mag != null && !rack)
            {
                weapon.GetMagazineSlot()?.AddWithoutRestrictions(mag);
                ZombiesReload.Chamber(weapon, mag);
            }

            if (!ZombiesItems.AddToSlot(inventory, weapon, slot, out string addError))
            {
                ZombiesPlugin.Log.LogWarning($"Mystery box: couldn't put the {gun.Name} in {slot.ID}: {addError}");
                ZombiesPlugin.Notify($"Couldn't give the {gun.Name}.", true);
                yield break;
            }
            // Mag full again after the chambered round, and the full reserve behind it
            ZombiesAmmo.TopUpMag(weapon);
            ZombiesAmmo.FillGun(weapon);
            ZombiesPlugin.Log.LogInfo($"Mystery box: {gun.Name} put in {slot.ID}, loaded: {ZombiesTrace.GunText(weapon)}");
            yield return null;

            // Into the hands (EFT's draw); nothing to reload. A draw that fails (its model not loaded yet) is tried
            // again after loading the models once more, so the hands never stay empty
            bool drawnOk = false;
            // A Fika headless host checks the draw against its copy of us: give our item add a moment to land there
            // first, and more tries, further apart (a refused draw leaves the inventory busy on the host a while)
            bool headlessClient = ZombiesNet.HeadlessMatch && !ZombiesNet.IsHost;
            int tries = headlessClient ? 6 : 3;
            // Drawn as soon as the headless says it has the gun (its ItemHere), at most 3 s
            yield return ZombiesNet.UntilHostHas(weapon, gun.Name);
            for (int attempt = 1; attempt <= tries && !drawnOk; attempt++)
            {
                bool done = false, failed = false;
                ZombiesAmmo.BootUpIfRaygun(weapon);
                player.SetInHands(weapon, result =>
                {
                    done = true;
                    failed = result.Failed;
                    if (result.Failed)
                        ZombiesPlugin.Log.LogWarning($"Mystery box: drawing the {gun.Name} failed (try {attempt}): {result.Error}");
                });
                float until = Time.time + 5f;
                while (!done && Time.time < until)
                    yield return null;
                drawnOk = player.HandsController?.Item == weapon && !failed;
                if (!drawnOk && attempt < tries)
                {
                    ZombiesPlugin.Log.LogWarning($"Mystery box: the {gun.Name} isn't in the hands (try {attempt}): loading its models again");
                    yield return ZombiesItems.LoadModels(weapon, weapon.GetCurrentMagazine(), sampleRound);
                    yield return new WaitForSeconds(headlessClient ? 3f : 0.5f);
                }
            }
            ZombiesPlugin.Log.LogInfo($"Mystery box: gave {gun.Name} ({(drawnOk ? "in the hands" : $"NOT in the hands after {tries} tries")}): {ZombiesTrace.GunText(weapon)}");
            if (rack)
                yield return Rack(player, weapon, mag, gun.Name);
            else if (clear && drawnOk)
                yield return RackOnlyClear(player, weapon, gun.Name);
        }

        private static readonly EquipmentSlot[] GunSlots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon };

        /// <summary>The gun's logged magazine (or none), filled with its standard round (the weapon's default ammo).</summary>
        /// <summary>F12 "Box guns are cocked as they come out" (on): EFT's reload from empty plays as a gun arrives.</summary>
        public static bool RackOnDraw => _rack?.Value ?? true;
        /// <summary>F12 "Cock with the mag already in" (on): the rack alone (EFT's misfire clear), no mag going in.</summary>
        public static bool RackOnly => _rackOnly?.Value ?? true;
        private static BepInEx.Configuration.ConfigEntry<bool> _rack, _rackOnly;

        public static void Bind(BepInEx.Configuration.ConfigFile config, string section)
        {
            _rack = config.Bind(section, "Box guns are cocked as they come out", true,
                "A gun from the box or a wall is cocked as it comes up, then topped back to full. Off: the full mag is already in, no rack.");
            _rackOnly = config.Bind(section, "Cock with the mag already in", true,
                "On: the gun comes up with its full mag in and only the rack plays (EFT's misfire clear: the charging handle or slide pulled, a round flips out). Off: EFT's reload from empty (the mag goes in, then the rack).");
        }

        /// <summary>
        /// The rack alone, with the mag already in: EFT has no rack of its own outside a reload, but clearing a misfire
        /// is exactly one (pull the charging handle or slide, the bad round flips out, the next one goes in). The
        /// chambered round is taken out as a misfired one (as EFT's own misfire takes it at the shot), the player
        /// knows what it is, and EFT's check-chamber key action runs the clear. Topped back to full after; if EFT
        /// won't, the state is put back and the gun stays loaded as it was.
        /// </summary>
        private static bool Idle(Player.FirearmController f) => f.CurrentOperation is Player.FirearmController.Idling;

        internal static IEnumerator RackOnlyClear(Player player, Weapon weapon, string name)
        {
            // Settled in the hands first (the draw ending)
            float until = Time.time + 3f;
            while (Time.time < until && !(player.HandsController is Player.FirearmController f0 && f0.Item == weapon && Idle(f0)))
                yield return null;
            if (!(player.HandsController is Player.FirearmController fc) || fc.Item != weapon || weapon.Chambers.Length == 0
                || !(weapon.Chambers[0].ContainedItem is Ammo round) || weapon.GetCurrentMagazine() == null || weapon.GetCurrentMagazine().Count == 0)
            {
                ZombiesPlugin.Log.LogInfo($"Mystery box: the {name} isn't ready for the rack, left loaded");
                yield break;
            }
            Weapon.MalfunctionState malf = weapon.MalfState;
            bool started = false;
            try
            {
                weapon.Chambers[0].RemoveItemWithoutRestrictions();
                malf.AmmoToFire = round;
                malf.MalfunctionedAmmo = round;
                malf.AmmoWillBeLoadedToChamber = null;
                // Silent: the state-changed event would start EFT's own malfunction handling (not idle any more)
                malf.ChangeStateSilent(Weapon.EMalfunctionState.Misfire);
                malf.AddPlayerWhoKnowMalfunction(player.ProfileId);
                malf.AddPlayerWhoKnowMalfType(player.ProfileId);
                string op = fc.CurrentOperation?.GetType().Name;
                started = fc.CheckChamber();
                if (!started)
                    ZombiesPlugin.Log.LogInfo($"Mystery box: the {name}'s rack refused in {op} (trigger {fc.IsTriggerPressed})");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Mystery box: the {name}'s rack couldn't start ({e.Message})");
            }
            if (started)
            {
                until = Time.time + 5f;
                while (Time.time < until && malf.State != Weapon.EMalfunctionState.None)
                    yield return null;
                // The repair's own end (the misfired round thrown, the next one in): back to idle
                until = Time.time + 3f;
                while (Time.time < until && player.HandsController is Player.FirearmController f1 && f1.Item == weapon && !Idle(f1))
                    yield return null;
            }
            if (!started || malf.State != Weapon.EMalfunctionState.None)
            {
                ZombiesPlugin.Log.LogWarning($"Mystery box: the {name}'s rack didn't play ({(started ? "still misfired" : "EFT refused")}): put back");
                malf.ChangeStateSilent(Weapon.EMalfunctionState.None);
                malf.AmmoToFire = null;
                malf.MalfunctionedAmmo = null;
            }
            if (weapon.Chambers[0].ContainedItem == null && weapon.GetCurrentMagazine() is Magazine m && m.Count > 0)
                ZombiesReload.Chamber(weapon, m);
            ZombiesAmmo.TopUpMag(weapon);
            ZombiesPlugin.Log.LogInfo($"Mystery box: {name} racked (mag in): {ZombiesTrace.GunText(weapon)}");
        }

        // The mag into the rig, then EFT's own reload (from empty: mag in, the bolt racked); topped back to full after
        internal static IEnumerator Rack(Player player, Weapon weapon, Magazine mag, string name)
        {
            InventoryController inventory = player.InventoryController;
            ItemAddress place = ZombiesItems.FindGrid(inventory, mag);
            bool placed = place != null && ZombiesItems.Add(inventory, mag, place, out _);
            bool done = false;
            if (placed && player.HandsController is Player.FirearmController firearm && firearm.Item == weapon)
            {
                try
                {
                    firearm.ReloadMag(mag, null, result =>
                    {
                        done = true;
                        if (result.Failed)
                            ZombiesPlugin.Log.LogWarning($"Mystery box: the {name}'s rack failed: {result.Error}");
                    });
                }
                catch (System.Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Mystery box: the {name}'s rack couldn't start ({e.Message})");
                    done = true;
                }
                float until = Time.time + 6f;
                while (!done && Time.time < until)
                    yield return null;
                // Still reloading (a long one): wait for it before touching the mag
                until = Time.time + 4f;
                while (Time.time < until && player.HandsController is Player.FirearmController f && f.IsInReloadOperation())
                    yield return null;
            }
            // Never left without its mag (the rig full, EFT refused): straight in, as without the rack
            if (weapon.GetCurrentMagazine() == null)
            {
                ZombiesPlugin.Log.LogInfo($"Mystery box: the {name} loaded without the rack");
                ZombiesReload.ForceLoad(player, weapon, mag, "box rack missed");
            }
            ZombiesAmmo.TopUpMag(weapon);
            ZombiesPlugin.Log.LogInfo($"Mystery box: {name} racked: {ZombiesTrace.GunText(weapon)}");
        }

        internal static Magazine MakeMagazine(Weapon weapon, JObject magPart)
        {
            if (magPart == null)
                return null;
            ItemFactory factory = Singleton<ItemFactory>.Instance;
            if (!(factory.CreateItem(MongoID.Generate(), (string)magPart["tpl"], null) is Magazine mag))
                return null;
            Fill(weapon, mag);
            return mag;
        }

        /// <summary>Fills an empty magazine with the weapon's standard round (its template's default ammo).</summary>
        private static void Fill(Weapon weapon, Magazine mag)
        {
            if (mag == null || mag.Count > 0)
                return;
            string ammoTpl = weapon.Template.defAmmo;
            if (mag is CylinderMagazine)
            {
                ZombiesAmmo.FillMag(weapon, mag);
                return;
            }
            if (!string.IsNullOrEmpty(ammoTpl) && Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), ammoTpl, null) is Ammo round)
            {
                round.StackObjectsCount = mag.MaxCount;
                var result = mag.Cartridges.Add(round, false);
                if (result.Failed)
                    ZombiesPlugin.Log.LogWarning($"Mystery box: couldn't fill the {mag.LocalizedName()}: {result.Error}");
            }
        }
    }
}
