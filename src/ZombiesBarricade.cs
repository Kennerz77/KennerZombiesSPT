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
    /// A barricade's boards in a zombies match : zombies tear them off one at a time (TearBoard; the zombie AI will
    /// call it, F12 can for now), and a player standing at it holding the interact key nails them back one by one,
    /// 10 points a board, up to a cap of rebuild points per player per round (the original's rebuild_barrier_cap_per_round, 500;
    /// NewRound resets it). Players never get through a window, boards or not; zombies climb in once all six are off.
    /// Torn boards fall away and vanish; rebuilt ones fly back into place.
    /// </summary>
    internal class ZombiesBarricade : MonoBehaviour
    {
        private const float RepairRange = 2.2f, BoardSeconds = 1.0f, FlySeconds = 0.55f;
        public const int PointsPerBoard = 10;

        public static ConfigEntry<int> RebuildCap;
        private static int _rebuiltThisRound;
        private static readonly List<ZombiesBarricade> All = new List<ZombiesBarricade>();
        private static GUIStyle _style;

        private class Board
        {
            public Transform T;
            public Vector3 Pos;
            public Quaternion Rot;
            public bool Up = true;
        }

        private readonly List<Board> _boards = new List<Board>();
        private float _nextBoard;

        public static void Bind(ConfigFile config, string section)
        {
            RebuildCap = config.Bind(section, "Barricade rebuild points per round", 500, new ConfigDescription(
                "Most points a player gets from rebuilding barricade boards in one round; 10 a board. Below it: round 1 pays 4 boards, then 2 more boards each round.",
                new AcceptableValueRange<int>(0, 5000)));
        }

        /// <summary>The barricade nearest this point within reach, or none.</summary>
        public static ZombiesBarricade Nearest(Vector3 p, float reach, System.Func<ZombiesBarricade, bool> also = null) => All
            .Where(b => b != null && b._boards.Count > 0 && (also == null || also(b)))
            .OrderBy(b => Vector3.Distance(b.transform.position, p)).FirstOrDefault(b => Vector3.Distance(b.transform.position, p) <= reach);

        /// <summary>A new round: everyone can earn rebuild points again.</summary>
        public static void NewRound() => _rebuiltThisRound = 0;

        /// <summary>
        /// How many points rebuilding pays this round: round 1 only 4 boards, then 2 boards more each round, never past
        /// the F12 cap (the original: 500).
        /// </summary>
        public static int PaidPointsThisRound()
        {
            int boards = 4 + 2 * Mathf.Max(0, ZombiesRounds.Round - 1);
            return Mathf.Min(boards * PointsPerBoard, RebuildCap.Value);
        }

        // ---------- Shared with the team (ZombiesNet): the host's boards are everyone's ----------

        /// <summary>Its layout entry's number (the same on every player's game).</summary>
        public int Entry = -1;

        /// <summary>The zone set on it in F8 (0: none set, worked out from where it stands).</summary>
        public int Zone;

        private static ZombiesBarricade ByEntry(int entry) => All.FirstOrDefault(b => b != null && b.Entry == entry);

        public static List<(int entry, int up)> AllBoards() => All.Where(b => b != null && b.Entry >= 0).Select(b => (b.Entry, b.BoardsUp)).ToList();

        /// <summary>The host says how many boards are up: torn or put back here to match.</summary>
        public static void SetBoards(int entry, int up)
        {
            ZombiesBarricade b = ByEntry(entry);
            if (b == null)
                return;
            while (b.BoardsUp > up && b.TearBoard())
            {
            }
            while (b.BoardsUp < up && b.PutBoardBack())
            {
            }
        }

        /// <summary>The host: a client put a board back (its points are its own).</summary>
        public static void RebuildRemote(int entry)
        {
            ZombiesBarricade b = ByEntry(entry);
            if (b != null && b.PutBoardBack())
                ZombiesNet.Boards(b.Entry, b.BoardsUp, always: true);
        }

        private bool PutBoardBack()
        {
            Board b = _boards.FirstOrDefault(x => !x.Up);
            if (b == null)
                return false;
            b.Up = true;
            // The slam comes as it lands (Fly)
            StartCoroutine(Fly(b, true));
            return true;
        }

        public int BoardsUp => _boards.Count(b => b.Up);

        private void Start()
        {
            // The window model's six boards (zombies.bundle zm_window: Board1..6)
            // Board1..Board6 only (the model's "Boards" holder holds all six: tearing it took the whole barricade)
            foreach (Transform t in GetComponentsInChildren<Transform>(true)
                         .Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.name, @"^Board\d+$")).OrderBy(t => t.name))
                _boards.Add(new Board { T = t, Pos = t.localPosition, Rot = t.localRotation });
            All.Add(this);
        }

        private void OnDestroy() => All.Remove(this);

        // One board at a time per barricade, however many zombies are at it (the original: one zombie works a window)
        public const float TearGap = 1.5f;
        private float _nextTear;

        /// <summary>A zombie tears off the next board, if the barricade's ready for another.</summary>
        public bool TryTearBoard() => ClaimTear() && TearBoard();

        /// <summary>This barricade's next board is someone's to rip (one at a time, the original's pace), if it's ready.</summary>
        public bool ClaimTear() => ClaimTear(TearGap);

        public bool ClaimTear(float gap)
        {
            if (Time.time < _nextTear || BoardsUp == 0)
                return false;
            _nextTear = Time.time + gap;
            return true;
        }

        /// <summary>The next board off (F12 strips them all with this): it's pulled outward, falls and goes.</summary>
        public bool TearBoard()
        {
            Board b = _boards.LastOrDefault(x => x.Up);
            if (b == null)
                return false;
            b.Up = false;
            ZombiesSounds.At("zm_board_snap_#", b.T.position, 0.9f);
            StartCoroutine(Fall(b));
            if (ZombiesNet.IsHost)
                ZombiesNet.Boards(Entry, BoardsUp);
            return true;
        }

        private IEnumerator Fall(Board b)
        {
            Vector3 from = b.T.localPosition;
            // Pulled out towards the zombie (the window's front, +Z, is the outside), then down
            Vector3 drop = b.T.parent.InverseTransformDirection(transform.forward * 0.6f + Vector3.down * 1.2f);
            for (float t = 0f; t < 0.5f; t += Time.deltaTime)
            {
                b.T.localPosition = from + drop * (t / 0.5f) * (t / 0.5f);
                yield return null;
            }
            if (!b.Up)
                b.T.gameObject.SetActive(false);
        }

        private static IEnumerator Hammer(Board b, Vector3 at)
        {
            // Once the slam's crash has rung out: under it they were never heard (the user, 2026-11-08, twice). The
            // hammer files are 8 dB louder too (data/sounds, originals in 1_Releases\Backup\hammer_before_boost_20261108)
            // A board torn off again before a hit: no more hammering (user, 2026-11-10: rebuild sounds as zombies broke
            // a fresh barricade)
            yield return new WaitForSeconds(0.45f);
            if (!b.Up)
                yield break;
            ZombiesSounds.At("zm_board_hammer_#", at, 1f, 30f);
            yield return new WaitForSeconds(Random.Range(0.26f, 0.32f));
            if (!b.Up)
                yield break;
            ZombiesSounds.At("zm_board_hammer_#", at, 1f, 30f);
        }

        // The original's rebuild: the board comes up off the ground outside (where it fell), turning over as it rises, arcs into
        // its place and slams home: the slam sound, a shudder of the boards and a puff of dust.
        private IEnumerator Fly(Board b, bool slam = false)
        {
            b.T.gameObject.SetActive(true);

            // The original's board lifting off the ground (zmb\blockers\boards\float)
            ZombiesSounds.At("zm_board_float_#", b.T.position, 0.45f);
            Vector3 start = b.Pos + b.T.parent.InverseTransformDirection(transform.forward * 0.7f + Vector3.down * 1.3f);
            Vector3 lift = b.T.parent.InverseTransformDirection(Vector3.up * 0.6f);
            Quaternion spin = Quaternion.AngleAxis(Random.Range(-120f, 120f), Vector3.forward) * Quaternion.AngleAxis(Random.Range(60f, 110f), Vector3.right);
            for (float t = 0f; t < FlySeconds; t += Time.deltaTime)
            {
                // Torn off again while still flying in (a zombie at a just-rebuilt window): the flight stops here, no
                // slam or hammering for a board that never landed (user, 2026-11-09: rebuild sounds with nobody
                // rebuilding)
                if (!b.Up)
                    yield break;
                float u = t / FlySeconds, ease = 1f - (1f - u) * (1f - u);
                b.T.localPosition = Vector3.Lerp(start, b.Pos, ease) + lift * Mathf.Sin(u * Mathf.PI);
                b.T.localRotation = Quaternion.Slerp(b.Rot * spin, b.Rot, ease);
                yield return null;
            }
            if (!b.Up)
                yield break;
            b.T.localPosition = b.Pos;
            b.T.localRotation = b.Rot;
            if (!slam)
                yield break;
            // The board hits the wall: the original's slam and its low thud (no lightning: that's for doors and the box)
            ZombiesSounds.At("zm_board_slam_#", b.T.position, 0.7f);
            ZombiesSounds.At("zm_board_lfe_#", b.T.position, 0.7f);
            // Then nailed on: two of the original's repair hammer hits (zm_board_repair split into single hits)
            ZombiesPlugin.Instance.StartCoroutine(Hammer(b, b.T.position));
            // The whole barricade shudders with the hit
            for (float t = 0f; t < 0.18f; t += Time.deltaTime)
            {
                float shake = (1f - t / 0.18f) * 0.012f;
                foreach (Board o in _boards.Where(x => x.Up))
                    o.T.localPosition = o.Pos + Random.insideUnitSphere * shake;
                yield return null;
            }
            foreach (Board o in _boards.Where(x => x.Up))
                o.T.localPosition = o.Pos;
        }

        /// <summary>Carpenter: every barricade's boards back at once.</summary>
        public static void RepairAll()
        {
            // One whole barricade at a time (the user, 2026-11-08), nearest the player first: its boards fly in one
            // after another, each slammed and nailed, then the next barricade
            ZombiesPlugin.Instance.StartCoroutine(RepairInTurn());
        }

        private const float CarpenterBoardGap = 0.18f, CarpenterBarricadeGap = 0.35f;

        private static IEnumerator RepairInTurn()
        {
            Vector3 me = Singleton<GameWorld>.Instance?.MainPlayer?.Transform.position ?? Vector3.zero;
            foreach (ZombiesBarricade b in All.Where(x => x != null && x._boards.Any(o => !o.Up))
                         .OrderBy(x => Vector3.Distance(x.transform.position, me)).ToList())
            {
                if (b == null || !ZombiesMatch.Active)
                    continue;
                foreach (Board board in b._boards.Where(x => !x.Up).ToList())
                {
                    if (b == null)
                        break;
                    board.Up = true;
                    b.StartCoroutine(b.Fly(board, true));
                    if (ZombiesNet.IsHost)
                        ZombiesNet.Boards(b.Entry, b.BoardsUp);
                    yield return new WaitForSeconds(CarpenterBoardGap);
                }
                yield return new WaitForSeconds(FlySeconds + CarpenterBarricadeGap);
            }
        }

        private bool Near(Player p) => p != null && Vector3.Distance(p.Transform.position, transform.position) <= RepairRange;

        private void Update()
        {
            if (!ZombiesMatch.Active || _boards.Count == 0 || BoardsUp == _boards.Count)
                return;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (!Near(me) || ZombiesDowned.Mine != ZombiesDowned.State.Up || !Input.GetKey(ZombiesControls.Key(ZombiesControls.Act.Use)) || Time.time < _nextBoard)
                return;
            _nextBoard = Time.time + BoardSeconds;
            // The host's boards are everyone's: the host puts it back and tells the rest; a client only asks, and its
            // board comes up with the host's count. (A client's own board going up first raced the host's counts: a
            // zombie's tear in between took it down and the count after flew it up again, slam and hammer, on that
            // player's game: user, 2026-11-10)
            if (ZombiesNet.IsHost)
            {
                if (!PutBoardBack())
                    return;
                ZombiesNet.Boards(Entry, BoardsUp);
            }
            else
                ZombiesNet.Rebuilt(Entry);
            ZombiesStats.Mine.Boards++; // for the match's XP (every board, paid or not)
            if (_rebuiltThisRound + PointsPerBoard <= PaidPointsThisRound())
            {
                _rebuiltThisRound += PointsPerBoard;
                ZombiesMatch.AddPoints(PointsPerBoard);
                ZombiesVox.Say("rebuild_boards", 0.15f, 25f);
                // The purchase accept as each paid board's 10 points come in
                ZombiesSounds.Head("zm_purchase", 1f);
            }
        }

        /// <summary>The "rebuild" prompt for the barricade you're at, if it's missing boards.</summary>
        public static void DrawPrompt()
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            ZombiesBarricade at = All.FirstOrDefault(b => b != null && b._boards.Count > 0 && b.BoardsUp < b._boards.Count && b.Near(me));
            if (at == null || ZombiesDowned.Mine != ZombiesDowned.State.Up)
                return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            float k = Screen.height / 1080f;
            _style.fontSize = Mathf.RoundToInt(20f * k);
            _style.normal.textColor = Color.white;
            // The zombies HUD's prompt (user, 2026-11-11)
            ZombiesHud.Prompt("Hold", ZombiesControls.KeyName(ZombiesControls.Act.Use), "to rebuild barrier", null, true, false, 0.58f, k);
        }

        /// <summary>F12: tears every board off the barricade nearest you (to try rebuilding).</summary>
        public static void TearNearest(Player me)
        {
            ZombiesBarricade b = All.Where(x => x != null).OrderBy(x => Vector3.Distance(x.transform.position, me.Transform.position)).FirstOrDefault();
            if (b == null)
            {
                ZombiesPlugin.Notify("No barricade in this match.", true);
                return;
            }
            while (b.TearBoard())
            {
            }
            ZombiesPlugin.Log.LogInfo("Zombies: nearest barricade torn down (F12)");
        }
    }

    /// <summary>
    /// The original's perk machine secret: going prone in front of a perk machine gives 100 points, once per machine per match, to
    /// whoever gets there first (user, 2026-11-09: both players had taken it). With Fika the host decides who was first
    /// (a client asks, the host answers everyone); solo or as the host it's decided here.
    /// </summary>
    internal class PerkProneBonus : MonoBehaviour
    {
        private const float Range = 2.2f;
        private const int Points = 100;
        private float _askedAt = -10f;

        // Machines whose change is gone this match (by Key), on every game
        internal static readonly HashSet<string> Taken = new HashSet<string>();

        private string Key => $"{name}@{Mathf.RoundToInt(transform.position.x)},{Mathf.RoundToInt(transform.position.z)}";

        private void Update()
        {
            if (!ZombiesMatch.Active || Taken.Contains(Key))
                return;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null || !me.IsInPronePose || ZombiesDowned.Mine != ZombiesDowned.State.Up
                || Vector3.Distance(me.Transform.position, transform.position) > Range)
                return;
            if (ZombiesNet.Online && !ZombiesNet.IsHost)
            {
                // Asked already, waiting for the host's answer
                if (Time.time - _askedAt < 2f)
                    return;
                _askedAt = Time.time;
                ZombiesNet.AskChange(Key);
                return;
            }
            Claim(Key, me.ProfileId);
        }

        /// <summary>The host (or solo): the first one to ask for this machine's change gets it, and everyone's told.</summary>
        internal static void Claim(string key, string who)
        {
            if (!Taken.Add(key))
                return;
            ZombiesNet.ChangeTaken(key, who);
            Given(key, who);
        }

        /// <summary>A machine's change is gone; if it went to this player, the points and the sound.</summary>
        internal static void Given(string key, string who)
        {
            Taken.Add(key);
            if (who != Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId)
                return;
            ZombiesMatch.AddPoints(Points);
            // The original's purchase accept, as for the loose change it is (the user, 2026-11-07)
            ZombiesSounds.Head("zm_purchase", 1f);
            ZombiesPlugin.Log.LogInfo($"Zombies: prone at {key}, +{Points}");
        }
    }

    /// <summary>
    /// The Quick Revive machine : look at it for "Buy Quick Revive (price)". Solo it works without the power and
    /// costs 500, then 1500, then 2000; with a team it needs the power and costs 1500. You can't hold two; going down uses
    /// it up. Solo, after its third self-revive the machine leaves for good (ZombiesDowned).
    /// </summary>
    internal class QuickReviveMachine : MonoBehaviour
    {
        private const float InteractDistance = 2.2f;
        private static readonly int[] SoloPrices = { 500, 1500, 2500 }; // The original solo: 500, then 1500, then 2500 (the user, 2026-11-07)
        private const int TeamPrice = 1500;
        private readonly ZombiesInteraction _menu = new ZombiesInteraction();

        private static bool Solo => ZombiesTeam.List().Count <= 1;

        private int Price => Solo ? SoloPrices[Mathf.Clamp(ZombiesDowned.SoloBuys, 0, SoloPrices.Length - 1)] : TeamPrice;

        private void Update()
        {
            if (!ZombiesMatch.Active || ZombiesDowned.QuickReviveGone || Solo && ZombiesDowned.SoloBuys >= SoloPrices.Length && ZombiesDowned.HasQuickRevive())
            {
                _menu.Reset();
                return;
            }
            if (ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
        }

        private void OnDestroy() => _menu.Reset();

        private IEnumerable<(string, System.Action)> Options()
        {
            if (!Solo && !ZombiesMatch.PowerIsOn)
                yield return ("Quick Revive (needs the power)", null);
            else if (ZombiesDowned.HasQuickRevive())
                yield return ("Quick Revive (you have it)", null);
            else if (ZombiesPerks.Drinking)
                yield return ("Quick Revive", null);
            else if (Solo && ZombiesDowned.SoloBuys >= SoloPrices.Length)
                yield return ("Quick Revive (none left)", null);
            else
                yield return ($"Buy Quick Revive ({Price})", ZombiesMatch.Afford(Price, Buy));
        }

        private void Buy()
        {
            if (ZombiesPerks.Drinking || ZombiesDowned.HasQuickRevive() || !ZombiesMatch.Spend(Price))
                return;
            ZombiesPerks.StartedDrinking("revive");
            // Counted as bought now (its price climbs); drunk through Perk Beverages' bottle when it's there
            ZombiesPlugin.Log.LogInfo($"Zombies perks: bought Quick Revive for {Price} ({(Solo ? "solo" : "team")})");
            ZombiesDowned.CountQuickReviveBuy();
            GetComponent<ZombiesMachineAudio>()?.Bought();
            ZombiesPlugin.Instance.StartCoroutine(ZombiesPerks.Drink("revive", ZombiesDowned.GiveQuickRevive));
        }
    }
}
