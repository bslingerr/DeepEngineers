# Depth progression: how it works and how to build for it

DeepEngineers (S16). Numbers as built on (the depth range raised by half for every grid); the dev commands
can retune them, and this guide follows.

## 1. The idea in one paragraph

Depth is the progression axis, the way it is in Subnautica. Every ship has a **crush depth**: the depth below
which its hull starts to break. The crush depth comes from the hull you build and from one upgrade block you can
add. Ores are laid out by depth: the common ones near the surface, the valuable ones deep. So the loop is: mine
shallow ores, build a hull that goes deeper, reach the next ores, build the next hull.

## 2. What decides a ship's crush depth

The plugin looks at the ship every 10 seconds while it is in the water and rates its hull.

**Which blocks count.** Only blocks that the water touches:

- **Armour blocks** count when one of their faces is in the water, that is, outside any airtight room. Armour deep
 inside the ship does not count. Light armour and heavy armour are the two classes.
- **Fittings** on the hull, meaning doors, hatches, windows, cockpits, connectors and vents that have water on one side
 and an airtight room on the other, take the class of the armour around them. A heavy hull with a heavy-armour
 hatch stays heavy.
- **Thrusters, lights, antennas, drills** and anything else mounted outside never count. They are equipment, not hull.
- A craft with **no armour at all**, for example a cockpit, batteries and thrusters, is rated as light armour of its
 grid size. It is not immune.
- **Stations have no crush depth.** A static grid, a house or a base on the seabed, is never rated, takes no crush
 damage and shows the depth only; convert a ship to a station and the limit goes away, convert it back and it returns.

The ship's rating is its **weakest counted class**: one light armour block on a heavy hull makes it a light hull.

**The ladder** (no upgrade block):

| Hull | Crush depth |
|---|---|
| Small grid, light armour | 350 m |
| Small grid, heavy armour | 500 m |
| Large grid, light armour | 800 m |
| Large grid, heavy armour | 1,200 m |

**Mechanical groups and docking.** A ship made of several grids joined by rotors, hinges or pistons is rated as one:
the best upgrade on any of its grids applies to all. A small ship docked inside a bigger ship's airtight hangar is
protected by the carrier.

## 3. The upgrade blocks

Two blocks in the **DeepEngineers** category of the build menu (G): **Pressure Hull Mk1, Mk2**, in a large-grid
and a small-grid version. One working block on the ship sets its crush depth, whatever the armour:

| Block | Crush depth | Alloy needed (large / small block) |
|---|---|---|
| Pressure Hull Mk1 | 1,400 m | 20 / 4 Mk1 alloy |
| Pressure Hull Mk2 | 2,000 m | 30 / 6 Mk2 alloy |

Only the best block counts; two Mk1 blocks do not add up. The block must be built and working. Each block also
takes ordinary components, as any SE block does.

**The alloys** are made in the assembler from ingots, one recipe per tier:

| Alloy | Ingots per unit |
|---|---|
| Mk1 alloy | Cobalt 10, Magnesium 4, Silver 5 |
| Mk2 alloy | Gold 3, Platinum 1, Uranium 1 |

## 4. Where the ores are

The planet's ores follow depth, like Minecraft's follow height. The SE ore detector finds them; the common ones
also show at the surface as outcrops of a different rock in Subnautica's picture.

| Ore | Where | Depth |
|---|---|---|
| Iron, Nickel, Silicon | everywhere, outcrops on the seabed | 0 to 200 m |
| Cobalt, Magnesium | slopes everywhere | 100 to 300 m |
| Silver, Gold | Grand Reef, Blood Kelp | 300 to 600 m |
| Platinum, Uranium | trenches, Lost River, Crater Edge | below 600 m, rare and small |

So: a 350 m light hull reaches iron, nickel and silicon and the cobalt and magnesium slopes; a 500 m heavy small hull
the first silver, which completes the Mk1 alloy. A large light hull (800 m) reaches gold and the first platinum and
uranium, the Mk2 alloy; a large heavy hull (1,200 m) the basins and most of the trenches. Mk1 (1,400 m) reaches the
bottom of the deepest trenches (about 1,300 m) and every ore. Mk2 (2,000 m) is the planet's cap: no floor of the planet's
own terrain lies deeper, only the Crater Edge wall going down to the void floor at 3,000 m, which is scenery.: "at least 350 metres for small grids without upgrades";: "re-rate the hulls to the
planet", "the small grids must stay at least 250-300 m"; before: 1,600/2,200/3,000 m with a Mk3.)

## 5. What happens near and below the limit

- **Creaks** start 50 m above the crush depth, louder and more frequent as you go down: every 9 s, then 6 s from
 25 m above, then every 3 s past the limit. The sounds are Subnautica's own hull creaks.
- **"Hull integrity critical"**, the Cyclops voice, when you cross the limit and again every 20 s while you stay
 below it, louder than the creaks. It re-arms when you go back up 10 m.
- **Damage** past the limit: 10 hit points per second plus one per metre deeper, capped at 100 per second from 90 m
 past, on the eight deepest exposed blocks each second. A light small craft 50 m past its limit loses blocks within
 seconds; a sub 100 m past its limit dies within a minute. Breaking the hull floods rooms, and flooded rooms expose
 more blocks.
- Damage whenever the world has **destructible blocks** on, Creative included. With destructible blocks off you get
 the creaks, the voice and the HUD, no losses. After loading a world there is a 60 s grace before damage starts,
 with a notice.
- The **HUD** shows depth, the ship's crush depth and the state (safe, creaking, past the limit). Under water this is
 Subnautica's depth meter at the top centre; seated it reads depth and crush depth like the Cyclops ("250 m / 600 m"),
 amber while creaking, red past the limit, with no hull panel. Above water, seated, SE's text line. The diver has no
 SE text line: under water Subnautica's depth meter, above water nothing.

## 6. The diver

The engineer in the suit has no crush depth, as in Subnautica. Depth only makes breathing faster: 1.5 times below
100 m, 2 times below 200 m. So the suit's oxygen is the diver's limit, not pressure.

## 7. Three example builds

1. **Scout, tier 0.** Small grid, cockpit, light armour around it, thrusters outside. Rated 350 m. Enough for the
 shallows, iron, nickel, silicon and the cobalt slopes. Below 350 m it creaks, then loses blocks.
2. **Miner, tier 1.** Small or large grid with one Pressure Hull Mk1 built in. 1,400 m whatever the armour: every ore,
 the trench bottoms included. Twenty Mk1 alloy for the large block is 200 cobalt, 80 magnesium, 100 silver ingots, which
 a 500 m heavy small hull collects on the slopes and the first Grand Reef ledges.
3. **Deep sub, tier 2.** Large grid, heavy armour, a Pressure Hull Mk2 (2,000 m): the Crater Edge wall and the planet's
 cap. The Mk2 alloy needs the gold, platinum and uranium of 300-1,500 m, which a large hull or a Mk1 hull reaches first.

## 8. Reading the ship

Seated under water, the HUD line tells the rating. If a heavy ship reads light, one light armour block touches the
water somewhere; replace it or cover it. If a ship reads lower than its upgrade block, the block is unpowered or
damaged. If a docked craft reads its own rating, it is outside the carrier's airtight rooms.
