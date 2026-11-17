using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The zombie animation pack on our zombies (user, 2026-11-13). data\zombie_anims.json is the pack retargeted onto
    /// the game's body skeleton (UnityAssets ZombieAnimBake): per clip, the body bones' local rotations each frame and
    /// the pelvis's place. A <see cref="ZombieAnimPlayer"/> on every zombie (each game, for every zombie it sees) writes
    /// them over the game's own animation after it has run: idle / walk / run blended by how fast the zombie really
    /// moves (the walk cycle kept in step with the ground), and one-off clips on top (attacks, roar, get-up) faded in
    /// and out. Dead zombies are left to the game (the ragdoll).
    /// </summary>
    internal static class ZombiesAnim
    {
        internal sealed class Clip
        {
            public string Name;
            public float Length;
            public int Frames;
            public bool Loop;
            public float[][] Rot;   // [frame][bone * 4]
            public Vector3[] Hips;  // [frame]
            // Traversal clips only (data/zombie_traverse.json): where the body goes, from the first frame (x right,
            // y up, z forward, metres; w = turn in degrees)
            public Vector4[] Root;
            // How high its legs clear (metres, the sill the original was made for), whether it goes over and back down to
            // the same floor (a vault: its rise is in the root), and its rise per frame (hips + a vault's root) and peak
            public float Clear;
            public bool Vault;
            public float[] Lift;
            public float LiftMax;
        }

        public static string[] Bones;
        // Each bone's parent in the full skeleton (the bake's "parents"): the zombies' AI bodies use a simplified skeleton
        // without Spine1, the Ribcage, the Thigh2s and the Toes (user's log, 2026-11-13), so a bone whose parent is missing
        // gets that parent's rotation folded in
        public static string[] Parents;
        public static float Fps = 30f;
        private static readonly Dictionary<string, Clip> Clips = new Dictionary<string, Clip>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded, _tried;

        public static ConfigEntry<bool> On;
        public static ConfigEntry<bool> LegsFromPack;

        /// <summary>The pelvis and the leg bones (thighs, calves, feet, toes): left to the game's walk unless LegsFromPack.</summary>
        public static bool IsLowerBody(string bone) =>
            bone != null && (bone.EndsWith("Pelvis") || bone.Contains("Thigh") || bone.Contains("Calf") || bone.Contains("Foot") || bone.Contains("Toe"));
        public static ConfigEntry<float> WalkClipSpeed, RunClipSpeed, CrawlClipSpeed, FastCrawlClipSpeed;
        public static ConfigEntry<bool> Eyes;
        public static ConfigEntry<float> EyeUp, EyeForward, EyeApart, EyeSize;

        public static void Bind(ConfigFile config)
        {
            const string s = "21. Zombie animations (dev)";
            // User (2026-11-15, after seeing the pack in game): "not a fan, only the crawlers". Walking zombies keep the
            // game's own infected animations; the pack is for crawlers (legs blown off, later rounds). New key name so the
            // old "New zombie animations = true" in saved configs doesn't carry over
            On = config.Bind(s, "Pack animations on walking zombies", false,
                "The animation pack on walking zombies too (off: the game's own infected animations). Crawlers always use the pack.");
            // User's first look at the pack in game (2026-11-15): twisted feet and legs. The baked walk keeps both feet
            // 30-50 deg out from the hips' facing and the run's right foot ~125 deg off (scratch anim_fk.py), while idle
            // and the attacks are clean. Off: the game's own walk moves the hips and legs (and makes the steps), the pack
            // only the spine, arms and head
            LegsFromPack = config.Bind(s, "Legs from the animation pack", false,
                "On: the pack moves the hips and legs too (and the zombies make the pack's own footsteps). Off: the game's own zombie walk moves them, the pack only the upper body.");
            WalkClipSpeed = config.Bind(s, "Walk clip ground speed (m/s)", 1.3f, new ConfigDescription(
                "How fast the walk clip's feet travel: lower if the feet slide forward, higher if they slide back.", new AcceptableValueRange<float>(0.3f, 4f)));
            RunClipSpeed = config.Bind(s, "Run clip ground speed (m/s)", 2.6f, new ConfigDescription(
                "The same for the run clip.", new AcceptableValueRange<float>(0.5f, 8f)));
            CrawlClipSpeed = config.Bind(s, "Crawl clip ground speed (m/s)", 0.35f, new ConfigDescription(
                "The same for the slow crawl.", new AcceptableValueRange<float>(0.05f, 3f)));
            FastCrawlClipSpeed = config.Bind(s, "Fast crawl clip ground speed (m/s)", 1.2f, new ConfigDescription(
                "The same for the fast crawl.", new AcceptableValueRange<float>(0.1f, 4f)));
            Eyes = config.Bind(s, "Glowing eyes", true, "Glowing orange eyes on the zombies (grey once they die).");
            EyeUp = config.Bind(s, "Eyes: up (m)", 0.085f, new ConfigDescription("Above the head bone.", new AcceptableValueRange<float>(-0.1f, 0.25f)));
            EyeForward = config.Bind(s, "Eyes: forward (m)", 0.095f, new ConfigDescription("In front of the head bone.", new AcceptableValueRange<float>(-0.1f, 0.25f)));
            EyeApart = config.Bind(s, "Eyes: apart (m)", 0.032f, new ConfigDescription("Each eye from the middle.", new AcceptableValueRange<float>(0f, 0.1f)));
            EyeSize = config.Bind(s, "Eyes: glow size", 0.075f, new ConfigDescription("How big each glow is.", new AcceptableValueRange<float>(0.005f, 0.3f)));
        }

        public static Clip Get(string name)
        {
            Load();
            return name != null && Clips.TryGetValue(name, out Clip c) ? c : null;
        }

        public static bool Ready
        {
            get
            {
                Load();
                return _loaded;
            }
        }

        /// <summary>
        /// Reads the clips on a worker thread at start-up (reading them on the first zombie froze the game 151 ms,
        /// user's log 2026-11-13). Plain JSON and arrays only, no Unity calls, so it's safe off the main thread.
        /// </summary>
        public static void Preload() => System.Threading.Tasks.Task.Run(() =>
        {
            lock (LoadLock)
                Load();
        });

        private static readonly object LoadLock = new object();

        private static void Load()
        {
            if (_tried)
                return;
            lock (LoadLock)
            {
                if (_tried)
                    return;
                LoadNow();
                _tried = true;
            }
        }

        /// <summary>The original's window climbs, mantles and jump-downs are loaded (data/zombie_traverse.json).</summary>
        public static bool TraverseReady => Ready && _traversals > 0;
        private static int _traversals;

        /// <summary>
        /// The original zombie traversal clips (tools/retarget_traversal.py: window climb, mantles, jump-downs, run and
        /// crawl versions), on the same bones as the pack, each with its root motion.
        /// </summary>
        private static void LoadTraversals()
        {
            string path = Path.Combine(ZombiesPlugin.Dir, "zombie_traverse.json");
            try
            {
                if (!File.Exists(path))
                    return;
                JObject doc = JObject.Parse(File.ReadAllText(path));
                string[] bones = doc["bones"].ToObject<string[]>();
                if (Bones == null || bones.Length != Bones.Length || !System.Linq.Enumerable.SequenceEqual(bones, Bones))
                {
                    ZombiesPlugin.Log.LogWarning("Zombie traversals: their bones don't match the animations': not loaded");
                    return;
                }
                foreach (JObject c in doc["clips"])
                {
                    var rot = (JArray)c["rot"];
                    var hips = (JArray)c["hips"];
                    var root = (JArray)c["root"];
                    var clip = new Clip
                    {
                        Name = (string)c["name"], Length = (float)c["length"], Frames = (int)c["frames"], Loop = false,
                        Rot = new float[rot.Count][], Hips = new Vector3[hips.Count], Root = new Vector4[root.Count],
                    };
                    for (int f = 0; f < rot.Count; f++)
                        clip.Rot[f] = rot[f].ToObject<float[]>();
                    for (int f = 0; f < hips.Count; f++)
                    {
                        var h = (JArray)hips[f];
                        clip.Hips[f] = new Vector3((float)h[0], (float)h[1], (float)h[2]);
                    }
                    for (int f = 0; f < root.Count; f++)
                    {
                        var r = (JArray)root[f];
                        clip.Root[f] = new Vector4((float)r[0], (float)r[1], (float)r[2], (float)r[3]);
                    }
                    clip.Clear = (float?)c["clear"] ?? 0f;
                    int last = clip.Root.Length - 1;
                    clip.Vault = last > 0 && Mathf.Abs(clip.Root[last].y) < 0.1f;
                    clip.Lift = new float[clip.Hips.Length];
                    for (int f = 0; f < clip.Lift.Length; f++)
                    {
                        float rise = clip.Hips[f].y - clip.Hips[0].y + (clip.Vault && f <= last ? clip.Root[f].y : 0f);
                        clip.Lift[f] = rise;
                        clip.LiftMax = Mathf.Max(clip.LiftMax, rise);
                    }
                    Clips[clip.Name] = clip;
                    _traversals++;
                }
                ZombiesPlugin.Log.LogInfo($"Zombie traversals: {_traversals} clips loaded (window climb, mantles, jump-downs)");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombie traversals: couldn't read {path} ({e.Message})");
            }
        }

        /// <summary>A traversal clip's root at a time (x right, y up, z forward from where it started; w = turn).</summary>
        public static Vector4 RootAt(Clip c, float time)
        {
            if (c?.Root == null || c.Root.Length == 0)
                return Vector4.zero;
            float f = Mathf.Clamp(time * Fps, 0f, c.Root.Length - 1);
            int a = Mathf.FloorToInt(f);
            int b = Mathf.Min(a + 1, c.Root.Length - 1);
            return Vector4.Lerp(c.Root[a], c.Root[b], f - a);
        }

        /// <summary>
        /// Plays a traversal clip on this zombie (every game: the host moves the body along its root, ZombiesTraverse;
        /// the others get the move from the network): the whole body, blended in and out. The clip, or null.
        /// </summary>
        public static Clip PlayTraverse(Player zombie, string name, float sill = 0f, float speed = 1f)
        {
            Clip c = Get(name);
            if (zombie == null || c == null || c.Root == null)
                return null;
            var player = zombie.GetComponent<ZombieAnimPlayer>();
            if (player == null)
            {
                player = zombie.gameObject.AddComponent<ZombieAnimPlayer>();
                player.Init(zombie);
            }
            // Over a real sill (a window): its lift lowered (or raised) so the legs clear it by as much as the original's
            float dip = c.Vault && sill > 0.2f && c.Clear > 0.2f
                ? Mathf.Clamp(c.Clear - (sill + (ZombiesTraverse.Clearance?.Value ?? 0.12f)), -0.4f, 1f) : 0f;
            return player.Traverse(c, dip, speed) ? c : null;
        }

        private static void LoadNow()
        {
            string path = Path.Combine(ZombiesPlugin.Dir, "zombie_anims.json");
            try
            {
                if (!File.Exists(path))
                {
                    ZombiesPlugin.Log.LogWarning($"Zombie animations: {path} missing: the game's own animations stay");
                    return;
                }
                JObject doc = JObject.Parse(File.ReadAllText(path));
                Bones = doc["bones"].ToObject<string[]>();
                Parents = doc["parents"]?.ToObject<string[]>();
                Fps = (float)doc["fps"];
                foreach (JObject c in doc["clips"])
                {
                    var rot = (JArray)c["rot"];
                    var hips = (JArray)c["hips"];
                    var clip = new Clip
                    {
                        Name = (string)c["name"], Length = (float)c["length"], Frames = (int)c["frames"], Loop = (bool)c["loop"],
                        Rot = new float[rot.Count][], Hips = new Vector3[hips.Count],
                    };
                    for (int f = 0; f < rot.Count; f++)
                        clip.Rot[f] = rot[f].ToObject<float[]>();
                    for (int f = 0; f < hips.Count; f++)
                    {
                        var h = (JArray)hips[f];
                        clip.Hips[f] = new Vector3((float)h[0], (float)h[1], (float)h[2]);
                    }
                    Clips[clip.Name] = clip;
                }
                _loaded = Bones.Length > 0 && Clips.Count > 0;
                ZombiesPlugin.Log.LogInfo($"Zombie animations: {Clips.Count} clips on {Bones.Length} bones loaded");
                LoadTraversals();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombie animations: couldn't read {path} ({e.Message}): the game's own animations stay");
            }
        }

        private static float _nextAttach;

        /// <summary>Each frame (every game): every living zombie gets its player.</summary>
        public static void Tick()
        {
            if (!ZombiesMatch.Active || On == null || Time.time < _nextAttach)
                return;
            _nextAttach = Time.time + 0.25f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            bool anims = On.Value && Ready, eyes = Eyes != null && Eyes.Value;
            if (!anims && !eyes)
                return;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (p == null || !ZombiesTeam.IsZombie(p) || p.HealthController == null || !p.HealthController.IsAlive)
                    continue;
                if (anims && p.GetComponent<ZombieAnimPlayer>() == null)
                    p.gameObject.AddComponent<ZombieAnimPlayer>().Init(p);
                if (eyes && p.GetComponent<ZombieEyes>() == null)
                    p.gameObject.AddComponent<ZombieEyes>().Init(p);
            }
        }

        /// <summary>A one-off clip on this zombie (an attack, the roar, a get-up), if it has a player.</summary>
        public static bool Play(Player zombie, string clip, float speed = 1f)
        {
            var player = zombie != null ? zombie.GetComponent<ZombieAnimPlayer>() : null;
            return player != null && player.PlayOnce(clip, speed);
        }

        /// <summary>The zombie is crawling now (legs gone): its locomotion is the crawl set.</summary>
        public static void SetCrawling(Player zombie, bool crawling)
        {
            if (zombie == null)
                return;
            var player = zombie.GetComponent<ZombieAnimPlayer>();
            // Crawlers always get the pack, even with it off for walking zombies
            if (player == null && crawling && Ready)
            {
                player = zombie.gameObject.AddComponent<ZombieAnimPlayer>();
                player.Init(zombie);
            }
            if (player != null)
                player.Crawling = crawling;
        }
    }

    /// <summary>One zombie's animation: locomotion from its real speed, one-off clips on top.</summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class ZombieAnimPlayer : MonoBehaviour
    {
        private Player _p;
        private Transform[] _bones;
        // Per bone: the baked bones whose rotations make up its rotation on this body (itself last, missing parents first)
        private int[][] _chains;
        // Per bone: a hip or leg bone (ZombiesAnim.IsLowerBody)
        private bool[] _lower;
        private Transform _pelvis;
        private Vector3 _lastPos;
        private float _speed;
        private float _walkPhase, _runPhase, _idlePhase;
        private float _weight;  // fades in when it starts (no snap from the game's pose)
        public bool Crawling;

        private ZombiesAnim.Clip _once;
        private float _onceTime, _onceSpeed, _onceWeight;

        public void Init(Player p)
        {
            _p = p;
            Transform pelvis = p.PlayerBones?.Pelvis?.Original;
            Transform root = pelvis != null ? pelvis.parent : null;
            if (root == null)
            {
                enabled = false;
                return;
            }
            var byName = new Dictionary<string, Transform>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (!byName.ContainsKey(t.name))
                    byName[t.name] = t;
            _bones = new Transform[ZombiesAnim.Bones.Length];
            var missing = new List<string>();
            for (int i = 0; i < _bones.Length; i++)
            {
                if (byName.TryGetValue(ZombiesAnim.Bones[i], out Transform t))
                    _bones[i] = t;
                else
                    missing.Add(ZombiesAnim.Bones[i]);
            }
            _pelvis = pelvis;
            _lastPos = p.Transform.position;
            _idlePhase = UnityEngine.Random.value * 5f;
            _walkPhase = UnityEngine.Random.value;
            _runPhase = UnityEngine.Random.value;
            // A twist bone or two the body doesn't have is fine (user's test 2026-11-13: 23 of 29 found, every zombie was
            // left to the game's own animation); a main limb missing isn't
            _chains = new int[_bones.Length][];
            _lower = new bool[_bones.Length];
            for (int i = 0; i < _lower.Length; i++)
                _lower[i] = ZombiesAnim.IsLowerBody(ZombiesAnim.Bones[i]);
            for (int i = 0; i < _bones.Length; i++)
            {
                var chain = new List<int> { i };
                if (_bones[i] != null && ZombiesAnim.Parents != null && i < ZombiesAnim.Parents.Length)
                {
                    string actual = _bones[i].parent != null ? _bones[i].parent.name : "";
                    string parent = ZombiesAnim.Parents[i];
                    // Up the full skeleton while the parent is a baked bone this body hasn't got
                    while (!string.IsNullOrEmpty(parent) && parent != actual)
                    {
                        int pi = Array.IndexOf(ZombiesAnim.Bones, parent);
                        if (pi < 0 || _bones[pi] != null)
                            break;
                        chain.Insert(0, pi);
                        parent = ZombiesAnim.Parents[pi];
                    }
                }
                _chains[i] = chain.ToArray();
            }
            bool limbMissing = missing.Exists(n => Array.IndexOf(MainBones, n) >= 0);
            if (missing.Count > 0 && !_missingLogged)
            {
                _missingLogged = true;
                ZombiesPlugin.Log.LogInfo($"Zombie animations: bones the zombies' bodies don't have: {string.Join(", ", missing)}" +
                                          (limbMissing ? " (a main limb: the game's own animation stays)" : " (only twist/extra bones: fine)"));
            }
            if (limbMissing)
                enabled = false;
        }

        private static bool _missingLogged;

        private static readonly string[] MainBones =
        {
            "Base HumanPelvis", "Base HumanSpine2", "Base HumanHead",
            "Base HumanLUpperarm", "Base HumanLForearm1", "Base HumanRUpperarm", "Base HumanRForearm1",
            "Base HumanLThigh1", "Base HumanLCalf", "Base HumanRThigh1", "Base HumanRCalf",
        };

        // A traversal clip playing (window climb, mantle, jump-down): over everything, legs included
        private ZombiesAnim.Clip _trav;
        private float _travTime, _travDip, _travSpeed = 1f;
        // Blended in and out longer than a cut (user, 2026-11-15: "smoothed out even more")
        private const float TravIn = 0.25f, TravOut = 0.4f;

        public bool Traverse(ZombiesAnim.Clip c, float dip = 0f, float speed = 1f)
        {
            if (c == null || _bones == null)
                return false;
            _trav = c;
            _travTime = 0f;
            _travDip = dip;
            _travSpeed = Mathf.Clamp(speed, 0.3f, 2f);
            enabled = true;
            return true;
        }

        public bool Traversing => _trav != null;

        // One frame of the traversal: every bone and the hips, blended in at the start and out over the end (smooth, no
        // snap from or back to the game's own walk)
        private void TraverseFrame(float dt)
        {
            _travTime += dt * _travSpeed;
            float len = _trav.Length;
            if (_travTime >= len)
            {
                _trav = null;
                return;
            }
            float w = Mathf.Min(Mathf.Clamp01(_travTime / TravIn), Mathf.Clamp01((len - _travTime) / TravOut));
            w = Mathf.SmoothStep(0f, 1f, w);
            for (int b = 0; b < _bones.Length; b++)
            {
                Transform t = _bones[b];
                if (t == null)
                    continue;
                t.localRotation = Quaternion.Slerp(t.localRotation, SampleChain(_trav, _travTime, _chains[b]), w);
            }
            if (_pelvis != null)
            {
                Vector3 hips = SampleHips(_trav, _travTime);
                // A vault: the clip's own rise (in its root: the body itself only slides along the floor), less the dip
                // to the real sill, in step with how far up it is
                if (_trav.Vault)
                {
                    hips.y += ZombiesAnim.RootAt(_trav, _travTime).y;
                    if (_trav.LiftMax > 0.05f && _travDip != 0f)
                        hips.y -= _travDip * Mathf.Clamp01(SampleLift(_trav, _travTime) / _trav.LiftMax);
                }
                _pelvis.localPosition = Vector3.Lerp(_pelvis.localPosition, hips, w);
            }
        }

        private static float SampleLift(ZombiesAnim.Clip c, float time)
        {
            if (c.Lift == null || c.Lift.Length == 0)
                return 0f;
            float f = Mathf.Clamp(time * ZombiesAnim.Fps, 0f, c.Lift.Length - 1);
            int a = Mathf.FloorToInt(f);
            int b = Mathf.Min(a + 1, c.Lift.Length - 1);
            return Mathf.Lerp(c.Lift[a], c.Lift[b], f - a);
        }

        public bool PlayOnce(string name, float speed)
        {
            ZombiesAnim.Clip c = ZombiesAnim.Get(name);
            if (c == null || !enabled)
                return false;
            _once = c;
            _onceTime = 0f;
            _onceSpeed = Mathf.Max(0.1f, speed);
            return true;
        }

        private void LateUpdate()
        {
            if (_p == null || _bones == null || !ZombiesMatch.Active || ZombiesAnim.On == null
                || _p.HealthController == null || !_p.HealthController.IsAlive)
            {
                // Dead (the ragdoll), off, or the match is over: the game's own animation from here
                if (_p == null || _p.HealthController == null || !_p.HealthController.IsAlive)
                    Destroy(this);
                return;
            }
            float dt = Time.deltaTime;
            // A window climb / jump-down playing: it alone, legs and all
            if (_trav != null)
            {
                if (dt > 0f)
                    TraverseFrame(dt);
                return;
            }
            // A walking zombie with the pack off for walkers: back to the game's own animation (a crawler keeps the pack)
            if (!Crawling && !ZombiesAnim.On.Value)
            {
                Destroy(this);
                return;
            }
            if (dt <= 0f)
                return;
            // Its real ground speed (smoothed), whatever moves it (the bot's mover, our scripted walks, the network)
            Vector3 pos = _p.Transform.position;
            Vector3 moved = pos - _lastPos;
            _lastPos = pos;
            moved.y = 0f;
            float now = moved.magnitude / dt;
            if (now > 15f)
                now = _speed; // a teleport (rise, vault): not a speed
            _speed = Mathf.Lerp(_speed, now, 1f - Mathf.Exp(-dt / 0.15f));
            // The game's own vault plays on its own (its animation, not ours painted over it): faded out for it, back after
            bool vaulting = false;
            try
            {
                vaulting = _p.VaultingComponent is EFT.Vaulting.VaultingComponent vc && vc.IsVaulting();
            }
            catch
            {
            }
            _weight = Mathf.MoveTowards(_weight, vaulting ? 0f : 1f, dt / (vaulting ? 0.12f : 0.3f));
            if (_weight <= 0f)
                return;

            // Locomotion: idle / walk / run (or the crawl set), their cycles in step with the ground
            string idleName = Crawling ? "Crawl_Idle" : "BL_Idle";
            string walkName = Crawling ? "Crawl_Slow" : "BL_Walk";
            string runName = Crawling ? "FastCrawl" : "BL_Run";
            float walkSpeed = Crawling ? ZombiesAnim.CrawlClipSpeed.Value : ZombiesAnim.WalkClipSpeed.Value;
            float runSpeed = Crawling ? ZombiesAnim.FastCrawlClipSpeed.Value : ZombiesAnim.RunClipSpeed.Value;
            ZombiesAnim.Clip idle = ZombiesAnim.Get(idleName), walk = ZombiesAnim.Get(walkName), run = ZombiesAnim.Get(runName);
            if (idle == null || walk == null || run == null)
                return;
            float moving = Mathf.InverseLerp(0.12f, 0.45f, _speed);
            float running = Mathf.InverseLerp(walkSpeed * 1.1f, Mathf.Max(walkSpeed * 1.2f, runSpeed * 0.8f), _speed);
            _idlePhase = Mathf.Repeat(_idlePhase + dt / idle.Length, 1f);
            // A crawler's cycle may slow right down with it: the 0.6 floor had its arms going faster than it moved
            // (user, 2026-11-16: crawl animation too fast for the movement)
            float walkRate = Mathf.Clamp(_speed / Mathf.Max(0.05f, walkSpeed), Crawling ? 0.15f : 0.6f, 2.2f);
            float runRate = Mathf.Clamp(_speed / Mathf.Max(0.05f, runSpeed), 0.6f, 2.2f);
            float walkBefore = _walkPhase, runBefore = _runPhase;
            _walkPhase = Mathf.Repeat(_walkPhase + dt * walkRate / walk.Length, 1f);
            _runPhase = Mathf.Repeat(_runPhase + dt * runRate / run.Length, 1f);
            bool legs = Legs;
            if (legs)
                Steps(moving, running, walkBefore, runBefore);

            // One-off on top: in fast, out over its last fifth
            float onceW = 0f;
            if (_once != null)
            {
                _onceTime += dt * _onceSpeed;
                if (_onceTime >= _once.Length)
                    _once = null;
                else
                {
                    float fadeOut = Mathf.Min(0.35f, _once.Length * 0.2f);
                    onceW = Mathf.Min(Mathf.Clamp01(_onceTime / 0.15f), Mathf.Clamp01((_once.Length - _onceTime) / fadeOut));
                }
            }

            int n = _bones.Length;
            Vector3 hips = Vector3.zero;
            for (int b = 0; b < n; b++)
            {
                Transform t = _bones[b];
                if (t == null || (!legs && _lower[b]))
                    continue;
                int[] chain = _chains[b];
                Quaternion q = SampleChain(idle, _idlePhase * idle.Length, chain);
                if (moving > 0f)
                {
                    Quaternion w = SampleChain(walk, _walkPhase * walk.Length, chain);
                    if (running > 0f)
                        w = Quaternion.Slerp(w, SampleChain(run, _runPhase * run.Length, chain), running);
                    q = Quaternion.Slerp(q, w, moving);
                }
                if (onceW > 0f)
                    q = Quaternion.Slerp(q, SampleChain(_once, _onceTime, chain), onceW);
                t.localRotation = _weight >= 1f ? q : Quaternion.Slerp(t.localRotation, q, _weight);
            }
            // The pelvis's height and sway with them
            Vector3 hp = SampleHips(idle, _idlePhase * idle.Length);
            if (moving > 0f)
            {
                Vector3 wh = SampleHips(walk, _walkPhase * walk.Length);
                if (running > 0f)
                    wh = Vector3.Lerp(wh, SampleHips(run, _runPhase * run.Length), running);
                hp = Vector3.Lerp(hp, wh, moving);
            }
            if (onceW > 0f)
                hp = Vector3.Lerp(hp, SampleHips(_once, _onceTime), onceW);
            if (_pelvis != null && legs)
                _pelvis.localPosition = _weight >= 1f ? hp : Vector3.Lerp(_pelvis.localPosition, hp, _weight);
        }

        /// <summary>The pack moves this zombie's hips and legs (F12, or it crawls: the crawl set is the pack's own).</summary>
        private bool Legs => Crawling || (ZombiesAnim.LegsFromPack?.Value ?? false);

        // ---------- Footsteps (user, 2026-11-13: the original's zombie steps, on the new animation's own foot plants) ----------
        // Where each foot (or hand, crawling) lands in the cycle, read off the baked clips (scratch anim_speed.py)
        private static readonly float[] WalkPlants = { 0.39f, 0.93f }, RunPlants = { 0f, 0.5f };
        private static readonly float[] CrawlPlants = { 0.28f, 0.81f }, FastCrawlPlants = { 0.57f, 0.71f };
        private const float StepHear = 25f;

        /// <summary>This zombie is making its own steps (the game's are shut for it, ZombiesZombieVox).</summary>
        public bool OwnSteps => enabled && _weight > 0.5f && Legs;

        private void Steps(float moving, float running, float walkBefore, float runBefore)
        {
            if (moving < 0.5f)
                return;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me == null || (me.Transform.position - _p.Transform.position).sqrMagnitude > StepHear * StepHear)
                return;
            bool fast = running >= 0.5f;
            float[] plants = Crawling ? (fast ? FastCrawlPlants : CrawlPlants) : fast ? RunPlants : WalkPlants;
            float before = fast ? runBefore : walkBefore, now = fast ? _runPhase : _walkPhase;
            foreach (float at in plants)
                if (Crossed(before, now, at))
                {
                    string sound = Crawling ? "zm_zstep_crawl_#" : fast ? "zm_zstep_run_#" : "zm_zstep_walk_#";
                    ZombiesSounds.At(sound, _p.Transform.position + Vector3.up * 0.1f, fast ? 0.8f : 0.6f, StepHear);
                }
        }

        // The phase went past "at" this frame (wrapping round 1 too)
        private static bool Crossed(float before, float now, float at)
        {
            if (now >= before)
                return at > before && at <= now;
            return at > before || at <= now;
        }

        // A bone's rotation on this body: its own, with any missing parents' folded in before it
        private static Quaternion SampleChain(ZombiesAnim.Clip c, float time, int[] chain)
        {
            Quaternion q = Sample(c, time, chain[0]);
            for (int k = 1; k < chain.Length; k++)
                q *= Sample(c, time, chain[k]);
            return q;
        }

        private static Quaternion Sample(ZombiesAnim.Clip c, float time, int bone)
        {
            float f = Mathf.Clamp(time * ZombiesAnim.Fps, 0f, c.Frames - 1);
            int a = Mathf.FloorToInt(f);
            int b = Mathf.Min(a + 1, c.Frames - 1);
            float k = f - a;
            float[] ra = c.Rot[a], rb = c.Rot[b];
            int i = bone * 4;
            var qa = new Quaternion(ra[i], ra[i + 1], ra[i + 2], ra[i + 3]);
            if (k <= 0.001f)
                return qa;
            var qb = new Quaternion(rb[i], rb[i + 1], rb[i + 2], rb[i + 3]);
            return Quaternion.Slerp(qa, qb, k);
        }

        private static Vector3 SampleHips(ZombiesAnim.Clip c, float time)
        {
            float f = Mathf.Clamp(time * ZombiesAnim.Fps, 0f, c.Frames - 1);
            int a = Mathf.FloorToInt(f);
            int b = Mathf.Min(a + 1, c.Frames - 1);
            return Vector3.Lerp(c.Hips[a], c.Hips[b], f - a);
        }
    }

    /// <summary>
    /// Glowing orange eyes (user, 2026-11-13: purple first, then "go from purple to orange"): two soft glows on the
    /// zombie's head bone, turned to the camera, so they follow the head and its nods. The offsets are in the body's terms
    /// (up, forward, apart) and turned into the head bone's own axes (forward = its +Y, up = its -X, right = its -Z, from
    /// the game's skeleton). When it dies the lights go out: a short dying flicker, then dull grey eyes on the body (no
    /// glow) until the body is cleared. Every game, every zombie.
    /// </summary>
    [DefaultExecutionOrder(32001)]
    internal sealed class ZombieEyes : MonoBehaviour
    {
        private static readonly Color Glow = new Color(1f, 0.42f, 0.05f, 1f);
        private static readonly Color DeadGrey = new Color(0.32f, 0.32f, 0.3f, 0.9f);
        private const float GoingOut = 0.7f, DeadSize = 0.4f; // seconds of flicker; dead eye size (of the glow's)
        private static Material _glow, _dead;
        private static Mesh _quad;
        private Player _p;
        private Transform _head;
        private Transform[] _eyes;
        private MeshRenderer[] _renderers;
        private float _diedAt = -1f;
        private bool _deadLook;

        public void Init(Player p)
        {
            _p = p;
            _head = p.PlayerBones?.Head?.Original;
            if (_head == null || !MakeShared())
            {
                enabled = false;
                return;
            }
            _eyes = new Transform[2];
            _renderers = new MeshRenderer[2];
            for (int i = 0; i < 2; i++)
            {
                var go = new GameObject("Zombies_Eye");
                go.transform.SetParent(_head, false);
                go.AddComponent<MeshFilter>().sharedMesh = _quad;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = _glow;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _eyes[i] = go.transform;
                _renderers[i] = r;
            }
        }

        private static bool MakeShared()
        {
            if (_glow != null && _dead != null && _quad != null)
                return true;
            Shader additive = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive");
            Shader plain = Shader.Find("Sprites/Default");
            if (additive == null && plain == null)
                return false;
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "KennerZombies eye glow", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[n * n];
            float c = (n - 1) / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x - c, y - c).magnitude / c;
                    // A bright core and a soft halo
                    float a = Mathf.Clamp01(Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f) + Mathf.Clamp01(1f - d * 4f));
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.Apply(true, false);
            _glow = new Material(additive ?? plain) { name = "KennerZombies eyes", mainTexture = tex, hideFlags = HideFlags.HideAndDontSave };
            if (_glow.HasProperty("_TintColor"))
                _glow.SetColor("_TintColor", Glow);
            _glow.color = Glow;
            // Dead: a dull grey eye, no light (drawn plainly, not added on top like the glow)
            var dot = new Texture2D(n, n, TextureFormat.RGBA32, true) { name = "KennerZombies dead eye", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x - c, y - c).magnitude / c;
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01((0.75f - d) * 6f));
                }
            dot.SetPixels(px);
            dot.Apply(true, false);
            _dead = new Material(plain ?? additive) { name = "KennerZombies dead eyes", mainTexture = dot, hideFlags = HideFlags.HideAndDontSave };
            _dead.color = DeadGrey;
            _quad = new Mesh { name = "KennerZombies eye quad", hideFlags = HideFlags.HideAndDontSave };
            _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            _quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            // The particle shaders multiply by the vertex colour: none on the mesh drew the glow black (invisible, user's
            // test 2026-11-13)
            _quad.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            _quad.RecalculateBounds();
            ZombiesPlugin.Log.LogInfo($"Zombie eyes: glow shader {(additive ?? plain).name}, dead eyes {(plain ?? additive).name}");
            return true;
        }

        private void LateUpdate()
        {
            if (_eyes == null || _head == null || !ZombiesMatch.Active)
            {
                if (_eyes != null)
                    foreach (Transform e in _eyes)
                        if (e != null)
                            Destroy(e.gameObject);
                Destroy(this);
                return;
            }
            bool alive = _p != null && _p.HealthController != null && _p.HealthController.IsAlive;
            if (!alive && _diedAt < 0f)
                _diedAt = Time.time;
            bool on = ZombiesAnim.Eyes != null && ZombiesAnim.Eyes.Value;
            Camera cam = ZombiesInteraction.ActiveCamera != null ? ZombiesInteraction.ActiveCamera : Camera.main;
            float up = ZombiesAnim.EyeUp.Value, fwd = ZombiesAnim.EyeForward.Value, apart = ZombiesAnim.EyeApart.Value, size = ZombiesAnim.EyeSize.Value;
            // Dying: the lights flicker out over GoingOut seconds (sputtering, shrinking), then grey eyes
            bool lit = true;
            float scale = 1f;
            if (_diedAt >= 0f)
            {
                float t = (Time.time - _diedAt) / GoingOut;
                if (t >= 1f)
                {
                    if (!_deadLook)
                    {
                        _deadLook = true;
                        foreach (MeshRenderer r in _renderers)
                            if (r != null)
                                r.sharedMaterial = _dead;
                    }
                    scale = DeadSize;
                }
                else
                {
                    // On less and less often, and dimmer (smaller), as it goes
                    lit = Mathf.Repeat(Time.time * 23f, 1f) > t * 0.85f;
                    scale = Mathf.Lerp(1f, 0.5f, t);
                }
            }
            for (int i = 0; i < _eyes.Length; i++)
            {
                Transform e = _eyes[i];
                if (e == null)
                    continue;
                bool show = on && lit;
                if (e.gameObject.activeSelf != show)
                    e.gameObject.SetActive(show);
                if (!show)
                    continue;
                float side = i == 0 ? -apart : apart;
                // forward = +Y, up = -X, right = -Z in the head bone's own axes
                e.localPosition = new Vector3(-up, fwd, -side);
                e.localScale = Vector3.one * size * scale;
                if (cam != null)
                    e.rotation = Quaternion.LookRotation(e.position - cam.transform.position, cam.transform.up);
            }
        }
    }
}
