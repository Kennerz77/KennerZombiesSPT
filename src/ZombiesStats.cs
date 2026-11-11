using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// What each player does in a zombies match (kills, headshots, knife kills, points earned and spent, doors opened,
    /// downs, revives), the original's points for it (hit 10, kill 100, knife kill 150), and the scoreboard: hold Tab (the inventory
    /// key, which a zombies match doesn't open) for the original's table of every player, in their colours. Your own row is counted
    /// here; the others' fill in once the Fika match state is shared.
    /// </summary>
    internal static class ZombiesStats
    {
        public const int HitPoints = 10, KillPoints = 50, HeadKillPoints = 100, KnifeKillPoints = 130; // The original

        public class Stats
        {
            public int Kills, Headshots, KnifeKills, Earned, Spent, Doors = 0, Downs, Revives = 0, Boards;
        }

        private static readonly Dictionary<string, Stats> ByPlayer = new Dictionary<string, Stats>();
        private static bool _hooked, _inventoryPatched;

        public static Stats Of(string profileId)
        {
            if (profileId == null)
                return new Stats();
            if (!ByPlayer.TryGetValue(profileId, out Stats s))
                ByPlayer[profileId] = s = new Stats();
            return s;
        }

        public static Stats Mine => Of(Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId);

        /// <summary>This player's stats as sent to the others (ZombiesNet), in a fixed order.</summary>
        public static int[] MineForNet()
        {
            Stats s = Mine;
            return new[] { s.Kills, s.Headshots, s.KnifeKills, s.Earned, s.Spent, s.Doors, s.Downs, s.Revives, s.Boards };
        }

        /// <summary>A teammate's stats as their game sent them: their row on every board here.</summary>
        public static void SetRemote(string profileId, int[] v)
        {
            if (string.IsNullOrEmpty(profileId) || v == null || v.Length < 9)
                return;
            Stats s = Of(profileId);
            s.Kills = v[0];
            s.Headshots = v[1];
            s.KnifeKills = v[2];
            s.Earned = v[3];
            s.Spent = v[4];
            s.Doors = v[5];
            s.Downs = v[6];
            s.Revives = v[7];
            s.Boards = v[8];
        }

        public static void LogMine()
        {
            Stats s = Mine;
            ZombiesPlugin.Log.LogInfo($"Zombies stats: points {ZombiesMatch.Points}, kills {s.Kills}, headshots {s.Headshots}, knife kills {s.KnifeKills}, " +
                                      $"earned {s.Earned}, spent {s.Spent}, doors {s.Doors}, downs {s.Downs}, revives {s.Revives}");
        }

        public static void ResetMine()
        {
            string id = Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
            if (id != null)
                ByPlayer.Remove(id);
        }

        public static void Reset()
        {
            ByPlayer.Clear();
            ZombiesTeam.ResetRoster();
            if (!_hooked)
            {
                _hooked = true;
                Player.OnPlayerDeadStatic += OnDead;
            }
        }

        // A zombie killed by you: the original's kill points (knife more), and the count
        private static void OnDead(Player victim, IPlayer aggressor, DamageInfo damage, EBodyPart part)
        {
            // The host tells everyone a zombie died (Fika's own death didn't always reach the others: zombies killed
            // by a Ray Gun stood on in the other game until the body clear, user 2026-11-09)
            if (ZombiesMatch.Active && victim != null && ZombiesNet.Online && ZombiesNet.IsHost && ZombiesTeam.IsZombie(victim))
                ZombiesNet.ZombieDied(victim.ProfileId);
            if (!ZombiesMatch.Active || victim == null || !ZombiesTeam.IsZombie(victim) || aggressor == null || ZombiesTeam.IsZombieSource(aggressor))
                return;
            bool knife = damage.DamageType == EDamageType.Melee;
            // Deadshot: a bullet kill by you is a headshot kill
            bool head = part == EBodyPart.Head || aggressor.IsYourPlayer && ZombiesPerks.EveryHitHead && !knife
                        && damage.DamageType != EDamageType.Explosion && damage.DamageType != EDamageType.GrenadeFragment;
            // Zombies die on the host's game (Fika): it tells a teammate their kill; a client counts only those
            if (ZombiesNet.Online && !ZombiesNet.IsHost)
                return;
            if (aggressor.IsYourPlayer)
                Killed(head, knife);
            else
                ZombiesNet.KillBy(aggressor.ProfileId, head, knife);
        }

        private static readonly System.Collections.Generic.Queue<float> _recent = new System.Collections.Generic.Queue<float>();

        /// <summary>A zombie killed by the local player (here, or told by the host).</summary>
        public static void Killed(bool head, bool knife)
        {
            Stats me = Mine;
            me.Kills++;
            if (head)
                me.Headshots++;
            // The original's kill lines: a streak (5 in 4 s), insta-kills, headshots, now and then
            _recent.Enqueue(Time.time);
            while (_recent.Count > 0 && Time.time - _recent.Peek() > 4f)
                _recent.Dequeue();
            if (_recent.Count >= 5)
                ZombiesVox.Say("kill_streak", 0.6f, 30f);
            else if (ZombiesPowerUps.InstaKillOn)
                ZombiesVox.Say("kill_insta", 0.2f, 20f);
            else if (head)
                ZombiesVox.Say("kill_headshot", 0.15f, 12f);
            if (knife)
                me.KnifeKills++;
            ZombiesMatch.AddPoints(knife ? KnifeKillPoints : head ? HeadKillPoints : KillPoints);
        }

        /// <summary>A hit of yours on a zombie (ZombiesRules' damage prefixes).</summary>
        public static void Hit()
        {
            ZombiesMatch.AddPoints(HitPoints);
        }

        // ---------- Tab: the scoreboard instead of the inventory ----------

        /// <summary>Every player owner's inventory key (EFT's and Fika's): does nothing in a zombies match.</summary>
        public static void PatchInventoryKey(Harmony harmony)
        {
            if (_inventoryPatched)
                return;
            _inventoryPatched = true;
            int patched = 0;
            foreach (Type t in AccessTools.AllTypes().Where(t => typeof(GamePlayerOwner).IsAssignableFrom(t)))
            {
                MethodInfo m = t.GetMethod(nameof(GamePlayerOwner.TranslateInventoryScreenInput),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (m == null || m.IsAbstract)
                    continue;
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesStats), nameof(NoInventory)));
                patched++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies: inventory key is the scoreboard in matches ({patched} owners)");
        }

        private static bool NoInventory(ECommand command, ref bool __result)
        {
            if (!ZombiesMatch.Active || !command.IsCommand(ECommand.ToggleInventory))
                return true;
            __result = true;
            return false;
        }

        private static GUIStyle _cell, _head;
        private static Texture2D _panel;
        private static readonly string[] Columns = { "SCORE", "POINTS", "KILLS", "HEADSHOTS", "DOWNS", "REVIVES", "DOORS" };

        /// <summary>
        /// A player's match score, which places them on the board (most first): every point earned, plus 10 a kill,
        /// 10 more a headshot, 20 more a knife kill, 150 a revive, 50 a door or debris opened and 250 a round survived,
        /// less 200 a time down. Never below 0.
        /// </summary>
        public static int Score(Stats s, int rounds)
        {
            if (s == null)
                return 0;
            int score = s.Earned + s.Kills * 10 + s.Headshots * 10 + s.KnifeKills * 20 + s.Revives * 150 + s.Doors * 50
                        + Mathf.Max(0, rounds) * 250 - s.Downs * 200;
            return Mathf.Max(0, score);
        }

        /// <summary>A player's points now (yours, or a teammate's as the network last said).</summary>
        public static int PointsOf(Player p) =>
            p == null ? 0 : p.IsYourPlayer ? ZombiesMatch.Points : ZombiesNet.TeamPoints.TryGetValue(p.ProfileId, out int pts) ? pts : 0;

        /// <summary>The board's rows, best score first: name, colour, and the values under Columns.</summary>
        public static List<(string name, Color colour, string[] values, int score)> Rows()
        {
            int rounds = Mathf.Max(0, ZombiesRounds.Round - 1);
            var rows = new List<(string, Color, string[], int)>();
            ZombiesTeam.List(); // keeps the roster up to date
            string myId = Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;
            // Everyone who played this match, dead or out included (the roster); before it fills, the team now
            var people = ZombiesTeam.Roster.Count > 0 ? ZombiesTeam.Roster.ToList()
                : ZombiesTeam.List().Where(p => p != null).Select(p => (p.ProfileId, p.Profile?.Nickname ?? "", p)).ToList();
            foreach (var (id, name, p) in people)
            {
                bool alive = p != null && p.gameObject != null;
                Color colour = ScoresHud.PlayerColours[Mathf.Clamp(ZombiesTeam.ColourOf(id, alive ? p : null), 1, 4) - 1];
                bool mine = id == myId;
                Stats st = mine || ByPlayer.ContainsKey(id) ? Of(id) : null;
                int score = Score(st, rounds);
                int points = mine ? ZombiesMatch.Points : alive ? PointsOf(p) : ZombiesNet.TeamPoints.TryGetValue(id, out int last) ? last : 0;
                string[] values = st == null
                    ? new[] { "-", points.ToString(), "-", "-", "-", "-", "-" }
                    : new[] { score.ToString(), points.ToString(), st.Kills.ToString(), st.Headshots.ToString(), st.Downs.ToString(),
                        st.Revives.ToString(), st.Doors.ToString() };
                rows.Add((name, colour, values, score));
            }
            return rows.OrderByDescending(r => r.Item4).ToList();
        }

        private static float _sessionFrom = -1f, _pausedAtStart;

        /// <summary>The player is in (kit on, the black fading): the session clock starts.</summary>
        public static void SessionStarted()
        {
            _sessionFrom = Time.realtimeSinceStartup;
            _pausedAtStart = ZombiesPause.PausedTotal;
        }

        // How long this match has been played, paused time left out (worked out when the board is drawn, so it's
        // always right the moment Tab is held)
        /// <summary>Seconds of this match so far (pauses left out), 0 before it starts.</summary>
        public static float SessionSeconds() =>
            _sessionFrom < 0f || !ZombiesMatch.Active ? 0f : Mathf.Max(0f, Time.realtimeSinceStartup - _sessionFrom - (ZombiesPause.PausedTotal - _pausedAtStart));

        public static string SessionTime()
        {
            if (_sessionFrom < 0f || !ZombiesMatch.Active)
                return "--:--";
            var t = TimeSpan.FromSeconds(Mathf.Max(0f, Time.realtimeSinceStartup - _sessionFrom - (ZombiesPause.PausedTotal - _pausedAtStart)));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        }

        public static void DrawBoard(bool force = false, float top = -1f)
        {
            if (!force && !Input.GetKey(ZombiesControls.Key(ZombiesControls.Act.Scoreboard)))
                return;
            if (Event.current.type != EventType.Repaint)
                return;
            // The zombies menus' look (user, 2026-11-11): a glass panel, a row face per player in their colour, your own
            // row lit with the red accent, the numbers in the display font
            float k = Screen.height / 1080f;
            if (_cell == null || _boardAt != Screen.height)
            {
                _boardAt = Screen.height;
                // EFT's Bender in bold: clear at a glance (user, 2026-11-11)
                _cell = ZombiesUi.ImguiLabel(Mathf.RoundToInt(26f * k), Color.white, TextAnchor.MiddleCenter, false);
                _cell.fontStyle = FontStyle.Bold;
                _cell.wordWrap = false;
                _head = ZombiesUi.ImguiLabel(Mathf.RoundToInt(16f * k), ZombiesUi.Dim, TextAnchor.MiddleCenter, false);
                _head.fontStyle = FontStyle.Bold;
                _head.wordWrap = false;
                _nameStyle = ZombiesUi.ImguiLabel(Mathf.RoundToInt(24f * k), Color.white, TextAnchor.MiddleLeft, false);
                _nameStyle.fontStyle = FontStyle.Bold;
                _nameStyle.wordWrap = false;
            }
            var ranked = Rows();
            IList<ScoresHud.Row> dummies = ScoresHud.Dummies;
            int rows = ranked.Count + (dummies?.Count ?? 0);
            float nameW = 320f * k, colW = 132f * k, rowH = 52f * k, gap = 6f * k;
            float w = nameW + colW * Columns.Length, h = 44f * k + (rowH + gap) * rows + 46f * k;
            float x = (Screen.width - w) * 0.5f, y = top < 0f ? (Screen.height - h) * 0.5f : Screen.height * top;
            ZombiesUi.ImguiPanel(new Rect(x - 22f * k, y - 18f * k, w + 44f * k, h + 30f * k));
            Color was = GUI.color;
            GUI.color = new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.9f);
            GUI.DrawTexture(new Rect(x, y + 40f * k, w, 2f * k), ZombiesUi.Tex(ZombiesUi.Divider));
            GUI.color = was;
            _head.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(x + 16f * k, y, nameW, 40f * k), "PLAYER", _head);
            _head.alignment = TextAnchor.MiddleCenter;
            for (int c = 0; c < Columns.Length; c++)
                GUI.Label(new Rect(x + nameW + c * colW, y, colW, 40f * k), Columns[c], _head);
            string me = Singleton<GameWorld>.Instance?.MainPlayer?.Profile?.Nickname;
            float ry = y + 48f * k;
            void Row(int rank, string name, Color colour, string[] values, bool mine)
            {
                var r = new Rect(x, ry, w, rowH);
                GUI.Box(r, GUIContent.none, ZombiesUi.Style(ZombiesUi.RowFace));
                // The player's colour as a bar on the left; your own row lit red
                GUI.color = colour;
                GUI.DrawTexture(new Rect(r.x + 3f * k, r.y + 6f * k, 4f * k, r.height - 12f * k), Texture2D.whiteTexture);
                if (mine)
                {
                    GUI.color = new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.35f);
                    GUI.DrawTexture(new Rect(r.x + 8f * k, r.y + 2f * k, r.width * 0.5f, r.height - 4f * k), ZombiesUi.Tex(ZombiesUi.SweepLeft));
                }
                GUI.color = was;
                ZombiesHud.Label(new Rect(x + 18f * k, ry, nameW - 20f * k, rowH), $"{rank}. {name}" + (mine ? "  (YOU)" : ""), _nameStyle, colour, k);
                for (int c = 0; c < Columns.Length; c++)
                    ZombiesHud.Label(new Rect(x + nameW + c * colW, ry, colW, rowH), values[c], _cell, c == 0 ? ZombiesHud.Gold : Color.white, k);
                ry += rowH + gap;
            }
            for (int i = 0; i < ranked.Count; i++)
                Row(i + 1, ranked[i].name, ranked[i].colour, ranked[i].values, ranked[i].name == me);
            // F12's dummy players (to lay the scores out before there's a team)
            for (int d = 0; dummies != null && d < dummies.Count; d++)
            {
                ScoresHud.Row row = dummies[d];
                string[] values = { (row.Points * 2).ToString(), row.Points.ToString(), (row.Points / 120).ToString(), (row.Points / 400).ToString(), "1", "0", "2" };
                Row(ranked.Count + d + 1, $"Dummy {row.Colour}", ScoresHud.PlayerColours[Mathf.Clamp(row.Colour, 1, 4) - 1], values, false);
            }
            // Bottom right of the board: the session's time (zombies has no raid clock)
            _head.alignment = TextAnchor.MiddleRight;
            GUI.Label(new Rect(x, y + h - 36f * k, w, 30f * k), $"SESSION TIME ELAPSED  {SessionTime()}", _head);
            _head.alignment = TextAnchor.MiddleCenter;
        }

        private static GUIStyle _nameStyle;
        private static int _boardAt;
    }
}
