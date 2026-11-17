using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

namespace KennerZombiesServer;

/// <summary>
/// SPZombies' own map loader, server side: registers every installed zombies map as a location, so zombies maps need
/// no other mod. A map's server files sit in its own folder, next to its bundles (the client's MapLoader serves those):
///
///   BepInEx\plugins\SPZombies_Maps\ZM_&lt;Name&gt;\server\   or   BepInEx\plugins\Kennerz_ZM_&lt;Map&gt;\server\
///   (a server without the BepInEx folder: SPT_Runtime\user\mods\Kennerz_ZM_Core\maps\&lt;id&gt;\)
///       base.json         the location's settings (its "Id" is the map id)
///       sdk_export.json   spawn points, exits and the export time, written when the map is built
///       locale.json       optional: { "Name": "...", "Description": "...", "Keys": { "&lt;key&gt;": "&lt;text&gt;" } }
///
/// SPT 4.1's LocationTable.GetDictionary() is the live dictionary the client's location list reads, so adding an entry
/// makes the map selectable. No loot, containers, BTR, mortar, transits, map packs and raid events: zombies
/// maps use none of them (their loot is emptied by ZombiesLoot anyway). Runs just after the original's slot: a map the
/// original CustomMapLoader mod already registered is left to it.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 90010)]
public class ZombiesMapLoader(
    LocationTable locationTable,
    LocaleTable localeTable,
    LocationConfig locationConfig,
    BotConfig botConfig,
    PmcConfig pmcConfig,
    ICloner cloner,
    JsonUtil jsonUtil,
    ISptLogger<ZombiesMapLoader> logger) : IOnLoad
{
    // the bots' brain weights, raid times and bot caps are copied from this map for a new one
    private const string DonorLocation = "factory4_day";

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        WarnLeftoverLoaderFolder();
        int loaded = 0;
        foreach (string dir in ServerFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await LoadMap(dir, cancellationToken))
                    loaded++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.Error($"[Zombies] The map in {dir} was not loaded: {e.Message}");
            }
        }
        if (loaded == 0)
            logger.Warning("[Zombies] No zombies maps installed (BepInEx\\plugins\\SPZombies_Maps\\ZM_<Name>\\server)");
    }

    /// <summary>Every installed map's server folder.</summary>
    private static IEnumerable<string> ServerFolders()
    {
        string plugins = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "BepInEx", "plugins"));
        var folders = new List<string>();
        if (Directory.Exists(plugins))
        {
            string shared = Path.Combine(plugins, "SPZombies_Maps");
            if (Directory.Exists(shared))
                folders.AddRange(Directory.GetDirectories(shared));
            folders.AddRange(Directory.GetDirectories(plugins, "Kennerz_ZM_*")
                .Where(d => !string.Equals(Path.GetFileName(d), "Kennerz_ZM_Core", StringComparison.OrdinalIgnoreCase)));
        }
        foreach (string folder in folders)
        {
            string server = Path.Combine(folder, "server");
            if (File.Exists(Path.Combine(server, "base.json")))
                yield return server;
        }
        // a server-only install (no BepInEx folder): maps\<id>\ next to this DLL
        string own = Path.Combine(Path.GetDirectoryName(typeof(ZombiesMapLoader).Assembly.Location)!, "maps");
        if (Directory.Exists(own))
            foreach (string dir in Directory.GetDirectories(own))
                if (File.Exists(Path.Combine(dir, "base.json")))
                    yield return dir;
    }

    /// <summary>
    /// Stronghold 0.1.0 put its files in SPT_Runtime\user\mods\CustomMapLoader; without that mod the folder has no DLL and
    /// the SPT server reports "No Assemblies found in path". Say what it is and what to do.
    /// </summary>
    private void WarnLeftoverLoaderFolder()
    {
        try
        {
            string mods = Path.GetDirectoryName(Path.GetDirectoryName(typeof(ZombiesMapLoader).Assembly.Location)!)!;
            string old = Path.Combine(mods, "CustomMapLoader");
            if (Directory.Exists(old) && Directory.GetFiles(old, "*.dll").Length == 0)
                logger.Warning($"[Zombies] {old} is a leftover from an older zombies map (no mod in it): SPZombies loads maps " +
                               "itself now. Delete that folder (and BepInEx\\plugins\\CustomMapLoader if it holds only old zombies maps) " +
                               "to stop the server's \"No Assemblies found\" error.");
        }
        catch
        {
            // only a hint
        }
    }

    private async Task<bool> LoadMap(string dir, CancellationToken cancellationToken)
    {
        var locationBase = await jsonUtil.DeserializeFromFileAsync<LocationBase>(Path.Combine(dir, "base.json"), cancellationToken)
                           ?? throw new InvalidDataException("base.json could not be parsed");
        string mapId = locationBase.Id;
        if (string.IsNullOrEmpty(mapId))
            throw new InvalidDataException("base.json has no Id");

        var locations = locationTable.GetDictionary();
        if (locations.ContainsKey(mapId))
        {
            // the original CustomMapLoader mod has this map too and registered it first: it stays theirs
            logger.Info($"[Zombies] Map {mapId} is already registered (by the CustomMapLoader mod), left as it is");
            return false;
        }
        foreach (var (key, existing) in locations)
            if (existing?.Base != null && existing.Base.IdField == locationBase.IdField)
                throw new InvalidOperationException($"_Id {locationBase.IdField} is already used by '{key}'; give base.json a new unique 24-character hex _Id");

        // the bundle keys the client's map loader serves
        locationBase.Scene = new Scene
        {
            Path = $"maps/{mapId}.bundle",
            Rcid = $"{mapId}.scenespreset.asset",
        };
        // retail banner rows point at files nothing serves (the client then shows an empty error)
        locationBase.Banners = [];
        // ServerAirdropManager.Init reads AirdropParameters[0] unconditionally at raid start, and an explicit [] replaces
        // the client's one-entry default, so an empty list fails the raid load
        if (locationBase.AirdropParameters is not { Count: > 0 })
            locationBase.AirdropParameters = [DisabledAirdrops()];

        string exportPath = Path.Combine(dir, "sdk_export.json");
        if (File.Exists(exportPath))
        {
            var export = await jsonUtil.DeserializeFromFileAsync<MapExport>(exportPath, cancellationToken)
                         ?? throw new InvalidDataException("sdk_export.json could not be parsed");
            locationBase.SpawnPointParams = export.SpawnPointParams ?? [];
            locationBase.Exits = export.Exits ?? [];
            // the client only accepts backend spawn points when the scene is not newer than this
            locationBase.UnixDateTime = Math.Max(locationBase.UnixDateTime, export.UnixDateTime);
        }
        else
        {
            logger.Warning($"[Zombies] {mapId}: no sdk_export.json, so spawn points and exits come from base.json");
        }

        var donor = locationTable.GetLocation(DonorLocation);
        var location = new Location
        {
            Base = locationBase,
            StaticAmmo = donor?.StaticAmmo ?? new Dictionary<string, IEnumerable<StaticAmmoDetails>>(),
            // zombies maps have no loot: empty pools, no containers, no loose loot
            StaticLoot = new LazyLoad<Dictionary<MongoId, StaticLootDetails>>(() => new Dictionary<MongoId, StaticLootDetails>(), cacheValue: true),
            StaticContainers = new LazyLoad<StaticContainerDetails>(() => new StaticContainerDetails
            {
                StaticWeapons = [],
                StaticContainers = [],
                StaticForced = [],
            }, cacheValue: false),
            LooseLoot = new LazyLoad<LooseLoot>(() => new LooseLoot
            {
                SpawnpointCount = new SpawnpointCount { Mean = 0, Std = 0 },
                Spawnpoints = [],
                SpawnpointsForced = [],
            }, cacheValue: false),
            AllExtracts = [],
        };

        locations.Add(mapId, location);
        ConfigureLocation(mapId);
        await RegisterLocales(dir, locationBase, cancellationToken);
        logger.Success($"[Zombies] Map {mapId} loaded: {locationBase.SpawnPointParams?.Count() ?? 0} spawn point(s), " +
                       $"{locationBase.Exits?.Count() ?? 0} exit(s)");
        return true;
    }

    // the client's own AirdropParameters defaults, with no plane drops (chance and max of 0)
    private static AirdropParameter DisabledAirdrops() => new()
    {
        PlaneAirdropStartMin = 250,
        PlaneAirdropStartMax = 550,
        PlaneAirdropEnd = 2200,
        PlaneAirdropChance = 0,
        PlaneAirdropMax = 0,
        PlaneAirdropCooldownMin = 550,
        PlaneAirdropCooldownMax = 850,
        AirdropPointDeactivateDistance = 60,
        MinimumPlayersCountToSpawnAirdrop = 6,
        UnsuccessfulTryPenalty = 120,
    };

    // per-map config lookups that otherwise miss for an unknown location id
    private void ConfigureLocation(string mapId)
    {
        if (locationConfig.ScavRaidTimeSettings.Maps.TryGetValue(DonorLocation, out var raidTime))
            locationConfig.ScavRaidTimeSettings.Maps.TryAdd(mapId, cloner.Clone(raidTime)!);
        locationConfig.StaticLootMultiplier.TryAdd(mapId, 1);
        locationConfig.LooseLootMultiplier.TryAdd(mapId, 1);

        // the client reads these brain weights by map id when a bot's brain activates
        // (/singleplayer/settings/bot/getBotBehaviours/); a missing key throws inside BotOwner activation and every bot
        // is left standing in place (BotState.ActiveFail)
        CopyDonorEntry(botConfig.AssaultBrainType, mapId);
        CopyDonorEntry(botConfig.PlayerScavBrainType, mapId);
        foreach (var (_, byMap) in pmcConfig.PmcType)
            CopyDonorEntry(byMap, mapId);
        if (botConfig.MaxBotCap.TryGetValue(DonorLocation, out var botCap))
            botConfig.MaxBotCap.TryAdd(mapId, botCap);
    }

    private void CopyDonorEntry<T>(Dictionary<string, T> byMap, string mapId) where T : class
    {
        if (byMap.TryGetValue(DonorLocation, out var donor))
            byMap.TryAdd(mapId, cloner.Clone(donor)!);
    }

    // the map select screen reads "<_Id> Name" / "<_Id> Description" from locales, not base.json
    private async Task RegisterLocales(string dir, LocationBase locationBase, CancellationToken cancellationToken)
    {
        string path = Path.Combine(dir, "locale.json");
        var locale = File.Exists(path)
            ? await jsonUtil.DeserializeFromFileAsync<MapLocale>(path, cancellationToken)
            : null;

        string name = locale?.Name ?? locationBase.Name ?? locationBase.Id;
        string description = locale?.Description ?? locationBase.Description ?? "";
        string id = locationBase.IdField.ToString();

        foreach (var (_, lazy) in localeTable.Global)
        {
            lazy.AddTransformer(entries =>
            {
                if (entries == null)
                    return entries;
                entries[$"{id} Name"] = name;
                entries[$"{id} Description"] = description;
                entries[locationBase.Id] = name;
                if (locale?.Keys != null)
                    foreach (var (key, text) in locale.Keys)
                        entries[key] = text;
                return entries;
            });
        }
    }

    private sealed record MapExport
    {
        [JsonPropertyName("UnixDateTime")]
        public long UnixDateTime { get; init; }

        [JsonPropertyName("SpawnPointParams")]
        public List<SpawnPointParam>? SpawnPointParams { get; init; }

        [JsonPropertyName("Exits")]
        public List<Exit>? Exits { get; init; }
    }

    private sealed record MapLocale
    {
        [JsonPropertyName("Name")]
        public string? Name { get; init; }

        [JsonPropertyName("Description")]
        public string? Description { get; init; }

        [JsonPropertyName("Keys")]
        public Dictionary<string, string>? Keys { get; init; }
    }
}
