using System.Collections.Generic;
using BepInEx.Configuration;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's damage, not EFT's: every zombie has a health pool (the original's: 150 at round 1, +100 a round to round 9, then 10%
    /// more each round) and a hit takes the original-style damage from it, wherever it lands, so a head doesn't just pop.
    /// - A gun's damage is its round's EFT damage times the zombies scale (0.3) times its class (pistols 1, SMGs 1.2,
    ///   rifles and machine guns 1.6, marksman and sniper rifles 3): the M1911 does about 20, so round 1 takes 7 shots
    ///   to the body and still stands for the knife (the original's 10 a hit + 130 for a knife kill).
    /// - Where it lands: head x3, neck/chest x1.2, belly and limbs x1 (aim for the head later, when they're tough).
    /// - The knife does 150 : a round 1 zombie in one stab, and it finishes off a shot-up one later.
    /// - Explosions: their EFT damage times the scale times 2.
    /// - Perks and power-ups ride on top (Double Tap, Deadshot's head bonus, Insta-Kill), as does Pack-a-Punch's own.
    /// While the pool lasts the hit does next to nothing to the EFT body (it still flinches and bleeds); the hit that
    /// empties it kills, where it landed (a headshot kill counts as one).
    /// The pools live on the host's game (where the zombies are); a client's perk bonuses are already in the damage
    /// it sends.
    /// </summary>
    internal static class ZombiesHealth
    {
        private static ConfigEntry<float> _scale, _head, _knife, _pistol, _smg, _rifle, _marksman, _shotgun;
        private static readonly Dictionary<Player, float> Pools = new Dictionary<Player, float>();
        private static ConfigEntry<float> _nadeNear, _nadeFar, _nadeRadius;

        /// <summary>the original's frag grenade (zombies have no shrapnel): damage by the blast, at the radius's edge, and the radius.</summary>
        // The original's grenades get stronger each round but stop one-hit killing after round ~4 (fan wikis, 2026-11-09): the close
        // damage grows by "Grenade damage per round" (300 + 50 a round: 450 at round 4, a round 4 zombie's health), the
        // edge in step with it
        // User, 2026-11-11: the M67 and the 40mm shells stronger for longer (400 + 100 a round: a zombie in the middle of
        // the blast dies to about round 15, ~85% of one at round 20; was 300 + 50, round ~4); PhD's blasts a bit weaker
        public static float GrenadeNear => (_nadeNear?.Value ?? 400f) + (_nadePerRound?.Value ?? 100f) * Mathf.Max(0, ZombiesRounds.Hard(ZombiesRounds.Round) - 1);
        public static float GrenadeFar => (_nadeFar?.Value ?? 100f) * GrenadeNear / Mathf.Max(1f, _nadeNear?.Value ?? 400f);
        // PhD Flopper's dive and knife blasts: the old grenade curve at 80% (240 at round 1, 600 at round 10), edge a quarter
        public static float PhdNear => 0.8f * (300f + 50f * Mathf.Max(0, ZombiesRounds.Hard(ZombiesRounds.Round) - 1));
        public static float PhdFar => PhdNear * 0.25f;
        public static float GrenadeRadius => _nadeRadius?.Value ?? 7f;

        /// <summary>The EFT explosion damage that comes out as this much the original damage here (explosions: EFT damage x scale x 2).</summary>
        public static float ExplosionEft(float bo) => bo / Mathf.Max(0.01f, (_scale?.Value ?? 0.3f) * 2f);

        public static void Bind(ConfigFile config)
        {
            const string s = "18. Zombie health and damage";
            ZombiesMeleeReach.Bind(config, s);
            ZombiesSwipe.Bind(config, s);
            ZombiesShotguns.Bind(config, s);
            ZombiesThrowArm.Bind(config, s);
            _scale = config.Bind(s, "Gun damage scale", 0.3f, new ConfigDescription(
                "EFT's bullet damage times this is a hit's zombies damage (0.3: the M1911 does about 20).", new AcceptableValueRange<float>(0.05f, 2f), Attr(99)));
            _head = config.Bind(s, "Headshot x", 3f, new ConfigDescription("Head bonus.", new AcceptableValueRange<float>(1f, 10f), Attr(98)));
            _knife = config.Bind(s, "Knife damage", 150f, new ConfigDescription("One stab at round 1.", new AcceptableValueRange<float>(10f, 2000f), Attr(97)));
            _nadeNear = config.Bind(s, "Grenade damage close", 400f, new ConfigDescription(
                "Frag grenade: damage to a zombie right by the blast (grenades throw no shrapnel in zombies; walls block the blast).",
                new AcceptableValueRange<float>(0f, 5000f), Attr(91)));
            _nadeFar = config.Bind(s, "Grenade damage at the edge", 100f, new ConfigDescription(
                "Damage to a zombie at the edge of the blast.", new AcceptableValueRange<float>(0f, 5000f), Attr(90)));
            _nadeRadius = config.Bind(s, "Grenade radius (m)", 7f, new ConfigDescription(
                "How far the blast reaches (the M67's own 7 m).", new AcceptableValueRange<float>(1f, 20f), Attr(89)));
            _nadePerRound = config.Bind(s, "Grenade damage per round", 100f, new ConfigDescription(
                "Added to the grenade's (and the monkey's) blast each round after the first.",
                new AcceptableValueRange<float>(0f, 500f), Attr(88)));
            ZombiesShells.Bind(config, s);
            // 0.925: the M1911's round (EFT 72) does 20, the original's pistol: 7 shots to the legs leave a round 1 zombie (150) up
            // for the knife (at 1 it did 21.6 and the 7th shot killed). New key so the old 1 isn't kept.
            _pistol = config.Bind(s, "Pistols x (M1911 20 a shot)", 0.925f, new ConfigDescription("Class bonus: 0.925 makes the M1911 do 20 a shot (7 leg shots then the knife on round 1).", new AcceptableValueRange<float>(0.1f, 10f), Attr(96)));
            _smg = config.Bind(s, "SMGs x", 1.2f, new ConfigDescription("Class bonus.", new AcceptableValueRange<float>(0.1f, 10f), Attr(95)));
            _rifle = config.Bind(s, "Rifles and MGs x", 1.6f, new ConfigDescription("Class bonus (assault rifles, carbines, machine guns).",
                new AcceptableValueRange<float>(0.1f, 10f), Attr(94)));
            _marksman = config.Bind(s, "Marksman and snipers x", 3f, new ConfigDescription("Class bonus.", new AcceptableValueRange<float>(0.1f, 10f), Attr(93)));
            _shotgun = config.Bind(s, "Shotguns x (per pellet)", 1f, new ConfigDescription("Class bonus, each pellet.", new AcceptableValueRange<float>(0.1f, 10f), Attr(92)));
            BindGuns(config, s);
        }

        private static ZombiesPlugin.ConfigurationManagerAttributes Attr(int order) => new ZombiesPlugin.ConfigurationManagerAttributes { Order = order };

        public static void Clear() => Pools.Clear();

        /// <summary>A zombie's full health this round (the original's curve, times F12's toughness).</summary>
        public static float Full => ZombiesRounds.BoHealth(ZombiesRounds.Round) * Mathf.Max(0.1f, ZombiesRounds.Toughness?.Value ?? 1f);

        /// <summary>
        /// A player's hit on a zombie, on the host: the original's damage off its pool; the EFT damage left on the hit is next to
        /// nothing, or enough to kill when the pool runs out.
        /// </summary>
        public static void Hit(Player zombie, ref DamageInfo hit)
        {
            if (zombie == null || !zombie.HealthController.IsAlive)
                return;
            if (!Pools.TryGetValue(zombie, out float pool))
                pool = Full;
            // Another player's bullet (Fika): the host's copy of their hit carries no gun (Fika fills it in for knives
            // only), so it fell to the weak "unknown gun" damage: a client's sniper hit less than the host's AR (user,
            // 2026-11-09). The gun in the shooter's hands here is the one that fired
            if (hit.Weapon == null && hit.DamageType == EDamageType.Bullet && hit.Player?.iPlayer is Player shooter
                && shooter.HandsController?.Item is Weapon held)
                hit.Weapon = held;
            pool -= BoDamage(hit);
            // The Thundergun's blast kills whatever it reaches, every round , and its kill throws the body
            bool thundergun = hit.Weapon != null && ZombiesAmmo.IsThundergun(hit.Weapon as Item);
            if (pool <= 0f || ZombiesPowerUps.InstaKill || thundergun)
            {
                Pools.Remove(zombie);
                hit.Damage = 100000f;
                return;
            }
            Pools[zombie] = pool;
            hit.Damage = 0.01f;
            ZombiesDebug.Log($"zombie hit: {pool:0} of {Full:0} left ({(hit.HittedBallisticCollider is BodyPartCollider c ? c.BodyPartType.ToString() : ZombiesRules.HitPart?.ToString() ?? "part unknown")}, {(hit.Weapon as Item)?.ShortName?.Localized() ?? "no gun"})");
        }

        /// <summary>the original's head bonus (Deadshot makes every bullet one).</summary>
        public static float HeadBonus => _head?.Value ?? 3f;

        private static float BoDamage(DamageInfo hit)
        {
            if (hit.DamageType == EDamageType.Melee)
                return _knife?.Value ?? 150f;
            float scale = _scale?.Value ?? 0.3f;
            // The Ray Gun (the original: ~1000 a direct hit, ~300 splash; Porter's X2 doubles both). Scaled from the cell's own
            // EFT numbers (bolt 115, blast 60 at its centre, less further out) so Double Tap's x2 and the blast's
            // fall-off still count; no head bonus (the original's wonder weapons have none). User, 2026-11-09: it was too weak
            if (hit.Weapon is Weapon ray && ray.TemplateId.ToString() == ZombiesAmmo.RaygunId)
            {
                float x2 = ZombiesPaP.Infused(ray) ? 2f : 1f;
                bool blast = hit.DamageType == EDamageType.Explosion || hit.DamageType == EDamageType.GrenadeFragment;
                return blast ? hit.Damage / 60f * (_raySplash?.Value ?? 300f) * x2 : hit.Damage / 115f * (_rayHit?.Value ?? 1000f) * x2;
            }
            if (hit.DamageType == EDamageType.Explosion || hit.DamageType == EDamageType.GrenadeFragment)
                return hit.Damage * scale * 2f;
            float damage = GunDamage(hit.Weapon as Weapon, hit.Damage, scale);
            // The part hit: the collider when there is one, else the body part EFT passed with the hit (a teammate's
            // shot on a Fika host carries no collider)
            EBodyPart? at = hit.HittedBallisticCollider is BodyPartCollider part ? part.BodyPartType : ZombiesRules.HitPart;
            if (at.HasValue)
                switch (at.Value)
                {
                    case EBodyPart.Head:
                        damage *= _head?.Value ?? 3f;
                        break;
                    case EBodyPart.Chest:
                        damage *= 1.2f;
                        break;
                }
            return damage;
        }

        // ---------- the original-style gun damage (user, 2026-11-09: "an SMG or AR on round 4 or 5 should take about 6 shots",
        // EFT's 5.7 did ~13 a hit: 33 shots at round 4) ----------
        // Each class has a the original body-shot damage for a typical round of that class (EFT damage "ref"); a gun does that times
        // its round's EFT damage over the ref (a stronger round in the class hits harder; Double Tap's x2 rides on top).
        // The original roughly: pistols 50-80, SMGs 75-110, ARs 110-150, LMGs 120-160, marksman ~200+, snipers 500+, shotguns per pellet.
        private const string M1911 = "5e81c3cbac2bb513793cdc75", M1A = "5aafa857e5b5b00018480968";
        private static ConfigEntry<float> _nadePerRound;
        private static ConfigEntry<float> _dmgPistol, _dmgSmg, _dmgRifle, _dmgLmg, _dmgMarksman, _dmgSniper, _dmgPellet, _dmgStarter, _dmgM14, _dmgDeagle, _dmgRsh, _rayHit, _raySplash;

        public static void BindGuns(ConfigFile config, string s)
        {
            ConfigDescription D(string text, float max, int order) => new ConfigDescription(text, new AcceptableValueRange<float>(1f, max), Attr(order));
            _dmgStarter = config.Bind(s, "Damage: starting M1911", 20f, D("A body shot from the starting pistol (20: 7 leg shots then the knife on round 1).", 500f, 88));
            _dmgM14 = config.Bind(s, "Damage: M1A", 110f, D("A body shot from the M1A, the early wall rifle: 2 body shots or 1 to the head at round 2.", 1000f, 87));
            _dmgPistol = config.Bind(s, "Damage: pistols", 65f, D("A body shot from a typical pistol round (9x19 / 5.7: ~60). A round 4 zombie has 450.", 1000f, 87));
            _dmgDeagle = config.Bind(s, "Damage: Desert Eagle", 220f, D("A body shot from any .50 AE Desert Eagle, whatever the round. Round 5 (550): 3 body shots or 1 to the head.", 2000f, 87));
            _dmgRsh = config.Bind(s, "Damage: RSh-12", 300f, D("A body shot from the RSh-12 revolver, whatever the round. Round 5 (550): 2 body shots or 1 to the head.", 2000f, 87));
            _rayHit = config.Bind(s, "Damage: Ray Gun hit", 1000f, D("A Ray Gun bolt hitting a zombie (about 1000: one shot to about round 9). Pack-a-Punched doubles it.", 5000f, 86));
            _raySplash = config.Bind(s, "Damage: Ray Gun splash", 300f, D("The Ray Gun blast at its centre (less further out). Pack-a-Punched doubles it.", 5000f, 86));
            _dmgSmg = config.Bind(s, "Damage: SMGs", 85f, D("A body shot from a typical SMG round (round 4: about 6 shots).", 1000f, 86));
            _dmgRifle = config.Bind(s, "Damage: rifles", 110f, D("A body shot from a typical assault rifle / carbine round (round 5: about 5 shots).", 1000f, 85));
            _dmgLmg = config.Bind(s, "Damage: machine guns", 130f, D("A body shot from a typical machine gun round.", 1000f, 84));
            _dmgMarksman = config.Bind(s, "Damage: marksman rifles", 220f, D("A body shot from a typical marksman rifle round.", 2000f, 83));
            _dmgSniper = config.Bind(s, "Damage: sniper rifles", 550f, D("A body shot from a typical sniper round.", 5000f, 82));
            _dmgPellet = config.Bind(s, "Damage: shotgun pellet", 40f, D("Each pellet (8 a shell: about 320 up close).", 1000f, 81));
        }

        private static float GunDamage(Weapon gun, float eftDamage, float oldScale)
        {
            if (gun != null && gun.TemplateId == M1911)
                return eftDamage / 72f * (_dmgStarter?.Value ?? 20f);
            // The M1A is the original's M14 (the early wall rifle, 8 a mag): not a marksman's 220 (it one-shot round 2 zombies,
            // user 2026-11-09). 110 a body shot (7.62 M80 = EFT 80): round 2 takes 2 body shots or 1 to the head
            if (gun != null && gun.TemplateId == M1A)
                return eftDamage / 80f * (_dmgM14?.Value ?? 110f);
            // Hand cannons (Desert Eagle .50 AE, RSh-12 12.7x55): a big hit, far over the Five-seveN / Glock's ~60
            // (user, 2026-11-09). Round 5 (550): Deagle 3 body / 1 head, RSh 2 body / 1 head. Scaled from a typical round
            // (.50 AE FMJ ~106, 12.7x55 PS12 ~115) so Double Tap's x2 on the EFT damage still counts
            if (HandCannon(gun, out bool rsh))
                return rsh ? eftDamage / 115f * (_dmgRsh?.Value ?? 300f) : eftDamage / 106f * (_dmgDeagle?.Value ?? 220f);
            float bo, eftRef;
            switch (gun?.WeapClass)
            {
                case "pistol": bo = _dmgPistol?.Value ?? 65f; eftRef = 55f; break;
                case "smg": bo = _dmgSmg?.Value ?? 85f; eftRef = 55f; break;
                case "assaultRifle":
                case "assaultCarbine": bo = _dmgRifle?.Value ?? 110f; eftRef = 55f; break;
                case "machinegun": bo = _dmgLmg?.Value ?? 130f; eftRef = 60f; break;
                case "marksmanRifle": bo = _dmgMarksman?.Value ?? 220f; eftRef = 70f; break;
                case "sniperRifle": bo = _dmgSniper?.Value ?? 550f; eftRef = 90f; break;
                case "shotgun": bo = _dmgPellet?.Value ?? 40f; eftRef = 40f; break;
                default:
                    // Wonder weapons and anything else: the old EFT-scaled damage
                    return eftDamage * oldScale * ClassBonus(gun);
            }
            return eftDamage / eftRef * bo;
        }

        private static bool HandCannon(Weapon gun, out bool rsh)
        {
            rsh = false;
            if (gun == null || gun.WeapClass != "pistol")
                return false;
            string cal;
            try
            {
                cal = gun.AmmoCaliber ?? "";
            }
            catch
            {
                cal = "";
            }
            rsh = cal.Contains("127x55");
            return rsh || cal.Contains("50AE");
        }

        private static float ClassBonus(Weapon gun)
        {
            switch (gun?.WeapClass)
            {
                case "pistol":
                    return _pistol?.Value ?? 0.925f;
                case "smg":
                    return _smg?.Value ?? 1.2f;
                case "assaultRifle":
                case "assaultCarbine":
                case "machinegun":
                    return _rifle?.Value ?? 1.6f;
                case "marksmanRifle":
                case "sniperRifle":
                    return _marksman?.Value ?? 3f;
                case "shotgun":
                    return _shotgun?.Value ?? 1f;
                default:
                    return 1f;
            }
        }
    }
}
