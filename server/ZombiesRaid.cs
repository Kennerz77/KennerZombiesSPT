using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.InRaid;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace KennerZombiesServer;

/// <summary>
/// A zombies match never changes the real character. The client strips the PMC to the zombies loadout in the raid
/// (that never reaches the server until the raid ends), so the profile on the server is untouched until then. When a
/// zombies raid ends, the PMC (gear, health, skills, stats, quests), its insurance claims and BTR deliveries are copied,
/// SPT's normal end of raid runs (survived, dead or left), and the copy is put back and saved. So nothing is lost,
/// gained or claimed from a zombies match. A crash mid-match never reaches this point, and the profile was never changed.
/// Each player's raid end comes here for their own profile, so a Fika team is covered player by player.
/// </summary>
internal static class ZombiesRaid
{
    internal static ProfileHelper? Profiles;
    internal static ICloner? Cloner;
    internal static SaveServer? Saves;
    internal static ISptLogger<ZombiesRaidHook>? Log;

    [ThreadStatic] private static bool _passThrough;

    /// <summary>
    /// The zombies maps: every layout file (&lt;location&gt;zombies.json) in BepInEx\plugins\Kennerz_ZM_*\maps marked
    /// "ZombiesMap": true, the same files the client reads. Map ids starting "zm_" count too (a server without the
    /// client folders).
    /// </summary>
    internal static bool IsZombiesMap(string? location)
    {
        if (string.IsNullOrEmpty(location))
            return false;
        if (location.StartsWith("zm_", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            string plugins = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "BepInEx", "plugins"));
            if (!Directory.Exists(plugins))
                return false;
            foreach (string mod in Directory.GetDirectories(plugins, "Kennerz_ZM_*"))
            {
                string file = Path.Combine(mod, "maps", location + "zombies.json");
                if (!File.Exists(file))
                    continue;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("ZombiesMap", out JsonElement z) && z.ValueKind == JsonValueKind.True)
                    return true;
            }
        }
        catch (Exception e)
        {
            Log?.Error($"[Zombies] Couldn't read the zombies maps: {e.Message}");
        }
        return false;
    }

    internal sealed class EndRaidPatch : AbstractPatch
    {
        protected override MethodBase? GetTargetMethod() =>
            typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.EndLocalRaidAsync));

        [PatchPrefix]
        private static bool Prefix(LocationLifecycleService __instance, MongoId sessionId, EndLocalRaidRequestData request,
            CancellationToken cancellationToken, ref Task __result)
        {
            if (_passThrough)
                return true;
            string? location = request?.ServerId?.Split('.')[0];
            if (!IsZombiesMap(location) || Profiles == null || Cloner == null || Saves == null)
                return true;
            __result = EndAndRestore(__instance, sessionId, request!, cancellationToken, location!);
            return false;
        }
    }

    private static async Task EndAndRestore(LocationLifecycleService service, MongoId sessionId, EndLocalRaidRequestData request,
        CancellationToken cancellationToken, string location)
    {
        SptProfile profile = Profiles!.GetFullProfile(sessionId);
        var pmc = Cloner!.Clone(profile.CharacterData?.PmcData);
        var insurance = Cloner.Clone(profile.InsuranceList);
        var btr = Cloner.Clone(profile.BtrDeliveryList);
        Task end;
        // SPT's own end of raid (this patch lets the call through)
        _passThrough = true;
        try
        {
            end = service.EndLocalRaidAsync(sessionId, request, cancellationToken);
        }
        finally
        {
            _passThrough = false;
        }
        try
        {
            await end;
        }
        finally
        {
            if (pmc != null && profile.CharacterData != null)
                profile.CharacterData.PmcData = pmc;
            profile.InsuranceList = insurance;
            profile.BtrDeliveryList = btr;
            await Saves!.SaveProfileAsync(sessionId, cancellationToken);
            Log?.Info($"[Zombies] {pmc?.Info?.Nickname}: zombies match on {location} over, character put back as it was before");
        }
    }
}

/// <summary>Hands the patch what it needs and turns it on.</summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 2)]
public class ZombiesRaidHook(ISptLogger<ZombiesRaidHook> logger, ProfileHelper profileHelper, ICloner cloner, SaveServer saveServer) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        ZombiesRaid.Profiles = profileHelper;
        ZombiesRaid.Cloner = cloner;
        ZombiesRaid.Saves = saveServer;
        ZombiesRaid.Log = logger;
        try
        {
            new ZombiesRaid.EndRaidPatch().Enable();
        }
        catch (Exception e)
        {
            logger.Error($"[Zombies] End of raid hook failed: zombies matches would keep their results! {e.GetBaseException().Message}");
        }
        try
        {
            new ZombiesNoLootPatch().Enable();
        }
        catch (Exception e)
        {
            logger.Error($"[Zombies] Zombies map loot hook failed: other mods' loot generators may run on zombies maps. {e.GetBaseException().Message}");
        }
        return Task.CompletedTask;
    }
}
