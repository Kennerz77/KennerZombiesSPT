using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// A zombie shocked by a wonder weapon's lightning (the Wunderwaffe DG-2 mod, through <see cref="ZombiesApi.Shock"/>):
    /// held on its feet where it stands, no swings, its body jerking as the current runs through it, until the weapon
    /// kills it (user, 2026-11-16: in the original they stay stood up while shocked, then die).
    /// </summary>
    internal static class ZombiesShock
    {
        private static readonly Dictionary<BotOwner, float> Until = new Dictionary<BotOwner, float>();

        public static bool Shocked(BotOwner z) => z != null && Until.TryGetValue(z, out float t) && Time.time < t;

        // The original's tesla deaths (retargeted by tools/retarget_traversal.py into zombie_traverse.json): stood up,
        // jerking, then down; a crawler's own
        private static readonly string[] Standing = { "tesla_death_a", "tesla_death_b", "tesla_death_d" };
        private static readonly string[] Crawling = { "tesla_crawl_death_a", "tesla_crawl_death_b" };

        /// <summary>
        /// Shocks the zombie: one of the original's tesla deaths played on it (on every game), held where it stands, no
        /// swings. Returns how long until its body is down (when the weapon should kill it, so the fall carries on as
        /// the ragdoll); without the clips, a jerking hold for the asked seconds instead.
        /// </summary>
        public static float Shock(Player zombie, float seconds)
        {
            if (zombie == null || zombie.HealthController == null || !zombie.HealthController.IsAlive)
                return 0f;
            float hold = Mathf.Max(0.1f, seconds);
            string[] names = ZombiesCrawlers.Is(zombie) ? Crawling : Standing;
            string name = names[Random.Range(0, names.Length)];
            ZombiesAnim.Clip clip = null;
            try
            {
                if (ZombiesAnim.TraverseReady)
                    clip = ZombiesAnim.PlayTraverse(zombie, name);
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombie shock: {name} couldn't play ({e.Message})");
            }
            if (clip != null)
            {
                hold = DownAt(clip);
                if (ZombiesNet.Online)
                    ZombiesNet.Traverse(zombie.ProfileId, name, 0f, 1f);
            }
            else
            {
                ZombieShockJitter jitter = zombie.GetComponent<ZombieShockJitter>() ?? zombie.gameObject.AddComponent<ZombieShockJitter>();
                jitter.Begin(zombie, hold);
            }
            BotOwner z = zombie.AIData?.BotOwner;
            if (z != null)
            {
                Until[z] = Time.time + hold;
                ZombiesRounds.HoldFor(z, hold + 0.5f);
                ZombiesRounds.BusyFor(z, hold + 0.5f);
            }
            return hold;
        }

        /// <summary>When the clip's body is down: its hips nearly at their lowest (before the clip blends back out).</summary>
        private static float DownAt(ZombiesAnim.Clip c)
        {
            float latest = Mathf.Max(0.2f, c.Length - 0.45f);
            if (c.Hips == null || c.Hips.Length < 2)
                return latest;
            float top = c.Hips[0].y, low = top;
            foreach (Vector3 h in c.Hips)
                low = Mathf.Min(low, h.y);
            float near = low + 0.12f * (top - low);
            for (int f = 0; f < c.Hips.Length; f++)
                if (c.Hips[f].y <= near)
                    return Mathf.Min(latest, f / ZombiesAnim.Fps);
            return latest;
        }

        public static void Clear() => Until.Clear();
    }

    /// <summary>
    /// The shocked body's convulsions, over whatever pose it has (after the zombie animation pack has written its
    /// frame): the head, spine and arms jerking about, quick and uneven, fading in over a tenth of a second.
    /// </summary>
    [DefaultExecutionOrder(32010)]
    internal sealed class ZombieShockJitter : MonoBehaviour
    {
        // Bone and how far it jerks (degrees)
        private static readonly (string bone, float degrees)[] Parts =
        {
            ("Base HumanSpine2", 5f), ("Base HumanSpine3", 6f), ("Base HumanNeck", 10f), ("Base HumanHead", 16f),
            ("Base HumanLUpperarm", 24f), ("Base HumanRUpperarm", 24f), ("Base HumanLForearm1", 20f), ("Base HumanRForearm1", 20f),
            ("Base HumanLPalm", 28f), ("Base HumanRPalm", 28f), ("Base HumanLThigh1", 4f), ("Base HumanRThigh1", 4f),
        };

        private Player _p;
        private readonly List<(Transform t, float degrees, float seed)> _bones = new List<(Transform, float, float)>();
        private float _from, _until;

        public void Begin(Player zombie, float seconds)
        {
            _p = zombie;
            _from = Time.time;
            _until = Time.time + seconds;
            if (_bones.Count > 0)
                return;
            var byName = new Dictionary<string, Transform>();
            foreach (Transform t in zombie.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name))
                    byName[t.name] = t;
            int i = 0;
            foreach (var (bone, degrees) in Parts)
                if (byName.TryGetValue(bone, out Transform t))
                    _bones.Add((t, degrees, 17.3f * ++i + Random.value * 50f));
        }

        private void LateUpdate()
        {
            if (_p == null || Time.time >= _until || _p.HealthController == null || !_p.HealthController.IsAlive)
            {
                Destroy(this);
                return;
            }
            float t = Time.time;
            float weight = Mathf.Clamp01((t - _from) / 0.1f);
            foreach (var (bone, degrees, seed) in _bones)
            {
                if (bone == null)
                    continue;
                // Fast, jagged twitches: two noises, one quick and one quicker
                float x = Noise(t * 17f, seed) + 0.5f * Noise(t * 41f, seed + 3.1f);
                float y = Noise(t * 15f, seed + 7.7f) + 0.5f * Noise(t * 37f, seed + 9.2f);
                float z = Noise(t * 19f, seed + 13.4f) + 0.5f * Noise(t * 43f, seed + 1.9f);
                bone.localRotation = bone.localRotation * Quaternion.Euler(new Vector3(x, y, z) * (degrees * weight / 1.5f));
            }
        }

        private static float Noise(float t, float seed) => Mathf.PerlinNoise(t, seed) * 2f - 1f;
    }
}
