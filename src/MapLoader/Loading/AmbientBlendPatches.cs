using System.Reflection;
using Audio.AmbientSubsystem;
using Audio.Extensions;
using Audio.SpatialSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // The game's EnvironmentSoundBlendSystem sets the outdoor ambience straight to full the moment the player
    // is in no indoor room, and on coming back in starts its fade down from full. Retail rooms are hand-placed
    // and seamless; generated ones can leave a gap (a doorway, a stair landing) where the player is outdoors
    // for a step, and the ambience jumps up and fades back. On custom maps the switch to outdoor waits a
    // moment and is dropped if the player is back in a room by then.
    internal class AmbientBlendExitDelayPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EnvironmentSoundBlendSystem), nameof(EnvironmentSoundBlendSystem.OnPlayerRoomChanged),
                new[] { typeof(ISpatialAudioRoom), typeof(EAudioRoomTypeMask), typeof(int) });

        [PatchPrefix]
        private static bool Prefix(EnvironmentSoundBlendSystem __instance, ISpatialAudioRoom __0, EAudioRoomTypeMask __1, int __2, EAudioRoomTypeMask ____currentRoomType)
        {
            if (!MapRegistry.IsCustomMapActive())
                return true;
            var delay = __instance.GetComponent<RoomExitDelay>() ?? __instance.gameObject.AddComponent<RoomExitDelay>();
            if (delay.Replaying)
                return true;
            if (____currentRoomType.IsIndoor() && __1.IsOutdoor())
            {
                delay.Defer(__0, __1, __2);
                return false;
            }
            delay.Cancel();
            return true;
        }
    }

    internal class RoomExitDelay : MonoBehaviour
    {
        private const float Delay = 0.35f; // a step through a gap between rooms is shorter

        public bool Replaying { get; private set; }
        private bool _pending;
        private float _due;
        private ISpatialAudioRoom _room;
        private EAudioRoomTypeMask _type;
        private int _outdoorRoomId;

        public void Defer(ISpatialAudioRoom room, EAudioRoomTypeMask type, int outdoorRoomId)
        {
            if (!_pending)
                _due = Time.time + Delay;
            _pending = true;
            _room = room;
            _type = type;
            _outdoorRoomId = outdoorRoomId;
        }

        public void Cancel() => _pending = false;

        private void Update()
        {
            if (!_pending || Time.time < _due)
                return;
            _pending = false;
            Replaying = true;
            try
            {
                GetComponent<EnvironmentSoundBlendSystem>().OnPlayerRoomChanged(_room, _type, _outdoorRoomId);
            }
            finally
            {
                Replaying = false;
            }
        }
    }
}
