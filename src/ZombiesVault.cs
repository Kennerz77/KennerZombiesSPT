using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using EFT.Vaulting;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Zombies climb in over a barricade with EFT's own vault (the real one players use: its states, root motion and
    /// animation), not a scripted float. EFT never gives bots a VaultingComponent (Player.InitVaultingComponent returns
    /// at once for AI); in a zombies match the bots get one, built by EFT's own code (the prefix just says "not AI").
    /// Bots never auto-vault (their auto-vault setting is off): only Over() starts one, at a barricade.
    /// Over(): the zombie stands at the sill facing in, the window's own colliders (solid for players) are switched off
    /// for the climb, VaultingComponent.TryVaulting() runs EFT's check (obstacle height and room); if EFT says no, the
    /// caller falls back to the old arc. Logged: "[vault] ..." so sills can be tuned in the map.
    /// </summary>
    internal static class ZombiesVault
    {
        public static ConfigEntry<bool> RealVault;
        private static bool _patched;
        // EFT's own public getter (the field is typed as its interface; a typed field ref to it threw at load and took
        // the whole zombies set-up down with it, 2026-11-08)
        private static VaultingComponent Component(Player p) => p?.VaultingComponent as VaultingComponent;
        private static readonly HashSet<string> Logged = new HashSet<string>();

        public static void Bind(ConfigFile config, string section)
        {
            RealVault = config.Bind(section, "Zombies climb in with the real EFT vault", true,
                "At a barricade a zombie does EFT's own vault over the sill (proper animation and movement). Off, or when EFT says the sill can't be vaulted: the old scripted climb.");
            PlayersVault = config.Bind(section, "Players can vault", false,
                "Off: players can't vault or climb onto things in zombies matches; only zombies climbing in at barricades vault. Normal raids are never touched.");
        }

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.InitVaultingComponent)),
                prefix: new HarmonyMethod(typeof(ZombiesVault), nameof(GiveBotsVaulting)),
                finalizer: new HarmonyMethod(typeof(ZombiesVault), nameof(NeverBreakSpawn)));
            // Players never vault in zombies (the original has no vault; the user, 2026-11-08): every vault EFT starts goes
            // through DoVaultingByStrategy (key press, auto vault), so only our own call for a zombie gets through
            harmony.Patch(AccessTools.Method(typeof(VaultingComponent), nameof(VaultingComponent.DoVaultingByStrategy)),
                prefix: new HarmonyMethod(typeof(ZombiesVault), nameof(OnlyZombiesVault)));
        }

        public static ConfigEntry<bool> PlayersVault;
        private static bool _ours;

        private static bool OnlyZombiesVault(ref bool __result)
        {
            if (!ZombiesMatch.Active || _ours || PlayersVault?.Value == true)
                return true;
            __result = false;
            return false;
        }

        // EFT's player vault set-up on a bot must never stop the bot from spawning: anything it throws is logged and
        // swallowed (that zombie then climbs in the scripted way: no component)
        private static Exception NeverBreakSpawn(Exception __exception, Player __instance)
        {
            if (__exception != null)
            {
                ZombiesPlugin.Log.LogWarning($"[vault] EFT's vault set-up failed on {__instance?.Profile?.Nickname}: {__exception.Message} (scripted climb for it)");
                try { AccessTools.Field(typeof(Player), "_vaultingComponent")?.SetValue(__instance, null); } catch { }
            }
            return null;
        }

        // EFT: if (aiControlled) return; -> in a zombies match a bot is built like a player here
        private static void GiveBotsVaulting(ref bool __0)
        {
            if (__0 && ZombiesMatch.Active)
                __0 = false;
        }

        /// <summary>
        /// The real vault over a barricade. Returns false at once (nothing done) when it can't run, so the caller
        /// scripts the climb; true when EFT vaulted (then the zombie is brought fully inside if it stopped short).
        /// </summary>
        // A barricade's colliders switched off for a climb come back on by this time whatever happens to the climb (a
        // zombie killed or removed mid-vault stopped its coroutine and left the window open: the user walked out of the
        // map through the barricade by the wheelbarrow, 2026-11-08)
        private const float BackOnAfter = 6f;
        private static readonly System.Collections.Generic.Dictionary<Collider, float> Pending = new System.Collections.Generic.Dictionary<Collider, float>();

        /// <summary>Each frame of a match: colliders left off by a climb that never finished are put back.</summary>
        public static void Tick()
        {
            if (Pending.Count == 0)
                return;
            float now = Time.time;
            foreach (var kv in Pending.Where(kv => kv.Key == null || now >= kv.Value).ToList())
            {
                if (kv.Key != null && !kv.Key.enabled)
                {
                    kv.Key.enabled = true;
                    ZombiesPlugin.Log.LogInfo($"[vault] {kv.Key.name} was still off after a climb: back on");
                }
                Pending.Remove(kv.Key);
            }
        }

        public static IEnumerator Over(BotOwner z, ZombiesBarricade b, Vector3 inside, Action<bool> done)
        {
            Player body = z?.GetPlayer;
            VaultingComponent vc = body != null && RealVault?.Value != false ? Component(body) : null;
            if (vc == null || b == null)
            {
                Note(body, "no vaulting on this bot (EFT gave it none)");
                done(false);
                yield break;
            }
            Transform w = b.transform;
            // Face straight in for a moment (EFT's check looks along the body's forward)
            for (float t = 0f; t < 0.3f; t += Time.deltaTime)
            {
                if (body == null || !body.HealthController.IsAlive)
                {
                    done(false);
                    yield break;
                }
                ZombiesRounds.Face(z, inside + Vector3.up * 1.2f);
                yield return null;
            }
            // The window's own colliders (players can't get through) off for the climb
            var off = b.GetComponentsInChildren<Collider>(false).Where(c => c.enabled).ToList();
            foreach (Collider c in off)
            {
                c.enabled = false;
                Pending[c] = Time.time + BackOnAfter;
            }
            // EFT's vault check (its own per-frame look for an obstacle close in front) found nothing from the outside spot
            // 0.8 m out: "strategy None" on 12 of 13 climbs (2026-11-09 log). So: eased up to the sill, trying each frame
            bool started = false;
            Vector3 sill = w.position + w.forward * 0.5f;
            Vector3 start = body.Transform.position;
            sill.y = start.y;
            for (float t = 0f; t < 0.6f && !started; t += Time.deltaTime)
            {
                if (body == null || !body.HealthController.IsAlive)
                    break;
                body.Teleport(Vector3.Lerp(start, sill, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.35f))), false);
                ZombiesRounds.Face(z, inside + Vector3.up * 1.2f);
                try
                {
                    _ours = true;
                    try { started = vc.TryVaulting(); }
                    finally { _ours = false; }
                }
                catch (Exception e)
                {
                    Note(body, "TryVaulting threw: " + e.Message);
                    break;
                }
                if (!started)
                    yield return null;
            }
            if (!started && body != null)
                Note(body, $"EFT won't vault here (strategy {vc.GetVaultingStrategy()}, can {vc.CanVaulting()}); scripted climb instead");
            if (!started)
            {
                foreach (Collider c in off)
                    if (c != null)
                        c.enabled = true;
                done(false);
                yield break;
            }
            // EFT moves it now; wait for the vault to end (at most 4 s)
            float until = Time.time + 4f;
            yield return null;
            while (body != null && body.HealthController.IsAlive && Time.time < until && SafeIsVaulting(vc))
            {
                ZombiesRounds.Face(z, inside + Vector3.up * 1.2f);
                yield return null;
            }
            foreach (Collider c in off)
                if (c != null)
                    c.enabled = true;
            if (body != null && body.HealthController.IsAlive)
            {
                // Still on the outside of the window's face (a climb onto the sill, or cut short): a short walk in
                float side = Vector3.Dot(body.Transform.position - w.position, w.forward);
                if (side > -0.3f)
                {
                    // EFT climbs it ONTO the sill (0.55-0.6 m out, every time in the 2026-11-09 log). The old step glided
                    // it down to the floor inside: "hovering over the barricade". Now it steps forward at the sill's
                    // height, off the inner edge, and falls to the floor itself (falls don't hurt zombies)
                    Vector3 from = body.Transform.position;
                    Vector3 edge = new Vector3(inside.x, from.y + 0.05f, inside.z);
                    for (float t = 0f; t < 0.3f; t += Time.deltaTime)
                    {
                        if (body == null || !body.HealthController.IsAlive)
                            break;
                        body.Teleport(Vector3.Lerp(from, edge, t / 0.3f), false);
                        yield return null;
                    }
                    // Let go: gravity takes it down (a short wait so the climb counts as done only once it's landed)
                    for (float t = 0f; t < 0.6f; t += Time.deltaTime)
                    {
                        if (body == null || !body.HealthController.IsAlive || body.MovementContext?.IsGrounded == true && t > 0.1f)
                            break;
                        yield return null;
                    }
                    Note(body, $"vaulted onto the sill ({side:0.00} m out): stepped off the inside and dropped");
                }
                else
                    Note(body, "vaulted in");
            }
            done(true);
        }

        private static bool SafeIsVaulting(VaultingComponent vc)
        {
            try { return vc.IsVaulting(); }
            catch { return false; }
        }

        // One line per kind of outcome (so the log shows how the sills behave without spam)
        private static void Note(Player body, string what)
        {
            if (Logged.Add(what.Split('(')[0]))
                ZombiesPlugin.Log.LogInfo($"[vault] {what}");
            else
                ZombiesDebug.Log($"[vault] {what}");
        }
    }
}
