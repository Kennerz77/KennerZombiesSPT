using System;
using System.Collections.Generic;
using System.Linq;
using EFT.UI;
using SPT.Reflection.Utils;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies lobby over Fika (zombies matches only). Fika's own lobby is its deploying screen: the map loads,
    /// the host gets a START RAID button and the others join from Fika's raid list until the host presses it. The
    /// zombies loading screen (the comic) covered all of that, so while a Fika zombies match waits for its start this
    /// draws the lobby over the comic: everyone in it (each game tells the others its name and whether it's ready, a
    /// ZombiesNet message once a second), players still loading the map, READY UP for the joiners, and START MATCH
    /// for whoever Fika gave the start button (the host), which presses Fika's own button. Nothing in Fika is changed;
    /// solo nothing here shows.
    /// </summary>
    internal static class ZombiesLobby
    {
        private class Entry
        {
            public string Name;
            public bool Ready, Host;
            public float Seen;
        }

        private static readonly Dictionary<string, Entry> Others = new Dictionary<string, Entry>();
        private static bool _ready, _was;
        private static float _nextBeat, _nextFind, _confirmUntil;
        private static DefaultUIButton _start;
        private static GUIStyle _title, _sub, _row, _tag, _button, _hint;
        private static Texture2D _panel;

        /// <summary>When the last lobby ended (realtime), 0 if none: the loading music's 2 minutes count from here.</summary>
        public static float EndedAt { get; private set; }

        /// <summary>A Fika zombies match its host hasn't started yet.</summary>
        public static bool Waiting
        {
            get
            {
                if (!ZombiesMatch.Active || !ZombiesNet.Online || ZombiesNet.FikaWaiting == null)
                    return false;
                try
                {
                    return ZombiesNet.FikaWaiting();
                }
                catch
                {
                    return false;
                }
            }
        }

        private static string MyId => ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile?.ProfileId ?? "";
        private static string MyName => ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile?.Nickname ?? "Player";

        /// <summary>Every frame of a zombies match: opens and closes the lobby, and tells the others who's here.</summary>
        public static void Tick()
        {
            bool waiting = Waiting;
            if (waiting && !_was)
            {
                Others.Clear();
                _ready = false;
                _autoStarted = false;
                _start = null;
                ZombiesPlugin.Log.LogInfo($"Zombies lobby: open ({(ZombiesNet.IsHost ? "host" : "joined")})");
            }
            else if (!waiting && _was)
            {
                EndedAt = Time.realtimeSinceStartup;
                ZombiesPlugin.Log.LogInfo($"Zombies lobby: started with {1 + Others.Count} in it");
                ZombiesParty.Done();
            }
            _was = waiting;
            if (!waiting)
                return;
            float now = Time.realtimeSinceStartup;
            if (now >= _nextFind)
            {
                _nextFind = now + 0.5f;
                if (_start == null)
                    _start = GameObject.Find("FikaStartButton")?.GetComponent<DefaultUIButton>();
            }
            // A party (ZombiesParty): the host starts by itself once the whole party's in the lobby
            // PLAY SOLO: straight in once Fika's start button is up
            // No party (PLAY SOLO, or the map screen's own NEXT): straight in, no lobby panel (the user, 2026-11-07)
            if (CanStart && Starter && (ZombiesParty.Solo || ZombiesParty.Expected <= 1) && !_autoStarted)
            {
                _autoStarted = true;
                ZombiesPlugin.Log.LogInfo("Zombies lobby: solo, starting");
                Start();
            }
            if (CanStart && Starter && ZombiesParty.Expected > 1 && 1 + Others.Count >= ZombiesParty.Expected
                && (ZombiesNet.FikaPlayers?.Invoke() ?? 1) - (ZombiesNet.HeadlessMatch ? 1 : 0) >= ZombiesParty.Expected && !_autoStarted)
            {
                _autoStarted = true;
                ZombiesPlugin.Log.LogInfo($"Zombies lobby: the whole party's in ({ZombiesParty.Expected}: {1 + Others.Count} in the lobby, Fika counts {ZombiesNet.FikaPlayers?.Invoke()}), starting");
                Start();
            }
            ZombiesLoadout.Stage = CanStart ? "Lobby: start when everyone's in" : _start == null && ZombiesNet.IsHost ? "Loading the map" : "Lobby: waiting for the host";
            if (now >= _nextBeat && !ZombiesNet.Headless)
            {
                _nextBeat = now + 1f;
                ZombiesNet.Lobby(MyName, _ready, ZombiesNet.IsHost || ZombiesNet.HeadlessRequester);
                foreach (string gone in Others.Where(o => now - o.Value.Seen > 5f).Select(o => o.Key).ToList())
                    Others.Remove(gone);
            }
        }

        /// <summary>Another player's lobby line (ZombiesNet).</summary>
        public static void Remote(string from, string name, bool ready, bool host)
        {
            if (string.IsNullOrEmpty(from) || from == MyId)
                return;
            if (!Others.TryGetValue(from, out Entry e))
            {
                Others[from] = e = new Entry();
                ZombiesPlugin.Log.LogInfo($"Zombies lobby: {name} is in");
            }
            e.Name = name;
            e.Ready = ready;
            e.Host = host;
            e.Seen = Time.realtimeSinceStartup;
        }

        private static bool _autoStarted;

        // Who presses Fika's start: the host's game, or with a Fika headless hosting, the lobby's leader (Fika gives
        // the headless's requester the start button)
        private static bool Starter => (ZombiesNet.IsHost && !ZombiesNet.Headless) || ZombiesNet.HeadlessRequester;

        private static bool CanStart => _start != null && _start.gameObject.activeInHierarchy;

        private static void Start()
        {
            try
            {
                if (_start == null)
                    return;
                ZombiesPlugin.Log.LogInfo($"Zombies lobby: starting the match ({1 + Others.Count} in the lobby, party of {ZombiesParty.Expected})");
                _start.OnClick?.Invoke();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"Zombies lobby: start failed: {e.Message}");
            }
        }

        /// <summary>Over the comic, every GUI event (its buttons need clicks).</summary>
        public static void Draw()
        {
            // A party (ZombiesParty) readied up on the map screen already and starts by itself once everyone's in:
            // no lobby panel over the loading screen for it
            // The panel isn't shown any more: parties ready up on the map screen, everything else starts by itself
            return;
            if (!Waiting)
                return;
            float s = Screen.height / 1080f;
            if (_panel == null)
            {
                _panel = new Texture2D(1, 1);
                _panel.SetPixel(0, 0, Color.white);
                _panel.Apply();
            }
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _sub = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
                _row = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
                _tag = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
                _hint = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                _button = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold };
            }
            _title.fontSize = Mathf.RoundToInt(34f * s);
            _sub.fontSize = Mathf.RoundToInt(18f * s);
            _row.fontSize = Mathf.RoundToInt(22f * s);
            _tag.fontSize = Mathf.RoundToInt(18f * s);
            _hint.fontSize = Mathf.RoundToInt(15f * s);
            _button.fontSize = Mathf.RoundToInt(22f * s);
            _title.normal.textColor = ZombiesMainMenu.Red;
            _sub.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            _hint.normal.textColor = new Color(0.65f, 0.65f, 0.65f);

            if (Event.current.type == EventType.Layout)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }

            // Everyone: me first, then the host, then the rest by name
            var rows = new List<(string name, bool ready, bool host, bool me)> { (MyName, _ready, ZombiesNet.IsHost || ZombiesNet.HeadlessRequester, true) };
            rows.AddRange(Others.Values.OrderByDescending(e => e.Host).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => (e.Name, e.Ready, e.Host, false)));
            int connected = 1;
            try
            {
                connected = ZombiesNet.FikaPlayers?.Invoke() ?? 1;
            }
            catch
            {
            }
            // Fika counts its headless as a player
            if (ZombiesNet.HeadlessMatch)
                connected--;
            int loading = Mathf.Max(0, connected - rows.Count);

            float w = 620f * s, rowH = 40f * s;
            float h = 150f * s + (rows.Count + (loading > 0 ? 1 : 0)) * rowH + 150f * s;
            var box = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f - 40f * s, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.DrawTexture(box, _panel);
            GUI.color = ZombiesMainMenu.Red;
            GUI.DrawTexture(new Rect(box.x, box.y, box.width, Mathf.Max(2f, 3f * s)), _panel);
            GUI.color = Color.white;

            float y = box.y + 20f * s;
            GUI.Label(new Rect(box.x, y, w, 44f * s), "STRONGHOLD", _title);
            y += 44f * s;
            GUI.Label(new Rect(box.x, y, w, 26f * s), $"ZOMBIES LOBBY  ·  {rows.Count + loading} / 4", _sub);
            y += 50f * s;

            float pad = 30f * s;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                Color c = ScoresHud.PlayerColours[i % 4];
                GUI.color = new Color(1f, 1f, 1f, 0.06f);
                GUI.DrawTexture(new Rect(box.x + pad, y + 2f * s, w - 2f * pad, rowH - 4f * s), _panel);
                GUI.color = Color.white;
                _row.normal.textColor = c;
                GUI.Label(new Rect(box.x + pad + 12f * s, y, w - 2f * pad, rowH), r.name + (r.me ? "  (you)" : ""), _row);
                string tag = r.host ? "HOST" : r.ready ? "READY" : "NOT READY";
                _tag.normal.textColor = r.host ? new Color(1f, 0.85f, 0.25f) : r.ready ? new Color(0.45f, 0.95f, 0.35f) : new Color(0.6f, 0.6f, 0.6f);
                GUI.Label(new Rect(box.x + pad, y, w - 2f * pad - 12f * s, rowH), tag, _tag);
                y += rowH;
            }
            if (loading > 0)
            {
                _row.normal.textColor = new Color(0.6f, 0.6f, 0.6f);
                GUI.Label(new Rect(box.x + pad + 12f * s, y, w - 2f * pad, rowH), $"{loading} more loading the map...", _row);
                y += rowH;
            }

            y += 20f * s;
            var buttonRect = new Rect(box.x + (w - 320f * s) / 2f, y, 320f * s, 52f * s);
            bool allReady = Others.Values.All(e => e.Ready || e.Host) && loading == 0;
            if (CanStart)
            {
                bool confirming = Time.realtimeSinceStartup < _confirmUntil;
                string label = allReady ? "START MATCH" : confirming ? "START ANYWAY?" : $"START MATCH ({Others.Values.Count(e => e.Ready) + 1}/{rows.Count + loading} READY)";
                if (GUI.Button(buttonRect, label, _button))
                {
                    if (allReady || confirming)
                        Start();
                    else
                        _confirmUntil = Time.realtimeSinceStartup + 3f;
                }
            }
            else if (!ZombiesNet.IsHost && !ZombiesNet.HeadlessRequester)
            {
                if (GUI.Button(buttonRect, _ready ? "NOT READY" : "READY UP", _button))
                {
                    _ready = !_ready;
                    _nextBeat = 0f; // tell everyone now
                }
            }
            else
            {
                GUI.Label(buttonRect, "Loading the map...", _sub);
            }
            y += 62f * s;
            GUI.Label(new Rect(box.x + pad, y, w - 2f * pad, 40f * s),
                CanStart ? "Friends join from Fika's raid list: ZOMBIES mode, Stronghold." : "Waiting for the host to start the match.", _hint);
        }
    }
}
