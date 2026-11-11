using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Comfort.Common;
using EFT;
using EFT.UI;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The in-match HUD in the zombies menus' look (user, 2026-11-11): smoked glass, EFT's font where the game ships it,
    /// a red accent, soft shadows. Shared by the interaction prompts (our own list in place of EFT's grey one: the key in
    /// a keycap, the action, the price in gold or red when you can't afford it), the barricade and revive prompts, the
    /// exfil banners and bars, the ammo counter's plate and the scoreboard. IMGUI, Repaint only unless noted.
    /// </summary>
    internal static class ZombiesHud
    {
        private static GUIStyle _text, _key, _price, _banner;
        private static int _styledAt;

        public static readonly Color Gold = new Color(1f, 0.84f, 0.38f);

        /// <summary>An interaction option's name starting with this is used but never drawn.</summary>
        public const string Hidden = "\u200B";

        private static void Styles(float k)
        {
            int at = Mathf.RoundToInt(k * 1000);
            if (_text != null && _styledAt == at)
                return;
            _styledAt = at;
            // Prompts in EFT's Bender, bold: clear at a glance (the display font read too thick: user, 2026-11-11)
            _text = ZombiesUi.ImguiLabel(Mathf.RoundToInt(21 * k), ZombiesUi.Ink, TextAnchor.MiddleLeft, false);
            _text.fontStyle = FontStyle.Bold;
            _text.wordWrap = false;
            _text.clipping = TextClipping.Overflow;
            _key = ZombiesUi.ImguiLabel(Mathf.RoundToInt(21 * k), new Color(0.06f, 0.05f, 0.05f), TextAnchor.MiddleCenter, false);
            _key.fontStyle = FontStyle.Bold;
            _key.wordWrap = false;
            _price = ZombiesUi.ImguiLabel(Mathf.RoundToInt(20 * k), Gold, TextAnchor.MiddleCenter, false);
            _price.fontStyle = FontStyle.Bold;
            _price.wordWrap = false;
            _banner = ZombiesUi.ImguiLabel(Mathf.RoundToInt(28 * k), Color.white, TextAnchor.MiddleCenter, false);
            _banner.fontStyle = FontStyle.Bold;
            _banner.wordWrap = false;
            _banner.clipping = TextClipping.Overflow;
        }

        private static Sprite Keycap => ZombiesUi.Glass(new Color(0.96f, 0.95f, 0.92f, 1f), new Color(0.74f, 0.72f, 0.69f, 1f), new Color(0f, 0f, 0f, 0.5f), 5f, 0.05f, 0.02f);
        private static Sprite Pill => ZombiesUi.Glass(new Color(0.09f, 0.08f, 0.075f, 0.82f), new Color(0.03f, 0.025f, 0.025f, 0.88f), new Color(1f, 1f, 1f, 0.14f), 6f, 0.08f);
        private static Sprite PricePill => ZombiesUi.Glass(new Color(0.20f, 0.16f, 0.06f, 0.85f), new Color(0.10f, 0.08f, 0.03f, 0.9f), new Color(1f, 0.84f, 0.38f, 0.45f), 5f, 0.06f);
        private static Sprite PriceShort => ZombiesUi.Glass(new Color(0.25f, 0.05f, 0.04f, 0.85f), new Color(0.12f, 0.02f, 0.02f, 0.9f), new Color(1f, 0.25f, 0.2f, 0.5f), 5f, 0.06f);

        private static void Box(Rect r, Sprite s, float alpha = 1f)
        {
            Color was = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, was.a * alpha);
            GUI.Box(r, GUIContent.none, ZombiesUi.Style(s));
            GUI.color = was;
        }

        private static void Shadow(Rect r, float k, float alpha = 0.55f)
        {
            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, was.a * alpha);
            GUI.Box(new Rect(r.x - 14 * k, r.y - 8 * k, r.width + 28 * k, r.height + 22 * k), GUIContent.none, ZombiesUi.Style(ZombiesUi.Soft));
            GUI.color = was;
        }

        /// <summary>Text with a soft drop shadow in the given style.</summary>
        public static void Label(Rect r, string text, GUIStyle style, Color colour, float k)
        {
            Color was = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, colour.a * 0.75f);
            GUI.Label(new Rect(r.x + 2 * k, r.y + 2 * k, r.width, r.height), text, style);
            style.normal.textColor = colour;
            GUI.Label(r, text, style);
            style.normal.textColor = was;
        }

        // ---------- Prompts ----------

        /// <summary>
        /// One prompt, centred at a height (0-1 of the screen), the original's way: "Press [F] to buy M1A  500" / "Hold [F] to rebuild
        /// barrier". verb null: no key shown (the other options under the selected one). dim: greyed. Returns its height.
        /// </summary>
        public static float Prompt(string verb, string key, string action, string price, bool afford, bool dim, float yFrac, float k, float alpha = 1f)
        {
            if (Event.current.type != EventType.Repaint)
                return 42f * k;
            Styles(k);
            float h = 42f * k, pad = 16f * k, gap = 8f * k;
            bool showKey = !string.IsNullOrEmpty(verb) && !string.IsNullOrEmpty(key);
            float verbW = showKey ? _text.CalcSize(new GUIContent(verb)).x : 0f;
            float keyW = showKey ? Mathf.Max(34f * k, _key.CalcSize(new GUIContent(key)).x + 18f * k) : 0f;
            float textW = _text.CalcSize(new GUIContent(action)).x;
            float priceW = string.IsNullOrEmpty(price) ? 0f : _price.CalcSize(new GUIContent(price)).x + 22f * k;
            float w = pad + (showKey ? verbW + gap + keyW + gap : 0f) + textW + (priceW > 0 ? gap * 1.5f + priceW : 0f) + pad;
            float x = Screen.width * 0.5f - w * 0.5f, y = Screen.height * yFrac;
            var r = new Rect(x, y, w, h);
            Color was = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha * (dim ? 0.55f : 1f));
            Shadow(r, k);
            Box(r, Pill);
            float cx = x + pad;
            Color ink = dim ? ZombiesUi.Dim : ZombiesUi.Ink;
            if (showKey)
            {
                Label(new Rect(cx, y, verbW + 4 * k, h), verb, _text, ink, k);
                cx += verbW + gap;
                var kr = new Rect(cx, y + 5 * k, keyW, h - 10 * k);
                Box(kr, Keycap);
                GUI.Label(kr, key, _key);
                cx += keyW + gap;
            }
            Label(new Rect(cx, y, textW + 4 * k, h), action, _text, ink, k);
            cx += textW + gap * 1.5f;
            if (priceW > 0)
            {
                var pr = new Rect(cx, y + 7 * k, priceW, h - 14 * k);
                Box(pr, afford ? PricePill : PriceShort);
                _price.normal.textColor = afford ? Gold : new Color(1f, 0.45f, 0.4f);
                GUI.Label(pr, price, _price);
                _price.normal.textColor = Gold;
            }
            GUI.color = was;
            return h;
        }

        /// <summary>"Buy M1A" -> "to buy M1A" (after "Press [F]").</summary>
        public static string To(string action) =>
            string.IsNullOrEmpty(action) ? "" : "to " + char.ToLowerInvariant(action[0]) + action.Substring(1);

        // "Buy M14 (500)" -> ("Buy M14", "500"); anything else as it is
        private static readonly Regex Priced = new Regex(@"^(.*?)\s*\((\d[\d,]*)(?:\s*points?)?\)\s*$");

        public static (string text, string price) Split(string name)
        {
            if (string.IsNullOrEmpty(name))
                return ("", null);
            Match m = Priced.Match(name);
            return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (name, null);
        }

        // ---------- EFT's interaction list, ours instead (zombies matches) ----------

        private static GamePlayerOwner _owner;
        private static Player _ownerOf;
        private static float _nextHide;

        /// <summary>
        /// The options of what you're looking at (doors, debris, wall buys, machines, the box...): EFT's own grey list
        /// is kept out of sight in a match and ours drawn instead, the selected one lit, the rest under it.
        /// </summary>
        public static void DrawActions()
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null || ZombiesDowned.Mine != ZombiesDowned.State.Up || ZombiesFrontEnd.InMatchMenu)
                return;
            try
            {
                if (_ownerOf != me || _owner == null)
                {
                    _owner = ((Component)me).GetComponent<GamePlayerOwner>();
                    _ownerOf = me;
                }
                var state = _owner?.AvailableInteractionState?.Value;
                if (state?.Actions == null || state.Actions.Count == 0)
                    return;
                float k = Screen.height / 1080f;
                string key = ZombiesControls.KeyName(ZombiesControls.Act.Use);
                float y = 0.58f;
                foreach (var a in state.Actions)
                {
                    if (a == null)
                        continue;
                    string name = a.Name ?? "";
                    // EFT's own options are locale keys
                    if (!name.Contains(" ") && name.Length > 0)
                    {
                        string loc = name.Localized();
                        if (!string.IsNullOrEmpty(loc))
                            name = loc;
                    }
                    // An option that's there to use but never shown (the easter egg's rocks: found by looking, as in the original)
                    if (name.StartsWith(Hidden))
                        continue;
                    var (text, price) = Split(name);
                    bool selected = a == state.SelectedAction;
                    bool afford = !a.Disabled;
                    float h = selected ? Prompt("Press", key, To(text), price, afford, a.Disabled, y, k)
                        : Prompt(null, null, text, price, afford, true, y, k);
                    y += (h + 8f * k) / Screen.height;
                }
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"hud: prompt failed ({e.Message})");
            }
        }

        /// <summary>Each frame of a match: EFT's own interaction list stays invisible (it still runs: the selection).</summary>
        public static void HideEftActions()
        {
            if (!ZombiesMatch.Active || Time.unscaledTime < _nextHide)
                return;
            _nextHide = Time.unscaledTime + 0.5f;
            foreach (ActionPanel p in UnityEngine.Object.FindObjectsOfType<ActionPanel>())
            {
                if (p == null)
                    continue;
                CanvasGroup g = p.GetComponent<CanvasGroup>() ?? p.gameObject.AddComponent<CanvasGroup>();
                g.alpha = 0f;
                _hidden.Add(g);
            }
        }

        private static readonly HashSet<CanvasGroup> _hidden = new HashSet<CanvasGroup>();

        /// <summary>The match is over: EFT's list shown again for normal raids.</summary>
        public static void RestoreEftActions()
        {
            foreach (CanvasGroup g in _hidden)
                if (g != null)
                    g.alpha = 1f;
            _hidden.Clear();
        }

        // ---------- Banners and bars (the exfil) ----------

        /// <summary>A banner across the top middle: glass strip, red accent under it, the words in a colour.</summary>
        public static void Banner(float yFrac, string text, Color colour, float k, float size = 28f, float alpha = 1f)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Styles(k);
            _banner.fontSize = Mathf.RoundToInt(size * k);
            float tw = _banner.CalcSize(new GUIContent(text)).x;
            float w = tw + 90f * k, h = size * 1.7f * k;
            var r = new Rect((Screen.width - w) / 2f, Screen.height * yFrac, w, h);
            Color was = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            Shadow(r, k, 0.5f);
            Box(r, Pill);
            GUI.color = new Color(colour.r, colour.g, colour.b, alpha * 0.85f);
            GUI.DrawTexture(new Rect(r.x + 20 * k, r.yMax - 3 * k, r.width - 40 * k, 2 * k), ZombiesUi.Tex(ZombiesUi.Divider));
            GUI.color = new Color(1f, 1f, 1f, alpha);
            Label(r, text, _banner, new Color(colour.r, colour.g, colour.b, alpha), k);
            GUI.color = was;
        }

        /// <summary>A glass progress bar with a label in it.</summary>
        public static void Bar(Rect r, float fill, Color colour, string label, float k)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Styles(k);
            Shadow(r, k, 0.5f);
            Box(r, Pill);
            var inner = new Rect(r.x + 3 * k, r.y + 3 * k, (r.width - 6 * k) * Mathf.Clamp01(fill), r.height - 6 * k);
            if (inner.width > 1f)
            {
                Color was = GUI.color;
                GUI.color = colour;
                GUI.Box(inner, GUIContent.none, ZombiesUi.Style(ZombiesUi.Flat(Color.white, new Color(0.72f, 0.72f, 0.72f), 3f)));
                GUI.color = was;
            }
            if (!string.IsNullOrEmpty(label))
            {
                _banner.fontSize = Mathf.RoundToInt(r.height * 0.62f);
                Label(r, label, _banner, Color.white, k);
            }
        }

        /// <summary>A glass window (the exfil vote).</summary>
        public static void Window(Rect r, float k)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            ZombiesUi.ImguiPanel(r);
            Color was = GUI.color;
            GUI.color = ZombiesUi.Red;
            GUI.DrawTexture(new Rect(r.x + 16 * k, r.y + 1 * k, r.width - 32 * k, 2 * k), ZombiesUi.Tex(ZombiesUi.Divider));
            GUI.color = was;
        }

        /// <summary>The plate behind the ammo counter: dark glass fading out to the left, a red line under it.</summary>
        public static void AmmoPlate(float right, float bottom, float width, float height, float k)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Color was = GUI.color;
            var r = new Rect(right - width + 30 * k, bottom - height, width, height + 14 * k);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(r, ZombiesUi.Tex(ZombiesUi.SweepRight));
            GUI.color = new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.85f);
            GUI.DrawTexture(new Rect(r.x + r.width * 0.25f, r.yMax - 3 * k, r.width * 0.75f, 2 * k), ZombiesUi.Tex(ZombiesUi.SweepRight));
            GUI.color = was;
        }
    }
}
