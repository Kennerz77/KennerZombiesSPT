using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.Networking;

namespace KennerZombies
{
    /// <summary>
    /// The power in a zombies match : until the power switch is thrown, the perk machines and Pack-a-Punch are dark
    /// (their lights off), and the map's exfils are shut. Thrown: the original's power-on sound, the power lights flicker on
    /// (PowerLights), each machine's lights flicker to life one after another and it starts the original's machine hum, and the
    /// locked door at ZB-013 unlocks and swings open. The exfils stay shut until the rounds open them (ZB-013 from round
    /// 15, then every 5th round); ExfilsOpen is for the rounds to set.
    /// </summary>
    internal static class ZombiesPower
    {
        private class Machine
        {
            public GameObject Root;
            public Light[] Lights;
            public float[] Intensity;
            public bool PaP;
        }

        private static readonly List<Machine> Machines = new List<Machine>();
        private static AudioClip _powerOn, _hum, _papIdle;
        private static float _nextExfilCheck;

        /// <summary>Exfils open (the rounds' call; shut until then).</summary>
        public static bool ExfilsOpen { get; set; }

        /// <summary>The match is built: the machines go dark until the power is on, the exfils shut.</summary>
        public static void OnBuilt(MonoBehaviour host)
        {
            Machines.Clear();
            ExfilsOpen = false;
            Shut.Clear();
            foreach (MapVisual v in UnityEngine.Object.FindObjectsOfType<MapVisual>())
            {
                if (v.Entry == null || (v.Entry.Type != MapEditor.Kind.PerkMachine && v.Entry.Type != MapEditor.Kind.PackAPunch))
                    continue;
                // Solo, Quick Revive runs without the power 
                if (v.Entry.What == "revive" && ZombiesTeam.List().Count <= 1)
                    continue;
                Light[] lights = v.GetComponentsInChildren<Light>(true);
                var m = new Machine { Root = v.gameObject, Lights = lights, Intensity = lights.Select(l => l.intensity).ToArray(), PaP = v.Entry.Type == MapEditor.Kind.PackAPunch };
                foreach (Light l in lights)
                    l.enabled = false;
                Machines.Add(m);
            }
            if (_powerOn == null)
                host.StartCoroutine(LoadSounds());
            ShutExfils();
            LockZb013Door();
            ZombiesPlugin.Log.LogInfo($"Zombies power: off ({Machines.Count} machines dark, exfils shut)");
        }

        /// <summary>Every second: the exfils stay shut while the rounds haven't opened them (EFT may set them up after the build).</summary>
        public static void Tick()
        {
            if (Time.time < _nextExfilCheck)
                return;
            _nextExfilCheck = Time.time + 1f;
            if (!ExfilsOpen)
                ShutExfils();
        }

        private static readonly HashSet<ExfiltrationPoint> Shut = new HashSet<ExfiltrationPoint>();

        // The map's exits, found once per match (a FindObjectsOfType every second took ~40 ms each time: part of the
        // stutter, the user's walking glitch 2026-11-07)
        private static ExfiltrationPoint[] _exits;
        private static UnityEngine.SceneManagement.Scene _exitsScene;

        private static void ShutExfils()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (_exits == null || _exitsScene != scene || _exits.Any(p => p == null))
            {
                _exits = UnityEngine.Object.FindObjectsOfType<ExfiltrationPoint>();
                _exitsScene = scene;
            }
            foreach (ExfiltrationPoint point in _exits)
                if (point.Status != EExfiltrationStatus.NotPresent)
                {
                    point.Disable();
                    Shut.Add(point);
                }
        }

        /// <summary>The rounds open the exits (the basement exfil, ZB-013, from round 15): the ones shut come back.</summary>
        public static void OpenExfils()
        {
            ExfilsOpen = true;
            foreach (ExfiltrationPoint point in Shut)
                if (point != null)
                    point.Enable();
            ZombiesPlugin.Log.LogInfo($"Zombies power: exits open ({Shut.Count})");
            Shut.Clear();
            ZombiesPlugin.Notify("The basement exfil is open.");
        }

        /// <summary>The switch is thrown (ZombiesMatch.PowerOn).</summary>
        public static void On(MonoBehaviour host)
        {
            Play2D(ZombiesWav.Fresh(ref _powerOn), 1f);
            PowerLights.PowerOn();
            for (int i = 0; i < Machines.Count; i++)
                host.StartCoroutine(WakeMachine(Machines[i], 0.6f + i * 0.35f));
            OpenZb013Door();
        }

        /// <summary>
        /// A map that's powered from the start (MapFile.AlwaysPowered): no sting, no flicker. The power lights and every
        /// machine's lights are simply on, the machines hum (once the hum has loaded), and ZB-013's door is open.
        /// </summary>
        public static void OnFromStart(MonoBehaviour host)
        {
            PowerLights.AllOn();
            foreach (Machine m in Machines)
                host.StartCoroutine(WakeNow(m));
            OpenZb013Door();
        }

        private static IEnumerator WakeNow(Machine m)
        {
            for (int i = 0; i < m.Lights.Length; i++)
                if (m.Lights[i] != null)
                {
                    m.Lights[i].enabled = true;
                    m.Lights[i].intensity = m.Intensity[i];
                }
            float until = Time.time + 10f;
            while (_hum == null && Time.time < until)
                yield return null;
            Hum(m);
        }

        /// <summary>Pack-a-Punch's idle hum once powered, on its own child (the model's own audio on its root had made
        /// the old check skip it: no PaP idle sound, user 2026-11-09). Perk machines hum through ZombiesMachineAudio
        /// (Perk Beverages' own hum), so only PaP gets this one.</summary>
        private static void Hum(Machine m)
        {
            if (m.Root == null || !m.PaP || m.Root.transform.Find(HumName) != null)
                return;
            ZombiesPlugin.Instance.StartCoroutine(HumWhenIn(m));
        }

        // Started only once the player is in: EFT resets the audio at raid start, which empties clips made before it,
        // and the hum started at map build "played" an empty clip all match (user, 2026-11-09: no PaP idle sound).
        // The original's own PaP idle loop (zmb perksacola pap loop.wav = data sounds zm_pap_idle.wav), else the machine hum.
        private static IEnumerator HumWhenIn(Machine m)
        {
            float until = Time.time + 300f;
            while (Time.time < until && (!ZombiesMatch.LoadedIn || _papIdle == null && _hum == null))
                yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(1f);
            if (m.Root == null || m.Root.transform.Find(HumName) != null)
                yield break;
            // The Pack-a-Punch mod's own idle sound (user, 2026-11-09: the original's loop.wav ticked, the rollers should roll):
            // the machine model's Audio_ProcessLoop (the rollers turning), at the PaP mod's F12 "Machine hum" volume
            AudioSource own = m.Root.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(a => a.name == "Audio_ProcessLoop" && a.clip != null);
            if (own != null)
            {
                own.loop = true;
                own.playOnAwake = false;
                own.mute = false;
                own.enabled = true;
                own.spatialBlend = 1f;
                own.rolloffMode = AudioRolloffMode.Linear;
                own.minDistance = 1f;
                own.maxDistance = ZombiesMachineAudio.HumRange;
                own.dopplerLevel = 0f;
                own.volume = PaPHumVolume() * ZombiesSounds.Volume(ZombiesSounds.Group.PackAPunch);
                if (!own.isPlaying)
                    own.Play();
                ZombiesPlugin.Log.LogInfo($"Zombies power: Pack-a-Punch idle rollers on ({own.clip.name}, volume {own.volume:0.00}, {own.maxDistance:0} m)");
                yield break;
            }
            AudioClip clip = _papIdle != null ? ZombiesWav.Fresh(ref _papIdle) : ZombiesWav.Fresh(ref _hum);
            if (clip == null)
                yield break;
            var go = new GameObject(HumName);
            go.transform.SetParent(m.Root.transform, false);
            go.transform.localPosition = new Vector3(0f, 1f, 0f);
            AudioSource hum = go.AddComponent<AudioSource>();
            hum.clip = clip;
            hum.loop = true;
            hum.playOnAwake = false;
            hum.spatialBlend = 1f;
            hum.rolloffMode = AudioRolloffMode.Linear;
            hum.minDistance = 1f;
            hum.maxDistance = ZombiesMachineAudio.HumRange;
            hum.dopplerLevel = 0f;
            hum.volume = 0.6f * ZombiesSounds.Volume(ZombiesSounds.Group.PackAPunch);
            hum.time = UnityEngine.Random.Range(0f, clip.length * 0.9f);
            hum.Play();
            ZombiesPlugin.Log.LogInfo($"Zombies power: Pack-a-Punch idle hum on ({clip.name}, {clip.length:0.0} s, {hum.maxDistance:0} m)");
        }

        private const string HumName = "KZ_PowerHum";

        // The PaP mod's F12 "1. Volume" > "Machine hum" (read only; 0.1 if it can't be read)
        private static float PaPHumVolume()
        {
            try
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.kennerz.paphideoutmodule", out var pap) && pap.Instance != null
                    && pap.Instance.Config.TryGetEntry("1. Volume", "Machine hum", out BepInEx.Configuration.ConfigEntry<float> hum))
                    return hum.Value;
            }
            catch
            {
            }
            return 0.1f;
        }

        // A machine comes to life: its lights stutter on, then it hums
        private static IEnumerator WakeMachine(Machine m, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (m.Root == null)
                yield break;
            for (int flick = 0; flick < 5; flick++)
            {
                bool on = flick % 2 == 0;
                for (int i = 0; i < m.Lights.Length; i++)
                    if (m.Lights[i] != null)
                    {
                        m.Lights[i].enabled = true;
                        m.Lights[i].intensity = on ? m.Intensity[i] : m.Intensity[i] * 0.15f;
                    }
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.04f, 0.14f));
            }
            for (int i = 0; i < m.Lights.Length; i++)
                if (m.Lights[i] != null)
                    m.Lights[i].intensity = m.Intensity[i];
            Hum(m);
        }

        // ZB-013's bunker door: the grille door in the construction factory's basement (the user's Ctrl+I, 2026-11-02)
        private const string Zb013DoorName = "Inside_Door_Metal_Grille_04_L_208-95";

        private static Door Zb013Door() => UnityEngine.Object.FindObjectsOfType<Door>()
            .FirstOrDefault(d => d.name.Contains(Zb013DoorName) || d.transform.parent != null && d.transform.parent.name.Contains(Zb013DoorName));

        /// <summary>Until the power's on, ZB-013's door is locked shut (whatever state the map left it in).</summary>
        private static void LockZb013Door()
        {
            Door door = Zb013Door();
            if (door == null)
                return;
            if (door.DoorState == EDoorState.Open)
                door.SetDoorState(EDoorState.Shut, true);
            door.SetDoorState(EDoorState.Locked, true);
        }

        // ZB-013's door unlocked and opened (by name; else the locked door nearest the ZB-013 exfil)
        // Unlocked and swung open without Door.Interact: SAIN's door-sound patch on Interact expects a player doing it
        // and threw (NullReference) when the power opened it
        private static void OpenDoor(Door door)
        {
            try
            {
                door.Unlock();
                door.SetDoorState(EDoorState.Shut, true);
                door.Open();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies power: opening {door.name} failed: {e.Message}");
            }
        }

        private static void OpenZb013Door()
        {
            Door named = Zb013Door();
            if (named != null)
            {
                OpenDoor(named);
                ZombiesPlugin.Log.LogInfo($"Zombies power: ZB-013's door {named.name} unlocked and opened");
                return;
            }
            ExfiltrationPoint exfil = UnityEngine.Object.FindObjectsOfType<ExfiltrationPoint>()
                .FirstOrDefault(p => (p.Settings?.Name ?? p.name).IndexOf("ZB-013", StringComparison.OrdinalIgnoreCase) >= 0
                                     || p.name.IndexOf("ZB013", StringComparison.OrdinalIgnoreCase) >= 0);
            if (exfil == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies power: no ZB-013 exfil found for its door");
                return;
            }
            Door door = UnityEngine.Object.FindObjectsOfType<Door>()
                .Where(d => d.DoorState == EDoorState.Locked && Vector3.Distance(d.transform.position, exfil.transform.position) < 30f)
                .OrderBy(d => Vector3.Distance(d.transform.position, exfil.transform.position))
                .FirstOrDefault();
            if (door == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies power: no locked door near ZB-013");
                return;
            }
            OpenDoor(door);
            ZombiesPlugin.Log.LogInfo($"Zombies power: ZB-013's door {door.name} unlocked and opened");
        }

        private static AudioSource _source;

        private static void Play2D(AudioClip clip, float volume)
        {
            if (clip == null)
                return;
            if (_source == null)
            {
                var go = new GameObject("Zombies_PowerSound");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _source = go.AddComponent<AudioSource>();
                ZombiesHeadAudio.Setup(_source, 16);
            }
            _source.PlayOneShot(clip, volume * ZombiesSounds.Volume(ZombiesSounds.Group.Power));
        }

        private static IEnumerator LoadSounds()
        {
            string dir = Path.Combine(ZombiesPlugin.Dir, "sounds");
            _powerOn = ZombiesWav.Load(dir, "zm_power_on");
            _hum = ZombiesWav.Load(dir, "zm_machine_hum");
            _papIdle = ZombiesWav.Load(dir, "zm_pap_idle");
            yield break;
        }
    }
}
