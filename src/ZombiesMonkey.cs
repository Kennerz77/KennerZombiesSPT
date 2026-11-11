using System.Collections;
using BepInEx.Configuration;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's Cymbal Monkey, the zombies mode's tactical grenade (the server's item, a clone of the M67; only from the
    /// mystery box). Thrown (the tactical key) it:
    /// - shows as the the original monkey (zombies.bundle zm_monkey, its cymbal-clap anim) instead of the M67, winds up with the
    ///   ratchet, and bounces off what it hits (a bouncy physics material);
    /// - once it lands: claps its cymbals and plays one of the original's three monkey songs at random, cymbal hits on top;
    /// - draws every zombie that's climbed in to it for as long as it plays (ZombiesRounds hunts it instead of players);
    /// - goes off as the song ends, a big blast (EFT's own explosion from the M67 it's built on, plus the original's frag).
    /// The box gives three (the original's tactical slots).
    /// </summary>
    internal static class ZombiesMonkey
    {
        public const string Template = "6a8f0e5b0c0a116000000c01";
        public const int Carry = 3;
        // The original: every zombie that's in with the players and anywhere near drops what it's doing for the monkey (user, 2026-11-09)
        private const float Lure = 60f, Height = 0.5f, AfterLanding = 0.3f;

        // The original's little blue glow at the monkey's base (F12 "21. Monkey bomb")
        private static ConfigEntry<bool> _glowOn;
        private static ConfigEntry<float> _glowBright, _glowReach, _glowSize, _scale, _boxTurn;

        /// <summary>The monkey's turn in the mystery box (degrees about up).</summary>
        // The model faces its -X (the original +X): -90 faces the player at the box front, 180 faces the player's right like the
        // guns lie (the user, 2026-11-08)
        public static float BoxTurn => _boxTurn?.Value ?? 180f;

        /// <summary>
        /// You've had the monkeys this life : the box never gives them to you again until you bleed out and come
        /// back (the respawn takes them); only a Max Ammo fills you back to three. Your HUD keeps the three slots (an
        /// outline for each one used). Each player has their own (all four can have them).
        /// </summary>
        public static bool HadThisLife;

        public static void Bind(ConfigFile config, string section)
        {
            _scale = config.Bind(section, "Thrown monkey size", 1f, new ConfigDescription(
                "The thrown monkey's size (1: 0.5 m tall).", new AcceptableValueRange<float>(0.3f, 3f)));
            _boxTurn = config.Bind(section, "In the box: turn", 180f, new ConfigDescription(
                "Which way the monkey faces in the mystery box (degrees round; 180: facing your right like the guns lie, -90: facing you). Takes effect the next time the box is set out.",
                new AcceptableValueRange<float>(-180f, 180f)));
            _glowOn = config.Bind(section, "Blue glow", true, "A small blue glow at the bottom middle of a thrown monkey.");
            _glowBright = config.Bind(section, "Blue glow brightness", 1.86f, new ConfigDescription("", new AcceptableValueRange<float>(0f, 8f)));
            _glowReach = config.Bind(section, "Blue glow reach (m)", 1.035f, new ConfigDescription("", new AcceptableValueRange<float>(0.1f, 5f)));
            _glowSize = config.Bind(section, "Blue glow size (m)", 0.55f, new ConfigDescription("The soft blue radiance round its middle.", new AcceptableValueRange<float>(0f, 2f)));
            config.Bind(section, "Give 3 monkeys", false, new ConfigDescription("Three Cymbal Monkeys into your tactical slots, as the box gives.", null,
                new ZombiesPlugin.ConfigurationManagerAttributes { HideDefaultButton = true, CustomDrawer = _ =>
                {
                    if (GUILayout.Button("Give 3 monkeys", GUILayout.ExpandWidth(true)) && ZombiesPlugin.CurrentPlayer(out GamePlayerOwner _o, out Player p))
                        ZombiesPlugin.Instance.StartCoroutine(Give(p));
                } }));
        }

        private static Material _glowMat;

        // The glow: a soft blue light and a small glowing spot, at the bottom middle, pulsing gently
        private static void AddGlow(Transform on, float baseY)
        {
            if (_glowOn != null && !_glowOn.Value)
                return;
            var go = new GameObject("zm_monkey_glow");
            go.transform.SetParent(on, false);
            go.transform.localPosition = new Vector3(0f, baseY, 0f);
            Light l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(0.25f, 0.55f, 1f);
            l.intensity = _glowBright?.Value ?? 1.6f;
            l.range = _glowReach?.Value ?? 0.9f;
            l.shadows = LightShadows.None;
            float size = _glowSize?.Value ?? 0.55f;
            if (size > 0f)
            {
                if (_glowMat == null && Shader.Find("Sprites/Default") is Shader sh)
                {
                    const int n = 64;
                    var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                        {
                            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                            float a = Mathf.Clamp01(1f - d);
                            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a * a));
                        }
                    tex.Apply();
                    _glowMat = new Material(sh) { mainTexture = tex, color = new Color(0.35f, 0.65f, 1f, 0.8f) };
                }
                // A camera-facing glow at its middle: the monkey in front hides part of it, so it shines out from within
                GameObject spot = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Object.Destroy(spot.GetComponent<Collider>());
                spot.name = "radiance";
                spot.transform.SetParent(go.transform, false);
                spot.transform.localScale = Vector3.one * size;
                Renderer r = spot.GetComponent<Renderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                if (_glowMat != null)
                    r.sharedMaterial = _glowMat;
                spot.AddComponent<FaceCam>();
            }
            go.AddComponent<GlowPulse>().Light = l;
        }

        private class FaceCam : MonoBehaviour
        {
            private void LateUpdate()
            {
                Camera cam = Camera.main;
                if (cam != null)
                    transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, cam.transform.up);
            }
        }

        private class GlowPulse : MonoBehaviour
        {
            public Light Light;
            private float _base;

            private void Start() => _base = Light != null ? Light.intensity : 0f;

            private void Update()
            {
                if (Light != null)
                    Light.intensity = _base * (0.8f + 0.2f * Mathf.Sin(Time.time * 6f));
            }
        }

        private class Live
        {
            public Grenade Grenade;
            public bool Landed;
            public float LandedAt;
        }

        private static readonly List<Live> Monkeys = new List<Live>();
        private static PhysicMaterial _bouncy;

        public static bool Is(Item item) => item != null && item.TemplateId.ToString() == Template;

        /// <summary>How long a monkey plays before it goes off, from its landing (the song's length).</summary>
        public static float SongSeconds = 7.7f;

        /// <summary>A thrown monkey's fuse (StartTimer): the song plus its flight, about 9 s from the throw.</summary>
        public const float FuseSeconds = 9f;

        /// <summary>A monkey was thrown (its timer started): its look, its bounce, its sounds and the lure.</summary>
        public static void Thrown(Grenade g)
        {
            if (g == null || Monkeys.Any(m => m.Grenade == g))
                return;
            var live = new Live { Grenade = g };
            Monkeys.Add(live);
            ZombiesPlugin.Instance.StartCoroutine(Run(live));
        }

        /// <summary>Where a zombie at <paramref name="from"/> is drawn to now: the nearest playing monkey within reach, or null.</summary>
        public static Vector3? LureFor(Vector3 from)
        {
            Monkeys.RemoveAll(m => m.Grenade == null);
            Live best = null;
            float bestD = Lure;
            foreach (Live m in Monkeys)
            {
                if (!m.Landed)
                    continue;
                float d = Vector3.Distance(from, m.Grenade.transform.position);
                if (d < bestD)
                {
                    bestD = d;
                    best = m;
                }
            }
            return best != null ? best.Grenade.transform.position : (Vector3?)null;
        }

        public static void Clear()
        {
            Monkeys.Clear();
            HadThisLife = false;
        }

        /// <summary>Bled out and back : the monkeys you carried are gone and the box can give them again.</summary>
        public static void TakeAll(Player player)
        {
            HadThisLife = false;
            InventoryController inventory = player?.InventoryController;
            if (inventory == null)
                return;
            foreach (Item m in inventory.Inventory.Equipment.GetAllItems().Where(Is).ToList())
            {
                try
                {
                    var removed = ItemManipulator.Remove(m, inventory, false, true);
                    if (removed.Succeeded)
                        ZombiesItems.Removed(inventory, removed, m);
                }
                catch (System.Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies: respawn couldn't take a Cymbal Monkey ({e.Message})");
                }
            }
        }

        // Thrown grenades as the game registers them (GameWorld.RegisterGrenade): looked at the next frame, once their
        // weapon is set. This replaced a FindObjectsOfType<Grenade> scan 4 times a second, which took ~40 ms on the map
        // each time (the stutter: the user's walking glitch, 2026-11-07)
        private static readonly List<Grenade> Registered = new List<Grenade>();
        private static bool _hooked;

        private static void Hook()
        {
            if (_hooked)
                return;
            _hooked = true;
            var m = HarmonyLib.AccessTools.Method(typeof(GameWorld), nameof(GameWorld.RegisterGrenade));
            if (m != null)
                ZombiesPlugin.HarmonyInstance.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ZombiesMonkey), nameof(GrenadeRegistered)));
            else
                ZombiesPlugin.Log.LogWarning("Zombies monkey: GameWorld.RegisterGrenade not found, thrown monkeys won't play");
        }

        private static void GrenadeRegistered(Throwable grenade)
        {
            if (grenade is Grenade g && ZombiesMatch.Active)
                Registered.Add(g);
        }

        /// <summary>
        /// Every quarter second in a match: any thrown monkey not yet dressed (one a teammate threw, made on this game
        /// by Fika, whose timer never started here) gets the same look, bounce, sounds and lure.
        /// </summary>
        public static void Tick()
        {
            Hook();
            if (!ZombiesMatch.Active || Registered.Count == 0)
                return;
            for (int i = Registered.Count - 1; i >= 0; i--)
            {
                Grenade g = Registered[i];
                if (g == null)
                {
                    Registered.RemoveAt(i);
                    continue;
                }
                if (g.WeaponSource == null)
                    continue; // not set up yet: next frame
                Registered.RemoveAt(i);
                if (Is(g.WeaponSource) && !Monkeys.Any(m => m.Grenade == g))
                    Thrown(g);
                else if (!Is(g.WeaponSource))
                    Grip(g);
            }
        }

        private static PhysicMaterial _grippy;

        /// <summary>
        /// Every other grenade (the M67s): no bounce and a lot of grip, and its spin damped so it stops rolling, so it lands about
        /// where it was thrown instead of skipping and rolling away (the user, 2026-11-08).
        /// </summary>
        private static void Grip(Grenade g)
        {
            if (_grippy == null)
                _grippy = new PhysicMaterial("zm_grenade_grip") { bounciness = 0.02f, bounceCombine = PhysicMaterialCombine.Minimum,
                    dynamicFriction = 1.2f, staticFriction = 1.5f, frictionCombine = PhysicMaterialCombine.Maximum };
            foreach (Collider c in g.GetComponentsInChildren<Collider>(true))
                c.sharedMaterial = _grippy;
            Rigidbody body = g.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.angularDrag = Mathf.Max(body.angularDrag, 4f);
            }
        }

        // The thrower's own game sets it off (Fika passes the blast on); another's only if it's still there a moment
        // after its song (no blast came over)
        private static bool Mine(Grenade g)
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            return me == null || g.ProfileId == me.ProfileId;
        }

        public static int Count(Player player) =>
            player?.InventoryController?.Inventory.Equipment.GetAllItems().Count(Is) ?? 0;

        /// <summary>From the box (or a Max Ammo): the tactical slots filled to three monkeys.</summary>
        public static IEnumerator Give(Player player, string from = "the box")
        {
            if (player?.InventoryController == null)
                yield break;
            if (player.IsYourPlayer)
                HadThisLife = true;
            int need = Carry - Count(player);
            if (need <= 0)
                yield break;
            Item[] monkeys = Enumerable.Range(0, need).Select(_ => Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), Template, null))
                .Where(i => i != null).ToArray();
            if (monkeys.Length == 0)
            {
                ZombiesPlugin.Log.LogWarning("Zombies: no Cymbal Monkey item (is the zombies server mod loaded?)");
                yield break;
            }
            yield return ZombiesItems.LoadModels(monkeys);
            InventoryController inventory = player.InventoryController;
            int given = 0;
            foreach (Item m in monkeys)
            {
                ItemAddress place = ZombiesItems.FindGrid(inventory, m);
                if (place == null || !ZombiesItems.Add(inventory, m, place, out _))
                    break;
                given++;
            }
            ZombiesPlugin.Log.LogInfo($"Zombies: {given} Cymbal Monkey(s) from {from} ({Count(player)} carried)");
        }

        // The thrown monkey's bounce sounds: a random take on each landing hard enough (not every tiny settle)
        private class Bounces : MonoBehaviour
        {
            private float _last;

            private int _hits;

            private void OnCollisionEnter(Collision c)
            {
                if (c.relativeVelocity.magnitude < 1.2f || Time.time - _last < 0.12f)
                    return;
                // Two short hops from the first impact, then it stays put
                if (++_hits >= 3)
                {
                    var body = GetComponent<Rigidbody>();
                    if (body != null)
                        body.velocity = new Vector3(body.velocity.x * 0.2f, Mathf.Min(0f, body.velocity.y), body.velocity.z * 0.2f);
                }
                _last = Time.time;
                float loud = Mathf.Clamp01(c.relativeVelocity.magnitude / 8f);
                ZombiesSounds.At("zm_monkey_bounce_#", transform.position, 0.35f + 0.6f * loud, 25f);
                // Its "oof!" / "ow!" on every bounce (the original's monkey land grunts; user 2026-11-09), never two on top of each other
                if (Time.time - _lastOof > 0.3f)
                {
                    _lastOof = Time.time;
                    ZombiesSounds.At("zm_monkey_vox_land_#", transform.position, 0.6f + 0.35f * loud, 30f);
                }
            }

            private float _lastOof = -10f;
        }

        /// <summary>
        /// Where a monkey can't do its job: out of the building (no roof over where it landed), where the zombies
        /// inside can't get to it. Roofs: the map's rain cover (WeatherObstacle, the SDK's roof scan), else a ray up.
        /// </summary>
        public static bool OutOfBounds(Vector3 at)
        {
            try
            {
                WeatherObstacle cover = WeatherObstacle.Instance;
                if (cover != null && cover.MeshCollider != null)
                    return !cover.MeshCollider.Raycast(new Ray(at + Vector3.up * 100f, Vector3.down), out _, 100f);
            }
            catch
            {
            }
            int solid = LayerMask.GetMask("Default", "HighPolyCollider", "LowPolyCollider", "Terrain");
            return !Physics.Raycast(at + Vector3.up * 0.5f, Vector3.up, 40f, solid, QueryTriggerInteraction.Ignore);
        }

        /// <summary>the original's way with a monkey thrown out of the map: gone in a flash of lightning, the teddy's laugh, no blast.</summary>
        private static void Vanish(Grenade g)
        {
            Vector3 at = g.transform.position;
            ZombiesPlugin.Log.LogInfo($"Zombies: Cymbal Monkey landed out of the building at {at}: gone in lightning");
            BoxCloud.Puff(at + Vector3.up * 0.4f, 0.8f);
            ZombiesSounds.At("zm_lightning_strike_#", at, 1f, 60f);
            AudioClip laugh = LaughClip();
            if (laugh != null)
            {
                var go = new GameObject("Zombies_MonkeyLaugh");
                go.transform.position = at;
                AudioSource src = go.AddComponent<AudioSource>();
                src.clip = laugh;
                src.spatialBlend = 1f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 4f;
                src.maxDistance = 60f;
                src.volume = ZombiesSounds.Volume(ZombiesSounds.Group.PowerUps);
                src.Play();
                Object.Destroy(go, laugh.length + 0.2f);
            }
            // Its own fuse never runs out: the grenade is gone with no explosion
            Object.Destroy(g.gameObject);
        }

        private static AudioClip _laugh;

        // The mystery box's teddy laugh (zombies.bundle box, its Audio_Laugh source)
        private static AudioClip LaughClip()
        {
            if (_laugh != null)
                return _laugh;
            GameObject box = ZombiesAssets.Box();
            AudioSource src = box != null ? box.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(a => a.name == "Audio_Laugh") : null;
            _laugh = src != null ? src.clip : null;
            return _laugh;
        }

        private static IEnumerator Run(Live live)
        {
            Grenade g = live.Grenade;
            // Bouncy: everything it hits throws it back up a little (the original's monkey skitters about)
            if (_bouncy == null)
                _bouncy = new PhysicMaterial("zm_monkey_bounce") { bounciness = 0.28f, bounceCombine = PhysicMaterialCombine.Maximum,
                    dynamicFriction = 0.6f, staticFriction = 0.8f, frictionCombine = PhysicMaterialCombine.Maximum };
            foreach (Collider c in g.GetComponentsInChildren<Collider>(true))
                c.sharedMaterial = _bouncy;
            // A clatter on every hard bounce (the original's equipment bounces, a take at random)
            if (g.GetComponent<Bounces>() == null)
                g.gameObject.AddComponent<Bounces>();
            // Never tumbling: it flies and lands upright (the monkey sits up, it never rolls or spins)
            Rigidbody body = g.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.freezeRotation = true;
                body.angularVelocity = Vector3.zero;
                Vector3 fwd = Vector3.ProjectOnPlane(g.transform.forward, Vector3.up);
                g.transform.rotation = Quaternion.LookRotation(fwd.sqrMagnitude > 0.01f ? fwd : Vector3.forward, Vector3.up);
            }
            // The M67 hidden, the monkey in its place
            foreach (Renderer r in g.GetComponentsInChildren<Renderer>(true))
                r.enabled = false;
            GameObject model = null;
            Animator anim = null;
            GameObject prefab = ZombiesAssets.Model("zm_monkey");
            if (prefab != null)
            {
                model = Object.Instantiate(prefab, g.transform, false);
                model.name = "zm_monkey";
                Bounds b = new Bounds(model.transform.position, Vector3.zero);
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
                    b.Encapsulate(r.bounds);
                float k = (b.size.y > 0.01f ? Height / b.size.y : 0.25f) * (_scale?.Value ?? 1f);
                model.transform.localScale *= k;
                model.transform.localPosition = new Vector3(0f, -0.05f, 0f);
                // The base: the model's lowest point, in the grenade's own space
                Bounds after = new Bounds(model.transform.position, Vector3.zero);
                foreach (Renderer r in model.GetComponentsInChildren<Renderer>())
                    after.Encapsulate(r.bounds);
                AddGlow(g.transform, g.transform.InverseTransformPoint(after.center).y);
                anim = model.GetComponentInChildren<Animator>();
                if (anim != null)
                {
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    anim.Play("rest", 0, 0f);
                }
            }
            // Thrown: the monkey's own voice as it leaves the hand (the original zmb/monkey/throw), riding with it
            ZombiesSounds.On("zm_monkey_vox_throw_#", g.transform, 0.9f, 30f);
            // Landed: barely moving for a moment
            Rigidbody rb = g.GetComponent<Rigidbody>();
            float still = 0f, started = Time.time;
            while (g != null && Time.time - started < 6f)
            {
                float v = rb != null ? rb.velocity.magnitude : 0f;
                still = v < 0.25f ? still + Time.deltaTime : 0f;
                if (still >= AfterLanding)
                    break;
                yield return null;
            }
            if (g == null)
                yield break;
            if (OutOfBounds(g.transform.position))
            {
                Monkeys.Remove(live);
                Vanish(g);
                yield break;
            }
            live.Landed = true;
            live.LandedAt = Time.time;
            // Settled where it is: no more bouncing or sliding about while it plays
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
            // Upright on the ground, clapping, a song picked at random (the original has three), cymbal hits on top
            if (model != null)
            {
                g.transform.rotation = Quaternion.Euler(0f, g.transform.eulerAngles.y, 0f);
                anim?.Play("clap", 0, 0f);
            }
            string song = "zm_monkey_song_" + Random.Range(0, 3);
            AudioClip clip = ZombiesSounds.Clip(song);
            AudioSource music = null;
            if (clip != null)
            {
                music = g.gameObject.AddComponent<AudioSource>();
                music.clip = clip;
                music.spatialBlend = 1f;
                music.rolloffMode = AudioRolloffMode.Linear;
                music.minDistance = 3f;
                music.maxDistance = 45f;
                ZombiesSounds.Follow(music, ZombiesSounds.Group.Monkey);
                music.Play();
            }
            ZombiesSounds.At("zm_monkey_cymbal_start", g.transform.position, 0.8f, 30f);
            // Its voice: a squeak as it lands (the original zmb\monkey\land). Its scream (monkey_scream_vox) isn't played: under the
            // song it sounded like the monkey being hurt (the user, 2026-11-07)
            ZombiesSounds.On("zm_monkey_vox_land_#", g.transform, 0.9f, 30f);
            ZombiesPlugin.Log.LogInfo($"Zombies: Cymbal Monkey landed at {g.transform.position}, playing {song}: zombies drawn to it");
            // Cymbal hits now and then while it plays (the original layers them over the song); the last stretch the clapping
            // speeds up (a tell that it's about to go); the song's end, it goes off
            float playFor = clip != null ? clip.length : SongSeconds, ends = Time.time + playFor;
            float nextHit = Time.time + Random.Range(0.3f, 0.7f);
            bool explo = false;
            while (g != null && Time.time < ends)
            {
                float left = ends - Time.time;
                // Its last cry, timed to end as it goes off (the original zmb\monkey\explo_vox, 1.3-1.7 s); left at the spot so
                // the blast doesn't cut it
                if (!explo && left <= 1.5f)
                {
                    explo = true;
                    ZombiesSounds.At("zm_monkey_vox_explo_#", g.transform.position, 1f, 40f);
                }
                if (anim != null)
                    anim.speed = left < 2f ? Mathf.Lerp(2.2f, 1f, left / 2f) : 1f;
                if (Time.time >= nextHit)
                {
                    nextHit = Time.time + (left < 2f ? Random.Range(0.18f, 0.3f) : Random.Range(0.45f, 0.9f));
                    ZombiesSounds.At("zm_monkey_cymbal", g.transform.position, left < 2f ? 0.7f : 0.5f, 30f);
                }
                yield return null;
            }
            Monkeys.Remove(live);
            if (g != null && !Mine(g))
            {
                float wait = Time.time + 1.5f;
                while (g != null && Time.time < wait)
                    yield return null;
            }
            if (g != null)
            {
                ZombiesPlugin.Log.LogInfo($"Zombies: Cymbal Monkey's song over: boom ({(Mine(g) ? "ours" : "a teammate's")})");
                ZombiesGrenades.Detonate(g);
            }
        }
    }
}
