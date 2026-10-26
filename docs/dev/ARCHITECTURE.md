# DeepEngineers architecture (v1)

Status: design, frozen for v1 implementation, revised after the review (log at the end). Date:;
S0 and S1 status added. What runs today is slices S0 and S1, both done on real runs on. S0
md`): both plugins load under their real loaders, pass the in-game WM-1 subsets
share the layout-1.0 section (Subnautica creates, SE opens), exchange heartbeats, generations and the hello, reject
wrong peers and second instances, and close cleanly.md` §10): the site binds
through the Water Mod API, SE's camera drives Subnautica's ghost camera in the never-saved Freedom host save, the map is
right in both games (WM-2/3/4, G1-E5 at three FOVs), and latency, streaming and load times are measured; Subnautica
still sits under SE in `ReadyIdle`, with no display switch. Everything from S2 on is design; the S1 measurements that
size it are written into §4.1, §4.2 and §5.14.
Companion documents: `docs/CONTRACT.md` (every number and record that crosses between the two games), `docs/ROADMAP.md`
md` (journal of real runs and installs)
`docs/recon/*.md` (evidence). This document states the decisions and the mechanisms. The recon documents are needed
only to re-check the evidence, never to understand a decision.

Evidence labels (D12): **VERIFIED** = read in decompiled source, a local game or mod file, official documentation, or
**REPORTED** = web or third-party claim not checked
locally; **HYPOTHESIS** = our inference, with the test that settles it. No decompiled code or game asset is
reproduced in this repository; classes and methods are named so each claim can be re-checked. Check ids (`S2.T-D1`)
and risk ids (`R2.3`) are the ROADMAP's.

Builds this design is pinned to:

| Item | Version | How it is checked at run time | On mismatch |
|---|---|---|---|
| Space Engineers (SE1) | 1.210.014, Steam 244850 build 24675677,.NET Framework 4.8 x64 | SE plugin reads `MyPerGameSettings.BasicGameInfo.GameVersion` at run time (1210014); never the `SpaceEngineersGame.SE_VERSION` const, which the compiler would inline (VERIFIED `SpaceEngineersGame.cs`) | display bridge refuses (`DE.Version REFUSE`); the terrain tiles have their own gate (§5.6) and stay |
| Pulsar | v2.4.3, `Legacy.exe` flavour, Harmony 2.4.2 bundled | logged at start | logged |
| Water Mod | 3.23, Workshop 2200451495, API handler 50271, API version 22 | `Jakaria.WaterData.Version` read by reflection from the mod's compiled script assembly (`MySession.Static.ScriptManager`); every API delegate key we use present with the expected `Func`/`Action` type; `VerifyVersion(22, "DeepEngineers")` (it only checks ≥ `MIN_API_VERSION` 19, so it cannot detect an update); behavioural probes (Rs bits, `GetDepth` cross-check, §5.5) | version ≠ "3.23": warning, probes run; refuse only when a probe or a delegate check fails |
| Subnautica | Steam 264710 build 20241558 (game version 1.22.83031), Unity 2019.4.36f1 Mono | SN plugin logs `Application.version`; terrain manifest check (5,416 files, 1,203,085,204 bytes) on the SE side | display bridge refuses; tiles refuse only on a manifest mismatch (§5.6, fail-safe §5.15) |
| BepInEx | 5.4.23.5 (Tobey's pack 5.4.23-pack.3.1.1) | `BepInEx\LogOutput.log` | logged |
| Nautilus | 1.0.0-pre.54 (`com.snmodding.nautilus`), installed but **not used** by v1 | soft `BepInDependency` for load order only | none |
| Windows | 11 Home x64 build 22000 | display-mode measurement at start (section 5.1) | A′ if see-through is measured not honoured |

the launcher starts both executables directly (§5.14), so an update cannot slip in
between sessions unnoticed.

---

## 1.

**The idea.** Space Engineers owns the whole simulation everywhere: the player, survival, building, grids, physics,
damage, saves and time. On one dedicated ocean planet, the underwater world is drawn by Subnautica: its water,
biomes, light, fog, caustics, sky seen from below, music and its living creature ecosystem. SE's ships and character
are mirrored into Subnautica as native Unity objects, and Subnautica's creatures attack them. **Hard requirement:
underwater must look like Subnautica.**

*** SE is never diminished. Its whole space gameplay (planets, interplanetary
travel, jump drives, survival, industry, physics everywhere) stays untouched, and a full SE playthrough without ever
diving must remain possible. Subnautica is an added layer limited to the water of one planet.

Consequences that shape every section below:
- SE keeps the OS focus, its input path, its HUD, its menus and its saves at all times. Nothing SE does is routed
 through Subnautica.
- The Subnautica layer is optional at run time. If Subnautica is missing, dead or not ready, SE draws its own Water
 Mod underwater view and the game goes on (section 5.14).
- The bridge touches one planet only (`DE_4546B`). Other planets, their oceans and the Water Mod's settings there are
 never modified.
 direction are world settings, so they apply to every planet of a world that holds `DE_4546B` (section 5.12).
- **Activation gates.** The SE plugin has no effect (no window, audio, render or process change, every patch body
 inert) unless the session is offline single player and the world holds exactly one `DE_4546B` planet. Window,
 z-order and audio changes apply only while the link is up and are undone on unlink. In multiplayer (client or host)
 the plugin logs `DE.Gate REFUSE multiplayer` and stays inert. The SN plugin enters bridge mode only when the
 launcher started it (CONTRACT §6); otherwise Subnautica stays vanilla.
- Saves never depend on the plugins, and a missing plugin never destroys the player's builds (section 5.15).
1).

---

## 2. Route

Classification per `knowledge/techniques/choosing-a-mashup-route.md` (universal-modder plugin):

| Route component | What it means here |
|---|---|
| **Live passthrough (state exchange)** | both real games run; SE poses, view state, time of day and audio state go to Subnautica; damage, readiness, creature positions and static colliders come back |
| **Native geometry transfer** | Subnautica draws SE's own meshes (loaded by SE's own model loader) with Subnautica's own shader, sky and fog; nothing is a pasted picture |
| **Live data conversion, in memory** | SE collides with Subnautica's seabed natively: an SE voxel data provider reads the user's own Subnautica octree files read-only; nothing derived is written |
| **OS composition (not frame compositing)** | no pixels cross between the processes; Windows' DWM shows Subnautica's window through SE's transparent window underwater |

The four questions of the route note:

1. **Real guest behaviour or only its look?** Both games' real behaviour. SE's full simulation must run (design
 rule). Subnautica's real renderer and ecosystem must run, because "looks like Subnautica" includes its lighting,
 streaming, biome fog and creature AI. So both games run at once.
2. **Who owns the player, and how does control come back?** SE owns the player permanently. There is no hand-over
 of movement or input to Subnautica at any time, so no hand-back is needed. Subnautica's player is a ghost that
 follows SE (section 5.11).
3. **Can the host draw guest geometry itself?** Yes. Unity builds SE meshes at run time and shades them with
 `MarmosetUBER`, so caustics, fog, sun, shadows and ambient apply natively (VERIFIED mechanisms,
 `probe-ship-render.md` §3). Frame compositing is rejected: the underwater picture must be Subnautica's own.
4. **What must every player own and run?** Both games on Steam, Windows 11 x64, Pulsar, the BepInEx pack, the Water
 Mod subscription and Python 3.10+ for the launcher (section 6). Nautilus is not required.

Routes rejected:
- **Everything inside SE** (SE draws a Subnautica-like ocean): SE mods cannot touch shaders; the Water Mod's fog can

- **Frame compositing** (pictures + depth exchanged): the picture would be SE's with Subnautica pixels pasted, or the
 reverse with SE pixels on top; neither gives Subnautica's native look on SE ships.
- **Asset conversion of Subnautica into SE**: same shader limit as above.
- **Engine recreation / rebuilt Subnautica ecosystem**: far larger than running the real game.

Nearest prior projects: the SkyCraft family (SkyCraft, the MIT parts of LibertyCraft, ValCraft read only, Killcraft;
`docs/recon/prior-art.md`). We re-implement their mechanisms (named shared section, seqlocks, rings, generations,
Python stand-ins, P/Invoke on the Unity side).
visible and focused instead of hidden
voxels; (c) both sides are C#, so one wire library (`DeepEngineers.Wire.dll`, netstandard2.0) is loaded by both
plugins.

---

## 3. Components and data flows

### 3.1 Component diagram

```
 +------------------------------------------------------------+
 | DeepEngineers.Launcher (launcher.py, Python 3.10 stdlib) |
 | preflight -> start Subnautica.exe|
 | -> wait snPhase=ReadyIdle -> start Legacy.exe as a child; |
 | never kills during D3D init; kills only by exact PID |
 +--------------+--------------------------------+------------+
 (1) start | | (2) start
 v v
+-------------------------------------------------+ +------------------------------------------------------+
| Subnautica.exe (Unity 2019.4.36f1, Mono, D3D11) | | Legacy.exe (Pulsar v2.4.3) -> SE 1.210.014 (net48) |
| BepInEx 5.4.23.5 + HarmonyX (Nautilus unused) | | Harmony 2.4.2 (from Pulsar) |
| DeepEngineers.SN (net472 BepInEx plugin) | | DeepEngineers.SE (net48 Pulsar plugin) |
| Bridge section create/poll, heartbeats, | | Gate offline SP, one DE_4546B, link state |
| bridge-mode token, mutex | | Bridge section open/poll, heartbeats, watchdog|
| HostSession load Freedom save, never save | | ViewState Space/Approach(Track,Prime)/Underwater/|
| Ghost locked player driven by SE, | | Surfacing |
| camera driver, UI hidden | | Display F (scene skip + DWM alpha), A', C; |
| ArmsBreath S5a: arms (view model) shown from| | Swim Subnautica's diver model in the water |
| C1 swim ext; O2 mirror, breath | | (S2b), fins + C1 swim ext (S5a) |
| Mirror mesh chunks, MarmosetUBER, | | seHwnd calls run on SE's render thread |
| SkyApplier, paint, lights | | Audio SE mix while focused, Water Mod mute |
| Proxies section colliders (K), character | | SiteMap site record, F/F^-1, (o,q,s), P* |
| section, TakeDamage postfix | | Snapshot PrepareForDraw postfix -> C1 C2 C5 C6 |
| Statics report static primitives -> C12 | | GridExport MyModel loader thread -> C3; parts C4 |
| Window NOACTIVATE|TOOLWINDOW (restyled | | Terrain SnOctreeSource + one SnTerrainProvider |
| hidden), bottom, same rect | | per tile, 34 MyVoxelMap tiles, guards |

| Perf Idle/Track/Full (C5 perfMode) | | Combat C8 -> DoDamage; abyss rule |
| TimeOfDay DayNightCycle driven forward | | TimeOfDay sun hour angle -> C6 |
| Capture in-game PNG on request (C10) | | Perf power-throttling opt-out, budgets |
| DevControl JSON lines (C11) | | DevControl JSON lines (C11), /de chat relay |
| DeepEngineers.Wire.dll (next to the plugin) | | WaterModAPI client (own code, handler 50271, API 22)|
+----------------+--------------------------------+ | DeepEngineers.Wire.dll (next to the plugin) |
 | +----------+----------------------------+--------------+
 | named section Local\DeepEngineers_v1 | | reads (no IPC)
 +--------> [ header | seqlocks | rings ] <------+ v
 ^ Subnautica_Data\...\Build18\
 | tests only (read-only spy, C11 control rings) CompiledOctreesCache\*.optoctrees
 tests/: Python oracles, fake_sn_host.py, fake_se_producer.py, (user's install, read-only)
 WM-1 vectors
 tools/: worldmap_ref.py, wire_ref.py, site_v1.py, sn-octree-scan, sn-octrees,
 mwm-winding, probe-dwm-standins, release/check_release.py (S9)

 SE world mod "DeepEngineers.World" (data only, our own files): PlanetGeneratorDefinition DE_4546B,
 six all-zero heightmaps, Data\WaterConfig.xml PlanetConfig -> seeds the sea when the planet is first spawned.
 Subnautica reads SE texture files (DDS) from SE's Content or mod folders, read-only, by absolute path sent in C3.
 Derived caches, captures, logs and restore files: %LOCALAPPDATA%\DeepEngineers\ (never in the repo, never shipped).
```

### 3.2 Data flows

Channel ids (C0…C12) are defined in `docs/CONTRACT.md` §5. Sizes are bounds; typical values in brackets.

| Cadence | Flow | Direction | Producer, consumer | Rate | Size |
|---|---|---|---|---|---|
| per SE tick | C1 camera + player pose (+ bones) | SE → SN | SE update thread (postfix on `MySandboxGame.PrepareForDraw`) → SN main thread | 60 Hz | ≤ 8 KiB [~0.3 KiB without bones, ~4.1 KiB with the astronaut's 81 bones; cap 160] |
| per SE tick | C2 entity poses `(o, q, s, k, v, ω)` + subparts + lights | SE → SN | same postfix, same tick as C1 | 60 Hz | ≤ 512 KiB [96 B per grid; 10 grids ≈ 1 KiB] |
| per SE tick | C5 state (view state, audio, pause, foreground, epochs) | SE → SN | update thread | 60 Hz | 256 B |
| per SE tick | C6 time of day (hour angle, mapped sun direction) | SE → SN | update thread | 60 Hz | 64 B |
| per SE tick | C0 SE heartbeat | SE → header | update thread (plus `MySession.LoadingStep` callbacks during a world load) | 60 Hz | 8 B |
| per SN frame | C7 SN status (ready, streaming, presented tick, counters) | SN → SE | SN main thread | 10–165 Hz | 256 B |
| per SN frame | C0 SN heartbeat | SN → header | SN main thread, from `Update` on the unscaled clock | 10–165 Hz | 8 B |
| 10 Hz (unscaled) | C9 creature table | SN → SE | SN main thread | 10 Hz | ≤ 12 KiB (256 × 48 B) |
| on change | C3 model stream (ModelBegin/ModelChunk/ModelEnd, MaterialDef, SkinnedBind) | SE → SN | SE loader thread → SN main thread drain | first sight of a model; ≤ 2 ms of serialisation per SE tick | 64 MiB ring [10–20 MB once per large ship] |
| on change | C4 grid and part records, collision skins, rooms | SE → SN | SE update thread → SN main thread | block add/remove/integrity, grid split/merge, colour, room change | 8 MiB ring [~112 B per part; 5,000-part ship ≈ 0.56 MB once] |
| on change | C12 static primitives per 160 m batch | SN → SE | SN main thread → SE update thread | when Subnautica streams static structures near the camera | 4 MiB ring [Aurora ≈ 3.6k primitives × 48 B ≈ 175 KB] |
| on event | C8 damage records | SN → SE | SN main thread (`LiveMixin.TakeDamage` postfix) → SE update thread | per bite, shock or blast (bite interval ≥ 1 s per creature) | 1,024 × 128 B ring |
| on request | C10 capture request/result | SE ↔ SN | test harness via SE plugin → SN; result back | tests only | 4 KiB |
| on request | C11 dev control (JSON lines) | harness (or the SE `/de` chat relay) ↔ plugins | Python harness ↔ plugin main threads | dev builds only, ≤ 100 msg/s | 4 × 256 KiB rings |
| none (file read) | Subnautica octrees → SE voxel provider | file → SE | SE worker threads (SE's own voxel jobs) and the Havok step (`RequestShapeBlocking`) | lazily, per 8 m cell SE needs | 1.2 GB read-only, memory-mapped; ~1.6 MB per-octree state array |
| none (file read) | SE DDS textures → Subnautica | file → SN | SN worker threads | once per texture set | as in SE's Content folder |

Steady-state bandwidth: SE writes about 5–10 KB per tick (0.3–0.6 MB/s); Subnautica writes under 0.2 MB/s.
Bulk geometry crosses once per model and is then reused.

Thread ownership (single producer and single consumer per ring, by construction):
- SE update thread: writes C1, C2, C4, C5, C6, SE heartbeat, C10 requests, C11 replies (CtlFromSe) and, while it
 reads C7, C8, C9, C10 results, C11 commands, C12. It never calls a window function on `seHwnd` (below).
- SE model-loader thread (one dedicated thread): the only writer of C3.
- SE render thread: owns SE's window (`MySandboxGame.InitializeRenderThread` creates it there and
 `MyRenderThread` pumps it; VERIFIED). It reads one volatile flag word (skip scene, margins, alpha fill) and executes
 every queued call on `seHwnd` (`SetWindowPos`, DWM margins, styles, `SetForegroundWindow`) in the `Present` hook;
 it never touches the section.
- SN main thread: writes C7, C8, C9, C10 results, C11 replies, C12, SN heartbeat; reads C1–C6, C10 requests,
 C11 commands. SN worker threads build vertex arrays from copies, never from the section. Every SN bridge timer runs
 from `Update`/`LateUpdate` on QPC or `Time.realtimeSinceStartup`/`unscaledDeltaTime`, never on scaled time,
 `FixedUpdate`, `WaitForSeconds` or `Invoke`, because `FreezeTime` sets `Time.timeScale = 0` (VERIFIED).

---

## 4. Runtime state machine

One state machine, owned by the SE plugin, evaluated once per SE tick in the `PrepareForDraw` postfix (the camera is
final there, VERIFIED order: `BeforeUpdate → Update → PrepareForDraw → AfterDraw`, `probe-ship-render.md` §6.1). It is
published in C5. It merges the display probe's version (§3.2) and the world-mapping probe's version (§5.2); where
they differ, the world-mapping numbers win (critic C8). One symbol is used for altitude everywhere: **`y` = SN
altitude, negative underwater**.

### 4.1 Inputs (all computed in doubles on the SE side)

| Input | Definition |
|---|---|
| `P` | `MySector.MainCamera.Position` (the camera decides, not the character; third person and spectator included) |
| `onSite` | the closest Water Mod planet to `P` (`WaterModAPI.GetClosestWater`) is the `DE_4546B` planet and the site is valid (section 5.5 validation passed) |
| `y` | `\|P − C\| − Rs`. `WaterModAPI.GetDepth` returns this same signed value and is used only as the 60-tick cross-check (must agree within 0.01 m, else logged). "Depth" in logs is `−y`. In C5 since S1: `camSnY` (diagnostic) and `camVelSn` |
| `vy` | rate of change of `y` (m/s), from consecutive ticks |
| `ρ∞` | `max(\|x\|, \|z\|)` of `F(P)` in SN metres |
| `peerState` | `Alive`, `Stalled` (heartbeat stale but the process exists), `Exited` (section 5.14) |
| `snReady` | `peerState = Alive` ∧ `snPhase = Active` (the Full recipe applied and rendering: Prime, Underwater, Surfacing; `ReadyIdle` = Idle, `WarmingUp` = Track or Full before its first rendered frame; mapping in CONTRACT §5.4 "C5/C7 semantics for S2". S1 never left `ReadyIdle`, so `snReady` was never true in S1, D73) ∧ `areaBuilt` (C7: `LargeWorldStreamer.IsRangeActiveAndBuilt` true for a **±16 m** box around the current camera and for a forward box along the C5 camera velocity, length `max(20 m, \|v\|·1 s)`; was ±56 m until the S1 real runs of: the collision clipmap level answers true for ±16 m always and ±32 m sometimes, never ±56 m — its loaded window is a few cells around the viewer's cell — so C7 `areaBuilt` was never set; the near field plus `streamingSettled` is the criterion, and the `camBuiltHalf` ladder in `DE.S1 sec` keeps measuring what is built for O9) ∧ `streamingSettled` only after a world load, a teleport, a new Subnautica generation or a link recovery (`IsWorldSettled` is a global idle test that rarely holds while the camera moves) ∧ freshness `presentedSeTick ≥ tick − 4` (CONTRACT §5.4) ∧ `rectOk` ∧ `uiHidden` |
| `zOrderOk` | the corrected z-order walker (section 5.1) finds `snHwnd` directly under `seHwnd` |
| `dwell` | time since the last Approach ↔ Underwater switch |
| `paused` | `MySandboxGame.IsPaused` |
| `foreground` | `MyGameWindow.IsActive` |

### 4.2 States and transitions

The table below is the picture policy `Waterline` (S2). Since S7a the default is `Site` ("Picture policy (S7a)" after
the transition table): same states and transitions, Subnautica's picture in every state but Space.

| State | Picture | SE scene | SE window alpha (DWM margins) | Subnautica mode (CONTRACT §5.4 `perfMode`) | Audio (section 5.3) |
|---|---|---|---|---|---|
| **Space** | SE | drawn | off (margins 0) | **Idle** (review F1: Full during the band-exit hold, until SE's scene-drawn frame is presented
| **Approach / Track** | SE | drawn | off | **Track**: time running, ghost transform at `F(P)`, streaming follows it, cameras off, `vSyncCount 0`, 10 fps, Normal | SE normal, Subnautica muted |
| **Approach / Prime** | SE | drawn, backbuffer alpha forced to 1 after the sprites | **on** (−1): Subnautica is composed underneath but invisible, so it is never an occluded window | **Full**: cameras on, full rate, shaders warm | SE normal, Subnautica muted |
| **Underwater** | Subnautica, SE HUD and menus on top | skipped (backbuffer cleared to (0,0,0,0)) | on | **Full** | Subnautica 1.0, SE game 0.7 × config, SE music 0, Water Mod 0 |
| **Surfacing** | transitional, 1–2 SE render frames | drawn again (alpha 1) | stays on | Full | crossfade back (300 ms) |

Subnautica reports the recipe it applied in C7 `perfModeApplied` and its phase in `snPhase`: **ReadyIdle** while Idle
(Space; also the launcher's ready phase, and Subnautica's own reaction to a stalled SE), **WarmingUp** while Track and
while Full is requested but no frame has been rendered with it yet, **Active** while Full renders (Prime, Underwater,
Surfacing). The exact table, with `targetFps`, the audio targets and the overlays, is CONTRACT §5.4 "C5/C7 semantics
for S2". The S1 camera-drive rule (section 5.11: ghost Player rule, drive gate outside the dive zone) holds in every
state; the recipes only add the freeze, the cameras, the frame-rate pair and the mix.

| From → To | Condition (all must hold) |
|---|---|
| Space → Approach (Track) | `onSite` ∧ `ρ∞ ≤ 7,800 m` ∧ (`y < enter` ∨ `y ≤ enter + 8·(−vy)` with `vy < 0`: the camera will be below the band enter within 8 s; +300 / +350 m until 13:20; +1,000 / +1,100 m from 13:40
| Approach → Space | `¬onSite` ∨ `ρ∞ > 7,900 m` ∨ (`y > exit` ∧ not `y ≤ exit + 8·(−vy)` with `vy < 0`, **held 1 s** — review F15 a craft hovering at the band with vy swinging toggled the states — exit +450 m default) ∨ `peerState ≠ Alive` (margins off first if Prime) |
| Track → Prime | `y ≤ +1 m`, or `y ≤ +5 m` ∧ `vy ≤ −0.2 m/s`, or a predicted crossing (`y / −vy`, `vy < 0`) within 4 s. Lead time sized from S1.N3: kept at 2 s |
| Prime → Track | none of the Prime conditions held for 10 s while `y < +5 m`, or for 3 s at `y ≥ +5 m` (the timeout is read at the current `y`), and `y > +1 m`; margins go off after the next presented frame |
| Prime → Underwater | `y ≤ −0.20 m` ∧ `ρ∞ ≤ 7,800 m` ∧ `snReady` ∧ `zOrderOk` ∧ `dwell ≥ 0.25 s` ∧ `presentedSeTick ≥ crossingTick`. Entry: margins are already on (since Prime, at least one presented frame); the SE render thread skips the scene from render frame N; the 300 ms audio crossfade starts |
| Underwater → Surfacing | `y > −0.05 m` (after `dwell ≥ 0.25 s`) ∨ `ρ∞ > 7,900 m` ∨ `¬onSite` ∨ `peerState ≠ Alive` ∨ `¬snReady` after a teleport. The last four apply immediately (no dwell) |
| Surfacing → Prime / Track / Space | scene drawn (alpha 1) on render frame M; then the Approach/Space rules decide; margins go off only when leaving Prime, after a frame with the scene drawn has been presented (S7a `Site` policy with the picture latch on: the unchanged request acknowledged, see below) |

*CONTRACT "Picture on screen", §5.4 "C5/C7 semantics for S7a").** The
states, their transitions and every threshold above stay; Underwater still gates the audio mix, the S5a oxygen and arms,
the swim model and the S6 proxies. What changes is which states show Subnautica's picture, selected by the machine input
`ViewInputs.Picture`: **`Site`** (default since S7a) shows Subnautica inside the site below the Approach band, above and
under water; **`Waterline`** is S2's policy (the table above), kept as the fallback (`de.s7 picture waterline`, env
`DEEPENGINEERS_PICTURE=waterline`) and as the replay policy of S2-era trace rows.

| State (policy `Site`) | Picture | SE scene skip | Margins (F) | A′ | Subnautica recipe (`perfMode`) | SN camera parking | Fade trigger | SN window `HideAllowed` |
|---|---|---|---|---|---|---|---|---|
| **Space** | SE (Water Mod surface from orbit, SE sky) | no | off (after a band exit with the latch on: until the scene-drawn request is presented, one request) | raised (after a band exit: once a drawn frame is presented) | **Idle** after the hold (F1: Full while the band-exit hold lasts; then frozen, cameras off, 10 fps, BelowNormal) | parked +60 m under the SE camera (streams the arrival column) | the latch's off edge (Subnautica → SE) | true unless margins or see-through are still on |
| **Approach / Track**, latch off | SE while Subnautica warms up or re-streams | no (alpha fill) | **on** from the Approach entry | raised | **Full** (cameras on, full rate) | true pose | — | false |
| **Approach / Track or Prime**, latch on | **Subnautica** (its surface, sky, weather; mirrored grids, astronaut, placement ghost) | **yes** | on | lowered (raised while a menu is open) | Full (30 fps while SE is unfocused) | true pose | the latch's on edge (SE → Subnautica) | false |
| **Underwater** (entered only with the latch on) | Subnautica | yes | on | lowered | Full | true pose | none (no swap at the waterline) | false |
| **Surfacing** | Subnautica while the latch holds (the state ends when its unchanged request is acknowledged: one tick); after an immediate exit, S2's Surfacing | = latch | on | = latch | Full | true pose | none (the off edge, if any, belongs to the exit) | false |

Design A′ follows the same latch with its `Lowered` semantics (SE lowered below Subnautica and its scene skipped while
the latch is on and no menu is open; raised after drawn frames when the latch drops). The drive gate of section 5.11
(D99: Subnautica's camera held outside the dive zone, depth hold below −7,900 m) is unchanged; parking is decided from
C5 `perfMode` (`CameraParkRules.Park` = C5 valid ∧ `perfMode ≠ Full`), which gives S2's Space-and-Track parking under
`Waterline` and Space-only parking under `Site`. `WindowStyleRules.SeShowsSubnautica` (so `HideAllowed`) and the
`snPhase` rules need no change: they already treat every non-Space view as "Subnautica shown".

Machine rules under `Site` (pure, in `Wire.ViewStateMachine.Step`, in this order each tick; the twin
`tests/s2/state_traces.py`, row version 4, follows the same order):
1. Events, render acknowledgement, waterline latch and `crossingTick`, site/peer/`snReady` exactly as today.
2. **Latch off at once** (no dwell) when `¬siteOk` (bridge, site, zone FAR/off-site, non-finite `y`), `peer ≠ Alive`,
 or a pending re-settle (teleport > 150 m, new generation, link recovery, world load) with `¬snReady`; picture reason
 `PictureOffSite` / `PictureOffPeer` / `PictureOffResettle`.
3. View transitions as the table above, with two `Site` differences: the Prime → Underwater guard's render bit is
 "latch off" (it replaces `marginsReady`, which needs the scene drawn); Surfacing completes on
 `renderAcked ∧ (latch ∨ scene-drawn request)`. A Space → Approach transition sets `bandTick` = this tick.
4. **Latch:** in Space → off (`PictureOffSpace`); else, when off, it turns **on** (`PictureOn`) if the view was not Space
 on the previous tick either (its margins request was issued) ∧ `y < +300 m` (the band itself: an Approach entered
 early by predicted arrival warms Subnautica up for 8 s but swaps at the same altitude at every speed) ∧ `siteOk` ∧
 `ρ∞ ≤ 7,800 m` ∧ `snReady` ∧ `zOrderOk` ∧
 picture dwell (0.25 s since the last latch change, `PictureDwellUs`) ∧ `presentedSeTick ≥ bandTick` ∧ (F: the
 margins-on request with the scene drawn acknowledged and the margins applied now, `MarginsReady`; A′: nothing more) ∧
 design F or A′. Failing guards are reported in `PictureBlocked` (the `ViewBlock` bits plus `Band` = bit 16,
 `NotApproach` = bit 17 and `High` = bit 18 for `y ≥ +300 m`). Once on, the latch holds up to the Approach exit
 (+350 m, the band's hysteresis).
5. Request: F `margins = view ≠ Space ∨ hold`, `skip = latch`, `alpha = ¬skip ∧ (margins ∨ marginsApplied)`, where
 `hold` = the previous request had margins ∧ (it skipped the scene ∨ it is not acknowledged yet): a Space entry with
 the latch on (the +350 m exit, a site or peer loss in Approach) draws SE's scene first and drops the margins one
 request later, after that frame is presented (the S2 crossing order; `ViewSim.OrderViolations` and the twin's order
 check stay green); A′ `lowered = latch ∧ ¬menu`, `skip = lowered`, and when the latch drops (no menu) SE stays
 lowered without skip until that drawn request is acknowledged, then is raised (a menu raises at once, as S2). Perf:
 `PerfFor` = Idle in Space, Full elsewhere; `targetFps` 30 when the picture is shown and SE is unfocused. The z-order
 re-assert pulse fires when margins become wanted (`MarginsWanted` = view ≠ Space), on re-activation and on a lost
 z-order. `PictureEdge` = +1 / −1 on a latch change (under `Waterline`: the Underwater entry / exit), the trigger of
 the crossing fade. C5 flag bit 9 `pictureSite` = effective policy `Site`.
Seeded failure S7a.M1 (`ViewMutants.S7aM1Waterline`): the machine runs `Waterline` while `Site` is selected.

*Implementation notes.* Outputs: `Picture` = the latch (`Waterline`: view = Underwater; the
machine keeps `ViewMachineState.Picture` = view = Underwater under `Waterline`, so a run-time policy switch and the edge
stay consistent); `PictureBlocked` = the failing on-edge guards while the picture is off, 0 while it is on (`Waterline`:
`Blocked`, the Underwater guard); `PictureReason` only under `Site` (None for the `Waterline` edges, whose cause is
`Reason`); `PicturePolicy` = `EffectivePicture(picture, mutants)` (any input value but `Waterline` runs `Site`). The
picture dwell is not reset by a world load: the load drops the latch (re-settle pending), so the latch returns at the
earliest 0.25 s later (15 ticks) and Underwater one tick after it. Trace rows are `vsm` version 4 (`ViewTrace`; input
`pp`, state `s_sp s_bt s_ps s_pt`, outputs `o_sp o_pe o_pb o_prs o_pp`); the twin `tests/s2/state_traces.py` replays rows
of version 3 or lower as `Waterline` and runs every S2 scenario under both policies plus `band`, `band-fast`,
`band-hover`, `site-dive`, `site-peer`, `site-teleport`, `site-aprime` (`--selftest`). SE: `PicturePolicy` (env
`DEEPENGINEERS_PICTURE`, `de.s7 {picture}`), `ViewStateDriver` (input, S7a.M1, `s7a.view` rows), `DisplayController`
(fade on `PictureEdge` with kind band/waterline, the see-through toggle cleared on the latch's off edge under `Site`,
auto re-assert while margins are requested with the scene drawn), `CrossingFlash` (band sky tints, `s7a.flash` rows,
S7a.M3), `S2Host` (C5 bit 9), `PictureHooks` (the 1,000/1,050 m entity radii above water). Subnautica:
`CameraParkRules.Park(c5Valid, perfMode)` / `ParkByView` (S7a.M2) / `Decide`, `PerfRecipeRules.ExpectedPerfMode(view,
approach, pictureSite)`, the unfocused (0, 30) rule on C5 seeThrough, `MirrorCullRules` (1,050 m above water),
`S1Telemetry.AreaBoxSite` (box at `min(y, +140 m)`, `areaBuiltRaw` on the `DE.S1 sec` line), `s7a.picture` rows from
`S2Host`, and `de.s2c5 {picture}` (default site: Full in Track, margins/see-through/bit 9 as the latch's steady
state). Two behaviours to know (open, not changed by S7a): (1) a margins loss after the latch is on (a failed DWM
call, `de.display margins=off`) does not drop the latch (only site, peer and re-streaming losses do); the render thread
then draws SE's scene by itself (`RenderDisplay.SkipAllowed` needs the margins), so there is no black frame, but the
machine can enter Underwater (audio) with SE's picture on screen; before the latch the same loss holds the latch and
Underwater (the `Site` restatement of S2 finding 11); (2) switching `Site` → `Waterline` (`de.s7 picture waterline`)
while the latch is on in Approach drops margins and skip in the same request (the `Waterline` request rule has no
`hold`, unchanged from S2), covered only by the render thread's scene-drawn presents before the margins go off; switch
in Space.

**State thresholds (the one table; every other document points here).** `y` is the SN altitude, **negative
underwater**; `vy` its rate (m/s, negative when descending); `ρ∞ = max(|x|, |z|)` of the camera in SN metres. All
values are chosen design values unless the last column says otherwise.

| Threshold | Value | Used by | Evidence / note |
|---|---|---|---|
| Approach altitude band | enter `y < +300 m` (or predicted within 8 s), leave `y > +350 m` (unless predicted within 8 s); a live setting since default +1,000 / +1,100 m since S7t (the transition row above; the tests pin +300 / +350 m) | Space ↔ Approach; from S7a (`Site`) also the only picture swap | chosen; the S1 drive gate uses the same band (D99). Since Subnautica's camera is parked at +60 m under the SE camera while the view is Space or Track (CameraParkRules): the arrival column streams before the dive; from S7a the parking follows C5 `perfMode ≠ Full` (Space only under `Site`) |
| Picture dwell (S7a) | 0.25 s between two picture-latch changes (`PictureDwellUs`) | the latch's on edge | chosen (as the waterline dwell); the off edges are immediate |
| Picture freshness (S7a) | `presentedSeTick ≥ bandTick` (the SE tick Approach was entered) on top of `snReady` | the latch's on edge | Subnautica must have presented a frame rendered at the unparked pose |
| Above-water mirror radius (S7a) | enter 1,000 m, leave 1,050 m while the camera is above the sea under `Site` (S5: 250 / 300 m) | SE entity set, Subnautica chunk culling | chosen: a ship on the sea 300 m below the band is in view; cost re-measured by S7a.T-P1r |
| Impostor reach | above the picture camera beyond the mirror radius: horizontal clearance 1,500 m in / 1,600 m out, from the camera up to 5,000 m in / 5,500 m out (100 m below it out); large root grids only, <= 30,000 blocks, <= 16, 2 per query every 30 ticks | SE S5 producer (GridAdd bit 6, coarse export), Subnautica coarse build and far plane (CONTRACT §5.4 "S7t impostors", "S7t altitude") | chosen: a ship parked at +2 km is a shape in Subnautica's view from the 1 km band and the surface |
| Near-field box above the world (S7a) | under `Site` the ±16 m camera box is centred at `min(y, +140 m)` under the camera | `areaBuilt` (`snReady`) for the picture latch at altitude | chosen: the octree rows end at +160 m (Mountain Island +155 m); what `IsRangeActiveAndBuilt` answers for empty air above them is HYPOTHESIS (S7a.N1 logs both boxes) |
| Prime entry | `y ≤ +1 m`; or `y ≤ +5 m` ∧ `vy ≤ −0.2 m/s`; or predicted crossing `y / −vy` (with `vy < 0`) ≤ 4 s | Track → Prime | lead time checked against S1.N3 (below) |
| Prime exit | none of the Prime conditions for 10 s while `y < +5 m` (strict), for 3 s at `y ≥ +5 m`; ∧ `y > +1 m` | Prime → Track | margins off after the next presented frame.5–1 s; the 13/10 SE log shows a Prime → Track drop before each slow dive and 533 ms from the crossing to the Underwater entry (quick re-dives without the drop: 33 ms). Cost: Subnautica at full rate up to 10 s longer near the surface. Real-run check: S2-STATUS §8.4 part `n7b` |
| Waterline entry | `y ≤ −0.20 m` | Prime → Underwater; C5 `crossingTick` | O15 open: S1.N10's sampler failed, so Subnautica's surface displacement is not yet measured |
| Waterline exit | `y > −0.05 m` (0.15 m band) | Underwater → Surfacing | after the dwell |
| Minimum dwell | 0.25 s since the last Approach ↔ Underwater switch | both waterline transitions | the immediate exits (zone, site, peer, teleport) ignore it |
| Zone CORE | `ρ∞ ≤ 1,950 m` (the terrain footprint) | C5 `zone` | S1 `ZoneTracker` (D65) |
| Zone TILE VOID | `1,950 m < ρ∞ ≤ 2,048 m` (rest of the 4,096 m tile) | C5 `zone` | |
| Zone OUTER VOID | `2,048 m < ρ∞`, entered at `≤ 7,800 m`, left at `> 7,900 m` | C5 `zone`; `GetBiome` patched to "void" | |
| Zone FAR | `ρ∞ > 7,900 m` (back at `≤ 7,800 m`), or another planet | Approach → Space; Underwater → Surfacing at once | keeps the ghost inside `OutOfBoundsWarp`'s ±8,192 m cube (VERIFIED) |
| Underwater entry, zone | `ρ∞ ≤ 7,800 m` | Prime → Underwater | |
| Freshness | `presentedSeTick ≥ tick − 4` | `snReady` | CONTRACT §5.4 |
| Crossing | `presentedSeTick ≥ crossingTick` | Prime → Underwater | target ≤ 60 ms after the crossing (S2.T-D1) |
| Near-field boxes | ±16 m around the camera = `areaBuilt` (`snReady`);5–7 s) | `areaBuilt` (`snReady`) | VERIFIED real run S1.N3: ±56 m never reported built |
| Crossfade | 300 ms linear, unscaled clock | audio (section 5.3) | |
| Exit polling | every tick while the margins are on, once per second otherwise | PeerExited | |
| Subnautica-side depth hold | `y < −7,900 m` holds the camera, `y ≥ −7,800 m` drives again | Subnautica's drive gate only, not a state-machine input | S1 D99 (our reading of `OutOfBoundsWarp`); SE's floor is at −3,000 m, so only a spectator gets there |

Waves and tides are 0 on this planet, so the SE surface is the sphere `r = Rs`. Subnautica draws a displaced water
surface (`WaterSurface` displacement textures); its amplitude at the camera is S1.N10's measurement. The S1 real runs
confirmed `Ocean.GetOceanLevel = 0`, but the displacement sampler read ±20 m (its texel-mapping HYPOTHESIS does not
hold: a tooling bug), so O15 stays open: the entry threshold is moved below the measured trough only if the fixed
sampler finds more than 0.20 m (HYPOTHESIS until then).

**Prime lead time (S1.** After a camera jump the ±16 m near field
was built in 0.38–0.98 s (camera box; forward box 0.45–0.91 s) and `IsWorldSettled` held on the busy → idle edge in
≈ 1.24 s for jumps of 0.5 and 1 km and ≈ 3.07 s at 2 km. In a continuous descent Track already streams at the mapped
pose, so Prime only has to switch the cameras on and warm the shaders: the 2 s predicted-crossing lead (10 m above the
water at the 5 m/s dive of S2.N7) is longer than the near-field build, and it is kept. After a teleport or jump the
dive is gated by `streamingSettled` instead: expect SE's own view for ≈ 1.3 s (≤ 1 km) to ≈ 3.1 s (2 km). S2.N7
checks both.

Zones (SN coordinates of the camera, for orientation):

| Zone | Extent | Underwater picture |
|---|---|---|
| CORE | inside the terrain footprint, `ρ∞ ≤ 1,950 m` (S1 implementation, D65) | Subnautica: terrain, biomes, fauna |
| TILE VOID | rest of the 4,096 m tile | Subnautica: native void biome, ghost leviathans as in vanilla |
| OUTER VOID | outside the tile, `ρ∞ ≤ 7,800 m` | Subnautica: empty water, `LargeWorld.GetBiome` patched to "void" for the fog; no eco-regions, so creatures find no targets. **S8a (`WindowOn`):** the sculpted seabed converted from SE's planet, the biome of SE's `cameraBiome`, the spawn table (§5.17) |
| FAR | `ρ∞ > 7,800 m` (7,900 m to leave), or any other planet | SE's own Water Mod underwater view (state Space) |

The 7,800/7,900 m limits keep the ghost player well inside `OutOfBoundsWarp`'s ±8,192 m cube (VERIFIED,
`OutOfBoundsWarp.FixedUpdate`).

### 4.3 Overlays (orthogonal to the states)

| Overlay | Trigger | Effect | Ends |
|---|---|---|---|
| **Paused** | `MySandboxGame.IsPaused` (ESC menu, Steam overlay, medical screen) | no state change;3); time of day not advanced; underwater the frozen Subnautica picture stays under SE's menu; SE writes no audio volume while paused or while a GUI screen other than gameplay is open | `IsPaused` false → freeze recomputed (still true in Space) |
| **Unfocused** | `¬foreground` (Alt+Tab, Start menu) | no state change; SE mutes itself (`MySandboxGame. Subnautica mutes; Subnautica (0, 30) fps if Underwater (from S7a: whenever C5 `seeThrough` is set, i.e. Subnautica's picture shows; the same set under `Waterline`) | SE foreground again → SE's own restore runs, then the plugin re-applies its mix in a postfix; z-order re-asserted (section 5.1) |
| **PeerStalled** | Subnautica heartbeat ≥ 3 s old (120 s while either side reports a loading phase), process alive | within one SE frame in Underwater/Surfacing: scene drawn, margins off, audio restored; state machine runs with `snReady = false`; the HUD notice "underwater renderer offline" shows at once underwater, after 10 s elsewhere (no picture depends on Subnautica in Space or Approach under `Waterline`; under S7a's `Site` the picture latch drops within the same SE frame in Approach too, and the notice shows at once whenever the latch was on) | heartbeat fresh again with the same PID: SE bumps `linkEpoch`; Subnautica answers `linkAckEpoch`; SE resends everything if the stall lasted ≥ 10 s or the counters disagree, else continues; `snReady` (with `streamingSettled`) gates the next dive. S8 (`S8Rules`): the fallback, the notice rule and its texts are code; the notice is shown once per loss and only in a DE world |
| **PeerExited** | process exit, `snPhase ∈ {Quitting, None}`, a changed `snPid` or `snGeneration`, or `snHwnd` no longer a visible window | as PeerStalled, immediately; SE detects exits every tick while margins are on, once per second otherwise | a new Subnautica generation reaches `ReadyIdle` (or a later phase) and acknowledges the hello; `Active` comes only with the next Prime (CONTRACT §5.4) |

### 4.4 Rules that keep the switch invisible
- Never show a half-streamed Subnautica: after a load, teleport, new generation or link recovery, SE stays on its
 Water Mod view until `snReady`. (S7a `Site`: the same rule drops the picture latch, above water too.)
- S7a `Site`: the swap moves from the waterline to the Approach band, where nothing swims and the view is wide. Entering:
 margins on at the Approach entry while SE still draws its opaque scene (alpha fill), Subnautica switched to Full and
 unparked; the scene is skipped once the latch guard holds and the time-of-day sync
 (section 5.12) puts Subnautica's sun where SE's stands. Leaving: the latch drops, the scene is drawn first, margins go
 off after a presented scene-drawn frame (the render acknowledgement contract of `Wire.ViewStateMachine`, unchanged).
- S7t: Subnautica's picture above the sea reaches the horizon. Above +2 m its camera's far
 plane (which also sizes the water's square, ±far around the camera) follows the horizon distance of SE's sea, a
 camera-centred skirt hazes the far sea into the horizon colour, and grids far above the camera arrive as coarse
 impostors (GridAdd bit 6) drawn out to ~5 km up; under 2 m and under water Subnautica's own look is untouched
 (CONTRACT §5.4 "S7t altitude").
- S7t T5 look match: where the pictures meet they look alike. SE's look moves from its own in Space
 to a match set at the band and below (softer shadows: less sun, more ambient; lower contrast and saturation; a
 blue-white distance fog; dimmer daylight stars), blended over 1,500 m above the band's enter altitude, by changing
 only the copies SE sends its renderer each frame; Subnautica keeps its palette and moves only its sun's shadow
 strength and ambient a little toward SE's above +20 m. Both restore their own values whenever the picture is off,
 under water and on peer loss (0.8 s ease), nothing persisted; `de.s7t look` tunes both live (CONTRACT §3.4, §5.4).
- Entering: margins on during Prime while SE still draws its scene (its alpha is 1: tonemapping, FXAA and the
 copy to the backbuffer write alpha 1, VERIFIED shaders and `MyRender11.DrawGameScene`; HUD sprites are blended
 after that, so the plugin adds an alpha-only full-screen fill after the sprites; HYPOTHESIS until the S2.T-D1
 readback), then skip the scene. Leaving: draw the scene first, then (only when leaving Prime) margins off. No frame
 ever presents a cleared backbuffer without margins (the T-D0 control showed such a frame as black).
- Two latency budgets, both measured in S2.T-D1: crossing → Subnautica ready (`presentedSeTick ≥ crossingTick`,
 target ≤ 60 ms; Subnautica runs at full rate from Prime) and skip frame → composed (≤ 2 refreshes, ≤ 12 ms at
 165 Hz). The pose transport under them was measured in S1 (real run, S1.N1/N2): C1 capture → Subnautica `onPreCull`
 p95 ≈ 16 ms (0.05–0.08 % extrapolated, 0 torn), yaw-step response p95 7.65 ms.
- **First S2 gate (S2.N8).** The only real-run data on a fully covered Subnautica shows it nearly stops rendering
 (0–7 % CPU, 0–5 % GPU, one 5 s `Responding = False` blip; Prime therefore keeps margins on, so
 Subnautica is composed under SE rather than occluded. Whether DWM still treats it as occluded when every SE pixel
 has alpha 1 is HYPOTHESIS; S2.N8 measures it before anything else in S2. Recorded alternatives: (a) keep a 1×1 px
 alpha-0 region in a corner during Prime; (b) stamp freshness in the main camera's `onPostRender` instead of at end
 of frame; (c) enter on `areaBuilt` alone with a short fade (`areaBuilt` is the ±16 m near field since S1; the
 `camBuiltHalf` ladder logged by the S1 telemetry shows how much more is built). A stall in Space, Track or Prime still trips
 PeerStalled after 3 s (D5), but there it has no visible effect and recovers by itself (section 4.3).

---
## 5. Subsystems

Each subsystem: decision, mechanism, evidence, fallback.

### 5.1 Display (D1)
- **Decision: design F, "see-through SE".** SE keeps OS focus all session. Subnautica's window sits directly under
 SE's, same rectangle (2560×1440 at 0,0 on the primary monitor), borderless, never foreground. Underwater, SE skips
 its 3D scene, its backbuffer is cleared to transparent black and DWM per-pixel alpha is on, so Subnautica's live
 picture shows through and SE's HUD sprites and menus are drawn on top. Borderless only (the AMD driver hangs on
 exclusive fullscreen on this PC). Caveat to D1's order: the DWM margins go on already in Approach/Prime, while SE
 still draws its opaque scene (section 4.4), so Subnautica is never a fully occluded window when it must render.
- **Mechanism (SE).**
 - Scene skip: a prefix on `MyRender11.FullDraw` clears the private `m_drawScene` while the skip flag is set; SE's
 own else-branch then runs `RC.ClearRtv(Backbuffer, default)` (transparent black), and `ConsumeMainSprites` draws
 the HUD and GUI on top (VERIFIED `$`: `FullDraw` calls `DrawScene`
 only if `m_drawScene && SceneSystemsReady`). Alternative: a prefix on the public static `DrawScene` with
 `AccessTools` delegates for the internal `Backbuffer` and `RC.ClearRtv`. Side effect: `PrepareGameScene →
 MyOffscreenRenderer.Render` is skipped as well, so LCD and offscreen textures are stale while see-through (LCD
 text is out of v1; they refresh on surfacing).
 - Crossfade: under `Site` the band swap is a ramp, not a cut.
 A prefix on `MyRender11.ConsumeMainSprites` (VERIFIED: `FullDraw` calls it after `DrawScene` or the clear and
 before the HUD/GUI sprites execute) draws one full-screen pass that scales SE's drawn frame to (colour × alpha^2.2,
 alpha) on the sRGB backbuffer, i.e. premultiplied for DWM, with alpha = 1 − smoothstep(p) over 700 ms; the HUD and
 GUI draw over it; the scene skip engages at p = 1 and disengages when the ramp back starts (the leave dwell); the
 margins stay on while p > 0; a picture loss without the dwell snaps. A′ (switch, off by default): the same ramp as
 SE's window constant alpha around the lowering (HYPOTHESIS: a layered window over SE's swap chain).
 - Alpha fill (Prime and Surfacing): an alpha-only full-screen pass (colour write mask = alpha, value 1) after the
 sprites and before `Present`, so translucent HUD sprites over the opaque scene do not leave alpha < 1
 (HYPOTHESIS mechanism through SE's internal device context; S2.T-D1 reads the alpha back).
 - DWM margins: `DwmExtendFrameIntoClientArea(seHwnd, −1)` on Prime entry, 0 after leaving Prime, applied by the
 render thread after the frame that needs them has been presented (postfix on `MyRender11.Present`: HYPOTHESIS for
 the exact hook).
 - **Window ownership.** SE's window belongs to SE's render thread (`MySandboxGame.InitializeRenderThread` creates
 it there; `MyRenderThread.RenderThreadStart` loops on the window's `IVRageWindow.UpdateRenderThread`, which
 `MyGameWindow` implements with a SharpDX `RenderLoop` that pumps it;
 VERIFIED). Every call on `seHwnd` (`HWND_NOTOPMOST`, A′ lower/raise, margins, styles, `SetForegroundWindow`) is
 queued by the update thread and executed by the render thread in the `Present` hook, so no synchronous
 cross-thread message blocks the simulation. Target: SE update time per toggle < 1 ms (S2.T-D1).
 - TopMost, only while linked: `HWND_NOTOPMOST` plus a postfix on `MyGameWindow.OnModeChanged` that undoes `TopMost
 VERIFIED). Restored on unlink or
 PeerExited. Handle from `MyWindowsWindows.WindowHandle`.
 - z-order re-assert, before Prime and after each SE re-activation: SE, being the foreground process, first calls
 `AllowSetForegroundWindow(snPid)` (raising another process's window needs that process to hold foreground
 permission, SetWindowPos docs; raising a background window took effect in only 1 of 3 synthetic runs,
 `probe-display-input.md` §3.3), then `SetWindowPos(snHwnd, seHwnd, …, NOMOVE|NOSIZE|NOACTIVATE|SWP_ASYNCWINDOWPOS)`
 from the update thread (posted to Subnautica's thread, never blocks). The next frames verify it with the walker
 below; up to 3 retries; the result is `zOrderOk`, part of the Prime → Underwater guard. Cross-process window calls
 in the whole design: this one only; every other call acts on the caller's own window.
 - Z-order walker (the S2.T-D1 oracle and `zOrderOk`): from `seHwnd` walk `GW_HWNDNEXT`, skipping windows that are
 invisible, DWM-cloaked (`DWMWA_CLOAKED`), zero-area, or owned by `snHwnd` or `seHwnd` (owned windows such as a
 the first remaining window must be `snHwnd`. The skipped list
 is logged.
 - SE's swap chain is DXGI 1.0 `SwapEffect.Discard` (bit-blt model), `R8G8B8A8_UNorm_SRgb`; DWM treats its alpha as
 premultiplied (VERIFIED code; VERIFIED synthetic T-D0 on this PC, `tools/probe-dwm-standins`). Translucent
 sprites blend in linear space and are stored sRGB-encoded, so a 50 % white panel stores 0.735 where DWM expects
 0.5: translucent HUD panels and glyph edges come out too bright over Subnautica (arithmetic; HYPOTHESIS until the S2.T-D0 translucent case). S2.T-D0
 gains a translucent case
 stored colour to `a·sRGB(c)` (R2.5).
 - Never use `MyRenderThread.Suspend` (it suspends SE's worker threads), never minimise SE, never change SE's
 resolution per dive.
- **Mechanism (Subnautica).**
 - At bridge start (the plugin's first player-loop `Update`, section 5.10), in bridge mode only, after the bridge token
 and the mutex pass (CONTRACT §1.2): `DisableProcessWindowsGhosting` (a main-thread stall ≥ 5 s would otherwise
 make Windows replace the window with a ghost window that has a taskbar entry and can be activated;
 `IsHungAppWindow` docs). A dormant Subnautica stays vanilla.
 - At `ReadyIdle`, before SE starts: `ShowWindow(SW_HIDE)` → `SetWindowLongPtr(GWL_EXSTYLE, … | WS_EX_NOACTIVATE |
 WS_EX_TOOLWINDOW)` → `SetWindowPos(NOMOVE|NOSIZE|NOZORDER|NOACTIVATE|FRAMECHANGED)` → `ShowWindow(SW_SHOWNOACTIVATE)`
 (the shell evaluates taskbar styles only when a window is shown; Microsoft "Managing Taskbar Buttons"); fallback
 `ITaskbarList:DeleteTab`. Then `SetWindowPos(HWND_BOTTOM, NOACTIVATE)` and `AllowSetForegroundWindow(ASFW_ANY)`
 while it is still the foreground process. The styles are re-applied after every `Screen.SetResolution`,
 fullscreen-mode change or `DisplayManager.OnDisplayChanged` (Unity rewrites window styles there) and verified
 once per second. T-D0 is no evidence for this path: its stand-in had the styles at creation.
 - Monitor and rectangle: the launcher passes `-monitor 1` (Unity 2019.4 player argument); the plugin verifies the
 display index and that its window rectangle equals SE's (C5 `seRect` and C7 `snRect`, both `GetWindowRect` in
 {left, top, right, bottom} order, CONTRACT §5.4), and compares them every second; a
 mismatch clears `rectOk` (no Underwater). Correction: `Screen.SetResolution(2560, 1440, FullScreenWindow)` (it
 cannot change monitor in 2019.4; `UnitySelectMonitor` is a PlayerPref, `DisplayManager`).
 Subnautica's Display option to the primary monitor once (the PC has a second 1920×1080 monitor). Launch flags are
 critic F7).
- **Foreground at start (HYPOTHESIS, S2.N10).** Subnautica gives up the foreground at `ReadyIdle` as above. The
 launcher starts `Legacy.exe` as its own child and calls `AllowSetForegroundWindow(sePid)`. When its link first
 comes up (SE opens the section once per second from `Init`, so normally at its main menu; CONTRACT §1.1 and §7
 "Start": SE's window changes happen while linked, which also keeps a plain SE session without Subnautica untouched)
 the SE plugin queues `SetForegroundWindow(seHwnd)` on the render thread, checks `GetForegroundWindow == seHwnd`,
 retries on its first activations and shows a HUD hint if focus is still missing. `ForegroundLockTimeout = 0` on this

 (200000). S1 real runs: SE took the focus by itself in every session (with `ForegroundLockTimeout = 0`), and S1.M8
 showed what that focus loss does to a Subnautica whose "run in background" is not forced (`FreezeTime`
 `ApplicationFocus`, `timeScale` 0).
- **Evidence.** `probe-display-input.md` §2.1, §3.2–3.5 (code), T-D0 synthetic PASS (opaque third green, transparent
 third shows the window below, flip-model window below composes live, hit-testing stays on the top window).
- **Mode selection at start (measured).** Config `Display = F | APrime | C`, default F. At session start, with
 Subnautica showing its calibration colour, SE turns see-through on for one frame and reads one composed pixel inside
 its rectangle (GDI); it also reads the per-app `UserGpuPreferences` value for the exact `Legacy.exe` path (this PC
 already has `SwapEffectUpgradeEnable=1;AutoHDREnable=1` globally). Not honoured → A′. That "optimizations for
 windowed games" (build ≥ 22557) can flip-upgrade SE's swap chain is REPORTED; that a flip-model top window loses
 DWM transparency is VERIFIED synthetic. Windows 11 21H2 is out of servicing and an automatic feature update is
 likely during v1 (R2.
 build, and after an upgrade T-D0's flip-top case is re-run with and without the per-app opt-out first.
- **Fallback A′ (z-order swap, SE still focused).** Underwater, SE lowers itself below Subnautica
 (`SetWindowPos(seHwnd, snHwnd, …, NOACTIVATE)`, executed on SE's render thread) and stops drawing (getter
 `MyGameWindow.DrawEnabled` patched to false); surfacing raises it (`HWND_TOP`; SE is the foreground process, so this
 is legal; VERIFIED synthetic). Menu rule: while `IMyGui.IsCursorVisible ∨ ChatEntryVisible ∨ ActiveGamePlayScreen ≠
 null ∨ MySandboxGame.IsPaused`, SE raises itself and draws (its own Water Mod view behind the menu). The SE HUD must
 then be rebuilt in Subnautica's uGUI (later slice); the SN plugin answers `WM_SETCURSOR` with no cursor while
 gameplay hides it.
- **Contingency C (shared D3D11 texture).** Only if DWM alpha is impossible: Subnautica renders into a
 `RenderTexture` opened by SE through `MyRender11.DeviceInstance` (`OpenSharedResource`), copied into SE's
 backbuffer in place of the scene, SE's sprites on top. Hook points VERIFIED, Mono-side interop untested, +1 frame of
 latency. Spike only if T-D1 and A′ both fail.

### 5.2 Input
- **Decision.** Nothing is forwarded. SE is the foreground window all session, so its own input path works
 unchanged: keyboard through `GetAsyncKeyState`, mouse through DirectInput `NonExclusive|Foreground`, joystick,
 text and IME (VERIFIED, `MyVRageInput.Update`, `MyDirectInput`). Subnautica never receives input.
- **Mechanism (Subnautica).** `WS_EX_NOACTIVATE` window, never foreground; Input System devices are reset and
 disabled in the background by default (REPORTED default; HYPOTHESIS that `GameInputSystem` keeps it); the plugin
 also calls `GameInput.ClearInput` every frame and keeps a dummy entry pushed on `InputHandlerStack.main`
 (VERIFIED APIs). Subnautica's Alt+Enter cannot fire because it never has focus.
- **Run in background.** Subnautica keeps simulating unfocused only while its option "Run in background"
 (`MiscSettings.runInBackground`, persisted as `Misc/RunInBackground`, default true) is on; otherwise
 `ApplicationFocus.UpdateState` freezes time, pauses all FMOD events and suspends the mixer on focus loss (VERIFIED).
 Before giving up the foreground the plugin sets it true in memory if it is false and calls
 `ApplicationFocus.OnRunInBackgroundChanged` (options never saved), logs the original value, and asserts every
 second that `FreezeTime` holds no `ApplicationFocus` entry. VERIFIED real run: forced at world
 set-up with the option switched off by a test hook, 0 of 236 `DE.S1 sec` lines showed a focus freeze; without the
 forcing (S1.M8) SE's focus took `FreezeTime` `ApplicationFocus` and `timeScale` 0 at once. Unity keeps calling
 `Update` either way, so the signal is the freeze entry, not `snFrame` (S1 D76).
- **Console.** Subnautica's console cannot receive keystrokes in a bridged session.
 commands (`/de …`); those meant for Subnautica are relayed over C11 and run there with
 `DevConsole.SendConsoleCommand`. Direct console use exists only in dormant (unbridged) Subnautica sessions.
- **Cursor.** SE's own, SE is on top. No cursor work in F.
- **Evidence.** `probe-display-input.md` §4; `SendInput`/`PostMessage` would not work anyway because SE ignores key
 and mouse window messages for gameplay (VERIFIED `MyGameForm`).
- **Fallback.** In A′ nothing changes for input (SE keeps focus). Only the abandoned option B would need
 `MyVRageInput.OverrideUpdate` + `UpdateStates`, kept as a documented tool, not planned.
- **Look latency.** SE samples the mouse at 60 Hz; Subnautica renders the newest camera orientation without
 interpolation and an interpolated position (D7). S2.T-I1 measures it (risk R2.9).

### 5.3 Audio (D9)
- **Decision.** Both engines play through the Windows shared mixer; SE decides the mix state (C5), each side applies
 it to itself, and no persisted setting of either game or of the Water Mod is ever changed (CONTRACT §1.1).

| State | Subnautica | SE game | SE HUD | SE music | Water Mod underwater sound |
|---|---|---|---|---|---|
| Space, Approach, any land or above-water case | muted (bus mute); frozen in Space | config | config | config | untouched |
| Underwater | 1.0 (biome music on) | 0.7 × config | config | 0 | 0 (emitters scaled, settings untouched) |
after ESC in Space and resume it stays frozen and silent | SE's own; the plugin writes nothing | | | |
| A GUI screen other than gameplay open, not paused (inventory, terminal, chat) | per state (not frozen: SE's simulation runs) | SE's own; the plugin writes nothing, so the last mix stays | | | |
| SE not foreground (Alt+Tab) | muted | 0 (SE's own `EnableMuteWhenNotInFocus`) | 0 | 0 | |
| PeerStalled, PeerExited | n/a | config | config | config | restored |

- **On the wire (C5, CONTRACT §5.4 "C5/C7 semantics for S2", the one audio table with the C5 values):** `snBusTarget`
 (Subnautica's `bus:/` target: 1.0 underwater and focused, else 0), `crossfadeMs` 300, `seMixActive` (the underwater
 SE mix is in effect), `guiScreenOpen` and `sePaused` (the plugin writes no SE volume), `seForeground`. The GUI-screen
 row was merged with the pause row before S2; CONTRACT separates them because SE does not pause for an inventory or
 terminal screen, so freezing Subnautica there would stop a live underwater picture.
- **Crossfades:** 300 ms linear, both directions, timed on the unscaled clock from C5; audio keeps the waterline
 hysteresis.
- **Mechanism (SE).** `MySandboxGame.UpdateSound` has its own focus logic: on focus loss it sets music, game, HUD and
 voice to 0 once; on refocus it restores Music = `Config.MusicVolume`, Game and HUD = `Config.GameVolume`; the
 loading screen calls `ChangeGlobalVolume(0, 0)` (VERIFIED). The plugin therefore writes
 `MyAudio.Static.VolumeGame/VolumeMusic/VolumeHud` in a postfix on `UpdateSound`, every tick and idempotently, but
 only while `MyGameWindow.IsActive` (or `EnableMuteWhenNotInFocus` is false), not paused, with no GUI screen other
 than gameplay open, and not in a loading phase. SE's own restore after Alt+Tab is thus overwritten on the same tick,
 and SE is never un-muted in the background. HUD target = `Config.GameVolume`, as SE restores it. Because
 `MyGuiScreenOptionsAudio.Save` persists `MyAudio.Static.VolumeGame/VolumeMusic` into `SpaceEngineers.cfg`, a prefix
 on it restores the config-derived volumes first (the copy of `MyAudio.Static` volumes into the config followed by
 `Config.Save` is VERIFIED in `MyGuiScreenOptionsAudio.Save`; the fix is checked by S8.N6).
- **Mechanism (Water Mod).** Never `/wvolume`: `CommandVolume` saves at once and prints a chat line. `Settings.Volume`
 belongs to `WaterSettingsComponent`, stored in `WaterClientSettings.xml` in SE's global storage (shared by every
 world) and written on every SE save and autosave (VERIFIED mod source: `WaterSettingsComponent.SaveData →
 WriteFileInGlobalStorage`; `WaterSoundComponent` uses `VolumeMultiplier × Settings.Volume`). Preferred: a postfix on
 the Water Mod's sound component that scales its emitters to 0 underwater and leaves the settings untouched
 (HYPOTHESIS: Harmony patches a mod's in-memory script assembly like any other). Fallback: in-memory `Settings.Volume
 json` before the first
 change, (ii) a prefix on `MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>)` (the overload normal

 that re-applies 0, (iii) a restore at the next plugin `Init` after an unclean end (Pulsar kills SE on exit,
 section 5.14).
- **Mechanism (Subnautica).
 `UWE.FreezeTime.Begin/End(FeedbackPanel)` is called only when that boolean changes. FreezeTime keys freezers by id
 (a second `Begin` with the same id is a no-op and `End` removes it), and its `FeedbackPanel` entry also writes
 `bus:/` volume and pause (VERIFIED `), so the mix uses `RuntimeManager.MuteAllEvents` (a bus mute
 FreezeTime does not touch) plus the crossfade volume re-applied after every FreezeTime transition (or a dedicated
 VCA, HYPOTHESIS that one fits). PDA voice, subtitles and notifications are suppressed (section 5.11). FMOD 2.01.15.
 S1.N6 (real run): with the Player's `IsUnderwater` left false or forced true, the 60 s holds at −150 m started
 `WaterAmbience` events and no breathing, PDA voice or depth-warning event in either
 variant, so the vanilla variant is kept and re-checked once the S2 mix mutes the Water Mod emitters (S2.T-A1).
** If a player sets `EnableMuteWhenNotInFocus = False`, SE stays audible on Alt+Tab
 (the plugin does not override SE's own setting); Subnautica still mutes.
- **Evidence.** `probe-display-input.md` §5; decomp `MySandboxGame.UpdateSound` / `UpdateAudioSettings`,
 `MyGuiScreenLoading.Update`, `MyGuiScreenOptionsAudio.Save`; Water Mod `WaterSettingsComponent.cs`,
 `WaterCommandComponent.cs`, `WaterSoundComponent.cs`. Oracles S2.T-A1 and S8.N9.
- **Audio polish.**
 - A1 *SE muffled under water.* A Harmony postfix on `MyEntity3DSoundEmitter.SelectEffect` (private, returns
 `MyStringHash`; looping sounds re-select every update, one-shots at play; VERIFIED) returns SE's own
 `LowPassNoHelmetNoOxy` while the plugin's underwater SE mix is in effect (`seMixActive`) and the camera is not in an
 airtight room, for sounds marked `ModifiableByHelmetFilters` (HYPOTHESIS fit; `de.
 try `LowPassHelmet`/`LowPassCockpit`). HUD and music are 2D and untouched.
 - A2 *Bus above water.* Under the S7a `Site` policy the table above still holds: Subnautica stays muted above water
 (the mix follows the Underwater state), so S7's event sounds play only for events under water, and above water SE
 plays its own air sounds.
 - A3 *Dry room under water.* The view state is Underwater by the camera's y, so Subnautica's bus is 1.0; through
 `IsInsideWalkable` its interior ambience plays (`FMODGameParams` interior events) and SE is not muffled (dry air).
 S7.N2 records the music and brightness changes
 - A4 *Crossfade reversal* (S2 D148): settled as built: SE restarts a full 300 ms ramp from the current level,
 Subnautica moves its bus at the constant rate 1/300 ms; uninterrupted fades agree.
 - A5 *Biome and depth ambience*: unchanged; the ghost at the camera feeds `FMODGameParams` (S1.N6's vanilla variant),
 recorded through a descent by S7.N11.
 - A6 *Event sounds*: every S7 effect sound is Subnautica's own FMOD event found at run time, 3D at the event, volume
 ∝ intensity, none beyond 250 m (§5.16).

### 5.4 World mapping and wire conventions (D4)
- **Decision.** One fixed map, "sea-level azimuthal equidistant + altitude": SN `y = |P − C| − Rs`; SN horizontal =
 `Rs·θ` along the azimuth from the anchor `U0`. Altitude and gravity direction are exact everywhere; the only
 distortion is a horizontal scale `s = Rs/r`: 1.000 at the sea surface, 1.0284 at the deepest floor (−1,657 m),
 1.0345 at the abyss limit (−2,000 m), 1.0526 at SE's floor (−3,000 m) and 0.9942 at the top of the Approach band
 (+350 m). All map math runs in the SE plugin in doubles; Subnautica never sees an SE coordinate or the site record.
- **What crosses.**
 - World-level data (entity poses, camera, velocities, world points and directions) crosses in Subnautica world
 space. A pose is `(o, q, s)`: `o` the SN position of the entity origin under the map linearised at the pivot
 `P*`; `q` a Unity quaternion `LookRotation(mapdir(Forward), mapdir(Up))` at `P*`, `w ≥ 0`; `s = Rs/|P* − C|`.
 - `P*` is the point of the entity group's boxes nearest to the SE camera (the camera itself when inside). It bounds
 the error seen from the camera to 0.24° over a 500 m hull.
 - Entity-local data (mesh vertices, normals, tangents, triangle indices, part, subpart and bone matrices,
 collision boxes, local hit points, static primitive axes) crosses in SE-local, SE-handed space in both
 directions; Subnautica applies the one mirror `(x, y, z) → (x, y, −z)` in `WireMath.Mirror`.
 - Triangles: Unity gets `(I0, I2, I1)` of `MyModel.Triangles`, which is the `.mwm` file order (VERIFIED by SE's ray
 code and by a census of 65,194,512 triangles in 17,397 models, 99.75 % consistent; `tools/mwm-winding`). A model
 with < 1 % agreement in Unity space is flipped automatically and logged.
- **Unity hierarchy.** Renderers live under a per-entity `Frame` (position `o`, identity rotation, scale `(s, 1, s)`)
 with an `Entity` child (rotation `q`, scale `(k, k, k)`). The camera, the ghost player and every proxy collider are
 never scaled. Colliders are never under the scaled Frame: each proxy section is a unit-scale root posed by the same
 affine, with box centres and sizes pre-scaled by `K = q⁻¹·S·q` (exact for a level hull with level/vertical box
 axes; worst case 0.11 m at −1,657 m and 45° tilt in the tool's table, ≤ 0.13 m with 4 m half-extent boxes, 0.15 m at
 −2,000 m; bound `(s − 1)·|h|`; VERIFIED arithmetic). Negative scales are never used.
- **Conformance.** The WM-1 vector file from `python -I tools/worldmap/wire_ref.py vectors <out.jsonl>` must be
 reproduced by the shared `WireMath.cs` within the header tolerances; subsets run at plugin load in both games (SE
 50 `pose` records; SN 50 `local` records as real GameObjects and 20 `box` records as real BoxColliders). The
 generator and the selftest take a site selector and use `SITE_V1`; pose and camera samples cover `y ∈ [−3,040,
 +350]`; rotated-box and `static` record kinds exist (done in S0, open item O1 closed; file values in CONTRACT §2.9).
 The subsets are compiled into `DeepEngineers.Wire.dll`; both passed in the real games on (S0.G1-E4a).
 Mutants that must fail are listed in CONTRACT §2.9.
- **Known limitation.** SE draws its crosshair with its unscaled camera while Subnautica draws the world scaled by
 `s` horizontally: a point on SE's view axis can be drawn up to about `(s − 1)/2 · sin 2α` rad off the crosshair:
 0.80° (14.4 px at 1440p, 70° vertical FOV) looking 45° up or down at the deepest floor, 0.97° (17.4 px) at −2,000 m,
 0.24° (4.3 px) at −500 m; 0 for level or vertical looks (VERIFIED, selftest with `SITE_V1`). Accepted for v1; a
 compensated Subnautica camera is the fix to test (S1.G1-E5).
- **Evidence.** `docs/recon/gap-1.md` (decision record; its numbers were computed for the superseded EXAMPLE site),
 `probe-world-mapping.md` §3; oracles `tools/worldmap/worldmap_ref.py` and `tools/worldmap/wire_ref.py`.
- **Fallback.** If Unity 2019.4 does not re-skew a rotating child under the `(s, 1, s)` Frame every frame (S3.G1-E2),
 use per-chunk rigid frames with `K` baked into the vertices.

### 5.5 Planet, site and world settings (D3)
- **Decision.** One dedicated planet `DE_4546B` from our own `PlanetGeneratorDefinition` in a local world mod, with
 one site: the 4,096 m Subnautica tile. Everything else on the planet is bottomless dead zone, which is
 lore-accurate (Subnautica has no seabed outside the crater).

| Parameter | Value | Why |
|---|---|---|
| Storage radius R | 60,000 m (spawn size 120 km) | menu-spawnable, same class as EarthLike |
| HillParams | Min −0.05, Max 0.12 | MinimumRadius 57,000 m, MaximumRadius 67,200 m: gravity exactly 1 g on [57,000, 67,200] m, which holds the whole play volume [58,000, 60,160] m (VERIFIED `MySphericalNaturalGravityComponent.GetGravityMultiplier`) |
| Heightmaps | six all-zero 16-bit faces (our own files) | flat floor at 57,000 m, 3,000 m below the sea, never drawn by Subnautica. **S8a:** sculpted by `tools/planet/sculpt_planet.py` (seabed 60–420 m deep with ridges, terraces and trenches; the crater kept at 57,000 m within ρ∞ 2,300 m; CONTRACT §3.3 "Sculpt"), drawn by Subnautica beyond the crater through the window (§5.17) |
| Sea | `WaterSettings.Radius = float32(60000/57000) = 1.0526316` → Rs = 60,000.00390625 m | sea at R: air density 1.0 and SE's "sea level" altitude = SN y to 4 mm |
| Waves, tides, currents, volumetric | 0, 0, 0, false | the SE surface is the sphere `r = Rs`, identical to SN `y = 0` |
| Water Mod `CrushDamage` | 0 on this planet only (per-planet setting) | the Water Mod defaults crush hulls at ≈ 400/600 m and characters at ≈ 293 m; other planets keep their defaults |
| Gravity | SurfaceGravity 1.0, falloff 7 | |
| Atmosphere | breathable, OxygenDensity 0.9, Density 1.0, LimitAltitude 2.0 | EarthLike numbers |
| Weather | no weather generators in v1 | Subnautica has no counterpart underwater |
| Anchor | U0 = (0, 0, 1), N0 = (0, −1, 0), E0 = (−1, 0, 0), ψ = 0, O = 0 | cube-face centre, axis-aligned tiles; SN (0, 0, 0) is on the sea surface |
| Abyss rule | below SN y −2,000 m the SE plugin crushes grids and characters on this planet; a floor guard at −2,500 m works in every world type (section 5.9) | protects the invisible −3,000 m floor; nothing of Subnautica exists below −1,920 m |

**World settings (set at world creation, world-wide;** They cannot come from a data-only world mod and
apply to every planet of the world:

| Setting | Value | Why |
|---|---|---|
| `EnableSunRotation`, `SunRotationIntervalMinutes` | true, 20 | one SE day = one Subnautica day (1,200 s); default 120; the day is six times faster on every planet of this world |
| Environment `SunAzimuth`, `SunElevation` | values that make `MySector.SunProperties.BaseSunDirectionNormalized` = (0, 0, 1) | the rotation axis is always recomputed from the environment's base direction (`MySunProperties.SunRotationAxis`) |
| Sector weather component `BaseSunDirection` | zero or (0, 0, 1) | the saved value overrides the environment's direction; if only one of the two is edited, the sun traces a cone |
| `EnableOxygen`, `EnableOxygenPressurization` | true | airtight rooms exist only with both (section 5.11) |
| Game mode, `DestructibleBlocks` | Survival, true for combat and the abyss damage | `MySlimBlock.DoDamage` does nothing without block destruction; `MyCharacter.DoDamage` does nothing in Creative (section 5.9) |

- **Mechanism (sea).** The world mod `DeepEngineers.World` (data only: the definition, the heightmaps,
 `Data\WaterConfig.xml` with a `PlanetConfig` for `DE_4546B`) is loaded by SE. The Water Mod applies a `PlanetConfig`
 only when a planet is spawned in a running session (`WaterModComponent.MyEntities_OnEntityAdd` while `_saveLoaded`);
 on every later load the sea comes from the world's saved Water Mod variable (`WaterModComponent.BeforeStart →
 AddWater` with the saved settings; `SaveData` writes it; VERIFIED mod source). So `WaterConfig.xml` seeds the sea
 once, when `DE_4546B` is first spawned with both mods loaded; afterwards the sea radius, crush damage, waves, tides
 and currents live in the SE save. A later `WaterConfig.xml` change never reaches an existing world, and `/wradius`
 or `/wcrush` edits persist: hence the validation below.
- **Mechanism (Water Mod API handshake).** The Water Mod hands its API delegates out once:
 `WaterAPIComponent.BeforeStart` calls `MyAPIGateway.Utilities.SendModMessage(50271, dict)`, which reaches only
 handlers already registered (no buffering, VERIFIED `MyAPIUtilities`). Mod session components are registered
 before plugin components (`MySession.RegisterComponentsFromAssemblies`: Sandbox, mod scripts, then
 `MyPlugins.Plugins`), but `BeforeStartComponents` iterates a `Dictionary`, so the order of the `BeforeStart` calls is
 not relied on. The plugin's session component therefore registers the 50271 handler in
 `LoadData` (all `LoadData` calls run before any `BeforeStart`) and calls `VerifyVersion(22, "DeepEngineers")` when the
 dictionary arrives. VERIFIED real run: every lab-world load received the dictionary in `LoadData`
 (`DE.WaterApi RECEIVED … delegates OK`), and the S1.M9 mutant, registering at the first `UpdateBeforeSimulation`,
 never did (`REFUSE API not received` after 600 ticks): the Water Mod 3.23 sends it from its `BeforeStart` only. Our API client is our own code written from the message-handler
 behaviour (handler id, delegate dictionary keys and types), not a copy of the mod's `WaterModAPI.cs` (section 6).
- **Mechanism (binding and validation).** `C = planet.PositionComp.GetPosition` is read after `MyEntities.Load`; the
 tiles are created in `BeforeStart` (they need only C; section 5.6). Validation runs at the first
 `UpdateBeforeSimulation` and is retried for up to 600 ticks (10 s), logging `DE.Site WAIT|OK|REFUSE <reason>`:
 API dictionary received (else `REFUSE API not received`); exactly one `DE_4546B` planet; `HasWater`; `Rs =
 GetPhysicalData(planet).Item2` equal to `60000.00390625f` bit for bit; `GetDepth` cross-check at five fixed site
 points and at the camera; `WaveHeight = 0`, `TideHeight = 0` through `GetWaveData`/`GetTideData`, `CrushDamage = 0`
 and `CurrentSpeed = 0` by reflection (the API has no getter for them; pinned to 3.23, CONTRACT §3); the sun checks of
 section 5.12, whose failure refuses only time sync (C6 flag, sky-only), never the display bridge. Rs and crush damage
 are re-checked every 10 s. A failure of any other check refuses the display bridge (the tiles stay; section 5.6).
 Real run: `DE.Site OK … after 1 ticks` with the planet spawned by `de.spawnplanet` at the integer centre.

 setter API 22 has besides `SetSeaLevel`) and prints the Water Mod commands for crush, wave, tide and current for the

- **Site constants** are frozen in `tools/worldmap/site_v1.py` (site version 1) and in CONTRACT §3; the bridge writes
 nothing about the site into the SE save, but the save does hold the Water Mod's sea for this planet (above) and the
 tile entity ids as landing-gear references (section 5.15). The constants supersede the earlier recon values (sea
 at Rs ≈ 63.5 km with HillParams 0/0.07 in `probe-world-mapping.md` §4. the basin
 with `Radius = 1.0325` in `probe-collision.md` §6): with the sea 3.5 km above R, air density at the sea would be
 0.583 and atmospheric thrusters would run at about 40 % (VERIFIED formula, `gap-2.md` §2.3).
- **Evidence.** `docs/recon/gap-2.md` §2 (VERIFIED code and arithmetic; `site_v1.py selftest` PASS); Water Mod
 `WaterAPIComponent.cs`, `WaterModComponent.cs`, `; decomp `MySession.cs` (component
 registration order, `LoadDataComponents`, `BeforeStartComponents`), `MySunProperties.cs`, `MySector.cs`,
 `MySectorWeatherComponent.cs`.
- **Fallback.** None needed for the planet itself; S1.G2-E3 (spawn the planet, PB altitude 0.00 ± 0.05 at the
 surface, 9.81 m/s² to 2 km depth, oxygen 90 %, `/wradius` prints 1.052632 (S1 D47); then a changed `WaterConfig.xml`
 must not change the saved sea, and a `/wcrush 0.5` on a copy must make the bridge report the mismatch (S1 D52))
 confirms it before any other plugin work. Real run: `de.g2e3` 24 PASS, 0 FAIL, 2 MANUAL.

### 5.6 Terrain provider and tiles (D2)
- **Decision.** SE collides with Subnautica's seabed natively. A plugin-registered `IMyStorageDataProvider`
 (`SnTerrainProvider`, provider id 45460) reads the user's `CompiledOctreesCache` read-only through the site map.
 It backs a 4×4×3 lattice of 1024³ `MyVoxelMap` tiles at pitch 1,020 m (4-voxel overlap); 34 slots are non-empty.
 Tiles are runtime entities with `Save = false`, recreated in a session component's `BeforeStart` every session.
 "Plug, not fill": the provider returns Subnautica data only, and a missing batch file reads as empty.
- **Terrain gate (independent of the display bridge).** The tiles depend only on: the plugin loaded with its global
 activation gate open (offline single player, exactly one `DE_4546B` planet; CONTRACT §1.2), the octree manifest
 (Subnautica build 20241558, 5,416 `.optoctrees`, 1,203,085,204 bytes, batch rows 7–19) and SE's storage API. They never depend on the display version pins, the Water Mod pin, Subnautica being
 alive or the section. A changed SE version logs `DE.Terrain WARN version` and still creates them. Failure to create
 them in a world that holds `DE_4546B` triggers the fail-safe of section 5.15.
- **Mechanism.**
 - At `Init`: `MyOctreeStorage.RegisterTypes(new[] { ourAssembly })`, guarded against duplicates (Pulsar does not
 fill `MyPlugins.UserAssemblies`, so there is no automatic registration; VERIFIED critic S9).
 - `BeforeStart` (after `MyEntities.Load`, before the first update; VERIFIED `MySession.Load` order): one shared,
 ref-counted `SnOctreeSource` (memory-mapped batch files, ~1.6 MB per-octree state array: empty, solid, surface)
 and **one `SnTerrainProvider` instance per tile** (requests arrive in storage-local coordinates, so each instance
 carries its tile's min corner); the 34 tiles are created with `MyWorldGenerator.AddVoxelMap(name, new
 MyOctreeStorage(provider, 1024³), worldMatrix, entityId, lazyPhysics: true, useVoxelOffset: false)`.
 - **Deterministic entity ids.** `AddVoxelMap` without an id gives each tile a fresh random id every session, and a
 landing gear or magnetic plate locked to a tile saves the tile's id (`AttachedEntityId`); on load
 `MyLandingGear.RetryLockServer` re-locks only if the body's `EntityId` equals the saved id, else it unlocks
 (VERIFIED). Each tile therefore gets `MyEntityIdentifier.ConstructId(ENTITY, 0x44455400000000 | siteVersion << 16 |
 k << 8 | j << 4 | i)` (CONTRACT §3 lists the formula and values). `AddVoxelMap` returns null when the id already
 exists; the plugin then refuses the terrain and runs the fail-safe.
 - Provider contract (full table in CONTRACT §3.1): the request-based `ReadRange(ref MyVoxelDataRequest, detectOnly)`
 is SE's real read path (`MyProviderLeaf`), honouring `RequestedData` and `RequestFlags`; with `detectOnly` it
 never touches `Target` (null there) and sets only `EmptyData`/`FullContent`/`ContentChecked`, and only for a
 range proven uniform; content is in LOD-voxel units (signed distance / LOD voxel size, as the planet provider
 does); `Intersect(BoundingBoxI, lod)` in LOD coordinates and conservative (physics empties every missing cell of
 the whole range on any answer other than `Intersects`; VERIFIED `MyVoxelPhysicsBody`); `Intersect(ref LineD)`
 returns the whole box interval; material bytes are `MyVoxelMaterialDefinition.Index` values resolved by name each
 session; `WriteTo`/`ReadFrom`/`SerializedSize` are 0 or no-op and never throw; `Close` releases the shared source.
 `RequestShapeBlocking` runs `ReadRange` inside the Havok step, so a worst-case budget per 8 m physics cell is set
 (< 1 ms, measured in S4.A-T1).
 - Voxel index convention: SE content index `i` (LOD 0) of a tile is the signed distance at world point `tileMin +
 i`, `tileMin = C +` lattice offset, no half-voxel (SE's planets carry `OffsetVoxelMapByHalfVoxel`; a half-voxel
 slip shifts the seabed 0.5 m per axis). HYPOTHESIS until the synthetic-plane test S4.A-T0b.
 - SE builds the Havok shapes itself, lazily, in 8 m cells on collision layer 28, as for planets (VERIFIED
 `MyVoxelPhysicsBody`). No runtime traffic between the games is needed for terrain.
 - Voxel edits on our tiles are blocked in v1 by prefixes on the non-generic entry points, returning false when the
 target storage's `DataProvider` is `SnTerrainProvider`: `MyStorageBase.WriteRange`; `MyVoxelGenerator.MakeCrater`,
 `CutOutSphereFast`, `CutOutShape`, `CutOutShapeWithProperties`, `FillInShape`, `PaintInShape`,
 `ChangeMaterialsInShape`, `RevertShape`; and the `MyVoxelBase` cut-out request (RPC) handlers. The generic sinks
 (`ExecuteOperationFast<T>`, `WriteRangeInternal<T>`) are not patched: each operator struct has its own JIT body.
 Every patch is checked at start-up.
- **Evidence.** `probe-collision.md` §3, §5, §7; `gap-2.md` §3, §4, §6 (lattice proof: every one of 387,094 solid
 octrees lies inside a tile interior; the coverage oracle was shown to fail at a wrong radial origin); decomp
 `IMyStorageDataProvider`, `MyProviderLeaf`, `MyVoxelPhysicsBody`, `MyPlanetShapeProvider`, `MyWorldGenerator.
 AddVoxelMap`, `MyLandingGear`, `MyEntityIdentifier`, `MyStorageBase`, `MyVoxelGenerator`.
- **Fallbacks.** If 1024³ provider maps cost too much (S4.A-T1): 2048³ tiles on a 2×2×2 lattice. If voxel-map tiles
 show seams or size problems: plan B, a Harmony splice of the tile into the planet's own provider
 (`MyPlanetStorageProvider.ReadRange/Intersect`). If Subnautica does not draw flat faces where its data stops
 (S4.G2-E1): a depth rule "no SE entity below the local cut depth inside the crater footprint", or Subnautica-side cap
 meshes.

### 5.7 Non-terrain statics (D2, hybrid)
- **Decision.** Subnautica's world structures (Aurora, precursor buildings, Lost River trees and skeletons, wrecks)
 are not in the octrees. Their colliders are primitives (VERIFIED by UnityPy counts: Aurora 3,281 boxes + 347
 capsules + 1 sphere, 4 mesh colliders; precursor gun 509 boxes). They are streamed to SE as static Havok primitives
 and censused (D2).
- **Mechanism (Subnautica, `Statics`).**
 - Which objects: chosen by entity type, never by rigidbody state. Being kinematic does not mean static in
 Subnautica: `FreezeRigidbodyWhenFar` makes creatures and pickupables kinematic beyond 48 m from the camera, and our
 own proxy sections are kinematic, non-trigger colliders on Default (VERIFIED). Included: `LargeWorldEntity`
 hierarchies whose prefab has no Rigidbody, or whose class id is on an explicit allow-list (wrecks, precursor,
 Aurora, Lost River trees and skeletons). Excluded: any hierarchy carrying `Creature`, `Pickupable`, `Vehicle`,
 `FreezeRigidbodyWhenFar`, `WorldForces`, `LiveMixin` (unless the class is allow-listed as a static structure),
 our marker, or the bridge root.
 - Collected: non-trigger colliders on the Default layer: box, capsule, sphere with their SN world pose; the rare
 `MeshCollider` is approximated by its OBB and logged. One C12 message per 160 m batch, versioned by an epoch.
 - Primitives entirely above SN y = 0 are not given to SE: SE does not draw Subnautica's above-water objects, and
 invisible walls above the sea would diminish SE.
- **Mechanism (SE).**
 - Each primitive's centre goes through `F⁻¹`; its axes become SE-local with the same mirror as entity-local data:
 `(mapdir⁻¹(q·x̂), mapdir⁻¹(q·ŷ), mapdir⁻¹(q·(−ẑ)))`, determinant +1 asserted (mapdir alone has determinant −1, and
 an improper rotation handed to Havok is undefined); horizontal sizes are divided by `s` at the centre (shear
 dropped, error ≤ `(s − 1)·|h|`).
 - One host entity per batch: a plain `MyEntity` (no model, `Render.Visible = false`, `Save = false`, `IsPreview =
 false`, no `NeedsUpdate` flags), named `DE.Statics.<bx>.<by>.<bz>.e<epoch>`, positioned at the batch centre and
 added with `MyEntities.Add`. `MyEntity.Save` defaults to true and `MyEntities.Save` serialises every saved entity
 (VERIFIED), hence `Save = false`. Its body: `MyPhysicsBody(host, RBF_STATIC).CreateFromCollisionObject(shape, …,
 layer 13)` with an `HkStaticCompoundShape` (or `HkListShape`) of `HkBoxShape`/`HkCapsuleShape`/`HkSphereShape`,
 built and baked on the update thread (Havok-initialised). Layer 13 collides with grids and characters (VERIFIED
 APIs). A host is created when a grid or character comes within 300 m and closed beyond 350 m, on world unload and
 on epoch change.

 in `%LOCALAPPDATA%\DeepEngineers\census\` (derived, never shipped), so parked ships far from the camera also
 collide. Until the census exists, statics collide only where Subnautica has streamed around the camera.
- **Evidence.** `probe-collision.md` §2.5, §5 option 4, §7 step 5; Subnautica `FreezeRigidbodyWhenFar.cs`; SE
 `MyEntity.cs` (Save flag), `MyEntities.cs` (save filter), `MyPhysicsBody.cs`.
- **Fallback.** If the census misses large non-primitive or non-Default structures (S7.N7), add them by mesh-collider
 OBB decomposition or skip them with a logged list.
- **S7.** Settled in CONTRACT §5.4 "S7 conventions" and §5.16 below: the scan by
 `Physics.OverlapBoxNonAlloc` per 160 m batch within 320 m of the camera, the filter by entity type with the census
 allow-list, per-batch sets that only grow within a Subnautica generation, epochs, the census cache read by SE for
 batches Subnautica has not streamed, and the exact box, capsule and sphere conversion through `staticAxes`.

### 5.8 Ship and character rendering (D8)
- **Decision.** The SE plugin loads models through SE's own loader and streams each model once plus per-part
 instance records on change; Subnautica builds native Unity meshes shaded with `MarmosetUBER`.
- **Mechanism (SE, `GridExport`).**
 - Walk follows SE's own grid exporter (`MyCubeGrid.GetModelDataFromGrid`): armor parts from
 `RenderData.Cells[*].CubeParts` (`part.Model`, `InstanceData.LocalMatrix`, `ColorMaskHSV`, texture offsets), fat
 blocks from `FatBlock.Model`, subparts through the entity hierarchy (VERIFIED).
 - Model loading on one dedicated worker thread: `new MyModel(path) { LoadUV = true }.LoadData(forceFullDetail:
 true)` (LOD0; Havok shapes are skipped because the thread has no Havok context; never `MyModels.GetModelOnlyData`,
 which holds LOD1). Indices come from `Triangles` (`Indices`/`Indices16` are null after load; VERIFIED).
 - Change hooks: postfixes on `MyCubeGridRenderCell.RebuildInstanceParts` / `MyCubeGridRenderData.RebuildDirtyCells`
 plus the grid events `OnBlockAdded/Removed`, `OnBlockIntegrityChanged`, `OnGridSplit/Merge` → C4 deltas.
 - Which entities: top-most entities within visibility + 50 m of the camera, from
 `MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref BoundingSphereD, List<MyEntity>, MyEntityQueryType)` with
 a reused list (`MyEntities.GetTopMostEntitiesInSphere` allocates a new list per call), with hysteresis; Subnautica
 keeps meshes 60 s after an entity leaves.
 - Budget: ≤ 2 ms of serialisation per SE tick, newest edits first, try-once writes with re-queue.
 - Materials: absolute texture paths resolved on the SE side (mods, skins via `MyAssetModifierDefinition`), so
 Subnautica never interprets SE's virtual file system.
- **Mechanism (Subnautica, `Mirror`).**
 - Chunked combined meshes: one Unity mesh per (8×8×8 blocks × material × colour), built (mirrored, `(I0, I2, I1)`)
 on worker threads and uploaded on the main thread (Unity 2019.4 has no `MeshData` API) with
 `Mesh.SetVertexBufferParams/SetVertexBufferData/SetIndexBufferData/SetSubMesh` within ≤ 2 ms per frame (VERIFIED
 present in this Unity). Meshes are reused, destroyed explicitly on epoch change.
 - Shader: `MarmosetUBER` loaded through Addressables (`Assets/Marmoset/Shader/Uber/MarmosetUber.shader`, the key
 Nautilus also uses; no Nautilus reference), never `Shader.Find` (two copies exist; VERIFIED with UnityPy). Property
 names from the shader object (`_SpecInt`, not `_SpecularInt`).
 - Texture slots: `_cm` RGB → `_MainTex` (raw BC7 sRGB upload, skipping mips to 512–1024 px); `_ng` normal repacked
 to DXT5nm → `_BumpMap`, gloss → `_SpecTex.a`; `lerp(0.04, _cm.rgb, metal)` → `_SpecTex.rgb`; `_add.g` emissive →
 `_Illum`; `_add.a` paint mask → `_MultiColorMask` R with `UWE_3COLOR`; `_add.r` AO multiplied into `_MainTex`.
 Colour space: Subnautica renders in Linear (`m_ActiveColorSpace = 1`), so `_cm` is read as sRGB while `_ng`, `_add`
 and alpha masks are created with `linear = true` and blitted into `RenderTextureReadWrite.Linear` targets; the
 `_SpecTex.rgb` lerp works on decoded values (S3.M8 seeds the sRGB mistake). v1 repacks on the GPU in process
 (Blit, `AsyncGPUReadback`, repack on a worker). `Texture2D.Compress` is synchronous on the main thread and makes
 only DXT1/DXT5 (several ms per 1024² map), so it is budgeted at one texture per N frames, or v1 uploads RGBA32
 uncompressed; an out-of-process `TexCache` tool is the fallback.
 - Paint: SE's `ColorMaskHSV` drives `_Color` per material through Subnautica's vehicle paint system
 (`ColorCustomizer`-style), approximate until checked against SE's own coloured export
 (`MyRenderProxy.RenderColoredTextures`, S3.C13). ** S7t T6:** SE's shader adds its run-time
 `ColorizeHSV` (0, 0.8, -0.1) to every key colour (`PixelUtilsMaterials.hlsli` `ColorizeGray`,
 `MyTextureDebugMultipliers.Defaults`); the tint takes that shader key, and every block slot in use gets SE's
 `ColorizeGray` baked per texel (per texture set × colour × metalness-colourable, `_MainTex` with UWE_3COLOR off,
 capped by `maskMb`/`maskSide`; `ColorMaskPaint`, CONTRACT §5.4 "S7t colour masks"), the character's P9 bake the same.
 - `SkyApplier` on every renderer with `renderers` filled before its `Start`, `dynamic = true` for moving grids.
 Chunks bordering airtight rooms get the base-interior sky with `SetCustomSky(MarmoSkies.main.GetSky(
 Skies.BaseInterior))` (or `anchorSky = Custom` with `customSkyPrefab = MarmoSkies.main.skyBaseInteriorPrefab`
 before `Start`); `anchorSky = BaseInterior` alone looks for a `SubRoot` or `MarmoLifepodSky` in the parents, finds
 none in our hierarchy and falls back to the outdoor biome sky (VERIFIED `SkyApplier.cs`). Never set a fresh
 `MaterialPropertyBlock` (it would erase the sky values).
 - Glass: clone a vanilla glass material (`Submersible_SeaMoth_Glass`); alpha-masked: `MARMO_ALPHA_CLIP`.
 - Subgrids (rotor tops, pistons, hinges, connected grids of the same physical group) are child transforms of their
 group's root grid (D8): each tick the SE plugin composes, in SE space, the subgrid's matrix relative to the root
 grid and sends it in C2's subpart table, so a subgrid cannot drift from its parent. Subparts (doors, turret heads)
 are child transforms the same way. Proxy sections of a subgrid stay unit-scale roots, posed from the root grid's
 `(o, q, s)` and the subgrid's relative matrix.
 - Lights: up to 64 SE lights within 60 m of the camera mapped to pooled Unity lights, at most 2 shadowed spots
 16).
 - Culling: chunks beyond the current water visibility (from `WaterBiomeManager` extinction, fallback 200 m) are
 disabled.
 - Near clip: `SNCameraRoot.minNearClip` (0.2 m in the scene) is only a clamp; the plugin sets `minNearClip = near`
 and then calls `SNCameraRoot.main.SetNearClip(near)` with SE's `NearPlaneDistance` (0.05 m), so cockpit consoles
 are not clipped.
- **Character.** Order of slices: proxy capsule → static astronaut mesh following the body pose → skinned astronaut
 driven by `MyCharacter.BoneAbsoluteTransforms` (bind poses from `SkinTransform`, conjugated by the mirror). The SE
 astronaut has 81 bones (`SE_astronaut.mwm`, VERIFIED with the mwm reader) and `BoneAbsoluteTransforms` is sized to
 `Model.Bones.Length`, so C1 carries up to 160 bones and the plugin asserts `boneCount == Model.Bones.Length` for
 every character model (`SHADER_MAX_BONES = 60` in VRage.Render11 is a per-mesh-section limit, not a skeleton
 limit). (S5 replaced the assertion by the 78-bone `canon1`; S7 restores `boneCount == Model.Bones.Length` with the
 `ExtraBones` rows 78–80 after the canonical ones, §5.16.) First person hides the `MaterialsDisabledIn1st` set. Using Subnautica's diver body is rejected.
- **Interpolation.** Subnautica keeps the last 4 snapshots, renders at `t = qpcNow − D` with D = 1.25 SE ticks
 (≈ 21 ms), interpolates every entity with one alpha (lerp `o`, slerp `q`, lerp `s`); the camera position uses the
 same alpha and the camera orientation is the latest one (D7). Applied in `ManagedUpdate.Queue.LateUpdateLast` and
 re-sampled once per frame in `Camera.onPreCull` only when the callback's camera is `MainCamera.camera` and before
 any other camera of that frame (Subnautica also renders `WaterSurface`'s clip camera, `SkyProbe`,
 `RenderToCubemap` and GUI cameras; VERIFIED), with the count of re-samples per frame logged (must be 1).
 `presentedSeTick` is the newer bracket tick of that frame's camera interpolation (CONTRACT §5.4). Teleport (camera
 jump > 20 m or > 45° in one tick, or the SE teleport flag): snap, suppress motion vectors for one frame.
- **S5 refinements.**
 - Part classes in `PartAdd.flags`: cube parts, armor edges (read from the render cell's private edge set, so the
 mirror shows SE's edges), fat blocks (armor under construction included: SE gives it a plain `MyCubeBlock`),
 subparts, character parts, the held tool, floating objects. Static parts stay merged in the 8×8×8-block chunks
 (material × colour, plus the no-shadow and metalness bits); a subpart that moves becomes `Dynamic`: its own child
 transform with a shared mesh, posed every tick from C2's subpart table. This replaces "subparts are child
 transforms" above: only moving ones are, so a static ship keeps the chunk draw-call count.
 - Skins are resolved SE-side into model variants (`|skin=` in the model key, geometry sent once per model and skin).
 - Chunk rebuilds use only states where the frame's C4 drain reached its starting head (no half-applied edit drawn);
 the old mesh stays until its replacement is uploaded; one build in flight per chunk key.
 - Entities: enter 250 m, leave 300 m around the SE camera (Subnautica's biome visibility is not known to SE; 200 m
 fallback + 50 m), `EntityRemove` 60 s after leaving; subgrids ride their physical-hierarchy root
 (`MyGridPhysicalHierarchy.GetRoot`).

 split by `MaterialsDisabledIn1st` into a body and a head part, skinned on the canonical skeleton `canon1` (78 bones
 shared by the male and female astronauts; `src/DeepEngineers.Wire/Data/SeSkeleton.canon1.tsv`). Subnautica poses
 it from three sources: SE's own bones from C1 (default: walking, seated, flying), Subnautica's own swim states
 retargeted from the ghost Player's animator while swimming (humanoid `HumanPoseHandler` when the rig is human, a
 per-bone retarget with rest-pose correction otherwise; HYPOTHESIS, O22), and the bind pose when neither is
 available. Fallback: the default Engineer model, never a Subnautica diver; S5a's diver arms stay behind a switch
 until S5.N7 passes. * S7p (P8, P9):* the switch defaults to SE's astronaut in first person (the head
 shadow-only as SE draws it, the diver arms forced off; `fpbody=diver` opts back in); the character's paint is the
 "Fallbacks" bake below, done: SE's `ColorizeGray` per texel, METALNESS_COLORABLE, on the character's materials only;
 the fallback model follows the player's gender; the held tool carries its own skin (CONTRACT §5.4 "Character").
- **Evidence.** `probe-ship-render.md` §1–6; `gap-1.md` §2.3, §3.3; decomp `MyGamePruningStructure.cs`,
 `MySkinnedEntity.cs`, `MyAnimationControllerComponent.cs`; Subnautica `SkyApplier.cs`, `MarmoSkies.cs`,
 `SNCameraRoot.cs`.
- **Fallbacks.** Paint error visible: bake an exact colourised albedo per (texture set × colour), checked against SE's
 export to 2/255. Cockpit shake (camera orientation newer than the hull's): interpolate the camera orientation with
 the same alpha (+1 tick of look latency). Model loading off-thread unsafe: main-thread time slicing.
- **Out of v1:** highlights, LCD text (and offscreen textures while see-through), first-person hands and tools beyond
 the skinned body, armor deformation, decals (section 7). (The block placement ghost came with S7a; welding, grinding
 and drilling are drawn by Subnautica's own effects from S7 event records, §5.16.)

### 5.9 Combat and damage (D6)
- **Decision.** Subnautica's creatures attack SE proxies with their own AI; SE health is authoritative; Subnautica
 health never changes.
- **Physics rate.** Subnautica runs physics at 10 Hz unless a "high precision" behaviour is registered
 (`MainGameController.UpdateFixedTimestep`: `fixedDeltaTime` 0.1 s, 0.02 s while
 `RegisterHighFixedTimestepBehavior` has a client; only the Player in a moving sub, the grappling hook and the
 propulsion cannon register one; VERIFIED). At 10 Hz proxy colliders would lag the rendered hull by up to 100 ms
 (2 m at 20 m/s). Decision: the SN plugin registers the bridge while any moving proxy section (or the character
 section) is within visibility, so all Subnautica physics runs at 50 Hz then; S5.T-P1 measures the cost. Fallback:
 keep 10 Hz and express the S6.B-T1 thresholds and the shape budget per 0.1 s step. `Time.fixedDeltaTime` is
 reported in C7 and asserted (S6.N7).
- **Transform sync.** `MainGameController.StartGame` sets `Physics.autoSyncTransforms = false` (VERIFIED), so moved
 transforms are invisible to physics queries until the next step. The plugin calls `Physics.SyncTransforms` after
 posing test colliders and before any query (self-tests, the raycast service).
- **Proxy sections (Subnautica side, `Proxies`).**
 - Per SE grid, a collision skin from SE's own decomposition (`MyCubeBlockCollector` logic: merged armor boxes, per
 block convex shapes as OBBs), sent in C4, split into sections of ≤ 16 m with box half extents ≤ 4 m.
 - Each section is a root GameObject on layer Default (0), unit scale, kinematic Rigidbody moved with
 `MovePosition/MoveRotation` in `FixedUpdate` (only while unfrozen) to `Apply(o, q, s, c_sec)`, boxes pre-scaled
 with `K`, refreshed per CONTRACT §2.6 (≤ 256 shapes per physics step, round robin).
 - Each section carries `LiveMixin` + `LiveMixinData` (`invincibleInCreative` false), a `DamageModifier` subclass that
 returns 0, `EcoTarget(type = Shark 2030)` anchors along the hull, and a marker component.
 - **Character section.** The swimming SE character is a proxy section of kind "character": a standalone root (not
 under the Player), its own kinematic Rigidbody, a non-trigger `CapsuleCollider` (radius 0.25 m, height 1.8 m), `LiveMixin` +
 `LiveMixinData`, the zero `DamageModifier`, the marker and an `EcoTarget`, posed from C1's character pose and
 disabled when the character is dead or seated. The earlier plan (a capsule on a child of the Player resolved
 through the attached Rigidbody, critic G7) is refuted by code: `EnterLockedMode → PlayerController.SetEnabled(false)
 → UnderwaterMotor.SetEnabled(false)` disables the Player's capsule, makes its Rigidbody kinematic and sets
 `detectCollisions = false`, and a capsule with its own Rigidbody resolves to itself in `MeleeAttack.GetTarget`,
 which then needs a `LiveMixin` on it (VERIFIED).
 - Harmony postfix on the protected `MeleeAttack.CanDealDamageTo` accepts targets carrying the marker (VERIFIED
 patchable: protected, non-virtual). Harmony prefix on `MeleeAttack.OnTouch` stashes the collider and the mouth
 position, because melee bites pass no hit point (VERIFIED critic F5).
 - **Damage capture.** A postfix on `LiveMixin.TakeDamage` emits one C8 record when `__instance` carries the marker
 or is `Player.main.liveMixin`, reading `originalDamage`, type, position and dealer from the arguments. A late
 `AddComponent` forwarder would not work: `LiveMixin.Start` caches `GetComponents<IOnTakeDamage>` of its own
 GameObject once, and the Player's LiveMixin started at scene load (VERIFIED). The exact emit condition (for
 example not for a dead LiveMixin) is fixed by a start-up self-check: 1 SN_HP of `TakeDamage` on a test section and
 on the Player must give exactly one record each.
 - Reapers bite proxies through the generic path for 80 (`ReaperMeleeAttack.GetBiteDamage` returns `cyclopsDamage`
 220 only for `SubControl` targets, which proxies are not; VERIFIED). Grabs and `AttackCyclops` do not apply.
 - Other paths that reach a proxy's `LiveMixin` (VERIFIED): `ShockerMeleeAttack` (ampeel) overrides `OnTouch` and
 deals 15 Electrical to any target with a live `LiveMixin`, without `CanDealDamageTo`; `SeaDragonMeleeAttack`
 (shove, swat, bite) and `CrabsnakeMeleeAttack` (vehicle path) call `TakeDamage` directly with Normal damage and the
 default position; crashfish `DamageSystem.RadiusDamage` deals Explosive. None of them passes through the mouth
 stash, so their hit point falls back to the closest point of the section's colliders to the dealer's position
 (C8 flag).
- **Damage record.** One record per `TakeDamage` on a proxy or on the ghost Player: amount = `originalDamage` in
 Subnautica hit points (unit SN_HP), Subnautica damage type, target entity id and section, the hit point in
 **SE-local** coordinates computed with the same `(o, q, s)` that placed the section at the last physics step, the
 SN world point for logs, dealer species, a dedup sequence number and the expected scale factor (CONTRACT §4, §5).
 For a section of a subgrid, Subnautica converts the point into the subgrid's own frame with the C2 subpart matrix it
 used for that physics step and sends the subgrid's entity id with the flag "frame = subgrid" (a 10 rpm rotor turns
 3° in 50 ms: 0.26 m at 5 m radius, a different small-grid cell).
- **SE application (`Combat`).** SE validates every record (current Subnautica generation, `worldEpoch`, amount ≤
 500 SN_HP, known entity and section) before applying it. Factor by target class (all values VERIFIED from SE's and
 Subnautica's data; the equivalence rule is a gameplay choice):

| Target | SE reference | Factor (SE units per SN HP) | Reaper bite 80 becomes |
|---|---|---|---|
| Character | `SpaceCharacterHealth` MaxValue 100 (up to 150 with `MaxHealthBuff` levels) vs Subnautica Player maxHealth 100 | 1.0 | 80 HP (80 % at buff level 0, 53 % at 150) |
| Small-grid block | small light armor cube MaxIntegrity 100 | 1.0 (`k_small`) | 80 integrity (80 % of light, 14 % of heavy 560) |
| Large-grid block | large light armor cube MaxIntegrity 2,500 | 25 (`k_large`) | 2,000 integrity (80 % of light, 12 % of heavy 16,500) |

 Rule: "a light armor cube of the grid's size is as tough as a Subnautica diver". Forwarded types (CONTRACT §4.1):
 Normal → `DeepBite`, Explosive → `DeepBlast`, Electrical → `DeepShock`, all with the same factor. Grids: the block
 containing the SE-local hit point (nearest block within one cell otherwise) receives `MySlimBlock.DoDamage(amount ×
 factor, type, sync: true, hitInfo, attackerId 0)`; SE then applies its own modifiers (block, grid and definition
 multipliers, all 1 by default; `DamageRatio`). Character: `MyCharacter.DoDamage(amount × 1.0, type, updateSync:
 true)`. Dedup by (Subnautica generation, sequence). Both calls do nothing unless the world allows it:
 `MySlimBlock.DoDamage` needs `MyCubeGrid.BlocksDestructionEnabled` (world `DestructibleBlocks` on, grid not
 immune), and `MyCharacter.DoDamage` does nothing in Creative (`CharactersCanDie` false) (VERIFIED). Combat therefore
 needs a Survival world with destructible blocks.
- **Abyss rule (purpose: nothing reaches the invisible −3,000 m floor).** On `DE_4546B` only, every 100 ticks:
 - Crush zone below SN y −2,000 m: every block whose centre is below it takes `DeepPressure` = MaxIntegrity ×
 (0.10 + 0.10 per 100 m below −2,000 m); a character takes 10 HP + 10 HP per 100 m below. A hull sinking at 5 m/s
 loses its lowest block after about 13 s (≈ 70 m below the limit) and is gone well before −2,500 m. The earlier
 "lowest block, 10 % per 100 ticks" rate needed about 28 min for a 100-block hull, so any sink speed ≥ 0.6 m/s
 reached the floor first.
 - Floor guard at −2,500 m, in every world type: the downward velocity component (along local down) of grids and
 characters below it is clamped to 0 each tick, with a HUD notice. It needs no damage, so it also works in
 Creative or non-destructible worlds, where the crush zone is inactive and `DE.Abyss DAMAGE-INACTIVE <reason>` is
 logged once.
 - Suspended (guard still on) for grids flagged by the terrain fail-safe (section 5.15). Rates are chosen (O7).
 Water Mod crush is 0 on this planet.
- **Evidence.** `probe-collision.md` §8, `subnautica.md` §3c, `critic.md` F5, F6, G4; SE `Content\Data\CubeBlocks\
 CubeBlocks_Armor.sbc`, `Components.sbc`, `Stats.sbc`, `EntityComponents.sbc` (`MaxHealthBuff` 100…150), `Bots.sbc`;
 decomp `MyCubeBlockDefinition.Init` (MaxIntegrity = Σ count × component MaxIntegrity), `MySlimBlock.DoDamage`,
 `MyCubeGrid.BlocksDestructionEnabled`, `MyCharacter.DoDamage`, `MyMaxHealthBuff`; Subnautica `MainGameController`,
 `LiveMixin`, `MeleeAttack`, `ShockerMeleeAttack`, `SeaDragonMeleeAttack`, `CrabsnakeMeleeAttack`, `UnderwaterMotor`.
- **Fallbacks.** Bites never fire (line of sight to section centres, EcoTarget ranges): more anchors, smaller
 sections; last resort, a `SubControl`-style component on sections (then reapers deal `cyclopsDamage` 220, and the
 factor table gets a separate row). Tunnelling through thin boxes: speculative CCD on proxies, thicker boxes.
 1,000 shapes cost > 2 ms: one non-convex `MeshCollider` per section baked off-thread (`Physics.BakeMesh`).
- *S7p-gameplay ( S7p, P11; SE's weapon
 explosion and tool events (the S7 C2 event window, already sent for the effects) are resolved by Subnautica against
 its creatures — a ray along each shot (to 250 m: the creature is not in SE, so SE's own hit may be short of it), a
 sphere for explosions and for the tools' work points — and applied with `LiveMixin.TakeDamage` on creatures only,
 with SE's own numbers through `k_char` (CONTRACT §4.2, §5.4 "S7p-gameplay"); Subnautica owns creature health, SE
 never sees creatures. v3: the ocean authority's Subnautica applies it.

### 5.10 Host save and session (D7, part 1)
- **Decision.
 never saved. On this PC it is `<Subnautica>\SNAppData\SavedGames\slot0000` (`gameMode` 1 = Freedom, session
 702d8c12-288a-43c2-9d23-a9efde02f690; Creative is forbidden: `GameModeOption.Creative`
 includes `NoAggression`, so creatures would never attack (VERIFIED critic S7).
- **Bridge mode only when launched for it.** BepInEx loads the plugin in every Subnautica launch, including a player
 starting Subnautica to play it, or a second instance (`forceSingleInstance = false`). The SN plugin enters bridge
 mode only when the environment variable `DEEPENGINEERS_BRIDGE=1` is set or the launcher's named mutex
 `Local\DeepEngineers_v1_launcher` exists; otherwise it stays dormant (vanilla Subnautica, no section, no window or
 save changes, console usable). It also takes the mutex `Local\DeepEngineers_v1_sn` and refuses when it is held
 (CONTRACT §6).
- **Bridge start on the first player-loop `Update`, never in `Awake`.** BepInEx runs
 its chainloader from `Application`'s static constructor, before the first scene load. A "DeepEngineers.Bridge"
 GameObject created in the plugin's `Awake` was destroyed by the StartScreen scene load 8 s later with no `Update`
 call, although `DontDestroyOnLoad` had been called. The plugin's `Awake` now only logs; its component (on BepInEx's
 manager object, which survives) runs the whole bridge start once on frame 1 (scene PreStartScreen): token, mutex,
 window role, section, then the bridge object with `DontDestroyOnLoad` and `SceneCleanerPreserve` (its scene is
 logged, "DontDestroyOnLoad"). The object's `OnDestroy` writes `snPhase = None` unless `OnApplicationQuit` ran: a
 destroyed bridge is a failure (SE sees PeerExited), not a quit.
- **Mechanism.** At the main menu the SN plugin loads the slot with `uGUI_MainMenu.main.LoadGameAsync(...)`; a Harmony
 prefix on `IngameMenu.SaveGame` and a second guard on `SaveLoadManager.SaveToDeepStorageAsync` skip saving; there is
 no autosave in Subnautica (VERIFIED: no autosave string). At load the plugin asserts `GameModeUtils.IsInvisible`
 is false and refuses combat otherwise. The bridge root object carries `SceneCleanerPreserve`. Options the plugin
 changes for the session (frame rate, vsync, run in background, clipmap quality, FOV) are changed in memory only and
 never saved (CONTRACT §1.1).
- **Fallback.** If a player only has a Creative save, the bridge renders but logs "combat disabled".

### 5.11 Ghost player (D7, part 2)
- **Decision.** Subnautica's `Player` stays in the world as a puppet in locked mode so streaming, biome detection,
 music, eco-targets and damage hooks keep working; its body is hidden; SE's character mirror is drawn instead.
- **Mechanism.** The `FreecamController` pattern, re-implemented (not instantiated, because
 `WaterscapeVolumeOnCamera` forces "camera inside" false while a `FreecamController` is active):
 `Player.main.EnterLockedMode(null)`, then `MainCameraControl` disabled (EnterLockedMode re-enables it, VERIFIED),
 a dummy entry pushed on `InputHandlerStack.main`, `GameInput.ClearInput` each frame, `Player.FreezeStats`
 asserted every second (the oxygen drain in `Player.Update` is blocked only by frozen stats). The camera rig is
 unparented and driven from C1 each frame (position interpolated by QPC stamps, latest orientation). Streaming
 follows `MainCamera.camera` automatically (VERIFIED `LargeWorldStreamer`).
- **Where the Player is (one rule).** Each frame the plugin moves the Player root to the SE character's pelvis (C1
 `pelvisSn`) when a character is present, not seated, and the camera is that character's own first- or third-person
 view (C1 `cameraMode` 1 or 2), and to the camera position otherwise: seated, spectator (`cameraMode` 4, also while
 the character stands elsewhere), cockpit or any other camera, no character. Outside the dive zone (C5 zone FAR or off-site, or the camera beyond ρ∞ 7,900 m, above +350 m or below
 −7,900 m) nothing is moved: in S1, which has no Idle recipe yet, the camera and the Player hold their last driven pose. S2 keeps this rule in every view state; the Idle, Track and
 Full recipes come on top of it (CONTRACT §5.4). With parent null the
 Player would never move and would stay where the host save was made, normally inside the lifepod: `escapePod` true
 makes `IsInsideWalkable` true everywhere (camera-inside fog underwater, `CanBeAttacked` false), and biome music,
 `BiomeGoalTracker` and `VoidGhostLeviathansSpawner` would use that spot's biome. With the root at the camera,
 `AggressiveWhenSeePlayer` (which targets `Player.main.gameObject`) would send creatures at the lens in third person
 (VERIFIED). At load the Player is moved out of the lifepod; after the first frame the plugin asserts `escapePod ==
 false` and `currentSub == null`, and it logs the Player-to-camera distance.
- **FOV.** `Player.Start` adds `PDACameraFOVControl`, which every frame lerps the FOV back to
 `MiscSettings.fieldOfView` (VERIFIED). The plugin disables it (`enabled = false`; never `Destroy`, whose `OnDestroy`
 resyncs to `MiscSettings`), calls `SNCameraRoot.main.SetFov(SE vertical FOV in degrees)` whenever C1 changes, and
 asserts `mainCamera.fieldOfView` each frame (drift logged). `MiscSettings.fieldOfView` is never written
 (`GameSettings.SaveAsync` would persist it).
- **Subnautica's own UI is hidden.** `uGUI_SceneHUD.IsActive` is false only in `Mode.Piloting` or cinematics, and
 `EnterLockedMode` sets `Mode.LockedPiloting`, so oxygen and health bars, quick-slots, the hand reticle,
 `ErrorMessage` lines, PDA notifications and subtitles would show through SE's transparent backbuffer and pollute
 captures (VERIFIED). The plugin calls `GUIController.SetHidePhase(HidePhase.MaskHUD)` (re-asserted after loads; `All` would also hide the first-person view model and the breath bubbles that S5a uses), or
 disables uGUI's screen canvases except its calibration canvas; suppresses PDA voice, subtitles and notifications
 (prefixes on the `PDASounds`/`VoiceNotification`/`ErrorMessage` entry points, or the PDA voice VCA at 0); zeroes
 `Player.radiationAmount` and disables `RadiationsScreenFX`. C7 reports `uiHidden`, part of `snReady`.
- **Interior fog.** Needs the world settings `EnableOxygen` and `EnableOxygenPressurization` (`MyCubeGrid.GasSystem`
 exists only then; VERIFIED `MyCubeGridSystems`); without them the flag stays false and `DE.Interior OFF
 pressurization disabled` is logged once. The flag comes from `MyGridGasSystem.GetOxygenRoomForCubeGridPosition` at
 the camera's grid cell (`MyOxygenRoom.IsAirtight`), keeping the last value while the background recompute runs
 (`GetRooms` returns false during it and rebuilds every room's cell list per call), and is sent as a C1 flag;
 `RoomSet` (C4) is sent only on change. A postfix then makes `Player.IsInsideWalkable` return true, which sets
 `_CameraInside` in the fog effect, exactly as inside a Cyclops. It also makes `Player.CanBeAttacked` false
 (VERIFIED), so creatures go for the hull, not the pilot.
- **Underwater flag.** In locked mode with no sub, `Player.UpdateIsUnderwater` forces `isUnderwater` false. About 30
 readers use `IsUnderwater`, audio among them: left false, `WaterAmbience` plays its surface loop and "reach
 surface" cues underwater; patched true, `BreathingSound` starts diver breathing and `RebreatherDepthWarnings` fires
 PDA depth warnings (VERIFIED). Narrow patches (`WaterAmbience` and the visual consumers) are preferred over a global
 `IsUnderwater` postfix; S1.N6 records visuals and FMOD events in both variants.
- **Arms and breath.** `S5aHost` hangs off the ghost rule's camera
 callbacks (`S1Host.AfterGhostFrame`, `AfterPreCullFrame`) and reads the C1 swim extension (CONTRACT §5.4) the C1
 reader decodes with every record. The first-person view model's renderers are exempted from the ghost rule
 (`GhostPlayer.Exempt`) and enabled only while C1 says inWater, the camera is under the sea, SE is in first person,
 C5 says Underwater/Surfacing and C1 drives the camera; the view model is posed at the camera rig with the rig's yaw
 (MainCameraControl's own rule) and the Player animator's parameters are set from C1 with `ArmsController` disabled.
 The ghost's `OxygenManager` is set to `o2/255 × capacity` each frame (its surface refill disabled) so the game's
 `LowOxygenAlert`, oxygen bar and suffocation overlay react to SE's suit; `Player.tookBreathEvent` and
 `PlayerBreathBubbles.MakeBubbles` fire on Subnautica's own cadence. Never-dies guards: `liveMixin.shielded`,
 a Harmony prefix skipping `Player.SuffocationDie`, and a 1-unit floor when that patch is missing. No motor, input,
 rigidbody or save API is touched. `docs/S5A-STATUS.md`.
- **Evidence.** `subnautica.md` §3a; `probe-ship-render.md` §3.3; Subnautica `Player.cs`, `PDACameraFOVControl.cs`,
 `SNCameraRoot.cs`, `uGUI_SceneHUD.cs`, `GUIController.cs`, `AggressiveWhenSeePlayer.cs`; SE `MyGridGasSystem.cs`.
- **Fallback.** If the `IsInsideWalkable` patch has unwanted side effects (`FlyAwayWhenScared`, oxygen), narrow it to
 the fog consumer (`WaterscapeVolumeOnCamera`). If `OutOfBoundsWarp` fires on the Player, disable it on the ghost.

### 5.12 Time of day
- **Decision.** SE owns time. `SunRotationIntervalMinutes` = 20 makes SE's period equal Subnautica's 1,200 s day
 (world settings, section 5.5).
- **Mechanism (SE).** At bind time the plugin reads `MySector.SunProperties.SunRotationAxis`,
 `SunProperties.BaseSunDirectionNormalized`, the sector weather component's saved base direction (reflection),
 `EnableSunRotation` and the interval, logs them with `siteVersion`, and checks `|axis·U0| < 1e-3`, `|base·axis| <
 1e-3`, interval = 20 and rotation enabled. The axis is SE world +Y, which is SN −Z: the site lies on the sun's
 equator (VERIFIED `MySunProperties.SunRotationAxis = normalize((B × Up) × B)` gives +Y for B = (0, 0, 1)). With the
 stock environment the axis would be (−0.342, 0.704, 0.622): sun declination 38.5° at the site, sunrise hour angle
 129°, 71.7 % daylight, so a remap that assumes sunrise at ±90° would make `IsDay` wrong for 21.7 % of each day.
 If a check fails, time sync is refused (C6 flag) and Subnautica drives only the sky, with the sunrise hour angle
 computed from the actual axis and declination (C6 `sunriseHourAngleRad`). Each tick at the camera: sun direction
 `SunDirectionNormalized`, the read axis, local up `U_P`; meridian `M = normalize(U − axis·(axis·U))`, west `W =
 normalize(axis × U)`; hour angle `H = atan2(s·W, s·M)`; sent in C6 with the sun direction mapped to SN space and a
 time-jump counter.
- **Mechanism (Subnautica).** `DayNightCycle.main.Pause`, re-asserted every frame while the time driver is active
 (`debugFreeze`, or `Pause` whenever `dayNightSpeed ≠ 0`): the console commands `daynight`, `day`, `night` and the
 end of skip-time set `_dayNightSpeed = 1` again (VERIFIED), and external changes are logged. Each frame the target
 sky hour `T = 12 h + H·24/2π`, inverted through Subnautica's day remap (day 0.25…0.75 → scalar 0.125 + (c −
 0.25)·1.5; night → (0.875 + (c′ − 0.75)·0.5) mod 1), and `timePassedAsDouble` advanced **forward** by the wrapped
 difference × 1,200 s, so timers, growth and spawns keep working. A jump larger than ¼ day, or a new jump counter, is a
 single forward jump. `uSkyManager.SunDirection` and `NorthPoleOffset` are set from the mapped sunrise direction and
 the camera latitude relative to the sun axis (≤ 1.96° inside the tile, 7.4° at `ρ∞` = 7,800 m). Subnautica's
 clamped underwater `SunLight` (±65°) is kept: it is part of its look. Paused: no advance. Tests use the plugin's own
 time command, never the console.
- **Host boundary.** SE sends only host-neutral sun data; Subnautica's day remap stays in the SN plugin.
- **S7a.** Built in S7a with two refinements, specified in CONTRACT §5.4 "S7a
 conventions": C6 carries the sun's rotation pole in SN axes (pad bytes 0x3C–0x3F, centi-degrees), because the hour
 angle and the sun direction alone cannot fix `SunDirection`/`NorthPoleOffset` near sunrise and sunset; and a small
 backward step (≤ 2° of cycle angle: the camera moving west faster than the sun) is a hold, not a day-long forward jump.
 The mapping matches Subnautica's sun **disk** (`uSkyManager.SunDir`) to SE's sun exactly; its stylised `SunLight` keeps
 its ±65° law.
- **Evidence.** `probe-world-mapping.md` §6 (APIs VERIFIED, method HYPOTHESIS); decomp `MySunProperties.cs`,
 `MySector.cs`, `MySectorWeatherComponent.cs`, `MyObjectBuilder_SessionSettings.cs` (default 120); Subnautica
 `DayNightCycle.cs`. Oracle S7.WM-5.
- **Fallback.** If the forward-only drive disturbs Subnautica systems, drive only the sky (`uSkyManager`) and leave
 `DayNightCycle` running at its own pace.

### 5.13 Performance budget (D10)

CPU % of 16 threads)

| Situation | SE | Subnautica |
|---|---|---|
| Both at their menus | 2.8 GB working set | 948 MB; machine had 12.4 GB of 32 GB free |
| Both in world | 5.6 GB | 2.3 GB |
| Visible but unfocused | 11–16 % CPU, 40–55 % GPU | 25–30 % CPU, ~43 % GPU |
| Subnautica fully hidden or minimised | 91–97 % GPU (99–100 % in run 3) | 4–9 % CPU, 2–8 % GPU (0–7 % / 0–5 % in run 3, with one 5 s `Responding = False` blip) |
| Both visible, uncapped, 1440p | 95–100 % GPU combined | |
| SE underwater with the Water Mod | FrameTime 8.33 ms (120 fps cap), CPULoadSmooth 24 %, 13.3 GB virtual; 19–33 % CPU, 44–100 % GPU | |

Conclusions: the GPU is the bottleneck; the hidden side must not render (Subnautica in Space and Track, SE's scene
underwater); SE does not stop rendering when merely covered; a fully covered Unity window may stall, so Subnautica
owns its idle state and is never a fully occluded window while it must render (section 4.4).

| State | Subnautica (`vSyncCount`, `targetFrameRate`) | SE | Target (HYPOTHESIS until S5.T-P1) |
|---|---|---|---|
camera.enabled = false` and the GUI camera off, (0, 10), process BelowNormal, muted | as solo | SE frame rate within 5 % of solo; Subnautica ≤ 11 fps |
| Approach / Track (whole surface band: sailing, hovering, building on islands) | time running, cameras off (`MainCamera.camera` stays valid, streaming follows the transform), (0, 10), Normal | full | SE frame time within 5 % of solo |
| Approach / Prime (seconds before a crossing) | cameras on, full rate, shaders warm | full | SE frame time +10–25 % while Prime; Prime duty cycle logged; warm-up hitch < 50 ms |
| Underwater | Full: (1, any) at 165 Hz, or (0, 120) if CPU-bound, Normal | simulation + Water Mod physics + sprites only | Subnautica p99 ≤ 16.7 ms; SE simulation ratio ≥ 0.98 |
| Unfocused overlay | (0, 30) if Underwater | SE's own | |

- The plugin owns both values per state and sets `Application.targetFrameRate` and `QualitySettings.vSyncCount`
 directly: Unity ignores `targetFrameRate` while `vSyncCount ≠ 0`, Subnautica's VSync option is applied at start,
 `GraphicsUtil.SetFrameRate` clamps to `frameRateMin = 30`, and `StartScreen.Start` forces 144 (VERIFIED). It
 re-asserts them every frame while the bridge is active (or postfixes `GraphicsUtil.SetVSyncEnabled/SetFrameRate/
 SetQualityLevel`), saves the user's values at load, restores them on quit and never calls `GameSettings.SaveAsync`.
 On the wire SE asks for a pair through C5 `targetFps`: 0 = no cap, (1, −1); n = (0, n), so Idle and Track send 10,
 Full 0 (or 120 when CPU-bound) and the Unfocused overlay 30 while Underwater (CONTRACT §5.4).
 A `vSyncCount` change fires `DisplayManager.OnDisplayChanged` (canvas rescale; window styles re-applied, section
 5.1), and `LargeWorldStreamer` halves its per-frame budget when `vSyncCount < 2`. The applied pair is reported in C7.
- Both plugins opt out of Windows power throttling on their own process: `SetProcessInformation(ProcessPowerThrottling,
 ControlMask = EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, StateMask = 0)`, return value logged (constants REPORTED
 from SDK headers; supported on build 22000, VERIFIED: both plugins logged `ok 0x5` in the S0 real run). SE does it
 at plugin `Init`, before its activation gate can be known, which CONTRACT §1.1/§1.2 allow (process-level, ends with
 the process, no game setting changed); Subnautica only in bridge mode. SE paces with 1 ms sleeps (`timeBeginPeriod(1)` in
 `MyVRagePlatform.Init`, VERIFIED), which Windows 11 may stop honouring for occluded, silent processes (Microsoft
 `timeBeginPeriod` documentation; whether either game is affected here is HYPOTHESIS, S5.T-P1).
- No CPU affinity in v1. Subnautica never runs above SE's priority. SE's render thread and process class untouched.
- Subnautica clipmap quality Medium for two-game sessions (8 meshing threads), set in memory only; raise only if
 S5.T-P1 allows.
- Subnautica physics at 50 Hz while moving proxies are visible (section 5.9): its PhysX cost is part of S5.T-P1.
- Work budgets: SE update thread ≤ 2 ms per tick for geometry serialisation, ≤ 0.3 ms for the pose snapshot
 (target); Subnautica ≤ 4 ms per frame to drain rings (or 48 MiB), ≤ 2 ms per frame for mesh uploads, proxies ≤ 1 ms
 PhysX p95 for 1,000 boxes per physics step.
- *S7p-gameplay ( S7p, P14
 more than the frame: the room class of a part is an O(1) index lookup (the RoomSet of a 100,669-part ship cost
 4,474 ms as a scan); Subnautica's mirror also caps per frame the Child parts created (2 ms, 48), the cell builds
 posted (2 ms) and the worker results taken (3 ms), marks the parts of prepared models once per frame and caches the
 per-grid model totals; SE amortises its S5 export reads to 4 ms per tick on average (a debt paid back by skipping
 reads: the stream is late, never a stutter train). Backlog rows `s7p.mirror` (Subnautica) and `s7p.export` (SE),
 CONTRACT §5.4 "S7p-gameplay".
- RAM: in world both games use 7.9 GB working set together (measured). The bridge adds an 80 MiB shared section
 (mapped by both), Subnautica mirror meshes (estimate 50–150 MB of VRAM for a 2,000–5,000-block ship), SE voxel cell
 caches for the tiles (S4.A-T1 measures them) and page cache for the octree files (reclaimable). The launcher refuses
 to start below 12 GB available and warns below 16 GB; commit must stay < 85 % of RAM (S5.T-P1).

### 5.14 Lifecycle and watchdogs (D11)
- **Start order.**
 1. Launcher preflight: Steam running; no `Legacy`, `SpaceEngineers`, `Subnautica` or `UnityCrashHandler64` process
 alive (stale crash handlers are closed by exact PID; a `SpaceEngineers.exe -report` crash form left by Pulsar
 games are never killed); no windowless single-thread game process
 from an earlier session (the zombie signature); ≥ 12 GB RAM available; a backup set newer than the last DE
 session (timestamp in our JSONL log): `se-appdata`, `subnautica-saves` (`<Subnautica>\SNAppData\SavedGames`, which
 holds the host slot) and `subnautica-locallow` (logs only); `OpenFileMappingW("Local\DeepEngineers_v1")` returns
 `ERROR_FILE_NOT_FOUND` (otherwise the holder PIDs are listed and the launcher stops).
 2. Start Subnautica first, directly: `Subnautica.exe -monitor 1` with `DEEPENGINEERS_BRIDGE=1` in its environment, so the
 launcher owns the PID. The launcher holds the named mutex `Local\DeepEngineers_v1_launcher` until it sees the
 ready phase or its wait ends; the mutex is the bridge token for any launch method.
 Fallback: `steam.exe -applaunch 264710`, PID found by exact path. Subnautica first, because its window would
 otherwise appear over a running SE and take focus at creation.
 3. The SN plugin takes its mutex, creates the section (CONTRACT §6 creator rules), loads the host save, parks in
 Idle, restyles and lowers its window (section 5.1) and publishes `snPhase = ReadyIdle` (Idle recipe and restyle
 from S2; in S1 it reached `ReadyIdle` 18.4–20 s after its start, with the camera then driven, cameras on and time
 running, S1 D73). The launcher waits for it
 open-only (`OpenFileMappingW`, never creating), up to 180 s (S0 waits only for `Menu`), then closes its section
 handle and drops the token. On timeout it does not start SE: it reports and leaves Subnautica running, and it
 never kills; `--se-anyway` starts SE with the bridge offline (changed after the S0 real run, where starting SE
 anyway hid a destroyed bridge object for 180 s).
 4. Start SE as the launcher's child: `Legacy.exe -noUpdate -noSplash -noPrompt -bin64 "<SE>\Bin64"` (VERIFIED,
 then `AllowSetForegroundWindow(sePid)`.
 5. The SE plugin opens the section (never creates it), validates identity (CONTRACT §6), publishes `seHwnd`, its
 generation and the world epoch, takes the foreground itself when the link comes up (section 5.1; S2) and places
 Subnautica directly under SE.
 Both games must run at the same integrity level: after both start, the launcher compares their token elevation and
 integrity level and refuses mixed elevation (UIPI would block the window calls and `GetAsyncKeyState` across
 levels; the section's DACL alone does not make mixed elevation work).
- **Watchdogs.**
 - Heartbeats: `GetTickCount64`, written from each game thread (a hung game thread reads as stalled): SE's update
 thread; Subnautica's main thread from `Update` on the unscaled clock. SE's autosave blocks its update thread for
 about 150 ms, so heartbeat
 gaps up to about 150 ms are normal and every freshness threshold below the stall rule must tolerate them. SE runs
 frame-capped at 120 fps on this PC while its simulation steps at 60 Hz (S0.N7).
 - SE phases: SE loads a world synchronously on its update thread (`MyGuiScreenLoading.Update → RunLoad →
 RunLoadingAction → MySession.Load`, which covers script compilation, definitions, `MyEntities.Load` and
 `BeforeStart`; VERIFIED), so `sePhase = LoadingWorld` is set by a prefix on `MyGuiScreenLoading.RunLoad`, before
 any blocking work, and the heartbeat is also written from the `MySession.LoadingStep`/`LoadingLocal*` callbacks
 (update thread) during the load. Timing (VERIFIED real run, S1.N12): the section shows `LoadingWorld` within
 0.03 s of the RunLoad prefix, while the log line `DE.Phase Menu -> LoadingWorld` is written only when `RunLoad`
 returns (the load's length later), so timing checks read the section or the RunLoad-prefix line (CONTRACT §5.4).
 A world whose `.sbsB5` was deleted (a save edit) fails its next load with `MyLoadingNeedXMLException` and a "convert
 from XML?" prompt that needs one click in SE, so such an edit never precedes an unattended launch.
 `Saving` is set by a prefix on `MySession.Save(out MySessionSnapshot, string,
 Action<SaveProgress>)` and cleared by a finalizer. `Exiting` (`seExiting`) is set by a prefix on
 `MySandboxGame.ExitThreadSafe` with `[HarmonyPriority(Priority.First)]`: Pulsar v2.4.3 prefixes that method with
 `Process.GetCurrentProcess.Kill` and skips the original, so `OnGameExit` and `Dispose` never run under Pulsar
 (REPORTED Pulsar source `; No
 cleanup relies on exit: process exit is the normal path.
 - Timeouts (D5): heartbeat older than 3 s in play, 120 s while either side reports a loading phase (SE
 `LoadingWorld` or `Saving`, Subnautica 1–3). The consequence is recoverable on both sides (PeerStalled, section
 4.3). S1 measured the world load times: 22.9–29.6 s from `RunLoad` to the
 first update for the lab world, 23. a load beyond 120 s
 only stalls the link, it ends nothing.
 - Process exit: each side opens the peer's PID with `SYNCHRONIZE`. SE polls `WaitForSingleObject(h, 0)` (a
 non-blocking call of a few µs) every tick while the margins are on (Prime, Underwater, Surfacing) and once per
 second otherwise; while the margins are on, the render thread also checks `IsWindow(snHwnd) ∧
 IsWindowVisible(snHwnd)` each frame. `snPhase ∈ {Quitting, None}` or a changed `snPid`/`snGeneration` means
 PeerExited at once. With the once-per-second poll, an exit is seen within 1 s plus one tick (928 ms measured on the
 real C# detector; the S0.T-L1s threshold is worded that way). When Subnautica quits on its own it sets `Quitting`
 and waits up to 100 ms for SE's acknowledgement (C5 overlay bit) before closing its window: the wait is spread over
 frames, except inside `OnApplicationQuit`, where it is a bounded poll on the main thread (CONTRACT §7).
 - Subnautica's side: SE heartbeat stale → SE stalled: mute, freeze, Idle; Subnautica **never quits on heartbeat
 SE process exit or `seExiting` → mute, freeze
 minimise without activation (`SW_SHOWMINNOACTIVE`) and quit within 10 s, without saving (VERIFIED real run
 S0.N9: `DE.Link EXITED` → `Quitting` → the game's own quit path). An SE block already dead when Subnautica first sees
 it is Stale and never makes Subnautica quit. S8: the reaction table is code (stalled:
 mute, freeze, Idle, never quit; exited: plus minimise and quit); the in-game quit (`IngameMenu.QuitGameAsync(true)`)
 waits for the world to settle without a timeout, so `Application.Quit` follows 5 s after the issue when SE exited;
 an Alt+Tab / ESC / peer edge is audited 1. `s8.restore` rows, S8.N4).
 - No blocking cross-process call on any game thread. Rings re-synchronise and state is resent on every new
 generation or link recovery (CONTRACT §5.2, §6).

The rows below say "above water" / "underwater" in S2's terms (policy `Waterline`, and Space under both policies). From
S7a under `Site`: inside the site below the band the picture latch is on above water too, so
a Subnautica exit, crash or stall there is handled as the underwater rows say (the latch drops within the same SE frame:
the scene is drawn first, margins go off after a presented drawn frame; the player sees SE's Water Mod sea and sky) and
the next on edge waits for `snReady` again (section 4.2 "Picture policy (S7a)").

| Event | Detected by | Reaction | Player sees |
|---|---|---|---|
| Subnautica exits or crashes above water | process exit (1 s poll: ≤ 1 s + one tick) | PeerExited: SE never goes see-through; audio restored | nothing until a dive; then SE's Water Mod view and a HUD notice |
| Subnautica exits or crashes underwater | process exit (per-tick poll), lost window, `Quitting` | PeerExited: SE draws its scene and drops the margins on the next frame; audio restored | one-frame switch to SE's own underwater view; simulation never stopped |
| Subnautica stalls (≥ 5 s main-thread block measured once) | heartbeat (3 s) | PeerStalled; the launcher offers (never performs) a kill after 30 s, by exact PID; ghosting is disabled, so the window stays restyled | underwater: SE's own view after 3 s; elsewhere nothing; recovery with a link epoch when it resumes |
| SE exits normally | `seExiting` (prefix before Pulsar's kill; S8: written before the prefix's log flush) or process exit | Subnautica mutes, freezes, minimises without activation and quits without saving (`IngameMenu.QuitGameAsync(true)` or `Application.Quit`; S8: `Application.Quit` 5 s after the issue if still running) | desktop |
| SE crashes | process exit | Subnautica mutes, freezes, minimises without activation and quits after ≤ 10 s; Pulsar starts `SpaceEngineers.exe -report` (crash form) | SE's crash form |
| SE hangs | heartbeat (3 s) | Subnautica mutes and freezes; it does not quit | frozen SE; underwater, the frozen Subnautica picture under SE's frozen HUD |
| SE world load or save | `sePhase` LoadingWorld / Saving (120 s) | Subnautica idles; a longer load only stalls the link | SE's loading screen |
| SE world load, unload or change | `worldEpoch` bump in C5 | Subnautica clears mirrors, proxies and statics state; SE re-validates the site | SE's loading screen |
| GPU driver reset (TDR) | both devices lost | SE shows its own driver-crash message; no relaunch in the same boot while any game process lingers | crash; SE autosave is the safety net |

- **No automatic Subnautica relaunch in v1.** A force-quit followed by a quick relaunch is the documented "zombie"
 wedge on this machine (a `javaw` once wedged in the AMD driver). The player restarts the renderer from the launcher;
 it relaunches only after the old process and its crash handler are gone and a 20 s cool-down (S8.T-L1d). SE keeps
 the section after PeerExited, so the relaunch preflight accepts a present section whose recorded `snPid` is dead
 (CONTRACT §7 "Relaunch"). Built (S8): `launcher.py restart-renderer`; the `run` exit wait offers a kill
 by exact PID after 30 s of hang (SE gone and Subnautica still there, or a heartbeat 30 s old) and never performs it
 (CONTRACT §7 "Exit wait"); both plugins log memory every second (SE `sample` row: `workingSetMB`, `privateMB`,
 `gcHeapMB`, `gc0-2`, `memoryLoadPct`, `commitPct`; Subnautica `DE.S2 sec`: `mem{ws priv mono gc load commit}`) for S8.N3.
- Nobody force-kills a game during D3D init; normal
 closing is `CloseMainWindow` and waiting.

### 5.15 Saves
- **SE owns all persistent state**: normal saves and SE autosave. What the bridge leaves in an SE save:
 - the Water Mod's sea for `DE_4546B`, in the Water Mod's own save variable (section 5.5);
 - the tile entity ids, as landing-gear and magnetic-plate references (`AttachedEntityId`); they are deterministic
 (section 5.6), so a gear-locked ship stays locked across reloads; without the plugin those gears unlock on load;
 - never a tile, a voxel storage, a statics host or a provider id: they have `Save = false`, and SE's save code
 skips non-saved entities and their voxel storages (VERIFIED `MyEntities.Save`, `MyVoxelMaps.GetVoxelMapsArray`).
- **Fail-safe, never fail-open.** If the plugin runs in a world holding `DE_4546B` but the tiles cannot be created
 (manifest mismatch, provider registration failure, id collision): grids inside the site footprint (`ρ∞ ≤ 2,100 m`,
 `y < 0`) are held (velocities cleared each tick and natural gravity disabled on them; HYPOTHESIS mechanism), saving
 and autosave are blocked by a prefix on `MySession.Save(out MySessionSnapshot, …)` (the other overload calls it;
 VERIFIED) with the HUD warning "DeepEngineers terrain missing:
 saving disabled", and the abyss crush is suspended for grids flagged as having descended while the terrain was
 missing. The display bridge's version pins never trigger this: they only switch off the Subnautica picture
 (section 5.6). S8.N8 tests it with a seeded failure.
- **Without the plugin** (SE started from Steam without Pulsar, or the plugin disabled), and equally when the
 activation gate refuses a DE world (multiplayer, more than one `DE_4546B`; section 1), nothing can protect the
 world: grids resting on the crater sink towards the −3,000 m floor and the next autosave keeps it. The README says
 never to open a DE world without the plugin and the world mod
 launch it" (pin table at the top). Option for v1.x (HYPOTHESIS): a whitelisted ModAPI script in the world mod
 that holds grids when the plugin's handshake is absent.
- The planet itself depends on the world mod (`DE_4546B` definition). Removing the world mod from a world that holds
 the planet is not supported (HYPOTHESIS: SE cannot rebuild a planet without its generator definition; S4.G2-E5
 records what happens on a copy).
- **Subnautica's host save is never saved** (section 5.10). Its contents only provide a loaded scene, streamer and
 eco-regions. Subnautica's story timeline restarts every session (risk R7.4).
- Bridge-owned files: configuration (Pulsar and BepInEx plugin config files), logs, `restore.json` (original values of
 settings the bridge touches, CONTRACT §1.1) and derived caches under `%LOCALAPPDATA%\DeepEngineers\` (mesh and
 texture caches, statics census, captures). All are disposable except `restore.json` while a restore is pending.

### 5.16 S7: interiors, lights, effects, statics, the skinned astronaut

Records, flags, pins and member signatures: CONTRACT §3.2 and §5.4 "S7 conventions" (`ext s7 look 1`,
`0x1f9b9b4a876a97f9`; layout 1.0 unchanged). Checks: ROADMAP S7 (S7.N1–N11, WM-5, M1–M19). Every feature is keyed
by entity id (grid, subgrid, character, batch), never by "the player" (v3).

**
 - *Data flow.* SE, each tick: the mirrored grids whose box contains the camera → the camera's cell → the grid's gas
 system → the room at that cell (`GetOxygenRoomForCubeGridPosition`, `GetSafeOxygenBlock` inside a block) →
 `IsAirtight` → C1 flag bit 3 and the C1 interior extension (room grid, index, oxygen, `Processing`, `Breached`,
 `CharacterInRoom`). The answer is held while the gas system recomputes (`IsProcessingData`), so a block change never
 flickers it. RoomSet (C4) carries every airtight room's air cells as boxes, sent when the grid's recompute completes
 (`OnProcessingDataComplete`) and the boxes changed. SE's swim latch stays off while the character's eye cell is in an
 airtight room, so the character walks. Subnautica: `IsInsideWalkable` true for the ghost (fog `_CameraInside`,
 no creature attack on the pilot, interior ambience), `Inside` chunks with the base-interior sky, `Boundary` chunks
 (walls, windows) with it while the camera is in that grid's room, `WaterClipProxy` boxes for rooms that straddle the
 sea surface.
 - *Budgets.* SE ≤ 0.05 ms per tick (a few lookups), RoomSet ≤ 0.5 ms per tick (resumable segmentation), Subnautica:
 re-classification only for cells whose class changed.
 - *Failure modes.* World settings off: `DE.Interior OFF pressurization disabled` once, flag false. Base-interior sky
 or clip material missing: biome sky / no clip, logged once. The fog through windows and the Water Mod's own
 behaviour in a dry room are HYPOTHESIS (CONTRACT O25, O26; S7.N1, N1c). Fallback if `IsInsideWalkable` misbehaves:
 narrow it to `WaterscapeVolumeOnCamera` (§5.11).
- **Lights.
 heat vents and lighting components; beacons; the character light) and writes up to 64 rows nearest the camera within
 Subnautica sets a pool of Unity lights every frame through

 the Seamoth cabin light (S7. ≤ 0.1 ms (SE), ≤ 0.2 ms (Subnautica). Drawn above and under water.
 * S7p (P10):* × a per-kind scale from the row's flags (suit lamp 1/25, spotlights 1/8, point lights 1: SE's
 eye adaptation has no Subnautica counterpart), live `de.s7 lightScale`, env `DEEPENGINEERS_SN_LIGHT_SCALE`.
**
 - *Data flow.* SE entry points raise events: thrusters and tools polled per tick (sustains with a stable key),
 `MyGunBase.Shoot` (muzzle, bullet streak), `MyProjectile.OnHit` (impact), `MyExplosions.AddExplosion` (explosion),
 `MyGridPhysics.PlayCollisionParticlesInternal` (grid impact), `MyGridGasSystem.AddDepressurizationEffects` (hull
 break), a surface-crossing test (splash). They go into a fixed per-tick buffer with per-type caps (128 per tick in
 all, dropped and counted beyond, never a growing queue), get consecutive sequence numbers and stay 8 ticks in the
 C2 event window, attached to the mirrored grid when there is one. Subnautica reads the window with C2, applies each
 sequence number once, keeps sustains alive by key and fades them when their rows stop, and draws everything with
 its own prefabs and FMOD events found at run time (the Seamoth's bubbles and engine loop, the repair tool, the Prawn
 drill, the crashfish detonation, the surface-type impact effects, the construction splash), under water with
 sound, above water as a light only. Nothing is shipped; a missing source falls back to bubbles or nothing. SE keeps
 the gameplay: effects never damage, push or save anything in Subnautica (prefabs with colliders, rigidbodies or
 damage components are refused).
 - *Budgets.* SE ≤ 0.2 ms per tick for the sources and the window; C2 grows by ≤ 64 KiB of rows per tick (in the S5
 0.3 ms C1 + C2 target); Subnautica ≤ 0.5 ms per frame with pooled effects and ≤ 2 instantiations per frame.
 - *Failure modes.* Over a cap: dropped, counted (`eventsDropped`, `s7.fx`); Subnautica more than 8 ticks behind:
 `eventsMissed`; a missing Harmony target: that event kind off; a lost Stop row: the sustain fades after 500 ms.
 - *S7p effects-under.* The row's free
 bits carry what the S7 looks lacked: a thruster's own flame colour (SE's lerp idle → full over strength / MAX_THRUST)
 and length scale, a shot's hit distance, the weapon's muzzle scale, a drill's contact point and class. Under water
 Subnautica composes the looks from its own particle systems at run time (a donor per role — glow, smoke, spark, dust,
 shimmer — cloned and stripped to its ParticleSystem, then given the recipe): a flame cone per thruster flame, a steam
 plume where a flame's heat reaches the sea surface (above or below), a layered explosion around the Seamoth's own
 destruction effect, a muzzle flash, a tracer flying muzzle → hit and a fine bubble streak instead of the diver's breath
 bubbles, a held alpha-blended dust plume with sparks and a capped light at the drill. Every look is capped (drop
 counters in `s7p.fx`) and scaled live (`de.s7p fx <type> <scale>`, env `DEEPENGINEERS_S7P_FX`); above water the
 scene-above route draws (the S7p looks there only with the `above` switch).
- **Statics into SE (the second terrain path).** Subnautica scans the loaded batches near the camera for static
 colliders (entity-type filter, census allow-list; never creatures, frozen or not, nor our own proxies), sends
 primitives per 160 m batch with epochs (C12), and in the census session also writes them to a cache. SE builds one
 non-saved static host per batch and epoch (`HkStaticCompoundShape`, layer 13) while a grid or character is within
 300 m (closed beyond 350 m), from live messages or the cache. Budgets: Subnautica ≤ 1 ms per frame (resumable scan),
 SE ≤ 1 ms per tick (C12 drain, ≤ 256 shapes added per tick, then one bake). Failure modes: a Havok error leaves that
 batch without a host (logged); an improper axis triple drops the primitive (counted); a full ring re-queues the batch.
 The census tool (`tools/sn-octrees/sn_prefab_colliders.py --census`) counts every collider of every prefab the
 BatchObjectsCache references and writes the allow-list (derived, `%LOCALAPPDATA%`, never shipped).
- **Skinned astronaut.** S5 already skins SE's own astronaut mesh from SE's bones (`canon1`, 78 rows; every weighted
 bone is canonical). S7 sends every model bone (`ExtraBones`: 81 rows, the 3 non-canonical bones after the 78) so
 `boneCount == Model.Bones.Length` holds for every character model and attachment bones are posed too; the S5
 sources stay: SE's bones by default, path G retarget while swimming and as the fallback, S5a's diver arms in first
 person. Check: bone world positions within 1 cm over a 60-frame walk, no skin tearing (S7.N6).
- **Audio polish.** §5.3 A1–A6.
- **S7 review fixes.** A room exists in SE only on a grid
 with an Air Vent (`MyGridGasSystem.s3` places a room fixture
 that has one (with a battery, a door, an Interior Light and three spotlights). The inside-a-block fallback applies only
 in a pressurized cockpit. The SE muffle runs only in realistic-sound worlds (SE's own effect path); Arcade worlds stay
 unfiltered and Subnautica's mix carries the dive. The statics scan uses the player's collision layers (the wreck walls
 on DiveReel), the census list `allowWorldForces` lets the kinematic explorable wrecks in, the dedup key carries size
 and rotation, and a growing batch is resent only after 2 stable passes. Surface splashes play `Vehicle.splashSound`
 (the one exception to the event-sound rule). Concave mesh colliders stay closed boxes in v1 (the two Precursor
 aquarium pipes).
- **S7p scene-above.** *Above water under Site*
 SE's scene skip removes SE's whole 3D pass, particles included (its billboards and GPU particles live inside
 `DrawGameScene`), so SE's own bullets, flames and blasts can never show over Subnautica's picture: the event stream is
 played by Subnautica above the waterline too (a second reader on the C2 window, seven world-space particle systems,
 route `de.s7p {above}`), and SE's missiles in flight are mirrored as S5 floating entities with their own model (the
 trail drawn by Subnautica). *Subparts*: blocks whose subparts SE turns in the render thread only (drill head, grinder
 blade, propeller, turbine, fans, H2 rotor) are exported Dynamic and posed with a spin integrated on SE from the same
 speed; the held tool's own subparts (the hand drill's bit) go as character parts. *Projections*: a projector's
 blueprint (an SE preview grid) rides the S5 grid stream with a Projection bit; blocks SE hides (built, or not
 buildable) are never exported; Subnautica draws it with one translucent hologram material; it never gets a collision
 skin, rooms or lights. Budgets: ≤ 24 missiles, ≤ 4 projections of ≤ 20,000 blocks, ≤ 1,024 spinners, ≤ 768 particles
 per frame; rows `s7p.scene` (SE) and `s7p.above` (Subnautica).

### 5.17 S8a: the terrain converter: drilling, the store, the seabed beyond the crater, the treadmill

Members with exact signatures, rules, budgets and the fail-safe: CONTRACT §3.3; records and the pin: CONTRACT §5.4
"S8a conventions" (`ext s8a terrain 1`, `0x7f480768c4cb872f` (ext s8a terrain 2, Stage C; was `0x3d52f1105b92a172`); layout 1.0 unchanged). Checks: ROADMAP "### S8a"
(S8a.C1–C3, N1–N10, M1–M14, T-P1r). Everything is keyed by tile cell, batch id or chart, never by "the player" (v3).

- **Decision.** One converter turns SE voxel content into Subnautica octree batches; SE owns every terrain change and
 is the only writer of converted batches; Subnautica draws them through its own loader. Subnautica 1.22.83031 has no
 live voxel edit (its `WorldStreaming` octrees are native arrays read from the install's batch files; the
 `Voxeland`/`compactTrees` path is legacy and empty) and no per-save terrain cache (none is read; save upgrades delete
 any), so a change reaches Subnautica as a new version of a batch file in our store plus a reload of that batch and of
 the clipmap cells over the change. The store is ours (`%LOCALAPPDATA%\DeepEngineers\terrain\`), never an install
 folder and never `SNAppData`, whose save slots sit inside the install folder on Steam.
- **Data flow, drilling (S8a-1).** SE tool, explosion or voxel hand → S4's guard lets it through on a tile → SE's octree
 storage writes its own leaves over the provider (physics, ore and yields stay SE's) → `RangeChanged` (any thread) →
 dirty 16³ overlay cells, the range mirrored into overlapping tiles → the drill worker (coalesced per batch) reads SE's
 content around the change, recomputes only the Subnautica voxels whose stencil meets it, splices them into the
 batch's newest version (store, else the install file), writes version + 1 and queues a notice → C2 S8a rows (repeated
 until acknowledged) → Subnautica validates the file (size, FNV), schedules on its streaming thread `BatchOctrees.Reload`
 (its `GetPath` postfix now answers the new version) and, once the batch is loaded, `ClipmapCell.Reload` for the level
 0–3 cells over the box → C7 `ackSeq`, `appliedSeq`. SE's save carries the overlay as `DE_S8a_overlay.vx2` through its
 own voxel snapshot list (SE deletes any other top-level file it did not stage); the load writes it back into the
 tiles and rebuilds the store's drill layer if its hash differs.
- **Data flow, the seabed (S8a-2).** Our sculpt tool writes the planet's heightmaps and material maps (offline, our own
 data; the crater kept at −3,000 m so the provider tiles stay alone there) → SE generates its planet as always →
 around the camera beyond the crater box, SE's window worker samples the planet's storage under the current chart and
 writes window batches into the store (water batches as "cleared", rock batches as tiny solid files, surface batches
 converted), nearest first with one batch of look-ahead → notices → Subnautica's octree ring, whose centre clamp the
 constructor prefix widened to the window universe, loads them through the loader prefix (all 125 octrees per file;
 crater ids stay the install's or the drill layer's). Planet edits regenerate the batches they touch. SE sends the
 camera's biome; Subnautica's `GetBiome` answers it beyond the crater; a sparse spawn table places Subnautica's own
 fish and plants from the served files (transient, never saved).
- **The treadmill (charts).** Chart 0 is the site map; with `TreadmillOn`, past site ρ∞ 4,800 m and then whenever the
 camera leaves a 3,200–7,040 m box (or drifts 3,840 m from the chart anchor), SE builds the next chart at the camera's
 sea point with the carried frame and moves the camera's SN position by whole batches; every mirrored thing follows
 because it is mapped through `SiteBinding.Map` each tick; the only change near the camera is the old chart's
 distortion (≤ 1.07e-3 of the distance). Subnautica snaps (never interpolates) across a chart change; SE ignores
 Subnautica-positioned input while Subnautica still reports the old chart. Above water a treadmill chart uses the
 `Waterline` picture policy (the crater's landmarks would show at wrong places). A step over terrain re-streams like a
 teleport (SE's picture covers it); in open water `NearEmpty` keeps Subnautica's picture; while the window lags near
 SE's surface, `NearPending` makes Subnautica report "not built", so SE's own picture shows SE's terrain (the fallback:
 never invisible ground).
- **Budgets.** SE game thread ≤ 20 µs per edit hook call, save ≤ 50 ms per 1,000 overlay cells; two SE worker threads
 (drill, window) below normal; window ≤ 250 ms p95 per surface batch; ≤ 64 new notices per tick; Subnautica ≤ 8 batch
 reloads in flight and ≤ 0.5 ms main-thread work per frame; store ≤ 2 GB; a drill edit visible in Subnautica P95 ≤
 1.0 s.
- **Failure modes and the fail-safe.** A batch that cannot be written (store refused, disk full): that layer stops,
 SE keeps its own edits and saves them, Subnautica keeps what it serves. A batch that cannot be loaded (bad size or
 hash, parse error): never served; the drill layer falls back to the previous version or the install file (never an
 empty crater batch), the window to water with "not built" near SE's surface. A reload that hangs: retried at 5 s,
 given up at 10 s with a counted failure (the feed never stalls). A full notice queue: a resync from the manifests. A
 broken overlay: refused once, its bytes carried unchanged through later saves, this session's edits not persisted.
 Without the plugin: SE ignores the `.vx2`, Subnautica never reads the store.
- **Evidence.** The converter's writer rules (layout, interior values, collapse) were checked on 152 install batches
 (1,264,598 interior nodes) and 150 octrees round-tripped byte for byte (contract time, read-only; ROADMAP "### S8a").
 HYPOTHESIS until the real run: the clipmap parent level covers a rebuilding cell, the latency budget, the
 treadmill's picture behaviour in open water, the material table's look.

---
## 6. What every player must own and install

| Item | Version | Source | Licence / terms | Notes |
|---|---|---|---|---|
| Windows 11 x64 | tested on build 22000 | | | borderless windows only;4); after a feature update the "optimizations for windowed games" may need a per-app opt-out for `Legacy.exe` (else display fallback A′, chosen by measurement) |
| Steam | current | Valve | | must be running for both games; both games set to "only update when I launch it" |
| Space Engineers (SE1) | 1.210.014 | Steam 244850 | Keen EULA | must be owned |
| Subnautica | build 20241558 (1.22.83031) | Steam 264710 | Unknown Worlds EULA | must be owned; Subnautica 2 is not supported; if its window opens on another monitor, the player sets its Display option to the primary monitor once (section 5.1, R2.8) |
| Pulsar | v2.4.3, `Pulsar-v2.4.3-win-x64.zip`, 31,380,992 B, SHA-256 `14B4EEC93EE4D2C223A712944646F657D372C16BECA4406843E4198F2E93FF8B` | GitHub SpaceGT/Pulsar | MIT | portable folder outside both games; launch `Legacy.exe` with `-noUpdate`; our plugin enabled by the profile entry `<string>DeepEngineers.SE.dll</string>` under `<Local>` in `Legacy\Profiles\Current. `build.ps1` writes it) |
| Tobey's BepInEx Pack for Subnautica | 5.4.23-pack.3.1.1 (BepInEx 5.4.23.5), 3,835,534 B, SHA-256 `21A1FD6EC22253A6CF05CCF1F6407F6055C95971A7DDA7545B33481069A4DD3E` | GitHub toebeann/BepInEx.Subnautica | BepInEx: LGPL-2.1 (REPORTED, not checked here) | extracted into the Subnautica folder; exact uninstall list in `docs/install/subnautica-bepinex-files.txt` |
| Nautilus | 1.0.0-pre.54 | GitHub SubnauticaModding/Nautilus | not checked here | optional: installed on this PC, **not used** by DeepEngineers v1 (shaders load through Addressables, commands through the game's `DevConsole`); a soft `BepInDependency` only orders loading when it is present |
| Water Mod | 3.23 (API 22) | Steam Workshop 2200451495 | no licence file | subscription only; depends on Text HUD API (758597413) |
| Python | 3.10 or later | python.org | PSF | runs `launcher.py` (stdlib only) |
| DeepEngineers SE plugin | ours | this repository (release DLLs) | ours | `<Pulsar>\Legacy\Local\DeepEngineers.SE\plugin.dll` + `DeepEngineers.Wire.dll` (Pulsar id `DeepEngineers.SE.dll`) |
| DeepEngineers SN plugin | ours | this repository (release DLLs) | ours | `<Subnautica>\BepInEx\plugins\DeepEngineers\DeepEngineers.SN.dll` + `DeepEngineers.Wire.dll` (listed in `docs/install/deepengineers-files.txt`, S0) |
| DeepEngineers.World world mod | ours (data only) | this repository | ours | `%APPDATA%\SpaceEngineers\Mods\DeepEngineers.World\` (could be a Workshop world mod later: it contains no code and no game data) |
| Subnautica host save | Freedom, "DeepEngineers-Host" | created once by the player | | never saved by the bridge; lives in `<Subnautica>\SNAppData\SavedGames` (Steam Cloud synced) |
| A DE world | created by the player in SE | | | mods Water Mod, Text HUD API and `DeepEngineers.World`; world settings of section 5.5 set at creation (`EnableSunRotation` true, `SunRotationIntervalMinutes` 20, sun base direction (0, 0, 1) in the environment and the sector weather component, `EnableOxygen` and `EnableOxygenPressurization` true, Survival with `DestructibleBlocks` for combat and the crush zone); `DE_4546B` spawned once with both mods loaded (that spawn seeds the sea) |
| Development only (not needed to play) |.NET SDK (8 or later), ilspycmd (local decompiles stay in the scratchpad), ffmpeg (`um win setup`, showcase video), UnityExplorer 4.13.6 BepInEx5 Mono build (optional inspector, not downloaded yet) | | | |

Release channels (v1): the SE plugin ships as a DLL in a GitHub release, installed as a Pulsar Local plugin; it can
never ship on the Steam Workshop (plugins are not mods); PluginHub and Pulsar's DevFolder
compilation are later options (DevFolder untested). The SN plugin ships as a GitHub release zip for the BepInEx
plugins folder. The world mod ships as a local mod folder (a Workshop world mod is possible: data only). The
launcher ships as `launcher.py`.

**S9 installer.** All of the above ships as one folder built by `tools/release/make_release.py`
(ROADMAP S9 "Installer"): `Install.cmd` installs or repairs from it, `Play.cmd` plays, `Uninstall.cmd` removes what the
install manifest recorded. Two rows of the table above change for strangers: the **Subnautica host save** is any
Freedom save the player made (the installer finds it in `SNAppData\SavedGames\slot*\gameinfo.json` and the release
launcher names it to the plugin through `DEEPENGINEERS_SN_HOST_SESSION`/`_SLOT`; the plugin's built-in default is
this PC's session), and **a DE world** is copied from the release's template world (`tools/release/world-template`,
derived read-only from "DE Lab S4" by `make_template_world.py`: the `DE_4546B` entity with its pristine storage file,
because `MyPlanet.Init` refuses a planet whose `.vx2` is missing, the planet's Water Mod sea in `Sandbox.sbc`'s
`JWater2` variable, the section 5.5 settings in both `Sandbox.sbc` and `Sandbox_config.sbc` (SE takes settings and
mods from the latter), Creative, one player identity that SE hands to the loading Steam user, no grid). Its first
load shows SE's "convert this world from XML" box (no binary sector cache is shipped). The release launcher also
passes `DEEPENG_SUBNAUTICA`, which the SE terrain gate reads before its development-PC default
(`SnSourceManifest.ResolveSubnauticaDir`). Pulsar and the BepInEx pack are downloaded by the installer from their
pinned release URLs and checked by SHA-256 (`src/DeepEngineers.Launcher/de_pins.py`); nothing third-party ships.
Integration: the release folder's plugins are the `DEEPENG_RELEASE` build only
(`make_release.py --flavour release`; `check_release.py` pins them; the installer warns on any other flavour), and
`%LOCALAPPDATA%\DeepEngineers` is shared by a development setup and an installed release on one PC: uninstall keeps
whatever that folder held before the first install (the manifest's `localBefore`).

Hardware reference (the only PC tested): Ryzen 7 3700X (8c/16t), RX 6900 XT 16 GB (driver 32.0.21045.1000), 32 GB
RAM, 2560×1440 at 165 Hz, plus a secondary 1920×1080 monitor. Minimum requirements are unknown.

Licensing and legal notes (not legal advice; SE clauses read in the local `Bin64\eula.txt`, VERIFIED):
- **SE EULA 1.4** allows non-commercial modification in relation to the games; **1.5** requires crediting Keen and "a
 prominent notice stating that you have modified the Software" (the NOTICE and README carry it); **1.6** forbids
 giving modified software to people who do not own the game, so we ship only our own code and every player owns SE;
 **2.1** limits use to the operation of a game; **2.2.3** forbids permitting the software "to be combined with, or
 become incorporated in, any other programs": this design runs SE next to Subnautica, which draws SE's models and
 **2.2.4** forbids decompiling except for
 interoperability (2.2.4.1), on condition that the results are "not disclosed or communicated without our prior
 written consent to any third party" (2.2.4.2) and not used for substantially similar software (2.2.4.3).
- Keeping decompiled code out of the repository is necessary but not sufficient: 2.2.4.2 plausibly covers the
 decompilation findings written in these documents, `docs/recon` and the planned public field notes (internal call
 orders, `MyFakes` flags, render behaviour, damage formulas).
 S9): keep the docs private, strip internal-behaviour detail from public notes, or ask Keen for consent.
- **Keen's plugin position** (REPORTED): plugins are unsupported and not permitted on Keen's official servers. The
 plugin is for single player; it is inert in multiplayer (section 1).
- **Subnautica**: no Unknown Worlds mod policy for Subnautica 1 was found (REPORTED: tolerated, unsupported). Never
 redistribute game files.
- **Water Mod**: no licence file in the mod folder. We depend on it through the Workshop subscription and its message
 API (handler 50271). Our API client is a clean reimplementation of the handshake (handler id, delegate dictionary,
 `VerifyVersion`) written from behaviour, never a copy of the mod's ` (which points to
 github.com/jakarianstudios/SE-Water; that repository's licence is REPORTED-unchecked). Our only uses of its
 internals are the read-only `WaterData.Version` string and the underwater sound mute (section 5.3); the mute is
 enabled only when the version is "3.23" and its patch targets resolve, otherwise the Water Mod's sound stays on
 underwater (logged). Reading its source to describe behaviour is interop research; nothing is copied.
- **Vehicle Framework** (GPL-3.0) and **Nitrox** (GPL-3.0): read for reference only; nothing copied. **ValCraft** (no
 licence file): read only, nothing reused. **SkyCraft, Killcraft** (MIT) and the MIT parts of **LibertyCraft**
 (protocol, fabric, tools; its GPL-3.0 `asi/` part was not read): mechanisms re-implemented in our own code; credit
 in a NOTICE file.
- **No game assets are shipped.** SE models and textures are read from the player's own SE install at run time
 (`MyModel`, DDS by path); Subnautica's terrain is read from the player's own install. Derived caches live only in
 `%LOCALAPPDATA%\DeepEngineers\` and are never committed or published. The release gate is our own lint
 `tools/release/check_release.py`

 `um publish check` runs as an extra lint, once with each game install: on its own it fails
 only on byte-identical game files and secrets, reports decompiler fingerprints as warnings, and cannot see derived
 caches (VERIFIED `um/publish.py`). **S9, built:** the lint also covers shipped `.py` files and scans
 every shipped file for machine paths, Steam ids (text, UTF-16, binary) and decompiler markers; the allowlist pins the
 release-flavour DLLs (`DEEPENG_RELEASE`) to their build outputs; `tools/release/strip_decomp_refs.py` prepares a
 source copy without decompiled-line citations (ROADMAP S9 "Built").
- The world mod's heightmaps are our own all-zero images.
- The repository says it was built with AI tool (README, NOTICE).

---

## 7. Non-goals for v1, and v2 ideas

| Not in v1 | What the player sees instead |
|---|---|
| Multiplayer | single player only; the plugin is inert in multiplayer; other players' characters are not mirrored |
| SE 3D-only feedback beyond grids and the character: block placement ghost and highlights (`MyCubeBuilder`), LCD text and offscreen textures (stale while see-through), welding and grinding particles, first-person hands and tools beyond the skinned body | underwater these are invisible in see-through mode; HUD sprites (crosshair, markers, toolbar, hints) are SE's own and visible |
| Weather | no weather generators on `DE_4546B` |
| The "void treadmill" (keeping Subnautica in view beyond 7,800 m) | FAR zone shows SE's Water Mod fog |
| Hull-pressure gameplay (Subnautica-like depth progression, hull modules) | Water Mod crush off on the dive planet; only the abyss rule at −2,000 m and the floor guard at −2,500 m |
| Subnautica 2 (UE5) | not supported; the Subnautica side sits behind a "visual host" boundary (glossary) so another host could replace it |
| Terrain edits (drilling, explosions on the crater) | blocked on the tiles |
| Creature ram impulses, reaper grabs, `AttackCyclops` behaviour on SE ships | creatures are pushed out of hulls; bites, ampeel shocks and crashfish blasts only (CONTRACT §4.1) |
| Subnautica's own gameplay systems (PDA, crafting, Seamoth, base building, survival stats) | unused; the ghost player is frozen and its UI hidden |
| Subnautica's above-water objects drawn by SE (Aurora hull, island bases) | SE shows its own voxel islands above water; those objects are seen only from below |
| Story events control (Aurora explosion, Sunbeam, precursor gun) | Subnautica's own timeline, restarted each session (R7.4) |
| Several dive sites, planets other than `DE_4546B` | one site |
| Mesh and texture disk caches | stretch item (v1.x), outside the definition of done; v1 rebuilds from SE's files each session |

v2 ideas: the void treadmill; hull-pressure gameplay; SE weather mapped to Subnautica sun dimming
(`VFXWeatherManager.sunScalar`); mirrored block placement ghost and LCDs; creature ram impulses into SE physics;
compensated Subnautica camera for the crosshair offset; small SE waves with the camera's wave offset sent to
Subnautica; sub-frames per 20 m chunk for very large static bases; terrain edits mirrored into Subnautica; a second
visual host behind the same boundary; a world-mod ModAPI guard that holds grids when the plugin is absent.

---

## 8. Risks, detection tests and fallbacks

The single risk register is ROADMAP §7 (likelihood, impact, labels). This table restates the risks that shape the
architecture, with the register's ids.

| Id | Risk | Detection test | Fallback |
|---|---|---|---|
| R2.1 | SE draws something opaque full screen every frame, or its scene leaves alpha < 1 where it should not | **S2.T-D1**: F8 toggles see-through 50 times; SE stays foreground 50/50; with no menu open < 15 % of backbuffer pixels have alpha > 0 (the HUD footprint, 10.2 % measured on the real runs at 2560×1440, is opaque by design; the ROADMAP's 2 % was a guess); with the scene drawn < 0.1 % have alpha < 1; every menu readable; seeded failure S2.M1 | filter the offending sprite texture; else A′; else C |
| R2.2 | Unity re-activates its window or drops `WS_EX_NOACTIVATE`/`WS_EX_TOOLWINDOW` (resolution change, `DisplayManager`, Steam overlay); the taskbar button survives a runtime restyle | S2.T-D1 foreground and z-order log; S2.N9 (no taskbar or Alt+Tab entry, a taskbar click leaves SE foreground) | hide/restyle/show, `ITaskbarList:DeleteTab`; re-apply on display changes; A′ |
| R2.3 | A fully covered Subnautica stalls in `Present` (measured 5 s `Responding = False` blip), so Prime warms nothing and `snReady` never holds | **S2.N8** (first S2 gate), S5.T-P1, S8.N3 | margins on in Prime (section 4.4); alternatives (a)–(c); stalls are recoverable (PeerStalled) |
| R2.4 | A Windows feature update flip-upgrades SE's swap chain (21H2 is out of servicing) | start-up composed-pixel measurement (S8.N7); S2.T-D0 re-run after any update | per-app opt-out for `Legacy.exe`; A′ automatically |
| R2.5 | Translucent HUD panels wrong over Subnautica: alpha ≈ a² and colour too bright (sRGB-encoded premultiplied mismatch) | S2.T-D0 translucent case
| R2.8 | Subnautica's window size or monitor not controllable (launch flags ignored, F7; second monitor) | rectangle compare every second (C5 `seRect`, C7 `snRect`) | `-monitor 1`; `Screen.SetResolution` with `FullScreenWindow`
| R2.9 | Look latency underwater too high (60 Hz orientation at 165 Hz display) | **S2.T-I1** and S1.N2 (target ≤ 25 ms added p95) | orientation extrapolation; Subnautica cap at 120 |
| R2.10 | The z-order re-assert does not raise Subnautica over a third-party window | S2.N5 with Notepad between the games; `zOrderOk` | `AllowSetForegroundWindow` + retries; stay on SE's view |
| R2.11 | SE does not get the foreground at start | S2.N10 (also with `ForegroundLockTimeout` at the Windows default) | SE takes it itself; HUD hint |
| R4.2 | 1024³ provider tiles cost too much memory or stutter (dense `HkUniformGridShape`, `ReadRange` in the Havok step) | **S4.A-T1** (3): tile RAM ≤ 1.5 GB, `ReadRange` p99 ≤ 2 ms, < 1 ms per 8 m physics cell, frames > 5 ms ≤ 1 % | 2048³ tiles (2×2×2); plan B planet-provider splice |
| R4.1 | Subnautica draws no faces where its data stops (plug assumption) | **S4.G2-E1**: warp to (880, −700, −816) and look up; expect a flat ceiling at 640 ± 2 m depth | depth rule under the crater footprint, or Subnautica-side cap meshes |
| R4.3 | SE and Subnautica surfaces disagree (mesher difference, half-voxel convention) | **S4.A-T0/A-T0b** offline; **S4.A-T1** P95 ≤ 0.25 m vs oracle heights; **S4.A-T2** cross-game raycasts P95 ≤ 0.3 m | tune the content mapping; widen budgets; diagnostic runtime triangle stream |
| R4.6 | The sea changes in game (`/wradius`, `/wcrush`) or differs from `WaterConfig.xml` (the save holds the sea) | site validation every 10 s; S1.G2-E3 | display bridge refuses; `/de site repair` |
| R4.8 | Tiles missing in a DE world (update, moved library, no Pulsar) and grids sink | S8.N8 | fail-safe (section 5.15); update only on launch; README warning |
| R4.9 | A gear-locked ship is unlocked after a reload | S4.G2-E3b | deterministic tile ids |
| R3.5 | Unity 2019.4 does not re-skew a rotating child under the `(s, 1, s)` Frame | **S3.G1-E2**: matrix deviation < 1e-4 m over 360° at s = 1.0284 and 1.0345 | per-chunk rigid frames with `K` baked into vertices |
| R6.5 | Bites never reach proxies (patch, line of sight, eco-targets, physics rate) | **S6.B-T1** (2): forwarded `originalDamage` 30/80 with proxy health unchanged; seeded failure S6.M2 | more anchors, smaller sections; `SubControl` path |
| R6.7 | Proxies move at Subnautica's default 10 Hz physics and lag the hull | S6.N7 (`Time.fixedDeltaTime` = 0.02 while a moving proxy is visible) | 10 Hz with per-0.1 s thresholds |
| R1.6 | Host save in the wrong mode (Creative → `NoAggression`) | startup assertion `GameModeUtils.IsInvisible == false` (S1.N5; real run PASS, S1.M3 red on a Creative copy) | combat disabled with a log line; player recreates a Freedom save |
| R1.8 | Subnautica's own UI or PDA voice shows through or plays | S1.N9 (real run PASS: no HUD in the captures; S1.M7 red); the sink rows still hold by construction until S2's `de.testUi` trigger | hide phase, PDA suppression |
| R1.3 | Streaming after a camera jump delays the dive | S1.N3 (real run: near field ±16 m built in < 1 s, settled ≈ 1.3 s up to 1 km, ≈ 3.1 s at 2 km), S2.N7 | Prime lead 2 s kept (§4.2); `snReady` gating keeps SE's view until built |
| R3.4 | `MyModel` loading off the update thread is unsafe (`MyFileSystem`, static dictionaries) | S5.N5: 200 distinct models, no exception | single loader thread (already), else main-thread time slicing |
| R1.7 | Crosshair offset noticeable at depth | **S1.G1-E5**: projected-pixel comparison against `(s − 1)/2 · sin 2α` (≈ 4.3 px at 500 m, ≈ 14.4 px at 1,657 m, ≈ 17.4 px at 2,000 m at 45°) | compensated Subnautica camera (`worldToCameraMatrix` with S⁻¹) |
| R0.6 | Game or loader update breaks hooks | version pins at start (SE runtime `GameVersion`, Subnautica version, Water Mod version string and delegates, layout hash); every patch target self-checked | display bridge refuses; terrain keeps its own gate; update only on launch |
| R5.1, R5.3 | Performance: SE simulation ratio drops, RAM pressure, Track or Prime cost | **S5.T-P1**: each state × 5 min; ratio ≥ 0.98, Track within 5 % of solo, Subnautica p99 ≤ 16.7 ms, commit < 85 % | clipmap quality Low, 120 fps cap, smaller budgets, affinity experiment (Subnautica on CCX1, mask 0xFF00) |
| R7.1 | `IsInsideWalkable` patch side effects (creature fear, oxygen) | S7.N2: every reader logged through an inside/outside cycle | narrow the patch to the fog consumer |
| R1.2 | `OutOfBoundsWarp` on the ghost Player or proxies | check the Player prefab (UnityPy); FAR limit keeps ±7,900 m | disable the component on the ghost |
| R7.4 | Subnautica story events (Aurora explosion, Sunbeam) fire during sessions, with time driven forward | observe in the first long session | host save created after those events, or suppress them |
| R4.7 | Respawn on `DE_4546B` lands on the floor | scenario test: die on the planet | Water Mod respawn pods or a plugin spawn on the sea surface (O5) → **the lifepod rule**: a post-death character that comes free under the waterline inside the site is lifted to the lifepod point at the surface (CONTRACT §7 Respawn placement) |
| R5.4 | Two-sided glass and alpha-masked SE materials render one-sided | S3 visual capture | duplicated reversed faces, or a cloned two-sided material |
| R6.6 | Damage applied twice, lost or misparsed after a restart | event log on both sides, dedup counters, record validation; seeded failures S6.M3 and S0.M7 | dedup by (generation, sequence); ring re-sync; overflow counter in C7 |
| R8.4 | GPU driver reset or exclusive-fullscreen hang (AMD) | n/a | borderless only; no relaunch in the same boot |
| R8.5 | A persisted setting is left changed (Water Mod volume, SE cfg audio, Subnautica options) | S2.T-A1 file hash, S8.N6 cfg compare, S8.N9 unclean end | in-memory changes only; `restore.json`; save hooks |
| R0.4 | Shared-library divergence hides a layout bug on both sides | independent Python layout reader/writer (S0.N1) | the Python oracle, written from CONTRACT, is authoritative for layout tests |

---

## 9. Repository layout and build/deploy loop (D14, D12)

### 9.1 Layout

```
DeepEngineers/
 md journal (facts, runs, dead ends)
 README.md install, uninstall, warnings (never open a DE world without the plugin)
 build.ps1 EXISTS: refuses to run while a game runs; builds the solution; deploys both plugins
 with DeepEngineers.Wire.dll; writes the Pulsar profile entry
 DeepEngineers.sln, Directory.Build.props EXIST (SEBin64 / SubnauticaDir overridable)
 docs/
 ARCHITECTURE.md, CONTRACT.md, ROADMAP.md
 S0-STATUS.md S0 status, real-run results, discrepancies D1–D46 (EXISTS)
 S1-STATUS.md S1 status, runbook, real-run record (§10), discrepancies D47–D108 (EXISTS)
 install/ subnautica-bepinex-files.txt, deepengineers-files.txt, UNINSTALL.md (EXIST since S0;
 UNINSTALL tested in S9)
 recon/ evidence (probes, gaps, critic)
 src/
 DeepEngineers.Wire/ netstandard2.0, deployed as DeepEngineers.Wire.dll next to each plugin (loads under
 EXISTS (S0, layout 1.0): Layout
 Records, LayoutHash, Seqlock, ByteRing, EntryRing, Resync, Native, SharedSection,
 SectionCreator, SectionValidation, WireInfo, BridgeToken, WireMath, SiteMap, Geometry,
 SiteV1, SelfTest, Wm1Subset, PyFloat. To add: ViewState (pure function, S2)
 DeepEngineers.SE/ net48 Pulsar plugin (x64), assembly name `plugin`. EXISTS (S0): Plugin, Link, Phases,
 VersionGate, ActivationGate, DevChannel, PerfLog, HarmonyPatches, mutants. To add:
 ViewState, Display, Audio, Snapshot, GridExport, Terrain, Statics, Combat, TimeOfDay,
 WaterModApi (own client), site binding
 DeepEngineers.SN/ net472 BepInEx plugin `deepengineers.sn` ("DeepEngineers.SN"). EXISTS (S0): Plugin
 (bridge start on the first player-loop Update), Bridge, SectionLink, SePeerMonitor,
 ControlConsumer, WindowRole, UnitySelfTest. To add: HostSession, Ghost, Mirror,
 Proxies, Statics, Window, Audio, Perf, TimeOfDay, Capture
 DeepEngineers.Launcher/ launcher.py (Python 3.10 stdlib): preflight, deploy, manifest, run, stop,
 restore-registry (EXISTS, S0)
 DeepEngineers.WorldMod/ data only: PlanetGeneratorDefinition DE_4546B, heightmap generator script
 (all-zero 16-bit faces), Data\WaterConfig.xml
 tools/ existing oracles: worldmap/{worldmap_ref,wire_ref,site_v1}.py, sn-octree-scan,
 sn-octrees, mwm-winding, probe-dwm-standins; spy/, measure/ (S0); to add: capture/,
 audio/, logs/, release/check_release.py
 tests/ EXISTS (S0, all in the solution): DeepEngineers.Wire.Tests (net48 + net472),
 DeepEngineers.WireMath.Tests (net48, full WM-1), DeepEngineers.SE.Tests,
 DeepEngineers.SN.Tests, DeepEngineers.Wire.CrossTest, WireSelfTest (layout 1.0),
 wire/ (layout_py.py, section_py.py, cross_test.py), standins/ (fake_sn_host.py,
 fake_se_producer.py, lifecycle_test.py), wm1/. The pre-contract wire_py/ was removed
 at the S0 exit. To add: DeepEngineers.Terrain.Tests, per-slice check scripts
 third_party/ gitignored downloads (Pulsar extracted, packs); never committed
```

Rules: no decompiled code, no game files and no derived caches in the repository; the release lint
(`tools/release/check_release.py`, section 6) before any release.

### 9.2 Build and deploy loop

| Part | Build | Deploy | Load check |
|---|---|---|---|
| Wire | `dotnet build src/DeepEngineers.Wire` and `dotnet test tests/…Wire.Tests` (net48 and net472 hosts); the full WM-1 file in `tests/…WireMath.Tests` (net48); `dotnet test DeepEngineers.sln` runs every test project | `DeepEngineers.Wire.dll` copied next to each plugin by `build.ps1` | layout asserts + Python cross-read; both plugins log `WireInfo` |
| SE plugin | `dotnet build src/DeepEngineers.SE -c Release` (references Bin64 DLLs and Pulsar's 0Harmony with `Private=false`; no NuGet dependencies that collide with Bin64, Pulsar issue #57) | `build.ps1` copies `plugin.dll` and `DeepEngineers.Wire.dll` to `third_party\pulsar\extracted\Legacy\Local\DeepEngineers.SE\` and enables the profile entry (Pulsar scans `Local\`; Pulsar's DevFolder compilation is an untested alternative | Pulsar `info.log` "Enabled Plugins: DeepEngineers.SE", SE log `[DeepEngineers.SE] Init…`, our `DE.WireSelfTest PASS` line |
| SN plugin | `dotnet build src/DeepEngineers.SN -c Release` (references `Subnautica_Data\Managed` and BepInEx core with `Private=false`; HarmonyX from BepInEx) | `build.ps1` copies `DeepEngineers.SN.dll`, `DeepEngineers.Wire.dll` (and the pdb) to `<Subnautica>\BepInEx\plugins\DeepEngineers\` (a game-folder write; listed in `docs/install/deepengineers-files.txt`) | `BepInEx\LogOutput.log`: `Loading [DeepEngineers.SN …]`, `DE.WireSelfTest PASS` |
| World mod | generate heightmaps with the script, copy the folder | `%APPDATA%\SpaceEngineers\Mods\DeepEngineers.World\` | SE log lists the mod; S1.G2-E3 checks the planet |
| Oracles | `python -I tools/worldmap/site_v1.py selftest`, `python -I tools/worldmap/wire_ref.py selftest` (site selector, `SITE_V1`), `… vectors <scratch>/wm1.jsonl`, `python -I tests/<oracle>.py` | none | exit code 0 and `PASS` lines |

The Pulsar folder is wiped by a Pulsar self-update; with `-noUpdate` it is pinned, and nothing irreplaceable is kept
there (the build re-deploys).

one launch of each game per test, closed by
`CloseMainWindow` then waited on by PID; every slice has an oracle and a seeded failure (the oracle must be shown to
fail on a mutant); a mutant that can persist state runs on a named throwaway copy with autosave off, between `um
backup create` and `um backup diff` (ROADMAP §3); Python stand-ins let slices be built before or without the games;
backups exist (`um backup` `se-appdata`, `subnautica-saves`, `subnautica-locallow`); captures come from inside each
game (capture requests, C10), never from Windows.Graphics.Capture alone.

### 9.3 Slices

Display + input + audio, S3 First block, S4 Planet + terrain, S5 Whole grids + character, S6 Collision B + combat, S7
Interiors, lights, time, statics, skinned astronaut, S8a The terrain converter (§5.17), S8 Robustness, S9 Showcase and
release prep). The ROADMAP owns the single numbering.

---

## 10. Glossary

| Term | Meaning |
|---|---|

| SN | Subnautica (Unity 2019.4), the visual and ecosystem host underwater |
| Visual host boundary | the line between host-neutral bridge data (poses in host world space, view/audio/time state, damage in host units) and Subnautica-specific code, which lives only in `DeepEngineers.SN` (plus the SE-side `SnOctreeSource` terrain reader behind its own interface). Another host would implement the same channels; C7 carries `hostKind` and capability bits |
| Pulsar | community SE plugin loader (successor of Plugin Loader); `Legacy.exe` runs SE1 in its own process |
| BepInEx, HarmonyX, Nautilus | Subnautica's mod loader, its patching library, and the Subnautica modding library (installed, unused by v1) |
| Water Mod | Jakaria's SE water simulation mod (Workshop 2200451495), our sea, buoyancy, drag and swimming |
| `DE_4546B` | our ocean planet's generator subtype |
| Site, site record | where the Subnautica tile sits on the planet; constants frozen as site version 1 (`site_v1.py`) |
| C, Rs | planet centre (SE world, doubles); Water Mod sea radius (60,000.00390625 m) |
| `y` | SN altitude, negative underwater; depth = −y; `WaterModAPI.GetDepth` returns `y` |
| F, F⁻¹ | the map SE world ↔ SN world (`worldmap_ref.py`) |
| mapdir | the map's direction transform at a point (improper: determinant −1) |
| `(o, q, s)` | pose on the wire: SN position, Unity quaternion, horizontal Jacobian scale |
| P* | pivot where the map is linearised for an entity group: the group's box point nearest to the SE camera |
| K | `q⁻¹·S·q`, the collider pre-scaling that replaces a scaled parent |
| Mirror | `(x, y, z) → (x, y, −z)` on entity-local data, applied only in the SN plugin |
| WM-1 | the shared conformance vector file generated by `wire_ref.py vectors` |
| Tile, lattice | one of 34 provider-backed 1024³ `MyVoxelMap`s on a 4×4×3 lattice, pitch 1,020 m, with deterministic entity ids |
| Provider | `SnTerrainProvider`, an `IMyStorageDataProvider` reading Subnautica's octrees; one instance per tile over a shared `SnOctreeSource` |
| Terrain gate, fail-safe | the tiles' own preconditions (independent of the display pins); what happens when they fail in a DE world (section 5.15) |
| Octree, batch | Subnautica terrain storage: 32 m octrees, 160 m batches of 5×5×5 octrees, 1 m voxels |
| Plug | the crater crust as Subnautica's data only, open water under it (no fill) |
| Abyss rule, floor guard | crush below SN y −2,000 m on `DE_4546B`; downward motion stopped below −2,500 m in every world type |
| CORE, TILE VOID, OUTER VOID, FAR | zones of the camera's SN position (section 4.2) |
| Space, Approach (Track, Prime), Underwater, Surfacing | view states (section 4); Track = Subnautica streams with cameras off; Prime = margins on and Subnautica at full rate just before a crossing |
| ReadyIdle, WarmingUp, Active | Subnautica phases (C7 `snPhase`) after the host save is loaded: Idle recipe; Track, or Full before its first rendered frame; Full rendering (CONTRACT §5.4) |
| Paused, Unfocused, PeerStalled, PeerExited | overlays on the view state |
| Link epoch | counter SE bumps when a stalled peer recovers with the same PID; triggers re-validation and resend |
Begin/End(FeedbackPanel)`, on changes of `Idle ∨ Paused ∨ SE stalled` |
| Bridge token | `DEEPENGINEERS_BRIDGE=1` or the launcher's named mutex `Local\DeepEngineers_v1_launcher`; without it the SN plugin stays dormant |
| Design F, A′, C | see-through SE with DWM alpha; z-order swap; shared D3D11 texture |
| DWM | Windows Desktop Window Manager (compositor) |
| `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW` | window styles: never activated by clicks; no taskbar or Alt+Tab entry once the window has been hidden and shown again with the style |
| Ghost player | Subnautica's `Player` in locked mode, moved by the plugin to the SE character's pelvis (or the camera) |
| Proxy, section | Subnautica-side kinematic collider set mirroring an SE grid, split into ≤ 16 m sections; the swimming character is a section of kind "character" |
| EcoTarget | Subnautica component that makes an object visible to creature AI (type Shark 2030 for prey) |
| LiveMixin | Subnautica health component; our postfix on `TakeDamage` turns hits on proxies and the ghost Player into damage records |
| Freedom mode | Subnautica game mode with `NoSurvival` only (creatures aggressive) |
| FreezeTime | Subnautica pause mechanism (`UWE.FreezeTime.Begin/End(Id)`, keyed by id, sets `Time.timeScale = 0`) |
| SkyApplier, MarmosetUBER | Subnautica's per-renderer ambient/reflection applier and its world shader |
| Seqlock | snapshot region with an odd/even counter; readers retry on a torn copy |
| SPSC ring, byte ring | fixed-record and variable-message single-producer single-consumer rings |
| Generation, epoch | per-process restart counter; per-world change counter |
| Heartbeat | `GetTickCount64` written by each game thread |
| QPC | `QueryPerformanceCounter` stamps used for interpolation |
| SN HP | Subnautica damage unit (the `originalDamage` passed to `LiveMixin.TakeDamage`) |
| Tick | one SE simulation step (1/60 s) |

---

## Review log

Findings of the adversarial review, in the order received (F1…F115), with what was done. "Applied" names where.
D-numbers are the architect's decisions, which were not reversed; where a finding pulled against one, the caveat and
the detection test were added instead.

| # | Finding (severity) | Outcome |
|---|---|---|
| F1 | Water Mod API handshake missed when registered in BeforeStart (high) | applied: handler in `LoadData`, validation at first `UpdateBeforeSimulation` with WAIT/REFUSE (5.5; CONTRACT §3; S1, S1.M9) |
| F2 | Random tile entity ids unlock landing gears on reload (high) | applied: deterministic ids, saves hold them as gear references (5.6, 5.15; CONTRACT §3; S4.G2-E3b, S4.M10) |
| F3 | Water Mod volume is persisted globally (high) | applied: never `/wvolume`; emitter mute preferred, in-memory fallback restored before saves; O6 closed (5.3; CONTRACT §1.1, §9; S2.T-A1) |
| F4 | SE world load blocks the update-thread heartbeat (high) | applied: `LoadingWorld` from a `RunLoad` prefix, heartbeat from `LoadingStep` callbacks, load times measured; D5's 120 s kept with a harmless consequence (5.14; S1.N12, S8.T-L1h) |
| F5 | Provider contract incomplete (high) | applied: provider contract table, one instance per tile, LOD units, material by name (5.6; CONTRACT §3.1; S4.N3, S4.M8) |
| F6 | `WaterConfig.xml` seeds the sea only at first spawn (medium) | applied: mechanism rewritten, crush/wave/tide/current validated, repair path (5.5; S1.G2-E3) |
| F7 | `VerifyVersion` cannot pin the Water Mod (medium) | applied: `WaterData.Version` + delegate checks + probes; call fixed to two arguments (pin table) |
| F8 | SE's own focus logic overwrites the mix (medium) | applied: mix written in an `UpdateSound` postfix only while focused (5.3; S2.T-A1) |
| F9 | Sun settings are world-wide and two-part (medium) | applied: world settings table, axis checks (5.5, 5.12; CONTRACT §3) |
| F10 | Interior flag needs pressurisation, `GetRooms` flickers (medium) | applied (5.11; S7.N1b) |
| F11 | Abyss rule inactive in Creative or non-destructible worlds (medium) | applied: floor guard at −2,500 m in every world type (5.9; S8.N5b) |
| F12 | Statics host entity unspecified and would be saved (medium) | applied (5.7; S7.N10, S7.M8) |
| F13 | Voxel-edit guard on generic methods does not work (medium) | applied: non-generic entry points listed (5.6; S4.N2, S4.M9) |
| F14 | `seHwnd` calls from the update thread block on the render thread (medium) | applied: all queued to the render thread (3.2, 5.1; S2.T-D1) |
| F15 | Half-voxel convention unstated (medium) | applied as HYPOTHESIS with a synthetic-plane test (5.6; CONTRACT §3.1; S4.A-T0b, S4.M6) |
| F16 | A-T1 rays through unbuilt cells (medium, hypothesis) | applied: prefetch and "unbuilt" verdict in the harness (S4.A-T1, S4.M7) |
| F17 | O8 answered by `MyMaxHealthBuff` (low) | applied: O8 closed (5.9; CONTRACT §4) |
| F18 | Wrong pruning-structure method name (low) | applied (5.8; S5) |
| F19 | `SE_VERSION` const would be inlined (low) | applied (pin table; S0.N12, S0.M8) |
| F20 | Autosaves use the other `MySession.Save` overload (low) | applied (5.3, 5.14) |
| F21 | `DrawScene` prefix needs internals; SE already clears (low) | applied: `FullDraw`/`m_drawScene` route, LCD side effect noted (5.1, 7) |
| F22 | sRGB backbuffer makes translucent panels too bright (low) | applied: T-D0 translucent case, R2.5 widened (5.1) |
| F23 | Covered Subnautica may not render in Approach (blocker) | applied: margins on in Prime with scene alpha 1, S2.N8 as the first gate, alternatives recorded (4.2, 4.4; S2.N8, S2.M7). Caveat to D1's order only |
| F24 | Subnautica physics at 10 Hz (high) | applied: high fixed timestep registered while moving proxies are visible, 10 Hz fallback (5.9; CONTRACT §2.6; S6.N7) |
| F25 | `PDACameraFOVControl` overrides the FOV; `minNearClip` is a clamp (high) | applied (5.8, 5.11; S1.N7, S1.M6) |
| F26 | Swim capsule under the locked Player gets no contacts (high) | applied: character proxy section, G7 marked refuted (5.9; S6.M8) |
| F27 | `LiveMixin` caches receivers in `Start` (high) | applied: `TakeDamage` postfix plus start-up self-check (5.9; S6.N9, S6.M12) |
| F28 | Player left at the save spot or at the lens (high) | applied: one rule, pelvis or camera (5.11; S1.N8, S1.M10) |
3; S2.T-A1, S2.M12) |
| F30 | Subnautica's HUD and PDA show through (high) | applied (5.11; S1.N9, S1.M7) |
| F31 | Statics filter takes kinematic creatures and our proxies (high) | applied: filter by entity type (5.7; S7.N9, S7.M7) |
| F32 | `IsWorldSettled` blocks dives from moving vehicles (high) | applied: `areaBuilt` plus forward box; settled only after loads/teleports (4.1; S2.N7, S2.M10).: the forward box left the gate (it held fast dives 0.5–7 s), telemetry only; S2.N7 (b) to re-judge on real fast dives |
| F33 | `targetFrameRate` ignored with vsync (medium) | applied with F54 (5.13) |
| F34 | "Run in background" off freezes Subnautica (medium) | applied (5.2; S1.N11, S1.M8) |
| F35 | `anchorSky = BaseInterior` falls back to the biome sky (medium) | applied: `SetCustomSky` (5.8; S7.N8, S7.M5) |
| F36 | `autoSyncTransforms` false in game (medium) | applied: `SyncTransforms` before queries (5.9; CONTRACT §2.9; S6.M9) |
| F37 | Scaled-time timers stop while frozen (medium) | applied: unscaled clocks rule (3.2; CONTRACT §2.1) |
| F38 | Nautilus hard vs soft dependency (medium) | applied: not referenced, soft dependency for load order only (pin table, 6) |
| F39 | Wire DLL already deployed and working (medium) | applied: deployed DLL is the v1 decision (2, 9.1, 9.2) |
| F40 | Electrical and other damage paths reach proxies (medium) | applied: `DeepShock`, fallback hit point (5.9; CONTRACT §4.1; S6.N6) |
| F41 | `IsUnderwater` also drives audio (medium) | applied (5.11; S1.N6) |
| F42 | `onPreCull` fires for every camera (low) | applied (5.8) |
| F43 | Console commands restart the day cycle (low) | applied: per-frame re-assert, plugin time command (5.12; S3.C7) |
| F44 | Subnautica's console cannot get keystrokes (low) | applied: `/de` chat relay over C11 (5.2; ROADMAP §5.10) |
| F45 | Launcher method, phase and timeout disagree (low) | applied: one rule (5.14; CONTRACT §7; ROADMAP §5.7) |
| F46 | Runtime `WS_EX_TOOLWINDOW` keeps the taskbar button (low, hypothesis) | applied together with F52 |
| F47 | Texture colour space and `Compress` cost (low) | applied (5.8; S3.M8) |
| F48 | Covered Subnautica in Approach, second report (blocker) | applied with F23 (S2.N8 first gate; R2.3 now covers Approach) |
| F49 | 1 Hz exit polling leaves SE transparent over the desktop (high) | applied: per-tick polling while margins are on, window check, `Quitting` (5.14; S8.T-L1g) |
| F50 | PeerDead never ends for a recovered stall (high) | applied: PeerStalled/PeerExited, link epoch (4.3; CONTRACT §6; S8.T-L1b) |
| F51 | Creator zeroing breaks live rings (high) | applied: ring re-sync rules, magic cleared first, record validation (CONTRACT §5.2, §6; S0.N11, S0.M7) |
| F52 | Runtime restyle keeps the taskbar button (high) | applied: hide/restyle/show at `ReadyIdle` (5.1; S2.N9) |
| F53 | Raising Subnautica needs foreground permission (high) | applied: `AllowSetForegroundWindow`, verify, retry, `zOrderOk` (5.1; S2.N5) |
| F54 | Frame-rate caps ignored with vsync; `SetFrameRate` clamps (high) | applied: (vSyncCount, targetFrameRate) per state (5.13; CONTRACT §5.4 C7; S2.N3) |
| F55 | Approach has no time limit, so surface play pays for Subnautica (high) | applied: Track/Prime split; caveat to D10: shader warm-up happens in Prime only (4.2, 5.13; S2.N11, S5.T-P1) |
| F56 | Pulsar kills SE in `ExitThreadSafe` (medium) | applied: first-priority prefix, no exit cleanup, crash form (5.14; S8.T-L1c) |
| F57 | Subnautica quits after 120 s of SE load or hang (high) | applied: never quits on heartbeat loss; D5's timeouts kept with recoverable consequences (5.14) |
| F58 | Every Subnautica launch would enter bridge mode (high) | applied: bridge token, mutex, creator refusal (5.10; CONTRACT §6; S0.N10) |
| F59 | Entry order presents a cleared frame before the margins (medium) | applied: margins first (4.4) |
| F60 | `GW_HWNDNEXT` oracle trips on owned hidden windows (medium) | applied: walker definition (5.1; S2.T-D1, S2.M9) |
| F61 | 21H2 out of servicing, flip-upgrade likely (high) | applied: measured detection, R2.1; S8.N7) |
| F62 | SE audio arbitration with `UpdateSound` (medium) | applied with F8 |
| F63 | Foreground hand-off from Subnautica is fragile (medium) | applied: SE takes the foreground itself (5.1; S2.N10, S2.M8) |
| F64 | Launcher timeouts, method and relaunch policy disagree (medium) | applied: one launcher table in CONTRACT §7 |
| F65 | Repo section code lacks DACL; Python creates by tag name (medium) | applied: S0 migration items, launcher closes its handle, preflight absence check (5.14; ROADMAP S0) |
| F66 | Ghosting replaces the restyled window during stalls (low) | applied: `DisableProcessWindowsGhosting` (5.1; S8.T-L1b) |
| F67 | Monitor selection and mid-session rect changes (low) | applied: `-monitor 1`, per-second compare (5.1) |
| F68 | One latency number mixed two budgets (low) | applied: two budgets (4.4) |
| F69 | Mixed elevation breaks window calls (low) | applied: launcher refuses mixed elevation; claim dropped (5.14; CONTRACT §6) |
| F70 | Astronaut has 81 bones, cap was 64 (high) | applied: cap 160, load-time assert (3.2, 5.8; CONTRACT §5; S7.M9) |
| F71 | S2.N2 bobbing trace crosses both thresholds (high) | applied: two traces (ROADMAP S2.N2, S2.M4) |
| F72 | Error budgets computed for the EXAMPLE site (high) | applied: selftest re-run with `SITE_V1` (0.24°, 0.11 m, 0.80°/14.4 px) (5.4; CONTRACT §2) |
| F73 | PeerDead same generation, second report (high) | applied with F50 |
| F74 | Sun axis comes from the environment, not the saved direction (high) | applied with F9 (5.12; S7.WM-5) |
| F75 | 1 Hz exit polling vs "one frame" claims (medium) | applied with F49; its 10 s timeout in Space/Approach rejected (contradicts D5's 3 s); the HUD notice is delayed there instead |
| F76 | "Depth" has opposite signs across documents (medium) | applied: one symbol `y` (4.1, glossary; ROADMAP S1.WM-2, S2) |
| F77 | `presentedSeTick` ambiguous, `snReady` flickers (medium) | applied: newer bracket tick, freshness 4 ticks, crossing tick for entry (4.1; CONTRACT §5.4) |
| F78 | Scale range wider than 0.997…1.028 (medium) | applied: s ∈ [0.994, 1.053], budgets at −2,000 m, WM-1 ranges (5.4; CONTRACT §2.1, §2.9) |
| F79 | Collider refresh radius ignores half extents (medium) | applied (CONTRACT §2.6) |
| F80 | Subgrid hit points in the root frame (medium) | applied (5.9; CONTRACT §2.7, §5.4 C8; S6.N8) |
| F81 | Static primitive orientation must be mirrored (medium) | applied (5.7; CONTRACT §2.7, §2.9) |
| F82 | Abyss rate too slow to protect the floor (medium) | applied with F11 (5.9; S8.N5) |
| F83 | S3.C2/S5.N1 reference was the exact map, not the affine (medium) | applied (ROADMAP S3.C2, S5.N1) |
| F84 | Record sizes and implicit pads (medium) | applied: explicit offsets for every record (CONTRACT §5.4) |
| F85 | Implemented ring framing differs from CONTRACT (medium) | applied: CONTRACT framing is v1, prototypes migrate in S0 (CONTRACT §5.3; ROADMAP S0) |
| F86 | Damage scope and names differ (medium) | applied: Normal/Explosive/Electrical forwarded, `k_small`/`k_large`, one name set (CONTRACT §4.1; S6) |
| F87 | Seqlock tries and launcher wait differ (low) | applied: 16 tries; Menu for S0, ReadyIdle 180 s from S1 |
| F88 | Docking pop understated (low) | applied: 0.33–0.45 m for a 200 m group (CONTRACT §2.4) |
| F89 | WM-6 tolerance tighter than the surface budget (low) | applied (ROADMAP S4.WM-6) |
| F90 | Control files vs C10/C11 (low) | applied: C10/C11 in the section kept, control files dropped (ROADMAP §5.10) |
| F91 | Subnautica's displaced water surface vs the 0.20 m band (low, hypothesis) | applied: measured in S1.N10 (4.2) |
| F92 | Pin failure makes grids sink (high) | applied: terrain gate and fail-safe (5.6, 5.15; S8.N8) |
| F93 | Water Mod volume persisted globally, second report (high) | applied with F3; `restore.json` and CONTRACT §1.1 "Settings touched" |
| F94 | `um publish check` cannot catch decompiled code or caches (high) | applied: own release lint (6; ROADMAP S9.N1, S9.M1) |
| F95 | Carried-in code contradicts the documents (high) | applied: carried-in code table in ROADMAP S0

| F97 | S8 automatic relaunch contradicts the safety rule (high) | applied: removed, player-initiated restart only (5.14; S8.T-L1d) |
| F98 | SE's audio options screen persists the plugin's volume (medium) | applied: no writes while a GUI screen is open, `Save` prefix (5.3; S8.N6) |
| F99 | C10/C11 vs control files, second report (medium) | applied with F90 |
| F100 | Creator overwrites any layout (medium) | applied: reuse only on a full match, else refuse (CONTRACT §6; S0.M6) |
| F101 | CONTRACT not specific enough for stand-ins (medium) | applied: siteHash, layoutHash grammar, SDDL, mutexes, offsets, one stand-in naming (CONTRACT §5.4, §6) |
| F102 | S0.M5 is not a mutant (medium) | applied: three detector mutants (ROADMAP S0.M5a–c) |
| F103 | Damage names and factors, second report (medium) | applied with F86 |
| F104 | Non-goals vs slice content; two numberings (medium) | applied: census stays in v1 (D2), mesh cache is a stretch item, ARCH §9.3 points to ROADMAP, one risk register (7, 8, 9.3) |
| F105 | Lifecycle rows without checks (medium) | applied: S8.T-L1d–h; T-L1f adapted to the no-quit rule of F57 |
| F106 | EULA clauses 1.5, 2.1, 2.2.3, 2.2.4. ROADMAP S9) |
| F107 | Water Mod API client must not be a copy (medium) | applied: clean reimplementation; internals limited and version-gated (5.5, 6) |
| F108 | Player requirements and release channel (medium) | applied: Python 3.10+ listed, release-channel paragraph (6) |
| F109 | Uninstall incomplete and untested (medium) | applied: `UNINSTALL.md` from a manifest, S9.N5 (CONTRACT §7; ROADMAP S9) |
| F110 | Mutants that persist state need isolation (medium) | applied: ROADMAP §3 rule 10 |
| F111 | Checks without numeric thresholds (low) | applied (ROADMAP S0.N7, S2.N4, S2.T-D1, S4.A-T1) |
| F112 | Plugin affects every SE session (medium) | applied: activation gates (1; CONTRACT §1.2; S8.N6) |
| F113 | Crosshair numbers for the EXAMPLE site (low) | applied with F72; S1.G1-E5 uses the formula |
| F114 | Unlabelled foreground and build-threshold claims (low) | applied: HYPOTHESIS / REPORTED labels, S2.N10 (5.1) |
| F115 | Backups miss the Subnautica saves (low) | applied: `subnautica-saves` in the preflight;14) |

No finding was rejected outright. Partial rejections: F75's 10 s timeout in Space and Approach (D5 keeps 3 s; the
stall is made harmless there instead), F4's longer load cap and F57's render-thread heartbeat (D5 keeps 120 s while
loading; the stall during a long load is harmless because Subnautica never quits on heartbeat loss).

**Consistency pass.** Numbers, ids, channels and Harmony targets were cross-checked
across the three documents, `site_v1.py` (record, `siteHash` 0x53ab8e77d68103ab over 1,506 bytes, radius bits
0x3F86BCA2, tile ids recomputed), the SE `.sbc` files and the decompiled sources; every Harmony target named exists.
Edits: a sun-check failure refuses only time sync, never the display bridge (5.5 and CONTRACT §3 runtime validation,
now matching 5.12 and CONTRACT's world-settings paragraph); the terrain gate sits inside the global activation gate, so a DE world opened in
multiplayer is as unprotected as one opened without the plugin (5.6, 5.15; CONTRACT §1.2, §8; ROADMAP R4.8, §9);
the fail-safe save block names the `MySession.Save(out …)` overload; C3 record names (no "ModelDef"); C11 relay
ownership; character capsule as radius 0.25 m, height 1.8 m; evidence labels on the sRGB panel arithmetic, the
audio-options save, the timer-resolution remark and the direct Subnautica launch; a "DE world" row and the conditional
Display option in section 6; ROADMAP: R1.2 limit ±7,900 m, Surfacing conditions, R4.3 budgets, S0.N12 (the version is
a field, not a getter), T-L1c/e quit-versus-gone times, `subnautica-saves` backup from S0, Water Mod subscription in
CONTRACT §5.4 freshness rationale (a 60 fps frame is 1 tick).
