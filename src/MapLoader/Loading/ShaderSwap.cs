using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KennerZombies.MapLoader.Loading
{
    // The SDK's materials use editor stand-ins named with the stand-in prefix + the game shader's name (Tools/ShaderStandins):
    // same properties and keywords as the game's shader, but only a preview's shading. When one of
    // our scenes loads, every such material is moved onto the game's compiled shader of that name;
    // textures, values and keywords carry over because the property names match.
    //
    // The game's shaders are loaded from StreamingAssets/Windows/shaders at startup, so they are
    // found among the loaded shaders. The prefix means a stand-in can never be picked as the target.
    // Approach from ArenaMapPort's ShaderRebind (prefixed stand-ins, exact-name lookup).
    internal static class ShaderSwap
    {
        // The stand-ins' names as baked into existing map bundles: kept exactly, like the stub class names
        private const string Prefix = "Tarkov/";

        public static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!MapRegistry.IsCustomScene(scene))
                return;
            try
            {
                var materials = new HashSet<Material>();
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                        foreach (var material in renderer.sharedMaterials)
                            materials.Add(material);
                Swap(scene.name, materials);
            }
            catch (Exception e)
            {
                Loader.Log.LogError($"Shader swap failed for scene {scene.name}: {e}");
            }
            // the full weather system ships inactive and is woken only once its shaders are the game's
            WeatherActivation.Activate(scene);
            // after the weather has had its chance to wake its WindController
            WindFallback.Apply();
            WeatherActivation.ActivateFlares(scene);
            AmbientAudio.Setup(scene);
            GraphicsModsCompat.PrepareScene(scene);
        }

        public static bool IsStandIn(Shader shader) =>
            shader != null && shader.name.StartsWith(Prefix, StringComparison.Ordinal);

        // the game's shader for a stand-in, or null when the game has none loaded
        public static Shader GameShader(string standInName)
        {
            var name = standInName.StartsWith(Prefix, StringComparison.Ordinal) ? standInName.Substring(Prefix.Length) : standInName;
            Shader found = null;
            foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
                if (shader != null && shader.name == name)
                {
                    found = shader;
                    break;
                }
            if (found == null)
                found = Shader.Find(name);
            return found != null && !IsStandIn(found) ? found : null;
        }

        public static void Swap(string context, IEnumerable<Material> candidates)
        {
            var materials = new HashSet<Material>();
            foreach (var material in candidates)
                if (material != null && IsStandIn(material.shader))
                    materials.Add(material);
            if (materials.Count == 0)
                return;

            var gameShaders = new Dictionary<string, Shader>(StringComparer.Ordinal);
            foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
                if (shader != null && !shader.name.StartsWith(Prefix, StringComparison.Ordinal))
                    gameShaders[shader.name] = shader;

            int swapped = 0;
            var missing = new HashSet<string>();
            foreach (var material in materials)
            {
                var name = material.shader.name.Substring(Prefix.Length);
                if (!gameShaders.TryGetValue(name, out var target))
                {
                    target = Shader.Find(name);
                    if (target == null || target.name.StartsWith(Prefix, StringComparison.Ordinal))
                    {
                        missing.Add(name);
                        continue;
                    }
                }
                // a queue the author set explicitly survives; one taken from the stand-in follows the game shader
                int queue = material.renderQueue;
                bool queueFromShader = queue == material.shader.renderQueue;
                material.shader = target;
                if (!queueFromShader)
                    material.renderQueue = queue;
                swapped++;
            }

            Loader.Verbose($"{context}: {swapped} material(s) moved onto game shaders");
            if (missing.Count > 0)
                Loader.Log.LogWarning($"{context}: game shader not found, stand-in kept for: {string.Join(", ", missing)}");
        }
    }
}
