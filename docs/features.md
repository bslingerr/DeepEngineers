# Features

[Back to the start](../README.md) | [Install](install.md) | [Play](play.md) | [Q&A](faq.md)

Everything under "In the game" is in the current build. Everything under "Coming" is planned or being built, with no date attached.

## In the game

### The planet

- **4546B in the Star System.** Subnautica's planet sits next to Earth, Mars and the other vanilla planets. If you are going there from another planet, check the Aurora's Beacon signal to land on Subnautica's original map
- **Subnautica's real map.** The 4 km by 4 km crater around the Safe Shallows is Subnautica's world.
- The rest of the planet is an ocean world with seabed relief, trenches and ore fields of its own.
- **One picture from orbit to the seabed.** Below about 1 km over the map the picture becomes Subnautica's sea and sky, with Space Engineers' ships, HUD and menus in it. above the 1km, its Space engineers again, with Subnautica's game limited at 10 fps in the background to save memory
- **Day and night in step** between the two games
- **The lifepod:** It's the Vanilla LifePod, pick it on the spawn screen: it falls 1 km on its parachute onto the Safe Shallows.

### The diver

- **Subnautica's swimming.** Under water the engineer moves like Subnautica's diver: the same speeds, drag and surface behaviour. The jetpack is off in the water.
- **Subnautica's arms and breathing** in first person: the swim animations, the bubbles, the choking when oxygen runs out.
- **No crush depth for the diver,** as in Subnautica. Depth makes you breathe faster: 1.5 times below 100 m, twice below 200 m.
- **A solid seabed.** Space Engineers collides with Subnautica's terrain: ships land on it, landing gear locks to it, and you swim right down to it.
- **Food from fish.** Catch Subnautica's small fish by hand (F), eat them raw or cook them in the Food Processor to feed Space Engineers' hunger. 

### Building and mining

- **Ships inside Subnautica's picture.** Every block, colour, light, thruster, moving part and damage state of the grids near you is drawn in the sea.
- **Ores by depth.** Iron, nickel and silicon in the shallows; cobalt and magnesium from 100 m; silver and gold from 300 m; platinum and uranium below 600 m. Pockets hide under the rock and show on the ore detector.
- **Drilling the seabed.** Space Engineers' drills cut Subnautica's terrain just like in SE

### Depth progression

- **Crush depth for every ship,** from its outer hull: 350 m for a light small ship up to 1,200 m for a heavy large one. Stations have none.
- **Three Pressure Hull blocks,** Mk1, Mk2, Mk3: 1,600, 2,200 and 3,000 m, built from alloys of the planet's ores.
- **Hull creaks** from 50 m above the limit, the **Cyclops voice** "Hull integrity critical" at the limit, block damage past it, and a depth readout with the crush depth on the HUD.

### Creatures and hazards

- **Subnautica's creatures with their own behaviour** see ships and divers: they bite, shove, shock and explode
- **Leviathans are bigger,** 2.5 times for the reaper, the adult ghost and the sea dragon: a hand-held weapon cannot kill them but large weapons and warheads can.
- **Grabbing:** A reaper or a sea dragon grabs a ship of its own size and holds it in its jaw while it chews. Bigger ships get bitten and pushed. If you are in a small Grid and a reaper sees you, good luck
- **Rage:** Shoot a leviathan that holds you: a little makes it angrier, enough makes it let go and flee. It never follows you out of the water.
- **Rockets hit creatures,** and several warheads going off together add up into one bigger blast and make bigger holes
- **Subnautica's hazards:** heat and lava, acid brine, electric shocks, poison gas, the crabsquid's EMP that power your ship off for 30 seconds.
- **Subnautica's help:** first-aid kits heal, brain coral bubbles refill oxygen.

### Rooms and air

- **Air under water only inside pressurised rooms.**
- **An interior look** under water: darker, cooler, lit by your own lamps.
- **Flooding and venting** you can see and hear: the water rises in a room that loses pressure, and goes down with vent sound and bubbles when you pressurize it again.

### Sound and weapons

- **Every Space Engineers sound is muffled under water:** guns, drills, welders, grinders, thrusters.
- **Ship weapons fire under water,** with bubble trails.
- **Cyclops and Seamoth voice lines when entering ships, or bases** ; only when Subnautica is rendering.

### Install, start, update

- **One click.** Install.cmd brings its own Python and loaders, places the plugins and the world mod, builds the Star System world and the shortcuts. The first Play makes Subnautica's save by itself (takes time)
- **Updates at every start,** from this repository's releases, checked against their published checksums.
- **Presets picked for your PC** at the first start: low, mid or high you can change them with **Preset.cmd**
- **Plain Space Engineers stays plain.** Started from Steam, Space Engineers is untouched; DeepEngineers loads only through its own start.
- **Nothing from either game is shipped.** Your own copies do all the drawing; Subnautica's side keeps no save of its own.

## Coming

- **Adding 4546B to an existing vanilla or modded save** ; Today, you can create or load DeepEngineers saves only
- **Smooth large blueprints,** fewer stutters and faster loading. this is a huge work, and requires lot of time to get it right.
- **Custom models for the Pressure Hull blocks.** Today they borrow the look of existing Space Engineers blocks.
- **A regenerated planet** around the 4X4 map, with its ore fields following the same depth rule as the map to get that "Subnautica's feel"
- **Ships high above the water seen from below,** as distant shapes.
