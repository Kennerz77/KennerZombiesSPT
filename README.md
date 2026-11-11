# Dr K's SPZombies

Round-based zombies for SPT. Survive wave after wave on a zombies map, earn points for every hit and kill, open doors, rebuild barricades, buy guns off the walls, spin the mystery box, drink perks, Pack-a-Punch your guns and find the way out. Solo, or with up to four friends over Fika.

Version **0.1.0 (alpha)**, for **SPT 4.1.6**. Made by **Kenner**.

## Requirements

| Mod | Version | Why |
|---|---|---|
| SPT | 4.1.x (made on 4.1.6) | |
| [Pack-a-Punch Hideout Module](https://github.com/Kennerz77/papHideoutModule) | 2.2.0 or newer | The Pack-a-Punch machine |
| [Dr K's Perk Beverages](https://github.com/Kennerz77/KennerPerkBeveragesSPT) | 0.1.0 or newer | The perk machines |
| [Ray Gun](https://github.com/Kennerz77/RayGunSPT) | 1.2.0 or newer | The Ray Gun in the box |
| [Thundergun](https://github.com/Kennerz77/ThunderGunSPT) | 1.2.0 or newer | The Thundergun in the box |
| [Hideout Uncensored](https://sp-mod.com/mod/3167/hideout-uncensored) by **trappuss** | 1.3.0 or newer | Needed by Pack-a-Punch, Ray Gun and Thundergun |
| A zombies map, e.g. [Dr K's SPZombies Stronghold Map](https://github.com/Kennerz77/KennerZombies-StrongholdMap) | 0.1.0 or newer | Somewhere to play |
| [Tushonka Mapping SDK](https://sp-mod.com/mod/3193/tushonka-mapping-sdk) by **mizmii** (its CustomMapLoader) | 1.9.3 or newer | Loads the zombies maps |

Optional:

| Mod | What it adds |
|---|---|
| [Project Fika](https://sp-mod.com/mod/2326/project-fika) and [Project Fika - Server](https://sp-mod.com/mod/2357/project-fika-server) | Co-op: lobbies, invites and matches for up to four players, and matches hosted on a Fika headless |

## Installing

1. Close the game and the SPT server.
2. Install the requirements above.
3. Extract the zip into your SPT folder (the one with the `SPT_Runtime` folder). You should end up with:
   - `BepInEx\plugins\Kennerz_ZM_Core\`
   - `SPT_Runtime\user\mods\Kennerz_ZM_Core\`
4. Install a zombies map the same way.
5. Start the server, then the game.

**Fika headless:** a match hosted on a headless is run by the headless's own copy of this mod. Put the same `BepInEx\plugins` folders on the headless install too: this mod, every zombies map, and Pack-a-Punch, Ray Gun, Thundergun and Dr K's Perk Beverages. The `SPT_Runtime` parts only go on the server.

## Playing

- On the main menu, press **ZOMBIES MODE** (top right). The zombies menu opens; **EXIT ZOMBIES MODE** takes you back to normal play.
- **SOLO:** pick a zombies map and play on your own. Esc pauses the match.
- **CREATE LOBBY** (with Fika): invite players from the **Social** list, or let them join if the lobby is open. Everyone readies up; the host picks the map, chooses whether a Fika headless or their own game hosts, and starts the match.
- **CUSTOMISE:** dress your character for zombies. Nothing you take in is lost, and your own guns stay at home.
- **MATCH REPORT** and **STATISTICS:** your last match and your lifetime numbers. Click a player in the Social list to see theirs.
- **ZOMBIES SETTINGS:** keybinds, every zombies volume, invites on or off, and the reload reminder.
- **In a match:** hold the scoreboard key (Tab) for the scores. Esc opens the match menu: Resume, Zombies settings, Game settings, Leave match (your points and stats so far are kept).

## Settings (F12)

The same as Zombies settings in the game: the sound and music volumes, the zombies controls, the scores on screen, the main menu options and invites.

## Compatibility

- Never changes another mod's files, settings or load order. Anything it needs to work alongside another mod is done inside this mod.
- Fika is used as it is: its files, settings and load order are never changed. In zombies mode a few of Fika's on-screen panels are hidden while the zombies menus are up. Without Fika, the co-op part simply stays off.
- Zombies matches only happen on zombies maps. Your normal raids, character and hideout are left as they are, and nothing from a zombies match is added to your character.

## Uninstalling

**Your Dr K points and statistics are saved inside the mod's folders.** Deleting the mod, or deleting its folders to do a clean update, deletes them. Updating by extracting the new zip over the old one keeps them. To keep them, back up these files first and put them back afterwards:

- `SPT_Runtime\user\mods\Kennerz_ZM_Core\kennerz_points.json` (your Dr K points)
- `SPT_Runtime\user\mods\Kennerz_ZM_Core\zombies_stats.json` (the statistics friends see)
- `BepInEx\plugins\Kennerz_ZM_Core\stats\` (your match history and statistics)

To remove the mod, delete `BepInEx\plugins\Kennerz_ZM_Core\` and `SPT_Runtime\user\mods\Kennerz_ZM_Core\`. Nothing from a zombies match is ever added to your normal character: zombies items stay in zombies matches.

## Credits

- **[Project Fika](https://sp-mod.com/mod/2326/project-fika)** and **[Project Fika - Server](https://sp-mod.com/mod/2357/project-fika-server)** by **Lacyway**, with **trippyone** and **ArchangelWTF**: the co-op this mode is built on. Not included.
- **[Tushonka Mapping SDK](https://sp-mod.com/mod/3193/tushonka-mapping-sdk)** by **mizmii**, with **Manimal1920**: its CustomMapLoader loads the zombies maps. Not included.
- **Hideout Uncensored** by **trappuss** ([page](https://sp-mod.com/mod/3167/hideout-uncensored)): needed by the gun mods. Not included.
- **Zombie, weapon, power-up, box, barricade and sound assets:** third-party game assets, exported with Greyhound and GrayWolf. This is a free, non-commercial fan mod; all third-party assets belong to their respective owners.
- Made by Kenner.

## Changelog

- **0.1.0 (alpha):** first release. Your Dr K points and statistics are saved inside the mod's folders: back them up before deleting the mod or doing a clean update (see Uninstalling).
