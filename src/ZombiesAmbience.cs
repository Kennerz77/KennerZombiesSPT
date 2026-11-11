using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace KennerZombies
{
    /// <summary>
    /// The map's ambience bed: &lt;map&gt;_ambience.wav beside its layout (shipped with the map mod, like the lore picture),
    /// looped quietly under everything for the whole zombies match, fading in over a few seconds. Its volume is F12
    /// "2. Zombies sounds" > "Map ambience" (times the master). Loaded whole: the file is mono 24 kHz, about 30 MB
    /// decoded (streamed, and with the mp3's cover art left in, it came out 0 s long and silent). A rebuilt match
    /// (closing F8) keeps it playing; the raid ending stops it.
    /// </summary>
    internal static class ZombiesAmbience
    {
        private const float FadeInSeconds = 4f;

        private static AudioClip _clip;
        private static string _clipFor;
        private static AudioSource _source;
        private static string _playingFor;

        public static void Start(MonoBehaviour host, string location)
        {
            if (_source != null && _playingFor == location)
                return;
            Stop();
            host.StartCoroutine(Play(location));
        }

        public static void Stop()
        {
            if (_source != null)
                UnityEngine.Object.Destroy(_source.gameObject);
            _source = null;
            _playingFor = null;
        }

        private static IEnumerator Play(string location)
        {
            if (_clip == null || _clipFor != location)
            {
                string layout = MapEditor.FileFor(location);
                string dir = string.IsNullOrEmpty(layout) ? null : Path.GetDirectoryName(layout);
                AudioClip loaded = dir == null || !File.Exists(Path.Combine(dir, location + "_ambience.wav")) ? null : ZombiesWav.Load(dir, location + "_ambience");
                if (loaded == null)
                {
                    ZombiesPlugin.Log.LogInfo($"Zombies ambience: none for {location} ({location}_ambience.wav beside its layout)");
                    yield break;
                }
                if (_clip != null)
                    UnityEngine.Object.Destroy(_clip);
                _clip = loaded;
                _clip.name = location + "_ambience";
                _clipFor = location;
            }
            // Only once the player is fully in: the kit on and the black loading screen fading
            while (ZombiesMatch.Active && !ZombiesMatch.LoadedIn)
                yield return null;
            // The match may have ended (or another started) while it loaded
            if (!ZombiesMatch.Active || _source != null)
                yield break;
            var go = new GameObject("Zombies_Ambience");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _source = go.AddComponent<AudioSource>();
            _source.clip = ZombiesWav.Fresh(ref _clip);
            _source.loop = true;
            _source.playOnAwake = false;
            ZombiesHeadAudio.Setup(_source);
            _source.priority = 32;
            _source.volume = 0f;
            _source.Play();
            go.AddComponent<Fader>();
            ZombiesHeadAudio.Report(_source, _clip.name);
            _playingFor = location;
            ZombiesPlugin.Log.LogInfo($"Zombies ambience: {_clip.name} looping ({_clip.length / 60f:0.0} min)");
        }

        // Fades in, then follows the F12 volume live
        private class Fader : MonoBehaviour
        {
            private float _in;
            private AudioSource _s;

            private void Awake() => _s = GetComponent<AudioSource>();

            private void Update()
            {
                _in = Mathf.MoveTowards(_in, 1f, Time.unscaledDeltaTime / FadeInSeconds);
                _s.volume = _in * ZombiesSounds.Volume(ZombiesSounds.Group.Ambience);
            }
        }
    }
}
