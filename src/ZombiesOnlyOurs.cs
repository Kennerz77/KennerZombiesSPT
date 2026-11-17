using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using EFT;
using HarmonyLib;

namespace KennerZombies
{
    /// <summary>
    /// A zombies match is strictly its 1-4 players and our own zombies (user, 2026-11-12). Other mods add AI to raids
    /// (squads of teammates, a dog, bosses, extra waves, PMC bots); a zombies map looks like any raid to them. Normal
    /// raids are never touched, so those mods can stay installed. In a zombies match, on the game that runs the bots
    /// (solo, the Fika host or the headless):
    /// - EFT's spawner refuses every bot whose role isn't a zombie's, at the one step every spawn goes through
    ///   (BotSpawner's inner "make this bot" call: waves, bosses, forced spawns and other mods' own calls alike), the
    ///   same way EFT itself drops a cancelled spawn. Our zombies (EFT's infected role) pass.
    /// - Every AI the core didn't spawn is removed as soon as it shows up (ZombiesRounds.RemoveStrays): bots made some
    ///   other way, or with a zombie's role by someone else.
    /// The server clears other mods' boss spawns and waves for zombies maps too (server ZombiesNoBotsPatch). Those mods
    /// are never changed: they may log that a bot didn't come, the match goes on.
    /// </summary>
    internal static class ZombiesOnlyOurs
    {
        private static readonly HashSet<string> Ours = new HashSet<string>();
        private static FieldInfo _inSpawnProcess;
        private static bool _patched;
        private static float _nextLog;
        private static int _refused;

        /// <summary>A zombie the core spawned (its spawn callback).</summary>
        public static void Mark(BotOwner bot)
        {
            string id = bot?.ProfileId;
            if (!string.IsNullOrEmpty(id))
                Ours.Add(id);
        }

        public static bool IsOurs(Player p) => p != null && Ours.Contains(p.ProfileId);

        // The spawn requests the core made (ZombiesRounds): the only ones let through in a zombies match. The game started
        // its own infected spawns late on (round 18), which passed the role check, stood outside and were then removed
        // after 4 s, dropping dead outside the map (user, 2026-11-16)
        private static readonly HashSet<BotCreationData> OurData = new HashSet<BotCreationData>();

        /// <summary>The core is about to spawn this request.</summary>
        public static void Expect(BotCreationData data)
        {
            if (data != null)
                OurData.Add(data);
        }

        /// <summary>The request's bot is made (its spawn callback).</summary>
        public static void Done(BotCreationData data)
        {
            if (data != null)
                OurData.Remove(data);
        }

        public static void Reset()
        {
            Ours.Clear();
            OurData.Clear();
            _refused = 0;
        }

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            try
            {
                // BotSpawner's private "make this bot" step (EFT names it method_N): found by its parameters, not its name
                MethodInfo make = AccessTools.GetDeclaredMethods(typeof(BotSpawner)).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.ReturnType == typeof(void) && p.Length == 4 && p[0].ParameterType == typeof(BotZone)
                           && p[1].ParameterType == typeof(BotCreationData) && p[2].ParameterType == typeof(Action<BotOwner>)
                           && p[3].ParameterType == typeof(CancellationToken);
                });
                _inSpawnProcess = AccessTools.Field(typeof(BotSpawner), "_inSpawnProcess");
                if (make == null || _inSpawnProcess == null)
                {
                    ZombiesPlugin.Log.LogWarning("Zombies: EFT's bot spawner step not found: other mods' AI is only removed after it spawns");
                    return;
                }
                harmony.Patch(make, prefix: new HarmonyMethod(typeof(ZombiesOnlyOurs), nameof(RefuseOthers)) { priority = Priority.First });
                ZombiesPlugin.Log.LogInfo($"Zombies: only zombies spawn in zombies matches (BotSpawner.{make.Name})");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: couldn't watch EFT's bot spawner ({e.Message}): other mods' AI is only removed after it spawns");
            }
        }

        // A bot that isn't a zombie never spawns in a zombies match
        private static bool RefuseOthers(BotSpawner __instance, BotCreationData data)
        {
            try
            {
                if (!ZombiesMatch.Active || data == null || OurData.Contains(data))
                    return true;
                // Not the core's: refused whatever it is, zombie roles too (they were let in and removed later)
                WildSpawnType role = TryRole(data, out WildSpawnType r) ? r : default;
                // What EFT does with a cancelled spawn: one fewer on the way, nothing made
                _inSpawnProcess.SetValue(__instance, Math.Max(0, (int)_inSpawnProcess.GetValue(__instance) - 1));
                _refused++;
                if (UnityEngine.Time.time >= _nextLog)
                {
                    _nextLog = UnityEngine.Time.time + 5f;
                    ZombiesPlugin.Log.LogInfo($"Zombies: a {role} bot wasn't let into the zombies match (not spawned by the zombies mode; {_refused} refused so far)");
                }
                return false;
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: bot spawn check: {e.Message}");
                return true;
            }
        }

        // The bot's role: what the spawn asked for, else its chosen profile's; unknown = let through (removed after if not ours)
        private static bool TryRole(BotCreationData data, out WildSpawnType role)
        {
            role = default;
            try
            {
                if (data._profileData != null && data._profileData.TryGetRole(out role, out _))
                    return true;
            }
            catch
            {
            }
            var settings = data.Profiles?.FirstOrDefault(p => p != null)?.Info?.Settings;
            if (settings == null)
                return false;
            role = settings.Role;
            return true;
        }
    }
}
