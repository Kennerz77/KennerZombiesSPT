using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// PHD Flopper in a zombies match:
    /// - Without it, your own explosions hurt you: your grenades, the Pack-a-Punched M1911's (Sally's) shells and the Ray
    ///   Gun's bolts going off near you count as a slap (once per blast, like a zombie's). With it, they never do.
    /// - With it, your knife is explosive: a knife hit on a zombie blows up round it (every few seconds at most), hurting
    ///   every zombie close by.
    /// - With it, diving: going prone straight out of a sprint sets off the same blast in the middle of you (the original's PhD
    ///   dive, user 2026-11-10): zombies round you are hurt as by the knife's blast, you never are (no player is).
    /// </summary>
    internal static class ZombiesPhd
    {
        public const float SelfReach = 3.5f, SelfClose = 1.6f, KnifeBlastEvery = 3f, KnifeBlastRadius = 3.5f;
        private static float _lastSelf = -100f, _lastKnife = -100f;

        public static bool Has => ZombiesMatch.Active && ZombiesPerks.Has("phd");

        /// <summary>One of your own blasts went off at a place: without PHD and close enough, it slaps you.</summary>
        public static void SelfBlast(Player me, Vector3 at, string what)
        {
            if (!ZombiesMatch.Active || me == null || Has || Time.time - _lastSelf < 0.5f)
                return;
            Vector3 chest = me.Transform.position + Vector3.up * 1.1f;
            if (Vector3.Distance(at, chest) > SelfReach)
                return;
            _lastSelf = Time.time;
            // The original: right on top of your own blast it downs you without Jugger-Nog (two hits), further off it's one
            float d = Vector3.Distance(at, chest);
            int hits = d <= SelfClose ? 2 : 1;
            ZombiesPlugin.Log.LogInfo($"Zombies: hurt by your own {what} at {d:0.0} m ({hits} hit{(hits > 1 ? "s" : "")}, no PHD Flopper)");
            // The second goes in at once (a slap's own gap would swallow it); F12's god mode still stops both
            if (ZombiesDebug.NoSlaps?.Value == true)
                return;
            for (int i = 0; i < hits; i++)
                ZombiesSlaps.Hit(me, i > 0);
        }

        // ---------- The dive ----------

        public const float DiveSprintGrace = 0.6f, DiveEvery = 1.5f;
        private static float _sprintedAt = -100f, _lastDive = -100f;
        private static bool _wasProne;

        /// <summary>Every frame in a match: a sprint followed at once by prone is a dive, and with PHD it blows up.</summary>
        public static void Tick(Player me)
        {
            if (me == null)
                return;
            bool prone = me.IsInPronePose;
            if (me.Physical != null && me.Physical.Sprinting)
                _sprintedAt = Time.time;
            bool dived = prone && !_wasProne && Time.time - _sprintedAt <= DiveSprintGrace;
            _wasProne = prone;
            if (!dived || !Has || ZombiesDowned.Mine != ZombiesDowned.State.Up || Time.time - _lastDive < DiveEvery)
                return;
            _lastDive = Time.time;
            Blast(me, me.Transform.position + Vector3.up * 0.4f, me.HandsController?.Item, "dive");
        }

        /// <summary>Your knife hit a zombie: with PHD, an explosion round it now and then.</summary>
        public static void KnifeHit(Player me, Vector3 at, EFT.InventoryLogic.Item knife)
        {
            if (!Has || me == null || Time.time - _lastKnife < KnifeBlastEvery)
                return;
            _lastKnife = Time.time;
            Blast(me, at, knife, "knife");
        }

        // The PHD blast: the grenade's look and the original's grenade damage on every zombie within reach, none on players
        private static void Blast(Player me, Vector3 at, EFT.InventoryLogic.Item knife, string what)
        {
            try
            {
                Singleton<Systems.Effects.Effects>.Instance?.EmitGrenade("Grenade_new", at, Vector3.up, 0);
            }
            catch
            {
            }
            // Its boom (user, 2026-11-11: the blast was silent; the effect above has no sound of its own): the game's
            // grenade body and low thump mixed (data/sounds/zm_phd_blast.wav)
            ZombiesSounds.At("zm_phd_blast", at, 1f, 45f);
            GameWorld world = Singleton<GameWorld>.Instance;
            IObserverToPlayerBridge bridge = world?.GetAlivePlayerBridgeByProfileID(me.ProfileId);
            int hit = 0;
            foreach (Player zombie in world.AllAlivePlayersList.Where(p => ZombiesTeam.IsZombie(p)).ToList())
            {
                if (zombie.HealthController == null || !zombie.HealthController.IsAlive)
                    continue;
                Vector3 chest = zombie.Transform.position + Vector3.up * 1.1f;
                float d = Vector3.Distance(at, chest);
                if (d > KnifeBlastRadius)
                    continue;
                float bo = Mathf.Lerp(ZombiesHealth.PhdNear, ZombiesHealth.PhdFar, Mathf.InverseLerp(0.5f, KnifeBlastRadius, d));
                var info = new DamageInfo
                {
                    DamageType = EDamageType.Explosion,
                    Damage = ZombiesHealth.ExplosionEft(bo),
                    Direction = (chest - at).normalized,
                    HitPoint = chest,
                    HitNormal = (at - chest).normalized,
                    MasterOrigin = at,
                    Player = bridge,
                    Weapon = knife,
                    BodyPartColliderType = EBodyPartColliderType.RibcageUp,
                };
                ZombiesRules.HitZombie(zombie, info);
                hit++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies: PHD {what} blast, {hit} zombie(s) hit");
        }
    }
}
