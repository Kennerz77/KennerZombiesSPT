using System.IO;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The map's loading music (&lt;map&gt;_loading.wav beside its layout, e.g. zm_stronghold_loading.wav): from the moment
    /// the map is built, while the black screen shows the lore comic, it is the only sound heard (every other sound in
    /// the game is paused: AudioListener.pause, which this source ignores). As the fade-in starts it fades out over
    /// a second and a half and the game's sound comes back. Never longer than 2 minutes, and a match ending puts the
    /// sound back too.
    /// </summary>
    internal class ZombiesLoadingMusic : MonoBehaviour
    {
        private const float FadeOut = 1.5f, AtMost = 120f;
        private static ZombiesLoadingMusic _on;
        private AudioSource _source;
        private float _started, _fadeFrom = -1f, _volume;

        public static void Begin(string location)
        {
            Stop();
            string layout = MapEditor.FileFor(location);
            string dir = string.IsNullOrEmpty(layout) ? null : Path.GetDirectoryName(layout);
            if (dir == null || !File.Exists(Path.Combine(dir, location + "_loading.wav")))
                return;
            AudioClip clip = ZombiesWav.Load(dir, location + "_loading");
            if (clip == null)
                return;
            var go = new GameObject("Zombies_LoadingMusic");
            DontDestroyOnLoad(go);
            _on = go.AddComponent<ZombiesLoadingMusic>();
            _on._source = go.AddComponent<AudioSource>();
            _on._source.clip = clip;
            ZombiesHeadAudio.Setup(_on._source, 8);
            _on._source.ignoreListenerPause = true;
            _on._volume = ZombiesSounds.Volume(ZombiesSounds.Group.Loading);
            _on._source.volume = _on._volume;
            _on._started = Time.realtimeSinceStartup;
            AudioListener.pause = true;
            // Not Unity's loop: past its end it carries on from LoopFrom (Update), so the intro isn't heard again
            _on._source.loop = false;
            _on._source.Play();
            ZombiesHeadAudio.Report(_on._source, clip.name);
            ZombiesPlugin.Log.LogInfo($"Zombies loading music: {clip.name} ({clip.length:0} s), the rest of the game's sound paused");
        }

        /// <summary>Its sound gone and the game's back, at once.</summary>
        public static void Stop()
        {
            if (_on != null)
                Destroy(_on.gameObject);
            _on = null;
            AudioListener.pause = false;
        }

        private const float LoopFrom = 30f;

        // Where the music is after this long: straight through once, then round from LoopFrom to the end
        private static float Position(float elapsed, float length)
        {
            if (length <= LoopFrom + 1f)
                return Mathf.Repeat(elapsed, Mathf.Max(1f, length - 0.1f));
            if (elapsed < length - 0.05f)
                return elapsed;
            return LoopFrom + Mathf.Repeat(elapsed - length, length - LoopFrom - 0.05f);
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            // The fade-in has started (the player is in) or it has run too long: the game's sound back, the music out
            // 25 s of it (faded out by then), the player loaded in, or the match gone: the game's own sound back (the map's wind)
            // Plays until the player is loaded in (looping if the load runs past its end; the user, 2026-11-07), the
            // match gone, or 2 minutes: then the game's own sound comes back
            if (_fadeFrom < 0f && (ZombiesMatch.LoadedIn || !ZombiesMatch.Active || (now - Mathf.Max(_started, ZombiesLobby.EndedAt) > AtMost && !ZombiesLobby.Waiting)))
            {
                _fadeFrom = now;
                AudioListener.pause = false;
            }
            // Stopped: its end reached (on again from LoopFrom, the user 2026-11-07: a long load flows on from 0:30, not
            // the start), or EFT reset the audio system as the raid started (ZombiesWav remakes the clip): on from where
            // it would be now
            if (_fadeFrom < 0f && _source != null && !_source.isPlaying && _source.clip != null)
            {
                _source.clip = ZombiesWav.Fresh(_source.clip);
                _source.time = Position(now - _started, _source.clip.length);
                _source.Play();
                ZombiesPlugin.Log.LogInfo($"Zombies loading music: on at {_source.time:0.0} s");
            }
            if (_fadeFrom < 0f)
            {
                // The F12 slider followed live
                _volume = ZombiesSounds.Volume(ZombiesSounds.Group.Loading);
                if (_source != null)
                    _source.volume = _volume;
                // EFT's audio reset ends with AudioListener.pause = false: keep the rest of the game quiet
                AudioListener.pause = true;
                return;
            }
            float t = (now - _fadeFrom) / FadeOut;
            if (t >= 1f || _source == null)
            {
                Stop();
                return;
            }
            _source.volume = _volume * (1f - t);
        }

        private void OnDestroy()
        {
            if (_on == this)
            {
                _on = null;
                AudioListener.pause = false;
            }
        }
    }
}
