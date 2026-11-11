using System.Collections;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's grenade on the grenade key, in a zombies match:
    /// - A tap: EFT's own quick throw (the grenade out, pin pulled and thrown in one go, where you look), its fuse the
    ///   full 5 s from the throw.
    /// - Held (past <see cref="HoldAfter"/>): the grenade comes out quick and the pin is pulled at once (EFT's
    ///   left-click throw, PullRingForHighThrow), then it's held back ready to throw while the key is down, cooking: the
    ///   crosshair flashes once a second (<see cref="CookStarted"/>). Let go and it's thrown with what's left of the 5 s;
    ///   held the whole 5 s it goes off in your hand and you go down (PHD Flopper saves you). The gun you had comes back.
    /// The draw and pin pull of a held one run faster (<see cref="Fast"/>).
    /// </summary>
    internal static class ZombiesQuickGrenade
    {
        private const float Fast = 2.2f, ReadyWithin = 3f, BackWithin = 4f, HoldAfter = 0.15f;
        private static bool _busy, _cooking;

        // When our grenade's pin came out (the fuse burns from then), for the throw's fuse (ZombiesGrenades.ShortFuse)
        private static float _pinAt = -1f;

        /// <summary>How long the grenade just thrown cooked in the hand (0 for one we didn't pull); used once.</summary>
        public static float TakeCooked()
        {
            float cooked = _pinAt < 0f ? 0f : Time.time - _pinAt;
            _pinAt = -1f;
            return cooked;
        }

        /// <summary>When the held grenade's pin came out (the crosshair flashes each second from then), or -1.</summary>
        public static float CookStarted => _cooking && _pinAt >= 0f ? _pinAt : -1f;

        /// <summary>The grenade key went down.</summary>
        /// <summary>Lethal: the M67 (frag). Tactical: anything else thrown (smoke, flash; the monkey bomb to come).</summary>
        public static void Press(Player me, bool lethal)
        {
            if (_busy || me?.InventoryController == null || me.HandsController == null)
                return;
            if (me.HandsController.InCanNotBeInterruptedOperation())
                return;
            ThrowWeap grenade = me.InventoryController.Inventory.Equipment.GetAllItems().OfType<ThrowWeap>()
                .Where(g => g != me.HandsController.Item && (g.TemplateId.ToString() == ZombiesGrenades.M67) == lethal)
                .FirstOrDefault();
            if (grenade == null)
            {
                ZombiesDebug.Log($"grenade: no {(lethal ? "lethal" : "tactical")} to throw");
                return;
            }
            ZombiesPlugin.Instance.StartCoroutine(Decide(me, grenade, ZombiesControls.Key(lethal ? ZombiesControls.Act.Grenade : ZombiesControls.Act.Tactical)));
        }

        /// <summary>The draw speed while one of ours comes out (ZombiesPerks.HandsSpeed multiplies it in).</summary>
        public static float DrawBoost => _busy && _cooking ? Fast : 1f;

        // A tap or a hold: let go quickly and it's EFT's quick throw, else the cook
        private static IEnumerator Decide(Player me, ThrowWeap grenade, KeyCode key)
        {
            _busy = true;
            float down = Time.time;
            // The monkey always comes out and is wound : a tap throws it once the crank's done (EFT's quick throw
            // had no draw at all, straight to the throw, the user 2026-11-08)
            bool monkeyTap = ZombiesMonkey.Is(grenade);
            while (!monkeyTap && ZombiesControls.IsDown(key) && Time.time - down < HoldAfter)
                yield return null;
            if (!monkeyTap && !ZombiesControls.IsDown(key))
            {
                _pinAt = -1f;
                ZombiesDebug.Log("grenade: tap, EFT's quick throw");
                // (the monkey's ratchet is one of its raise sounds now: ZombiesMonkeyHands.RaiseNotes)
                // EFT's quick throw itself (its G key goes through the quick-slot UI, not the player): out, pin, thrown
                bool failed = false;
                me.SetInHandsForQuickUse(grenade, result =>
                {
                    if (result.Failed)
                    {
                        failed = true;
                        ZombiesPlugin.Log.LogWarning($"Zombies grenade: the quick throw failed: {result.Error}");
                    }
                });
                if (failed)
                {
                    _busy = false;
                    yield break;
                }
                // Busy until the throw is over (no second grenade on top of it)
                yield return new WaitForSeconds(0.6f);
                float until = Time.time + BackWithin;
                while (Time.time < until && me.HandsController != null && me.HandsController.GetType().Name.Contains("QuickGrenade"))
                    yield return null;
                _busy = false;
                yield break;
            }
            yield return Cook(me, grenade, key);
        }

        private static IEnumerator Cook(Player me, ThrowWeap grenade, KeyCode key)
        {
            _cooking = true;
            Item gun = me.HandsController.Item is Weapon w ? w : null;
            Animator hands = null;
            float oldSpeed = 1f;
            try
            {
                ZombiesDebug.Log($"grenade: held, cooking ({grenade.ShortName.Localized()}), back to {(gun != null ? gun.ShortName.Localized() : "nothing")} after");
                me.SetInHands(grenade, _ => { });
                // Out, then the pin pulled: both quick
                IGrenadeController ctrl = null;
                float until = Time.time + ReadyWithin;
                while (Time.time < until)
                {
                    ctrl = me.HandsController as IGrenadeController;
                    if (ctrl != null && ctrl.Item == grenade)
                    {
                        if (hands == null)
                        {
                            hands = me.HandsController.ControllerGameObject != null
                                ? me.HandsController.ControllerGameObject.GetComponentInChildren<Animator>() : null;
                            if (hands != null)
                            {
                                oldSpeed = hands.speed;
                                hands.speed = Fast;
                            }
                        }
                        if (ctrl.CanThrow())
                            break;
                    }
                    yield return null;
                }
                if (ctrl == null || ctrl.Item != grenade || !ctrl.CanThrow())
                {
                    ZombiesDebug.Log("grenade: never came out ready");
                    yield break;
                }
                // What EFT's own left click does: the pin out, held at the pre-throw (a second one, below, throws it).
                // Only the grenade's idle takes it (CanThrow is already true while the draw is still finishing, and a
                // pull then was dropped: the "let go" pulled the pin and threw at once, never cooked), so it's tried
                // until EFT is holding it
                until = Time.time + ReadyWithin;
                float nextPull = 0f;
                string op = null;
                while (!ctrl.WaitingForHighThrow && Time.time < until && me.HandsController == (object)ctrl)
                {
                    if (Time.time >= nextPull)
                    {
                        nextPull = Time.time + 0.15f;
                        var g = me.HandsController as Player.GrenadeHandsController;
                        op = g?.CurrentOperation?.GetType().Name ?? me.HandsController?.GetType().Name;
                        // Still coming out: wait; idle: EFT's own left click (the pin out, held)
                        if (!(g?.CurrentOperation is Player.GrenadeHandsController.SpawnOperation))
                            ctrl.HandleFireInput();
                    }
                    yield return null;
                }
                ZombiesDebug.Log($"grenade: pin out (waiting to throw: {ctrl.WaitingForHighThrow}, in {op})");
                if (!ctrl.WaitingForHighThrow)
                {
                    // Never worse than a plain throw: EFT didn't hold it, so it's thrown now (no cook)
                    ZombiesDebug.Log("grenade: EFT never held it at the pin: thrown as it is");
                    if (me.HandsController == (object)ctrl)
                        ctrl.HandleFireInput();
                    _pinAt = -1f;
                    yield return new WaitForSeconds(1.5f);
                    if (gun is Weapon w2 && me.HandsController?.Item != w2 && w2.Parent != null && !(me.HandsController?.InCanNotBeInterruptedOperation() ?? false))
                        me.SetInHands(w2, _ => { });
                    yield break;
                }
                bool monkey = ZombiesMonkey.Is(grenade);
                // The monkey is wound up (its ratchet), and has no fuse in the hand: held as long as the key is
                _pinAt = monkey ? -1f : Time.time;
                if (monkey)
                    _cooking = false; // its ratchet plays with its raise sounds (ZombiesMonkeyHands.RaiseNotes)
                // Back at the pre-throw, held while the key's down, cooking; the pin pull plays quick too (the original's snap),
                // the speed back to normal once it's out
                until = Time.time + ReadyWithin;
                while (!ctrl.WaitingForHighThrow && Time.time < until && me.HandsController == (object)ctrl)
                    yield return null;
                float pinFast = Time.time + 0.45f;
                while (Time.time < pinFast && ZombiesControls.IsDown(key) && me.HandsController == (object)ctrl)
                    yield return null;
                if (hands != null)
                    hands.speed = oldSpeed;
                // The monkey: held while the key's down, and never thrown before its crank (the original's raise) is done
                float crankEnd = Time.time + (monkey ? ZombiesMonkeyView.CrankLeft : 0f);
                while ((ZombiesControls.IsDown(key) || Time.time < crankEnd) && me.HandsController == (object)ctrl && ZombiesMatch.Active
                       && (monkey || Time.time - _pinAt < ZombiesGrenades.FuseSeconds))
                    yield return null;
                bool inHand = !monkey && Time.time - _pinAt >= ZombiesGrenades.FuseSeconds;
                until = Time.time + ReadyWithin;
                while (!ctrl.WaitingForHighThrow && Time.time < until && me.HandsController == (object)ctrl)
                    yield return null;
                // Let go: thrown, as a second left click throws a held grenade in EFT (HandleFireInput while waiting)
                bool waiting = ctrl.WaitingForHighThrow;
                if (_pinAt >= 0f)
                    ZombiesGrenades.Released(Time.time - _pinAt);
                if (me.HandsController == (object)ctrl)
                {
                    ctrl.HandleFireInput();
                    yield return null;
                    if (me.HandsController == (object)ctrl && ctrl.WaitingForHighThrow)
                        ctrl.HighThrow();
                }
                ZombiesDebug.Log($"grenade: let go (was waiting: {waiting}, still waiting: {me.HandsController == (object)ctrl && ctrl.WaitingForHighThrow})");
                // Held too long: it goes off in the hand (thrown with nothing left, so the blast is right there) and
                // you go down, unless PHD Flopper
                if (inHand)
                {
                    ZombiesPlugin.Log.LogInfo("Zombies: a grenade went off in the hand");
                    if (!ZombiesPhd.Has && ZombiesDowned.Mine == ZombiesDowned.State.Up)
                        ZombiesDowned.GoDown(me);
                }
                // The gun back once the throw is done
                until = Time.time + BackWithin;
                while (Time.time < until && me.HandsController == (object)ctrl)
                    yield return null;
                yield return new WaitForSeconds(0.1f);
                if (!inHand && gun is Weapon back && me.HandsController?.Item != back && back.Parent != null
                    && !(me.HandsController?.InCanNotBeInterruptedOperation() ?? false))
                    me.SetInHands(back, _ => { });
            }
            finally
            {
                if (hands != null)
                    hands.speed = oldSpeed;
                _cooking = false;
                _busy = false;
            }
        }
    }
}
