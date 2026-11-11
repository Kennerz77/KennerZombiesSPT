using System;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombies match's health and damage rules, for players only (zombies keep EFT's rules), and only in a zombies
    /// match: no friendly fire from anything a player does (guns, grenades, their own explosions); none of EFT's
    /// debuffs (bleeds, fractures, contusion, stun, pain, intoxication, the infected bots' infection, hunger and thirst).
    /// The perk drinks' blur is a camera effect of their own and isn't touched. Each Fika client runs its own player's
    /// health, so every client applies the same rules to its own player. Arcade guns: no inspect or ammo checks.
    /// </summary>
    internal static class ZombiesRules
    {
        private static Harmony _harmony;
        private static bool _damagePatched;
        private static float _nextTopUp;

        public static void Patch(Harmony harmony)
        {
            _harmony = harmony;
            Type hc = typeof(ActiveHealthController);
            Prefix(hc, nameof(ActiveHealthController.TryApplySideEffects), nameof(NoSideEffects));
            Prefix(hc, nameof(ActiveHealthController.DoFracture), nameof(SkipForPlayer));
            Prefix(hc, nameof(ActiveHealthController.DoStun), nameof(SkipForPlayer));
            Prefix(hc, nameof(ActiveHealthController.DoContusion), nameof(SkipForPlayer));
            Prefix(hc, nameof(ActiveHealthController.BluntContusion), nameof(SkipForPlayer));
            Prefix(hc, nameof(ActiveHealthController.TryAddZombieInfection), nameof(SkipForPlayer));
            Prefix(hc, nameof(ActiveHealthController.DoIntoxication), nameof(NoEffect));
            harmony.Patch(AccessTools.Method(hc, nameof(ActiveHealthController.DoBleed), new[] { typeof(bool), typeof(EBodyPart) }),
                prefix: new HarmonyMethod(typeof(ZombiesRules), nameof(SkipForPlayer)));
            Prefix(hc, nameof(ActiveHealthController.ChangeEnergy), nameof(NoDrain));
            Prefix(hc, nameof(ActiveHealthController.ChangeHydration), nameof(NoDrain));
            // No vaulting or climbing over things (manual or automatic): players can't slip out over walls and windows
            foreach (string property in new[] { "CanVault", "CanClimb" })
            {
                MethodInfo getter = AccessTools.PropertyGetter(typeof(Physical), property);
                if (getter != null)
                    harmony.Patch(getter, prefix: new HarmonyMethod(typeof(ZombiesRules), nameof(NoVault)));
                else
                    ZombiesPlugin.Log.LogWarning($"Zombies rules: Physical.{property} not found (vaulting stays on)");
            }
            // No swapping the gun to the other shoulder (EFT's left stance, user 2026-11-11): every way in (its toggle and
            // set keys, Fika's copy of a teammate) sets LeftStanceController.LeftStance, so that setter keeps it off
            MethodInfo leftStance = AccessTools.PropertySetter(typeof(LeftStanceController), nameof(LeftStanceController.LeftStance));
            if (leftStance != null)
                harmony.Patch(leftStance, prefix: new HarmonyMethod(typeof(ZombiesRules), nameof(NoLeftStance)));
            else
                ZombiesPlugin.Log.LogWarning("Zombies rules: LeftStanceController.LeftStance not found (hand swapping stays on)");
        }

        private static void NoLeftStance(ref bool value)
        {
            if (ZombiesMatch.Active)
                value = false;
        }

        /// <summary>The match starts: a player who came in with the gun on the left shoulder has it put back on the right.</summary>
        public static void RightStance(Player player)
        {
            try
            {
                LeftStanceController stance = player?.MovementContext?.LeftStanceController;
                if (stance != null && stance.LeftStance)
                {
                    stance.SetLeftStanceForce(false);
                    ZombiesPlugin.Log.LogInfo("Zombies rules: left stance was on: back to the right shoulder");
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies rules: couldn't reset the left stance: {e.Message}");
            }
        }

        /// <summary>
        /// Arcade guns: no inspecting (L), mag check, chamber check, fire mode check or sight zeroing (its distance isn't
        /// shown either) for your own player in a zombies match; the rounds are on the HUD. Every hands controller that has them (EFT's and Fika's) is patched.
        /// </summary>
        public static void PatchNoChecks(Harmony harmony)
        {
            if (_checksPatched)
                return;
            _checksPatched = true;
            int patched = 0;
            foreach (Type t in AccessTools.AllTypes().Where(t => typeof(Player.ItemHandsController).IsAssignableFrom(t)))
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                             .Where(m => Checks.Contains(m.Name) && !m.IsAbstract))
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesRules), m.ReturnType == typeof(bool) ? nameof(NoCheckBool) : nameof(NoCheck)));
                    patched++;
                }
            // The sight's zeroing distance ("50" by the ammo): never shown
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.ShowAmmoCountZeroingPanel)),
                prefix: new HarmonyMethod(typeof(ZombiesRules), nameof(NoZeroingPanel)));
            ZombiesPlugin.Log.LogInfo($"Zombies rules: no inspect or ammo checks ({patched} methods)");
        }

        private static bool _checksPatched;

        // Inspect, the ammo checks, and zeroing the sight up / down
        private static readonly string[] Checks = { "ExamineWeapon", "CheckAmmo", "CheckChamber", "CheckFireMode", "OpticCalibrationSwitchUp", "OpticCalibrationSwitchDown" };

        private static bool NoZeroingPanel(Player __instance) => !(ZombiesMatch.Active && __instance.IsYourPlayer);

        private static bool Blocked(Player.ItemHandsController hands) => ZombiesMatch.Active && hands?._player != null && hands._player.IsYourPlayer;

        private static bool NoCheck(Player.ItemHandsController __instance) => !Blocked(__instance);

        private static bool NoCheckBool(Player.ItemHandsController __instance, ref bool __result)
        {
            if (!Blocked(__instance))
                return true;
            __result = false;
            return false;
        }

        private static readonly FieldInfo PhysicalPlayer = AccessTools.Field(typeof(Physical), "_player");

        private static bool NoVault(Physical __instance, ref bool __result)
        {
            if (!ZombiesMatch.Active || ZombiesDebug.VaultingOff?.Value == false || !(PhysicalPlayer?.GetValue(__instance) is IPlayer player) || player.IsAI)
                return true;
            __result = false;
            return false;
        }

        private static void Prefix(Type type, string method, string prefix)
        {
            MethodInfo target = AccessTools.Method(type, method);
            if (target == null)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies rules: {type.Name}.{method} not found");
                return;
            }
            _harmony.Patch(target, prefix: new HarmonyMethod(typeof(ZombiesRules), prefix));
        }

        private static bool ForPlayer(ActiveHealthController hc) =>
            ZombiesMatch.Active && ZombiesDebug.DebuffsOff?.Value != false && hc?.Player != null && !hc.Player.IsAI;

        private static bool SkipForPlayer(ActiveHealthController __instance) => !ForPlayer(__instance);

        private static bool NoEffect(ActiveHealthController __instance, ref ActiveHealthController.Effect __result)
        {
            if (!ForPlayer(__instance))
                return true;
            __result = null;
            return false;
        }

        private static bool NoSideEffects(ActiveHealthController __instance, ref bool __result)
        {
            if (!ForPlayer(__instance))
                return true;
            __result = false;
            return false;
        }

        // Hunger and thirst never go down (food and drink can still add)
        private static bool NoDrain(ActiveHealthController __instance, float value) => value >= 0f || !ForPlayer(__instance);

        // ---------- Friendly fire ----------

        /// <summary>
        /// Every Player type's own ApplyShot / ApplyDamageInfo (EFT's, and Fika's subclasses, found at the first zombies
        /// match once every mod is loaded): a player's damage never lands on a player, themselves included.
        /// </summary>
        public static void PatchDamage()
        {
            if (_damagePatched || _harmony == null)
                return;
            _damagePatched = true;
            int patched = 0;
            foreach (Type t in AccessTools.AllTypes().Where(t => typeof(Player).IsAssignableFrom(t)))
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.GetMethodBody() == null)
                        continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 0 || p[0].ParameterType != typeof(DamageInfo))
                        continue;
                    // The damage is taken by position (__0): Fika's ObservedPlayer names it "DamageInfo", EFT "damageInfo".
                    // One method that won't patch is skipped, never the rest (nor the match set-up after it)
                    string prefix;
                    if (m.Name == nameof(Player.ApplyShot) && m.ReturnType != typeof(void))
                        prefix = nameof(NoFriendlyShot);
                    else if (m.Name == nameof(Player.ApplyDamageInfo))
                        prefix = nameof(NoFriendlyDamage);
                    else
                        continue;
                    try
                    {
                        _harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesRules), prefix));
                        patched++;
                    }
                    catch (Exception e)
                    {
                        ZombiesPlugin.Log.LogWarning($"Zombies rules: {t.Name}.{m.Name} not patched ({e.Message})");
                    }
                }
            ZombiesPlugin.Log.LogInfo($"Zombies rules: friendly fire off ({patched} damage methods)");
        }

        /// <summary>
        /// Damage on a player in a zombies match never lands as EFT damage: a zombie's hit is a slap (counted on the
        /// player's own client), anything from a player (friendly fire, own explosions) or the world does nothing.
        /// </summary>
        private static int _hitFrame = -1;
        private static Player _hitTarget;
        private static Vector3 _hitPoint;
        private static EDamageType _hitType;

        /// <summary>
        /// A hit of ours on a zombie (knife swipe, grenade/shell blast, PHD). On a Fika client the zombie is the host's
        /// and its copy only records a direct hit (ObservedPlayer.ApplyDamageInfo): the shot path sends it to the host.
        /// Hits made with the direct call did nothing on clients (user, 2026-11-09: their knife, grenades, shells).
        /// </summary>
        public static void HitZombie(Player zombie, DamageInfo info)
        {
            if (zombie == null)
                return;
            if (ZombiesNet.Online && !ZombiesNet.IsHost)
                zombie.ApplyShot(info, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, (EArmorPlateCollider)0, default);
            else
                zombie.ApplyDamageInfo(info, EBodyPart.Chest, EBodyPartColliderType.RibcageUp, 0f);
        }

        private static int _blastFrame = -1;
        private static readonly System.Collections.Generic.Dictionary<Player, (Vector3 at, float damage)> Blasted = new System.Collections.Generic.Dictionary<Player, (Vector3 at, float damage)>();

        private static bool OtherPartOfBlast(Player target, DamageInfo hit)
        {
            if (hit.DamageType != EDamageType.Explosion && hit.DamageType != EDamageType.GrenadeFragment)
                return false;
            if (_blastFrame != Time.frameCount)
            {
                _blastFrame = Time.frameCount;
                Blasted.Clear();
            }
            // A much bigger hit than the first one this frame is a different blast, not another body part: EFT's own
            // 40mm fragments (small) land first in the same frame as our the original shell blast (big), and the the original blast was
            // being dropped as "another part" (War Machine / M203 needed 2-3 shells on round 3, user 2026-11-09)
            if (!Blasted.TryGetValue(target, out var first) || hit.Damage > first.damage * 2f)
            {
                Blasted[target] = (hit.HitPoint, hit.Damage);
                return false;
            }
            return (hit.HitPoint - first.at).sqrMagnitude >= 0.0001f;
        }

        private static bool SameHit(Player target, DamageInfo hit)
        {
            bool same = Time.frameCount == _hitFrame && target == _hitTarget && hit.DamageType == _hitType && (hit.HitPoint - _hitPoint).sqrMagnitude < 0.0001f;
            _hitFrame = Time.frameCount;
            _hitTarget = target;
            _hitPoint = hit.HitPoint;
            _hitType = hit.DamageType;
            return same;
        }

        private static bool Friendly(Player target, ref DamageInfo damageInfo)
        {
            if (!ZombiesMatch.Active || target == null)
                return false;
            IPlayer from = damageInfo.Player?.iPlayer;
            // A player's hit on a zombie: the original's 10 points to the shooter, then the original's damage (ZombiesHealth)
            if (ZombiesTeam.IsZombie(target))
            {
                // The same hit comes through several of EFT's damage methods we hook (ApplyDamageInfo and the ones it
                // calls): only the first counts. The knife's flat 150 came off three times (a round 1-3 zombie dropped
                // to one stab, +10 points thrice) and every bullet paid its 10 points several times (the user's bugs.mp4)
                if (SameHit(target, damageInfo))
                    return false;
                // A blast reaches every body part of every zombie in it, each its own hit: only the first part of
                // each zombie counts (the Ray Gun paid ~14 x 10 points a shot and would land its splash as often:
                // user, 2026-11-09); the other parts' hits are dropped
                // A 40mm shell's own EFT blast: dropped, our the original blast (ZombiesShells) is the shell's damage
                if (ZombiesShells.DropEft(damageInfo))
                    return true;
                if (OtherPartOfBlast(target, damageInfo))
                    return true;
                // The original's knife already landed on the key press (ZombiesSwipe): EFT's own later stab is dropped
                if (ZombiesSwipe.DropEftHit(damageInfo, from))
                    return true;
                // (Zombies looked unkillable while climbing in: every hit on a busy one logged to see where it goes)
                if (ZombiesRounds.IsBusy(target.AIData?.BotOwner))
                    ZombiesDebug.Log($"hit on a busy zombie (climbing/swinging): {damageInfo.DamageType} {damageInfo.Damage:0} from {(from != null && from.IsYourPlayer ? "you" : from?.ProfileId)}");
                // A teammate's hit, here on the host (Fika sent it): the host pays their 10 points, as it credits kills
                // (their own game missed most hits: user, 2026-11-09)
                if (from != null && !from.IsYourPlayer && ZombiesNet.IsHost && ZombiesNet.Online && target.HealthController?.IsAlive == true
                    && !ZombiesTeam.IsZombieSource(from))
                    ZombiesNet.HitBy(from.ProfileId);
                if (from != null && from.IsYourPlayer && target.HealthController?.IsAlive == true)
                {
                    // Offline or the host: your own 10 points here; a Fika client gets them from the host
                    if (!ZombiesNet.Online || ZombiesNet.IsHost)
                        ZombiesStats.Hit();
                    // PHD Flopper: the knife blows up round the zombie now and then
                    if (damageInfo.DamageType == EDamageType.Melee)
                        ZombiesPhd.KnifeHit(Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer, damageInfo.HitPoint, damageInfo.Weapon);
                    // The shooter's perks go into the damage (sent to the host as it is, with Fika)
                    damageInfo.Damage *= ZombiesPerks.DamageFactor;
                    // Deadshot: every bullet lands as a headshot (the head's bonus, wherever it hit)
                    if (ZombiesPerks.EveryHitHead && damageInfo.DamageType != EDamageType.Melee && damageInfo.DamageType != EDamageType.Explosion
                        && damageInfo.DamageType != EDamageType.GrenadeFragment)
                    {
                        EBodyPart at = damageInfo.HittedBallisticCollider is BodyPartCollider part ? part.BodyPartType : EBodyPart.Stomach;
                        if (at != EBodyPart.Head)
                            damageInfo.Damage *= ZombiesHealth.HeadBonus / (at == EBodyPart.Chest ? 1.2f : 1f);
                    }
                }
                // The zombie's health pool, where the zombies live (solo, or the host for everyone's hits)
                if (from != null && !ZombiesTeam.IsZombieSource(from) && ZombiesNet.IsHost)
                    ZombiesHealth.Hit(target, ref damageInfo);
                return false;
            }
            // Your own blast (Sally's shell, the Ray Gun's bolt) near you: a slap without PHD Flopper
            if (target.IsYourPlayer && from != null && from.ProfileId == target.ProfileId
                && (damageInfo.DamageType == EDamageType.Explosion || damageInfo.DamageType == EDamageType.GrenadeFragment))
                ZombiesPhd.SelfBlast(target, damageInfo.MasterOrigin != Vector3.zero ? damageInfo.MasterOrigin : target.Transform.position, "explosion");
            // A zombie's own hit on a player does nothing: only its slap (swung, ZombiesRounds) counts, so a crowd
            // can't tag you down in an instant
            // F12 can let players hurt each other again (EFT's damage)
            if (from != null && !from.IsAI && ZombiesDebug.FriendlyFireOff?.Value == false)
                return false;
            return true;
        }

        // The body part EFT passes beside the hit (2nd argument of ApplyShot / ApplyDamageInfo): a teammate's hit on
        // a Fika host has no hit collider, so headshots counted as body shots on a headless (user, 2026-11-09)
        internal static EBodyPart? HitPart;

        private static void TakePart(object[] args) =>
            HitPart = args != null && args.Length > 1 && args[1] is EBodyPart part ? part : (EBodyPart?)null;

        private static bool NoFriendlyShot(Player __instance, ref DamageInfo __0, ref PlayerHitInfo __result, object[] __args)
        {
            TakePart(__args);
            if (!Friendly(__instance, ref __0))
                return true;
            __result = null;
            return false;
        }

        private static bool NoFriendlyDamage(Player __instance, ref DamageInfo __0, object[] __args)
        {
            TakePart(__args);
            return !Friendly(__instance, ref __0);
        }

        // ---------- Each frame of a match ----------

        /// <summary>Keeps the player fed and watered, and clears anything EFT managed to put on them.</summary>
        public static void Tick(Player player)
        {
            if (player?.ActiveHealthController == null || Time.time < _nextTopUp)
                return;
            _nextTopUp = Time.time + 0.5f;
            ActiveHealthController hc = player.ActiveHealthController;
            float energy = hc.Energy.Maximum - hc.Energy.Current;
            if (energy > 0.01f)
                hc.ChangeEnergy(energy);
            float hydration = hc.Hydration.Maximum - hc.Hydration.Current;
            if (hydration > 0.01f)
                hc.ChangeHydration(hydration);
            foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
                if (part != EBodyPart.Common)
                    hc.RemoveNegativeEffects(part);
        }
    }
}
