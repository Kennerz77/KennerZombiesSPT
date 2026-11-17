using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Audio.SpatialSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KennerZombies.MapLoader.Loading
{
    // Retail maps ship a configured SpatialAudioSystem (in their sound scene) plus a baked .audiobakedata
    // file. A map built with the SDK's spatial audio has both: its system and rooms in its scenes, its bake
    // in bundles/maps/<id>_audio/ (SpatialAudioDataPathPatch finds it), and the game initialises it as on
    // any map. A map without one gets the blank system the game creates, whose OcclusionSettings and
    // poolsConfig are null and whose Initialize throws, aborting the raid load: Initialize is skipped then,
    // the system stays uninitialised and sounds play without room/portal occlusion.
    //
    // Approach for maps without spatial audio from an earlier RaidFixPatches
    // (Patch_SpatialAudioInit and Patch_OcclusionWhenUninitialized), used with the author's permission.
    internal class SpatialAudioInitPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.Initialize));
        }

        [PatchPrefix]
        private static bool Prefix(SpatialAudioSystem __instance, ref Task __result)
        {
            if (!MapRegistry.IsCustomMapActive())
            {
                Loader.Verbose("SpatialAudioSystem.Initialize: not a custom map, running the original");
                return true;
            }
            var info = __instance._locationInfo;
            var bake = info != null && !string.IsNullOrEmpty(info.relativeBakeDataPath)
                ? MapRegistry.FindAudioFile(Path.GetFileName(info.relativeBakeDataPath)) : null;
            if (bake != null && __instance.OcclusionSettings != null && __instance.poolsConfig != null)
            {
                Loader.Verbose($"Custom map: spatial audio from {Path.GetFileName(bake)} ({info.roomsCount} rooms, {info.portalsCount} portals)");
                return true;
            }
            Loader.Verbose("Custom map: no baked spatial audio; spatial audio (occlusion) is not initialised and sounds play unoccluded.");
            __result = Task.CompletedTask;
            return false;
        }
    }

    // The game reads the bake from StreamingAssets/<relativeBakeDataPath>; an SDK map's is in its audio folder
    internal class SpatialAudioDataPathPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Constructor(typeof(SpatialAudioDataLoader), new[] { typeof(string), typeof(UnityEngine.MonoBehaviour) });

        [PatchPostfix]
        private static void Postfix(SpatialAudioDataLoader __instance, string dataPath)
        {
            if (File.Exists(__instance._fullPath) || !MapRegistry.IsCustomMapActive())
                return;
            var file = MapRegistry.FindAudioFile(Path.GetFileName(dataPath));
            if (file != null)
                __instance._fullPath = file;
        }
    }

    // Meta XR acoustics load their files through StreamingAssets (MetaXRAcousticMap.LoadMapAsync and
    // MetaXRAcousticGeometry.LoadGeometryAsync take the path below it); an SDK map's are in its audio
    // folder, reached from StreamingAssets with a relative path
    internal class MetaAcousticPathPatch : ModulePatch
    {
        private readonly MethodBase _target;

        private MetaAcousticPathPatch(MethodBase target)
        {
            _target = target;
        }

        protected override MethodBase GetTargetMethod() => _target;

        public static MetaAcousticPathPatch[] All()
        {
            var patches = new System.Collections.Generic.List<MetaAcousticPathPatch>();
            foreach (var (type, method) in new[] { ("MetaXRAcousticMap", "LoadMapAsync"), ("MetaXRAcousticGeometry", "LoadGeometryAsync") })
            {
                var target = AccessTools.Method(AccessTools.TypeByName(type), method, new[] { typeof(string) });
                if (target != null)
                    patches.Add(new MetaAcousticPathPatch(target));
            }
            return patches.ToArray();
        }

        [PatchPrefix]
        private static void Prefix(ref string __0)
        {
            if (string.IsNullOrEmpty(__0) || !MapRegistry.IsCustomMapActive())
                return;
            var streaming = UnityEngine.Application.streamingAssetsPath;
            if (File.Exists(Path.Combine(streaming, __0)))
                return;
            var file = MapRegistry.FindAudioFile(Path.GetFileName(__0));
            if (file == null)
                return;
            __0 = MakeRelative(streaming, file).Replace(Path.DirectorySeparatorChar, '/');
            Loader.Verbose($"Meta acoustics: {Path.GetFileName(file)} from the map's audio folder");
        }

        // Path.GetRelativePath, which netstandard2.1's Unity runtime may lack
        private static string MakeRelative(string from, string to)
        {
            var fromUri = new System.Uri(Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar, '/') + "/");
            var toUri = new System.Uri(Path.GetFullPath(to));
            return System.Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString());
        }
    }

    // BetterAudio still routes impacts and gunshots through ProcessSourceOcclusion, which throws on
    // an uninitialised system (and the effect replays the failing sound every frame). Return -1,
    // the game's own "no occlusion" result, until the system is initialised. Real maps initialise,
    // so this never changes their behaviour.
    internal class OcclusionWhenUninitializedPatch : ModulePatch
    {
        private readonly MethodBase _target;

        public OcclusionWhenUninitializedPatch(MethodBase target)
        {
            _target = target;
        }

        protected override MethodBase GetTargetMethod() => _target;

        public static OcclusionWhenUninitializedPatch[] ForAllOverloads()
        {
            var patches = new System.Collections.Generic.List<OcclusionWhenUninitializedPatch>();
            foreach (var method in typeof(SpatialAudioSystem).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (method.Name == nameof(SpatialAudioSystem.ProcessSourceOcclusion) && method.ReturnType == typeof(int))
                    patches.Add(new OcclusionWhenUninitializedPatch(method));
            return patches.ToArray();
        }

        [PatchPrefix]
        private static bool Prefix(ref int __result)
        {
            if (SpatialAudioSystem.Initialized)
                return true;
            __result = -1;
            return false;
        }
    }

    // Interactive objects (breakable windows, doors) occlude their sounds through
    // ProcessInteractiveObjectOcclusion, which also throws on an uninitialised system. For a window
    // the throw comes from PlayHitSound inside WindowBreaker.BreakByHit, after the impact effect but
    // before the glass breaks: only the first shot cracked it, later shots just spawned particles.
    // Skipped until the system is initialised; the sound then plays unoccluded.
    internal class InteractiveOcclusionWhenUninitializedPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SpatialAudioSystem), nameof(SpatialAudioSystem.ProcessInteractiveObjectOcclusion));

        [PatchPrefix]
        private static bool Prefix() => SpatialAudioSystem.Initialized;
    }
}
