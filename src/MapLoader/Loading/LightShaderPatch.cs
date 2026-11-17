using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // The game's own light types draw through the game's hidden shaders, which the SDK cannot reference
    // from the editor, so SDK maps ship them with the shader field empty. Fill it by the name retail
    // maps use right before the light first reads it; these shaders are always included in the game
    // build. Retail lights already have theirs and pass through untouched.
    //
    //  AreaLight      m_ProxyShader  "Hidden/AreaLight"     read in InitDirect (from Awake/OnEnable)
    //  AdvancedLight  Shader         "CustomLights/Light"   read in Initialize (from Start/OnEnable)
    internal class LightShaderPatch : ModulePatch
    {
        private static readonly Dictionary<string, Shader> Found = new Dictionary<string, Shader>();
        private readonly MethodBase _target;

        private LightShaderPatch(MethodBase target)
        {
            _target = target;
        }

        public static LightShaderPatch[] ForAllLights() => new[]
        {
            new LightShaderPatch(AccessTools.Method(typeof(AreaLight), "InitDirect")),
            new LightShaderPatch(AccessTools.Method(typeof(AdvancedLight), "Initialize")),
        };

        protected override MethodBase GetTargetMethod() => _target;

        [PatchPrefix]
        private static bool Prefix(MonoBehaviour __instance)
        {
            // explicit comparisons: Unity's overloaded == also catches destroyed/missing references
            if (__instance is AreaLight area && area.m_ProxyShader == null)
            {
                area.m_ProxyShader = Find("Hidden/AreaLight");
                if (area.m_ProxyShader == null)
                    return Disable(area);
            }
            else if (__instance is AdvancedLight advanced && advanced.Shader == null)
            {
                advanced.Shader = Find("CustomLights/Light");
                if (advanced.Shader == null)
                    return Disable(advanced);
            }
            return true;
        }

        // a material from a null shader throws every frame; the light stays off instead
        private static bool Disable(MonoBehaviour light)
        {
            light.enabled = false;
            return false;
        }

        private static Shader Find(string name)
        {
            if (Found.TryGetValue(name, out var shader) && shader != null)
                return shader;
            shader = Shader.Find(name);
            if (shader == null)
                Loader.Log.LogError($"Shader '{name}' not found; lights that need it are disabled on this map.");
            return Found[name] = shader;
        }
    }
}
