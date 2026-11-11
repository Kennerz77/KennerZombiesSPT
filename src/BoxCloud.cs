using System.Collections;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The puff of dark cloud with lightning in it when the mystery box vanishes (and a smaller one where it lands):
    /// soft grey puffs billowing out, jagged blue-white bolts flickering through them and a flash of light, all gone
    /// in about two seconds.
    /// </summary>
    internal class BoxCloud : MonoBehaviour
    {
        private const float Life = 2.2f;
        private static Texture2D _puff;
        private static readonly Color Bolt = new Color(0.75f, 0.85f, 1f);

        public static void Puff(Vector3 at, float size)
        {
            var go = new GameObject("Zombies_BoxCloud (KennerZombies)");
            go.transform.position = at;
            go.AddComponent<BoxCloud>().StartCoroutine(go.GetComponent<BoxCloud>().Run(size));
        }

        private IEnumerator Run(float size)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Destroy(gameObject);
                yield break;
            }

            // The cloud: a burst of soft grey puffs, growing and fading
            ParticleSystem ps = gameObject.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 1.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f * size, 1.1f * size);
            main.startSize = new ParticleSystem.MinMaxCurve(0.6f * size, 1.3f * size);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.22f, 0.22f, 0.26f, 0.85f), new Color(0.38f, 0.38f, 0.44f, 0.75f));
            main.maxParticles = 64;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.4f * size;
            ParticleSystem.SizeOverLifetimeModule grow = ps.sizeOverLifetime;
            grow.enabled = true;
            grow.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.6f));
            ParticleSystem.ColorOverLifetimeModule fade = ps.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(0.8f, 0.6f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;
            var renderer = GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = new Material(shader) { mainTexture = Puff() };
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ps.Emit(28);

            // The light: a hard flicker of blue-white, then gone
            var lightGo = new GameObject("flash");
            lightGo.transform.SetParent(transform, false);
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Bolt;
            light.range = 6f * size;
            light.shadows = LightShadows.None;

            // Lightning: a few jagged bolts, redrawn and flickering for the first second
            var bolts = new LineRenderer[3];
            var boltMaterial = new Material(shader);
            for (int i = 0; i < bolts.Length; i++)
            {
                var b = new GameObject("bolt").AddComponent<LineRenderer>();
                b.transform.SetParent(transform, false);
                b.useWorldSpace = true;
                b.sharedMaterial = boltMaterial;
                b.widthMultiplier = 0.025f * size;
                b.positionCount = 7;
                b.startColor = b.endColor = Bolt;
                bolts[i] = b;
            }

            float start = Time.time;
            while (Time.time < start + Life)
            {
                float t = Time.time - start;
                bool striking = t < 1.1f && Random.value < 0.55f;
                light.intensity = striking ? Random.Range(1f, 2f) : Mathf.Max(0f, 0.6f - t * 0.8f);
                foreach (LineRenderer b in bolts)
                {
                    b.enabled = striking && Random.value < 0.7f;
                    if (b.enabled)
                        Jag(b, transform.position, size);
                }
                yield return new WaitForSeconds(0.05f);
            }
            Destroy(boltMaterial);
            Destroy(renderer.sharedMaterial);
            Destroy(gameObject);
        }

        /// <summary>A bolt from inside the cloud down and outwards, kinked at every step.</summary>
        private static void Jag(LineRenderer b, Vector3 from, float size)
        {
            Vector3 dir = (Random.insideUnitSphere + Vector3.down * 0.8f).normalized;
            Vector3 p = from + Random.insideUnitSphere * 0.2f * size;
            for (int i = 0; i < b.positionCount; i++)
            {
                b.SetPosition(i, p);
                p += (dir + Random.insideUnitSphere * 0.7f).normalized * (0.18f * size);
            }
        }

        /// <summary>A soft round puff, a little lumpy, for the cloud particles.</summary>
        private static Texture2D Puff()
        {
            if (_puff != null)
                return _puff;
            const int n = 64;
            _puff = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float lump = 0.85f + 0.15f * Mathf.PerlinNoise(x * 0.15f, y * 0.15f);
                    float a = Mathf.Clamp01(1f - d) * lump;
                    _puff.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            _puff.Apply();
            return _puff;
        }
    }
}
