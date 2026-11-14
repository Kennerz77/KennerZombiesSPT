using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies character: at the start of a zombies match everything the PMC wears and carries goes, except the
    /// player's own look and melee, chosen by wearing them before the match: face cover (mask, balaclava), eyewear and the
    /// knife in the scabbard (the original's quick knife). The pockets and secure container stay but are emptied. Then the ComTac
    /// VI goes on and the M1911 comes out (the original's starter). The server puts the real character back exactly when the raid
    /// ends (ZombiesRaid on the server), so nothing here is ever lost. EFT's networked inventory moves, so Fika players
    /// see each other's kit.
    /// </summary>
    internal static class ZombiesLoadout
    {
        // Everyone hears through the ComTac VI in a match (ZombiesGearFree swaps its sound in, whatever's worn): a player
        // with their own headset keeps it on show; one without gets an invisible ComTac
        public const string ComtacVi = "66b5f6985891c84aab75ca76";
        private const string StarterGun = "m1911";
        // The zombies pockets (server: ZombiesItemIds.Pockets): 48 cells, room for everything a match hands out (spare
        // mags 1x1 to 2x2, 40 mm rounds, grenades, monkeys, perk bottles), so nobody needs a rig or a backpack (the user,
        // 2026-11-08: the inventory is never opened in a match). EFT's reload and quick throw take from the pockets
        private const string BigPockets = "6a8f0e5b0c0a116000000d01";
        // Once given to players with no rig / as a hidden pack; no longer handed out (the pockets hold it all). Kept so a
        // match started on an older build still hides them
        public const string Rig = "628baf0b967de16aab5a4f36";
        public const string Pack = "544a5cde4bdc2d39388b456b";
        // What the player picks for themselves (worn on the PMC before the match)
        // Players dress as they like (the user, 2026-11-07): what's worn stays on as a look (no weight, slowdown, slap
        // protection, visor overlay or mask breathing in a match: ZombiesGearFree), the weapons come off and what's carried
        // is emptied out. Backpacks come off too (the user, 2026-11-08: no bulk on anyone's back)
        private static readonly EquipmentSlot[] Kept = { EquipmentSlot.FaceCover, EquipmentSlot.Eyewear, EquipmentSlot.Scabbard,
            EquipmentSlot.Headwear, EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.ArmBand, EquipmentSlot.Earpiece };
        // Worn but emptied (their own rig, armour's pouches, pockets, secure container)
        private static readonly string[] Emptied = { nameof(EquipmentSlot.Pockets), nameof(EquipmentSlot.SecuredContainer),
            nameof(EquipmentSlot.TacticalVest), nameof(EquipmentSlot.ArmorVest) };

        /// <summary>The starting gun: the map's own (its layout's StartGun) when it's in the box's gun table, else the M1911.</summary>
        public static BoxGuns.Gun Starter
        {
            get
            {
                string own = ZombiesMatch.StartGunHere;
                BoxGuns.Gun gun = own != null && own != StarterGun ? BoxGuns.FindGun(own) : null;
                if (own != null && own != StarterGun && gun == null)
                    ZombiesPlugin.Log.LogWarning($"Zombies loadout: the map's starting gun '{own}' isn't in box_builds.json, giving the M1911");
                return gun ?? BoxGuns.FindGun(StarterGun);
            }
        }

        /// <summary>The kit is on and the M1911 racked: the match's black screen can fade in.</summary>
        // The original's starting M1911: 32 rounds in reserve on top of the 8 loaded
        private const int StartReserve = 32;

        public static bool Ready { get; set; }

        // Co-op: seconds after "hands empty" before a held gun is taken off (the other games catch up first)
        private const float HeldWait = 6f;

        /// <summary>How far the kit is (0..1) and what it's doing, for the loading screen's bar.</summary>
        public static float Progress { get; set; }
        public static string Stage { get; set; } = "";

        private static void Step(float progress, string stage)
        {
            Progress = progress;
            Stage = stage;
        }

        public static IEnumerator Apply(Player player)
        {
            InventoryController inventory = player?.InventoryController;
            if (inventory == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies loadout: no inventory to set");
                yield break;
            }
            // Co-op, joining: nothing changes until the host's game has the match and us in it (its answer to our Hello).
            // Hands emptied before that never reached it: it kept us holding the gun we spawned with and refused every
            // draw after (user, 2026-11-11: the headless came in 25 s after FikaTest1, who held a Ray Gun)
            if (ZombiesNet.Online && !ZombiesNet.IsHost && !ZombiesNet.JoinedHost)
            {
                Step(0.42f, "Waiting for the host");
                float from = Time.realtimeSinceStartup, most = from + 60f, ask = from + 3f;
                while (!ZombiesNet.JoinedHost && Time.realtimeSinceStartup < most)
                {
                    if (Time.realtimeSinceStartup >= ask)
                    {
                        ask += 3f;
                        ZombiesNet.AskHostAgain();
                    }
                    yield return null;
                }
                if (ZombiesNet.JoinedHost)
                    ZombiesPlugin.Log.LogInfo($"Zombies loadout: the host has the match ({Time.realtimeSinceStartup - from:0.0} s): kit going on");
                else
                    ZombiesPlugin.Log.LogWarning("Zombies loadout: no word from the host after 60 s: kit going on anyway");
            }
            Step(0.45f, "Gearing up");
            // Hands emptied first: a gun taken off while held left EFT's hands on an item that wasn't there any more
            // (the user spawned with their Five-seveN out: nothing could be drawn, no shooting, no knife)
            if (player.HandsController != null && !(player.HandsController is Player.EmptyHandsController))
            {
                bool heldWeapon = player.HandsController.Item is Weapon;
                bool joining = ZombiesNet.Online && !ZombiesNet.IsHost;
                // Co-op, joining: the host's copy of us must have finished drawing the gun we spawned with first. Hands
                // emptied while it was still drawing never reached it (its gun models load slowly on a headless): it kept
                // the gun and refused every draw after (user, 2026-11-11, both testers came in with no gun)
                if (joining && heldWeapon)
                {
                    Step(0.45f, "Waiting for the host");
                    string spawnGun = player.HandsController.Item.Id.ToString();
                    float from = Time.realtimeSinceStartup, most = from + 25f;
                    while (Time.realtimeSinceStartup < most && ZombiesNet.HostSeesMe() != spawnGun
                           && (ZombiesNet.HostHandsHeard || Time.realtimeSinceStartup < from + 5f))
                        yield return null;
                    string seen = ZombiesNet.HostSeesMe();
                    ZombiesPlugin.Log.LogInfo(seen == spawnGun
                        ? $"Zombies loadout: the host's copy of us holds our {player.HandsController.Item.LocalizedName()} ({Time.realtimeSinceStartup - from:0.0} s): putting it away"
                        : $"Zombies loadout: the host's copy of us holds {(seen == null ? "nothing we've heard of" : seen == "" ? "nothing" : seen)} after {Time.realtimeSinceStartup - from:0.0} s: putting the gun away anyway");
                }
                bool emptied = false;
                try { player.SetEmptyHands(_ => emptied = true); }
                catch (Exception e) { ZombiesPlugin.Log.LogWarning($"Zombies loadout: emptying the hands failed: {e.Message}"); emptied = true; }
                float until = Time.realtimeSinceStartup + 5f;
                while (!emptied && Time.realtimeSinceStartup < until)
                    yield return null;
                until = Time.realtimeSinceStartup + 2f;
                while (!(player.HandsController is Player.EmptyHandsController) && Time.realtimeSinceStartup < until)
                    yield return null;
                ZombiesPlugin.Log.LogInfo($"Zombies loadout: hands emptied ({player.HandsController?.GetType().Name})");
                // Co-op: the other games (the headless host above all) build their copy of our hands from Fika's
                // messages, a gun's models first. The gun taken off 0.3 s after "hands empty" reached them before
                // their copy had finished taking it out: EFT threw ("Trying to get parent of an item that doesn't have a
                // parent: MDR"), the host's copy kept holding it and refused every draw after, so the M1911 never came
                // out (user, 2026-11-11, FikaTest2 spawned holding a Pack-a-Punched MDR). Behind the loading comic, so
                // the wait costs nothing.
                if (joining && heldWeapon && ZombiesNet.HostHandsHeard)
                {
                    // The host says when its copy of us has empty hands too (then a moment for the other players' games)
                    float from = Time.realtimeSinceStartup, most = from + 20f;
                    while (ZombiesNet.HostSeesMe() != "" && Time.realtimeSinceStartup < most)
                        yield return null;
                    bool seenEmpty = ZombiesNet.HostSeesMe() == "";
                    if (seenEmpty)
                        ZombiesPlugin.Log.LogInfo($"Zombies loadout: the host's copy of us has empty hands ({Time.realtimeSinceStartup - from:0.0} s)");
                    else
                        ZombiesPlugin.Log.LogWarning($"Zombies loadout: the host's copy of us still holds {ZombiesNet.HostSeesMe()} after 20 s: kit going on anyway");
                    yield return new WaitForSecondsRealtime(1.5f);
                }
                else if (ZombiesNet.Online && heldWeapon)
                {
                    // An older host that doesn't say: a fixed wait
                    ZombiesPlugin.Log.LogInfo($"Zombies loadout: co-op: {HeldWait} s for the other games to put the gun away too");
                    yield return new WaitForSecondsRealtime(HeldWait);
                }
            }
            int removed = 0;
            try
            {
                // The character's own slots (by name), so a slot this EFT version doesn't have is never asked for
                foreach (Slot slot in inventory.Inventory.Equipment.Slots)
                {
                    Item item = slot.ContainedItem;
                    // The dogtag can't come off (it belongs to the character)
                    if (item == null || slot.ID == nameof(EquipmentSlot.Dogtag))
                        continue;
                    if (Emptied.Contains(slot.ID))
                    {
                        if (item is CompoundItem container)
                            foreach (Item inside in container.Grids.SelectMany(g => g.Items).ToList())
                                removed += Remove(inside, inventory);
                        continue;
                    }
                    if (Kept.Any(k => k.ToString() == slot.ID))
                        continue;
                    removed += Remove(item, inventory);
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies loadout: taking the gear off failed: {e}");
            }
            yield return new WaitForSecondsRealtime(0.3f);
            yield return GiveBigPockets(inventory);
            ZombiesGrenades.FillTo(player, ZombiesGrenades.Start);

            ItemAddress ears = null;
            bool ownHeadset = inventory.Inventory.Equipment.GetSlot(EquipmentSlot.Earpiece)?.ContainedItem != null;
            // Headset sound "None": no ComTac handed out at all
            if (!ownHeadset && ZombiesSwipe.Headset?.Value == ZombiesSwipe.HeadsetSound.ComTac)
            try
            {
                Item comtac = Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), ComtacVi, null);
                ears = comtac != null ? inventory.FindSlotToPickUp(comtac) : null;
                if (ears != null && !ZombiesItems.Add(inventory, comtac, ears, out string error))
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies loadout: couldn't put the ComTac on: {error}");
                    ears = null;
                }
                // A helmet that blocks headsets: on anyway, in the ear slot past the rule (its model hidden: ZombiesGearFree)
                if (ears == null && comtac != null)
                {
                    Slot ear = inventory.Inventory.Equipment.GetSlot(EquipmentSlot.Earpiece);
                    if (ear != null && ear.ContainedItem == null && ear.AddWithoutRestrictions(comtac).Succeeded)
                    {
                        ears = ear.CreateItemAddress();
                        ZombiesNet.ItemAdded(comtac, ears);
                    }
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies loadout: the ComTac failed: {e}");
            }
            ZombiesPlugin.Log.LogInfo($"Zombies loadout: {removed} things taken off, " +
                (ownHeadset ? "own headset kept (ComTac VI sound)" : $"ComTac VI {(ears != null ? "on" : "not fitted")}"));

            // No knife on them (the user, 2026-11-07): a random melee weapon from the game in the scabbard. Its models
            // are loaded first: an unloaded one broke the hands at the first quick knife ("bundle is not loaded", no
            // hands after, and the player walked through walls in that state)
            Item spare = null;
            Slot scabbardSlot = null;
            try
            {
                scabbardSlot = inventory.Inventory.Equipment.GetSlot(EquipmentSlot.Scabbard);
                ItemFactory factory = Singleton<ItemFactory>.Instance;
                if (scabbardSlot != null && scabbardSlot.ContainedItem == null && factory != null)
                {
                    var knives = factory.ItemTemplates.Values.OfType<KnifeTemplate>()
                        .Where(t => !t.QuestItem && scabbardSlot.Filters != null && scabbardSlot.CheckCompatibility(factory.CreateItem(MongoID.Generate(), t._id, null)))
                        .ToList();
                    if (knives.Count > 0)
                        spare = factory.CreateItem(MongoID.Generate(), knives[UnityEngine.Random.Range(0, knives.Count)]._id, null);
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies loadout: the spare melee failed: {e.Message}");
            }
            if (spare != null)
            {
                yield return ZombiesItems.LoadModels(spare);
                try
                {
                    bool ok = ZombiesItems.AddToSlot(inventory, spare, scabbardSlot, out string error);
                    ZombiesPlugin.Log.LogInfo($"Zombies loadout: no knife, given a {spare.LocalizedName()} (models loaded){(ok ? "" : " (failed: " + error + ")")}");
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies loadout: the spare melee failed: {e.Message}");
                }
            }

            BoxGuns.Gun starter = Starter;
            if (starter == null)
            {
                ZombiesPlugin.Notify("No starting gun (M1911) in box_builds.json for the zombies loadout.", true);
                yield break;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies loadout: giving the {starter.Name}");
            Step(0.65f, $"Loading the {starter.Name}");
            yield return BoxGive.Give(starter, player);
            Step(0.85f, "Chambering a round");
            // The original's start: the slide racked (EFT's reload did it, the cock you hear), then 7 in the mag + 1 chambered = 8
            if (player.HandsController?.Item is Weapon started)
            {
                for (float until = Time.realtimeSinceStartup + 5f; Time.realtimeSinceStartup < until; )
                {
                    if (started.Chambers.Length > 0 && started.Chambers[0].ContainedItem != null)
                        break;
                    yield return null;
                }
                ZombiesAmmo.TopUpMag(started);
                // The original (Kino): 8 loaded, 32 in reserve to start; Max Ammo later fills the reserve to 80. A map's own
                // starting gun gets the same: four mags in reserve
                ZombiesAmmo.SetReserve(started, starter.Id == StarterGun ? StartReserve : ZombiesAmmo.StartReserveFor(started));
            }
            // The scores' picture, with the kit on (mask, glasses)
            yield return new WaitForSecondsRealtime(0.5f);
            PlayerPortrait.Capture(player);
            // Taken again once looking level (a head tilted down showed its top: the user wants it like the teammates')
            ZombiesPlugin.Instance.StartCoroutine(PlayerPortrait.RetakeWhenLevel(player));
            Step(1f, "Ready");
            Ready = true;
            ZombiesStats.SessionStarted();
            // The teammates' games get the whole kit (Fika)
            yield return new WaitForSecondsRealtime(1.5f);
            ZombiesNet.SendKit();
        }

        // The pockets swapped for big ones (empty already; the server puts the real ones back with the rest)
        private static IEnumerator GiveBigPockets(InventoryController inventory)
        {
            Slot pocketSlot = inventory.Inventory.Equipment.GetSlot(EquipmentSlot.Pockets);
            if (pocketSlot.ContainedItem?.TemplateId.ToString() == BigPockets)
                yield break;
            Item made = Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), BigPockets, null);
            if (made == null)
            {
                ZombiesPlugin.Log.LogError("Zombies loadout: no zombies pockets (is the zombies server mod installed?)");
                yield break;
            }
            yield return ZombiesItems.LoadModels(made);
            try
            {
                Slot slot = pocketSlot;
                Item pockets = made;
                if (slot.ContainedItem != null)
                {
                    ZombiesNet.ItemRemoved(slot.ContainedItem);
                    slot.RemoveItemWithoutRestrictions();
                }
                var added = slot.AddWithoutRestrictions(pockets);
                slot.ApplyContainedItem(); // the body's model of the slot follows
                ZombiesItems.Known(inventory, pockets);
                if (added.Succeeded)
                    ZombiesNet.ItemAdded(pockets, slot.CreateItemAddress());
                ZombiesPlugin.Log.LogInfo(added.Failed ? $"Zombies loadout: big pockets failed: {added.Error}" : "Zombies loadout: big pockets on");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies loadout: big pockets failed: {e.Message}");
            }
        }

        // One item off (a failure is logged and the rest carry on)
        private static int Remove(Item item, InventoryController inventory)
        {
            try
            {
                var result = ItemManipulator.Remove(item, inventory, false, true);
                if (!result.Succeeded)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies loadout: couldn't take off {item.LocalizedName()}: {result.Error}");
                    return 0;
                }
                ZombiesItems.Removed(inventory, result, item);
                return 1;
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies loadout: couldn't take off {item?.LocalizedName()}: {e.Message}");
                return 0;
            }
        }
    }
}
