using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace KennerZombies.MapLoader.Loading
{
    // SAIN (me.sol.sain) only knows retail locations. On any other map its LocationClass.parseLocation
    // logs an error and its findLocation tries again every update for the whole raid: tens of thousands
    // of errors, enough to stutter the game, and SAIN's per-map settings are never found.
    //
    // On a custom map this answers findLocation with the retail map SAIN should treat it as (config
    // "SAIN map type", default Customs) and marks the location found. Found by reflection, so nothing
    // here needs SAIN to be installed.
    internal class SainLocationPatch : ModulePatch
    {
        private static Type _locationClass;
        private static Type _locationEnum;
        private static FieldInfo _found;
        private static PropertyInfo _location;

        public static bool Available()
        {
            _locationClass = AccessTools.TypeByName("SAIN.Components.LocationClass");
            if (_locationClass == null)
                return false;
            _found = AccessTools.Field(_locationClass, "_foundLocation");
            _location = AccessTools.Property(_locationClass, "Location");
            _locationEnum = _location?.PropertyType;
            return _found != null && _location != null && _locationEnum != null && _locationEnum.IsEnum
                   && AccessTools.Method(_locationClass, "findLocation") != null;
        }

        protected override MethodBase GetTargetMethod() => AccessTools.Method(_locationClass, "findLocation");

        [PatchPrefix]
        private static bool Prefix(object __instance)
        {
            try
            {
                if ((bool)_found.GetValue(__instance))
                    return false;
                var id = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.LocationId : null;
                if (string.IsNullOrEmpty(id) || !MapRegistry.MapIds.Contains(id))
                    return true;
                var name = Loader.SainMapType;
                if (!Enum.IsDefined(_locationEnum, name))
                {
                    Loader.Log.LogWarning($"SAIN has no map type '{name}'; using Customs. It knows: {string.Join(", ", Enum.GetNames(_locationEnum))}");
                    name = "Customs";
                }
                _location.SetValue(__instance, Enum.Parse(_locationEnum, name));
                _found.SetValue(__instance, true);
                Loader.Verbose($"SAIN treats custom map '{id}' as {name}");
                return false;
            }
            catch (Exception e)
            {
                Loader.Log.LogError($"SAIN map type failed: {e.Message}");
                return true;
            }
        }
    }
}
