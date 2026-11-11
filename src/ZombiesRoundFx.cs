using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace KennerZombies
{
    /// <summary>
    /// The original's round presentation. Round 1: the number appears big in the middle of the screen with the round start music,
    /// holds, then glides up into its corner, shrinking as it goes. A round's last zombie dies: the round end music plays
    /// while the counter flashes, and the next round starts the moment it ends (ZombiesRounds waits the music's length),
    /// the start music cutting straight in as the counter goes up. Sounds: sounds/zm_roundstart.wav and zm_roundend.wav;
    /// volume F12 "2b. Zombies music" > "Round start and end music".
    /// </summary>
    internal static class ZombiesRoundFx
    {
        private const float IntroHold = 2.2f, IntroGlide = 1.6f, IntroScale = 2.4f, FlashPeriod = 1.2f, BumpSeconds = 0.9f;
        public const float EndFallback = 8f;

        private static AudioClip _start, _end;
        private static AudioSource _source;
        private static bool _loading;
        private static float _introAt = -1f, _flashUntil = -1f, _bumpAt = -1f;
        private static bool _introDone;

        public static void Load(MonoBehaviour host)
        {
            if (_loading || (_start != null && _end != null))
                return;
            _loading = true;
            host.StartCoroutine(LoadClips());
        }

        public static void Clear()
        {
            _introAt = _flashUntil = _bumpAt = -1f;
            _introDone = false;
            if (_source != null)
                _source.Stop();
        }

        /// <summary>A round starts (host or client): the start music; round 1 also gets the big intro.</summary>
        public static void RoundStarted(int round)
        {
            // A rebuilt match (F8 closed) starts round 1 again: no intro or music a second time this raid
            if (round <= 1 && _introDone)
                return;
            Play(ZombiesWav.Fresh(ref _start));
            _flashUntil = -1f;
            if (round <= 1)
            {
                _introAt = Time.time;
                _introDone = true;
            }
            else
                _bumpAt = Time.time;
        }

        /// <summary>How long the round-start music runs (s): no zombies until it's done (ZombiesRounds).</summary>
        public static float StartLength => _start != null ? _start.length : 0f;

        /// <summary>A round's last zombie died: the end music and the flashing; returns how long until the next round.</summary>
        public static float RoundEnded()
        {
            ZombiesVox.Say("round_end", 0.3f, 60f);
            Play(ZombiesWav.Fresh(ref _end));
            float length = _end != null ? _end.length : EndFallback;
            _flashUntil = Time.time + length;
            return length;
        }

        /// <summary>Where the counter is right now: 0 = the corner, 1 = big in the middle (and how big).</summary>
        public static float Intro(out float scale)
        {
            scale = 1f;
            if (_introAt < 0f)
                return 0f;
            float e = Time.time - _introAt;
            if (e < IntroHold)
            {
                scale = IntroScale;
                return 1f;
            }
            if (e < IntroHold + IntroGlide)
            {
                float u = Mathf.SmoothStep(0f, 1f, (e - IntroHold) / IntroGlide);
                scale = Mathf.Lerp(IntroScale, 1f, u);
                return 1f - u;
            }
            _introAt = -1f;
            return 0f;
        }

        /// <summary>The counter's colour: the original's red, flashing white while the round end plays, a white blink as it goes up.</summary>
        public static Color Colour(Color red)
        {
            float now = Time.time;
            if (now < _flashUntil)
                return Mathf.Repeat(now, FlashPeriod) < FlashPeriod / 2f ? Color.white : red;
            if (_bumpAt > 0f && now - _bumpAt < BumpSeconds)
                return Color.Lerp(Color.white, red, (now - _bumpAt) / BumpSeconds);
            return red;
        }

        private static void Play(AudioClip clip)
        {
            if (clip == null)
                return;
            if (_source == null)
            {
                var go = new GameObject("Zombies_RoundMusic");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _source = go.AddComponent<AudioSource>();
                ZombiesHeadAudio.Setup(_source, 16);
                _source.playOnAwake = false;
                _source.priority = 16;
            }
            // The end music's last note runs straight into the start: cut, never overlapped
            _source.Stop();
            _source.clip = clip;
            _source.volume = ZombiesSounds.Volume(ZombiesSounds.Group.Rounds);
            _source.Play();
            ZombiesHeadAudio.Report(_source, clip.name);
        }

        private static IEnumerator LoadClips()
        {
            string dir = Path.Combine(ZombiesPlugin.Dir, "sounds");
            _start = ZombiesWav.Load(dir, "zm_roundstart");
            _end = ZombiesWav.Load(dir, "zm_roundend");
            ZombiesPlugin.Log.LogInfo($"Zombies rounds: music {(_start != null ? _start.length.ToString("0.0") : "missing")} s / {(_end != null ? _end.length.ToString("0.0") : "missing")} s");
            _loading = false;
            yield break;
        }

        private static IEnumerator Load(string name, Action<AudioClip> done)
        {
            string file = Path.Combine(ZombiesPlugin.Dir, "sounds", name);
            if (!File.Exists(file))
            {
                ZombiesPlugin.Log.LogWarning($"Zombies rounds: {name} is missing");
                yield break;
            }
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, AudioType.OGGVORBIS))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    done(DownloadHandlerAudioClip.GetContent(req));
            }
        }
    }
}
