# DeepEngineers roadmap (v1)

Status: design phase, revised after the review of the same day (log at the end of `docs/ARCHITECTURE.md`).
md`); its
discrepancies and lessons are applied here, in CONTRACT and in ARCHITECTURE. S1 is **done**; its measured numbers and lessons are applied
here (section 5.12), in CONTRACT and in ARCHITECTURE. Next: S2, whose CONTRACT-first items are now in CONTRACT §1.1 and
§5.4 ("C5/C7 semantics for S2") and ARCHITECTURE §4.2 (the state thresholds table).
This roadmap turns the decisions D1–D14 (section 1) into one numbered list of slices, S0 to S9. Each slice has an
There is no calendar here: slices are
steps, and a slice starts when the slices it builds on have exited. Test durations such as "5 min" are test
parameters, not schedule.

Companion documents: `docs/CONTRACT.md` (bridge contract: every number and record, the single source for values this
roadmap tests), `docs/ARCHITECTURE.md` (journal). The recon reports in
`docs/recon/` hold the evidence; this file restates every decision it needs, so it can be read without them.

Evidence labels: **VERIFIED**,
**REPORTED** (web or creator claim not checked locally), **HYPOTHESIS** (inference or proposed threshold, to be
tested). Evidence levels for slice results: *source inspection* < *synthetic test* (Python stand-ins, unit tests,
stand-in windows) < *real run* (both real games on the pinned versions below, and someone looked).

| Part | Version |
|---|---|
| Space Engineers 1 | 1.210.014, Steam build 24675677,.NET Framework 4.8 x64, process `Legacy` under Pulsar; run-time check `MyPerGameSettings.BasicGameInfo.GameVersion` |
| Pulsar | v2.4.3 Legacy, `third_party\pulsar\extracted\` (ships Lib.Harmony **2.4.2.0**, `Libraries\Legacy\0Harmony.dll`) |
| Water Mod / Text HUD API | 3.23 (workshop 2200451495, API id 50271, version 22; pinned by `WaterData.Version`) / workshop 758597413 |
| Subnautica | 1.22.83031, Steam build 20241558, Unity 2019.4.36f1 Mono, D3D11 |
| BepInEx / Nautilus | 5.4.23.5 (Tobey's pack 3.1.1, HarmonyX **2.9.0.0** in `BepInEx\core\0Harmony.dll`) / 1.0.0.54 installed, not used by v1 |
| Machine | Windows 11 build 22000 (21H2, out of servicing), Ryzen 7 3700X, RX 6900 XT (driver 32.0.21045.1000), 32 GB, 2560×1440 at 165 Hz plus a 1920×1080 secondary monitor, borderless only |

---

## 0. The slices in one screen

| Slice | Goal | Games in the loop | Builds on | Evidence at exit |
|---|---|---|---|---|
| **S0** Hello bridge | carried-in prototypes migrated to the CONTRACT layout; both plugins load, run the WM-1 subset, share the file mapping, exchange heartbeats and generations; wrong peers, second instances and unlaunched Subnautica sessions rejected | both, at their menus | none | **DONE**: real run (load + link only
| **S1** Pose bridge | SE camera drives Subnautica's ghost camera in a never-saved Freedom host save; site binding through the Water Mod API; map checked in game (WM-2/3/4, G1-E5); ghost Player rule, FOV, hidden UI; open: the S1.N10 sampler (O15) |
| **S2** Display + input + audio | first gate: a covered Subnautica keeps rendering (S2.N8); design F (SE on top, see-through underwater), state machine with Track/Prime, input stays with SE, audio rules; mutants M1/M3/M4/M5/M6/M14 red
| **S2b** Swimming (Subnautica's movement model in SE) | the engineer moves underwater exactly like Subnautica's diver: same control semantics, speeds, acceleration, drag, surface behaviour, oxygen rules; jetpack off in the water; measured against Subnautica's own motor; no contract change | SE alone (S2b.N1–N4), both (S2b. independent of S2 | **DONE**: real run (N1–N3 on DE Lab, N4 on a Survival copy, replay-exact
SE's suit O2 mirrored into Subnautica's `OxygenManager` so its breathing, bubbles and low-O2 choke play natively (PDA voice suppressed; the ghost cannot die); fins option (Ultraglide +3.2 m/s default, `/de swim fins`); dev see-through hotkey F8 → Apps; the swim state crosses in the C1 pad (layout 1.0 unchanged, extension pinned on its own) | SE alone (`launcher.py s5a`: S5a.FINS/O2/ARMS/CHOKE), both (s2 recipe + `s5a --analyze --sn-log`: S5a.SN; S5A-STATUS §11 runbook |
was "S2c") | a native SE inventory screen with Subnautica's ergonomics: grid of slots, drag and drop, nearby containers side by side, quick transfer, toolbar assignment, search, PDA-style look; SE's items, icons and inventory API; recon of existing Pulsar/Workshop inventory plugins first | SE alone | none (plugin only; no bridge)
| **S3** First block | one large armor block, then a 3×3 plate, drawn natively in Subnautica at the mapped seabed position (C1–C14) | Subnautica (S3a), both (S3b) | S2 | **DONE (conditional)**: real run (S3a-1 with the fake producer; S3b-1/2/3 with both games: C2 0.12 mm, N2r 0. the character in S5 remains the finer comparison); deferred to the S3 fix pass: C13 paint ΔE 17, C14 seams 3.9×, N1b 0.96; oracle items C5 (dark threshold), C6 (P1 framing), C9 (Seamoth shadow model) → S8;5.1 |
| **S4** Planet + terrain | SE collides natively with Subnautica's terrain through 34 provider-backed voxel tiles with deterministic ids; saves stay portable; gear locks survive reloads | SE (A-T1), both (A-T2, WM-6), dormant Subnautica console (G2-E1) | S1; S3 for the WM-6 and A-T2 captures | **REAL RUNS — collision proven, exit pending**: S4-1/1b (SE alone) and S4-2 (both games) — A-T2 Subnautica ray vs SE cast P95 0.189 m (106 cols), WM-6 cube on the seabed +0.027 m, drop 10/10, walk sink ≤ 0.144 m, N1 0.05 m, N3 0 violations, 3c 861 µs; open: crater edge beyond 1.8 km, thin-feature tail (D1), A-T0b LOD ≥ 1, 3f, throwaway-copy sessions, mutant groups; S4-STATUS §9.9, §12
| **S5** Whole grids + character | every grid near the camera mirrored (instances, chunks, colours, subgrids, build/damage states), static astronaut; T-P1 | both | S3, S4 | **REAL RUNS** — S5-1 stand-in at scale (5,000 blocks p99 6.8 ms), S5-2a/2c/2d/2e both games: character green unattended (path G retarget 100 %, N7a 0.09 cm, N7b), model set 200 + census PASS, N5 PASS; five mutant groups red as designed; T-P1 baselines + combined states recorded
| **S6** Collision B + combat | sectioned proxies (grid and character) in Subnautica at 50 Hz physics; creatures avoid, bite, shock and blast; damage reaches SE with the contract's unit and scale, exactly once | Subnautica (B-T1), SE (S6.N1), both | S5 | real run |

| **S8** Robustness | lifecycle (T-L1a–h), stall recovery, fail-safe, settings restore, Alt+Tab, pause, long runs, memory, abyss rule, "SE never diminished" regression | both | S2–S7 | real run |

Work that needs no game can start early: the S4 offline items (A-T0, A-T0b, provider unit tests) right after S0, and
the S4.G2-E1 console check at the first dormant (unbridged) Subnautica session (it is the only open fact that could
still reverse the terrain "plug" decision).

---

## 1. Decisions this roadmap applies (recap, not reopened)

| Id | Decision | Where it lands |
|---|---|---|
| D1 | **Display = design F.** SE keeps OS focus all session. Underwater the SE plugin skips its 3D scene (backbuffer cleared to transparent) and enables DWM per-pixel alpha (`DwmExtendFrameIntoClientArea` with −1 margins). Subnautica's window sits directly under SE's, same rectangle, `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW`, never foreground. Nothing is forwarded; SE's HUD and menus draw over the Subnautica picture. Fallback **A′** (z-order swap, SE still focused, HUD rebuilt later) if T-D1 fails; contingency **C** (shared D3D11 texture) only if DWM alpha is impossible. Borderless only (AMD exclusive-fullscreen hang). | S2, S8 |
| D2 | **Terrain = provider-backed SE voxel tiles.** A plugin-registered `IMyStorageDataProvider` (`MyOctreeStorage.RegisterTypes` in `Init`) reads the user's `CompiledOctreesCache` read-only through the site map. 4×4×3 lattice of 1024³ `MyVoxelMap` tiles, pitch 1020 m, 34 non-empty; `Save = false`, recreated in `BeforeStart`; plug, not fill. Abyss rule: crush below SN y −2,000 m on this planet; Water Mod `CrushDamage` 0 on this planet. Subnautica's non-terrain statics go to SE as static Havok primitives (streamed, then censused). | S4, S7, S8 |
| D3 | **Planet `DE_4546B`** in a local world mod: storage radius 60,000 m, HillParams −0.05/0.12, six all-zero heightmaps (floor 57,000 m), Water Mod `Radius` = float32(60000/57000) = 1.0526316 → Rs = 60,000.0039 m, waves and tides 0, `Volumetric` false, SurfaceGravity 1, breathable atmosphere, no weather generators, `SunRotationIntervalMinutes` 20, horizontal `BaseSunDirection`. One site (the 4,096 m tile); the rest is bottomless dead zone. Constants frozen in `tools/worldmap/site_v1.py` (site version 1) and `docs/CONTRACT.md`. | S1, S4, S7 |
| D4 | **Map and wire.** Sea-level azimuthal-equidistant + altitude map (`worldmap_ref.py`), all map math in the SE plugin in doubles. World-level data crosses in Subnautica world space as poses `(o, q, s)` with the group-nearest pivot P*. Entity-local data (meshes, part matrices, skins, local hit points) crosses in SE-local SE-handed space; Subnautica applies the single mirror (x, y, −z) in `WireMath.Mirror`; triangles become (I0, I2, I1) with an automatic per-model flip. Renderers under a Frame scaled (s, 1, s); camera and player capsule unscaled; colliders are unit-scale roots pre-scaled with K = q⁻¹·S·q. Conformance = WM-1 vectors from `tools/worldmap/wire_ref.py`, a subset run at plugin load on both sides. Known limitation: crosshair bias up to (s−1)/2 rad. | S0, S1, S3, S6 |
| D5 | **Transport.** One named file mapping, P/Invoke `CreateFileMappingW`/`MapViewOfFile` on both sides (no.NET `MemoryMappedFile`, no cross-process events on game threads), polled once per frame/tick. Seqlock snapshots for poses and state, bounded queues for events, padded byte rings for meshes; header with magic, version, byte order, PIDs, heartbeats, generations. Subnautica (started first) creates the section. Peer dead after 3 s in play, 120 s while loading. A restart generation triggers resends. | S0, S8 |
| D6 | **Ownership.** SE owns player movement, physics, health, inventory, saves, time and camera. Subnautica owns the underwater picture, its creatures' AI, ambience and biome music. Combat: creatures attack SE proxies (postfix on `MeleeAttack.CanDealDamageTo`, prefix on `MeleeAttack.OnTouch` for collider and mouth contact, `LiveMixin` with zero `DamageModifier` and a forwarding receiver, `EcoTarget` Shark anchors). Damage records carry unit, scale factor and dedup id; SE applies `MySlimBlock.DoDamage` / `MyCharacter.DoDamage`; SE health is authoritative. Scale factors are fixed in CONTRACT from SE values against Subnautica bites (boneshark 30, crabsquid 40, reaper 80, ghost 85). | S6 |
| D7 | **Subnautica host save** in **Freedom** mode (Creative contains `NoAggression`), never saved (Harmony on `IngameMenu.SaveGame`); ghost player in locked mode (`FreecamController`-like: `EnterLockedMode`, dummy `InputHandlerStack` entry, `GameInput.ClearInput`); camera driven from SE's camera pose each frame (position interpolated by QPC stamps, latest orientation); streaming follows the camera; `IsInsideWalkable` patched true inside SE airtight rooms. | S1, S7 |
| D8 | **Ship rendering.** SE loads models with its own loader (`new MyModel(path){LoadUV=true}.LoadData(true)` on a worker thread, LOD0, Havok skipped), streams each model once plus per-part instance records on change (render-cell and `RebuildInstanceParts` hooks, grid events). Subnautica builds chunked combined meshes (8×8×8 blocks × material × colour), MarmosetUBER via Addressables (not `Shader.Find`; property `_SpecInt`), `SkyApplier` per renderer, `_MultiColorMask`/`ColorCustomizer` for SE paint, SE `_cm/_ng/_add` DDS → Marmoset slots; subgrids as child transforms; character: proxy → static astronaut → skinned astronaut later. Multiplayer out of scope. | S3, S5, S7 |
| D9 | **Audio.** Underwater: Subnautica `bus:/` 1.0, SE game 0.7 × config, SE music 0, Water Mod volume 0. Above water: Subnautica muted and paused. 300 ms crossfades. SE pause → Subnautica `FreezeTime`. Alt+Tab → both muted. | S2, S7 |
| D10 | **Performance.** Subnautica idle recipe in Space (`FreezeTime`, cameras off, `targetFrameRate` 10, BelowNormal); Approach warm-up (camera < 300 m above the sea, in zone); full rate underwater. Both plugins opt out of power throttling. No CPU affinity in v1.6 GB + Subnautica 2.3 GB; neither pauses unfocused; the GPU saturates when both render, so the hidden side must not render. | S2, S5 |
| D11 | **Lifecycle.** Launcher starts Subnautica (BepInEx) first, waits for its ready flag, then SE via Pulsar `Legacy.exe -noUpdate -noSplash -noPrompt -bin64 <SE Bin64>`. Watchdogs: header heartbeats + process handles. Subnautica dies → SE falls back to Water Mod rendering. SE dies → Subnautica quits. Nobody force-kills a game during D3D init; kills only by exact PID. | S0, S8 |
| D12 | **Process.** English docs; evidence labels; every slice has an oracle and a seeded failure (the oracle shown to fail on a mutant); Python stand-ins; one launch of each game per test, closed by `CloseMainWindow` then PID; backups exist (`um backup` se-appdata, subnautica-locallow; plus subnautica-saves). | everywhere |
| D13 | **Non-goals v1:** multiplayer; SE 3D-only feedback beyond grids + character (placement ghost, LCD text, hands/tools); weather; the void treadmill (FAR shows SE's Water Mod fog); hull-pressure progression (v2); Subnautica 2. The Subnautica side sits behind a "visual host" boundary. | section 8 |
| D14 | **Repo layout:** `src/DeepEngineers.Wire` (netstandard2.0), `src/DeepEngineers.SE` (net48 Pulsar plugin), `src/DeepEngineers.SN` (net472 BepInEx plugin), `src/DeepEngineers.Launcher` (script in v1), `tools/`, `tests/`, `docs/`. | section 5 |

**Caveats added by the review (decisions kept; ARCHITECTURE has the mechanisms).** D1: the DWM margins go on already
in Approach/Prime while SE still draws its opaque scene, so Subnautica is never a fully occluded window when it must
render (first S2 gate, S2.N8). D3: the day length and sun direction are world settings and apply to every planet of a
DE world. D5: the 3 s / 120 s timeouts are kept, but their consequence is recoverable (PeerStalled with a link epoch;
Subnautica never quits on heartbeat loss). D6: the "forwarding receiver" is a postfix on `LiveMixin.TakeDamage` (a
late receiver is never called); the swimming character is its own proxy section. D7: the locked Player is moved by the
plugin to the SE character's pelvis (or the camera). D10: Approach is split into Track (cameras off, whole surface
band) and Prime (cameras on, seconds before a crossing); shader warm-up happens in Prime only.

---

## 2. One numbering for every test

### 2.1 Convention
- Slices are `S0`…`S9`. These ids are stable.
- A check keeps its probe name behind its slice: `S1.WM-2`, `S3.C7`, `S4.A-T1`, `S2.T-D1`. The two gap reports both
 used E1–E5, so their experiments become `G1-E<n>` (gap-1, wire) and `G2-E<n>` (gap-2, site): `S3.G1-E2`,
 `S4.G2-E1`.
- New checks with no probe name are `S<n>.N<k>`. Seeded failures are `S<n>.M<k>`.
Check S3.C7 PASS...`) and file names under `tests/out/` use these ids. Risk ids
 (`R2.3`) are this file's (section 7), also used by ARCHITECTURE.

### 2.2 Old names → new ids

| Source | Old id | New id | Note |
|---|---|---|---|
| prior-art §d.2 | S0 protocol library | S0.N1–N4 | layout, seqlock torture, rings, cross-language |
| prior-art | S1 link | S0.N5–N12, S0.T-L1s | real link + synthetic lifecycle |
| prior-art | S2 geometry | S3 | |
| prior-art | S3 pose sync (gold wall) | S1.N1–N2 (camera), S3.N2 (gold wall with an SE HUD marker) | |
| prior-art | S4 character on seabed | S4.N1 | |
| prior-art | S5 damage once | S6.N1, S6.B-T1e | |
| prior-art | S6 display switch | S2 | |
| probe-collision | A-T0, A-T1, A-T2 | S4.A-T0 (+ A-T0b), S4.A-T1, S4.A-T2 | A-T0 is offline; can start after S0 |
| probe-collision | B-T1 | S6.B-T1 (Subnautica only) and S6.B-T1e (end to end) | |
| probe-display-input | T-D0 | done (synthetic PASS); re-run as S2.T-D0 regression with a translucent case | |
| probe-display-input | T-D1, T-I1, T-A1 | S2.T-D1, S2.T-I1, S2.T-A1 | |
| probe-display-input | T-P1 | S5.T-P1, re-run S8.T-P1r | |
| probe-display-input | T-L1 synthetic / real | S0.T-L1s / S8.T-L1a–h | |
| probe-world-mapping | WM-0 | done (`worldmap_ref.py selftest` PASS; `site_v1.py selftest` PASS) | |
| probe-world-mapping | WM-1 | S0.WM-1 (full file, `dotnet test`) + S0.G1-E4a (in-game subsets) | |
| probe-world-mapping | WM-2, WM-3, WM-4 | S1.WM-2, S1.WM-3, S1.WM-4 | |
| probe-world-mapping | WM-5 | S7.WM-5 | |
| probe-world-mapping | WM-6 | S4.WM-6 | "baked seabed" is now "provider seabed" |
| probe-ship-render | S2-a, S2-b | S3a, S3b | |
| probe-ship-render | C1–C12, C13, C14 | S3.C1–S3.C14 (C1, C2, C3, C11, C12 re-run per grid in S5.N1) | |
| probe-ship-render §6.7 | 1 analytic projection, 2 coherence, 3 transport histogram, 4 step response, 5 dual-visible capture, 6 gold wall | S1.G1-E5 / S3.N3, S5.N4, S1.N1, S1.N2, S2.N0 (composed capture), S3.N2 | |
| gap-1 | E1 winding census | done | |
| gap-1 | E2 shear, E3 K colliders, E4 self-test + C3 on real models, E5 crosshair offset | S3.G1-E2, S6.G1-E3, S0.G1-E4a + S5.G1-E4, S1.G1-E5 | |
| gap-2 | E1 cut faces, E2 save portability, E3 planet behaviour, E4 Rs bits | S4.G2-E1, S4.G2-E2 (+ G2-E3b gear locks, G2-E5 world mod removal), S1.G2-E3, S1.G2-E4 | E3/E4 moved to S1 (see 2.4) |
| MODDING_PLAN-subnautica | slices (0)…(4) | S0, S1, S3, S6.B-T1, S4.A-T1 | |
| MODDING_PLAN-space-engineers | unknowns 1–7 | 1 → S0; 2 (input injection) dropped by D1; 3 (`DrawEnabled`) → only in fallback A′ (S2); 4 (triangle colliders) replaced by D2 (runtime stream kept as an optional A-T2 diagnostic); 5 → S1; 6 → S3/S5; 7 → S0/S1 | |
| critic | G1 … G12 | G1 closed (gap-1), G2 closed (gap-2), G3 → S1.N5/S6.N2, G4 → S6 + CONTRACT, G5 → S2.N1, G6 → S4 (provider id 45460), G7 → **refuted by code** (the locked Player's capsule gets no contacts; replaced by the character proxy section, S6.B-T1e/S6.M8) and S7.N2, G8 → S0 preflight + S5.A-T0 + S7.N7, G11 → section 3 + stand-ins, G12 → section 8 | |

### 2.3 Evidence carried in (done before S0)
- T-D0, synthetic, PASS on this PC: a DXGI 1.0 bitblt stand-in cleared to alpha 0 with `DwmExtendFrameIntoClientArea(-1)`
 shows a live flip-model window underneath; hit-testing stays on the top window; a flip-model top window loses
 transparency (`tools/probe-dwm-standins`). Limits: the stand-ins had their window styles at creation, the lower
 window was partly visible, and runs lasted < 4 s; no fully covered case was tested.
- WM-0: `worldmap_ref.py selftest` PASS (round trip 1.4e-10 m); `site_v1.py selftest` PASS with the frozen site;
 `wire_ref.py selftest` PASS (EXAMPLE site); the same selftest re-run in memory with `SITE_V1` PASS and gives the site-v1 budgets used in these documents.
- gap-1 E1: `.mwm` winding census, 99.75 % of file-order triangles point against vertex normals.
- Octree readers: C# (`tools/sn-octree-scan`) and Python (`tools/sn-octrees`) agree on 669,150 octrees.

4.23.5 + Nautilus load at the menu; a
 scratch `BuildTest` plugin loaded at 20:55 and was moved out afterwards (not present now); Pulsar's Local plugin
 format was found and proven (20:54). `build.ps1` built and deployed the skeleton: Subnautica logged `Loading
 [DeepEngineers 0.0.1]`, Awake, 600 frames and OnDestroy; Pulsar logged "Enabled Plugins: DeepEngineers.SE … Initializing
 1 plugins" and SE logged `[DeepEngineers.SE] Init` with `WireInfo.Describe`, so `DeepEngineers.Wire.dll` loads
 next to the plugin under **both** loaders. `Dispose` never appears in the SE log (explained by Pulsar's exit patch,
 ARCHITECTURE §5.14).
py` passed
 synthetic torture and cross-process tests, but on a **pre-contract layout** (S0 "Carried-in code").

### 2.4 Where this roadmap departs from the proposed skeleton
- The planet world mod and its checks (G2-E3, G2-E4) move from S4 to **S1**: WM-2/3/4 need the real sea at Rs.
 S4 keeps the terrain.
- `LargeWorld.GetBiome` outside the tile (OUTER VOID zone) is patched in **S2** with the zones of the state machine.
- The gold-wall test is in **S3**, as "SE HUD marker vs Subnautica-drawn block", which is what design F makes
 visible.
- The skinned astronaut (D8 "later slices") is in **S7**.
- The covered-render gate (S2.N8) is the first S2 check, before any state-machine work.

---

## 3. Rules for every slice

1. **Oracle first.** Every check states its numeric pass threshold before code is written. Every slice runs at least
 one seeded failure (a mutant build, a mutated input or a disabled patch). The check must go red on it, and the red
 A check that has never been red does not count.
2. **Stand-ins before games.** Each side is first run against a Python stand-in of the other side (section 5.8).
 That gives at most *synthetic test*. Only a real run gives *real run*, and only for the scenarios played.
3. **Who launches. One launch of each game per test, counted
 in section 10 (baselines included). Close with `CloseMainWindow`, then wait on the PID. Kill only by exact PID, and
 never during D3D init. Never relaunch right after a forced quit (zombie pattern on this AMD machine). Check that

4. **Before a session.** No game process alive (`Legacy`, `SpaceEngineers`, `Subnautica`); no stale
 `UnityCrashHandler64`; no `SpaceEngineers -report` crash form; at least 12 GB RAM free (G8); a backup set
 (`se-appdata`, `subnautica-saves`, `subnautica-locallow`) newer than the last DE session (`um backup list`); the
 section `Local\DeepEngineers_v1` absent.
5. **Captures.** Pictures come from inside each game (Subnautica `ScreenCapture.CaptureScreenshot`, SE
 `MyRenderProxy.TakeScreenshot`) through capture requests on C10, plus a composed-desktop capture where the
 composite matters (S2.N0). Every capture gets a JSON sidecar (slice, check id, frame, snapshot tick, poses,
 settings, build ids). Every capture is opened and looked at (a 0.33 scaled copy). Liveness: two captures one second
 apart must differ while the scene animates, and the process must have used CPU in between. Subnautica's own UI is
 hidden in every capture (S1.N9).
6. **Logs.** Our plugins log `DE.`-prefixed lines into the game logs and structured JSONL into
 `%LOCALAPPDATA%\DeepEngineers\logs\`. A run fails if any `[Error`/`[Fatal` line (BepInEx) or exception
 (`Player.log`, SE log) comes from our assemblies.
7. **Journal.
 was not covered. Circuit breaker: after three identical failures, stop, write down what is known, change approach.
8. **Derived data stays local.** Mesh/texture caches, octree state maps, census caches, SE colour exports and
 captures live under `%LOCALAPPDATA%\DeepEngineers\` or the scratchpad, never in the repo. Decompiled code is
 never copied into the repo.
9. **Real-run evidence line.** Every real-run result names: SE 1.210.014 (24675677) via Pulsar 2.4.3 Legacy;
 Subnautica 1.22.83031 (20241558) with BepInEx 5.4.23.5; Water Mod 3.23; Windows 11 build (22000 today); RX 6900 XT
 driver 32.0.21045.1000; 2560×1440.
10. **Mutant isolation.** Every mutant that can persist state runs on a named throwaway copy (for example "DE Lab –
 mutant S4.M4", or a copied Subnautica slot), with SE autosave disabled in that world's settings, `um backup
 create` before and `um backup diff` after, and the copy deleted after the run. Subnautica slots live in
 `<Subnautica>\SNAppData\SavedGames` and are synced by Steam Cloud, so creating, uploading and restoring them is an

---
## 4. Slices

Each slice lists: goal; what it builds on and which CONTRACT sections must exist first; work items by part;
stand-ins; the oracle (checks, thresholds, data sources); seeded failures; exit criteria; what it
proves and what it does not. Values (offsets, timeouts, factors, tries) are CONTRACT's; this file points to them.

### S0 Hello bridge

**Status: DONE evidence level *real run*** (both games with both plugins, 7 launches of each side
"S0 REAL RUN" and "S0 EXIT"; results and runbook in `docs/S0-STATUS.md` §3 and §5b). S0.N1–N4, WM-1, T-L1s and N11
green on synthetic tests; N5, G1-E4a (both halves), N6 (autosave caveat), N7, N8, N9, N10a, N10b (partial evidence)
and N12 green on real runs; M1–M8 red where designed (M1, M6 and M8 against the real games). The text below is the
plan as executed, with the S0 discrepancies applied (S0-STATUS §6, ids D1–D46).

**Goal.** The carried-in prototypes are migrated to the CONTRACT layout. Both plugins load under their real loaders,
identify themselves, pass the in-game WM-1 subset, and talk through the shared section: Subnautica creates it (only
when the launcher started it), SE opens it, both write PIDs, heartbeats and generations. A peer with the wrong magic,
version or layout is rejected and never overwritten; a second instance refuses. SE with no Subnautica plays
untouched.

**Builds on:** nothing. **CONTRACT first:** §5 (region map, delivery, every record with offsets), §6, §7
launcher table.

**Carried-in code**:

| File | State today | Against CONTRACT | Decision |
|---|---|---|---|
| `src/DeepEngineers.Wire/SharedSection.cs` | `CreateOrOpen` passes `lpAttributes = IntPtr.Zero` (no DACL, no label); `TryOpen` returns null when absent | §6 security descriptor and creator rules | keep the class; add the SDDL and the creator rules |
| `src/DeepEngineers.Wire/Seqlock.cs` | slot `[int seq][4 pad][payload]`, `maxTries` 8 | §5.3: `seq` is the record's first field at 0x00; ≤ 16 tries | rewrite |
| `src/DeepEngineers.Wire/ByteRing.cs` | `[long write][long read][int cap][int reserved]` inside the region (24 B, both counters on one cache line), `[int length]` messages, −1 skip marker | §5.2/§5.3: head/tail in the ring-control page on separate 64-byte lines, `{u32 type, u32 payloadBytes}`, type-0 pad, capacity = region size | rewrite |
| `src/DeepEngineers.Wire/Native/Kernel32.cs` | file-mapping P/Invoke | needs mutexes, `OpenProcess`, `WaitForSingleObject`, `AllowSetForegroundWindow`, `ConvertStringSecurityDescriptorToSecurityDescriptorW` | extend |
| `src/DeepEngineers.Wire/WireInfo.cs` | `ProtocolVersion = 0` | `wireVersion` 1 | update |
| `src/DeepEngineers.SN/Plugin.cs` | `[BepInPlugin("deepengineers.sn", "DeepEngineers", "0.0.1")]`, logs Awake/600 frames/OnDestroy | S0.N5 greps `Loading [DeepEngineers.SN` | rename the plugin name to `DeepEngineers.SN` |
| `src/DeepEngineers.SN/DeepEngineers.SN.csproj` | `ProjectReference` to Wire; `DeepEngineers.Wire.dll` copied next to the plugin | v1 decision: deployed `DeepEngineers.Wire.
| `src/DeepEngineers.SE/Plugin.cs`, `.csproj` | `IPlugin`, assembly name `plugin`, logs Init with `WireInfo` | | extend |
| `build.ps1` | refuses while a game runs; builds; deploys `plugin.dll` + `DeepEngineers.Wire.dll` to `Legacy\Local\DeepEngineers.SE\`, `DeepEngineers.SN.dll` + Wire + pdb to `BepInEx\plugins\DeepEngineers\`; adds `<string>DeepEngineers.SE.dll</string>` under `<Local>` in `Current.xml` | profile entry scripted (R0.3 closed) | keep; `launcher.py deploy` reuses it and logs SHA-256 into `docs/install/deepengineers-files.txt` |
| `tests/WireSelfTest` | net48 torture and cross-process tests, PASS on the prototypes | pre-contract layout | rewrite against the new layout
| `tests/wire_py/wire.py`, `test_cross.py` | `mmap.mmap(-1, size, tagname=…)` (creates or opens, default security), pre-contract framing | §6: tools open only (ctypes `OpenFileMappingW`), only `fake_sn_host.py` creates (ctypes `CreateFileMappingW` + SDDL) | replace with `tests/wire/layout_py.py` and the stand-ins

**Work items**

| Part | Items |
|---|---|
| Wire | `Layout`: every region and record of CONTRACT §5.4 as `StructLayout(Explicit)` with `FieldOffset`, explicit pads; layout-hash text generator (CONTRACT §6 grammar). `Seqlock` (record-embedded `seq` at 0x00, ≤ 16 tries, keep last good copy). `EntryRing` (C8: SPSC, u64 head/tail on separate 64-byte lines in the control page). `ByteRing` (`{u32 type, u32 payloadBytes}`, 8-byte aligned, type-0 pad at the wrap, reject messages larger than half the ring). Re-sync helpers (consumer tail:= head; producers never zero heads). `Native` P/Invoke (`CreateFileMappingW` with a security descriptor, `OpenFileMappingW`, `MapViewOfFile`, `UnmapViewOfFile`, `CloseHandle`, `CreateMutexW`, `OpenMutexW`, `GetTickCount64`, `QueryPerformanceCounter/Frequency`, `GetCurrentProcessId`, `OpenProcess(SYNCHRONIZE)`, `WaitForSingleObject(h, 0)`, `AllowSetForegroundWindow`, `ConvertStringSecurityDescriptorToSecurityDescriptorW`). `WireMath` (`Mirror`, `MirrorTri`, `Apply`, `ToLocal`, `K`, `staticAxes`, SE-side pose and camera builders; no Unity or VRage types in its core). `SiteV1` constants and `siteHash` copied from `site_v1.py`. `SelfTest` over embedded WM-1 subset records. Hello fields: plugin and wire versions, `siteVersion`, Rs, `siteHash` (C5); `hostKind` = 1 and capability bits (C7), the visual-host boundary of D13 |
| SN plugin | `[BepInPlugin("deepengineers.sn", "DeepEngineers.SN", …)]`; soft `BepInDependency` on Nautilus for load order only (no reference). `Awake` logs only. The bridge start runs once, on the plugin's first player-loop `Update`, never in `Awake`: bridge token (`DEEPENGINEERS_BRIDGE=1` or the launcher's named mutex `Local\DeepEngineers_v1_launcher`) else `DE.Bridge DORMANT` and nothing else; mutex `Local\DeepEngineers_v1_sn` (held → `REFUSE second instance`); then, in bridge mode only (CONTRACT §1.2 order, D17), `DisableProcessWindowsGhosting` and the power-throttling opt-out; create the section by the creator rules (CONTRACT §6); the bridge GameObject with `SceneCleanerPreserve` and `DontDestroyOnLoad`, whose `OnDestroy` writes `snPhase = None` unless `OnApplicationQuit` ran (a destroyed bridge is a failure, not a quit). Heartbeat every frame from `Update` on the unscaled clock; peer process handle polled once per second (unscaled). Logs `DE.Hello` (plugin, wire, site, BepInEx, Unity, game version). Runs the Subnautica subset: 50 `local` records as real Frame/Entity GameObjects, 20 `box` records as real BoxColliders on unit-scale roots, after `Physics.SyncTransforms`. Power-throttling opt-out (`SetProcessInformation(ProcessPowerThrottling)`, return value logged; bridge mode only, above). C11 consumer (dev builds). Stays at the main menu in S0 (`snPhase = Menu`) |
| SE plugin | `IPlugin` main class (`Init`, `Update`). `Init`: a dedicated server leaves the plugin inert; log `DE.Hello`; run the SE subset (50 `pose` records); power-throttling opt-out (allowed before the activation gate is known, CONTRACT §1.1, D30); the activation gate itself is evaluated once per world epoch at the first InWorld tick (multiplayer → `DE.Gate REFUSE`, `gateOpen` 0, no hello; CONTRACT §1.2, D29); `MyPerGameSettings.BasicGameInfo.GameVersion` read at run time → `DE.Version OK\|REFUSE` (never the `SE_VERSION` const); log the `Init` time against `MySession.OnLoading` (critic open question 4). Opens the section once per second, never creates; validates (CONTRACT §6) → `DE.Link UP`, `WAIT` (rate-limited) or `REJECT`. Writes `sePid`, heartbeat (update thread) and generation; mutex `Local\DeepEngineers_v1_se`. `sePhase` from prefixes on `MyGuiScreenLoading.RunLoad`, `MySession.Save(out MySessionSnapshot, …)` and `MySandboxGame.ExitThreadSafe` (`HarmonyPriority.First`). Re-syncs its consumer tails on a new Subnautica generation. Without Subnautica it does nothing else |
| Wire tests | `tests/DeepEngineers.Wire.Tests` (`dotnet test`, targets net48 and net472): offset asserts, seqlock torture, ring wrap/pad/full-policy cases, re-sync cases, the layout-hash vector. The full WM-1 file (both sites) runs in `tests/DeepEngineers.WireMath.Tests` (net48 only: both targets would run on the same.NET 4.8 runtime on this PC; D16). Added in S0: `tests/DeepEngineers.SE.Tests` (net48, the real SE `Link`, `DevChannel`, `Phases` and S0.M5a–c), `tests/DeepEngineers.SN.Tests` (net472, the Unity-free SN link code), `tests/DeepEngineers.Wire.CrossTest` (C# half of S0.N4) |
| tools/tests | `tests/wire/layout_py.py`: independent Python layout written by hand from CONTRACT (never generated from C#), including the layout-hash text, and a cross-language round trip. `tests/standins/fake_sn_host.py` (creates exactly like the SN plugin: ctypes `CreateFileMappingW` with the security descriptor, creator rules) and `tests/standins/fake_se_producer.py` (ctypes `OpenFileMappingW`, open only) (5.8). `tools/spy/de_spy.py` (read-only, `FILE_MAP_READ`). `tools/worldmap/wire_ref.py`: site selector for `vectors` and `selftest`, `y ∈ [−3,040, +350]` for pose and camera samples, rotated-box and `static` kinds (CONTRACT O1); keep both site files. `tests/wm1/make_subset.py` → the two in-game subset files. Port `measure.ps1` from the scratchpad to `tools/measure/` (process names `Legacy` and `Subnautica`). `docs/install/deepengineers-files.txt` (every file we create in a game or loader folder) |
| Launcher | `src/DeepEngineers.Launcher/launcher.py`: `preflight` (CONTRACT §7, including the section-absent check and, at its first run, a backup of Subnautica's `Screenmanager` registry keys), `deploy` (copy built DLLs to the two drop folders, SHA-256 logged, manifest updated), `run` (start `Subnautica.exe -monitor 1` directly with the bridge token, wait for the magic and `snPhase = Menu` (S0; 180 s), close its section handle, start `Legacy.exe` as a child, `AllowSetForegroundWindow(sePid)`, refuse mixed elevation, print PIDs, wait on exits). Changed after the first real run: when Subnautica never reaches the ready phase, `run` does not start SE (it reports and leaves Subnautica running); `--se-anyway` restores the old "SE with the bridge offline" behaviour (CONTRACT §7). Also `manifest`, `stop` (`de.quit`, then `CloseMainWindow`) and `restore-registry` |
| World mod | none |

**Stand-ins.** Both sides emulated: fake SN host + fake SE producer run the whole S0 protocol with no game, including
the synthetic lifecycle test and a restart of each.

**Oracle**

| Check | What | Pass | Data |
|---|---|---|---|
| S0.N1 | layout | C# (net48, net472) and Python offset tables identical; struct sizes equal; layout hash equal from both canonical texts | `dotnet test`, `layout_py.py` |
| S0.N2 | seqlock torture | ≥ 10⁷ writes with payload checksums, 0 torn snapshots accepted | `dotnet test` |
| S0.N3 | rings | wrap and pad exact cases; full policies exactly as CONTRACT §5.1 states per channel (C8: overflow queue up to 4,096, then drop the oldest and count; C3/C4/C12: producer tries once and re-queues; C11: a reply that does not fit is dropped and counted) | `dotnet test` |
| S0.WM-1 | WireMath vs vectors | every record within the header tolerances (positions 1e-6 m in doubles and 1 mm after float32; directions and quaternions 1e-9; scale 1e-12; local round trip 1e-9 m), for the EXAMPLE and SITE_V1 files | `dotnet test` |
| S0.N4 | cross-language | Python-created section (`fake_sn_host.py`) opened by the C# test and the reverse; magic, PIDs, heartbeats, generations read back | script |
| S0.T-L1s | synthetic lifecycle, host in `ReadyIdle` or later (CONTRACT §5.4 counts Menu as a loading phase, so a host parked at Menu gets the 120 s stall limit; D18) | fake SE reports Subnautica **Exited** within the 1 s process poll plus one tick after process exit (the stand-in polls every tick; `--exit-poll-ms 1000` reproduces the real cadence, and SE.Tests check the real C# detector at 1 s + one 16 ms tick; D38) and **Stalled** ≤ 3 s after heartbeats stop; a stall that ends → link epoch +1, and a resend when the stall lasted ≥ 10 s or the counters disagree (CONTRACT §6; the test stalls 11 s; D37); restart → generation +1, tails re-synced, resend | stand-ins |
| S0.N5 | real load | `LogOutput.log`: `Loading [DeepEngineers.SN …]` and our `DE.Hello`; Pulsar `info.log` lists the plugin; SE log has `DE.Hello`; no errors from our assemblies | logs |
| S0.G1-E4a | in-game subsets | `DE.WireSelfTest PASS n=50` (SE) and `PASS n=70` (Subnautica), worst errors logged | logs |
| S0.N6 | real link | `DE.Link UP` on both sides; spy sampled once per second for 60 s shows heartbeat ages < 100 ms outside SE autosaves and generations 1/1 (SE's autosave blocks its update thread for about 150 ms: one 141 ms sample in the real run, during a 156 ms world snapshot; the 3 s stall rule is unaffected) | spy |
| S0.N7 | SE alone | Subnautica not running: SE loads a world and plays; at most one `DE.Link WAIT` per 10 s; no exception; our 1 s plugin log: FrameTime p95 within +3 % of a plugin-less baseline run at the same spot over ≥ 120 samples. The baseline is the same SE launched with `DEEPENGINEERS_SE_MODE=measure` (the plugin runs only its 1 s sampler: no link, patches, hooks or power-throttling change), because SE's own STATISTICS line comes only once a minute (D34) | our JSONL, SE log |
| S0.N8 | load order | plugin `Init` logged before `MySession.OnLoading` | SE log |
| S0. 1 s in the S0 real run); Subnautica quits by itself on SE's exit through the game's own quit path (implemented in S0, D19), otherwise it closes on the `de.quit` control command, then `CloseMainWindow` (`launcher.py stop`); no process or crash handler left | launcher |
| S0.N10 | bridge mode, creator rules | Subnautica started without the token: `DE.Bridge DORMANT`, no section exists; a second bridged instance: `REFUSE second instance`, the first session's region checksums unchanged. Evidence note (real run, N10b): Unity and Steam let a second `Subnautica.exe` start (it ran 60 s and closed cleanly); its `REFUSE` line cannot be read from `LogOutput.log`, which the first instance holds, so the pass rests on `snPid` and checksums. To do: open the SN JSONL (`sn-<timestamp>-<pid>.jsonl`) before the mutex check, so a refused instance leaves its own record | spy, logs |
| S0.N11 | ring re-sync | `fake_sn_host.py` restarted while `fake_se_producer.py` fills C3/C4 and a fake SE consumer drains C8: no message misparsed (every payload checksum valid), generation +1, consumer tails re-synced, resend completes | stand-ins |
| S0.N12 | version pin at run time | a dev-build test hook sets `MyPerGameSettings.BasicGameInfo.GameVersion` to 1210015 before the check and restores it after: `DE.Version REFUSE`, display bridge off, plugin otherwise healthy | SE log |

**Seeded failures**

| Mutant | Expected |
|---|---|
| S0.M1 (required) fake SN host publishes magic `DEEQ`, then (second run) version 2 | real SE logs `DE.Link REJECT`, writes nothing (spy: `sePid` stays 0, region checksums unchanged) |
| S0.M2 seqlock writer that skips the odd step | S0.N2 red (torn copies accepted) |
| S0.M3 ring writer without the pad record | S0.N3 red |
| S0.M4 WM-1 mutants from gap-1 §5: mirror dropped; triangle swap dropped; `mapdir` at the entity origin instead of P*; `s` dropped or applied to y; ω without the sign flip; double mirror on the Subnautica side | S0.WM-1 red for each |
| S0.M5a detector ignores process exit (no `WaitForSingleObject`) | the "Exited" part of S0.T-L1s red (also on the real C# `Link` in SE.Tests: not Exited within 2.5 s) |
| S0.M5b timeout constant 30 s instead of 3 s | the "Stalled ≤ 3 s" part of S0.T-L1s red |
| S0.M5c liveness judged on "heartbeat value changed" with a reused PID | the restart-detection part of S0.T-L1s red |
| S0.M6 a foreign creator pre-creates the section with layout 2, or with a different size (implemented as `fake_sn_host.py --mutant layout2\|size --passive`, a creator that holds no mutex; D40) | real SN plugin logs `REFUSE`, region checksums unchanged |
| S0.M7 creator that zeroes the producer head words on re-initialisation | S0.N11 misparses (red) |
| S0.M8 version check against the compile-time `SE_VERSION` const | S0.N12 red (no REFUSE) |

*** See section 10: confirm the approval of the BepInEx plugin-folder write already made on (and of the plugin-folder entry in the uninstall manifest); the Pulsar profile entry is written by
`build.ps1`; approve a first `subnautica-saves` backup (the preflight requires it); set both games to "only update when
I launch it"; approve the launches (counted, baseline included).
"S0 REAL RUN").

**Exit.** All checks green; every mutant turned its check red; one real session of each game with both plugins,
closed cleanly

`Player.log` and Unity analytics).
on F115, and the 21:05/21:12 lines are labelled pre-contract synthetic tests).

**Proves** (*real run*): both loaders load our assemblies (with `DeepEngineers.Wire.dll` beside them); a P/Invoke named
file mapping with an explicit security descriptor is shared between.NET 4.8 and Unity 2019.4 Mono on this PC;
WireMath gives identical results in both real runtimes; SE is untouched when Subnautica is absent; a normal
Subnautica launch stays vanilla. **Does not prove:** anything visual, any timing in world, any behaviour under load.

---

### S1 Pose bridge

**Status: DONE evidence level *real run*** (both games with both plugins on the pinned versions, DE Lab
world with `DE_4546B` at the integer centre, host save `slot0000` in Freedom
record in `docs/S1-STATUS.md` §10). Session A: oracle table PASS, 99 rows, 0 FAIL, 0 MISSING; sessions C and D:
S1.G1-E5 at 50° and 90°, `--g1e5-coverage` PASS [50, 70, 90]; session B (`tests/s1/session_b.py`): drive gate,
S1.N9 capture, S1.N11, S1.N12 spy and S1.N3 PASS; S1.G2-E3 24/24 by `de.g2e3`; S1.
Every mutant red on the real games (S1.M10 partial: the host save was made outside the lifepod). Open: S1.N8's
N10 displacement sampler (CONTRACT O15).
The text below is the plan as executed, with the S1 discrepancies applied where they change a check (S1-STATUS §5,
ids D47–D108).

**Goal.** SE's camera drives Subnautica's ghost camera in a Freedom host save that is never saved. The site binds
through the Water Mod API. The map is right in the real games (WM-2, WM-3, WM-4, G1-E5). The ghost Player, FOV and
hidden UI follow one rule each. Latency, streaming and world-load times are measured. The display is not switched yet
(S2): Subnautica runs under SE, and evidence comes from logs and captures taken inside Subnautica.

**Builds on:** S0. **CONTRACT first:** snapshot fields (§2.8, §5.4 C1), site record and world settings (§3),
interpolation policy, C11 command names, settings touched (§1.1).

**Work items**

| Part | Items |
|---|---|
| World mod (part 1) | Own files in `%APPDATA%\SpaceEngineers\Mods\DeepEngineers.World\`: `Data\PlanetGeneratorDefinitions.sbc` with `DE_4546B` (spawn size 120,000; HillParams −0.05/0.12; SurfaceGravity 1.0, falloff 7; EarthLike atmosphere numbers: Breathable, OxygenDensity 0.9, Density 1.0, LimitAltitude 2.0, MaxWindSpeed 80; no SurfaceDetail, EnvironmentItems, ore mappings or WeatherGenerators), six all-zero 16-bit 2048² heightmaps and minimal material maps (own art; the minimal file set is HYPOTHESIS until the planet spawns), `Data\WaterConfig.xml` with a `PlanetConfig` for `DE_4546B`: Radius 1.0526316, WaveHeight 0, TideHeight 0, Volumetric false, CrushDamage 0, CurrentSpeed 0, EnableFish false (applied when the planet is first spawned; the save keeps the sea afterwards). Lab world "DE Lab" (Creative; the abyss crush zone is inactive there, only the floor guard acts) with Water Mod, Text HUD API and this mod; `DE_4546B` spawned once at an integer position |
| SE plugin | Session component: registers the 50271 handler in `LoadData`; on the dictionary, `VerifyVersion(22, "DeepEngineers")`, `WaterData.Version` by reflection, delegate key/type checks. Site binding: C after `MyEntities.Load`; validation at the first `UpdateBeforeSimulation`, retried ≤ 600 ticks (one `DE_4546B`, `HasWater`, Rs bits, `GetDepth` = `y` within 0.01 m, crush/wave/tide/current 0) → `DE.Site WAIT\|OK\|REFUSE <reason>`; re-check every 10 s; `/de site repair`; full site record logged with `siteVersion`; the S0 hello carried the compiled `SiteV1.RsExpected` (D31); from S1 the Rs it carries is the one validated through the Water Mod API (CONTRACT §3, §6 "Hello"). World-load timing (from the `RunLoad` prefix to the first update) and `LoadingStep` heartbeats (both exist since S0). Snapshot writer in a Harmony postfix on `MySandboxGame.PrepareForDraw` (camera final there, same tick as grids; VERIFIED order): tick (`MySession.GameplayFrameCounter`), `qpcCapture`, simulation time, flags (in world, paused, first person, teleported, character present/seated/swimming), `camPos = F(Pcam)`, `camRot`, `vFovDeg` (`FovWithZoom`), near plane, `pelvisSn`. C11 test commands and the `/de` chat relay: `de.teleport` (spectator to F⁻¹ of an SN point with a heading via `MySpectator.Static.Position`, `SetTarget`; VERIFIED members), scripted WM-3 path, look along E/N/down, 30° yaw step, log the screen pixel of a world point through `MySector.MainCamera.ViewProjectionMatrix` |
| SN plugin | Auto-load the host save "DeepEngineers-Host" (`uGUI_MainMenu.LoadGameAsync`); on this PC it is `<Subnautica>\SNAppData\SavedGames\slot0000`, `gameinfo. Assert game mode Freedom and `GameModeUtils.IsInvisible == false`, else `DE.HostSave REFUSE`. Never save: prefix on `IngameMenu.SaveGame` (D7) and, as a second guard, on `SaveLoadManager.SaveToDeepStorageAsync`; each blocked attempt logs `DE.SaveBlocked`. Ghost: `Player.main.EnterLockedMode(null)`; the plugin moves the Player root each frame to C1 `pelvisSn` (character present, not seated, and the camera is its own first- or third-person view) or to the camera; outside the dive zone (C5 zone FAR or off-site, ρ∞ > 7,900 m, y > +350 m or y < −7,900 m) the camera and the Player are held, not driven; moved out of the lifepod at load; asserts `escapePod == false` and `currentSub == null` after the first frame; dummy `InputHandlerStack.main.Push`, `GameInput.ClearInput` every frame, `MainCameraControl` disabled, `Player.FreezeStats` asserted every second, body renderers hidden. FOV: `PDACameraFOVControl.enabled = false` (never destroyed), `SNCameraRoot.main.SetFov` on change, `fieldOfView` asserted per frame. Near: `minNearClip = near`, then `SetNearClip(near)`. UI hidden: `GUIController.SetHidePhase(HidePhase.All)` (re-asserted after loads), PDA voice, subtitles and notifications suppressed, `radiationAmount` zeroed and `RadiationsScreenFX` disabled. "Run in background" forced on in memory before the focus hand-over (`ApplicationFocus.OnRunInBackgroundChanged`), original logged. Camera rig unparented and driven in `ManagedUpdate` `LateUpdateLast`, re-sampled once per frame in `Camera.onPreCull` for `MainCamera.camera` only: position interpolated on QPC stamps with delay D, orientation = latest snapshot. Outcome counters (bracketed / extrapolated / held / torn) logged every 300 frames. `presentedSeTick` = newer bracket tick, stamped at end of frame. Logged each second: `Player.main.IsUnderwater`, the fog's camera-inside flag, biome, depth, Player-to-camera distance; whether the Player carries `OutOfBoundsWarp` (G7). Streaming telemetry after each camera jump (`IsWorldSettled`, `IsRangeActiveAndBuilt` for the camera box and the forward box, ±16 m since the S1 real runs). Water-surface displacement sampled at the camera. Every timer on the unscaled clock |
| tools/tests | `tests/s1/check_wm.py`: recomputes F(P) for every logged sample with `worldmap_ref.py` + `site_v1.py` (independent of WireMath). `tests/s1/latency.py`: histograms and step response from both JSONL logs. `tests/s1/fmod_events.py`: FMOD events started per 60 s hold |
| Launcher | `run --continue` (SE loads the last world) exists since S0; from S1 `run --ready-phase ReadyIdle` waits for `ReadyIdle` (180 s), with the S0 abort rule on timeout (`--se-anyway` to start SE with the bridge offline; CONTRACT §7) |

**Stand-ins.** Fake SE producer drives the real Subnautica plugin along scripted WM-2/3/4 paths (Subnautica only).
Fake SN host consumes the real SE snapshots and checks the WM numbers (SE only).

**Oracle**

| Check | What | Pass |
|---|---|---|
| S1.G2-E3 | planet behaves as computed (no display bridge needed) | programmable block `TryGetPlanetElevation(Sealevel)` 0.00 ± 0.05 at the water; natural gravity 9.81 from the surface down to 2 km depth; atmospheric thruster at 100 % on the surface; oxygen 90 %; `/wradius` (and `/wsettings`) prints 1.052632, the setting to 7 significant digits (`/wrradius` prints the camera's relative radius; S1 D47; bit-exactness is S1.G2-E4); no Water Mod crush damage on a grid at 1,000 m; flat floor at 3,000 m depth. Then, on copies (S1 D52): (a) the world mod's `WaterConfig.xml` changed after the spawn leaves the saved sea unchanged and `DE.Site OK`; (b) `/wcrush 0.5` makes the bridge report `DE.Site REFUSE` naming `crushDamage` (within 10 s, by the re-check). Real run: `de.g2e3` 24 PASS, 0 FAIL, 2 MANUAL |
| S1.G2-E4 | Rs bits | `GetPhysicalData(planet).Item2` == 60000.00390625f exactly (the API key is `GetPhysicalData`, S1 D57). Real run: bits 0x476a6001 |
| S1.N13 | site binding timing | 50271 dictionary received by the `LoadData` handler; `DE.Site OK` within 600 ticks of the first update |
| S1.WM-2 | known point: spectator at F⁻¹(−1029, −150, −1116), facing SN north, still ≥ 2 s | Subnautica camera position within 1 cm of the oracle's F(P); forward within 0.05° of (0, 0, 1); SE `GetDepth` −150.00 ± 0.01 (= `y`); Subnautica `Ocean.GetDepthOf` 150.00 ± 0.01 (= −`y`); biome `grandReef`; capture shows the reef below (looked at) |
| S1.WM-3 | curvature run: 2 km great circle east from the anchor at SE altitude −100 m, at least 100 samples by tick | Subnautica y = −100.00 ± 0.01 at every sample, x within 1 cm of the oracle (numeric only: the camera passes through rock) |
| S1.WM-4 | orientation at the anchor, a tile corner and the deepest floor (198, −1657, −518), looking along local E, N and down | forward (1,0,0), (0,0,1), (0,−1,0) within 0.05°; mapped SE gravity direction (0,−1,0) within 0.05° |
| S1. exit = `check_wm.py --g1e5-coverage` over three sessions); a point on SE's view axis 10 m ahead | Subnautica pixel − SE pixel = the value of `(s − 1)/2 · sin 2α` projected with the logged FOV (about 4.3 px at 500 m, 14.4 px at 1,657 m, 17.4 px at 2,000 m at 45° with a 70° vertical FOV) within ±1 px; 0 ± 0.5 px for level and vertical looks |
| S1.N1 | transport histogram over 10,000 frames (`qpcCapture` → `qpcUsed` in `onPreCull`) | p95 ≤ 25 ms; extrapolated frames < 1 % at a steady 60 Hz; 0 torn snapshots accepted (thresholds HYPOTHESIS, ship-render §6.6) |
| S1.N2 | step response: 50 scripted 30° yaw steps; latency = first `onPreCull` with a yaw change > 15° minus qpc(T) | p50 and p95 recorded; p95 ≤ 33 ms (two SE ticks; HYPOTHESIS) |
| S1.N3 | streaming after camera jumps of 0.5, 1 and 2 km | time to `IsWorldSettled` (on the busy → idle edge) and to `IsRangeActiveAndBuilt` recorded; sizes the Prime lead time. Real run, settled / camera box / forward box: 0.5 km 1,237 / 621 / 706 ms; 1 km 1,246 / 714 / 822 ms; 2 km 3,065 / 982 / 909 ms; so the near field is built within 1 s and the streamer is idle within ≈ 1.3 s up to 1 km and ≈ 3.1 s at 2 km (ARCHITECTURE §4.2 "Prime lead time"). Snaps without a move (the SE teleport latch's second frame) start no probe |
| S1.N4 | host save untouched | SHA-256 of every file in the host slot under `<Subnautica>\SNAppData\SavedGames\` identical before and after; a test-triggered `IngameMenu.SaveGame` logs `DE.SaveBlocked` |
| S1.N5 | Freedom assertion | `DE.HostSave mode=Freedom invisible=false` |
| S1.N6 | locked-mode visuals and audio (Subnautica open question 4) | WM-2 capture shows underwater fog and caustics; during a 60 s hold at −150 m, the FMOD events started (`WaterAmbience`, `BreathingSound`, PDA voice) are logged with `IsUnderwater` left false and with each narrow patch; `FreezeStats` held throughout |
| S1.N7 | FOV hold, 60 s, with SE's FOV different from Subnautica's options FOV | `mainCamera.fieldOfView` equals C1 `vFovDeg` within 0.01° on every frame, read before the plugin's own per-frame assertion corrects it, and the plugin's drift counter stays 0 |
| S1.N8 | ghost Player rule, at a known point in first and third person (swimming) and seated | Player root within 0.05 m of the SE pelvis when swimming, of the camera when seated or in spectator; `escapePod == false`, `currentSub == null`; biome equals the camera's biome; `IsInsideWalkable` false outside rooms; `CanBeAttacked` true while swimming |
| S1.N9 | Subnautica UI hidden | a capture at a known spot shows no uGUI pixels (diff against a calibration of the scene); no PDA voice event in 60 s; 0 `ErrorMessage` lines on screen; C7 `uiHidden` |
| S1.N10 | water surface (CONTRACT O15) | maximum displacement of Subnautica's water surface at the camera over 60 s recorded; `Ocean.GetOceanLevel` = 0; if the trough exceeds 0.20 m the entry threshold is moved below it. **Open after the real runs:** `GetOceanLevel` = 0 holds, but the sampler read ±20 m (its texel-mapping HYPOTHESIS, S1 D84, does not hold: a tooling bug); fixed and re-run in S2 |
| S1.N11 | run in background | started with the option off (test switch, in memory): the plugin forces it on; `FreezeTime` holds no `ApplicationFocus` entry for 60 s after the focus hand-over: `focusFreezer=false` and `timeScale` ≠ 0 in every `DE.S1 sec` (`snFrame` keeps advancing even without the forcing, so it does not discriminate; S1 D76). Real run: 0 of 236 lines with a focus freeze |
| S1. the spy sees `sePhase = LoadingWorld` within 1 s of `RunLoad`, compared with the RunLoad-prefix log line, not with `DE.Phase Menu -> LoadingWorld`, which is written when `RunLoad` returns. Real run: +0.027 s; lab world 22.9–29.6 s, largest world 23.4 s |

**Seeded failures**

| Mutant | Expected |
|---|---|
| S1.M1 naive tangent-plane map | S1.WM-2 misses by about 19.2 m (gap-2 §7); S1.WM-3 drifts to about −133.3 m at 2 km |
| S1.M2 camera published from `IPlugin.Update` (previous tick) | S1.N2 shifts by about one SE tick (16.7 ms) against the `PrepareForDraw` baseline. S1.N1's transport does not shift by a tick: the mutant stamps `qpcCapture` when it publishes the stale camera |
| S1.M3 host slot in Creative (a throwaway copy, rule 10) | `DE.HostSave REFUSE`; no camera driving |
| S1.M4 save guards disabled, on a throwaway copy of the host slot (rule 10) | S1.N4 reports changed hashes (restore from the `subnautica-saves` backup after) |
| S1.M5 Rs read plus one ulp | `DE.Site REFUSE Rs` |
| S1.M6 `PDACameraFOVControl` left enabled | S1.N7 red (the FOV drifts to the options value) |
| S1.M7 hide phase left at None | S1.N9 red |
| S1.M8 run-in-background enforcement off, option off | S1.N11 red: `focusFreezer=true` and `timeScale=0` after the focus hand-over (`snFrame` keeps advancing: Unity still calls `Update`; S1 D76). Real run: RED |
| S1.M9 50271 handler registered late: at the first `UpdateBeforeSimulation`, after every `BeforeStart` | `DE.Site REFUSE API not received`; S1.N13 red |
| S1.M10 Player root left where `EnterLockedMode(null)` leaves it | S1.N8 red (`escapePod` true, biome of the save spot). Real run: partial, the `NOT MOVED` assertion red; the `escapePod` branch needs a host save made inside the lifepod |

*** Approve creating `%APPDATA%\SpaceEngineers\Mods\DeepEngineers.World\` (a write outside the repo)
and the lab world; spawn `DE_4546B` once. Create the Subnautica host
save: new game, Freedom, save once, quit; then approve `um backup create "<Subnautica>\SNAppData\SavedGames" --name
subnautica-saves`. Approve the throwaway host-slot copies for S1.M3/M4 (inside the game folder, uploaded by Steam
Cloud) and their deletion.N12). Fly a small ship for the G2-E3
thruster, oxygen and 1,000 m checks if they are not scripted. Look at the WM-2 capture. Approve launches.

**Exit.
*
every mutant red (S1.M10 partial), and the numbers recorded: S1.N1 25,662 frames, extrapolated 0.078 %, 0 torn,
transport p95 ≈ 16 ms; S1.N2 step response p50 5.08 / p95 7.65 ms; S1.N3 as in its row; O11 Subnautica `ReadyIdle`
18.4–20 s, SE world load 22.9–29.6 s (lab) and 23.4 s (largest world). Remaining, none of them blocking S2: the
N8 swimming and seated poses (the character must first be moved to the site with
`tools/lab/set_planet_position.py`, then one click on SE's conversion prompt, section 5.
`wm2.png`/`n9.png`, and the S1.N10 sampler (O15).

**Proves** (*real run*): the map, the frame conventions and the camera channel are right in both real games; the
site binds through the Water Mod API; the host save is Freedom and stays untouched; the planet matches the site
record. **Does not prove:** what the player sees on the composed screen, input, audio mixing, anything about grids.

---

### S2 Display + input + audio

**Goal.** First, prove that Subnautica keeps rendering while SE covers it in the Prime setup (S2.N8). Then design F end
to end: SE on top and focused; in Prime the DWM margins go on while SE still draws its opaque scene; underwater SE
stops drawing its 3D scene, so Subnautica shows through with SE's HUD and menus on top. One state machine drives
display, Subnautica's Idle/Track/Full modes and audio. Input never leaves SE. The fallback A′ harness exists and has
run once.

**Builds on:** S1. **CONTRACT first:** C5 and C7 fields (view state, approach mode, overlays, perf
mode, `zOrderOk`, `marginsOn`, `crossingTick`, `snRect`, applied frame-rate pair, `uiHidden`, `areaBuilt`), §1.1
settings touched, the audio table, the state thresholds (ARCHITECTURE §4, with `y` negative underwater). Checked
against CONTRACT on before any S2 code; layout 1.0 (hash `0x4f3a35f6fc1eaf31`) already carries every field,
so S2 changes no record:

| Item | Where | State |
|---|---|---|
| C5 `viewState` (0x10), `approachMode` (0x14), `overlays` (0x1C) | CONTRACT §5.4 C5 and "C5/C7 semantics for S2" | fields present; meanings written (`approachMode` only while Approach) |
| C5 `perfMode` (0x28), `targetFps` (0x2C) | same | present; view state → perf mode → Subnautica recipe → `snPhase` table, and the `targetFps` → (`vSyncCount`, `targetFrameRate`) rule written |
| C5 flags `seeThrough` (3), `marginsOn` (5), `zOrderOk` (6), `seMixActive` (7), `guiScreenOpen` (8) | same | present; meanings written |
| C5 `crossingTick` (0x78) | same | present; made exact: the first tick of the descent with `y ≤ −0.20 m` |
| C5 `seRect` (0x58), C7 `snRect` (0x68), C7 `snHwnd` (0x60) | same | present; `GetWindowRect` {left, top, right, bottom} convention written |
| C5 `snBusTarget` (0x30), `crossfadeMs` (0x34), `camVelSn` (0x6C) | same | present; linear ramp on the unscaled clock written |
| C7 applied frame-rate pair: `perfModeApplied` (0x0C), `vSyncCountApplied` (0x7C), `targetFrameRateApplied` (0x80) | same | present |
| C7 `snPhase` ReadyIdle / WarmingUp / Active | CONTRACT §5.4 C0 phase list and the mapping table | mapping written (S1 never left ReadyIdle, S1 D73) |
| C7 flags `uiHidden` (6), `areaBuilt` (4, ±16 m), `rectOk` (5), `frozen` (7), `runInBackgroundForced` (8), `streamingSettled` (1); `areaBuiltSeTick`/`Pos`, `presentedSeTick` | same | present; meanings written (`areaBuilt` ±16 m from the S1 real runs; `presentedSeTick` not advanced while the cameras are off) |
| §1.1 settings touched | CONTRACT §1.1, rows marked **(S2)** | present; rows added: Subnautica process priority, cameras, freeze and mix |
| The audio table | CONTRACT §5.4 (with the C5 values), ARCHITECTURE §5.3 | present; the pause and GUI-screen rows separated (a GUI screen does not freeze Subnautica) |
| The state thresholds | ARCHITECTURE §4.2, "State thresholds" table (the only copy) | written once |

Measurements carried from S1 (real runs): Prime lead (S1.N3, below), ±16 m `areaBuilt`, pose transport p95 ≈ 16 ms
(S1.N1), `ReadyIdle` 18.4–20 s and world loads 23–30 s (O11), `GetDepth = y` exactly (D56).

**State machine (owned by the SE plugin, published in C5).** Critic C8 merged the two probes; the world-mapping
version is used.

| State | Enter when | SE | Subnautica (C5 `perfMode`) | Audio |
|---|---|---|---|---|
SE per config |
| Approach / Track | dive planet, in zone, `y < +300 m` | normal, opaque, margins 0 | Track: unfrozen, ghost transform at the mapped pose, streaming follows, cameras off, (0, 10) | as Space |
| Approach / Prime | Track and; ARCHITECTURE §4.2 "Prime exit") | scene drawn, alpha filled to 1 after the sprites, margins −1, z-order re-asserted | Full: cameras on, full rate | as Space |
| Underwater | Prime and `y ≤ −0.20 m` and `ρ∞ ≤ 7,800 m` and `snReady` and `zOrderOk` and `presentedSeTick ≥ crossingTick`; minimum dwell 0.25 s | scene skipped (backbuffer (0,0,0,0)), margins stay on | Full: (1, any) at 165 Hz, or (0, 120) if CPU-bound | Subnautica 1.0; SE game 0.7 × config; SE music 0; Water Mod underwater sound 0; 300 ms crossfade |
| Surfacing | `y > −0.05 m` (after the 0.25 s dwell), or zone exit (7,900 m), leaving the site, peer stalled or exited, or `¬snReady` after a teleport (the last four apply at once, no dwell) | scene drawn first; margins stay while in Prime | Full until the next state | crossfade back |

Every threshold of this table is in ARCHITECTURE §4.2's "State thresholds" table (one copy). Zones (SN coordinates of
the camera): CORE (`ρ∞ ≤ 1,950 m`) and TILE VOID (inside ±2,048 m), OUTER VOID (to ±7,800 m, hysteresis 7,900 m), FAR
(beyond: SE's own Water Mod view, D13). A GUI screen without pause
(inventory, terminal) → the plugin writes no SE volume, Subnautica is not frozen. SE not foreground → SE mutes itself
(`EnableMuteWhenNotInFocus`), the plugin writes no SE volume, Subnautica mutes. Crossing order: margins already on
since Prime (at least one presented frame), then the scene is skipped; leaving: scene first, margins off only when
leaving Prime.

Subnautica's phase follows the recipe it applied (CONTRACT §5.4): `snPhase` ReadyIdle in Space (Idle), WarmingUp in
Track and in Prime until the Full recipe has rendered a frame, Active in Prime, Underwater and Surfacing; `snReady`
needs Active. The S1 camera-drive rule (ghost Player rule, drive gate outside the dive zone) stays in every state; the
recipes add the freeze, the cameras, the frame-rate pair and the mix. **Prime lead time:** the 2 s predicted-crossing
lead is kept after S1.N3 (real run): in a continuous descent Track already streams at the mapped pose and the ±16 m
near field builds in < 1 s, so Prime only switches the cameras on; after a teleport or jump `streamingSettled` gates
the dive (≈ 1.3 s up to 1 km, ≈ 3.1 s at 2 km). S2.N7 checks both.

**Work items**

| Part | Items |
|---|---|
| SE plugin | Every call on `seHwnd` queued to the render thread (the window belongs to it; executed in the `Present` hook). Window set-up only while linked and with the activation gate not refused (CONTRACT §1.1, §1.2; everything the display bridge does also needs the bound site and the acknowledged hello): `HWND_NOTOPMOST` and a postfix on `MyGameWindow.OnModeChanged` undoing `TopMost`, restored on unlink; handle from `MyWindowsWindows.WindowHandle`. Scene skip: prefix on `MyRender11.FullDraw` clearing `m_drawScene` (SE's else-branch clears the backbuffer), or a `DrawScene` prefix with `AccessTools` delegates. Alpha fill after the sprites in Prime (HYPOTHESIS mechanism). Margins on Prime entry. z-order re-assert: `AllowSetForegroundWindow(snPid)`, `SetWindowPos(snHwnd, seHwnd, NOMOVE\|NOSIZE\|NOACTIVATE\|ASYNCWINDOWPOS)`, verification by the walker on the next frames, ≤ 3 retries → `zOrderOk`. When the link first comes up (SE opens the section once per second from `Init`, so normally at its main menu; CONTRACT §1.1 and §7 "Start"): `SetForegroundWindow(seHwnd)` on the render thread, checked, retried on the first activations, HUD hint. State machine as a pure function in Wire, driven by `y` from the map (`WaterModAPI.GetDepth` only as the 60-tick cross-check), with every threshold from ARCHITECTURE §4.2; it replaces S1's zone-based `perfMode` rule (S1 D65) and writes the C5 fields of CONTRACT §5.4 "C5/C7 semantics for S2" (`viewState`, `approachMode`, `overlays`, `perfMode`/`targetFps`, `snBusTarget`, `crossfadeMs`, `crossingTick`, `seRect`, flags `seeThrough`, `marginsOn`, `zOrderOk`, `seMixActive`, `guiScreenOpen`). Exit polling every tick while margins are on; `IsWindow`/`IsWindowVisible(snHwnd)` each render frame. Audio: postfix on `MySandboxGame.UpdateSound` writing `MyAudio.Static` volumes only while focused, unpaused, with no GUI screen open and not loading; prefix on `MyGuiScreenOptionsAudio.Save`. Water Mod: emitter mute (postfix on its sound component) or the in-memory fallback with `restore.json` and the `MySession.Save(out …)` prefix/finalizer; never `/wvolume`. F8 dev hotkey for T-D1. Backbuffer alpha readback (dev only). Sprite filter hook for any full-screen opaque sprite found in S2.N1. Start-up composed-pixel measurement for the display mode. Fallback A′ behind a config switch: lower/raise instead of alpha, `DrawEnabled` getter patched false (A′ only) |
| SN plugin | At `ReadyIdle`: `ShowWindow(SW_HIDE)` → add `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW` → `SetWindowPos(…FRAMECHANGED)` → `ShowWindow(SW_SHOWNOACTIVATE)`; fallback `ITaskbarList:DeleteTab`; `HWND_BOTTOM`; `AllowSetForegroundWindow(ASFW_ANY)`; styles re-applied after every resolution or `DisplayManager` change and verified each second. Monitor and rectangle: verify display index (launched with `-monitor 1`) and rect = C5 `seRect`, compared every second → `rectOk`; correction `Screen.SetResolution(2560, 1440, FullScreenWindow)`. Idle, Track and Full recipes with `(vSyncCount, targetFrameRate)` set directly and re-asserted every frame, the user's values saved at load and restored on quit, never persisted. Mix with `RuntimeManager.MuteAllEvents` and the bus volume re-applied after every `FreezeTime` transition (or a VCA). Input silencing (from S1) plus logging of Input System device state. Patch `LargeWorld.GetBiome` to return "void" outside the tile (world-mapping §1.3: unpatched, it wraps east of the tile and returns null north and south). Publish `snHwnd`, `snRect`, `snReady` flags (`areaBuilt` with the ±16 m camera and forward boxes of S1, `streamingSettled` on the busy → idle edge, `rectOk`, `uiHidden`, `frozen`), `presentedSeTick` (not advanced while the cameras are off), applied frame-rate pair and `perfModeApplied`; `snPhase` ReadyIdle / WarmingUp / Active by the applied recipe (CONTRACT §5.4). Keep the S1 camera-drive rule and drive gate in every view state. Carried from S1: a dev `de.testUi` command that raises one `ErrorMessage` line and one PDA notification, so an S1.M7 variant that also skips the UI sinks can show S1.N9's sink rows red, now that `uiHidden` gates `snReady`. Calibration pattern command (full-screen solid colour UI, the only canvas left visible). `Quitting` handshake (≤ 100 ms wait for SE's acknowledgement) |
| tools/tests | `tools/capture/desktop_shot.ps1` (GDI desktop capture, ported from the scratchpad `shot.ps1`; T-D0 proved it sees DWM composition). Composed video method chosen in S2.N0 (HYPOTHESIS: ffmpeg Desktop Duplication `ddagrab` or gfxcapture in monitor mode). T-D1 driver and analysis (walker, black-frame detector, alpha readback). `tools/audio/peaks.py` (pycaw peak meters per process session every 20 ms; needs approval for the pip download, not given yet: the tool guards the import, says so clearly and keeps a `--selftest` that runs without pycaw). The S1.N10 displacement sampler fixed and re-run (O15; its texel mapping read ±20 m). C11 sessions scripted like `tests/s1/session_b.py` through `tools/lab/se_cmd.py` (section 6). State-machine unit tests with scripted `y` traces (including the two S2.N2 traces). Re-run of `tools/probe-dwm-standins` (T-D0) with a translucent case and a runtime-restyle variant; a covered-render stand-in (flip-model window fully covered by an opaque bit-blt window, Present duration and interval logged). UI Automation script for taskbar and Alt+Tab entries; a script placing Notepad between the games |

**Stand-ins.** `tools/probe-dwm-standins` rehearses the T-D1 harness logic and the covered-render gate with stand-in
windows (no game). The fake SE producer drives the Subnautica plugin through all states (Subnautica only).

**Oracle**

| Check | What | Pass |
|---|---|---|
| S2.N8 (first gate) | covered render: (1) stand-in: flip-model window fully covered by an opaque bit-blt window for 10 min, then under a top window with margins on and alpha 1 for 10 min; (2) real: Subnautica under SE in Prime (margins on, scene alpha 1) for 60 s, and the same with margins off for comparison | with margins on: Subnautica `snFrame` and heartbeat advance at ≥ 25 fps, no heartbeat gap > 1 s, `presentedSeTick` age ≤ 4 ticks on ≥ 99 % of SE ticks; the margins-off numbers recorded, with the S1 `camBuiltHalf` ladder (what the streamer has built around the camera) logged in both runs for the `areaBuilt`-only alternative. The result settles CONTRACT O9 (or selects an alternative of ARCHITECTURE §4.4) |
| S2.T-D0 | synthetic DWM proof re-run; translucent case (50 % white panel over a dark lower window); runtime-restyle variant | PASS as before; composed translucent pixel vs the expected value recorded (R2.5) |
| S2.N0 | composed-capture tooling | with see-through on, the Subnautica calibration colour appears in the composed capture in SE's transparent area; with see-through off it does not; two captures 1 s apart differ while Subnautica animates |
| S2.T-D1 | F8 toggled 50 times, then inventory (I), terminal (K), toolbar config (G), ESC, chat (Enter), F10 while see-through | SE foreground 50/50 (`GetForegroundWindow == seHwnd`); the z-order walker (ARCHITECTURE §5.1) finds `snHwnd` directly under `seHwnd` at every toggle, skipped windows logged; DWM call returns S_OK; crossing → `presentedSeTick ≥ crossingTick` ≤ 60 ms and skip frame → composed ≤ 2 refreshes (≤ 12 ms at 165 Hz), by present timestamps and composed frames; no black frame at any switch (detector on composed captures); with no menu open < 15 % of backbuffer pixels have alpha > 0 (the opaque HUD footprint measured at 10.2 % on the real runs; 2 % was a guess); with the scene drawn (Prime) < 0.1 % have alpha < 1; SE update time per toggle < 1 ms; SE simulation ratio ≥ 0.98 |
| S2.N1 | full-screen sprites (G5): blood overlay `MyGuiControlBloodOverlay` (take damage in a Survival copy), camera/turret overlay `MyHudCameraOverlay`, medical screen, loading screens | each keeps < 2 % opaque coverage or is filtered |
| S2.T-I1 | human-driven 60 s script, twice (see-through off, then on): walk, jetpack, mouse look, tool, toolbar scroll, number keys, inventory | SE event counts and summed mouse delta within 1 % between runs; Subnautica `GameInput` all zero; Input System devices disabled or zero |
| S2.T-A1 | 10 dive/surface cycles, one ESC underwater, one ESC in Space then resume, one Alt+Tab away and back while underwater, one SE save made underwater; peak meters of `Legacy.exe` and `Subnautica.exe` every 20 ms | underwater: Subnautica peak > 0; above water: Subnautica peak = 0; crossfades overlap, no gap with both silent > 100 ms; no SE music underwater, also after the Alt+Tab return (SE music 0, SE game 0.7 × config); both silent while away; after ESC in Space and resume, Subnautica stays frozen (`Time.timeScale == 0`) and silent; with the inventory open underwater (a GUI screen without pause) Subnautica is not frozen and the SE mix is unchanged (CONTRACT §5.4); `WaterClientSettings.xml` byte-identical (SHA-256) before and after |
| S2.N2 | hysteresis, two traces: (a) spectator bobbing ±0.05 m around `y` = −0.125 m (inside the band) for 30 s; (b) bobbing ±0.1 m around −0.125 m at 5 Hz for 30 s; zone exit at 7,900 m | (a) 0 switches; (b) switches never closer than 0.25 s and ≤ 4 per second; crossing order logged and respected every time; SE view beyond 7,900 m |
| S2. C7 shows the pair (0, 10), `perfModeApplied` Idle, `snPhase` ReadyIdle and `frozen`; SE STATISTICS `FrameTimeGPU` within 5 % of SE alone |
| S2.N4 | see-through GPU: 60 s underwater hold | SE per-process GPU % at least 30 % lower than at the same spot above water |
| S2.N5 | Alt+Tab away and back 5 times, with a visible Notepad window placed between the games each time | SE re-raised; Subnautica directly under SE again (walker) within 3 retries; both muted while away |
| S2.N6 | OUTER VOID biome at SN x = +2,500 m | `GetBiome` returns "void" |
| S2.N7 | warm-up and moving dives: (a) after an Approach phase the crossing never shows a half-streamed Subnautica; (b) a scripted dive at 5 m/s vertical (Prime from about `y` +10 m by the 2 s lead); (c) a teleport to −150 m 1 km and 2 km away | (a) `snReady` gating holds (`areaBuilt` = the ±16 m boxes); SE frame hitch < 50 ms during Prime; (b) Underwater entered ≤ 0.5 s after the crossing; (c) SE's own view until `streamingSettled`, then Underwater; the wait recorded against S1.N3 (≈ 1.3 s at 1 km, ≈ 3.1 s at 2 km) |
| S2.N9 | taskbar and Alt+Tab | after `ReadyIdle`, no Subnautica taskbar button and no Alt+Tab entry (UI Automation); a click where the button was and an Alt+Tab attempt leave `GetForegroundWindow == seHwnd` |
| S2.N10 | start-up foreground | 5 s after SE's main menu appears, `GetForegroundWindow == seHwnd`; Subnautica never foreground after `ReadyIdle` (100 ms samples)
| S2.N11 | Track cost | 60 s hovering at +50 m and 60 s sailing on the surface in zone: SE frame time within 5 % of SE alone at the same spots; Prime duty cycle logged |
| S2.A′ | fallback harness, 50 toggles with lower/raise | SE foreground 50/50; Subnautica fully visible when lowered; result recorded so A′ is ready |

**Seeded failures**

| Mutant | Expected |
|---|---|
| S2.M1 (required) DWM call disabled | T-D1 opacity check reports "opaque"; S2.N0 calibration colour absent |
| S2.M2 `ClearInput` silencer off and `backgroundBehavior` set to `IgnoreFocus` | S2.T-I1 reports non-zero Subnautica input |
| S2.M3 above-water mute skipped | S2.T-A1 flags Subnautica peak > 0 above water |
| S2.M4 no hysteresis and no dwell | S2.N2 red on trace (a) (switches inside the band) and on the spacing check of trace (b) |
| S2.M5 `GetBiome` patch off | S2.N6 returns a non-void biome or null |
| S2.M6 idle recipe off | S2.N3 red |
| S2.M7 margins off during Prime | S2.N8 shows the covered stall (or the chosen alternative removed fails the same way) |
| S2.M8 SE does not take the foreground at `Init` | S2.N10 red |
| S2.M9 a visible Notepad between the games while the walker runs | T-D1 z-order check red (proves the oracle) |
| S2.M10 `snReady` gated on `IsWorldSettled` | S2.N7 (b) delayed beyond 0.5 s or never |
| S2.M11 SE volumes written every tick without the focus gate | S2.T-A1 red (SE audible while away) |
| S2.M12 freeze by separate `Begin/End(FeedbackPanel)` per cause | S2.T-A1 red (Subnautica unfrozen after ESC and resume in Space) |
| S2.M13 Water Mod volume through `/wvolume` | S2.T-A1 red (`WaterClientSettings.xml` hash changes) |
| S2.M14 `vSyncCount` left at 1 in Idle | S2.N3 frame-rate check red |

*** Be the human for S2.T-I1 (two scripted 60 s runs). Listen to the T-A1 cycles and judge the
crossfades. Judge the crossing and the HUD over Subnautica: first verdict on "underwater looks like Subnautica".
Change `ForegroundLockTimeout` to the Windows default and back for the S2.
it). Approve the pycaw download. Approve launches. If T-D1 or S2.N8 fails without a working alternative, A′ becomes
the v1 display (D1)

**Exit.** All checks green and all mutants red under F; or T-D1 failed, A′ chosen and S2.A′ plus T-I1/T-A1 green
under A′.N8 result.

**Proves** (*real run*): design F (or A′) works with both real games on this PC: focus, z-order, transparency, input
and audio. **Does not prove:** mirrored geometry, performance with grids, long sessions, behaviour after a Windows
build change (flip-model upgrade, S8.N7).

---

**Goal.** Underwater must *feel* like Subnautica, not only look like it. The design rule is unchanged (SE owns the
player, its inventory, survival, tools, saves; input never leaves SE): the SE plugin replaces the engineer's movement
while he is in the water of `DE_4546B` by a clean reimplementation of Subnautica's underwater motor, with Subnautica's
numbers read from its code (never copied) and verified by measurement. The Water Mod's own character physics (judged poor

**Builds on:** S1 (site binding, Water Mod API for the water surface, `/de` commands, se-*.jsonl). Independent of S2:
testable in SE alone.

**Movement model
** swim in the look direction
(free 3-D, no walking on the floor, no gravity), forward/backward/strafe/vertical speeds and their acceleration and drag
(inertia when released), stepped at Subnautica's 0.1 s; no sprint (Subnautica's swim motor has none); the vertical speed
halved each time the swim motor is enabled again (the latch entry and every crossing of the surface with the latch on:
Subnautica's Dive↔Run motor switch); the faster contact acceleration (45 m/s² instead of 20) while touching something;
surface behaviour
(float at the surface, head out, cannot rise above it without the jetpack), entry and exit transitions on the eye
(camera) point as Subnautica tests them (walk → swim when the eye is 0.1 m under the surface; swim → walk when it is
0.8 m above, or 0.1 m when standing on something; fly with the jetpack once the head is out), camera unchanged (SE's
first/third person).
Jetpack: **off in the water** (v1: Subnautica has no jetpack; the engineer gets it back the moment his head is above the
surface, which is how he climbs onto his ship). Oxygen: SE's suit consumption underwater set to Subnautica's rule (surface
refills); depth: Subnautica's diver takes no pressure damage (VERIFIED: `Player.CrushDamageUpdate` is never invoked;
depth only speeds up breathing, ×1.5 below 100 m and ×2 below 200 m), so the character keeps CrushDamage 0 on this
planet; Subnautica's pressure thresholds are a vehicle rule (`CrushDamage`), applied to SE grids in S5. Animations: SE has no swim cycle — first person
is the reference (as in Subnautica); third person uses the closest SE pose; the diver's own arms/fins drawn by Subnautica on
the SE camera are an S5 item (then the picture *and* the hands are Subnautica's).

**Work items.** SE plugin: `SwimController` (per-tick velocity from input + look + the model; `MyCharacter` physics and
jetpack/dampener control; water test from the site map `y` and the Water Mod surface; entry/exit hysteresis), oxygen and
pressure rules, `/de swim` dev commands (`dump`, `model`, `off`), C11 `de.swim` for scripted traces, se-*.jsonl `swim`
events (tick, input, velocity, y). Wire: `SwimModel.Step` (the pure model, CONTRACT §2.10) and `SwimTrace` (the `swim`
row). Tools: `tests/s2b/swim_replay.py` replaying the model from the logged inputs (independent reimplementation in
Python from the same numbers; `n1` is the S2b.N1 oracle), `tests/s2b/sn_reference.py`
a stand-in trace set (`swim_replay.py gen`). Integration: the plugin writes the Wire `swim` row with its own keys appended (CONTRACT §2.10 "SE controller");
`launcher.py s2b` runs the scripted parts in SE alone and `tests/s2b/analyze_swim.py` judges N1–N4 against
`swim_replay.py`; runbook and results in `docs/S2B-STATUS.md`. Review fixes: the
session checks its preconditions (helmet closed, hydrogen, no O₂ bottle) and refuses the parts otherwise, starts SE with
the model in standby, logs a row every tick (`de.swim rows=on`; normal play: every tick only while scripted, else once a
second), sets a known jetpack state before the parts and waits for each script to end in SE; the plugin keeps the
player's jetpack/dampener states in a save taken while swimming and across a seat.

**Oracle**

| Check | What | Pass |
|---|---|---|
| S2b.N1 | scripted inputs in SE alone (forward 10 s, release, strafe, backward, up, down, sprint; `swim_replay.py script n1`) at −50 m | logged speeds, acceleration time and stop distance within 5 % of the Python replay of Subnautica's model (sprint: no change, Subnautica has no swim sprint); `swim_replay.py n1` |
| S2b.N2 | surface: swim up from −20 m | stops with the head at the surface; no rise above it without the jetpack; jetpack available again above |
| S2b.N3 | entry/exit: walk into the water from the shore, jump out onto a block (SE alone the site's ocean has no shore: `launcher.py s2b` part n3 = 4 scripted crossings, swim up into the surface clamp, jetpack on + up out of the water, jetpack off and fall back in, plus the n2 surface hold; the shore walk-in is watched in N5) | one transition each way, no bouncing (hysteresis), logged |
| S2b.N4 | oxygen and pressure | O₂ decreases at Subnautica's rate underwater (1 /s, ×1.5 below 100 m, ×2 below 200 m; 45 units = a full suit), refills at the surface (30 /s above −1 m); no pressure damage to the character at any depth (Subnautica's diver has none, CONTRACT §2.10); `SpaceEngineers.cfg` and `WaterClientSettings.xml` unchanged |
| S2b.

**Seeded failures.** S2b.M1 model numbers ×1.5 → N1 red. S2b.M2 jetpack left on → N2 red. S2b.M3 no hysteresis → N3 red.
S2b.M4 O₂ rule off → N4 red.

*** The reef swim (N5) and the verdict; the jetpack decision is taken (off in the water); revisit if the
feel asks for a "thruster pack" later (out of v1). Before the scripted session: DE Lab's engineer with the helmet closed,
at least 25 % hydrogen and no O₂ bottle in the inventory, saved. Decisions from the review: the swimming
capsule (SE's upright engineer is about 1.7 m tall; Subnautica's swimming capsule is 0.75 m) and SE's O₂ bottle refill
in the water (kept as SE's, or suppressed while swimming).

*N5"):** N1–N3 green on the real SE (stop times/distances equal to
the replay to the mm; float peak +0.595 m; 4/4 crossings; restores 5/5); N4 needs a Survival copy (the Creative suit
**N5: "la nage est correcte", surface and transition OK, a bit slow
the arm animation is what misses.** Follow-ups: (a) a `fins` option with Subnautica's own modifiers right after S3): Subnautica's own first-person arms (`Player.armsController`) drawn on the SE camera and
driven by the swim state in C1; **Subnautica's breathing sounds, breath bubbles and low-oxygen gasping/choking** by
mirroring the SE suit's O₂ fraction into Subnautica's own oxygen manager (so its breath interval by depth, bubbles
(`PlayerBreathBubbles`) and warnings fire natively; the PDA voice stays suppressed) — the O₂ fraction travels in C1,
which needs the layout's first minor bump (1.1) with the full re-pin procedure (CONTRACT §5.2/§6), unless a spare C1 pad
byte exists; (c) the F8 hotkey remap (dev see-through vs SE's spectator). One compact workflow for (a)+(b)+(c), few

**Exit.
**Proves** (*real run*): the engineer swims like Subnautica's diver. **Does not prove:** animations (S5), creatures (S6).

---
### built awaiting the real run; `docs/S5A-STATUS.md`)

**Goal.** Underwater must look and feel like Subnautica: the S2b follow-ups (a), (b) and (c) above in one slice.
(1) Subnautica's first-person arms (`MainCameraControl.main.viewModel`) with their swim animations on the ghost camera,
driven by SE's swim state and hidden out of the water; no Subnautica motor or input runs. (2) SE's suit O2 fraction
mirrored into Subnautica's `OxygenManager` so the real breathing event, `PlayerBreathBubbles`, `LowOxygenAlert` and the
choke play; PDA voice suppressed; the ghost never dies (`Player.SuffocationDie` prefixed away; CONTRACT §4.2).

default Ultraglide; the Python twin and the S2b.N1 oracle model the row's `fin` bits. (4) The dev see-through hotkey
moved from F8 (SE's free spectator) to Apps (the Menu key; `config.json "seeThroughKey"` still overrides).

**Transport decision.** The swim state travels in the first 12 bytes of the C1 pad (0x94–0x9F), layout 1.0 unchanged
(hash `0x4f3a35f6fc1eaf31` still pinned); the extension is pinned on its own (`SwimWire.CanonicalText`,
`0x832ba93ca25bc307`, in `SwimWireTests.cs` and `analyze_s5a.py`). CONTRACT §5.4 "C1 swim extension", §2.10.

**Work items (done, no game).** Wire `SwimWire.cs`, `SwimModel` fins; SE `SwimWireFill`, `SwimController` fins/O2
override, `S5aMutants` (M1 fins claimed not applied, M2 o2 stuck, M3 wire frozen), `S2Config` hotkey; SN `S5aHost`,
`S5aRules`, `S5aPatches`, `FrameReader.Swim`, `GhostPlayer.Exempt`; launcher session `s5a` (parts fins, o2, arms, choke;
`--analyze --sn-log`); oracle `tests/s5a/analyze_s5a.py` (rows S5a.FINS, S5a.O2, S5a.ARMS, S5a.CHOKE, S5a.SN, S5a.ALL);
Python twin `swim_replay.py` fins. 613 unit tests green (baseline 576), selftests green, dry-runs exit 0.

**Exit (real run).** `launcher.py s5a` SE alone: every S5a.FINS/O2/ARMS/CHOKE/ALL row PASS and S5a.M1/M2/M3 red on
their rows; both games (s2 recipe alongside) + `s5a --analyze DIR --sn-log FILE`: every S5a.SN/CHOKE row PASS (arms
visible=1 state=swim while swimming, visible=0 state=out out of the water, DE.O2 mirrored per step with shielded=1,
a lowO2/choke event in the choke window, DE.PDA played=0, no DE.Ghost damage/death/moved/saved line)
the arms and the breathing (S5A-STATUS §11.6 looks). **Proves:** the ghost renders Subnautica's arms and breathes from
SE's state without owning anything. **Does not prove:** the full astronaut (S5), creatures (S6).

---

**Goal. Subnautica's inventory UI cannot be reused for SE items (its
`TechType` model, slot sizes and uGUI would have to be faked for every SE item, and it would need the mouse above SE's
HUD, against the display design). Instead the SE plugin adds a **native SE screen with Subnautica's ergonomics**: a grid
of slots with SE's own item icons, drag and drop, nearby containers side by side, right-click/"transfer all", quick
assignment to the toolbar, search/filter, a dark PDA-style skin; opened on the inventory key (the terminal tab stays
available). Pure SE plugin work on SE's GUI framework (`MyGuiScreenBase` and controls) and inventory API (`MyInventory`,
transfers, `MyToolbar`), VERIFIED; nothing crosses the bridge; Subnautica untouched.

**Builds on:** nothing of the bridge (plugin only). the inventory is comfort).

*SE counts litres, Subnautica counts slots.
(a) a grid of virtual slots cut from the volume (one slot = a fixed number of litres; big items span several slots —
closest to Subnautica); (b) item tiles plus a capacity bar (closest to SE). Recon first: existing Pulsar/Workshop
inventory plugins (grid inventories, sorters) — reuse or learn from them before writing one.

**Work items.** Recon note (`docs/recon/inventory-plugins.md`); SE plugin: `InventoryScreen` (screen, layout, slot
rendering from `MyPhysicalItemDefinition` icons, drag and drop, container discovery within reach, transfer actions,
toolbar assignment, search), key binding and settings (opt-in; the stock screens untouched), dev command `/de inv`;
tests for the Unity-free parts (slot cutting from volumes, layout, transfer planning).

**Oracle**

| Check | What | Pass |
|---|---|---|
| S2c.N1 | open/close, drag between the character and a nearby container, "transfer all", toolbar assignment | every action mirrored by SE's own inventory state (volumes, item counts) with no loss; the stock terminal shows the same |
| S2c.N2 | SE frame time with the screen open | within 5 % of the stock terminal screen |
| S2c. nothing persisted outside SE's own save |

**Seeded failures.** S2c.M1 transfer planner off by one item → N1 red. S2c.M2 layout without the volume rule → N1 red.

**Exit. **Proves:** SE can carry Subnautica's
ergonomics natively. **Does not prove:** anything about the bridge.

---
### S3 First block

**Goal.** One SE large armor block, then a 3×3 plate, drawn natively in Subnautica at the mapped seabed position,
lit, fogged and shadowed like a Seamoth next to it. S3a uses the fake producer; S3b uses the real SE plugin.

**Builds on:** S2 (S3a only needs S1). **CONTRACT first:** ModelBegin/ModelChunk/ModelEnd, MaterialDef, PartAdd/PartRemove records with
offsets (§5.4), grid pose `(o, q, s, k)`, mesh conventions and texture colour space (§2.5).

**Work items**

| Part | Items |
|---|---|
| SE plugin (S3b) | A static grid (station) holding `LargeBlockArmorBlock` at F⁻¹(lab point), block centre at seabed height h (lower half buried; h from a Subnautica raycast reported over the bridge or from the lab config). Model loading on one worker thread: `new MyModel(path){LoadUV = true}.LoadData(true)` (Havok shapes skipped because the thread is not Havok-initialised); geometry from `Triangles` with `GetMeshList` ranges (never `Indices`, which are null after loading); positions, normals, tangents/binormals, UVs in model space. Armor parts from `RenderData.Cells[*].CubeParts`: `MyCubePart.Model`, `InstanceData.LocalMatrix`, `ColorMaskHSV`, the armor pattern offset from the instance's packed bytes 5–7 as SE's cube-instancing shader reads them (`GetTextureOffset(0/1)` returns 0 in the installed build, CONTRACT §2.5), UV = uv / `PatternScale` + offset. MaterialDef with resolved absolute texture paths. Records through the byte ring with a per-tick budget (a few models or about 2 ms). Pose from the group pivot P*; the logged SE matrix and P* go into the JSONL for S3.C2. A GPS marker on the block (HUD sprite) for S3.N2. `MyRenderProxy.RenderColoredTextures` export of the block colour for S3.C13, written to `%LOCALAPPDATA%` |
| SN plugin | Frame (position o, scale (s, 1, s)) / Entity (rotation q, scale k). Mesh built on a worker thread, already mirrored; triangles (I0, I2, I1); per-model C3 measure with an automatic flip below 1 %. Upload on the main thread with `SetVertexBufferParams/SetVertexBufferData/SetIndexBufferData/SetSubMesh` within ≤ 2 ms per frame. MarmosetUBER from Addressables `Assets/Marmoset/Shader/Uber/MarmosetUber.shader` after the main scene is loaded (never `Shader.Find`); `_SpecInt`. Textures: `_cm` uploaded raw as BC7 sRGB `_MainTex` (skip mips to 1024 px); `_add` and `_ng` sampled as their DDS declares (a DXGI `*_SRGB` format decodes on sampling, as SE's shader sees it; CONTRACT §2.5) and converted in the plugin on the GPU (upload, blit into linear `R8G8B8A8_UNorm` targets, `AsyncGPUReadback` in the target's own format, repack on a worker thread) into `_MultiColorMask`, `_SpecTex`, `_BumpMap` (X→A, Y→G with G′ = 1 − G: SE's shader negates the normal map's y, CONTRACT §2.5; HYPOTHESIS until S3.N1b) and `_Illum`, each converted map also blitted at 64 × 64 into its 8-bit conversion target and into an R32G32B32A32_SFloat target, both read back, for S3.N1d; the `_SpecTex.rgb` lerp on decoded values; `Texture2D.Compress` budgeted at one texture per N frames, or RGBA32 uploaded uncompressed in v1; `UWE_3COLOR` with `_Color`/`_SpecColor` set the way `ColorCustomizer.SetColors` does. `SkyApplier` with `renderers` filled before its `Start`; layer 0; shadows on; never set a fresh `MaterialPropertyBlock` afterwards. Lab scene command: wait for a layer-30 collider under the lab point, place the block, spawn a reference Seamoth 6 m aside (kinematic, lights off), take captures (UI hidden) at P1 (front 6 m), P2 (side 6 m, 0.3 m above the seabed), P3 (10 m above), P4 (front 40 m) after 90 frames each, with sidecars. The camera poses come from the producer (fake or SE spectator) |
| tools/tests | `tests/s3/check_placement.py` (S3.C2: recomputes `Apply(o, q, s)` with `wire_ref.wire_pose` from the logged SE matrix and the logged P*, independent of WireMath; separately checks affine vs the exact map F against the tool's bound); `tests/s3/image_checks.py` (C5–C10, C13, C14 from captures and sidecars); fake producer S3a: a procedural 2.5 m cube with UVs, a MaterialDef pointing at SE's real armor DDS files (read in place), one PartAdd, a static pose |

**Oracle** (ship-render §7.3, ids kept)

| Check | Pass |
|---|---|
| S3.C1 geometry arrived | `DE.Mirror built... gridModelVerts=V gridModelTris=T` equals the producer's counts (S3b: `MyModel.GetVerticesCount/GetTrianglesCount`), and the drawn triangles (`gridTris`, the chunk totals) equal the model triangles: each model triangle is drawn exactly once. Drawn vertex totals may differ (a chunk copies only the vertices its submesh uses) |
| S3.C2 placement | 8 drawn vertices in Subnautica world space match `Apply(o, q, s)` recomputed in Python (`wire_ref.wire_pose`) from the logged SE matrix and P*, within 1 mm: per corner direction the uploaded vertex whose SE-local position is the most extreme, mapped by its chunk renderer's `localToWorldMatrix` (`DE.Mirror placed... seLocalCorners= cornersSn=`; for a box the 8 vertices are its corners). Corners rebuilt from the mesh bounds could not see a dropped mirror on a symmetric box. Separately (S3.C2.affine, a check of the wire convention on SE's logged values, which no Subnautica output can turn red), affine vs the exact map F stays within the tool's bound `max(s, 1)·d²/(√3·(r − d)) + (θ/sin θ − 1)·s·d`, θ the larger anchor angle of P* and the point (`wire_ref.affine_vs_f_bound_at`; at the lab spot 0.12 mm at 1 m, 2.0 mm at 10 m, 0.11 m at 100 m from P*, `SITE_V1`; the earlier form without max(s, 1) and r − d was exceeded by up to 1.4 % on 100 m boxes) |
| S3.C3 winding | > 99 % of triangles have a geometric normal agreeing with their vertex normals |
| S3.C4 material | `shader.name == "MarmosetUBER"`, `isSupported`, planned keywords, layer 0, `SkyApplier` present and its sky non-null after 2 frames (`InspectorApplySky`, the sky actually applied; `InspectorEnvironmentSky` stays null for outdoor appliers) |
| S3.C5 terrain occludes | P2: the buried half is hidden (a camera ray to a buried corner hits layer 30 first; image region check) |
| S3.C6 fog like native | contrast(P4)/contrast(P1) within ±20 % of the Seamoth's; MISSING (inconclusive) when the 8-bit floor of the four contrasts (half a level for a region flat at one level, 1/√n of it otherwise) lets the ratio of ratios straddle the band, or a contrast is within two floors of 0 |
| S3.C7 sun response | noon/midnight luminance ratio (`de.time 0.5` / `de.time 0.0`, the plugin's own time command; never the console's `daynight`, which restarts the cycle) within ±25 % of the Seamoth's; MISSING (inconclusive) under the same 8-bit floor rule as C6 (a near-black midnight cannot carry a ratio) |
| S3.C8 caustics | P3 at < 20 m depth: temporal std-dev of top-face luminance over 60 frames > 30 % of the Seamoth's |
| S3.C9 shadow | anti-sun seabed patch darkened by at least 50 % of the Seamoth's darkening, with the sun at about 45° (`de.time 0.24`: Subnautica's sun elevation is 90° − abs(−65° + 130°·clamp(2c − 0.5)) for the cycle time c = (t − 0.125)/0.75·0.5 + 0.25, decomp `DayNightCycle.GetDayNightCycleTime`, `uSkyManager.InitSunAndMoon`; 0.35 gave 64°) |
| S3.C10 oracle alive | two P3 captures 1 s apart differ; CPU used in between |
| S3.C11 no harm | no new error lines in `LogOutput.log` or `Player.log`; main-thread CPU per frame with and without the block (GetThreadTimes over 600 frames: waits for vsync or a frame cap are not counted, where the wall frame time stays on the cap) delta < 0.3 ms |
| S3.C12 teardown | quit to menu and reload: our mirror roots gone and our mesh count back to baseline. Implemented as `de.s3 {action: teardown}` (it runs the mirror's world-loss path of a quit to menu now, so S3.M6's preserved roots survive it as they would a scene unload) then `de.s3 {action: count}`: roots, preserved roots, scene markers and our meshes all 0; a real quit to menu is the orchestrator's decision (S3-STATUS §5.9) |
| S3.C13 paint (S3b) | painted region at P1 within ΔE < 5 of SE's exported PNG for that colour, sampled at the same UV under neutral light |
| S3.C14 pattern (S3b) | across plate seams, mean absolute gradient < 1.5× the in-block mean |
| S3.G1-E2 shear | Frame (1.0284, 1, 1.0284) and (1.0345, 1, 1.0345) + Entity rotating 360° in 1° steps: `renderer.localToWorldMatrix` corners vs emulated `Apply` < 1e-4 m on every frame, compared on the matrix's linear part (corner − matrix translation vs `Apply(l) − Apply(origin)`): float32 world positions near the lab spot are spaced 1.2e-4 m (CONTRACT §2.5) |
| S3.N1 glyph and bump | N1g: an asymmetric glyph texture reads correctly on a known face (4/4 colour quadrants). N1b "bump faces the sun": the glyph's bump map is 8 × 8 cells, in each colour quadrant an up/down checker (top two cell rows) and a right/left checker (bottom two); on the camera-facing side face the up-tilted cells are brighter than the down-tilted cells of the same colour (geometric mean of the four quadrant ratios ≥ 1.03, HYPOTHESIS; MISSING when too dark): light from above favours them whatever the sun's azimuth, so this settles G′ = 1 − Y and the tangent w (either wrong swaps up and down). N1d conversion fidelity: every converted map's 8-bit conversion target equals a float readback of the same blit within 1/255 (`DE.Mirror texture fidelity`). Information rows: N1x (the right/left cells, judged when the sun lies along the face) and N1t (the models' stored tangent frames against their UVs: the glyph cube's tangent = +u, binormal = +v; SE's armor models with the same census carry N1b's verdict) |
| S3.N2 gold wall (design F) | during a scripted 0.5 Hz ±30° yaw sine, offset between SE's GPS marker (sprite) and the Subnautica-drawn block centre in composed frames: ≤ 2 px at rest; while moving, offset / angular speed matches the S1.N2 latency within one Subnautica frame (HYPOTHESIS thresholds) |
| S3.N3 analytic projection | SE `ViewProjectionMatrix` pixel of a block corner vs Subnautica `WorldToScreenPoint` of the mirrored corner: within 0.5 px for level looks, the S1.G1-E5 offset otherwise |

**Seeded failures**

| Mutant | Expected |
|---|---|
| S3.M1 Z mirror dropped | S3.C2 and S3.C3 red |
| S3.M2 triangle swap dropped | S3.C3 near 0 % |
| S3.M3 `SkyApplier` removed, or a fresh property block set after it | S3.C4 red; C6/C7 off the Seamoth ratios |
| S3.M4 block drawn from the previous snapshot (one-tick lag): the producer publishes the previous tick's snapshot (C1 camera and C2 pose) stamped with the current tick, the GPS marker staying current (a lag of the static block's pose alone cannot show while only the camera turns) | S3.N2 red while moving |
| S3.M5 capture file reused | S3.C10 red |
| S3.M6 mirror root given `SceneCleanerPreserve` | S3.C12 red |
| S3.M7 shear test with a rigid child (no scaled Frame) | S3.G1-E2 deviation equals the rigid-draw error (proves the check measures something) |
| S3.M8 `_ng` converted through an sRGB render target | S3.N1d red. N1b is not required to turn red: the sRGB encode is monotonic, so the bump ordering survives |

*** Look at the P1–P4 captures and say whether the block belongs next to the Seamoth (an exit
item, not a formality). Pick the paint colour for C13 or let the script pick. Approve launches.

**Exit.** All checks green and mutants red; If S3.G1-E2 fails: switch to per-chunk rigid
frames with K baked into vertices (gap-1 fallback) before S5.

**Proves** (*real run*): SE geometry and SE textures go through Subnautica's own pipeline with native light, fog,
caustics and shadows, placed to 1 mm against the wire convention. **Does not prove:** many grids, moving grids,
performance, interiors, lights, the character.

---

### S4 Planet + terrain

**Goal.** SE collides natively with Subnautica's terrain at the site, through 34 non-saved provider-backed tiles with
deterministic entity ids, and the crust underside (plug) matches what Subnautica draws. Saves stay loadable without
the plugin; gear-locked ships stay locked across reloads.

**Builds on:** S1 (planet, site), S3 for the cross-game captures. A-T0, A-T0b and the provider unit tests can start
after S0. **CONTRACT first:** provider identity and contract table (§3.1), tile lattice and entity ids (§3), source
manifest, content mapping (after A-T0), terrain gate and fail-safe (§1.2, §7).

**Work items**

| Part | Items |
|---|---|
| SE plugin | `SnTerrainProvider: IMyStorageDataProvider` with `[MyStorageDataProvider(45460)]`; guarded `MyOctreeStorage.RegisterTypes` in `Init` (once; registering twice throws). Terrain gate in `BeforeStart` (CONTRACT §1.2: offline single player, exactly one `DE_4546B`, source manifest, provider registered, members resolved, 34 ids free, 34 maps created; all or nothing): Subnautica path, `index.txt`, build 20241558, manifest of 5,416 files / 1,203,085,204 bytes / rows 7–19 / `manifestHash` 0x2cc277ce91763285 (CONTRACT §3.1 "Source manifest"); otherwise no tiles, `DE.Terrain REFUSE` and the fail-safe's S4 part (`failSafeActive`, saving blocked with the HUD warning; the grid hold and the crush suspension are S8, CONTRACT §7). One shared, ref-counted `SnOctreeSource` (lazy memory-mapped batch files; preallocated per-octree tables of about 11.5 MB: node offset, node count and state, the state array alone 1.6 MB) and one provider instance per tile. Provider members exactly as the CONTRACT §3.1 table: request-based `ReadRange` honouring `RequestedData`/`RequestFlags`, `detectOnly` never touching `Target`, the `MyStorageData` overload, `Intersect(box, lod)` with the box in LOD-0 storage coordinates (`lod` is only the caller's meshing LOD; VERIFIED callers), `Intersect(ref LineD)` conservative but false over open water (laser antennas use it), content in LOD-voxel units (CONTRACT §3.1 "Content mapping"), materials by name each session (first-pass depth-band table of CONTRACT §3.1; Subnautica's 209 terrain types unused for now; gameplay choice), serialisation no-ops, `Close` ref-counted; thread-safe and allocation-free. The existing session component (`DeepEngineersSession`, `[MySessionComponentDescriptor]`, registered by Pulsar) calls `TerrainTiles.OnBeforeStart` right after `SiteBinding.OnBeforeStart` (C first, one component, fixed order) and creates the 34 tiles with `MyWorldGenerator.AddVoxelMap(name, storage, worldMatrix (min corner + 512), entityId (CONTRACT §3 formula), lazyPhysics: true, useVoxelOffset: false)` and `Save = false`; a null return (id taken) → refuse and fail-safe. Voxel edits on the tiles blocked by prefixes on `MyStorageBase.WriteRange`, `MyVoxelGenerator.MakeCrater`, `CutOutSphereFast`, `CutOutShape`, `CutOutShapeWithProperties`, `FillInShape`, `PaintInShape`, `ChangeMaterialsInShape`, `RevertShape`, `MyVoxelBase.UpdateVoxelShape` (the voxel-hand funnel behind every `MyVoxelBase` request handler) and `MyShipMiningSystem.RequestCutOut` (ship drills count their ore before the write, so `WriteRange` alone would hand out free ore) (each checked at start; exact signatures in CONTRACT §3.1 "Edit guards"). New references (`Private=false`): `VRage.NativeWrapper` (`VrVoxelMesh` in `PostProcess`), `HavokWrapper` (A-T1 cell checks). Tile list with entity ids logged with `siteVersion` |
| SN plugin | Raycast service for A-T2 and WM-6 (after `Physics.SyncTransforms`): layer-30 `Physics.Raycast` at requested columns; mesh-only raycast against the chunk's render mesh for the native budget. Built as `de.s4 {probe|status|cancel}` on the C11 dev channel (no wire change; grammar in `docs/S4-STATUS.md`): the camera is held over each column (colliders exist only within about ±16 m of it); the render mesh is the `hiFilters` mesh of the `ClipmapChunk` whose `collision` is the hit collider (pooled colliders are normally not parented to their chunk, VERIFIED `TerrainPoolManager.SetParent`) |
| tools/tests | Octree reader/sampler in the VRage-free library `src/DeepEngineers.Terrain` (netstandard2.0 and net48; its sources are linked into the SE plugin, no extra DLL), tested by `tests/DeepEngineers.Terrain.Tests` (net48, offline, reads the user's octrees read-only): A-T0 runner over 10 batches: 12-18-12 Safe Shallows, 7-17-10 densest (10.2 MB), 14-18-16 kelp forest, 10-17-11 Jellyshroom cave entrance, 6-17-5 Grand Reef CoralArch, 7-13-15 Lost River region (REPORTED region, most surface octrees at −960…−640 m there), 10-11-13 lava zone, 14-8-9 deepest floor (−1,657 m), 14-19-18 Mountain Island top, 18-15-7 the shallowest cut ceiling (uniform solid over a missing batch); oracle `tools/sn-octrees/sn_octree_batch.py` with an `--at0 OUT_DIR` mode (rasters of type, density and leaf size plus a JSON summary, written outside the repository; format in the S4 contract result and the runner's docstring); A-T0b synthetic plane through the provider; provider unit tests (S4.N3). A-T1 harness: 64×64 vertical rays, each preceded by `MyVoxelPhysicsBody.PrefetchShapeOnRay(ref LineD)` and a wait until the cells exist (`MyVoxelPhysicsBody.GetShape(0, out HkUniformGridShape)` then `HkUniformGridShape.GetChild(x, y, z, out HkBvCompressedMeshShape)` per cell: true = built, an emptied cell included), then `MyPhysics.CastRay`, compared with `MyVoxelBase.GetIntersectionWithLine` against the storage; every sample records "cell built", so an unbuilt cell is a harness error, not a hole; Python reference heights through `worldmap_ref.py`; the bake variant (`WriteRange` into `MyOctreeStorage(null, 256)` from one Safe Shallows batch) for comparison. Session and oracle: `launcher.py s4 --mode S4se|S4both` (SE alone with the passive host, or both games; `--dry-run` prints the plan with the source manifest measured read-only and the reference heights; `--selftest` runs the parts against in-process fake plugins; throwaway parts `n2`, `g2e2save`, `g2e3b`, `g2e2noplugin`, `g2e5`, `m4`, `m10` need `--world` on a copy made by `tools/lab/make_terrain_copy.py`, autosave 0, LastSession pointed at it; `--list-mutant-groups` for the runbook); `tests/s4/analyze_s4.py` (one row per S4 id, `--selftest` with seeded M2–M10) on reference heights from `tools/worldmap/worldmap_ref.py` `OctreeColumns` (exact trilinear roots per SN column; an SE vertical ray is radial, so it maps to one SN column through F) |

**Oracle**

| Check | What | Pass |
|---|---|---|
| S4.A-T0 | offline: C# reader/sampler vs Python on 10 batches | identical solid grids; signed distances within 1e-6; equal leaf-size and crossing counts; content iso-surface vs Subnautica signed-distance iso-surface Hausdorff P95 ≤ 0.05 m (measured at iso byte 127: met on Subnautica's own sample grid, P95 ≤ 3.6 mm; on the grid offset by (0.37, 0.21, 0.45) m, SE's real case, P95 0.06–0.md`) |
| S4.A-T0b | offline: synthetic flat plane served through the provider at a known height (an untruncated plane distance), meshed by SE's own isosurface code path (`VRage.Voxels.IsoMesher`, VRage.NativeWrapper.dll over the native VRage.Native.dll, read in place from Bin64; if it cannot run outside SE, the same test runs in SE as `de.terrain at0b`) | at LOD 0 and LOD 1–2 (content in LOD units) the mesh height equals, within 1e-3 m, the height predicted from the content bytes with SE's iso byte, and lies within the byte quantization (0.5/127.5 · 2^L m) of the plane; records SE's iso byte. Subnautica's sample convention cancels out here: S4.A-T2 decides it (CONTRACT §3.1) |
| S4.N3 | provider unit tests | `ReadRange` with `detectOnly` and `Target == null` never writes and sets `EmptyData`/`FullContent` only for proven-uniform ranges; output `Flags` as CONTRACT §3.1 (`EmptyData`/`FullContent` exactly when every written byte is empty/full); `Intersect` boxes in LOD-0 coordinates, never Disjoint/Contains on a non-uniform octree, and the curvature bound holds on dense samples of random boxes; `Intersect(ref LineD)` false over open water; per-sample map vs any per-request approximation ≤ 1 mm; materials resolved by name; `WriteTo`/`ReadFrom` no-ops; no allocation over 10,000 `ReadRange` calls on touched batches |
| S4.A-T1 | SE only | (1) vertical rays (prefetched) vs reference heights: P95 \|Δ\| ≤ 0.25 m, max ≤ 1.0 m on columns without overhangs, no ray through the crust in terrain columns with every cell built, same verdict on 1,000 random rays; bake and provider hit heights agree within 1 mm on the shared batch. (2) a 1-block large grid dropped at 10 points rests within ±0.3 m of the reference; a scripted character walk of 50 m never sinks more than 0.5 m below it. (3) tile RAM ≤ 1.5 GB after the 34 tiles; `ReadRange` p99 ≤ 2 ms and < 1 ms per 8 m physics cell (`RequestShapeBlocking` in the Havok step); frames > 5 ms ≤ 1 % during a 100 m/s fly-over; shape-build time per 8 m cell logged (decides 1024 vs 2048 tiles; collision probe §10) |
| S4.A-T2 | both games | Subnautica layer-30 raycast vs SE `CastRay` at the same mapped columns: P95 ≤ 0.3 m; Subnautica collider vs render deviation measured (the native budget); capture of an SE-mirrored block resting on the seabed from 3 m, looked at |
| S4.WM-6 | SE 2.5 m cube dropped at F⁻¹ of a flat SN spot near (0, −18, 0) | mirrored bottom face within the S4.A-T2 value measured at that column (≤ 0.3 m) of Subnautica's terrain (Subnautica raycast down from the cube); within 5 cm of SE's own resting height mapped through F (the map check); capture |
| S4.G2-E1 | cut faces (dormant, unbridged Subnautica: the console works there; Creative is fine for looking) | `warp 880 -700 -816`, look up: flat ceiling at depth 640 ± 2 m; `warp -560 -1950 112`: flat ceiling at 1,920 m; `warp -250 -900 1008`, face −X: flat wall at x = −288, 38 m away, spanning at least depths 960–864. **If it fails**, the plug decision is reopened (depth rule under the crater footprint, or Subnautica-side cap meshes) |
| S4.G2-E2 | save portability | the save holds no tile, no `.vx2` for it and no provider id; with the plugin a resting grid moves < 1 cm in the first 10 s after load; without the plugin the world loads (no `KeyNotFoundException` from `ReadDataProvider`) and the grid sinks (expected: the reason for the README warning and the fail-safe) |
| S4.G2-E3b | gear locks across reloads | a ship landing-gear-locked to a tile is still locked after save and reload (`AttachedEntityId` equals the tile's deterministic id) |
| S4.G2-E5 | world mod removed (on a copy of a DE world) | what SE does is recorded (resolves the HYPOTHESIS of CONTRACT §7 "Uninstall") |
| S4.N1 | prior-art S4: SE character on Subnautica's seabed | logged feet height vs Subnautica terrain height under the same column within 0.3 m |
| S4.N2 | edits blocked | a hand drill, a ship drill, a warhead, a meteor (`MyVoxelGenerator.MakeCrater` through a test command; SE 1.210's meteors make no crater) and the voxel hand each leave the storage content hash unchanged and `MyStorageBase.IsRangeModified` false; no ore from either drill; `DE.Terrain EditBlocked <path>` logged for each (paths of CONTRACT §3.1) |

**Seeded failures**

| Mutant | Expected |
|---|---|
| S4.M1 A-T0 mutants: sign flip, 1-voxel offset, swapped x/z, child order z·4+y·2+x | S4.A-T0 red for each |
| S4.M2 tile shifted 2 m | S4.A-T1 (1) red |
| S4.M3 `Intersect` wrongly Disjoint for one 8 m cell | rays find the hole; S4.A-T1 red. SE asks `Intersect(box)` on the physics path only from the prediction-box prefetch of moving entities (`MyVoxelPhysicsBody.UpdateAfterSimulation10`; `PrefetchShapeOnRay` never calls it, VERIFIED), so each A-T1 ray also records SE's own per-cell `Storage.Intersect` answer (`intersectHole`) and the drop test shows the hole natively |
| S4.M4 tiles created with `Save = true` (throwaway world copy, autosave off, rule 10) | S4.G2-E2 finds the storage in the save |
| S4.M5 provider source check against a wrong manifest | `DE.Terrain REFUSE`, no tiles, fail-safe active |
| S4.M6 provider samples shifted by half a voxel | S4.A-T0b red (0.5 m off) |
| S4.M7 A-T1 harness without the prefetch | the harness reports "unbuilt" cells, not holes |
| S4.M8 provider sets `EmptyData` (with `Target` set) on one surface cell's request | the mesher skips the cell (VERIFIED): S4.A-T1 finds the hole; its `detectOnly` twin S4.M8b turns only S4.N3 red (`detectOnly` runs on edit paths only, VERIFIED) |
| S4.M9 one edit guard removed per path (cutout = hand drill and warhead, shipdrill, explosion, meteor, voxelhand) | S4.N2 red for that path |
| S4.M10 random tile entity ids (throwaway copy, rule 10) | S4.G2-E3b red (gear unlocked) |

*** Run the G2-E1 warps in any dormant Subnautica session. Approve one SE launch without our plugin (Pulsar profile without it) for G2-E2, on a world copy. Approve the
throwaway world copies of S4.M4 and S4.M10 and the G2-E5 copy. After any Subnautica update, have the manifest re-measured
and re-pinned (CONTRACT §3.1; the terrain refuses until then). Look at the A-T2 and WM-6 captures. Choose the voxel
material policy. Accept 2048 tiles if A-T1 says 1024 costs too much (or plan B: a Harmony patch on
`MyPlanetStorageProvider`, collision probe 3c). Approve launches.

**Exit.** All checks green, mutants red, tile size, content mapping and voxel index convention recorded in CONTRACT.

**Proves** (*real run*): SE ships and characters collide with Subnautica's own terrain at the site, at any range;
gear locks survive reloads; the world still loads without the plugin. **Does not prove:** collision with wrecks and
precursor structures (S7), grid proxies in Subnautica (S6), performance with both games and large ships (S5).

---

### S5 Whole grids + character

**Goal.** Every grid near the camera is mirrored: instanced armor, fat blocks, subparts, subgrids, colours, skins,
build stages and damage states, live; the character is the player's own SE model (bind pose, then SE's bones, then
Subnautica's swim states retargeted while swimming;4 "C1–C4 conventions
for S5"); T-P1 measures every state.

*
character** (its model, skin/DLC suit and colour mask, read from the player's install at run time like S3 reads block
models and textures; a skinned export: bone indices and weights travel with the mesh), animated with **Subnautica's swim
clips retargeted** through one SE-rig to Subnautica-rig bone map (all SE engineer skins share the same skeleton, so one
map serves every player; humanoid `AvatarBuilder.BuildHumanAvatar` at run time if the clips are humanoid, else a
per-bone rotation retarget with rest-pose correction: HYPOTHESIS, settled in S5). First person uses the same model's
arms (SE shows its own body in first person), so S5a's Subnautica diver arms are the placeholder until S5. Fallback when
the export fails: the default Engineer model, never a Subnautica diver (SE is never diminished). A fixed "permanent
character" for everyone was considered and rejected: it would hide the player's SE identity.

**Builds on:** S3, S4. **CONTRACT first:** delta records, subgrid relative matrices, character record, budgets.

**Work items**

| Part | Items |
|---|---|
| SE plugin | Entity set: top-most entities in a sphere around the camera from `MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref BoundingSphereD, List<MyEntity>, MyEntityQueryType)` with a reused list (radius = visibility + 50 m, hysteresis), kept 60 s after leaving. Per grid: armor parts (render cells), fat blocks (`FatBlock.Model` at the block matrix, `ColorMaskHSV`), subparts through the entity hierarchy with per-tick local matrices for animated ones. Deltas from `MyCubeGridRenderData.RebuildDirtyCells` / `MyCubeGridRenderCell.RebuildInstanceParts` and the grid events (`OnBlockAdded/Removed`, `OnBlockIntegrityChanged`, `OnGridSplit/Merge`). Build stages and damage through the current model per block. Subgrids (rotors, pistons, hinges, connectors) sent as children of their physical-group root with relative SE-local matrices (D8; one pivot per group, gap-1 §3.5). Skins resolved SE-side (`MyAssetModifierDefinition`). Floating objects. Character: the player's own model, skin and colour, split by `MaterialsDisabledIn1st` into a body and a head part, skinned on the canonical skeleton `canon1` (`src/DeepEngineers.Wire/Data/SeSkeleton.canon1.tsv`), bones every tick in C1, fallback the default Engineer. Serialisation budget per tick, newest edits first (CONTRACT §5.4 "Budgets and back-pressure (S5)"). Dev-build stream recorder `de.s5 record` (`.derec`) |
| SN plugin | Chunks of 8×8×8 blocks with one combined mesh per material × colour; 32-bit indices above 65k vertices; rebuild only touched chunks; reuse `Mesh` objects; destroy explicitly on world change. Glass from a cloned vanilla glass material; alpha-masked parts as cutout; glass and alpha-masked faces two-sided (gap-1 open question 4). `SkyApplier` dynamic on moving grids. `LODGroup` per chunk (check against `SkyApplier`, ship-render open question 7) and culling at the biome's water visibility (fallback 200 m). All grids interpolated with the camera's alpha; teleport snap; outcome counters. Moving subparts as their own children posed from C2 (CONTRACT §2.8). Character: skinned renderers on the `canon1` bone tree, sources SE bones / swim retarget / bind (CONTRACT §5.4 "Character"); first-person body behind the switch `de.s5 fpbody=se\|diver` (S5a's diver arms until S5.N7 passes). **Stretch (outside the definition of done):** lazy mesh cache in `%LOCALAPPDATA%\DeepEngineers\cache\meshes` keyed by path, size, mtime and SE build |
| tools/tests | Stream recorder/replayer (records SE's C3 model and C4 part streams to `%LOCALAPPDATA%` for the fake producer; derived data, never in the repo). T-P1 runner (`tools/measure/measure.ps1` at 1 s, both plugins' per-second JSONL, SE STATISTICS) |

**Oracle**

| Check | What | Pass |
|---|---|---|
| S5.G1-E4 | C3 over the first 200 distinct real models (the set also holds the census-listed block models `RoundCornerTriangleRightPlate` and `InsetConnectorConstruction_1`, `gap-1.md` §2.3, so S5.M2 can turn it red) | > 99 % per model, except the census-listed inverted models (flipped automatically and logged) and glass or alpha-masked parts |
| S5.N1 | C1, C2, C11, C12 per grid at scale | counts equal SE's; 20 random parts placed within 1 mm of `Apply(o, q, s)` recomputed in Python from the logged matrices and P* (as S3.C2); no new errors; clean teardown |
| S5.N2 | live edits: weld, grind, damage, paint, build stage | Subnautica change within 10 SE ticks of SE's event (HYPOTHESIS); part counts match after each |
| S5.N3 | subgrids and subparts: a rotor at 10 rpm, a piston, a door | mirrored pose vs SE pose (through `wire_ref.py`) within 1 mm at interpolation alpha 1 |
| S5.N4 | coherence (ship-render §6.7 #2): camera in a cockpit, ship at 20 m/s with a 0.5 Hz yaw sine | screen std-dev of a cockpit-fixed point < 0.5 px over 10 s |
| S5.N5 | worker-thread loader | 200 distinct models loaded with no exception (ship-render §1.4) |
| S5. Subnautica underwater p99 ≤ 16.7 ms with the 2,000-block ship (HYPOTHESIS budget) |
| S5.N7 | the player's character | N7a: the body's entity pose follows SE's character within 1 cm in third person, and in the SeBones source the pelvis, head and right-palm bones match SE's absolute bones within 1 cm (recomputed through `wire_ref.py`); N7b: the `HiddenInFirstPerson` part is not drawn in first person and is drawn in third person; the fallback model is used only with a logged reason |
| S5.T-P1 | baselines: each game alone, 5 min at the same spots; combined: Space, Approach/Track hover and surface sailing, Prime approach, Underwater cruise (with Subnautica physics at 50 Hz while proxies are visible, from S6 on in S8.T-P1r), 5 min each; one extra run with Subnautica on CCX1 (mask 0xFF00) as an experiment only (D10: no affinity in v1) | SE simulation ratio ≥ 0.98 in every state; SE frame time in Space and in Track within 5 % of SE alone; Prime duty cycle logged; Subnautica underwater p99 ≤ 16.7 ms; commit < 85 % of RAM, no page-file growth; SE warm-up hitch < 50 ms per frame |

**Seeded failures**

| Mutant | Expected |
|---|---|
| S5.M1 grids interpolated one tick behind the camera | S5.N4 far above 0.5 px |
| S5.M2 per-model flip off | S5.G1-E4 red on the listed inverted models |
| S5.M3 PartRemove dropped | S5.N1 count mismatch after a grind |
| S5.M4 Subnautica idle recipe off in Space | S5.T-P1 Space criterion red |
| S5.M5 cameras left on in Track | S5.T-P1 Track criterion red |

**Builder contract.**
- Mutant insertion points: S5.M1 in the Subnautica pose sampler (roots and C2 subpart rows sampled at the snapshot
 before the bracket's newer tick, the S3.M4 "Previous" path moved to the consumer); S5.M2 in the Subnautica mesh
 build pass per-model flip (measure logged, flip skipped); S5.M3 in the SE C4 producer (PartRemove not written, still
 counted as sent, so the consumer audit does not resend); S5.M4 and S5.M5 in Subnautica's perf recipes (Idle replaced
 by Full in Space; cameras kept on in Track). Switched by `de.mutant S5.Mx on|off` on the side that owns them.
- `launcher.py s5 --mode S5a|S5b`: S5a = Subnautica with the fake producer replaying a `.derec` recording
 (`--replay <name>`) or a synthesised ship (`--synth 500|2000|5000`, built from SE models read in place); S5b = both
 games. Parts: `g1e4`, `n1` (counts, 20 random parts, no harm, teardown), `n2` (weld, grind, damage, paint, stage),
 `n3` (rotor 10 rpm, piston, door), `n4` (cockpit coherence), `n5` (loader, SE), `n6` (scale), `n7` (character),
 `record` (SE `de.s5 record`), `teardown`; `launcher.py tp1 --state base-se|base-sn|space|track-hover|track-sail|prime|
 underwater [--ccx1]` for T-P1 (5 min each, `measure.ps1` at 1 s plus both per-second JSONL and SE STATISTICS).
- JSONL rows (`"ev"`; SE in `se-*.jsonl`, Subnautica in its own JSONL): SE `s5.grid`, `s5.part` (the 20 N1 parts with
 rows and P*), `s5.edit`, `s5.sub` (N3 matrices), `s5.char`, `s5.model` (path, counts, load ms or exception),
 `s5.coh` (N4 SE-side pixel), `s5.budget` (per second: C3/C4 ms and bytes, queue depths, back-pressure, `c2Dropped`,
 entities); Subnautica `s5.model` (winding agreement, flipped), `s5.grid`, `s5.placed`, `s5.edit` (the
 `presentedSeTick` at which an edit became visible), `s5.sub` (alpha-1 evaluation), `s5.coh` (per-frame screen
 position), `s5.scale` (per second: chunks, renderers, draw estimate, vertices, mesh and texture bytes, frame p50/p99,
 upload ms), `s5.char` (source, coverage, follow error, hidden parts), `s5.rig`, `s5.perf`. Analyzer
 `tests/s5/analyze_s5.py` rows: `S5.G1-E4`, `S5.N1.C1`, `S5.N1.C2`, `S5.N1.C11`, `S5.N1.C12`, `S5.N2.<edit>`, `S5.N3.<rotor|
 piston|door>`, `S5.N4`, `S5.N5`, `S5.N6.<500|2000|5000>`, `S5.N7a`, `S5.N7b`, `S5.N7c`, `S5.T-P1.<state>` (PASS, FAIL,
 MISSING, INFO for `ccx1`).

*Pilot the T-P1 states (or
approve a scripted autopilot). Judge a real ship underwater (captures and live), and the character: swimming in third
person and the first-person arms with `de.s5 fpbody=se` (S5.N7c). Approve the baseline launches
(counted in section 10) and the combined ones.

**Exit.

**Proves** (*real run*): real SE ships of the tested sizes look native and stay in sync, within the frame budget, on
this PC; surface play on the dive planet costs SE no more than 5 %. **Does not prove:** proxies and combat (S6),
interiors and lights (S7), long sessions (S8).

---

### S6 Collision B + combat

**Goal.** SE grids and the swimming SE character exist physically in Subnautica at 50 Hz physics. Creatures avoid
them, collide with them and bite, shock or blast them; each hit reaches SE as damage in the contract's unit and scale,
exactly once. The Freedom mode is asserted.

**Builds on:** S5. **CONTRACT first:** §4 (unit, factors `k_char`, `k_small`, `k_large`, damage types `DeepBite`,
`DeepBlast`, `DeepShock`, validation, dedup), §2.6 (physics rate, K, refresh, character section, `SyncTransforms`),
§2.7 (hit point, subgrid frame, fallback), §5.4 C8, collision-skin record. The factors are chosen in CONTRACT (O7: the
S6 thresholds then change, not the checks).

**Facts for the scale factors**:

| Side | Value | Source |
|---|---|---|
| Subnautica bites | boneshark 30, crabsquid 40, reaper 80 (every 4 s), ghost leviathan 85; ampeel shock 15 Electrical (every 2 s, no dealer passed); crashfish ≤ 50 Explosive (radius 5 m, falloff to the target's `LiveMixin` transform, no dealer passed); Player health 100. `ReaperMeleeAttack.cyclopsDamage`: class default 160, prefab 220, only against a `SubControl` | prefab data (subnautica.md §3c), `ShockerMeleeAttack`, `Crash.cs`, `DamageSystem.RadiusDamage` |
| SE components | SteelPlate MaxIntegrity 100, MetalGrid 30 | `Content\Data\Components.sbc` |
| SE block MaxIntegrity | Σ count × component MaxIntegrity (`MyCubeBlockDefinition`): LargeBlockArmorBlock 25 × 100 = 2,500; LargeHeavyBlockArmorBlock 150 × 100 + 50 × 30 = 16,500; SmallBlockArmorBlock 100; SmallHeavyBlockArmorBlock 5 × 100 + 2 × 30 = 560 | `CubeBlocks_Armor.sbc` |
| SE character | Health max 100, up to 150 with `MaxHealthBuff` levels | `Content\Data\Stats.sbc`, `EntityComponents.sbc` |
| SE block damage path | `MySlimBlock.DoDamage` multiplies by `BlockGeneralDamageModifier`, min(grid, root `GridGeneralDamageModifier`), the definition's `GeneralDamageMultiplier` and `DamageRatio` (2 − BuildIntegrity/MaxIntegrity); all 1 for a fully built stock armor block in a default world. It does nothing unless `MyCubeGrid.BlocksDestructionEnabled` (world `DestructibleBlocks` on, grid not immune) | decompiled `MySlimBlock`, `MyCubeGrid` |
| SE character damage path | `MyCharacter.DoDamage` returns false when the character cannot die; `MyCharacter.CharactersCanDie` is false in Creative (`MyFakes.CHARACTER_CAN_DIE_EVEN_IN_CREATIVE_MODE` = false) | decompiled `MyCharacter`, `MyFakes` |
| Subnautica physics rate | 0.1 s unless a high-precision behaviour is registered (then 0.02 s): `RegisterHighFixedTimestepBehavior(MonoBehaviour)` / `DeregisterHighFixedTimestepBehavior(MonoBehaviour)` | `MainGameController.UpdateFixedTimestep` |
| SE block damage return | `MySlimBlock.DoDamage(…, sync: true, …)` returns true even when nothing is applied (destruction off, safe zone); the applied amount is the drop of `Integrity − AccumulatedDamage` (integrity itself changes after the grid's `OnceAfterSimulation` pass) | decompiled `MySlimBlock`, `MyCubeGrid`, `MyComponentStack` (contract-s6) |

So the SE combat lab world must be **Survival with destructible blocks**, the SE twin of Subnautica's
Creative/`NoAggression` trap.

**Work items**

| Part | Items |
|---|---|
| SE plugin | Collision skin per grid and per subgrid (in its own frame) from SE's own decomposition (`MyCubeBlockCollector` logic / `MyGridShape` shapes: merged armor boxes, per-block convex pieces as OBBs in v1), split so no half extent exceeds 4 m; sections of ≤ 16 m (CONTRACT §2.6); versioned, resent on block add/remove/integrity events. **Settled by contract-s6 (CONTRACT §5.4 "C4 type 6 `SkinSet` (S6)"):** the collector and `MyBlockVerticesCache` are `internal`, so the logic is re-implemented on public members with SE's own public `MyVoxelSegmentation` (`Simple`, 0 iterations, as `MyGridShape`); model-collision pieces as OBBs from `HkShape.GetLocalAABB`; slopes as full cells in v1; a fixed section lattice (6 cells large, 32 small); resent on block add/remove only (integrity-only changes do not alter a v1 skin); hooked in `GridMirrorProducer.S5UpdateGrids` by one call. Damage consumer: validation (generation, epoch, amount ≤ 500 SN_HP, known entity and section), dedup by id (window of 8,192 ids, CONTRACT §4.1); SE-local hit point in the frame of `targetEntityId` → that grid's cell → block; `MySlimBlock.DoDamage(originalDamage × k_small or k_large, MyStringHash.GetOrCompute("DeepBite" \| "DeepBlast" \| "DeepShock"), sync: true, hitInfo, attackerId: 0)` with the applied amount read as the drop of `Integrity − AccumulatedDamage` (its return value does not tell), or `MyCharacter.DoDamage(originalDamage × k_char, type, true, 0)` gated by `CharactersCanDie`/`CharacterCanDie`; `DE.Damage applied id=... unit=... k=... se=...`. C8 read through an `EntryRing` the `Link` owns (re-synced with the other consumer tails). Character pose for the character section in C1 (no character `SkinSet` in v1) |
| SN plugin | One unit-scale root per section, kinematic Rigidbody, layer Default, BoxColliders with K pre-scaling and the refresh rule of CONTRACT §2.6 (1 cm threshold with half extents in `ρ_max`, orthonormalised children for rotated boxes, at most 256 shapes per physics step, round robin), moved with `MovePosition/MoveRotation` while unfrozen; never a child of the scaled Frame. `RegisterHighFixedTimestepBehavior` while any moving proxy is visible, unregistered otherwise; `fixedDeltaTime` in C7. Character section: standalone root, kinematic Rigidbody, non-trigger capsule (radius 0.25 m, height 1.8 m), posed from C1, disabled when dead or seated. Per section: `LiveMixin` + `LiveMixinData` (`invincibleInCreative` false), zero-damage `DamageModifier` (or `shielded`), `EcoTarget` Shark (2030) anchors, marker component. Postfix on `LiveMixin.TakeDamage` emitting C8 records for marked LiveMixins and `Player.main.liveMixin`; start-up damage self-check. Postfix on `MeleeAttack.CanDealDamageTo` accepting the marker; prefix on `MeleeAttack.OnTouch` stashing the collider and the mouth position. Local hit point by `ToLocal` with the section's stored affine of the last physics step, then into the subgrid frame for subgrid sections; fallback hit point (closest collider point to the dealer) when no stash exists. `Physics.SyncTransforms` before every own query. Damage records into a bounded queue with the CONTRACT's full policy. Speculative CCD on the section bodies (HYPOTHESIS that it helps). **Settled by contract-s6 (CONTRACT §2.6, §2.7, §4.2):** the deregistration method is `DeregisterHighFixedTimestepBehavior`; a prefix on `TakeDamage` records the game's gate for the postfix; the ampeel (`ShockerMeleeAttack.OnTouch`) and crashfish blasts (`DamageSystem.RadiusDamage`) pass no dealer, so prefix/finalizer pairs on both supply the dealer position (and the radius flag); sections are also `shielded`; the character capsule is centred on the C1 pelvis bone; subgrid sections are posed with `q·R(rel)` and keep their boxes in the subgrid frame; proxies follow the mirror's applied pose of the latest camera sample (hook in `MirrorConsumer.OnCameraSample`), and `SkinSet` reaches them through `MirrorState.Feed` (one case) |
| tools/tests | B-T1 scene (synthetic skin from the fake producer, or a recorded one); fake SN host that sends synthetic and malformed damage records to the real SE; spawns through `DevConsole.SendConsoleCommand("spawn reaperleviathan 1 30")` relayed by `de.spawn`. **Contract-s6 notes:** the spawn places each creature at `camera + 30 m·forward` or at the first hit of a raycast on all layers along it, so the camera looks at open water, not at a proxy; every session with the real SE passes `--damage-rate 0` to the fake SN host (its S0 synthetic records would otherwise reach the S6 consumer as rejected noise); the fake SE producer drains C8 into `s6.c8` rows for the Subnautica-alone parts; `wire_ref.py s6bound` gives the S6.G1-E3 bound |

**Oracle**

| Check | What | Pass |
|---|---|---|
| S6.B-T1 | Subnautica only: synthetic proxy 20×6×6 m, 50 boxes, 2 sections, looping at 5 m/s; 20 peepers, 1 boneshark, 1 reaper | (1) `Physics.ComputePenetration` between every creature collider and the proxy: no penetration > 0.1 m for more than 3 consecutive 0.02 s steps over 3 min; (2) at least one forwarded record with `originalDamage` 30 (boneshark) and 80 (reaper), proxy health unchanged; (3) proxy update + PhysX time with 1,000 boxes p95 ≤ 1 ms per physics step |
| S6.G1-E3 | WM-1 `box` records (including rotated boxes) as real colliders, after `SyncTransforms` | `ClosestPoint` probes within 1 mm level; tilted: within the bound the tool computes for the tested depth and tilt (`wire_ref.py` with `SITE_V1`, up to −2,000 m), not the gap-1 §4.4 table |
| S6.N1 | SE only, fake SN host: 100 synthetic bite records including 10 duplicates, sent to one small grid and one large grid and to the character | small-grid integrity delta = Σ originalDamage × `k_small` (1.0) and large-grid delta = Σ × `k_large` (25) over unique ids (tolerance 1e-3); character health delta = Σ × `k_char` (1.0); duplicates applied 0 times |
| S6.B-T1e | end to end (prior-art S5): a reaper spawned 30 m from a real SE grid's proxy; a boneshark near the swimming SE character (character section) | one `DE.Damage applied` per Subnautica hit id; block integrity delta = 80 × `k_large` (large grid); damaged block is the one under the SE-local hit point (± 1 cell); character health −30 × `k_char` |
| S6.N2 | Freedom assertion | `mode=Freedom invisible=false`; combat tests refuse to start otherwise |
| S6.N3 | avoidance | sampled peeper paths never cross a section (`AvoidObstacles` uses mask −5 only on prefabs with `avoidTerrainOnly` false; the class default is true, which scans the terrain layer 30 alone, so on such prefabs contact keeps them out; the probe logs each spawned creature's value — contract-s6) |
| S6.N4 | line of sight on a long hull | bites still land on a 100 m hull with rock between the creature and the hull centre (sections) |
| S6.N5 | crashfish | a crashfish detonating near a proxy gives one `DeepBlast` record, amount ≤ 50 × k, radius flag set |
| S6.N6 | ampeel | an ampeel touching a proxy gives `DeepShock` records of 15 × k with the fallback-hit-point flag (the hit point from the ampeel's position, supplied by the `ShockerMeleeAttack.OnTouch` context: the game passes no dealer) |
| S6.N7 | physics rate | `Time.fixedDeltaTime` = 0.02 while a moving proxy is visible and 0.1 otherwise (C7) |
| S6.N8 | hit on a rotating subgrid (rotor at 10 rpm with a small-grid head) | the damaged block is the one under the hit point in the subgrid's frame (± 1 cell); the record carries the subgrid's id and the "frame = subgrid" flag |
| S6.N9 | damage self-check at start | 1 SN_HP of `TakeDamage` on a test section and on the Player gives exactly one record each (C7 `damageSelfTestPass`) |
| S6.N10 | record validation | the fake SN host sends 100 malformed records (old generation, old epoch, amount 0 or > 500, unknown entity or section): none applied, all counted |

**Seeded failures**

| Mutant | Expected |
|---|---|
| S6.M1 proxy colliders set as triggers | S6.B-T1 (1) red |
| S6.M2 `CanDealDamageTo` postfix removed | S6.B-T1 (2) gets no bites |
| S6.M3 dedup off | S6.N1 applies the 10 duplicates |
| S6.M4 SE lab world in Creative | S6.N1 character part red (proves the gate) |
| S6.M5 colliders parented under the scaled Frame, Entity rotated 30° | S6.G1-E3 deviation beyond the bound |
| S6.M6 host slot in Creative (throwaway copy, rule 10) | S6.N2 refuses |
| S6.M7 `k_small` and `k_large` swapped | S6.N1 red on both grids |
| S6.M8 character capsule parented under the locked Player | S6.B-T1e gets no character bites |
| S6.M9 `SyncTransforms` skipped | S6.G1-E3 probe after a 30° re-pose reads the stale pose (red) |
| S6.M10 radius damage not forwarded | S6.N5 red |
| S6.M11 hit point left in the root grid's frame | S6.N8 damages the wrong block |
| S6.M12 late `AddComponent` forwarder on the Player instead of the `TakeDamage` postfix | S6.N9 red (no Player record) |

Where each mutant lives (contract-s6): S6.M1, M2, M5, M8, M9, M10, M11, M12 are Subnautica code switches (M10 drops
the records built inside a `RadiusDamage` context before C8: the forwarding is Subnautica's, so S6.N5 turns red even
with the stand-in SE); S6.M3 and M7 are SE code switches; S6.M4 and M6 are world choices made by the launcher (the
Creative DE Lab for the SE-alone n1 part; a Creative throwaway host-slot copy for the selftest part), not code.
S6.M5 offline: modelled with
Unity's `lossyScale` for a collider under the scaled Frame (HYPOTHESIS until the real run), the mutant is red through
the **grid-aligned** boxes placed as BoxColliders on the Entity (`center = Mirror(b)`, scaled by `lossyScale`): 0.077 m
at −150 m, 0.52 m at −1,000 m with the 30° turn, against bounds of 0.012 m and 0.080 m; a rotated box on its own
child is no worse than the K method under that model, and a level hull hides the mutant (0 m), so the in-game M5 must
turn the Entity by 30° about a horizontal axis and probe the grid-aligned WM-1 boxes (their section centres lie up to
50 m from the Entity origin).
Integrator notes: the crashfish picks only a target whose Rigidbody reports more than
`minimumVelocity` 0.2 m/s (`AggressiveWhenSeeTarget.IsTargetValid`, Crash prefab), so S6.N5 needs a moving proxy: the S6a
n5/n6 hull loops at 1 m/s on a 2 m radius, and whether a kinematic body moved by `MovePosition` reports that velocity is
a HYPOTHESIS (a static S6b grid may give no blast). S6 is on by default in both plugins (`DEEPENGINEERS_S6`); the
launcher sets it off for every non-s6 session and for `run` unless the environment says on.

**API.** CONTRACT §2.6, §2.7, §4.1/§4.2 and §5.4 ("C4 type 6 `SkinSet` (S6)", "C7 for S6", C8
field meanings, "S6 combat conventions", hash `0xb6ee880712f8253b`) are settled against the decompiled build; layout
1.0 is unchanged. The class and command list the builders code against is the contract-s6 result, copied into the S6
status document by the integrator.

*** Approve a Survival copy of the SE lab world with destructible blocks. Approve console spawns in
the host save (relayed by `de.spawn`) and the throwaway host-slot copy for S6.M6. Watch a reaper bite and judge it.
Approve launches.

**Exit.

**Proves** (*real run*): Subnautica's creatures fight SE ships and the SE character through the bridge, with SE
health authoritative and no double counting. **Does not prove:** other damage dealers (`AttackCyclops`, reaper
grabs, `AttachAndSuck`; section 8), ramming impulses, long fights under load.

---
### S7 Interiors, lights, thrusters, audio polish, time of day, statics into SE, skinned astronaut

** Inside the site below the Approach
> altitude the picture is Subnautica's; SE's only in Space (CONTRACT "Picture on screen" changes). Items: view policy
> (see-through for Approach/Surfacing inside the zone, margins off), camera parking only in Space, the block-placement
> ghost mirrored into Subnautica, Subnautica walk/idle animator parameters for the SE character out of the water, SE's
> sun driving `DayNightCycle` (S7.WM-5 pulled forward), T-P1 re-run and the S2 oracles re-stated. The Water Mod stays as
> physics and as the planet from space. Estimate 1–.
>
> **S7a contract.** CONTRACT "Picture on screen" and the ownership rows, §5.4 "C1 locomotion
> extension", "C5/C7 semantics for S7a" and "S7a conventions" (`ext s7a picture 3`, `0x4257c11c17ff772a`; layout 1.0
> unchanged), ARCHITECTURE §4.2 "Picture policy (S7a)" and §5.12 "S7a". Wording of the note above made exact: the
> margins are **on** in every non-Space state (design F cannot show Subnautica without them); "margins off" holds for
> Space only. `Waterline` (S2's policy) stays selectable as the fallback. In short: a picture latch in the view machine
> turns on in Approach once Subnautica is ready (`snReady`, z-order, margins applied, 0.25 s dwell, a frame presented
> since the band entry) and off in Space or on a site, peer or re-streaming loss; Subnautica renders Full in every
> non-Space state and is parked only in Space; the crossing fade follows the latch; the placement ghost is a one-part
> synthetic grid (`GhostEntityId`, GridAdd flag `Ghost`, C2 flags shown/allowed) drawn translucent green or red; the C1
> pad 0xA0–0xAF carries the SE character's movement state so Subnautica's walk, idle, jump and dive clips drive the
> astronaut out of the water; SE's sun reaches Subnautica through C6 (hour angle, sun direction and, in its pad, the
> sun's pole), and Subnautica's paused `DayNightCycle` is advanced forward only so its sun **disk** stands exactly where
> SE's does (`SunDirection`, `NorthPoleOffset` from the pole); grids are mirrored within 1,000 m above water.
>
> **S7a oracle**
>
> | Check | What | Pass |
> |---|---|---|
> | S7a.N1 | picture per state: a spectator path Space (+2,000 m) → +400 → +250 m (Track, 10 s) → +3 m (Prime, 10 s) → −5 m (Underwater, 10 s) → +3 m (Surfacing → Prime) → +150 m → +500 m (Space), then 8,000 m out (FAR); SE `s7a.view` / `vsm` rows, Subnautica `s7a.picture` rows, a capture pair at +150 m | every state seen ≥ 1 s; Space: `seeThrough` 0, Idle applied, parked; every non-Space row after the latch: `seeThrough` 1, margins on, Full applied, not parked, `HideAllowed` false, C5 bit 9 set; the latch on within 2.0 s of the later of the Approach entry and the +300 m crossing on a settled descent (HYPOTHESIS budget), never above +300 m; **zero picture edges within ±2 s of each waterline crossing** while the audio still crossfades there; FAR and off-site: Space
> | S7a.N2 | the swap at the band: 5 descents +600 → +200 m at 20 m/s and 2 at 100 m/s (predicted arrival), the same ascents, and 20 s hovering between +300 and +350 m | exactly one SE → Subnautica edge per descent and one back per ascent, none while hovering; at 100 m/s the latch is on before `y` < +150 m; an `s7a.flash` row of kind `band` within 1 SE tick of every edge (the fade covers the swap); the sun alike: at every edge the angle between C6 `sunDirSn` and Subnautica's `uSkyManager.SunDir` ≤ 0.25° |
> | S7a.N3 | placement ghost: `de.py` ≤ 1 mm and 0.01° on rows with bracket alpha 1; colour green ⇔ `m_buildAllowed` on 100 % of rows outside 3 ticks after a change; latency (SE tick of a pose change → first presented Subnautica frame using it) ≤ 3 ticks p95, ≤ 6 max; shown within 2 s of `probe` for a loaded model (5 s first load), removed within 1 tick of `stop`; no `s6.*` row or proxy names the ghost id; S5's counts (S5.N1 rows) unchanged by it |
> | S7a.N4 | astronaut on a deck above water (a grid platform at the surface; `de.swim {script}` stands 5 s, walks 10 s, jumps once, steps off into the sea;s5 fpbody=diver` | standing: `move_speed` < 0.1 and a `player_view_idle` clip on ≥ 90 % of rows; walking: `move_speed` ≥ 0.5 and a `player_view_run*`/`sprint*` clip on ≥ 90 %; source Retarget on ≥ 95 % of foot-state frames; the jump shows `jump` true after 0.45 s of air; in first person the diver arms are shown on ≥ 95 % of foot-state frames
> | S7a.N5 | time sync: 5 min at 2 s samples (SE `s7a.sun`, Subnautica `s7a.sun`), `de.s7 time sync off` 10 s then on, one camera teleport 3 km east and one 3 km west; optionally `de.s7 time 0.26/0.5/0.74` if SE's renderer takes the dev override | angle(C6 `sunDirSn`, `uSkyManager.SunDir`) ≤ **0.25°** on ≥ 99 % of samples outside holds and ≤ 2.25° always (tolerance from the sun math: exact mapping, float32 and the 0.01° pole step ≤ 0.02°, one frame of transport ≤ 0.02°, margin for the frame order); `IsDay` = (C6 elevation > 0) whenever the elevation is beyond ±0.3°; `timePassedAsDouble` never decreases; `dayNightSpeed` 0 while synced; sync off → speed 1 within 1 s, on again → ≤ 0.25° within 1 s; the west teleport gives a hold or one logged forward jump, the east one an advance |
> | S7a.T-P1r | T-P1 re-run (`launcher.py tp1 --state space|track-hover|track-sail|prime|underwater` plus `base-se`/`base-sn`, 5 min each, picture `site`; one `--picture waterline` track-hover for comparison) | SE simulation ratio ≥ 0.98 in every state; SE frame time in Space within 5 % of SE alone; Subnautica p99 ≤ 16.7 ms in track-hover, track-sail, prime and underwater (all Full now); commit < 85 % of RAM, no page-file growth; SE frame time in Track vs SE alone recorded as INFO: S5's "Track within 5 %" criterion no longer applies (Subnautica renders Full there by design
>
> **S7a seeded failures**
>
> | Mutant | Side | Expected |
> |---|---|---|
> | S7a.M1 the machine runs `Waterline` while `Site` is selected (`ViewMutants.S7aM1Waterline`) | SE (Wire) | S7a.N1 red (Approach rows show SE's picture, picture edges at the waterline) |
> | S7a.M2 camera parked by view as S2 (Space or Track) instead of `perfMode ≠ Full` | SN | S7a.N1 red (parked while the picture shows in Track) |
> | S7a.M3 no crossing fade on a picture edge | SE | S7a.N2 fade red |
> | S7a.M4 ghost `ghostAllowed` always set | SE | S7a.N3 colour red (green while blocked) |
> | S7a.M5 out-of-water `move_speed*` forced to 0 | SN | S7a.N4 walk red (idle clip while walking) |
> | S7a.M6 time drive writes `timePassedAsDouble` straight from the target (backward allowed) | SN | S7a.N5 monotonic red (the west teleport steps time back) |
>
> **S7a pass contract (exact names in CONTRACT §5.4; three builders, each owning only its files).**
> - *Policy, camera, fade.* Wire `ViewStateMachine.cs` (`ViewPicture`, `ViewInputs.Picture`, machine state `Picture`,
> `BandTick`, `PicSwitched`, `PicSwitchUs`, outputs `Picture`, `PictureEdge`, `PictureBlocked`, `PictureReason`,
> `PicturePolicy`; `ViewThresholds.PictureDwellUs`; `ViewBlock.Band`/`NotApproach`/`High`; `ViewReason.PictureOn` …
> `PictureOffResettle`; `ViewMutants.S7aM1Waterline`; `MarginsWanted`, `PerfFor`, `EffectivePicture`; `ApplyTo`
> writes C5 bit 9), `ViewTrace.cs` (row version 4: input `pp`; state `s_sp`, `s_bt`, `s_ps`, `s_pt`; outputs `o_sp`,
> `o_pe`, `o_pb`, `o_prs`, `o_pp`), `S7aConventions.cs` (the canonical text verbatim), `tests/s2/state_traces.py`
> (version 4; rows of version ≤ 3 replay as `Waterline`; scenarios `band`, `band-fast`, `band-hover`, `site-dive`,
> `site-peer`, `site-teleport`, `site-aprime`; `--picture site|waterline`, default site); SE `ViewStateDriver`,
> `DisplayController`, `CrossingFlash` (sky tint), `PicturePolicy` (env, `de.s7 picture`), the S5 `EntitySet` radius
> hook; SN `S2Rules` (`CameraParkRules.Park(bool c5Valid, uint perfMode)`, the unfocused rule,
> `ExpectedPerfMode(view, approach, pictureSite)`), its callers in `S2Host`, the S5 chunk-culling radius hook, the
> `S1Telemetry` hook that centres the `areaBuilt` camera box at `min(y, +140 m)` under `Site` (raw box logged).
> **Re-stated pins (never deleted):** the default input is `Site`, so every S2 test that pins the picture
> (`ViewStateMachineTests`: `PerfModeAndTargetFps_PerState`, `PrimeToUnderwater_EachGuardBlocksAlone`,
> `Surfacing_MarginsOffOnlyWhenLeavingPrime_SceneFirst`, `CrossingOrder_HoldsOverRandomDivesAndRenderLatencies`,
> `ZOrderReassert_PulsesAtPrimeEntryRefocusAndLoss`, `APrime_LowersInsteadOfMargins_MenuRaises_DrawBeforeRaise`,
> `PeerStalledOrExited_UnderwaterSurfacesWithinOneTick`, `Teleport_Underwater_SurfacesUntilSettledAfterTheTeleport`,
> `ApplyTo_WritesTheC5Fields_KeepsCallerFlags`, and the `ViewSim` helpers; SN `S2Tests`
> `S2Recipe_ExpectedPerfModeOfTheViewState`, the `CameraParkRules` and window-style cases; the twin's request and perf)
> keeps its S2 expectation under an explicit `Waterline` input with the comment "S2 policy, the fallback", and gains a
> `Site` statement of what S7a wants and why (Track Full, margins from the Approach entry, skip = latch, no swap at the
> waterline, Surfacing one tick under the latch, parking only in Space). The S2.N2 traces keep counting Underwater
> switches (the states did not change).
> - *Placement ghost.* SE `PlacementGhost` (`GhostReader`: one cached `FieldInfo` for `MyCubeBuilder.m_gizmo`),
> minimal hooks in `GridMirrorProducer.S5.cs` (tick call, reserved first C2 row, the audit's sent count); Wire
> `S7aGhost.cs` (GridAdd/PartAdd/C2 row builders); SN `GhostView` with hooks in the S5 chunk path (materials after a
> build, visibility from C2 bit 2, colour from bit 3) and the C2 reader; `tests/s5/analyze_s5.py` skips the ghost;
> mutant S7a.M4.
> - *Astronaut, sun, tools.* Wire `S7aLoco.cs` (C1 codec), `S7aSky.cs` (C6 pole codec), `SunMath.cs`; SE
> `LocoWireFill`, `SunSync`, hooks in `FrameWriter` (C1 loco bytes, the C6 record), `S7aCommands` (`de.s7`
> dispatcher; SN `S7aHost`, `DayNightApply`, `S7aAnimRules`
> hooks in `S5aHost`/`S5aRules` (out-of-water drive, arms gate), `CharacterMirror` (source rule), `FrameReader` (C6,
> loco), `LabScene` (`de.time` refused while synced); `tools/worldmap/worldmap_ref.py s7asun` (reference sun mapping
> and vectors), `tests/s7a/analyze_s7a.py` (rows below, `--selftest`), `src/DeepEngineers.Launcher/launcher_s7a.py`
> (registered by `launcher.py`); mutants S7a.M5, S7a.M6.
> - `launcher.Mx] [--deck <blueprint>] [--dry-run]`
> both games, DE Lab, the S5 session rules (S5-STATUS §9.1; character parked at SN (24, −8, 0)); parts `n1`
> (spectator path, captures), `n2` (band descents/ascents/hover), `n3` (ghost probe, aims, stop), `n4` (deck, scripted
> walk, first person), `n5` (sun samples, sync off/on, east/west teleports, optional dev phases), `teardown` (`de.s7
> ghost stop`, sync on, picture site, mutants off); mutant groups: M1 → n1, M2 → n1, M3 → n2, M4 → n3, M5 → n4, M6 →
> n5; `launcher.py tp1 --picture site|waterline` for S7a.T-P1r. Output `tests/out/s7a/<stamp>/` (`s7a-session.jsonl`,
> `se-*.jsonl`/`sn-*.jsonl`, logs, captures, `s7a-table.txt`, `s7a-results.json`); `s7a --analyze <folder>`.
> - JSONL rows (`"ev"`): SE `s7a.view` {tick, view, approach, policy, picture, edge, reason, pictureReason, blocked,
> pictureBlocked, bandTick, y, vy, rho, zone, snReady, request, renderSeq}; `s7a.flash` {tick, kind band|waterline, toSn, durationMs} (every fade); `s7a.ghost` {tick, active, shown,
> allowed, def, model, modelKey, cubeSize, mode grid|voxel|dynamic, parts, seWorld {o, rows}, snPose {o, q}, c4
> counts} (every change, 10 Hz while active; as built also entityId, enabled, partsVersion, reason (why inactive),
> `seWorld.rows` = [[Right], [Up], [Backward], [Translation]], snPose.s, c4 {gridAdds, partAdds, partRemoves,
> entityRemoves}, latency {lastTicks, p95Ticks, maxTicks, samples, presentedSeTick}: SE ticks from a pose change to the
> first SE tick whose C7 presentedSeTick is at or past it, and mutantS7aM4 while S7a.M4 is on; `buildAllowed` = SE's own `m_buildAllowed` beside `allowed` = the C2 flag
> sent, which S7a.M4 forces: S7a.N3.colour compares with `buildAllowed`, integrator); `s7a.loco` {tick, state, raw, flags, vF, vS, vU, yawRate, inWater}
> (every change, 2 Hz); `s7a.sun` {tick, checks, H, c, elevDeg, sunDirSn, pole {az, el}, flags, jumpSeq, devPhase}
> (bind, jumps, `de.s7 status`, 0.1 Hz; 0.5 Hz during `n5`). Subnautica `s7a.picture` {snFrame, presentedSeTick,
> view, approach, perfMode, perfApplied, seeThrough, marginsOn, pictureSite, park, phase, hideAllowed, camY,
> frameMsP99} (every change, 1 Hz); `s7a.ghost` {snFrame, presentedSeTick, c2Tick, present, shown, allowed, material,
> renderers, root {o, q}, alpha, poseErrMm, firstPresentedTick} (every change, 10 Hz while present; as built also
> entity, c2Flags, materialRequest, renderersEnabled, pose {o, q, s, tick, how} (the applied C2 pose), newestTick,
> changeTick (C2 tick of the last pose change), newestAtPresent (newest C1 tick at the presenting sample),
> changes, presentedChanges, latencyLastTicks/latencyP95Ticks/latencyMaxTicks (newestAtPresent − changeTick), builds,
> swaps; poseErrMm only on samples drawn at alpha 1); `s7a.anim`
> {snFrame, seTick, source, locoState, inWater, params {is_underwater, on_surface, move_speed, move_speed_x/y/z, jump,
> diving}, clips, armsShown, fpbody, walkSource} (every change, 2 Hz); `s7a.sun` {snFrame, c6Tick, jumpSeq, H,
> cTarget, sTarget, sNow, cNow, timePassed, step advance|hold|jump|none, advancedS, sunDirectionDeg,
> northPoleOffsetDeg, sunDirC6, sunDirSn, errDeg, isDay, c6ElevDeg, dayNightSpeed, lightElevDeg} (every 2 s, each
> hold or jump, `de.s7 sun`).
> - Analyzer rows (`tests/s7a/analyze_s7a.py`; PASS, FAIL, MISSING, INFO): `S7a.N1.space`, `S7a.N1.track`,
> `S7a.N1.prime`, `S7a.N1.underwater`, `S7a.N1.surfacing`, `S7a.N1.waterline`, `S7a.N1.far`, `S7a.N1.capture` (INFO),
> `S7a.N2.swap`, `S7a.N2.fade`, `S7a.N2.sun`, `S7a.N2.hover`, `S7a.N3.pose`, `S7a.N3.colour`, `S7a.N3.latency`,
> `S7a.N3.lifecycle`, `S7a.N3.noproxy`, `S7a.N4.idle`, `S7a.N4.walk`, `S7a.N4.jump`, `S7a.N4.arms`, `S7a.N5.err`,
> `S7a.N5.isday`, `S7a.N5.monotonic`, `S7a.N5.syncoff`, `S7a.N5.teleport`, `S7a.T-P1r.<state>`.
>
** Judge Subnautica's picture above water and the band swap with its fade; the ghost's look
> (material, green/red); the astronaut walking on a deck (walk source `retarget` or SE's own `sebones`) and the diver
> arms above water; the stylised `SunLight` (up to 25° above the disk at sunrise and sunset). Decide the definition-of-
> done item "surface play costs ≤ 5 %" against S7a.T-P1r (Subnautica now renders Full in the whole band). Approve the
> launches. **Exit:** S7a.N1–N5 green, S7a.M1–M6 red, S7a.
> verdicts recorded.

**Goal.** Complete the look (interiors, lights, thrusters and every SE action's underwater look and sound, the
skinned astronaut, the audio polish) and the second terrain path (wrecks and precursor structures collide in SE,
streamed and censused). Time of day was pulled into S7a and is built there; S7 keeps only its full-day check WM-5.

*
that has a look and a sound in air needs an underwater one, drawn and played by Subnautica from SE events: thrusters
(bubble trail and cavitation ∝ thrust, engine loop pitched by power), weapons (muzzle flash dimmed, bullet bubble
streaks, impact bubbles), explosions (Subnautica's own underwater explosion FX and sound at the SE blast position and
radius), tools (welder sparks, grinder sparks, drill sediment cloud; the repair tool and Prawn drill effects), grid
impacts and hull breaks (bubbles, muffled clang), splashes at the surface (Water Mod side). Transport: one SE → SN
**event record** (type, position, direction, intensity, lifetime) batched per tick, attached to the mirrored grid when
there is one; every effect and sound comes from the player's Subnautica (prefabs and FMOD events found at run time,
never shipped); SE keeps the gameplay (damage, physics).
will it be possible to drain the water so I can walk inside?"): the S7 interior is that answer: an airtight SE room
reads as a dry, walkable, unfogged interior in Subnautica's picture with the water seen through its windows, and SE's
swim model stays off inside it (the swim gate; the Water Mod's own behaviour in the room is O25, checked by S7.N1c).

**Builds on:** S5, S6, S7a. **CONTRACT first: done (contract-s7).** CONTRACT §3.2 and §5.4 "S7 conventions"
(`ext s7 look 1`, FNV-1a 64 `0x1f9b9b4a876a97f9`, 143 lines, 6,583 bytes; **layout 1.0 unchanged, no pin changed**: the
interior flag in C1 bit 3 plus the C1 interior extension 0xB0–0xBF, room records in C4 `RoomSet`, the light table in
C2, the effect events in the C2 region tail 0x40000–0x4FFFF with their window header in `SeEntitiesHeader.pad_1c`,
static primitives in C12, all 81 bones with `ExtraBones`), §2.3 `staticAxes` with the size and capsule rules, the game
members with exact signatures; ARCHITECTURE §5.3 (audio polish A1–A6) and §5.16.

**Work items**

| Part | Items |
|---|---|
| Wire | `S7Conventions` (constants, canonical text, hash, shared rules), `S7Interior` (C1 interior codec, RoomSet flags and boxes, chunk class), `S7Lights` (row codec and order), `S7Events` (row codec, window, caps, priority, sustain), `S7Statics` (C12 codec, `staticAxes` sizes, capsule, host name), `S7Bones` (row order) |
| SE plugin | Interiors: the camera probe per tick (`GetOxygenRoomForCubeGridPosition`, `GetSafeOxygenBlock`, `IsAirtight`, the hold while `IsProcessingData`), C1 flag bit 3, C5 bit 4, the C1 interior extension; `DE.Interior OFF pressurization disabled` once without both world settings; RoomSet (airtight air cells as boxes) from `OnProcessingDataComplete` on change; the swim gate (`CharacterInRoom`, per character id). Effects: thruster and tool sustains polled per tick, postfixes on `MyGunBase.Shoot`, `MyProjectile.OnHit`, `MyExplosions.AddExplosion`, `MyGridPhysics.PlayCollisionParticlesInternal`, `MyGridGasSystem.AddDepressurizationEffects`, the splash test; the capped per-tick buffer and the 8-tick C2 window. Statics: C12 consumer, one non-saved `HkStaticCompoundShape` host per batch and epoch (layer 13) within 300 m of a grid or character, closed beyond 350 m, the census cache. Bones: `ExtraBones` (81 rows). Audio: the `SelectEffect` postfix (A1). Commands and rows of CONTRACT §5.4 "S7 conventions" |
| SN plugin | Interiors: RoomSet store, chunk classes, base-interior sky (`SetCustomSky(MarmoSkies.main.GetSky(Skies.BaseInterior))`), the `IsInsideWalkable` postfix and its caller trace, `WaterClipProxy` boxes for rooms at the surface. Lights: pooled Unity lights, ≤ 2 shadowed spots, the Seamoth calibration. Effects: the window reader, sustains, the effect library found at run time (Seamoth bubbles and engine loop, repair tool, Prawn drill, crashfish detonation, `VFXSurfaceTypeManager`, the construction splash), safety filter, fallbacks, sounds under water only. Statics: the batch scan, the filter by entity type with the census allow-list, epochs, census mode writing the cache. Bones: the extra rows posed flat. Commands and rows |
| tools/tests | `tools/sn-octrees/sn_prefab_colliders.py --census` (every bundle the BatchObjectsCache references, non-Default and mesh colliders counted, the allow-list generated into `%LOCALAPPDATA%\DeepEngineers\census\`; S7.N7), `tools/worldmap/s7_ref.py` (statics geometry reference and vectors), `tools/lab/set_world_settings.py` (oxygen, pressurisation and the M6 sun variant on a world copy; `--dry-run`, `--selftest`), `tests/s7/analyze_s7.py` (rows below, `--selftest` with the Python twin of the canonical text and codecs), `src/DeepEngineers.Launcher/launcher_s7.py` (registered by `launcher.py`), the fake producer's census camera path, image checks for interiors and lights |

The 13 direct readers of `IsInsideWalkable` that the patch touches: `AttachAndSuck`, `BasePipeConnector`, `ColorCorrection`, `FMODGameParams`, `FlyAwayWhenScared`,
`LargeWorldStreamer`, `Player`, `PlayerSoundTrigger`, `TemperatureDamage`, `UnstuckPlayer`, `UseableDiveHatch`,
`WaterscapeVolumeOnCamera`, `uGUI_AdaptiveBrightness`; plus 5 through `Player.IsInside`:
`ArmsController`, `FootstepSounds`, `GasPod`, `UwePostProcessingManager`, `WarpBall` (`SkyApplier` calls
`Constructable.IsInside`, not the Player's).

**Oracle** (DE Lab with `EnableOxygen` and `EnableOxygenPressurization` on unless noted; the S5 session rules; rows of
CONTRACT §5.4 "S7 conventions")

| Check | What | Pass |
|---|---|---|
| S7.N1 | sealed room (light armor 5 × 4 × 5 blocks, inner 3 × 2 × 3 cells, one window block; built by `de.s3` at SN (60, −12, 60) or `--room <blueprint>`); the spectator inside 10 s and 2 m outside 10 s, twice; Subnautica `de.interior {patch: on/off}` A/B with `de.pixel` probes on (a) the inner wall 1.5 m ahead and (b) a point 20 m beyond the window; a wall block removed, then re-placed; an interior block added and removed | **flag:** SE `inRoom` true on 100 % of rows from 2 ticks after each entry to the exit, false outside; Subnautica `inside` = C1 on ≥ 99 % of frames; **fog:** probe (a) differs between patch on and off by ≥ 0.05 (mean absolute RGB, 0–1) and probe (b) by ≤ 0.02 (the interior unfogged, the water through the window still fogged), capture pair looked at (INFO); **breach:** flag false and `breached` within 2 s of the gas system's completion after the removal, Subnautica `inside` false within 3 frames after, true again within 2 s of the re-placement; **flicker:** no flag change while `processing` is set and none outside the breach and re-seal edges |
| S7.N1b | pressurisation off (world copy `DE Lab NoPress`), the same room | flag false on 100 % of rows with the camera inside; `DE.Interior OFF pressurization disabled` exactly once; no RoomSet sent |
| S7. forward speed ≥ 80 % of the dry-land walk (no water drag: O25, HYPOTHESIS)
| S7.N2 | `IsInsideWalkable` side effects: `de.interior {action: trace, seconds: 10}` inside, then outside | every one of the 13 direct and 5 transitive readers listed with its call count inside and outside (not running in the scene: INFO "not called"); the ghost unchanged by the cycle (health equal; position within 0.05 m of the camera rule, no unstuck teleport; O₂ = the mirrored C1 value; no temperature damage); `CanBeAttacked` false inside, true outside
| S7.N3 | lights: an SE Interior Light (default settings) 3 m from a light-armor wall in the room, and a Seamoth (`de.spawn Seamoth 1 3`) with its cabin light at the same distance from a like wall, night or ≥ 200 m deep | luminance ratio (probe on each lit wall point) within ±25 % with the session's `LightIntensityScale` (recorded); ≤ 2 shadowed lights on every `s7.lights` frame; `lightCount` ≤ 64; no row beyond 60 m; capture looked at (INFO) |
| S7.N4 | effects: a test ship (4 hydrogen, 4 ion thrusters, a gatling gun, a ship welder, grinder and drill, a target plate) at SN (0, −20, 80); thrust override 0/0.25/0.5/0.75/1.0 for 3 s each; 20 × `ShootOnce`; ship tools 5 s each; injected explosions at radius 2, 10 and 30 m; a small grid dropped on the ship and one into the sea from +10 m; the N1 room breached under water; `de.fx {action: stress, n: 500}`; the thruster test repeated at SN y +2 m;rate:** Subnautica emission per flame vs `CurrentStrength` linear (r² ≥ 0.9), zero within 0.25 s of strength 0; **thruster.rpm:** loop `rpm` = the rule ± 0.05 on ≥ 95 % of frames; **thruster.log:** at constant strength ≤ 1 sustain row per flame per 15 ticks (+1), each step answered within 1 tick; **weapon:** 20 muzzles and 20 streaks sent, none dropped, all applied (no seq gap), latency ≤ 3 frames p95, attached positions within 0.05 m; **impact:** as many impacts as SE `OnHit` calls on the plate, within 0.1 m; **explosion:** at the blast position within 0.5 m, scale ∝ radius within ±10 %, source logged; **tools:** each sustain shown ≥ 95 % of its active window, gone ≤ 0.6 s after the stop; **gridimpact:** ≥ 1 within 0.5 s of contact, within 0.5 m; **hullbreak:** one at the breached cell, bubbles ≥ 2 s; **splash:** one `Surface` event within 0.1 s of the crossing, within 0.5 m; **loss:** `eventsMissed` 0 with Subnautica at 30 fps during the burst; **rates:** exactly 128 accepted and 372 dropped and counted in the stress tick, per-type caps held, the buffer empty next tick, no row older than 8 ticks; **above:** above water lights only and no Subnautica event sound; **capture:** one per kind looked at (INFO) |
| S7.WM-5 | day/night, samples every 30 s for one SE day (1,200 s) (the drive is built in S7a, CONTRACT §5.4 "S7a conventions"; S7a.N5 checks 5 min of it, WM-5 stays the full-day run) | bind-time checks logged and passed (`\|axis·U0\| < 1e-3`, `\|base·axis\| < 1e-3`, interval 20); Subnautica `IsDay` true exactly when the SE sun is above the horizon at the camera (± one sample); `timePassedAsDouble` never decreases |
| S7.N5 | statics into SE: three census sites (Aurora hull, a precursor structure, Lost River trees and skeleton), the camera parked 20 m away; 200 rays per site from Subnautica (`de.statics {action: rays}`), cast by both games; with the census cache: a small grid placed against a wreck 1 km from the camera | P95 \|d_SE − d_SN\| ≤ 0.1 m and every SE hit a `DE.Statics` host; the far grid rests on the cached host after 5 s (SE ray from it hits the host) |
| S7.Bones.Length` = 81 for each model (`DE.S7 bones`); \|SE bone world (mapped) − Subnautica bone position\| ≤ 1 cm for all 81 bones on all 60 frames (frames at alpha ≥ 0.999); no tearing: every vertex's weights sum to 1 ± 1e-4 with indices < `boneCount`, capture looked at |
| S7.N7 | census completeness (offline, `sn_prefab_colliders.py --census`) | every class id the BatchObjectsCache references counted; each class with a non-trigger Default mesh collider or a static collider on another layer is allow-listed as handled (OBB) or on the logged skip list with a reason; FAIL on an unlisted class |
| S7.N8 | interior sky (part n1) | 100 % of `s7.sky` rows: `Inside` chunks apply `MarmoSkies.main.GetSky(Skies.BaseInterior)`, `Exterior` chunks the biome sky at their position, `Boundary` chunks the interior sky while the camera is in that room and the biome sky otherwise |
| S7.N9 | statics filter: a reaper (`de.spawn ReaperLeviathan 1 60`) kept 60 m from the camera (kinematic by `FreezeRigidbodyWhenFar` beyond 48 m) and the S6 lab ship with its proxies in the scan radius | no C12 primitive with the reaper's class id hash and none inside a proxy section box; the wreck batch near the camera reports ≥ 1 primitive |
| S7.Statics` name and no host entity id |
| S7.N11 | audio: a descent +5 → −30 m beside the test ship (thrusters at 50 %), then into the dry room | SE: every modifiable 3D emitter muffled while `seMixActive` and not in a room, none above water or in the room (100 % of `s7.audio` rows); Subnautica: event sounds started only for `Underwater` rows; biome and depth parameters recorded (INFO)

**Seeded failures** (`de.mutant S7.Mx on|off` on the side that owns them; world copies where noted)

| Mutant | Side | Expected |
|---|---|---|
| S7.M1 `IsInsideWalkable` postfix off | SN | S7.N1.fog red (no A/B difference), S7.N2.readers red |
| S7.M2 time set directly instead of advanced (= S7a.M6) | SN | S7.WM-5 monotonic red (a backward step) |
| S7.M3 the statics reporter drops capsules | SN | S7.N5 red (rays pass through Lost River trees) |
| S7.M4 bones mirrored component-wise instead of conjugated | SN | S7.N6.pos red |
| S7.M5 `SetSky(Skies.BaseInterior)` without the custom sky | SN | S7.N8 red (biome sky on `Inside` chunks) |
| S7.M6 only the sector weather base direction set, environment stock (world copy `DE Lab SunWeather`) | world | bind check fails, `DE.Time REFUSE axis`, sky-only: S7.WM-5 bind red, as it must be |
| S7.M7 statics chosen by "static or kinematic" | SN | S7.N9 red (the frozen reaper reported) |
| S7.M8 statics host `Save = true` (throwaway copy, rule 10) | SE | S7.N10 red |
| S7.M9 bones truncated to 64 | SE | S7.N6.count red |
| S7.M10 interior flag from `GetRooms` every tick | SE | S7.N1.flicker red |
| S7.M11 thruster bubble emission constant | SN | S7.N4.thruster.rate red |
| S7.M12 `MyGunBase.Shoot` postfix off | SE | S7.N4.weapon red |
| S7.M13 explosion scale fixed (radius ignored) | SN | S7.N4.explosion red |
| S7.M14 tool sustains never refreshed (start row only) | SE | S7.N4.tools red (the effect fades while the tool works) |
| S7.M15 attached events sent with `entityId` 0 (SE-local positions read as world) | SE | S7.N4.weapon and gridimpact position red |
| S7.M16 splash never raised | SE | S7.N4.splash red |
| S7.M17 event window of 1 tick (no hold) | SE | S7.N4.loss red at 30 fps |
| S7.M18 no shadow cap in the light pool | SN | S7.N3.shadows red |
| S7.M19 muffle postfix off | SE | S7.N11.muffle red |
| S7.M20 event caps off (every raised event accepted) | SE | S7.N4.rates red |

**S7 pass contract (exact names in CONTRACT §5.4 "S7 conventions"; four builders, each owning only its files; a new
file name starts with `S7`; earlier files take minimal, reported hooks only; the view state machine, `state_traces.py`
and the S7a files are not touched except where a hook is listed).**
- *Interiors and audio (B1).* Wire `S7Conventions.cs` (the whole canonical text, constants and shared rules; the
 other builders code against its names), `S7Interior.cs`; SE `S7Interior.cs` (probe, hold, settings check, RoomSet
 producer, rows, `de.interior`), `S7Audio.cs` (A1 postfix, `de.s7audio`, rows), `S7Commands.cs` (the `de.interior`,
 `de.lights`, `de.fx`, `de.statics`, `de.bones`, `de.s7audio` dispatcher, chained first in `Plugin.ExtraCommand`),
 `S7Host.cs` (life cycle and per-tick calls of every S7 feature); SN `S7Interior.cs` (RoomSet store, chunk classes,
 sky, clip proxies, rows, `de.interior`), `S7InteriorPatches.cs` (the postfix and the trace probe), `S7Host.cs` (life
 cycle, the S7 C11 routing); `tools/lab/set_world_settings.py`. Hooks: SE `FrameWriter.cs` (flag bit 3 and the
 interior bytes with every C1), `S2Host.cs` (C5 bit 4), `GridMirrorProducer.S5.cs` (RoomSet records on lane Edit after
 the first GridAdd and on a stream restart), `SwimController.cs` (the swim gate), `Plugin.cs` (one dispatcher line);
 SN `MirrorWire.cs` (RoomSet decoded instead of ignored), ` (the chunk class in the key,
 the sky after each build), `FrameReader.cs` (the interior bytes), `Bridge.cs` (the S7 host).
- *Lights and effects (B2).* Wire `S7Lights.cs`, `S7Events.cs`; SE `S7Lights.cs`, `S7Effects.cs` (sources, buffer,
 window, splash test, rows, `de.lights`, `de.fx`), `S7EffectPatches.cs` (the five postfixes); SN `S7Lights.cs`,
 `S7Effects.cs` (reader, sustains, spawning, rows), `S7EffectLibrary.cs` (run-time sources, safety filter,
 fallbacks). Hooks: SE `Link.cs` (a `WriteSeEntities` overload that also writes the light rows, the `pad_1c` header
 and the event rows inside the same seqlock write), `GridMirrorProducer.S5.cs` (the call at the C2 write); SN
 `MirrorWire.cs` (the C2 reader copies `lightCount` and `eventCount` rows in the same read), `DeepEngineers.SN.csproj`
 (a `Private=false` reference to `UnityEngine.ParticleSystemModule.dll`).
- *Statics (B3).* Wire `S7Statics.cs`; SE `S7Statics.cs` (C12 consumer, host life cycle, census cache, rays, rows,
 `de.statics`), `S7StaticsHost.cs` (Havok shapes, host entity, `Save = false`); SN `S7Statics.cs` (scan, filter,
 dedup, epochs, census mode and cache writer, rays, rows); `tools/sn-octrees/sn_prefab_colliders.py` (`--census`),
 `tools/worldmap/s7_ref.py`, `tests/s7/vectors/s7_statics.tsv`, the fake producer's census camera path
 (`tests/standins/fake_se_producer.py --s7-census`, a hook). Hooks: SE `Link.cs` (the C12 reader), SN `Link.cs` (the
 C12 producer), C7 `capabilities` bit 2.
- *Astronaut, mutants and the oracle tools (B4).* Wire `S7Bones.cs`; SE `S7Bones.cs` (rows, `de.bones`),
 `S7Mutants. SN `S7Bones.cs`
 `S7Mutants.cs` (M1–M5, M7, M11, M13, M18); hooks: SE `CharacterExport.cs` (`ExtraBones`: CharacterAdd, SkinnedBind
 and the C1 rows), SN `CharacterMirror.cs` (the extra rows posed flat), the `de.mutant` dispatchers (one line each);
 `tests/s7/analyze_s7.py`, `src/DeepEngineers.Launcher/launcher_s7.py` (+ its registration line in `launcher.py`).
 *As built:* the SE half is `SkinnedExport.cs` (`de.bones`:
 `SkinnedExport.Command`), the Subnautica half `SkinnedAstronaut.cs` + `SkinnedAstronautRules.cs` (`de.bones`:
 `SkinnedAstronaut.Handle`), with one more hook in SN `S5Math.cs` (the C1 reader takes 79–160 rows and keeps rows
 78.. as extras); the audio polish A1–A6 is this build pass too: SE `AudioPolish.cs` + `AudioPolishRules.cs` (the
 `SelectEffect` postfix, `de.s7audio`: `AudioPolish.Command`, hooks in `S2Host.Tick` and `CharacterExport.FillBones`),
 SN `AudioPolish.cs` + `AudioPolishRules.cs` (`s7.audio` rows, the event-sound rule `AudioPolish.OnEventSound`, hook
 in `AudioMix.Tick`).
- *Cross-build pass entry points.* Wire: `S7Conventions` (`Header` "ext s7 look 1", `ExtensionVersion`
 1, `PinnedHash` 0x1f9b9b4a876a97f9, `CanonicalLines` 143, `CanonicalBytes` 6583, `CanonicalText`, `Hash`, one nested
 static class per `const` group of the text with the same names and values); `S7InteriorWire.Write(ref SeFrame,
 in C1Interior)` / `TryRead(ref SeFrame, out C1Interior)`; `RoomSetCodec.Encode(…)` / `TryDecode(…)`;
 `InteriorRules.Classify(int x, int y, int z, IReadOnlyList<CellBox>)` → `InteriorClass`; `S7EventRow` (64 B explicit
 struct, not a layout record), `S7EventWindow` (`TryAdd(in S7EventRow)` with caps and priority, `EndTick(ulong tick)`,
 `CopyRows(…)`, per-type dropped counters, `Stream`, `FirstSeq`); `C2Ext` (`Write(byte* header, …)`, `TryRead(byte*
 header, ulong tick, out …)` over `pad_1c`); `S7LightRow.SetFalloff/GetFalloff(byte* row, …)` over `pad_3c`;
 `S7StaticsCodec` (`Encode`, `TryDecode`), `S7StaticsMath` (axes, sizes, capsule, `HostName(bx, by, bz, epoch, part)`);
 `S7Bones.ExtraRows(…)`. SE: `S7Interior.FillWire(ref SeFrame)` (FrameWriter hook), `S7Interior.CharacterInRoom(long
 characterId)` (swim gate), `S7Lights.Fill(SeLight[] rows, out int count, out uint found)`, `S7Effects.Window`,
 `Link.WriteSeEntities(…, SeLight[] lights, int lightCount, S7EventRow[] events, int eventCount, in C2Ext ext)` (B2's
 overload, also called with the lights by the S5 producer), `S7Mutants.<Mx>` flags, `S7Commands.Execute(cmd, args,
 via)`. Subnautica: `S7Interior.InsideNow` (read by the postfix), `S7Interior.OnRoomSet(…)` (MirrorWire hook),
 `S7Effects.OnC2(…)` / `S7Lights.OnC2(…)` (MirrorWire C2 hook), `S7Mutants.<Mx>`, `S7Host` life cycle.
 *Statics as built:* SE `S7Statics.Tick(ulong nowMs, Link link, uint
 worldEpoch, bool inWorld)` (per tick after `GridMirrorProducer.Tick`), `S7Statics.OnUnload` (world unload),
 `S7Statics.Command(IDictionary<string, object> args)` (`de.statics`, routed by `S7Commands`), `S7Statics.StatusLine`,
 `S7Statics.HostIds`; SE `Link.TryReadStatics(out uint type, byte[] buffer, out int payloadBytes)` (the C12 reader).
 Subnautica: `new S7Statics(byte* sectionBase, uint generation)` (after the creator step; its producer is
 `SectionLink.C12Producer(byte*)`), `Update(ulong nowMs)` (per frame), `FillStatus(ref SnStatus)` (C7 `capabilities`
 bit 2, from the S7 host's C7 hook in `Bridge.WriteStatus`), `Handle(ControlRequest, JsonObjectWriter, ulong now)`
 (`de.statics`), `Dispose`. Mutant flags read: SN `S7Mutants.M3`, `M7`; SE `S7Mutants.M8`.
- *As built by the interiors-and-lights build pass.* Wire `S7Conventions.cs` (the whole canonical
 text; `EventCaps.Of`, `EventType.Name`, `InteriorClass.Name` added as helpers outside the text), `S7Interior.cs`
 (`C1Interior`, `S7InteriorWire`, `CellBox`, `RoomSetCodec`, `InteriorRules` incl. `Classify`, `InteriorSky`,
 `StraddlesSurface`, `Plan`; `RoomProbe` + `RoomFlagHold` = the hold rule), `S7Lights.cs` (`S7LightRow`, `LightSelect`,
 `LightPoolRules`, `LightCalibration`, and `S7LightReader`, the Subnautica light reader on the effects build pass
 `IC2ExtensionReader` list: it replaces the pinned `S7Lights.OnC2(…)` of Subnautica). SE `InteriorProbe.cs`
 (`S7Interior.ProbeCamera` / `FillWire` / `SwimGate` / `CharacterInRoom(long)` / `CameraInRoom`), `InteriorRooms.cs`
 (the RoomSet producer), `LightTable.cs` (`S7Lights.Fill`), `InteriorCommands.cs` (`de.interior`, `de.lights`; chained
 first in `Plugin.ExtraCommand` in place of the unowned `S7Commands`); hooks `FrameWriter` (probe + `FillWire`),
 `S2Host` (C5 bit 4), `GridMirrorProducer.S5`, `SwimController` (the gate, exit reason `room` = `SwimExit.Room` 15 in `SwimRules`),
 `Plugin` (one dispatcher). SN `InteriorRules.cs` (Unity-free: `InteriorStore`, `InteriorChunks`, `InsideRule`,
 `InsideTrace`), `InteriorView.cs` (`S7Interior.InsideNow`, skies, clip proxies, rows, `de.interior`),
 `InteriorPatches.cs` (the postfix, the trace probe, the `UnstuckPlayer.TrySavePosition` prefix), `LightMirror.cs`
 (the pool, `de.lights`), `InteriorHost.cs` (their life cycle in place of the unowned `S7Host`); hooks `MirrorWire`
 (RoomSet store, `CellKey.C`, `GridRecord.Rooms`), `MirrorConsumer.ApplyCell` (`InteriorHooks.CellApplied`),
 `FrameReader` (the interior bytes), `CharacterMirror` (`EntityTransform`, `CharacterId` accessors), `Bridge` (the
 host). Image checks `tests/s7/s7_interior_check.py` (+ the `s7_images.py` name the analyzer imports);
 `tools/lab/set_world_settings.py`. Mutant flags are the astronaut/tools build pass `S7Mutants` (SE M10; SN M1, M5,
 M18 read here).
- *As built by the integrator.* SE `S7Commands.cs` (`S7Commands.Execute`, chained
 first in `Plugin.ExtraCommand` in place of the builders' two links: `de.interior`/`de.lights` → `InteriorCommands`,
 `de.fx` → `S7Effects`, `de.statics` → `S7Statics.Command`, `de.bones` → `SkinnedExport`, `de.s7audio` →
 `AudioPolish`) and `S7Host.cs` (`Tick` → `S7Statics.Tick` after `DamageConsumer.Tick` in `Plugin`;
 `OnWorldForgotten` → the interior holds, room handlers and the effects' grid index, from `GridMirrorProducer.S5Forget`;
 `OnSessionUnload` → `S7Statics.OnUnload`, from `SessionComponent.UnloadData`). Subnautica `S7Host.cs` (the statics
 reporter: `new S7Statics(section base, generation)` after the other hosts, `Update` per frame, C7 `capabilities` bit 2
 in `Bridge.WriteStatus`, disposed before every unmap; `de.statics`; `de.bones` → `SkinnedAstronaut.Handle`); the
 interiors/lights host (`InteriorHost`) and the effect player (`S7Effects`) stay wired in `Bridge` by their builders.
 `launcher_s7.
 and restores the walk source `sebones` (the S7a default) in teardown.
- `launcher.Mx] [--world NAME]
 [--room BLUEPRINT] [--ship BLUEPRINT] [--quick] [--dry-run]`: both games, DE Lab, the S5 session rules (character
 parked at SN (24, −8, 0)), SE environment `DEEPENGINEERS_S5=on`, `DEEPENGINEERS_PICTURE=site`, S6 off unless set;
 the session start row carries `band` and the world settings. Parts: `n1` (room, flag, fog A/B, breach, flicker, sky

 overrides off, patch on, mutants off). `launcher.py s7 --census [--batches all|<file>] [--dry-run]`: Subnautica with
 the fake producer's camera at each batch centre for 3 s, census mode on (writes the cache). Mutant groups: M1 → n1,
 n2; M2 → wm5; M3 → n5; M4, M9 → n6; M5, M10 → n1; M6 → wm5 (`--world "DE Lab SunWeather"`); M7 → n9; M8 → n10;
 M11–M17, M20 → n4; M18 → n3; M19 → n11. `--list-mutant-groups`, `--analyze <folder>`, `--selftest`. Output
 `tests/out/s7/<stamp>[-<mutant>]/` (`s7-session.jsonl`, `se-*.jsonl`/`sn-*.jsonl`, logs, captures, `s7-table.txt`,
 `s7-results.json`).
- Analyzer rows (`tests/s7/analyze_s7.py`; PASS, FAIL, MISSING, INFO): `S7.N1.flag`, `S7.N1.fog`, `S7.N1.breach`,
 `S7.N1.flicker`, `S7.N1.capture` (INFO), `S7.N1b.flag`, `S7.N1b.log`, `S7.N1c.walk`, `S7.N1c.look` (INFO),
 `S7.N2.readers`, `S7.N2.ghost`, `S7.N2.music` (INFO), `S7.N2.brightness` (INFO), `S7.N3.ratio`, `S7.N3.shadows`,
 `S7.N3.cap`, `S7.N4.thruster.rate`, `S7.N4.thruster.rpm`, `S7.N4.thruster.log`, `S7.N4.weapon`, `S7.N4.impact`,
 `S7.N4.explosion`, `S7.N4.tools`, `S7.N4.gridimpact`, `S7.N4.hullbreak`, `S7.N4.splash`, `S7.N4.loss`,
 `S7.N4.rates`, `S7.N4.above`, `S7.N4.capture` (INFO), `S7.WM-5.bind`, `S7.WM-5.isday`, `S7.WM-5.monotonic`,
 `S7.N5.rays`, `S7.N5.census`, `S7.N6.count`, `S7.N6.pos`, `S7.N6.tear`, `S7.N7.census`, `S7.N8.sky`, `S7.N9.filter`,
 `S7.N10.save`, `S7.N11.muffle`, `S7.N11.bus`, `S7.N11.ambience` (INFO).
- *S7 review fixes.*
 The room of S7.N1/N1b/N1c/N2/N3/N8/N11 is the `de.s3` room fixture (`plate: {orientation: room}`: a 5 × 5 × 5
 light-armor shell around a 3 × 3 × 3 interior, a round window in the north wall, a battery, an **Air Vent** (SE builds
 no airtight room without a gas block), a door, an Interior Light about 2.5 m from the east inner wall and three
 spotlights), block (0, 0, 0) at `--room-at` (default SN (60, −12, 60)); n1 toggles the door 5 times with the camera
 inside (`de.interior {action: door}`), so S7.M10 and S7.M18 run idle. Refined rows: S7.N1.flicker is INFO without a
 recompute in the n1 windows; S7.N2.readers judges the always-on readers (WaterscapeVolumeOnCamera, FMODGameParams,
 LargeWorldStreamer, uGUI_AdaptiveBrightness or ColorCorrection) in both windows, inside frames and calls, and
 UnstuckPlayer absent inside, the other readers INFO (rows picked by the launcher's part windows); S7.N2.ghost skips
 the outside attack check in a NoAggression save; S7.N3.shadows needs ≥ 3 shadowed spot rows; S7.N4.thruster.rate/.rpm
 fit Subnautica against SE's sustain rows of the same key (SE `de.fx` trace during the thrust steps); S7.N4.weapon and.impact count SE's per-event rows; S7.N4.loss runs `de.fx {action: stress, n: 500, ticks: 8}` with Subnautica capped
 to 30 fps (`de.fx {action: fps, cap: 30}`), S7.N4.rates the one-tick stress; S7.N4.above and S7.N11.bus fail on any
 sound request allowed without Underwater but the surface-splash exception; S7.N5.rays uses the rays whose Subnautica
 hit is a static and SE's first statics-host hit (no host = +inf; ≤ 5 % misses); S7.N6.pos lerps SE's rows by alpha;
 S7.N9.filter reads the reporter's `creaturesReported`/`insideProxy` (MISSING without them); S7.N11.muffle is INFO in an
 Arcade-sound world (SE's effect path is off there; S7.M19 is then not judgeable).

*
SE's world settings, or approve `python -I tools/lab/set_world_settings.py --world "DE Lab" --oxygen on
--pressurization on` run by the orchestrator with SE closed (its `--dry-run` first; the backup preflight). (2) Approve
the throwaway world copies `DE Lab NoPress` (S7.N1b), `DE Lab SunWeather` (S7.M6) and `DE Lab S7Save` (S7.N10, S7.M8).
(3) Approve the census session (Subnautica with the fake producer, unattended, one 3 s stop per batch with objects:
2,975 batches; writes `%LOCALAPPDATA%\DeepEngineers\census\`). (4) Choose the light feel after S7.N3 (`LightIntensityScale`,
`LightRangeScale`, live through `de.lights`).N1c), fire and use the
hand tools under water and optionally detonate a warhead (S7.N4), switch the character model (S7.N6), save the copy
(S7.N10); his own drained house needs an **Air Vent** (SE builds no airtight room on a grid without one; it need not be
powered or on). (6) Judge interiors (the drained house), lights, thrusters and every effect kind, day/night, the audio
(muffle, Subnautica's event sounds, the interior ambience of S7.N2), the skinned astronaut, a ship against the Aurora.
(7) Approve launches.

**Exit.

**Proves** (*real run*): the remaining look features and static collision work as tested. **Does not prove:**
anything listed out of v1 (section 8).

---

### contract contract-s8a; after S7, before S8)

**Goal.** One SE-voxel → Subnautica-terrain converter, used twice. **S8a-1 drilling (inside the site):** a hole drilled,
blasted or voxel-handed in SE's provider tiles appears in Subnautica, in the same place, with the same size, colliding
the same way, and survives saving and reloading in both games; SE keeps the mining yields and the physics. **S8a-2 the
seabed beyond the crater:** the planet `DE_4546B` gets real, sculpted terrain (SE's own, so the planet is never empty),
and around the camera beyond the crater Subnautica draws that terrain, converted from SE's planet voxels into
Subnautica batches; the crater stays authentic (the install's batches); far from the crater a treadmill re-anchors the
map in whole-batch steps so the camera always has Subnautica batch space; designed Subnautica biomes (material mapping,
biome fog, a sparse spawn table of Subnautica's own flora and fauna) come last in the same slice.
will be able to drill under the water, how will it work? SN doesn't have voxels that break like SE"; "I really want
real seabed - it's what people expect from an ocean planet, VOID is anticlimactic"; content source "both". Budget: one

**What the decompiled Subnautica changed.** (1) In Subnautica 1.22.83031 the terrain on screen and in physics is `WorldStreaming`'s: `BatchOctreesStreamer`
holds 5 × 5 × 5 batches around the camera as native 4-byte node arrays per 32 m `Octree`, read only from
`<install>\Subnautica_Data\StreamingAssets\SNUnmanagedData\Build18\CompiledOctreesCache` through
`BatchOctreesStreamer.GetPath(Int3)`, and `ClipmapStreamer` meshes them. There is **no live voxel edit path**:
`Voxeland`/`VoxelandData` are legacy here (`land.data` is cleared to nothing), `LargeWorldStreamer.compactTrees` is
never filled (`LoadCompiledOctreesThreaded` has no caller), so `PerformSphereEdit`/`PerformBoxEdit` and the `dig`
console command edit nothing, and `SaveCompactOctrees` would write empty octrees into the install folder (never call
it). (2) There is **no per-save octree cache**: nothing reads one, and `UserStoragePC.UpgradeSaveDataAsync` (run at
every Steam start) deletes `compiled-batch-*.optoctrees` found in save folders. (3) The Steam save slots live in
`<Subnautica>\SNAppData\SavedGames`, inside the install folder, not under LocalLow. Hence: Subnautica's edit path is
**rewrite the batch file in our own store, then `BatchOctrees.Reload` + `ClipmapCell.Reload`**; the "ghost save cache"
is our store `%LOCALAPPDATA%\DeepEngineers\terrain\` (derived data, never inside an install or `SNAppData`), served by
a `GetPath` postfix; the host save stays never-saved. (4) SE's save deletes every top-level file of the save folder it
did not stage (`MySessionSnapshot.Save`), so the drill overlay rides SE's own voxel snapshot list (`DE_S8a_overlay.vx2`).
(5) The converter's output format is proven against the install offline (below).

**Converter proof (contract time; scripts `octree_rules.py`, `octree_roundtrip.py`
`surface_types.py` and the canonical-text generator `canon.py` in the contract session's scratchpad
** 152 random
batch files, 18,800 octrees, 1,264,598 interior nodes: node layout = `CompactOctree.SetInternal` (pre-order, blocks of
8 from id 1), every interior node = `OctNode.CacheDownsampledType` of its children (with the
`ComputeMostCommonChildType` tie rule), no node that `OctNode.CanCollapse` would collapse; 150 subdivided octrees
rasterised to 32³ voxels and rebuilt with those rules came back **byte-identical**. B1 turns these checks into
`tools/sn-octrees/sn_octree_write.py` and the C# round trip (S8a.C1).

**Builds on:** S4 (reader, provider tiles, guards, site map), S5 (every mirrored thing is placed through
`SiteBinding.Map` each tick), S7 (statics), S7a (picture policy as a live setting). **CONTRACT first: done
(contract-s8a).** CONTRACT §3.3 (members with exact signatures, overlay, store, window, chart and sculpt rules, budgets)
and §5.4 "S8a conventions" (`ext s8a terrain 1`, FNV-1a 64 **`0x7f480768c4cb872f` (ext s8a terrain 2, Stage C; was `0x3d52f1105b92a172`)**, 150 lines, 11,841 bytes;
**layout 1.0 unchanged, no pin moved**: the feed lives in C2 region bytes 0x50000–0x5803F inside the C2 seqlock, the
acknowledgement in C7 pad bytes 0x8C–0xBB), ARCHITECTURE §5.17.

**Work items**

| Part | Items |
|---|---|
| Wire (shared core) | `S8aConventions` (canonical text, constants, hash); `S8aOctree` (32³ raster ⇄ octree nodes by the engine's rules; batch file parse/serialize, crater ids with the octree-bounds subset, window ids with all 125 octrees); `S8aConvert` (SN sample point, SE content → SN density/type, material table); `S8aFeed` (C2 S8a header and notice rows, C7 block, the SE queue with caps, resync and dropped counter, Subnautica's `IC2ExtensionReader`); `S8aChart` (chart construction, step rule, chart key); `S8aWindowPlan` (ring, look-ahead, near box); `S8aStore` (paths, manifest, overlay file, store key) |
| SE plugin | Drilling: the S4 guards let edits through on the tiles while `DrillOn` (the provider stays read-only: the edit lands in `MyOctreeStorage`'s own leaves); `MyStorageBase.RangeChanged` on every tile → dirty 16³ overlay cells; edits mirrored into overlapping tiles; a worker converts each edited region into Subnautica voxels (copy-on-write over the base batch), writes the new batch version into the store and queues a notice; the overlay rides `MySession.Save` (`CompressedVoxelSnapshots["DE_S8a_overlay"]`) and is restored in `BeforeStart` after the tiles; at load the drill layer of the store is checked against the overlay hash and rebuilt when it differs. Window: a worker generates the ring of batches around the camera (plus look-ahead) from the planet's storage (`MyStorageBase.ReadRange`, classification at LOD 3, LOD 0 only for surface octrees), writes them into the store, queues notices, sets `NearPending`/`NearEmpty`, computes `cameraBiome`; planet `RangeChanged` regenerates the batches it touches. Chart: steps and re-centres (CONTRACT "chart.*"), `SiteBinding.SetChart`, cached maps refreshed, Subnautica-positioned input ignored during a transition, picture policy `Waterline` requested while a treadmill chart (k ≥ 1) is active (the crater's islands and the Aurora would show at wrong places above water). The C2 S8a header and rows written inside the C2 seqlock (`Link` hook); C7 acks read each tick |
| SN plugin | `BatchOctreesStreamer.GetPath(Int3)` postfix (store version for every batch the manifests or notices name); `BatchOctrees.LoadOctrees` prefix for window ids (all 125 octrees; crater ids run the original); `BatchOctreesStreamer` constructor prefix widening `centerMin`/`centerMax` of the full-detail streamer to the window universe; the notice reader (C2 extension reader) → on the streaming thread `BatchOctrees.Reload(id)`, then `ClipmapCell.Reload(id)` for the level 0–3 cells meeting the notice box, once the batch is loaded; C7 S8a block (ack, applied, chart applied, counters); `areaBuilt` gated by `NearPending` and unapplied window notices near the camera; `GetBiome` beyond the crater box from `cameraBiome`; no interpolation across a `chartSeq` change; the spawn table (Subnautica prefabs at run time, transient, never saved); `de.s8a` (status, probe, rays, verify, reload, spawns) |
| tools/tests | `tools/sn-octrees/sn_octree_write.py` (Python twin writer, `--roundtrip N` on the install read-only, `--selftest`); `tools/planet/s8a_ref.py` (conventions twin: text, hash, feed codec, chart math, window plan, store key; vectors `tests/s8a/vectors/*.tsv` read by the C# tests); `tools/planet/sculpt_planet.py` + `tools/planet/sculpt_rules.json` (seed + rules → six 2048² 16-bit heightmaps and six RGBA material maps into the world mod, the `.sbc` `CustomMaterialTable`, `--preview`, `--verify`, `--check-world <save>` buried-grid check, `--dry-run`); `tools/worldmod/check_planet.py` (accepts a sculpted planet: the crater rule instead of "all samples 0"); `tests/s8a/analyze_s8a.py` (rows below, `--selftest` with synthetic sessions and every mutant red on its rows only); `tests/s8a/s8a_install_check.py` (read-only install listing before/after: S8a.N6); `src/DeepEngineers.Launcher/launcher_s8a.py` |

**Oracle** (the S5 session rules; SN coordinates through the active chart; rows of CONTRACT §5.4 "S8a conventions";
offline rows need no game)

| Check | What | Pass |
|---|---|---|
| S8a.C1 | offline: converter round trip on the install (≥ 100 batch files incl. 12-18-12, 7-17-10, 14-8-9; read-only), C# `S8aOctree` and the Python twin; rule census on every parsed octree | every file: parse → raster → write → bytes identical (C# and Python); the S4 reader (`SnOctreeSource`) reads the written files with the same samples; `.rules`: 0 interior nodes off `CacheDownsampledType`, 0 collapsible nodes, layout pre-order |
| S8a.C2 | offline: sculpt (`sculpt_planet.py --verify` on the chosen seed) | `.sculpt`: every face 2048², 16-bit, decoded heights re-derived from the rules within 1 unit; material maps RGBA with R in the `.sbc` table; `.flat`: every pixel with site-v1 ρ∞ ≤ 2,450 m is 0 and the bicubic surface at 2,000 points with ρ∞ ≤ 2,300 m is SN y −3,000 ± 0.01 m; no surface above SN y −10 |
| S8a.C3 | offline: the pin | Wire text = CONTRACT block = Python twin (hash `0x7f480768c4cb872f` (ext s8a terrain 2, Stage C; was `0x3d52f1105b92a172`), 150 lines, 11,841 bytes); layout 1.0 `layout_py.py compare` PASS; every other extension pin unchanged |
| S8a.N1 | drilling, scripted: `de.s8a drill` at 6 sites in the crater (radius 2.5 m, explosion path `PerformCutOutSphereFast` × 4, hand-drill path `CutOutShapeWithPropertiesAsync` × 2; one site on a tile seam), 10 s apart, camera 12 m away | `.edit`: SE's storage changed (`IsRangeModified`, SE ray into the hole ≥ 1.5 m deeper); `.latency` (from the SE edit tick to the SE tick at which C7 `appliedSeq` first covers its notice, plus one presented Subnautica frame): **P95 ≤ 1.0 s, max ≤ 2.5 s** (HYPOTHESIS budget: coalesce 250 ms + convert ≤ 10 ms + write ≤ 50 ms + reload and rebuild); `.visible`: a Subnautica ray down each hole's axis ≥ 1.5 m deeper than before (MISSING when `.edit` is not PASS) |
| S8a.N2 | geometry: 9 × 9 Subnautica layer-30 rays over each hole's footprint vs the analytic carve (centre F(c), vertical radius r, horizontal r·s, s = Rs/\|c − C\|) intersected with the S4 reference terrain | tolerance derived from the 1 m octree leaf: `.position` fitted centre within **0.25 m**; `.size` fitted radii within **±0.25 m**; surface points P95 ≤ 0.25 m, max ≤ 0.5 m (half a leaf); `.overlap` (the seam site): SE's ray into the hole hits the carved surface in every overlapping tile (no second tile's original surface) |
| S8a.N3 | collision across the hole: 64 columns inside 0.7 r of each hole's axis, SE `CastRay` vs Subnautica layer-30 `Physics.Raycast` (prefetched; S4's harness) | `.rays`: **P95 ≤ 0.1 m**, max ≤ 0.5 m (the carved surface is converted once; S4 measured 0.189 m P95 on the unedited seabed, which stays `.rim` INFO: 32 columns on a ring 1.5 r–3 r) |
| S8a.N4 | the SE save keeps it (world copy `DE Lab S8aSave`): 3 holes, the scripted save (`de.s8a save` → `MyAsyncSaving.Start`, refused outside a `DE_THROWAWAY.txt` copy;save`: SE rays at the 3 holes within 0.05 m of the pre-save values and Subnautica rays as N3; `.file`: the save holds `DE_S8a_overlay.vx2` (decodes, cell count = the session's dirty cells, hash = `overlayHash`); `.portable` (offline, read-only): no tile storage, no `.vx2` with provider id 45460, the S4.
| S8a.N5 | Subnautica's store holds the edited batch, and a reload shows the hole: `de.s8a reload {all: true}` on Subnautica (every loaded batch and clipmap cell reloaded), and the relaunch of N4 | `.store`: every notice's file exists in the store with the manifest's bytes and FNV; `.bytes`: the file Subnautica served (FNV of the bytes it read) = the file SE wrote (SE `s8a.write` row); `.reload`: N3's rays after the reload and after the relaunch, same pass |
| S8a.N6 | nothing written into either install | `.install`: no file under either install root (Subnautica excluding `SNAppData`, SE `Bin64`, `Content`) has a size, count or write time changed during the session (read-only listing before/after) and the S4 source manifest hash is unchanged; `.writes`: every `s8a.write` row lies under `%LOCALAPPDATA%\DeepEngineers\terrain\`; `.hostsave`: the host slot hash unchanged (S1.N4) |
| S8a.N7 | the window (after the sculpt is deployed; `TreadmillOn` off; world copy `DE Lab S8a`): spectator paths from SN (1,800, y, 0) to (4,600, y, 0) and along the ring, 60 m above the SE seabed, at 20 m/s and at 80 m/s; a 6 m `de.s8a drill` into the generated seabed at one spot | `.rays`: Subnautica layer-30 vs SE `CastRay` on the planet at 200 columns along the paths: **P95 ≤ 0.3 m**, max ≤ 1.0 m (S4's budget, conversion inverted); `.coverage`: share of path samples whose ring batches were applied before the camera came within 100 m of them ≥ 99 % at 20 m/s, ≥ 90 % at 80 m/s; `.time`: per surface octree p95 ≤ 10 ms and per surface batch p95 ≤ 450 ms, empty/solid batch p95 ≤ 5 ms on the one window worker; `.memory`: SE working-set growth ≤ 300 MB and Subnautica's ≤ 150 MB over the paths, store ≤ `Store.MaxMB`; `.edit`: the drilled spot regenerated (version + 1) and Subnautica's rays see the 6 m hole (N2 tolerances) |
| S8a.N8 | the re-anchor (`TreadmillOn`): a path from the crater to site-v1 ρ∞ 12 km and back, 60 m above the seabed, with a test grid (`de.s3` plate) moved along beside the camera at 30 m, plus `de.s8a step` forced once in open water and once over terrain | `.jump`: for every mirrored entity, \|Δ(p − cam)\| across each step tick ≤ **1.1e-3 · \|p − cam\| + 0.01 m** (bound of the outgoing chart's distortion: θ ≤ 0.08 rad at the 0 → 1 step), orientation ≤ 0.05°; `.snap`: Subnautica rows show no interpolated pose between the two charts; `.batchstep`: every `ChartStep` T is a whole number of batches and the C1 camera jump equals T to 1 mm; `.picture`: an open-water step keeps Subnautica's picture (no picture edge within ±2 s), a step over terrain shows SE's picture ≤ 1.5 s |
| S8a.N9 | the treadmill holds when generation lags: N7's 80 m/s path with `de.s8a window {throttleMs: 2000}` (the generator sleeps per batch) | `.gate`: no presented Subnautica frame while SE's surface lies within `NearSurfaceM` of the camera and a near-box window batch is not applied (C7 `areaBuilt` false within 1 SE tick of `NearPending`); `.resume`: Subnautica's picture back ≤ 1.5 s after the last near-box notice is applied |
| S8a.N10 | biomes and spawns (N7's paths, spawns on) | `.material`: at 200 window columns Subnautica's top solid voxel type = the table entry of SE's surface material there (≥ 99 %); `.spawn`: spawned counts per depth band per hectare within ±30 % of the table over ≥ 1 km² of path, none inside the crater box, none in the host save (S1.N4 hash), none beyond `DespawnM`; `.biome` INFO: `GetBiome` at the camera = `names[cameraBiome]` |
| S8a.T-P1r | T-P1 with S8a on: `launcher.py tp1 --state underwater` and `--state track-sail`, plus a new `window` state (camera over generated seabed at 30 m/s, the window worker busy), 5 min each | the S7a.T-P1r criteria (SE simulation ratio ≥ 0.98; Subnautica p99 ≤ 16.7 ms); the window worker ≤ 1 core on average; no SE frame > 25 ms caused by an S8a game-thread hook (`s8a.perf` rows) |

**Seeded failures** (`de.mutant S8a.Mx on|off` on the side that owns them; each turns exactly the listed rows red)

| Mutant | Side | Red rows |
|---|---|---|
| S8a.M1 drill notices suppressed (files still written) | SE | S8a.N1.latency, S8a.N1.visible, S8a.N3.rays |
| S8a.M2 converter samples at the voxel corner (no + 0.5) | SE | S8a.N2.position, S8a.N3.rays |
| S8a.M3 overlay not added to the save snapshot | SE | S8a.N4.save, S8a.N4.file |
| S8a.M4 `GetPath` serves the install from the n5 reload on | SN | S8a.N5.reload |
| S8a.M5 drill store written under the SE world folder | SE | S8a.N6.writes |
| S8a.M6 window generated from `MyPlanet.Provider` (the unedited generator) instead of the storage | SE | S8a.N7.edit |
| S8a.M7 interpolation across a `chartSeq` change | SN | S8a.N8.snap |
| S8a.M8 chart step T = the exact overshoot (not whole batches) | SE | S8a.N8.batchstep |
| S8a.M9 `NearPending` never set | SE | S8a.N9.gate |
| S8a.M10 material table bypassed (every type 1) | SE | S8a.N10.material |
| S8a.M11 spawn density × 10 | SN | S8a.N10.spawn |
| S8a.M12 S4 guards kept on the tiles while `DrillOn` | SE | S8a.N1.edit |
| S8a.M13 interior nodes = child 0's value (no `CacheDownsampledType`) | Wire (offline) | S8a.C1.rules, S8a.C1.roundtrip |
| S8a.M14 edits not mirrored into overlapping tiles | SE | S8a.N2.overlap |

**S8a pass contract (exact names in CONTRACT §3.3 and §5.4 "S8a conventions"; four builders, each owning only its
files; new file names start with `S8a`; S7 and S7a files take minimal reported hooks only; S6 files untouched; the view
state machine is not touched: the picture policy and the band are live settings).**
- *B1 Converter core and tools.* Wire `S8aConventions.cs`, `S8aOctree.cs`, `S8aConvert.cs`, `S8aFeed.cs`, `S8aChart.cs`,
 `S8aWindowPlan.cs`, `S8aStore.cs`; tests `tests/DeepEngineers.Wire.Tests/S8a*Tests.cs`,
 `tests/DeepEngineers.Terrain.Tests/S8aRoundTripTests.cs` (install batches through `S8aOctree` and back through
 `SnOctreeSource`; skips cleanly without the install); `tools/sn-octrees/sn_octree_write.py`, `tools/planet/s8a_ref.py`,
 `tools/planet/sculpt_planet.py`, `tools/planet/sculpt_rules.json`, `tests/s8a/vectors/`, and the reported edit of
 `tools/worldmod/check_planet.py` (sculpted heights) and `src/DeepEngineers.WorldMod/Data/PlanetGeneratorDefinitions.sbc`
 (`CustomMaterialTable` rows)
- *B2 SE drilling and the SE side of the feed.* SE ` (edit policy, `RangeChanged`, mirroring, dirty
 cells, `MySession.Save` postfix, restore), ` (worker, copy-on-write splice, store writes,
 notices), ` (the SE queue, the C2 write, C7 ack read), ` (life cycle, per-tick calls,
 `s8a.*` rows), ` (`de.s8a` dispatcher: drill, overlay, save, status, step and window delegate to
 B3), ` (every SE flag of the table). Hooks: ` (`Block`: one line),
 ` (after creation, at unload), `Link.cs` (the S8a write inside the C2 seqlock), `Plugin.cs`
 (dispatcher line, `S8aHost.Tick`, patch install).
- *B3 SE window, chart and biomes.* SE ` (planner, worker, near box, regeneration on planet edits,
 `cameraBiome`), ` (planet storage reads and classification), ` (steps,
 re-centres, return, `PicturePolicy.Set` while k ≥ 1). Hooks: `SiteBinding.cs` (`SetChart`, `SiteMapV1`, `ChartSeq`),
 `EffectsProducer.cs`, `S7Statics.cs`, `SunSync.cs` (re-read the map when `ChartSeq` changes; S7Statics also drops C12
 batches during a transition).
- *B4 Subnautica side, launcher and analyzer.* SN `S8aTerrain.cs` (host: store view, notice reader, reload scheduler on
 the streaming thread, clipmap cell reloads, C7 block, rows, `de.s8a`), `S8aPatches.cs` (`GetPath` postfix, `LoadOctrees`
 prefix, constructor prefix, the `areaBuilt` gate helper, the `GetBiome` rule helper), `S8aSpawns.cs`, `S8aMutants.cs`
 (M4, M7, M11). Hooks: `MirrorWire.cs` (register the reader), `Bridge.cs` (host, C7), `S1Telemetry.cs` (`areaBuilt`),
 `S2Patches.cs` (`GetBiome` beyond the crater box), the S5 interpolation sites (snap on `chartSeq`); launcher
 `src/DeepEngineers.Launcher/launcher_s8a.py` (+ one registration line in `launcher.py`), `tests/s8a/analyze_s8a.py`,
 `tests/s8a/s8a_install_check.py`; the fake producer's S8a feed mode (`tests/standins/fake_se_producer.py --s8a`, a
 hook) for an SN-only reload check.
- *Cross-build pass entry points.* Wire: `S8aConventions` (`Header` "ext s8a terrain 1", `PinnedHash` 0x3d52f1105b92a172, `CanonicalLines`
 150, `CanonicalBytes` 11841, `CanonicalText`, `Hash`, one nested class per const group); `S8aOctree.EncodeOctree(byte[]
 type32, byte[] density32) → byte[] nodes`, `S8aOctree.DecodeOctree(byte[] nodes, int offset, int nodeCount, byte[]
 type32, byte[] density32)`, `S8aBatchFile.Parse(byte[] file, int bx, int by, int bz) → S8aBatchFile` (`Octree(i)`,
 `SetOctree(i, byte[])`, `Serialize → byte[]`, `IsCraterId`); `S8aConvert.EncodeSample(double s, byte type, out byte
 outType, out byte outDensity)`, `S8aConvert.ContentToD(byte content)`, `S8aConvert.SampleCentreSn(int vx, int vy, int
 vz) → Vec3`, `S8aMaterialMap.SnType(string seSubtype) → byte`; `S8aNotice` (32 B), `S8aHeader` (64 B), `S8aC7`
 (48 B), `S8aFeedWriter` (`TryEnqueue(in S8aNotice)`, `WriteInto(byte* c2Region, ulong tick, in S8aHeader state)`,
 `OnAck(uint stream, uint ackSeq)`, `Dropped`, `Pending`), `S8aFeedReader: IC2ExtensionReader`; `S8aChart.Step(SiteMap
 k, Vec3 cameraSe, int tx, int tz) → SiteMap`, `S8aChart.Decide(…) → (int tx, int tz, bool toSite)`,
 `S8aChart.ChartKey(SiteMap m, ulong sculptHash)`; `S8aWindowPlan.Plan(Vec3 camSn, Vec3 velSn, List<Int3Key> ids)`;
 `S8aStore.StoreKey(string worldPath, int siteVersion)`, `S8aStore.DrillFolder/WindowFolder`, `S8aManifest`,
 `S8aOverlayFile.Encode/Decode`. SE: `S8aOverlay.AllowsEdit(int path, MyVoxelBase v, MyStorageBase s)`,
 `S8aOverlay.OnTilesCreated`, `S8aOverlay.OnUnload`, `S8aFeed.Enqueue(in S8aNotice)`, `S8aFeed.WriteInto(byte*
 rec, ulong tick)`, `S8aHost.Tick(ulong nowMs, Link link, bool inWorld)`, `S8aWindow.Tick(…)`, `S8aWindow.NearPending`,
 `S8aChartManager.ChartSeq`, `SiteBinding.SetChart(SiteMap map, uint chartSeq, string why)`, `S8aMutants.<Mx>`,
 `S8aCommands.Execute(cmd, args, via)`. Subnautica: `new S8aTerrain(byte* sectionBase, uint generation)`,
 `Update(ulong nowMs)`, `FillStatus(ref SnStatus)`, `Handle(ControlRequest, JsonObjectWriter, ulong now)`, `Dispose`,
 `S8aTerrain.AreaBuiltGate(bool native, Vector3 cam) → bool`, `S8aTerrain.BiomeAt(Vector3 p) → string`,
 `S8aMutants.<Mx>`.
- `launcher.
 S8a.Mx] [--treadmill] [--sites FILE] [--dry-run] [--selftest] [--list-mutant-groups] [--analyze <folder>]`: c1–c3
 offline (no game); the others both games on a throwaway copy (`DE Lab S8a`: `tools/lab/make_terrain_copy.py`,
 `DE_THROWAWAY.txt`, autosave 0; n4 on `DE Lab S8aSave`), the S5 session rules (character parked at SN (24, −8, 0)), SE
 environment `DEEPENGINEERS_S5=on`, `DEEPENGINEERS_PICTURE=site`, `DEEPENGINEERS_S8A=on`, `DEEPENGINEERS_S8A_TREADMILL`
 on for n8 only; the session start row carries `band`, the world, the S8a settings and the store key. Parts: `n1`
 (drills), `n2` (footprint rays), `n3` (collision rays), `n4` (save, quit, relaunch both, check; two launches), `n5`
 (`de.s8a reload`), `n6` (install listing before/after; runs in every session), `n7` (window paths, window drill), `n8`
 (treadmill path, forced steps), `n9` (throttled path), `n10` (material probes, spawns), `teardown` (mutants off,
 throttle off, treadmill off, spawns cleared). Mutant groups: M1 → n1, n3; M2 → n2, n3; M3 → n4; M4 → n5; M5 → n1, n6;
 M6 → n7; M7, M8 → n8; M9 → n9; M10, M11 → n10; M12 → n1; M13 → c1; M14 → n2. Output `tests/out/s8a/<stamp>[-<mutant>]/`
 (`s8a-session.jsonl`, `se-*.jsonl`/`sn-*.jsonl`, logs, captures, `s8a-install-before.txt`/`-after.txt`,
 `s8a-table.txt`, `s8a-results.json`).
- JSONL rows (`"ev"`): SE `s8a.edit` {tick, path, tile, min, max, cells} (each guarded edit let through, rate-limited per
 path to 10/s with a count); `s8a.write` {tick, layer, bx, by, bz, version, bytes, fnv, ms, path} (every store file);
 `s8a.notice` {tick, seq, kind, layer, bx, by, bz, version, min, max}; `s8a.feed` {tick, firstSeq, rows, pending,
 dropped, ackSeq, appliedSeq, resyncEpoch} (1 Hz and on change); `s8a.window` {tick, chartSeq, ring, generated,
 pending, nearPending, nearEmpty, msP95, workerBusy, storeMB} (1 Hz); `s8a.chart` {tick, chartSeq, chartKey, T, anchor,
 reason, camSnBefore, camSnAfter} (every step); `s8a.overlay` {tick, cells, hash, why save|restore|mirror}; `s8a.perf`
 {tick, hookUsP99}. Subnautica `s8a.notice` {snFrame, seq, kind, bx, by, bz, version, served, scheduledMs, appliedMs,
 cellsReloaded}; `s8a.load` {snFrame, bx, by, bz, path, bytes, fnv, ok}; `s8a.status` {snFrame, ackSeq, appliedSeq,
 chartSeqApplied, served, failures, nearPendingSeen, areaBuiltNative, areaBuilt} (1 Hz); `s8a.rays` {label, columns,
 hits}; `s8a.spawn` {snFrame, techType, band, count, despawned}; `s8a.biome` {snFrame, cameraBiome, biome}.
- Analyzer rows (`tests/s8a/analyze_s8a.py`; PASS, FAIL, MISSING, INFO): `S8a.C1.roundtrip`, `S8a.C1.rules`,
 `S8a.C2.sculpt`, `S8a.C2.flat`, `S8a.C3.pin`, `S8a.N1.edit`, `S8a.N1.latency`, `S8a.N1.visible`, `S8a.N2.position`,
 `S8a.N2.size`, `S8a.N2.overlap`, `S8a.N3.rays`, `S8a.N3.rim` (INFO), `S8a.N4.save`, `S8a.N4.file`, `S8a.N4.portable`,
 `S8a.N5.store`, `S8a.N5.bytes`, `S8a.N5.reload`, `S8a.N6.install`, `S8a.N6.writes`, `S8a.N6.hostsave`, `S8a.N7.rays`,
 `S8a.N7.coverage`, `S8a.N7.time`, `S8a.N7.memory`, `S8a.N7.edit`, `S8a.N8.jump`, `S8a.N8.snap`, `S8a.N8.batchstep`,
 `S8a.N8.picture`, `S8a.N9.gate`, `S8a.N9.resume`, `S8a.N10.material`, `S8a.N10.spawn`, `S8a.N10.biome` (INFO),
 `S8a.T-P1r.<state>`, `S8a.Mx.red`.

**Sessions.** S8a-0 offline (`s8a --parts c1,c2,c3`, every selftest). S8a-1 both games idle on
`DE Lab S8a`: `n1,n2,n3,n5,n6,teardown`. S8a-2 on `DE Lab S8aSave`: `n4,n6`. S8a-3 after the sculpt is deployed, on
`DE Lab S8a`: `n7,n9,n10,n6,teardown`, then `--treadmill --parts n8,n6,teardown`.
(`launcher.py run` with S8a on). The mutant groups. T-P1r. **Launch count:** SE 1 (S8a-1) + 2 (S8a-2) + 2 (S8a-3) + 1
(S8a-4) + 13 groups (M13 is offline) + 3 (T-P1r) = **22**, Subnautica the same **22**; the optional plugin-less SE launch
for N4.portable on top.

*** (1) **The planet's look:** pick a seed from `python -I tools/planet/sculpt_planet.py --seed <n>
--preview <scratch>` (depth map and shaded relief; the default rules: shelves of −60 to −420 m, terraces every 40 m,
ridges up to −15 m, six trenches to −1,400 m, the crater kept flat at −3,000 m with a moat to 2,900 m), then approve
writing the 12 PNGs and the `.sbc` table into the world mod and its deploy into SE's Mods folder (user data). The new
seabed applies to **every world using `DE_4546B`**, his own saves included: approve the read-only `--check-world
<save>` first (it lists grids outside the crater that the new seabed would bury). (2) Approve the throwaway copies
`DE Lab S8a` and `DE Lab S8aSave` (saves are disposable: no separate approval needed by the standing rule; listed for
the record) and, optionally, one plugin-less SE launch for S8a.N4.portable. (3) Note the new derived store
`%LOCALAPPDATA%\DeepEngineers\terrain\` (≤ 2 GB, safe to delete at any time; nothing goes into either install or into
`SNAppData`; the host save stays never-saved).
and across a tile seam, a warhead on the copy, then out beyond the crater along the new seabed, drill it, and (with the
treadmill on) cross a step; judge the hole and its rebuild flicker, the seabed's shapes, materials, biome colours, fish
and plant density, and the step. (5) Decide the defaults after S8a: drilling on by default (S4.N2 then runs with
`DEEPENGINEERS_S8A_DRILL=off`), treadmill on by default or not. (6) Approve the launches (22 per game).

**Exit.** S8a.C1–C3 and S8a.
recorded.

**Proves** (*real run*): SE's own drilling shows and collides identically in Subnautica and survives saves in both
games; the planet beyond the crater is real seabed in both games, streamed from SE's voxels; the map can be re-anchored
far from the crater without a visible jump. **Does not prove:** multiplayer (v3), designed Subnautica biome sites
dropped into the planet ("both": a later slice uses the same converter in reverse, install batches written into the
store at chosen sites), floating-origin steps without a re-stream over terrain.

---

### S8 Robustness

**Goal.** The pair survives real life: one side dying, stalling, hanging or reloading, Alt+Tab, pause, long
sessions; stalls recover by themselves; persisted settings stay untouched; memory stays bounded; the abyss rule and
the floor guard protect SE's invisible floor; a DE world is never damaged when the terrain is missing; SE alone stays
untouched.

**Builds on:** S2–S7. **CONTRACT first:** §7 lifecycle and launcher tables, timeouts, link epoch, fail-safe, §1.1
settings touched, abyss rates.

**Work items**

| Part | Items |
|---|---|
| SE plugin | PeerStalled and PeerExited fallbacks (scene re-enabled and margins removed on the next frame, audio restored, HUD notice "underwater renderer offline", delayed 10 s outside Underwater); link-epoch recovery and resend; process-exit polling every tick while margins are on, window check on the render thread. World epoch bump on world change/reload. Abyss rule: crush zone below −2,000 m (rates of CONTRACT §4.1), floor guard below −2,500 m in every world type, `DE.Abyss DAMAGE-INACTIVE` logging, suspension for fail-safe grids. Terrain fail-safe: grids in the footprint held, saving blocked with the HUD warning. Flip-model detection at start (composed calibration pixel; per-app `UserGpuPreferences` read); automatic switch to A′ if alpha is not honoured. Activation gate checks (multiplayer, worlds without `DE_4546B`) |
| SN plugin | SE exited → mute, freeze, minimise without activation (`SW_SHOWMINNOACTIVE`), quit within 10 s (`IngameMenu.QuitGameAsync(true)` or `Application.Quit`). SE stalled → mute, freeze, Idle; **never quit on heartbeat loss**; resume and answer the new link epoch. `Quitting` handshake. New epoch → clear every mirror. Soft relink command (generation bump without a relaunch). `de.quit` and `de.stallSn <s>` test commands |
| Launcher | Exit waits on both process handles; offers (never performs automatically) a kill by exact PID after 30 s of hang; **no automatic relaunch**: the player can restart the renderer from the launcher, which starts it only after the old PID and its crash handler are gone and a 20 s cool-down; preflight lists a `SpaceEngineers -report` crash form; registry keys compared and restored |

**Oracle**

| Check | What | Pass |
|---|---|---|
| S8.T-L1a | `de.quit` while underwater | PeerExited and SE fallback within one SE frame of `Quitting` or the process exit; no SE frame > 25 ms; no composed frame shows non-game pixels |
| S8.T-L1b | `de.stallSn 10` (Subnautica blocks its main thread 10 s) | PeerStalled at 3 s (underwater: SE's own view); afterwards, same PID: link epoch +1, `linkAckEpoch` answered, full resend (stall ≥ 10 s), counts match S5.N1; no ghost window and no Subnautica taskbar entry during the stall |
| S8.T-L1c | normal SE exit | the spy sees `seExiting` written before the SE process ends (our `ExitThreadSafe` prefix ran before Pulsar's kill); Subnautica starts quitting within 10 s (CONTRACT §7) and its process is gone within 15 s; no `UnityCrashHandler64` left; Steam shows both games closed |
| S8.T-L1d | player-initiated renderer restart while SE is foreground | `GetForegroundWindow == seHwnd` at every 100 ms sample; new generation detected; resend counts match S5.N1; the launcher waited for the old PID, its crash handler and 20 s |
| S8.T-L1e | `fake_se_producer.py` drives the real Subnautica plugin, then the stand-in process is ended | Subnautica minimised without activation, quitting within 10 s (CONTRACT §7) and its process gone in ≤ 15 s; no crash handler left |
| S8.T-L1f | the fake producer stops its heartbeat for 60 s but stays alive, then resumes | Subnautica frozen and muted from 3 s, never quits, resumes and acknowledges the new link epoch |
| S8.T-L1g | crash-style Subnautica exit underwater: a killed SN stand-in window under the real SE (the real game only through a dev-build `de.crashSn` that calls `Environment. never an external kill) | SE draws its scene on the next frame; no composed frame shows the desktop or a third-party window |
| S8.Phase` transition line; section 5.12); Subnautica survives and stays Idle; no "SE stalled" episode while the load lasts ≤ 120 s (beyond: one recoverable stall); the link recovers without manual action |
| S8.N1 | SE world reload | epoch bump; mirrors cleared and rebuilt; our mesh count back to baseline |
| S8.N2 | soft relink | generation bump; SE resends every model and part; counts match S5.N1 |
| S8.N3 | long run: a 60-minute mixed session (space, approach, surface sailing, dives, combat) | working sets, SE `GCMemoryUsed` (STATISTICS) and Subnautica's Mono heap show no growth > 200 MB per 30 min after warm-up (HYPOTHESIS threshold); commit < 85 %; no heartbeat gap > 1 s outside Space and Track; no exception from our assemblies |
| S8.N4 | Alt+Tab × 10 and ESC × 10 underwater | state restored each time; audio rules hold |
| S8.N5 | abyss rule (Survival, destructible blocks; world copy) | a 100-block reference hull sinking from −1,950 m at 5 m/s is destroyed before −2,500 m (time logged); nothing goes below −2,500 m; a grid at −1,990 m takes no damage; a character below −2,000 m takes the set rate; no effect on other planets |
| S8.N5b | abyss rule in a Creative, non-destructible copy | `DE.Abyss DAMAGE-INACTIVE` logged once; the floor guard stops the hull at −2,500 m ± 5 m |
| S8.N6 | SE never diminished | with the plugin installed and Subnautica not running: a session on another planet and on the dive planet (Water Mod rendering underwater), no errors; S4.G2-E2 re-run; other planets keep the Water Mod's default crush. Audio options opened underwater and OK clicked, then exit: `SpaceEngineers.cfg` `GameVolume` and `Music_Volume` equal the pre-session backup |
| S8.N7 | flip-model detection | the start-up measurement reports alpha honoured on this build; a forced "not honoured" result switches to A′ |
| S8.N8 | terrain fail-safe (world copy with a grid resting on the crater) | with a forced manifest mismatch: the grid moves < 1 cm over 60 s; no save is written (attempts blocked with the HUD warning); no abyss crush on it |
| S8.N9 | settings restore after an unclean end | fallback audio path forced; an autosave underwater; the session ended underwater by `CloseMainWindow` (Pulsar's kill path); then vanilla SE: `WaterClientSettings.xml` `Volume` equals the backup value; `restore.json` consumed at the next plugin `Init` |
| S8.T-P1r | T-P1 with every feature on (including 50 Hz Subnautica physics with proxies visible) | S5.T-P1 criteria still met |

**Seeded failures:** S8.M1 heartbeat written from a worker thread instead of the game thread → S8.T-L1b never
detects the stall. S8.M2 crush zone and floor guard disabled → the reference hull of S8.N5 reaches the −3,000 m floor.
S8.M3 mirrors not cleared on a new epoch → S8.N1 mesh count grows. S8.M4 launcher restart without the cool-down →
S8.T-L1d reports the violation. S8.M5 floor guard off in the Creative copy → S8.N5b red. S8.M6 no `LoadingWorld`
phase → S8.T-L1h red (Subnautica logs "SE stalled" at 3 s into the load). S8.M7 fail-safe off → S8.N8 grid sinks.
S8.M8 restore skipped → S8.N9 red. S8.M9 Subnautica ignores SE's process exit → S8.T-L1e red. S8.M10 Subnautica quits
on heartbeat loss → S8.T-L1f red. S8.M11 exit polled at 1 Hz while margins are on → S8.T-L1g shows desktop pixels.
S8.M12 activation gate removed → S8.N6 reads `TopMost` false in the non-DE world. Never kill a real game as a test
step.

*** Approve each robustness session, the world copies (rule 10) and the multiplayer join on the

Approve `de.crashSn` on the real Subnautica, or keep T-L1g on the stand-in only. Review the long-run summary. The
abyss rates are chosen in CONTRACT

**Exit.** All checks green, mutants red.

**Proves** (*real run*): the failure matrix behaves as designed on this PC, and no failure of the bridge damages a
save or a persisted setting. **Does not prove:** GPU driver resets (TDR) are never induced; behaviour on other
machines or other Windows builds.

*not yet run with the games).**
Built: SE `S8Rules.cs` (the fallback table peer × view × time: scene drawn on the frame, margins off after one drawn
present, latch dropped, crossfade snap, audio restored at once instead of the 300 ms ramp; the HUD notice "underwater
renderer offline" at once with Subnautica's picture on screen, after 10 s elsewhere, in a DE world, once per loss;
the exit-poll and resend rules; rows `s8.peer`, `s8.notice`), the display guard in `DisplayController` (a linked peer
not alive is never skipped over or lowered under, dev overrides included), `S2Host` re-reading the link in the
PrepareForDraw step (a loss found in this update is drawn in this frame), `Link` stall bookkeeping (`LastStallForMs`,
`Recoveries`), the `ExitThreadSafe` prefix writing `seExiting` before the log flush, memory columns in the 1 s
`sample` row (`S8Memory.cs`); SN `S8Rules.cs` (reaction table, quit path, quit step with `Application.Quit` 5 s after
the issue when SE exited, `de.stallSn` syntax, the Alt+Tab / ESC audit), `S8Host.cs` (rows `s8.peer`, `s8.quit`,
`s8.restore`; `s8{…} mem{…}` in `DE.S2 sec`), `de.stallSn` (dev C11); launcher `launcher_s8.py` (the `run` exit path:
waits on both handles, samples the section read-only per poll, CloseMainWindow 15 s after SE, a kill OFFER after 30 s
of hang, never a kill or a relaunch; `restart-renderer`; `restore-settings`; `kill --pid` with the typed PID; the
settings backup, the pre-session snapshot and the restore of bridge-made values at the next clean start). Tests: SE
S8Tests (10), SN S8Tests (7), `python -I tests/s8/test_s8_launcher.py` (27 checks with the stand-ins).
**Not in the minimum:** the abyss rule and the floor guard (S8.N5/N5b, S8.M2/M5), the terrain fail-safe holds
(S8.N8), the flip-model detection and the automatic A′ switch at start (S8.N7), the activation-gate additions, the
world-epoch / soft relink commands (S8.N1/N2, S8.M3), `de.crashSn` (T-L1g stays on the stand-in), the seeded failures S8.M1–M12 and the 60-minute run itself (S8.N3: the counters are in place).
**Integrated** with S9 (`docs/S8S9-STATUS.md`): the S8 paths use only process handles and the section
in the release flavour (no C11; `launcher.py stop` falls back to `CloseMainWindow` when `de.quit` gets no answer);
the unused S0 `launcher.wait_exits` was removed (the `run` exit path is `launcher_s8.wait_exits`).

---

### S9 Showcase and release prep

**Goal.
decision), a clean release lint, README install steps and a tested uninstall.

**Work items:** record the composed screen with the method chosen in S2.N0 (a window capture of SE alone would miss
the Subnautica layer); `um win record` for single-window clips with game-only audio where useful; the
`showcase-video` skill for the cut.2.4.2 conditions
decompilation on its results not being disclosed to third parties without Keen's written consent, and 2.2.3 concerns
combining the software with other programs; the options are to keep `docs/recon` and SE internal-behaviour notes
private, to strip internal-behaviour detail from public notes, or to ask Keen for consent. Field notes with `um kb new
--game subnautica...` and `--game space-engineers...` only within that decision (BepInEx route, ghost camera,
MarmosetUBER through Addressables, Freedom vs Creative `NoAggression`, save guards; Pulsar Local plugins,
provider-backed voxel tiles with deterministic ids; technique notes on two-window DWM see-through switching and a
C#↔C# bridge with an independent Python oracle); `um kb check`; Release lint
`tools/release/check_release.py` (run with `python -I`, exit ≠ 0 on failure): (1) an allowlist manifest of release
files (built DLLs with the SHA-256 the deploy step logged, world-mod files produced by the generator script, README,
NOTICE, `launcher.py`), any other file fails; (2) normalised token 12-grams of every shipped `.cs` file compared with
the scratchpad decomp, any hit fails; (3) `um publish check <release folder> --game <SE install>` and again with the
Subnautica install, as extra lints. README: what to own (both games), Python 3.10+, Pulsar, BepInEx pack (Nautilus not
needed), Water Mod + Text HUD API, the world mod, the world settings of a DE world, creating the Freedom host save,
the launcher, "never open a DE world without the plugin and the world mod", "set both games to update only when
launched". `docs/install/UNINSTALL.md` generated from the manifest of everything created or changed. NOTICE: Keen
credit and the notice that SE is modified (EULA 1.5); Water Mod used through the Workshop (no licence file; our API
client is our own code); SkyCraft, Killcraft and the MIT parts of LibertyCraft re-implemented (MIT); ValCraft read
only (no licence); Vehicle Framework and Nitrox not used (GPL); SE EULA 2.2.4; AI
disclosure.

**Oracle:** S9.N1 `check_release.py` exits 0 on the release folder. S9.
fresh drop folders and S0's checks pass. S9.N3 composed video frames are live and show Subnautica underwater with SE's
HUD; S9.N4 the approved field notes pass `um kb check`. S9.N5 uninstall run, following
`UNINSTALL.md`: the Subnautica folder listing equals the pre-BepInEx snapshot (the user's saves excepted); SE started
from Steam (no Pulsar) loads a non-DE world with no `DE.` lines; `WaterClientSettings.xml`, the `SpaceEngineers.cfg`
audio keys and the Subnautica registry keys equal the backups.
**Seeded failures:** S9.M1 a header-less ILSpy snippet and a `.mesh` cache file planted in
a scratch copy of the release → `check_release.py` exits ≠ 0 for each (`um publish check` alone would only warn or
miss them). S9.M2 one uninstall step skipped → the S9.N5 diff names it.

**Installer.** `python -I tools/release/make_release.py
[--configuration C] [--zip]` builds the release folder from the repository and a finished build (no build, no
download): `Install.cmd`/`install.ps1`, `Play.cmd`/`play.ps1`: a zip backup
step instead of the universal-modder CLI, then `launcher.py run`), `Uninstall.cmd`, README/NOTICE/UNINSTALL, `release.json` + `SHA256SUMS.txt` (every file with SHA-256, the pins), `plugins/` (no pdb), `worldmod/
DeepEngineers.World/`, `world-template/DeepEngineers/`, `launcher/` (launcher.py with launcher_paths, launcher_release,
launcher_s8, de_install, de_steam, de_pins, layout_py, section_py). `install.ps1` (PowerShell 5.1) finds Python ≥ 3.10
and runs `launcher/de_install.py`, which prints one PASS/WARN/FAIL line per step: release folder against release.json;
games closed; Steam through the registry and `libraryfolders.vdf`; both games' app manifests with the pins of
`de_pins.py` (SE Steam build 24675677 = 1.210.014; Subnautica build 20241558 and changeset 83031) — a different build
**stops** before anything is written; "only update when launched" (WARN); Subnautica's CompiledOctreesCache against the
terrain gate's pinned listing (stop); Water Mod 3.23 + Text HUD API in the SE library's Workshop folder (FAIL with the
subscribe links); a Freedom Subnautica save for the renderer (recorded for the launcher: the plugin's built-in host
session is this PC's); Pulsar 2.4.3 and Tobey's BepInEx pack 5.4.23-pack.3.1.1 downloaded at install time from their
GitHub release URLs, SHA-256 checked (or `-OfflineDir`, or reused when their marker files match; another BepInEx is
kept with a WARN); the two plugins; the Pulsar profile entry; the world mod; the template world copied into
`Saves\<SteamID64>\<name>` (never over an existing folder); `%LOCALAPPDATA%\DeepEngineers\install\install-manifest.json`
(every file, folder and entry created or changed, with hashes and the backup of anything replaced) + `paths.json`;
then `launcher.py backup` and `preflight`. A second run repairs. `Uninstall.cmd` walks the manifest backwards (settings
through `launcher.py restore-settings --yes`; a played template world and the world mod are kept; a BepInEx another mod
uses is kept). The template world is `tools/release/make_template_world.py`'s read-only derivation of "DE Lab S4": the
planet entity and its pristine `.vx2` (SE cannot load a planet without its storage file), the Water Mod sea for that
planet only, one player identity that a stranger's game takes over, a free character hovering 50 m above the anchor,
the DE_LAB_WORLD settings and the 20-minute day in both world files; no grid. Tests: `python -I
tests/release/run_tests.py` (fake Steam libraries, games, Workshop, zips over file:// URLs; no network).

*** The publishing decision (O13). Approve the release contents, the video, field-note PRs and any
publishing. Approve the uninstall run (it changes game folders; reinstall afterwards).

**Proves:** the release is clean, installable from its README and removable from `UNINSTALL.md` on this PC. **Does
not prove:** a second machine.

**Built.**
- *Flavour switch:* `build.ps1 -Flavor release` (= `dotnet build -c Release -p:DeepEngFlavor=release`) defines
 `DEEPENG_RELEASE` in both plugins and builds into `src\<plugin>\bin\ReleaseFlavor\Release\` (Wire too; no pdb, source
 root mapped, so no machine path in a DLL); The
 release flavour: no mutants, no harness ring (C11a), no measure-only mode, no S0.N12/S1.N11 hooks, `/de` limited to
 the user tuning commands of `Wire.UserCommandRules` (their relay to Subnautica kept; CONTRACT §5.1 C11 row); the
 hello lines say `build=release flavor=release`.
- *Version gates:* `Wire.VersionPinRules` holds the pins and the plain words; SE: the `DE.Version REFUSE` line + one
 red HUD notice at the first in-world update; Subnautica: `SnVersionGate` (changeset 83031, else
 `Application.version`), dormant in a bridge launch with one `ErrorMessage` notice (CONTRACT §0, §7).
- *Docs:* `docs/README.md` (public), `NOTICE.md`, `docs/install/UNINSTALL.md` generated by
 `tools/release/gen_uninstall.py` from the installer's manifest format (`de_install.py`, format 1; `--check` keeps
 it current). The README follows the installer (Install.cmd copies the world and checks the Freedom save) instead of
 the hand-made steps listed above.
- *Lint:* `tools/release/check_release.py release|sources`, allowlist `tools/release/allowlist.txt` (`build=` pins the
 release-flavour DLLs and every copied file to its repository source); exit = sum of bits 1 allowlist, 2 decompiled
 12-grams (.cs and.py; braces, comments, `this.`, using/namespace lines and pure-number windows normalised away),
 4 machine paths / Steam ids (text, UTF-16, binary u64 and varint) / decompiler markers, 8 `um publish check`; 16
 usage. The decomp folder comes from `--decomp` or `$DEEPENG_DECOMP` (never stored). On a trial release folder
 (make_release.py, release-flavour DLLs): exit 0 with all four checks (S9.N1 on this PC); a scratch copy with a planted
 `.mesh` file and 30 header-less lines of the decomp: exit 3 (S9.M1). On the repository's sources (the planned source
 release) check 2 reports 1,574 shared runs in 287 files, 34 of them SUSPECT
 the reviewed ones. `tools/release/strip_decomp_refs.py` copies the tree without the decompiled-source citations in
 Tests
 `tests/release/test_check_release.py`, `test_strip_decomp_refs.py`; `tests/DeepEngineers.Wire.Tests/S9ReleaseRulesTests.cs`.

**Integrated.**
Both flavours build with 0 warnings and 0 errors and every test is green; a release folder made by `make_release.py
--flavour release` passes `check_release.py` with all four checks (S9.N1 on this PC, no game run), and the S9.M1
plants (a header-less ILSpy snippet, a `.mesh` file) and four planted machine paths or Steam ids each fail it. The
shipped installer ran against the S9 fixtures (dry run, install, status, uninstall back to the pre-install listing).
Integrator changes: uninstall keeps what `%LOCALAPPDATA%\DeepEngineers` held before the first install (manifest
`localBefore`: the development PC's logs, census and registry backup are not deleted); a dry run on a PC without
BepInEx plans the plugins instead of failing them; the installer warns when a release folder is not the release
flavour; the README names `restart-renderer` next to the S8 "renderer offline" notice. S9.N2 (Install.cmd on this PC)
and S9.N5 (Uninstall.

---

## 5. Development environment

### 5.1 Repository layout (D14)

| Path | Contents |
|---|---|
| `DeepEngineers.sln`, `Directory.Build.props`, `build.ps1` | EXIST: solution; machine paths as overridable properties (`SEBin64`, `SubnauticaDir`, `PulsarDir`; env `DEEPENG_SE_BIN64`, `DEEPENG_SUBNAUTICA`), defaults set to this PC; `build.ps1` builds and deploys (S0 "Carried-in code") |
| `src/DeepEngineers.Wire/` | netstandard2.0, deployed as `DeepEngineers.Wire.dll` next to each plugin: layout constants, seqlock and ring code, `Native` P/Invoke, `WireMath`, `SiteV1` constants, the display state machine as a pure function, self-test runner, embedded WM-1 subsets. No game, Unity or Harmony reference. EXISTS (S0, layout 1.0): `Layout`, `Records`, `LayoutHash`, `Seqlock`, `ByteRing`, `EntryRing`, `Resync`, `Native`, `SharedSection` (+ `SectionCreator`, `SectionValidation`), `WireInfo`, `BridgeToken`, `WireMath`, `SiteMap`, `Geometry`, `SiteV1`, `SelfTest`, `Wm1Subset`, `PyFloat`; the display state machine comes later (S2) |
| `src/DeepEngineers.SE/` | net48 x64 Pulsar plugin (assembly `plugin`): gate, bridge, site binding, Water Mod API client, snapshot writer, mesh/part/skin streaming, see-through, audio, terrain provider and tiles, fail-safe, damage consumer, abyss rule, statics consumer. EXISTS (S0): `Plugin`, `Link`, `Phases`, `VersionGate`, `ActivationGate`, `DevChannel`, `PerfLog`, `HarmonyPatches`, mutants and logs |
| `src/DeepEngineers. EXISTS (S0): `Plugin` (bridge start on the first player-loop `Update`), `Bridge`/`BridgeStartup`/`BridgeBehaviour`, `SectionLink`, `SePeerMonitor`, `ControlConsumer`, `WindowRole`, `UnitySelfTest`, phases and logs |
| `src/DeepEngineers.Launcher/` | `launcher.py` (Python 3.10 stdlib): preflight, deploy, manifest, run, stop, restore-registry (EXISTS, S0) |
| `tools/` | existing oracles (`worldmap/`, `sn-octrees/`, `sn-octree-scan/`, `mwm-winding/`, `probe-dwm-standins/`) plus `spy/`, `measure/`, `capture/`, `audio/`, `logs/`, `release/` |
| `tests/` | EXIST (S0): `DeepEngineers.Wire.Tests` (net48 + net472), `DeepEngineers.WireMath.Tests` (net48), `DeepEngineers.SE.Tests` (net48), `DeepEngineers.SN.Tests` (net472; resolves `BepInEx.dll` and `UnityEngine.CoreModule.dll` from the install), `DeepEngineers.Wire.CrossTest`, `WireSelfTest/` (rewritten for layout 1.0), `wire/` (Python layout, section code, cross test), `standins/`, `wm1/`; all in `DeepEngineers.sln`. `wire_py/` (pre-contract) was removed at the S0 exit. Added in S1: `DeepEngineers.WorldMod.Tests`, `tests/s1/` (check scripts and `session_b.py`). To add: `DeepEngineers.Terrain.Tests` (C#), per-slice check scripts `s2/`…`s9/`. Outputs in `tests/out/` (in `.gitignore` with `TestResults/`) |
| `docs/` | CONTRACT, ARCHITECTURE, ROADMAP, `S0-STATUS.md`, `S1-STATUS.md`, `worldmod/DE_LAB_WORLD.md`, `install/` manifests (`subnautica-bepinex-files.txt`, `deepengineers-files.txt`, `UNINSTALL.md`; the launcher's `subnautica-screenmanager-backup.json`, see 5.6), `recon/` |

`DeepEngineers.Wire` ships as `DeepEngineers.Wire.dll` beside each plugin. This is the v1 decision, and it is proven:
the skeleton's Wire. Pulsar resolves
sibling DLLs with `AssetResolver.ResolveLocal`). Our assembly name is unique, so the Bin64 resolver issue (Pulsar #57)
does not apply. Both copies come from one build.

### 5.2 Target frameworks and build
- Wire netstandard2.0; SE net48, `PlatformTarget` x64; SN net472; both plugins `AllowUnsafeBlocks`. Language version
 "latest" only for features that need no runtime support.
- `dotnet build -c Release` (SDK 10/8 installed). `Microsoft.NETFramework.ReferenceAssemblies` 1.0.3 as a build-only
 package. No other NuGet dependency in either plugin.
- Tests: `dotnet test` (net48 and net472 targets) and Python 3.10 with `-I`; numpy only where `tools/` already uses it.

### 5.3 Reference assemblies (all `Private=false`, never copied or shipped)

| Plugin | From | Assemblies (what they are used for) |
|---|---|---|
| SE | `C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64` | `Sandbox.Game` (`MySandboxGame`, `MySession`, `MyCubeGrid`, `MyPlanet`, `MyOctreeStorage`, `MyWorldGenerator`, `MyGuiScreenLoading`), `Sandbox.Common`, `Sandbox.Graphics` (`MyGuiScreenBase`, the type of `MyGuiScreenLoading.m_screenToLoad`; referenced since S0, D26), `SpaceEngineers.Game` (`MyLandingGear`), `SpaceEngineers.ObjectBuilders`, `VRage` (`IPlugin`, `MySpectator`), `VRage.Game` (`MyModel`, `MyCamera`, `IMyStorageDataProvider`, `MyEntityIdentifier`), `VRage.Math`, `VRage.Library`, `VRage.Input`, `VRage.Audio`, `VRage.Render` (`MyRenderProxy`), `VRage.Render11` (`MyRender11.FullDraw/DrawScene`), `VRage.Platform.Windows` (`MyGameWindow`, `MyWindowsWindows`), `HavokWrapper` (S7 static bodies) |
| SE | `third_party\pulsar\extracted\Libraries\Legacy` | `0Harmony.dll` (Lib.Harmony 2.4.2.0) |
| SN | `...\Subnautica\Subnautica_Data\Managed` | `Assembly-CSharp`, `Assembly-CSharp-firstpass`, `UnityEngine`, `UnityEngine.CoreModule`, `UnityEngine.PhysicsModule`, `UnityEngine.InputLegacyModule` (S0), `UnityEngine.ScreenCaptureModule`, `UnityEngine.ImageConversionModule`, `Unity.InputSystem`, `FMODUnity`, `Unity.Addressables`, `Unity.ResourceManager` |
| SN | `...\Subnautica\BepInEx\core` | `BepInEx.dll`, `0Harmony.dll` (HarmonyX 2.9.0.0). No `Nautilus.dll` reference |

Private members are reached with Harmony's `AccessTools`/`Traverse`, not with publicised copies of game assemblies.
The Water Mod is reached only through its message API and the two version-gated internals of ARCHITECTURE §6.

### 5.4 Harmony
Two different Harmony builds: SE uses Pulsar's Lib.Harmony 2.4.2.0, Subnautica uses BepInEx's HarmonyX 2.9.0.0. Each
plugin references its loader's copy and never ships one. Wire never references Harmony. Patch ids
`deepengineers.se` and `deepengineers.sn`. Every patch logs its target at start, and a missing target fails the
plugin's start-up self-check instead of silently doing nothing. The `ExitThreadSafe` prefix uses
`[HarmonyPriority(Priority.First)]` so it runs before Pulsar's own prefix.

### 5.5 How our code is loaded
** Pulsar scans `Legacy\Local\`: top-level
 managed DLLs (id = file name) or sub-folders containing `plugin.dll` (id = `<dirname>.dll`) or `<dirname>.dll`. It is
 enabled by `<string>DeepEngineers.SE.dll</string>` under `<Local>` in `Legacy\Profiles\Current.xml`, which
 `build.ps1` writes (R0.3 closed). Loading is `Assembly.LoadFrom` + the first type implementing `IPlugin`; sibling
 DLLs resolve from the folder; Pulsar registers our `[MySessionComponentDescriptor]` classes. Always launch with
 `-noUpdate`: an update wipes the Pulsar folder, so it holds deployed copies only, never data.
- **Pulsar, DevFolder (alternative).** Pulsar compiles a source folder at launch with its Roslyn
 `Libraries\Compiler\Compiler.exe` (`CompilerTimeout` 30,000 ms in `Legacy\config.xml`). Untested; a possible release
 option (players compile sources rather than receive a binary).
- **BepInEx.** `<Subnautica>\BepInEx\plugins\DeepEngineers\` holds `DeepEngineers.SN.dll`, `DeepEngineers.Wire.dll`
 (and the pdb in dev builds); a game-folder write, listed in `docs/install/deepengineers-files.txt`; config
 `BepInEx\config\deepengineers.sn.cfg`. Uninstall: `UNINSTALL.md`.
- `launcher.py deploy` builds nothing; it copies the built DLLs to both folders and logs their SHA-256, so the
 "loaded" check (S0.N5) can be compared with the "built" hash, and the release lint can check its allowlist.

### 5.6 Paths

| What | Where |
|---|---|
| SE game log | `%APPDATA%\SpaceEngineers\SpaceEngineers_<timestamp>.log` (+ `VRageRender-DirectX11_<timestamp>.log`); our lines start with `DE.` |
| Pulsar log | `third_party\pulsar\extracted\Legacy\info.log` |
| BepInEx log | `<Subnautica>\BepInEx\LogOutput.log` |
| Unity log | `%USERPROFILE%\AppData\LocalLow\Unknown Worlds\Subnautica\Player.log` (+ `Player-prev.log`) |
| Our structured logs | `%LOCALAPPDATA%\DeepEngineers\logs\`: `se-<timestamp>.jsonl`, `sn-<timestamp>-<pid>.jsonl` (bridge mode only; the PID tells two Subnautica instances apart, D24), `launcher-<timestamp>.jsonl` |
| Captures, caches, census, restore file, registry backup | `%LOCALAPPDATA%\DeepEngineers\{captures,cache\meshes,cache\textures,census,restore.json,registry-backup.json}`. The launcher also writes the registry backup to `docs/install/subnautica-screenmanager-backup.json` (machine-specific display values
| SE saves / local mods / global storage | `%APPDATA%\SpaceEngineers\Saves\<SteamID64>\` / `%APPDATA%\SpaceEngineers\Mods\` / `%APPDATA%\SpaceEngineers\Storage\` (holds `WaterClientSettings.xml`) |
| Subnautica saves | `<Subnautica>\SNAppData\SavedGames\` (inside the game folder, Steam Cloud synced) |
| Backups | `~/.universal-modder/backups/` (`se-appdata`, `subnautica-locallow` (logs only), `subnautica-saves` from S0, renewed in S1 once the host slot exists) |
| Release folder | `launcher.py` beside `..\release.json` runs in release mode (`launcher_paths.py`): games from `%LOCALAPPDATA%\DeepEngineers\install\paths.json` (the installer's) or Steam, Pulsar `%LOCALAPPDATA%\DeepEngineers\Pulsar`, backups `%LOCALAPPDATA%\DeepEngineers\backups` (`launcher.py backup`, newest 3 kept), the install manifest beside `paths.json`, downloads cached in `%LOCALAPPDATA%\DeepEngineers\downloads`; this PC's paths are dev-mode defaults only |
| Test results | `tests/out/` |

### 5.
1. `launcher.py preflight`: CONTRACT §7 launcher table (no game process, crash handlers and crash forms listed, ≥ 12
 GB RAM, backups newer than the last session, section absent, registry keys equal to the backup)
 informed.
2. Start Subnautica directly: `Subnautica.exe -monitor 1` with `DEEPENGINEERS_BRIDGE=1`, holding the named mutex
 `Local\DeepEngineers_v1_launcher` (the direct launch works: S0 real runs). No size flags (ignored, critic F7).
 `-vrmode None` and `-force-d3d11` are optional (HYPOTHESIS: `boot.config` enables VR probing). `steam.exe -applaunch 264710` only as a fallback.
3. Wait for the Subnautica plugin's phase in the header: `Menu` in S0, `ReadyIdle` from S1 (180 s; measured 18.4–20 s,
 O11), with `snPid` = the started process (`--any-sn-pid` relaxes it); then close the
 section handle and release the token. On timeout do not start SE: report, leave Subnautica running and close it
 normally (`launcher.py stop`); `--se-anyway` starts SE with the bridge offline (CONTRACT §7; changed after the first
 S0 real run, where starting SE anyway hid a destroyed bridge object).
4. Start SE as a child: `third_party\pulsar\extracted\Legacy.exe -noUpdate -noSplash -noPrompt -bin64 "C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64"`;
 tests may add `-continue` to load the last world. Steam must be running. `AllowSetForegroundWindow(sePid)`; refuse
 mixed elevation.
5. Wait for `DE.Link UP` and `InWorld`; drive the scene through C11 control commands (5.10); collect logs and
 captures.
6. Close SE with `CloseMainWindow`, wait on its PID (< 45 s observed). The Subnautica plugin then quits by itself
 (D11); otherwise send `de.quit`, then `CloseMainWindow`, then wait on its PID.
7.
 confirmation. Clean crash handlers by exact PID. Never relaunch right after a forced quit.

### 5.8 Python stand-ins and spies (independent of the C# code)
- `tests/standins/fake_sn_host.py`: creates the section exactly like the Subnautica plugin (ctypes
 `CreateFileMappingW` with the CONTRACT §6 security descriptor and the creator rules, `MapViewOfFile`), publishes
 phases, ready flags, heartbeats, `presentedSeTick`, synthetic and malformed damage records; checks what SE sends
 (WM numbers, records).
- `tests/standins/fake_se_producer.py`: opens the section like SE (ctypes `OpenFileMappingW`, fails cleanly when
 absent); sends scripted camera paths (WM-2/3/4, yaw steps, `y` traces), the procedural cube and real DDS paths
 (S3a), recorded model/part streams (S5), synthetic skins (S6); can stall its heartbeat or exit (S8.T-L1e/f).
- `tools/spy/de_spy.py`: read-only view of the live section (`FILE_MAP_READ`): heartbeat ages, generations, phases,
 epochs, seq counters, ring fill, region checksums; used in every real run. Its region checksums are BLAKE2b-64 (fast;
 `--fnv` adds FNV-1a 64 of small regions) while the C# tests use FNV-1a 64; both serve only "unchanged" comparisons
 (D42).
- `fake_sn_host.py` also plays an incompatible creator for the mutants: `--mutant magic|wire2` (S0.M1) and `--mutant
 layout2|size` (S0.M6, a foreign creator holding no mutex), with `--passive` and `--no-token-check`; `--mutant
 zero-heads` is S0.M7.
- All read the layout from `tests/wire/layout_py.py`, written by hand from CONTRACT. No tool uses `mmap` tag names
 (they create the section with default security). If C# and Python disagree, the contract decides.

### 5.9 The WM-1 vector file
- Generated, never committed: `python -I tools/worldmap/wire_ref.py vectors tests/out/wm1.jsonl [--site v1|example]`
 (default `v1`) → 25,011 JSON lines, 9.11 MB per site, deterministic; sha256 on this machine:
 SITE_V1 `da0968793172ab364b7b062e48f1acc9c64d5199ae21b04b8de24e1904507674`, EXAMPLE
 `9f04bb727eac6e658cf4f10cd35dac6ee0874e7b5ad346d797e13bdba1862004` (CONTRACT §2.9; O1 closed). The `eb3e08f0...0d5b`
 value of gap-1 §5 (23,011 lines, 7.29 MB) belongs to the pre-S0 EXAMPLE file. `wire_ref.py selftest` runs both sites
 (`--site v1|example|all`).
- Header: site, conventions, tolerances. Kinds: point, dir, pose, camera, local, box (with rotated boxes), mesh,
 motion, static. Tolerances the header does not carry are in CONTRACT §2.9 (D13).
- In-game subsets (`tests/wm1/make_subset.py`): 50 `pose` records for SE; 50 `local` + 20 `box` for Subnautica;
 compiled as C# arrays into `DeepEngineers.Wire.dll` (`Wm1Subset.cs`, not embedded resources; D15), with their SHA-256
 rechecked and logged at self-test time. The full file is checked by `tests/DeepEngineers.WireMath.Tests` (D16).

### 5.10 Test control channel
Dev builds take commands on C11 (CONTRACT §5.1, §5.4): JSON lines on four rings in the section, active only while the
header's `devControl` is non-zero, never read by release builds. A release build is one compiled with the symbol
`DEEPENG_RELEASE` (both plugins use the same symbol since S0; the S9 release configuration must define it, D25). The
Python harness attaches as the producer of CtlToSe and CtlToSn (bit 0); capture requests go through C10.`); the SE
plugin runs them and relays those meant for Subnautica on CtlToSn (bit 1, exclusive with a harness), where the SN
plugin executes them, using `DevConsole.SendConsoleCommand` for game console commands. Subnautica's own console
cannot receive keystrokes in a bridged session (the window is never focused and input is cleared every frame); it is
used directly only in dormant, unbridged sessions (S4.G2-E1). There is no control-file channel. From the S1 real runs:
SE drains C11a only while bit 0 is set and re-syncs its tail when it attaches or sees a new peer, so a harness repeats
`de.ping` until SE answers before its first real command; `tools/lab/se_cmd.py` does this for one command to SE, or to
Subnautica with `--to sn` (C11c/C11d), and cannot run while the launcher's own harness holds bit 0 (CONTRACT §5.4 C11
lists the S1 commands).

### 5.11 Lessons from the S0 real run
- **The SN bridge object is created on the plugin's first player-loop `Update`, never in `Awake`.** BepInEx runs the
 chainloader from `Application`'s static constructor, before the first scene load; a GameObject created there was
 destroyed by the StartScreen load 8 s later with 0 `Update` calls, although `DontDestroyOnLoad` had been called. The
 whole bridge start now runs on frame 1 (scene PreStartScreen);
 `OnDestroy` writes `snPhase = None` unless `OnApplicationQuit` ran.
- **SE's autosave blocks its update thread for about 150 ms** (156 ms measured; one heartbeat age of 141 ms, 51 updates
 in that second, frame max 73 ms). Heartbeat gaps up to about 150 ms are normal; any per-tick writer or freshness
 threshold must tolerate them (S0.N6 reads "< 100 ms outside SE autosaves"; the 3 s stall rule is unaffected).
- **SE is frame-capped at 120 fps here (frame time 8.333 ms) while its simulation steps at 60 Hz** (S0.N7: p95 8.3332 ms
 with and without the plugin; plugin cost about 4–5 µs per update). A check that counts SE updates must say whether it
 counts frames or simulation ticks (`GameplayFrameCounter`).
- **Never block a game thread; every timer on the unscaled clock** (CONTRACT §2.1, §5; one bounded exception, the
 ≤ 100 ms quit acknowledgement inside `OnApplicationQuit`, CONTRACT §7). No exception from our assemblies in any S0
 real run.
- **The launcher aborts when Subnautica never reaches the ready phase** (`--se-anyway` to start SE with the bridge
 offline), so a failed bridge is reported instead of hidden behind a 180 s wait.
- **`LogOutput.log` belongs to the first Subnautica instance**: a second instance's lines are lost there. Per-instance
 evidence comes from `sn-<timestamp>-<pid>.jsonl` (to do: open it before the mutex check, S0.N10).
- **Harmony targets** are looked up with explicit parameter types and VERIFIED; a missing
 target logs `DE.Harmony MISSING` and stays inert (5.4).
implementation tool build and
 run the no-game tests only. Writes into game, loader or `%APPDATA%\SpaceEngineers` folders (plugin folders, the world

### 5.12 Lessons from the S1 real runs
- **In S1 Subnautica idles in `ReadyIdle`** with its camera driven by C1, cameras on and time running; WarmingUp and
 Active are S2 phases, entered only through the perf recipes (CONTRACT §5.4 "C5/C7 semantics for S2"). A tool that
 waits for Active against an S1 build waits forever (the first `session_b.py` did).
- **Streaming (S1.N3):** after a camera jump the ±16 m near field is built within about 1 s and `IsWorldSettled` holds
 on the busy → idle edge within about 1.3 s up to 1 km and 3.1 s at 2 km; a ±56 m box is never reported built, so
 C7 `areaBuilt` uses ±16 m. The SE teleport latch makes C1 snap on two written frames, so a probe ignores a snap
 without a move (a 0 m "jump").
- **Phase timing:** the section shows `sePhase = LoadingWorld` at the `RunLoad` prefix; the SE log's `DE.Phase Menu ->
 LoadingWorld` line comes when `RunLoad` returns, about 17–24 s later. Timing checks use the section or the
 RunLoad-prefix line.
- **The Water Mod sends its API dictionary from its `BeforeStart` only** (3.23; S1.M9 red on the real game): the 50271
 handler must be registered in `LoadData`.
- **A save edit that deletes `SANDBOX_0_0_0_.sbsB5` makes the next load fail with `MyLoadingNeedXMLException`** and a
 "convert from XML?" prompt that needs one click in SE, so it never precedes an unattended launch.
- **SE's spectator in the lab world:** the DE Lab character stood 1,850 km from the site until the save edit, so a
 scripted session first teleports the spectator to the site (`de.teleport 0 -150 0`); C1 is off-site for Subnautica
 until then.
- **Measured numbers to reuse:** pose transport p95 ≈ 16 ms (S1.N1), yaw-step response p95 7.65 ms (S1.N2), Subnautica
 `ReadyIdle` 18.4–20 s, SE world load 23–30 s (O11), `GetDepth = y` exactly (`componentRadius` = Rs).
- **Harness facts:** the launcher's `run` blocks until the games exit and must never be piped (the detached game
 inherits the pipe, so a pipeline waits for Subnautica; redirect to a file); under Windows PowerShell 5.1 a JSON
 argument loses its inner quotes, so tools take `key=value` tokens; the preflight needs the three backups newer than
 the last DE session, so they are refreshed before every launch (`um backup create <src> --name <name>`).

---

## 6. Measurement and oracle tooling

| Tool | State | Use |
|---|---|---|
| `measure.ps1` sampler | ported in S0 to `tools/measure/measure.ps1` (CPU % of 16 threads, working set, GPU 3D-engine % per PID, foreground window, `Responding`; process names `Legacy` and `Subnautica`; 1 s option, CSV output) | used by S2.N3/N4/N8/N11, S5.T-P1, S8.N3 |
| `um win shot` | ready (`um win setup` installed ffmpeg with gfxcapture, encoder h264_amf) | one window's own frames, even when covered (not minimised): `um win shot --exe Subnautica.exe out.png --scale 0.33`. It cannot show the composed picture of design F |
| Composed-desktop capture | scratchpad `shot.ps1` (GDI desktop; T-D0 proved it sees DWM composition) | port to `tools/capture/desktop_shot.ps1`; video method chosen in S2.N0; black-frame detector for S2.T-D1 |
| In-game captures | Subnautica: exists since S1 (`de.capture`, PNG + JSON sidecar under `%LOCALAPPDATA%\DeepEngineers\captures`, used in the S1 real runs); SE: to build (no SE capture in S1, D70) | Subnautica `ScreenCapture.CaptureScreenshot` at end of frame; SE `MyRenderProxy.TakeScreenshot(sizeMultiplier, path, debug, ignoreSprites, showNotification)` (VERIFIED signature); both through C10 requests, with sidecars |
| SE STATISTICS line | present in every SE log, about once a minute, with a `STATISTICS LEGEND` line | fields: time, network counters, `CPULoadSmooth`, `ThreadLoadSmooth`, `GetOnlinePlayerCount`, `Ping`, `GCMemoryUsed`, `ProcessMemory`, `PCUBuilt`, `PCU`, `GridsCount`, `RenderCPULoadSmooth`, `RenderGPULoadSmooth`, `HardwareCPULoad`, `HardwareAvailableMemory`, `FrameTime`, `LowSimQuality`, `FrameTimeLimit`, `FrameTimeCPU`, `FrameTimeGPU`, `CPULoadLimit`, `TrackedMemory`, `GCMemoryAllocated`, `PersistedEncounters`, `EncounterEntities`, `DRS`. Parser `tools/logs/se_stats.py`. Too sparse for short checks: our SE plugin logs simulation ratio, update ms and frame ms every second, and `DEEPENGINEERS_SE_MODE=measure` runs only that sampler, which is the plugin-less baseline of S0.N7 and of later "SE alone" comparisons (D34) |
| BepInEx `LogOutput.log` | present | load lines `Loading [<name> <version>]`, our `DE.` lines, `[Error`/`[Fatal` grep |
| `Player.log` | written by the Steam copy now | exceptions, D3D and resolution lines |
| Our JSONL logs | exist (S0; names in 5.6) | every numeric check reads these, never screen text |
T-A1 |
| Section spy | exists (S0, `tools/spy/de_spy.py`; `--s1` adds the C1 and C7 S1 fields) | heartbeat ages, generations, epochs, ring fill, region checksums |
| Launcher sessions | exist (`src/DeepEngineers.Launcher/launcher.py`: `preflight`, `deploy`, `manifest`, `run`, `stop`, `restore-registry`, and `s1`, the scripted S1 session with its oracle table and `--analyze`) | `run` and `s1` block until the games exit: **never pipe them** (the detached game inherits the pipe, so a pipeline waits for Subnautica); redirect to a file. Arguments as `key=value` tokens under Windows PowerShell 5.1 |
| One C11 command | exists (S1, `tools/lab/se_cmd.py`; selftest 13/13 against the stand-ins) | one command to SE through C11a/C11b (repeats `de.ping` until SE answers, takes and releases `devControl` bit 0), or to Subnautica with `--to sn` (C11c/C11d: `de.status`, `de.capture`, …); `key=value` arguments; `--clear-stale-harness-bit` after a killed harness |
| Scripted C11 sessions | exists (S1, `tests/s1/session_b.py`: launch, S1.N3 jumps, drive gate, S1.N9 capture, S1.N12 spy, collect, analyse; analyser selftest with seeded failures) | the pattern for S2's scripted sessions (S2.N2 traces, S2.N7 dives) |
| S1 oracles | exist (`tests/s1/check_wm.py`, `latency.py`, `fmod_events.py`, each with `--selftest`) | WM-2/3/4, G1-E5 (`--g1e5-coverage`), S1.N7; S1.N1/N2 histograms and baseline shifts; FMOD events per hold |
| Lab world tools | exist (`tools/lab/make_lab_world.py` copies a save and adds the world mod to both world files; `de.spawnplanet` and `de.g2e3` in the SE plugin; `tools/lab/set_planet_position.py`, superseded by `de.spawnplanet`, still moves the character for S1.N8) | DE Lab set-up; a `.sbsB5` deletion needs one click in SE at the next load (section 5.12) |
| UI Automation script | to build (S2) | taskbar and Alt+Tab entries (S2.N9) |
| Release lint | to build (S9) | `tools/release/check_release.py`: allowlist, verbatim-code 12-grams, `um publish check` per game |
| Offline oracles | exist | `worldmap_ref.py`, `wire_ref.py` (site selector since S0), `site_v1.py`, `sn_octree_batch.py`, `sn_octree_stats.py`, `sn_crust_cuts.py`, `sn_prefab_colliders.py`, `mwm_winding.py`, `tools/sn-octree-scan`, `tools/probe-dwm-standins` |

---

## 7. Risk register

The single risk register of the project; ARCHITECTURE §8 restates the architectural ones with these ids. Likelihood
and impact are judgements (H/M/L). "Detected by" names the check that would show it.

| Id | Risk | L / I | Label | Detected by | Mitigation or fallback |
|---|---|---|---|---|---|
| R0.1 | P/Invoke file mapping misbehaves under Unity 2019.4 Mono | L / H | VERIFIED working | S0.N6 | P/Invoke is the field-proven path; `MemoryMappedFile` is not used (reported Mono JIT bug) |
| R0.2 | elevation mismatch (one game elevated) blocks the section or the window calls | L / M | REPORTED (SkyCraft); UIPI rules | launcher check | explicit security descriptor; mixed elevation refused by the launcher |
| R0.3 | Pulsar Local enabling cannot be scripted; `build.ps1` writes the entry) | | |
| R0.4 | C# layout and Python layout drift | M / H | design | S0.N1 | hand-written Python from CONTRACT; offset asserts on both runtimes; layout hash from both |
| R0.5 | power-throttling opt-out unsupported on build 22000 | — | **closed**: VERIFIED supported | S0 log of return value | log and continue; T-P1 shows whether it matters |
| R0. S0.N12 proved the runtime pin on the real SE) | version pins (runtime `GameVersion`, `WaterData.Version`, delegates), patch-target self-check, S0.N12 | display bridge refuses; terrain keeps its own gate and the fail-safe; games set to update only on launch |
| R0.7 | carried-in prototypes mistaken for the contract layout | M / M | VERIFIED (files read) | S0 "Carried-in code", S0.N1 | migrate in S0
| R1.1 | locked-mode Player reads as "not underwater": some effects stay dry, and patching it starts breathing sounds and PDA depth warnings | M / M | VERIFIED code (`UpdateIsUnderwater`, 30 readers); real run S1.N6: no breathing, PDA voice or depth warning in either variant, vanilla kept | S1.N6, S2.T-A1 | narrow patches (`WaterAmbience`, visual consumers) if S2's mix shows a need; `FreezeStats` held |
| R1.2 | the Player carries `OutOfBoundsWarp` and warps the ghost | L / M | VERIFIED present on the Player (S1 log); real run: no warp, the drive gate held at 8,000 m and +400 m | S1 log, S1 drive gate, S2.N2 zone run | keep within ±7,900 m (FAR zone: 7,800 m in, 7,900 m out; drive gate); disable the component while ghosted if it ever acts |
| R1.3 | streaming after a camera jump is slow, so the dive waits | M / M | measured (real run S1.N3): near field ±16 m < 1 s, settled ≈ 1.3 s up to 1 km, ≈ 3.1 s at 2 km | S1.N3, S2.N7 | Prime lead 2 s kept (ARCHITECTURE §4.2); `snReady` gating keeps SE's view until built |
| R1.4 | another save path writes the host save | L / M | VERIFIED two entry points; real run S1.N4 PASS, S1.M4 red (9 files changed without the guard) | S1.N4 | two guards; hash check every session |
| R1.5 | the planet needs more than all-zero heightmaps to spawn | — | **closed**: the minimal file set spawns without a load error, with the floor at −3,000.005 to −3,000.008 m and material `Sand_02` (real run, `de.g2e3` 24/24) | S1.G2-E3 | |
| R1.6 | host save in the wrong mode (Creative → `NoAggression`) | L / M | VERIFIED gate | S1.N5 | combat disabled with a log line; player recreates a Freedom save |
| R1.7 | crosshair offset noticeable at depth (0.80° at the floor, 0.97° at −2,000 m) | M / L | VERIFIED arithmetic | S1.G1-E5 | compensated Subnautica camera (v2) |
| R1.8 | Subnautica's own HUD, PDA voice or radiation FX show through or play | H / M | VERIFIED code | S1.N9 | hide phase, PDA suppression, radiation off |
| R1.9 | `PDACameraFOVControl` pulls the FOV back | H / M | VERIFIED code | S1.N7 | component disabled, FOV asserted per frame |
| R1.10 | the Water Mod API handshake is missed (late registration) | H / H | VERIFIED code; real run: received in `LoadData` in every load, S1.M9 red (the dictionary comes from the mod's `BeforeStart` only) | S1.N13, S1.M9 | handler in `LoadData` |
| R2.1 | an opaque full-screen SE sprite covers Subnautica | M / H | VERIFIED users exist (G5) | S2.T-D1, S2.N1 | sprite filter; else A′ |
| R2.2 | Unity rejects or loses `WS_EX_NOACTIVATE`/`WS_EX_TOOLWINDOW`, or keeps its taskbar button | M / H | VERIFIED docs (restyle needs hide/show) | S2.T-D1, S2.N9 | hide/restyle/show at `ReadyIdle`, `ITaskbarList:DeleteTab`, re-apply on display changes; A′ |
| R2.N8 (first gate), S8.N3 | margins on in Prime so it is composed, not occluded; alternatives (1 px alpha-0 region, `onPostRender` stamp, `areaBuilt`-only entry); stalls recoverable |
| R2.4 | a Windows feature update (21H2 is out of servicing) flip-upgrades SE and kills DWM alpha | M / H | REPORTED (builds ≥ 22557; SwapEffectUpgradeEnable=1 already set globally) | start-up measurement, S2.T-D0, S8. per-app opt-out; auto A′ |
| R2.5 | translucent SE panels wrong over Subnautica: alpha ≈ a² and colour too bright (sRGB-encoded vs premultiplied) | H / L | VERIFIED blend state; colour arithmetic HYPOTHESIS | S2.
| R2.6 | no capture method sees the composed picture at display rate | M / M | HYPOTHESIS | S2.N0 | present timestamps for timing; GDI stills for content |
| R2.7 | Water Mod volume setting persisted | — | **closed**: VERIFIED persisted globally (CONTRACT O6); the plugin never writes it | S2.T-A1, S8.N9 | emitter mute or in-memory override restored before saves |
| R2.8 | Subnautica's window size or monitor not controllable | M / M | VERIFIED (flags ignored, second monitor) | rect compare every second | `-monitor 1`, `Screen.
| R2.9 | look latency underwater too high | M / M | HYPOTHESIS | S2.T-I1, S1.N2 | orientation extrapolation; cap 120 |
| R2.10 | the z-order re-assert does not raise Subnautica over a third-party window | M / H | VERIFIED docs; 1 of 3 synthetic raises | S2.N5, `zOrderOk` | `AllowSetForegroundWindow(snPid)`, retries, stay on SE's view |
| R2.11 | SE does not get the foreground at start | M / M | HYPOTHESIS (hidden by `ForegroundLockTimeout = 0`) | S2.N10 | SE takes it itself; HUD hint |
| R2.12 | Subnautica's frame caps ignored (vsync on, `SetFrameRate` clamp) | H / M | VERIFIED code and docs | S2.N3 | pair set directly per state (C5 `targetFps` → pair, CONTRACT §5.4) and reported in C7 |
| R3.1 | MarmosetUBER unpacks normals the Unity way, so SE `_ng` needs repacking | H / M | HYPOTHESIS (strongly suggested) | S3.N1 | repack X→A, Y→G (planned) |
| R3.2 | `UWE_3COLOR` is not SE's HSV paint | H / M | HYPOTHESIS | S3.C13 | bake colourised albedo per texture set × colour |
| R3.3 | V axis or DDS row order wrong | M / M | HYPOTHESIS | S3.N1 glyph | flip V for decoded images only |
| R3.4 | `MyModel` loading off the update thread is unsafe | M / M | HYPOTHESIS | S5.N5 | one dedicated loader thread or update-thread time slicing |
| R3.5 | Unity 2019.4 does not recompute shear for rotating children | L / M | REPORTED for 2021.3+ | S3.G1-E2 | per-chunk rigid frames with K baked |
| R3.6 | in-plugin GPU texture conversion is too slow (`Compress` on the main thread) or wrong (colour space) | M / M | VERIFIED API limits | S3.C11, S3.C13, S3.M8 | budget per frame or RGBA32; out-of-process converter with BCnEncoder.NET (approval needed) |
| R4.1 | Subnautica does not draw the cut faces | L / H | HYPOTHESIS (strong) | S4.G2-E1 | depth rule under the crater footprint, or Subnautica-side cap meshes |
| R4.2 | 1024³ provider maps cost too much (dense `HkUniformGridShape` table, `ReadRange` inside the Havok step) | M / M | HYPOTHESIS | S4.A-T1 (3) | 2048³ tiles (2×2×2); plan B planet-provider patch |
| R4.3 | SE's mesher disagrees with Subnautica's surface beyond the S4 budgets (A-T1 P95 0.25 m vs oracle heights, A-T2 P95 0.3 m cross-game; incl. the half-voxel convention) | M / M | HYPOTHESIS | S4.A-T0, A-T0b, A-T1, A-T2 | tune the content mapping; accept the native budget measured in A-T2 |
| R4.4 | provider reads too slow for fast ships (> 5 ms spikes) | M / M | HYPOTHESIS | S4.A-T1 (3) | coarser `Intersect` state, read caching |
| R4.5 | physics steps before `BeforeStart` and grids fall | L / M | HYPOTHESIS | S4.G2-E2 | create tiles earlier (`LoadData`) with C deferred |
| R4.6 | the sea changes in game (`/wradius`, `/wcrush`) or differs from `WaterConfig.xml` | M / M | VERIFIED (the save holds the sea) | site validation every 10 s, S1.G2-E3 | display bridge refuses; `/de site repair` |
| R4.7 | respawn on `DE_4546B` lands on the floor | M / M | HYPOTHESIS | scenario test | Water Mod respawn pods or a sea-surface spawn (CONTRACT O5) |
| R4.8 | tiles missing in a DE world and grids sink (update, moved library, no Pulsar; or the activation gate refused: multiplayer, more than one `DE_4546B`, where the fail-safe is inert too) | M / H | VERIFIED mechanism | S8.N8 | terrain gate independent of the display pins; fail-safe; README warning; update only on launch |
| R4.9 | gear-locked ships unlocked on every reload | H / M | VERIFIED code (`MyLandingGear.RetryLockServer`) | S4.G2-E3b | deterministic tile ids |
| R4.10 | voxel edits reach the tiles through an unguarded path | M / L | VERIFIED entry points | S4.N2 | prefixes on every non-generic entry point |
| R5.1 | draw calls and vertices of large ships exceed the frame budget | M / H | HYPOTHESIS | S5.N6, S5.T-P1 | LODs, fog culling, interior-chunk shadows off, instancing test |
| R5.2 | RAM: 12.T-P1, S8.N3 | preflight ≥ 12 GB; Medium clipmaps
| R5.3 | CPU/GPU contention lowers SE's simulation ratio or frame rate (incl. Track and Prime) | M / H | HYPOTHESIS | S5.T-P1, S2.N11 | Track with cameras off; Subnautica BelowNormal when idle; affinity experiment |
| R5.4 | two-sided glass and alpha-masked SE materials render one-sided | M / L | HYPOTHESIS | S3 capture | duplicated reversed faces, or a cloned two-sided material |
| R6.1 | fast creatures tunnel through thin proxy boxes | M / M | HYPOTHESIS | S6.B-T1 (1) | speculative CCD; thicker boxes |
| R6.2 | K refreshes cost too much PhysX time on a rolling hull | M / M | HYPOTHESIS (gap-1 OQ3) | S6.B-T1 (3) | larger threshold, lower budget per step |
| R6.
| R6.4 | SE combat test silently applies nothing (Creative or non-destructible world) | H / M | VERIFIED gates | S6.M4, S6.N1 | Survival + destructible lab world |
| R6.5 | bites never reach proxies (patch, line of sight, eco-targets) | M / H | HYPOTHESIS | S6.B-T1 (2) | more anchors, smaller sections; `SubControl` path |
| R6.6 | damage applied twice, lost or misparsed after a restart | L / H | design | S6.N1, S6.N10, S0.N11 | dedup, validation, ring re-sync; overflow counter |
| R6.7 | proxies move at Subnautica's default 10 Hz and lag the hull | H / M | VERIFIED code | S6.N7 | high fixed timestep registered; per-step thresholds at 10 Hz as fallback |
| R6.8 | the swimming character is never bitten | H / M | VERIFIED code (G7 refuted) | S6.B-T1e, S6.M8 | standalone character section |
| R7.1 | `IsInsideWalkable` side effects (13 readers) misbehave | M / M | VERIFIED readers | S7.N2 | targeted patches per reader |
| R7.2 | census misses non-primitive or non-Default structures | M / M | HYPOTHESIS (sample of 9 bundles) | S7.N7 | handle mesh colliders as triangle bodies |
| R7.3 | sun direction and north-pole conventions wrong | M / L | HYPOTHESIS | S7.WM-5 captures | adjust signs |
| R7.4 | Subnautica story events (Aurora explosion, Sunbeam) fire during sessions, with time driven forward | M / L | HYPOTHESIS | first long session | host save created after those events, or suppress them |
| R7.5 | the sun axis is not perpendicular to the base direction (world settings half-edited) | M / M | VERIFIED formula | S7.WM-5 bind checks | sky-only fallback; world-setting instructions in the README |
| R8.1 | a forced quit wedges a game as a zombie until reboot | L / H | REPORTED on this machine (javaw) | launcher | exact-PID kills only after timeouts; one launch per test; no automatic relaunch |
| R8.2 | memory grows over long sessions | M / M | HYPOTHESIS | S8.N3 | explicit mesh destruction; caches bounded |
| R8.3 | heartbeats from the wrong thread hide hangs | L / H | design | S8.M1 | write heartbeats from each game thread only |
| R8.4 | GPU driver reset or exclusive-fullscreen hang (AMD) | L / H | REPORTED | n/a | borderless only; no relaunch in the same boot |
| R8.5 | a persisted setting is left changed (Water Mod volume, SE cfg audio, Subnautica options or registry) | M / M | VERIFIED mechanisms | S2.T-A1, S8.N6, S8.N9, S9.N5 | in-memory changes only, `restore.json`, save hooks, registry backup |
| R8.6 | a stall leaves the bridge off for the session | M / M | VERIFIED once (5 s blip) | S8.T-L1b | PeerStalled with link-epoch recovery |
| R8.T-L1c | `ExitThreadSafe` prefix with priority First; no exit cleanup |
| R9.1 | derived data or decompiled code slips into the release | L / H | design (`um publish check` alone cannot catch it, VERIFIED) | S9.N1, S9.M1 | own release lint with allowlist and verbatim-code check |
| R9.2 | publishing constraints (SE EULA 1.5, 1.6, 2.1, 2.2.3, 2.2.4.2; no Water Mod licence; depend on Workshop items; README states what players must own

---

Every item is a
defect or a missing finish in what S3–S7 already ship; nothing here is new scope. The orchestrator fixed P15 by hand;
P1–P14 are the S7p workflow's builders (four, grouped by the files they touch), then integrate → review → fix.

|---|---|---|---|
| P1 | "no bullets or rockets showed above water"; "grinder and other tools have no effects out of the water, inside of the water it's amazing"; Make it a rule: SE's bullets, missiles, tool sparks, thruster flames and explosions are visible above water — either SE's transparent/particle pass composited over the picture (preferred if the depth test can be kept honest: SE's grids are drawn in Subnautica, so SE's own depth is the mirror's) or the same S7 event stream played by Subnautica with above-water variants (muzzle flash, tracer, sparks, flame, smoke, blast). Missiles/rockets are entities: mirrored as moving event-driven objects if the render route is the event one. | scene-above |
| P2 | "there are no thrusters flames showing, only bubbles, it also doesn't transition the colors" | Under water a thruster shows a flame cone in the block's own colour (`MyThrust` ThrustColor / the definition's flame colours, scaled by CurrentStrength) plus the bubbles and a heat shimmer; the event record carries colour and strength. | effects-under |
| P3 | "thrusters should do massive effects from the heat (vapor) when flying by the water; this is where we should discuss about creating new assets" | A vapor/steam plume where a thruster's flame meets the surface (above and just below the waterline): composed in code from Subnautica's own particle textures/materials (no external assets, no fal); density from strength and distance to the waterline. | effects-under |
| P4 | "explosions are not great (it's just a yellow flash)" | A layered explosion: Subnautica's own explosion/shockwave VFX where one exists (the Cyclops / Seamoth destruction effects, the crash site), else fireball + smoke + shock ring + bubbles under water, debris sparks; scaled by the SE explosion radius; the sound already exists (S7). | effects-under |
| P5 | "the effect when firing the gun is just bubbles from my mask times 10" | Muzzle flash (bright, short), a tracer along the shot, an impact puff at the hit (the gun event carries muzzle, direction and hit); under water a bubble streak in place of smoke. | effects-under |
| P6 | "the drill effect is bad, it's blinding and the bit of the drill is missing" | Effect: no additive white-out; dust/sediment plume + sparks on contact, light capped. The drill head is a block subpart (SE `MyShipDrill` DrillHead/subpart): the grid mirror must carry rotating subparts (SubgridPoses) — check which subparts the S3/S5 mirror drops. | effects-under (effect), scene-above (subpart) |
| P7 | "projectors don't work" | The projected blueprint (`MyProjectorBase.ProjectedGrid`) is a grid SE renders as a hologram; mirror it into Subnautica as a ghost grid (the S7a ghost material, blue tint, no collision, no statics), following the projector's offset/rotation. | scene-above |
keep them as the swim-stroke option, off by default). Decide with `de.s5 fpbody=se` as the starting point (S5-STATUS §9.5). | character |
ModelName`, female/male/DLC skins), the colour mask (`ColorMask` HSV → the same hue/saturation/value mapping SE's shader applies) and the suit/skin textures already exported in S7 (SkinnedExport) — find which of model, colour or skin is wrong and fix the export + the Subnautica material. | character |
| P10 | "the light of my suit is absolutely blinding specially at night" | The character light and the spotlights go through the S7 light table: an intensity/range scale per source kind tuned for Subnautica's exposure (a live `de.s7 lightScale` + env), default so the suit light reads like SE's. | character |
| P11 | "mobs in SN can't be killed" | SE → creature damage: Subnautica resolves the SE gun/tool events against its creature colliders (raycast along the shot, sphere for drill/grinder/welder) and applies `LiveMixin.TakeDamage` with SE's damage numbers mapped; the S6 Combat proxies already carry the other direction. | gameplay |
| P12 | "any ship under water goes slowly towards the surface; the dampeners are on, shouldn't it compensate? instead it's me who press C" | Decision: a ship with dampeners on holds depth. Find how the buoyancy is applied (the water mod client / our S4–S5 force) and make it a force the thruster dampeners see and cancel (applied before the thrust system's dampener pass, or folded into the gravity vector the dampeners already counter); a grid without thrusters keeps its (small) positive buoyancy. | gameplay |
| P13 | "sometimes my camera is stuck in a single axis (Y)" | Reproduce from the swim/camera code paths (SwimInput pitch clamp, the hand camera, the S2 motion plan, the Subnautica CameraDriver pose): a camera that can only turn on one axis; find the state that sticks and clear it; add the symptom to the C1/C5 trace so the analyzer can catch it. | gameplay |
| P14 | "I've placed a workshop monster of a ship, SN crashed then came back I think? it's loading very slowly" | The S5 mirror budgets for a big grid: mesh building chunked across frames on Subnautica's side (never a multi-second main-thread stall), triangle/texture caps with a visible degrade (far parts coarse), SE's export throttled; the watchdog must not count a slow mirror as a dead peer. | gameplay |
| P15 | "I went from 1 km to jump in the water, the transition was very slow" | **Done by the orchestrator 21:1x:** the log shows the latch was blocked by `areaBuilt` from 566 m down to −10 m (the streamer never settles during a 110 m/s dive), not by the band; the air rule above +156 m now reports built = true (`S1Telemetry.AreaBuilt = airAbove || camBox`). | — |

Rules for S7p: nothing new in scope, no new assets from outside the two installs, no edits to the quarantined S8a files
(scratchpad `s8a-paused/`), every fix with its no-game test or a HYPOTHESIS line in docs/S7P-STATUS.

*As built, character build pass:* **P8** `fpbody=se`
by default; the S5a diver gate forced off every tick under `se` (not only while the SE body draws: the old rule brought
the diver's arms and shadow back in every frame the SE body was missing); SE's head parts shadow-only in first person as
SE draws them (VERIFIED); `s7p.char` row with the `snBodyLeak` verdict; `/de sn s5 fpbody diver` for the swim strokes.
*mwm`, four tool skins, none on the suit)
the paint was wrong (S3's tint, S3.C13 ΔE ≈ 17) → SE's `ColorizeGray` baked per texel into the character's materials
(METALNESS_COLORABLE, `de.s5 paint=bake|approx`); gender-aware fallback; the held tool's own skin; the identity line and
row. **P10** per-kind light scales (suit lamp 25 → Unity 1.0, spotlights 8 → 1.0; `de.s7 lightScale`, env
`DEEPENGINEERS_SN_LIGHT_SCALE`).
SE's picture, the lamp's feel at night.

*As built, gameplay build pass:* **P11**
Subnautica's own reader of the S7 event window: BulletStreak rays (to 250 m), explosion spheres and tool contact
spheres damage creatures (`LiveMixin.TakeDamage`, `Creature` only) with SE's numbers through `k_char` (rifle 30, gatling
33 SN_HP per projectile; explosion = SE damage with Subnautica's falloff; hand drill 20/s, hand grinder 16/s, ship
grinder/welder 120/s, ship drill 13.3/s); `de.s7p damage`, env `DEEPENGINEERS_S7P_DAMAGE`; rows `s7p.damage`, `s7p.hit`.
**P12** the rise is the Water Mod's buoyancy force against SE's dampeners; a postfix on
`MyEntityThrustComponent.CalculateRelativeVelocity` offsets the dampening target by −F/2M so the thrusters hold the depth;
`de.s7p hold`, row `s7p.hold`. **P13** SE ignores yaw in walking mode while the proxy is in the air and the character
is not falling; leaving a seat under water leaves it there (only the head turns) → SE's own `StartFalling` after
6 ticks; `de.s7p yawfix`, rows `s7p.camera` (SE, per-axis input and motion, `yawStuck`) and `s7p.cam` (Subnautica; NaN
poses now held).
then SE's 3 s watchdog restarted the whole stream; the room class is now an O(1) index, the mirror's main-thread
steps have per-frame budgets, SE's export reads are amortised; rows `s7p.mirror`, `s7p.export`. Not edited: SE's link-epoch recovery always resends everything (`S3Logic.StreamGate`, `Link`), SE's `GetRooms`
≈ 400 ms on that ship (S7 O-4).

*As built, scene-above build pass:* **P1** route A
is impossible (VERIFIED `MyRender11.FullDraw`: the skip never calls `DrawScene`; SE's particles and billboards are
drawn inside `DrawGameScene` against its G-buffer, nothing survives
pass), so route B: Subnautica plays every event row above the waterline with its own particles (muzzle flash, tracer,
sparks, thruster flame by kind, smoke, layered blast, tool sparks and drill dust) through a second reader of the C2
window, and SE's missiles in flight ride the S5 stream as floating entities (`Missile` GridAdd bit; the trail is
Subnautica's); `de.s7p above events|se|off`, env `DEEPENGINEERS_S7P_ABOVE`; rows `s7p.scene` (SE), `s7p.above` (SN).
**P6b** the drill head, grinder blade, propellers, turbines, fans and H2 rotors are turned by SE's render thread only
(the S5 mirror had them frozen): exported Dynamic with an integrated spin; the held tool's own subparts (the hand drill's
bit — the "missing bit") go as character parts; `de.s7p subparts`. **P7** a projector's blueprint rides the S5 grid
stream (`Projection` bit), SE's hidden blocks (built / not buildable) left out, a hologram material in Subnautica, no
skin/rooms/lights, gone when the projector stops; `de.s7p projections`.

blocks leaving it.

*As built, effects-under build pass:* **P2** SE sends each flame's own colour (the definition's FlameIdleColor → FlameFullColor over
strength / MAX_THRUST, SE's rule VERIFIED in `Sandbox.RenderDirect`'s `MyThrustFlameAnimator`) and its FlameLengthScale
class in the row's pad; Subnautica draws a flame cone per flame in that colour at SE's own length, its light, a heat
shimmer only if the game has a distortion material, S7's bubbles and engine loop kept. **P3** a steam plume where a flame's
heat (1.5 × its length) reaches within 4 m of the surface, above or below, density from strength and distance, 3 per grid /
12 in all, with Subnautica's surface splash as spray and boiling bubbles. **P4** the Seamoth's own destruction effect
(stripped of its debris) from a 4 m radius, else S7's crashfish, plus fireball, smoke or a bubble cloud, a shock ring at
2 × radius, debris sparks, a flash scaled by the radius. **P5** the S7 gun look was literally the diver's breath bubbles
(`PlayerBreathBubbles` at the muzzle, ~80 along the shot, 8 at the hit; the flash was a light only): now a 50 ms muzzle
flash, a tracer flying muzzle → hit (SE's own ray gives the hit distance), a fine bubble streak from the Seamoth's bubbles,
sparks and the game's impact puff at the hit. **P6a** S7 replayed the 1.5 s exoDrill one-shot every 0.1 s (≈ 15 stacked
additive copies) and held the Prawn's drill effect on ship drills: now one held alpha-blended dust plume per drill coloured
by what it touches (SE sends the contact point and the voxel material class), sparks on metal, a light capped at 1.0 / 4 m.
All live: `de.s7p fx <type> <scale>` (chat `/de s7p fx flame 1.5`), env `DEEPENGINEERS_S7P_FX`; rows `s7p.fx` (SN) and the
`s7p*` keys of `s7.fx` (SE).
the explosion's scale, the tracer's length, the drill's dust.

*Integrated:* the four builders build together (0 warnings, 0 errors), every test
and no-game check is green, layout 1.0 and every earlier pin unchanged (new pin `ext s7p fx 1` only), 0 unresolved
references on both plugins. `de.s7p` is one verb with four handlers, each answering only its own keys (SE chain:
gameplay → effects-under `fx` → scene-above; Subnautica: effects-under → scene-above → gameplay); the integrator fixed
Subnautica's two-word chat form `/de sn s7p above se`. Above water the route is scene-above's (P1) unless
`de.s7p fx above 1` hands the thruster, muzzle, streak, impact, explosion and drill rows to effects-under's looks. Nothing
is deployed by the integrator

### the transition is really limiting the true results, it's ugly, I would love to fix this first") — right after S7p, before S8a
**Goal.** The swap between SE's picture and Subnautica's at the Approach band stops being a cut: both pictures look
alike where they meet, they blend into each other, and the band goes back up to 1 km so a ship is well inside
Subnautica's picture before the sea fills the screen. What exists today: the picture latch and the band (S7a, live
+400 / +450 m), `CrossingFlash` (a tint sprite of a few hundred ms over the cut), `WaterLook` (the Water Mod's ocean
tuned towards Subnautica's palette, `de.water`), the time sync (S7) so both suns agree. What is missing: a real blend,
Subnautica's horizon and sky at altitude, the band height, high ships in Subnautica's view.

| id | Item | Direction | Side |
|---|---|---|---|
| T1 | **Crossfade** instead of a cut, both ways, default 700 ms, live-tunable | Display F puts Subnautica's window behind SE's and SE's swap chain is transparent where the scene is skipped (S2, flip-model alpha honoured; A′ copies the frame when not). A crossfade = SE's scene still rendered while a final alpha-only pass writes the backbuffer alpha from 1 → 0 (to Subnautica) or 0 → 1 (back to SE) with an ease curve; the scene skip engages when the fade-in ends and disengages when the fade-out starts; the A′ path applies the same ramp in its copy composite. Driven by the view machine's picture edge (`ViewOutputs.PictureEdge`); under `Site` the fade starts at the latch (with the air rule of 21:1x the latch is on at the band during a dive) and the fade-out at the leave dwell. `CrossingFlash` stays for the waterline; under `Site` it is off by default when the crossfade is on. Trace `s7t.fade` rows {tick, dir, ms}; `de.s7t fade <ms>|off`, env `DEEPENGINEERS_FADE_MS`. Perf: SE's full scene for 0.7 s at the band is the cost; the perf mode stays what the machine says. | SE |
| T2 | **Subnautica's picture right up to ~1.5 km**: horizon, haze, sky | (a) The ocean surface to the horizon: from 1 km the `WaterSurface` grid's edge shows; add a far horizon ring/plane at sea level with the surface's material (coarse mesh, no displacement) out to the horizon distance for the camera's altitude, or extend the grid if the component allows it. (b) Distance haze by altitude: fog colour/density so the far water and the crater-edge void (no seabed beyond the crater until S8a) fade into the atmosphere colour, not into a visible edge. (c) Nothing of the lore reappears (landmarks rule). The sky's colours at altitude are T5's. Oracle: `tests/s7t/compare.py` on screenshot pairs at the band (mean colour of the sky / horizon / water bands within a tolerance; | SN |
| T3 | **Band back to 1 km + impostors** | Default `ViewThresholds` +1,000 / +1,100 m again once T2 holds (live setting, no pin; the launcher/analyzer defaults follow; the test band is pinned separately). The S5 mirror reaches grids above the picture's camera as coarse impostors: large blocks only, no textures, no subparts, no lights, no effects, capped per tick, out to ~5 km up (the S7a mirror radii 1,500 / 1,600 m stay the horizontal rule; a vertical reach added, CONTRACT 5.4 lines), so a ship in SE's sky is a shape in Subnautica's view; SE's HUD markers already draw on top. | SE + SN |
you can really tell you are switching games, especially because of the shadows and the skybox" | Both renderers move toward one target look where they meet (Subnautica's palette: under water it is the reference, and the planet from space stays SE's). (a) SE under `Site` across the band: the post-process settings and the sun/shadow parameters (shadow darkness and softness, ambient intensity, the environment's sun properties reachable at run time) interpolated from SE's own look in `Space` to the match set at the band, restored on leave; (b) SE's planet atmosphere — our own `src/DeepEngineers.WorldMod/Data/PlanetGeneratorDefinitions. the orchestrator copied EarthLike's `AtmosphereSettings` and `CloudLayers` (values from the game's definitions, textures referenced by path) into it 22:55 — deployed by `tools/worldmod/deploy_worldmod.ps1` with the S7p deploy (SE closed). T5 then tunes those values toward Subnautica's sky palette from the surface and from 1 km (a definition change needs the world reloaded — say so in the runbook); (c) Subnautica at and above the surface: the sun light's shadow strength/softness and the ambient (uSky's ambient/ground colour), the sky's horizon colour, haze and cloud layer at altitude, the colour grade above water, all toward the same target; (d) one `de. the sun direction stays the S7 time sync. Oracle: `tests/s7t/compare.py` on screenshot pairs at the band: sky, horizon and water bands, plus a shadowed hull face against a lit face (luminance ratio = shadow contrast) within a tolerance; mean RGB / luminance of the same screen regions): sky top SE (135,221,235) L204 vs Subnautica (123,176,234) L169 — SE's sky is pale cyan, Subnautica's a deeper blue; sky at the horizon SE (153,234,246) L218 vs SN (197,241,255) L233 — Subnautica has a bright white haze band at the horizon; far water SE (38,72,96) L67 vs SN (129,155,187) L152 — Subnautica has strong aerial perspective over distance, SE none; near water SE (27,68,104) L62 vs SN (65,97,122) L92 — SE's water darker and more saturated. SE's picture also showed stars in daylight and no clouds (the missing AtmosphereSettings, now EarthLike's) and the 60 km planet's strong horizon curvature (Rs is pinned: not a target). | SE + SN |
the character's mask is S7p P9. Apply SE's mask formula to the mirrored block materials: SE's shader takes the block's base colour texture (the "ColorMask"/alpha channel marks the masked area) and shifts it by the grid's HSV offsets on Subnautica's side per block instance (the C13 bit/fields of the S3 record carry the mask; TextureImport/MarmosetMaterials build the material); skins stay as exported. Oracle: the pair's ship and suit colours within a tolerance in compare.py. | SE + SN |
| T4 | **SE's ocean from above matches Subnautica's at the band** | `WaterLook` defaults re-tuned so the Water Mod's ocean seen from the band and above (colour, fog colour/distance, wave scale, foam, specular) matches T2's picture at the band — the same screenshot pairs; the look applies at bind as today; `de.water` stays live. | SE |

*
layer covers the island from altitude in Subnautica's picture; T2/T5 raise or switch it off above the band.

a ship parked at
+2 km watched from the surface): no visible cut, no horizon edge, the same sky and water colour across the band; the
`s7t.fade` rows show both directions
only, no sessions by the orchestrator: "no verification, we will see that next week"), then the project pauses.** Estimate: one workflow of ~7 tool, 2.5–. Runs after S7p returns
because both edit the composite (RenderHooks/FrameComposer), the S5 mirror and the shared hooks.

*Integrated:* the four builders (se-composite T1/T3/T4, sn-altitude T2/T3, look-match
T5, colour-mask T6) build together (0 warnings, 0 errors); every test is green (1,549: Wire 461 × 2, SE 277, SN 257,
WireMath 51, Terrain 36, WorldMod 6); 67 no-game commands exit 0; the Cecil scan resolves both plugins (0 unresolved).
Integrator edits: the measured band colours in one source (`, read by SE's fog colour, Subnautica's
skirt and pinned against `band_compare.py`), the look match's atmosphere sent back after a disable, the world-mod
seeded-failure anchors after the EarthLike `CloudLayers`.
hide the island in SN".

## 8. Out of v1

| Item | Why or when |
|---|---|
| Multiplayer (the plugin is inert there) | D13, D8 |
** Shape: each player runs the pair (SE client + hidden Subnautica); SE's netcode syncs grids/characters/voxel edits; mirrors from the local client's replicated entities; **one ocean authority** = the host's Subnautica (creature poses relayed through SE's mod network channel, the friends' creatures as puppets; damage only from the host's ocean, applied by SE's server authority); every player owns both games; the world mod published or shared; launcher usable by a friend; testing needs a second machine/account (dedicated server + one client covers part). Mod compat: publish the Harmony patch list + a startup conflict report (SE plugins); Subnautica player/camera/HUD mods conflict, creature/flora/biome mods are content; **the SE terrain provider must read Subnautica terrain patch files** (keeps collision = picture; also the v2 path for designed seabed). Keep v1/v2 free of one-player assumptions. | D13 (v3)
| SE 3D-only feedback underwater: block placement ghost and highlights (`MyCubeBuilder`), welding/grinding particles, LCD text and offscreen textures (stale while see-through), first-person hands and held tools | D13 (critic G12) |
| Weather on the dive planet | D3, D13 |
| The void treadmill; the FAR zone shows SE's Water Mod fog | D13; **in S8a: the treadmill charts (`TreadmillOn`, CONTRACT §3.
SE's HUD markers already draw on top) so a ship high in SE's sky is spotted from the sea; built with the S7-1 fixes: renderers, colliders, exploder/radiation/lights off, statics skip it; env DEEPENGINEERS_SN_LANDMARKS / de.s7 landmarks to compare)**, the other landmarks behind the same toggle later; also avoids the ghost save's Aurora explosion when the synced clock passes its day; Subnautica's sky dome showing SE's own skybox textures (read from the player's install) as a taste option. SE's live sky behind Subnautica's sea (reverse layering, Unity per-pixel alpha) only on request. | S7/S8 items
we have time to think about it". Shape: a sliding window that writes Subnautica terrain batches around the player from SE's voxels (the inverse of S4's reader; octree format already understood), biome/material assignment, generated spawns, the site map re-anchored in batch steps; the crater stays authentic. Bigger than S4. Stages: (1) the void treadmill (open ocean everywhere, no seam at the site edge), (2) SE terrain converted on the fly, (3) biomes and spawns. Decide the content source first (SE's procedural seabed vs designed biomes). S8a-1 drilling first, S8a-2 the seabed**; was v2; **contract-s8a: ROADMAP "### S8a"** |
| Hull-pressure gameplay (Subnautica-style depth progression) | D13 (v2); v1 has `CrushDamage` 0 plus the abyss rule and floor guard. **Scheduled as S16 (§14) on.** |
| Subnautica 2 (UE5) | D13; the visual-host boundary keeps it possible later |
| Armor deformation, decals, projector holograms, emissive status parts | ship-render later slices; **projector holograms done in S7p P7** |
| Damage dealers other than bites, ampeel shocks and crashfish blasts: `AttackCyclops`, reaper grabs, `AttachAndSuck`; ramming impulses | critic F6/G4 |
SN doesn't have voxels that break like SE"** — Subnautica's terrain *is* voxel (Voxeland octrees; the early-access Terraformer edited them and the edit path survives in the engine), so a drill works as: SE drills its own voxel layer over the provider tiles (edit guards lifted for a copy-on-write layer), and the edited cells are converted into Subnautica octree edits around the hole — the same SE-voxel → Subnautica-terrain converter as v2's seabed beyond the crater. v1: drilling the Subnautica-backed tiles stays blocked (the guards), the rest of the planet drills in SE alone. | collision probe §7.4; split S8a-1 (drilling: edits inside the site through the converter, a small window) then S8a-2 (seabed beyond the crater: the sliding window over the planet, biomes/spawns). **Contract-s8a (ROADMAP "### S8a"):** the engine's own edit path does not survive in Subnautica 1.22.83031 (`compactTrees` is never filled, so `PerformSphereEdit`/`dig` edit nothing, and no per-save octree cache is read); S8a rewrites the batch file in its own store and reloads the batch and its clipmap cells |
| SE drawing Subnautica-only objects above water (Aurora hull, island plants) | world-mapping OQ4 |
| Several dive sites; Lifepod 5 and gas-giant choices | world-mapping §4.3, OQ5 |
| Compensated Subnautica camera for the crosshair bias (≤ 0.80° at the floor, 0.97° at −2,000 m) | D4 known limitation; gap-1 OQ2 |
| Per-chunk sub-frames for very large static bases; look prediction | gap-1 §3.5 v2; ship-render §6.5 (needs focus option B) |
| HUD rebuilt in Subnautica | only if A′ becomes the display (D1 "later") |
| Mesh and texture disk caches | stretch (v1.x), outside the definition of done |
| Protection of a DE world opened without the plugin | v1.x idea: a ModAPI guard in the world mod (HYPOTHESIS) |
")** — S9 deliverable: one installer (packaged launcher, no Python): verifies both Steam installs, pinned hash-checked Pulsar + BepInEx, deploys both plugins, installs the planet world mod (definitions only) and the lab world template, preflight + backups, launches in order, exact uninstall; channels: SE Workshop for the planet mod + dependencies (Water Mod, Text HUD API), Pulsar plugin hub for the SE plugin, Thunderstore/Nexus for the Subnautica plugin (r2modman); stated minimum RAM (≈32 GB until S8 lowers it), Windows + both games owned; honest AI-built labelling, code and converters only. | S9; ties into v3 (joining a friend's world) |

---

## 9. Definition of done for v1

v1 is done when all of the following hold, as *real runs* on the pinned versions (section 0):

1. Stretch items (mesh
 and texture caches) are not required.
2.py run`: an SE survival world with `DE_4546B` and the DE world
 settings; fly from space to the site; sail a 2,000-block submarine on the surface; dive. Underwater looks like
 A reaper and a boneshark attack
 SE damage follows the contract. The SE character walks on Subnautica's seabed and swims; it is bitten. Interiors are
 unfogged; lights and thrusters show. Surface, return to space: Subnautica idles and SE's frame time is back to within
 5 % of SE alone. No crash, no error from our assemblies; no persisted setting changed.
3. **SE is never diminished:** S8.N6, S8.N8 and S8.N9 green (SE with the plugin but without Subnautica plays normally
 everywhere; saves load without the plugin; other planets' oceans untouched; a missing terrain never damages a
 world; settings untouched) and the S5.T-P1 Space **and Track** criteria met (surface play on `DE_4546B` costs ≤ 5 %).
4. **The visual-host boundary holds:** the SE plugin references no Subnautica type and speaks only the wire; every
 Subnautica-specific rule (mirror, materials, creatures) lives in `DeepEngineers.SN`; the hello names the host
 (`hostKind`) and its capabilities.
5. **Docs:** CONTRACT complete (ownership, settings touched, gates, units and axes, channels with offsets, protocol,
 lifecycle, damage, site, not covered); ARCHITECTURE up to date; this roadmap updated with outcomes; README install;
 `UNINSTALL.md` tested (S9.N5); NOTICE.
6. **Release hygiene:** S9.N1 clean
 check`; the showcase video approved.
7. **Known limitations written down:** crosshair bias up to (s−1)/2 rad (0.80° at the deepest floor, 0.97° at
 −2,000 m); FAR-zone fog; the out-of-v1 list; the single test machine; a DE world opened without the plugin (or in
 multiplayer, where the plugin is inert) is not protected.

---

## 10.

***

|---|---|---|
| Downloads (Pulsar, BepInEx pack, Nautilus), loader installs into the Subnautica folder, ffmpeg via `um win setup` | Day 0 | "ok pour tout" (downloads + loader installs + ffmpeg) |
| Backups `se-appdata`, `subnautica-locallow` | Day 0 | yes (note: `subnautica-locallow` holds logs only, not saves) |
| Water Mod subscription (Workshop 2200451495
| Scratch `BuildTest` plugin dropped into `BepInEx\plugins\DeepEngineers.
| Pulsar Local test plugin and profile edit (inside `third_party`, not a game folder), then restored | 20:54 | within the Pulsar install approval |
| Skeleton deployed by `build.ps1`: `<Subnautica>\BepInEx\plugins\DeepEngineers\` (`DeepEngineers.SN.dll`, `.pdb`, `DeepEngineers.Wire.dll`, dated 20:56) and Pulsar `Local\DeepEngineers.SE\` with the `Current. Listed since S0 in `docs/install/deepengineers-files.txt` and `UNINSTALL.md` (`subnautica-bepinex-files.txt` keeps its 40 loader lines) |

**Still needed.**

| Slice | Approvals | In-game or system actions | Judgements |
|---|---|---|---|
| S0 | **done** | done | none |
still open: none | S1.N8: swim (first and third person) and sit at the site, after `set_planet_position.py` and one click on SE's conversion prompt | the WM-2 and S1.N9 captures (`wm2.png`, `n9.png`) |
| S2 | pycaw download (not given yet); Notepad placement script; launches | T-I1 as the human at the keyboard (twice); A′ decision if T-D1 or S2.N8 fails |
| S3 | launches | paint colour (optional) | the block next to the Seamoth |
| S4 | one SE launch without our plugin on a world copy; throwaway world copies (S4.M4, S4.M10, G2-E5); a dormant Subnautica session for the console warps; launches | G2-E1 warps | A-T2/WM-6 captures; voxel materials; tile size |
| S5 | baseline launches (each game alone at the T-P1 spots) and combined launches | spawn three test ships; pilot the T-P1 states | a real ship underwater |
| S6 | Survival + destructible SE world copy; console spawns (`de.spawn`); throwaway host-slot copy; launches | none required | the bite |
| S7 | census session; throwaway world copies; launches | world settings of the lab world (sun, interval, oxygen, pressurisation) | interiors, lights, thrusters, day/night |
| S8 | each robustness session; world copies; `de.crashSn` on the real Subnautica (optional); launches | Alt+Tab/ESC sequences | long-run summary |
| S9 | publishing decision (O13); release contents, video, field-note PRs, publishing; the uninstall run (game folders change; reinstall afterwards) | follow the README alone | the video cut |
| any | the launcher's backup and restore of Subnautica's `Screenmanager` registry keys; optional UnityExplorer download; optional BCnEncoder.NET (texture fallback) | only if needed: Subnautica's Display option set to the primary monitor once (R2.8); after a Windows feature update, the per-app opt-out for `Legacy.exe` (a system setting, R2.4) | |

---

## 11. Open items that can change this roadmap

| Item | Decided by | Effect if it goes the other way |
|---|---|---|
| A covered or alpha-1-composed Subnautica keeps rendering | S2.N8 | an alternative of ARCHITECTURE §4.4; if none works, A′ |
| Subnautica draws the crust's cut faces | S4.G2-E1 | depth rule or cap meshes added to S4 |
| DWM alpha honoured with the real games | S2.T-D1 | A′ becomes v1's display; HUD underwater out of v1 |
| Shear recomputed for rotating children in Unity 2019.4 | S3.G1-E2 | per-chunk rigid frames before S5 |
| Cost of 1024³ provider tiles | S4.A-T1 | 2048³ tiles or the planet-provider patch |
| Half-voxel convention of the provider | S4.A-T0b | provider sample offset changed |
| Locked-mode "underwater" effects | S1.N6 (real run: no unwanted event in either variant, vanilla kept) | narrow patches, re-checked by S2.T-A1 once the Water Mod emitters are muted |
| Subnautica's surface displacement at the camera | S1.N10 re-run with the fixed sampler (O15) | the 0.20 m entry threshold moves below the measured trough |
| Texture conversion speed in the plugin | S3 | out-of-process converter (new dependency, approval) |
| Composed-capture method | S2.N0 | timing checks rely on present timestamps only |
| Water Mod emitter mute feasible | S2 (CONTRACT O10) | in-memory `Settings.Volume` fallback with restore before saves |
| Cost of 50 Hz Subnautica physics | S5.T-P1 / S8.T-P1r | 10 Hz with per-0.1 s thresholds |

| Scale factors `k_char`, `k_small`, `k_large` | chosen in CONTRACT §4.1

---

## 12.

"v1" = this roadmap's slices; "v2" = after S9.

| Idea | Decision | Slice / item |
|---|---|---|
| Submarines: Subnautica's, or built in SE | SE grids are the submarines (design rule; Water Mod buoyancy; S5 draws them in Subnautica). Subnautica vehicles stay out of v1; "Seamoth-style" SE blueprints are a v2 content port | S5, v2 |
| Subnautica movement underwater | accepted | S2b |
| Water Mod only on the dive planet; make it "Europa" | one ocean planet already; the vanilla Europa (≈ 9.5 km radius) cannot host the 4 km flat site (curvature) — **re-skinning DE_4546B as Europa is possible
| Place blocks "Minecraft-style" underwater | native SE building; S3/S5 show the blocks in Subnautica. v1 limit: voxel edits (digging) are SE-only, not shown in the Subnautica picture | S3, S5; v2 voxel mirror |
CONTRACT event record first |
| No ship building underwater: flooded electronics, build on a platform | accepted as a gameplay rule: functional blocks (cockpits, thrusters, reactors, …) fail while submerged unless inside an airtight room (SE's gas-system rooms) → submarines must be airtight | **S5b Flooding rules** (after S5; oracle: block states vs submersion, airtight room keeps them dry, seeded failure: rule off) |

| Skybox | above water the sky is SE's planet atmosphere (SE renders everything above water); below water it is Subnautica's. Accord the two: DE_4546B atmosphere colours tuned towards Subnautica's sky, sun synchronised (the known MISMATCH) | S7 |
Entities are SE's and never move or vanish. Three paths, see §13 | **v2 goal**, with a feasibility spike |
| Subnautica creatures underwater | accepted | S6 |
Fix: a speed-aware lead (Prime when the predicted crossing is within max(2 s, the S1.N3 settle time for the distance)) and **pre-streaming the predicted impact point** (the ghost tracks the predicted crossing point, not the camera, while falling); oracle: a scripted fall from +300 m at 90 m/s → Underwater ≤ 300 ms after the crossing | S2 polish ("fast entry"), with the crossing fade |
| Smooth crossing (no frame-to-frame cut) | accepted: a short "water veil" fade (~250 ms) drawn by SE at the crossing while Subnautica is already underneath
| The Water Mod's surface breaks the mood vs Subnautica's water | accepted. needs a real-run spike (mask pass in SE's render, GPU budget ≈ SN 50 % + SE 30 %, sky accord: SN's reflections show SN's sky), after S4 (islands then exist in both worlds) and S5 (the submerged hull reappears in SN). Fallback: tune the Water Mod's per-planet settings (colour, fog, transparency, reflections, small waves) and the sun
| Subnautica swim animation? character roll? water/air transitions with and without jetpack | S2b: no Subnautica animation in SE (no swim cycle; first person is the reference); roll disabled in water (pitch only, like Subnautica); jetpack off in the water and back when the head is out; leaving the water onto a platform = jump + jetpack. S5: Subnautica draws the diver's arms/fins on the SE camera | S2b, S5 |
compatibility *on* the site (MES, AiEnabled, DefenseShields…) is a late item | S8/S9 |
| SE ores replacing Subnautica's | accepted with the v1 limit: SE ore deposits in the seabed tiles (at Subnautica's outcrop spots or random), SE detector/drills work; drilling changes SE's terrain, not the Subnautica picture (v2 voxel mirror) | S4 item "ores" |

## 13. v2 goal: a whole-planet ocean with relief

Subnautica's world is a hand-made 4 × 4 km terrain in batch octrees; beyond it the engine shows only the "void". It
does not generate terrain. Three paths, cheapest first:
1. **A lattice of sites**: the Subnautica map repeated (rotated/mirrored copies) over the planet; the S1 map F accepts
 any anchor, S4's provider lays the same tiles. Relief everywhere at once; the repetition shows (an archipelago of
 craters, each ringed by its void).
2. **SE relief drawn by Subnautica**: outside the sites the seabed is SE-generated voxel terrain (unlimited size), mirrored
 into the Subnautica picture the way S5 mirrors grids (voxel chunk meshes exported through C3/C4), lit and fogged by
 Subnautica. No repetition; Subnautica's flora/fauna are placed afterwards. The path that scales. Main direction.
3. **Enlarging Subnautica's own world**: generating new octree batches and pushing the streamer's bounds (mods patch
 batches; extending the map is uncharted). Feasibility spike before any promise.
AI image generation (fal) is not a terrain tool (no continuity, no determinism); it may provide lookalike textures.

## 14.

Each slice: one long tool plus a review. S8a resumes as one long tool plus a review, not the 4-build pass workflow: `wf_22478c02-e45` cannot be resumed across sessions, its contract tool's output lives in the S8a sections of ROADMAP/CONTRACT/ARCHITECTURE, and the builders' partial files are kept in `paused/s8a/`. Where an existing
slice already covers a point, the point is a **fix** of that slice, not new work; it is marked.

Measure on both PCs first; site bind during SE's loading screen; perf presets low/mid/high on both sides with an
auto-pick at install; 30 fps cap on shared GPUs; kit cache on disk; 16 GB run as the pass mark.
**Window streaming under a burst:** a teleport queued ~170 ring notices; 151 batch
reloads gave up at the 10 s limit (168 retries). The Subnautica reload tracker must order reloads by camera distance
and scale the give-up with the queue, and SE should send the near box first after a jump.
*friend's log):** a Workshop blueprint of a few thousand blocks drops Subnautica to
1 fps while SE stays at 120 (44 grids, 3402 blocks, 6608 parts, frameMsP99 5625 ms). The mirror build must become
incremental and cheap: a per-frame part budget ordered by camera distance, one instanced or combined mesh per block
type per grid chunk, colliders only for the S6 proxies near the character, a per-grid light cap, and a merged hull
mode for grids above a size. a 30,000-block grid stays playable through the merged hull mode and distance LOD (detailed models only within about 150 m, instanced armour everywhere, impostor beyond 1 km).

- **Never SE's picture inside the planet.** On respawn, on the respawn menu, after a teleport: the picture must be
 Subnautica's, never SE's while Subnautica catches up (a curtain while the window rebuilds, not the SE picture).
- **Blueprints.** Selecting a blueprint lags a lot; placing it waits a very long time or never spawns; the same for small
 ships and small-grid blueprints. The mirror build must never block SE's paste or the projection: incremental, off the
 game thread, with a budget.

- **Low fps and stutters** in general; measurements with tools (MSI Afterburner / RTSS allowed, downloads authorised),
 published in a **GitHub page** "requirements" section (the project needs a GitHub page: part of S10/release).

Placeholder: the showcase video and posts for the release; discussed when the slices are done.

### S8a addendum: landing gear clipping into the seabed
A landed grid's landing gear sits inside Subnautica's sand while SE rests it on the voxel surface (friend's PC,
The S4 provider tiles and Subnautica's mesh differ by decimetres on soft or sloped sand. S8a's
converter sets the surface fidelity; until then a per-tile height correction measured against the Subnautica mesh
at the site, or a surface offset in the tile sampler, is the small fix.

### S8a Stage D: drilling polish
6–0.8 s, a ship-drilled crater renders
right; while drilling the cells around the drill are see-through (meshes hidden on all four levels until rebuilt).
Deferred items, in `docs/S8A-BRIEF.md` "Stage D": keep the old mesh until the new one is built; level 0 immediately,
levels 1–3 ≤ 1/s; a live in-memory octree edit path for ship drilling with the file written in the background.

### S11 Creatures and hazards (extends S6; S6's proxies get a fix pass)
1. **Danger model.** Every leviathan very dangerous: a handheld gun cannot kill one; a big ship's turrets or a
 warhead can. Mechanism: the S6 damage mirror gets a weapon-class table (handheld, small turret, large turret,
 explosion) scaled against Subnautica's own creature health, instead of one scale for everything. **Stacked

 world and that nothing of ours breaks it, sum the yields for creature damage, and cluster the simultaneous
 explosions into one effect in Subnautica whose radius grows with the summed yield (cube root), bigger shockwave,
 bubble dome and sound, never dropped by the per-second effect cap.
2. **Grabs.** In Subnautica a Reaper grabs the Seamoth and chews it; the same for SE grids, **only for grids of the

 grid bounding size (Reaper: up to about a Seamoth's volume; Sea Dragon: larger), bigger grids get bitten and
 pushed, never held. The grid
 is held with its position driven from the creature's mouth bone (hold force like `DampenerHold`) and takes block
 damage per bite; released on the creature's release event. Damage amounts are the weapon-class and bite tables
 of point 1, a separate subject from the grab rule.
3. **Hitboxes (fix of S6).** Creatures still pass through ships and the character: the sectioned proxies exist (S6)
 audit which proxy layers Subnautica's creature avoidance and physics
 actually use, and the proxy density of large grids.
4. **Every Subnautica damage source reaches the player** (S6 mirrors creature bites only): lava and heat, acid brine,
 radiation (the Aurora), electric (Ampeel, Warper), poison gas, Crabsquid EMP (as SE power loss). Crush depth is
 not a creature hazard: it is S16. One table `DamageType -> SE damage per second and effect`, exactly once per event.
5. **Everything that heals or gives air works too**: Brain Coral bubbles, oxygen plants, air pockets and bases refill
 the SE suit O2; first-aid kit effects as SE health; Subnautica's food/water stays out (SE has no hunger).

### was v2 non-goal D13)
v1 set the Water Mod's `CrushDamage` to 0 and kept Subnautica's rule for the diver (no pressure damage, only faster
breathing: ×1.5 below 100 m, ×2 below 200 m, already S2b). S16 makes depth the progression axis, Subnautica's way:
1. **Hull classes with crush depths.** Every grid gets a crush depth from its weakest pressure-relevant block, with
 Subnautica's numbers as the ladder: light armour small grid about 200 m (Seamoth), heavy armour small grid 300 m,
 light armour large grid 500 m (Cyclops), heavy armour large grid 900 m; interior/non-armour blocks count only
 when exposed to water (the S7 air rule knows which rooms are wet).
2. **Pressure hull upgrades** as DeepEngineers blocks in the world mod (Data/CubeBlocks): three tiers raising the
 grid's crush depth to 900, 1300 and 1700 m like the Seamoth/Cyclops/Prawn depth modules, with a cost that uses the
 ores already in the planet; one per grid counts (the best).
3. **Damage below crush depth** through the Water Mod's own `CrushDamage` path where it fits, else the plugin: block
 damage per second scaling with the metres past the limit, hull creaks (Subnautica's Cyclops creak events) from
 50 m above the limit, the Cyclops "Hull integrity critical" voice at the limit (shared with S15's voice rule),
 warning on the HUD.
4. **HUD depth**: current depth, the grid's crush depth, and the diver's breathing multiplier, on the SE HUD (Text HUD
 API) and as Subnautica's own depth meter behaviour under water.
5. the S2b breathing
 multipliers stand.
6. **Oracle**: a scripted descent of a light small grid breaks blocks past 200 m and a tier-3 hull does not until
 1700 m

### S12 Interiors v2 (extends S7 interior look; the air rule gets a fix)
1. **Air next to a house (fix).** The air rule `AreaBuilt = airAbove || camBox` grants air near a pressurised room
 from outside: the room test must be "inside the room's oxygen volume", not the camera box.
2. **Lighting inside under water** barely differs from outside: an interior look preset (darker, cooler, no caustics,
 lamp-driven) when inside a pressurised room under water (the "interior look" item of the next-week list).
3. **Flooding and venting with sound and picture**: when a room loses pressure under water the water comes in as an
 animation (a rising water plane with Subnautica's flood shader and splash audio), venting plays air-vent sound and
 bubbles. Proposal: a DeepEngineers **air vent block** (world mod, Data/CubeBlocks) that is the visible source of the
 effect, so the scenario has an object.

### S13 Character and animation v2 (fixes of S5/S5a/S2b/S7p; T6 colour masks)
1. Subnautica's own hands appear under water and blend with the SE character (S5a arms rule leaks).
2. Near a surface the swim animation cancels and the engineer stands up as if in air: the swim state must hold while
 submerged whatever the ground contact (S2b surface rule).
3. Tool and gun animations break on equip; the tool skin is still red and SE skins, custom ones included, are not
 shown (T6 colour masks, tool paint).
4. Shadows become huge in the dark with the flashlight on (near-field light falloff and shadow bias; next-week list).
5. Third-person swimming: the body must move along its own facing, not the camera's, so the backwards animation and
 the actual motion agree (S2b third-person rule).
6. Swim animations while holding tools, guns and blocks (today: SE's bones while a tool is held).
7. **Placement ghost** like SE's: the real block mesh with a transparent green/red material instead of a box (S7p
 projection path).

A design-first slice, after S14 and before S10: one tool plays the role of the designer. It reads the roadmap, the

list of small things that add play or immersion (examples to start from, not the answer: creature sounds through the
hull scaled by block armour; Subnautica's PDA voice lines on first events, such as the first dive below 200 m or the
first leviathan; scanner-room style pings on a ship's sensor block; wildlife reacting to lights and thruster noise;
drift of small debris in thruster wash; a dive computer on the HUD; night-time bioluminescence dimming the SE lights
rule; Cyclops-style "hull integrity" warnings at crush depth; seabase-like ambience inside a pressurised room).
own entry: **the Cyclops AI voice on SE grids**, "Welcome aboard, captain" when entering a ship or a
house, polished: played by the Subnautica plugin from its own FMOD events (never shipped), once per grid entry with a
cooldown, only for grids with a cockpit or a pressurised room (not every door or ladder), at SE's volume rules; the
rest of the Cyclops set where it fits ("Engine powering up" on first thruster use, "Hull integrity" at crush depth,
"Silent running" when the lights go off, "Fire!" on block fire), each at most once per session unless the event
repeats after a long gap. the same tool then builds the picked items, a review checks. Estimate at launch.

- **One planet from orbit to the seabed.** "Even from 600 km, 200 km, 100 km we see that it's Subnautica's planet, and
 the transition between the two colours (SN/SE) is the same": the SE planet's look from space and from the air must
 be continuous with Subnautica's below 1 km: the atmosphere block (Rayleigh/Mie colours, fog), the cloud layers, the
 sun, and our own water material (`TransparentMaterials.sbc` in the world mod, our textures; the Water Mod's surface
 look is selectable per planet) matched to Subnautica's surface and fog colours measured the S7t T5 way at the

- **SE's real planets in Subnautica's sky.** Replace Subnautica's two moons with SE's actual planets and moons at
 their positions and apparent sizes (SE renders them in its own picture; Subnautica's picture must show the same
 sky): hide Subnautica's sky bodies (find the moon/planet sky objects next to the C6 sky pole we already drive),
 add discs/spheres for each SE planet from the world's planet list (positions, radii; our own generated textures or
 flat palettes, never SE's), lit by the same sun, so the sky is one universe from both sides.
- **Ships above the water seen from below.** From inside Subnautica (under water, looking up) grids above 1 km or in
 space are invisible because the S5 mirror has a range; S10's far-grid impostor (planned: "impostor beyond 1 km")
 covers it: a low-detail shell for every grid beyond the mirror range, a sprite beyond that.

### S14 Audio v2 (extends S7 audio polish)
1. The jetpack sound loops under water even with the jetpack off (mute the SE jetpack emitter while submerged).
2. Every SE sound under water sounds under water: guns, drills, block placement, welding, grinding through one
 underwater filter (low-pass and reverb; check what the Water Mod already applies and use its path if any).
3. Ship weapons fire under water (today disabled) with muffled sound and bubble tracers (S7p bullet-bubble path).
4. Thruster sound only in the left ear in third person (3D emitter or listener position bug) and a more pleasant
 thruster sound with a short ramp, since corrections near the seabed trigger it constantly.

## Sources

md`; `docs/CONTRACT.md`; `docs/ARCHITECTURE.md`; `docs/recon/{critic, gap-1
gap-2, probe-display-input, probe-world-mapping, probe-collision, probe-ship-render, prior-art, subnautica,
space-engineers, MODDING_PLAN-subnautica, MODDING_PLAN-space-engineers}.md`; `docs/install/subnautica-bepinex-files.txt`;
`tools/` (headers and usage of every script; `wire_ref.py` `vectors` and `selftest` use `worldmap_ref.EXAMPLE`);
`src/` and `tests/` (the carried-in prototypes), `build.ps1`.

Plugin method (universal-modder `2c2201437ca6`): `skills/mashup-mods/SKILL.md`; `skills/game-automation/SKILL.md`;
`knowledge/techniques/{bridge-contracts-ownership-units-and-lifecycle, oracles-how-agents-know-a-mod-works,
evidence-levels-for-mashup-claims, installers-load-order-and-loaded-code-checks}.md`; `um` CLI argument lists
(`um/win.py`, `um/backup.py`, `um/publish.py`, `um/kb.py`).

Checked for this roadmap on read-only (described, not copied):
- SE content: `Content\Data\Components.sbc` (SteelPlate 100, MetalGrid 30), `CubeBlocks\CubeBlocks_Armor.sbc` (armor
 components), `Stats.sbc` (Health max 100), `EntityComponents.sbc` (`MaxHealthBuff` levels 100…150),
 `Content\Models\Characters\Astronaut\SE_astronaut.mwm` (81 bones); `Bin64\eula.txt` (clauses 1.4–1.6, 2.1, 2.2.3,
 2.2.4).
- SE decompiled: `, `,
 `, `, `,
 `, `, `,
 ` (`GameplayFrameCounter`, `LoadingStep`, `Save` overloads, component order),
 ` (`RunLoad`), ` (`UpdateSound`),
 ` (`AddVoxelMap` entity id), `,
 `, `.
- Subnautica decompiled: `Assembly-CSharp\{IngameMenu, SaveLoadManager, Player, MeleeAttack, MainGameController,
 LiveMixin, PDACameraFOVControl, FreezeRigidbodyWhenFar}.cs`, `Assembly-CSharp-firstpass\{GraphicsUtil,
 UWE\FreezeTime}.cs`; grep for `IsInsideWalkable` readers and `OutOfBoundsWarp`.
- Files: `BepInEx\LogOutput.log`; `BepInEx\plugins\DeepEngineers\` listing; `BepInEx\core\0Harmony.dll` (2.9.0.0),
 `BepInEx.dll` (5.4.23.5); Pulsar `Libraries\Legacy\0Harmony.dll` (2.4.2.0), `README.md`, `Legacy\config.xml`,
 `Legacy\Profiles\Current.xml`, `Legacy\Sources\sources.xml`; `Subnautica_Data\Managed` module list; SE log
 `SpaceEngineers_20261011_194025789.log` (STATISTICS legend); scratchpad `measure.ps1`, `buildtest\{se,sn}` csproj
 files; the `wire_ref.py` selftest re-run with `SITE_V1` (scratch runner).
