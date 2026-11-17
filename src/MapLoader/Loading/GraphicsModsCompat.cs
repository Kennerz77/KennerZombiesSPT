using UnityEngine;
using UnityEngine.SceneManagement;

namespace KennerZombies.MapLoader.Loading
{
    // What popular graphics mods expect from a map that SDK maps otherwise don't have.
    //
    //  Amand's Graphics   finds the map's LevelSettings by object name: a fixed name per retail scene,
    //                     "!settings" for any other scene. Without it, its camera setup throws halfway
    //                     and leaves its effects half-applied. The LevelSettings object is renamed.
    //  HollywoodFX        its concussion (suppression) blur also drives the camera's UltimateBloom lens
    //                     dust. Retail camera prefabs have one; the game's fallback camera that SDK maps
    //                     use doesn't, so the effect threw after turning the blur on, before letting it
    //                     wear off: the screen stayed blurred. A disabled UltimateBloom is added.
    internal static class GraphicsModsCompat
    {
        private const string AmandsDefaultName = "!settings";

        public static void PrepareScene(Scene scene)
        {
            if (GameObject.Find(AmandsDefaultName) != null)
                return;
            foreach (var root in scene.GetRootGameObjects())
            {
                var settings = root.GetComponentInChildren<LevelSettings>(true);
                if (settings == null)
                    continue;
                Loader.Verbose($"{scene.name}: LevelSettings object '{settings.name}' renamed to '{AmandsDefaultName}' for graphics mods");
                settings.gameObject.name = AmandsDefaultName;
                return;
            }
        }

        public static void PrepareCamera(GameObject camera)
        {
            if (camera.GetComponent<UltimateBloom>() != null)
                return;
            // Awake only caches a reference; disabled, it never renders
            var bloom = camera.AddComponent<UltimateBloom>();
            bloom.enabled = false;
            Loader.Verbose("Added a disabled UltimateBloom to the fallback camera");
        }
    }
}
