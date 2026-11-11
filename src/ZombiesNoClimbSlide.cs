using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The map's no-climb boxes (KZ_NoClimb/NoClimb_*: invisible solid blocks standing on vehicles, trailers, tables,
    /// tyre stacks...) stop anyone climbing up, but a player dropping onto one from a floor above landed on its top and
    /// stood in mid-air (the user, 2026-11-08: the cargo trailer under the top floor). Standing on one, you're slid off
    /// its nearest side (away from its middle) until you're clear, and drop to the floor beside it. Your own player only.
    /// </summary>
    internal static class ZombiesNoClimbSlide
    {
        private const float Speed = 4f;
        private static int _mask = -1;
        private static bool _sliding;

        public static void Tick(Player me)
        {
            if (me == null || !ZombiesMatch.Active)
                return;
            if (_mask < 0)
                _mask = LayerMask.GetMask("LowPolyCollider", "HighPolyCollider", "Default");
            Vector3 at = me.Transform.position;
            if (!Physics.Raycast(at + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 0.6f, _mask, QueryTriggerInteraction.Ignore)
                || hit.collider == null || !hit.collider.name.StartsWith("NoClimb_"))
            {
                _sliding = false;
                return;
            }
            Vector3 away = at - hit.collider.bounds.center;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f)
                away = me.Transform.forward;
            away.y = 0f;
            if (!_sliding)
            {
                _sliding = true;
                ZombiesDebug.Log($"no-climb: you landed on {hit.collider.name}, slid off it");
            }
            me.Teleport(at + away.normalized * Speed * Time.deltaTime, false);
        }
    }
}
