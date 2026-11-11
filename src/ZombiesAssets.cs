using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>Loads zombies.bundle once, in the background at startup, and hands out the box and sounds by name.</summary>
    internal static class ZombiesAssets
    {
        public const string BundleFileName = "zombies.bundle";

        private static AssetBundle _bundle;
        private static bool _failed;
        private static readonly Dictionary<string, Object> Loaded = new Dictionary<string, Object>();
        private static readonly HashSet<string> Missing = new HashSet<string>();

        public static string BundlePath => Path.Combine(ZombiesPlugin.Dir, BundleFileName);

        public static bool Preloading { get; private set; }

        public static bool Ready => _bundle != null && !Preloading;

        public static IEnumerator PreloadAsync()
        {
            if (_bundle != null || _failed)
                yield break;
            if (!File.Exists(BundlePath))
            {
                ZombiesPlugin.Log.LogError($"Bundle not found at {BundlePath}");
                _failed = true;
                yield break;
            }
            Preloading = true;
            try
            {
                AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(BundlePath);
                yield return request;
                _bundle = request.assetBundle;
                if (_bundle == null)
                {
                    ZombiesPlugin.Log.LogError($"Failed to load {BundleFileName}. Was it built with Unity 2022.3.43f1 for StandaloneWindows64?");
                    _failed = true;
                }
            }
            finally
            {
                Preloading = false;
            }
        }

        /// <summary>The mystery box prefab, or null.</summary>
        public static GameObject Box() => Load<GameObject>("zm_box");

        /// <summary>What a box spot shows while the box is elsewhere (cases, teddy, blocks), or null.</summary>
        public static GameObject Fake() => Load<GameObject>("zm_box_fake");

        /// <summary>A map model (a door, debris, window boards...) by name, or null while it isn't in the bundle (no error).</summary>
        public static GameObject Model(string name)
        {
            if (!Ready || Missing.Contains(name))
                return null;
            if (Loaded.TryGetValue(name, out Object done) && done != null)
                return done as GameObject;
            var asset = _bundle.LoadAsset<GameObject>(name);
            if (asset == null)
            {
                Missing.Add(name);
                return null;
            }
            Loaded[name] = asset;
            return asset;
        }

        /// <summary>A sound from the bundle (its file name without .wav), or null.</summary>
        public static AudioClip Clip(string name) => Load<AudioClip>(name);

        /// <summary>An animation clip from the bundle (the monkey's the original arm clips), or null.</summary>
        public static AnimationClip Anim(string name) => Load<AnimationClip>(name);

        private static T Load<T>(string name) where T : Object
        {
            if (!Ready || Missing.Contains(name))
                return null;
            if (Loaded.TryGetValue(name, out Object done) && done != null)
                return done as T;
            T asset = _bundle.LoadAsset<T>(name);
            if (asset == null)
            {
                Missing.Add(name);
                ZombiesPlugin.Log.LogError($"'{name}' not in bundle. Assets: {string.Join(", ", _bundle.GetAllAssetNames())}");
                return null;
            }
            Loaded[name] = asset;
            return asset;
        }
    }
}
