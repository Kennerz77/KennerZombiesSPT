// SPZombies headshot gore (licence text: data\gore\LICENSE-gore.txt).
// Changes for SPZombies: namespace, log prefix, file location, zombies-only (our zombies in a zombies match).
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

using BepInEx.Logging;
using Comfort.Common;
using Diz.Skinning;
using EFT;
using EFT.Ballistics;
using EFT.Visual;
using Systems.Effects;
using UnityEngine;

namespace KennerZombies;

// backport of the newer event's zombie head trauma: a qualifying head hit swaps the head to its
// skull-open mesh and adds a blood burst. the event's own trigger rule lives in il2cpp code we
// cant read, so the chances here are ours
internal sealed class ZombiesGoreRuntime
{
    internal sealed class Settings
    {
        internal bool Enabled = true;
        internal float HeadshotChance = 0.35f;
        internal float KillingHeadshotChance = 0.75f;
        internal bool BloodBurst = true;
        internal bool BurstSound = true;
        internal bool MeleeHits;
    }

    private const EDamageType RangedDamage = EDamageType.Bullet | EDamageType.Sniper | EDamageType.GrenadeFragment
        | EDamageType.Explosion | EDamageType.Landmine | EDamageType.Artillery;
    private const string InfectedRolePrefix = "infected";
    private const float SplatterRange = 3f;

    private static readonly int MainTex = Shader.PropertyToID("_MainTex");
    private static readonly int BumpMap = Shader.PropertyToID("_BumpMap");
    private static readonly int SpecMap = Shader.PropertyToID("_SpecMap");
    private static readonly int DecalPower = Shader.PropertyToID("_DecalPower");
    private static readonly int Glossness = Shader.PropertyToID("_Glossness");
    private static readonly int Specularness = Shader.PropertyToID("_Specularness");
    private static readonly int DefVals = Shader.PropertyToID("_DefVals");
    private static readonly int SpecVals = Shader.PropertyToID("_SpecVals");
    private static readonly int ReflectColor = Shader.PropertyToID("_ReflectColor");

    private readonly ManualLogSource _log;
    private readonly Dictionary<Shader, Material> _fleshByShader = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private ZombiesGorePack? _pack;
    private ZombiesGoreSplash? _splash;
    private int _splatterMask;

    internal Settings Current { get; } = new();

    internal ZombiesGoreRuntime(ManualLogSource log)
    {
        _log = log;
    }

    internal bool LoadPack()
    {
        // shipped beside the core DLL: data\gore -> BepInEx\plugins\Kennerz_ZM_Core\gore
        var directory = Path.Combine(ZombiesPlugin.Dir, "gore");
        var path = Path.Combine(directory, ZombiesGorePack.FileName);
        if (!File.Exists(path))
        {
            _log.LogWarning("[gore] zombie gore is off: " + path + " is missing");
            return false;
        }

        try
        {
            _pack = ZombiesGorePack.Load(path);
        }
        catch (Exception exception)
        {
            _log.LogError("[gore] zombie gore is off: could not read " + path + ": " + exception);
            return false;
        }

        // unknown layer names just contribute no bits
        _splatterMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain");
        _log.LogInfo("[gore] zombie gore pack loaded: " + _pack.Heads.Count + " heads");
        _splash = new ZombiesGoreSplash(_log, directory);
        if (!_splash.FileExists)
            _log.LogWarning("[gore] " + ZombiesGoreSplash.FileName + " is missing, head bursts use the plain blood burst and no sound");
        return true;
    }

    internal void Shutdown()
    {
        foreach (var material in _fleshByShader.Values)
        {
            if (material)
                UnityEngine.Object.Destroy(material);
        }
        _fleshByShader.Clear();
        _splash?.Dispose();
        _splash = null;
        _pack?.Dispose();
        _pack = null;
    }

    // called after Player.ApplyShot accepted a hit
    internal void OnShotApplied(Player player, DamageInfo damage, EBodyPart bodyPart)
    {
        var pack = _pack;
        if (!_firstHeadLogged && bodyPart == EBodyPart.Head && ZombiesMatch.Active)
        {
            _firstHeadLogged = true;
            _log.LogInfo($"[gore] first head hit this match: {player?.GetType().Name}, AI {player?.IsAI}, zombie {(player != null && ZombiesTeam.IsZombie(player))}, " +
                         $"damage {damage.DamageType}, online {ZombiesNet.Online}, host {ZombiesNet.IsHost}, on {Current.Enabled}");
        }
        if (pack == null || !Current.Enabled || bodyPart != EBodyPart.Head || !IsInfected(player))
            return;
        if ((damage.DamageType & RangedDamage) == 0 && !(Current.MeleeHits && damage.DamageType == EDamageType.Melee))
            return;
        // Co-op: the SHOOTER's game rolls the gore and tells everyone (Msg.Gore, relayed by the host). With Fika a
        // player's hit on a zombie runs ApplyShot only on that player's game (ObservedPlayer.ApplyShot sends a damage
        // packet; the host applies it via HandleDamagePacket, no ApplyShot), so a host-only roll never saw players'
        // headshots: no gore with a headless host (2026-11-17, twice). Once per zombie on this game (_opened, by id)
        if (!_opened.Add(player.ProfileId))
            return;

        // The kill is the host's to decide; on a player's game this hit "kills" when it takes the head's health to 0
        var killed = !player.HealthController.IsAlive;
        if (!killed)
        {
            try
            {
                killed = player.HealthController.GetBodyPartHealth(EBodyPart.Head).Current - damage.Damage <= 0f;
            }
            catch
            {
                // no health to read: the plain headshot chance
            }
        }
        var chance = killed ? Current.KillingHeadshotChance : Current.HeadshotChance;
        if (chance <= 0f || UnityEngine.Random.value >= chance)
        {
            _opened.Remove(player.ProfileId);
            return;
        }
        if (ZombiesNet.Online)
        {
            ZombiesNet.Gore(player.ProfileId, damage.HitPoint, damage.Direction);
            if (!_sentLogged)
            {
                _sentLogged = true;
                _log.LogInfo("[gore] a zombie's head opened here, sent to the other players (logged once)");
            }
        }
        // This game's own copy, when it draws heads
        if (!ZombiesNet.Headless)
            Open(player, damage);
    }

    // Zombies whose head was already opened on this game (a zombie's id is new each spawn); cleared each match
    private readonly HashSet<string> _opened = new HashSet<string>();
    private bool _sentLogged, _remoteLogged, _firstHeadLogged;

    internal void NewMatch()
    {
        _opened.Clear();
        _sentLogged = _remoteLogged = _firstHeadLogged = false;
    }

    /// <summary>Another player's game opened this zombie's head: the same here (no roll of our own).</summary>
    internal void OnRemote(Player player, Vector3 hitPoint, Vector3 direction)
    {
        if (_pack == null || !Current.Enabled || player == null || !IsInfected(player) || ZombiesNet.Headless)
            return;
        _opened.Add(player.ProfileId);
        var damage = new DamageInfo { HitPoint = hitPoint, Direction = direction, DamageType = EDamageType.Bullet };
        bool opened = Open(player, damage);
        if (!_remoteLogged)
        {
            _remoteLogged = true;
            _log.LogInfo($"[gore] another player opened a zombie's head: {(opened ? "shown here" : "this head couldn't be swapped here")} (logged once)");
        }
    }

    /// <summary>The skull opened (when this head has a gore mesh) and the blood burst. False when the head can't be swapped.</summary>
    private bool Open(Player player, DamageInfo damage)
    {
        var pack = _pack;
        var body = player.PlayerBody;
        if (pack == null || !body || !body.BodySkins.TryGetValue(EBodyModelPart.Head, out var headSkin) || !headSkin)
            return false;
        var lods = headSkin._lods;
        if (lods == null || lods.Length == 0 || !lods[0])
            return false;
        var renderer = lods[0].SkinnedMeshRenderer;
        if (!renderer || !renderer.sharedMesh || pack.IsGoreMesh(renderer.sharedMesh))
            return false;

        var prefab = headSkin.gameObject.name;
        var cloneSuffix = prefab.IndexOf("(Clone)", StringComparison.Ordinal);
        if (cloneSuffix >= 0)
            prefab = prefab.Substring(0, cloneSuffix);
        if (pack.TryFind(renderer.sharedMesh.name, prefab, out var head))
        {
            if (!TrySwapHead(body, lods, head))
                return false;
        }
        else
        {
            ReportOnce("nohead:" + prefab, "[gore] no gore mesh for zombie head '" + prefab + "' (mesh '" + renderer.sharedMesh.name + "'); blood only.");
        }

        if (Current.BloodBurst || Current.BurstSound)
            EmitBlood(damage);
        return true;
    }

    // SPZombies: only our own zombies, and only in a zombies match (normal raids are left to other mods)
    private static bool IsInfected(Player player)
    {
        // Not player.IsAI: on a co-op player's game a zombie is Fika's copy of the host's bot, which needn't count as AI
        // there (no gore for players' shots, 2026-11-17). ZombiesTeam.IsZombie leaves humans out by itself
        if (!player || !ZombiesMatch.Active)
            return false;
        return ZombiesTeam.IsZombie(player);
    }

    private bool TrySwapHead(PlayerBody body, AbstractSkin[] lods, ZombiesGorePack.Head head)
    {
        var skeleton = body.SkeletonRootJoint;
        if (!skeleton)
            return false;

        // the deformed mesh can carry fewer/reordered bones than the base head, so re-skin from its own paths
        var bones = new Transform[head.BonePaths.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            if (!skeleton.Bones.TryGetValue(head.BonePaths[i], out bones[i]) || !bones[i])
            {
                ReportOnce("nobone:" + head.Prefab, "[gore] gore skipped for '" + head.Prefab + "': skeleton has no bone '" + head.BonePaths[i] + "'.");
                return false;
            }
        }
        if (!skeleton.Bones.TryGetValue(head.RootBone, out var rootBone) || !rootBone)
            return false;

        // validate every lod before touching any of them
        for (var i = 0; i < lods.Length; i++)
        {
            var renderer = lods[i] ? lods[i].SkinnedMeshRenderer : null;
            if (!renderer || renderer!.sharedMaterials.Length != head.Mesh.subMeshCount - 1)
            {
                ReportOnce("mats:" + head.Prefab, "[gore] gore skipped for '" + head.Prefab + "': unexpected material layout.");
                return false;
            }
        }

        for (var i = 0; i < lods.Length; i++)
        {
            var renderer = lods[i].SkinnedMeshRenderer;
            var materials = renderer.sharedMaterials;
            var swapped = new Material[materials.Length + 1];
            for (int source = 0, target = 0; target < swapped.Length; target++)
                swapped[target] = target == head.FleshSubmesh ? GetFlesh(materials) : materials[source++];

            renderer.sharedMesh = head.Mesh;
            renderer.bones = bones;
            renderer.rootBone = rootBone;
            renderer.sharedMaterials = swapped;

            // keep the game's own re-skin / face cover paths on the new mesh
            var skin = lods[i] as Skin;
            if (lods[i] is HeadSkin headSkin)
            {
                headSkin.Data = new HeadSkinData { Base = head.Mesh, FaceCover = head.Mesh };
                skin = headSkin.Skin;
            }
            if (skin)
            {
                skin!._bonePaths = head.BonePaths;
                skin._rootBonePath = head.RootBone;
            }
        }
        return true;
    }

    // flesh is the head's own skin material with the flesh maps swapped in, so the shader is
    // the exact instance the game already renders with
    private Material GetFlesh(Material[] headMaterials)
    {
        var source = headMaterials[0];
        for (var i = 0; i < headMaterials.Length; i++)
        {
            var candidate = headMaterials[i];
            // eyes use the emissive variant
            if (candidate && candidate.shader && candidate.shader.name.IndexOf("Emissive", StringComparison.Ordinal) < 0)
            {
                source = candidate;
                break;
            }
        }

        if (_fleshByShader.TryGetValue(source.shader, out var flesh) && flesh)
            return flesh;

        flesh = new Material(source) { name = "gore_flesh" };
        flesh.SetTexture(MainTex, _pack!.Diffuse);
        flesh.SetTexture(BumpMap, _pack.Normal);
        flesh.SetTexture(SpecMap, _pack.Gloss);
        // values from the event's pork_head material; decal power off so the skin's hit decals dont map onto the brain
        flesh.SetFloat(DecalPower, 0f);
        flesh.SetFloat(Glossness, 1.76f);
        flesh.SetFloat(Specularness, 3.74f);
        flesh.SetVector(DefVals, new Vector4(1f, 0.2f, 0f, 0f));
        flesh.SetVector(SpecVals, new Vector4(1.1f, 2f, 0f, 0f));
        flesh.SetColor(ReflectColor, new Color(0.235f, 0.235f, 0.235f, 0.5f));
        _fleshByShader[source.shader] = flesh;
        return flesh;
    }

    private void EmitBlood(DamageInfo damage)
    {
        try
        {
            if (!Singleton<Effects>.Instantiated)
                return;
            var effects = Singleton<Effects>.Instance;
            var point = damage.HitPoint;
            var direction = damage.Direction.sqrMagnitude > 0.0001f ? damage.Direction.normalized : Vector3.up;

            var camera = Camera.main;
            var distance = camera ? Vector3.Distance(camera.transform.position, point) : 0f;
            if (Current.BurstSound)
                _splash?.PlaySound(point, distance);
            if (!Current.BloodBurst)
                return;

            // the event's splash when its bundle is there, the stock body-hit particles otherwise
            var body = _splash != null && _splash.Emit(point) ? null : effects.Get(MaterialType.Body);
            if (body != null && camera)
            {
                var particles = body.Particles;
                for (var i = 0; i < particles.Length; i++)
                {
                    // out the exit side and up off the opened skull
                    particles[i].Emit(point, direction, distance);
                    particles[i].Emit(point + Vector3.up * 0.06f, Vector3.up, distance);
                    particles[i].Emit(point + Vector3.up * 0.06f, (Vector3.up - direction).normalized, distance);
                }
            }

            if (_splatterMask == 0)
                return;
            if (Physics.Raycast(point, direction, out var wall, SplatterRange, _splatterMask, QueryTriggerInteraction.Ignore))
                effects.EmitBloodOnEnvironment(wall.point, wall.normal);
            if (Physics.Raycast(point, Vector3.down, out var floor, SplatterRange, _splatterMask, QueryTriggerInteraction.Ignore))
                effects.EmitBloodOnEnvironment(floor.point, floor.normal);
        }
        catch (Exception exception)
        {
            ReportOnce("blood", "[gore] zombie gore blood burst failed: " + exception);
        }
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Count < 64 && _reported.Add(key))
            _log.LogWarning(message);
    }
}
