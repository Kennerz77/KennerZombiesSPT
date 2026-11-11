using System.IO;
using System.Linq;
using BepInEx.Configuration;
using EFT.UI;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace KennerZombies
{
    /// <summary>
    /// A moving background behind the main menu in zombies mode (user, 2026-11-09): a video looping silently, full
    /// screen and cropped to fill it, under every menu button (first child of the menu's own canvas, over EFT's 3D
    /// scene). It fades in with zombies mode and pauses when hidden. The video ships with the mod:
    /// plugins\Kennerz_ZM_Core\menu\zm_menu_bg.webm (from data\menu; BSG's own Halloween 2026 menu loop, found in a
    /// Wallpaper Engine wallpaper, "Parallax Halloween | Escape from Tarkov"); none there = EFT's own menu as before.
    /// </summary>
    internal class ZombiesMenuBackground : MonoBehaviour
    {
        private const string Name = "KennerZombies MenuBackground";
        private const float FadeSeconds = 1.2f;
        private static ConfigEntry<bool> _on;
        private static ConfigEntry<float> _bright;

        private VideoPlayer _video;
        private RenderTexture _rt;
        private RawImage _image;
        private string _file;
        private bool _looked, _ready, _failed;
        private float _alpha;

        public static void Bind(ConfigFile config, string section)
        {
            _on = config.Bind(section, "Menu video background", true,
                "Zombies mode's main menu plays its own video background behind the buttons (it fades over your chosen EFT background going in, and back out to it).");
            _bright = config.Bind(section, "Menu video brightness", 0.85f, new ConfigDescription(
                "How bright the menu video is (lower: the buttons stand out more).", new AcceptableValueRange<float>(0.2f, 1f)));
        }

        private string FindFile()
        {
            string dir = Path.Combine(ZombiesPlugin.Dir, "menu");
            foreach (string ext in new[] { ".webm", ".mp4" })
            {
                string f = Path.Combine(dir, "zm_menu_bg" + ext);
                if (File.Exists(f))
                    return f;
            }
            return null;
        }

        // The screen it shows behind now: the main menu, the character select or the map select (user, 2026-11-09)
        private static Component CurrentScreen()
        {
            try
            {
                if (!(Comfort.Common.Singleton<EFT.UI.Screens.IScreenManager>.Instance?.CurrentScreenType is EFT.UI.Screens.EEftScreenType t))
                    return null;
                switch (t)
                {
                    case EFT.UI.Screens.EEftScreenType.MainMenu: return ZombiesMainMenu.Menu;
                    case EFT.UI.Screens.EEftScreenType.SelectRaidSide: return ZombiesMainMenu.SideScreen;
                    case EFT.UI.Screens.EEftScreenType.SelectLocation: return ZombiesMapSelect.Screen;
                    case EFT.UI.Screens.EEftScreenType.TimeHasCome: return ZombiesMapSelect.DeployScreen;
                    // The zombies menu's GAME SETTINGS and CUSTOMISE: the same video behind them (user, 2026-11-10)
                    case EFT.UI.Screens.EEftScreenType.Settings: return Find(ref _settings);
                    case EFT.UI.Screens.EEftScreenType.Inventory: return Find(ref _inventory);
                    default: return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static EFT.UI.Settings.SettingsScreen _settings;
        private static EFT.UI.InventoryScreen _inventory;

        private static T Find<T>(ref T cached) where T : Component
        {
            if (cached == null || !cached.gameObject.activeInHierarchy)
                cached = UnityEngine.Object.FindObjectsOfType<T>().FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy);
            return cached;
        }

        private void Update()
        {
            if (!_looked)
            {
                _looked = true;
                _file = FindFile();
                if (_file != null)
                    ZombiesPlugin.Log.LogInfo($"Zombies menu: video background {Path.GetFileName(_file)}");
            }
            ZombiesMainMenu.HideFikaMenu();
            if (_file == null || _failed)
                return;
            Component screen = CurrentScreen();
            bool on = _on?.Value != false && ZombiesMapSelect.Zombies && !ZombiesMatch.Active;
            bool menuWant = on && ZombiesPlugin.AtMenus && screen != null && screen.gameObject.activeInHierarchy;
            // After a zombies match: from the raid's end, under the after action report and through EFT's loading back
            // to the main menu, the video covers EFT's grey after-raid backdrop (user, 2026-11-09). Held on our own
            // top canvas (IMGUI, the report, still draws over it) until the main menu's own copy takes over
            bool waiting = ZombiesReport.Waiting;
            if (_wasWaiting && !waiting)
                _toMenuUntil = Time.unscaledTime + 30f;
            _wasWaiting = waiting;
            if (menuWant && _onScreen != null && _onScreen != _overlay)
                _toMenuUntil = 0f;
            bool afterWant = on && !menuWant && !ZombiesPlugin.InRealRaid && (waiting || Time.unscaledTime < _toMenuUntil);
            bool want = menuWant || afterWant;
            if (menuWant && (_image == null || _onScreen != screen))
                Make(screen);
            else if (afterWant && (_image == null || _onScreen != Overlay()))
                Make(Overlay());
            if (_image == null)
                return;
            // A cross-fade over EFT's own background, whichever the player picked in its settings (it stays underneath
            // untouched): in with zombies mode, back out to theirs when they switch to normal (user, 2026-11-09)
            _alpha = Mathf.MoveTowards(_alpha, want ? 1f : 0f, Time.unscaledDeltaTime / FadeSeconds);
            float b = _bright?.Value ?? 0.85f;
            _image.color = new Color(b, b, b, _alpha);
            _image.enabled = _alpha > 0.001f;
            Cover();
            if (_video != null && _ready)
            {
                if (want && !_video.isPlaying)
                    _video.Play();
                else if (!want && _alpha <= 0f && _video.isPlaying)
                    _video.Pause();
            }
        }

        private Component _onScreen;
        private Canvas _overlay;
        private bool _wasWaiting;
        private float _toMenuUntil;

        // Our own screen-wide canvas over EFT's UI (its spinner included), for the after-match stretch
        private Component Overlay()
        {
            if (_overlay != null)
                return _overlay;
            var go = new GameObject("KennerZombies MenuVideoCanvas", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            _overlay = go.AddComponent<Canvas>();
            _overlay.renderMode = RenderMode.ScreenSpaceOverlay;
            _overlay.sortingOrder = 32000;
            return _overlay;
        }

        // Behind every button of the screen: the first thing its canvas draws (over EFT's 3D menu scene). Moving between
        // the three screens it goes along, still playing and without a fade
        private void Make(Component screen)
        {
            Canvas canvas = screen.GetComponentInParent<Canvas>();
            canvas = canvas != null ? canvas.rootCanvas : null;
            if (canvas == null)
                return;
            _onScreen = screen;
            bool showing = _image != null && _alpha > 0f;
            Transform old = canvas.transform.Find(Name);
            var go = old != null ? old.gameObject : _image != null ? _image.gameObject : new GameObject(Name, typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            go.transform.SetAsFirstSibling();
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            (go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>()).ignoreLayout = true;
            _image = go.GetComponent<RawImage>() ?? go.AddComponent<RawImage>();
            _image.raycastTarget = false;
            if (!showing)
            {
                _image.color = new Color(1f, 1f, 1f, 0f);
                _alpha = 0f;
            }
            if (_video == null)
            {
                var host = new GameObject("KennerZombies MenuVideo");
                host.transform.SetParent(transform, false);
                _video = host.AddComponent<VideoPlayer>();
                _video.playOnAwake = false;
                _video.source = VideoSource.Url;
                _video.url = "file:///" + _file.Replace('\\', '/');
                _video.isLooping = true;
                _video.skipOnDrop = true;
                _video.audioOutputMode = VideoAudioOutputMode.None;
                _video.renderMode = VideoRenderMode.RenderTexture;
                _video.errorReceived += (vp, msg) =>
                {
                    _failed = true;
                    ZombiesPlugin.Log.LogWarning($"Zombies menu: the video background won't play ({msg}); EFT's own menu stays");
                    if (_image != null)
                        _image.enabled = false;
                };
                _video.prepareCompleted += vp =>
                {
                    int w = (int)Mathf.Max(16, vp.width), h = (int)Mathf.Max(16, vp.height);
                    // At most 2560 wide: the menu doesn't need the source's full 3440
                    float k = Mathf.Min(1f, 2560f / w);
                    _rt = new RenderTexture(Mathf.RoundToInt(w * k), Mathf.RoundToInt(h * k), 0);
                    _rt.Create();
                    vp.targetTexture = _rt;
                    if (_image != null)
                        _image.texture = _rt;
                    _ready = true;
                    ZombiesPlugin.Log.LogInfo($"Zombies menu: video background ready ({w}x{h}, {vp.length:0.0} s)");
                };
                _video.Prepare();
            }
            else if (_rt != null)
                _image.texture = _rt;
        }

        // Cropped to fill the screen whatever its shape (the source is 21:9)
        private void Cover()
        {
            if (_rt == null || _image == null)
                return;
            float screen = (float)Screen.width / Mathf.Max(1, Screen.height);
            float video = (float)_rt.width / Mathf.Max(1, _rt.height);
            _image.uvRect = video > screen
                ? new Rect((1f - screen / video) / 2f, 0f, screen / video, 1f)
                : new Rect(0f, (1f - video / screen) / 2f, 1f, video / screen);
        }
    }
}
