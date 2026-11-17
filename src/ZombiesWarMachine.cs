using System.Collections;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using Systems.Effects;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The War Machine (the Milkor M32A1 from the box) in a zombies match:
    /// - Pack-a-Punched: every shell goes off where it lands, however close (its arming time skipped), as the Sally.
    /// - Plain: it fires M67s on a 3 s fuse (M67 damage, ZombiesShells). The M67 leaves the muzzle (Fired), flies in a
    ///   lob, bounces off whatever it hits, rolls, blinks red faster as the fuse runs down, and goes off where it ends up
    ///   <see cref="FuseTime"/> after the shot, with EFT's own explosion (damage, points). The 40mm shell the game
    ///   fires with it does nothing anywhere (its hits are skipped).
    /// Hooked on ClientGameWorld.ShotDelegate (a projectile hitting something), before EFT checks the arming time.
    /// </summary>
    internal static class ZombiesWarMachine
    {
        private const string M32 = "6275303a9f372d6ea97f9ec7";
        // The plain War Machine's M67s: 3 s from the shot (user, 2026-11-13; was 4 s; hand grenades keep theirs)
        private const float FuseTime = 3f;
        // How much of its speed the grenade keeps bouncing off what it hit, and the fastest it leaves it
        private const float Bounce = 0.3f, MostBounceSpeed = 9f;

        private static int _depth, _depthFrame = -1;
        private static bool _inShot;
        private static float _armTime;
        private static object _template;

        // How fast the plain War Machine's M67 leaves the barrel (m/s): a lob, not a shell (user, 2026-11-15: "shoot M67s
        // which travel, roll, bounce and cook")
        private const float LaunchSpeed = 20f;

        public static void Patch(Harmony harmony)
        {
            foreach (var shot in ZombiesSally.ShotDelegates())
                harmony.Patch(shot, prefix: new HarmonyMethod(typeof(ZombiesWarMachine), nameof(Before)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(ZombiesWarMachine), nameof(After)) { priority = Priority.Last });
            // The shot itself: the plain War Machine's M67 leaves the muzzle here
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot)),
                postfix: new HarmonyMethod(typeof(ZombiesWarMachine), nameof(Fired)));
        }

        /// <summary>
        /// Your plain War Machine fired: an M67 leaves the muzzle (a real body: it flies, bounces, rolls) and goes off
        /// <see cref="FuseTime"/> after the shot. The game's own 40mm shell is let go and does nothing where it lands
        /// (<see cref="Before"/>). The other games get the same M67 (Msg.WarMachineM67) and fly their own copy; the
        /// damage is only ever the shooter's (ZombiesShells).
        /// </summary>
        private static void Fired(Player.FirearmController __instance, object[] __args)
        {
            try
            {
                if (!ZombiesMatch.Active || __instance == null)
                    return;
                Player me = Singleton<GameWorld>.Instance?.MainPlayer;
                Item weapon = __instance.Item;
                if (me == null || !ReferenceEquals(me.HandsController, __instance) || weapon?.TemplateId.ToString() != M32 || ZombiesPaP.Infused(weapon))
                    return;
                // InitiateShot(weapon, ammo, shotPosition, shotDirection, fireportPosition, chamberIndex, overheat)
                Item ammo = __args.Length > 1 ? __args[1] as Item : null;
                if (__args.Length < 4 || !(__args[2] is Vector3 from) || !(__args[3] is Vector3 dir) || ammo == null)
                    return;
                Vector3 start = from + dir.normalized * 0.4f;
                Vector3 velocity = dir.normalized * LaunchSpeed;
                Launch(start, velocity, me.ProfileId, ammo.TemplateId.ToString(), weapon, FuseTime);
                if (ZombiesNet.Online)
                    ZombiesNet.WarMachineM67(start, velocity, me.ProfileId, ammo.TemplateId.ToString());
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: the War Machine's M67 couldn't be fired ({e.Message})");
            }
        }

        /// <summary>Another player's plain War Machine fired: our own copy of their M67 (its blast does no damage here).</summary>
        public static void FiredRemote(Vector3 start, Vector3 velocity, string who, string ammoTpl)
        {
            try
            {
                if (!ZombiesMatch.Active)
                    return;
                Item weapon = Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), M32, null);
                Launch(start, velocity, who, ammoTpl, weapon, FuseTime);
            }
            catch (System.Exception e)
            {
                ZombiesDebug.Log($"war machine remote M67: {e.Message}");
            }
        }

        private static void Launch(Vector3 start, Vector3 velocity, string who, string ammoTpl, Item weapon, float seconds)
        {
            Item ammo = Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), ammoTpl, null);
            ExplosiveAmmoComponent boom = ammo?.GetItemComponent<ExplosiveAmmoComponent>();
            object template = boom == null ? null : Traverse.Create(boom).Property("Template").GetValue();
            string effect = template == null ? null : Traverse.Create(template).Field("ExplosionType").GetValue<string>();
            GameWorld world = Singleton<GameWorld>.Instance;
            var calc = world == null ? null : Traverse.Create(world).Property("SharedBallisticsCalculator").GetValue<IBallisticsCalculator>()
                       ?? Traverse.Create(world).Field("SharedBallisticsCalculator").GetValue<IBallisticsCalculator>();
            if (boom == null)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: the War Machine's M67 has no blast ({ammoTpl}): not fired");
                return;
            }
            ZombiesPlugin.Instance.StartCoroutine(Fuse(boom, start, velocity, who, calc, weapon, effect, seconds));
        }

        // Prefix: false skips the game's own handling of the hit (the plain War Machine's shell does nothing at all)
        private static bool Before(GameWorld __instance, Shot shotResult)
        {
            // A world calling its base ShotDelegate runs this twice: only the outer one counts (else the arming time
            // would be "restored" to the 0 set by the outer one and stay there)
            if (Time.frameCount != _depthFrame)
            {
                _depthFrame = Time.frameCount;
                _depth = 0;
            }
            if (_depth++ > 0)
                return true;
            _inShot = false;
            try
            {
                if (!ZombiesMatch.Active || shotResult?.Ammo == null || shotResult.Weapon?.TemplateId.ToString() != M32)
                    return true;
                // Plain: the M67 fired with it (Fired) is the War Machine's grenade; the shell itself never lands as anything
                if (!ZombiesPaP.Infused(shotResult.Weapon))
                    return false;
                ExplosiveAmmoComponent boom = shotResult.Ammo.GetItemComponent<ExplosiveAmmoComponent>();
                object template = boom == null ? null : Traverse.Create(boom).Property("Template").GetValue();
                if (template == null)
                    return true;
                Traverse t = Traverse.Create(template);
                _inShot = true;
                _template = template;
                _armTime = t.Field("FuzeArmTimeSec").GetValue<float>();
                // Papped: off on impact, however close
                t.Field("FuzeArmTimeSec").SetValue(0f);
            }
            catch (System.Exception e)
            {
                ZombiesDebug.Log($"war machine: {e.Message}");
            }
            return true;
        }

        private static void After()
        {
            if (_depth > 0 && --_depth > 0)
                return;
            if (!_inShot)
                return;
            _inShot = false;
            try
            {
                Traverse.Create(_template).Field("FuzeArmTimeSec").SetValue(_armTime);
            }
            catch
            {
            }
            _template = null;
        }

        // The M67 out of the barrel: it flies, bounces off what it hits and rolls (a real body, its own model), a red light
        // blinking faster as the fuse runs down, then the bang where it ended up (user, 2026-11-13 / 11-15: "fire M67s")
        // The flying M67: its size, what it hits (the map, not bodies), how much of a hit's speed it keeps
        private const float Radius = 0.035f, FlightBounce = 0.32f, Grip = 0.7f;
        private static readonly int HitMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain", "Default");

        private static IEnumerator Fuse(ExplosiveAmmoComponent boom, Vector3 at, Vector3 velocity, string who, IBallisticsCalculator calc,
            Item weapon, string effect, float seconds)
        {
            Vector3 normal = Vector3.up;
            var go = new GameObject("Zombies_WarMachineM67");
            go.transform.position = at;
            int layer = LayerMask.NameToLayer("Loot");
            go.layer = layer >= 0 ? layer : 0;
            // Moved here, not by a Rigidbody: the game doesn't step physics bodies we add, so the M67 hung where it was fired
            // and went off there (user, 2026-11-16: "they just float in the air"). Gravity, a sphere sweep for what it hits,
            // a bounce off it (a third of the speed into it), grip along it, and a roll to a stop
            Vector3 vel = velocity, spinAxis = Random.onUnitSphere;
            float spin = 720f;
            var lamp = new GameObject("Blink");
            lamp.transform.SetParent(go.transform, false);
            Light blink = lamp.AddComponent<Light>();
            blink.color = new Color(1f, 0.15f, 0.1f);
            blink.range = 1.5f;
            // The M67's own model (loads in a moment; the body flies meanwhile)
            Item m67 = Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), ZombiesGrenades.M67, null);
            if (m67 != null)
                ZombiesPlugin.Instance.StartCoroutine(BoxGuns.MakeItemModel(m67, model =>
                {
                    if (model == null)
                        return;
                    if (go == null)
                    {
                        Object.Destroy(model);
                        return;
                    }
                    // Only its look: its own body and colliders off (the sphere above is the grenade's body)
                    foreach (Rigidbody r in model.GetComponentsInChildren<Rigidbody>(true))
                        Object.Destroy(r);
                    foreach (Collider c in model.GetComponentsInChildren<Collider>(true))
                        Object.Destroy(c);
                    model.transform.SetParent(go.transform, false);
                    model.transform.localRotation = Quaternion.identity;
                    Bounds b = BoxGuns.VisibleBounds(model);
                    model.transform.position += go.transform.position - b.center;
                }));
            float end = Time.time + seconds;
            bool resting = false;
            while (Time.time < end && ZombiesMatch.Active && go != null)
            {
                float left = end - Time.time;
                blink.intensity = Mathf.Repeat(Time.time * Mathf.Lerp(8f, 2f, left / FuseTime), 1f) < 0.5f ? 1.2f : 0f;
                float dt = Mathf.Min(Time.deltaTime, 0.05f);
                if (!resting)
                {
                    vel += Physics.gravity * dt;
                    Vector3 move = vel * dt;
                    float dist = move.magnitude;
                    if (dist > 0f && Physics.SphereCast(at, Radius, move / dist, out RaycastHit hit, dist, HitMask, QueryTriggerInteraction.Ignore))
                    {
                        at = at + move / dist * hit.distance + hit.normal * 0.002f;
                        Vector3 into = Vector3.Project(vel, hit.normal), along = vel - into;
                        vel = -into * FlightBounce + along * Grip;
                        spin *= 0.6f;
                        // Slow on something it can lie on: it stops there
                        if (hit.normal.y > 0.6f && vel.magnitude < 0.6f)
                        {
                            resting = true;
                            vel = Vector3.zero;
                            normal = hit.normal;
                        }
                    }
                    else
                        at += move;
                    go.transform.position = at;
                    go.transform.rotation = Quaternion.AngleAxis(spin * dt, spinAxis) * go.transform.rotation;
                }
                yield return null;
            }
            if (go != null)
                Object.Destroy(go);
            if (!ZombiesMatch.Active)
                yield break;
            try
            {
                // The M67's own blast (the 40mm shell's effect name isn't one of the grenade effects: nothing showed)
                if (Singleton<Effects>.Instantiated)
                    Singleton<Effects>.Instance.EmitGrenade("Grenade_new", at, normal, 0);
                if (calc != null)
                    Grenade.Explosion(null, boom, at, who, calc, weapon, normal * 0.08f);
                ZombiesDebug.Log($"war machine: shell went off at {at} after its fuse");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: the War Machine shell's blast failed ({e.Message})");
            }
        }
    }
}
