using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using SPT.Reflection.Utils;

namespace KennerZombies
{
    /// <summary>
    /// Paying for the mystery box (950 roubles, wherever it is) and, in the hideout, collecting its gun: there the server
    /// mod takes the money from the stash and puts the gun in the stash through an item event (the Ray Gun's way: EFT
    /// won't hand items over in the hideout); in a raid the money comes off the roubles carried, here on the client.
    /// </summary>
    internal static class BoxPay
    {
        public const int Price = 950;
        private const string Roubles = "5449016a4bdc2d6f028b456f";
        private const string PayAction = "ZombiesBoxPay", TakeAction = "ZombiesBoxTake";

        /// <summary>Pays for a spin; <paramref name="done"/> gets whether it went through.</summary>
        public static void Pay(Player player, bool inHideout, Action<bool> done)
        {
            if (inHideout)
            {
                Send(new BoxOperation { Action = PayAction }, done);
                return;
            }
            done(PayFromPockets(player));
        }

        /// <summary>The hideout: the taken gun into the stash, built as the box showed it.</summary>
        public static void TakeToStash(BoxGuns.Gun gun, Action<bool> done)
        {
            Item item = BoxGuns.BuildItem(gun);
            if (item == null)
            {
                done(false);
                return;
            }
            Send(new BoxOperation { Action = TakeAction, build = BuildJson(item).ToString(Newtonsoft.Json.Formatting.None) }, done);
        }

        /// <summary>The roubles the player carries in the raid, less the price (stacks emptied first); false if short.</summary>
        private static bool PayFromPockets(Player player)
        {
            InventoryController inventory = player?.InventoryController;
            if (inventory == null)
                return false;
            List<Item> stacks = inventory.Inventory.Equipment.GetAllItems().Where(i => i.TemplateId.ToString() == Roubles).ToList();
            if (stacks.Sum(i => i.StackObjectsCount) < Price)
            {
                Warn($"Not enough money for the box ({Price} ₽ carried).");
                return false;
            }
            int left = Price;
            foreach (Item stack in stacks.OrderBy(i => i.StackObjectsCount))
            {
                if (left <= 0)
                    break;
                int take = Math.Min(left, stack.StackObjectsCount);
                left -= take;
                if (take == stack.StackObjectsCount)
                {
                    var removed = ItemManipulator.Remove(stack, inventory, false, true);
                    ZombiesItems.Removed(inventory, removed, stack);
                }
                else
                {
                    stack.StackObjectsCount -= take;
                    stack.RaiseRefreshEvent(false, true);
                }
            }
            ZombiesPlugin.Log.LogInfo($"Mystery box: paid {Price} RUB from the roubles carried");
            return true;
        }

        /// <summary>An item and its parts as the gun logger writes them: {tpl, parts: [{slot, tpl, parts}]}.</summary>
        private static JObject BuildJson(Item item)
        {
            var parts = new JArray();
            if (item is CompoundItem compound && compound.Slots != null)
                foreach (Slot slot in compound.Slots)
                    if (slot.ContainedItem != null && !(slot.ContainedItem is Ammo))
                    {
                        JObject part = BuildJson(slot.ContainedItem);
                        part["slot"] = slot.ID;
                        parts.Add(part);
                    }
            return new JObject { ["tpl"] = item.TemplateId.ToString(), ["parts"] = parts };
        }

        private static void Send(BoxOperation operation, Action<bool> done)
        {
            ClientBackendSession session = ClientAppUtils.GetMainApp()?.GetClientBackEndSession() as ClientBackendSession;
            if (session == null)
            {
                done(false);
                return;
            }
            session.SendOperationRightNow(operation, result =>
            {
                bool ok = result != null && !result.Failed;
                if (!ok)
                    Warn(string.IsNullOrEmpty(result?.Error) ? "The box didn't take the money." : result.Error);
                done(ok);
            });
        }

        private static void Warn(string message) =>
            NotificationManager.DisplayWarningNotification(message, ENotificationDurationType.Default);

        /// <summary>Item event body; field names match the server's BoxRequest.</summary>
        private class BoxOperation
        {
            public string Action;
            public string build;
        }
    }
}
