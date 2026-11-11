using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Things left on the floor in a zombies match, and the original's clean-up of them:
    /// - The Ray Gun's spent cells and the Thundergun's cartridge fall out on a reload as in the original, but only as models
    ///   (no loot: nothing to pick up again; the shots left in them go back into the reserve as before).
    /// - As each new round starts (as in the original), the dead zombies' bodies and everything dropped are cleared away.
    /// </summary>
    internal static class ZombiesLitter
    {
        // Most models on the floor at once (the oldest go first)
        private const int MostDropped = 40;

        private static readonly List<GameObject> Dropped = new List<GameObject>();
        private static readonly HashSet<string> Cleared = new HashSet<string>();

        /// <summary>A model of this item falls from here (out of the gun on a reload): seen, not lootable.</summary>
        public static void Drop(Item item, Vector3 from, Vector3 velocity)
        {
            if (item == null || !ZombiesMatch.Active)
                return;
            // A fresh copy of it: the model never touches the real item (it's still in the gun, or going away)
            Item copy = Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), item.TemplateId.ToString(), null);
            if (copy == null)
                return;
            ZombiesPlugin.Instance.StartCoroutine(BoxGuns.MakeItemModel(copy, go =>
            {
                if (go == null)
                    return;
                if (!ZombiesMatch.Active)
                {
                    Object.Destroy(go);
                    return;
                }
                go.name = "Zombies_Dropped_" + item.ShortName;
                go.transform.SetPositionAndRotation(from, Random.rotation);
                int layer = LayerMask.NameToLayer("Loot");
                SetLayer(go.transform, layer >= 0 ? layer : 0);
                Bounds b = BoxGuns.VisibleBounds(go);
                var col = go.AddComponent<BoxCollider>();
                col.center = go.transform.InverseTransformPoint(b.center);
                col.size = Vector3.Max(go.transform.InverseTransformVector(b.size).Abs(), Vector3.one * 0.02f);
                // The model can carry its own body already (frozen by MakeItemModel): AddComponent gave none and the
                // cell hung in mid-air (the user's video); the one there is woken up instead
                var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.detectCollisions = true;
                rb.useGravity = true;
                rb.mass = 0.3f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.velocity = velocity;
                rb.angularVelocity = Random.insideUnitSphere * 6f;
                Dropped.Add(go);
                while (Dropped.Count > MostDropped)
                {
                    if (Dropped[0] != null)
                        Object.Destroy(Dropped[0]);
                    Dropped.RemoveAt(0);
                }
                // Asleep once it's settled: no physics cost lying there
                ZombiesPlugin.Instance.StartCoroutine(Settle(rb));
            }));
        }

        /// <summary>Out of this player's gun: from the hands, falling forward and down a little.</summary>
        public static void DropFromHands(Item item, Player player, float delay)
        {
            if (item == null || player == null)
                return;
            ZombiesPlugin.Instance.StartCoroutine(DropLater(item, player, delay));
        }

        private static IEnumerator DropLater(Item item, Player player, float delay)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);
            if (player == null)
                yield break;
            Transform root = player.PlayerBones?.WeaponRoot?.Original;
            Vector3 at = root != null ? root.position : player.Transform.position + Vector3.up * 1.2f;
            Vector3 v = player.Transform.forward * 0.6f + player.Transform.right * 0.3f + Vector3.down * 0.5f + player.Velocity * 0.5f;
            Drop(item, at, v);
        }

        private static IEnumerator Settle(Rigidbody rb)
        {
            yield return new WaitForSeconds(4f);
            if (rb != null)
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
        }

        private static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            foreach (Transform c in t)
                SetLayer(c, layer);
        }

        private static Vector3 Abs(this Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>A new round: the bodies and what was dropped are cleared .</summary>
        public static void NewRound()
        {
            int models = 0, bodies = 0;
            foreach (GameObject go in Dropped)
                if (go != null)
                {
                    Object.Destroy(go);
                    models++;
                }
            Dropped.Clear();
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world != null)
            {
                foreach (Player p in world.AllPlayersEverExisted.ToList())
                {
                    if (p == null || p.HealthController == null || p.HealthController.IsAlive || !ZombiesTeam.IsZombie(p)
                        || Cleared.Contains(p.ProfileId))
                        continue;
                    try
                    {
                        Cleared.Add(p.ProfileId);
                        p.gameObject.SetActive(false);
                        bodies++;
                    }
                    catch (System.Exception e)
                    {
                        ZombiesPlugin.Log.LogWarning($"Zombies: a body couldn't be cleared ({e.Message})");
                    }
                }
            }
            if (models + bodies > 0)
                ZombiesPlugin.Log.LogInfo($"Zombies: round start cleared {bodies} bodies and {models} dropped things");
        }

        // ---------- Body piles (user, 2026-11-09): bodies don't wait for the round's end ----------
        // A body goes 30 s after its zombie died, and past 12 bodies on the ground the oldest go at once, so a big
        // round never leaves a pile lying for the whole round. Checked every 2 s on each game (each clears its own view).
        private const float BodyStays = 30f;
        private const int MostBodies = 12;
        private static readonly Dictionary<string, float> DiedAt = new Dictionary<string, float>();
        private static float _nextBodies;

        // ---------- New zombies hidden for their first moment (user, 2026-11-09: a body flashed on the spawn points) ----------
        // A zombie is made standing on its spawn spot, then the rise puts it underground; Fika's first position for it
        // on the other games (and a frame on the host) showed it standing there. Each new zombie's drawn parts are off
        // for its first 0.6 s on every game.
        private static readonly HashSet<string> SeenZombies = new HashSet<string>();
        private static readonly Dictionary<Player, (float until, List<Renderer> off)> Hidden = new Dictionary<Player, (float, List<Renderer>)>();

        // A Fika client: zombies seen appearing whose rise sound is still to play. Fika makes them far off the map
        // first ("5008 m away") and moves them in after: the sound waits till one is on the map near the player
        private static readonly Dictionary<Player, float> RiseSoundDue = new Dictionary<Player, float>();

        private static void TickRiseSounds()
        {
            if (RiseSoundDue.Count == 0)
                return;
            Vector3? me = Singleton<GameWorld>.Instance?.MainPlayer?.Position;
            foreach (var pair in RiseSoundDue.ToList())
            {
                Player z = pair.Key;
                if (z == null || Time.time > pair.Value || me == null)
                {
                    RiseSoundDue.Remove(z);
                    continue;
                }
                float away = Vector3.Distance(z.Position, me.Value);
                if (away > 150f)
                    continue;
                RiseSoundDue.Remove(z);
                ZombiesSounds.At("zm_rise_dirt_#", z.Position, 1f, 40f);
                ZombiesDebug.Log($"zombie rose {away:0} m away: rise sound played");
            }
        }

        public static void TickFresh()
        {
            TickDeadOnHost();
            TickAliveList();
            TickRiseSounds();
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (p == null || SeenZombies.Contains(p.ProfileId) || !ZombiesTeam.IsZombie(p))
                    continue;
                SeenZombies.Add(p.ProfileId);
                // The rise out of the dirt is scripted on the host: its sound there reaches nobody when the host is a
                // Fika headless, so each player's game plays it for a zombie it sees appear
                if (ZombiesNet.Online && !ZombiesNet.IsHost)
                    RiseSoundDue[p] = Time.time + 10f;
                var off = new List<Renderer>();
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled)
                    {
                        r.enabled = false;
                        off.Add(r);
                    }
                Hidden[p] = (Time.time + 0.6f, off);
            }
            if (Hidden.Count == 0)
                return;
            foreach (var pair in Hidden.ToList())
            {
                if (pair.Key != null && Time.time < pair.Value.until)
                    continue;
                foreach (Renderer r in pair.Value.off)
                    if (r != null)
                        r.enabled = true;
                Hidden.Remove(pair.Key);
            }
        }

        public static void Tick()
        {
            // The host decides (Fika): every game clears the same bodies at the same time (user, 2026-11-09)
            if (ZombiesNet.Online && !ZombiesNet.IsHost)
                return;
            if (Time.time < _nextBodies)
                return;
            _nextBodies = Time.time + 2f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            var lying = new List<(Player p, float at)>();
            foreach (Player p in world.AllPlayersEverExisted)
            {
                if (p == null || p.HealthController == null || p.HealthController.IsAlive || Cleared.Contains(p.ProfileId) || !ZombiesTeam.IsZombie(p))
                    continue;
                if (!DiedAt.TryGetValue(p.ProfileId, out float at))
                    DiedAt[p.ProfileId] = at = Time.time;
                lying.Add((p, at));
            }
            if (lying.Count == 0)
                return;
            lying.Sort((a, b) => a.at.CompareTo(b.at));
            int over = lying.Count - MostBodies;
            var gone = new List<string>();
            for (int i = 0; i < lying.Count; i++)
            {
                if (i >= over && Time.time - lying[i].at < BodyStays)
                    continue;
                try
                {
                    Cleared.Add(lying[i].p.ProfileId);
                    lying[i].p.gameObject.SetActive(false);
                    gone.Add(lying[i].p.ProfileId);
                }
                catch (System.Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies: a body couldn't be cleared ({e.Message})");
                }
            }
            if (gone.Count > 0)
                ZombiesNet.BodiesGone(gone);
        }

        // ---------- A zombie the host says died but still standing here (Fika's death missed) ----------
        private static readonly Dictionary<string, float> DeadOnHost = new Dictionary<string, float>();

        /// <summary>A client: the host's zombie died; if this copy still looks alive 1.5 s on, it's taken away.</summary>
        public static void DiedOnHost(string id)
        {
            if (!string.IsNullOrEmpty(id))
                DeadOnHost[id] = Time.time + 0.8f;
        }

        private static readonly Dictionary<string, Vector3> LastSeenAt = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, float> FirstCheck = new Dictionary<string, float>();

        // ---------- The host's living zombies, every 3 s (user, 2026-11-10: a zombie dead on one game still stood alive on
        // the other; Fika's death and our "died on the host" message both missed it) ----------
        private static float _nextAliveList;
        private static readonly Dictionary<string, int> NotOnHost = new Dictionary<string, int>();

        private static void TickAliveList()
        {
            if (!ZombiesNet.Online || !ZombiesNet.IsHost || Time.time < _nextAliveList)
                return;
            _nextAliveList = Time.time + 3f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            var ids = world.AllAlivePlayersList.Where(p => ZombiesTeam.IsZombie(p) && p.HealthController != null && p.HealthController.IsAlive)
                .Select(p => p.ProfileId).ToList();
            ZombiesNet.AliveZombies(ids);
        }

        /// <summary>
        /// A client: the host's list of living zombies. A zombie alive (or standing) here that the host no longer has in
        /// two lists in a row (3-6 s: a zombie the host just spawned is always on its list before it reaches us) is taken
        /// away like a cleared body.
        /// </summary>
        public static void AliveOnHost(HashSet<string> alive)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            var seen = new HashSet<string>();
            foreach (Player p in world.AllAlivePlayersList.ToList())
            {
                if (!ZombiesTeam.IsZombie(p) || Cleared.Contains(p.ProfileId) || alive.Contains(p.ProfileId))
                    continue;
                seen.Add(p.ProfileId);
                int misses = NotOnHost.TryGetValue(p.ProfileId, out int m) ? m + 1 : 1;
                NotOnHost[p.ProfileId] = misses;
                if (misses < 2)
                    continue;
                try
                {
                    Cleared.Add(p.ProfileId);
                    p.gameObject.SetActive(false);
                    ZombiesPlugin.Log.LogWarning($"Zombies: a zombie alive here ({p.ProfileId}, {(p.HealthController?.IsAlive == true ? "alive" : "dead")}) is gone on the host (not in its last two lists of living zombies): taken away");
                }
                catch
                {
                }
            }
            foreach (string id in NotOnHost.Keys.ToList())
                if (!seen.Contains(id))
                    NotOnHost.Remove(id);
        }

        private static void TickDeadOnHost()
        {
            if (DeadOnHost.Count == 0)
                return;
            GameWorld world = Singleton<GameWorld>.Instance;
            foreach (var pair in DeadOnHost.ToList())
            {
                if (Time.time < pair.Value)
                    continue;
                DeadOnHost.Remove(pair.Key);
                Player p = world?.AllPlayersEverExisted.FirstOrDefault(x => x != null && x.ProfileId == pair.Key);
                if (p == null || Cleared.Contains(pair.Key))
                    continue;
                // Still alive here, or dead here but never fell (its head still at standing height: no ragdoll, the
                // "alive-looking dead body" the user kept seeing, 2026-11-09)
                bool alive = p.HealthController != null && p.HealthController.IsAlive;
                float headUp = 0f;
                try
                {
                    headUp = p.PlayerBones.Head.position.y - p.Position.y;
                }
                catch
                {
                }
                // A body frozen mid-swing (killed by someone else while it slapped a player: Fika's death landed during the
                // knife kick and no ragdoll came) leans: its head sits lower than standing, never on the floor
                bool standing = headUp > 0.7f;
                if (!alive && !standing)
                    continue;
                // Dead and still moving (a Thundergun fling, a ragdoll still falling): not a frozen body. Looked at again
                // shortly, for up to 6 s, so a flung zombie lands before anything's decided (user, 2026-11-10: they
                // vanished mid-air)
                if (!alive)
                {
                    // The ragdoll's head: the player's own position stays where it died while the body flies
                    Vector3 now = p.Position;
                    try
                    {
                        now = p.PlayerBones.Head.position;
                    }
                    catch
                    {
                    }
                    bool moving = !LastSeenAt.TryGetValue(pair.Key, out Vector3 was) || (now - was).sqrMagnitude > 0.04f;
                    LastSeenAt[pair.Key] = now;
                    if (!FirstCheck.TryGetValue(pair.Key, out float first))
                        FirstCheck[pair.Key] = first = Time.time;
                    // In the air (flung) or still moving: left alone, up to 8 s
                    if ((moving || headUp > 2.2f) && Time.time - first < 8f)
                    {
                        DeadOnHost[pair.Key] = Time.time + 0.5f;
                        continue;
                    }
                }
                try
                {
                    Cleared.Add(pair.Key);
                    p.gameObject.SetActive(false);
                    ZombiesPlugin.Log.LogInfo($"Zombies: a zombie the host killed was still {(alive ? "alive" : "standing dead")} here (head {headUp:0.0} m up): taken away");
                }
                catch
                {
                }
            }
        }

        /// <summary>A client: the host cleared these bodies.</summary>
        public static void BodiesFromHost(List<string> ids)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            var want = new HashSet<string>(ids);
            foreach (Player p in world.AllPlayersEverExisted)
            {
                if (p == null || !want.Contains(p.ProfileId) || Cleared.Contains(p.ProfileId))
                    continue;
                try
                {
                    Cleared.Add(p.ProfileId);
                    p.gameObject.SetActive(false);
                }
                catch
                {
                }
            }
        }

        /// <summary>A new match: nothing from the last one.</summary>
        public static void Reset()
        {
            DiedAt.Clear();
            SeenZombies.Clear();
            RiseSoundDue.Clear();
            DeadOnHost.Clear();
            NotOnHost.Clear();
            Hidden.Clear();
            foreach (GameObject go in Dropped)
                if (go != null)
                    Object.Destroy(go);
            Dropped.Clear();
            Cleared.Clear();
        }
    }
}
