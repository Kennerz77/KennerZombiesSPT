using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's Pack-a-Punch in a zombies match, on the PaP mod's machine model: with the power on, hold a gun that isn't
    /// upgraded and buy it (5000). The gun goes into the machine (out of your hands and slot) the way the PaP mod's own
    /// machine does it: the sting and crunch, the flag up, the gun riding in on the take anim, the rollers spinning up
    /// while it works, then out again wearing the camo on the eject anim, creeping back in: take it within 15 s or
    /// it's gone, as in the original. It comes
    /// back 115 infused for the match (the PaP mod's own look, glow and bonuses; never kept after the match: the
    /// character is put back) with a full reserve. Guns only: grenades and the rest can't go in.
    /// </summary>
    internal class ZombiesPaP : MonoBehaviour
    {
        public const int Price = 5000;
        private const float InteractDistance = 2.4f, Churn = 4f, TakeWithin = 15f;
        private const float JingleMin = 40f, JingleMax = 110f, HearJingleWithin = 25f;
        private readonly ZombiesInteraction _menu = new ZombiesInteraction();
        private AudioSource _jingle, _loop, _sting, _crunch;
        private float _idleVolume = -1f;
        private float _nextJingle;

        private enum State { Idle, Churning, Ready }
        private State _state;

        // ---------- Shared between the players (user, 2026-11-09) ----------
        // Everyone sees a player's gun go in, churn and come out; while it's theirs nobody else can buy or take, and
        // everyone (the host too) knows which guns are Pack-a-Punched (camo, sounds, tracers, the Ray Gun's x2).
        internal static readonly Dictionary<string, ZombiesPaP> Machines = new Dictionary<string, ZombiesPaP>();
        private string _remoteOwner;   // another player's turn at this machine (their profile id)
        private string _remoteName;
        private bool _watching;        // this game only shows their churn (no build, no infuse, no take)

        private string Key => $"{Mathf.RoundToInt(transform.position.x)},{Mathf.RoundToInt(transform.position.z)}";
        private Weapon _gun;
        private EquipmentSlot _from;
        private float _readyUntil;

        private Animator _machine, _flag;
        private Transform[] _rollers = new Transform[0];
        private Quaternion[] _rollerRest = new Quaternion[0];
        private float _spin, _spinTarget, _spinAngle;
        private object _display;

        // The PaP mod's machine sequence (its hideout machine's states and the gun's path, in the model's own space)
        private const string IdleState = "p_zm_pap_idle", TakeGunState = "p_zm_pap_take_gun", WorkingState = "p_zm_pap_working", EjectGunState = "p_zm_pap_eject_gun";
        private static readonly Vector3 GunFront = new Vector3(0f, 2.15f, 2.0f), GunInside = new Vector3(0f, 2.2f, 0.0f),
            GunOut = new Vector3(0f, 2.15f, 0.9f), GunGone = new Vector3(0f, 2.2f, -0.7f);
        private const float IdleSpin = 60f, WorkingSpin = 780f;

        /// <summary>Another player's turn (ZombiesNet): 0 their gun goes in (its item), 1 they took it or it's gone.</summary>
        internal static void Remote(string key, byte kind, string from, Item gun)
        {
            if (!Machines.TryGetValue(key, out ZombiesPaP m) || m == null)
                return;
            if (kind == 0 && gun is Weapon w)
            {
                if (m._state != State.Idle)
                    m.StopAllCoroutines();
                m._remoteOwner = from;
                m._remoteName = ZombiesNet.PlayerName(from);
                m._watching = true;
                m._gun = w;
                ZombiesPlugin.Log.LogInfo($"Zombies PaP: {m._remoteName}'s {w.LocalizedName()} goes in");
                m.StartCoroutine(m.ChurnIt());
            }
            else if (kind == 1 && m._watching)
                m.RemoteDone();
        }

        private void RemoteDone()
        {
            StopAllCoroutines();
            _gun = null;
            _remoteOwner = null;
            _watching = false;
            _state = State.Idle;
            DropDisplay();
            Play(_flag, "flag_down");
            Play(_machine, IdleState);
            _spinTarget = IdleSpin;
        }

        private void Start()
        {
            Machines[Key] = this;
            Animator[] animators = GetComponentsInChildren<Animator>(true);
            _flag = animators.FirstOrDefault(a => a.gameObject.name == "pap_flag");
            _machine = animators.FirstOrDefault(a => a != _flag);
            Transform[] all = GetComponentsInChildren<Transform>(true);
            _rollers = new[] { "j_rollers_large", "j_rollers_small" }.Select(n => all.FirstOrDefault(t => t.name == n)).Where(t => t != null).ToArray();
            _rollerRest = _rollers.Select(r => r.localRotation).ToArray();
            _spinTarget = IdleSpin;
            _jingle = Find("Audio_Jingle", null, false);
            _loop = Find("Audio_ProcessLoop", null, true);
            _sting = Find("Audio_Sting", "pap_sting", false);
            _crunch = Find("Audio_Crunch", "pap_crunch", false);
            _nextJingle = Time.time + UnityEngine.Random.Range(15f, JingleMin);
        }

        // The model's own sources (muted when it was placed), with the PaP bundle's clip by name if it had none
        private AudioSource Find(string name, string clip, bool loop)
        {
            AudioSource s = GetComponentsInChildren<AudioSource>(true).FirstOrDefault(a => a.name == name);
            if (s == null)
                return null;
            if (s.clip == null && clip != null)
                s.clip = PaPClip(clip);
            s.loop = loop;
            s.playOnAwake = false;
            s.enabled = true;
            // The jingle carried right across the map with the prefab's own range (user, 2026-11-09): the machines' range
            if (name == "Audio_Jingle")
            {
                s.spatialBlend = 1f;
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = 1f;
                s.maxDistance = ZombiesMachineAudio.JingleRange;
            }
            _baseVolume[s] = s.volume;
            return s;
        }

        // The prefab's own volumes, times F12 "2. Zombies sounds" > Pack-a-Punch (set every frame: the slider works live)
        private readonly System.Collections.Generic.Dictionary<AudioSource, float> _baseVolume = new System.Collections.Generic.Dictionary<AudioSource, float>();

        private void LevelSounds()
        {
            float v = ZombiesSounds.Volume(ZombiesSounds.Group.PackAPunch);
            foreach (var kv in _baseVolume)
                if (kv.Key != null)
                    kv.Key.volume = kv.Value * v;
        }

        private static AudioClip PaPClip(string name)
        {
            try
            {
                return AccessTools.TypeByName("PaPHideoutModule.PaPAssets")?.GetMethod("Clip", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { name }) as AudioClip;
            }
            catch
            {
                return null;
            }
        }

        private void Update()
        {
            LevelSounds();
            if (!ZombiesMatch.Active)
                return;
            if (ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out Player player))
                _menu.Tick(owner, ((Component)player).transform, transform, InteractDistance, Options);
            // Not taken in time: gone 
            if (_state == State.Ready && Time.time > _readyUntil)
            {
                ZombiesPlugin.Log.LogInfo($"Zombies PaP: {_gun?.LocalizedName()} not taken, gone");
                if (_watching)
                {
                    RemoteDone();
                    return;
                }
                ZombiesNet.PaPUse(Key, 1, null);
                _gun = null;
                _state = State.Idle;
                DropDisplay();
                Play(_flag, "flag_down");
                Play(_machine, IdleState);
            }
            // Ready: the gun creeping back into the machine
            else if (_state == State.Ready)
                DisplayAt(Vector3.Lerp(GunOut, GunGone, 1f - (_readyUntil - Time.time) / TakeWithin), Mathf.Sin(Time.time * 2f) * 0.02f);
            // Its jingle now and then while powered, when someone's near
            if (ZombiesMatch.PowerIsOn && _state == State.Idle && _jingle != null && _jingle.clip != null && !_jingle.isPlaying && Time.time >= _nextJingle)
            {
                _nextJingle = Time.time + UnityEngine.Random.Range(JingleMin, JingleMax);
                if (player != null && Vector3.Distance(((Component)player).transform.position, transform.position) < HearJingleWithin)
                    MachineJingles.TryPlay(_jingle);
            }
        }

        private void OnDestroy()
        {
            if (Machines.TryGetValue(Key, out ZombiesPaP m) && m == this)
                Machines.Remove(Key);
            _menu.Reset();
            DropDisplay();
        }

        // The rollers turning on top of the anims (after the Animator has posed them)
        private void LateUpdate()
        {
            if (_rollers.Length == 0)
                return;
            float target = ZombiesMatch.PowerIsOn ? _spinTarget : 0f;
            _spin = Mathf.MoveTowards(_spin, target, 720f * Time.deltaTime);
            if (_spin == 0f)
                return;
            _spinAngle = Mathf.Repeat(_spinAngle + _spin * Time.deltaTime, 360f);
            for (int i = 0; i < _rollers.Length; i++)
                _rollers[i].localRotation = _rollerRest[i] * Quaternion.AngleAxis(i == 0 ? _spinAngle : -_spinAngle * 1.6f, Vector3.right);
        }

        private static void Play(Animator a, string state)
        {
            if (a != null && a.HasState(0, Animator.StringToHash(state)))
                a.Play(state, 0, 0f);
        }

        private float Length(string clip)
        {
            AnimationClip c = _machine?.runtimeAnimatorController?.animationClips.FirstOrDefault(x => x.name == clip);
            return c != null ? c.length : 1f;
        }

        // ---------- The gun riding through (the PaP mod's PaPDisplayGun, by reflection: it is internal to that mod) ----------

        private static Type _displayType;

        private IEnumerator MakeDisplay(Weapon gun)
        {
            _displayType = _displayType ?? AccessTools.TypeByName("PaPHideoutModule.PaPDisplayGun");
            Transform model = _machine != null ? _machine.transform : transform;
            if (_displayType == null)
                yield break;
            try
            {
                _display = AccessTools.Constructor(_displayType, new[] { typeof(Transform) })?.Invoke(new object[] { model });
                if (_display == null)
                    throw new Exception("no PaPDisplayGun(Transform)");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies PaP: no gun in the machine ({e.Message})");
                _display = null;
                yield break;
            }
            yield return (IEnumerator)AccessTools.Method(_displayType, "Load").Invoke(_display, new object[] { gun });
        }

        private void DisplayAt(Vector3 local, float bob = 0f) => Call("SetLocalPosition", local, bob);

        private void Call(string method, params object[] args)
        {
            if (_display == null)
                return;
            try
            {
                AccessTools.Method(_displayType, method).Invoke(_display, args);
            }
            catch
            {
            }
        }

        private void DropDisplay()
        {
            Call("Dispose");
            _display = null;
        }

        private IEnumerator Ride(Vector3 from, Vector3 to, float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                DisplayAt(Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t / seconds)));
                yield return null;
            }
            DisplayAt(to);
        }

        private IEnumerable<(string, Action)> Options()
        {
            if (!ZombiesMatch.PowerIsOn)
            {
                yield return ("Pack-a-Punch (needs the power)", null);
                yield break;
            }
            if (_watching)
            {
                yield return ($"Pack-a-Punch ({_remoteName ?? "a teammate"}'s turn)", null);
                yield break;
            }
            if (_state == State.Churning)
                yield break;
            if (_state == State.Ready)
            {
                yield return ($"Take {_gun.LocalizedName()}", Take);
                yield break;
            }
            Player me = ZombiesPlugin.CurrentPlayer(out _, out Player p) ? p : null;
            if (!(me?.HandsController?.Item is Weapon held) || ZombiesDowned.Mine != ZombiesDowned.State.Up)
                yield return ("Pack-a-Punch (hold a gun)", null);
            else if (Infused(held))
                yield return ("Pack-a-Punch (already upgraded)", null);
            else
                yield return ($"Pack-a-Punch {held.LocalizedName()} ({Price})", ZombiesMatch.Afford(Price, () => Buy(me, held)));
        }

        private void Buy(Player me, Weapon held)
        {
            if (_state != State.Idle || !ZombiesMatch.Spend(Price))
                return;
            _state = State.Churning;
            StartCoroutine(HandOver(me, held));
        }

        // As in the hideout: the gun leaves the hands (arms out, empty), the "All right" gesture and its voice line, and
        // the gun goes into the machine; you stand and watch it work
        private IEnumerator HandOver(Player me, Weapon held)
        {
            // Never under a running reload (ZombiesHands)
            yield return ZombiesHands.Settle(me, "Pack-a-Punch");
            me.SetEmptyHands(_ => { });
            float until = Time.time + 3f;
            while (Time.time < until && !(me.HandsController is Player.EmptyHandsController))
                yield return null;
            if (ZombiesPlugin.CurrentPlayer(out GamePlayerOwner owner, out _))
                try
                {
                    owner.PlayPhraseOrGesture(203, false); // EInteraction "All right"
                    owner.PlayPhraseOrGesture((int)EPhraseTrigger.GoodWork, false);
                }
                catch (Exception e)
                {
                    ZombiesDebug.Log($"PaP gesture failed: {e.Message}");
                }
            EquipmentSlot? from = SlotOf(me, held);
            ZombiesNet.PaPUse(Key, 0, held);
            var removed = ItemManipulator.Remove(held, me.InventoryController, false, true);
            if (!removed.Succeeded)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies PaP: couldn't take the {held.LocalizedName()}: {removed.Error}");
                ZombiesNet.PaPUse(Key, 1, null);
                ZombiesMatch.AddPoints(Price);
                _state = State.Idle;
                ZombiesPerks.Rearm(me);
                yield break;
            }
            ZombiesItems.Removed(me.InventoryController, removed, held);
            _gun = held;
            _from = from ?? EquipmentSlot.FirstPrimaryWeapon;
            ZombiesPlugin.Log.LogInfo($"Zombies PaP: {held.LocalizedName()} in");
            ZombiesVox.Say("pap_wait", 0.6f, 10f);
            yield return ChurnIt();
        }

        // The PaP mod's sequence: sting and crunch, flag up, the gun in on the take anim, working (rollers fast, the
        // loop), the gun gone; then out wearing the camo on the eject anim, timed to the sting's ding
        private IEnumerator ChurnIt()
        {
            _state = State.Churning;
            _jingle?.Stop();
            float start = Time.time;
            Play(_flag, "flag_up");
            _sting?.Play();
            _crunch?.Play();
            yield return MakeDisplay(_gun);
            DisplayAt(GunFront);
            float take = Mathf.Max(1f, Length(TakeGunState)), eject = Mathf.Max(1f, Length(EjectGunState));
            Play(_machine, TakeGunState);
            yield return Ride(GunFront, GunInside, take);
            float work = Mathf.Max(0.5f, Churn - (Time.time - start) - eject);
            Play(_machine, WorkingState);
            _spinTarget = WorkingSpin;
            // The rollers' loop runs all the time once powered (idle, ZombiesPower): louder while working
            _idleVolume = _loop != null && _loop.isPlaying ? _loop.volume : -1f;
            if (_loop != null)
            {
                if (_idleVolume > 0f)
                    _loop.volume = _idleVolume * 1.5f;
                if (!_loop.isPlaying)
                    _loop.Play();
            }
            for (float t = 0f; t < work * 0.5f; t += Time.deltaTime)
            {
                DisplayAt(GunInside, Mathf.Sin(t * 4f) * 0.04f);
                yield return null;
            }
            Call("SetVisible", false);
            // Inside the machine: the gun becomes its Pack-a-Punch build (new attachments), shown as it comes out
            float upgradeStarted = Time.time;
            // A teammate's gun too, for the display only (their game makes the real one): the PaP'd M16 came out of
            // the machine here with its old grip, not the M203 (user, 2026-11-09)
            yield return Upgrade();
            float rest = work * 0.5f - (Time.time - upgradeStarted);
            if (rest > 0f)
                yield return new WaitForSeconds(rest);
            // Back to the idle rollers (it was idling before), else stopped as before
            if (_loop != null)
            {
                if (_idleVolume > 0f)
                    _loop.volume = _idleVolume;
                else
                    _loop.Stop();
            }
            _spinTarget = IdleSpin;
            // Infused now, so the gun comes out wearing it (a teammate's: shown wearing it here, the real mark comes
            // from their game)
            if (!_watching)
                Infuse(_gun);
            else
                MarkHere(_gun);
            if (_upgraded)
            {
                DropDisplay();
                yield return MakeDisplay(_gun);
                DisplayAt(GunInside);
            }
            Call("ShowCamo", _gun, true);
            Call("SetVisible", true);
            Play(_machine, EjectGunState);
            yield return Ride(GunInside, GunOut, eject);
            Play(_flag, "flag_down");
            _readyUntil = Time.time + TakeWithin;
            _state = State.Ready;
        }

        private void Take()
        {
            if (_state != State.Ready || _gun == null || !ZombiesPlugin.CurrentPlayer(out _, out Player me))
                return;
            _state = State.Churning; // no second take while its new build loads
            StartCoroutine(TakeUpgraded(me));
        }

        private bool _upgraded;

        // The gun's Pack-a-Punch build from the gun table (box_builds.json "pap": new attachments), made fresh with its
        // models loaded, replacing _gun while it's inside the machine; a gun with none (another mod's) stays as it is
        private IEnumerator Upgrade()
        {
            _upgraded = false;
            Weapon gun = _gun;
            BoxGuns.Gun table = _watching ? ZombiesGunTable.Guess(gun) : ZombiesGunTable.Of(gun);
            if (table?.Pap != null && table.Pap["tpl"] != null && !ZombiesAmmo.IsWonderWeapon(gun))
            {
                Weapon built = null;
                Magazine mag = null;
                try
                {
                    if (BoxGuns.BuildItem(table.Pap, out Newtonsoft.Json.Linq.JObject magPart) is Weapon w)
                    {
                        built = w;
                        mag = BoxGive.MakeMagazine(w, magPart);
                    }
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies PaP: the {table.Name}'s upgraded build failed ({e.Message}); the gun comes back as it was");
                }
                if (built != null)
                {
                    yield return ZombiesItems.LoadModels(built, mag);
                    if (mag != null)
                    {
                        built.GetMagazineSlot()?.AddWithoutRestrictions(mag);
                        ZombiesReload.Chamber(built, mag);
                    }
                    else
                        ZombiesAmmo.Ready(built);
                    ZombiesGunTable.Remember(built, table);
                    ZombiesPlugin.Log.LogInfo($"Zombies PaP: the {table.Name} comes out as its upgraded build");
                    _gun = built;
                    _upgraded = true;
                }
            }
        }

        private IEnumerator TakeUpgraded(Player me)
        {
            _state = State.Ready;
            yield return GiveBack(me, _gun);
        }

        private IEnumerator GiveBack(Player me, Weapon gun)
        {
            InventoryController inventory = me.InventoryController;
            // Back where it was; that slot filled since (the box): another gun slot, else the gun in the hands goes
            Slot slot = inventory.Inventory.Equipment.GetSlot(_from);
            if (slot.ContainedItem != null)
                slot = new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon }
                    .Select(s => inventory.Inventory.Equipment.GetSlot(s)).FirstOrDefault(s => s.ContainedItem == null);
            if (slot == null)
            {
                Item inHands = me.HandsController?.Item;
                slot = new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon }
                    .Select(s => inventory.Inventory.Equipment.GetSlot(s)).FirstOrDefault(s => s.ContainedItem == inHands)
                    ?? inventory.Inventory.Equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon);
                Item replaced = slot.ContainedItem;
                var removed = ItemManipulator.Remove(replaced, inventory, false, true);
                ZombiesItems.Removed(inventory, removed, replaced);
            }
            Infuse(gun);
            DropDisplay();
            Play(_machine, IdleState);
            if (!ZombiesItems.AddToSlot(inventory, gun, slot, out string error))
            {
                ZombiesPlugin.Log.LogWarning($"Zombies PaP: couldn't give the {gun.LocalizedName()} back: {error}");
                yield break;
            }
            _gun = null;
            _state = State.Idle;
            ZombiesNet.PaPUse(Key, 1, null);
            // Its mags half as big again, full, and the bigger stock behind them
            ZombiesAmmo.GrowPaPMags(me);
            ZombiesAmmo.TopUpMag(gun);
            ZombiesAmmo.FillGun(gun);
            // Its underbarrel launcher (the M16's M203) loaded, with its spare rounds
            StartCoroutine(ZombiesUbgl.Fill(me, gun, "Pack-a-Punch"));
            ZombiesVox.Say("pap", 1f, 5f);
            // Cocked as it comes out (as a box gun): its mag taken out and the chamber emptied, then EFT's reload from
            // empty puts it back and racks it once it's in the hands
            Magazine mag = gun.GetCurrentMagazine();
            bool rack = BoxGive.RackOnDraw && mag != null && !ZombiesAmmo.IsWonderWeapon(gun) && !(gun is Revolver) && !(mag is CylinderMagazine);
            // Rack only (F12 "Cock with the mag already in"): loaded as it is, EFT's misfire clear racks it in the hands
            bool clear = rack && BoxGive.RackOnly && gun.Chambers.Length > 0 && !(gun is Revolver);
            if (clear)
                rack = false;
            // No chamber (open-bolt belt-feds: the RPD, M60): nothing to rack, the box stays in (user, 2026-11-10: the
            // PaP'd RPD came out without its box, needing a reload, and the other player never saw the box on it)
            if (gun.Chambers.Length == 0)
                rack = false;
            if (rack)
            {
                gun.GetMagazineSlot()?.RemoveItemWithoutRestrictions();
                foreach (Slot chamber in gun.Chambers)
                    if (chamber.ContainedItem != null)
                        chamber.RemoveItemWithoutRestrictions();
            }
            // A Fika headless host refuses drawing a gun it hasn't got yet
            yield return ZombiesNet.UntilHostHas(gun, "Pack-a-Punched gun");
            ZombiesAmmo.BootUpIfRaygun(gun);
            me.SetInHands(gun, result =>
            {
                if (rack && !result.Failed)
                    ZombiesPlugin.Instance.StartCoroutine(BoxGive.Rack(me, gun, mag, gun.LocalizedName()));
                else if (clear && !result.Failed)
                    ZombiesPlugin.Instance.StartCoroutine(BoxGive.RackOnlyClear(me, gun, gun.LocalizedName()));
            });
            ZombiesPlugin.Log.LogInfo($"Zombies PaP: {gun.LocalizedName()} upgraded, in {slot.ID}");
        }

        private static EquipmentSlot? SlotOf(Player me, Item item)
        {
            foreach (EquipmentSlot s in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
                if (me.InventoryController.Inventory.Equipment.GetSlot(s).ContainedItem == item)
                    return s;
            return null;
        }

        // ---------- The PaP mod ----------

        private static MethodInfo _isInfused, _markForRaid;
        private static bool _looked;

        private static void Look()
        {
            if (_looked)
                return;
            _looked = true;
            _isInfused = AccessTools.TypeByName("PaPHideoutModule.PaPApi")?.GetMethod("IsInfused", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Item) }, null);
            // Infused for this raid only (the way the PaP mod marks an enemy's 115 gun); never reaches the profile
            _markForRaid = AccessTools.Method(AccessTools.TypeByName("PaPHideoutModule.PaPRegistry"), "MarkEnemyGun");
        }

        /// <summary>A machine is working on a gun of ours right now (hands are empty on purpose meanwhile).</summary>
        public static bool Holding(string gunId)
        {
            foreach (ZombiesPaP m in Machines.Values)
                if (m != null && !m._watching && m._state != State.Idle)
                    return true;
            return false;
        }

        public static bool Infused(Item gun)
        {
            Look();
            try
            {
                return _isInfused != null && (bool)_isInfused.Invoke(null, new object[] { gun });
            }
            catch
            {
                return false;
            }
        }

        public static void Infuse(Weapon gun)
        {
            Look();
            try
            {
                _markForRaid?.Invoke(null, new object[] { gun.Id.ToString() });
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies PaP: couldn't infuse ({e.Message})");
            }
            // Every other game (the host too): its camo, sounds and tracers there, the Ray Gun's x2 on the host
            ZombiesNet.PaPInfused(gun.Id.ToString());
        }

        // A teammate's gun shown coming out of the machine: marked on this game only, so the display wears the camo
        // (their real upgraded build has its own id and comes marked from their game)
        private static void MarkHere(Weapon gun)
        {
            Look();
            try
            {
                _markForRaid?.Invoke(null, new object[] { gun.Id.ToString() });
            }
            catch
            {
            }
        }

        /// <summary>Another player's gun was Pack-a-Punched (ZombiesNet): marked here too.</summary>
        internal static void InfuseRemote(string id)
        {
            Look();
            try
            {
                _markForRaid?.Invoke(null, new object[] { id });
                ZombiesPlugin.Log.LogInfo($"Zombies PaP: a teammate's gun {id} is Pack-a-Punched here too");
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies PaP: couldn't mark a teammate's gun ({e.InnerException?.Message ?? e.Message})");
            }
        }
    }
}
