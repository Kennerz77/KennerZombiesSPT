// SPZombies headshot gore (licence text: data\gore\LICENSE-gore.txt).
// Changes for SPZombies: namespace, log prefix, file location, zombies-only (our zombies in a zombies match).
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using Comfort.Common;
using UnityEngine;

namespace KennerZombies;

// the newer event's head-burst splash and impact sounds, played from an unmodified copy of its
// detail-trauma catalog bundle (gore-splash.bundle)
internal sealed class ZombiesGoreSplash
{
    internal const string FileName = "gore-splash.bundle";
    private const string PrefabName = "Zombie_Head_Splash_VFX";
    private const string ClipPrefix = "impact_head_flesh";
    private const int PoolSize = 8;
    private const float InstanceLifetime = 3f;
    // from the the event's impact_head_flesh sound bank
    private const int Rolloff = 120;
    private const float Volume = 0.5f;

    private readonly ManualLogSource _log;
    private readonly string _path;
    private readonly List<AudioClip> _clips = new();
    private readonly List<GameObject> _pool = new();
    private readonly List<float> _started = new();
    private AssetBundle? _bundle;
    private GameObject? _prefab;
    private bool _failed;

    internal ZombiesGoreSplash(ManualLogSource log, string pluginDirectory)
    {
        _log = log;
        _path = Path.Combine(pluginDirectory, FileName);
    }

    internal bool FileExists => File.Exists(_path);

    internal bool Emit(Vector3 position)
    {
        if (!EnsureLoaded())
            return false;

        var now = Time.time;
        var slot = -1;
        for (var i = _pool.Count - 1; i >= 0; i--)
        {
            if (!_pool[i])
            {
                _pool.RemoveAt(i);
                _started.RemoveAt(i);
            }
        }
        for (var i = 0; i < _pool.Count; i++)
        {
            if (now - _started[i] > InstanceLifetime)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0 && _pool.Count < PoolSize)
        {
            var created = UnityEngine.Object.Instantiate(_prefab!);
            created.SetActive(false);
            // raids unload their scene; the pool is ours and outlives them
            UnityEngine.Object.DontDestroyOnLoad(created);
            _pool.Add(created);
            _started.Add(0f);
            slot = _pool.Count - 1;
        }
        if (slot < 0)
        {
            // all busy: restart the oldest
            slot = 0;
            for (var i = 1; i < _pool.Count; i++)
            {
                if (_started[i] < _started[slot])
                    slot = i;
            }
        }

        var instance = _pool[slot];
        _started[slot] = now;
        instance.SetActive(false);
        // authored rotation points the burst up
        instance.transform.SetPositionAndRotation(position, _prefab!.transform.rotation);
        instance.SetActive(true);
        var system = instance.GetComponent<ParticleSystem>();
        if (system)
        {
            system.Clear(true);
            system.Play(true);
        }
        return true;
    }

    internal void PlaySound(Vector3 position, float distance)
    {
        if (!EnsureLoaded() || _clips.Count == 0 || !Singleton<BetterAudio>.Instantiated)
            return;
        var clip = _clips[UnityEngine.Random.Range(0, _clips.Count)];
        if (!clip)
            return;
        // clip overload on purpose: the bank's own blend options live in a bundle that may not be loaded
        Singleton<BetterAudio>.Instance.PlayAtPoint(position, clip, distance, BetterAudio.AudioSourceGroupType.Impacts,
            Rolloff, Volume, EOcclusionTest.OneShotFullOcclusion);
    }

    internal void Dispose()
    {
        foreach (var instance in _pool)
        {
            if (instance)
                UnityEngine.Object.Destroy(instance);
        }
        _pool.Clear();
        _started.Clear();
        _clips.Clear();
        _prefab = null;
        if (_bundle)
            _bundle!.Unload(true);
        _bundle = null;
    }

    // first use happens in a raid, so the game's shaders bundle the splash material points at is loaded
    private bool EnsureLoaded()
    {
        if (_prefab)
            return true;
        if (_failed || !FileExists)
            return false;

        try
        {
            if (!_bundle)
                _bundle = AssetBundle.LoadFromFile(_path);
            if (!_bundle)
                throw new InvalidOperationException("AssetBundle.LoadFromFile returned null");

            // only the catalog is a named asset and its script is retail-only, so the load itself returns
            // nothing; what matters is that it pulls the prefab and clips it references into memory
            foreach (var name in _bundle!.GetAllAssetNames())
                _bundle.LoadAsset(name);

            foreach (var system in Resources.FindObjectsOfTypeAll<ParticleSystem>())
            {
                if (system.name == PrefabName && !system.transform.parent && !system.gameObject.scene.IsValid())
                {
                    _prefab = system.gameObject;
                    break;
                }
            }
            if (!_prefab)
                throw new InvalidOperationException("splash prefab not found after loading the catalog");
            _prefab!.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            _clips.Clear();
            foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (clip.name.StartsWith(ClipPrefix, StringComparison.Ordinal))
                {
                    clip.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    _clips.Add(clip);
                }
            }
            _log.LogInfo("[gore] zombie gore splash loaded: " + _clips.Count + " impact clips");
            return true;
        }
        catch (Exception exception)
        {
            _failed = true;
            _log.LogWarning("[gore] zombie gore splash unavailable, using the plain blood burst: " + exception.Message);
            return false;
        }
    }
}
