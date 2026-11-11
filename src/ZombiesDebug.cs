using System;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;

namespace KennerZombies
{
    /// <summary>
    /// F12's debug sections for the zombies match, one per part of the mode, each action written to the log:
    ///   11. Debug: loadout and ammo   (give the kit again, an M1911, fill or empty the reserve, infinite ammo, ammo state)
    ///   12. Debug: health and slaps   (no slaps, take one, clear them, go down, revive, bleed out, respawn, a dummy downed
    ///                                  teammate, last stand times, pretend Quick Revive)
    ///   13. Debug: rules              (friendly fire, EFT's debuffs, vaulting, Ctrl+I, a detailed log)
    ///   14. Debug: points and stats   (give or take points, log or reset the stats)
    /// The switches are on as the mode plays (rules on, cheats off); the buttons only work in a zombies match.
    /// </summary>
    internal static class ZombiesDebug
    {
        public static ConfigEntry<KeyboardShortcut> MarkShotKey;
        public static ConfigEntry<bool> InfiniteAmmo, NoSlaps, FriendlyFireOff, DebuffsOff, VaultingOff, WhatsThisKey, Detailed;

        public static void Bind(ConfigFile config, Func<string, Action, ZombiesPlugin.ConfigurationManagerAttributes> button)
        {
            const string ammo = "11. Debug: loadout and ammo";
            Button(config, ammo, "Give the zombies loadout again", "Strips the character and gives the ComTac VI and the M1911 again, as at the start of a match.",
                () => WithPlayer(p => Singleton<GameWorld>.Instance.StartCoroutine(ZombiesLoadout.Apply(p))), button, 1);
            Button(config, ammo, "Give an M1911", "Hands over an M1911 (with its mag and a reload), as the loadout does.",
                () => WithPlayer(p => { BoxGuns.Gun g = BoxGuns.FindGun("m1911"); if (g != null) Singleton<GameWorld>.Instance.StartCoroutine(BoxGive.Give(g, p)); }), button, 2);
            // To try the M203 launcher's reload and shells quickly (user, 2026-11-10)
            Button(config, ammo, "Give an M16 (M203)", "Hands over the box's M16 with its M203 underbarrel launcher, as the box would.",
                () => WithPlayer(p => { BoxGuns.Gun g = BoxGuns.FindGun("m16"); if (g != null) Singleton<GameWorld>.Instance.StartCoroutine(BoxGive.Give(g, p)); }), button, 2);
            Button(config, ammo, "Fill the reserve", "The gun in your hands gets its full reserve back (as Max Ammo will).", () => WithPlayer(ZombiesAmmo.Fill), button, 3);
            Button(config, ammo, "Empty the reserve", "The gun in your hands loses its reserve and spare mag (to try running dry).", () => WithPlayer(ZombiesAmmo.Empty), button, 4);
            InfiniteAmmo = config.Bind(ammo, "Infinite reserve", false, new ConfigDescription(
                "The reserve never runs down (the mag still empties and reloads).", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 95 }));
            Button(config, ammo, "Grenades to 4", "Fills your M67s to 4 (as Max Ammo and the wall buy do).", () => WithPlayer(p => ZombiesGrenades.FillTo(p, ZombiesGrenades.Most)), button, 5);
            Button(config, ammo, "Log ammo state", "Writes every gun's mag, spare and reserve to the log.", ZombiesAmmo.LogState, button, 6);

            const string health = "12. Debug: health and slaps";
            NoSlaps = config.Bind(health, "No slaps (god mode)", false, new ConfigDescription(
                "Zombie hits do nothing at all.", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 99 }));
            Button(config, health, "Take a slap", "As if a zombie hit you (two without Jugger-Nog and you're down).", () => WithPlayer(p => ZombiesSlaps.Hit(p, true)), button, 2);
            Button(config, health, "Clear slaps", "Back to full, as the regen does.", ZombiesSlaps.Clear, button, 3);
            Button(config, health, "Go down", "Down now: the last stand (prone crawl, last stand gun, bleeding out).", () => WithPlayer(ZombiesSlaps.DownNow), button, 4);
            Button(config, health, "Revive me now", "Up again from down, as a teammate's revive.", () => WithPlayer(p => ZombiesDowned.Revive(p, "F12")), button, 5);
            Button(config, health, "Bleed out now", "The bleed-out ends now (with no teammates up: game over, the raid ends).", () => WithPlayer(ZombiesDowned.BleedOut), button, 6);
            Button(config, health, "Respawn me", "Back in after bleeding out, as the next round's start will do.", () => WithPlayer(ZombiesDowned.Respawn), button, 7);
            Button(config, health, "Place a dummy downed teammate", "A downed teammate 2 m in front of you: its revive icon goes white to orange to red as it bleeds out; hold F by it to revive.",
                () => WithPlayer(ZombiesDowned.PlaceDummy), button, 8);
            ZombiesDowned.Bind(config, health);

            const string barricades = "15. Debug: barricades";
            Button(config, barricades, "Tear down nearest barricade", "All six boards off the barricade nearest you, to try rebuilding (hold F by it).",
                () => WithPlayer(ZombiesBarricade.TearNearest), button, 1);
            Button(config, barricades, "Next round now", "Skips to the next round (the zombies alive stay).", () => ZombiesNet.HostDo("nextround"), button, 3);
            Button(config, barricades, "Kill every zombie", "Every zombie alive dies (the round counts them).", () => ZombiesNet.HostDo("killall"), button, 4);
            Button(config, barricades, "Jump to round 15 (exfil test)", "Straight to round 15, the first exfil round: the lever in the basement can be pulled.", () => ZombiesNet.HostDo("exfilround"), button, 4);
            Button(config, barricades, "New round (rebuild points again)", "Resets everyone's rebuild points for the round, as a new round will.",
                ZombiesBarricade.NewRound, button, 2);

            const string rules = "13. Debug: rules";
            FriendlyFireOff = config.Bind(rules, "No friendly fire", true, new ConfigDescription(
                "Players can't hurt players (nor themselves). Off: EFT's damage between players.", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 99 }));
            DebuffsOff = config.Bind(rules, "No EFT debuffs", true, new ConfigDescription(
                "No bleeds, fractures, contusion, stun, pain, infection, hunger or thirst on players.", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 98 }));
            VaultingOff = config.Bind(rules, "No vaulting", true, new ConfigDescription(
                "Players can't vault or climb over things.", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 97 }));
            WhatsThisKey = config.Bind(rules, "Ctrl+I names what you look at", true, new ConfigDescription(
                "In any raid: Ctrl+I writes the three meshes in your aim (scene, path, LOD level) to the log.", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 96 }));
            Detailed = config.Bind(rules, "Detailed log", true, new ConfigDescription(
                "Writes every slap, points change and ammo top-up to the log.", null, new ZombiesPlugin.ConfigurationManagerAttributes { Order = 95 }));

            const string points = "14. Debug: points and stats";
            Button(config, points, "Give 1000 points", "Adds 1000 points (with the pop-up).", () => ZombiesMatch.AddPoints(1000), button, 1);
            Button(config, points, "Take 500 points", "Spends 500 points (with the pop-up).", () => ZombiesMatch.Spend(500), button, 2);
            Button(config, points, "Log stats", "Writes your match stats to the log.", ZombiesStats.LogMine, button, 3);
            Button(config, points, "Reset stats", "Kills, headshots, downs and the rest back to 0.", ZombiesStats.ResetMine, button, 4);

            ZombiesPowerUps.Bind(config, (section, name, action, order) => Button(config, section, name, $"{name} (F12 test).", action, button, order));

            const string machines = "16. Debug: machines";
            Button(config, machines, "Turn the power on", "The power comes on as if the switch was pulled (machines, PaP, the ZB-013 door).", ZombiesMatch.PowerOn, button, 1);
            foreach (ZombiesPerks.Perk perk in ZombiesPerks.All)
            {
                string id = perk.Id;
                Button(config, machines, "Give " + perk.Name, $"{perk.Name} at once, no drink, past the four-perk limit.", () => ZombiesPerks.Give(id), button, 2);
            }
            Button(config, machines, "Give 3 Cymbal Monkeys", "Three monkeys into the rig (the tactical key throws them), as the box gives them.",
                () => WithPlayer(p => Singleton<GameWorld>.Instance.StartCoroutine(ZombiesMonkey.Give(p))), button, 2);
            Button(config, machines, "Make chalk pictures for every gun", "Draws a chalk outline of every gun in the weapon table from its own model, to plugins\\Kennerz_ZM_Core\\chalk (to choose wall buys from).",
                () => WithPlayer(p => Singleton<GameWorld>.Instance.StartCoroutine(ZombiesChalk.Sheet())), button, 5);
            Button(config, machines, "Lose every perk", "Every perk gone, as going down does (Mule Kick's third gun too).", ZombiesPerks.LoseAll, button, 3);
            MarkShotKey = config.Bind(machines, "Mark game over shot key", new KeyboardShortcut(UnityEngine.KeyCode.F9, UnityEngine.KeyCode.LeftControl), new ConfigDescription(
                "In a zombies match: saves the camera where it is (stand or fly with F8, look at what it should show) as a game over camera shot for this map (<map>_gameover_shots.txt beside the layout). With any saved, the game over uses only those."));
            Button(config, machines, "Mark game over shot here", "The camera where it is now saved as a game over shot (same as the key).", ZombiesGameOver.MarkShot, button, 4);
            Button(config, machines, "Game over now", "The game over as if the match were lost: the camera shots, the board, the song, then back to the menu with the match report.", () => WithPlayer(p => ZombiesGameOver.Begin(p, "F12")), button, 4);
            Button(config, machines, "Pack-a-Punch the gun in my hands", "The gun in your hands upgraded at once, reserve filled (no machine).",
                () => WithPlayer(p => { if (p.HandsController?.Item is EFT.InventoryLogic.Weapon w) { ZombiesPaP.Infuse(w); ZombiesAmmo.FillGun(w); } }), button, 4);
        }

        /// <summary>Written when "Detailed log" is on.</summary>
        public static void Log(string text)
        {
            if (Detailed != null && Detailed.Value)
                ZombiesPlugin.Log.LogInfo("[debug] " + text);
        }

        private static void Button(ConfigFile config, string section, string name, string text, Action action,
            Func<string, Action, ZombiesPlugin.ConfigurationManagerAttributes> button, int order)
        {
            ZombiesPlugin.ConfigurationManagerAttributes look = button(name, () =>
            {
                ZombiesPlugin.Log.LogInfo($"[debug] {section} > {name}");
                action();
            });
            look.Order = 100 - order; // ConfigurationManager lists higher Order first
            // BepInEx refuses ' (and = " [ ] \) in a key: "Give Widow's Wine" stopped the whole start-up
            config.Bind(section, SafeKey(name), false, new ConfigDescription(text, null, look));
        }

        /// <summary>A name made fit for a BepInEx config key (no ' = " [ ] \ or line breaks).</summary>
        public static string SafeKey(string name)
        {
            foreach (char c in "'=\"[]\\\n\t")
                name = name.Replace(c.ToString(), "");
            return name;
        }

        private static void WithPlayer(Action<Player> action)
        {
            if (!ZombiesMatch.Active || !Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.MainPlayer == null)
            {
                ZombiesPlugin.Notify("Only in a zombies match.", true);
                return;
            }
            action(Singleton<GameWorld>.Instance.MainPlayer);
        }
    }
}
