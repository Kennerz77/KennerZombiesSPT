using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies mode's logging, to tell afterwards what went on in a match (F12 "19. Debug: logging"):
    /// - Every match gets its own file, plugins\Kennerz_ZM_Core\logs\zm_&lt;map&gt;_&lt;date&gt;.log: every line our mods
    ///   write (this one, Perk Beverages, PaP, the wonder weapons), timed from the match start, and every game error
    ///   while the match runs, with its stack (each kind in full once, then counted every 10 s). The last 300 lines
    ///   before the match (the loading) go in first.
    /// - Watchers: what's in your hands (a drink, a gun, nothing), your gun's mag, chamber and reloads, and a line on
    ///   the zombies every few seconds (alive, rising, busy, left to spawn).
    /// - "Dump everything" (button, or Ctrl+F10): the whole match state at once (you, your guns and mags, perks,
    ///   points, round, power, zones, every zombie).
    /// </summary>
    internal class ZombiesTrace : MonoBehaviour, ILogListener
    {
        public static ConfigEntry<bool> MatchFile, WatchHands, WatchZombies, AllErrors;
        public static ConfigEntry<KeyboardShortcut> DumpKey;
        public static ConfigEntry<float> ZombiesEvery;

        private static ZombiesTrace _instance;
        private static StreamWriter _file;
        private static string _filePath;
        private static DateTime _matchStartTime;
        private static readonly Queue<string> Before = new Queue<string>();
        private const int BeforeLines = 300;
        private static readonly object Lock = new object();

        public static void Bind(ConfigFile config, Func<string, Action, ZombiesPlugin.ConfigurationManagerAttributes> button)
        {
            const string section = "19. Debug: logging";
            MatchFile = config.Bind(section, "Match log file", true, new ConfigDescription(
                "Each zombies match writes its own log to plugins\\Kennerz_ZM_Core\\logs (our mods' lines and every game error, timed).",
                null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 99 }));
            WatchHands = config.Bind(section, "Log hands, gun and reloads", true, new ConfigDescription(
                "Writes what's in your hands as it changes, your gun's mag and chamber, and every reload (and why one was refused).",
                null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 98 }));
            WatchZombies = config.Bind(section, "Log the zombies", true, new ConfigDescription(
                "Writes a line on the zombies every few seconds: alive, rising, at barricades, slapping, left to spawn.",
                null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 97 }));
            ZombiesEvery = config.Bind(section, "Zombies line every (s)", 10f, new ConfigDescription(
                "How often the zombies line is written.", new AcceptableValueRange<float>(2f, 60f),
                new ZombiesPlugin.ConfigurationManagerAttributes { Order = 96 }));
            AllErrors = config.Bind(section, "Game errors in the match file", true, new ConfigDescription(
                "Every game error during a match goes in the match file with its stack, not only ours (a broken drink showed " +
                "up only as a game error).", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 95 }));
            DumpKey = config.Bind(section, "Dump everything key", new KeyboardShortcut(KeyCode.F10, KeyCode.LeftControl), new ConfigDescription(
                "Writes the whole match state to the log at once.", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 94 }));
            ZombiesPlugin.ConfigurationManagerAttributes look = button("Dump everything", Dump);
            look.Order = 93;
            config.Bind(section, "Dump everything", false, new ConfigDescription("Writes the whole match state to the log now.", null, look));
            ZombiesPlugin.ConfigurationManagerAttributes open = button("Open the logs folder", () =>
            {
                Directory.CreateDirectory(LogsDir);
                Application.OpenURL("file:///" + LogsDir.Replace('\\', '/'));
            });
            open.Order = 92;
            config.Bind(section, "Open the logs folder", false, new ConfigDescription("Opens plugins\\Kennerz_ZM_Core\\logs.", null, open));
            ZombiesPlugin.ConfigurationManagerAttributes weather = button("Weather report now", MapWeather.ReportNow);
            weather.Order = 91;
            config.Bind(section, "Weather report now", false, new ConfigDescription(
                "Writes the rain, light, sky and world fog numbers to BepInEx\\LogOutput.log now, in any raid (a normal raid gives numbers to compare with).",
                null, weather));
        }

        private static string LogsDir => Path.Combine(ZombiesPlugin.Dir, "logs");

        private void Awake()
        {
            _instance = this;
            BepInEx.Logging.Logger.Listeners.Add(this);
            Application.logMessageReceived += UnityLog;
        }

        private void OnDestroy()
        {
            BepInEx.Logging.Logger.Listeners.Remove(this);
            Application.logMessageReceived -= UnityLog;
            Close();
        }

        // ---------- The match file ----------

        /// <summary>A match has started: its own file, with the loading's lines first.</summary>
        public static void MatchStarted(string map)
        {
            Close();
            _matchStartTime = DateTime.Now;
            Errors.Clear();
            if (MatchFile?.Value != true)
                return;
            try
            {
                Directory.CreateDirectory(LogsDir);
                // The 20 newest kept
                foreach (FileInfo old in new DirectoryInfo(LogsDir).GetFiles("zm_*.log").OrderByDescending(f => f.LastWriteTime).Skip(19))
                    old.Delete();
                // The player's name in it: two test games in one install started in the same second wrote one file
                // and a player's log was lost (user, 2026-11-10)
                string who = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer?.Profile?.Nickname ?? (ZombiesNet.Headless ? "headless" : "player");
                who = new string(who.Where(char.IsLetterOrDigit).ToArray());
                _filePath = Path.Combine(LogsDir, $"zm_{map}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{who}_{System.Diagnostics.Process.GetCurrentProcess().Id}.log");
                _file = new StreamWriter(_filePath, false, Encoding.UTF8) { AutoFlush = true };
                _file.WriteLine($"Zombies match log: {map}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {ZombiesPlugin.PluginName} {ZombiesPlugin.PluginVersion}");
                _file.WriteLine("---------- before the match (loading) ----------");
                lock (Lock)
                    foreach (string line in Before)
                        _file.WriteLine(line);
                _file.WriteLine("---------- the match (times from its start) ----------");
                ZombiesPlugin.Log.LogInfo($"Zombies log: this match is written to {_filePath}");
            }
            catch (Exception e)
            {
                _file = null;
                ZombiesPlugin.Log.LogWarning($"Zombies log: couldn't open the match file: {e.Message}");
            }
        }

        /// <summary>The match is over: the errors' repeat counts written and the file closed.</summary>
        public static void MatchEnded()
        {
            if (_file == null)
                return;
            FlushRepeats(true);
            Write("---------- match over ----------");
            Close();
        }

        private static void Close()
        {
            try
            {
                _file?.Dispose();
            }
            catch
            {
            }
            _file = null;
        }

        private static void Write(string line)
        {
            if (_file == null)
            {
                lock (Lock)
                {
                    Before.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {line}");
                    while (Before.Count > BeforeLines)
                        Before.Dequeue();
                }
                return;
            }
            try
            {
                // Seconds since the match started (DateTime: this can be called off the main thread)
                double t = (DateTime.Now - _matchStartTime).TotalSeconds;
                lock (Lock)
                    _file?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{(int)(t / 60):00}:{t % 60:00.0}] {line}");
            }
            catch
            {
            }
        }

        // Our mods' lines (BepInEx sources named Kennerz-...)
        public void LogEvent(object sender, LogEventArgs e)
        {
            string source = e.Source?.SourceName;
            if (source == null || !source.StartsWith("Kenner", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("DrK", StringComparison.OrdinalIgnoreCase))
                return;
            Write($"{e.Level,-7} {source}: {e.Data}");
        }

        public void Dispose()
        {
        }

        // ---------- Game errors ----------

        private class Seen
        {
            public int Repeats;
            public float Since;
        }

        private static readonly Dictionary<string, Seen> Errors = new Dictionary<string, Seen>();
        private static float _nextRepeats;

        private static void UnityLog(string message, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert)
                return;
            // Our own BepInEx lines echoed into Unity's log are in the file already
            if (message != null && message.StartsWith("[") && (message.Contains(":Kenner") || message.Contains(":DrK")))
                return;
            bool ours = (stack != null && (stack.Contains("KennerZombies") || stack.Contains("PerkBeverages") || stack.Contains("PaPHideoutModule")))
                        || (message != null && (message.Contains("PerkBeverages") || message.Contains("Kenner")));
            if (_file == null || (!ours && AllErrors?.Value != true))
                return;
            string first = stack?.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
            string key = (message ?? "") + "|" + first;
            if (Errors.TryGetValue(key, out Seen seen))
            {
                seen.Repeats++;
                return;
            }
            Errors[key] = new Seen { Since = Time.realtimeSinceStartup };
            string trimmed = string.Join("\n      ", (stack ?? "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(14).Select(l => l.Trim()));
            Write($"{(ours ? "OURS " : "")}{type}: {message}\n      {trimmed}");
            // Ours go to the BepInEx log too (the game's own are only in the match file: there can be thousands)
            if (ours)
                ZombiesPlugin.Log.LogWarning($"Zombies log: game error in our code: {message} (at {first})");
        }

        private static void FlushRepeats(bool all)
        {
            foreach (var kv in Errors)
                if (kv.Value.Repeats > 0)
                {
                    Write($"  ... repeated {kv.Value.Repeats} more times: {kv.Key.Split('|')[0]}");
                    kv.Value.Repeats = 0;
                }
        }

        // ---------- Watchers ----------

        private string _hands;
        private string _gun;
        private bool _wasReloading;
        private float _nextZombies;
        private bool _wasActive;

        private void Update()
        {
            bool active = ZombiesMatch.Active && ZombiesPlugin.RaidStarted;
            if (active != _wasActive)
            {
                _wasActive = active;
                _hands = _gun = null;
                if (!active)
                    MatchEnded();
            }
            if (DumpKey != null && DumpKey.Value.IsDown())
                Dump();
            if (_file != null && Time.realtimeSinceStartup >= _nextRepeats)
            {
                _nextRepeats = Time.realtimeSinceStartup + 10f;
                FlushRepeats(false);
            }
            if (!active)
                return;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null)
                return;
            if (WatchHands?.Value == true)
                WatchMyHands(me);
            if (WatchZombies?.Value == true && Time.time >= _nextZombies)
            {
                _nextZombies = Time.time + ZombiesEvery.Value;
                ZombiesPlugin.Log.LogInfo($"[zombies] {ZombiesRounds.Describe(false)}");
            }
        }

        private void WatchMyHands(Player me)
        {
            string hands;
            try
            {
                hands = HandsText(me);
            }
            catch (Exception e)
            {
                hands = $"unreadable ({e.GetType().Name})";
            }
            if (hands != _hands)
            {
                _hands = hands;
                ZombiesPlugin.Log.LogInfo($"[hands] {hands}");
            }
            if (!(me.HandsController is Player.FirearmController fc) || !(fc.Item is Weapon w))
            {
                _gun = null;
                return;
            }
            bool reloading = false;
            try
            {
                reloading = fc.IsInReloadOperation();
            }
            catch
            {
            }
            if (reloading != _wasReloading)
            {
                _wasReloading = reloading;
                ZombiesPlugin.Log.LogInfo($"[hands] reload {(reloading ? "started" : "done")}: {GunText(w)}");
            }
            // The mag and chamber, not every shot: which mag is in, and the chamber going empty or full
            Magazine mag = w.GetCurrentMagazine();
            string gun = $"{w.Id}|{mag?.Id}|{Chambered(w)}|{(mag != null && mag.Count == 0)}";
            if (gun != _gun)
            {
                _gun = gun;
                ZombiesPlugin.Log.LogInfo($"[hands] gun: {GunText(w)}");
            }
        }

        public static string HandsText(Player me)
        {
            var hc = me.HandsController;
            if (hc == null)
                return "nothing (no hands controller)";
            string name = hc.GetType().Name;
            Item item = hc.Item;
            return item == null ? $"{name}, no item" : $"{name}: {item.LocalizedName()} ({item.TemplateId})";
        }

        public static bool Chambered(Weapon w) => w.Chambers.Length > 0 && w.Chambers[0].ContainedItem != null;

        public static string GunText(Weapon w)
        {
            if (w == null)
                return "no gun";
            Magazine mag = w.GetCurrentMagazine();
            string where = w.Parent?.Container?.ID ?? "nowhere";
            return $"{w.LocalizedName()} in {where}: mag {(mag == null ? "none" : $"{mag.LocalizedName()} {mag.Count}/{mag.MaxCount}")}, " +
                   $"chamber {(w.Chambers.Length == 0 ? "n/a" : Chambered(w) ? "loaded" : "empty")}, reload mode {w.ReloadMode}";
        }

        // ---------- Dump ----------

        public static void Dump()
        {
            var s = new StringBuilder("Zombies dump:\n");
            void Line(string text) => s.Append("  ").AppendLine(text);
            try
            {
                Line($"match active {ZombiesMatch.Active}, raid started {ZombiesPlugin.RaidStarted}, map editor open {MapEditor.IsOpen}");
                if (!ZombiesMatch.Active || !Singleton<GameWorld>.Instantiated)
                {
                    ZombiesPlugin.Log.LogInfo(s.ToString());
                    return;
                }
                Player me = Singleton<GameWorld>.Instance.MainPlayer;
                Line($"round {ZombiesRounds.Round}, points {ZombiesMatch.Points}, power {(ZombiesMatch.PowerIsOn ? "on" : "off")}, box spot {ZombiesMatch.BoxSpot()}, opened entries [{string.Join(", ", ZombiesMatch.OpenedEntries())}]");
                if (me != null)
                {
                    Vector3 p = me.Transform.position;
                    int zone = ZombiesZones.ZoneAt(p);
                    Line($"me: {p} zone {zone} ({(ZombiesZones.IsOpen(zone) ? "open" : "shut")}), state {ZombiesDowned.Mine}, slaps {ZombiesSlaps.MaxHits} to go down, alive {me.HealthController.IsAlive}");
                    Line($"hands: {HandsText(me)}");
                    foreach (EquipmentSlot slot in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
                        Line($"{slot}: {(me.InventoryController.Inventory.Equipment.GetSlot(slot).ContainedItem is Weapon w ? GunText(w) : "empty")}");
                    var mags = new List<Magazine>();
                    me.InventoryController.GetReachableItemsOfTypeNonAlloc(mags);
                    Line($"mags you can reload from: {(mags.Count == 0 ? "none" : string.Join(", ", mags.Select(m => $"{m.LocalizedName()} {m.Count}/{m.MaxCount} in {m.Parent?.Container?.ID}")))}");
                    var others = me.InventoryController.Inventory.Equipment.GetAllItems().OfType<Magazine>().Where(m => !mags.Contains(m) && !(m.Parent?.Container is Slot)).ToList();
                    if (others.Count > 0)
                        Line($"mags out of reach: {string.Join(", ", others.Select(m => $"{m.LocalizedName()} {m.Count} in {m.Parent?.Container?.ID}"))}");
                    Line($"perks: {string.Join(", ", ZombiesPerks.All.Where(k => ZombiesPerks.Has(k.Id)).Select(k => k.Name))}{(ZombiesDowned.HasQuickRevive() ? ", Quick Revive" : "")} ({ZombiesPerks.Count} of {ZombiesPerks.MaxPerks})");
                    Line($"grenades: {ZombiesGrenades.Count(me)}");
                }
                ZombiesAmmo.LogState();
                s.Append(ZombiesRounds.Describe(true));
            }
            catch (Exception e)
            {
                s.AppendLine($"  (dump stopped: {e})");
            }
            ZombiesPlugin.Log.LogInfo(s.ToString());
        }
    }
}
