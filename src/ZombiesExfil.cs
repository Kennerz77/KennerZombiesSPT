using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies exfil (the user, 2026-11-08), placed with F8:
    /// - "Exfil activator" (the lever): from round 15, any player throws it to start the exfil (free).
    /// - "Exfil hold area" (a box, the basement): the defend timer (90 s, F12) only runs while at least one player is
    ///   standing in it (up, not downed); empty, it pauses. Zombies keep coming the whole time.
    /// - Then the exfil opens for a window (30 s, F12) to get to the "Exfil zone" (a box; none placed: the ZB-013 exit).
    /// - At zero, everyone in the exfil zone leaves together (the raid ends Survived, +50% XP: "EXFIL BONUS" on the
    ///   report); everyone else gets the normal game over (no exfil).
    /// The host runs the timer and says when it's done (ZombiesNet Msg.Exfil); solo it's the player's own game. A map
    /// without an activator keeps the old rule (EFT's ZB-013 exit opens at round 15).
    /// </summary>
    internal static class ZombiesExfil
    {
        public enum Phase : byte { None, Locked, Ready, Holding, Open, Done, Closing }

        public static Phase Now { get; private set; }
        private static float _left;
        private static bool _paused;
        private static float _nextSend;
        private static Vector3? _activator;
        private static readonly List<MapEditor.Entry> Hold = new List<MapEditor.Entry>(), Zone = new List<MapEditor.Entry>();
        private static ConfigEntry<float> _holdSeconds, _windowSeconds, _bonus;

        /// <summary>You left by the exfil this match (the XP bonus goes on).</summary>
        public static bool Escaped { get; private set; }

        /// <summary>This map has an exfil activator (else the old ZB-013 rule).</summary>
        public static bool Present => _activator != null;

        public static float BonusFactor => _bonus?.Value ?? 0.5f;

        // ---------- The swarm (user, 2026-11-10: Cold War's exfil) ----------
        private static ConfigEntry<float> _drain, _swarmDelay, _perExtra, _solo;
        private static ConfigEntry<int> _swarmMax;
        private static ConfigEntry<bool> _swarmSprint;

        /// <summary>From the lever pull until the exfil closes: endless zombies (ZombiesRounds).</summary>
        public static bool Swarming => Present && (Now == Phase.Holding || Now == Phase.Open);
        public static float SwarmDelay => _swarmDelay?.Value ?? 0.5f;
        public static int SwarmMax => _swarmMax?.Value ?? 32;
        public static bool SwarmSprint => _swarmSprint?.Value ?? true;
        private static float HoldTotal => Mathf.Max(1f, _holdSeconds?.Value ?? 90f);
        private static float DrainRate => _drain?.Value ?? 2f;

        // How fast the bar fills right now: more holders faster, solo faster
        private static float FillRate(int holding)
        {
            float rate = 1f + Mathf.Max(0, holding - 1) * (_perExtra?.Value ?? 0.5f);
            if (Humans().Count(InMatch) <= 1)
                rate *= _solo?.Value ?? 1.5f;
            return rate;
        }

        /// <summary>The middle of the hold area (the swarm's zombies come at it).</summary>
        public static Vector3? HoldCentre
        {
            get
            {
                if (Hold.Count == 0)
                    return _activator;
                Vector3 c = Vector3.zero;
                foreach (MapEditor.Entry e in Hold)
                    c += MapEditor.Pos(e);
                return c / Hold.Count;
            }
        }

        public static void Bind(ConfigFile config)
        {
            const string s = "23. Exfil";
            _holdSeconds = config.Bind(s, "Hold the area (s)", 90f, new ConfigDescription(
                "How long at least one player has to stay in the exfil hold area (the basement) once the lever's thrown. Nobody in it: the timer waits.",
                new AcceptableValueRange<float>(10f, 600f)));
            _windowSeconds = config.Bind(s, "Time to get to the exfil (s)", 30f, new ConfigDescription(
                "Once the hold's done: how long everyone has to get into the exfil zone. At zero, those in it leave; the rest get the normal game over.",
                new AcceptableValueRange<float>(5f, 120f)));
            _bonus = config.Bind(s, "Exfil XP bonus", 0.5f, new ConfigDescription(
                "Extra match XP (and so Dr. K points) for getting out: 0.5 = +50%.", new AcceptableValueRange<float>(0f, 3f)));
            _perExtra = config.Bind(s, "Each extra player holding: fill x more", 0.5f, new ConfigDescription(
                "The bar fills faster with more players in the hold area: +this much speed for each player after the first.",
                new AcceptableValueRange<float>(0f, 2f)));
            _solo = config.Bind(s, "Solo: fill speed x", 1.5f, new ConfigDescription(
                "On your own the hold bar fills this much faster (solo's harder).", new AcceptableValueRange<float>(0.5f, 4f)));
            _drain = config.Bind(s, "Bar drains with nobody holding (x fill speed)", 2f, new ConfigDescription(
                "With nobody standing in the hold area, the hold bar empties at this much of its fill speed (0 = it just waits).",
                new AcceptableValueRange<float>(0f, 3f)));
            _swarmDelay = config.Bind(s, "Swarm: spawn delay x", 0.5f, new ConfigDescription(
                "From the lever pull until the exfil closes zombies never stop coming (the round can't end): the round's spawn delay times this.",
                new AcceptableValueRange<float>(0.1f, 1f)));
            _swarmMax = config.Bind(s, "Swarm: most zombies at once", 32, new ConfigDescription(
                "How many can be alive at once during the exfil swarm (normal rounds: 'Most zombies at once').", new AcceptableValueRange<int>(1, 60)));
            _swarmSprint = config.Bind(s, "Swarm: everyone sprints", true, new ConfigDescription(
                "Every zombie spawned during the exfil swarm sprints, whatever the round."));
        }

        /// <summary>The match is built: the map's exfil pieces read.</summary>
        public static void Build(IEnumerable<MapEditor.Entry> entries)
        {
            Clear();
            foreach (MapEditor.Entry e in entries)
            {
                if (e.Type == MapEditor.Kind.ExfilActivator && _activator == null)
                    _activator = MapEditor.Pos(e);
                else if (e.Type == MapEditor.Kind.ExfilHold)
                    Hold.Add(e);
                else if (e.Type == MapEditor.Kind.ExfilZone)
                    Zone.Add(e);
            }
            if (Present)
            {
                Now = IsExfilRound(ZombiesRounds.Round) ? Phase.Ready : Phase.Locked;
                if (Now == Phase.Ready)
                    ReadyAnnounce();
                ZombiesPlugin.Log.LogInfo($"Zombies exfil: activator at {_activator}, {Hold.Count} hold area(s), {Zone.Count} exfil zone(s)");
            }
        }

        public static void Clear()
        {
            Music(false);
            _readyUntil = -1f;
            ZombiesTeam.ClearExtracted();
            ZombiesGameOver.ExfilResult = null;
            ZombiesDowned.EndExfilSpectate();
            Extracted.Clear();
            ExtractingNow.Clear();
            _extracting = _extracted = false;
            _extractLeft = ExtractSeconds;
            _emptyFor = 0f;
            _voteEnds = -1f;
            _noVoteBefore = 0;
            _myVote = null;
            _askedVote = false;
            Votes.Clear();
            Now = Phase.None;
            _activator = null;
            Hold.Clear();
            Zone.Clear();
            Escaped = false;
            _left = 0f;
            _paused = false;
        }

        /// <summary>A round started (each game): from round 15 the lever can be thrown.</summary>
        // Exfil rounds: 15, then every 5th (user, 2026-11-10). The lever only works in one; a failed vote or a failed
        // hold waits for the next
        public const int ExfilEvery = 5;

        public static bool IsExfilRound(int r) => r >= ZombiesRounds.ExfilRound && (r - ZombiesRounds.ExfilRound) % ExfilEvery == 0;

        public static int NextExfilRound(int r)
        {
            if (r <= ZombiesRounds.ExfilRound)
                return ZombiesRounds.ExfilRound;
            int k = (r - ZombiesRounds.ExfilRound + ExfilEvery - 1) / ExfilEvery;
            return ZombiesRounds.ExfilRound + k * ExfilEvery;
        }

        public static void RoundStarted(int round)
        {
            bool exfilRound = IsExfilRound(round);
            if (Now == Phase.Locked && exfilRound)
            {
                Now = Phase.Ready;
                ReadyAnnounce();
            }
            else if (Now == Phase.Ready && !exfilRound)
            {
                Now = Phase.Locked;
                ZombiesPlugin.Notify($"The exfil window has passed: next at round {NextExfilRound(round)}.", false);
            }
        }

        // ---------- Areas ----------

        // Inside a placed box (it stands on its spot, turned like its entry)
        private static bool In(MapEditor.Entry e, Vector3 p)
        {
            if (e?.Size == null || e.Size.Length < 3)
                return false;
            Vector3 local = Quaternion.Inverse(MapEditor.Rotation(e)) * (p - MapEditor.Pos(e));
            return Mathf.Abs(local.x) <= e.Size[0] * 0.5f && Mathf.Abs(local.z) <= e.Size[2] * 0.5f && local.y >= -0.5f && local.y <= e.Size[1];
        }

        public static bool InHold(Vector3 p) => Hold.Count > 0 ? Hold.Any(e => In(e, p)) : _activator.HasValue && Vector3.Distance(p, _activator.Value) < 12f;

        public static bool InZone(Vector3 p)
        {
            if (Zone.Count > 0)
                return Zone.Any(e => In(e, p));
            // None placed: EFT's ZB-013 exit (its own trigger), else close to the lever
            foreach (var point in UnityEngine.Object.FindObjectsOfType<EFT.Interactive.ExfiltrationPoint>())
            {
                string n = ((point.Settings?.Name ?? "") + point.name).ToLowerInvariant().Replace("-", "").Replace("_", "");
                if (!n.Contains("zb013") && !n.Contains("zb1013"))
                    continue;
                foreach (Collider c in point.GetComponentsInChildren<Collider>())
                {
                    Bounds b = c.bounds;
                    b.Expand(1f);
                    if (b.Contains(p))
                        return true;
                }
            }
            return _activator.HasValue && Vector3.Distance(p, _activator.Value) < 6f;
        }

        // Players standing (up) and those still in the match (not bled out)
        private static bool IsUp(Player p) => p.IsYourPlayer ? ZombiesDowned.Mine == ZombiesDowned.State.Up : ZombiesNet.StateOf(p.ProfileId) == ZombiesNet.State.Up;
        private static bool InMatch(Player p) => p.IsYourPlayer ? ZombiesDowned.Mine != ZombiesDowned.State.Dead : ZombiesNet.StateOf(p.ProfileId) != ZombiesNet.State.Dead;
        private static IEnumerable<Player> Humans() => ZombiesTeam.List().Where(p => p != null && ZombiesTeam.IsHuman(p) && p.HealthController != null && p.HealthController.IsAlive);

        // ---------- The lever ----------

        /// <summary>
        /// Thrown at the lever (this player's game). Solo: the exfil starts. A team: a vote first (user, 2026-11-09):
        /// every player still in the match has VoteSeconds to vote yes (F1) or no (F2); all yes and it starts, a no or
        /// a missing vote and it doesn't, and the lever stays shut until the next round. The thrower votes yes.
        /// </summary>
        public static void Start()
        {
            bool refused = _noVoteBefore > 0 && _noVoteBefore >= ZombiesRounds.Round;
            if (Now != Phase.Ready || Voting || refused)
            {
                if (refused)
                    ZombiesPlugin.Notify("The team said no to the exfil this round.", true);
                return;
            }
            if (!ZombiesNet.Online || Humans().Count(InMatch) <= 1)
            {
                Begin();
                return;
            }
            if (ZombiesNet.IsHost)
                OpenVote(Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId);
            else
            {
                _askedVote = true;
                ZombiesNet.ExfilVote(0);
            }
        }

        // The exfil starts (solo, or the vote passed: on the host)
        private static void Begin()
        {
            ZombiesPlugin.Log.LogInfo("Zombies exfil: the lever's thrown, hold the area");
            Set(Phase.Holding, _holdSeconds?.Value ?? 90f, false, null);
            ZombiesNet.Exfil((byte)Now, _left, false, null);
        }

        // ---------- The team's vote ----------

        private const float VoteSeconds = 20f;
        private static readonly Dictionary<string, bool> Votes = new Dictionary<string, bool>();
        private static float _voteEnds = -1f, _nextVoteSend;
        private static int _yes, _needed;
        private static int _noVoteBefore;   // the round a vote failed in: no new one before the next round
        private static bool? _myVote;

        /// <summary>A vote is open (this game's view).</summary>
        public static bool Voting => _voteEnds > 0f;

        private static float VoteLeft => Mathf.Max(0f, _voteEnds - Time.time);

        // The host opens it (the thrower's yes counted)
        private static void OpenVote(string by)
        {
            if (Voting || Now != Phase.Ready)
                return;
            Votes.Clear();
            _myVote = null;
            _voteEnds = Time.time + VoteSeconds;
            if (!string.IsNullOrEmpty(by))
                Votes[by] = true;
            if (by == Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId)
                _myVote = true;
            ZombiesPlugin.Log.LogInfo($"Zombies exfil: vote opened by {by}");
            ZombiesSounds.Head("zm_purchase", 0.8f);
            CheckVote();
            if (Voting)
                SendVoteState(by);
        }

        /// <summary>This player's F1 / F2.</summary>
        public static void Vote(bool yes)
        {
            if (!Voting || _myVote.HasValue)
                return;
            _myVote = yes;
            ZombiesSounds.Head("zm_purchase", 0.8f);
            if (ZombiesNet.IsHost)
                Cast(Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId, yes);
            else
                ZombiesNet.ExfilVote(yes ? (byte)1 : (byte)2);
        }

        // The host counts a vote
        private static void Cast(string who, bool yes)
        {
            if (!Voting || string.IsNullOrEmpty(who))
                return;
            Votes[who] = yes;
            ZombiesPlugin.Log.LogInfo($"Zombies exfil: {who} votes {(yes ? "yes" : "no")}");
            CheckVote();
            if (Voting)
                SendVoteState(null);
        }

        private static List<string> Voters() => Humans().Where(InMatch).Select(p => p.ProfileId).ToList();

        // The host: everyone yes -> the exfil; any no or time up -> not this round
        private static void CheckVote()
        {
            List<string> voters = Voters();
            _needed = voters.Count;
            _yes = voters.Count(v => Votes.TryGetValue(v, out bool y) && y);
            bool anyNo = voters.Any(v => Votes.TryGetValue(v, out bool y) && !y);
            if (_needed > 0 && _yes >= _needed)
            {
                CloseVote(true);
                ZombiesNet.ExfilVote(4);
                Begin();
            }
            else if (anyNo || VoteLeft <= 0f)
            {
                CloseVote(false);
                ZombiesNet.ExfilVote(5);
            }
        }

        // State to everyone; the a/b numbers are yes/needed. The thrower's id rides in the "who" state as their yes
        private static void SendVoteState(string thrower)
        {
            ZombiesNet.ExfilVote(3, _yes, Mathf.Max(1, _needed), VoteLeft);
            if (!string.IsNullOrEmpty(thrower))
                _thrower = thrower;
        }

        private static string _thrower;

        private static void CloseVote(bool passed)
        {
            _voteEnds = -1f;
            _myVote = null;
            Votes.Clear();
            if (!passed)
                _noVoteBefore = ZombiesRounds.Round;
            ZombiesPlugin.Notify(passed ? "Everyone voted yes: exfil called!" : "Exfil vote failed: not this round.", !passed);
            ZombiesPlugin.Log.LogInfo($"Zombies exfil: vote {(passed ? "passed" : "failed")}");
        }

        /// <summary>A vote message (ZombiesNet).</summary>
        public static void VoteMessage(string from, byte kind, int a, int b, float left)
        {
            switch (kind)
            {
                case 0:
                    if (ZombiesNet.IsHost)
                        OpenVote(from);
                    break;
                case 1:
                case 2:
                    if (ZombiesNet.IsHost)
                        Cast(from, kind == 1);
                    break;
                case 3:
                    if (ZombiesNet.IsHost)
                        break;
                    bool opening = !Voting;
                    _yes = a;
                    _needed = b;
                    _voteEnds = Time.time + left;
                    if (opening)
                    {
                        // The one who threw the lever (asked here) has already said yes
                        _myVote = _askedVote ? true : (bool?)null;
                        _askedVote = false;
                        ZombiesSounds.Head("zm_purchase", 0.8f);
                    }
                    break;
                case 4:
                case 5:
                    if (!ZombiesNet.IsHost)
                        CloseVote(kind == 4);
                    break;
                case 6:
                    if (ZombiesNet.IsHost)
                    {
                        if (a == 1)
                            ExtractingNow.Add(from);
                        else
                            ExtractingNow.Remove(from);
                    }
                    break;
                case 7:
                    MarkOut(from);
                    Player who = ZombiesTeam.List().FirstOrDefault(p => p != null && p.ProfileId == from)
                                 ?? Singleton<GameWorld>.Instance?.AllAlivePlayersList.FirstOrDefault(p => p != null && p.ProfileId == from);
                    if (who != null)
                    {
                        ZombiesTeam.MarkExtracted(who);
                        ZombiesPlugin.Notify($"{who.Profile?.Nickname} got out!", false);
                    }
                    break;
            }
        }

        private static bool _askedVote;

        // The host: time runs out, players leave; state to everyone once a second
        private static void TickVote()
        {
            if (!Voting)
                return;
            if (!ZombiesNet.IsHost)
            {
                // The host's word closes it; a lost one doesn't leave it up forever
                if (Time.time > _voteEnds + 3f)
                    _voteEnds = -1f;
                return;
            }
            CheckVote();
            if (Voting && Time.time >= _nextVoteSend)
            {
                _nextVoteSend = Time.time + 1f;
                SendVoteState(null);
            }
        }

        // ---------- Each frame ----------

        public static void Tick()
        {
            if (ZombiesMatch.Active)
            {
                TickVote();
                TickExtract();
            }
            if (!ZombiesMatch.Active || Now == Phase.None || Now == Phase.Locked || Now == Phase.Ready || Now == Phase.Done)
                return;
            float dt = Time.deltaTime;
            if (!ZombiesNet.IsHost)
            {
                // A client counts down between the host's word (smooth on screen); the host decides the end
                if (Now == Phase.Open || Now == Phase.Closing)
                    _left = Mathf.Max(0f, _left - dt);
                else if (Now == Phase.Holding && !_paused)
                    _left = Mathf.Max(0f, _left - dt * FillRate(Mathf.Max(1, Humans().Count(p => IsUp(p) && InHold(p.Transform.position)))));
                else if (Now == Phase.Holding)
                    _left = Mathf.Min(HoldTotal, _left + dt * DrainRate);
                return;
            }
            if (Now == Phase.Holding)
            {
                int holding = Humans().Count(p => IsUp(p) && InHold(p.Transform.position));
                _paused = holding == 0;
                // Option B (user, 2026-11-10): the bar fills while held and slowly empties with nobody in it
                if (!_paused)
                    _left -= dt * FillRate(holding);
                else
                    _left = Mathf.Min(HoldTotal, _left + dt * DrainRate);
                // Empty bar and nobody holding for a while: the exfil fails (user, 2026-11-10)
                _emptyFor = _paused && Progress <= 0.001f ? _emptyFor + dt : 0f;
                if (_emptyFor >= FailAfter)
                {
                    Fail();
                    return;
                }
                if (_left <= 0f)
                {
                    Set(Phase.Open, _windowSeconds?.Value ?? 30f, false, null);
                    ZombiesNet.Exfil((byte)Now, _left, false, null);
                    return;
                }
            }
            else if (Now == Phase.Open || Now == Phase.Closing)
            {
                _left -= dt;
                // Everyone still in the match is out: it's over at once
                bool allOut = Extracted.Count > 0 && !Humans().Any(InMatch);
                // The window shut: anyone already extracting gets to finish (up to ExtractSeconds), nobody new starts
                if (Now == Phase.Open && _left <= 0f && !allOut)
                {
                    Set(Phase.Closing, ExtractSeconds, false, null);
                    ZombiesNet.Exfil((byte)Now, _left, false, null);
                    return;
                }
                if (allOut || Now == Phase.Closing && (_left <= 0f || ExtractingNow.Count == 0))
                {
                    List<string> out_ = Extracted.ToList();
                    ZombiesNet.Exfil((byte)Phase.Done, 0f, false, out_);
                    Set(Phase.Done, 0f, false, out_);
                    return;
                }
            }
            if (Time.time >= _nextSend)
            {
                _nextSend = Time.time + 1f;
                ZombiesNet.Exfil((byte)Now, _left, _paused, null);
            }
        }

        /// <summary>The host's word (or ours): the phase, time left, and at the end who's leaving.</summary>
        public static void Set(Phase phase, float left, bool paused, List<string> leaving)
        {
            if (Now == Phase.Done || !Present)
                return;
            Phase was = Now;
            Now = phase;
            _left = left;
            _paused = paused;
            if (phase == was)
                return;
            switch (phase)
            {
                case Phase.Holding:
                    ZombiesPlugin.Notify("Exfil called: hold the basement!");
                    Music(true);
                    break;
                case Phase.Open:
                    ZombiesPlugin.Notify("The exfil is open: get in!");
                    // Only Kino's teleporter as the way out opens (user, 2026-11-11: no round end sting with it)
                    ZombiesSounds.Head("zm_exfil_teleport", 1f);
                    break;
                case Phase.Closing:
                    ZombiesPlugin.Notify("The exfil is closing!");
                    break;
                case Phase.Locked:
                    if (was == Phase.Holding)
                        Failed();
                    break;
                case Phase.Done:
                    End(leaving ?? new List<string>());
                    break;
            }
        }

        // "You win" / "you lose" first, then the game over cinematic (user, 2026-11-10)
        private static IEnumerator StingThenGameOver(Player me, bool won)
        {
            string sting = won ? "zm_exfil_win" : "zm_exfil_lose";
            ZombiesSounds.Head(sting, 1f);
            AudioClip c = ZombiesSounds.Clip(sting);
            yield return new WaitForSecondsRealtime(Mathf.Clamp(c != null ? c.length * 0.6f : 3f, 2f, 8f));
            if (me != null)
                ZombiesGameOver.Begin(me, won ? "exfiled" : "left behind at the exfil");
        }

        // ---------- Ready this round: banner and Kino's teleporter (user, 2026-11-10) ----------
        private static float _readyUntil = -1f;

        private static void ReadyAnnounce()
        {
            _readyUntil = Time.time + 6f;
            ZombiesSounds.Head("zm_exfil_teleport", 1f);
            ZombiesPlugin.Log.LogInfo("Zombies exfil: this round can call the exfil");
        }

        // ---------- The exfil's music, looping while it's on ----------
        private static AudioSource _music;

        private static void Music(bool on)
        {
            if (ZombiesNet.Headless)
                return;
            if (!on)
            {
                if (_music != null)
                    ZombiesPlugin.Instance.StartCoroutine(FadeOut(_music));
                _music = null;
                return;
            }
            if (_music != null)
                return;
            // The easter egg song goes first (user, 2026-11-10): the exfil's music comes in once it's over
            if (ZombiesRock.SongPlaying)
            {
                if (!_musicWaiting)
                {
                    ZombiesPlugin.Log.LogInfo("Zombies exfil: the easter egg song is playing: the exfil music waits for it");
                    ZombiesPlugin.Instance.StartCoroutine(MusicAfterSong());
                }
                return;
            }
            AudioClip clip = ZombiesSounds.Clip("zm_exfil_music");
            if (clip == null)
                return;
            var go = new GameObject("Zombies_ExfilMusic");
            _music = go.AddComponent<AudioSource>();
            _music.clip = clip;
            _music.loop = true;
            ZombiesHeadAudio.Setup(_music, 6);
            ZombiesSounds.Follow(_music, ZombiesSounds.Group.Rounds);
            _music.Play();
        }

        private static bool _musicWaiting;

        private static IEnumerator MusicAfterSong()
        {
            _musicWaiting = true;
            while (ZombiesRock.SongPlaying && Swarming)
                yield return new WaitForSeconds(0.5f);
            _musicWaiting = false;
            ZombiesPlugin.Log.LogInfo($"Zombies exfil: the easter egg song is over: exfil music {(Swarming ? "starts now" : "not needed (the exfil is over)")}");
            if (Swarming)
                Music(true);
        }

        private static IEnumerator FadeOut(AudioSource s)
        {
            float from = s != null ? s.volume : 0f;
            for (float t = 0f; t < 1.5f && s != null; t += Time.unscaledDeltaTime)
            {
                s.volume = from * (1f - t / 1.5f);
                yield return null;
            }
            if (s != null)
                UnityEngine.Object.Destroy(s.gameObject);
        }

        // ---------- A failed hold ----------

        private const float FailAfter = 15f;
        private static float _emptyFor;

        // The host: the bar sat empty with nobody holding: the exfil's off until the next exfil round, the swarm's
        // extra zombies leave and the round goes back to its own count
        private static void Fail()
        {
            ZombiesPlugin.Log.LogInfo("Zombies exfil: nobody held the area, the exfil failed");
            Set(Phase.Locked, 0f, false, null);
            ZombiesNet.Exfil((byte)Now, 0f, false, null);
        }

        private static void Failed()
        {
            _emptyFor = 0f;
            ZombiesPlugin.Notify($"EXFIL FAILED: next chance at round {NextExfilRound(ZombiesRounds.Round + 1)}");
            Music(false);
            ZombiesSounds.Head("zm_exfil_lose", 1f);
            if (ZombiesNet.IsHost)
                ZombiesRounds.EndSwarm();
        }

        // ---------- Getting out: stand in the exfil zone for ExtractSeconds (user, 2026-11-10) ----------

        public const float ExtractSeconds = 5f;
        private static float _extractLeft = ExtractSeconds;
        private static bool _extracting, _extracted;
        /// <summary>Everyone who got out (every game).</summary>
        public static readonly HashSet<string> Extracted = new HashSet<string>();
        // The host: who's extracting right now (the window's closing waits for them)
        private static readonly HashSet<string> ExtractingNow = new HashSet<string>();

        /// <summary>Seconds left of our own extract while we stand in the zone (for the HUD), or -1.</summary>
        public static float MyExtractLeft => _extracting ? _extractLeft : -1f;

        private static void TickExtract()
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null || ZombiesNet.Headless || _extracted)
                return;
            bool may = Now == Phase.Open || Now == Phase.Closing && _extracting;
            bool inZone = may && IsUp(me) && InZone(me.Transform.position);
            if (inZone)
            {
                if (!_extracting)
                {
                    _extracting = true;
                    _extractLeft = ExtractSeconds;
                    ExtractingSays(me.ProfileId, true);
                }
                _extractLeft -= Time.deltaTime;
                if (_extractLeft <= 0f)
                    ExtractMe(me);
            }
            else if (_extracting)
            {
                _extracting = false;
                _extractLeft = ExtractSeconds;
                ExtractingSays(me.ProfileId, false);
            }
        }

        private static void ExtractingSays(string id, bool on)
        {
            if (ZombiesNet.IsHost)
            {
                if (on)
                    ExtractingNow.Add(id);
                else
                    ExtractingNow.Remove(id);
            }
            else
                ZombiesNet.ExfilVote(6, on ? 1 : 0);
        }

        private static void ExtractMe(Player me)
        {
            _extracted = true;
            _extracting = false;
            Escaped = true;
            ZombiesPlugin.Log.LogInfo("Zombies exfil: you got out");
            ZombiesPlugin.Notify("SUCCESSFULLY EXFILED");
            ZombiesSounds.Head("zm_purchase", 1f);
            if (ZombiesNet.IsHost)
                MarkOut(me.ProfileId);
            else
                ZombiesNet.ExfilVote(7);
            ZombiesTeam.MarkExtracted(me);
            ZombiesDowned.SpectateAfterExfil(me);
        }

        private static void MarkOut(string id)
        {
            Extracted.Add(id);
            ExtractingNow.Remove(id);
        }

        // The window's over: in the exfil zone you leave with the bonus, anywhere else it's the game over
        private static void End(List<string> leaving)
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null)
                return;
            bool out_ = leaving.Contains(me.ProfileId) || _extracted;
            ZombiesPlugin.Log.LogInfo($"Zombies exfil: done, {leaving.Count} player(s) out; you {(out_ ? "made it" : "were left behind")}");
            // Everyone sees the game over cinematic, with their own result on it (user, 2026-11-10); the raid ends as
            // survived for those out (ZombiesGameOver.StopRaid)
            Escaped = out_;
            ZombiesGameOver.ExfilResult = out_;
            Music(false);
            ZombiesPlugin.Instance.StartCoroutine(StingThenGameOver(me, out_));
        }

        private static IEnumerator Leave(Player me)
        {
            Escaped = true;
            ZombiesPlugin.Notify("EXFILTRATED");
            ZombiesReport.Keep(ZombiesRounds.Round);
            yield return new WaitForSecondsRealtime(2f);
            try
            {
                object game = Singleton<AbstractGame>.Instance;
                var stop = game == null ? null : HarmonyLib.AccessTools.Method(game.GetType(), "Stop", new[] { typeof(string), typeof(ExitStatus), typeof(string), typeof(float) });
                if (stop == null)
                {
                    ZombiesPlugin.Log.LogWarning("Zombies exfil: no game Stop to end the raid with");
                    yield break;
                }
                stop.Invoke(game, new object[] { me.ProfileId, ExitStatus.Survived, "ZB-013", 0f });
                ZombiesPlugin.Log.LogInfo("Zombies exfil: raid ended (survived)");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies exfil: ending the raid failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // ---------- On screen ----------

        /// <summary>The exits panel's timer text for ZB-013 (null: leave it to the old rule).</summary>
        public static string PanelText()
        {
            if (!Present)
                return null;
            switch (Now)
            {
                case Phase.Locked: return $"Round {NextExfilRound(ZombiesRounds.Round + 1)}";
                case Phase.Ready: return "Lever";
                case Phase.Holding: return $"Hold {Mathf.RoundToInt(Progress * 100f)}%{(_paused ? " (empty)" : "")}";
                case Phase.Open: return $"Open {Clock(_left)}";
                case Phase.Closing: return "Closing";
                default: return "Closed";
            }
        }

        private static string Clock(float s)
        {
            int t = Mathf.CeilToInt(Mathf.Max(0f, s));
            return $"{t / 60}:{t % 60:00}";
        }

        private static GUIStyle _style;

        public static void Draw()
        {
            if (!ZombiesMatch.Active || Event.current.type != EventType.Repaint)
                return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            DrawMarker();
            if (Now == Phase.Ready && Time.time < _readyUntil)
            {
                float k = Screen.height / 1080f;
                float a = Mathf.Clamp01((_readyUntil - Time.time) / 1f);
                _style.fontSize = Mathf.RoundToInt(48f * k);
                bool flash = Mathf.Repeat(Time.time, 0.6f) < 0.4f;
                ZombiesHud.Banner(0.13f, "EXFIL READY THIS ROUND!", new Color(0.3f, 1f, 0.35f), k, 40f, a * (flash ? 1f : 0.75f));
            }
            if (Voting)
                DrawVote();
            if (Now == Phase.Holding)
                DrawHoldBar();
            else if (Now == Phase.Open || Now == Phase.Closing)
            {
                float k2 = Screen.height / 1080f;
                // Out already (watching the others): still the time they have left (user, 2026-11-11)
                string head = _extracted ? (Now == Phase.Open ? $"YOU GOT OUT  -  EXFIL CLOSES IN {Clock(_left)}" : "YOU GOT OUT")
                    : Now == Phase.Closing ? "EXFIL CLOSING" : $"EXFIL OPEN: GET IN  {Clock(_left)}";
                ZombiesHud.Banner(0.11f, head, _extracted ? new Color(0.3f, 1f, 0.35f) : new Color(1f, 0.3f, 0.25f), k2, 28f);
                if (_extracting)
                    ZombiesHud.Banner(0.17f, $"EXFILTRATING {Mathf.CeilToInt(_extractLeft)}", new Color(0.3f, 1f, 0.35f), k2, 44f);
            }
        }

        // ---------- The exfil symbol (user, 2026-11-10): on screen over everything, like the revive icon ----------
        // Callable this round: over the lever. Open (and closing): over the exfil zone, where to go. Fades out as you get
        // close (gone within 3 m, full from 15 m), a little smaller far off.
        private static Texture2D _icon;
        private static bool _iconTried;

        private static Vector3? MarkerSpot()
        {
            if (Now == Phase.Ready)
                return _activator.HasValue ? _activator.Value + Vector3.up * 1.6f : (Vector3?)null;
            if ((Now == Phase.Open || Now == Phase.Closing) && !_extracted)
            {
                if (Zone.Count > 0)
                {
                    Vector3 c = Vector3.zero;
                    foreach (MapEditor.Entry e in Zone)
                        c += MapEditor.Pos(e);
                    return c / Zone.Count + Vector3.up * 1.6f;
                }
                return _activator.HasValue ? _activator.Value + Vector3.up * 1.6f : (Vector3?)null;
            }
            return null;
        }

        private static void DrawMarker()
        {
            Vector3? spot = MarkerSpot();
            Camera cam = Camera.main;
            if (!spot.HasValue || cam == null)
                return;
            if (!_iconTried)
            {
                _iconTried = true;
                _icon = ZombiesDowned.LoadPng("zm_exfil_icon.png");
            }
            if (_icon == null)
                return;
            Vector3 sp = cam.WorldToScreenPoint(spot.Value);
            if (sp.z <= 0f)
                return;
            float fade = Mathf.InverseLerp(3f, 15f, sp.z);
            if (fade <= 0.01f)
                return;
            float k = Screen.height / 1080f;
            float size = Mathf.Clamp(110f * k * 14f / Mathf.Max(4f, sp.z), 48f * k, 110f * k);
            float w = size * _icon.width / Mathf.Max(1f, _icon.height);
            Color was = GUI.color;
            // Open: pulses gently, the way to go
            float pulse = Now == Phase.Ready ? 1f : 0.8f + 0.2f * Mathf.Sin(Time.time * 5f);
            GUI.color = new Color(1f, 1f, 1f, fade * pulse);
            GUI.DrawTexture(new Rect(sp.x - w * 0.5f, Screen.height - sp.y - size * 0.5f, w, size), _icon);
            _style.fontSize = Mathf.RoundToInt(16f * k);
            GUI.color = was;
            Color text = Now == Phase.Ready ? new Color(1f, 0.9f, 0.6f, fade) : new Color(0.3f, 1f, 0.35f, fade * pulse);
            Shadowed(new Rect(sp.x - 120f * k, Screen.height - sp.y + size * 0.55f, 240f * k, 22f * k), $"{Mathf.RoundToInt(sp.z)} m", text);
        }

        /// <summary>How full the hold bar is (0-1).</summary>
        public static float Progress => Mathf.Clamp01(1f - _left / HoldTotal);

        private static Texture2D _white;

        private static Texture2D White()
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            return _white;
        }

        private static void Box(Rect r, Color c)
        {
            Color was = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, White());
            GUI.color = was;
        }

        private static void Shadowed(Rect r, string text, Color colour)
        {
            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, _style);
            GUI.color = colour;
            GUI.Label(r, text, _style);
            GUI.color = was;
        }

        // HOLD THE AREA bar, top middle: fills while someone stands in the hold area, red and draining with nobody in it
        private static void DrawHoldBar()
        {
            float s = Screen.height / 1080f;
            int inside = Humans().Count(p => IsUp(p) && InHold(p.Transform.position));
            bool empty = _paused;
            bool flash = empty && Mathf.Repeat(Time.time, 0.8f) < 0.4f;
            float w = 600f * s, h = 28f * s, x = (Screen.width - w) / 2f, y = Screen.height * 0.12f + 30f * s;
            string title = empty ? "NOBODY HOLDING!" : $"HOLD THE AREA  ({inside} in)";
            Color titleColour = empty ? (flash ? new Color(1f, 0.15f, 0.1f) : new Color(0.7f, 0.1f, 0.08f)) : new Color(1f, 0.9f, 0.6f);
            ZombiesHud.Banner(0.12f - 34f * s / Screen.height, title, titleColour, s, 26f);
            ZombiesHud.Bar(new Rect(x, y, w, h), Progress, empty ? new Color(0.85f, 0.12f, 0.08f) : ZombiesHud.Gold, $"{Mathf.RoundToInt(Progress * 100f)}%", s);
        }

        // The vote pop-up, top middle like the party invite (black box, thin grey edge); keys from ZOMBIES CONTROLS
        private static void DrawVote()
        {
            float s = Screen.height / 1080f;
            float w = 640f * s, h = 156f * s, x = (Screen.width - w) / 2f, y = 110f * s;
            ZombiesHud.Window(new Rect(x, y, w, h), s);
            _style.fontSize = Mathf.RoundToInt(30f * s);
            Shadowed(new Rect(x, y + 12f * s, w, 36f * s), "EXFIL VOTE", new Color(0.85f, 0.1f, 0.08f));
            _style.fontSize = Mathf.RoundToInt(22f * s);
            Shadowed(new Rect(x, y + 52f * s, w, 30f * s), $"Leave through the basement?  {_yes}/{Mathf.Max(1, _needed)} yes  ({Mathf.CeilToInt(VoteLeft)} s)", Color.white);
            string mine = _myVote.HasValue ? (_myVote.Value ? "You voted YES" : "You voted NO")
                : $"[{ZombiesControls.KeyName(ZombiesControls.Act.VoteYes)}] YES        [{ZombiesControls.KeyName(ZombiesControls.Act.VoteNo)}] NO";
            Shadowed(new Rect(x, y + 96f * s, w, 34f * s), mine, new Color(1f, 0.9f, 0.6f));
            // time left as a thin bar along the bottom
            Box(new Rect(x + 12f * s, y + h - 9f * s, (w - 24f * s) * Mathf.Clamp01(VoteLeft / VoteSeconds), 4f * s), new Color(0.85f, 0.1f, 0.08f));
        }
    }

    /// <summary>The exfil's own each-frame and on-screen (on the plugin object).</summary>
    internal class ZombiesExfilHud : MonoBehaviour
    {
        private void Update() => ZombiesExfil.Tick();
        private void OnGUI() => ZombiesExfil.Draw();
    }

    /// <summary>The exfil lever (F8 "Exfil activator"): Kino's power switch model, thrown to call the exfil.</summary>
    internal class ExfilLever : MonoBehaviour
    {
        private const float InteractDistance = 2.5f, PullTime = 1.67f;
        private readonly ZombiesInteraction _menu = new ZombiesInteraction();
        private Animator _animator;
        private bool _thrown;

        private void Start()
        {
            _animator = GetComponentInChildren<Animator>(true);
            _animator?.Play("off");
        }

        private void Update()
        {
            ZombiesExfil.Phase now = ZombiesExfil.Now;
            if (!_thrown && now != ZombiesExfil.Phase.Locked && now != ZombiesExfil.Phase.Ready)
                Pull(); // thrown by a teammate (or the host's word): the lever goes over here too
            // The exfil failed (back to locked or ready): the lever flips back off
            if (_thrown && (now == ZombiesExfil.Phase.Locked || now == ZombiesExfil.Phase.Ready))
            {
                _thrown = false;
                StartCoroutine(BackOff());
            }
            if (_thrown || now != ZombiesExfil.Phase.Locked && now != ZombiesExfil.Phase.Ready)
            {
                _menu.Reset();
                return;
            }
            if (ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
        }

        private void OnDestroy() => _menu.Reset();

        private IEnumerable<(string, Action)> Options()
        {
            if (ZombiesExfil.Now == ZombiesExfil.Phase.Locked)
                yield return ($"Exfil (round {ZombiesExfil.NextExfilRound(ZombiesRounds.Round + (ZombiesExfil.IsExfilRound(ZombiesRounds.Round) ? 0 : 1))})", (Action)ZombiesMatch.Deny);
            else
                yield return ("Call the exfil (hold the basement)", (Action)Throw);
        }

        private void Throw()
        {
            if (_thrown)
                return;
            ZombiesExfil.Start();
            Pull();
        }

        private void Pull()
        {
            if (_thrown)
                return;
            _thrown = true;
            _menu.Reset();
            StartCoroutine(PullAnim());
        }

        private IEnumerator BackOff()
        {
            ZombiesSounds.At("zm_exfil_lever", transform.position, 1f, 30f);
            if (_animator != null)
            {
                _animator.Play("pull", 0, 1f);
                _animator.speed = -1f;
                yield return new WaitForSeconds(PullTime);
                _animator.speed = 1f;
                _animator.Play("off");
            }
        }

        private IEnumerator PullAnim()
        {
            // The original's switch flip (not the power-on): the exfil and its music start with it
            ZombiesSounds.At("zm_exfil_lever", transform.position, 1f, 30f);
            if (_animator != null)
            {
                _animator.Play("pull", 0, 0f);
                yield return new WaitForSeconds(PullTime);
                _animator.Play("on");
            }
        }

        private void Sound(string name)
        {
            AudioSource s = GetComponentsInChildren<AudioSource>(true).FirstOrDefault(a => a.name == name);
            if (s == null)
                return;
            s.enabled = true;
            s.volume = Mathf.Min(s.volume, 1f) * ZombiesSounds.Volume(ZombiesSounds.Group.Power);
            s.Play();
        }
    }
}
