using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;

namespace KennerZombiesServer;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.kennerz.zmcore"; // same GUID as the client plugin (Forge rule)
    public string Name { get; init; } = "Kennerz-ZM_Core";
    public string Author { get; init; } = "Kenner";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.2");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    // Pack-a-Punch and the perks are part of zombies (required); the wonder weapons are optional
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        { "com.kennerz.paphideoutmodule", new SemanticVersioning.Range(">=2.2.0") },
        { "com.kennerz.perkbeverages", new SemanticVersioning.Range(">=0.1.0") },
        { "com.kennerz.raygun", new SemanticVersioning.Range(">=1.2.0") },
        { "com.kennerz.thundergun", new SemanticVersioning.Range(">=1.2.0") },
    };
    public string? Url { get; init; } = "https://github.com/Kennerz77/KennerZombiesSPT";
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Body of the client's mystery box item events.</summary>
public record BoxRequest : BaseInteractionRequestData
{
    /// <summary>The gun taken, as the client logged it: {"tpl": ..., "parts": [{"slot", "tpl", "parts"}, ...]}.</summary>
    [JsonPropertyName("build")]
    public string? Build { get; set; }
}

/// <summary>
/// The mystery box in the hideout (to try it): a spin costs 950 roubles from the stash, and the gun taken goes into the
/// stash, built as logged with a full magazine of its standard round. Both answer through the reply, so the client
/// sees the money go and the gun arrive at once. (In raids the client pays and hands the gun over itself.)
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BoxShop(ISptLogger<BoxShop> logger, PaymentService paymentService, InventoryHelper inventoryHelper,
    ItemHelper itemHelper, HttpResponseUtil httpResponseUtil)
{
    public const string PayAction = "ZombiesBoxPay";
    public const string TakeAction = "ZombiesBoxTake";
    public const int BoxPrice = 950;
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    public ItemEventRouterResponse Pay(PmcData pmcData, BoxRequest body, MongoId sessionId, ItemEventRouterResponse output)
    {
        double roubles = pmcData.Inventory?.Items?.Where(i => i.Template.ToString() == Roubles).Sum(i => i.Upd?.StackObjectsCount ?? 1) ?? 0;
        if (roubles < BoxPrice)
            return httpResponseUtil.AppendErrorToOutput(output, "Not enough money for the box (950 ₽).");
#pragma warning disable CS0618
        // The 5-argument overload exists on every SPT 4.1.x (see the Ray Gun's shop)
        paymentService.AddPaymentToOutput(pmcData, Money.ROUBLES, BoxPrice, sessionId, output);
#pragma warning restore CS0618
        if (output.Warnings?.Count > 0)
            return output;
        logger.Info($"[Zombies] {pmcData.Info?.Nickname} paid {BoxPrice} RUB for the mystery box");
        return output;
    }

    public ItemEventRouterResponse Take(PmcData pmcData, BoxRequest body, MongoId sessionId, ItemEventRouterResponse output)
    {
        List<Item> items;
        try
        {
            items = Build(body.Build);
        }
        catch (Exception e)
        {
            logger.Error($"[Zombies] Bad gun from the box: {e.Message}");
            return httpResponseUtil.AppendErrorToOutput(output, "The box's gun couldn't be made.");
        }
        if (items.Count == 0)
            return httpResponseUtil.AppendErrorToOutput(output, "The box's gun couldn't be made.");
        if (!inventoryHelper.CanPlaceItemsInInventory(sessionId, [items]))
            return httpResponseUtil.AppendErrorToOutput(output, "No room in your stash for the gun.");
        inventoryHelper.AddItemsToStash(sessionId, new AddItemsDirectRequest { ItemsWithModsToAdd = [items], FoundInRaid = false }, pmcData, output);
        logger.Info($"[Zombies] {pmcData.Info?.Nickname} took a {items[0].Template} from the mystery box ({items.Count} items)");
        return output;
    }

    /// <summary>The gun's items: the weapon, every part in its slot (nested), its magazine filled with its default round.</summary>
    private List<Item> Build(string? json)
    {
        var items = new List<Item>();
        if (string.IsNullOrEmpty(json))
            return items;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string weaponTpl = root.GetProperty("tpl").GetString()!;
        KeyValuePair<bool, TemplateItem?> weapon = itemHelper.GetItem(new MongoId(weaponTpl));
        if (!weapon.Key)
            throw new InvalidOperationException($"unknown weapon {weaponTpl}");
        var gun = new Item { Id = new MongoId(), Template = new MongoId(weaponTpl), Upd = new Upd { StackObjectsCount = 1 } };
        items.Add(gun);
        string? ammo = weapon.Value?.Properties?.DefAmmo;
        if (root.TryGetProperty("parts", out JsonElement parts))
            AddParts(items, gun, parts, ammo);
        return items;
    }

    private void AddParts(List<Item> items, Item parent, JsonElement parts, string? ammo)
    {
        foreach (JsonElement p in parts.EnumerateArray())
        {
            string tpl = p.GetProperty("tpl").GetString()!;
            KeyValuePair<bool, TemplateItem?> template = itemHelper.GetItem(new MongoId(tpl));
            if (!template.Key)
            {
                logger.Warning($"[Zombies] Unknown part {tpl} skipped");
                continue;
            }
            var part = new Item { Id = new MongoId(), Template = new MongoId(tpl), ParentId = parent.Id.ToString(), SlotId = p.GetProperty("slot").GetString() };
            items.Add(part);
            // A magazine comes full of the gun's standard round
            int capacity = template.Value?.Properties?.Cartridges?.FirstOrDefault()?.MaxCount is double max ? (int)max : 0;
            if (part.SlotId == "mod_magazine" && capacity > 0 && !string.IsNullOrEmpty(ammo))
                items.Add(new Item
                {
                    Id = new MongoId(), Template = new MongoId(ammo), ParentId = part.Id.ToString(), SlotId = "cartridges",
                    Location = 0, Upd = new Upd { StackObjectsCount = capacity },
                });
            if (p.TryGetProperty("parts", out JsonElement children))
                AddParts(items, part, children, ammo);
        }
    }
}

/// <summary>Routes the client's mystery box item events.</summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class BoxShopRouter(BoxShop shop) : ItemEventRouter([
    new ItemRouteAction<BoxRequest>(BoxShop.PayAction,
        (url, pmcData, body, sessionID, output, cancellationToken) => ValueTask.FromResult(shop.Pay(pmcData, body, sessionID, output))),
    new ItemRouteAction<BoxRequest>(BoxShop.TakeAction,
        (url, pmcData, body, sessionID, output, cancellationToken) => ValueTask.FromResult(shop.Take(pmcData, body, sessionID, output))),
]) { }
