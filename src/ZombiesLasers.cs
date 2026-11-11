using System.Linq;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The gun table's rule (make_guns.py): every laser/light on a Pack-a-Punched gun is ON in a zombies match, and
    /// players can't switch them (EFT's tactical toggle keys are taken by ZombiesControls). Each player's own game
    /// switches its held gun's on through EFT's own SetLightsState (so Fika shows it to the others). Plain guns are
    /// left as they come. User, 2026-11-09: the Sally's laser could be toggled and didn't come on.
    /// </summary>
    internal static class ZombiesLasers
    {
        private static float _next;

        public static void Tick(Player me)
        {
            if (!ZombiesMatch.Active || me == null || Time.time < _next)
                return;
            _next = Time.time + 0.5f;
            if (!(me.HandsController is Player.FirearmController fc) || !(fc.Item is Weapon gun) || !ZombiesPaP.Infused(gun))
                return;
            var lights = gun.GetAllItems().Select(i => i.GetItemComponent<LightComponent>()).Where(l => l != null).ToList();
            if (lights.Count == 0 || lights.All(l => l.IsActive))
                return;
            var states = lights.Select(l => new LightsState { Id = l.Item.Id.ToString(), IsActive = true, LightMode = l.SelectedMode }).ToArray();
            try
            {
                if (fc.SetLightsState(states, true, false))
                    ZombiesDebug.Log($"lasers: {lights.Count} on for the Pack-a-Punched {gun.LocalizedName()}");
            }
            catch (System.Exception e)
            {
                ZombiesDebug.Log($"lasers: couldn't switch on ({e.Message})");
                _next = Time.time + 5f;
            }
        }
    }
}
