using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Our sounds, read straight from 16-bit PCM WAV files (as the Thundergun's station sounds are). Clips loaded through
    /// Unity's web request from .ogg came out empty in a raid (the map ambience was 0 s long, the wall buy, cha-ching and
    /// round music silent): every sound of the mod is a .wav now (data\sounds, converted with ffmpeg), each clip's length
    /// logged when it loads.
    /// </summary>
    internal static class ZombiesWav
    {
        // ---------- EFT's audio reset ----------
        // EFT resets Unity's audio system (AudioUtils.ResetAudioBuffer -> AudioSettings.Reset) as every raid starts and
        // again when it ends. That empties every clip made with AudioClip.Create: ours were still there but 0 s long
        // (the ambience "0.0 min", round music, cha-ching silent in raids; menu music gone after a raid). Every clip
        // made here is remembered; when the audio system resets each one is made again from its file, our sources
        // playing an old one are given the new one (loops start again), and Fresh() turns a kept old clip into its new one.

        private class Made { public AudioClip Clip; public string Dir, Name; }
        private static readonly List<Made> All = new List<Made>();
        private static readonly Dictionary<AudioClip, AudioClip> NewFor = new Dictionary<AudioClip, AudioClip>(new ByReference());

        // Unity's == calls two destroyed clips equal: these keys go by the C# object itself
        private class ByReference : IEqualityComparer<AudioClip>
        {
            public bool Equals(AudioClip a, AudioClip b) => ReferenceEquals(a, b);
            public int GetHashCode(AudioClip c) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c);
        }
        private static bool _watching, _remaking;

        /// <summary>How many times EFT has reset the audio system since the game started.</summary>
        public static int Resets { get; private set; }

        /// <summary>This clip, or the one made in its place after EFT's audio reset (kept in the field too).</summary>
        public static AudioClip Fresh(ref AudioClip clip)
        {
            clip = Fresh(clip);
            return clip;
        }

        /// <summary>This clip, or the one made in its place after EFT's audio reset.</summary>
        public static AudioClip Fresh(AudioClip clip)
        {
            int guard = 0;
            while ((object)clip != null && NewFor.TryGetValue(clip, out AudioClip next) && guard++ < 64)
                clip = next;
            // Emptied by EFT's audio reset and not remade yet: now, as it's wanted
            if ((object)clip != null && Pending.ContainsKey(clip))
                clip = RemakeNow(clip) ?? clip;
            return clip;
        }

        // Clips emptied by EFT's audio reset, waiting to be made again from their files (the old clip -> how)
        private static readonly Dictionary<AudioClip, Made> Pending = new Dictionary<AudioClip, Made>(new ByReference());

        private static AudioClip RemakeNow(AudioClip old)
        {
            if (!Pending.TryGetValue(old, out Made m))
                return null;
            Pending.Remove(old);
            AudioClip clip = Read(m.Dir, m.Name);
            if (clip == null)
                return null;
            clip.name = m.Clip != null ? m.Clip.name : m.Name;
            NewFor[old] = clip;
            m.Clip = clip;
            UnityEngine.Object.Destroy(old);
            return clip;
        }

        private static void Watch()
        {
            if (_watching)
                return;
            _watching = true;
            AudioSettings.OnAudioConfigurationChanged += changed => Remake(changed);
        }

        private static void Remake(bool deviceChanged)
        {
            if (_remaking)
                return;
            _remaking = true;
            Resets++;
            int made = 0, swapped = 0, restarted = 0;
            float seconds = 0f;
            try
            {
                // Lazily (the user's load-in freeze: 124 clips, 1403 s of sound remade in one frame): every clip marked
                // stale, only those a source holds now remade at once; the rest when they're next played (Fresh)
                foreach (Made m in All)
                    if (m.Clip != null)
                        Pending[m.Clip] = m;
                foreach (AudioSource s in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (s == null || (object)s.clip == null || !Pending.ContainsKey(s.clip))
                        continue;
                    AudioClip clip = RemakeNow(s.clip);
                    if (clip == null)
                        continue;
                    s.clip = clip;
                    swapped++;
                    made++;
                    seconds += clip.length;
                    if (s.loop && s.enabled && s.GetComponent<ZombiesLoadingMusic>() == null)
                    {
                        s.Play();
                        restarted++;
                    }
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies sound: remaking clips after EFT's audio reset failed ({e.Message})");
            }
            _remaking = false;
            ZombiesPlugin.Log.LogInfo($"Zombies sound: EFT reset the audio (#{Resets}{(deviceChanged ? ", device changed" : "")}): " +
                                      $"{made} clips made again now ({seconds:0} s), {Pending.Count} to remake when next played, {restarted} loops restarted");
        }

        /// <summary>The clip from &lt;dir&gt;\&lt;name&gt;.wav, or null (missing or not 16-bit PCM; logged).</summary>
        public static AudioClip Load(string dir, string name)
        {
            Watch();
            AudioClip clip = Read(dir, name);
            if (clip != null)
                All.Add(new Made { Clip = clip, Dir = dir, Name = name });
            return clip;
        }

        private static AudioClip Read(string dir, string name)
        {
            string path = Path.Combine(dir, name + ".wav");
            if (!File.Exists(path))
            {
                ZombiesPlugin.Log.LogWarning($"Zombies sound: {name}.wav is missing ({dir})");
                return null;
            }
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                int channels = BitConverter.ToInt16(bytes, 22), rate = BitConverter.ToInt32(bytes, 24), bits = BitConverter.ToInt16(bytes, 34);
                if (bits != 16)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies sound: {name}.wav is {bits}-bit, needs 16-bit PCM");
                    return null;
                }
                // Walk the chunks to "data" (there can be others before it)
                int at = 12;
                while (at + 8 <= bytes.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                    int size = BitConverter.ToInt32(bytes, at + 4);
                    if (id == "data")
                    {
                        int count = Math.Min(size, bytes.Length - at - 8) / 2;
                        int frames = count / channels;
                        // A long track (the ambience, songs) streams from the samples kept here: one clip that big
                        // came out 0 s long
                        if (frames > rate * 20)
                        {
                            short[] pcm = new short[count];
                            Buffer.BlockCopy(bytes, at + 8, pcm, 0, count * 2);
                            int pos = 0;
                            AudioClip streamed = AudioClip.Create(name, frames, channels, rate, true,
                                data =>
                                {
                                    for (int i = 0; i < data.Length; i++)
                                    {
                                        data[i] = pcm[pos] / 32768f;
                                        pos = (pos + 1) % pcm.Length;
                                    }
                                },
                                p => pos = Math.Max(0, Math.Min(p * channels, pcm.Length - 1)));
                            ZombiesDebug.Log($"sound {name}: {streamed.length:0.0} s (streamed)");
                            return streamed;
                        }
                        var samples = new float[count];
                        for (int i = 0; i < count; i++)
                            samples[i] = BitConverter.ToInt16(bytes, at + 8 + i * 2) / 32768f;
                        AudioClip clip = AudioClip.Create(name, frames, channels, rate, false);
                        clip.SetData(samples, 0);
                        return clip;
                    }
                    at += 8 + size + (size & 1);
                }
                ZombiesPlugin.Log.LogWarning($"Zombies sound: {name}.wav has no data");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies sound: {name}.wav couldn't be read ({e.Message})");
            }
            return null;
        }
    }
}
