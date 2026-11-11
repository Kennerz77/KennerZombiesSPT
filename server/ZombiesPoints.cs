using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace KennerZombiesServer;

/// <summary>
/// Dr. Kennerz points: what a zombies match is worth, kept per profile on the server for the rewards to come (the
/// user, 2026-11-08). The client works out the match's XP (its tally screen) and its points, and sends them once per
/// match; the server adds them to the profile's total and keeps the last matches. Saved as kennerz_points.json beside
/// this mod's DLL. POST /kzm/points {op: get | add, points, xp, map, rounds, match}: the reply is the profile's total.
/// A match id seen before is never added twice (a resend after a hiccup).
/// </summary>
[Injectable(InjectionType.Singleton)]
public class KennerzPointsService
{
    private readonly object _lock = new();
    private Dictionary<string, Account>? _accounts;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public class Match
    {
        [JsonPropertyName("match")] public string Id { get; set; } = "";
        [JsonPropertyName("when")] public string When { get; set; } = "";
        [JsonPropertyName("map")] public string Map { get; set; } = "";
        [JsonPropertyName("rounds")] public int Rounds { get; set; }
        [JsonPropertyName("xp")] public int Xp { get; set; }
        [JsonPropertyName("points")] public int Points { get; set; }
    }

    public class Account
    {
        [JsonPropertyName("total")] public long Total { get; set; }
        [JsonPropertyName("matches")] public int Matches { get; set; }
        [JsonPropertyName("last")] public List<Match> Last { get; set; } = new();
    }

    public class Reply
    {
        [JsonPropertyName("total")] public long Total { get; set; }
        [JsonPropertyName("added")] public int Added { get; set; }
        [JsonPropertyName("matches")] public int Matches { get; set; }
    }

    private static string FilePath => Path.Combine(Path.GetDirectoryName(typeof(KennerzPointsService).Assembly.Location) ?? ".", "kennerz_points.json");

    private Dictionary<string, Account> Accounts()
    {
        if (_accounts != null)
            return _accounts;
        try
        {
            _accounts = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Account>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            // A broken file is kept aside, never thrown away
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".broken_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true);
            _accounts = new();
        }
        return _accounts;
    }

    public Reply Handle(string profileId, PointsRequest req)
    {
        lock (_lock)
        {
            var all = Accounts();
            if (!all.TryGetValue(profileId, out Account? acc))
                all[profileId] = acc = new Account();
            int added = 0;
            if (req.Op == "add" && req.Points > 0 && !string.IsNullOrEmpty(req.Match) && acc.Last.All(m => m.Id != req.Match))
            {
                added = req.Points;
                acc.Total += added;
                acc.Matches++;
                acc.Last.Add(new Match
                {
                    Id = req.Match!, When = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Map = req.Map ?? "",
                    Rounds = req.Rounds, Xp = req.Xp, Points = added,
                });
                if (acc.Last.Count > 30)
                    acc.Last.RemoveAt(0);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Pretty));
            }
            return new Reply { Total = acc.Total, Added = added, Matches = acc.Matches };
        }
    }
}

public record PointsRequest : IRequestData
{
    [JsonPropertyName("op")] public string Op { get; set; } = "get";
    [JsonPropertyName("points")] public int Points { get; set; }
    [JsonPropertyName("xp")] public int Xp { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("rounds")] public int Rounds { get; set; }
    [JsonPropertyName("match")] public string? Match { get; set; }
}

[Injectable(TypePriority = OnLoadOrder.Routers)]
public class KennerzPointsRouter(JsonUtil jsonUtil, KennerzPointsService points, HttpResponseUtil httpResponseUtil) : SPTarkov.Server.Core.DI.StaticRouter(jsonUtil, [
    new SPTarkov.Server.Core.DI.RouteAction<PointsRequest>(
        "/kzm/points",
        (url, info, sessionId, output, cancellationToken) =>
            ValueTask.FromResult(httpResponseUtil.NoBody(points.Handle(sessionId.ToString(), info)))
    ),
]) { }
