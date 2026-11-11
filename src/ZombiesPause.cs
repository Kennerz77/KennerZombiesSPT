using System;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.UI.Screens;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Pausing a zombies match, classic style, without stopping the game's clock (stopping it, Time.timeScale 0, also froze
    /// EFT's Esc menu: its options greyed out, and a click there started the resume):
    /// - Solo: Esc's menu (or its settings) pauses the match behind it: every zombie stands frozen (their AI's update is
    ///   skipped, they stop where they are), no zombie spawns and the round's timers wait. The menu works as normal.
    ///   Back in the game (the menu closed for a moment), a 3 second countdown runs, still paused, then play carries on.
    /// - With a team: only the host can pause, with a "Pause match" button over their Esc menu; the zombies live on the
    ///   host's game, so they freeze for everyone, and every player sees ZombiesNet.HeadlessMatch ? "PAUSED BY THE PARTY LEADER" : "PAUSED BY HOST". "Resume match" counts down
    ///   the same way.
    /// </summary>
    internal static class ZombiesPause
    {
        private const float CountdownSeconds = 3f, ClosedFor = 0.3f;
        private static bool _frozen, _hostPause, _remotePaused, _patched;
        private static float _frozenAt, _resumeAt = -1f, _closedSince = -1f;
        private static Enum _lastScreen;
        private static GUIStyle _style, _banner;

        /// <summary>Seconds this match has stood paused so far (the session time leaves them out).</summary>
        public static float PausedTotal => _pausedTotal + (_frozen ? Time.realtimeSinceStartup - _frozenReal : 0f);
        private static float _pausedTotal, _frozenReal;

        /// <summary>The match stands still (zombies frozen, round timers waiting).</summary>
        public static bool Paused => _frozen;

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            harmony.Patch(AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual)),
                prefix: new HarmonyMethod(typeof(ZombiesPause), nameof(BotUpdate)));
        }

        // A frozen match: the zombies' AI doesn't run (nothing moves them, nothing swings)
        private static bool BotUpdate() => !((_frozen || ZombiesGameOver.Active) && ZombiesMatch.Active);

        /// <summary>Every zombie stopped where it stands (its walk cut: it idles), for the pause and the game over.</summary>
        public static void StillZombies()
        {
            foreach (BotOwner z in ZombiesRounds.Zombies.ToList())
                try
                {
                    z?.Mover?.Stop();
                    z?.GetPlayer?.Move(Vector2.zero);
                    z?.GetPlayer?.EnableSprint(false);
                }
                catch
                {
                }
        }

        /// <summary>EFT's Esc menu (or its settings) is up.</summary>
        public static bool IsMenuOpen() => MenuOpen(out _);

        private static bool MenuOpen(out Enum screen)
        {
            // The screen manager outlives its screens at the match's end (its getter throws then)
            try
            {
                screen = Singleton<IScreenManager>.Instance?.CurrentScreenType;
            }
            catch
            {
                screen = null;
            }
            return screen is EEftScreenType type && (type == EEftScreenType.MainMenu || type == EEftScreenType.Settings);
        }

        public static void Tick()
        {
            bool menu = MenuOpen(out Enum screen);
            if (!Equals(screen, _lastScreen))
            {
                _lastScreen = screen;
                ZombiesDebug.Log($"screen: {screen}");
            }
            bool solo = Solo;
            if (!solo)
            {
                // A team: the host's button decides (Draw); counting down after "Resume match"
                if (_frozen && !_hostPause && _resumeAt > 0f && Time.realtimeSinceStartup >= _resumeAt)
                {
                    Unfreeze();
                    ZombiesNet.Pause(false);
                }
                return;
            }
            if (menu && !MapEditor.IsOpen)
            {
                _closedSince = -1f;
                _resumeAt = -1f;
                if (!_frozen)
                    Freeze("solo menu");
                return;
            }
            if (!_frozen)
                return;
            // Back in the game only once the menu has stayed closed a moment (a click on the menu never resumes)
            if (_closedSince < 0f)
                _closedSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - _closedSince < ClosedFor)
                return;
            if (_resumeAt < 0f)
                _resumeAt = Time.realtimeSinceStartup + CountdownSeconds;
            else if (Time.realtimeSinceStartup >= _resumeAt)
                Unfreeze();
        }

        private static void Freeze(string why)
        {
            _frozen = true;
            _frozenAt = Time.time;
            _frozenReal = Time.realtimeSinceStartup;
            foreach (BotOwner z in ZombiesRounds.Zombies.ToList())
                try
                {
                    z?.Mover?.Stop();
                    z?.GetPlayer?.Move(Vector2.zero);
                }
                catch
                {
                }
            ZombiesPlugin.Log.LogInfo($"Zombies: paused ({why})");
        }

        private static void Unfreeze()
        {
            if (!_frozen)
                return;
            _frozen = false;
            _pausedTotal += Time.realtimeSinceStartup - _frozenReal;
            _resumeAt = -1f;
            _closedSince = -1f;
            float stood = Time.time - _frozenAt;
            ZombiesRounds.ShiftTimers(stood);
            // A moment to move before anything lands
            ZombiesSlaps.SafeUntil = Time.time + ZombiesSlaps.Grace;
            ZombiesPlugin.Log.LogInfo($"Zombies: back in play (paused {stood:0} s)");
        }

        /// <summary>Play carries on now (the match ending).</summary>
        public static void Resume()
        {
            _pausedTotal = 0f;
            _hostPause = _remotePaused = false;
            Unfreeze();
        }

        /// <summary>A client: the host paused or resumed the match.</summary>
        public static void FromHost(bool on) => _remotePaused = on;

        // Solo pause (Esc freezes it) only with one player on this game's own match; a Fika headless match is always
        // a team match (its host is nobody's game), even down to one player
        private static bool Solo => !ZombiesNet.HeadlessMatch && ZombiesTeam.List().Count <= 1;

        /// <summary>
        /// Who has the Pause match button: the host's game, or in a Fika headless match the leader (player 1: the team
        /// list is in join order, the lobby's leader first; if they leave, the next player gets it).
        /// </summary>
        private static bool Controller
        {
            get
            {
                if (!ZombiesNet.HeadlessMatch)
                    return ZombiesNet.IsHost;
                if (ZombiesNet.Headless)
                    return false;
                var team = ZombiesTeam.List();
                return team.Count > 0 && team[0] != null && team[0].IsYourPlayer;
            }
        }

        // The leader's own view of the pause (a headless match): what it last asked for, until the host says
        private static bool Shown => ZombiesNet.HeadlessMatch && !ZombiesNet.IsHost ? _remotePaused : _hostPause;

        /// <summary>The host: the match's leader asked to pause or resume (a Fika headless match).</summary>
        public static void Asked(bool on, string from)
        {
            if (!ZombiesMatch.Active)
                return;
            ZombiesPlugin.Log.LogInfo($"Zombies: {from} asked to {(on ? "pause" : "resume")} the match");
            HostSet(on);
        }

        private static void HostSet(bool on)
        {
            if (on)
            {
                if (_hostPause)
                    return;
                _hostPause = true;
                _resumeAt = -1f;
                if (!_frozen)
                    Freeze(ZombiesNet.Headless ? "the leader, on the headless" : "host");
                ZombiesNet.Pause(true);
            }
            else if (_hostPause)
            {
                _hostPause = false;
                _resumeAt = Time.realtimeSinceStartup + CountdownSeconds;
            }
        }

        // Flip on to bring the party leader's / host's Pause match button back
        private const bool TeamPauseEnabled = false;

        public static void Draw()
        {
            if (!ZombiesMatch.Active)
                return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _banner = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            }
            bool solo = Solo;
            // The host's button (a headless match: the leader's), over their Esc menu
            // Team pause is off for now (user, 2026-11-10: broken over Fika, to come back later as a QoL feature);
            // solo pause (Esc) is unchanged
            if (TeamPauseEnabled && !solo && Controller && MenuOpen(out _))
            {
                var r = new Rect(Screen.width / 2f - 130f, Screen.height * 0.06f, 260f, 46f);
                if (GUI.Button(r, Shown ? "Resume match" : "Pause match"))
                {
                    bool on = !Shown;
                    if (ZombiesNet.IsHost)
                        HostSet(on);
                    else
                    {
                        // The headless decides; it tells everyone (this game too) with its Pause message
                        ZombiesNet.AskPause(on);
                        ZombiesPlugin.Log.LogInfo($"Zombies: asked the headless to {(on ? "pause" : "resume")} the match");
                    }
                }
            }
            if (!solo && (_frozen || _remotePaused) && _resumeAt < 0f)
            {
                _banner.fontSize = Mathf.RoundToInt(Screen.height * 0.045f);
                Label(new Rect(0f, Screen.height * 0.16f, Screen.width, Screen.height * 0.08f), ZombiesNet.HeadlessMatch ? "PAUSED BY THE PARTY LEADER" : "PAUSED BY HOST", _banner, Color.white);
            }
            if (!_frozen || _resumeAt < 0f)
                return;
            int left = Mathf.CeilToInt(_resumeAt - Time.realtimeSinceStartup);
            if (left < 1)
                return;
            _style.fontSize = Mathf.RoundToInt(Screen.height * 0.12f);
            Label(new Rect(0f, Screen.height * 0.3f, Screen.width, Screen.height * 0.25f), left.ToString(), _style, new Color(0.75f, 0.05f, 0.05f));
        }

        private static void Label(Rect r, string text, GUIStyle style, Color colour)
        {
            Color before = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 4f, r.y + 4f, r.width, r.height), text, style);
            GUI.color = colour;
            GUI.Label(r, text, style);
            GUI.color = before;
        }
    }
}
