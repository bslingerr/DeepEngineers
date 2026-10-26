# Q&A

[Back to the start](../README.md) | [Install](install.md) | [Play](play.md) | [Features](features.md)

## Is this optimized for a playthrough yet ?
Yes, No, Maybe, Probably. Not much helpful i know but the mod is still very much WIP, many tests were done, several hours of games put into it, no issues so far but this still doesnt mean its perfect.

Huge pourcent of my time making this project with claude was to tell him to optimize everything he touched "claude optimize the game make no mistake" but it still needs polishing.
(this wasnt made in 3 prompt btw, it was a JOKE)

I'm doing all of the testing alone with my 2 computers, this take a huge amount of my (free) time (i'm a jobber).

## Why do I need both games?

Because both of them run at the same time. Space Engineers is the game you play: your character, your ships, the physics, the survival rules and the save. Subnautica runs hidden behind it and draws everything under the water: the sea, the seabed, the light and the creatures, with your ships drawn into its picture. DeepEngineers connects the two while they run. It ships nothing from either game, so it needs your own copies of both, bought on Steam.

## Can you mine the safe shallow ? 

Yes, and not just here and there. Every bit of the seabed is real terrain you can mine (or explode) Subnautica’s own voxels included

The ores deposits sit in pockets under the rock just like Space engineers, and the deeper you go the better they get. .

Outside Subnautica’s map (4x4km), the rest of the planet is an ocean world with its own relief, trenches and ore fields, and the same rule applies: it’s all terrain, it all mines.

## Do I have to start Subnautica or Space Engineers myself?

No. **The launcher** starts Subnautica in the background, then Space Engineers. Subnautica's window stays behind Space Engineers; leave it open while you play, it is the renderer. The first time you launch the mod, it creates Subnautica's own Freedom save, about three minutes.

## Will it run on 16 GB of memory?

Run : Yes
Smoothly ? : probably, probably not, this depends on how much RAM actually available you have when launching the mod, i played it with 9 GB free RAM with some stutters on the lowest preset.

Both games together take most of the RAM, so close other heavy programs before you start; The launcher reports the free memory and goes on whatever the figure. 32 GB leaves room for large ships and a smoother experience.

## Big blueprints make the game lag. Is that known?

Yes. Ships of several thousand blocks are heavy for Subnautica's side to draw, and pasting a large blueprint can take a long time. Above 1,000 blocks the paste preview shows as a flat shape. Smooth large blueprints are part of the upcoming performance work.

Again, this mostly depends on your computer.

## Can I add 4546B to my existing save?

Not yet; it is coming.
Today 4546B comes with a new game or loads with DeepEngineers' saves

## Is there multiplayer?

No. Not yet.

DeepEngineers is single player for now. I rather focus polishing the single player side first.
In a multiplayer session it does nothing, and you see plain Space Engineers

## Do my other Space Engineers mods work?

Yes, Space Engineers mods work alongside it; a handful have been tested. Two things to know: the Water Mod is required, and on DeepEngineers worlds the vanilla Planetary Rover respawn ship is limited to the vanilla planets, so a planet added by another mod no longer offers it. 

I've tested the most populars mods, i cannot assure you that every mods out there works, if one doesnt, open an issue in the repo or post it on the reddit post

## Does DeepEngineers change my normal games?

No. Space Engineers started from Steam is plain Space Engineers: DeepEngineers loads only through its own Play. Subnautica's renderer loads its Freedom save and never saves it, so your Subnautica saves are untouched.

## Where are my saves?

In Space Engineers' normal save folder, like any other world: Load Game lists them, and you back them up the usual way. The launcher also keeps a weekly backup of them under %LOCALAPPDATA%\DeepEngineers\backups. Subnautica's saves are never touched

## How do I update?

The launcher checks this repository's releases at every start and offers a newer version; on yes it downloads it, checks it against the release's checksums, replaces its own files, repairs the install and starts again. `Play.cmd -AutoUpdate` skips the question. See [Install](install.md#updates).

## How do I uninstall?

**Uninstall DeepEngineers** in the Start menu, or Uninstall.cmd in the folder. It undoes exactly what Install recorded: the plugins, the loaders it brought, the world mod, the Star System world copy, the shortcuts. Your Space Engineers worlds stay where they are; delete the DeepEngineers ones yourself if you want them gone. UNINSTALL.md in the folder lists every change for the manual way.

## Why does the screen go black with "Subnautica is loading the area..."?

DeepEngineers hides the half-drawn picture while Subnautica builds the area around you: after a respawn, a world load or a long jump. 
It lifts as soon as the area around you is built, usually within a few seconds depending on your computer. 
if it says Subnautica is not responding, the curtain gives up after a minute; quit and start again.

## Can the diver be crushed?

No. As in Subnautica, the suit has no crush depth but depth makes you breathe faster (1.5 times below 100 m, twice below 200 m), so oxygen is the limit. Ships are another story: see [the depth system](play.md#the-depth-system).

## Is there food and hunger?

Yes. Space Engineers' hunger works as usual, and Subnautica's small fish feed you: catch one by hand with F, eat it raw or cook it in the Food Processor. See [Food](play.md#food).

## Is Subnautica's story in it?

No. You get Subnautica's world, not the story

## Are there known issues?

Yes, it is a beta and i'm the only tester here, although claude helps , its not enough to fix all the bugs, if you found some please report them either on the reddit post, workshop post, or check the next question

## How do I report a bug?

Open an issue in this repository. Say what you did, what you saw and the time it happened, and add a screenshot if you can. Attach the DeepEngineers logs of that session: type `%LOCALAPPDATA%\DeepEngineers\logs` in the Explorer address bar to open the folder.

## How does this work ? 

Both games run at the same time, but you only play Space Engineers. 
Space engineer's character, ships, building, physics, survival, saves
Subnautica runs in the background and handles the ocean itself.

DeepEngineers is built around two plugins and a bridge between the games. Space Engineers uses a Pulsar plugin with Harmony to hook into the game, while Subnautica has its own BepInEx plugin. Neither game gets permanently modified. The plugins run alongside the games and work with the installations you already have.

The two plugins communicate through shared memory. It's a fixed block of memory split into regions, with one side writing and the other reading. Sequence locks make sure neither game reads data while it's halfway through being updated.

Space Engineers sends its camera position and rotation every tick, along with the time of day, session state and the positions of objects that need to be visible underwater
Subnautica reads that data every frame and moves its camera to match it also sends information back, including its current status, nearby creatures and terrain changes. Everything goes through shared memory, so there's no socket connection, network traffic or constant file reads involved.

The tricky part is getting both games to look like one. Subnautica doesn't take over the screen. Space Engineers still handles the HUD, menus and everything above the water. Underwater, the relevant parts of the Space Engineers window become transparent, letting Windows composite Subnautica behind it.

Ships aren't just rendered as flat images on top of the water, either. Nearby grids are exported block by block and reconstructed inside Subnautica. That includes their models, materials, paint, lights, moving parts and damage. Subnautica renders these copies using its own lighting, fog and underwater effects, so they actually look like they're sitting in the ocean rather than being pasted into the scene.

The player character is handled in much the same way. Its model and bone transforms are exported so Subnautica can reproduce the suit and its animations underwater. While Subnautica is still loading or generating an area, a black curtain hides the unfinished parts instead of showing geometry that isn't ready yet.

The planet is a real Space Engineers world based on 4546B, with a 60 km radius and the sea starting at the surface. The seabed beneath the 4 × 4 km mapped area comes from Subnautica's actual terrain, not a recreation of it 

Subnautica stores its terrain in voxel octrees inside batch files. DeepEngineers converts that data into Space Engineers voxel format, which means the terrain is part of the world you actually play in. 

You can land on it, collide with it and lock landing gear to it

Terrain changes work in both directions. If you drill into the seabed in Space Engineers, DeepEngineers writes the change back to Subnautica's terrain and reloads the affected area. The visual catches up in roughly a second, so what you see in the ocean stays consistent with what you've done in Space Engineers.

Outside the mapped area, the planet is simply an ocean world for now. Space Engineers doesn't have native water physics, so buoyancy, drag and the actual water behaviour come from Water Mod made by Jakaria **https://steamcommunity.com/sharedfiles/filedetails/?id=2200451495**, configured through the Deepengineers' mod to work with Subnautica's surface 

The rest of the gameplay is built on top of that connection. Creatures can detect ships and attack them, with damage carried back into Space Engineers. Depth and crush limits apply, air is only available in pressurized rooms, and sound gets muffled underwater. When you're diving, movement uses Subnautica's swimming mechanics, and you can catch and eat fish.

Space Engineers remains in control of the inputs

The general idea of running two games together and passing data between them isn't new. There have been similar projects putting Minecraft inside Skyrim or GTA, for example. The only difference is how the two games share the work

DeepEngineers instead of bringing the guest game's graphics into the host, it sends the host's objects into the guest game's renderer.
That's intentional: Subnautica's lighting, fog and underwater atmosphere are a big part of the project, and reproducing all of that in Space Engineers wouldn't make much sense

The terrain follows the same idea. Rather than using Subnautica's seabed as a visual layer or relying on raycasts for collision, DeepEngineers converts it into actual Space Engineers voxels. 

DeepEngineers ships no file from either game; everything, the terrain included, is converted on the player’s PC from the player’s own installs 

If you want more details, download the source code, read the comments or give to an agent that will explain it to you.
