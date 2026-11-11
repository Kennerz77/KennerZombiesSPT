using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;

namespace KennerZombiesServer;

/// <summary>
/// The zombies party: players get together on the zombies map screen before a match (Fika co-op). One player leads
/// and picks the map; the others join the party, everyone readies up, and when all are ready the leader's game hosts
/// the Fika raid and the others' games join it by themselves. Kept in memory only (a party is gone with the server).
/// POST /kzm/party {op, name, map, ready, partyId, mods, maps, target, private}: every call is also a heartbeat (the
/// caller's zombies mods and maps, for the invite checks); the reply is the open parties, the caller's own, the
/// server's players (each with what they'd be missing to play) and an invite waiting for the caller.
/// Ops: list, create, join, leave, ready, map, start (leader: all ready, hosting now), hosted (leader: the Fika raid is
/// up, join now), done (leader: the raid's started, the party closes), private (leader: invite only or public),
/// invite (target: a party's made for the caller if it has none), decline (the caller's invite), disband (the host
/// leaves: the lobby closes for everyone; a host's "leave" does the same), getstats (target: their statistics).
/// Any call may carry "history" (the caller's own match list): kept as their statistics for others to open.
/// Someone invited who doesn't have the zombies mode (never called here) gets an SPT chat message instead, naming the
/// mods to get.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ZombiesPartyService(SaveServer saveServer, MailSendService mailSendService, ISptLogger<ZombiesPartyService> logger,
    IServiceProvider services)
{
    private const double MemberTimeout = 12, LeaderTimeout = 12, StaleAfter = 600, OnlineFor = 30, InviteFor = 90;
    // Spam stoppers: one invite every few seconds per player, and a wait before inviting someone again who said no
    // After a no: 5 s before that sender can invite them again, 5 s more for each further no, at most 20 s; the count
    // starts over 10 minutes after the last no (user, 2026-11-09)
    private const double RequestFor = 60;
    private const double InviteGap = 3, NoStep = 5, NoMax = 20, NoForget = 600, InMatchInviteGap = 30;
    public const int MaxPlayers = 4;

    /// <summary>What a zombies player needs, by plugin GUID: the name players know it by.</summary>
    private static readonly Dictionary<string, string> Needed = new()
    {
        ["com.kennerz.zmcore"] = "Dr K's SPZombies",
        ["com.kennerz.paphideoutmodule"] = "Pack-a-Punch Hideout Module",
        ["com.kennerz.perkbeverages"] = "Dr K's Perk Beverages",
        ["com.kennerz.raygun"] = "Ray Gun",
        ["com.kennerz.thundergun"] = "Thundergun",
        ["com.mizmii.custommaploader"] = "CustomMapLoader",
        ["com.fika.core"] = "Fika",
    };

    // The core must match exactly (the match is played on both games); the others only need to be there
    private const string Core = "com.kennerz.zmcore";

    private readonly object _lock = new();
    private readonly Dictionary<string, Party> _parties = new();
    private readonly Dictionary<string, Presence> _seen = new();
    private readonly Dictionary<string, Invite> _invites = new();
    // What happened to an invite (declined, no answer), for the player who sent it: shown once as a pop-up
    private readonly Dictionary<string, List<string>> _outcomes = new();
    // (inviter, target) -> no invites before this time (they said no)
    private readonly Dictionary<(string, string), DateTime> _noUntil = new();
    private readonly Dictionary<(string, string), (int Count, DateTime Last)> _noCount = new();

    public class Member
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("ready")] public bool Ready { get; set; }
        [JsonIgnore] public DateTime Seen { get; set; }
    }

    public class Party
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";       // the leader's profile id (= Fika's match id)
        [JsonPropertyName("map")] public string Map { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "open"; // open, starting, hosted
        // The Fika match the members join: a headless's profile id when a Fika headless hosts, else empty (= Id)
        [JsonPropertyName("fikaMatch")] public string FikaMatch { get; set; } = "";
        [JsonPropertyName("private")] public bool Private { get; set; }
        // The host's Fika mode: true = a free Fika headless hosts the match (when one is free), false = the host's game
        [JsonPropertyName("headless")] public bool Headless { get; set; }
        [JsonPropertyName("members")] public List<Member> Members { get; set; } = new();
        [JsonIgnore] public HashSet<string> Invited { get; } = new();
        // Players asking to join this (private) lobby: id -> when asked (the host answers ACCEPT / DECLINE)
        [JsonIgnore] public Dictionary<string, DateTime> Requests { get; } = new();
        // Times each player was kicked from this party: from the 3rd on they're back only with the HOST's own invite
        [JsonIgnore] public Dictionary<string, int> Kicked { get; } = new();
        // START MATCH: a 5 s countdown everyone sees, then the leader's game hosts (user, 2026-11-09). -1 = none
        [JsonPropertyName("countdown")] public double Countdown { get; set; } = -1;
        [JsonIgnore] public DateTime? CountdownEnd { get; set; }   // removed by the leader: back only by a new invite
        [JsonIgnore] public DateTime Made { get; set; }
    }

    private class Presence
    {
        public string Name = "";
        public DateTime Seen;
        public Dictionary<string, string> Mods = new();
        public List<string> Maps = new();
        public bool Zombies;          // the main menu's mode switch on ZOMBIES (or in a zombies match)
        public string Match = "";     // the zombies map of the match they're in, or empty
        public int Count;             // players in that match
        public int Round;             // the round their match is on
        public bool Raid;             // in a normal raid (their zombies game says so)
        public string RaidMap = "";   // that normal raid's map
        public DateTime LastInvited;  // the last invite sent to them (one every 30 s while they're in a match)
        public bool NoInvites;        // invite pop-ups turned off in their F12
        public DateTime LastInvite;   // the last invite they sent (spam stopper)
    }

    public class Invite
    {
        [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N"); // each invite its own (a no hides only that one)
        [JsonPropertyName("partyId")] public string PartyId { get; set; } = "";
        [JsonPropertyName("from")] public string From { get; set; } = "";
        [JsonPropertyName("map")] public string Map { get; set; } = "";
        [JsonPropertyName("missing")] public List<string> Missing { get; set; } = new();
        [JsonIgnore] public DateTime Made { get; set; }
        [JsonIgnore] public string FromId { get; set; } = "";
    }

    public class PlayerInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("online")] public bool Online { get; set; }      // has the zombies mode and called in the last 30 s
        [JsonPropertyName("zombies")] public bool Zombies { get; set; }    // has ever called here (has the zombies mode)
        [JsonPropertyName("missing")] public List<string> Missing { get; set; } = new();
        [JsonPropertyName("inParty")] public bool InParty { get; set; }
        [JsonPropertyName("invited")] public bool Invited { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = "offline"; // offline, normal, zombies, match
        [JsonPropertyName("match")] public string Match { get; set; } = "";
        [JsonPropertyName("inRaid")] public bool InRaid { get; set; }      // normal mode, in a raid right now
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("round")] public int Round { get; set; }
        [JsonPropertyName("max")] public int Max { get; set; } = MaxPlayers;
        [JsonPropertyName("joinParty")] public string? JoinParty { get; set; } // their open party the caller may join
        [JsonPropertyName("hasStats")] public bool HasStats { get; set; }      // their statistics are on this server
        [JsonPropertyName("partySize")] public int PartySize { get; set; }     // the open lobby they're in (players), else 0
        [JsonPropertyName("raidMap")] public string RaidMap { get; set; } = ""; // normal mode, in a raid: its map
        [JsonPropertyName("requestParty")] public string? RequestParty { get; set; } // their private lobby the caller may ask to join
        [JsonPropertyName("requested")] public bool Requested { get; set; }          // the caller's ask is waiting there
    }

    public class JoinRequest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    /// <summary>A player's lifetime zombies statistics (their game's own match list, as it sent it).</summary>
    public class StatsReply
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("matches")] public JsonElement? Matches { get; set; }
    }

    public class Reply
    {
        [JsonPropertyName("parties")] public List<Party> Parties { get; set; } = new();
        [JsonPropertyName("joinable")] public List<string> Joinable { get; set; } = new();
        [JsonPropertyName("mine")] public Party? Mine { get; set; }
        [JsonPropertyName("players")] public List<PlayerInfo> Players { get; set; } = new();
        [JsonPropertyName("invite")] public Invite? Invite { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("notice")] public string? Notice { get; set; }
        [JsonPropertyName("toasts")] public List<string> Toasts { get; set; } = new();
        [JsonPropertyName("stats")] public StatsReply? Stats { get; set; }
        // Fika headless clients connected and READY right now (0: the host's own game hosts)
        [JsonPropertyName("headlessFree")] public int HeadlessFree { get; set; }
        [JsonPropertyName("headlessKnown")] public int HeadlessKnown { get; set; }
        // The host: players asking to join their lobby
        [JsonPropertyName("requests")] public List<JoinRequest> Requests { get; set; } = new();
    }

    // ---------- Everyone's statistics (user, 2026-11-10: click a player, see their zombies statistics) ----------
    // Each game sends its match list whenever it has a new match; kept by profile id in zombies_stats.json beside this
    // mod, so an offline player's page still opens.

    private Dictionary<string, string>? _stats;

    private static string StatsFile => Path.Combine(Path.GetDirectoryName(typeof(ZombiesPartyService).Assembly.Location) ?? ".", "zombies_stats.json");

    private Dictionary<string, string> Stats()
    {
        if (_stats != null)
            return _stats;
        try
        {
            _stats = File.Exists(StatsFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatsFile)) : null;
        }
        catch (Exception e)
        {
            logger.Warning($"[Zombies] Statistics file couldn't be read: {e.Message}");
        }
        return _stats ??= new Dictionary<string, string>();
    }

    private void KeepStats(string id, JsonElement matches)
    {
        if (matches.ValueKind != JsonValueKind.Array)
            return;
        Stats()[id] = matches.GetRawText();
        try
        {
            File.WriteAllText(StatsFile, JsonSerializer.Serialize(_stats));
        }
        catch (Exception e)
        {
            logger.Warning($"[Zombies] Statistics file couldn't be saved: {e.Message}");
        }
    }

    private StatsReply StatsOf(string id)
    {
        var reply = new StatsReply { Id = id, Name = NameOf(id) };
        if (Stats().TryGetValue(id, out string? raw))
        {
            try
            {
                reply.Matches = JsonDocument.Parse(raw).RootElement.Clone();
            }
            catch
            {
            }
        }
        return reply;
    }

    public Reply Handle(string sessionId, PartyRequest req)
    {
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            Prune(now);
            string? error = null, notice = null;
            if (!_seen.TryGetValue(sessionId, out Presence? me))
                _seen[sessionId] = me = new Presence();
            me.Seen = now;
            if (!string.IsNullOrEmpty(req.Name))
                me.Name = req.Name;
            if (req.Mods != null)
                me.Mods = req.Mods;
            if (req.Maps != null)
                me.Maps = req.Maps;
            me.Zombies = req.Zombies;
            me.Match = req.Match ?? "";
            me.Count = req.Count;
            me.Round = req.Round;
            me.Raid = req.Raid;
            me.RaidMap = req.RaidMap ?? "";
            me.NoInvites = req.NoInvites;
            var toasts = new List<string>();
            StatsReply? statsReply = null;
            if (req.History is JsonElement history)
                KeepStats(sessionId, history);
            Party? mine = Find(sessionId);
            if (mine != null && mine.Members.FirstOrDefault(m => m.Id == sessionId) is Member self)
            {
                self.Seen = now;
                if (!string.IsNullOrEmpty(req.Name))
                    self.Name = req.Name;
                // The leader's map screen: its selected map is the party's (sent with every call)
                if (mine.Id == sessionId && mine.State == "open" && !string.IsNullOrEmpty(req.Map))
                    mine.Map = req.Map;
                if (mine.Id == sessionId && mine.State == "open" && req.Headless != null)
                    mine.Headless = req.Headless.Value;
            }
            // A party stuck starting or hosted when the match it was for is gone (the host's game crashed or quit, then
            // came back to the menu: user, 2026-11-10, the only fix was a server restart). The leader is back, in no
            // match or raid, and says it isn't hosting: the party opens again (same players, all NOT READY). A member
            // like that whose leader has been silent a minute drops out of it instead, free to make a new lobby.
            if (mine != null && mine.State != "open" && req.Busy == false && string.IsNullOrEmpty(me.Match) && !me.Raid
                && req.Op is not ("start" or "hosted" or "done"))
            {
                if (mine.Id == sessionId)
                {
                    ReopenDead(mine, now);
                }
                else if (mine.Members.FirstOrDefault(m => m.Id == mine.Id) is not Member lead || (now - lead.Seen).TotalSeconds > 60)
                {
                    mine.Members.RemoveAll(m => m.Id == sessionId);
                    logger.Info($"[Zombies] Party: {NameOf(sessionId)} left {NameOf(mine.Id)}'s party, its match never came (the host is gone)");
                    Tell(sessionId, "The match never started (the host left): make or join a new lobby.");
                    if (mine.Members.Count == 0)
                        _parties.Remove(mine.Id);
                    mine = null;
                }
            }
            switch (req.Op)
            {
                case "create":
                    mine = Create(sessionId, req, now);
                    break;
                case "join":
                    if (req.PartyId != null && _parties.TryGetValue(req.PartyId, out Party? p) && p.State == "open")
                    {
                        if (p.Members.Count >= MaxPlayers)
                            error = "That party is full.";
                        else if (Barred(sessionId, p) && !HasInvite(sessionId, p, fromHost: true))
                            error = "You were kicked from that party too many times: only the host can invite you back.";
                        else if (p.Private && !HasInvite(sessionId, p))
                            error = "That party is invite only.";
                        else
                        {
                            Leave(sessionId);
                            p.Members.Add(new Member { Id = sessionId, Name = req.Name ?? "Player", Seen = now });
                            mine = p;
                        }
                    }
                    else
                        error = "That party isn't open any more.";
                    _invites.Remove(sessionId);
                    break;
                case "leave":
                case "disband":
                    // The host leaving disbands the lobby (Leave does it); a player just leaves
                    Leave(sessionId);
                    mine = null;
                    break;
                case "requestjoin":
                    // Asking to join a private lobby (user, 2026-11-11): the host answers
                    if (req.PartyId != null && _parties.TryGetValue(req.PartyId, out Party? askP) && askP.State == "open" && askP.Id != sessionId)
                    {
                        if (askP.Members.Count >= MaxPlayers)
                            toasts.Add("That lobby is full.");
                        else if (askP.Requests.TryGetValue(sessionId, out DateTime askedAt) && (now - askedAt).TotalSeconds < RequestFor)
                            toasts.Add("You've already asked. Wait for the host.");
                        else
                        {
                            askP.Requests[sessionId] = now;
                            toasts.Add($"Asked {NameOf(askP.Id)} to join their lobby.");
                        }
                    }
                    else
                        toasts.Add("That lobby isn't open any more.");
                    break;
                case "acceptjoin":
                case "declinejoin":
                    if (mine != null && mine.Id == sessionId && !string.IsNullOrEmpty(req.Target) && mine.Requests.Remove(req.Target))
                    {
                        if (req.Op == "declinejoin")
                            Tell(req.Target, $"{me.Name} declined your request to join.");
                        else if (mine.State != "open" || mine.Members.Count >= MaxPlayers)
                            Tell(req.Target, $"{me.Name}'s lobby is full or starting.");
                        else
                        {
                            Leave(req.Target);
                            mine.Members.Add(new Member { Id = req.Target, Name = NameOf(req.Target), Seen = now });
                            _invites.Remove(req.Target);
                            Tell(req.Target, $"{me.Name} let you into their lobby.");
                        }
                    }
                    break;
                case "getstats":
                    if (!string.IsNullOrEmpty(req.Target))
                        statsReply = StatsOf(req.Target);
                    break;
                case "ready":
                    if (mine?.Members.FirstOrDefault(m => m.Id == sessionId) is Member ready)
                        ready.Ready = req.Ready;
                    break;
                case "map":
                    if (mine != null && mine.Id == sessionId && mine.State == "open" && !string.IsNullOrEmpty(req.Map))
                        mine.Map = req.Map;
                    break;
                case "private":
                    if (mine != null && mine.Id == sessionId)
                        mine.Private = req.Private;
                    break;
                case "invite":
                    // Anyone in the party can invite (user, 2026-11-09); a player gets one invite at a time
                    if (string.IsNullOrEmpty(req.Target) || req.Target == sessionId)
                        break;
                    string who = NameOf(req.Target);
                    if ((now - me.LastInvite).TotalSeconds < InviteGap)
                    {
                        toasts.Add("Slow down: one invite every few seconds.");
                        break;
                    }
                    if (mine != null && mine.State != "open")
                    {
                        error = "The match is already starting.";
                        break;
                    }
                    if (mine != null && mine.Members.Any(m => m.Id == req.Target))
                    {
                        toasts.Add($"{who} is already in your party.");
                        break;
                    }
                    if (mine != null && mine.Members.Count >= MaxPlayers)
                    {
                        toasts.Add("The party is full.");
                        break;
                    }
                    if (Busy(req.Target, now))
                    {
                        me.LastInvite = now;
                        toasts.Add($"{who} is busy (in a raid).");
                        break;
                    }
                    if (_seen.TryGetValue(req.Target, out Presence? them) && them.NoInvites && them.Seen > now.AddSeconds(-OnlineFor))
                    {
                        me.LastInvite = now;
                        toasts.Add($"{who} has invites off!");
                        break;
                    }
                    // Someone in a zombies match can be invited (it pops up in their game), but not over and over
                    if (them != null && !string.IsNullOrEmpty(them.Match) && (now - them.LastInvited).TotalSeconds < InMatchInviteGap)
                    {
                        toasts.Add($"{who} is in a match and was invited moments ago.");
                        break;
                    }
                    if (_invites.TryGetValue(req.Target, out Invite? pending))
                    {
                        toasts.Add(mine != null && pending.PartyId == mine.Id
                            ? $"{who} already has your party's invite."
                            : $"{who} already has an invite waiting. Try again in a moment.");
                        break;
                    }
                    if (_noUntil.TryGetValue((sessionId, req.Target), out DateTime until) && until > now)
                    {
                        toasts.Add($"{who} said no. You can invite them again in {Math.Ceiling((until - now).TotalSeconds)} s.");
                        break;
                    }
                    if (mine == null)
                        mine = Create(sessionId, req, now);
                    me.LastInvite = now;
                    if (them != null)
                        them.LastInvited = now;
                    notice = SendInvite(sessionId, me, mine, req.Target, now);
                    break;
                case "countdown":
                    // The leader's START MATCH: only with everyone else ready
                    if (mine != null && mine.Id == sessionId && mine.State == "open" && mine.CountdownEnd == null)
                    {
                        if (mine.Members.Count < 2 || mine.Members.Any(m => m.Id != mine.Id && !m.Ready))
                            toasts.Add("Not everyone's ready.");
                        else
                            mine.CountdownEnd = now.AddSeconds(CountdownSeconds);
                    }
                    break;
                case "cancelcountdown":
                    if (mine != null && mine.Id == sessionId && mine.CountdownEnd != null)
                        StopCountdown(mine, $"{me.Name} stopped the countdown.");
                    break;
                case "kick":
                    // The leader removes a member (user, 2026-11-09); they can come back only with a new invite
                    if (mine == null || mine.Id != sessionId || string.IsNullOrEmpty(req.Target) || req.Target == sessionId)
                        break;
                    if (mine.State != "open")
                    {
                        error = "The match is already starting.";
                        break;
                    }
                    if (mine.Members.RemoveAll(m => m.Id == req.Target) > 0)
                    {
                        mine.Kicked[req.Target] = mine.Kicked.TryGetValue(req.Target, out int times) ? times + 1 : 1;
                        if (_invites.TryGetValue(req.Target, out Invite? kickedInvite) && kickedInvite.PartyId == mine.Id)
                            _invites.Remove(req.Target);
                        Tell(req.Target, $"{me.Name} removed you from the party.");
                        toasts.Add($"{NameOf(req.Target)} was removed from the party.");
                    }
                    break;
                case "decline":
                    if (_invites.TryGetValue(sessionId, out Invite? declined))
                    {
                        _invites.Remove(sessionId);
                        Tell(declined.FromId, $"{NameOf(sessionId)} declined your invite.");
                        var pair = (declined.FromId, sessionId);
                        int nos = _noCount.TryGetValue(pair, out var had) && (now - had.Last).TotalSeconds < NoForget ? had.Count + 1 : 1;
                        _noCount[pair] = (nos, now);
                        _noUntil[pair] = now.AddSeconds(Math.Min(NoStep * nos, NoMax));
                        if (_parties.TryGetValue(declined.PartyId, out Party? from))
                            from.Invited.Remove(sessionId);
                    }
                    break;
                case "start":
                    // Only ever forward: the leader's "hosted" can come first (Fika's server is up that fast)
                    if (mine != null && mine.Id == sessionId && mine.State == "open")
                        mine.State = "starting";
                    break;
                case "hosted":
                    if (mine != null && mine.Id == sessionId)
                    {
                        mine.State = "hosted";
                        mine.FikaMatch = req.FikaMatch ?? "";
                    }
                    break;
                case "done":
                    if (mine != null && mine.Id == sessionId)
                    {
                        _parties.Remove(mine.Id);
                        mine = null;
                    }
                    break;
            }
            Invite? invite = _invites.TryGetValue(sessionId, out Invite? waiting) && mine?.Id != waiting.PartyId ? waiting : null;
            if (invite != null)
                invite.Missing = Missing(me, invite.Map, _seen.TryGetValue(invite.PartyId, out Presence? host) ? host : null);
            // A countdown stops when someone un-readies, leaves or a new host takes over; the time left goes out
            foreach (Party cp in _parties.Values)
            {
                if (cp.CountdownEnd != null && cp.State == "open")
                {
                    Member? notReady = cp.Members.FirstOrDefault(m => m.Id != cp.Id && !m.Ready);
                    if (cp.Members.Count < 2 || notReady != null)
                        StopCountdown(cp, notReady != null ? $"Countdown stopped: {notReady.Name} isn't ready." : "Countdown stopped: nobody else is in the party.");
                    else if ((now - cp.CountdownEnd.Value).TotalSeconds > 15)
                        StopCountdown(cp, "The match didn't start. Press START MATCH again.");
                }
                else if (cp.State != "open")
                    cp.CountdownEnd = null;
                cp.Countdown = cp.CountdownEnd == null ? -1 : Math.Max(0, (cp.CountdownEnd.Value - now).TotalSeconds);
            }
            if (_outcomes.TryGetValue(sessionId, out List<string>? told))
            {
                toasts.AddRange(told);
                _outcomes.Remove(sessionId);
            }
            // The host's waiting join requests (old ones and players gone quiet dropped)
            var requests = new List<JoinRequest>();
            if (mine != null && mine.Id == sessionId)
            {
                foreach (var (rid, at) in mine.Requests.ToList())
                    if ((now - at).TotalSeconds > RequestFor || mine.Members.Any(m => m.Id == rid))
                        mine.Requests.Remove(rid);
                requests = mine.Requests.Keys.Select(rid => new JoinRequest { Id = rid, Name = NameOf(rid) }).ToList();
            }
            return new Reply
            {
                Requests = requests,
                Parties = _parties.Values.Where(x => x.State == "open").ToList(),
                Joinable = _parties.Values.Where(x => x.State == "open" && MayJoin(sessionId, x)).Select(x => x.Id).ToList(),
                Mine = mine,
                Players = req.Players ? Players(sessionId, me, mine, now) : new(),
                Invite = invite,
                Error = error,
                Notice = notice,
                Toasts = toasts,
                Stats = statsReply,
                HeadlessFree = HeadlessCount(out int known),
                HeadlessKnown = known,
            };
        }
    }

    private Party Create(string sessionId, PartyRequest req, DateTime now)
    {
        Leave(sessionId);
        var party = new Party { Id = sessionId, Map = req.Map ?? "", Made = now, Private = req.Private };
        party.Members.Add(new Member { Id = sessionId, Name = req.Name ?? "Player", Seen = now });
        _parties[sessionId] = party;
        return party;
    }

    /// <summary>
    /// The invite: kept for the target (their game shows it the next time it calls, with what they're missing), and,
    /// when they've never called here (no zombies mode), an SPT chat message saying what to get.
    /// </summary>
    private string SendInvite(string sessionId, Presence me, Party party, string target, DateTime now)
    {
        party.Invited.Add(target);
        _invites[target] = new Invite { PartyId = party.Id, From = me.Name, Map = party.Map, Made = now, FromId = sessionId };
        string name = NameOf(target);
        if (_seen.ContainsKey(target))
            return (_seen[target].Seen > now.AddSeconds(-OnlineFor) ? $"Invite sent to {name}." : $"Invite sent to {name}: they'll see it when they're next on.");
        try
        {
            string mods = string.Join(", ", Needed.Values) + (string.IsNullOrEmpty(party.Map) ? "" : $", and the zombies map {MapName(party.Map)}");
            mailSendService.SendSystemMessageToPlayer(new MongoId(target),
                $"{me.Name} invited you to play Dr K's SPZombies{(string.IsNullOrEmpty(party.Map) ? "" : " on " + MapName(party.Map))}. " +
                $"To join, get these mods on SPT Forge: {mods}. Then open PLAY, switch to ZOMBIES and look for the invite.", null, null);
        }
        catch (Exception e)
        {
            logger.Warning($"[Zombies] Invite message to {target} failed: {e.Message}");
        }
        return $"{name} doesn't have the zombies mode: they've been sent a message saying what to get.";
    }

    private static string MapName(string map) =>
        map.StartsWith("zm_") && map.Length > 3 ? char.ToUpperInvariant(map[3]) + map.Substring(4) : map;

    /// <summary>What a player lacks to play the host's match: mods missing (or the core at another version), and the map.</summary>
    private static List<string> Missing(Presence? player, string map, Presence? host)
    {
        var missing = new List<string>();
        if (player == null || player.Mods.Count == 0)
            return Needed.Values.ToList();
        foreach (var (guid, name) in Needed)
        {
            if (!player.Mods.TryGetValue(guid, out string? version))
                missing.Add(name);
            else if (guid == Core && host != null && host.Mods.TryGetValue(Core, out string? hostVersion) && hostVersion != version)
                missing.Add($"{name} {hostVersion} (you have {version})");
        }
        if (!string.IsNullOrEmpty(map) && !player.Maps.Contains(map, StringComparer.OrdinalIgnoreCase))
            missing.Add($"Zombies map: {MapName(map)}");
        return missing;
    }

    private string NameOf(string id)
    {
        if (_seen.TryGetValue(id, out Presence? p) && !string.IsNullOrEmpty(p.Name))
            return p.Name;
        try
        {
            return saveServer.GetProfile(new MongoId(id))?.CharacterData?.PmcData?.Info?.Nickname ?? "that player";
        }
        catch
        {
            return "that player";
        }
    }

    /// <summary>
    /// Every player on this server but the caller, with where they are: in a zombies match (the map and how many are
    /// in it), in the zombies menus, in normal play (online through Fika, or their zombies game says so), or offline.
    /// Zombies first, then online, then offline; each by name.
    /// </summary>
    private List<PlayerInfo> Players(string sessionId, Presence me, Party? mine, DateTime now)
    {
        var list = new List<PlayerInfo>();
        HashSet<string> fikaOnline = FikaOnline();
        foreach (var (id, profile) in saveServer.GetProfiles())
        {
            string key = id.ToString();
            string? nick = profile?.CharacterData?.PmcData?.Info?.Nickname;
            if (key == sessionId || string.IsNullOrEmpty(nick) || nick.StartsWith("headless", StringComparison.OrdinalIgnoreCase))
                continue;
            _seen.TryGetValue(key, out Presence? p);
            bool ours = p != null && p.Seen > now.AddSeconds(-OnlineFor);
            string status = ours && !string.IsNullOrEmpty(p!.Match) ? "match"
                : ours && p!.Zombies ? "zombies"
                : ours || fikaOnline.Contains(key) ? "normal"
                : "offline";
            Party? theirs = Find(key);
            bool joinable = theirs != null && theirs != mine && theirs.State == "open" && theirs.Members.Count < MaxPlayers
                && MayJoin(sessionId, theirs);
            list.Add(new PlayerInfo
            {
                Id = key,
                Name = nick,
                Zombies = p != null,
                Online = status != "offline",
                Missing = p == null ? new() : Missing(p, mine?.Map ?? "", me),
                InParty = mine != null && mine.Members.Any(m => m.Id == key),
                // INVITED only while that invite is waiting: declined or unanswered, INVITE comes back
                Invited = mine != null && _invites.TryGetValue(key, out Invite? waitingFor) && waitingFor.PartyId == mine.Id,
                InRaid = status == "normal" && (ours && p!.Raid || _fikaInRaid.Contains(key)),
                Status = status,
                Match = status == "match" ? p!.Match : "",
                Count = status == "match" ? p!.Count : 0,
                Round = status == "match" ? p!.Round : 0,
                JoinParty = joinable ? theirs!.Id : null,
                HasStats = Stats().ContainsKey(key),
                PartySize = theirs != null && theirs.State == "open" ? theirs.Members.Count : 0,
                RequestParty = theirs != null && theirs != mine && theirs.State == "open" && theirs.Members.Count < MaxPlayers && !joinable ? theirs.Id : null,
                Requested = theirs != null && theirs.Requests.ContainsKey(sessionId),
                RaidMap = status == "normal" && ours && p!.Raid ? p.RaidMap : "",
            });
        }
        int Rank(PlayerInfo x) => x.Status is "match" or "zombies" ? 0 : x.Status == "normal" ? 1 : 2;
        return list.OrderBy(Rank).ThenBy(x => x.Status == "match" ? 0 : 1).ThenBy(x => x.Name).Take(60).ToList();
    }

    // Fika's own list of who's online (its PresenceService, read only, never changed): profile ids. Looked up by name
    // so this mod doesn't need Fika (without Fika there's no co-op, and the list only knows zombies players)
    private object? _fikaPresence;
    private System.Reflection.FieldInfo? _fikaOnline;
    private bool _fikaLooked;
    private DateTime _fikaAt;
    private HashSet<string> _fikaCache = new();
    private HashSet<string> _fikaInRaid = new();

    // Fika's own headless list (its HeadlessService, read only, never changed): how many are READY
    private object? _fikaHeadless;
    private System.Reflection.PropertyInfo? _fikaHeadlessClients;
    private bool _headlessLooked;

    private int HeadlessCount(out int known)
    {
        known = 0;
        try
        {
            if (!_headlessLooked)
            {
                _headlessLooked = true;
                Type? type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("FikaServer.Services.Headless.HeadlessService", false)).FirstOrDefault(t => t != null);
                if (type != null)
                {
                    _fikaHeadless = services.GetService(type);
                    _fikaHeadlessClients = type.GetProperty("HeadlessClients");
                }
                logger.Info(_fikaHeadless != null && _fikaHeadlessClients != null ? "[Zombies] Lobby: Fika's headless list found" : "[Zombies] Lobby: no Fika headless list (host mode only)");
            }
            if (_fikaHeadless == null || _fikaHeadlessClients?.GetValue(_fikaHeadless) is not System.Collections.IDictionary clients)
                return 0;
            int ready = 0;
            foreach (System.Collections.DictionaryEntry e in clients)
            {
                known++;
                object? v = e.Value;
                object? state = v?.GetType().GetProperty("State")?.GetValue(v) ?? v?.GetType().GetField("State")?.GetValue(v);
                if (state?.ToString() == "READY")
                    ready++;
            }
            return ready;
        }
        catch (Exception e)
        {
            logger.Warning($"[Zombies] Reading Fika's headless list failed: {e.Message}");
            return 0;
        }
    }

    private HashSet<string> FikaOnline()
    {
        if ((DateTime.UtcNow - _fikaAt).TotalSeconds < 2)
            return _fikaCache;
        _fikaAt = DateTime.UtcNow;
        try
        {
            if (!_fikaLooked)
            {
                _fikaLooked = true;
                Type? type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("FikaServer.Services.PresenceService", false)).FirstOrDefault(t => t != null);
                if (type != null)
                {
                    _fikaPresence = services.GetService(type);
                    _fikaOnline = type.GetField("_onlinePlayers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                }
                logger.Info(_fikaPresence != null && _fikaOnline != null ? "[Zombies] Players list: Fika's online list found" : "[Zombies] Players list: no Fika online list (zombies players only)");
            }
            var ids = new HashSet<string>();
            var inRaid = new HashSet<string>();
            if (_fikaPresence != null && _fikaOnline?.GetValue(_fikaPresence) is System.Collections.IDictionary online)
                foreach (System.Collections.DictionaryEntry e in online)
                {
                    string id = e.Key.ToString() ?? "";
                    ids.Add(id);
                    if (ActivityOf(e.Value).Contains("RAID", StringComparison.OrdinalIgnoreCase))
                        inRaid.Add(id);
                }
            _fikaCache = ids;
            _fikaInRaid = inRaid;
        }
        catch (Exception e)
        {
            logger.Warning($"[Zombies] Reading Fika's online list failed: {e.Message}");
        }
        return _fikaCache;
    }

    // Invite only = only with an invite to it still waiting (an old, used or declined invite doesn't count: the
    // private switch blocked nobody who'd ever been invited, user 2026-11-09); someone kicked needs a new invite too
    private bool HasInvite(string sessionId, Party p, bool fromHost = false) =>
        _invites.TryGetValue(sessionId, out Invite? inv) && inv.PartyId == p.Id && (!fromHost || inv.FromId == p.Id);

    // Kicked 3 times from this party (user, 2026-11-09)
    private const int KicksToBar = 3;

    private static bool Barred(string sessionId, Party p) => p.Kicked.TryGetValue(sessionId, out int n) && n >= KicksToBar;

    private bool MayJoin(string sessionId, Party p) =>
        Barred(sessionId, p) ? HasInvite(sessionId, p, fromHost: true) : !p.Private || HasInvite(sessionId, p);

    private const double CountdownSeconds = 5;

    private void StopCountdown(Party p, string why)
    {
        p.CountdownEnd = null;
        p.Countdown = -1;
        foreach (Member m in p.Members)
            Tell(m.Id, why);
    }

    private void Tell(string playerId, string text)
    {
        if (string.IsNullOrEmpty(playerId))
            return;
        if (!_outcomes.TryGetValue(playerId, out List<string>? list))
            _outcomes[playerId] = list = new List<string>();
        if (list.Count < 6)
            list.Add(text);
    }

    // In a normal raid: their zombies game says so, or Fika's online list has them in one. A zombies match isn't busy:
    // the invite pops up in their match (user, 2026-11-10), at most one every 30 s
    private bool Busy(string id, DateTime now)
    {
        if (_seen.TryGetValue(id, out Presence? p) && p.Seen > now.AddSeconds(-OnlineFor))
        {
            if (!string.IsNullOrEmpty(p.Match))
                return false;
            if (p.Raid)
                return true;
        }
        FikaOnline();
        return _fikaInRaid.Contains(id);
    }

    // Fika's presence entry's activity (in menu / in raid / in hideout), by name: read only
    private static string ActivityOf(object? presence)
    {
        if (presence == null)
            return "";
        try
        {
            Type t = presence.GetType();
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            object? v = t.GetProperty("Activity", all)?.GetValue(presence) ?? t.GetField("Activity", all)?.GetValue(presence)
                ?? t.GetField("<Activity>k__BackingField", all)?.GetValue(presence);
            return v?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private Party? Find(string sessionId) => _parties.Values.FirstOrDefault(p => p.Members.Any(m => m.Id == sessionId));

    private void Leave(string sessionId)
    {
        Party? p = Find(sessionId);
        if (p == null)
            return;
        if (p.Id == sessionId)
            LeaderGone(p); // the next member becomes the host (only while the lobby's open)
        else
        {
            string name = p.Members.FirstOrDefault(m => m.Id == sessionId)?.Name ?? NameOf(sessionId);
            p.Members.RemoveAll(m => m.Id == sessionId);
            // Someone left an open lobby: everyone's NOT READY again and a countdown stops (user, 2026-11-09). Once the
            // match is loading it carries on with whoever's left
            if (p.State == "open")
            {
                foreach (Member m in p.Members)
                    m.Ready = false;
                p.CountdownEnd = null;
                foreach (Member m in p.Members)
                    Tell(m.Id, $"{name} left the lobby: ready up again.");
            }
        }
    }

    /// <summary>
    /// The host left (or stopped calling): the lobby is disbanded and everyone goes back to the zombies menu (user,
    /// 2026-11-10; it used to pass to the next player). Invites to it go too. A party already starting or hosted
    /// belongs to the old host's match: it goes quietly.
    /// </summary>
    private void LeaderGone(Party p)
    {
        string oldId = p.Id;
        string oldName = p.Members.FirstOrDefault(m => m.Id == oldId)?.Name ?? NameOf(oldId);
        _parties.Remove(oldId);
        foreach (var (target, inv) in _invites.ToList())
            if (inv.PartyId == oldId)
                _invites.Remove(target);
        if (p.State == "open")
            foreach (Member m in p.Members)
                if (m.Id != oldId)
                    Tell(m.Id, $"{oldName} disbanded the lobby.");
        p.Members.Clear();
        logger.Info($"[Zombies] Party: {oldName}'s lobby disbanded");
    }

    private void ReopenDead(Party p, DateTime now)
    {
        logger.Info($"[Zombies] Party: {NameOf(p.Id)}'s party was '{p.State}' but its match is gone (the host's game is back on the menu): open again");
        p.State = "open";
        p.FikaMatch = "";
        p.CountdownEnd = null;
        p.Made = now;
        foreach (Member m in p.Members)
        {
            m.Ready = false;
            Tell(m.Id, "The last match didn't start (the host's game left it): the lobby is open again, ready up.");
        }
    }

    // Players who stopped calling drop out; a party whose leader stopped closes. Hosted parties stay a while (the
    // games are loading the map, not calling). Old invites, and invites to parties that are gone, go too
    private void Prune(DateTime now)
    {
        foreach (Party p in _parties.Values.ToList())
        {
            if ((now - p.Made).TotalSeconds > StaleAfter)
            {
                _parties.Remove(p.Id);
                continue;
            }
            if (p.State != "open")
                continue;
            p.Members.RemoveAll(m => m.Id != p.Id && (now - m.Seen).TotalSeconds > MemberTimeout);
            Member? leader = p.Members.FirstOrDefault(m => m.Id == p.Id);
            if (leader == null || (now - leader.Seen).TotalSeconds > LeaderTimeout)
                LeaderGone(p);
        }
        foreach (var (target, invite) in _invites.ToList())
        {
            bool open = _parties.TryGetValue(invite.PartyId, out Party? p) && p.State == "open";
            bool old = (now - invite.Made).TotalSeconds > InviteFor;
            if (!old && open)
                continue;
            _invites.Remove(target);
            if (open && old)
            {
                Tell(invite.FromId, $"{NameOf(target)} didn't answer your invite.");
                p!.Invited.Remove(target);
            }
        }
        foreach (var (key, until) in _noUntil.ToList())
            if (until <= now)
                _noUntil.Remove(key);
        foreach (var (key, no) in _noCount.ToList())
            if ((now - no.Last).TotalSeconds > NoForget)
                _noCount.Remove(key);
    }
}

public record PartyRequest : IRequestData
{
    [JsonPropertyName("op")] public string Op { get; set; } = "list";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
    // "hosted": the Fika match id when a Fika headless hosts for the leader (else the leader's own id)
    [JsonPropertyName("fikaMatch")] public string? FikaMatch { get; set; }
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("partyId")] public string? PartyId { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
    [JsonPropertyName("private")] public bool Private { get; set; }
    [JsonPropertyName("players")] public bool Players { get; set; }
    [JsonPropertyName("mods")] public Dictionary<string, string>? Mods { get; set; }
    [JsonPropertyName("maps")] public List<string>? Maps { get; set; }
    [JsonPropertyName("zombies")] public bool Zombies { get; set; }
    [JsonPropertyName("match")] public string? Match { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("round")] public int Round { get; set; }
    [JsonPropertyName("raid")] public bool Raid { get; set; }
    [JsonPropertyName("raidMap")] public string? RaidMap { get; set; }
    // The leader's Fika mode (headless when free / host)
    [JsonPropertyName("headless")] public bool? Headless { get; set; }
    [JsonPropertyName("noInvites")] public bool NoInvites { get; set; }
    // This game is hosting or joining a match right now (null: an older client that doesn't say)
    [JsonPropertyName("busy")] public bool? Busy { get; set; }
    // The caller's own match list (their lifetime statistics), sent whenever it has grown
    [JsonPropertyName("history")] public JsonElement? History { get; set; }
}

[Injectable(TypePriority = OnLoadOrder.Routers)]
public class ZombiesPartyRouter(JsonUtil jsonUtil, ZombiesPartyService party, HttpResponseUtil httpResponseUtil) : SPTarkov.Server.Core.DI.StaticRouter(jsonUtil, [
    new SPTarkov.Server.Core.DI.RouteAction<PartyRequest>(
        "/kzm/party",
        (url, info, sessionId, output, cancellationToken) =>
            ValueTask.FromResult(httpResponseUtil.NoBody(party.Handle(sessionId.ToString(), info)))
    ),
]) { }
