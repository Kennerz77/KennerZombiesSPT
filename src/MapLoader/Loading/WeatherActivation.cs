using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KennerZombies.MapLoader.Loading
{
    // Wakes the SDK's full weather system (TOD_Sky, WeatherController, clouds, wind, rain, snow).
    //
    // The SDK saves its root, "SDK_Weather", inactive: those components cache materials and shaders
    // in Awake, and Awake would otherwise run on the editor's stand-in shaders before sceneLoaded
    // lets us swap them. Here, before activating it:
    //
    //  - every material it uses, held by renderers or by component and ScriptableObject fields, is
    //    moved onto the game's shader (ShaderSwap)
    //  - Shader fields holding a stand-in placeholder (prefix + name) get the game's shader of that name, and
    //    ComputeShader fields holding a stand-in placeholder (prefix + name) the game's compute shader
    //  - "Freeze Time" (TOD_Time.LockCurrentTime saved on) lets the raid clock set the time once,
    //    then holds it there
    //
    // The SDK's lens flare settings, "SDK_Flares" (FlareSceneSettings with the game's flare atlas),
    // ship inactive the same way and get the same shader swap before they wake.
    internal static class WeatherActivation
    {
        private const string RootName = "SDK_Weather";
        private const string FlaresRootName = "SDK_Flares";
        // Baked into existing map bundles: kept exactly, like the stub class names
        private const string ComputePrefix = "Tarkov_";
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void ActivateFlares(Scene scene)
        {
            var root = InactiveRoot(scene, FlaresRootName);
            if (root == null)
                return;
            try
            {
                SwapShaders(scene, root);
            }
            catch (Exception e)
            {
                Loader.Log.LogError($"Preparing the lens flares failed, activating them anyway: {e}");
            }
            root.SetActive(true);
            Loader.Verbose($"Lens flares active on {scene.name}");
        }

        private static GameObject InactiveRoot(Scene scene, string name)
        {
            foreach (var go in scene.GetRootGameObjects())
                if (go.name == name)
                    return go.activeSelf ? null : go;
            return null;
        }

        // every material the root uses, held by renderers or by component and ScriptableObject
        // fields, onto the game's shader; placeholder Shader and ComputeShader fields to the game's
        private static void SwapShaders(Scene scene, GameObject root)
        {
            var materials = new HashSet<Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                materials.UnionWith(renderer.sharedMaterials);
            // by reference: game types' own GetHashCode can throw on incomplete data (CloudLayer does)
            var visited = new HashSet<object>(ReferenceComparer.Instance);
            int shaders = 0, computes = 0;
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                Walk(root.name, behaviour, 0, visited, materials, ref shaders, ref computes);
            ShaderSwap.Swap(scene.name + "/" + root.name, materials);
            Loader.Verbose($"{root.name}: {materials.Count} material(s) checked, {shaders} shader and {computes} compute shader field(s) filled");
        }

        public static void Activate(Scene scene)
        {
            var root = InactiveRoot(scene, RootName);
            if (root == null)
                return;

            try
            {
                SwapShaders(scene, root);

                foreach (var time in root.GetComponentsInChildren<TOD_Time>(true))
                    if (time.LockCurrentTime)
                    {
                        time.LockCurrentTime = false;
                        time.gameObject.AddComponent<FreezeTimeAfterStart>();
                    }

                // the fog remap data stays as on Customs; WeatherFogDensityPatch makes the world fog
                // follow the weather and MboitFogTimePatch covers the frames before the raid clock exists

                // a cloud layer without its texture throws every frame (maps built before the SDK saved it);
                // CloudsNotRunningPatch keeps the WeatherController from calling the disabled clouds
                foreach (var clouds in root.GetComponentsInChildren<EFT.Rendering.Clouds.CloudController>(true))
                {
                    var layer = HarmonyLib.Traverse.Create(clouds).Field("_settings").GetValue() as EFT.Rendering.Clouds.CloudLayer;
                    if (layer == null || layer.LayerA == null || layer.LayerA.CloudTexture == null)
                    {
                        clouds.enabled = false;
                        Loader.Log.LogWarning($"{RootName}: cloud layer has no texture, clouds disabled. Rebuild the full weather system in the SDK.");
                    }
                }

                // TOD_Sky drives its directional light ("Light" under Sky Dome); without it every
                // weather component throws and the raid fails to start (the object was deleted in the SDK)
                foreach (var components in root.GetComponentsInChildren<TOD_Components>(true))
                    if (components.Light == null)
                    {
                        components.Light = CreateSunLight(components.transform);
                        Loader.Log.LogWarning($"{RootName}: the sky's sun light ('Light' under '{components.name}') is missing; created a default one. Rebuild the full weather system in the SDK.");
                    }

                // the camera counts as under rain (screen drops, wet screen, ripples) only when
                // WeatherObstacle exists; a map without rain cover gets an empty one: rain everywhere
                if (UnityEngine.Object.FindObjectOfType<WeatherObstacle>() == null)
                {
                    var cover = new GameObject("SDK_RainCover") { layer = LayerMask.NameToLayer("Triggers") };
                    cover.transform.SetParent(root.transform, false);
                    cover.AddComponent<WeatherObstacle>();
                    Loader.Verbose($"{RootName}: no rain cover on the map; the whole map counts as under rain");
                }

                // AmbientLight attaches to cameras only through an OnRenderObjectManager; maps built
                // before the SDK added one to SDK_Weather have none
                if (UnityEngine.Object.FindObjectOfType<OnRenderObjectManager>() == null)
                {
                    var manager = new GameObject("OnRenderObjectManager");
                    manager.transform.SetParent(root.transform, false);
                    manager.AddComponent<OnRenderObjectManager>();
                    Loader.Verbose($"{RootName}: added an OnRenderObjectManager");
                }
            }
            catch (Exception e)
            {
                Loader.Log.LogError($"Preparing the weather system failed, activating it anyway: {e}");
            }

            root.SetActive(true);
            Loader.Verbose($"Full weather system active on {scene.name}");
        }

        // Retail maps' camera prefabs carry the weather's camera components: TOD_Scattering (fog and
        // atmosphere), RainScreenDrops and ScreenWater (rain on the screen). SDK maps use the game's
        // fallback camera, so the SDK ships them on an inactive "SDK_CameraEffects" template and they
        // are copied onto the player camera here. The components' Awake only caches references; their
        // setup runs in Start, after the copied values are in place.
        public static void CopyCameraEffects(GameObject camera)
        {
            var template = FindCameraTemplate();
            if (template == null)
                return;
            int copied = 0;
            foreach (var source in template.GetComponents<MonoBehaviour>())
            {
                if (source == null)
                    continue;
                var existing = camera.GetComponent(source.GetType()) as MonoBehaviour;
                if (existing != null)
                {
                    // the fallback camera's own effect (PrismEffects: exposure, tonemapping, bloom):
                    // take the retail map's settings but keep the camera's shaders, textures and preset
                    try
                    {
                        var references = SnapshotReferences(existing);
                        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), existing);
                        foreach (var (field, value) in references)
                            field.SetValue(existing, value);
                        copied++;
                    }
                    catch (Exception e)
                    {
                        Loader.Log.LogWarning($"Could not apply {source.GetType().Name} settings to the camera: {e.Message}");
                    }
                    continue;
                }
                try
                {
                    var copy = (MonoBehaviour)camera.AddComponent(source.GetType());
                    // object references survive as instance ids within the session
                    JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(source), copy);
                    // the field, not the MBOIT property: the property runs Init now, before the camera is complete
                    if (copy is TOD_Scattering scattering)
                        scattering._mboit = false;
                    copy.enabled = true;
                    copied++;
                }
                catch (Exception e)
                {
                    Loader.Log.LogWarning($"Could not copy {source.GetType().Name} onto the camera: {e.Message}");
                }
            }
            if (copied > 0)
                Loader.Verbose($"Copied {copied} weather camera effect(s) onto {camera.name}");
        }

        // Customs' sun light, as the SDK builds it
        private static GameObject CreateSunLight(Transform skyDome)
        {
            var go = new GameObject("Light") { layer = skyDome.gameObject.layer };
            go.transform.SetParent(skyDome, false);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.933f, 0.813f, 0.509f);
            light.intensity = 0.8f;
            light.shadows = LightShadows.Soft;
            light.shadowBias = 0.02f;
            light.shadowNormalBias = 0.1f;
            light.cullingMask = unchecked((int)4292345855u);
            light.bounceIntensity = 0f;
            return go;
        }

        private static List<(FieldInfo, object)> SnapshotReferences(MonoBehaviour component)
        {
            var list = new List<(FieldInfo, object)>();
            for (var type = component.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var field in type.GetFields(Fields | BindingFlags.DeclaredOnly))
                    if (!field.IsStatic && typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                        list.Add((field, field.GetValue(component)));
            return list;
        }

        private static GameObject FindCameraTemplate()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !MapRegistry.IsCustomScene(scene))
                    continue;
                foreach (var go in scene.GetRootGameObjects())
                    if (go.name == RootName)
                    {
                        var template = go.transform.Find("SDK_CameraEffects");
                        if (template != null)
                            return template.gameObject;
                    }
            }
            return null;
        }

        // fields of components and of the serializable objects and ScriptableObjects they hold
        private static void Walk(string label, object target, int depth, HashSet<object> visited, HashSet<Material> materials, ref int shaders, ref int computes)
        {
            if (target == null || depth > 4 || !visited.Add(target))
                return;
            for (var type = target.GetType(); type != null && type != typeof(MonoBehaviour) && type != typeof(ScriptableObject) && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(Fields | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || field.IsLiteral)
                        continue;
                    var fieldType = field.FieldType;
                    object value;
                    try
                    {
                        value = field.GetValue(target);
                    }
                    catch
                    {
                        continue;
                    }
                    if (value == null)
                        continue;

                    if (value is Material material)
                    {
                        materials.Add(material);
                    }
                    else if (value is Shader shader)
                    {
                        if (ShaderSwap.IsStandIn(shader))
                        {
                            var game = ShaderSwap.GameShader(shader.name);
                            if (game != null)
                            {
                                field.SetValue(target, game);
                                shaders++;
                            }
                            else
                            {
                                Loader.Log.LogWarning($"{label}: no game shader '{shader.name.Substring(7)}' for {type.Name}.{field.Name}");
                            }
                        }
                    }
                    else if (value is ComputeShader compute)
                    {
                        if (compute.name.StartsWith(ComputePrefix, StringComparison.Ordinal))
                        {
                            var game = GameCompute(compute.name.Substring(ComputePrefix.Length));
                            if (game != null)
                            {
                                field.SetValue(target, game);
                                computes++;
                            }
                            else
                            {
                                // the placeholder has none of the game's kernels; null is the state the
                                // game's code checks for (e.g. WindController skips tree wind)
                                field.SetValue(target, null);
                                Loader.Log.LogWarning($"{label}: game compute shader '{compute.name.Substring(ComputePrefix.Length)}' isn't loaded; {type.Name}.{field.Name} left empty");
                            }
                        }
                    }
                    else if (value is ScriptableObject asset)
                    {
                        Walk(label, asset, depth + 1, visited, materials, ref shaders, ref computes);
                    }
                    else if (value is IList list && !(value is string))
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var item = list[i];
                            if (item is Material m)
                                materials.Add(m);
                            else if (item is ScriptableObject so)
                                Walk(label, so, depth + 1, visited, materials, ref shaders, ref computes);
                            else if (item != null && IsSerializableClass(item.GetType()))
                                Walk(label, item, depth + 1, visited, materials, ref shaders, ref computes);
                        }
                    }
                    else if (IsSerializableClass(fieldType))
                    {
                        Walk(label, value, depth + 1, visited, materials, ref shaders, ref computes);
                    }
                }
            }
        }

        private static bool IsSerializableClass(Type type) =>
            type.IsClass && !typeof(UnityEngine.Object).IsAssignableFrom(type) && type.IsSerializable && type != typeof(string);

        private static ComputeShader GameCompute(string name)
        {
            foreach (var compute in Resources.FindObjectsOfTypeAll<ComputeShader>())
                if (compute != null && compute.name == name)
                    return compute;
            return null;
        }
    }

    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // lets the raid clock set TOD_Time once (it reaches it a few frames into the raid), then locks it
    internal class FreezeTimeAfterStart : MonoBehaviour
    {
        private TOD_Time _time;
        private int _ticksWithClock;

        private void Awake()
        {
            _time = GetComponent<TOD_Time>();
        }

        private void FixedUpdate()
        {
            if (_time == null)
            {
                Destroy(this);
                return;
            }
            if (_time.GameDateTime != null && ++_ticksWithClock >= 2)
            {
                _time.LockCurrentTime = true;
                Loader.Verbose("Time of day frozen at the raid's start time");
                Destroy(this);
            }
        }
    }
}
