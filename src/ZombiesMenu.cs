using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Settings;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace KennerZombies
{
    /// <summary>
    /// The menu while the map select's game mode is Zombies: switching plays the original's enter sting (zm_enter), the game's menu
    /// music fades out, and when the sting has played through, Damned 2 loops (cut to loop at 3:12; the first switch of
    /// a session rarely plays Damned (rare) once first). Switching back plays the exit sting (zm_exit), Damned fades out,
    /// and the game's own menu music comes back once the sting is done. Switching again mid-sting cuts it for the other
    /// one. The map picture takes a slight red tint in zombies mode. In a raid or the hideout all of it stands down.
    /// </summary>
    internal class ZombiesMenu : MonoBehaviour
    {
        private const float RareChance = 0.1f;
        private const float FadeOutTime = 0.6f, TintTime = 0.5f;
        // The music after a sting comes in this long before the sting ends (F12), fading up while the sting fades away
        private const float InRaidFade = 2.5f;
        private static BepInEx.Configuration.ConfigEntry<float> _overlap;
        private static BepInEx.Configuration.ConfigEntry<bool> _together;
        private static float StingOverlap => _overlap?.Value ?? 2.5f;
        private static readonly Color ZombiesTint = new Color(1f, 0.68f, 0.68f);
        // GUISounds' menu music player (it picks a random track from Audio/Music and plays the next when one ends)
        private static readonly FieldInfo MenuMusicField = AccessTools.Field(typeof(GUISounds), "audioSource_3");

        private static ZombiesMenu _instance;
        private static BepInEx.Configuration.ConfigEntry<float> _musicVolume, _stingVolume;

        public static void Bind(BepInEx.Configuration.ConfigFile config, string section)
        {
            ZombiesMenuBackground.Bind(config, section);
            // In the music section with the rest of the songs (the user, 2026-11-08)
            _musicVolume = config.Bind(ZombiesSounds.MusicSection, "Main menu music", 1f, new BepInEx.Configuration.ConfigDescription(
                "Damned 2 (and the rare one) in the menu in zombies mode, on top of the game's music volume.",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 1f),
                new ZombiesPlugin.ConfigurationManagerAttributes { Order = 91 }));
            _stingVolume = config.Bind(section, "Enter / exit volume", 1f, new BepInEx.Configuration.ConfigDescription(
                "The sound switching into and out of zombies mode, on top of the game's music volume.",
                new BepInEx.Configuration.AcceptableValueRange<float>(0f, 1f)));
            _together = config.Bind(section, "Music starts with the sound", true, new BepInEx.Configuration.ConfigDescription(
                "The music starts the moment you switch, fading in under the enter / exit sound (which plays at full). Off: the music comes in near the sound's end."));
            _overlap = config.Bind(section, "Music comes in early (s)", 2.5f, new BepInEx.Configuration.ConfigDescription(
                "How long before the enter / exit sound ends the next music starts, crossfading with it over that time.",
                new BepInEx.Configuration.AcceptableValueRange<float>(0.5f, 4f)));
        }

        private AudioClip _enter, _exit, _damned, _rare;
        private AudioSource _sting, _music;
        private bool _zombies, _waiting, _rareRolled;
        private AudioSource _menu;
        private float _menuFactor = 1f;
        private float _menuVolume = -1f; // the menu music's own volume while we hold it down, else -1
        private Image _map;
        private float _tint;

        /// <summary>The map select's game mode was switched.</summary>
        public static void ModeChanged(bool zombies)
        {
            if (_instance != null)
                _instance.Switch(zombies);
        }

        /// <summary>The map select's map picture, to tint (shown at once in the current mode's colour).</summary>
        public static void MapPicture(Image map)
        {
            if (_instance == null || map == null)
                return;
            _instance._map = map;
            map.color = Color.Lerp(Color.white, ZombiesTint, _instance._tint);
        }

        private void Awake()
        {
            _instance = this;
            _sting = MakeSource();
            _music = MakeSource();
        }

        private AudioSource MakeSource()
        {
            AudioSource s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            s.ignoreListenerPause = true;
            return s;
        }

        private IEnumerator Start()
        {
            yield return Load("zm_enter", c => _enter = c);
            yield return Load("zm_exit", c => _exit = c);
            yield return Load("damned2", c => _damned = c);
            yield return Load("damned_rare", c => _rare = c);
        }

        private static IEnumerator Load(string name, Action<AudioClip> done)
        {
            AudioClip clip = ZombiesWav.Load(Path.Combine(ZombiesPlugin.Dir, "sounds"), name);
            if (clip != null)
                done(clip);
            yield break;
        }

        private void Switch(bool zombies)
        {
            if (zombies == _zombies)
                return;
            _zombies = zombies;
            // A sting still playing is cut for the other one
            _sting.Stop();
            _sting.clip = zombies ? ZombiesWav.Fresh(ref _enter) : ZombiesWav.Fresh(ref _exit);
            _stingFade = 1f;
            // Together: no waiting for the sound, the music starts now (its fade-in under the sound)
            _waiting = _sting.clip != null && _together?.Value != true;
            if (_sting.clip != null)
                _sting.Play();
        }

        // The zombies settings (controls) page, over the zombies menu (the menu and lobby are ZombiesFrontEnd's uGUI)
        private void OnGUI()
        {
            ZombiesControls.DrawPage();
        }

        private void Update()
        {
            ZombiesParty.Tick();
            ZombiesParty.Apply();
            // The zombies music carries on while the raid loads; it stands down once the player is in
            bool inGame = ZombiesPlugin.InRealRaid || ZombiesPlugin.InHideoutNow;
            // A zombies map built (the comic and its loading music up): Damned stops dead, the loading music is all you hear
            if (ZombiesMatch.Active)
            {
                if (_sting.isPlaying)
                    _sting.Stop();
                if (_music.isPlaying)
                    _music.Stop();
                _music.clip = null;
                _musicFade = 0f;
                _waiting = false;
            }
            if (inGame || ZombiesMatch.Active)
            {
                StandDown();
                return;
            }
            // Out of a zombies match, until the report shows: nothing plays (Damned had restarted again and again
            // behind the black screen as EFT reset the audio, and EFT's own menu music came back at full for a moment,
            // the user 2026-11-08). EFT's menu music is held at nothing, so the zombies menu starts silent on it.
            // Once the report itself is up, Damned plays under it (the user, 2026-11-08: not the raid's end music)
            if (ZombiesGameOver.Black || ZombiesReport.Waiting && !ZombiesReport.FreshOnScreen)
            {
                if (_sting.isPlaying)
                    _sting.Stop();
                if (_music.isPlaying)
                    _music.Stop();
                _music.clip = null;
                _musicFade = 0f;
                _waiting = false;
                if (_menu == null && Singleton<GUISounds>.Instantiated && MenuMusicField != null)
                    _menu = MenuMusicField.GetValue(Singleton<GUISounds>.Instance) as AudioSource;
                _menuFactor = 0f;
                if (_menu != null)
                {
                    if (_menuVolume < 0f)
                        _menuVolume = _menu.volume;
                    _menu.volume = 0f;
                    Mute(true);
                }
                ApplyVolume();
                return;
            }
            if (_waiting && (!_sting.isPlaying || _sting.time >= _sting.clip.length - StingOverlap))
                _waiting = false;
            if (!_waiting && _sting.isPlaying && _together?.Value != true)
                _stingFade = Mathf.MoveTowards(_stingFade, 0f, Time.unscaledDeltaTime / StingOverlap);

            // The game's menu music: down while zombies or a sting plays, back once the exit sting is done
            if (_menu == null && Singleton<GUISounds>.Instantiated && MenuMusicField != null)
            {
                _menu = MenuMusicField.GetValue(Singleton<GUISounds>.Instance) as AudioSource;
            }
            float menuTarget = _zombies || _waiting ? 0f : 1f;
            _menuFactor = Mathf.MoveTowards(_menuFactor, menuTarget, Time.unscaledDeltaTime / (menuTarget < _menuFactor ? FadeOutTime : StingOverlap));
            if (_menu != null)
            {
                if (_menuFactor < 1f)
                {
                    if (_menuVolume < 0f)
                        _menuVolume = _menu.volume;
                    _menu.volume = _menuVolume * _menuFactor;
                    // Muted once fully down: EFT's Character screen fades its music back up with a tween (DOFade to 1
                    // over 2 s) that beat our per-frame volume and brought the normal menu music back under Damned
                    // (user, 2026-11-09). A tween only touches the volume, never the mute.
                    Mute(_menuFactor < 0.02f);
                }
                else
                    ReleaseMenu();
            }

            // Damned: starts as the enter sting ends, fades out on the way back to normal
            if (_zombies && !_waiting)
            {
                _musicFade = Mathf.MoveTowards(_musicFade, 1f, Time.unscaledDeltaTime / StingOverlap);
                if (!_music.isPlaying)
                {
                    if (_music.clip != null && _music.clip == ZombiesWav.Fresh(ref _rare))
                        Play(_damned, true, false);
                    else
                        StartMusic();
                }
            }
            else if (_music.isPlaying)
            {
                _musicFade = Mathf.MoveTowards(_musicFade, 0f, Time.unscaledDeltaTime / FadeOutTime);
                if (_musicFade <= 0f)
                {
                    _music.Stop();
                    _music.clip = null;
                }
            }

            _tint = Mathf.MoveTowards(_tint, _zombies ? 1f : 0f, Time.unscaledDeltaTime / TintTime);
            if (_map != null)
                _map.color = Color.Lerp(Color.white, ZombiesTint, _tint);
            ApplyVolume();
        }

        // Our sounds play outside the game's music channel (EFT mutes that while a raid loads, and Damned carries on
        // through the loading), at the music volume the player set
        private void ApplyVolume()
        {
            if (Time.unscaledTime >= _nextLevel)
            {
                _nextLevel = Time.unscaledTime + 1f;
                try
                {
                    int db = Singleton<SettingsManager>.Instance.Sound.Settings.MusicVolumeValue;
                    _level = db <= -79 ? 0f : Mathf.Pow(10f, db / 20f);
                }
                catch
                {
                    _level = 1f;
                }
            }
            _music.volume = _musicFade * _level * (_musicVolume?.Value ?? 1f);
            _sting.volume = _stingFade * _level * (_stingVolume?.Value ?? 1f);
        }

        private float _musicFade, _stingFade = 1f, _level = 1f, _nextLevel;

        // The first time a session goes zombies, now and then the rare one plays once before Damned 2 takes over
        private void StartMusic()
        {
            if (!_rareRolled && _rare != null && _damned != null)
            {
                _rareRolled = true;
                if (UnityEngine.Random.value < RareChance)
                {
                    Play(_rare, false);
                    return;
                }
            }
            _rareRolled = true;
            Play(_damned, true);
        }

        private void Play(AudioClip clip, bool loop, bool fadeIn = true)
        {
            clip = ZombiesWav.Fresh(clip);
            if (clip == null)
                return;
            _music.clip = clip;
            _music.loop = loop;
            _musicFade = fadeIn ? 0f : 1f;
            _music.Play();
        }

        // In the raid (or the hideout): Damned fades away as the player is in, and the game's music is its own again
        private void StandDown()
        {
            if (_sting.isPlaying)
                _sting.Stop();
            _waiting = false;
            if (_music.isPlaying)
            {
                _musicFade = Mathf.MoveTowards(_musicFade, 0f, Time.unscaledDeltaTime / InRaidFade);
                if (_musicFade <= 0f)
                {
                    _music.Stop();
                    _music.clip = null;
                }
            }
            _menuFactor = 1f;
            ReleaseMenu();
            ApplyVolume();
        }

        private void ReleaseMenu()
        {
            if (_menu != null && _menuVolume >= 0f)
                _menu.volume = _menuVolume;
            _menuVolume = -1f;
            Mute(false);
        }

        // Only ever undoes our own mute (EFT never mutes this source itself)
        private bool _muted;

        private void Mute(bool on)
        {
            if (_menu == null || on == _muted)
                return;
            _muted = on;
            _menu.mute = on;
        }
    }
}
