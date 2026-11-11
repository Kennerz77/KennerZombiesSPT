using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Lightning placed in F8, for the map's lore: jagged blue-white bolts and a flicker of light, over and over.
    /// "crackle": small arcs spitting out every way, often, over a sparking hum (Shadows of Evil's electricity).
    /// "storm": big bolts striking down, now and then, with the original's thunder crack. Its scale sets how big.
    /// </summary>
    internal class ZombiesLightning : MonoBehaviour
    {
        private static readonly Color Bolt = new Color(0.6f, 0.8f, 1f);
        private const int Segments = 8;

        private MapEditor.Entry _entry;
        private LineRenderer[] _bolts;
        private Material _material;
        private Light _light;
        private AudioSource _loop;
        private float _nextBurst, _burstEnds, _nextRedraw;

        public void Init(MapEditor.Entry entry) => _entry = entry;

        private bool Storm => _entry?.What == "storm";

        // "surge": a door or debris going (ZombiesMatch.Buyable): arcs all over its area, without a break, no sound of its own
        private bool Surge => _entry?.What == "surge";

        /// <summary>The area a surge's arcs come from (the door's or debris' size).</summary>
        public Vector3 Box = Vector3.one;

        /// <summary>A surge of lightning over this area for a while, then gone (a door or debris being bought).</summary>
        public static void SurgeAt(Vector3 middle, Vector3 size, float seconds)
        {
            var go = new GameObject("Zombies_ClearLightning");
            go.transform.position = middle;
            var bolts = go.AddComponent<ZombiesLightning>();
            bolts.Init(new MapEditor.Entry { What = "surge", Scale = Mathf.Clamp(Mathf.Max(size.x, size.y) * 0.5f, 0.6f, 2f) });
            bolts.Box = size;
            Destroy(go, seconds);
        }

        private float Size => Mathf.Max(0.1f, _entry != null && _entry.Scale > 0f ? _entry.Scale : 1f);

        private void Start()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                enabled = false;
                return;
            }
            _material = new Material(shader);
            _bolts = new LineRenderer[4];
            for (int i = 0; i < _bolts.Length; i++)
            {
                var b = new GameObject("bolt").AddComponent<LineRenderer>();
                b.transform.SetParent(transform, false);
                b.useWorldSpace = true;
                b.sharedMaterial = _material;
                b.positionCount = Segments;
                b.startColor = b.endColor = Bolt;
                b.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                b.enabled = false;
                _bolts[i] = b;
            }
            var lightGo = new GameObject("flash");
            lightGo.transform.SetParent(transform, false);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = Bolt;
            _light.shadows = LightShadows.None;
            _light.intensity = 0f;
            _nextBurst = Time.time + Random.Range(0.2f, 1.5f);
        }

        private void Update()
        {
            if (_bolts == null)
                return;
            float now = Time.time, size = Size;
            bool storm = Storm;
            EnsureLoop(storm, size);
            if (Surge && now >= _nextBurst)
            {
                _burstEnds = now + Random.Range(0.12f, 0.3f);
                _nextBurst = _burstEnds + Random.Range(0.02f, 0.12f);
            }
            else if (now >= _nextBurst)
            {
                _burstEnds = now + (storm ? Random.Range(0.25f, 0.6f) : Random.Range(0.15f, 0.5f));
                _nextBurst = _burstEnds + (storm ? Random.Range(2.5f, 7f) : Random.Range(0.3f, 2.2f));
                ZombiesSounds.At(storm ? "zm_lightning_flash_#" : "zm_lightning_strike_#", transform.position,
                    storm ? 1f : 0.45f, (storm ? 80f : 25f) * size);
            }
            if (now < _nextRedraw)
                return;
            _nextRedraw = now + 0.05f;
            bool on = now < _burstEnds;
            _light.range = (storm ? 14f : Surge ? 4f : 5f) * size;
            _light.intensity = on && Random.value < 0.7f ? Random.Range(0.8f, 2f) * (storm ? 1.4f : 1f) : 0f;
            foreach (LineRenderer b in _bolts)
            {
                b.enabled = on && Random.value < 0.75f;
                if (b.enabled)
                    Jag(b, storm, size);
            }
        }

        // The crackle's sparking hum, 3D (a storm has none)
        private void EnsureLoop(bool storm, float size)
        {
            if (storm || Surge)
            {
                if (_loop != null)
                    _loop.Stop();
                return;
            }
            if (_loop == null)
            {
                AudioClip clip = ZombiesSounds.Clip("zm_lightning_loop");
                if (clip == null)
                    return;
                _loop = gameObject.AddComponent<AudioSource>();
                _loop.clip = clip;
                _loop.loop = true;
                _loop.spatialBlend = 1f;
                _loop.rolloffMode = AudioRolloffMode.Linear;
                _loop.minDistance = 0.5f;
                _loop.dopplerLevel = 0f;
                _loop.volume = 0.35f * ZombiesSounds.Volume(ZombiesSounds.Group.Lore);
                _loop.time = Random.value * clip.length;
            }
            _loop.maxDistance = 18f * size;
            if (!_loop.isPlaying)
                _loop.Play();
        }

        /// <summary>A bolt from here: a storm's strikes down, a crackle's arcs spit out any way, kinked at every step.</summary>
        private void Jag(LineRenderer b, bool storm, float size)
        {
            Vector3 from = transform.position;
            if (Surge)
            {
                // From anywhere in the area, short and kinked
                from += Vector3.Scale(new Vector3(Random.Range(-0.5f, 0.5f), Random.Range(-0.5f, 0.5f), Random.Range(-0.5f, 0.5f)), Box);
                storm = false;
            }
            Vector3 dir = storm ? (Vector3.down + Random.insideUnitSphere * 0.6f).normalized : Random.onUnitSphere;
            float length = (storm ? Random.Range(2f, 4f) : Random.Range(0.4f, 1.2f)) * size;
            float kink = (storm ? 0.35f : 0.12f) * size;
            b.widthMultiplier = (storm ? 0.05f : 0.02f) * size;
            for (int i = 0; i < Segments; i++)
            {
                float t = i / (float)(Segments - 1);
                Vector3 p = from + dir * length * t;
                if (i > 0 && i < Segments - 1)
                    p += Random.insideUnitSphere * kink;
                b.SetPosition(i, p);
            }
        }

        private void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
        }
    }
}
