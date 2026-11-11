using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InputSystem;
using EFT.UI;
using EFT.UI.Matchmaker;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KennerZombies
{
    /// <summary>
    /// The zombies mode is chosen on the main menu (the user, 2026-11-07): a NORMAL / ZOMBIES dropdown at its top right
    /// (a copy of the game's own dropdown) is the only place the mode is switched. In zombies mode:
    /// - the main menu is cut down to PLAY ZOMBIES (EFT's play button, renamed) and CHARACTER (and EXIT): no trading,
    ///   no hideout, no PvE / PvP toggle, until the mode goes back to normal; EFT's logo is the zombies logo;
    /// - the music and the red tint follow the mode on every screen (ZombiesMenu), without a cut;
    /// - PLAY ZOMBIES's side select shows only your PMC, in the middle (no scav);
    /// - the map select shows only the zombies maps (ZombiesMapSelect) and has no mode switch of its own.
    /// Normal mode puts every hidden button back.
    /// </summary>
    internal static class ZombiesMainMenu
    {
        private const string DropdownName = "KennerZombies MenuMode";
        private const string LabelName = "KennerZombies MenuMode Label";
        private static readonly FieldInfo OverlayField = AccessTools.Field(typeof(DropDownBox), "_overlayLayer");

        private static MenuScreen _menu;

        /// <summary>The main menu screen (the zombies menu background sits behind it).</summary>
        public static MenuScreen Menu => _menu;

        /// <summary>The character (side) select screen, once shown (the zombies menu background sits behind it too).</summary>
        public static MatchMakerSideSelectionScreen SideScreen { get; private set; }
        private static string _playKey;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MenuScreen), nameof(MenuScreen.Show), new[] { typeof(MenuScreen.MainMenuBaseScreenController) }),
                postfix: new HarmonyMethod(typeof(ZombiesMainMenu), nameof(MenuShown)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(MatchMakerSideSelectionScreen), nameof(MatchMakerSideSelectionScreen.Show),
                    new[] { typeof(MatchMakerSideSelectionScreen.RaidSideSelectionScreenController) }),
                postfix: new HarmonyMethod(typeof(ZombiesMainMenu), nameof(SideShown)) { priority = Priority.Last });
            // Esc on the match's menu while one of our pop-ups / pages is up closes that only, not EFT's menu
            harmony.Patch(AccessTools.DeclaredMethod(typeof(MenuScreen), nameof(MenuScreen.TranslateCommand)),
                prefix: new HarmonyMethod(typeof(ZombiesMainMenu), nameof(HoldEsc)));
            // The match's Esc menu shows the match behind it, not EFT's menu scene (user, 2026-11-10)
            var show = AccessTools.DeclaredMethod(typeof(EnvironmentUI), nameof(EnvironmentUI.ShowEnvironment));
            if (show != null)
                harmony.Patch(show, prefix: new HarmonyMethod(typeof(ZombiesMainMenu), nameof(NoSceneInMatch)));
        }

        private static bool HoldEsc(EFT.InputSystem.ECommand command, ref EFT.InputSystem.InputNode.ETranslateResult __result)
        {
            if (!ZombiesFrontEnd.BlockEsc || !command.IsCommand(EFT.InputSystem.ECommand.Escape))
                return true;
            __result = EFT.InputSystem.InputNode.ETranslateResult.BlockAll;
            return false;
        }

        private static bool _sceneHeldLogged;

        private static void NoSceneInMatch(ref bool value)
        {
            if (!value || !ZombiesMatch.Active || !ZombiesPlugin.InRealRaid)
                return;
            value = false;
            if (!_sceneHeldLogged)
            {
                _sceneHeldLogged = true;
                ZombiesPlugin.Log.LogInfo("Main menu: EFT's menu scene held off behind the zombies match's Esc menu");
            }
        }

        // ---------- The main menu ----------

        private static void MenuShown(MenuScreen __instance)
        {
            try
            {
                _menu = __instance;
                if (InRaid)
                {
                    foreach (string n in new[] { DropdownName, LabelName, LogoName })
                        __instance.transform.Find(n)?.gameObject.SetActive(false);
                    foreach (string n in new[] { ReportButtonName, StatsButtonName })
                        __instance._hideoutButton?.transform.parent.Find(n)?.gameObject.SetActive(false);
                    return;
                }
                // The old NORMAL / ZOMBIES switch is gone: a ZOMBIES MODE button (ZombiesFrontEnd) and the zombies
                // menu's EXIT ZOMBIES MODE switch the mode now (user, 2026-11-10)
                __instance.transform.Find(DropdownName)?.gameObject.SetActive(false);
                __instance.transform.Find(LabelName)?.gameObject.SetActive(false);
                ApplyMenu(__instance);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Main menu (zombies mode): {e}");
            }
        }

        // The Esc menu inside a running raid (the only time the switch hides). Not just "a GameWorld with a player":
        // the hideout's or a finished raid's world can still be up as the main menu comes back, and the switch vanished
        // after going hideout -> raid -> main menu (user, 2026-11-08)
        private static bool InRaid => ZombiesPlugin.InRealRaid;

        /// <summary>Each frame (the plugin's Update): EFT's own main menu buttons kept off under the zombies menu.</summary>
        public static void Tick()
        {
            try
            {
                if (_feOn && _menu != null)
                    HideEftButtons();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Main menu (zombies mode) check: {e}");
            }
        }

        // ---------- Under the zombies menu (ZombiesFrontEnd): EFT's buttons and the bottom task bar out of sight ----------

        private static bool _feOn, _feLobby, _eftHidden;
        private static CanvasGroup _barGroup;

        /// <summary>The zombies menu (or its lobby) is on screen or gone: EFT's main menu under it follows.</summary>
        public static void FrontEnd(bool on, bool lobby)
        {
            if (on == _feOn && lobby == _feLobby)
                return;
            _feOn = on;
            _feLobby = lobby;
            try
            {
                if (_menu == null)
                    return;
                if (on)
                    HideEftButtons();
                else if (_eftHidden)
                {
                    _eftHidden = false;
                    if (!InRaid)
                        foreach (Component c in new Component[] { _menu._playButton, _menu._playerButton, _menu._exitButton })
                            if (c != null)
                                c.gameObject.SetActive(true);
                }
                TaskBar(!on);
                ApplyMenu(_menu);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Main menu: couldn't make way for the zombies menu ({e.Message})");
            }
        }

        private static void HideEftButtons()
        {
            // (DISCONNECT and RESUME: the match's Esc menu; EFT shows them again itself next time it opens)
            foreach (Component c in new Component[] { _menu._playButton, _menu._playerButton, _menu._tradeButton, _menu._hideoutButton,
                         _menu._exitButton, _menu._toggleGameModeButton, _menu._disconnectButton, _menu._hideScreenButton })
                if (c != null && c.gameObject.activeSelf)
                {
                    c.gameObject.SetActive(false);
                    _eftHidden = true;
                }
            foreach (string n in new[] { ReportButtonName, StatsButtonName })
                _menu._hideoutButton?.transform.parent.Find(n)?.gameObject.SetActive(false);
        }

        // The bottom bar faded out and dead to clicks (not switched off: EFT's own code keeps talking to it)
        private static void TaskBar(bool show)
        {
            if (_barGroup == null)
            {
                var bar = UnityEngine.Object.FindObjectOfType<MenuTaskBar>();
                if (bar == null)
                    return;
                _barGroup = bar.GetComponent<CanvasGroup>() ?? bar.gameObject.AddComponent<CanvasGroup>();
            }
            _barGroup.alpha = show ? 1f : 0f;
            _barGroup.blocksRaycasts = show;
            _barGroup.interactable = show;
        }

        /// <summary>The normal main menu's ZOMBIES MODE button.</summary>
        public static void EnterZombies() => SetMode(true);

        /// <summary>The zombies menu's EXIT ZOMBIES MODE.</summary>
        public static void LeaveZombies() => SetMode(false);

        private static void SetMode(bool zombies)
        {
            if (zombies == ZombiesMapSelect.Zombies)
                return;
            ZombiesMapSelect.SetZombies(zombies);
            if (_menu != null)
                ApplyMenu(_menu);
            ZombiesPlugin.Log.LogInfo($"Main menu: {(zombies ? "zombies" : "normal")} mode");
        }

        // ---------- Fika's main-menu panels: hidden in zombies mode only (Fika's files and settings untouched) ----------
        // The orange banner is EFT's own beta-warning box (MenuScreen._alphaWarningGameObject) with Fika's text in it
        // ("Welcome to Fika... use the Server Browser": not how zombies joins, it has its own party panel); the
        // ONLINE PLAYERS window is Fika's MainMenuUIScript._userInterface. Both put back as they were in normal mode.
        // The zombies PLAYERS panel says "(Powered by Fika)" instead (user, 2026-11-09).
        private static bool _bannerHidden, _bannerWas, _fikaHidden, _fikaWas;
        private static System.Type _fikaUiType;
        private static bool _fikaUiLooked;
        private static System.Reflection.PropertyInfo _fikaInstance;
        private static System.Reflection.FieldInfo _fikaPanel;

        private static GameObject FikaOnlinePlayers()
        {
            if (!_fikaUiLooked)
            {
                _fikaUiLooked = true;
                _fikaUiType = AccessTools.TypeByName("Fika.Core.UI.Custom.MainMenuUIScript");
                _fikaInstance = _fikaUiType != null ? AccessTools.Property(_fikaUiType, "Instance") : null;
                _fikaPanel = _fikaUiType != null ? AccessTools.Field(_fikaUiType, "_userInterface") : null;
            }
            if (_fikaInstance == null || _fikaPanel == null)
                return null;
            object script = _fikaInstance.GetValue(null);
            return script == null ? null : _fikaPanel.GetValue(script) as GameObject;
        }

        /// <summary>Each frame at the menus: Fika's banner and online players kept off in zombies mode, back in normal.</summary>
        public static void HideFikaMenu()
        {
            try
            {
                // At the zombies menus, and in a zombies match (its Esc menu: user, 2026-11-10)
                bool z = ZombiesMapSelect.Zombies && ZombiesPlugin.AtMenus || ZombiesMatch.Active;
                GameObject banner = _menu != null ? _menu._alphaWarningGameObject : null;
                if (banner != null)
                {
                    if (z && banner.activeSelf)
                    {
                        if (!_bannerHidden)
                            _bannerWas = true;
                        _bannerHidden = true;
                        banner.SetActive(false);
                    }
                    else if (!z && _bannerHidden)
                    {
                        _bannerHidden = false;
                        banner.SetActive(_bannerWas);
                    }
                }
                GameObject online = FikaOnlinePlayers();
                if (online != null)
                {
                    if (z && online.activeSelf)
                    {
                        if (!_fikaHidden)
                            _fikaWas = true;
                        _fikaHidden = true;
                        online.SetActive(false);
                    }
                    else if (!z && _fikaHidden)
                    {
                        _fikaHidden = false;
                        online.SetActive(_fikaWas);
                    }
                }
            }
            catch (Exception e)
            {
                if (!_fikaWarned)
                    ZombiesPlugin.Log.LogWarning($"Main menu: Fika's panels couldn't be hidden in zombies mode ({e.Message})");
                _fikaWarned = true;
            }
        }

        private static bool _fikaWarned;

        private static void ApplyMenu(MenuScreen menu)
        {
            bool z = ZombiesMapSelect.Zombies;
            HideFikaMenu();
            foreach (Component c in new Component[] { menu._tradeButton, menu._hideoutButton, menu._toggleGameModeButton })
                if (c != null)
                    c.gameObject.SetActive(!z);
            if (menu._playButton != null)
            {
                if (_playKey == null)
                    _playKey = menu._playButton.HeaderText;
                if (z)
                {
                    TMP_Text shown = menu._playButton.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
                    menu._playButton.SetRawText("PLAY ZOMBIES", shown != null ? Mathf.RoundToInt(shown.fontSize) : 36);
                }
                else if (!string.IsNullOrEmpty(_playKey))
                    menu._playButton.SetHeaderText(_playKey);
                RedText.On(menu._playButton.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(), z);
            }
            // The bottom task bar: no hideout, traders or flea market in zombies mode
            try
            {
                var bar = UnityEngine.Object.FindObjectOfType<MenuTaskBar>();
                if (bar?._toggleButtons != null)
                    foreach (var kv in bar._toggleButtons)
                        if (HiddenTabs.Contains(kv.Key) && kv.Value != null)
                            kv.Value.gameObject.SetActive(!z);
                // The survey button isn't one of the menu types: found by name
                if (bar != null)
                    foreach (Transform t in bar.GetComponentsInChildren<Transform>(true))
                        if (t.name.IndexOf("survey", StringComparison.OrdinalIgnoreCase) >= 0 && t.parent != null && t.GetComponentInParent<MenuTaskBar>() == bar)
                        {
                            t.gameObject.SetActive(!z);
                            break;
                        }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Main menu: the task bar buttons couldn't be hidden ({e.Message})");
            }
            // EFT's logo on the main menu is part of the 3D menu scene (EnvironmentUIRoot): hidden in zombies mode, the
            // zombies logo in its place on the menu
            try
            {
                var env = UnityEngine.Object.FindObjectOfType<EnvironmentUIRoot>();
                if (env != null)
                {
                    if (z)
                    {
                        if (env._pveLogo != null)
                            env._pveLogo.SetActive(false);
                        if (env._regularLogo != null)
                            env._regularLogo.SetActive(false);
                    }
                    else
                        env.SetSessionMode(EnvironmentSession(menu));
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Main menu: EFT's logo couldn't be swapped ({e.Message})");
            }
            // The zombies logo stays on the zombies menu, not in the lobby (user, 2026-11-10)
            MenuLogo(menu, z && !_feLobby && !InRaid);
            // MATCH REPORT and STATISTICS are the zombies menu's own buttons now
            ReportButton(menu, false);
            if (_feOn)
                HideEftButtons();
        }

        private const string ReportButtonName = "KennerZombies MatchReport", StatsButtonName = "KennerZombies Statistics";

        // Zombies mode: MATCH REPORT (a copy of EFT's Trading button, in its slot under CHARACTER, KP icon) opens the last
        // match's after action report again; STATISTICS (a copy of the Hideout button, in its slot) opens the lifetime
        // statistics page. Both EFT buttons are hidden in zombies mode (the user, 2026-11-08)
        private static void ReportButton(MenuScreen menu, bool zombies)
        {
            bool show = zombies && !InRaid;
            MenuCopy(menu._tradeButton, ReportButtonName, "MATCH REPORT", "kp_icon.png", ZombiesReport.ShowLast, show && ZombiesReport.HasSaved);
            MenuCopy(menu._hideoutButton, StatsButtonName, "STATISTICS", "stats_icon.png", ZombiesStatsScreen.Show, show);
        }

        private static void MenuCopy(DefaultUIButton from, string name, string text, string icon, UnityEngine.Events.UnityAction click, bool show)
        {
            try
            {
                if (from == null)
                    return;
                Transform parent = from.transform.parent;
                Transform have = parent.Find(name);
                DefaultUIButton button = have != null ? have.GetComponent<DefaultUIButton>() : null;
                if (button == null)
                {
                    if (!show)
                        return;
                    GameObject copy = UnityEngine.Object.Instantiate(from.gameObject, parent, false);
                    copy.name = name;
                    copy.transform.SetSiblingIndex(from.transform.GetSiblingIndex() + 1);
                    button = copy.GetComponent<DefaultUIButton>();
                    button.OnClick.RemoveAllListeners();
                    button.OnClick.AddListener(click);
                    Sprite sprite = IconSprite(icon);
                    if (sprite != null)
                        button.SetIcon(sprite, sprite);
                }
                button.gameObject.SetActive(show);
                if (show)
                {
                    TMP_Text shown = button.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
                    button.SetRawText(text, shown != null ? Mathf.RoundToInt(shown.fontSize) : 36);
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Main menu: the {text} button couldn't be made ({e.Message})");
            }
        }

        private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();

        private static Sprite IconSprite(string file)
        {
            if (Icons.TryGetValue(file, out Sprite s))
                return s;
            string path = System.IO.Path.Combine(ZombiesPlugin.Dir, "hud", file);
            if (System.IO.File.Exists(path))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path)))
                    s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            Icons[file] = s;
            return s;
        }

        private static ESessionMode EnvironmentSession(MenuScreen menu)
        {
            object mode = Traverse.Create(menu).Field("_sessionMode").GetValue();
            return mode is ESessionMode m ? m : ESessionMode.Pve;
        }

        private const string LogoName = "KennerZombies MenuLogo";

        // Hideout, Traders, Flea Market, Builds, Handbook, Messenger, News: off the task bar in zombies mode
        private static readonly HashSet<EMenuType> HiddenTabs = new HashSet<EMenuType>
            { EMenuType.Hideout, EMenuType.Trade, EMenuType.RagFair, EMenuType.EditBuild, EMenuType.Handbook, EMenuType.Chat, EMenuType.NewsHub };

        /// <summary>the original's red for the zombies mode's words on the menus.</summary>
        public static readonly Color Red = new Color(0.78f, 0.08f, 0.06f);

        // The zombies logo (hud/zm_logo.png) at the top of the main menu, where EFT's logo stands
        private static void MenuLogo(MenuScreen menu, bool show)
        {
            Transform existing = menu.transform.Find(LogoName);
            if (existing == null && show)
            {
                string file = System.IO.Path.Combine(ZombiesPlugin.Dir, "hud", "zm_logo.png");
                if (!System.IO.File.Exists(file))
                    return;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "zm_menu_logo" };
                tex.LoadImage(System.IO.File.ReadAllBytes(file));
                var go = new GameObject(LogoName, typeof(RectTransform));
                go.transform.SetParent(menu.transform, false);
                Image img = go.AddComponent<Image>();
                img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                img.preserveAspect = true;
                img.raycastTarget = false;
                (go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>()).ignoreLayout = true;
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(-60f, -70f);
                rt.sizeDelta = new Vector2(1050f, 340f);
                existing = go.transform;
            }
            if (existing != null)
                existing.gameObject.SetActive(show);
        }

        // A copy of the game's own dropdown, top right of the main menu, made once
        private static DropDownBox Dropdown(MenuScreen screen)
        {
            Transform existing = screen.transform.Find(DropdownName);
            if (existing != null)
                return existing.GetComponent<DropDownBox>();
            DropDownBox template = Resources.FindObjectsOfTypeAll<DropDownBox>().FirstOrDefault(d => d != null && !d.name.StartsWith("KennerZombies"));
            if (template == null)
            {
                ZombiesPlugin.Log.LogWarning("Main menu: no game dropdown found to copy; the Normal / Zombies switch is missing");
                return null;
            }
            DropDownBox dropdown = UnityEngine.Object.Instantiate(template, screen.transform, false);
            dropdown.name = DropdownName;
            (dropdown.GetComponent<LayoutElement>() ?? dropdown.gameObject.AddComponent<LayoutElement>()).ignoreLayout = true;
            var rt = (RectTransform)dropdown.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.one;
            rt.anchoredPosition = new Vector2(-60f, -110f);
            rt.sizeDelta = new Vector2(220f, rt.sizeDelta.y);
            dropdown.gameObject.SetActive(true);
            dropdown.Show(new[] { "NORMAL", "ZOMBIES" });
            if (OverlayField != null && OverlayField.GetValue(dropdown) == null)
                OverlayField.SetValue(dropdown, screen.transform);
            dropdown.UpdateValue(ZombiesMapSelect.Zombies ? 1 : 0, false);
            dropdown.OnValueChanged.Subscribe(i => SetMode(i == 1));
            MakeLabel(screen, rt);
            return dropdown;
        }

        private static void MakeLabel(MenuScreen screen, RectTransform dropdown)
        {
            TMP_Text font = screen.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault();
            if (font == null)
                return;
            var go = new GameObject(LabelName, typeof(RectTransform));
            go.transform.SetParent(screen.transform, false);
            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.font = font.font;
            label.fontSharedMaterial = font.fontSharedMaterial;
            label.color = Color.white;
            label.raycastTarget = false;
            (label.GetComponent<LayoutElement>() ?? label.gameObject.AddComponent<LayoutElement>()).ignoreLayout = true;
            label.text = "GAME MODE";
            RedText.On(label, true);
            label.fontSize = 16f;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            var rt = label.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.one;
            rt.sizeDelta = new Vector2(dropdown.sizeDelta.x, 24f);
            rt.anchoredPosition = dropdown.anchoredPosition + new Vector2(0f, 26f);
            label.gameObject.SetActive(true);
        }

        // ---------- The side select: only the PMC, in the middle ----------

        private static readonly Dictionary<Transform, Vector3> MovedFrom = new Dictionary<Transform, Vector3>();

        private static void SideShown(MatchMakerSideSelectionScreen __instance)
        {
            SideScreen = __instance;
            try
            {
                bool z = ZombiesMapSelect.Zombies;
                // "PVE ZONE" under the title says ZOMBIES MODE, in red
                if (Traverse.Create(__instance).Field("_subCaption").GetValue() is TMP_Text sub)
                    ZombiesMapSelect.SetCaption(sub);
                foreach (Component c in new Component[] { __instance._savagesButton, __instance._savagesBigButton, __instance._savageModelView,
                             __instance._randomButton, __instance._savageBlocker })
                    if (c != null)
                        c.gameObject.SetActive(!z);
                // Back where they were first (normal mode, or a fresh centring)
                foreach (var kv in MovedFrom)
                    if (kv.Key != null)
                    {
                        kv.Key.position = kv.Value;
                        if (kv.Key.GetComponent<LayoutElement>() is LayoutElement le)
                            le.ignoreLayout = false;
                    }
                MovedFrom.Clear();
                if (!z)
                    return;
                if (__instance._pmcButton?.SpawnedObject != null)
                    __instance._pmcButton.SpawnedObject.IsToggled = true;
                try
                {
                    var env = UnityEngine.Object.FindObjectOfType<EnvironmentUIRoot>();
                    env?._pveLogo?.SetActive(false);
                    env?._regularLogo?.SetActive(false);
                }
                catch
                {
                }
                // The PMC's whole column (its 3D model, PMC label, YOUR MAIN CHARACTER) slid so the model is in the middle:
                // the lowest parent holding the PMC's model and label but not the scav's, taken out of the screen's
                // layout so the layout can't put it back (moving the parts on their own left the model where it was)
                Transform pmcView = __instance._pmcModelView != null ? __instance._pmcModelView.transform : null;
                Transform label = __instance._pmcBigButton != null ? __instance._pmcBigButton.transform : __instance._pmcButton?.transform;
                Transform scav = __instance._savageModelView != null ? __instance._savageModelView.transform : null;
                if (pmcView == null)
                    return;
                Transform column = null;
                for (Transform t = pmcView; t != null && t != __instance.transform; t = t.parent)
                {
                    if (scav != null && scav.IsChildOf(t))
                        break;
                    if (label == null || label.IsChildOf(t))
                        column = t;
                }
                var moved = column != null ? new[] { column } : new[] { pmcView, label, __instance._pmcButton?.transform }.Where(t => t != null).ToArray();
                Vector3[] corners = new Vector3[4];
                RectTransform measure = pmcView as RectTransform ?? label as RectTransform;
                if (measure == null)
                    return;
                measure.GetWorldCorners(corners);
                Canvas canvas = pmcView.GetComponentInParent<Canvas>();
                RectTransform root = canvas != null ? (RectTransform)canvas.rootCanvas.transform : null;
                float middle = Screen.width / 2f;
                if (root != null)
                {
                    Vector3[] rc = new Vector3[4];
                    root.GetWorldCorners(rc);
                    middle = (rc[0].x + rc[2].x) / 2f;
                }
                float shift = middle - (corners[0].x + corners[2].x) / 2f;
                foreach (Transform t in moved)
                {
                    (t.GetComponent<LayoutElement>() ?? t.gameObject.AddComponent<LayoutElement>()).ignoreLayout = true;
                    MovedFrom[t] = t.position;
                    t.position += new Vector3(shift, 0f, 0f);
                }
                ZombiesPlugin.Log.LogInfo($"Side select: PMC {(column != null ? "column '" + column.name + "'" : moved.Length + " parts")} slid {shift:0} to the middle");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Side select (zombies mode): {e}");
            }
        }
    }
}
