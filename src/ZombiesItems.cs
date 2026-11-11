using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.Jobs;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Putting a newly made item on a player in a raid. EFT only sends existing items' moves over its network
    /// transactions (an add of a new item is refused: "operationResult is of unexpected type AddResult"), so the add is
    /// done directly and its events raised the way EFT's own reload code does, which updates the character and the UI.
    /// </summary>
    internal static class ZombiesItems
    {
        public static bool Add(InventoryController inventory, Item item, ItemAddress to, out string error)
        {
            var added = ItemManipulator.Add(item, to, inventory, false, true);
            if (!added.Succeeded)
            {
                error = added.Error?.ToString();
                return false;
            }
            added.Value.RaiseEvents(inventory, CommandStatus.Begin);
            added.Value.RaiseEvents(inventory, CommandStatus.Succeed);
            error = null;
            Known(inventory, item);
            Shared(inventory, item, to);
            return true;
        }

        /// <summary>
        /// A removal done (ItemManipulator.Remove has already taken the item out): its events raised so the character, the
        /// hands and the UI catch up, and the teammates' games told. EFT's network transaction can't be run on it after
        /// (it rebuilds the removal from the item's parent, which is gone: "Trying to get parent of an item that doesn't
        /// have a parent"), which threw and stopped whatever came next (the box's swap, the bottle taken away).
        /// </summary>
        public static void Removed(InventoryController inventory, OperationResult<RemoveResult> removed, Item item)
        {
            if (removed.Failed)
                return;
            try
            {
                removed.Value.RaiseEvents(inventory, CommandStatus.Begin);
                removed.Value.RaiseEvents(inventory, CommandStatus.Succeed);
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies items: removing {item?.LocalizedName()}: {e.Message}");
            }
            if (inventory == Singleton<GameWorld>.Instance?.MainPlayer?.InventoryController)
                ZombiesNet.ItemRemoved(item);
        }

        /// <summary>
        /// The item (and everything in it) known to its owner. EFT hides what's in a searchable grid (pockets, rig) until
        /// it's been searched: an item put there directly was "unknown", and EFT refused to touch it ("Cannot manipulate
        /// unknown or not found item"): no reload from those mags, no grenade throw, the bottle never drunk, the box's
        /// swap stuck. Returns how many were unknown.
        /// </summary>
        public static int Known(InventoryController inventory, Item item)
        {
            if (inventory == null || item == null || !(inventory.SearchController is IPlayerSearchController search))
                return 0;
            int fixedCount = 0;
            foreach (Item i in item.GetAllItems())
            {
                try
                {
                    // A container (pockets, rig, backpack) searched: what's in it can be used ("NonExistent" otherwise)
                    if (i is SearchableItem container && !inventory.SearchController.IsSearched(container))
                    {
                        search.SetItemAsSearched(container);
                        fixedCount++;
                    }
                    if (i.Parent == null || inventory.SearchController.IsItemKnown(i, i.Parent))
                        continue;
                    search.SetItemAsKnown(i, false);
                    fixedCount++;
                }
                catch
                {
                }
            }
            return fixedCount;
        }

        private static float _nextSweep;

        /// <summary>Once a second in a match: anything on the player that's still unknown made known (logged).</summary>
        public static void Sweep(Player player)
        {
            if (player == null || Time.time < _nextSweep)
                return;
            _nextSweep = Time.time + 1f;
            int n = Known(player.InventoryController, player.InventoryController?.Inventory?.Equipment);
            if (n > 0)
                ZombiesPlugin.Log.LogInfo($"Zombies items: {n} unknown items or unsearched containers on you made known (EFT would refuse to use them)");
        }

        // The teammates' games get the new item too (ZombiesNet; nothing without Fika)
        private static void Shared(InventoryController inventory, Item item, ItemAddress to)
        {
            if (inventory == Singleton<GameWorld>.Instance?.MainPlayer?.InventoryController)
                ZombiesNet.ItemAdded(item, to);
        }

        /// <summary>
        /// Into an equipment slot: EFT's own add, or past the slot's filter when it refuses (the zombies mode's gun slots
        /// take any gun: a pistol in a rifle slot).
        /// </summary>
        public static bool AddToSlot(InventoryController inventory, Item item, Slot slot, out string error)
        {
            if (Add(inventory, item, slot.CreateItemAddress(), out error))
                return true;
            var added = slot.AddWithoutRestrictions(item);
            slot.ApplyContainedItem(); // the body's model of the slot follows
            if (added.Failed)
            {
                error = added.Error?.ToString();
                return false;
            }
            error = null;
            Known(inventory, item);
            Shared(inventory, item, slot.CreateItemAddress());
            return true;
        }

        /// <summary>
        /// Room for a newly made item in the rig, pockets or backpack. EFT's own FindGridToPickUp asks the item for its
        /// parent, which throws for an item that isn't anywhere yet ("Trying to get parent of an item that doesn't have a
        /// parent"), so the grids are searched here.
        /// </summary>
        public static ItemAddress FindGrid(InventoryController inventory, Item item)
        {
            // The zombies pockets first (48 cells, every mag size; EFT's reload and quick throw take from them), then
            // whatever else is worn, the secure container last
            foreach (EquipmentSlot slot in new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack, EquipmentSlot.SecuredContainer })
            {
                if (!(inventory.Inventory.Equipment.GetSlot(slot).ContainedItem is CompoundItem container))
                    continue;
                foreach (var grid in container.Grids)
                {
                    ItemAddress place = grid.FindLocationForItem(item);
                    if (place != null)
                        return place;
                }
            }
            return null;
        }

        /// <summary>
        /// The 3D models of newly made items (and everything in them: parts, mags, rounds), loaded before they're used.
        /// EFT only has a model ready when something in the raid already uses it; a reload with rounds whose model isn't
        /// loaded fails ("patron_1143x23_acp.bundle is not loaded"): no mag swap, and the rounds never move.
        /// </summary>
        public static IEnumerator LoadModels(params Item[] items)
        {
            ResourceKey[] keys = items.Where(i => i != null).SelectMany(i => i.GetAllItems())
                // Every model the item has: its own and the one held in the hands (a bottle's drink, a med's use)
                .SelectMany(i => i.Template.AllResources).Where(k => k != null && !string.IsNullOrEmpty(k.path))
                .GroupBy(k => k.path).Select(g => g.First()).ToArray();
            if (keys.Length == 0 || !Singleton<ObjectsFactory>.Instantiated)
                yield break;
            // While the raid itself is still loading, EFT's own loads come first: this can take a while (it ran past
            // 15 s once and the starting M1911 came out with no model: empty hands for the whole match)
            float started = Time.realtimeSinceStartup;
            Task load = Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid,
                ObjectsFactory.AssemblyType.Local, keys, JobYieldPriority.Immediate);
            float until = started + 60f;
            while (!load.IsCompleted && Time.realtimeSinceStartup < until)
                yield return null;
            float took = Time.realtimeSinceStartup - started;
            if (load.IsFaulted)
                ZombiesPlugin.Log.LogWarning($"Loading item models failed: {load.Exception?.GetBaseException().Message}");
            else if (!load.IsCompleted)
                ZombiesPlugin.Log.LogWarning($"Loading item models: still not done after {took:0} s ({keys.Length} models)");
            else if (took > 3f)
                ZombiesPlugin.Log.LogInfo($"Loading item models: {keys.Length} took {took:0.0} s");
        }
    }
}
