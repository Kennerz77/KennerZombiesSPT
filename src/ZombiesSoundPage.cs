using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies settings page's SOUND tab (user, 2026-11-11): every volume of the zombies mode in one scrolling list,
    /// grouped (music, menus, the match, the box, Pack-a-Punch, the perk machines, the Ray Gun, the Thundergun). Each
    /// slider IS the F12 (BepInEx) setting, read live from each of our mods' own config, so the two always agree and a
    /// new volume added to any of them shows up here by itself. Only our own mods (Kennerz) are listed.
    /// </summary>
    internal static class ZombiesSoundPage
    {
        private class Row
        {
            public ConfigEntry<float> Entry;
            public string Label, Help;
            public float Min, Max;
        }

        private class Group
        {
            public string Title;
            public int Order;
            public readonly List<Row> Rows = new List<Row>();
        }

        // Which mod sections go where, and in what order (anything else with "volume" / "sound" / "music" in its
        // section name lands in OTHER SOUNDS)
        private static readonly (string guid, string section, string title, int order)[] Known =
        {
            ("com.kennerz.zmcore", "2b. Zombies music", "MUSIC", 0),
            ("com.kennerz.zmcore", "10. Zombies menu sounds", "MENUS AND NOTIFICATIONS", 1),
            ("com.kennerz.zmcore", "2. Zombies sounds", "ZOMBIES MATCH", 2),
            ("com.kennerz.zmcore", "3. Mystery box volume", "MYSTERY BOX", 3),
            ("com.kennerz.paphideoutmodule", "1. Volume", "PACK-A-PUNCH", 4),
            ("com.kennerz.perkbeverages", "1. Volume", "PERK MACHINES", 5),
            ("com.kennerz.raygun", "Volume", "RAY GUN", 6),
            ("com.kennerz.thundergun", "Volume", "THUNDERGUN", 7),
        };

        private static readonly string[] Mods =
            { "com.kennerz.zmcore", "com.kennerz.paphideoutmodule", "com.kennerz.perkbeverages", "com.kennerz.raygun", "com.kennerz.thundergun" };

        private static List<Group> _groups;
        private static Vector2 _scroll;
        private static GUIStyle _head, _label, _help, _value, _track, _thumb, _small;
        private static int _styledAt;

        /// <summary>Read again next time (a mod loaded late).</summary>
        public static void Forget() => _groups = null;

        private static List<Group> Groups()
        {
            if (_groups != null)
                return _groups;
            var groups = new Dictionary<string, Group>();
            foreach (string guid in Mods)
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out var info) || info.Instance == null)
                    continue;
                ConfigFile config = info.Instance.Config;
                foreach (ConfigDefinition def in config.Keys.ToList())
                {
                    if (!(config[def] is ConfigEntry<float> e))
                        continue;
                    string sec = def.Section, key = def.Key;
                    var known = Known.FirstOrDefault(k => k.guid == guid && k.section == sec);
                    bool soundSection = known.title != null || sec.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0
                        || sec.IndexOf("sound", StringComparison.OrdinalIgnoreCase) >= 0 || sec.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!soundSection || sec.IndexOf("debug", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    // Ranges, times and brightness aren't volumes
                    if (key.Contains("(m)") || key.Contains("(s)") || key.IndexOf("brightness", StringComparison.OrdinalIgnoreCase) >= 0
                        || key.IndexOf("range", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    var range = e.Description?.AcceptableValues as AcceptableValueRange<float>;
                    string title = known.title ?? "OTHER SOUNDS";
                    if (!groups.TryGetValue(title, out Group g))
                        groups[title] = g = new Group { Title = title, Order = known.title != null ? known.order : 99 };
                    g.Rows.Add(new Row
                    {
                        Entry = e, Label = key, Help = e.Description?.Description ?? "",
                        Min = range?.MinValue ?? 0f, Max = range?.MaxValue ?? 1f,
                    });
                }
            }
            _groups = groups.Values.OrderBy(g => g.Order).ThenBy(g => g.Title).ToList();
            ZombiesPlugin.Log.LogInfo($"Zombies settings: sound tab, {_groups.Sum(g => g.Rows.Count)} volumes in {_groups.Count} groups");
            return _groups;
        }

        private static void Styles(float s)
        {
            if (_head != null && _styledAt == Mathf.RoundToInt(s * 1000))
                return;
            _styledAt = Mathf.RoundToInt(s * 1000);
            _head = ZombiesUi.ImguiLabel(Mathf.RoundToInt(19 * s), ZombiesUi.Red, TextAnchor.LowerLeft, true);
            _label = ZombiesUi.ImguiLabel(Mathf.RoundToInt(17 * s), ZombiesUi.Ink);
            _label.wordWrap = false;
            _help = ZombiesUi.ImguiLabel(Mathf.RoundToInt(12 * s), ZombiesUi.Faint);
            _help.wordWrap = false;
            _help.clipping = TextClipping.Clip;
            _value = ZombiesUi.ImguiLabel(Mathf.RoundToInt(16 * s), ZombiesUi.Ink, TextAnchor.MiddleRight, true);
            _small = ZombiesUi.ImguiLabel(Mathf.RoundToInt(14 * s), ZombiesUi.Dim, TextAnchor.MiddleLeft);
            _track = new GUIStyle(GUI.skin.horizontalSlider)
            {
                border = ZombiesUi.Border(ZombiesUi.RowFace), fixedHeight = Mathf.Round(8 * s), margin = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(0, 0, 0, 0),
            };
            _track.normal.background = ZombiesUi.Tex(ZombiesUi.RowFace);
            _thumb = new GUIStyle(GUI.skin.horizontalSliderThumb)
            {
                fixedWidth = Mathf.Round(16 * s), fixedHeight = Mathf.Round(20 * s), border = ZombiesUi.Border(ZombiesUi.Flat(ZombiesUi.Red, ZombiesUi.RedDeep, 3f)),
            };
            _thumb.normal.background = _thumb.hover.background = _thumb.active.background = _thumb.focused.background =
                ZombiesUi.Tex(ZombiesUi.Flat(ZombiesUi.Red, ZombiesUi.RedDeep, 3f));
        }

        /// <summary>The tab's content inside the page's body.</summary>
        public static void Draw(Rect area, float s)
        {
            Styles(s);
            List<Group> groups = Groups();
            float headH = 40 * s, rowH = 52 * s;
            float content = groups.Sum(g => headH + g.Rows.Count * rowH) + 10 * s;
            GUI.Label(new Rect(area.x, area.y, area.width, 22 * s), "The same volumes as in F12. Changes save at once.", _small);
            var view = new Rect(area.x, area.y + 26 * s, area.width, area.height - 26 * s);
            float cw = view.width - 18 * s;
            _scroll = GUI.BeginScrollView(view, _scroll, new Rect(0f, 0f, cw, content), false, true);
            float y = 0f;
            foreach (Group g in groups)
            {
                GUI.Label(new Rect(4 * s, y, cw, headH - 6 * s), g.Title, _head);
                GUI.color = new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.45f);
                GUI.DrawTexture(new Rect(0f, y + headH - 5 * s, cw, 1.5f * s), ZombiesUi.Tex(ZombiesUi.Divider));
                GUI.color = Color.white;
                y += headH;
                foreach (Row r in g.Rows)
                {
                    if (Event.current.type == EventType.Repaint)
                        GUI.Box(new Rect(0f, y + 2 * s, cw, rowH - 4 * s), GUIContent.none, ZombiesUi.Style(ZombiesUi.RowFace));
                    float labelW = cw * 0.46f;
                    GUI.Label(new Rect(12 * s, y + 4 * s, labelW, 26 * s), r.Label, _label);
                    GUI.Label(new Rect(12 * s, y + 27 * s, labelW, 18 * s), new GUIContent(r.Help, r.Help), _help);
                    float sx = 12 * s + labelW + 10 * s, sw = cw - sx - 80 * s;
                    float v = r.Entry.Value;
                    float nv = GUI.HorizontalSlider(new Rect(sx, y + rowH / 2f - 4 * s, sw, 20 * s), v, r.Min, r.Max, _track, _thumb);
                    if (Mathf.Abs(nv - v) > 0.0005f)
                        r.Entry.Value = Mathf.Round(nv * 100f) / 100f;
                    GUI.Label(new Rect(cw - 72 * s, y, 60 * s, rowH), Mathf.RoundToInt(r.Entry.Value * 100f) + "%", _value);
                    y += rowH;
                }
            }
            GUI.EndScrollView();
        }
    }
}
