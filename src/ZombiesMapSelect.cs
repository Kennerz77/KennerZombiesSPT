using System;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.UI;
using EFT.UI.Matchmaker;
using HarmonyLib;
using JsonType;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KennerZombies
{
    /// <summary>
    /// The map select screen's game mode: a dropdown at its top right (a copy of the game's own dropdown) with NORMAL and
    /// ZOMBIES. Normal is the game as it is, without the zombies maps. Zombies shows only the zombies maps (ZombiesMaps), each
    /// where its MapSelectSpot says (Stronghold: where Customs is); every other map, the routes between them and CustomMapLoader's page buttons are
    /// hidden. Only the map select screen is touched; picking Stronghold is what makes a raid a zombies match.
    /// </summary>
    internal static class ZombiesMapSelect
    {
        private const string DropdownName = "KennerZombies Mode";
        private const string LabelName = "KennerZombies Mode Label";
        private static readonly string[] PagerNames = { "MapPagePrevious", "MapPageNext", "MapPageTitle" };

        private static readonly FieldInfo SessionField = AccessTools.Field(typeof(MatchMakerSelectionLocationScreen), "_session");
        private static readonly FieldInfo RaidSettingsField = AccessTools.Field(typeof(MatchMakerSelectionLocationScreen), "_raidSettings");
        private static readonly FieldInfo ScreenControllerField = AccessTools.Field(typeof(MatchMakerSelectionLocationScreen), "ScreenController");
        private static readonly FieldInfo OverlayField = AccessTools.Field(typeof(DropDownBox), "_overlayLayer");

        public static bool Zombies { get; private set; }

        public static void Patch(Harmony harmony)
        {
            // After CustomMapLoader's own patches on the same methods (its map pages), so ours has the last word
            harmony.Patch(AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), "DisplayLocation"),
                postfix: new HarmonyMethod(typeof(ZombiesMapSelect), nameof(DisplayPostfix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(MatchMakerSelectionLocationScreen), "ShowInternal"),
                prefix: new HarmonyMethod(typeof(ZombiesMapSelect), nameof(ShowPrefix)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(typeof(ZombiesMapSelect), nameof(ShowFinalizer)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(LocationInfoPanel), nameof(LocationInfoPanel.Set)),
                postfix: new HarmonyMethod(typeof(ZombiesMapSelect), nameof(InfoPostfix)));
            // The ready screen says ZOMBIES MODE too
            harmony.Patch(AccessTools.Method(typeof(MatchMakerAcceptScreen), nameof(MatchMakerAcceptScreen.Show),
                    new[] { typeof(MatchMakerAcceptScreen.MatchmakerAcceptScreenController) }),
                postfix: new HarmonyMethod(typeof(ZombiesMapSelect), nameof(ReadyPostfix)));
            // And the deploying screen ("starting local game")
            harmony.Patch(AccessTools.Method(typeof(MatchmakerTimeHasCome), nameof(MatchmakerTimeHasCome.Show),
                    new[] { typeof(MatchmakerTimeHasCome.TimeHasComeScreenController) }),
                postfix: new HarmonyMethod(typeof(ZombiesMapSelect), nameof(DeployPostfix)));
            // The mode is chosen on the main menu (ZombiesMainMenu) and kept until it's switched back there
            // A zombies map goes from the map select straight to the ready screen: no raid settings, no insurance
            MethodInfo raidSettingsStep = AccessTools.Method(typeof(MainMenuShowOperation), "method_50");
            if (raidSettingsStep != null)
                harmony.Patch(raidSettingsStep, prefix: new HarmonyMethod(typeof(ZombiesMapSelect), nameof(SkipToReady)));
            else
                ZombiesPlugin.Log.LogWarning("Map select: the raid settings step wasn't found; zombies maps show the settings and insurance screens");
        }

        private const string ZombiesCaption = "ZOMBIES MODE";
        private static readonly System.Collections.Generic.Dictionary<TMP_Text, string> Captions = new System.Collections.Generic.Dictionary<TMP_Text, string>();

        /// <summary>The screen's small header under its title ("PVE ZONE"): "ZOMBIES MODE" in zombies mode.</summary>
        internal static void SetCaption(TMP_Text caption)
        {
            if (caption == null)
                return;
            if (caption.text != ZombiesCaption)
                Captions[caption] = caption.text;
            if (Zombies)
                caption.text = ZombiesCaption;
            else if (Captions.TryGetValue(caption, out string normal))
                caption.text = normal;
            // "ZOMBIES MODE" in the mode's red
            RedText.On(caption, true);
        }

        private static void ReadyPostfix(MatchMakerAcceptScreen __instance)
        {
            try
            {
                SetCaption(__instance._subCaption);
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Map select (ready screen header): {ex}");
            }
        }

        /// <summary>The deploying screen, once shown (the zombies menu video sits behind it too).</summary>
        public static MatchmakerTimeHasCome DeployScreen { get; private set; }

        private static void DeployPostfix(MatchmakerTimeHasCome __instance)
        {
            DeployScreen = __instance;
            try
            {
                SetCaption(__instance._subCaption);
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Map select (deploying screen header): {ex}");
            }
        }

        /// <summary>The mode, from the main menu's switch (the only place it changes).</summary>
        public static void SetZombies(bool zombies)
        {
            if (zombies == Zombies)
                return;
            Zombies = zombies;
            ZombiesMenu.ModeChanged(zombies);
        }

        private static readonly FieldInfo MenuRaidSettings = AccessTools.Field(typeof(MainMenuShowOperation), "raidSettings_0");
        private static readonly MethodInfo ReadyStep = AccessTools.Method(typeof(MainMenuShowOperation), "method_52");

        // method_50 shows the raid settings screen (then insurance), method_52 the ready screen
        private static bool SkipToReady(MainMenuShowOperation __instance)
        {
            try
            {
                var raid = MenuRaidSettings?.GetValue(__instance) as RaidSettings;
                if (raid?.SelectedLocation == null || !ZombiesMaps.Is(raid.SelectedLocation.Id) || ReadyStep == null)
                    return true;
                ReadyStep.Invoke(__instance, null);
                return false;
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Map select (straight to ready): {ex}");
                return true;
            }
        }

        private static readonly Color BannerTint = Color.white; // untinted: the user's own picture (2026-11-07)

        // The info panel of a zombies map: no raid time, round based, 1-4 players, and the picture of the map whose
        // place it takes (Stronghold: Customs') tinted red, instead of the default (Factory) picture
        private static void InfoPostfix(LocationInfoPanel __instance, LocationSettings.Location location, IEftSession session)
        {
            try
            {
                MapEditor.MapFile map = ZombiesMaps.Get(location?.Id);
                if (__instance._banner != null)
                    __instance._banner.color = map != null ? BannerTint : Color.white;
                Backdrop(__instance, map != null);
                if (map == null)
                    return;
                __instance._playTime.text = "EXFIL";
                __instance._difficulty.text = "ROUND BASED";
                __instance._players.text = "1-4";
                LocationSettings.Location spot = string.IsNullOrEmpty(map.MapSelectSpot) ? null
                    : session?.LocationSettings?.locations?.Values.FirstOrDefault(l => l.Id == map.MapSelectSpot);
                Sprite own = Banner(map.Map);
                if (own != null && __instance._banner != null)
                {
                    __instance._banner.sprite = own;
                    __instance._banner.preserveAspect = true;
                }
                else if (location.Banners.Length == 0 && spot != null && spot.Banners.Length != 0)
                    spot.Banners.First().GetAndAssignSprite(session, __instance._banner, true, default);
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Map select (info panel): {ex}");
            }
        }

        // A soft dark panel behind the map's info (picture, name, description, the three icons) on a zombies map: the
        // text was hard to read over the moving menu video (user, 2026-11-09). A real blur needs a screen-grab shader
        // EFT's UI doesn't have; a dark backing fading out at its edges does the job. Hidden on normal maps.
        private const string BackdropName = "KennerZombies InfoBackdrop";
        private static Sprite _soft;

        private static void Backdrop(LocationInfoPanel panel, bool show)
        {
            Transform have = panel.transform.Find(BackdropName);
            if (have == null)
            {
                if (!show)
                    return;
                var go = new GameObject(BackdropName, typeof(RectTransform));
                go.transform.SetParent(panel.transform, false);
                go.AddComponent<LayoutElement>().ignoreLayout = true;
                var rt = (RectTransform)go.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(-40f, -30f);
                rt.offsetMax = new Vector2(40f, 30f);
                Image img = go.AddComponent<Image>();
                img.sprite = SoftSprite();
                img.type = Image.Type.Sliced;
                img.color = new Color(0f, 0f, 0f, 0.72f);
                img.raycastTarget = false;
                have = go.transform;
            }
            have.SetAsFirstSibling();
            have.gameObject.SetActive(show);
        }

        // 64x64, solid in the middle, fading to nothing over its outer 24 px (9-sliced, so the fade stays 24 px at any size)
        private static Sprite SoftSprite()
        {
            if (_soft != null)
                return _soft;
            const int n = 64, edge = 24;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Min(Mathf.Min(x, n - 1 - x), Mathf.Min(y, n - 1 - y));
                    float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / edge));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply();
            _soft = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(edge, edge, edge, edge));
            return _soft;
        }

        // <map>_banner.png beside the layout (Stronghold: the factory from the user's strongholdmap.webp)
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> Banners = new System.Collections.Generic.Dictionary<string, Sprite>();

        internal static Sprite Banner(string map)
        {
            if (Banners.TryGetValue(map, out Sprite known))
                return known;
            Sprite sprite = null;
            try
            {
                string layout = MapEditor.FileFor(map);
                string file = string.IsNullOrEmpty(layout) ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(layout), map + "_banner.png");
                if (file != null && System.IO.File.Exists(file))
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = map + "_banner" };
                    tex.LoadImage(System.IO.File.ReadAllBytes(file));
                    sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    sprite.name = map + "_banner";
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Map select: {map}'s picture couldn't be read: {e.Message}");
            }
            Banners[map] = sprite;
            return sprite;
        }

        private static void DisplayPostfix(LocationSettings.Location location, ref bool __result)
        {
            string id = location?.Id;
            __result = Zombies ? ZombiesMaps.Is(id) : __result && !ZombiesMaps.Is(id);
            if (__result)
                _shown.Add(id);
        }

        private static readonly System.Collections.Generic.List<string> _shown = new System.Collections.Generic.List<string>();

        // A zombies map can take a normal map's place (Stronghold: Customs')
        private static void ShowPrefix(MatchMakerSelectionLocationScreen __instance)
        {
            _shown.Clear();
            try
            {
                // Normal mode never keeps a zombies map selected (left selected when backing out of zombies mode)
                var raid = (RaidSettings)RaidSettingsField.GetValue(__instance);
                if (!Zombies && raid?.SelectedLocation != null && ZombiesMaps.Is(raid.SelectedLocation.Id))
                    raid.SelectedLocation = null;
                var locations = ((IEftSession)SessionField.GetValue(__instance))?.LocationSettings?.locations;
                if (locations == null)
                    return;
                // The list is keyed by the maps' database ids (_Id), not their names: found by Id
                foreach (MapEditor.MapFile map in ZombiesMaps.All)
                {
                    if (string.IsNullOrEmpty(map.MapSelectSpot))
                        continue;
                    LocationSettings.Location zombies = locations.Values.FirstOrDefault(l => l.Id == map.Map);
                    LocationSettings.Location spot = locations.Values.FirstOrDefault(l => l.Id == map.MapSelectSpot);
                    if (zombies != null && spot != null)
                    {
                        zombies.IconX = spot.IconX;
                        zombies.IconY = spot.IconY;
                    }
                }
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Map select (Stronghold's place): {ex}");
            }
        }

        /// <summary>The map select screen last shown (the zombies party presses its NEXT for the leader).</summary>
        public static MatchMakerSelectionLocationScreen Screen { get; private set; }

        private static void ShowFinalizer(MatchMakerSelectionLocationScreen __instance)
        {
            Screen = __instance;
            try
            {
                bool transition = Traverse.Create(RaidSettingsField.GetValue(__instance)).Field("transitionType").GetValue<int>() > 0
                    && __instance._marathonCapture != null && __instance._marathonCapture.activeSelf;
                // The mode is switched on the main menu only: no switch here (an old one hidden)
                Transform old = __instance.transform.Find(DropdownName);
                if (old != null)
                    old.gameObject.SetActive(false);
                Transform label = __instance.transform.Find(LabelName);
                if (label != null)
                    label.gameObject.SetActive(false);
                ZombiesPlugin.Log.LogInfo($"Map select: {(Zombies ? "zombies" : "normal")} mode, {_shown.Count} maps shown{(Zombies ? ": " + string.Join(", ", _shown) : "")}; "
                    + $"{__instance._locationButtonsContainer.Cast<Transform>().Count(t => t.gameObject.activeSelf)} buttons on screen");
                ZombiesMenu.MapPicture(MapPicture(__instance));
                SetCaption(__instance._subCaption);
                // Only the route lines: the location buttons can sit under the same container (showing them again is the game's
                // own doing each time the screen shows)
                if (Zombies && !transition && __instance._locationPathsContainer != null)
                    foreach (LocationPath path in __instance._locationPathsContainer.GetComponentsInChildren<LocationPath>(true))
                        path.gameObject.SetActive(false);
                // CustomMapLoader's PREVIOUS / NEXT / page title: no pages in zombies mode (its finalizer shows them again in normal)
                if (Zombies)
                    foreach (Transform t in __instance.GetComponentsInChildren<Transform>(true))
                        if (PagerNames.Contains(t.name))
                            t.gameObject.SetActive(false);
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Map select (mode dropdown): {ex}");
            }
        }

        // The Tarkov map picture behind the location buttons (found as CustomMapLoader finds it: the first parent with an
        // Image and an AspectRatioFitter)
        private static Image MapPicture(MatchMakerSelectionLocationScreen screen)
        {
            for (Transform t = screen._locationButtonsContainer; t != null && t != screen.transform; t = t.parent)
                if (t.GetComponent<AspectRatioFitter>() != null && t.TryGetComponent(out Image image))
                    return image;
            return null;
        }

        // A copy of the game's own dropdown, made once per screen
        private static DropDownBox Dropdown(MatchMakerSelectionLocationScreen screen)
        {
            Transform existing = screen.transform.Find(DropdownName);
            if (existing != null)
                return existing.GetComponent<DropDownBox>();
            DropDownBox template = Resources.FindObjectsOfTypeAll<DropDownBox>().FirstOrDefault(d => d != null && d.name != DropdownName);
            if (template == null)
            {
                ZombiesPlugin.Log.LogWarning("Map select: no game dropdown found to copy; the Normal / Zombies switch is missing");
                return null;
            }
            DropDownBox dropdown = UnityEngine.Object.Instantiate(template, screen.transform, false);
            dropdown.name = DropdownName;
            // Placed by hand: never pushed around by (or pushing) a layout on the screen
            (dropdown.GetComponent<LayoutElement>() ?? dropdown.gameObject.AddComponent<LayoutElement>()).ignoreLayout = true;
            var rt = (RectTransform)dropdown.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.one;
            rt.anchoredPosition = new Vector2(-60f, -110f);
            rt.sizeDelta = new Vector2(220f, rt.sizeDelta.y);
            dropdown.gameObject.SetActive(true);
            dropdown.Show(new[] { "NORMAL", "ZOMBIES" });
            // The open list goes on the context menu layer; without one (no item screen yet) it stays on this screen
            if (OverlayField != null && OverlayField.GetValue(dropdown) == null)
                OverlayField.SetValue(dropdown, screen.transform);
            dropdown.UpdateValue(Zombies ? 1 : 0, false);
            dropdown.OnValueChanged.Subscribe(i => SetMode(screen, i == 1));
            MakeLabel(screen, rt);
            return dropdown;
        }

        // "GAME MODE" over the dropdown, in the screen's own caption font
        private static void MakeLabel(MatchMakerSelectionLocationScreen screen, RectTransform dropdown)
        {
            TextMeshProUGUI caption = screen.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault(t => t.name.IndexOf("caption", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? screen.GetComponentsInChildren<TextMeshProUGUI>(true).FirstOrDefault();
            if (caption == null)
                return;
            // A new text in the caption's font: a copy would bring the game's localisation along, which writes its own text back
            var go = new GameObject(LabelName, typeof(RectTransform));
            go.transform.SetParent(screen.transform, false);
            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.font = caption.font;
            label.fontSharedMaterial = caption.fontSharedMaterial;
            label.color = caption.color;
            label.raycastTarget = false;
            (label.GetComponent<LayoutElement>() ?? label.gameObject.AddComponent<LayoutElement>()).ignoreLayout = true;
            label.text = "GAME MODE";
            label.fontSize = 16f;
            label.alignment = TextAlignmentOptions.Left;
            label.enableWordWrapping = false;
            var rt = label.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.one;
            rt.sizeDelta = new Vector2(dropdown.sizeDelta.x, 24f);
            rt.anchoredPosition = dropdown.anchoredPosition + new Vector2(0f, 26f);
            label.gameObject.SetActive(true);
        }

        private static void SetMode(MatchMakerSelectionLocationScreen screen, bool zombies)
        {
            if (zombies == Zombies)
                return;
            Zombies = zombies;
            ZombiesMenu.ModeChanged(zombies);
            try
            {
                // A map the other mode doesn't show isn't kept selected
                var raid = (RaidSettings)RaidSettingsField.GetValue(screen);
                if (raid?.SelectedLocation != null && ZombiesMaps.Is(raid.SelectedLocation.Id) != zombies)
                    raid.SelectedLocation = null;
                // Shown again with the new mode, as CustomMapLoader turns its pages
                object controller = ScreenControllerField.GetValue(screen);
                AccessTools.Method(screen.GetType(), "Show", new[] { controller.GetType() }).Invoke(screen, new[] { controller });
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogError($"Map select (switching to {(zombies ? "zombies" : "normal")}): {ex}");
            }
        }
    }
}
