using System;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's gun feel in a zombies match, for your own player's guns: recoil kept very low and shots far tighter than
    /// EFT's (hip fire included), and a Pack-a-Punched gun (PaP Hideout Module's infused guns) another step on, close to
    /// a beam. Recoil is cut where EFT works it out for the gun in the hands (the recoil effect's RecalculateRecoilParams
    /// OnChangeWeapon); spread where EFT asks a gun for its accuracy cone (Weapon.GetTotalCenterOfImpact). Each Fika
    /// client does its own player. F12 "5. Zombies match (dev)" has the levels. Normal raids aren't touched.
    /// </summary>
    internal static class ZombiesGuns
    {
        private static ConfigEntry<float> _recoil, _spread, _papRecoil, _papSpread;
        private static bool _patched;
        private static MethodInfo _isInfused;
        private static bool _papLooked;

        public static void Bind(ConfigFile config, string section)
        {
            _recoil = config.Bind(section, "Gun recoil x", 0.699f, new ConfigDescription(
                "Recoil of your guns in a zombies match (1 = EFT's).", new AcceptableValueRange<float>(0f, 1f)));
            _spread = config.Bind(section, "Gun spread x", 0.198f, new ConfigDescription(
                "How wide your shots spread in a zombies match (1 = EFT's).", new AcceptableValueRange<float>(0f, 1f)));
            _papRecoil = config.Bind(section, "Pack-a-Punched recoil x", 0.702f, new ConfigDescription(
                "Recoil of a Pack-a-Punched gun (1 = EFT's).", new AcceptableValueRange<float>(0f, 1f)));
            _papSpread = config.Bind(section, "Pack-a-Punched spread x", 0.7f, new ConfigDescription(
                "Spread of a Pack-a-Punched gun (1 = EFT's): close to a beam.", new AcceptableValueRange<float>(0f, 1f)));
        }

        public static void Patch(Harmony harmony)
        {
            if (_patched)
                return;
            _patched = true;
            int recoil = 0;
            foreach (Type t in AccessTools.AllTypes().Where(t => t.Name == "NewRecoilShotEffect" || t.Name == "OldRecoilShotEffect"))
            {
                MethodInfo m = AccessTools.Method(t, "RecalculateRecoilParamsOnChangeWeapon");
                if (m == null)
                    continue;
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(ZombiesGuns), nameof(LowRecoil)));
                recoil++;
            }
            harmony.Patch(AccessTools.Method(typeof(Weapon), nameof(Weapon.GetTotalCenterOfImpact)),
                postfix: new HarmonyMethod(typeof(ZombiesGuns), nameof(TightSpread)));
            ZombiesPlugin.Log.LogInfo($"Zombies guns: low recoil ({recoil} recoil effects) and tight spread");
        }

        private static bool Papped(Item gun)
        {
            if (!_papLooked)
            {
                // Looked up once, even if it throws: an exception here must never reach EFT's gun handling
                _papLooked = true;
                try
                {
                Type api = AccessTools.TypeByName("PaPHideoutModule.PaPApi");
                // IsInfused(Item) exactly: the API has other IsInfused overloads
                _isInfused = api?.GetMethod("IsInfused", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Item) }, null);
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies guns: PaP check unavailable ({e.Message})");
                }
            }
            try
            {
                return _isInfused != null && (bool)_isInfused.Invoke(null, new object[] { gun });
            }
            catch
            {
                return false;
            }
        }

        private static bool Mine(Item gun) => ZombiesMatch.Active && gun != null
            && gun.Owner?.ID == Singleton<GameWorld>.Instance?.MainPlayer?.ProfileId;

        // The recoil worked out for the gun now in the hands, cut down (its strengths and the camera kick)
        private static void LowRecoil(object __instance, Player.FirearmController firearmController)
        {
            // Runs inside EFT's gun draw: anything going wrong here is logged, never thrown into it
            try
            {
                CutRecoil(__instance, firearmController);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies guns: recoil not changed ({e.Message})");
            }
        }

        private static void CutRecoil(object __instance, Player.FirearmController firearmController)
        {
            Item gun = firearmController?.Item;
            if (firearmController == null || !firearmController._player.IsYourPlayer || !Mine(gun))
                return;
            float f = Papped(gun) ? _papRecoil.Value : _recoil.Value;
            Traverse t = Traverse.Create(__instance);
            foreach (string field in new[] { "BasicPlayerRecoilRotationStrength", "BasicPlayerRecoilPositionStrength" })
            {
                Traverse v = t.Field(field);
                if (!v.FieldExists())
                    continue;
                object value = v.GetValue();
                if (value is Vector2 v2)
                    v.SetValue(v2 * f);
                else if (value is Vector3 v3)
                    v.SetValue(v3 * f);
                else if (value is float fl)
                    v.SetValue(fl * f);
            }
            if (t.Field("ShotRecoilProcessValues").GetValue() is Array processes)
                foreach (int i in new[] { 3, 4 })
                {
                    if (i >= processes.Length)
                        continue;
                    object p = processes.GetValue(i);
                    Traverse pt = Traverse.Create(p).Field("IntensityMultiplicator");
                    if (pt.FieldExists() && pt.GetValue() is float k)
                    {
                        pt.SetValue(k * f);
                        processes.SetValue(p, i); // a struct comes back as a copy
                    }
                }
        }

        private static void TightSpread(Weapon __instance, ref float __result)
        {
            try
            {
                if (Mine(__instance))
                    __result *= (Papped(__instance) ? _papSpread.Value : _spread.Value) * ZombiesPerks.AimFactor;
            }
            catch
            {
                // EFT's own spread stays
            }
        }
    }
}
