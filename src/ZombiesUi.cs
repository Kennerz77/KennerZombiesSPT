using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KennerZombies
{
    /// <summary>
    /// The look shared by the zombies menus (user, 2026-11-10: "more modern and lifelike, not dead flat squares"): EFT's
    /// own Bender font, panels of dark smoked glass (soft gradient, rounded corners, a faint edge and top light, film
    /// grain, a soft shadow under them), buttons that light up red from the left with an accent bar when hovered, and
    /// The original's front-end sounds (hover, select, back, denied, open, close, toggle, lobby join / leave, invites). All of it
    /// is made in code: the sprites are drawn into textures once, nothing ships but the sounds (data\sounds\zm_ui_*.wav).
    /// The IMGUI pages (statistics, zombies controls) use the same textures through Imgui*.
    /// </summary>
    internal static class ZombiesUi
    {
        public static readonly Color Red = new Color(0.82f, 0.10f, 0.07f);
        public static readonly Color RedDeep = new Color(0.42f, 0.03f, 0.02f);
        public static readonly Color Ink = new Color(0.93f, 0.91f, 0.86f);    // EFT's warm off-white
        public static readonly Color Dim = new Color(0.64f, 0.62f, 0.58f);
        public static readonly Color Faint = new Color(0.42f, 0.41f, 0.39f);
        public static readonly Color Ready = new Color(0.36f, 0.86f, 0.38f);
        public static readonly Color NotReady = new Color(0.90f, 0.20f, 0.16f);
        public static readonly Color Gold = new Color(0.95f, 0.78f, 0.32f);

        /// <summary>When an IMGUI page last closed on Esc (that same press isn't the menus' Esc too).</summary>
        public static float EscUsedAt = -10f;

        // ---------- Fonts: EFT's own ----------

        private static TMP_FontAsset _regular, _bold;
        private static bool _fontsLogged;

        /// <summary>The font for a text: EFT's own Bender (user, 2026-11-11: "use Tarkov's fonts"); bold ones in its bold style.</summary>
        public static TMP_FontAsset Font(bool bold)
        {
            if (_regular == null || _bold == null)
                FindFonts();
            return bold ? _bold ?? _regular : _regular ?? _bold;
        }

        private static void FindFonts()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Where(f => f != null).ToList();
                // EFT marks its fallback-only assets "DON'T USE!": only real ones. Its real Bender is "Bender Normal SDF";
                // bold is TMP's own style on it
                TMP_FontAsset Pick(Func<string, bool> want) => all.FirstOrDefault(f => !f.name.ToLowerInvariant().Contains("don't use") && want(f.name.ToLowerInvariant()));
                _regular = Pick(n => n.Contains("bender") && n.Contains("normal")) ?? Pick(n => n.Contains("bender") && !n.Contains("italic"));
                _bold = Pick(n => n.Contains("bender") && n.Contains("bold") && !n.Contains("italic")) ?? _regular;
                if (_regular == null)
                {
                    TMP_Text any = ZombiesMainMenu.Menu != null ? ZombiesMainMenu.Menu.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault() : null;
                    _regular = any != null ? any.font : all.FirstOrDefault();
                }
                if (!_fontsLogged && _regular != null)
                {
                    _fontsLogged = true;
                    ZombiesPlugin.Log.LogInfo($"Zombies menus: font {_regular.name}");
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies menus: EFT's fonts weren't found ({e.Message})");
            }
        }

        /// <summary>No font of our own any more: the menus use EFT's (kept so callers read the same).</summary>
        public static TMP_FontAsset Display => null;

        /// <summary>The HUD's font (EFT's Bender for IMGUI when the game has it; else the default).</summary>
        public static Font DisplayImguiFont() => ImguiFont(false);

        // IMGUI can't draw a TMP font; its source font is used when the game ships it
        private static Font _imguiFont;
        private static bool _imguiLooked;

        public static Font ImguiFont(bool bold = false)
        {
            if (!_imguiLooked || _imguiFont == null)
            {
                _imguiLooked = true;
                TMP_FontAsset f = Font(bold);
                _imguiFont = f != null ? f.sourceFontFile : null;
                if (_imguiFont == null)
                    _imguiFont = Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(x => x != null && x.name.ToLowerInvariant().Contains("bender"));
            }
            return _imguiFont;
        }

        // ---------- Sprites, drawn once ----------

        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();

        private static Sprite Cached(string key, Func<Sprite> make)
        {
            if (Sprites.TryGetValue(key, out Sprite s) && s != null)
                return s;
            s = make();
            Sprites[key] = s;
            return s;
        }

        // Signed distance to a rounded rectangle's edge (negative inside), for anti-aliased corners
        private static float RoundDist(float x, float y, float w, float h, float r)
        {
            float qx = Mathf.Abs(x - w / 2f) - (w / 2f - r), qy = Mathf.Abs(y - h / 2f) - (h / 2f - r);
            float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
        }

        private static Texture2D NewTex(int w, int h, string name, bool repeat = false) =>
            new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "KennerZombies " + name, wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave,
            };

        private static float Grain(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2246822519u);
                h = (h ^ (h >> 13)) * 1274126177u;
                return ((h ^ (h >> 16)) & 0xffff) / 65535f;
            }
        }

        /// <summary>
        /// A glass panel: a top-to-bottom gradient between two colours, rounded corners of radius r, a 1 px edge, a
        /// thin light along the top inside edge, and fine grain baked in. 9-sliced.
        /// </summary>
        public static Sprite Glass(Color top, Color bottom, Color edge, float r = 7f, float topLight = 0.07f, float grain = 0.035f)
        {
            string key = $"glass {top} {bottom} {edge} {r} {topLight} {grain}";
            return Cached(key, () =>
            {
                const int n = 64;
                Texture2D tex = NewTex(n, n, "glass");
                var px = new Color[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float d = RoundDist(x + 0.5f, y + 0.5f, n, n, r);
                        float inside = Mathf.Clamp01(0.5f - d);
                        Color c = Color.Lerp(bottom, top, y / (n - 1f));
                        // edge: 1 px band at the outline
                        float e = Mathf.Clamp01(1f - Mathf.Abs(d + 1f));
                        c = Color.Lerp(c, new Color(edge.r, edge.g, edge.b, Mathf.Max(c.a, edge.a)), e * edge.a);
                        // the top light just inside the top edge
                        if (y >= n - 3 && d < -1.5f)
                            c = new Color(Mathf.Min(1f, c.r + topLight), Mathf.Min(1f, c.g + topLight), Mathf.Min(1f, c.b + topLight), c.a);
                        float g = (Grain(x, y, 7) - 0.5f) * grain;
                        c = new Color(Mathf.Clamp01(c.r + g), Mathf.Clamp01(c.g + g), Mathf.Clamp01(c.b + g), c.a * inside);
                        px[y * n + x] = c;
                    }
                tex.SetPixels(px);
                tex.Apply();
                float b = Mathf.Max(r + 2f, 10f);
                return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            });
        }

        /// <summary>The standard panel: smoked glass, near black with a little warmth.</summary>
        public static Sprite Panel => Glass(new Color(0.085f, 0.075f, 0.07f, 0.80f), new Color(0.03f, 0.025f, 0.025f, 0.90f), new Color(1f, 1f, 1f, 0.13f));

        /// <summary>A darker, nearly solid panel for pop-ups over everything.</summary>
        public static Sprite Window => Glass(new Color(0.10f, 0.085f, 0.08f, 0.97f), new Color(0.035f, 0.03f, 0.03f, 0.98f), new Color(1f, 1f, 1f, 0.16f), 9f, 0.09f);

        /// <summary>A button's resting face.</summary>
        public static Sprite ButtonFace => Glass(new Color(0.13f, 0.11f, 0.10f, 0.82f), new Color(0.05f, 0.04f, 0.04f, 0.88f), new Color(1f, 1f, 1f, 0.16f), 5f, 0.10f);

        /// <summary>A row in a list (lighter, thinner edge).</summary>
        public static Sprite RowFace => Glass(new Color(1f, 1f, 1f, 0.055f), new Color(1f, 1f, 1f, 0.025f), new Color(1f, 1f, 1f, 0.08f), 4f, 0.04f, 0.02f);

        /// <summary>Red light coming in from the left (a hovered button), white so it takes a colour.</summary>
        public static Sprite SweepLeft => Cached("sweep", () =>
        {
            const int w = 128, h = 8;
            Texture2D tex = NewTex(w, h, "sweep");
            var px = new Color[w * h];
            for (int x = 0; x < w; x++)
            {
                float t = x / (w - 1f);
                float a = Mathf.Pow(1f - t, 1.6f) * 0.95f + 0.12f;
                for (int y = 0; y < h; y++)
                    px[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        });

        /// <summary>The same light coming in from the right (the ammo counter's plate).</summary>
        public static Sprite SweepRight => Cached("sweepR", () =>
        {
            const int w = 128, h = 8;
            Texture2D tex = NewTex(w, h, "sweepR");
            var px = new Color[w * h];
            for (int x = 0; x < w; x++)
            {
                float t = 1f - x / (w - 1f);
                float a = Mathf.Pow(1f - t, 1.4f);
                for (int y = 0; y < h; y++)
                    px[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        });

        /// <summary>A soft blob, solid in the middle and fading out over its edge: shadows and glows (9-sliced).</summary>
        public static Sprite Soft => Cached("soft", () =>
        {
            const int n = 64, edge = 24;
            Texture2D tex = NewTex(n, n, "soft");
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Min(Mathf.Min(x, n - 1 - x), Mathf.Min(y, n - 1 - y));
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / edge)));
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(edge, edge, edge, edge));
        });

        /// <summary>An anti-aliased disc with a faint glow ring (ready lights).</summary>
        public static Sprite Dot => Cached("dot", () =>
        {
            const int n = 48;
            Texture2D tex = NewTex(n, n, "dot");
            var px = new Color[n * n];
            float c = (n - 1) / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x - c, y - c).magnitude;
                    float core = Mathf.Clamp01(11f - d);
                    float glow = Mathf.Clamp01(1f - (d - 10f) / 13f) * 0.35f;
                    float shine = Mathf.Clamp01(1f - new Vector2(x - c + 3f, y - c - 4f).magnitude / 6f) * 0.5f;
                    float a = Mathf.Max(core, glow);
                    float v = 0.8f + shine * core;
                    px[y * n + x] = new Color(Mathf.Min(1f, v), Mathf.Min(1f, v), Mathf.Min(1f, v), a);
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        });

        /// <summary>A line fading out at both ends (dividers under headings).</summary>
        public static Sprite Divider => Cached("divider", () =>
        {
            const int w = 128, h = 2;
            Texture2D tex = NewTex(w, h, "divider");
            var px = new Color[w * h];
            for (int x = 0; x < w; x++)
            {
                float t = x / (w - 1f);
                float a = Mathf.Clamp01(Mathf.Min(t, 1f - t) * 5f);
                px[x] = px[w + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        });

        /// <summary>Darker towards the screen's edges: the menu video sits back and the text stands out.</summary>
        public static Sprite Vignette => Cached("vignette", () =>
        {
            const int n = 128;
            Texture2D tex = NewTex(n, n, "vignette");
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x - n / 2f) / (n / 2f), dy = (y - n / 2f) / (n / 2f);
                    float d = Mathf.Sqrt(dx * dx * 0.8f + dy * dy);
                    px[y * n + x] = new Color(0f, 0f, 0f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((d - 0.45f) / 0.9f)));
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        });

        /// <summary>A fade from nothing at the top to solid at the bottom (under a picture's caption).</summary>
        public static Sprite FadeUp => Cached("fadeup", () =>
        {
            const int w = 4, h = 128;
            Texture2D tex = NewTex(w, h, "fadeup");
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float a = Mathf.SmoothStep(0f, 1f, 1f - y / (h - 1f));
                for (int x = 0; x < w; x++)
                    px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
        });

        // ---------- Building blocks ----------

        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        /// <summary>Anchored at one point (0..1 of the parent), placed by its pivot at an offset, of a size.</summary>
        public static RectTransform Place(this RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Filling its parent, less an inset on each side (left, bottom, right, top).</summary>
        public static RectTransform Fill(this RectTransform rt, float l = 0f, float b = 0f, float r = 0f, float t = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(l, b);
            rt.offsetMax = new Vector2(-r, -t);
            return rt;
        }

        public static Image Img(Transform parent, string name, Sprite sprite, Color color, bool sliced = true, bool raycast = false)
        {
            RectTransform rt = Rect(parent, name);
            Image img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.type = sliced && sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        /// <summary>A panel with its soft shadow: the returned rect is the panel itself.</summary>
        public static RectTransform PanelWithShadow(Transform parent, string name, Sprite face = null, float shadow = 0.55f)
        {
            RectTransform holder = Rect(parent, name);
            Image sh = Img(holder, "Shadow", Soft, new Color(0f, 0f, 0f, shadow));
            sh.rectTransform.Fill(-22f, -30f, -22f, -14f);
            Image body = Img(holder, "Face", face ?? Panel, Color.white, true, true);
            body.rectTransform.Fill();
            return holder;
        }

        public static TextMeshProUGUI Text(Transform parent, string name, string text, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.Left, bool bold = false, float spacing = 0f)
        {
            RectTransform rt = Rect(parent, name);
            TextMeshProUGUI t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset f = Font(bold);
            if (f != null)
                t.font = f;
            t.text = text ?? "";
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.characterSpacing = spacing;
            // EFT's font made bold by TMP; the display font is heavy enough as it is
            if (bold && t.font != Display)
                t.fontStyle = FontStyles.Bold;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            t.richText = true;
            return t;
        }

        /// <summary>A thin divider line.</summary>
        public static Image Line(Transform parent, Color color, float y, float inset = 0f)
        {
            Image line = Img(parent, "Divider", Divider, color, false);
            RectTransform rt = line.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(inset, 0f);
            rt.offsetMax = new Vector2(-inset, 0f);
            rt.anchoredPosition = new Vector2(0f, -y);
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, 1.5f);
            return line;
        }

        public static ZButton Button(Transform parent, string name, string label, float fontSize, Action click, bool big = false)
        {
            RectTransform rt = Rect(parent, name);
            ZButton b = rt.gameObject.AddComponent<ZButton>();
            b.Build(label, fontSize, big);
            b.Clicked = click;
            return b;
        }

        // ---------- Sounds (the original's front end) ----------

        private static AudioSource _ui;
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
        private static readonly Dictionary<string, float> LastPlayed = new Dictionary<string, float>();
        private static BepInEx.Configuration.ConfigEntry<float> _volume;

        public static void Bind(BepInEx.Configuration.ConfigFile config, string section)
        {
            _volume = config.Bind(section, "Menu button sounds", 0.6f, new BepInEx.Configuration.ConfigDescription(
                "The zombies menus' own sounds (hover, click, back, pop-ups, players joining and leaving, invites).",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 1f)));
        }

        // Each sound's own level against the others (the hover is a tick; the join and the invite are big)
        private static readonly Dictionary<string, float> Level = new Dictionary<string, float>
        {
            ["hover"] = 0.45f, ["select"] = 0.8f, ["back"] = 0.8f, ["denied"] = 0.7f, ["open"] = 0.7f, ["close"] = 0.65f,
            ["toggle"] = 0.75f, ["join"] = 0.9f, ["leave"] = 0.9f, ["invite"] = 0.85f, ["invite_sent"] = 0.8f, ["all_ready"] = 0.85f,
        };

        /// <summary>A menu sound by its short name (zm_ui_&lt;name&gt;.wav); the same one at most every 50 ms.</summary>
        public static void Sound(string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name))
                    return;
                if (LastPlayed.TryGetValue(name, out float at) && Time.unscaledTime - at < 0.05f)
                    return;
                LastPlayed[name] = Time.unscaledTime;
                if (!Clips.TryGetValue(name, out AudioClip clip) || clip == null)
                {
                    clip = ZombiesWav.Load(Path.Combine(ZombiesPlugin.Dir, "sounds"), "zm_ui_" + name);
                    Clips[name] = clip;
                }
                clip = ZombiesWav.Fresh(clip);
                if (clip == null)
                    return;
                if (_ui == null)
                {
                    var go = new GameObject("KennerZombies MenuSounds");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _ui = go.AddComponent<AudioSource>();
                    _ui.playOnAwake = false;
                    _ui.spatialBlend = 0f;
                    _ui.ignoreListenerPause = true;
                }
                float v = (_volume?.Value ?? 0.6f) * (ZombiesSounds.Master?.Value ?? 1f) * (Level.TryGetValue(name, out float l) ? l : 0.8f);
                _ui.PlayOneShot(clip, v);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies menus: sound {name} didn't play ({e.Message})");
            }
        }

        // ---------- The same look for the IMGUI pages ----------

        private static readonly Dictionary<string, Texture2D> ImguiTex = new Dictionary<string, Texture2D>();

        /// <summary>A sprite's texture for a GUIStyle (with Border() as its 9-slice border).</summary>
        public static Texture2D Tex(Sprite s) => s != null ? s.texture : Texture2D.whiteTexture;

        public static RectOffset Border(Sprite s) =>
            s == null ? new RectOffset() : new RectOffset((int)s.border.x, (int)s.border.z, (int)s.border.w, (int)s.border.y);

        /// <summary>A rounded button face of one flat colour (IMGUI hover / active states).</summary>
        public static Sprite Flat(Color top, Color bottom, float r = 5f) => Glass(top, bottom, new Color(1f, 1f, 1f, 0.18f), r, 0.10f);

        /// <summary>An IMGUI button in the menus' look: glass at rest, red when hovered, darker red pressed.</summary>
        public static GUIStyle ImguiButton(int size)
        {
            var st = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = size,
                border = Border(ButtonFace), padding = new RectOffset(10, 10, 4, 4),
            };
            Font f = ImguiFont(true);
            if (f != null)
                st.font = f;
            st.normal.background = Tex(ButtonFace);
            st.hover.background = Tex(Flat(new Color(0.78f, 0.10f, 0.07f, 0.95f), new Color(0.45f, 0.04f, 0.03f, 0.95f)));
            st.active.background = Tex(Flat(new Color(0.50f, 0.05f, 0.03f, 1f), new Color(0.30f, 0.02f, 0.02f, 1f)));
            st.focused.background = st.normal.background;
            st.normal.textColor = Ink;
            st.hover.textColor = st.active.textColor = Color.white;
            return st;
        }

        public static GUIStyle ImguiLabel(int size, Color color, TextAnchor align = TextAnchor.MiddleLeft, bool bold = false)
        {
            var st = new GUIStyle(GUI.skin.label) { fontSize = size, alignment = align, fontStyle = bold ? FontStyle.Bold : FontStyle.Normal, wordWrap = true };
            Font f = ImguiFont(bold);
            if (f != null)
                st.font = f;
            st.normal.textColor = color;
            return st;
        }

        /// <summary>A glass panel drawn in IMGUI (with its soft shadow), Repaint only.</summary>
        public static void ImguiPanel(Rect r, bool window = true, float shadow = 0.6f)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, shadow);
            Sprite soft = Soft;
            GUI.Box(new Rect(r.x - 26f, r.y - 16f, r.width + 52f, r.height + 46f), GUIContent.none, Style(soft));
            GUI.color = was;
            GUI.Box(r, GUIContent.none, Style(window ? Window : Panel));
        }

        /// <summary>Screen-wide darkening under a page.</summary>
        public static void ImguiDim(float alpha = 0.55f)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Color was = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, alpha);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = was;
        }

        private static readonly Dictionary<Sprite, GUIStyle> BoxStyles = new Dictionary<Sprite, GUIStyle>();

        public static GUIStyle Style(Sprite s)
        {
            if (BoxStyles.TryGetValue(s, out GUIStyle st) && st.normal.background != null)
                return st;
            st = new GUIStyle { border = Border(s) };
            st.normal.background = Tex(s);
            BoxStyles[s] = st;
            return st;
        }
    }

    /// <summary>
    /// A zombies menu button: glass face, EFT's font in capitals; hovered, a red light sweeps in from the left with an
    /// accent bar and a tick of sound; pressed, it dips. Off (greyed): dim, and a click gives the denied sound.
    /// </summary>
    internal class ZButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        public Action Clicked;
        /// <summary>The pointer came onto it (the map list shows that map's card).</summary>
        public Action Hovered;
        public string ClickSound = "select";
        public bool Interactable = true;
        /// <summary>Lit as if hovered (the selected map in a list, a toggled choice).</summary>
        public bool Selected;
        public TextMeshProUGUI Label { get; private set; }

        private Image _face, _sweep, _accent, _edgeGlow;
        private float _h, _sel;
        private bool _over, _down;

        public void Build(string label, float fontSize, bool big)
        {
            var rt = (RectTransform)transform;
            _edgeGlow = ZombiesUi.Img(rt, "Glow", ZombiesUi.Soft, new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0f));
            _edgeGlow.rectTransform.Fill(-14f, -14f, -14f, -14f);
            _face = ZombiesUi.Img(rt, "Face", ZombiesUi.ButtonFace, Color.white, true, true);
            _face.rectTransform.Fill();
            _sweep = ZombiesUi.Img(_face.transform, "Sweep", ZombiesUi.SweepLeft, new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0f), false);
            _sweep.rectTransform.Fill(2f, 2f, 2f, 2f);
            _accent = ZombiesUi.Img(_face.transform, "Accent", null, ZombiesUi.Red, false);
            RectTransform a = _accent.rectTransform;
            a.anchorMin = new Vector2(0f, 0.5f);
            a.anchorMax = new Vector2(0f, 0.5f);
            a.pivot = new Vector2(0f, 0.5f);
            a.anchoredPosition = new Vector2(2f, 0f);
            a.sizeDelta = new Vector2(3f, 0f);
            // Every button in the zombies display font
            Label = ZombiesUi.Text(rt, "Label", (label ?? "").ToUpperInvariant(), fontSize, ZombiesUi.Ink, TextAlignmentOptions.Center, true, big ? 2f : 1f);
            Label.rectTransform.Fill(12f, 0f, 12f, 0f);
        }

        public void SetLabel(string text)
        {
            if (Label != null)
                Label.text = (text ?? "").ToUpperInvariant();
        }

        public void OnPointerEnter(PointerEventData e)
        {
            _over = true;
            if (Interactable)
                ZombiesUi.Sound("hover");
            try
            {
                Hovered?.Invoke();
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Zombies menus: button hover '{Label?.text}': {ex}");
            }
        }

        public void OnPointerExit(PointerEventData e)
        {
            _over = false;
            _down = false;
        }

        public void OnPointerDown(PointerEventData e) => _down = e.button == PointerEventData.InputButton.Left;

        public void OnPointerUp(PointerEventData e) => _down = false;

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left)
                return;
            if (!Interactable)
            {
                ZombiesUi.Sound("denied");
                return;
            }
            ZombiesUi.Sound(ClickSound);
            try
            {
                Clicked?.Invoke();
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Zombies menus: button '{Label?.text}': {ex}");
            }
        }

        private void OnDisable()
        {
            _over = _down = false;
            _h = 0f;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _h = Mathf.MoveTowards(_h, _over && Interactable ? 1f : 0f, dt * 7f);
            _sel = Mathf.MoveTowards(_sel, Selected ? 1f : 0f, dt * 7f);
            float lit = Mathf.Max(_h, _sel * 0.7f);
            float ease = 1f - (1f - lit) * (1f - lit);
            Color red = ZombiesUi.Red;
            if (_sweep != null)
                _sweep.color = new Color(red.r, red.g, red.b, 0.72f * ease);
            if (_edgeGlow != null)
                _edgeGlow.color = new Color(red.r, red.g, red.b, 0.28f * ease);
            if (_accent != null)
            {
                float fullH = ((RectTransform)transform).rect.height - 8f;
                _accent.rectTransform.sizeDelta = new Vector2(3f + 2f * ease, Mathf.Lerp(fullH * 0.35f, fullH, ease));
                _accent.color = Interactable ? new Color(red.r, red.g, red.b, 0.45f + 0.55f * ease) : new Color(0.4f, 0.4f, 0.4f, 0.3f);
            }
            if (_face != null)
                _face.color = Interactable ? Color.white : new Color(1f, 1f, 1f, 0.55f);
            if (Label != null)
                Label.color = !Interactable ? new Color(ZombiesUi.Faint.r, ZombiesUi.Faint.g, ZombiesUi.Faint.b, 0.9f)
                    : Color.Lerp(ZombiesUi.Ink, Color.white, ease);
            float s = _down && Interactable ? 0.975f : 1f;
            transform.localScale = Vector3.Lerp(transform.localScale, new Vector3(s, s, 1f), dt * 20f);
        }
    }

    /// <summary>A plain clickable area (a player's row): hover tint, a click with the pointer's screen position.</summary>
    internal class ZClickable : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public Action<Vector2> Clicked;
        public Image Tint;
        public Color Rest = new Color(1f, 1f, 1f, 0f), Hover = new Color(0.82f, 0.10f, 0.07f, 0.22f);
        private bool _over;
        private float _h;

        public void OnPointerEnter(PointerEventData e)
        {
            _over = true;
            if (Clicked != null)
                ZombiesUi.Sound("hover");
        }

        public void OnPointerExit(PointerEventData e) => _over = false;

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left || Clicked == null)
                return;
            ZombiesUi.Sound("select");
            try
            {
                Clicked(e.position);
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Zombies menus: row click: {ex}");
            }
        }

        private void OnDisable()
        {
            _over = false;
            _h = 0f;
        }

        private void Update()
        {
            _h = Mathf.MoveTowards(_h, _over && Clicked != null ? 1f : 0f, Time.unscaledDeltaTime * 8f);
            if (Tint != null)
                Tint.color = Color.Lerp(Rest, Hover, _h);
        }
    }
}
