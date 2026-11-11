using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's knife (the user, 2026-11-08: "instant, swipes the screen, good enough range to avoid slaps"): the melee key
    /// lands its hit the moment it's pressed, on the nearest zombie in a cone in front out to <see cref="Range"/>, with
    /// The original's knife damage and points (a Melee hit through the same damage rules as any other). EFT's quick knife still
    /// plays (sped up) as the swing you see, and its own hit, which comes later in the swing, is dropped
    /// (<see cref="DropEftHit"/>) so nothing counts twice. F12 "18. Zombie health and damage" > "Knife: ...".
    /// </summary>
    internal static class ZombiesSwipe
    {
        private static ConfigEntry<float> _range, _angle;
        private static bool _ours;
        private static float _blockEftUntil;

        public static float Range => _range?.Value ?? 2.4f;

        public static void Bind(ConfigFile config, string section)
        {
            _range = config.Bind(section, "Knife: instant reach (m)", 2.4f, new ConfigDescription(
                "The knife: the hit lands the moment you press melee, on the nearest zombie this close in front of you (a zombie's slap reaches 1.3 m).",
                new AcceptableValueRange<float>(1f, 4f)));
            _angle = config.Bind(section, "Knife: swipe width (degrees)", 70f, new ConfigDescription(
                "How wide the knife's swipe is, across the middle of the screen.", new AcceptableValueRange<float>(20f, 150f)));
            _animSpeed = config.Bind(section, "Knife: swing animation speed", 1.7f, new ConfigDescription(
                "How fast the knife's swing plays (1 = EFT's own speed). Higher is a quicker slash.", new AcceptableValueRange<float>(0.8f, 3f)));
            _every = config.Bind(section, "Knife: time between swings (s)", 0.85f, new ConfigDescription(
                "The shortest time from one knife swing to the next (holding or spamming melee can't go faster).", new AcceptableValueRange<float>(0.3f, 2f)));
            Headset = config.Bind(section, "Headset sound (everyone)", HeadsetSound.None, new ConfigDescription(
                "What everyone hears through in a zombies match, whatever headset they wear (it's only a look): None = no headset " +
                "effect at all (the game's plain hearing), ComTac = the ComTac VI's. Next match."));
        }

        public enum HeadsetSound { None, ComTac }

        public static ConfigEntry<HeadsetSound> Headset;
        private static ConfigEntry<float> _animSpeed, _every;

        /// <summary>The knife's swing animation speed (ZombiesKnife).</summary>
        public static float AnimSpeed => _animSpeed?.Value ?? 1.7f;

        // The hit lands as the blade crosses the screen: 0.12 s at the old 2.8 speed, later as the swing slows
        private static float HitAfter => 0.12f * 2.8f / Mathf.Max(0.5f, AnimSpeed);
        private static float SwingEvery => _every?.Value ?? 0.85f;

        // One swing per swing animation (spamming F hit faster than the knife was seen to move, the user 2026-11-08);
        // the hit lands as the blade crosses the screen, a beat after the key
        private static float _nextSwing;

        /// <summary>The melee key: false while a swing is still going (no new stab sent then).</summary>
        public static bool TrySwing(Player me)
        {
            if (!ZombiesMatch.Active || me == null || ZombiesDowned.Mine != ZombiesDowned.State.Up)
                return true;
            // One swing at a time: the next only once the last one's animation is over (user, 2026-11-09: knifing
            // could be spammed with no animation)
            if (Time.time < _nextSwing || _pending || ZombiesKnife.Busy
                || me.HandsController != null && me.HandsController.GetType().Name.Contains("QuickKnife"))
                return false;
            _nextSwing = Time.time + SwingEvery;
            _pending = true;
            _swingId++;
            ZombiesPlugin.Instance.StartCoroutine(HitSoon(me, _swingId));
            return true;
        }

        // The hit waits for the blade: EFT's knife animation fires its own "hit now" event as the blade crosses the screen
        // (BaseKnifeController.IEventsConsumerOnFiringBullet). The old fixed 0.2 s landed the damage before the swing was
        // seen (the quick knife first draws), user 2026-11-09. Fallback: no event in time -> the hit lands anyway.
        private static bool _pending;
        private static int _swingId;

        private static System.Collections.IEnumerator HitSoon(Player me, int id)
        {
            yield return new WaitForSeconds(0.45f * 1.7f / Mathf.Max(0.8f, AnimSpeed));
            // With Fika the knife can come into the hands late (the host answers first): up to 1.5 s more for it
            float until = Time.time + 1.5f;
            while (_pending && id == _swingId && !ZombiesKnife.SwingSeen && Time.time < until)
                yield return null;
            if (_pending && id == _swingId)
            {
                _pending = false;
                // Only a swing that's really happening hits (EFT's knife in the hands): a swing that never started
                // lands nothing (it killed zombies with no animation at all)
                if (ZombiesKnife.SwingSeen)
                {
                    ZombiesDebug.Log("knife: no swing event from EFT's knife in time, hit landed anyway");
                    Swing(me);
                }
                else
                    ZombiesDebug.Log("knife: the swing never started, no hit");
            }
        }

        /// <summary>EFT's knife animation event for the blade crossing: our swipe's hit lands now.</summary>
        public static void Patch(HarmonyLib.Harmony harmony)
        {
            var m = HarmonyLib.AccessTools.Method(typeof(Player.BaseKnifeController), nameof(Player.BaseKnifeController.IEventsConsumerOnFiringBullet));
            if (m == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies knife: EFT's swing event wasn't found; the hit lands on a timer");
                return;
            }
            harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ZombiesSwipe), nameof(BladeCrossed)));
        }

        private static void BladeCrossed(Player.BaseKnifeController __instance)
        {
            if (!_pending || !ZombiesMatch.Active)
                return;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null || !ReferenceEquals(me.HandsController, __instance))
                return;
            _pending = false;
            Swing(me);
        }

        /// <summary>The swipe's hit (ZombiesSwipe.TrySwing times it).</summary>
        private static void Swing(Player me)
        {
            if (!ZombiesMatch.Active || me == null || ZombiesDowned.Mine != ZombiesDowned.State.Up)
                return;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            // EFT's own knife hit, later in the swing, is dropped either way (hit or miss: the original's swipe is the one hit)
            _blockEftUntil = Time.time + 1.2f;
            Transform eye = me.CameraPosition != null ? me.CameraPosition : me.Transform.Original;
            Vector3 from = eye.position, look = eye.forward;
            Vector3 flatLook = new Vector3(look.x, 0f, look.z).normalized;
            float half = (_angle?.Value ?? 70f) * 0.5f;
            Player best = null;
            float bestD = float.MaxValue;
            foreach (Player z in world.AllAlivePlayersList.Where(p => p != null && ZombiesTeam.IsZombie(p) && p.HealthController != null && p.HealthController.IsAlive))
            {
                Vector3 chest = z.Transform.position + Vector3.up * 1.1f;
                Vector3 to = chest - from;
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                float d = flat.magnitude;
                if (d > Range || d >= bestD || Mathf.Abs(to.y) > 1.6f)
                    continue;
                if (d > 0.4f && Vector3.Angle(flatLook, flat) > half)
                    continue;
                if (Physics.Linecast(from, chest, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                    continue;
                best = z;
                bestD = d;
            }
            if (best == null)
            {
                ZombiesDebug.Log("knife: swipe, nothing in reach");
                return;
            }
            Vector3 at = best.Transform.position + Vector3.up * 1.1f;
            var info = new DamageInfo
            {
                DamageType = EDamageType.Melee,
                Damage = 50f, // The original's knife damage comes from ZombiesHealth (150); this is only EFT's carrier value
                Direction = (at - from).normalized,
                HitPoint = at,
                HitNormal = (from - at).normalized,
                MasterOrigin = from,
                Player = world.GetAlivePlayerBridgeByProfileID(me.ProfileId),
                Weapon = me.HandsController?.Item,
                BodyPartColliderType = EBodyPartColliderType.RibcageUp,
            };
            _ours = true;
            try
            {
                // A Fika client's zombies are the host's: its copy only records a direct hit (ObservedPlayer
                // .ApplyDamageInfo), the shot path sends it to the host (the client's knife did nothing: user, 2026-11-09)
                ZombiesRules.HitZombie(best, info);
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies knife: the swipe's hit failed ({e.Message})");
            }
            finally
            {
                _ours = false;
            }
            ZombiesDebug.Log($"knife: swipe hit a zombie {bestD:0.0} m away");
        }

        /// <summary>EFT's own knife hit on a zombie, from you, just after our swipe: dropped (ours already landed).</summary>
        public static bool DropEftHit(DamageInfo hit, IPlayer from) =>
            !_ours && hit.DamageType == EDamageType.Melee && from != null && from.IsYourPlayer && Time.time < _blockEftUntil;
    }
}
