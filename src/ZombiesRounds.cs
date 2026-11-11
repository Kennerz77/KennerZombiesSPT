using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using Comfort.Common;
using EFT;
using EFT.Game.Spawning;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's rounds in a zombies match: each round has a number of zombies (the original's formula: round 1 solo is 6, more each
    /// round and for each player), spawned at the layout's zombie spawns in open zones near the players (shared out
    /// between them, at most 24 alive at once, the original's pace quickening each round). The zombies are EFT's own infected
    /// (their brain and animations). A zombie far from every player for a while despawns and goes back in the count to
    /// come again near someone. At a barricade's outside a zombie tears the boards off one by one, then climbs through.
    /// Kills are counted by ZombiesStats (points). When a round's zombies are all spawned and dead, the next round starts.
    /// Only the host spawns (Fika sends its bots to the others); solo you're the host.
    /// </summary>
    internal static class ZombiesRounds
    {
        private const float FirstRoundAfter = 2f, RoundBreak = 10f, FarDistance = 60f, FarSeconds = 10f, SpawnerRange = 45f;
        private const float BoardTearSeconds = 1.5f, BarricadeReach = 1.7f, FeedReach = 35f;

        // Each zombie's barricade: it goes there first, tears it down and climbs in, and only then hunts 
        private static readonly Dictionary<BotOwner, ZombiesBarricade> Feeds = new Dictionary<BotOwner, ZombiesBarricade>();
        // The barricades a zombie has already given up on: it never goes back to one (two windows close together had
        // it walking back and forth between them)
        private static readonly Dictionary<BotOwner, HashSet<ZombiesBarricade>> Tried = new Dictionary<BotOwner, HashSet<ZombiesBarricade>>();

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> MaxAlive;
        public static ConfigEntry<string> ZombieType;
        public static ConfigEntry<float> FirstSpawnDelay, WalkSpeed, CounterX, CounterY, CounterSize, Toughness;
        public static ConfigEntry<int> HardestRound;
        public static ConfigEntry<bool> ShowLeft;
        public static ConfigEntry<float> RoundGap, SpawnGrace;

        /// <summary>
        /// The round the zombies are made for: the round itself up to F12's "Hardest round" (25), then held there (the
        /// user, 2026-11-08: the original's curve to round 25, then the game stays as hard and can go on). The original itself has no cap
        /// (the original game 4 brought one in).
        /// </summary>
        public static int Hard(int round) => Math.Min(round, Math.Max(1, HardestRound?.Value ?? 25));
        public static ConfigEntry<bool> TearSwing, VaultAnim;
        public static ConfigEntry<float> BoardSeconds;
        public static ConfigEntry<int> WaddleRounds, SprintersFrom;
        public static ConfigEntry<float> SpeedUp;

        // Each zombie's pace, chosen as it spawns (the original: walkers early, joggers, then runners and sprinters)
        private static readonly Dictionary<BotOwner, float> Pace = new Dictionary<BotOwner, float>();

        public static int Round { get; private set; }
        private static int _toSpawn, _spawnedThisRound, _inFlight, _playerTurn;
        private static float _nextSpawn, _roundStartsAt = -1f, _nextCheck;
        private static readonly Dictionary<BotOwner, float> Alive = new Dictionary<BotOwner, float>(); // zombie -> time it went far
        private static readonly Dictionary<BotOwner, float> NextTear = new Dictionary<BotOwner, float>();
        private static readonly HashSet<BotOwner> ClimbedIn = new HashSet<BotOwner>();

        /// <summary>The zombie is inside the play area: climbed in through its barricade, or spawned inside.</summary>
        public static bool Inside(BotOwner z) => z != null && ClimbedIn.Contains(z);

        /// <summary>The zombie is busy: climbing in, swinging or tearing a board.</summary>
        public static bool IsBusy(BotOwner z) => z != null && Busy.Contains(z);
        private static List<(Vector3 position, Quaternion rotation)> _spawners = new List<(Vector3, Quaternion)>();
        private static GUIStyle _style;

        public static void Bind(ConfigFile config, string section)
        {
            Enabled = config.Bind(section, "Zombies spawn", true, "Rounds of zombies in a zombies match (off: an empty map to look round).");
            MaxAlive = config.Bind(section, "Most zombies at once", 24, new ConfigDescription("", new AcceptableValueRange<int>(1, 60)));
            ZombieType = config.Bind(section, "Zombie bot type", "infectedAssault", "EFT's bot role spawned as zombies (infectedAssault: EFT's infected).");
            // The original: 2 s at round 1, 5% quicker a round (6 s left only 2-6 alive at once even at round 4 and rounds dragged, user
            // 2026-11-09). New key so the old saved 6 isn't kept.
            FirstSpawnDelay = config.Bind(section, "Seconds between spawns, round 1", 2f, new ConfigDescription(
                "Seconds between zombies spawning in round 1; 5% quicker each round after.", new AcceptableValueRange<float>(0.5f, 15f)));
            CounterX = config.Bind("6. Scores on screen", "Round counter across", 0.02f, new ConfigDescription(
                "The round number's gap from the right edge of the screen (it sits in the top right corner).", new AcceptableValueRange<float>(0f, 1f)));
            CounterY = config.Bind("6. Scores on screen", "Round counter up", 0.977f, new ConfigDescription(
                "How high the round number sits, from the bottom (0) to the top (1).", new AcceptableValueRange<float>(0f, 1f)));
            CounterSize = config.Bind("6. Scores on screen", "Round counter size", 1.42f, new ConfigDescription(
                "How big the round number is.", new AcceptableValueRange<float>(0.3f, 3f)));
            ShowLeft = config.Bind("6. Scores on screen", "Show zombies left", true,
                "Under the round number: how many zombies are left this round (alive plus still to come). Handy.");
            SpawnGrace = config.Bind(section, "Wait after round music (s)", 1.5f, new ConfigDescription(
                "No zombies come until the round-start music has finished, plus this many seconds.", new AcceptableValueRange<float>(0f, 10f)));
            OutsideMost = config.Bind(section, "Zombie still outside: comes again after (s)", 75f, new ConfigDescription(
                "A zombie that hasn't climbed in this long after spawning is taken away and spawns again at a way in near the players.",
                new AcceptableValueRange<float>(20f, 600f)));
            LastFewMost = config.Bind(section, "Last two zombies: round moves on after (s)", 90f, new ConfigDescription(
                "With two or fewer zombies left in a round for this long, they die (unless fighting a player right then) and the next round starts.",
                new AcceptableValueRange<float>(20f, 600f)));
            RoundGap = config.Bind(section, "Pause between rounds (s)", 3f, new ConfigDescription(
                "Quiet time after the round-end music finishes before the next round starts.", new AcceptableValueRange<float>(0f, 20f)));
            HardestRound = config.Bind(section, "Hardest round", 25, new ConfigDescription(
                "Zombies stop getting tougher here (the classic numbers up to it): their health, how many come, how fast they spawn and " +
                "walk stay at this round's from then on, so a match can go on as long as you can hold out. The round counter keeps counting.",
                new AcceptableValueRange<int>(5, 100)));
            Toughness = config.Bind(section, "Zombie toughness x", 1f, new ConfigDescription(
                "On top of the health per round (150 at round 1, +100 a round to 9, then 10% more each round).", new AcceptableValueRange<float>(0.1f, 5f)));
            WaddleRounds = config.Bind(section, "Waddle rounds", 5, new ConfigDescription(
                "Rounds of nothing but slow walkers before they start speeding up.", new AcceptableValueRange<int>(1, 30)));
            SpeedUp = config.Bind(section, "Speed-up per round", 0.02f, new ConfigDescription(
                "How much faster (0-1 of EFT's walk) zombies get each round after the waddle.", new AcceptableValueRange<float>(0f, 0.2f)));
            SprintersFrom = config.Bind(section, "Sprinters from round", 18, new ConfigDescription(
                "The round some zombies start sprinting (the later rounds).", new AcceptableValueRange<int>(2, 60)));
            BoardSeconds = config.Bind(section, "Seconds per board torn off", 3f, new ConfigDescription(
                "How long a barricade holds per board: one board comes off every this many seconds, however many zombies are at it.",
                new AcceptableValueRange<float>(0.5f, 10f)));
            TearSwing = config.Bind(section, "Zombies swing at boards", true, "A zombie at a barricade swings its arms (EFT's infected attack) and the board comes off on the hit.");
            VaultAnim = config.Bind(section, "Zombies use the vault animation", true,
                "Climbing in over a barricade plays EFT's own vault animation on the body (off: the body just follows the path).");
            WalkSpeed = config.Bind(section, "Walker speed", 0.131f, new ConfigDescription(
                "How fast the slowest zombies walk (0-1 of EFT's walk); joggers and runners come in with the rounds.", new AcceptableValueRange<float>(0.05f, 1f)));
        }

        /// <summary>A new match: round 1 starts a moment after the players are in.</summary>
        public static void Start(IEnumerable<MapEditor.Entry> entries)
        {
            Clear();
            SpawnZones.Clear();
            var spawns = entries.Where(e => e.Type == MapEditor.Kind.ZombieSpawn).ToList();
            _spawners = spawns.Select(e => (MapEditor.Pos(e), MapEditor.Rotation(e))).ToList();
            // A zone picked in F8 wins over where the spawn stands
            foreach (MapEditor.Entry e in spawns.Where(e => e.Zone > 0))
                SpawnZones[MapEditor.Pos(e)] = e.Zone;
            Round = 0;
            _roundStartsAt = Time.time + FirstRoundAfter;
            ZombiesPlugin.Log.LogInfo($"Zombies rounds: {_spawners.Count} zombie spawns, round 1 in {FirstRoundAfter:0} s");
        }

        public static void Clear()
        {
            foreach (BotOwner z in Alive.Keys.ToList())
                Despawn(z);
            Alive.Clear();
            FeedRoute.Clear();
            ZombiesTrails.Clear();
            ClimbFree.Clear();
            GroundDrop.Clear();
            GroundOut.Clear();
            NextHunt.Clear();
            HuntQueue.Clear();
            _players = new List<Player>();
            NextTear.Clear();
            NextSlap.Clear();
            Unreach.Clear();
            NextThrow.Clear();
            NextThrowAt.Clear();
            Pace.Clear();
            Rising.Clear();
            Busy.Clear();
            HeldUntil.Clear();
            Feeds.Clear();
            LoggedWeapons.Clear();
            Progress.Clear();
            NextReroute.Clear();
            Tried.Clear();
            LastPathComplete.Clear();
            SpawnLeads.Clear();
            NoPathLogged.Clear();
            ZombiesHealth.Clear();
            ZombiesWidow.Clear();
            LastGoal.Clear();
            ShambleRest.Clear();
            ClimbedIn.Clear();
            Round = 0;
            _toSpawn = _spawnedThisRound = _inFlight = 0;
            PendingTo.Clear();
            HiddenSince.Clear();
            _leftFromHost = _leftSent = -1;
            _roundStartsAt = -1f;
        }

        /// <summary>the original's zombies in a round (zombie_max_ai 24, 6 more per extra player, early rounds cut down).</summary>
        public static int ZombiesInRound(int round, int players)
        {
            float multiplier = Mathf.Max(1f, round / 5f);
            if (round >= 10)
                multiplier *= round * 0.15f;
            float max = 24f + (players <= 1 ? 0.5f * 6f * multiplier : (players - 1) * 6f * multiplier);
            float early = round switch { 1 => 0.25f, 2 => 0.3f, 3 => 0.5f, 4 => 0.7f, 5 => 0.9f, _ => 1f };
            return Mathf.Max(1, Mathf.FloorToInt(max * early));
        }

        // The original's spawn pace: slow at first, 5% quicker each round, at least 0.08 s
        private static float SpawnDelay => Mathf.Max(0.08f, (FirstSpawnDelay?.Value ?? 2f) * Mathf.Pow(0.95f, Hard(Round) - 1));

        // A new zombie's pace: every zombie waddles for the first rounds, then they all get a little faster each round,
        // close together (a narrow spread, no sudden runners mixed in); some sprint only from late rounds
        private static float PickPace()
        {
            if (ZombiesExfil.Swarming && ZombiesExfil.SwarmSprint)
                return 1.4f;
            float walk = WalkSpeed?.Value ?? 0.1f;
            int waddle = WaddleRounds?.Value ?? 5;
            int r = Hard(Round); // held at "Hardest round"
            float spread = UnityEngine.Random.Range(0.93f, 1.07f);
            if (r <= waddle)
                return walk * spread;
            float pace = Mathf.Min(1f, (walk + (r - waddle) * (SpeedUp?.Value ?? 0.02f)) * spread);
            if (r >= (SprintersFrom?.Value ?? 18) && UnityEngine.Random.value < Mathf.Min(0.5f, 0.15f + (r - (SprintersFrom?.Value ?? 18)) * 0.03f))
                return 1.4f;
            return pace;
        }

        // EFT's infected brain sets its own speed and sprints every frame (a walker ran): our zombies keep their pace
        private static bool _pacePatched;

        public static void PatchPace(Harmony harmony)
        {
            if (_pacePatched)
                return;
            _pacePatched = true;
            harmony.Patch(AccessTools.Method(typeof(BotMover), nameof(BotMover.SetTargetMoveSpeed), new[] { typeof(float) }),
                prefix: new HarmonyMethod(typeof(ZombiesRounds), nameof(KeepSpeed)));
            harmony.Patch(AccessTools.Method(typeof(BotMover), nameof(BotMover.Sprint)),
                prefix: new HarmonyMethod(typeof(ZombiesRounds), nameof(KeepSprint)));
            // Only our orders move a zombie: EFT's infected brain no longer routes it on its own (dodging shots, side
            // steps, wandering), so it comes straight on along its route
            var onlyOurs = new HarmonyMethod(typeof(ZombiesRounds), nameof(OnlyOurRoute));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(BotMover)))
                if (m.Name == nameof(BotMover.GoToPoint) || m.Name == nameof(BotMover.CurrentStateGoToPoint))
                    harmony.Patch(m, prefix: onlyOurs);
            harmony.Patch(AccessTools.Method(typeof(BotMover), nameof(BotMover.GoToByWay)),
                prefix: new HarmonyMethod(typeof(ZombiesRounds), nameof(OnlyOurWay)));
            var way = new HarmonyMethod(typeof(ZombiesRounds), nameof(OnlyOurWay));
            var noWay = AccessTools.Method(typeof(BotMover), nameof(BotMover.GoToPointNoWay));
            if (noWay != null)
                harmony.Patch(noWay, prefix: way);
            // The bots' own mover overrides GoToByWay: EFT's infected brain walked them through that, straight at the
            // player (into walls) once a window was open. Every override blocked the same way.
            int overrides = 0;
            foreach (Type t in AccessTools.AllTypes().Where(t => t != typeof(BotMover) && typeof(BotMover).IsAssignableFrom(t)))
                foreach (var m in AccessTools.GetDeclaredMethods(t).Where(m => m.Name == nameof(BotMover.GoToByWay) || m.Name == nameof(BotMover.GoToPointNoWay)))
                {
                    harmony.Patch(m, prefix: way);
                    overrides++;
                }
            ZombiesPlugin.Log.LogInfo($"Zombies rounds: zombie moves only ours ({overrides} mover override(s) blocked too)");
            // Only our facing too: EFT's infected brain turned them to where they walk every frame while the hunt
            // turned them to the player every 0.15 s, a fight that read as twitching, side-stepping, rugged walking
            // (the user, 2026-11-08). The two-argument forms are what every look order ends in.
            var onlyOurLook = new HarmonyMethod(typeof(ZombiesRounds), nameof(OnlyOurLook));
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(BotSteering)).Where(m =>
                         (m.Name == nameof(BotSteering.LookToPoint) || m.Name == nameof(BotSteering.LookToMovingDirection)
                          || m.Name == nameof(BotSteering.LookToDirection) || m.Name == nameof(BotSteering.LookToPathDestPoint))
                         && m.GetParameters().Any(x => x.ParameterType == typeof(float))))
                harmony.Patch(m, prefix: onlyOurLook);
        }

        private static bool _ourLook;

        private static bool OnlyOurLook(BotSteering __instance) =>
            _ourLook || __instance?._owner == null || !Alive.ContainsKey(__instance._owner);

        /// <summary>Our zombie turned to look at a point (the only look orders it takes).</summary>
        public static void Face(BotOwner z, Vector3 point)
        {
            _ourLook = true;
            try { z?.Steering?.LookToPoint(point); }
            catch { }
            finally { _ourLook = false; }
        }

        /// <summary>Our zombie facing the way it walks (its route).</summary>
        private static void FaceAlong(BotOwner z)
        {
            _ourLook = true;
            try { z?.Steering?.LookToMovingDirection(); }
            catch { }
            finally { _ourLook = false; }
        }

        // In with the players, a zombie faces the one it's after only this close (or on a clear straight line at them);
        // further off it faces the way its route goes, so it walks round corners instead of side-stepping
        private const float FaceWithin = 4f;

        private static bool _ourMove;

        private static bool OnlyOurRoute(BotMover __instance, ref UnityEngine.AI.NavMeshPathStatus __result)
        {
            if (_ourMove || __instance?._owner == null || !Alive.ContainsKey(__instance._owner))
                return true;
            __result = UnityEngine.AI.NavMeshPathStatus.PathComplete;
            return false;
        }

        private static bool OnlyOurWay(BotMover __instance) =>
            _ourMove || __instance?._owner == null || !Alive.ContainsKey(__instance._owner);

        private static void KeepSpeed(BotMover __instance, ref float target)
        {
            if (__instance?._owner != null && Pace.TryGetValue(__instance._owner, out float pace))
                target = Mathf.Min(1f, pace) * PaceFactor(__instance._owner) * (ZombiesWidow.Webbed(__instance._owner) ? ZombiesWidow.Slow : 1f);
        }

        private static void KeepSprint(BotMover __instance, ref bool val)
        {
            if (__instance?._owner != null && Pace.TryGetValue(__instance._owner, out float pace))
                val = pace > 1f && !ZombiesWidow.Webbed(__instance._owner) && PaceFactor(__instance._owner) >= 1f;
        }

        // After a swing (a slap or a board ripped) the zombie is held where it stands until the swing's animation is over,
        // then its pace eases back in: it used to walk on mid-swing and slide out of it at full pace
        private const float SlapHold = 1.05f, RipHold = 0.8f, PaceEaseIn = 0.6f;
        private static readonly Dictionary<BotOwner, float> HeldUntil = new Dictionary<BotOwner, float>();

        private static float PaceFactor(BotOwner z)
        {
            if (!HeldUntil.TryGetValue(z, out float until))
                return 1f;
            float since = Time.time - until;
            if (since < 0f)
                return 0f;
            if (since >= PaceEaseIn)
            {
                HeldUntil.Remove(z);
                return 1f;
            }
            return since / PaceEaseIn;
        }

        private static void Hold(BotOwner z, float seconds)
        {
            HeldUntil[z] = Time.time + seconds;
            try
            {
                z.Mover?.Stop();
                z.Sprint(false);
                z.SetTargetMoveSpeed(0f);
            }
            catch
            {
            }
        }

        /// <summary>The zombies, for the log: a line, or (full) a line on each one.</summary>
        // EFT's infected brain moves its bots at full run whatever target speed they're given (the 2026-11-08 speed log:
        // round 1 walkers at pace 0.11 going 4.3-4.7 m/s): the pace is held on the body itself, with EFT's own state
        // speed limit (the barbed-wire one: our zombies never walk on wire), and no sprinting unless it's a sprinter
        private static void CapSpeed(BotOwner z, Player body)
        {
            try
            {
                if (body?.MovementContext == null || !Pace.TryGetValue(z, out float pace))
                    return;
                bool sprinter = pace > 1f && !ZombiesWidow.Webbed(z) && PaceFactor(z) >= 1f;
                float limit = sprinter ? 1f : Mathf.Clamp01(Mathf.Min(1f, pace) * PaceFactor(z) * (ZombiesWidow.Webbed(z) ? ZombiesWidow.Slow : 1f));
                var mc = body.MovementContext;
                mc.RemoveStateSpeedLimit(Player.ESpeedLimit.BarbedWire);
                if (!sprinter)
                {
                    mc.AddStateSpeedLimit(Mathf.Max(0.01f, limit), Player.ESpeedLimit.BarbedWire);
                    if (body.Physical != null && body.Physical.Sprinting)
                        mc.EnableSprint(false);
                }
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"zombie speed cap failed: {e.Message}");
            }
        }

        // Each zombie's real speed over the ground (m/s), measured every couple of seconds
        private static readonly Dictionary<BotOwner, (Vector3 at, float t, float speed)> Speed = new Dictionary<BotOwner, (Vector3, float, float)>();

        private static void MeasureSpeed(BotOwner z, Vector3 at)
        {
            if (!Speed.TryGetValue(z, out var s))
            {
                Speed[z] = (at, Time.time, 0f);
                return;
            }
            float dt = Time.time - s.t;
            if (dt < 2f)
                return;
            Vector3 d = at - s.at;
            d.y = 0f;
            Speed[z] = (at, Time.time, d.magnitude / dt);
        }

        public static string Describe(bool full)
        {
            var s = new System.Text.StringBuilder();
            float now = Time.time;
            List<BotOwner> alive = Alive.Keys.Where(z => z != null).ToList();
            s.Append($"round {Round}: {alive.Count} alive ({alive.Count(Rising.Contains)} rising, {alive.Count(Busy.Contains)} busy (swinging or climbing), " +
                     $"{alive.Count(z => HeldUntil.TryGetValue(z, out float u) && now < u)} held after a swing, {alive.Count(ZombiesWidow.Webbed)} webbed, " +
                     $"{alive.Count(ClimbedIn.Contains)} climbed in), {_spawnedThisRound}/{_toSpawn} spawned, {_inFlight} on the way");
            if (_roundStartsAt > 0f)
                s.Append($", next round in {_roundStartsAt - now:0} s");
            // How fast they really move (walking ones only: rising/busy ones stand still)
            Player meNow = Singleton<GameWorld>.Instance?.MainPlayer;
            var moving = alive.Where(z => !Rising.Contains(z) && !Busy.Contains(z) && Speed.TryGetValue(z, out var v) && v.speed > 0.05f).ToList();
            if (moving.Count > 0)
                s.Append(" | speeds " + string.Join(", ", moving.Take(8).Select(z => $"{Speed[z].speed:0.00} m/s" +
                    (meNow != null ? $" @{Vector3.Distance(meNow.Transform.position, z.Position):0} m" : "") + (ClimbedIn.Contains(z) ? " in" : " out")
                    + $" (pace {(Pace.TryGetValue(z, out float pz) ? pz : 0f):0.00})")));
            if (!full)
                return s.ToString();
            s.AppendLine();
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            int i = 0;
            foreach (BotOwner z in alive)
            {
                i++;
                try
                {
                    Vector3 p = z.Position;
                    string state = Rising.Contains(z) ? "rising" : Busy.Contains(z) ? "busy" : ZombiesWidow.Webbed(z) ? "webbed" : "hunting";
                    float pace = Pace.TryGetValue(z, out float pc) ? pc : -1f;
                    s.AppendLine($"  zombie {i}: {p} zone {ZombiesZones.ZoneAt(p)}, {state}, pace {pace:0.00} x{PaceFactor(z):0.00}, " +
                                 $"{(me != null ? Vector3.Distance(me.Transform.position, p).ToString("0.0") + " m from me" : "")}, " +
                                 $"{(ClimbedIn.Contains(z) ? "inside" : "outside")}, far for {(Alive.TryGetValue(z, out float far) && far > 0f ? now - far : 0f):0} s, " +
                                 $"speed {z.GetPlayer?.MovementContext?.CharacterMovementSpeed ?? -1f:0.00}");
                }
                catch (Exception e)
                {
                    s.AppendLine($"  zombie {i}: unreadable ({e.Message})");
                }
            }
            return s.ToString();
        }

        /// <summary>The zombies alive now (Widow's Wine's web).</summary>
        public static IEnumerable<BotOwner> Zombies => Alive.Keys;

        public static void Tick()
        {
            if (Enabled?.Value != true || !ZombiesMatch.Active || !ZombiesPlugin.RaidStarted || !IsHost() || ZombiesPause.Paused || ZombiesGameOver.Active)
                return;
            // The zombies-left count to the team (from here, not the HUD: a Fika headless draws nothing)
            SendLeft();
            if (Time.time >= _nextStrayCheck)
            {
                _nextStrayCheck = Time.time + 1f;
                RemoveStrays();
            }
            // Round 1 counts down from the moment the player has the pistol (the original: 10 s after you're in)
            if (Round == 0 && !ZombiesMatch.LoadedIn)
                _roundStartsAt = Time.time + FirstRoundAfter;
            // Between rounds
            if (_roundStartsAt > 0f)
            {
                if (Time.time >= _roundStartsAt)
                    NextRound();
                return;
            }
            if (Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + 0.5f;
                CheckZombies();
            }
            // The original hunting: trails recorded every frame, and the ones in with the players looked at a few a frame
            // (each about every 0.15 s) instead of all together every 0.5 s
            ZombiesTrails.Record(_players);
            HuntSome();
            // The exfil swarm (lever pulled until the exfil closes): they never stop coming, quicker and more at once,
            // and the round can't end meanwhile
            bool swarm = ZombiesExfil.Swarming;
            if (swarm && !_wasSwarm)
                _swarmBase = _spawnedThisRound;
            _wasSwarm = swarm;
            int cap = swarm ? ZombiesExfil.SwarmMax : MaxAlive.Value;
            if ((swarm || _spawnedThisRound + _inFlight < _toSpawn) && Alive.Count + _inFlight < cap && Time.time >= _nextSpawn)
            {
                _nextSpawn = Time.time + SpawnDelay * (swarm ? ZombiesExfil.SwarmDelay : 1f);
                SpawnOne();
            }
            // The round's last couple taking too long (lost, stuck, out of reach): they die and the round moves on
            int stillToCome = Math.Max(0, _toSpawn - _spawnedThisRound) + Alive.Count + _inFlight;
            if (!swarm && _toSpawn > 0 && _roundStartsAt < 0f && stillToCome > 0 && stillToCome <= 2)
            {
                if (_fewSince < 0f)
                    _fewSince = Time.time;
                else if (Time.time - _fewSince > (LastFewMost?.Value ?? 90f))
                {
                    int killed = 0;
                    foreach (BotOwner z in Alive.Keys.ToList())
                    {
                        Player body = z?.GetPlayer;
                        if (body == null)
                            continue;
                        // One in a fight with a player right now is left to them
                        if (_players.Any(p => p != null && Vector3.Distance(p.Transform.position, z.Position) < 6f))
                            continue;
                        body.ActiveHealthController?.Kill(EDamageType.Undefined);
                        killed++;
                    }
                    _spawnedThisRound = _toSpawn;
                    ZombiesPlugin.Log.LogInfo($"Zombies rounds: round {Round}'s last {stillToCome} took over {LastFewMost?.Value ?? 90f:0} s: {killed} put down, the round moves on");
                    _fewSince = -1f;
                }
            }
            else
                _fewSince = -1f;
            if (!swarm && _spawnedThisRound >= _toSpawn && _inFlight == 0 && Alive.Count == 0)
            {
                ZombiesPlugin.Log.LogInfo($"Zombies rounds: round {Round} done");
                // The original: the round end music plays out, then the next round starts as it ends
                // The original: the round end music plays out, then a short quiet, then the next round (user 2026-11-09: a longer pause)
                _roundStartsAt = Time.time + ZombiesRoundFx.RoundEnded() + (RoundGap?.Value ?? 3f);
                // The others' counters flash and their round end music plays too (they only heard round starts)
                ZombiesNet.RoundEnd(Round);
            }
        }

        /// <summary>The round the exits open from (the basement exfil, ZB-013).</summary>
        public const int ExfilRound = 15;

        /// <summary>Time stood still (a pause): the round's timers move on by it.</summary>
        public static void ShiftTimers(float seconds)
        {
            if (_roundStartsAt > 0f)
                _roundStartsAt += seconds;
            _nextSpawn += seconds;
            _nextCheck += seconds;
        }

        private static float _nextStrayCheck;
        private static readonly Dictionary<string, float> StraySeen = new Dictionary<string, float>();

        // EFT's own bot spawning (scavs, raiders...) finds the map's bot zone and fills it; on a zombies map only our
        // infected belong: any other bot is taken off the map as soon as it shows up
        private static void RemoveStrays()
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            foreach (Player p in world.AllAlivePlayersList.Where(x => x != null && x.IsAI).ToList())
            {
                string role = p.Profile?.Info?.Settings?.Role.ToString() ?? "";
                if (role.StartsWith("infected", StringComparison.OrdinalIgnoreCase))
                    continue;
                BotOwner bot = p.AIData?.BotOwner;
                // Not the moment it shows up: taken off right after spawning, it was still in EFT's waiting list of new
                // bots, which then threw "An item with the same key has already been added: BotN" every frame on the
                // headless (~59/s) and kept every later bot out of that list (user's test, 2026-11-09). Its first 4 s
                // (and until EFT has it active) it's left alone
                if (!StraySeen.TryGetValue(p.ProfileId, out float seen))
                {
                    StraySeen[p.ProfileId] = Time.time;
                    continue;
                }
                if (Time.time - seen < 4f || (bot != null && bot.BotState != EBotState.Active))
                    continue;
                StraySeen.Remove(p.ProfileId);
                ZombiesPlugin.Log.LogInfo($"Zombies rounds: a {role} bot isn't a zombie: taken off the map");
                try
                {
                    if (bot != null)
                        bot.LeaveData.RemoveFromMap();
                    else
                        p.ActiveHealthController?.Kill(EDamageType.Undefined);
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies rounds: couldn't remove a {role} bot ({e.Message})");
                }
            }
        }

        public static void NextRound()
        {
            Round++;
            int players = Math.Max(1, ZombiesTeam.List().Count);
            _toSpawn = ZombiesInRound(Hard(Round), players);
            _spawnedThisRound = 0;
            _roundStartsAt = -1f;
            // No zombies until the round-start music is done, plus a short grace (user 2026-11-09)
            _nextSpawn = Time.time + ZombiesRoundFx.StartLength + (SpawnGrace?.Value ?? 1.5f);
            ZombiesBarricade.NewRound();
            NewRoundHere();
            ZombiesNet.Round(Round);
            ZombiesRoundFx.RoundStarted(Round);
            ZombiesPlugin.Log.LogInfo($"Zombies rounds: round {Round}, {_toSpawn} zombies ({players} player{(players == 1 ? "" : "s")})");
            TopUpProfiles(_toSpawn);
        }

        // ---------- Spawning ----------

        /// <summary>the original's zombie health for a round.</summary>
        public static float BoHealth(int round) => (round = Hard(round)) < 10 ? 150f + 100f * (Mathf.Max(1, round) - 1) : 950f * Mathf.Pow(1.1f, round - 9);


        /// <summary>A client: the host's round (its number on screen, and what a new round brings here).</summary>
        public static void RoundFromHost(int round)
        {
            if (ZombiesNet.IsHost || round == Round)
                return;
            bool next = round > Round;
            Round = round;
            _roundStartsAt = -1f;
            if (next)
            {
                ZombiesBarricade.NewRound();
                NewRoundHere();
                ZombiesRoundFx.RoundStarted(Round);
            }
        }

        /// <summary>A client: the host's round is over (its counter flashes, the round end music plays).</summary>
        public static void RoundEndFromHost(int round)
        {
            if (ZombiesNet.IsHost || round != Round)
                return;
            ZombiesRoundFx.RoundEnded();
        }

        // Zombies left this round: alive now plus still to come (the host's own count; a Fika client gets the host's)
        private static int _leftFromHost = -1, _leftSent = -1;
        private static float _leftSentAt;

        public static int Left => ZombiesNet.IsHost ? Math.Max(0, _toSpawn - _spawnedThisRound) + Alive.Count : _leftFromHost;

        /// <summary>A client: the host's zombies-left count.</summary>
        public static void LeftFromHost(int left)
        {
            if (!ZombiesNet.IsHost)
                _leftFromHost = left;
        }

        // Host: the count to the team when it changes (at most twice a second)
        private static void SendLeft()
        {
            if (!ZombiesNet.IsHost)
                return;
            int left = Left;
            if (left == _leftSent || Time.unscaledTime - _leftSentAt < 0.5f)
                return;
            _leftSent = left;
            _leftSentAt = Time.unscaledTime;
            ZombiesNet.Left(left);
        }

        // What each player's game does as a round starts: grenades back, the power-up count, the dead come back
        private static void NewRoundHere()
        {
            // The basement exfil (ZB-013) opens from round 15
            if (ZombiesExfil.Present)
                ZombiesExfil.RoundStarted(Round);
            else if (Round >= ExfilRound && !ZombiesPower.ExfilsOpen)
                ZombiesPower.OpenExfils();
            if (Round > 1)
                ZombiesGrenades.NewRound();
            ZombiesPowerUps.NewRound();
            ZombiesLitter.NewRound();
            if (Singleton<GameWorld>.Instance?.MainPlayer is Player me && ZombiesDowned.Mine == ZombiesDowned.State.Dead)
                ZombiesDowned.Respawn(me);
        }

        private static bool IsHost()
        {
            if (!ZombiesNet.IsHost)
                return false;
            // Solo, or Fika's host: the one whose game runs the bots
            return Singleton<IBotGame>.Instantiated && Singleton<IBotGame>.Instance.BotsController?.BotSpawner != null;
        }

        private static void SpawnOne()
        {
            var players = ZombiesTeam.List().Where(p => p != null && p.HealthController.IsAlive).ToList();
            var open = _spawners.Where(s => ZombiesZones.IsOpen(SpawnZone(s.position))).ToList();
            if (players.Count == 0 || open.Count == 0)
                return;
            // No zones on the map: only spawns whose way in leads to a player on foot (a window into a room still shut
            // behind a door or debris doesn't feed yet); none at all (a navmesh gap) and every open one is used
            var leading = open.Where(s => Leads(s.position, players)).ToList();
            if (leading.Count > 0)
                open = leading;
            // Shared out between the players: each in turn gets one
            Player who = players[_playerTurn++ % players.Count];
            // Spread over the windows (the original: every open barricade gets worked, solo too): the open window with the fewest
            // zombies on their way to it (ties: the one nearest the player whose turn it is), then one of its own spawns.
            // Spawns that feed no window (inside ones) count as a "window" of their own.
            var byWindow = open.GroupBy(s => FeedFor(s.position, FeedReach)).ToList();
            var pick = byWindow
                .OrderBy(g => g.Key == null ? 0 : HeadedTo(g.Key))
                .ThenBy(g => g.Min(s => Vector3.Distance(s.position, who.Transform.position)))
                .First();
            // The exfil swarm floods the hold area: the ways in nearest it take them (the two nearest, in turn), not a
            // spread over every window (user, 2026-11-10)
            Vector3? hold = ZombiesExfil.Swarming ? ZombiesExfil.HoldCentre : null;
            if (hold.HasValue)
            {
                var near = byWindow.Where(g => g.Key != null).OrderBy(g => Vector3.Distance(g.Key.transform.position, hold.Value)).Take(2).ToList();
                if (near.Count > 0)
                    pick = near.OrderBy(g => HeadedTo(g.Key)).First();
            }
            var spots = pick.OrderBy(s => Vector3.Distance(s.position, who.Transform.position)).Take(3).ToList();
            var at = spots[UnityEngine.Random.Range(0, spots.Count)];
            ZombiesDebug.Log($"spawn: barricade {(pick.Key != null ? pick.Key.Entry.ToString() : "none")} ({(pick.Key != null ? HeadedTo(pick.Key) : 0)} already on their way, {byWindow.Count} ways in open)");
            Spawn(at.position, at.rotation, pick.Key);
        }

        // How many zombies alive outside are headed for this barricade (not climbed in yet)
        // (spawns asked for and not here yet count too: on a Fika headless a bot takes seconds to come, and every
        // request in between had picked the same barricade)
        private static int HeadedTo(ZombiesBarricade b) => Feeds.Count(kv => kv.Value == b && kv.Key != null && !ClimbedIn.Contains(kv.Key) && Alive.ContainsKey(kv.Key))
            + (b != null && PendingTo.TryGetValue(b, out int n) ? n : 0);

        // Spawns asked for and not here yet, by the barricade they'll feed
        private static readonly Dictionary<ZombiesBarricade, int> PendingTo = new Dictionary<ZombiesBarricade, int>();

        // Its place in the queue outside its barricade: the first three side by side at the boards, the rest in rows
        // behind them; the queue moves up as zombies climb in
        private static Vector3 QueueSpot(BotOwner z, ZombiesBarricade b)
        {
            var queue = Feeds.Where(kv => kv.Value == b && kv.Key != null && !ClimbedIn.Contains(kv.Key) && Alive.ContainsKey(kv.Key))
                .Select(kv => kv.Key).OrderBy(o => o.Id).ToList();
            int k = Mathf.Max(0, queue.IndexOf(z));
            float side = (k % 3 - 1) * 0.65f, back = 0.8f + (k / 3) * 0.9f;
            return OutsideGround(b, b.transform.position + b.transform.forward * back + b.transform.right * side);
        }

        // ---------- Which window a spawn feeds ----------

        // The barricade a zombie from this spawn goes in by: the nearest within reach whose outside faces the spawn,
        // at about the spawn's own height (a yard's spawn never takes the window a floor up)
        private const float FeedHeight = 2.5f;

        // A barricade higher or lower than FeedHeight still takes a spawn whose zombies can walk to its outside (a ramp
        // up to an upstairs window: zm_stronghold #8, 2026-11-08); worked out once per spawn and barricade
        private static readonly Dictionary<(Vector3, int), bool> FeedRoute = new Dictionary<(Vector3, int), bool>();

        private static ZombiesBarricade FeedFor(Vector3 spawn, float reach) =>
            ZombiesBarricade.Nearest(spawn, reach, b => Vector3.Dot(spawn - b.transform.position, b.transform.forward) > 0f
                                                       && (Mathf.Abs(spawn.y - b.transform.position.y) <= FeedHeight || WalksTo(spawn, b)));

        private static bool WalksTo(Vector3 spawn, ZombiesBarricade b)
        {
            var key = (spawn, b.GetInstanceID());
            if (FeedRoute.TryGetValue(key, out bool ok))
                return ok;
            ok = Reaches(spawn, b.transform.position + b.transform.forward * 1.2f);
            FeedRoute[key] = ok;
            ZombiesPlugin.Log.LogInfo($"Zombies rounds: spawn at {spawn} {(ok ? "walks" : "can't walk")} to barricade {b.Entry} ({b.transform.position.y - spawn.y:+0.0;-0.0} m up/down)");
            return ok;
        }

        private static readonly Dictionary<Vector3, (bool ok, float until)> SpawnLeads = new Dictionary<Vector3, (bool, float)>();

        // A spawn leads to a player: from just inside its window (or the spawn itself, with none), a walkable route on
        // the navmesh as it stands now (shut doors and debris cut out of it) to any player. Checked every 3 s at most.
        // A map with zones is gated by them instead.
        private static bool Leads(Vector3 spawn, List<Player> players)
        {
            if (ZombiesZones.Any)
                return true;
            if (SpawnLeads.TryGetValue(spawn, out var known) && Time.time < known.until)
                return known.ok;
            ZombiesBarricade feeds = FeedFor(spawn, FeedReach);
            Vector3 from = feeds != null ? feeds.transform.position - feeds.transform.forward * 1.5f : spawn;
            bool ok = players.Any(p => Reaches(from, p.Transform.position));
            if (!SpawnLeads.TryGetValue(spawn, out var before) || before.ok != ok)
                ZombiesDebug.Log($"spawn at {spawn} {(ok ? "leads to a player" : "doesn't lead to any player yet")} (way in: {(feeds != null ? "barricade " + feeds.Entry : "none")})");
            SpawnLeads[spawn] = (ok, Time.time + 3f);
            return ok;
        }

        // A zombie spawn's zone: the zone it's in; outside every zone (a yard), the zone of the barricade it feeds (the
        // nearest within 15 m whose outside faces it, by the room just inside that barricade)
        private static readonly Dictionary<Vector3, int> SpawnZones = new Dictionary<Vector3, int>();

        private static int SpawnZone(Vector3 spawn)
        {
            if (SpawnZones.TryGetValue(spawn, out int known))
                return known;
            int zone = ZombiesZones.ZoneAt(spawn);
            if (zone == 0)
            {
                ZombiesBarricade feeds = FeedFor(spawn, 15f);
                if (feeds != null)
                    zone = feeds.Zone > 0 ? feeds.Zone : ZombiesZones.ZoneAt(feeds.transform.position - feeds.transform.forward * 1.5f);
            }
            SpawnZones[spawn] = zone;
            ZombiesPlugin.Log.LogInfo($"Zombies rounds: zombie spawn at {spawn} is zone {(zone == 0 ? (ZombiesZones.Any ? "none (never spawns)" : "none (no zones: always on)") : zone.ToString())}");
            return zone;
        }

        private static BotZone _zone;

        private static async void Spawn(Vector3 position, Quaternion rotation, ZombiesBarricade target)
        {
            // On the way until the bot is really here (EFT's spawn calls back later): counted against the round's
            // total and its barricade till then, or 30 s at most (a spawn EFT never finishes)
            _inFlight++;
            if (target != null)
                PendingTo[target] = (PendingTo.TryGetValue(target, out int had) ? had : 0) + 1;
            bool released = false;
            void Release()
            {
                if (released)
                    return;
                released = true;
                _inFlight = Mathf.Max(0, _inFlight - 1);
                if (target != null && PendingTo.TryGetValue(target, out int n))
                    PendingTo[target] = Mathf.Max(0, n - 1);
            }
            bool asked = false;
            try
            {
                BotsController bots = Singleton<IBotGame>.Instance.BotsController;
                BotSpawner spawner = bots.BotSpawner;
                // Found once (a scene search on every spawn cost ~40 ms each: a hitch per zombie)
                if (_zone == null)
                    _zone = UnityEngine.Object.FindObjectsOfType<BotZone>().FirstOrDefault();
                BotZone zone = _zone;
                if (zone == null)
                {
                    ZombiesPlugin.Log.LogWarning("Zombies rounds: the map has no bot zone to spawn in");
                    return;
                }
                if (!Enum.TryParse(ZombieType.Value, true, out WildSpawnType role))
                    role = WildSpawnType.infectedAssault;
                // EFT's zombie profiles run low over a round and it hands back one it has already used: it drops that
                // spawn without a word ("generate profile with same id" in its own error log) and the round sat 30 s
                // waiting for a zombie that never came, again and again (user, 2026-11-10: round 4's long wait for the
                // last zombie). Another profile is tried at once, EFT is asked for more, and a spawn with none is
                // let go straight away
                BotCreationData data = null;
                for (int tries = 0; tries < 6; tries++)
                {
                    data = await BotCreationData.Create(new GetProfileDataParams(EPlayerSide.Savage, role, BotDifficulty.normal, 0f), spawner._botCreator, 1, spawner);
                    if (data?.Profiles != null && data.Profiles.Any(pr => pr != null))
                        break;
                    if (tries == 0)
                        TopUpProfiles(10);
                    data = null;
                    await System.Threading.Tasks.Task.Delay(150);
                }
                if (data == null)
                {
                    ZombiesPlugin.Log.LogWarning("Zombies rounds: EFT had no unused zombie profile after 6 tries: this spawn is let go (the next one tries again)");
                    return;
                }
                var point = new SpawnPoint
                {
                    Id = Guid.NewGuid().ToString(), Name = "zm_spawn", Position = position, Rotation = rotation,
                    Sides = EPlayerSideMask.All, Categories = ESpawnCategoryMask.All, BotZone = zone, DelayToCanSpawnSec = 0f,
                };
                spawner.SpawnBotsInZoneOnPositions(new List<ISpawnPoint> { point }, zone, data, owner =>
                {
                    Release();
                    Alive[owner] = -1f;
                    SpawnedAt[owner] = Time.time;
                    if (ZombiesExfil.Swarming)
                        SwarmSpawned.Add(owner);
                    Pace[owner] = PickPace();
                    // No weapon in sight from the first frame (the original's zombies are empty-handed)
                    HideWeapons(owner);
                    // Its way in: the barricade facing its spawn (outside it), or none (it spawned inside: hunts at once)
                    ZombiesBarricade feeds = FeedFor(position, FeedReach);
                    if (feeds != null)
                        Feeds[owner] = feeds;
                    else
                        ClimbedIn.Add(owner);
                    ZombiesDebug.Log($"zombie's way in: {(feeds != null ? $"barricade {feeds.Entry} at {feeds.transform.position} ({Vector3.Distance(feeds.transform.position, position):0.0} m)" : "none within " + FeedReach + " m, hunts at once")}");
                    ZombiesPlugin.Instance.StartCoroutine(Rise(owner, position));
                    _spawnedThisRound++;
                    ZombiesDebug.Log($"zombie spawned at {position} ({Alive.Count} alive, {_spawnedThisRound}/{_toSpawn})");
                });
                asked = true;
                ZombiesPlugin.Instance.StartCoroutine(ReleaseLater(30f, Release));
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies rounds: spawn failed: {e.Message}");
            }
            finally
            {
                if (!asked)
                    Release();
            }
        }

        // Asks EFT's bot profile store to keep this many more zombie profiles ready (fetched from the server)
        private static void TopUpProfiles(int count)
        {
            try
            {
                if (!Enum.TryParse(ZombieType.Value, true, out WildSpawnType role))
                    role = WildSpawnType.infectedAssault;
                if (Singleton<IBotGame>.Instantiated && Singleton<IBotGame>.Instance.BotsController?.BotSpawner?._botCreator is BotCreatorClient creator)
                {
                    creator.AddToTargetBackup(BotDifficulty.normal, role, count);
                    ZombiesDebug.Log($"spawn: asked EFT to keep {count} more zombie profiles ready");
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies rounds: zombie profile top-up skipped: {e.Message}");
            }
        }

        private static System.Collections.IEnumerator ReleaseLater(float seconds, Action release)
        {
            yield return new WaitForSeconds(seconds);
            release();
        }

        // ---------- The zombies alive ----------

        private static List<Player> _players = new List<Player>();
        private static readonly List<BotOwner> HuntQueue = new List<BotOwner>();
        private static readonly Dictionary<BotOwner, float> NextHunt = new Dictionary<BotOwner, float>();
        private static int _huntAt;
        private const int HuntsPerFrame = 3;
        private const float HuntEvery = 0.15f;

        // The zombies in with the players, a few each frame, round robin
        private static void HuntSome()
        {
            if (HuntQueue.Count == 0 || _players.Count == 0)
                return;
            int done = 0;
            for (int tries = 0; tries < HuntQueue.Count && done < HuntsPerFrame; tries++)
            {
                _huntAt = (_huntAt + 1) % HuntQueue.Count;
                BotOwner z = HuntQueue[_huntAt];
                if (z == null || !Alive.ContainsKey(z) || !ClimbedIn.Contains(z) || Rising.Contains(z) || Busy.Contains(z))
                    continue;
                if (NextHunt.TryGetValue(z, out float next) && Time.time < next)
                    continue;
                Player body = z.GetPlayer;
                if (body == null || !body.HealthController.IsAlive)
                    continue;
                NextHunt[z] = Time.time + HuntEvery;
                Hunt(z, _players);
                done++;
            }
        }

        private static void CheckZombies()
        {
            var players = ZombiesTeam.List().Where(p => p != null && p.HealthController.IsAlive).ToList();
            _players = players;
            HuntQueue.Clear();
            foreach (BotOwner q in Alive.Keys)
                if (ClimbedIn.Contains(q))
                    HuntQueue.Add(q);
            foreach (BotOwner z in Alive.Keys.ToList())
            {
                // Dead only by its health (a bot just spawned isn't active yet: that isn't dead). Gone from the world counts too
                Player body = z == null ? null : z.GetPlayer;
                if (z == null || body == null || body.HealthController != null && !body.HealthController.IsAlive)
                {
                    Alive.Remove(z);
                    NextTear.Remove(z);
                    ClimbedIn.Remove(z);
                    NextHunt.Remove(z);
                    Speed.Remove(z);
                    ZombiesTrails.Forget(z);
                    continue;
                }
                Vector3 at = z.Position;
                MeasureSpeed(z, at);
                CapSpeed(z, body);
                float nearest = players.Count == 0 ? 0f : players.Min(p => Vector3.Distance(p.Transform.position, at));
                // Far from everyone for a while: gone, and back in the count to come again near someone
                if (nearest > FarDistance)
                {
                    if (Alive[z] < 0f)
                        Alive[z] = Time.time;
                    else if (Time.time - Alive[z] > FarSeconds)
                    {
                        Despawn(z);
                        Alive.Remove(z);
                        _spawnedThisRound--;
                        ZombiesDebug.Log("zombie despawned (far), back in the count");
                        continue;
                    }
                }
                else
                    Alive[z] = -1f;
                // Never got in (user, 2026-11-10: a round stuck on a zombie nobody could find): still outside its
                // barricade this long after spawning, and not mid-climb or rising: gone, back in the count to come
                // again at a way in near the players
                if (!ClimbedIn.Contains(z) && !Busy.Contains(z) && !Rising.Contains(z)
                    && SpawnedAt.TryGetValue(z, out float born) && Time.time - born > (OutsideMost?.Value ?? 75f))
                {
                    ZombiesPlugin.Log.LogInfo($"[ai] zombie still outside {Time.time - born:0} s after spawning (at {at}, {nearest:0} m from the nearest player): back in the count to come again");
                    Despawn(z);
                    Alive.Remove(z);
                    SpawnedAt.Remove(z);
                    _spawnedThisRound--;
                    continue;
                }
                HideWeapons(z);
                ZombiesBlockers.Ignore(body);
                if (Rising.Contains(z) || Busy.Contains(z))
                    continue;
                TryBarricade(z);
                // In with the players: HuntSome looks at it (a few a frame); outside: here, every 0.5 s
                if (!ClimbedIn.Contains(z) || ZombiesTrails.On?.Value == false)
                    Hunt(z, players);
                if (Stuck(z, at, nearest))
                    continue;
            }
        }

        // ---------- Stuck zombies ----------

        // Not getting anywhere for a while (no path through the yard, caught on a corner): outside, it's walked to its
        // barricade along a straight line; inside, it goes back in the count and comes again. Each case logged.
        private const float StuckSeconds = 6f, StuckInsideSeconds = 12f, StuckMove = 0.5f;
        private static readonly Dictionary<BotOwner, (Vector3 at, float since)> Progress = new Dictionary<BotOwner, (Vector3, float)>();

        private static bool Stuck(BotOwner z, Vector3 at, float nearestPlayer)
        {
            float now = Time.time;
            if (!Progress.TryGetValue(z, out var p) || Vector3.Distance(p.at, at) > StuckMove)
            {
                Progress[z] = (at, now);
                return false;
            }
            Feeds.TryGetValue(z, out ZombiesBarricade b);
            bool outside = !ClimbedIn.Contains(z);
            // Waiting its turn at the window, or at a player slapping: not stuck
            if (nearestPlayer < 2.5f || outside && b != null && (Vector3.Distance(at, b.transform.position) <= BarricadeReach + 1.5f || Vector3.Distance(at, QueueSpot(z, b)) <= 1.5f))
            {
                Progress[z] = (at, now);
                return false;
            }
            float still = now - p.since;
            if (outside && b != null && still > StuckSeconds)
            {
                // Its own barricade, always (never another): a straight walk to its place in the queue there
                Vector3 to = QueueSpot(z, b);
                ZombiesPlugin.Log.LogInfo($"[ai] zombie stuck {still:0} s outside at {at} (zone {ZombiesZones.ZoneAt(at)}), {Vector3.Distance(at, to):0.0} m from its barricade {b.Entry}: walked there");
                Progress[z] = (at, now + 20f);
                ZombiesPlugin.Instance.StartCoroutine(Shamble(z, to));
                return true;
            }
            // Inside and near a player (in sight): never sent back, it walks at them instead (ChaseStraight)
            if (!outside && nearestPlayer < 5f)
            {
                Progress[z] = (at, now);
                return false;
            }
            if (!outside && still > StuckInsideSeconds)
            {
                ZombiesPlugin.Log.LogInfo($"[ai] zombie stuck {still:0} s inside at {at} (zone {ZombiesZones.ZoneAt(at)}), {nearestPlayer:0.0} m from the nearest player: back in the count");
                Progress.Remove(z);
                Despawn(z);
                Alive.Remove(z);
                _spawnedThisRound--;
                return true;
            }
            return false;
        }

        // The point just outside a barricade, where a zombie stands to tear its boards
        private static Vector3 OutsidePoint(ZombiesBarricade b) => OutsideGround(b, b.transform.position + b.transform.forward * 0.8f);

        // A barricade set on a sill (a concrete barrier, a wall top) had its outside spot up in the air at the boards'
        // height: no navmesh there, and its zombies stopped short in the yard for good (barricade 1 under the three
        // containers, the user 2026-11-08: 23 "no path" lines, none got in). The spot is taken down to the walkable
        // ground in front, out to 2.2 m, ground well under the sill first (not the barrier's own top). Kept per
        // barricade (they never move in a match).
        private static readonly Dictionary<ZombiesBarricade, float> GroundDrop = new Dictionary<ZombiesBarricade, float>();
        private static readonly Dictionary<ZombiesBarricade, float> GroundOut = new Dictionary<ZombiesBarricade, float>();

        private static Vector3 OutsideGround(ZombiesBarricade b, Vector3 spot)
        {
            if (b == null)
                return spot;
            if (!GroundDrop.TryGetValue(b, out float drop))
            {
                drop = 0f;
                float extra = 0f, sill = b.transform.position.y;
                bool found = false;
                foreach (bool low in new[] { true, false })
                {
                    foreach (float d in new[] { 0.8f, 1.1f, 1.4f, 1.8f, 2.2f })
                    {
                        Vector3 p = b.transform.position + b.transform.forward * d;
                        if (!UnityEngine.AI.NavMesh.SamplePosition(p - Vector3.up * 1f, out UnityEngine.AI.NavMeshHit hit, 1.6f, UnityEngine.AI.NavMesh.AllAreas))
                            continue;
                        Vector3 flat = hit.position - p;
                        flat.y = 0f;
                        if (flat.magnitude > 0.7f || hit.position.y > sill + 0.3f || low && hit.position.y > sill - 0.4f)
                            continue;
                        drop = sill - hit.position.y;
                        extra = d - 0.8f;
                        found = true;
                        break;
                    }
                    if (found)
                        break;
                }
                GroundDrop[b] = drop;
                GroundOut[b] = extra;
                if (found && (drop > 0.3f || extra > 0.01f))
                    ZombiesPlugin.Log.LogInfo($"[ai] barricade at {b.transform.position}: its outside spot taken down {drop:0.00} m and {extra:0.0} m further out to the ground");
                else if (!found)
                    ZombiesPlugin.Log.LogInfo($"[ai] barricade at {b.transform.position}: no walkable ground found in front of it (outside spot left at the boards' height)");
            }
            return spot + b.transform.forward * GroundOut[b] - Vector3.up * drop;
        }

        // The zone a barricade feeds: the one set on it, else the room just inside it
        private static int BarricadeZone(ZombiesBarricade b) =>
            b.Zone > 0 ? b.Zone : ZombiesZones.ZoneAt(b.transform.position - b.transform.forward * 1.5f);

        private static readonly UnityEngine.AI.NavMeshPath ScratchPath = new UnityEngine.AI.NavMeshPath();

        // A walkable route on the navmesh from one place to another (both snapped to it)
        private static bool Reaches(Vector3 from, Vector3 to)
        {
            if (!UnityEngine.AI.NavMesh.SamplePosition(from, out UnityEngine.AI.NavMeshHit a, 3f, UnityEngine.AI.NavMesh.AllAreas)
                || !UnityEngine.AI.NavMesh.SamplePosition(to, out UnityEngine.AI.NavMeshHit b, 2f, UnityEngine.AI.NavMesh.AllAreas))
                return false;
            return UnityEngine.AI.NavMesh.CalculatePath(a.position, b.position, UnityEngine.AI.NavMesh.AllAreas, ScratchPath)
                   && ScratchPath.status == UnityEngine.AI.NavMeshPathStatus.PathComplete;
        }

        private static readonly Dictionary<BotOwner, float> NextReroute = new Dictionary<BotOwner, float>();

        /// <summary>
        /// A zombie outside whose barricade it can't walk to (no route on the navmesh, as when a yard doesn't join one
        /// window): it takes the nearest other barricade in an open zone that it can reach, instead of standing at a
        /// dead end or being walked through a wall. Returns true when it switched. At most one check in 5 s per zombie.
        /// </summary>
        private static bool Reroute(BotOwner z, Vector3 at, string why)
        {
            if (ClimbedIn.Contains(z) || NextReroute.TryGetValue(z, out float next) && Time.time < next)
                return false;
            NextReroute[z] = Time.time + 5f;
            Feeds.TryGetValue(z, out ZombiesBarricade current);
            if (!Tried.TryGetValue(z, out HashSet<ZombiesBarricade> tried))
                Tried[z] = tried = new HashSet<ZombiesBarricade>();
            ZombiesBarricade better = ZombiesBarricade.Nearest(at, 150f,
                b => b != current && !tried.Contains(b) && ZombiesZones.IsOpen(BarricadeZone(b)) && Reaches(at, OutsidePoint(b)));
            if (better == null)
                return false;
            if (current != null)
                tried.Add(current);
            Feeds[z] = better;
            LastGoal.Remove(z);
            Progress.Remove(z);
            ZombiesPlugin.Log.LogInfo($"[ai] zombie at {at} (zone {ZombiesZones.ZoneAt(at)}): {why}; new barricade {better.Entry} at {better.transform.position} " +
                                      $"({(current != null ? "was " + current.Entry : "none before")}, {Vector3.Distance(at, better.transform.position):0} m)");
            return true;
        }

        private const float ChaseStraightWithin = 2.5f; // was 10: round 1 walkers hovered at you from 10 m
        private static readonly Dictionary<BotOwner, bool> LastPathComplete = new Dictionary<BotOwner, bool>();

        // Nothing solid between a zombie's chest and the player's
        private static bool Sees(Vector3 from, Vector3 to) =>
            !Physics.Linecast(from + Vector3.up * 1.2f, to + Vector3.up * 1.2f, LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain"), QueryTriggerInteraction.Ignore);

        // The most a straight-walking zombie steps up or down in one go (a kerb, a stair): never onto a sandbag or fence
        private const float MaxStep = 0.45f;

        // The ground under a straight-walk step: the navmesh where it lies within a step of the feet, else the collider
        // straight below (cast from a step up, so a prop's top over the head is never taken), else the same height
        private static Vector3 OnGround(Vector3 next, float feetY)
        {
            var probe = new Vector3(next.x, feetY, next.z);
            if (UnityEngine.AI.NavMesh.SamplePosition(probe, out UnityEngine.AI.NavMeshHit onMesh, 0.6f, UnityEngine.AI.NavMesh.AllAreas)
                && Mathf.Abs(onMesh.position.y - feetY) <= MaxStep)
                return new Vector3(next.x, onMesh.position.y, next.z);
            if (Physics.Raycast(probe + Vector3.up * MaxStep, Vector3.down, out RaycastHit ground, MaxStep + 1.5f,
                    LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain", "Default"), QueryTriggerInteraction.Ignore))
                return new Vector3(next.x, ground.point.y, next.z);
            return probe;
        }

        // Up to 3 s straight at the player (following them), at the zombie's own pace, kept on the ground, never into a
        // wall (it stops when something is in the way); then the normal hunt again
        private static System.Collections.IEnumerator ChaseStraight(BotOwner z, Player target)
        {
            Player body = z?.GetPlayer;
            if (body == null || target == null)
                yield break;
            Busy.Add(z);
            // Its own walking speed (a round 1 walker creeps), from how fast it has really been going
            float speed = Mathf.Clamp(Speed.TryGetValue(z, out var sp) && sp.speed > 0.05f ? sp.speed : 0.5f, 0.25f, 3f);
            ZombiesDebug.Log($"zombie: last stretch straight at the player ({speed:0.00} m/s)");
            float until = Time.time + 3f, nextHold = 0f;
            while (body != null && body.HealthController.IsAlive && target != null && Time.time < until)
            {
                Vector3 at = body.Transform.position, goal = target.Transform.position;
                Vector3 flat = new Vector3(goal.x - at.x, 0f, goal.z - at.z);
                if (flat.magnitude <= SlapReach * 0.8f)
                    break;
                if (Time.time >= nextHold)
                {
                    nextHold = Time.time + 0.25f;
                    Hold(z, 0.5f);
                }
                Vector3 step = flat.normalized * Mathf.Min(flat.magnitude, speed * Time.deltaTime);
                if (Physics.SphereCast(at + Vector3.up * 1f, 0.3f, step.normalized, out _, 0.45f, LayerMask.GetMask("HighPolyCollider", "LowPolyCollider"), QueryTriggerInteraction.Ignore))
                    break;
                Vector3 next = OnGround(at + step, at.y);
                body.Teleport(next, false);
                Face(z, goal + Vector3.up * 1.4f);
                yield return null;
            }
            Busy.Remove(z);
            LastGoal.Remove(z);
        }

        // A walk to a point, kept on the ground, facing the way it goes. The bot's own walking is held off the whole way
        // (its old route fought the steps and walked it backwards); it ends at the point, or after 45 s.
        // It follows Unity's own navmesh route there when there is one (whole or partial), corner to corner, and only
        // walks a straight line when there's none (2026-11-10: EFT said "no path" from the west spawns to barricade 1,
        // the straight line ran into a container's end and was retried every 0.5 s for minutes).
        private static readonly Dictionary<BotOwner, float> ShambleRest = new Dictionary<BotOwner, float>();

        private static System.Collections.IEnumerator Shamble(BotOwner z, Vector3 to)
        {
            Player body = z?.GetPlayer;
            if (body == null)
                yield break;
            if (ShambleRest.TryGetValue(z, out float rest) && Time.time < rest)
                yield break;
            Busy.Add(z);
            var stops = new List<Vector3>();
            if (UnityEngine.AI.NavMesh.SamplePosition(body.Transform.position, out UnityEngine.AI.NavMeshHit sa, 2f, UnityEngine.AI.NavMesh.AllAreas)
                && UnityEngine.AI.NavMesh.SamplePosition(to, out UnityEngine.AI.NavMeshHit sb, 3f, UnityEngine.AI.NavMesh.AllAreas))
            {
                var route = new UnityEngine.AI.NavMeshPath();
                if (UnityEngine.AI.NavMesh.CalculatePath(sa.position, sb.position, UnityEngine.AI.NavMesh.AllAreas, route)
                    && route.status != UnityEngine.AI.NavMeshPathStatus.PathInvalid && route.corners.Length > 2)
                    stops.AddRange(route.corners.Skip(1).Take(route.corners.Length - 2));
            }
            bool routed = stops.Count > 0;
            stops.Add(to);
            int leg = 0;
            // Its own pace (a steady shamble, not a dash), never slower than a slow walk
            float speed = Mathf.Max(0.7f, 1.1f * Mathf.Clamp(Pace.TryGetValue(z, out float pace) ? pace * 4f : 1f, 0.6f, 2.5f));
            float until = Time.time + 45f, nextStop = 0f;
            string why = "time ran out";
            Vector3 start = body.Transform.position;
            while (body != null && body.HealthController.IsAlive && Time.time < until)
            {
                Vector3 at = body.Transform.position;
                Vector3 aim = stops[leg];
                Vector3 flat = new Vector3(aim.x - at.x, 0f, aim.z - at.z);
                if (flat.magnitude < 0.4f)
                {
                    if (++leg < stops.Count)
                        continue;
                    why = "arrived";
                    break;
                }
                if (Time.time >= nextStop)
                {
                    nextStop = Time.time + 0.25f;
                    Hold(z, 0.5f);
                }
                Vector3 dir = flat.normalized;
                // Never through or onto a fence, sandbags or a wall: it stops (the 2026-11-07 video had one walking
                // along the fence tops: the navmesh 1.5 m up was taken as its ground)
                // On Unity's route the corners already keep clear of walls (by the agent's width): a slimmer check there
                if (Physics.SphereCast(at + Vector3.up * 1f, routed ? 0.2f : 0.3f, dir, out RaycastHit wall, routed ? 0.3f : 0.45f, LayerMask.GetMask("HighPolyCollider", "LowPolyCollider"), QueryTriggerInteraction.Ignore))
                {
                    why = $"blocked by {wall.collider.name}";
                    break;
                }
                Vector3 next = OnGround(at + dir * Mathf.Min(flat.magnitude, speed * Time.deltaTime), at.y);
                body.Teleport(next, false);
                Face(z, aim + Vector3.up * 1.4f);
                yield return null;
            }
            LastGoal.Remove(z);
            Busy.Remove(z);
            if (body != null)
            {
                float moved = Vector3.Distance(start, body.Transform.position);
                // Blocked before it got anywhere: no new walk for 5 s (it was restarted every 0.5 s, 270 times)
                if (why.StartsWith("blocked") && moved < 0.3f)
                    ShambleRest[z] = Time.time + 5f;
                ZombiesPlugin.Log.LogInfo($"[ai] zombie's {(routed ? $"navmesh-route walk ({stops.Count} legs, reached {leg})" : "straight walk")} to its barricade: {why}, "
                                          + $"{Vector3.Distance(body.Transform.position, to):0.0} m left (moved {moved:0.0} m)");
            }
        }

        // Logger (2026-11-10: zombies from the west spawns never reached barricade 1; the editor's navmesh has a whole
        // way there, the game said "no path", so something added in the match cuts it). Once a minute per goal: Unity's
        // own route (whole, partial and where it ends, or none), how far the ends are off the navmesh, and every carving
        // obstacle within 4 m of where the route stops or of the goal. Reads only.
        private static readonly Dictionary<Vector3, float> WhyLogged = new Dictionary<Vector3, float>();

        private static void WhyNoPath(Vector3 from, Vector3 goal)
        {
            try
            {
                if (WhyLogged.TryGetValue(goal, out float last) && Time.time - last < 60f)
                    return;
                WhyLogged[goal] = Time.time;
                bool a = UnityEngine.AI.NavMesh.SamplePosition(from, out UnityEngine.AI.NavMeshHit ha, 2f, UnityEngine.AI.NavMesh.AllAreas);
                bool b = UnityEngine.AI.NavMesh.SamplePosition(goal, out UnityEngine.AI.NavMeshHit hb, 3f, UnityEngine.AI.NavMesh.AllAreas);
                var path = new UnityEngine.AI.NavMeshPath();
                if (a && b)
                    UnityEngine.AI.NavMesh.CalculatePath(ha.position, hb.position, UnityEngine.AI.NavMesh.AllAreas, path);
                Vector3 stop = path.corners.Length > 0 ? path.corners[path.corners.Length - 1] : from;
                var near = new List<string>();
                foreach (UnityEngine.AI.NavMeshObstacle o in UnityEngine.Object.FindObjectsOfType<UnityEngine.AI.NavMeshObstacle>())
                {
                    if (o == null || !o.enabled || !o.carving)
                        continue;
                    Vector3 c = o.transform.TransformPoint(o.center);
                    Vector3 size = Vector3.Scale(o.size, o.transform.lossyScale);
                    float d = Mathf.Min(Vector3.Distance(c, stop), Vector3.Distance(c, goal));
                    if (d < 4f + size.magnitude / 2f)
                        near.Add($"{o.name} at {c} size {size} turn {o.transform.eulerAngles.y:0}");
                }
                ZombiesPlugin.Log.LogInfo($"[ai] why no path {from} -> {goal}: start {(a ? $"{Vector3.Distance(from, ha.position):0.00} m off the navmesh" : "OFF the navmesh")}, "
                    + $"goal {(b ? $"{Vector3.Distance(goal, hb.position):0.00} m off" : "OFF the navmesh")}; Unity's route: {path.status}, {path.corners.Length} corners, "
                    + $"stops at {stop} ({Vector3.Distance(stop, goal):0.0} m short). Carving near: {(near.Count == 0 ? "none" : string.Join("; ", near))}");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"[ai] why-no-path logger failed: {e.Message}");
            }
        }

        // Unity's route from here towards a point: where a partial one ends (the reachable navmesh nearest the goal)
        private static readonly UnityEngine.AI.NavMeshPath ReachPath = new UnityEngine.AI.NavMeshPath();

        private static bool NearestReachable(Vector3 from, Vector3 goal, out Vector3 reach)
        {
            reach = goal;
            if (!UnityEngine.AI.NavMesh.SamplePosition(from, out UnityEngine.AI.NavMeshHit a, 2f, UnityEngine.AI.NavMesh.AllAreas)
                || !UnityEngine.AI.NavMesh.SamplePosition(goal, out UnityEngine.AI.NavMeshHit b, 3f, UnityEngine.AI.NavMesh.AllAreas))
                return false;
            if (!UnityEngine.AI.NavMesh.CalculatePath(a.position, b.position, UnityEngine.AI.NavMesh.AllAreas, ReachPath) || ReachPath.corners.Length == 0
                || ReachPath.status == UnityEngine.AI.NavMeshPathStatus.PathInvalid)
                return false;
            reach = ReachPath.corners[ReachPath.corners.Length - 1];
            return true;
        }

        // ---------- Nav links walked across ----------
        private static readonly Dictionary<BotOwner, float> NextCross = new Dictionary<BotOwner, float>();

        private static bool TryCrossLink(BotOwner z, Vector3 at, Vector3 goal)
        {
            if (ZombiesNavLinks.Ends.Count == 0 || Busy.Contains(z) || Rising.Contains(z)
                || (NextCross.TryGetValue(z, out float next) && Time.time < next))
                return false;
            foreach (var (a, b, oneWay) in ZombiesNavLinks.Ends)
            {
                // Which end it's standing at (within 1.2 m across, 1.5 m up/down); the far end is where it goes
                Vector3 from, to;
                if (Near(at, a))
                {
                    from = a;
                    to = b;
                }
                else if (!oneWay && Near(at, b))
                {
                    from = b;
                    to = a;
                }
                else
                    continue;
                // Only when its player is on the far side: the far end is nearer the player than this one, by a margin
                float here = Vector3.Distance(from, goal), there = Vector3.Distance(to, goal);
                if (there > here - 0.5f)
                    continue;
                NextCross[z] = Time.time + 4f;
                ZombiesPlugin.Instance.StartCoroutine(CrossLink(z, to));
                return true;
            }
            return false;
        }

        private static bool Near(Vector3 at, Vector3 end) =>
            new Vector2(at.x - end.x, at.z - end.z).magnitude < 1.8f && Mathf.Abs(at.y - end.y) < 1.5f;

        // A short straight walk across the link (through a doorway too tight for the bake): its pace, on the ground
        private static System.Collections.IEnumerator CrossLink(BotOwner z, Vector3 to)
        {
            Player body = z?.GetPlayer;
            if (body == null)
                yield break;
            Busy.Add(z);
            float speed = Mathf.Max(0.8f, 1.1f * Mathf.Clamp(Pace.TryGetValue(z, out float pace) ? pace * 4f : 1f, 0.6f, 2.5f));
            float until = Time.time + 6f, nextHold = 0f;
            Vector3 start = body.Transform.position;
            while (body != null && body.HealthController.IsAlive && Time.time < until)
            {
                Vector3 at = body.Transform.position;
                Vector3 flat = new Vector3(to.x - at.x, 0f, to.z - at.z);
                if (flat.magnitude < 0.3f)
                    break;
                if (Time.time >= nextHold)
                {
                    nextHold = Time.time + 0.25f;
                    Hold(z, 0.5f);
                }
                Vector3 step = at + flat.normalized * Mathf.Min(flat.magnitude, speed * Time.deltaTime);
                step.y = Mathf.MoveTowards(at.y, to.y, 4f * Time.deltaTime);
                // The real floor under the step (not the navmesh: in the gap its nearest point is back where it
                // started, and every step snapped back: "walked 0.1 m, 1.5 m short")
                if (Physics.Raycast(step + Vector3.up * 0.8f, Vector3.down, out RaycastHit floor, 2f,
                        LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain", "Default"), QueryTriggerInteraction.Ignore))
                    step.y = floor.point.y;
                body.Teleport(step, false);
                Face(z, to + Vector3.up * 1.4f);
                yield return null;
            }
            LastGoal.Remove(z);
            Busy.Remove(z);
            if (body != null)
                ZombiesDebug.Log($"[ai] zombie walked a nav link: {Vector3.Distance(start, body.Transform.position):0.0} m, {Vector3.Distance(body.Transform.position, to):0.0} m short");
        }

        private const float SlapReach = 1.3f, SlapEvery = 1.8f;
        private static readonly Dictionary<BotOwner, float> NextSlap = new Dictionary<BotOwner, float>();
        private static readonly Dictionary<BotOwner, (Vector3 at, float time)> LastGoal = new Dictionary<BotOwner, (Vector3, float)>();
        private static readonly Dictionary<BotOwner, float> NoPathLogged = new Dictionary<BotOwner, float>();

        // The original's zombie: straight for the nearest player still standing (downed players are left alone); outside a
        // barricade it goes to the barricade first. Close enough, it slaps (ZombiesSlaps; each client takes its own).
        private static void Hunt(BotOwner z, List<Player> players)
        {
            // Down or dead players aren't hunted (a teammate's state comes over the network)
            var standing = players.Where(p => p.IsYourPlayer ? ZombiesDowned.Mine == ZombiesDowned.State.Up : ZombiesNet.StateOf(p.ProfileId) == ZombiesNet.State.Up).ToList();
            if (standing.Count == 0)
                return;
            Vector3 at = z.Position;
            Player target = standing.OrderBy(p => Vector3.Distance(p.Transform.position, at)).First();
            Vector3 goal = target.Transform.position;
            // At one end of a nav link with its player towards the other: walked across (EFT's bot movement stops
            // where the navmesh does, in the middle of the link: zombies stood in the basement doorway, user 2026-11-09)
            if (TryCrossLink(z, at, goal))
                return;
            // A Cymbal Monkey playing near: those in with the players go to it and nothing else (no slaps)
            Vector3? monkey = ClimbedIn.Contains(z) ? ZombiesMonkey.LureFor(at) : null;
            if (monkey.HasValue)
            {
                // Whatever it was doing (a held swing, a straight chase) is dropped: straight off to the monkey
                Busy.Remove(z);
                NextSlap[z] = Time.time + SlapEvery;
                goal = monkey.Value;
                Hunting(z, goal);
                Face(z, goal + Vector3.up * 0.3f);
                return;
            }
            if (!ClimbedIn.Contains(z))
            {
                // One barricade, kept: one with none yet takes the nearest facing it, once, and sticks with it
                if (!Feeds.TryGetValue(z, out ZombiesBarricade b) || b == null)
                {
                    b = ZombiesBarricade.Nearest(at, 25f, x => Vector3.Dot(at - x.transform.position, x.transform.forward) > 0f);
                    if (b != null)
                        Feeds[z] = b;
                }
                if (b != null)
                    goal = QueueSpot(z, b);
            }
            ZombiesTrails.Kind kind = ZombiesTrails.Kind.Path;
            try
            {
                float pace = Pace.TryGetValue(z, out float p) ? p : 1f;
                z.Sprint(pace > 1f);
                z.SetTargetMoveSpeed(Mathf.Min(1f, pace));
                // A new route only when the goal has moved or a while has passed: re-routing every check cancels the
                // bot's own steps (it span on the spot)
                // Inside: the original hunting (ZombiesTrails): straight at the player when there's a clear line, else along the
                // player's trail (a fixed crumb: a new route only when the crumb changes), else a full route, rationed
                bool hunting = ClimbedIn.Contains(z);
                if (hunting && ZombiesTrails.On?.Value != false)
                    goal = ZombiesTrails.GoalFor(z, at, target, out kind);
                float moved, every;
                if (!hunting)
                {
                    moved = 1.5f;
                    every = 2f;
                }
                else if (ZombiesTrails.On?.Value == false)
                {
                    moved = 0.6f;
                    every = 0.5f;
                }
                else if (kind == ZombiesTrails.Kind.Direct)
                {
                    moved = 0.8f;
                    every = 0.35f;
                }
                else if (kind == ZombiesTrails.Kind.Trail)
                {
                    moved = 0.3f;
                    every = 3f;
                }
                else
                {
                    moved = 2.5f;
                    every = 1.5f;
                }
                bool due = !LastGoal.TryGetValue(z, out (Vector3 at, float time) last) || Vector3.Distance(last.at, goal) > moved || Time.time - last.time > every;
                // Straight lines and trail crumbs are cheap; a full route waits its turn (a few a frame)
                if (due && (kind == ZombiesTrails.Kind.Direct || kind == ZombiesTrails.Kind.Trail || ZombiesTrails.Budget()))
                {
                    LastGoal[z] = (goal, Time.time);
                    UnityEngine.AI.NavMeshPathStatus status;
                    _ourMove = true;
                    status = UnityEngine.AI.NavMeshPathStatus.PathInvalid;
                    try
                    {
                        status = z.GoToPoint(goal, false, 0.6f, false, false, true, false, false);
                        // The player on a bit of navmesh the rest doesn't join (a corner of the basement behind props):
                        // EFT's route says "no path" and the zombies stood upstairs (user, 2026-11-09). Unity's own
                        // route still reaches the nearest point it can: it goes there, the close chase does the rest
                        if (status != UnityEngine.AI.NavMeshPathStatus.PathComplete && ClimbedIn.Contains(z)
                            && NearestReachable(at, goal, out Vector3 reach) && Vector3.Distance(reach, at) > 1f)
                        {
                            status = z.GoToPoint(reach, false, 0.6f, false, false, true, false, false);
                            if (status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
                                ZombiesDebug.Log($"[ai] zombie at {at}: no full way to its player, heading to the nearest reachable point {reach} ({Vector3.Distance(reach, goal):0.0} m from them)");
                        }
                    }
                    finally
                    {
                        _ourMove = false;
                        LastPathComplete[z] = status == UnityEngine.AI.NavMeshPathStatus.PathComplete;
                    }
                    if (status != UnityEngine.AI.NavMeshPathStatus.PathComplete
                        && (!NoPathLogged.TryGetValue(z, out float logged) || Time.time - logged > 10f))
                    {
                        NoPathLogged[z] = Time.time;
                        ZombiesPlugin.Log.LogInfo($"[ai] zombie at {at} (zone {ZombiesZones.ZoneAt(at)}) has {(status == UnityEngine.AI.NavMeshPathStatus.PathPartial ? "only part of a path" : "no path")} to {goal} " +
                                                  $"({(ClimbedIn.Contains(z) ? "hunting" : "going to its barricade")}): the navmesh doesn't join them");
                        if (!ClimbedIn.Contains(z))
                            WhyNoPath(at, goal);
                    }
                    // Outside, with no way on foot to its barricade: it keeps that barricade (one window, chosen once,
                    // never swapped) and walks straight to it
                    if (status != UnityEngine.AI.NavMeshPathStatus.PathComplete && !ClimbedIn.Contains(z) && !Busy.Contains(z)
                        && Feeds.TryGetValue(z, out ZombiesBarricade own) && own != null)
                        ZombiesPlugin.Instance.StartCoroutine(Shamble(z, OutsidePoint(own)));
                }
            }
            catch
            {
            }
            if (!ClimbedIn.Contains(z))
            {
                // Outside: facing along its way to the barricade, and the boards once it's there (waiting its turn)
                if (Feeds.TryGetValue(z, out ZombiesBarricade mine) && mine != null && Vector3.Distance(at, OutsidePoint(mine)) < 2.5f)
                    Face(z, mine.transform.position + Vector3.up * 1.2f);
                else
                    FaceAlong(z);
            }
            if (ClimbedIn.Contains(z))
            {
                // Facing the one it's after when close or on a straight line at them, else along its route
                if (kind == ZombiesTrails.Kind.Direct || Vector3.Distance(target.Transform.position, at) <= FaceWithin)
                    Face(z, target.Transform.position + Vector3.up * 1.4f);
                else
                    FaceAlong(z);
                // In with them but no route the last stretch (they stand on something the navmesh doesn't cover): close
                // enough and in sight, it walks straight at them instead of standing a few metres off
                float d = Vector3.Distance(target.Transform.position, at);
                if (d > SlapReach && d < ChaseStraightWithin && !Busy.Contains(z) && ZombiesTrails.SameFloor(at, target.Transform.position)
                    && Sees(at, target.Transform.position)
                    && (!LastPathComplete.TryGetValue(z, out bool whole) || !whole || Progress.TryGetValue(z, out var pr) && Time.time - pr.since > 2f))
                    ZombiesPlugin.Instance.StartCoroutine(ChaseStraight(z, target));
            }
            if (InSlapReach(at, target.Transform.position))
            {
                Unreach.Remove(z);
                // Paused: no swing starts
                if (!ZombiesPause.Paused && (!NextSlap.TryGetValue(z, out float next) || Time.time >= next))
                {
                    NextSlap[z] = Time.time + SlapEvery;
                    ZombiesPlugin.Instance.StartCoroutine(Slap(z, target));
                }
            }
            // The rubble throw (MaybeThrow) is off: the user would rather patch the map's glitch spots (2026-11-08)
            else if (ClimbedIn.Contains(z) && ThrowRubble)
                MaybeThrow(z, at, target);
        }

        // The original's reach: across the ground, and up onto a low ledge, box or bonnet (the user's glitch spots: a player on
        // a tyre stack 1-1.5 m up was "out of reach" of the zombies pressed against it, as the reach was a straight line)
        private const float SlapReachUp = 2f, SlapReachDown = 1f;

        private static bool InSlapReach(Vector3 zombie, Vector3 player, float extra = 0f)
        {
            float up = player.y - zombie.y;
            if (up > SlapReachUp || up < -SlapReachDown)
                return false;
            Vector3 flat = player - zombie;
            flat.y = 0f;
            return flat.magnitude <= SlapReach + extra + Mathf.Max(0f, up) * 0.15f;
        }

        // ---------- Rubble thrown at a player they can't get to (Cold War's answer to glitch spots) ----------

        // In with the players, in sight, close, and no way to them (up out of reach, or no full route) for a few seconds:
        // the zombie throws a chunk of rubble that counts as a slap. Each zombie now and then, and not a hail at once.
        private const bool ThrowRubble = false;
        private const float ThrowWithin = 9f, ThrowAfter = 3f, ThrowEvery = 6f, ThrowGapPerTarget = 2.5f;
        private static readonly Dictionary<BotOwner, float> Unreach = new Dictionary<BotOwner, float>();
        private static readonly Dictionary<BotOwner, float> NextThrow = new Dictionary<BotOwner, float>();
        private static readonly Dictionary<string, float> NextThrowAt = new Dictionary<string, float>();

        private static void MaybeThrow(BotOwner z, Vector3 at, Player target)
        {
            Vector3 p = target.Transform.position;
            Vector3 flat = p - at;
            flat.y = 0f;
            bool high = p.y - at.y > 1f;
            bool noRoute = LastPathComplete.TryGetValue(z, out bool whole) && !whole;
            bool stuckClose = Progress.TryGetValue(z, out var pr) && Time.time - pr.since > 3f && flat.magnitude < 4f;
            if (flat.magnitude > ThrowWithin || !(high || noRoute || stuckClose) || Busy.Contains(z) || !Sees(at, p))
            {
                Unreach.Remove(z);
                return;
            }
            if (!Unreach.TryGetValue(z, out float since))
            {
                Unreach[z] = Time.time;
                return;
            }
            if (Time.time - since < ThrowAfter || NextThrow.TryGetValue(z, out float mine) && Time.time < mine
                || NextThrowAt.TryGetValue(target.ProfileId, out float theirs) && Time.time < theirs)
                return;
            NextThrow[z] = Time.time + ThrowEvery;
            NextThrowAt[target.ProfileId] = Time.time + ThrowGapPerTarget;
            ZombiesPlugin.Log.LogInfo($"[ai] zombie at {at} throws rubble at a player it can't reach ({(high ? $"{p.y - at.y:0.0} m up" : noRoute ? "no route" : "stuck close")}, {flat.magnitude:0.0} m)");
            ZombiesPlugin.Instance.StartCoroutine(Throw(z, target));
        }

        private static Material _rubble;

        private static System.Collections.IEnumerator Throw(BotOwner z, Player target)
        {
            Player body = z?.GetPlayer;
            if (body == null || target == null)
                yield break;
            Busy.Add(z);
            try
            {
                z.Mover?.Stop();
                Hold(z, SlapHold);
                Face(z, target.Transform.position + Vector3.up * 1.4f);
                if (body.HandsController is Player.KnifeController arms)
                    arms.MakeKnifeKick();
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"zombie throw swing failed: {e.Message}");
            }
            yield return new WaitForSeconds(0.45f);
            if (body == null || !body.HealthController.IsAlive || target == null || !target.HealthController.IsAlive)
            {
                Busy.Remove(z);
                yield break;
            }
            // The chunk: a small dark lump in an arc from its hand to the player's chest
            var rock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEngine.Object.Destroy(rock.GetComponent<Collider>());
            rock.transform.localScale = new Vector3(0.16f, 0.12f, 0.14f);
            if (_rubble == null)
                _rubble = new Material(Shader.Find("Standard")) { color = new Color(0.28f, 0.26f, 0.24f) };
            rock.GetComponent<Renderer>().sharedMaterial = _rubble;
            Vector3 from = body.Transform.position + Vector3.up * 1.6f + body.Transform.forward * 0.3f;
            float flight = 0.55f, t = 0f;
            Quaternion spin = UnityEngine.Random.rotation;
            while (t < flight && target != null)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / flight);
                Vector3 to = target.Transform.position + Vector3.up * 1.2f;
                rock.transform.position = Vector3.Lerp(from, to, k) + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.8f);
                rock.transform.rotation = spin * Quaternion.Euler(k * 720f, k * 360f, 0f);
                yield return null;
            }
            Vector3 hitAt = rock.transform.position;
            UnityEngine.Object.Destroy(rock);
            ZombiesSounds.At("zm_debris_rock_#", hitAt, 0.6f, 20f);
            if (target != null && target.HealthController.IsAlive && Vector3.Distance(hitAt, target.Transform.position + Vector3.up * 1.2f) < 1.5f)
            {
                if (target.IsYourPlayer)
                    ZombiesSlaps.Hit(target);
                else
                    ZombiesNet.Slap(target.ProfileId);
            }
            yield return new WaitForSeconds(SlapRecover);
            LastGoal.Remove(z);
            Busy.Remove(z);
        }

        // A route to a fixed spot (the monkey), re-planned only when it moves or now and then
        private static void Hunting(BotOwner z, Vector3 goal)
        {
            try
            {
                float pace = Pace.TryGetValue(z, out float p) ? p : 1f;
                z.Sprint(pace > 1f);
                z.SetTargetMoveSpeed(Mathf.Min(1f, pace));
                if (LastGoal.TryGetValue(z, out (Vector3 at, float time) last) && Vector3.Distance(last.at, goal) < 0.5f && Time.time - last.time < 1f)
                    return;
                LastGoal[z] = (goal, Time.time);
                _ourMove = true;
                try
                {
                    z.GoToPoint(goal, false, 0.8f, false, false, true, false, false);
                }
                finally
                {
                    _ourMove = false;
                }
            }
            catch
            {
            }
        }

        // At a barricade's outside: a board every 1.5 s, then through it (the original's climb), once
        // A barricade's next climb-in time (one zombie at a time through a window) and the random pause a zombie takes
        // on arriving before it starts on the boards
        private static readonly Dictionary<ZombiesBarricade, float> ClimbFree = new Dictionary<ZombiesBarricade, float>();
        private const float ClimbGap = 2.2f, ArrivePause = 1.2f;

        private static void TryBarricade(BotOwner z)
        {
            if (ClimbedIn.Contains(z))
                return;
            Vector3 at = z.Position;
            ZombiesBarricade b = Feeds.TryGetValue(z, out ZombiesBarricade mine) && mine != null ? mine : ZombiesBarricade.Nearest(at, BarricadeReach + 1f);
            // At it: by the boards, or on its (grounded) outside spot, for a barricade up on a sill
            if (b == null || Vector3.Distance(at, b.transform.position) > BarricadeReach + 1f && Vector3.Distance(at, OutsidePoint(b)) > 1.2f
                || Vector3.Dot(at - b.transform.position, b.transform.forward) < 0f)
                return;
            if (b.BoardsUp > 0)
            {
                // A moment at the window first (each its own, so a crowd doesn't grab in step), then a board at a
                // time (the barricade paces it)
                if (!NextTear.TryGetValue(z, out float since))
                    NextTear[z] = Time.time + UnityEngine.Random.Range(0f, ArrivePause);
                else if (Time.time - since >= BoardTearSeconds && b.ClaimTear(BoardSeconds?.Value ?? 3f))
                    ZombiesPlugin.Instance.StartCoroutine(RipBoard(z, b));
                return;
            }
            // All boards off: up and over, into the room, one at a time (the original: they come through a window one by one,
            // the rest wait their turn behind)
            if (ClimbFree.TryGetValue(b, out float free) && Time.time < free)
                return;
            ClimbFree[b] = Time.time + ClimbGap + UnityEngine.Random.Range(0f, 0.6f);
            ClimbedIn.Add(z);
            ZombiesPlugin.Instance.StartCoroutine(Vault(z, b.transform.position + b.transform.forward * 0.6f, b.transform.position - b.transform.forward * 1.3f, b));
        }

        // The original's rise out of the ground (scripted for now; the original's own climb-out anim on the body to come): from under the
        // spawn up through the dirt, with its sound; no hunting, tearing or slapping until it's out
        private const float RiseDepth = 1.7f, RiseSeconds = 2.2f;
        private static readonly HashSet<BotOwner> Rising = new HashSet<BotOwner>();

        private static System.Collections.IEnumerator Rise(BotOwner z, Vector3 spot)
        {
            Player body = z?.GetPlayer;
            if (body == null)
                yield break;
            Rising.Add(z);
            ZombiesSounds.At("zm_rise_dirt_#", spot, 1f, 30f);
            float sway = UnityEngine.Random.Range(0f, 10f);
            for (float t = 0f; t < RiseSeconds; t += Time.deltaTime)
            {
                if (body == null || !body.HealthController.IsAlive)
                    break;
                float k = t / RiseSeconds;
                // Quick at first, slowing as the feet come out; a little side to side, clawing up
                float up = 1f - (1f - k) * (1f - k);
                Vector3 wobble = new Vector3(Mathf.Sin(sway + t * 7f), 0f, Mathf.Cos(sway + t * 6f)) * 0.04f * (1f - k);
                body.Teleport(spot - Vector3.up * RiseDepth * (1f - up) + wobble, false);
                yield return null;
            }
            if (body != null && body.HealthController.IsAlive)
                body.Teleport(spot, false);
            Rising.Remove(z);
        }

        // The climb through: about a second along an arc over the sill (the bot's own walking held off meanwhile)
        private const float VaultSeconds = 1.3f, VaultHeight = 0.9f;

        // The original's slap: the zombie stops, turns to you and swings (EFT's infected attack); the hit only counts if you're
        // still in reach when the swing lands, so stepping back dodges it and a crowd can't tag you down at once
        private const float SlapWindUp = 0.5f, SlapRecover = 0.55f, SlapLandsWithin = 0.6f;

        private static System.Collections.IEnumerator Slap(BotOwner z, Player target)
        {
            Player body = z?.GetPlayer;
            if (body == null || target == null)
                yield break;
            Busy.Add(z);
            try
            {
                z.Mover?.Stop();
                Hold(z, SlapHold);
                Face(z, target.Transform.position + Vector3.up * 1.4f);
                if (body.HandsController is Player.KnifeController arms)
                    arms.MakeKnifeKick();
                else
                    ZombiesDebug.Log($"zombie slap: no knife hands ({body.HandsController?.GetType().Name ?? "none"}), no swing shown");
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"zombie slap swing failed: {e.Message}");
            }
            yield return new WaitForSeconds(SlapWindUp);
            // A monkey started playing during the swing: the swing is let go (no hit), off to the monkey
            bool lured = body != null && ClimbedIn.Contains(z) && ZombiesMonkey.LureFor(body.Transform.position).HasValue;
            if (!lured && body != null && body.HealthController.IsAlive && target != null && target.HealthController.IsAlive
                && InSlapReach(body.Transform.position, target.Transform.position, SlapLandsWithin))
            {
                // A teammate's slap is counted on their own game
                if (target.IsYourPlayer)
                    ZombiesSlaps.Hit(target);
                else
                    ZombiesNet.Slap(target.ProfileId);
            }
            yield return new WaitForSeconds(SlapRecover);
            // A fresh route once the swing is over (the stop cancelled the old one)
            LastGoal.Remove(z);
            Busy.Remove(z);
        }

        // A zombie busy at a barricade (ripping a board, climbing in): nothing else steers it meanwhile (the re-routing
        // and the slap check fighting the scripted move made it jitter)
        private static readonly HashSet<BotOwner> Busy = new HashSet<BotOwner>();
        private const float SwingToHit = 0.35f, AfterRip = 0.45f, ToSillSeconds = 0.5f, SillHeight = 1f;

        // The original's rip: the zombie stops, faces the boards and swings (EFT's infected attack); the board comes off on the hit
        private static System.Collections.IEnumerator RipBoard(BotOwner z, ZombiesBarricade b)
        {
            Player body = z?.GetPlayer;
            if (body == null || b == null)
                yield break;
            Busy.Add(z);
            try
            {
                z.Mover?.Stop();
                Hold(z, RipHold);
                Face(z, b.transform.position + Vector3.up * 1.2f);
                if (TearSwing?.Value != false && body.HandsController is Player.KnifeController arms)
                    arms.MakeKnifeKick();
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"zombie swing failed: {e.Message}");
            }
            yield return new WaitForSeconds(SwingToHit);
            if (b != null && body != null && body.HealthController.IsAlive)
                b.TearBoard();
            yield return new WaitForSeconds(AfterRip);
            LastGoal.Remove(z);
            Busy.Remove(z);
        }

        // Over the window: a step up to the sill, then over and down the far side on a smooth arc, the body playing
        // EFT's own vault animation (the animator's vault flags) as it goes, facing the way it climbs
        private static System.Collections.IEnumerator Vault(BotOwner z, Vector3 from, Vector3 to, ZombiesBarricade b = null)
        {
            Player body = z?.GetPlayer;
            if (body == null)
                yield break;
            Busy.Add(z);
            // Its own walking held off the whole way (it fought the climb and slid), eased back in after
            Hold(z, ToSillSeconds + VaultSeconds + 0.2f);
            Vector3 start = body.Transform.position;
            PlayerAnimator anim = body.MovementContext?.PlayerAnimator;
            bool animated = false;
            // To the sill (eased), facing the window
            for (float t = 0f; t < ToSillSeconds; t += Time.deltaTime)
            {
                if (body == null || !body.HealthController.IsAlive)
                {
                    Busy.Remove(z);
                    yield break;
                }
                float k = Mathf.SmoothStep(0f, 1f, t / ToSillSeconds);
                body.Teleport(Vector3.Lerp(start, from, k), false);
                Face(z, to + Vector3.up * 1.2f);
                yield return null;
            }
            // EFT's own vault over the sill (2026-11-08: the scripted arc read as floating); the arc below only when
            // EFT says no
            if (b != null && ZombiesVault.RealVault?.Value != false)
            {
                Hold(z, 5f);
                bool vaulted = false;
                yield return ZombiesVault.Over(z, b, to, ok => vaulted = ok);
                if (vaulted)
                {
                    LastGoal.Remove(z);
                    ZombiesDebug.Log($"zombie vaulted in at {to}");
                    Busy.Remove(z);
                    yield break;
                }
                Hold(z, VaultSeconds + 0.2f);
            }
            if (VaultAnim?.Value != false && anim != null)
            {
                try
                {
                    anim.SetVaultingHeight(SillHeight);
                    anim.SetVaultingLength(Vector3.Distance(from, to));
                    anim.SetVaultingSpeed(1f);
                    anim.SetDoVault(true);
                    anim.SetIsVaulting(true);
                    animated = true;
                }
                catch (Exception e)
                {
                    ZombiesDebug.Log($"zombie vault animation failed: {e.Message}");
                }
            }
            // Over and down: eased along, lifted in the middle
            for (float t = 0f; t < VaultSeconds; t += Time.deltaTime)
            {
                if (body == null || !body.HealthController.IsAlive)
                    break;
                float k = Mathf.SmoothStep(0f, 1f, t / VaultSeconds);
                float lift = Mathf.Sin(Mathf.Clamp01(t / VaultSeconds) * Mathf.PI) * VaultHeight;
                body.Teleport(Vector3.Lerp(from, to, k) + Vector3.up * lift, false);
                Face(z, to + (to - from).normalized * 3f + Vector3.up * 1.2f);
                yield return null;
            }
            if (body != null && body.HealthController.IsAlive)
                body.Teleport(to, false);
            if (animated)
            {
                try
                {
                    anim.SetDoVault(false);
                    anim.SetIsVaulting(false);
                }
                catch
                {
                    // Left to the animator
                }
            }
            LastGoal.Remove(z);
            ZombiesDebug.Log($"zombie climbed in at {to}");
            Busy.Remove(z);
        }

        // Zombies never show a weapon: EFT's infected carry a melee item (its swing is the slap), so its model is hidden,
        // in the hands and on the body (and any gun a bot type came with)
        private static readonly EquipmentSlot[] WeaponSlots = { EquipmentSlot.Scabbard, EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };
        private static readonly HashSet<BotOwner> LoggedWeapons = new HashSet<BotOwner>();

        private static void HideWeapons(BotOwner z) => HideWeapons(z?.GetPlayer, z);

        private static float _nextHideAll;
        private static readonly HashSet<string> HiddenOnce = new HashSet<string>();

        /// <summary>
        /// Every game, host and Fika clients alike: no zombie it sees holds a weapon (only the host ran the hiding
        /// before, so a client saw the infected with their knives and guns). Once a second over every zombie.
        /// </summary>
        public static void HideAllZombieWeapons()
        {
            if (Time.time < _nextHideAll || !Comfort.Common.Singleton<GameWorld>.Instantiated)
                return;
            _nextHideAll = Time.time + 1f;
            // The host hides its own zombies' weapons as they spawn; only a Fika client needs this pass. A zombie can
            // arrive before Fika has given it its hands (a headless host: zombies came with melee weapons out, user
            // 2026-11-09), so each is looked at for its first 15 s and again whenever its hands change, not just once
            // (a full renderer walk on every zombie every second cost frames)
            if (ZombiesNet.IsHost)
                return;
            float now = Time.time;
            foreach (Player p in Comfort.Common.Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (!ZombiesTeam.IsZombie(p))
                    continue;
                GameObject hands = p.HandsController?.ControllerGameObject;
                if (!HiddenSince.TryGetValue(p.ProfileId, out var seen))
                    HiddenSince[p.ProfileId] = seen = (now, null);
                if (now - seen.first > 15f && hands == seen.hands)
                    continue;
                HideWeapons(p, null);
                HiddenSince[p.ProfileId] = (seen.first, hands);
            }
        }

        // A Fika client: when it first saw each zombie, and the hands it last hid
        private static readonly Dictionary<string, (float first, GameObject hands)> HiddenSince = new Dictionary<string, (float, GameObject)>();

        private static void HideWeapons(Player body, BotOwner z)
        {
            if (body == null)
                return;
            int hidden = 0;
            try
            {
                GameObject hands = body.HandsController?.ControllerGameObject;
                if (hands != null)
                    foreach (Renderer r in hands.GetComponentsInChildren<Renderer>(true))
                        if (r.enabled)
                        {
                            r.enabled = false;
                            hidden++;
                        }
                // Anything else on the body that's a weapon (a melee model hung on a hand bone): by its own or a parent's name
                foreach (MeshRenderer r in ((Component)body).GetComponentsInChildren<MeshRenderer>(true))
                    if (r.enabled && IsWeaponModel(r.transform))
                    {
                        r.enabled = false;
                        hidden++;
                    }
                foreach (PlayerBody.SlotView view in body.PlayerBody.SlotViews.GetValuesEnumerator())
                {
                    if (view == null || !WeaponSlots.Contains(view._equipmentSlot) || view.Model == null)
                        continue;
                    foreach (Renderer r in view.Model.GetComponentsInChildren<Renderer>(true))
                        if (r.enabled)
                        {
                            r.enabled = false;
                            hidden++;
                        }
                }
            }
            catch (Exception e)
            {
                if (z == null || LoggedWeapons.Add(z))
                    ZombiesDebug.Log($"zombie weapon hiding failed: {e.Message}");
            }
            if (hidden > 0 && z != null && LoggedWeapons.Add(z))
                ZombiesDebug.Log($"zombie's weapon hidden ({hidden} meshes, hands: {body.HandsController?.Item?.LocalizedName() ?? "none"})");
        }

        private static bool IsWeaponModel(Transform t)
        {
            for (int i = 0; t != null && i < 6; i++, t = t.parent)
            {
                string n = t.name.ToLowerInvariant();
                if (n.StartsWith("weapon_") || n.Contains("knife") || n.Contains("melee") || n.Contains("axe") || n.Contains("machete") || n.Contains("sledge"))
                    return true;
            }
            return false;
        }

        private static void Despawn(BotOwner z)
        {
            try
            {
                if (z != null && !z.IsDead)
                    z.LeaveData.RemoveFromMap();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies rounds: despawn failed: {e.Message} at {e.StackTrace?.Replace(System.Environment.NewLine, " | ")}");
            }
        }

        // The exfil swarm: who it brought, and how far the round had got before it
        private static readonly HashSet<BotOwner> SwarmSpawned = new HashSet<BotOwner>();
        private static int _swarmBase;
        private static bool _wasSwarm;

        /// <summary>
        /// The host: the exfil failed. The swarm's zombies still alive leave the map and the round goes back to its own
        /// count (as far as it had got when the lever was pulled).
        /// </summary>
        public static void EndSwarm()
        {
            int gone = 0;
            foreach (BotOwner z in SwarmSpawned.ToList())
            {
                if (z == null || !Alive.ContainsKey(z))
                    continue;
                Alive.Remove(z);
                Despawn(z);
                gone++;
            }
            SwarmSpawned.Clear();
            _spawnedThisRound = Math.Min(_spawnedThisRound, _swarmBase);
            _wasSwarm = false;
            ZombiesPlugin.Log.LogInfo($"Zombies rounds: the exfil swarm's over: {gone} of its zombies left, back to round {Round}'s count ({_spawnedThisRound}/{_toSpawn} spawned)");
        }

        private static readonly Dictionary<BotOwner, float> SpawnedAt = new Dictionary<BotOwner, float>();
        private static float _fewSince = -1f;
        public static ConfigEntry<float> OutsideMost, LastFewMost;

        /// <summary>F12 / the host: straight to this round (the exfil's first, to test it).</summary>
        public static void JumpToRound(int round)
        {
            Round = Math.Max(0, round - 1);
            NextRound();
        }

        /// <summary>F12: every zombie gone now (the round counts them as killed).</summary>
        public static void KillAll()
        {
            foreach (BotOwner z in Alive.Keys.ToList())
                z?.GetPlayer?.ActiveHealthController?.Kill(EDamageType.Undefined);
        }

        // ---------- On screen: the round number (the original's red, Roman numerals to IX) ----------

        private static readonly string[] Roman = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX" };

        /// <summary>the original's round number: Roman numerals up to IX, then the number.</summary>
        public static string RoundText(int round) => round > 0 && round < 10 ? Roman[round] : round.ToString();

        // The original's tally marks for rounds 1 to 10 (four strokes and the fifth across them), digits from 11
        private const int TallyUpTo = 10;
        private static Texture2D _stroke;

        public static void Draw()
        {
            if (Round <= 0 || Enabled?.Value != true)
                return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Overflow };
                // EFT's own font when the game has it for IMGUI (user, 2026-11-11)
                if (ZombiesUi.DisplayImguiFont() != null)
                {
                    _style.font = ZombiesUi.DisplayImguiFont();
                    _style.fontStyle = FontStyle.Bold;
                }
            float baseK = Screen.height / 1080f * (CounterSize?.Value ?? 1f);
            // Round 1's intro: big in the middle, then gliding into the corner (ZombiesRoundFx)
            float middle = ZombiesRoundFx.Intro(out float scale);
            float k = baseK * scale;
            Vector2 size = CounterSize2(Round, k);
            Vector2 corner = CounterSize2(Round, baseK);
            // The corner: top right ("across" = gap from the right edge, "up" = how high, 1 = the top)
            var cornerPos = new Vector2(Screen.width * (1f - (CounterX?.Value ?? 0.02f)) - corner.x, Screen.height * (1f - (CounterY?.Value ?? 0.97f)));
            var middlePos = new Vector2((Screen.width - size.x) / 2f, (Screen.height - size.y) / 2f - Screen.height * 0.08f);
            float x = Mathf.Lerp(cornerPos.x, middlePos.x, middle), y = Mathf.Lerp(cornerPos.y, middlePos.y, middle);
            Color red = ZombiesRoundFx.Colour(new Color(0.75f, 0.05f, 0.05f)), shadow = new Color(0f, 0f, 0f, 0.7f);
            // Zombies left, under the round number in the corner (not while round 1's number is still in the middle)
            if (ShowLeft?.Value == true && middle <= 0.01f)
                DrawLeft(cornerPos.x + corner.x, cornerPos.y + corner.y, baseK, shadow);
            if (Round <= TallyUpTo)
            {
                DrawTally(Round, x + 3f * k, y + 3f * k, k, shadow);
                DrawTally(Round, x, y, k, red);
                return;
            }
            _style.fontSize = Mathf.RoundToInt(72f * k);
            string text = Round.ToString();
            _style.normal.textColor = shadow;
            GUI.Label(new Rect(x + 3f * k, y + 3f * k, size.x + 20f * k, 100f * k), text, _style);
            _style.normal.textColor = red;
            GUI.Label(new Rect(x, y, size.x + 20f * k, 100f * k), text, _style);
        }

        private static GUIStyle _leftStyle;
        private static int _leftShown = -1;
        private static readonly GUIContent LeftText = new GUIContent();

        // "N LEFT" right-aligned under the round counter's corner spot; nothing between rounds (0) or before a count
        private static void DrawLeft(float right, float top, float k, Color shadow)
        {
            int left = Left;
            if (left <= 0 || Event.current.type != EventType.Repaint)
                return;
            if (_leftStyle == null)
                _leftStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight, clipping = TextClipping.Overflow };
                // EFT's own font when the game has it for IMGUI (user, 2026-11-11)
                if (ZombiesUi.DisplayImguiFont() != null)
                {
                    _leftStyle.font = ZombiesUi.DisplayImguiFont();
                    _leftStyle.fontStyle = FontStyle.Bold;
                }
            if (left != _leftShown)
            {
                _leftShown = left;
                LeftText.text = left + (left == 1 ? " ZOMBIE LEFT" : " ZOMBIES LEFT");
            }
            _leftStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(22f * k));
            var r = new Rect(right - 400f * k, top - 6f * k, 400f * k, 30f * k);
            _leftStyle.normal.textColor = shadow;
            GUI.Label(new Rect(r.x + 2f * k, r.y + 2f * k, r.width, r.height), LeftText, _leftStyle);
            _leftStyle.normal.textColor = new Color(0.85f, 0.82f, 0.78f, 0.9f);
            GUI.Label(r, LeftText, _leftStyle);
        }

        // How much room the counter takes at this size (tally strokes, or digits)
        private static Vector2 CounterSize2(int round, float k)
        {
            if (round <= TallyUpTo)
            {
                float step = 17f * k, width = 8f * k, groupGap = 26f * k;
                int groups = (round - 1) / 5, inLast = (round - 1) % 5;
                float w = groups * (4 * step + groupGap) + (inLast >= 4 ? 3 * step + width + 10f * k : inLast * step + width);
                return new Vector2(w, 100f * k);
            }
            _style.fontSize = Mathf.RoundToInt(72f * k);
            Vector2 s = _style.CalcSize(new GUIContent(round.ToString()));
            return new Vector2(s.x, 100f * k);
        }

        private static void DrawTally(int count, float x, float y, float k, Color colour)
        {
            if (_stroke == null)
            {
                _stroke = new Texture2D(1, 1);
                _stroke.SetPixel(0, 0, Color.white);
                _stroke.Apply();
            }
            float height = 72f * k, width = 8f * k, step = 17f * k, groupGap = 26f * k;
            float top = y + 14f * k;
            Color before = GUI.color;
            GUI.color = colour;
            Matrix4x4 matrix = GUI.matrix;
            for (int i = 0; i < count; i++)
            {
                int group = i / 5, inGroup = i % 5;
                float gx = x + group * (4 * step + groupGap);
                if (inGroup < 4)
                {
                    // A stroke, a little crooked like it was painted (the same lean every frame)
                    float lean = Mathf.Sin(i * 12.9898f) * 4f;
                    float sx = gx + inGroup * step, cx = sx + width / 2f, cy = top + height / 2f;
                    GUIUtility.RotateAroundPivot(lean, new Vector2(cx, cy));
                    GUI.DrawTexture(new Rect(sx, top + Mathf.Sin(i * 7.31f) * 3f * k, width, height), _stroke);
                    GUI.matrix = matrix;
                }
                else
                {
                    // The fifth: across the four, corner to corner
                    float span = 3 * step + width, cx = gx + span / 2f, cy = top + height / 2f;
                    float length = Mathf.Sqrt(span * span + height * height) + 10f * k;
                    float angle = Mathf.Atan2(height, span + 10f * k) * Mathf.Rad2Deg;
                    GUIUtility.RotateAroundPivot(-angle - 10f, new Vector2(cx, cy));
                    GUI.DrawTexture(new Rect(cx - length / 2f, cy - width / 2f, length, width), _stroke);
                    GUI.matrix = matrix;
                }
            }
            GUI.color = before;
        }
    }
}
