using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace KennerZombiesServer;

/// <summary>The zombies mode's own items' ids (one prefix, easy to spot).</summary>
internal static class ZombiesItemIds
{
    /// <summary>the original's Cymbal Monkey: the tactical grenade (only from the mystery box, in a zombies match).</summary>
    public const string CymbalMonkey = "6a8f0e5b0c0a116000000c01";
    public const string M67 = "58d3db5386f77426186285a0";
    public const string HandbookThrowables = "5b5f7a2386f774093f2ed3c4";

    /// <summary>
    /// The zombies pockets: EFT's 2x3 pockets with room for everything a match hands out (spare mags 1x1 to 2x2, the
    /// launcher's 40 mm rounds, grenades, monkeys, perk bottles), so nobody needs a rig or a backpack. Six 2x3 cells
    /// and four 1x3, 48 in all. Put on at a match's start only (the player never opens the inventory in a match).
    /// </summary>
    public const string Pockets = "6a8f0e5b0c0a116000000d01";
    public const string PocketsBase = "64cbd95a29b9b4283e216ff5";
}

/// <summary>
/// Adds the Cymbal Monkey: a clone of the M67 (so EFT throws it like a grenade); the client gives it its the original look,
/// sounds, lure and fuse in a zombies match. Kept out of loot, bot gear, rewards, traders and the flea: the mystery
/// box is its only source.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class ZombiesItemLoader(
    ISptLogger<ZombiesItemLoader> logger,
    CustomItemService customItemService,
    TemplateTable templateTable,
    ItemConfig itemConfig,
    RagfairConfig ragfairConfig,
    ItemFilterService itemFilterService) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        MongoId id = ZombiesItemIds.CymbalMonkey;
        string parent = templateTable.Items.TryGetValue(ZombiesItemIds.M67, out TemplateItem? m67) ? m67.Parent : "543be6564bdc2df4348b4568";
        CreateItemResult result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = ZombiesItemIds.M67,
            ParentId = parent,
            NewId = id,
            NewItemName = "zm_cymbal_monkey",
            AddToHandbook = true,
            HandbookParentId = ZombiesItemIds.HandbookThrowables,
            HandbookPriceRoubles = 1,
            AddToFleaPriceDb = false,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = "Cymbal Monkey", ShortName = "Monkey", Description = "Wind it up and throw it. They can't resist the music." },
            },
        });
        if (result.Success != true)
        {
            logger.Error($"[Kennerz-ZM_Core] Could not add the Cymbal Monkey: {string.Join("; ", result.Errors ?? [])}");
            return Task.CompletedTask;
        }
        itemConfig.Blacklist.Add(id);
        itemConfig.LootableItemBlacklist.Add(id);
        itemConfig.RewardItemBlacklist.Add(id);
        ragfairConfig.Dynamic.Blacklist.Custom.Add(id);
        itemFilterService.AddItemToBlacklistCache([id]);
        itemFilterService.AddItemToLootableBlacklistCache([id]);
        logger.Success("[Kennerz-ZM_Core] Cymbal Monkey added (mystery box only)");
        AddPockets();
        return Task.CompletedTask;
    }

    private void AddPockets()
    {
        MongoId id = ZombiesItemIds.Pockets;
        if (!templateTable.Items.TryGetValue(ZombiesItemIds.PocketsBase, out TemplateItem? basePockets))
        {
            logger.Error("[Kennerz-ZM_Core] No 2x3 pockets to base the zombies pockets on");
            return;
        }
        CreateItemResult result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = ZombiesItemIds.PocketsBase,
            ParentId = basePockets.Parent,
            NewId = id,
            NewItemName = "zm_pockets",
            AddToHandbook = false,
            AddToFleaPriceDb = false,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = "Pockets", ShortName = "Pockets", Description = "Pockets" },
            },
        });
        if (result.Success != true || !templateTable.Items.TryGetValue(id, out TemplateItem? pockets) || pockets.Properties?.Grids == null)
        {
            logger.Error($"[Kennerz-ZM_Core] Could not add the zombies pockets: {string.Join("; ", result.Errors ?? [])}");
            return;
        }
        // Each grid a copy of the base's 2x3 pocket (its filters: anything but containers), resized
        Grid wide = basePockets.Properties!.Grids!.First(g => g.Properties?.CellsH == 2);
        var grids = new List<Grid>();
        int n = 0;
        foreach ((int h, int v) in new[] { (2, 3), (2, 3), (2, 3), (2, 3), (2, 3), (2, 3), (1, 3), (1, 3), (1, 3), (1, 3) })
        {
            n++;
            grids.Add(new Grid
            {
                Id = new MongoId(),
                Name = "pocket" + n,
                Parent = id,
                Prototype = wide.Prototype,
                Properties = new GridProperties
                {
                    CellsH = h,
                    CellsV = v,
                    Filters = wide.Properties!.Filters,
                    IsSortingTable = false,
                    MaxCount = 0,
                    MaxWeight = 0,
                    MinCount = 0,
                },
            });
        }
        pockets.Properties.Grids = grids;
        itemConfig.Blacklist.Add(id);
        itemConfig.LootableItemBlacklist.Add(id);
        itemConfig.RewardItemBlacklist.Add(id);
        ragfairConfig.Dynamic.Blacklist.Custom.Add(id);
        itemFilterService.AddItemToBlacklistCache([id]);
        itemFilterService.AddItemToLootableBlacklistCache([id]);
        logger.Success("[Kennerz-ZM_Core] Zombies pockets added (48 cells, zombies matches only)");
    }
}
