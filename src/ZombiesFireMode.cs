using System;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Fire modes are set by the mode, not the player, in a zombies match: every gun with a full auto mode fires full
    /// auto (the rest keep their own), except the ones named in F12 "Burst guns" (the AUG, M16 and G36 by default),
    /// which fire bursts of 3 even where EFT's own gun has no burst. Switching (B) does nothing.
    /// Also the crosshair: a small one in the middle of the screen, hidden while aiming down sights.
    /// </summary>
    internal static class ZombiesFireMode
    {
        private static ConfigEntry<string> _burst;
        private static ConfigEntry<bool> _crosshair;
        private static ConfigEntry<string> _single, _auto, _burst4;
        private static float _next;
        private static Texture2D _white;

        public static void Bind(ConfigFile config, string section)
        {
            _burst = config.Bind(section, "Burst guns", "AUG,M16,G36",
                "Guns that fire 3-round bursts in a zombies match (any part of the gun's name, comma separated). Every other gun fires full auto.");
            _single = config.Bind(section, "Single fire guns", "SA58",
                "Guns that only fire single shots in a zombies match (any part of the gun's name, comma separated).");
            _burst4 = config.Bind(section, "4-round burst guns", "Glock 18",
                "Guns that fire 4-round bursts in a zombies match (any part of the gun's name, comma separated).");
            _auto = config.Bind(section, "Full auto guns", "Saiga",
                "Guns that fire full auto in a zombies match even without an auto mode of their own (any part of the name, comma separated).");
            _crosshair = config.Bind(section, "Crosshair", true, "A small crosshair in the middle of the screen in a zombies match (hidden while aiming).");
        }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.ChangeFireMode)),
                prefix: new HarmonyMethod(typeof(ZombiesFireMode), nameof(NoSwitch)));
            // A gun given bursts that has none of its own: 3 a pull
            harmony.Patch(AccessTools.PropertyGetter(typeof(FireModeComponent), nameof(FireModeComponent.BurstShotsCount)),
                postfix: new HarmonyMethod(typeof(ZombiesFireMode), nameof(BurstOfThree)));
        }

        private static bool NoSwitch(ref bool __result)
        {
            if (!ZombiesMatch.Active)
                return true;
            __result = false;
            return false;
        }

        private static void BurstOfThree(FireModeComponent __instance, ref int __result)
        {
            if (!ZombiesMatch.Active)
                return;
            if (__instance?.Item is Weapon gun && Named(gun, _burst4?.Value))
                __result = 4;
            else
                __result = 3; // The original's 3-round bursts (the G36C's own is 2)
        }

        /// <summary>Every half second: the player's guns on their mode.</summary>
        public static void Tick(Player me)
        {
            if (me == null || Time.time < _next)
                return;
            _next = Time.time + 0.5f;
            try
            {
                ZombiesAmmo.GrowPaPMags(me);
                foreach (EquipmentSlot slot in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
                    if (me.Inventory.Equipment.GetSlot(slot).ContainedItem is Weapon gun)
                        Set(gun);
            }
            catch (Exception e)
            {
                ZombiesDebug.Log($"fire modes: {e.Message}");
            }
        }

        private static void Set(Weapon gun)
        {
            FireModeComponent fm = gun.FireMode;
            // The Ray Gun and Thundergun fire as their own mods make them
            if (fm == null || ZombiesAmmo.IsWonderWeapon(gun))
                return;
            // Full auto only where the gun has it (a semi-auto pistol forced to it cycled its slide over and over);
            // the burst guns burst whatever they list
            // Marksman and sniper rifles never (the Pack-a-Punch can add an auto mode: the M1A went full auto)
            string cls = (gun.WeapClass ?? "").ToLowerInvariant();
            bool precise = cls.Contains("marksman") || cls.Contains("sniper");
            bool auto = !precise && fm.AvailableEFireModes != null && fm.AvailableEFireModes.Contains(Weapon.EFireMode.fullauto);
            Weapon.EFireMode want = IsBurst(gun) ? Weapon.EFireMode.burst : auto ? Weapon.EFireMode.fullauto : fm.FireMode;
            // The gun table's own mode for this gun, plain or Pack-a-Punched (the M16, EFT's "M4A1", bursts)
            Weapon.EFireMode? table = ZombiesGunTable.Mode(gun);
            if (table.HasValue)
                want = table.Value;
            // The burst list wins over the table (the Glock 18: 3-round bursts, not single)
            if (IsBurst(gun))
                want = Weapon.EFireMode.burst;
            // The named lists win: single-only (SA-58), full auto by force (Saiga-12), 4-round bursts (Glock 18)
            // Full auto by force on a gun EFT only lets fire single (the Saiga-12): EFT's auto mode made it rack over and
            // over, so it stays single and holding the trigger re-pulls it at its fire rate (Tick)
            if (Named(gun, _auto?.Value))
                want = fm.AvailableEFireModes != null && fm.AvailableEFireModes.Contains(Weapon.EFireMode.fullauto) ? Weapon.EFireMode.fullauto : Weapon.EFireMode.single;
            if (Named(gun, _burst4?.Value))
                want = Weapon.EFireMode.burst;
            if (Named(gun, _single?.Value))
                want = Weapon.EFireMode.single;
            // A marksman rifle left on auto by the PaP (or an earlier build) goes back to single
            if (precise && want == Weapon.EFireMode.fullauto)
                want = Weapon.EFireMode.single;
            if (fm.FireMode == want)
                return;
            // Straight onto the field: SetFireMode refuses a mode the gun doesn't list
            fm.FireMode = want;
            fm.OnChanged.Invoke();
            ZombiesPlugin.Log.LogInfo($"Zombies fire modes: {gun.LocalizedName()} on {want}");
        }

        private static float _nextPull;
        private static bool _released;

        /// <summary>
        /// Each frame of a match: a forced-auto gun EFT only fires single (the Saiga-12) keeps firing while the
        /// trigger's held, the trigger let go and pulled again at the gun's fire rate.
        /// </summary>
        public static void AutoTick(Player me)
        {
            if (!(me?.HandsController is Player.FirearmController fc) || !(fc.Item is Weapon gun) || !Named(gun, _auto?.Value)
                || gun.FireMode?.FireMode != Weapon.EFireMode.single)
                return;
            if (!Input.GetMouseButton(0) || Cursor.visible)
            {
                _released = false;
                return;
            }
            if (_released)
            {
                // Pulled again the frame after it was let go
                _released = false;
                fc.SetTriggerPressed(true);
                return;
            }
            if (Time.time < _nextPull)
                return;
            float rate = Mathf.Max(gun.SingleFireRate > 0 ? gun.SingleFireRate : gun.FireRate, 120f);
            _nextPull = Time.time + 60f / rate;
            fc.SetTriggerPressed(false);
            _released = true;
        }

        private static bool Named(Weapon gun, string list)
        {
            string names = (gun.LocalizedName() + " " + gun.Name + " " + gun.ShortName).ToUpperInvariant().Replace("-", "");
            return (list ?? "").Split(',').Select(n => n.Trim().ToUpperInvariant().Replace("-", "")).Any(n => n.Length > 0 && names.Contains(n));
        }

        private static bool IsBurst(Weapon gun)
        {
            string names = (gun.LocalizedName() + " " + gun.Name + " " + gun.ShortName).ToUpperInvariant().Replace("-", "");
            return (_burst?.Value ?? "").Split(',').Select(n => n.Trim().ToUpperInvariant().Replace("-", "")).Any(n => n.Length > 0 && names.Contains(n));
        }

        public static void DrawCrosshair(Player me)
        {
            if (_crosshair != null && !_crosshair.Value || me == null || ZombiesPause.Paused || MapEditor.IsOpen)
                return;
            float cook = ZombiesQuickGrenade.CookStarted;
            if (cook < 0f && me.ProceduralWeaponAnimation != null && me.ProceduralWeaponAnimation.IsAiming)
                return;
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            float u = Screen.height / 1080f;
            // The gap shows the hipfire spread (ZombiesHipfire): wide at rest, blooming as you fire
            float spread = ZombiesHipfire.CrosshairGap(me, ZombiesInteraction.ActiveCamera);
            // Drawn tighter than the spread itself (user, 2026-11-11: too far apart)
            float gap = Mathf.Max(3f * u, spread * 0.55f), len = 6f * u, w = Mathf.Max(1f, 2f * u);
            float cx = Screen.width / 2f, cy = Screen.height / 2f;
            Color before = GUI.color;
            // A cooking grenade: the crosshair flashes red at each second of its fuse 
            Color main = new Color(1f, 1f, 1f, 0.85f);
            if (cook >= 0f)
            {
                float into = (Time.time - cook) % 1f;
                main = into < 0.25f ? new Color(1f, 0.15f, 0.1f, 1f) : new Color(1f, 1f, 1f, 0.5f);
                if (into < 0.25f)
                {
                    gap *= 1.6f;
                    len *= 1.4f;
                }
            }
            foreach (var (c, o) in new[] { (new Color(0f, 0f, 0f, 0.6f), 1f), (main, 0f) })
            {
                GUI.color = c;
                GUI.DrawTexture(new Rect(cx - w / 2f + o, cy - gap - len + o, w, len), _white);
                GUI.DrawTexture(new Rect(cx - w / 2f + o, cy + gap + o, w, len), _white);
                GUI.DrawTexture(new Rect(cx - gap - len + o, cy - w / 2f + o, len, w), _white);
                GUI.DrawTexture(new Rect(cx + gap + o, cy - w / 2f + o, len, w), _white);
            }
            GUI.color = before;
        }
    }
}
