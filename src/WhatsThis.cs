using System.Linq;
using System.Text;
using EFT;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Dev help for the map: Ctrl+I in a raid names what you're looking at (the three nearest meshes the aim passes
    /// through: their scene, path in the scene, and which LOD level of which LOD group draws them, then the three nearest
    /// colliders on the aim, which find invisible blockers), on screen and in the log, so a map object can be found in
    /// Unity exactly.
    /// </summary>
    internal static class WhatsThis
    {
        public static void Tick()
        {
            if (ZombiesDebug.WhatsThisKey?.Value == false || !Input.GetKeyDown(KeyCode.I) || !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                return;
            Camera cam = Camera.main;
            if (cam == null)
                return;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            var hits = Object.FindObjectsOfType<Renderer>()
                .Where(r => r.enabled && r.isVisible && !(r is ParticleSystemRenderer))
                .Select(r => (r, ok: r.bounds.IntersectRay(ray, out float d), d))
                .Where(h => h.ok && h.d < 150f && h.d > 0.3f)
                .OrderBy(h => h.d)
                .Take(3)
                .ToList();
            var sb = new StringBuilder();
            foreach (var (r, _, d) in hits)
            {
                string path = r.name;
                for (Transform p = r.transform.parent; p != null; p = p.parent)
                    path = p.name + "/" + path;
                LODGroup g = r.GetComponentInParent<LODGroup>();
                int level = -1;
                if (g != null)
                {
                    LOD[] lods = g.GetLODs();
                    for (int i = 0; i < lods.Length; i++)
                        if (lods[i].renderers.Contains(r))
                            level = i;
                }
                sb.AppendLine($"{d:0.0} m: [{r.gameObject.scene.path}] {r.gameObject.scene.name}/{path}{(g != null ? $" (LOD group {g.name}, level {level})" : " (no LOD group)")} at {r.transform.position}");
            }
            // What actually stops you: the colliders on the aim (an invisible blocker has no mesh to show above)
            var blocks = Physics.RaycastAll(ray, 150f, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => h.distance > 0.3f && h.collider.GetComponentInParent<Player>() == null)
                .OrderBy(h => h.distance)
                .Take(3)
                .ToList();
            foreach (RaycastHit h in blocks)
            {
                Collider c = h.collider;
                string path = c.name;
                for (Transform p = c.transform.parent; p != null; p = p.parent)
                    path = p.name + "/" + path;
                Renderer own = c.GetComponent<Renderer>();
                string seen = own != null ? (own.enabled && own.isVisible ? "drawn" : "its mesh not drawn") : "no mesh of its own";
                sb.AppendLine($"{h.distance:0.0} m collider ({c.GetType().Name}, layer {LayerMask.LayerToName(c.gameObject.layer)}, {seen}): [{c.gameObject.scene.path}] {c.gameObject.scene.name}/{path} at {c.transform.position}");
            }
            string text = hits.Count == 0 && blocks.Count == 0 ? "Nothing in your aim." : sb.ToString();
            ZombiesPlugin.Log.LogInfo("What's this:\n" + text);
            ZombiesPlugin.Notify(hits.Count == 0 && blocks.Count == 0 ? text
                : "What's this: " + (blocks.Count > 0 ? blocks[0].collider.name : hits[0].r.name) + " (the rest in the log)");
        }
    }
}
