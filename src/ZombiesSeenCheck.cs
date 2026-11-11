using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace KennerZombies
{
    /// <summary>
    /// Logger (user, 2026-11-10: two zombies were invisible on one player's game once, no log): every 5 s, each living
    /// zombie within 40 m whose body draws nothing here (no skinned renderer on, all switched off, forced off or shadows
    /// only, or the body object inactive) is logged with what's off, once when it goes and once when it comes back. Two
    /// checks in a row are needed, so the short corpse-litter hide (0.6 s) never counts. Reads only, changes nothing.
    /// </summary>
    internal static class ZombiesSeenCheck
    {
        private const float Every = 5f, Near = 40f;
        private static float _next;
        private static readonly HashSet<string> Suspect = new HashSet<string>();
        private static readonly HashSet<string> Reported = new HashSet<string>();

        public static void Clear()
        {
            Suspect.Clear();
            Reported.Clear();
        }

        public static void Tick(Player me)
        {
            if (me == null || Time.time < _next || !Singleton<GameWorld>.Instantiated)
                return;
            _next = Time.time + Every;
            Vector3 eye = me.Transform.position;
            var stillOff = new HashSet<string>();
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList.ToList())
            {
                if (!ZombiesTeam.IsZombie(p) || p.HealthController == null || !p.HealthController.IsAlive)
                    continue;
                float dist = Vector3.Distance(eye, p.Transform.position);
                if (dist > Near)
                    continue;
                SkinnedMeshRenderer[] skins = ((Component)p).GetComponentsInChildren<SkinnedMeshRenderer>(true);
                int drawn = 0, disabled = 0, inactive = 0, forced = 0, shadowOnly = 0;
                foreach (SkinnedMeshRenderer r in skins)
                {
                    if (r == null)
                        continue;
                    if (!r.gameObject.activeInHierarchy)
                        inactive++;
                    else if (!r.enabled)
                        disabled++;
                    else if (r.forceRenderingOff)
                        forced++;
                    else if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                        shadowOnly++;
                    else
                        drawn++;
                }
                string id = p.ProfileId;
                if (drawn > 0)
                {
                    if (Reported.Remove(id))
                        ZombiesPlugin.Log.LogInfo($"Zombies seen check: zombie {p.Profile?.Nickname} ({id}) draws again ({drawn} skinned renderers on, {dist:0} m)");
                    continue;
                }
                stillOff.Add(id);
                if (!Suspect.Contains(id) || Reported.Contains(id))
                    continue;
                Reported.Add(id);
                bool bodyOn = p.PlayerBody != null && p.PlayerBody.gameObject.activeInHierarchy;
                string offNames = string.Join(", ", skins.Where(r => r != null).Take(8).Select(r => r.name + (r.enabled ? "" : " (off)")));
                ZombiesPlugin.Log.LogWarning($"Zombies seen check: zombie {p.Profile?.Nickname} ({id}) at {dist:0} m DRAWS NOTHING here: {skins.Length} skinned "
                    + $"renderers: {disabled} switched off, {inactive} inactive, {forced} forced off, {shadowOnly} shadows only; body object "
                    + $"{(bodyOn ? "on" : "OFF")}, player object {(((Component)p).gameObject.activeInHierarchy ? "on" : "OFF")}, "
                    + $"host: {ZombiesNet.IsHost}, round {ZombiesRounds.Round}. Renderers: {offNames}");
            }
            Suspect.Clear();
            Suspect.UnionWith(stillOff);
        }
    }
}
