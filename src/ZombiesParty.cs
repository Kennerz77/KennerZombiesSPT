using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using SPT.Common.Http;
using SPT.Reflection.Utils;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies party (co-op with Fika), behind the zombies main menu's lobby (ZombiesFrontEnd draws it; this keeps it
    /// in step with the server). One player makes a lobby (private by default) and picks the map; others join by invite
    /// (or freely when it's open); everyone readies up and the host's START MATCH runs a 5 s countdown. At zero the host's
    /// game walks EFT's own screens by itself (PLAY, the PMC, the map, NEXT, then Fika's START) to host the Fika raid,
    /// and once it's up the others' games join it from wherever they are (Fika's own join): the whole lobby goes into the
    /// loading screen together, and the match starts once everyone's in (ZombiesLobby). The host leaving disbands the
    /// lobby (everyone back to the zombies menu); a player leaving just leaves it, and everyone's NOT READY again.
    /// The party lives on our server mod (POST /kzm/party, in memory). Fika itself is never changed; without Fika there
    /// are no lobbies, only SOLO.
    /// Each player's lifetime statistics (ZombiesHistory) go up to the server too, so anyone can open them from the
    /// players list ("X's statistics").
    /// </summary>
    internal static class ZombiesParty
    {
        private const string Route = "/kzm/party";

        public class Member
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("ready")] public bool Ready;
        }

        public class Party
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("map")] public string Map;
            [JsonProperty("state")] public string State;
            [JsonProperty("private")] public bool Private;
            [JsonProperty("headless")] public bool Headless;
            [JsonProperty("countdown")] public double Countdown = -1;
            // The Fika match to join: a headless's profile id when a headless hosts, else empty (= the leader's id)
            [JsonProperty("fikaMatch")] public string FikaMatch;
            [JsonProperty("members")] public List<Member> Members = new List<Member>();
        }

        public class PlayerInfo
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("online")] public bool Online;
            [JsonProperty("zombies")] public bool Zombies;
            [JsonProperty("missing")] public List<string> Missing = new List<string>();
            [JsonProperty("inParty")] public bool InParty;
            [JsonProperty("invited")] public bool Invited;
            [JsonProperty("status")] public string Status = "offline"; // offline, normal, zombies, match
            [JsonProperty("match")] public string Match = "";
            [JsonProperty("inRaid")] public bool InRaid;
            [JsonProperty("count")] public int Count;
            [JsonProperty("round")] public int Round;
            [JsonProperty("max")] public int Max = 4;
            [JsonProperty("joinParty")] public string JoinParty;
            [JsonProperty("hasStats")] public bool HasStats;
            [JsonProperty("partySize")] public int PartySize;
            [JsonProperty("raidMap")] public string RaidMap = "";
            [JsonProperty("requestParty")] public string RequestParty;
            [JsonProperty("requested")] public bool Requested;
        }

        public class JoinRequest
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
        }

        public class Invite
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("partyId")] public string PartyId;
            [JsonProperty("from")] public string From;
            [JsonProperty("map")] public string Map;
            [JsonProperty("missing")] public List<string> Missing = new List<string>();
        }

        public class StatsReply
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("matches")] public List<ZombiesHistory.Match> Matches;
        }

        private class Reply
        {
            [JsonProperty("parties")] public List<Party> Parties = new List<Party>();
            [JsonProperty("joinable")] public List<string> Joinable = new List<string>();
            [JsonProperty("mine")] public Party Mine;
            [JsonProperty("players")] public List<PlayerInfo> Players = new List<PlayerInfo>();
            [JsonProperty("invite")] public Invite Invite;
            [JsonProperty("error")] public string Error;
            [JsonProperty("notice")] public string Notice;
            [JsonProperty("toasts")] public List<string> Toasts = new List<string>();
            [JsonProperty("stats")] public StatsReply Stats;
            [JsonProperty("headlessFree")] public int HeadlessFree;
            [JsonProperty("headlessKnown")] public int HeadlessKnown;
            [JsonProperty("requests")] public List<JoinRequest> Requests = new List<JoinRequest>();
        }

        // Ops that carry more than the op name (invite, private, decline, kick, map, stats), sent before the next call
        private static readonly Queue<Dictionary<string, object>> Ops = new Queue<Dictionary<string, object>>();
        private static string _dismissed;
        // New lobbies start private (invite only): user, 2026-11-10
        private static bool _wantPrivate = true;

        /// <summary>The zombies mods this game has, by plugin GUID (the server checks invites against the host's).</summary>
        private static Dictionary<string, string> _mods;

        private static readonly string[] Guids =
        {
            "com.kennerz.zmcore", "com.kennerz.paphideoutmodule", "com.kennerz.perkbeverages", "com.kennerz.raygun",
            "com.kennerz.thundergun", "com.mizmii.custommaploader", "com.fika.core",
        };

        private static Dictionary<string, string> Mods()
        {
            if (_mods != null)
                return _mods;
            _mods = new Dictionary<string, string>();
            foreach (string guid in Guids)
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out var info))
                    _mods[guid] = info.Metadata.Version.ToString();
            return _mods;
        }

        /// <summary>Logged in, at the menus (not in a raid or the hideout, not loading a match).</summary>
        public static bool AtMenu =>
            !ZombiesMatch.Active && ZombiesPlugin.AtMenus && ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile != null;

        private static Reply _last;
        private static string _pendingOp;
        private static bool _wantReady, _busy, _hostPressed, _joining;
        private static float _nextPoll, _startedAt = -1f;
        private static bool _wasShowing;
        private static string _error;

        /// <summary>The party's size when the raid was hosted (the in-raid lobby starts once this many are in), else 0.</summary>
        public static int Expected { get; private set; }

        /// <summary>SOLO was played: a one-player Fika raid, started by itself (no party, no START press).</summary>
        public static bool Solo { get; private set; }

        /// <summary>True when this game leads the party that's being hosted.</summary>
        public static bool Leading { get; private set; }

        public static string MyId => ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile?.ProfileId;
        private static string MyName => ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile?.Nickname ?? "Player";

        // The players list has come from the server since the zombies menu opened (until then it says LOADING, so
        // nobody thinks no one's on: user, 2026-11-09)
        private static volatile bool _listsLoaded;
        private static BepInEx.Configuration.ConfigEntry<bool> _invitesOn, _useHeadless;

        public static void Bind(BepInEx.Configuration.ConfigFile config, string section)
        {
            _useHeadless = config.Bind(section, "Host on a headless when one is free", true, new BepInEx.Configuration.ConfigDescription(
                "As the lobby's host: a free Fika headless hosts the match (smoother for everyone). Off: your own game hosts."));
            _invitesOn = config.Bind(section, "Invite pop-ups", true, new BepInEx.Configuration.ConfigDescription(
                "Zombies invites from other players pop up. Off: nobody can invite you (they're told you have invites turned off)."));
        }

        // ---------- What the zombies menu shows ----------

        /// <summary>Fika is here (lobbies, invites and joining need it; SOLO doesn't).</summary>
        public static bool HasFika => ZombiesNet.FikaJoin != null;

        /// <summary>This player's lobby, or null.</summary>
        public static Party Mine => _last?.Mine;

        public static bool IsHost => Mine != null && Mine.Id == MyId;

        public static bool ListsLoaded => _listsLoaded;

        // The players list as the server last sent it (kept between calls that don't ask for it: the match's Esc menu
        // had to wait for a fresh one, user 2026-11-11)
        private static volatile List<PlayerInfo> _players = new List<PlayerInfo>();

        public static List<PlayerInfo> Players => _players;

        /// <summary>Fika headless clients free right now (by the server's count of Fika's own list).</summary>
        public static int HeadlessFree => _last?.HeadlessFree ?? 0;

        /// <summary>The lobby's Fika mode as the host chose it: a headless (when one is free) or the host's own game.</summary>
        public static bool UseHeadless
        {
            get => _useHeadless == null || _useHeadless.Value;
            set
            {
                if (_useHeadless != null)
                    _useHeadless.Value = value;
                _nextPoll = 0f;
            }
        }

        public static bool WantReady => _wantReady;

        /// <summary>Invites from other players pop up (the zombies settings' switch; off: senders are told so).</summary>
        public static bool InvitesOn
        {
            get => _invitesOn == null || _invitesOn.Value;
            set
            {
                if (_invitesOn != null)
                    _invitesOn.Value = value;
                _nextPoll = 0f; // the server hears it at once
            }
        }

        private static string RaidLocation()
        {
            try
            {
                return Singleton<GameWorld>.Instance?.LocationId ?? "";
            }
            catch
            {
                return "";
            }
        }

        public static string Error => _error;

        /// <summary>The map the host has picked for the lobby (the first zombies map until they change it).</summary>
        public static string LobbyMap
        {
            get
            {
                Party mine = Mine;
                if (mine != null && !IsHost && !string.IsNullOrEmpty(mine.Map))
                    return mine.Map;
                if (string.IsNullOrEmpty(_lobbyMap) || !ZombiesMaps.Is(_lobbyMap))
                    _lobbyMap = MapIds.FirstOrDefault();
                return _lobbyMap;
            }
        }

        private static string _lobbyMap;

        /// <summary>Everyone but the host is ready (the host counts as ready: user, 2026-11-10).</summary>
        public static bool AllOthersReady(Party mine) =>
            mine != null && mine.Members.Count > 1 && mine.Members.Where(m => m.Id != mine.Id).All(m => m.Ready);

        /// <summary>The host's START MATCH is lit: everyone else ready, a zombies map picked, nothing under way.</summary>
        public static bool CanStart => IsHost && Mine.State == "open" && AllOthersReady(Mine) && !Leading && ZombiesMaps.Is(LobbyMap) && !Counting;

        /// <summary>The 5 s countdown is running here.</summary>
        public static bool Counting => _cdEnd >= 0f;

        public static int CountdownLeft => Mathf.Max(0, Mathf.CeilToInt(_cdEnd - Time.unscaledTime));

        /// <summary>The match is being hosted or joined right now (the menus are covered).</summary>
        public static bool Starting => AtMenu && (Leading && !Solo && Time.time - _leadSince < 40f || _joining || _goal == Goal.Host);

        private static float _leadSince = -100f;

        /// <summary>An invite is waiting for this player (shown as the pop-up).</summary>
        public static Invite PendingInvite
        {
            get
            {
                Invite inv = _last?.Invite;
                if (_invitesOn != null && !_invitesOn.Value)
                    return null;
                // At the menus, or in a zombies match (it pops up there: user, 2026-11-10)
                bool where = AtMenu || ZombiesMatch.Active && !ZombiesNet.Headless;
                if (inv == null || Key(inv) == _dismissed || !where || !HasFika && inv.Missing.Count == 0)
                    return null;
                return inv;
            }
        }

        /// <summary>The last pop-up messages from the server (declined, busy, X left...), one at a time.</summary>
        public static string TakeToast()
        {
            lock (Toasts)
                return Toasts.Count > 0 ? Toasts.Dequeue() : null;
        }

        private static readonly Queue<string> Toasts = new Queue<string>();

        /// <summary>A message of our own (or EFT's / Fika's, ZombiesNotices) for the top-middle toast.</summary>
        public static void Toast(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            lock (Toasts)
                if (Toasts.Count < 6)
                    Toasts.Enqueue(text);
        }
        private static readonly Dictionary<string, float> Clicked = new Dictionary<string, float>();

        /// <summary>An invite to this player was just sent from here (the button says SENDING for a moment).</summary>
        public static bool Sending(string id) => Clicked.TryGetValue(id, out float at) && Time.unscaledTime - at < 3f;

        // ---------- What the zombies menu does ----------

        /// <summary>CREATE LOBBY: a new lobby, private, with this player as host.</summary>
        public static void CreateLobby()
        {
            if (!HasFika)
                return;
            _wantPrivate = true;
            _wantReady = false;
            _pendingOp = "create";
            _nextPoll = 0f;
            ZombiesPlugin.Log.LogInfo("Zombies party: making a lobby");
        }

        /// <summary>INVITE (a lobby is made first by the server when there's none).</summary>
        public static void InvitePlayer(string id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            if (Mine == null)
                _wantPrivate = true;
            Clicked[id] = Time.unscaledTime;
            Queue(new Dictionary<string, object> { ["op"] = "invite", ["target"] = id });
            ZombiesUi.Sound("invite_sent");
        }

        /// <summary>The host: players asking to join their lobby (not yet answered here).</summary>
        public static List<JoinRequest> Requests =>
            (_last?.Requests ?? new List<JoinRequest>()).Where(r => r != null && !_answered.Contains(r.Id)).ToList();

        private static readonly HashSet<string> _answered = new HashSet<string>();

        /// <summary>Ask to join someone's private lobby (its host gets ACCEPT / DECLINE).</summary>
        public static void RequestJoin(string partyId)
        {
            if (string.IsNullOrEmpty(partyId) || !HasFika)
                return;
            Queue(new Dictionary<string, object> { ["op"] = "requestjoin", ["partyId"] = partyId });
            ZombiesUi.Sound("invite_sent");
        }

        /// <summary>The host answers a join request.</summary>
        public static void AnswerRequest(string id, bool accept)
        {
            if (string.IsNullOrEmpty(id))
                return;
            _answered.Add(id);
            Queue(new Dictionary<string, object> { ["op"] = accept ? "acceptjoin" : "declinejoin", ["target"] = id });
        }

        /// <summary>JOIN a friend's open lobby.</summary>
        public static void JoinParty(string partyId)
        {
            if (string.IsNullOrEmpty(partyId))
                return;
            _pendingOp = "join:" + partyId;
            _wantReady = false;
            _nextPoll = 0f;
        }

        public static void ToggleReady()
        {
            _wantReady = !_wantReady;
            _pendingOp = "ready";
            _nextPoll = 0f;
        }

        public static void TogglePrivate()
        {
            if (!IsHost)
                return;
            Queue(new Dictionary<string, object> { ["op"] = "private", ["private"] = _wantPrivate = !Mine.Private });
        }

        /// <summary>The host's CHANGE MAP: everyone's lobby shows it.</summary>
        public static void SetMap(string id)
        {
            if (!ZombiesMaps.Is(id))
                return;
            _lobbyMap = id;
            if (IsHost)
                Queue(new Dictionary<string, object> { ["op"] = "map", ["map"] = id });
        }

        public static void StartMatch()
        {
            if (!CanStart)
                return;
            Queue(new Dictionary<string, object> { ["op"] = "countdown" });
        }

        public static void CancelCountdown()
        {
            if (!IsHost || !Counting)
                return;
            Queue(new Dictionary<string, object> { ["op"] = "cancelcountdown" });
            StopCountdownHere();
        }

        public static void Kick(string id)
        {
            if (!IsHost || id == MyId)
                return;
            Queue(new Dictionary<string, object> { ["op"] = "kick", ["target"] = id });
        }

        /// <summary>Leaving the lobby: the host's leaving disbands it, a player's only takes them out.</summary>
        public static void LeaveLobby()
        {
            if (Mine == null)
                return;
            _pendingOp = IsHost ? "disband" : "leave";
            _wantReady = false;
            _nextPoll = 0f;
            ZombiesPlugin.Log.LogInfo($"Zombies party: {(IsHost ? "disbanding the lobby" : "leaving the lobby")}");
        }

        public static void AnswerInvite(bool yes)
        {
            Invite inv = _last?.Invite;
            if (inv == null)
                return;
            _dismissed = Key(inv);
            if (!yes || inv.Missing.Count > 0)
            {
                Queue(new Dictionary<string, object> { ["op"] = "decline" });
                return;
            }
            _pendingOp = "join:" + inv.PartyId;
            _wantReady = false;
            _nextPoll = 0f;
            // Back to the zombies main menu, where the lobby is (from wherever the menus are, normal mode included)
            Go(Goal.Menu, null);
        }

        /// <summary>A player's lifetime statistics, asked of the server (the statistics page opens, LOADING until they come).</summary>
        public static void ShowStatsOf(string id, string name)
        {
            if (string.IsNullOrEmpty(id))
                return;
            if (id == MyId)
            {
                ZombiesStatsScreen.Show();
                return;
            }
            _statsWanted = id;
            ZombiesStatsScreen.ShowOther(id, name);
            Queue(new Dictionary<string, object> { ["op"] = "getstats", ["target"] = id });
        }

        private static string _statsWanted;
        private static volatile StatsReply _statsArrived;
        private static int _statsSent = -1;

        /// <summary>SOLO: EFT's map screen in zombies mode, with only the zombies maps and BACK; its NEXT plays solo.</summary>
        public static void GoSolo()
        {
            if (Mine != null)
                return;
            Go(Goal.Solo, null);
        }

        // ---------- Walking EFT's own screens (solo, hosting, an invite taken elsewhere) ----------

        private enum Goal { None, Solo, Host, Menu }
        private static Goal _goal;
        private static string _goalMap;
        private static float _goalUntil, _goalNext;

        /// <summary>On EFT's map screen for SOLO (its NEXT plays solo, its BACK goes straight to the zombies menu).</summary>
        public static bool SoloScreen { get; private set; }

        private static void Go(Goal goal, string map)
        {
            _goal = goal;
            _goalMap = map;
            // Back to the menu from a match (an invite taken there) takes as long as leaving it does
            _goalUntil = Time.time + (goal == Goal.Menu ? 90f : 25f);
            _goalNext = 0f;
        }

        private static EFT.UI.Screens.EEftScreenType? CurrentScreen
        {
            get
            {
                try
                {
                    return Singleton<EFT.UI.Screens.IScreenManager>.Instance?.CurrentScreenType is EFT.UI.Screens.EEftScreenType t ? t : (EFT.UI.Screens.EEftScreenType?)null;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static UnityEngine.CanvasGroup _sideFade;

        private static void TickGoal()
        {
            // The PMC select is never seen on the way (zombies is always the PMC: user, 2026-11-11)
            bool walking = _goal == Goal.Solo || _goal == Goal.Host;
            var sideScreen = ZombiesMainMenu.SideScreen;
            if (sideScreen != null)
            {
                if (_sideFade == null || _sideFade.gameObject != sideScreen.gameObject)
                    _sideFade = sideScreen.GetComponent<UnityEngine.CanvasGroup>() ?? sideScreen.gameObject.AddComponent<UnityEngine.CanvasGroup>();
                _sideFade.alpha = walking ? 0f : 1f;
            }
            if (_goal == Goal.None)
                return;
            // On the side select: on at once, not on the next step
            if (walking && CurrentScreen == EFT.UI.Screens.EEftScreenType.SelectRaidSide)
                _goalNext = 0f;
            // Never walked inside a match (its Esc menu is a "main menu" too)
            if (!AtMenu && Time.time <= _goalUntil)
                return;
            if (Time.time > _goalUntil)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies party: couldn't get to the {_goal} screen in time");
                if (_goal == Goal.Host)
                    Leading = false;
                _goal = Goal.None;
                return;
            }
            if (Time.time < _goalNext)
                return;
            _goalNext = Time.time + 0.6f;
            try
            {
                switch (CurrentScreen)
                {
                    case EFT.UI.Screens.EEftScreenType.MainMenu:
                        if (!ZombiesMapSelect.Zombies)
                            ZombiesMapSelect.SetZombies(true);
                        if (_goal == Goal.Menu)
                        {
                            _goal = Goal.None;
                            break;
                        }
                        ZombiesMainMenu.Menu?.Click(EFT.UI.EMenuType.Play);
                        break;
                    case EFT.UI.Screens.EEftScreenType.SelectRaidSide:
                        if (_goal == Goal.Menu)
                        {
                            EFT.UI.Screens.EftScreenManager.Instance?.TryReturnToRootScreen();
                            break;
                        }
                        var side = ZombiesMainMenu.SideScreen;
                        if (side?._pmcButton?.SpawnedObject != null)
                            side._pmcButton.SpawnedObject.IsToggled = true;
                        side?._nextButton?.OnClick?.Invoke();
                        break;
                    case EFT.UI.Screens.EEftScreenType.SelectLocation:
                        var screen = ZombiesMapSelect.Screen;
                        if (_goal == Goal.Menu)
                        {
                            EFT.UI.Screens.EftScreenManager.Instance?.TryReturnToRootScreen();
                            break;
                        }
                        if (_goal == Goal.Solo)
                        {
                            SoloScreen = true;
                            _goal = Goal.None;
                            ZombiesPlugin.Log.LogInfo("Zombies party: on the map screen for solo");
                            break;
                        }
                        // Hosting: the lobby's map picked, then NEXT (Fika's START is pressed in Tick once it's up)
                        if (screen == null || screen._acceptButton == null)
                            break;
                        if (screen.SelectedLocation?.Id != _goalMap)
                        {
                            PickMap(_goalMap);
                            break;
                        }
                        _goal = Goal.None;
                        _startedAt = Time.time;
                        ZombiesPlugin.Log.LogInfo($"Zombies party: {_goalMap} picked on the map screen, NEXT");
                        screen._acceptButton.OnClick.Invoke();
                        break;
                    default:
                        // Anywhere else at the menus (traders, character, settings): back to the main menu first
                        EFT.UI.Screens.EftScreenManager.Instance?.TryReturnToRootScreen();
                        break;
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies party: the way to the {_goal} screen failed ({e.Message})");
                if (_goal == Goal.Host)
                    Leading = false;
                _goal = Goal.None;
            }
        }

        // ---------- Each frame ----------

        public static void Tick()
        {
            // A Fika headless has no party and no one at its menus: no check-ins with the server
            if (ZombiesNet.Headless)
                return;
            TickGoal();
            TickMapScreen();
            // A host whose match never came (Fika never started it) is back to being a normal lobby member of nothing
            if (Leading && !ZombiesMatch.Active && AtMenu && Time.time - _leadSince > 90f && _goal == Goal.None)
            {
                ZombiesPlugin.Log.LogWarning("Zombies party: the hosted match never started; standing down");
                Leading = false;
                Solo = false;
                _hostPressed = false;
                Expected = 0;
            }
            // A hosted party: Fika's START once the accept screen is up; members join once hosted
            if (Leading && !_hostPressed && ZombiesNet.FikaHostNow != null && _startedAt > 0f && Time.time - _startedAt < 30f)
            {
                // SOLO stays on this game; a lobby goes to a Fika headless when one is free
                if (ZombiesNet.FikaHostNow(!Solo && (_useHeadless?.Value ?? true)))
                {
                    _hostPressed = true;
                    ZombiesPlugin.Log.LogInfo("Zombies party: Fika's START pressed, hosting");
                }
            }
            // The zombies menu: every second. In a lobby elsewhere in the menus: every 2 s, so the lobby keeps you.
            // Anywhere else: every 10 s, so friends see you online and invites reach you
            // The zombies menu, or the match's Esc menu (its social list): the players list comes with every call
            bool showing = ZombiesFrontEnd.Showing || ZombiesFrontEnd.InMatchMenu;
            if (showing && !_wasShowing)
                _nextPoll = 0f; // a fresh list straight away
            _wasShowing = showing;
            bool inParty = _last?.Mine != null;
            bool loggedIn = ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile != null;
            if (!loggedIn || _busy || Time.time < _nextPoll)
                return;
            _nextPoll = Time.time + (showing ? 1f : inParty ? 2f : 10f);
            string op = _pendingOp ?? "list";
            _pendingOp = null;
            Party mine = _last?.Mine;
            bool leadingLobby = mine == null || mine.Id == MyId;
            var body = new Dictionary<string, object>
            {
                ["op"] = op, ["name"] = MyName, ["ready"] = _wantReady,
                // The host's (or a new lobby's) map
                ["map"] = leadingLobby ? LobbyMap ?? "" : "",
                ["mods"] = Mods(), ["maps"] = ZombiesMaps.All.Select(m => m.Map).ToList(),
                ["players"] = showing, ["private"] = _wantPrivate,
                // Where this player is, for everyone's players list
                ["zombies"] = ZombiesMapSelect.Zombies || ZombiesMatch.Active,
                ["match"] = ZombiesMatch.Active ? ZombiesMatch.LocationHere ?? "" : "",
                ["count"] = ZombiesMatch.Active ? ZombiesTeam.List().Count : 0,
                ["round"] = ZombiesMatch.Active ? ZombiesRounds.Round : 0,
                // In a normal raid: shown as NORMAL MODE: IN RAID, and invites to them come back "busy"
                ["raid"] = !ZombiesMatch.Active && ZombiesPlugin.InRealRaid,
                ["raidMap"] = !ZombiesMatch.Active && ZombiesPlugin.InRealRaid ? RaidLocation() : "",
                // F12 "Invite pop-ups" off: nobody can invite you (they're told so)
                ["noInvites"] = _invitesOn != null && !_invitesOn.Value,
                // Hosting or joining a match right now: a party stuck "starting"/"hosted" whose host says no is reopened
                // by the server (its match died with a crashed game)
                ["busy"] = Leading && !Solo || _joining || ZombiesMatch.Active || _goal == Goal.Host,
            };
            // The host's Fika mode, for everyone's lobby
            if (mine != null && mine.Id == MyId)
                body["headless"] = UseHeadless;
            // This player's statistics, sent up whenever there's a new match in them (others can open them)
            try
            {
                var all = ZombiesHistory.All;
                if (all.Count != _statsSent && !ZombiesMatch.Active)
                {
                    body["history"] = all.Skip(Math.Max(0, all.Count - 300)).ToList();
                    _statsSent = all.Count;
                }
            }
            catch
            {
            }
            if (Ops.Count > 0 && op == "list")
                foreach (var pair in Ops.Dequeue())
                    body[pair.Key] = pair.Value;
            if (op.StartsWith("join:"))
            {
                body["op"] = "join";
                body["partyId"] = op.Substring(5);
            }
            if (op == "create")
                body["private"] = true;
            Send(body);
        }

        private static void Send(Dictionary<string, object> body)
        {
            _busy = true;
            string json = JsonConvert.SerializeObject(body);
            bool withLists = body.TryGetValue("players", out object pl) && pl is bool b && b;
            Task.Run(() =>
            {
                try
                {
                    string text = RequestHandler.PostJson(Route, json);
                    return JsonConvert.DeserializeObject<Reply>(text);
                }
                catch (Exception e)
                {
                    return new Reply { Error = "Party server not reachable (" + e.Message + ")" };
                }
            }).ContinueWith(t =>
            {
                Reply r = t.Result;
                // Back on Unity's thread at the next frame (Apply reads it; nothing Unity is touched here)
                _last = r;
                if (withLists && r != null && string.IsNullOrEmpty(r.Error))
                {
                    _listsLoaded = true;
                    _players = r.Players ?? new List<PlayerInfo>();
                }
                _error = r?.Error;
                lock (Toasts)
                {
                    if (r?.Toasts != null)
                        foreach (string t2 in r.Toasts)
                            if (!string.IsNullOrEmpty(t2) && Toasts.Count < 6)
                                Toasts.Enqueue(t2);
                    if (!string.IsNullOrEmpty(r?.Notice) && Toasts.Count < 6)
                        Toasts.Enqueue(r.Notice);
                }
                if (r?.Stats != null)
                    _statsArrived = r.Stats;
                _busy = false;
                Arrived = true;
            });
        }

        private static volatile bool Arrived;

        /// <summary>Each frame: acts on the latest reply (members join a hosted party; the countdown; statistics).</summary>
        public static void Apply()
        {
            CountdownTick();
            StatsReply stats = _statsArrived;
            if (stats != null)
            {
                _statsArrived = null;
                if (stats.Id == _statsWanted)
                    ZombiesStatsScreen.SetOther(stats.Id, stats.Name, stats.Matches);
                _statsWanted = null;
            }
            if (!Arrived)
                return;
            Arrived = false;
            Party mine = _last?.Mine;
            // The countdown's end on this game's clock (kept steady: only moved when the server's says otherwise)
            if (mine != null && mine.State == "open" && mine.Countdown >= 0)
            {
                float end = Time.unscaledTime + (float)mine.Countdown;
                if (_cdEnd < 0f || Mathf.Abs(end - _cdEnd) > 0.4f)
                    _cdEnd = end;
            }
            else if (mine == null || mine.State == "open")
                StopCountdownHere();
            string me = MyId;
            if (mine == null || me == null)
                return;
            bool leader = mine.Id == me;
            // The server reopened the party (its match never came): not joining any more
            if (mine.State == "open" && _joining && !ZombiesMatch.Active)
                _joining = false;
            // READY follows the server (someone left: everyone's NOT READY again), unless a change of ours is on its way
            if (_pendingOp == null && mine.State == "open" && mine.Members.FirstOrDefault(m => m.Id == me) is Member self)
                _wantReady = self.Ready;
            // Members: the leader's raid is up, join it
            if (!leader && mine.State == "hosted" && !_joining && ZombiesNet.FikaJoin != null)
            {
                _joining = true;
                Expected = mine.Members.Count;
                string match = string.IsNullOrEmpty(mine.FikaMatch) ? mine.Id : mine.FikaMatch;
                ZombiesPlugin.Log.LogInfo($"Zombies party: {Name(mine.Id, mine)}'s raid is up on {mine.Map}: joining {(match == mine.Id ? "the leader's game" : "Fika headless " + match)}");
                ZombiesNet.FikaJoin(match, mine.Map);
            }
        }

        // ---------- EFT's map screen in zombies mode (only reached for SOLO, or by the host's game when hosting) ----------
        // READY is hidden (shrunk to nothing, so its own look is left alone); NEXT says PLAY SOLO and, pressed, marks the
        // raid as solo (Fika's START is then pressed by itself, no headless). BACK goes straight to the zombies menu.

        private static readonly Dictionary<Transform, Vector3> Shrunk = new Dictionary<Transform, Vector3>();
        private static EFT.UI.Matchmaker.MatchMakerSelectionLocationScreen _hooked;
        private static string _acceptKey;
        private static bool _renamed, _toRoot;
        private static float _toRootUntil;

        private static void TickMapScreen()
        {
            var screen = ZombiesMapSelect.Screen;
            EFT.UI.Screens.EEftScreenType? on = CurrentScreen;
            if (SoloScreen && on == EFT.UI.Screens.EEftScreenType.MainMenu)
                SoloScreen = false;
            // BACK from the zombies map screen: on through the side select to the main menu
            if (_toRoot)
            {
                if (on == EFT.UI.Screens.EEftScreenType.MainMenu || Time.time > _toRootUntil)
                    _toRoot = false;
                else if (on == EFT.UI.Screens.EEftScreenType.SelectRaidSide && Time.frameCount % 20 == 0)
                    EFT.UI.Screens.EftScreenManager.Instance?.TryReturnToRootScreen();
            }
            if (screen == null)
                return;
            bool zombies = ZombiesMapSelect.Zombies;
            Hide(screen._readyButton, zombies);
            try
            {
                if (_hooked != screen && screen._acceptButton != null)
                {
                    _hooked = screen;
                    screen._acceptButton.OnClick.AddListener(AcceptPressed);
                }
                if (screen._acceptButton != null)
                {
                    if (_acceptKey == null)
                        _acceptKey = screen._acceptButton.HeaderText;
                    if (zombies && !_renamed)
                    {
                        _renamed = true;
                        screen._acceptButton.SetRawText("PLAY SOLO", 28);
                    }
                    else if (!zombies && _renamed && !string.IsNullOrEmpty(_acceptKey))
                    {
                        _renamed = false;
                        screen._acceptButton.SetHeaderText(_acceptKey);
                    }
                }
            }
            catch (Exception e)
            {
                if (!_mapWarned)
                    ZombiesPlugin.Log.LogWarning($"Zombies party: the map screen's NEXT couldn't be set up ({e.Message})");
                _mapWarned = true;
            }
        }

        private static bool _mapWarned;

        // NEXT on the zombies map screen pressed by the player (the host's own press comes from TickGoal, Leading already)
        private static void AcceptPressed()
        {
            if (!ZombiesMapSelect.Zombies || Leading || !HasFika)
                return;
            if (_last?.Mine != null)
                Send(new Dictionary<string, object> { ["op"] = "leave", ["name"] = MyName });
            _last = null;
            _wantReady = false;
            Solo = true;
            Leading = true;
            _leadSince = Time.time;
            _hostPressed = false;
            _startedAt = Time.time;
            Expected = 1;
            SoloScreen = false;
            ZombiesPlugin.Log.LogInfo("Zombies party: playing solo");
        }

        private static void Hide(Component button, bool hide)
        {
            if (button == null)
                return;
            Transform t = button.transform;
            if (hide && !Shrunk.ContainsKey(t))
            {
                Shrunk[t] = t.localScale;
                t.localScale = Vector3.zero;
            }
            else if (!hide && Shrunk.TryGetValue(t, out Vector3 scale))
            {
                t.localScale = scale;
                Shrunk.Remove(t);
            }
        }

        private static void StartHosting(Party mine)
        {
            Leading = true;
            _leadSince = Time.time;
            _hostPressed = false;
            _startedAt = -1f;
            Expected = mine.Members.Count;
            _pendingOp = "start";
            _nextPoll = 0f;
            ZombiesPlugin.Log.LogInfo($"Zombies party: all {mine.Members.Count} ready on {mine.Map}: hosting");
            Go(Goal.Host, ZombiesMaps.Is(mine.Map) ? mine.Map : LobbyMap);
        }

        /// <summary>
        /// The match is up (the bridge's manager event): the members can join now. matchId null = the leader's own
        /// Fika server hosts (match id = the leader's profile id); else a Fika headless hosts under that id.
        /// </summary>
        public static void Hosted(string matchId)
        {
            if (!Leading || Solo)
                return;
            var op = new Dictionary<string, object> { ["op"] = "hosted", ["name"] = MyName };
            if (!string.IsNullOrEmpty(matchId))
                op["fikaMatch"] = matchId;
            Send(op);
            ZombiesPlugin.Log.LogInfo($"Zombies party: the raid is up ({(string.IsNullOrEmpty(matchId) ? "hosted by this game" : "hosted by Fika headless " + matchId)}), the party told to join");
        }

        /// <summary>The match has started (everyone's in): the party's done.</summary>
        public static void Done()
        {
            if (Leading && !Solo)
                Send(new Dictionary<string, object> { ["op"] = "done", ["name"] = MyName });
            Leading = false;
            Solo = false;
            _joining = false;
            _hostPressed = false;
            _wantReady = false;
            _goal = Goal.None;
            Expected = 0;
            _last = null;
        }

        public static string Name(string id, Party p) => p?.Members.FirstOrDefault(m => m.Id == id)?.Name ?? "the host";

        public static string MapName(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "no map yet";
            // Our maps by their layout's own name (Stronghold)
            if (ZombiesMaps.Is(id))
                return id.StartsWith("zm_") && id.Length > 3 ? char.ToUpperInvariant(id[3]) + id.Substring(4) : id;
            try
            {
                var loc = Location(id);
                string n = loc != null ? (loc.Id + " Name").Localized() : null;
                return string.IsNullOrEmpty(n) || n.EndsWith(" Name") ? id : n;
            }
            catch
            {
                return id;
            }
        }

        /// <summary>The map of the raid being deployed (EFT's own raid settings: solo, the host and joiners alike).</summary>
        public static string DeployMap
        {
            get
            {
                try
                {
                    object app = ClientAppUtils.GetMainApp();
                    var rs = app == null ? null : HarmonyLib.Traverse.Create(app).Field("_raidSettings").GetValue<EFT.RaidSettings>();
                    return rs?.SelectedLocation?.Id;
                }
                catch
                {
                    return null;
                }
            }
        }

        public static JsonType.LocationSettings.Location Location(string id) =>
            ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.LocationSettings?.locations?.Values.FirstOrDefault(l => l.Id == id);

        private static void Queue(Dictionary<string, object> op)
        {
            Ops.Enqueue(op);
            _nextPoll = 0f;
        }

        // ---------- START MATCH countdown: 5 s, a the original pre-match tick each second, the clock beep at zero ----------

        private static float _cdEnd = -1f;
        private static int _cdLast = -1;
        private static AudioSource _beeper;
        private static AudioClip _tick, _go;

        /// <summary>The zombies maps, in a steady order.</summary>
        public static List<string> MapIds => ZombiesMaps.All.Select(m => m.Map).Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id).ToList();

        private static float _lastPick = -10f;

        private static void PickMap(string id)
        {
            // At most every 1.5 s (if EFT ever refused a pick, it'd retry every frame)
            if (Time.unscaledTime - _lastPick < 1.5f)
                return;
            _lastPick = Time.unscaledTime;
            var screen = ZombiesMapSelect.Screen;
            var loc = Location(id);
            if (screen == null || loc == null)
                return;
            try
            {
                screen.OnLocationSelected(loc);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies party: couldn't pick {id} ({e.Message})");
            }
        }

        private static void StopCountdownHere()
        {
            _cdEnd = -1f;
            _cdLast = -1;
        }

        private static void CountdownTick()
        {
            if (!Counting)
                return;
            Party mine = _last?.Mine;
            if (mine == null || mine.State != "open")
            {
                StopCountdownHere();
                return;
            }
            float left = _cdEnd - Time.unscaledTime;
            int n = Mathf.CeilToInt(left);
            if (n == _cdLast)
                return;
            _cdLast = n;
            if (n >= 1 && n <= 5)
                Beep(ref _tick, "zm_countdown_beep");
            else if (n <= 0)
            {
                Beep(ref _go, "zm_countdown_go");
                StopCountdownHere();
                // The host's game hosts now; the others join once it's up
                if (mine.Id == MyId && !Leading)
                {
                    if (ZombiesMaps.Is(mine.Map) || ZombiesMaps.Is(LobbyMap))
                        StartHosting(mine);
                    else
                    {
                        _error = "Pick the map first.";
                        Queue(new Dictionary<string, object> { ["op"] = "cancelcountdown" });
                    }
                }
            }
        }

        private static void Beep(ref AudioClip clip, string name)
        {
            try
            {
                if (clip == null)
                    clip = ZombiesWav.Load(System.IO.Path.Combine(ZombiesPlugin.Dir, "sounds"), name);
                clip = ZombiesWav.Fresh(ref clip);
                if (clip == null)
                    return;
                if (_beeper == null)
                {
                    var go = new GameObject("KennerZombies PartyBeeper");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _beeper = go.AddComponent<AudioSource>();
                    _beeper.playOnAwake = false;
                    _beeper.spatialBlend = 0f;
                    _beeper.ignoreListenerPause = true;
                }
                _beeper.PlayOneShot(clip, ZombiesSounds.Volume(ZombiesSounds.Group.Purchases));
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies party: countdown sound {name} didn't play ({e.Message})");
            }
        }

        // ---------- BACK on the zombies map screen: straight to the zombies menu ----------

        public static void Patch(HarmonyLib.Harmony harmony)
        {
            var controller = typeof(EFT.UI.Matchmaker.MatchMakerSelectionLocationScreen.SelectionLocationScreenController);
            System.Reflection.MethodInfo close = HarmonyLib.AccessTools.Method(controller, "CloseScreen");
            if (close == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies party: no CloseScreen on the map screen (BACK goes to the side select)");
                return;
            }
            harmony.Patch(close, postfix: new HarmonyLib.HarmonyMethod(typeof(ZombiesParty), nameof(MapScreenClosed)));
        }

        private static void MapScreenClosed()
        {
            if (!ZombiesMapSelect.Zombies || Leading || _goal != Goal.None)
                return;
            SoloScreen = false;
            _toRoot = true;
            _toRootUntil = Time.time + 6f;
        }

        // Each invite by its own id (a NO hid every later invite from the same party: user, 2026-11-09)
        private static string Key(Invite inv) => string.IsNullOrEmpty(inv.Id) ? inv.PartyId : inv.Id;
    }
}
