using System;
using BepInEx.Logging;
using KennerZombies.MapLoader.AI;
using KennerZombies.MapLoader.Loading;

namespace KennerZombies.MapLoader
{
    /// <summary>
    /// SPZombies' own map loader: serves each installed zombies map's bundles from its own folder
    /// (BepInEx\plugins\SPZombies_Maps\ZM_&lt;Name&gt;\bundles or Kennerz_ZM_&lt;Map&gt;\bundles), fixes up its scenes as they
    /// load (stand-in shaders swapped for the game's, weather, time of day, ambience, lights, cameras) and gives bots
    /// navigation data made from the map's navmesh. The server side (ZombiesMapLoader) registers each map as a location.
    ///
    /// Zombies maps need no other mod. The map select pages, raid events, loot, BTR and sniper zones are left out:
    /// zombies maps don't use them. A map the original CustomMapLoader mod also has is left to it (MapRegistry).
    /// </summary>
    internal static class Loader
    {
        internal static ManualLogSource Log => ZombiesPlugin.Log;

        /// <summary>Bots get cover points, voxels, door links and patrols made from the navmesh at raid start.</summary>
        internal const bool GenerateAiData = true;

        /// <summary>With SAIN installed: the map type SAIN treats zombies maps as, for its per-map settings.</summary>
        internal const string SainMapType = "Customs";

        /// <summary>Detail lines (every bundle served, AI generation counts); dev builds only.</summary>
#if RELEASE
        internal static bool VerboseLogging => false;
#else
        internal static bool VerboseLogging = false;
#endif

        internal static void Init()
        {
            MapRegistry.Init(MapEditor.MapFolders());
            if (MapRegistry.MapIds.Count == 0 && MapRegistry.LeftToOriginal.Count > 0)
                Log.LogInfo($"Map loader: nothing to load ({string.Join(", ", MapRegistry.LeftToOriginal)} left to the CustomMapLoader mod)");
            else if (MapRegistry.MapIds.Count == 0)
                Log.LogWarning("Map loader: no zombies maps installed (BepInEx\\plugins\\SPZombies_Maps\\ZM_<Name>\\bundles)");
            else
                Log.LogInfo($"Map loader: {string.Join(", ", MapRegistry.MapIds)}");

            ShaderSwap.Install();
            Enable(new BundleHostPatch());
            Enable(new SpatialAudioInitPatch());
            Enable(new SpatialAudioDataPathPatch());
            Enable(new AmbientBlendExitDelayPatch());
            foreach (var patch in MetaAcousticPathPatch.All())
                Enable(patch);
            Enable(new SceneSingletonPatch());
            Enable(new StationaryControllerPatch());
            Enable(new AmbientLightShaderPatch());
            foreach (var patch in LightShaderPatch.ForAllLights())
                Enable(patch);
            Enable(new LevelSettingsFixPatch());
            Enable(new SimpleTimeOfDayPatch());
            Enable(new RainCameraPatch());
            Enable(new SwitchCurvePatch());
            Enable(new PhysicsPropAwakePatch());
            Enable(new PhysicsPropPushPatch());
            Enable(new MboitFogTimePatch());
            Enable(new CloudsNotRunningPatch());
            Enable(new WeatherFogDensityPatch());
            Enable(new CameraEffectsPatch());
            foreach (var patch in CameraSettingSetterPatch.ForAllSetters())
                Enable(patch);
            foreach (var patch in OcclusionWhenUninitializedPatch.ForAllOverloads())
                Enable(patch);
            Enable(new InteractiveOcclusionWhenUninitializedPatch());
            Enable(new RestoreCoversPatch());
            Enable(new CachePointsPatch());
            Enable(new StartCorePointPatch());
            Enable(new NullClipPatch());
            Enable(new NullClipQueuedPatch());
            Enable(new BarbedWireSoundPatch());
            if (SainLocationPatch.Available())
                Enable(new SainLocationPatch());
        }

        // one failing patch must not stop the others from attaching
        private static void Enable(SPT.Reflection.Patching.ModulePatch patch)
        {
            try
            {
                patch.Enable();
            }
            catch (Exception e)
            {
                Log.LogError($"Map loader: {patch.GetType().Name} failed to attach: {e}");
            }
        }

        internal static void Verbose(string message)
        {
            if (VerboseLogging)
                Log.LogInfo(message);
        }
    }
}
