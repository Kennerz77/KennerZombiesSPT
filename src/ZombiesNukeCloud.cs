using System.IO;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The Nuke's look on each zombie: a flat 2D mushroom cloud that rises, opens and fades (a flipbook billboard from
    /// hud\zm_nuke_cloud.png, 4 x 2 frames), cheap next to a full explosion effect per zombie.
    /// </summary>
    internal class ZombiesNukeCloud : MonoBehaviour
    {
        private const float Seconds = 1.4f, Height = 2.6f;
        private const int Columns = 4, Rows = 2, Frames = 8;

        private static Texture2D _sheet;
        private static Material _material;
        private static bool _tried;

        private Mesh _mesh;
        private float _born;
        private int _frame = -1;
        private readonly Vector2[] _uv = new Vector2[4];

        public static void At(Vector3 ground)
        {
            if (!Load())
                return;
            var go = new GameObject("Zombies_NukeCloud");
            go.transform.position = ground;
            go.AddComponent<ZombiesNukeCloud>();
            Destroy(go, Seconds + 0.1f);
        }

        private static bool Load()
        {
            if (_tried)
                return _material != null;
            _tried = true;
            try
            {
                string file = Path.Combine(ZombiesPlugin.Dir, "hud", "zm_nuke_cloud.png");
                Shader shader = Shader.Find("Sprites/Default");
                if (!File.Exists(file) || shader == null)
                    return false;
                _sheet = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                if (!ImageConversion.LoadImage(_sheet, File.ReadAllBytes(file)))
                    return false;
                _material = new Material(shader) { mainTexture = _sheet };
                return true;
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies nuke cloud: not loaded ({e.Message})");
                return false;
            }
        }

        private void Start()
        {
            _born = Time.time;
            float w = Height * 0.5f;
            _mesh = new Mesh
            {
                vertices = new[] { new Vector3(-w, 0f, 0f), new Vector3(w, 0f, 0f), new Vector3(-w, Height, 0f), new Vector3(w, Height, 0f) },
                triangles = new[] { 0, 2, 1, 2, 3, 1 },
            };
            _mesh.bounds = new Bounds(new Vector3(0f, Height * 0.5f, 0f), Vector3.one * Height);
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            MeshRenderer r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            LateUpdate();
        }

        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                // Faces the camera, upright
                Vector3 to = transform.position - cam.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.001f)
                    transform.rotation = Quaternion.LookRotation(to, Vector3.up);
            }
            int frame = Mathf.Clamp((int)((Time.time - _born) / Seconds * Frames), 0, Frames - 1);
            if (frame == _frame || _mesh == null)
                return;
            _frame = frame;
            float u = (frame % Columns) / (float)Columns, v = 1f - (frame / Columns + 1) / (float)Rows;
            float du = 1f / Columns, dv = 1f / Rows;
            _uv[0] = new Vector2(u, v);
            _uv[1] = new Vector2(u + du, v);
            _uv[2] = new Vector2(u, v + dv);
            _uv[3] = new Vector2(u + du, v + dv);
            _mesh.uv = _uv;
        }

        private void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
