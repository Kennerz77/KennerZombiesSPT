using System;
using Comfort.Common;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

namespace KennerZombies.MapLoader.Loading
{
    // Ambient sounds of SDK maps: plain AudioSources (the SDK's Ambient Sounds window, or any
    // source under "SDK_Ambience" or tagged in its name). Their object name carries tags:
    //
    //   [Outdoor] [Indoor]            the game's outdoor/indoor ambience mixers, which the game
    //                                 crossfades as the player goes in and out of buildings
    //   [Nature] [Technical]          the game's environment sound mixers (birds, machinery)
    //   [Day] [Night] [Rain] [Clear]  fade with daylight (sun visibility) and rain intensity
    //   [RainLight] [RainMedium]      play only while the rain is that intense (the game's
    //   [RainHeavy]                   RainController.IntensityType: Low, Med, High)
    //
    // Untagged sources under SDK_Ambience go to the outdoor mixer when 2D, the nature mixer when 3D.
    // Routed through the game's mixers they follow its volume settings.
    //
    // Maps can also carry the game's own AmbientAudioSystem (the SDK builds it as on Customs). The game
    // starts it; the SDK can't reference the game's mixer, so objects name their mixer group instead,
    // " @GroupName" at the end of the name, resolved here before the system's components start.
    internal static class AmbientAudio
    {
        private const string RootName = "SDK_Ambience";
        private const string MixerTag = " @";

        public static void Setup(Scene scene)
        {
            int mixers = ResolveMixerTags(scene);
            bool system = false;
            foreach (var root in scene.GetRootGameObjects())
                system |= root.GetComponentInChildren<Audio.AmbientSubsystem.AmbientAudioSystem>(true) != null;
            if (system)
                Loader.Verbose($"{scene.name}: the game's ambient audio system, {mixers} mixer group(s) assigned");

            int count = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                bool underRoot = root.name == RootName;
                foreach (var source in root.GetComponentsInChildren<AudioSource>(true))
                {
                    if (!underRoot && !HasTag(source.name))
                        continue;
                    if (source.GetComponent<AmbientSoundFader>() == null)
                    {
                        source.gameObject.AddComponent<AmbientSoundFader>();
                        count++;
                    }
                }
            }
            // the game's own ambient players (random sound events): started by the AmbientAudioSystem
            // when they're in one of its groups, by AmbientPlayerStarter otherwise
            int players = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var player in root.GetComponentsInChildren<Audio.AmbientSubsystem.BaseAmbientSoundPlayer>(true))
                    if (player.GetComponentInParent<Audio.AmbientSubsystem.AmbientSoundPlayerGroup>(true) == null && player.GetComponent<AmbientPlayerStarter>() == null)
                    {
                        player.gameObject.AddComponent<AmbientPlayerStarter>();
                        players++;
                    }
            if (count + players > 0 || system)
            {
                Loader.Verbose($"{scene.name}: {count} ambient sound(s), {players} ambient sound player(s)");
                if (UnityEngine.Object.FindObjectOfType<AmbientMixerDriver>() == null)
                    new GameObject("SDK_AmbientMixerDriver").AddComponent<AmbientMixerDriver>();
            }
        }

        // "Name @GroupName": the object's AudioSource and any empty AudioMixerGroup field of its
        // components get the game's mixer group of that name
        private static int ResolveMixerTags(Scene scene)
        {
            if (!MonoBehaviourSingleton<BetterAudio>.Instantiated || MonoBehaviourSingleton<BetterAudio>.Instance.Master == null)
                return 0;
            System.Collections.Generic.Dictionary<string, AudioMixerGroup> groups = null;
            int assigned = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    int tag = t.name.LastIndexOf(MixerTag, StringComparison.Ordinal);
                    if (tag < 0)
                        continue;
                    var groupName = t.name.Substring(tag + MixerTag.Length).Trim();
                    if (groups == null)
                    {
                        groups = new System.Collections.Generic.Dictionary<string, AudioMixerGroup>(StringComparer.OrdinalIgnoreCase);
                        foreach (var g in MonoBehaviourSingleton<BetterAudio>.Instance.Master.FindMatchingGroups(string.Empty))
                            if (!groups.ContainsKey(g.name))
                                groups[g.name] = g;
                    }
                    if (!groups.TryGetValue(groupName, out var group))
                    {
                        Loader.Log.LogWarning($"{scene.name}: '{t.name}' names mixer group '{groupName}', which the game doesn't have");
                        continue;
                    }
                    var source = t.GetComponent<AudioSource>();
                    if (source != null && source.outputAudioMixerGroup == null)
                        source.outputAudioMixerGroup = group;
                    foreach (var behaviour in t.GetComponents<MonoBehaviour>())
                        for (var type = behaviour != null ? behaviour.GetType() : null; type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                                if (field.FieldType == typeof(AudioMixerGroup) && field.GetValue(behaviour) == null)
                                    field.SetValue(behaviour, group);
                    assigned++;
                }
            return assigned;
        }

        private static bool HasTag(string name) =>
            name.IndexOf("[Outdoor]", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("[Indoor]", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("[Nature]", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("[Technical]", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static AudioMixerGroup MixerFor(string objectName, float spatialBlend)
        {
            var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            var n = objectName.ToLowerInvariant();
            return n.Contains("[indoor]") ? audio.AmbientInMixer
                : n.Contains("[outdoor]") ? audio.AmbientOutMixer
                : n.Contains("[technical]") ? audio.EnvTechnicalSoundsGroup
                : n.Contains("[nature]") ? audio.EnvNatureSoundsGroup
                : spatialBlend < 0.5f ? audio.AmbientOutMixer : audio.EnvNatureSoundsGroup;
        }
    }

    // Starts one of the game's ambient sound players (e.g. SeasonAmbientSoundPlayer: random clips from
    // a SoundBank at random intervals). On retail maps the game's AmbientAudioSystem assigns its mixer
    // and tells it the season; a season player stays silent until it knows the season.
    internal class AmbientPlayerStarter : MonoBehaviour
    {
        private bool _routed;

        private void Update()
        {
            if (!_routed && MonoBehaviourSingleton<BetterAudio>.Instantiated)
            {
                var source = GetComponent<AudioSource>();
                if (source != null && source.outputAudioMixerGroup == null)
                    source.outputAudioMixerGroup = AmbientAudio.MixerFor(name, source.spatialBlend);
                _routed = true;
            }
            var seasons = Seasons.Controller;
            if (!_routed || seasons == null)
                return;
            foreach (var dependent in GetComponents<Audio.AmbientSubsystem.ISeasonDependent>())
                dependent.SetSeasonStatus(seasons.Status);
            Destroy(this);
        }
    }

    // BetterAudio mutes the ambient mixers ("AmbientInVolume", "AmbientOutVolume" at -80 dB) when a
    // raid starts. On retail maps the game's spatial audio opens them per room the player is in,
    // blending outdoor ambience in through doors and windows (EnvironmentSoundBlendSystem). A map with
    // the SDK's spatial audio and the game's ambient system works the same way, and this steps aside
    // once the spatial audio is initialised. Without them, this opens the mixers from the map's indoor
    // triggers: the outdoor mixer outdoors, the indoor one indoors, crossfaded.
    //
    // Without the game's AmbientAudioSystem it also applies the headphones template and opens the
    // day/night mixers (the SDK's [Day]/[Night] tags fade the sources instead). With it, the system
    // does both, and this tells it which room the player is in: the game's MyPlayerRoomChangedEvent,
    // which its wind, rain and reverb follow, with a stand-in room for indoors and outdoors.
    internal class AmbientMixerDriver : MonoBehaviour
    {
        private const float FadeSpeed = 1.5f; // per second, linear volume
        private float _outdoor = -1f;
        private bool _templateApplied;
        private int _templateAttempts;
        private int _roomSent = -1; // -1 none yet, 0 outdoor, 1 indoor
        private int _nextNativeCheck;

        private void Update()
        {
            if (!MonoBehaviourSingleton<BetterAudio>.Instantiated)
                return;
            // the map's own spatial audio rooms: the game sends the real room events, and its blend system
            // (when the map has one) drives the ambient mixers
            bool native = Audio.SpatialSystem.SpatialAudioSystem.Initialized;
            if (native && Time.frameCount >= _nextNativeCheck)
            {
                _nextNativeCheck = Time.frameCount + 60;
                if (FindObjectOfType<Audio.AmbientSubsystem.EnvironmentSoundBlendSystem>() != null)
                {
                    Loader.Verbose("Ambient audio: the map's spatial audio and blend system drive the ambience");
                    Destroy(gameObject);
                    return;
                }
            }
            var audio = MonoBehaviourSingleton<BetterAudio>.Instance;
            var mixer = audio.Master;
            if (mixer == null)
                return;
            var system = MonoBehaviourSingleton<Audio.AmbientSubsystem.AmbientAudioSystem>.Instantiated
                ? MonoBehaviourSingleton<Audio.AmbientSubsystem.AmbientAudioSystem>.Instance
                : null;

            // BetterAudio applies the headphones template (which sets "AmbientVolume", the parent of
            // both ambient mixers, and the environment levels) only when the game's AmbientAudioSystem
            // reports it has started; without one it's applied here once the raid is up
            if (system == null && !_templateApplied && Singleton<EFT.GameWorld>.Instantiated && Singleton<EFT.GameWorld>.Instance.MainPlayer != null)
            {
                try
                {
                    audio.ApplyCurrentHeadphonesTemplate();
                    _templateApplied = true;
                    Loader.Verbose("Ambient audio: headphones template applied");
                }
                catch (Exception e)
                {
                    if (++_templateAttempts >= 10)
                    {
                        _templateApplied = true;
                        Loader.Log.LogWarning($"Ambient audio: could not apply the headphones template ({e.Message}); ambience may be silent");
                    }
                }
            }

            var environment = EFT.EnvironmentEffect.EnvironmentManager.Instance;
            bool outdoor = environment == null || environment.Environment == EnvironmentType.Outdoor;
            float target = outdoor ? 1f : 0f;
            _outdoor = _outdoor < 0f ? target : Mathf.MoveTowards(_outdoor, target, Time.deltaTime * FadeSpeed);

            if (!native && system != null && system.Initialized && _roomSent != (outdoor ? 0 : 1))
            {
                try
                {
                    var room = outdoor ? SdkAudioRoom.Outdoor : SdkAudioRoom.Indoor;
                    EFT.GlobalEvents.GlobalEventsController.CreateEvent<EFT.GlobalEvents.AudioEvents.MyPlayerRoomChangedEvent>()
                        .Invoke(room, room.ID, room.Type, SdkAudioRoom.Outdoor.ID, Audio.SpatialSystem.Data.EPlayerRoomInteractionState.Enter);
                }
                catch (Exception e)
                {
                    Loader.Log.LogWarning($"Ambient audio: room change failed: {e.Message}");
                }
                _roomSent = outdoor ? 0 : 1;
            }

            // written every frame: the game resets them when the raid starts
            mixer.SetFloat("AmbientOutVolume", ToDb(Mathf.Lerp(0.3f, 1f, _outdoor))); // outside is still faintly heard indoors
            mixer.SetFloat("AmbientInVolume", ToDb(1f - _outdoor));
            if (system == null)
            {
                mixer.SetFloat("AmbientOutDayVolume", 0f);
                mixer.SetFloat("AmbientOutNightVolume", 0f);
                mixer.SetFloat("AmbientInDayVolume", 0f);
                mixer.SetFloat("AmbientInNightVolume", 0f);
            }
        }

        private static float ToDb(float volume) => Mathf.Log10(Mathf.Clamp(volume, 0.0001f, 1f)) * 20f;
    }

    // The room the game's ambient system is told the player is in: SDK maps have no spatial audio
    // rooms, only indoor triggers, so there is one outdoor and one indoor room. The masks are the keys
    // the game's mixer data uses (Outdoor, IndoorGeneric).
    internal sealed class SdkAudioRoom : Audio.SpatialSystem.ISpatialAudioRoom
    {
        public static readonly SdkAudioRoom Outdoor = new SdkAudioRoom(-100, Audio.SpatialSystem.EAudioRoomTypeMask.Outdoor, "SDK outdoor");
        public static readonly SdkAudioRoom Indoor = new SdkAudioRoom(-101, Audio.SpatialSystem.EAudioRoomTypeMask.IndoorGeneric, "SDK indoor");

        private static readonly System.Collections.Generic.List<ISpatialPortal> NoPortals = new System.Collections.Generic.List<ISpatialPortal>();

        private SdkAudioRoom(short id, Audio.SpatialSystem.EAudioRoomTypeMask type, string name)
        {
            ID = id;
            Type = type;
            Name = name;
        }

        public short ID { get; }
        public Audio.SpatialSystem.EAudioRoomTypeMask Type { get; }
        public string Name { get; }
        public BoxCollider[] Colliders { get; } = new BoxCollider[0];
        public bool IsOutdoor => this == Outdoor;
        public bool IsIsolated => false;
        public float RoomSize => 0f;
        public Audio.AmbientSubsystem.RoomAmbientData RoomAmbientData { get; } = new Audio.AmbientSubsystem.RoomAmbientData();
        public Bounds Bounds => default;
        public bool IsValid => true;
        public bool CheckIsolationRelation(Audio.SpatialSystem.ISpatialAudioRoom roomToCheck) => false;
        public void PlayRoomToneSound() { }
        public void StopRoomToneSound() { }
        public System.Collections.Generic.List<ISpatialPortal> GetPortals() => NoPortals;
    }

    internal class AmbientSoundFader : MonoBehaviour
    {
        private AudioSource _source;
        private float _volume;
        private bool _day, _night, _rain, _clear, _routed;
        private RainController.ERainIntensity? _rainBand; // [RainLight] [RainMedium] [RainHeavy]

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            _volume = _source.volume;
            var n = name.ToLowerInvariant();
            _day = n.Contains("[day]");
            _night = n.Contains("[night]");
            _rain = n.Contains("[rain]");
            _clear = n.Contains("[clear]");
            _rainBand = n.Contains("[rainlight]") ? RainController.ERainIntensity.Low
                : n.Contains("[rainmedium]") ? RainController.ERainIntensity.Med
                : n.Contains("[rainheavy]") ? RainController.ERainIntensity.High
                : (RainController.ERainIntensity?)null;
            _source.volume = 0f; // faded in once the time of day and weather are known
        }

        private void Update()
        {
            if (!_routed)
                Route();

            float factor = 1f;
            if (_day || _night)
            {
                float sun = TODSkyProvider.IsAvailable && TODSkyProvider.Instance != null ? TODSkyProvider.Instance.SunVisibility : 1f;
                factor *= _day ? sun : 1f - sun;
            }
            if (_rain || _clear)
            {
                float rain = Mathf.Clamp01(RainController.Intensity);
                factor *= _rain ? rain : 1f - rain;
            }
            // one loop per rain intensity: crossfades as the rain changes
            if (_rainBand.HasValue)
                factor *= RainController.IntensityType == _rainBand.Value ? 1f : 0f;
            _source.volume = Mathf.MoveTowards(_source.volume, _volume * factor, Time.deltaTime * 0.5f);
        }

        private void Route()
        {
            if (!MonoBehaviourSingleton<BetterAudio>.Instantiated)
                return;
            _routed = true;
            if (_source.outputAudioMixerGroup != null)
                return;
            _source.outputAudioMixerGroup = AmbientAudio.MixerFor(name, _source.spatialBlend);
        }
    }
}
