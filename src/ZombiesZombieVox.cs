using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's zombie voices in a zombies match (user, 2026-11-13), instead of EFT's infected's own:
    /// - now and then each zombie moans (walkers), or screams (sprinters);
    /// - close enough to swing at a player, an attack growl and a swing whoosh;
    /// - close behind the player, a growl right behind them;
    /// - dying, a death sound.
    /// Every game plays them for every zombie it sees (no messages: the same in co-op). Only the nearest few talk at
    /// once, so a horde is a crowd, not a wall of noise. EFT's own voice is shut for zombies in a match.
    /// Sounds: data\sounds\zm_zvox_*.wav and zm_zswing_*.wav, from the original's banks (our sound bank extractor).
    /// </summary>
    internal static class ZombiesZombieVox
    {
        private const float Hear = 35f;           // farther zombies stay quiet (the player can't hear them anyway)
        private const int MostTalking = 4;        // the nearest this many may talk at once (was 6: too busy, user 2026-11-13)
        private const float SprintSpeed = 3.2f;   // m/s: faster than this it's a sprinter (screams, not moans)
        private const float AttackReach = 2.2f;
        private const float BehindReach = 4f;

        private static readonly Dictionary<int, float> NextMoan = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> NextAttack = new Dictionary<int, float>();
        // How talkative each zombie is (user, 2026-11-13: "a range in how vocal some zombies are", not every zombie
        // moaning all the time and every death voiced): about a quarter quiet, most ordinary, a fifth loud
        private static readonly Dictionary<int, float> Chatty = new Dictionary<int, float>();

        private static float ChattyOf(int id)
        {
            if (Chatty.TryGetValue(id, out float c))
                return c;
            double r = _dice.NextDouble();
            c = r < 0.25 ? Roll(0.2f, 0.45f) : r < 0.8 ? Roll(0.7f, 1.1f) : Roll(1.3f, 1.8f);
            Chatty[id] = c;
            return c;
        }
        private static readonly HashSet<BaseSpeaker> ZombieSpeakers = new HashSet<BaseSpeaker>();
        private static readonly List<(Player p, float d)> Near = new List<(Player, float)>();
        private static float _nextBehind, _nextSpeakers;
        private static bool _patched;
        private static System.Random _dice = new System.Random();

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            int n = 0;
            foreach (var m in AccessTools.GetDeclaredMethods(typeof(BaseSpeaker)))
                if (m.Name == nameof(BaseSpeaker.Play) || m.Name == nameof(BaseSpeaker.PlayDirect) || m.Name == nameof(BaseSpeaker.PlayExternal)
                    || m.Name == nameof(BaseSpeaker.PlayDirectRandom))
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ZombiesZombieVox), nameof(Quiet)));
                    n++;
                }
            // The game's own footsteps: shut for a zombie walking on the new animation (it plays the original's steps on its
            // foot plants, ZombieAnimPlayer); a zombie left to the game's animation keeps the game's steps
            var steps = AccessTools.Method(typeof(Player), nameof(Player.PlayStepSound));
            if (steps != null)
            {
                harmony.Patch(steps, prefix: new HarmonyMethod(typeof(ZombiesZombieVox), nameof(NoGameSteps)));
                n++;
            }
            Player.OnPlayerDeadStatic += OnDead;
            ZombiesPlugin.Log.LogInfo($"Zombies voices: the original's zombie voices on ({n} of EFT's speaker calls shut for zombies)");
        }

        public static void Reset()
        {
            NextMoan.Clear();
            NextAttack.Clear();
            Chatty.Clear();
            ZombieSpeakers.Clear();
            _nextBehind = 0f;
        }

        private static bool NoGameSteps(Player __instance)
        {
            if (!ZombiesMatch.Active || __instance == null)
                return true;
            var anim = __instance.GetComponent<ZombieAnimPlayer>();
            return anim == null || !anim.OwnSteps;
        }

        // EFT's own infected voice: shut for zombies in a match (ours speak instead)
        private static bool Quiet(BaseSpeaker __instance) => !(ZombiesMatch.Active && __instance != null && ZombieSpeakers.Contains(__instance));

        private static float Roll(float min, float max) => min + (float)_dice.NextDouble() * (max - min);

        public static void Tick(Player me)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (!ZombiesMatch.Active || world == null || me == null || ZombiesPause.Paused || ZombiesGameOver.Active)
                return;
            float now = Time.time;
            bool refresh = now >= _nextSpeakers;
            if (refresh)
            {
                _nextSpeakers = now + 1f;
                ZombieSpeakers.Clear();
            }
            Vector3 ear = me.Transform.position;
            Near.Clear();
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (p == null || !ZombiesTeam.IsZombie(p) || p.HealthController == null || !p.HealthController.IsAlive)
                    continue;
                if (refresh && p.Speaker != null)
                    ZombieSpeakers.Add(p.Speaker);
                float d = Vector3.Distance(p.Transform.position, ear);
                if (d <= Hear)
                    Near.Add((p, d));
            }
            Near.Sort((a, b) => a.d.CompareTo(b.d));
            for (int i = 0; i < Near.Count && i < MostTalking; i++)
            {
                Player z = Near[i].p;
                float d = Near[i].d;
                int id = z.Id;
                float chatty = ChattyOf(id);
                Transform head = z.PlayerBones?.Head?.Original ?? z.Transform.Original;
                // Swinging range: the swing itself makes the noise (Swing, from the host's slap); no moaning meanwhile
                if (d <= AttackReach)
                    continue;
                // Right behind the player: a growl at their back (not too often)
                if (d <= BehindReach && now >= _nextBehind)
                {
                    Vector3 to = z.Transform.position - ear;
                    to.y = 0f;
                    if (to.sqrMagnitude > 0.01f && Vector3.Angle(me.Transform.forward, to) > 120f)
                    {
                        _nextBehind = now + Roll(6f, 10f);
                        ZombiesSounds.On("zm_zvox_behind_#", head, 1f, 20f);
                        NextMoan[id] = now + Roll(3f, 5f);
                        continue;
                    }
                }
                if (!NextMoan.TryGetValue(id, out float next))
                {
                    // A first moan a while after it shows up (not all at once; the quiet ones much later)
                    NextMoan[id] = now + Roll(1f, 6f) / chatty;
                    continue;
                }
                if (now < next)
                    continue;
                Vector3 v = z.Velocity;
                v.y = 0f;
                bool sprinting = v.magnitude > SprintSpeed;
                NextMoan[id] = now + (sprinting ? Roll(3.5f, 7f) : Roll(5f, 12f)) / chatty;
                ZombiesSounds.On(sprinting ? "zm_zvox_sprint_#" : "zm_zvox_amb_#", head, sprinting ? 0.85f : 0.75f, 30f);
            }
        }

        /// <summary>
        /// A zombie swings (user, 2026-11-13: "slapping sounds, yells when they go to slap"): its attack yell (most swings,
        /// by how vocal it is), the whoosh, and the swing animation. On the host from the slap or the board rip itself,
        /// and on every other game from the host's message.
        /// </summary>
        public static void Swing(Player zombie, bool rip)
        {
            try
            {
                if (!ZombiesMatch.Active || zombie == null)
                    return;
                Player me = Singleton<GameWorld>.Instance?.MainPlayer;
                Transform head = zombie.PlayerBones?.Head?.Original ?? zombie.Transform.Original;
                bool near = me != null && Vector3.Distance(head.position, me.Transform.position) <= Hear;
                if (near)
                {
                    float chatty = ChattyOf(zombie.Id);
                    if (_dice.NextDouble() < Mathf.Clamp01((rip ? 0.35f : 0.55f) + 0.35f * chatty))
                        ZombiesSounds.On("zm_zvox_attack_#", head, 0.95f, 25f);
                    ZombiesSounds.At("zm_zswing_#", head.position, 0.7f, 14f);
                    NextMoan[zombie.Id] = Time.time + Roll(2f, 4f) / chatty;
                }
                // A crawler swipes from the floor (the pack's crawl attacks); walkers use the game's own swing now
                string[] swings = ZombiesCrawlers.Is(zombie) ? new[] { "Attack_LH_Crawl", "Attack_RH_Crawl" } : new[] { "Attack_LH_Stand", "Attack_RH_Stand", "Attack_BH_Stand" };
                ZombiesAnim.Play(zombie, swings[_dice.Next(swings.Length)], 1.35f);
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"zombie swing sound: {e.Message}");
            }
        }

        public static void SwingRemote(string profileId, bool rip)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || string.IsNullOrEmpty(profileId))
                return;
            foreach (Player p in world.AllAlivePlayersList)
                if (p != null && p.ProfileId == profileId)
                {
                    Swing(p, rip);
                    return;
                }
        }

        private static void OnDead(Player victim, IPlayer aggressor, DamageInfo damage, EBodyPart part)
        {
            try
            {
                if (!ZombiesMatch.Active || victim == null || !ZombiesTeam.IsZombie(victim))
                    return;
                Player me = Singleton<GameWorld>.Instance?.MainPlayer;
                if (me == null || Vector3.Distance(victim.Transform.position, me.Transform.position) > Hear)
                    return;
                Transform head = victim.PlayerBones?.Head?.Original ?? victim.Transform.Original;
                // Not every death is voiced: quiet ones mostly die silently, loud ones nearly always cry out
                if (_dice.NextDouble() < Mathf.Clamp01(0.2f + 0.45f * ChattyOf(victim.Id)))
                    ZombiesSounds.At("zm_zvox_death_#", head.position, 0.85f, 30f);
                Chatty.Remove(victim.Id);
                NextMoan.Remove(victim.Id);
                NextAttack.Remove(victim.Id);
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"zombie voice (death): {e.Message}");
            }
        }
    }
}
