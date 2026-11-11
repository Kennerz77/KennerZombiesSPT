using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Dress up with no downside (the user, 2026-11-07): in a zombies match whatever a player wears costs nothing.
    /// - armour's and the backpack's speed cap, turn slowdown and ergonomics loss gone (Player.RecalculateEquipmentParams:
    ///   its Armor speed limit and mouse modifier taken off, the ergonomics penalty zeroed);
    /// - no weight: EFT's overweight limits raised far out of reach for the match (and the Weight speed limit off);
    /// - the Thundergun's own heavy-gun slowdown off too (its speed limit and mouse modifier, cause 935, Thundergun's
    ///   ThundergunHandling), so nothing changes how fast you move or turn;
    /// - the ComTac everyone wears (forced on, even under a helmet that blocks headsets) isn't drawn on anyone;
    /// - a visor or mask over the face (Altyn, gas masks...) is a look only: no visor overlay on your screen
    ///   (VisorEffect skipped), and no muffled hearing or mask breathing (EFT's Muffled state off, its breath stopped).
    /// Armour never stops a slap: ZombiesRules counts a zombie's hit before EFT's armour code sees it.
    /// EFT's own values back when the match ends.
    /// </summary>
    internal static class ZombiesGearFree
    {
        private const Player.ESpeedLimit ThundergunSpeed = (Player.ESpeedLimit)935;
        private const Player.EMouseSensitivityModifier ThundergunTurn = (Player.EMouseSensitivityModifier)935;
        private static Vector2? _walk, _base, _sprint, _walkSpeed;
        private static float _nextHide;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.RecalculateEquipmentParams)),
                postfix: new HarmonyMethod(typeof(ZombiesGearFree), nameof(EquipmentPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.SetupSoundsFromFaceShieldComponent)),
                postfix: new HarmonyMethod(typeof(ZombiesGearFree), nameof(FaceShieldPostfix)));
            harmony.Patch(AccessTools.Method(typeof(VisorEffect), nameof(VisorEffect.OnRenderImage)),
                prefix: new HarmonyMethod(typeof(ZombiesGearFree), nameof(NoVisor)));
            harmony.Patch(AccessTools.Method(typeof(BetterAudio), nameof(BetterAudio.ApplyHeadphonesTemplate)),
                prefix: new HarmonyMethod(typeof(ZombiesGearFree), nameof(SameEars)));
        }

        // Everyone hears the same in a match: whatever headset (or helmet, or none) is worn, EFT gets the ComTac VI's
        // sound. The headset worn is only a look
        private static EFT.InventoryLogic.HeadphonesTemplate _comtac;
        private static bool _earsSet;

        private static void SameEars(ref EFT.InventoryLogic.HeadphonesTemplate template)
        {
            if (!ZombiesMatch.Active)
                return;
            // "None": the game's plain hearing, as with no headset on
            if (ZombiesSwipe.Headset?.Value != ZombiesSwipe.HeadsetSound.ComTac)
            {
                if (EFT.DataProviding.DataProvider.TryGetData<EFT.ActiveHeadphones.HeadphonesLocalTemplatesContainer>(out var plain))
                    template = plain.Default;
                return;
            }
            if (_comtac == null && Singleton<ItemFactory>.Instantiated
                && Singleton<ItemFactory>.Instance.ItemTemplates.TryGetValue(ZombiesLoadout.ComtacVi, out var t))
                _comtac = t as EFT.InventoryLogic.HeadphonesTemplate;
            if (_comtac != null)
                template = _comtac;
        }

        /// <summary>EFT's headset sound worked out again (the match's ComTac in, or the player's own back after).</summary>
        private static void Reapply()
        {
            try
            {
                Player me = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
                me?.UpdatePhonesReally();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies gear: headset sound failed: {e.Message}");
            }
        }

        // The visor's screen overlay (scratches, edges, the Altyn's narrow slit): passed straight through in a match
        private static bool NoVisor(RenderTexture source, RenderTexture destination)
        {
            if (!ZombiesMatch.Active)
                return true;
            Graphics.Blit(source, destination);
            return false;
        }

        private static void FaceShieldPostfix(Player __instance)
        {
            if (ZombiesMatch.Active && __instance != null && __instance.IsYourPlayer)
                Unmuffle(__instance);
        }

        /// <summary>A face-covering visor or mask: not muffled, and the breathing it started stopped.</summary>
        private static void Unmuffle(Player p)
        {
            if (!p.Muffled)
                return;
            try
            {
                p.Muffled = false;
                p.UpdateMuffledState();
                bool wasBreathing = p.HeavyBreath;
                p.UpdateBreathStatus();
                if (wasBreathing && !p.HeavyBreath && p.Speaker != null && p.Speaker.Importance == 0)
                    p.Speaker.Shut();
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies gear: unmuffling failed: {e.Message}");
            }
        }

        private static void EquipmentPostfix(Player __instance)
        {
            if (!ZombiesMatch.Active || __instance == null || !__instance.IsYourPlayer)
                return;
            Clear(__instance);
            var field = AccessTools.Field(typeof(Player), "_ergonomicsPenalty");
            if (field != null && field.GetValue(__instance) is float pen && pen != 0f)
            {
                field.SetValue(__instance, 0f);
                __instance.ProceduralWeaponAnimation?.UpdateWeaponVariables();
            }
        }

        private static void Clear(Player p)
        {
            p.RemoveStateSpeedLimit(Player.ESpeedLimit.Armor);
            p.RemoveStateSpeedLimit(Player.ESpeedLimit.Weight);
            p.RemoveStateSpeedLimit(ThundergunSpeed);
            p.RemoveMouseSensitivityModifier(Player.EMouseSensitivityModifier.Armor);
            p.RemoveMouseSensitivityModifier(ThundergunTurn);
        }

        /// <summary>The match starts: no weight for anyone.</summary>
        public static void Apply()
        {
            if (!Singleton<GlobalConfiguration>.Instantiated)
                return;
            var st = Singleton<GlobalConfiguration>.Instance.Stamina;
            if (_walk == null)
            {
                _walk = st.WalkOverweightLimits;
                _base = st.BaseOverweightLimits;
                _sprint = st.SprintOverweightLimits;
                _walkSpeed = st.WalkSpeedOverweightLimits;
            }
            var far = new Vector2(900f, 1000f);
            st.WalkOverweightLimits = far;
            st.BaseOverweightLimits = far;
            st.SprintOverweightLimits = far;
            st.WalkSpeedOverweightLimits = far;
            ZombiesPlugin.Log.LogInfo("Zombies gear: no weight, armour or heavy-gun penalties this match");
        }

        public static void Restore()
        {
            if (_earsSet)
            {
                _earsSet = false;
                Reapply(); // ZombiesMatch.Active is off by now: the player's own headset sound
            }
            if (_walk == null || !Singleton<GlobalConfiguration>.Instantiated)
                return;
            var st = Singleton<GlobalConfiguration>.Instance.Stamina;
            st.WalkOverweightLimits = _walk.Value;
            st.BaseOverweightLimits = _base.Value;
            st.SprintOverweightLimits = _sprint.Value;
            st.WalkSpeedOverweightLimits = _walkSpeed.Value;
        }

        /// <summary>
        /// What the match puts on everyone and nobody sees: the ComTac (ears) and the hidden pack (backpack slot).
        /// </summary>
        private static bool Hidden(Player p, EFT.InventoryLogic.EquipmentSlot slot)
        {
            // Not the rig any more: no rig is handed out now (the pockets hold it all), so an LBT-1961A on a player is
            // their own look. Hiding it by template hid a player's own LBT, which EFT showed again for each reload (seen
            // by the others as the rig popping in and out, user 2026-11-10)
            if (slot != EFT.InventoryLogic.EquipmentSlot.Earpiece && slot != EFT.InventoryLogic.EquipmentSlot.Backpack)
                return false;
            string tpl = p.Equipment?.GetSlot(slot)?.ContainedItem?.TemplateId.ToString();
            // The given ComTac only (a player's own headset is their look)
            return slot == EFT.InventoryLogic.EquipmentSlot.Earpiece ? tpl == ZombiesLoadout.ComtacVi : tpl == ZombiesLoadout.Pack;
        }

        /// <summary>Each frame of a match: your own penalties kept off; each second, the hidden kit hidden on everyone.</summary>
        public static void Tick(Player me)
        {
            if (me != null && ZombiesInertia.Tweaks)
                Clear(me);
            // Put on before the match started (the observer's update came before ZombiesMatch.Active)
            if (me != null)
                Unmuffle(me);
            if (me != null && !_earsSet)
            {
                _earsSet = true;
                Reapply();
            }
            if (Time.time < _nextHide || !Singleton<GameWorld>.Instantiated)
                return;
            _nextHide = Time.time + 1f;
            foreach (Player p in Singleton<GameWorld>.Instance.AllAlivePlayersList)
            {
                if (p == null || !ZombiesTeam.IsHuman(p) || p.PlayerBody?.SlotViews == null)
                    continue;
                foreach (PlayerBody.SlotView view in p.PlayerBody.SlotViews.GetValuesEnumerator())
                    if (view != null && view.Model != null && Hidden(p, view._equipmentSlot))
                        foreach (Renderer r in view.Model.GetComponentsInChildren<Renderer>(true))
                            if (r.enabled)
                                r.enabled = false;
            }
        }
    }
}
