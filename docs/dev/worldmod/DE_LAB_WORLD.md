# DE Lab: the S1 lab world, the planet DE_4546B and the S1.G2-E3 checks

CONTRACT §3). It covers creating the
"DE Lab" world, spawning `DE_4546B` once, moving it to an integer centre, and running S1.G2-E3 by hand. S1.G2-E3 does
not need our plugin. S1.G2-E4 and `DE.Site` need the S1 SE plugin; they are noted where they apply.

Labels used below:
- **VERIFIED**: read, the installed Water Mod 3.23 sources or the stock
 content, or proven by the no-game tests.
- **HYPOTHESIS**: true only once the planet has been spawned in game.

## 0. What you get

| Item | Value |
|---|---|
| World | "DE Lab": Creative, Empty World scenario, no asteroids. Mods: Water Mod (Workshop 2200451495), Text HUD API (758597413), DeepEngineers.World (local) |
| Planet | `DE_4546B`, spawn size **120000** (diameter); R = 60,000 m. Flat floor at MinimumRadius 57,000 m, i.e. 3,000 m below the sea. MaximumRadius 67,200 m; gravity is exactly 1 g between the two |
| Planet centre C | **(−1,000,000, 250,000, 1,500,000)**: the CONTRACT §3 test centre, so its conformance table gives in-game GPS coordinates directly |
| Sea | Water Mod radius **1.0526316** (float32 0x3F86BCA2), so Rs = 57,000 × 1.0526316f = **60000.00390625** m. No waves, tides or currents, no crush damage, no fish |
| Dive-site anchor, SN (0, 0, 0) | SE (−1000000, 250000, 1560000.00390625), straight above C on +Z |

## 1. Before the first launch

1. Check the mod folder and deploy it.World\`

 ```
 python -I tools/worldmod/check_planet.py --decomp-se auto --water-mod auto --se-content auto
 powershell -NoProfile -ExecutionPolicy Bypass -File tools\worldmod\deploy_worldmod.ps1
 ```

 - The first command must end with `CHECK PLANET PASS`.
 - The deploy script refuses to run while SE is running. It runs the checker again, copies the 15 files and
 compares their SHA-256 values with the source.
 - The deployed folder must contain exactly these files:
 - `metadata.mod`
 - `Data\PlanetGeneratorDefinitions.sbc`
 - `Data\WaterConfig.xml`
 - `Data\PlanetDataFiles\DE_4546B\{front,back,left,right,up,down}.png` and `…_mat.png`
2. Water Mod and Text HUD API are already subscribed on this PC.
3. Launch SE through Pulsar as usual. The SE plugin is optional for sections 2–6.

## 2. Create the world (SE main menu)

1. Click **New Game**, then **Custom Game**, then choose the scenario **Empty World**.
2. Set the name to **DE Lab** and the game mode to **Creative**.
3. Open **Mods** and add:
 - Water Mod (Workshop)
 - Text HUD API (Workshop)
 - **DeepEngineers.World** (listed as a local mod)

 Then click OK.
4. Open **Advanced** and set:

 | Setting (label in SE) | Value | Why |
 |---|---|---|
 | Asteroid Amount | None | a clean lab: no procedural asteroids |
 | Enable Oxygen | on | the oxygen check; CONTRACT §3 world settings |
 | Enable Airtightness | on | the Water Mod crushes only blocks next to an airtight room, so check 6 needs it |
 | Destructible Blocks | on | without it no block can take damage, and check 6 would prove nothing |
 | Enable In-Game Scripts | on | the probe script in section 7 |
 | Enable Copy/Paste | on | pasting the depth rig |
 | Meteor Activity | Safe | no meteors on the test rigs |
 | Enable Weather, Enable Economy, Cargo Ships, Enable Drones, Enable Planetary Encounters, Enable Spiders, Enable Wolves | off | nothing should spawn in the lab |
 | Enable Sun Rotation / Day Duration | on / anything | S1 does not use the sun. The exact CONTRACT values (20 min, base direction (0, 0, 1)) cannot be set in this screen: the slider is logarithmic. They matter from S7 (time sync); until then the plugin only refuses time sync |

5. Start the world. Save once with Esc, then **Save**.

Alternative: `tools/lab/make_lab_world.py` belongs to the orchestrator. It makes "DE Lab" from a copy of an
existing save and adds the local mod to both world files, `Sandbox.sbc` and `Sandbox_config.sbc`
It refuses while SE runs. If you
use it, check before section 4 that no planet of that save lies within 200 km of C: the copied save keeps its own
planets. The copy keeps the source's world settings: compare them with the table above. Sections 3–8 are the same.

## 3. Check the world load (SE log `%APPDATA%\SpaceEngineers\SpaceEngineers_*.log`)

The Water Mod reads `Data\WaterConfig.xml` of every mod in the world when the world loads (VERIFIED,
`WaterConfigComponent`). The log must contain:

```
WaterMod: Found Water Config for mod '…' (twice: the Water Mod's own file and ours)
WaterMod: Loaded Planet Config 'MyObjectBuilder_PlanetGeneratorDefinition/DE_4546B'
```

For a local mod the quoted name may be empty.

## 4. Spawn `DE_4546B` (once, with both mods loaded)

The Water Mod applies our `PlanetConfig` only when a `DE_4546B` planet is added to a running session (VERIFIED,
`WaterModComponent.MyEntities_OnEntityAdd`: server and after its own `BeforeStart`). That spawn writes the sea into the
save; afterwards the save holds it (CONTRACT §3).

There are two routes. Route A needs no clicks except the final save and no save edit; route B is the manual
alternative.

### 4A. Command route: `de.spawnplanet` (S1 SE plugin, dev build)

The plugin calls `MyWorldGenerator.AddPlanet` itself, with the **minimum corner** that puts the centre exactly at C

Section 5 is then not needed. Whether the spawned
centre really reads (−1000000, 250000, 1500000) is a HYPOTHESIS until the first run: the plugin logs it.

1. Load "DE Lab" with the S1 plugin. Before the spawn the log shows `DE.Site REFUSE no DE_4546B planet in this world
 after 600 ticks` (expected).
2. The command travels over the section, so one must exist: Subnautica in bridge mode, or `python -I tests/standins/fake_sn_host.py --no-token-check --passive --hold-seconds 900` in a second
 terminal. SE attaches within 5 s (`DE.Link UP`).
3. Dry run (nothing spawned): `python -I tools/lab/se_cmd.py de.spawnplanet dryRun=true`. Expect
 `storageName=DE_4546B-4546d120000 hillMax=0.12 storageSize=262144 offsetVoxelMapByHalfVoxel=True
 minCorner=(-1131072.5, 118927.5, 1368927.5)`.
4. Spawn: `python -I tools/lab/se_cmd.py de.spawnplanet '{}'` (in SE's chat: `/de spawnplanet`). The defaults are
 definition `DE_4546B`, size 120000, seed 4546, centre (−1000000, 250000, 1500000); each can be given. SE stops for a few seconds while the maps load; `se_cmd.py` waits up to 180 s for this reply. Expect:
 - the reply `ok: true`, `entityId`, `centre` = the request, `formula: PASS`, `storageName`, `minCorner`;
 - in the SE log `DE.Lab spawnplanet done … centre(PositionComp.GetPosition)=(-1000000, 250000, 1500000) …
 maxAxisError=0 m min-corner rule PASS storage.Size=262144 … waterMod.HasWater=True` (record this line: it proves
 the rule, and that the Water Mod added the sea during the spawn), then
 `DE.Site REBIND (de.spawnplanet created #…)` and, within 600 ticks, `DE.Site OK site v1 bound …`;
 - none of the error lines of 4B step 7 (the same load path).
5. The checks: `python -I tools/lab/se_cmd.py de.g2e3 '{}'` (section 8).
6. Save: Esc, then **Save**. Until then the planet and its sea exist only in the running session.

The command refuses, and logs `DE.Lab spawnplanet REFUSED <reason>`, when: SE is not in a world; the activation gate
is not open (offline single player only); no `PlanetGeneratorDefinition` of that name is loaded (it lists the loaded
ones: is DeepEngineers.World in the world?); a `DE_4546B` planet already exists; another planet's centre lies within
200 km of C; the storage name is taken; or `MyPlanets.CanSpawnPlanet` says no (the world's MaxPlanets). If
`AddPlanet` throws after the planet was added (another mod's `MyEntities.OnEntityAdd` handler), the planet stays:
the command still raises RequiresDX and rebinds the site, logs `AddPlanet THREW …` on the `done` line and replies
`ok: false` with the planet's fields; run `de.g2e3` and read the SE log before saving. Exit codes and the transport
are described at the top of `tools/lab/se_cmd.py` (a timed-out reply says whether SE took the command; a
`se_cmd.py` killed mid-run leaves devControl bit 0 set: `python -I tools/lab/se_cmd.py --clear-stale-harness-bit`
once no other harness runs).

### 4B. Manual route: the spawn menu

1. In the world, press **Shift+F10** to open the **Spawn Menu**.
2. Choose the category **Planets**.
3. Under **Select Type:**, choose **DE_4546B**. The generator's display name is its SubtypeId.
4. Drag **Select Size:** fully to the right. The label must read **120 km**: the slider maximum, 120000, which gives
 R = 60,000.
5. Under **Generation Seed:**, type **4546**. The seed does not change this flat planet; it only fixes the storage
 name `DE_4546B-4546d120000`.
6. Click **Spawn**. A sphere preview appears about 360 km ahead of the camera. Left-click to place it. The planet's
 centre lands where the preview's centre was, which is not an integer position.
7. Check the SE log.
 - These lines must be absent (VERIFIED: with them SE silently falls back to an all-zero heightmap or to the default
 material):
 - `Error loading heightmap DE_4546B`
 - `Could not load texture`
 - `Heighmap texture` (sic)
 - `Could not load voxel material`
 - `Cubemap faces must be all the same size`
 - This line is expected and harmless: `PERFORMANCE WARNING: Postprocessing voxel triangle decimation steps not
 defined for …`. The definition has no `MesherPostprocessing`.
8. Press Esc, then **Save**. Then **Exit to Main Menu** and quit SE. The next step edits the save, which SE must not
 have open.

After route B the planet's centre is wherever the preview was: continue with section 5.

## 5. route B only)

**After route A (section 4A) skip this section**: the planet was created at the integer centre. Moving the character
is optional there: the S1 sessions drive the spectator (`de.teleport`, `de.path`). To park the character 50 m above
the water anyway, run `tools/lab/set_planet_position.py` with SE closed; it rewrites the planet `Position` to the same
min corner, so the planet does not move.

It edits one world file and deletes its
binary cache. `tools/lab/set_planet_position.py` does steps 1–5 by script (backup first).

1. Back up the folder `%APPDATA%\SpaceEngineers\Saves\<SteamID>\DE Lab\` (copy it next to itself).
2. Open `SANDBOX_0_0_0_.sbs` in a text editor and find the planet entity. It is the
 `<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Planet">` whose `<StorageName>` is `DE_4546B-4546d120000`.
3. In its `<PositionAndOrientation>`, set the first element to:

 ```
 <Position x="-1131072.5" y="118927.5" z="1368927.5" />
 ```

 Leave `Forward`, `Up` and `Orientation` as they are (planets never rotate).
4. Find the character, the `<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Character">`. Set its `Position` to
 `x="-1000000" y="250000" z="1560050"`, which is 50 m above the water over the anchor. If the character is seated,
 leave the seat and save before editing.
5. Delete `SANDBOX_0_0_0_.sbsB5`. SE loads that binary copy before the `.sbs` when it exists (VERIFIED,
 `MyLocalCache.LoadSector`) and recreates it from the edited `.sbs`. At the next load SE shows the "convert this world
 from XML?" message box: click OK. That is the reload from the `.sbs`. **So

 cannot answer the box). Putting the B5 back from the backup makes SE load the binary sector again and overwrite the
 edited `.sbs` at its next save.

Why the position ends in.5 (VERIFIED):
- The saved `Position` is the planet's minimum corner. The storage of this planet is 262,144 voxels wide
 (`FindBestOctreeSize`).
- When SE loads any voxel map it adds half a voxel (`MyPerGameSettings.OffsetVoxelMapByHalfVoxel`).
- So C = saved Position + 131,072.5 on each axis. The stock "Star System" EarthLike is saved at
 (−131072, −131072, −131072), so its centre is (0.5, 0.5, 0.5).

The sea survives the move: the Water Mod saves it under the planet's EntityId, not its position (VERIFIED,
`WaterModComponent.SaveData`/`BeforeStart`).

For another integer centre, `python -I tools/worldmod/check_planet.py --centre X,Y,Z` prints the numbers.

## 6. Reload and confirm

1. Choose **Load Game**, then **DE Lab**. The character starts 50 m above the water.
2. Run `/wsettings` in chat. Every value below must match:

 ```
 Radius: 1.052632
 WaveHeight: 0
 TideHeight: 0
 CrushDamage: 0
 CurrentSpeed: 0
 EnableFish: False
 ```

 `/wradius` must print `The water's radius is '1.052632'.`

 - The Water Mod prints the float with 7 significant digits: 1.0526316 shows as 1.052632. The bit-exact check is
 S1.G2-E4 (the plugin compares `GetPhysical(planet).Item2` with 60000.00390625f).
 - `/wrradius` is a different command, "relative radius": it prints (|camera − C| − 2)/57,000 to five decimals. With
 the camera at the water it prints 1.05260, and about 1.0535 at 50 m above. It never shows the setting, so do not
 use it for this check.
3. Turn on `/wdepth` (it needs Text HUD API). It shows the camera's depth or altitude in whole metres.

## 7. Test rigs and the probe script

**Rig A, "DE Probe"** (a small-grid ship, built at the water). It needs:
- a cockpit or remote control
- a programmable block
- a battery
- an air vent, open to the air (not in a sealed room)
- at least one small atmospheric thruster, plus enough thrusters to hover
- a gyroscope

**Rig B, "DE Depth"** (a large-grid station):
- a hollow 3 × 3 × 3 cube of Light Armor Blocks: 26 blocks, centre cell empty, so the centre cell is an airtight room
- a remote control, a programmable block and a battery attached outside the cube

The Water Mod crushes only blocks that border an airtight room and the water: here, the six face-centre blocks
(VERIFIED, `WaterPhysicsComponentGrid.CanCrushBlock`).

To place Rig B at depth:
1. Build it once, select it and press Ctrl+C.
2. Press F8 (spectator) and fly down while watching the `/wdepth` readout.
3. At the depth you want, press Ctrl+V and left-click to place it.
4. A large grid placed on its own is a station. If the paste comes out as a ship, use its terminal: **Info**, then
 **Convert to station**.

**Probe script.** Our own code: paste it into each programmable block, then **Check Code** and **OK**. It refreshes
every 100 ticks and shows its lines in the block's terminal (and in its Custom Data).

```csharp
// DeepEngineers S1.G2-E3 probe (DE_4546B). Values for the block's own grid.
const double R = 60000.0; // DE_4546B AverageRadius = spawn size 120000 / 2
public Program { Runtime.UpdateFrequency = UpdateFrequency.Update100; }
public void Main(string argument, UpdateType updateSource)
{
 var ctls = new List<IMyShipController>;
 GridTerminalSystem.GetBlocksOfType(ctls, b => b.CubeGrid == Me.CubeGrid);
 if (ctls.Count == 0) { Echo("Add a cockpit or a remote control to this grid."); return; }
 var c = ctls[0];
 Vector3D C;
 if (!c.TryGetPlanetPosition(out C)) { Echo("Not in natural gravity."); return; }
 double sea, surf;
 c.TryGetPlanetElevation(MyPlanetElevation.Sealevel, out sea); // |controller - C| - AverageRadius
 c.TryGetPlanetElevation(MyPlanetElevation.Surface, out surf); // distance from the centre of mass to the floor
 Vector3D ctl = c.WorldAABB.Center, com = c.CenterOfMass;
 double g = c.GetNaturalGravity.Length;
 var sb = new StringBuilder;
 sb.AppendLine("DE S1.G2-E3 probe");
 sb.AppendLine(string.Format("centre {0:F3}, {1:F3}, {2:F3}", C.X, C.Y, C.Z));
 sb.AppendLine(string.Format("R {0:F3} m", (ctl - C).Length - sea));
 sb.AppendLine(string.Format("sea elev {0:F3} m", sea));
 sb.AppendLine(string.Format("floor {0:F3} m", (com - C).Length - surf - R));
 sb.AppendLine(string.Format("gravity {0:F3} m/s2 = {1:F4} g", g, g / 9.81));
 var vents = new List<IMyAirVent>;
 GridTerminalSystem.GetBlocksOfType(vents, b => b.CubeGrid == Me.CubeGrid);
 sb.AppendLine(vents.Count > 0 ? string.Format("oxygen {0:F3}", vents[0].GetOxygenLevel): "oxygen (no air vent)");
 var thr = new List<IMyThrust>;
 GridTerminalSystem.GetBlocksOfType(thr, b => b.CubeGrid == Me.CubeGrid && b.BlockDefinition.SubtypeId.Contains("Atmospheric"));
 if (thr.Count > 0 && thr[0].MaxThrust > 0)
 sb.AppendLine(string.Format("atmo thr {0:F3} of max", thr[0].MaxEffectiveThrust / thr[0].MaxThrust));
 else
 sb.AppendLine("atmo thr (no atmospheric thruster)");
 Echo(sb.ToString);
 Me.CustomData = sb.ToString;
}
```

## 8. S1.G2-E3: what to check (pass values)

**Without rigs.** `python -I tools/lab/se_cmd.py de.g2e3 '{}'` (chat `/de g2e3`) computes checks 1–7 and 9 from the
game's API and logs one `DE.G2E3 <check> <value> <expected> PASS|FAIL (how it was computed)` line per row, then
`DE.G2E3 SUMMARY n PASS m FAIL k MANUAL`; on a healthy site: 24 PASS, 0 FAIL, 2 MANUAL. What it computes:
- the planet centre (`PositionComp.GetPosition`) and `MinimumRadius`/`AverageRadius`/`MaximumRadius`;
- the sea through the Water Mod API and settings: Rs bits 0x476a6001, radius bits 0x3f86bca2, wave, tide, crush and
 current 0, fish and volumetric off;
- check 4 as `|P − C| − AverageRadius` at the water point P = C + U0·Rs: the formula of `TryGetPlanetElevation(Sealevel)`
 applied to the exact water point, not a floating hull, so it reads Rs − AverageRadius = 0.0039;
- natural gravity at the water, −1,000 m and −2,000 m along U0;
- air density, oxygen as an unsealed air vent reads it (`MyOxygenProviderSystem.GetOxygenInPoint`, the planet's own
 `GetOxygenForPosition` in the note), and the stock small atmospheric thruster's force multiplier at the water;
- the floor (`GetClosestSurfacePointGlobal`) at the anchor, the tile corner SN (2048, −500, 2048) and the planet's +X
 face;
- the voxel material 1.5 m below the anchor floor.

Check 8 (crush on Rig B after 60 s) is `MANUAL (rig)` and check 10 (load errors) is `MANUAL (log)`. The rows' exact
names and tolerances are in `src/DeepEngineers.SE/LabLogic.cs` (`G2E3`). The table below remains the rig route.

| # | Check | Where | Pass |
|---|---|---|---|
| 1 | Planet centre | probe `centre`, any rig | −1000000.000, 250000.000, 1500000.000 |
| 2 | AverageRadius | probe `R`, any rig | 60000.000 ± 0.001 |
| 3 | Sea settings | `/wsettings`, `/wradius` (section 6) | Radius 1.052632, WaveHeight 0, TideHeight 0, CrushDamage 0, CurrentSpeed 0, EnableFish False |
| 4 | SE sea level at the water (ROADMAP: `TryGetPlanetElevation(Sealevel)` 0.00 ± 0.05) | Rig A floating at rest | probe `sea elev` within ±1 m while the cockpit sits on the water line. See the note below the table for the ±0.05 m figure |
| 5 | Natural gravity 9.81 | probe `gravity`: Rig A at the water; Rig B at `sea elev` ≈ −1000 and ≈ −2000 | 9.810 m/s², 1.0000 g, at all three |
| 6 | Atmospheric thruster at 100 % | Rig A hovering 0–10 m above the water | `atmo thr` ≥ 0.999 (0.999 at +10 m, 1.000 at the water line) |
| 7 | Oxygen 90 % | Rig A at the water, air vent on and open to the air | `oxygen` 0.900 (≥ 0.899 within 10 m above the water) |
| 8 | No Water Mod crush at 1,000 m | Rig B at `sea elev` −1000 ± 50, wait 60 s | all 26 armor blocks keep 100 % integrity; no bubbles |
| 9 | Flat floor 3,000 m below the sea | probe `floor` on Rig A and on Rig B at both depths (three places); then the spectator flown down at the anchor | `floor` −3000.000 ± 0.05 at every rig. The voxel floor appears at `/wdepth` 3000. The floor material is **sand** (Sand_02, from the `_mat` maps); stone would mean the material maps did not load |
| 10 | Load errors | SE log, section 4 step 7 | none of the listed lines |

**Check 4.** Every value the probe shows, apart from `sea elev` itself, is derived from SE's own AverageRadius. The
water surface cannot be located to 5 cm by hand: a floating hull settles by eye only to about ±1 m, and
`/wradius`'s 7 digits bound Rs only to [59999.996, 60000.053]. The ±0.05 m statement follows exactly from two
facts:
- AverageRadius = 60000 (check 2).
- Rs = 60000.00390625 bit for bit (S1.G2-E4, plugin).

So the sea-level elevation at the water is +0.0039 m. The plugin's `DE.Site` check (`GetDepth` at the camera equals
SN y within 0.01 m) measures it directly.

**Optional control for check 8** (ROADMAP §3 rule 10: a throwaway copy, never "DE Lab" itself). On a copy named
"DE Lab – crush", run `/wcrush 0.5`. The six face-centre blocks of Rig B must start losing integrity within about
60 s. This shows check 8 can fail. Delete the copy afterwards.

**Bridge part of S1.G2-E3** (needs the S1 SE plugin). Run each step on a throwaway copy:
1. Change `CrushDamage` in the *deployed* `Data\WaterConfig.xml` to 0.5 and reload the copy.
 - `/wsettings` is unchanged: the sea comes from the save.
 - The plugin logs `DE.Site OK`.

 Restore the file with `deploy_worldmod.ps1`.
2. On the copy, run `/wcrush 0.5`, save and reload. The plugin must log `DE.Site REFUSE`, naming the crush damage
 field.

ROADMAP's wording ("the world keeps its saved sea, and the bridge reports `DE.Site REFUSE`") merges these two cases;
see the report of this work.

## 9.

- SE log lines: section 3 lines, and section 4 step 7 shows no errors.
- Spawn: the seed and the storage name.
- Saved planet Position: before the edit (as spawned) and after.
- Probe readouts of section 8 for each rig.
- Outputs of `/wsettings` and `/wradius`.
- Floor material seen: sand or stone.

Record with each of these whether it is a real run or a synthetic run.
