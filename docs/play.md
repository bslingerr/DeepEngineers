# Play

[Back to the start](../README.md) | [Install](install.md) | [Features](features.md) | [Q&A](faq.md)

## Your first minutes

After installing and launching the mod, Create a new game, select Sandbox, customize if you want to add mods or change the survival/creative mode and press **Start**

If you want to start on Subnautica's planet (4546B), pick the 4546B lifepod on the spawn screen. It falls 1 km on its parachute onto the same spawn point of Subnautica.

## [WIP] The depth system 

Depth is the progression, as in Subnautica. Every ship has a **crush depth**: below it, the hull breaks and you might lose your ship. Common ores lie shallow, valuable ones are deeper

| Depth | What happens there | Hull you need |
|---|---|---|
| 0 to 100 m | Iron, nickel and silicon. The diver breathes normally. | Anything (a bare small craft is rated 350 m) |
| 100 to 300 m | Cobalt and magnesium on the slopes. The diver uses air faster. | Small grid, light armour (350 m) |
| 300 to 500 m | Silver and gold begin (Grand Reef, Blood Kelp). | Small grid, heavy armour (500 m) |
| 500 to 800 m | Platinum and uranium from 600 m (trenches, Lost River, Crater Edge). | Large grid, light armour (800 m) |
| 800 to 1,200 m | Most of the trenches. | Large grid, heavy armour (1,200 m) |
| 1,200 to 1,500 m | The planet's deepest floor, at 1,500 m. | Pressure Hull Mk1 (1,600 m) |
| 1,500 to 2,200 m | The trenches below that floor. | Pressure Hull Mk2 (2,200 m) |
| 2,200 to 3,000 m | Everything, the void outside the map included. | Pressure Hull Mk3 (3,000 m) |

This is very much work in progress, it might be buggy and not smooth

### What sets a ship's crush depth

- **Only the hull the water touches counts:** armour blocks with a face in the water, and doors, hatches, windows, cockpits, connectors and vents with water on one side and an airtight room on the other (these take the class of the armour around them). Armour deep inside, thrusters, lights, antennas and drills never count.
- **The weakest class wins.** One light armour block touching the water makes a heavy hull a light hull.
- A ship with no armour at all is rated as light armour of its grid size.
- Grids joined by rotors, hinges or pistons are rated as one ship. A small ship docked in a bigger ship's airtight hangar is protected by it.
- **Stations have no crush depth.** A base on the seabed is never crushed.

### Pressure Hull blocks

Build menu (G), category **DeepEngineers**, large and small versions. One working block sets the ship's crush depth whatever the armour. Only the best block counts: two Mk1 do not add up, and an unpowered or damaged block does not count.

| Block | Crush depth | Alloy per block (large / small) | One alloy, made in the assembler from |
|---|---|---|---|
| Pressure Hull Mk1 | 1,600 m | 20 / 4 Mk1 alloy | iron 20, nickel 10, silicon 5 ingots |
| Pressure Hull Mk2 | 2,200 m | 30 / 6 Mk2 alloy | cobalt 10, magnesium 4, silver 5 ingots |
| Pressure Hull Mk3 | 3,000 m | 40 / 8 Mk3 alloy | gold 3, platinum 1, uranium 1 ingots |

Each block takes ordinary components to craft

### Near and past the limit

- From 50 m above the limit the hull **creaks**, more often as you sink
- At the limit the Cyclops voice says **"Hull integrity critical"**, again every 20 seconds while you stay below
- **Past the limit** the deepest outer blocks take damage, and faster the deeper you go: 50 m past, a light small craft loses blocks within seconds; 100 m past, a sub dies within a minute
- Broken hulls flood rooms and expose more blocks.
- When **Seated** under water, the HUD reads depth and crush depth, for example "250 m / 350 m": amber while creaking and becomes red past the limit.
- When **Swimming**, Subnautica's depth meter shows at the top of the screen.
- Damage needs destructible blocks on in the world settings; after a load you get 60 seconds of grace (in creative mode, your ship won't get destroyed)

### The diver

The suit has no crush depth, as in Subnautica. Depth only makes you breathe faster (1.5 times below 100 m, twice below 200 m), so oxygen is the limit.

## Ores

| Depth band | What you find | How common |
|---|---|---|
| 0 to 200 m | Iron, nickel, silicon | the most common: about one pocket every 35 m |
| 100 to 300 m | Cobalt, magnesium | about a third as many |
| 300 to 600 m | Silver, gold | about a quarter as many |
| 600 m and below | Platinum, uranium | about a twelfth as many, and small |

Nothing rare spawns in the shallows

- The **ore detector** marks the pockets. Each hides under at least 2 m of rock: dig about 2 m under the marker.

## Food

Space Engineers' hunger works as usual, and the planet feeds you. Swim up to a small fish with **empty hands**, look at it and press **F**: "F: catch the Peeper" shows when one is in reach. The fish goes into your inventory. Eat it as any Space Engineers ration, or cook it in the **Food Processor** (4 seconds) for twice the food. 

## Creatures

Subnautica's creatures keep their own behaviour and spawn point

| Creature | What it does | What works |
|---|---|---|
| **Reaper leviathan** (2.5 times its Subnautica size) | Grabs ships up to about 15 m across and chews them in its jaw, each bite about a quarter of a small-grid block. Bites and shoves bigger ships. It can smash your ship onto the seabed | Ship weapons: a little damage angers it (harder bites, longer hold); keep shooting with large guns (rocket,gatlings) makes it let go and flee. |
| **Ghost leviathan** (adult 2.5 times, juvenile 1.8 times) | Bites hard, about a third of a small block. | Large guns and warheads. |
| **Sea Dragon** (2.5 times) | Grabs bigger ships than the reaper; a bite takes about half a block. | Damaging him enough breaks the grab. Large guns, warheads. |
| **Crabsquid** | Its EMP switches off a ship's power for up to 30 seconds. | Kill it with any weapons or keep away. |
| **Ampeels, crashfish, bonesharks** | Shock, explode, bite, the diver too. | Any weapons. |

Fighting rules:

- A hand-held weapon cannot kill a leviathan. Large ship turrets and warheads can. Several warheads going off together add up.
- Rockets explode on the creature they hit.
- Stations cant be grabbed, and neither can the ships built with rotors, hinges or pistons: they are bitten instead. Small wreckage (under 8 blocks) are ignored

**Other hazards** hurt the suit: heat and lava, acid brine, electric shocks, poison gas.

**What helps:** first-aid kits heal; brain coral bubbles and Space engineer's Oxygen tanks refills your O2.

## Rooms, air and flooding and energy

- Under water you have air only **inside a pressurised room**. 
- To make your room pressurised, place an **AIR VENT** inside of your room/ship
- When a room loses pressure under water, **the water rises in it** with a splash. Repair, close and pressurize: the water goes down, with vent sound and bubbles.
- A flooded room is outside as far as the crush depth is concerned.
- Solar panels works ABOVE the water
