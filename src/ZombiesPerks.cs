using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's perks in a zombies match, bought at the layout's perk machines (they need the power; Quick Revive has its own
    /// machine rules, QuickReviveMachine). Four at most; going down loses them all, as in the original. What each does:
    /// - Jugger-Nog: 5 slaps to go down, not 3 (ZombiesSlaps).
    /// - Speed Cola: reloads 2.2x as fast, guns swap out and in twice as fast again (on top of everyone's quicker swap).
    /// - Double Tap II: every bullet does double damage, a fifth more fire rate.
    /// - Stamin-Up: moving 20% faster (walking and sprinting), sprinting lasts twice as long.
    /// - Quick Revive: solo, get yourself up; with a team, revive others twice as fast (ZombiesDowned).
    /// - Mule Kick: a third gun (the holster slot takes any gun); lost with the perk, the third gun goes too.
    /// - Deadshot Daiquiri: the original (PC): 35% less spread, the hipfire much tighter; no damage change.
    /// - Widow's Wine: slapped with a grenade left, one goes to a web that holds every zombie within 8 m for 10 s; your
    ///   thrown grenades web the zombies round where they go off too (ZombiesWidow).
    /// - PHD Flopper: your own explosions no longer hurt you, and your knife blows up round a zombie now and then (ZombiesPhd).
    /// Perk Beverages (Kennerz's perk mod) counts too: a perk drunk there is had here. The drink and the HUD icons will
    /// come from it once it can be asked to.
    /// </summary>
    internal static class ZombiesPerks
    {
        public class Perk
        {
            public string Id, Name;
            public int Price;
        }

        public static readonly Perk[] All =
        {
            new Perk { Id = "jugg", Name = "Jugger-Nog", Price = 2500 },
            new Perk { Id = "speed", Name = "Speed Cola", Price = 3000 },
            new Perk { Id = "dtap", Name = "Double Tap Root Beer", Price = 2000 },
            new Perk { Id = "stamin", Name = "Stamin-Up", Price = 2000 },
            new Perk { Id = "deadshot", Name = "Deadshot Daiquiri", Price = 1500 },
            new Perk { Id = "mule", Name = "Mule Kick", Price = 4000 },
            new Perk { Id = "widow", Name = "Widow's Wine", Price = 4000 },
            new Perk { Id = "phd", Name = "PHD Flopper", Price = 2000 },
        };

        /// <summary>the original's four, or the map's own (its layout's MaxPerks).</summary>
        public static int MaxPerks => ZombiesMatch.MaxPerksHere;
        private const float SpeedColaReload = 2.2f, SpeedColaDraw = 2f, DoubleTapRate = 1.33f, StaminUpWalk = 1.35f; // was 1.2 ("even quicker", the user 2026-11-08)

        private static readonly HashSet<string> Owned = new HashSet<string>();
        private static bool _patched;

        public static bool Has(string id) => Owned.Contains(id) || ZombiesSlaps.HasPerk(id);

        /// <summary>Perks held (Quick Revive counted, Perk Beverages' too), for the four-perk limit.</summary>
        public static int Count => All.Count(p => Has(p.Id)) + (ZombiesDowned.HasQuickRevive() ? 1 : 0);

        // ---------- Drinking it (Perk Beverages' bottle and the original drink) ----------

        private static Type _perkDef, _perkDrink;
        private static bool _looked;

        private static void Look()
        {
            if (_looked)
                return;
            _looked = true;
            _perkDef = AccessTools.TypeByName("PerkBeverages.PerkDef");
            _perkDrink = AccessTools.TypeByName("PerkBeverages.PerkDrink");
        }

        // The perk's bottle template (Perk Beverages' PerkDef.All: Id -> Bottle), or none
        private static string BottleOf(string id)
        {
            Look();
            if (!(_perkDef?.GetField("All")?.GetValue(null) is Array defs))
                return null;
            foreach (object d in defs)
                if (Traverse.Create(d).Field("Id").GetValue<string>() == id)
                    return Traverse.Create(d).Field("Bottle").GetValue<string>();
            return null;
        }

        /// <summary>
        /// Bought: the perk's bottle into your hands and drunk (Perk Beverages' the original drink: its anims and sounds, the burp,
        /// the blur and the perk, all its own); without Perk Beverages, the perk at once.
        /// </summary>
        public static IEnumerator Drink(string id, Action withoutBottle)
        {
            Player me = Me;
            string bottle = BottleOf(id);
            if (me == null || bottle == null || !(Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), bottle, null) is FoodDrink drink))
            {
                ZombiesPlugin.Log.LogInfo($"Zombies perks: {id} given without a drink ({(me == null ? "no player" : bottle == null ? "no Perk Beverages bottle for it" : "the bottle couldn't be made")})");
                withoutBottle();
                yield break;
            }
            // The bottle's models: in the world and in the hands (the drink). The hands one missing broke the hands
            // ("item_perk_bottle_revive_container.bundle is not loaded") and the perk never came.
            yield return ZombiesItems.LoadModels(drink);
            ItemAddress place = ZombiesItems.FindGrid(me.InventoryController, drink);
            string error = "no room in the pockets";
            if (place == null || !ZombiesItems.Add(me.InventoryController, drink, place, out error))
            {
                ZombiesPlugin.Log.LogWarning($"Zombies perks: no room for the {id} bottle ({error}), given without the drink");
                withoutBottle();
                yield break;
            }
            // A Fika headless host refuses drawing a bottle it hasn't got yet (the drink was put away, user 2026-11-09)
            yield return ZombiesNet.UntilHostHas(drink, id + " bottle");
            ZombiesPlugin.Log.LogInfo($"Zombies perks: drinking {id} (bottle in {place.Container?.ID}, hands: {ZombiesTrace.HandsText(me)})");
            // Never under a running reload (ZombiesHands)
            yield return ZombiesHands.Settle(me, $"the {id} drink");
            bool failed = false, answered = false;
            me.SetInHands(drink, 1f, 0, result =>
            {
                answered = true;
                if (result.Failed)
                {
                    failed = true;
                    ZombiesPlugin.Log.LogWarning($"Zombies perks: couldn't drink the {id} bottle ({result.Error}), given without the drink");
                }
            });
            ZombiesPlugin.Instance.StartCoroutine(DrinkWatch(me, id, drink, withoutBottle, () => failed, () => answered));
        }

        /// <summary>
        /// A perk bought and still being drunk (until it's given, or 15 s): no machine sells again meanwhile, so a quick
        /// second press can't spend the price twice for one perk.
        /// </summary>
        public static bool Drinking
        {
            get
            {
                if (_drinking != null && (Got(_drinking) || Time.time >= _drinkingUntil))
                    _drinking = null;
                return _drinking != null;
            }
        }
        private static string _drinking;
        private static float _drinkingUntil;

        public static void StartedDrinking(string id)
        {
            _drinking = id;
            _drinkingUntil = Time.time + 15f;
        }

        private static bool Got(string id) => id == "revive" ? ZombiesDowned.HasQuickRevive() : Has(id);

        /// <summary>
        /// The drink is watched to its end: the bottle in the hands, then gone, then the perk (Perk Beverages gives it
        /// after the burp). Whatever goes wrong on the way (the drink refused, stuck, put away, or drunk without the perk
        /// coming), the perk is given anyway, the bottle taken away and the gun put back in the hands.
        /// </summary>
        private static IEnumerator DrinkWatch(Player me, string id, Item drink, Action give, Func<bool> failed, Func<bool> answered)
        {
            string bottleId = drink.Id.ToString();
            bool InHands() => me != null && me.HandsController is Player.MedsController mc && mc.Item?.Id.ToString() == bottleId;
            bool InGear() => me != null && me.InventoryController.Inventory.Equipment.GetAllItems().Any(i => i.Id.ToString() == bottleId);
            // Into the hands (up to 4 s)
            float start = Time.time;
            while (Time.time - start < 4f && !InHands() && !failed())
                yield return null;
            bool drank = InHands();
            ZombiesPlugin.Log.LogInfo($"Zombies perks: {id} bottle {(drank ? "in the hands" : failed() ? "refused" : answered() ? "not in the hands" : "no answer from the game")} after {Time.time - start:0.0} s ({ZombiesTrace.HandsText(me)})");
            // The drink (up to 12 s), then the bottle leaving the gear
            start = Time.time;
            while (drank && Time.time - start < 12f && InHands())
                yield return null;
            bool stuck = drank && InHands();
            if (drank && !stuck)
                ZombiesPlugin.Log.LogInfo($"Zombies perks: {id} bottle out of the hands after {Time.time - start:0.0} s, bottle {(InGear() ? "still in the gear" : "gone (drunk)")}");
            // Perk Beverages' burp and perk come about a second after the bottle goes
            start = Time.time;
            while (!stuck && drank && Time.time - start < 3.5f && !Got(id))
                yield return null;
            if (Got(id))
            {
                ZombiesPlugin.Log.LogInfo($"Zombies perks: {id} drunk, perk in");
                // The character's line for it (only the fallback path below gave one before: never heard, 2026-11-08)
                ZombiesVox.PerkAfter(id, 0.4f);
                yield break;
            }
            ZombiesPlugin.Log.LogWarning($"Zombies perks: the {id} drink didn't finish ({(stuck ? "stuck in the hands" : !drank ? "never in the hands" : "no perk after it")}; hands: {ZombiesTrace.HandsText(me)}): perk given, gun back out");
            give();
            if (InGear())
                try
                {
                    var removed = ItemManipulator.Remove(drink, me.InventoryController, false, true);
                    ZombiesItems.Removed(me.InventoryController, removed, drink);
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies perks: the {id} bottle couldn't be taken away: {e.Message}");
                }
            Rearm(me);
        }

        /// <summary>The last gun back in the hands (after a drink that went wrong left them empty or stuck).</summary>
        public static void Rearm(Player me)
        {
            if (me == null || me.HandsController is Player.FirearmController)
                return;
            try
            {
                me.TrySetLastEquippedWeapon(true, result => ZombiesPlugin.Log.LogInfo(
                    $"Zombies perks: gun back out: {(result.Failed ? "failed, " + result.Error : ZombiesTrace.HandsText(me))}"));
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies perks: couldn't put the gun back in the hands: {e.Message}");
            }
        }

        // Perk Beverages' perks cleared too (down)
        private static void ClearBeverages()
        {
            Look();
            try
            {
                _perkDrink?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
            }
            catch
            {
            }
        }

        public static Perk Find(string id) => All.FirstOrDefault(p => p.Id == id);

        public static void Give(string id)
        {
            Owned.Add(id);
            ZombiesPlugin.Log.LogInfo($"Zombies perks: {Find(id)?.Name ?? id}");
            ZombiesVox.Perk(id);
            ResetHandSpeeds();
        }

        /// <summary>Down: every perk lost .</summary>
        public static void LoseAll()
        {
            ClearBeverages();
            if (Owned.Count == 0)
                return;
            if (Owned.Contains("mule"))
                LoseThirdGun();
            Owned.Clear();
            ZombiesPlugin.Log.LogInfo("Zombies perks: lost (down)");
            ResetHandSpeeds();
        }

        // Mule Kick gone: the gun in its slot (the holster) goes with it
        private static void LoseThirdGun()
        {
            Player me = Me;
            if (!(me?.InventoryController.Inventory.Equipment.GetSlot(EquipmentSlot.Holster).ContainedItem is Weapon third))
                return;
            var removed = ItemManipulator.Remove(third, me.InventoryController, false, true);
            ZombiesItems.Removed(me.InventoryController, removed, third);
            ZombiesPlugin.Log.LogInfo($"Zombies perks: Mule Kick lost, {third.LocalizedName()} gone");
        }

        public static void Reset()
        {
            Owned.Clear();
            ClearBeverages();
        }

        // The hands' reload and draw speeds set again (Speed Cola gained or lost shows at once)
        private static void ResetHandSpeeds()
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            try
            {
                (me?.HandsAnimator as FirearmsAnimator)?.SetSpeedParameters();
                me?.MovementContext?.PlayerAnimator?.SetSpeedParameters();
            }
            catch
            {
            }
        }

        // ---------- The effects ----------

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            harmony.Patch(AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetSpeedParameters)),
                prefix: new HarmonyMethod(typeof(ZombiesPerks), nameof(HandsSpeed)));
            harmony.Patch(AccessTools.Method(typeof(PlayerAnimator), nameof(PlayerAnimator.SetSpeedParameters)),
                prefix: new HarmonyMethod(typeof(ZombiesPerks), nameof(BodySpeed)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Weapon), nameof(Weapon.FireRate)),
                postfix: new HarmonyMethod(typeof(ZombiesPerks), nameof(FasterFire)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(Weapon), nameof(Weapon.SingleFireRate)),
                postfix: new HarmonyMethod(typeof(ZombiesPerks), nameof(FasterFire)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MovementContext), nameof(MovementContext.MaxSpeed)),
                postfix: new HarmonyMethod(typeof(ZombiesPerks), nameof(FasterWalk)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(PhysicalBase), nameof(PhysicalBase.SprintSpeed)),
                postfix: new HarmonyMethod(typeof(ZombiesPerks), nameof(FasterSprint)));
            ZombiesPlugin.Log.LogInfo("Zombies perks: effects ready");
        }

        private static Player Me => Singleton<GameWorld>.Instance?.MainPlayer;

        // Speed Cola: the reload and draw speeds EFT sets, for your hands
        // Everyone in a zombies match swaps guns quicker (the original's quick swap; ZombiesStamina's "Weapon swap speed x", the
        // putting away and the drawing both), and Speed Cola's draw and reload ride on top
        private static void HandsSpeed(FirearmsAnimator __instance, ref float reload, ref float draw)
        {
            if (!ZombiesMatch.Active)
                return;
            // (the headless has no player of its own: every hands there are a teammate's)
            if (Me == null || !ReferenceEquals(Me.HandsAnimator, __instance))
            {
                // A teammate's hands here: played at the speeds their own game uses (else the host saw them still
                // reloading after they'd finished, and their next shots did little or nothing: the wall-buy shotgun,
                // user 2026-11-10)
                Player mate = Teammate(__instance);
                if (mate == null)
                    return;
                BaseSpeeds[__instance] = (reload, draw);
                if (RemoteSpeeds.TryGetValue(mate.ProfileId, out var rs))
                {
                    reload *= rs.reload;
                    draw *= rs.draw;
                }
                return;
            }
            float r = 1f, d = 1f;
            d *= ZombiesStamina.SwapSpeed * ZombiesQuickGrenade.DrawBoost;
            // Everyone reloads a touch faster than EFT's own (the user, 2026-11-07); Speed Cola on top
            r *= BaseReload * SlowReloaders(Me?.HandsController?.Item);
            if (Has("speed"))
            {
                bool wonder = ZombiesAmmo.IsWonderWeapon(Me?.HandsController?.Item);
                r *= wonder ? WonderSpeedColaReload : SpeedColaReload;
                d *= wonder ? WonderSpeedColaDraw : SpeedColaDraw;
            }
            reload *= r;
            draw *= d;
            // The others play our hands at the same speeds
            SendSpeeds(r, d);
        }

        // Ours to the others: reload and draw multipliers, and the Ray Gun's shot speed (Double Tap)
        private static void SendSpeeds(float r, float d)
        {
            float f = Has("dtap") ? DoubleTapRate : 1f;
            if (Mathf.Abs(r - _sentReload) > 0.01f || Mathf.Abs(d - _sentDraw) > 0.01f || Mathf.Abs(f - _sentFire) > 0.01f)
            {
                _sentReload = r;
                _sentDraw = d;
                _sentFire = f;
                ZombiesNet.SendHandsSpeed(r, d, f);
            }
        }

        private static float _sentReload = -1f, _sentDraw = -1f, _sentFire = -1f;
        private static readonly Dictionary<string, float> RemoteFire = new Dictionary<string, float>();
        private static readonly Dictionary<Player, Animator> TeamAnims = new Dictionary<Player, Animator>();
        private static readonly Dictionary<string, (float reload, float draw)> RemoteSpeeds = new Dictionary<string, (float, float)>();
        private static readonly Dictionary<FirearmsAnimator, (float reload, float draw)> BaseSpeeds = new Dictionary<FirearmsAnimator, (float, float)>();

        private static Player Teammate(FirearmsAnimator anim)
        {
            foreach (Player p in ZombiesTeam.List())
                if (p != null && !p.IsYourPlayer && ReferenceEquals(p.HandsAnimator, anim))
                    return p;
            return null;
        }

        /// <summary>A teammate's game says its hands speeds: their hands here are set to them at once.</summary>
        public static void RemoteHandsSpeed(string profileId, float reload, float draw, float fire)
        {
            RemoteSpeeds[profileId] = (reload, draw);
            RemoteFire[profileId] = fire;
            Player mate = ZombiesTeam.List().FirstOrDefault(p => p != null && p.ProfileId == profileId);
            if (mate?.HandsAnimator is FirearmsAnimator anim)
            {
                var b = BaseSpeeds.TryGetValue(anim, out var known) ? known : (1f, 1f);
                anim.SetSpeedParameters(b.Item1, b.Item2);
            }
            // Logger (user to confirm teammates see each other's reloads at the right speed, 2026-11-10); sent only on change
            ZombiesPlugin.Log.LogInfo($"Zombies perks: {mate?.Profile?.Nickname ?? profileId}'s hands speed here: reload x{reload:0.##}, draw x{draw:0.##}, fire x{fire:0.##} (animator {(mate?.HandsAnimator is FirearmsAnimator ? "set" : "not found yet")})");
        }

        public static void ClearHandsSpeeds()
        {
            RemoteSpeeds.Clear();
            BaseSpeeds.Clear();
            RemoteFire.Clear();
            TeamAnims.Clear();
            _sentReload = _sentDraw = _sentFire = -1f;
        }

        // Speed Cola on the Ray Gun and Thundergun: much less than on other guns (the user, 2026-11-07: their reloads and
        // draws are long scripted the original sequences that looked rushed at x2.2 / x2)
        private const float WonderSpeedColaReload = 1.3f, WonderSpeedColaDraw = 1.25f;

        private const float BaseReload = 1.15f;

        /// <summary>
        /// Shotguns and revolvers (the RSh-12) reload half as fast again by default (the user, 2026-11-08: their round-by-
        /// round reloads dragged); Speed Cola still goes on top.
        /// </summary>
        private const float SlowReloaderBoost = 1.5f;

        // RSh-12 up again (user, 2026-11-10: "needs to be much higher": was 2.4, x2.76 with the base)
        private const float LauncherReloadBoost = 2f, RevolverReloadBoost = 3.4f;

        // The UMP 45's reload, quicker (user, 2026-11-10)
        private const string UmpId = "5fc3e272f8b6a877a729eac5";
        private const float UmpReloadBoost = 1.5f;

        // LMGs (the RPD and M60: EFT's "machinegun" class): their belt reloads were too slow (user, 2026-11-10)
        private const float LmgReloadBoost = 1.6f;

        private static float SlowReloaders(EFT.InventoryLogic.Item item)
        {
            if (!(item is EFT.InventoryLogic.Weapon w) || ZombiesAmmo.IsWonderWeapon(w))
                return 1f;
            // The War Machine and the M16's M203 launcher: quicker again (user, 2026-11-09)
            if (w.TemplateId.ToString() == "6275303a9f372d6ea97f9ec7")
                return LauncherReloadBoost;
            if (Me?.HandsController is Player.FirearmController fc)
            {
                try
                {
                    if (fc.IsInLauncherMode())
                        return LauncherReloadBoost;
                }
                catch
                {
                }
            }
            bool shotgun = w.WeapClass == "shotgun";
            bool revolver = w.GetCurrentMagazine() is EFT.InventoryLogic.CylinderMagazine || w.WeapClass == "revolver";
            // Revolvers (the RSh-12) quicker again (user, 2026-11-09: "much quicker than it is")
            if (w.WeapClass == "machinegun")
                return LmgReloadBoost;
            if (w.TemplateId.ToString() == UmpId)
                return UmpReloadBoost;
            return revolver ? RevolverReloadBoost : shotgun ? SlowReloaderBoost : 1f;
        }

        private static void BodySpeed(PlayerAnimator __instance, ref float reload, ref float draw)
        {
            if (!ZombiesMatch.Active || Me?.MovementContext == null || !ReferenceEquals(Me.MovementContext.PlayerAnimator, __instance))
                return;
            draw *= ZombiesStamina.SwapSpeed;
            reload *= BaseReload * SlowReloaders(Me?.HandsController?.Item);
            if (!Has("speed"))
                return;
            bool wonder = ZombiesAmmo.IsWonderWeapon(Me?.HandsController?.Item);
            reload *= wonder ? WonderSpeedColaReload : SpeedColaReload;
            draw *= wonder ? WonderSpeedColaDraw : SpeedColaDraw;
        }

        // Double Tap on the Ray Gun: its held trigger refires as the shot animation ends (Raygun's RaygunAuto), so the
        // faster fire rate alone does nothing; the hands play the shot a third faster too (the original: 0.27 -> 0.2 s a shot in
        // the user's video). Only during its FIRE state; the animator's speed is put back after.
        private static Animator _fastHands;

        private static float _nextTeamHands;

        // A teammate's new hands (a gun swap makes new ones here, and EFT may never set their speeds): set to their speeds
        public static void TickTeamHands()
        {
            TeamAnimSpeeds();
            if (RemoteSpeeds.Count == 0 || Time.time < _nextTeamHands)
                return;
            _nextTeamHands = Time.time + 0.5f;
            foreach (Player p in ZombiesTeam.List())
            {
                if (p == null || p.IsYourPlayer || !RemoteSpeeds.ContainsKey(p.ProfileId) || !(p.HandsAnimator is FirearmsAnimator anim) || BaseSpeeds.ContainsKey(anim))
                    continue;
                anim.SetSpeedParameters(1f, 1f);
            }
        }

        // Each frame: a teammate's whole hands animation at the speed their own game plays it: a bolt-action gun's
        // (ZombiesKnife.BoltSpeed) and the Ray Gun's shot with their Double Tap; else 1
        private static void TeamAnimSpeeds()
        {
            if (!ZombiesMatch.Active)
                return;
            foreach (Player p in ZombiesTeam.List())
            {
                if (p == null || p.IsYourPlayer)
                    continue;
                GameObject hands = p.HandsController?.ControllerGameObject;
                if (hands == null)
                    continue;
                if (!TeamAnims.TryGetValue(p, out Animator a) || a == null || !a.transform.IsChildOf(hands.transform))
                    TeamAnims[p] = a = hands.GetComponentInChildren<Animator>();
                if (a == null)
                    continue;
                float want = 1f;
                if (p.HandsController.Item is Weapon w)
                {
                    if (w.BoltAction)
                        want = ZombiesKnife.BoltSpeed;
                    else if (w.TemplateId.ToString() == ZombiesAmmo.RaygunId && RemoteFire.TryGetValue(p.ProfileId, out float f) && f > 1f
                             && a.GetCurrentAnimatorStateInfo(Mathf.Max(0, a.GetLayerIndex("Hands"))).IsName("FIRE"))
                        want = f;
                }
                if (Mathf.Abs(a.speed - want) > 0.001f)
                    a.speed = want;
            }
        }

        public static void Tick(Player me)
        {
            TickTeamHands();
            var hands = me?.HandsController;
            bool on = ZombiesMatch.Active && Has("dtap") && hands?.Item != null
                      && hands.Item.TemplateId.ToString() == ZombiesAmmo.RaygunId && hands.ControllerGameObject != null;
            Animator a = !on ? null
                : _fastHands != null && _fastHands.transform.IsChildOf(hands.ControllerGameObject.transform) ? _fastHands
                : hands.ControllerGameObject.GetComponentInChildren<Animator>();
            if (_fastHands != null && _fastHands != a)
            {
                _fastHands.speed = 1f;
                _fastHands = null;
            }
            if (a == null)
                return;
            int layer = Mathf.Max(0, a.GetLayerIndex("Hands"));
            bool firing = a.GetCurrentAnimatorStateInfo(layer).IsName("FIRE");
            a.speed = firing ? DoubleTapRate : 1f;
            _fastHands = a;
        }

        // Double Tap II: your guns fire a third faster
        private static void FasterFire(Weapon __instance, ref int __result)
        {
            if (!ZombiesMatch.Active || __instance.Owner?.ID != Me?.ProfileId)
                return;
            // Revolvers (the RSh-12): the hammer cocks between shots at EFT's slow pace; much quicker in zombies
            // (user, 2026-11-09). EFT times the cock-and-fire cycle from the fire rate
            if (__instance.WeapClass == "revolver" || __instance.GetCurrentMagazine() is CylinderMagazine && __instance.WeapClass != "grenadeLauncher")
                __result = Mathf.RoundToInt(__result * RevolverFireRate);
            if (Has("dtap"))
                __result = Mathf.RoundToInt(__result * DoubleTapRate);
        }

        private const float RevolverFireRate = 2.2f;

        // Stamin-Up: walking a little faster
        private static void FasterWalk(MovementContext __instance, ref float __result)
        {
            if (!ZombiesMatch.Active || Me == null || !ReferenceEquals(Me.MovementContext, __instance))
                return;
            // Everyone a little quicker in zombies (F12 "Player speed x"), Stamin-Up on top
            __result *= ZombiesStamina.PlayerSpeed * (Has("stamin") ? StaminUpWalk : 1f);
        }

        // Stamin-Up: sprinting faster too
        private static void FasterSprint(PhysicalBase __instance, ref float __result)
        {
            if (!ZombiesMatch.Active || Me == null || !ReferenceEquals(Me.Physical, __instance))
                return;
            __result *= ZombiesStamina.PlayerSpeed * (Has("stamin") ? StaminUpWalk : 1f);
        }

        /// <summary>Double Tap II: your bullet's damage on a zombie doubled (ZombiesRules' damage prefix).</summary>
        public static float DamageFactor => ZombiesMatch.Active && Has("dtap") ? 2f : 1f;

        /// <summary>
        /// Deadshot Daiquiri as the original on PC: 35% less spread (EFT's own and the hipfire's, ZombiesHipfire) and no sway.
        /// No damage change (the original's head-snapping aim assist is a controller thing).
        /// </summary>
        public static bool EveryHitHead => false;
        public static float AimFactor => ZombiesMatch.Active && Has("deadshot") ? 0.65f : 1f;

        /// <summary>Deadshot's hipfire: the hip spread and its bloom 35% tighter.</summary>
        public static float HipFactor => ZombiesMatch.Active && Has("deadshot") ? 0.65f : 1f;

        /// <summary>Stamin-Up: sprinting drains half as fast (ZombiesStamina).</summary>
        public static float SprintDrainFactor => ZombiesMatch.Active && Has("stamin") ? 0.5f : 1f;
    }

    /// <summary>A perk machine (not Quick Revive): "Buy Jugger-Nog (2500)", once the power's on; four perks at most.</summary>
    internal class PerkMachineBuy : MonoBehaviour
    {
        private const float InteractDistance = 2.2f;
        private readonly ZombiesInteraction _menu = new ZombiesInteraction();
        private ZombiesPerks.Perk _perk;

        public void Init(string id) => _perk = ZombiesPerks.Find(id);

        private void Update()
        {
            if (_perk == null || !ZombiesMatch.Active)
                return;
            if (ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
        }

        private void OnDestroy() => _menu.Reset();

        private IEnumerable<(string, Action)> Options()
        {
            if (!ZombiesMatch.PowerIsOn)
                yield return ($"{_perk.Name} (needs the power)", null);
            else if (ZombiesPerks.Drinking)
                yield return ($"{_perk.Name}", null);
            else if (ZombiesPerks.Has(_perk.Id))
                yield return ($"{_perk.Name} (you have it)", null);
            else if (ZombiesPerks.Count >= ZombiesPerks.MaxPerks)
                yield return ($"{_perk.Name} ({ZombiesPerks.MaxPerks} perks at most)", null);
            else
                yield return ($"Buy {_perk.Name} ({_perk.Price})", ZombiesMatch.Afford(_perk.Price, Buy));
        }

        private void Buy()
        {
            if (ZombiesPerks.Drinking || ZombiesPerks.Has(_perk.Id) || ZombiesPerks.Count >= ZombiesPerks.MaxPerks || !ZombiesMatch.Spend(_perk.Price))
                return;
            string id = _perk.Id;
            ZombiesPerks.StartedDrinking(id);
            ZombiesPlugin.Log.LogInfo($"Zombies perks: bought {_perk.Name} for {_perk.Price} ({ZombiesPerks.Count + 1} of {ZombiesPerks.MaxPerks})");
            GetComponent<ZombiesMachineAudio>()?.Bought();
            ZombiesPlugin.Instance.StartCoroutine(ZombiesPerks.Drink(id, () => ZombiesPerks.Give(id)));
        }
    }
}
