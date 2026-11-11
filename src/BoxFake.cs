using System.Collections;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// What a box spot shows while the box is elsewhere (the original's Der Eisendrache): the cinder blocks with a plank, three
    /// old cases and the teddy sitting on the middle one, twitching now and then (its two idle anims, at random).
    /// </summary>
    internal class BoxFake : MonoBehaviour
    {
        private Animator _teddy;

        public void Init((Vector3 position, Quaternion rotation) at)
        {
            name = "Zombies_MysteryBoxSpot (KennerZombies)";
            transform.localScale = Vector3.one * MysteryBox.BoxScale;
            Transform foot = transform.Find("StandHeight");
            float lift = foot != null ? -foot.localPosition.y * MysteryBox.BoxScale : 0f;
            transform.SetPositionAndRotation(at.position + Vector3.up * lift, at.rotation);
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
                foreach (Material mat in renderer.materials)
                {
                    if (mat == null || mat.shader == null || mat.shader.isSupported)
                        continue;
                    Shader gameShader = Shader.Find(mat.shader.name);
                    if (gameShader != null)
                        mat.shader = gameShader;
                }
            _teddy = transform.Find("Display/p7_zm_der_magic_box_fake")?.GetComponent<Animator>();
            Solid();
            StartCoroutine(Twitch());
        }

        // Solid like the box itself: one box collider round the blocks and cases (walked into, not over)
        private void Solid()
        {
            bool any = false;
            Bounds local = default;
            foreach (MeshFilter f in GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh == null)
                    continue;
                Bounds b = f.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = transform.InverseTransformPoint(f.transform.TransformPoint(corner));
                    if (!any)
                        local = new Bounds(p, Vector3.zero);
                    else
                        local.Encapsulate(p);
                    any = true;
                }
            }
            if (!any)
                return;
            var go = new GameObject("SpotHit");
            go.transform.SetParent(transform, false);
            int layer = LayerMask.NameToLayer("LowPolyCollider");
            if (layer >= 0)
                go.layer = layer;
            var c = go.AddComponent<BoxCollider>();
            // Tall enough not to be jumped onto (2 m in the world)
            float tall = Mathf.Max(local.size.y, 2f / Mathf.Max(0.01f, transform.lossyScale.y));
            c.size = new Vector3(local.size.x, tall, local.size.z);
            c.center = new Vector3(local.center.x, local.min.y + tall / 2f, local.center.z);
        }

        private IEnumerator Twitch()
        {
            while (true)
            {
                // Now and then, not often (the user, 2026-11-01): the bear is one bone, so a twitch turns all of it
                yield return new WaitForSeconds(Random.Range(60f, 180f));
                if (_teddy != null && _teddy.isActiveAndEnabled)
                    _teddy.Play(Random.value < 0.5f ? "twitch_a" : "twitch_b", 0, 0f);
            }
        }

        public void Show(bool on) => ShowDisplay(on);

        /// <summary>
        /// The cases and the teddy (and the spot's own hit box) on or off; the cinder blocks and plank always stay:
        /// where the box is, it sits on this spot's plank.
        /// </summary>
        public void ShowDisplay(bool on)
        {
            gameObject.SetActive(true);
            Transform display = transform.Find("Display");
            if (display != null)
                display.gameObject.SetActive(on);
            Transform hit = transform.Find("SpotHit");
            if (hit != null)
                hit.gameObject.SetActive(on);
        }

        /// <summary>The top of the plank, where the box stands (world height), or null.</summary>
        public float? PlankTop
        {
            get
            {
                Transform plank = transform.Find("Stand/Plank");
                Renderer r = plank != null ? plank.GetComponentInChildren<Renderer>(true) : null;
                return r != null ? r.bounds.max.y : (float?)null;
            }
        }
    }
}
