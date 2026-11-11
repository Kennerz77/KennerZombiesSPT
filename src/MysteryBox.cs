using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's mystery box, in the hideout for now (free to use there; points come with the zombies mode). Opening it
    /// spins the guns from the box list on the original's own timing (the "switch" notetracks of o_zombie_magic_box_weapon_rise),
    /// then offers the last one for 12 seconds. Now and then the teddy bear comes up instead: the box laughs, flies off
    /// and lands at its other spot. Two spots, placed with the F12 debug entries.
    /// </summary>
    internal class MysteryBox : MonoBehaviour
    {
        /// <summary>the original models are oversized for EFT (the perk machines too): the box is drawn at this size.</summary>
        public const float BoxScale = 0.35f;

        private const float InteractDistance = 3f;
        private const string CollisionLayer = "LowPolyCollider";

        // o_zombie_magic_box_weapon_rise's "switch" notetrack frames (30 fps): a new gun each, quick then slowing down
        private static readonly int[] SwitchFrames =
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 20, 22, 24, 26, 28, 31, 34, 37, 40, 43, 46, 49, 52, 57, 63, 69, 75, 81, 89, 99, 109 };
        private const float RiseSeconds = 117f / 30f;   // the rise anim's "end" note
        private const float OfferSeconds = 12f;          // The original: the gun waits 12 s to be taken
        private const float LidClosedAt = 10f / 30f;     // the close anim's "lid_closed" note
        private const float LeaveSeconds = 225f / 30f, ArriveSeconds = 148f / 30f, ImpactAt = 140f / 30f;
        // The last gun can be taken 2 s after it settles; the box can be used again 2 s after its lid closes (the user)
        private const float TakeDelay = 0.7f, ReuseDelay = 2f;
        // The teddy: up for a second before anything (it sinks in), then rising with the laugh, gone after this long
        // The original (the user's boxgoes.mp4, 2026-11-07): Samantha's laugh very shortly after the bear comes up; the bear
        // sits about 3 s, then flies up and away; about 1.5 s after it's gone the empty box lifts off with its lid still
        // open, the lid closing on the way up (the leave animation's own)
        private const float TeddyRise = 3.0f, LaughAfterTeddy = 0.5f, RiseAfterLaugh = 2.3f, LiftAfterTeddyGone = 0.4f; // timed off the video (bear 6.2 s, laugh 6.73 s, rise ~9 s, bye bye 12.4 s, lift ~13 s)
        // The box's flight: its background sound first, then (this much later) the laugh and "bye bye" as it lifts
        private const float LiftAfterSound = 0.4f;
        private const float SpinSoundAfter = 2.5f; // the spin sound, this long after the box starts to rise

        private enum State { Idle, Spinning, Offering, Closing, Leaving, Away, Arriving }

        private State _state = State.Idle;
        private Animator _box, _weaponPivot, _teddyPivot;
        private Transform _weaponTag;
        private Light _light;
        private float _lightFull;
        private readonly Dictionary<string, AudioSource> _audio = new Dictionary<string, AudioSource>();
        private readonly Dictionary<AudioSource, float> _baseVolume = new Dictionary<AudioSource, float>();
        private GameObject _teddy;
        private BoxBeam _beam;
        private BoxCollider _gunHit;
        private float _takeFrom, _usableFrom;
        // Who paid for this spin: only they can take the gun (with Fika, the others can't steal it)
        private string _buyer;
        // Points this game paid for the running spin (0 on the other players' games): given back if the teddy comes
        private int _paidPoints;
        private bool _paying;
        private readonly ZombiesInteraction _menu = new ZombiesInteraction();

        private readonly List<BoxGuns.Model> _models = new List<BoxGuns.Model>();
        private BoxGuns.Model _shown;
        private bool _loadingModels;

        // The original's teddy: never in the first 4 uses at a spot, then a rising chance (see TeddyComes)
        private int _usesHere, _moves;
        internal static bool ForceTeddy;

        /// <summary>F12: the next spin stops on this gun (its box_builds id: raygun, thundergun, cymbal_monkey), carried or not.</summary>
        internal static string ForceGun;

        public int Spot { get; private set; }

        /// <summary>A zombies match's spots (the layout's), else the hideout's two F12 spots.</summary>
        internal MatchBoxSpots Match { get; set; }

        // How far the cinder-block stand lifts the box off the floor (its spot is where the stand stands)
        private float _standLift;

        /// <summary>A Fire Sale's extra box at another spot (the original: a box on every spot while it lasts): never a teddy,
        /// never moves, not shared over the network, gone once the fire sale ends and it's closed.</summary>
        public bool FireSaleExtra { get; set; }

        /// <summary>Nobody's using it (shut, not spinning, offering or moving).</summary>
        public bool Idle => _state == State.Idle;

        /// <summary>The F12 raid test box: it has no spots, so the teddy lands it back where it was.</summary>
        public bool RaidTest { get; set; }

        // Our own dice for the teddy and the gun (EFT reseeds Unity's Random on shots: the rolls repeated, ZombiesPowerUps)
        private static readonly System.Random Dice = new System.Random();

        public void Init(int spot, (Vector3 position, Quaternion rotation) at)
        {
            Spot = spot;
            name = "Zombies_MysteryBox (KennerZombies)";
            transform.localScale = Vector3.one * BoxScale;
            Transform standFoot = transform.Find("StandHeight");
            _standLift = standFoot != null ? -standFoot.localPosition.y * BoxScale : 0f;
            transform.SetPositionAndRotation(Seat(spot, at), at.rotation);
            HideOwnStand();
            _box = GetComponent<Animator>();
            _weaponPivot = transform.Find("WeaponPivot")?.GetComponent<Animator>();
            _teddyPivot = transform.Find("TeddyPivot")?.GetComponent<Animator>();
            _weaponTag = transform.Find("WeaponPivot/tag_origin");
            _light = transform.Find("BoxLight")?.GetComponent<Light>();
            if (_light != null)
                _light.range *= BoxScale; // about 2.5 m in game
            foreach (AudioSource s in GetComponentsInChildren<AudioSource>(true))
            {
                _audio[s.name.Replace("Audio_", "")] = s;
                _baseVolume[s] = s.volume;
            }
            _teddy = transform.Find("TeddyPivot/tag_origin/Teddy")?.gameObject;
            FixShaders();
            int layer = LayerMask.NameToLayer(CollisionLayer);
            if (layer >= 0)
                foreach (Collider c in GetComponentsInChildren<Collider>(true))
                    c.gameObject.layer = layer;
            Play(_box, "idle");
            Play(_weaponPivot, "down");
            Play(_teddyPivot, "down");
            Glow();
            _beam = BoxBeam.Create(transform);
            // A generous hit area round the gun on offer: looking at the gun (not into the hay) gives the prompt
            var hit = new GameObject("GunHit");
            hit.transform.SetParent(transform, false);
            if (layer >= 0)
                hit.layer = layer;
            _gunHit = hit.AddComponent<BoxCollider>();
            _gunHit.enabled = false;
            ZombiesPlugin.Log.LogInfo($"Mystery box set out at spot {spot + 1} ({at.position.x:F2}, {at.position.y:F2}, {at.position.z:F2}).");
            StartCoroutine(LoadModels());
        }

        // The question marks' glow: warm yellow (the user, 2026-11-01)
        // Not too strong: raids' exposure made 3x blinding (the user, 2026-11-01, Factory)
        private static readonly Color QuestionGlow = new Color(1f, 0.75f, 0.25f) * 1.5f;

        // The warm light out of the open box: on the moment it opens, strong enough to light the gun and the room
        private const float BoxLightFull = 3f;

        private void Glow()
        {
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
                foreach (Material mat in renderer.materials)
                {
                    if (mat == null || !mat.name.Contains("qmark") || !mat.HasProperty("_EmissionColor"))
                        continue;
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", QuestionGlow);
                }
        }

        private void FixShaders()
        {
            // Only rebind bundled shaders that can't run here (pink); swapping working ones can lose keyword variants
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
                foreach (Material mat in renderer.materials)
                {
                    if (mat == null || mat.shader == null || mat.shader.isSupported)
                        continue;
                    Shader gameShader = Shader.Find(mat.shader.name);
                    if (gameShader != null)
                        mat.shader = gameShader;
                }
        }

        /// <summary>Every box gun's display model, made one after another in the background (the spin uses what's ready).</summary>
        private IEnumerator LoadModels()
        {
            if (_loadingModels || _models.Count > 0)
                yield break;
            _loadingModels = true;
            foreach (BoxGuns.Gun gun in BoxGuns.BoxGunList())
            {
                yield return BoxGuns.MakeModel(gun, m =>
                {
                    m.Go.SetActive(false);
                    _models.Add(m);
                });
            }
            _loadingModels = false;
            ZombiesPlugin.Log.LogInfo($"Mystery box: {_models.Count} gun models ready.");
        }

        private void OnDestroy()
        {
            _menu.Reset();
            if (_beam != null)
                Destroy(_beam.gameObject);
            foreach (BoxGuns.Model m in _models)
                if (m.Go != null)
                    Destroy(m.Go);
            _models.Clear();
        }

        private void Update()
        {
            if (_state != State.Away && ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
            if (_light != null)
                _light.intensity = Mathf.MoveTowards(_light.intensity, _lightFull, Time.deltaTime * 4f);
        }

        private void LateUpdate()
        {
            // The shown gun hangs on the rising pivot at its own size (the box is scaled, the gun isn't)
            if (_shown?.Go != null && _weaponTag != null)
            {
                Quaternion turn = transform.rotation * _shown.Align;
                Vector3 down = Vector3.down * Sink();
                _shown.Go.transform.SetPositionAndRotation(_weaponTag.position + turn * _shownOffset + down, turn);
                if (_gunHit != null && _gunHit.enabled)
                    _gunHit.transform.position = _hitAt + down;
            }
        }

        // The original's offer: the gun hangs over the box, then sinks slowly back into it as its time runs out (gone into the
        // box as the lid shuts)
        private float _offerStart, _sinkDepth;
        private Vector3 _hitAt;

        private float Sink()
        {
            if (_state != State.Offering || _sinkDepth <= 0f)
                return 0f;
            float t = Mathf.Clamp01((Time.time - _offerStart) / OfferSeconds);
            return _sinkDepth * t * t;
        }

        private IEnumerable<(string, Action)> Options()
        {
            switch (_state)
            {
                case State.Idle:
                    bool ready = _models.Count > 0 && Time.time >= _usableFrom && !_paying;
                    // A zombies match: points; elsewhere roubles
                    string cost = ZombiesMatch.Active ? $"{ZombiesPowerUps.BoxPrice}" : $"{BoxPay.Price} ₽";
                    yield return (_models.Count > 0 ? $"Mystery Box ({cost})" : "Mystery Box (getting the guns ready...)",
                        !ready ? null : !ZombiesMatch.Active ? (Action)Open : ZombiesMatch.Afford(ZombiesPowerUps.BoxPrice, Open));
                    break;
                case State.Offering:
                    if (!IsBuyer())
                        yield return ($"{_shown?.Gun.Name} (someone else's)", null);
                    else
                        yield return ($"Take {_shown?.Gun.Name}", Time.time >= _takeFrom ? (Action)Take : null);
                    break;
                case State.Spinning:
                    yield return ("Mystery Box...", null);
                    break;
            }
        }

        // ---------- Using the box ----------

        /// <summary>The player now is the one who opened the box (always, solo; with Fika, a teammate isn't).</summary>
        private bool IsBuyer() => _buyer == null || (ZombiesPlugin.CurrentPlayer(out _, out Player p) && p.ProfileId == _buyer);

        /// <summary>Pays (950 roubles, the stash in the hideout, the roubles carried in a raid), then opens.</summary>
        private void Open()
        {
            if (_state != State.Idle || Time.time < _usableFrom || _paying || !ZombiesPlugin.CurrentPlayer(out _, out Player buyer))
                return;
            _paying = true;
            // In a zombies match the box costs points (950, as in the original)
            if (ZombiesMatch.Active)
            {
                _paying = false;
                int price = ZombiesPowerUps.BoxPrice;
                if (ZombiesMatch.Spend(price))
                {
                    StartSpin(buyer.ProfileId);
                    _paidPoints = price;
                }
                return;
            }
            BoxPay.Pay(buyer, ZombiesPlugin.InHideout, paid =>
            {
                _paying = false;
                if (this == null)
                    return;
                if (!paid)
                {
                    // The original's deny buzz (the same one as the perk machines), from the box
                    AudioClip deny = ZombiesAssets.Clip("box_deny");
                    if (deny != null)
                        AudioSource.PlayClipAtPoint(deny, transform.position + Vector3.up * 0.5f, 0.8f * ZombiesPlugin.BoxVolume("Open"));
                    return;
                }
                if (_state == State.Idle)
                    StartSpin(buyer.ProfileId);
            });
        }

        /// <summary>Straight to a spot (the host's pick at the start), standing idle there.</summary>
        public void PlaceAt(int spot, (Vector3 position, Quaternion rotation) at)
        {
            Spot = spot;
            transform.SetPositionAndRotation(Seat(spot, at), at.rotation);
        }

        // Where the box stands at a spot: on that spot's plank (a match's spots keep their cinder blocks and plank; the
        // box's own stand is hidden), else on its own stand
        private Vector3 Seat(int spot, (Vector3 position, Quaternion rotation) at)
        {
            float? top = Match?.PlankTop(spot);
            return top.HasValue ? new Vector3(at.position.x, top.Value, at.position.z) : at.position + Vector3.up * _standLift;
        }

        private void HideOwnStand()
        {
            Transform stand = transform.Find("Stand");
            if (Match != null && stand != null)
                stand.gameObject.SetActive(false);
        }

        // ---------- Shared with the team (ZombiesNet): whoever spins, everyone sees the same spin ----------

        private BoxGuns.Model _forcedFinal;
        private int _forcedNext = -1;

        private static MysteryBox Current => ZombiesMatch.Instance != null ? FindObjectsOfType<MysteryBox>().FirstOrDefault(b => b.Match != null && !b.FireSaleExtra) : null;

        // The box at a spot: the main one or a fire sale's extra there (-1: the main one)
        private static MysteryBox AtSpot(int spot) => spot < 0 ? Current
            : FindObjectsOfType<MysteryBox>().FirstOrDefault(b => b.Match != null && b.Spot == spot);

        /// <summary>A teammate's spin: the same gun (or teddy) and, for a teddy, the same next spot. Fire sale boxes
        /// too (they were each player's own: another player could open the same one, user 2026-11-09).</summary>
        public static void SpinRemote(string buyer, bool teddy, string gun, int next, int spot)
        {
            MysteryBox box = AtSpot(spot);
            if (box == null)
                return;
            if (box._state != State.Idle)
                box.StopAllCoroutines();
            box._forcedFinal = box._models.FirstOrDefault(m => m.Gun.Id == gun);
            box._forcedNext = next;
            box.StartSpin(buyer, teddy);
        }

        /// <summary>The buyer took the gun: the lid closes for everyone.</summary>
        public static void TakenRemote(int spot)
        {
            MysteryBox box = AtSpot(spot);
            if (box != null && box._state == State.Offering)
                box.StartCoroutine(box.Close());
        }

        private void StartSpin(string buyer, bool? remoteTeddy = null)
        {
            _buyer = buyer;
            _paidPoints = 0; // set again by the buyer's own Open, right after
            _state = State.Spinning;
            _usesHere++;
            bool teddy = remoteTeddy ?? TeddyComes();
            if (remoteTeddy == null)
            {
                // Decided here, played the same on every player's game
                _forcedFinal = teddy ? null : PickFinal();
                _forcedNext = teddy && Match != null ? Match.Other(Spot) : -1;
                ZombiesNet.BoxSpin(teddy, _forcedFinal?.Gun.Id, _forcedNext, Spot);
            }
            Play(_box, "open");
            Sound("Open");
            Sound("Music");
            // No spin sound (the user, 2026-11-01: it muddied the music box)
            _lightFull = BoxLightFull;
            if (_light != null)
                _light.intensity = BoxLightFull;
            StartCoroutine(Spin(teddy));
        }

        /// <summary>
        /// The original's teddy odds (the original's box script, kept by the later games): never in the first 4 uses at a spot; uses 4-7 a
        /// 15% chance; then, before the box has ever moved, the 8th use always; after a move, uses 8-12 a 30% chance and
        /// 13 on 50%. The count starts again at each new spot.
        /// </summary>
        private bool TeddyComes()
        {
            // No teddy during a fire sale , and never from a fire sale's extra box
            if (FireSaleExtra || ZombiesPowerUps.FireSaleOn)
                return false;
            if (ForceTeddy)
            {
                ForceTeddy = false;
                return true;
            }
            int n = _usesHere;
            float roll = (float)Dice.NextDouble();
            bool teddy = n < 4 ? false
                : n < 8 ? roll < 0.15f
                : _moves == 0 ? true
                : n < 13 ? roll < 0.30f
                : roll < 0.50f;
            ZombiesPlugin.Log.LogInfo($"Mystery box use {n} at this spot (moves {_moves}): {(teddy ? "teddy" : "a gun")}");
            return teddy;
        }

        private IEnumerator Spin(bool teddy)
        {
            // Where the pivot rests in the box: the shown gun sinks back to here while it waits
            float restY = _weaponTag != null ? _weaponTag.position.y : 0f;
            Play(_weaponPivot, "rise");
            float start = Time.time;
            BoxGuns.Model last = null;
            // Any gun flashes past while it spins; the one it stops on follows the original's rules (PickFinal)
            BoxGuns.Model final = _forcedFinal ?? PickFinal();
            _forcedFinal = null;
            for (int i = 0; i < SwitchFrames.Length; i++)
            {
                float at = start + SwitchFrames[i] / 30f;
                while (Time.time < at)
                    yield return null;
                last = Show(i == SwitchFrames.Length - 1 && final != null ? final : Pick(last));
            }
            while (Time.time < start + RiseSeconds)
                yield return null;
            Play(_weaponPivot, "up");

            if (teddy)
            {
                Show(null);
                // The original gives the box's price back when the teddy shows (user, 2026-11-11); only the buyer's game paid
                if (_paidPoints > 0 && ZombiesMatch.Active)
                {
                    ZombiesMatch.Refund(_paidPoints);
                    ZombiesPlugin.Log.LogInfo($"Mystery box: teddy, {_paidPoints} points given back");
                }
                _paidPoints = 0;
                StartCoroutine(Leave());
                yield break;
            }

            // The gun waits to be taken (from 2 s after it settles)
            _state = State.Offering;
            _takeFrom = Time.time + TakeDelay;
            FitGunHit();
            _hitAt = _gunHit != null ? _gunHit.transform.position : Vector3.zero;
            _offerStart = Time.time;
            _sinkDepth = _weaponTag != null ? Mathf.Max(0f, _weaponTag.position.y - restY) : 0f;
            float until = Time.time + OfferSeconds;
            while (_state == State.Offering && Time.time < until)
                yield return null;
            if (_state == State.Offering)
                StartCoroutine(Close());
        }

        /// <summary>
        /// The gun the spin stops on, as the original's box decides: never one the buyer already carries (until they've swapped it
        /// away), and a wonder weapon (Ray Gun, Thundergun) only while nobody in the lobby has one. Solo the lobby is
        /// the player; with Fika it becomes every player's guns (the shared match state). With nothing allowed, any.
        /// </summary>
        private BoxGuns.Model PickFinal()
        {
            if (ForceGun != null)
            {
                BoxGuns.Model forced = _models.FirstOrDefault(m => m.Gun.Id == ForceGun);
                ZombiesPlugin.Log.LogInfo($"Mystery box: F12 forced the {ForceGun}{(forced == null ? " (not in this box)" : "")}");
                ForceGun = null;
                if (forced != null)
                    return forced;
            }
            HashSet<string> carried = CarriedTemplates();
            HashSet<string> lobby = ZombiesPlugin.WonderWeaponsOut(carried);
            // The Ray Gun: one per player, every player can have one (the user, 2026-11-09). The Thundergun: one in
            // the whole match, never while any player has it; once its owner swaps it away it's back in the box for
            // anyone, them too (the check reads everyone's gear now). Never both on one player (the user, 2026-11-08:
            // holding one, the box won't give the other). Both still show in the spin.
            HashSet<string> held = WonderGunsHeldByAnyone();
            bool buyerHasOne = carried.Contains(ZombiesAmmo.RaygunId) || carried.Contains(ZombiesAmmo.ThundergunId);
            List<BoxGuns.Model> allowed = _models.Where(m =>
            {
                // The monkeys once a life (the user, 2026-11-08): still in the spin, never the pick once you've had them
                if (m.Gun.Id == BoxGuns.MonkeyId)
                    return !ZombiesMonkey.HadThisLife && ZombiesMonkey.Count(Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer) == 0;
                string tpl = BoxGuns.Template(m.Gun);
                if (tpl == ZombiesAmmo.RaygunId || tpl == ZombiesAmmo.ThundergunId)
                    if ((tpl == ZombiesAmmo.ThundergunId && held.Contains(tpl)) || buyerHasOne)
                        return false;
                return tpl != null && !carried.Contains(tpl) && !(m.Gun.Role == "box_wonder" && lobby.Contains(tpl));
            }).ToList();
            if (allowed.Count == 0)
                return Pick(null);
            // Wonder weapons come up a little less often than the rest (the user, 2026-11-01)
            float total = allowed.Sum(Weight), roll = (float)Dice.NextDouble() * total;
            foreach (BoxGuns.Model m in allowed)
            {
                roll -= Weight(m);
                if (roll <= 0f)
                    return m;
            }
            return allowed[allowed.Count - 1];
        }

        // The wonder guns (Ray Gun, Thundergun) any player in the match has on them, teammates too (their kit is synced)
        private static HashSet<string> WonderGunsHeldByAnyone()
        {
            var held = new HashSet<string>();
            try
            {
                foreach (Player p in ZombiesTeam.List())
                {
                    if (p == null || !ZombiesTeam.IsHuman(p))
                        continue;
                    var items = p.InventoryController?.Inventory?.Equipment?.GetAllItems();
                    if (items == null)
                        continue;
                    foreach (var it in items)
                    {
                        string tpl = it?.TemplateId.ToString();
                        if (tpl == ZombiesAmmo.RaygunId || tpl == ZombiesAmmo.ThundergunId)
                            held.Add(tpl);
                    }
                }
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Mystery box: couldn't check the team's wonder guns ({e.Message})");
            }
            return held;
        }

        // How likely a gun is to be the one the spin stops on, against 1 for an ordinary gun
        private const float WonderWeight = 1f; // every gun an equal chance (the user, 2026-11-07; was 0.5)

        private static float Weight(BoxGuns.Model m) => m.Gun.Role == "box_wonder" ? WonderWeight : 1f;

        /// <summary>The weapon templates the player has in their weapon slots (and in their hands).</summary>
        private static HashSet<string> CarriedTemplates()
        {
            var set = new HashSet<string>();
            if (!ZombiesPlugin.CurrentPlayer(out _, out Player player) || player.InventoryController == null)
                return set;
            foreach (EFT.InventoryLogic.EquipmentSlot slot in new[] { EFT.InventoryLogic.EquipmentSlot.FirstPrimaryWeapon,
                         EFT.InventoryLogic.EquipmentSlot.SecondPrimaryWeapon, EFT.InventoryLogic.EquipmentSlot.Holster })
                if (player.InventoryController.Inventory.Equipment.GetSlot(slot).ContainedItem is EFT.InventoryLogic.Weapon w)
                    set.Add(w.TemplateId.ToString());
            if (player.HandsController?.Item is EFT.InventoryLogic.Weapon held)
                set.Add(held.TemplateId.ToString());
            return set;
        }

        private BoxGuns.Model Pick(BoxGuns.Model not)
        {
            if (_models.Count == 0)
                return null;
            if (_models.Count == 1)
                return _models[0];
            BoxGuns.Model m;
            do
                m = _models[UnityEngine.Random.Range(0, _models.Count)];
            while (m == not);
            return m;
        }

        // The shown gun's root, from the pivot, in its own turned space: puts the middle of what's drawn on the pivot
        private Vector3 _shownOffset;

        private BoxGuns.Model Show(BoxGuns.Model model)
        {
            if (_shown?.Go != null)
                _shown.Go.SetActive(false);
            _shown = model;
            if (model?.Go != null && _weaponTag != null)
            {
                model.Go.SetActive(true);
                // Placed at the pivot, then moved so the centre of its drawn bounds (attachments included) sits there
                Quaternion turn = transform.rotation * model.Align;
                model.Go.transform.SetPositionAndRotation(_weaponTag.position, turn);
                Vector3 middle = BoxGuns.VisibleBounds(model.Go).center;
                _shownOffset = Quaternion.Inverse(turn) * (_weaponTag.position - middle);
                LateUpdate();
            }
            return model;
        }

        /// <summary>The gun's hit area: its drawn bounds, a little bigger, on the box (so the box's prompt sees it).</summary>
        private void FitGunHit()
        {
            if (_gunHit == null || _shown?.Go == null)
                return;
            Bounds b = BoxGuns.VisibleBounds(_shown.Go);
            Transform t = _gunHit.transform;
            t.position = b.center;
            t.rotation = Quaternion.identity;
            // The box is scaled: undo it, so the size is in metres
            Vector3 lossy = transform.lossyScale;
            t.localScale = new Vector3(1f / lossy.x, 1f / lossy.y, 1f / lossy.z);
            _gunHit.center = Vector3.zero;
            _gunHit.size = b.size * 1.3f + Vector3.one * 0.08f;
            _gunHit.enabled = true;
        }

        private static System.Reflection.MethodInfo _raygunPickup;

        private static void PlayRaygunPickup()
        {
            try
            {
                _raygunPickup = _raygunPickup ?? HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("Raygun.RaygunPickup"), "Play");
                _raygunPickup?.Invoke(null, null);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Mystery box: the Ray Gun pickup sound failed ({e.Message})");
            }
        }

        private void Take()
        {
            if (_state != State.Offering || Time.time < _takeFrom || !IsBuyer())
                return;
            BoxGuns.Gun gun = _shown?.Gun;
            ZombiesPlugin.Log.LogInfo($"Mystery box: took {gun?.Name}");
            if (ZombiesMatch.Active)
                ZombiesVox.BoxPickup(gun);
            // The Ray Gun mod's own pickup sound (the original's), as when it's picked up off the ground
            if (gun != null && BoxGuns.Template(gun) == "6a7e5a71d9a1160000000001")
                PlayRaygunPickup();
            // In a raid the gun goes to the hands; in the hideout (EFT won't hand items over there) to the stash
            if (gun != null && !ZombiesPlugin.InHideout && ZombiesPlugin.CurrentPlayer(out _, out Player player))
                ZombiesPlugin.Instance.StartCoroutine(BoxGive.Give(gun, player));
            else if (gun != null)
                BoxPay.TakeToStash(gun, ok =>
                {
                    if (ok)
                        ZombiesPlugin.Notify($"{gun.Name} is in your stash.");
                });
            ZombiesNet.BoxTaken(Spot);
            StartCoroutine(Close());
        }

        private void ShowFakeAt(int spot, bool on)
        {
            if (Match != null)
                Match.ShowFake(spot, on);
            else
                ZombiesPlugin.Instance.ShowFake(spot, on);
        }

        private IEnumerator Close()
        {
            _state = State.Closing;
            if (_gunHit != null)
                _gunHit.enabled = false;
            Show(null);
            Play(_weaponPivot, "down");
            Play(_box, "close");
            yield return new WaitForSeconds(LidClosedAt);
            Sound("Close");
            _lightFull = 0f;
            yield return new WaitForSeconds(0.6f - LidClosedAt);
            Play(_box, "idle");
            _usableFrom = Time.time + ReuseDelay;
            _state = State.Idle;
        }

        // ---------- The teddy: the box moves ----------

        private IEnumerator Leave()
        {
            _state = State.Leaving;
            Play(_weaponPivot, "down");
            // The original's order (the user, 2026-11-01): the teddy rises with Samantha's laugh while the box sits still; once
            // the bear is up and gone, the box rises spinning with the "bye bye", the whoosh and its vanishing sound;
            // then a cloud with lightning puffs over it and it's gone, leaving the old cases with the teddy on its blocks
            // The bear shows where the last gun was and sits a moment (it sinks in) before laughing and rising
            if (_teddy != null)
                _teddy.SetActive(true);
            Play(_teddyPivot, "down");
            yield return new WaitForSeconds(LaughAfterTeddy);
            Sound("Laugh");
            if (IsBuyer())
                ZombiesVox.Say("box_move", 1f, 5f);
            yield return new WaitForSeconds(RiseAfterLaugh);
            Play(_teddyPivot, "rise");
            yield return new WaitForSeconds(TeddyRise);
            if (_teddy != null)
                _teddy.SetActive(false);
            Play(_teddyPivot, "down");
            // A beat with the empty box open before it goes
            yield return new WaitForSeconds(LiftAfterTeddyGone);

            // The sound behind the laugh first, a beat, then the laugh and "bye bye" as the box lifts (the user 2026-11-01)
            Sound("Whoosh");
            Sound("Disappear");
            yield return new WaitForSeconds(LiftAfterSound);
            float rise = Time.time;
            Play(_box, "leave");
            Sound("ByeBye");

            _beam?.Show(false);
            float goneAt = Mathf.Max(rise + LeaveSeconds - 0.3f, rise + ClipLength("ByeBye"));
            if (_box != null)
                _box.speed = Mathf.Clamp((LeaveSeconds - 0.3f) / (goneAt - rise), 0.4f, 1f);
            // The original's box spin sound once it's up and spinning (a few seconds into the flight, the user 2026-11-01)
            bool spinning = false;
            while (Time.time < goneAt)
            {
                if (!spinning && Time.time >= rise + SpinSoundAfter)
                {
                    Sound("Spin");
                    spinning = true;
                }
                yield return null;
            }
            if (_box != null)
                _box.speed = 1f;
            // Where the box is now, up in the air: the cloud
            Transform body = transform.Find("tag_origin/tag_animate");
            // Just over the box itself (it was too high above it)
            BoxCloud.Puff(body != null ? body.position - Vector3.up * 0.1f : transform.position + Vector3.up * 1.2f, 1f);
            Sound("Poof");
            _lightFull = 0f;
            if (_teddy != null)
                _teddy.SetActive(false);
            Play(_teddyPivot, "down");
            SetVisible(false);
            _state = State.Away;
            if (!RaidTest)
                ShowFakeAt(Spot, true);
            yield return new WaitForSeconds(3f);

            // Lands at its other spot: the cases there vanish in a puff as it comes down
            int next = RaidTest ? Spot : _forcedNext >= 0 ? _forcedNext : Match != null ? Match.Other(Spot) : ZombiesPlugin.OtherSpot(Spot);
            _forcedNext = -1;
            Spot = next;
            (Vector3 position, Quaternion rotation) at = RaidTest ? (transform.position - Vector3.up * _standLift, transform.rotation) : Match != null ? Match.Place(next) : ZombiesPlugin.SpotPlace(next);
            transform.SetPositionAndRotation(RaidTest ? at.position + Vector3.up * _standLift : Seat(next, at), at.rotation);
            if (!RaidTest)
            {
                ShowFakeAt(next, false);
                BoxCloud.Puff(at.position + Vector3.up * 0.6f, 0.7f);
            }
            _moves++;
            _usesHere = 0;
            _state = State.Arriving;
            SetVisible(true);
            if (_gunHit != null)
                _gunHit.enabled = false;
            Play(_box, "arrive");
            // The spin sound again as it comes down into the new spot (the user, 2026-11-01)
            Sound("Spin");
            yield return new WaitForSeconds(ImpactAt);
            Sound("Land");
            _beam?.Show(true);
            yield return new WaitForSeconds(ArriveSeconds - ImpactAt);
            Play(_box, "idle");
            _state = State.Idle;
            ZombiesPlugin.Log.LogInfo($"Mystery box moved to spot {next + 1}.");
        }

        /// <summary>Comes down into its spot as after a move (the arrive anim, spin, land, beam): a Fire Sale's extra box.</summary>
        /// <summary>The box comes down here; speed above 1 plays the arrival faster (a fire sale box catching up).</summary>
        public void ArriveHere(float speed = 1f) => StartCoroutine(Arrive(Mathf.Max(0.1f, speed)));

        private IEnumerator Arrive(float speed)
        {
            _state = State.Arriving;
            BoxCloud.Puff(transform.position + Vector3.up * 0.6f, 0.7f);
            SetVisible(true);
            if (_gunHit != null)
                _gunHit.enabled = false;
            if (_box != null)
                _box.speed = speed;
            Play(_box, "arrive");
            Sound("Spin");
            yield return new WaitForSeconds(ImpactAt / speed);
            Sound("Land");
            _beam?.Show(true);
            yield return new WaitForSeconds((ArriveSeconds - ImpactAt) / speed);
            if (_box != null)
                _box.speed = 1f;
            Play(_box, "idle");
            _state = State.Idle;
        }

        private void SetVisible(bool on)
        {
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
                r.enabled = on;
            foreach (Collider c in GetComponentsInChildren<Collider>(true))
                c.enabled = on;
        }

        // ---------- Placement (debug) ----------

        /// <summary>Moved by the F12 placement tools: only while it stands still at a spot.</summary>
        public bool CanMove => _state == State.Idle;

        public void MoveTo(Vector3 position, Quaternion rotation) => transform.SetPositionAndRotation(position + Vector3.up * _standLift, rotation);

        /// <summary>Where the box's spot is (the stand's foot), from where the box is now.</summary>
        public Vector3 SpotPosition => transform.position - Vector3.up * _standLift;

        // ---------- Helpers ----------

        private static void Play(Animator animator, string state)
        {
            if (animator != null)
                animator.Play(state, 0, 0f);
        }

        private void Sound(string name)
        {
            if (_audio.TryGetValue(name, out AudioSource s) && s != null)
            {
                s.volume = (_baseVolume.TryGetValue(s, out float v) ? v : 0.8f) * ZombiesPlugin.BoxVolume(name);
                s.Play();
            }
        }

        private float ClipLength(string name) =>
            _audio.TryGetValue(name, out AudioSource s) && s != null && s.clip != null ? s.clip.length : 0f;

        private void Loop(string name, bool on)
        {
            if (!_audio.TryGetValue(name, out AudioSource s) || s == null)
                return;
            if (on && !s.isPlaying)
                s.Play();
            else if (!on)
                s.Stop();
        }
    }
}
