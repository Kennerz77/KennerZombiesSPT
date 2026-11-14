using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// The original's last stand in a zombies match, for your own player:
    /// - Solo without Quick Revive, the last slap is game over at once (the original: no last stand alone without it).
    /// - Down (the last slap): you're forced prone (no standing, sprinting or jumping) and crawl. Whatever you held, you get
    ///   the Ray Gun with 20 shots if you carry one, else the M1911 with 7 rounds, and no reserve. The screen reddens
    ///   while you bleed out (45 s); a bar shows the time left.
    /// - Revived (a teammate within 2.5 m sees EFT's "F Revive" prompt and holds F 4 s, 2 s with Quick Revive, as a bar; or solo with Quick Revive by
    ///   yourself after 10 s, 3 times a match, then the Quick Revive machine rises and goes in a flash of lightning):
    ///   up again, slaps cleared, your guns and ammo as they were.
    /// - Bled out: with teammates still standing you're out until the next round, watching a teammate first person (left
    ///   and right click switch who); your body stays where it fell. Solo, or the last one up, it's game over (the raid
    ///   ends and the server puts the character back).
    /// Downed teammates show the original's revive icon over their tag, white to orange to deep red as they bleed out, filling while
    /// someone revives them. Other players' downed state needs the Fika match state (to come); F12 "12. Debug: health and
    /// slaps" has a dummy downed teammate to try reviving, and buttons for every step.
    /// </summary>
    internal static class ZombiesDowned
    {
        public enum State { Up, Down, Dead }

        private const string RayGunTpl = "6a7e5a71d9a1160000000001";
        internal const string FullCellTpl = "6a7e5a71d9a11600000000ca"; // the Ray Gun's 10-shot cold fusion cell
        private const string M1911Tpl = "5e81c3cbac2bb513793cdc75";
        // Close enough to revive: generous, so the prompt is easy to catch (no need to aim at them)
        private const float ReviveRange = 2.5f;

        public static ConfigEntry<float> BleedOutTime, ReviveTime, QuickReviveTime, SoloSelfRevive;
        public static ConfigEntry<bool> PretendQuickRevive;
        public static ConfigEntry<int> RevivePoints;

        public static State Mine { get; private set; } = State.Up;
        private static float _downAt;
        private static int _soloRevives;
        private static bool _patched;

        // What the last stand took, to give back
        private static Weapon _standGun;
        private static bool _standGunIsTemp;
        private static Item _heldBefore;
        private static object _ammoHeld, _raygunHeld;
        private static string[] _cellsHeld;

        // Spectating (dead with teammates up)
        private static int _watching;

        // F12's dummy downed teammate: a place to revive, bleeding out like a real one
        private static Vector3? _dummy;
        private static float _dummyDownAt, _reviveStart = -1f;

        public static void Bind(ConfigFile config, string section)
        {
            BleedOutTime = config.Bind(section, "Bleed-out time (s)", 45f, new ConfigDescription(
                "How long a downed player has before bleeding out.", new AcceptableValueRange<float>(5f, 120f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 80 }));
            ReviveTime = config.Bind(section, "Revive time (s)", 4f, new ConfigDescription(
                "How long the interact key is held to revive someone.", new AcceptableValueRange<float>(0.5f, 10f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 79 }));
            QuickReviveTime = config.Bind(section, "Revive time with Quick Revive (s)", 2f, new ConfigDescription(
                "The same, for a reviver with Quick Revive.", new AcceptableValueRange<float>(0.5f, 10f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 78 }));
            SoloSelfRevive = config.Bind(section, "Solo Quick Revive gets you up after (s)", 4f, new ConfigDescription(
                "Solo with Quick Revive: how long you're down before getting up by yourself.", new AcceptableValueRange<float>(1f, 30f), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 77 }));
            RevivePoints = config.Bind(section, "Points for a revive", 100, new ConfigDescription(
                "Points the reviver gets for bringing a teammate back up (Double Points doubles it).", new AcceptableValueRange<int>(0, 2000), new ZombiesPlugin.ConfigurationManagerAttributes { Order = 75 }));
            PretendQuickRevive = config.Bind(section, "Give me Quick Revive (pretend)", false, new ConfigDescription(
                "Acts as if you'd drunk Quick Revive (Perk Beverages hasn't got it working yet).", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 76 }));
        }

        public static void Reset()
        {
            ExitSpectate();
            Mine = State.Up;
            _teamOverPending = false;
            _soloRevives = 0;
            SoloBuys = 0;
            ZombiesLitter.Reset();
            _machineGone = false;
            _boughtQuickRevive = false;
            _downWithQuickRevive = false;
            _standGun = null;
            _heldBefore = null;
            _lastGun = null;
            _ammoHeld = null;
            _raygunHeld = null;
            _cellsHeld = null;
            _dummy = null;
            _reviveStart = -1f;
        }

        public static bool HasQuickRevive() => PretendQuickRevive?.Value == true || _boughtQuickRevive || ZombiesSlaps.HasPerk("revive");

        // Quick Revive bought off the machine (going down uses it up, as in the original: it's bought again)
        private static bool _boughtQuickRevive, _downWithQuickRevive;

        /// <summary>Solo, all three Quick Revives used: the machine has gone for the rest of the match.</summary>
        public static bool QuickReviveGone => _soloRevives >= 3 || _machineGone;
        private static bool _machineGone;

        /// <summary>Solo Quick Revive buys so far this match (its price climbs: 500, 1500, 2500).</summary>
        public static int SoloBuys { get; private set; }

        /// <summary>A Quick Revive bought (solo, the next one costs more).</summary>
        public static void CountQuickReviveBuy()
        {
            if (!Solo)
                return;
            SoloBuys++;
            // The last one bought (the user, 2026-11-07): the machine laughs, rises and goes in a flash of lightning
            if (SoloBuys >= 3)
                ZombiesPlugin.Instance.StartCoroutine(LeaveAfterBuy());
        }

        private static IEnumerator LeaveAfterBuy()
        {
            yield return new WaitForSeconds(2f);
            QuickReviveMachineLeaves();
        }

        /// <summary>Quick Revive had (without Perk Beverages' drink: at once; with it, Perk Beverages holds it).</summary>
        public static void GiveQuickRevive()
        {
            _boughtQuickRevive = true;
            ZombiesPlugin.Log.LogInfo("Zombies: Quick Revive bought");
            // The original has its own line for it, as for the other perks (the user's video: right after the drink)
            ZombiesVox.Perk("revive");
        }

        private static bool Solo => ZombiesTeam.List().Count <= 1;

        // ---------- Going down ----------

        /// <summary>The last slap: down (ZombiesSlaps).</summary>
        public static void GoDown(Player player)
        {
            if (Mine != State.Up || player == null)
                return;
            ZombiesVox.Say("down", 1f, 3f);
            Patch();
            // Going down uses Quick Revive up and loses every perk 
            _downWithQuickRevive = HasQuickRevive();
            _boughtQuickRevive = false;
            ZombiesPerks.LoseAll();
            // The original solo: without Quick Revive there's no last stand, it's game over at once
            if (Solo && !_downWithQuickRevive)
            {
                ZombiesPlugin.Log.LogInfo("Zombies: down solo without Quick Revive, game over");
                ZombiesStats.Mine.Downs++;
                Mine = State.Dead;
                // The original's game over (the shots, the board, the song), then the raid ends
                ZombiesGameOver.BeginAfterFall(player, "down solo without Quick Revive");
                return;
            }
            Mine = State.Down;
            _downAt = Time.time;
            ZombiesStats.Mine.Downs++;
            ZombiesPlugin.Log.LogInfo($"Zombies: down{(Solo && _downWithQuickRevive && _soloRevives < 3 ? $" (Quick Revive: up in {SoloSelfRevive.Value:0} s)" : "")}");
            if (!player.IsInPronePose)
                player.ToggleProne();
            player.EnableSprint(false);
            player.StartCoroutine(TakeLastStandGun(player));
            // The new down count goes out first (messages arrive in order): if this down ends the match, every game's
            // board already has it (user, 2026-11-11: the down that ended the game wasn't on the scoreboard)
            ZombiesNet.SendStatsNow();
            ZombiesNet.Down();
            // The last one up going down with everyone else down or dead: game over for the team
            if (ZombiesNet.Online)
                CheckEveryoneDown();
        }

        private static IEnumerator TakeLastStandGun(Player player)
        {
            InventoryController inventory = player.InventoryController;
            _heldBefore = player.HandsController?.Item;
            Item[] carried = inventory.Inventory.Equipment.GetAllItems().ToArray();
            Weapon gun = carried.OfType<Weapon>().FirstOrDefault(w => w.TemplateId.ToString() == RayGunTpl);
            _standGunIsTemp = false;
            if (gun == null)
            {
                // Always a fresh M1911 just for the last stand (into the holster), so your own one keeps its rounds and
                // its slide, and the stand's one goes away again when you're up
                Weapon[] before = carried.OfType<Weapon>().ToArray();
                BoxGuns.Gun m1911 = BoxGuns.FindGun("m1911");
                if (m1911 != null)
                    yield return BoxGive.Give(m1911, player, lastStand: true);
                gun = inventory.Inventory.Equipment.GetAllItems().OfType<Weapon>()
                    .FirstOrDefault(w => w.TemplateId.ToString() == M1911Tpl && !before.Contains(w));
                _standGunIsTemp = gun != null;
                // No room in the holster: your own M1911 then
                if (gun == null)
                    gun = carried.OfType<Weapon>().FirstOrDefault(w => w.TemplateId.ToString() == M1911Tpl);
            }
            if (gun == null)
            {
                ZombiesPlugin.Log.LogWarning("Zombies: no last stand gun to give");
                yield break;
            }
            _standGun = gun;
            if (gun.TemplateId.ToString() == RayGunTpl)
            {
                _cellsHeld = gun.Chambers.Select(c => c.ContainedItem?.TemplateId.ToString()).ToArray();
                foreach (Slot chamber in gun.Chambers)
                    SetChamber(chamber, FullCellTpl);
                // Only those 20 shots: no reserve, no spare cells to reload with, until you're up
                _raygunHeld = ZombiesAmmo.HoldRaygunForLastStand(player, gun);
            }
            else
                _ammoHeld = ZombiesAmmo.HoldForLastStand(player, gun, 7);
            if (player.HandsController?.Item != gun)
                player.SetInHands(gun, _ => { });
            ZombiesPlugin.Log.LogInfo($"Zombies: last stand with the {gun.LocalizedName()}");
        }

        internal static void SetChamber(Slot chamber, string tpl)
        {
            if (chamber.ContainedItem != null)
                chamber.RemoveItem();
            if (tpl != null && Singleton<ItemFactory>.Instance?.CreateItem(MongoID.Generate(), tpl, null) is Item cell)
                chamber.AddWithoutRestrictions(cell);
        }

        // ---------- Up again ----------

        /// <summary>Revived (a teammate, solo Quick Revive, or F12).</summary>
        public static void Revive(Player player, string how, bool swapBack = true)
        {
            if (Mine != State.Down || player == null)
                return;
            Mine = State.Up;
            _beingRevivedStart = -1f;
            ZombiesSlaps.Reset();
            // A moment to get out of trouble after the revive (user, 2026-11-11)
            ZombiesSlaps.SafeUntil = Time.time + ZombiesSlaps.Grace;
            if (_standGun != null)
            {
                if (_cellsHeld != null)
                    for (int i = 0; i < _standGun.Chambers.Length && i < _cellsHeld.Length; i++)
                        SetChamber(_standGun.Chambers[i], _cellsHeld[i]);
                if (_ammoHeld != null)
                    ZombiesAmmo.ReleaseLastStand(player, _standGun, _ammoHeld);
                if (_raygunHeld != null)
                    ZombiesAmmo.ReleaseRaygunLastStand(_standGun, _raygunHeld);
            }
            // Back to the gun held before (a knife in the hands is left to the player); the stand's own M1911 goes once
            // it's out of the hands, so the swap is a normal draw, not a gun vanishing mid-hand
            Weapon temp = _standGunIsTemp ? _standGun : null;
            // The gun last held while up (user, 2026-11-09: revived, it should be out), else what was in the hands
            Weapon back = _lastGun ?? _heldBefore as Weapon;
            if (back != null && (back == _standGun || !player.InventoryController.Inventory.Equipment.GetAllItems().Contains(back)))
                back = _heldBefore as Weapon;
            if (back != null && (back == _standGun || !player.InventoryController.Inventory.Equipment.GetAllItems().Contains(back)))
                back = null;
            if (back == null && temp != null)
                back = player.InventoryController.Inventory.Equipment.GetAllItems().OfType<Weapon>().FirstOrDefault(w => w != temp);
            // A respawn hands out its own kit (RespawnKit): no draw of the old gun here, it's about to be taken away (user
            // report, 2026-11-14: a friend who bled out came back next round with no gun and couldn't do anything: this
            // draw and the respawn's emptying the hands and taking that same gun ran at once)
            if (swapBack)
                ZombiesPlugin.Instance.StartCoroutine(SwapBack(player, back, temp));
            _standGun = null;
            _cellsHeld = null;
            _ammoHeld = null;
            _raygunHeld = null;
            _heldBefore = null;
            if (player.IsInPronePose)
                player.ToggleProne();
            ZombiesPlugin.Log.LogInfo($"Zombies: revived ({how})");
            ZombiesVox.Say("revived", 0.9f, 3f);
            ZombiesNet.Up();
        }

        private static IEnumerator SwapBack(Player player, Weapon back, Weapon temp)
        {
            // Up from the prone first, then the gun (EFT may refuse a draw mid pose change): tried 3 times
            for (int attempt = 0; back != null && attempt < 3 && player.HandsController?.Item != back; attempt++)
            {
                yield return new WaitForSecondsRealtime(attempt == 0 ? 0.3f : 0.8f);
                bool done = false;
                try { player.SetInHands(back, _ => done = true); } catch { done = true; }
                for (float until = Time.realtimeSinceStartup + 3f; !done && Time.realtimeSinceStartup < until; )
                    yield return null;
            }
            if (back != null && player.HandsController?.Item != back)
                ZombiesPlugin.Log.LogWarning($"Zombies: revived but the {back.LocalizedName()} wouldn't come out");
            else if (back != null)
            {
            }
            else if (temp != null && player.HandsController?.Item == temp)
            {
                bool done = false;
                try { player.SetEmptyHands(_ => done = true); } catch { done = true; }
                for (float until = Time.realtimeSinceStartup + 3f; !done && Time.realtimeSinceStartup < until; )
                    yield return null;
            }
            if (temp == null || player.HandsController?.Item == temp)
                yield break;
            try
            {
                var removed = ItemManipulator.Remove(temp, player.InventoryController, false, true);
                ZombiesItems.Removed(player.InventoryController, removed, temp);
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: last stand M1911 not removed ({e.Message})");
            }
        }

        // ---------- Bleeding out ----------

        public static void BleedOut(Player player)
        {
            if (Mine != State.Down || player == null)
                return;
            ZombiesNet.Dead();
            bool othersUp = ZombiesTeam.List().Any(p => p != null && !p.IsYourPlayer && p.HealthController.IsAlive
                                                        && ZombiesNet.StateOf(p.ProfileId) == ZombiesNet.State.Up);
            if (!othersUp)
            {
                // Solo, or nobody left standing: game over (the server puts the character back)
                ZombiesPlugin.Log.LogInfo("Zombies: bled out, game over");
                Mine = State.Dead;
                ZombiesGameOver.Begin(player, "bled out, nobody left standing");
                return;
            }
            Mine = State.Dead;
            _watching = 0;
            EnterSpectate(player);
            ZombiesPlugin.Log.LogInfo("Zombies: bled out, spectating until the next round");
        }

        /// <summary>Back in (the next round's start, or F12): at your spawn, standing, with your kit as you left it.</summary>
        public static void Respawn(Player player)
        {
            if (Mine != State.Dead || player == null || !player.HealthController.IsAlive)
                return;
            ExitSpectate();
            Mine = State.Down;
            Revive(player, "respawned", swapBack: false);
            ZombiesPlugin.Instance.StartCoroutine(RespawnKit(player));
        }

        /// <summary>Points a bled-out player comes back with at least (the original: back in with the starting pistol and some points).</summary>
        private const int RespawnPoints = 1000;

        // The original's way back after bleeding out: at your own spawn, your guns gone, the starting M1911, and some points
        private static System.Collections.IEnumerator RespawnKit(Player player)
        {
            if (ZombiesMatch.StartSpot is Vector3 spot)
                player.Teleport(spot, false);
            bool emptied = false;
            try { player.SetEmptyHands(_ => emptied = true); } catch { emptied = true; }
            float until = Time.realtimeSinceStartup + 4f;
            while (!emptied && Time.realtimeSinceStartup < until)
                yield return null;
            until = Time.realtimeSinceStartup + 2f;
            while (!(player.HandsController is Player.EmptyHandsController) && Time.realtimeSinceStartup < until)
                yield return null;
            // Co-op: the other games' copies of us put the gun away too before it's taken off the slot (as the starting
            // kit does, ZombiesLoadout): a gun taken while a copy still held it made that game refuse every draw after
            if (ZombiesNet.Online)
            {
                if (!ZombiesNet.IsHost && ZombiesNet.HostHandsHeard)
                {
                    float most = Time.realtimeSinceStartup + 10f;
                    while (ZombiesNet.HostSeesMe() != "" && Time.realtimeSinceStartup < most)
                        yield return null;
                    if (ZombiesNet.HostSeesMe() != "")
                        ZombiesPlugin.Log.LogWarning($"Zombies: respawn: the host's copy of us still holds {ZombiesNet.HostSeesMe()} after 10 s: going on anyway");
                }
                yield return new WaitForSecondsRealtime(1f);
            }
            foreach (EquipmentSlot s in new[] { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster })
            {
                Item gun = player.InventoryController.Inventory.Equipment.GetSlot(s).ContainedItem;
                if (gun == null)
                    continue;
                try
                {
                    var removed = ItemManipulator.Remove(gun, player.InventoryController, false, true);
                    if (removed.Succeeded)
                        ZombiesItems.Removed(player.InventoryController, removed, gun);
                }
                catch (Exception e)
                {
                    ZombiesPlugin.Log.LogWarning($"Zombies: respawn couldn't take the {gun.LocalizedName()} ({e.Message})");
                }
            }
            // The monkeys go with the guns : the box can give them again
            ZombiesMonkey.TakeAll(player);
            // Grenades back to the starting two (the original: a respawn is the starting kit; a Max Ammo had filled them to 4)
            ZombiesGrenades.TrimTo(player, ZombiesGrenades.Start);
            yield return new WaitForSecondsRealtime(0.3f);
            ZombiesMatch.RestorePoints(RespawnPoints);
            BoxGuns.Gun starter = ZombiesLoadout.Starter;
            if (starter != null)
                yield return BoxGive.Give(starter, player);
            ZombiesPlugin.Log.LogInfo($"Zombies: respawned with the {starter?.Name ?? "starting gun"}");
            // The gun must end up in the hands: if it didn't come out, it's drawn past whatever held EFT up
            string tpl = starter != null ? BoxGuns.Template(starter) : null;
            Weapon given = tpl == null ? null : player.InventoryController.Inventory.Equipment.GetAllItems().OfType<Weapon>()
                .FirstOrDefault(w => w.TemplateId.ToString() == tpl);
            float waitHands = Time.realtimeSinceStartup + 4f;
            while (given != null && player.HandsController?.Item != given && Time.realtimeSinceStartup < waitHands)
                yield return null;
            if (given != null && player.HandsController?.Item != given)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: respawn: the {given.LocalizedName()} didn't come out (hands: {ZombiesTrace.HandsText(player)}): drawing it again");
                yield return ZombiesReload.ForceDraw(player, given);
            }
        }

        /// <summary>
        /// Everyone's down or dead (each game checks as the others' states arrive): the game over for all, then the menu.
        /// Solo it's the bleed-out's own game over.
        /// </summary>
        public static void CheckEveryoneDown()
        {
            if (!ZombiesMatch.Active || ZombiesGameOver.Active || Mine == State.Up || _teamOverPending)
                return;
            Player me = Singleton<GameWorld>.Instance?.MainPlayer;
            // Solo (Fika counts as online even alone) keeps its own rules: self-revive, the bleed-out's game over
            if (me == null || ZombiesTeam.List().Count < 2)
                return;
            bool anyoneUp = ZombiesTeam.List().Any(p => p != null && !p.IsYourPlayer && p.HealthController.IsAlive
                                                        && ZombiesNet.StateOf(p.ProfileId) == ZombiesNet.State.Up);
            if (anyoneUp)
                return;
            ZombiesPlugin.Log.LogInfo("Zombies: the whole team is down: game over after the last one's fall");
            _teamOverPending = true;
            ZombiesPlugin.Instance.StartCoroutine(TeamGameOver(me));
        }

        // The whole team down: a moment for the last one to drop into their downed pose (seen by everyone, a spectator
        // too), then the same fall as solo (red, into black) and the game over fading in (user, 2026-11-11: it cut
        // straight to the game over)
        private const float TeamOverDelay = 1.5f;
        private static bool _teamOverPending;

        private static IEnumerator TeamGameOver(Player me)
        {
            yield return new WaitForSecondsRealtime(TeamOverDelay);
            _teamOverPending = false;
            if (!ZombiesMatch.Active || ZombiesGameOver.Active || me == null)
                yield break;
            ExitSpectate();
            Mine = State.Dead;
            ZombiesGameOver.BeginAfterFall(me, "the whole team is down");
        }

        // ---------- Every frame ----------

        // The last gun this player held while standing (the one drawn again after a revive)
        private static Weapon _lastGun;

        public static void Tick(Player me)
        {
            if (me == null)
                return;
            if (Mine == State.Up && me.HandsController?.Item is Weapon held && held != _standGun)
                _lastGun = held;
            if (Mine == State.Down)
            {
                if (!me.IsInPronePose && Time.time - _downAt > 0.5f)
                    me.ToggleProne();
                // The last stand gun stays in the hands
                if (_standGun != null && me.HandsController?.Item != _standGun
                    && Time.frameCount % 30 == 0)
                    me.SetInHands(_standGun, _ => { });
                // The original: bleeding out stops while a teammate revives you (a stale start, 1 s past its length, is dropped)
                if (_beingRevivedStart >= 0f)
                {
                    if (Time.time - _beingRevivedStart > _beingRevivedFor + 1f)
                        _beingRevivedStart = -1f;
                    else
                        _downAt += Time.deltaTime;
                }
                float down = Time.time - _downAt;
                if (Solo && _downWithQuickRevive && _soloRevives < 3 && down >= SoloSelfRevive.Value)
                {
                    _soloRevives++;
                    Revive(me, $"Quick Revive {_soloRevives}/3");
                    if (_soloRevives >= 3)
                        QuickReviveMachineLeaves();
                }
                else if (down >= BleedOutTime.Value)
                    BleedOut(me);
            }
            TickDummy(me);
            TickTeam(me);
        }

        // ---------- Being revived (shown to the one down: the bar and who) ----------

        private static string _reviverName;
        private static float _beingRevivedStart = -1f, _beingRevivedFor;

        /// <summary>A teammate started (seconds &gt; 0) or stopped (0) reviving you.</summary>
        public static void BeingRevived(string fromProfile, float seconds)
        {
            if (seconds <= 0f || Mine != State.Down)
            {
                _beingRevivedStart = -1f;
                return;
            }
            _reviverName = ZombiesTeam.List().FirstOrDefault(p => p != null && p.ProfileId == fromProfile)?.Profile?.Nickname ?? "";
            _beingRevivedStart = Time.time;
            _beingRevivedFor = seconds;
        }

        // The revive bar while down: a teammate's (with their name), or solo Quick Revive's own (no name)
        private static bool RevivedNow(out float fill, out string by)
        {
            fill = 0f;
            by = "";
            if (Mine != State.Down)
                return false;
            if (_beingRevivedStart >= 0f)
            {
                fill = Mathf.Clamp01((Time.time - _beingRevivedStart) / Mathf.Max(0.1f, _beingRevivedFor));
                by = _reviverName ?? "";
                return true;
            }
            if (Solo && _downWithQuickRevive && _soloRevives < 3)
            {
                fill = Mathf.Clamp01((Time.time - _downAt) / Mathf.Max(0.1f, SoloSelfRevive.Value));
                return true;
            }
            return false;
        }

        // ---------- A real teammate down (ZombiesNet): hold F by them to bring them up ----------

        private static string _revivingWho;
        private static float _teamReviveStart = -1f;

        private static IEnumerable<(Player player, float since)> DownTeammates() =>
            ZombiesTeam.List().Where(p => p != null && !p.IsYourPlayer && ZombiesNet.StateOf(p.ProfileId) == ZombiesNet.State.Down)
                .Select(p => (p, ZombiesNet.Downed[p.ProfileId].since));

        private static void TickTeam(Player me)
        {
            Player near = Mine == State.Up && Input.GetKey(ZombiesControls.Key(ZombiesControls.Act.Use))
                ? DownTeammates().Select(d => d.player).FirstOrDefault(p => Vector3.Distance(me.Transform.position, p.Transform.position) <= ReviveRange)
                : null;
            if (near == null)
            {
                if (_revivingWho != null)
                    ZombiesNet.Reviving(_revivingWho, 0f);
                _revivingWho = null;
                _teamReviveStart = -1f;
                GunBack(me);
                return;
            }
            if (_revivingWho != near.ProfileId)
            {
                if (_revivingWho != null)
                    ZombiesNet.Reviving(_revivingWho, 0f);
                _revivingWho = near.ProfileId;
                _teamReviveStart = Time.time;
                ZombiesNet.Reviving(near.ProfileId, ReviveSeconds());
                GunAway(me);
            }
            if (Time.time - _teamReviveStart >= ReviveSeconds())
            {
                ZombiesPlugin.Log.LogInfo($"Zombies: revived {near.Profile?.Nickname}");
                ZombiesVox.Say("revive_player", 0.9f, 5f);
                ZombiesStats.Mine.Revives++;
                // Points for the revive (user, 2026-11-09; F12 "Points for a revive")
                if (RevivePoints != null && RevivePoints.Value > 0)
                    ZombiesMatch.AddPoints(RevivePoints.Value);
                ZombiesNet.Revive(near.ProfileId);
                ZombiesNet.Downed.Remove(near.ProfileId);
                _revivingWho = null;
                _teamReviveStart = -1f;
                GunBack(me);
            }
        }

        // The gun put away while reviving , drawn again when the revive ends or stops (user, 2026-11-09)
        private static bool _gunAway;

        /// <summary>The gun is put away for a revive: hands commands are held off meanwhile (ZombiesControls).</summary>
        public static bool GunIsAway => _gunAway;

        private static void GunAway(Player me)
        {
            if (_gunAway)
                return;
            _gunAway = true;
            try
            {
                me.SetEmptyHands(_ => { });
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: gun not put away for the revive ({e.Message})");
            }
        }

        private static void GunBack(Player me)
        {
            if (!_gunAway)
                return;
            _gunAway = false;
            if (Mine != State.Up)
                return;
            // Only from empty hands: something already in them (drawn meanwhile) is left as it is
            if (!(me.HandsController is Player.EmptyHandsController))
                return;
            try
            {
                me.TrySetLastEquippedWeapon(true, _ => { });
            }
            catch (Exception e)
            {
                ZombiesPlugin.Log.LogWarning($"Zombies: gun not drawn back after the revive ({e.Message})");
            }
        }

        /// <summary>Got out at the exfil: watch the others (left / right click) until the match ends.</summary>
        public static void SpectateAfterExfil(Player me)
        {
            _exfilSpectate = true;
            EnterSpectate(me);
        }

        private static bool _exfilSpectate;

        public static void EndExfilSpectate()
        {
            if (!_exfilSpectate)
                return;
            _exfilSpectate = false;
            ExitSpectate();
        }

        public static void LateTick()
        {
            if (Mine != State.Dead && !_exfilSpectate || !_spectating)
                return;
            // Left / right click: the next / the previous teammate still standing
            if (Input.GetMouseButtonDown(0))
                _watching++;
            if (Input.GetMouseButtonDown(1))
                _watching--;
        }

        // ---------- Spectating after bleeding out (user, 2026-11-09: the bled-out host could still crawl and shoot,
        // and the clicks did nothing: EFT's own camera follow put the view back on the body every frame) ----------
        // The body stays where it fell; this player's controls and EFT's camera follow are off until the respawn, and the
        // camera rides in the watched teammate's eyes, posed just before each frame is drawn (as the game over does).

        private static bool _spectating;
        private static GamePlayerOwner _specOwner;
        private static PlayerCameraController _specFollow;

        private static List<Player> Watchable() =>
            ZombiesTeam.List().Where(p => p != null && !p.IsYourPlayer && p.HealthController.IsAlive
                                          && ZombiesNet.StateOf(p.ProfileId) != ZombiesNet.State.Dead).ToList();

        /// <summary>Who is being watched now (for the HUD), or null.</summary>
        public static string WatchingName
        {
            get
            {
                var team = Watchable();
                return !_spectating || team.Count == 0 ? null : team[((_watching % team.Count) + team.Count) % team.Count].Profile?.Nickname;
            }
        }

        private static void EnterSpectate(Player me)
        {
            if (_spectating || me == null)
                return;
            _spectating = true;
            try
            {
                me.SetEmptyHands(_ => { });
            }
            catch
            {
            }
            _specOwner = ((Component)me).GetComponent<GamePlayerOwner>();
            _specFollow = ((Component)me).GetComponent<PlayerCameraController>();
            if (_specOwner != null)
                _specOwner.enabled = false;
            if (_specFollow != null)
                _specFollow.enabled = false;
            Application.onBeforeRender += PoseSpectator;
        }

        private static void ExitSpectate()
        {
            if (!_spectating)
                return;
            _spectating = false;
            _specLogged = null;
            Application.onBeforeRender -= PoseSpectator;
            if (_specOwner != null)
                _specOwner.enabled = true;
            if (_specFollow != null)
                _specFollow.enabled = true;
            _specOwner = null;
            _specFollow = null;
        }

        private static string _specLogged;

        private static void PoseSpectator()
        {
            if (!_spectating || ZombiesGameOver.Active)
                return;
            var team = Watchable();
            Camera cam = ZombiesInteraction.ActiveCamera;
            if (team.Count == 0 || cam == null)
                return;
            Player target = team[((_watching % team.Count) + team.Count) % team.Count];
            // Logger (user to confirm the chase cam, 2026-11-10): who is watched, once per change
            if (target.ProfileId != _specLogged)
            {
                _specLogged = target.ProfileId;
                ZombiesPlugin.Log.LogInfo($"Zombies spectate: chase cam on {target.Profile?.Nickname ?? "?"} (2.4 m behind, 0.9 m up, {team.Count} to watch)");
            }
            Transform eyes = target.PlayerBones?.Head?.Original;
            if (eyes == null)
                return;
            // Behind and above them, looking where they look (user, 2026-11-10: from the eyes the view was inside
            // helmets and masks); pulled in where a wall is in the way
            Vector3 look = target.LookDirection;
            Vector3 flat = new Vector3(look.x, 0f, look.z);
            flat = flat.sqrMagnitude > 1e-4f ? flat.normalized : target.Transform.forward;
            Vector3 pivot = eyes.position + Vector3.up * 0.25f;
            Vector3 back = -flat * 2.4f + Vector3.up * 0.9f;
            float dist = back.magnitude;
            if (Physics.SphereCast(pivot, 0.2f, back.normalized, out RaycastHit hit, dist, LayersMaskController.HighPolyWithTerrainMask, QueryTriggerInteraction.Ignore))
                dist = Mathf.Max(0.3f, hit.distance - 0.1f);
            Vector3 at = pivot + back.normalized * dist;
            cam.transform.position = at;
            cam.transform.rotation = Quaternion.LookRotation((eyes.position + look * 6f) - at);
        }

        // ---------- The Quick Revive machine goes (solo, after its 3rd revive) ----------

        private static void QuickReviveMachineLeaves()
        {
            if (_machineGone)
                return;
            _machineGone = true;
            MapVisual machine = UnityEngine.Object.FindObjectsOfType<MapVisual>()
                .FirstOrDefault(v => v.Entry != null && v.Entry.Type == MapEditor.Kind.PerkMachine && v.Entry.What == "revive");
            if (machine == null)
            {
                ZombiesPlugin.Log.LogInfo("Zombies: Quick Revive used up (no machine on the map to send off)");
                return;
            }
            // On the plugin, not the machine: the machine is destroyed mid-way, which stopped the coroutine and left the flash on
            ZombiesPlugin.Instance.StartCoroutine(RiseAndGo(machine.gameObject));
        }

        private static IEnumerator ByeByeAfter(Vector3 at, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            PlayBoxSound("Audio_ByeBye", at);
        }

        private static void PlayBoxSound(string source, Vector3 at)
        {
            GameObject box = ZombiesAssets.Box();
            AudioSource src = box != null ? box.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(a => a.name == source) : null;
            if (src?.clip == null)
                return;
            var go = new GameObject("Zombies_MachineVoice");
            go.transform.position = at;
            AudioSource s = go.AddComponent<AudioSource>();
            s.clip = src.clip;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 4f;
            s.maxDistance = 60f;
            s.volume = ZombiesSounds.Volume(ZombiesSounds.Group.PowerUps);
            s.Play();
            UnityEngine.Object.Destroy(go, src.clip.length + 0.2f);
        }

        private static IEnumerator RiseAndGo(GameObject machine)
        {
            ZombiesPlugin.Log.LogInfo("Zombies: Quick Revive used up, the machine leaves");
            // The box's own lift sounds (no teddy laugh, the user 2026-11-07): the whoosh and vanish, then "bye bye" as it rises
            PlayBoxSound("Audio_Whoosh", machine.transform.position + Vector3.up);
            PlayBoxSound("Audio_Disappear", machine.transform.position + Vector3.up);
            ZombiesPlugin.Instance.StartCoroutine(ByeByeAfter(machine.transform.position + Vector3.up, 0.6f));
            Vector3 start = machine.transform.position;
            // Lightning crawling over it all the way up
            ZombiesLightning.SurgeAt(start + Vector3.up * 1.2f, new Vector3(1.2f, 2.4f, 1.2f), 3.3f);
            ZombiesSounds.At("zm_lightning_loop", start + Vector3.up, 1f, 50f);
            for (float t = 0f; t < 3f && machine != null; t += Time.deltaTime)
            {
                float up = Mathf.SmoothStep(0f, 1f, t / 3f) * 4f;
                machine.transform.position = start + Vector3.up * up + UnityEngine.Random.insideUnitSphere * 0.03f;
                yield return null;
            }
            // The flash: a bright blue-white strike, flickering out
            var flash = new GameObject("Zombies_QuickReviveLightning");
            flash.transform.position = machine.transform.position + Vector3.up * 1f;
            Light light = flash.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.75f, 0.85f, 1f);
            light.range = 7f;
            light.intensity = 0f;
            UnityEngine.Object.Destroy(flash, 1.5f); // never left on, whatever happens below
            ZombiesSounds.At("zm_lightning_strike_#", flash.transform.position, 1f, 80f);
            BoxCloud.Puff(flash.transform.position, 2f);
            PlayBoxSound("Audio_Poof", flash.transform.position);
            if (machine != null)
                UnityEngine.Object.Destroy(machine);
            // A soft flicker (it was far too bright)
            for (int i = 0; i < 6 && light != null; i++)
            {
                light.intensity = i % 2 == 0 ? 2.2f : 0.6f;
                yield return new WaitForSeconds(0.06f);
            }
            if (flash != null)
                UnityEngine.Object.Destroy(flash);
        }

        // ---------- F12's dummy downed teammate ----------

        public static void PlaceDummy(Player me)
        {
            Vector3 at = me.Transform.position + me.Transform.forward * 2f;
            if (Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
                at = hit.point;
            _dummy = at;
            _dummyDownAt = Time.time;
            _reviveStart = -1f;
        }

        private static void TickDummy(Player me)
        {
            if (_dummy == null)
                return;
            if (Time.time - _dummyDownAt >= BleedOutTime.Value)
            {
                ZombiesPlugin.Log.LogInfo("Zombies: the dummy teammate bled out");
                _dummy = null;
                return;
            }
            bool holding = Mine == State.Up && Input.GetKey(ZombiesControls.Key(ZombiesControls.Act.Use)) && Vector3.Distance(me.Transform.position, _dummy.Value) <= ReviveRange;
            if (!holding)
            {
                _reviveStart = -1f;
                return;
            }
            if (_reviveStart < 0f)
                _reviveStart = Time.time;
            if (Time.time - _reviveStart >= ReviveSeconds())
            {
                ZombiesPlugin.Log.LogInfo("Zombies: revived the dummy teammate");
                ZombiesStats.Mine.Revives++;
                _dummy = null;
                _reviveStart = -1f;
            }
        }

        private static float ReviveSeconds() => HasQuickRevive() ? QuickReviveTime.Value : ReviveTime.Value;

        // ---------- On screen ----------

        private static GUIStyle _style;
        private static Texture2D _white;

        public static void Draw()
        {
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, clipping = TextClipping.Overflow };
                _white = Texture2D.whiteTexture;
            }
            float k = Screen.height / 1080f;
            if (Mine == State.Down)
            {
                float t = Mathf.Clamp01((Time.time - _downAt) / BleedOutTime.Value);
                ZombiesSlaps.DrawRed(0.35f + 0.6f * t);
                if (RevivedNow(out float fill, out string by))
                    Bar(Screen.width * 0.5f, Screen.height * 0.62f, fill, Color.white,
                        "BEING REVIVED BY: " + (string.IsNullOrEmpty(by) ? "_______" : by), k);
                else
                    Bar(Screen.width * 0.5f, Screen.height * 0.72f, 1f - t, StatusColour(t), "BLEEDING OUT", k);
            }
            else if (Mine == State.Dead && !ZombiesGameOver.Active && !ZombiesGameOver.Finished)
            {
                _style.fontSize = Mathf.RoundToInt(26f * k);
                _style.normal.textColor = Color.white;
                GUI.Label(new Rect(0, Screen.height * 0.8f, Screen.width, 40f * k), (WatchingName is string watched ? $"Watching {watched}  -  " : "") + "You will respawn next round  -  left / right click to switch player", _style);
            }
            foreach ((Player who, float since) in DownTeammates().ToList())
            {
                float t = Mathf.Clamp01((Time.time - since) / BleedOutTime.Value);
                bool mine = _revivingWho == who.ProfileId && _teamReviveStart >= 0f;
                float revive = mine ? Mathf.Clamp01((Time.time - _teamReviveStart) / ReviveSeconds()) : 0f;
                ReviveIcon(who.Transform.position + Vector3.up * 0.9f, t, revive, k);
                Player me = Singleton<GameWorld>.Instance?.MainPlayer;
                bool close = Mine == State.Up && me != null && Vector3.Distance(me.Transform.position, who.Transform.position) <= ReviveRange;
                if (revive > 0f)
                    Bar(Screen.width * 0.5f, Screen.height * 0.62f, revive, Color.white, "REVIVING", k);
                else if (close)
                    Prompt(ZombiesControls.KeyName(ZombiesControls.Act.Use), "Revive " + (who.Profile?.Nickname ?? ""), k);
            }
            if (_dummy != null)
            {
                float t = Mathf.Clamp01((Time.time - _dummyDownAt) / BleedOutTime.Value);
                float revive = _reviveStart < 0f ? 0f : Mathf.Clamp01((Time.time - _reviveStart) / ReviveSeconds());
                ReviveIcon(_dummy.Value + Vector3.up * 0.9f, t, revive, k);
                Player me = Singleton<GameWorld>.Instance?.MainPlayer;
                bool near = Mine == State.Up && me != null && Vector3.Distance(me.Transform.position, _dummy.Value) <= ReviveRange;
                if (revive > 0f)
                    Bar(Screen.width * 0.5f, Screen.height * 0.62f, revive, Color.white, "REVIVING", k);
                else if (near)
                    Prompt(ZombiesControls.KeyName(ZombiesControls.Act.Use), "Revive", k);
            }
        }

        // EFT's interaction prompt look: the key in a light box, the action beside it
        private static void Prompt(string key, string action, float k)
        {
            // The zombies HUD's prompt (user, 2026-11-11)
            ZombiesHud.Prompt("Hold", key, ZombiesHud.To(action), null, true, false, 0.58f, k);
        }

        private static void OldPrompt(string key, string action, float k)
        {
            float h = 30f * k, gap = 8f * k;
            _style.fontSize = Mathf.RoundToInt(18f * k);
            // The key box fits the key's name (F, Shift, Mouse 4...)
            float keyW = Mathf.Max(30f * k, _style.CalcSize(new GUIContent(key)).x + 12f * k);
            float textW = _style.CalcSize(new GUIContent(action)).x + 16f * k;
            float w = keyW + gap + textW;
            float x = Screen.width * 0.5f - w * 0.5f, y = Screen.height * 0.58f;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(new Rect(x + keyW + gap, y, textW, h), _white);
            GUI.color = new Color(0.9f, 0.9f, 0.9f, 0.95f);
            GUI.DrawTexture(new Rect(x, y, keyW, h), _white);
            GUI.color = Color.white;
            _style.normal.textColor = Color.black;
            GUI.Label(new Rect(x, y, keyW, h), key, _style);
            _style.normal.textColor = Color.white;
            GUI.Label(new Rect(x + keyW + gap, y, textW, h), action, _style);
        }

        /// <summary>the original's revive icon over a downed player: white, then orange, then deep red as they bleed out; it fills while someone revives them.</summary>
        public static void ReviveIcon(Vector3 world, float bleed, float revive, float k)
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;
            Vector3 s = cam.WorldToScreenPoint(world);
            if (s.z <= 0f)
                return;
            float size = Mathf.Clamp(46f * k * 12f / Mathf.Max(4f, s.z), 18f * k, 46f * k);
            float x = s.x, y = Screen.height - s.y;
            Color c = StatusColour(bleed);
            LoadIcon();
            if (_frame != null && _fill != null)
            {
                // The original's revive icon: its inside white, to orange, to red as they bleed out; the frame and REVIVE on top
                float iw = size * 1.6f, ih = iw * _frame.height / Mathf.Max(1f, _frame.width);
                var r = new Rect(x - iw * 0.5f, y - ih * 0.5f, iw, ih);
                GUI.color = c;
                GUI.DrawTexture(r, _fill);
                GUI.color = Color.white;
                GUI.DrawTexture(r, _frame);
                size = ih;
            }
            else
            {
                // A cross: dark edge, then the status colour
                Cross(x, y, size + 4f * k, new Color(0f, 0f, 0f, 0.7f));
                Cross(x, y, size, c);
            }
            if (revive > 0f)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(x - size * 0.5f, y + size * 0.6f, size * revive, 4f * k), _white);
            }
            GUI.color = Color.white;
        }

        private static Texture2D _frame, _fill;
        private static bool _iconTried;

        // hud/zm_revive_frame.png + zm_revive_fill.png (the original's revive icon, from the user's classic zombies revive.jpg)
        private static void LoadIcon()
        {
            if (_iconTried)
                return;
            _iconTried = true;
            _frame = LoadPng("zm_revive_frame.png");
            _fill = LoadPng("zm_revive_fill.png");
        }

        internal static Texture2D LoadPng(string name)
        {
            try
            {
                string file = System.IO.Path.Combine(ZombiesPlugin.Dir, "hud", name);
                if (!System.IO.File.Exists(file))
                    return null;
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                return ImageConversion.LoadImage(t, System.IO.File.ReadAllBytes(file)) ? t : null;
            }
            catch
            {
                return null;
            }
        }

        private static void Cross(float x, float y, float size, Color c)
        {
            GUI.color = c;
            float arm = size * 0.3f;
            GUI.DrawTexture(new Rect(x - arm * 0.5f, y - size * 0.5f, arm, size), _white);
            GUI.DrawTexture(new Rect(x - size * 0.5f, y - arm * 0.5f, size, arm), _white);
        }

        // White, to orange half way, to deep red at the end
        private static Color StatusColour(float t) => t < 0.5f
            ? Color.Lerp(Color.white, new Color(1f, 0.55f, 0.1f), t * 2f)
            : Color.Lerp(new Color(1f, 0.55f, 0.1f), new Color(0.6f, 0f, 0f), (t - 0.5f) * 2f);

        private static void Bar(float cx, float cy, float fill, Color colour, string label, float k)
        {
            // The zombies HUD's bar, the words over it (user, 2026-11-11)
            float bw = 380f * k, bh = 14f * k;
            ZombiesHud.Bar(new Rect(cx - bw * 0.5f, cy, bw, bh), fill, colour, null, k);
            ZombiesHud.Banner((cy - 50f * k) / Screen.height, label, colour, k, 20f);
        }

        private static void OldBar(float cx, float cy, float fill, Color colour, string label, float k)
        {
            float w = 320f * k, h = 10f * k;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(cx - w * 0.5f - 2f, cy - 2f, w + 4f, h + 4f), _white);
            GUI.color = colour;
            GUI.DrawTexture(new Rect(cx - w * 0.5f, cy, w * Mathf.Clamp01(fill), h), _white);
            GUI.color = Color.white;
            _style.fontSize = Mathf.RoundToInt(20f * k);
            _style.normal.textColor = colour;
            GUI.Label(new Rect(cx - w, cy - 34f * k, w * 2f, 30f * k), label, _style);
        }

        // ---------- No standing, sprinting or jumping while down ----------

        private static void Patch()
        {
            if (_patched)
                return;
            _patched = true;
            Harmony h = ZombiesPlugin.HarmonyInstance;
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.ToggleProne)), prefix: new HarmonyMethod(typeof(ZombiesDowned), nameof(NoGettingUp)));
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.ChangePose)), prefix: new HarmonyMethod(typeof(ZombiesDowned), nameof(NoPoseChange)));
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.Jump)), prefix: new HarmonyMethod(typeof(ZombiesDowned), nameof(NoPoseChange)));
            h.Patch(AccessTools.Method(typeof(Player), nameof(Player.EnableSprint)), prefix: new HarmonyMethod(typeof(ZombiesDowned), nameof(NoSprint)));
        }

        private static bool Locked(Player p) => p != null && p.IsYourPlayer && Mine != State.Up && ZombiesMatch.Active;

        // Down: going prone is allowed, getting up isn't
        private static bool NoGettingUp(Player __instance) => !(Locked(__instance) && __instance.IsInPronePose);

        private static bool NoPoseChange(Player __instance) => !Locked(__instance);

        private static void NoSprint(Player __instance, ref bool enable)
        {
            if (Locked(__instance))
                enable = false;
        }
    }
}
