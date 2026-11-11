using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's game over. When the match is lost (solo down without Quick Revive, or the last player standing bleeds out):
    /// "GAME OVER" and the rounds survived, the scoreboard (ranked by score), and the game over song; meanwhile the camera
    /// leaves the body and glides through a few shots of the map, live (the zombies still roam it as they were). The
    /// shots are worked out at that moment and checked so the camera never passes through anything (a sphere swept along
    /// each one, ground and walls included); they loop for as long as the song plays. Then the match report is kept
    /// for the main menu (ZombiesReport) and the raid ends (the server puts the character back).
    /// </summary>
    internal class ZombiesGameOver : MonoBehaviour
    {
        private const float ShotSeconds = 7f, Clearance = 0.6f, AtLeast = 12f, AtMost = 90f, FadeOut = 2.5f;
        private static ZombiesGameOver _on;

        public static bool Active => _on != null;

        private struct Shot
        {
            public Vector3 From, To, Look;
        }

        private readonly List<Shot> _shots = new List<Shot>();
        private Player _player;
        private GamePlayerOwner _owner;
        private PlayerCameraController _follow;
        private Transform _camera;
        private AudioSource _song;
        private float _started, _ends;
        private int _rounds;
        private string _why;
        private GUIStyle _big, _small;
        private bool _ended;

        // ---------- The fall before it ----------

        private const float FallSeconds = 2.8f;
        private static bool _falling;
        private static float _fallStart;

        /// <summary>Going down into a game over: first the fall, seen from your own eyes (prone, the screen going red,
        /// no control), then the game over itself (the user, 2026-11-08: show the player going down first).</summary>
        public static void BeginAfterFall(Player me, string why)
        {
            if (_on != null || _falling || me == null)
                return;
            _falling = true;
            Finished = false;
            ZombiesPlugin.Instance.StartCoroutine(Fall(me, why));
        }

        private static System.Collections.IEnumerator Fall(Player me, string why)
        {
            _fallStart = Time.realtimeSinceStartup;
            GamePlayerOwner owner = ((Component)me).GetComponent<GamePlayerOwner>();
            try
            {
                if (owner != null)
                    owner.enabled = false;
                if (!me.IsInPronePose)
                    me.ToggleProne();
                me.EnableSprint(false);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: the fall before the game over: {e.Message}");
            }
            while (Time.realtimeSinceStartup - _fallStart < FallSeconds)
            {
                if (Time.frameCount % 10 == 0)
                    ZombiesPause.StillZombies();
                yield return null;
            }
            _falling = false;
            Begin(me, why);
        }

        /// <summary>The fall's red, drawn over everything (ZombiesReport's OnGUI).</summary>
        public static void DrawFall()
        {
            if (!_falling)
                return;
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _fallStart) / FallSeconds);
            Color before = GUI.color;
            GUI.color = new Color(0.45f, 0f, 0f, Mathf.Lerp(0.15f, 0.65f, t));
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            // The last moments into black, where the game over fades in from
            float black = Mathf.Clamp01((t - 0.8f) / 0.2f);
            if (black > 0f)
            {
                GUI.color = new Color(0f, 0f, 0f, black);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            }
            GUI.color = before;
        }

        /// <summary>The exfil's result for this player (null: no exfil): shown under GAME OVER, and the raid ends as
        /// survived when true.</summary>
        public static bool? ExfilResult;

        public static void Begin(Player me, string why)
        {
            if (_on != null || me == null)
                return;
            Finished = false;
            var go = new GameObject("Zombies_GameOver");
            DontDestroyOnLoad(go);
            _on = go.AddComponent<ZombiesGameOver>();
            _on.Init(me, why);
        }

        private void Init(Player me, string why)
        {
            // The easter egg song stops: the game over plays its own (user, 2026-11-10)
            ZombiesRock.StopSong();
            _player = me;
            _why = why;
            _started = Time.realtimeSinceStartup;
            // Out through the exfil on round 15: you survived round 15 (user, 2026-11-11); dying: the ones before it
            _rounds = ExfilResult == true ? Mathf.Max(0, ZombiesRounds.Round) : Mathf.Max(0, ZombiesRounds.Round - 1);
            // The body stays, the camera leaves it
            _owner = ((Component)me).GetComponent<GamePlayerOwner>();
            _follow = ((Component)me).GetComponent<PlayerCameraController>();
            if (_owner != null)
                _owner.enabled = false;
            if (_follow != null)
                _follow.enabled = false;
            _camera = ZombiesInteraction.ActiveCamera?.transform;
            MakeShots(me.Transform.position);
            Application.onBeforeRender += Pose;
            // The song, over everything (a pause or the loading's quiet never cuts it)
            AudioClip clip = ZombiesWav.Load(System.IO.Path.Combine(ZombiesPlugin.Dir, "sounds"), "zm_gameover");
            if (clip != null)
            {
                _song = gameObject.AddComponent<AudioSource>();
                _song.clip = clip;
                ZombiesHeadAudio.Setup(_song, 8);
                _song.ignoreListenerPause = true;
                ZombiesSounds.Follow(_song, ZombiesSounds.Group.GameOver);
                _song.Play();
            }
            _ends = _started + Mathf.Min(clip != null ? clip.length : 20f, AtMost);
            // The zombies stop where they are and idle (their AI is held: ZombiesPause.BotUpdate)
            ZombiesPause.StillZombies();
            ZombiesNet.SendStatsNow();
            ZombiesReport.Keep(_rounds);
            ZombiesPlugin.Log.LogInfo($"Zombies: GAME OVER ({why}), {_rounds} round(s) survived, {_shots.Count} camera shot(s), song {(clip != null ? clip.length.ToString("0") + " s" : "missing")}");
        }

        // ---------- The shots ----------

        private static int Solid => Physics.DefaultRaycastLayers & ~LayerMask.GetMask("Player", "Deadbody", "HitCollider", "PlayerSpiritAura", "Interactive");

        // A camera path is clear: both ends and the sweep between them are clear of anything solid by Clearance
        private static bool Clear(Vector3 a, Vector3 b)
        {
            if (Physics.CheckSphere(a, Clearance, Solid, QueryTriggerInteraction.Ignore) || Physics.CheckSphere(b, Clearance, Solid, QueryTriggerInteraction.Ignore))
                return false;
            Vector3 d = b - a;
            return !Physics.SphereCast(a, Clearance, d.normalized, out _, d.magnitude, Solid, QueryTriggerInteraction.Ignore);
        }

        // Over the ground (not out over the void past the map's edge)
        private static bool Grounded(Vector3 p) => Physics.Raycast(p, Vector3.down, 60f, Solid, QueryTriggerInteraction.Ignore);

        // ---------- Hand-picked shots: <map>_gameover_shots.txt beside the layout ----------
        // One line a shot, "x y z yaw pitch": the camera there, drifting 2.5 m sideways over the shot, looking where it
        // looked. Made in a match with the "Mark game over shot" key (F12 "16. Debug: machines"): stand (or fly, F8)
        // where the shot should be, look at what it should show, press it. When the file has any, the game over uses
        // only those, in order.

        private static string ShotsFile()
        {
            string layout = MapEditor.FileFor(ZombiesMatch.LocationHere);
            return string.IsNullOrEmpty(layout) ? null
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(layout), ZombiesMatch.LocationHere + "_gameover_shots.txt");
        }

        /// <summary>The camera where it is now, saved as a game over shot.</summary>
        public static void MarkShot()
        {
            Camera cam = ZombiesInteraction.ActiveCamera;
            string file = ShotsFile();
            if (cam == null || file == null)
                return;
            Vector3 p = cam.transform.position, e = cam.transform.eulerAngles;
            string line = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} {1:0.00} {2:0.00} {3:0.0} {4:0.0}", p.x, p.y, p.z, e.y, e.x);
            System.IO.File.AppendAllText(file, line + "\n");
            int n = System.IO.File.ReadAllLines(file).Count(l => l.Trim().Length > 0);
            ZombiesPlugin.Notify($"Game over shot {n} saved.");
            ZombiesPlugin.Log.LogInfo($"Zombies: game over shot {n} saved ({line}) to {file}");
        }

        private bool LoadShots()
        {
            string file = ShotsFile();
            if (file == null || !System.IO.File.Exists(file))
                return false;
            foreach (string raw in System.IO.File.ReadAllLines(file))
            {
                string[] v = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (v.Length < 5 || raw.TrimStart().StartsWith("#"))
                    continue;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (!float.TryParse(v[0], System.Globalization.NumberStyles.Float, ci, out float x) || !float.TryParse(v[1], System.Globalization.NumberStyles.Float, ci, out float y)
                    || !float.TryParse(v[2], System.Globalization.NumberStyles.Float, ci, out float z) || !float.TryParse(v[3], System.Globalization.NumberStyles.Float, ci, out float yaw)
                    || !float.TryParse(v[4], System.Globalization.NumberStyles.Float, ci, out float pitch))
                    continue;
                Vector3 at = new Vector3(x, y, z);
                Quaternion look = Quaternion.Euler(pitch, yaw, 0f);
                Vector3 across = look * Vector3.right;
                // What it looked at: the first thing ahead, else 12 m out
                Vector3 target = Physics.Raycast(at, look * Vector3.forward, out RaycastHit hit, 60f, Solid, QueryTriggerInteraction.Ignore)
                    ? hit.point : at + look * Vector3.forward * 12f;
                _shots.Add(new Shot { From = at - across * 1.25f, To = at + across * 1.25f, Look = target });
            }
            return _shots.Count > 0;
        }

        private void MakeShots(Vector3 died)
        {
            // Hand-picked shots for this map, when there are any
            if (LoadShots())
                return;
            var entries = ZombiesMatch.EntriesHere;
            Vector3 centre = entries.Where(e => e.Type == MapEditor.Kind.PlayerStart).Select(MapEditor.Pos).DefaultIfEmpty(died).First();
            // What's worth looking at: the machines, Pack-a-Punch, the box spots, the power switch, the windows
            var sights = entries.Where(e => e.Type == MapEditor.Kind.PerkMachine || e.Type == MapEditor.Kind.PackAPunch || e.Type == MapEditor.Kind.BoxSpot
                                            || e.Type == MapEditor.Kind.PowerSwitch || e.Type == MapEditor.Kind.Window || e.Type == MapEditor.Kind.Rock)
                .Select(MapEditor.Pos).ToList();
            var candidates = new List<Shot>();
            // Wide arcs round the middle of the map, high up, looking in
            foreach (float radius in new[] { 22f, 32f, 45f })
                foreach (float height in new[] { 9f, 14f, 20f })
                    for (int a = 0; a < 360; a += 30)
                    {
                        Vector3 from = centre + Quaternion.Euler(0f, a, 0f) * Vector3.forward * radius + Vector3.up * height;
                        Vector3 to = centre + Quaternion.Euler(0f, a + 35f, 0f) * Vector3.forward * radius + Vector3.up * (height - 2f);
                        candidates.Add(new Shot { From = from, To = to, Look = centre + Vector3.up * 1.5f });
                    }
            // Slow passes by the map's places, a few metres off, looking at them
            foreach (Vector3 p in sights)
                for (int a = 0; a < 360; a += 45)
                {
                    Vector3 side = Quaternion.Euler(0f, a, 0f) * Vector3.forward;
                    Vector3 across = Vector3.Cross(Vector3.up, side);
                    Vector3 mid = p + side * 6f + Vector3.up * 2.5f;
                    candidates.Add(new Shot { From = mid - across * 3f, To = mid + across * 3f + Vector3.up * 0.5f, Look = p + Vector3.up * 1.2f });
                }
            var rng = new System.Random();
            var ok = candidates.OrderBy(_ => rng.Next())
                .Where(s => Clear(s.From, s.To) && Grounded(s.From) && Grounded(s.To)
                            && !Physics.Linecast(Vector3.Lerp(s.From, s.To, 0.5f), s.Look, Solid, QueryTriggerInteraction.Ignore))
                .ToList();
            // A mix: high arcs and close passes in turn, six at most, no two looking at the same place
            var arcs = ok.Where(s => (s.Look - centre).sqrMagnitude < 1f).Take(3).ToList();
            var passes = ok.Where(s => (s.Look - centre).sqrMagnitude >= 1f).GroupBy(s => s.Look).Select(g => g.First()).Take(3).ToList();
            for (int i = 0; i < Mathf.Max(arcs.Count, passes.Count); i++)
            {
                if (i < arcs.Count)
                    _shots.Add(arcs[i]);
                if (i < passes.Count)
                    _shots.Add(passes[i]);
            }
            // Nothing clear at all: high over where they fell, looking down
            if (_shots.Count == 0)
            {
                Vector3 up = died + Vector3.up * 12f;
                _shots.Add(new Shot { From = up + Vector3.back * 4f, To = up + Vector3.forward * 4f, Look = died });
            }
        }

        private void Pose()
        {
            if (_camera == null || _shots.Count == 0)
                return;
            float t = Time.realtimeSinceStartup - _started;
            int i = (int)(t / ShotSeconds) % _shots.Count;
            float u = Mathf.SmoothStep(0f, 1f, t % ShotSeconds / ShotSeconds);
            Shot s = _shots[i];
            Vector3 at = Vector3.Lerp(s.From, s.To, u);
            _camera.SetPositionAndRotation(at, Quaternion.LookRotation(s.Look - at, Vector3.up));
        }

        private void Update()
        {
            // Every frame: still standing (a zombie mid-step kept walking otherwise)
            if (!_ended && Time.frameCount % 10 == 0)
                ZombiesPause.StillZombies();
            if (_ended || Time.realtimeSinceStartup < _ends)
                return;
            _ended = true;
            End();
        }

        // The song is over: the raid ended behind a black screen. The camera is NOT given back to the body (it cut to
        // your own first-person death after the fade, the user 2026-11-08): the screen stays black and the game quiet
        // until the raid is gone, then the after action report comes up.
        private void End()
        {
            Application.onBeforeRender -= Pose;
            HoldBlack();
            ZombiesPlugin.Log.LogInfo("Zombies: game over finished, leaving the match");
            Finished = true;
            try
            {
                if (_player != null && _player.HealthController.IsAlive)
                    _player.ActiveHealthController?.Kill(EDamageType.Undefined);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: ending the match failed: {e.Message}");
            }
            // Dead alone isn't the end under Fika (it hands the host its spectator camera, "Press F8 to extract"): the
            // raid is ended for real a moment later, EFT's own Stop as killed (2026-11-08)
            string id = _player?.ProfileId;
            ZombiesPlugin.Instance.StartCoroutine(StopRaid(id));
            Destroy(gameObject);
        }

        // ---------- Black until the raid is gone ----------

        private static bool _black;
        private static float _volumeBefore = 1f;

        /// <summary>The screen held black (and the game muted) from the game over's fade until the raid has unloaded.</summary>
        public static bool Black => _black;

        private static void HoldBlack()
        {
            if (_black)
                return;
            _black = true;
            _volumeBefore = AudioListener.volume;
            AudioListener.volume = 0f;
        }

        /// <summary>Each frame (ZombiesReport): the raid is gone, the screen and the sound come back.</summary>
        public static void TickBlack()
        {
            if (!_black || Comfort.Common.Singleton<GameWorld>.Instantiated)
                return;
            _black = false;
            AudioListener.volume = _volumeBefore > 0.01f ? _volumeBefore : 1f;
        }

        /// <summary>Drawn over everything while held black (the game over's own drawing is gone by then).</summary>
        private static GUIStyle _loading;

        public static void DrawBlack()
        {
            if (!_black)
                return;
            GUI.depth = -2000;
            Color before = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            // The raid takes a while to unload: a loading line so it doesn't look frozen (the user, 2026-11-08)
            float k = Screen.height / 1080f;
            if (_loading == null)
                _loading = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold };
            _loading.fontSize = Mathf.RoundToInt(30f * k);
            int dots = (int)(Time.realtimeSinceStartup * 2.5f) % 4;
            float pulse = 0.6f + 0.4f * Mathf.Sin(Time.realtimeSinceStartup * 3f);
            GUI.color = new Color(0.75f, 0.05f, 0.05f, pulse);
            GUI.Label(new Rect(0f, Screen.height - 110f * k, Screen.width - 70f * k, 50f * k), "LOADING" + new string('.', dots).PadRight(3), _loading);
            GUI.color = before;
        }

        /// <summary>The game over has played out (the raid is ending): nothing of the match's own screens shows now.</summary>
        public static bool Finished { get; private set; }

        /// <summary>A new match: the last one's game over is forgotten.</summary>
        public static void ResetFinished() => Finished = false;

        private static System.Collections.IEnumerator StopRaid(string profileId)
        {
            yield return new WaitForSecondsRealtime(1.5f);
            try
            {
                object game = Comfort.Common.Singleton<AbstractGame>.Instance;
                var stop = game == null ? null : HarmonyLib.AccessTools.Method(game.GetType(), "Stop", new[] { typeof(string), typeof(ExitStatus), typeof(string), typeof(float) });
                if (stop == null || string.IsNullOrEmpty(profileId))
                {
                    ZombiesPlugin.Log.LogWarning("Zombies: game over: no game Stop to end the raid with");
                    yield break;
                }
                ZombiesPlugin.Log.LogInfo("Zombies: game over: raid ended (killed)");
                bool survived = ExfilResult == true;
                stop.Invoke(game, new object[] { profileId, survived ? ExitStatus.Survived : ExitStatus.Killed, survived ? "ZB-013" : null, 0f });
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: game over: ending the raid failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        private void OnDestroy()
        {
            Application.onBeforeRender -= Pose;
            if (_on == this)
                _on = null;
        }

        /// <summary>Drawn over everything during the game over.</summary>
        public static void Draw()
        {
            if (_on == null)
                return;
            _on.DrawOver();
        }

        private void DrawOver()
        {
            float k = Screen.height / 1080f;
            if (_big == null)
            {
                _big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _small = new GUIStyle(_big);
            }
            float fade = Mathf.Clamp01((Time.realtimeSinceStartup - _started) / 1.5f);
            _big.fontSize = Mathf.RoundToInt(110f * k);
            _small.fontSize = Mathf.RoundToInt(34f * k);
            Shadowed(new Rect(0f, Screen.height * 0.07f, Screen.width, 130f * k), "GAME OVER", _big, new Color(0.75f, 0.05f, 0.05f, fade));
            string survived = _rounds == 1 ? "You survived 1 round" : $"You survived {_rounds} rounds";
            Shadowed(new Rect(0f, Screen.height * 0.07f + 125f * k, Screen.width, 50f * k), survived, _small, new Color(1f, 1f, 1f, fade));
            if (ExfilResult.HasValue)
                Shadowed(new Rect(0f, Screen.height * 0.07f + 175f * k, Screen.width, 50f * k),
                    ExfilResult.Value ? "SUCCESSFULLY EXFILED" : "EXFIL UNSUCCESSFUL", _small,
                    ExfilResult.Value ? new Color(0.3f, 1f, 0.35f, fade) : new Color(0.9f, 0.1f, 0.08f, fade));
            Color before = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, fade);
            ZombiesStats.DrawBoard(true, 0.3f);
            // To black over the song's last seconds; the match ends as it does
            float black = Mathf.Clamp01((Time.realtimeSinceStartup - (_ends - FadeOut)) / FadeOut);
            if (black > 0f)
            {
                GUI.color = new Color(0f, 0f, 0f, black);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            }
            GUI.color = before;
        }

        private static void Shadowed(Rect r, string text, GUIStyle style, Color colour)
        {
            Color before = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, colour.a * 0.85f);
            GUI.Label(new Rect(r.x + 4f, r.y + 4f, r.width, r.height), text, style);
            GUI.color = colour;
            GUI.Label(r, text, style);
            GUI.color = before;
        }
    }
}
