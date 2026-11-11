using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The melee key's stab (EFT's quick knife) played faster in a zombies match: the hands' animator runs at
    /// <see cref="Speed"/> while the quick knife is in the hands, so the swing, its hit (an animation event) and the
    /// gun coming back all come sooner. Put back to its own speed the moment the stab is over.
    /// </summary>
    internal static class ZombiesKnife
    {
        internal const float BoltSpeed = 1.15f;
        // The knife swing's speed: F12 "Knife: swing animation speed" (2.8 looked too quick, the user 2026-11-08)
        private static float Speed => ZombiesSwipe.AnimSpeed;
        private static Animator _sped, _bolt;
        private static float _was = 1f, _boltWas = 1f;

        // ---------- A swing that never starts (user, 2026-11-09, Fika headless) ----------
        // The melee key sent EFT's quick knife and the hands never answered: no knife came, the gun stayed out but
        // wouldn't fire, melee or throw again (our own hit still landed, so zombies died). 2 s with no knife in the
        // hands after asking: the hands put away and the gun drawn again (as for a stuck reload)
        private static float _askedAt = -1f;
        private static object _askedHands;
        private static bool _sawKnife;

        /// <summary>The melee key just sent EFT's quick knife.</summary>
        public static void Asked(Player me)
        {
            _askedAt = Time.time;
            _askedHands = me?.HandsController;
            _sawKnife = false;
        }

        /// <summary>A swing asked for and not over yet (its knife not come and gone): no new one (no spam with no anim).</summary>
        public static bool Busy => _askedAt >= 0f;

        /// <summary>EFT's knife is (or was) in the hands for the swing asked for: the swing is really happening.</summary>
        public static bool SwingSeen => _sawKnife;

        private static void WatchSwing(Player me, bool stabbing)
        {
            if (_askedAt < 0f)
                return;
            if (stabbing)
                _sawKnife = true;
            // The knife came and went: the swing's animation is over, the next one may go
            if (_sawKnife && !stabbing)
            {
                _askedAt = -1f;
                _askedHands = null;
                return;
            }
            // A knife that came and never left (4 s): the key's freed anyway, never locked for good
            if (_sawKnife)
            {
                if (Time.time - _askedAt > 4f)
                {
                    _askedAt = -1f;
                    _askedHands = null;
                }
                return;
            }
            if (Time.time - _askedAt < 1f)
                return;
            bool stuck = !_sawKnife && me != null && ReferenceEquals(me.HandsController, _askedHands);
            _askedAt = -1f;
            _askedHands = null;
            if (!stuck || !ZombiesMatch.Active)
                return;
            // EFT ignores a quick knife now and then (mid shot, mid swap): one that didn't start just frees the key.
            // Two in a row within 10 s is the real hang (the hands not answering): put away and drawn again
            bool again = Time.time - _lastNoStart < 10f;
            _lastNoStart = Time.time;
            if (!again)
            {
                ZombiesDebug.Log("knife: EFT didn't start that swing (busy hands?), key free again");
                return;
            }
            _lastNoStart = -100f;
            ZombiesPlugin.Log.LogWarning("Zombies knife: two swings in a row never started (hands not answering): gun put away and drawn again");
            ZombiesPlugin.Instance.StartCoroutine(ZombiesReload.Redraw(me));
        }

        private static float _lastNoStart = -100f;

        public static void Tick(Player me)
        {
            var hands = me?.HandsController;
            // A bolt-action gun in the hands: its whole hands animation (the bolt above all) a touch faster
            bool bolt = ZombiesMatch.Active && hands?.Item is EFT.InventoryLogic.Weapon w && w.BoltAction && hands.ControllerGameObject != null;
            Animator boltAnim = bolt ? hands.ControllerGameObject.GetComponentInChildren<Animator>() : null;
            if (boltAnim != _bolt)
            {
                if (_bolt != null)
                    _bolt.speed = _boltWas;
                _bolt = boltAnim;
                if (_bolt != null)
                {
                    _boltWas = _bolt.speed;
                    _bolt.speed = BoltSpeed;
                }
            }
            bool stabbing = ZombiesMatch.Active && hands != null && (hands.GetType().Name.Contains("QuickKnife") || hands.GetType().Name.Contains("QuickGrenade"));
            WatchSwing(me, stabbing);
            if (stabbing && _sped == null && hands.ControllerGameObject != null)
            {
                _sped = hands.ControllerGameObject.GetComponentInChildren<Animator>();
                if (_sped != null)
                {
                    _was = _sped.speed;
                    _sped.speed = Speed;
                }
            }
            else if (!stabbing && _sped != null)
            {
                _sped.speed = _was;
                _sped = null;
            }
        }
    }
}
