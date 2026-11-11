using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using Newtonsoft.Json;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies map editor (dev): in any normal raid, with "Map editor on" in F12, F8 opens its window and a free
    /// camera. Place what a zombies map needs (player start, zombie spawns, Nacht-style boarded windows, box spots, perk
    /// machines, Pack-a-Punch, wall buys, buyable doors and debris, solid blockers and play-area walls): a new thing
    /// follows the mouse until clicked down; click a placed thing to select it, then move, turn and size it with the
    /// window's sliders, the arrow keys or the mouse. Saved as maps\&lt;location&gt;.json beside the DLL (e.g.
    /// maps\bigmap.json for Customs); "Test layout" sets it out as a match would (ZombiesMatch). Things show as their
    /// real models where there are some (MapVisual), else coloured boxes. The asset list on the left places any of the
    /// map's own props or any EFT item (AssetCatalog) as a Prop. Left-drag a placed thing to move it (Shift: keep its
    /// height); the step sliders set how far the arrow keys and the wheel move, turn and size it.
    /// </summary>
    internal class MapEditor : MonoBehaviour
    {
        public enum Kind { PlayerStart, ZombieSpawn, Window, BoxSpot, PerkMachine, PackAPunch, WallBuy, Door, Debris, Blocker, PlayAreaWall, Prop, PowerLight, PowerSwitch, Zone, Rock, Lightning, Lamp, Decal, ExfilActivator, ExfilHold, ExfilZone }

        /// <summary>One placed thing, as saved.</summary>
        public class Entry
        {
            public Kind Type;
            public string What;        // which perk, which wall gun, which door or debris look, which prop (else empty)
            public int Price;          // doors and debris: points to clear it
            public float[] Position;   // x, y, z
            public float Turn;         // degrees about up
            public float Tilt;         // degrees forward / back (about its own side-to-side line): stands a flat thing up
            public float Roll;         // degrees sideways (about its own front-to-back line)
            public float Scale = 1f;   // things without a size (machines, the box...): how big, 1 = as made
            public float[] Size;       // windows, doors, debris, blockers, walls: width, height, depth (metres)
            public float[] Stretch;    // a Unity marker's uneven scale on top of Scale (x, y, z; a stretched 115 rock), or none
            public bool Locked;        // the editor won't move, turn, size or delete it until unlocked
            public bool OpensWithPower; // doors and debris: not bought, they clear when the power comes on (Verrückt's split map)
            public bool NoBoard;        // wall buys: the chalk straight on the wall, not on a wooden board
            public int Zone;            // zombie spawns and barricades: the zone they belong to (spawns only once it's open); 0 = worked out from where it stands
        }

        public class MapFile
        {
            public string Map;
            public float? Hour;        // the time of day held on this map (0-24), or none: the raid's own clock
            public MapWeather.Settings Weather; // the weather held on this map, or none: the raid's own
            public bool ZombiesMap = false; // a zombies map (on the map select's Zombies page), not a layout over a normal map
            public string MapSelectSpot = null; // zombies maps: the location whose place on the map select it takes ("bigmap": Customs'), or none: its own
            public bool Moonlight = true; // zombies maps: the bright moonlit night (F12 "7. Moonlight")
            public int MaxPerks = 0;           // zombies maps: perks a player may hold (0: the original's four)
            public int BoxStart = -1;          // zombies maps: the entry index of the box spot the box always starts at (-1: random)
            public bool AlwaysPowered = false; // zombies maps: the power is on from the start, no power switch (Nacht style)
            public List<Entry> Entries = new List<Entry>();
            public List<ZoneShape> Zones = new List<ZoneShape>(); // outlined zones (F8 "Zones")
        }

        /// <summary>
        /// A zone drawn as an outline on the floor (its corners, in order), standing from a little under its lowest
        /// corner to Height over its highest: whatever's inside (spawns, barricades, doors) is in that zone.
        /// </summary>
        public class ZoneShape
        {
            public int Id = 1;
            public List<float[]> Points = new List<float[]>(); // x, y, z of each corner
            public float Height = 4f;   // metres over the highest corner
            public float Below = 1f;    // metres under the lowest corner

            [JsonIgnore]
            public float Floor => Points.Count == 0 ? 0f : Points.Min(p => p[1]) - Below;
            [JsonIgnore]
            public float Top => Points.Count == 0 ? 0f : Points.Max(p => p[1]) + Height;

            /// <summary>Inside the outline (seen from above) and between its floor and top.</summary>
            public bool Contains(Vector3 q, float margin = 0f)
            {
                if (Points.Count < 3 || q.y < Floor - margin || q.y > Top + margin)
                    return false;
                bool inside = false;
                for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
                {
                    float xi = Points[i][0], zi = Points[i][2], xj = Points[j][0], zj = Points[j][2];
                    if ((zi > q.z) != (zj > q.z) && q.x < (xj - xi) * (q.z - zi) / (zj - zi) + xi)
                        inside = !inside;
                }
                return inside || (margin > 0f && EdgeDistance(q) <= margin);
            }

            private float EdgeDistance(Vector3 q)
            {
                float best = float.MaxValue;
                var p = new Vector2(q.x, q.z);
                for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
                {
                    var a = new Vector2(Points[j][0], Points[j][2]);
                    var b = new Vector2(Points[i][0], Points[i][2]);
                    Vector2 ab = b - a;
                    float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                    best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
                }
                return best;
            }

            [JsonIgnore]
            public Vector3 Middle => Points.Count == 0 ? Vector3.zero
                : new Vector3(Points.Average(p => p[0]), Floor + Below, Points.Average(p => p[2]));
        }

        // ---------- F12 ----------

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<KeyboardShortcut> _key;
        private static ConfigEntry<float> _camSpeed, _moveStep, _turnStep, _sizeStep, _scaleStep;

        // The looks a door or a pile of debris can have (the original models to put there when the map is built)
        private static readonly string[] DoorLooks = { "metal_rusted", "wood_church", "cellar", "kino_wood", "kino_ornate", "bunker", "barbed_gate", "metal_rustic", "sandbags", "bookcase" };
        private static readonly string[] DebrisLooks = { "concrete_rubble", "wood_planks", "barbed_wire", "brick_pile", "junk_pile", "cave_rocks", "concrete_pile", "mud_rocks", "burnt_wagon", "church_bricks", "mud_sandbags", "burnt_wood", "trash_pile", "plank_heap", "gorod_rubble", "barbed_wire_2", "kino_barbed_wire", "corrugated_metal" };

        // 115 rocks: an Easter egg one (investigate them all: "115" plays) or a lore one (it only glows and hums)
        private static readonly string[] RockLooks = { "easter_egg", "lore" };
        // Lightning: small arcs crackling often, or big strikes now and then
        private static readonly string[] LightningLooks = { "crackle", "storm" };
        // Lamps (always lit) and power lights: the map's own fixtures, by prop name
        private static readonly string[] LampLooks = { "Streetlight_lamp_07_B_source_on", "Searchlight_03_source_on", "Hanging_lamp_04_source_on", "Fluorescent_lamp_02_source_on", "Fluorescent_lamp_04_source_on" };
        // Wall decals (the original's: bloody hands, drips, splatter, scrawl, chalk): data\decals\<name>.png
        internal static readonly string[] DecalLooks ={ "blood_hand_01", "blood_hand_03", "blood_hand_05", "blood_drips_01", "blood_drips_02", "blood_drips_03", "blood_splatter_02", "blood_splatter_03", "blood_smear_02", "blood_spray_01", "wall_scrawl", "chalk_wishing", "chalk_follow_steps", "scratch_marks", "decal_115_splatter" };

        private static readonly string[] Perks = { "jugg", "speed", "dtap", "deadshot", "stamin", "revive", "mule", "widow", "phd" };

        public static void Bind(ConfigFile config)
        {
            const string s = "4. Map editor (dev)";
            _enabled = config.Bind(s, "Map editor on", false,
                "In a raid: shows this map's zombies layout and lets you edit it (the window key opens the editor and a free camera). Off for normal play.");
            _key = config.Bind(s, "Editor window key", new KeyboardShortcut(KeyCode.F8),
                "Opens and closes the map editor's window and free camera (with the map editor on, in a raid).");
            _camSpeed = config.Bind(s, "Free camera speed", 8.2f, new ConfigDescription("Metres a second (Shift: 4 times).",
                new AcceptableValueRange<float>(1f, 30f)));
            _moveStep = config.Bind(s, "Arrow move step cm", 2f, new ConfigDescription("How far one arrow key press moves the selected thing.",
                new AcceptableValueRange<float>(0.5f, 100f)));
            _turnStep = config.Bind(s, "Arrow turn step degrees", 17f, new ConfigDescription("How far Alt + Left / Right (and the wheel while carrying) turns it.",
                new AcceptableValueRange<float>(0.5f, 90f)));
            _sizeStep = config.Bind(s, "Arrow size step cm", 12f, new ConfigDescription("How much Ctrl + arrows resize it.",
                new AcceptableValueRange<float>(1f, 100f)));
            _scaleStep = config.Bind(s, "Wheel scale step percent", 12.8f, new ConfigDescription("How much Alt + wheel scales it per notch.",
                new AcceptableValueRange<float>(1f, 50f)));
        }

        private static string[] _wallGuns;

        private static string[] WallGuns()
        {
            if (_wallGuns != null)
                return _wallGuns;
            try
            {
                string path = Path.Combine(ZombiesPlugin.Dir, "box_builds.json");
                var all = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(path));
                // The wall's own guns first, then the rest
                _wallGuns = all.OrderBy(t => (string)t["role"] == "wall" ? 0 : 1).Select(t => (string)t["id"]).Prepend(ZombiesGrenades.WallId).ToArray();
            }
            catch (Exception)
            {
                _wallGuns = new[] { "mp5k" };
            }
            return _wallGuns;
        }

        /// <summary>The choices for a type's "What" (perk, wall gun, look), or null when it has none.</summary>
        private static string[] Variants(Kind kind) => kind switch
        {
            Kind.PerkMachine => Perks,
            Kind.WallBuy => WallGuns(),
            Kind.Door => DoorLooks,
            Kind.Debris => DebrisLooks,
            Kind.Rock => RockLooks,
            Kind.Lightning => LightningLooks,
            Kind.Lamp => LampLooks,
            Kind.PowerLight => LampLooks,
            Kind.Decal => DecalLooks,
            _ => null,
        };

        private static bool HasPrice(Kind kind) => kind == Kind.Door || kind == Kind.Debris || kind == Kind.WallBuy;

        // ---------- Running ----------

        internal static MapEditor Instance;

        /// <summary>The editor window is open (its own settings are being tried).</summary>
        internal static bool IsOpen => Instance != null && Instance._open;

        private MapFile _map;
        private readonly List<MapVisual> _visuals = new List<MapVisual>();
        private int _selected = -1;
        private string _location;
        private bool _dirty;
        private readonly FreeCam _cam = new FreeCam();
        private bool _open;

        // What "Place" puts down next
        private Kind _kind = Kind.ZombieSpawn;
        private int _zone = 1;          // the zone new zones, zombie spawns and barricades go in

        // Things that belong to a zone: the zone boxes themselves, zombie spawns, barricades
        private static bool HasZone(Kind kind) => kind == Kind.Zone || kind == Kind.ZombieSpawn || kind == Kind.Window;
        private readonly Dictionary<Kind, int> _variant = new Dictionary<Kind, int>();
        private int _price = 750;

        // A thing following the mouse (new, or being moved), and where it was before (to put it back on Esc)
        private int _carrying = -1;
        private bool _carryingNew;
        private float[] _carryFrom;
        private float _carryTurnFrom;

        private GameObject _selectionBox;

        private static string MapsDir => Path.Combine(ZombiesPlugin.Dir, "maps");

        /// <summary>
        /// Where layout files are: each map mod's own maps\ folder (BepInEx\plugins\Kennerz_ZM_&lt;Map&gt;\maps, shipped
        /// with the map), then the core's (dev layouts over normal maps, and new ones until a map mod holds them).
        /// </summary>
        internal static IEnumerable<string> MapsDirs()
        {
            string plugins = Path.GetDirectoryName(ZombiesPlugin.Dir);
            if (plugins != null && Directory.Exists(plugins))
                foreach (string mod in Directory.GetDirectories(plugins, "Kennerz_ZM_*"))
                    if (!string.Equals(Path.GetFullPath(mod), Path.GetFullPath(ZombiesPlugin.Dir), StringComparison.OrdinalIgnoreCase)
                        && Directory.Exists(Path.Combine(mod, "maps")))
                        yield return Path.Combine(mod, "maps");
            yield return MapsDir;
        }

        private void Awake() => Instance = this;

        private void Update()
        {
#if RELEASE
            // Release builds: the map editor (F8) and the dev hooks are not there at all
            return;
#endif
            string location = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null && !ZombiesPlugin.InHideout
                ? Singleton<GameWorld>.Instance.LocationId : null;
            bool on = _enabled != null && _enabled.Value && location != null;
            if (!on)
            {
                if (_location != null)
                    Clear();
                return;
            }
            if (location != _location)
            {
                _location = location;
                Load();
            }
            if (_key.Value.IsDown() && !Typing)
                SetOpen(!_open);
            if (_open)
            {
                _cam.Speed = _camSpeed.Value;
                _cam.Tick(!AnyModifier() && !Typing);
                Carry();
                ZoneClicks();
                Click();
                Drag();
            }
            if (!Typing)
            {
                Nudge();
                UndoKeys();
            }
            Wheel();
            DriveSliders();
            CommitWhenSettled();
            UpdateSelectionBox();
        }

        private bool _cursorShown;

        private void LateUpdate()
        {
#if RELEASE
            // Release builds: the map editor (F8) and the dev hooks are not there at all
            return;
#endif
            // The cursor for the editor (hidden while looking about with the right button held)
            bool want = _open && !Input.GetMouseButton(1);
            if (want != _cursorShown)
                ShowCursor(want);
        }

        /// <summary>
        /// Asks EFT itself to show or hide the cursor in game (its own "show cursor" event). Setting Cursor.visible
        /// directly fought EFT's InputManager, which hides it again every frame in a raid: both redid the cursor every
        /// frame, and that made the game crawl while the editor was open.
        /// </summary>
        private void ShowCursor(bool show)
        {
            _cursorShown = show;
            try
            {
                EFT.GlobalEvents.GlobalEventsController.Instance?.CreateCommonEvent<EFT.GlobalEvents.ToggleShowInGameCursorEvent>().Invoke(show);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Map editor: couldn't {(show ? "show" : "hide")} the cursor: {e.Message}");
            }
        }

        // The asset search box has the keyboard: the camera and arrow keys leave the keys alone
        private static bool Typing => GUIUtility.keyboardControl != 0;

        private static bool AnyModifier() =>
            Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        private void SetOpen(bool open)
        {
            if (open == _open)
                return;
            if (open)
            {
                if (!ZombiesPlugin.CurrentPlayer(out _, out Player player) || !_cam.Start(player))
                    return;
                // Testing ends when the editor opens again
                ZombiesMatch.StopTest();
                ShowVisuals(true);
                // Every window back where it starts (one dragged off screen or lost comes back)
                _window = new Rect(-1f, 60f, 380f, 640f);
                _assetWindow = new Rect(20f, 60f, 420f, 700f);
                _zoneWindow = new Rect(460f, 60f, 330f, 460f);
                _open = true;
            }
            else
            {
                CancelCarry();
                _cam.Stop();
                _open = false;
                // Closing keeps what was changed (time, weather, placed things): no lost work for a missed Save
                if (_dirty)
                    Save();
                // In a zombies match the layout is built again from the file: what was placed is live at once (solid,
                // working), and the editor's previews step aside for it
                if (ZombiesMatch.Active)
                {
                    ShowVisuals(false);
                    ZombiesMatch.RebuildNow();
                }
                if (_cursorShown)
                    ShowCursor(false);
            }
        }

        public void Load()
        {
            if (_location == null)
                return;
            string location = _location;
            ClearVisuals();
            _location = location;
            _map = Read(_location) ?? new MapFile { Map = _location };
            foreach (Entry entry in _map.Entries)
                _visuals.Add(Marker(entry));
            _selected = -1;
            _editZone = -1;
            _dirty = false;
            _undo.Clear();
            _redo.Clear();
            _pending = false;
            _committed = Snapshot();
            if (_map.Hour.HasValue)
                MapTime.Hold(_map.Hour.Value);
            if (_map.Weather != null)
                MapWeather.Hold(_map.Weather);
            Highlight();
            // The previews (zones, arrows, outlines, spawns) only show while the editor is open
            if (!_open)
                ShowVisuals(false);
            ZombiesPlugin.Log.LogInfo($"Map editor: {_location}, {_map.Entries.Count} placed");
            ZombiesPlugin.Notify($"Map editor: {_location} ({_map.Entries.Count} placed). {_key.Value} opens the editor.");
        }

        public void Save()
        {
            if (_map == null)
                return;
            string file = FileFor(_map.Map);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, JsonConvert.SerializeObject(_map, Formatting.Indented, new Newtonsoft.Json.Converters.StringEnumConverter()));
            _dirty = false;
            ZombiesMaps.Forget();
            ZombiesPlugin.Log.LogInfo($"Map editor: saved {_map.Entries.Count} to {file}");
            ZombiesPlugin.Notify($"Saved {_map.Entries.Count} placed things to maps\\{Path.GetFileName(file)}.");
        }

        /// <summary>
        /// A map's zombies layout file: maps\&lt;location&gt;zombies.json (Customs: bigmapzombies.json). Only our own
        /// layout, laid over the map while the game runs: the map itself is never changed.
        /// </summary>
        internal static string FileFor(string location)
        {
            // Saved back where it was found (a map mod's layout stays in that map mod); a new one goes in the core's
            foreach (string dir in MapsDirs())
            {
                string file = Path.Combine(dir, location + "zombies.json");
                if (File.Exists(file))
                    return file;
            }
            return Path.Combine(MapsDir, location + "zombies.json");
        }

        /// <summary>The saved layout for a map (its LocationId), or null when there's none or it can't be read.</summary>
        internal static MapFile Read(string location)
        {
            string file = FileFor(location);
            // Layouts saved before the rename (maps\bigmap.json): read, and saved under the new name from then on
            if (!File.Exists(file))
                file = Path.Combine(MapsDir, location + ".json");
            if (!File.Exists(file))
                return null;
            try
            {
                MapFile map = JsonConvert.DeserializeObject<MapFile>(File.ReadAllText(file));
                if (map != null)
                    map.Map = location;
                return map;
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Map editor: couldn't read {file}: {e.Message}");
                return null;
            }
        }

        private void Clear()
        {
            SetOpen(false);
            ClearVisuals();
        }

        private void ClearVisuals()
        {
            foreach (MapVisual v in _visuals)
                if (v != null)
                    Destroy(v.gameObject);
            _visuals.Clear();
            _map = null;
            _selected = -1;
            _carrying = -1;
            _location = null;
        }

        private void ShowVisuals(bool on)
        {
            foreach (MapVisual v in _visuals)
                if (v != null)
                    v.gameObject.SetActive(on);
        }

        /// <summary>Saves, goes back to the player and sets the layout out as a match would.</summary>
        private void Test()
        {
            Save();
            SetOpen(false);
            ShowVisuals(false);
            ZombiesMatch.StartTest();
        }

        /// <summary>Moves the player's body to the ground under the camera, then goes back to it.</summary>
        private void DropPlayerHere()
        {
            if (!ZombiesPlugin.CurrentPlayer(out _, out Player player))
                return;
            Vector3 at = _cam.Position;
            if (Physics.Raycast(at, Vector3.down, out RaycastHit ground, 100f, ~0, QueryTriggerInteraction.Ignore))
                at = ground.point;
            SetOpen(false);
            player.Teleport(at + Vector3.up * 0.1f, false);
        }

        // ---------- Placing, selecting, carrying ----------

        private string CurrentVariant(Kind kind)
        {
            string[] v = Variants(kind);
            if (v == null)
                return "";
            _variant.TryGetValue(kind, out int i);
            return v[Mathf.Clamp(i, 0, v.Length - 1)];
        }

        /// <summary>A new thing of the chosen kind, following the mouse until clicked down.</summary>
        private void PlaceNew()
        {
            if (_map == null)
                return;
            if (_kind == Kind.Prop && string.IsNullOrEmpty(_asset))
            {
                ZombiesPlugin.Notify("Pick a prop in the asset list first.", true);
                return;
            }
            CancelCarry();
            var entry = new Entry
            {
                Type = _kind,
                What = _pendingWhat ?? (_kind == Kind.Prop ? _asset : _kind == Kind.Zone ? _zone.ToString() : CurrentVariant(_kind)),
                Zone = _kind == Kind.ZombieSpawn || _kind == Kind.Window ? _zone : 0,
                Price = HasPrice(_kind) ? _price : 0,
                Position = Vec(_cam.Position + _cam.FlatForward * 3f),
                // Its front faces the camera
                Turn = Quaternion.LookRotation(-_cam.FlatForward, Vector3.up).eulerAngles.y,
                Size = DefaultSize(_kind),
            };
            _pendingWhat = null;
            _map.Entries.Add(entry);
            _visuals.Add(Marker(entry));
            _selected = _map.Entries.Count - 1;
            Touch();
            _carrying = _selected;
            _carryingNew = true;
            Highlight();
        }

        private void StartCarry()
        {
            if (!Valid(_selected) || IsLocked(_selected))
                return;
            _carrying = _selected;
            _carryingNew = false;
            _dragging = false;
            _grab = Vector3.zero;
            _carryFrom = (float[])_map.Entries[_selected].Position.Clone();
            _carryTurnFrom = _map.Entries[_selected].Turn;
        }

        private void CancelCarry()
        {
            if (!Valid(_carrying))
            {
                _carrying = -1;
                return;
            }
            int i = _carrying;
            _carrying = -1;
            if (_carryingNew)
                Delete(i);
            else
            {
                _map.Entries[i].Position = _carryFrom;
                _map.Entries[i].Turn = _carryTurnFrom;
                _visuals[i].Apply();
            }
        }

        /// <summary>The carried thing sits where the mouse points (a wall buy flat on the wall it points at).</summary>
        private void Carry()
        {
            if (!Valid(_carrying))
                return;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CancelCarry();
                return;
            }
            Entry e = _map.Entries[_carrying];
            if (!MouseRay(out Ray ray))
                return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift)
            {
                // Slides at its own height
                var level = new Plane(Vector3.up, Pos(e));
                if (level.Raycast(ray, out float along))
                {
                    e.Position = Vec(ray.GetPoint(along) + _grab);
                    _visuals[_carrying].Apply();
                    Touch();
                }
                DropIfDone();
                return;
            }
            if (!WorldHit(ray, out RaycastHit hit))
            {
                DropIfDone();
                return;
            }
            Vector3 at = hit.point + _grab;
            if (e.Type == Kind.WallBuy && Mathf.Abs(hit.normal.y) < 0.5f)
            {
                Vector3 normal = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
                e.Turn = Quaternion.LookRotation(normal, Vector3.up).eulerAngles.y;
                at += normal * 0.03f - Vector3.up * (_visuals[_carrying].Size.y * 0.5f);
            }
            else if (e.Type == Kind.Decal && Mathf.Abs(hit.normal.y) < 0.5f)
            {
                // A wall decal lies flat on the wall, its middle where the mouse points (its spot is the bottom edge)
                Vector3 normal = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
                e.Turn = Quaternion.LookRotation(normal, Vector3.up).eulerAngles.y;
                Renderer quad = _visuals[_carrying].GetComponentInChildren<Renderer>();
                float half = quad != null ? quad.bounds.size.y * 0.5f : 0f;
                at += normal * 0.005f - Vector3.up * half;
            }
            e.Position = Vec(at);
            _visuals[_carrying].Apply();
            Touch();
            DropIfDone();
        }

        /// <summary>Dragged: let go of the mouse to put it down. Placed or "Move with mouse": click (not on a window).</summary>
        private void DropIfDone()
        {
            if (_dragging ? !Input.GetMouseButton(0) : Input.GetMouseButtonDown(0) && !OverWindow())
            {
                _carrying = -1;
                _dragging = false;
                _pressed = -1;
            }
        }

        // Left button held on a placed thing: once the mouse moves a little, it's dragged
        private int _pressed = -1;
        private Vector2 _pressAt;
        private bool _dragging;
        private Vector3 _grab;

        private void Drag()
        {
            if (_pressed < 0 || Valid(_carrying) || IsLocked(_pressed))
                return;
            if (!Input.GetMouseButton(0) || !Valid(_pressed))
            {
                _pressed = -1;
                return;
            }
            if (((Vector2)Input.mousePosition - _pressAt).sqrMagnitude < 36f)
                return;
            _selected = _pressed;
            StartCarry();
            _dragging = true;
            // Held where it was grabbed, not by its foot
            Entry e = _map.Entries[_carrying];
            if (MouseRay(out Ray ray))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                var level = new Plane(Vector3.up, Pos(e));
                if (shift && level.Raycast(ray, out float along))
                    _grab = Pos(e) - ray.GetPoint(along);
                else if (!shift && WorldHit(ray, out RaycastHit hit))
                    _grab = Vector3.ProjectOnPlane(Pos(e) - hit.point, Vector3.up);
            }
        }

        /// <summary>Left click (not on the window, not carrying) selects the placed thing under the mouse.</summary>
        private void Click()
        {
            if (Valid(_carrying) || !Input.GetMouseButtonDown(0) || OverWindow() || (ValidZone(_editZone) && Alt()) || !MouseRay(out Ray ray))
                return;
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _visuals.Count; i++)
                if (_visuals[i] != null && _visuals[i].WorldBounds().IntersectRay(ray, out float d) && d < bestDistance)
                {
                    best = i;
                    bestDistance = d;
                }
            _selected = best;
            _pressed = best;
            _pressAt = Input.mousePosition;
            Highlight();
        }

        private bool MouseRay(out Ray ray)
        {
            Camera cam = _cam.Camera;
            ray = cam != null ? cam.ScreenPointToRay(Input.mousePosition) : default;
            return cam != null;
        }

        private readonly RaycastHit[] _hits = new RaycastHit[32];

        /// <summary>The nearest world surface along the ray (not the player's own body).</summary>
        private bool WorldHit(Ray ray, out RaycastHit hit)
        {
            hit = default;
            ZombiesPlugin.CurrentPlayer(out _, out Player player);
            Transform body = player != null ? ((Component)player).transform : null;
            int count = Physics.RaycastNonAlloc(ray, _hits, 300f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (body != null && _hits[i].collider.transform.IsChildOf(body))
                    continue;
                if (_hits[i].distance < best)
                {
                    best = _hits[i].distance;
                    hit = _hits[i];
                }
            }
            // Things already placed (they don't collide in the editor): so one can go on top of another
            for (int i = 0; i < _visuals.Count; i++)
            {
                if (i == _carrying || _visuals[i] == null || !_visuals[i].gameObject.activeSelf)
                    continue;
                if (_visuals[i].Raycast(ray, out float d, out Vector3 normal) && d < best)
                {
                    best = d;
                    hit.point = ray.GetPoint(d);
                    hit.normal = normal;
                    hit.distance = d;
                }
            }
            return best < float.MaxValue;
        }

        private void Delete(int i)
        {
            if (!Valid(i))
                return;
            _map.Entries.RemoveAt(i);
            Destroy(_visuals[i].gameObject);
            _visuals.RemoveAt(i);
            _selected = Mathf.Min(_selected, _map.Entries.Count - 1);
            Touch();
            Highlight();
        }

        private void Duplicate()
        {
            if (!Valid(_selected))
                return;
            Entry e = _map.Entries[_selected];
            var copy = JsonConvert.DeserializeObject<Entry>(JsonConvert.SerializeObject(e));
            _map.Entries.Add(copy);
            _visuals.Add(Marker(copy));
            _selected = _map.Entries.Count - 1;
            Touch();
            StartCarry();
            Highlight();
        }

        /// <summary>The selected thing's look changed: its model is made again.</summary>
        private void Remake(int i)
        {
            if (!Valid(i))
                return;
            Destroy(_visuals[i].gameObject);
            _visuals[i] = Marker(_map.Entries[i]);
            Touch();
            Highlight();
        }

        /// <summary>A placed thing as the editor shows it: its look, plus a direction arrow where it has a facing.</summary>
        private static MapVisual Marker(Entry e)
        {
            MapVisual v = MapVisual.Make(e);
            v.AddArrow();
            return v;
        }

        private bool IsLocked(int i) => Valid(i) && _map.Entries[i].Locked;

        private void SetAllLocked(bool locked)
        {
            if (_map == null)
                return;
            CancelCarry();
            foreach (Entry e in _map.Entries)
                e.Locked = locked;
            Touch();
        }

        // ---------- Undo ----------

        private const int MaxUndo = 200;
        private const float SettleSeconds = 0.4f;
        private readonly List<string> _undo = new List<string>(), _redo = new List<string>();
        private string _committed;   // the layout as of the last undo step
        private bool _pending;       // changed since then
        private float _lastTouch;

        /// <summary>Something changed: unsaved, and an undo step once the changes stop (a whole drag is one step).</summary>
        private void Touch()
        {
            _dirty = true;
            _pending = true;
            _lastTouch = Time.unscaledTime;
        }

        private string Snapshot() => _map == null ? null : JsonConvert.SerializeObject(_map);

        private void CommitWhenSettled()
        {
            if (!_pending || Time.unscaledTime - _lastTouch < SettleSeconds || Input.GetMouseButton(0) || Valid(_carrying)
                || NudgeKeys.Any(Input.GetKey))
                return;
            Commit();
        }

        private void Commit()
        {
            _pending = false;
            string now = Snapshot();
            if (now == null || now == _committed)
                return;
            if (_committed != null)
            {
                _undo.Add(_committed);
                if (_undo.Count > MaxUndo)
                    _undo.RemoveAt(0);
            }
            _redo.Clear();
            _committed = now;
        }

        private void UndoKeys()
        {
            if (!_open || !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                return;
            if (Input.GetKeyDown(KeyCode.Z))
                Undo();
            else if (Input.GetKeyDown(KeyCode.Y))
                Redo();
            else if (Input.GetKeyDown(KeyCode.C))
                Copy();
            else if (Input.GetKeyDown(KeyCode.V))
                Paste();
        }

        // ---------- Copy and paste ----------

        private string _clipboard; // the copied thing, as saved

        private void Copy()
        {
            if (!Valid(_selected))
                return;
            _clipboard = JsonConvert.SerializeObject(_map.Entries[_selected]);
            ZombiesPlugin.Notify($"Copied {Describe(_map.Entries[_selected])}. Ctrl + V pastes it.");
        }

        /// <summary>A copy of the copied thing (unlocked), following the mouse until clicked down (Esc: no paste).</summary>
        private void Paste()
        {
            if (_map == null || _clipboard == null)
                return;
            CancelCarry();
            Entry copy = JsonConvert.DeserializeObject<Entry>(_clipboard);
            if (copy == null)
                return;
            copy.Locked = false;
            _map.Entries.Add(copy);
            _visuals.Add(Marker(copy));
            _selected = _map.Entries.Count - 1;
            _carrying = _selected;
            _carryingNew = true;
            _dragging = false;
            _grab = Vector3.zero;
            Touch();
            Highlight();
        }

        private void Undo()
        {
            if (_map == null)
                return;
            CancelCarry();
            if (_pending)
                Commit();
            if (_undo.Count == 0)
            {
                ZombiesPlugin.Notify("Nothing to undo.", true);
                return;
            }
            _redo.Add(_committed);
            string back = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            Restore(back);
        }

        private void Redo()
        {
            if (_map == null || _redo.Count == 0)
                return;
            CancelCarry();
            if (_pending)
                Commit();
            if (_redo.Count == 0)
                return;
            _undo.Add(_committed);
            string again = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            Restore(again);
        }

        /// <summary>Puts the layout back as it was in a snapshot (every placed thing made again).</summary>
        private void Restore(string snapshot)
        {
            MapFile map = JsonConvert.DeserializeObject<MapFile>(snapshot);
            if (map == null)
                return;
            foreach (MapVisual v in _visuals)
                if (v != null)
                    Destroy(v.gameObject);
            _visuals.Clear();
            map.Map = _map.Map;
            _map = map;
            foreach (Entry entry in _map.Entries)
                _visuals.Add(Marker(entry));
            _selected = Mathf.Min(_selected, _map.Entries.Count - 1);
            _carrying = -1;
            _pressed = -1;
            _committed = snapshot;
            _pending = false;
            _dirty = true;
            if (_map.Hour.HasValue)
                MapTime.Hold(_map.Hour.Value);
            if (_map.Weather != null)
                MapWeather.Hold(_map.Weather);
            Highlight();
        }

        private bool Valid(int i) => _map != null && i >= 0 && i < _map.Entries.Count && i < _visuals.Count;

        private static string Describe(Entry e) =>
            e.Type + (string.IsNullOrEmpty(e.What) ? "" : $" ({(e.Type == Kind.Prop ? AssetCatalog.Short(e.What) : e.What)})")
            + (e.OpensWithPower ? ", Power" : e.Price > 0 ? $", {e.Price} points" : "")
            + (e.Zone > 0 ? $", zone {e.Zone}" : "");

        /// <summary>Its turn, then its tilt and roll about its own axes.</summary>
        internal static Quaternion Rotation(Entry e) => Quaternion.Euler(e.Tilt, e.Turn, e.Roll);

        internal static Vector3 Pos(Entry e) => new Vector3(e.Position[0], e.Position[1], e.Position[2]);

        private static float[] Vec(Vector3 v) => new[] { v.x, v.y, v.z };

        private void Changed(int i)
        {
            if (!Valid(i))
                return;
            _visuals[i].Apply();
            Touch();
        }

        // ---------- Arrow keys and the wheel ----------

        private const float RepeatDelay = 0.35f, RepeatInterval = 0.03f;
        private static float StepMetres => _moveStep.Value / 100f;
        private static float StepDegrees => _turnStep.Value;
        private static readonly KeyCode[] NudgeKeys = { KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow };
        private float _nextRepeat;

        private Vector3 ViewForward()
        {
            if (_cam.Active)
                return _cam.FlatForward;
            Camera cam = ZombiesInteraction.ActiveCamera;
            return cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized : Vector3.forward;
        }

        /// <summary>
        /// Arrows move the selected thing as you face; Shift + Up / Down raises and lowers it; Alt + Left / Right turns it,
        /// Alt + Up / Down tilts it forward / back, Alt + Shift + Left / Right rolls it sideways;
        /// Ctrl + arrows resize a window, door, debris, blocker or wall (Left / Right its width, Up / Down its height);
        /// Ctrl + Shift + Up / Down its depth.
        /// </summary>
        private void Nudge()
        {
            if (!Valid(_selected) || IsLocked(_selected))
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

            Entry e = _map.Entries[_selected];
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            Vector3 p = Pos(e);
            if (ctrl)
            {
                if (e.Size == null)
                    return;
                float step = _sizeStep.Value / 100f;
                if (shift && key == KeyCode.UpArrow) e.Size[2] += step;
                else if (shift && key == KeyCode.DownArrow) e.Size[2] = Mathf.Max(0.05f, e.Size[2] - step);
                else if (key == KeyCode.LeftArrow) e.Size[0] = Mathf.Max(0.2f, e.Size[0] - step);
                else if (key == KeyCode.RightArrow) e.Size[0] += step;
                else if (key == KeyCode.UpArrow) e.Size[1] += step;
                else if (key == KeyCode.DownArrow) e.Size[1] = Mathf.Max(0.2f, e.Size[1] - step);
                else return;
            }
            else if (alt)
            {
                if (shift && key == KeyCode.LeftArrow) e.Roll -= StepDegrees;
                else if (shift && key == KeyCode.RightArrow) e.Roll += StepDegrees;
                else if (key == KeyCode.LeftArrow) e.Turn -= StepDegrees;
                else if (key == KeyCode.RightArrow) e.Turn += StepDegrees;
                else if (key == KeyCode.UpArrow) e.Tilt -= StepDegrees;
                else if (key == KeyCode.DownArrow) e.Tilt += StepDegrees;
                else return;
            }
            else if (shift)
            {
                if (key == KeyCode.UpArrow) p.y += StepMetres;
                else if (key == KeyCode.DownArrow) p.y -= StepMetres;
                else return;
            }
            else
            {
                Vector3 forward = ViewForward();
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                p += (key == KeyCode.UpArrow ? forward : key == KeyCode.DownArrow ? -forward : key == KeyCode.RightArrow ? right : -right) * StepMetres;
            }
            e.Position = Vec(p);
            Changed(_selected);
        }

        private static float WheelStep => _scaleStep.Value / 100f;
        private static float WheelDegrees => _turnStep.Value;

        /// <summary>
        /// Alt + mouse wheel scales the selected thing evenly (the scale step a notch): its size if it has one, else its
        /// Scale. The wheel alone turns the thing being carried (the turn step a notch).
        /// </summary>
        private void Wheel()
        {
            // Windows turns Shift + wheel into a sideways scroll: either way counts
            Vector2 scroll = Input.mouseScrollDelta;
            float wheel = !Mathf.Approximately(scroll.y, 0f) ? scroll.y : scroll.x;
            if (Mathf.Approximately(wheel, 0f) || (_open && OverWindow()))
                return;
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!alt && !ctrl && !shift)
            {
                if (Valid(_carrying))
                {
                    _map.Entries[_carrying].Turn += Mathf.Sign(wheel) * WheelDegrees;
                    Changed(_carrying);
                }
                return;
            }
            if (!Valid(_selected) || IsLocked(_selected))
                return;
            Entry e = _map.Entries[_selected];
            if (ctrl || shift)
            {
                // Ctrl + wheel: width; Shift + wheel: height (the size step a notch). A thing sized only by its
                // scale (a prop, a machine) gets a size from how big it stands now, then stretches
                if (e.Size == null)
                {
                    Vector3 now = _visuals[_selected].Size;
                    e.Size = new[] { now.x, now.y, now.z };
                }
                int axis = ctrl ? 0 : 1;
                e.Size[axis] = Mathf.Max(0.05f, e.Size[axis] + Mathf.Sign(wheel) * _sizeStep.Value / 100f);
                Changed(_selected);
                return;
            }
            float factor = wheel > 0f ? 1f + WheelStep : 1f / (1f + WheelStep);
            if (e.Size != null)
                for (int i = 0; i < 3; i++)
                    e.Size[i] = Mathf.Max(0.05f, e.Size[i] * factor);
            else
                e.Scale = Mathf.Clamp((e.Scale > 0f ? e.Scale : 1f) * factor, 0.1f, 10f);
            Changed(_selected);
        }

        // ---------- Zones: outlined on the floor ----------

        private const int ZoneWindowId = 0x5A4D5A; // "ZMZ"
        private Rect _zoneWindow = new Rect(460f, 60f, 330f, 460f);
        private bool _zonesShown;
        private int _editZone = -1;   // the zone being outlined (Alt + clicks add and take away its corners)
        private Vector2 _zoneScroll;
        private static Material _lineMaterial;
        private GUIStyle _zoneLabel;

        private bool ValidZone(int i) => _map != null && i >= 0 && i < _map.Zones.Count;

        private static bool Alt() => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        private static Color ZoneColour(int id) => Color.HSVToRGB(Mathf.Repeat(id * 0.137f, 1f), 0.75f, 1f);

        private static Vector3 V(float[] p) => new Vector3(p[0], p[1], p[2]);

        /// <summary>Outlining a zone: Alt + left click adds a corner where the mouse points, Alt + right click takes the last away.</summary>
        private void ZoneClicks()
        {
            if (!ValidZone(_editZone) || !Alt() || OverWindow())
                return;
            ZoneShape z = _map.Zones[_editZone];
            if (Input.GetMouseButtonDown(0) && MouseRay(out Ray ray) && WorldHit(ray, out RaycastHit hit))
            {
                z.Points.Add(Vec(hit.point));
                Touch();
            }
            else if (Input.GetMouseButtonDown(1) && z.Points.Count > 0)
            {
                z.Points.RemoveAt(z.Points.Count - 1);
                Touch();
            }
        }

        private void DrawZones(int id)
        {
            GUILayout.Label("Zones 1 and 2 are open from the start; any other opens when a door or debris on its edge is " +
                            "opened. Spawns, barricades, doors and debris inside an outline are in that zone.", _wrap);
            _zoneScroll = GUILayout.BeginScrollView(_zoneScroll, GUILayout.Height(150f));
            for (int i = 0; i < _map.Zones.Count; i++)
            {
                ZoneShape z = _map.Zones[i];
                GUI.color = i == _editZone ? Color.yellow : ZoneColour(z.Id);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Zone {z.Id} ({z.Points.Count} corners)", GUILayout.Width(130f));
                GUI.color = Color.white;
                if (GUILayout.Button(i == _editZone ? "Done" : "Edit"))
                    _editZone = i == _editZone ? -1 : i;
                if (GUILayout.Button("Redo"))
                {
                    z.Points.Clear();
                    _editZone = i;
                    Touch();
                }
                bool delete = GUILayout.Button("Delete");
                GUILayout.EndHorizontal();
                if (delete)
                {
                    _map.Zones.RemoveAt(i);
                    _editZone = _editZone == i ? -1 : _editZone > i ? _editZone - 1 : _editZone;
                    Touch();
                    break;
                }
            }
            GUI.color = Color.white;
            GUILayout.EndScrollView();
            if (GUILayout.Button("New zone"))
            {
                int next = 1;
                while (_map.Zones.Any(z => z.Id == next))
                    next++;
                _map.Zones.Add(new ZoneShape { Id = next });
                _editZone = _map.Zones.Count - 1;
                Touch();
            }
            if (ValidZone(_editZone))
            {
                ZoneShape z = _map.Zones[_editZone];
                GUILayout.Space(6f);
                GUILayout.Label($"Outlining zone {z.Id}", GUI.skin.box, GUILayout.ExpandWidth(true));
                int number = Mathf.RoundToInt(Slider("Zone number", z.Id, 1f, 20f, "0"));
                float height = Slider("Height over the floor", z.Height, 0.5f, 30f, "0.0 m");
                float below = Slider("Reach under the floor", z.Below, 0f, 10f, "0.0 m");
                if (number != z.Id || !Mathf.Approximately(height, z.Height) || !Mathf.Approximately(below, z.Below))
                {
                    z.Id = number;
                    z.Height = height;
                    z.Below = below;
                    Touch();
                }
                GUILayout.Label("Alt + left click: a corner where the mouse points. Alt + right click: take the last one away. " +
                                "Go round the area with its barricades, its zombie spawns and the doors and debris that open it.", _wrap);
                GUILayout.BeginHorizontal();
                GUI.enabled = z.Points.Count > 0;
                if (GUILayout.Button("Remove last corner"))
                {
                    z.Points.RemoveAt(z.Points.Count - 1);
                    Touch();
                }
                GUI.enabled = true;
                if (GUILayout.Button("Done"))
                    _editZone = -1;
                GUILayout.EndHorizontal();
                if (z.Points.Count > 0 && z.Points.Count < 3)
                    GUILayout.Label("A zone needs 3 corners or more.", _wrap);
            }
            GUI.DragWindow();
        }

        /// <summary>Each zone's number over its middle.</summary>
        private void DrawZoneLabels()
        {
            Camera cam = _cam.Camera;
            if (cam == null || _map.Zones.Count == 0)
                return;
            _zoneLabel ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 16 };
            foreach (ZoneShape z in _map.Zones.Where(z => z.Points.Count > 0))
            {
                Vector3 s = cam.WorldToScreenPoint(z.Middle + Vector3.up * 1.5f);
                if (s.z <= 0f)
                    continue;
                _zoneLabel.normal.textColor = ZoneColour(z.Id);
                GUI.Label(new Rect(s.x - 60f, Screen.height - s.y - 12f, 120f, 24f), $"Zone {z.Id}", _zoneLabel);
            }
        }

        /// <summary>The outlines, drawn through walls: on the floor, at the top, and a post at every corner.</summary>
        private void OnRenderObject()
        {
            if (!_open || _map == null || _map.Zones.Count == 0)
                return;
            if (_lineMaterial == null)
            {
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null)
                    return;
                _lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                _lineMaterial.SetInt("_ZWrite", 0);
            }
            _lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            for (int i = 0; i < _map.Zones.Count; i++)
            {
                ZoneShape z = _map.Zones[i];
                int n = z.Points.Count;
                if (n == 0)
                    continue;
                bool editing = i == _editZone;
                Color c = editing ? Color.yellow : ZoneColour(z.Id);
                float floor = z.Floor, top = z.Top;
                for (int k = 0; k < n; k++)
                {
                    Vector3 a = V(z.Points[k]);
                    // The corner's post, floor to top
                    GL.Color(c);
                    GL.Vertex(new Vector3(a.x, floor, a.z));
                    GL.Vertex(new Vector3(a.x, top, a.z));
                    if (n < 2 || (k == n - 1 && n < 3))
                        continue;
                    Vector3 b = V(z.Points[(k + 1) % n]);
                    // The closing edge of one being outlined is dimmer (it closes itself)
                    GL.Color(editing && k == n - 1 ? new Color(c.r, c.g, c.b, 0.4f) : c);
                    GL.Vertex(a + Vector3.up * 0.05f);
                    GL.Vertex(b + Vector3.up * 0.05f);
                    GL.Vertex(new Vector3(a.x, top, a.z));
                    GL.Vertex(new Vector3(b.x, top, b.z));
                }
            }
            // Where the next corner would go
            if (ValidZone(_editZone) && Alt() && !OverWindow() && MouseRay(out Ray ray) && WorldHit(ray, out RaycastHit hit))
            {
                ZoneShape z = _map.Zones[_editZone];
                GL.Color(Color.white);
                if (z.Points.Count > 0)
                {
                    GL.Vertex(V(z.Points[z.Points.Count - 1]) + Vector3.up * 0.05f);
                    GL.Vertex(hit.point + Vector3.up * 0.05f);
                }
                GL.Vertex(hit.point);
                GL.Vertex(hit.point + Vector3.up * 2f);
            }
            GL.End();
            GL.PopMatrix();
        }

        // ---------- Window ----------

        private const int WindowId = 0x5A4D45, AssetWindowId = 0x5A4D41; // "ZME", "ZMA"
        private Rect _window = new Rect(-1f, 60f, 380f, 640f);
        private Rect _assetWindow = new Rect(20f, 60f, 420f, 700f);
        private Vector2 _listScroll;
        private GUIStyle _wrap;

        // Move sliders (they spring back to the middle): how hard each is pushed, -1..1
        private float _moveRight, _moveUp, _moveForward;

        private bool OverWindow()
        {
            Vector2 mouse = Input.mousePosition;
            var gui = new Vector2(mouse.x, Screen.height - mouse.y);
            return _open && (_window.Contains(gui) || _assetWindow.Contains(gui) || (_zonesShown && _zoneWindow.Contains(gui)));
        }

        private void OnGUI()
        {
#if RELEASE
            // Release builds: the map editor (F8) and the dev hooks are not there at all
            return;
#endif
            if (!_open || _map == null)
                return;
            _wrap ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
            // The editor on the right, the asset list on the left
            if (_window.x < 0f)
                _window.x = Screen.width - _window.width - 20f;
            _window = GUILayout.Window(WindowId, _window, DrawWindow, $"Zombies map editor: {_map.Map}{(_dirty ? " *" : "")}");
            if (_assetsHidden)
            {
                _assetWindow = new Rect(_assetWindow.x, _assetWindow.y, 120f, 30f);
                if (GUI.Button(_assetWindow, "Show assets"))
                    _assetsHidden = false;
            }
            else
            {
                _assetWindow.width = 420f;
                _assetWindow.height = Mathf.Min(_assetWindow.height, Screen.height - 80f);
                _assetWindow = GUILayout.Window(AssetWindowId, _assetWindow, DrawAssets, "Assets");
            }
            if (_zonesShown)
                _zoneWindow = GUILayout.Window(ZoneWindowId, _zoneWindow, DrawZones, "Zones");
            DrawZoneLabels();
            // Esc or a click outside a text box: the keys go back to the camera
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                GUIUtility.keyboardControl = 0;
        }

        private void DrawWindow(int id)
        {
            // ---- Place ----
            GUILayout.Label("Place", GUI.skin.box, GUILayout.ExpandWidth(true));
            string[] kinds = KindNames;
            _kind = (Kind)GUILayout.SelectionGrid((int)_kind, kinds, 3);
            string[] variants = Variants(_kind);
            if (variants != null)
            {
                _variant.TryGetValue(_kind, out int v);
                _variant[_kind] = Cycle(v, variants, "Look");
            }
            if (HasPrice(_kind))
                _price = PriceSlider(_price);
            if (HasZone(_kind))
                _zone = Mathf.RoundToInt(Slider("Zone", _zone, 1f, 20f, "0"));
            if (GUILayout.Button(Valid(_carrying) ? "Click in the world to put it down (Esc: cancel)" : "Place (follows the mouse, click to put down)"))
                PlaceNew();
            if (GUILayout.Button(_zonesShown ? "Hide the zones window" : "Zones (outline each zone)"))
                _zonesShown = !_zonesShown;

            // ---- Selected ----
            GUILayout.Space(6f);
            GUILayout.Label("Selected", GUI.skin.box, GUILayout.ExpandWidth(true));
            if (Valid(_selected))
                DrawSelected(_map.Entries[_selected]);
            else
                GUILayout.Label("Nothing: click a placed thing in the world or in the list.");

            // ---- List ----
            GUILayout.Space(6f);
            GUILayout.Label($"Placed ({_map.Entries.Count})", GUI.skin.box, GUILayout.ExpandWidth(true));
            _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Height(130f));
            for (int i = 0; i < _map.Entries.Count; i++)
            {
                GUI.color = i == _selected ? Color.yellow : Color.white;
                if (GUILayout.Button($"{i + 1}. {Describe(_map.Entries[i])}{(_map.Entries[i].Locked ? "  [locked]" : "")}", GUI.skin.label))
                {
                    _selected = i;
                    Highlight();
                }
            }
            GUI.color = Color.white;
            GUILayout.EndScrollView();

            // ---- Time ----
            GUILayout.Space(6f);
            GUILayout.Label("Time of day", GUI.skin.box, GUILayout.ExpandWidth(true));
            DrawTime();

            // ---- Weather ----
            GUILayout.Space(6f);
            GUILayout.Label("Weather", GUI.skin.box, GUILayout.ExpandWidth(true));
            DrawWeather();

            // ---- Power ----
            GUILayout.Space(6f);
            GUILayout.Label("Power", GUI.skin.box, GUILayout.ExpandWidth(true));
            bool always = GUILayout.Toggle(_map.AlwaysPowered, " Power always on (no power switch: perks, Pack-a-Punch and lights work from the start)");
            if (always != _map.AlwaysPowered)
            {
                _map.AlwaysPowered = always;
                Touch();
            }

            // ---- Steps ----
            GUILayout.Space(6f);
            GUILayout.Label("Steps (arrow keys and wheel)", GUI.skin.box, GUILayout.ExpandWidth(true));
            _moveStep.Value = Slider("Move", _moveStep.Value, 0.5f, 100f, "0.0 cm");
            _turnStep.Value = Slider("Turn", _turnStep.Value, 0.5f, 90f, "0.0 deg");
            _sizeStep.Value = Slider("Size", _sizeStep.Value, 1f, 100f, "0 cm");
            _scaleStep.Value = Slider("Scale", _scaleStep.Value, 1f, 50f, "0 \\%");

            // ---- File ----
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUI.enabled = _undo.Count > 0 || _pending;
            if (GUILayout.Button("Undo")) Undo();
            GUI.enabled = _redo.Count > 0;
            if (GUILayout.Button("Redo")) Redo();
            GUI.enabled = true;
            if (GUILayout.Button("Lock all")) SetAllLocked(true);
            if (GUILayout.Button("Unlock all")) SetAllLocked(false);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save")) Save();
            if (GUILayout.Button("Reload")) Load();
            if (GUILayout.Button("Test layout")) Test();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Put me here")) DropPlayerHere();
            if (GUILayout.Button($"Close ({_key.Value})")) SetOpen(false);
            GUILayout.EndHorizontal();
            GUILayout.Label("WASD fly, Q / E down / up, Shift fast, hold right mouse to look. Left click selects, left drag moves " +
                            "(Shift: keep its height). Arrows nudge, Alt + Left / Right turn, Alt + Up / Down tilt, Alt + Shift + Left / Right roll, Ctrl + arrows size, Alt + wheel scales, Ctrl + wheel width, Shift + wheel height, " +
                            "the wheel turns a carried thing. Ctrl + Z undoes, Ctrl + Y redoes, Ctrl + C / Ctrl + V copy and paste. Locked things can be selected but not changed.", _wrap);
            GUI.DragWindow();
        }

        private void DrawSelected(Entry e)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{_selected + 1}. {Describe(e)}");
            GUI.color = e.Locked ? new Color(1f, 0.6f, 0.3f) : Color.white;
            if (GUILayout.Button(e.Locked ? "Locked (unlock)" : "Lock", GUILayout.Width(110f)))
            {
                e.Locked = !e.Locked;
                Touch();
            }
            GUI.color = Color.white;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUI.enabled = !e.Locked;
            if (GUILayout.Button("Move with mouse")) StartCarry();
            GUI.enabled = true;
            if (GUILayout.Button("Duplicate")) Duplicate();
            if (GUILayout.Button("Go to")) _cam.MoveTo(Pos(e) - _cam.FlatForward * 4f + Vector3.up * 2f);
            GUI.enabled = !e.Locked;
            if (GUILayout.Button("Delete"))
            {
                GUI.enabled = true;
                Delete(_selected);
                return;
            }
            GUILayout.EndHorizontal();
            // Locked: nothing below can change it
            DrawSelectedSettings(e);
            GUI.enabled = true;
        }

        private void DrawSelectedSettings(Entry e)
        {

            string[] variants = Variants(e.Type);
            if (variants != null)
            {
                int at = Mathf.Max(0, Array.IndexOf(variants, e.What));
                int now = Cycle(at, variants, "Look");
                if (now != at)
                {
                    e.What = variants[now];
                    Remake(_selected);
                }
            }
            // A zombie spawn's or barricade's zone: it spawns / feeds once that zone is open (0: from where it stands)
            if (e.Type == Kind.ZombieSpawn || e.Type == Kind.Window)
            {
                int now = Mathf.RoundToInt(Slider("Zone (0: auto)", e.Zone, 0f, 20f, "0"));
                if (now != e.Zone)
                {
                    e.Zone = now;
                    Touch();
                }
            }
            // A zone's number (1 and 2: where players start, open from the start)
            if (e.Type == Kind.Zone)
            {
                int.TryParse(e.What, out int zone);
                int now = Mathf.RoundToInt(Slider("Zone number", Mathf.Max(1, zone), 1f, 20f, "0"));
                if (now != zone)
                {
                    e.What = now.ToString();
                    Touch();
                }
            }
            if (HasPrice(e.Type))
            {
                if (e.Type == Kind.WallBuy)
                {
                    bool board = GUILayout.Toggle(!e.NoBoard, " Chalk on a wooden board");
                    if (board == e.NoBoard)
                    {
                        e.NoBoard = !board;
                        Touch();
                    }
                }
                bool power = e.Type != Kind.WallBuy && GUILayout.Toggle(e.OpensWithPower, " Cost: Power (opens when the power comes on)");
                if (e.Type != Kind.WallBuy && power != e.OpensWithPower)
                {
                    e.OpensWithPower = power;
                    Touch();
                }
                if (!e.OpensWithPower)
                {
                    int price = PriceSlider(e.Price);
                    if (price != e.Price)
                    {
                        e.Price = price;
                        Touch();
                    }
                }
            }

            // Move sliders: push and hold, they spring back
            _moveRight = Spring("Left / right", _moveRight);
            _moveForward = Spring("Back / forward", _moveForward);
            _moveUp = Spring("Down / up", _moveUp);

            float turn = Slider("Turn", Mathf.Repeat(e.Turn, 360f), 0f, 360f, "0");
            if (!Mathf.Approximately(turn, Mathf.Repeat(e.Turn, 360f)))
            {
                e.Turn = Mathf.Round(turn);
                Changed(_selected);
            }
            float tilt = Slider("Tilt", Signed(e.Tilt), -180f, 180f, "0");
            if (!Mathf.Approximately(tilt, Signed(e.Tilt)))
            {
                e.Tilt = Mathf.Round(tilt);
                Changed(_selected);
            }
            float roll = Slider("Roll", Signed(e.Roll), -180f, 180f, "0");
            if (!Mathf.Approximately(roll, Signed(e.Roll)))
            {
                e.Roll = Mathf.Round(roll);
                Changed(_selected);
            }
            // Quick quarter turns: a thing that came in lying flat stands up with one press
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Tilt 90")) { e.Tilt = Signed(e.Tilt + 90f); Changed(_selected); }
            if (GUILayout.Button("Roll 90")) { e.Roll = Signed(e.Roll + 90f); Changed(_selected); }
            if (GUILayout.Button("Turn 90")) { e.Turn = Mathf.Repeat(e.Turn + 90f, 360f); Changed(_selected); }
            if (GUILayout.Button("Upright")) { e.Tilt = 0f; e.Roll = 0f; Changed(_selected); }
            GUILayout.EndHorizontal();
            if (e.Size != null)
            {
                string[] axes = { "Width", "Height", "Depth" };
                for (int i = 0; i < 3; i++)
                {
                    float size = Slider(axes[i], e.Size[i], 0.05f, e.Type == Kind.Zone ? 80f : 12f, "0.00 m");
                    if (!Mathf.Approximately(size, e.Size[i]))
                    {
                        e.Size[i] = size;
                        Changed(_selected);
                    }
                }
            }
            else
            {
                float scale = Slider("Scale", e.Scale > 0f ? e.Scale : 1f, 0.1f, 5f, "0.00");
                if (!Mathf.Approximately(scale, e.Scale))
                {
                    e.Scale = scale;
                    Changed(_selected);
                }
            }
        }

        /// <summary>Clouds, rain, fog and wind, and whether the map holds them; saved with the layout.</summary>
        private void DrawWeather()
        {
            bool hold = GUILayout.Toggle(_map.Weather != null, " Keep this weather on the map (saved with the layout)");
            if (hold && _map.Weather == null)
            {
                _map.Weather = new MapWeather.Settings();
                if (!MapWeather.Hold(_map.Weather))
                    ZombiesPlugin.Notify("This map has no weather system (its sky doesn't change).", true);
                Touch();
            }
            else if (!hold && _map.Weather != null)
            {
                _map.Weather = null;
                MapWeather.Release();
                Touch();
            }
            if (_map.Weather == null)
                return;
            MapWeather.Settings w = _map.Weather;
            float clouds = Slider("Clouds", w.Clouds, -1f, 1f, "0.00");
            float rain = Slider("Rain", w.Rain, 0f, 1f, "0.00");
            float fog = Slider("Fog", w.Fog, 0.001f, 0.255f, "0.000");
            float wind = Slider("Wind", w.Wind, 0f, 1f, "0.00");
            if (!Mathf.Approximately(clouds, w.Clouds) || !Mathf.Approximately(rain, w.Rain) || !Mathf.Approximately(fog, w.Fog)
                || !Mathf.Approximately(wind, w.Wind))
            {
                w.Clouds = clouds;
                w.Rain = rain;
                w.Fog = fog;
                w.Wind = wind;
                MapWeather.Hold(w);
                Touch();
            }
        }

        /// <summary>An hour slider (quarter hours) and whether the map holds it; both saved with the layout.</summary>
        private void DrawTime()
        {
            float now = _map.Hour ?? MapTime.Hour ?? 12f;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Hour", GUILayout.Width(60f));
            float hour = Mathf.Round(GUILayout.HorizontalSlider(now, 0f, 23.75f, GUILayout.ExpandWidth(true)) * 4f) / 4f;
            GUILayout.Label($"{(int)hour:00}:{(int)(hour % 1f * 60f):00}", GUILayout.Width(60f));
            GUILayout.EndHorizontal();
            bool hold = GUILayout.Toggle(_map.Hour.HasValue, " Keep this time on the map (saved with the layout)");
            if (hold && (!_map.Hour.HasValue || !Mathf.Approximately(hour, _map.Hour.Value)))
            {
                _map.Hour = hour;
                MapTime.Hold(hour);
                Touch();
            }
            else if (!hold && _map.Hour.HasValue)
            {
                _map.Hour = null;
                MapTime.Release();
                Touch();
            }
            else if (!hold && !Mathf.Approximately(hour, Mathf.Round(now * 4f) / 4f))
            {
                // Moved without holding: jump there and let the clock run on
                MapTime.Hold(hour);
                MapTime.Release();
            }
        }

        // ---------- Asset list ----------

        private int _assetTab; // 0 map props, 1 items, 2 our models
        private string _pendingWhat; // the look a picked "our model" gives the next new thing
        private string _search = "";
        private string _category;
        private string _asset;
        private Vector2 _categoryScroll, _assetScroll;
        private List<AssetCatalog.Asset> _shown = new List<AssetCatalog.Asset>();
        private string _shownFor, _categoriesFor;
        private List<(string, string)> _categories = new List<(string, string)>();
        private string _allLabel;
        // The buttons' names: a Window is the original's boarded-up barricade (saved as "Window" so older layouts still load)
        private static readonly string[] KindNames = Enum.GetNames(typeof(Kind)).Select(n => n == "Window" ? "Barricade" : n == "Rock" ? "115 Rock" : n == "ExfilActivator" ? "Exfil lever" : n == "ExfilHold" ? "Exfil hold area" : n == "ExfilZone" ? "Exfil zone" : n).ToArray();
        private static readonly string[] AssetTabs = { "Map props", "Items", "Our models" };
        private bool _assetsHidden;
        private const float RowHeight = 20f, ListHeight = 440f;

        private void DrawAssets(int id)
        {
            GUILayout.BeginHorizontal();
            int tab = GUILayout.Toolbar(_assetTab, AssetTabs);
            if (GUILayout.Button("Hide", GUILayout.Width(50f)))
                _assetsHidden = true;
            GUILayout.EndHorizontal();
            if (tab != _assetTab)
            {
                _assetTab = tab;
                _category = null;
            }
            List<AssetCatalog.Asset> all = _assetTab == 0 ? AssetCatalog.SceneAssets() : _assetTab == 1 ? AssetCatalog.ItemAssets() : AssetCatalog.OurAssets();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(50f));
            _search = GUILayout.TextField(_search ?? "");
            if (GUILayout.Button("x", GUILayout.Width(24f)))
            {
                _search = "";
                GUIUtility.keyboardControl = 0;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            // Categories
            _categoryScroll = GUILayout.BeginScrollView(_categoryScroll, GUILayout.Width(150f), GUILayout.Height(ListHeight));
            if (CategoryButton(_allLabel ??= $"All ({all.Count})", _category == null))
                _category = null;
            string categoriesKey = $"{_assetTab}|{all.Count}";
            if (categoriesKey != _categoriesFor)
            {
                _categoriesFor = categoriesKey;
                _categories = all.GroupBy(a => a.Category).Select(g => (g.Key, $"{g.Key} ({g.Count()})")).ToList();
            }
            foreach ((string name, string label) in _categories)
                if (CategoryButton(label, _category == name))
                    _category = name;
            GUILayout.EndScrollView();

            // The assets in it
            string filter = $"{_assetTab}|{_category}|{_search}|{all.Count}";
            if (filter != _shownFor)
            {
                _shownFor = filter;
                string search = (_search ?? "").Trim().ToLowerInvariant();
                _allLabel = null;
                _shown = all.Where(a => (_category == null || a.Category == _category)
                                        && (search.Length == 0 || a.Name.ToLowerInvariant().Contains(search))).ToList();
                _assetScroll = Vector2.zero;
            }
            // Only the rows in view are drawn (thousands of buttons a frame was slow); the rest is empty space
            _assetScroll = GUILayout.BeginScrollView(_assetScroll, GUILayout.Height(ListHeight));
            int first = Mathf.Clamp(Mathf.FloorToInt(_assetScroll.y / RowHeight), 0, Mathf.Max(0, _shown.Count - 1));
            int last = Mathf.Min(_shown.Count, first + Mathf.CeilToInt(ListHeight / RowHeight) + 2);
            GUILayout.Space(first * RowHeight);
            for (int i = first; i < last; i++)
            {
                AssetCatalog.Asset a = _shown[i];
                GUI.color = a.Key == _asset ? Color.yellow : Color.white;
                if (GUILayout.Button(a.Name, GUI.skin.label, GUILayout.Height(RowHeight)))
                {
                    // Picked: it follows the mouse to be put down
                    _asset = a.Key;
                    if (a.Key.StartsWith(AssetCatalog.DecalPrefix))
                    {
                        _kind = Kind.Decal;
                        _pendingWhat = a.Key.Substring(AssetCatalog.DecalPrefix.Length);
                    }
                    else
                        _kind = Kind.Prop;
                    GUIUtility.keyboardControl = 0;
                    PlaceNew();
                }
            }
            GUI.color = Color.white;
            GUILayout.Space(Mathf.Max(0, _shown.Count - last) * RowHeight);
            GUILayout.EndScrollView();
            GUILayout.EndHorizontal();
            GUILayout.Label(_assetTab == 0
                ? $"{all.Count} props from this map (copies of the map's own; only on this map). Click one to place it."
                : _assetTab == 2
                ? $"{all.Count} of our own models (wall decals snap flat onto walls). Click one to place it."
                : $"{all.Count} EFT items (any map; they take a moment to load). Click one to place it.", _wrap);
            if (_assetTab == 0 && GUILayout.Button("Scan the map again"))
                AssetCatalog.Rescan();
            GUI.DragWindow();
        }

        private static bool CategoryButton(string label, bool on)
        {
            GUI.color = on ? Color.yellow : Color.white;
            bool pressed = GUILayout.Button(label, GUI.skin.label);
            GUI.color = Color.white;
            return pressed;
        }

        /// <summary>An angle as -180..180.</summary>
        private static float Signed(float degrees) => Mathf.Repeat(degrees + 180f, 360f) - 180f;

        private int Cycle(int at, string[] options, string label)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(60f));
            if (GUILayout.Button("<", GUILayout.Width(28f))) at = (at - 1 + options.Length) % options.Length;
            GUILayout.Label(options[Mathf.Clamp(at, 0, options.Length - 1)], GUI.skin.box, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(">", GUILayout.Width(28f))) at = (at + 1) % options.Length;
            GUILayout.EndHorizontal();
            return Mathf.Clamp(at, 0, options.Length - 1);
        }

        private int PriceSlider(int price) => Mathf.RoundToInt(Slider("Price", price, 0f, 5000f, "0") / 250f) * 250;

        private float Slider(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(60f));
            value = GUILayout.HorizontalSlider(value, min, max, GUILayout.ExpandWidth(true));
            GUILayout.Label(value.ToString(format), GUILayout.Width(60f));
            GUILayout.EndHorizontal();
            return value;
        }

        private float Spring(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(100f));
            value = GUILayout.HorizontalSlider(value, -1f, 1f, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            // Let go: back to the middle
            return Input.GetMouseButton(0) ? value : 0f;
        }

        /// <summary>The move sliders, while held: up to 1.5 m a second, faster the further they're pushed.</summary>
        private void DriveSliders()
        {
            if (!_open || !Valid(_selected) || IsLocked(_selected) || (_moveRight == 0f && _moveUp == 0f && _moveForward == 0f))
                return;
            Vector3 forward = ViewForward();
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 push = right * _moveRight + Vector3.up * _moveUp + forward * _moveForward;
            Entry e = _map.Entries[_selected];
            e.Position = Vec(Pos(e) + push * (1.5f * Time.unscaledDeltaTime));
            Changed(_selected);
        }

        // ---------- Markers ----------

        private static float[] DefaultSize(Kind kind) => kind switch
        {
            // A window opening (its front, facing the player when placed, is the outside the zombies come from)
            Kind.Window => new[] { 1.2f, 1.4f, 0.3f },
            Kind.Door => new[] { 1.2f, 2.2f, 0.15f },
            Kind.Debris => new[] { 2.5f, 1.6f, 1.5f },
            // Solid, invisible in a match: seals gaps and shapes the map
            Kind.Blocker => new[] { 2f, 2.5f, 0.3f },
            Kind.PlayAreaWall => new[] { 4f, 3f, 0.1f },
            // An area of the map (the original's zones): what's inside it is its; opened by the doors and debris that touch it
            Kind.Zone => new[] { 12f, 5f, 12f },
            // The exfil (ZombiesExfil): the area someone has to hold (the basement) and where you leave from
            Kind.ExfilHold => new[] { 12f, 4f, 12f },
            Kind.ExfilZone => new[] { 4f, 3f, 4f },
            _ => null,
        };

        /// <summary>A box the entry's size standing on its spot (blockers and walls in a match).</summary>
        internal static void Place(GameObject go, Entry e)
        {
            Vector3 size = e.Size != null ? new Vector3(e.Size[0], e.Size[1], e.Size[2]) : MarkerSize(e.Type) * (e.Scale > 0f ? e.Scale : 1f);
            go.transform.localScale = size;
            go.transform.rotation = Rotation(e);
            // Standing on its spot (the cube's middle is half its height up, along its own up when tilted)
            go.transform.position = Pos(e) + go.transform.rotation * Vector3.up * (size.y * 0.5f);
        }

        internal static Vector3 MarkerSize(Kind kind) => kind switch
        {
            Kind.PlayerStart => new Vector3(0.5f, 0.9f, 0.5f),
            Kind.ZombieSpawn => new Vector3(0.6f, 1.8f, 0.6f),
            Kind.BoxSpot => new Vector3(2.1f, 0.7f, 0.6f),
            Kind.PerkMachine => new Vector3(0.9f, 2f, 0.7f),
            Kind.PackAPunch => new Vector3(1.2f, 1.8f, 1f),
            Kind.WallBuy => new Vector3(0.8f, 0.4f, 0.05f),
            Kind.PowerLight => new Vector3(1.2f, 0.15f, 0.2f),
            Kind.PowerSwitch => new Vector3(0.6f, 1.1f, 0.3f),
            Kind.ExfilActivator => new Vector3(0.6f, 1.1f, 0.3f),
            Kind.Rock => new Vector3(0.3f, 0.2f, 0.2f),
            Kind.Lightning => new Vector3(0.3f, 0.3f, 0.3f),
            Kind.Lamp => new Vector3(1.2f, 0.15f, 0.2f),
            Kind.Decal => new Vector3(1f, 1f, 0.02f),
            _ => Vector3.one,
        };

        internal static Color Colour(Kind kind) => kind switch
        {
            Kind.PlayerStart => new Color(0.2f, 1f, 0.3f, 0.7f),
            Kind.ZombieSpawn => new Color(1f, 0.15f, 0.1f, 0.6f),
            Kind.BoxSpot => new Color(1f, 0.85f, 0.2f, 0.6f),
            Kind.PerkMachine => new Color(0.2f, 0.5f, 1f, 0.6f),
            Kind.PackAPunch => new Color(0.7f, 0.2f, 1f, 0.6f),
            Kind.WallBuy => new Color(1f, 0.55f, 0.1f, 0.8f),
            // Faint: unlit colours glow in dark corners
            Kind.Window => new Color(0.55f, 0.35f, 0.18f, 0.25f),
            Kind.Door => new Color(0.55f, 0.35f, 0.15f, 0.7f),
            Kind.Debris => new Color(0.6f, 0.6f, 0.55f, 0.7f),
            Kind.Blocker => new Color(0.3f, 0.3f, 0.35f, 0.5f),
            Kind.PowerLight => new Color(1f, 0.95f, 0.7f, 0.7f),
            Kind.PowerSwitch => new Color(1f, 0.8f, 0.1f, 0.8f),
            Kind.ExfilActivator => new Color(0.2f, 1f, 0.6f, 0.8f),
            Kind.ExfilHold => new Color(0.2f, 1f, 0.4f, 0.12f),
            Kind.ExfilZone => new Color(0.1f, 1f, 0.9f, 0.25f),
            Kind.Zone => new Color(0.3f, 0.6f, 1f, 0.12f),
            Kind.Rock => new Color(0.3f, 0.7f, 1f, 0.7f),
            Kind.Lightning => new Color(0.6f, 0.8f, 1f, 0.5f),
            Kind.Lamp => new Color(1f, 0.85f, 0.5f, 0.7f),
            Kind.Decal => new Color(0.8f, 0.1f, 0.1f, 0.5f),
            _ => new Color(0.2f, 0.9f, 1f, 0.25f),
        };

        /// <summary>The selected coloured box shows lighter, the rest in their own colours.</summary>
        private void Highlight()
        {
            if (_map == null)
                return;
            for (int i = 0; i < _visuals.Count; i++)
            {
                if (_visuals[i] == null)
                    continue;
                Color c = Colour(_map.Entries[i].Type);
                _visuals[i].Tint(i == _selected ? Color.Lerp(c, Color.white, 0.6f) : c);
            }
        }

        // The outline's path: round the bottom, then up and along each top edge (corners 0-3 bottom, 4-7 top)
        private static readonly int[] OutlinePath = { 0, 1, 2, 3, 0, 4, 5, 1, 5, 6, 2, 6, 7, 3, 7, 4 };

        /// <summary>A thin yellow outline round the selected thing (a filled box hid the model behind it).</summary>
        private void UpdateSelectionBox()
        {
            bool show = _visuals.Count > 0 && Valid(_selected) && _visuals[_selected] != null && _visuals[_selected].gameObject.activeSelf;
            if (!show)
            {
                if (_selectionBox != null)
                    _selectionBox.SetActive(false);
                return;
            }
            if (_selectionBox == null)
            {
                _selectionBox = new GameObject("Zombies_MapSelection (KennerZombies)");
                LineRenderer line = _selectionBox.AddComponent<LineRenderer>();
                line.useWorldSpace = false;
                line.positionCount = OutlinePath.Length;
                line.widthMultiplier = 0.02f;
                line.material = new Material(Shader.Find("Sprites/Default"));
                line.startColor = line.endColor = new Color(1f, 0.85f, 0.2f, 0.9f);
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.receiveShadows = false;
            }
            MapVisual v = _visuals[_selected];
            Vector3 half = (v.Size + Vector3.one * 0.04f) * 0.5f;
            var lr = _selectionBox.GetComponent<LineRenderer>();
            for (int i = 0; i < OutlinePath.Length; i++)
            {
                int c = OutlinePath[i];
                float x = c == 0 || c == 3 || c == 4 || c == 7 ? -half.x : half.x;
                float z = c == 0 || c == 1 || c == 4 || c == 5 ? -half.z : half.z;
                float y = c < 4 ? -0.02f : half.y * 2f;
                lr.SetPosition(i, new Vector3(x, y, z));
            }
            _selectionBox.SetActive(true);
            _selectionBox.transform.SetPositionAndRotation(v.transform.position, v.transform.rotation);
        }

        private void OnDestroy() => _cam.Stop();
    }
}
