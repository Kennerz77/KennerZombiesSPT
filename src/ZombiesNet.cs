using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies match shared between players, host-authoritative. This mod never touches Fika: the separate Fika
    /// add-on (Kennerz_ZM_CoreFika.dll, loaded only when Fika is installed) plugs in here: it sets <see cref="Online"/>
    /// and <see cref="IsHost"/>, carries the bytes (<see cref="Sender"/> / <see cref="Receive"/>) and turns items into
    /// bytes and back. Without it (solo, no Fika) nothing is sent and every check reads "host".
    /// The host runs the zombies, rounds, barricade boards and power-up drops and tells everyone; a client asks the host
    /// (a door bought, a board rebuilt, a power-up touched) and the host passes it on to all. Each player keeps their own
    /// points, perks, ammo and downs, and tells the others what they need to see (points, down/up, items made).
    /// </summary>
    public static class ZombiesNet
    {
        /// <summary>In a Fika raid (the add-on sets it).</summary>
        public static bool Online { get; set; }

        /// <summary>The Fika host, or solo.</summary>
        public static bool IsHost { get; set; } = true;

        /// <summary>The add-on's send: a client's goes to the host, the host's to every client.</summary>
        public static Action<byte[]> Sender { get; set; }

        /// <summary>The add-on's item (de)serialiser (EFT's binary item format, written by Fika's writer).</summary>
        public static Func<Item, byte[]> ItemToBytes { get; set; }
        public static Func<byte[], Item> BytesToItem { get; set; }

        private enum Msg : byte
        {
            Hello = 1, State, Points, Round, Open, Power, Boards, Rebuild, BoxStart, BoxSpin, BoxTaken,
            Down, Up, Dead, Revive, Slap, PuDrop, PuGrab, PuTake, Web, ItemAdd, ItemRemove, Kill, Rock, Pause, Lobby, Vox, Restore, Reviving, Exfil, Left, Hands, RoundEnd, Cook, Stats, Bodies, HitPay, ZombieDead, In, PauseAsk, Rules, ChangeAsk, ChangeTaken, ItemHere, ExfilVote, HostDo, PaPUse, PaPInfused, Ready, Go, HandsSpeed, GunRounds, AliveZombies, HostHands,
        }

        // A message being applied: nothing it causes is sent again
        private static bool _applying;

        // The session's profile while the player isn't made yet (Fika's lobby, the map still loading): the same id
        private static string Me => Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId
            ?? SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile?.ProfileId ?? "";

        /// <summary>The add-on: a Fika match its host hasn't started yet (Fika's lobby).</summary>
        public static Func<bool> FikaWaiting { get; set; }

        /// <summary>The add-on: players in the Fika match, those still loading included.</summary>
        public static Func<int> FikaPlayers { get; set; }

        /// <summary>
        /// Fika (bridge): press Fika's own START on the accept screen (host the raid); false while it isn't up yet.
        /// The bool: a free Fika headless may host it (a party), or never (PLAY SOLO).
        /// </summary>
        public static Func<bool, bool> FikaHostNow { get; set; }

        /// <summary>Fika (bridge): join the raid hosted by this profile id, on this map (Fika's own join from the menu).</summary>
        public static Action<string, string> FikaJoin { get; set; }

        /// <summary>Fika (bridge): this game's Fika server is up as the host (the party can join now).</summary>
        public static void FikaServerUp()
        {
            // A Fika headless game hosts for the party's leader; the leader tells the party (FikaHeadlessUp)
            if (!Headless)
                ZombiesParty.Hosted(null);
        }

        /// <summary>
        /// Fika (bridge): the party's leader asked a Fika headless to host and is now in its raid as a client; the
        /// members join that match (its id is the headless's profile id, not the leader's).
        /// </summary>
        public static void FikaHeadlessUp(string matchId) => ZombiesParty.Hosted(matchId);

        /// <summary>
        /// This game is a Fika headless (set by the bridge from Fika's own flag): it hosts the match with no one
        /// playing on it. Host logic runs (rounds, zombies, points, bodies, power-ups); nothing for its own player.
        /// </summary>
        public static bool Headless { get; set; }

        /// <summary>This match is hosted by a Fika headless (true on the headless and on every player's game).</summary>
        public static bool HeadlessMatch { get; set; }

        /// <summary>This game asked the headless to host (the party's leader): it presses Fika's START in the raid.</summary>
        public static bool HeadlessRequester { get; set; }

        /// <summary>Fika's headless player (named headless_... by Fika's server): not a player, not a zombie.</summary>
        public static bool IsHeadlessPlayer(Player p) =>
            p != null && ((Headless && p.IsYourPlayer)
                || (p.Profile?.Info?.Nickname?.StartsWith("headless_", StringComparison.OrdinalIgnoreCase) ?? false));

        /// <summary>This player in the zombies lobby: name, ready, host.</summary>
        internal static void Lobby(string name, bool ready, bool host) => Send(Msg.Lobby, w =>
        {
            w.Write(name ?? "Player");
            w.Write(ready);
            w.Write(host);
        });

        // ---------- Sending ----------

        /// <summary>The exfil's state (the host's word; a client throwing the lever sends Holding).</summary>
        public static void Exfil(byte phase, float left, bool paused, List<string> leaving) => Send(Msg.Exfil, w =>
        {
            w.Write(phase);
            w.Write(left);
            w.Write(paused);
            w.Write(leaving?.Count ?? 0);
            if (leaving != null)
                foreach (string id in leaving)
                    w.Write(id ?? "");
        }, true);

        private static void Send(Msg type, Action<BinaryWriter> body = null, bool always = false)
        {
            // Nothing sent while applying someone else's message (no echoes), unless it's this player's own news
            if (!Online || (_applying && !always) || Sender == null)
                return;
            try
            {
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write((byte)type);
                    w.Write(Me);
                    body?.Invoke(w);
                    w.Flush();
                    Sender(ms.ToArray());
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies net: sending {type} failed: {e.Message}");
            }
        }

        /// <summary>
        /// From the add-on, on the main thread. The host passes a client's message on to every client (the sender
        /// skips its own) and applies it; a client applies what the host sends.
        /// </summary>
        /// <summary>When each other player's game was last heard from (any message; a heartbeat every 2 s in a match).</summary>
        public static readonly Dictionary<string, float> LastHeard = new Dictionary<string, float>();

        public static void Receive(byte[] data, bool fromClient)
        {
            if (data == null || data.Length < 2)
                return;
            if (IsHost && fromClient)
                Sender?.Invoke(data);
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data)))
                {
                    var type = (Msg)r.ReadByte();
                    string from = r.ReadString();
                    if (from == Me)
                        return;
                    LastHeard[from] = Time.realtimeSinceStartup;
                    _applying = true;
                    try
                    {
                        Apply(type, from, r);
                    }
                    finally
                    {
                        _applying = false;
                    }
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies net: a message failed: {e.Message}");
            }
        }

        private static void Apply(Msg type, string from, BinaryReader r)
        {
            if (!ZombiesMatch.Active && type != Msg.ItemAdd && type != Msg.ItemRemove && type != Msg.Lobby)
                return;
            switch (type)
            {
                case Msg.Hello:
                    // Someone new: the host tells them where the match stands; everyone sends their kit
                    if (IsHost)
                    {
                        SendState();
                        // One set of match rules for everyone: the host's (ZombiesSettings)
                        Send(Msg.Rules, ZombiesSettings.Write, always: true);
                        // Back after a drop (seen before in this match): their points as they were
                        if (Seen.TryGetValue(from, out int pts))
                        {
                            Send(Msg.Restore, w => { w.Write(from); w.Write(pts); }, always: true);
                            ZombiesPlugin.Log.LogInfo($"Zombies net: {from} is back: {pts} points given back");
                        }
                    }
                    SendKit();
                    break;
                case Msg.State:
                    ApplyState(r);
                    break;
                case Msg.Rules:
                    ZombiesSettings.Read(r);
                    break;
                case Msg.ExfilVote:
                    {
                        byte kind = r.ReadByte();
                        int a = r.ReadInt32(), b = r.ReadInt32();
                        float left = r.ReadSingle();
                        ZombiesExfil.VoteMessage(from, kind, a, b, left);
                        break;
                    }
                case Msg.PaPUse:
                    {
                        string key = r.ReadString();
                        byte kind = r.ReadByte();
                        int n = r.ReadInt32();
                        byte[] bytes = n > 0 ? r.ReadBytes(n) : null;
                        Item gun = null;
                        if (bytes != null && BytesToItem != null)
                            try { gun = BytesToItem(bytes); } catch (Exception e) { ZombiesPlugin.Log.LogWarning($"Zombies net: a teammate's PaP gun didn't unpack ({e.Message})"); }
                        if (gun != null)
                            ZombiesPlugin.Instance.StartCoroutine(PaPWhenLoaded(key, kind, from, gun));
                        else
                            ZombiesPaP.Remote(key, kind, from, null);
                        break;
                    }
                case Msg.PaPInfused:
                    ZombiesPaP.InfuseRemote(r.ReadString());
                    break;
                case Msg.HostDo:
                    {
                        string what = r.ReadString();
                        if (IsHost)
                            RunHostDo(what, from);
                        break;
                    }
                case Msg.HandsSpeed:
                    {
                        float hr = r.ReadSingle(), hd = r.ReadSingle(), hf = r.ReadSingle();
                        ZombiesPerks.RemoteHandsSpeed(from, hr, hd, hf);
                    }
                    break;
                case Msg.GunRounds:
                    {
                        string gunId = r.ReadString();
                        int rounds = r.ReadInt32();
                        ZombiesAmmo.RoundsRemote(PlayerOf(from), gunId, rounds);
                    }
                    break;
                case Msg.Ready:
                    if (IsHost)
                        PlayersReady.Add(from);
                    break;
                case Msg.Go:
                    if (!GoNow)
                        ZombiesPlugin.Log.LogInfo("Zombies net: the host says everyone's ready: in we go");
                    GoNow = true;
                    break;
                case Msg.ItemHere:
                    HostHasNow(r.ReadString());
                    break;
                case Msg.ChangeAsk:
                    {
                        string key = r.ReadString();
                        if (IsHost)
                            PerkProneBonus.Claim(key, from);
                        break;
                    }
                case Msg.ChangeTaken:
                    {
                        string key = r.ReadString(), who = r.ReadString();
                        if (!IsHost)
                            PerkProneBonus.Given(key, who);
                        break;
                    }
                case Msg.Points:
                    TeamPoints[from] = r.ReadInt32();
                    Seen[from] = TeamPoints[from];
                    break;
                case Msg.Restore:
                    {
                        string who = r.ReadString();
                        int pts = r.ReadInt32();
                        if (who == Me && !_restored)
                        {
                            _restored = true;
                            ZombiesMatch.RestorePoints(pts);
                        }
                    }
                    break;
                case Msg.Vox:
                    ZombiesVox.Remote(from, r.ReadInt32(), r.ReadString());
                    break;
                case Msg.Round:
                    ZombiesRounds.RoundFromHost(r.ReadInt32());
                    break;
                case Msg.Hands:
                    HandsRemote(from, r.ReadByte(), r.ReadString());
                    break;
                case Msg.HostHands:
                {
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                        HostSeen[r.ReadString()] = r.ReadString();
                    HostHandsHeard = true;
                    break;
                }
                case Msg.Stats:
                {
                    var v = new int[9];
                    for (int i = 0; i < v.Length; i++)
                        v[i] = r.ReadInt32();
                    ZombiesStats.SetRemote(from, v);
                    break;
                }
                case Msg.Bodies:
                {
                    int n = r.ReadInt32();
                    var ids = new List<string>();
                    for (int i = 0; i < n; i++)
                        ids.Add(r.ReadString());
                    ZombiesLitter.BodiesFromHost(ids);
                    break;
                }
                case Msg.AliveZombies:
                {
                    int n = r.ReadInt32();
                    var alive = new HashSet<string>();
                    for (int i = 0; i < n; i++)
                        alive.Add(r.ReadString());
                    if (!IsHost)
                        ZombiesLitter.AliveOnHost(alive);
                    break;
                }
                case Msg.Cook:
                    ZombiesGrenades.CookRemote(from, r.ReadSingle());
                    break;
                case Msg.RoundEnd:
                    ZombiesRounds.RoundEndFromHost(r.ReadInt32());
                    break;
                case Msg.Left:
                    ZombiesRounds.LeftFromHost(r.ReadInt32());
                    break;
                case Msg.Open:
                    ZombiesMatch.OpenRemote(r.ReadInt32());
                    break;
                case Msg.Power:
                    ZombiesMatch.PowerOn();
                    break;
                case Msg.Pause:
                    ZombiesPause.FromHost(r.ReadBoolean());
                    break;
                case Msg.PauseAsk:
                    // Only the host acts on it (a Fika headless: the lobby's leader asks it to pause/resume)
                    bool ask = r.ReadBoolean();
                    if (IsHost)
                        ZombiesPause.Asked(ask, from);
                    break;
                case Msg.Rock:
                    ZombiesRock.FoundRemote(r.ReadInt32());
                    break;
                case Msg.Boards:
                    ZombiesBarricade.SetBoards(r.ReadInt32(), r.ReadInt32());
                    break;
                case Msg.Rebuild:
                    if (IsHost)
                        ZombiesBarricade.RebuildRemote(r.ReadInt32());
                    break;
                case Msg.BoxStart:
                    ZombiesMatch.BoxAt(r.ReadInt32());
                    break;
                case Msg.BoxSpin:
                    {
                        bool teddy = r.ReadBoolean();
                        string gun = r.ReadString();
                        int next = r.ReadInt32();
                        int spot = r.BaseStream.Position < r.BaseStream.Length ? r.ReadInt32() : -1;
                        MysteryBox.SpinRemote(from, teddy, gun, next, spot);
                        break;
                    }
                case Msg.BoxTaken:
                    MysteryBox.TakenRemote(r.BaseStream.Position < r.BaseStream.Length ? r.ReadInt32() : -1);
                    break;
                case Msg.Down:
                    Downed[from] = (State.Down, Time.time);
                    ZombiesDowned.CheckEveryoneDown();
                    break;
                case Msg.Up:
                    Downed.Remove(from);
                    break;
                case Msg.Dead:
                    Downed[from] = (State.Dead, Time.time);
                    ZombiesDowned.CheckEveryoneDown();
                    break;
                case Msg.Revive:
                    if (r.ReadString() == Me && Singleton<GameWorld>.Instance?.MainPlayer is Player me)
                        ZombiesDowned.Revive(me, "a teammate");
                    break;
                case Msg.Exfil:
                    {
                        byte phase = r.ReadByte();
                        float left = r.ReadSingle();
                        bool paused = r.ReadBoolean();
                        int n = r.ReadInt32();
                        var ids = new List<string>();
                        for (int i = 0; i < n; i++)
                            ids.Add(r.ReadString());
                        ZombiesExfil.Set((ZombiesExfil.Phase)phase, left, paused, phase == (byte)ZombiesExfil.Phase.Done ? ids : null);
                    }
                    break;
                case Msg.Reviving:
                    {
                        string target = r.ReadString();
                        float seconds = r.ReadSingle();
                        if (target == Me)
                            ZombiesDowned.BeingRevived(from, seconds);
                    }
                    break;
                case Msg.Slap:
                    if (r.ReadString() == Me && Singleton<GameWorld>.Instance?.MainPlayer is Player slapped)
                        ZombiesSlaps.Hit(slapped);
                    break;
                case Msg.PuDrop:
                    ZombiesPowerUps.DropRemote(r.ReadInt32(), (ZombiesPowerUps.Kind)r.ReadByte(), ReadVector(r));
                    break;
                case Msg.PuGrab:
                    if (IsHost)
                        ZombiesPowerUps.GrabRequest(r.ReadInt32(), from);
                    break;
                case Msg.PuTake:
                    ZombiesPowerUps.TakeRemote(r.ReadInt32(), (ZombiesPowerUps.Kind)r.ReadByte(), r.ReadString());
                    break;
                case Msg.Web:
                    ZombiesWidow.WebRemote(ReadVector(r));
                    break;
                case Msg.ZombieDead:
                    ZombiesLitter.DiedOnHost(r.ReadString());
                    break;
                case Msg.In:
                    if (PlayersIn.Add(from) && Headless)
                        ZombiesPlugin.Log.LogInfo($"Zombies over Fika (headless): {from} is in the match");
                    break;
                case Msg.HitPay:
                    if (r.ReadString() == Me)
                        ZombiesStats.Hit();
                    break;
                case Msg.Kill:
                    if (r.ReadString() == Me)
                        ZombiesStats.Killed(r.ReadBoolean(), r.ReadBoolean());
                    break;
                case Msg.ItemAdd:
                    ItemAddRemote(from, r);
                    break;
                case Msg.Lobby:
                    ZombiesLobby.Remote(from, r.ReadString(), r.ReadBoolean(), r.ReadBoolean());
                    break;
                case Msg.ItemRemove:
                    ItemRemoveRemote(from, r.ReadString());
                    break;
            }
        }

        private static void WriteVector(BinaryWriter w, Vector3 v)
        {
            w.Write(v.x);
            w.Write(v.y);
            w.Write(v.z);
        }

        private static Vector3 ReadVector(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        // ---------- The match as a whole: a client joining (or built late) asks the host for it ----------

        /// <summary>The map is built here: a client asks the host where everything stands.</summary>
        internal static void Built()
        {
            TeamPoints.Clear();
            Downed.Clear();
            if (Online && !IsHost)
                Send(Msg.Hello);
        }

        /// <summary>The kit waiting for the host: ask again (a Hello sent before the host's game had the match is lost).</summary>
        internal static void AskHostAgain()
        {
            if (Online && !IsHost && !JoinedHost)
                Send(Msg.Hello);
        }

        /// <summary>The add-on: the raid has started (a client asks the host, in case it was built before going online).</summary>
        public static void RaidStarted()
        {
            if (ZombiesMatch.Active && !IsHost)
                Send(Msg.Hello);
        }

        /// <summary>
        /// Everything this player wears and carries, sent again whole (after the zombies kit is on, and when someone
        /// joins): the others' copy of this player then has every item made here, whatever arrived when.
        /// </summary>
        internal static void SendKit()
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (!Online || me == null)
                return;
            foreach (Slot slot in me.InventoryController.Inventory.Equipment.Slots)
                if (slot.ContainedItem != null && slot.ID != "Dogtag")
                    ItemAdded(slot.ContainedItem, slot.CreateItemAddress());
            Send(Msg.Points, w => w.Write(ZombiesMatch.Points));
        }

        private static void SendState()
        {
            Send(Msg.State, w =>
            {
                w.Write(ZombiesRounds.Round);
                w.Write(ZombiesMatch.PowerIsOn);
                List<int> opened = ZombiesMatch.OpenedEntries();
                w.Write(opened.Count);
                foreach (int i in opened)
                    w.Write(i);
                List<(int entry, int up)> boards = ZombiesBarricade.AllBoards();
                w.Write(boards.Count);
                foreach ((int entry, int up) in boards)
                {
                    w.Write(entry);
                    w.Write(up);
                }
                w.Write(ZombiesMatch.BoxSpot());
            }, always: true); // the host's answer to a Hello (no-echo rule held it back)
        }

        private static void ApplyState(BinaryReader r)
        {
            int round = r.ReadInt32();
            bool power = r.ReadBoolean();
            int opened = r.ReadInt32();
            var open = new List<int>();
            for (int i = 0; i < opened; i++)
                open.Add(r.ReadInt32());
            int boards = r.ReadInt32();
            var up = new List<(int, int)>();
            for (int i = 0; i < boards; i++)
                up.Add((r.ReadInt32(), r.ReadInt32()));
            int box = r.ReadInt32();
            ZombiesRounds.RoundFromHost(round);
            if (power && !ZombiesMatch.PowerIsOn)
                ZombiesMatch.PowerOn();
            foreach (int i in open)
                ZombiesMatch.OpenRemote(i);
            foreach ((int entry, int n) in up)
                ZombiesBarricade.SetBoards(entry, n);
            ZombiesMatch.BoxAt(box);
            JoinedHost = true;
            ZombiesPlugin.Log.LogInfo($"Zombies net: joined the host's match (round {round}, power {(power ? "on" : "off")}, {open.Count} opened, box spot {box + 1})");
        }

        // ---------- Calls from the match ----------

        // Always sent: points often change while applying the host's message (a kill it confirmed), which once kept a
        // client's score from ever reaching the others
        /// <summary>This player's character line, for the others to hear from them (3D).</summary>
        internal static void Vox(int character, string line) => Send(Msg.Vox, w => { w.Write(character); w.Write(line); }, always: true);

        internal static void Points(int points) => Send(Msg.Points, w => w.Write(points), always: true);
        // Always sent: a client's F12 "Next round" runs on the host while its message is being applied, and the new round
        // number never reached anyone (user, 2026-11-10: the counter stayed while the zombie count changed)
        internal static void Round(int round) => Send(Msg.Round, w => w.Write(round), always: true);
        internal static void RoundEnd(int round) => Send(Msg.RoundEnd, w => w.Write(round), always: true);
        internal static void BodiesGone(List<string> ids) => Send(Msg.Bodies, w =>
        {
            w.Write(ids.Count);
            foreach (string id in ids)
                w.Write(id);
        });
        internal static void AliveZombies(List<string> ids) => Send(Msg.AliveZombies, w =>
        {
            w.Write(ids.Count);
            foreach (string id in ids)
                w.Write(id);
        });
        internal static void Cook(float cooked) => Send(Msg.Cook, w => w.Write(cooked));
        internal static void Opened(int entry) => Send(Msg.Open, w => w.Write(entry));

        internal static void RockFound(int entry) => Send(Msg.Rock, w => w.Write(entry));
        internal static void Power() => Send(Msg.Power);
        internal static void Pause(bool on) => Send(Msg.Pause, w => w.Write(on), always: true); // also the host's answer to a pause ask (no-echo rule)

        /// <summary>The exfil vote: 0 call (a lever thrown), 1 yes, 2 no (players to the host); 3 state yes/needed/left, 4 passed, 5 failed (host to all).</summary>
        internal static void ExfilVote(byte kind, int a = 0, int b = 0, float left = 0f) =>
            Send(Msg.ExfilVote, w => { w.Write(kind); w.Write(a); w.Write(b); w.Write(left); }, always: true);

        /// <summary>
        /// F12 debug buttons that change the match (next round, kill all): run on the host, which owns the rounds. On a
        /// client they used to change only its own round counter (user, 2026-11-09: "round 15" with round 2 zombies)
        /// </summary>
        internal static void HostDo(string what)
        {
            if (!Online || IsHost)
            {
                RunHostDo(what, null);
                return;
            }
            Send(Msg.HostDo, w => w.Write(what), always: true);
            ZombiesPlugin.Notify($"Asked the host: {what}");
        }

        private static void RunHostDo(string what, string from)
        {
            ZombiesPlugin.Log.LogInfo($"Zombies debug: {what}{(from != null ? " (asked by " + from + ")" : "")}");
            switch (what)
            {
                case "nextround": ZombiesRounds.NextRound(); break;
                case "killall": ZombiesRounds.KillAll(); break;
                case "exfilround": ZombiesRounds.JumpToRound(ZombiesRounds.ExfilRound); break;
            }
        }

        /// <summary>The Pack-a-Punch machine: 0 my gun goes in (sent whole), 1 I took it / it's gone.</summary>
        internal static void PaPUse(string key, byte kind, Item gun)
        {
            if (!Online)
                return;
            byte[] bytes = null;
            if (gun != null && ItemToBytes != null)
                try { bytes = ItemToBytes(gun); } catch { bytes = null; }
            Send(Msg.PaPUse, w =>
            {
                w.Write(key);
                w.Write(kind);
                w.Write(bytes?.Length ?? 0);
                if (bytes != null)
                    w.Write(bytes);
            }, always: true);
        }

        internal static void PaPInfused(string id)
        {
            if (Online)
                Send(Msg.PaPInfused, w => w.Write(id), always: true);
        }

        private static System.Collections.IEnumerator PaPWhenLoaded(string key, byte kind, string from, Item gun)
        {
            yield return ZombiesItems.LoadModels(gun);
            ZombiesPaP.Remote(key, kind, from, gun);
        }

        /// <summary>A player's name by profile id (the match's players), or "a teammate".</summary>
        internal static string PlayerName(string id) => PlayerOf(id)?.Profile?.Nickname ?? "a teammate";

        /// <summary>A perk machine's loose change: a client asks the host for it; the host says who got it.</summary>
        internal static void AskChange(string key) => Send(Msg.ChangeAsk, w => w.Write(key), always: true);
        internal static void ChangeTaken(string key, string who) => Send(Msg.ChangeTaken, w => { w.Write(key); w.Write(who ?? ""); }, always: true);

        /// <summary>The match's leader (headless match): asks the host to pause (true) or resume (false).</summary>
        /// <summary>This game's hands speeds (reload, draw multipliers), for the others to play our hands at.</summary>
        internal static void SendHandsSpeed(float reload, float draw, float fire) => Send(Msg.HandsSpeed, w =>
        {
            w.Write(reload);
            w.Write(draw);
            w.Write(fire);
        }, always: true);

        /// <summary>The held gun's mag holds this many rounds now (ZombiesAmmo.SyncLoaded).</summary>
        internal static void GunRounds(string gunId, int count) => Send(Msg.GunRounds, w =>
        {
            w.Write(gunId);
            w.Write(count);
        }, always: true);

        internal static void AskPause(bool on) => Send(Msg.PauseAsk, w => w.Write(on), always: true);

        /// <summary>Host: how many zombies are left in this round (killed or not yet spawned), for the counter.</summary>
        internal static void Left(int left) => Send(Msg.Left, w => w.Write(left));
        // always: the host's answer to a client's rebuild goes out while that message is being applied (the no-echo rule
        // held it back, and a client's boards only came up at the next tear: user, 2026-11-10)
        internal static void Boards(int entry, int up, bool always = false) => Send(Msg.Boards, w => { w.Write(entry); w.Write(up); }, always);
        internal static void Rebuilt(int entry) => Send(Msg.Rebuild, w => w.Write(entry));
        internal static void BoxStart(int spot) => Send(Msg.BoxStart, w => w.Write(spot));
        // The spot says which box (the main one or a fire sale's extra at that spot)
        internal static void BoxSpin(bool teddy, string gun, int next, int spot) => Send(Msg.BoxSpin, w => { w.Write(teddy); w.Write(gun ?? ""); w.Write(next); w.Write(spot); });
        internal static void BoxTaken(int spot) => Send(Msg.BoxTaken, w => w.Write(spot));
        // A player's own state: sent even while applying another message (the host's slap downs you, a revive gets you up)
        internal static void Down() => Send(Msg.Down, always: true);
        internal static void Up() => Send(Msg.Up, always: true);
        internal static void Dead() => Send(Msg.Dead, always: true);
        internal static void Revive(string who) => Send(Msg.Revive, w => w.Write(who));
        /// <summary>Started (seconds &gt; 0, how long it takes) or stopped (0) reviving this teammate: their screen shows it.</summary>
        internal static void Reviving(string who, float seconds) => Send(Msg.Reviving, w => { w.Write(who ?? ""); w.Write(seconds); });
        internal static void Slap(string who) => Send(Msg.Slap, w => w.Write(who));
        internal static void PowerUpDropped(int id, ZombiesPowerUps.Kind kind, Vector3 at) => Send(Msg.PuDrop, w => { w.Write(id); w.Write((byte)kind); WriteVector(w, at); });
        internal static void PowerUpGrab(int id) => Send(Msg.PuGrab, w => w.Write(id));
        // The host decides a grab while applying the client's request: it has to go out (it never did: the client kept it)
        internal static void PowerUpTaken(int id, ZombiesPowerUps.Kind kind, string by) => Send(Msg.PuTake, w => { w.Write(id); w.Write((byte)kind); w.Write(by ?? ""); }, always: true);
        internal static void ZombieDied(string id) => Send(Msg.ZombieDead, w => w.Write(id), always: true);
        internal static void HitBy(string who) => Send(Msg.HitPay, w => w.Write(who), always: true);
        internal static void KillBy(string who, bool head, bool knife) => Send(Msg.Kill, w => { w.Write(who); w.Write(head); w.Write(knife); });
        internal static void Web(Vector3 at) => Send(Msg.Web, w => WriteVector(w, at));

        // ---------- Teammates ----------

        internal enum State { Up, Down, Dead }

        /// <summary>Teammates' points (by profile), as they last told us.</summary>
        internal static readonly Dictionary<string, int> TeamPoints = new Dictionary<string, int>();

        // Every player's last points this match, kept through a drop (TeamPoints is cleared at each build), and whether
        // this game had its own given back already
        private static readonly Dictionary<string, int> Seen = new Dictionary<string, int>();
        private static bool _restored;

        /// <summary>A new match: nobody seen yet.</summary>
        /// <summary>Items the Fika headless says it has added to its copy of their owner (so they can be drawn).</summary>
        internal static readonly HashSet<string> HostHas = new HashSet<string>();

        /// <summary>
        /// Before drawing a new item in a match a Fika headless hosts: wait till the headless says it has it (its
        /// ItemHere), at most `most` seconds. The headless checks every draw against its copy of us, and a draw of an
        /// item it hadn't got yet was refused (pistols, then perk bottles: "Could not find item"). Elsewhere: no wait.
        /// </summary>
        // Items drawn before the headless had their models in: drawn again once it says it has them (its copy of the
        // hands had failed to build, "bundle is not loaded", and the player could load in but never shoot: the headless
        // on a PC running 3 games took 12 s to load the M1911, user 2026-11-09)
        private static readonly HashSet<string> DrawnEarly = new HashSet<string>();

        internal static System.Collections.IEnumerator UntilHostHas(Item item, string what, float most = 30f)
        {
            if (!HeadlessMatch || IsHost || item == null)
                yield break;
            string id = item.Id.ToString();
            float from = Time.time, until = from + most;
            while (!HostHas.Contains(id) && Time.time < until)
                yield return null;
            if (!HostHas.Contains(id))
                DrawnEarly.Add(id);
            ZombiesDebug.Log($"draw: the headless {(HostHas.Contains(id) ? "had" : "hadn't confirmed")} the {what} after {Time.time - from:0.00} s");
        }

        private static void HostHasNow(string id)
        {
            HostHas.Add(id);
            if (!DrawnEarly.Remove(id))
                return;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me?.HandsController?.Item != null && me.HandsController.Item.Id.ToString() == id)
            {
                ZombiesPlugin.Log.LogInfo("Zombies net: the headless has the gun in your hands now: drawn again so its copy of your hands works");
                ZombiesPlugin.Instance.StartCoroutine(ZombiesReload.Redraw(me));
            }
        }

        // ---------- The loading screen: nobody leaves it until every player is ready, then all at once ----------
        // (user, 2026-11-09: a player whose gun the headless hadn't loaded came in unable to shoot). Each game says when
        // its kit is in (and the headless has its gun); the host, once all have, sends Go and everyone fades in together.

        internal static readonly HashSet<string> PlayersReady = new HashSet<string>();
        public static bool GoNow { get; private set; }
        private static float _readySentAt = -10f, _goSentAt = -10f, _waitSince = -1f;
        private static int _goSends;

        /// <summary>A gun drawn before the headless had it is waiting to be drawn again.</summary>
        internal static bool HandsPending => DrawnEarly.Count > 0;

        /// <summary>This game: ready to come in (sent each second until the host's Go, in case one is lost).</summary>
        internal static void TickReady(bool ready)
        {
            if (!Online || GoNow || !ready || Me == null)
                return;
            if (IsHost)
            {
                PlayersReady.Add(Me);
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now - _readySentAt < 1f)
                return;
            _readySentAt = now;
            Send(Msg.Ready, null, always: true);
        }

        /// <summary>The host: every player ready (or waited <paramref name="atMost"/> s) -> Go, sent a few times.</summary>
        internal static void TickGo(float atMost)
        {
            if (!Online || !IsHost)
                return;
            float now = Time.realtimeSinceStartup;
            if (GoNow)
            {
                if (_goSends < 5 && now - _goSentAt > 1f)
                {
                    _goSentAt = now;
                    _goSends++;
                    Send(Msg.Go, null, always: true);
                }
                return;
            }
            if (_waitSince < 0f)
                _waitSince = now;
            var team = ZombiesTeam.List();
            bool all = team.Count > 0 && team.All(p => PlayersReady.Contains(p.ProfileId));
            if (!all && now - _waitSince < atMost)
                return;
            GoNow = true;
            _goSentAt = now;
            _goSends = 1;
            Send(Msg.Go, null, always: true);
            ZombiesPlugin.Log.LogInfo($"Zombies net: {(all ? "every player is ready" : "waited long enough")} ({PlayersReady.Count} of {team.Count}): everyone in");
        }

        /// <summary>Players whose game says they're loaded in (the headless starts round 1 once all are).</summary>
        internal static readonly HashSet<string> PlayersIn = new HashSet<string>();

        /// <summary>This player is loaded in (sent with the presence beat, so a late or lost one still lands).</summary>
        internal static void ImIn() => Send(Msg.In);

        /// <summary>
        /// A joining game: the host has answered our Hello with its match state, so its game has the match and our
        /// player in it. Until then nothing we do to our hands or kit reaches the host (the headless's game could start
        /// 25 s after ours: our hands were emptied before it had us, it built us still holding the gun we spawned with,
        /// then refused every draw; user 2026-11-11, FikaTest1 with a Ray Gun).
        /// </summary>
        internal static bool JoinedHost { get; private set; }

        internal static void NewMatch()
        {
            JoinedHost = false;
            HostSeen.Clear();
            HostHandsHeard = false;
            _hostHandsSent = null;
            PlayersIn.Clear();
            HostHas.Clear();
            DrawnEarly.Clear();
            PlayersReady.Clear();
            ZombiesPerks.ClearHandsSpeeds();
            GoNow = false;
            _waitSince = -1f;
            _goSends = 0;
            RebuiltOnce.Clear();
            PerkProneBonus.Taken.Clear();
            Seen.Clear();
            _restored = false;
        }

        /// <summary>Teammates down or dead (by profile), and since when.</summary>
        internal static readonly Dictionary<string, (State state, float since)> Downed = new Dictionary<string, (State, float)>();

        internal static State StateOf(string profileId) => Downed.TryGetValue(profileId, out var s) ? s.state : State.Up;

        // ---------- Items made on a player (the zombies kit, box guns, mags, grenades, perk bottles) ----------
        // EFT only sends moves of items everyone already has; a new item would be unknown to the others' copy of you
        // (their game couldn't show it in your hands). So it's sent whole, with where it went, and added there

        /// <summary>A new item put on the local player at <paramref name="to"/>.</summary>
        internal static void ItemAdded(Item item, ItemAddress to)
        {
            if (!Online || ItemToBytes == null || item == null || to == null)
                return;
            string where = Address(to);
            if (where == null)
                return;
            byte[] bytes;
            try
            {
                bytes = ItemToBytes(item);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies net: couldn't pack {item.LocalizedName()}: {e.Message}");
                return;
            }
            Send(Msg.ItemAdd, w =>
            {
                w.Write(where);
                w.Write(bytes.Length);
                w.Write(bytes);
            });
        }

        /// <summary>An item taken off the local player outside EFT's own moves (the pockets swapped).</summary>
        internal static void ItemRemoved(Item item)
        {
            if (item != null)
                Send(Msg.ItemRemove, w => w.Write(item.Id.ToString()));
        }

        // "slot|parentId|slotName" or "grid|parentId|gridId|x|y|rotated"
        private static string Address(ItemAddress to)
        {
            if (to.Container is Slot slot)
                return $"slot|{slot.ParentItem.Id}|{slot.ID}";
            if (to is GridItemAddress grid)
                return $"grid|{grid.Grid.ParentItem.Id}|{grid.Grid.ID}|{grid.LocationInGrid.x}|{grid.LocationInGrid.y}|{(int)grid.LocationInGrid.r}";
            return null;
        }

        private static Player PlayerOf(string profileId) =>
            Singleton<GameWorld>.Instance?.AllAlivePlayersList.FirstOrDefault(p => p != null && p.ProfileId == profileId);

        private static void ItemAddRemote(string from, BinaryReader r)
        {
            string where = r.ReadString();
            byte[] bytes = r.ReadBytes(r.ReadInt32());
            if (!TryAddRemote(from, where, bytes, false))
                PendingAdds.Add((from, where, bytes, Time.time + 60f));
        }

        // Items another game added that can't go in yet: its player isn't here yet (its kit goes on while the map
        // loads) or the container they go in (its pockets, its rig) hasn't arrived. Tried again each second, a minute
        private static readonly List<(string from, string where, byte[] bytes, float until)> PendingAdds = new List<(string, string, byte[], float)>();
        private static float _nextPending;

        /// <summary>Each frame of a match: the held-back item adds tried again (in the order they came).</summary>
        public static void TickPending()
        {
            TickHands();
            TickStats();
            if (PendingAdds.Count == 0 || Time.time < _nextPending)
                return;
            _nextPending = Time.time + 1f;
            for (int i = 0; i < PendingAdds.Count; i++)
            {
                var (from, where, bytes, until) = PendingAdds[i];
                bool last = Time.time > until;
                if (TryAddRemote(from, where, bytes, last) || last)
                {
                    PendingAdds.RemoveAt(i);
                    i--;
                }
            }
        }

        // ---------- What each player holds (user, 2026-11-09: the other game saw the host empty-handed) ----------
        // Fika tells the others which item a player drew; a gun our kit gave arrives with our item message, which waits
        // for its models first, so Fika's draw came first, found no item and gave up (hands empty until the next draw).
        // So every game also says what it holds (on a change, and every 3 s), and the others put that item in the
        // player's hands through Fika's own observed-player call once the item is there. Fika itself is unchanged.

        // ---------- Each player's stats for everyone's board (user, 2026-11-09: teammates showed "-") ----------
        private static string _statsSent;
        private static float _nextStats, _statsForceAt;

        /// <summary>Sends this player's stats now (the game over, so every board has the final numbers).</summary>
        public static void SendStatsNow()
        {
            _statsForceAt = Time.time;
            TickStats();
        }

        private static void TickStats()
        {
            if (!Online || Time.time < _nextStats && _statsForceAt <= 0f)
                return;
            _nextStats = Time.time + 2f;
            int[] v = ZombiesStats.MineForNet();
            string key = string.Join(",", v);
            if (key == _statsSent && _statsForceAt <= 0f)
                return;
            _statsSent = key;
            _statsForceAt = 0f;
            Send(Msg.Stats, w =>
            {
                foreach (int x in v)
                    w.Write(x);
            }, always: true);
        }

        private const byte HoldFirearm = 1, HoldKnife = 2;
        private static string _myHeld;
        private static float _nextHeldSend, _nextHandsFix;
        private static readonly Dictionary<string, (byte kind, string id)> Held = new Dictionary<string, (byte, string)>();
        private static readonly Dictionary<string, float> HandsFixedAt = new Dictionary<string, float>();

        private static void TickHands()
        {
            if (!Online)
                return;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me != null)
            {
                Item item = me.HandsController?.Item;
                byte kind = me.HandsController is Player.FirearmController ? HoldFirearm : me.HandsController is Player.KnifeController ? HoldKnife : (byte)0;
                // Empty hands are news too (kind 0): without it the others kept the last gun in Held and their hands fix
                // put it back in this player's hands on their games every 2 s (user 2026-11-11: both came in holding the
                // gun they spawned with on the other's screen)
                if (kind != 0 && item == null)
                    kind = 0;
                string held = kind == 0 ? "0:" : kind + ":" + item.Id;
                if (held != _myHeld || Time.time >= _nextHeldSend)
                {
                    _myHeld = held;
                    _nextHeldSend = Time.time + 3f;
                    string id = kind == 0 ? "" : item.Id.ToString();
                    Send(Msg.Hands, w =>
                    {
                        w.Write(kind);
                        w.Write(id);
                    });
                }
            }
            TickHostHands();
            // A Fika headless is the authority on everyone's hands (Fika builds its copies from their own games):
            // never rebuilt there (it broke the copies: every quick knife came back with no hands)
            if (Headless || Time.time < _nextHandsFix || Held.Count == 0)
                return;
            _nextHandsFix = Time.time + 0.5f;
            foreach (var pair in Held.ToList())
                FixHands(pair.Key, pair.Value.kind, pair.Value.id);
        }

        // ---------- The host's copy of each player's hands ----------
        // The host (a Fika headless above all) checks every draw against its own copy of each player. At a match's start
        // its copy draws the gun the player spawned with, slowly (gun models load there): hands emptied while it's still
        // drawing never reached it, it kept that gun and refused every draw after (user, 2026-11-11). So the host says
        // what its copy of each player holds, and the kit waits on that.

        private static readonly Dictionary<string, string> HostSeen = new Dictionary<string, string>();
        private static string _hostHandsSent;
        private static float _nextHostHands, _hostHandsAgain;

        /// <summary>The host has said what its copies hold at least once (an older host never does).</summary>
        internal static bool HostHandsHeard { get; private set; }

        /// <summary>What the host's copy of this player holds: an item id, "" for empty hands, null if not heard yet.</summary>
        internal static string HostSeesMe() => HostSeen.TryGetValue(Me, out string id) ? id : null;

        private static void TickHostHands()
        {
            if (!IsHost || Time.time < _nextHostHands)
                return;
            _nextHostHands = Time.time + 0.25f;
            var list = new List<(string pid, string held)>();
            foreach (Player p in ZombiesTeam.List())
                if (p != null && !p.IsYourPlayer)
                    list.Add((p.ProfileId, p.HandsController is Player.EmptyHandsController || p.HandsController?.Item == null ? "" : p.HandsController.Item.Id.ToString()));
            if (list.Count == 0)
                return;
            string key = string.Join(";", list.Select(x => x.pid + "=" + x.held));
            if (key == _hostHandsSent && Time.time < _hostHandsAgain)
                return;
            _hostHandsSent = key;
            _hostHandsAgain = Time.time + 2f;
            Send(Msg.HostHands, w =>
            {
                w.Write(list.Count);
                foreach ((string pid, string held) in list)
                {
                    w.Write(pid);
                    w.Write(held);
                }
            }, always: true);
        }

        private static void HandsRemote(string from, byte kind, string id)
        {
            if (kind == 0)
            {
                // Hands emptied: nothing to put back in them
                Held.Remove(from);
                return;
            }
            Held[from] = (kind, id);
            if (Headless)
                return;
            FixHands(from, kind, id);
            // A teammate's Pack-a-Punched gun drawn here the first time: their hands rebuilt once, 1 s on, so its new
            // parts show (a PaP'd M16's M203 was missing on the others, user 2026-11-09)
            if (kind == HoldFirearm && RebuiltOnce.Add(from + ":" + id))
                ZombiesPlugin.Instance.StartCoroutine(RebuildIfPaP(from, id));
        }

        private static readonly HashSet<string> RebuiltOnce = new HashSet<string>();

        private static System.Collections.IEnumerator RebuildIfPaP(string from, string id)
        {
            yield return new WaitForSeconds(1f);
            Player owner = PlayerOf(from);
            Item held = owner?.HandsController?.Item;
            if (held == null || held.Id.ToString() != id || !ZombiesPaP.Infused(held))
                yield break;
            HandsFixedAt.Remove(from);
            FixHands(from, HoldFirearm, id, rebuild: true);
            ZombiesDebug.Log($"teammate {owner.Profile?.Nickname}'s Pack-a-Punched {held.LocalizedName()}: hands rebuilt so its parts show");
        }

        private static void FixHands(string from, byte kind, string id, bool rebuild = false)
        {
            Player owner = PlayerOf(from);
            if (owner == null || owner.IsYourPlayer)
                return;
            if (!rebuild && owner.HandsController?.Item?.Id.ToString() == id)
                return;
            // Only an item that has arrived; not again within 2 s (Fika's own draw may be under way)
            Item item = owner.InventoryController.Inventory.Equipment.GetAllItems().FirstOrDefault(i => i.Id.ToString() == id);
            if (item == null || HandsFixedAt.TryGetValue(from, out float at) && Time.time - at < 2f)
                return;
            HandsFixedAt[from] = Time.time;
            try
            {
                MethodInfo spawn = owner.GetType().GetMethod("SpawnHandsController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (spawn == null)
                    return;
                var type = kind == HoldKnife ? EHandsControllerType.Knife : EHandsControllerType.Firearm;
                spawn.Invoke(owner, new object[] { type, new MongoID(item.Id.ToString()), false });
                ZombiesPlugin.Log.LogInfo($"Zombies net: {owner.Profile?.Nickname}'s hands set to {item.LocalizedName()} (Fika's draw had missed it)");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies net: couldn't set {owner.Profile?.Nickname}'s hands ({(e.InnerException ?? e).Message})");
            }
        }

        private static bool TryAddRemote(string from, string where, byte[] bytes, bool giveUp)
        {
            Player owner = PlayerOf(from);
            if (owner == null || BytesToItem == null)
                return false;
            string[] p = where.Split('|');
            Item parent = owner.InventoryController.Inventory.Equipment.GetAllItems().FirstOrDefault(i => i.Id.ToString() == p[1])
                          ?? (owner.InventoryController.Inventory.Equipment.Id.ToString() == p[1] ? owner.InventoryController.Inventory.Equipment : null);
            if (!(parent is CompoundItem compound))
            {
                if (giveUp)
                    ZombiesPlugin.Log.LogWarning($"Zombies net: {owner.Profile?.Nickname}'s item: no {p[1]} to put it in (gave up)");
                return false;
            }
            Item item = BytesToItem(bytes);
            if (item == null)
                return true;
            ZombiesPlugin.Instance.StartCoroutine(AddWhenLoaded(owner, item, compound, p));
            return true;
        }

        private static System.Collections.IEnumerator AddWhenLoaded(Player owner, Item item, CompoundItem parent, string[] p)
        {
            // A Fika headless is the host every draw is checked by: the item must be there at once (its models took
            // 5-13 s to load there and the players' draws were refused meanwhile: "Could not find item"). Its models
            // load afterwards, for the observed player's hands.
            if (!Headless)
                yield return ZombiesItems.LoadModels(item);
            try
            {
                // Already there (the kit sent again): left as it is
                if (owner.InventoryController.Inventory.Equipment.GetAllItems().Any(i => i.Id == item.Id))
                    yield break;
                if (p[0] == "slot")
                {
                    Slot slot = parent.Slots.FirstOrDefault(s => s.ID == p[2]);
                    if (slot != null && slot.ContainedItem != null)
                        slot.RemoveItemWithoutRestrictions();
                    slot?.AddWithoutRestrictions(item);
                    // The body's model of that slot (a gun on the back) follows only this (EFT's own moves call it)
                    slot?.ApplyContainedItem();
                }
                else
                {
                    var grid = parent.Grids.FirstOrDefault(g => g.ID == p[2]);
                    var at = new LocationInGrid(int.Parse(p[3]), int.Parse(p[4]), (ItemRotation)int.Parse(p[5]));
                    grid?.AddItemWithoutRestrictions(item, at);
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies net: adding {item.LocalizedName()} to {owner.Profile?.Nickname} failed: {e.Message}");
            }
            if (!Headless && item is Weapon w)
            {
                // A gun: what came over (a PaP'd M16's M203 didn't show on the others, user 2026-11-09), and if its
                // owner already holds it here, their hands rebuilt now that it's whole with its models in
                ZombiesDebug.Log($"teammate's {w.LocalizedName()} arrived: {w.GetAllItems().Count()} parts ({string.Join(", ", w.Mods.Select(m => m.ShortName.Localized()))})");
            }
            if (Headless)
            {
                // Its models next, then the owner's game is told it can draw it (UntilHostHas). Told before the models
                // were in, the headless's copy of their hands failed to build ("bundle is not loaded") and later hand
                // changes (a quick knife) hung on the player's game (user, 2026-11-09)
                ZombiesPlugin.Log.LogInfo($"Zombies net (headless): {item.LocalizedName()} added to {owner.Profile?.Nickname} ({p[0]} {p[2]})");
                float from = Time.time;
                yield return ZombiesItems.LoadModels(item);
                string id = item.Id.ToString();
                Send(Msg.ItemHere, w => w.Write(id), always: true);
                ZombiesDebug.Log($"headless: {item.LocalizedName()}'s models in after {Time.time - from:0.0} s, its owner told");
            }
        }

        // A gun still in that player's hands here is never pulled out from under them: their "hands empty" can land
        // after this message (the PaP machine empties the hands, then takes the gun). Taken while held, their copy's hands
        // kept a gun that no longer existed and every later hands move was refused ("Can't draw cause can't find way at
        // graph", every 1-2 s on the headless): the PaP'd M16 vanished on each draw and perk drinks were cut off after
        // 0.6 s (user, 2026-11-09). It waits (up to 5 s) for the hands to let go, then goes.
        private static void ItemRemoveRemote(string from, string itemId)
        {
            Player owner = PlayerOf(from);
            Item item = owner?.InventoryController.Inventory.Equipment.GetAllItems().FirstOrDefault(i => i.Id.ToString() == itemId);
            if (item == null)
                return;
            if (Holds(owner, item))
            {
                ZombiesPlugin.Instance.StartCoroutine(RemoveOnceLetGo(owner, item));
                return;
            }
            RemoveNow(item);
        }

        private static bool Holds(Player owner, Item item)
        {
            Item held = owner?.HandsController?.Item;
            return held != null && (held == item || held.GetAllParentItems().Contains(item) || item.GetAllItems().Contains(held));
        }

        private static System.Collections.IEnumerator RemoveOnceLetGo(Player owner, Item item)
        {
            float until = Time.time + 5f;
            while (owner != null && Holds(owner, item) && Time.time < until)
                yield return null;
            if (owner != null && Holds(owner, item))
            {
                // Still held: their copy's hands emptied here first, so it never holds a gun that's gone
                ZombiesPlugin.Log.LogWarning($"Zombies net: {owner.Profile?.Nickname} still held {item.LocalizedName()} here after 5 s: hands emptied before it goes");
                bool done = false;
                try
                {
                    owner.SetEmptyHands(_ => done = true);
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies net: couldn't empty {owner.Profile?.Nickname}'s hands ({e.Message})");
                    done = true;
                }
                float wait = Time.time + 2f;
                while (!done && Time.time < wait)
                    yield return null;
            }
            RemoveNow(item);
        }

        private static void RemoveNow(Item item)
        {
            if (item?.CurrentAddress?.Container is Slot slot)
            {
                slot.RemoveItemWithoutRestrictions();
                // The body's model of that slot goes too: without this a teammate's old gun stayed drawn on their back
                // or hip on the other games (user, 2026-11-11). EFT's own moves call it; a raw remove doesn't
                slot.ApplyContainedItem();
            }
            else if (item?.CurrentAddress is GridItemAddress grid)
                grid.Grid.RemoveWithoutRestrictions(item);
        }
    }
}
