using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Reloads in a zombies match, watched and rescued:
    /// - Every reload of your gun is logged (the mag, whether EFT could start it, how it ended).
    /// - When EFT finds no mag to reload with (it only says "need ammo"), the log says why: each mag you carry and why
    ///   EFT refused it. The match's guns sit in slots EFT doesn't expect (a pistol in a rifle slot), so EFT can refuse
    ///   a mag that fits. With "Reload rescue" on, a mag with rounds that fits the gun goes straight in (and a round
    ///   into the chamber when it's empty), so a gun is never stuck unloadable.
    /// - <see cref="ForceLoad"/>: the same straight load, for a gun handed over whose first reload never happened.
    /// </summary>
    internal static class ZombiesReload
    {
        public static ConfigEntry<bool> Rescue, CancelOnSwap;
        private static bool _patched;

        public static void Bind(ConfigFile config)
        {
            Rescue = config.Bind("19. Debug: logging", "Reload rescue", true, new ConfigDescription(
                "When EFT refuses every mag you carry for a reload, a mag with rounds that fits the gun goes straight in.",
                null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 90 }));
            CancelOnSwap = config.Bind("3. Zombies controls", "Swapping cancels a reload", true,
                "Swapping guns (or anything else that puts the gun away) before the new mag is in cancels the reload: the gun keeps the rounds it had.");
        }

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            try
            {
                // EFT's and every subclass that declares its own (Fika's client controllers override it)
                foreach (Type t in AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                             .Where(t => typeof(Player.FirearmController).IsAssignableFrom(t)))
                {
                    var m = AccessTools.DeclaredMethod(t, nameof(Player.FirearmController.ReloadMag),
                        new[] { typeof(Magazine), typeof(ItemAddress), typeof(Callback) });
                    if (m == null || m.IsAbstract)
                        continue;
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesReload), nameof(ReloadAsked)));
                    // Before and after every other mod's prefix: the Thundergun's spent canister keeps its place
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesReload), nameof(NotePlace)) { priority = Priority.First });
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesReload), nameof(KeepPlace)) { priority = Priority.Last });
                }
                harmony.Patch(AccessTools.Method(typeof(Player.FirearmController.BaseReloadOperation), nameof(Player.FirearmController.BaseReloadOperation.HideWeapon)),
                    postfix: new HarmonyMethod(typeof(ZombiesReload), nameof(HideDuringReload)));
                harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.ReloadMagNotFound)),
                    prefix: new HarmonyMethod(typeof(ZombiesReload), nameof(NoMagFound)));
                // EFT threw in here on Fika clients (FirearmsAnimator gone: a leftover gun controller still told of
                // movement changes); the throw broke MovementContext's state change for the live controller and its
                // reload never finished (user, 2026-11-09). Skipped quietly when there's no animator, zombies only
                harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.ResetAimingAnimationsFlags)),
                    prefix: new HarmonyMethod(typeof(ZombiesReload), nameof(NoAnimatorNoReset)));
                // The same hole in EFT's knife: a quick knife on a Fika client hit something, SetDeflected found no
                // animator and threw every frame, the swing never ended and the hands stayed broken (user, 2026-11-09:
                // shoot, then melee). Skipped quietly with no animator, zombies only
                harmony.Patch(AccessTools.Method(typeof(Player.BaseKnifeController), nameof(Player.BaseKnifeController.SetDeflected)),
                    prefix: new HarmonyMethod(typeof(ZombiesReload), nameof(NoAnimatorNoDeflect)));
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies reload: couldn't watch EFT's reloads: {e.Message}");
            }
        }

        private static bool NoAnimatorNoDeflect(Player.BaseKnifeController __instance)
        {
            if (!ZombiesMatch.Active)
                return true;
            try
            {
                bool ok = __instance != null && __instance.FirearmsAnimator != null && __instance._player?.MovementContext != null;
                if (!ok)
                    ZombiesDebug.Log("knife: EFT's deflect skipped (no animator on this knife: it would have broken the hands)");
                return ok;
            }
            catch
            {
                return false;
            }
        }

        private static bool NoAnimatorNoReset(Player.FirearmController __instance)
        {
            if (!ZombiesMatch.Active)
                return true;
            try
            {
                return __instance != null && __instance.FirearmsAnimator != null;
            }
            catch
            {
                return false;
            }
        }

        private static Player Me => Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;

        private static void Log(string text) => ZombiesPlugin.Log.LogInfo("[reload] " + text);

        // Off: dropped models hung in mid-air (the user 2026-11-07); the cell drop is off too
        private const bool DropCartridge = false;

        // ---------- The Thundergun's spent canister (user, 2026-11-11) ----------
        // In normal play the Thundergun mod drops the spent canister at the player's feet on a reload (its ReloadMag
        // prefix sets the place for the old canister to none). In a zombies match that left a real canister on the
        // floor that could be picked up. Here the old canister goes where the new one came from instead (EFT's normal
        // swap), and the zombies ammo takes it as the gun's spare and refills it from the reserve, like every other gun's
        // mag. The Thundergun mod is unchanged: normal play still drops it.
        private static ItemAddress _place;
        private static Player.FirearmController _placeFor;

        // First of all prefixes: the place EFT was asked to put the old mag
        private static void NotePlace(Player.FirearmController __instance, ItemAddress itemAddress)
        {
            _place = itemAddress;
            _placeFor = __instance;
        }

        // Last of all prefixes: a Thundergun's place taken away in a zombies match is put back
        private static void KeepPlace(Player.FirearmController __instance, ref ItemAddress itemAddress)
        {
            try
            {
                if (itemAddress == null && _place != null && ReferenceEquals(_placeFor, __instance) && ZombiesMatch.Active
                    && __instance.Item is Weapon gun && ZombiesAmmo.IsThundergun(gun))
                {
                    itemAddress = _place;
                    Log("Thundergun: the spent canister goes back into the pockets (no canister dropped in zombies)");
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies reload: Thundergun canister place: {e.Message}");
            }
            finally
            {
                _place = null;
                _placeFor = null;
            }
        }

        // One drop per reload (a subclass's ReloadMag can call its base: both are patched)
        private static readonly HashSet<object> _dropped = new HashSet<object>();

        private static System.Collections.IEnumerator Forget(object controller)
        {
            yield return new WaitForSeconds(1f);
            _dropped.Remove(controller);
        }

        private static void ReloadAsked(Player.FirearmController __instance, Magazine magazine, ref Callback callback)
        {
            if (!ZombiesMatch.Active || Me?.HandsController != __instance)
                return;
            bool can = false;
            try
            {
                can = __instance.CanStartReload();
            }
            catch
            {
            }
            Log($"asked: {magazine?.LocalizedName() ?? "no mag"} {magazine?.Count} from {magazine?.Parent?.Container?.ID ?? "nowhere"}, " +
                $"EFT can start it: {can}; gun before: {ZombiesTrace.GunText(__instance.Item)}");
            Callback inner = callback;
            Weapon gun = __instance.Item;
            // The Thundergun's spent cartridge falls out as in the original (a model only, cleared at the next round)
            if (DropCartridge && gun != null && ZombiesAmmo.IsThundergun(gun) && gun.GetCurrentMagazine() is Magazine old && !_dropped.Contains(__instance))
            {
                _dropped.Add(__instance);
                ZombiesLitter.DropFromHands(old, Me, 0.7f);
                ZombiesPlugin.Instance.StartCoroutine(Forget(__instance));
            }
            _askedAt = Time.time;
            _askedBy = __instance;
            // What the gun and the mag going in hold now, for a swap that cancels it (CancelForSwap)
            _snapGun = gun;
            _snapNext = magazine;
            _snapNextCount = magazine?.Count ?? 0;
            _snapOld = gun?.GetCurrentMagazine();
            _snapOldTotal = (_snapOld?.Count ?? 0) + (gun?.ChamberAmmoCount ?? 0);
            callback = result =>
            {
                _askedAt = -1f;
                if (_snapGun == gun)
                    _snapGun = null;
                Log(result == null || !result.Failed ? $"finished: {ZombiesTrace.GunText(gun)}" : $"FAILED: {result.Error}; {ZombiesTrace.GunText(gun)}");
                inner?.Invoke(result);
            };
        }

        // ---------- A reload cancelled by a swap (user, 2026-11-15) ----------
        // "Swapping gun halfway through a reload: it doesn't count the reload as happening, cancels it and does the action the
        // player asked for." EFT's reload has already moved the mags in the inventory when it starts (the animation only
        // shows it), and holds a swap until it's over. Here: before the new mag is in, the reload is fast-forwarded (EFT's own
        // way to finish it at once, so its inventory stays right) and the rounds are put back as they were (ZombiesAmmo), then
        // the swap goes ahead at once. Mag guns only (shotgun tubes keep the shells already in, as in the original); never the
        // wonder weapons' cells.
        private static Weapon _snapGun;
        private static Magazine _snapOld, _snapNext;
        private static int _snapOldTotal, _snapNextCount;

        /// <summary>Your gun's mag reload, if one is under way and its mag not in yet: cancelled (true) before a swap.</summary>
        public static bool CancelForSwap(Player me)
        {
            if (!ZombiesMatch.Active || CancelOnSwap?.Value == false || !(me?.HandsController is Player.FirearmController fc))
                return false;
            if (!(fc.CurrentOperation is Player.FirearmController.ReloadExternalMagOperation op) || op.State == Player.EOperationState.Finished
                || op._magInsertedToWeapon)
                return false;
            Weapon gun = fc.Item;
            if (gun == null || gun != _snapGun || _snapNext == null || ZombiesAmmo.IsWonderWeapon(gun))
                return false;
            Magazine next = _snapNext, old = _snapOld;
            int oldTotal = _snapOldTotal, nextCount = _snapNextCount;
            _snapGun = null;
            try
            {
                op.FastForward();
            }
            catch (Exception e)
            {
                Log($"cancel: the fast-forward threw ({e.Message})");
                return false;
            }
            try
            {
                ZombiesAmmo.ReloadCancelled(me, gun, next, oldTotal - gun.ChamberAmmoCount, old, nextCount);
            }
            catch (Exception e)
            {
                Log($"cancel: putting the rounds back threw ({e.Message})");
            }
            _askedAt = -1f;
            Log($"cancelled by a swap: {ZombiesTrace.GunText(gun)}");
            return true;
        }

        // Anything that puts the gun away mid-reload (EFT's own slot keys, a grenade, going down): the same cancel. EFT
        // stores the hide and runs it once the reload is over; the fast-forward ends it now, so the hide runs at once
        private static void HideDuringReload(Player.FirearmController.BaseReloadOperation __instance)
        {
            try
            {
                Player p = __instance?.Player;
                if (p != null && p.IsYourPlayer && __instance is Player.FirearmController.ReloadExternalMagOperation)
                    CancelForSwap(p);
            }
            catch (Exception e)
            {
                Log($"cancel on hide threw ({e.Message})");
            }
        }

        // ---------- A reload that never ends (user, 2026-11-09, Fika client) ----------
        // EFT's own FirearmController threw (ResetAimingAnimationsFlags, from a movement state change) in the middle of
        // a reload: the mag went in but the reload never finished, the hands stayed stuck in it. Nothing of ours to
        // fix in EFT, so: a reload still going after 6 s gets the gun put away and drawn again (a fresh controller).
        private static float _askedAt = -1f;
        private static Player.FirearmController _askedBy;
        private static bool _rescuing;

        public static void Tick()
        {
            if (_askedAt < 0f || _rescuing || Time.time - _askedAt < 6f)
                return;
            Player me = Me;
            var fc = _askedBy;
            _askedAt = -1f;
            if (!ZombiesMatch.Active || me == null || fc == null || me.HandsController != fc)
                return;
            bool stuck;
            try
            {
                stuck = fc.IsInReloadOperation();
            }
            catch
            {
                stuck = true;
            }
            if (!stuck)
                return;
            Log($"STUCK for 6 s, the gun is put away and drawn again: {ZombiesTrace.GunText(fc.Item)}");
            ZombiesPlugin.Instance.StartCoroutine(Redraw(me));
        }

        // ---------- A drawn gun vanishing (user, 2026-11-09: the PaP'd M16 came out and was gone ~0.9 s later, every time,
        // "Operation aborted" in EFT's log, after the player had Pack-a-Punched their Ray Gun; cause not found) ----------
        // A gun drawn and then empty hands within 2 s, twice in 20 s: the kit sent to the host again (its copy may be
        // missing or stale) and the gun drawn again a moment later; logged so the next test shows the cause.
        private static string _drawnId, _vanishedId;
        private static float _drawnAt, _vanishedAt, _healedAt = -100f;
        private static int _vanishes;

        // Hands left empty with a gun still carried and nothing of ours going on (a reload or draw the host aborted):
        // after a few seconds the last gun's copy on the host is replaced with ours and it's drawn again, so a player
        // is never stuck gunless (user, 2026-11-10: "my character has no gun out and can't do anything")
        private static float _emptySince = -1f;
        private static string _lastGunId;

        public static void WatchEmpty(Player me)
        {
            if (me?.HandsController == null || _rescuing)
                return;
            if (me.HandsController.Item is Weapon held)
            {
                _lastGunId = held.Id.ToString();
                _emptySince = -1f;
                return;
            }
            bool quiet = ZombiesMatch.LoadedIn && ZombiesDowned.Mine == ZombiesDowned.State.Up && !ZombiesDowned.GunIsAway
                         && !ZombiesGameOver.Active && !ZombiesExfil.Escaped && !ZombiesPerks.Drinking && !ZombiesPaP.Holding(_lastGunId);
            if (!quiet || !(me.HandsController is Player.EmptyHandsController))
            {
                _emptySince = -1f;
                return;
            }
            if (_emptySince < 0f)
            {
                _emptySince = Time.time;
                return;
            }
            if (Time.time - _emptySince < 5f || Time.time - _healedAt < 8f)
                return;
            Item gun = _lastGunId == null ? null
                : me.InventoryController?.Inventory?.Equipment?.GetAllItems().FirstOrDefault(i => i.Id.ToString() == _lastGunId);
            if (gun == null)
                gun = me.InventoryController?.Inventory?.Equipment?.GetAllItems().OfType<Weapon>().FirstOrDefault();
            if (gun == null)
                return;
            _healedAt = Time.time;
            _emptySince = -1f;
            ZombiesPlugin.Log.LogWarning($"Zombies: hands empty for 5 s with the {gun.LocalizedName()} still carried: its copy on the host replaced, drawn again");
            if (gun.CurrentAddress != null && ZombiesNet.Online && !ZombiesNet.IsHost)
            {
                ZombiesNet.ItemRemoved(gun);
                ZombiesNet.ItemAdded(gun, gun.CurrentAddress);
            }
            ZombiesPlugin.Instance.StartCoroutine(RedrawLater(me));
        }

        public static void WatchVanish(Player me)
        {
            if (me?.HandsController == null || _rescuing)
                return;
            Item held = me.HandsController.Item;
            if (held is Weapon w)
            {
                string id = w.Id.ToString();
                if (id != _drawnId)
                {
                    _drawnId = id;
                    _drawnAt = Time.time;
                }
                return;
            }
            if (!(me.HandsController is Player.EmptyHandsController) || _drawnId == null)
                return;
            string gone = _drawnId;
            float after = Time.time - _drawnAt;
            _drawnId = null;
            if (after > 2f)
                return;
            _vanishes = gone == _vanishedId && Time.time - _vanishedAt < 20f ? _vanishes + 1 : 1;
            _vanishedId = gone;
            _vanishedAt = Time.time;
            ZombiesPlugin.Log.LogWarning($"Zombies: the gun just drawn ({gone}) went away after {after:0.0} s (#{_vanishes})");
            if (_vanishes < 2 || Time.time - _healedAt < 15f)
                return;
            _healedAt = Time.time;
            // Likely the host's copy of that gun is out of step with ours (seen right after a reload's mag pooling, which
            // changes rounds without telling the host): its copy is replaced with ours (removed, then sent whole), then
            // the gun is drawn again
            Item gun = me.InventoryController?.Inventory?.Equipment?.GetAllItems().FirstOrDefault(i => i.Id.ToString() == gone);
            if (gun?.CurrentAddress != null)
            {
                ZombiesNet.ItemRemoved(gun);
                ZombiesNet.ItemAdded(gun, gun.CurrentAddress);
                ZombiesPlugin.Log.LogWarning($"Zombies: {gun.LocalizedName()} keeps vanishing on the draw: the host's copy replaced with ours, drawn again");
            }
            else
            {
                ZombiesNet.SendKit();
                ZombiesPlugin.Log.LogWarning("Zombies: a gun keeps vanishing on the draw: kit sent to the host again, drawn again");
            }
            ZombiesPlugin.Instance.StartCoroutine(RedrawLater(me));
        }

        private static System.Collections.IEnumerator RedrawLater(Player me)
        {
            yield return new WaitForSeconds(1.2f);
            if (me != null && me.HandsController is Player.EmptyHandsController)
                yield return Redraw(me);
        }

        /// <summary>
        /// The hands stuck (a swap never answered): EFT's own put-away first; if even that never answers in 3 s, put away
        /// again outside EFT's queue of hand changes, then the gun is drawn. Logged at each step.
        /// </summary>
        internal static System.Collections.IEnumerator ForceDraw(Player me, Weapon gun)
        {
            if (_rescuing || me == null)
                yield break;
            _rescuing = true;
            bool emptied = false;
            try
            {
                me.SetEmptyHands(_ => emptied = true);
            }
            catch (Exception e)
            {
                Log($"force draw: put away refused: {e.Message}");
            }
            float until = Time.time + 3f;
            while (!emptied && Time.time < until)
                yield return null;
            if (!emptied && !(me.HandsController is Player.EmptyHandsController))
            {
                ZombiesPlugin.Log.LogWarning("Zombies: force draw: the hands never put away: put away again, skipping EFT's queue of hand changes");
                try
                {
                    // Not scheduled: past whatever hand change the queue is stuck behind (Fika's player still sends it)
                    me.Proceed(true, (Callback<IEmptyHandsController>)(_ => emptied = true), false);
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies: force draw: a fresh hands controller couldn't be made ({e.Message})");
                }
                yield return new WaitForSeconds(0.5f);
            }
            bool done = false;
            try
            {
                me.SetInHands(gun, result =>
                {
                    done = true;
                    ZombiesPlugin.Log.LogInfo($"Zombies: force draw of the {gun.LocalizedName()}: {(result.Failed ? "failed: " + result.Error : "done")}");
                });
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: force draw: drawing refused ({e.Message})");
            }
            until = Time.time + 4f;
            while (!done && Time.time < until)
                yield return null;
            _rescuing = false;
        }

        internal static System.Collections.IEnumerator Redraw(Player me)
        {
            _rescuing = true;
            bool emptied = false;
            try
            {
                me.SetEmptyHands(_ => emptied = true);
            }
            catch (Exception e)
            {
                Log($"put away refused: {e.Message}");
            }
            float until = Time.time + 3f;
            while (!emptied && Time.time < until)
                yield return null;
            try
            {
                me.TrySetLastEquippedWeapon(true, _ => { });
            }
            catch (Exception e)
            {
                Log($"draw again refused: {e.Message}");
            }
            _rescuing = false;
        }

        // EFT found no mag it would take: why, and the rescue
        private static void NoMagFound(Player.FirearmController __instance)
        {
            Player me = Me;
            if (!ZombiesMatch.Active || me == null || me.HandsController != __instance || !(__instance.Item is Weapon w))
                return;
            Slot slot = w.GetMagazineSlot();
            var s = new StringBuilder($"EFT found no mag for the {ZombiesTrace.GunText(w)}. ");
            Magazine best = null;
            try
            {
                List<Magazine> carried = me.InventoryController.Inventory.Equipment.GetAllItems().OfType<Magazine>()
                    .Where(m => m.Parent?.Container != slot && !(m.Parent?.Container is Slot)).ToList();
                var reachable = new List<Magazine>();
                me.InventoryController.GetReachableItemsOfTypeNonAlloc(reachable);
                if (carried.Count == 0)
                    s.Append("No mags carried at all.");
                foreach (Magazine m in carried)
                {
                    bool fits = slot != null && slot.CheckCompatibility(m);
                    string eft = slot == null ? "the gun has no mag slot"
                        : ItemManipulator.CheckMoveIgnoringTargetItem(m, slot, me.InventoryController) is var check && check.Succeeded ? "EFT would take it"
                        : $"EFT refuses: {check.Error}";
                    s.Append($"[{m.LocalizedName()} {m.Count}/{m.MaxCount} in {m.Parent?.Container?.ID}, {(reachable.Contains(m) ? "in reach" : "out of reach")}, " +
                             $"{(fits ? "fits the gun" : "doesn't fit")}, {eft}] ");
                    if (fits && m.Count > 0 && (best == null || m.Count > best.Count))
                        best = m;
                }
            }
            catch (Exception e)
            {
                s.Append($"(couldn't check: {e.Message})");
            }
            Log(s.ToString());
            if (best != null && Rescue?.Value == true && w.GetCurrentMagazine()?.Count != best.MaxCount)
                ForceLoad(me, w, best, "rescue");
        }

        /// <summary>
        /// The mag into the gun without EFT's reload: EFT's own move when it takes it (the gun in the hands shows the
        /// mag), else straight into the slot; the mag that was in goes where this one was. A round into an empty chamber.
        /// </summary>
        public static bool ForceLoad(Player player, Weapon w, Magazine mag, string why)
        {
            InventoryController inventory = player?.InventoryController;
            Slot slot = w?.GetMagazineSlot();
            if (inventory == null || slot == null || mag == null)
                return false;
            try
            {
                ItemAddress from = mag.Parent;
                Magazine old = slot.ContainedItem as Magazine;
                bool done = false;
                if (old == null)
                {
                    var moved = ItemManipulator.Move(mag, slot.CreateItemAddress(), inventory, false);
                    if (moved.Succeeded)
                    {
                        moved.Value.RaiseEvents(inventory, CommandStatus.Begin);
                        moved.Value.RaiseEvents(inventory, CommandStatus.Succeed);
                        done = true;
                    }
                    else
                        Log($"{why}: EFT's move refused ({moved.Error}), straight in");
                }
                if (!done)
                {
                    var removed = ItemManipulator.Remove(mag, inventory, false, true);
                    if (removed.Failed)
                    {
                        Log($"{why}: couldn't take the {mag.LocalizedName()} out of {from?.Container?.ID}: {removed.Error}");
                        return false;
                    }
                    removed.Value.RaiseEvents(inventory, CommandStatus.Begin);
                    removed.Value.RaiseEvents(inventory, CommandStatus.Succeed);
                    if (old != null)
                    {
                        slot.RemoveItemWithoutRestrictions();
                        ItemAddress back = from is GridItemAddress ? from : ZombiesItems.FindGrid(inventory, old);
                        if (back == null || !ZombiesItems.Add(inventory, old, back, out _))
                            Log($"{why}: the old {old.LocalizedName()} had nowhere to go (dropped)");
                    }
                    var added = slot.AddWithoutRestrictions(mag);
                    if (added.Failed)
                    {
                        Log($"{why}: the {mag.LocalizedName()} wouldn't go in: {added.Error}");
                        return false;
                    }
                }
                Chamber(w, mag);
                Log($"{why}: loaded straight in: {ZombiesTrace.GunText(w)}");
                return true;
            }
            catch (Exception e)
            {
                Log($"{why}: failed: {e}");
                return false;
            }
        }

        // A round from the mag into an empty chamber (the slide racked)
        public static void Chamber(Weapon w, Magazine mag)
        {
            if (w.Chambers.Length == 0 || w.Chambers[0].ContainedItem != null || mag.Count == 0)
                return;
            Ammo stack = mag.Cartridges.Items.OfType<Ammo>().LastOrDefault();
            if (stack == null || !(Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), stack.TemplateId, null) is Ammo round))
                return;
            round.StackObjectsCount = 1;
            if (stack.StackObjectsCount > 1)
                stack.StackObjectsCount--;
            else
                mag.Cartridges.Remove(stack, false);
            w.Chambers[0].AddWithoutRestrictions(round);
        }
    }
}
