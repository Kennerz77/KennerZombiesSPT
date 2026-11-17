using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using Systems.Effects;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Crawlers, as in the original: a blast that leaves a zombie alive can take its legs off, and it drags itself on
    /// along the ground (user, 2026-11-15: from round 10, 30% of the blasts it lives through). It keeps its health,
    /// still comes in through the barricades and still attacks, from the floor. The last-zombie crawler trick stays.
    /// The host decides (the blast lands in its health pool, ZombiesHealth.Hit) and tells every game (Msg.Crawler);
    /// each game shows it: the lower legs gone with a blood burst (CrawlerLegs), the pack's crawl set
    /// (ZombiesAnim.SetCrawling), the crawl attacks (ZombiesZombieVox.Swing). The host also slows it (ZombiesRounds.Crawl).
    /// </summary>
    internal static class ZombiesCrawlers
    {
        private const string Section = "25. Crawlers";
        private static ConfigEntry<int> _fromRound;
        private static ConfigEntry<float> _chance, _speed, _health;

        // Profile ids of this match's crawlers (ids are new each match, so old ones never match again)
        private static readonly HashSet<string> Ids = new HashSet<string>();

        public static void Bind(ConfigFile config)
        {
            _fromRound = config.Bind(Section, "Crawlers from round", 10, new ConfigDescription(
                "From this round a blast a zombie lives through can take its legs off.", new AcceptableValueRange<int>(1, 100)));
            _chance = config.Bind(Section, "Crawler chance", 0.3f, new ConfigDescription(
                "The chance that a blast a zombie lives through makes it a crawler.", new AcceptableValueRange<float>(0f, 1f)));
            _speed = config.Bind(Section, "Crawler speed (x walker)", 0.4f, new ConfigDescription(
                "How fast crawlers drag themselves along, as a share of a walking zombie's pace.", new AcceptableValueRange<float>(0.1f, 1f)));
            // A crawler kept whatever health it had left after the blast: late rounds' pools made them sponges (user, 2026-11-16)
            _health = config.Bind(Section, "Crawler health (share of full)", 0.25f, new ConfigDescription(
                "A new crawler's health is cut to at most this share of a zombie's full health this round.", new AcceptableValueRange<float>(0.05f, 1f)));
        }

        public static float SpeedOfWalker => _speed?.Value ?? 0.4f;

        public static void Clear() => Ids.Clear();

        public static bool Is(Player zombie) => zombie != null && Ids.Contains(zombie.ProfileId);

        /// <summary>
        /// On the host, after a hit landed in a zombie's health pool and it lived: a blast from round 10 can make it a
        /// crawler.
        /// </summary>
        public static void AfterHit(Player zombie, DamageInfo hit)
        {
            try
            {
                if (zombie == null || !ZombiesMatch.Active || !ZombiesNet.IsHost || Is(zombie))
                    return;
                if (hit.DamageType != EDamageType.Explosion && hit.DamageType != EDamageType.GrenadeFragment && hit.DamageType != EDamageType.Landmine)
                    return;
                // Not the Ray Gun Mark II's bolts: their burst is energy, not a blast that takes legs (user, 2026-11-16)
                if (hit.Weapon is EFT.InventoryLogic.Item w && w.TemplateId.ToString() == ZombiesAmmo.RaygunMk2Id)
                    return;
                if (ZombiesRounds.Round < (_fromRound?.Value ?? 10) || UnityEngine.Random.value >= (_chance?.Value ?? 0.3f))
                    return;
                if (zombie.HealthController == null || !zombie.HealthController.IsAlive)
                    return;
                Make(zombie, hit.HitPoint);
                if (ZombiesNet.Online)
                    ZombiesNet.Crawler(zombie.ProfileId);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"[crawler] making a crawler failed: {e.Message}");
            }
        }

        /// <summary>The host said this zombie is a crawler now.</summary>
        public static void MakeRemote(string profileId)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null || string.IsNullOrEmpty(profileId))
                return;
            foreach (Player p in world.AllAlivePlayersList)
                if (p != null && p.ProfileId == profileId)
                {
                    Make(p, null);
                    return;
                }
        }

        private static void Make(Player zombie, Vector3? blastAt)
        {
            if (!Ids.Add(zombie.ProfileId))
                return;
            ZombiesAnim.SetCrawling(zombie, true);
            if (zombie.GetComponent<CrawlerLegs>() == null)
                zombie.gameObject.AddComponent<CrawlerLegs>().Init(zombie);
            // The host moves it: crawl pace from here on, and its health cut (the host holds the pools)
            if (ZombiesNet.IsHost)
            {
                ZombiesRounds.Crawl(zombie.AIData?.BotOwner);
                ZombiesHealth.CapPool(zombie, _health?.Value ?? 0.25f);
            }
            ZombiesPlugin.Log.LogInfo($"[crawler] {zombie.ProfileId} lost its legs (round {ZombiesRounds.Round})");
        }

        /// <summary>
        /// A crawler's lower legs: calves (and so the feet) shrunk away under the knees, held every frame, with a blood
        /// burst when they go. Stays on the body after death (the legs don't come back).
        /// </summary>
        [DefaultExecutionOrder(32010)]
        internal sealed class CrawlerLegs : MonoBehaviour
        {
            private static readonly string[] Calves = { "Base HumanLCalf", "Base HumanRCalf" };
            private readonly List<Transform> _calves = new List<Transform>();

            public void Init(Player p)
            {
                Transform pelvis = p.PlayerBones?.Pelvis?.Original;
                Transform root = pelvis != null ? pelvis.parent : p.Transform.Original;
                if (root == null)
                    return;
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    if (Array.IndexOf(Calves, t.name) >= 0)
                        _calves.Add(t);
                Blood(p);
                if (_calves.Count == 0)
                    ZombiesPlugin.Log.LogWarning("[crawler] no calf bones found on this body: the legs stay");
            }

            private void LateUpdate()
            {
                for (int i = 0; i < _calves.Count; i++)
                    if (_calves[i] != null)
                        _calves[i].localScale = new Vector3(0.01f, 0.01f, 0.01f);
            }

            // At each knee: the body-hit blood out of the stump, and a splash on the floor below
            private void Blood(Player p)
            {
                try
                {
                    if (!Singleton<Effects>.Instantiated)
                        return;
                    Effects effects = Singleton<Effects>.Instance;
                    var body = effects.Get(MaterialType.Body);
                    Camera cam = Camera.main;
                    foreach (Transform knee in _calves)
                    {
                        if (knee == null)
                            continue;
                        Vector3 at = knee.position;
                        float distance = cam != null ? Vector3.Distance(cam.transform.position, at) : 0f;
                        if (body != null && cam != null)
                            foreach (var particles in body.Particles)
                            {
                                particles.Emit(at, Vector3.down, distance);
                                particles.Emit(at, (Vector3.down + p.Transform.forward * -0.5f).normalized, distance);
                            }
                        if (Physics.Raycast(at + Vector3.up * 0.2f, Vector3.down, out RaycastHit floor, 3f,
                                LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain"), QueryTriggerInteraction.Ignore))
                            effects.EmitBloodOnEnvironment(floor.point, floor.normal);
                    }
                }
                catch (Exception e)
                {
                    ZombiesDebug.Log($"crawler blood: {e.Message}");
                }
            }
        }
    }
}
