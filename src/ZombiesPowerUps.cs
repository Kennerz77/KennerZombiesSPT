using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's power-ups in a zombies match. A zombie you kill now and then drops one (F12: the chance, the most per round
    /// (the original: 4), and how often each kind comes, its rarity): the original's own model glowing green, spinning and bobbing with
    /// its hum, gone after 26 s (blinking for the last 6). Walk into it: the grab sound, the announcer saying what it is
    /// and its name big on screen. What each does:
    /// - Max Ammo: every gun's reserve full, grenades to 4.
    /// - Insta-Kill (30 s): any hit kills a zombie.
    /// - Double Points (30 s): every point earned is doubled.
    /// - Nuke: every zombie alive goes up in its own little blast, one after another, players untouched (no real
    ///   explosion, only its look and sound); 400 points, no kill points for the zombies.
    /// - Carpenter: every barricade rebuilt at once; 200 points.
    /// - Fire Sale (30 s): the box costs 10.
    /// - Widow's Wine kiss: only from zombies killed in a Widow's Wine web; one grenade back.
    /// Timed ones show at the bottom of the screen with the seconds left.
    /// </summary>
    internal static class ZombiesPowerUps
    {
        public enum Kind { MaxAmmo, InstaKill, DoublePoints, Nuke, Carpenter, FireSale, WidowsKiss }

        private class Def
        {
            public Kind Kind;
            public string Model, Vox, Sound, Text;
            public bool Timed;
            public int Rarity;
            public ConfigEntry<int> Weight;
            public ConfigEntry<float> Scale;
            public float Size = 1f; // default size against the others (the user's tuned look, 2026-11-09)
        }

        private static readonly Def[] Defs =
        {
            new Def { Kind = Kind.MaxAmmo, Model = "zm_powerup_max_ammo", Vox = "zm_pu_vox_maxammo", Sound = "zm_pu_max_ammo", Text = "MAX AMMO!", Rarity = 10 },
            new Def { Kind = Kind.InstaKill, Size = 0.9f, Model = "zm_powerup_insta_kill", Vox = "zm_pu_vox_instakill", Sound = "zm_pu_instakill", Text = "INSTA-KILL!", Timed = true, Rarity = 10 },
            new Def { Kind = Kind.DoublePoints, Model = "zm_powerup_double_points", Vox = "zm_pu_vox_doublepoints", Text = "DOUBLE POINTS!", Timed = true, Rarity = 10 },
            new Def { Kind = Kind.Nuke, Size = 1.4f, Model = "zm_powerup_nuke", Vox = "zm_pu_vox_nuke", Text = "KA-BOOM!", Rarity = 8 },
            new Def { Kind = Kind.Carpenter, Size = 0.8f, Model = "zm_powerup_carpenter", Vox = "zm_pu_vox_carpenter", Sound = "zm_pu_carpenter_end", Text = "CARPENTER!", Rarity = 6 },
            new Def { Kind = Kind.FireSale, Size = 2.5f, Model = "zm_powerup_fire_sale", Vox = "zm_pu_vox_firesale", Text = "FIRE SALE!", Timed = true, Rarity = 4 },
            new Def { Kind = Kind.WidowsKiss, Size = 0.5f, Model = "zm_powerup_widows_wine", Sound = "zm_ww_web_#", Text = "WIDOW'S KISS", Rarity = 0 },
        };

        private const int NukePoints = 400, CarpenterPoints = 200, FireSalePrice = 10;
        // Walk into it to take it (the user, 2026-11-07: bigger power-ups, a small pickup)
        private const float PickUpReach = 0.8f, BlinkFor = 6f, Float = 1.0f;

        private static ConfigEntry<bool> _on;
        private static ConfigEntry<float> _chance, _size, _glow, _glowRange, _lasts, _timed, _kissChance, _goldGlow, _haloSize, _iconSize, _iconUp;
        private static ConfigEntry<bool> _showBanner;
        private static ConfigEntry<int> _perRound;
        private static readonly Dictionary<Kind, float> Until = new Dictionary<Kind, float>();
        private static readonly List<PowerUpDrop> Drops = new List<PowerUpDrop>();
        private static int _thisRound;
        private static bool _hooked;
        private static string _banner;
        private static float _bannerAt = -100f, _flashAt = -100f;

        // The original's HUD icons for the running timed power-ups (hud\zm_pu_icon_*.png, 99_Tools\powerup_icons.py)
        private static readonly Dictionary<Kind, Texture2D> Icons = new Dictionary<Kind, Texture2D>();

        private static Texture2D Icon(Kind k)
        {
            if (Icons.TryGetValue(k, out Texture2D t))
                return t;
            string name = k == Kind.DoublePoints ? "double" : k == Kind.InstaKill ? "insta" : k == Kind.FireSale ? "firesale" : null;
            t = null;
            try
            {
                string file = name == null ? null : System.IO.Path.Combine(ZombiesPlugin.Dir, "hud", $"zm_pu_icon_{name}.png");
                if (file != null && System.IO.File.Exists(file))
                {
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                    if (!ImageConversion.LoadImage(t, System.IO.File.ReadAllBytes(file)))
                        t = null;
                }
            }
            catch
            {
                t = null;
            }
            Icons[k] = t;
            return t;
        }
        private static Material _glowMaterial;

        public static bool InstaKill => Active(Kind.InstaKill);
        public static bool DoublePoints => Active(Kind.DoublePoints);
        public static int BoxPrice => Active(Kind.FireSale) ? FireSalePrice : BoxPay.Price;
        private static bool Active(Kind k) => ZombiesMatch.Active && Until.TryGetValue(k, out float t) && Time.time < t;

        // ---------- F12 ----------

        public static void Bind(ConfigFile config, Action<string, string, Action, int> button)
        {
            const string s = "17. Power-ups";
            _on = config.Bind(s, "Power-ups drop", true, Order("Zombies you kill now and then drop a power-up.", 99));
            _chance = config.Bind(s, "Drop chance (%)", 4f, new ConfigDescription("The chance a zombie you kill drops one.",
                new AcceptableValueRange<float>(0f, 100f), Attr(98)));
            _perRound = config.Bind(s, "Most per round", 4, new ConfigDescription("", new AcceptableValueRange<int>(0, 30), Attr(97)));
            _size = config.Bind(s, "Size (m)", 0.83f, new ConfigDescription("How big a power-up is (its longest side).", new AcceptableValueRange<float>(0.1f, 2f), Attr(96)));
            _glow = config.Bind(s, "Glow brightness", 0.8f, new ConfigDescription("Its green light.", new AcceptableValueRange<float>(0f, 10f), Attr(95)));
            _showBanner = config.Bind(s, "Name banner on pickup", false, Order("The power-up's name big on screen when picked up (off: only the green burst and the announcer).", 93));
            _iconSize = config.Bind(s, "HUD icon size", 1f, new ConfigDescription("The running power-ups' icons, bottom middle.", new AcceptableValueRange<float>(0.4f, 3f), Attr(93)));
            _iconUp = config.Bind(s, "HUD icons height", 60f, new ConfigDescription("How far above the bottom of the screen the icons sit.", new AcceptableValueRange<float>(0f, 600f), Attr(93)));
            _goldGlow = config.Bind(s, "Gold glow", 0.6f, new ConfigDescription(
                "How much the gold lights itself (so it shows in the dark).", new AcceptableValueRange<float>(0f, 2f), Attr(94)));
            _goldGlow.SettingChanged += (_, __) => GoldOf.Clear();
            _haloSize = config.Bind(s, "Green halo size", 1.8f, new ConfigDescription(
                "The green glow round it, against its size (0: none).", new AcceptableValueRange<float>(0f, 4f), Attr(94)));
            _glowRange = config.Bind(s, "Glow reach (m)", 3.5f, new ConfigDescription("How far its light reaches.", new AcceptableValueRange<float>(0.5f, 15f), Attr(94)));
            _lasts = config.Bind(s, "Stays for (s)", 26f, new ConfigDescription("Blinking at the end.", new AcceptableValueRange<float>(5f, 120f), Attr(93)));
            _timed = config.Bind(s, "Timed ones last (s)", 30f, new ConfigDescription("Insta-Kill, Double Points, Fire Sale.",
                new AcceptableValueRange<float>(5f, 120f), Attr(92)));
            _kissChance = config.Bind(s, "Widows kiss chance (%)", 20f, new ConfigDescription(
                "The chance a zombie killed in a Widow's Wine web drops a kiss (a grenade back).", new AcceptableValueRange<float>(0f, 100f), Attr(91)));
            int order = 90;
            foreach (Def d in Defs.Where(d => d.Rarity > 0))
                d.Weight = config.Bind(s, $"Rarity: {Name(d)}", d.Rarity, new ConfigDescription(
                    "How often this one comes against the others (0: never).", new AcceptableValueRange<int>(0, 20), Attr(order--)));
            // Each one's own size on top of "Size (m)" (F12 "17b. Power-up sizes"), changed live on the ones out
            int so = 50;
            foreach (Def d in Defs)
            {
                Def def = d;
                d.Scale = config.Bind("17b. Power-up sizes", $"Size: {Name(d).Replace("'", "")}", d.Size, new ConfigDescription(
                    "This power-up's size against the others (1: as \"Size (m)\").", new AcceptableValueRange<float>(0.2f, 3f), Attr(so--)));
                d.Scale.SettingChanged += (_, __) =>
                {
                    foreach (PowerUpDrop drop in Drops.Where(x => x != null && x.Kind == def.Kind))
                        drop.transform.localScale = Vector3.one * def.Scale.Value;
                };
            }
            int b = 1;
            foreach (Def d in Defs)
            {
                Kind k = d.Kind;
                button(s, $"Drop {Name(d)}", () => DropInFront(k), 20 + b++);
            }
            button(s, "Drop a random one", () => DropInFront(null), 20 + b++);
            button(s, "Clear power-ups", ClearDrops, 20 + b);
        }

        private static ConfigDescription Order(string text, int order) => new ConfigDescription(text, null, Attr(order));
        private static ZombiesPlugin.ConfigurationManagerAttributes Attr(int order) => new ZombiesPlugin.ConfigurationManagerAttributes { Order = order };
        private static string Name(Def d) => d.Text.TrimEnd('!');

        // ---------- The match ----------

        public static void Start()
        {
            Clear();
            if (_hooked)
                return;
            _hooked = true;
            Player.OnPlayerDeadStatic += OnDead;
        }

        public static void Clear()
        {
            ClearDrops();
            Until.Clear();
            _thisRound = 0;
            _banner = null;
        }

        public static void NewRound()
        {
            // Logger (user, 2026-11-10: drops felt very common in player-hosted Fika): what the round just over gave
            if (_killsThisRound > 0 || _thisRound > 0 || _kissesThisRound > 0)
                ZombiesPlugin.Log.LogInfo($"Zombies power-ups: round over: {_thisRound} drop(s) + {_kissesThisRound} Widows kiss from {_killsThisRound} kills inside (cap {_perRound?.Value}, host: {ZombiesNet.IsHost})");
            _thisRound = 0;
            _killsThisRound = 0;
            _kissesThisRound = 0;
        }

        private static int _killsThisRound, _kissesThisRound;

        // Our own dice for the drop rolls (user, 2026-11-10: in player-hosted Fika drops came about every other kill).
        // EFT reseeds Unity's shared Random on shots (MalfunctionRandom: Random.InitState(seed + n), kept the same on
        // every game for Fika), so a roll made in a kill right after a shot got a fixed, repeating value: kills in the
        // same frame even dropped two at once. System.Random is never touched by EFT
        private static readonly System.Random Dice = new System.Random();
        private static float Roll100() => (float)(Dice.NextDouble() * 100.0);

        private static void ClearDrops()
        {
            foreach (PowerUpDrop d in Drops.ToList())
                if (d != null)
                    UnityEngine.Object.Destroy(d.gameObject);
            Drops.Clear();
        }

        // A zombie you killed: maybe a power-up where it fell (only the host drops them; solo you're the host)
        private static void OnDead(Player victim, IPlayer aggressor, DamageInfo damage, EBodyPart part)
        {
            try
            {
                // The host drops them for everyone (ZombiesNet)
                if (!ZombiesMatch.Active || !ZombiesNet.IsHost || victim == null || !victim.IsAI || aggressor == null || aggressor.IsAI || _on?.Value != true)
                    return;
                Vector3 at = victim.Transform.position;
                // The original: a zombie killed before it got in (still outside its barricade) never drops anything
                if (!ZombiesRounds.Inside(victim.AIData?.BotOwner))
                    return;
                _killsThisRound++;
                if (ZombiesWidow.Webbed(victim.AIData?.BotOwner) && Roll100() < _kissChance.Value)
                {
                    _kissesThisRound++;
                    ZombiesPlugin.Log.LogInfo($"Zombies power-ups: Widows kiss rolled (webbed kill {_killsThisRound} of round {ZombiesRounds.Round}, {_kissChance.Value}%, not capped)");
                    Drop(Kind.WidowsKiss, at);
                    return;
                }
                // The original's feel: rare early, more as the rounds go (round 1 at a third of the F12 chance, the full chance
                // from round 10), at most the F12 count per round
                int round = Mathf.Max(1, ZombiesRounds.Round);
                float chance = _chance.Value * Mathf.Lerp(0.4f, 1f, Mathf.Clamp01((round - 1) / 9f));
                if (_thisRound >= _perRound.Value || Roll100() >= chance)
                    return;
                Def pick = Pick();
                if (pick != null)
                {
                    _thisRound++;
                    ZombiesPlugin.Log.LogInfo($"Zombies power-ups: drop {_thisRound}/{_perRound.Value} of round {round} on kill {_killsThisRound} (chance {chance:0.##}%, killer {aggressor.Profile?.Nickname ?? "?"}, host: {ZombiesNet.IsHost}, online: {ZombiesNet.Online})");
                    Drop(pick.Kind, at);
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies power-ups: drop failed ({e.Message})");
            }
        }

        // One kind by rarity (Fire Sale never while a fire sale is on)
        private static Def Pick()
        {
            int round = Mathf.Max(1, ZombiesRounds.Round);
            var pool = Defs.Where(d => d.Weight != null && d.Weight.Value > 0 && !(d.Kind == Kind.FireSale && Active(Kind.FireSale))
                                       && round >= FirstRound(d.Kind)).ToList();
            int total = pool.Sum(d => d.Weight.Value);
            if (total <= 0)
                return null;
            int roll = Dice.Next(0, total);
            foreach (Def d in pool)
            {
                if (roll < d.Weight.Value)
                    return d;
                roll -= d.Weight.Value;
            }
            return pool.Last();
        }

        /// <summary>
        /// The first round each can drop (the original's way: no Fire Sale early, no Carpenter before there are boards to fix): Max
        /// Ammo and Double Points from round 1, Insta-Kill and the Nuke from 2, Carpenter from 3, Fire Sale from 5.
        /// </summary>
        private static int FirstRound(Kind k)
        {
            switch (k)
            {
                case Kind.InstaKill:
                case Kind.Nuke:
                    return 2;
                case Kind.Carpenter:
                    return 3;
                case Kind.FireSale:
                    return 5;
                default:
                    return 1;
            }
        }

        private static void DropInFront(Kind? kind)
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (!ZombiesMatch.Active || me == null)
            {
                ZombiesPlugin.Notify("Only in a zombies match.", true);
                return;
            }
            Vector3 flat = Vector3.ProjectOnPlane(me.Transform.forward, Vector3.up).normalized;
            Kind k = kind ?? Pick()?.Kind ?? Kind.MaxAmmo;
            Drop(k, me.Transform.position + flat * 3f);
        }

        private static int _nextId = 1;

        public static void Drop(Kind kind, Vector3 at)
        {
            int id = _nextId++;
            Make(id, kind, at);
            ZombiesNet.PowerUpDropped(id, kind, at);
        }

        /// <summary>The host dropped one: it shows here too.</summary>
        public static void DropRemote(int id, Kind kind, Vector3 at)
        {
            // Logger: a client shows the host's drops (it never rolls its own: OnDead returns when not the host)
            ZombiesPlugin.Log.LogInfo($"Zombies power-ups: the host dropped {kind} (id {id}), shown here (host: {ZombiesNet.IsHost})");
            Make(id, kind, at);
        }

        /// <summary>The host: a client touched one. First come: it's theirs, and everyone gets it (the original's power-ups are the team's).</summary>
        public static void GrabRequest(int id, string by)
        {
            PowerUpDrop d = Drops.FirstOrDefault(x => x != null && x.Id == id);
            if (d == null)
                return;
            Kind kind = d.Kind;
            Drops.Remove(d);
            UnityEngine.Object.Destroy(d.gameObject);
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me != null)
                Grab(kind, me, by);
            else
                HostOnly(kind);
            ZombiesNet.PowerUpTaken(id, kind, by);
        }

        /// <summary>
        /// A host with no player of its own (a Fika headless): the team's half of a power-up only, the part that lives
        /// on the host (timers for Insta-Kill and the rest, the nuke's kills, the carpenter's boards). Without it a
        /// carpenter did nothing and a nuke killed nothing (user, 2026-11-09).
        /// </summary>
        private static void HostOnly(Kind kind)
        {
            Def def = Defs.First(d => d.Kind == kind);
            if (def.Timed)
                Until[kind] = Time.time + (_timed?.Value ?? 30f);
            ZombiesPlugin.Log.LogInfo($"Zombies power-ups (host, no player): {Name(def)} taken");
            if (kind == Kind.Nuke)
                ZombiesPlugin.Instance.StartCoroutine(Nuke());
            else if (kind == Kind.Carpenter)
                ZombiesBarricade.RepairAll();
        }

        /// <summary>The host says who took one: gone, and its effect here.</summary>
        public static void TakeRemote(int id, Kind kind, string by)
        {
            PowerUpDrop d = Drops.FirstOrDefault(x => x != null && x.Id == id);
            if (d != null)
            {
                Drops.Remove(d);
                UnityEngine.Object.Destroy(d.gameObject);
            }
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            if (me != null)
                Grab(kind, me, by);
            else if (ZombiesNet.IsHost)
                HostOnly(kind);
        }

        private static void Make(int id, Kind kind, Vector3 at)
        {
            Def def = Defs.First(d => d.Kind == kind);
            GameObject prefab = ZombiesAssets.Model(def.Model);
            var go = new GameObject("Zombies_PowerUp_" + kind);
            go.transform.position = at + Vector3.up * Float;
            if (prefab != null)
            {
                GameObject model = UnityEngine.Object.Instantiate(prefab, go.transform, false);
                Fit(model);
            }
            else
            {
                GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(ball.GetComponent<Collider>());
                ball.transform.SetParent(go.transform, false);
                ball.transform.localScale = Vector3.one * (_size?.Value ?? 0.5f) * 0.6f;
                ball.GetComponent<Renderer>().sharedMaterial = GlowMaterial;
            }
            if ((_haloSize?.Value ?? 1.8f) > 0f)
                AddHalo(go);
            Light light = new GameObject("Glow").AddComponent<Light>();
            light.transform.SetParent(go.transform, false);
            light.type = LightType.Point;
            light.color = new Color(0.45f, 1f, 0.3f);
            light.intensity = _glow?.Value ?? 0.8f;
            light.range = _glowRange?.Value ?? 3.5f;
            light.shadows = LightShadows.None;
            AudioSource hum = go.AddComponent<AudioSource>();
            hum.clip = ZombiesSounds.Clip("zm_pu_loop");
            hum.loop = true;
            hum.spatialBlend = 1f;
            hum.rolloffMode = AudioRolloffMode.Linear;
            hum.minDistance = 1f;
            hum.maxDistance = 15f;
            hum.volume = 0.6f * ZombiesSounds.Volume(ZombiesSounds.Group.PowerUps);
            if (hum.clip != null)
                hum.Play();
            ZombiesSounds.At("zm_pu_spawn", go.transform.position, 1f, 30f);
            PowerUpDrop drop = go.AddComponent<PowerUpDrop>();
            drop.Id = id;
            drop.Kind = kind;
            drop.Born = Time.time;
            drop.Lasts = _lasts?.Value ?? 26f;
            go.transform.localScale = Vector3.one * (def.Scale?.Value ?? 1f);
            Drops.Add(drop);
            ZombiesPlugin.Log.LogInfo($"Zombies power-ups: {Name(def)} dropped");
        }

        // The model sized (its longest side) and glowing green; centred on its holder
        private static void Fit(GameObject model)
        {
            Renderer[] rs = model.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in rs)
                r.sharedMaterials = r.sharedMaterials.Select(Gold).ToArray();
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.Destroy(c);
            if (rs.Length == 0)
                return;
            Bounds b = rs[0].bounds;
            foreach (Renderer r in rs)
                b.Encapsulate(r.bounds);
            float longest = Mathf.Max(b.size.x, b.size.y, b.size.z);
            if (longest > 0.0001f)
                model.transform.localScale *= (_size?.Value ?? 0.5f) / longest;
            b = rs[0].bounds;
            foreach (Renderer r in rs)
                b.Encapsulate(r.bounds);
            model.transform.position += model.transform.parent.position - b.center;
        }

        // The original's look: the model in shiny gold (its own normal maps kept), lit a little from inside so it reads in the dark
        private static readonly Dictionary<Material, Material> GoldOf = new Dictionary<Material, Material>();

        private static Material Gold(Material from)
        {
            if (from == null)
                return GlowMaterial;
            if (GoldOf.TryGetValue(from, out Material gold) && gold != null)
                return gold;
            gold = new Material(from) { name = from.name + "_gold" };
            // Standard (Specular setup), as zombies.bundle builds them: metal gold = a dark gold base and a gold shine.
            // The spec map's keyword must go with the map: left on, the shader read the missing map as white (white
            // shine = silver; the user, 2026-11-08: "our drops are still silver")
            Color body = new Color(0.42f, 0.27f, 0.06f), shine = new Color(1f, 0.77f, 0.32f);
            if (gold.HasProperty("_MainTex"))
                gold.SetTexture("_MainTex", null);
            if (gold.HasProperty("_Color"))
                gold.SetColor("_Color", body);
            if (gold.HasProperty("_SpecColor"))
                gold.SetColor("_SpecColor", shine);
            if (gold.HasProperty("_SpecGlossMap"))
                gold.SetTexture("_SpecGlossMap", null);
            if (gold.HasProperty("_MetallicGlossMap"))
                gold.SetTexture("_MetallicGlossMap", null);
            gold.DisableKeyword("_SPECGLOSSMAP");
            gold.DisableKeyword("_METALLICGLOSSMAP");
            if (gold.HasProperty("_Metallic"))
                gold.SetFloat("_Metallic", 0.55f);
            if (gold.HasProperty("_Glossiness"))
                gold.SetFloat("_Glossiness", 0.85f);
            if (gold.HasProperty("_GlossMapScale"))
                gold.SetFloat("_GlossMapScale", 0.85f);
            if (gold.HasProperty("_EmissionColor"))
            {
                gold.EnableKeyword("_EMISSION");
                gold.SetTexture("_EmissionMap", null);
                gold.SetColor("_EmissionColor", shine * 0.6f * (_goldGlow?.Value ?? 0.6f));
            }
            GoldOf[from] = gold;
            return gold;
        }

        private static Texture2D _haloTexture;
        private static Material _haloMaterial;

        // The soft green glow round it (the original's halo): a camera-facing quad, brightest in the middle
        private static void AddHalo(GameObject on)
        {
            if (_haloMaterial == null)
            {
                const int n = 64;
                _haloTexture = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                        float a = Mathf.Clamp01(1f - d);
                        _haloTexture.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                    }
                _haloTexture.Apply();
                _haloMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = _haloTexture, color = new Color(0.35f, 1f, 0.3f, 0.55f) };
            }
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            UnityEngine.Object.Destroy(quad.GetComponent<Collider>());
            quad.name = "Halo";
            quad.transform.SetParent(on.transform, false);
            quad.transform.localScale = Vector3.one * (_size?.Value ?? 1.25f) * (_haloSize?.Value ?? 1.8f);
            Renderer r = quad.GetComponent<Renderer>();
            r.sharedMaterial = _haloMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            quad.AddComponent<FaceCamera>();
        }

        private class FaceCamera : MonoBehaviour
        {
            private void LateUpdate()
            {
                Camera cam = Camera.main;
                if (cam == null || transform.parent == null)
                    return;
                // Just behind the model as seen from the camera, so the glow surrounds it without tinting its front
                Vector3 centre = transform.parent.position;
                Vector3 away = (centre - cam.transform.position).normalized;
                transform.position = centre + away * (_size?.Value ?? 1.25f) * 0.45f;
                transform.rotation = Quaternion.LookRotation(away, cam.transform.up);
            }
        }

        private static Material GlowMaterial
        {
            get
            {
                if (_glowMaterial == null)
                    _glowMaterial = new Material(Shader.Find("Sprites/Default")) { color = new Color(0.3f, 0.55f, 0.2f, 0.92f) }; // dimmer green (was 0.55, 1, 0.35: too bright)
                return _glowMaterial;
            }
        }

        public static void Tick()
        {
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            // A Fika headless's own player is nobody: it never picks a drop up
            if (me != null && !ZombiesNet.Headless && ZombiesDowned.Mine == ZombiesDowned.State.Up)
            {
                Vector3 p = me.Transform.position;
                foreach (PowerUpDrop d in Drops.ToList())
                {
                    if (d == null)
                    {
                        Drops.Remove(d);
                        continue;
                    }
                    Vector3 at = d.transform.position;
                    if (Vector2.Distance(new Vector2(at.x, at.z), new Vector2(p.x, p.z)) <= PickUpReach && Mathf.Abs(at.y - Float - p.y) < 2f)
                    {
                        if (ZombiesNet.IsHost)
                            GrabRequest(d.Id, me.ProfileId);
                        else if (!d.Asked)
                        {
                            // The host decides who got it first
                            d.Asked = true;
                            ZombiesNet.PowerUpGrab(d.Id);
                        }
                    }
                }
            }
            // Double Points over: the original's "off" sound
            foreach (Kind k in Until.Keys.ToList())
                if (Time.time >= Until[k])
                {
                    Until.Remove(k);
                    if (k == Kind.DoublePoints)
                        ZombiesSounds.Head("zm_pu_double_off", 0.8f);
                    ZombiesPlugin.Log.LogInfo($"Zombies power-ups: {k} over");
                }
            FireSaleMusic();
            // A box on every spot while it lasts (the spots kept by the match: never a search of the scene each frame,
            // which on this map cost a lot every frame)
            MatchBoxSpots.Current?.FireSale(FireSaleOn);
        }

        /// <summary>Insta-Kill is on right now.</summary>
        public static bool InstaKillOn => ZombiesMatch.Active && Active(Kind.InstaKill);

        /// <summary>A fire sale is on right now.</summary>
        public static bool FireSaleOn => ZombiesMatch.Active && Active(Kind.FireSale);

        // ---------- Fire Sale's music (the original mus\zombie\firesale), from the box for as long as it lasts ----------

        private static AudioSource _saleMusic;
        private static float _saleFade;

        private static void FireSaleMusic()
        {
            bool on = ZombiesMatch.Active && Active(Kind.FireSale);
            if (on && _saleMusic == null)
            {
                AudioClip clip = ZombiesSounds.Clip("zm_pu_firesale_music");
                if (clip == null)
                    return;
                MysteryBox box = MatchBoxSpots.Current?.Box;
                Player me = Singleton<GameWorld>.Instance?.MainPlayer;
                var go = new GameObject("Zombies_FireSaleMusic");
                go.transform.position = box != null ? box.transform.position + Vector3.up : me != null ? me.Transform.position : Vector3.zero;
                _saleMusic = go.AddComponent<AudioSource>();
                _saleMusic.clip = clip;
                _saleMusic.loop = true;
                // Heard round the box, loud close to it (a 2D bed when there's no box)
                _saleMusic.spatialBlend = box != null ? 1f : 0f;
                _saleMusic.rolloffMode = AudioRolloffMode.Linear;
                _saleMusic.minDistance = 6f;
                _saleMusic.maxDistance = 45f;
                _saleMusic.volume = 0.9f * ZombiesSounds.Volume(ZombiesSounds.Group.FireSale);
                _saleMusic.Play();
                _saleFade = 1f;
                ZombiesPlugin.Log.LogInfo($"Zombies power-ups: fire sale music on ({(box != null ? "at the box" : "no box: in your head")})");
            }
            if (_saleMusic == null)
                return;
            if (!on)
            {
                // Over: a second's fade, then gone
                _saleFade -= Time.deltaTime;
                if (_saleFade <= 0f)
                {
                    UnityEngine.Object.Destroy(_saleMusic.gameObject);
                    _saleMusic = null;
                    return;
                }
            }
            _saleMusic.volume = 0.9f * ZombiesSounds.Volume(ZombiesSounds.Group.FireSale) * Mathf.Clamp01(_saleFade);
        }

        private static void Grab(Kind kind, Player me, string by)
        {
            if (me != null && by == me.ProfileId)
            {
                string line = kind switch
                {
                    Kind.MaxAmmo => "powerup_ammo", Kind.InstaKill => "powerup_insta", Kind.Nuke => "powerup_nuke",
                    Kind.FireSale => "powerup_firesale", Kind.DoublePoints => "powerup_double", Kind.Carpenter => "powerup_carpenter",
                    _ => null,
                };
                // After the announcer (the original: about 2 s after the pickup, the user's gameplay video)
                if (line != null)
                    ZombiesVox.SayAfter(2.2f, line, 0.9f, 3f);
            }
            Def def = Defs.First(d => d.Kind == kind);
            ZombiesSounds.Head("zm_pu_grab", 0.9f);
            if (def.Vox != null)
                ZombiesSounds.Head(def.Vox, 1f);
            if (def.Sound != null)
                ZombiesSounds.Head(def.Sound, 0.8f);
            _banner = def.Text;
            _bannerAt = Time.realtimeSinceStartup;
            if (def.Timed)
                Until[kind] = Time.time + (_timed?.Value ?? 30f);
            ZombiesPlugin.Log.LogInfo($"Zombies power-ups: {Name(def)} picked up");
            switch (kind)
            {
                case Kind.MaxAmmo:
                    // Bled out and watching: nothing (the original: the respawn kit is the starting pistol, not a Max Ammo'd one)
                    if (ZombiesDowned.Mine == ZombiesDowned.State.Dead)
                        break;
                    foreach (Weapon w in me.InventoryController.Inventory.Equipment.GetAllItems().OfType<Weapon>())
                        ZombiesAmmo.FillGun(w);
                    ZombiesAmmo.FillHeldGun(me);
                    ZombiesGrenades.FillTo(me, ZombiesGrenades.Most);
                    ZombiesUbgl.FillAll(me);
                    // The monkeys back to three, if you've had them this life (the original: Max Ammo is the only refill)
                    if (ZombiesMonkey.HadThisLife || ZombiesMonkey.Count(me) > 0)
                        ZombiesPlugin.Instance.StartCoroutine(ZombiesMonkey.Give(me, "a Max Ammo"));
                    break;
                case Kind.Nuke:
                    ZombiesPlugin.Instance.StartCoroutine(Nuke());
                    break;
                case Kind.Carpenter:
                    // The host puts the boards back (and tells everyone); the points are everyone's
                    if (ZombiesNet.IsHost)
                        ZombiesBarricade.RepairAll();
                    ZombiesMatch.AddPoints(CarpenterPoints);
                    break;
                case Kind.WidowsKiss:
                    // Only the one who took it
                    if (by == me.ProfileId)
                        ZombiesGrenades.FillTo(me, Mathf.Min(ZombiesGrenades.Most, ZombiesGrenades.Count(me) + 1));
                    break;
            }
        }

        // The original's nuke: a white flash, then each zombie goes up in its own small blast, one after another. Only the look
        // and sound of a blast (EFT's effect, no explosion damage), so players are never hurt; the zombie just dies
        private static IEnumerator Nuke()
        {
            _flashAt = Time.realtimeSinceStartup;
            ZombiesSounds.Head("zm_pu_nuke_flash", 1f);
            ZombiesMatch.AddPoints(NukePoints);
            // Only the host's game has the zombies' brains: it does the killing, everyone sees the flash. Each player's
            // game puts the clouds and their sound on its own copies of the zombies (a Fika headless host shows nobody
            // anything: the user saw the kills but no clouds, 2026-11-09)
            if (!ZombiesNet.IsHost)
            {
                List<Player> copies = Singleton<GameWorld>.Instance?.AllAlivePlayersList.Where(ZombiesTeam.IsZombie).ToList() ?? new List<Player>();
                yield return new WaitForSeconds(0.6f);
                foreach (Player p in copies.OrderBy(_ => UnityEngine.Random.value))
                {
                    if (p == null)
                        continue;
                    ZombiesNukeCloud.At(p.Transform.position);
                    ZombiesSounds.At("zm_pu_nuke_soul_#", p.Transform.position + Vector3.up * 0.9f, 0.8f, 35f);
                    yield return new WaitForSeconds(UnityEngine.Random.Range(0.08f, 0.25f));
                }
                yield break;
            }
            List<BotOwner> doomed = ZombiesRounds.Zombies.Where(z => z != null && !z.IsDead).ToList();
            ZombiesPlugin.Log.LogInfo($"Zombies power-ups: nuke, {doomed.Count} zombie(s)");
            yield return new WaitForSeconds(0.6f);
            foreach (BotOwner z in doomed.OrderBy(_ => UnityEngine.Random.value))
            {
                if (z == null || z.IsDead || z.GetPlayer == null)
                    continue;
                Vector3 at = z.GetPlayer.Transform.position + Vector3.up * 0.9f;
                // A flat 2D mushroom cloud where it stood (a full explosion per zombie cost too much)
                ZombiesNukeCloud.At(z.GetPlayer.Transform.position);
                ZombiesSounds.At("zm_pu_nuke_soul_#", at, 0.8f, 35f);
                z.GetPlayer.ActiveHealthController?.Kill(EDamageType.Undefined);
                yield return new WaitForSeconds(UnityEngine.Random.Range(0.08f, 0.25f));
            }
        }

        // ---------- On screen ----------

        private static GUIStyle _big, _small;

        public static void Draw()
        {
            if (_big == null)
            {
                _big = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _small = new GUIStyle(_big);
                // EFT's own font when the game has it for IMGUI (user, 2026-11-11)
                if (ZombiesUi.DisplayImguiFont() != null)
                {
                    _big.font = ZombiesUi.DisplayImguiFont();
                    _big.fontStyle = FontStyle.Normal;
                    _small.font = ZombiesUi.DisplayImguiFont();
                    _small.fontStyle = FontStyle.Bold;
                }
            }
            float s = Screen.height / 1080f;
            Color before = GUI.color;

            // The nuke's white flash
            float flash = 1f - (Time.realtimeSinceStartup - _flashAt) / 1.2f;
            if (flash > 0f)
            {
                GUI.color = new Color(1f, 1f, 1f, flash * 0.85f);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            }

            // Picked up: the original's green burst, filling the screen for a moment and fading (the user's gameplay video)
            float age = Time.realtimeSinceStartup - _bannerAt;
            if (_banner != null && age < 0.6f)
            {
                float a = age < 0.08f ? age / 0.08f : 1f - (age - 0.08f) / 0.52f;
                GUI.color = new Color(0.35f, 1f, 0.25f, 0.45f * a);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = before;
            }
            // The name banner only if F12 asks for it (the original shows none)
            if (_banner != null && age < 3f && (_showBanner?.Value ?? false))
            {
                float a = age < 2.2f ? 1f : 1f - (age - 2.2f) / 0.8f;
                _big.fontSize = Mathf.RoundToInt(64 * s * (1f + Mathf.Max(0f, 0.25f - age) * 1.2f));
                var r = new Rect(0, Screen.height * 0.22f, Screen.width, 100 * s);
                _big.normal.textColor = new Color(0f, 0f, 0f, 0.7f * a);
                GUI.Label(new Rect(r.x + 3 * s, r.y + 3 * s, r.width, r.height), _banner, _big);
                _big.normal.textColor = new Color(0.6f, 1f, 0.35f, a);
                GUI.Label(r, _banner, _big);
            }

            // The timed ones running: the original's small icons, bottom middle (2x, skull, $), blinking the last 5 s
            var running = Defs.Where(d => d.Timed && Active(d.Kind)).ToList();
            if (running.Count > 0)
            {
                float size = 52 * s * (_iconSize?.Value ?? 1f), gap = 10 * s;
                float x = (Screen.width - running.Count * size - (running.Count - 1) * gap) / 2f;
                float y = Screen.height - size - (_iconUp?.Value ?? 60f) * s;
                foreach (Def d in running)
                {
                    float left = Until[d.Kind] - Time.time;
                    bool show = left > 5f || Mathf.Repeat(Time.time * 4f, 1f) < 0.6f;
                    Texture2D icon = Icon(d.Kind);
                    if (show && icon != null)
                        GUI.DrawTexture(new Rect(x, y, size, size), icon);
                    else if (show)
                    {
                        _small.fontSize = Mathf.RoundToInt(22 * s);
                        _small.normal.textColor = Color.white;
                        GUI.Label(new Rect(x - size, y, size * 3, size), Name(d), _small);
                    }
                    x += size + gap;
                }
            }
            GUI.color = before;
        }
    }

    /// <summary>A power-up lying in the world: spins, bobs, blinks before it goes.</summary>
    internal class PowerUpDrop : MonoBehaviour
    {
        public ZombiesPowerUps.Kind Kind;
        public int Id;
        public bool Asked;
        public float Born, Lasts;
        private Vector3 _home;
        private Renderer[] _renderers;

        private void Start()
        {
            _home = transform.position;
            _renderers = GetComponentsInChildren<Renderer>(true);
        }

        private void Update()
        {
            float age = Time.time - Born;
            if (age > Lasts)
            {
                Destroy(gameObject);
                return;
            }
            transform.Rotate(0f, 90f * Time.deltaTime, 0f, Space.World);
            transform.position = _home + Vector3.up * Mathf.Sin(age * 2.2f) * 0.08f;
            // Blinking faster as it runs out
            float left = Lasts - age;
            bool on = left > 6f || Mathf.Repeat(age * (left > 3f ? 3f : 6f), 1f) < 0.55f;
            foreach (Renderer r in _renderers)
                if (r != null)
                    r.enabled = on;
        }
    }
}
