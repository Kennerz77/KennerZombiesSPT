using BepInEx.Configuration;
using EFT;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KennerZombies
{
    /// <summary>
    /// The Cymbal Monkey in first person with the original's own arm animations: the clips baked from the original's vm_monkey_bomb_*
    /// onto the M67's hands rig (UnityAssets\Zombies\Editor\MonkeyHands, zombies.bundle zm_monkey_vm_*) are played over
    /// the M67's animation while a monkey is in your hands, following EFT's grenade states:
    ///   from the moment it's in the hands: raise (the key wound), the original's whole 1.5 s whatever EFT's own draw does (its
    ///   draw ends at 0.6 s and cut the crank short, the user 2026-11-08); the throw waits for it (ZombiesQuickGrenade)
    ///   then, held: wind (the rest of the original's pull-pin, cymbals going), played once and held on its last pose
    ///   FIRE / Q FIRE (the throw) -> throw, timed to EFT's throw; IDLE TO OUT -> raise backwards
    /// The bake puts the original's view on EFT's camera (MonkeyHandsImporter.AnchorBlend 0): the monkey close in front, the
    /// right hand under it, as the original shows it; the monkey is the original's view model size there (ZombiesMonkeyHands).
    /// EFT's own controller keeps running underneath (its events still throw the grenade); only the pose is ours. The
    /// monkey model rides the weapon bone (ZombiesMonkeyHands). F12 "21. Monkey bomb" > "the original arms" turns it off.
    /// </summary>
    internal static class ZombiesMonkeyView
    {
        private static ConfigEntry<bool> _on;
        private static PlayableGraph _graph;
        private static AnimationPlayableOutput _output;
        private static Animator _animator;
        private static AnimationClipPlayable _raise, _wind, _throw, _idle, _current, _prev;
        private static AnimationMixerPlayable _mixer;
        private static float _switchAt, _fade = Fade;
        private const float Fade = 0.12f;
        private static bool _missingLogged;
        private static float _since;          // when the monkey came into the hands (the crank runs from here)
        private static string _lastState;

        /// <summary>the original's arm clips are playing on your hands (ZombiesMonkeyHands puts the monkey on their weapon bone).</summary>
        public static bool Active => _graph.IsValid() && _animator != null;

        // EFT's grenade states we know; anything else in the first moments is the pull-out (the quick throw's own draw)
        private static readonly string[] Known = { "OUT TO IDLE", "IDLE TO OUT", "FIRE START", "FIRE IDLE", "ALT FIRE START", "ALT FIRE IDLE", "FIRE", "Q FIRE", "ALT FIRE", "IDLE" };
        internal const float CrankSeconds = 1.5f; // The original's raise: 45 frames at 30 fps, all of it

        private static ConfigEntry<float> _weight, _windSpeed;

        /// <summary>Seconds of the crank still to play (0 once it's done, or without the original's arms): the throw waits for it.</summary>
        public static float CrankLeft => Active ? Mathf.Max(0f, CrankSeconds - (Time.time - _since)) : 0f;

        public static void Bind(ConfigFile config, string section)
        {
            // New key (was "the original arms (test)", off): on by default now the monkey rides the weapon bone the clips move
            _on = config.Bind(section, "Classic arms", true, "The monkey in your hands moves with the classic arm animations: the key cranked as it comes out (held or tapped), cymbals going while held, the classic throw. Off: the M67's arms.");
            _weight = config.Bind(section, "Classic arms: strength", 1f, new ConfigDescription(
                "How much of the classic arm pose is shown over the M67's (1: all classic, 0: all EFT; in between blends them).", new AcceptableValueRange<float>(0f, 1f)));
            _windSpeed = config.Bind(section, "Classic arms: wind speed", 1f, new ConfigDescription(
                "How fast the held wind-up (cymbals going) loops.", new AcceptableValueRange<float>(0.2f, 3f)));
            _weight.SettingChanged += (_, __) => { if (_output.IsOutputValid()) _output.SetWeight(_weight.Value); };
        }

        /// <summary>Each frame of a match, for your own player.</summary>
        public static void Tick(Player me)
        {
            var hands = me?.HandsController;
            bool monkey = _on?.Value == true && ZombiesMatch.Active && hands != null && ZombiesMonkey.Is(hands.Item) && hands.ControllerGameObject != null;
            if (!monkey)
            {
                Stop();
                return;
            }
            Animator a = _animator;
            if (a == null || !a.transform.IsChildOf(hands.ControllerGameObject.transform) && a.gameObject != hands.ControllerGameObject)
            {
                Stop();
                a = hands.ControllerGameObject.GetComponentInChildren<Animator>();
                if (a == null || !Start(a))
                    return;
                _since = Time.time;
            }
            int layer = a.GetLayerIndex("Hands");
            if (layer < 0)
                layer = 0;
            AnimatorStateInfo st = a.GetCurrentAnimatorStateInfo(layer);
            float crank = (Time.time - _since) / CrankSeconds;
            string state = System.Array.Find(Known, n => st.IsName(n));
            if (state != _lastState)
            {
                _lastState = state;
                ZombiesDebug.Log($"monkey hands state: {state ?? "unknown " + st.shortNameHash} at {Time.time - _since:0.00} s ({hands.GetType().Name})");
            }
            bool throwing = st.IsName("FIRE") || st.IsName("Q FIRE") || st.IsName("ALT FIRE");
            if (throwing)
                // From wherever the hold left the arm (cocked back), on through the original's throw
                Show(_throw, Mathf.Lerp(_throwFrom, 1f, Mathf.Clamp01(st.normalizedTime)));
            else if (st.IsName("IDLE TO OUT"))
                Show(_raise, 1f - Mathf.Clamp01(st.normalizedTime));
            else if (crank < 1f)
                // The crank by the clock, whatever EFT's draw/idle/pin states do underneath
                Show(_raise, Mathf.Clamp01(crank));
            else
            {
                // Wound and still held: the arm drawn back into the original's throw, cocked over the shoulder and kept
                // there until the key's let go (the rest of the pull-pin looped as the same move over and over,
                // the user 2026-11-08)
                _throwFrom = HoldPoint;
                Show(_throw, HoldPoint, HoldFade);
            }
        }

        // Where in the original's throw the arm is cocked back over the shoulder (frame ~7 of 15, the hand up at the top right,
        // MonkeyHandsPreview), and how long the arm takes to draw back into it
        private const float HoldPoint = 0.45f, HoldFade = 0.35f;
        private static float _throwFrom;

        // A clip shown: at a set point of it (0..1), or running looped (-1)
        private static void Show(AnimationClipPlayable clip, float at, float fade = Fade)
        {
            if (!clip.IsValid())
                return;
            if (!_current.Equals(clip))
            {
                // A short cross-fade from the clip before (frozen where it was): no snap from the crank into the hold
                // or the throw (the user saw it "cut to" the next pose, 2026-11-08)
                _graph.Disconnect(_mixer, 0);
                _graph.Disconnect(_mixer, 1);
                _prev = default;
                if (_current.IsValid())
                {
                    _current.SetSpeed(0);
                    _graph.Connect(_current, 0, _mixer, 1);
                    _prev = _current;
                }
                _graph.Connect(clip, 0, _mixer, 0);
                _current = clip;
                _switchAt = Time.time;
                _fade = fade;
                if (at < 0f)
                    clip.SetTime(0);
            }
            float w = _prev.IsValid() ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((Time.time - _switchAt) / Mathf.Max(0.01f, _fade))) : 1f;
            _mixer.SetInputWeight(0, w);
            _mixer.SetInputWeight(1, 1f - w);
            if (at < 0f)
            {
                clip.SetSpeed(_windSpeed?.Value ?? 1f);
                // Played through once, then held on its last pose while the key stays down (looping it read as the
                // same move over and over, the user 2026-11-08)
                double len = clip.GetAnimationClip().length;
                if (len > 0 && clip.GetTime() >= len)
                {
                    clip.SetTime(len);
                    clip.SetSpeed(0);
                }
            }
            else
            {
                clip.SetSpeed(0);
                clip.SetTime(at * clip.GetAnimationClip().length);
            }
        }

        private static bool Start(Animator a)
        {
            AnimationClip raise = ZombiesAssets.Anim("zm_monkey_vm_raise"), wind = ZombiesAssets.Anim("zm_monkey_vm_wind"),
                throwClip = ZombiesAssets.Anim("zm_monkey_vm_throw"), idle = ZombiesAssets.Anim("zm_monkey_vm_idle");
            if (raise == null || wind == null || throwClip == null || idle == null)
            {
                if (!_missingLogged)
                    ZombiesPlugin.Log.LogWarning("Zombies monkey view: the classic arm clips aren't in zombies.bundle; the M67's arms stay");
                _missingLogged = true;
                return false;
            }
            _animator = a;
            _graph = PlayableGraph.Create("zm_monkey_vm");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _output = AnimationPlayableOutput.Create(_graph, "zm_monkey_arms", a);
            _output.SetWeight(_weight?.Value ?? 1f);
            _raise = AnimationClipPlayable.Create(_graph, raise);
            _wind = AnimationClipPlayable.Create(_graph, wind);
            _throw = AnimationClipPlayable.Create(_graph, throwClip);
            _idle = AnimationClipPlayable.Create(_graph, idle);
            _mixer = AnimationMixerPlayable.Create(_graph, 2);
            _output.SetSourcePlayable(_mixer);
            _current = default;
            _prev = default;
            _throwFrom = 0f; // a tap's throw (no hold): the whole of the original's throw
            Show(_raise, 0f);
            _graph.Play();
            ZombiesPlugin.Log.LogInfo($"Zombies monkey view: Classic arms on {a.name}");
            return true;
        }

        private static void Stop()
        {
            if (_graph.IsValid())
                _graph.Destroy();
            _animator = null;
            _current = default;
            _prev = default;
            _lastState = null;
        }
    }
}
