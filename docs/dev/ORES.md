# Ores on DE_4546B

Where the ore is, how much, and why going deeper pays. Numbers are the rule as coded (`src/DeepEngineers.Wire/S8aCraterOres.cs`,
`tools/planet/layout_4546b.json` "ores"); the look is Subnautica's, the mining is Space Engineers'.

## Inside the Subnautica map (the crater, 4 km x 4 km)

**The depth table.** The map is cut into 8 m cubes. Each cube may hold one ore pocket, a sphere of 2 to 3.5 m. Which ore
depends on the cube's depth under the sea; the chance per cube is the share:

| Ore (SE) | Depth band | Share per 8 m cube | Pockets per hectare (100 m x 100 m, one 8 m layer) |
|---|---|---|---|
| Iron | 0 to 200 m | 1.0 % | 1.6 |
| Nickel | 0 to 200 m | 0.7 % | 1.1 |
| Silicon | 0 to 200 m | 0.7 % | 1.1 |
| Cobalt | 100 to 300 m | 0.4 % | 0.6 |
| Magnesium | 100 to 300 m | 0.4 % | 0.6 |
| Silver | 300 to 600 m | 0.3 % | 0.5 |
| Gold | 300 to 600 m | 0.3 % | 0.5 |
| Platinum | 600 m and below | 0.1 % | 0.2 |
| Uraninite | 600 m and below | 0.1 % | 0.2 |

So yes, deeper is worth it: the shallows give the three common ores only; cobalt and magnesium start at 100 m; silver and
gold only between 300 and 600 m; platinum and uranium only below 600 m (the Lost River and the lava zone). Nothing rare
ever spawns in the shallows.

**Density.** Before the common band filled 7.4 % of the cubes, about 24 pockets per hectare, one every 20 m or so:
The table above is a third of that: 2.4 % of the cubes, about 8 pockets per
hectare in the common band, one every 35 m. The large Subnautica deposits (the ones a Prawn drills) add one SE pocket of 6 m
each, where they are; the planet rules file (`de-planet-rules.txt` beside the planet data) can switch the cube rule off and
cap those per square kilometre.

**Cover.** Every pocket keeps at least 2 m of the map's own rock above and around it. From the outside the seabed looks like
Subnautica; the ore shows when you dig into it or when SE's ore detector marks it. The detector's marker is the hint: the
vein starts 2 m under the rock it points at. (A surface paint of the pockets exists behind `DEEPENGINEERS_S8A_ORE_SHOW_M`, off
by default: with the flat 2 m cover it painted the whole footprint.)

**Yield.** The pockets are SE voxels of SE's own ore materials, mined with SE's drills and refined as in SE. In Subnautica's
picture they wear this mod's own ore textures (slots of the terrain palette the game leaves empty), so an opened vein reads
as ore, not as grey rock.

## Beyond the map (the rest of the planet)

The planet's own ore comes from the sculpt tool's field rule, written into the planet's material maps when the planet is
generated: deposits grouped into fields on trench walls, ridge flanks and cliff feet, the common ores at half weight in the
ring around the crater, the deep ores only in the planet's trenches, at Space Engineers' EarthLike density overall (3.46 % of
the surface pixels). Those maps do not change with the table above; a planet regeneration is planned (S8a, "terrain 7").

## Known and open

