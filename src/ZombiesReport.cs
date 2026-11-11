using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.UI.Screens;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;

namespace KennerZombies
{
    /// <summary>
    /// The after-match report: kept at the game over (the map, rounds survived, time played, the ranked board with each
    /// player's score and stats, the XP tally and the Dr. Kennerz points). Out of the raid it is the ONE after-raid
    /// screen: drawn over EFT's own after-raid background, whose screens are hidden and skipped (ZombiesAfterRaid);
    /// NEXT goes on to the main menu (the user, 2026-11-08). The last one is saved to logs\last_report.json, and the zombies main menu's "MATCH REPORT" button
    /// (ZombiesMainMenu) opens it again. Every report is also added to logs\reports.txt, one block per match.
    /// </summary>
    internal class ZombiesReport : MonoBehaviour
    {
        private class Row
        {
            public string Name;
            public float R, G, B;
            public string[] Values;
            public int Score;
        }

        private class Report
        {
            public string Map, Time, When;
            public int Rounds;
            public List<Row> Rows = new List<Row>();
            public ZombiesXp.Result Xp;
            [JsonIgnore] public float ShownAt = -1f; // when it first showed (the tally counts up from then)
            [JsonIgnore] public bool Review;          // opened again from the menu: everything shown at once
        }

        /// <summary>This match has its report (game over, or the raid ending another way): kept once.</summary>
        public static bool KeptThisMatch { get; private set; }

        /// <summary>A fresh report from the match just ended, until NEXT: EFT's own after-raid screens are skipped for it (ZombiesAfterRaid).</summary>
        public static bool Waiting => _last != null && !_last.Review;

        // EFT's after-raid run, once it has started (its background shows): NEXT finishes it into the main menu
        private static Func<System.Threading.Tasks.Task> _toMenu;

        /// <summary>EFT's after-raid screens have started (ZombiesAfterRaid): the report shows over their background.</summary>
        public static void AfterRaidStarted(Func<System.Threading.Tasks.Task> toMenu) => _toMenu = toMenu;

        /// <summary>A new match is built: the next end keeps a new report.</summary>
        public static void NewMatch()
        {
            KeptThisMatch = false;
            Forfeited = false;
        }

        /// <summary>Left the match from the menu: nothing banked, no report, straight to the main menu.</summary>
        public static bool Forfeited { get; private set; }

        public static void Forfeit()
        {
            KeptThisMatch = true;
            Forfeited = true;
            _last = null;
            ZombiesPlugin.Log.LogInfo("Zombies: left the match: no rewards, no report");
        }

        public static void ForfeitDone() => Forfeited = false;

        private static readonly string[] Columns = { "SCORE", "POINTS", "KILLS", "HEADSHOTS", "DOWNS", "REVIVES", "DOORS" };
        private static Report _last;
        private GUIStyle _title, _cell, _head, _button;
        private Texture2D _panel, _backdrop, _buttonBg, _buttonHover;
        private static bool _menuInputOff;

        private static string Dir => Path.Combine(ZombiesPlugin.Dir, "logs");
        private static string LastFile => Path.Combine(Dir, "last_report.json");

        /// <summary>A report from an earlier match is saved (the menu's MATCH REPORT button shows only then).</summary>
        public static bool HasSaved => File.Exists(LastFile);

        /// <summary>The menu's MATCH REPORT button: the last match's report again, all shown at once.</summary>
        public static void ShowLast()
        {
            try
            {
                var r = JsonConvert.DeserializeObject<Report>(File.ReadAllText(LastFile));
                if (r == null)
                    return;
                r.Review = true;
                _last = r;
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies report: couldn't open the last one ({e.Message})");
            }
        }

        private static void Save(Report r)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(LastFile, JsonConvert.SerializeObject(r, Formatting.Indented));
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies report: couldn't save it ({e.Message})");
            }
        }

        // Teammates' last numbers can land just after the game over (a down that ended the match): the board's rows are
        // read again a few seconds on, XP and history stay as they were
        private static System.Collections.IEnumerator RefreshRows(Report report)
        {
            yield return new WaitForSecondsRealtime(3f);
            if (report == null || report != _last)
                yield break;
            try
            {
                report.Rows = ZombiesStats.Rows().Select(x => new Row
                {
                    Name = x.name, R = x.colour.r, G = x.colour.g, B = x.colour.b, Values = x.values, Score = x.score,
                }).ToList();
                Save(report);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies report: couldn't refresh the board ({e.Message})");
            }
        }

        /// <summary>The match as it stands, kept for the after-raid screen (the game over calls it).</summary>
        public static void Keep(int rounds)
        {
            if (KeptThisMatch)
                return;
            KeptThisMatch = true;
            try
            {
                _last = new Report
                {
                    Map = ZombiesMatch.LocationHere ?? "",
                    Rounds = rounds,
                    Time = ZombiesStats.SessionTime(),
                    When = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    Rows = ZombiesStats.Rows().Select(x => new Row
                    {
                        Name = x.name, R = x.colour.r, G = x.colour.g, B = x.colour.b, Values = x.values, Score = x.score,
                    }).ToList(),
                    Xp = ZombiesXp.Work(ZombiesStats.Mine, rounds),
                };
                ZombiesPlugin.Instance.StartCoroutine(RefreshRows(_last));
                ZombiesXp.Send(_last.Xp, _last.Map, rounds);
                Save(_last);
                ZombiesHistory.Add(ZombiesHistory.Now(rounds, _last.Xp));
                Directory.CreateDirectory(Dir);
                var lines = new List<string> { $"=== {_last.When} {_last.Map}: {rounds} round(s) survived, {_last.Time}" };
                for (int i = 0; i < _last.Rows.Count; i++)
                    lines.Add($"{i + 1}. {_last.Rows[i].Name}: " + string.Join(", ", Columns.Select((c, n) => $"{c.ToLowerInvariant()} {_last.Rows[i].Values[n]}")));
                foreach (ZombiesXp.Line l in _last.Xp.Lines)
                    lines.Add($"   XP {l.What}: {l.Count} x {l.Each} = {l.Xp}");
                lines.Add($"   XP total {_last.Xp.Xp} = {_last.Xp.Points} Dr. K points");
                File.AppendAllLines(Path.Combine(Dir, "reports.txt"), lines);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies report: couldn't keep it ({e.Message})");
            }
        }

        private static bool InRaid => Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null;

        // In the main menu, out of any raid
        private static bool InMenu()
        {
            if (!ZombiesPlugin.AtMenus)
                return false;
            try
            {
                return Singleton<IScreenManager>.Instance?.CurrentScreenType is EEftScreenType t && t == EEftScreenType.MainMenu;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A fresh report (after a match) is on screen now: the zombies menu music plays under it.</summary>
        public static bool FreshOnScreen => _last != null && !_last.Review && _last.ShownAt >= 0f && ShowNow();

        // Everything playing as the report comes up, once: what still plays under it (the user heard music that
        // shouldn't be there, 2026-11-08)
        private static void LogPlaying()
        {
            try
            {
                var names = new List<string>();
                foreach (AudioSource a in Resources.FindObjectsOfTypeAll<AudioSource>())
                    if (a != null && a.isPlaying && a.gameObject.scene.IsValid() || a != null && a.isPlaying && a.gameObject.scene.name == "DontDestroyOnLoad")
                        names.Add($"{a.gameObject.name}: {a.clip?.name ?? "(one-shot)"} vol {a.volume:0.##}");
                ZombiesPlugin.Log.LogInfo("Zombies report: sounds playing as it came up: " + (names.Count == 0 ? "none" : string.Join("; ", names.Distinct())));
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies report: couldn't list the sounds ({e.Message})");
            }
        }

        /// <summary>In the main menu, out of any raid (the statistics page closes otherwise).</summary>
        public static bool InMenuNow() => InMenu();

        /// <summary>A report (fresh or a review) is on screen now.</summary>
        public static bool OnScreen => ShowNow();

        // A fresh report shows once EFT's after-raid screens are up (over their background), or in the main menu if they
        // never came; a review only in the main menu
        private static bool ShowNow() => _last != null && !InRaid && (_last.Review ? InMenu() : _toMenu != null || InMenu());

        // While the report is up, EFT's menu under it takes no clicks (IMGUI buttons don't block uGUI ones)
        private void Update()
        {
            ZombiesGameOver.TickBlack();
            ZombiesAfterRaid.Tick();
            if (ZombiesMatch.Active || Waiting)
                ZombiesAchievements.QuietFor(180f);
            bool off = ShowNow() || ZombiesStatsScreen.Open || ZombiesControls.PageOpen;
            if (off == _menuInputOff)
                return;
            _menuInputOff = off;
            try
            {
                // The one switched off is remembered: once off it's no longer EventSystem.current, so turning "current"
                // back on found nothing and the main menu stayed dead to clicks (user, 2026-11-09)
                if (off)
                {
                    _inputOff = EventSystem.current;
                    if (_inputOff != null)
                        _inputOff.enabled = false;
                }
                else
                {
                    if (_inputOff != null)
                        _inputOff.enabled = true;
                    _inputOff = null;
                    // Any other left off (a scene change between): the menu's own back on too
                    foreach (EventSystem es in UnityEngine.Object.FindObjectsOfType<EventSystem>(true))
                        if (es != null && !es.enabled && es.gameObject.activeInHierarchy)
                            es.enabled = true;
                }
            }
            catch
            {
            }
        }

        private static EventSystem _inputOff;

        private static Texture2D Pixel(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private void OnGUI()
        {
            ZombiesGameOver.DrawFall();
            if (ZombiesGameOver.Black)
            {
                ZombiesGameOver.DrawBlack();
                return;
            }
            if (!ShowNow())
            {
                if (ZombiesMapSelect.Zombies && !ZombiesStatsScreen.Open && !ZombiesControls.PageOpen && !ZombiesFrontEnd.InLobby && InMenu())
                    DrawNote();
                return;
            }
            float k = Screen.height / 1080f;
            // The zombies menus' look (user, 2026-11-11): glass panel, the display font for the title, EFT's Bender in bold
            // for everything else (clear), a row face per player with their colour
            if (_title == null || _styledAt != Screen.height)
            {
                _styledAt = Screen.height;
                _title = ZombiesUi.ImguiLabel(52, ZombiesUi.Red, TextAnchor.MiddleCenter, true);
                _title.wordWrap = false;
                _cell = ZombiesUi.ImguiLabel(24, Color.white, TextAnchor.MiddleCenter, false);
                _cell.fontStyle = FontStyle.Bold;
                _cell.wordWrap = false;
                _head = ZombiesUi.ImguiLabel(22, ZombiesUi.Dim, TextAnchor.MiddleCenter, false);
                _head.fontStyle = FontStyle.Bold;
                _head.wordWrap = false;
                _backdrop = Pixel(new Color(0f, 0f, 0f, 0.6f));
                _button = ZombiesUi.ImguiButton(26);
            }
            GUI.depth = -1000;
            // In the menu (a review, or EFT's screens never came) the menu is dimmed behind; after a raid EFT's own
            // blurred background is left to show
            if (_toMenu == null)
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _backdrop);
            if (_last.ShownAt < 0f)
            {
                _last.ShownAt = _last.Review ? Time.unscaledTime - 1000f : Time.unscaledTime;
                if (!_last.Review)
                    LogPlaying();
            }
            float nameW = 300f * k, colW = 130f * k, rowH = 46f * k, lineH = 34f * k;
            ZombiesXp.Result xp = _last.Xp;
            int xpLines = xp?.Lines.Count ?? 0;
            float tallyH = xp == null ? 0f : 150f * k + lineH * (xpLines + 4);
            float w = nameW + colW * Columns.Length + 60f * k, h = 260f * k + rowH * Mathf.Max(1, _last.Rows.Count) + tallyH;
            var box = new Rect((Screen.width - w) / 2f, Mathf.Max(10f * k, (Screen.height - h) / 2f), w, h);
            ZombiesUi.ImguiPanel(box);
            float x = box.x + 30f * k, y = box.y + 20f * k;
            _title.fontSize = Mathf.RoundToInt(52f * k);
            _title.normal.textColor = ZombiesUi.Red;
            GUI.Label(new Rect(box.x, y, w, 64f * k), "AFTER ACTION REPORT", _title);
            GUI.color = new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.8f);
            GUI.DrawTexture(new Rect(box.x + w / 2f - 220f * k, y + 64f * k, 440f * k, 2f * k), ZombiesUi.Tex(ZombiesUi.Divider));
            GUI.color = Color.white;
            _head.fontSize = Mathf.RoundToInt(22f * k);
            _head.normal.textColor = ZombiesUi.Ink;
            GUI.Label(new Rect(box.x, y + 64f * k, w, 32f * k),
                $"{_last.Map}   ·   {(_last.Rounds == 1 ? "1 round" : _last.Rounds + " rounds")} survived   ·   {_last.Time}   ·   {_last.When}", _head);
            y += 120f * k;
            _head.fontSize = Mathf.RoundToInt(18f * k);
            _head.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(x, y, nameW, rowH), "PLAYER", _head);
            _head.alignment = TextAnchor.MiddleCenter;
            for (int c = 0; c < Columns.Length; c++)
                GUI.Label(new Rect(x + nameW + c * colW, y, colW, rowH), Columns[c], _head);
            _head.normal.textColor = ZombiesUi.Dim;
            _cell.fontSize = Mathf.RoundToInt(24f * k);
            for (int i = 0; i < _last.Rows.Count; i++)
            {
                Row r = _last.Rows[i];
                float ry = y + rowH * (i + 1);
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.Box(new Rect(x - 10f * k, ry + 3f * k, w - 40f * k, rowH - 6f * k), GUIContent.none, ZombiesUi.Style(ZombiesUi.RowFace));
                    GUI.color = new Color(r.R, r.G, r.B);
                    GUI.DrawTexture(new Rect(x - 7f * k, ry + 9f * k, 4f * k, rowH - 18f * k), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                }
                _cell.normal.textColor = new Color(r.R, r.G, r.B);
                _cell.alignment = TextAnchor.MiddleLeft;
                GUI.Label(new Rect(x, ry, nameW, rowH), $"{i + 1}. {r.Name}" + (r.Name == MyName ? "  (YOU)" : ""), _cell);
                _cell.alignment = TextAnchor.MiddleCenter;
                for (int c = 0; c < Columns.Length && c < (r.Values?.Length ?? 0); c++)
                    GUI.Label(new Rect(x + nameW + c * colW, ry, colW, rowH), r.Values[c], _cell);
            }
            if (xp != null)
                DrawTally(xp, box, y + rowH * (_last.Rows.Count + 1) + 20f * k, k, lineH);
            _button.fontSize = Mathf.RoundToInt(26f * k);
            if (GUI.Button(new Rect(box.x + (w - 240f * k) / 2f, box.yMax - 66f * k, 240f * k, 48f * k), _last.Review ? "BACK" : "NEXT", _button))
            {
                if (!_last.Review)
                    Save(_last); // again, now with the banked total
                _last = null;
                _ticked = 0;
                Func<System.Threading.Tasks.Task> toMenu = _toMenu;
                _toMenu = null;
                ZombiesAfterRaid.ShowEftScreens();
                if (toMenu != null)
                {
                    try
                    {
                        toMenu();
                    }
                    catch (Exception e)
                    {
                        ZombiesPlugin.Log.LogWarning($"Zombies report: couldn't go on to the main menu ({e.Message})");
                    }
                }
            }
        }

        // ---------- The zombies main menu's note (the user, 2026-11-08) ----------

        private GUIStyle _noteHead, _noteText;

        private static readonly string[] NoteLines =
        {
            "You can enter CHARACTER for this mode and equip what you like.",
            "You will not lose any items on this character set up in zombies mode.",
            "Guns will not be brought in though, so don't even try!",
        };

        private static ConfigEntry<bool> _noteOn;
        private static bool _noteClosed;   // OK pressed: gone until the game is started again
        private bool _dontShow;            // the tick box
        private GUIStyle _noteButton, _noteTick;
        private Texture2D _noteBox, _okBg, _okHover, _tickOff, _tickOn;

        /// <summary>The note's setting (F12): ticking "Don't show me again" and pressing OK turns it off; F12 brings it back.</summary>
        public static void Bind(ConfigFile config, string section) =>
            _noteOn = config.Bind(section, "Show the main menu note", true, new ConfigDescription(
                "The note at the top left of the zombies main menu about the character set-up (its \"Don't show me again\" box turns this off)."));

        // Top left of the main menu in zombies mode, in a black box: OK hides it for now, with the box ticked for good
        private void DrawNote()
        {
            if (_noteClosed || _noteOn?.Value == false)
                return;
            float k = Screen.height / 1080f;
            if (_noteHead == null)
            {
                _noteHead = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontStyle = FontStyle.Bold, wordWrap = false };
                _noteText = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, wordWrap = true };
                _noteBox = Pixel(new Color(0f, 0f, 0f, 0.92f));
                _okBg = Pixel(new Color(0.5f, 0.04f, 0.03f, 1f));
                _okHover = Pixel(new Color(0.75f, 0.08f, 0.06f, 1f));
                _tickOff = Pixel(new Color(0.25f, 0.25f, 0.25f, 1f));
                _tickOn = Pixel(new Color(0.78f, 0.08f, 0.06f, 1f));
                _noteButton = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _noteButton.normal.background = _okBg;
                _noteButton.hover.background = _okHover;
                _noteButton.active.background = _okHover;
                _noteButton.normal.textColor = _noteButton.hover.textColor = _noteButton.active.textColor = Color.white;
                _noteTick = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
            }
            _noteHead.fontSize = Mathf.RoundToInt(26f * k);
            _noteText.fontSize = Mathf.RoundToInt(20f * k);
            _noteButton.fontSize = Mathf.RoundToInt(20f * k);
            _noteTick.fontSize = Mathf.RoundToInt(18f * k);
            _noteTick.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            float pad = 18f * k, w = 520f * k, x = 32f * k + pad, top = 28f * k;

            // The box's height: heading, the lines, then the OK row
            float textH = 38f * k;
            foreach (string line in NoteLines)
                textH += _noteText.CalcHeight(new GUIContent(line), w) + 8f * k;
            float rowH = 40f * k;
            GUI.DrawTexture(new Rect(32f * k, top, w + pad * 2f, pad + textH + 10f * k + rowH + pad), _noteBox);

            float y = top + pad;
            Shadow(new Rect(x, y, w, 34f * k), "NOTE:", _noteHead, new Color(0.78f, 0.08f, 0.06f));
            y += 38f * k;
            foreach (string line in NoteLines)
            {
                float h = _noteText.CalcHeight(new GUIContent(line), w);
                Shadow(new Rect(x, y, w, h), line, _noteText, new Color(0.92f, 0.92f, 0.92f));
                y += h + 8f * k;
            }
            y += 10f * k;

            // Don't show me again: a box and its words, both clickable
            float box = 22f * k;
            var tickRect = new Rect(x, y + (rowH - box) / 2f, box, box);
            GUI.DrawTexture(tickRect, _dontShow ? _tickOn : _tickOff);
            if (_dontShow)
            {
                Color before = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(tickRect.x + box * 0.28f, tickRect.y + box * 0.28f, box * 0.44f, box * 0.44f), Texture2D.whiteTexture);
                GUI.color = before;
            }
            var tickLabel = new Rect(x + box + 10f * k, y, 260f * k, rowH);
            GUI.Label(tickLabel, "Don't show me again", _noteTick);
            if (GUI.Button(new Rect(x, y, box + 10f * k + 260f * k, rowH), GUIContent.none, GUIStyle.none))
                _dontShow = !_dontShow;

            if (GUI.Button(new Rect(x + w - 120f * k, y, 120f * k, rowH), "OK", _noteButton))
            {
                _noteClosed = true;
                if (_dontShow && _noteOn != null)
                    _noteOn.Value = false;
            }
        }

        private static void Shadow(Rect r, string text, GUIStyle style, Color colour)
        {
            Color before = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), text, style);
            GUI.color = colour;
            GUI.Label(r, text, style);
            GUI.color = before;
        }

        // The XP tally, the original-style: a line every 0.35 s, then the total counting up, then the Dr. Kennerz points
        private const float LineEvery = 0.35f;

        private void DrawTally(ZombiesXp.Result xp, Rect box, float y, float k, float lineH)
        {
            float t = Time.unscaledTime - _last.ShownAt;
            int shown = Mathf.Clamp(Mathf.FloorToInt(t / LineEvery), 0, xp.Lines.Count);
            if (shown > _ticked)
            {
                _ticked = shown;
                if (!_last.Review)
                    ZombiesSounds.Head("zm_purchase", 0.5f);
            }
            float x = box.x + 120f * k, w = box.width - 240f * k;
            _title.fontSize = Mathf.RoundToInt(34f * k);
            _title.normal.textColor = ZombiesHud.Gold;
            GUI.Label(new Rect(box.x, y, box.width, 46f * k), "EXPERIENCE", _title);
            y += 54f * k;
            _cell.fontSize = Mathf.RoundToInt(22f * k);
            for (int i = 0; i < shown; i++)
            {
                ZombiesXp.Line l = xp.Lines[i];
                float ly = y + lineH * i;
                _cell.normal.textColor = l.Each < 0 && l.Count > 0 ? new Color(0.9f, 0.25f, 0.2f) : new Color(0.85f, 0.85f, 0.85f);
                _cell.alignment = TextAnchor.MiddleLeft;
                GUI.Label(new Rect(x, ly, w * 0.55f, lineH), l.What, _cell);
                _cell.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(x + w * 0.55f, ly, w * 0.25f, lineH), $"{l.Count} x {l.Each}", _cell);
                _cell.alignment = TextAnchor.MiddleRight;
                GUI.Label(new Rect(x + w * 0.8f, ly, w * 0.2f, lineH), (l.Xp > 0 ? "+" : "") + l.Xp.ToString("N0"), _cell);
            }
            if (shown < xp.Lines.Count)
                return;
            float after = t - LineEvery * xp.Lines.Count;
            int total = Mathf.RoundToInt(xp.Xp * Mathf.Clamp01(after / 1.2f));
            float ty = y + lineH * xp.Lines.Count + 8f * k;
            _cell.fontSize = Mathf.RoundToInt(28f * k);
            _cell.normal.textColor = Color.white;
            _cell.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(x, ty, w * 0.6f, lineH * 1.2f), "TOTAL XP", _cell);
            _cell.alignment = TextAnchor.MiddleRight;
            GUI.Label(new Rect(x + w * 0.6f, ty, w * 0.4f, lineH * 1.2f), total.ToString("N0"), _cell);
            if (after < 1.3f)
                return;
            _cell.normal.textColor = new Color(0.75f, 0.05f, 0.05f);
            _cell.alignment = TextAnchor.MiddleLeft;
            GUI.Label(new Rect(x, ty + lineH * 1.3f, w * 0.6f, lineH * 1.2f), "DR. K POINTS", _cell);
            _cell.alignment = TextAnchor.MiddleRight;
            GUI.Label(new Rect(x + w * 0.6f, ty + lineH * 1.3f, w * 0.4f, lineH * 1.2f), "+" + xp.Points.ToString("N0"), _cell);
            _cell.fontSize = Mathf.RoundToInt(18f * k);
            _cell.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            _cell.alignment = TextAnchor.MiddleRight;
            string bank = xp.Total.HasValue ? $"Your total: {xp.Total.Value:N0}" : xp.Error ?? (_last.Review ? "" : "Banking...");
            GUI.Label(new Rect(x, ty + lineH * 2.5f, w, lineH), bank, _cell);
        }

        private int _ticked, _styledAt;

        private static string MyName => SPT.Reflection.Utils.ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile?.Nickname;
    }
}
