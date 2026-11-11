using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's chalk outline for a wall buy, drawn from the gun model itself: the model is photographed side-on by a hidden
    /// camera (flat white on clear, on a layer of its own), and the silhouette's edge becomes a thick chalk line, broken
    /// up like chalk on a rough wall (the Thundergun mod's chalk, from a picture instead of its icon). Every gun gets its
    /// own outline, nothing to draw by hand.
    /// </summary>
    internal static class ZombiesChalk
    {
        private const int Width = 512;

        /// <summary>The chalk picture of this model as seen from <paramref name="outOfWall"/>, and its size in metres.</summary>
        public static Texture2D Draw(GameObject model, Vector3 outOfWall, Vector3 up, out Vector2 size)
        {
            Texture2D shot = Shot(model, outOfWall, up, Width, out size);
            if (shot == null)
                return null;
            Texture2D chalk = Chalk(shot);
            Object.Destroy(shot);
            return chalk;
        }

        /// <summary>
        /// A chalk picture of every gun in box_builds.json (wall, box, starter, wonder), made from each gun's own model the way
        /// a wall buy draws its outline, saved to plugins\Kennerz_ZM_Core\chalk\&lt;id&gt;.png to choose wall buys from.
        /// </summary>
        public static IEnumerator Sheet()
        {
            string dir = Path.Combine(ZombiesPlugin.Dir, "chalk");
            Directory.CreateDirectory(dir);
            System.Collections.Generic.List<string> ids;
            try
            {
                ids = JArray.Parse(File.ReadAllText(Path.Combine(ZombiesPlugin.Dir, "box_builds.json"))).OfType<JObject>().Select(o => (string)o["id"]).Where(i => i != null).ToList();
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogError($"Chalk sheet: couldn't read box_builds.json: {e.Message}");
                yield break;
            }
            ZombiesPlugin.Notify($"Making chalk pictures for {ids.Count} guns (plugins\\Kennerz_ZM_Core\\chalk)...");
            int made = 0;
            foreach (string id in ids)
            {
                BoxGuns.Gun gun = BoxGuns.FindGun(id);
                if (gun == null)
                    continue;
                BoxGuns.Model model = null;
                yield return BoxGuns.MakeModel(gun, m => model = m);
                if (model?.Go == null)
                {
                    ZombiesPlugin.Log.LogWarning($"Chalk sheet: {id}: no model");
                    continue;
                }
                var holder = new GameObject("Zombies_ChalkSheet");
                holder.transform.position = new Vector3(0f, 3000f, 0f);
                model.Go.transform.SetParent(holder.transform, false);
                model.Go.transform.localPosition = Vector3.zero;
                model.Go.transform.localRotation = model.Align;
                yield return null;
                Texture2D chalk = Draw(model.Go, Vector3.forward, Vector3.up, out Vector2 size);
                if (chalk != null)
                {
                    File.WriteAllBytes(Path.Combine(dir, id + ".png"), chalk.EncodeToPNG());
                    made++;
                    ZombiesPlugin.Log.LogInfo($"Chalk sheet: {id} {size.x:0.00} x {size.y:0.00} m");
                    Object.Destroy(chalk);
                }
                else
                    ZombiesPlugin.Log.LogWarning($"Chalk sheet: {id}: couldn't be drawn");
                Object.Destroy(holder);
                yield return null;
            }
            ZombiesPlugin.Notify($"Chalk pictures done: {made} of {ids.Count} (plugins\\Kennerz_ZM_Core\\chalk).");
        }

        /// <summary>The model's plain white silhouette as seen from <paramref name="towards"/> (a HUD icon).</summary>
        public static Texture2D Silhouette(GameObject model, Vector3 towards, Vector3 up, int width) => Shot(model, towards, up, width, out _);

        private static Texture2D Shot(GameObject model, Vector3 outOfWall, Vector3 up, int width, out Vector2 size)
        {
            size = Vector2.one;
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return null;
            Bounds b = renderers[0].bounds;
            foreach (Renderer r in renderers)
                b.Encapsulate(r.bounds);

            // The picture's frame: the model's extent across and up the wall
            Vector3 right = Vector3.Cross(up, outOfWall).normalized;
            float w = 0f, h = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                w = Mathf.Max(w, Mathf.Abs(Vector3.Dot(corner - b.center, right)));
                h = Mathf.Max(h, Mathf.Abs(Vector3.Dot(corner - b.center, up)));
            }
            w *= 2.1f;
            h *= 2.1f;
            size = new Vector2(w, h);
            int pw = width, ph = Mathf.Clamp(Mathf.RoundToInt(width * h / Mathf.Max(0.01f, w)), 16, 1024);

            // A layer nobody uses, for the camera to see only the gun
            int layer = 31;
            for (int i = 31; i >= 8; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                {
                    layer = i;
                    break;
                }
            // Every surface plain white for the shot (the gun's own textures can be see-through in their alpha, which
            // left the outline empty), shown even when hidden, then all put back
            var saved = new int[renderers.Length];
            var savedMaterials = new Material[renderers.Length][];
            var savedOn = new bool[renderers.Length];
            var white = new Material(Shader.Find("Sprites/Default")) { color = Color.white };
            for (int i = 0; i < renderers.Length; i++)
            {
                saved[i] = renderers[i].gameObject.layer;
                renderers[i].gameObject.layer = layer;
                savedMaterials[i] = renderers[i].sharedMaterials;
                savedOn[i] = renderers[i].enabled;
                renderers[i].sharedMaterials = Enumerable.Repeat(white, Mathf.Max(1, savedMaterials[i].Length)).ToArray();
                renderers[i].enabled = true;
            }

            var camGo = new GameObject("Zombies_ChalkCamera");
            Camera cam = camGo.AddComponent<Camera>();
            cam.enabled = false;
            cam.orthographic = true;
            cam.orthographicSize = h * 0.5f;
            cam.aspect = w / h;
            cam.cullingMask = 1 << layer;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.renderingPath = RenderingPath.Forward;
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 20f;
            cam.transform.SetPositionAndRotation(b.center + outOfWall * 5f, Quaternion.LookRotation(-outOfWall, up));
            var rt = new RenderTexture(pw, ph, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            Texture2D shot = null;
            try
            {
                // Flat: every surface drawn plain white (only the shape matters)
                cam.Render();
                RenderTexture before = RenderTexture.active;
                RenderTexture.active = rt;
                shot = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, pw, ph), 0, 0);
                shot.Apply();
                RenderTexture.active = before;
            }
            finally
            {
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null)
                    {
                        renderers[i].gameObject.layer = saved[i];
                        renderers[i].sharedMaterials = savedMaterials[i];
                        renderers[i].enabled = savedOn[i];
                    }
                Object.Destroy(white);
                cam.targetTexture = null;
                Object.Destroy(rt);
                Object.Destroy(camGo);
            }
            return shot;
        }

        // The silhouette's edge as a chalk line (Thundergun's chalk)
        private static Texture2D Chalk(Texture2D source)
        {
            int w = source.width, h = source.height;
            Color32[] src = source.GetPixels32();
            var solid = new bool[w * h];
            for (int i = 0; i < src.Length; i++)
                solid[i] = src[i].a > 60;
            var random = new System.Random(935);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool any = false, all = true;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            bool s = xx >= 0 && yy >= 0 && xx < w && yy < h && solid[yy * w + xx];
                            any |= s;
                            all &= s;
                        }
                    float a = any && !all ? 0.95f : 0f;
                    if (a > 0f)
                    {
                        a *= 0.65f + 0.35f * Mathf.PerlinNoise(x * 0.09f, y * 0.09f);
                        if (random.NextDouble() < 0.14)
                            a *= 0.25f;
                    }
                    pixels[y * w + x] = new Color32(238, 242, 255, (byte)(a * 255));
                }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "zm_chalk", wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(pixels);
            tex.Apply(true, false);
            return tex;
        }
    }
}
