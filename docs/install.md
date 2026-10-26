# Install

[Back to the start](../README.md) | [Play](play.md) | [Features](features.md) | [Q&A](faq.md)

## Before you start

- **Space Engineers** and **Subnautica** (the first game, not Below Zero), both bought and installed through Steam, on the same PC (pirated versions will not work)
- Steam running and logged in
- 32 GB RAM leaves room for large ships and a smoother experience ; 16 GB of ram is the bare minimum, you can play with it but expect stutters (see the [Q&A](faq.md#will-it-run-on-16-gb-of-memory)).

DeepEngineers is built against set versions of both games and of the Water Mod Made by Jakaria **https://steamcommunity.com/sharedfiles/filedetails/?id=2200451495** (3.23); The launcher checks warns when a version differs

## Install, step by step

1. Download **DeepEngineers.zip** from the [Releases](../../releases) page and unzip it anywhere, for example `C:\Games\DeepEngineers`.

2. Double-click **Install.cmd**. It:
   - finds both games through Steam;
   - brings its own Python runtime, the pinned Space Engineers plugin loader and the pinned Subnautica loader, each checked against its published checksum;
   - places the two DeepEngineers plugins and the DeepEngineers world mod;
   - builds the DeepEngineers Star System world from your own Star System files, so New Game lands on 4546B;
   - opens up the workshop pages for you to download, **it will wait for you to download them before proceeding.**
   - puts **DeepEngineers** and **Uninstall DeepEngineers** on your desktop and in the Start menu.
   You need to read its last line: INSTALL PASS everything is good to go or INSTALL FAIL, read the reason and fix it
   
3. Double-click **DeepEngineers** (or **Play.cmd**). Subnautica launches first, then Space Engineers starts with DeepEngineers loaded.

4. In Space Engineers: **New Game**, **Sandbox** **Star System**, Customize if you want to **add mods**, or be in **creative mode**, Then **Start**.
5. If u chose Survival, On the spawn screen, pick **4546B lifepod**; If you chose Creative, Select "Space Suit".

The vanilla earth pod appears 1 km above the water and comes down on its parachute. 

A black screen with **"Subnautica is loading the area..."** can show for a few seconds while the sea around you is being built. (the loading time depends on your machine and if the world load for the first time) ; 

The world (Survival) also has every vanilla planet of the Star System with its own lifepod.

## Every time you play

Start with **DeepEngineers** on the desktop, or Play.cmd. Space Engineers started from Steam runs a vanilla Space Engineers, same for Subnautica (Your saves are not edited)

Play does three things before the games: it checks for a newer DeepEngineers release and offers it, it renews the backups once a week, and it runs the preflight checks. To continue a world: Play, then **Load Game**.

## Updates

The Launcher asks GitHub for the latest release at every start. When a newer one exists it asks once; on yes it downloads the zip, checks it against the release's checksums, replaces the plugins, the world mod and the launcher, repairs the install and starts the new version. Your saves are preserved.

## Performance preset

The first time, the launcher picks a preset from your PC specs and ask you if you want to change it:

| Preset | Picked when | What it changes |
|---|---|---|
| Low | a shared graphics chip | Subnautica capped at 30 fps, fewer ship lights, shorter ranges |
| Mid | a dedicated graphics card | the defaults |
| High | 30 GB or more and a dedicated graphics card | more lights, longer ranges |

To change the preset later on, you can open the DeepEngineers Folder and open **Preset.cmd**

## Uninstall

**Uninstall DeepEngineers** in the Start menu, or Uninstall.cmd. It undoes exactly what Install recorded: the plugins, the loaders it brought, the world mod, the Star System world copy, the shortcuts. Your worlds stay; delete them yourself from Load Game if you want them gone. **UNINSTALL.md** in the folder lists every change for the manual way

## If something goes wrong

- **Install or Play stops with a message.** It names the one check that failed. Fix that and run it again
- **The capsule is missing from the spawn screen.** Space Engineers was started from Steam, or the card was not Star System. Close it, start DeepEngineers, New Game, Star System, Start.
- **Under water it looks like plain Space Engineers.** Either Subnautica is closed (crashed ; didnt open its freedom save) or that its loading. Check the launcher logs, if Subnautica is on its main menu, load the Freedom Save in the menu.
