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
            // EFT never vaults a sprinter (its own rule: no vault and no climb while sprinting) and wants the body standing
            // on the ground: the old loop teleported it a step nearer every frame while asking, so it was never grounded
            // ("can False" on most refusals, user's logs 2026-11-13), and sprinters were always refused. Now: sprint and
            // mover off, eased into place once, a moment to settle on the ground, then asked each frame
            try
            {
                z.Mover?.Stop();
                z.Sprint(false);
                body.MovementContext?.EnableSprint(false);
            }
            catch
            {
            }
            bool started = false;
            EVaultingStrategy chosen = EVaultingStrategy.None;
            Vector3 sill = w.position + w.forward * 0.5f;
            Vector3 start = body.Transform.position;
            sill.y = start.y;
            for (float t = 0f; t < 0.35f; t += Time.deltaTime)
            {
                if (body == null || !body.HealthController.IsAlive)
                    break;
                body.Teleport(Vector3.Lerp(start, sill, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.35f))), false);
                ZombiesRounds.Face(z, inside + Vector3.up * 1.2f);
                yield return null;
            }
            for (float t = 0f; t < 0.6f && !started; t += Time.deltaTime)
            {
                if (body == null || !body.HealthController.IsAlive)
                    break;
                ZombiesRounds.Face(z, inside + Vector3.up * 1.2f);
                // Settled first: two frames at least, and grounded
                if (t > 0.05f && Grounded(vc))
                {
                    try
                    {
                        _ours = true;
                        try
                        {
                            started = vc.TryVaulting();
                            // EFT's choice by its own rules: over a thin enough obstacle it vaults, else it climbs onto it
                            if (started)
                                chosen = vc.VaultingLength <= 0.41f && vc.VaultingHeight <= 1.1f ? EVaultingStrategy.Vault : EVaultingStrategy.Climb;
                        }
                        finally { _ours = false; }
                    }
                    catch (Exception e)
                    {
                        Note(body, "TryVaulting threw: " + e.Message);
                        break;
                    }
                }
                if (!started)
                    yield return null;
            }
            if (!started && body != null)
                Note(body, $"EFT won't vault here (strategy {vc.GetVaultingStrategy()}, can {vc.CanVaulting()}; {Why(vc)}); scripted climb instead");
            Record(b, vc, started ? chosen.ToString() : "Refused", started ? "" : Why(vc));
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
                    // A short step just past the window's face (not the old 1.8 m glide to the inside spot at sill height,
                    // which read as clipping through and hovering, user 2026-11-13), then the drop
                    Vector3 from = body.Transform.position;
                    Vector3 past = w.position - w.forward * 0.45f;
                    Vector3 edge = new Vector3(past.x, from.y + 0.05f, past.z);
                    float stepTime = Mathf.Clamp(Vector3.Distance(from, edge) / 2.2f, 0.15f, 0.45f);
                    for (float t = 0f; t < stepTime; t += Time.deltaTime)
                    {
                        if (body == null || !body.HealthController.IsAlive)
                            break;
                        body.Teleport(Vector3.Lerp(from, edge, Mathf.SmoothStep(0f, 1f, t / stepTime)), false);
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

        // ---------- What each barricade measures (user, 2026-11-13: "log the favoured heights, showable in the Unity map") ----------
        // Per barricade (its layout entry): every climb's obstacle as EFT measured it (top above the zombie's feet, depth)
        // and what EFT did: Vault (over), Climb (onto), Refused (and why). Written to logs\vault_spots.json beside the DLL;
        // the map project's Kennerz > Vault Guide draws it at each barricade with EFT's own height bands.
        private class Spot
        {
            public int Entry;
            public Vector3 At, Forward;
            public readonly List<string> Tries = new List<string>();
            public int Vaults, Climbs, Refused;
        }

        private static readonly Dictionary<int, Spot> Spots = new Dictionary<int, Spot>();
        private static float _nextWrite;

        private static void Record(ZombiesBarricade b, VaultingComponent vc, string result, string why)
        {
            try
            {
                if (b == null || vc == null)
                    return;
                if (!Spots.TryGetValue(b.Entry, out Spot s))
                    Spots[b.Entry] = s = new Spot { Entry = b.Entry, At = b.transform.position, Forward = b.transform.forward };
                if (result == "Vault")
                    s.Vaults++;
                else if (result == "Climb")
                    s.Climbs++;
                else
                    s.Refused++;
                string F(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                s.Tries.Add($"{{\"result\":\"{result}\",\"height\":{F(vc.VaultingHeight)},\"depth\":{F(vc.VaultingLength)},\"why\":\"{why.Replace("\"", "'")}\"}}");
                if (s.Tries.Count > 40)
                    s.Tries.RemoveAt(0);
                if (Time.time >= _nextWrite)
                {
                    _nextWrite = Time.time + 2f;
                    WriteSpots();
                }
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"[vault] record failed: {e.Message}");
            }
        }

        private static void WriteSpots()
        {
            string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"map\":\"").Append(Comfort.Common.Singleton<GameWorld>.Instance?.LocationId ?? "").Append("\",\"barricades\":[");
            bool first = true;
            foreach (Spot s in Spots.Values.OrderBy(x => x.Entry))
            {
                if (!first)
                    sb.Append(',');
                first = false;
                sb.Append("{\"entry\":").Append(s.Entry)
                  .Append(",\"at\":[").Append(F(s.At.x)).Append(',').Append(F(s.At.y)).Append(',').Append(F(s.At.z)).Append(']')
                  .Append(",\"forward\":[").Append(F(s.Forward.x)).Append(',').Append(F(s.Forward.y)).Append(',').Append(F(s.Forward.z)).Append(']')
                  .Append(",\"vaults\":").Append(s.Vaults).Append(",\"climbs\":").Append(s.Climbs).Append(",\"refused\":").Append(s.Refused)
                  .Append(",\"tries\":[").Append(string.Join(",", s.Tries)).Append("]}");
            }
            sb.Append("]}");
            string dir = System.IO.Path.Combine(ZombiesPlugin.Dir, "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "vault_spots.json"), sb.ToString());
        }

        // EFT's vault context: is the body standing on the ground (its CanVaulting needs it)
        private static bool Grounded(VaultingComponent vc)
        {
            try
            {
                return Traverse.Create(vc).Field("_vaultingContext").Property("IsGrounded").GetValue<bool>();
            }
            catch
            {
                return true;
            }
        }

        // Every input of EFT's vault decision (VaultingComponent.CanVaulting / GetVaultingStrategy), for the log
        private static string Why(VaultingComponent vc)
        {
            try
            {
                Traverse ctx = Traverse.Create(vc).Field("_vaultingContext");
                Traverse model = Traverse.Create(vc).Field("_vaultingModelDebug");
                Traverse obstacle = model.Property("ObstacleCalculatorModel");
                Traverse moves = model.Property("VaultingStatesModel");
                bool vaultMove = moves.Property("VaultState").Method("CanMove").GetValue<bool>();
                bool climbMove = moves.Property("ClimbState").Method("CanMove").GetValue<bool>();
                bool stairs = model.Property("StairsCalculatorModel").Method("IsStairsCondition").GetValue<bool>();
                return $"grounded {ctx.Property("IsGrounded").GetValue<bool>()}, moving {ctx.Property("MovementDirection").GetValue<Vector2>()}, " +
                       $"sprinting {ctx.Property("IsSprintEnabled").GetValue<bool>()}, terrain {obstacle.Property("IsTerrain").GetValue<bool>()}, " +
                       $"obstacle {vc.VaultingHeight:0.00} m high x {vc.VaultingLength:0.00} m deep, vault fits {vaultMove}, climb fits {climbMove}, stairs {stairs}";
            }
            catch (Exception e)
            {
                return "why unknown: " + e.Message;
            }
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
