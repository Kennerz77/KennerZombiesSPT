using EFT.Weather;
using UnityEngine;

namespace KennerZombies.MapLoader.Loading
{
    // The trees' and wires' wind on maps without the full weather system. The game's WindController (part of the full
    // weather) sets the wind globals from the weather every frame; the simple time-of-day rig has none, so they'd keep
    // whatever the last raid left, or nothing: Nature/SpeedTreeEFT normalizes _ST_WindVector unchecked, and a zero
    // vector turns every swaying tree's vertices into NaN (the tree disappears). This sets what WindController's own
    // Awake sets, its default wind (0.05, 0.1), through the same two methods (ApplyWindToPlants, WiresController.Update),
    // whenever one of our scenes loads and no WindController is awake; one that wakes later takes over.
    internal static class WindFallback
    {
        private static readonly Vector2 DefaultWind = new Vector2(0.05f, 0.1f);

        public static void Apply()
        {
            if (Object.FindObjectOfType<WindController>() != null)
                return;
            // ApplyWindToPlants
            var direction = DefaultWind.normalized;
            float magnitude = Mathf.Clamp(DefaultWind.magnitude, 0.0001f, 0.99999f);
            var wind = direction * magnitude;
            Shader.SetGlobalVector("_WindVector", wind);
            Shader.SetGlobalVector("_PrevWindVector", wind);
            Shader.SetGlobalVector("_ST_WindVector", new Vector4(direction.x, 0f, direction.y, magnitude));
            // no WindCalculator: the shaders blend each tree's property block themselves
            Shader.SetGlobalFloat("_WindCalculated", 0f);
            // WiresController.Update with its default settings
            new WiresController().Update(DefaultWind);
            Loader.Verbose("Wind: no WindController in the map; the game's default wind is set for trees and wires");
        }
    }
}
