using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Weather;
using UnityEngine;
using UnityEngine.AI;

namespace KennerZombies
{
    /// <summary>
    /// The map loader (dev, first step of a zombies match): in a raid on a map with a saved layout (maps\&lt;map&gt;.json
    /// from the map editor) it sets the layout out for real: blockers and play-area walls go solid (and invisible),
    /// doors and debris go solid and are bought away with points, box spots, machines and wall buys show as their
    /// models (MapVisual), and the points show the original-style (ScoresHud). Off by default; the map editor's "Test layout"
    /// turns it on. Normal bots and extracts stay until the real zombies match takes over.
    /// </summary>
    internal class ZombiesMatch : MonoBehaviour
    {
        // Collides with players moving (as the PaP machine and the box); bullets go through for now
        private const string CollisionLayer = "LowPolyCollider";
        public const int StartPoints = 500; // The original's

        private static ConfigEntry<bool> _enabled, _showBlockers;

        public static void Bind(ConfigFile config, Func<string, Action, ZombiesPlugin.ConfigurationManagerAttributes> drawer)
        {
            const string s = "5. Zombies match (dev)";
            _enabled = config.Bind(s, "Build the layout in raids", false,
                "In a raid on a map with a saved layout (maps\\<map>.json): makes its blockers and walls solid and its doors and " +
                "debris solid and buyable with points, as a zombies match will. Dev only: normal bots and extracts stay. Off for normal play.");
            _showBlockers = config.Bind(s, "Show blockers", false,
                "Shows the blockers and play-area walls (invisible in a match) so you can see where they are.");
            _showBlockers.SettingChanged += (_, __) => Instance?.ShowBlockers();
            Add(config, s, "Rebuild layout", "Sets the saved layout out again (after saving changes in the map editor). Points go back to 500.",
                () => Instance?.Rebuild(), drawer);
            Add(config, s, "Go to player start", "Puts you on the layout's player start.", () => Instance?.GoToStart(), drawer);
            Add(config, s, "Give 5000 points", "Adds 5000 points (to try buying doors and debris).", () => AddPoints(5000), drawer);
            ZombiesStamina.Bind(config, s);
            ZombiesInertia.Bind(config, s);
            ZombiesGround.Bind(config, s);
            ZombiesGuns.Bind(config, s);
            ZombiesHipfire.Bind(config, s);
            ZombiesVox.Bind(config, s);
            BoxGive.Bind(config, s);
            ZombiesMonkeyHands.Bind(config, s);
            ZombiesMonkeyView.Bind(config, "21. Monkey bomb");
            ZombiesMonkey.Bind(config, "21. Monkey bomb");
            ZombiesFireMode.Bind(config, s);
            ZombiesBarricade.Bind(config, s);
            ZombiesRounds.Bind(config, s);
            // Newer parts each on their own: one failing must never stop the binds after it (the whole set-up)
            try { ZombiesVault.Bind(config, s); } catch (Exception e) { ZombiesPlugin.Log.LogError("ZombiesVault.Bind failed: " + e); }
            try { ZombiesTrails.Bind(config, s); } catch (Exception e) { ZombiesPlugin.Log.LogError("ZombiesTrails.Bind failed: " + e); }
            ZombiesHealth.Bind(config);
            ScoresHud.Bind(config, "6. Scores on screen");
        }

        /// <summary>The map editor's "Test layout": turns the layout on and builds it again (from the file just saved).</summary>
        public static void StartTest()
        {
            _enabled.Value = true;
            // Already on in this raid: build again from the saved file (else the next frame builds it)
            if (Instance != null && Instance._location != null)
                Instance.Build();
        }

        /// <summary>The map editor closed in a zombies match: the match is built again from the layout just saved.</summary>
        public static void RebuildNow()
        {
            if (Instance != null && Instance._location != null)
                Instance.Build();
        }

        /// <summary>The map editor opened again: the test layout goes.</summary>
        public static void StopTest()
        {
            if (_enabled.Value)
                _enabled.Value = false;
        }

        private static void Add(ConfigFile config, string section, string name, string text, Action action,
            Func<string, Action, ZombiesPlugin.ConfigurationManagerAttributes> drawer) =>
            config.Bind(section, name, false, new ConfigDescription(text, null, drawer(name, action)));

        // ---------- Points (the match state; Fika will share it later) ----------

        public static int Points { get; private set; }

        /// <summary>The power switch has been thrown this match (perks, Pack-a-Punch and the exfil need it).</summary>
        public static bool PowerIsOn { get; private set; }

        public static void PowerOn()
        {
            if (PowerIsOn)
                return;
            PowerIsOn = true;
            ZombiesNet.Power();
            // A map powered from the start (a client hearing the host's power before its own build): no sting, no flicker
            if (Instance?._map != null && Instance._map.AlwaysPowered)
            {
                ZombiesPower.OnFromStart(Instance);
                return;
            }
            ZombiesPower.On(Instance);
            ZombiesPlugin.Log.LogInfo("Zombies match: power on");
            ZombiesPlugin.Notify("The power is on.");
        }

        /// <summary>Back in after a drop: the points the host last saw (no pop, no stats).</summary>
        /// <summary>This player's own spawn spot this match (where a bled-out player comes back).</summary>
        public static Vector3? StartSpot => Instance?._startPos;

        public static void RestorePoints(int points)
        {
            if (points <= Points)
                return;
            Points = points;
            ZombiesNet.Points(Points);
            ZombiesPlugin.Log.LogInfo($"Zombies: back in the match, {points} points given back");
            ZombiesPlugin.Notify($"Welcome back: {points} points.", false);
        }

        public static void AddPoints(int amount)
        {
            if (Instance == null || !Instance.Built)
            {
                ZombiesPlugin.Notify("Build the layout in a raid first.", true);
                return;
            }
            // Double Points: everything earned doubled
            if (amount > 0 && ZombiesPowerUps.DoublePoints)
                amount *= 2;
            Points += amount;
            ZombiesDebug.Log($"points +{amount} = {Points}");
            ZombiesNet.Points(Points);
            if (amount > 0 && Active)
                ZombiesStats.Mine.Earned += amount;
            ScoresHud.PopPoints(amount);
        }

        /// <summary>
        /// An interaction option that costs points: the buy when there are enough, else the original's denied sound (the prompt
        /// stays usable, it just says no).
        /// </summary>
        public static Action Afford(int price, Action buy) => Points >= price ? buy : Deny;

        /// <summary>the original's "can't afford it" sound.</summary>
        public static void Deny()
        {
            ZombiesSounds.Head("zm_deny", 1f);
            ZombiesVox.Say("nomoney", 0.7f, 8f);
        }

        /// <summary>Points given back (the box's teddy): not earned (no Double Points, not in the stats), the spend undone.</summary>
        public static void Refund(int amount)
        {
            if (amount <= 0)
                return;
            Points += amount;
            ZombiesNet.Points(Points);
            if (Active)
                ZombiesStats.Mine.Spent = Mathf.Max(0, ZombiesStats.Mine.Spent - amount);
            ScoresHud.PopPoints(amount);
        }

        /// <summary>Takes the points if there are enough.</summary>
        public static bool Spend(int amount)
        {
            if (Points < amount)
                return false;
            Points -= amount;
            // The original's purchase accept on every purchase (classic zombies purchase_accept.wav)
            ZombiesSounds.Head("zm_purchase", 1f);
            ZombiesDebug.Log($"points -{amount} = {Points}");
            ZombiesNet.Points(Points);
            if (Active)
                ZombiesStats.Mine.Spent += amount;
            ScoresHud.PopPoints(-amount);
            return true;
        }

        // ---------- Shared with the team (ZombiesNet) ----------

        /// <summary>A layout entry's number (the same on every player's game: they all load the same layout).</summary>
        public static int EntryIndex(MapEditor.Entry e) => Instance?._map?.Entries.IndexOf(e) ?? -1;

        /// <summary>A door or debris bought by a teammate: it clears here too.</summary>
        public static void OpenRemote(int entry)
        {
            foreach (Buyable b in FindObjectsOfType<Buyable>())
                if (b.Entry != null && EntryIndex(b.Entry) == entry)
                    b.ClearRemote();
        }

        public static List<int> OpenedEntries() => Opened.ToList();
        internal static readonly HashSet<int> Opened = new HashSet<int>();

        /// <summary>The host's box spot.</summary>
        public static void BoxAt(int spot)
        {
            if (spot >= 0)
                Instance?._boxSpots?.ForceSpot(spot);
        }

        public static int BoxSpot() => Instance?._boxSpots?.Box != null ? Instance._boxSpots.Box.Spot : -1;

        // ---------- Running ----------

        internal static ZombiesMatch Instance;

        private string _location;
        private MapEditor.MapFile _map;

        /// <summary>The layout's entries in this match (none outside one).</summary>
        public static System.Collections.Generic.IReadOnlyList<MapEditor.Entry> EntriesHere =>
            (System.Collections.Generic.IReadOnlyList<MapEditor.Entry>)Instance?._map?.Entries ?? new MapEditor.Entry[0];

        /// <summary>The map this match is on.</summary>
        public static string LocationHere => Instance?._location;

        /// <summary>Perks a player may hold on this map: its layout's MaxPerks, else the original's four.</summary>
        public static int MaxPerksHere => Instance?._map != null && Instance._map.MaxPerks > 0 ? Instance._map.MaxPerks : 4;

        /// <summary>The gun players start (and respawn) with on this map: its layout's StartGun, else null (the M1911).</summary>
        public static string StartGunHere => string.IsNullOrWhiteSpace(Instance?._map?.StartGun) ? null : Instance._map.StartGun;

        /// <summary>A perk's price on this map: its layout's PerkPrices entry when set (above 0), else the mode's own.</summary>
        public static int PerkPrice(string id, int fallback) =>
            id != null && Instance?._map?.PerkPrices != null && Instance._map.PerkPrices.TryGetValue(id, out int p) && p > 0 ? p : fallback;
        private readonly List<GameObject> _built = new List<GameObject>();
        private readonly List<Renderer> _blockerRenderers = new List<Renderer>();

        private bool Built => _map != null;

        private void Awake() => Instance = this;

        /// <summary>A real zombies match is on (a zombies map, not a dev layout over a normal map): its rules apply.</summary>
        /// <summary>One step of a match's set-up: its failure is logged and the rest still runs (a patch that failed on
        /// Fika's player once skipped the loading screen and the starting kit).</summary>
        private static float _nextHitchLog, _nextGroundLog;

        /// <summary>
        /// The map's invisible walk boxes (KZ_NavProxies, made so the navmesh reaches places) stood a few cm above the
        /// floor in places, and players walked on them: to the others they floated. Each box with a real floor collider
        /// just under its top (up to 20 cm) is lowered onto it; a box with nothing under it stays (it is the floor there).
        /// </summary>
        private static void SettleWalkBoxes()
        {
            int solid = LayerMask.GetMask("Default", "HighPolyCollider", "LowPolyCollider", "Terrain");
            int moved = 0, kept = 0;
            foreach (Collider box in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (box == null || box.isTrigger || !InNavProxies(box.transform))
                    continue;
                Bounds b = box.bounds;
                float top = b.max.y;
                var hits = Physics.RaycastAll(new Vector3(b.center.x, top + 0.05f, b.center.z), Vector3.down, 0.3f, solid, QueryTriggerInteraction.Ignore);
                float floor = float.NegativeInfinity;
                foreach (RaycastHit h in hits)
                    if (h.collider != box && !InNavProxies(h.collider.transform) && h.point.y < top - 0.005f && h.point.y > floor)
                        floor = h.point.y;
                if (float.IsNegativeInfinity(floor) || top - floor > 0.2f)
                {
                    kept++;
                    continue;
                }
                box.transform.position += Vector3.down * (top - floor);
                moved++;
            }
            if (moved + kept > 0)
                ZombiesPlugin.Log.LogInfo($"Zombies match: {moved} walk boxes lowered onto the floor under them, {kept} left (nothing under them)");
        }

        private static bool InNavProxies(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name == "KZ_NavProxies")
                    return true;
            return false;
        }

        private static void Step(string what, System.Action step)
        {
            try { step(); }
            catch (System.Exception e) { ZombiesPlugin.Log.LogError($"Zombies match set-up: {what} failed: {e}"); }
        }

        public static bool Active => Instance != null && Instance._location != null && ZombiesMaps.Is(Instance._location);

        private void Update()
        {
            // A Fika headless has no player of its own in the raid (Fika never makes one): its match builds once the
            // raid's world is up
            bool inWorld = Singleton<GameWorld>.Instantiated && !ZombiesPlugin.InHideout
                && (Singleton<GameWorld>.Instance.MainPlayer != null || ZombiesNet.Headless && ZombiesPlugin.RaidStarted);
            string location = inWorld && !string.IsNullOrEmpty(Singleton<GameWorld>.Instance.LocationId)
                ? Singleton<GameWorld>.Instance.LocationId : null;
            if (location != null)
                WhatsThis.Tick();
            // A zombies map is always a zombies match; a dev layout over a normal map only with the dev switch on
            bool wanted = location != null && (ZombiesMaps.Is(location) || _enabled != null && _enabled.Value);
            if (!wanted)
            {
                if (_location != null)
                    Teardown();
                return;
            }
            if (location != _location)
            {
                _location = location;
                _teleportPending = true;
                Build();
                _teleportPending = false;
                if (ZombiesMaps.Is(location))
                {
                    Step("ZombiesSlaps.Reset", () => ZombiesSlaps.Reset());
                    Step("ZombiesDowned.Reset", () => ZombiesDowned.Reset());
                    Step("ZombiesBarricade.NewRound", () => ZombiesBarricade.NewRound());
                    Step("ZombiesStats.Reset", () => ZombiesStats.Reset());
                    Step("ZombiesStats.PatchInventoryKey", () => ZombiesStats.PatchInventoryKey(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesAmmo.Reset", () => ZombiesAmmo.Reset());
                    Step("ZombiesRules.PatchDamage", () => ZombiesRules.PatchDamage());
                    Step("ZombiesRules.PatchNoChecks", () => ZombiesRules.PatchNoChecks(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesGuns.Patch", () => ZombiesGuns.Patch(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesPerks.Patch", () => ZombiesPerks.Patch(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesReload.Patch", () => ZombiesReload.Patch(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesAchievements.Patch", () => ZombiesAchievements.Patch(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesRounds.PatchPace", () => ZombiesRounds.PatchPace(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesVault.Patch", () => ZombiesVault.Patch(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesGrenades.Patch", () => ZombiesGrenades.Patch(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesWidow.Patch", () => ZombiesWidow.Patch(ZombiesPlugin.HarmonyInstance));
                    Step("ZombiesPowerUps.Start", () => ZombiesPowerUps.Start());
                    Step("ZombiesSeenCheck.Clear", () => ZombiesSeenCheck.Clear());
                    Step("ZombiesPerks.Reset", () => ZombiesPerks.Reset());
                    Step("ZombiesDecals.Restore", () => ZombiesDecals.Restore(location));
                    Step("ZombiesGround.Apply", () => ZombiesGround.Apply());
                    _loadoutGiven = false;
                    // The starting M1911's models loading while the comic shows (they took up to 48 s at the kit)
                    StartCoroutine(BoxGive.Preload(ZombiesLoadout.Starter));
                    // The original's start: black until the kit is on and the pistol cocked, then a fade in
                    ZombiesLoadout.Ready = false;
                    ZombiesLoadout.Progress = 0.1f;
                    ZombiesLoadout.Stage = "Building the map";
                    _shownProgress = 0f;
                    LoadLore(location);
                    ZombiesTips.Begin(location);
                    ZombiesLoadingMusic.Begin(location);
                    ZombiesLogo.Restore();
                    ZombiesTrace.MatchStarted(location);
                    _blackSince = Time.realtimeSinceStartup;
                    _shownSince = _blackSince;
                    LoadedIn = false;
                    _headlessSince = -1f;
                    ZombiesVox.NewMatch();
                    ZombiesNet.NewMatch();
                    _fadeFrom = -1f;
                }
            }
            if (_map != null && Time.time < _reholdUntil && Time.time >= _nextRehold && !MapEditor.IsOpen)
            {
                _nextRehold = Time.time + 1f;
                HoldTimeAndWeather();
            }
            if (Active)
            {
                Player me = Singleton<GameWorld>.Instance.MainPlayer;
                ZombiesLobby.Tick();
                // A Fika headless: the match's host with no one playing on it
                if (ZombiesNet.Headless)
                {
                    TickHeadless();
                    return;
                }
                // The zombies kit once the player is in (inventory moves during the loading screen may not take)
                if (!_loadoutGiven && ZombiesPlugin.RaidStarted)
                {
                    _loadoutGiven = true;
                    // Into the starting zone while the screen is still black
                    if (_startPos.HasValue)
                    {
                        me.Teleport(_startPos.Value, false);
                        if (_startYaw.HasValue)
                            me.MovementContext.Rotation = new Vector2(_startYaw.Value, 0f);
                        ZombiesPlugin.Log.LogInfo($"Zombies match: start at {_startPos.Value} (zone {ZombiesZones.ZoneAt(_startPos.Value)}), facing {_startYaw?.ToString("0") ?? "as it was"}");
                    }
                    StartCoroutine(ZombiesLoadout.Apply(me));
                    ZombiesStamina.Apply(me);
                    ZombiesRules.RightStance(me);
                    ZombiesGearFree.Apply();
                    me.RecalculateEquipmentParams();
                    ZombiesInertia.Apply();
                    ZombiesStamina.PatchJump(ZombiesPlugin.HarmonyInstance);
                }
                ZombiesProfile.Frame();
                ZombiesProfile.Measure("ZombiesPause.Tick", () => ZombiesPause.Tick());
                ZombiesProfile.Measure("ZombiesFireMode.Tick", () => ZombiesFireMode.Tick(me));
                ZombiesProfile.Measure("ZombiesControls.Tick", () => ZombiesControls.Tick(me));
                ZombiesProfile.Measure("ZombiesKnife.Tick", () => ZombiesKnife.Tick(me));
                ZombiesProfile.Measure("ZombiesPhd.Tick", () => ZombiesPhd.Tick(me));
                ZombiesReload.WatchVanish(me);
                ZombiesProfile.Measure("ZombiesNoClimbSlide.Tick", () => ZombiesNoClimbSlide.Tick(me));
                ZombiesVault.Tick();
                ZombiesProfile.Measure("ZombiesMonkeyHands.Tick", () => ZombiesMonkeyHands.Tick(me));
                ZombiesProfile.Measure("ZombiesMonkeyView.Tick", () => ZombiesMonkeyView.Tick(me));
                ZombiesProfile.Measure("ZombiesPerks.Tick", () => ZombiesPerks.Tick(me));
                ZombiesDevFile.Tick();
                ZombiesProfile.Measure("ZombiesTeam.HideFikaPlates", () => ZombiesTeam.HideFikaPlates());
                ZombiesProfile.Measure("ZombiesRounds.HideAllZombieWeapons", () => ZombiesRounds.HideAllZombieWeapons());
                ZombiesProfile.Measure("ZombiesHudHide.Tick", () => ZombiesHudHide.Tick());
                ZombiesProfile.Measure("ZombiesGearFree.Tick", () => ZombiesGearFree.Tick(me));
                ZombiesProfile.Measure("ZombiesSeenCheck.Tick", () => ZombiesSeenCheck.Tick(me));
                ZombiesProfile.Measure("ZombiesMeleeReach.Tick", () => ZombiesMeleeReach.Tick());
                ZombiesProfile.Measure("ZombiesTeam.TickPresence", () => ZombiesTeam.TickPresence());
                ZombiesProfile.Measure("ZombiesFireMode.AutoTick", () => ZombiesFireMode.AutoTick(me));
                ZombiesProfile.Measure("ZombiesLasers.Tick", () => ZombiesLasers.Tick(me));
                // What the player stands on, every 20 s (players looked to float a little to the others: an invisible
                // collider over the visible floor would do it)
                if (me != null && Time.time >= _nextGroundLog && LoadedIn)
                {
                    _nextGroundLog = Time.time + 20f;
                    Vector3 at = me.Transform.position;
                    if (Physics.Raycast(at + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f, LayerMask.GetMask("Default", "HighPolyCollider", "LowPolyCollider", "Terrain"), QueryTriggerInteraction.Ignore))
                    {
                        string path = hit.collider.name;
                        for (Transform t = hit.collider.transform.parent; t != null; t = t.parent)
                            path = t.name + "/" + path;
                        var rend = hit.collider.GetComponent<Renderer>();
                        ZombiesDebug.Log($"ground: feet {at.y:0.000}, collider {path} (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}, {(rend != null && rend.enabled ? "drawn" : "invisible")}) top {hit.point.y:0.000}");
                    }
                }
                ZombiesProfile.Measure("ZombiesNet.TickPending", () => ZombiesNet.TickPending());
                ZombiesProfile.Measure("ZombiesLitter.Tick", () => ZombiesLitter.Tick());
                ZombiesProfile.Measure("ZombiesLitter.TickFresh", () => ZombiesLitter.TickFresh());
                ZombiesProfile.Measure("ZombiesReload.Tick", () => ZombiesReload.Tick());
                // Hitches (the user: "stuttery, like resetting every second"): long frames logged with the time, and
                // the player's speed, so a frame-time spike shows apart from a movement reset
                float dt = Time.unscaledDeltaTime;
                if (dt > 0.05f && Time.unscaledTime > _nextHitchLog)
                {
                    _nextHitchLog = Time.unscaledTime + 0.25f;
                    ZombiesProfile.Measure("ZombiesDebug.Log", () => ZombiesDebug.Log($"hitch {dt * 1000f:0} ms at {Time.unscaledTime:0.00} (speed {me?.MovementContext?.CharacterMovementSpeed:0.##}, smoothed {me?.MovementContext?.SmoothedCharacterMovementSpeed:0.##})"));
                }
                ZombiesProfile.Measure("ZombiesMonkey.Tick", () => ZombiesMonkey.Tick());
                ZombiesProfile.Measure("ZombiesVox.Tick", () => ZombiesVox.Tick(me));
                if (ZombiesDebug.MarkShotKey != null && ZombiesDebug.MarkShotKey.Value.IsDown())
                    ZombiesProfile.Measure("ZombiesGameOver.MarkShot", () => ZombiesGameOver.MarkShot());
                ZombiesProfile.Measure("ZombiesPowerUps.Tick", () => ZombiesPowerUps.Tick());
                ZombiesProfile.Measure("ZombiesHipfire.Tick", () => ZombiesHipfire.Tick());
                ZombiesProfile.Measure("ZombiesRules.Tick", () => ZombiesRules.Tick(me));
                ZombiesProfile.Measure("ZombiesStamina.Tick", () => ZombiesStamina.Tick(me));
                ZombiesProfile.Measure("ZombiesInertia.Tick", () => ZombiesInertia.Tick(me));
                ZombiesProfile.Measure("ZombiesPower.Tick", () => ZombiesPower.Tick());
                ZombiesProfile.Measure("ZombiesRounds.Tick", () => ZombiesRounds.Tick());
                ZombiesProfile.Measure("ZombiesDowned.Tick", () => ZombiesDowned.Tick(me));
                ZombiesProfile.Measure("ZombiesSlaps.Tick", () => ZombiesSlaps.Tick());
                ZombiesProfile.Measure("ZombiesAmmo.Tick", () => ZombiesAmmo.Tick(me));
                ZombiesAmmo.SyncLoaded(me);
                ZombiesReload.WatchEmpty(me);
                ZombiesProfile.Measure("ZombiesItems.Sweep", () => ZombiesItems.Sweep(me));
            }
        }

        private float _headlessSince = -1f;

        /// <summary>
        /// A Fika headless's frame: the host's half of the match only (rounds, zombies, power, power-ups, bodies,
        /// points from everyone's hits). Its own player (Fika's, nobody plays it) gets no kit, no start spot and none
        /// of the player's own ticks. Round 1 waits until every player's game says it's in (they sit through the
        /// comic), at most ComicAtMost after the lobby.
        /// </summary>
        private void TickHeadless()
        {
            _loadoutGiven = true;
            ZombiesLoadout.Ready = true;
            if (!LoadedIn && ZombiesPlugin.RaidStarted && !ZombiesLobby.Waiting)
            {
                float now = Time.realtimeSinceStartup;
                if (_headlessSince < 0f)
                    _headlessSince = now;
                // Every player's game ready (kit in, gun loaded here) -> Go to all, round 1 from then
                ZombiesNet.TickGo(ComicAtMost);
                if (ZombiesNet.GoNow)
                {
                    LoadedIn = true;
                    ZombiesPlugin.Log.LogInfo($"Zombies match (headless): everyone told to come in ({ZombiesTeam.List().Count} players), round 1 coming");
                }
            }
            ZombiesPause.Tick();
            ZombiesVault.Tick();
            ZombiesPerks.TickTeamHands();
            ZombiesRounds.HideAllZombieWeapons();
            ZombiesTeam.TickPresence();
            ZombiesNet.TickPending();
            ZombiesLitter.Tick();
            ZombiesLitter.TickFresh();
            ZombiesMonkey.Tick();
            ZombiesPowerUps.Tick();
            ZombiesPower.Tick();
            ZombiesRounds.Tick();
            ZombiesSlaps.Tick();
        }

        // Spectating moves the camera after EFT has placed it
        private void LateUpdate()
        {
            if (Active)
                ZombiesDowned.LateTick();
        }

        private float _reholdUntil, _nextRehold;
        private bool _loadoutGiven = true;

        // The match's own fade in from black (the original: black, the pistol cocks, fade in)
        // The comic shows at least ComicSeconds (the loading music's length); ready by then, you're in on the dot with the
        // 1911 cocking, else as soon as you are (the loading music keeps playing, looping, until then)
        private const float FadeTime = 1.5f, ComicSeconds = 25f, ComicAtMost = 120f;
        private bool _waitingForPlayers;
        private float _blackSince = -1f, _fadeFrom = -1f, _shownSince = -1f;

        /// <summary>The comic is gone and the player is in: rounds and the ambience start from here.</summary>
        public static bool LoadedIn { get; private set; }
        private static Texture2D _black;

        // The loading screen over the black: the map's lore picture (beside its layout: <map>_lore.jpg or .png) and a white
        // bar with the percentage (the raid starting, then the kit going on and the pistol loaded)
        private static Texture2D _lore, _white;
        private static string _loreFor;
        private float _shownProgress;
        private static GUIStyle _loadStyle, _stageStyle;

        private static void LoadLore(string location)
        {
            if (_loreFor == location)
                return;
            _loreFor = location;
            if (_lore != null)
                Destroy(_lore);
            _lore = null;
            try
            {
                string dir = Path.GetDirectoryName(MapEditor.FileFor(location));
                string file = new[] { ".jpg", ".png" }.Select(ext => Path.Combine(dir, location + "_lore" + ext)).FirstOrDefault(File.Exists);
                if (file == null)
                {
                    ZombiesPlugin.Log.LogInfo($"Zombies match: no lore picture for {location} ({location}_lore.jpg beside its layout): plain black loading screen");
                    return;
                }
                _lore = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "zm_lore" };
                _lore.LoadImage(File.ReadAllBytes(file));
                ZombiesPlugin.Log.LogInfo($"Zombies match: lore picture {Path.GetFileName(file)} ({_lore.width}x{_lore.height})");
            }
            catch (Exception e)
            {
                _lore = null;
                ZombiesPlugin.Log.LogWarning($"Zombies match: the lore picture couldn't be read: {e.Message}");
            }
        }

        private void DrawFadeIn()
        {
            // The headless loads in on its own (TickHeadless): nobody watches its screen
            if (_blackSince < 0f || ZombiesNet.Headless)
                return;
            float now = Time.realtimeSinceStartup;
            // The wait for the kit only counts once the raid has started (EFT's countdown can be long)
            if (!ZombiesPlugin.RaidStarted)
            {
                _blackSince = now;
                // EFT's countdown: the bar creeps on to a third while it runs
                ZombiesLoadout.Progress = Mathf.Min(0.35f, Mathf.Max(ZombiesLoadout.Progress, 0.1f) + Time.unscaledDeltaTime * 0.01f);
                ZombiesLoadout.Stage = "Waiting for the raid";
            }
            else if (ZombiesLoadout.Progress < 0.4f)
            {
                ZombiesLoadout.Progress = 0.4f;
                ZombiesLoadout.Stage = "Dropping in";
            }
            // The comic's time counts from the start, not through a Fika lobby
            if (ZombiesLobby.Waiting)
                _shownSince = now;
            float shown = now - _shownSince;
            bool go;
            if (ZombiesNet.Online)
            {
                // Over Fika: ready here = kit in, the comic's minimum shown, the headless has our gun; then wait for
                // the host's Go so everyone fades in at once (a player host decides it here)
                bool mine = ZombiesPlugin.RaidStarted && ZombiesLoadout.Ready && shown >= ComicSeconds && !ZombiesNet.HandsPending;
                ZombiesNet.TickReady(mine);
                if (ZombiesPlugin.RaidStarted && !ZombiesLobby.Waiting && !ZombiesNet.Headless)
                    ZombiesNet.TickGo(ComicAtMost);
                go = ZombiesNet.GoNow || (ZombiesPlugin.RaidStarted && shown > ComicAtMost + 30f);
                _waitingForPlayers = mine && !go;
                if (_waitingForPlayers)
                    ZombiesLoadout.Stage = "Waiting for players";
            }
            else
                go = (ZombiesLoadout.Ready && shown >= ComicSeconds) || (ZombiesPlugin.RaidStarted && shown > ComicAtMost);
            if (_fadeFrom < 0f && go)
            {
                _waitingForPlayers = false;
                _fadeFrom = now;
                LoadedIn = true; // the loading music fades out on its own from here
                ZombiesVox.Say("game_start", 0.8f, 600f);
                ZombiesSounds.Head("zm_1911_cock", 1f);
                ZombiesPlugin.Log.LogInfo($"Zombies match: loaded in after {shown:0.0} s of the comic (kit ready: {ZombiesLoadout.Ready})");
            }
            float alpha = _fadeFrom < 0f ? 1f : 1f - (now - _fadeFrom) / FadeTime;
            if (alpha <= 0f)
            {
                _blackSince = -1f;
                return;
            }
            if (_black == null)
            {
                _black = new Texture2D(1, 1);
                _black.SetPixel(0, 0, Color.black);
                _black.Apply();
            }
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            GUI.depth = -1000;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            float s = Screen.height / 1080f;
            float barY = Screen.height - 80f * s;
            if (_lore != null)
            {
                // The picture as big as fits above the bar, its own shape kept
                float maxW = Screen.width - 80f * s, maxH = barY - 60f * s;
                float k = Mathf.Min(maxW / _lore.width, maxH / _lore.height);
                float w = _lore.width * k, h = _lore.height * k;
                GUI.DrawTexture(new Rect((Screen.width - w) / 2f, 30f * s + (maxH - h) / 2f, w, h), _lore, ScaleMode.ScaleToFit);
            }
            // The bar: a white outline, filled as the loading goes (eased, never backwards)
            float target = ZombiesLoadout.Ready ? 1f : Mathf.Clamp01(ZombiesLoadout.Progress);
            _shownProgress = Mathf.Max(_shownProgress, Mathf.MoveTowards(_shownProgress, target, Time.unscaledDeltaTime * 0.8f));
            float barW = Mathf.Min(720f * s, Screen.width * 0.6f), barH = Mathf.Max(4f, 10f * s), x = (Screen.width - barW) / 2f;
            float line = Mathf.Max(1f, 2f * s), gap = line * 2f;
            GUI.DrawTexture(new Rect(x - gap - line, barY - gap - line, barW + 2f * (gap + line), line), _white);
            GUI.DrawTexture(new Rect(x - gap - line, barY + barH + gap, barW + 2f * (gap + line), line), _white);
            GUI.DrawTexture(new Rect(x - gap - line, barY - gap - line, line, barH + 2f * (gap + line)), _white);
            GUI.DrawTexture(new Rect(x + barW + gap, barY - gap - line, line, barH + 2f * (gap + line)), _white);
            GUI.DrawTexture(new Rect(x, barY, barW * _shownProgress, barH), _white);
            if (_loadStyle == null)
            {
                _loadStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
                _stageStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight };
            }
            _loadStyle.fontSize = Mathf.RoundToInt(22f * s);
            _stageStyle.fontSize = Mathf.RoundToInt(18f * s);
            _loadStyle.normal.textColor = new Color(1f, 1f, 1f, alpha);
            _stageStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f, alpha);
            Rect under = new Rect(x, barY + barH + gap + 8f * s, barW, 30f * s);
            GUI.Label(under, _waitingForPlayers ? "WAITING FOR PLAYERS" : $"LOADING  {Mathf.RoundToInt(_shownProgress * 100f)}%", _loadStyle);
            GUI.Label(under, ZombiesLoadout.Stage, _stageStyle);
            ZombiesTips.Draw(alpha, s, x - gap - line);
            GUI.color = Color.white;
        }

        /// <summary>The layout's hour and weather (the F8 settings), held.</summary>
        private void HoldTimeAndWeather()
        {
            // F8 open: its own sliders set the hour and weather live (this match's copy of the layout would put them back)
            if (_map == null || MapEditor.IsOpen)
                return;
            // Only when it has drifted: setting the clock again and again makes the light jump
            if (_map.Hour.HasValue && MapTime.Off(_map.Hour.Value))
                MapTime.Hold(_map.Hour.Value);
            // Rain left to the rain cycle once it runs
            if (_map.Weather != null && (MapWeather.Held == null || WeatherController.Instance?.WeatherDebug?.Enabled != true))
                MapWeather.Hold(_map.Weather);
            else if (_map.Weather != null && WeatherController.Instance?.WeatherDebug is WeatherDebug w
                && (Mathf.Abs(w.CloudDensity - _map.Weather.Clouds) > 0.01f || Mathf.Abs(w.Fog - Mathf.Clamp(_map.Weather.Fog, 0.001f, 0.255f)) > 0.001f
                    || Mathf.Abs(w.WindMagnitude - _map.Weather.Wind) > 0.01f))
            {
                w.CloudDensity = _map.Weather.Clouds;
                w.Fog = Mathf.Clamp(_map.Weather.Fog, 0.001f, 0.255f);
                w.WindMagnitude = _map.Weather.Wind;
            }
        }

        private void Rebuild()
        {
            if (_location == null)
            {
                ZombiesPlugin.Notify("Turn \"Build the layout in raids\" on in a raid first.", true);
                return;
            }
            Build();
        }

        private void Build()
        {
            Clear();
            _map = MapEditor.Read(_location);
            if (_map == null)
            {
                ZombiesPlugin.Notify($"No zombies layout for {_location} yet (maps\\{_location}.json).", true);
                return;
            }
            // Things placed in Unity (objects named KZM_<Kind>...) join the layout, so they work here like F8's
            try
            {
                ZombiesUnityMarkers.Adopt(_map);
            }
            catch (Exception ex)
            {
                ZombiesPlugin.Log.LogWarning($"Unity markers: {ex.Message}");
            }
            int layer = LayerMask.NameToLayer(CollisionLayer);
            int solid = 0, buyable = 0, shown = 0;
            var boxPlaces = new List<(Vector3 position, Quaternion rotation)>();
            var boxEntries = new List<int>();
            ZombiesZones.Build(_map.Entries, _map.Zones);
            _chosenYaw = null;
            Vector3? start = ChooseStart();
            if (_teleportPending)
            {
                _startPos = start;
                _startYaw = _chosenYaw;
            }
            foreach (MapEditor.Entry e in _map.Entries)
            {
                switch (e.Type)
                {
                    case MapEditor.Kind.Blocker:
                    case MapEditor.Kind.PlayAreaWall:
                        GameObject wall = Solid(e, layer);
                        Carve(wall);
                        Renderer r = wall.GetComponent<Renderer>();
                        r.enabled = _showBlockers.Value;
                        _blockerRenderers.Add(r);
                        solid++;
                        break;
                    case MapEditor.Kind.Door:
                    case MapEditor.Kind.Debris:
                        // Bought, it's destroyed and its hole in the navmesh goes with it: the way through opens
                        MapVisual buy = Model(e, layer, true);
                        Carve(buy.gameObject);
                        buy.gameObject.AddComponent<Buyable>().Init(e);
                        buyable++;
                        break;
                    // Box spots: the working box at one, the empty stand at the rest (set out after the loop)
                    case MapEditor.Kind.BoxSpot:
                        if (ZombiesMaps.Is(_location))
                        {
                            // Turned as the editor shows it (its hinge away from the side it was placed from)
                            boxPlaces.Add((MapEditor.Pos(e), MapEditor.Rotation(e) * Quaternion.Euler(0f, 180f, 0f)));
                            boxEntries.Add(_map.Entries.IndexOf(e));
                            break;
                        }
                        Carve(Model(e, layer, true).gameObject);
                        shown++;
                        break;
                    case MapEditor.Kind.PerkMachine:
                        // The original's secret: prone in front of it, 100 points once
                        MapVisual machine = Model(e, layer, true);
                        Carve(machine.gameObject);
                        machine.gameObject.AddComponent<PerkProneBonus>();
                        machine.gameObject.AddComponent<ZombiesMachineAudio>().Init(e.What);
                        if (e.What == "revive")
                            machine.gameObject.AddComponent<QuickReviveMachine>();
                        else
                            machine.gameObject.AddComponent<PerkMachineBuy>().Init(e.What);
                        shown++;
                        break;
                    case MapEditor.Kind.PackAPunch:
                        MapVisual pap = Model(e, layer, true);
                        Carve(pap.gameObject);
                        pap.gameObject.AddComponent<ZombiesPaP>();
                        shown++;
                        break;
                    case MapEditor.Kind.Prop:
                        // Flat ground cover (tiles, sheets, sleeping bags) is walked over: it never cuts the navmesh
                        MapVisual prop = Model(e, layer, true);
                        if (string.IsNullOrEmpty(e.What) || (!e.What.Contains("_tile") && !e.What.Contains("cardboard_set") && !e.What.Contains("Sleep_bag")))
                            Carve(prop.gameObject);
                        shown++;
                        break;
                    // Players never get through a window in the original (boarded or not; only zombies climb in): solid
                    case MapEditor.Kind.Window:
                        // Its opening cut from the navmesh: zombies come up to the boards (and climb in by script)
                        MapVisual window = Model(e, layer, true);
                        Carve(window.gameObject);
                        ShootThrough(window, layer);
                        ZombiesBarricade barricade = window.gameObject.AddComponent<ZombiesBarricade>();
                        barricade.Entry = EntryIndex(e);
                        barricade.Zone = e.Zone;
                        shown++;
                        break;
                    case MapEditor.Kind.PowerLight:
                        Model(e, layer, false);
                        shown++;
                        break;
                    // The original's power switch: thrown once, the power's on for the match
                    case MapEditor.Kind.PowerSwitch:
                        Model(e, layer, true).gameObject.AddComponent<PowerSwitch>();
                        shown++;
                        break;
                    // The exfil lever (ZombiesExfil); its hold area and exfil zone are only boxes in F8
                    case MapEditor.Kind.ExfilActivator:
                        Model(e, layer, true).gameObject.AddComponent<ExfilLever>();
                        shown++;
                        break;
                    // 115 rocks: a hit box to look at for "Investigate" (never blocks moving)
                    case MapEditor.Kind.Rock:
                        MapVisual rock = Model(e, layer, false);
                        rock.AddSolid(LayerMask.NameToLayer("HighPolyCollider"));
                        rock.gameObject.AddComponent<ZombiesRock>().Init(e);
                        shown++;
                        break;
                    case MapEditor.Kind.Lightning:
                    case MapEditor.Kind.Lamp:
                    case MapEditor.Kind.Decal:
                        Model(e, layer, false);
                        shown++;
                        break;
                    case MapEditor.Kind.WallBuy:
                        // A hit box to look at for the prompt (on the bullets' layer: it never blocks moving)
                        MapVisual wallGun = Model(e, layer, false);
                        wallGun.AddSolid(LayerMask.NameToLayer("HighPolyCollider"));
                        wallGun.gameObject.AddComponent<WallBuy>().Init(e);
                        shown++;
                        break;
                }
            }
            if (boxPlaces.Count > 0)
            {
                _boxSpots = new MatchBoxSpots(boxPlaces);
                Player me = Singleton<GameWorld>.Instance?.MainPlayer;
                // The map's own start spot when it has one (zm_stronghold: the F8 list's 39), else random
                int startSpot = _map.BoxStart >= 0 ? boxEntries.IndexOf(_map.BoxStart) : -1;
                _boxSpots.SetOut(start ?? (me != null ? me.Transform.position : boxPlaces[0].position), startSpot);
                // The host's spot is everyone's (a client's own pick is replaced when the host answers)
                if (ZombiesNet.IsHost)
                    ZombiesNet.BoxStart(BoxSpot());
            }
            if (ZombiesMaps.Is(_location))
            {
                ZombiesNavLinks.Add(_location); // before the rounds: spawns pick their barricade by the routes
                ZombiesReport.NewMatch();
                ZombiesRounds.Start(_map.Entries);
                ZombiesExfil.Build(_map.Entries);
                ZombiesNet.Built();
            }
            // Power and exfils only on a real zombies map (a dev layout over a normal raid leaves its exits alone)
            if (ZombiesMaps.Is(_location))
            {
                ZombiesPower.OnBuilt(this);
                ZombiesAmbience.Start(this, _location);
                ZombiesRoundFx.Load(this);
                // A map without power (Nacht style): everything works from the start, no switch, no power-on sting
                if (_map.AlwaysPowered)
                {
                    PowerIsOn = true;
                    ZombiesPower.OnFromStart(this);
                    ZombiesPlugin.Log.LogInfo("Zombies match: power on from the start (this map has no power switch)");
                }
            }
            HoldTimeAndWeather();
            // EFT's clock and weather may not be ready yet, or the raid's own weather lands after us: set again a while
            _reholdUntil = Time.time + 20f;
            Points = StartPoints;
            ZombiesPlugin.Log.LogInfo($"Zombies match: {_location} built, {solid} blockers/walls, {buyable} doors/debris, {shown} props/machines/wall buys/windows");
            // (SettleWalkBoxes is off: lowering the walk boxes left the zombies above them on the baked navmesh, stuck)
            ZombiesPhotos.OnMatchBuilt(this);
            ZombiesPlugin.Notify($"Zombies layout built: {solid} blockers, {buyable} doors and debris. {Points} points.");
        }

        /// <summary>A solid box the entry's size, standing on its spot, in its type's colour (opaque).</summary>
        private GameObject Solid(MapEditor.Entry e, int layer)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"Zombies_{e.Type}{(string.IsNullOrEmpty(e.What) ? "" : "_" + e.What)} (KennerZombies)";
            if (layer >= 0)
                go.layer = layer;
            Color c = MapEditor.Colour(e.Type);
            c.a = 1f;
            var renderer = go.GetComponent<Renderer>();
            renderer.material = new Material(Shader.Find("Sprites/Default")) { color = c };
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            MapEditor.Place(go, e);
            _built.Add(go);
            return go;
        }

        /// <summary>The thing as it looks (its model, or a coloured box), solid if asked.</summary>
        /// <summary>
        /// A window is shot through, boards or not: its own colliders only stop players (the movement layer), and the
        /// map's own small pieces in its opening (planks, boards nailed over the hole) lose their bullet colliders.
        /// </summary>
        private static void ShootThrough(MapVisual window, int playerLayer)
        {
            foreach (Collider c in window.GetComponentsInChildren<Collider>(true))
                if (playerLayer >= 0)
                    c.gameObject.layer = playerLayer;
            Vector3 size = window.Size;
            Transform t = window.transform;
            Vector3 middle = t.position + t.rotation * new Vector3(0f, size.y * 0.5f, 0f);
            Vector3 half = new Vector3(size.x * 0.5f, size.y * 0.5f, Mathf.Max(size.z, 0.6f) * 0.5f + 0.4f);
            int bullets = LayerMask.GetMask("HighPolyCollider", "TransparentCollider");
            int off = 0;
            foreach (Collider c in Physics.OverlapBox(middle, half, t.rotation, bullets, QueryTriggerInteraction.Ignore))
            {
                if (c == null || c.transform.IsChildOf(t))
                    continue;
                Bounds b = c.bounds;
                // Only pieces about the size of the opening (a plank, a board), never the wall round it
                if (b.size.x > size.x + 1f || b.size.z > size.x + 1f || b.size.y > size.y + 1f)
                    continue;
                Vector3 local = Quaternion.Inverse(t.rotation) * (b.center - middle);
                if (Mathf.Abs(local.x) > half.x || Mathf.Abs(local.y) > half.y || Mathf.Abs(local.z) > half.z)
                    continue;
                c.enabled = false;
                off++;
                ZombiesDebug.Log($"window at {t.position}: bullets pass {c.name} now");
            }
            // Then shoot test rays through the opening itself (a grid, both ways): whatever a bullet would still stop
            // on in the hole (an invisible box, a filled-in collider) is switched off if it's under 8 m, else named in
            // the log (a wall that big would open more than the window)
            var blockers = new HashSet<Collider>();
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 4; j++)
                {
                    Vector3 p = middle + t.right * ((i / 4f - 0.5f) * size.x * 0.8f) + t.up * ((0.35f + j / 3f * 0.5f - 0.5f) * size.y); // 35-85% up: never the sill or sandbags under it
                    foreach (Vector3 dir in new[] { t.forward, -t.forward })
                        foreach (RaycastHit h in Physics.RaycastAll(p - dir * 2f, dir, 4f, bullets, QueryTriggerInteraction.Ignore))
                            if (h.collider != null && h.collider.enabled && !h.collider.transform.IsChildOf(t))
                                blockers.Add(h.collider);
                }
            foreach (Collider c in blockers)
            {
                Vector3 bs = c.bounds.size;
                string what = $"{c.GetType().Name} {PathOf(c.transform)} (layer {LayerMask.LayerToName(c.gameObject.layer)}, size {bs.x:0.0} x {bs.y:0.0} x {bs.z:0.0})";
                if (Mathf.Max(bs.x, bs.y, bs.z) < 8f)
                {
                    c.enabled = false;
                    off++;
                    ZombiesPlugin.Log.LogInfo($"Zombies match: window at {t.position}: bullets were stopped in the opening by {what}: off now");
                }
                else
                    ZombiesPlugin.Log.LogWarning($"Zombies match: window at {t.position}: bullets are stopped in the opening by {what} (too big to switch off: fix it in Unity)");
            }
            if (off > 0)
                ZombiesPlugin.Log.LogInfo($"Zombies match: window at {t.position}: {off} of the map's own pieces in its opening let bullets through");
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }

        private MapVisual Model(MapEditor.Entry e, int layer, bool solid)
        {
            MapVisual v = MapVisual.Make(e, true);
            if (solid && !v.HasOwnColliders)
                v.AddSolid(layer);
            _built.Add(v.gameObject);
            return v;
        }

        /// <summary>
        /// Cuts the thing's footprint out of the baked navmesh (a carving NavMeshObstacle the size of its models), so
        /// zombies path around what was placed in game without a re-bake in Unity. Carving only cuts holes: new floor
        /// still needs a bake.
        /// </summary>
        private static void Carve(GameObject go)
        {
            Transform t = go.transform;
            bool any = false;
            var b = new Bounds();
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                // The model's own box, in the thing's space (a world box would grow when it's turned)
                MeshFilter mf = r.GetComponent<MeshFilter>();
                Bounds local = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds(r.transform.InverseTransformPoint(r.bounds.center), Vector3.zero);
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = t.InverseTransformPoint(r.transform.TransformPoint(corner));
                    if (!any)
                        b = new Bounds(p, Vector3.zero);
                    else
                        b.Encapsulate(p);
                    any = true;
                }
            }
            if (!any)
                return;
            NavMeshObstacle o = go.AddComponent<NavMeshObstacle>();
            o.shape = NavMeshObstacleShape.Box;
            o.center = b.center;
            o.size = Vector3.Max(b.size, Vector3.one * 0.1f);
            o.carving = true;
            o.carveOnlyStationary = true;
        }

        private void ShowBlockers()
        {
            foreach (Renderer r in _blockerRenderers)
                if (r != null)
                    r.enabled = _showBlockers.Value;
        }

        // Where the players start this match (a Player Start in zone 1; with a team, zone 1 or 2), put there as the
        // black screen fades in; none placed: EFT's own spawn
        private bool _teleportPending;
        private Vector3? _startPos;
        // The way the spawn faces (the marker's own forward, the line out of the SDK's spawn box; a Player Start's Turn):
        // the player is turned to it on arriving, not left facing however they stood (into a wall, the user 2026-11-08)
        private float? _startYaw, _chosenYaw;

        // The map's player spawn kept for solo games
        private const string SoloSpawn = "Spawn_Player_3";

        private Vector3? ChooseStart()
        {
            int[] zones = ZombiesTeam.List().Count <= 1 ? new[] { 1 } : ZombiesZones.StartZones;
            var starts = _map.Entries.Where(e => e.Type == MapEditor.Kind.PlayerStart).ToList();
            if (starts.Count == 0)
            {
                // No Player Start placed: the map's own player spawns (Spawn_Player_1..4, no zones needed), else a
                // spawn inside the start zone, else that zone's middle
                var markers = UnityEngine.Object.FindObjectsOfType<EFT.Game.Spawning.SpawnPointMarker>();
                // Each player at their own spawn (the user, 2026-11-07): player 1 at Spawn_Player_1 ... player 4 at
                // Spawn_Player_4; solo is player 1. The place is Fika's player number (the host 1, then join order: the
                // team list only holds you at the map build, which once put everyone on the same spot)
                var team = markers.Where(m => m.name.StartsWith("Spawn_Player_")).ToList();
                if (team.Count > 0)
                {
                    int place = ZombiesTeam.ExpectedPlayers() <= 1 ? 1 : ZombiesTeam.Place(Singleton<GameWorld>.Instance?.MainPlayer);
                    var own = team.FirstOrDefault(m => m.name == "Spawn_Player_" + place)
                              ?? team.OrderBy(m => m.name, StringComparer.Ordinal).ElementAt((place - 1) % team.Count);
                    _chosenYaw = own.transform.eulerAngles.y;
                    ZombiesPlugin.Log.LogInfo($"Zombies match: player {place} of {ZombiesTeam.ExpectedPlayers()}, spawn {own.name} facing {_chosenYaw:0}");
                    return own.transform.position + Vector3.up * 0.1f;
                }
                if (!ZombiesZones.Any)
                    return null;
                var spawns = markers.Where(m => zones.Contains(ZombiesZones.ZoneAt(m.transform.position))).ToList();
                if (spawns.Count > 0)
                {
                    var spawn = spawns[UnityEngine.Random.Range(0, spawns.Count)];
                    _chosenYaw = spawn.transform.eulerAngles.y;
                    return spawn.transform.position + Vector3.up * 0.1f;
                }
                Vector3? middle = ZombiesZones.Middle(zones[0]);
                if (middle.HasValue && Physics.Raycast(middle.Value + Vector3.up * 2f, Vector3.down, out RaycastHit ground, 20f, ~0, QueryTriggerInteraction.Ignore))
                    return ground.point + Vector3.up * 0.1f;
                return middle;
            }
            var inZones = starts.Where(e => zones.Contains(ZombiesZones.ZoneAt(MapEditor.Pos(e)))).ToList();
            var pick = inZones.Count > 0 ? inZones : starts;
            MapEditor.Entry chosen = pick[UnityEngine.Random.Range(0, pick.Count)];
            _chosenYaw = chosen.Turn;
            return MapEditor.Pos(chosen) + Vector3.up * 0.1f;
        }

        private void GoToStart()
        {
            MapEditor.Entry start = _map?.Entries.Find(e => e.Type == MapEditor.Kind.PlayerStart);
            if (start == null || !ZombiesPlugin.CurrentPlayer(out _, out Player player))
            {
                ZombiesPlugin.Notify("No player start in this layout.", true);
                return;
            }
            player.Teleport(MapEditor.Pos(start) + Vector3.up * 0.1f, false);
        }

        private MatchBoxSpots _boxSpots;

        private void Clear()
        {
            PowerIsOn = false;
            ZombiesTeam.ClearPresence();
            ZombiesGearFree.Restore();
            ZombiesMeleeReach.Restore();
            Opened.Clear();
            ZombiesNavLinks.Remove();
            ZombiesGameOver.ResetFinished();
            ZombiesRounds.Clear();
            _boxSpots?.Clear();
            _boxSpots = null;
            foreach (GameObject go in _built)
                if (go != null)
                    Destroy(go);
            _built.Clear();
            _blockerRenderers.Clear();
            _map = null;
        }

        private void Teardown()
        {
            // A zombies match that ended any way but the game over (the exfil, leaving with a team): its report too
            try
            {
                if (ZombiesMaps.Is(_location) && ZombiesMatch.LoadedIn && !ZombiesReport.KeptThisMatch)
                    ZombiesReport.Keep(Mathf.Max(0, ZombiesRounds.Round - 1));
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies report: not kept at the raid's end ({e.Message})");
            }
            ZombiesPause.Resume();
            ZombiesSettings.Restore();
            ZombiesAmbience.Stop();
            MachineJingles.Reset();
            ZombiesLoadingMusic.Stop();
            ZombiesRoundFx.Clear();
            ZombiesPowerUps.Clear();
            ZombiesRock.Clear();
            ZombiesExfil.Clear();
            ZombiesBlockers.Clear();
            ZombiesStamina.Restore();
            ZombiesInertia.Restore();
            ZombiesDecals.Clear();
            ZombiesGround.Restore();
            ZombiesHudHide.Restore();
            Clear();
            _location = null;
            Points = 0;
        }

        // ---------- Points on screen (the original's scores) ----------

        private void OnGUI()
        {
            // Nothing on screen until the player is in (not on the loading screen)
            // The game over covers everything (no HUD under it)
            if (ZombiesGameOver.Active)
            {
                if (Event.current.type == EventType.Repaint)
                    ZombiesGameOver.Draw();
                return;
            }
            // The pause button takes clicks: every IMGUI event, not just the paint pass the HUD below draws in (the
            // leader's "Pause match" was drawn but never clickable, user 2026-11-10)
            if (Built && Active && ZombiesPlugin.RaidStarted)
                ZombiesProfile.Measure("GUI ZombiesPause.Draw", () => ZombiesPause.Draw());
            if (Built && Event.current.type == EventType.Repaint && ZombiesPlugin.RaidStarted)
            {
                // The match's Esc menu up: the round, portraits and points stand aside (user, 2026-11-10)
                bool menuUp = ZombiesFrontEnd.InMatchMenu;
                if (!menuUp)
                    ZombiesProfile.Measure("GUI ScoresHud.Draw", () => ScoresHud.Draw(Points, ScoresHud.Dummies ?? ScoresHud.Teammates(), PlayerPortrait.Get(Singleton<GameWorld>.Instance?.MainPlayer)));
                if (Active)
                {
                    ZombiesProfile.Measure("GUI ZombiesTeam.DrawTags", () => ZombiesTeam.DrawTags());
                    ZombiesProfile.Measure("GUI ZombiesStats.DrawBoard", () => ZombiesStats.DrawBoard());
                    ZombiesProfile.Measure("GUI ZombiesSlaps.Draw", () => ZombiesSlaps.Draw());
                    ZombiesProfile.Measure("GUI ZombiesDowned.Draw", () => ZombiesDowned.Draw());
                    ZombiesProfile.Measure("GUI ZombiesBarricade.DrawPrompt", () => ZombiesBarricade.DrawPrompt());
                    // What you're looking at: our own list (EFT's grey one kept out of sight)
                    ZombiesHud.HideEftActions();
                    ZombiesProfile.Measure("GUI ZombiesHud.DrawActions", () => ZombiesHud.DrawActions());
                    if (!menuUp)
                        ZombiesProfile.Measure("GUI ZombiesRounds.Draw", () => ZombiesRounds.Draw());
                    ZombiesProfile.Measure("GUI ZombiesAmmo.Draw", () => ZombiesAmmo.Draw());
                    ZombiesProfile.Measure("GUI ZombiesPowerUps.Draw", () => ZombiesPowerUps.Draw());
                    ZombiesProfile.Measure("GUI ZombiesNoDamage.Draw", () => ZombiesNoDamage.Draw());
                    ZombiesProfile.Measure("GUI ZombiesFireMode.DrawCrosshair", () => ZombiesFireMode.DrawCrosshair(Singleton<GameWorld>.Instance?.MainPlayer));
                    ZombiesProfile.Measure("GUI ZombiesGrenadeIndicator.Draw", () => ZombiesGrenadeIndicator.Draw());
                }
            }
            // (The Esc menu's old ZOMBIES CONTROLS button is gone: the match's own Esc menu, ZombiesFrontEnd, opens the
            // zombies settings page)
            // Over everything, from the moment the map is built (the loading screen's end included)
            if (Event.current.type == EventType.Repaint)
                DrawFadeIn();
            // Fika's lobby (the host hasn't started): over the comic, every event (its buttons)
            if (_blackSince >= 0f)
                ZombiesLobby.Draw();
        }
    }

    /// <summary>
    /// The original's power switch (Kino der Toten's, zombies.bundle zm_power_switch): look at it for "Turn on the power". Thrown,
    /// the lever pulls down with its clunk (the "pull" anim), and as it lands the power's on for the whole match
    /// (ZombiesMatch.PowerOn: Kino's power-on, every power light, perk machine and Pack-a-Punch to life, ZB-013's door);
    /// then it sits down, dials humming (the "on" idle loop). Without the bundle it's the stand-in box, same switch.
    /// </summary>
    internal class PowerSwitch : MonoBehaviour
    {
        private const float InteractDistance = 2.5f, PullTime = 1.67f;
        private readonly ZombiesInteraction _menu = new ZombiesInteraction();
        private Animator _animator;
        private bool _thrown;

        private void Start()
        {
            _animator = GetComponentInChildren<Animator>(true);
            _animator?.Play(ZombiesMatch.PowerIsOn ? "on" : "off");
        }

        private void Update()
        {
            if (_thrown || ZombiesMatch.PowerIsOn)
            {
                _menu.Reset();
                return;
            }
            if (ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
        }

        private void OnDestroy() => _menu.Reset();

        private IEnumerable<(string, Action)> Options()
        {
            yield return ("Turn on the power", (Action)Throw);
        }

        private void Throw()
        {
            if (_thrown)
                return;
            _thrown = true;
            _menu.Reset();
            StartCoroutine(Pull());
        }

        private IEnumerator Pull()
        {
            Sound("Audio_Lever");
            if (_animator != null)
            {
                _animator.Play("pull", 0, 0f);
                yield return new WaitForSeconds(PullTime);
                _animator.Play("on");
            }
            Sound("Audio_PowerOn");
            ZombiesMatch.PowerOn();
        }

        // The prefab's own 3D sounds (switched off when the model was placed)
        private void Sound(string name)
        {
            AudioSource s = GetComponentsInChildren<AudioSource>(true).FirstOrDefault(a => a.name == name);
            if (s == null)
                return;
            s.enabled = true;
            s.volume = Mathf.Min(s.volume, 1f) * ZombiesSounds.Volume(ZombiesSounds.Group.Power);
            s.Play();
        }
    }

    /// <summary>
    /// A wall buy : look at it for "Buy M14 (500)"; bought, the gun goes to your hands. Carrying that gun already, it
    /// offers its ammo instead at half the price, filling its reserve.
    /// </summary>
    internal class WallBuy : MonoBehaviour
    {
        private const float InteractDistance = 2f;
        private readonly ZombiesInteraction _menu = new ZombiesInteraction();
        private MapEditor.Entry _entry;
        private BoxGuns.Gun _gun;
        // The grenades' wall buy (the original: tops your frags up to 4)
        private bool _grenades;

        public void Init(MapEditor.Entry entry)
        {
            _entry = entry;
            _grenades = entry.What == ZombiesGrenades.WallId;
            _gun = _grenades ? null : BoxGuns.FindGun(entry.What);
            StartCoroutine(Chalk());
        }

        // The original's chalk: until the first purchase only the outline shows (on a board unless turned off); the first one
        // slides the gun out of the wall, and it stays there after
        private Transform _gunModel;
        private bool _shown;
        private static Texture2D _boardTexture;
        private const float SlideDepth = 0.35f, SlideSeconds = 1.6f;

        private IEnumerator Chalk()
        {
            string name = _grenades ? "grenades" : _gun?.Name ?? _entry.What;
            MapVisual visual = GetComponent<MapVisual>();
            for (float until = Time.time + 30f; Time.time < until && (visual == null || visual.ModelRoot == null); )
                yield return null;
            if (visual?.ModelRoot == null)
            {
                ZombiesPlugin.Log.LogWarning($"Wall buy {name}: no gun model after 30 s, no chalk or board");
                yield break;
            }
            _gunModel = visual.ModelRoot;
            FindWall(name);
            Texture2D chalk = ZombiesChalk.Draw(_gunModel.gameObject, _out, transform.up, out Vector2 size);
            if (chalk == null)
            {
                // Never invisible: the gun shows on the wall instead
                ZombiesPlugin.Log.LogWarning($"Wall buy {name}: the chalk outline couldn't be drawn, the gun shows instead");
                SetGunShown(true);
                yield break;
            }
            SetGunShown(false);
            Bounds b = new Bounds(_gunModel.position, Vector3.zero);
            foreach (Renderer r in _gunModel.GetComponentsInChildren<Renderer>(true))
                b.Encapsulate(r.bounds);
            // The board just behind the gun's back, the chalk on its face
            // On the wall's face: the wall buy's own spot is where it was snapped onto the wall. (Behind the gun's back
            // was inside the wall: the gun sits half in it, so the board and the chalk never showed.)
            float gunBack = Vector3.Dot(b.center - _wall, _out)
                            - Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(_out.x), Mathf.Abs(_out.y), Mathf.Abs(_out.z))));
            Vector3 back = b.center - _out * Vector3.Dot(b.center - _wall, _out) + _out * 0.005f;
            ZombiesPlugin.Log.LogInfo($"Wall buy {name}: the gun's back is {gunBack * 100f:0.0} cm {(gunBack < 0f ? "into" : "out from")} the wall, board put on the wall's face");
            string shader = "none";
            string how;
            if (!_entry.NoBoard)
            {
                // One picture, no see-through parts: the wood with the chalk drawn on it (a cut-out chalk quad came out as a
                // solid white card in the raid, the shader variant for it isn't there)
                Vector2 boardSize = size * 1.15f;
                Texture2D picture = ComposeBoard(chalk, 1f / 1.15f);
                shader = Board(back, boardSize, picture);
                how = "chalk drawn on the board's picture";
            }
            else
                how = ChalkQuad(back + _out * (BoardThickness + 0.004f), size, chalk);
            // The gun half out of the board, towards the arrow: its middle on the board's face (where it slides out to)
            Vector3 face = back + _out * (_entry.NoBoard ? 0f : BoardThickness);
            _gunModel.position += _out * Vector3.Dot(face - b.center, _out);
            int lines = chalk.GetPixels32().Count(c => c.a > 40);
            ZombiesPlugin.Log.LogInfo($"Wall buy {name}: chalk {size.x:0.00} x {size.y:0.00} m ({lines} chalk pixels, {how}), " +
                                      $"{(_entry.NoBoard ? "no board" : "on a board (" + shader + ")")}, at {back}");
            // The outline as drawn, to look at (logs\chalk_<gun>.png)
            try
            {
                string dir = System.IO.Path.Combine(ZombiesPlugin.Dir, "logs");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"chalk_{_entry.What}.png"), chalk.EncodeToPNG());
            }
            catch
            {
            }
        }

        // Which way is out of the wall, and where its face is: a ray from each side of the spot (the facing it was placed
        // with can point into the wall)
        private Vector3 _out, _wall;

        private void FindWall(string name)
        {
            _out = transform.forward;
            _wall = transform.position;
            int mask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain", "Default");
            Vector3 mid = transform.position + transform.up * 0.15f;
            // Shot from out in front back at the wall: the side it hits from is outside (our own hit box skipped)
            if (WallHit(mid + transform.forward * 0.6f, -transform.forward, mask, out Vector3 front))
            {
                _out = transform.forward;
                _wall = front;
            }
            else if (WallHit(mid - transform.forward * 0.6f, transform.forward, mask, out Vector3 back))
            {
                _out = -transform.forward;
                _wall = back;
            }
            else
                ZombiesPlugin.Log.LogWarning($"Wall buy {name}: no wall found within 0.6 m of it, the board goes on its spot");
            ZombiesPlugin.Log.LogInfo($"Wall buy {name}: out of the wall is {(_out == transform.forward ? "its front" : "its back (turned round)")}, wall face {Vector3.Distance(_wall, transform.position):0.00} m from its spot");
        }

        private static bool WallHit(Vector3 from, Vector3 dir, int mask, out Vector3 point)
        {
            point = Vector3.zero;
            float best = float.MaxValue;
            foreach (RaycastHit h in Physics.RaycastAll(from, dir, 1.2f, mask, QueryTriggerInteraction.Ignore))
                if (h.distance < best && h.collider.GetComponentInParent<MapVisual>() == null)
                {
                    best = h.distance;
                    point = h.point;
                }
            return best < float.MaxValue;
        }

        // The original's wall buy board: a plank of old wood fixed to the wall, lit like the rest of the room
        private const float BoardThickness = 0.025f;
        private static Material _boardMaterial;
        private static string _boardShader;

        /// <summary>The board's wood with the chalk outline drawn on it, as one opaque picture (the chalk fills the middle 1/frac).</summary>
        private static Texture2D ComposeBoard(Texture2D chalk, float frac)
        {
            Texture2D wood = BoardTexture();
            bool haveWood = wood != null && wood.width > 4;
            int w = 1024, h = Mathf.Clamp(Mathf.RoundToInt(w * chalk.height / (float)chalk.width), 64, 1024);
            Color32[] c = chalk.GetPixels32();
            int cw = chalk.width, ch = chalk.height;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
                    Color col = haveWood ? wood.GetPixelBilinear(u * 2f % 1f, v * 2f % 1f) * 0.78f : new Color(0.38f, 0.27f, 0.17f);
                    col.a = 1f;
                    float cu = (u - 0.5f) / frac + 0.5f, cv = (v - 0.5f) / frac + 0.5f;
                    if (cu >= 0f && cu < 1f && cv >= 0f && cv < 1f)
                    {
                        Color32 k = c[Mathf.Clamp((int)(cv * ch), 0, ch - 1) * cw + Mathf.Clamp((int)(cu * cw), 0, cw - 1)];
                        col = Color.Lerp(col, new Color(k.r / 255f, k.g / 255f, k.b / 255f, 1f), k.a / 255f);
                    }
                    px[y * w + x] = col;
                }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "zm_wallbuy_picture", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(true, false);
            return tex;
        }

        private string Board(Vector3 wallFace, Vector2 size, Texture2D picture = null)
        {
            if (_boardMaterial == null)
            {
                // A lit shader that's loaded (zombies.bundle brings Standard Specular), else a plain one
                Shader shader = new[] { "Standard (Specular setup)", "Standard", "Legacy Shaders/Diffuse", "Sprites/Default" }
                    .Select(Shader.Find).FirstOrDefault(s => s != null && s.isSupported);
                _boardShader = shader?.name ?? "none";
                _boardMaterial = new Material(shader) { name = "zm_wallbuy_board", mainTexture = BoardTexture(), color = new Color(0.85f, 0.85f, 0.85f) };
                if (_boardMaterial.HasProperty("_Glossiness"))
                    _boardMaterial.SetFloat("_Glossiness", 0.15f);
                if (_boardMaterial.HasProperty("_SpecColor"))
                    _boardMaterial.SetColor("_SpecColor", new Color(0.08f, 0.08f, 0.08f));
            }
            GameObject plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plank.name = "board";
            Destroy(plank.GetComponent<Collider>());
            plank.transform.SetParent(transform, true);
            plank.transform.rotation = Quaternion.LookRotation(_out, transform.up);
            plank.transform.position = wallFace + _out * (BoardThickness * 0.5f);
            Vector3 lossy = transform.lossyScale;
            plank.transform.localScale = new Vector3(size.x / lossy.x, size.y / lossy.y, BoardThickness / lossy.z);
            Renderer renderer = plank.GetComponent<Renderer>();
            if (picture != null)
                renderer.sharedMaterial = new Material(_boardMaterial) { name = "zm_wallbuy_picture", mainTexture = picture, color = Color.white };
            else
                renderer.sharedMaterial = _boardMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return _boardShader;
        }

        // The chalk: cut out of its picture on the same lit shader as the board (a see-through sprite didn't show in
        // EFT's lighting), glowing a little like the original's chalk
        private string ChalkQuad(Vector3 at, Vector2 size, Texture2D chalk)
        {
            Shader shader = new[] { "Standard (Specular setup)", "Standard" }.Select(Shader.Find).FirstOrDefault(s => s != null && s.isSupported);
            if (shader == null)
            {
                Quad("chalk", at, size, chalk, Color.white);
                return "sprite";
            }
            var m = new Material(shader) { name = "zm_wallbuy_chalk", mainTexture = chalk, color = Color.white };
            m.SetFloat("_Mode", 1f);
            m.SetFloat("_Cutoff", 0.25f);
            m.EnableKeyword("_ALPHATEST_ON");
            m.SetOverrideTag("RenderType", "TransparentCutout");
            m.renderQueue = 2450;
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetTexture("_EmissionMap", chalk);
                m.SetColor("_EmissionColor", new Color(0.55f, 0.57f, 0.6f));
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            if (m.HasProperty("_SpecColor"))
                m.SetColor("_SpecColor", Color.black);
            if (m.HasProperty("_Glossiness"))
                m.SetFloat("_Glossiness", 0f);
            Quad("chalk", at, size, chalk, Color.white).sharedMaterial = m;
            return "lit cut-out";
        }

        private Renderer Quad(string name, Vector3 at, Vector2 size, Texture2D texture, Color tint)
        {
            GameObject q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, true);
            q.transform.position = at;
            // The quad's face looks back along its own +Z: turned to face out of the wall
            q.transform.rotation = Quaternion.LookRotation(-_out, transform.up);
            q.transform.localScale = new Vector3(size.x / transform.lossyScale.x, size.y / transform.lossyScale.y, 1f);
            Renderer r = q.GetComponent<Renderer>();
            r.sharedMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = texture, color = tint };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return r;
        }

        private static Texture2D BoardTexture()
        {
            if (_boardTexture != null)
                return _boardTexture;
            string file = System.IO.Path.Combine(ZombiesPlugin.Dir, "hud", "wallbuy_board.png");
            _boardTexture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (System.IO.File.Exists(file))
                _boardTexture.LoadImage(System.IO.File.ReadAllBytes(file));
            return _boardTexture;
        }

        private void SetGunShown(bool on)
        {
            _shown = on;
            if (_gunModel != null)
                foreach (Renderer r in _gunModel.GetComponentsInChildren<Renderer>(true))
                    r.enabled = on;
        }

        // The first purchase: the gun slides out of the chalk, the original's wall sound with it
        private IEnumerator SlideOut()
        {
            if (_gunModel == null || _shown)
                yield break;
            SetGunShown(true);
            Vector3 end = _gunModel.position, start = end - (_out == Vector3.zero ? transform.forward : _out) * SlideDepth;
            for (float t = 0f; t < SlideSeconds && _gunModel != null; t += Time.deltaTime)
            {
                float k = t / SlideSeconds;
                _gunModel.position = Vector3.Lerp(start, end, 1f - (1f - k) * (1f - k) * (1f - k));
                yield return null;
            }
            if (_gunModel != null)
                _gunModel.position = end;
        }

        private void Update()
        {
            if ((_gun != null || _grenades) && ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
        }

        private void OnDestroy() => _menu.Reset();

        private void BuyGrenades()
        {
            if (!ZombiesPlugin.CurrentPlayer(out _, out Player player) || ZombiesGrenades.Count(player) >= ZombiesGrenades.Most || !ZombiesMatch.Spend(_entry.Price))
                return;
            ZombiesPlugin.Log.LogInfo($"Zombies match: bought grenades off the wall for {_entry.Price}");
            ZombiesSounds.Head("zm_wallbuy", 0.9f);
            StartCoroutine(SlideOut());
            ZombiesGrenades.FillTo(player, ZombiesGrenades.Most);
        }

        private int AmmoPrice => Mathf.Max(0, _entry.Price / 2);

        // The same gun carried already (by its weapon template)
        private Weapon Owned()
        {
            string tpl = (string)_gun.Base?["tpl"];
            if (tpl == null || !ZombiesPlugin.CurrentPlayer(out _, out Player player))
                return null;
            return player.InventoryController.Inventory.Equipment.GetAllItems().OfType<Weapon>().FirstOrDefault(w => w.TemplateId.ToString() == tpl);
        }

        private IEnumerable<(string, Action)> Options()
        {
            if (_grenades)
            {
                bool full = ZombiesPlugin.CurrentPlayer(out _, out Player me) && ZombiesGrenades.Count(me) >= ZombiesGrenades.Most;
                yield return ($"Buy grenades ({_entry.Price})", full ? null : ZombiesMatch.Afford(_entry.Price, BuyGrenades));
                yield break;
            }
            Weapon owned = Owned();
            if (owned != null)
                yield return ($"Buy ammo for {_gun.Name} ({AmmoPrice})", ZombiesMatch.Afford(AmmoPrice, () => BuyAmmo(owned)));
            else
                yield return ($"Buy {_gun.Name} ({_entry.Price})", ZombiesMatch.Afford(_entry.Price, BuyGun));
        }

        private void BuyGun()
        {
            if (!ZombiesMatch.Spend(_entry.Price) || !ZombiesPlugin.CurrentPlayer(out _, out Player player))
                return;
            ZombiesVox.Say("wall_buy", 0.7f, 10f);
            ZombiesPlugin.Log.LogInfo($"Zombies match: bought {_gun.Name} off the wall for {_entry.Price}");
            ZombiesSounds.Head("zm_wallbuy", 0.9f);
            StartCoroutine(SlideOut());
            ZombiesPlugin.Instance.StartCoroutine(BoxGive.Give(_gun, player));
        }

        private void BuyAmmo(Weapon owned)
        {
            if (!ZombiesMatch.Spend(AmmoPrice))
                return;
            ZombiesAmmo.FillGun(owned);
            ZombiesPlugin.Log.LogInfo($"Zombies match: bought ammo for {_gun.Name} for {AmmoPrice}");
            ZombiesSounds.Head("zm_wallbuy", 0.9f);
        }
    }

    /// <summary>
    /// A door or pile of debris: look at it for "Open door (750)" / "Clear debris (1000)"; bought, it moves out of the way
    /// and goes. One set to open with the power (OpensWithPower) has no prompt: it clears when the power comes on.
    /// </summary>
    internal class Buyable : MonoBehaviour
    {
        private const float InteractDistance = 2.5f, ClearSeconds = 0.9f;

        private readonly ZombiesInteraction _menu = new ZombiesInteraction();
        private MapEditor.Entry _entry;
        private bool _clearing;

        public void Init(MapEditor.Entry entry) => _entry = entry;

        public MapEditor.Entry Entry => _entry;

        /// <summary>A teammate bought it: it clears here too (no points).</summary>
        public void ClearRemote()
        {
            if (_clearing)
                return;
            _clearing = true;
            _menu.Reset();
            StartCoroutine(Clear());
        }

        private bool IsDoor => _entry.Type == MapEditor.Kind.Door;

        private void Update()
        {
            // One the power opens: no prompt, it goes when the power comes on
            if (_entry.OpensWithPower)
            {
                if (!_clearing && ZombiesMatch.PowerIsOn)
                    PowerClear();
                return;
            }
            if (!_clearing && ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
        }

        private void OnDestroy() => _menu.Reset();

        // The power's on: it clears by itself (after a moment, as the machines wake), the map opens up
        private void PowerClear()
        {
            _clearing = true;
            _menu.Reset();
            ZombiesPlugin.Log.LogInfo($"Zombies match: power opened {_entry.Type} ({_entry.What})");
            StartCoroutine(AfterPowerClear());
        }

        private IEnumerator AfterPowerClear()
        {
            yield return new WaitForSeconds(UnityEngine.Random.Range(0.8f, 1.6f));
            yield return Clear();
        }

        private IEnumerable<(string, Action)> Options()
        {
            string what = IsDoor ? "Open door" : "Clear debris";
            yield return ($"{what} ({_entry.Price})", ZombiesMatch.Afford(_entry.Price, Buy));
        }

        private void Buy()
        {
            if (_clearing || !ZombiesMatch.Spend(_entry.Price))
                return;
            if (ZombiesMatch.Active)
                ZombiesStats.Mine.Doors++;
            _clearing = true;
            _menu.Reset();
            ZombiesPlugin.Log.LogInfo($"Zombies match: bought {_entry.Type} ({_entry.What}) for {_entry.Price}");
            ZombiesNet.Opened(ZombiesMatch.EntryIndex(_entry));
            StartCoroutine(Clear());
        }

        // Kino der Toten's door sounds for each look (metal, wooden, the chain-link gate); rubble rumbles, junk clatters
        private static readonly HashSet<string> MetalDoors = new HashSet<string> { "metal_rusted", "bunker", "metal_rustic" };
        private static readonly HashSet<string> RockDebris = new HashSet<string>
            { "concrete_rubble", "brick_pile", "cave_rocks", "concrete_pile", "mud_rocks", "church_bricks", "gorod_rubble" };

        private string MoveSound => IsDoor
            ? _entry.What == "barbed_gate" ? "zm_door_chain_#" : MetalDoors.Contains(_entry.What) ? "zm_door_metal_#" : "zm_door_wood_#"
            : RockDebris.Contains(_entry.What) ? "zm_debris_rock_#" : "zm_debris_junk_#";

        /// <summary>
        /// The original's clear: it lifts and shakes (a door slides up, debris floats up) with its own grind or rumble, then goes
        /// in a lightning cloud with the original's lightning crack and slam; then it's gone.
        /// </summary>
        private IEnumerator Clear()
        {
            // The zones on both sides of it open (their zombie spawns can spawn now)
            ZombiesZones.OpenAround(transform.position);
            ZombiesMatch.Opened.Add(ZombiesMatch.EntryIndex(_entry));
            // Still solid while it goes (nothing walks through it until it's gone)
            Vector3 from = transform.position;
            Vector3 size = GetComponent<MapVisual>()?.Size ?? new Vector3(1.5f, 2f, 0.3f);
            Vector3 middle = from + Vector3.up * size.y * 0.5f;
            ZombiesSounds.At(MoveSound, middle, 1f, 40f);
            // The original: lightning all over it as it goes, a door up into the frame, debris down into the ground
            ZombiesSounds.At("zm_door_lightning_#", middle, 1f, 60f);
            if (!IsDoor)
                ZombiesSounds.At("zm_debris_flux_#", middle, 1f, 40f);
            ZombiesLightning.SurgeAt(middle, size, ClearSeconds + 0.3f);
            Vector3 to = from + (IsDoor ? Vector3.up * size.y : Vector3.down * (size.y * 1.1f + 0.2f));
            float start = Time.time;
            while (Time.time < start + ClearSeconds)
            {
                float t = (Time.time - start) / ClearSeconds;
                Vector3 shake = IsDoor ? Vector3.zero : UnityEngine.Random.insideUnitSphere * 0.04f * (1f - t);
                transform.position = Vector3.Lerp(from, to, t * t) + shake;
                yield return null;
            }
            ZombiesSounds.At("zm_door_slam", middle, 0.8f, 40f);
            ZombiesPlugin.Log.LogInfo($"Zombies match: {(IsDoor ? "door up" : "debris down")} with its lightning ({_entry.What})");
            BoxCloud.Puff(middle, Mathf.Clamp(Mathf.Max(size.x, size.y) * 0.6f, 0.8f, 2.5f));
            Destroy(gameObject);
        }
    }
}
