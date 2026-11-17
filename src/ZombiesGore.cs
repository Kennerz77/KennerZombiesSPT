// SPZombies headshot gore (licence text: data\gore\LICENSE-gore.txt).
// Zombies-only, its own F12 section; steps aside when another mod's zombie gore is loaded (a skull would burst twice).
#nullable enable
using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Headshot gore on our zombies: a head hit can open the skull (the event's skull-open head meshes, data\gore\
    /// gore-heads.bin) with a blood burst and a wet hit sound (gore-splash.bundle). Settings, the pack and the hook that
    /// feeds it head hits; ZombiesGoreRuntime does the swap.
    /// Runs where the zombie's hit lands as EFT damage: solo and on the Fika host. Other Fika games don't see it yet.
    /// </summary>
    internal static class ZombiesGore
    {
        internal const string Section = "24. Zombie gore";
        private const string HalloweenGuid = "com.manimal.halloween2026";

        private static ZombiesGoreRuntime? _runtime;
        private static bool _failureLogged;

        internal static void Install(Harmony harmony, ConfigFile config)
        {
            var enabled = config.Bind(Section, "Enabled", true,
                "Headshots on zombies can open the skull with a blood burst. Applies live.");
            var headshotChance = config.Bind(Section, "Headshot chance", 0.35f,
                new ConfigDescription("Chance that a head hit the zombie survives opens the skull. Applies live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            var killingChance = config.Bind(Section, "Killing headshot chance", 0.75f,
                new ConfigDescription("Chance that a killing head hit opens the skull. Applies live.",
                    new AcceptableValueRange<float>(0f, 1f)));
            var bloodBurst = config.Bind(Section, "Blood burst", true,
                "Extra blood particles and splatter on walls and floor when a skull opens. Applies live.");
            var burstSound = config.Bind(Section, "Burst sound", true,
                "A wet head-hit sound when a skull opens. Applies live.");
            var meleeHits = config.Bind(Section, "Knife hits", false,
                "Let knife hits to the head open the skull too, not just bullets. Applies live.");

            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(HalloweenGuid))
            {
                ZombiesPlugin.Log.LogInfo("[gore] another zombie gore mod is installed: its gore runs, ours stays off");
                return;
            }

            try
            {
                var runtime = new ZombiesGoreRuntime(ZombiesPlugin.Log);
                void Apply(object? sender = null, EventArgs? args = null)
                {
                    runtime.Current.Enabled = enabled.Value;
                    runtime.Current.HeadshotChance = headshotChance.Value;
                    runtime.Current.KillingHeadshotChance = killingChance.Value;
                    runtime.Current.BloodBurst = bloodBurst.Value;
                    runtime.Current.BurstSound = burstSound.Value;
                    runtime.Current.MeleeHits = meleeHits.Value;
                }

                Apply();
                if (!runtime.LoadPack())
                    return;

                // The ApplyShot hooks go on at the first zombies match (PatchShots), once every mod's player types are
                // loaded: with Fika even a solo match's zombies are Fika's own player type with its own ApplyShot, which
                // a hook on EFT's Player.ApplyShot alone never sees (user's first test, 2026-11-15: no skull opened)
                _harmony = harmony;
                enabled.SettingChanged += Apply;
                headshotChance.SettingChanged += Apply;
                killingChance.SettingChanged += Apply;
                bloodBurst.SettingChanged += Apply;
                burstSound.SettingChanged += Apply;
                meleeHits.SettingChanged += Apply;
                _runtime = runtime;
                ZombiesPlugin.Log.LogInfo("[gore] zombie head gore armed");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogError($"[gore] zombie head gore could not be set up, everything else is unaffected: {e}");
                _runtime?.Shutdown();
                _runtime = null;
            }
        }

        private static Harmony? _harmony;
        private static bool _shotsPatched;

        /// <summary>
        /// Hooks every Player type's own ApplyShot (EFT's and Fika's subclasses), like ZombiesRules.PatchDamage. Called
        /// at match build; once per game start.
        /// </summary>
        internal static void PatchShots()
        {
            if (_shotsPatched || _harmony == null || _runtime == null)
                return;
            _shotsPatched = true;
            var postfix = new HarmonyMethod(typeof(ZombiesGore).GetMethod(nameof(ShotApplied), BindingFlags.Static | BindingFlags.NonPublic));
            int patched = 0;
            foreach (Type t in AccessTools.AllTypes())
            {
                if (!typeof(Player).IsAssignableFrom(t))
                    continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    // ApplyClientShot: Fika's own on a co-op player's game, for that player's hits on another game's zombie
                    // (ObservedClientBridge calls it instead of ApplyShot: no gore in co-op until hooked, 2026-11-17)
                    if (m.Name != nameof(Player.ApplyShot) && m.Name != "ApplyClientShot" || m.ReturnType == typeof(void) || m.IsAbstract || m.GetMethodBody() == null)
                        continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length < 2 || p[0].ParameterType != typeof(DamageInfo) || p[1].ParameterType != typeof(EBodyPart))
                        continue;
                    try
                    {
                        _harmony.Patch(m, postfix: postfix);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        ZombiesPlugin.Log.LogWarning($"[gore] {t.Name}.ApplyShot not hooked ({e.Message})");
                    }
                }
            }
            ZombiesPlugin.Log.LogInfo($"[gore] head hits hooked: {patched} method(s)");
        }

        // An override that calls the base runs both hooks for one hit: only the first counts
        private static bool _headHookSeen;
        private static int _lastFrame = -1;
        private static Player? _lastPlayer;
        private static Vector3 _lastPoint;

        /// <summary>A new match: forget whose heads were opened.</summary>
        internal static void NewMatch() => _runtime?.NewMatch();

        /// <summary>The host opened this zombie's head: the same on this game.</summary>
        internal static void Remote(string profileId, Vector3 point, Vector3 direction)
        {
            try
            {
                var world = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
                Player? zombie = world?.AllPlayersEverExisted.FirstOrDefault(p => p != null && p.ProfileId == profileId);
                if (zombie != null)
                    _runtime?.OnRemote(zombie, point, direction);
            }
            catch (Exception e)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    ZombiesPlugin.Log.LogError($"[gore] the host's gore couldn't be shown here: {e}");
                }
            }
        }

        // By position (__0, __1): Fika's ObservedPlayer names the damage "DamageInfo", EFT "damageInfo"
        private static void ShotApplied(Player __instance, DamageInfo __0, EBodyPart __1, object __result)
        {
            // null result = the shot was rejected (already dead, friendly fire, a hit our rules dropped)
            var runtime = _runtime;
            if (__1 == EBodyPart.Head && _headHookSeen != ZombiesMatch.Active && ZombiesMatch.Active)
            {
                _headHookSeen = true;
                ZombiesPlugin.Log.LogInfo($"[gore] head hit hook ran: {__instance?.GetType().Name}, shot {(__result == null ? "refused" : "taken")} (logged once per game)");
            }
            if (__result == null || runtime == null || __1 != EBodyPart.Head)
                return;
            if (Time.frameCount == _lastFrame && __instance == _lastPlayer && (__0.HitPoint - _lastPoint).sqrMagnitude < 0.0001f)
                return;
            _lastFrame = Time.frameCount;
            _lastPlayer = __instance;
            _lastPoint = __0.HitPoint;

            try
            {
                runtime.OnShotApplied(__instance, __0, __1);
            }
            catch (Exception e)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    ZombiesPlugin.Log.LogError($"[gore] zombie gore failed, the hit itself is unaffected: {e}");
                }
            }
        }
    }
}
