# Dr K's SPZombies

Round-based zombies for SPT. Survive wave after wave on a zombies map, earn points for every hit and kill, open doors, rebuild barricades, buy guns off the walls, spin the mystery box, drink perks, Pack-a-Punch your guns and find the way out. Solo, or with up to four friends over Fika.

Version **0.2.0 (alpha)**, for **SPT 4.1.6**. Made by **Kenner**.

## Requirements

| Mod | Version | Why |
|---|---|---|
| SPT | 4.1.x (made on 4.1.6) | |
| [Pack-a-Punch Hideout Module](https://github.com/Kennerz77/papHideoutModule) | 2.2.0 or newer | The Pack-a-Punch machine |
| [Dr K's Perk Beverages](https://github.com/Kennerz77/KennerPerkBeveragesSPT) | 0.1.0 or newer | The perk machines |
| [Ray Gun](https://github.com/Kennerz77/RayGunSPT) | 1.2.0 or newer | The Ray Gun in the box |
| [Hideout Uncensored](https://sp-mod.com/mod/3167/hideout-uncensored) by **trappuss** | 1.3.0 or newer | Needed by Pack-a-Punch and the guns |
| A zombies map, e.g. [Dr K's SPZombies Stronghold Map](https://github.com/Kennerz77/KennerZombies-StrongholdMap) | 0.1.0 or newer | Somewhere to play |

Optional:

| Mod | What it adds |
|---|---|
| [Project Fika](https://sp-mod.com/mod/2326/project-fika) and [Project Fika - Server](https://sp-mod.com/mod/2357/project-fika-server) | Co-op: lobbies, invites and matches for up to four players, and matches hosted on a Fika headless |
| [Thundergun](https://github.com/Kennerz77/ThunderGunSPT) 1.2.0 or newer | A wonder weapon the host can pick for the box |
| [Ray Gun Mark II](https://github.com/Kennerz77/RayGunMk2SPT) 1.0.0 or newer | A wonder weapon the host can pick for the box |
| [Wunderwaffe DG-2](https://github.com/Kennerz77/WunderwaffeSPT) 1.0.0 or newer | A wonder weapon the host can pick for the box |
| [MP40](https://github.com/Kennerz77/MP40-SPT) 1.0.0 or newer | A box gun, and a wall buy on maps that place one |

## Installing

1. Close the game and the SPT server.
2. Install the requirements above.
3. Extract the zip into your SPT folder (the one with the `SPT_Runtime` folder). You should end up with:
   - `BepInEx\plugins\Kennerz_ZM_Core\`
   - `BepInEx\plugins\SPZombies_Maps\` (where zombies maps go)
   - `SPT_Runtime\user\mods\Kennerz_ZM_Core\`
4. Install a zombies map the same way (see Zombies maps below).
5. Start the server, then the game.

**Fika headless:** a match hosted on a headless is run by the headless's own copy of this mod. Put the same `BepInEx\plugins` folders on the headless install too: this mod, `SPZombies_Maps` with every zombies map in it (and `Kennerz_ZM_Stronghold`), and Pack-a-Punch, Ray Gun, Dr K's Perk Beverages and any of the optional guns you use. The `SPT_Runtime` parts only go on the server.

## Zombies maps

Zombies maps go in `BepInEx\plugins\SPZombies_Maps`, each in its own folder named `ZM_<MapName>` (for example `ZM_Asylum`). A map's download comes ready to extract into your SPT folder, with everything for the map in its own folder. SPZombies loads the map itself: no other mod is needed. Restart the server and the game after adding a map; it then shows on the map screen in zombies mode.

The mod makes the `SPZombies_Maps` folder (with a short read-me) the first time it starts, if it isn't there.

**Make your own:** the **SPZombies Map Kit** is a Unity add-on for map makers. You place the zombies side with the kit: barricades, zombie spawns, perk machines, the mystery box, wall buys, doors, the power and the exfil, plus the map's own settings (starting gun, perk prices, perk limit, time, weather, sounds and loading tips). Maps made with the kit need version 0.1.2 or newer (0.2.0 for maps with ledges). A new kit version that builds the whole map by itself is coming.

## Playing

- On the main menu, press **ZOMBIES MODE** (top right). The zombies menu opens; **EXIT ZOMBIES MODE** takes you back to normal play.
- **SOLO:** pick a zombies map and play on your own. Esc pauses the match.
- **CREATE LOBBY** (with Fika): invite players from the **Social** list, or let them join if the lobby is open. Everyone readies up; the host picks the map, chooses whether a Fika headless or their own game hosts, and starts the match.
- **CUSTOMISE:** dress your character for zombies. Nothing you take in is lost, and your own guns stay at home.
- **MATCH REPORT** and **STATISTICS:** your last match and your lifetime numbers. Click a player in the Social list to see theirs.
- **WONDER WEAPONS:** the Ray Gun is always in the mystery box. Pick up to two more from the ones you have installed (Thundergun, Ray Gun Mark II, Wunderwaffe). In a lobby the host picks, and everyone sees the picks. **Any number** puts every one in the box, for 30% less Dr. K points at the end of the match. Each of the picked ones is one per team.
- **ZOMBIES SETTINGS:** keybinds, every zombies volume, invites on or off, and the reload reminder.
- **In a match:** hold the scoreboard key (Tab) for the scores. Esc opens the match menu: Resume, Zombies settings, Game settings, Leave match (your points and stats so far are kept).

## Settings (F12)

The same as Zombies settings in the game: the sound and music volumes, the zombies controls, the scores on screen, the main menu options and invites.

## Compatibility

- Never changes another mod's files, settings or load order. Anything it needs to work alongside another mod is done inside this mod.
- Fika is used as it is: its files, settings and load order are never changed. In zombies mode a few of Fika's on-screen panels are hidden while the zombies menus are up. Without Fika, the co-op part simply stays off.
- Zombies matches only happen on zombies maps. Your normal raids, character and hideout are left as they are, and nothing from a zombies match is added to your character.
- Loot mods (such as Lots of Loot): zombies maps have no map loot, so loot generation is skipped there. Your normal maps keep whatever loot your loot mods give them.

## Uninstalling

**Your Dr K points and statistics are saved inside the mod's folders.** Deleting the mod, or deleting its folders to do a clean update, deletes them. Updating by extracting the new zip over the old one keeps them. To keep them, back up these files first and put them back afterwards:

- `SPT_Runtime\user\mods\Kennerz_ZM_Core\kennerz_points.json` (your Dr K points)
- `SPT_Runtime\user\mods\Kennerz_ZM_Core\zombies_stats.json` (the statistics friends see)
- `BepInEx\plugins\Kennerz_ZM_Core\stats\` (your match history and statistics)

To remove the mod, delete `BepInEx\plugins\Kennerz_ZM_Core\` and `SPT_Runtime\user\mods\Kennerz_ZM_Core\`. Nothing from a zombies match is ever added to your normal character: zombies items stay in zombies matches.

## Credits

- **[Project Fika](https://sp-mod.com/mod/2326/project-fika)** and **[Project Fika - Server](https://sp-mod.com/mod/2357/project-fika-server)** by **Lacyway**, with **trippyone** and **ArchangelWTF**: the co-op this mode is built on. Not included.
- **Hideout Uncensored** by **trappuss** ([page](https://sp-mod.com/mod/3167/hideout-uncensored)): needed by the gun mods. Not included.
- **Zombie, weapon, power-up, box, barricade and sound assets:** third-party game assets, exported with Greyhound and GrayWolf. This is a free, non-commercial fan mod; all third-party assets belong to their respective owners.
- Made by Kenner.

## Changelog

- **0.2.0 (alpha):** SPZombies now loads zombies maps itself, so no other mod is needed for them. Pick your wonder weapons (the Ray Gun plus up to two of the Thundergun, Ray Gun Mark II and Wunderwaffe), with an "any number" option for 30% less Dr. K points. The Thundergun is now optional. New optional guns: the Ray Gun Mark II, the Wunderwaffe DG-2 and the MP40. Crawlers: zombies can lose their legs to explosions from round 10. Zombies climb through windows, drop down ledges and die to the Wunderwaffe with the original-style animations. Headshots can open zombies' skulls, for every player in co-op too. The mystery box is less likely to give you the gun you just had. Memory is cleaned at each round change, so long matches no longer slow down. Many balance and co-op fixes: see the update notes.

- **0.1.2 (alpha):** zombies maps now go in their own `SPZombies_Maps` folder (one `ZM_<MapName>` folder per map), ready for maps made with the SPZombies Map Kit. Maps can set their own starting gun and perk prices. Stronghold keeps working where it is. Fixed: after a quick knife or a perk drink, some guns from the box or a wall (ones your character had never examined) didn't come back to your hands. Fixed: in co-op, a player who bled out could come back the next round with no gun and stuck hands.
- **0.1.1 (alpha):** fixes a server error that stopped zombies maps from loading when Lots of Loot (or another mod that replaces the map loot) is installed.
- **0.1.0 (alpha):** first release. Your Dr K points and statistics are saved inside the mod's folders: back them up before deleting the mod or doing a clean update (see Uninstalling).
