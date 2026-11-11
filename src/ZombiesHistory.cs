using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Every finished match, kept for good: your own numbers (rounds, kills, headshots, knife kills, downs, revives,
    /// doors, boards, points, score, XP, Dr. Kennerz points), one line per match in stats\history.json beside the plugin
    /// (the user, 2026-11-08). The zombies main menu's STATISTICS page (ZombiesStatsScreen) reads it back.
    /// </summary>
    internal static class ZombiesHistory
    {
        public class Match
        {
            public string When, Map, Time;
            public int Rounds, Kills, Headshots, KnifeKills, Downs, Revives, Doors, Boards, Earned, Spent, Score, Xp, Kp;
            public float Seconds;
        }

        private static List<Match> _all;
        private static string _allFor;

        // One file per profile (stats\history_<profile id>.json): two players on one install (Fika testers) kept one
        // shared file, and everyone's statistics page showed the same matches (user, 2026-11-11)
        private static string ProfileId => SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile?.ProfileId;

        private static string FilePath => Path.Combine(ZombiesPlugin.Dir, "stats", $"history_{ProfileId ?? "unknown"}.json");

        // The old shared file: the first profile to start takes it over (a player's own PC has just the one profile)
        private static string OldFile => Path.Combine(ZombiesPlugin.Dir, "stats", "history.json");

        public static IReadOnlyList<Match> All
        {
            get
            {
                string id = ProfileId;
                if (_all != null && _allFor == id)
                    return _all;
                _all = null;
                _allFor = id;
                try
                {
                    if (id != null && !File.Exists(FilePath) && File.Exists(OldFile))
                    {
                        File.Move(OldFile, FilePath);
                        ZombiesPlugin.Log.LogInfo($"Zombies statistics: the old shared history is now profile {id}'s");
                    }
                    _all = File.Exists(FilePath) ? JsonConvert.DeserializeObject<List<Match>>(File.ReadAllText(FilePath)) : null;
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies statistics: history couldn't be read ({e.Message})");
                }
                if (_all == null)
                    _all = new List<Match>();
                return _all;
            }
        }

        /// <summary>The match that just ended, added (the report keeps it, once per match).</summary>
        public static void Add(Match m)
        {
            try
            {
                var list = (List<Match>)All;
                list.Add(m);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(list, Formatting.Indented));
                ZombiesPlugin.Log.LogInfo($"Zombies statistics: match {list.Count} kept ({m.Rounds} rounds, {m.Kills} kills)");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies statistics: the match couldn't be kept ({e.Message})");
            }
        }

        /// <summary>Your numbers in this match.</summary>
        public static Match Now(int rounds, ZombiesXp.Result xp)
        {
            ZombiesStats.Stats s = ZombiesStats.Mine ?? new ZombiesStats.Stats();
            return new Match
            {
                When = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Map = ZombiesMatch.LocationHere ?? "",
                Time = ZombiesStats.SessionTime(),
                Seconds = ZombiesStats.SessionSeconds(),
                Rounds = rounds,
                Kills = s.Kills,
                Headshots = s.Headshots,
                KnifeKills = s.KnifeKills,
                Downs = s.Downs,
                Revives = s.Revives,
                Doors = s.Doors,
                Boards = s.Boards,
                Earned = s.Earned,
                Spent = s.Spent,
                Score = ZombiesStats.Score(s, rounds),
                Xp = xp?.Xp ?? 0,
                Kp = xp?.Points ?? 0,
            };
        }
    }

    /// <summary>
    /// The zombies main menu's STATISTICS page: lifetime totals and bests from ZombiesHistory, and a bar chart of the
    /// last matches (three bars each: rounds, kills, headshots). Centred over everything on the menus' glass panel;
    /// BACK or Esc closes it. The same page shows another player's statistics (their game sent them to the server):
    /// "NAME'S STATISTICS", LOADING until they arrive.
    /// </summary>
    internal class ZombiesStatsScreen : MonoBehaviour
    {
        private static bool _open;
        private GUIStyle _title, _label, _value, _small, _button;
        private Texture2D _tile, _white;
        private int _styledAt;

        // Someone else's page: their id and name, and their matches once the server's answered (null: still loading)
        private static string _otherId, _otherName;
        private static List<ZombiesHistory.Match> _other;
        private static bool _otherLoading;

        public static bool Open => _open;

        public static void Show()
        {
            _otherId = null;
            _other = null;
            _otherLoading = false;
            _open = true;
            ZombiesUi.Sound("open");
        }

        /// <summary>Another player's page, LOADING until SetOther brings their matches.</summary>
        public static void ShowOther(string id, string name)
        {
            _otherId = id;
            _otherName = string.IsNullOrEmpty(name) ? "Player" : name;
            _other = null;
            _otherLoading = true;
            _open = true;
            ZombiesUi.Sound("open");
        }

        public static void SetOther(string id, string name, List<ZombiesHistory.Match> matches)
        {
            if (id != _otherId)
                return;
            if (!string.IsNullOrEmpty(name))
                _otherName = name;
            _other = matches ?? new List<ZombiesHistory.Match>();
            _otherLoading = false;
        }

        private static void Close()
        {
            _open = false;
            _otherId = null;
            _other = null;
            ZombiesUi.Sound("back");
        }

        private static readonly Color Red = new Color(0.75f, 0.05f, 0.05f);
        private static readonly Color[] BarColours =
        {
            new Color(0.78f, 0.08f, 0.06f), // rounds
            new Color(0.85f, 0.7f, 0.2f),   // kills
            new Color(0.85f, 0.85f, 0.85f), // headshots
        };
        private static readonly string[] BarNames = { "ROUNDS", "KILLS", "HEADSHOTS" };

        private static Texture2D Pixel(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void Update()
        {
            if (_open && !ZombiesReport.InMenuNow() && !ZombiesFrontEnd.InMatchMenu)
                _open = false;
        }

        private void OnGUI()
        {
            if (!_open)
                return;
            float k = Screen.height / 1080f;
            if (_title == null || _styledAt != Screen.height)
            {
                _styledAt = Screen.height;
                _title = ZombiesUi.ImguiLabel(Mathf.RoundToInt(48f * k), Red, TextAnchor.MiddleCenter, true);
                _label = ZombiesUi.ImguiLabel(Mathf.RoundToInt(16f * k), ZombiesUi.Dim, TextAnchor.MiddleCenter);
                _value = ZombiesUi.ImguiLabel(Mathf.RoundToInt(34f * k), Color.white, TextAnchor.MiddleCenter, true);
                _small = ZombiesUi.ImguiLabel(Mathf.RoundToInt(20f * k), ZombiesUi.Dim, TextAnchor.MiddleCenter);
                _tile = ZombiesUi.Tex(ZombiesUi.RowFace);
                _white = Pixel(Color.white);
                _button = ZombiesUi.ImguiButton(Mathf.RoundToInt(24f * k));
            }
            // Esc closes it (as BACK)
            Event ev = Event.current;
            if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
            {
                ev.Use();
                ZombiesUi.EscUsedAt = Time.unscaledTime;
                Close();
                return;
            }
            GUI.depth = -1000;
            ZombiesUi.ImguiDim(0.6f);
            bool other = _otherId != null;
            IReadOnlyList<ZombiesHistory.Match> all = other ? (IReadOnlyList<ZombiesHistory.Match>)_other ?? new List<ZombiesHistory.Match>() : ZombiesHistory.All;
            float w = 1400f * k, h = 900f * k;
            var box = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            ZombiesUi.ImguiPanel(box);
            // A thin red line under the title
            GUI.color = new Color(Red.r, Red.g, Red.b, 0.8f);
            GUI.DrawTexture(new Rect(box.x + w / 2f - 160f * k, box.y + 84f * k, 320f * k, 2f * k), ZombiesUi.Tex(ZombiesUi.Divider));
            GUI.color = Color.white;
            _title.normal.textColor = Red;
            GUI.Label(new Rect(box.x, box.y + 18f * k, w, 64f * k), other ? $"{_otherName.ToUpperInvariant()}'S STATISTICS" : "STATISTICS", _title);
            _small.normal.textColor = ZombiesUi.Dim;
            string sub = other && _otherLoading ? "LOADING" + new string('.', (int)(Time.unscaledTime * 2.5f) % 4)
                : all.Count == 0 ? (other ? $"{_otherName} hasn't played a zombies match yet" : "No matches played yet")
                : $"{all.Count} match{(all.Count == 1 ? "" : "es")} played";
            GUI.Label(new Rect(box.x, box.y + 92f * k, w, 30f * k), sub, _small);

            // Lifetime tiles: two rows of six
            var tiles = new List<(string name, string value)>
            {
                ("MATCHES", all.Count.ToString("N0")),
                ("HIGHEST ROUND", all.Count == 0 ? "-" : all.Max(m => m.Rounds).ToString()),
                ("KILLS", all.Sum(m => m.Kills).ToString("N0")),
                ("HEADSHOTS", all.Sum(m => m.Headshots).ToString("N0")),
                ("KNIFE KILLS", all.Sum(m => m.KnifeKills).ToString("N0")),
                ("BEST SCORE", all.Count == 0 ? "-" : all.Max(m => m.Score).ToString("N0")),
                ("DOWNS", all.Sum(m => m.Downs).ToString("N0")),
                ("REVIVES", all.Sum(m => m.Revives).ToString("N0")),
                ("DOORS OPENED", all.Sum(m => m.Doors).ToString("N0")),
                ("BOARDS REBUILT", all.Sum(m => m.Boards).ToString("N0")),
                ("TIME PLAYED", Played(all.Sum(m => m.Seconds))),
                ("DR. K POINTS", all.Sum(m => m.Kp).ToString("N0")),
            };
            float tileW = (w - 70f * k) / 6f, tileH = 96f * k, ty = box.y + 134f * k;
            for (int i = 0; i < tiles.Count; i++)
            {
                var r = new Rect(box.x + 20f * k + (i % 6) * (tileW + 6f * k), ty + (i / 6) * (tileH + 8f * k), tileW, tileH);
                if (Event.current.type == EventType.Repaint)
                    GUI.Box(r, GUIContent.none, ZombiesUi.Style(ZombiesUi.RowFace));
                _value.fontSize = Mathf.RoundToInt(34f * k);
                _value.normal.textColor = i == tiles.Count - 1 ? Red : Color.white;
                GUI.Label(new Rect(r.x, r.y + 10f * k, r.width, 46f * k), tiles[i].value, _value);
                _label.fontSize = Mathf.RoundToInt(16f * k);
                _label.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
                GUI.Label(new Rect(r.x, r.y + 58f * k, r.width, 28f * k), tiles[i].name, _label);
            }

            // The chart: the last matches, three bars each
            float cy = ty + 2f * (tileH + 8f * k) + 24f * k;
            _value.fontSize = Mathf.RoundToInt(24f * k);
            _value.normal.textColor = Color.white;
            List<ZombiesHistory.Match> last = all.Skip(Mathf.Max(0, all.Count - 12)).ToList();
            GUI.Label(new Rect(box.x, cy, w, 34f * k), last.Count == 0 ? "" : $"LAST {last.Count} MATCH{(last.Count == 1 ? "" : "ES")}", _value);
            // Legend
            float lx = box.x + w / 2f - 270f * k;
            for (int b = 0; b < 3; b++)
            {
                GUI.color = BarColours[b];
                GUI.DrawTexture(new Rect(lx + b * 190f * k, cy + 44f * k, 18f * k, 18f * k), _white);
                GUI.color = Color.white;
                _small.alignment = TextAnchor.MiddleLeft;
                GUI.Label(new Rect(lx + b * 190f * k + 26f * k, cy + 38f * k, 160f * k, 30f * k), BarNames[b], _small);
                _small.alignment = TextAnchor.MiddleCenter;
            }
            float chartTop = cy + 84f * k, chartBottom = box.yMax - 110f * k, chartH = chartBottom - chartTop;
            float chartX = box.x + 40f * k, chartW = w - 80f * k;
            GUI.DrawTexture(new Rect(chartX, chartBottom, chartW, 2f * k), _tile);
            if (last.Count > 0)
            {
                int[] most =
                {
                    Mathf.Max(1, last.Max(m => m.Rounds)), Mathf.Max(1, last.Max(m => m.Kills)), Mathf.Max(1, last.Max(m => m.Headshots)),
                };
                float slot = chartW / 12f, barW = Mathf.Min(26f * k, slot / 4.2f);
                for (int i = 0; i < last.Count; i++)
                {
                    ZombiesHistory.Match m = last[i];
                    int[] v = { m.Rounds, m.Kills, m.Headshots };
                    float sx = chartX + i * slot + (slot - 3f * barW - 8f * k) / 2f;
                    for (int b = 0; b < 3; b++)
                    {
                        float bh = Mathf.Max(2f * k, (chartH - 30f * k) * v[b] / most[b]);
                        var r = new Rect(sx + b * (barW + 4f * k), chartBottom - bh, barW, bh);
                        GUI.color = BarColours[b];
                        GUI.DrawTexture(r, _white);
                        GUI.color = Color.white;
                        _small.fontSize = Mathf.RoundToInt(14f * k);
                        GUI.Label(new Rect(r.x - 10f * k, r.y - 22f * k, barW + 20f * k, 20f * k), v[b].ToString(), _small);
                    }
                    _small.fontSize = Mathf.RoundToInt(14f * k);
                    string day = m.When != null && m.When.Length >= 10 ? m.When.Substring(5, 5) : "";
                    GUI.Label(new Rect(chartX + i * slot, chartBottom + 6f * k, slot, 20f * k), day, _small);
                }
                _small.fontSize = Mathf.RoundToInt(20f * k);
            }
            if (GUI.Button(new Rect(box.x + (w - 240f * k) / 2f, box.yMax - 66f * k, 240f * k, 48f * k), "BACK", _button))
                Close();
        }

        private static string Played(float seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
        }
    }
}
