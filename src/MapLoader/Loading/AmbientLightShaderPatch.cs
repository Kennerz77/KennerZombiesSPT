using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // The SDK's lighting rig (Factory's TODSkySimple + AmbientLight) cannot reference the game's hidden
    // shaders from the editor, so AmbientLight arrives with its five shader fields empty and
    // Initialize would build materials from null shaders. Fill them by the names Factory's
    // AmbientLight uses; the shaders are always included in the game build.
    //
    // Initialize runs from OnEnable, Start, LateUpdate and SetSH, so this catches the first call
    // whatever triggers it. Retail AmbientLights already have their shaders and pass through untouched.
    internal class AmbientLightShaderPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(AmbientLight), nameof(AmbientLight.Initialize));
        }

        [PatchPrefix]
        private static bool Prefix(AmbientLight __instance)
        {
            if (__instance.ScreenAmbientShader != null)
                return true;

            // explicit comparisons, not ??: Unity's overloaded == also catches destroyed/missing references
            if (__instance.DepthWriteShader == null)
                __instance.DepthWriteShader = Find("Hidden/WriteDepth");
            if (__instance.ClearStencilShader == null)
                __instance.ClearStencilShader = Find("Hidden/ClearStencil");
            if (__instance.WriteStencilShader == null)
                __instance.WriteStencilShader = Find("Hidden/StencilShadow");
            __instance.ScreenAmbientShader = Find("Hidden/WriteScreenAmbient");
            if (__instance.AnalyticSourceShader == null)
                __instance.AnalyticSourceShader = Find("Hidden/AnalyticSource");

            if (__instance.DepthWriteShader == null || __instance.ClearStencilShader == null || __instance.WriteStencilShader == null
                || __instance.ScreenAmbientShader == null || __instance.AnalyticSourceShader == null)
            {
                // a material from a null shader throws every frame; the map stays unlit instead
                Loader.Log.LogError("AmbientLight shaders not found; ambient light is disabled on this map.");
                __instance.enabled = false;
                return false;
            }
            Loader.Verbose("Filled AmbientLight shaders for the custom map");
            return true;
        }

        private static Shader Find(string name)
        {
            var shader = Shader.Find(name);
            if (shader == null)
                Loader.Log.LogWarning($"Shader '{name}' not found");
            return shader;
        }
    }
}
