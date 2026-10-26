# S8a planet v2: design 5 (depth continuity), one planet that plays like the Subnautica map

Status: **previews only**, and the route B sample (section 12) built: offline rows PASS, waiting for the runs of `ns` and `nl`. Nothing regenerated, nothing deployed, no pin bumped. The deployed planet is design 3
(sculptHash `0x59f91bb12dc43654`, conventions `ext s8a terrain 5` `0xcbf4ca049d06c430`); the repository's world mod still holds
it, and the tool reproduces it byte for byte (a scratch regeneration gives the same sculptHash; `--selftest` now pins design 3 at
128 px). Design 4 (`layout_4546b_d4.
the trough at 12.2 km) and the review found its ores worse. Design 5 is `tools/planet/layout_4546b_d5.json`; every design-5 rule
in `sculpt_de4546b.py` is keyed on a layout entry design 3 does not have.

Previews: `tests/out/s8a/stageC2-preview/` (`tools/planet/s8a_c2_preview.py --sn-scan DIR --sn-density FILE`), numbers in
`c2-numbers.json`. The install-derived inputs (the 4 m column maps of `tools/sn-octree-scan`, the entity census) stay outside the
repository.

## 1.5 km out and from 30-40 km up)

| Seen | Cause (measured) |
|---|---|
| "Blue nothing" 17.5 km from the site | Depth, not a window gap: design 3's seabed at -199 m under his camera (site (12683, 12285)); 369 batches written, 0 pending; a thin spawn table. |
| The moat, a dark ring | Design 3: -3,000 m from 12.3 to 14 km. |
| The site tile, a hard-edged square | Design 3's square crater: -3,000 m inside rhoInf 2,450 m, a square wall to 3,700 m; Subnautica's terrain ends at 1,600-2,220 m (its crater edge, measured per heading). |
| The planet "white, cloud-like, ice" | The rock materials are SE's asteroid stones `Stone_01`..`05`; far away SE draws their `_distance` textures, pale grey and blue-grey (#8d8987, #71808e). |
| White land fragments on the tile | Unresolved (the tiles' land is Grass and Sand_02, Grass's far texture is green): a colour or render question, settled by the look part's captures from 5 and 30 km (section 6). |
| Design 4 (his look at c2-seam) | "The square and ring part": the 560-760 m inner floor round the map and the 600-820 m trough at 12.2 km. |

## 2. The rules (design 5)

**R1 Depths.** One planet field everywhere beyond the ring: shelf 40-120 m, reefs and slopes 150-400 m, ridges, deep basins
550-1,050 m (Grand Reef's look) and trenches 700-1,300 m (Blood Kelp's look) beyond 26 km only, island chains. No seabed deeper
than 2,000 m anywhere.

**R2 Continuity: no crater, no moat, no trough.**
- Around the Subnautica map the floor continues at the map's own rim depth for each heading (`tools/planet/sn_rim.py`: rEdge = the
 last terrain column followed by 200 m of void, rimY = the median top 50-200 m inside it; 720 headings, a circular Gaussian of
 4 deg; measured rEdge 1,600-2,220 m (median 1,857), rimY -202..-816 m (median -370)) and
 blends into the ring's biome depths over 500 m. The crater of the original game is gone as a landform; its biomes remain.
- The ring blends into the planet's field over 10.5-12.9 km (start wiggled +-300 m, end fixed so the junction is continuous).
 The Crater Edge biome only marks a reef drop-off inside that band where the seabed lies 150-300 m deep (its G is now sparseReef,
 never void; never deeper than 300 m there).
- The continuity rule: no ring (an annulus 100 m deeper than the shallowest annulus within 3 km on both sides, in at least 10 of
 12 sectors of 30 deg) and no km-scale gradient over 500 m per km outside named features (canyons, trenches, deep basins,
 islands), judged offline from the heightmap and in game by rays (rows C2.continuity and NZ.continuity).

**R3 Under the tiles.** The interior (every Subnautica column more than 300 m inside the rim's edge
for its heading, caves and overhangs included) is protected: the planet stays 30 m below each column's lowest floor (the preview's
96 m min-filter; the regeneration adds a per-pixel guard on the bicubic surface at 4 m). The rim's last 300 m (the crater edge's
drop into the void: its outward faces, overhangs and feet down to -1,250 m) the continued floor buries. Without that the cap ran a
70-200 m wide, 750 m deep slot round the map. Both pictures
must agree, so **the window serves the batches of the rim's last 300 m and of the map's void columns as the union of the install's
octrees and the planet** (Subnautica's terrain or the planet's, whichever is solid): a precondition of the gate (section 7), not a
later part. In the Subnautica view the crater edge then ends in the same floor as in SE's.

**R4 Palette.** The material map's R values keep their roles; the SE material behind each becomes a planetary one: Sand_02 ->
Sand_02, Stone_01 -> `Stone`, Stone_02 -> Soil, Stone_03 -> DesertRocks, Stone_04 -> AlienRockyMountain, Stone_05 -> AlienSoil,
Grass -> Grass (one `CustomMaterialTable` block of `PlanetGeneratorDefinitions.sbc`, the tiles' `MaterialBands.Names`,
`S8aMaterialMap.Subtypes`/`BiomeOf`, terrain 6). Subnautica's types stay. Around the map the planet takes the tiles' depth-band material, the same on both sides of the seam.

**R5 Islands.** Every surface above `grassAboveY` (-2 m) is Grass, or `Stone` where the slope is >= 0.55; on the tiles every
land column (y >= 0) the same.

**R6 The 3D field everywhere (Stage F2 beyond the ring), redesigned after  and MEDIUM 13.**
- Placements per placement tile on each face's grid, built on demand from fnv(pin, face, tile, cell) and the biome map's G (a planet
 biome takes its Subnautica counterpart's classes, density and 3D rules).
- The repeat rule without order dependence: a static lattice of brush classes. Each cell's slot is (cx mod 4, cz mod 4); slot s
 draws only from pool partition s (the biome's brushes split into 16 partitions by hash). Two cells sharing a slot are at least 4
 cells apart, 326 m with the 0.2-0.8 jitter of 96 m cells, so no brush repeats within 300 m, and a cell's brush depends on its
 own cell alone (no apron, no memo, no build order). A biome with fewer than 16 brushes leaves partitions empty; they borrow the
 next partition's brushes and S8a.NF.repeat.planet reports the repeats that follow.
- Equal-area cells: the face grid is gnomonic (a cell at a face's edge covers 35 % of a centre cell's area, at a corner 19 %:
 the 1/(1 + u^2 + v^2)^1.5 weight), so density and cost would rise up to 5x; the keep probability is multiplied by the cell's area
 ratio, so placements per km2 and the cost are flat across a face.
- Evaluation in each placement's local tangent frame (no stretching toward the antipode); the base is SE's own heightmap on every
 face.
- A thread-safe LRU of placement tiles sized for the most cameras SE serves at once (a dedicated server: every player's
 neighbourhood): 25 tiles per camera, about 0.3 MB each camera; NF.memory judges it.
- S8a.NF.determinism builds each tile from a cold cache in two orders.
- The cost: implementation, not the idea. Per voxel the field maps the SN point to
 the planet's local frame, evaluates the Catmull-Rom base and then loops over every placement near the batch (not near the voxel).
 The fix (section 8, 120 k): cull the placements per 8 m sub-box once, stop at a placement's reach, cache the H lattice per
 placement tile (keyed by the pin), and read the window per batch rather than per octree; the target is 1.5-2x SE's own read. The
 window's slowness in a test run (NF.drill's live edits 60 s late) is this cost queued behind the window, not a second problem.

**R7 Life (deferred to S10, except the water column).** The census (CellsCache, read-only): Subnautica's biomes carry 261-1,364
entities and slots per hectare; the spawn table 5-21. Every planet biome is 15-67x thinner. This slice keeps only the
water-column table (fish and particles in every band down to the floor, so a basin is never empty water; NZ.dead needs it); the
slot/template system moves to S10 with the generator (section 11).

**R8 Ores you can see.** Subnautica's own large deposits (`Drillable*`) at every vein outcrop and on drilled vein walls (the SE
scanner per 32 m detector cell at LOD 1, anchored at the exposed voxel horizontally nearest SE's detector centroid, sized by the
first-sighting exposed area, shrunk by the cube root of the volume left; Subnautica keeps one per record within 100 m, transient,
never in the save, never its own report, never damaged by its drills, never hidden by the flora rule). Mapping: Iron titanium,
Nickel nickel, Silicon quartz, Cobalt lead, Magnesium lithium, Silver silver, Gold gold, Platinum diamond, Uranium uraninite.

**R9 Props over holes.** Ground is judged against the store's carved voxels (the served batch
files: no colliders needed, so objects that wake beyond collider range are judged too); carve boxes are kept per store key and
chart, never merged into large boxes, and persisted in the store so a restart re-applies them; candidates are found per carve box
with `Physics.OverlapBox` (no scene-wide search on the main thread); this mod's deposits carry a marker and are never candidates.

**R10 Ore density (the vein rule, in the tools now: `ores.veinRule`).** Veins of 1-2 pixels on a jittered lattice per rarity
class, the first pixel at the seabed (outcrop value), rarest classes first:

| Class | Ores | Band | Spacing | Target veins/km2 | Design 5 (dry run) |
|---|---|---|---|---|---|
| common | Iron, Nickel, Silicon | 0-200 m | 250 m | 14.4 | 14.19 |
| medium | Cobalt, Magnesium | 100-300 m | 450 m | 4.4 | 4.44 |
| uncommon | Silver, Gold | 300-600 m | 800 m | 1.5 | 1.40 |
| rare | Platinum, Uranium | 600-1,500 m (ring: Lost River, Blood Kelp, Grand Reef; planet: trenches and basins) | 2,000 m | 0.25 | 0.23 |

Ore share of the planet's seabed 2.66 % (SE's EarthLike 3.46 % +-35 %: within; design 3 3.07 %, design 4 5.97 %), Clark-Evans R
1.18 (an even lattice; the fields row's clustering test does not apply to veins: S8a.N10.veins replaces it for layouts with a vein
rule). The common spacing is 250 m, not 100-200 m: with 46 m pixels a vein is at least 2,116 m2, and 150 m would put 9 % of the
shelf in ore. The crater: the cube rule goes (today 22-26 pockets per hectare at 0-200 m, a detector marker every 20-30 m); pockets
only under Subnautica's own large deposits (`crater-deposits.tsv`), at most 4 per km2. Item 2's code, switched on by the layout: design
5's `ores.crater` (`cubeRule` false, `depositsPerKm2` 4) is written by the sculpt tool as `de-planet-rules.txt` beside the 12 PNGs
(design 3 writes none, and a stale one is removed), read by the SE plugin at world up (Wire `S8aPlanetRules`) and folded into the
window's and the charts' keys, so a store made under other rules is never served. The cap counts per 1 km x 1 km cell of the crater
(SN x, z floor-divided; the crater's biome mosaic is finer than a kilometre, so "per biome" is approximated by the cell), keeping the
deposits in a fixed hash order of their positions (independent of the file's order). S8a.M27 turns the cube rule back on.

## 3. The numbers (design 5 previews, design 3 as deployed)

- Depth beyond the ring, area share (design 5 / design 3): 40-120 m 66.4 / 0.7 %, 120-150 m 2.8 / 4.3 %, 150-400 m 25.0 / 23.6 %,
 400-550 m 0.8 / 3.0 %, 550-1,100 m 4.5 / 62.8 %, 1,100-2,000 m 0.3 / 5.2 %, deeper than 2,000 m 0 / 0.4 %. Globe bands: shelves 40-130 m 66.6 %, plains 150-420 m 26.2 %, trenches 700-1,300 m 3.8 %.
- Sections (35, 160, 280 deg): at 3 km -70 / -39 / -61 m (design 3 -3,000); the ring's edge at 12 km -147..-197 m; 14 km -41..-72 m;
 16 km -50..-95 m;6 km -106 m (design 3 -199 m).
- The seam (8 m columns, rhoInf 1,900-2,300 m): the largest step 17.7 m per 8 m, p99 12.1 m (design 3: 1,761 m).
 Seabed deeper than 1,500 m inside 3.7 km: 0 (design 3: 32 km2). Rim fill: 18,522 of 51,373 rim columns buried (median 59 m, at
 most 952 m: the crater edge's feet). Protected columns with the planet above their lowest floor: 40 of 125,208 (at most 29.6 m)
 before the regeneration's guard.
- Continuity: rings 0 (design 3: the moat, 12.4-14.9 km, 12 of 12 sectors); km-scale gradient p50 26 m/km, p99 548 m/km, 1.3 % of
 the profile over 500 m/km (design 3: 16 %).
- Islands: land pixels with a non-island material 0 of 368; tile land columns with a non-island material 0 of 8,875 (design 3: 236).
 Biome G void: 0 pixels (design 3: 6,022, about 26 km2).
- Stamped field on planet biomes: basin (grandReef rules) 43 placements per km2, plains (sparseReef) 78, trench (bloodKelp) 35.
- The tool: design 3 174 s, 754 MB peak; design 5 with the scan 188 s, 885 MB (+ about 1 s for the rim and the cap); design 5's
 sculptHash on this machine `0x78fb4aec4b1558f1`, the scan's input pin `099a6efa25e3f90b`.

## 4. Release: generated on the user's machine

At release the installer generates the planet files from the user's own install and the layout (a converter step, like the
terrain store): nothing derived from Subnautica's octrees ships.
- **Pin.** The sculptHash line of the conventions gains the layout's hash, the tool's version and the scan's input pin (fnv1a64
 of `top.i16` then `lowfloor.i16`, `sn_rim.scan_hash`); a peer whose files differ is told which input differs.
- **Bytes.** numpy's exp, sin, arctan2 and arccos may round differently on another SIMD path, and one flipped 16-bit quantum
 changes the sculptHash. Until the converter is ported to C# with the polynomial math F2 already uses,
 `--verify` compares decoded heights within 1 LSB as well as the hash, and multiplayer peers use the host's files.
- **Users have no Python.** The C# converter (above) also removes numpy and scipy (the cap's min-filter) from the install path.
- **The cap without a scan.** The layout's constants: rim fallbackEdgeM 1,850 m, fallbackY -370 m, and -1,600 m under an authored
 footprint polygon (a 2,250 m circle). Can the cap be a layout constant per column band without losing the guarantee? No: a
 band's constant must lie below every protected floor in the band (the 1st percentile is -1,584 m, caves go deeper), which puts
 the floor 1,000 m under the visible seabed of the whole band, and the band's void columns become deep pits: the dead zone again.
 The per-column cap keeps both, so the constants are the fallback only.

## 5. Rows

New launcher parts `nz` (the planet), `no` (ores), `nl` (the look). Offline rows run in `check_planet` / `sculpt --verify`, each
with a sculpt `--mutant` that writes a known-bad file set into scratch.

| Row | Oracle | Mutant -> red rows |
|---|---|---|
| S8a.NZ.dead | Stratified seeded points: 16 in 2-6 km, 16 in 6-12 km, 16 in 12-20 km (the old moat's annulus), 32 beyond, none on land; camera at y -1 and -20 m; the seabed or a placed living entity within the fog distance (Subnautica's own: the distance where its waterscape transmittance exp(-extinction x d) falls to 5 %, extinction from WaterBiomeManager's settings at the camera, logged by SN); seabed distance from SE rays and from Subnautica's loaded octrees | M16 (SN: the window serves water below 300 m, no water-column spawns) -> NZ.dead, VD.presentDistance |
| S8a.NZ.continuity | rays in 24 headings, 50 m steps out to 30 km, SE and Subnautica (loaded octrees): no ring, gradient as R2 | sculpt `--mutant trough` (design 4's trough) -> C2.continuity, NZ.continuity, NZ.dead |
| S8a.NZ.seam | 32 points round the rim and 32 on the map's border: height step <= 2 m between rays 8 m inside and outside; the same material band both sides | M17 (SE: the tiles' bands one band down) -> NZ.seam, NZ.island, NZ.palette, N10.material |
| S8a.NZ.rimfill | the rim's batches: Subnautica's loaded octrees hold the planet's floor where SE has it (the union) | M28 (SE: the rim batches served from the install only) -> NZ.rimfill, NZ.dead (crater points), NZ.continuity |
| S8a.NZ.island | tile land columns near 8 island spots and every planet island pixel: SE's material is Grass or Stone | M18 (SE: tile land back to Sand_02) -> NZ.island |
| S8a.NZ.palette | 16 points per palette role: SE's subtype is the palette's; the window's Subnautica type unchanged | M10 (existing: every type 1) -> NZ.palette, N10.material |
| S8a.NF.cost / scope / determinism / fallback / memory | as today plus spots at rho 20, 40, 90 km, the antipode face, a face seam; determinism from a cold cache in two orders | M15 (failAt) -> NF.fallback (exercise); M19 (LRU never evicts) -> NF.memory; M20 (slots ignore the lattice) -> NF.repeat.planet, NF.determinism |
| S8a.NO.cover / vein / agree / shrink / immune | (part `no`) every outcrop within 100 m has a deposit; every deposit lies in a cell SE's own detector reports for that ore; this mod's centroid = SE's detector's within 1 m and the deposit within 5 m of it horizontally, or an edge exposure on its own ore (SE's material 1 m inside the anchor; undrilled); a drill shrinks it (the ore within 6 m of its first anchor) or removes it; 60 Subnautica drill hits leave it whole | M21 (SN skips every third record) -> NO.cover; M22 (SN adds a deposit 40 m from any record) -> NO.vein; M23 (SE offsets anchors 8 m) -> NO.agree; M24 (SN ignores updates) -> NO.shrink; M25 (SN drill gets through) -> NO.immune |
| S8a.ND.props | one object per family (Pink Cap, scrap metal, limestone outcrop, coral plate, big coral tubes) over a carve point: all hidden after the carve, and again after a restart | M26 (SN: v1's candidate rule) -> ND.props |
| S8a.N10.veins / oredensity | offline: the vein table per class within x0.5-x2, the share within SE's EarthLike +-35 %; in game (part `no`): SE's own detector (MyDepositQuery) over 300 m round 16 crater points (a 4 x 4 grid at +-500 and +-1,500 m) and 32 planet points (11.25 deg apart at 15, 22, 30 km), the camera 30 m above the seabed; detector cells of one ore within 64 m are one deposit; the crater within the rules' cap x 1.5, each class within x0.5-x2 of the table in its depth band (>= 3 points); design 3 (no rules file) is information | sculpt `--mutant oredense` -> N10.veins, N10.oredensity; M27 (SE: the crater's cube rule on whatever the rules say) -> N10.oredensity (crater) |
| S8a.NL.look | section 6 | M29 (SN: water-column spawns and flora off beyond the map) -> NL.look |
| Offline C2.relief / matpatch, in game NL.checker | section 11 (the checker): the neighbouring-pixel height step p90 <= 2.5 m on shelves, plains and basins; no single-pixel material island outside ores, patch sizes per biome; the seabed's dark-block share in far captures within the spawn's + 10 points | sculpt `--mutant rough` -> C2.relief, NL.checker; `--mutant speckle` -> C2.matpatch |
| Offline C2.depths / void / cap / islandmat / seam / continuity / novoid | the histogram's bands; no seabed deeper than 2,000 m; the bicubic surface at 4 m never above a protected floor - 30 m; no land without an island material; the floor continuous across the map's border; R2's rule; no G void | sculpt `--mutant deep`, `nocap`, `sandland`, `square`, `trough`, `voidG` |

## 6.

- Captures of Subnautica's picture (the launcher's screen capture) at the spawn inside the map (three captures, the spawn's own
 spread sets the tolerances) and at 2 far points per planet biome, 5-40 km from the site, chosen deterministically from the layout
 (the first pixel of each biome on a seeded spiral that lies at least 2 km inside the biome), each at 8 m and 25 m above the
 seabed, pitch -15 deg, forced noon (the time sync), the same fog; plus two orbital captures.
- `look-sheet.png` in the session folder: the spawn on the left, the far points on the right, labelled with biome, distance and depth.
- Row S8a.NL.look per far capture against the spawn's: the flat-seabed fraction (the share of 16 px blocks below the image's
 edge-density threshold), the silhouettes above the horizon line (connected components of the dark-on-light mask in the upper
 third), the colour histogram distance (chi-square over 8 x 8 x 8 bins), the entities in view (Subnautica's rows: live spawns and
 deposits within the frustum and 60 m). Tolerance per metric: the spawn's own spread (max - min over its three captures) times 3,
 at least an absolute floor (flat fraction 0.10, silhouettes 2, histogram 0.15, entities 5); within tolerance on 3 of 4 metrics
 passes. the row keeps the work honest between his looks.

## 7. Rollback and the gate

1. Before regenerating: copy the 12 planet PNGs, `PlanetGeneratorDefinitions.sbc`, `s8a_ref.py`'s canon and `S8aConventions`
 (terrain 5) to `%LOCALAPPDATA%\DeepEngineers\rollback\planet-design3-<date>\` with a sha256 manifest; `gen_planet_files.py
 --rollback <folder>` restores them and re-hashes. Run `--check-world` on every world that used the planet (their voxel edits stay
 on the new base after a rollback).
2. pin bump to terrain 6. The regeneration
 list: the R1-R5 rules as previewed; the crater's ore density switch (`ores.crater`, section 2); the band-limited relief, the
 per-biome material groups and the Subnautica blend types (section 11, the checker), each with its offline row (C2.relief,
 C2.matpatch) and sculpt mutant.
3. Preconditions in the same deploy: the window's union batches (R3), the water-column table (R7), the flora v2 fixes (R9).
4. No deploy until the full S8a queue (c2, n7, n10, nd, nf, nz, no, nl) is green on a throwaway copy of the world (nf in its own
 session: it switches the ring field on for the whole session).

## 8. Cost

| Part | Tokens |
|---|---|
| Regeneration: design 5, the guard, the palette, islands, tile bands, pin 6, offline rows and sculpt mutants, rollback tooling | 200 k |
| The checker: band-limited relief, per-biome material groups (SE ComplexMaterials), Subnautica blend types, C2.relief, C2.matpatch, NL.checker | 70 k |
| The window's union batches for the rim and the void columns (R3) | 200 k |
| The 3D field planet-wide (lattice slots, equal-area cells, LRU, twin, C#, vectors, far nf spots) | 450 k |
| The field's cost fix | 120 k |
| The water-column table and NZ.dead | 80 k |
| Ores: the SE scanner, Subnautica's deposits, part `no`, rows, mutants (the Wire core is done) | 250 k |
| Props: flora v2 fixes, ND.props, part and rows | 120 k |
| The nz and nl parts, rows and mutants | 200 k |
| Queue on a throwaway copy, review fixes | 100 k |
| Later: the C# converter (release, R4 of section 4) | 150 k |
| Deferred to S10: life at Subnautica's density (R7), view distance (section 10) | 650 k |

## 9. View distance (deferred to S10; the design stays)

Subnautica draws terrain through its clipmap: level L has 2^L m voxels, levels 0-2 7 chunks of 16 voxels (112, 224, 448 m), level 3
8 x 4 (1,024 m, +-512 m), level 4 one chunk; window terrain therefore never reaches beyond about 512 m. One window batch costs 497 ms
(p95), so a backlog of 73 batches (470 in another session) is 40 s to 4 minutes of stale terrain. Plan: a coarse far ring written
from LOD-3 reads (1/512 of the voxel work), frustum-and-fog priority, Subnautica's far path fed from the heightmap. Rows
VD.firstPicture, VD.presentDistance, VD.backlog, VD.memory.

## 10.

Design 5 is the floor any route starts from (continuity, the vein rule, the palette
islands, the rim); its planet field (shelf, plains, ridges, basins) is the placeholder each route replaces.

**Route A: statistics.**
1. Census tool over the install (the octree scanner, the entity census and the brush cutter exist): per Subnautica biome, the depth
 distribution, the relief spectrum (slope and curvature histograms, overhang share = columns with a floor under a ceiling, cave
 mouths and pillars per hectare from the 3D scan), rock-type proportions (block types), every prop, plant and creature density
 with its clustering (pair correlation, Clark-Evans), spacing and depth rules, biome adjacency and patch sizes (biome map
 components). Output: a pinned numbers file (never the map), generated on the user's machine at install. 200 k.
2. Biome layout generator: patches with the measured sizes and neighbours (the shallows -> kelp -> plateaus -> mushroom -> reef ->
 blood kelp sequence along the depth gradient, an adjacency Markov chain), equal-area on the faces, the real 2 km map at the origin. 150 k.
3. Per-biome terrain: noise tuned to each biome's relief spectrum plus the brushes for overhangs and caves through the planet-wide
 F2 field (R6); never a flat shelf. 150 k on top of R6.
4. Props, flora, creatures at the measured densities and rules (R7's slot and template system). 400 k (= R7).
5. Oracle: per biome, the generated planet's statistics against the census, offline on the layout and in game by rays, entity counts
 and the look part; tolerance = Subnautica's own spread between patches of the same biome (the generated value within the 10-90 %
 range of the real patches). 150 k.
Route A total about 1.05 M beyond design 5 and R6. **First slice that shows a biome outside the map reading as Subnautica:** Grassy
Plateaus (large, shallow, distinctive: red grass, plateaus with cliffs) on one 1 x 1 km planet patch at 20 km: its census, its
terrain (noise + brushes), its entities from Subnautica's own loot table and baked templates, the look part against the spawn; about
250 k.

**Route B: real-map chunks as macro-brushes.**
- Cut the real map into biome pieces of 500 m to 1.5 km (a Grassy Plateaus piece, a Kelp Forest, a Mushroom Forest, a Grand Reef
 stretch, Blood Kelp, Dunes, Sparse Reef) from the install's octrees with their entity cells, excluding every landmark (the Aurora,
 islands, the lifepod area, the Lost River, bases, wrecks: an authored mask of polygons and depth ranges); place them through the
 F2 field by the layout generator, in quarter turns and mirrored (8 variants), seams blended over 50-100 m, noise between chunks;
 props and plants come along by transforming the batch's entity cells; the landmarks stay in the real map only.
- What exists: the octree reader and writer with its proven round trip (c1), Subnautica's field sampled into SE voxels through the
 site map's transform (the S4 tiles), SE voxels back into Subnautica octrees (the window), the brush cutter (the same cut at 32-64 m).
- What is new: the entity-cell transform (protobuf object trees: positions and rotations rewritten, cells re-keyed; mirroring has no
 entity equivalent, so mirrored chunks rotate their entities only and asymmetric props read rotated); the chunk library format,
 generated on the user's machine like the terrain store (Subnautica's octree cache is 1,165 MB for its 11 km2 of terrain, about
 100 MB per km2; its cells 164 MB, 15 MB per km2: a library of 8 biomes x 3 pieces of 1 km2 is about 2.8 GB on disk); memory: a
 chunk is decoded per read box from its octrees (tens of MB per chunk in use, against 8 MB for the whole brush library); field cost
 per voxel: one octree lookup per voxel under a chunk (cheaper than a brush's trilinear), plus the seam blend.
- Risks: recognisability (a 1 km piece repeats every few km: with 2-5 pieces per biome and 8 variants, a player crossing 10 km sees
 the same plateau again); seams (bands where two pieces meet, visible as smoothed strips); depth mismatch (a piece's native depth
 against the layout's: re-based by +-50 m at most, beyond that its fog, lighting and spawn bands lie); landmark exclusion leaves
 about 60 % of the map usable.
- Cost: cutter, landmark mask and library 200 k; entity-cell transform and its spawner 200 k; macro-placement and seams in F2 200 k;
 the layout generator 150 k (shared with A); oracle 100 k: about 850 k.

**Route C: a hand-made planet. about
100-200 k of tooling, then his hours; F2 and R7 still apply.

**Numbers to decide.** The planet's seabed is about 45,000 km2; the real map's terrain is 11 km2. Route B covers it by repetition
(each 1 km2 piece about 2,000 times if every biome had 3 pieces); route A makes every patch unique at the cost of looking
statistically, not literally, like Subnautica

## 11. Open points

- **The first look sheet.** The far frames were dark and showed "terraces". Measured:
 - *Darkness.* Subnautica's biome at the far Grassy Plateaus cells is its own `grassyPlateaus` with its own water settings
 (sunlightScale 1.75, ambientScale 2, murkiness 0.45, start distance 35 m, SkyGrassyPlateaus), the same as inside the map. What
 differs is the clock: `_UweLightScalar` 0.015 at the far points against 1.0 at the spawn. Each treadmill chart step gives SE a new
 site map, SunSync re-bound on it and cleared the dev phase (`de.s7 time 0.5`), so SE's natural clock came back (c 0.96, the sun
 65 deg under the horizon) for every far capture. Fixed (SunSync keeps the dev phase across a re-bind; it clears on world unload or
 `de.s7 time off`). The biome rule is not the cause.
 - *Terraces* (the night frames): the same patches as the checker below, seen by moonlight.
- **The checker on the far Grassy Plateaus.** At noon the far Grassy Plateaus (26-30 km,
 -165/-175 m) show hard light sand and dark patches in a rough square grid near the heightmap's pixel scale; the mountains at 18 km are
 a smooth bare plain. Measured on design 3's files and on the window's served octrees there:
 - *Not the material map.* The `_mat` R channel is one value (1) over 13 x 13 pixels round both far points (G one biome, B ore only in
 vein pixels), and the served voxels are one Subnautica type (1, Sand02) over two whole batches but one iron patch. SE's
 nearest-pixel material lookup has nothing to alternate.
 - *The relief at the pixel scale.* Neighbouring 46 m pixels differ by p50 3.5-5.8 m, p90 9.9-10.9 m, max 14-20 m: slopes of
 0.08-0.24 that flip sign from pixel to pixel. The served seabed is smooth at 1 m (no stair), but its slope field over nine batches
 (480 m) is p10/p50/p90 0.05/0.13/0.26 in patches tens of metres wide. The source is the layout's noise below the pixel grid:
 `hills` (10 m x relief, 160 m, 2 octaves: the second at 80 m), `detail` (2 m, 90 m, 2 octaves: 45 m) and the ridged `boulders`,
 sampled every 46 m, so they alias into per-pixel bumps; design 5 carries the same three blocks.
 - *The look.* Subnautica shades those facets: the lit sand where a facet is gentle or faces the sun, a dark tone where it is
 steeper or faces away (a hard edge: its terrain shader's slope blend, or the sun's shadow of the 5-10 m bumps; one capture at
 noon and one at c 0.35 of the same spot tells which: a pattern that moves with the sun is shading, one that stays is the slope
 blend). On the sheet: dark seabed blocks (under 0.6 x the seabed's median luma) 28 % at the first plateau point against 7-15 %
 at the spawn.
 - *The fix (design 5's regeneration list, section 7):* (1) band-limit the heightmap: no noise octave shorter than 4 pixels (184 m)
 on shelves, plains and basins; the 80 m hills octave, the 45 m detail octave and the boulders leave the heightmap (small relief
 belongs to the 3D field, R6, which Subnautica meshes at 1 m without aliasing); (2) one dominant material per biome with
 transitions by slope and depth through SE's own `ComplexMaterials` rules (a material group per biome value in R, layers chosen by
 slope and height, as EarthLike does) instead of one material per pixel, and dithering at the pixel scale only where two biomes'
 groups meet; (3) the far biomes' dominant materials mapped to Subnautica's own blend types of that biome (Grassy Plateaus:
 `GP_SandToRock`, `GP_SandToRock_steep`, palette 160-164), so Subnautica's slope blend draws sand to rock as in the real plateaus.
 - *Rows.* Offline S8a.C2.relief: per globe band outside ridges, trenches, chains and islands, the neighbouring-pixel height step
 p90 <= 2.5 m (slope 0.05 at 46 m); design 3 fails it at its Grassy Plateaus (p90 about 10 m); sculpt `--mutant rough` (the
 short octaves back) -> red. Offline S8a.C2.matpatch: in the material map no single-pixel island (a pixel whose 8 neighbours all
 differ) outside ore and vein pixels, and the patch-size distribution per biome reported (median, p10); `--mutant speckle` (every
 tenth pixel flipped) -> red. In game S8a.NL.checker (part `nl`): per far capture the share of seabed blocks under 0.6 x the
 seabed's median luma within the spawn's share + 10 points (measured: 28 % vs 7-15 %); the thresholds are fitted on design 5's first
 sheet.
- **NL.look's metric.** The far frames at L 4-23 against the spawn's 83-105 read "5/8 like the spawn": terrace edges counted as
 texture and as silhouettes. Now the brightness, the colour and the plants and creatures within 40 m (`de.s8a life`, the registry of
 started entities, this mod's deposits apart) must each match, then 2 of 3 texture metrics; silhouettes count only above the horizon
 line. The same sheet reads 0/8.

- **The night light in the crater** (Grassy Plateaus evenly lit, Crash Zone black, the same sun state): Subnautica's own per-biome
 water settings and biome skies, or our time drive moving `timePassed` and the sun light apart: the `s8a.biome` row now logs the
 water settings, `_UweLightScalar` against `_UweLocalLightScalar`, the biome sky, the atmosphere volume's biome and uSkyLight's
 ambient at each change of Subnautica's biome.
- **From orbit the ring stays darker than the shelf** where its biomes are deeper (the Subnautica map's surroundings at its rim
 depth, -370 m median); a gradient, not a ring (the continuity rule).

## 12. Route B, first slice: the sample chunk and the chunk library format

Decision: design 5 with the review's changes is the floor; route B (real-map chunks as
macro-brushes through the F2 field) is the terrain and prop source beyond the map; route A's census is only the check. Nothing

**The sample.** One Grassy Plateaus chunk, the 640 m window SN x -960..-320, z -512..128 (66 % Grassy Plateaus, the best 640 m
window of the install's biome map; 83 % at 512 m; no 1 km window holds more than 40 %), rows 82-94 (SN y -416..0: the stored
range starts 64 m under its lowest surface octree; the Lost River under the plateau is cut away, rock below the range).
Placed at site-v1 SN (4158, -19563), 20 km from the site, on design 3's shelf (planet biome Grassy Plateaus, seabed median -121 m,
sd 8 m over 900 m): in the point's own tangent frame (gnomonic on the sea sphere, no stretching; site v1's frame carried along
the great circle, so the chunk's axes are SN's there), one quarter turn (the entities' transform exercised; no mirror: it has no
entity equivalent), re-based -25 m (the planet's median minus the chunk's, limit +-50 m), blended into SE's heightmap over 80 m
inside its square. Served seabed p5/p50/p95 -188/-121/-80 m. Previews `tests/out/s8a/stageNS-preview/` (relief, seam profile,
entities by family).

**The field** (`DeepEngineers.Terrain.S8aChunkField`, twin `tools/planet/s8a_chunks.py`): per planet-local point the chunk's own
signed distance (the trilinear of Subnautica's sample over the voxel centres: near voxels (density - 126) / 126, far +-1; beyond
the near band the vertical distance to the column top, so coarse LODs and the blend see distances, not a +-1 m clamp), blended
with the heightmap's (h - y) by smoothstep(0, 80 m, the distance inside the square); one octree descent per voxel sample, no
decode, no cache, any thread; square roots only, so the twin's bytes are the C#'s (vectors `tests/s8a/vectors/sample/`, a
synthetic chunk committed, the store's chunk's hashes when present; `S8aChunkFieldTests` both byte for byte). Served by the same
`MyPlanetStorageProvider.ReadRange` prefix as the ring (S8aRing), first, for reads at LOD <= 3 that meet the sample's zone; the
Intersect postfix and the window's LOD-3 classification widen its boxes; `DEEPENGINEERS_S8A_SAMPLE=on` (the launcher sets it for
`ns` and `nl` only); with it on, the window's and the charts' keys carry `'|sample|' + identity` (fnv of the placement text, the
chunk's bin and tsv).

**Entities.** From the install's baked cells (read-only): the cell roots' children with their Transform (protobuf-net fields 1
position, 2 rotation, 3 scale, `ProtobufSerializerPrecompiled` Transform serializer) and the `EntitySlotsPlaceholder` slots
Left out: every prefab path
naming a landmark (Aurora, wrecks, lifepods, escape pods, Precursor, Degasi, starship debris, abandoned bases), every entity
within 48 m of a landmark root, the unique families (fragments, data boxes, PDAs, atmosphere volumes) and class ids without a
prefab. Kept 39,247 (33,703 slots, 5,392 flora, 69 props, 68 lights, 15 creatures), left out 2,685 (2,618 near the 31 landmark
roots of the window: lifepod 17, three explorable wrecks, cargo debris). SE places them (turned, re-based) and writes those
within their cell level's radius (100/150/250/400 m) + 60 m into `<store>\<storeKey>\sample-entities.tsv` in the current chart's
SN coordinates (the rotation through the chart's frame at each entity); Subnautica spawns baked records by class id
(`PrefabDatabase.GetPrefabAsync`) and fills slots with its own loot table (`CellManager.GetPrefabForSlot(IEntitySlot)` with an
`EntitySlotData`, once per slot and session), transient, moved on a chart step.

**Rows (part `ns`; offline rows in `s8a_chunks.py verify`, also run by `ns` on 500 octrees).** S8a.NS.ready; S8a.NS.cut (no
landmark or unique prefab kept, none within 48 m of a root: PASS offline, 31 roots in the window); S8a.NS.roundtrip (every
octree's bytes = the install's, decode -> our writer -> the same bytes: PASS offline, 5,200 of 5,200); S8a.NS.placement (re-base
within +-50 m, frame orthonormal, seabed lit: PASS offline); S8a.NS.terrain (two lines through the chunk past its edges: Subnautica
vs SE within N7's budget, SE = the field's top P95 <= 0.5 m, the field = the chunk's own top where its weight is 1, P95 <= 0.5 m on
>= 5 columns off the heightmap, the C# own top = the twin's <= 0.02 m); S8a.NS.seam (one line across each side: beyond the square
SE = the heightmap within 0.5 m, every 4 m step <= max(4 m, 1.25 x the inputs' step + 0.1 x |own - heightmap|)); S8a.NS.cost and.scope (NF.cost's rule against SE's own generator in the same run; nothing served outside the square or above LOD 3);
S8a.NS.determinism (the C# bytes and SE's generator read = the twin's); S8a.NS.entities (per spot within 60 m: SE's records per
family = the chunk's own +-15 % + 3, Subnautica's live objects >= 90 % per baked family, every slot rolled, some filled). Part `nl`
runs with the sample on: its far points are the sample's two core spots and one design-3 Grassy Plateaus point beside them
(S8a.NL.look judges the sample's; the design-3 one is the sheet's "before").

**The cost (S8a.NS.cost; the first run `ns` 20261024-113052: 287 ns per voxel at LOD 0 against SE's own 41; LOD 1 p95 10.46 ms
against a budget of 7.63; LOD 3 4.27 ms against 4 x 0.96 + 0.5 = 4.34).** Per voxel the field spent the tangent frame (about 10 ns), the
heightmap's Catmull-Rom (about 35 ns, at every voxel, even where the chunk's weight is 1) and eight octree descents for the trilinear
(about 60 % of the time). The fast path (`S8aChunkField.ServeBox`; the per-voxel field kept as `ServeBoxReference`) gives the same
bytes: the twin's vectors still pass and `S8aChunkFieldTests` compares the two on 560 random boxes over the synthetic and the store's
chunk at LOD 0-3, byte for byte.
- Per column, from the decoded octrees at load: the highest voxel that is not far water and the lowest that is not far rock. A sample
 over the first (or under the second, or under the stored floor) is -1 (+1) without a descent. This is exact, not a near-band rule:
 Subnautica's writer collapses equal near children into one near leaf, so a near density can sit metres over the surface (the first
 try, a 3 m margin over the tops, failed the equality test on the synthetic chunk at once).
- The trilinear's eight samples, when they lie in one octree, come from a per-thread cache of decoded octrees (32 direct-mapped slots
 of 32 KB, a byte code per voxel and a table of the samples' doubles), at LOD 0-1 only (at LOD 2-3 a box crosses more octrees than
 the decodes would repay).
- Where the chunk's weight is 1 the heightmap is not read (w fc + (1 - w) fb = fc there, +-0 apart, the same byte).
- A box wholly in the core over every non-empty voxel and every top by the LOD is filled with 0; under every non-rock voxel and the tops
 by the LOD, with 255.

Measured offline (this machine, one thread, 400 random boxes over gp1, in and around the square): LOD 0 63 ns per voxel (the reference
141), LOD 1 83 (142), LOD 2 103 (150), LOD 3 136 (166). Expected in game, scaling the run by the same ratios: LOD 0 about 130 ns per voxel,
LOD 1 p95 about 6.1 ms (budget 7.63), LOD 3 p95 about 3.5 ms (budget 4.34, the tightest: the band's heightmap is what remains). SE's own
read pattern (neighbouring boxes again and again) reuses the cache more than random boxes do.

**The chunk's own block types.** The red grass is not an entity: it is the terrain's grass decoration of the block types 44 and 45 (RedSeaweedTall02/03), with GP_DirtToSand 162, GP_SandToRock 160 and the rock types the plateau's surface types (a census of the chunk's surface voxels). Through SE the window wrote the SE materials' types (Sand_02 -> 1), so the carpet and the blend types were lost. The window's converters now take the chunk's native type for every solid voxel where the chunk's weight is at least one half (`S8aSample.NativeTypeAt`: the native voxel, or the first solid one within 2 m under it; ores keep their ore types), counted in `nativeTypedVoxels`; the sample's identity gained the marker `types` so no window file made before is served. The slot fill was not the cause: it asks Subnautica's loot table with the slot's own baked biome (`EntitySlotData.biomeType`), not the position's; its 15 % fill is that table's own rate. S8a.NL.look against the reference now also requires the ground cover (the share of the frame's lower 60 % in a red hue) at least half the real plateau's: 134042 re-read gives 0/4 (cover 0.000 against 0.30).

**The chunk library format (derived on the user's machine at install time; never shipped).** Like the terrain store and the brush
library, chunks are derived from the user's own install by a converter step (`s8a_chunks.py build` today, the C# converter at
release) into `%LOCALAPPDATA%\DeepEngineers\terrain\chunks\`; the repository holds only the tool, the placement rules and hashes.
- `chunk-<name>-v1.bin`: `DECHUNK1`, u16 version 1, i32 nx ny nz (octrees), i32 ox0 oy0 oz0 (the window's first octree in the SN
 grid); u16 node count per octree (index `(ix * ny + iy) * nz + iz`); u32 length + zlib of every octree's node bytes in that
 order (Subnautica's own 4-byte nodes, copied unchanged; empty octrees 0 nodes); u32 length + zlib of int32 column tops
 `[z][x]` at 1 m in 1/256 m over the floor (`int.MinValue` none). gp1: 20 x 13 x 20 octrees, 37.6 MB of nodes, 13.8 MB on disk.
- `chunk-<name>-v1.tsv`: `de-chunk-index 1`, header lines (name, biome, window, octrees, yFloor, medianTop, landmarkRadiusM,
 nodesBytes, belowNotSolid), then `baked` (class id, family, cell level, position, rotation, scale: float32 values), `slot` (biome,
 allowed types, density, pose) and `excluded` (class id, reason, prefab path, position) lines.
- `sample-v1.txt` (the placement; later one line per placement in the layout generator's output): `de-sample 1`, chunk, chunkFnv,
 indexFnv, sn, anchor/east/north (repr doubles: the C# reads the twin's bits), turns, mirror, rebaseM, blendM, halfM.
- Pins: the identity (placement text, bin, tsv FNVs) goes into the store keys; the twin's vectors pin the field; a peer whose library
 differs has a different identity (multiplayer peers use the host's chunks, as they use the host's planet files).
- Size: the octree cache is about 100 MB per km2 raw, about 35 MB per km2 compressed; a library of 8 biomes x 3 pieces of 0.4 km2
 is about 350 MB on disk; in memory only the chunks a camera is near (37.6 MB each for gp1, the nodes uncompressed).
- At install the converter checks NS.cut and NS.roundtrip on every chunk; a missing or stale chunk refuses the sample (SE's
 heightmap serves the point) and logs the refusal; the build reads the install only.

west edge, blood kelp's trench) reads as a slope of 0.5-0.75 (`ns-seam.png`); the sample's slots fill from Subnautica's own loot
table at random (counts per family vary per session; NS.entities judges them filled, not equal); drilling the chunk is SE's planet
edit over the served content, as in F2 (not exercised by `ns`).

## 13.

idea whether ore remains; replicate SE's ores in Subnautica as voxels." So the vein itself must look like ore in Subnautica's
terrain, on every ore voxel the converter writes (outcrops and drilled walls alike); the deposit props go.

**What the engine allows.** Subnautica's terrain palette is `Voxeland.types` (`VoxelandBlockType[]`,
indexed by the octree's type byte), built by `Voxeland.LoadPaletteStatic` from the `VoxelandBlockTypePrefab` resources
 and handed to the streamer by `WorldStreamer.Start(VoxelandBlockType[],
WorldStreamer.Settings)`; the streaming mesher reads it as `IVoxeland.types`
 and each mesh layer takes `types[type].material`
Each block type owns its Material. The installed palette has 255 slots (0-254) with 22 null
ones (95, 96, 107, 108, 117-119, 121-123, 134-139, 168, 169, 177-179) and 255 free beyond it: room for nine ore types without touching a game type.

**Choice: (b) our own ore materials in the palette's free slots, with (a) as the measured fallback.**
- (b) A Harmony prefix on `WorldStreamer.Start` replaces the array with a copy whose free slots hold nine `VoxelandBlockType`s
 (filled, the layer of a plain rock type such as 16 GreyRock), each with `new Material(rock.material)` (the same terrain shader,
 keywords and lighting, so caustics, fog and the day cycle match) and our own generated textures (dark rock with veins and crystals
 in the ore's colour, one per SE ore family: Iron rust-red, Nickel green-grey, Silicon white quartz, Cobalt blue, Magnesium pale
 violet, Silver bright grey, Gold yellow, Platinum white-blue, Uranium green glow; made by our texture generator, never game
 assets). The shader's texture property names are read from the cloned material at run time and logged (`s8a.palette.ore`); a
 property that is not there disables only that ore's texture (the slot keeps the cloned rock, logged MISSING). The converter's
 `convert.material` ore rows point at these slots (a pin bump, with the regeneration's).
- (a) Existing types that may read as ore: 206 RedRock, 155 DGR_Crystal_To_Rock, 159 GR_Crystal_To_Rock, 157 DGR_GlowingCoral, 129
 LR_GroundToRock_Lights (names only so far; the blend types draw rock on steep faces, so a drilled wall may read as rock). Evidence
 first: a dev command paints a 12 m test strip of each candidate in a window batch beside the camera, one capture each at noon. (a) costs a table change; (b) about 120 k tokens with the textures and the rows.
that needs a texture that reads as ore on any face
 and no game biome's own look (a crystal type seen in the Grand Reef would make every Grand Reef wall read as ore).

**The props.** `S8aOreDeposits` (Subnautica's Drillable props on SE's outcrop records) goes off by default once the ore voxels are
in (its env switch kept for comparison); SE's outcrop scan stays as the oracle of where exposed ore is. No surface cue unless the

**Rows.** S8a.NO.voxel: every SE ore voxel the window serves within one voxel of the surface carries its ore's Subnautica type (a
per-batch census in the window worker: ore shell voxels against written ore types, 100 %); S8a.NO.look: a capture at a drilled vein
shows the ore look on the remaining wall (the wall pixels' mean colour distance from the biome rock's >= a threshold fitted on the
first capture); S8a.NO.palette: the nine slots filled at `WorldStreamer.Start` with their textures (Subnautica's row); mutant S8a.M30
(the converter maps every ore to plain rock) turns NO.voxel and NO.look red.

**Built.**
- Conventions: convert.material's nine ore rows take the slots (Iron_02 95, Nickel_01 96, Silicon_01 107, Cobalt_01 108,
 Magnesium_01 117, Silver_01 118, Gold_01 119, Platinum_01 121, Uraninite_01 122); the new line "convert.orelook" names them. Wire,
 both twins (`s8a_ref.py`, `s8a_biomes.py`), the vectors (`convert.tsv`, `biomes.tsv`), the analyzer's N10 table and CONTRACT §5.4
 moved together; both plugins deploy together (the pin differs from 5).
- Subnautica: `S8aOrePalette` (the `WorldStreamer.Start` prefix) fills the slots: `VoxelandBlockType.FilledMaterial` with GreyRock's
 layer, `new Material(GreyRock.material)` with the albedo-like texture properties replaced by our generated 256 px ore texture (dark
 rock, veins and glints in the ore's colour; `Material.GetTexturePropertyNames` at run time, normal/mask/noise maps kept), then
 `RuntimeInit`. The row `s8a.palette.ore` names the template's texture properties and what was replaced (the evidence for a retune);
 `DEEPENGINEERS_S8A_ORE_LOOK=off` keeps the rock's textures (the slots still draw).
- The paint test: `de.s8a orelook {as: <type>}` copies a palette type's material into the ore materials in place (every ore voxel
 then shows that type), `{as: "ours"}` restores ours. Part `no` captures at the drilled vein: ours, as GreyRock (16, the "no ore
 look" reference), and as each fallback candidate (206, 155, 159, 157, 129).
- The props: `S8aOreDeposits` off unless `DEEPENGINEERS_S8A_ORE_PROPS=on` (set by the launcher for part `no` only, whose NO.cover..
 NO.immune still judge them); `no` turns them off for its ore-look captures.
- Rows: S8a.NO.palette (patched, applied, 9 slots ours or the game's, >= 1 texture replaced per slot); S8a.NO.voxel (`de.s8a
 orevoxels`: the converters' census of solid surface voxels written from an ore material, all of them with the ore's type, >= 50 in
 the session); S8a.NO.look (ours against the same view as GreyRock: >= 2 % of the frame changed by a mean RGB distance >= 20, the
 candidates reported beside it); mutant S8a.M30 (the converter writes every ore as plain rock 84): NO.voxel and NO.look red.
- Not proven in game;OreColours` and
 `OreTexture`).
