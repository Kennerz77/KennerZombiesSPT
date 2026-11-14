using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using SPT.Reflection.Utils;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Kennerz Zombies Mode (planned: see ZOMBIES_PLAN.md beside the source). So far: the gun logger (building the box
    /// and wall-buy guns in EFT's own modding screen, then writing them down for the weapon table) and the original's mystery
    /// box, set out in the hideout to try, at two spots placed with F12's debug entries.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.SoftDependency)]
    // Pack-a-Punch, the perks, the Ray Gun and the Thundergun are part of zombies: all required (the user, 2026-11-08;
    // each of these mods leaves no trace in a profile when removed)
    [BepInDependency("com.kennerz.paphideoutmodule", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.kennerz.perkbeverages", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.kennerz.raygun", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.kennerz.thundergun", BepInDependency.DependencyFlags.HardDependency)]
    public class ZombiesPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.kennerz.zmcore"; // same GUID as the server mod will have (Forge rule)
        public const string PluginName = "Kennerz-ZM_Core";     // Forge rule: "Username-ModName"
        public const string PluginVersion = "0.1.2";

        internal static ManualLogSource Log;

        /// <summary>The plugin's folder (gun_library.jsonl is written here).</summary>
        internal static readonly string Dir = Path.GetDirectoryName(typeof(ZombiesPlugin).Assembly.Location);

        /// <summary>Handed over by the HideoutPlayerOwner.Init patch; Unity's null check makes it go null once destroyed.</summary>
        internal static HideoutPlayerOwner Owner;

        internal static ZombiesPlugin Instance;

        internal static HarmonyLib.Harmony HarmonyInstance;

        private void Awake()
        {
            // Release builds: F12 shows players only their own settings
            ReleaseMenu.Install(this, d => new[] { "0. Main menu", "2. Zombies sounds", "2b. Zombies music", "3. Mystery box volume", "3. Zombies controls", "6. Scores on screen", "10. Zombies menu sounds" }.Contains(d.Section) && (d.Section != "6. Scores on screen" || d.Key == "Show zombies left" || d.Key == "Player photos"));
            Log = Logger;
            Instance = this;
            // Where zombies maps go (BepInEx\plugins\SPZombies_Maps), made on first start
            MapEditor.EnsureMapsFolder();
            var harmony = new HarmonyLib.Harmony(PluginGuid);
            HarmonyInstance = harmony;
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.Init)),
                postfix: new HarmonyLib.HarmonyMethod(typeof(ZombiesPlugin), nameof(OwnerInitPostfix)));
            // The wonder weapons in the box, when their mods are installed
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.kennerz.raygun"))
                ZombiesApi.RegisterBoxWeapon("6a7e5a71d9a1160000000001", "Ray Gun", true);
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.kennerz.thundergun"))
                ZombiesApi.RegisterBoxWeapon("6a7e5a71d9a1150000000001", "Thundergun", true);
            ZombiesMapSelect.Patch(harmony);
            ZombiesLogo.Patch(harmony);
            Safely("main menu mode", () => ZombiesMainMenu.Patch(harmony));
            ZombiesPause.Patch(harmony);
            Safely("no scavs", () => ZombiesNoScavs.Patch(harmony));
            ZombiesExfilUi.Patch(harmony);
            ZombiesFireMode.Patch(harmony);
            Safely("controls", () => ZombiesControls.Patch(harmony));
            Safely("hipfire", () => ZombiesHipfire.Patch(harmony));
            Safely("leaving a solo match", () => ZombiesLeave.Patch(harmony));
            Safely("after-raid screens", () => ZombiesAfterRaid.Patch(harmony));
            Safely("world fog", () => ZombiesLook.Patch(harmony));
            Safely("war machine", () => ZombiesWarMachine.Patch(harmony));
            Safely("inertia", () => ZombiesInertia.Patch(harmony));
            Safely("controls keys", () => ZombiesControls.Bind(Config));
            Safely("no damage", () => ZombiesNoDamage.Patch(harmony));
            Safely("no gun malfunctions", () => ZombiesNoJams.Patch(harmony));
            Safely("no knife charges", () => ZombiesKnifeCharges.Patch(harmony));
            Safely("ray gun blasts", () => ZombiesRayBlast.Patch(harmony));
            Safely("knife swing event", () => ZombiesSwipe.Patch(harmony));
            Safely("shotgun spread", () => ZombiesShotguns.Patch(harmony));
            Safely("Sally", () => ZombiesSally.Patch(harmony));
            Safely("40mm shell blasts", () => ZombiesShells.Patch(harmony));
            Safely("gun names", () => ZombiesGunTable.PatchNames(harmony));
            Safely("gear free", () => ZombiesGearFree.Patch(harmony));
            Safely("reload guard", () => ZombiesReloadGuard.Patch(harmony));
            // Every gun counts as examined in a match, so EFT hands it back after a knife or a drink
            Safely("examined", () => ZombiesExamined.Patch(harmony));
            Safely("teammates' blasts and flybys", () => ZombiesBlastFeel.Patch(harmony));
            Safely("throw arm", () => ZombiesThrowArm.Patch(harmony));
            Safely("ray gun cells", () => ZombiesAmmo.PatchRaygunCells(harmony));
            Safely("war machine dump reload", () => ZombiesAmmo.PatchWarMachine(harmony));
            ZombiesRules.Patch(harmony);
            gameObject.AddComponent<ZombiesMenu>();
            gameObject.AddComponent<ZombiesMenuBackground>();
            gameObject.AddComponent<ZombiesReport>();
            gameObject.AddComponent<ZombiesStatsScreen>();
            gameObject.AddComponent<ZombiesFrontEnd>();
            ZombiesMenu.Bind(Config, "10. Zombies menu sounds");
            Safely("menu button sounds", () => ZombiesUi.Bind(Config, "10. Zombies menu sounds"));
            Safely("notifications", () => ZombiesNotices.Patch(harmony));
            StartCoroutine(ZombiesAssets.PreloadAsync());
            ZombiesSounds.Preload(this);
            StartCoroutine(LoadFikaBridge());
            BindBoxDebug();
            BindBoxVolume();
            gameObject.AddComponent<MapEditor>();
            MapEditor.Bind(Config);
            gameObject.AddComponent<Moonlight>();
            Moonlight.Bind(Config, "7. Moonlight");
            gameObject.AddComponent<ZombiesLook>();
            ZombiesLook.Bind(Config, "8. Rain");
            gameObject.AddComponent<ZombiesExfilHud>();
            Safely("exfil", () => ZombiesExfil.Bind(Config));
            ZombiesReport.Bind(Config, "0. Main menu");
            Safely("invite pop-ups", () => ZombiesParty.Bind(Config, "0. Main menu"));
            Safely("lobby leave question", () => ZombiesParty.Patch(harmony));
            gameObject.AddComponent<RainCycle>();
            RainCycle.Bind(Config, "8. Rain");
            gameObject.AddComponent<PowerLights>();
            PowerLights.Bind(Config, "9. Power (dev)");
            gameObject.AddComponent<ZombiesMatch>();
            ZombiesMatch.Bind(Config, (label, action) =>
                new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button(label, action) });
            // Each part's settings on their own: one that fails is logged and the rest still start (a bad key in the
            // debug section once stopped everything after it, the logging and sound settings with it)
            gameObject.AddComponent<ZombiesTrace>();
            Safely("debug sections", () => ZombiesDebug.Bind(Config, (label, action) =>
                new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button(label, action) }));
            Safely("logging", () => ZombiesTrace.Bind(Config, (label, action) =>
                new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button(label, action) }));
            Safely("reload rescue", () => ZombiesReload.Bind(Config));
            Safely("photo tour", () => ZombiesPhotos.Bind(Config, "19. Debug: logging"));
            Safely("sound volumes", () => ZombiesSounds.Bind(Config));

            const string tools = "1. Gun library";
            Config.Bind(tools, "Log my guns", false, new ConfigDescription(
                "Writes every gun you have equipped (primary, secondary, holster) with all its attachments to gun_library.jsonl " +
                "in this mod's folder: build it in the modding screen, equip it, press. This is the gun as the box or a wall gives it.",
                null, new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button("Log my guns", () => LogGuns(false)) }));
            Config.Bind(tools, "Log my guns as PaP", false, new ConfigDescription(
                "The same, marked as the Pack-a-Punched version (the attachments PaP adds): it pairs with the base gun of the same weapon.",
                null, new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button("Log my guns as PaP", () => LogGuns(true)) }));
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private static void Safely(string what, Action bind)
        {
            try
            {
                bind();
            }
            catch (Exception e)
            {
                Log.LogError($"Zombies: the {what} settings failed to start: {e}");
            }
        }

        private static void OwnerInitPostfix(HideoutPlayerOwner __instance) => Owner = __instance;

        /// <summary>The raid itself is on (the player is in), not still loading or deploying.</summary>
        internal static bool RaidStarted => Singleton<AbstractGame>.Instantiated && Singleton<AbstractGame>.Instance.Status == GameStatus.Started;

        internal static bool InHideout
        {
            get
            {
                HideoutPlayerOwner owner = Owner;
                return owner != null && owner.gameObject.activeInHierarchy && owner.HideoutPlayer != null;
            }
        }

        /// <summary>
        /// A real raid is on (not the hideout). EFT's hideout is a game and a world of its own (EFT.HideoutGame,
        /// HideoutGameWorld) that stay up, "started", behind the main menu after a visit, so <see cref="RaidStarted"/>
        /// plus "a GameWorld with a player" were true at the menu too (user, 2026-11-08: the mode switch and the zombies
        /// menu music gone after a hideout trip).
        /// </summary>
        internal static bool InRealRaid
        {
            get
            {
                if (!RaidStarted || Singleton<AbstractGame>.Instance is HideoutGame)
                    return false;
                if (!Singleton<GameWorld>.Instantiated)
                    return false;
                GameWorld world = Singleton<GameWorld>.Instance;
                return world != null && !(world is HideoutGameWorld) && world.MainPlayer != null;
            }
        }

        /// <summary>
        /// At the menus: no real raid on and not walking round the hideout. Not "no GameWorld": the hideout's world stays
        /// up behind the main menu after a visit (the MATCH REPORT / STATISTICS screens never opened after one, 2026-11-09).
        /// </summary>
        internal static bool AtMenus => !InRealRaid && !InHideoutNow;

        // Screens only ever seen at the main menu and on the way into a raid, never inside the hideout
        private static readonly System.Collections.Generic.HashSet<EFT.UI.Screens.EEftScreenType> MenuScreens =
            new System.Collections.Generic.HashSet<EFT.UI.Screens.EEftScreenType>
            {
                EFT.UI.Screens.EEftScreenType.MainMenu, EFT.UI.Screens.EEftScreenType.SelectRaidSide,
                EFT.UI.Screens.EEftScreenType.SelectLocation, EFT.UI.Screens.EEftScreenType.OfflineRaid,
                EFT.UI.Screens.EEftScreenType.Insurance, EFT.UI.Screens.EEftScreenType.MatchMakerAccept,
                EFT.UI.Screens.EEftScreenType.TimeHasCome, EFT.UI.Screens.EEftScreenType.KeyAccess,
                EFT.UI.Screens.EEftScreenType.MapPoints,
            };

        /// <summary>
        /// Walking round the hideout now. EFT keeps the hideout loaded after a visit (its player and owner stay up behind
        /// the main menu), so <see cref="InHideout"/> alone was still true back at the menu: the zombies menu went
        /// silent and the mode switch vanished (user, 2026-11-08). On a main-menu / raid-setup screen it's never the hideout.
        /// </summary>
        internal static bool InHideoutNow
        {
            get
            {
                if (!InHideout)
                    return false;
                try
                {
                    return !(Singleton<EFT.UI.Screens.IScreenManager>.Instance?.CurrentScreenType is EFT.UI.Screens.EEftScreenType t && MenuScreens.Contains(t));
                }
                catch
                {
                    return true;
                }
            }
        }

        /// <summary>The player using things right now: the hideout's, or the raid's main player (and its owner).</summary>
        /// <summary>
        /// The wonder weapons anyone in the lobby holds right now. Solo that's the player's own guns; with Fika every
        /// player's (the shared match state, still to come).
        /// </summary>
        internal static HashSet<string> WonderWeaponsOut(HashSet<string> mine) => new HashSet<string>(mine);

        internal static bool CurrentPlayer(out GamePlayerOwner owner, out Player player)
        {
            if (InHideout)
            {
                owner = Owner;
                player = Owner.HideoutPlayer;
                return player != null;
            }
            player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            owner = player != null ? ((Component)player).GetComponent<GamePlayerOwner>() : null;
            return player != null && owner != null;
        }

        // ---------- Mystery box in the hideout (to try it) ----------

        // The box's two spots (placed in game 2026-11-01; "Bring spot N here", arrows, "Log box spots" to change)
        private static readonly (Vector3, Quaternion)[] DefaultSpots =
        {
            // Turned 180 from the logged 189.19 / 13.58: the lid's hinge was on the player's side (2026-11-01)
            (new Vector3(10.8280f, 0.0202f, 6.9805f), Quaternion.Euler(0f, 9.19f, 0f)),
            (new Vector3(8.4660f, 0.0202f, 2.1126f), Quaternion.Euler(0f, 193.58f, 0f)),
        };

        private static readonly (Vector3 position, Quaternion rotation)?[] Moved = new (Vector3, Quaternion)?[2];

        internal static (Vector3 position, Quaternion rotation) SpotPlace(int spot) => Moved[spot] ?? DefaultSpots[spot];

        internal static int OtherSpot(int spot) => spot == 0 ? 1 : 0;

        private static ConfigEntry<bool> NudgeEnabled;
        internal static ConfigEntry<bool> OtherModsGuns;
        private readonly BoxFake[] _fakes = new BoxFake[2];

        /// <summary>The empty spot's cases and teddy: shown while the box isn't at that spot.</summary>
        internal void ShowFake(int spot, bool on)
        {
            if (spot < 0 || spot >= _fakes.Length)
                return;
            if (_fakes[spot] == null && on)
            {
                GameObject prefab = ZombiesAssets.Fake();
                if (prefab == null)
                    return;
                _fakes[spot] = Instantiate(prefab).AddComponent<BoxFake>();
                _fakes[spot].Init(SpotPlace(spot));
            }
            if (_fakes[spot] != null)
                _fakes[spot].Show(on);
        }

        private void ClearFakes()
        {
            for (int i = 0; i < _fakes.Length; i++)
            {
                if (_fakes[i] != null)
                    Destroy(_fakes[i].gameObject);
                _fakes[i] = null;
            }
        }
        private static ConfigEntry<float> VolumeAll, VolumeLid, VolumeMusic, VolumeSpin, VolumeTeddy, VolumeMove;

        private void BindBoxVolume()
        {
            const string audio = "3. Mystery box volume";
            ConfigDescription Range(string text) => new ConfigDescription(text, new AcceptableValueRange<float>(0f, 1f));
            VolumeAll = Config.Bind(audio, "All box sounds", 0.296f, Range("Every mystery box sound together."));
            VolumeLid = Config.Bind(audio, "Lid", 1f, Range("The lid opening and closing."));
            VolumeMusic = Config.Bind(audio, "Music box", 1f, Range("The music box tune while it spins."));
            VolumeSpin = Config.Bind(audio, "Spin", 1f, Range("The guns spinning."));
            VolumeTeddy = Config.Bind(audio, "Teddy (laugh, bye bye)", 0.859f, Range("The laugh and the \"bye bye\" when the teddy comes up."));
            VolumeMove = Config.Bind(audio, "Box moving", 1f, Range("The whoosh, poof and disappearing as the box leaves, and its landing."));
        }

        /// <summary>The volume slider for one of the box's sounds (by its audio object's name), times "All box sounds".</summary>
        internal static float BoxVolume(string sound)
        {
            ConfigEntry<float> part = sound == "Open" || sound == "Close" ? VolumeLid
                : sound == "Music" ? VolumeMusic
                : sound == "Spin" ? VolumeSpin
                : sound == "Laugh" || sound == "ByeBye" ? VolumeTeddy
                : VolumeMove;
            return (VolumeAll?.Value ?? 1f) * (part?.Value ?? 1f);
        }
        private MysteryBox _box, _raidBox;
        private bool _boxLoading;

        private void BindBoxDebug()
        {
            const string debug = "2. Mystery box (debug)";
            for (int i = 0; i < 2; i++)
            {
                int spot = i;
                Config.Bind(debug, $"Bring spot {spot + 1} here", false, new ConfigDescription(
                    $"Moves the box's spot {spot + 1} 2.5 m in front of you, facing you, and puts the box there. Fine-tune with the arrow keys, then \"Log box spots\".",
                    null, new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button($"Spot {spot + 1} here", () => BringSpot(spot)) }));
            }
            NudgeEnabled = Config.Bind(debug, "Arrow-key placement", false,
                "Moves the box (and the spot it stands on) with the keyboard: arrows forward / back / left / right as you face, Shift + Up / Down " +
                "raises and lowers it, Alt + Left / Right turns it. A tap is one small step; hold to keep going. Turn it off when done.");
            Config.Bind(debug, "Log box spots", false, new ConfigDescription(
                "Writes both spots' positions and turns to BepInEx\\LogOutput.log (for saving them in the mod).", null,
                new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button("Log box spots", LogSpots) }));
            Config.Bind(debug, "Spawn box here (raid test)", false, new ConfigDescription(
                "In a raid: puts a mystery box 2.5 m in front of you, to take its guns for real (testing only: replacing a gun " +
                "deletes the one in your hands). The zombies mode will have its own boxes.", null,
                new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button("Box here (raid)", SpawnRaidBox) }));
            OtherModsGuns = Config.Bind(debug, "Other mods guns in the box", false,
                "Adds every gun other mods add to the game (found by their models coming from a mod's bundle) to the mystery box, " +
                "as their default presets. Off by default. Takes effect the next time the box is set out (go back into the hideout).");
            OtherModsGuns.SettingChanged += (_, __) => BoxGuns.Reload();
            Config.Bind(debug, "Teddy on the next spin", false, new ConfigDescription(
                "The next spin brings up the teddy bear, so the box moves to its other spot.", null,
                new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button("Teddy next", () => { MysteryBox.ForceTeddy = true; Notify("The next spin brings the teddy.", false); }) }));
            foreach ((string id, string name) in new[] { ("raygun", "Ray Gun"), ("thundergun", "Thundergun"), (BoxGuns.MonkeyId, "Cymbal Monkey") })
            {
                Config.Bind(debug, $"Get the {name} from the box", false, new ConfigDescription(
                    $"The next spin of the box stops on the {name} (even if you carry one).", null,
                    new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button($"{name} next", () => { MysteryBox.ForceGun = id; Notify($"The next spin brings the {name}.", false); }) }));
                Config.Bind(debug, $"Give the {name} now", false, new ConfigDescription(
                    $"Hands you the {name} straight away, as if taken from the box.", null,
                    new ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ => Button($"Give {name}", () =>
                    {
                        BoxGuns.Gun g = BoxGuns.BoxGunList().FirstOrDefault(x => x.Id == id) ?? BoxGuns.FindGun(id);
                        if (g != null && CurrentPlayer(out GamePlayerOwner _o, out Player p))
                            StartCoroutine(BoxGive.Give(g, p));
                        else
                            Notify($"No {name} (or not in a raid).", true);
                    }) }));
            }
        }

        // Fika's part of the zombies mode (Kennerz_ZM_CoreFika.bridge, built against Fika): loaded only when Fika is
        // installed, once every plugin is in. This DLL never touches a Fika type itself (it would fail without Fika)
        private System.Collections.IEnumerator LoadFikaBridge()
        {
            yield return null;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core"))
            {
                Log.LogInfo("Fika isn't installed: zombies plays solo");
                yield break;
            }
            string file = System.IO.Path.Combine(Dir, "Kennerz_ZM_CoreFika.bridge");
            try
            {
                System.Reflection.Assembly bridge = System.Reflection.Assembly.LoadFrom(file);
                bridge.GetType("KennerZombiesFika.FikaBridge")?.GetMethod("Start")?.Invoke(null, null);
                Log.LogInfo("Zombies over Fika ready");
            }
            catch (Exception e)
            {
                Log.LogError($"Zombies over Fika couldn't start ({e.GetBaseException().Message}): matches stay solo");
            }
        }

        // The hideout's test box (development only): the box is the zombies mode's, never in a player's hideout
        private const bool HideoutTestBox = false;

        private void Update()
        {
            bool inHideout = InHideout;
            if (HideoutTestBox && inHideout && _box == null && !_boxLoading && ZombiesAssets.Ready)
                SpawnBox();
            else if (!inHideout && _box != null)
            {
                Destroy(_box.gameObject);
                _box = null;
                ClearFakes();
                for (int i = 0; i < Moved.Length; i++)
                    Moved[i] = null;
            }
            if (inHideout && NudgeEnabled.Value)
                Nudge();
            ZombiesMainMenu.Tick();
        }

        private void SpawnBox()
        {
            GameObject prefab = ZombiesAssets.Box();
            if (prefab == null)
            {
                _boxLoading = true; // a bad bundle: don't try every frame
                return;
            }
            GameObject go = Instantiate(prefab);
            _box = go.AddComponent<MysteryBox>();
            _box.Init(0, SpotPlace(0));
            // Every other spot shows the old cases with the teddy
            ClearFakes();
            for (int i = 0; i < _fakes.Length; i++)
                if (i != _box.Spot)
                    ShowFake(i, true);
        }

        private void SpawnRaidBox()
        {
            if (InHideout || !CurrentPlayer(out _, out Player player) || !ZombiesAssets.Ready)
            {
                Notify("Only in a raid (the hideout has its own box).", true);
                return;
            }
            if (_raidBox != null)
                Destroy(_raidBox.gameObject);
            Transform body = ((Component)player).transform;
            Camera camera = ZombiesInteraction.ActiveCamera;
            Vector3 flat = Vector3.ProjectOnPlane(camera != null ? camera.transform.forward : body.forward, Vector3.up).normalized;
            Vector3 at = body.position + flat * 2.5f;
            // Onto the ground under that spot
            if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 4f, ~0, QueryTriggerInteraction.Ignore))
                at = ground.point;
            _raidBox = Instantiate(ZombiesAssets.Box()).AddComponent<MysteryBox>();
            _raidBox.Init(0, (at, Quaternion.LookRotation(flat, Vector3.up)));
            _raidBox.RaidTest = true;
            Log.LogInfo($"Raid test box at ({at.x:F2}, {at.y:F2}, {at.z:F2})");
        }

        private void BringSpot(int spot)
        {
            HideoutPlayer player = Owner?.HideoutPlayer;
            if (!InHideout || player == null)
            {
                Notify("Go to the hideout first.", true);
                return;
            }
            Transform body = ((Component)player).transform;
            Camera camera = ZombiesInteraction.ActiveCamera;
            Vector3 look = camera != null ? camera.transform.forward : body.forward;
            Vector3 flat = Vector3.ProjectOnPlane(look, Vector3.up).normalized;
            Vector3 at = body.position + flat * 2.5f;
            // Its hinge away from the player, as in the original: the lid opens up behind the gun (the model's +Z is the hinge side)
            Quaternion rotation = Quaternion.LookRotation(flat, Vector3.up);
            Moved[spot] = (at, rotation);
            if (_box != null && _box.CanMove)
            {
                _box.MoveTo(at, rotation);
                if (_box.Spot != spot)
                {
                    // Show this spot's box: put it here as the current spot
                    Destroy(_box.gameObject);
                    _box = Instantiate(ZombiesAssets.Box()).AddComponent<MysteryBox>();
                    _box.Init(spot, (at, rotation));
                }
            }
            Log.LogInfo($"Box spot {spot + 1} brought to ({at.x:F3}, {at.y:F3}, {at.z:F3}), rotation y {rotation.eulerAngles.y:F2}.");
            Notify($"Box spot {spot + 1} moved here. Fine-tune it with the arrow keys.", false);
        }

        // One tap: 1 cm or 1 degree. Held past the delay, a step every repeat interval
        private const float StepMetres = 0.01f, StepDegrees = 1f, RepeatDelay = 0.35f, RepeatInterval = 0.03f;
        private static readonly KeyCode[] NudgeKeys = { KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow };
        private float _nextRepeat;

        /// <summary>F12 debug: arrow keys move the box (and its current spot), relative to where the player is looking.</summary>
        private void Nudge()
        {
            if (_box == null || !_box.CanMove)
                return;
            KeyCode key = NudgeKeys.FirstOrDefault(Input.GetKey);
            if (key == KeyCode.None)
                return;
            if (Input.GetKeyDown(key))
                _nextRepeat = Time.unscaledTime + RepeatDelay;
            else if (Time.unscaledTime >= _nextRepeat)
                _nextRepeat = Time.unscaledTime + RepeatInterval;
            else
                return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            Transform t = _box.transform;
            if (alt)
            {
                if (key == KeyCode.LeftArrow) t.Rotate(Vector3.up, -StepDegrees, Space.World);
                else if (key == KeyCode.RightArrow) t.Rotate(Vector3.up, StepDegrees, Space.World);
                else return;
            }
            else if (shift)
            {
                if (key == KeyCode.UpArrow) t.position += Vector3.up * StepMetres;
                else if (key == KeyCode.DownArrow) t.position += Vector3.down * StepMetres;
                else return;
            }
            else
            {
                Camera camera = ZombiesInteraction.ActiveCamera;
                Transform view = camera != null ? camera.transform : ((Component)Owner.HideoutPlayer).transform;
                Vector3 forward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                Vector3 step = key == KeyCode.UpArrow ? forward : key == KeyCode.DownArrow ? -forward : key == KeyCode.RightArrow ? right : -right;
                t.position += step * StepMetres;
            }
            Moved[_box.Spot] = (_box.SpotPosition, t.rotation);
        }

        private void LogSpots()
        {
            for (int i = 0; i < 2; i++)
            {
                (Vector3 p, Quaternion r) = SpotPlace(i);
                Log.LogInfo($"[box spots] spot {i + 1}: position new Vector3({p.x:F4}f, {p.y:F4}f, {p.z:F4}f), rotation y {r.eulerAngles.y:F2}" +
                            (Moved[i] == null ? " (default, not placed yet)" : ""));
            }
            Notify("Box spots written to BepInEx\\LogOutput.log.", false);
        }

        internal static void Notify(string message) => Notify(message, false);

        /// <summary>The F12 menu (BepInEx Configuration Manager) draws entries by this class's field names.</summary>
        internal sealed class ConfigurationManagerAttributes
        {
            public bool? HideDefaultButton;
            public Action<ConfigEntryBase> CustomDrawer;
            public int? Order;
            public bool? HideSettingName;
        }

        private static void Button(string label, Action action)
        {
            if (GUILayout.Button(label, GUILayout.ExpandWidth(true)))
                action();
        }

        // ---------- Gun logger ----------

        private static readonly EquipmentSlot[] GunSlots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };

        private static void LogGuns(bool pap)
        {
            try
            {
                Profile profile = CurrentProfile();
                if (profile?.Inventory?.Equipment == null)
                {
                    Notify("No character loaded yet (go to the main menu, stash or hideout first).", true);
                    return;
                }
                var lines = new List<string>();
                foreach (EquipmentSlot slot in GunSlots)
                {
                    if (profile.Inventory.Equipment.GetSlot(slot).ContainedItem is Weapon weapon)
                    {
                        lines.Add(GunJson(weapon, slot, pap));
                        Log.LogInfo($"[guns] logged {(pap ? "PaP " : "")}{weapon.LocalizedName()} ({weapon.TemplateId}) from {slot}");
                    }
                }
                if (lines.Count == 0)
                {
                    Notify("No guns equipped: put the gun in your primary, secondary or holster slot.", true);
                    return;
                }
                string file = Path.Combine(Dir, "gun_library.jsonl");
                File.AppendAllLines(file, lines, new UTF8Encoding(false));
                Notify($"Logged {lines.Count} gun(s){(pap ? " as PaP" : "")} to gun_library.jsonl.", false);
            }
            catch (Exception e)
            {
                Log.LogError($"[guns] logging failed: {e}");
                Notify("Logging the guns failed (see LogOutput.log).", true);
            }
        }

        /// <summary>The profile on screen: the hideout's, a raid's, or the menus' (stash, modding screen).</summary>
        private static Profile CurrentProfile()
        {
            if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null)
                return Singleton<GameWorld>.Instance.MainPlayer.Profile;
            return ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile;
        }

        /// <summary>One gun as a JSON line: what it is, and every attachment by slot, nested as built.</summary>
        private static string GunJson(Weapon weapon, EquipmentSlot from, bool pap)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Field(sb, "logged", DateTime.Now.ToString("s")).Append(',');
            Field(sb, "kind", pap ? "pap" : "base").Append(',');
            Field(sb, "slot", from.ToString()).Append(',');
            Field(sb, "name", weapon.LocalizedName()).Append(',');
            Field(sb, "tpl", weapon.TemplateId.ToString()).Append(',');
            Field(sb, "caliber", weapon.AmmoCaliber ?? "").Append(',');
            Magazine mag = weapon.GetCurrentMagazine();
            sb.Append("\"magSize\":").Append(mag != null ? mag.MaxCount : 0).Append(',');
            Item round = mag?.Cartridges?.Items?.FirstOrDefault() ?? weapon.Chambers?.FirstOrDefault()?.ContainedItem;
            Field(sb, "ammoTpl", round?.TemplateId.ToString() ?? "").Append(',');
            Field(sb, "ammoName", round != null ? round.LocalizedName() : "").Append(',');
            Field(sb, "fireMode", weapon.SelectedFireMode.ToString()).Append(',');
            sb.Append("\"parts\":");
            Parts(sb, weapon);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>The item's filled slots: [{slot, name, tpl, parts}], nested.</summary>
        private static void Parts(StringBuilder sb, Item item)
        {
            sb.Append('[');
            bool first = true;
            if (item is CompoundItem compound && compound.Slots != null)
            {
                foreach (Slot slot in compound.Slots)
                {
                    Item part = slot.ContainedItem;
                    // Rounds in the chamber aren't attachments
                    if (part == null || part is Ammo)
                        continue;
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append('{');
                    Field(sb, "slot", slot.ID).Append(',');
                    Field(sb, "name", part.LocalizedName()).Append(',');
                    Field(sb, "tpl", part.TemplateId.ToString()).Append(',');
                    State(sb, part);
                    sb.Append("\"parts\":");
                    Parts(sb, part);
                    sb.Append('}');
                }
            }
            sb.Append(']');
        }

        /// <summary>
        /// A part's settings as set on it: a sight's selected scope, reticle/mode per scope and zoom; a light or laser's
        /// on/off and mode. Written as "sight" / "light" fields (each followed by a comma), nothing for other parts.
        /// </summary>
        private static void State(StringBuilder sb, Item part)
        {
            SightComponent sight = part.GetItemComponent<SightComponent>();
            if (sight != null)
            {
                sb.Append("\"sight\":{\"scope\":").Append(sight.SelectedScope)
                  .Append(",\"modes\":[").Append(string.Join(",", sight.ScopesSelectedModes ?? new int[0])).Append(']')
                  .Append(",\"zoom\":").Append(sight.ScopeZoomValue.ToString(System.Globalization.CultureInfo.InvariantCulture))
                  .Append("},");
            }
            LightComponent light = part.GetItemComponent<LightComponent>();
            if (light != null)
                sb.Append("\"light\":{\"on\":").Append(light.IsActive ? "true" : "false").Append(",\"mode\":").Append(light.SelectedMode).Append("},");
        }

        private static StringBuilder Field(StringBuilder sb, string key, string value) =>
            sb.Append('"').Append(key).Append("\":\"").Append(Escape(value)).Append('"');

        private static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

        internal static void Notify(string message, bool warning)
        {
            if (warning)
            {
                // Warnings go to the log too, to find them afterwards
                Log.LogWarning(message);
                NotificationManager.DisplayWarningNotification(message, ENotificationDurationType.Default);
            }
            else
                NotificationManager.DisplayMessageNotification(message, ENotificationDurationType.Default, ENotificationIconType.Default, null);
        }
    }
}
