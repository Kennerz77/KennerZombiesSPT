using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's blast for 40mm shells in a zombies match (War Machine, Sally from the PaP'd M1911, the M203 on the PaP'd M16).
    /// EFT's own M381 explosion is weak (strength 70 + 10 fragments: ~40 the original damage), nothing like the original (user, 2026-11-09:
    /// "are explosives akin to the original's?"). Each shell your game fires now hits every zombie in reach and in sight with the original
    /// damage, full close in, less to the edge (the original zombies' War Machine: 450-125, Pack-a-Punched 900-125), as EFT
    /// Explosion damage from you so points, kills and PHD work like any shot; EFT's own shell damage on zombies is dropped
    /// (<see cref="DropEft"/>). Your own blast near you is still the PHD rule (ZombiesRules -> ZombiesPhd.SelfBlast).
    /// </summary>
    internal static class ZombiesShells
    {
        private const string M381 = "5ede474b0c226a66f5402622";
        private const string M32 = "6275303a9f372d6ea97f9ec7";
        private const string M1911 = "5e81c3cbac2bb513793cdc75";
        private static readonly string[] M203 = { "6357c98711fb55120211f7e1", "639c3fbbd0446708ee622ee9" };

        private static ConfigEntry<float> _wmNear, _wmPapNear, _ubglNear, _sallyNear, _far, _raySplash, _rayReach;
        private static bool _applying;

        public static void Bind(ConfigFile config, string s)
        {
            ConfigDescription D(string text, int order) => new ConfigDescription(text, new AcceptableValueRange<float>(0f, 5000f),
                new ZombiesPlugin.ConfigurationManagerAttributes { Order = order });
            _wmNear = config.Bind(s, "War Machine blast close", 450f, D("The War Machine: damage right by the blast (fades to the edge value).", 80));
            _wmPapNear = config.Bind(s, "War Machine (PaP) blast close", 900f, D("Pack-a-Punched War Machine.", 79));
            _ubglNear = config.Bind(s, "M203 (PaP M16) blast close", 600f, D("The Pack-a-Punched M16's grenade launcher.", 78));
            _sallyNear = config.Bind(s, "Sally (PaP M1911) blast close", 900f, D("Mustang & Sally's shell (its blast is smaller, 60% of the reach).", 77));
            _rayReach = config.Bind(s, "Ray Gun splash reach (m)", 4.5f, new ConfigDescription("How far the Ray Gun's splash reaches (the cell's own blast is 2.5 m; past it zombies take the outer splash, fading out).",
                new AcceptableValueRange<float>(2.5f, 10f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 75 }));
            _raySplash = config.Bind(s, "Ray Gun outer splash", 300f, D("The Ray Gun's splash just past the cell's own blast (60% of this there, fading to a third at the reach). Pack-a-Punched doubles it.", 74));
            _far = config.Bind(s, "Shell blast at the edge", 125f, D("Every 40mm shell's damage at the edge of its blast.", 76));
        }

        public static void Patch(Harmony harmony)
        {
            var m = AccessTools.GetDeclaredMethods(typeof(Grenade)).FirstOrDefault(x => x.Name == nameof(Grenade.Explosion) && x.IsStatic
                && x.GetParameters().Any(p => p.Name == "originalWeaponItem"));
            if (m == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies shells: EFT's explosion method wasn't found; shells keep EFT's damage");
                return;
            }
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(ZombiesShells), nameof(Blast)));
        }

        /// <summary>A gun whose explosion hits are shell hits (EFT's own are dropped in a zombies match: ours land instead).</summary>
        public static bool IsShellGun(Item gun)
        {
            if (gun == null)
                return false;
            string id = gun.TemplateId.ToString();
            return id == M32 || id == M1911 || M203.Contains(id) || gun is Weapon w && w.WeapClass == "grenadeLauncher";
        }

        /// <summary>EFT's own explosion damage from a shell gun on a zombie: none (our the original blast is the shell's damage).</summary>
        public static bool DropEft(DamageInfo hit) => !_applying && ZombiesMatch.Active
            && (hit.DamageType == EDamageType.Explosion || hit.DamageType == EDamageType.GrenadeFragment) && IsShellGun(hit.Weapon);

        private static void Blast(IExplosiveItem grenadeItem, Vector3 grenadePosition, string playerProfileIDWhoThrew, Item originalWeaponItem)
        {
            try
            {
                if (!ZombiesMatch.Active || originalWeaponItem == null)
                    return;
                Item shell = grenadeItem as Item;
                bool ray = originalWeaponItem.TemplateId.ToString() == ZombiesAmmo.RaygunId;
                if (!ray && shell != null && shell.TemplateId.ToString() != M381)
                    return;
                if (!ray && !IsShellGun(originalWeaponItem))
                    return;
                GameWorld world = Singleton<GameWorld>.Instance;
                Player me = world?.MainPlayer;
                if (me == null || playerProfileIDWhoThrew != me.ProfileId)
                    return;
                string id = originalWeaponItem.TemplateId.ToString();
                float near, radius = 7f, far = -1f;
                string what;
                float inner = 0f;
                if (ray)
                {
                    // The Ray Gun's splash reaches further than the cell's own 2.5 m blast (user, 2026-11-09): zombies
                    // past EFT's blast and up to "Ray Gun splash reach" get the original's splash, fading out
                    bool pap = ZombiesPaP.Infused(originalWeaponItem);
                    near = (_raySplash?.Value ?? 300f) * 0.6f * (pap ? 2f : 1f);
                    far = near * 0.35f;
                    radius = _rayReach?.Value ?? 4.5f;
                    inner = 2.2f;
                    what = pap ? "Ray Gun (PaP) splash" : "Ray Gun splash";
                }
                else if (id == M32)
                {
                    bool pap = ZombiesPaP.Infused(originalWeaponItem);
                    if (pap)
                    {
                        near = _wmPapNear?.Value ?? 900f;
                        what = "War Machine (PaP)";
                    }
                    else
                    {
                        // Plain: it fires M67s on a fuse (user, 2026-11-09), so an M67's blast, round bonus and all
                        near = ZombiesHealth.GrenadeNear;
                        far = ZombiesHealth.GrenadeFar;
                        radius = ZombiesHealth.GrenadeRadius;
                        what = "War Machine";
                    }
                }
                else if (id == M1911)
                {
                    near = _sallyNear?.Value ?? 900f;
                    radius = 7f * 0.6f;
                    what = "Sally";
                }
                else
                {
                    near = _ubglNear?.Value ?? 600f;
                    what = "M203";
                }
                if (far < 0f)
                    far = Mathf.Min(near, _far?.Value ?? 125f);
                IObserverToPlayerBridge shooter = world.GetAlivePlayerBridgeByProfileID(me.ProfileId);
                Vector3 at = grenadePosition + Vector3.up * 0.3f;
                int hit = 0;
                _applying = true;
                try
                {
                    foreach (Player zombie in world.AllAlivePlayersList.Where(p => ZombiesTeam.IsZombie(p)).ToList())
                    {
                        if (zombie.HealthController == null || !zombie.HealthController.IsAlive)
                            continue;
                        Vector3 chest = zombie.Transform.position + Vector3.up * 1.1f;
                        float d = Vector3.Distance(at, chest);
                        if (d < inner || d > radius || Physics.Linecast(at, chest, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                            continue;
                        float bo = Mathf.Lerp(near, far, Mathf.InverseLerp(Mathf.Max(1f, inner), radius, d));
                        var info = new DamageInfo
                        {
                            DamageType = EDamageType.Explosion,
                            Damage = ZombiesHealth.ExplosionEft(bo),
                            Direction = (chest - at).normalized,
                            HitPoint = chest,
                            HitNormal = (at - chest).normalized,
                            MasterOrigin = at,
                            Player = shooter,
                            // No gun on our blast: a host drops explosion hits from shell guns as EFT's own shell
                            // blast (DropEft), and a Fika client's blast reached a headless host as just that, so the
                            // Sally / War Machine / M203 did nearly nothing in a headless match (user, 2026-11-09).
                            // The PaP bonus is already in "near" above
                            Weapon = null,
                            BodyPartColliderType = EBodyPartColliderType.RibcageUp,
                        };
                        ZombiesRules.HitZombie(zombie, info);
                        hit++;
                    }
                }
                finally
                {
                    _applying = false;
                }
                ZombiesDebug.Log($"{what} shell blast at {at}: {hit} zombie(s) hit ({near:0}-{far:0} over {radius:0.0} m)");
            }
            catch (System.Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies shells: the blast failed ({e.Message})");
            }
        }
    }
}
