using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's blue beam over the mystery box, like the Halloween event's ritual beam: a tall soft shaft of blue light out
    /// of the box's question marks. It's a camera-facing strip, barely there up close and plainer the further away
    /// the player is, so it marks the box across a map without glaring next to it.
    /// </summary>
    internal class BoxBeam : MonoBehaviour
    {
        // Narrow at the box, spreading out higher up (the user, 2026-11-01); twice as tall (the user, 2026-11-01)
        private const float FootWidth = 0.5f, TopWidth = 6f, Height = 80f;
        private const float NearDistance = 3f, FarDistance = 40f;
        // Stronger from afar (the user wanted it brighter higher up, 2026-11-01)
        private const float NearAlpha = 0.03f, FarAlpha = 0.75f;
        private static readonly Color Blue = new Color(0.35f, 0.65f, 1f);

        private Transform _box;
        private Material _material;
        private float _shown = 1f, _target = 1f;

        public static BoxBeam Create(Transform box)
        {
            var go = new GameObject("Zombies_BoxBeam (KennerZombies)");
            go.AddComponent<MeshFilter>().sharedMesh = Fan();
            go.AddComponent<MeshRenderer>();
            var beam = go.AddComponent<BoxBeam>();
            beam._box = box;
            beam._material = new Material(Shader.Find("Sprites/Default")) { mainTexture = Strip(), color = Blue };
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = beam._material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return beam;
        }

        /// <summary>Fades the beam out (the box is away) or back in.</summary>
        public void Show(bool on) => _target = on ? 1f : 0f;

        private void LateUpdate()
        {
            if (_box == null)
            {
                Destroy(gameObject);
                return;
            }
            Camera camera = ZombiesInteraction.ActiveCamera;
            // Stands on the box's middle, its foot at the lid (a little above the box's origin), turned to face the camera
            Vector3 foot = _box.position + Vector3.up * 0.4f;
            transform.position = foot;
            if (camera != null)
            {
                Vector3 toCamera = Vector3.ProjectOnPlane(camera.transform.position - transform.position, Vector3.up);
                if (toCamera.sqrMagnitude > 1e-4f)
                    transform.rotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
            }
            _shown = Mathf.MoveTowards(_shown, _target, Time.deltaTime * 0.8f);
            float distance = camera != null ? Vector3.Distance(camera.transform.position, foot) : FarDistance;
            // Seen up close it hardly shows; from afar (or looking up it from below) it reads
            float alpha = Mathf.Lerp(NearAlpha, FarAlpha, Mathf.InverseLerp(NearDistance, FarDistance, distance)) * _shown;
            Color c = Blue;
            c.a = alpha;
            _material.color = c;
        }

        private void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
        }

        /// <summary>The beam's shape: a flat strip from the foot up, widening towards the top (its own space: y up, facing -z).</summary>
        private static Mesh Fan()
        {
            var mesh = new Mesh { name = "box_beam" };
            mesh.vertices = new[]
            {
                new Vector3(-FootWidth * 0.5f, 0f, 0f), new Vector3(FootWidth * 0.5f, 0f, 0f),
                new Vector3(-TopWidth * 0.5f, Height, 0f), new Vector3(TopWidth * 0.5f, Height, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The beam's picture: brightest down the middle, soft at the sides; faint at the foot, growing stronger
        /// with height to its brightest high up, fading out only in its last stretch.</summary>
        private static Texture2D Strip()
        {
            const int w = 32, h = 256;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
            {
                float up = y / (float)(h - 1);
                // Faint at the foot, stronger the higher it goes (to ~85% of its height), then fading out at the top
                float vertical = Mathf.Lerp(0.15f, 1f, Mathf.SmoothStep(0f, 1f, up / 0.85f)) * Mathf.Clamp01((1f - up) / 0.15f)
                                  * Mathf.Clamp01(up / 0.02f);
                for (int x = 0; x < w; x++)
                {
                    float across = Mathf.Abs(x / (float)(w - 1) * 2f - 1f);
                    // A wider bright core higher up, so the top reads as a solid shaft
                    float core = Mathf.Pow(1f - across, Mathf.Lerp(2.2f, 1.4f, up));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, core * vertical));
                }
            }
            tex.Apply();
            return tex;
        }
    }
}
