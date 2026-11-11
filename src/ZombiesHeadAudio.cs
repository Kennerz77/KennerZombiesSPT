using System.Collections.Generic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Sounds heard "in the head" (the cha-ching, the wall buy, round music, the ambience, the loading and game over
    /// songs). Plain 2D sources stayed silent in a raid while 3D ones in the world played (the PaP's crunch, the doors),
    /// so these are 3D sources riding on the listener itself every frame: full volume from any side, nothing to fall off
    /// with. The first time each one plays, what Unity says about it is logged (playing, culled to a virtual voice, the
    /// listener's volume and pause).
    /// </summary>
    internal class ZombiesHeadAudio : MonoBehaviour
    {
        private static AudioListener _listener;
        private static readonly HashSet<string> Logged = new HashSet<string>();

        /// <summary>Sets a source up to be heard in the head (and to follow the listener).</summary>
        public static AudioSource Setup(AudioSource s, int priority = 32)
        {
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 100f;
            s.maxDistance = 1000f;
            s.dopplerLevel = 0f;
            s.spread = 180f;
            s.priority = priority;
            s.playOnAwake = false;
            if (s.GetComponent<ZombiesHeadAudio>() == null)
                s.gameObject.AddComponent<ZombiesHeadAudio>();
            return s;
        }

        private static float _nextLook;

        private static Transform Listener()
        {
            // A search of the scene at most twice a second (it ran every frame for every sound while loading)
            if ((_listener == null || !_listener.isActiveAndEnabled) && Time.unscaledTime >= _nextLook)
            {
                _nextLook = Time.unscaledTime + 0.5f;
                _listener = null;
                foreach (AudioListener l in FindObjectsOfType<AudioListener>())
                    if (l.isActiveAndEnabled)
                    {
                        _listener = l;
                        break;
                    }
            }
            if (_listener != null)
                return _listener.transform;
            Camera c = ZombiesInteraction.ActiveCamera;
            return c != null ? c.transform : null;
        }

        private void LateUpdate()
        {
            Transform l = Listener();
            if (l != null)
                transform.position = l.position;
        }

        /// <summary>Logs a sound's state once, a moment after it started.</summary>
        public static void Report(AudioSource s, string name)
        {
            if (s == null || !Logged.Add(name) || ZombiesPlugin.Instance == null)
                return;
            ZombiesPlugin.Instance.StartCoroutine(ReportLater(s, name));
        }

        private static System.Collections.IEnumerator ReportLater(AudioSource s, string name)
        {
            yield return new WaitForSecondsRealtime(0.2f);
            if (s == null)
                yield break;
            Transform l = Listener();
            ZombiesPlugin.Log.LogInfo($"[sound] {name}: playing {s.isPlaying}, virtual {s.isVirtual}, volume {s.volume:0.00}, clip {(s.clip != null ? s.clip.length.ToString("0.0") + " s" : "one-shot")}, " +
                                      $"listener {(l != null ? l.name : "NONE")} (pause {AudioListener.pause}, volume {AudioListener.volume:0.00}), distance {(l != null ? Vector3.Distance(l.position, s.transform.position).ToString("0.0") : "-")}");
        }
    }
}
