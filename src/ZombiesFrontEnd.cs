using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.UI;
using EFT.UI.Screens;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KennerZombies
{
    /// <summary>
    /// The zombies main menu and lobby (user's mock-ups, 2026-11-10: "Zombies main menu.png", "lobby menu.png").
    ///
    /// Normal mode: one ZOMBIES MODE button at the main menu's top right (where the NORMAL / ZOMBIES switch was).
    ///
    /// Zombies mode, main menu: EFT's own buttons, its mode switch and the bottom task bar are gone; the zombies logo
    /// stays. On the left: SOLO (EFT's map screen with only the zombies maps: pick one, PLAY SOLO or BACK), CREATE LOBBY,
    /// CUSTOMISE (the character screen), MATCH REPORT, STATISTICS, GAME SETTINGS (EFT's settings over the zombies video),
    /// ZOMBIES SETTINGS (the controls page, centred). EXIT ZOMBIES MODE at the bottom. On the right the SOCIAL list:
    /// IN GAME, ONLINE, OFFLINE; INVITE asks to make a lobby first; click a player for their statistics.
    ///
    /// The lobby (in a lobby): no logo; the map's card top left (picture, name, description, NO TIME / ROUND BASED /
    /// 1-4); CHANGE MAP (host only: the map list in its own window), ZOMBIES SETTINGS, GAME SETTINGS, START MATCH (the
    /// host's, lit once everyone else is ready; READY UP for the others). In the middle ZOMBIES LOBBY, the lobby type
    /// (the host switches PRIVATE / OPEN) and the players, host first, with a green / red light each: click one for INFO
    /// (their statistics) or, as the host, KICK. SOCIAL on the right. Esc asks before leaving (the host's leaving
    /// disbands the lobby).
    ///
    /// Everything is uGUI on our own canvases (the menus' look: ZombiesUi), over EFT's main menu, under the IMGUI pages
    /// (statistics, report, zombies settings). Pop-ups (invites, questions, the map list, a player's options) sit on a
    /// second canvas over everything at the menus, Esc closing them. The data and the server talk are ZombiesParty's.
    /// </summary>
    internal class ZombiesFrontEnd : MonoBehaviour
    {
        /// <summary>The zombies main menu or lobby is on screen.</summary>
        public static bool Showing { get; private set; }

        /// <summary>The lobby is on screen.</summary>
        public static bool InLobby { get; private set; }

        /// <summary>The zombies match's own Esc menu is on screen (LEAVE MATCH, settings, RESUME, social).</summary>
        public static bool InMatchMenu { get; private set; }

        /// <summary>Esc belongs to one of ours right now (a pop-up or an IMGUI page): EFT's menu mustn't close on it.</summary>
        public static bool BlockEsc => _modalUp || ZombiesControls.PageOpen || ZombiesStatsScreen.Open;

        private static bool _modalUp;

        private Canvas _main, _top;
        private CanvasScaler _mainScaler, _topScaler;
        private RectTransform _mainRoot, _topRoot;
        private CanvasGroup _menuGroup, _lobbyGroup, _normalGroup, _vignetteGroup, _matchGroup;
        private RectTransform _menu, _lobby, _normal, _match;
        private bool _built, _buildFailed, _wasVisible;
        private float _menuA, _lobbyA, _normalA, _matchA, _shownAt;
        private SocialPanel _matchSocial;
        private TextMeshProUGUI _pausedText;
        private string _inviteHeard;

        // Main menu
        private ZButton _create, _report;
        private SocialPanel _menuSocial, _lobbySocial;

        // Lobby
        private MapCard _lobbyCard;
        private ZButton _changeMap, _startButton, _typeButton, _fikaButton, _backButton;
        private TextMeshProUGUI _typeText, _fikaText, _footer, _countdown;
        private RectTransform _deploy;
        private CanvasGroup _deployGroup;
        private MapCard _deployCard;
        private float _deployA;
        private readonly List<MemberRow> _rows = new List<MemberRow>();

        // Pop-ups
        private RectTransform _modal;
        private CanvasGroup _modalGroup;
        private string _modalKind;
        private Action _modalEsc;
        private string _inviteShown;

        // Toast, cover, banner
        private RectTransform _toast, _cover, _banner;
        private TextMeshProUGUI _toastText, _bannerText, _coverSub;
        private CanvasGroup _toastGroup, _coverGroup, _bannerGroup;
        private float _toastAt = -100f;
        private const float ToastSeconds = 3.6f;

        // Sounds as the lobby changes
        private string _partyId;
        private readonly HashSet<string> _members = new HashSet<string>();
        private bool _couldStart;

        private void Update()
        {
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime > _nextError)
                {
                    _nextError = Time.unscaledTime + 10f;
                    ZombiesPlugin.Log.LogError($"Zombies menus: {e}");
                }
            }
        }

        private float _nextError;

        private static EEftScreenType? Screen
        {
            get
            {
                try
                {
                    return Singleton<IScreenManager>.Instance?.CurrentScreenType is EEftScreenType t ? t : (EEftScreenType?)null;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>An IMGUI page is up over the menus (they take the clicks and Esc).</summary>
        private static bool ImguiPage => ZombiesStatsScreen.Open || ZombiesControls.PageOpen || ZombiesReport.OnScreen;

        private void Frame()
        {
            if (ZombiesNet.Headless)
                return;
            MenuScreen menu = ZombiesMainMenu.Menu;
            bool atMenus = ZombiesPlugin.AtMenus && !ZombiesMatch.Active;
            bool onMain = atMenus && !ZombiesPlugin.InRealRaid && Screen == EEftScreenType.MainMenu && menu != null
                          && menu.isActiveAndEnabled && !ZombiesReport.Waiting && !ZombiesGameOver.Black;
            bool zombies = ZombiesMapSelect.Zombies;
            bool show = onMain && zombies;
            bool lobby = show && ZombiesParty.Mine != null;
            bool normalButton = onMain && !zombies;
            // In a zombies match: its Esc menu (EFT's raid menu, minimised) gets our own
            bool inMatch = ZombiesMatch.Active && ZombiesPlugin.InRealRaid;
            bool matchMenu = inMatch && !ZombiesGameOver.Active && Screen == EEftScreenType.MainMenu && menu != null && menu.isActiveAndEnabled;
            if (!_built && !_buildFailed && (show || normalButton || matchMenu || (atMenus || inMatch) && ZombiesParty.PendingInvite != null))
                Build();
            if (!_built)
                return;
            Showing = show;
            InLobby = lobby;
            InMatchMenu = matchMenu;
            // EFT's deploying screen (the player model, Fika's loading bars) in zombies mode: the map's card in place of
            // EFT's control tips (user, 2026-11-11)
            bool deploy = atMenus && zombies && Screen == EEftScreenType.TimeHasCome;
            DeployScreen(deploy);
            bool visible = show || matchMenu || deploy;
            if (visible && !_wasVisible)
                _shownAt = Time.unscaledTime;
            _wasVisible = visible;
            ZombiesMainMenu.FrontEnd(visible, lobby);

            // Fit the reference layout to the screen (narrower than 16:9: by width)
            float aspect = (float)UnityEngine.Screen.width / Mathf.Max(1, UnityEngine.Screen.height);
            _mainScaler.matchWidthOrHeight = _topScaler.matchWidthOrHeight = aspect < 1.76f ? 0f : 1f;

            float dt = Time.unscaledDeltaTime;
            _menuA = Mathf.MoveTowards(_menuA, show && !lobby ? 1f : 0f, dt * 4f);
            _lobbyA = Mathf.MoveTowards(_lobbyA, lobby ? 1f : 0f, dt * 4f);
            _normalA = Mathf.MoveTowards(_normalA, normalButton ? 1f : 0f, dt * 4f);
            _matchA = Mathf.MoveTowards(_matchA, matchMenu ? 1f : 0f, dt * 6f);
            _deployA = Mathf.MoveTowards(_deployA, deploy ? 1f : 0f, dt * 4f);
            Fade(_deployGroup, _deploy, _deployA, false);
            if (deploy)
                _deployCard.Set(ZombiesParty.DeployMap ?? ZombiesParty.LobbyMap);
            Fade(_menuGroup, _menu, _menuA, show && !lobby);
            Fade(_lobbyGroup, _lobby, _lobbyA, lobby);
            Fade(_normalGroup, _normal, _normalA, normalButton);
            Fade(_matchGroup, _match, _matchA, matchMenu);
            _vignetteGroup.alpha = Mathf.Max(Mathf.Max(_menuA, _lobbyA), _matchA * 0.75f);
            _main.gameObject.SetActive(_menuA > 0f || _lobbyA > 0f || _normalA > 0f || _matchA > 0f || _deployA > 0f);
            _top.gameObject.SetActive(atMenus || inMatch);

            LobbySounds(atMenus);
            if (show && !lobby)
                UpdateMenu();
            if (lobby)
                UpdateLobby();
            if (matchMenu)
                UpdateMatch();
            if (atMenus || inMatch)
            {
                UpdateInvite(inMatch, matchMenu);
                if (atMenus)
                    UpdateRequests();
                UpdateToast();
                if (atMenus)
                {
                    UpdateCover();
                    UpdateBanner(onMain);
                }
                if (atMenus || matchMenu)
                    Keys(lobby);
                // A pop-up in a match lives only on its menu
                if (inMatch && !matchMenu && _modal != null)
                    CloseModal(false);
            }
            else if (_modal != null)
                CloseModal(false);
        }

        private static void Fade(CanvasGroup g, RectTransform rt, float a, bool on)
        {
            g.alpha = a;
            g.interactable = g.blocksRaycasts = on && a > 0.5f;
            rt.gameObject.SetActive(a > 0f);
            // A small rise as it comes in
            rt.anchoredPosition = new Vector2(0f, (1f - a) * -14f);
        }

        // ---------- Esc ----------

        private void Keys(bool lobby)
        {
            if (!Input.GetKeyDown(KeyCode.Escape) || ImguiPage)
                return;
            // The same Esc that just closed a page of ours, or EFT's settings, isn't a new one (it asked to leave the
            // lobby: user, 2026-11-10)
            if (Time.unscaledTime - ZombiesUi.EscUsedAt < 0.4f || Time.unscaledTime - _shownAt < 0.4f)
                return;
            if (_modal != null)
            {
                Action esc = _modalEsc;
                CloseModal(true);
                esc?.Invoke();
                return;
            }
            if (lobby && !ZombiesParty.Starting && ZombiesParty.Mine?.State == "open")
                AskLeave();
        }

        private void AskLeave()
        {
            bool host = ZombiesParty.IsHost;
            Confirm("LEAVE LOBBY?", host ? "Leaving will disband the lobby. Are you sure?" : "Leaving will leave the group. Are you sure?",
                "LEAVE", "STAY", () => ZombiesParty.LeaveLobby(), null);
        }

        // ---------- Building ----------

        private void Build()
        {
            try
            {
                _main = MakeCanvas("KennerZombies FrontEnd", MainOrder(), out _mainScaler, out _mainRoot);
                _top = MakeCanvas("KennerZombies FrontEnd Popups", 30500, out _topScaler, out _topRoot);

                // The vignette behind the zombies menus
                Image vig = ZombiesUi.Img(_mainRoot, "Vignette", ZombiesUi.Vignette, new Color(1f, 1f, 1f, 0.78f), false);
                vig.rectTransform.Fill();
                _vignetteGroup = vig.gameObject.AddComponent<CanvasGroup>();
                _vignetteGroup.blocksRaycasts = false;

                _menu = Layer(_mainRoot, "Menu", out _menuGroup);
                _lobby = Layer(_mainRoot, "Lobby", out _lobbyGroup);
                _normal = Layer(_mainRoot, "Normal", out _normalGroup);
                _match = Layer(_mainRoot, "Match", out _matchGroup);
                _deploy = Layer(_mainRoot, "Deploy", out _deployGroup);
                _deployCard = new MapCard(_deploy, new Vector2(620f, 530f));
                _deployCard.Root.Place(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-90f, 40f), new Vector2(620f, 530f));
                BuildMatch();
                BuildMenu();
                BuildLobby();
                BuildNormal();
                BuildOverlays();
                _built = true;
                ZombiesPlugin.Log.LogInfo($"Zombies menus: built (canvas order {_main.sortingOrder}, font {ZombiesUi.Font(false)?.name ?? "none"})");
            }
            catch (Exception e)
            {
                _buildFailed = true;
                ZombiesPlugin.Log.LogError($"Zombies menus: couldn't be built, EFT's menu stays: {e}");
            }
        }

        // Just over EFT's main menu canvas
        private static int MainOrder()
        {
            try
            {
                Canvas c = ZombiesMainMenu.Menu != null ? ZombiesMainMenu.Menu.GetComponentInParent<Canvas>() : null;
                c = c != null ? c.rootCanvas : null;
                if (c != null)
                {
                    ZombiesPlugin.Log.LogInfo($"Zombies menus: EFT's menu canvas '{c.name}' {c.renderMode}, order {c.sortingOrder}");
                    return Mathf.Clamp(c.sortingOrder + 5, 5, 29000);
                }
            }
            catch
            {
            }
            return 50;
        }

        private Canvas MakeCanvas(string name, int order, out CanvasScaler scaler, out RectTransform root)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(transform, false);
            go.layer = 5;
            Canvas c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = order;
            c.pixelPerfect = false;
            scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            scaler.referencePixelsPerUnit = 100f;
            go.AddComponent<GraphicRaycaster>();
            root = (RectTransform)go.transform;
            return c;
        }

        private static RectTransform Layer(RectTransform parent, string name, out CanvasGroup group)
        {
            RectTransform rt = ZombiesUi.Rect(parent, name).Fill();
            group = rt.gameObject.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            rt.gameObject.SetActive(false);
            return rt;
        }

        // ----- The zombies main menu -----

        private void BuildMenu()
        {
            var items = new (string label, Action click)[]
            {
                ("Solo", () => ZombiesParty.GoSolo()),
                ("Create lobby", () => ZombiesParty.CreateLobby()),
                ("Customise", () => ZombiesMainMenu.Menu?.Click(EMenuType.Player)),
                ("Match report", () =>
                {
                    ZombiesReport.ShowLast();
                    ZombiesUi.Sound("open");
                }),
                ("Statistics", () => ZombiesStatsScreen.Show()),
                ("Game settings", () => ZombiesMainMenu.Menu?.Click(EMenuType.Settings)),
                ("Zombies settings", () => ZombiesControls.OpenPage()),
            };
            for (int i = 0; i < items.Length; i++)
            {
                ZButton b = ZombiesUi.Button(_menu, "Button " + items[i].label, items[i].label, 30f, items[i].click, true);
                ((RectTransform)b.transform).Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, -362f - i * 87f), new Vector2(470f, 74f));
                if (i == 1)
                    _create = b;
                if (i == 3)
                    _report = b;
            }
            ZButton exit = ZombiesUi.Button(_menu, "Button Exit", "Exit zombies mode", 28f, () => ZombiesMainMenu.LeaveZombies(), true);
            exit.ClickSound = "back";
            ((RectTransform)exit.transform).Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 56f), new Vector2(420f, 72f));
            _menuSocial = new SocialPanel(this, _menu, new Vector2(445f, 700f), new Vector2(-30f, -300f));
        }

        private void UpdateMenu()
        {
            _create.Interactable = ZombiesParty.HasFika;
            _report.Interactable = ZombiesReport.HasSaved;
            _menuSocial.Refresh(false);
        }

        // ----- The lobby -----

        private void BuildLobby()
        {
            _lobbyCard = new MapCard(_lobby, new Vector2(630f, 545f));
            _lobbyCard.Root.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), new Vector2(630f, 545f));

            _changeMap = LobbyButton(0, "Change map", OpenMapWindow);
            LobbyButton(1, "Zombies settings", () => ZombiesControls.OpenPage());
            LobbyButton(2, "Game settings", () => ZombiesMainMenu.Menu?.Click(EMenuType.Settings));
            _startButton = LobbyButton(3, "Start match", StartPressed);

            // ZOMBIES LOBBY and the lobby type
            RectTransform head = ZombiesUi.PanelWithShadow(_lobby, "Title");
            head.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(50f, -140f), new Vector2(560f, 172f));
            TextMeshProUGUI title = ZombiesUi.Text(head, "Title", "ZOMBIES LOBBY", 40f, ZombiesUi.Red, TextAlignmentOptions.Center, true, 4f);
            title.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(520f, 52f));
            ZombiesUi.Line(head, new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.7f), 66f, 70f);
            _typeButton = ZombiesUi.Button(head, "Type", "Lobby type: private", 20f, () => ZombiesParty.TogglePrivate());
            _typeButton.ClickSound = "toggle";
            ((RectTransform)_typeButton.transform).Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 58f), new Vector2(360f, 40f));
            _typeText = ZombiesUi.Text(head, "TypeText", "LOBBY TYPE: PRIVATE", 20f, ZombiesUi.Ink, TextAlignmentOptions.Center, false, 1.5f);
            _typeText.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 58f), new Vector2(460f, 40f));
            // Fika mode: a free Fika headless hosts the match, or the host's own game (user, 2026-11-11)
            _fikaButton = ZombiesUi.Button(head, "Fika", "Fika mode: host", 20f, () =>
            {
                ZombiesParty.UseHeadless = !ZombiesParty.UseHeadless;
                if (ZombiesParty.UseHeadless)
                    ZombiesParty.Toast("Headless is used only if one is free when the match starts; otherwise your game hosts.");
            });
            _fikaButton.ClickSound = "toggle";
            ((RectTransform)_fikaButton.transform).Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(360f, 40f));
            _fikaText = ZombiesUi.Text(head, "FikaText", "FIKA MODE: HOST", 20f, ZombiesUi.Ink, TextAlignmentOptions.Center, false, 1.5f);
            _fikaText.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(460f, 40f));

            // The players
            RectTransform players = ZombiesUi.PanelWithShadow(_lobby, "Players");
            players.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(50f, -336f), new Vector2(640f, 524f));
            for (int i = 0; i < ZombiesPartyMax; i++)
                _rows.Add(new MemberRow(this, players, i));
            _countdown = ZombiesUi.Text(players, "Countdown", "", 40f, ZombiesUi.Red, TextAlignmentOptions.Center, true, 3f);
            _countdown.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 64f), new Vector2(600f, 56f));
            _footer = ZombiesUi.Text(players, "Footer", "", 19f, ZombiesUi.Dim, TextAlignmentOptions.Center);
            _footer.enableWordWrapping = true;
            _footer.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(600f, 80f));
            Image fLine = ZombiesUi.Img(players, "FooterLine", ZombiesUi.Divider, new Color(1f, 1f, 1f, 0.14f), false);
            fLine.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 120f), new Vector2(560f, 1.5f));

            _lobbySocial = new SocialPanel(this, _lobby, new Vector2(525f, 724f), new Vector2(-26f, -266f));
            // BACK where the menu's EXIT ZOMBIES MODE is: the same question as Esc (user, 2026-11-11)
            _backButton = ZombiesUi.Button(_lobby, "Button Back", "Back", 28f, AskLeave, true);
            _backButton.ClickSound = "back";
            ((RectTransform)_backButton.transform).Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 56f), new Vector2(420f, 72f));
        }

        private const int ZombiesPartyMax = 4;

        private ZButton LobbyButton(int i, string label, Action click)
        {
            ZButton b = ZombiesUi.Button(_lobby, "Button " + label, label, 28f, click, true);
            ((RectTransform)b.transform).Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(70f, -625f - i * 90f), new Vector2(390f, 76f));
            return b;
        }

        private void StartPressed()
        {
            if (ZombiesParty.IsHost)
            {
                if (ZombiesParty.Counting)
                    ZombiesParty.CancelCountdown();
                else
                    ZombiesParty.StartMatch();
            }
            else
                ZombiesParty.ToggleReady();
        }

        private void UpdateLobby()
        {
            ZombiesParty.Party mine = ZombiesParty.Mine;
            if (mine == null)
                return;
            bool host = ZombiesParty.IsHost;
            bool open = mine.State == "open";
            bool starting = ZombiesParty.Starting || !open;
            _lobbyCard.Set(ZombiesParty.LobbyMap);
            _changeMap.Interactable = host && open && !ZombiesParty.Counting;
            _backButton.Interactable = open && !starting;
            if (host)
            {
                _startButton.SetLabel(ZombiesParty.Counting ? "Cancel" : "Start match");
                _startButton.Interactable = !starting && (ZombiesParty.Counting || ZombiesParty.CanStart);
            }
            else
            {
                _startButton.SetLabel(ZombiesParty.WantReady ? "Not ready" : "Ready up");
                _startButton.Interactable = open && !starting;
            }
            _startButton.ClickSound = host ? "select" : "toggle";
            string type = mine.Private ? "PRIVATE" : "OPEN";
            _typeButton.gameObject.SetActive(host && open);
            _typeButton.SetLabel("Lobby type: " + type);
            _typeText.gameObject.SetActive(!(host && open));
            _typeText.text = "LOBBY TYPE: <color=#" + ColorUtility.ToHtmlStringRGB(mine.Private ? ZombiesUi.Gold : ZombiesUi.Ready) + ">" + type + "</color>";
            // Fika mode: the host switches it only while a headless is free (locked to HOST otherwise)
            bool free = ZombiesParty.HeadlessFree > 0;
            bool headless = host ? ZombiesParty.UseHeadless && free : mine.Headless;
            string mode = headless ? "HEADLESS" : "HOST";
            _fikaButton.gameObject.SetActive(host && open);
            _fikaButton.Interactable = free && !ZombiesParty.Counting;
            _fikaButton.SetLabel(free ? "Fika mode: " + mode : "Fika mode: host (no headless free)");
            _fikaText.gameObject.SetActive(!(host && open));
            _fikaText.text = "FIKA MODE: <color=#" + ColorUtility.ToHtmlStringRGB(headless ? ZombiesUi.Ready : ZombiesUi.Gold) + ">" + mode + "</color>";
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].Set(i < mine.Members.Count ? mine.Members[i] : null, mine, i);
            if (ZombiesParty.Counting)
            {
                int n = ZombiesParty.CountdownLeft;
                _countdown.text = n > 0 ? $"MATCH STARTS IN {n}" : "STARTING...";
                _footer.text = host ? "Press CANCEL to stop the countdown." : "Get ready...";
            }
            else if (starting)
            {
                _countdown.text = "STARTING MATCH";
                _footer.text = host ? "Hosting the match..." : $"{ZombiesParty.Name(mine.Id, mine)} is hosting the match...";
            }
            else
            {
                _countdown.text = "";
                string first = mine.Members.Count < 2 ? "Invite players from the social list to fill the lobby."
                    : host ? (ZombiesParty.AllOthersReady(mine) ? "Everyone's ready: press START MATCH." : "All players must be ready to start the match.")
                    : ZombiesParty.WantReady ? $"You're ready. {ZombiesParty.Name(mine.Id, mine)} starts the match." : "All players must be ready to start the match.";
                _footer.text = first + "\n<color=#8a8680>Click on players for options</color>";
            }
            _lobbySocial.Refresh(true);
        }

        // ----- The match's Esc menu: LEAVE MATCH, ZOMBIES SETTINGS, GAME SETTINGS, RESUME, and the social list -----
        // The match shows through behind it (EFT's menu scene is held off: ZombiesMainMenu), the HUD's round, portrait
        // and points stand aside while it's up (user, 2026-11-10)

        private void BuildMatch()
        {
            _pausedText = ZombiesUi.Text(_match, "Paused", "", 46f, ZombiesUi.Red, TextAlignmentOptions.Left, true, 6f);
            _pausedText.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(72f, 200f), new Vector2(700f, 64f));
            // RESUME at the top, LEAVE MATCH at the bottom (user, 2026-11-11)
            var items = new (string label, Action click, string sound)[]
            {
                ("Resume", () => ZombiesMainMenu.Menu?._hideScreenButton?.OnClick?.Invoke(), "back"),
                ("Zombies settings", () => ZombiesControls.OpenPage(), "select"),
                ("Game settings", OpenGameSettingsInMatch, "select"),
                ("Leave match", () => Confirm("LEAVE MATCH?", "Leave the match now? Your points and stats so far are kept. Are you sure?",
                    "LEAVE", "STAY", LeaveMatchNow, null), "select"),
            };
            for (int i = 0; i < items.Length; i++)
            {
                ZButton b = ZombiesUi.Button(_match, "Button " + items[i].label, items[i].label, 30f, items[i].click, true);
                b.ClickSound = items[i].sound;
                ((RectTransform)b.transform).Place(new Vector2(0f, 0.5f), new Vector2(0f, 1f), new Vector2(70f, 180f - i * 87f), new Vector2(470f, 74f));
            }
            _matchSocial = new SocialPanel(this, _match, new Vector2(445f, 700f), new Vector2(-30f, -190f));
        }

        private void UpdateMatch()
        {
            _pausedText.text = ZombiesPause.Paused ? "PAUSED" : "";
            _matchSocial.Refresh(false);
        }

        /// <summary>
        /// EFT's settings from the match's menu. In a raid they open from the bottom task bar's settings button (its own
        /// event); the main menu's route (MenuScreen.Click) does nothing there (user, 2026-11-11: GAME SETTINGS didn't
        /// open). So the task bar's event is raised as its button would (the bar itself is hidden in zombies matches).
        /// </summary>
        private static void OpenGameSettingsInMatch()
        {
            try
            {
                MenuTaskBar bar = FindObjectOfType<MenuTaskBar>();
                if (bar != null && HarmonyLib.AccessTools.Field(typeof(MenuTaskBar), "OnTaskBarButtonPressed")?.GetValue(bar) is Action<EMenuType, bool> pressed)
                {
                    pressed(EMenuType.Settings, true);
                    ZombiesPlugin.Log.LogInfo("Zombies menus: game settings opened (task bar)");
                    return;
                }
                ZombiesPlugin.Log.LogWarning($"Zombies menus: no task bar to open the game settings from ({(bar == null ? "no bar" : "no listener")}): trying the menu's way");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies menus: game settings from the task bar failed ({e.Message}): trying the menu's way");
            }
            ZombiesMainMenu.Menu?.Click(EMenuType.Settings);
        }

        /// <summary>Out of the match the way EFT's own DISCONNECT goes (no rewards: ZombiesLeave).</summary>
        private static void LeaveMatchNow()
        {
            try
            {
                ZombiesPlugin.Log.LogInfo("Zombies menus: leaving the match from the menu");
                ZombiesLeave.SkipNextReconnectScreen();
                ZombiesMainMenu.Menu?._disconnectButton?.OnClick?.Invoke();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies menus: couldn't leave the match: {e}");
            }
        }

        // EFT's own tips panel and location name off on the zombies deploying screen, back for normal raids
        private bool _deployHidden;

        private void DeployScreen(bool zombies)
        {
            var screen = ZombiesMapSelect.DeployScreen;
            if (screen == null)
                return;
            if (zombies)
            {
                if (screen._bannersPanel != null && screen._bannersPanel.gameObject.activeSelf)
                    screen._bannersPanel.gameObject.SetActive(false);
                if (screen._locationName != null && screen._locationName.gameObject.activeSelf)
                    screen._locationName.gameObject.SetActive(false);
                _deployHidden = true;
            }
            else if (_deployHidden && !ZombiesMapSelect.Zombies)
            {
                _deployHidden = false;
                screen._bannersPanel?.gameObject.SetActive(true);
                screen._locationName?.gameObject.SetActive(true);
            }
        }

        // ----- Normal mode: the ZOMBIES MODE button -----

        private void BuildNormal()
        {
            TextMeshProUGUI label = ZombiesUi.Text(_normal, "Label", "GAME MODE", 15f, ZombiesUi.Red, TextAlignmentOptions.Left, true, 2f);
            label.rectTransform.Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-60f, -82f), new Vector2(260f, 22f));
            ZButton b = ZombiesUi.Button(_normal, "Zombies mode", "Zombies mode", 24f, () => ZombiesMainMenu.EnterZombies(), true);
            ((RectTransform)b.transform).Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-60f, -106f), new Vector2(260f, 56f));
        }

        // ---------- Pop-ups ----------

        private void BuildOverlays()
        {
            // The toast (top middle)
            _toast = ZombiesUi.PanelWithShadow(_topRoot, "Toast", ZombiesUi.Window, 0.5f);
            _toast.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -64f), new Vector2(680f, 60f));
            _toastGroup = _toast.gameObject.AddComponent<CanvasGroup>();
            _toastGroup.blocksRaycasts = false;
            _toastGroup.alpha = 0f;
            Image accent = ZombiesUi.Img(_toast, "Accent", null, ZombiesUi.Red, false);
            accent.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(6f, 0f), new Vector2(4f, 36f));
            _toastText = ZombiesUi.Text(_toast, "Text", "", 21f, ZombiesUi.Ink, TextAlignmentOptions.Center);
            _toastText.enableWordWrapping = true;
            _toastText.rectTransform.Fill(20f, 4f, 20f, 4f);

            // In a lobby, away from the main menu: where to go
            _banner = ZombiesUi.PanelWithShadow(_topRoot, "Banner", ZombiesUi.Window, 0.4f);
            _banner.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(760f, 44f));
            _bannerGroup = _banner.gameObject.AddComponent<CanvasGroup>();
            _bannerGroup.blocksRaycasts = false;
            _bannerGroup.alpha = 0f;
            _bannerText = ZombiesUi.Text(_banner, "Text", "", 19f, ZombiesUi.Ink, TextAlignmentOptions.Center);
            _bannerText.rectTransform.Fill(14f, 0f, 14f, 0f);

            // STARTING MATCH: over EFT's screens while the host's game walks them and everyone joins
            _cover = ZombiesUi.Rect(_topRoot, "Cover").Fill();
            Image black = _cover.gameObject.AddComponent<Image>();
            black.color = new Color(0f, 0f, 0f, 0.94f);
            black.raycastTarget = true;
            _coverGroup = _cover.gameObject.AddComponent<CanvasGroup>();
            _coverGroup.alpha = 0f;
            Image vig = ZombiesUi.Img(_cover, "Vignette", ZombiesUi.Vignette, Color.white, false);
            vig.rectTransform.Fill();
            TextMeshProUGUI big = ZombiesUi.Text(_cover, "Title", "STARTING MATCH", 54f, ZombiesUi.Red, TextAlignmentOptions.Center, true, 6f);
            big.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(1200f, 80f));
            _coverSub = ZombiesUi.Text(_cover, "Sub", "", 22f, ZombiesUi.Dim, TextAlignmentOptions.Center);
            _coverSub.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -30f), new Vector2(1200f, 40f));
            _cover.gameObject.SetActive(false);
        }

        private void UpdateToast()
        {
            float t = Time.unscaledTime - _toastAt;
            if (t > ToastSeconds)
            {
                string next = ZombiesParty.TakeToast();
                if (next != null)
                {
                    _toastText.text = next;
                    _toastAt = Time.unscaledTime;
                    t = 0f;
                    ZombiesUi.Sound("toggle");
                }
            }
            float a = t > ToastSeconds ? 0f : Mathf.Clamp01(Mathf.Min(t / 0.3f, (ToastSeconds - t) / 0.35f));
            _toastGroup.alpha = a;
            _toast.gameObject.SetActive(a > 0f);
        }

        private float _coverA, _coverSince = -1f;

        private void UpdateCover()
        {
            ZombiesParty.Party mine = ZombiesParty.Mine;
            bool want = (ZombiesParty.Starting || mine != null && mine.State != "open")
                        && Screen != EEftScreenType.TimeHasCome && !ZombiesMatch.Active;
            if (want && _coverSince < 0f)
                _coverSince = Time.unscaledTime;
            if (!want)
                _coverSince = -1f;
            // Never longer than half a minute (an error of Fika's must be seen)
            if (want && Time.unscaledTime - _coverSince > 30f)
                want = false;
            _coverA = Mathf.MoveTowards(_coverA, want ? 1f : 0f, Time.unscaledDeltaTime * 3f);
            _coverGroup.alpha = _coverA;
            _coverGroup.blocksRaycasts = want;
            _cover.gameObject.SetActive(_coverA > 0f);
            if (_coverA > 0f)
                _coverSub.text = (ZombiesParty.IsHost ? "Hosting the match" : "Joining the match") + new string('.', (int)(Time.unscaledTime * 2.5f) % 4);
        }

        private void UpdateBanner(bool onMain)
        {
            ZombiesParty.Party mine = ZombiesParty.Mine;
            EEftScreenType? s = Screen;
            bool want = mine != null && mine.State == "open" && !onMain && s != EEftScreenType.SelectLocation && s != EEftScreenType.SelectRaidSide;
            if (want)
                _bannerText.text = ZombiesParty.IsHost ? "You're hosting a zombies lobby: back to the main menu to start the match."
                    : $"You're in {ZombiesParty.Name(mine.Id, mine)}'s zombies lobby: back to the main menu to ready up.";
            _bannerGroup.alpha = Mathf.MoveTowards(_bannerGroup.alpha, want ? 1f : 0f, Time.unscaledDeltaTime * 4f);
            _banner.gameObject.SetActive(_bannerGroup.alpha > 0f);
        }

        // Players coming and going in the lobby, the lobby made or gone, everyone ready (the host's)
        private void LobbySounds(bool atMenus)
        {
            ZombiesParty.Party mine = ZombiesParty.Mine;
            if (!atMenus)
            {
                _partyId = null;
                _members.Clear();
                return;
            }
            if (mine == null)
            {
                if (_partyId != null)
                {
                    ZombiesUi.Sound("leave");
                    if (_modalKind == "options" || _modalKind == "maps")
                        CloseModal(false);
                }
                _partyId = null;
                _members.Clear();
                _couldStart = false;
                return;
            }
            var now = new HashSet<string>(mine.Members.Select(m => m.Id));
            if (_partyId == null)
                ZombiesUi.Sound("join");
            else
            {
                string me = ZombiesParty.MyId;
                if (now.Any(id => id != me && !_members.Contains(id)))
                    ZombiesUi.Sound("join");
                else if (_members.Any(id => id != me && !now.Contains(id)))
                    ZombiesUi.Sound("leave");
            }
            _partyId = mine.Id;
            _members.Clear();
            _members.UnionWith(now);
            bool can = ZombiesParty.CanStart;
            if (can && !_couldStart)
                ZombiesUi.Sound("all_ready");
            _couldStart = can;
        }

        // ----- The modal layer: one pop-up at a time, Esc closes it -----

        private RectTransform OpenModal(string kind, Action esc, float dim = 0.6f, bool clickOutsideCloses = false)
        {
            CloseModal(false);
            _modalUp = true;
            _modalKind = kind;
            _modalEsc = esc;
            _modal = ZombiesUi.Rect(_topRoot, "Modal " + kind).Fill();
            _modal.SetSiblingIndex(Mathf.Max(0, _cover.GetSiblingIndex()));
            Image back = _modal.gameObject.AddComponent<Image>();
            back.color = new Color(0f, 0f, 0f, dim);
            back.raycastTarget = true;
            if (clickOutsideCloses)
            {
                ZClickable c = _modal.gameObject.AddComponent<ZClickable>();
                c.Clicked = _ => CloseModal(true);
            }
            _modalGroup = _modal.gameObject.AddComponent<CanvasGroup>();
            _modalGroup.alpha = 0f;
            StartCoroutine(FadeIn(_modalGroup));
            return _modal;
        }

        private static System.Collections.IEnumerator FadeIn(CanvasGroup g)
        {
            while (g != null && g.alpha < 1f)
            {
                g.alpha = Mathf.MoveTowards(g.alpha, 1f, Time.unscaledDeltaTime * 7f);
                yield return null;
            }
        }

        private void CloseModal(bool sound)
        {
            if (_modal == null)
                return;
            if (sound)
                ZombiesUi.Sound("close");
            Destroy(_modal.gameObject);
            _modalUp = false;
            _modal = null;
            _modalKind = null;
            _modalEsc = null;
            _inviteShown = null;
        }

        /// <summary>A window in the middle of the screen with its title; the returned rect is its body.</summary>
        private RectTransform Window(RectTransform modal, string title, Vector2 size)
        {
            RectTransform w = ZombiesUi.PanelWithShadow(modal, "Window", ZombiesUi.Window, 0.75f);
            w.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size);
            // Clicks on the window don't fall through to the close-on-click-outside
            w.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            Image accent = ZombiesUi.Img(w, "TopAccent", ZombiesUi.Divider, ZombiesUi.Red, false);
            accent.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -1f), new Vector2(size.x - 30f, 2f));
            TextMeshProUGUI t = ZombiesUi.Text(w, "Title", title, 32f, ZombiesUi.Red, TextAlignmentOptions.Center, true, 3f);
            t.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -18f), new Vector2(size.x - 40f, 44f));
            ZombiesUi.Line(w, new Color(1f, 1f, 1f, 0.14f), 70f, 40f);
            return w;
        }

        private void Confirm(string title, string body, string yes, string no, Action onYes, Action onNo)
        {
            RectTransform m = OpenModal("confirm", onNo);
            ZombiesUi.Sound("open");
            RectTransform w = Window(m, title, new Vector2(680f, 260f));
            TextMeshProUGUI text = ZombiesUi.Text(w, "Body", body, 22f, ZombiesUi.Ink, TextAlignmentOptions.Center);
            text.enableWordWrapping = true;
            text.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -86f), new Vector2(600f, 70f));
            ZButton y = ZombiesUi.Button(w, "Yes", yes, 22f, () =>
            {
                CloseModal(false);
                onYes?.Invoke();
            });
            ((RectTransform)y.transform).Place(new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(-10f, 22f), new Vector2(240f, 54f));
            ZButton n = ZombiesUi.Button(w, "No", no, 22f, () =>
            {
                CloseModal(false);
                onNo?.Invoke();
            });
            n.ClickSound = "back";
            ((RectTransform)n.transform).Place(new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(10f, 22f), new Vector2(240f, 54f));
        }

        /// <summary>A small menu of choices where the player clicked (a player's options); a click outside closes it.</summary>
        private void Options(Vector2 screenPos, string heading, List<(string label, Action act, bool on)> items)
        {
            RectTransform m = OpenModal("options", null, 0.18f, true);
            ZombiesUi.Sound("open");
            float h = 52f + items.Count * 50f;
            RectTransform w = ZombiesUi.PanelWithShadow(m, "Options", ZombiesUi.Window, 0.6f);
            w.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_topRoot, screenPos, null, out Vector2 local);
            Vector2 size = _topRoot.rect.size;
            // Kept on the screen
            float x = Mathf.Clamp(local.x + 8f, -size.x / 2f + 10f, size.x / 2f - 290f);
            float y = Mathf.Clamp(local.y - 8f, -size.y / 2f + h + 10f, size.y / 2f - 10f);
            w.Place(new Vector2(0.5f, 0.5f), new Vector2(0f, 1f), new Vector2(x, y), new Vector2(280f, h));
            TextMeshProUGUI t = ZombiesUi.Text(w, "Heading", heading, 18f, ZombiesUi.Red, TextAlignmentOptions.Left, true, 1.5f);
            t.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -10f), new Vector2(250f, 30f));
            ZombiesUi.Line(w, new Color(1f, 1f, 1f, 0.14f), 42f, 10f);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                ZButton b = ZombiesUi.Button(w, item.label, item.label, 19f, () =>
                {
                    CloseModal(false);
                    item.act?.Invoke();
                });
                b.Interactable = item.on;
                ((RectTransform)b.transform).Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -50f - i * 50f), new Vector2(252f, 42f));
            }
        }

        // ----- The invite pop-up: centre screen, over everything, JOIN / DECLINE (Esc declines) -----

        private void UpdateInvite(bool inMatch, bool matchMenu)
        {
            ZombiesParty.Invite live = ZombiesParty.PendingInvite;
            ZombiesParty.Invite inv = ImguiPage ? null : live;
            string key = inv == null ? null : (inv.Id ?? inv.PartyId);
            // In a match, playing: a note at the top middle (press Esc to answer); the question on the Esc menu
            if (inMatch)
            {
                string liveKey = live == null ? null : (live.Id ?? live.PartyId);
                if (liveKey != null && liveKey != _inviteHeard)
                {
                    _inviteHeard = liveKey;
                    ZombiesUi.Sound("invite");
                }
                bool note = live != null && !matchMenu;
                if (note)
                    _bannerText.text = $"<b>{live.From}</b> invited you to their zombies lobby  -  press <color=#{ColorUtility.ToHtmlStringRGB(ZombiesUi.Red)}>ESC</color> to answer";
                _bannerGroup.alpha = Mathf.MoveTowards(_bannerGroup.alpha, note ? 1f : 0f, Time.unscaledDeltaTime * 4f);
                _banner.gameObject.SetActive(_bannerGroup.alpha > 0f);
                if (!matchMenu)
                    return;
            }
            if (inv == null)
            {
                if (_modalKind == "invite")
                    CloseModal(false);
                return;
            }
            if (_modalKind == "invite" && _inviteShown == key)
                return;
            // A question already up waits for its answer; options or the map list make way
            if (_modalKind == "confirm")
                return;
            bool missing = inv.Missing.Count > 0;
            if (inMatch && !missing)
            {
                // From inside a match: taking it means leaving the match
                RectTransform mm = OpenModal("invite", () => ZombiesParty.AnswerInvite(false), 0.65f);
                _inviteShown = key;
                ZombiesUi.Sound("open");
                RectTransform ww = Window(mm, "ZOMBIES INVITE", new Vector2(720f, 300f));
                TextMeshProUGUI body = ZombiesUi.Text(ww, "Body", $"<b>{inv.From}</b> has invited you to their lobby. Do you wish to leave?\n"
                    + "<size=80%><color=#8a8680>Note: progress from this match will not be saved.</color></size>", 23f, ZombiesUi.Ink, TextAlignmentOptions.Center);
                body.enableWordWrapping = true;
                body.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -86f), new Vector2(650f, 100f));
                ZButton yes = ZombiesUi.Button(ww, "Yes", "Yes", 22f, () =>
                {
                    CloseModal(false);
                    ZombiesParty.AnswerInvite(true);
                    LeaveMatchNow();
                });
                ((RectTransform)yes.transform).Place(new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(-10f, 22f), new Vector2(240f, 54f));
                ZButton nope = ZombiesUi.Button(ww, "No", "No", 22f, () =>
                {
                    CloseModal(false);
                    ZombiesParty.AnswerInvite(false);
                });
                nope.ClickSound = "back";
                ((RectTransform)nope.transform).Place(new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(10f, 22f), new Vector2(240f, 54f));
                return;
            }
            RectTransform m = OpenModal("invite", () => ZombiesParty.AnswerInvite(false), 0.65f);
            _inviteShown = key;
            if (!inMatch)
                ZombiesUi.Sound("invite");
            RectTransform w = Window(m, "ZOMBIES INVITE", new Vector2(700f, missing ? 400f : 280f));
            string where = string.IsNullOrEmpty(inv.Map) ? "" : " on <color=#" + ColorUtility.ToHtmlStringRGB(ZombiesUi.Red) + ">" + ZombiesParty.MapName(inv.Map) + "</color>";
            TextMeshProUGUI text = ZombiesUi.Text(w, "Body", $"<b>{inv.From}</b> invited you to play zombies{where}.", 24f, ZombiesUi.Ink, TextAlignmentOptions.Center);
            text.enableWordWrapping = true;
            text.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -88f), new Vector2(620f, 70f));
            if (missing)
            {
                TextMeshProUGUI need = ZombiesUi.Text(w, "Missing", "You need these first (on SPT Forge):\n" + string.Join("\n", inv.Missing.Select(x => "- " + x)),
                    18f, ZombiesUi.Dim, TextAlignmentOptions.Top);
                need.enableWordWrapping = true;
                need.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -160f), new Vector2(600f, 150f));
                ZButton ok = ZombiesUi.Button(w, "OK", "OK", 22f, () =>
                {
                    CloseModal(false);
                    ZombiesParty.AnswerInvite(false);
                });
                ((RectTransform)ok.transform).Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 22f), new Vector2(240f, 54f));
                return;
            }
            ZButton join = ZombiesUi.Button(w, "Join", "Join", 22f, () =>
            {
                CloseModal(false);
                ZombiesParty.AnswerInvite(true);
            });
            ((RectTransform)join.transform).Place(new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(-10f, 22f), new Vector2(240f, 54f));
            ZButton no = ZombiesUi.Button(w, "Decline", "Decline", 22f, () =>
            {
                CloseModal(false);
                ZombiesParty.AnswerInvite(false);
            });
            no.ClickSound = "back";
            ((RectTransform)no.transform).Place(new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(10f, 22f), new Vector2(240f, 54f));
        }

        // ----- The host: someone asks to join the lobby (ACCEPT / DECLINE, Esc declines) -----

        private string _requestShown;

        private void UpdateRequests()
        {
            if (!ZombiesParty.IsHost || ImguiPage)
                return;
            ZombiesParty.JoinRequest r = ZombiesParty.Requests.FirstOrDefault();
            if (r == null)
            {
                if (_modalKind == "request")
                    CloseModal(false);
                _requestShown = null;
                return;
            }
            if (_modalKind == "request" && _requestShown == r.Id || _modal != null && _modalKind != "options")
                return;
            string id = r.Id;
            RectTransform m = OpenModal("request", () => ZombiesParty.AnswerRequest(id, false), 0.65f);
            _requestShown = id;
            ZombiesUi.Sound("invite");
            RectTransform w = Window(m, "JOIN REQUEST", new Vector2(680f, 260f));
            TextMeshProUGUI text = ZombiesUi.Text(w, "Body", $"<b>{r.Name}</b> wants to join your lobby.", 24f, ZombiesUi.Ink, TextAlignmentOptions.Center);
            text.enableWordWrapping = true;
            text.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -90f), new Vector2(600f, 60f));
            ZButton yes = ZombiesUi.Button(w, "Accept", "Accept", 22f, () =>
            {
                CloseModal(false);
                ZombiesParty.AnswerRequest(id, true);
            });
            ((RectTransform)yes.transform).Place(new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(-10f, 22f), new Vector2(240f, 54f));
            ZButton no = ZombiesUi.Button(w, "Decline", "Decline", 22f, () =>
            {
                CloseModal(false);
                ZombiesParty.AnswerRequest(id, false);
            });
            no.ClickSound = "back";
            ((RectTransform)no.transform).Place(new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(10f, 22f), new Vector2(240f, 54f));
        }

        // ----- CHANGE MAP: the maps on the left, the hovered one's card on the right -----

        private void OpenMapWindow()
        {
            if (!ZombiesParty.IsHost)
                return;
            RectTransform m = OpenModal("maps", null, 0.65f);
            ZombiesUi.Sound("open");
            RectTransform w = Window(m, "CHANGE MAP", new Vector2(1160f, 690f));
            var card = new MapCard(w, new Vector2(700f, 540f));
            card.Root.Place(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-28f, 28f), new Vector2(700f, 540f));
            string current = ZombiesParty.LobbyMap;
            card.Set(current);
            List<string> maps = ZombiesParty.MapIds;
            for (int i = 0; i < maps.Count; i++)
            {
                string id = maps[i];
                ZButton b = ZombiesUi.Button(w, "Map " + id, ZombiesParty.MapName(id), 24f, () =>
                {
                    ZombiesParty.SetMap(id);
                    CloseModal(false);
                });
                b.Selected = id == current;
                b.Hovered = () => card.Set(id);
                ((RectTransform)b.transform).Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -96f - i * 64f), new Vector2(370f, 56f));
            }
            if (maps.Count < 2)
            {
                TextMeshProUGUI more = ZombiesUi.Text(w, "More", "More maps show here as they're installed.", 17f, ZombiesUi.Faint, TextAlignmentOptions.TopLeft);
                more.enableWordWrapping = true;
                more.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(34f, -96f - maps.Count * 64f - 6f), new Vector2(360f, 60f));
            }
            ZButton back = ZombiesUi.Button(w, "Back", "Back", 22f, () => CloseModal(true));
            back.ClickSound = "back";
            ((RectTransform)back.transform).Place(new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(28f, 28f), new Vector2(370f, 56f));
        }

        // ---------- A player's options (social list and lobby) ----------

        internal void PlayerOptions(Vector2 at, string id, string name, bool lobbyMember)
        {
            var items = new List<(string, Action, bool)>();
            items.Add((lobbyMember ? "Info" : "View statistics", () => ZombiesParty.ShowStatsOf(id, name), true));
            if (lobbyMember)
            {
                if (ZombiesParty.IsHost && id != ZombiesParty.MyId)
                    items.Add(("Kick player", () => ZombiesParty.Kick(id), ZombiesParty.Mine?.State == "open"));
            }
            else
            {
                ZombiesParty.PlayerInfo p = ZombiesParty.Players.FirstOrDefault(x => x.Id == id);
                if (p != null && CanInvite(p))
                    items.Add(("Invite", () => Invite(p), true));
                if (p != null && !string.IsNullOrEmpty(p.JoinParty) && ZombiesParty.Mine == null && ZombiesParty.HasFika)
                    items.Add(("Join lobby", () => ZombiesParty.JoinParty(p.JoinParty), true));
                // A private lobby: ask its host (user, 2026-11-11)
                else if (p != null && !string.IsNullOrEmpty(p.RequestParty) && ZombiesParty.Mine == null && ZombiesParty.HasFika && !ZombiesMatch.Active)
                    items.Add((p.Requested ? "Request sent" : "Request to join", () => ZombiesParty.RequestJoin(p.RequestParty), !p.Requested));
            }
            Options(at, name.ToUpperInvariant(), items);
        }

        internal static bool CanInvite(ZombiesParty.PlayerInfo p)
        {
            ZombiesParty.Party mine = ZombiesParty.Mine;
            // Players in a zombies match can be invited (it pops up in their match); not from inside a match, and not
            // someone in a normal raid
            return ZombiesParty.HasFika && !ZombiesMatch.Active && !p.InParty && !p.Invited && !(p.Status == "normal" && p.InRaid)
                   && (mine == null || mine.State == "open" && mine.Members.Count < ZombiesPartyMax);
        }

        /// <summary>INVITE: in a lobby it's sent; on the menu a lobby is made first, if the player says so.</summary>
        internal void Invite(ZombiesParty.PlayerInfo p)
        {
            if (ZombiesParty.Mine != null)
            {
                ZombiesParty.InvitePlayer(p.Id);
                return;
            }
            Confirm("CREATE A LOBBY?", $"Inviting {p.Name} needs a lobby. Create one and send the invite?", "CREATE", "CANCEL",
                () => ZombiesParty.InvitePlayer(p.Id), null);
        }

        // ---------- The social list ----------

        private class SocialPanel
        {
            private readonly ZombiesFrontEnd _fe;
            private readonly RectTransform _content;
            private string _sig;

            public SocialPanel(ZombiesFrontEnd fe, RectTransform parent, Vector2 size, Vector2 topRight)
            {
                _fe = fe;
                RectTransform root = ZombiesUi.PanelWithShadow(parent, "Social");
                root.Place(new Vector2(1f, 1f), new Vector2(1f, 1f), topRight, size);
                TextMeshProUGUI title = ZombiesUi.Text(root, "Title", "SOCIAL", 30f, ZombiesUi.Red, TextAlignmentOptions.Left, true, 3f);
                title.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -12f), new Vector2(200f, 40f));
                TextMeshProUGUI fika = ZombiesUi.Text(root, "Fika", "(Powered by Fika)", 14f, ZombiesUi.Faint, TextAlignmentOptions.Right);
                fika.rectTransform.Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-20f, -24f), new Vector2(200f, 22f));
                ZombiesUi.Line(root, new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.6f), 58f, 14f);

                // The scrolling list
                RectTransform view = ZombiesUi.Rect(root, "View").Fill(12f, 12f, 18f, 68f);
                view.gameObject.AddComponent<RectMask2D>();
                Image hit = view.gameObject.AddComponent<Image>();
                hit.color = new Color(0f, 0f, 0f, 0f);
                _content = ZombiesUi.Rect(view, "Content");
                _content.anchorMin = new Vector2(0f, 1f);
                _content.anchorMax = new Vector2(1f, 1f);
                _content.pivot = new Vector2(0.5f, 1f);
                _content.offsetMin = _content.offsetMax = Vector2.zero;
                var layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.spacing = 5f;
                layout.childControlHeight = true;
                layout.childControlWidth = true;
                layout.childForceExpandHeight = false;
                layout.childForceExpandWidth = true;
                layout.padding = new RectOffset(2, 2, 2, 8);
                _content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                ScrollRect scroll = view.gameObject.AddComponent<ScrollRect>();
                scroll.content = _content;
                scroll.viewport = view;
                scroll.horizontal = false;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 32f;
                scroll.verticalScrollbar = Bar(root);
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            }

            // A slim red scrollbar down the right
            private static Scrollbar Bar(RectTransform root)
            {
                RectTransform track = ZombiesUi.Rect(root, "Scrollbar");
                track.anchorMin = new Vector2(1f, 0f);
                track.anchorMax = new Vector2(1f, 1f);
                track.pivot = new Vector2(1f, 0.5f);
                track.offsetMin = new Vector2(-12f, 14f);
                track.offsetMax = new Vector2(-7f, -70f);
                Image bg = track.gameObject.AddComponent<Image>();
                bg.color = new Color(1f, 1f, 1f, 0.06f);
                RectTransform area = ZombiesUi.Rect(track, "Area").Fill();
                Image handle = ZombiesUi.Img(area, "Handle", null, new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.85f), false, true);
                handle.rectTransform.Fill();
                Scrollbar bar = track.gameObject.AddComponent<Scrollbar>();
                bar.handleRect = handle.rectTransform;
                bar.targetGraphic = handle;
                bar.direction = Scrollbar.Direction.BottomToTop;
                return bar;
            }

            public void Refresh(bool inLobby)
            {
                List<ZombiesParty.PlayerInfo> players = ZombiesParty.Players;
                ZombiesParty.Party mine = ZombiesParty.Mine;
                // Rebuilt only when something shown has changed
                string sig = (ZombiesParty.ListsLoaded ? "L" : "-") + (mine == null ? "" : mine.State + mine.Members.Count) + "|" + string.Join(";", players.Select(p =>
                    $"{p.Id},{p.Name},{p.Status},{p.Match},{p.Count},{p.Round},{p.InParty},{p.Invited},{p.JoinParty},{p.InRaid},{p.Missing.Count},{p.PartySize},{p.RaidMap},{p.RequestParty},{p.Requested},{ZombiesParty.Sending(p.Id)}")) + ZombiesMatch.Active;
                if (sig == _sig)
                    return;
                _sig = sig;
                for (int i = _content.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
                var sections = new[]
                {
                    ("IN GAME", players.Where(p => p.Status == "match" || p.Status == "zombies").ToList()),
                    ("ONLINE", players.Where(p => p.Status == "normal").ToList()),
                    ("OFFLINE", players.Where(p => p.Status == "offline").ToList()),
                };
                foreach (var (header, list) in sections)
                {
                    Header(header, list.Count);
                    if (list.Count == 0)
                        Empty(ZombiesParty.ListsLoaded ? "Nobody" : "LOADING...");
                    foreach (ZombiesParty.PlayerInfo p in list)
                        Row(p);
                }
            }

            private void Header(string text, int count)
            {
                RectTransform rt = ZombiesUi.Rect(_content, "Header " + text);
                rt.gameObject.AddComponent<LayoutElement>().preferredHeight = 36f;
                TextMeshProUGUI t = ZombiesUi.Text(rt, "Text", text + (count > 0 ? $"  <color=#8a8680>({count})</color>" : ""), 18f, ZombiesUi.Red, TextAlignmentOptions.BottomLeft, true, 2f);
                t.rectTransform.Fill(6f, 6f, 6f, 0f);
                Image line = ZombiesUi.Img(rt, "Line", ZombiesUi.Divider, new Color(ZombiesUi.Red.r, ZombiesUi.Red.g, ZombiesUi.Red.b, 0.4f), false);
                line.rectTransform.anchorMin = new Vector2(0f, 0f);
                line.rectTransform.anchorMax = new Vector2(1f, 0f);
                line.rectTransform.pivot = new Vector2(0.5f, 0f);
                line.rectTransform.offsetMin = new Vector2(0f, 1f);
                line.rectTransform.offsetMax = new Vector2(0f, 2.5f);
            }

            private void Empty(string text)
            {
                RectTransform rt = ZombiesUi.Rect(_content, "Empty");
                rt.gameObject.AddComponent<LayoutElement>().preferredHeight = 38f;
                TextMeshProUGUI t = ZombiesUi.Text(rt, "Text", text, 16f, ZombiesUi.Faint, TextAlignmentOptions.Left);
                t.rectTransform.Fill(10f, 0f, 6f, 0f);
            }

            // Where they are (user, 2026-11-10): "Zombies - In party - 2/4", "Zombies - In menu", "Zombies - In game -
            // Stronghold", "Zombies - In solo - Stronghold", "Normal mode - In menu", "Normal mode - In raid - Customs"
            private static string Where(ZombiesParty.PlayerInfo p)
            {
                switch (p.Status)
                {
                    case "match":
                        return $"Zombies - {(p.Count <= 1 ? "In solo" : "In game")} - {ZombiesParty.MapName(p.Match)}{(p.Round > 0 ? $" (round {p.Round})" : "")}";
                    case "zombies":
                        string z = p.PartySize > 0 ? $"Zombies - In party - {p.PartySize}/{p.Max}" : "Zombies - In menu";
                        return p.Missing.Count > 0 ? z + $" (missing {p.Missing.Count})" : z;
                    case "normal":
                        return !p.InRaid ? "Normal mode - In menu"
                            : string.IsNullOrEmpty(p.RaidMap) ? "Normal mode - In raid" : $"Normal mode - In raid - {ZombiesParty.MapName(p.RaidMap)}";
                    default:
                        return "Offline";
                }
            }

            private void Row(ZombiesParty.PlayerInfo p)
            {
                RectTransform rt = ZombiesUi.Rect(_content, "Player " + p.Name);
                rt.gameObject.AddComponent<LayoutElement>().preferredHeight = 62f;
                Image face = rt.gameObject.AddComponent<Image>();
                face.sprite = ZombiesUi.RowFace;
                face.type = Image.Type.Sliced;
                face.raycastTarget = true;
                Image tint = ZombiesUi.Img(rt, "Tint", ZombiesUi.Glass(Color.white, Color.white, new Color(1f, 1f, 1f, 0f), 4f, 0f, 0f), new Color(1f, 1f, 1f, 0f));
                tint.rectTransform.Fill();
                ZClickable click = rt.gameObject.AddComponent<ZClickable>();
                click.Tint = tint;
                string id = p.Id, name = p.Name;
                click.Clicked = pos => _fe.PlayerOptions(pos, id, name, false);

                bool online = p.Status != "offline";
                Color nameColour = !online ? ZombiesUi.Faint : p.Status == "normal" ? ZombiesUi.Ink
                    : p.Missing.Count == 0 ? ZombiesUi.Ready : ZombiesUi.Gold;
                Image dot = ZombiesUi.Img(rt, "Dot", ZombiesUi.Dot, online ? nameColour : new Color(0.35f, 0.35f, 0.35f), false);
                dot.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, 0f), new Vector2(18f, 18f));
                TextMeshProUGUI n = ZombiesUi.Text(rt, "Name", p.Name, 20f, nameColour, TextAlignmentOptions.Left);
                n.rectTransform.anchorMin = new Vector2(0f, 0.5f);
                n.rectTransform.anchorMax = new Vector2(1f, 1f);
                n.rectTransform.offsetMin = new Vector2(34f, 0f);
                n.rectTransform.offsetMax = new Vector2(-130f, -4f);
                string where = Where(p);
                TextMeshProUGUI w = ZombiesUi.Text(rt, "Where", where, 14f, online ? ZombiesUi.Dim : ZombiesUi.Faint, TextAlignmentOptions.Left, false, 1f);
                w.rectTransform.anchorMin = new Vector2(0f, 0f);
                w.rectTransform.anchorMax = new Vector2(1f, 0.5f);
                w.rectTransform.offsetMin = new Vector2(34f, 4f);
                w.rectTransform.offsetMax = new Vector2(-130f, 0f);

                // The action on the right
                string tag = p.InParty ? "IN LOBBY" : p.Invited ? "INVITED" : ZombiesParty.Sending(p.Id) ? "SENDING" : p.Requested ? "ASKED" : null;
                if (tag != null)
                {
                    TextMeshProUGUI tg = ZombiesUi.Text(rt, "Tag", tag, 15f, ZombiesUi.Dim, TextAlignmentOptions.Center, true, 1.5f);
                    tg.rectTransform.Place(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(112f, 34f));
                    return;
                }
                if (!string.IsNullOrEmpty(p.JoinParty) && ZombiesParty.Mine == null && ZombiesParty.HasFika)
                {
                    ZButton j = ZombiesUi.Button(rt, "Join", "Join", 16f, () => ZombiesParty.JoinParty(p.JoinParty));
                    ((RectTransform)j.transform).Place(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(112f, 36f));
                    return;
                }
                if (ZombiesMatch.Active)
                    return;
                ZButton inv = ZombiesUi.Button(rt, "Invite", "Invite", 16f, () => _fe.Invite(p));
                inv.Interactable = CanInvite(p);
                ((RectTransform)inv.transform).Place(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-8f, 0f), new Vector2(112f, 36f));
            }
        }

        // ---------- A lobby player's row ----------

        private class MemberRow
        {
            private readonly RectTransform _root;
            private readonly Image _dot, _tint;
            private readonly TextMeshProUGUI _slot, _name, _status;
            private readonly ZClickable _click;
            private string _id, _nameText;
            private string _shown;

            public MemberRow(ZombiesFrontEnd fe, RectTransform parent, int i)
            {
                _root = ZombiesUi.Rect(parent, "Row " + i);
                _root.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -22f - i * 84f), new Vector2(600f, 74f));
                Image face = _root.gameObject.AddComponent<Image>();
                face.sprite = ZombiesUi.RowFace;
                face.type = Image.Type.Sliced;
                face.raycastTarget = true;
                _tint = ZombiesUi.Img(_root, "Tint", ZombiesUi.Glass(Color.white, Color.white, new Color(1f, 1f, 1f, 0f), 4f, 0f, 0f), new Color(1f, 1f, 1f, 0f));
                _tint.rectTransform.Fill();
                _click = _root.gameObject.AddComponent<ZClickable>();
                _click.Tint = _tint;
                _click.Clicked = pos =>
                {
                    if (_id != null)
                        fe.PlayerOptions(pos, _id, _nameText, true);
                };
                _dot = ZombiesUi.Img(_root, "Dot", ZombiesUi.Dot, ZombiesUi.NotReady, false);
                _dot.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(26f, 26f));
                _slot = ZombiesUi.Text(_root, "Slot", $"PLAYER {i + 1}", 15f, ZombiesUi.Faint, TextAlignmentOptions.Left, true, 2f);
                _slot.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(56f, -8f), new Vector2(200f, 20f));
                _name = ZombiesUi.Text(_root, "Name", "", 26f, ZombiesUi.Ink, TextAlignmentOptions.Left);
                _name.rectTransform.Place(new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(56f, 8f), new Vector2(360f, 40f));
                _status = ZombiesUi.Text(_root, "Status", "", 22f, ZombiesUi.NotReady, TextAlignmentOptions.Right, true, 2f);
                _status.rectTransform.Place(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-18f, 0f), new Vector2(200f, 40f));
            }

            public void Set(ZombiesParty.Member m, ZombiesParty.Party p, int i)
            {
                string me = ZombiesParty.MyId;
                bool host = m != null && m.Id == p.Id;
                bool ready = host || m != null && m.Ready;
                string shown = m == null ? "empty" : $"{m.Id}|{m.Name}|{ready}|{host}|{m.Id == me}";
                if (shown == _shown)
                    return;
                _shown = shown;
                _id = m?.Id;
                _nameText = m?.Name;
                if (m == null)
                {
                    _name.text = "Open slot";
                    _name.color = ZombiesUi.Faint;
                    _status.text = "";
                    _dot.color = new Color(0.3f, 0.3f, 0.3f, 0.6f);
                    _root.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.5f);
                    return;
                }
                _root.GetComponent<Image>().color = Color.white;
                Color colour = i < ScoresHud.PlayerColours.Length ? ScoresHud.PlayerColours[i] : ZombiesUi.Ink;
                _name.text = m.Name + (m.Id == me ? " <color=#8a8680><size=70%>(YOU)</size></color>" : "");
                _name.color = colour;
                _status.text = host ? "HOST" : ready ? "READY" : "NOT READY";
                _status.color = host ? ZombiesUi.Gold : ready ? ZombiesUi.Ready : ZombiesUi.NotReady;
                _dot.color = ready ? ZombiesUi.Ready : ZombiesUi.NotReady;
            }
        }

        // ---------- A map's card (as on EFT's map screen: picture, name, description, the three facts) ----------

        private class MapCard
        {
            public readonly RectTransform Root;
            private readonly Image _picture;
            private readonly AspectRatioFitter _fit;
            private readonly TextMeshProUGUI _name, _desc, _time, _kind, _players;
            private string _shown;

            public MapCard(RectTransform parent, Vector2 size)
            {
                Root = ZombiesUi.PanelWithShadow(parent, "MapCard");
                Root.sizeDelta = size;
                float picH = size.y * 0.56f;
                RectTransform frame = ZombiesUi.Rect(Root, "Picture");
                frame.anchorMin = new Vector2(0f, 1f);
                frame.anchorMax = new Vector2(1f, 1f);
                frame.pivot = new Vector2(0.5f, 1f);
                frame.offsetMin = new Vector2(10f, -picH - 10f);
                frame.offsetMax = new Vector2(-10f, -10f);
                frame.gameObject.AddComponent<RectMask2D>();
                _picture = ZombiesUi.Img(frame, "Image", null, Color.white, false);
                RectTransform pr = _picture.rectTransform;
                pr.anchorMin = pr.anchorMax = new Vector2(0.5f, 0.5f);
                pr.pivot = new Vector2(0.5f, 0.5f);
                _fit = _picture.gameObject.AddComponent<AspectRatioFitter>();
                _fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                _fit.aspectRatio = 16f / 9f;
                Image fade = ZombiesUi.Img(frame, "Fade", ZombiesUi.FadeUp, new Color(0.035f, 0.03f, 0.028f, 0.95f), false);
                fade.rectTransform.anchorMin = new Vector2(0f, 0f);
                fade.rectTransform.anchorMax = new Vector2(1f, 0f);
                fade.rectTransform.pivot = new Vector2(0.5f, 0f);
                fade.rectTransform.offsetMin = Vector2.zero;
                fade.rectTransform.offsetMax = new Vector2(0f, picH * 0.45f);

                _name = ZombiesUi.Text(Root, "Name", "", 46f, ZombiesUi.Ink, TextAlignmentOptions.BottomLeft);
                _name.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(30f, -picH - 6f), new Vector2(size.x - 60f, 60f));
                _desc = ZombiesUi.Text(Root, "Description", "", 19f, ZombiesUi.Dim, TextAlignmentOptions.TopLeft);
                _desc.enableWordWrapping = true;
                _desc.enableAutoSizing = true;
                _desc.fontSizeMin = 14f;
                _desc.fontSizeMax = 19f;
                _desc.overflowMode = TextOverflowModes.Truncate;
                _desc.rectTransform.anchorMin = new Vector2(0f, 0f);
                _desc.rectTransform.anchorMax = new Vector2(1f, 1f);
                _desc.rectTransform.offsetMin = new Vector2(30f, 66f);
                _desc.rectTransform.offsetMax = new Vector2(-30f, -picH - 18f);

                Sprite[] icons = InfoIcons();
                _time = Fact(Root, 0, size.x, icons[0]);
                _kind = Fact(Root, 1, size.x, icons[1]);
                _players = Fact(Root, 2, size.x, icons[2]);
            }

            // The three facts spread evenly across the card: the first to the left edge, the middle centred, the last to
            // the right edge (icon + words sized to fit each)
            private static TextMeshProUGUI Fact(RectTransform root, int i, float width, Sprite icon)
            {
                float slot = (width - 60f) / 3f;
                RectTransform rt = ZombiesUi.Rect(root, "Fact " + i);
                rt.Place(new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(30f + i * slot, 18f), new Vector2(slot, 38f));
                var row = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
                row.childAlignment = i == 0 ? TextAnchor.MiddleLeft : i == 1 ? TextAnchor.MiddleCenter : TextAnchor.MiddleRight;
                row.spacing = 10f;
                row.childControlWidth = row.childControlHeight = true;
                row.childForceExpandWidth = row.childForceExpandHeight = false;
                if (icon != null)
                {
                    Image img = ZombiesUi.Img(rt, "Icon", icon, ZombiesUi.Ink, false);
                    img.preserveAspect = true;
                    var le = img.gameObject.AddComponent<LayoutElement>();
                    le.preferredWidth = le.preferredHeight = 32f;
                }
                TextMeshProUGUI t = ZombiesUi.Text(rt, "Text", "", 21f, ZombiesUi.Ink, TextAlignmentOptions.Left, false, 1f);
                t.overflowMode = TextOverflowModes.Overflow;
                return t;
            }

            public void Set(string map)
            {
                if (map == _shown)
                    return;
                _shown = map;
                JsonType.LocationSettings.Location loc = string.IsNullOrEmpty(map) ? null : ZombiesParty.Location(map);
                string name = ZombiesParty.MapName(map);
                string desc = "";
                try
                {
                    if (loc != null)
                    {
                        string d = (loc._Id + " Description").Localized();
                        if (!string.IsNullOrEmpty(d) && !d.EndsWith(" Description"))
                            desc = d;
                    }
                }
                catch
                {
                }
                _name.text = name;
                _desc.text = desc;
                _time.text = "EXFIL";
                _kind.text = "ROUND BASED";
                _players.text = "1-4";
                Sprite pic = string.IsNullOrEmpty(map) ? null : ZombiesMapSelect.Banner(map);
                _picture.sprite = pic;
                _picture.color = pic != null ? Color.white : new Color(0.12f, 0.1f, 0.1f, 1f);
                if (pic != null)
                    _fit.aspectRatio = pic.rect.width / Mathf.Max(1f, pic.rect.height);
            }

            // EFT's own little icons from its map screen's info panel (clock, fist, people), if it can be found
            private static Sprite[] _icons;

            private static Sprite[] InfoIcons()
            {
                if (_icons != null)
                    return _icons;
                _icons = new Sprite[3];
                try
                {
                    LocationInfoPanel panel = Resources.FindObjectsOfTypeAll<LocationInfoPanel>().FirstOrDefault(x => x != null);
                    if (panel == null)
                        return _icons;
                    TMP_Text[] texts = { panel._playTime, panel._difficulty, panel._players };
                    for (int i = 0; i < 3; i++)
                    {
                        if (texts[i] == null)
                            continue;
                        for (Transform t = texts[i].transform.parent; t != null && t != panel.transform && _icons[i] == null; t = t.parent)
                            _icons[i] = t.GetComponentsInChildren<Image>(true).FirstOrDefault(im => im != null && im.sprite != null
                                && !im.transform.IsChildOf(texts[i].transform) && im.rectTransform.rect.width < 80f)?.sprite;
                    }
                    ZombiesPlugin.Log.LogInfo($"Zombies menus: map card icons {string.Join(", ", _icons.Select(s => s?.name ?? "-"))}");
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies menus: EFT's map icons weren't found ({e.Message})");
                }
                return _icons;
            }
        }
    }
}
