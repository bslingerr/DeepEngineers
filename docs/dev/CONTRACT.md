
Status: v1, complete for implementation, revised after the review (log at the end of
`docs/ARCHITECTURE.md`). Layout 1.0 (§5, §6) is implemented and pinned by S0, which exited on real runs on (`docs/S0-STATUS. the S0 discrepancies of `S0-STATUS.md` §6 (ids D1–D46) are
applied where they touch this file. S1 (pose bridge) exited on real runs on (`docs/S1-STATUS.md` §10,
its measured numbers and the S1 discrepancies (D47–D108) that touch this file are applied, and the
S2 semantics of the C5/C7 fields are fixed in §5.4 ("C5/C7 semantics for S2") without any layout change (layout 1.0,
hash `0x4f3a35f6fc1eaf31`). S2b (swimming) adds §2.10, the SE plugin's swim model, with no wire change. S5a adds the fins modifier to §2.10 and the **C1 swim extension** of §5.4: 12 bytes inside the C1 pad
(0x94-0x9F), so layout 1.0 and its hash are unchanged (decision: pad bytes, no re-pin; the extension is pinned on its
own, hash `0x832ba93ca25bc307`). S5 keeps layout 1.0
too: its new meanings live in fields of existing records that layout 1.0 does not pin, and are pinned on their own
(§5.2 "Layout decision for S5", §5.4 "C1–C4 conventions for S5", hash `0x31dfd6f9befdef5d`, with the canonical
skeleton table `src/DeepEngineers.Wire/Data/SeSkeleton.canon1.tsv`). S6, S7a and S7 keep layout 1.0 too; their meanings are pinned on their own (§5.4 "S6 combat
conventions", "S7a conventions", "S7 conventions": `ext s7 look 1`, `0x1f9b9b4a876a97f9`). S8a keeps layout 1.0 as well
(§3.3, §5.4 "S8a conventions": `ext s8a terrain 6`, `0x3188251ab11e2fec`). Every number and record that crosses between the two games is here; the reasons
and mechanisms are in `docs/ARCHITECTURE.md`, the checks in `docs/ROADMAP.md`, the evidence in `docs/recon/`. This
file holds behaviour and numbers only: no game code, no game data. Template: universal-modder
`bridge-contracts-ownership-units-and-lifecycle.md`.

Labels: **VERIFIED**,
**REPORTED** (not checked locally), **HYPOTHESIS** (to be tested; the test is named). Values marked "chosen" are
design decisions, not measurements.

Change control: any change to a number in §2–§6 bumps the matching version (`wireVersion`, `siteVersion`,
md`. A peer with a different major version or a
different layout hash is rejected, never misread (§6).

---

## 0. Route and exact versions

live in-memory terrain conversion (SE reads Subnautica's octree files through a voxel provider) + OS composition of
two windows (DWM per-pixel alpha); no frame compositing, no pixels cross.**

| Role | Program | Exact build | Loader | OS |
|---|---|---|---|---|
NET Framework 4.8 x64) | 1.210.014, Steam 244850 build 24675677; run-time check `MyPerGameSettings.BasicGameInfo.GameVersion` = 1210014 (never the inlined `SE_VERSION` const) | Pulsar v2.4.3 `Legacy.exe` (Harmony 2.4.2) | Windows 11 x64 build 22000 |
| Visual and ecosystem host (underwater) | Subnautica (Unity 2019.4.36f1, Mono, D3D11) | Steam 264710 build 20241558, game version 1.22.83031; run-time check `SNUtils.GetPlasticChangeSetOfBuild(0)` = 83031, else `Application.version` = "1.22.83031": another build in a bridge launch keeps the plugin dormant with one notice and one `DE.Version REFUSE sn` line (`SnVersionGate`) | BepInEx 5.4.23.5 (Tobey's pack 5.4.23-pack.3.1.1); Nautilus 1.0.0-pre.54 installed but not used | same PC |
| Sea simulation in SE | Water Mod (Jakaria) | 3.23, Workshop 2200451495, API handler 50271, API version 22; pinned by `Jakaria.WaterData.Version` (reflection) + delegate key/type checks + behaviour probes; `VerifyVersion(22, "DeepEngineers")` only checks ≥ 19 | SE mod scripts | |
| Planet data | DeepEngineers.World (our local world mod) | site version 1 | SE mods | |
| Our plugins | DeepEngineers.SE (net48), DeepEngineers.SN (net472), `DeepEngineers.Wire.dll` (netstandard2.0) deployed next to each | wire version 1, layout 1.0, damage scale version 1 | | |

Our Water Mod API client is our own code written from the message-handler behaviour (handler id, delegate
dictionary keys and types, `VerifyVersion`), never a copy of the mod's `WaterModAPI.cs`. No translation layer (both
games are native Windows). Multiplayer: none; both plugins are inert in multiplayer.

---

## 1. Ownership

|---|---|---|---|
| Player movement, character physics, jetpack, swimming | SE (`MyCharacter`, Havok; underwater forces from the Water Mod; from S2b, in the water of `DE_4546B`, the SE plugin sets the character's velocity from the swim model of §2.10 instead of the Water Mod's character forces) | never handed over | n/a. Subnautica's `Player` is a locked-mode ghost moved by the plugin to the SE character's pelvis (or the camera) |
| Ship and grid physics | SE | never | n/a |
| Camera pose, FOV, near plane | SE (`MySector.MainCamera`) | always mirrored to Subnautica while the bridge is alive (C1) | n/a. Subnautica's `MainCameraControl` and `PDACameraFOVControl` stay disabled |
e. below the Approach altitude band, above water and under water alike (its sea surface, sky, weather, the mirrored grids, astronaut and placement ghost); SE only in Space (above the band, beyond 7,900 m, off the site, bridge down). SE's HUD and GUI stay on top. The waterline no longer swaps pictures (it still switches the audio mix, oxygen, the arms and the swim model). Policy `Waterline` (S2's rule: SE above water and in space, Subnautica underwater) stays selectable as the fallback (`de.s7 picture waterline`, env `DEEPENGINEERS_PICTURE=waterline`) | `Site`: the picture latch turns on in Approach (Track or Prime) held since an earlier tick, below +300 m (the band itself, also when Approach was entered earlier by predicted arrival), inside 7,800 m, with `snReady`, `zOrderOk`, picture dwell ≥ 0.25 s, `presentedSeTick ≥ bandTick` (the SE tick Approach was entered) and, in design F, the margins-on request presented and applied (ARCHITECTURE §4.2 "Picture policy (S7a)"); the swap is softened by the crossing fade and by the time-of-day sync. `Waterline`: view state enters Underwater (ARCHITECTURE §4, thresholds in §4.2: `y ≤ −0.20 m`, zone ≤ 7,800 m, `snReady`, `zOrderOk`, `presentedSeTick ≥ crossingTick`, dwell ≥ 0.25 s, from Approach/Prime) | `Site`: Approach → Space (`y > +350 m` unless arrival is predicted within 8 s, zone > 7,900 m, leaving the site, bridge or peer lost), or a teleport/new generation/link recovery while `snReady` is false (SE's picture until Subnautica has re-streamed), all within one SE frame. `Waterline`: Surfacing: `y > −0.05 m`, zone > 7,900 m, leaving the site, teleport before re-streaming, or peer stalled/exited (within one SE frame) |
| HUD, menus, chat, GUI screens | SE always (its sprites are drawn over Subnautica in design F) | never | n/a (fallback A′ only: SE raises its window while a menu is open). Subnautica's own HUD, PDA voice, subtitles and notifications are hidden |
| Input focus (keyboard, mouse, joystick, text, IME) | SE always | never | n/a. Subnautica is `WS_EX_NOACTIVATE`, never foreground, its input cleared every frame |
| Window z-order | SE plugin: `AllowSetForegroundWindow(snPid)` then `SetWindowPos(snHwnd, seHwnd, …, SWP_ASYNCWINDOWPOS)` (the only cross-process window call), verified by the z-order walker | before Prime, after each SE re-activation | n/a |
| SE's own window (styles, margins, TopMost, foreground) | SE's render thread (calls queued by the update thread) | while linked | undone on unlink or PeerExited |
| Audio mix state | SE decides (C5); each side applies it to its own engine (the audio table: §5.4 "C5/C7 semantics for S2") | view state, pause, foreground | follows the state |
| Pause | SE (ESC menu, Steam overlay, medical screen) | `MySandboxGame.Begin(FeedbackPanel)` on a change | `End(FeedbackPanel)` only when the whole condition becomes false. Subnautica never pauses SE |
| Time of day | SE (world settings `SunRotationIntervalMinutes` 20, sun base direction (0, 0, 1); world-wide) | always (from S7a: SE writes C6 every tick; `de.s7 time sync off` hands the clock back to Subnautica) | Subnautica's `DayNightCycle` is paused every frame and driven forward from C6, and `uSkyManager.SunDirection`/`NorthPoleOffset` follow the C6 sky pole, so the sun disk stands where SE's does (§5.4 "S7a conventions") |
| Weather | SE (no weather generators on `DE_4546B` in v1) | n/a | n/a |
| SE grids, characters, floating objects | SE; Subnautica draws mirrors | entity within visibility + 50 m of the camera on the dive site | mirror freed 60 s after the entity leaves; all mirrors cleared on world epoch change |
| Seabed shape | Subnautica's data (the player's own octree files) inside the crater; **S8a:** beyond the crater box SE's planet (our sculpted heightmaps), converted into Subnautica batches by SE (§3.3) | n/a | n/a |
| Seabed collision for SE (terrain → SE) | SE, natively, through `SnTerrainProvider` tiles (no runtime traffic); own gate, independent of the display bridge | tiles created in `BeforeStart` each session | tiles are never saved; their deterministic ids may appear in saves as landing-gear references; fail-safe when they cannot be created (§7) |
| Seabed rendering | Subnautica | Underwater (from S7a, `Site` policy: whenever Subnautica's picture is shown) | Surfacing (`Site`: the picture latch off) |
| Terrain edits | nobody (blocked on our tiles in v1); **S8a (`DrillOn`):** SE (its own octree leaves over the provider tiles, persisted as `DE_S8a_overlay.vx2`; planet edits as SE saves them); Subnautica draws the converted batches from the store (§3.3) | a `RangeChanged` on a tile or the planet | `DrillOn` off: S4's guards |
| Static structure collision (Subnautica statics → SE) | Subnautica reports primitives (C12); SE owns the static bodies on non-saved host entities (S7: one `HkStaticCompoundShape` host per batch and epoch, layer 13, `Save = false`; §5.4 "S7 conventions") | a 160 m batch streamed by Subnautica (or from the census cache) while a grid or character is within 300 m | body removed beyond 350 m, on epoch change, world unload or a new Subnautica generation |
| SE grids as obstacles and targets in Subnautica (SE → Subnautica collision) | SE sends skins (C4); Subnautica owns kinematic proxy sections, including the character section | grid or swimming character mirrored | proxy destroyed with the mirror |
| Creatures (AI, spawning, ecology, movement) | Subnautica | never | n/a |
| Creature contact with proxies | Subnautica PhysX (50 Hz while moving proxies are visible) pushes creatures out of kinematic proxies; ships are never pushed in v1 | n/a | ram impulses: v2 |
| Damage detection on SE entities | Subnautica (postfix on `LiveMixin.TakeDamage` for marked proxy sections and the ghost Player) | every hit → one C8 record | n/a |
| Damage application and health (characters and blocks) | SE, authoritative (§4.2) | validated C8 record → `MyCharacter.DoDamage` / `MySlimBlock.DoDamage` | Subnautica health never changes |
| Death and respawn | SE | SE character dies → C1 flag `characterDead`: Subnautica hides the avatar and disables the character section | SE respawn → new character id → new mirror |
| Inventory, building, power, oxygen, food, survival | SE | never | Subnautica's equivalents unused; ghost stats frozen (`Player.FreezeStats`) |
| Water simulation (buoyancy, drag, drowning, pressure) | SE + Water Mod; crush off on `DE_4546B`; abyss rule and floor guard by the SE plugin; from S2b the character's suit oxygen in the water of `DE_4546B` follows the swim model's oxygen rule (§2.10), and the character takes no crush damage there (Subnautica's diver has none) | n/a | n/a |
| Underwater visuals (fog, caustics, light, sky and surface from below) | Subnautica | Underwater | Surfacing |
| Looks and sounds of SE actions **(S7)**: thrusters, weapons, explosions, tools, grid impacts, hull breaks, splashes, SE's lights | SE keeps the gameplay (forces, damage, tools) and raises event records and light rows (C2, §5.4 "S7 conventions"); Subnautica draws them and plays their sounds with its own prefabs and FMOD events, found at run time (never shipped); under water the full look and sound, above water the lights only | every SE tick inside the site after the hello ack | n/a (an effect fades out when its rows stop) |
| Dry interiors **(S7)**: the camera inside an airtight SE room | SE decides (`MyGridGasSystem` rooms; C1 flag bit 3 and the interior extension); Subnautica applies it (`IsInsideWalkable`, the base-interior sky, clip proxies); SE's swim model stays off inside (the swim gate) | the camera's (or the character's eye) cell in an airtight room | out of the room, a breach, or the world settings off |
| Above-water visuals inside the site (sea surface and waves, sky, sun, clouds, weather, islands) **(S7a)** | Subnautica (`Site` policy); Accepted gaps while the latch is on: symmetry and multi-block placement previews (only the default gizmo is mirrored); above water, the full looks of SE's thruster flames, weapons, tools and explosions (S7 draws SE's lights and a light flash or glow for those events above water, the full look and sound under water); grids are mirrored within 1,500 m above water (S7a radii; 250 m under water) |
| Biome music and ambience | Subnautica (underwater only) | Underwater | Surfacing (300 ms crossfade) |
| Saves | SE (normal and autosave) | n/a | Subnautica's host save is never saved |
| Subnautica session (scene, streaming, eco-regions) | SN plugin (Freedom host save "DeepEngineers-Host": slots are `slotNNNN` and cannot be renamed, so the host save is the slot whose `gameinfo.json` session id is the configured one, on this PC `slot0000`; S1, D72), bridge mode only with the launcher's token | launcher start | quits when the SE process exits; never on heartbeat loss |
| Loading screens | SE's own; Subnautica idles | world load (`sePhase = LoadingWorld`, `worldEpoch` bump) | first tick in world |
| Site record and all map math | SE plugin, doubles | n/a | n/a |
| SE cockpits, seats, cryo pods, ladders ("vehicles and furniture") | SE | never | Subnautica only receives the camera pose and the "inside airtight room" flag |
| Subnautica scripted events (Aurora, Sunbeam, precursor) | Subnautica's own timeline, uncontrolled in v1 | n/a | see §8 |
| Process lifetime | launcher starts both (Subnautica first); the SE process's exit ends Subnautica; Subnautica's death leaves SE running | n/a | the player restarts the renderer from the launcher (no automatic relaunch) |

### 1.1 Settings touched (both sides)

No persisted setting of either game, of the Water Mod or of Windows is changed by the bridge. Runtime changes are
listed with their restore point. `restore.json` lives in `%LOCALAPPDATA%\DeepEngineers\`. Rows marked **(S2)** are
first applied by slice S2 (display, input, audio); the others exist since S0 or S1.

|---|---|---|---|---|---|
| Water Mod underwater sound **(S2)** | Water Mod | n/a | Underwater; preferred path: the mod's emitters scaled to 0 in memory by a postfix on its sound component (O10 decides whether that path works) | surfacing, PeerStalled/PeerExited | preferred path changes no setting (emitters scaled in memory; nothing to restore) |
| Water Mod `Settings.Volume` **(S2, fallback path only**, if O10 rejects the emitter path; `WaterClientSettings.xml` in SE global storage, shared by all worlds, written on every save; byte-identical before and after every session, S2.T-A1) | Water Mod / player | the file at plugin `Init`, copied to `restore.json` before the first change | in memory, Underwater; never through `/wvolume` (it saves at once) | before every SE save (prefix on `MySession.Save(out MySessionSnapshot, …)`, 0 re-applied by a finalizer), surfacing, PeerStalled/Exited | `restore.json` applied at the next plugin `Init`; S8: the launcher backs the file's `<Volume>` up at its first run and, at the next clean start after an unclean end or with `restore. `launcher.py restore-settings --yes` restores it from the backup |
| SE `MyAudio.Static.VolumeGame/VolumeMusic/VolumeHud` **(S2)** | SE | `Config.GameVolume`, `Config.MusicVolume` | in memory only (`MyAudio.Static`), written in a postfix on `MySandboxGame.UpdateSound` only while focused, unpaused, no GUI screen other than gameplay open, not loading (C5 `seMixActive`, `guiScreenOpen`; audio table §5.4) | every tick from config; a prefix on `MyGuiScreenOptionsAudio.Save` restores the config-derived values before SE persists them | none needed: `SpaceEngineers.cfg` is never written by us (S8.N6 compares it); S8: the launcher backs `GameVolume`, `Music_Volume` and `VoiceChatVolume` up at its first run, snapshots them before every session, restores at the next clean start only a value an unclean end can have left (0.7 × / 0 × the pre-session value) and reports any other difference; `restore-settings --yes` restores them from the backup |
| SE window TopMost, z-order, DWM margins, foreground **(S2)** | SE | `MyGameWindow` mode; margins 0 | while linked and the activation gate is not refused (§1.2: the gate is evaluated at the first InWorld tick, so the window set-up starts at link-up, normally at SE's main menu, and a refusal undoes it); SE takes the foreground itself at link-up (§7 Start); margins in Prime/Underwater/Surfacing (S7a `Site` policy: in every view state but Space, see §5.4 "C5/C7 semantics for S7a"); every call on `seHwnd` runs on SE's render thread | unlink, PeerExited, leaving Prime (`Site`: entering Space), gate refused | process exit |
| SE window `WS_EX_LAYERED` + constant alpha (`SetLayeredWindowAttributes(LWA_ALPHA)`) **** | SE | not layered | during an A′ crossfade ramp (§5.4 "S7t crossfade"), SE's render thread | the ramp's end (lowered, or back at SE's picture), the switch or the design changing | process exit (runtime window style) |
| Subnautica `QualitySettings.vSyncCount`, `Application.targetFrameRate` **(S2)** | Subnautica options (`Graphics/VSync`, `Graphics/FrameRate`) | values at load | per C5 `perfMode`/`targetFps` (§5.4 "C5/C7 semantics for S2"), set directly and re-asserted every frame; the applied pair is reported in C7 | quit | never persisted (no `GameSettings.SaveAsync`) |
| Subnautica `MiscSettings.runInBackground` | options `Misc/RunInBackground` | value at load (logged) | forced true in memory before the focus hand-over (S1: at world set-up, before `ReadyIdle`, D75; VERIFIED real run S1.N11, and S1.M8 red without it) | quit | never persisted |
| Subnautica FOV (`SNCameraRoot.SetFov`), `PDACameraFOVControl.enabled` | options `Graphics/FOV` | `MiscSettings.fieldOfView` | bridge mode | quit | `MiscSettings.fieldOfView` never written |
| Subnautica near plane: `SNCameraRoot.minNearClip` lowered to SE's near (C1 `near`, ≤ 4 m; often below Subnautica's 0.2 default, whose options tooltip warns of shadow flicker below 0.3), then `SetNearClip(near)` | Subnautica (`SNCameraRoot` field, 0.2) | value at load | bridge mode, on each C1 `near` change | quit | runtime only (a scene field, never persisted) |
| Ghost `Player.liveMixin.shielded` = true (S1; the damage capture of S6 replaces it, §4.2) | Subnautica (`LiveMixin` field) | false | world set-up, re-asserted every second | quit | runtime only (host save never saved) |
| Subnautica clipmap quality | options | value at load | Medium, in memory | quit | never persisted |
| Subnautica window styles, size, monitor **(S2)** | Unity / player; `Screen.SetResolution` persists the `Screenmanager` keys under `HKCU\Software\Unknown Worlds\Subnautica` | registry values backed up by the launcher at its first run | at `ReadyIdle`: hidden, `WS_EX_NOACTIVATE \| WS_EX_TOOLWINDOW` added, shown without activation, `HWND_BOTTOM`; re-applied after every resolution or display change; rect correction (`Screen.SetResolution`) only when C7 `rectOk` fails | styles: quit; registry: the launcher restores changed keys from its backup | launcher compares at the next preflight; `restore-registry --yes` or `restore-settings --yes` writes the backup back |
| Subnautica process priority class **(S2)** | Windows, per process | the process default (Normal) | BelowNormal while the Idle recipe is applied (C5 `perfMode` 0), Normal otherwise | quit | none needed: process-level, never persisted |
| Subnautica main camera (`MainCamera.camera.enabled`) and GUI camera **(S2)** | Subnautica scene | enabled | disabled in the Idle and Track recipes (`MainCamera.camera` stays valid, streaming follows its transform; S7a `Site` policy never requests Track: Idle in Space, Full elsewhere) | Full recipe, quit | runtime only |
| Subnautica `DayNightCycle` speed and `uSkyManager.SunDirection`/`NorthPoleOffset` **(S7a)** | Subnautica scene (`_dayNightSpeed` 1; the scene's sky angles) | values at the first apply (logged) | while C6 says time sync valid: `Pause` re-asserted every frame, `timePassedAsDouble` advanced forward only, the two sky angles written every frame (§5.4 "S7a conventions") | C6 sync off, PeerStalled/Exited (SE's view), quit: `Resume` and the logged sky angles back | runtime only (host save never saved) |
| Subnautica freeze and mix **(S2)**: `UWE.FreezeTime.MuteAllEvents` and the `bus:/` volume | Subnautica | not frozen; bus volume at load | freeze = Idle ∨ Paused ∨ SE stalled or exited; mute and bus volume per C5 `snBusTarget`/`crossfadeMs` (§5.4) | quit | runtime only (host save never saved) |
| Subnautica `Time.fixedDeltaTime` | Subnautica (`MainGameController`) | 0.1 s | 0.02 s while moving proxies are visible (registered behaviour) | unregister | runtime only |
| Subnautica HUD hide phase, PDA voice, radiation FX, `DayNightCycle` speed | Subnautica | game defaults | bridge mode | quit | runtime only (host save never saved) |
| Pulsar profile `Legacy\Profiles\Current.ps1` deploy | uninstall (`docs/install/UNINSTALL.md`) | n/a |
S7: `tools/lab/set_world_settings. the plugins never write them) |
| Subnautica `Player.IsInsideWalkable` result **(S7)** | Subnautica | n/a (a Harmony postfix on the result; no field written) | while the newest C1 (≤ 100 ms) says the camera is in an airtight room | the first frame without it; quit | runtime only |
| SE emitter effect `MyEntity3DSoundEmitter.SelectEffect` result **(S7, audio A1)** | SE | n/a (postfix on the result) | the plugin's underwater SE mix in effect (`seMixActive`) and the camera not in an airtight room | the next effect selection after it ends (looping sounds re-select every update) | runtime only |
| Windows power throttling of the game process (opt-out with `SetProcessInformation(ProcessPowerThrottling)`; VERIFIED supported on build 22000, both plugins logged `ok 0x5` in the S0 real run) | Windows, per process | the process default | SE: at plugin `Init` in every session (not in `DEEPENGINEERS_SE_MODE=measure`), before the activation gate can be known; SN: at bridge start, bridge mode only | process exit | none needed: process-level, never persisted |
| Windows ghost-window replacement (`DisableProcessWindowsGhosting`) | Windows, per process | enabled | SN: at bridge start, bridge mode only, after the bridge token and the mutex pass | process exit | none needed: process-level, never persisted |
| Subnautica `Physics.autoSyncTransforms` | Subnautica (false in game) | value before the call | switched off during the WM-1 self-test call only (§2.9) | end of that call | runtime only |
| SE character jetpack and inertia dampeners **(S2b)** | SE / player | the states at the swim entry (`DE.Swim enter jetpackWas/dampenersWas`) | in the water of `DE_4546B` (swim latch on): the jetpack off whenever the head is under (§2.10 jetpack latch), the dampeners off while the latch is on; queued and switched by the plugin Update after the tick (`TurnOnJetpack`, `EnableDampeners`) | latch exit: surface, grounded, model off, left the water → both back; jetpack exit (flown out) → dampeners back, the jetpack stays on; seated → dampeners back, the jetpack **parked**: leaving the seat in the water the swim goes on with the remembered states, out of the water the remembered jetpack and dampeners are set after the cockpit's restore; dead or another character → nothing | a save taken while the swim policy holds them (swimming, or parked in a seat; SE's autosave included) stores the remembered states: a postfix on `MyCharacterJetpackComponent.GetObjectBuilder(MyObjectBuilder_Character)` writes `JetpackEnabled` = remembered ∨ current and `DampenersEnabled` = remembered. Residual: a save while seated in a cockpit after a swim keeps the cockpit's own copy, the swim's off, which the cockpit restores when the pilot leaves after a reload |
| SE suit oxygen consumption `MyCharacterOxygenComponent.OxygenConsumptionRateMutliplier` **(S2b)** | SE (runtime property, default 1; not saved; otherwise set only by `MyOxygenConsumptionRateBuff`) | the value at the first change of a swim | in the water while the oxygen rule is in force (§2.10): set so the suit loses Subnautica's 1 / 1.5 / 2 units per second of a 45-unit tank (17.64 for the default engineer at the safe depth) | every latch exit except dead or another character; S2b.M4 restores it at once | none: runtime only |
| SE suit oxygen amount `SuitOxygenAmount` **(S2b)** | SE (the suit's gas, saved with the character) | n/a | refill: eye above −1 m in the water, Subnautica's 30 units/s of 45 (2/3 of the capacity per second), added every 10 ticks | n/a (a refill, like SE's own oxygen sources) | n/a |
| SE render copies of the post-process, environment light, fog and planet settings **** | SE (`MyPostprocessSettingsWrapper.Settings`, `MySector.SunProperties/FogProperties/PlanetProperties`) | the values SE sends each frame | the copies `MyGuiScreenGamePlay.Draw` hands `MyRenderProxy` (`UpdateRenderEnvironment`, `UpdateFogSettings`, `UpdatePlanetSettings`: locals; `SwitchPostprocessSettings`: the static by ref, put back exactly by a finalizer after the message took its copy), blended by the look-match weight (§3.4) | every frame (weight 0: SE's own values pass unchanged): the picture off, peer lost, under water, off the site, above the band's ramp | none needed: no `MySector` field, no definition and no config is written (`MySector.GetEnvironmentSettings` and `SaveEnvironmentDefinition` see SE's own) |
| SE planet atmosphere on the renderer **(S7t T5)** | SE (DE_4546B's definition, `MyPlanet.AtmosphereSettings`) | `MyPlanet.AtmosphereSettings` (read only) | only when an atmosphere key of `de.s7t look` is set: the blended settings sent through the planet's render component (`MyRenderComponentPlanet.UpdateAtmosphereSettings`), ≤ 1 per 10 frames | the planet's own settings sent back when the keys clear or the weight returns to 0; the render object dies with the world | none needed: `MyPlanet.AtmosphereSettings` (saved with the world) is never written, and the load reads the definition |
| Subnautica main camera far plane (`SNCameraRoot.SetFarPlaneDistance`: main, GUI and IMGUI cameras) and, only with `fogMul` ≠ 1, uSky's global `_SkyFogDensity` **** | Subnautica scene | the far plane read once per `SNCameraRoot` instance (1,700 m in 1.22.83031); uSky rewrites `_SkyFogDensity` itself every Update | above +2 m under `pictureSite`: the horizon distance of SE's sea (1,700…20,000 m; ≥ 6,000 m while impostors exist), set when it moves ≥ 0.5 m; `_SkyFogDensity` = uSky's own × `fogMul` at the main camera's pre-cull (§5.4 "S7t altitude") | below +2 m, `de.s7t horizon off`, the `Waterline` policy, an exception, the bridge's teardown and quit; a new world brings the prefab's value | runtime only |
| Subnautica sun `Light.shadowStrength`, `uSkyLight.ambientLight`/`SunIntensity`, `uSkyManager.Exposure` **(S7t T5)** | Subnautica scene | the values captured before the first write (a change by the game while held becomes the new own value) | above the surface (weight > 0 from +20 m), SE Alive, C5 fresh, `pictureSite` (§3.4) | weight back to 0 (surface, under water, picture off, SE lost), quit (`BeginQuit`, application quit), a scene change (forgotten with the objects) | runtime only (host save never saved) |

Decision (S0, D29/D30): the power-throttling opt-out at SE `Init` is allowed in every session, including one whose
activation gate later closes (§1.2). It is process-level, it ends with the process, and it changes no game, mod or
Windows setting; the gate is evaluated only once a world is in play, long after `Init`.

### 1.2 Activation gates

| Side | Gate | Effect when closed |
|---|---|---|
| SE plugin, global | offline single player (S0: `MySession.OnlineMode` OFFLINE, no `MyMultiplayer.Static`, `Sync.IsServer`, not a dedicated server) ∧ exactly one `DE_4546B` planet in the world (from S1, with the site binding); evaluated once per world epoch at the first InWorld tick | no window, audio or render change, and no process change other than the power-throttling opt-out already made at `Init` (§1.1); patch bodies inert; `DE.Gate REFUSE <reason>` once (multiplayer: always refused). S0 behaviour: the link stays open; the header heartbeat, `sePhase` (S0's only patches, the phase prefixes, keep running) and C5 are still written, with `seFlags.gateOpen` 0 and `helloEpoch` 0 (no hello). Whether a refused session should drop the link instead is open (O17). A dedicated server leaves the plugin fully inert from `Init` (no link, no patches) |
| SE terrain | evaluated once per world load in the session component's `BeforeStart` (game thread, after `MyEntities.Load` and after the site binding's planet search, before the first simulation step), independent of the display bridge, the version pins of §0 other than SE's, the Water Mod, the site validation, Subnautica and the section; the tiles need only C. In order: (1) the global gate's predicate (offline single player, the conditions of the row above, evaluated early); (2) exactly one `DE_4546B` (`SiteBinding.PlanetCount`); (3) the source manifest (§3.1); (4) id 45460 registered to `SnTerrainProvider` (§3.1); (5) every SE member the tiles need resolved (edit guards excluded: a missing guard only logs, §3.1); (6) the 34 entity ids free (`MyEntityIdentifier.ExistsById`); (7) all 34 `AddVoxelMap` calls return a map, materials resolved. All or nothing: `DE.Terrain OK tiles=34 siteVersion=1 …` | (1) fails: no tiles, no fail-safe (`DE.Terrain SKIP multiplayer`; §8). (2) finds 0: not a DE world, nothing (`DE.Terrain SKIP no DE_4546B`); finds ≥ 2: nothing, not protected (`DE.Terrain SKIP planets=<n>`; §8). (3)–(7) fail: `DE.Terrain REFUSE <condition> <reason>`, no tile (any tile already created is closed), `seFlags.terrainUp` 0, fail-safe (§7) with `seFlags.failSafeActive` 1. Another SE version only logs `DE.Terrain WARN version` |
| SE display bridge | terrain-independent: link up ∧ §6 validation ∧ site validation (§3) ∧ version pins (§0) | Water Mod view only; never see-through |
| SN plugin | bridge token (`DEEPENGINEERS_BRIDGE=1` or the launcher's named mutex `Local\DeepEngineers_v1_launcher`) ∧ mutex `Local\DeepEngineers_v1_sn` free ∧ §6 creator rules pass, checked in this order at the plugin's first player-loop `Update`, never in `Awake` (§7) | dormant: vanilla Subnautica, no section, no window, option or save change; ghosting and power throttling are changed only after the token and the mutex pass |

Implementation note. The "exactly one `DE_4546B`" condition is implemented as a
**site** check, not in the global gate: 0 or 2+ planets give `DE.Site REFUSE` (display bridge refused,
`displayRefused`, no hello), and the global gate keeps the S0 conditions. The effect on the display is the one this
table requires (no picture change, no hello). For S2 this means: SE's window set-up (§1.1 "SE window" row) follows the
link and the global gate, while everything the display bridge does (margins, scene skip, alpha fill, the SE audio mix,
the Water Mod mute, any view state other than Space) needs the display bridge row above, which from S1 includes a bound
site and an acknowledged hello. In a world without the planet (S1.N12 "Lost Colony": `REFUSE API not received`, no
hello) the view state therefore stays Space and Subnautica stays Idle.

---

## 2. Units and axes

### 2.1 Units

| Quantity | SE | Subnautica | Ratio on the wire |
|---|---|---|---|
| Length | metre: `MyVoxelConstants.VOXEL_SIZE_IN_METRES = 1`, grid cells 2.5 m / 0.5 m, `MyGravityProviderSystem.G = 9.81` m/s² | metre: 1 voxel per Unity unit (`WorldStreaming.Rasterizer`, world 4,096 × 3,200 × 4,096 voxels in `Build18\index.txt`), `PhysicsManager` gravity (0, −9.8, 0), depth HUD from `Ocean.GetDepthOf` in the same unit (VERIFIED) | **1: 1, measured** (both games' voxel size and gravity), not chosen for feel |
| Horizontal scale of the map | | | `s = Rs/r`: 0.9942 at the top of the Approach band (+350 m), 0.9974 at the highest island top, 1.0284 at the deepest floor (−1,657 m), 1.0345 at the abyss limit (−2,000 m), 1.0526 at SE's floor (−3,000 m). Geometry of the map, not a unit ratio; computed by SE, applied by Subnautica. Published error budgets and test thresholds are sized at −2,000 m, the normal play limit |
| Altitude | SN `y = \|P − C\| − Rs` (doubles); `WaterModAPI.GetDepth` returns this same value, negative underwater | `y` (Unity world); `Ocean.GetDepthOf` returns `−y` (VERIFIED real run, S1.WM-2: 150.00 at `y` −150) | one symbol, `y`; "depth" in logs = `−y` |
| Angles | radians (VRage) | degrees only for `Camera.fieldOfView` | FOV: SE vertical FOV in radians × 180/π = Unity vertical `fieldOfView` (both vertical, VERIFIED) |
| Time | ticks of 1/60 s (`MyEngineConstants.UPDATE_STEPS_PER_SECOND = 60`); sun period `SunRotationIntervalMinutes` × 60 s | seconds; `DayNightCycle` day 1,200 s | SE interval 20 min = 1,200 s, so one SE day = one Subnautica day |
| Clocks | `GetTickCount64` (heartbeats, ms) and `QueryPerformanceCounter` (tick stamps) on both sides; QPC frequency written in the header and checked equal. On the Unity side every bridge timer runs from `Update`/`LateUpdate` on QPC or `Time.realtimeSinceStartup`/`unscaledDeltaTime`, never on scaled time, `FixedUpdate`, `WaitForSeconds` or `Invoke` (`FreezeTime` sets `Time.timeScale = 0`) | | |
| Physics step | SE 1/60 s | 0.1 s by default; 0.02 s while the bridge is registered as a high-precision behaviour (moving proxies visible, §2.6) | thresholds per step name the step |
| Health and damage | §4 (separate lines) | | |

### 2.2 Spaces

| Space | Handedness | Units, type | Who uses it |
|---|---|---|---|
| `SE-world` | right-handed, Y up, Forward = −Z | metres, double | SE plugin only |
| `SE-local(e)` | right-handed (an entity's model or grid space) | metres, float on the wire | wire, entity-local data, **both directions** |
| `SN-world` | left-handed (Unity), Y up, sea at y = 0 | metres, float on the wire | wire, world-level data, **both directions** |
| `Unity-local(e)` | left-handed, = `Mirror(SE-local)` | metres, float | Subnautica plugin only, never on the wire |

Axis map at the anchor (site v1): SE E0 = (−1, 0, 0) → SN +X (east); SE U0 = (0, 0, 1) → SN +Y (up); SE N0 =
(0, −1, 0) → SN +Z (north). The map's direction transform has determinant −1 everywhere (VERIFIED numerically,
`wire_ref.py selftest`, max −0.9953): the handedness change of world-level data is inside the map. For entity-local
data the handedness change is the declared mirror, which flips SE-local Z (SE Backward becomes Unity −Z, so SE Forward
becomes Unity +Z). The SE sun rotation axis (world +Y) maps to SN −Z: the site lies on the sun's equator.

### 2.3 Shared functions (the contract is executable)

Reference implementations (stdlib Python, independent of the C# code): `tools/worldmap/worldmap_ref.py` (the map)
and `tools/worldmap/wire_ref.py` (the wire conventions), with the site constants of `tools/worldmap/site_v1.py`. The
C# copy is `WireMath.cs` in `DeepEngineers.Wire.dll`, loaded by both plugins.

|---|---|---|
| `F(P)`, `F⁻¹(Q)` (points) | SE plugin, doubles | `worldmap_ref.Site.se_to_sn` / `sn_to_se`: `v = P − C`; `le, lu, ln` = components on E0, U0, N0; `θ = atan2(√(le² + ln²), lu)`; `(x, z) = Rs·θ·(le, ln)/√(le² + ln²)`; `y = \|v\| − Rs`; then yaw ψ and offset O (both 0 in site v1). Inverse closed form |
| `mapdir_P(w)`, inverse | SE plugin | `Site.dir_se_to_sn` / `dir_sn_to_se`: the anchor frame carried along the great circle to P, `w → (w·E_P, w·U_P, w·N_P)` |
| `pose(M, P*) → (o, q, s)` | SE plugin | `s = Rs/\|P* − C\|`; `o = F(P*) + J(P*)·(M.T − P*)` with `J(P*)·w = S·mapdir_{P*}(w)`, `S = diag(s, 1, s)`; `q = LookRotation(mapdir_{P*}(M.Forward), mapdir_{P*}(M.Up))` built by our own code (Unity's `LookRotation` is native), `w ≥ 0` |
| `camera(M) → (p, q)` | SE plugin | `p = F(M.T)` exactly; `q` as above at `M.T`; **no `s`**; FOV = SE vertical FOV in degrees |
| `vel(v)` | SE plugin | `S·mapdir_{P*}(v)` |
| `angvel(ω)` | SE plugin | `−mapdir_{P*}(ω)` (pseudovector under an improper map; Unity's numeric `AngleAxis` sense) |
| `staticAxes(q)` | SE plugin | SE-local axes of a Subnautica static primitive at its converted centre: `(mapdir⁻¹(q·x̂), mapdir⁻¹(q·ŷ), mapdir⁻¹(q·(−ẑ)))`, determinant +1 asserted (else the primitive is dropped and counted). S7 (§5.4 "S7 conventions", `format statics.*`): the centre `F⁻¹(c)`; box half extents `half_i · sqrt((a_i.x² + a_i.z²)/s² + a_i.y²)` with `a_i = q·ê_i` (SN) and `s = Rs/r` at the centre (horizontal sizes divided by `s`, shear dropped, error at most `(s − 1)·half` for s ≥ 1); capsule end points `F⁻¹(c ∓ a·h)` exactly, radius `r/s`; sphere `r/s`; reference `tools/worldmap/s7_ref.py` |
| `Mirror(v)` | SN plugin | `(x, y, −z)`; its own inverse |
| `MirrorTri(I0, I1, I2)` | SN plugin | `(I0, I2, I1)` |
| `Apply(o, q, s, l)` | SN plugin | `o + S·(q·Mirror(l))`, realised by the Frame/Entity hierarchy (§2.5) |
| `ToLocal(o, q, s, w)` | SN plugin | `Mirror(q⁻¹·S⁻¹·(w − o))` |
| `K(q, s)·v` | SN plugin | `q⁻¹·S·(q·v)` = `s·v − (s − 1)·u·(u·v)` with `u = q⁻¹·ŷ`: scales the part of `v` that is horizontal in SN by `s` |

Rules:
- **Compose in SE space, convert once at the boundary.** The SE plugin multiplies SE matrices (grid × part,
 character × bone) in SE space, then maps the world result. The SN plugin multiplies SE-local matrices in SE space
 (part matrix × model vertex), then mirrors the result. Quaternions and Euler angles are never converted
 component-wise.
- **No negative scale** on any Unity transform. The camera is never scaled.
- **Rotations** travel as Unity quaternions `(x, y, z, w)` with `w ≥ 0`; receivers compare them as rotations
 (`|dot| ≥ 1 − ε`).
- **Uniform entity scale** `k` (SE `PositionComp.Scale`, default 1) is sent separately and applied on the Entity child
 as `(k, k, k)`. Forward and Up are normalised before `q` is built.
- **SE matrix rows** on the wire: Right, Up, Backward, Translation (VRage `M11…M43`), 12 floats, row-major;
 `Forward = −Backward`; an SE-local point `l` goes to `l.x·Right + l.y·Up + l.z·Backward + T`.

### 2.4 Pivot policy (SE side; the wire does not carry `P*`)
- Groups: one per SE physical grid group (`MyCubeGrid.GetGridGroup(GridLinkTypeEnum.Physical)`); the controlled
 character joins the group of the grid it is seated in or standing on; every other entity is its own group.
- `P*` = the point of the group's oriented boxes (`PositionComp.LocalAABB` in each member's frame) nearest to the SE
 camera; the camera itself when inside. It moves continuously with the camera, so `o`, `q`, `s` never jump except on
 docking or undocking. That pop is about `|ΔP*|·d/R` for a point at distance `d` from the pivot: ≤ 0.33 m (extent²/2R)
 to 0.45 m (tool, affine vs F at 200 m) for a 200 m group; the 0.5 s blend that hides it is a HYPOTHESIS to test.
- Error bound seen from the camera: ≤ 0.24° over a 500 m × 100 m × 60 m hull at the deepest floor (VERIFIED
 arithmetic, `wire_ref.py selftest` with `SITE_V1`).

### 2.5 Entity-local data (SE → SN) and the Unity hierarchy

| Data | Wire content | Subnautica conversion |
|---|---|---|
| Vertex position, normal, tangent, binormal | SE model space, as `MyModel` returns them (LOD0, `forceFullDetail`) | `Mirror` on each |
| Tangent handedness | the binormal is sent | `w = sign(dot(Cross(n′, t′), b′))` on the mirrored vectors with Unity's own formula, +1 when zero (`MeshConventions.TangentW`). This always equals SE's own stored sign `sign(dot(Cross(t, n), b))` (VRage.Render11 `MyMeshes`; the mirror does not change `w`, VERIFIED numerically, `wire_ref.py selftest`), so Unity's rebuilt binormal is the mirrored SE bitangent. SE's pixel shader negates the normal map's y before `(T, B = Cross(T, N)·w, N)` (`PixelUtils.hlsli`, `adjust_normalmap_precomputed_tangents`), so the repacked `_BumpMap` carries −y (G′ = 1 − G) |
| Triangles | `MyModel.Triangles` order, unchanged | `MirrorTri`: Unity gets `(I0, I2, I1)` (= the `.mwm` file order). A model with < 1 % of triangles whose Unity-space geometric normal agrees with its mirrored vertex normals is flipped and logged (35 of 17,397 SE models, mostly foliage, VERIFIED census). Agreement: `dot(Cross(b − a, c − a), n_a + n_b + n_c) > 0` for the Unity triangle (a, b, c); degenerate triangles (\|g\| < 1e-9, \|Σn\| < 1e-6 or \|cos\| < 0.05, the census rule) are excluded from the fraction (`MeshConventions.MeasureWinding`, one measure per model: rigid part matrices do not change it) |
| Cube part, subpart, dummy matrix | SE-local 3×4 rows | transform model vertices by it in SE space, then `Mirror` (chunk build); or as a Unity child: `localPosition = Mirror(T)`, `localRotation = LookRotation(Mirror(Forward), Mirror(Up))` |
| Bone absolute and bind (`SkinTransform`) matrices | SE model space, 3×4 rows; the SE astronaut has 81 bones (VERIFIED `SE_astronaut.mwm`, male and female). From S5 the bones travel in the canonical order `canon1` (the 78 bones both astronauts share by name and parent, §5.4 "Character"): `boneCount` = 78, every canonical bone found by name in `Model.Bones` with the canonical parent, else the default model (then no character); the earlier assertion `boneCount == Model.Bones.Length` is replaced by this one; S7 restores it for the row count with `ExtraBones` (rows 0–77 `canon1`, rows 78–80 the model's other bones, §5.4 "S7 conventions") | conjugate: Unity matrix = `Mirror · Mᵀ · Mirror` (column form) |
| Vertex bone indices and weights (skinned models, S5) | canonical indices (u8) and f32 weights, ≤ 4 per vertex, summing to 1, from `MyModel.GetBoneIndicesWeights(int)`; weights on a non-canonical bone moved to its nearest canonical ancestor (§5.4 "Character") | Unity `BoneWeight` per vertex; `bindposes` = the conjugated SkinnedBind; `SkinnedMeshRenderer.bones` = the canonical tree |
| Lights, thruster dummies, emitters on an entity | SE-local position and direction | children of the Entity transform, after `Mirror` |
| Collision skin | boxes {centre, half extents, axes 3×3}, section centres and kinds, in the SE-local frame of the grid or subgrid that owns them | §2.6 |
| UV, texture rows | as SE stores them: raw `MyModel.TexCoords`; `ModelBegin.patternScale` = `MyModel.PatternScale`; `PartAdd.uvOffset` = the part's armor pattern offset `(offsetX/patternX, offsetY/patternY)` from `MyCubeGrid.GetCubeParts(…, outPatternOffsets, …)` (`MyCubeInstanceData.GetTextureOffset` returns 0 in the installed build), 0 for other models | `uv / patternScale + uvOffset` per part (what SE's renderer does: `MyMeshes` divides at load, the cube-instancing vertex shader adds the offset; `MeshConventions.ArmorUv`). Not a handedness question: V and DDS row order fixed by the glyph test (S3) |
| Texture colour space | DDS as stored | Subnautica is Linear. Each source is sampled as its file declares: a DXGI `*_SRGB` format decodes on sampling, as SE's shader sees it (SE's `Content\Textures\Models`: `_add` is BC7_UNORM_SRGB in 1,229 files and BC7_UNORM in 116, `_ng` BC7_UNORM in 1,416 and `_SRGB` in 114; the armor set `large_edges` is `_cm` SRGB, `_ng` UNORM, `_add` SRGB); legacy files without a DX10 header: `_cm` sRGB, the others linear. Every conversion target is linear (`R8G8B8A8_UNorm`, read back in its own format) |

Unity hierarchy for every mirrored entity:
```
Frame (root) position o, rotation identity, localScale (s, 1, s)
 Entity localPosition 0, localRotation q, localScale (k, k, k)
 chunks, subparts, bones, lights Unity-local (= mirrored SE-local) data
```
The renderer matrix is `S·R(q)`, sheared when the hull tilts: that is the differential of the map. Not scaled: the
camera, the ghost Player, every proxy collider (including the character section). With the entity scale, an SE-local
point `l` is drawn at `o + S·(q·(k·Mirror(l)))` (`PoseMath.Apply`). Unity holds world positions in float32: near the lab
spot (|o| ≈ 1.5 km, spacing 1.2e-4 m) a renderer corner differs from `Apply` by up to 1.7e-4 m from rounding alone
(emulated, `S3PoseTests`), so a 1e-4 m comparison (S3.G1-E2) uses the matrix's linear part (corner minus the matrix
translation, vs `Apply(l) − Apply(chunk origin)`: ≤ 2.4e-6 m) or a Frame near the origin; a rigid child without the
scaled Frame (S3.M7) is off by `|(S − I)·q·k·Mirror(l)|` (`PoseMath.RigidDrawError`: 62 mm on a 2.5 m block at
s = 1.0284, 4.4 mm at the lab spot's s = 1.0025).

### 2.6 Proxy colliders (Subnautica side)

- **Physics rate.** While any moving proxy section (its target pose moved > 1 mm or > 0.05° against the anchor pose of its last move, within the last 0.25 s — one C4 cadence gap; S6-1: a 2 s window stacked on the 2 s hold below made it 4 s, a
 subgrid's relative matrix included) has its root within 200 m of the main camera, the SN plugin's proxy stepper (a
 `MonoBehaviour`) is registered with `MainGameController.Instance.RegisterHighFixedTimestepBehavior(MonoBehaviour)`;
 after 2 s with none it calls `DeregisterHighFixedTimestepBehavior(MonoBehaviour)` (the game's name, not
 "Unregister"), and always on world unload, bridge teardown and `OnDestroy`. VERIFIED `MainGameController`: the
 clients are a `HashSet<MonoBehaviour>`; `UpdateFixedTimestep` sets `Time.fixedDeltaTime` 0.1 s when it is empty and
 0.02 s otherwise; the game's other clients are the Player inside a moving sub (not piloting), the grappling hook
 and the propulsion cannon, none of which the ghost Player uses (its `currentSub` is null, asserted since S1). C7 reports `fixedDeltaTime` (written by the bridge
 every frame since S0). Fallback (if S5.T-P1 rejects the cost): 0.1 s, with every per-step threshold restated per
 0.1 s.
- Each section is a **root** GameObject (no parent; `DontDestroyOnLoad`; named `DE.Proxy <entity x16>/<sectionId
 x8>`): unit scale, layer Default (0), a kinematic `Rigidbody` (`isKinematic` true, `useGravity` false,
 `interpolation` None, `collisionDetectionMode` `ContinuousSpeculative` — the enum value 3 exists in this Unity
 (VERIFIED `UnityEngine.PhysicsModule`); that it helps against tunnelling is a HYPOTHESIS, S6.B-T1 (1)). Each
 physics step (the stepper's `FixedUpdate`, ordered before the game's scripts; physics does not step while Subnautica
 is frozen) it moves with `Rigidbody.MovePosition(Vector3)` to `Apply(o, q, s, c_sec)` and `MoveRotation(Quaternion)`
 to `q`, where `c_sec` is the section centre (`SkinSection.centre`, SE-local metres) and `(o, q, s)` is **the pose the
 mirror applied to the entity's Frame at the latest camera sample** (`MirrorConsumer.OnCameraSample`: the same
 CamBracket and the same camera-group lock as the drawn hull, read from the entity view's applied pose and tick).
 No own extrapolation in v1: the collider may lag the drawn hull by one render frame (`|v|·Δt`, logged in
 `s6.proxy`). A section whose target is more than 2 m or 30° from its body pose is teleported (`Rigidbody.position`
 and `rotation`) instead of swept, and counted. The `(o, q, s)`, the subgrid relative matrix (below) and the SE tick
 used are stored per section at each step for the hit point (§2.7).
- Each BoxCollider on the section root (boxes with level/vertical axes) or on a unit-scale child (rotated boxes):
 `center = K·Mirror(b − c_sec)`; `size_i = 2·h_i·|K·Mirror(e_i)|` for half extents `h_i` along box axes `e_i`. For a
 rotated box the child rotation is the orthonormalised (polar decomposition) frame of `K·Mirror(e_i)`, because those
 vectors are not orthogonal once `K` scales them. Convention (S0, `WireMath.PrescaleBox`, WM-1 `box` records): the
 polar factor of the columns `(K·Mirror(e0), K·Mirror(e1), −K·Mirror(e2))`; the third column is negated because
 `Mirror` makes the box frame left-handed and a box is symmetric under an axis flip; `K` is symmetric positive
 definite, so this rotation equals the rigid frame `(Mirror(e0), Mirror(e1), −Mirror(e2))` to rounding and `K`
 changes only the sizes. Exact only for a level hull with level/vertical box axes;
 otherwise the corner error is ≤ `(s − 1)·|h|` (the earlier `(s − 1)·|h|/2` was not a bound: 0.127 m measured vs
 0.098 m at −1,657 m, 45°, 4 m half extents). Tool table (`wire_ref.py selftest`, `SITE_V1`, 16 m section, half
 extents ≤ 4 m): 0.014 m at −200 m/45°, 0.067 m at −1,000 m/45°, 0.111 m at −1,657 m/45°; ≈ 0.15 m at −2,000 m.
 S6.G1-E3 computes its bound from the tool for the tested depth and tilt (`python -I tools/worldmap/wire_ref.py
 s6bound depth=<m> tilt=<deg> [h=4,4,4] [offset=8] [site=v1] [samples=400] [seed=1]`: the worst corner deviation
 of the K-pre-scaled box from the exact affine over random headings, azimuths and box axes at that depth and tilt,
 with the box centre up to `offset` metres from `c_sec` per axis; the bound printed is max(1.02 × worst, 0.001 m);
 `axes=level` takes level/vertical box axes, `m5=1` adds the S6.M5 family's deviations). Per box, the deviation is
 predicted exactly by Wire `CollisionSkinMath.PrescaleCornerError(q, s, h, e0, e1, e2)` (independent of `o`,
 `c_sec` and the box centre, which are exact); with h = 4, 4, 4 the tool gives 0.0118 m at −150 m, 0.080 m at
 −1,000 m and 0.162 m at −2,000 m, nearly independent of the tilt.
- Refresh centres and sizes when `|s − s_applied|·ρ_max` or `(s − 1)·|u − u_applied|·ρ_max` exceeds 1 cm (`u =
 q⁻¹·ŷ`, with `q` the section root's rotation), with `ρ_max = max over the section's boxes of (|c_box − c_sec| +
 |h_box|)`, so a one-box section is refreshed too (a 1,000 m depth change moves each face of a 4 m box by 6.8 cm).
 At most 256 shapes per physics step, round robin; the same budget covers creating the colliders of a new or changed
 section (a 1,000-box skin takes 4 steps). Colliders are non-trigger (`isTrigger` false).
- **Sections and boxes (the skin, §5.4 C4 type 6).** A grid's skin is SE's own decomposition (`MyGridShape` /
 `MyCubeBlockCollector` logic, re-implemented on public members: the collector and `MyBlockVerticesCache` are
 `internal`): armor and other box cells merged by SE's own public `MyVoxelSegmentation` (`Simple`, 0 merge
 iterations, as `MyGridShape` does with `MyPerGameSettings.UseGridSegmenter` true), then split at a fixed section
 lattice and into pieces of at most 3 cells (large grid, 3.75 m half extent) or 16 cells (small grid, 4.0 m) per
 axis; per-block convex pieces of model collision shapes are OBBs in v1, split so no half extent exceeds 4 m. The
 lattice is anchored at the grid's cell 0: 6 cells (15 m) per large-grid section, 32 cells (16 m) per small-grid
 section; `c_sec` is the lattice cell's centre, so a section's id and centre never change when blocks are added or
 removed. Cell boxes lie inside their section's cube; an OBB belongs to the section of its centre and may protrude by
 at most its half diagonal (`ρ_max` covers it). Spheres and capsules are never non-uniformly scaled.
- **Subgrids.** A subgrid's skin arrives in the subgrid's own SE-local frame (`SkinSet` of the subgrid's entity id).
 Its sections use the **root grid's** `(o, q, s)` and the subgrid's relative matrix `rel` (= subgrid world × root
 world⁻¹, the C2 subgrid row of §2.8, sampled with the same bracket as the drawn subgrid): the section root goes to
 `Apply(o, q, s, rel·c_sec)` with rotation `q·R(rel)`, `R(rel) = LookRotation(Mirror(rel.Forward), Mirror(rel.Up))`
 (the subgrid Frame's Unity-local rotation), and its boxes stay in the subgrid's frame, pre-scaled with
 `K(q·R(rel), s)`; the refresh rule above uses `u = (q·R(rel))⁻¹·ŷ`. This is the same placement as carrying the
 boxes into the root's frame each step (`Mirror(rel·l) = R(rel)·Mirror(l) + Mirror(rel.T)`), without rewriting every
 box while a rotor turns. `rel` is stored per step for the hit point.
- **Character section** (kind 2, section id 0): a standalone root with its own kinematic Rigidbody and a non-trigger
 `CapsuleCollider` (radius 0.25 m, height 1.8 m, `direction` Y) on a unit-scale child, built from C1 alone (no
 `SkinSet` is sent for a character in v1): centre = the canonical pelvis bone's translation (`MirrorSkeleton.Pelvis`
 = 2, SE model space) and axis = pelvis → head (`MirrorSkeleton.Head` = 13) when C1's bones are valid, else (0, 0.9,
 0) and the character's up; posed with C1 `charO/charQ/charS` at the same camera sample as the character mirror;
 the same components as a grid section; disabled while C1 says the character is absent, dead or seated. Never a child
 of the Player (`EnterLockedMode` disables the Player's capsule and sets `detectCollisions = false`).
- **Components per section** (grid and character): `LiveMixin` with its `LiveMixinData` assigned right after
 `AddComponent` (before `Start`), `health = maxHealth`, `shielded = true`, a `DamageModifier` subclass whose
 `ModifyDamage` returns 0, an `EcoTarget` set with `SetTargetType(EcoTargetType.Shark)` (2030) right after
 `AddComponent` (one per section root: a section is at most one 16 m eco-region cell), and the marker component
 (§4.2 lists the fields).
- A collider is never a child of a scaled transform (Unity 2019.4: primitive colliders do not support shear; a
 child's scale is not updated when it rotates; a compound collider needs its Rigidbody on the root; VERIFIED docs).
- `Physics.autoSyncTransforms` is false in game (`MainGameController.StartGame`, VERIFIED): the plugin calls
 `Physics.SyncTransforms` after posing colliders and before any physics query of its own (self-tests, the G1-E3
 probe, the raycast service, the closest-point hit point of §2.7). The simulation step syncs moved transforms by
 itself, and the line-of-sight raycast of the `CanDealDamageTo` postfix (§2.7) runs exactly as the game's own query
 in the same call, without a sync.

### 2.7 Subnautica → SE

| Data | Wire space | Conversion on SE |
|---|---|---|
| Creature position, attack origin, effect position, static primitive centre | SN-world float3 | `F⁻¹` (doubles) |
| Directions (attack, impulse) | SN-world unit float3 | `mapdir⁻¹` at the converted point |
| Static primitive orientation | SN-world quaternion `q` | `staticAxes(q)` (§2.3): the mirror is applied, determinant +1 asserted |
| **Damage hit point on a mirrored entity** | **SE-local float3 of the grid or subgrid the hit section belongs to** + `{targetEntityId, rootEntityId, sectionId, snapshotTick}` and the flag "frame = subgrid"; the SN-world point is also sent, for logs | used directly: local → block cell of that grid (its own `GridSize`) |
| Lengths (damage radius, ranges) | SN metres | used as SE metres (horizontal error `s − 1`: 2.84 % at the deepest floor, 3.45 % at −2,000 m; accepted) |
| Static primitive sizes | SN metres | half extent `h_i·\|S⁻¹·(q·e_i)\|` with `S` at the converted centre (the horizontal part divided by `s`; S0 convention, WM-1 `static` records); shear dropped (error ≤ `(s − 1)·\|h\|`) |

Subnautica computes the local hit point with `ToLocal(o, q, s, w)` using the **same `(o, q, s)` that placed that
section at the last physics step**, stored per section; never with the interpolated render transform. For a subgrid
section it then applies the inverse of the subgrid's relative matrix used at that step (`InvertRigid(rel)·l`), so the
point is in the subgrid's frame, and the record carries the subgrid's id and the flag "frame = subgrid". For the
character section the pose is the character's `(charO, charQ, charS)` of that step.

**The hit point `w`.** The game's damage calls carry no contact point:
`MeleeAttack.OnTouch(Collider)` calls `TakeDamage(GetBiteDamage(target), default, DamageType.Normal, gameObject)`;
`ShockerMeleeAttack.OnTouch(Collider)` (an override that never calls the base) calls `TakeDamage(electricalDamage,
default, DamageType.Electrical)` **with no dealer**; `DamageSystem.RadiusDamage(float maxDamage, Vector3 position,
float detonateRadius, DamageType type, GameObject sourceObject)` calls `TakeDamage(maxDamage·f, sourceObject's
position, type)`, also with no dealer; `SeaDragonMeleeAttack` and `CrabsnakeMeleeAttack` pass the default position and
the creature as dealer (all VERIFIED). So the plugin keeps three short-lived contexts, each set by a Harmony prefix and
cleared by a finalizer of the same call, and the `TakeDamage` postfix takes the first that applies:
1. **Mouth stash** (prefix on `MeleeAttack.OnTouch(Collider)`, which `ReaperMeleeAttack.OnTouch` reaches through its
 `base.OnTouch` call): the touched collider and `mouth.transform.position` (the creature's position if `mouth` is
 null). When the postfix's target is the stashed collider's resolved target and its `dealer` is the stashing
 creature: `w = collider.ClosestPoint(mouth)`; flag "contact from the mouth capture".
2. **Dealer context** (prefix on `ShockerMeleeAttack.OnTouch(Collider)`: the ampeel and its position; prefix on
 `DamageSystem.RadiusDamage`: the blast `position`, `detonateRadius` and `sourceObject`): `w` = the closest point of
 the hit section's colliders to that position; flag "fallback hit point", plus "radius damage" for a blast. The
 context also supplies the dealer for `dealerTechType`.
3. Otherwise, with a `dealer`: the closest point of the section's colliders to `dealer.transform.position`; with no
 dealer but a non-default `position` argument: to that position; with neither: the section root (counted). Flag
 "fallback hit point".
"The closest point of the section's colliders" = the nearest of `Collider.ClosestPoint(p)` over the section's enabled
colliders, after `Physics.SyncTransforms`. A crashfish blast's amount falls off with the distance from the blast to
the `LiveMixin`'s transform, i.e. to the **section root** (`c_sec`), not to the hull surface (VERIFIED
`DamageSystem.RadiusDamage`: `max(0, (radius − d)/radius)`, one `TakeDamage` per `LiveMixin` found as an ancestor of
an overlapped collider, overlap on all layers); accepted in v1 (S6.N5 detonates within 5 m of a section centre).
**Bite acceptance.** A Harmony postfix on the protected, non-virtual `MeleeAttack.CanDealDamageTo(GameObject target)`
turns a `false` into `true` only for a target carrying an enabled marker of a section whose combat gate is open, and
only if the game's own line-of-sight rule holds with the proxy counted as the target: a raycast from the creature's
`transform.position` to the target's `transform.position` (the section root), layer mask −5 (everything but Ignore
Raycast), triggers ignored, every hit resolving (through `attachedRigidbody`) to the target, **any section of the same
root entity**, the creature itself or another `Creature` (VERIFIED: the game returns `false` for a proxy before its
raycast, because a proxy is no Player, Creature, Vehicle, `SubControl` or `CyclopsDecoy`). The ampeel shock needs no
acceptance (`ShockerMeleeAttack.OnTouch` checks only a live `LiveMixin`). Bites start from the creature's mouth
trigger: the `OnTouch` component invokes on `OnTriggerEnter` with a non-trigger collider (or a trigger on the Useable
layer), once per entry; its per-prefab `tagFilter` is not read (HYPOTHESIS: empty on the biting prefabs; S6.B-T1 (2)
settles it).

### 2.8 Snapshot field spaces

| Field | Type | Space / meaning |
|---|---|---|
| `camPos` | 3×f32 | SN-world, `F(Pcam)`, exact |
| `camRot` | 4×f32 | Unity quaternion |
| `vFovDeg`, `near`, `aspect` | 3×f32 | Unity vertical FOV (degrees), near plane (m), SE aspect (for logs) |
| per entity: `id, o, q, s, k, v, ω` | u64, 3, 4, 1, 1, 3, 3 ×f32 | SN-world; `ω` in Unity's numeric `AngleAxis` sense |
| character: `id, o, q, s` + `pelvisSn` + bones | as an entity + 3×f32 + `n × 12 × f32` | pelvis in SN-world (where the ghost Player root goes); bones in SE model space rows (mirrored by Subnautica) |

Subnautica interpolates `o` (lerp), `q` (slerp) and `s` (lerp) with one alpha for every entity and the camera
position; render time `t = qpcNow − 1.25 SE ticks`; the camera orientation is the newest one. A camera jump > 20 m or
> 45° in one tick, or the SE teleport flag, snaps and suppresses motion vectors for one frame. The re-sample happens
once per frame for the main camera only. `presentedSeTick` is defined in §5.4 (C7).

**Many grids, subgrids, subparts and bones.** One bracket and one alpha per frame for everything: the
camera's (older tick, newer tick, alpha) of that frame's sample (LateUpdateLast, then the main camera's onPreCull
re-sample) poses, in this order, every root entity of C2 (`o` lerp, `q` slerp, `s` lerp, `k` of the newer; past the
newest snapshot by at most one tick `o` is extrapolated with the camera's factor, as S3 does), then every row of the C2
subpart table (subgrid relative matrices, dynamic subparts, the held tool) and the player's C1 bones. A row's SE matrix
is split into its translation and its rotation (orthonormalised rows → quaternion in SE space; never a component-wise
conversion), lerped and slerped with the same alpha (clamped to [0, 1]; rows are not extrapolated), then mirrored once
into the Unity child: `localPosition = Mirror(T)`, `localRotation = LookRotation(Mirror(Forward), Mirror(Up))`. A row
missing from the older snapshot takes the newer one's value; missing from both, the child keeps its last pose (sticky,
counted). The character's Frame/Entity uses C1 `charO/charQ/charS` with the same bracket. Subgrids never get their own
`(o, q, s)`: they ride their root's Frame/Entity with the relative matrix (D8), so a hinge cannot drift from its root.
At alpha 1 every drawn transform equals SE's tick-N matrices through `Apply` (S5.N3: ≤ 1 mm, recomputed with
`wire_ref.py`); the coherence of a cockpit-fixed point under a moving camera is S5.N4 (< 0.5 px), and sampling grids
one tick behind the camera is its seeded failure S5.M1. **Camera-group lock:** the camera's
orientation is the newest C1 tick, so the entities that move with the camera — the root grid whose group holds it
(C2 flag bit 1), its subgrid rows and dynamic children, and the seated or first-person character — are posed at that
newest tick (alpha 1) and translated by the camera's interpolation offset `P(bracket) − P(newest)`; every other
entity keeps the bracket interpolation. Under S5.M1 the group is taken one tick earlier, which keeps the mutant red. The Retarget animation source (§5.4 "Character") is not
interpolated from SE ticks: it follows Subnautica's animator clock.

### 2.9 Conformance (WM-1) and mutants
- Generator: `python -I tools/worldmap/wire_ref.py vectors <out.jsonl> [N=10000] [--site v1|example]`: deterministic
 JSON lines (header, `point`, `dir`, `pose`, `camera`, `local`, `box` with rotated boxes, `mesh`, `motion`,
 `static`); 25,011 lines and 9.11 MB per site. The default site is `v1`; `--site example` gives the EXAMPLE file.
 sha256 on this machine: SITE_V1
 `da0968793172ab364b7b062e48f1acc9c64d5199ae21b04b8de24e1904507674`, EXAMPLE
 `9f04bb727eac6e658cf4f10cd35dac6ee0874e7b5ad346d797e13bdba1862004`. `python -I tools/worldmap/wire_ref.py selftest
 [--site v1|example|all]` runs both sites by default. Generated by the test runner, never committed.
- **The contract site for WM-1 and for the selftest is `site_v1.SITE_V1`** (§3). Open item O1 is closed (S0): the
 site selector exists, pose and camera samples cover `y ∈ [−3,040, +350]`, the `box` kind has rotated boxes and the
 `static` kind exists (primitive centre, quaternion, sizes → SE axes with determinant +1). The sha256 in `gap-1.md`
 (`eb3e08f0…`, 23,011 lines) belongs to the pre-S0 EXAMPLE file. The `SITE_V1` selftest numbers quoted in these
 documents were produced on by a scratch runner that swapped the site in memory; `selftest --site v1` is
 now the in-repository way to recompute them.
- Tolerances: positions in doubles 1e-6 m; after float32 1 mm; directions and quaternions (as rotations) 1e-9; scale
 1e-12; local round trip 1e-9 m. Conventions the file header does not carry (S0, D13): float32 `q` and `s` fields
 2.5e-7; velocities 1e-9 absolute; quaternions compared as rotations, `min(|a − b|, |a + b|) ≤ 1e-9`. C# readers parse
 the file with a correctly rounded parser (`PyFloat.Parse`/`Repr` in `DeepEngineers.Wire`).NET Framework
 `double.Parse` is not correctly rounded (8 of 300 17-digit strings, D14).
- Consumers: `dotnet test` of `WireMath.cs` against every record of both site files (`tests/DeepEngineers.WireMath.Tests`);
 SE in-game self-check at plugin `Init` (50 `pose` records, log `DE.WireSelfTest PASS|FAIL n=50 worst=…`);
 Subnautica in-game self-check at bridge start (log `n=70`: 50 `local` records as real Frame/Entity GameObjects, 20
 `box` records as real BoxColliders checked with `collider.bounds` and `Collider.ClosestPoint` after
 `Physics.SyncTransforms`). The subsets are compiled C# arrays in `DeepEngineers.Wire.dll` (`Wm1Subset.cs`, made by
 `tests/wm1/make_subset.py`), whose SHA-256 is rechecked and logged at each run. Unity-check rules (S0, D21):
 `collider.bounds` extents accepted up to 1.01× the expected value plus `contactOffset`; collider rotation tolerance
 1e-5; `Physics.autoSyncTransforms` switched off during the call and restored. Both halves passed in the real games on (S0.G1-E4a; Unity half worst 4.5e-4 m). A FAIL disables the display bridge.
- Mutants that must fail: mirror dropped; triangle swap dropped; `mapdir` taken at the entity origin instead of `P*`;
 `s` dropped or applied to y; `ω` without the sign flip; `q` rebuilt from mirrored Forward/Up on the Subnautica side
 (double mirror); colliders parented under the scaled Frame (fails the in-game box check with the Entity rotated by
 30°); static primitive axes without the mirror (determinant check trips); `SyncTransforms` skipped (the box check
 after a 30° re-pose reads the stale pose).

### 2.10 Swimming model (S2b)

SE owns the engineer everywhere (§1). In the water of `DE_4546B` the SE plugin's swim controller sets the character's
velocity every SE tick from the **swim model**, a clean reimplementation of Subnautica's diver: `SwimModel.Step(ref
SwimState, ref SwimInputs, out SwimOutputs)` in `DeepEngineers.Wire.dll` (`SwimModel.cs`; pure, deterministic,
allocation-free). Input never leaves SE; Subnautica is untouched (its ghost Player keeps following C1). Since S5a the swim
**state** crosses the bridge, nothing else: the C1 swim extension (§5.4; layout 1.0 unchanged, hash
`0x4f3a35f6fc1eaf31`) carries the latch, mode, move axes, speed fractions and the suit's O2 fraction for Subnautica's
arms and breathing; input, physics and oxygen stay SE's. The telemetry is the SE JSONL `swim` row. Numbers
and their sources: `tests/s2b/sn_reference.py`
independent replay and S2b.N1 oracle: `tests/s2b/swim_replay.py`.

**Sources.** The rules are VERIFIED`Assembly-CSharp` (`UnderwaterMotor`, `PlayerController`,
`Player`, `OxygenManager`, `Oxygen`, `Ocean`, `MainGameController`). The values are those serialized on the Player of
Subnautica's Main scene (VERIFIED: `sn_reference.py check-scene` reads `main.unity_*.bundle` read-only; PlayerController
pathID 527, UnderwaterMotor 403, Oxygen 405): **the swim speeds differ from the C# initializers** (7.6/7.6/7.6/6.3 m/s,
not 6.64/6.64/6.64/6.6; with the initializers the top speed would be 4.98 m/s instead of 5.70). The physics engine's
linear damping (`v·max(0, 1 − dt·drag)` per step, PhysX 4.1 native) is REPORTED.

**Frame, reference point, time.**
- Swim frame: +Y up (away from the planet centre at the character), X/Z any horizontal basis; the SE plugin uses the
 SN-world axes of the map at the character (`mapdir`). The model never builds a rotation from the look vectors, so
 handedness does not matter. Outputs are in the same frame.
- Reference point: Subnautica tests every water rule at the player root, which is the camera pivot (VERIFIED scene: the
 camera object sits at local 0 under the root; the capsule reaches 0.25 m above it and 1.5 m below standing). The
 model's height is the **eye**: the SE character's head / first-person camera point, in SN metres (`y`, sea at 0),
 whatever the camera mode.
- Time: a free diver's physics steps at **0.1 s** in Subnautica (`MainGameController` 0.1 s; 0.02 s only in a moving
 sub or with the grappling arm / propulsion cannon). The model is called every SE tick (1/60 s) and runs one Subnautica
 motor step every **6 ticks** (`SnStepTicks`), holding the resulting velocity in between: the 60 Hz SE path equals
 Subnautica's path at every 0.1 s and is linear in between, as Subnautica's own body moves. A step runs at once when
 the model takes control (phase 0).

| Constant (`SwimThresholds`) | Value | Subnautica source | Use |
|---|---|---|---|
| `DiveForwardMax`, `DiveBackwardMax`, `DiveStrafeMax` | 7.6 m/s | scene | max speed with forward / backward / strafe input, eye below 0 (motor mode Dive) |
| `VerticalMax` | 6.3 m/s | scene | vertical maximum in every mode, and the floor of the max speed |
| `RunForwardMax`, `RunBackwardMax`, `RunStrafeMax` | 3.5, 5, 5 m/s | scene = ` | swim latch on, eye at or above 0 |
| `WaterAcceleration` | 20 m/s² | scene = ` | velocity added per step |
| `SwimDrag` | 2.5 /s | scene = ` | damping per step ×0.75 |
| `SurfaceSpeedMultiplier` | 1.3 | ` | max speed ×1.3 while the eye is above 0; the acceleration follows the ratio (`, 244-247`) |
| `HeldToolPenalty` | 1 m/s | ` | max speed −1 while a tool is held |
| `GroundAcceleration` | 45 m/s² | scene UnderwaterMotor word 24; ` | the acceleration while the diver touches something; model input `Grounded` |
| `EntryVerticalFactor` | 0.5 | ` (`SetEnabled(true)`) | vertical velocity halved each time the swim motor is enabled: when the swim latch turns on, and at every Dive↔Run motor switch with the latch on, i.e. each crossing of the eye through 0 |
| `SurfaceTopY`, `SurfaceBandBottomY`, `SurfaceExponent`, `LookDownY` | 0.52 m (0.8 − 0.28), −0.5 m, 0.3, −0.3 | ` | the surface rule |
| `NormalizeEpsilon` | 1e-5 | UnityEngine `Vector3.Normalize` (VERIFIED, CoreModule) | a zero input or direction stays zero |
| `SwimEnterEyeY` | −0.1 m | ` | swim latch on below it |
| `SwimExitEyeY`, `SwimExitGroundedEyeY` | +0.8 m, +0.1 m | ` | swim latch off at +0.8, or at +0.1 while grounded (tested only above +0.1) |
| `UnderwaterEyeY` | 0 | ` | isUnderwater: Dive numbers and no breathing below it |
| `JetpackOnEyeY`, `JetpackOffEyeY` | 0, −0.1 |
| `OxygenCapacity` | 45 | scene Oxygen | the diver's own air, no tank |
| `OxygenRefillPerSecond`, `OxygenRefillEyeY` | 30 /s, −1 m | ` | refill while the eye is above −1 m (strict) |
| `SafeDepth`, `UnsafeDepth`, `CrushDepth` | 0.1, 100, 200 m | ` | depth classes |
| breath periods × factors | 3 s × 1, 2.25 s × 1.5, 1.5 s × 2 | ` | 180 / 135 / 90 ticks; 1, 1.5, 2 per second |
| `SprintFactor` | 1 (no sprint) | `; ` | documentation: Subnautica's swim motor has no sprint |

**Motor step** (`SwimModel.MotorStep`, every 6th controlled tick), from the velocity `v0` SE measured (halved
vertically on the tick the swim motor is enabled again: the latch entry and each crossing of the eye through 0 with the
latch on, event `MotorSwitch`; such a tick between two steps halves the held velocity's vertical part instead), the
move axes `m = (strafe, vertical, forward)` in [−1, 1] and the look vectors:
1. `num = min(1, |m|)`; `h = normalize(strafe, 0, forward)` (zero below 1e-5).
2. Max speed `a`: forward max if `h.z > 0`, backward max if `h.z < 0`; then `max(a, strafe max)` if `h.x ≠ 0`; then
 `max(a, VerticalMax)`; Dive numbers below 0, Run numbers at or above. Then −1 m/s with a tool, ×1.3 above 0;
 `ratio = a / a_before`. `cap = max(|v0|, a)`.
3. Direction `d = normalize(h.x·right_h + h.z·forward + (0, m.vertical, 0))`, `right_h` = the horizontal part of the
 camera right, normalized (roll removed); `forward` with pitch: free 3-D in the look direction.
4. `dv = num · 20 · 0.1` (`num · 45 · 0.1` while `Grounded`, the contact acceleration; × `ratio` when > 1). If `dv > 0`:
 `v1 = v0 + d·dv`, rescaled to `cap` if longer; then, if the
 eye is at or above −0.5 m and there is no down input and the rotated horizontal input's y is not below −0.3:
 `v1.y ×= pow(clamp01((0.52 − eye) / 1.02), 0.3)`. If `dv = 0`: `v1 = v0`.
5. `v = v1 · max(0, 1 − 0.1 · 2.5)` (×0.75). The controller sets `v` on this tick and the 5 following ones.

*** Subnautica's `AlterMaxSpeed` adds the fins
bonus to the selected max speed after the tank and suit penalties and before the held-tool penalty and the surface
×1.3
so the ratio `a / a_before` and with it the acceleration carry it (`, 244-247`). The model's input
`SwimInputs.FinsModifier` (m/s; `SwimFins`: off 0, basic 1.9, ultra 3.2) is added in step 2 right after
`max(a, VerticalMax)`: with Ultraglide the Dive maxima become 10.8 m/s (forward, backward, strafe) and 9.5 m/s
(vertical), the first step adds `2 × 10.8/7.6` m/s, and the steady speeds are **8.1 m/s** horizontal and **7.125 m/s**
vertical (`sn_reference.py derived finsUltraForwardSteady`, `finsUltraVerticalSteady`); at the surface the vertical
floor rules, `(6.3 + 3.2) × 1.3 × 0.75 = 9.26 m/s`; the time to 90 % is unchanged (0.6 s). The SE controller's setting
(`SwimController.Fins`, default ultra) goes to the model every tick and into the row key `fin` and the C1 byte `fins`;
`/de swim fins off|basic|ultra`, C11 `de.swim {fins: "off"|"basic"|"ultra"}`. S2b.M1 scales the base maxima only, not
the fins. A fresh `SwimInputs` has no fins (the S2b numbers below hold for `fin = 0`); the Python twin reads the row's
`fin` bits (absent = 0) so the replay stays exact in either setting.

Implied (`sn_reference.py derived`, VERIFIED by the C# tests): top speed **5.70 m/s** forward, backward and strafe,
**4.725 m/s** vertical, **6.14 m/s** at the surface (eye above 0), 4.95 m/s with a tool; an analog input `n` settles at
`6·n` m/s below the cap; 90 % of the top speed from 0.6 s, all of it from 1.0 s; stop distance **1.71 m** (1.69 m until
below 0.05 m/s, in 1.6 s). Swimming up stops with the eye between +0.52 m and at most +0.618 m (the 0.1 s steps
overshoot the rule a little; `swim_replay.surface_head_max`; +0.584 m from −20 m; the motor switch at the crossing
halves the vertical speed, without it the limit would be +0.687 m), always below the +0.8 m exit: the diver cannot leave
the water by swimming. In contact (seabed): 0 → 3.375 → 5.70 m/s in two steps (0.2 s instead of 1.0 s).

**Modes, hysteresis, jetpack, roll** (`SwimOutputs.Mode`, per tick, in this order):

| Mode | Condition | Velocity | Jetpack |
|---|---|---|---|
| Walk | swim latch off (on below eye −0.1; off at +0.8, or +0.1 grounded) | SE's stock movement (the model is inert, `ControlsVelocity` false) | allowed |
| Swim | latch on, eye < 0 | the model (Dive numbers) | not allowed: the controller switches it off |
| Surface | latch on, eye ≥ 0, jetpack off or not allowed | the model (Run numbers, ×1.3 above 0, surface rule) | allowed once the eye reached 0 (jetpack latch: on at eye ≥ 0, off below −0.1) |
| Fly | latch on, jetpack on and allowed | SE's jetpack (the climb out onto a platform: jump + jetpack) | allowed |

While `ControlsVelocity`, the controller sets the character's linear velocity to `SwimOutputs.Velocity` before SE's
physics step; SE gravity, the Water Mod's character forces and the inertia dampeners must not act on the character
(the measured velocity fed back as `SwimInputs.Velocity` on the next tick must equal the imposed one unless something
was hit: S2b.N1 measures exactly that). The model yields no rotation: roll is disabled in the water (the controller
ignores roll input; pitch and yaw stay SE's). Animations stay SE's (no swim cycle; first person is the reference).
Events (`SwimEvents`): Entered, Exited, JetpackGranted, JetpackRevoked, Breath, Resumed, MotorSwitch (64: the eye crossed
0 with the latch on); each is logged.

**Oxygen rule** (in force while the latch is on or the eye is below 0, `OxygenRuleActive`): Subnautica units = seconds
of breath; the SE suit maps linearly, `O2 = suit fill level × 45` (a full SE suit = Subnautica's 45 s without a tank).
Per tick: if the eye is below 0, a breath when `tick % period == 0` (180 / 135 / 90 ticks for depth > 0.1 / > 100 /
> 200 m) removing 3 / 3.375 / 3 (clamped at 0), i.e. 1 / 1.5 / 2 per second: 45 s / 30 s / 22.5 s of air; then, if
the eye is above −1 m, +0.5 (30 per second, clamped at 45): full in 1.5 s. `OxygenDelta` is the rule's exact
per-tick value (logged and replayed); the SE controller applies the same rule as rates ("SE controller" below) in
place of SE's own suit consumption while the rule is in force; outside it SE's suit rules apply unchanged. An empty
suit triggers SE's own suffocation.

**Arms, O2 mirror and breath.** Subnautica shows its own first-person arms and plays its own
breathing from the state above, never from its own motor, input or oxygen consumption (§1 unchanged): the SE controller
writes the latch, mode, move axes, speed fractions, depth class, fins and the suit's O2 fraction into the C1 swim
extension every tick (§5.4), and the Subnautica plugin (`S5aHost`) (a) shows the view model's renderers only while C1
`inWater` is set, the camera is under `Ocean.GetOceanLevel`, C1 `cameraMode` is FirstPerson, C5 says Underwater or
Surfacing and C1 drives the camera — hidden again the frame any of these fails, on a new SE generation, at quit or when
the ghost rule stops — and sets the Player animator's parameters from the C1 fields with `ArmsController` disabled;
(b) sets the ghost's `OxygenManager` to `o2/255 × capacity` every frame through `AddOxygen`/`RemoveOxygen` with the
manager's own Update (the surface refill) disabled, so `LowOxygenAlert`, the oxygen bar, `FMODGameParams` and
`Player.SuffocationUpdate` react to SE's suit; (c) fires `Player.tookBreathEvent` every breath period of the C1 depth
class and `PlayerBreathBubbles.MakeBubbles` every `delay` while underwater. The ghost cannot die of it: `liveMixin.shielded`
stays on, `Player.SuffocationDie` is prefixed away (§4.2), and without that patch the mirror floors at 1 unit. The PDA
voice stays suppressed by S1's entry-point prefixes. Death, damage and the oxygen rule itself stay SE's.

**Pressure: the diver takes none.** `Player.CrushDamageUpdate`
 is private and never invoked: the only `CrushDamageUpdate` invocations are ` and
`, each on its own component (vehicles, bases); the depth class "Crush" (> 200 m) only shortens
the breath. So the character keeps `CrushDamage` 0 on `DE_4546B` (§3) at every depth (the abyss rule of §4.1 below
−2,000 m is ours and unchanged). Subnautica's pressure thresholds are a vehicle rule (`CrushDamage`: initializers
100 m base, 20 damage every 3 s; per-vehicle prefab values and depth modules not read): S5.

**Telemetry.** `SwimTrace.Format(ref SwimState pre, ref SwimInputs, ref SwimOutputs, char[] buf[, string segment])`
writes one `swim` row (version 1, < 2,048 chars, allocation-free) with the inputs, the state before and the outputs;
every double as its exact IEEE-754 bits for the replay, plus readable keys for people and `tests/s2b/analyze_swim.py`
(`st`, `inW`, `yHead` = eye, `o2` = suit fill 0..1, `o2u` = Subnautica units, `move`, `vel` = the velocity set,
`pos` = eye, `look`, `jetOk`, `tr` on a transition, `seg` = script label; S5a: `fin` = the fins modifier bits, absent in
rows written before S5a = 0); key list in `SwimModel.cs`; the state keys
include `s_eu`. S2b.N1 needs a row
every tick while a script runs (a gap ends a segment). The plugin's row is this row with a UTC `t` first and the SE
keys appended (`SwimRowExtras`, no collision): `siteVersion`, `dt`, `src` (script|keys|none), `step`, `vsrc`
(velocity source), `exit` (reason on an exit tick), `chestY`, `applied`, `gain`, `vApp`, `vSe` (SE's velocity right
after its physics step: the measured motion), `posAfter`, `jetWant`, `damp`, `dampWant`, `o2Use`, `o2Refill` and,
when the suit is readable, `o2L`, `o2Cap`, `o2Mult`, `helmet`. The C# model and the Python replay agree bit for bit on every
scripted scenario (maximum velocity difference 0); `swim_replay.analyzer_port` is the analyser's model.

**S2b.N1 script** (`swim_replay.py script n1`; SE alone at −50 m, looking level): forward 10 s, release 4 s, strafe
6 s, release 4 s, backward 6 s, release 4 s, up 6 s, release 4 s, down 6 s, release 4 s, forward with sprint 6 s,
release 4 s. `swim_replay.py n1` compares SE's own motion with the open-loop model on these segments (5 %).

**Seeded failures** (`SwimInputs.Mutants`, `SwimMutants`): S2b.M1 `M1Numbers` (max speeds and acceleration ×1.5),
S2b.M2 `M2JetpackInWater` (the jetpack stays allowed in the water), S2b.M3 `M3NoHysteresis` (one swim threshold +0.35 m
both ways, one jetpack threshold 0), S2b.M4 `M4OxygenOff` (the oxygen rule off). The replay is red on each unless
`--honour-mutant`.

**SE controller.**
- Hook: one `Step` per SE tick in a Harmony prefix on `MyPhysics.SimulateInternal`
 after SE's character update, gravity and the queued forces, the last write before the Havok
 step; the postfix reads the post-step velocity and position for the row. A prefix on `MyCharacter.Simulate`
 lets a script drive SE's own walk and jetpack whenever the model does not set the velocity.
 A missing target logs `DE.Harmony MISSING` and `DE.Swim FEATURE DISABLED` once and the controller stays inert.
- Swim state = the swim latch (modes Swim, Surface, Fly): the `DE.Swim enter|exit` lines fall on the rows' Entered /
 Exited ticks. The velocity is set only while `ControlsVelocity` (Swim, Surface); in Fly SE's jetpack moves the
 engineer. Exit reasons: the jetpack running at the latch exit → `jetpack`; grounded line → `grounded`; else
 `surface`; the controller's own exits (`model-off`, `seated`, `dead`, `left-water`, `character-changed`) reset the
 model to Walk without a model transition.
- Inputs: move axes from SE's `MyCharacter.MoveIndicator` (X strafe right, Y up, Z backward) and `WantsSprint`, or a
 `de.swim` script; look = the head matrix in first person, SE's camera in third person; eye = the head bone (else
 feet + 1.7 m, logged FEATURE DISABLED head); `Grounded` = the character proxy's `Supported` (the grounded exit and
 the contact acceleration; SE's support sees the floor, not walls or ceilings); `JetpackOn` =
 `JetpackRunning`; `ToolHeld` = `CurrentWeapon != null`; `Oxygen` = suit fill level × 45; `Tick` =
 `GameplayFrameCounter`.
- `Velocity` input while the model keeps control (`de.swim velocitySource`): `poststep` (default) = SE's velocity
 right after the previous physics step, i.e. what Havok made of the imposed velocity (contacts included), before SE's
 character update and the Water Mod write into it again (both are overwritten by the next imposed velocity, so they
 never move the engineer); `readback` = the velocity at the prefix; `model` = the model's own previous output. On
 the tick the model takes control it is SE's current velocity. The in-step effect of SE's physics is measured and
 compensated (`SwimGain`, `de.swim gain=auto|off|<factor>`).
- Oxygen as rates: while the latch is on and the rule in force, SE's own suit sink is set
 (`OxygenConsumptionRateMutliplier`, §1.1) so that the suit loses `units/s ÷ 45` of its capacity per second (1 / 1.5
 / 2 units/s by depth class with the eye below 0), and the refill (30 units/s above −1 m) is added to
 `SuitOxygenAmount` every 10 ticks. Breaths are therefore spread evenly instead of every 3 / 2.25 / 1.5 s; the S2b.N4
 oracle compares rates over ≥ 10 s. The rule needs the helmet closed (SE's suit sink). SE's own refill from carried
 O₂ bottles still

 suppress while swimming); the scripted session carries no O₂ bottle and the N4 oracle drops upward jumps.
- Rows: every tick while a script runs, and after `de.swim rows=on` every tick the model is
 evaluated (the launcher's scripted session); otherwise once a second (tick % 60) while the latch is on, plus every
 transition; none while the model is off. Written without allocation; the SE keys are fixed-point decimals (9 for `dt`, 6 otherwise). The step index `n` skips
 one after any change of the model state outside `Step` (teleport, controller exit), so the replay sees a gap there,
 never a state mismatch. Mutants: `S2bMutants` bits 1/2/4/8 = `SwimMutants` M1–M4.
- Inert (stock SE movement) when the site is not bound or the activation gate is closed, off the dive planet,
 dead or seated, with `de.swim model=off` or `DEEPENGINEERS_SWIM=off` (no S2b patches at all);
 `DEEPENGINEERS_SWIM=standby` installs the patches with the model off until `de.swim model=on` (the launcher's s2b
 session: DE Lab loads at −3,000 m, the crush class). A game member or type missing at run time (`MissingMemberException`,
 `TypeLoadException`,...) logs `DE.Swim FEATURE DISABLED controller` once and leaves the controller inert; callers
 outside S2b (plugin Update, C1, the state dump) go through `SwimGuard`, so a swim controller that cannot initialise
 (a stale Wire DLL) never stops the link, the heartbeat or C1.
- Seat: sitting down while swimming ends the swim with the jetpack parked (§1.1); a third Harmony
 target, a postfix on `MyCharacterJetpackComponent.GetObjectBuilder(MyObjectBuilder_Character)`
 keeps the player's states in a save (§1.1). `DE.Harmony S2b OK 3 targets
 patched`.
- The `de.swim` dump carries `tick`, `rows`, `standby`, `policy.parked`, `compensation.bias` and `suit` {`helmet`,
 `h2Level`, `jetpackPowered`, `o2Bottles`, `h2Bottles`, `survival`}: the scripted session's preconditions.

** Subnautica's 45 s tank stays the rule; while an
 SE oxygen bottle **with gas** is in the character's inventory the swim controller leaves SE's own consumption
 multiplier in place (the depth factor off), so SE's suit timing applies and SE's own below-50 % refill from the bottle
 keeps draining it at SE's pace; an empty bottle counts as none. The inventory is scanned once per second (the
 bottle's `GasLevel` read by reflection). `SwimO2Rule.WantedMultiplier`, log `DE.Swim o2 tank: …`, `bottle=` on the
 `DE.Swim sec` line, `bottleWithGas` in `de.swim o2`. The S2b/S5 oxygen oracles stay bottle-free (make_survival_copy
 strips bottles by default).

**Not modelled (v1).** The ledge step-up of ` (physics traces; SE's jump + jetpack instead);
tanks, suits, Seaglide, rebreather (no SE counterpart; fins are modelled since S5a); the one-frame lag of Subnautica's motor mode behind
`isUnderwater`; Subnautica's float32 arithmetic (the model uses doubles); the 1-second throttle of Subnautica's
grounded ray (the SE input is evaluated every tick); the Surface depth class's 99,999 s breath (never taken); the
velocity `UnderwaterMotor.SetEnabled` halves is Subnautica's `playerController.velocity` (possibly one frame old), the
model halves SE's measured one; contacts with walls and ceilings (SE's support sees the floor only, so the contact
acceleration applies on the seabed)
diver swims in a 0.75 m capsule from 0.5 m below to 0.25 m above the eye
SE keeps its upright engineer (head bone about
1.6–1.7 m above the feet), so the SE camera stays about 1.6 m above the seabed (Subnautica: 0.5 m) and gaps a diver
swims through can stop the engineer. Every branch of the motor's rules and its handling: `sn_reference.py coverage`.

---

## 3. Site record (v1)

Source: `docs/recon/gap-2.md` (decision and evidence). Machine-readable copy: `python -I tools/worldmap/site_v1.py record`.
Conformance: `python -I tools/worldmap/site_v1.py selftest [<octree_state.npy>]` must print `SITE V1 SELFTEST PASS`.

The site is one Subnautica tile on one Space Engineers ocean planet. The constants below are compiled into the SE
plugin under `SITE_VERSION = 1`. Runtime values are read and validated as described below. The bridge writes nothing
about the site into the SE save; the save does hold the Water Mod's sea for this planet and the tile entity ids as
landing-gear references.

### Constants

| Field | Value | Notes |
|---|---|---|
| `siteVersion` | 1 | bump on any change below; tools and logs carry it |
| `planet.generatorSubtypeId` | `DE_4546B` | own `PlanetGeneratorDefinition` in a local world mod; the dive planet is the planet of this generator (exactly one per world, else the bridge refuses) |
| `planet.storageRadius` R | 60,000 m | spawn size (diameter) 120,000 m |
| `planet.hillParams` | Min −0.05, Max 0.12 | MinimumRadius 57,000 m, AverageRadius 60,000 m, MaximumRadius 67,200 m (float32) |
| `planet.heightmaps` | 6 faces, 16-bit, all zero | flat floor at MinimumRadius; no SurfaceDetail, EnvironmentItems or weather generators |
| `planet.gravity` | SurfaceGravity 1.0, falloff 7 | exactly 1 g for 57,000 ≤ r ≤ 67,200 |
| `planet.atmosphere` | Breathable, OxygenDensity 0.9, Density 1.0, LimitAltitude 2.0, MaxWindSpeed 80 | atmosphere altitude 14,400 m above R; air density 1.0 at the sea |
| `water.settingsRadius` | 1.0526316 (float32 bits 0x3F86BCA2) | Water Mod `PlanetConfig` for `DE_4546B`; equals `SetSeaLevel(planet, 3000)`; applied only when the planet is first spawned, then held in the world save |
| `water.RsExpected` | 60,000.00390625 m (float32) | = 57,000f × 1.0526316f |
| `water.waveHeight`, `tideHeight`, `volumetric` | 0, 0, false | the SE surface is the sphere r = Rs |
| `water.crushDamage` | 0 | this planet only (per-planet `WaterSettings`); other planets keep the Water Mod defaults |
| `water.currentSpeed`, `enableFish` | 0, false | |
| `anchor.U0`, `N0`, `E0` | (0, 0, 1), (0, −1, 0), (−1, 0, 0) | world axes; planets never rotate; E0 × N0 = U0 (VRage cross) |
| `anchor.psi`, `anchor.O` | 0°, (0, 0, 0) | SN (0, 0, 0) is the anchor on the sea surface |
| `sun.baseSunDirection`, `sun.rotationIntervalMinutes` | (0, 0, 1), 20 | **world settings, not world-mod data** (table below); the sun axis is SE world +Y = SN −Z; one SE day = one SN day (1,200 s) |
| `depth.abyssLimitSnY` | −2,000 m | crush zone below it (§4.1); the floor guard at −2,500 m is a gameplay rule of §4.1 (O7), not a site constant |
| `depth.planetFloorSnY` | −3,000.004 m | SE floor, never drawn by Subnautica |
| `terrain.representation` | provider-backed `MyVoxelMap` tiles, **plug**: Subnautica data only, a missing batch file = empty, no fill | the provider, its SE calls and the content mapping: §3.1 |
| `terrain.tile`, `pitch`, `interiorMargin` | 1024 voxels (1 m), 1020 m, 2 voxels | 4-voxel overlap; neighbouring interiors abut. Storage `new MyOctreeStorage(provider, (1024, 1024, 1024))`: 1024 is already a power of two, one provider leaf at LOD 10 (VERIFIED, §3.1). SE meshes a tile's boundary voxels against "outside = empty" (`FillOutOfBounds`), so a tile shows false faces only where its own boundary voxels are solid; those lie inside the neighbour's interior, inside rock the neighbour also has (the margin's purpose) |
| `terrain.lattice` | lateral min corners C + {−2042, −1022, −2, 1018} on X and Y; radial min corners C + {58,064, 59,084, 60,104} on Z | 4 × 4 × 3 slots, world axes (planets never rotate). VERIFIED on this PC: `site_v1.py selftest <octree_state.npy>` with the state map of all 5,416 files: every one of 387,094 solid octrees lies inside the tile interiors (mapped extent rel. C: X −1,807.7…1,904.8, Y −1,902.8…1,918.8, Z 58,067.0…60,153.9) |
| `terrain.tilesCreated` | 34: all 16 of row 0 (Z 58,064), all 16 of row 1 (Z 59,084), row 2 only (i, j) = (1, 0) and (1, 1) | i indexes X, j indexes Y, k the radial row, from the listed min corners. VERIFIED by the same run: 34 of 48 slots non-empty (row 0: 1,656–31,132 solid octrees per tile; row 1: 2,653–29,908; row 2: 2 and 17) |
| `terrain.entityId(i, j, k)` | `MyEntityIdentifier.ConstructId(ENTITY, 0x44455400000000 \| siteVersion << 16 \| k << 8 \| j << 4 \| i)` = `0x0144455400010000 + 0x100·k + 0x10·j + i`; (0, 0, 0) = 91,274,119,533,887,488; (1, 1, 2) = 0x0144455400010211 | `ENTITY` = 1 and `ConstructId(t, n) = (n & 0x00FFFFFFFFFFFFFF) \| t << 56` (VERIFIED `MyEntityIdentifier.cs`); 0x444554 is "DET". Derived from `siteVersion` and the slot (not a field of the `site_v1.py` record); deterministic, so landing-gear locks survive reloads; all 34 checked free with `MyEntityIdentifier.ExistsById` before the first tile, and `AddVoxelMap` returns null for an id that exists: either refuses the terrain (fail-safe, §7); the plugin logs all 34 (`DE.Terrain TILE i,j,k id=0x… min=(…)`) with `siteVersion` |
| `terrain.worldMatrix` | `MatrixD.CreateTranslation(tileMin + (512, 512, 512))`, identity rotation; `tileMin` = C + the lattice min corner (doubles) | `MyVoxelBase.InitVoxelMap` puts the box centre at the translation, so `PositionLeftBottomCorner` = `tileMin` exactly (VERIFIED). Storage name `DE.Terrain.<i>.<j>.<k>.s<siteVersion>`; `lazyPhysics: true` (rigid bodies still come at `OnAddedToScene`, VERIFIED) |
| `terrain.saved` | false (`MyEntity.Save = false`) | set right after `AddVoxelMap` returns, in the same game-thread `BeforeStart` call; `MyEntities.Save` and the session's voxel-map array skip non-saved maps (VERIFIED). Tiles are recreated each session; no `.vx2`, no provider id in a save |
| `terrain.useVoxelOffset` | false | the world matrix places the min corner exactly at C + lattice offset (SE would otherwise add 0.5 m, `MyPerGameSettings.OffsetVoxelMapByHalfVoxel`) |
| `terrain.edits` | blocked (v1) | edit guards: §3.1 |
| `snSource` | Steam build 20241558; `index.txt` lines 2–5 `4096 3200 4096` / `128 100 128` / `32` / `5 5 5`; 5,416 `.optoctrees`, 1,203,085,204 bytes; batch rows 7–19 (y −1,920…+160); `manifestHash` 0x2cc277ce91763285 | VERIFIED on this PC; rule and folder: §3.1 "Source manifest". `manifestHash` is new in S4 and not a field of the `site_v1.py` record (so `siteHash` is unchanged); any mismatch: no tiles and the fail-safe |
| `siteHash` | FNV-1a 64 over the UTF-8 bytes of `json.dumps(site_v1.record, sort_keys=True, separators=(",", ":"))`; today `0x53ab8e77d68103ab` over 1,506 bytes | generated by the tool and compiled into `SiteV1`; recomputed whenever the record text changes (O1) |

### World settings (set at world creation; world-wide

| Setting | Value | Checked at bind time |
|---|---|---|
| `EnableSunRotation`, `SunRotationIntervalMinutes` | true, 20 (default 120; the day is six times faster on every planet of the world) | values read |
| Environment `SunAzimuth`, `SunElevation` | values that make `SunProperties.BaseSunDirectionNormalized` = (0, 0, 1) | `\|axis·U0\| < 1e-3`, `\|base·axis\| < 1e-3` with `axis = MySector.SunProperties.SunRotationAxis` |
| Sector weather component `BaseSunDirection` | zero or (0, 0, 1) (it overrides the environment's direction when saved) | read by reflection and logged |
| `EnableOxygen`, `EnableOxygenPressurization` | true (S7 interiors need airtight rooms; SE creates `MyCubeGrid.GridSystems.GasSystem` only with both) | read and logged at bind (the `world{…}` part of the site line); without both: `DE.Interior OFF pressurization disabled` once per world and the interior flag stays false (§3.2, S7.N1b) |
| Game mode, `DestructibleBlocks` | Survival, true (damage and the crush zone need them; the floor guard does not) | `DE.Abyss DAMAGE-INACTIVE` / combat refused logged otherwise |

When the sun checks fail, time sync is refused (C6 flag) and Subnautica drives only its sky from the actual axis and
declination (`sunriseHourAngleRad`).

### Runtime values and validation
- The plugin's session component registers the Water Mod message handler (50271) in `LoadData`; the Water Mod sends
 its delegate dictionary once, from its own `BeforeStart`, and `SendModMessage` does not buffer. On
 arrival: `VerifyVersion(22, "DeepEngineers")`, `WaterData.Version` read by reflection (≠ "3.23": warning), every
 delegate key we use checked for presence and type. Consequence of the version pin (S1, D58/D106): crush damage and
 current speed have no API getter (D57) and are read by reflection from `WaterComponent.Settings`, which is pinned to
 3.23; another version leaves them unverifiable, so the site is refused after 600 ticks (`crushDamage unverifiable`)
 even though the version string itself is only a warning; the launcher preflight fails on another Workshop version.
- `C` = `planet.PositionComp.GetPosition` (double), read once per session after `MyEntities.Load`; the tiles need
 only C and are created in `BeforeStart`.
- Validation runs at the first `UpdateBeforeSimulation` and retries for up to 600 ticks, logging `DE.Site WAIT|OK|
 REFUSE <reason>`: API received (else `REFUSE API not received`); one `DE_4546B`; `HasWater`; `Rs =
 GetPhysicalData(planet).Item2` equal to 60000.00390625f bit for bit (the API key is `GetPhysicalData`, S1 D57);
 `GetDepth` equal to `y` within 0.01 m at five fixed SN points (the anchor and the §3 conformance points) and at the
 camera while `|y|` ≤ 10 km (S1 D66); `WaveHeight = 0`, `TideHeight = 0` (`GetWaveData`/`GetTideData`),
 `CrushDamage = 0`, `CurrentSpeed = 0` (reflection only: the API has no getter for them, S1 D57); the sun checks
 above, whose failure refuses only time sync (C6 flag, sky-only), never the display bridge. Rs and crush damage are
 re-checked every 10 s. A failure of any other check refuses the display bridge only (the tiles stay). Real run: `DE.Site OK … after 1 ticks` in every load of the lab world (S1.N13), and the S1.M5 mutant (Rs + 1 ulp)
 refused at the 10 s re-check.
 (`SetWaterRadius`; API 22 has no other setter) and prints the Water Mod commands for crush, wave, tide and current
 `de.site rebind` re-validates. A changed `WaterConfig.xml` never reaches an existing world (the sea comes from
 the save after the first spawn), which is why the values are validated rather than trusted.
- The plugin logs the full record (constants, C, Rs, tile list with entity ids, sun axis) at start, and every tool or
 log line carries `siteVersion`.

** The values above stay the spawn-time configuration and the oracle
> sessions' water (the launcher sets `DEEPENGINEERS_WATERLOOK=off` for every scripted session). In play the SE plugin
> applies `WaterLook.Defaults` once per world when the site is bound: texture JWater2, opaque, lit, no foam, waves
> 0.5 m / speed 0.02 / scale 0.5, colour intensity 5, specularity 4, specular intensity 1, ambient 0.2 (`/de water`
> tunes them live, `reset` restores the spawn-time values). With 0.5 m waves the physical surface moves with the Water
> Mod's wave; the view machine and the swim model read that surface, so a camera floating at the surface may swap
> pictures more often than over the flat contract water.
>
> **S7t T4: the look re-tuned toward Subnautica's sea at the band** (`WaterLook.Defaults`,
> applied at bind as above; `/de water` stays live): colour intensity 5 → **8**, ambient 0.2 → **0.3**, specular
> intensity 1 → **0.8**; texture, opacity, lighting, foam, waves and specularity unchanged.
> 23:05 (near water SE (27,68,104) L62 vs Subnautica (65,97,122) L92; far water (38,72,96) L67 vs (129,155,187) L152).
> Mapping: above water the Water Mod lights its billboards with `max(dot(up, sun), AmbientColorIntensity) ×
> ColorIntensity` (VERIFIED mod source `WaterRenderSessionComponent`); the near-water ratio is ×1.48 on screen and ×2.4
> in linear light, SE's eye adaptation pulls toward the lower bound with the sea filling half the frame → ×1.6
the ambient floor keeps a low-sun sea lit as Subnautica's sky reflection does
> specular intensity = Subnautica's `WaterSurface.sunReflectionAmount` 0.8
`FogColor` is applied by the mod under water only (where `Site` shows
> Subnautica's picture): not a band field, the world's value stays. The remaining hue gap (SE's sea more saturated) and
> the far-water haze are T5's (SE's grade and atmosphere at the band, §3.4) and T2's (Subnautica's horizon). Settings
> touched: the Water Mod's in-memory `WaterSettings` / `PlanetConfig` of the bound planet only (saved with the world by
> the mod, as before); `Data/WaterConfig.xml` keeps the contract values only (it seeds new worlds, the look applies at bind).

### Map
- The map is the world-mapping map (`docs/recon/probe-world-mapping.md` §3.2–3.4; the oracle is
 `tools/worldmap/worldmap_ref.py`), with this site's anchor and Rs.
- SN y = |P − C| − Rs, the altitude above the sea sphere, which equals SE's sea-level elevation to within 4 mm.
- SN horizontal position = Rs·θ along the azimuth from U0.
- Depth = −SN y.
- The WM-1 shared vector file is generated with this site (`site_v1.SITE_V1`), not with `worldmap_ref.EXAMPLE`.

### Conformance vectors (test centre C = (−1,000,000, 250,000, 1,500,000); doubles; tolerance 1e-6 m)

| SN point | SE world | Horizontal scale Rs/r |
|---|---|---|
| (0, 0, 0) | (−1000000.000000, 250000.000000, 1560000.003906) | 1.000000 |
| (−1029, −150, −1116) | (−998973.681996, 251113.091246, 1559830.850460) | 1.002506 |
| (198, −1657, −518) | (−1000192.529159, 250503.687396, 1558340.511965) | 1.028401 |
| (346, 155, 906) | (−1000346.878728, 249091.699052, 1560147.145897) | 0.997423 |
| (2048, −500, 2048) | (−1002030.144690, 247969.855310, 1559430.694846) | 1.008403 |

### Open items that may change this section
- E1 (gap-2 §9, S4.G2-E1) has to confirm that Subnautica draws flat faces where its terrain data stops. If it does
 not, add a "below the local cut depth" rule under the crater footprint.
- S4.A-T1 may move `terrain.tile` to 2048 (lattice 2 × 2 × 2, new entity ids) if 1024³ provider maps are too costly.
- The Subnautica sample offset of the content mapping (§3.1: voxel centre, HYPOTHESIS with offline evidence) is decided
 by S4.A-T2; SE's iso byte by S4.A-T0b. Neither is a site-record field.

### 3.1 Terrain provider identity and contract (not part of the site version)

Settled against the SE 1.210.014 and Subnautica 83031 decomp on (S4 "CONTRACT first"). Decomp paths are
SE types are in `se/Sandbox.Game`
`se/VRage.Game`, `se/VRage`, `se/VRage.Math` unless named; `se-extra/HavokWrapper` holds the Havok wrappers.

**Identity and registration.**
- `SnTerrainProvider` (namespace `DeepEngineers.SE.Terrain`, in the plugin assembly `plugin`) carries
 `[MyStorageDataProvider(45460)]`

 It has a public parameterless constructor

- Registration: `MyOctreeStorage.RegisterTypes(Assembly[])` (public static) iterates `assembly.GetTypes` and calls
 `Dictionary.Add` on the private statics `m_attributesById` (`Dictionary<int, MyStorageDataProviderAttribute>`) and
 `m_attributesByType` (`Dictionary<Type, MyStorageDataProviderAttribute>`), so a second registration throws
 `ArgumentException`, and a type of the plugin that cannot load throws `ReflectionTypeLoadException`
 The static constructor registers the executing assembly, `MyPlugins.GameAssembly`,
 `SandboxAssembly` and `MyPlugins.UserAssemblies`; Pulsar does not fill `UserAssemblies`
 so nothing registers us automatically.
- Guard (once per process, in `Plugin.Init`, before any world loads): read `m_attributesById` by reflection. Id 45460
 absent → `RegisterTypes(new[] { typeof(SnTerrainProvider).Assembly })` in try/catch, then read again. Present with
 `ProviderType == typeof(SnTerrainProvider)` → registered (`DE.Terrain PROVIDER registered id=45460`). Present with
 another type, an exception, or the field not found → `DE.Terrain PROVIDER FAIL <reason>` once; the terrain gate
 then refuses in every DE world of the process (§1.2 condition 4). The registration is global and permanent for the
 process; tiles are never saved, so the id never reaches a save (S4.G2-E2 checks the files).
- One provider instance per tile (requests arrive in storage-local coordinates; each instance holds its slot, its
 `tileMin` and the session's material table), all over one shared, ref-counted `SnOctreeSource` (memory-mapped batch
 files, lazy per-batch octree index and state). The session component part holds one reference while it creates the
 tiles and drops it in `UnloadData`; each provider holds one from construction until its `Close`.

**Tile creation (SE calls, VERIFIED signatures).**
- Storage: `new MyOctreeStorage(IMyStorageDataProvider dataProvider, Vector3I size)` with `size = (1024, 1024, 1024)`:
 the size becomes `GetNearestBiggerPowerOfTwo(max)` = 1024
 tree height 6, and `ResetInternal` installs one `MyProviderLeaf` per data type at LOD 10 covering the whole
 cube.
- Map: `MyWorldGenerator.AddVoxelMap(string storageName, MyStorageBase storage, MatrixD worldMatrix, long entityId = 0L,
 bool lazyPhysics = false, bool useVoxelOffset = true)` with `storageName = "DE.Terrain.<i>.<j>.<k>.s<siteVersion>"`,
 `worldMatrix = MatrixD.CreateTranslation(tileMin + new Vector3D(512, 512, 512))` (identity rotation: the lattice is in
 world axes), `entityId` = §3 `terrain.entityId(i, j, k)`, `lazyPhysics: true`, `useVoxelOffset: false`. It returns
 null, logging `CRITICAL-VOXEL MAP!!!`, when `MyEntityIdentifier.ExistsById(entityId)`
 otherwise it sets `EntityId`, `DelayRigidBodyCreation`, calls `MyVoxelMap.Init`, then
 `MyEntities.Add` and `RaiseEntityCreated`. The world matrix translation is the box centre:
 `PositionLeftBottomCorner = Translation − SizeInMetresHalf` = `tileMin` exactly; `useVoxelOffset: true` would add
 0.5 m on every axis (`MyPerGameSettings.OffsetVoxelMapByHalfVoxel`, VERIFIED `MyVoxelBase.InitVoxelMap`).
 `lazyPhysics` only delays the rigid bodies to `OnAddedToScene`, which `MyEntities.Add` runs at once

- Right after each map is returned, on the game thread inside `BeforeStart`: `map.Save = false`
 `MyEntities.Save` and `MySession`'s voxel map array skip it
 Nothing can save between `MyEntities.Add` and this
 line (same game-thread call).
- Ids: `MyEntityIdentifier.ConstructId(ID_OBJECT_TYPE type, long uniqueNumber)` =
 `(uniqueNumber & 0x00FFFFFFFFFFFFFF) | (long)type << 56`, `ENTITY = 1`;
 `SiteV1.TileEntityId` implements it (no SE call needed). All 34 ids are checked with
 `MyEntityIdentifier.ExistsById(long)` before the first map is created.
- Teardown: SE closes the tiles with the world

**Member table** (`VRage.Voxels.IMyStorageDataProvider`, VRage.Game.dll, VERIFIED interface). SE calls the read
members of our tiles through `MyOctreeStorage` and `MyProviderLeaf` (VERIFIED `MyProviderLeaf.cs`) under the storage's
shared lock (`StorageLock.AcquireSharedUsing`), so `Close` (exclusive lock) never overlaps them; reads of one tile run
concurrently on many threads. The one exception is `MyLaserAntenna`, which calls `Intersect(ref LineD)` on
`Storage.DataProvider` directly from the update thread, without the lock: hence the source's own reader guard
(Threading below).

| Member (exact signature) | When SE calls it (thread, LOD, data, flags) | What the provider must do |
|---|---|---|
| `void ReadRange(ref MyVoxelDataRequest request, bool detectOnly = false)`, `detectOnly` false | SE's real read path: `MyStorageBase.ReadRange(MyStorageData, MyStorageDataTypeFlags, int lodIndex, Vector3I min, Vector3I max, ref MyVoxelRequestFlags)` → `MyOctreeStorage.ReadRangeInternal` → `MyProviderLeaf.ReadRange` (VERIFIED). Callers: physics cells (`MyVoxelPhysicsBody.CreateMesh`: LOD 0, range `8c − 1 … 8c + 10`, content then material, flags `SurfaceMaterial \| ForPhysics \| Postprocess \| UseNativeProvider`; worker threads of `MyPrecalcJobPhysicsPrefetch` and the blocking `Parallel.For` of `RequestShapeBlocking` inside the Havok step), render clipmap cells (worker threads, LOD 0…10), `MyVoxelBase.GetIntersectionWithLine` (LOD 0 content, caller's thread), cached 8³ chunk reads. `lodIndex > 10` is never forwarded (`ReadRangeInternal` reads only `lod ≤ treeHeight + 4`). `MyOctreeStorage` clamps the range to the tile (`[0, 1023 >> L]`) and fills the outside itself (content 0, material 255, `FillOutOfBounds`), so the provider only sees in-tile ranges | Inputs: `Lod`, `MinInLod`, `MaxInLod` (inclusive, LOD coordinates of this tile), `Offset` (where `MinInLod` goes in `Target`), `RequestedData` (`Content`, `Material` or both; write only those, and only into `Target[type]` arrays that exist), `RequestFlags` (input; hints, may be ignored: our material is cheap, so `SurfaceMaterial` and `ConsiderContent` change nothing). Write every requested sample of the range at `Target.ComputeLinear(p − MinInLod + Offset)`. Output: `request.Flags = RequestFlags & ~(EmptyData \| FullContent \| ContentChecked \| ContentCheckedDeep \| OneMaterial)`, plus `EmptyData` iff every written value of every requested type is its empty value (content 0, material 255) and `FullContent` iff content was requested and every content byte written is 255. `MyOctreeStorage` keeps only `EmptyData` and `FullContent` from the provider, AND-ed with its own; the mesher returns an empty or full cell on them without meshing (VERIFIED `MyDualContouringMesher.Calculate:143-150`), so a wrong `EmptyData` here is a hole (S4.M8) |
| same, `detectOnly` true (`Target` is null) | Only `MyProviderLeaf.TryGetUniformValue` (whole leaf: LOD 10, `MinInLod = MaxInLod = (0,0,0)`), called by the write/sweep traversals of `MyOctreeStorage` (`WriteRangeOps`, `SweepRangeOps`), i.e. edit paths, which the guards below block on our tiles (VERIFIED callers). Never on a read path | Never touch `Target`. `request.Flags = RequestFlags` without the five bits above, plus `EmptyData` (and `ContentChecked`) only when the box classifies `Empty`, `FullContent` (and `ContentChecked`) only when it classifies `Full` (classification below); otherwise none. For a material request `EmptyData` means "no solid in the box" (`TryGetUniformValue` then answers 255) |
| `void ReadRange(MyStorageData target, MyStorageDataTypeFlags dataType, ref Vector3I writeOffset, int lodIndex, ref Vector3I minInLod, ref Vector3I maxInLod)` | Only `MyProviderLeaf.GetFilteredValue` (one sample at the leaf's LOD, edit/sweep traversals; VERIFIED) and our own tools | The request path with `Target = target`, `Offset = writeOffset`, no flags; same values |
| `ContainmentType Intersect(BoundingBoxI box, int lod)` | `MyOctreeStorage.IntersectInternal` (under the shared lock; `MyStorageBase.Intersect` flushes its write cache first and answers `Disjoint` when closed). **`box` is in LOD-0 storage voxel coordinates, inclusive, already intersected with the leaf box `[0, 1024]`**; `lod` is the caller's meshing LOD (a hint). Callers: physics (`MyVoxelPhysicsBody` prefetch loop on the update thread, `UpdateAfterSimulation10`, for the prediction boxes of moving grids and floating objects; `PrefetchShapeOnRay` and its jobs never call it, VERIFIED: the box of all missing 8 m cells in a prediction box, `(min·8, (max + 1)·8)`; **any answer other than `Intersects` empties every one of those cells without meshing**, VERIFIED `), the clipmap (`MyVoxelClipmap.ApproximateCellConstitution`: the LOD-0 bounds of an LOD-L cell with `Min − 1`; `Disjoint` → empty, `Contains` → full), `MyVoxelBase.IntersectStorage`. The planet provider also ignores `lod` and treats the box as LOD-0 coordinates | `Classify(box)` (below): `Empty` → `Disjoint`, `Full` → `Contains`, `Mixed` → `Intersects`. Never `Disjoint` or `Contains` unless proven; never guess |
| `bool Intersect(ref LineD line, out double startOffset, out double endOffset)` | `MyOctreeStorage.IntersectInternal(ref LineD)` (the line already clipped to the leaf box; it then reads `line.From`/`line.To` back) and `MyLaserAntenna` line of sight. `MyPhysics.CastLongRay` uses it for planets only (it skips voxels whose `GetOrePriority != −1`; `MyVoxelMap` returns 1, VERIFIED). `line` is in storage-local metres (`world → WorldMatrixInvScaled + SizeInMetresHalf`), i.e. LOD-0 voxel units from `tileMin`; offsets are normalised to the given line | Conservative and as tight as cheap: clip the line to `[0, 1024]³`, walk it in steps of ≤ 16 m, `Classify` each step's box (expanded by the step's own sag, below); `false` when every step is `Empty`; otherwise `true`, `startOffset`/`endOffset` = the first and last non-empty step's ends as fractions of the given line, and `line` set to that sub-segment. A line over open water inside a tile box therefore does not block a laser antenna |
| `void ReindexMaterials(Dictionary<byte, byte> oldToNewIndexMap)` | Only `MyOctreeStorage.LoadInternal` when a saved storage's material table is remapped: never for our tiles | No-op (indices are resolved by name each session; below) |
| `int SerializedSize { get; }`, `void WriteTo(Stream stream)` | `MyOctreeStorage.WriteDataProvider` when the storage is saved or copied (`Save`, `Copy`; `Copy` runs only for `Shared` storages, ours are not, VERIFIED `MyVoxelBase.BeforeContentChanged`) | 0 / writes nothing; never throw |
| `void ReadFrom(int storageVersion, Stream stream, int size, ref bool isOldFormat)` | `ReadDataProvider` when a storage carrying id 45460 is loaded (never: no tile is saved) | Skip `size` bytes, stay inert (no slot: every read answers empty, every `Intersect` `Disjoint`); never throw |
| `void PostProcess(VrVoxelMesh mesh, MyStorageDataTypeFlags dataTypes)` | `MyDualContouringMesher.Calculate` after every non-empty mesh of a storage with a provider (render and physics, worker threads; VERIFIED ``). `VrVoxelMesh` is `VRage.Voxels.VrVoxelMesh` in **VRage.NativeWrapper.dll** (VERIFIED by reflection on Bin64), so the plugin references it (`Private=false`) | No-op, thread-safe |
| `void DebugDraw(ref MatrixD worldMatrix)` | `MyOctreeStorage.DebugDraw` in the `Content_DataProvider` debug mode only | No-op |
| `void Close` | Once per storage from `MyStorageBase.CloseInternal`, under the exclusive lock (VERIFIED) | Idempotent (`Interlocked` flag); releases this instance's reference on the shared source; the last reference unmaps every view and closes the files. After `Close` every read answers empty and `Intersect` answers `Disjoint` without touching the source |

**Classification `Classify(box)`** (used by `Intersect(box)`, `detectOnly` and line walks; box inclusive, LOD-0 tile
voxels). (1) The part of the box outside `[0, 1023]³` is empty for SE (`FillOutOfBounds`), so such a box is never
`Full`. (2) Map the 8 corners `tileMin + corner` through F (`SiteMap.SeToSn`) and take their SN AABB; expand it on every
axis by `L²/(2·rMin) + 0.01` m, where L is the box's largest extent in metres and rMin the smallest `|corner − C|`
(the implementation, `TileSampler.Classify`, uses the distance from C to the box, which is never larger, so its margin is
never smaller)
(this bounds the curvature of the map over the box: the second derivative of `|P − C|` along a line is at most `1/r`;
S4.N3 checks the bound by dense sampling of random boxes). (3) The samples that can enter a stencil of a point of that
AABB are those with `P(v)` within 1 m of it on every axis; look up the states of the octrees holding them (`P(v)`: content mapping below; `Missing` =
no batch file or outside the SN world; `Empty`; `Solid`; `Mixed`, as `sn_octree_stats.py` classifies them). All `Missing`/`Empty` → `Empty`; all `Solid` and (1) does not apply → `Full`;
anything else → `Mixed`. An octree with one near-surface leaf is `Mixed`; an octree subdivided into solid leaves is
`Mixed` too (conservative). Refinement: a `Mixed` octree is resolved
to the leaves that hold those samples (exact, since a sample's value is its leaf's): all far empty → as `Empty`, all far
solid → as `Solid`, any near-surface leaf → `Mixed`. Open water inside the 32 m octree that holds the seabed then
classifies `Empty` (laser antennas, the physics prefetch); the soundness check of S4.N3 (never `Empty`/`Full` over a box
whose LOD-0, 1 or 2 bytes are not uniform) passes on synthetic batches and on the Safe Shallows. While closing, an
octree of a batch not yet indexed stays `Mixed` and leaves are not read.

**Threading and budget.** Thread-safe and, in the steady state, allocation-free: per-thread scratch is `[ThreadStatic]`
and allocated once per thread; the only other allocations are a batch's memory map and view, once per batch file per
session on its first touch (≤ 5,416). Lazy per-batch index: on first touch, the batch file's 125 node counts are walked
(the octrees of out-of-world positions are not stored) into preallocated tables (offset, node count, state per octree; about 11.5 MB for the 1,638,400 octrees, the state
array alone 1.6 MB),
published with a volatile "ready" flag; two threads indexing the same batch at once write identical values (benign).
`RequestShapeBlocking` runs `ReadRange` inside the Havok step, so the worst case per 8 m physics cell is < 1 ms and
`ReadRange` p99 ≤ 2 ms (measured by S4.A-T1(3)). Statistics (calls, µs histogram, batches mapped, bytes mapped) are
kept with `Interlocked` counters and logged as `DE.Terrain STATS`. Unmapping: the last `Release` sets the source's
`closing` flag, waits until its in-flight reader count (`Interlocked`, taken around every access to mapped memory) is
zero, then unmaps; an access that finds `closing` set touches no mapped memory and answers empty (reads) or `Mixed`
(classification of a batch not yet indexed). The state tables are managed arrays and stay readable.

**Source manifest**.
- Folder: `<Subnautica>\Subnautica_Data\StreamingAssets\SNUnmanagedData\Build18\CompiledOctreesCache`, where
 `<Subnautica>` is the plugin config key `subnauticaDir` (reserved: the S4 build does not read it yet), else the
 environment variable `DEEPENG_SUBNAUTICA`, else
 `C:\Program Files (x86)\Steam\steamapps\common\Subnautica`
 Files are
 read in place and never copied.
- `index.txt` (next to the folder): its lines 2–5 are `4096 3200 4096`, `128 100 128`, `32`, `5 5 5`

- Build: `buildid` of `<Subnautica>\..\..\appmanifest_264710.acf` = **20241558** (VERIFIED on this PC).
- Files matching `compiled-batch-<x>-<y>-<z>.optoctrees`: **5,416**, **1,203,085,204 bytes**, batch rows y **7…19**,
 x and z 0…25, and nothing else in the folder (VERIFIED on this PC; equal to the ROADMAP's numbers).
- `manifestHash` = FNV-1a 64 (offset 0xcbf29ce484222325, prime 0x100000001b3) over the UTF-8 text made of one line
 `<file name>\t<size in bytes>\n` per batch file in ordinal (byte-wise) order of the names: **0x2cc277ce91763285** over
 212,410 bytes (VERIFIED on this PC).
- Any difference (folder or `index.txt` missing, other lines, other build, count, bytes, rows or hash) →
 `DE.Terrain REFUSE source <field> found=<v> expected=<v>`, no tiles, fail-safe (§7). The manifest is measured once per
 process (a directory listing, ~5,400 `stat`s) and logged as `DE.Terrain SOURCE path=… build=… files=… bytes=…
 rows=7..19 manifestHash=0x… index=ok`. A batch whose int32 version is < 4 or whose 125 node counts do not consume the
 file exactly is treated as missing (Subnautica's own rule for < 4, VERIFIED `BatchOctrees.LoadOctrees`) and logged
 once as `DE.Terrain BATCH-BAD <name>` (never seen on this PC: every file consumed exactly). After a Subnautica update
 py …` and the listing hash) and the numbers are
 re-pinned here: build, files, bytes and rows are fields of the `site_v1.py` record (`SiteV1`), so `siteHash` and
 `siteVersion` follow §3 change control; `manifestHash` and the `index.txt` lines are pinned in the Terrain library's
 `SnSourceManifest` only.

**Octree format facts the C# reader matches**

- World: 4096 × 3200 × 4096 voxels of 1 m, origin `(−2048, −3040, −2048)`; 128 × 100 × 128 octrees of 32 voxels;
 batches of 5 × 5 × 5 octrees (160 m), 26 × 20 × 26 batch slots.
- Batch file: int32 LE version (must be ≥ 4; all are 4), then for each octree of the batch **in x-outer, y, z-inner
 order** (`Int3.Range`/`Int3.Next` increments z first), and only if its octree index `(bx·5 + ix, by·5 + iy, bz·5 + iz)`
 lies inside `(128, 100, 128)`: a uint16 LE node count n, then n nodes of 4 bytes. n = 0 is an empty octree.
- Node: byte 0 `type`, byte 1 `density`, bytes 2–3 uint16 LE `firstChild`; `firstChild == 0` is a leaf. The root is
 node 0 (size 32). The 8 children of a node are the consecutive nodes `firstChild + c` with **child order
 `c = 4·xbit + 2·ybit + zbit`** (x is the most significant bit; `Octree.GetNodeId`, `OctNode.ChildDX/DY/DZ`), where a
 bit is 1 for the upper half of the parent along that axis. A leaf of size s covers an s³ cube with one (type,
 density). The oracle also stops at size 1 (`fc == 0 or size == 1`); the reader does the same.
- Density: 0 = "far" (solid iff `type > 0`); otherwise near-surface, signed distance `(density − 126) / 126` in voxels,
 positive inside, solid iff `density ≥ 126` (`OctNode.IsBelowSurface`; `EncodeDensity(dist) = floor(dist·126) + 126`
 for `|dist| ≤ 1`, else 0). Near leaves larger than one voxel hold one constant distance over the leaf (as the oracle
 rasterises them; what the native rasteriser does with them is open: collision probe open question 3).
- Missing batch file → every octree of it is empty (`LoadOctrees` returns false → `ClearOctrees`), which is the plug.
- Voxel `(vx, vy, vz)` (world index) occupies the SN cube `origin + v … origin + v + 1`; S4.M1's mutants are a sign
 flip of the distance, a 1-voxel offset, swapped x/z, and the child order `4·zbit + 2·ybit + xbit`.

**Content mapping** (SE content byte from Subnautica's octrees; first pass, tuned only through the checks named).
- Samples: `s(v) = +1` for a far solid voxel, `−1` for a far empty voxel or no data (missing batch, empty octree,
 outside the SN world), `(density − 126)/126` for a near voxel. Sample position in SN metres:
 `P(v) = (−2048, −3040, −2048) + v + o`, with **`o = (0.5, 0.5, 0.5)` (the voxel centre; HYPOTHESIS)**. Offline evidence: at SN (0, 0) the centre convention
 gives −17.204 m against the measured −17.212 m (corner convention −17.853 m); at the grandReef spot (−1029, −1116)
 −235.254 m against −235.138 m (corner −235.500 m). The exact roots of the trilinear field `D` below along those columns
 (`tools/worldmap/worldmap_ref.py` `OctreeColumns`, the S4 analyzer's reference; `python -I tools/worldmap/worldmap_ref.py
 s4check`) are −17.208 m and −235.133 m with the centre convention (corner: −17.853 m, −235.500 m), within 5 mm of both
 layer-30 hits. S4.A-T2 decides it (a 0.5 m error fails its 0.3 m P95). The
 constant is `SnField.SampleOffset` (C#) and `SAMPLE_OFFSET` (`tools/sn-octrees/sn_field.py`, the Python reference).
- Field: `D(p)` = trilinear interpolation of `s` over the 8 samples around the SN point p (voxel units = metres).
- SE sample: tile index `i` at LOD L sits at the SE world point `w = tileMin + i·2^L` (no half voxel; `tileMin` =
 C + lattice min corner, doubles); `p = F(w)` (`SiteMap.SeToSn`, per sample; an affine approximation per request is
 allowed only if its error over the request box is ≤ 1 mm, checked by S4.N3).
- LOD rule: if the 8 stencil samples are all far and of one sign, `d = ±1` (saturated at every LOD); otherwise
 `d = clamp(D(p) / 2^L, −1, 1)` (signed distance in LOD-voxel units, as `MyPlanetShapeProvider` does). At LOD 0 both
 branches equal `clamp(D(p), −1, 1)`. Physics uses LOD 0 only (`MyVoxelPhysicsBody.UseLod1VoxelPhysics = false`,
 VERIFIED); render LODs ≥ 1 point-sample the LOD-0 field, so their surface can be off by up to `2^(L−1)` m where
 Subnautica's band is ±1 voxel (accepted; coarse octree nodes as an LOD source are a later refinement).
- Byte: `content = (byte)((d · 0.5f + 0.5f) · 255f)` (float, truncation), the exact expression of
 `MyPlanetShapeProvider.CalculateDistanceFieldInternal` (VERIFIED), so d = 0 gives 127. SE's binary inside test is
 `content ≥ 127` (`MyStorageData.ComputeContentConstitution`, VERIFIED) and `MyVoxelConstants.VOXEL_ISO_LEVEL = 127`;
 the byte value at which SE's native mesher places the surface is what S4.A-T0b records. Offline S4.A-T0b
 ( a synthetic plane through `TileSampler`, tile (2, 2, 1), LOD 0–2, 2,048 columns each): every served byte
 equals the plane's; the iso at byte 127 is unbiased (mean error ≤ 5e-5 m, max ≤ 0.5/126.5 · 2^L m) while byte 127.5
 is biased by −0.5/127.5 · 2^L m; on Subnautica's own sample grid `content ≥ 127` reproduces the solid rule
 `density ≥ 126` exactly. S4.A-T0 therefore scores the content at iso 127; the in-SE `de.terrain at0b` still records
 the native mesher's own value.
- Resampling: SE's samples sit at an arbitrary phase with respect to
 Subnautica's sample grid. On Subnautica's grid the content iso-surface is within P95 ≤ 3.6 mm of `D = 0`; on the grid
 offset by (0.37, 0.21, 0.45) m it is P95 0.06–0.16 m away, because Subnautica's ±1-voxel distance band saturates at
 ±1 at the next sample and SE's linear edge interpolation crosses that kink (at SN (0, 0) SE's content surface is
 −17.175 m against `D`'s −17.208 m). This is inherent to resampling the first-pass field; it is inside the S4.A-T1
 (0.25 m) and S4.A-T2 (0.3 m) budgets, not inside the ROADMAP's A-T0 0.05 m on an offset grid (open, reported).
- Material: `content == 0` → 255 (no material); otherwise the depth-band material of the sample's SN y (table below),
 whatever the request flags say.
- First-pass depth-band table (gameplay choice; Subnautica's 209 terrain types are not used
 yet, and SE ores replacing Subnautica's are a ROADMAP §12 idea). Names VERIFIED in this install's
 `Content\Data\VoxelMaterials_*.sbc`:

 | SN y of the sample | SE voxel material (`SubtypeId`) |
 |---|---|
 | y ≥ +2 | `Grass` (island tops above the sea) |
 | −40 ≤ y < +2 | `Sand_02` (shallows, beaches) |
 | −200 ≤ y < −40 | `Stone_01` |
 | −500 ≤ y < −200 | `Stone_02` |
 | −900 ≤ y < −500 | `Stone_03` |
 | −1,300 ≤ y < −900 | `Stone_04` |
 | y < −1,300 | `Stone_05` (the deepest floor and the crust underside) |

 Resolved once per session in `BeforeStart` with `MyDefinitionManager.Static.GetVoxelMaterialDefinition(string name)`
 → `.Index`; an unresolved name falls back to
 `Stone_01`, then to `GetDefaultVoxelMaterialDefinition` (index 0), and logs `DE.Terrain WARN material <name>`. The
 seven indices are immutable for the session and shared by the 34 providers.

**Edit guards** (Harmony prefixes, installed in `Init` with the other patches, each target resolved by exact
signature and logged `DE.Terrain GUARD <path> ok|MISSING`; a guard returns false (skips the original) when the target
storage's `DataProvider is SnTerrainProvider`, sets out values and `__result` as stated, counts the attempt per path (`Interlocked`; prefixes can run on worker
threads) and logs `DE.Terrain EditBlocked <path> n=<count>` at most once per second per path). Paths are VERIFIED in
the decomp:

| Path name | SE entry (exact signature) | Reached from | Prefix result |
|---|---|---|---|
| `cutout` | `MyVoxelGenerator.CutOutShapeWithProperties(MyVoxelBase voxelMap, MyShape shape, out float voxelsCountInPercent, out MyVoxelMaterialDefinition voxelMaterial, Dictionary<MyVoxelMaterialDefinition, int> exactCutOutMaterials, bool updateSync, bool onlyCheck, bool applyDamageMaterial, bool onlyApplyMaterial, bool skipCache)` (static void) | hand drill (`MyDrillBase.TryDrillVoxels` → `MyVoxelBase.CutOutShapeWithPropertiesAsync`), warhead and other explosions (`MyExplosion.CutOutVoxelMap`, `MyVoxelBase.VoxelCutoutSphere_Implementation`) | `voxelsCountInPercent = 0`, `voxelMaterial = null`, dictionary untouched → no ore, no change |
| `shipdrill` | `MyShipMiningSystem.RequestCutOut(MyShipDrill drill, bool applyDiscardingModifier, bool applyDamagedMaterial, Vector3D hitPosition, MyVoxelBase voxel)` (instance void) | ship drills (`MyDrillBase.TryDrillVoxels` → `MyShipDrill.TryDrillVoxel`). The mining system counts the cut-out materials before its `WriteRange` (VERIFIED `ClusterCutOut.CutOutChunk` then `m_storage.WriteRange`), so the `WriteRange` guard alone would hand out ore: this guard is required | skip (no request queued) |
| `explosion` | `MyVoxelGenerator.CutOutSphereFast(MyVoxelBase voxelMap, ref Vector3D center, float radius, out Vector3I cacheMin, out Vector3I cacheMax, bool notifyChanged)` (static bool) | grid explosion batches (`MyCubeGrid`, in a `Parallel.For`: the prefix is thread-safe), `MyVoxelBase.PerformCutOutSphereFast` | `__result = false`, `cacheMin = cacheMax = Vector3I.Zero` |
| `meteor` | `MyVoxelGenerator.MakeCrater(MyVoxelBase voxelMap, BoundingSphereD sphere, Vector3 direction, MyVoxelMaterialDefinition material)` (static void) | ModAPI `IMyVoxelMaps.MakeCrater`, `MyVoxelBase.CreateVoxelMeteorCrater(_Implementation)`, our `de.terrain crater`. SE 1.210's `MyMeteor.CreateCrater` makes no voxel edit (particles and sound only, VERIFIED) | skip |
| `voxelhand` | `MyVoxelBase.UpdateVoxelShape(MyVoxelBase.OperationType type, MyShape shape, byte Material)` (private instance void; the funnel of every voxel-hand operation: `VoxelOperation{Sphere,Box,Capsule,Ramp,Elipsoid}_Implementation` and their `Perform…` twins) and `MyVoxelGenerator.CutOutShape(MyVoxelBase, MyShape, bool voxelHand)`, `FillInShape(MyVoxelBase, MyShape, byte)`, `PaintInShape(MyVoxelBase, MyShape, byte)`, `RevertShape(MyVoxelBase, MyShape)`, `ChangeMaterialsInShape(MyVoxelBase, MyShape, byte, bool[])` (static void) | voxel hand and brushes, mods and scripts | skip |
| `writerange` | `MyStorageBase.WriteRange(MyStorageData source, MyStorageDataTypeFlags dataToWrite, Vector3I voxelRangeMin, Vector3I voxelRangeMax, bool notifyRangeChanged, bool skipCache)` | backstop for any other writer (the ship mining system's write, client cut-out replays) | skip |

Not patched: the generic sinks `ExecuteOperationFast<TVoxelOperator>` and `WriteRangeInternal<TOperator>` (one JIT body
per operator struct). A drill or explosion on a tile still sets `MyVoxelBase.BeforeContentChanged` before the guard;
that is harmless here (it only copies `Shared` storages and affects saved maps; ours are neither). The trash voxel
revertor touches only edited chunks and closes only `MyCompositeShapeProvider` asteroids, so it never acts on the tiles
A guard target that does not resolve leaves that path
unguarded (an edit would live in memory only and vanish at the next load, since the tiles are never saved); it is
logged and never refuses the terrain. Edit witness for S4.N2: `MyStorageBase.IsRangeModified(ref BoundingBoxI)` is true
as soon as any non-provider leaf exists in the range (VERIFIED `MyOctreeStorage.IsModifiedInternal`), and the content
hash of `de.terrain hash` is unchanged. **S8a:** while `DrillOn` the guards let every path
through on the tiles (§3.3 "Drill overlay", "guard.lift"); S4.N2 and S4.M9 then run with `DEEPENGINEERS_S8A_DRILL=off`,
which restores this table exactly.

### 3.2 Interiors, the world-settings check and audio polish (S7; not part of the site version)

Settled by contract-s7 on; the records and member signatures are in §5.4 "S7 conventions".
- **World settings.
 have both on by default; a Creative world as created). SE creates a grid's gas system (`MyCubeGrid.GridSystems.
 GasSystem`) only with both, so without them no room exists. The SE plugin
 reads both once per world at bind (the site line's `world{… oxygen= pressurization=}`) and, when either is off, logs
 `DE.Interior OFF pressurization disabled` exactly once per world; the interior flag then stays false and no RoomSet
 is sent (S7.N1b). The plugins never write a world setting; `tools/lab/set_world_settings.py` (`--dry-run` first)

- **Interior flag.** C1 flag bit 3 `cameraInAirtightRoom` (and C5 bit 4, the same value) = the camera's grid cell lies in
 an airtight room (`MyOxygenRoom.IsAirtight`) of a mirrored grid, from `GetOxygenRoomForCubeGridPosition` (or
 `GetSafeOxygenBlock` inside a block), the last value held while that gas system recomputes; never from `GetRooms`
 per tick. The C1 interior extension (0xB0–0xBF) adds the room's grid id, index, oxygen level and the
 `CharacterInRoom` bit that keeps SE's swim model off in a dry room. Subnautica turns `Player.IsInsideWalkable` true,
 draws `Inside` chunks with the base-interior sky (and `Boundary` chunks while the camera is in that grid's room),
 and clips the sea surface out of rooms that straddle it.
- **Audio polish** (the open items of ARCHITECTURE §5.3 and docs/S2-STATUS.md; nothing invented): A1 SE's own 3D
 emitters are low-passed under water (`LowPassNoHelmetNoOxy`, HYPOTHESIS fit) while the underwater SE mix is in
 effect and the camera is not in an airtight room; A2 the bus rule above water under `Site` stays S2's (Subnautica
 muted above water), so S7 event sounds play only on under-water events; A3 in a dry room under water Subnautica's
 interior ambience plays (through `IsInsideWalkable`) and SE is not muffled; A4 the crossfade reversal (S2 D148) is
 documented as built; A5 biome and depth ambience unchanged (the ghost at the camera); A6 every S7 event sound is
 Subnautica's own FMOD event, 3D at the event, none beyond 250 m.

 building sounds and SE's reverb; under water each thruster keeps SE's own sound per type with a short envelope (Subnautica's
 Cyclops/Seamoth loops as an option); §5.4 "S14 conventions".

### 3.3 Terrain converter: drill overlay, store, window, chart and sculpt (S8a; not part of the site version)

Settled by contract-s8a on against the decompiled SE 1.210.014 and Subnautica 1.22.83031
Records, pins and the canonical text are in §5.4 "S8a conventions"; checks in
ROADMAP "### S8a"; mechanisms in ARCHITECTURE §5.17. Everything here is in addition to §3.1, which stays as written
except where "S8a" says otherwise.

**What the engines do (VERIFIED; these facts replace the S8a task's assumptions).**
- Subnautica draws and collides with `WorldStreaming`'s terrain: `WorldStreamer` (`LargeWorldStreamer.main.streamerV2`)
 owns two `BatchOctreesStreamer`s (full detail: LOD 0–3, a ring of `batchesPerSide` × `batchesVertically` ×
 `batchesPerSide` = 5 × 5 × 5 batches from `streaming-<quality>.json`, centre clamped to `centerMin` (0, 0, 0) …
 `centerMax` (25, 20, 25); low detail: LOD 4, 1 × 1 × 1 at (0, 0, 0)) and a `ClipmapStreamer` (5 levels from
 `clipmaps-<quality>.json`, level 0 with colliders). Every batch is read on the streamer's I/O thread by
 `BatchOctrees.LoadOctrees` from `BatchOctreesStreamer.GetPath(id)` =
 `<worldPath>\CompiledOctreesCache\compiled-batch-{x}-{y}-{z}.optoctrees`, where `worldPath` is the install's `SNUnmanagedData\Build18` (`LargeWorld.MountWorldAsync` →
 `WorldStreamer.Settings.worldPath`; `WorldStreamer.CreateStreamers`). Octrees live as native 4-byte node arrays
 (`WorldStreaming.Octree.data`, private `NativeArray<byte>`).
- There is **no live voxel edit path and no per-save octree cache** in this build. `LargeWorldStreamer.compactTrees` is
 never filled (`ReadCompiledOctrees` is reached only from `LoadCompiledOctreesThreaded`, which has no caller), so
 `PerformSphereEdit`, `PerformBoxEdit` and the `dig` console command edit nothing; `LoadBatchThreadedAsync` tests
 `GetCompiledOctreesCachePath(pathPrefix, …)` with an empty body; `pathPrefix` is the install's world folder (not a
 save slot). `UserStoragePC.UpgradeSaveDataAsync` (run at every Steam start by `PlatformServicesSteam`) deletes
 `compiled-batch-*.optoctrees` from save folders. `Voxeland`, `VoxelandData` (`land.data.ClearToNothing` at
 `LargeWorldStreamer.Initialize`) and `VoxelandChunk` are the legacy path and carry no terrain here.
- Subnautica's Steam saves are `<Subnautica>\SNAppData\SavedGames\slotNNNN` (`PlatformServicesSteam`:
 `Directory.GetParent(Application.dataPath)` + `SNAppData/SavedGames`), inside the install folder; the temporary save is under
 `Application.temporaryCachePath\TempSave`. The host save (`slot0000`) stays never-saved (§1, S1).
- SE's save (`MySessionSnapshot.Save`) stages every file in `<save>\.new`, copies it to `TargetDir` and **deletes every
 top-level file of `TargetDir` it did not stage** (the thumbnail excepted); subfolders are untouched.

**Subnautica members S8a uses** (VERIFIED `subnautica/Assembly-CSharp` and `Assembly-CSharp-firstpass`; namespace
`WorldStreaming` unless named; a member that does not resolve logs `DE.S8a MISSING <member>` once and disables only its
feature, as every slice does).

| Member (exact signature) | S8a use |
|---|---|
| `BatchOctreesStreamer.GetPath(Int3 batchId)` → `string` (public) | Harmony **postfix**: the store's validated version of that batch when the store view names one (drill layer for crater ids, window layer otherwise), else the original result |
| `BatchOctreesStreamer(IThread streamingThread, Int3.Bounds octreeBounds, int minLod, int maxLod, int batchSize, int numOctreesPerBatch, string path, BatchOctreesStreamer.Settings settings)` (public constructor) | Harmony **prefix**, only when `minLod == 0` and the window is on: `settings.centerMin` = (UniverseMin, 0, UniverseMin), `settings.centerMax` = (UniverseMax, 20, UniverseMax) (public `Int3` fields of `BatchOctreesStreamer.Settings`); the private readonly `centerBounds` is computed from them in the constructor; `octreeBounds` (public readonly) is left alone, because the install's edge batches 25-*-* and *-*-25 store only octrees 125–127 and a wider bound would misparse them |
| `BatchOctrees.LoadOctrees` → `bool` (private) | Harmony **prefix** for window ids (bx or bz outside 0…25): `__instance.id`, injected `___streamer` (`BatchOctreesStreamer`), `___allocator` (`UWE.SplitNativeArrayPool<byte>`), `___octrees` (`Array3<Octree>`); open `___streamer.GetPath(id)` with `new PooledBinaryReader(Stream)`, require version ≥ 4, then for each of the 125 octrees in `Int3.Range(5)` order `Octree.Read(PooledBinaryReader reader, Int3 batchId, SplitNativeArrayPool<byte> allocator)` and `Octree.UnloadChildren(int lod, SplitNativeArrayPool<byte> allocator)` with `___streamer.minLod`; `__result` false on a missing or bad file (the original caller then clears the octrees: empty water); crater ids run the original |
| `BatchOctreesStreamer.GetBatch(Int3 id)` → `BatchOctrees` (null unless the ring holds `id`); `BatchOctrees.Reload(Int3 newId)`, `IsLoaded`, `id` | the edit and window reload: `Reload(id)` with the batch's own id unloads (waiting while a clipmap cell of its range is building: `ProcessUnloadingQueue` → `IsProcessing`) and loads again through `GetPath` |
| `BatchOctreesStreamer.streamingThread` (`UWE.IThread`, public get); `UWE.IThread.Enqueue(Task. `UWE.Task.
| `ClipmapStreamer.GetLevel(int levelId)` → `ClipmapLevel`; `ClipmapStreamer.settings.levels` (`ClipMapManager.LevelSettings[]`); `ClipmapLevel.id`, `cellSize` (public readonly int), `GetCell(Int3 cellId)` → `ClipmapCell`; `ClipmapCell.Reload(Int3 newId)`, `id`; `ClipmapStreamer.IsRangeActiveAndBuilt(Int3.Bounds blockRange)` → `bool` | after the batch is loaded again, `Reload(cell.id)` for every level 0–3 cell whose range meets the notice box (range as the private `ClipmapLevel.GetCellRange`: `Int3.MinMax(FloorDiv(mins − (3 << id), cellSize), CeilDiv(maxs + 2·(3 << id), cellSize))`); a loaded cell does **not** rebuild on `OnRangeLoaded` (only `WaitingForOctrees` cells react), hence the explicit reload; a `Visible` cell hides its mesh at once and rebuilds (HYPOTHESIS: the parent level covers it meanwhile; S8a. applied = `IsRangeActiveAndBuilt` over the notice box |
| `WorldStreamer.UpdateStreamingCenter(Vector3 wsPos)`: `streamingCenter = (Int3)transform.InverseTransformPoint(wsPos + (0, −8, 0))`; `BatchOctreesStreamer.UpdateCenter(Int3 position)`: centre = `centerBounds.Clamp(FloorDiv(position, batchSize))`, ring `Int3.CenterSize(centre, arraySize)` | the window plan replicates it on SE (CONTRACT "window.set") |
| `LargeWorldStreamer.IsRangeActiveAndBuilt(Bounds bb)` → `bool`; `LargeWorldStreamer.GetBlock(Vector3)` (`Int3.Floor(land.transform.InverseTransformPoint(p))`, the land at (−2048, −3040, −2048)) | S1's `areaBuilt`; S8a gates it in S1's telemetry (a hook, not a patch) |
| `LargeWorld.GetBiome(Vector3 wsPos)` → `string` (S2's prefix) | beyond the crater box: `biome.names[cameraBiome]` within 200 m of the camera, else `void` |
| `LargeWorldStreamer.MakeEntityTransient(GameObject entity)`; `CraftData.GetPrefabForTechTypeAsync(TechType techType, bool verbose = true)` → `CoroutineTask<GameObject>` (S7) | the spawn table's prefabs, never saved |
| **Not used** (and why): `LargeWorldStreamer.LoadBatchAsync(Int3 index)`, `LoadBatchTaskedAsync(BatchCells batchCells, bool _editMode)` (entity cells and batch objects; window ids fail `CheckBatch`, so no entity cells exist there), `UnloadBatchObjects(Int3 index)` (no batch objects in window ids), `SaveBatchCompiledCache(Int3 index)` and the private `SaveCompactOctrees(Int3 batchIndex, string path)` (would write `compactTrees`, all null, into the install's world folder: **never call**), `GetCompiledOctreesCachePath(string prefix, string filename)` (only `BatchUpgrade` and the dead writer), `PerformSphereEdit`/`PerformBoxEdit` (edit nothing here), `Voxeland*` | |

Batch index math (VERIFIED): SN point p → block `v = Floor(p − (−2048, −3040, −2048))`; batch `FloorDiv(v, 160)`; octree
`FloorDiv(v, 32)`; in-batch octree `(ix, iy, iz)` file order x-outer, y, z-inner (`Int3.Range`); child order
`4·xbit + 2·ybit + zbit`. Crater ids: bx, bz ∈ [0, 25], by ∈ [0, 19]; the install holds rows 7–19 (§3.1).

**SE members S8a uses** (VERIFIED `se/Sandbox.Game`, `se/VRage`; S4's guard paths and their signatures stay as in §3.1
"Edit guards").

| Member (exact signature) | S8a use |
|---|---|
| `MyStorageBase.RangeChanged`: `public event Action<Vector3I, Vector3I, MyStorageDataTypeFlags>` | subscribed on every tile and on the planet's storage; raised by `OnRangeChanged` from `ExecuteOperationFast`/`WriteRange` on the editing thread (explosions run inside a `Parallel.For`): the handler only enqueues, thread-safe, ≤ 20 µs |
| `MyStorageBase.ReadRange(MyStorageData target, MyStorageDataTypeFlags dataToRead, int lodIndex, Vector3I lodVoxelRangeMin, Vector3I lodVoxelRangeMax)` | the converter's reads of tiles (overlay included) and of the planet (worker threads, under SE's shared storage lock) |
| `MyStorageBase.WriteRange(MyStorageData source, MyStorageDataTypeFlags dataToWrite, Vector3I voxelRangeMin, Vector3I voxelRangeMax, bool notifyRangeChanged = true, bool skipCache = false)` | the overlay restore at load and the mirroring of an edit into overlapping tiles, each under its thread-static S8a flag (S4's `writerange` guard lets them through) |
| `MyStorageBase.IsRangeModified(ref BoundingBoxI box)` → `bool` | S8a.N1.edit; a window batch is regenerated at load when its planet preimage is modified |
| `MyStorageData(Vector3I size, byte[] content = null, byte[] material = null)`, `Resize(Vector3I size3D)`, `Content(int linearIdx)`, `Material(int linearIdx)` | buffers, x fastest (S4) |
| `MyVoxelBase.PerformCutOutSphereFast(Vector3D center, float radius, bool notify)` (→ `MyVoxelGenerator.CutOutSphereFast`, S4 path `explosion`); `MyVoxelBase.CutOutShapeWithPropertiesAsync(OnCutOutResults results, MyShape shape, bool updateSync = false, bool onlyCheck = false, bool applyDamageMaterial = false, bool onlyApplyMaterial = false, bool skipCache = true)` (the hand-drill path, S4 `cutout`), with `MyVoxelBase.OnCutOutResults(float voxelsCountInPercent, MyVoxelMaterialDefinition voxelMaterial, Dictionary<MyVoxelMaterialDefinition, int> exactCutOutMaterials)` and `new MyShapeSphere(Vector3D center, float radius)` | `de.s8a drill` (scripted holes); ship drills (`MyShipMiningSystem.RequestCutOut`, S4 `shipdrill`), warheads and the voxel hand come through S4's paths. `MyShape.SignedDistanceToDensity(float)` = `clamp(−d, −1, 1)·0.5 + 0.5`: a cut leaves SE's content a ±1 m linear ramp, the same band as Subnautica's density |
| `MyVoxelBase.Storage` (`VRage.Game.Voxels.IMyStorage`), `PositionLeftBottomCorner` (`Vector3D`), `InitVoxelMap(MatrixD worldMatrix, Vector3I size, bool useOffset = true)` (+0.5 m when `MyPerGameSettings.OffsetVoxelMapByHalfVoxel`) | LOD-0 sample i of any voxel map sits at `PositionLeftBottomCorner + i` (S4.A-T0b for the tiles, which use no offset; a planet's corner already carries its offset; S8a.N7.rays checks it) |
| `MyPlanet` (`: MyVoxelBase`): `Provider` (`MyPlanetStorageProvider`, id 10042), `Generator` (`MyPlanetGeneratorDefinition`), `MinimumRadius`, `AverageRadius`, `MaximumRadius` (float), `GetClosestSurfacePointGlobal(ref Vector3D globalPos)` → `Vector3D` | the window reads `Storage` (edits included; S8a.M6 reads `Provider` instead); `NearSurface` from the closest surface point |
| `MySession.Save(out MySessionSnapshot snapshot, string customSaveName = null, Action<SaveProgress> progress = null)` → `bool` | Harmony **postfix** when it returns true (autosave and Save As go through it; `snapshot.TargetDir` = the new `CurrentPath`): `snapshot.CompressedVoxelSnapshots["DE_S8a_overlay"]` = the overlay file. S4's `TerrainFailSafe` already holds a **prefix** on the same member (saving blocked while the fail-safe is active): the postfix then sees `false` and adds nothing |
| `Sandbox.Game.Screens.Helpers.MyAsyncSaving.Start(Action callbackOnFinished = null, string customName = null)` (public static) | `de.s8a save` (S8a.N4), game thread, only in a `DE_THROWAWAY.txt` copy |
| `MySessionSnapshot.CompressedVoxelSnapshots` (`Dictionary<string, byte[]>`, public get/set) | written raw by `SaveVoxelSnapshot(name, data, compress: false, …)` as `<name>.vx2` into the staging folder, then copied, cloud-listed and kept by the cleanup; `MyVoxelMaps.GetVoxelMapsData(bool includeChanged, bool compressed, Dictionary<string, IMyStorage> voxelStorageNameCache = null)` skips maps with `Save == false` (the tiles); `MySession.GetVoxelsSizeInBytes` only sums `*.vx2` (an orphan file is inert without the plugin) |
| `MyPlanetShapeProvider` (surface radius `R + bicubic(h)·heightRatio + minHillHeight`, `minHillHeight = HillParams.Min·R`, `heightRatio = (Max − Min)·R`, h = value/65535; `GetValueForPositionCacheless(int face, ref Vector2 texcoord, out Vector3 localNormal)` samples `texcoord·(Resolution − 1)`); `MyCubemapHelpers.CalculateSampleTexcoord(ref Vector3 localPos, out int face, out Vector2 texCoord)`, `GetNameForFace(int i)`; `MyPlanetTextureMapProvider` (heightmaps 8 or 16-bit, square, every face the same size; `<face>_mat` Rgba32: R material, G biome, B ore); `MyHeightmapFace(int resolution)` | the sculpt's encoding (CONTRACT "sculpt.*"): for `DE_4546B` R = 60,000, surface radius = 57,000 + 10,200·h; face 1 `back` (+Z) holds the site anchor U0 = (0, 0, 1) at texcoord (0.5, 0.5) |

**Drill overlay (S8a-1).** While `DrillOn` (env `DEEPENGINEERS_S8A_DRILL`, default on with `DEEPENGINEERS_S8A=on`), S4's
guard `Block` lets every edit path through on a tile; SE's `MyOctreeStorage` then writes its own leaves over the
`MyProviderLeaf`s (copy-on-write), so the provider is never written (it has no write member) and mining yields,
physics and ore stay SE's. Each `RangeChanged` range marks the 16³ LOD-0 overlay cells it touches (key: tile (i, j, k)
and cell (cx, cy, cz); by cell, never by player: v3-safe) and, when the range lies in another tile's box too (the
4-voxel overlaps at pitch 1,020 m), the same content is copied into that tile by `WriteRange` under the mirror flag.
The overlay is the set of dirty cells with their current content and material: at `MySession.Save` (postfix) it is read
back from the tiles (`ReadRange`, game thread, ≤ 50 ms per 1,000 cells) and stored as `DE_S8a_overlay.vx2` (format
"overlay.file"); in `BeforeStart`, right after the 34 tiles exist, it is written back (`WriteRange`, restore flag,
`notifyRangeChanged` true). `DrillOn` off restores S4's behaviour exactly (S4.N2 runs with it off). A world loaded
without the plugin keeps the file inert, and the next plugin-less save drops it (the holes go with the tiles).

**The store (Subnautica's terrain cache).** Root `%LOCALAPPDATA%\DeepEngineers\terrain\` (derived data; never inside
either game's install folder or `SNAppData`); per SE world a folder `<storeKey x16>` with `drill\` and
`window\<chartKey x16>\`; files `compiled-batch-<bx>-<by>-<bz>.v<version>.optoctrees` in Subnautica's own format
("octree.batch"; the S4 reader reads a folder of them after a rename to the plain name, which the round trip does);
versions are never overwritten (Subnautica may hold the previous one open); `manifest.v1.txt` per layer folder
("store.manifest"). **One writer: SE** (the drill converter and the window worker); **one reader: Subnautica**, through
`GetPath` and the loader prefix, only versions it has validated (size and FNV-1a 64 from the notice or the manifest,
checked on a worker before the reload is scheduled). The drill layer is rebuilt at world load when the manifest's
`overlayHash` differs from the overlay just restored (an older SE save, Save As, another world); window folders are
per chart and kept for the site chart (0), evicted least-recently-used beyond `Store.MaxMB` (2,048 MB) for files farther
than `KeepWindowM` (1,600 m) from the camera, never the current ring, never drill files. Old versions are deleted
`GraceMs` (10 s) after Subnautica's `appliedSeq` covers a newer one, in both layers, so a live edit always finds the version Subnautica holds.

**Conversion (the one core: Wire `S8aConvert` + `S8aOctree`, Python twin `tools/sn-octrees/sn_octree_write.py`).** SN voxel
v holds the sample at P(v) = (−2048, −3040, −2048) + v + 0.5 (S4's centre convention, confirmed by S4.A-T2's real run);
its value comes from SE's content field at F⁻¹(P(v)) ("convert.sample": trilinear over SE's LOD-0 samples `content/127.5
− 1`, clamped to ±1, encoded as Subnautica's `EncodeDensity`, far beyond ±1); the drill layer recomputes only the
voxels whose stencil meets an edited SE range ("convert.copyonwrite") and keeps the base type of voxels that were
solid; newly solid voxels and the whole window take the material table:

| SE voxel material (`SubtypeId`, resolved by name each session) | Subnautica block type | Evidence | Sculpt `CustomMaterialTable` value |
|---|---|---|---|
| `Sand_02` | 1 | Safe Shallows 12-18-12: 38 % (172: 36 %) | 1 (existing) |
| `Stone_01` | 111 | kelp forest 14-18-16: 43 % | 2 |
| `Stone_02` | 150 | Grand Reef CoralArch 6-17-5: 84 % | 3 |
| `Stone_03` | 190 | Jellyshroom entrance 10-17-11: 79 % | 4 |
| `Stone_04` | 141 | Lost River region 7-13-15: 35 % | 5 |
| `Stone_05` | 84 | deepest floor 14-8-9: 84 % | 6 |
| `Grass` | 219 | Mountain Island top 14-19-18: 43 % | 7 (the designed planet's island above water) |
the SN log's `s8a.palette` row names the types for a retune) | ore map B 10 |
| `Nickel_01` | 96 (was 27 before terrain 6) | ore: found only below 600 m (92 %) | B 20 |
| `Silicon_01` | 107 (was 130 before terrain 6) | ore: found only below 600 m (97 %) | B 30 |
| `Cobalt_01` | 108 (was 160 before terrain 6) | ore: 100–300 m band (53 %) | B 40, outcrop B 41 |
| `Magnesium_01` | 117 (was 39 before terrain 6) | ore: 100–300 m band (82 %) | B 50, outcrop B 51 |
| `Silver_01` | 118 (was 166 before terrain 6) | ore: found only at 300–600 m (99 %) | B 60, outcrop B 61 |
| `Gold_01` | 119 (was 155 before terrain 6) | ore: found only at 300–600 m (99 %) | B 70, outcrop B 71 |
| `Platinum_01` | 121 (was 29 before terrain 6) | ore: found only below 600 m (100 %: the Lost River / lava formations) | B 80, outcrop B 81 |
| `Uraninite_01` | 122 (was 200 before terrain 6) | ore: found only below 600 m (100 %) | B 90, outcrop B 91 |
| any other | 84 | | |

The type names are Subnautica's palette's (not read offline)
change bumps `ext s8a terrain`). The ore rows (Stage C, `ext s8a terrain 2`) were picked by a read-only census of the
install's batches (each type's share of near-surface solid voxels per depth band): common ores a shallow stone, rare
ores a type that occurs only in the deep formations.
Silicon) start at the seabed (`Start` 0) with types found only in the deep formations, so their deposits outcrop in
Subnautica; the deeper ores keep their `Start` and put 10 % of each deposit's pixels on a second row (`Start` 0, the
outcrop). Below the surface the veins show on the walls of a drilled hole (Subnautica's own octree collapse keeps far-solid
types as blocks of 2–8 m). Octrees are written by the engine's own rules ("octree.write"), proven on the install
(ROADMAP "### S8a", converter proof: 1,264,598 interior nodes, 150 octrees byte-identical); the C# writer is proven
again against the S4 reader (S8a.C1).

**Window (S8a-2).** The window universe is SN batch ids bx, bz ∈ [−35, 60] outside the crater box [0, 25] (SN x, z ∈
[−7,648, 7,712)), by ∈ [0, 19]. Subnautica's full-detail octree ring follows the camera there because the constructor
prefix widens its centre clamp; batches outside the crater box load through the loader prefix (all 125 octrees per
file) from the window layer; ids without a file load empty (water). SE's window worker (one thread, below normal,
Windows background I/O mode as S4's prefetcher) keeps the ring around the camera generated, nearest first, plus one
batch column ahead when the horizontal speed is ≥ 5 m/s ("window.set"), from the planet's storage under the current
chart: per batch, LOD 3 classification of the preimage (all water → `BatchCleared`; all rock → a file of 125 one-node solid octrees),
LOD 0 reads only for octrees that meet the surface; of those, SE computes the voxel materials only within one voxel of a non-full voxel (content first, then a material-only read with `MyVoxelRequestFlags.ConsiderContent` over that shell, Stage C timing fix): every stencil sample a converted surface voxel takes its type from is SE's; a solid voxel just outside the shell takes its shell neighbour's material, every other far-solid voxel the box's most common shell material (never meshed, ignored by Subnautica's octree rules) (a drill's walls become a shell when the window regenerates the batch; env `DEEPENGINEERS_S8A_SHELL=off` restores the full read). A planet `RangeChanged` regenerates the batches whose preimage it

box are re-read and re-converted, the others are copied from the batch's previous file; edits coalesce for `CoalesceMs` +
`CoalesceMsPerMB` per MB of edited box, and an edit arriving while that batch's job runs never
cancels it: it becomes a follow-up job started after the apply. Subnautica reloads the cells of a served batch nearest the camera
first, waits `RetryFor(queued)` / `GiveUpFor(queued)` (scaled by the queue, at most 120 s) and, after a give-up or a reload,
re-enqueues every cell left unloaded and not processing (`cellKicks`). `NearPending`/`NearEmpty` follow "window.ready"; `cameraBiome` follows "biome.rule"; spawns follow
"spawn.*" (Subnautica side, from the served files). Known limit: the 64 m strip SN x or z ∈ [2,048, 2,112) at the crater
box's +x and +z edges belongs to crater batch 25, whose octrees 128–129 lie outside the install's bounds: no
Subnautica terrain there (the sculpt keeps it at −3,000 m, dark and deep).

**Chart and treadmill.** A chart is a `SiteMap` (§2.3) with its own anchor (U0, N0, ψ, O); chart 0 is the site map v1

decides): then FAR (7,800/7,900 m) still hands the picture to SE. With `TreadmillOn`, "chart.step" moves the camera's
SN position by whole batches (T) and "chart.anchor" builds chart k + 1 at the camera's sea point with the carried frame,
so the camera's mapped pose and its local frame are continuous; the only change near the camera is the outgoing
chart's distortion there, tangential `θ/sin θ − 1 ≈ θ²/6` (θ = arc distance to its anchor / Rs): ≤ 1.07e-3 at the
0 → 1 step (site ρ∞ 4,800 m) and ≤ 6.8e-4 later (≤ 3,840 m from the anchor), hence S8a.N8.jump's bound 1.1e-3 · distance
+ 0.01 m. "chart.apply" keeps C1, C2 and the S8a header of the step tick on one chart; Subnautica snaps across a
`chartSeq` change; SE drops Subnautica-positioned input (C9 positions, C12 batches) while C7 `chartSeqApplied` lags.
While a treadmill chart (k ≥ 1) is active SE requests the picture policy `Waterline` (`PicturePolicy.Set(Waterline,
"s8a.treadmill")`, the S7a live setting): above water the crater's islands and the Aurora would show at wrong places;
back on chart 0 the policy returns to the value it had. The view machine is unchanged: a step's camera jump is a
teleport to it, so over terrain SE's picture covers the re-stream (≤ 1.5 s, S8a.N8.picture), while in open water
`NearEmpty` keeps `areaBuilt` and Subnautica's picture.

**Sculpt.** `tools/planet/sculpt_planet.py`
(= `sculpt_de4546b.py`) writes the six `<face>.png` (2048², 16-bit grayscale) and `<face>_mat.png` (2048², RGBA: R the
`CustomMaterialTable` value 1–7 of the table above, G the biome, B the ore) of `src/DeepEngineers.WorldMod/Data/PlanetDataFiles/DE_4546B/`
and the `PlanetGeneratorDefinitions.sbc` rows (`CustomMaterialTable` 1–7 with `MaxDepth` 5, `OreMappings`, `PlanetMaps`
Material, Ores and Biome on) once, from the authored layout `tools/planet/layout_4546b.json` ("sculpt.encode",
"sculpt.site"): Subnautica's biome layout around the authentic crater (the install's batches, untouched) scaled ×3 to a
12 km ring, then the Crater Edge down to the void floor (SN y −3,000); beyond it the whole planet (Stage E, below). Biome regions
are the nearest-seed cells of the layout's seeds (map metres × 3, a fixed warp); heights blend between neighbours. Each
biome has Subnautica's depth band and a relief style: Safe Shallows 0–80 m (sand flats, coral boulders), Kelp Forest
30–130 m (trenches), Grassy Plateaus 50–180 m (flat tops and drops), Mushroom Forest 100–250 m (rolling hills), Sparse
Reef 60–200 m (outcrops), Grand Reef 180–500 m (the drop, arches), Dunes 100–400 m (long dunes), Mountains 0–600 m (ridges
and the island above water, `Grass` above −2 m), Underwater Islands 100–500 m (pillars), Blood Kelp 300–700 m, Crash Zone
50–300 m (a sand slope), Lost River entrances 500–900 m (three canyons), Crater Edge (the −3,000 m wall); hills of ±8 m
every 200–500 m everywhere; a seed varies only the ±2 m detail layer. The crater stays at SN y −3,000 within ρ∞ 2,300 m
(all pixels within 2,450 m are 0, so the bicubic surface is exactly flat there and never meets the provider tiles), the
moat to 2,900 m, the wall up to the ring by 3,700 m. G = the biome's `biome.names` index + 1 (inert in SE without
`MaterialGroups`; the window's biome rule reads it at the camera's surface point). B = an `OreMappings` `Value`

the depth-rarity table lives in the layout (Iron/Nickel/Silicon 0–200 m common, Cobalt/Magnesium 100–300 m,
Silver/Gold 300–600 m in Grand Reef and Blood Kelp, Platinum/Uranium below 600 m in the canyons, Blood Kelp and the Crater
Edge; deposits ≥ 1 pixel ≈ 46 m). Nothing of the game is read or copied. The encoding is the decomp's and was measured
with SE's own native voxel generator on our files (`tools/planet/sculpt_native_check.py`, S8a.C2.native: ≤ 0.1 m on
slopes below 0.5) and in game (launcher part `c2g`, S8a.C2.ingame). A new sculpt changes `sculptHash`, hence every chart
key: the window regenerates.

*F1 offline).** The brush library (`tools/planet/s8a_brushes.py
build`, read-only on the install): octrees (32 m) and 2 x 2 x 2 groups (64 m) of the Subnautica map's batches between SN y
-640 and +160 that hold a form, classified wall / boulder / overhang / cave / pillar / floor and tagged by the install's biome
map, mapped to the
ring's biomes; never an octree within 48 m of a landmark root (the Aurora, lifepods, wrecks, Precursor and Degasi bases: the baked
entity cells' objects whose prefab paths name them) or below SN y -600 (the Lost River's and the lava zones' unique places).
Re-encoded as our own data: a signed distance field (metres, positive in rock, int8 in 1/16 m, +-7.9 m) and the block type per
voxel, with the provenance (batch, octree or group), in `<store root>\brushes\library-v1.bin` + `index-v1.json`. The ring's 3D
base (`tools/planet/s8a_ring3d.py`): f = the designed seabed - y, worm caves (Blood Kelp, Lost River), Grand Reef arches; brushes
stamped per 96 m cell by biome density and classes, any yaw, mirrored, scale 0.9-1.15, ground aligned, blended over 6 m, never the
same brush within 300 m. Rows S8a.F.library,.forbidden,.repeat,.seam,.blend (offline).

**Stage F2: the ring served by the planet's generator.** The brief asked for S4 provider
tiles (voxel maps) in a moving window over the planet. That cannot hold the ring's caves: a voxel map overlaps the planet's own rock
and SE has no subtraction between voxel maps (each `MyVoxelBase` meshes and collides on its own storage), so a tunnel or an
overhang's underside dug in a tile under the seabed would stay filled by the planet. The field is therefore served by the planet's
own generator: a Harmony prefix on `MyPlanetStorageProvider.ReadRange(ref MyVoxelDataRequest, bool)` (every content read of the
unedited planet: the render clipmap, physics and the S8a window alike, through `MyProviderLeaf`) writes the field's content for reads
at LOD <= 3 whose box meets SN radius 3,530-12,270 m and the field's reach above and below the seabed (stamps to 128 m, caves to
130 m), and lets SE's own generator answer every other read: the heightmap is the field's base (the same Catmull-Rom over the same
16-bit face, so the two meet without a seam), and it is the fallback (coarser LODs, outside the zone, before the field is ready, after
any failure: the original runs on the same request). A postfix on `Intersect(BoundingBoxI, int)` answers Intersects for boxes the
field may fill; the window's LOD-3 classification marks such boxes Surface (a 3 m pillar or a tunnel falls between 8 m samples).
SE's clipmap LODs are the moving window: the field is computed only where SE reads at LOD <= 3, near the camera. Materials stay
SE's (`MyPlanetMaterialProvider.ReadMaterialRange`, called as the original does). Drilling is SE's planet edit over this content
and reaches Subnautica through the window's edit path (the drill layer on top). The base comes from SE's own face-1 heightmap and
biome map in memory (checked against the world mod's `back.png` / `back_mat.png`), the brushes from the store's library
(`library-v1.bin`, `index-v1.tsv`); the field is made on SE's first read of the planet at any LOD (the load's far LODs) or, failing that, at world up on a pool thread; the placements are made once (15,894 with the 320-brush library), deterministic from the maps,
the library and seed 4546. Determinism: the served bytes are `RingField.ServeBox` at the planet-local voxel positions, plain IEEE
double arithmetic in a fixed order (the site mapping's arctan and sin/cos are polynomials), so every run and every machine serves the
same bytes for a pin; vectors (`tests/s8a/vectors/ring3d/`, "serve" lines) hold the twin's bytes of four boxes (LOD 0 to 3) and
`RingFieldTests` checks the C# byte for byte; in game `de.s8a ring {serve}` hashes the same boxes through the C# and through SE's own
generator read (S8a.NF.determinism), because saved drill deltas sit on the generator's output. Off unless `DEEPENGINEERS_S8A=on` and
`DEEPENGINEERS_S8A_RING=on` (the launcher sets it for the `nf` part only
`nf` previews); `DEEPENGINEERS_S8A_RING_LOD` lowers the LOD limit. With the ring on, the window's and the charts' keys carry the
field's identity ("store.key"). Oracle part `nf`: S8a.NF.ready,.agree (Subnautica vs SE over ring paths),.served (SE's surface is
the field's top, not the heightmap),.seam (Subnautica follows SE across window batch seams on steep spots), S8a.F.repeat,.forbidden,.blend (read back from the served planet), S8a.NF.cost (the prefix's time per call and LOD, on every thread and on the
game thread, against SE's own generator in the same run: p95 <= 4 x SE's + 0.5 ms, the game thread's max <= max(8 ms, 2 x SE's)),.scope (never served outside the zone or above LOD 3, checked per served box with SiteMap's own arctan),.determinism,.fallback
(`S8a.M15` makes the field fail on the reads of a plain column: SE's heightmap serves it, no hole, no seam),.memory (S10: SE +300 MB,
Subnautica +150 MB),.time (the window's batches over the ring),.drill (Stage D on a stamp).

*`S8aFlora` v2.1, Subnautica; docs/S8A-PLANET-V2.md R9).** A carve (a
drill-layer notice, or a window notice for part of a batch without Reuse) is kept per store key and chart as its own box (never
merged into larger boxes) and persisted in `<store>\<storeKey>\flora-carves.tsv`, so a restart applies them again. Candidates
come from this mod's registry of `LargeWorldEntity` objects, fed by three paths
 and from
`Physics.OverlapBox`
a free (non-kinematic) body
over a carve is woken (`Rigidbody.WakeUp`) and falls by itself. Ground is judged
against Subnautica's served voxels (`S8aVoxelGround`: the batch files the `GetPath` postfix served, the original install file for
"had ground before"; no colliders needed, so objects that wake beyond collider range are judged too; physics only as the fallback
when a file is not readable). When a carve's notice arrives, the candidates in its box (+1 m) are found with `Physics.OverlapBox`
(no scene-wide search on the main thread); after the carve is applied (+400 ms) one whose original ground existed and whose ground
is gone now is hidden. Objects whose cell loads later inside a carved box (`LargeWorldEntity.Start` postfix) are tested 800 ms
after by the same rule ("late"). Candidates: `LargeWorldEntity` objects of the Near, Medium and Far cell levels, pickupable items
included (Pink Caps, scrap metal), outcrops, deposits, fragments; never creatures, vehicles, the player, bases, constructables,
free physics bodies, nor this mod's own deposits (`S8aOreDeposits` marks them before they start). An object wider than 3 m is
hidden only when its root and 3 of 4 footprint points lost their ground. Hidden = renderers and colliders off, transient (not part
of the cell's serialized data). Row `s8a.flora` {seq, why notice|reload|late, candidates, hidden, kept, families
"family:tested/hidden/kept,..." (deposit, outcrop, fragment, pickflora, pickup, mushroom, coral, plant, rock, debris, other),
voxelReads, box}; `s8a.status` adds floraLate, floraLateHidden, floraNotLoaded, floraCarvesLoaded, floraFamilies. Test props
(S8a.ND.props): `de.s8a props {at: [x, y, z]}` sets one of each family (Pink Cap, scrap metal, limestone outcrop, coral shell
plate, big coral tubes; Subnautica's own prefabs, transient, kinematic) on the seabed, `{status}` lists each (family, hidden,
y, then the rule's view: candidate, in the registry, collisions on, kinematic, cell level, ground now, ground in the original),
`{clear}`; row `s8a.props` (registry size, found through it, woken); the rule's view names the registry path (start, cell, dev)
and why an object is not a candidate (inactive, level, ours, creature, vehicle, player, base, constructable, free body); a candidate
needs its object active in the scene, not its LargeWorldEntity component enabled. `de.s8a life {radius}` counts the registered
plants and creatures round the camera (this mod's deposits and test props apart; NL.look). `S8a.M26` restores v1's candidate rule (terrain within 0.8 m by
physics). `s8a.status` adds floraRegistered, floraRegistryFound, floraWoken, floraViaStart, floraViaCell, floraViaDev, cellRegisterPatched.

*`S8aLiveEdit`, Subnautica).** A new
version of a served batch that the octree streamers hold is applied in memory: the changed octrees (the previous file against
the new one, `S8aLivePatch.ChangedOctrees`) are read on each streamer's I/O thread with the loader's own calls (`Octree.Read`
over the record, then `UnloadChildren(minLod)`), so the memory equals a reload of the file (`S8aLivePatch.LoadedBytes`; row
S8a.ND.roundtrip via `de.s8a verify {octrees}`); every loaded cell over the box is rebuilt beside its mesh (`MeshBuilder` on the
meshing threads, `DoFinalizePart` and the collider bake on the build-layers pump) and swapped in one main-thread step (the cell
never leaves its state; the old chunk returns to the pool): level 0 at once, levels 1–3 at most once a second per batch. A batch
the streamers do not hold, a parse error or a missing member falls back to the batch reload. Rows `s8a.live`, `s8a.cellvis`
(per frame while `de.s8a cellvis {seconds}`); SE `de.s8a drillpass {from, to, seconds, everyMs, radius}` cuts along a line
(launcher part `nd`, rows S8a.ND.hidden,.wall,.live,.roundtrip). Env `DEEPENGINEERS_S8A_LIVE=off` returns to batch reloads.
Order: a level-0 rebuild enters the meshing threads at once, a level 1–3 one waits until no level-0
rebuild is pending (`PumpHigher`, a cell waiting is not queued twice); the finishing step (`DoFinalizePart`, the collider bake, the
swap) runs on this mod's own main-thread pump (`PumpFinalize`, called from the S8a frame step, the thread of Subnautica's
BuildLayers coroutine), level 0 first, at least one per frame and up to `DEEPENGINEERS_S8A_LIVE_FINALIZE_MS` (default 3 ms;
0 = Subnautica's BuildLayers queue, which takes max((int)(numBuildLayersPerSecond × Δt), 1) items a frame,
`: one cell a frame at 70–170 fps). `s8a.live` adds per level-0 cell the means l0QueueMs, l0MeshMs, l0WaitMs, l0FinalMs
and firstL0Ms; `s8a.cellvis` adds finalWaiting; `s8a.status` adds finalized, finalizeMsAvg, finalizeMsMax, finalizeMaxPerFrame.
The window's notice box for an incremental batch covers only the
changes the level-0 mesh can show (`S8aFieldSampler.LastMeshChanged`: a density change, solid to empty or back, a near voxel's type,
a far-solid voxel's type when a 6-neighbour in its octree is empty or near); a rock or water octree rewritten with the same nodes
adds nothing; far-solid types inside the rock change the file (and the patched octrees) without widening the box. `s8a.write` adds
meshCells, densityCells and changedBox (every change). Binary-cut walls: SE's cut is binary, so the first
solid voxel behind it can be far-solid, and Subnautica's collapse rule writes a 4 × 4 × 4 far-solid block as one leaf with its child
0's type (a voxel 3 m away, below the vein); a far-solid voxel the conversion samples that has an empty 6-neighbour in its octree is
written near at density 251 (s = 125/126) instead, so a block collapses only when its wall voxels agree (`S8aFieldSampler.WallNear`).
The shell fill's far voxels take their nearest shell voxel's material layer by layer (only solid joined to no shell takes the box's
most common).

*** Charts ≥ 1 keep SE's picture policy (Subnautica's picture below the
altitude band) and set the C2 flag `OffSite`: Subnautica then hides the Subnautica map's landmarks, its terrain above the sea
(levels 1–3 over the crater box) and the horizon skirt, and restores them on chart 0 (`S8aOffSite`; env
`DEEPENGINEERS_S8A_OFFSITE_WATERLINE=on` brings back the earlier Waterline policy). Steps: 5 batches from chart 0, 12 after (the
camera lands 1,920 m inside the 3,200–7,040 m box), automatic ones `StepGapMs` apart. On a ChartStep Subnautica serves the
previous chart's window files under the shifted ids at once (no gap of water under the camera; the new chart's Reuse copies then
change no octree), SE re-announces kept entries nearest first, and SE's `NearPending` also counts near-box batches whose notice
Subnautica has not applied. A reload job holds its slot at most 10 s (the per-notice give-up still grows with the queue).

*names: *the Subnautica map* = the authentic 2 km terrain around the
lifepod, *the ring* = the designed biomes, *the planet* = everything else).** The layout's `planet` block paints every face:
the Crater Edge's void floor runs on as a moat to ρ 14 km (site v1 ρ = R_s × the angle from the anchor) and a wall rises over
2.5 km to an ocean floor of 100–1,500 m: shelves 110–230 m (basins noise above its level, ~25 % of the floor), abyssal plains
650–1,250 m, ridges up to 520 m above the plains, trenches to 1,500 m here and there, nine island chains (50 islands, land up to
+45 m with `Grass` above −2 m, a 60–95 m platform around each, a seamount slope to the floor; centres keep off the face
seams). Planet biomes (G): shelf `grassyPlateaus`, plains `dunes`, ridges and islands `mountains`, trenches `bloodKelp`
(Subnautica fog names; the spawn table is by depth band: generic fish), the moat `void`. The ring and the Subnautica map are
untouched (heights, R and G of the ring identical to design 2). Ores come in **fields** everywhere (layout `ores.fieldRule`):
per cell of 20 pixels (~0.9 km) one candidate field at the best of 6 pixels by slope (trench walls, ridge flanks), cliff foot and
trench/ridge bonus, kept with probability (0.35 + 0.65 × score) × (the weight of the rows eligible there / 0.074), its row drawn
by share (the ring's common ores × 0.5), 10–20 deposits of 2–6 pixels within 150–400 m. The planet's bands: Silver and Gold
300–1,500 m, Platinum and Uranium 600–1,500 m in trenches only; the rest as in the ring. Measured on design 3: ore share of the
planet's seabed 3.07 % (SE's EarthLike 3.46 %, its `_mat` B channel read read-only), Clark-Evans R 0.51 (EarthLike 1.66: evenly
spread), 83 % of deposits with a neighbour within 250 m. Rows S8a.C2.globe and S8a.N10.fields (`sculpt_planet.py --verify`);
`check_planet.py` row `png.globe` (every face sculpted once any face besides back is).

**Ores in the Subnautica map (Stage E; `S8aCraterOres`, Wire).** The S4 provider's material function (`MaterialBands.For(Vec3)`
with `MaterialBands.Ores`, S8a on and `DEEPENGINEERS_S8A_CRATER_ORES` not off) gives a solid voxel the ore of the pocket it lies
in: the SN world is cut into 8 m cubes; a cube holds a sphere of 2–3.5 m of the row drawn by the layout's ring shares among the
rows whose band holds the cube centre's depth (splitmix64 of the cube, deterministic); SE yields it, the S8a-1 drill layer shows
the hole's walls with the ore's Subnautica type. Subnautica's large resource deposits are not in the install

so Subnautica reports each one it starts inside the map
 into `<store>\<storeKey>\crater-deposits.tsv` (TechType, x, y, z); SE re-reads
the file every 5 s and puts a 6 m pocket 3 m under each, of the ore its TechType maps to (Titanium/Magnetite Iron, Copper/Nickel
Nickel, Quartz/Salt/AluminiumOxide Silicon, Lead/Mercury Cobalt, Lithium/Sulphur Magnesium, Silver, Gold, Diamond/Kyanite
Platinum, Uraninite Uranium). SE asks the provider whenever it reads an unmodified range, so a pocket reaches voxels read after
the report. `de.s8a ores` reads them back (S8a.N10.crater).

**Planet rules (planet v2 R8; `S8aPlanetRules`, Wire).** The layout's runtime rules live beside the 12 PNGs as
`Data\PlanetDataFiles\DE_4546B\de-planet-rules.txt` (magic `de-planet-rules 1`, one `key=value` per line, unknown keys kept and
ignored), written by `sculpt_de4546b.py --out` from the layout's `ores.crater` (design 3 has none: no file, and the tool removes a
stale one). Keys: `crater.cubeRule` (default true; false: the 8 m cube rule is off, ore only in the pockets under Subnautica's
large deposits) and `crater.depositsPerKm2` (default 0 = no cap; else at most floor(cap) deposits per 1 km × 1 km cell of SN x/z,
kept in the order of a hash of their rounded positions). SE reads the file at world up (a bad file: the defaults and a warning),
gives the crater's ore rule the switches (`S8aCraterOreRule.CubeRule`, `.DepositsPerKm2`; `S8a.M27` keeps the cube rule on) and
folds the file's fnv1a64 into the sculpt part of the window's and the charts' keys (`fnv1a64(utf8 "sculptHash x16|rules|hash
x16")`; no file: unchanged). The sculpt hash itself stays over the 12 PNGs. `de.s8a status` adds planetRules; `s8a.deposits` adds
cubeRule, depositsPerKm2, capped.

**Ores you can see (planet v2 R8; `S8aOutcropsSe` SE, `S8aOreDeposits` Subnautica, `S8aOutcrops` Wire).** SE scans the planet's
seabed round the camera (`MyGamePruningStructure.GetAllVoxelMapsInSphere`; per 32 m detector cell at LOD 2 as SE's own
`MyDepositQuery`, then the cell's 2 m voxels at LOD 1 within 112 m): an ore voxel with an empty neighbour is exposed; per cell
and ore one record anchored at the exposed voxel horizontally nearest the detector's centroid, with its normal, the exposed area
at the first sighting and the ore volume now (a drill lowers it; `drilled` once one LOD 1 voxel of ore is gone, in the cell or
near the first anchor, and the first sighting is frozen from then on). The deposit's size follows the ore within 3 LOD 1 voxels (6 m)
of its first anchor, by the cube root of that local volume
left; it goes below 10 % of it. Records within 8 m of a crater deposit
of the same ore are left out (Subnautica's own deposit is there). SE writes `<store>\<storeKey>\ore-outcrops.tsv` (magic
`de-s8a-outcrops 1` seq chartSeq chartKey; per record, tab-separated: key x16, ore, SN x y z, normal x y z, exposed m2, volume
m3, first exposed m2, first volume m3, the detector's centroid x y z, drilled 0/1, zone, scale) at most twice a second, atomically. Subnautica polls it and keeps one `Drillable` deposit per record within 100 m of the camera
(despawn beyond 130 m): the ore's prefab
 set on the
anchor along the normal, sunk 0.4 m, yawed by its key, scaled by the cube root of volume / first volume from the first-sighting
size (0.6–3); transient (`MakeEntityTransient`), never in the save, never reported as a crater deposit, never a flora candidate,
never damaged.
Commands: SE `de.s8a outcrops {status | probe {radius} | detect {radius}}` (detect runs SE's own `MyDepositQuery` by reflection
and lists its deposits per body and cell), Subnautica `de.s8a deposits {on | clear | immune: n}`. Rows `s8a.outcrops`,
`s8a.outcrops.probe`, `s8a.oredetect` (SE), `s8a.deposits`, `s8a.deposit.immune` (Subnautica). Env `DEEPENGINEERS_S8A_ORES_VISIBLE=off`.
Launcher part `no`: per ore site the camera 12 m above the seabed, held; SE's records and detector within 100 m, Subnautica's
deposits, SE's material 1 m inside each anchor along its normal; a drill on the biggest undrilled outcrop; 60 Subnautica drill hits
on the nearest deposit; then SE's detector over 300 m round 16 crater and 32 planet points. S8a.NO.agree: this mod's centroid equals
SE's detector's (within 1 m), and the deposit lies within 5 m of it or, an edge exposure, on its own ore. Subnautica's command replies write every number as a
JSON number in invariant culture. Rows S8a.NO.cover,.vein,.agree,.shrink,.immune, S8a.N10.oredensity; mutants M21 (SN
skips every third record), M22 (SN adds a phantom deposit), M23 (SE anchors 8 m off), M24 (SN ignores updates), M25 (SN's drill
gets through), M27 (the cube rule back on).

**The look (planet v2; launcher part `nl`).** SE's clock at noon, the camera at the spawn and at the far points (two per planet
biome, 13.5–40 km, only on seabeds no deeper than 450 m and never the void: Subnautica's noon light) at 8 m and 25 m above the
seabed, pitch −15°, held. Beyond 7.9 km SE's zone is FAR and Subnautica's picture is parked unless the charts follow the camera, so per far point the treadmill goes off (chart 0), the camera is teleported in site metres, the
treadmill goes on (`de.s8a chart {treadmill}`, run time) and the capture waits for the charts settled (unchanged 6 s, applied by
Subnautica, zone not FAR), Subnautica built twice, and retries a black frame (mean luma under 3) up to 3 times; each capture records
its chart, picture and window state; the session's treadmill is restored at the end; a capture per stop and the orbit shots; a contact sheet
`look-sheet. row S8a.NL.look. SE's sun dev phase (`de.s7 time c`) lasts across the re-binds a
chart step causes (SunSync), until the world unloads or `de.s7 time off`; `S8a.M29`
(SN: water-column spawns and flora off beyond the map) turns it red. A gate row before any deploy of a regenerated planet.

**The Aurora distress beacon (Stage E; `S8aBeacon`, SE).** On the world's first session, wherever the local player is in SE's
outside DE_4546B's gravity the placeholder text says the signal is faint and relayed), and once
more on the first arrival in the planet's gravity (`MyGravityProviderComponent.IsPositionInRange`) when the GPS is missing then,
SE adds the GPS "Aurora distress signal" at the crash site (the Aurora's root,
SN (764.4, 0, −138.6) at the waterline, read read-only from the install's aurora scene) and shows a placeholder radio text once
(chat and HUD notification; S15 writes the lore). Once per world: the marker `<store>\<storeKey>\beacon.done`, or the GPS
already in the list. `de.s8a beacon` (dev) re-arms; `DEEPENGINEERS_BEACON=off` turns it off. Row `s8a.beacon`; S8a.N11.beacon.

**Ores as voxels.** The nine SE ores convert to this mod's own block types in the installed palette's null slots (format convert.orelook: Iron_02 95, Nickel_01 96, Silicon_01 107, Cobalt_01 108, Magnesium_01 117, Silver_01 118, Gold_01 119, Platinum_01 121, Uraninite_01 122). Subnautica fills them by a **prefix** on `WorldStreamer.Start(VoxelandBlockType[] blockTypes, WorldStreamer.Settings settings)`: `VoxelandBlockType.FilledMaterial(Material, string)`, the layer of GreyRock (the template), `new Material(GreyRock.material)` with its albedo-like texture properties (`Material.GetTexturePropertyNames`) set to our generated ore texture, `RuntimeInit(int)`; a slot the game already fills is left alone. `de.s8a orelook {as: type | "ours"}` (Subnautica, dev) copies a palette type's material into the ore materials in place (the paint test). The converter counts the solid surface voxels it writes from an ore material and those with the ore's type (`de.s8a orevoxels {reset}`, SE, dev); S8a.M30 writes them as rock 84. The deposit props are off unless `DEEPENGINEERS_S8A_ORE_PROPS=on` (part `no` only). Rows S8a.NO.palette,.voxel,.look; mutant S8a.M30.

**The sample.** One chunk of the
Subnautica map (Grassy Plateaus, 640 m, cut read-only from the user's install into `%LOCALAPPDATA%\DeepEngineers\terrain\chunks\`:
`chunk-gp1-v1.bin`, `chunk-gp1-v1.tsv`, the placement `sample-v1.txt`; never shipped) is served by the planet's generator at
site-v1 SN (4158, -19563), exactly as Stage F2 serves the ring: the `MyPlanetStorageProvider.ReadRange` prefix of S8aRing answers
content reads at LOD <= MaxLod whose planet-local box meets the sample's zone (its square in the anchor's tangent frame, padded,
and its altitude range) with `S8aChunkField.ServeBox` (the chunk's own field blended into SE's heightmap over 80 m inside the
square; plain IEEE double arithmetic, square roots only; materials SE's own, read as the original does); the Intersect postfix and
the window's LOD-3 classification widen its boxes; everything else is SE's own generator. Deterministic: twin = C# byte for byte
(`tests/s8a/vectors/sample/`, `S8aChunkFieldTests`), and in game `de.s8a sample {serve}` compares the C# bytes with SE's generator
read (S8a.NS.determinism). The window's converters give every solid voxel where the chunk's weight is at least one half the chunk's own Subnautica block type (`S8aSample.NativeTypeAt`, Wire `S8aFieldSampler.NativeType`; ores keep their ore types), so the plateau's grass-bearing and blend types survive the SE round trip. The entities: SE places the chunk's records (turned, re-based, rotated through the current chart's frame)
and writes those within their cell level's radius + 60 m to `<store>\<storeKey>\sample-entities.tsv` (Wire `S8aSampleEntities`,
magic `de-s8a-sample 1`), at most twice a second when the camera moved 20 m or the chart changed; Subnautica spawns a baked record
by class id and fills a slot with its own loot table

transient
(`MakeEntityTransient`), within the record's radius, moved on a chart step. Off unless `DEEPENGINEERS_S8A=on` and
`DEEPENGINEERS_S8A_SAMPLE=on` (the launcher sets it for the parts `ns` and `nl`; `run` never does). With it on, the window's and the
charts' sculpt hash is fnv1a64(utf8 sculptHash x16 + '|sample|' + identity x16), identity = fnv1a64(utf8 'sample|types|' + placement x16 +
'|' + chunk bin x16 + '|' + chunk tsv x16) of the store's files ('missing' for a file not built): a dev-only switch outside the
pinned canonical text (the sample is not in `ext s8a terrain 6` either; the planet-wide chunk placement folds into "ring.field" and "store.key" at the
regeneration's pin bump). Commands (SE, dev): `de.s8a sample` (status, the cost table against SE's own generator), `{serve: boxes}`,
`{columns, fromY, toY}` (the field's top, the chunk's own top, the heightmap, the chunk's weight, the native column), `{where:
[[u, v],...]}` (tangent points in the current chart's SN coordinates); Subnautica `de.s8a sample {radius, on, clear}` (records and
live objects per family). Rows `s8a.sample.init` (SE), `s8a.sample` (Subnautica, 1 Hz); launcher part `ns`: S8a.NS.ready,.cut,.roundtrip,.placement,.terrain,.seam,.cost,.scope,.determinism,.entities.

**Budgets.** SE game thread: the guard decision and the `RangeChanged` enqueue ≤ 20 µs per call; the save postfix
≤ 50 ms per 1,000 dirty cells; the restore ≤ 2 s per 10,000 cells (in `BeforeStart`). SE workers: drill converter
(one thread) and window worker (one thread), below normal; a batch rewritten at most every 250 ms + 50 ms per MB of its
file (coalescing), one write ≤ 50 ms for ≤ 10 MB; window p95 ≤ 250 ms per surface batch, ≤ 5 ms per water or rock batch;
window worker ≤ 64 MB transient. Wire: ≤ 64 new notice rows per tick, ≤ 1,024 rows, a 4,096-notice queue. Subnautica:
≤ 8 batch reloads in flight, ≤ 64 clipmap cell reloads per notice (beyond that, levels 0–1 for the box and level 2–3
once per batch), ≤ 0.5 ms main-thread work per frame (manifests are parsed off the main thread). Disk ≤ 2,048 MB.
Latency: a drill edit shows in Subnautica P95 ≤ 1.0 s, max ≤ 2.5 s (S8a.N1).

**Fail-safe.** (1) Store root not writable or disk full: `DE.S8a STORE REFUSE <reason>` once; that layer's flag clears
in the header (`DrillOn`/`WindowOn`), Subnautica keeps every version it already serves, SE's own overlay keeps working
and saving (SE keeps the hole; Subnautica may lack it, visible in `s8a.status`). (2) A file that fails Subnautica's check
(size or FNV, version < 4, a parse error) is never served: the drill layer keeps the previous good version or the
install path (an install batch is never replaced by an empty one, which `LoadOctrees` would produce), the window answers
no file (water) and `areaBuilt` stays false while SE's surface is near; `reloadFailures` counts. (3) A reload that does
not complete in 5 s is retried once; at 10 s the notice counts as applied with a failure (the feed never stalls).
(4) Overlay restore failure (decode, hash or site version): `DE.S8a OVERLAY REFUSE <reason>` once; the tiles stay the
provider's, the next saves carry the original file bytes unchanged (so SE's cleanup does not drop it) and this
session's edits are not persisted (logged). (5) New Subnautica generation or link epoch: stream reset, SE resends from
the oldest unacknowledged notice, Subnautica re-reads both manifests. (6) World change: SE queues `StoreReset` for both
layers; Subnautica drops its lookups and reads the new store's manifests. (7) Plugin absent: SE loads the world (the
`.vx2` is inert); Subnautica never reads the store.

### 3.4 Look match at the band (S7t T5; not part of the site version)

Added S7t. Code: SE `,
Subnautica `; commands and rows in §5.4 "S7t look match"; nothing persisted (§1.1).
- **Target.** Subnautica's palette: under water and at the surface it is the reference, and the planet seen from space
 stays SE's. mean RGB /
 luminance of the same screen regions; SE's half was taken before EarthLike's AtmosphereSettings reached DE_4546B):

 | Band | SE | Subnautica | What SE lacks |
 |---|---|---|---|
 | sky top | (135,221,235) L204 | (123,176,234) L169 | a deeper blue (SE's default Rayleigh 20/7.5/10 scattered green) |
 | sky at the horizon | (153,234,246) L218 | (197,241,255) L233 | the blue-white haze band |
 | far water | (38,72,96) L67 | (129,155,187) L152 | aerial perspective (linear fog fraction ≈ 0.37 toward the haze colour) |
 | near water | (27,68,104) L62 | (65,97,122) L92 | lighter, less saturated (linear fog fraction ≈ 0.07 at ~550 m) |
 | shadows | hard, dark | soft | ambient against sun (lit/shadow ratio; `tests/s7t/compare.py` measures it) | S7t (integrator): **one source** for these eight colours: `src/DeepEngineers.Wire/S7tBandTargets.cs`
 (`HazeHorizon` = Subnautica's sky at the horizon, `HazeLow` = its far water). SE's `fogColor` default and Subnautica's
 skirt colours (`HorizonRules.DefaultHorizonRgb`/`DefaultLowRgb`, §5.4 "S7t altitude") read it; the oracle's table
 (`tests/s7t/band_compare.py` `TARGETS`) is its Python twin, pinned by Wire.Tests `S7tBandTargetsTests`.

- **Weight.** SE: 1 at and below the band's enter altitude (live `ViewThresholds.ApproachEnterY`, +1,000 m since T3),
 smoothstep to 0 at enter + `ramp` (1,500 m), 0 below −1 m (nothing under water), 0 whenever the picture is off (no
 world or the view driver stale > 30 ticks, bridge down, Subnautica not Alive, not the Site policy, off the dive
 zones), eased at most 1/`ease` per second (0.8 s: a peer loss restores SE's own look within 0.8 s). Subnautica: 0 at
 and below +20 m (`low`), smoothstep to 1 at +300 m (`high`) and above, 0 under water, 0 when SE is not Alive, C5 older
 than 1.5 s or not `pictureSite`; eased the same way.
- **SE's match set at the band** (each key multiplies (×) or replaces (=) SE's own value in the copy SE sends its
 renderer each frame; unset keys keep SE's own):

 | Key | SE member | At the band | Derivation |
 |---|---|---|---|
 | `sun` | `MyEnvironmentLightData.SunDiffuseFactor` | ×0.85 | shadow contrast R = 1 + S·n·l / A; SE's hard light (R ≈ 5, HYPOTHESIS) toward Subnautica's soft (R ≈ 2.2): S/A × 0.47 with `ambient` |
 | `ambient` | `…AmbientDiffuseFactor` | ×1.8 | as above (the eye adaptation keeps the overall exposure) |
 | `atmoAmbient` | `MyRenderPlanetSettings.AtmosphereIntensityAmbientMultiplier` | ×1.25 | the sky's share of the ambient (bluish fill in the shadows, as Subnautica's uSky ambient) |
 | `skybox` | `MyEnvironmentLightData.SkyboxBrightness` | ×0.5 | no stars in Subnautica's daylight sky |
 | `contrast` | `MyPostprocessSettings.Data.Contrast` | ×0.90 | Subnautica's softer tone at the band |
 | `saturation` | `…Data.Saturation` | ×0.88 | SE's near water more saturated (27,68,104) than Subnautica's (65,97,122) |
 | `fog` | `MyRenderFogSettings.FogMultiplier` | =0.25 | the pair asks fog ≈ 0.07 at ~550 m and ≈ 0.37 far (M·(1 − e^(−D·d)): M 0.46, D 0.0003); halved because EarthLike's FogIntensity already adds aerial perspective |
 | `fogDensity` | `…FogDensity` | =0.0003 | as above (per metre, HYPOTHESIS: SE's fog law) |
 | `fogColor` | `…FogColor` | =(197,241,255) | Subnautica's horizon haze band |

 Live but unset by default: `brightness`, `tint`, `vibrance`, `bloom`, `exposure`, `eyeTau`, `whitePoint`, `vignette`,
 `vignetteLength`, `ambientSpec`, `aoDir`, `aoIndirect`, `envSkybox`, `envAtmo`, `shadowFade`, `sunColor`, `fogAtmo`,
 `fogSky`, `atmoInt`, `atmoDesat`, `clouds`, and the planet's atmosphere `rayleigh`, `mieColor`, `mieG`, `mieHeight`,
 `rayleighHeight`, `atmoIntensity`, `atmoFog` (sent through the planet's render component, never written into
 `MyPlanet.AtmosphereSettings`).
- **SE's atmosphere** (DE_4546B, `src/DeepEngineers.WorldMod/Data/PlanetGeneratorDefinitions.sbc`, read at world load):
 SE's renderer uses β_R = (5.8e‑6, 1.35e‑5, 3.31e‑5) / `RayleighScattering` and β_M = 2e‑5 / `MieColorScattering` per
 channel
 From EarthLike's values: `RayleighScattering` Y 84 → 74 (EarthLike's zenith
 ratio 0.11–0.36: 0.31–0.50: 1 against Subnautica's 0.24: 0.52: 1, linear: green low), `MieColorScattering`
 (44, 62, 150) → (50, 60, 90) (a blue-white instead of a warm haze), `FogIntensity` 1.0 → 1.4 (aerial perspective).
 EarthLike's values stay in the file's comment.
- **Subnautica** keeps its sky (uSky scattering, `skyFogDensity`/`skyFogColor`, clouds), its water and its above-water
 grade (`UwePostProcessingManager`'s profile: `ColorGradingModel`, enabled by the options' colour-grading mode;
 nothing biome- or surface-specific); only `shadow` (`Light.shadowStrength` of `uSkyManager.main.SunLight`, =1.0) and
 `ambient` (`uSky.uSkyLight.ambientLight`, ×0.85) move toward SE's harder light; `sun` (`uSkyLight.SunIntensity`) and
 `exposure` (`uSkyManager.Exposure`) are live, unset. The sun's direction and the time stay S7a's (`DayNightApply`).
- **Numbers for T4.** Near water at the band
 toward (65,97,122) L92 from (27,68,104) L62 (≈ ×2.2 linear luminance, less saturated): `ambient`
 (AmbientColorIntensity) 0.2 → 0.
 11:04 choice; `JWaterLight` the alternative). Far water toward (129,155,187) L152 comes from the aerial perspective
 (the atmosphere's FogIntensity and the fog keys above), not from the Water Mod (its `fog` is the under-water fog).
 HYPOTHESIS: whether SE's environment fog and the atmosphere's FogIntensity reach the Water Mod's surface billboards;

 *In force:* `WaterLook.Defaults` as built by se-composite — `colorIntensity` **8**,
 `ambient` **0.3**, `specularIntensity` **0.8** (§3, the water-look block's "S7t T4" note); the 7 / 0.35 above was this table's first-step
 suggestion, not a second set of defaults.

---

## 4. Damage scaling and health ownership

Two separate lines, as the template requires: §4.1 converts amounts; §4.2 says who owns health. The scaling is a
**gameplay scale, chosen**; every input number is VERIFIED.

### 4.1 Damage scaling (`damageScaleVersion = 1`)

**Unit on the wire:** Subnautica hit points (`SN_HP`), the `originalDamage` passed to `LiveMixin.TakeDamage` before
any `DamageModifier` (captured by our postfix on `TakeDamage`, VERIFIED signature). Exact member
`public bool LiveMixin.TakeDamage(float originalDamage, Vector3 position = default, DamageType type =
DamageType.Normal, GameObject dealer = null)`, returning true when the call killed. It acts only if `health > 0`, not
`invincible`, and not `GameModeUtils.IsInvisible && (invincibleInCreative || a BaseCell on the object)`; then the
damage taken is `shielded ? 0: DamageSystem.CalculateDamage(originalDamage, type, gameObject, dealer)` (global
`damageMultiplier`, every `DamageModifier.ModifyDamage` in the children, Player armour, the `nodamage` cheat),
`IOnTakeDamage` receivers cached at `Start` are notified, and a shielded object returns there. The argument
`originalDamage` is untouched by all of that, so a Harmony prefix and postfix read it by name (`originalDamage`,
`position`, `type`, `dealer`); the prefix records whether the gate above passed (`__state`), and only then does the
postfix emit (S6 `DamagePatches`, §4.2).

**Reference values.**

| Side | Item | Value | Source (VERIFIED) |
|---|---|---|---|
| Subnautica | Player max health | 100 | `subnautica.md` §3c (prefab read) |
| Subnautica | bites (`MeleeAttack.biteDamage`, DamageType Normal) | biter 7, boneshark 30, crabsquid 40, reaper 80 (interval 4 s), ghost leviathan 85 | `subnautica.md` §3c prefab table |
| Subnautica | reaper and ghost against a `SubControl` target | `cyclopsDamage` 220 and 250 (prefab values; the `ReaperMeleeAttack` class default is 160) | `ReaperMeleeAttack.GetBiteDamage`, `GhostLeviathanMeleeAttack.GetBiteDamage`; **proxies carry no `SubControl`, so they take the plain bite** (`biteDamage`). `ReaperMeleeAttack.OnTouch` bites only with `reaper.Aggression ≥ 0.5` and after `biteInterval`, then calls `base.OnTouch`; its grabs need a concrete `SeaMoth`/`Exosuit` |
| Subnautica | ampeel shock | 15 Electrical (`electricalDamage`, class default) to any target with a live `LiveMixin`, at most every `electricalDamageInterval` 2 s, no `CanDealDamageTo` check, **no dealer passed** (`cyclopsDamage` 50 only for a `SubControl`) | `ShockerMeleeAttack.OnTouch`; `OnMouthTouch` also calls `base.OnTouch` (a normal bite) |
| Subnautica | sea dragon shove, swat, bite; crabsnake vehicle path | Normal, default position, the creature as dealer | `SeaDragonMeleeAttack`, `CrabsnakeMeleeAttack` |
| Subnautica | crashfish explosion | `Crash.Detonate`: `DamageSystem.RadiusDamage(maxDamage 50, crash position, detonateRadius 5, Explosive, crash)`; overlap sphere on all layers; one `TakeDamage(50·f, crash position, Explosive)` per `LiveMixin` (ancestor of an overlapped collider), no dealer; `f = max(0, (5 − d)/5)` with `d` measured to the `LiveMixin`'s transform (a proxy: its section root) | `Crash.cs`, `DamageSystem.cs` |
| SE | character health | `SpaceCharacterHealth` MaxValue 100, wired to `Default_Astronaut` through `CharacterStatComponent`; `MaxHealthBuff` raises MaxValue through levels 100, 110, 120, 130, 140, 150 | `Content\Data\Stats.sbc`, `EntityComponents.sbc`, decomp `MyMaxHealthBuff` |
| SE | character damage path | `MyCharacter.DoDamage`: amount × `CharacterGeneralDamageModifier` (default 1); `EnvironmentDamageMultiplier` only for type Environment; does nothing when the character cannot die (`CharactersCanDie` false in Creative) | decomp `MyCharacter.cs`, `MyFakes.cs` |
| SE | character damage path, exact (S6) | `public bool MyCharacter.DoDamage(float damage, MyStringHash damageType, bool updateSync, long attackerId = 0, MyStringHash? extraInfo = null)`: returns false (nothing applied) in a safe zone without damage, for an admin with Invulnerable, when the instance `CharacterCanDie` is false (static `CharactersCanDie` false in Creative unless `MyFakes.CHARACTER_CAN_DIE_EVEN_IN_CREATIVE_MODE`, and the local player's serial id is 0), when `StatComp` is null, or when `attackerId` is the character itself; else `MyDamageSystem` before-handlers, then `StatComp.DoDamage` → `Health.Decrease` at once (`MyEntityStat.Value` drops in the same call); the "hurt" animation event and camera shake play | decomp `MyCharacter.cs`, `MyCharacterStatComponent.cs` |
| SE | block integrity | `MyCubeBlockDefinition.Init`: MaxIntegrity = Σ count × component MaxIntegrity (no override in these four definitions); SteelPlate 100, MetalGrid 30 | `CubeBlocks_Armor.sbc`, `Components.sbc`, decomp |
| SE | large light armor cube `LargeBlockArmorBlock` | SteelPlate 20 + 5 → **2,500** | |
| SE | large heavy armor cube `LargeHeavyBlockArmorBlock` | SteelPlate 135 + 15, MetalGrid 50 → 15,000 + 1,500 = **16,500** | |
| SE | small light armor cube `SmallBlockArmorBlock` | SteelPlate 1 → **100** | |
| SE | small heavy armor cube `SmallHeavyBlockArmorBlock` | SteelPlate 4 + 1, MetalGrid 2 → 500 + 60 = **560** | |
| SE | block damage path | `MySlimBlock.DoDamage`: amount × `BlockGeneralDamageModifier` × min(grid, root grid `GridGeneralDamageModifier`) × definition `GeneralDamageMultiplier` (all default 1) × `DamageRatio` (1 when fully built); does nothing unless `MyCubeGrid.BlocksDestructionEnabled` (world `DestructibleBlocks` on, grid not immune) | decomp `MySlimBlock.cs`, `MyCubeGrid.cs`, `MyObjectBuilder_CubeBlockDefinition.cs` |
| SE | block damage path, exact (S6) | `public bool MySlimBlock.DoDamage(float damage, MyStringHash damageType, bool sync, MyHitInfo? hitInfo, long attackerId, long realHitEntityId = 0, bool shouldDetonateAmmo = true, MyStringHash? extraInfo = null)`: false only for `damage ≤ 0`; with `sync` on the server (single player is the server) `FatBlock.ReceivedDamage(…)` may consume it, else `DoDamageSynced` sends it and calls the local `DoDamage(damage, type, hitInfo, addDirtyParts, attackerId, extraInfo)`, which applies the modifiers above **only if `CubeGrid.BlocksDestructionEnabled || ForceBlockDestructible`**, then `DoDamageInternal`: × `DamageRatio` (= 2 − BuildIntegrity/MaxIntegrity; damage leaves `BuildIntegrity` unchanged, `MyComponentStack.ApplyDamage`, so it stays 1 on a fully built block), safe-zone check, `MyDamageSystem` before/after handlers, then `AccumulatedDamage += damage`; the grid applies it in `ProcessDamageApplication` (scheduled `OnceAfterSimulation`), or at once when it destroys the block. **The return value does not say that integrity changed**: the applied amount is the drop of `Integrity − AccumulatedDamage` across the call | decomp `MySlimBlock.cs`, `MyCubeGrid.cs`, `MyComponentStack.cs` |
| SE | SE's own creatures, for scale | SpaceSpider CharacterDamage 35, Wolf 12, GridDamage 0 | `Content\Data\Bots.sbc` |

**Rule (chosen):** a light armor cube of the target's grid size, and an SE character, are each as tough as a
Subnautica diver (100 SN_HP).

| Target class (sent by SE in `GridAdd` / implied for characters) | Factor (SE units per SN_HP) | SE unit |
|---|---|---|
| Character (`k_char`) | 100 / 100 = **1.0**, absolute (does not scale with `MaxHealthBuff`) | health points |
| Small-grid block (`k_small`) | 100 / 100 = **1.0** | integrity |
| Large-grid block (`k_large`) | 2,500 / 100 = **25** | integrity |

**Per Subnautica damage type (the one name set used everywhere):**

| SN DamageType (source) | v1 | SE damage type (`MyStringHash.GetOrCompute`) | Amount applied |
|---|---|---|---|
| Normal (melee bites; sea dragon shove/swat; crabsnake) | forwarded | `DeepBite` | `originalDamage × k(target)` |
| Explosive (`DamageSystem.RadiusDamage`, crashfish) | forwarded (C8 flag "radius") | `DeepBlast` | `originalDamage × k(target)` |
| Electrical (ampeel `ShockerMeleeAttack`, 15) | forwarded | `DeepShock` | `originalDamage × k(target)` |
| Collide (rams) | not forwarded; impulses are v2 | n/a | n/a |
| Heat, Fire, Poison, Acid, Cold, Puncture, Pressure, Drill, LaserCutter, Radiation, Starve, Smoke, Undefined | not forwarded (not expected on proxies in v1); counted in the SN log | n/a | n/a |
| Abyss crush (SE only, not from Subnautica) | SE plugin | `DeepPressure` | below |

*S11:* the S6 rows above are unchanged (unit 1). The hazard types Heat, Poison, Acid, Cold, Fire and
the mechanism is dormant) on the ghost Player or the character section now travel as S11 records (C8 unit 17 `HazardHp`, SE types `DeepHeat` …
`DeepSmoke`, k = 1.0 per SN_HP, a per-type per-second cap); heals, oxygen, the crabsquid EMP, grabs and bite pushes as
units 18–22; Pressure stays out (crush depth is S16). §5.4 "S11 conventions".

**Abyss rule (SE only; rates chosen, O7).** On `DE_4546B`, every 100 ticks:
- crush zone below SN y −2,000 m: every block whose centre is below it takes `DeepPressure` = MaxIntegrity × (0.10 +
 0.10 per 100 m below −2,000 m); a character takes 10 HP + 10 HP per 100 m below. A hull sinking at 5 m/s loses its
 lowest block after about 13 s. Active only where SE damage applies (Survival, destructible blocks, grid not immune);
 otherwise `DE.Abyss DAMAGE-INACTIVE <reason>` is logged once;
- floor guard below −2,500 m, in every world type: the downward velocity component (along local down) of grids and
 characters is clamped to 0 each tick, with a HUD notice;
- grids flagged by the terrain fail-safe (§7) take no crush damage; the guard still applies.

**Worked numbers (one hit; character percentages assume `MaxHealthBuff` level 0, MaxValue 100; at level 5, MaxValue
150, a reaper bite takes 53 %):**

| Hit | SN_HP | Character | Small light (100) | Small heavy (560) | Large light (2,500) | Large heavy (16,500) |
|---|---|---|---|---|---|---|
| Biter | 7 | 7 HP (7 %) | 7 (7 %) | 7 (1.3 %) | 175 (7 %) | 175 (1.1 %) |
| Ampeel shock | 15 | 15 HP (15 %) | 15 (15 %) | 15 (2.7 %) | 375 (15 %) | 375 (2.3 %) |
| Boneshark | 30 | 30 HP (30 %) | 30 (30 %) | 30 (5.4 %) | 750 (30 %) | 750 (4.5 %) |
| Crabsquid | 40 | 40 HP (40 %) | 40 (40 %) | 40 (7.1 %) | 1,000 (40 %) | 1,000 (6.1 %) |
| Reaper | 80 | 80 HP (80 %) | 80 (80 %) | 80 (14.3 %) | 2,000 (80 %) | 2,000 (12.1 %) |
| Ghost leviathan | 85 | 85 HP (85 %) | 85 (85 %) | 85 (15.2 %) | 2,125 (85 %) | 2,125 (12.9 %) |
| Crashfish at the section root | ≤ 50 | ≤ 50 HP | ≤ 50 | ≤ 50 | ≤ 1,250 | ≤ 1,250 |

(A reaper needs 9 bites, about 36 s at its 4 s interval, to destroy one large heavy armor cube.)

**Application rules**.
- SE drains C8 on its update thread after the mirror producer, at most 1,024 records per tick (the ring's capacity),
 only while the link is Up and the hello is acknowledged; a new Subnautica generation re-syncs the C8 tail (§5.2) and
 resets the dedup window. Every record is checked **in this order**, the first failure dropping it with that reason
 counted (`DamageReject`), never applying it:
 1. `Generation`: `snGeneration` ≠ the current Subnautica generation (C0). 2. `Epoch`: `worldEpoch` ≠ SE's current
 world epoch. 3. `Sequence` (dedupSeq 0), `Duplicate` or `TooOld`: the dedup window (below). 4. `Unit` (≠ 1),
 `Amount` (not finite, ≤ 0 or > 500 SN_HP), `Kind` (targetKind not 1 or 2), `Type` (snDamageType not Normal 0,
 Explosive 7 or Electrical 8), `HitPoint`. 5. `Entity`: a grid target that SE is not mirroring with a sent skin, or whose
 `rootEntityId` is not its root (the subgrid's GridAdd parent, else itself); a character target that is not the
 character SE exports in C1/CharacterAdd. `Section`: a grid section id not in the current or the previous SkinSet
 SE sent for that entity; a character section other than 0. `Frame`: the flag "frame = subgrid" set for a root
 grid or the character, or clear for a subgrid. 6. The application gates
 below (`NoBlock`, `NotDestructible`, `CannotDie`, `NotApplied`).
- **Dedup (exactly once).** The hit id is `(snGeneration, dedupSeq)`; Subnautica numbers its records 1, 2, 3… per
 generation in the order it builds them (gaps are records it dropped from its overflow queue: counted, not errors).
 SE keeps, per generation, the highest seq seen `hw` and a bitmap of the last 8,192 ids (> 1,024 ring + 4,096 overflow
 records in flight): seq > hw → new (window slides); `hw − 8,192 < seq ≤ hw` → new if its bit is clear (counted
 `reordered`), else `Duplicate`; seq ≤ hw − 8,192 → `TooOld`. An id is marked at step 3, so a replay of a record
 rejected later in the order is a `Duplicate`, never re-validated.
- Factor `k` (table above): character 1.0; grid by the `targetClass` SE sent in its GridAdd (1 large → 25, 2 small →
 1.0). Every record carries the factor Subnautica expects (`seScaleExpected`, from the target class SE sent); SE
 recomputes it, **always uses its own value** and counts and logs a mismatch (|Δ| > 1e-6·max(1, k)).
- **Grid target.** The SE-local hit point `l` (in the frame of `targetEntityId`, the grid or subgrid named by the
 record) selects the cell `round(l / GridSize)` with that grid's own `GridSize`, rounding half away from zero (as
 VRage `Vector3I.Round`, which `MyCubeGrid.WorldToGridInteger` uses), and `MyCubeGrid.GetCubeBlock(Vector3I)` (every
 cell of a multi-cell block maps to its block). If that cell is empty (box approximation), the occupied cell of the
 3×3×3 neighbourhood whose centre is nearest to `l` (ties: smallest x, then y, then z); none → `NoBlock`. Gate:
 `MySession.Static.Settings.DestructibleBlocks` and (`grid.BlocksDestructionEnabled` or `block.ForceBlockDestructible`),
 else `NotDestructible` (one `DE.Damage INACTIVE blocks: <reason>` line). Call: `block.DoDamage(amount × k,
 MyStringHash.GetOrCompute("DeepBite" | "DeepBlast" | "DeepShock"), sync: true, hitInfo: { Position = the grid's
 world matrix × l, Normal = unit vector from the block's world centre to that point (grid Up when zero) },
 attackerId: 0)` with the defaults `realHitEntityId` 0, `shouldDetonateAmmo` true. The applied SE amount is the drop
 of `Integrity − AccumulatedDamage` across the call; 0 → `NotApplied` (a safe zone, a fat block that consumed it).
 One block per record; damage beyond the block's remaining integrity is lost, as with any single SE `DoDamage`.
- **Character target.** Gate: `MyCharacter.CharactersCanDie` and the instance's `CharacterCanDie`, else `CannotDie`
 (one `DE.Damage INACTIVE character: <Creative|…>` line; this is the S6.M4 gate). Call: `character.DoDamage(amount ×
 k, type, updateSync: true, attackerId: 0)`; the applied amount is the drop of `StatComp.Health.Value` across the
 call; `false` or no drop → `NotApplied`. SE's own rules apply (safe zones, admin invulnerability).
- One `DE.Damage applied id=<gen>:<seq> unit=SN_HP k=<k> se=<amount×k> applied=<drop> target=<entity> kind=grid|character
 section=<id> block=<x,y,z> type=<DeepBite|…> amount=<SN_HP>` line and one `s6.damage` row per applied record;
 rejections are counted per reason (`s6.stats`) and rowed (`s6.reject`, the first 200 per reason per minute).
- Subnautica never sends `amount ≤ 0` (counted on its side) nor a type outside Normal, Explosive, Electrical; the
 checks above still apply to everything SE reads (the fake host's malformed records, S6.N10).

### 4.2 Health ownership
- **SE is authoritative** for character health (`MyCharacterStatComponent.Health`) and block integrity. All deaths,
 destruction, repair and welding happen in SE only.
- **Subnautica health never changes** on anything that represents SE: every proxy section (the character section
 included) and the ghost Player carry a `DamageModifier` subclass returning 0 (or `LiveMixin.shielded`);
 `LiveMixinData.invincibleInCreative` is false. Damage is captured by a postfix on `LiveMixin.TakeDamage`, not by a
 late-added `IOnTakeDamage` receiver: `LiveMixin.Start` caches the receivers of its own GameObject once (VERIFIED).
 A start-up self-check sends 1 SN_HP to a test section and to the Player and expects exactly one record each.
 Subnautica never kills a mirror.
- **S6 settlement.** A section's `LiveMixin` is added with `AddComponent` (its `Awake`
 takes pooled `DamageInfo`/events at once), then gets a `LiveMixinData` made with
 `ScriptableObject.CreateInstance<LiveMixinData>` **before its `Start`** (`maxHealth` is read through `data`):
 `maxHealth` 100, `invincibleInCreative` false, `knifeable` false, `weldable` false, `destroyOnDeath` false,
 `broadcastKillOnDeath` false, `passDamageDataOnDeath` false, `canResurrect` false, every effect null; then `health =
 maxHealth`, `invincible` false (it makes the game skip the call, so nothing would be emitted), `shielded` **true** (the
 damage stays 0 and `TakeDamage` returns right after notifying receivers: no sound, no effect, no death) and the
 zero `DamageModifier` (belt and braces for other `CalculateDamage` callers). The ghost Player is already `shielded`
 (S5a). The marker component on each section root carries: entity id, root entity id, section id, kind, grid size
 and target class, `isSubgrid`, and the per-step pose (`o, q, s`, `rel`, SE tick).
- **Emit rule (the `TakeDamage` postfix).** One record per call, only when: the prefix saw the game's gate pass (`health
 > 0`, not `invincible`, not invisible-mode-with-`invincibleInCreative`); `__instance` is a section's `LiveMixin`
 (an O(1) lookup, not `GetComponent`) or `Player.main.liveMixin`; `originalDamage` is finite and > 0 (else counted
 `zeroAmount`: a blast's edge gives 0); `type` is Normal, Explosive or Electrical (else counted per type); the
 Freedom gate passed (§5.4 C7); and, for the Player, the ghost rule placed the Player at the character this frame
 (ARCHITECTURE §5.11: character present, not seated, its own first/third-person view), else counted
 `playerNotAtCharacter`. A Player record is a character record (`targetKind` 2, section 0, the C1 character id) with
 the S6 flag `GhostPlayer`; in locked mode the Player's capsule is disabled, so in practice only the self-check and
 rare non-contact damage reach it. Records go to C8 through the producer's overflow queue (§5.1).
- **Start-up self-check (S6.N9, C7 `damageSelfTestPass`).** After the world is ready and the Freedom gate passed:
 the postfix is switched to capture mode, `TakeDamage(1f, default, DamageType.Normal, null)` is called on a hidden,
 collider-less test section (marker and `LiveMixin` as above) and on `Player.main.liveMixin`; capture mode builds the
 full records but keeps them local (never in C8: the Player record would hurt the SE character). Pass = exactly one
 record each, amount 1, type Normal, target kinds 1 and 2; then the test section is destroyed. Repeated by `de.s6
 selftest`; the flag is cleared at each world load until the check passes again.
- Destroyed or damaged blocks reach Subnautica as part and skin updates (C4); character death as a C1 flag.
- (S5a) The mirrored oxygen may reach 0 so Subnautica's own choke plays, but the ghost's suffocation death path
 (`Player.SuffocationDie` → `liveMixin.Kill`, which ignores `shielded`) is skipped by a Harmony prefix
 (`S5aPatches`); without the patch the mirror never goes below 1 unit. The engineer's suffocation death happens in SE
 (§2.10 "Oxygen rule").
- Subnautica creatures keep their own health (Subnautica owns it).
 "mobs in SN can't be killed"):* SE's guns, explosions and tools damage them — Subnautica resolves SE's C2 effect events
 (BulletStreak, Explosion, the tool sustains) against its creatures and calls `LiveMixin.TakeDamage(originalDamage,
 position, type, dealer)` on a LiveMixin whose GameObject carries a `Creature` (never a proxy section, never the ghost
 Player, so nothing is forwarded back to SE); the amounts are SE's own numbers through `k_char` (§4.1: a creature takes
 what an SE character would take), §5.4 "S7p-gameplay". The ghost Player is the dealer for the local player's rows.
- `MaxHealthBuff` raises the SE character's MaxValue up to 150 (six levels); `k_char` stays 1.0 per SN_HP (absolute HP).

---

## 5. Channels

One named file mapping, `Local\DeepEngineers_v1`, 80 MiB (0x05000000 bytes), page-file backed, created by Subnautica
with P/Invoke `CreateFileMappingW` (security descriptor §6) and opened by SE with `OpenFileMappingW`; both map it with
`MapViewOfFile`. No.NET `MemoryMappedFile`, no named events, no blocking call on a game thread (one bounded
exception: Subnautica's ≤ 100 ms acknowledgement poll inside `OnApplicationQuit`, §7). Each side polls once
per frame (Subnautica, from `Update` on the unscaled clock) or tick (SE).

### 5.1 Channel table

| Id | Name | Kind | Direction | Rate | Size bound | When full or late |
|---|---|---|---|---|---|---|
| C0 | Header and heartbeats | header words | both (each side writes its own block) | every game-thread frame/tick | 4 KiB | peer heartbeat older than 3 s in play, 120 s while either side is in a loading phase → PeerStalled; process exit, `snPhase ∈ {Quitting, None}`, a changed PID or generation → PeerExited (§6, §7) |
| C1 | `SeFrame`: camera + player pose + bones | seqlock | SE → SN | 60 Hz, in the `PrepareForDraw` postfix | 8 KiB (bones ≤ 160 × 48 B; the astronaut has 81; from S5 the 78 bones of the canonical skeleton `canon1`, §5.4; S7: all 81 with `ExtraBones`, and the interior extension at 0xB0–0xBF, §5.4 "S7 conventions") | overwritten each tick; reader retries ≤ 16 times, else keeps its last coherent copy; newest snapshot older than 100 ms while SE is alive → hold pose (no extrapolation beyond one tick), counted |
| C2 | `SeEntities`: entity poses, subparts, lights | seqlock table | SE → SN | 60 Hz, same tick as C1 | 512 KiB (1,024 entities × 96 B, 2,048 subparts × 64 B, 64 lights × 64 B; S7: the light rows written, and the effect-event window, 1,024 × 64 B at 0x40000 with its header in `pad_1c`, §5.4 "S7 conventions"; S8a: the terrain notices, a 64 B header at 0x50000 and 1,024 × 32 B rows from 0x50040, §5.4 "S8a conventions") | as C1; C2's tick must equal C1's, else the pair is re-read ≤ 4 times and the last coherent pair is kept (counted "incoherent"); more than 1,024 entities: farthest dropped first, logged |
| C3 | `SeModels`: ModelBegin/Chunk/End, MaterialDef, SkinnedBind | byte ring | SE → SN | on first sight of a model; producer budget ≤ 2 ms per SE tick | 64 MiB ring; chunks ≤ 4 MiB; a message never exceeds half the ring | producer (SE loader thread) tries once; no room → the model stays queued (newest-needed first); consumer drains every frame up to 4 ms or 48 MiB; parts referring to a missing model wait (logged after 10 s) |
| C4 | `SeParts`: grid/part/skin/room records | byte ring | SE → SN | on change (block add/remove/integrity, colour, split/merge, room change, epoch) | 8 MiB ring | producer (SE update thread) tries once; no room → the grid stays dirty and is re-sent; consumer drains every frame within the same 4 ms budget |
| C5 | `SeState`: view state, display mode, audio mix, pause, foreground, epochs, hello | seqlock | SE → SN | 60 Hz | 256 B | overwritten; Subnautica applies the latest; stale > 3 s → SE stalled |
| C6 | `SeTime`: hour angle, sun direction, jump counter | seqlock | SE → SN | 60 Hz | 64 B | overwritten; a changed jump counter means a single forward jump |
| C7 | `SnStatus`: phase, ready and streaming flags, presented SE tick, acks, applied settings, counters (S8a: the terrain acknowledgement block 0x8C–0xBB, §5.4 "S8a conventions") | seqlock | SN → SE | every SN frame | 256 B | overwritten; SE uses the latest; freshness rule in §5.4 |
| C8 | `SnDamage`: damage records | SPSC fixed-record ring | SN → SE | per hit (bite interval ≥ 1 s per creature: `MeleeAttack.biteInterval` default 1 s, VERIFIED; per-prefab values not all read) | 1,024 × 128 B | Subnautica keeps an overflow queue of up to 4,096 records and retries next frame; beyond that it drops the **oldest** and counts `damageDropped` in C7; SE drains everything each tick and validates every record (§4.1) |
| C9 | `SnCreatures`: creature table | seqlock table | SN → SE | 10 Hz (unscaled clock) | 256 × 48 B | overwritten; nearest 256 to the camera kept; v1 consumers: logs and tests (sonar/HUD later) |
| C10 | `Capture`: request and result | seqlock pair | request SE → SN (relayed from the test harness), result SN → SE | tests only | 4 KiB | one request in flight; a new request id replaces an unanswered one; result `failed` after 5 s |
| C11 | `Control`: JSON lines, four rings (CtlToSe, CtlFromSe, CtlToSn, CtlFromSn) | byte rings | test harness (or the SE `/de` chat relay) ↔ each plugin | dev builds only, ≤ 100 messages/s | 4 × 256 KiB | active only when the header's `devControl` is non-zero; the harness (Python) may wait; a plugin never waits: a reply that does not fit is dropped and counted. Release builds (both plugins compiled with `DEEPENG_RELEASE`) never read C11a/C11b and never serve a harness. **S9:** a release build keeps the SE `/de` chat relay for the user tuning commands only (`Wire.UserCommandRules`: `de.water`; `de.s7` band/lightScale/landmarks; `de.s7t` look/horizon/clouds; `de.s7p` fx; chat `/de help`, `/de version`): SE relays only those on C11c, and Subnautica reads C11c only while `devControl` bit 1 is set and bit 0 clear and answers anything else `ok:false` "developer command, not in the release build" |
| C12 | `SnStatics`: static primitives per 160 m batch | byte ring | SN → SE | when Subnautica streams static structures near the camera (first producer: S7, §5.4 "S7 conventions") | 4 MiB ring | producer tries once, re-queues the batch; SE drains every tick within 1 ms, rest next tick |

Dev control and captures use C10/C11 in the section only; there is no control-file channel. `devControl` (C0 0x038):
bit 0 = a Python harness is attached and is the producer of CtlToSe and CtlToSn; bit 1 = the SE plugin relays the
The two bits are exclusive (SE sets bit 1 only while bit
0 is clear; the harness refuses to attach while bit 1 is set), so every ring keeps one producer. Every change of
`devControl` by any writer is an atomic compare-and-swap on the whole word (C# `Interlocked.CompareExchange`; the
Python harness `lock cmpxchg`), never a read followed by a write.

**C11d consumer (S0, D27).** C11d (CtlFromSn) has one consumer at a time, the holder of a `devControl` bit: the
harness while it holds bit 0, SE while it holds bit 1. SE takes bit 1 (by compare-and-swap, only while bit 0 is
clear) on the first relayed `/de` command, consumes C11d only while it holds bit 1, and releases bit 1 after 60 s
without a relayed command. At attach, SE clears a bit 1 left by a crashed SE (only one SE holds the SE mutex). A bit 0

layout): restart the session so that creator rule 1 makes a new section, or clear the word by hand once the harness
is gone (dev builds only).

### 5.2 Region map (layout 1.0; pinned by the S0 layout tests)

| Region | Id | Kind | Offset | Size | Record size | Version |
|---|---|---|---|---|---|---|
| C0 Header | 0x00 (0) | 1 Header | 0x0000000 | 0x1000 | 256 | 1 |
| Ring control (ring r = 0…7 in the order C3, C4, C8, C12, C11a, C11b, C11c, C11d: head u64 at 0x1000 + r·0x80, tail u64 at 0x1000 + r·0x80 + 0x40) | 0x01 (1) | 2 RingControl | 0x0001000 | 0x1000 | 128 | 1 |
| C5 SeState | 0x50 (80) | 3 Seqlock | 0x0002000 | 0x1000 | 256 | 1 |
| C6 SeTime | 0x60 (96) | 3 Seqlock | 0x0003000 | 0x1000 | 64 | 1 |
| C7 SnStatus | 0x70 (112) | 3 Seqlock | 0x0004000 | 0x1000 | 256 | 1 |
| C10 Capture (request at +0x000, result at +0x800) | 0xA0 (160) | 5 SeqlockPair | 0x0005000 | 0x1000 | 256 | 1 |
| reserved | | | 0x0006000 | 0xA000 | | |
| C1 SeFrame | 0x10 (16) | 3 Seqlock | 0x0010000 | 0x2000 | 256 | 1 |
| reserved | | | 0x0012000 | 0xE000 | | |
| C2 SeEntities | 0x20 (32) | 4 SeqlockTable | 0x0020000 | 0x80000 | 96 | 1 |
| C9 SnCreatures | 0x90 (144) | 4 SeqlockTable | 0x00A0000 | 0x4000 | 48 | 1 |
| reserved | | | 0x00A4000 | 0xC000 | | |
| C8 SnDamage records | 0x80 (128) | 6 RecordRing | 0x00B0000 | 0x20000 | 128 | 1 |
| reserved | | | 0x00D0000 | 0x30000 | | |
| C11a CtlToSe | 0xB0 (176) | 7 ByteRing | 0x0100000 | 0x40000 | 8 | 1 |
| C11b CtlFromSe | 0xB1 (177) | 7 ByteRing | 0x0140000 | 0x40000 | 8 | 1 |
| C11c CtlToSn | 0xB2 (178) | 7 ByteRing | 0x0180000 | 0x40000 | 8 | 1 |
| C11d CtlFromSn | 0xB3 (179) | 7 ByteRing | 0x01C0000 | 0x40000 | 8 | 1 |
| C12 SnStatics | 0xC0 (192) | 7 ByteRing | 0x0200000 | 0x400000 | 8 | 1 |
| C4 SeParts | 0x40 (64) | 7 ByteRing | 0x0600000 | 0x800000 | 8 | 1 |
| reserved | | | 0x0E00000 | 0x200000 | | |
| C3 SeModels | 0x30 (48) | 7 ByteRing | 0x1000000 | 0x4000000 | 8 | 1 |
| end | | | 0x5000000 | | | |

Region table conventions of layout 1.0 (S0, D1; written in the C0 region table, 17 entries, and inside the layout
hash): id = channel × 16 + part (C0 0x00 and the ring-control page 0x01; C11a–d 0xB0–0xB3); kind 1 Header, 2
RingControl, 3 Seqlock, 4 SeqlockTable, 5 SeqlockPair, 6 RecordRing, 7 ByteRing; record size = the size of the
region's main record (header 256, ring-control entry 128, `SeFrame` 256, `SeState` 256, `SeTime` 64, `SnStatus` 256,
`SeEntity` 96, `SnCreature` 48, `DamageRecord` 128, `CaptureRequest` 256; a table's own header is not counted), and
for a byte ring its 8-byte message alignment; version 1 for every region. Reserved gaps have no entry.

**Layout decision for S5: layout 1.0 suffices; no re-pin.** S5 needs instanced parts with model,
matrix, colour, pattern offset and skin (PartAdd), part removal and recolour (PartRemove, PartRecolor), subgrids with a
parent and a relative matrix every tick (GridAdd `parentEntityId`, the C2 subpart table, D8), per-tick subpart and
held-tool matrices (the C2 subpart table), floating objects (C2 kind 3), skinned models with bone indices and weights
(ModelBegin flag `skinned`, 76 B vertices, SkinnedBind), a character with a model and bones every tick (CharacterAdd,
C1 bones ≤ 160) and the swim state (the C1 swim extension). Every one of these records exists in layout 1.0, sized for
this slice in S0. What S5 adds is meaning in fields that layout 1.0 carries but does not pin (`PartAdd.flags` and
`skinId`, `GridAdd.flags`, `CharacterAdd.flags`, C1 `bonesVersion`, the opaque model and part key texts) and one data
table (the canonical skeleton); they are pinned on their own (§5.4 "C1–C4 conventions for S5", `0x31dfd6f9befdef5d`),
as the C1 swim extension is (`0x832ba93ca25bc307`). No record, field, offset, capacity, message type or enumeration
value pinned by layout 1.0 changes, so `0x4f3a35f6fc1eaf31`, `Fixtures/layout-1.0.txt`, `tests/wire/layout_py.py`, the
LayoutTests and the swim extension's pins stay as they are, and O16 stays open. Limits accepted with 1.0 (each would
need a 1.1 if a real run shows it too small; S5 logs them): C2 holds 1,024 entities and 2,048 subpart rows per tick
(S5 writes rows only for subgrids, moving subparts and the tool, nearest first, and counts drops); PartAdd carries no
per-instance armor deformation (out of v1, ARCHITECTURE §5.8); MaterialDef has no first-person or metalness flag (S5
splits the character model by material and carries metalness in `PartAdd.flags`). Procedure if a later slice needs a
1.1 (written out for the builders; not used by S5): decide O16 first (step 3 compares the hash of the 1.0 subset, or
the minor tolerance of step 2 is dropped); change ` with `layoutMinor` 1, new records only in
reserved space or as new message types, no 1.0 offset moved; regenerate the canonical text as
`Fixtures/layout-1.1.txt` and pin its hash in the LayoutTests (net48 and net472); bring `tests/wire/layout_py.py`'s own
hand-written table to the same bytes; keep the C1 swim extension at 0x94–0x9F (or move it and update both of its pins,
`SwimWire` and `tests/s5a/analyze_s5a.py`); rebuild and deploy both plugins together (they ship the same
`DeepEngineers.Wire.dll`)

**Layout decision for S7: layout 1.0 suffices; no re-pin.** The interior flag, room records,
light table, static primitives and bone records exist in layout 1.0 (C1 flag bit 3, C4 `RoomSet`, the C2 light table,
C12, C1 bones ≤ 160); the effect events live in C2 region space past the layout's tables (0x40000–0x4FFFF) with their
window header in `SeEntitiesHeader.pad_1c`, the interior extension in the C1 pad (0xB0–0xBF). Every new meaning is
pinned by `ext s7 look 1` (`0x1f9b9b4a876a97f9`, §5.4 "S7 conventions"); no layout, swim, S5, S6 or S7a pin changes.
A C4 message type, a new ring or a new region (an SE → Subnautica event ring like C8) was rejected: each changes the
layout text, needs O16 decided and a coordinated re-pin of both plugins and the Python oracle.

**Layout decision for S8a: layout 1.0 suffices; no re-pin; O16 stays open.** The terrain
notices live in C2 region space past S7's events (0x50000–0x5803F, inside the C2 seqlock), the acknowledgement in the C7
pad (0x8C–0xBB), the batch data and the overlay in files; reliability comes from repeating unacknowledged rows and from
the manifests, not from a ring. Pinned by `ext s8a terrain 6` (`0x3188251ab11e2fec`, §5.4 "S8a conventions"); no
layout, swim, S5, S6, S7a or S7 pin changes. A reliable SE → Subnautica ring (a C4 message type or a new region) was
rejected for the same reason as in S7.

Ring capacities are the region sizes (powers of two); the head (producer) and tail (consumer) are monotonically
increasing u64 byte (or record) counters in the ring-control page, on separate 64-byte lines. A side writes only the
words it owns: the producer its head, the consumer its tail.

**Re-synchronisation after a restart (both directions).** When a consumer sees a new peer generation (or a new link
epoch), it sets its tail:= the current head of every ring it consumes, dropping in-flight data, and never writes the
producer's word. A restarted producer continues from the head it finds in the section and never zeroes it. Seqlock
`seq` words are never reset to 0 by a restarted writer (it continues from the even value it finds). The consumer then
waits for the resend (§6). "Every ring it consumes" is literal: Subnautica re-syncs C11c too on a new SE generation, so
harness commands in flight are dropped and the harness retries (S0, D23).

### 5.3 Delivery mechanisms
- **Seqlock.** `seq` is the first u32 of every seqlock record (offset 0x00). Writer: `seq` odd (store, then release
 fence), payload, `seq` even (release). Reader: read `seq` (acquire); if odd, pause and retry; copy; acquire fence;
 accept only if `seq` is unchanged and non-zero. ≤ 16 tries per region per frame. The writer is tested to make `seq`
 odd during the write (a counter that jumps even → even is a known failure mode).
- **SPSC fixed-record ring (C8).** Slot = counter mod capacity; full when `head − tail = capacity`.
- **Byte rings (C3, C4, C11, C12).** Messages are 8-byte aligned: `{u32 type, u32 payloadBytes}` + payload, padded to
 8 bytes. A message that would cross the end is preceded by a pad record (type 0); the consumer, seeing type 0,
 skips to the ring start. Messages larger than half the ring are rejected by the producer (models are chunked).
 Details fixed by S0 (D5): a pad record's `payloadBytes` = the bytes left to the ring end − 8 (the consumer ignores
 it); a pad at ring offset 0, two pads in a row, a message that crosses the end or exceeds half the ring, and
 misaligned or impossible counters (`head − tail` > capacity) are corrupt: the consumer consumes nothing and re-syncs
 its tail to the head (§5.2); "larger than half the ring" is measured on the framed, padded size; a producer that finds
 impossible counters or a misaligned head reports `Corrupt` and never writes.
- **Carried-in prototypes (migrated in S0).** The pre-contract `ByteRing.cs` (24-byte header inside the region,
 `[int length]` messages, −1 skip marker), `Seqlock.cs` (`[int seq][pad]` slot header, 8 tries) and
 `tests/wire_py/wire.py` were rewritten to this section in S0 (ROADMAP S0 "Carried-in code"); `tests/wire_py/` was
 removed at the S0 exit.
 pre-contract layout.
- **Ordering.** C3 and C4 are independent; Subnautica holds a part until its model and material are complete.
 `worldEpoch` in C2, C4, C5 and C8 lets each side drop anything from an old world.

### 5.4 Record layouts (little-endian; offsets in bytes from the record start; strings UTF-8 with a u16 length prefix; every pad is explicit)

Flags listed below without bit numbers take bits 0, 1, 2… in the listed order (S0, D4): C2 light flags 0 spot, 1
shadows, 2 on; C3 `ModelBegin.flags` 0 skinned, 1 hasTangents; C3 `MaterialDef.flags` "glass CW/CCW, alpha-masked" is
three bits, 0 glassCw, 1 glassCcw, 2 alphaMasked; C9 flags 0 hunting, 1 targetIsMirror. The `const` block of the
layout hash pins every flag and enumeration value (§6).

**C0 header:** 0x000 u32 `magic` (written last by the creator); 0x004 u16 `layoutMajor` = 1; 0x006 u16 `layoutMinor`
= 0; 0x008 u32 `byteOrderMark` = 0x01020304; 0x00C u32 `headerSize` = 0x1000; 0x010 u64 `sectionSize` = 0x5000000;
0x018 u64 `layoutHash`; 0x020 u32 `wireVersion` = 1; 0x024 u32 `siteVersion` = 1; 0x028 u32 `damageScaleVersion` = 1;
0x02C u32 `regionCount`; 0x030 u64 `qpcFrequency`; 0x038 u32 `devControl`; 0x03C u32 pad.
SN block: 0x040 u32 `snPid`; 0x044 u32 `snGeneration`; 0x048 u64 `snHeartbeatMs`; 0x050 u32 `snPhase`; 0x054 u32
`snFlags` (bit 0 bridgeMode, 1 ghostingDisabled, 2 stylesApplied); 0x058 u64 `snHwnd`; 0x060–0x07F pad.
SE block: 0x080 u32 `sePid`; 0x084 u32 `seGeneration`; 0x088 u64 `seHeartbeatMs`; 0x090 u32 `sePhase`; 0x094 u32
`seFlags` (bit 0 gateOpen, 1 terrainUp, 2 displayRefused, 3 failSafeActive); 0x098 u64 `seHwnd`; 0x0A0 u32
`seLoadStep` (last `MySession.LoadingStep` value, diagnostic); 0x0A4–0x0FF pad.
Region table: 0x100, `regionCount` × 32 B {0x00 u32 id, 0x04 u32 kind, 0x08 u64 offset, 0x10 u64 size, 0x18 u32
recordSize, 0x1C u32 version}.
- `snPhase`: 0 None, 1 Booting, 2 Menu, 3 LoadingSave, 4 ReadyIdle, 5 WarmingUp, 6 Active, 7 Quitting. Loading phases:
 1–3.
- `sePhase`: 0 None, 1 Menu, 2 LoadingWorld (set by a prefix on `MyGuiScreenLoading.RunLoad`), 3 InWorld, 4 Saving
 (prefix/finalizer on `MySession.Save(out MySessionSnapshot, …)`), 5 Exiting (prefix on
 `MySandboxGame.ExitThreadSafe`, priority First). Loading phases: 2 and 4. Timing reference: the section shows `LoadingWorld` at the `RunLoad` prefix (+0.027 s after the plugin's
 RunLoad-prefix log line, 5 Hz polling); the SE log line `DE.Phase Menu -> LoadingWorld` is written only when
 `RunLoad` returns, about 17–24 s later (the load itself). Timing checks use the section or the RunLoad-prefix line,
 never the `DE.Phase` transition line.
- `seHwnd`: SE's main window. S0 takes it from `Process.MainWindowHandle`, refreshed every 10 s while the link is up
 (HYPOTHESIS until S2 checks it against the render window, D35). From S2 SE's window calls use
 `MyWindowsWindows.WindowHandle` (the render-thread window), and S2 checks that the published `seHwnd` is that window.

**C1 SeFrame (0x2000 region):** 0x00 u32 seq; 0x04 u32 flags (bit 0 inWorld, 1 paused, 2 firstPerson, 3
cameraInAirtightRoom, 4 teleported, 5 characterPresent, 6 characterDead, 7 characterSeated, 8 characterSwimming);
0x08 u64 tick (`MySession.Static.GameplayFrameCounter`); 0x10 i64 qpcCapture; 0x18 f64 simTimeSec; 0x20 f32 simSpeed;
0x24 u32 worldEpoch; 0x28 f32 camPos[3]; 0x34 f32 camRot[4]; 0x44 f32 vFovDeg; 0x48 f32 near; 0x4C f32 aspect; 0x50 u32
cameraMode (0 other, 1 first person, 2 third person, 3 cockpit, 4 spectator); 0x54 u32 pad; 0x58 u64 characterId; 0x60
f32 charO[3]; 0x6C f32 charQ[4]; 0x7C f32 charS; 0x80 u32 boneCount (≤ 160, = `Model.Bones.Length`); 0x84 u32
bonesVersion; 0x88 f32 pelvisSn[3]; 0x94–0xFF pad; 0x100 bones: boneCount × 48 B (12 f32, SE model space rows); end ≤
0x1F00. S7: flag bit 3 `cameraInAirtightRoom` is written, the pad 0xB0–0xBF holds the interior extension and, with
`ExtraBones`, `boneCount` = `Model.Bones.Length` (§5.4 "S7 conventions"). From S5 (wording only): `boneCount` = 78 and the bones are in the canonical order `canon1`; `bonesVersion` =
the low 32 bits of the current CharacterAdd `modelKey`, 0 = no bones (§5.4 "Character").

**C1 swim extension.** Decision: the swim state travels in the first 12 bytes
of the C1 pad, 0x94–0x9F, not in a layout 1.1. The layout-1.0 field table (and so its hash `0x4f3a35f6fc1eaf31`) still
describes 0x94–0xFF as one pad; a reader without the extension sees pad bytes and ignores them, and an SE build before
S5a leaves `version` 0, which readers treat as "no extension". The extension is pinned on its own, so drift is still
caught: Wire `SwimWire` (offsets, codec, canonical text, FNV-1a 64 `0x832ba93ca25bc307`), `SwimWireTests.cs` (the bytes
lie inside the layout-1.0 pad, the hash, round trips, clamps), the Python twin `tests/s5a/analyze_s5a.py` (`C1_SWIM`,
the same text and hash, checked by its `--selftest`). Written with every C1 record (every SE tick, 60 Hz) from the swim
controller's last tick (`SwimWireFill`); idle (no character, dead, seated, off the planet, model off, eye above +5 m)
= flags 0, mode Walk, axes and speeds 0, `o2` 255, `fins` kept. Canonical text: `ext C1 swim 1 0x94 12` then one
`field swim <name> 0x<off> 1 u8|i8` line per byte.

| Offset | Type | Name | Value |
|---|---|---|---|
| 0x94 | u8 | `version` | 1 (0 = no extension written) |
| 0x95 | u8 | `flags` | bit 0 inWater (the swim latch: modes Swim, Surface, Fly), 1 underwater (eye < 0), 2 atSurface (mode Surface), 3 grounded (character support), 4 moving (any move axis non-zero), 5 jetpackSuppressed (latch on, head under), 6 controls (the model set the velocity this tick), 7 o2Override (the `o2` byte is the dev override) |
| 0x96 | u8 | `mode` | `SwimMode`: 0 Walk, 1 Swim, 2 Surface, 3 Fly |
| 0x97 | u8 | `fins` | `SwimFins`: 0 off, 1 basic (+1.9 m/s), 2 ultra (+3.2 m/s; default) |
| 0x98 | u8 | `speedH` | horizontal speed / reference, 0..255 = 0..1, clamped; reference = steady top speed of the last motor step = `MaxSpeed × 0.75` (direction maxima, fins, tool, surface included: 5.70 m/s plain forward, 8.1 m/s Ultraglide), 0 while the model does not control |
| 0x99 | u8 | `speedV` | \|vertical speed\| / reference, 0..255 |
| 0x9A | i8 | `moveForward` | body-relative move input × 127, clamped −127..127 (+ forward) |
| 0x9B | i8 | `moveStrafe` | + right |
| 0x9C | i8 | `moveVertical` | + up |
| 0x9D | u8 | `o2` | the suit's O2 fraction × 255, rounded half away from zero; the dev override `/de swim o2 <0..1>` when set (flag bit 7; the override is written even while the controller is idle); otherwise 255 when the suit cannot be read or the controller is idle, so the ghost never chokes on missing data |
| 0x9E | u8 | `depthClass` | `SwimDepthClass`: 0 surface, 1 safe, 2 unsafe, 3 crush |
| 0x9F | i8 | `vyFrac` | signed vertical speed / reference × 127 (+ rising) |

Subnautica reads it from the same C1 record its camera uses (the `tick` of that record names the sample in its
`DE.Arms`/`DE.O2` lines, the join key of `tests/s5a/analyze_s5a.py`): the arms show only with inWater set and the
camera under water, the OxygenManager is set to `o2/255 × capacity` with the ghost's health shielded; nothing in
Subnautica may hurt, kill, move or save the ghost (§1). Seeded failures (SE, dev builds): S5a.M1 fins claimed but not
applied, S5a.M2 `o2` stuck, S5a.M3 flags/mode/axes/speeds frozen.

Subnautica side (stage 2, `src/DeepEngineers.SN/S5aHost.cs`, `S5aRules.cs`, `S5aPatches.cs`; docs/S5A-STATUS.md §10):
the extension is decoded by the C1 reader with every fresh record (`FrameReader.Swim`, version 1 only; version 0 =
no extension: everything below stays off). **Arms:** the first-person view model (`MainCameraControl.main.viewModel`)
is shown only while inWater is set, the camera is under the sea, C1 `cameraMode` is FirstPerson, C5 says Underwater or
Surfacing and C1 drives the camera; it is posed at the camera rig's position with the rig's yaw (what the disabled
MainCameraControl writes to it) and animated through the Player animator's own parameters (`is_underwater`,
`on_surface`, `move_speed*`, `view_pitch`, `view_turn`) from the C1 fields; `ArmsController` is disabled while S5a
drives; no Subnautica motor or input runs (PlayerController stays disabled, the rigidbody kinematic). **Oxygen:** the
ghost's `OxygenManager` is set to `o2/255 × capacity` every frame through `AddOxygen`/`RemoveOxygen`; its Update (the
30 units/s surface refill) is disabled while mirroring; the ghost's own breaths are already off (FreezeStats). The real
`LowOxygenAlert`, `uGUI_OxygenBar`, `FMODGameParams` and `Player.SuffocationUpdate` read the mirrored value. **The ghost
never dies:** `liveMixin.shielded` stays on, a Harmony prefix skips `Player.SuffocationDie` (the only path from empty
oxygen to `liveMixin.Kill`, §4.2), and without that patch the mirror floors the units at 1 so suffocation never starts.
**Breath:** `Player.tookBreathEvent` is fired every breath period of the C1 depth class while underwater and
`PlayerBreathBubbles.MakeBubbles` every `delay` (the game's own cadence, which the ghost's `isUnderwater` = false would
otherwise never start). **PDA voice** stays suppressed by S1's entry-point prefixes (SoundQueue.Play,
VoiceNotification.Play, PDANotification.Play, Subtitles, popups), never by a bus mute (the breath, bubble and alert
emitters share the SFX buses).

**C1 locomotion extension.** The SE character's movement state out of the
water, so Subnautica's own walk, idle, run, jump and dive clips can animate the mirrored astronaut on a deck or an island
now that Subnautica's picture is shown above water (`Site` policy). It travels in the next 16 bytes of the C1 pad,
**0xA0–0xAF** (layout 1.0 describes 0x94–0xFF as one pad, `P(0x94, 0x6C)`, and the swim extension uses only 0x94–0x9F,
VERIFIED `SwimWire.cs` "0xA0-0xFF stay pad"); a reader without the extension sees pad bytes. Pinned on its own inside
the S7a conventions text (`field loco …` lines, §5.4 "S7a conventions"); version 0 = not written (an SE build before
S7a): Subnautica then keeps S5's sources. Written with every C1 record (every SE tick) by the SE plugin
(`LocoWireFill.Fill`, beside `SwimWireFill`) from the controlled character (`MySession.Static.LocalCharacter`, C1's);
no character = all zero except `version` 1 and `state` None.

| Offset | Type | Name | Value |
|---|---|---|---|
| 0xA0 | u8 | `version` | 1 (0 = no extension) |
| 0xA1 | u8 | `state` | `C1LocoState` (below) |
| 0xA2 | u8 | `flags` | `C1LocoFlags`: bit 0 grounded (the swim extension's character-support source: the same test SwimController uses for its `grounded` bit), 1 crouching (`MyCharacter.IsCrouching`), 2 jetpack (`MyCharacter.JetpackRunning`), 3 inWater (copy of the swim latch, swim flag bit 0), 4 seated, 5 dead (`MyCharacter.IsDead`), 6 ladder |
| 0xA3 | u8 | pad | 0 |
| 0xA4 | i16 | `vForwardCm` | the character's linear velocity (`MyCharacter.Physics.LinearVelocity`, m/s) along its horizontal forward (`WorldMatrix.Forward` with the `WorldMatrix.Up` component removed, normalised), cm/s, rounded half away from zero, clamped to ±32,767 |
| 0xA6 | i16 | `vStrafeCm` | along the horizontal right (`WorldMatrix.Right` likewise), cm/s (+ right) |
| 0xA8 | i16 | `vUpCm` | along `WorldMatrix.Up`, cm/s (+ up) |
| 0xAA | i16 | `yawRateCdeg` | the character's yaw rate about `WorldMatrix.Up` from consecutive ticks, centidegrees per simulation second, + = turning right (clockwise seen from above), clamped |
| 0xAC | u16 | `seMovement` | the raw `MyCharacter.GetCurrentMovementState` value (`MyCharacterMovementEnum: ushort`, diagnostic) |
| 0xAE | u16 | pad | 0 |

`state` from `MyCharacterMovementEnum` (VERIFIED `VRage.Game.MyCharacterMovementEnum`, `MyCharacterMovement`: mode =
value & 15 (`MovementTypeMask`), direction = value & 1008, speed = value & 3072), first match wins: no character → 0
None; swim latch on (swim flag bit 0) → 13 Swim (the swim extension drives); `Died` (6) → 12 Dead; mode 1 `Sitting` →
11 Seated; mode 7 `Ladder` or the `LadderOut` bit (16384) → 10 Ladder; mode 3 `Flying` → 9 Fly; mode 4 `Falling` → 8
Fall; mode 5 `Jump` → 7 Jump; mode 2 `Crouching`: direction 0 → 5 CrouchIdle, else 6 CrouchWalk; mode 0 `Standing`:
direction 0 (rotating in place included) → 1 Idle, else speed 0 → 2 Walk, 1024 (`Fast`) → 3 Run, 2048 (`VeryFast`) →
4 Sprint. Members: `GetCurrentMovementState` → `MyCharacterMovementEnum`,
`IsCrouching`, `IsSprinting`, `IsDead`, `JetpackRunning` (bool properties), `WorldMatrix` (`MatrixD`),
`Physics.LinearVelocity` (`Vector3`).

Subnautica side (S7a; `S5aHost` and `CharacterMirror` hooks, `S7aAnimRules` in the SN plugin): with the extension at
version 1, the swim latch off and `state` ∈ {Idle, Walk, Run, Sprint, Jump, Fall} (the **foot states**), the Player
animator is driven out of the water exactly as S5a drives it in the water (ArmsController disabled while driving,
`cullingMode = AlwaysAnimate`): `is_underwater` false, `on_surface` false, `move_speed_x` = `vStrafeCm`/100 × k,
`move_speed_z` = `vForwardCm`/100 × k, `move_speed_y` = 0 while grounded (Subnautica's own above-water rule,
VERIFIED `ArmsController.GetRelativeVelocity`) else `vUpCm`/100 × k, `move_speed` = the vector's magnitude (k =
`GroundAnimScalePermille`/1000 = 1.0, HYPOTHESIS for the look), `view_pitch` and `view_turn` as S5a, `jump` = state
Jump or Fall held ≥ `FallAnimMs` (450 ms, Subnautica's `Player.GetPlayFallingAnimation` delay, VERIFIED), `diving` =
state Fall ∧ `vUpCm` < −100 ∧ C1 `pelvisSn.y` > 0.5 m (falling towards the sea), `diving_land` false (Subnautica's
obstacle raycast is not reproduced in v1). Parameter names VERIFIED (`AnimatorHashID.move_speed`, `move_speed_x/y/z`;
`ArmsController.Update`/`SetPlayerSpeedParameters`/`UpdateDiving`/`InstallAnimationRules`: `is_underwater`,
`on_surface`, `jump`, `diving`, `diving_land`, `view_pitch`, `view_turn`); there is no grounded parameter in the
Player animator. Clip names from the S5-2a `s5.rig` row (the generic `player_view` rig): `player_view_idle`,
`player_view_runF_3p5`/`runB`/`runL`/`runR`, `player_view_sprint_forward`/`backward`/`left`/`right`,
`player_view_jump_START`, `player_view_jump_loop_HIGH`/`LOW`, `player_view_jump_hardland_max`, `DiveDown`,
`player_dive`, `DiveUp`, `DiveDown_loop`, `Dive_loop`, `DiveUp_loop`, surface swim `view_surface_swim_forward`/`left`/
`right`, `Idle_lean`, `Back_lean` (which state of the controller plays which clip is HYPOTHESIS, logged by the
`s7a.anim` row through `Animator.GetCurrentAnimatorClipInfo(0)`). Character source (S5's "Retarget" rule extended): a
foot state with the walk source `retarget` (default; `de.s7 walk retarget|sebones`, env
`DEEPENGINEERS_SN_WALKSOURCE`) uses Retarget (path G, Subnautica's clips); every other dry state (CrouchIdle,
CrouchWalk, Fly, Ladder, Seated, Dead, None) keeps SeBones (SE's own pose: the jetpack, crouch, ladder and seat poses
exist only in SE). First person: the S5a view-model arms show out of the water too when C1 `cameraMode` is FirstPerson,
C1 drives the camera, the camera is above the sea, C5 `seeThrough` is set (Subnautica's picture is shown), the picture
policy is `Site` (C5 flag bit 9) and `state` is a foot state, with `de.s5 fpbody=diver` (with `se` the S5 rule
`ArmsGate.ForceOff` still holds); the in-water S5a gate is unchanged. Seeded failure S7a.M5 (SN): the out-of-water
`move_speed*` forced to 0.

**C2 SeEntities:** header 0x00 u32 seq; 0x04 u32 entityCount; 0x08 u64 tick; 0x10 u32 worldEpoch; 0x14 u32
subpartCount; 0x18 u32 lightCount; 0x1C–0x3F pad. Entities from 0x40, 96 B each: 0x00 u64 entityId; 0x08 u32 kind (1
root grid, 2 character other than the player, 3 floating object); 0x0C u32 flags (bit 0 static, bit 1 in the camera's
group; S7a: bits 2 `ghostShown` and 3 `ghostAllowed` on the placement ghost's row only, "S7a conventions"); 0x10 u64 groupId; 0x18 f32 o[3]; 0x24 f32 q[4]; 0x34 f32 s; 0x38 f32 k; 0x3C f32 v[3]; 0x48 f32 ω[3]; 0x54
u32 partsVersion; 0x58 u32 skinVersion; 0x5C u32 pad. Subparts from 0x18040, 64 B each: 0x00 u64 ownerId; 0x08 u64
partKey; **Subgrids are not entity records** (D8): a subgrid appears in the
subpart table with `ownerId` = the root grid of its physical group and `partKey` = the subgrid's entity id, and its
matrix relative to the root grid, composed in SE space; Subnautica parents it under the root grid's Entity. Write order: SE writes C2 for a tick before C1 of the same tick, so a reader that saw C1(N) finds C2(N); Subnautica re-reads C2 up to 4 times while its newest tick is behind the C1 tick its camera used, then holds the newest C2 snapshot at or before that tick (counted `incoherent`). Lights
from 0x38040, 64 B each: 0x00 u64 ownerId; 0x08 f32 pos[3]; 0x14 f32 dir[3]; 0x20 f32 colour[3]; 0x2C f32 range; 0x30
f32 intensity; 0x34 f32 coneDeg; 0x38 u32 flags (spot, shadows, on); 0x3C u32 pad. S7 writes the light rows (`pad_3c`
= f32 `falloff`, flags bit 3 `Character`, version nibble 28–31), the effect-event window at 0x40000 and its header in
`pad_1c` (§5.4 "S7 conventions").

**C3 messages** (framed `{u32 type, u32 payloadBytes}`, payload offsets below):
- 1 `ModelBegin` (64 B): 0x00 u64 modelKey (FNV-1a 64 of the lower-case normalised path and the LOD); 0x08 u32 lod;
 0x0C u32 vertexCount; 0x10 u32 indexCount; 0x14 u32 submeshCount; 0x18 u32 flags (skinned, hasTangents); 0x1C f32
 bounds[6]; 0x34 f32 patternScale; 0x38 u32 chunkCount; 0x3C u32 totalBytes.
- 2 `ModelChunk`: 0x00 u64 modelKey; 0x08 u32 chunkIndex; 0x0C u32 bytes; 0x10 payload bytes.
- 3 `ModelEnd` (16 B): 0x00 u64 modelKey; 0x08 u64 payloadHash (FNV-1a 64 over the concatenated chunk payloads, the
 convention of the S0 stand-ins; chosen, D39).
- 4 `MaterialDef`: 0x00 u64 materialKey; 0x08 u32 drawTechnique; 0x0C u32 flags (glass CW/CCW, alpha-masked); 0x10
 four strings in order colour-metal, normal-gloss, add-maps, alpha-mask (absolute paths).
- 5 `SkinnedBind`: 0x00 u64 modelKey; 0x08 u32 boneCount; 0x0C u32 pad; 0x10 boneCount × 48 B bind matrices (SE
 model space).
- Model payload (concatenated chunks): vertices of 56 B (f32 pos[3], normal[3], tangent[3], binormal[3], uv[2]);
 skinned models 76 B (adds u8 boneIndex[4], f32 weight[4]); then u32 indices in `MyModel.Triangles` order; then
 submeshes of 24 B (u64 materialKey, u32 triStart, u32 triCount, u32 drawTechnique, u32 pad).

**C3/C4 conventions for S3 (wording only; layout 1.0 and its hash unchanged).** Code: `ModelCodec`, `MeshConventions`
in `DeepEngineers.Wire`; independent twin `wire_ref.py` (byte-identical payloads, `S3VectorTests`).
- `modelKey` = FNV-1a 64 of the UTF-8 text `<path>|lod<n>`, the path trimmed, `/` → `\`, lower-case (invariant), e.g.
 `models\cubes\large\armorcube.mwm|lod0`, plus `|scale=<s>` (.NET Framework `float.ToString("R")`) when the model is
 drawn rescaled, since PartAdd has no scale field and the vertices then carry it; `materialKey` = FNV-1a 64 of
 `<cm>|<ng>|<add>|<alphamask>|<drawTechnique>|<flags>` (paths normalised the same way, numbers in decimal): a content
 key, one MaterialDef per distinct material. Both are opaque to Subnautica.
- `bounds[6]` = (min x, y, z, max x, y, z), SE model space; every `drawTechnique` is the numeric
 `VRageRender.Import.MyMeshDrawTechnique` (SE's byte enum); `triStart`/`triCount` count triangles (`MyMesh`);
 tangent and binormal are zero when `hasTangents` is clear.
- Chunks carry the payload in order: indices 0 … chunkCount − 1, each 1 B … 4 MiB of data (`bytes` = payloadBytes − 16),
 Σ bytes = totalBytes = vertexCount·stride + indexCount·4 + submeshCount·24. A producer writes one model's Begin, chunks
 and End with no other model's messages in between (resuming after Full); MaterialDef and SkinnedBind may come
 anywhere. The consumer copies chunks on its main thread and checks `payloadHash` on a worker; a new ModelBegin
 abandons an open model (counted); after a re-sync, chunks and ends before the next ModelBegin are dropped (counted).
- MaterialDef strings follow the header without padding (u16 length + UTF-8 each, empty = none); the payload length is
 exact. PartAdd `local` = the part's grid-local `MyCubeInstanceData.LocalMatrix` rows (rigid), `colorMaskHSV` =
 `ColorMaskHSV.xyz`, `uvOffset` as in §2.5.
- C7 counters: `mirroredEntityCount` = grids known (GridAdd accepted, no EntityRemove or ClearAll since); `pendingParts` = parts whose model has not ended or whose submesh materials (read from the payload's submesh table at ModelEnd, before any parse) are not all defined, plus orphan parts (received before their GridAdd), plus 1 while the consumer knows records were lost to a corrupt-framing re-sync (§5.3) and no `ClearAll` has followed; the part records of a grid whose GridAdd the epoch filter dropped are dropped with it, never kept as orphans; a model refused by the worker (hash, counts) is not pending; `c3BytesDrained` = Σ (payloadBytes + 8) of the C3 messages fed to the assembler (records skipped by a re-sync are not counted).
- Consumer audit: a corrupt-framing re-sync on the Subnautica side loses records for good unless SE resends, and none of the restart triggers of §6 fires for it. Once a stream is complete, with SE's C3/C4 queues empty, both rings drained (consumer tails at the producer heads) and a C7 at least 2 Subnautica frames newer than the drained rings, SE compares: `c3BytesDrained` grows as SE's written C3 bytes (the same unit) from the first such moment of the stream; `mirroredEntityCount` equals the grids SE sent; `pendingParts` is at most the parts whose model SE could not load. A mismatch lasting 2 s restarts the stream (`ClearAll(worldEpoch, linkEpoch)` and everything), at most once per 10 s.
- Test traffic: the S0 stand-ins' synthetic models have `vertexCount` 0 and hash their chunk bodies without the 16-byte
 stamp each chunk starts with; consumers accept a `vertexCount` 0 model unhashed and never build it. Every real model
 (`vertexCount` > 0) follows the rules above (the S3a fake producer, the SE plugin, Wire's `ModelEncoder`).

**C4 messages:**
- 1 `GridAdd` (32 B; S7a: flags bit 3 `Ghost` marks the placement ghost, "S7a conventions"; S7p: bit 4 `Projection`, bit 5 `Missile`, §5.4 "S7p scene-above"; S7t: bit 6 `Impostor` = 64, a coarse grid above the camera, §5.4 "S7t impostors" (SE) and "S7t altitude" (Subnautica)): 0x00 u64 entityId; 0x08 u32 targetClass (1 large grid, 2 small grid); 0x0C u32 flags; 0x10 u64
 parentEntityId (root grid of the physical group for a subgrid, 0 for a root grid); 0x18 u32 worldEpoch; 0x1C u32 pad.
- 2 `EntityRemove` (8 B): 0x00 u64 entityId.
- 3 `PartAdd` (100 B payload, 108 B framed, 112 B padded): 0x00 u64 entityId; 0x08 u64 partKey; 0x10 u64 modelKey;
 0x18 f32 local[12]; 0x48 f32 colorMaskHSV[3]; 0x54 f32 uvOffset[2]; 0x5C u32 skinId; 0x60 u32 flags.
- 4 `PartRemove` (16 B): 0x00 u64 entityId; 0x08 u64 partKey.
- 5 `PartRecolor` (32 B): 0x00 u64 entityId; 0x08 u64 partKey; 0x10 f32 hsv[3]; 0x1C u32 pad.
- 6 `SkinSet`: 0x00 u64 entityId (a grid or a subgrid; boxes in its own SE-local frame); 0x08 u32 skinVersion; 0x0C
 u32 sectionCount; then per section a 24 B header {0x00 u32 sectionId; 0x04 f32 centre[3]; 0x10 u32 boxCount; 0x14
 u32 kind (1 grid, 2 character)} followed by boxCount × 64 B boxes {0x00 f32 centre[3]; 0x0C f32 half[3]; 0x18 f32
 axes[9] (rows, always present; identity for grid-aligned boxes); 0x3C u32 flags}. Meaning (S6): "C4 type 6
 `SkinSet` (S6)" below.
- 7 `RoomSet`: 0x00 u64 entityId; 0x08 u32 count; 0x0C u32 pad; count × 24 B {i32 min[3], i32 max[3]} airtight room
 AABBs in grid cells. Sent only when the set changes. S7: `pad` = `flags` (version nibble, `Truncated`), the boxes cover
 exactly the airtight rooms' air cells (§5.4 "S7 conventions").
- 8 `CharacterAdd` (24 B): 0x00 u64 entityId; 0x08 u64 modelKey; 0x10 u32 flags; 0x14 u32 boneCount.
- 9 `ClearAll` (8 B): 0x00 u32 worldEpoch; 0x04 u32 linkEpoch.

**C4 type 6 `SkinSet`.** One message carries an entity's
**complete** collision skin and replaces any earlier one (no per-section delta on the wire).
- Header: `entityId` = a grid or subgrid SE mirrors (never a floating object or a character in v1: the character
 section comes from C1, §2.6); `skinVersion` = 1, 2, 3… per entity per stream (a `ClearAll` or the entity's
 `EntityRemove` restarts it at 1; Subnautica ignores a version ≤ the one it applied, counted `skinStale`);
 `sectionCount` ≥ 0 (0 = the entity currently has no collision: every section is removed).
- Section: `sectionId` = the lattice id `((ix+512)<<20)|((iy+512)<<10)|(iz+512)` with `i = floorDiv(round(c /
 GridSize), SectionCells)` per axis (round half away from zero, `c` = a box centre, `SectionCells` 6 large / 32
 small, `i` ∈ [−512, 511]: ±7.7 km large, ±8.2 km small), unique and ascending in the message; `centre` = `c_sec` =
 `(i·SectionCells + (SectionCells − 1)/2)·GridSize` per axis, SE-local metres; `boxCount` ≥ 1; `kind` 1 (grid). A
 section of kind 2 is never sent in v1; a consumer ignores it and counts it.
- Box: `centre` SE-local metres; `half` > 0 and ≤ 4.0 m (+1e-4 rounding) along the box axes; `axes` = rows `e0, e1,
 e2`, orthonormal (|eᵢ·eⱼ − δᵢⱼ| ≤ 1e-5) and right-handed, **exactly** the identity rows for a grid-aligned box;
 `flags` bit 0 `Rotated` (axes not the identity: Subnautica puts the box on an orthonormalised child, §2.6), bits
 8–11 the source (0 `Segment`: merged box cells, 1 `ModelShapeObb`: a convex piece of a block's model collision as
 an OBB, 15 `Synthetic`: the stand-in producer), every other bit 0. Order in a section: `Segment` boxes by their
 minimum cell (z, then y, then x), then `ModelShapeObb` boxes by block position (z, y, x) and shape index.
- Size: payload = 16 + Σ (24 + 64·boxCount) bytes, at most 2 MiB (`SkinMaxBytes`, ≈ 32,000 boxes); a larger skin is
 not sent (`DE.S6 skin TOO LARGE <entity>`, no proxy for that grid). A message whose length differs from the one its
 counts imply, an out-of-range half extent or non-orthonormal axes is dropped whole and counted (`skinBad`); so are
 `skinVersion` 0, a section kind other than 1 or 2, a box with flag bits outside `Rotated` and the source nibble,
 and a box without `Rotated` whose rows are not exactly the identity.
- **Decomposition (SE producer, v1; members VERIFIED).** For each block of `MyCubeGrid.GetBlocks` whose
 `BlockDefinition.HasPhysics` is true: (a) `BlockTopology == MyBlockTopology.Cube`: every cell `Min…Max` enters the
 segmentation input, whatever its `PhysicsOption` (Box as SE; Convex — slopes, corners, deformed armor — as full
 cells: an over-approximation of at most one cell in v1); (b) another topology with `PhysicsOption != None`: when
 `FatBlock?.ModelCollision?.HavokCollisionShapes` (`MyEntity.ModelCollision`, `MyModel.HavokCollisionShapes`) is
 non-empty, one OBB per convex piece (the children of a single `HkListShape` — `TotalChildrenCount`,
 `GetChildByIndex(int)` —, the shapes of a single `HkMoppBvTreeShape` — `ShapeCollection.ShapeCount`,
 `GetShape(uint, null)` —, else each array element), from `HkShape.GetLocalAABB(0f, out Vector4 min, out Vector4
 max)` in model space, placed exactly as `MyCubeBlockCollector.AddShapesCustom` places it: scale
 `ModelCollision.ScaleFactor`, rotation `block.Orientation.GetMatrix(out Matrix)`, translation
 `FatBlock.PositionComp.LocalMatrixRef.Translation` (the OBB's rows are the orientation's Right, Up, Backward; a
 signed axis permutation — every 90° block orientation — gives a grid-aligned box on the identity rows with its half
 extents permuted, `Rotated` clear; a half extent is at least 5 mm: Wire `CollisionSkinMath.ObbFromModelAabb`);
 otherwise its cells enter the segmentation input (as
 `AddShapesBox`); (c) `PhysicsOption None`: nothing. Segmentation: `new MyVoxelSegmentation`, `ClearInput`,
 `AddInput(Vector3I)` per cell, `FindSegments(MyVoxelSegmentationType.Simple, 0)` (what `MyGridShape` runs); each
 segment is split at the section lattice and into at most `MaxBoxCells` (3 large, 16 small) cells per axis, as
 equal as possible; a cell range `[min, max]` becomes centre `(min + max)/2·GridSize`, half `(max − min + 1)·GridSize/2`.
 An OBB with a half extent over 4 m is split into equal pieces along that axis. If `MyVoxelSegmentation` is missing,
 Wire's `CollisionSkinMath.MergeCellsGreedy` replaces it (logged once). Cost: block collection resumes across ticks
 within 0.5 ms per tick, one grid at a time, nearest first.
- **Resend rule.** SE sends a grid's first `SkinSet` once its `GridAdd` is written (never before it), then a new
 complete `SkinSet` (version + 1) whenever a rebuild changes any section (sections compared by id and a content hash
 of their bytes). A rebuild follows `MyCubeGrid.OnBlockAdded` / `OnBlockRemoved` (a destroyed block is removed) at
 most once per grid per 30 ticks after the last event, and the S5 sweep's safety net; integrity-only changes
 (`OnBlockIntegrityChanged`: damage, welding, grinding without removal) do not change a v1 skin and send nothing. A
 stream restart (`ClearAll` and everything) resends every skin from version 1. Lane: NearEdit within 50 m, else Edit
 (`MirrorBackPressure.Classify`). Subnautica diffs a new skin against the applied one by section id and content
 hash, rebuilding only changed sections (within the 256 shapes per physics step of §2.6), and drops a skin with its
 entity (`EntityRemove`, `ClearAll`).

**C1–C4 conventions for S5.** Why no layout 1.1:
§5.2 "Layout decision for S5". Code: Wire `MirrorConventions` (the constants below, `CanonicalText`, `Hash`, the key
and flag helpers) and the skeleton table `src/DeepEngineers.Wire/Data/SeSkeleton.canon1.tsv` (generated into Wire as
C# arrays, D15 style: `MirrorSkeleton.Canon1.cs` from `python -I tools/worldmap/wire_ref.py s5skeleton-cs <out>`; the
data file stays the source, `MirrorConventionsTests` compares all 7 columns); the S5 math in Wire `InstanceMath` (SE row
algebra, the subgrid row, §2.8 interpolation, the mirror conjugation `ToUnity`/`ToUnityAffine`), `SkinMath` (bone map,
weight remap, SkinnedBind, C1 bone locals, split by material), `ChunkMath` (cells, chunk keys, colour key, draw kind),
`MirrorBudgets`/`MirrorBackPressure` and `StreamRecording` (`.derec`); independent twin in Python (`wire_ref.py` S5
functions: the same text, keys and payloads, read by the C# vector test `tests/DeepEngineers.Wire.Tests/S5VectorTests.cs`
as `S3VectorTests` reads the S3 vectors; `wire_ref.py s5selftest`). Every SE member named here was checked in
the decompiled build of the installed game (VERIFIED unless marked; a missing member logs once and turns off only its
feature); Unity members in the installed game's `UnityEngine.AnimationModule.dll` / `UnityEngine.CoreModule.dll`.

*Canonical text* (exact; ASCII, `\n` after every line, the last included; `const` lines sorted by group, then value,
then name, as §6 sorts the layout's), FNV-1a 64 **`0x31dfd6f9befdef5d`** (37 lines, 1,539 bytes):
```
ext s5 mirror 1
const MirrorCharacterFlags Player 1
const MirrorCharacterFlags Fallback 2
const MirrorCharacterFlags Version1 268435456
const MirrorCharacterPart Body 1
const MirrorCharacterPart Head1st 2
const MirrorCharacterPart Tool 3
const MirrorGridFlags Static 1
const MirrorGridFlags Subgrid 2
const MirrorGridFlags FloatingObject 4
const MirrorGridFlags Version1 268435456
const MirrorLimits MaxSkinWeights 4
const MirrorLimits CanonicalBones 78
const MirrorPartClass CubePart 1
const MirrorPartClass Edge 2
const MirrorPartClass FatBlock 3
const MirrorPartClass Subpart 4
const MirrorPartClass CharacterBody 5
const MirrorPartClass Tool 6
const MirrorPartClass FloatingObject 7
const MirrorPartFlags ClassMask 15
const MirrorPartFlags Dynamic 16
const MirrorPartFlags HiddenInFirstPerson 32
const MirrorPartFlags BuildStage 64
const MirrorPartFlags Deformed 128
const MirrorPartFlags NoShadows 256
const MirrorPartFlags MetalnessColorable 512
const MirrorPartFlags Version1 268435456
const MirrorPartFlags VersionMask 4026531840
format modelKey <path>|lod<n>[|scale=<s>][|skin=<skin>][|char=body|char=head1st]
format partKey.cube fnv1a64(u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)
format partKey.edge fnv1a64(u32 2, u64 modelKey, f32 tx, f32 ty, f32 tz, f32 packedRotation)
format partKey.fat fnv1a64(utf8 fat|<minX>,<minY>,<minZ>)
format partKey.subpart fnv1a64(utf8 sub|<fatKey hex16>|<name>[/<name>])
format partKey.floating 1
format skinId fnv1a32(utf8 lower(<skin>)) or 0
skeleton canon1 78 0x995765282e7d4f1d
```
Pinned by `MirrorConventionsTests` (text byte for byte, hash, the skeleton hash recomputed from the data file, every
flag inside its field) and the Python twin. Changing a line changes these conventions: bump `ext s5 mirror <n>` and
Not compared between peers at run time (both plugins ship the
same `DeepEngineers.Wire.dll`; §6 "Extensions").

*Version nibble.* Bits 28–31 of `PartAdd.flags`, `GridAdd.flags` and `CharacterAdd.flags` = 1 (`Version1`) from an S5
producer. 0 = an S3 producer: a PartAdd is a rigid, chunked cube part with shadows; a GridAdd is a grid; a
CharacterAdd is ignored (as today). Any other value: the record is ignored and counted (`conventionsRejected`), not an
error. Numbers: FNV-1a 64 as §6; FNV-1a 32 offset `0x811c9dc5`, prime `0x01000193`; text keys are UTF-8, numbers in
invariant decimal, `x16` = 16 lower-case hex digits.

*Entity set (SE).* Streaming of C3/C4 starts after the hello ack (as S3) and adds entities only while C5 `zone` is
CORE, TILE VOID or OUTER VOID. Each tick the plugin queries the top-most entities in a sphere around the SE camera with
`MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref BoundingSphereD, List<MyEntity>, MyEntityQueryType.Both)`
into one reused list (VERIFIED: the call appends, `clear: false`, so the list is cleared first). Kept:
`MyCubeGrid` (not `IsPreview`, not `MarkedForClose`, `GridScale` 1; else skipped and logged once), `MyFloatingObject`,
and the player's `MyCharacter` (C1's); voxel maps (planet, S4 tiles) and everything else are ignored. Enter radius 250 m
(water visibility fallback 200 m + 50 m: SE does not know Subnautica's biome visibility; Subnautica culls at it), leave
radius 300 m (S7a, `Site` picture policy with the camera above the sea: 1,000 m and 1,050 m, "C5/C7 semantics for
S7a"); an entity outside the leave radius for 60 s, or closed by SE, gets `EntityRemove`; until then it keeps its C2 row.
Grids enter and leave with their whole physical hierarchy (root `MyGridPhysicalHierarchy.Static.GetRoot(MyCubeGrid)`,
VERIFIED `MyGridPhysicalHierarchy`): the hierarchy is in the set while any of its grids is inside the radius. At most
1,024 entities (C2), nearest first; the rest wait. S7t: above the camera, beyond the radius, large root grids
up to 5 km up ride as coarse impostors (GridAdd bit 6; "S7t impostors"); a root the set lets go inside that reach is
demoted to one instead of removed, and one the set takes is upgraded.

*GridAdd (S5).* `targetClass` 1/2 from `GridSizeEnum`; `flags` = Version1 | Static (`IsStatic`) | Subgrid (non-root) |
FloatingObject (a floating object's registration: `targetClass` 2, ignored); `parentEntityId` = the hierarchy root's id
for a subgrid (flattened: a rotor on a piston on a hinge is a child of the root, never of its mechanical parent), 0
otherwise. A GridAdd for a known id is a re-parent or flag change: the consumer keeps the entity's parts and meshes
(VERIFIED today's consumer keeps them) and moves its transform. Sent again when the hierarchy changes
(`MyCubeGrid.OnConnectionChangeCompleted(MyCubeGrid, GridLinkTypeEnum)` with `Physical`, split, merge, root change).

*PartAdd (S5).
× parent × …) and rigid. `colorMaskHSV`, `uvOffset` as S3 (§2.5); `skinId` = FNV-1a 32 of the lower-case skin subtype
(`MyStringHash.String`), 0 without a skin (information only: the skin is already resolved into the model's
materials, below).

| Class (`flags` & 15) | Source (SE, update thread) | `partKey` | `local` | Drawn as |
|---|---|---|---|---|
| 1 CubePart (0 = S3 legacy, same) | `grid.RenderData.Cells[*].CubeParts` keys (`MyCubePart`: `Model`, `InstanceData`, `SkinSubtypeId`) | `partKey.cube` (S3's `PartDiff.PartKey`) | `InstanceData.LocalMatrix` | chunk |
| 2 Edge | the cell's private edge set `m_edgesToRender` (`ConcurrentDictionary<long, MyEdgeRenderData>`: `ModelId`, model path `MyModel.GetById(int)`, `EdgeInfo.LocalOrthoMatrix`, colour and skin from `EdgeInfo.GetNormalInfo(FirstAvailable, out Color, out MyStringHash, …)` with HSV = `Color.ColorToHSVDX11` as SE does; VERIFIED `MyCubeGridRenderCell.AddEdgeParts`, `MyFourEdgeInfo`; read by reflection, a missing member turns edges off with one log line); `NoShadows` set (SE draws edges without shadows, instance flags 0) | `partKey.edge` | unpacked `LocalOrthoMatrix` | chunk (Subnautica may cull edge-only chunks beyond 130 m large / 35 m small, SE's `EdgeViewDistance`) |
| 3 FatBlock | every `MySlimBlock.FatBlock` with a model: `FatBlock.Model` at `FatBlock.PositionComp.LocalMatrixRef` (grid-local), `MySlimBlock.ColorMaskHSV`, the block's skin; armor under construction also has one (`MySlimBlock.UpdateVisual`: `ShowParts` false creates a plain `MyCubeBlock`, VERIFIED) | `partKey.fat` from `MySlimBlock.Min` | the fat block matrix | chunk |
| 4 Subpart | `MyEntity.Subparts` (`Dictionary<string, MyEntitySubpart>`, names from the `subpart_<name>` dummies; nested through each subpart's own `Subparts`), model `Subpart.Model`, matrix `PositionComp.LocalMatrixRef` (relative to its parent) | `partKey.subpart` (path of dictionary keys from the block) | composed grid-local matrix at send time | chunk while static; with `Dynamic`: its own child transform |
| 5 CharacterBody | the player's character (below) | `MirrorCharacterPart` 1 Body, 2 Head1st | identity | skinned renderer |
| 6 Tool | `MyCharacter.CurrentWeapon` entity (`IMyHandheldGunObject<MyDeviceBase>`, its `Model`) — optional in S5 | 3 | identity; posed by C2 | own child, `Dynamic` |
| 7 FloatingObject | `MyFloatingObject` (its `Model`; uniform scale in C2 `k`) | 1 | identity | own child of its Entity |

Flag bits: `Dynamic` (16) = drawn as its own child transform posed from C2's subpart table, never merged in a chunk;
`HiddenInFirstPerson` (32); `BuildStage` (64) = the model is a `MyCubeBlockDefinition.BuildProgressModels[i].File`
stage (`MySlimBlock.CalculateCurrentModelID` ≥ 0; information and N2 logs); `Deformed` (128) = the instance carries
SE's armor deformation (`MyCubeInstanceData.EnableSkinning` with a non-neutral bone; `MyGridSkeleton`), not drawn in v1
(ARCHITECTURE §5.8), counted; `NoShadows` (256); `MetalnessColorable` (512) = SE's render flag for this part, used by the
paint fix pass (S3.C13) and since S7t T6 by the block paint (`ColorMaskPaint`, below), ignored before it. A PartAdd for a known
(entity, partKey) replaces the part (today's consumer does).

*Dynamic subparts.* A subpart is sent static (chunked) unless it is known to move: SE compares each mirrored subpart's
composed grid-local matrix every tick (or round-robin within the C2 budget); the first change above 1e-4 m or 1e-4 rad
re-sends its PartAdd with `Dynamic` (same key: a replace), and from then on its row is in C2 every tick until the grid
is re-sent. SE may pre-mark subparts of block types that animate (doors, turrets, pistons, drills, grinders, welders,
wind turbines) to spare the first rebuild. A dynamic subpart's C2 row: `ownerId` = the grid (or subgrid) that owns the
block, `partKey` = its key, `local` = its composed matrix in that grid's SE-local frame.

*C2 subpart table (S5 rows).* Three kinds share it, told apart by the consumer's records, not by a flag: a subgrid row
(`partKey` = an entity id known by GridAdd with `parentEntityId = ownerId`; `local` = the subgrid's world matrix times
the inverse of the root's, composed in doubles in SE space each tick); a dynamic subpart row; the held tool row
(`ownerId` = the character id, `partKey` 3, `local` in the character's model space). Order and overflow: subgrids (by
root distance), then dynamic subparts (nearest first), then the tool; rows past 2,048 are dropped and counted
(`c2Dropped`), and the consumer holds a dropped child's last pose.

*Model keys and skins.* `modelKey` text (`format modelKey`): `<path>` = the `MyModel.AssetName` the exporter loaded,
normalised as S3; `|scale=` as S3; `|skin=<skin>` (lower-case subtype) only when the skin overrides at least one
material of that model; `|char=body` / `|char=head1st` for the two character models. Skins are resolved SE-side:
`MyDefinitionManager.Static.GetAssetModifierDefinitionForRender(MyStringHash)` → `MyAssetModifiers.SkinTextureChanges`
(`Dictionary<MyStringId, MyTextureChange>` keyed by material name; `ColorMetalFileName`, `NormalGlossFileName`,
`ExtensionsFileName`, `AlphamaskFileName`), applied per submesh whose `MyMeshMaterial.Name` matches, then the
MaterialDef carries the resulting absolute paths (a new content `materialKey`). A skin variant is a separate model
(same geometry, its own submesh material keys); geometry is resent once per (model, skin) in use.

*Live edits (deltas).* SE marks (grid, render cell) dirty from a postfix on
`MyCubeGridRenderCell.RebuildInstanceParts(RenderFlags)` (`__instance.m_gridRenderComponent.CubeGrid`; it runs on the
update thread: `MyCubeGrid.UpdateInstanceData` → `RebuildDirtyCells` is the completion callback of SE's parallel
`UpdateDirtyInternal`, and VRage runs those callbacks from `MySession`'s update through `Parallel.RunCallbacks`,
VERIFIED) and from the grid events; a dirty cell is diffed against its sent set after two identical reads (S3's
debounce), at most once per tick. Fat blocks and subparts are diffed per touched block.

| SE event (VERIFIED member) | Records |
|---|---|
| block added / removed / destroyed: `MyCubeGrid.OnBlockAdded`, `OnBlockRemoved` (`Action<MySlimBlock>`), the cell rebuild | PartAdd / PartRemove of the block's cube parts, edges of the touched cells, fat block and every subpart |
| build or damage stage: `OnBlockIntegrityChanged(MySlimBlock)`; the model follows `BuildLevelRatio` against `BuildProgressModels[i].BuildRatioUpperBound` (`CalculateCurrentModel(out Matrix)`) | PartRemove of the old parts, PartAdd of the stage model (`BuildStage`); a fat block keeps its key (replace) |
| paint: `MyCubeGrid.ChangeColorAndSkin(MySlimBlock, Vector3?, MyStringHash?)` (it calls `UpdateVisual`, so the cell rebuild follows) | PartRecolor of every part of the block |
| character colour mask only (same model, same skin) | PartRecolor of its Body and Head1st parts |
| skin: the same method | PartRemove + PartAdd with the skin variant `modelKey` |
| armor deformation: `MyGridSkeleton`, instance bones | nothing drawn in v1; `Deformed` on the next PartAdd |
| split / merge: `OnGridSplit`, `OnGridMerge` (`Action<MyCubeGrid, MyCubeGrid>`) | GridAdd of the new grid, PartRemove/PartAdd moves, EntityRemove of a merged-away grid |
| hierarchy: `OnConnectionChangeCompleted` (`Physical`), `GetRoot` | GridAdd again (re-parent) |
| subpart starts moving | PartAdd again with `Dynamic`, then C2 rows |
| character model, skin or colour (polled each tick: `ModelName`, `ColorMask`, the asset modifier list) or a new character id | EntityRemove (old id) + CharacterAdd + its PartAdds |

*Chunk key and consumer rules (Subnautica).* One Unity `Mesh` per chunk key (entity, cell, `materialKey`, colour key,
`flags & (NoShadows | MetalnessColorable)`): cell = `floor(T / (8 · g))` per axis of the part's `local` translation, g
= 2.5 m (`targetClass` 1) or 0.5 m (2) (today's `CellKey.Of`, 20 m / 4 m cells); colour key = HSV quantised to 1e-4
(`ColourRules.Key`). 16-bit indices up to 65,535 vertices, else `Mesh.indexFormat = IndexFormat.UInt32`. Every chunked
part (classes 1–4 without `Dynamic`) contributes each model triangle once (S3's coverage rule). `Dynamic` parts, tools
and floating objects are their own `MeshRenderer` children, their `Mesh` shared per (modelKey, materialKey, colour key).
(SN build pass, accepted by the integrator: the colour is a material-slot property, never in the vertices, while
the pattern `uvOffset` is in them; the shared child mesh is therefore keyed by (modelKey, `uvOffset`), one `Mesh` per
`materialKey`, and the colour key selects the material slot.)
Rebuilds: PartAdd/PartRemove/PartRecolor dirty the old and new cells; dirty cells are built only from a state where the
frame's C4 drain reached the head read at its start (no half-applied edit is drawn) or after 10 frames regardless; at
most one build in flight per chunk key (a cell dirtied again is rebuilt once more after); the old mesh stays drawn until
its replacement is uploaded; meshes are reused and destroyed explicitly on EntityRemove, ClearAll, world change and quit.
Parts of a skinned model wait for its SkinnedBind (pending). Subgrid children are hidden until their first C2 row.

*Character.* The player's character (C1 `characterId`) is mirrored from C4 records and C1 every tick; other characters
are not mirrored in v1 (§8).
- Records: `CharacterAdd` {entityId = character id, `modelKey` = the body model's key, `flags` = Version1 | Player |
 Fallback (when the default model replaced the player's), `boneCount` = 78}; then PartAdd Body (class 5, key 1) and
 PartAdd Head1st (class 5, key 2, `HiddenInFirstPerson`), both `local` identity, `colorMaskHSV` = `MyCharacter.ColorMask`
 (`Render.ColorMaskHsv`), `MetalnessColorable` set (VERIFIED `MyCharacter`: `Render.MetalnessColorable = true`).
 Optional: PartAdd Tool (class 6, key 3, `Dynamic`) with its C2 row.
- Models: the character's `MyModel.AssetName` (`Models\Characters\Astronaut\SE_astronaut.mwm` or the female one; the
 stub names its LOD0 geometry through `GeometryDataAsset`, and `LoadData(true)` takes vertices and blend data from that
 asset, bones from the stub, VERIFIED `MyModel.LoadData`/`LoadGeometryData`) loaded as every model (`LoadUV`,
 `LoadData(true)`; `LoadData` leaves `Bones` null when the model's animation tag has no clips, so then
 `LoadAnimationData(true)`, as `MySkinnedEntity.ObtainBones` does), split by material: submeshes whose `MyMeshMaterial.Name` is in
 `MyCharacterDefinition.MaterialsDisabledIn1st` (VERIFIED `Characters.sbc`: Head, Light, Headlight, Astronaut_glass,
 Astronaut_head, Spacesuit_hood, plus the female head materials) form `|char=head1st`, the rest `|char=body`; each model
 holds only the vertices its submeshes use (`SkinMath.SplitByMaterial`: a triangle belongs to the first submesh whose
 range covers it, S3's coverage rule; a part's triangles are grouped by submesh in submesh order, vertices renumbered in
 first-use order; triangles outside every range stay with the body after its submeshes; a submesh left without
 triangles is dropped; a part without triangles is not sent). Both are skinned (`ModelBegin.flags` skinned, 76 B vertices) with
 SkinnedBind. Skins: the character's `MyAssetModifierComponent.AssetModifiers` (`List<MyDefinitionId>`, in list order)
 resolved as for blocks.
- Canonical skeleton `canon1` (`SeSkeleton.canon1.tsv`, 78 bones: the bones the male and female astronauts share by
 name with the same parent; VERIFIED from both `.mwm` files: 81 bones each, the female lists bones 14–23 in another
 order and has other mesh-node bones; blend indices ≤ 76 in both LOD0 files). Every bone index on the wire is a
 canonical index: vertex `boneIndex`, the SkinnedBind order, the C1 bone order. The SE plugin maps its model's
 `MyModelBone[]` (`Name`, `Parent`, `Transform`; VERIFIED `MyModelImporter.ReadBones`) to canonical rows by name once per
 model. A vertex weight on a non-canonical bone moves to its nearest canonical ancestor (row 0 when it has none);
 duplicates are summed, the 4 largest kept, renormalised to 1 (`MaxSkinWeights` 4; weights from
 `MyModel.GetBoneIndicesWeights(int triangleIndex)`, VERIFIED public). Exactly (`SkinMath.RemapInfluences`, so both twins
 agree bit for bit): weights ≤ 0 or NaN are dropped; equal targets are summed in slot order in doubles; ties keep the
 lower canonical index; slots are sorted by weight, descending, each the float32 of w / Σ (Σ over the kept slots, in
 that order); unused slots are index 0, weight 0; a vertex with no positive weight gets (0, 1). SkinnedBind matrix i = the inverse of canonical bone i's absolute bind matrix (its `Transform`
 composed down the model's parent chain, row order abs = local × parentAbs), which equals SE's own `SkinTransform`
 (VERIFIED `MyCharacterBone` constructor: `SkinTransform = Matrix.Invert(AbsoluteTransform)`).
- C1: `boneCount` = 78, bones = `MyCharacter.BoneAbsoluteTransforms` (`Matrix[]` by model bone index, VERIFIED
 `MySkinnedEntity`) picked in canonical order, SE model space rows; `bonesVersion` = the low 32 bits of the current
 CharacterAdd `modelKey` (0 = no bones). Subnautica uses C1 bones only when both match its current character. S7: with
 `ExtraBones` (CharacterAdd `flags` bit 2) the C1 and SkinnedBind rows continue past row 77 with the model's other bones
 (`boneCount` = `Model.Bones.Length`, 81; §5.4 "S7 conventions").
if
 the player's model cannot be exported (load, a missing canonical bone or parent, no blend data), SE exports the
 default Engineer
 (`MyDefinitionManager.Static.Characters.TryGetValue("Default_Astronaut", out MyCharacterDefinition)`, its `Model`) with the player's colour, no skin, `Fallback` set,
 `DE.S5 char FALLBACK <reason>` once; if that fails too, no character mirror (the proxy capsule stays). Never a
 Subnautica diver.
- Subnautica: the Frame/Entity of the character from C1 `charO/charQ/charS` (camera bracket, §2.8); under the Entity
 the canonical bone tree (78 `Transform`s named as `seBone`, rest locals from SkinnedBind: absolute bind =
 inverse(SkinnedBind), relative = abs × parentAbs⁻¹ in SE space and VRage row order, then conjugated by the mirror as
 §2.5); one `SkinnedMeshRenderer`
 per character part (`sharedMesh` with `boneWeights` and `bindposes` = the mirrored SkinnedBind, `bones` = the tree,
 `rootBone` = row 0, `updateWhenOffscreen` true); materials as for grids. Animation source per frame:
 - **SeBones** (default; walking, standing on the seabed, seated, flying, dead): bone i local = its C1 matrix relative
 to its parent's (`abs_i × abs_parent⁻¹`, SE row order), conjugated by the mirror, from the C1 bones of the camera
 bracket's two ticks, interpolated as §2.8. This is SE's own pose (S5.N7a).
 - **Retarget** (swimming): when the C1 swim extension is version 1 with `inWater`, mode Swim or Surface, not grounded,
 and the character is not seated or dead. Source = the ghost Player's own animator (`Player.main.playerAnimator`;
 HYPOTHESIS: the same Animator as the ArmsController's that S5a drives, logged), posed by Subnautica's own swim states
 from the parameters S5a already sets every frame while the extension is valid (VERIFIED `S5aHost.SetDriving`:
 driving does not depend on the arms being shown, `cullingMode = AlwaysAnimate`); read after the animator evaluated. Path H (humanoid; preferred when `playerAnimator.isHuman`): the SE skeleton
 gets an Avatar from `AvatarBuilder.BuildHumanAvatar(GameObject, HumanDescription)` (`human` from the `human` column:
 `HumanBone.boneName` = `seBone`, `humanName` = `HumanTrait.BoneName[(int)HumanBodyBones.<human>]`, the same order
 being HYPOTHESIS caught by the avatar's `isValid`/`isHuman` check; `skeleton` = every
 canonical bone at its rest local transform except the upper arms, raised to horizontal: SE's bind is an A-pose,
 upper arm (∓0.203, 1.433) to palm (∓0.572, 1.059) m, about 45° down, VERIFIED from the bone matrices; the T-pose
 description is the rest-pose correction, HYPOTHESIS); two `HumanPoseHandler(Avatar, Transform)` made once (source:
 `playerAnimator.avatar` on `playerAnimator.transform`; target: the SE avatar on the SE bone root) and disposed on
 release; each frame `GetHumanPose(ref pose)` on the source, then `SetHumanPose(ref pose)` on the target with
 `bodyPosition` reset to the SE rest hips (no root motion; one reused `HumanPose`, no per-frame allocation). Path G (generic; the source is not humanoid or path H
 failed): per row with `snBone` ≠ `?`, `R_se(t) = (R_sn(t) · R_sn,rest⁻¹) · R_se,rest` in the character's frame, local =
 parent⁻¹ · world; unmapped rows keep their rest local. Retarget is available only when its path maps all 15 required
 human bones (Hips, upper/lower legs, feet, Spine, Head, upper/lower arms, hands); else SeBones. Humanoid or generic,
 clip and state names, and the rig's bone names are HYPOTHESIS: the first
 use logs `DE.S5 rig animator=… isHuman=… avatar=… avatarHuman=… bones=<n> names=[…] clips=[…]` and fills `snBone`.
 - **Bind** (the static astronaut): no valid C1 bones and no retarget.
 - S7a: Retarget also out of the water in a foot state (Idle, Walk, Run, Sprint, Jump, Fall) of the C1 locomotion
 extension while the walk source is `retarget` (§5.4 "C1 locomotion extension").
 Source changes blend over 0.25 s per bone (HYPOTHESIS for the look). Third person: the body stays at C1's pose; only
 bone rotations change. First person with Retarget: the Entity is offset so the retargeted `HeadDummy` (SE's
 `HeadBone`) sits at the C1 camera position with the rig's yaw (S5a's view-model rule); with SeBones: no offset.
 `HiddenInFirstPerson` parts are not drawn while C1 `cameraMode` is FirstPerson, drawn otherwise (S5.N7b). S5a's
 diver arms stay the placeholder behind the switch `de.s5 fpbody=se|diver`: with `se` and a drawn SE first-person body, the S5 host
 sets `S5aHost.Gate = ArmsGate.ForceOff` (S5a keeps the animator, O₂, breath and bubbles) and restores the previous
 gate when the SE body is not drawn; the two are never shown together.
- *S7p-character.* (P8) `fpbody` defaults to `se` (env unset; `diver` = S5a's swim-stroke arms, opt-in).
 Under `se` the S5 host keeps `S5aHost.Gate = ForceOff` every tick once SE's astronaut was seen (a CharacterAdd record
 or a built rig; sticky for the process), whatever the SE body's state in the frame (no diver arms, mask or shadow; a
 `de.arms` change is kept as the diver gate and forced off again); with no SE character at all (S5 off on SE: the S5a
 sessions) the diver arms stay the only first-person body; under `diver` the user's gate is back and no SE part is
 drawn in first person. Part rule (`S7pFirstPerson.Show`): third person = drawn; SE first person
 with `se` = body drawn and casting, `HiddenInFirstPerson` parts **shadow-only** (`ShadowCastingMode.ShadowsOnly`),
 VERIFIED SE: `EnableHead(false)` removes `Visible` from the `MaterialsDisabledIn1st` materials, i.e.
 `SkipInMainView | SkipInForward` (`MyProxiesFactory.GetRenderableProxyFlags`), and the shadow views skip only
 `SkipInDepth` (`MyGeometryRendererOld`): SE's own first-person head still casts. Subnautica's Player renderers stay
 hidden by the S1 ghost rule; the `s7p.char` row audits them.
 (`SE_astronaut.mwm`, the entity's own) and skins (four tool skins, none on the suit) exported as SE draws them; the
 difference was the paint: S3's UWE_3COLOR tint (one `_Color` from SE's 144 grey, the metal reduction always on;
 S3.C13 ΔE ≈ 17). Since S7p the character's painted materials get SE's own colour per texel
 (`S7pCharacterPaint`/`S7pPaint.Bake`): the `_cm` (read back through an sRGB target) and `_add` texels, coloring =
 `_add.a`, no metal reduction (VERIFIED: `MyCharacter` sets `Render.MetalnessColorable = true`, shader combos
 `USE_SKINNING|METALNESS_COLORABLE`), `ColorizeGray` (`PixelUtilsMaterials.hlsli`: hsv of the sRGB texel with h, s = 0,
 `saturate(hsv + ColorMaskHSV)`, back to linear, lerp by coloring; the key colour is `ColorMaskHsv` unchanged,
 `MyRender11.ColorFromMask`; **corrected S7t T6:** the shader first adds SE's run-time `ColorizeHSV`
 (0, 0.8, -0.1) to the key, so the bake now runs on `ColorMaskRules.ShaderKey`; P9 had it as 0, which turned every
 baked on a pool thread, uploaded through the mirror's
 budget as sRGB RGBA32 with mips, set as `_MainTex` with UWE_3COLOR off and `_Color`/`_SpecColor` white, re-applied
 after every `ApplyMaps`; one bake per (texture set, colour key, metalness-colourable), ≤ 12 kept;
 `de.s5 {paint: bake|approx}` / env `DEEPENGINEERS_SN_CHAR_PAINT` (default bake). The fallback model follows the
 player's gender (`MyCharacterDefinition.Gender` "Female" → `Default_Astronaut_Female`, else `Default_Astronaut`). The
 held tool carries its own skin: the tool entity's `MyAssetModifierComponent` (SE adds it in `EquipWeapon` and fills it
 asynchronously) resolved like a block skin into the PartAdd's `modelKey`/`skinId` (+ `MetalnessColorable` when the
 skin says so); a skin arriving later re-sends the part; the plain tool after 3 s undecided. SE logs
 `DE.S7p char identity (<why>): character … definition … gender … model … colorMaskHsv (h, s, v) skins […] toolSkin …`
 and an `s5.char` row `op: identity` on every export and every colour change.
- *S7p character rows.* Subnautica `s7p.char` (1 Hz, S5 host): `fpbody`, `armsGate`, `gateForced`,
 `gateReasserted`, `cameraMode`, `firstPerson`, `characterBuilt`, `seCharacterKnown`, `seBodyFirstPerson`, `headShadowOnly`,
 `diverArmsShown`, `snPlayerRenderersOn` (enabled, active renderers under `Player.main`), `snShadowCastersOn`,
 `snBodyLeak` (= fpbody se ∧ SE's astronaut seen ∧ first person ∧ (diver arms ∨ a Player renderer on): the P8 symptom, logged once per onset
 as `DE.S7p char LEAK`), `snLeakSeconds`, `paint` (mode and counters), `paintBaked`, `paintSlots`, `lightKinds`.
 `s5.char` parts carry `show` (drawn | shadowOnly | hidden). SE `s5.char` add/remove rows carry `colorMaskHsv`; the
 `op: identity` row `modelName`, `gender`, `asset`, `colorMaskHsv`, `skins`, `toolSkin`.
- *S7t colour masks.* SE's paint on the mirrored grids and the
 character. **Formula (VERIFIED):** `PixelUtilsMaterials.hlsli` `FeedOutputInternal`: coloring = `_add.a`, reduced by
 `RemoveMetalnessFromColoring` (metal 0.4 → 0.7) unless METALNESS_COLORABLE; `ColorizeGray`: key = `ColorMaskHSV` +
 `frame_.TextureDebugMultipliers.ColorizeHSV`, hsv = (0, 0, max sRGB channel of the texel), final = saturate(hsv + key),
 base = lerp(texel, srgb_to_rgb(hsv_to_rgb(final)), coloring). `ColorizeHSV` = `MyTextureDebugMultipliers.Defaults`
 (0, 0.8, -0.1) (`MySector.SunProperties.TextureMultipliers` → `MyEnvironmentData` → `MyCommon` frame constants; the
 game's Environment.sbc sets only `GlossMultiplier`, `MyStructXmlSerializer` keeps the struct default) — the colour
 picker's `MyColorPickerConstants.HSVOffsetToHSV` (s + 0.8, v + 0.45) is the same shift. The key colour per part is
 already in every PartAdd (`colorMaskHSV` 0x48: cube parts `MyCubeInstanceData.ColorMaskHSV` = `MySlimBlock.ColorMaskHSV`,
 fat blocks/subparts `MySlimBlock.ColorMaskHSV` → `Render.ColorMaskHsv`, edges `ColorToHSVDX11`), a repaint re-sends
 the parts (S5's part hash includes the HSV; the colour key is in the chunk key, so a new colour is a new chunk/slot).
 **Subnautica:** the S3 tint (`PaintRules.MaterialColour`, UWE_3COLOR) and P9's character bake both take the shader key
 (`ColorMaskRules.ShaderKey`); every block slot (SE material × colour) drawn on a chunk or S5 child renderer gets SE's
 colour per texel (`ColorMaskPaint`): a round-robin scan of the views (≤ 1,024 renderers per frame; renderers carrying
 another material — the S7a ghost, P7 holograms — skipped; part flag 512 decides METALNESS_COLORABLE, a slot used by
 both kinds keeps the reduction), one sRGB readback of `_cm` + `_add` per painted set (≤ 1 new per frame, ≤ 4 held), one
 pool-thread bake per (set, colour, colourable) (≤ 2 in flight) kept from the mip whose side ≤ `maskSide` (default
 512), uploaded through the mirror's budget, set as `_MainTex` with UWE_3COLOR off and `_Color`/`_SpecColor` white
 (+ the coloured `_Illum`), re-applied after every `ApplyMaps`; no material created; total held ≤ `maskMb` (default
 256 MB; least recently used free bakes freed first, a slot that does not fit keeps the tint); a slot carrying P9's
 bake (`_MainTex` named `DE.S7p paint…`) is left to the character. `de.s7t {mask: bake|approx|status, maskSide: 64..2048,
 maskMb: 16..4096}` (chat `/de sn s7t mask approx`, `mask side 1024`, `mask mb 512`; the other S7t features' keys pass
 on) / env `DEEPENGINEERS_SN_MASK`, `DEEPENGINEERS_SN_MASK_SIDE`, `DEEPENGINEERS_SN_MASK_MB` (same defaults). Rows:
 Subnautica `s7t.mask` (every 2 s while slots or bakes exist): `mode`, `side`, `mb`, `mbUsed`, `slots`, `applied`,
 `bakes`, `baked`, `pending`, `queue`, `sources`, `inFlight`, `cycles`, `visits`, `foreign`, `scanCapFrames` (scan cap
 hit), `applyCapDrops` (> 64 overrides in a frame), `budgetWaits`, `evictions`, `idleFreed`, `staleReleased`,
 `overrides`, `restores`, `characterSkipped`, `mcMixed`, `bakesStarted`, `bakesDone`, `bakesFailed`, `readbacks`,
 `sourceWaits`, `cpuMsMax`, `disabled`, `key`; lines `DE.S7t mask baked #n … colorMaskHSV … key … seGrey144=(r,g,b)`,
 `DE.S7t mask budget full`, `DE.S7t mask FEATURE DISABLED`. SE logs once per process at the first mirrored grid
 `DE.S7t mask SE ColorizeHSV=(x,y,z) = … | DIFFERS …` (the run-time value against Subnautica's constant).
- *S7p-gameplay.*
 - **P14 big grids.
 main thread 4,474 ms in one C4 record (the RoomSet: `InteriorChunks.Reclassify` tested every box for every part),
 SE's link watchdog (3 s) then bumped the link epoch and the stream restarted with ClearAll. `format interior.class`
 is unchanged; Subnautica evaluates it through `S7pRoomIndex` (the boxes inflated by one cell in 8-cell buckets, one
 index per box array; exactly the scan's answer): measured on the same set, 100,669 lookups 19–26 ms (build 6 ms)
 instead of 2.0 s (.NET 4.7.2; Mono ≈ 2×), 4.8 box tests per part instead of 2,250. Per-frame budgets of the mirror's
 main-thread steps (`S7pMirrorBudget`, first unit always runs): Child parts created ≤ 2 ms and ≤ 48 per frame (the
 rest next frame, the grid stays ChildrenDirty), cell builds posted ≤ 2 ms (the rest stay dirty), worker results taken
 ≤ 3 ms; the parts of the models prepared in a frame are marked in one pass (was one full scan per model); the S3.C1
 grid model totals of the `DE.Mirror built` line are computed once per (grid version, model serial) (was one full
 scan per uploaded cell). Live `de.s7p {mirrorMs: x}` (child budget x ms, the others scaled; 0.25–20). SE: the S5
 export reads (deadline 1.5 ms between cells; one workshop render cell read 25–83 ms) are amortised by a debt
 (`ExportReadDebt`: a tick's excess over `readAvgMs` 4 ms is paid back by skipping the following ticks' reads, cap
 250 ms; the stream is late, far grids last): `de.s7p {readAvgMs: ms | off}`, env `DEEPENGINEERS_S7P_READAVG`.
 - **Rows.** Subnautica `s7p.mirror` (1 Hz while busy, every 10 s otherwise): `grids`, `parts`, `dirtyCells`,
 `buildsInFlight`, `buildsCapped`, `workerQueued`, `uploadsPending`, `uploadOverBudget`, `childDirtyGrids`,
 `childBacklog`, `childFramesDeferred`, `childrenDeferred`, `postsDeferred`, `resultsDeferred`, `preparedBatched`,
 `drainMsMax`, `drainFramesOverBudget`, `roomIndexBuilds`, `roomIndexBuildMsMax`, `roomLookups`, `roomLinear`,
 `roomBoxTests`, `roomPartsMoved`, `frames`, `framesOver16_7`, `frameP99Ms`, `frameMaxMs`, `budgetScale`, `busy`. SE
 `s7p.export` (1 Hz while busy, every 10 s otherwise): `grids`, `parts`, `cellsQueued`, `blocksQueued`,
 `firstPending`, `firstPassNotDone`, `readMsMax`, `readAvgMs`, `debtMs`, `skippedTicks`, `spikes`, `c3Queue`,
 `c3QueueBytes`, `c4Queue`, `c4QueueBytes`, `snPendingParts`, `snMirrored`, `busy`. A stall shows as `drainMsMax`
 or `frameMaxMs` ≥ 3,000 with SE's `DE.Link STALL`.
 - **P11 creature damage** (§4.2): Subnautica's own `S7EventReader` on the C2 extension list (the effect player's is
 untouched). BulletStreak: a ray from the muzzle along the shot, length min(800 m if the streak is at its 60 m cap,
 else the streak length; 250 m), hits sorted; the shooter's own Entity or S6 proxy (same entity or root) and the ghost
 Player are passed through, the first creature takes `ProjectileHp × count × scale` (count = round(intensity × 8),
 1–8; hand weapon = attached to a character: 30 SN_HP = SmallCaliber `ProjectileHealthDamage` 100 × AutomaticRifleGun
 `DamageMultiplier` 0.3; ship weapon: 33 = LargeCaliber 33) and stops the shot, anything else solid stops it.
 Explosion: each creature within `size` m once, `intensity × 1,000 × max(0, (r − d)/r) × scale`, d to the LiveMixin's
 transform (Subnautica's `DamageSystem.RadiusDamage` falloff), type Explosive. Tools (sustains by entity and key until
 their Stop row or 500 ms): every 0.25 s a sphere of radius clamp(size, 0.5, 3) m at the work point, each creature
 `dps × 0.25 × scale`: hand drill 20/s (Drill), hand grinder 16/s, ship grinder and ship welder 120/s, ship drill
 13.3/s (Drill), hand welder 0. Caps per frame: 64 events, 32 damage calls, 64 tools (drops counted).
 `de.s7p {damage: scale | off | on}` (Subnautica), env `DEEPENGINEERS_S7P_DAMAGE` (default 1). Rows `s7p.damage`
 (1 Hz while active, every 10 s otherwise: `scale`, `shots`, `blasts`, `toolSteps`, `hits`, `kills`, `hp`, `tools`,
 the totals, `blocked`, `passedShooter`, `missedEntity`, `lapsed`, `eventsDeferred`, `hitsDropped`, `toolsDropped`,
 `drainedOff`, `readerMissed`, `readerOverflow`, `readerQueued`, `lastHit`) and `s7p.hit` (≤ 20 per second: `why`,
 `creature`, `hp`, `healthBefore`, `healthAfter`, `maxHealth`, `killed`, `snType`, `local`, `at`, `scale`); a kill
 logs `DE.S7p damage KILL`.
 - **P12 depth hold** (SE): the Water Mod's grid buoyancy (`APPLY_WORLD_FORCE` every tick in the water) left SE's
 dampeners (gravity cancelled, velocity damped with 2·M) at a steady rise F/. A postfix on the private
 `MyEntityThrustComponent.CalculateRelativeVelocity(MyEntity)` adds −F/ (grid frame) to the dampening target of a
 `MyCubeGrid` with dampeners on, dynamic, while the mod applies buoyancy (its `BuoyancyForce`, `FluidDepth`,
 `PercentUnderwater`, `SimulatePhysics` read by compiled getters; Water Mod 3.23 pinned): the thrusters then command
 −F too (their own power and limits). Never the character's jetpack; autopilot thrust not covered.
 `de.s7p {hold: on | off | factor 0..2}`, env `DEEPENGINEERS_S7P_HOLD` (default on). Row `s7p.hold` (1 Hz, ≤ 8
 grids it acted on): `entity`, `buoyancyN`, `massKg`, `offsetMs`, `vUpMs` (velocity along the local up: ≈ 0 when it
 holds), `applications`, `factor`, the postfix counters.
 - **P13 camera axes.** SE turns the body (yaw) in walking mode only when the proxy is not in the air or the character
 falls or jumps (`MyCharacter.MoveAndRotateInternal`); a character that leaves a seat while already IN_AIR (no
 state change) is never set falling, and under water the swim model keeps it in the air: only the head turns. SE's
 `StartFalling` is called after 6 ticks of that state (retried every 120). `de.s7p {yawfix: on | off}`, env
 `DEEPENGINEERS_S7P_YAWFIX` (default on). Rows SE `s7p.camera` (1 Hz while the camera moves or has input, and
 `why: fix` at each fix): `yawDeg`, `pitchDeg` (motion over the second against the local up), `yawInputTicks`,
 `pitchInputTicks`, `blockedTicks`, `yawStuck`, `pitchStuck` (≥ 20 input ticks, < 0.05° of motion), `samples`,
 `groundState`, `falling`, `jumping`, `jetpack`, `sitting`, `ladder`, `dead`, `firstPerson`, `headX`,
 `cameraController`, `fixOn`, `fixes`, `fixFailures`, `episodes`; Subnautica `s7p.cam` (1 Hz while driven): `frames`,
 `yawDeg`, `pitchDeg`, `rollDeg` of the presented camera, `nonFinite` (C1 poses with a non-finite component, now held
 at the last good pose instead of reaching the transform), `nonFiniteTotal`.
 - `de.s7p` is shared by the S7p features: each answers only its own keys (gameplay: SE `hold`, `yawfix`,
 `readAvgMs`; Subnautica `damage`, `mirrorMs`; both `action: "gameplay"` for the status) and passes the others on.
- *S7t look match.*
 - **Command.** `de.s7t {look: "key=value …" | {key: value} | [tokens] | "status"}` on each side (C11; SE chat
 `/de s7t look …`, Subnautica through SE's relay `/de sn s7t look …` = `{argv: ["look", …]}`). `de.s7t` is shared by
 the S7t features: the look handler answers only a `look` key or an argv whose first word is `look` and passes the
 others on (SE: first in `Plugin.ExtraCommand`; Subnautica: before the colour-mask handler in `Bridge.Handle`).
 Tokens: `on`, `off`, `reset` (the match set's defaults), `status`, `key=value`, `key=unset` (SE's or Subnautica's own
 value stays), `key=default`, `force=0..1|auto` (the weight forced anywhere in the world, for tuning by eye),
 SE `ramp=m` / `ease=s`, Subnautica `low=m` / `high=m` / `ease=s`. SE keys: §3.4's table (colours `r,g,b` in 0..1
 or 0..255; one factor for the three `tint`/`sunColor` channels); Subnautica keys `shadow`, `ambient`, `sun`,
 `exposure`. Env defaults applied at start in the same syntax (blank- or `;`-separated): SE `DEEPENGINEERS_LOOK`,
 Subnautica `DEEPENGINEERS_SN_LOOK` (`off` turns the side off). Reply `line` (applied, problems, the weight and its
 gate; the full table on `status`), SE also `w`, `wTarget`, `gate`, `table`, `hooks`, `missing`, the hit counters.
 - **Rows `s7t.look`.** SE (1 Hz while the weight is above 0; an edge row on every gate or weight-end change, ≤ 4 per
 second, the rest counted in `rowsDropped`; one per command): `side` "se", `why` (periodic | edge | command),
 `tick`, `gate` (off | noWorld | force | bridge | peer | policy | site | y | underwater | band | ramp | high), `w`,
 `wTarget`, `y` (null without a world), `band`, `ramp`, `ease`, `force` (null = auto), `enabled`, `draws`,
 `activeDraws`, `envHits`, `fogHits`, `planetHits`, `postHits` (one per frame each while active: a hook at 0 after
 300 active frames is logged as never run), `atmoSent`, `atmoDeferred` (a change held back by the 10-frame cap),
 `atmoFailures`, `atmoRestores`, `atmoModified`, `hooks`, `missing`, `errors`, `rowsDropped`, `disabled` (when a
 hook failed), `live` {key: [own…, now…]} for every set key (SE's own value and the value sent, per channel).
 Subnautica (every 2 s while a member is held, edges as SE's, one per command and per restore): `side` "sn", `why`
 (periodic | edge | command | restore), `snFrame`, `gate` (off | noWorld | force | peer | c5 | policy | y |
 underwater | surface | ramp | high), `w`, `wTarget`, `y`, `low`, `high`, `ease`, `force`, `enabled`, `sky`,
 `sunLight`, `uSkyLight` (the objects found), `frames`, `writes`, `restores`, `rowsDropped`, `disabled`, `live`
 {key: [own, now, target]}, `externalChanges` (the game changed a held member; its value became the own).
 - **Logs.** SE `DE.S7t look match: <table>` at start, `DE.Harmony SE s7t look: N hooks (…)` (+ `MISSING …`),
 `DE.S7t look …` per command, `DE.S7t look hooks all ran …` or `… never ran …` once after 300 active frames,
 `DE.S7t look atmosphere: the planet's own settings sent back`, `DE.S7t look DISABLED …` (a hook threw: SE's own look
 from the next frame). Subnautica `DE.S7t look match (Subnautica): <table>`, `DE.S7t look (Subnautica) …` per
 command, `DE.S7t look restored Subnautica's own light (<why>)`, `DE.S7t look DISABLED …`.
 - **Multiplayer keying (v3).** The look match is the local viewer's renderer only (one camera per machine): no
 entity, record or player key.
- *S7t altitude: the sea to the horizon, the haze, the impostors.*
 - **Rule.** The ocean surface is a square of ±`camera.farClipPlane` around the camera (VERIFIED
 `WaterSurface.BeginRenderWaterSurface` → `HeightFieldMesh.BeginRender(…, size: farClipPlane, …)`) and the far plane
 is 1,700 m: from altitude the sea ended 1.7 km out and, at a yaw beyond 45°, the frustum saw past the square's side
 Above +2 m under `pictureSite` the far plane follows the
 geometric horizon of SE's sea, `farK × sqrt(2·Rs·h + h²)` (Rs = `SiteV1.RsExpected`; 6.8 km at 380 m, 11 km at
 1 km), clamped to [Subnautica's own, `farMax` 20 km] and at least `impostorFar` 6,000 m while impostors exist,
 through `SNCameraRoot.SetFarPlaneDistance(float)`; below +2 m, switched off, under `Waterline`, on an exception
 and at teardown Subnautica's own value comes back (§1.1). A horizon skirt (one ring mesh, rows v =
 `HorizonRules.SkirtRows`, 128 columns, Unity's `Sprites/Default` else `Hidden/Internal-Colored`, no depth write,
 queue = the water material's + 1, enabled for the main camera only) is moved to the camera and scaled
 (R, altitude, R) at its pre-cull: row v shows the sea at distance R / v, R = `fadeStart` × far; alpha = smoothstep
 over the sea distance from R (0) to `fadeEnd` × far (the frame's alpha), the horizon colour above the horizon
 feather (alpha 0). Colours: the sky at the horizon (197,241,255) and the far water (129,155,187) measured on
 Subnautica's own 380 m screenshot (the look match keeps Subnautica's palette as the reference and carries no colour;
 `AltitudeHost.Targets` is the hook if a target table ever does), in linear light, times uSky's own mean sky colour
 now over noon per channel (read only; floor `night`). `fogMul` ≠ 1 rewrites `_SkyFogDensity` = uSky's own ×
 `fogMul` after uSky's Update (default 1 everywhere: Subnautica's own aerial perspective). The haze table
 (`HorizonRules.DefaultTable`, linear in altitude): AltM 0/50/150/400/1000/1500/3000, fadeStart.85/.70/.55/.45/.40/.38/.35, fadeEnd 1.00/.98/.97/.96/.95/.95/.95, alpha 0/1/1/1/1/1/1, farK 1, fogMul 1.
 - **Impostors (T3, Subnautica half).** GridAdd `flags` bit 6 `Impostor` (= 64, `ImpostorRules.ImpostorGridFlag`; read
 only on a version-1 GridAdd; Wire `S7tImpostorRules.GridFlagImpostor`, set by SE's producer, "S7t impostors"). Built
 coarse: chunked parts whose model's largest extent ≥ `minPart` (0.75) × the block size (2.5 m / 0.5 m) only, one
 chunk per cell and colour with the reserved material key `0x4445537374494D50` (Subnautica's MarmosetUBER, no
 texture, `_Color` = the part's paint), no shadows, no Child parts (subparts, doors, rotor tops removed), no lights and
 no effects attached to it (skipped and counted); its cells are posted at most `perFrame` (4) per frame, the rest stay
 dirty (`cellsDeferred`); culled beyond sqrt(1,600² + `reach`²) + 100 m above the sea (`reach` 5,500 m = SE's
 `S7tImpostorRules.UpLeaveM`), S5's distance
 under water. A GridAdd that clears the bit (or `impostor off`) rebuilds every cell in full, cell by cell. Collision:
 Subnautica builds proxies only from a SkinSet, which SE's producer does not send for an impostor (SE side).
 - **Command.** `de.s7t {horizon: on|off} | {haze: "key=value …" | "reset"} | {impostor: on|off, key: value} | {alt}`
 (C11; chat `/de sn s7t horizon off`, `/de sn s7t haze alpha=0.8 fadeStart=0.5`, `/de sn s7t impostor minPart=0.5`,
 `/de sn s7t alt` = `{argv: [...]}`); this handler answers only those first words/keys and passes `look`, `mask` …
 on. Keys: `alpha` 0..1, `fadeStart` 0.05..0.99 / `fadeEnd` 0.06..1 (of the far plane), `farK` 0.1..4, `farMax`
 1,000..100,000 m, `fogMul` 0..20, `feather` 0..0.5, `horizonRgb` / `lowRgb` r,g,b (0..255), `tod` on|off, `night`
 0..1, `gain` 0..8, `minAlt` m, `impostorFar` m, `reach` m, `minPart`, `perFrame`; `table` resets a table key. Env
 defaults (same default as the command, read at start): `DEEPENGINEERS_S7T_HORIZON` on|off (on),
 `DEEPENGINEERS_S7T_HAZE` key=value pairs separated by blanks or `;` (empty = the table; a bad list keeps the whole
 table), `DEEPENGINEERS_S7T_IMPOSTOR` on|off (on). Reply: `state`, `altM`, `horizonM`, `farBaseM`, `farM`, `skirtOn`,
 `skirtRadiusM`, `fadeRatio`, `alpha`, `fogMul`, `horizonRgb`, `lowRgb`, `colourSource`, `shader`, `queue`,
 `waterQueue`, `impostors`, `impostorCellsPosted`, `impostorCellsDeferred`, `disabled`, `settings`, `line`.
 - **Rows.** Subnautica `s7t.alt` (1 Hz, at once on a state change and per command): `state` (off | below | above),
 `altM`, `horizonM`, `farBaseM`, `farM`, `farApplied`, `impostorFar`, `pictureSite`, `skirtOn`, `skirtBuilt`,
 `skirtRadiusM`, `fadeRatio`, `alpha`, `feather`, `fogMul`, `fogWritten`, `horizonRgb`, `lowRgb`, `colourSource`

 `preCulls`, `farSets`, `farRestores`, `skirtBuilds`, `colourWrites`, `fogWrites`, `errors`, `msMax` (the Tick's
 worst ms since the last row), `disabled`. `s7t.imp` (1 Hz while impostors exist or were built, and per command):
 `on`, `impostors`, `cellsPosted`, `cellsDeferred` (the per-frame cap's drop counter), `partsKept`, `partsSkipped`,
 `childrenRemoved`, `lightsSkipped`, `effectsSkipped`, `swapsToCoarse`, `swapsToFull`, `slotsCreated`, `culled`,
 `minPart`, `perFrame`, `reachM`. Logs `DE.S7t altitude <settings>` at start and per command, `DE.S7t horizon
 above|below|off at <m> m: far …` on a state change, `DE.S7t horizon skirt built …`, `DE.S7t horizon skirt queue …`,
 `DE.S7t impostor grid=<id> coarse|full …`, `DE.S7t altitude DISABLED …` (an exception: the far plane restored).
 - **Multiplayer keying (v3).** The far plane and the skirt are the local viewer's camera only; impostors are keyed by
 the GridAdd entity id like every mirrored grid (S7-STATUS 5.6).
- *S7t impostors.*
 - **Bit.** GridAdd `flags` bit 6 `Impostor` = 64 (`S7tImpostorRules.GridFlagImpostor`): the free bit after S5's 0–2,
 S7a's `Ghost` 3 and S7p's `Projection` 4 / `Missile` 5, outside the version nibble; read only on a version-1 GridAdd
 (`IsImpostor`). Set → a coarse impostor; a GridAdd for the known id that clears it = the upgrade (the held-back parts
 follow), one that sets it = the demotion (the parts already sent stay, nothing finer follows).
 - **Reach (the vertical rule; the S7a radii 1,500 / 1,600 m stay the horizontal one).** Under the effective `Site`
 policy with the entity set allowed to add (C5 zone CORE / TILE VOID / OUTER VOID, the stream up), every 30 ticks SE
 queries `MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref BoundingSphereD, List<MyEntity>, MyEntityQueryType)`
 on a sphere centred 2,700 m above the camera along the planet's radial,
 radius 3,225 m (the whole leave cylinder). A candidate is a physical-hierarchy root (`EntitySet.RootOf`) that is a
 `MyCubeGrid` of `GridSizeEnum` Large ("large blocks only"), not `IsPreview`, ≤ 30,000 blocks, mirrorable, not in the S5
 entity set, whose `WorldAABB` sphere (centre, half-diagonal) split along the radial at the camera has a horizontal
 clearance ≤ 1,500 m and reaches from the camera's level up to 5,000 m (`InReach`, enter); a member stays while the
 clearance is ≤ 1,600 m and the sphere spans −100 m…+5,500 m (leave). Admission: the nearest 2 per query while fewer
 than 16 impostors exist (`Admit`; ties by id; the rest counted `capDrops`; `small` / `tooBig` counted).
 - **Coarse export.** Only the root grid (no subgrids). The S5 exporter's diffs pass `S7tImpostorRules.Filter`: cube
 parts (class 1) and fat blocks (3) go, edges (2) and subparts (4) are held back (never requested as models, never
 sent; a replace or recolour updates the held copy, a remove drops it unsent); no Dynamic-subpart C2 rows; never in
 the S6 collision skins or the S7 room sets; no light rows (60 m) and no effect rows (250 m) reach it; textures are
 Subnautica's choice (its coarse material). C2 entity rows as any root. Upgrade: the S5 entity set takes the grid (it
 came within the S7a radius): GridAdd without the bit, then the held parts on the first-send lane, ≤ 512 a tick
 (`flushDeferred`). Demotion: the entity set lets a root go (outside the leave radius for its 60 s) that is inside the
 reach, large, ≤ 30,000 blocks and under the cap: it stays as an impostor instead of an EntityRemove (its subgrids
 leave). Leave: EntityRemove when out of the reach, `impostors off` or not `Site`. A stream restart clears the held
 sets (the exporter re-emits everything, the filter holds again).
 - **Toggle.** `de.s7t {impostors: on|off}` (chat `/de s7t impostors off`), env `DEEPENGINEERS_IMPOSTORS` (default on).
 - **Row.** SE `s7t.impostor` (1 Hz while impostors exist or a counter moved): `tick`, `active`, `members`, `queries`,
 `candidates`, `admitted`, `upgrades`, `demotions`, `leaves`, `capDrops`, `small`, `tooBig`, `heldParts`, `heldNow`,
 `flushed`, `flushDeferred`, `queryMs`, `queryMsMax`. Logs `DE.S7t impostor <id> in (…)`, `… upgraded to a full mirror
 …`, `DE.S7t grid <id> demoted to an impostor (…)`, the S5 `leave` rows with `impostor out of the reach …`.
 - **Multiplayer keying (v3).** Keyed by the GridAdd entity id like every mirrored grid; the reach is the local camera's.
- *S7t crossfade.*
 - **Rule (design F).** Under the effective `Site` policy the band swap is a blend: SE's scene stays drawn while one
 alpha pass, in a prefix of `MyRender11.ConsumeMainSprites`
 scales the backbuffer to
 (colour × f, alpha) — blend ZERO / BLEND_FACTOR on colour, BLEND_FACTOR / ZERO on alpha; f = alpha^2.2 on SE's
 `R8G8B8A8_UNorm_SRgb` backbuffer (the blend runs on linear values: the stored value becomes alpha × itself, the
 premultiplied colour DWM composes), f = alpha on a linear target. The HUD, the GUI and S7a's band tint draw over it
 with their own blending. Alpha = 1 − smoothstep(p), p ∈ [0, 1] the ramp (0 = SE's picture), moving at 1/`ms` per
 millisecond of the render thread's QPC clock toward its target, so a reversed ramp turns back from where it is. The
 target is Subnautica while the machine's picture latch is on and its F15 leave dwell (`ViewMachineState.LeftArmed`:
 the altitude exit condition holds; Approach → Space after 1 s) has not started: the fade in starts at the latch's
 on edge (the request carries the skip; the render thread engages it only at p = 1, the scene drawn and faded until
 then); the fade back starts at the leave dwell (the request drops the skip at once; the 700 ms ramp ends before the
 latch drops in Space; the margins stay on while p > 0); a dwell cancelled turns back (`back`); a latch that drops
 without the dwell (site, peer, re-streaming loss, zone) snaps to SE's picture at once (`snap`, S2's rule). The S2
 alpha fill skips a faded frame. Off (S7a's instant swap) under `Waterline`, design C, the dev see-through toggle or
 forced margins/skip, `de.s7t fade off`, and in A′ unless its switch is on; and for the session once 3 drawn frames
 of an F ramp never reached the prefix (patch missing or inlined: `DE.Display crossfade OFF …`, status `hookDead`).
 The main sprites always take that path

 - **A′ (switch `fadeAprime`, default off).** A′ has no per-pixel alpha (it is the not-honoured fallback): SE's window
 takes the ramp as its constant alpha (`WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA)`, render thread): on top
 and drawing while it fades out, lowered at p = 1 (style removed); back: drawn frames first (S2), raised at alpha 0,
 faded in, style removed at p = 0. A failed call keeps the cut (logged; retried with the next ramp). HYPOTHESIS: a
 layered window and SE's swap chain (A′ is chosen when DWM's alpha is not honoured, e.g. a flip-upgraded chain).
 - **Band tint.** `CrossingFlash` draws no band tint (and writes no `s7a.flash` row) while the crossfade covers the
 swap (`S7tSettings.CrossfadeCovers`); `de.s7t {flash: on}` (env `DEEPENGINEERS_S7T_FLASH=on`) draws it too. The
 `Waterline` tints are unchanged.
 - **Commands.** `de.s7t {fade: <ms> 50..5000 | on (700) | off | straight | premul}` (chat `/de s7t fade 700`), `{flash:
 on|off}`, `{fadeAprime: on|off}`, `{action: composite}` (or a bare `de.s7t`): the state. Env `DEEPENGINEERS_FADE_MS`
 (700; `off`/0 = the cut), `DEEPENGINEERS_FADE_APRIME` (off), `DEEPENGINEERS_FADE_STRAIGHT` (off: the dev comparison
 without premultiplying). The verb is shared with the other S7t handlers (each answers only its own keys or first
 word; this one `fade`, `flash`, `fadeAprime`, `impostors`, `composite`).
 - **Row.** SE `s7t.fade` at each start and end of a ramp (≤ 20 a second, the rest counted in `dropped`): `tick`, `phase`
 (start | end | superseded), `dir` (toSn | toSe), `ms` (the setting), `path` (F | A' | snap | cut: the ramp off),
 `reason` (latch | leaveDwell | back | loss | off | none), `view`, `design`, `p`, `actualMs` (end and superseded),
 `passes`, `heldSkips`, `marginHolds`, `passErrors`, `layered`, `flash`, `dropped`. Under `Waterline` no row. Logs
 `DE.Display crossfade to Subnautica|to SE path=… ms=… reason=…`, `DE.Display crossfade done … in <ms> ms (passes …)`,
 `DE.Display crossfade snap to SE …`, `DE.Display crossfade pass FAILED …` (once per distinct error), `DE.Flash band …
 not drawn: the S7t crossfade covers the swap`, `DE.Harmony S7t OK MyRender11.ConsumeMainSprites …` (or `MISSING …`:
 the crossfade off). `de.display state` carries `crossfade {…}`.
 - **Perf.** SE draws its full scene during the ramps only (700 ms each way at the band); the machine's perf modes are
 unchanged. Multiplayer: the local viewer's display only.

*Budgets and back-pressure (S5).*

| Item | Budget | When over |
|---|---|---|
| SE C3 serialisation | ≤ 2 ms per tick, ≤ 16 MiB written per second | models stay queued, ordered by the nearest pending part that needs them |
| SE model loading | the one worker thread `DE.ModelLoader`, one model at a time | queued |
| SE C4 | ≤ 1 ms and ≤ 512 KiB per tick | queued (try-once writes, the update thread never waits) |
| SE C4 order | (1) ClearAll, EntityRemove, GridAdd re-parent, CharacterAdd; (2) edits within 50 m of the camera, newest first; (3) other edits, newest first; (4) first sends, nearest entity first, within it nearest cell first. SE build pass: lane 1 also carries every first GridAdd and the character's PartAdds; within a lane the order is FIFO (a newest-first reorder could put a PartRemove before the PartAdd of the same key), "newest first" holds between entities; an entity's edit never takes a higher lane than one still holding its earlier records; an EntityRemove first drops the entity's queued records of lanes 2-4 | |
| SE back-pressure | C4 free < 1 MiB: classes (1)–(2) only; C3 free < 8 MiB: no new model starts | resumes when free |
| SE C1 + C2 per tick | ≤ 0.3 ms target; 1,024 entities; 2,048 subpart rows (order above) | rows dropped, counted `c2Dropped` |
| SE dirty diff | at most once per tick per (grid, cell) | coalesced |
| Subnautica drain | ≤ 4 ms or 48 MiB per frame (§5.1) | rest next frame |
| Subnautica chunk builds | worker threads; one build in flight per chunk key; ≤ 256 queued, dirty-newest then nearest | coalesced |
| Subnautica uploads | ≤ 2 ms per frame, `Mesh` reused (`SetVertexBufferParams/Data`, `SetIndexBufferData`, `SetSubMesh`) | next frame |
| Subnautica character | bone tree posed once per camera sample; retarget ≤ 0.3 ms per frame (HYPOTHESIS) | Retarget off, SeBones, logged |

*C7 counters (S5).* `mirroredEntityCount` = entities known by GridAdd (grids, subgrids, floating objects) plus the
character known by CharacterAdd; `pendingParts` also counts skinned parts waiting for their SkinnedBind. SE's consumer
audit (S3) uses the same definitions on its side.

*Stream recording (`.derec`, tests only).* `%LOCALAPPDATA%\DeepEngineers\recordings\<name>.derec` plus `<name>.json`;
derived from the player's own game, never in the repository or a game folder. Little-endian. Header (64 B): 0x00 u8[8]
magic `DEREC001`; 0x08 u32 formatVersion 1; 0x0C u32 headerBytes 64; 0x10 u64 layoutHash; 0x18 u64 conventionsHash;
0x20 u32 wireVersion; 0x24 u32 seVersion (1210014; 0 synthetic); 0x28 u64 createdUnixMs; 0x30 u32 flags (bit 0 has
C1, 1 has C2, 2 synthetic); 0x34 u32 worldEpoch; 0x38 u64 reserved 0. Records: a 24 B header {0x00 u16 kind, 0x02 u16
pad, 0x04 u32 bytes, 0x08 u64 seTick, 0x10 i64 qpc} then `bytes` bytes padded to 8. Kinds: 1 a C3 message exactly as
written (`{u32 type, u32 payloadBytes}` + payload, unpadded), 2 a C4 message, 3 a C2 snapshot (the 0x40 header, then
`entityCount` × 96 B, `subpartCount` × 64 B, `lightCount` × 64 B, packed), 4 a C1 record (0x100 B + `boneCount` × 48 B),
5 a marker (one UTF-8 JSON object, e.g. `{"ev":"edit","kind":"grind"}`), 0xFFFF end (0 bytes; a file without it is
replayed to its last whole record). Writer: the SE plugin, dev builds only (`de.s5 record {name, c1, c2}` / `de.s5
record stop`), from the producer's write path (each message after a successful ring write, with that tick), through a
bounded queue drained by a background thread (drops counted, never a wait). Starting a recording restarts the stream
(ClearAll and everything again; `restart: false` keeps it) so the file holds every model and part it needs. Synthesised ships use the same format
with `synthetic` set. The fake producer replays a file after the hello ack with the S5 budgets (`--replay <name>`,
`--synth 500|2000|5000`). The JSON sidecar: name, formatVersion, created, the hashes, seVersion, first and last tick,
counts per kind, bytes, models, grids, parts, dropped, sha256 of the `.derec`.

**C5 SeState (256 B):** 0x00 u32 seq; 0x04 u32 flags (bit 0 seForeground, 1 sePaused, 2 seExiting, 3 seeThrough
(scene skipped), 4 cameraInAirtightRoom, 5 marginsOn, 6 zOrderOk, 7 seMixActive, 8 guiScreenOpen); 0x08 u64 tick; 0x10
u32 viewState (0 Space, 1 Approach, 2 Underwater, 3 Surfacing); 0x14 u32 approachMode (0 none, 1 Track, 2 Prime); 0x18
u32 displayMode (0 F, 1 A′, 2 C); 0x1C u32 overlays (bit 0 Paused, 1 Unfocused, 2 PeerStalled, 3 PeerExited (also the
acknowledgement of `Quitting`)); 0x20 u32 zone (0 CORE, 1 TILE VOID, 2 OUTER VOID, 3 FAR, 4 off-site); 0x24 u32
worldEpoch; 0x28 u32 perfMode (0 Idle, 1 Track, 2 Full); 0x2C u32 targetFps; 0x30 f32 snBusTarget; 0x34 f32 crossfadeMs
(300); 0x38 u32 helloEpoch; 0x3C u32 linkEpoch; 0x40 f64 Rs; 0x48 u64 siteHash; 0x50 u32 siteVersion; 0x54 u32 pad;
0x58 i32 seRect[4]; 0x68 f32 camSnY (diagnostic); 0x6C f32 camVelSn[3]; 0x78 u64 crossingTick (SE tick of the latest
downward waterline crossing; exact meaning in "C5/C7 semantics for S2" below); 0x80–0xFF pad. S7a uses flags bit 9
`pictureSite` (outside the layout's `SeStateFlags`, pinned by the S7a conventions: "C5/C7 semantics for S7a" below);
every other bit, field and offset keeps its meaning.

**C6 SeTime (64 B):** 0x00 u32 seq; 0x04 u32 jumpSeq; 0x08 u64 tick; 0x10 f64 hourAngleRad (0 at local noon at the
camera); 0x18 f32 sunElevationRad; 0x1C f32 sunDirSn[3]; 0x28 f32 dayLengthSec (1,200); 0x2C f32 simSpeed; 0x30 u32
paused; 0x34 u32 flags (bit 0 timeSyncValid: sun checks passed; bit 1 skyOnly); 0x38 f32 sunriseHourAngleRad (π/2 when
the declination is 0); 0x3C u32 pad. First written by S7a (no slice wrote C6 before): every field's exact meaning, the
flags bit 2 `skyPoleValid` and the sky pole in the pad bytes 0x3C–0x3F are in "S7a conventions" below.

**C7 SnStatus (256 B):** 0x00 u32 seq; 0x04 u32 flags (bit 0 worldLoaded, 1 streamingSettled, 2 combatEnabled = not
`GameModeUtils.IsInvisible`, 3 wireSelfTestPass, 4 areaBuilt, 5 rectOk, 6 uiHidden, 7 frozen, 8 runInBackgroundForced, 9
timeSyncApplied, 10 damageSelfTestPass); 0x08 u32 snPhase; 0x0C u32 perfModeApplied; 0x10 u64 snFrame; 0x18 u64
presentedSeTick; 0x20 u64 areaBuiltSeTick; 0x28 f32 areaBuiltPos[3]; 0x34 u32 helloAckEpoch;
0x38 u32 linkAckEpoch; 0x3C u32 mirroredEntityCount; 0x40 u32 pendingParts; 0x44 u32 damageDropped; 0x48 u64
c3BytesDrained; 0x50 u32 damageOverflowDepth; 0x54 f32 frameMsP50; 0x58 f32 frameMsP99; 0x5C u32 hostKind (1 =
Subnautica 1); 0x60 u64 snHwnd; 0x68 i32 snRect[4]; 0x78 u32 capabilities (bit 0 underwater picture, 1 creatures and
damage, 2 statics, 3 time of day, 4 capture); 0x7C u32 vSyncCountApplied; 0x80 i32 targetFrameRateApplied; 0x84 f32
fixedDeltaTime; 0x88 u32 resamplesLastFrame; 0x8C–0xFF pad.
- `presentedSeTick` = the newer bracket tick of the camera interpolation used in the last presented frame (the tick of
 the newest snapshot used: the camera orientation always uses it), stamped at the end of that frame. Wording open
 25 tick old
 the S1 code (real run: transport p95 16 ms, worst lag 1 tick) implements **the newer bracket tick**, inside the
 freshness rule below either way.
- Freshness for `snReady`: `presentedSeTick ≥ tick − 4` (D = 1.25 ticks plus a 60 fps frame period of 1 tick, rounded
 up, plus 1; at 30 fps the older bracket would satisfy a 2-tick bound on only about 37.5 % of ticks). Entry into
 Underwater additionally needs `presentedSeTick ≥ crossingTick` (C5).

**C5/C7 semantics for S2 (wording only; layout 1.0 and its hash unchanged).** In S1, C5 carried `viewState` Space
only, `perfMode` Full or Idle by zone (S1 D65), `snBusTarget` 0 and `crossfadeMs` 300; Subnautica stayed in
`ReadyIdle` with its camera driven, its cameras on and time running, and applied no perf recipe (S1 D73), so
`snPhase` never reached WarmingUp or Active and `snReady` was never true. From S2 the SE plugin's state machine
(ARCHITECTURE §4; every threshold in the table of ARCHITECTURE §4.2, with `y` negative underwater) writes the fields
below each tick and Subnautica applies them. The S1 camera-drive rule (ghost Player rule and drive gate, ARCHITECTURE
§5.11) holds in every view state; the recipes add the freeze, the cameras, the frame-rate pair and the mix on top.

| C5 `viewState` (`approachMode`) | C5 `perfMode`, `targetFps` | Subnautica recipe | C7 `snPhase` | C5 `snBusTarget`² | SE side of the mix |
|---|---|---|---|---|---|
main and GUI cameras off; (`vSyncCount` 0, `targetFrameRate` 10); process BelowNormal | ReadyIdle 4 | 0 | SE per config |
| Approach 1 (Track 1) | Track 1, 10 | time running; cameras off; ghost and camera transform at F(P), streaming follows it; (0, 10); Normal | WarmingUp 5 | 0 | SE per config |
| Approach 1 (Prime 2) | Full 2, 0 | cameras on, full rate, shaders warm | WarmingUp until the first frame rendered with the Full recipe, then Active 6 | 0 | SE per config |
| Underwater 2 (none 0) | Full 2, 0 (120 when CPU-bound, ARCHITECTURE §5.13) | Full | Active 6 | 1.0 | SE game 0.7 × config¹, SE HUD per config, SE music 0, Water Mod underwater sound 0 (`seMixActive`) |
| Surfacing 3 (none 0) | Full 2, 0 | Full | Active 6 | 0 (crossfade back) | back to config (crossfade) |

¹ S14 inside SE's game volume: each SE thruster's own sound under water follows S14's envelope (level × gain on its
emitter), and with Subnautica's `engine subnautica` mode SE's hydrogen and ion thruster sounds are faded to 0 (Subnautica's Cyclops
and Seamoth loops play them); §5.4 "S14 conventions". The 0.7 factor itself is unchanged.

² S15: Subnautica applies the target to its own options VCAs instead of `bus:/`: the effects and ambience follow it as before
(0 above water, over `crossfadeMs`), the music and the voice stay live above water at `Mix.MusicAbovePermille` /
`Mix.VoiceAbovePermille` (0.6) of the player's level with a 2 s crossfade (a line started under water ends above it), and
`bus:/` stays at 1, muted only with no live SE, SE not in front or quitting. Without the VCAs, or under S15.M19, the old `bus:/`
fade and mute. SE's side is unchanged; in Space the `FeedbackPanel` freeze still pauses `bus:/` (music included).

Overlays and peer states change the mix and the freeze, not the view state (ARCHITECTURE §4.3):

| Condition | C5 | Subnautica | SE |
|---|---|---|---|
the `FeedbackPanel` freeze also pauses `bus:/`); after ESC in Space and resume it stays frozen (Idle) and silent | the plugin writes no SE volume |
| A GUI screen other than gameplay open, not paused (inventory, terminal, toolbar config, chat; `guiScreenOpen`) | unchanged | unchanged: SE's simulation runs, so Subnautica is not frozen | the plugin writes no SE volume (the last mix stays until the screen closes) |
| SE not foreground (`seForeground` 0, overlay bit 1) | `snBusTarget` 0; `targetFps` 30 while Underwater | muted; (0, 30) while Underwater | SE mutes itself (`EnableMuteWhenNotInFocus`); the plugin writes no SE volume; both silent |
| PeerStalled or PeerExited (overlay bits 2, 3: SE's view of Subnautica) | the state machine runs with `snReady` false (Underwater → Surfacing at once) | n/a | within one SE frame: scene drawn, margins off, SE volumes and the Water Mod sound restored |
| SE stalled or exited (Subnautica's view: C0 heartbeat 3 s, 120 s while loading; process exit) | stale, ignored | applies Idle by itself, muted, `snPhase` ReadyIdle (Quitting on exit); never quits on heartbeat loss | n/a |

Field meanings:
- C5 `approachMode` is Track or Prime only while `viewState` = Approach, else 0. `overlays` bits are set while the
 overlay holds.
- C5 `targetFps` selects the frame-rate pair: 0 = no cap, (`vSyncCount` 1, `targetFrameRate` −1: the display rate,
 165 Hz here); n > 0 = (0, n). Subnautica sets the pair directly and re-asserts it every frame (§1.1).
- C5 `snBusTarget` is Subnautica's `bus:/` target volume (0…1), reached by a linear ramp of `crossfadeMs` (300) on the
 unscaled clock, restarted from the current value whenever the target changes (since S15 `voice 5` the target of its effects and
 ambience group, the music and the voice live above water: footnote ²).
- C5 flags: `seeThrough` (3) = the render thread skips the scene in the frame being presented; `marginsOn` (5) =
 `DwmExtendFrameIntoClientArea(seHwnd, −1)` returned S_OK and is in effect (cleared once margins 0 are applied);
 `zOrderOk` (6) = the z-order walker (ARCHITECTURE §5.1) found `snHwnd` directly under `seHwnd` at its last check
 (after each re-assert, up to 3 retries); `seMixActive` (7) = the plugin's underwater SE mix is in effect (applied and
 not yet restored); `guiScreenOpen` (8) = a GUI screen other than gameplay is open.
- C5 `seRect` and C7 `snRect`: the window rectangle in virtual-screen pixels, Win32 `RECT` order {left, top, right,
 bottom} (right and bottom exclusive), from `GetWindowRect` of `seHwnd` / `snHwnd` (both windows are borderless, so
 window = client area); on this PC (0, 0, 2560, 1440). C7 `snHwnd` repeats C0's.
- C5 `camVelSn`: the camera velocity in SN m/s (rate of change of C1 `camPos` per simulation second; written since S1).
- C5 `crossingTick`: the first SE tick of the current descent at which `y` ≤ −0.20 m (the Underwater entry threshold),
 so `presentedSeTick ≥ crossingTick` means Subnautica has presented a frame from below the entry threshold.
- C7 `snPhase`: `ReadyIdle` is first published when the host save is loaded, the ghost is set up, the window is
 restyled and the Idle recipe is applied (the launcher's ready phase); afterwards the phase follows `perfModeApplied`
 as in the table above. C7 `perfModeApplied` is the recipe in effect at the end of the frame (it lags C5 by at most a
 frame, and is Idle while SE is stalled); `vSyncCountApplied`/`targetFrameRateApplied` are read back after the
 per-frame re-assertion.
- C7 `presentedSeTick` describes a presented camera frame: with the cameras off (Idle, Track) it is not advanced, so
 the freshness rule and `snReady` can hold only from Prime on.
- C7 flags: `streamingSettled` (1) = `LargeWorldStreamer.IsWorldSettled` seen on a busy → idle edge (or idle for
 1 s) since the last world load, teleport or jump, new SE generation or link recovery; `areaBuilt` (4) = both ±16 m
 boxes (camera, and forward along C5 `camVelSn`, `max(20 m, |v|·1 s)` ahead) reported built by
 `IsRangeActiveAndBuilt`, with `areaBuiltSeTick`/`areaBuiltPos` the `presentedSeTick` and SN camera position of the
 last frame for which they were (S1); `rectOk` (5) = `snRect` equals C5 `seRect` and Subnautica is on the expected
 display, compared every second; `uiHidden` (6) = `SetHidePhase(MaskHUD)` applied (checked every frame) and the UI sinks
 installed (S1 D85: not a pixel check)
 `runInBackgroundForced` (8) = `runInBackground` true in memory and `FreezeTime` holding no `ApplicationFocus` entry.
- `snReady` is computed by SE from C0 and C7 (ARCHITECTURE §4.1): peer Alive ∧ `snPhase` Active ∧ `areaBuilt` ∧
 `streamingSettled` (only after a load, teleport, new generation or link recovery) ∧ freshness ∧ `rectOk` ∧
 `uiHidden`.

**C5/C7 semantics for S7a (picture policy; wording only, layout 1.0 and its hash unchanged;**
The view machine keeps its states, thresholds and transitions (ARCHITECTURE §4.2); Underwater still gates the audio
mix, the S5a oxygen and arms, the swim model and the S6 proxies. S7a changes only the **picture policy**: a machine input
`ViewInputs.Picture` (`ViewPicture.Site` = 0, the default; `ViewPicture.Waterline` = 1, S2's policy and the fallback)
selects where Subnautica's picture shows. Under `Site` a **picture latch** (machine state `Picture`) turns on in Approach
once Subnautica is ready, holds through Underwater and Surfacing (the waterline no longer swaps pictures) and turns off
in Space or on a site, peer or re-streaming loss: the only swap left is at the Approach band. Exact rules: ARCHITECTURE
§4.2 "Picture policy (S7a)". Under `Waterline` everything is "C5/C7 semantics for S2" above, unchanged.

| C5 `viewState` (`approachMode`), policy `Site` | Picture | Request (design F) | A′ | `perfMode`, `targetFps` | Subnautica recipe, C7 `snPhase` | SN camera | `snBusTarget` |
|---|---|---|---|---|---|---|---|
| Space 0 (none 0) | SE | margins off, scene drawn (after a band exit with the latch on: the scene drawn first, margins off one request later, once that frame is presented) | raised (after a band exit: once a drawn frame is presented) | Idle 0, 10 | frozen, cameras off, BelowNormal; ReadyIdle 4 | parked +60 m under the SE camera | 0 |
| Approach 1 (Track 1 or Prime 2), latch off (Subnautica warming or re-streaming, or still above the band enter — +1,000 m default — after a predicted-arrival entry) | SE | margins on, scene drawn, alpha fill | raised | Full 2, 0 | Full; WarmingUp 5 until two frames rendered, then Active 6 | true pose | 0 |
| Approach 1 (Track 1 or Prime 2), latch on | **Subnautica** | margins on, scene skipped | lowered (raised while a menu is open, as S2) | Full 2, 0 (30 while SE is not foreground) | Full; Active 6 | true pose | 0 |
| Underwater 2 (none 0); entered only with the latch on | Subnautica | margins on, scene skipped | lowered | Full 2, 0 (120 when CPU-bound; 30 unfocused) | Full; Active 6 | true pose | 1.0 |
| Surfacing 3 (none 0) | Subnautica while the latch holds (the state then ends as soon as its unchanged request is acknowledged: one tick); after an immediate exit (site, peer, re-streaming) the latch is off and Surfacing is S2's | margins on; skip = latch | lowered = latch | Full 2, 0 | Full; Active 6 | true pose | 0 (crossfade back) |

- C5 flags: `seeThrough` (3) = the scene skipped (F) or SE lowered and skipped (A′), i.e. Subnautica's picture shown;
 `marginsOn` (5) as before; **bit 9 `pictureSite`** (S7a conventions `C5FlagsExt.PictureSite`) = the machine's
 effective policy is `Site` (after the seeded failure S7a.M1). `crossingTick` keeps its S2 meaning (the waterline: it
 still gates the Underwater state). The Track recipe is never requested under `Site`
 (`freeze = Idle ∨ Paused ∨ SE stalled`) is unchanged, so Subnautica runs from Approach entry.
- Subnautica (S7a rules in the SN plugin): camera parking `CameraParkRules.Park(c5Valid, perfMode)` = C5 valid ∧
 `perfMode ≠ Full` (under `Waterline` exactly S2's Space and Track; under `Site` Space only: never while Subnautica's
 picture can be shown; seeded failure S7a.M2 parks by view as S2 did); the 30 fps unfocused rule of
 `PerfRecipeRules.Select` fires on `¬seForeground ∧ seeThrough` (S2: `∧ viewState = Underwater`, the same set under
 `Waterline`); `PerfRecipeRules.ExpectedPerfMode(view, approach, pictureSite)` is Idle in Space and Full elsewhere
 under `Site`; `WindowStyleRules.SeShowsSubnautica` / `SnWindow.HideAllowed` and `PhaseRules` are unchanged. C7 `areaBuilt` above the world: under `Site` (C5 bit 9)
 the ±16 m camera box asked of `LargeWorldStreamer.IsRangeActiveAndBuilt(Bounds)` is centred at the camera's (x, z)
 and `min(y, +140 m)`, so a camera at the band (+1,000 m default) asks for the world's top layer under it — S7a-1b: that box is never reported built far below the camera (the clip level builds near the camera only), so **above the near field (y > +140 m + 16 m) under `Site`, C7 `areaBuilt` = true** (air around the camera, nothing to build; `S7aConventions.AirAboveTheNearField`; below, the camera box decides; the C7 `streamingSettled` bit stays its own flag (the highest terrain, Mountain
 Island, is +155 m; the octree rows end at +160 m) rather than for empty air above the octree, whose answer
 (`clipmapStreamer.IsRangeActiveAndBuilt` and `cellManager.AreCellsLoaded` outside the batch rows) is HYPOTHESIS; the
 raw box is still logged (`areaBuiltRaw` on the s1 rows). `areaBuiltSeTick`/`areaBuiltPos` keep their meaning.
- SE: the crossing fade (`CrossingFlash`, 260 ms) follows the machine's picture edge (`ViewOutputs.PictureEdge`: +1 SE →
 Subnautica, −1 back) under both policies; a band
 swap uses a sky tint. The see-through dev toggle (Menu key) clears itself on the latch's off edge (`Site`) or on
 leaving Underwater (`Waterline`). The z-order auto re-assert runs while margins are requested with the scene drawn
 (S2: Prime only). Seeded failure S7a.M3: no fade on a picture edge.
- Entity set (S5 hook, SE `EntitySet` and the Subnautica culling): while C1 `camPos.y` > 0 under `Site`, enter radius
 `MirrorEnterAboveWaterM` 1,500 m and leave radius `MirrorLeaveAboveWaterM` 1,600 m — the leave radius at any altitude since review F10 — (at +1,000 m a ship on the sea below
 is 300 m away; Subnautica's above-water view reaches kilometres), else S5's 250/300 m; Subnautica culls mirrored
 chunks at the same radius above water and at the biome's water visibility (fallback 200 m) under water. S5's "kept 60
 s after leaving" rule is unchanged.

**S7a conventions.** Layout decision for S7a:
**layout 1.0 suffices, no re-pin.** S7a adds meaning to fields and bits layout 1.0 carries but does not pin, as S5 and
S6 did: GridAdd `flags` bit 3 (`Ghost`), the C2 entity `flags` bits 2–3 (ghost row only), C5 `flags` bit 9, C6 (first
written by S7a) `flags` bit 2 and its pad bytes 0x3C–0x3F, and the C1 pad bytes 0xA0–0xAF. Free-bit evidence: GridAdd flags use bits 0–2
(`MirrorGridFlags` Static, Subgrid, FloatingObject) and the version nibble 28–31; C2 entity flags bits 0–1
(`SeEntityFlags`); C5 flags bits 0–8 (`SeStateFlags`); C6 flags bits 0–1 (`SeTimeFlags`) and `private uint pad_3c`;
C1 `pad_94[0x6C]` of which the swim extension holds 0x94–0x9F. None of the layout's constant groups
(`Layout.ConstantGroups`: `SeEntityFlags`, `SeStateFlags`, `SeTimeFlags`, …) nor S5's `MirrorGridFlags` gains a member:
the new values are S7a's own constants, so `0x4f3a35f6fc1eaf31`, `0x832ba93ca25bc307`, `0x31dfd6f9befdef5d` and
`0xb6ee880712f8253b` stay as they are. A pre-S7a peer sees pad bytes and unknown bits it ignores (it would draw the ghost
as an opaque grid; both plugins ship the same `DeepEngineers.Wire.dll`). Code: Wire `S7aConventions` (constants,
`CanonicalText`, `Hash`), `S7aLoco` (C1 codec), `S7aSky` and `SunMath` (C6 codec, the sun mapping), `S7aGhost`
(ghost records); Python twins `tests/s7a/analyze_s7a.py --selftest` (text, hash, codecs) and `tools/worldmap/
worldmap_ref.py s7asun` (the sun mapping; its vectors are read by the C# `S7aSunVectorTests`).

*Canonical text* (exact; ASCII, `\n` after every line, the last included; `const` lines sorted by group (ordinal), then
value, then name; then the `field` lines in the listed order; then the `format` lines sorted by key), FNV-1a 64
**`0x4257c11c17ff772a`**:
```
ext s7a picture 3
const C1Loco Version 1
const C1Loco Size 16
const C1Loco Offset 160
const C1LocoFlags Grounded 1
const C1LocoFlags Crouching 2
const C1LocoFlags Jetpack 4
const C1LocoFlags InWater 8
const C1LocoFlags Seated 16
const C1LocoFlags Dead 32
const C1LocoFlags Ladder 64
const C1LocoFlags ToolHeld 128
const C1LocoState None 0
const C1LocoState Idle 1
const C1LocoState Walk 2
const C1LocoState Run 3
const C1LocoState Sprint 4
const C1LocoState CrouchIdle 5
const C1LocoState CrouchWalk 6
const C1LocoState Jump 7
const C1LocoState Fall 8
const C1LocoState Fly 9
const C1LocoState Ladder 10
const C1LocoState Seated 11
const C1LocoState Dead 12
const C1LocoState Swim 13
const C2FlagsExt GhostShown 4
const C2FlagsExt GhostAllowed 8
const C5FlagsExt PictureSite 512
const C6FlagsExt SkyPoleValid 4
const C6Sky Size 4
const C6Sky Offset 60
const Ghost PartKey 1
const Ghost MaxParts 32
const Ghost EntityId 91274063699247105
const GridFlagsExt Ghost 8
const Limits SunHoldMaxCdeg 200
const Limits PictureDwellMs 250
const Limits FallAnimMs 450
const Limits GroundAnimScalePermille 1000
const Limits MirrorEnterAboveWaterM 1500
const Limits MirrorLeaveAboveWaterM 1600
const ViewPicture Site 0
const ViewPicture Waterline 1
field loco version 0xa0 1 u8
field loco state 0xa1 1 u8
field loco flags 0xa2 1 u8
field loco pad 0xa3 1 u8
field loco vForwardCm 0xa4 2 i16
field loco vStrafeCm 0xa6 2 i16
field loco vUpCm 0xa8 2 i16
field loco yawRateCdeg 0xaa 2 i16
field loco seMovement 0xac 2 u16
field loco pad2 0xae 2 u16
field sky poleAzCdeg 0x3c 2 i16
field sky poleElCdeg 0x3e 2 i16
format ghost C2 kind 1 pose = gizmo SpaceDefault.m_worldMatrixAdd; PartAdd key 1 class 3 NoShadows local = translation(ModelOffset) model = MyCubeBlockDefinition.Model|lod0
format pole q = M_sn x W_sn, M = normalize(U-axis*(axis.U)), W = normalize(axis x U); az = atan2(q.x,q.z), el = asin(q.y), centi-degrees rounded half away from zero
format sky c = frac(H/2pi+0.5); n = normalize(up-q*(up.q)); r = -(q x n); SunDirection = atan2(-r.x,-r.z); NorthPoleOffset = atan2(-n.x*cos(SunDirection)+n.z*sin(SunDirection),n.y)
format time DayNightCycle paused; timePassedAsDouble += frac(sTarget-sNow)*1200 unless behind by <= SunHoldMaxCdeg of cycle angle (hold)
```
Pinned by `S7aConventionsTests` (text byte for byte, hash, line and byte counts, every value equal to the code's
constants, every new bit outside the layout's and S5's constant groups, the loco bytes inside `pad_94` after the swim
extension, the sky bytes on `pad_3c`) and by the Python twin. Changing a line bumps `ext s7a picture <n>`, updates both
`GhostEntityId` = `0x0144454700000001` = `MyEntityIdentifier.ConstructId(ENTITY
0x44454700000001)` ("DEG"; the terrain tiles are "DET", §3), checked free with `MyEntityIdentifier.ExistsById` at bind
(else the ghost is off, `DE.S7a ghost OFF id taken`).

*Placement ghost.*
- Source: `MyCubeBuilder.Static` (public static field, set in the session component's `LoadData`); `BlockCreationIsActivated`
 (public bool property, private setter: true from `Activate(MyDefinitionId? blockDefinitionId = null, bool
 groupwideDLCCheck = false)` until `DeactivateBlockCreation` / `Deactivate`); `CurrentBlockDefinition` (public
 override `MyCubeBlockDefinition` property, from `MyCubeBuilderState.CurrentBlockDefinition`); `DynamicMode` (public
 bool property, logs only); the gizmo `protected MyCubeBuilderGizmo m_gizmo` (the only reflection: one cached
 `FieldInfo`; missing → `DE.S7a ghost OFF missing MyCubeBuilder.m_gizmo` once, the ghost alone disabled);
 `MyCubeBuilderGizmo.SpaceDefault` (public property, `m_spaces[0]`) of the public nested class
 `MyCubeBuilderGizmo.MyGizmoSpaceProperties`, public fields `bool Enabled`, `MatrixD m_worldMatrixAdd`, `Matrix
 m_localMatrixAdd`, `bool m_buildAllowed`, `bool m_showGizmoCube`, `MyCubeBlockDefinition m_blockDefinition`,
 `Vector3I m_min`/`m_max`, `Vector3I? m_startBuild`/`m_continueBuild`, `float m_animationProgress`; block definition
 public fields `Model` (string, `MyPhysicalModelDefinition`), `ModelOffset` (Vector3), `CubeSize` (`MyCubeSize`),
 `Size` (Vector3I).
- The gizmo matrix: with a target grid, `UpdateGizmo_Grid` sets `m_worldMatrixAdd` = `m_localMatrixAdd ×
 CurrentGrid.WorldMatrix` (the block's 24-way orientation in the grid it will join) with its translation the mean of the
 block's cells (`m_positions[i] · GridSize` through the grid's world matrix): the block **centre**, so the block spans
 ±`Size · GridSize`/2 cells around it (small-on-large: `GetCubeSize(def.CubeSize)`); without a grid (voxel or dynamic
 placement) it is the free block frame (`CaluclateDynamicModePos` sets its translation). In every mode SE draws the
 model at rotation(`m_worldMatrixAdd`) and translation(`m_worldMatrixAdd`) + `TransformNormal(def.ModelOffset,
 m_worldMatrixAdd)` (× `GridScale`, always 1 for mirrored grids; `MyCubeBuilder.Draw`, `UpdateGizmo_Grid`,
 `UpdateGizmo_VoxelMap`, `UpdateGizmo_DynamicMode`). While SE animates a rotation (`m_animationProgress` < 1, a slerp
 from `m_animationLastMatrix`), `m_worldMatrixAdd` already holds the target: the ghost snaps (accepted). The gizmo is
 refreshed in `MyCubeBuilder.Draw` (`UpdateGizmos(…, draw: true)`, update thread); the ghost reads it in the S5
 producer's tick, so the pose is at most one tick old (HYPOTHESIS on the order, measured by S7a.N3). Refinement: the C2 writer re-reads `m_worldMatrixAdd`, `m_showGizmoCube` and `m_buildAllowed` through the
 same cached member when it writes the ghost's row in the `PrepareForDraw` postfix, so the row carries the gizmo of the
 frame SE draws; the tick's read decides activity and the definition. The row field `mode` (logs only) is `dynamic`
 when `DynamicMode`, `grid` when `SpaceDefault.m_removeBlock` (public `MySlimBlock`) is set (the gizmo targets a
 block), else `voxel` (`CurrentGrid` is `protected internal`; no second reflected member).
- Active = `Static` ≠ null ∧ `BlockCreationIsActivated` ∧ the definition (`m_blockDefinition ?? CurrentBlockDefinition`)
 non-null ∧ `SpaceDefault.Enabled` ∧ the ghost switch on ∧ the S5 stream up (after the hello ack, C5 zone CORE, TILE
 VOID or OUTER VOID). Records while active: `GridAdd` {`GhostEntityId`, `targetClass` 1 for `MyCubeSize.Large`, 2 for
 Small, `flags` = Version1 | `Ghost` (8), parent 0, `worldEpoch`} once per activation and again when the cube size
 changes; `PartAdd` {`GhostEntityId`, `partKey` 1, `modelKey` = `MirrorConventions.ModelKey(def.Model, 0)` (with
 `|scale=` as S3 when `GetCubeSize/GetCubeSizeOriginal` ≠ 1), `local` = the rows of
 `Matrix.CreateTranslation(def.ModelOffset)`, `colorMaskHSV` 0, `uvOffset` 0, `skinId` 0, `flags` = Version1 |
 FatBlock (3) | NoShadows} whenever the definition changes (same key: a replace). A definition with an empty `Model`:
 its cube parts from `MyCubeGrid.GetCubeParts(MyCubeBlockDefinition, Vector3I, Matrix, float, List<string>,
 List<MatrixD>, List<Vector3>, List<Vector4UByte>, bool)` (public static) at `(def, Vector3I.Zero, Matrix.Identity,
 gridSize, …, topologyCheck: true)`: part keys 1…n (n ≤ `Ghost.MaxParts` 32, class 1 CubePart | NoShadows, `local` =
 the returned matrices, model key `ModelKey(<asset>, 0)` with the same `|scale=` rule), PartRemove of keys no longer
 used. The model reaches Subnautica through C3 like every S5 model (first sight:
 the loader queue; the ghost shows once its model has ended). C2: one entity row every tick while active, written first
 and never dropped: `kind` 1, `flags` = `ghostShown` (bit 2) when `m_showGizmoCube` | `ghostAllowed` (bit 3) when
 `m_buildAllowed`, `groupId` 0, `(o, q, s)` = the block frame `m_worldMatrixAdd` mapped as a root grid's pose, `k` 1,
 `v` = `ω` = 0, `partsVersion` = the ghost's part serial, `skinVersion` 0. `EntityRemove` when it stops being active; a stream restart resends it from its GridAdd. Lanes: GridAdd
 and EntityRemove lane 1, PartAdd lane 2 (`MirrorBackPressure`). The ghost is never an entry of the S5 mirrored-grid
 set (`GridMirrorProducer.Grids`), so S6 sends no `SkinSet` for it and Subnautica builds no proxy (VERIFIED:
 `Combat.CollisionSkin.Tick` iterates that set; `ProxySections` are fed only by `SkinSet`), and S5 writes no `s5.grid`
 or `s5.edit` row for it; the SE consumer audit counts it as one sent entity while it is alive (Subnautica's
 `mirroredEntityCount` includes it). Only the default gizmo space is mirrored (symmetry spaces and multi-block drags are
 out of v1). Seeded failure S7a.M4 (SE): `ghostAllowed` always set.
- Subnautica (`GhostView`): an entity whose GridAdd has `Ghost` is drawn like any mirrored grid (chunks, §2.8 posing)
 but every renderer of it gets the ghost material, textures ignored: green `(0.25, 1.0, 0.35, 0.40)` while the newest
 C2 row has `ghostAllowed`, red `(1.0, 0.25, 0.20, 0.40)` otherwise (two shared materials, swapped on a change),
 renderers enabled only while `ghostShown`, `shadowCastingMode` Off, `receiveShadows` false, no collider, no
 `SkyApplier` change. Material source, first available and `shader.isSupported`: the S5 vanilla glass clone
 (`MarmosetMaterials`' glass, tinted through `_Color`), `Shader.Find("Standard")` in Fade mode (`_Mode` 2,
 `_SrcBlend` SrcAlpha, `_DstBlend` OneMinusSrcAlpha, `_ZWrite` 0, keyword `_ALPHABLEND_ON`, `renderQueue` 3000),
 `Shader.Find("Sprites/Default")`; logged `DE.S7a ghost material=<glass|standard|sprite>`; `de.s7 {ghostMaterial}`
 `GhostView.Current.MaterialSource`, `RowNow` for `{action: "ghost"}`). Two-sided through
 `_Cull` 0 where the shader has it (the glass clone) and Sprites/Default (Cull Off); Standard has no `_Cull` and stays
 single-sided. The ghost renderers take its material in the frame their cell is uploaded (no frame with the opaque
 Marmoset material) and again every frame after the consumer's material steps; the colour and visibility are also
 applied at each camera sample from the newest C2 row. Subnautica's S5 rows (`s5.grid`, `s5.placed`, `s5.edit`) and
 `tests/s5/analyze_s5.py` skip the ghost (flag bit 3 or the id).

*C6 written by SE (`SunSync`, every tick in the `PrepareForDraw` postfix, camera final; members VERIFIED).* Inputs:
`s` = `MySector.SunProperties.SunDirectionNormalized` (`MySector.SunProperties` is a public static `MySunProperties`
struct field; `SunDirectionNormalized` a public `Vector3` field: the direction **to** the sun, written by
`MySectorWeatherComponent` every update while `EnableSunRotation`, `Vector3.Transform(m_baseSunDirection,
Matrix.CreateFromAxisAngle(m_sunRotationAxis, 2π·ElapsedGameTime.TotalSeconds/m_speed))` with `m_speed` = 60 ×
`SunRotationIntervalMinutes`, `CalculateSunDirection`), renormalised in doubles; `a` =
`MySector.SunProperties.SunRotationAxis` (public `Vector3` property: normalize((B × Up) × B), with Left instead of Up
when |B·Up| > 0.95); `U` = normalize(P − C) at the camera position P; `MySession.Static.Settings.EnableSunRotation`
(bool), `SunRotationIntervalMinutes` (float, default 120), `MySession.Static.ElapsedGameTime` (TimeSpan property), the
component `MySession.Static.GetComponent<MySectorWeatherComponent>` with `RotationInterval` (public float property =
`m_speed`). The bind checks are S1's (`SiteBinding`: |axis·U0| < 1e-3, |base·axis| < 1e-3, interval 20, rotation on).
Fields:
- `hourAngleRad` H = atan2(s·W, s·M) with the meridian `M` = normalize(U − a(a·U)) and west `W` = normalize(a × U)
 (VRage cross): 0 at local noon, + towards sunset; on this site the sun rises at SN +x and sets at SN −x. `sunElevationRad`
 = asin(s·U); `sunDirSn` = Wire `SiteMap.DirSeToSn(P, s)` (f32); `dayLengthSec` = `RotationInterval` (1,200 on DE
 worlds); `simSpeed` = C1's; `paused` = `MySandboxGame.IsPaused`; `sunriseHourAngleRad` = acos(clamp(−tan δ·tan λ, −1,
 1)) with sin δ = s·a and sin λ = U·a (π/2 when δ = 0, SE's own construction).
- `flags` bit 0 `timeSyncValid` = the bind checks passed ∧ rotation on ∧ interval > 0 ∧ the sync switch on (`de.s7 time
 sync`, env `DEEPENGINEERS_TIMESYNC`, default on); bit 1 `skyOnly` = the sun is readable but a bind check failed
 (`DE.Time REFUSE …`): Subnautica drives the same mapping without the S7a.N5 claim; **bit 2 `skyPoleValid`** (S7a) = the
 pad holds this tick's pole (|U·a| ≤ 0.999; else the meridian is undefined and the last pole stays).
- **Pad 0x3C–0x3F (S7a)**: i16 `poleAzCdeg`, i16 `poleElCdeg` = the sun's rotation pole in SN axes at the camera, `q` =
 `M_sn × W_sn` (component cross product of the two vectors mapped with `DirSeToSn`), az = atan2(q.x, q.z), el =
 asin(q.y), centi-degrees rounded half away from zero (host-neutral, like `sunDirSn`; the 0.01° step moves the sun by
 ≤ 0.02°).
- `jumpSeq` +1 when H moves by more than 1° beyond the expected 2π·Δt/`dayLengthSec` in one tick (world load, `de.s7
 time`, a camera teleport across the site), logged `DE.Time jump`.
- Dev phase override `de.s7 time <c>` (dev builds; HYPOTHESIS path): every tick after the weather component's update,
 `MySector.SunProperties.SunDirectionNormalized` is turned about `a` so that the camera's cycle time
 `c = frac(H/2π + 0.5)` equals the request (a runtime field, never saved: the component saves its own
 `m_sunDirectionNormalized`); `de.s7 time off` clears it. If SE's renderer ignores the field (SE's sun does not move in
 a capture) the reply says so and S7a.N5 runs on the natural clock.

*Time drive (Subnautica `DayNightApply`, every frame while C6 is fresh (≤ 3 s), `skyPoleValid` and bit 0 or bit 1 are
set; members VERIFIED).* `DayNightCycle.main` (public static): `Pause` whenever `dayNightSpeed` ≠ 0 (re-asserted every
frame; the console's `day`/`night`/`daynight` and the end of skip-time set the speed back to 1: logged as external);
`SetDayNightTime(float)` is never used (it always adds a whole day, `timePassedAsDouble += 1200 − tp % 1200 + s·1200`).
- Target cycle time `c = frac(H/2π + 0.5)`: Subnautica's `GetDayNightCycleTime` scale (0.25 sunrise, 0.5 noon, 0.75
 sunset), which `uSkyManager.Update` turns into `Timeline = c·24` and `InitSunAndMoon` into the sun disk
 `SunDir = −(Euler(0, SunDirection, NorthPoleOffset) · Euler(15°·Timeline − 90°, 0, 0) · forward)`. Its day scalar
 `s_t` inverts `GetDayNightCycleTime` with the live public fields `sunRiseTime`/`sunSetTime` (0.125/0.875 by
 default): day (c ∈ [0.25, 0.75]): `s = rise + (c − 0.25)·2·(set − rise)`; night: `c' = c < 0.25 ? c + 1: c`,
 `s = frac(set + (c' − 0.75)·2·(1 − (set − rise)))`.
- Forward only: with `c_now = GetDayNightCycleTime`, when `frac(c_now − c)·360°` ≤ `SunHoldMaxCdeg`/100 = 2°
 Subnautica is slightly ahead: **hold** (nothing written; happens only when the camera moves west faster than the sun,
 ≥ 314 m/s on this planet, or jumps west); else `timePassedAsDouble += frac(s_t − GetDayScalar)·1200` (public double
 field). The first apply after a bind, a new `jumpSeq` or a hold beyond 2° is a single forward jump of less than one
 day, logged `DE.Time jump`. Nothing in Subnautica but base power reads `DayNightCycle.deltaTime` (`BaseBioReactor`,
 `BaseNuclearReactor`, `Charger`, `FiltrationMachine`, `SolarPanel`, `ThermalPlant`, `ToggleLights`; VERIFIED grep),
 all unused by the bridge. Seeded failure S7a.M6 (SN): `timePassedAsDouble` set straight to the target (backward
 allowed).
- Sky angles (every frame): `q` from the pole bytes, `n` = normalize(up − q(up·q)) (the noon point), `r` = −(q × n) (the
 sunrise point), `uSkyManager.main.SunDirection` = atan2(−r.x, −r.z) and `NorthPoleOffset` = atan2(−n.x·cos SD +
 n.z·sin SD, n.y) in degrees (public float fields; `uSkyManager.main` public static), NPO clamped to ±60° (the scene's
 range, `OnValidate`) with `DE.Time WARN npo` beyond. On this site SD = −90° and NPO = 0 at the anchor, |NPO| ≤ 7.45° at
 ρ∞ = 7,800 m. Exactness (S7a reference check `worldmap_ref.py s7asun`): Subnautica's sun disk then lies along SE's sun
 direction at the camera, worst 2.4e-6° over 20,000 random sites, axes and times in doubles; float32 and the pole
 quantisation add ≤ 0.02°; `IsDay` (`sunRiseTime` < scalar < `sunSetTime`) holds exactly while SE's sun is above the
 camera's horizon.
- Subnautica's `SunLight` (shadows, caustics) keeps its stylised day elevation `90° − |−65° + 130°·clamp01(2c − 0.5)|`
 (S3.C9's formula, `InitSunAndMoon`, `sunMaxAngle` 65) on the disk's azimuth plane: up to 25° higher than the disk at
 sunrise and sunset, equal at noon
- `de.s7 time sync off` (SE) clears bit 0 (and bit 1): Subnautica calls `Resume` and restores the sky angles it logged at
 its first apply. The S3 lab command `de.time` (Subnautica) is refused while the drive holds the clock.
- Check members (SN rows): `uSkyManager.main.SunDir` (public Vector3 property), `Timeline` (public float);
 `DayNightCycle.main.timePassedAsDouble` (public double), `GetDayScalar`, `GetDayNightCycleTime` (float),
 `IsDay` (bool), `dayNightSpeed` (float property); `WaterSurface.Get` (public static) and `Ocean.GetOceanLevel`
 (public static float) for the above-water picture logs.

*S7a control commands (C11, dev builds; chat `/de s7 …`).* SE `de. `0 ≤ enter < exit`; the machine takes it on its next step; the band is written on every vsm trace row (`be`, `bx`, vsm/5) and in the session start rows, and the S7a mirror radii stay pinned for the default band), `{action: "status"}` (policy and latch, band
tick, picture blocked bits, ghost state and counters, the C1 loco state, the sun: H, c, elevation, pole, checks,
`jumpSeq`, sync; it also writes one `s7a.sun` row), `{action: "loco"}` (the last C1 loco bytes), `{action: "sun"}` (the
sun part and one `s7a.sun` row; the launcher's n5 samples it every 2 s); `{picture: "site" | "waterline"}`; `{ghost:
"on" | "off" | "stop"}` (`stop` =
`MyCubeBuilder.Static.DeactivateBlockCreation`); `{ghost: "probe", block: "<TypeId>/<SubtypeId>"}` (test hook:
`MyCubeBuilder.Static.Activate(new MyDefinitionId(...))`; HYPOTHESIS that it enters build mode for the controlled
`{time: {sync: "on" | "off"}}`; `{time: <c 0..1>}`; `{time: "off"}`. Env (SE)
`DEEPENGINEERS_PICTURE=site|waterline` (default site), `DEEPENGINEERS_GHOST=on|off` (default on),
`DEEPENGINEERS_TIMESYNC=on|off` (default on). Subnautica `de.s7`: `{action: "status" | "sun" | "ghost" | "anim"}` (one
row each, at once), `{walk: "retarget" | "sebones"}`, `{ghostMaterial: "auto" | "glass" | "standard" | "sprite"}`; env
`DEEPENGINEERS_SN_WALKSOURCE`. Seeded failures through `de.mutant S7a.M1 … M6 on|off` on the side that owns them
(ROADMAP S7a).

**S7 conventions.** Scope: ROADMAP

the skinned astronaut). Layout decision for S7: **layout 1.0 suffices, no re-pin, no pin changed.** Every S7 record
kind lives in a record, table, region or message type layout 1.0 already has, or in pad bytes and unpinned region
space, as S5, S6 and S7a did:

| S7 record kind | Carrier (layout 1.0) | What S7 adds (pinned below) | Why no layout 1.1 |
|---|---|---|---|
| Interior flag | C1 `flags` bit 3 `cameraInAirtightRoom` and C5 `flags` bit 4 (layout constants, never written before S7) | the source and the hold rule (`format interior.flag`); the **C1 interior extension** in the C1 pad 0xB0–0xBF | the bits exist; 0xB0–0xBF lie in `pad_94` (0x94–0xFF), after the swim (0x94–0x9F) and loco (0xA0–0xAF) extensions |
| Room records | C4 type 7 `RoomSet` (header 16 B, `RoomBox` 24 B; never sent before S7: Subnautica counts it as ignored today) | the box meaning (`format roomset.box`), `RoomSetHeader.pad_c` = u32 `flags` (`RoomSetFlags`, version nibble) | message type and records are layout 1.0's |
| Light table | C2 lights at 0x38040, 64 × 64 B, header `lightCount` (written 0 until S7) | the row meaning (`format light.row`), `SeLight.flags` bit 3 and the version nibble 28–31, `SeLight.pad_3c` = f32 `falloff` | table, stride and count are layout 1.0's |
| Effect events | C2 region bytes **0x40000–0x4FFFF** (1,024 rows × 64 B) and the window header in `SeEntitiesHeader.pad_1c` (0x1C–0x3F) | the event row, the 8-tick window, caps, priority, sustain rule (`format event.*`) | the C2 tables end at `SeEntitiesLimits.End` = 0x39040 inside a 0x80000 region; the C2 seqlock already covers the whole region. A C4 message type, a new ring or a new region would change the layout text (`message`, `ring`, `region` lines) and need O16 first |
| Static primitives | C12 `BatchStatics` / `BatchClear` (region 0xC0, 4 MiB byte ring, ring index 3; no producer before S7) | `BatchStaticsHeader.pad_6` = u16 `version` 1, `StaticPrimitive.flags` (`StaticFlags`), the geometry (`format statics.*`), epochs | messages and `StaticKind` values are layout 1.0's |
| Bone records | C1 bones (≤ 160 × 48 B), C3 `SkinnedBind`, C4 `CharacterAdd.boneCount` | `CharacterFlagsExt.ExtraBones` (CharacterAdd `flags` bit 2): `boneCount` = `Model.Bones.Length` (81), rows 0–77 `canon1`, rows 78–80 the model's other bones (`format bones.order`) | C1 already carries up to 160 bones (8 KiB region) |

Free-space evidence: SE writes C1 as a whole 0x100-byte head from
one struct whose pad bytes stay 0 unless an extension fills them, so an older SE writes 0 at 0xB0 (version 0 = no
extension); `Link.WriteSeEntities` sets the C2 header fields one by one (never `pad_1c`) and writes nothing at or past
0x39040; Subnautica's C2 reader copies the header, the entities and the subpart rows only; `SeLight.flags` uses bits 0–2
(`SeLightFlags`); nothing writes `RoomSetHeader.pad_c`, `BatchStaticsHeader.pad_6` or `StaticPrimitive.flags`;
`CharacterAdd.flags` uses `MirrorCharacterFlags` Player 1, Fallback 2 and the version nibble 28–31. No constant group
of the layout (`Layout.ConstantGroups`), of S5, S6 or S7a gains a member: S7's values are S7's own constants. So
`0x4f3a35f6fc1eaf31` (layout 1.0, `Fixtures/layout-1.0.txt`, `tests/wire/layout_py.py`), `0x832ba93ca25bc307` (swim),
`0x31dfd6f9befdef5d` (S5), `0xb6ee880712f8253b` (S6) and `0x4257c11c17ff772a` (S7a) stay as they are. A stale C2
extension (written by an earlier S7 SE generation of the same section) is rejected by `tickLow` ≠ the low 32 bits of
the header's `tick`. Code: Wire `S7Conventions` (constants, `CanonicalText`, `Hash`, `CanonicalLines`,
`CanonicalBytes`, the shared rules), `S7Interior` (C1 interior codec, RoomSet flags and boxes, chunk class), `S7Lights`
(row codec, order), `S7Events` (row codec, window, caps, priority, sustain), `S7Statics` (C12 codec, geometry, host
name), `S7Bones` (row order); Python twins `tests/s7/analyze_s7.py --selftest` (text, hash, counts, codecs) and
`tools/worldmap/s7_ref.py` (the statics geometry; its vectors `tests/s7/vectors/s7_statics.tsv` are read by the C#
`S7StaticsVectorTests`).

*Canonical text* (exact; ASCII, `\n` after every line, the last included; `const` lines sorted by group (ordinal), then
value, then name; then the `field` lines in the listed order; then the `format` lines sorted by key), FNV-1a 64
**`0x1f9b9b4a876a97f9`** (143 lines, 6,583 bytes; `ext s7 look 1`):
```
ext s7 look 1
const BoneLimits AstronautBones 81
const BoneLimits MaxBones 160
const C12Ext Version 1
const C1Interior Version 1
const C1Interior Size 16
const C1Interior Offset 176
const C1InteriorFlags InRoom 1
const C1InteriorFlags Pressurization 2
const C1InteriorFlags Processing 4
const C1InteriorFlags Breached 8
const C1InteriorFlags CharacterInRoom 16
const C2Events WindowTicks 8
const C2Events RowSize 64
const C2Events MaxPerTick 128
const C2Events MaxRows 1024
const C2Events Offset 262144
const C2Ext Version 1
const C2Ext Offset 28
const C2Ext Size 36
const C2ExtFlags Lights 1
const C2ExtFlags Events 2
const C2LightFlagsExt Character 8
const C2LightFlagsExt Version1 268435456
const C2LightFlagsExt VersionMask 4026531840
const CharacterFlagsExt ExtraBones 4
const EventCaps Drill 8
const EventCaps Explosion 8
const EventCaps Grinder 8
const EventCaps HullBreak 8
const EventCaps PrawnDrill 8
const EventCaps Repair 8
const EventCaps Welder 8
const EventCaps GridImpact 16
const EventCaps Splash 16
const EventCaps WeaponMuzzle 16
const EventCaps BulletStreak 32
const EventCaps Impact 32
const EventCaps Thruster 64
const EventFlags Attached 1
const EventFlags Sustain 2
const EventFlags Stop 4
const EventFlags Underwater 8
const EventFlags Surface 16
const EventFlags Hit 32
const EventFlags Local 64
const EventLimits SustainRefreshTicks 15
const EventLimits SurfaceBandCm 50
const EventLimits SustainStepPermille 50
const EventLimits StreakMaxM 60
const EventLimits SplashMinSpeedCmS 200
const EventLimits EffectRadiusM 250
const EventLimits SustainLifetimeMs 500
const EventLimits ExplosionRadiusM 1000
const EventType Thruster 1
const EventType WeaponMuzzle 2
const EventType BulletStreak 3
const EventType Impact 4
const EventType Explosion 5
const EventType Welder 6
const EventType Grinder 7
const EventType Drill 8
const EventType Repair 9
const EventType PrawnDrill 10
const EventType GridImpact 11
const EventType HullBreak 12
const EventType Splash 13
const InteriorClass Exterior 0
const InteriorClass Boundary 1
const InteriorClass Inside 2
const LightLimits MaxShadowedSpots 2
const LightLimits RadiusM 60
const LightLimits MaxLights 64
const RoomLimits MaxBoxes 4096
const RoomSetFlags Truncated 1
const RoomSetFlags Version1 268435456
const StaticFlags Census 1
const StaticFlags AllowListed 2
const StaticFlags SkewApprox 4
const StaticsLimits BatchM 160
const StaticsLimits CreateRadiusM 300
const StaticsLimits ScanRadiusM 320
const StaticsLimits CloseRadiusM 350
const StaticsLimits MaxPerHost 1024
const StaticsLimits MinRescanMs 2000
const StaticsLimits MaxPerBatch 4096
const ThrusterKind Other 0
const ThrusterKind Ion 1
const ThrusterKind Atmospheric 2
const ThrusterKind Hydrogen 3
const ToolSource Hand 1
const ToolSource Ship 2
const WeaponKind Projectile 1
const WeaponKind Missile 2
field interior version 0xb0 1 u8
field interior flags 0xb1 1 u8
field interior roomO2 0xb2 1 u8
field interior pad 0xb3 1 u8
field interior roomIndex 0xb4 4 u32
field interior roomGridId 0xb8 8 u64
field c2ext version 0x1c 1 u8
field c2ext flags 0x1d 1 u8
field c2ext eventCount 0x1e 2 u16
field c2ext eventFirstSeq 0x20 4 u32
field c2ext eventStream 0x24 4 u32
field c2ext eventsDropped 0x28 4 u32
field c2ext lightsFound 0x2c 4 u32
field c2ext tickLow 0x30 4 u32
field c2ext pad 0x34 12 u8[12]
field light falloff 0x3c 4 f32
field event seq 0x0 4 u32
field event type 0x4 1 u8
field event flags 0x5 1 u8
field event lifetimeMs 0x6 2 u16
field event entityId 0x8 8 u64
field event key 0x10 8 u64
field event pos 0x18 12 f32[3]
field event dir 0x24 12 f32[3]
field event intensity 0x30 4 f32
field event size 0x34 4 f32
field event tickLow 0x38 4 u32
field event subtype 0x3c 2 u16
field event pad 0x3e 2 u16
field statics version 0x6 2 u16
field prim flags 0x1 1 u8
field roomset flags 0xc 4 u32
format audio.bus Subnautica event sounds only on Underwater rows while snBusTarget > 0; rows without Underwater draw lights only
format audio.muffle postfix MyEntity3DSoundEmitter.SelectEffect: LowPassNoHelmetNoOxy while seMixActive and C1 interior InRoom clear, sounds ModifiableByHelmetFilters only
format bones.order rows 0..77 canon1; rows 78..boneCount-1 the model's non-canonical bones in Model.Bones order; boneCount = Model.Bones.Length <= 160
format bones.unity Mirror * M^T * Mirror; extra rows posed flat under the Entity from their absolute matrices
format event.frame Attached: pos, dir in the SE-local frame of entityId; else SN-world (F, mapdir)
format event.key fnv1a64(utf8 thr|<fatKey x16>|<flame index>) | fnv1a64(utf8 tool|<fatKey x16>) | fnv1a64(utf8 tool|e<entity id>); one-shots 0
format event.priority over MaxPerTick: Stop rows, Explosion, HullBreak, GridImpact, Splash, Impact, WeaponMuzzle, Repair, Welder, Grinder, Drill, PrawnDrill, Thruster, BulletStreak; newest first within a type
format event.sustain a row on start, on |d intensity| >= SustainStepPermille/1000, on an attached move > 0.05 m, else every SustainRefreshTicks; lifetimeMs = SustainLifetimeMs; Stop row at the end; Subnautica fades a sustain whose lifetime ran out
format event.window rows = accepted events of ticks [tick-7, tick], seq consecutive from eventFirstSeq; eventStream changes when seq restarts; tickLow = low 32 bits of tick
format interior.class Inside: cell round(T/g) inside a room box; Boundary: Chebyshev distance 1 from a room box; else Exterior
format interior.flag GetOxygenRoomForCubeGridPosition(cell of the camera) else GetSafeOxygenBlock(camera).Room; IsAirtight; last value held while IsProcessingData
format interior.sky Inside: SetCustomSky(MarmoSkies.main.GetSky(BaseInterior)); Boundary: the same while C1 roomGridId = its entity, else SetSky(Auto) with dynamic; Exterior: SetSky(Auto)
format light.row ownerId frame as event.frame; a point row per LightOn, a Spot row per ReflectorOn; nearest first within RadiusM; cap MaxLights; Shadows on spot rows only
format roomset.box cells [min, max] of airtight room air cells, MyVoxelSegmentation Simple; Truncated: one box per room (its cell AABB)
format statics.axes staticAxes(q) = (mapdir^-1(q*x), mapdir^-1(q*y), mapdir^-1(q*(-z))), det +1; size_i *= sqrt((a_i.x^2+a_i.z^2)/s^2+a_i.y^2)
format statics.capsule size = (radius, halfSegment, axis 0|1|2); halfSegment = max(0, height/2 - radius); HkCapsuleShape(F^-1(c - a*halfSegment) - o, F^-1(c + a*halfSegment) - o, radius/s(c)); sphere radius/s(c)
format statics.host DE.Statics.<bx>.<by>.<bz>.e<epoch>[.p<k>]: MyEntity, no model, Save false, layer 13, HkStaticCompoundShape baked, origin F^-1(mean centre), identity rotation
```
Pinned by `S7ConventionsTests` (text byte for byte, hash, line and byte counts, every value equal to the code's
constants, every new bit outside the layout's, S5's, S6's and S7a's constant groups, the interior bytes inside `pad_94`
after the loco extension, the C2 extension inside `pad_1c`, the event rows inside the C2 region and past
`SeEntitiesLimits.End`) and by the Python twin. Changing a line bumps `ext s7 look <n>`, updates both pins and is
Field names in the text: `interior` = C1 0xB0–0xBF, `c2ext` = `SeEntitiesHeader` 0x1C–0x3F, `light` =
`SeLight`, `event` = the C2 event row, `statics` = `BatchStaticsHeader`, `prim` = `StaticPrimitive`, `roomset` =
`RoomSetHeader`.

*C1 interior extension (0xB0–0xBF; written with every C1 record by SE, `S7Interior.FillWire`).*

| Offset | Type | Name | Value |
|---|---|---|---|
| 0xB0 | u8 | `version` | 1 (0 = not written: an SE before S7; Subnautica then reads C1 flag bit 3 alone) |
| 0xB1 | u8 | `flags` | `C1InteriorFlags`: bit 0 `InRoom` (= C1 flags bit 3), 1 `Pressurization` (the world has `EnableOxygen` ∧ `EnableOxygenPressurization`), 2 `Processing` (the camera grid's gas system is recomputing: `InRoom` holds its last value), 3 `Breached` (the camera cell is in a room of a mirrored grid that is not airtight), 4 `CharacterInRoom` (the controlled character's eye cell is in an airtight room: the swim gate below) |
| 0xB2 | u8 | `roomO2` | the room's `OxygenLevel(GridSize)` × 255, rounded half away from zero, 0 without a room (information) |
| 0xB3 | u8 | pad | 0 |
| 0xB4 | u32 | `roomIndex` | `MyOxygenRoom.Index` of that room, 0xFFFFFFFF without one (information) |
| 0xB8 | u64 | `roomGridId` | the entity id of the grid (or subgrid) whose airtight room holds the camera; 0 = none |

- **Source (SE, every tick, update thread, `PrepareForDraw` postfix with C1).** Only when the world has both settings
 (else all zero but `version`, the flag false and `DE.Interior OFF pressurization disabled` logged once per world).
 Candidates: the S5 mirrored grids (and their subgrids) whose `PositionComp.WorldAABB` contains the camera, nearest
 first. Per candidate `g`: `cell = g.WorldToGridInteger(camera)`; `gs = g.GridSystems.GasSystem` (null: next); while
 `((IMyGridGasSystem)gs).IsProcessingData` the grid keeps its last answer (`Processing` set); else `room =
 gs.GetOxygenRoomForCubeGridPosition(ref cell)`, and when that is null (the camera inside a block, e.g. a cockpit)
 `gs.GetSafeOxygenBlock(camera).Room`; `InRoom` = `room != null ∧ room.IsAirtight`; `Breached` = `room != null ∧
 ¬room.IsAirtight`. The first candidate with `InRoom` wins (`roomGridId`, `roomIndex`, `roomO2`). Never from
 `GetRooms` per tick (it returns false while processing and rebuilds every room's cell set per call; S7.M10). The same
 probe at the controlled character's eye point (the one the S2b swim controller uses) gives `CharacterInRoom`, per
 character id.
- **C5.** SE writes C5 flags bit 4 equal to C1 flags bit 3 of the same tick (for the audio rule; Subnautica reads C1).
** The S2b swim latch does not turn on, and turns off with the
 exit reason `room`, while `CharacterInRoom` holds for the controlled character (keyed by its entity id); the swim
 model's oxygen rule therefore does not run in a dry room. Whether the Water Mod 3.23 itself treats airtight rooms
 as dry (buoyancy, drag, drowning) is HYPOTHESIS: S7.N1c.

*C4 RoomSet (S7).* `entityId` = a mirrored grid or subgrid; `flags` (`pad_c`) = `RoomSetFlags.Version1` (bits 28–31 =
1; any other nibble: ignored and counted) | `Truncated` (bit 0); `count` boxes {i32 min[3], i32 max[3]} = inclusive
cell ranges in the grid's cell coordinates (`MySlimBlock.Min` convention; cell c spans `c·GridSize ± GridSize/2`
grid-local) covering exactly the air cells of the grid's **airtight** rooms: `MyGridGasSystem.GetRooms(List<
IMyOxygenRoom>)` → each `MyOxygenRoom` with `IsAirtight` → its `Blocks` (`HashSet<Vector3I>`) → `MyVoxelSegmentation`
(`ClearInput`, `AddInput(Vector3I)`, `FindSegments(MyVoxelSegmentationType.Simple, 0)`, as S6; fallback Wire
`CollisionSkinMath.MergeCellsGreedy`, logged once). More than `RoomLimits.MaxBoxes` (4,096) boxes: one box per room
(its cell AABB) with `Truncated`. `count` 0 = the grid has no airtight room. Sent after the grid's first `GridAdd`,
then only when the box set changes (content hash), evaluated after the grid's gas system finished a recompute: a
handler added with `+=` to its `OnProcessingDataComplete` (a multicast `Action` property that no game code assigns;
removed with `-=` when the grid leaves the set; it runs on the main thread from `OnBackgroundTaskFinished`), with a
10 s content-hash re-check as the safety net, and on a stream restart; lane Edit (S5 `MirrorBackPressure`), ≤ 0.5 ms
per tick, resumable. Subnautica keeps the newest set per entity, drops it with the entity (`EntityRemove`, `ClearAll`).

*Interior chunks, sky, inside-walkable and clip proxies (Subnautica, `S7Interior`).*
- **Chunk class** (`format interior.class`; `InteriorClass`): every chunked part gets a class from its `local`
 translation `T` (cell `round(T/g)`, g = the grid size of `targetClass`) against its entity's room boxes: `Inside`
 (the cell is in a box), `Boundary` (Chebyshev distance 1 from a box: walls, floors, windows), else `Exterior`. The
 class joins the S5 chunk key (Subnautica-internal), so the three never share a mesh; a new RoomSet re-classifies the
 entity's parts and dirties only the cells whose class changed (S5 rebuild rules).
- **Sky** (`format interior.sky`): `Inside` chunks `SkyApplier.SetCustomSky(MarmoSkies.main.GetSky(Skies.BaseInterior))`;
 `Boundary` chunks the same while C1's interior extension says `InRoom` with `roomGridId` = that chunk's entity (or its
 root), otherwise `SetSky(Skies.Auto)` and `dynamic = true` (the biome sky, the S5 default); `Exterior` chunks keep
 S5's biome sky. Never `SetSky(Skies.BaseInterior)` alone: without a `SubRoot` or `MarmoLifepodSky` parent it finds no
 environment and falls back to the biome sky (VERIFIED `SkyApplier.GetSkyForEnvironment`; S7.M5). The sky is
 re-applied after every rebuild of the chunk (the S5 material step).
- **Inside-walkable.** A Harmony postfix on `Player.IsInsideWalkable` sets the result true for `Player.main` (the
 ghost) while the newest C1 (≤ 100 ms old, the bridge driving the camera) has `InRoom`. It turns on the game's
 inside behaviour exactly as in a Cyclops: `WaterscapeVolumeOnCamera` passes `cameraInside` to the fog
 (`_CameraInside`), `Player.CanBeAttacked` is false, `FMODGameParams` interior events switch, and the other readers
 of S7.N2 follow. Whether the fog then spares the interior while the water seen through a window stays fogged is
 HYPOTHESIS (shader behaviour; S7.N1 measures it).
- **Clip proxies.** For a mirrored entity whose room box, mapped to SN, straddles the sea surface (box min y < 0 <
 box max y, the S2 surface y = 0), one `WaterClipProxy` child per such box (`shape` Box, `immovable` false,
 `clipMaterial` = the material of the first `WaterClipProxy` found in a loaded game prefab at run time, the unit box
 scaled to the room box (+ the border the component adds)), so a surfaced hull shows no sea surface inside. Fully
 submerged or fully dry rooms get none. Missing material: none, `DE.S7 interior clip OFF no material` once.

*C2 lights (S7; `S7Lights`).* Written with every C2 snapshot (the S5 writer, same tick), `lightCount` ≤ 64, rows
nearest first. Rows (`format light.RadiusM` (60 m) of
the SE camera: a point row when `LightOn` (`pos` = `Position`, `dir` 0, `colour` = `Color` as gamma RGB 0–1, `range` =
`Range`, `intensity` = `Intensity`, `coneDeg` 0, `falloff` = `Falloff`) and a spot row when `ReflectorOn` (`dir` =
`ReflectorDirection`, `colour` = `ReflectorColor`, `range` = `ReflectorRange`, `intensity` = `ReflectorIntensity`,
`coneDeg` = `ReflectorConeDegrees`, the **full** cone, `falloff` = `ReflectorFalloff`, `Shadows` = `CastShadows`).

(`Vector3D.Transform(p, WorldMatrixNormalizedInv)`); 0 = not mirrored, then SN-world through F and mapdir. `flags`:
`Spot` 1, `Shadows` 2, `On` 4 (layout) | `Character` 8 (a character's light) | `Version1` (bits 28–31 = 1).
(the private `MyLights` pool has no spatial query): `MyLightingLogic.Lights` reached through the field `m_lightingLogic`
of `MyLightingBlock`, `MySearchlight`, `MyHeatVentBlock` and `MyLightingComponent`; `MyBeacon.m_light`; the character
light `MyRenderComponentCharacter.
off with one log line).
`OnBlockRemoved`; ≤ 0.1 ms per tick. `c2ext.lightsFound` = candidates before the cap. Subnautica (`S7Lights`): a pool
of Unity `Light`s set from the newest C2 rows every frame (point: `type` Point; spot: `type` Spot, `spotAngle` =

interpolated hull; `color` = `colour`; `range` = `range` × `LightRangeScale`; `intensity` = `intensity` ×
`LightIntensityScale` (tunables, defaults 1.0 and 1.0, HYPOTHESIS until S7.N3 calibrates them against the Seamoth cabin
light
is absolutely blinding specially at night"*: kind from the row's own flags, `characterSpot` = Character ∧ Spot,
`characterPoint`, `blockSpot`, `blockPoint`; intensity scales 0.04 / 1 / 0.125 / 1, range scales 1: SE's suit lamp
`REFLECTOR_INTENSITY` 25 and a spotlight's default reflector 8 (`MyReflectorLight`/`MySearchlight`: intensity × 8) land
at Unity 1.0, SE drawing them through its eye adaptation and Subnautica having none (HYPOTHESIS: Subnautica's own lamps
sit near 1; the helmet point 0.5 and the interior lights (intensity × 2, S7.N3's global
scale) keep SE's values; env `DEEPENGINEERS_SN_LIGHT_SCALE` = `kind=intensity[:range],…`, live `de.s7 {lightScale:
"<kind> <factor> [range]" | "status" | "reset"}` (chat `/de sn s7 lightScale suit 0.04`; aliases suit/helmet,
spotlight, interior); `s7.lights` (Subnautica) adds `kindScales`, `kindNote`, rows per kind and `suitIntensity`/
`suitRange` (the suit lamp as drawn); SE's `s7.lights` adds `suitOn`, `suitSpotIntensity`, `suitSpotRange`,
`suitPointIntensity`, `suitPointRange` (the raw values sent)); `shadows` Soft on at most `LightLimits.MaxShadowedSpots` (2) rows: the nearest spot
rows with `Shadows`, every other light `shadows` None; Unity has no falloff exponent, so `falloff` is logged and kept
for the calibration (HYPOTHESIS: unused). Lights are drawn above and under water alike (closing S7a's accepted gap). Calibration source (S7.N3): the
spawned Seamoth's `Light` components (`GetComponentsInChildren<Light>(true)`): those under `SeaMoth.toggleLights.
lightsParent` are the headlights, the others the cabin light (HYPOTHESIS, every one logged with type, intensity, range
and angle at spawn; recon read headlights 1.5 / 100 m / 50° and cabin 2.2–3.0 at 7–10 m from the prefab).

*C2 effect events (S7; `S7Events`).* **Header extension** (`pad_1c`): `version` 1; `flags` (`C2ExtFlags` `Lights` 1:
the light rows carry S7 meaning, `Events` 2: the window is written); `eventCount` (≤ 1,024); `eventFirstSeq`;
`eventStream`; `eventsDropped` (cumulative cap drops in this stream); `lightsFound`; `tickLow` = the low 32 bits of the
header's `tick` (else the whole extension is stale and ignored); 12 pad bytes 0. **Row** (64 B at C2 offset 0x40000 +
i × 64):

| Offset | Type | Name | Value |
|---|---|---|---|
| 0x00 | u32 | `seq` | event number, consecutive in the window, from 1 per stream |
| 0x04 | u8 | `type` | `EventType` (table below) |
| 0x05 | u8 | `flags` | `EventFlags`: 1 `Attached`, 2 `Sustain`, 4 `Stop`, 8 `Underwater` (the point is below the sea: SN y < 0), 16 `Surface` (\|SN y\| ≤ 0.5 m), 32 `Hit` (the tool or shot touches something), 64 `Local` (the camera's own character or seat is the source) |
| 0x06 | u16 | `lifetimeMs` | one-shot: the effect's duration; sustain: `SustainLifetimeMs` (500) |
| 0x08 | u64 | `entityId` | the mirrored grid, subgrid or character the effect is attached to; 0 = world |
| 0x10 | u64 | `key` | sustain identity (`format event.key`); one-shots 0 |
| 0x18 | f32[3] | `pos` | `Attached`: SE-local metres of `entityId`; else SN-world metres (F) |
| 0x24 | f32[3] | `dir` | unit vector in the same frame (`Attached`: SE-local; else mapdir); 0 = none |
| 0x30 | f32 | `intensity` | 0–1, per type |
| 0x34 | f32 | `size` | metres, per type |
| 0x38 | u32 | `tickLow` | low 32 bits of the SE tick that raised it |
| 0x3C | u16 | `subtype` | per type |
| 0x3E | u16 | pad | 0 |

| `type` | SE source (update thread; members VERIFIED, table below) | `pos`, `dir`, `intensity`, `size`, `subtype` | Subnautica look (under water) and sound (found at run time; fallback) |
|---|---|---|---|
| 1 Thruster (sustain, key `thr\|<fatKey>\|<i>`) | each `MyThrust` of a mirrored grid within 250 m, polled every tick: one sustain per flame of `Flames` | flame `Position`/`Direction` through the block's `PositionComp.LocalMatrixRef` (grid-local; `dir` = exhaust), `CurrentStrength`, flame `Radius`, `ThrusterKind` from `BlockDefinition.ThrusterType` (`Ion` 1, `Atmospheric` 2, `Hydrogen` 3, else 0) | a clone of `SeaMoth.bubbles` per flame, emission rate ∝ intensity × size (cavitation burst above 0.7, HYPOTHESIS look); one engine loop per entity from `SeaMoth.engineSound.engineRpmSFX` (its FMOD asset), parameter `rpm` = Σ intensity·size² / Σ size² over the entity's live thruster sustains; above water: a flame glow light per thruster kind (atmospheric none) |
| 2 WeaponMuzzle | postfix `MyGunBase.Shoot(Vector3D, Vector3, Vector3, MyEntity, uint?, bool)` | `initialPosition`, `direction`, 1, 1 m, `WeaponKind` (HighSpeed ammo 1 Projectile, Missile 2) | a pooled light flash dimmed ×0.35 under water (60 ms) and a bubble burst (`PlayerBreathBubbles.bubblesPrefab`); sound: `SeamothTorpedo.fireSound` (HYPOTHESIS feel) else none |
| 3 BulletStreak | the same postfix, HighSpeed ammo only | origin, direction, `ProjectileCount`/8 (≤ 1), length min(`MaxTrajectory`, 60 m), lifetime = length / `DesiredSpeed` (≥ 100 ms) | bubbles emitted along the ray (`ParticleSystem.Emit(Vector3, Vector3, float, float, Color32)` on a bubble clone); no sound |
| 4 Impact | postfix `MyProjectile.OnHit(IMyEntity, ref MyHitInfo, object, ref LineD)` | `hitInfo.Position`, `hitInfo.Normal`, 1, 0.5 m, 1 grid / 2 voxel / 0 other | `VFXSurfaceTypeManager.main.Play(metal (grid) or rock, impact, …)` plus a bubble burst; sound: none (SE's own muffled sound) |
| 5 Explosion | postfix `MyExplosions.AddExplosion(ref MyExplosionInfo, bool)` when it returns true, within 1,000 m | `ExplosionSphere.Center` (world), 0, min(1, `Damage`/1,000), `ExplosionSphere.Radius`, `ExplosionType` (`MyExplosionTypeEnum`) | `Crash.detonateParticlePrefab` via `Utils.PlayOneShotPS` scaled radius/5 m (Crash's own radius), else `SeamothTorpedo.explosionPrefab`; sound: the prefab's own emitter (HYPOTHESIS) else none; a light flash; damage never (the prefab carries none: `Crash.Detonate` damages in code) |
| 6 Welder (sustain, `tool\|<fatKey>`) | `MyShipWelder` (`MyShipToolBase.IsShooting`) | the block's work point (block centre + Forward × half size, HYPOTHESIS), Forward, 1, block size, `ToolSource` 2 | the repair tool's `Welder.fxControl` (`VFXController`) clone; loop `Welder.weldSound` (`FMODASRPlayer` start/stop) |
| 7 Grinder (sustain) | `MyAngleGrinder` (`MyEngineerToolBase.IsShooting`, key `tool\|e<id>`), `MyShipGrinder` (`MyShipToolBase.IsShooting`, key `tool\|<fatKey>`) | hand: `GunBase.GetMuzzleWorldPosition`; ship: as 6; `Hit` from `HasHitBlock` (hand); 1 with `Hit` else 0.25; `ToolSource` 1/2 | `VFXSurfaceTypeManager.main.Play(metal, diamondBlade, …)` at 10 Hz while `Hit` (HYPOTHESIS look); sound: the repair tool loop (HYPOTHESIS) |
| 8 Drill (sustain, `tool\|e<id>`) | `MyHandDrill.IsShooting` | `m_drillBase.Sensor.FrontPoint`, the tool's Forward, 1 drilling an object (`IsDrillingAnObject`) else 0.25, 0.5 m | `VFXSurfaceTypeManager.main.Play(rock or sand, exoDrill, …)` (the sediment cloud); sound: `ExosuitDrillArm.loop` asset (HYPOTHESIS) |
| 9 Repair (sustain, `tool\|e<id>`) | `MyWelder` (the hand welder) `IsShooting` | `GunBase.GetMuzzleWorldPosition`, Forward, 1 with `HasHitBlock` else 0.25, 0.3 m | the Subnautica repair tool's own look and sound (`Welder.fxControl`, `Welder.weldSound`) |
| 10 PrawnDrill (sustain, `tool\|<fatKey>`) | `MyShipDrill.IsShooting` | `m_drillBase.Sensor.FrontPoint`, Forward, 1 / 0.25, `Sensor` reach | the Prawn drill arm's own look and sound (`ExosuitDrillArm.drillFXprefab`, `fxControl`, `loop`/`loopHit`) |
| 11 GridImpact | postfix `MyGridPhysics.PlayCollisionParticlesInternal(IMyEntity, ref Vector3D, ref Vector3, ref Vector3, float, float, ContactPointDataTypes)` when `flags` has `Particle_Collision` or `Particle_GridCollision` | `worldPosition`, `normal`, min(1, `separatingSpeed`/20 m/s), min(4, `impulse`/1e5) m | `VFXSurfaceTypeManager.main.Play(metal, impact, …)` and a bubble burst; sound: `CollisionSound` assets of the Seamoth prefab (HYPOTHESIS that it carries one) |
| 12 HullBreak | postfix `MyGridGasSystem.AddDepressurizationEffects(MyCubeGrid, Vector3I, Vector3I)` when `from ≠ to` and the grid is not `MarkedForClose` | `grid.GridIntegerToWorld(from)` (attached: the cell centre), normalize(to − from), 1, `GridSize` | a bubble column (bubble clone, 3 s; or `VFXSubLeakPoint.leakEffectPrefabs` when a loaded prefab has them) |
| 13 Splash | SE: an entity of the S5 set whose world AABB bottom crosses SN y = 0 with \|vy\| ≥ 2 m/s (`SplashMinSpeedCmS`) | the crossing (x, 0, z) SN-world, up, min(1, \|vy\|/20 m/s), the horizontal half-diagonal | `VFXConstructing.surfaceSplashFX` of the Seamoth prefab, sound `Vehicle.splashSound`; surface only (`Surface` flag) |

- **Window and caps (SE, `format event.window`, `event.priority`).** Sources append to a fixed per-tick buffer (never
 a growing queue): at most `EventCaps[type]` new events per type per tick and `C2Events.MaxPerTick` (128) in all; over
 a cap the lower-priority events are dropped and counted per type (`eventsDropped`, `s7.fx` rows). Accepted events get
 consecutive `seq`; the window holds every accepted event of the last 8 ticks (`WindowTicks`, ≤ 1,024 rows). Sources
 run only in the site (C5 zone CORE, TILE VOID or OUTER VOID) after the hello ack, within `EffectRadiusM` (250 m) of
 the camera (explosions 1,000 m). `eventStream` = (SE generation << 16) | (S5 stream restarts & 0xFFFF): a new value
 restarts `seq` at 1.
- **Attachment.** `Attached` with SE-local coordinates when the source's top-most parent is a mirrored grid or the
 mirrored character (thrusters, ship tools, hull breaks always; shots and impacts when the shooter/target is
 mirrored); else world (SN-world through F and mapdir).
- **Sustain** (`format event.sustain`): a row on start, on a change of `intensity` ≥ 0.05, on an attached move > 0.05 m
 (a moving subpart), else every 15 ticks; a `Stop` row when it ends; Subnautica keys sustains by (`entityId`, `key`),
 updates them in place and fades out one whose `lifetimeMs` passed without a row (a lost Stop or a dropped refresh).
- **Reader (Subnautica, `S7Effects`).** In the same seqlock read as C2 (copying `lightCount` light rows and
 `eventCount` event rows), only when `c2ext.version` = 1 and `tickLow` matches; rows with `seq` > the last applied seq
 of the same `eventStream` are applied in `seq` order; a gap (`eventFirstSeq` > last + 1) adds to `eventsMissed`; a new
 `eventStream` resets the last seq to `eventFirstSeq` − 1. Attached effects are children of the entity's Entity
 transform at `Mirror(pos)` (a missing entity: dropped and counted); world effects at `pos`. Look by `flags`:
 `Underwater` rows get the under-water look and sound above; rows without it get the light only (flash, glow) and no
 sound (Subnautica's bus is 0 above water; SE plays its own sounds in air; `format audio.bus`). Effects come from pools
 filled at start from the sources found; at most 2 `Instantiate` per frame; ≤ 0.5 ms per frame.
- **Sources (Subnautica, `S7EffectLibrary`).** Found once per world load from the player's own game: prefabs through
 `CraftData.GetPrefabForTechTypeAsync(TechType)` (Seamoth 2000, Exosuit 2001, Welder 750, Crash 2509; the drill arm
 through `Exosuit.armPrefabs` for `ExosuitDrillArmModule` 2116), their component fields above, FMOD assets by their
 `FMODAsset.path` (logged), `VFXSurfaceTypeManager.main`. A prefab is used only if its hierarchy has nothing but
 Transform, ParticleSystem(Renderer), Light, `VFX*` and `FMOD_*` components and their helpers (no Collider,
 Rigidbody, `LiveMixin`, damage component; else refused, logged): effects never touch Subnautica's gameplay. Anything
 missing: that kind falls back to the bubble clone (or nothing) with `DE.S7 fx SOURCE MISSING <kind> <what>` once.
 Nothing is shipped, nothing is written to the game.
- **As built.** Code: Wire `S7Events.cs` (`S7EventRow`, `C2Ext` with
 `For`/`Write`/`TryRead`, `S7EventKeys`, `S7EventRules`), `S7EventsWindow.cs` (`S7EventWindow`: `TryAdd`, `EndTick`,
 `CopyRows`, `Extension`, `SetStream`, per-type counters; `S7SustainTracker`), `S7EventsReader.cs`
 (`IC2ExtensionReader`, `S7EventReader`); SE `EffectsProducer.cs` (`S7Effects`: `Window`, `ForC2` called by the S5 C2
 writer, `Execute` for `de.fx`), `EffectsPatches.cs` (`S7EffectPatches`); SN `EffectsPlayer.cs` (`S7Effects`),
 `EffectsLibrary.cs` (`S7EffectLibrary`, `S7FxPool`). The pinned `S7Effects.OnC2` is realised as the reader object:
 `MirrorWire.EntityTableReader.Extensions` (a list of `IC2ExtensionReader`) copies inside the C2 seqlock window and
 commits after a coherent read (MirrorWire.cs is linked Unity-free into the SN tests, so it calls no S7 class; the
 lights reader uses the same list). Refinements: the ship welder/grinder work point is `MyShipToolBase.DetectorSphere`

 size = its radius, `Hit` = the protected `m_isActivatedOnSomething` (``);
 `MyThrust.Flames` is read by reflection once per thruster and model (`MyThrustFlameAnimator.FlameInfo` lives in
 `Sandbox.RenderDirect.dll`, which the plugin does not reference; `Position`, `Direction`, `Radius` as the
 `LoadDummies` initialiser shows); a sustain also sends a row when its source flags change (Underwater, Surface, Hit);
 `de.fx {action: stress}` closes the tick, raises n one-shots and closes it again, so its reply and the `s7.fx` row
 `why: stress` carry exactly that tick's numbers (`accepted`, `dropped`, `perType`, `nextTickPending`, `oldestTicks`);
 splashes draw their surface look and, since the orchestrator's decision of play
 `Vehicle.splashSound`: the one documented exception to `format audio.bus` (a `Splash` row with `Surface` may start
 its sound without `Underwater`, still only while Subnautica's bus target is above 0; counted as `splashAllowed`); every
 event sound asks the audio rule (`AudioPolish.OnEventSound`). Rows as built (the fields
 `tests/s7/analyze_s7.py` reads): SE `s7.fx` per-row deltas {raised, sent, dropped, sustainRows} plus `sustainKeys`,
 `ticks`, `stepLagMaxTicks`, `why` periodic|drop|stress and the totals; SE `s7.fx.event` one row per accepted one-shot
 (≤ 256/s; sustains too while `de.fx {action: trace}`) {tick, seq, stream, type, flags, entity, key, pos, dir, posSn,
 intensity, size, subtype, lifetimeMs, tickLow}; SN `s7.fx` per-row deltas {applied, spawned, failed, soundsStarted,
 missed} plus totals, reader counters, `sources`, `pools`; SN `s7.fx.event` {snFrame, seTick, seq, stream, type, flags,
 entity, key, how, posSn, latencyTicks, latencyFrames, scale, lifetimeMs, source, sound}; SN `s7.fx.sustain` {snFrame,
 seTick, type, key, entity, state start|refresh|stop|fade, shown}; SN `s7.fx.thruster` (10 Hz per live thruster
 sustain) {snFrame, seTick, entity, key, intensity, size, emission, rpm, rpmRule, enginePlaying}.

*S7p effects-under ( S7p, ROADMAP "### S7p Polish pass" P2, P3, P4, P5, P6a
bubbles, it also doesn't transition the colors", "massive effects from the heat (vapor) when flying by the water",
"explosions are just a yellow flash", "the gun is just bubbles from my mask times 10", "the drill effect is blinding").
Layout 1.0, `ext s7 look 1` (`0x1f9b9b4a876a97f9`) and every earlier pin unchanged; the row stays 64 bytes and every S7
offset holds.* S7p gives the S7 row's free bits a meaning, pinned on its own:

| What | Carrier | Readers that must know |
|---|---|---|
| A thruster's flame: SE's own colour of this strength and the definition's length scale | `extra` = the S7 pad at 0x3E (`format event.flame`) | Subnautica's S7 player and the above-water route (`S7pAboveRules.ThrusterColour` decodes it); 0 = an older SE: the kind's vanilla colour, scale 1.15 |
| A shot's hit distance (the tracer ends at the hit) | `extra` (cm) + flag `Hit` on BulletStreak rows (`format event.streak`); `size`/`lifetimeMs` keep their S7 meaning (creature-damage range, above-water tracer) | the S7 player |
| The muzzle flash scale of the weapon | WeaponMuzzle `size` (was 1 m): 1.0 large grid, 0.5 small grid, 0.3 hand-held | the S7 player |
| What a drill bit touches, and where | Drill/PrawnDrill `subtype` bits 8–11 (`DrillContact`; bits 0–7 stay the `ToolSource`) and `pos` = the contact point quantised to 25 cm while `Hit` (`format event.drill`) | the S7 player; every other type keeps bits 8–15 at 0 (`format event.subtype`) |

*Canonical text* (exact; ASCII, `\n` after every line; `const` lines sorted by group, value, name; the `field` line; the
`format` lines sorted by key), FNV-1a 64 **`0xc83edaca61ce1545`** (35 lines, 2,304 bytes; `ext s7p fx 1`), pinned by Wire
`S7pEffectsTests` (text, hash, counts, every constant from the code, the field at the row's 0x3E, the S7 pin unchanged):
```
ext s7p fx 1
const DrillContact None 0
const DrillContact Rock 1
const DrillContact Sediment 2
const DrillContact Ice 3
const DrillContact Metal 4
const DrillContact Other 5
const FlameExtra ClassBits 3
const FlameExtra BBits 4
const FlameExtra GShift 4
const FlameExtra RBits 4
const FlameExtra GBits 5
const FlameExtra RShift 9
const FlameExtra ClassShift 13
const S7pLimits ContactLookupsPerTick 4
const S7pLimits StreakRayLayer 15
const S7pLimits StreakRaysPerTick 16
const S7pLimits ContactQuantumCm 25
const S7pLimits ContactRefreshTicks 30
const S7pLimits MuzzleHandCm 30
const S7pLimits MuzzleSmallCm 50
const S7pLimits MuzzleLargeCm 100
const S7pLimits LengthClassStepMilli 125
const S7pLimits LengthClassBaseMilli 750
const S7pLimits LengthScaleDefaultMilli 1150
const S7pLimits MaxThrustPermille 1500
const SubtypeBits HighShift 8
const SubtypeBits ContactMask 15
const SubtypeBits LowMask 255
field event extra 0x3e 2 u16
format event.drill Drill and PrawnDrill: subtype = ToolSource | DrillContact<<HighShift (DrilledEntity: voxel by MaterialTypeName, grid Metal, other Other; None while not drilling); pos = DrilledEntityPoint quantised to ContactQuantumCm in the row frame while Hit, else the sensor FrontPoint
format event.flame Thruster: extra = class<<ClassShift | round(r*15)<<RShift | round(g*31)<<GShift | round(b*15); class = clamp(round((FlameLengthScale*1000 - LengthClassBaseMilli) / LengthClassStepMilli), 1, 7), 0 = absent (the kind's colour, LengthScaleDefaultMilli); rgb = lerp(FlameIdleColor, FlameFullColor, clamp01(CurrentStrength / MAX_THRUST)).xyz clamped 0..1 (the definition's gamma numbers)
format event.muzzle WeaponMuzzle: size = MuzzleLargeCm/100 for a large-grid weapon, MuzzleSmallCm/100 small grid, MuzzleHandCm/100 hand-held
format event.streak BulletStreak: extra = round(distance*100) clamped 1..65535 (cm) to the first hit of MyPhysics.CastRay(origin, origin + dir*size, list, StreakRayLayer) not on the shooter, flag Hit; 0 = no hit or no ray (at most StreakRaysPerTick rays per tick on the update thread); size and lifetimeMs keep their S7 meaning
format event.subtype bits 0-7 keep the S7 meaning for every type (read subtype & LowMask); bits 8-15 are 0 except Drill and PrawnDrill (DrillContact); extra is 0 for every type but Thruster and BulletStreak
```
No Python twin: `tests/s7/analyze_s7.py` packs the pad as 0 and reads no S7p value (the S7 text it twins is unchanged).

*Cross-reference:* with S11 on, SE's Explosion rows also carry a value in
`extra` (the summed yield, `format event.yield`) and in `subtype` bits 8–11 (the weapon class of the origin, `format
event.class`): §5.4 "S11 conventions". The lines above that say `extra` and bits 8–15 are 0 for other types describe
`ext s7p fx 1` alone; an S7p reader ignores both on Explosion rows (no S7p consumer reads them).

- **SE (`EffectsProducer.cs`, update thread).** The colour rule is SE's own

 the colours and the scale are read once per
 thruster at index time from `MyThrustDefinition.FlameIdleColor`/`FlameFullColor` (public `Vector4`) and
 `FlameLengthScale` (public float), `MAX_THRUST` from `VRage.Game.MyConstants.MAX_THRUST` (1.5) at the first active tick.
 The streak ray is `MyPhysics.CastRay(Vector3D, Vector3D, List<HitInfo>, int)` on layer 15
 the nearest hit whose `GetHitEntity.GetTopMostParent` is not the shooter's top-most entity;
 only on the update thread (`raysOffThread` otherwise), ≤ 16 per tick (`raysSkipped`). The drill class comes from
 `MyDrillBase.DrilledEntity` (public get; voxel → `MyVoxelBase.GetMaterialAt(ref Vector3D)` → `MyVoxelMaterialDefinition.
 MaterialTypeName`, ice/snow → Ice, sand/soil/grass/dirt/mud/gravel/moss/silt/desert → Sediment, else Rock; grid →
 Metal; anything else Other) and `DrilledEntityPoint` (public get); ≤ 4 material lookups per tick, each drill re-read
 every 30 ticks (`contactSkipped`). Switches `colour`, `ray`, `contact` (on by default; off = the S7 rows exactly).
- **Subnautica (`EffectsPlayer.cs` + `EffectsPlayer.S7p.cs`, `EffectsLibrary.cs`, `EffectsRecipes.cs`; rules in Wire
 `S7pEffectRules`).** Under water (the `Underwater` rows; above water the S7p looks stay off unless the switch `above`
 is on, because the scene-above route B owns the picture above the waterline — both on would draw twice):
 P2 a flame cone per flame (a held clone, local space) in SE's colour, length = SE's own length (9.5 × scale × radius ×
 strength) × `flame`, diameter 2 × radius, ≤ 600 particles/s, a flame light from the S7 glow pool (≤ 8, range ≤ 25 m),
 a heat shimmer only when a donor with a distortion/refraction shader exists; S7's bubbles and engine loop unchanged
 (S7.N4.thruster.rate holds). P3 per frame every live thruster flame whose reach (1.5 × length + radius) crosses or comes
 within 4 m of SN y = 0 raises a white steam plume at that point on the surface, density = strength × (1 − d/4)² × (1 −
 0.5·t/reach), ≥ 0.03; 3 per grid and 12 in all (the strongest; the rest `plumeDropped`), Subnautica's own surface
 splash pulsed as spray at density ≥ 0.5, boiling bubbles under the steam when the flame is below. P4 a flash (8 × (0.5 +
 0.5·intensity), × 0.6 under water, range 4 × radius within 6..120 m), Subnautica's own explosion
 (`Vehicle.destructionEffect` of the Seamoth from a 4 m radius, scaled radius / 6 m, else S7's crashfish detonation), a fireball, smoke above water or a rising bubble cloud
 under it, a shock ring flying out to 2 × radius in the horizontal plane, debris sparks; S7's sound rule unchanged. P5 a
 50 ms muzzle flash (three glow sprites along the barrel + a light), forward sparks, fine bubbles under water; a tracer
 (one stretched particle) flying muzzle → hit at the bullet's speed (size / lifetimeMs) for hit-distance / speed; a trail
 of fine bubbles along that path (one per 0.6 m, ≤ 48); at the hit S7's surface impact plus sparks and fine bubbles. The
 diver's breath bubbles (`PlayerBreathBubbles`) are no longer used by any S7p look. P6a one held, alpha-blended dust
 plume per drill while the bit touches (rock 18/s, sediment 26/s, ice 10/s, metal 0, × max(0.5, size)), coloured by the
 contact, sparks (metal 30/s, rock 6/s), a contact light ≤ 1.0 / 4 m; the S7 look replayed the 1.5 s exoDrill one-shot
 every 0.1 s (≈ 15 stacked copies) and held the Prawn's `drillFXprefab` on the ship drill: the "blinding" white-out
 (HYPOTHESIS). `drillgame` = Subnautica's own exoDrill/Prawn effect instead, one held instance while touching.
- **Sources (found at run time, never shipped; the safety filter of S7).** New: `Flare.fxControl.emitters[*].fx` (TechType
 Flare 754), `HeatBlade.fxControl.emitters[*].fx` (801), `Exosuit.fxcontrol.emitters[0].fx` (jump jets) and `[2].fx`
 (landing puff), `Vehicle.destructionEffect` of the Seamoth and the Prawn (cloned inactive with every gameplay part's
 GameObject removed: "stripped", counted), `VFXSeamothDamages.smokeParticles` and `.sparksPrefabs[i]` of the Seamoth.
 The S7 filter's `VFX*` allowance now refuses `VFXPushBodiesFromCenter`, `VFXExplosionPlayerFX` (screen effect),
 `VFXEarthquake`, `VFXFallingChunk/Debris/Rocks`, `VFXWaterSpray`, `VFXWeatherManager`, `VFXConsoleCommands`,
 `VFXSchoolFish*`, `VFXPrecursor*` (three), `VFXScan`, `VFXScanning`, `VFXOnAttack`. Lights inside the drill sources are
 capped at 1.0 / 4 m, inside the impact and destruction sources at 1.5 / 6 m. The composed looks: for each role (glow,
 smoke, spark, dust, shimmer) the best particle system of the sources by `S7pEffectRules.DonorScore` (names, blending:
 glow and spark additive, smoke and dust never additive; the source's rank in the role's list; a relaxed pass by name
 when the blending is unreadable; mesh-particle systems never), cloned and stripped to its ParticleSystem (children,
 scripts, sub-emitters, collision, lights, trails and forces off; material, texture sheet, vertex streams and custom
 data kept), then given the recipe's shape, lifetime, colour and emission. Every choice is logged (`DE.S7p fx donor …`,
 `DE.S7p fx composites …`, `DE.S7p fx source <S>: lights …; systems …`); a role without a donor turns its looks off
 (the S7 look stays).
- **Live scales** (`S7pFxSettings`): `de.s7p fx [status | reset | <type> <scale> | type=scale …]`, types `flame glow
 shimmer plume explosion muzzle tracer streak impact drill sparks light` (multipliers 0..10, 0 = that S7p look off and
 the S7 look back) and the switches `above drillgame colour ray contact`; environment `DEEPENGINEERS_S7P_FX` ("flame=1.5,
 plume=0.5", read by both plugins at start). `de.s7p` is the shared S7p verb: the effects answer only a request with
 `fx` (`{argv: ["fx", …]}`, `{fx: "flame 1.5"}`, `{fx: {flame: 1.5}}`, `{fx: "flame", action: "1.5"}` and `{action:
 "fx", flame: "1.5"}` from SE's chat parser, `{action: "fx", type, scale}`; `S7pFxSettings.Tokens`) and pass every other
 on. SE answers it in `S7Effects.Execute` (chained before scene-above's handler), applies its switches and from chat
 relays the tokens to Subnautica (`de.s7p {fx: "…"}`); Subnautica's effect player answers it before the bridge's later
 handlers. SE also takes `de.fx {s7p: "colour=0,…" | {colour: 0} | status | reset}`.
- **Caps and drop counters (per frame / tick).** SE: 16 streak rays, 4 material lookups per tick. Subnautica: flames 96,
 shimmer 24, plumes 16 clones (3 per grid, 12 in all; 256 candidates), muzzle 16, dust 12, sparks 3 shared + 16 held,
 the shared world emitters (bubbles 4, tracers 4, fireball, smoke, ring 2 each), 600 bubbles and 300 sparks per frame,
 4 drill lights; every refusal counted (`*None`, `plumeDropped`, `plumeCandCapped`, `bubblesCapped`, `sparksCapped`,
 `sharedNone`) in the rows below; S7's 2 instantiations per frame and 0.45 ms budget unchanged.
- **Rows.** SE `s7.fx` gains `s7pColoured`, `s7pNoColour`, `s7pRays`, `s7pRayHits`, `s7pRaysSkipped`, `s7pRaysOffThread`,
 `s7pRayErrors`, `s7pContactRows`, `s7pContactLookups`, `s7pContactSkipped`, `s7pContactErrors`, `s7p` (the switches that
 differ from their defaults); `de.fx` status the same plus `s7pExt`, `s7pSettings`, `s7pMaxThrust`. Subnautica: new
 `s7p.fx` (1 Hz) {snFrame, seTick, ext, pinned, changed, above, composites{look: built|off}, donors{role: source/system},
 flames, flameNone, flameLights, shimmers, plumes, plumesLive, plumeDropped, plumeCandCapped, plumeNone, sprays, boils,
 muzzles, muzzleNone, tracers, tracerNone, streakBubbles, bubbles, bubblesCapped, sparks, sparksCapped, impactSparks,
 explosions, explosionPrefabs, explosionLayers, explosionNone, dust, dustNone, drillSparks, drillLights, drillLightNone,
 drillGame, sharedNone, lightsCapped, stripped} (cumulative); `s7.fx.thruster` gains {flame, flameLen, colour (the raw
 `extra`), lengthScale} and its `kind` is the subtype's low byte; `de.fx {action: sources}` lists the donors and
 composites.
`de.s7p fx …` tunes live).** The donors chosen in the player's game
 give the intended looks (glow additive, smoke white enough for steam, the Seamoth destruction effect readable at a
 scale); the composed flame reads as a flame under water at SE's length; the plume reach (1.5 × flame + radius, 4 m
 band) matches "flying by the water"; the stacked one-shots were the drill's white-out; a distortion shader exists for
 the shimmer (else none, logged).

*C12 static primitives (S7; `S7Statics`).* **Subnautica producer.** Every `MinRescanMs` (2 s) and after a batch
finished loading, the batches within `ScanRadiusM` (320 m) of the camera (`LargeWorldStreamer.GetContainingBatch`,
160 m batches, `GetBatchMins/Maxs`) are scanned with `Physics.OverlapBoxNonAlloc(centre, half, buffer, identity, mask,
QueryTriggerInteraction.Ignore)` (resumable, ≤ 1 ms per frame; S7 review F5: `mask` = every layer the player collides
with, `!Physics.GetIgnoreLayerCollision(LayerID.Player, l)`, minus `LayerID.TerrainCollider` and `LayerID.Trigger`,
the pattern of `LayerID.GetAllCollisionsMask`; first written as `1 << 0` (Default), which missed 2,510 solid wreck
colliders on the layer DiveReel). A collider counts when enabled, non-trigger, on a layer of that mask, and its
hierarchy passes the filter: under a `LargeWorldEntity` (or a batch root object), no `Creature`, `Pickupable`, `Vehicle`,
`FreezeRigidbodyWhenFar`, `WorldForces`, S6 `ProxyMarker`, the bridge root, and no `Rigidbody` or `LiveMixin` unless the root's
`UniqueIdentifier.ClassId` is on the census allow-list (chosen by entity type, never by "static or kinematic": S7.M7).
Primitives: Box (`center`, `size`/2 × `lossyScale`), Capsule
(`center`, `radius`, `height`, `direction`; `format statics.capsule`), Sphere (`center`, `radius` × max scale), a
`MeshCollider` as the OBB of `sharedMesh.bounds` (kind 4 `MeshObb`); `centreSn`, `qSn` (w ≥ 0) world; `classIdHash` =
FNV-1a 32 of the root's class id; `flags` (`StaticFlags`): `Census` 1 (from the cache), `AllowListed` 2, `SkewApprox` 4
(a non-uniform scale under a rotation, > 1 % shear). Primitives entirely above SN y = 0 are not sent. Per batch the set
only grows within one Subnautica generation and world epoch (a cell unload never removes a wreck); dedup by; a batch is resent with `epoch` + 1
when new primitives appear; ≤
`MaxPerBatch` (4,096) primitives per message (the rest logged). `BatchClear` only when a batch reported non-empty
becomes empty after a reload of the same generation (HYPOTHESIS: never in practice). `version` (`pad_6`) = 1; C7
`capabilities` bit 2 `statics` set while the reporter runs. **Census** (`de.statics census on`, the census session):
the same messages are also written to `%LOCALAPPDATA%\DeepEngineers\census\batches\<bx>.<by>.<bz>.c12` (the payload
bytes, `Census` set), derived, never shipped. **SE consumer.** Drains C12 every tick within 1 ms; per batch: the
newest epoch wins; a host exists while a mirrored grid or the character is within `CreateRadiusM` (300 m) of the
batch's primitives and closes beyond `CloseRadiusM` (350 m), on epoch change, world unload and a new Subnautica
generation. Without a live message, a cached census file of the batch is used (`Census`), so a grid parked against a
wreck far from the camera collides. Host (`format statics.host`): a plain `MyEntity`, no model, `Render.Visible`
false, `Save = false`, named `DE.Statics.<bx>.<by>.<bz>.e<epoch>[.p<k>]` (k for batches over `MaxPerHost` 1,024
primitives), world matrix = translation `o` = F⁻¹(the mean of the primitive centres), identity rotation;
`MyEntities.Add(host, true)`; body `new MyPhysicsBody(host, RigidBodyFlag.RBF_STATIC)` with
`CreateFromCollisionObject(shape, Vector3.Zero, host.WorldMatrix, null, MyPhysics.CollisionLayers.StaticCollisionLayer
(13))`; `shape` = `new HkStaticCompoundShape(HkReferencePolicy.TakeOwnership)`
 + `AddInstance(HkShape, Matrix)` per primitive + `Bake`, built on the update thread (Havok-initialised) over several ticks (≤ 256 primitives added per tick).
Geometry (`format statics.axes`, `statics.capsule`): box → `HkBoxShape(half')` with `half'_i = half_i ·
sqrt((a_i.x² + a_i.z²)/s² + a_i.y²)` (a_i the SN world axis i, s = Rs/r at the centre) at `F⁻¹(c) − o` with the rows
`staticAxes(q)` (determinant +1 asserted, else the primitive is dropped and counted); capsule → `HkCapsuleShape(F⁻¹(c −
a·h) − o, F⁻¹(c + a·h) − o, radius/s(c))`; sphere → `HkSphereShape(radius/s(c))`; MeshObb as a box. A malformed
message (counts, version, NaN) is dropped whole and counted. A host is never in a save (S7.N10, M8).

*C12 as built.* (1) **Lightmapped prefab
scenes.** The Aurora is not under a `LargeWorldEntity` or a batch root: `LightmappedPrefabs` loads the `aurora` scene
additively and parks its `__LIGHTMAPPED_PREFAB__` root at the scene root renamed `<scene>-MainPrefab`
The
reporter therefore also accepts a root named `<scene>-MainPrefab` as world, with the class id `scene:<scene, lower
case>` (Wire `S7StaticsMath.LightmappedSceneClassId`); without it the Aurora hull of S7.N5 is never reported. (2)
**Census sources.** The batch objects are almost only atmosphere volumes and lights (174 class ids); the wrecks,
precursor structures and Lost River trees live in the baked entity cells, so the census tool reads both
`BatchObjectsCache` and `CellsCache` (`CellManager.LoadCacheBatchCellsFromStream`: `CellsFileHeader`, per cell
`CellHeaderEx` + data / legacy / waiter object trees), follows every `PrefabPlaceholder.prefabClassId` in a prefab, and
adds the lightmapped scenes as `scene:<scene>`. The allow-list file (`%LOCALAPPDATA%\DeepEngineers\census\
allowlist.json`, derived, never shipped) is `{version 1, allow: [classId…], handled: [classId…], skip: {classId:
reason}, summary, classes: {classId: {file, bundle, via, instances, colliders, kinds, verdicts, mesh, nonDefault,
largeMeshObbs, rigidbody, liveMixin, status, reason, allowListed}}}`; `allow` = the handled classes whose reported
colliders sit under a Rigidbody or a LiveMixin (the only two rules the allow-list waives; the entity types never are).

scenes, 833 handled, 838 skipped with a reason, 367 allow-listed; G10: 352 mesh colliders (26 non-trigger Default),
3,029 non-Default colliders, 2 non-convex mesh OBBs with a half extent over 20 m (`Precursor_Pipes_Aquarium01/02`, up to
62 m). **Rule:** a concave `MeshCollider` is sent as the closed box of its
`sharedMesh.bounds` like every mesh collider; the two Precursor aquarium pipe meshes (half extents up to 62 m) therefore
become closed boxes in SE (their openings closed); no further work in v1. (3) **Reporter.** A collider is evaluated once per
generation (instance-id cache; statics do not move; toggling S7.M3 or S7.M7 clears the cache); a full overlap query is
split into octants down to 10 m; `BatchClear` is never sent (the sets only grow) and its pad 0x06 stays 0. (4) **SE
consumer.** The life cycle measures each batch's primitive bounds against the anchors in SN metres through F (every S5
mirrored grid or subgrid: its box centre and half diagonal; every online player's character: never "the player"); the
host's name is its `DisplayName` (`MyEntity.Name` is not set: SE's name dictionary stays clean); a census batch whose
hosts closed is forgotten and re-read when an anchor returns. (5) **S7.N5 protocol.** Subnautica `de.statics {action:
rays, n, range (60 m), seed}` replies `rays: [{i, o, d, aim, len, hit, dSn, static, kind, cls, hitName}]` (SN world);
SE `de.statics {action: rays, rays: [{o, d, len, dSn, static}]}` casts F⁻¹(o) → F⁻¹(o + d·len) with `MyPhysics.CastRay(…, 0)`
and replies per ray `{hit, dSe, dSnOfSe = |F(hit) − o|, host, entity, name, diff = |dSnOfSe − dSn|}` and `p95DiffM`
(S7 review F6: every hit along the ray through `MyPhysics.CastRay(Vector3D, Vector3D, List<HitInfo>, int)`, sorted by
distance, and the first hit that is a statics host is compared, so terrain or a fish in front is no mismatch; per ray
also `firstHost`, `firstName`, `static`; summary counters `snStatic`, `seMissWhereSnStatic`, `firstHitNotHost`,
`snNotStatic`; Subnautica aims at the colliders it has, decision None, Duplicate or MutantCapsule, not at what was sent,
and `static` = its first hit is one of them; the oracle uses the static rays only, a missing SE host counting +inf, and
fails above 5 % misses);
`{action: probe, entity, down}` casts from a grid's box centre towards the planet centre (the far-grid check); `{action:
hosts}` lists the hosts with their entity ids (S7.N10).

*Character bones (S7; `S7Bones`).* With `ExtraBones` (CharacterAdd `flags` bit 2), set by SE when the model has ≤ 160
bones and every `canon1` bone is found (else S5's 78-row export, logged once): `CharacterAdd.boneCount` = C3
`SkinnedBind.boneCount` = C1 `boneCount` = `Model.Bones.Length` (81 for both astronauts, VERIFIED `SE_astronaut.mwm`
male and female), rows 0–77 in `canon1` order (unchanged S5 meaning: vertex indices, retarget, hierarchy), rows 78–80
the model's non-canonical bones in `Model.Bones` order (`format bones.order`): their C1 rows are
`BoneAbsoluteTransforms[i]`, their SkinnedBind rows the inverse absolute bind (`SkinTransform`). No vertex weights them
(LOD0 blend indices ≤ 76 in both models), so the skin is the S5 skin; Subnautica poses rows 78+ flat under the Entity
from their absolute matrices (`format bones.unity`, the §2.5 conjugation `Mirror · Mᵀ · Mirror`), rows 0–77 as S5
(SeBones, Retarget, Bind; the S5 character path with path G stays the swim source and the fallback; S5a's diver arms
stay the first-person diver). `boneCount == Model.Bones.Length` is asserted for every character model exported in the
session (`DE.S7 bones <model> <n>`). Bones are keyed by the character id (C1 `characterId` + `bonesVersion`); C1 carries
the local viewer's character only (other characters: §8, open item O24).

*Audio polish (S7; the items of ARCHITECTURE §5.3 and S2-STATUS, nothing new).* (A1) SE's external emitters under
water: a Harmony postfix on `MyEntity3DSoundEmitter.SelectEffect` returns `LowPassNoHelmetNoOxy` (SE's own effect,
HYPOTHESIS fit; `de.
SE mix is in effect (C5 `seMixActive`) and the camera is not in an airtight room, for sounds whose `MySoundData.
ModifiableByHelmetFilters` is true (`format audio.muffle`); looping sounds re-select every update, one-shots at play.
S7 review F2: only in a world with `MySession.Static.Settings.RealisticSound` and `MyFakes.ENABLE_NEW_SOUNDS` (SE's own
gate: in Arcade sound mode SE installs no effect delegate and its `SelectEffect` answers `NullOrEmpty`, so a forced
answer would only make `Update` restart every looping modifiable sound); an Arcade world logs once `DE.Audio muffle:
Arcade sound mode, SE's own effect path is off — Subnautica's mix carries the dive` and S7.N11.muffle is INFO there (no
safe Arcade route through the public API: the effect must replace the emitter's private voice).
(A2) The bus above water under `Site` stays S2's: Subnautica muted above water (S7a "Audio … Stays"), so S7's event
sounds play only on `Underwater` rows (`format audio.bus`). (A3) Inside an airtight room under water the view state
is still Underwater (y rule): Subnautica's bus 1.0 with its interior ambience (`FMODGameParams` interior events through
`IsInsideWalkable`), SE game 0.7 × config, and no muffle (dry air); recorded by S7.
for a patch. (A4) The crossfade reversal wording (S2 D148) is settled as built: SE restarts a full `crossfadeMs` ramp
from the current level, Subnautica moves its bus at the constant rate 1/`crossfadeMs`; uninterrupted fades agree. (A5)
Biome and depth ambience: unchanged (the ghost at the camera feeds `FMODGameParams`; S1.N6's vanilla variant), recorded
by S7.N11 (INFO). (A6) Event sounds: §"C2 effect events" table, 3D at the event, volume ∝ intensity, none beyond 250 m.

*Game members S7 uses.* A missing member logs
`DE.Harmony MISSING <label> (feature off)` or `DE.S7 <feature> OFF missing <member>` once and disables only that
feature.

| Side | Member (exact signature) | Use | Source |
|---|---|---|---|
| SE | `MyGridGasSystem.GetOxygenRoomForCubeGridPosition(ref Vector3I gridPosition)` → `MyOxygenRoom` (public) | interior flag | ` |
| SE | `MyGridGasSystem.GetSafeOxygenBlock(Vector3D position)` → `MyOxygenBlock`; `MyOxygenBlock.Room` → `MyOxygenRoom` | flag inside a block | `, ` |
| SE | `IMyGridGasSystem.IsProcessingData` (bool; explicit implementation over `m_isProcessingData`) | hold rule | `, ` |
| SE | `MyGridGasSystem.OnProcessingDataComplete` / `OnProcessingDataStart` (public `Action` properties, invoked at the background task's end/start) | RoomSet trigger | ` |
| SE | `MyGridGasSystem.GetRooms(List<IMyOxygenRoom> list)` → bool (false while processing; fills `Blocks` per call) | RoomSet only | ` |
| SE | `MyGridGasSystem.AddDepressurizationEffects(MyCubeGrid grid, Vector3I from, Vector3I to)` (public static void) | HullBreak | ` |
| SE | `MyOxygenRoom.IsAirtight` (bool), `Index` (int), `Blocks` (`HashSet<Vector3I>`), `OxygenLevel(float gridSize)` → float | flag, RoomSet | ` |
| SE | `MyCubeGrid.GridSystems` (`MyCubeGridSystems`) → `GasSystem` (`MyGridGasSystem`; created only with both settings); `MyCubeGrid.WorldToGridInteger(Vector3D coords)` → `Vector3I`; `GridSize` (float); `GridIntegerToWorld(Vector3I)` → `Vector3D` | probe | `; ` |
| SE | `MySession.Static.Settings` (`MyObjectBuilder_SessionSettings`) fields `EnableOxygen`, `EnableOxygenPressurization` (bool) | world-settings check | ` |
| SE | `MyLight.Position` (Vector3D), `Color` (Color), `Falloff`, `Range`, `Intensity` (float), `LightOn` (bool), `ReflectorOn` (bool), `ReflectorDirection` (Vector3), `ReflectorColor`, `ReflectorRange`, `ReflectorIntensity`, `ReflectorFalloff`, `ReflectorConeDegrees` (full cone: `2·acos(1 − ReflectorConeMaxAngleCos)`), `CastShadows` (bool) | light rows | ` |
| SE | `MyLightingLogic.Lights` (`List<MyLight>`); fields `m_lightingLogic` of `MyLightingBlock` (protected), `MySearchlight`, `MyHeatVentBlock`, `MyLightingComponent` (private); `MyBeacon.m_light`, `MyRenderComponentCharacter. `; `; `; `; `; ` |
| SE | `MyThrust.CurrentStrength` (float), `Flames` (`ListReader<MyThrustFlameAnimator.FlameInfo>`: `Position`, `Direction` (Vector3), `Radius` (float), `HasDamage`), `ThrustForwardVector` (Vector3I), `BlockDefinition` (`MyThrustDefinition`: `ThrusterType` MyStringHash, `ForceMagnitude`) | Thruster | ` |
| SE | `MyGunBase. renderId = null, bool userNormalizedPositionForEffects = true)` (public void; every other `Shoot` ends here); `WeaponProperties.AmmoDefinition` (`AmmoType`, `DesiredSpeed`, `MaxTrajectory`; `MyProjectileAmmoDefinition.ProjectileCount` int) | WeaponMuzzle, BulletStreak | `; `; ` |
| SE | `MyProjectile.OnHit(IMyEntity entity, ref MyHitInfo hitInfo, object customdata, ref LineD line)` (public, struct instance); `MyHitInfo.Position` (Vector3D), `Normal` (Vector3) | Impact | `; ` |
| SE | `MyExplosions.AddExplosion(ref MyExplosionInfo explosionInfo, bool updateSync = true)` → bool (public static); `MyExplosionInfo.ExplosionSphere` (BoundingSphereD), `ExplosionType` (`MyExplosionTypeEnum: byte`), `Damage` (float) | Explosion | `; `; ` |
| SE | `MyEngineerToolBase.IsShooting`, `HasHitBlock` (bool), `GunBase` (`MyToolBase`) → `GetMuzzleWorldPosition` → Vector3D; `MyHandDrill.IsShooting`, `IsDrillingAnObject`, private `m_drillBase`; `MyShipToolBase.IsShooting`; `MyShipDrill.IsShooting`, private `m_drillBase`; `MyDrillBase.IsDrilling`, `Sensor` → `MyDrillSensorBase.FrontPoint`/`Center` (Vector3D); `MyCharacter.CurrentWeapon` (`IMyHandheldGunObject<MyDeviceBase>`) | tool sustains | `; `; `; `; `; `; `; ` |
| SE | `MyGridPhysics.PlayCollisionParticlesInternal(IMyEntity otherEntity, ref Vector3D worldPosition, ref Vector3 normal, ref Vector3 separatingVelocity, float separatingSpeed, float impulse, MyEntity.ContactPointData.ContactPointDataTypes flags)` (public void) | GridImpact | ` |
| SE | `MyEntity3DSoundEmitter.SelectEffect` → `MyStringHash` (private, no parameters; also the `EmitterMethods[3]` delegate); `m_lastSoundData` (`MySoundData.ModifiableByHelmetFilters`, public bool field, default true) | muffle (A1) | `; ` |
| SE | `MySkinnedEntity.BoneAbsoluteTransforms` (`Matrix[]`), `AnimationController`; `MyModel.Bones` (`MyModelBone[]`); `MyCharacterBone.SkinTransform` (Matrix) | bones | `; `; ` |
| SE | `MyEntities.Add(MyEntity entity, bool insertIntoScene = true)`; `MyEntity.Save` (bool); `MyPhysicsBody(IMyEntity entity, RigidBodyFlag flags)`, `RigidBodyFlag.RBF_STATIC` = 4; `MyPhysicsBody.CreateFromCollisionObject(HkShape shape, Vector3 center, MatrixD worldTransform, HkMassProperties? massProperties = null, int collisionFilter = 15)`; `MyPhysics.CollisionLayers.StaticCollisionLayer` = 13 | statics host | `; `; `; ` |
| SE | `HkStaticCompoundShape(HkReferencePolicy policy)`, `AddInstance(HkShape shape, Matrix transform)` → int, `Bake`, implicit `HkShape`; `HkBoxShape(Vector3 halfExtents)`; `HkCapsuleShape(Vector3 vertexA, Vector3 vertexB, float radius)`; `HkSphereShape(float radius)` | statics shapes | `; `; `; ` |
| SE | `MyPhysics.CastRay(Vector3D from, Vector3D to, int raycastFilterLayer = 0)` → `MyPhysics.HitInfo?` (`Position` Vector3D, `HkHitInfo`); `MyPhysicsExtensions.GetHitEntity(this HkHitInfo)` → `IMyEntity` | S7.N5 rays | `; ` |
| SE | test hooks: `Sandbox.ModAPI.Ingame.IMyThrust.ThrustOverridePercentage` (float get/set), `IMyUserControllableGun.ShootOnce`, `IMyFunctionalBlock.Enabled` (bool get/set) | n4 scripted triggers | `, `, ` |
| SN | `SkyApplier.SetCustomSky(Sky customSky)`, `SetSky(Skies skyMode)`, fields `anchorSky`, `customSkyPrefab`, `dynamic`, `renderers`, property `InspectorApplySky` (Sky) | interior sky, S7.N8 | ` |
| SN | `MarmoSkies.main` (static), `GetSky(Skies sky)` → `Sky`, `GetSky(GameObject skyPrefab)`, `skyBaseInteriorPrefab`; enum `Skies` {Auto, Custom, SafeShallow, BaseInterior, BaseGlass, ExplorableWreck} | interior sky | `; `Skies.cs` |
| SN | `Player.IsInsideWalkable` → bool (public, non-virtual); `Player.IsInside`; `Player.CanBeAttacked`; `Player.main` | inside patch, S7.N2 | ` |
| SN | `WaterClipProxy.shape` (`WaterClipProxy.Shape` {DistanceField, Box}), `immovable`, `clipMaterial` (Material), `Rebuild`; `WaterscapeVolume.Settings.RenderImage(Camera, bool cameraInside, RenderTexture, RenderTexture)` | clip proxies, fog | `; `; ` |
| SN | `SeaMoth.bubbles` (ParticleSystem), `engineSound` (`EngineRpmSFXManager` → `engineRpmSFX` `FMOD_CustomLoopingEmitter`, parameter `rpm`), `toggleLights` (`ToggleLights.lightsParent`); `Vehicle.torpedoTypes` (`TorpedoType.prefab`), `Vehicle.splashSound` (FMODAsset); `SeamothTorpedo.explosionPrefab`, `fireSound`; `VFXConstructing.surfaceSplashFX`; `CollisionSound.hitSoundSmall/Medium/Fast/Slow` | effect sources, light calibration | `; `; `; `; `; `; `; ` |
| SN | `Welder.fxControl` (`VFXController`), `weldSound` (`FMODASRPlayer`); `ExosuitDrillArm.drillFXprefab`, `fxControl`, `loop`, `loopHit`, `hitSound`, `drillSound`; `Exosuit.armPrefabs` (`ExosuitArmPrefab[]` {techType, prefab}); `Crash.detonateParticlePrefab`; `PlayerBreathBubbles.bubblesPrefab`; `VFXSubLeakPoint.leakEffectPrefabs` | effect sources | `; `; `; `; `; ` |
| SN | `VFXSurfaceTypeManager.main`, `Play(VFXSurfaceTypes surfaceType, VFXEventTypes eventType, Vector3 position, Quaternion orientation, Transform parent)` → ParticleSystem; `VFXEventTypes` {diamondBlade, heatBlade, impact, knife, exoDrill}; `VFXController.Play`, `Stop` | impacts, tools | `; `VFXEventTypes.cs`; ` |
| SN | `Utils.PlayOneShotPS(GameObject particleSystemPrefab, Vector3 position, Quaternion orientation, Transform effectParent = null)`; `Utils.PlayFMODAsset(FMODAsset asset, Vector3 position, float soundRadiusObsolete = 20f)`; `FMODUWE.PlayOneShot(FMODAsset asset, Vector3 position, float volume = 1f)`, `FMODUWE.GetEvent(FMODAsset)`; `FMODAsset.path`, `id`; `FMOD_CustomEmitter.SetAsset(FMODAsset)`, `Play`, `Stop(STOP_MODE)`, `SetParameterValue(string, float)` | effects, sounds | `; `; `; ` |
| SN | `CraftData.GetPrefabForTechTypeAsync(TechType techType, bool verbose = true)` → `CoroutineTask<GameObject>`; `TechType` Seamoth 2000, Exosuit 2001, Welder 750, Crash 2509, ExosuitDrillArmModule 2116 | source prefabs | `; `TechType.cs` |
| SN | `LargeWorldStreamer.main`, `GetContainingBatch(Vector3 wsPos)` → Int3, `GetBatchMins/Maxs/Center(Int3)` → Vector3, `batch2root` (`Dictionary<Int3, LargeWorldBatchRoot>`), `BatchObjectsCacheFolder`, `GetBatchObjectsFilename(Int3)`; `LargeWorldEntity`; `UniqueIdentifier.ClassId` (string); `Creature`, `Pickupable`, `Vehicle`, `FreezeRigidbodyWhenFar` (`freezeDist` 48), `WorldForces`, `LiveMixin` | statics reporter, census | `; `; `; ` |
| SN (Unity) | `Physics.OverlapBoxNonAlloc(Vector3 center, Vector3 halfExtents, Collider[] results, Quaternion orientation, int mask, QueryTriggerInteraction queryTriggerInteraction)` → int; `Physics.Raycast(Vector3, Vector3, out RaycastHit, float, int, QueryTriggerInteraction)`; `BoxCollider.center/size`, `CapsuleCollider.center/radius/height/direction`, `SphereCollider.center/radius`, `MeshCollider.sharedMesh/convex`, `Collider.isTrigger/enabled/attachedRigidbody/bounds` | statics, S7.N5 | `UnityEngine.PhysicsModule.dll` (reflection) |
| SN (Unity) | `Light.type`, `range`, `intensity`, `color`, `spotAngle`, `shadows`, `renderMode`, `cullingMask`; `ParticleSystem.Play`, `Stop(bool, ParticleSystemStopBehavior)`, `Emit(int)`, `Emit(Vector3, Vector3, float, float, Color32)`, `emission.rateOverTimeMultiplier`; `SkinnedMeshRenderer.bones`, `rootBone`, `sharedMesh`, `updateWhenOffscreen` | lights, effects, bones | `UnityEngine.CoreModule.dll`, `UnityEngine.ParticleSystemModule.dll` (reflection; the SN project gains a `Private=false` reference to the latter) |

*Budgets and failure modes (CONTRACT budgets unchanged).* SE per tick: interiors ≤ 0.05 ms, lights ≤ 0.1 ms, event
sources and the window ≤ 0.2 ms (postfixes append O(1) to the fixed buffer), C1 + C2 with lights and events within the
S5 0.3 ms target (≤ 64 + 1,024 rows), RoomSet ≤ 0.5 ms and statics ≤ 1 ms (C12 drain and shape building), C4 ≤ 512 KiB
per tick (RoomSets on lane Edit). Subnautica per frame: lights ≤ 0.2 ms, effects ≤ 0.5 ms, statics scan ≤ 1 ms, the
proxy step unchanged. Never a growing queue: event caps with dropped counters, the 8-tick window, lights capped at 64
with 2 shadowed, statics streamed in 160 m batches within the S4/S5 radii (scan 320 m, hosts 300/350 m), a
`MaxPerBatch` and a census. Failures: settings off → interior OFF once; gas system processing → last value; a prefab,
FMOD asset or material missing → that kind's fallback or off, logged once; a Harmony target missing → that feature
off; SN behind more than 8 ticks → `eventsMissed`; C12 full → Subnautica re-queues the batch; a Havok error → that
batch has no host (logged once); a model over 160 bones or without `canon1` → S5's export
v3 rule: every mirror, event, light, room, host and bone record is keyed by entity id (grid
subgrid, character, batch); no S7 class assumes one character (the swim gate and the bones are per character id; C1's
interior extension describes the viewer's own camera).

*S7 control commands (C11, dev builds; chat `/de <verb> …`; the SE dispatcher `S7Commands.Execute` is chained first in
`Plugin.ExtraCommand`).* SE: `de.interior {action: status | probe, point?}` (the camera answer, the candidates, the
gas systems' processing state, the RoomSets sent), `de.lights {action: status}`, `de.fx {action: status | stress, n}`,
`{thrust: <entity id>, override: 0..1}` (`ThrustOverridePercentage` on every thruster of that grid), `{gun: <entity
id>, shots: n}` (`ShootOnce` n times
step), `{tool: <block entity id>, on: bool}` (`Enabled`), `{inject: <type>, at: [x,y,z], …}` (a synthetic event into
the window: the Subnautica path alone), `de.statics {action: status | hosts | rays, n, batch?}` (SE half of S7.N5: the
mapped rays cast with `MyPhysics.CastRay`), `de.bones {action: status | rows, frames}` (`s7.bones` rows, world
positions mapped by F), `de.s7audio {effect: name | off}`. Subnautica: `de.interior {action: status | trace, seconds}`
(the N2 caller trace: a postfix probe `new StackFrame(2, false).GetMethod.DeclaringType` counted per type, only
inside the trace window) `{patch: on | off}` (live A/B for S7.N1), `de.lights {action: status}`, `de.fx {action: status
| sources}`, `de.statics {action: status | census on | census off | rays, n}` (the Subnautica half of S7.N5:
`Physics.Raycast` along rays at reported primitives, the rays sent back for SE), `de.bones {action: rows, frames}`.
Seeded failures through `de.mutant S7.Mx on|off` on the side that owns them (ROADMAP S7).

*Rows (JSONL, `"ev"`).* SE: `s7.interior` {tick, inRoom, processing, breached, roomGridId, roomIndex, roomO2,
characterInRoom, candidates, settings {oxygen, pressurization}} (every change, 2 Hz while inside or near a room),
`s7.rooms` {tick, entity, boxes, truncated, hash, sent} (each RoomSet), `s7.lights` {tick, found, sent, shadowed,
fx` {tick, raised[type], sent[type], dropped[type], window, stream, firstSeq} (1 Hz and every
drop), `s7.statics` {tick, batch, epoch, primitives, host, state created|closed|census, ms} (each change), `s7.bones`
{tick, model, boneCount, modelBones, extra, world[[x,y,z]…]} (on request), `s7.audio` {tick, mixActive, inRoom, effect,
emitters, muffled} (each change, 1 Hz under water). Subnautica: `s7.interior` {snFrame, c1Tick, inside, patched,
roomGridId, chunks {inside, boundary, exterior}, skies {interior, biome}, clipProxies}, `s7.inside` {window
inside|outside, readers {type: calls}, ghost {o2, health, pos}, canBeAttacked, music, brightness}, `s7.sky` {entity,
chunk, class, applied interior|biome, ok}, `s7.lights` {snFrame, rows, applied, shadowed, pool}, `s7.fx`
{snFrame, applied[type], spawned[type], failed[type], missed, sustains, sources{kind: found|fallback|missing}},
`s7.statics` {snFrame, batch, epoch, primitives, filtered{reason: n}, census}, `s7.bones` {snFrame, presentedSeTick,
alpha, model, boneCount, world[[x,y,z]…]}, `s7.audio` {snFrame, events{type: started}, bus}.

*Interiors and lights as built.* SE
rows: `s7.interior` also {why change|periodic|status, disabled, m10}; `s7.rooms` also {rooms, cells, bytes, ms, why
first|gridAdd|recompute|recheck} (a first set without any airtight room is not sent: Subnautica's default is no room);
`s7.lights` also {grids, usLast, usMax, maxDistM (the farthest row sent), disabled}. Subnautica rows (the S5 row shape:
fields under `data`): `s7.interior` also {why, c1InRoom, c1Extension, c1Flags, patchOn, m1, driving, roomSets,
overrides, unstuckSkipped}; `s7.sky` also {why rebuild|verify, anchor, dynamic, m5, cameraInRoom (the camera's C1 room
is this chunk's entity or its root)}, `class` as `inside`/`boundary`/`exterior`; `s7.inside` {why, window,
insideFrames, outsideFrames, calls, unknown, readers, via (the readers through `IsInside`), readersInside,
readersOutside, viaInside, viaOutside, notCalled (comma list), ghost {o2 (fraction), o2Units, c1O2 (C1 swim
extension fraction), health, pos, posErrM (distance to `GhostPlayer.Target`)}, canBeAttacked, music (FMODGameParams
calls), brightness (uGUI_AdaptiveBrightness + ColorCorrection calls), unstuckSkipped}; `s7.lights` also {why,
c2Tick, found, missingOwner, intensityScale, rangeScale, m18, created, recreated, readerCommits, rowsNotV1,
rowsInvalid, registered}, plus `why seamoth` {seamoth} and `why calibrate` {seLum, seamothLum, ratio, pass,
suggestedScale, applied}. Commands: SE `de.lights` answers `status` only; Subnautica `de.lights` also `{action:
seamoth}` (every spawned Seamoth's `Light`s: head = under `toggleLights.lightsParent`, else cabin), `{action:
calibrate, se, seamoth, apply?}` (two probe luminances → ratio, ±25 %, suggested scale) and `{scale, range}` (the
tunables; environment `DEEPENGINEERS_LIGHT_INTENSITY`, `DEEPENGINEERS_LIGHT_RANGE` at start). Harmful readers of the
inside override neutralised for the ghost: `UnstuckPlayer.TrySavePosition`
 is skipped while the override holds (its inside branch stores `basePosition` = the origin as the
last safe point); `TemperatureDamage` stops inside, oxygen does not read the flag (`Player.CanBreathe` reads
`currentSub`). The clip proxy takes the source proxy's layer with its material.

*Integration as built.* SE: `S7Commands.Execute`
is chained first in `Plugin.ExtraCommand` and routes `de.interior`, `de.lights`, `de.fx`, `de.statics`, `de.bones` and
`de.s7` stays S7a's). `S7Host.Tick` runs `S7Statics.Tick` once per update after the S6
damage consumer; `S7Host.OnWorldForgotten` (the S5 producer's world forget: left the world or a new world epoch) clears
the interior holds, the RoomSet handlers and the effects' grid index; `S7Host.OnSessionUnload` (the session component's
`UnloadData`) closes every statics host. Subnautica: the S7 host owns the statics reporter (created after the other
hosts with the mapped section and the generation, updated every frame, `FillStatus` in the bridge's C7 write: bit 2,
disposed before every unmap of the section) and answers `de.statics` and `de.bones`; `de.interior`/`de.lights` stay
with the interiors host and `de.fx` with the effect player.

*S7 review fixes.*
(F1) SE builds airtight-room data only for a grid with a gas block that can pressurize a room: an **Air Vent**
(`MyGridGasSystem.ShouldPressurize`: `m_forcePressurize`, `m_gasBlocks.Count > 0` or existing rooms; `m_gasBlocks`
takes every `IMyGasBlock` whose `CanPressurizeRoom` is true, i.e. `MyAirVent`, powered or not). A house or fixture
without one never gets `InRoom`; the probe logs once per grid `DE.Interior: no airtight room possible on <grid>: it has
no Air Vent (MyGridGasSystem pressurizes only with a gas block)`, the RoomSet producer counts `noRoomData` (rows,
`de.interior` status) and logs `DE.S7 rooms <id>: no room data …` once. `de.s3 {place: {plate: {orientation: room}}}`
places the S7 room fixture (a 5 × 5 × 5 light-armor shell around a 3 × 3 × 3 interior; a round window in the north
wall, a battery in the floor, an Air Vent, a door, an Interior Light and three spotlights inside; block ids in the reply
`room`). (F8) `de.interior {action: door, block: <entity id>, open: true | false}` toggles a door through
`Sandbox.ModAPI.Ingame.IMyDoor.OpenDoor` / `CloseDoor` (a powered door: `MyDoorBase.Open` needs power), whose
`OnDoorStateChanged` makes the gas system recompute: the scripted recompute of S7.N1.flicker and S7.M10; row `s7.door`.
(F10) `de.fx {action: stress, n, ticks: k}` spreads the n events over k consecutive ticks (from the C2 hook), the
totals in an `s7.fx` row `why: stress.burst`; Subnautica's dev command `de.fx {action: fps, cap: 30 | 0}` caps the frame
rate through the S2 perf applier (vSyncCount 0, targetFrameRate = cap; cap 0 returns the recipe's pair) for the
S7.N4.loss burst, cleared by the launcher's teardown and when the effect player is disposed. (F13) A full effect pool
never lends a clone a sustain holds; the thruster pool is 128. (F15) Clip proxies are rebuilt only when their own
entity's room set changed; a destroyed proxy takes the mesh and material instance `WaterClipProxy` made. (F17)
Subnautica's `s7.statics` rows carry `creaturesReported` (primitives under a `Creature`: S7.M7 only) and `insideProxy`
(primitive centres inside an S6 proxy section box). (F18) SE's `stepLagMaxTicks` is measured by the sustain tracker
(ticks from a source's ≥ 0.05 change to the row carrying it, open steps included); Subnautica counts every event-sound
request by flag class (`requestsUnderwater`, `requestsDry`, `splashAllowed`, `allowedDry` = violations). (F19) S7.M8
(statics hosts with `Save = true`) is refused, `DE.S7 statics MUTANT S7.M8 REFUSED (not a throwaway copy)`, unless the
session's save folder (`MySession.Static.CurrentPath`) holds `DE_THROWAWAY.txt` (checked when set and again when the
world runs). (F20) Subnautica draws no SE light row from a snapshot older than 1 s. (F21) `s7.inside` carries
`noAggression` (`GameModeUtils.IsInvisible`). (F22) SE's `s7.statics` `created` rows carry `bakeMs` and `raiseMs`.
(F23) The grid-contact postfix exits before any math while the effects are inactive and counts with `Interlocked`
(v3: on a multiplayer host contacts go through `MyCubeGrid.UpdateParticleContactPoint` and miss this postfix). (F24) The
N2 caller trace reads one `StackFrame` at a time, not a full `StackTrace` per call.

*S7p scene-above.*
(P1) **SE's particle pass cannot be kept under the picture** (VERIFIED VRage.Render11 `MyRender11.cs`: the skip clears
`m_drawScene`, so `FullDraw` never calls `DrawScene` and clears the backbuffer to (0,0,0,0),; every GPU
particle and billboard pass runs inside `DrawGameScene` against `MyGBuffer.Main` — `RenderScheduler`, then
`MyBillboardRenderer.RenderLDR` / `RenderPostPP`): route B is the rule. Subnautica plays every C2 event row
**without `Underwater`** (the picture's waterline, SN y = 0, as SE's `S7EventRules.WaterFlags` sets it) with an
above-water variant through its own second `S7EventReader` on the C2 extension list (the S7 player keeps the under-water
looks, the splash, its flash lights and every sound; SE keeps its own sounds above water): muzzle flash (+ sparks, a smoke
puff; a missile launch: bigger flash, fire, backblast smoke), a tracer (one stretched particle flying the streak's length
in its lifetime), impact sparks/dust (by `subtype` grid/voxel), a layered blast (flash, fireball, smoke, debris sparks, a
shock ring; scaled by `size` = radius, clamped at 60 m), grid-impact sparks/dust, a hull-break vapour jet (held for the
row's lifetime), and per-frame sustains: the thruster flame along the flame dummy's `dir` in SE's own flame colour and length class
when the row carries them (S7p-effects-under's `extra` at 0x3E, `S7pFxCodec.TryDecodeFlame`; else the kind's colour by
`subtype & 0xFF` ion/hydrogen/atmospheric), rate and length from `intensity` × `size`; welder/repair blue-white sparks, grinder sparks
while `Hit`, drill dust and sparks while `Hit`. Seven world-space particle systems, unscaled time, materials from Unity's
legacy particle shaders by name (else a loaded particle shader, `Sprites/Default`, `Hidden/Internal-Colored`) with a soft
dot made in code; ≤ 256 sustains, ≤ 768 particles per frame, ≤ 512 rows and 0.25 ms of rows per frame (the rest next
frame), each cut counted. When effects-under's knob `de.s7p fx above 1` draws its own looks above water, the types it
covers (thruster, muzzle, streak, impact, explosion, drills) are left to it (`rowsDeferredToFx`). **Missiles** (`MyMissile`
entities; `MyMissiles.Static.GetAllMissilesInSphere(ref BoundingSphereD, List<MyEntity>)`, public, VERIFIED
`MyMissiles.cs`) ride the S5 stream as floating entities: the nearest 24 within 1,500 m of the camera (over: counted),
GridAdd `FloatingObject | Missile` (bit 5 = 32), one PartAdd class 7 with SE's own missile model, a C2 entity row every
tick, EntityRemove when it explodes, leaves the sphere or the cap; Subnautica adds a trail at its tail along its flight
(flame and smoke above water, a bubble stream below); never in a collision skin or a room set. Route toggle
`de.s7p {above: events | se | off}` (both plugins; env `DEEPENGINEERS_S7P_ABOVE`, default `events`): `se` = the S7
behaviour above water (no variants, missiles still mirrored, trails under water only), `off` = neither variants nor
missiles. (P6b) **Render-thread spinners**: SE turns some block subparts in the render thread only (a rotation animator
fed by a speed; the entity's local matrix never moves, so the S5 motion detector never saw them) — the ship drill head
(`MyShipDrillRenderComponent+MyDrillHeadRenderComponent.m_speed`, AxisZ, spin = speed / 0.5 s), the ship grinder blade
(`MyRenderComponentShipGrinder+MyRenderComponentShipGrinderBlade.m_speed`, AxisX, 41.8879 / 20.94395 rad/s²), the
atmospheric thruster propeller (`MyRenderComponentThrust.m_propellerSpeed` on the block render, AxisZ), the wind turbine
(`MyRenderComponentWindTurbine+TurbineRenderComponent.m_speed`, AxisY), the air vent fan
(`MyRenderComponentAirVent+MyWallFanRenderComponent`, `m_speed` of its generic base, AxisX) and the hydrogen engine rotor
(`MyRenderComponentHydrogenEngine+MyRotatingSubpartRenderComponent`, `m_speed` of its generic base, AxisZ); all private
float fields VERIFIED. Such a subpart is exported `Dynamic` and its C2 row carries
`R(axis, angle) × composed` (VRage row order) with the angle integrated on SE from the same speed (spin-up/down rates as
SE's, relative ones from the largest speed seen; ≤ 0.9 π per 60 Hz tick so Subnautica's slerp turns the right way);
≤ 1,024 spinners (over: static, counted); a missing field turns only that type off (logged once). **The held tool's
subparts**
 go as character parts class Tool, `Dynamic`, keys `0x7470000000000000 | i` (i < 8; Body 1,
Head1st 2, Tool 3 unchanged), each posed by its own C2 row (its local chain × the tool relative to the character), sent
and removed with the tool (`CharacterExport.TickTool`). Toggle `de.s7p {subparts: on | off}` (SE; env
`DEEPENGINEERS_S7P_SUBPARTS`, default on). (P7) **Projections ride the S5 grid stream** (not the S7a ghost record: a
blueprint is a whole grid with cells, fat blocks and subparts, the ghost record one block): a preview grid is mirrored
when its projector is projecting
(`Sandbox.ModAPI.Ingame.IMyProjector.IsProjecting`), it is the projector's `ProjectedGrid` (one per projector), its
projector `Scale` is 1 (a console miniature is not), it has ≤ 20,000 blocks and ≤ 4 projections are mirrored (a mirrored
one keeps its place); else it stays out (counted by reason in `s7p.scene`). GridAdd carries `Projection` (bit 4 = 16;
both new bits outside S5's 0–2, S7a's Ghost 3 and the version nibble; read only on a version-1 GridAdd). Blocks SE hides in
the projection (`|Dithering| ≥ 1`: `MyProjectorBase.HideCube` sets −1 — already built, or not buildable with "show only
buildable"; the cube parts carry it in `InstanceData.ColorMaskHSV.W`, the block in `MySlimBlock.Dithering`) are never
exported, and a projection has no armor edges; a postfix on `MyProjectorBase.SetTransparency(MySlimBlock, float)`
 re-reads a block the moment SE shows or hides it, so a welded block leaves
the hologram on the next export. It leaves (EntityRemove) when the projector stops projecting or turns off. No collision
skin (S6), no room set and no interior probe (S7), no light rows; Subnautica draws every renderer of a projection with one
hologram material (Unity's Standard in Fade mode as the S7a ghost, colour (0.32, 0.66, 1.0, 0.30), emission (0.08, 0.24,
0.55)), no shadows. Toggle `de.s7p {projections: on | off}` (SE; env `DEEPENGINEERS_S7P_PROJECTIONS`, default on).
**`de.s7p` is shared**: scene-above answers `above`, `projections`, `subparts`, `action: scene | status` (and a bare
`de.s7p`), returns null/false otherwise; chat `/de s7p <words>` becomes `k=v` / `k v` / a lone action word for every S7p
feature (`S7pScene.ChatArgs`); from chat SE relays the route to Subnautica. **Rows**: SE `s7p.scene` (1 Hz and on every
command) {why, above, projections, subparts, missilesInSphere, missilesMirrored, missileEnters, missileLeaves,
missileCapDrops, projectionsMirrored, projectionEnters, projectionLeaves, projectionVerdicts {reason: n}, hiddenParts,
hiddenBlocks, transparencyMarks, spinners, spinnersMarked, spinRows, spinCapDrops, spinFieldMissing, toolParts,
toolPartsSent, toolPartRows, solidFiltered, patch}; Subnautica `s7p.above` (1 Hz and on a route change; fields under
`data`) {why, above, rowsSeen, rowsPlayed, rowsUnderwater, rowsMode, rowsNoEntity, rowsDeferredToFx, fxAbove,
readerMissed, readerOverflow, sustains, sustainCapDrops, rowsBudgetLeft, emits, emitsCapped, budgetCuts, missileViews,
missileTrailEmits, missileCapDrops, projectionViews, projectionRenderers, projectionSwaps, additive, alpha, hologram,
msLast, msMax, playedByType {type: n}}. Lines: `DE.S7p scene-above SE: …`, `DE.S7p scene-above SN: …`, `DE.S7p fx kit
ready: additive=… alpha=…`, `DE.S7p hologram material=…`, `DE.S7p projection <id> mirrored …`, `DE.S7p tool <type>: n
subpart(s) …`, `DE.S7p spin MISSING …`, `DE.Harmony S7p OK MyProjectorBase.SetTransparency(MySlimBlock, float) postfix`.

**C7 for S6 (wording only; layout 1.0 and its hash unchanged; offsets VERIFIED against `Records.cs` `SnStatus`).**
- 0x84 f32 `fixedDeltaTime` = `Time.fixedDeltaTime` sampled every frame (written since S0): 0.02 while the proxy
 stepper is registered (§2.6), 0.1 otherwise (S6.N7).
- 0x04 flags bit 10 `damageSelfTestPass`: set when the start-up self-check of §4.2 passed in this world; cleared at
 each world load until it passes again; never set while the Freedom gate fails or the `TakeDamage` patch is missing.
 Bit 2 `combatEnabled` keeps its S1 meaning (`!GameModeUtils.IsInvisible`).
- 0x78 `capabilities` bit 1 "creatures and damage": the S6 host is running, the `TakeDamage` prefix/postfix and the
 `CanDealDamageTo` postfix are installed and the **Freedom gate** passed: `GameModeUtils.GetGameMode(out mode, out
 cheats)` gives `mode == GameModeOption.Freedom` (= `NoSurvival`, 2) and `GameModeUtils.IsInvisible` (the
 effective `NoAggression` bit, Creative's or the `invisible` cheat's) is false; logged `DE.S6 freedom mode=Freedom
 invisible=false PASS` (else `REFUSED`, and the combat checks refuse to start: S6.N2). While the gate fails, sections
 still collide but no record is emitted (counted `refused`).
- 0x44 u32 `damageDropped` = the records the producer's overflow queue dropped, oldest first (`OverflowQueue.Dropped`,
 low 32 bits); 0x50 u32 `damageOverflowDepth` = the records waiting in it now (≤ 4,096).
- No proxy counters in C7 (layout 1.0 has none and S6 adds none): section, box, refresh, physics-time and hit counts
 are in Subnautica's `s6.*` rows and the `de.s6` status reply.

**C8 damage record (128 B):** 0x00 u32 dedupSeq; 0x04 u32 snGeneration; 0x08 u32 unit (1 = SN_HP); 0x0C f32 amount
(originalDamage); 0x10 f32 seScaleExpected; 0x14 u16 snDamageType; 0x16 u16 flags (bit 0 contact from the mouth
capture, 1 radius damage, 2 fallback hit point, 3 frame = subgrid); 0x18 u32 targetKind (1 grid section, 2
character); 0x1C u32 sectionId; 0x20 u64 targetEntityId (the grid or subgrid whose frame `localHit` is in, or the
character); 0x28 u64 rootEntityId; 0x30 u64 snapshotTick (SE tick of the pose that placed the section); 0x38 f32
localHit[3] (SE-local of `targetEntityId`); 0x44 f32 snWorldHit[3]; 0x50 f32 attackDirSn[3]; 0x5C i32 dealerTechType;
0x60 u32 dealerInstanceId; 0x64 u32 worldEpoch; 0x68 f64 snTimeSec; 0x70–0x7F pad.

*C8 field meanings.* One record per emitted `TakeDamage` call (§4.2), written by Subnautica's main
thread through `OverflowQueue<DamageRecord>.WriteOrQueue(EntryRing, …)` (the queue is flushed first every frame, so
order is kept).

| Offset | Field | Meaning |
|---|---|---|
| 0x00 | u32 `dedupSeq` | 1, 2, 3… per Subnautica generation, +1 per record built (a record later dropped from the overflow leaves a gap); 0 is never sent. Hit id = `(snGeneration, dedupSeq)`, written `gen:seq` in logs |
| 0x04 | u32 `snGeneration` | the writer's C0 Subnautica generation |
| 0x08 | u32 `unit` | 1 (`DamageUnit.SnHp`) |
| 0x0C | f32 `amount` | `originalDamage`, the first argument of `TakeDamage`; finite, > 0 |
| 0x10 | f32 `seScaleExpected` | the factor Subnautica expects: 25 for a section of a GridAdd `targetClass` 1 (large), 1.0 for 2 (small), 1.0 for the character |
| 0x14 | u16 `snDamageType` | Subnautica's `DamageType` value: Normal 0, Explosive 7, Electrical 8 (nothing else is sent; enum VERIFIED `DamageType.cs`) |
| 0x16 | u16 `flags` | bit 0 `MouthContact`, 1 `Radius`, 2 `FallbackHitPoint`, 3 `FrameSubgrid` (layout-pinned `DamageFlags`); bit 4 `GhostPlayer` (S6 conventions: the hit was on `Player.main.liveMixin`); other bits 0 |
| 0x18 | u32 `targetKind` | 1 grid section, 2 character (`DamageTargetKind`) |
| 0x1C | u32 `sectionId` | the hit section's `SkinSection.sectionId`; 0 for the character |
| 0x20 | u64 `targetEntityId` | the grid or subgrid whose frame `localHit` is in (the subgrid itself for a subgrid section), or the C1 character id |
| 0x28 | u64 `rootEntityId` | the subgrid's GridAdd `parentEntityId`; the grid itself for a root grid; the character itself |
| 0x30 | u64 `snapshotTick` | the SE tick of the pose that placed the section at the last physics step |
| 0x38 | f32 `localHit[3]` | SE-local metres of `targetEntityId` (§2.7) |
| 0x44 | f32 `snWorldHit[3]` | `w`, SN-world (logs) |
| 0x50 | f32 `attackDirSn[3]` | unit vector from the dealer's point (mouth, ampeel, blast centre, dealer) to `w`; zero when there is none |
| 0x5C | i32 `dealerTechType` | `CraftData.GetTechType(dealer)` (from the context when `dealer` is null; 0 = None) |
| 0x60 | u32 `dealerInstanceId` | `(uint)dealer.GetInstanceID`, 0 when none (logs) |
| 0x64 | u32 `worldEpoch` | the world epoch of the mirrored data Subnautica used (its acknowledged hello's) |
| 0x68 | f64 `snTimeSec` | `Time.realtimeSinceStartup` at the hit (logs) |
| 0x70 | pad 16 | 0 |

SE's handling of every field is §4.1 "Application rules".

**S6 combat conventions.** Layout decision for
S6: **layout 1.0 suffices, no re-pin**. Every S6 record exists in it (C4 type 6 `SkinSetHeader`/`SkinSection`/
`SkinBox`, C8 `DamageRecord` with its 1,024-slot ring, C7 `fixedDeltaTime`, `damageDropped`, `damageOverflowDepth`,
flag bit 10, capability bit 1, the C1 character pose and bones); S6 adds meaning in fields and bits layout 1.0 does
not pin (`SkinBox.flags`, `DamageRecord.flags` bit 4, the section-id and centre formulas, the reject reasons, the
dedup window), pinned here as S5 pinned its own. `0x4f3a35f6fc1eaf31`, `Fixtures/layout-1.0.txt`,
`tests/wire/layout_py.py` and the S5 and swim-extension pins stay as they are. Code: Wire `CombatConventions` (the
constants, `CanonicalText`, `Hash`), `CollisionSkinMath`, `SkinCodec`, `DamageRules`; the Python twin in
`tools/worldmap/wire_ref.py` (`s6conventions`, `s6selftest`, `s6vectors`, `s6bound`).

*Canonical text* (exact; ASCII, `\n` after every line, the last included; `const` lines sorted by group, then value,
then name; `format` lines sorted by key), FNV-1a 64 **`0xb6ee880712f8253b`** (54 lines, 2,068 bytes):
```
ext s6 combat 1
const C8FlagsExt GhostPlayer 16
const CombatLimits RefreshMm 10
const CombatLimits SkinDebounceTicks 30
const CombatLimits CapsuleRadiusMm 250
const CombatLimits MaxShapesPerStep 256
const CombatLimits MaxAmountSnHp 500
const CombatLimits CapsuleHeightMm 1800
const CombatLimits MaxHalfExtentMm 4000
const CombatLimits OverflowRecords 4096
const CombatLimits DedupWindow 8192
const CombatLimits SkinMaxBytes 2097152
const DamageReject Generation 1
const DamageReject Epoch 2
const DamageReject Sequence 3
const DamageReject Duplicate 4
const DamageReject TooOld 5
const DamageReject Unit 6
const DamageReject Amount 7
const DamageReject Kind 8
const DamageReject Type 9
const DamageReject Entity 10
const DamageReject Section 11
const DamageReject Frame 12
const DamageReject HitPoint 13
const DamageReject NoBlock 14
const DamageReject NotDestructible 15
const DamageReject CannotDie 16
const DamageReject NotApplied 17
const DamageScale KChar 1
const DamageScale KSmall 1
const DamageScale Version 1
const DamageScale KLarge 25
const SkinBoxFlags Rotated 1
const SkinBoxFlags SourceShift 8
const SkinBoxFlags SourceMask 3840
const SkinBoxSource Segment 0
const SkinBoxSource ModelShapeObb 1
const SkinBoxSource Synthetic 15
const SkinLattice MaxBoxCellsLarge 3
const SkinLattice SectionCellsLarge 6
const SkinLattice SectionIdBits 10
const SkinLattice MaxBoxCellsSmall 16
const SkinLattice SectionCellsSmall 32
const SkinLattice SectionIdBias 512
const SnDamageForward Normal 0
const SnDamageForward Explosive 7
const SnDamageForward Electrical 8
format characterSection id 0, capsule r 0.25 h 1.8 centred on the C1 pelvis bone, axis pelvis->head
format hitId snGeneration:dedupSeq, dedupSeq 1.. per Subnautica generation
format seDamageType 0=DeepBite 7=DeepBlast 8=DeepShock
format sectionCentre (i*SectionCells+(SectionCells-1)/2)*GridSize per axis
format sectionId ((ix+512)<<20)|((iy+512)<<10)|(iz+512), i=floorDiv(roundAway(c/GridSize),SectionCells), c=box centre
format skinVersion 1.. per entity per stream, a SkinSet replaces the whole skin
```
Pinned by `CombatConventionsTests` (text byte for byte, hash, every value equal to the code's constants, the layout's
`DamageFlags`/`SkinSectionKind`/`DamageTargetKind` untouched) and by the Python twin. `DamageScale Version` must equal
the header's `damageScaleVersion` (`WireInfo.DamageScaleVersion` = 1). Changing a line bumps `ext s6 combat <n>`,
a change of a factor also bumps `damageScaleVersion` (§4.1, O7). Not
compared between peers at run time (both plugins ship the same `DeepEngineers.Wire.dll`).

*S6 control commands (C11, dev builds; the full argument lists are in the S6 status document).* SE `de.s6`
(`{status}`, `{skin: "<entityId>"}`, `{damage: {…}, apply}`, `{selftest}`, `{resend}`; chat `/de s6 …`) and the chat
relay `/de spawn <TechType> [n] [dist]` → Subnautica `de.spawn`. Subnautica `de.spawn {techType, count, dist}`:
`DevConsole.SendConsoleCommand("spawn <techType> <count> <dist>")` (VERIFIED `DevConsole.SendConsoleCommand(string)
→ Submit`, `SpawnConsoleCommand`: `TechType` parsed ignoring case, `CraftData.IsAllowed`, count default 1, `maxDist`
default 12; each object is placed by `Utils.CreatePrefab` at `camera + maxDist·forward`, or at the first hit of a
raycast on all layers along it — a proxy in front of the camera catches the spawn point); and `de.s6 {action:
"status" | "selftest" | "spawn" | "bt1" | "probe" | "paths" | "penetration" | "local" | "teardown", …}` (sn-s6: `local` builds the SN-side synthetic B-T1 hull — 50 boxes, or 1,000 for B-T1 (3) — looping at 5 m/s when no
stand-in producer sends a skin; `teardown` stops every session and removes the local hull and the de.spawn creatures;
`bt1` takes `proxy: "wire" | "local"` and `boxes`).

**C9 SnCreatures:** header 0x00 u32 seq; 0x04 u32 count; 0x08 u64 snFrame; 0x10–0x3F pad; records from 0x40, 48 B
each: 0x00 u32 instanceId; 0x04 i32 techType; 0x08 f32 posSn[3]; 0x14 f32 velSn[3]; 0x20 f32 aggression; 0x24 u32
flags (hunting, targetIsMirror); 0x28 u64 targetEntityId.

**C10:** request at +0x000 (256 B): 0x00 u32 seq; 0x04 u32 reqId; 0x08 u32 kind (1 next frame PNG, 2 first frame using
SE tick ≥ atSeTick); 0x0C u32 pad; 0x10 u64 atSeTick; 0x18 path (u16 length + bytes, relative to
`%LOCALAPPDATA%\DeepEngineers\captures\`). Result at +0x800 (256 B): 0x00 u32 seq; 0x04 u32 reqId; 0x08 u32 status (0
pending, 1 ok, 2 failed); 0x0C f32 alpha; 0x10 u64 snFrame; 0x18 u64 seTickUsed; 0x20 path.

**C11:** message type 1 = one JSON object per message, UTF-8: request `{"id": n, "cmd": "<name>", "args": {…}}`, reply
`{"id": n, "ok": true|false, …}`. Commands (dev only): `de.ping`, `de.version` (SE), `de.state`, `de.selftest`,
`de.versionTest` (SE: the S0.N12 test hook), `de.seeThrough on|off`
(the T-D1 toggle), `de.capture`, `de.teleport` (SE spectator to an SN point via `F⁻¹`), `de.time <dayFraction>` (the
plugin's own time command; never the game console), `de.spawn <TechType> <n> <dist>` (Subnautica, through
`DevConsole.SendConsoleCommand`), `de.stallSn <seconds>` (args `{"seconds": x}` or `[x]`), `de.quit` (Subnautica
only), `de.mutant <name> on|off` (args `{"name": "<id>", "on": true|false}`). A command relayed from SE chat (`/de sn
<cmd> …`) carries its words as `{"argv": [...]}` (S0, D28, D44). Added in S1 and used in the real runs (S1 D88, D104):
SE `de.site [repair|rebind]`, `de.teleport {x,y,z,yawDeg}`, `de.path`, `de.look`, `de.pixel`, `de.spawnplanet`,
`de.g2e3`; Subnautica `de.status`, `de.warpcam`, `de.capture`, `de.testSave`, `de.trace`, `de.underwater`, `de.pixel
{pointSn, minSeTick, label}` (answered `pending` at once, the result logged as `DE.SnPixel`). Delivery facts (S0/S1
real runs): SE drains C11a only while `devControl` bit 0 is set, and it re-syncs its C11a tail when it attaches or sees a new
peer, so a harness sends `de.ping` (repeated once per second) until SE answers before any real command
(`tools/lab/se_cmd.py` does; with `--to sn` it uses C11c/C11d, whose Subnautica tail is re-synced at every new link
epoch). S7a adds `de.s7` on both sides (§5.4 "S7a conventions"); while the S7a time drive holds Subnautica's clock its
`de.time` is refused.

**C12 messages:** 1 `BatchStatics`: 0x00 i16 bx; 0x02 i16 by; 0x04 i16 bz; 0x06 u16 pad; 0x08 u32 epoch; 0x0C u32
count; primitives from 0x10, 48 B each {0x00 u8 kind (1 box, 2 capsule, 3 sphere, 4 mesh OBB); 0x01 u8 flags; 0x02 u16
pad; 0x04 f32 centreSn[3]; 0x10 f32 qSn[4]; 0x20 f32 size[3] (box half extents; capsule radius, half height, axis;
sphere radius); 0x2C u32 classIdHash (FNV-1a 32 of the prefab class id, for logs and the allow-list)}. 2 `BatchClear`
(16 B): 0x00 i16 bx, by, bz; 0x06 u16 pad; 0x08 u32 epoch; 0x0C u32 pad. S7 (first producer): header `pad` 0x06 =
u16 `version` 1, primitive `flags` = `StaticFlags`, capsule `size` = (radius, half segment, axis), the reporter, the
hosts and the census (§5.4 "S7 conventions").

**S8a conventions.** Scope: ROADMAP
"### S8a" (drilling, the store, the window, the chart; rules and members in §3.3). **Layout decision for S8a: layout
1.0 suffices, no re-pin, no pin changed, O16 stays open.** The O16 rule as it stands: validation step 2 tolerates
another minor while step 3's hash covers every field and rejects it, so any layout 1.1 needs O16 decided first and a
coordinated re-pin of both plugins and `tests/wire/layout_py.py`; S8a needs none:

| S8a record kind | Carrier (layout 1.0) | What S8a adds (pinned below) | Why no layout 1.1 |
|---|---|---|---|
| Terrain notices SE → Subnautica (a batch file rewritten or cleared, a chart step, a store reset), each with the batch, the file version and the changed box | C2 region bytes **0x50000–0x5803F**: the S8a header (64 B) at 0x50000 and up to 1,024 rows × 32 B from 0x50040, inside the C2 seqlock SE writes every tick | `field s8a …`, `field notice …`, "feed.*" | the C2 tables end at `SeEntitiesLimits.End` = 0x39040 and S7's events at 0x4FFFF inside a 0x80000 region; a reliable C4 message type, a new ring or a new region would change the layout text (`message`, `ring`, `region` lines) and need O16 |
| Acknowledgement and status Subnautica → SE | C7 `SnStatus` pad bytes **0x8C–0xBB** (48 of the 116 bytes of `pad_8c`) | `field c7s8a …` | C7 is written every Subnautica frame; no extension uses its pad |
| Batch data and the drill overlay | files: the store (§3.3) and the SE save (`DE_S8a_overlay.vx2`) | "octree.batch", "store.*", "overlay.*" | a batch file is 0.2–10 MB: not wire traffic |

Reliability without a ring: SE repeats every unacknowledged notice until C7 `ackSeq` covers it; the level state lives in
the manifests; a full queue turns into a resync ("feed.*"). Free-space evidence: SE's C2 write sets the header fields,
the tables, the light rows, S7's event rows (0x40000 + 64·i, at most 1,024) and `pad_1c`, nothing at or past 0x50000;
Subnautica's C2 read copies the header, entities and subparts and lets each `IC2ExtensionReader` copy its own bytes
inside the read window, so a Subnautica plugin older than S8a never sees the S8a bytes; `SnStatus` declares
`pad_8c[0x74]` (`Layout.cs` `P(0x8C, 0x74)`) and no extension writes it. SE writes the S8a header (version 1) on every
C2 write while S8a is on and rows only while C7 `c7s8a.version` = 1 (a Subnautica plugin with S8a); version 0 otherwise.
No constant group of the layout, S5, S6, S7a or S7 gains a member, so `0x4f3a35f6fc1eaf31` (layout 1.0),
`0x832ba93ca25bc307` (swim), `0x31dfd6f9befdef5d` (S5), `0xb6ee880712f8253b` (S6), `0x4257c11c17ff772a` (S7a) and
`0x1f9b9b4a876a97f9` (S7) stay as they are. A stale header (an earlier SE generation's) is rejected by `tickLow` ≠ the
low 32 bits of the C2 header's `tick`. Code: Wire `S8aConventions` (constants, `CanonicalText`, `Hash`,
`CanonicalLines`, `CanonicalBytes`), `S8aFeed`, `S8aOctree`, `S8aConvert`, `S8aChart`, `S8aWindowPlan`, `S8aStore`;
Python twins `tools/planet/s8a_ref.py` (text, hash, codecs, chart, plan, keys; vectors `tests/s8a/vectors/`) and
`tools/sn-octrees/sn_octree_write.py` (the octree writer).

*Canonical text* (exact; ASCII, `\n` after every line, the last included; `const` lines sorted by group (ordinal), then
value, then name; then the `field` lines in the listed order; then the `format` lines sorted by key), FNV-1a 64
**`0x3188251ab11e2fec`** (155 lines, 14,696 bytes; `ext s8a terrain 6` since (planet v2, ores as voxels): convert.material's nine ore rows take this mod's own block types in the palette's null slots 95-122 and the new line "convert.orelook"; `ext s8a terrain 5` (`0xcbf4ca049d06c430`) since late night (Stage F2): "ring.field" and the store keys with the ring's identity ("store.key"); `ext s8a terrain 4` (`0x6edd82cc7194e678`) since: the C2 flag OffSite, "chart. `ext s8a terrain 3` (`0x4741bf09b81065a2`) since: the common ores' Subnautica types
`ext s8a terrain 2` (Stage C, `0x7f480768c4cb872f`): biome.names and
biome.rule gained the designed planet's biome map, convert.material its nine ore rows, sculpt.encode the G and B
channels, sculpt.site the authored layout and the island; `ext s8a terrain 1` was `0x3d52f1105b92a172`, 11,841 bytes):
```
ext s8a terrain 6
const C2S8a Version 1
const C2S8a RowSize 32
const C2S8a HeaderSize 64
const C2S8a MaxPerTick 64
const C2S8a MaxRows 1024
const C2S8a Offset 327680
const C2S8a RowsOffset 327744
const C2S8aFlags DrillOn 1
const C2S8aFlags WindowOn 2
const C2S8aFlags TreadmillOn 4
const C2S8aFlags NearPending 8
const C2S8aFlags NearEmpty 16
const C2S8aFlags Resync 32
const C2S8aFlags OffSite 64
const C7S8a Version 1
const C7S8a Size 48
const C7S8a Offset 140
const C7S8aFlags StoreOpen 1
const C7S8aFlags GetPathPatched 2
const C7S8aFlags LoaderPatched 4
const C7S8aFlags CenterWidened 8
const C7S8aFlags Refused 16
const Chart Site 0
const Chart Step0Batches 5
const Chart StepBatches 12
const Chart StepGapMs 2000
const Chart BoxInnerM 3200
const Chart AnchorMaxM 3840
const Chart ReturnM 4160
const Chart Chart0OutM 4800
const Chart BoxOuterM 7040
const NoticeFlags AllSolid 1
const NoticeFlags AllEmpty 2
const NoticeFlags Reuse 4
const NoticeFlags Final 8
const NoticeFlags Restore 16
const NoticeKind BatchWritten 1
const NoticeKind BatchCleared 2
const NoticeKind ChartStep 3
const NoticeKind StoreReset 4
const NoticeLayer Drill 1
const NoticeLayer Window 2
const Overlay Version 1
const Overlay CellVoxels 16
const Overlay MaxCells 65536
const Queue MaxReloadsInFlight 8
const Queue MaxCellReloadsPerNotice 64
const Queue MaxPending 4096
const SnWorld OriginY -3040
const SnWorld OriginX -2048
const SnWorld OriginZ -2048
const SnWorld FileVersion 4
const SnWorld OctreesPerBatch 5
const SnWorld BatchesY 20
const SnWorld BatchesX 26
const SnWorld BatchesZ 26
const SnWorld OctreeVoxels 32
const SnWorld OctreesY 100
const SnWorld NearZero 126
const SnWorld OctreesX 128
const SnWorld OctreesZ 128
const SnWorld BatchVoxels 160
const Spawn RadiusM 150
const Spawn DespawnM 250
const Store CoalesceMsPerMB 50
const Store CoalesceMs 250
const Store KeepWindowM 1600
const Store MaxMB 2048
const Store GraceMs 10000
const Window UniverseMin -35
const Window CraterMin 0
const Window LookAheadBatches 1
const Window RingBatches 5
const Window StreamCenterDownM 8
const Window NearBoxM 16
const Window CraterMax 25
const Window UniverseMax 60
const Window NearSurfaceM 150
const Window BiomeRadiusM 200
const Window LookAheadMinSpeedCmS 500
field s8a version 0x0 1 u8
field s8a flags 0x1 1 u8
field s8a rowCount 0x2 2 u16
field s8a firstSeq 0x4 4 u32
field s8a stream 0x8 4 u32
field s8a dropped 0xc 4 u32
field s8a tickLow 0x10 4 u32
field s8a resyncEpoch 0x14 4 u32
field s8a storeKey 0x18 8 u64
field s8a overlayGen 0x20 4 u32
field s8a chartSeq 0x24 4 u32
field s8a cameraBiome 0x28 1 u8
field s8a pad 0x29 1 u8
field s8a nearPendingCount 0x2a 2 u16
field s8a seabedBelowCamM 0x2c 4 f32
field s8a windowGen 0x30 4 u32
field s8a drillGen 0x34 4 u32
field s8a chartKey 0x38 8 u64
field notice seq 0x0 4 u32
field notice kind 0x4 1 u8
field notice layer 0x5 1 u8
field notice flags 0x6 2 u16
field notice bx 0x8 2 i16
field notice by 0xa 2 i16
field notice bz 0xc 2 i16
field notice pad 0xe 2 u16
field notice version 0x10 4 u32
field notice min 0x14 3 u8[3]
field notice max 0x17 3 u8[3]
field notice octrees 0x1a 2 u16
field notice cells 0x1c 4 u32
field c7s8a version 0x8c 1 u8
field c7s8a flags 0x8d 1 u8
field c7s8a reloadsPending 0x8e 2 u16
field c7s8a ackSeq 0x90 4 u32
field c7s8a appliedSeq 0x94 4 u32
field c7s8a stream 0x98 4 u32
field c7s8a resyncEpoch 0x9c 4 u32
field c7s8a chartSeqApplied 0xa0 4 u32
field c7s8a batchesServed 0xa4 4 u32
field c7s8a reloadFailures 0xa8 4 u32
field c7s8a lastLatencyMs 0xac 2 u16
field c7s8a nearPendingSeen 0xae 2 u16
field c7s8a storeKey 0xb0 8 u64
field c7s8a pad 0xb8 4 u32
format biome.names 0 safeShallows, 1 grassyPlateaus, 2 kelpForest, 3 dunes, 4 bloodKelp, 5 sparseReef, 6 mountains, 7 void, 8 mushroomForest, 9 grandReef, 10 underwaterIslands, 11 crashZone, 12 lostRiver_Canyon; 255 none (Subnautica's own answer)
format biome.rule cameraBiome from the window column under the camera: the planet's biome map (the material map's G, PlanetMaps Biome) at the surface point when it holds 1..13: G - 1; else by the surface material: Grass 6; Sand_02 0 when the seabed is <= 60 m deep, else 1; Stone_01 2; Stone_02 3; Stone_03 4; Stone_04 5; Stone_05 or deeper than 1000 m 7; GetBiome(p) outside the crater box returns names[cameraBiome] within BiomeRadiusM of the camera, else void
format chart.anchor chart k+1: anchor = the camera's point on the sea sphere; E0', N0' = FrameAt_k(camera), so mapdir is continuous at the camera; psi kept; O' = F_k(camera) - T horizontally (O'.y = 0), so F_k+1(camera) = F_k(camera) - T exactly; a T = 0 step re-centres and keeps the camera's SN position
format chart.apply SE swaps SiteBinding.Map at the tick it queues the ChartStep notice (C1, C2 and the S8a header of that tick are all under chart k+1); Subnautica never interpolates across a chartSeq change; SE ignores Subnautica-positioned input (C9 positions, C12 batches) while C7 chartSeqApplied != chartSeq
format chart.offsite C2 flag OffSite while the chart index is >= 1; SE keeps its picture policy (Subnautica's picture below the altitude band, as on chart 0); Subnautica hides the Subnautica map's above-water landmarks (the Aurora, the crater box's terrain above the sea, the horizon skirt) while OffSite is set and restores them when it clears; on a ChartStep Subnautica serves the previous chart's window files under the shifted ids (new id = old id - T) until the new chart's notices arrive
format chart.step chart 0, TreadmillOn only: site-v1 rhoInf > Chart0OutM -> chart 1 with T = Step0Batches*160 times the sign of the larger of |x|, |z| along that axis; chart k >= 1 with the camera at SN (x, z): |x| > BoxOuterM -> T = StepBatches*160*sign(x) along x, likewise z; |x| < BoxInnerM and |z| < BoxInnerM -> T = -StepBatches*160*sign along the larger axis; arc distance from the anchor > AnchorMaxM -> T = 0; one step per tick, an automatic step no sooner than StepGapMs after the previous one; back to chart 0 when the camera's site-v1 rhoInf <= ReturnM
format convert.copyonwrite drill layer: SN voxel v is recomputed iff its 8-sample SE stencil intersects an edited SE range of the tile overlay; else it keeps its base value (the newest store version of the batch, else the install file); T = the base type when the base voxel is solid, else the mapped SE material
format convert.material SE voxel material subtype -> SN block type: Sand_02 1, Stone_01 111, Stone_02 150, Stone_03 190, Stone_04 141, Stone_05 84, Grass 219, Iron_02 95, Nickel_01 96, Silicon_01 107, Cobalt_01 108, Magnesium_01 117, Silver_01 118, Gold_01 119, Platinum_01 121, Uraninite_01 122, any other 84; names resolved to indices once per session
format convert.orelook Subnautica's palette slots 95, 96, 107, 108, 117, 118, 119, 121 and 122 (null in the installed palette) hold this mod's ore block types for Iron_02, Nickel_01, Silicon_01, Cobalt_01, Magnesium_01, Silver_01, Gold_01, Platinum_01 and Uraninite_01 in that order: a WorldStreamer.Start prefix fills each (filled, the layer of the template rock GreyRock, its material cloned with this mod's generated ore texture); a slot the game's palette already fills is left alone (the ore then shows that type)
format convert.sample SN voxel v (world index) -> P = (OriginX, OriginY, OriginZ) + v + 0.5; w = F^-1(P) (site v1 for the drill layer, the current chart for the window); D = trilinear over the 8 LOD0 samples (content/127.5 - 1) around w - PositionLeftBottomCorner; s = clamp(D, -1, 1); |s| >= 1: density 0, type = s > 0 ? T: 0; else density = floor(s*126) + 126, type = s >= 0 ? T: 0
format feed.ack rows = SE's unacknowledged notices from C7 ackSeq + 1 of the same stream, seq consecutive from firstSeq, at most MaxPerTick new rows per tick and MaxRows in all; Subnautica applies seq > its last in order, writes ackSeq (reload scheduled) and appliedSeq (batch loaded and the collision level built over the row's box); tickLow = low 32 bits of the C2 tick; stream changes when seq restarts
format feed.resync a full queue (MaxPending) drops the oldest unacknowledged notices into dropped, bumps resyncEpoch and sets Resync; Subnautica then re-reads both manifests and reloads every loaded batch whose served version differs; StoreReset (new storeKey, new overlay or new chartKey) clears that layer's lookup first
format guard.lift while DrillOn every S4 edit guard lets the edit through on a tile: it lands in MyOctreeStorage's own leaves over the provider, never in the provider; each edited range is mirrored by WriteRange into every other tile containing it and marks the overlay cells it touches; DrillOn off = S4's guards
format octree.batch int32 version 4; octrees x-outer, y, z-inner; crater ids store only octrees inside (OctreesX, OctreesY, OctreesZ), window ids all 125; each a u16 node count then 4-byte nodes (type, density, u16 firstChild, 0 = leaf); child c = 4*xbit + 2*ybit + zbit
format octree.write 32^3 raster -> nodes bottom-up: CanCollapse (8 leaf children, not mixed below/above the surface, near children all equal) -> a leaf with the first near child's value, else child 0's; otherwise interior = CacheDownsampledType (density = integer mean of near children, else 0; type = ComputeMostCommonChildType); layout CompactOctree.SetInternal (pre-order, 8-node blocks from 1)
format overlay.file DE_S8a_overlay.vx2 in the SE save = deflate(magic DES8AOV1, u32 version, u32 siteVersion, u32 cellCount, cells sorted by key: u8 i, j, k, pad, u8 cx, cy, cz, pad, content[4096], material[4096] x fastest) + u64 fnv1a64 of the uncompressed bytes; cell = 16^3 LOD0 voxels of tile (i, j, k)
format overlay.persist postfix MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>) when it returns true: snapshot.CompressedVoxelSnapshots[DE_S8a_overlay] = the file; restored in BeforeStart after the tiles by WriteRange(Content|Material, notify) under the restore flag
format ring.field Stage F2 (DEEPENGINEERS_S8A_RING=on): MyPlanetStorageProvider.ReadRange(ref MyVoxelDataRequest, bool) of the site planet serves the ring field (RingField version 2, twin tools/planet/s8a_ring3d.py) for content reads at LOD <= 3 whose box meets SN radius 3530..12270 m and the field's reach over and under the heightmap seabed; content = RingField.ServeBox at the planet-local voxel positions (storage position * 2^lod - StorageSize / 2, site v1 with C = 0, the arctan a polynomial), materials SE's own; every other read, a failure and a field not ready: SE's own generator (the heightmap), which is also the field's base
format sculpt.encode heightmap h = clamp(round((60000.00390625 + y - 57000) / 10200 * 65535), 0, 65535) for an SE surface at SN altitude y; pixel p of a 2048 face <-> texcoord p / 2047; faces 0 front, 1 back, 2 left, 3 right, 4 up, 5 down (MyCubemapHelpers); _mat R = CustomMaterialTable value (Sand_02 1.. Stone_05 6, Grass 7), G = biome.names index + 1 (0 none), B = OreMappings Value (0 none), A = 255; PlanetMaps Material, Ores and Biome on
format sculpt.site every pixel whose site-v1 rhoInf <= 2450 m is 0, so the bicubic surface is exactly SN y -3000 within rhoInf 2300 m (the provider tiles); tools/planet/layout_4546b.json shapes the rest once (biome seeds scaled x3 to a 12 km ring, per-biome depth bands and relief, canyons, the Mountains island, the ore depth-rarity table; a seed varies the detail layer only); every surface outside the island stays at least 2 m below the sea, the island tops at most +70 m
format spawn.place window columns only, keyed by fnv1a64(chartKey, bx, bz, column, row index); seabed = the top solid voxel of the served file; within RadiusM of the camera, gone beyond DespawnM; MakeEntityTransient, never saved; moved by -T on a chart step
format spawn.table per hectare of seabed by depth d, Subnautica TechTypes: d <= 60 Peeper 3 Bladderfish 3 Boomerang 2 BluePalm 6 PurpleTentacle 4 CoralShellPlate 3; d <= 200 Hoopfish 3 Spadefish 2 Reginald 2 RedGrass 8 PurpleFan 3 SmallKoosh 2; d <= 500 Eyeye 2 HoleFish 2 Mesmer 1 BloodGrass 6; d <= 1000 Spinefish 2 GarryFish 1 BloodGrass 2; deeper none
format store.key storeKey = fnv1a64(utf8 lower(world folder path at load) + '|' + siteVersion); chartKey = fnv1a64(utf8 'chart|' + U0, N0, psiDeg, O as R round-trip doubles joined by '|' + '|' + sculptHash x16); sculptHash = fnv1a64 over 'name<TAB>bytes<TAB>fnv1a64 x16<LF>' of the 12 planet data files in ordinal order; with the ring field on (Stage F2) the window's and the charts' sculptHash is fnv1a64(utf8 sculptHash x16 + '|ring|' + ringId x16), ringId = fnv1a64(utf8 'ring|' + rules x16 + '|' + library x16 + '|' + index x16): rules = fnv1a64 of the ring rules text (RingRules.Text), library and index = fnv1a64 of library-v1.bin and index-v1.tsv in the brush store ('missing' for a file not built)
format store.manifest manifest.v1.txt per layer folder: line 1 'de-s8a-store 1 <drill|window> <key x16> <overlayHash|sculptHash x16>'; then '<bx> <by> <bz> <version> <bytes> <fnv1a64 x16> <flags> <file path relative to the terrain folder>' per served batch, sorted by bx, by, bz; rewritten at most once per second and at unload (temp file then replace)
format store.path %LOCALAPPDATA%\DeepEngineers\terrain\<storeKey x16>\drill\ and %LOCALAPPDATA%\DeepEngineers\terrain\<storeKey x16>\window\<chartKey x16>\, files compiled-batch-<bx>-<by>-<bz>.v<version>.optoctrees and manifest.v1.txt; a version is never overwritten; nothing inside either game install or SNAppData
format window.center BatchOctreesStreamer constructor prefix, minLod 0 only: settings.centerMin = (UniverseMin, 0, UniverseMin), settings.centerMax = (UniverseMax, 20, UniverseMax); octreeBounds unchanged
format window.loader BatchOctrees.LoadOctrees prefix for ids outside the crater box (bx or bz outside CraterMin..CraterMax): read GetPath(id), version >= FileVersion, all 125 octrees in file order (Octree.Read, then UnloadChildren(streamer.minLod)); a missing or bad file answers false (empty water); crater ids run the original
format window.ready NearPending while a window id meeting the camera's +-NearBoxM box is neither written nor known empty and SE's surface is within NearSurfaceM of the camera; Subnautica's areaBuilt = its own value and not NearPending and every window notice meeting that box applied; NearEmpty (all such ids empty or absent) lets areaBuilt ignore unbuilt empty clipmap cells
format window.set c = the octree ring centre as WorldStreamer.UpdateStreamingCenter computes it (camera - StreamCenterDownM in y, FloorDiv 160, clamped to the widened centre); ids within RingBatches^3 around c plus LookAheadBatches ahead along the horizontal velocity when |v| >= LookAheadMinSpeedCmS; crater ids excluded; nearest first
```
Pinned by `S8aConventionsTests` (text byte for byte, hash, line and byte counts, every value equal to the code's
constants, the header and rows inside the C2 region past S7's events, the C7 block inside `pad_8c`) and by the Python
twin. Field names in the text
`s8a` = the S8a header at C2 region offset 0x50000 (offsets relative to it), `notice` = a row at 0x50040 + 32·i
(offsets relative to the row), `c7s8a` = C7 `SnStatus` (absolute offsets).

*C2 S8a header (0x50000, 64 B; written by SE inside the C2 seqlock, `S8aFeed.WriteInto`).*

| Offset | Type | Name | Value |
|---|---|---|---|
| 0x00 | u8 | `version` | 1 (0 = not written: an SE before S8a or S8a off; Subnautica ignores the block) |
| 0x01 | u8 | `flags` | `C2S8aFlags`: DrillOn 1, WindowOn 2, TreadmillOn 4, NearPending 8, NearEmpty 16, Resync 32 |
| 0x02 | u16 | `rowCount` | rows that follow (≤ 1,024) |
| 0x04 | u32 | `firstSeq` | seq of the first row (the oldest unacknowledged notice) |
| 0x08 | u32 | `stream` | changes when SE restarts its seq (a new world or SE generation) |
| 0x0C | u32 | `dropped` | notices dropped by a full queue since the stream began |
| 0x10 | u32 | `tickLow` | low 32 bits of the C2 header's `tick` |
| 0x14 | u32 | `resyncEpoch` | bumped with every resync |
| 0x18 | u64 | `storeKey` | "store.key" |
| 0x20 | u32 | `overlayGen` | bumped whenever the overlay's dirty-cell set changes |
| 0x24 | u32 | `chartSeq` | 0 = the site chart; + 1 per step |
| 0x28 | u8 | `cameraBiome` | "biome.names" index, 255 = none |
| 0x29 | u8 | `pad` | 0 |
| 0x2A | u16 | `nearPendingCount` | window ids of the near box not yet written |
| 0x2C | f32 | `seabedBelowCamM` | distance from the camera down to SE's surface (NaN unknown) |
| 0x30 | u32 | `windowGen` | window files written this stream |
| 0x34 | u32 | `drillGen` | drill files written this stream |
| 0x38 | u64 | `chartKey` | "store.key" (the window folder of the current chart) |

*Notice row (0x50040 + 32·i, 32 B).*

| Offset | Type | Name | Value |
|---|---|---|---|
| 0x00 | u32 | `seq` | consecutive from `firstSeq` |
| 0x04 | u8 | `kind` | `NoticeKind`: BatchWritten 1, BatchCleared 2 (no file: water), ChartStep 3, StoreReset 4 |
| 0x05 | u8 | `layer` | `NoticeLayer`: Drill 1, Window 2 (0 for ChartStep) |
| 0x06 | u16 | `flags` | `NoticeFlags`: AllSolid 1, AllEmpty 2, Reuse 4 (served from the previous chart's file under the shifted id until regenerated), Final 8, Restore 16 (rebuilt from the overlay at load) |
| 0x08 | i16 ×3 | `bx`, `by`, `bz` | batch id; ChartStep: T in batches (`bx`, `bz`), `by` 0 |
| 0x0E | u16 | `pad` | 0 |
| 0x10 | u32 | `version` | the file's version; ChartStep: the new `chartSeq`; StoreReset: the layer's new generation |
| 0x14 | u8 ×3 | `min` | batch-local voxel box (0–159) of the change, inclusive |
| 0x17 | u8 ×3 | `max` | |
| 0x1A | u16 | `octrees` | octrees rewritten (≤ 125) |
| 0x1C | u32 | `cells` | Subnautica voxels whose value changed |

*C7 S8a block (0x8C–0xBB, written by Subnautica with every C7 record, `S8aTerrain.FillStatus`).*

| Offset | Type | Name | Value |
|---|---|---|---|
| 0x8C | u8 | `version` | 1 (0 = a Subnautica plugin without S8a: SE writes no rows) |
| 0x8D | u8 | `flags` | `C7S8aFlags`: StoreOpen 1, GetPathPatched 2, LoaderPatched 4, CenterWidened 8, Refused 16 |
| 0x8E | u16 | `reloadsPending` | batch reloads scheduled and not applied |
| 0x90 | u32 | `ackSeq` | highest contiguous seq received and scheduled (same `stream`) |
| 0x94 | u32 | `appliedSeq` | highest contiguous seq whose batch is loaded and whose box is built at the collision level |
| 0x98 | u32 | `stream` | echo of the header's `stream` the acks refer to |
| 0x9C | u32 | `resyncEpoch` | the last resync handled |
| 0xA0 | u32 | `chartSeqApplied` | the chart Subnautica's poses and window refer to |
| 0xA4 | u32 | `batchesServed` | `GetPath` answers from the store since start |
| 0xA8 | u32 | `reloadFailures` | files refused or reloads timed out |
| 0xAC | u16 | `lastLatencyMs` | notice seen → applied, last notice |
| 0xAE | u16 | `nearPendingSeen` | window notices meeting the near box not yet applied |
| 0xB0 | u64 | `storeKey` | the store Subnautica serves from (0 = none) |
| 0xB8 | u32 | `pad` | 0 |

*S8a control commands (C11, dev builds).* SE `de.s8a {action: "status" | "drill" | "overlay" | "save" | "window" |
"step" | "chart", …}`: `drill {sn: [x, y, z], radius, path: "sphere" | "hand"}` (the explosion path
`PerformCutOutSphereFast` or the hand-drill path, at F⁻¹ of the point, on the tile there or the planet), `overlay
{cells: true}` (dirty cells, hash), `save` (`Sandbox.Game.Screens.Helpers.MyAsyncSaving.Start(Action callbackOnFinished = null, string customName = null)`,
the game's own save, on the game thread; refused unless the world folder holds `DE_THROWAWAY.txt`, as S7.M8), `window {throttleMs, on}`, `step {axis, sign}`
(forces a treadmill step), `chart` (current chart, key, anchor). Subnautica `de.s8a {action: "status" | "probe" |
"rays" | "verify" | "reload" | "spawns", …}`: `probe {sn}` (type and density of a voxel from the served file), `rays
{columns: [[x, z], …], label}` (layer-30 rays after `Physics.SyncTransforms`, S4's camera hold), `verify {bx, by, bz}`
(FNV of the served file), `reload {all | bx, by, bz}`, `spawns {on, clear}`. Mutants: `de.mutant S8a.Mx on|off` on the
side that owns them (ROADMAP "### S8a").

**S11 conventions.** Scope: ROADMAP §14
"### S11 Creatures and hazards", docs/S11-BRIEF.md. Layout decision: **layout 1.0 suffices, no re-pin, and `ext s6 combat 1`
stays as pinned**. S11 adds meaning where layout 1.0 and the earlier extensions leave room: C8 records whose `unit` is not
1 (S6's SN_HP damage keeps unit 1, its checks, factors and reject reasons untouched), C8 `flags` bits 8–9, and the `extra`
pad of C2 Explosion event rows (0 in `ext s7p fx 1`, which keeps its meaning for Thruster and BulletStreak). Code: Wire
`S11Conventions` (the constants, the tables, `CanonicalText`, `Hash`), `S11Rules` (`S11Danger`, `S11WeaponTable`,
`S11BlastRules`, `S11BlastChain`, `S11GrabRules`, `S11PushRules`, `S11HazardRules`, `S11HealRules`, `S11OxygenRules`,
`S11EmpRules`, `S11Records`, `S11RateWindow`); the Python twin `tools/s11/s11_ref.py` (`--selftest`, `--vectors` into
`tests/s11/vectors/`). The tables are defaults: the weapon table is retuned live with `de.s11 weapon` (dev), which never
changes the pin.

*C8 S11 units (one record per event, exactly once).* Every S11 record is built by Subnautica's S6 `DamageSink` (the same
`dedupSeq` numbering per generation, the same 4,096-record overflow queue, the same ring), so SE's one dedup window covers
S6 and S11 together. SE's drain sends a record whose `unit` is 17–23 to the S11 consumer **before** S6's payload check;
the consumer runs S6's `CheckHeader` (Generation, Epoch) and the shared `DedupWindow.Admit` (Sequence, Duplicate, TooOld:
counted under S6's `DamageReject`), then `S11Records.CheckPayload` (`S11Reject` Unit 1, Amount 2, Kind 3, Type 4, Vector
5, Entity 6, NotForwarded 7; MissileGone 18 when SE no longer has the missile), then the target (the exported C1 character for kind 2, a mirrored grid with a sent skin for
kind 1: `Entity`), then the per-unit gate and application:

| unit | Subnautica writes it when | fields | SE applies (game thread) |
|---|---|---|---|
| 17 `HazardHp` | the ghost Player's or the character section's `LiveMixin.TakeDamage` with a type of the hazard table (S6's emit rule `TypeNotForwarded`), or an S11 sampler (temperature, lava contact) | amount SN_HP ≤ 500; `snDamageType` the Subnautica type; kind 2; flags `GhostPlayer` / `ViaSection` / `ViaSampler` | `MyCharacter.DoDamage(amount × k, "DeepHeat"…, true, 0)` gated like S6 (`CharactersCanDie`, `CharacterCanDie`: `CannotDie`), capped per type at `capPerS` SE HP per 1,000 ms (`S11RateWindow`; the excess counted `capped`); the HUD notice of the row's effect at most every 5 s |
| 18 `HealHp` | `LiveMixin.AddHealth(float)` on `Player.main.liveMixin` (Subnautica's first-aid kit: `Survival.Use` → `AddHealth(50)`) | amount SN_HP ≤ 500; kind 2 | `StatComp.Health.Increase(min(amount × k, Max − Value), null)` (`S11HealRules`), capped 100 HP/s; dead characters are not healed |
| 19 `OxygenS` | a `Bubble` touching the character capsule (`Bubble.oxygenSeconds`), an `OxygenArea` trigger around it (`oxygenPerSecond × dt`, summed per 250 ms), the character inside a powered Subnautica base (30 s/s, `OxygenManager`'s surface rate) | amount SN seconds ≤ 120; `snDamageType` = OxygenSource; kind 2 | `MyCharacterOxygenComponent.SuitOxygenAmount = min(capacity, amount + seconds / 45 × capacity)` (the S2b tank mapping) |
| 20 `EmpS` | `EMPBlast.OnTouch(Collider)` on a grid section (the crabsquid's blast) once per blast per grid | amount seconds (`disableElectronicsTime` × ‰, 1..30); kind 1 | every enabled electricity producer of the grid (`MyResourceSourceComponent` with `ElectricityId`, a `MyFunctionalBlock`) is switched off and switched on again after the time (only those S11 switched off; all of them at world unload); a save inside the window writes them on (postfix on `MyFunctionalBlock.GetObjectBuilderCubeBlock(bool)`, ); `NoPower` when the grid has none |
| 21 `Grab` | a reaper's (`ReaperMeleeAttack.OnTouch`) or a sea dragon's (`SeaDragonMeleeAttack.OnTouchFront`) touch on a section of a grid whose own box fits the creature's size class, under the creature's own grab conditions | `snDamageType` = GrabPhase (1 start, 2 hold every 20 ms (catch-up: every frame at 60 fps), 3 release); `snWorldHit` the attach point (`seamothAttachPoint` / `exosuitAttachPoint`), `attackDirSn` its forward, `localHit` its up; `dealerInstanceId` the grab key; start: amount = the planned hold in s | start: SE re-checks the size class with the grid's own `PositionComp.LocalAABB` (`TooLarge`), one creature per grid (`AlreadyHeld`), dynamic grids only (`Static`), no remnant (`Debris`); the hold is kinematic (`format hold.LocalAABB` centre), its target the attach point reached from the anchor's start position over the row's `blendMs` (1 s / 0.25 s, the game's own lerps); every SE tick the anchor is measured against that target led by the mouth's velocity, over 1 m the root grid's matrix is set (`SetWorldMatrix`, the anchor on the target, the orientation attach × relative-at-start), then the root grid's linear and angular velocities are set so the anchor moves with the mouth (SE's physics resolves contacts); seeded failure S11.M12 restores the spring-damper force of `format hold.force`, S11.M13 the bite-point anchor; release on the release record, on 1,000 ms without a hold record, when the grid or the world goes, or at the surface (`format surface.release`: the Water Mod depth at the top of the grid's bounds under 2 m; SE then drops that key's later records quietly). Review of (`ext s11 creatures 3`): after the blend the grid lies as Subnautica lays a Seamoth; a grid with mechanical subgrids is refused (`Subgrids`: only the root would move);5 s later an `s11.grab` "after" row reports its speed and depth. Subnautica refuses a static grid and lets go itself when the held grid's proxy stays more than 15 m from the mouth for 1 s after a 1.5 s grace |
| 22 `Push` | a bite (S6 record via the mouth) of a push-table creature on a grid it does not hold | amount N·s (creature mass × closing speed × ‰); `attackDirSn` the direction | `AddForce(APPLY_WORLD_IMPULSE_AND_WORLD_ANGULAR_IMPULSE, dir × min(J, m × maxDv), bite point, null)` on the root grid |
| 23 `MissileHit` | Subnautica's sweep of a mirrored SE missile met a live creature (`format missile.hit`, `S11Missiles`) | kind 1; target = root = the missile's SE entity id; `snWorldHit` the hit point, `attackDirSn` the flight direction, the creature as dealer; amount 1; `snDamageType` HitBody 0 / HitHolder 1 (`format c8.s11missile`) | `Behind` when the hit lies over 1 m behind `MyMissile.Origin` along its flight; a body hit: the `MyMissile` moved to the hit point (`PositionComp.SetPosition`) and exploded: its own explosion and Explosion row there; a holder hit (the missile left the grid the creature holds; its hit point lies inside that grid): exploded where it is with `ExplosionDamage` 0 and one synthetic Explosion row of its damage at the hit point (the player's hull untouched); a health-pool slug (railgun, artillery) one synthetic row of `HealthPool` × 2 % at 3 m (no SE damage); `MissileGone` when it exploded elsewhere first |

The hold damage of a held grid travels as ordinary S6 bite records (unit 1, Normal, the creature as dealer, every
`intervalMs`): SE applies them through S6's path unchanged.

*"creatures never leave the water with a grid"; "the rockets go through them").* **Rage** (`format rage.hold`, the `rage` rows, live `de.s11 {action: rage}`): the SN_HP an SE weapon deals a holding creature (S7p's path through `danger.applied`) accumulates per hold; at 30 % of a reaper's `LiveMixin.maxHealth` (35 % for a sea dragon;Scared.Add(1)`, Aggression held at 0, `LastTarget.SetTarget(null)`, `SwimBehaviour.SwimTo` away, no grab); a lower hit makes it angrier once (hold damage × 1.5, the hold + 4 s / + 2 s, `Aggression.Add(1)`); each once per hold; the game's own Electrical/Poison release stays. **Surface** (`format surface.release`): Subnautica's creatures stay below by physics, and nothing of theirs follows a target out of the water except momentum; S11 releases a hold when the creature's top (the higher of its root and its jaw plus its girth: in-game 144924 the bounds' top of a pitched 165 m reaper stood 13 m out of the water with its body under it) or the grid's comes within 2 m of the surface (both sides), clamps the steering target of a creature that holds, flees or hunts an SE grid section to its girth + 2 m deep at the last door, `SplineFollowing.GoTo`, and at every physics step stops its upward speed while its top is within 2 m; while it holds, its animation plays at 1 / its scale; the S11 `S11FollowWatch` is the grid-follows-the-mouth watchdog, not a creature follow. **Missiles** (`format missile.hit`): SE missiles explode only on SE contacts or at their range and the creatures exist only in Subnautica (the S6 proxies are SE grids mirrored into Subnautica; nothing of a creature exists in SE), so rockets flew through every creature; and an explosion's damage was measured to the creature's root transform (tens of metres inside a x3 reaper's hull). Now Subnautica sweeps the mirrored missiles (unit 23 above; a fast slug's flight before its mirror is cast at its second pose, speed × 100 ms), the struck creature takes the explosion at d = 0, every other creature at the distance to its nearest collider (`format blast.damage`; a non-convex mesh collider by its bounds), and a gatling round fired from inside a creature, or from the grid it holds and aimed at it, hits it. Seeded failures S11.M13 (the bite anchor: S11.C6), S11.M14 (no surface rule, both sides: S11.C7), S11.M15 (the old hit test: S11.R1–R3), S11.M16 (no rage: S11.R4, R5).

*Weapon classes (A).* Subnautica resolves SE's C2 weapon, explosion and tool rows against its creatures (S7p-gameplay,
§4.2); S11 sets the class of each: a row attached to the C1 character is `Handheld`, to a grid `SmallGrid` or `LargeGrid`
by its GridAdd class, an Explosion row by its origin. The creature's tier comes from its `LiveMixin.maxHealth` read at run
time (≥ 2,000 `Leviathan`, ≥ 200 `Large`, else `Small`); the amount dealt is `format danger.applied`. Explosion rows: the
yield is `format event.yield` (SE's own explosion damage: a warhead 15,000, a 200 mm missile 500), the damage
`format blast.damage`; blast damage calls are exempt from S7p's 32-calls-per-frame cap.

** The scale table sets a leviathan's root
5, juvenile ghost ×1.8, Sea Dragon ×2.5;5 would be better": Reaper ×2.5 again, its grab class 400 m³ / 15 m and push cap 600 cm/s with it (`ext s11 creatures 5`); the grab classes and push caps scale with them; the Sea Emperor and every
growing or aquarium creature untouched): absolute, so a creature saved scaled never compounds, and re-asserted because
`Creature.Start` resets a non-growing creature to one. Unity scales its colliders (body, mouth
trigger), its child transforms (`seamothAttachPoint`, `exosuitAttachPoint`) and the animated skeleton with the root; the
`LiveMixin` health is data and stays; the swim forces and the rigidbody mass are not scaled (the push uses mass × scale³).
SE multiplies the S6 Normal hits of a table creature on grids (its bites and its grab's per-second hold damage) by its

takes a share of a typical functional small-grid block of 2,000–5,000 integrity (reaper 80 × 11 = 880, a quarter; ghost
85 × 14, a third; juvenile 85 × 7; sea dragon × 5.8 for half of one with an assumed 300 SN_HP bite, its prefab value
logged live in `s11.scale` as `biteSn`); never on the character (S6's value). Retuned live: Subnautica
`de.s11 {action: scale, creature, factor}`, SE `de.s11 {bite: {creature, mult}}` (0–50).

** A grid with fewer than 8 blocks or
a box under 8 m³ is debris (`format debris.grid`; his session's remnants were split pieces of 1–4 blocks, the reaper held a
4-small-block one and a single large block): Subnautica switches its sections' EcoTarget off (no creature hunts it), a
priority-last `MeleeAttack.CanDealDamageTo` postfix refuses bites on it, no grab starts and no push; SE refuses a grab start
or a push on it with `S11Reject.Debris` (exact `MyCubeGrid.BlocksCount`). Subnautica estimates the block count from its
skin (occupied volume / one block); the character is never debris. Seeded failure S11.M11 (both sides) keeps remnants as
targets.

*Damage on screen (pass 5).* SE's full-screen blood overlay (`MyGuiControlBloodOverlay` drawing
`MyRenderComponentCharacter.GetHUDBloodAlpha`) is 0.8 after any health drop, however small, decays only in the
character render component's `Draw`, and stays up under 20 % health; with S11 on, a postfix changes only the per-hit
pulse, set from the last drop (`format hud.blood`: gone 800 ms after it), 0 under Subnautica's picture when Subnautica
reports its own damage FX in the C7 S11 block (`SnStatus` 0xC0–0xC3, `format c7.s11`); the game's own low-health floor
stays (under Subnautica's picture too) and so does its value while dead. Under its picture Subnautica plays
`DamageFX.AddHudDamage` for each hazard record and hit on the character (`format fx.sn`: the camera's `DamageScreenFX`
heat, cold, poison, electric or blood overlay; the HUD splatters moved under S16's overlay canvas `DE.S16.Hud`).

*Stacked warheads (A).* VERIFIED in SE's decomp: a warhead chain-detonates only when **armed** (`MyWarhead.CanExplode` =
`IsFunctional && IsArmed`); an armed warhead destroyed by a neighbour explodes after a random 0–500 ms
(`OnDestroy` → `MarkForExplosion` + `ExplodeDelayed(500)`; a bullet makes it explode at once); each explosion is one
`MyExplosions.AddExplosion` with `WarheadExplosionDamage` 15,000 and a sphere whose radius grows by 2.4 % per warhead in
range, at most 30 m (`GetExplosionSphere`). An unarmed warhead is simply destroyed. SE groups the explosions of one tick
(`format blast.group`, ≤ 16 groups per tick, nothing dropped) into one Explosion row each, `extra` = the summed yield;
Subnautica's picture opens a chain at the first row and absorbs the rows that join it within 600 ms; when the window
closes a chain that absorbed anything plays one escalated explosion of the whole sum (radius cbrt(Σr³), ≤ 200 m).

*Hitboxes (B).* VERIFIED: `AvoidObstacles` scans `Physics.Raycast/SphereCast` on `Voxeland.GetTerrainLayerMask` (layer
30 only) unless `avoidTerrainOnly` is false (mask −5), and `AvoidTerrain` (the leviathans' 30 m scan) uses the terrain
octree (`LargeWorldStreamer.OctreeRaycast`), not colliders; `Creature.GetCanSeeObject` linecasts layer 30. Proxies stay
on layer 0 (on layer 30 every section would hide its own EcoTarget from `GetCanSeeObject` and leave the S6 penetration
oracle); S11 adds the proxies to both avoidance scans instead, leaving the game's masks as they are: beside
`AvoidObstacles`' own scan a proxy-only cast with the action's radius and distance starts the same avoidance, its direction
search is repeated with the proxies too, and `AvoidTerrain`'s octree test also sees proxies; a section of the ship the
creature attacks is never an obstacle. The
character capsule and the sections exist only while S6 runs: `launcher.py run` now starts both games with

*Control (C11, dev builds).* Subnautica `de.s11 {action: "status" | "weapon" | "scale" | "rage" | "scene" | "teardown", …}`: `weapon {class,
tier, mult, floor}` (or `reset`) retunes the live table; `scene {part: "danger" | "path" | "grab" | "hazard" | "emp" |
"air", …}` the oracle scenes (`grab`: a forced reaper-keyed grab through Subnautica's own record path; `path`: the
scripted avoidance probe); SE `de.s11 {status | stack | inject | bite | release | guard | volley}` (`guard`: the harness's
admin invulnerability on the local player, restored on false and on unload; never set by a play session). Chat:
`/de s11 …` (SE), `/de sn s11 …` (relayed). Mutants (dev): `de.mutant S11.Mx on|off`.

*Canonical text* (exact; ASCII, `\n` after every line, the last included; the order is the code's: header, const groups,
table rows, format lines sorted by key), FNV-1a 64 **`0xa10efc0920e7f8e8`** (133 lines, 12,020 bytes):
```
ext s11 creatures 5
const S11Unit HazardHp 17
const S11Unit HealHp 18
const S11Unit OxygenS 19
const S11Unit EmpS 20
const S11Unit Grab 21
const S11Unit Push 22
const S11Unit MissileHit 23
const S11Flags ViaSampler 256
const S11Flags ViaSection 512
const WeaponClass Handheld 1
const WeaponClass SmallGrid 2
const WeaponClass LargeGrid 3
const WeaponClass Explosion 4
const CreatureTier Small 1
const CreatureTier Large 2
const CreatureTier Leviathan 3
const CreatureTierMinHp Large 200
const CreatureTierMinHp Leviathan 2000
weapon Handheld Small multPermille 1000 floorPermille 0
weapon Handheld Large multPermille 1000 floorPermille 0
weapon Handheld Leviathan multPermille 1000 floorPermille 500
weapon SmallGrid Small multPermille 1000 floorPermille 0
weapon SmallGrid Large multPermille 1000 floorPermille 0
weapon SmallGrid Leviathan multPermille 1000 floorPermille 250
weapon LargeGrid Small multPermille 1000 floorPermille 0
weapon LargeGrid Large multPermille 1000 floorPermille 0
weapon LargeGrid Leviathan multPermille 1000 floorPermille 0
weapon Explosion Small multPermille 1000 floorPermille 0
weapon Explosion Large multPermille 1000 floorPermille 0
weapon Explosion Leviathan multPermille 1000 floorPermille 0
const Blast YieldUnitSe 10
const Blast JoinPadM 2
const Blast ChainWindowMs 600
const Blast VisualRadiusCapM 200
const Blast DamageRadiusCapM 100
const Blast MaxGroupsPerTick 16
const Blast ClassShift 8
grab ReaperLeviathan 2540 attach seamothAttachPoint dps seamothDamagePerSecond maxVolumeM3 400 maxEdgeDm 150 holdMinMs 8000 holdRandMs 5000 cooldownMs 10000 dpsPermille 1000 intervalMs 1000 blendMs 1000 release Electrical,Poison
grab SeaDragon 2553 attach exosuitAttachPoint dps exosuitDamagePerSecond maxVolumeM3 2900 maxEdgeDm 310 holdMinMs 3000 holdRandMs 0 cooldownMs 10000 dpsPermille 1000 intervalMs 1000 blendMs 250 release none
push ReaperLeviathan 2540 factorPermille 1000 maxDvCmS 600
push GhostLeviathan 2562 factorPermille 1000 maxDvCmS 1000
push GhostLeviathanJuvenile 2565 factorPermille 1000 maxDvCmS 480
push SeaDragon 2553 factorPermille 1000 maxDvCmS 1000
scale ReaperLeviathan 2540 factorPermille 2500 biteMultPermille 11000
scale GhostLeviathan 2562 factorPermille 2500 biteMultPermille 14000
scale GhostLeviathanJuvenile 2565 factorPermille 1800 biteMultPermille 7000
scale SeaDragon 2553 factorPermille 2500 biteMultPermille 5800
rage ReaperLeviathan 2540 releasePermille 300 angerPermille 1500 extendMs 4000 fleeMs 10000
rage SeaDragon 2553 releasePermille 350 angerPermille 1500 extendMs 2000 fleeMs 10000
const Hold OmegaMilli 6000
const Hold ZetaPermille 1000
const Hold MaxAccelCmS2 15000
const Hold AngOmegaMilli 4000
const Hold MaxAngAccelMilli 12000
const Hold TimeoutMs 1000
const Hold RecordIntervalMs 20
const Hold SnapCm 100
const Hold ServoKpMilli 10000
const Hold ServoKRotMilli 8000
const Hold MaxAngVelMilli 6000
const Hold LeadMaxMs 100
const Hold MaxMouthSpeedCmS 15000
const Hold ReleaseSpeedCmS 1000
const GrabPhase Start 1
const GrabPhase Hold 2
const GrabPhase Release 3
hazard Heat 1 DeepHeat kPermille 1000 capPerS 50 effect heat
hazard Poison 5 DeepPoison kPermille 1000 capPerS 30 effect poison
hazard Acid 6 DeepAcid kPermille 1000 capPerS 30 effect acid
hazard Cold 10 DeepCold kPermille 1000 capPerS 30 effect cold
hazard Fire 14 DeepFire kPermille 1000 capPerS 50 effect fire
hazard Smoke 16 DeepSmoke kPermille 1000 capPerS 10 effect smoke
const Heal KPermille 1000
const Heal CapPerS 100
const Heal MaxRecordHp 500
const Oxygen TankS 45
const Oxygen BaseRefillPerS 30
const Oxygen MaxRecordS 120
const OxygenSource Bubble 1
const OxygenSource Area 2
const OxygenSource Base 3
const OxygenSource Other 4
const Emp SecondsPermille 1000
const Emp MinS 1
const Emp MaxS 30
const Lava ProbeIntervalMs 100
const Lava HeatMilliHpPerProbe 2500
const Lava ReachCm 40
const Limits MaxHazardHp 500
const Limits MaxPushNs 10000000
const Limits MaxEmpS 60
const C7 Offset 192
const C7 Size 4
const C7 Version 1
const C7Flags DamageFxOk 1
const C7Flags DamageFxShown 2
const Hud HitFadeMs 800
const Hud FullHitHp 10
const Hud MinAlphaPermille 200
const Hud MaxAlphaPermille 800
const Debris MinBlocks 8
const Debris MinVolumeM3 8
const Surface MarginCm 200
const Missile HitRadiusCm 100
const Missile BackCastCm 3000
const Missile SlugPermille 20
const Missile SlugRadiusCm 300
const Missile AttributionMs 1500
const Missile BackCastMs 100
const Missile HitBody 0
const Missile HitHolder 1
format blast.damage hp = yield * mult(Explosion, tier) * max(0, (R - d) / R), d = the distance to the nearest point of the creature's own colliders in the overlap (Collider.ClosestPoint; a non-convex mesh collider by its bounds; its LiveMixin transform without one; 0 for the creature a missile struck, missile.hit), R = min(cluster R, DamageRadiusCapM)
format blast.group join when |c - C| <= max(R, r) + JoinPadM; R = cbrt(sum r^3); C = yield-weighted centre; yield = sum
format c7.s11 SnStatus 0xc0-0xc3 written by Subnautica every frame: version, flags (DamageFxOk, DamageFxShown), 2 reserved; version 0 = absent (SE keeps its own overlay)
format c8.s11 unit HazardHp|HealHp: amount SN_HP, kind 2 character; OxygenS: amount SN seconds, snDamageType = OxygenSource; EmpS: amount seconds, kind 1 grid
format c8.s11grab unit Grab: snDamageType = GrabPhase, snWorldHit = attach point, attackDirSn = attach forward, localHit = attach up, dealerInstanceId = grab key, amount = planned hold s (start) else 1
format c8.s11missile unit MissileHit: kind 1, targetEntityId = rootEntityId = the missile's SE entity id, snWorldHit = the hit point, attackDirSn = the flight direction, dealerTechType and dealerInstanceId = the struck creature, amount 1, snDamageType = HitBody 0 or HitHolder 1 (the missile left the grid that creature holds)
format c8.s11push unit Push: amount = impulse N*s, attackDirSn = push direction (SN world), snWorldHit = bite point
format danger.applied min(base * mult, max(0, health - floor * maxHealth)); tier by LiveMixin.maxHealth read at run time
format debris.grid a grid is debris (never hunted, bitten, grabbed or pushed: no EcoTarget, no bite, no grab start, no push) when blocks < MinBlocks or its box volume < MinVolumeM3 m3; SE counts MyCubeGrid.BlocksCount, Subnautica estimates blocks = sum of its skin box volumes / block edge^3 (0.5 m small, 2.5 m large; unknown: the volume alone); the character is never debris
format event.class Explosion: subtype bits 8-11 = the WeaponClass of the origin (a character Handheld, a small grid SmallGrid, a warhead, a large grid or none Explosion); 0 = absent (Explosion); groups never mix classes
format event.yield Explosion: extra = clamp(round(yield / YieldUnitSe), 1, 65535); 0 = absent (yield = intensity * 1000)
format fx.sn under Subnautica's picture a hazard record or a bite on the character plays DamageFX.AddHudDamage(hp / 100, point, type) at most every 250 ms per type; its splatters move under the S16 overlay canvas; leaving the picture clears them
format hold.force (S11.M12 only, the pre-kinematic rule) m*(clamp(w^2*(xt - x) + 2*z*w*(vt - v), aMax) - g) at the centre of mass, x and v = the bite anchor and its velocity, aMax = MaxAccelCmS2 / 100 * max(1, scale); -F_buoy*(1 - holdShare) at the centre of buoyancy
format hold.kinematic every SE tick: xt = the last attach point (the jaw centre: seamothAttachPoint / exosuitAttachPoint, where Subnautica holds a Seamoth or an Exosuit) + its velocity (between the last two records by Subnautica's emit times snTimeSec, else their arrival; over MaxMouthSpeedCmS none) * min(age, LeadMaxMs); the anchor = the grid's centre of bounds (PositionComp.LocalAABB centre, grid-local); the target xb = a + (xt - a) * f, a = the anchor at the start, f = min(1, held / blendMs of the grab row), vb = its rate; the orientation = attach * slerp(relative-at-start, aligned, f), aligned = the grid's longest box axis on the attach forward and its shortest on the attach up (signs nearest the start); the error = |xb - anchor|; over SnapCm the grid's matrix is set (anchor on xb); then the anchor's velocity = vb + (xb - anchor) * ServoKpMilli / 1000, the angular velocity = clamp(rotation vector of (target * current^-1) * ServoKRotMilli / 1000, MaxAngVelMilli / 1000), both set on the root grid (no force: SE's physics owns the rest); a grid with mechanical subgrids is never held (Subgrids); at any release the root grid's linear speed is capped at ReleaseSpeedCmS, its angular velocity zeroed and, at a surface release, its upward component removed; while it holds, Subnautica plays the creature's animation at 1 / its scale (the jaw swings at the unscaled creature's speed)
format hud.blood SE blood alpha: the game's own while dead; the per-hit pulse = clamp(drop / FullHitHp, Min, Max) * (1 - dt / HitFadeMs) after the last health drop, 0 from HitFadeMs on (0 under Subnautica's picture with a fresh c7.s11 DamageFxOk); the game's low-health floor (health ratio <= 0.2: (0.2 - ratio) / 0.2 + 0.3) kept: max(pulse, floor)
format missile.hit Subnautica sweeps every mirrored missile (GridAdd Missile) each frame from its last pose to this one (Physics.DefaultRaycastLayers, triggers ignored; its first pose also by an overlap of HitRadiusCm and a cast of BackCastCm along -forward, its second by a cast of speed * BackCastMs behind the first): the nearest collider of a live creature (LiveMixin + Creature) before any other solid (the ghost Player passes) is a hit, one MissileHit record per missile; a holder's jaw within the held grid's reach of the first flight is a HitHolder; SE refuses a hit more than 1 m behind the missile's launch point (MyMissile.Origin, Behind); a HitBody moves the missile to the hit point and explodes it (MyMissile.Explode: its own Explosion row); a HitHolder explodes it where it is with its damage set to 0 (the player's hull untouched) and raises one synthetic Explosion row of its damage at the hit point; a health-pool slug (HasHealthPool) raises one synthetic Explosion row of yield HealthPool * SlugPermille / 1000, radius SlugRadiusCm / 100, its launcher's class; the struck creature takes that blast at d = 0 (its hit within AttributionMs and JoinPadM of the centre)
format oxygen.litres SE litres = seconds / TankS * suit OxygenCapacity, capped at the capacity
format push.dv min(impulse / grid mass, MaxDvCmS / 100) along the push direction at the bite point; impulse = mass * scale^3 * closing * factor
format rage.hold during a hold the SN_HP the holder takes from SE weapons (danger.applied) accumulates; at ReleasePermille / 1000 * maxHealth it lets go (a release record) and flees for FleeMs (Scared + 1, Aggression held at 0, LastTarget cleared, swims away kept under the surface.release follow depth, no grab), once per hold; the first lower hit makes it angrier, once per hold: hold damage * AngerPermille / 1000, the planned hold + ExtendMs, Aggression + 1; retuned live (de.s11 rage), never the pin
format scale.creature root localScale = factor * one (absolute; Creature.Start resets a non-growing creature to one first); BiteMult (its own value, not the factor) on Normal hits on SE grids, bites and hold damage
format surface.release depths scale with the bodies: a creature's top is the higher of its root and its head (the attach point) plus its girth (half its thinnest collider-bounds extent); while it holds a grid it lets go when its top or the held grid's comes within MarginCm / 100 m of the surface (Subnautica: SN y = the altitude above the Water Mod sea sphere; SE: the Water Mod GetDepth at the top of the grid's bounds; SE then drops that grab key's later hold records); no grab starts there; a creature that holds, flees or hunts an SE grid section (LastTarget) is steered no higher than -(its girth + MarginCm / 100) m at SplineFollowing.GoTo (the last door of SwimTo, Attack and Overshoot), and at every physics step (Locomotion.ManagedFixedUpdate) loses its upward speed while its top is within MarginCm / 100 m of the surface; nothing of S11 ever drives it upward
```
Pinned by `S11ConventionsTests` (text byte for byte, hash, units outside S6's, the hazard set disjoint from S6's forwarded
types, S6's pin unchanged) and by the Python twin (`s11_ref.py --selftest`). Changing a line bumps `ext s11 creatures <n>`
and updates both pins.

**S16 conventions ( S16 pass; review fixes the same evening; `ext s16 depth 4` the same night: the damage gate is the world's destructible blocks in any game mode (Creative included), the HUD placed by its readout parts only (backdrops left out); `ext s16 depth 5`: no SE text line for the diver (`format hud.se`), the grid line only as the seated fallback; `ext s16 depth 8` (S16b 00:22: "1603 m / 1700 m" 176 of 156 units at TMP's auto-size floor): the short form "1603 / 1700 m" above 6 digits, the font measured to 90 % of the compass (floor 25 %); layout 1.0 and its hash unchanged; pinned on their own).** Scope: ROADMAP §14
"### S16 Depth progression", docs/S16-BRIEF.md. Layout decision: **layout 1.0 suffices, no re-pin**: the SE → Subnautica
values travel in the C5 `SeState` pad (`pad_80`, 0x80–0xFF, unused before S16), bytes 0x80–0xA9, version byte 0 = absent;
Subnautica's HUD state comes back in the C7 `SnStatus` pad after the S8a block, bytes 0xBC–0xBF (version byte 0 = absent);
every earlier pin is unchanged. Code: Wire `S16Conventions` (the tables, `CanonicalText`, `Hash`), `S16Rules` (`S16Rule`,
`S16Classify`, `S16Exposure`, `S16Ladder`, `S16Damage`, `S16TopK`, `S16Creak`, `S16CreakClock`, `S16VoiceArm`,
`S16VoiceRule` (the S15 voice helper: once per event, cooldown), `S16Hud`, `S16Wire`, `S16SeqReader`); the Python twin
`tools/s16/s16_ref.py` (`--selftest`, `--vectors` into `tests/s16/vectors/`, `--ladder`). SE: ` (game
thread), `S16SeLogic.cs`, `S16Commands.cs` (`de.s16`, dev); Subnautica: `, `S16SnHud.cs` (main thread),
`S16SnRules.cs`; the world mod `Data/CubeBlocks_S16.sbc` (checked by `tools/worldmod/check_planet.py` → `s16_blocks.py`
and by SE's own serializer in `tests/worldmod/DeepEngineers.WorldMod.Tests/S16BlocksTests.cs`).

*Who does the damage (decided, VERIFIED in the Water Mod 3.23 source).* The Water Mod's crush path
 damages every block whose per-subtype `BlockConfig.MaximumPressure`
 is below the grid's single `FluidPressure`
 by a flat `Settings.CrushDamage × (recalculateFrequency + 1)` per update through its
`BlockDamageComponent`; `CrushDamage` is per planet, not per grid, and
`GetCrushDepth` of the API answers 0. It cannot carry the ladder, the tiers or the curve, and
§3 validates `CrushDamage = 0` every 10 s. So **the plugin does the damage and the Water Mod's `CrushDamage` stays 0**.

*Crush depth (`format block.class`, `format exposure`, `format crush.depth`).* Per grid (every grid within 1 km above the sea
of the bound planet; scanned when in the water, on a block-count change, after its gas system finished a recompute, and
every 600 ticks; 1,500 blocks per tick): each block's class from its definition
its exposure over the face-neighbour cells of `Min..Max` with the neighbours'
classes and the S7 air rule (`MyGridGasSystem.GetOxygenRoomForCubeGridPosition`,
``; `MyOxygenRoom.IsAirtight`); no scan while `IMyGridGasSystem.IsProcessingData` (``). Armour counts when it touches
water (outer hull, the walls of a flooded room), a Fitting only on the pressure boundary (water on one side, an airtight
room on another: a window, a door, a canopy), rated as the armour it touches; thrusters and lights outside never count; nothing counted = light
armour of the grid size. A tier block counts while `ComponentStack.IsFunctional`; the best one
raises the crush depth
Static grids (stations) are never tracked nor rated. *Groups:* the best tier of the mechanical group
 raises
every grid of it; a grid linked by a connector (`Physical`, ``) or a landing gear (`NoContactDamage`, ``) to a larger
grid outside its mechanical group is docked: its limit is at least the carrier's (never lowered by it), and it takes no
damage while every block centre (sampled to 512) lies in an airtight room of the carrier (`MyCubeGrid.WorldToGridInteger`,
``).

*Damage (`format damage.block`).* Every 60 ticks per grid (the next tick set first: exactly once per period, no catch-up;
the first period 60 + id mod 60 ticks after the grid is tracked, so a grid split off a damaged one never repeats its
parent's period and the grids are out of phase), none during the first 3,600 ticks after S16 becomes active in a world, in Survival with `DestructibleBlocks`,
`MyCubeGrid.BlocksDestructionEnabled` (``), `de.s16 damage on` (default) and outside the terrain fail-safe's footprint
(§7): the eight deepest damage-set blocks whose centre is past the
limit (one pass, no allocation: `S16TopK` = `S16Damage.Pick`) each get `MySlimBlock.DoDamage(amount, "DeepPressure", true,
null, 0)` (``; SE's own modifiers, safe zones and destruction; never a health write). The damage set is the counted
blocks, or every block with a wet face when nothing counts. `k` is S6's
(small 1, large 25 integrity per hull point). Subnautica's own vehicle rule is flat
ours grows with the metres past the limit and is capped. The diver keeps Subnautica's
rule (§2.10: no crush damage, breathing ×1.5 below 100 m, ×2 below 200 m).

*HUD, creaks, voice (`format hud.*`, `format creak.*`, `format voice.rule`, `format c7.s16`).* The HUD grid is the seated
player's, else the grid whose box holds the character
(dev: `de.s16 watch`); its depth is `−WaterModAPI.GetDepth` at the deepest of its box's eight corners. SE's HUD line
 every 30 ticks, red from the creak band, is the fallback: while the
picture is Subnautica's (C5 viewState Underwater or Surfacing) Subnautica shows its own HUD (HUD A/B) and says so in the C7
block, and SE hides its grid line then (`format hud.se`: SE never shows a diver line). The bridge masks Subnautica's HUD

so S16 re-parents `uGUI_DepthCompass` under its own overlay canvas `DE.S16.Hud`

pauses its script and writes its player readout: the
diver's depth, and seated the Cyclops helm's form "depth m / crush m"
toned by the HUD state (`format hud.sn`). Depth 6 drops the Seamoth panel

nothing showed: its parts were measured from one of four fields and placed off screen). The creak cadence and the voice rule run on SE's game ticks; the C5 counters carry the
events; Subnautica plays one sound per increment: the creaks are `BaseHullStrength.crushSounds[level − 1]`
the voice the
Cyclops's `SubRoot.hullCriticalNotification` `sound`

the groan on damage the Cyclops's `CrushDamage.soundOnDamage`
all through `FMODUWE.PlayOneShot(FMODAsset, Vector3, float)` at `MainCamera.camera`;
the Cyclops prefab from `LightmappedPrefabs.main.GetScenePrefab("cyclops")`.
A missing source logs `DE.S16 MISSING <what>` once; each sound is one explicit line `DE.S16 sound <kind> seq= level= played=
source= path= coalesced=`.

*Rows and control (dev).* SE `s16.site`, `s16.grace`, `s16.grid`, `s16.group`, `s16.depth`, `s16.damage`, `s16.creak`,
`s16.voice`, `s16.hud`, `s16.descend`; Subnautica `s16.creak`, `s16.voice`, `s16.damage`, `s16.snhud`. `de.s16 {status |
ladder | damage: on|off | spawn: light|heavy|room|bare|tier1..2|tier2small|… | descend | stop | job | watch | remove | floor | deep}`
(chat `/de s16 …`), `DEEPENGINEERS_S16=off` disables the slice; mutants S16.M1–M6, M8–M10 (SE), S16.M7 (Subnautica);
oracle `tests/s16/analyze_s16.py`, session `launcher.py s16`.

*Canonical text* (exact; ASCII, `\n` after every line, the last included; the order is the code's: header, the const groups,
the hull and tier rows, the C5 and C7 field lines, the format lines sorted by key), FNV-1a 64 **`0xbad78a2237d74b6d`** (118
lines, 8,516 bytes
```
ext s16 depth 11
const S16Size Small 1
const S16Size Large 2
const S16Class LightArmor 1
const S16Class HeavyArmor 2
const S16Class Fitting 3
hull Small LightArmor crushM 350
hull Small HeavyArmor crushM 500
hull Large LightArmor crushM 800
hull Large HeavyArmor crushM 1200
tier 1 DE_PressureHullMk1 crushM 1400 ingots Cobalt,Magnesium,Silver
tier 2 DE_PressureHullMk2 crushM 2000 ingots Gold,Platinum,Uranium
const S16Alloy DE_HullAlloyMk 2
const Damage PeriodTicks 60
const Damage BaseMilliHpS 10000
const Damage PerMetreMilliHpS 1000
const Damage CapMilliHpS 100000
const Damage MaxBlocksPerPeriod 8
const Damage KSmallPermille 1000
const Damage KLargePermille 25000
const Damage GraceTicks 3600
const Damage SeType DeepPressure
const Creak BandM 50
const Creak Level2M 25
const Creak PeriodTicks1 540
const Creak PeriodTicks2 360
const Creak PeriodTicks3 180
const Voice CooldownMs 20000
const Voice RearmM 10
const Voice RepeatMs 20000
const Voice VolumePermille 1600
const Voice DuckMs 3500
const Voice DuckPermille 250
const HudState off 0
const HudState safe 1
const HudState creak 2
const HudState limit 3
const HudState damage 4
const Hud RefreshTicks 30
const Hud AliveMs 1500
const Hud MinDepthDm 5
const Hud TopPermille 60
const Hud CautionRgb 16760896
const Hud FitPermille 900
const Hud MinFontPermille 250
const Hud SeatedLongDigits 6
const Hud CentreTolPermille 50
const Hud RefWidth 1920
const Hud RefHeight 1080
const Hud MaxPartPermille 500
const DamageGate none 0
const DamageGate destructible 1
const DamageGate indestructible 2
const DamageGate off 3
const SeLine none 0
const SeLine grid 1
const SeLine suppressed 2
const Tone normal 0
const Tone caution 1
const Tone danger 2
const Diver UnsafeDepthM 100
const Diver CrushDepthM 200
const Diver BreathX10Safe 10
const Diver BreathX10Unsafe 15
const Diver BreathX10Crush 20
const C5 Offset 128
const C5 Size 42
const C5 Version 2
const C5Flags Grid 1
const C5Flags Diver 2
const C5Flags DamageOn 4
const C5Flags Docked 8
const C5Flags Exempt 16
const C5Flags Grace 32
field s16 version 0x80 1 u8
field s16 flags 0x81 1 u8
field s16 creakLevel 0x82 1 u8
field s16 hullCode 0x83 1 u8
field s16 tier 0x84 1 u8
field s16 breathX10 0x85 1 u8
field s16 hudState 0x86 2 u16
field s16 depthM 0x88 4 f32
field s16 crushM 0x8c 4 f32
field s16 creakSeq 0x90 4 u32
field s16 voiceSeq 0x94 4 u32
field s16 damageSeq 0x98 4 u32
field s16 worldEpoch 0x9c 4 u32
field s16 gridId 0xa0 8 i64
field s16 hullPct 0xa8 1 u8
field s16 powerPct 0xa9 1 u8
const C7 Offset 188
const C7 Size 4
const C7 Version 1
const C7Flags CompassOk 1
const C7Flags SeamothOk 2
const C7Flags DiverShown 4
const C7Flags GridShown 8
field s16 c7version 0xbc 1 u8
field s16 c7flags 0xbd 1 u8
field s16 c7reserved 0xbe 2 u16
format block.class HeavyArmor: definition EdgeType Heavy; LightArmor: TypeId CubeBlock and SubtypeId contains Armor (ordinal, ignoring case); every other block (windows, doors, cockpits, vents, connectors) a Fitting; the tier blocks are Fittings that set the tier while ComponentStack.IsFunctional
format c5.s16 SeState 0x80-0xa9; version 0 = absent; creakSeq, voiceSeq, damageSeq count events (u32, never reset in an SE session); a reader takes its first value as the baseline and plays one sound per increment seen in the same worldEpoch
format c7.s16 SnStatus 0xbc-0xbf written by Subnautica every frame; version 0 = absent; SE hides its grid line while GridShown is set in a fresh record (DiverShown only reports Subnautica's diver depth meter)
format creak.cadence one creak when the level becomes >= 1 or rises, then one every PeriodTicks(level of the last creak) while the level stays >= 1
format creak.level d = the HUD grid's lowest point depth; 0 if d < L - BandM, 1 if d < L - Level2M, 2 if d <= L, 3 if d > L
format crush.depth max(min over counted blocks of hull(size, class), crushM of the best functional tier block or 0); no counted block: hull(size, LightArmor); a static grid (a station) is never tracked nor rated: no crush depth, no damage, seated in its cockpit the diver readout
format damage.block every PeriodTicks per grid (first period PeriodTicks + id mod PeriodTicks after it is tracked), none during GraceTicks after S16 becomes active: the MaxBlocksPerPeriod deepest damage-set blocks whose centre depth d > L each take MySlimBlock.DoDamage(min(Cap, Base + PerMetre * (d - L)) * PeriodTicks / 60 * k(size), DeepPressure, sync, no hit info, attacker 0); the damage set is the counted blocks, or every block with a wet face when none counts
format damage.gate crush damage applies when the world's DestructibleBlocks is on, whatever the game mode (Creative included), unless de.s16 damage is off (the dev override): Destructible, Indestructible, Off, None without a session; SE's DoDamage also honours the grid's own destructible flag (MyCubeGrid.BlocksDestructionEnabled)
format exposure a face-neighbour cell outside the block is wet when empty and not in an airtight room (MyGridGasSystem.GetOxygenRoomForCubeGridPosition, IsAirtight), dry when empty and in one; armour counts when a neighbour is wet, a Fitting when one is wet and one dry, as the majority class of the armour cells it touches (HeavyArmor only with more heavy than light cells, else LightArmor)
format group.crush L = max(crush.depth, crushM of the best functional tier block of the mechanical group); docked (a Physical or NoContactDamage link to a larger grid outside the mechanical group): L = max(L, the carrier's L), no damage while every block centre lies in an airtight room of the carrier
format hud.diver no grid: depth = the eye; breathX10 = BreathX10Crush if d > CrushDepthM, BreathX10Unsafe if d > UnsafeDepthM, else BreathX10Safe; no crush depth for the diver
format hud.hull hullPct = ceil(100 * sum(Integrity - AccumulatedDamage) / the largest sum(MaxIntegrity) seen) over the damage set; powerPct = ceil(100 * stored / capacity) over the grid's batteries, 255 without one
format hud.place the canvas scale is min(W / RefWidth, H / RefHeight) x uiScale; an element is moved so that the bounds of its readout (the shown mode's own fields and the graphics on their parents up to the element; a part wider than W x MaxPartPermille / 1000 or taller than H x MaxPartPermille / 1000 is a backdrop and left out; canvas units, origin the canvas centre) have its top-centre at (0, H/2 - H x TopPermille / 1000), then shifted back inside the canvas; on screen (pixels, top-left origin) x = (u + W/2) x scale, y = (H/2 - v) x scale
format hud.se SE's HUD text line: None for the diver (under water Subnautica's compass shows the depth, above water nothing); for a seated grid Grid, Suppressed while Subnautica shows its readouts (GridShown in a fresh C7 record)
format hud.sn Subnautica shows its own HUD while the picture is its own (C5 viewState Underwater or Surfacing) and hudState is not Off: uGUI_DepthCompass's player readout (shadow, halfMoon, depthText, suffixText); the diver: depthText = depth, suffixText = MeterSuffix; seated, the Cyclops helm's form depth / crush in one field: depthText = depth + ' ' + MeterSuffix + ' / ' + crushM + ' ' + MeterSuffix, or depth + ' / ' + crushM + ' ' + MeterSuffix when the two numbers have more than SeatedLongDigits digits together, crushM <= 0 (no crush depth): depth + ' ' + MeterSuffix only; suffixText off, no word wrapping, overflow, out of any layout group, centred on the compass (|text centre - half-moon centre| <= CentreTolPermille / 1000 of the compass width), its font size measured: min(its own, its own x target / the preferred width at its own), at least MinFontPermille / 1000 of its own, target = the compass width x FitPermille / 1000; it fits when TMP renders one line no wider than the target (live textInfo.lineCount, textBounds after ForceMeshUpdate); its tone Normal for Safe, Caution (CautionRgb) for Creak, Danger (textColorDanger, halfMoonDanger) for Limit and Damage; no Seamoth panel
format hud.state Off if d < MinDepthDm / 10; else Damage if d > L and damage applies, Limit if d > L, Creak if d >= L - BandM, Safe
format voice.mix Subnautica plays the voice at VolumePermille / 1000 and every S16 creak and damage groan that starts within DuckMs after a voice at DuckPermille / 1000 of its own volume
format voice.rule one hullCritical event when d > L while armed (disarmed by it, armed again when d < L - RearmM) and one more every RepeatMs while d stays > L; it plays when the same grid played none within CooldownMs
```
Pinned by `S16ConventionsTests` (text byte for byte, hash, the extensions inside `pad_80` and `pad_8c` after S8a's block,
layout 1.0 and the S6/S11 pins unchanged, de.s16 dev-only) and by the Python twin (`s16_ref.py --selftest`). Changing a line
bumps `ext s16 depth <n>` and updates both pins.

**S13 conventions.** Scope: ROADMAP §14
docs/S13-BRIEF.md). Wire
`S13Conventions`, `S13CharWire` (codec), `S13LightFit` (the fit).
(§6's list of extensions pinned outside the layout hash gains this one.)
- *C1 S13 extension (0xC0–0xC7 of the C1 pad, free before S13: swim 0x94–0x9F, loco 0xA0–0xAF, interior 0xB0–0xBF).*
 0xC0 u8 `version` 1 (0 = an SE before S13: Subnautica keeps S5a's input rule), 0xC1 u8 `flags` (1 Valid: the look frame
 was read, 2 BodyBasis: the swim model moved the character along this frame this tick, 4 Swimming: the swim extension's
 Controls), 0xC2/0xC4/0xC6 i16 `vRightCm`/`vUpCm`/`vForwardCm`: `MyCharacter.Physics.LinearVelocity` in the character's
 look frame = `GetHeadMatrix(includeY: false, includeX: true, …)`
 cm/s rounded half away from zero, clamped ±32,767. Written with every C1 record by
 `S13Se.FillWire` (FrameWriter, after the interior extension; no character: version 1, flags 0). Subnautica reads it in
 `FrameReader.Poll` (`S13Char`, `S13Valid`).
- *Point 1 (hands).* Under fpbody=se (S5 host gate ForceOff) Subnautica's diver view model is never drawn: the 1.3 s dive
 window of (`S5aHost`, the only path that drew it under ForceOff, 19–27 `s7p.char` leak rows per free
 play since) no longer shows it (S13.M1 brings it back). The dive and surfacing sounds it was meant to restore are
 Subnautica's `WaterAmbience` (`diveStart`, `diveStartSplash` when −vUp > 5.5 m/s, `PlayReachSurfaceSound`; WaterAmbience.cs:
 33-66), which the ghost never plays itself: `S13Sn.SwimFrame`
 plays them on the C1 swim flags' eye crossing (InWater, Underwater edge; dives ≥ 1 s apart). SE's astronaut does the dive
 animation through the swim retarget.
- *Point 2 (swim near a surface).* `CharSourceRules.Swimming` → `S13Source.Swimming`: ground contact (C1 swim flag Grounded =
 `CharacterProxy.Supported`) cancels the swim source only with the eye out of the water (no Underwater flag); submerged the
 swim (Retarget/Blend) holds whatever the contact (S13.M2 = the S5 rule); never on a ladder. The SE swim latch already ignored contact below +0.1 m (S2b), so no SE change.
- *Point 3 (tools, skins).* The held tool's PartAdd `colorMaskHSV` = the tool entity's `Render.ColorMaskHsv`
 not (0, 0, 0)
 (+ ColorizeHSV = hue 0, saturation 0.8: the red tool) (S13.M3). Subnautica's T6 per-texel paint (`ColorMaskPaint`) now visits
 the character's child parts too. A character child part is hidden until its first C2 row (no frame at the feet on equip).
 A rooted model (a mod's or a custom character's `.mwm`) resolves relative texture names in its own content root first
 (`S13SeRules.TextureFullPath`, SE's rule: MyMwmUtils.GetFullMwmContentPath, MyResourceUtils.GetTextureFullPath
 ); such a material is keyed by its full paths. Skins stay read from the install at run time (nothing shipped).
- *Point 4 (lamp shadows, falloff).* Every mirrored spot with shadows: `shadowNearPlane` 0.5 m
 the character's own lamp 1.0 m

 `shadowBias` 0.05 and `shadowNormalBias` 0.4 set explicitly (Unity's defaults). The character
 lamp's range × k and intensity × e: k = the least-squares range factor of Unity's built-in attenuation (HYPOTHESIS: 1 / (1 + 25
 x²), faded to 0 from x² = 0.8) against SE's spot curve (VERIFIED `LightingModel.hlsli` GetAttenuation: (1 − x⁴)³ / (1 + falloff
 x²)) over x ∈ [0, 1.5] on a 0.01 grid in [1, 4] (2.42 for the suit's falloff 1), e = the mean of the unfitted curve over
 [0, 1] / the fitted one (0. Block spots unfitted by default (`de.s13
 blockFit on`); points unchanged. S13.M4 = Unity's 0.2 m and no fit.
- *Point 5 (third-person direction).* SE: in third person the swim model's basis is the C1 look frame (body yaw, head pitch),
 not SE's camera; `de.s13 basis body|camera`, S13.M5 = the camera.
 Subnautica: under water the animator's `move_speed_x/y/z` are the look-frame velocity of the extension
 smoothed as S5a smooths (S13.M8 = S5a's input axes).
- *Point 6 (held items).* Swimming with a held tool, gun or block (C1 loco ToolHeld; a block is SE's CubePlacer) =
 `CharSource.Blend` (4): the parent-relative locals of `SE_RigLThigh`, `SE_RigRThigh` and their descendants from the swim
 retarget, every other bone SE's (the arms hold the item where SE puts it); without SE bones the full retarget.
- *Point 7 (placement ghost).* Each ghost renderer whose slot material has a `_BumpMap` draws Unity's Standard (Fade) with that
 normal map (the real block's panels and edges), `_Glossiness` 0.55, tinted green/red at alpha 0.45 with a 0.18 emission,
 one pair per slot; the camera inside the ghost's bounds (+0.3 m) fades it to 0. The rotation is
 SE's gizmo pose as before (S7a; SE's 0.1 s rotation slerp still snaps). `de.s13 ghost textured|flat`, S13.M7 = flat. Review 4:
 a player build may strip Standard's `_ALPHABLEND_ON`/`_NORMALMAP`/`_EMISSION` variants (`isSupported` stays true); once per
 process, in Update, each source (Standard Fade, then Subnautica's glass clone with the `_BumpMap`) draws its green material
 with `Graphics.Blit` over a 4×4 target cleared to (0, 0, 0, 0) and is kept when the texel is translucent (alpha < 0.9) and,
 for the glass, tinted (green above red); none kept → the flat S7a material. Line `DE.S13 ghost shader …` (shader, keywords,
 probe colours); the `s13.ghost` row carries `source`, `shader`, `probe`.
- *Rows.* SE `s13.swim` (1 Hz in the water and on a basis change: `basis`, `firstPerson`, `cameraBodyDeg`, the look-frame
 velocity, the inputs), `s13.tool` (`entityHsv`, `sentHsv`, `skin`); Subnautica `s13.char` (1 Hz: `leakFrames`, `leakVisibleFrames` (the
 flag), `preCullFrames`, `preCullForceOffFrames`, `forceOffFrames`, `submergedGrounded*`, `heldSwim*`, `tool*`, `anim*`,
 `splashes`, `splashesFirstPerson`, `splashSounds`), `s13.tooln` ( 1 Hz per held tool part: the received `h`, `s`,
 `v`, `red` = hue 0 with s ≥ 0 (SE's red key), `ageS`, `bakeMode`, per renderer slot `state` applied | pending | converting |
 none | approx | foreign | noslot with the slot's colour and `paintMax`), `s13.light` (1 Hz while
 the lamp casts: `nearPlane`, `rangeFactor`, `intensityFactor`, `falloff`, `surfaceM` (a ray along the lamp), `shadowLen10cm`
 = 0.1 × surface / near plane, `magnification`), `s13.ghost` (1 Hz: `textured`, `flat`, `inside`). Commands `de.s13` (dev
 only, not in the release table): SE `status | basis body|camera`; Subnautica `status | ghost textured|flat | lightFit on|off |
 blockFit on|off | nearPlane <m>|default | sounds on|off`. Mutants S13.M1–M8 (SE: M3, M5; Subnautica: M1, M2, M4, M6, M7, M8).

**S14 conventions; layout 1.0 and its hash unchanged; one C7 block; pinned on their own).**

the water; docs/S14-BRIEF.md). Wire `S14Conventions` (`ext s14 audio 6`, FNV-1a 64 `0x1261b20244b0b097`, 70 lines, 7,180
bytes; the twin is `tests/s14/analyze_s14.py`, checked by `--selftest`; `S14ConventionsTests`), `S14Rules` and `S14EngineEnv`
(the rules and the envelope both plugins use), `S14Wire` (the C7 block). (§6's list of extensions pinned outside the layout hash gains this one.)
- *Point 1 (the jetpack loop under water).* Cause (VERIFIED): `MyCharacterSoundComponent.SelectSound`
 answers the jetpack idle pair in the Flying state whatever the jetpack
 S2b's swim controller holds Flying with the jetpack off under water (`SwimController.EnsureProxyFlying`), and
 `PlayStateSound` stops the looping emitters (the idle loop on `m_soundEmitters[4]`) only for its private
 static `EmptySoundPair`, so the loop started in the air never stopped. A postfix answers that very instance
 (MySoundPair has no == operator) while the swim latch (`SwimGuard.SwimmingCharacterId`, cached on the update thread: SelectSound
 runs in `UpdateAfterSimulationParallel`) holds the local character in Flying with `JetpackRunning` false: SE's own
 "jetpack off" path stops the loops. S14.M1 = SE's answer.
- *Point 2 (every SE sound under water).audio` row of has
 `realisticSound` false): in an Arcade world `MyEntity3DSoundEmitter.InstallDefaultEmitterMethods` installs no
 implicit-effect delegate, so `PlaySoundInternal` (the effect) never filters and S7's postfix stood down. S14's Arcade route gives every emitter the realistic default (`EmitterMethods[3]` = the emitter's own
 `SelectEffect`; a prefix on `PlaySoundInternal(bool, bool, bool, bool, bool?)` and from the S7 postfix, AudioPolish S14 hook),
 so SE applies the S7 answer itself. The effect is
 SE's `LowPassHelmet` (1800 Hz since round 4; `AudioEffects.sbc`)
 in both sound modes: **this supersedes the effect name of S7's `format
 audio.muffle` line** (`LowPassNoHelmetNoOxy`, 100 Hz: the vacuum muffle, nearly silent); the S7 line keeps its gate
 (`seMixActive`, not `InRoom`, `ModifiableByHelmetFilters` only) and stays pinned as written (`ext s7 look 1` unchanged).

 eye, not the mix: the effect while S2's camera SN y (`ViewStateDriver.Y`, the machine's last step) is below `Muffle.EyeYMm`,
 whatever the view state; `seMixActive` follows the view machine's waterline hysteresis (enter at -0.20 m, leave at -0.05 m),
 its 250 ms dwell and its 300 ms ramp, so the effect lingered above the surface after a rise and was missing between 0 and
 -0.
 camera above the water, 328 the mix off with the camera under it). One line in AudioPolish's postfix (`S14Se.MuffleOn`), the
 same gate for the building sounds and the reverb; the S2 mix itself (SE's 0.7, the Water Mod's sound, Subnautica's bus) and
 the thruster rules keep `seMixActive`. Row `s14.eye` (each change, 1 Hz near or under the water: `eyeY`, `camY` = an
 independent `MySector.MainCamera` read, `eyeUnder`, `muffle`, `mixActive`, and per interval `judged` / `mismatch` frames
 outside `Muffle.DeadbandMm` and `crossings`); S14.N2E (at most 2 mismatched frames per crossing, the frame of lag); S14.M10
 = the mix again; the `s14.muffle` rows carry `eyeUnder` and S14.N2 judges by it.
 `de.s7audio {effect}` still picks any of SE's four. The building sounds SE plays through `MyGuiAudio.PlaySound(MyGuiSounds)`
 get the same effect
 menu clicks, the suit's voice, breathing and helmet sounds stay dry. SE's reverb
 (`MyAudio.ApplyReverb`, the game submix reverb) is on under the same gate when the player's EnableReverb option is on, the
 reverb detector's preset (`MyEntityReverbDetectorComponent.CurrentReverbPreset`) back after. The Water Mod adds no filter of
 its own (its WaterSoundComponent plays ambience emitters only, muted by S2). Subnautica's sounds are not touched (no double
 filter). S14.M2 = no Arcade route.
- *Point 3 (ship weapons under water).* Cause (VERIFIED): the Water Mod 3.23's own `Data/WaterConfig.xml` (lines 38-57) sets
 `FunctionalUnderwater="false"` on its 20 weapon entries, and `WaterPhysicsComponentGrid` switches those blocks off
 (`IMyFunctionalBlock.Enabled = false`) under water. The Water Mod reads `Data/WaterConfig.xml` of every mod of the world in the
 world's mod order, the last entry of a definition winning (`WaterConfigComponent.LoadConfigFiles`, `WaterData.BlockConfigs[id]
 = config`); a DE world lists the Water Mod first and DeepEngineers.World after it. **Override in the world mod** (data, the
 Water Mod's own path): `<BlockConfigs>` with the same 20 entries, every attribute equal (Volume drives buoyancy) except
 `FunctionalUnderwater="true"`; read at every session load. Checked by `check_planet.py` (`s14.weapons.*`, with `--water-mod`
 against the installed mod), `test_worldmod.py` (5 seeded failures), `S14WeaponConfigTests` (XmlSerializer on.NET Framework).
 The SE plugin reads back `Jakaria.WaterData.BlockConfigs` (row `s14.weapons`; S14.M3 writes them back to false in memory) and
 logs `DE.S14 WARN … the world's mod order: [...]` once when fewer than 20 are functional. Review 2: the Water Mod keeps a
 submerged weapon's earlier state only in memory
 so a ship saved under water before S14 reloads with its guns off; while the override is in effect
 the plugin switches the weapons under water that are off on once per grid, at its first scan after load (within 400 m of the
 camera; `MyFunctionalBlock.Enabled`, row `s14.weapons.restore`); the terminal stays the fallback.
 The shots' bubble tracers and muffled sound are S7p's (P5) and point 2's.
- *Point 4 (thrusters).* (a) Subnautica's FMOD listener comes from the scene's `FMODUnity.StudioListener`
 the bridge unparents and drives the camera rig, so a listener elsewhere hears the mirrored
 sounds from the wrong place. A postfix on `StudioListener.SetListenerLocation` writes the main camera's pose and attenuation
 position for the same listener (`FMOD.Studio.System.setListenerAttributes(int, ATTRIBUTES_3D, VECTOR)`) while
 MainCameraControl is disabled (the bridge drives the camera); S14.M4 leaves the scene's. SE's own listener is SE's camera
 (`MySandboxGame.UpdateSound`); SE's own 3D pan is round 4's (below).
 different thrusters in SE and just pasted an electric (Seamoth) engine sound on all of them"): under water each thruster plays
 SE's own cue of its definition (`BlockDefinition.PrimarySound`, per type: hydrogen, ion, atmospheric), muffled by point 2,
 started on the tick its strength passes 0.1
 with the S14 envelope on its emitter's `VolumeMultiplier`: level
 and strength to their targets at full scale per 200 ms up and per 300 ms down, volume = level x gain(strength), gain = 0.25 +
 0.75 strength^1.5 (small corrections quieter; S14.M7 = SE's own level, no envelope). No Subnautica loop by default. Option
 `/de sn s14 engine subnautica` (Subnautica side): hydrogen plays the Cyclops engine loop
 ion the Seamoth loop (S7's), atmospheric and other stay SE's;
 one loop per grid and type with the same envelope on the FMOD event (`rpm` parameter, `EventInstance.setVolume`; S14.M5 = S7's
 instant full-level loop); SE fades its own sound of those types to 0 over 300 ms (no double sound). Subnautica publishes the
 mode and the loaded loops in the **C7 S14 block (`SnStatus` 0xC4–0xC7, after S11's 0xC0–0xC3)**: 0xC4 u8 version 1, 0xC5 u8
 flags (1 SnLoops, 2 Hydrogen, 4 Ion: the types its loop plays), 2 reserved; version 0 = absent (SE keeps its own sound).
 Above water SE's own, unchanged: nothing is written there (a value S14 wrote under water is given back once). **Ships:** SE's ship sound system (on in this build) silences every silenceable block of a grid with a ship sound group

 and plays one thruster sound per type for the grid (`ShipEmitters`).
 S14 never writes a silenced block; under water it scales that grid-level sound in a postfix on the private
 `UpdateThrusterVolume(ShipEmitters, ShipEmitters, ShipThrusters, …)` (SE sets the main and idle volumes again each
 update, so the scale never compounds): the main emitter × the envelope of the type's strongest thruster, or main, idle and push
 emitters × a fade to 0 when Subnautica plays the type; `m_shipGrid` and `m_emitters` bound once (a missing member
 logs `DE.S14 MISSING …` and leaves the ship sound SE's). Row `s14.thrusters` counts `silenced`, `shipGrids`, `shipCovered` and
 `shipCoveredAudible` (a ship-level emitter of a covered type playing above 0.05, read from its voice: must be 0).
- *Point 5 (thrusters above the water).* Each above-water thruster flame of the S7 player also holds Subnautica's own Prawn jet
 (`Exosuit.fxcontrol.emitters[0].fx`, the S7p library's `ExosuitJets`), along the flame, local scale = radius x 1.6 x (0.3 +
 0.7 x intensity) clamped to [0. the spray where the plume meets the sea is S7p's P3.
 Review 4: the template's emission axis (its first ParticleSystem's transform in the template's frame x the shape module's
 rotation x +Z for a cone, box or rectangle shape or a disabled shape; reversed by a negative start speed) is turned onto the
 flame by a fixed rotation computed once (`DE.S14 jets above water: … corrected by N deg`, row `axis`).
 the 22:09 rows had a jet on every flame
 there but small and inside scene-above's puffs): `jets.old` scene-above's kit (S7pScene `S7pAboveFx.Sustains`, S14 hook) draws
 no thruster puff while the jets are on and their source is found, the S7 player ticking (`S7Effects.S14KitThrusterOff`;
 otherwise the kit as before); `jets.length` every particle system of a jet starts at the template's speed x clamp(SE's flame
 length / (the template's speed x lifetime x the jet's scale), 1, 20), scaling Hierarchy. S14.M6 = no jet and the puffs back.
* The world was Arcade (SE log "RealisticSound = False"
 at all three loads; `DE Lab S4/Sandbox_config.sbc` `<RealisticSound>false`; every `s14.muffle` row `realisticSound` false); the
 sound mode is the world's (Edit Settings > Advanced > Sound mode). (a) **The left ear** (`pan.stereo`, `pan.row`). SE's
 listener is the camera and its frame is right
 nothing of the plugins or the Water Mod
 moves SE's listener or an emitter's position. `MyXAudio2.Apply3D` calculates every
 3D voice with `new DspSettings(srcChannels, dst)` from the emitter's `SourceChannels` (the voice's buffer channels,;
 `MySourceVoice.GetOutputChannels`) but a helper emitter whose `ChannelCount` stays 1
 outside X3DAudio's contract (SrcChannelCount must equal the emitter's ChannelCount; SharpDX passes
 both as given), then swaps coefficients [1] and [2] and writes the matrix. 2-channel waves play in 3D
 where a cue has no 3D wave; SE's ship sounds
 play 3D in third person and 2D only in a small ship's first-person cockpit or inside a large ship
 (`MyShipSoundComponent.UpdateShouldPlay2D`): the "third person only" of the complaint. The Realistic setting changes
 this only for `m_realistic` voices; on DE's planet (atmosphere)
 both modes take the 3D path.
 grid itself for a large grid
 i.e. from the grid's AABB centre, 3D unless the character is
 enclosed (`m_insideShip`), while the third-person camera looks at the cockpit's head
 in the 10-18 save (401 of the session's 483 blocks) the grid's centre sits 7.5 m to the cockpit's left: SE's own geometry puts the
 whole ship sound on the left, in both sound modes. `pan.own`: the ship-level sounds of the grid whose ship controller the
 player controls play SE's 2D path in both views (a prefix and postfix on the private `UpdateShouldPlay2D`, set
 `m_shouldPlay2D` and its changed flag; SE's `UpdateSoundDimension` restarts the loops once); every other
 ship keeps SE's 3D. A postfix on `Apply3D` rebuilds the matrix of a voice with 2 channels, or not the count SE
 used, from one valid 1-channel X3DAudio calculation with SE's own listener and emitter: matrix[d x channels + s] = g[d] x 0.5
 (2 channels) x SE's linear distance factor; 1-channel voices keep SE's. Row
 `s14.pan` (1 Hz while an SE voice of a ship, the character or a thruster block plays; dev and release): per voice `src`,
 `d3`, `cue`, `realistic`, `seCh`/`ch` (SE's count / the voice's), `dist`, `geo` = dot(direction, camera right), `L`/`R` =
 the voice's own output matrix (`Voice.GetOutputMatrix`), `pan`, `oneSided`, and for the 2-channel 3D ones SE's matrix before
 the fix (`rawL`/`rawR`, `fixed`); `voices3D`, `judged`, `oneSidedCentred`, `signFlips`, `rawOneSidedCentred`, `stereo3D`,
 `fixed`, `mismatch`, SE's listener read back against the camera (`listenerFrontDeg`, `listenerTopDeg`, `listenerPosM`),
 `firstPerson`, `ownShip`, `ownShip2D`, `ownShip3D` (the controlled grid's playing ship sounds by path), `ownForced`. Judged by
 S14.N4P (no one-sided 3D voice within ~20 deg of front/back, signs agree beyond 0.3, listener <= 2 deg, no own-ship sound in
 3D); S14.N4Q (INFO) shows whether SE's own 2-channel matrix was the one-sided one. S14.M8 = SE's own pan (no rebuild, the own
 ship 3D). (b) **Too muffled** (`effect.chain`): an
 XAudio2 voice carries one filter, so the only stacking is a `Real*` recording (made to sound heard through a suit) under the
 effect: in a Realistic world `SelectCue`
 picks it where there is no air or in a small pressurized cockpit. Under water a
 postfix answers the pair's Arcade recording there (and `m_realistic` false: RealisticVolumeChange 1, positional); counted per
 modifiable play under water (`chainPlays`, `chainZero`, `chainOne`, `chainTwo`, `recordings`, `cueSwaps`, `mode` in
 `s14.muffle`), S14.N2C requires none stacked and >= 90 % exactly one, either mode (S14.M9 forces the Real* recording). The
 22:09 session had one filter (750 Hz) on every muffled sound: "too muffled" was LowPassCockpit itself, hence LowPassHelmet.
 (c) The ship-level `s14.engine` periodic row reports the envelope state the read factor was written with (`levelNow` = the
 tick's): the 22:09 N4V miss (410 of 442) was that one-update lag.
- *Rows.* SE `s14.jetpack` (change, 1 Hz while swimming: `swimHeld`, `jetpackRunning`, `state`, `idleLoopPlaying`,
 `runLoopPlaying` read from SE's emitters, `silenced`), `s14.muffle` (change, 1 Hz under water, 5 s above: `route`
 realistic|arcade|none, `plays`, `modifiable`, `applied` = the started modifiable sounds whose `m_activeEffect` is the effect,
 `gui`, `guiMuffled`, `reverbAvailable`, `reverbOn`, `sampleCutoffHz` = the XAudio2 filter of the last started modifiable sound
 read back from its voice), `s14.thrusters` (`registered`, `submerged`, per type `hydrogen`/`ion`/`atmospheric` and their
 `…Source` se | subnautica, `covered`, `coveredQuiet`, `seAudible`, `snMode`), `s14.engine` with `source` se (per SE thruster:
 start, attack, release, stopped, resume and 1 Hz for 4: `type`, `cue`, `level`, `rpm` = strength, `rpmS`, `volume`,
 `volumeRead` = the voice's multiplier, `attackMs`, `releaseMs`), `s14.weapons` (10 s: `weaponConfigs`,
 `functionalUnderwater`, the weapons under water within 400 m, `enabledUnderwater`, `workingUnderwater`); Subnautica
 `s14.listener` (1 Hz while the bridge drives the camera: `scene` (the StudioListener's path), `sceneOffsetM`, `sceneAngleDeg`,
 `readOffsetM`/`readAngleDeg` = FMOD's readback against the camera in the same instant, `overridden`; the analyzer reports
 S14.N4L INCONCLUSIVE when the scene's listener already sat on the camera), `s14.engine` with
 `source` subnautica (start, attack, release, stopped, resume and 1 Hz: `type`, `loop`, `level`, `rpm`, `rpmS`, `volume`,
 `volumeRead` (FMOD's getVolume), `attackMs`, `releaseMs`), `s14.jets` (1 Hz while a thruster burns above water: `aboveThrusters`, `jets`, `samples` intensity,radius,scale;
 round 4: `oldFlames` / `kitSkipped` = scene-above's thruster puffs drawn / left to the jets since the last row, `particles`,
 `visible` = jets a camera renders, `lengthFactor`, `flameLenM`, `naturalM`); round 4 SE `s14.pan` and the `s14.muffle` chain
 fields (above).
 Commands `de.s14` (dev only, not in the release table): SE `status | jetpack|arcade|gui|reverb|thrusters|pan|cue on|off | effect
 <name> | weapons check`; Subnautica `status | engine se|subnautica | listener|envelope|jets on|off | jetScale 0.1..4`. Off: `DEEPENGINEERS_S14=off`
 (both). Mutants S14.M1-M10 (SE: M1, M2, M3, M7, M8, M9, M10; Subnautica: M4, M5, M6).

**S15 conventions.**
Scope: ROADMAP §14 "### S15 Gameplay and immersion polish" and "### S15 addenda", docs/S15-BRIEF.md (the designer's list and
md).
Layout decision: **layout 1.0 suffices, no re-pin**: the voice counters travel in the C5 `SeState` pad after S16's block,
bytes 0xAC–0xCB (version byte 0 = absent; 2 since `voice 5`); the sky (SE's planets for Subnautica's picture) in C6's region
space past the 64-byte `SeTime` record, never inside it: the frame at region offset +0x100 (96 B), the body list at +0x200 (32 B
header + 16 × 64 B rows) and the events block at +0x700 (64 B, the sonar since `voice 5`), each with its own seqlock word at its
offset 0 (the S8a precedent of C2's region space); every earlier pin is unchanged. Code: Wire `S15Conventions` (the tables,
`CanonicalText`, `Hash`), `S15Rules` (`S15VoiceGate` over S16's `S16VoiceRule`, `S15AboardHold`, `S15PowerSwitch`,
`S15Wire`, `S15EventsWire`, `S15MixRules`, `S15SkyWire`, `S15Sky`, `S15Palette`, `S15Colour`); the Python twin
`tools/s15/s15_ref.py` (`--selftest`, `--vectors` into `tests/s15/vectors/`, `--tables`). SE: ` (game thread),
`S15SePicks.cs` (the sonar, the far sea), `S15SeLogic.cs` (the C5 block holder and the C6 writer `Link` calls), `S15Commands.cs`
(`de.s15`, dev); Subnautica: ` (the voice), `S15SnMix.cs` (the audio groups, AudioMix's S15 hook), `S15SnStory.cs`
(the story off), `S15SnSky.cs` (the sky), `S15SnPicks.cs` (the kept picks), `S15SnRules.cs` (the queue, the shared voice clock
`S15SnVoiceClock` S16Sn calls, the groups' levels, the line watch); the world mod `Data/TransparentMaterials.sbc` and
`Textures/DE_4546B_Water.dds` (generated by `tools/s15/gen_water_texture.py`), `Data/WaterConfig.xml` naming it with
`Transparent` false (checked by `tools/worldmod/s15_water.py` and SE's own serializer in
`tests/worldmod/DeepEngineers.WorldMod.Tests/S15MaterialTests.cs`).

*welcome`, `voice.power`, `voice.gate`, `voice.mix`, `voice.end`
`c5.s15`;* Only four lines remain: "Welcome aboard" (the Cyclops, a large grid or a
station), the Seamoth's own boarding line (a small grid), "Engine powering up", "Engine shutting down"; S16's "Hull integrity
critical" is S16's, unchanged. Removed from play (not defaulted off; their rules, rows, mutants and dev commands are gone): fire,
silent running, abandon ship and its countdown, hull breach and restored, engine power down on thrust, the oxygen warnings
(Subnautica's own `LowOxygenAlert` is no longer touched), the first-encounter lines; the once-per-session rule and the held edge of
SE decides every event on
its game thread every `ScanTicks`: the aboard grid is the cockpit the local character sits in
else the grid whose airtight room holds the head
else, for the
power lines only, a station whose world box holds the head (a base, a house); held
`AboardMs`; a door, a ladder or a ship's open deck is none of these. The welcomes: the aboard condition by seat or sealed room,
Welcome for a large grid or a station (`MyCubeGrid.GridSizeEnum`), SmallWelcome for a small dynamic grid; their rising edge
is the boarding, so a dive never greets. EnginePowerUp: the aboard grid powered, its distributor
 answering `ResourceStateByType(ElectricityId, false)`

 other than NoPower (SE's own PowerStateWorks, the state its OnPowerGenerationChanged follows): the edge is the power from none to some, whatever brought it
EnginePowerDown: `MyCubeGrid.IsPowerSwitchOn`
 off after it went off from a powered grid (`S15PowerSwitch`: SE's `SwitchPower` recomputes at once, so the
scan before decides): SE's Y key
the character's own SwitchReactors does nothing
so Y works seated. Never thrust or thruster strength. `S15VoiceGate` turns rising edges into events after
RearmMs (2 s for the power lines, 5 s for the welcomes) and S16's `S16VoiceRule` decides per event and grid (10 s for the power
lines: a quick Y double press says it once; 120 s for the welcomes): one rule, not two. An edge met while the S2 rule says SE's
audio is live (C5 `snBusTarget` < 0.5) is skipped: not sent, no cooldown started, never held or replayed. A grid that becomes
aboard takes its power and its switch as the baseline (boarding a powered grid is no EnginePowerUp, a switched-off one no
EnginePowerDown). A sent event increments its counter in the C5 block, version 2: 0xAC u8 version, 0xAD u8 flags (`C5Flags`: Aboard,
Seated, Room, Powered, Large, Station), 0xAE u16 0, 0xB0 u32 worldEpoch, 0xB4/0xB8/0xBC/0xC0 u32 the counters of Welcome,
EnginePowerUp, EnginePowerDown, SmallWelcome, 0xC4 i64 the aboard grid. Subnautica plays one line per increment (S16's
`S16SeqReader`: the first value is the baseline, several missed in one frame are one line, a new world epoch re-baselines), one
voice at a time both ways on one clock (`S15SnVoiceClock`: an S15 line waits `QueueGapMs` = S16's DuckMs after the last S15 or S16
voice, S16's hull voice waits `QueueGapMs` after an S15 line, held, never dropped, S16's creaks and groans duck under both; at most
`QueueMax` lines wait, a new line beyond that is dropped, a welcome replaces the newest other line); never started while its own C5
`snBusTarget` says SE's audio is live. The lines: the Cyclops prefab's own `SubRoot.welcomeNotification`,
`enginePowerUpNotification`, `enginePowerDownNotification` and the Seamoth's own boarding line,
`Vehicle.welcomeNotification`
 of the Seamoth prefab, else its
`Vehicle.enterSound`, else `SeaMoth.enterSeamoth`'s asset; each `VoiceNotification.sound` started as an FMOD
instance the way `FMODUWE.PlayOneShot` does at the camera at `S16VoiceMix.VoiceVolume` (1.6), as S16 plays
its hull voice, and followed to its end (`format voice.end`: its playback state, timeline position and length, the voice group's
lowest level and the master mute during it). The FMOD paths are prefab data: Subnautica logs each once
(`DE.S15 sound <event> = <member> path=…`).

*Subnautica's audio across the surface (`format audio.groups`
going above the water"; and a voice line under way cut the same way).* The S2 rule (one game's audio at a time) faded `bus:/` to C5
`snBusTarget` and muted it above water, music and voices included. Now Subnautica's own options VCAs carry the surface

the effects and ambience VCA = the player's slider value × the S2 rule's own fade (0 above water, as before), the music
and voice VCAs = the player's value × their group level, 1 under water and `Mix.MusicAbovePermille` / `Mix.VoiceAbovePermille`
(0.6) above it, linear over `Mix.CrossfadeMs` (2 s), so the music plays on and a line started under water ends above it;
`bus:/` stays at 1 and unmuted above water (still muted with no live SE, SE not in front, quitting: AudioMix's S15 hook, three
lines). The player's value is tracked by read-back (a VCA volume other than the one written last was SoundSystem's and becomes
the factor); every VCA written is given back when the groups go off. Without the three VCAs, with `DEEPENGINEERS_S15` "nomix" or
under S15.M19 the S2 rule's old `bus:/` fade and mute carry the surface.
`FeedbackPanel` freeze still pauses `bus:/`, music included, as before. SE's own audio is unchanged. The
above-water levels are tunable for a session (`de.s15 {music|voiceAbove: 0..1}` on Subnautica

*Subnautica's story off (`format story.off`, `aurora.off`
Subnautica's story is not played).* Every story line goes through `Story.StoryGoal.Execute(string, Story.GoalType)`

a Harmony prefix skips it (no line, no radio message, no goal
completion). The Aurora: `AuroraWarnings.Update` triggers its four warnings between
`CrashedShipExploder.timeToStartWarning` and `timeToStartCountdown`
and
`CrashedShipExploder.Update` then starts the countdown voice (`shipCountdownSound`), the explosion's sound,
the player's VFX, `MainCameraControl.ShakeCamera` (our picture's camera), `DamageSystem.RadiusDamage` 2000 at 500 m
and the model swap, and afterwards a camera shake and a rumble every 15-25 s in its biome: held off (a prefix moves both
times to `Story.AuroraNeverS` before each Update; an Aurora a save already exploded keeps its model and its Update is skipped).
Every PDA voice request is logged. The system lines kept (the Cyclops and
Seamoth set, the sonar, Subnautica's own oxygen alert) are no story goals.

*C, SE's planets in Subnautica's sky (`format c6.s15frame`, `c6.s15list`, `moons.hide`, `sky.disc`, `sky.dir`, `sky.size`).*
SE lists the planets every 300 ticks
nearest the bound planet first, and writes the list when it changes
(listSeq + 1) and every 300 ticks: rows 0x00 f64 x/y/z = centre − C (SE world), 0x18 f32 radius, 0x1C f32 atmosphere radius,
0x20 u16 palette, 0x22 u8 flags (1 Bound, 2 Atmosphere), 0x23 u8 name length, 0x24 ASCII name ≤ 28; header 0x04 u8 version 1,
0x05 u8 count, 0x08 u32 listSeq, 0x0C u32 worldEpoch. With every C6 write (Link.WriteSeTime, inside its lock) the frame: 0x04
u8 version 1, 0x05 u8 flags (1 Valid; 0 = SE's sky off), 0x06 u16 body count, 0x08 u32 listSeq, 0x0C u32 worldEpoch, 0x10 u64
tick, 0x18 f64 × 3 = P − C of `MySector.MainCamera` (SE world), 0x30 f32 × 9 = the rows of mapdir at P for the current chart
(`SiteBinding.Map`: SN = M × SE). Subnautica takes the frame's seq it finds first as a baseline (fresh only after a change)
and needs the frame's worldEpoch to be its C5's; it computes each body's direction (M × unit(centre − P)) and angular radius
(asin(R / d)) itself, hides its own two sky bodies

 and draws one camera-facing
disc per body (not the bound planet) at 0.9 of its camera's far plane, radius D tan(α), its vertex colours a flat palette
(`palette` rows: never SE's textures) lit by uSky's `SunDir` as a sphere, the limb toward the palette's limb colour for a body
with an atmosphere; Unity's own `Sprites/Default` (else `Hidden/Internal-Colored`) at a test run without depth write, shown
while the camera is above the sea and some of the body above the horizon. The hidden moon is judged on night captures of
Subnautica's picture along both signs of uSky's moon axis; the size row is
the transport of the list and the frame, with the pixels on a capture of each picture as information.

*B, one planet from orbit to the seabed (`format water.material`, `ending.palette`, `band.colour`, `air.colour`,
`orbit.colour`).* The sea of DE_4546B is drawn with the world mod's own `TransparentMaterial` `DE_4546B_Water` (the Water
Mod's JWater2 values, our generated texture) with the Water Mod's `Transparent` off

`Data/WaterConfig.xml` sets both (and the material below) for new worlds; the plugin applies them through
`de.water texture … transparent off material DE_4546B_Sea` once per world load

for the session).
`tools/s15/measure_ending.py` (palette only; the frames stay outside the repository): the sea, the clouds and their cover of
the face, the limb, the rim warm toward the sun and cool elsewhere (`ending` row). Two sea textures, because the Water Mod has
one surface material per planet and no distance term
the near one (110, 156, 184) is the texture Subnautica's near water at the switch is drawn from, the far one

both through the gains of run 20261024-054404 (both pictures at noon: SE drew (79, 121, 121) as (71, 99, 109) at the switch
and as (62, 105, 104) from 30 km). The SE plugin swaps the planet's texture to the far one above 20 km over the sea and back
below 15 km (`format water.far`; WaterLook, a LOD rebuild). The planet's Water Mod material is `DE_4546B_Sea`, a copy of
its own "Water" MaterialConfig with Reflectivity 0.5: run
20261024-063156 measured the band's near water at dMax 34 → 15, dL 13 → 3 with it; `de.water material=Water` brings the mod's
own back for a session and the band part takes its information picture with it. The band's far water is SE's atmosphere, not
the Water Mod (`format band.far`): SE's far water sits at its own horizon colour ((far − near) / (horizon − near) in luma 1.00
with the mod's material, 0.98 with ours: (164, 205, 239) and (159, 201, 236) against the horizon (171, 203, 235)) while
Subnautica's water fog leaves its far water near the water (0.15: (106, 135, 171) between its near water (92, 124, 162) and
its horizon (157, 208, 247)). The mod's billboards take only Reflectivity × max(fresnel, 0.1), the light (max(sun, ambient) ×
ColorIntensity), the specular and the texture, no fog above water, and Reflectivity
0.925 → 0.5 moved the far luma by −4 (the near by +15): no mod value brings (164, 205, 239) to (106, 135, 171) without
darkening the near water by the same factor. So the band row is split: the skies and the near water at T5's own, the far
water within T5 of the haze line, the segment from Subnautica's far water to its horizon in the same pair, nearest point in
RGB (in that run 9 / 1 at t 0.96 with the mod's material and 6 / 1 at t 0.91 with ours, against 66–69 / 63–67 to Subnautica's
far water itself; a teal or dark far sea fails: the near texture's old teal (72, 110, 112) is 59 off the line). SE's clock: S7's `de.s7 time` turns
only SE's sun field, which SE's renderer does not follow (run 20261024-041144: SE at night), and S7's time sync is off in the
test world, so the launcher sets SE's own clock
and Subnautica's together. The Water Mod softens its surface over min((altitude / 2000)², Radius × DistantSoftnessOffset) × 5 m
900 m from 27 km up with its default 0.003, so in that run the seabed under the
camera showed through from 30 km; `Data/WaterConfig.xml` sets the planet's `DistantSoftnessOffset` 0.0001 (a PlanetConfig
value, read at every world load: its own softness up to 4.9 km, 30 m above). The sea's seam (the S8a look sheet 20261024-124136: a
vertical step across the sea from 5 km, 44 luma, a band on the globe from 30 km, 32): the mod lights each surface quad as a whole, its
corner normal's `max(sunDot, AmbientColorIntensity) × ColorIntensity` plus, only where sunDot > 0, `pow(dot(n, h), Specularity) ×
SpecularIntensity` with a half vector built from the camera's planet-local position minus world quad corners
so at
the terminator the term switches on along the quad edges (0.2 against 0.64 in that run: × 1.7 on screen, as measured). That sheet's "noon"
was S7's `de.s7 time`, which reaches neither SE's renderer (MyGuiScreenGamePlay.Draw, runs inside MySandboxGame.PrepareForDraw
before our postfix) nor the mod
SE's real sun had the terminator through the site. At SE's real noon (`de.s15
clock`) the S15 air captures step 2-6 luma. `Data/WaterConfig.xml` sets the planet's `SpecularIntensity` 0 (PlanetConfig, every load; the
plugin's look defaults 0 too): the terminator is then the mod's continuous `max(sunDot, ambient)`, its quads' faceting left. Row S15.SEAM
(the air captures at SE's noon: step ≤ 12 luma), S15.SEAM.PAIR (information: at the site's sunrise with the mod's specular, the camera 4 km
east between two shots, the step moves with the ground; with ours, none). The rows: at the switch altitude T5's own for the skies and the
near water (compare.py bands, 24 per channel, 16 luma), the haze line for the far water; from 30 and 5 km looking down (`air`) and at the disc centre from 600, 200,
100 km (`orbit`, the disc from the geometry: the image's centre, asin(Rs / (Rs + h)) through the vertical field of view), at
most 10 % seabed pixels (a sea pixel: the larger of green and blue ahead of red by 25; SE's sea is teal) and the sea pixels' mean with the ending's hue
(r, g, b / sum within 30 per mille) and a luma from the ending's to the near water's (the ending is a low sun); the limb, the
rim and the cloud share against the ending as information. SE draws the Water Mod's surface after its atmosphere and cloud
layers, so from above the sea covers SE's haze and clouds: the haze shows at the rim only,
beyond the sea; no definition value changes that.

*The kept picks (docs/S15-DESIGN.md 8, 9, 3, 5, 11; `format c6.s15events`, `night.lights`, `lens.splash`, `sonar.ping`,
`seabase.room`, `fish.scare`).* SE counts the sonar in the events block of C6's region space at +0x700 (64 B, its own seqlock word
at 0; version 2: 0x04 u8 version, 0x05 u8 flags (1 SonarOn), 0x06 u16 count 0, 0x08 u32 worldEpoch, 0x0C u32 sonarSeq, the rest
zero; written with every C6 while S15 is active). The mirrored block lights (S7's kinds BlockPoint and BlockSpot) take 0.4 + 0.6 ×
`DayNightCycle.GetLightScalar` of their intensity (a one-line hook in `LightMirror.cs`); the camera surfacing after a second under
the sea calls `LensWater.CreateSplash`; a functional block of the aboard grid named with "sonar" and on pings every
5 s while seated (`CyclopsSonarButton.pingIterationDuration`): Subnautica plays the Cyclops's sonar sound and `SNCameraRoot.SonarPing`
in an airtight room under the sea (S7's inside flag) the `FMODGameParams` loops of
state `OnlyInBase` play as `OnlyInside` (S7's `IsInsideWalkable` answers) and a warm point light (255, 240, 220; 0.6; 8 m) follows
the camera; an S6 ship section faster than 4 m/s scares the small fish (`Scareable` with a `FleeWhenScared`) within 15 m
(`Creature.Scared`, `CreatureFear.SetScarePosition`, the flee action). The S6 and S11 hooks of the removed creature-attack line
(`S15Se.OnCreatureHit` in ` and `) are no-ops.

*Rows and control (dev).* SE `s15.site`, `s15.aboard` (`how` seat | room | station, `large`, `station`), `s15.live` (each change of
the S2 rule's live state), `s15.cond` (every change of a condition; an off row carries the last on sample's ms, a baseline row
`baseline`: the analyzer replays the gate; with `how`, `large`, `station`, `powered`, `powerBefore` = the scan before, `switchOn`,
`reactors`, `uraniumKg`, `thrust`), `s15.power` (each change of the aboard grid's power, switch, reactors and fuel, and at the
boarding), `s15.voice` (`result` played | cooldown | seAudio | forced, `held` (S15.M17 only), `seq`, `snBus`), `s15.skylist`,
`s15.sky` (every 60 ticks: each body's direction in SE's camera frame, its angular radius, the camera still or not), `s15.water`
(`applied`, `texture`, `transparent`, `seaMaterial`), `s15.waterswap`, `s15.sonar` (each ping); Subnautica `s15.voice` (`played`,
`reason`, `volume`, `snBus`, `source`, `path`, `coalesced`, `dropped`), `s15.voiceend` (`heardMs`, `lenMs`, `posMs`, `complete`,
`voiceMin`, `crossed`, `startedUnder`, `muted`, `groups`), `s15.mix` (each change, every `Mix.RowMs` while a group fades, else 5 s:
`active`, `why`, `sfx`, `music`, `voice`, their targets and above-water levels, `s2Target`, `muted`, the player's values and the
VCAs read back), `s15.story` (each story goal: `key`, `type`, `skipped`, `path` of its PDA line), `s15.pda` (each PDA line that
starts: `sound`, `subtitles`, `storyKey`), `s15.aurora` (the Aurora's state: held | exploded: Update skipped | running,
`countdownS`, `warningS`, `countdownPlaying`, `explodePlaying`), `s15.alert` (once per world: Subnautica's own LowOxygenAlert
`found`, `enabled`), `s15.skylist`, `s15.sky` (on presented SE ticks that are multiples of 60: each disc's direction in Unity's
camera frame from the transforms, its angular radius from its scale), `s15.moons` (every 2 s, measured from the skybox material),
`s15.night` (every 5 s), `s15.splash`, `s15.sonar`, `s15.seabase`, `s15.fish`. `de.s15 {status | spawn: sub|ship|deck | board |
unboard | reactor: grid, cell, fuel | fuel: grid, kg | power: on|off | thrust | play: welcome|engine|down|small | voice|sky|water:
on|off | water: apply | planet | orbit | look: [x, y, z] | shot | clock | sonar: grid, on}` (chat `/de s15 …`), Subnautica
`de.s15 {status | voice | sky | time | music: 0..1 | voiceAbove: 0..1 | story: aurora1..aurora4|key}` (the reply carries uSky's
moon axis and the shown discs); `DEEPENGINEERS_S15=off` (or `novoice`, `nosky`, `nowater`, `nomix`, `nostory`); mutants S15.M1,
M2, M4, M16, M17, M18 (SE: no rules, a deck counts, the previous see-through look at bind, EnginePowerUp on the thrust again, the
held welcome sent after the dive, the size ignored), S15.M3, M5–M7, M11–M15, M19, M20 (Subnautica: the disc on the unmapped
direction, the moons on, the size doubled, the S2 rule off, no night dimming, no lens splash, the sonar without its pulse, no base
hum, no fish scared, the music and the voice cut above water as before, the story on); S15.M8–M10 retired with their rules; oracle
`tests/s15/analyze_s15.py`, session `launcher.py s15` (parts selftest, voice, sky, band, air, orbit, sonar, life, teardown).

*Canonical text* (exact; ASCII, `\n` after every line, the last included; the order is the code's: header, the voice rows,
the const groups, the palette, orbit, ending and air rows, the mix's VCAs, the sonar, seabase and water rows, the format lines
sorted by key), FNV-1a 64
**`0xd5d5c7fdeeece0ec`** (126 lines, 16,636 bytes):
```
ext s15 voice 5
voice 1 Welcome key grid rearmMs 5000 cooldownMs 120000 member SubRoot.welcomeNotification prefab cyclops when boarding a large grid or a station
voice 2 EnginePowerUp key grid rearmMs 2000 cooldownMs 10000 member SubRoot.enginePowerUpNotification prefab cyclops when the aboard grid's power from none to some
voice 3 EnginePowerDown key grid rearmMs 2000 cooldownMs 10000 member SubRoot.enginePowerDownNotification prefab cyclops when the aboard grid's power switch off
voice 4 SmallWelcome key grid rearmMs 5000 cooldownMs 120000 member Vehicle.welcomeNotification prefab seamoth when boarding a small grid
const Voice AboardMs 1000
const Voice ScanTicks 10
const Voice SnBusLivePermille 500
const Voice QueueGapMs 3500
const Voice QueueMax 2
const Voice VolumePermille 1600
const C5 Offset 172
const C5 Size 32
const C5 Version 2
const C5Flags Aboard 1
const C5Flags Seated 2
const C5Flags Room 4
const C5Flags Powered 8
const C5Flags Large 16
const C5Flags Station 32
const Events Offset 1792
const Events Size 64
const Events Version 2
const EventFlags SonarOn 1
const Picks NightLightPermille 400
const Picks SplashMinUnderMs 1000
const Picks SonarPeriodMs 5000
const Picks SeabaseLightPermille 600
const Picks SeabaseLightRangeDm 80
const Picks FishScareSpeedDms 40
const Picks FishScareRangeM 15
const Picks FishScarePermille 1000
const Picks FishScanMs 1000
const Picks FishJumpSpeedDms 1000
const Mix MusicAbovePermille 600
const Mix VoiceAbovePermille 600
const Mix CrossfadeMs 2000
const Mix RowMs 250
const Story AuroraNeverS 1000000000
const Sky FrameOffset 256
const Sky FrameSize 96
const Sky ListOffset 512
const Sky ListHeader 32
const Sky RowSize 64
const Sky MaxBodies 16
const Sky NameMax 28
const Sky Version 1
const Sky ListRefreshTicks 300
const Sky DiscDistancePermille 900
const Sky DiscSegments 48
const Sky DiscRings 6
const Sky AmbientPermille 60
const Sky LimbPermille 850
const Sky DayAlphaPermille 850
const Sky NightAlphaPermille 1000
const Sky RenderQueue 2600
const Sky RowTicks 60
const SkyFlags Valid 1
const BodyFlags Bound 1
const BodyFlags Atmosphere 2
const Oracle AngleCentiDeg 100
const Oracle SizePermille 100
const Oracle StillCentiDeg 5
const Moons PlanetRadiusMicro 1000
const Moons TextureSize 4
const Colour TolChannel 24
const Colour TolLuma 16
const Colour CentrePermille 400
const Colour LimbInnerPermille 850
const Colour LimbOuterPermille 970
const Colour BandOffsetM 50
const Colour FacePermille 300
const Colour SeaLeadMin 25
const Colour CloudLumaMin 200
const Colour SeabedMaxPermille 100
const Colour SeaChromaPermille 30
palette 0 * rgb 128,128,128 limb 170,180,200
palette 1 DE_4546B rgb 65,97,122 limb 197,241,255
palette 2 EarthLike rgb 70,110,160 limb 140,190,255
palette 3 Moon rgb 150,150,150 limb 150,150,150
palette 4 Mars rgb 170,100,70 limb 210,160,130
palette 5 Europa rgb 200,215,230 limb 220,235,255
palette 6 Alien rgb 110,130,90 limb 170,220,160
palette 7 Titan rgb 200,170,120 limb 230,200,150
palette 8 Triton rgb 180,200,215 limb 220,230,240
palette 9 Pertam rgb 190,140,90 limb 230,190,140
orbit 600 km centre 34,59,75 limb 66,69,77
orbit 200 km centre 34,59,75 limb 66,69,77
orbit 100 km centre 34,59,75 limb 66,69,77
ending sea 34,59,75 cloud 120,114,110 cover 127 limb 66,69,77 rim warm 164,135,121 cool 82,85,94
air 30 km face 34,59,75
air 5 km face 34,59,75
mix vca sfx 634d9305-7695-40df-999a-cc1d049e1ebe music 0d0dd1e2-9d46-4a5b-aae8-ae412d827831 voice 3c4e7775-5fbe-4b00-83e0-35bc9add106a
sonar tag sonar
seabase light 255,240,220
water far DE_4546B_WaterFar texture Textures\DE_4546B_WaterFar.dds mean 62,96,122 up 20000 down 15000 seaMaterial DE_4546B_Sea reflectivity 0.5 plain Water
water DE_4546B_Water texture Textures\DE_4546B_Water.dds size 256 mean 110,156,184 previous JWater2 transparent off softnessOffset 0.0001 specularIntensity 0
format aboard the aboard grid is the grid of the cockpit the local character sits in (MyCharacter.UsingEntity as MyCockpit), else the grid whose airtight room (the S7 air rule) holds the character's head, else (for voice.power only) a station grid (IsStatic) whose world box holds the head: a base or a house; it counts after AboardMs; a door, a ladder or an open deck of a ship never counts
format air.colour SE's picture from a spectator over the site looking down at each air row's altitude (de.s15 orbit, SE's own screenshot): the face (the frame's central FacePermille circle) has at most SeabedMaxPermille seabed pixels (neither sea: the larger of green and blue ahead of red by SeaLeadMin, nor cloud: luma CloudLumaMin) and, for a row at or above WaterFar.SwapUpM (the far sea), its sea pixels' mean has the row's hue (each of r, g, b / (r + g + b) within SeaChromaPermille / 1000) and a luma from the row's less TolLuma to Subnautica's near water's plus TolLuma (the ending's low sun to the noon over the site); below it (the near sea, tuned for the switch) the hue is information
format audio.groups Subnautica's mix in groups: the effects and ambience group follows the S2 rule (C5 snBusTarget over crossfadeMs: 0 above water), the music and voice groups go to Mix.MusicAbovePermille and Mix.VoiceAbovePermille / 1000 above water and back to 1 under it over Mix.CrossfadeMs; bus:/ stays at 1 and unmuted above water (still muted with no live SE, SE not in front, quitting); without the VCAs the S2 rule's old bus:/ fade and mute
format aurora.off the Aurora's countdown and explosion never start (CrashedShipExploder: its warning and countdown times moved to Story.AuroraNeverS before each Update, so AuroraWarnings and the countdown sound, explosion, camera shake and radius damage never trigger); a save whose Aurora already exploded keeps its model and its Update is skipped (no periodic camera shake or rumble)
format band.colour the four bands of compare.py bands (sky_top, sky_horizon, far_water, near_water) of SE's picture at band enter + BandOffsetM against Subnautica's at band enter - BandOffsetM, same heading: sky_top, sky_horizon and near_water each within TolChannel per channel and TolLuma, far_water by band.far; the pair with the Water Mod's own material (WaterFar.PlainMaterial) is information
format band.far SE's far_water within TolChannel per channel and TolLuma of the haze line: the segment from Subnautica's far_water to its sky_horizon in the same pair (its nearest point in RGB; the luma against that point's). SE's atmosphere carries the far sea to its horizon colour within a few km (run 20261024-063156, luma: (far - near) / (horizon - near) 0.98, Subnautica's water fog 0.15) and no Water Mod value moves it (its billboards take Reflectivity x max(fresnel, 0.1), the light, the specular and the texture, no fog above water; Reflectivity 0.925 to 0.5 moved the far luma by -4, the near by +15), so any point of that line is either game's far sea; a far sea off it (teal, dark, land) fails
format c5.s15 SeState 0xac-0xcb, version 2 (0 = absent): flags (Aboard, Seated, Room, Powered, Large, Station), worldEpoch, the counters of events 1-4 (u32, never reset in an SE session), the aboard grid; a reader takes its first value as the baseline and plays one line per increment seen in the same worldEpoch (S16SeqReader)
format c6.s15events C6 region +0x700, its own seqlock, written with every C6 while S15 is active: version 2, flags (SonarOn: the sonar switch on aboard), count 0, worldEpoch, the sonar ping counter (read the c5.s15 way); the rest zero
format c6.s15frame C6 region +0x100, its own seqlock, written with every C6: tick, listSeq, camRel = P - C (SE world, f64), the rows of mapdir at P for the current chart (SN = M x SE, f32); a reader takes the seq it finds first as a baseline (fresh only after a change, 3 s) and the frame's worldEpoch must be C5's
format c6.s15list C6 region +0x200, its own seqlock: count <= MaxBodies, nearest the bound planet first: centre - C (SE world, f64), AverageRadius, AtmosphereRadius, palette, flags, ASCII name <= NameMax; written when the list changes (listSeq + 1) and every ListRefreshTicks
format ending.py, palette only): the sea, the clouds and their cover of the face, the limb, the rim warm and cool; the orbit and air rows' targets, the water texture's hue and the planet definition's atmosphere and clouds
format fish.scare a ship's proxy section (S6) faster than FishScareSpeedDms / 10 m/s (and not over FishJumpSpeedDms / 10: a jump) scares the small fish (a Scareable with a FleeWhenScared) within FishScareRangeM of it: Creature.Scared + FishScarePermille / 1000, CreatureFear.SetScarePosition at the section, the flee action started; checked every FishScanMs
format lens.splash Subnautica's LensWater.CreateSplash on the main camera when the camera rises through the sea surface after SplashMinUnderMs under it
format moons.hide Subnautica's two sky bodies off: uSkyManager.planetRadius PlanetRadiusMicro with the planet held opposite the sun (planetOrbitSpeed 0, planetZenith from the sun: no eclipse), the planet's and the moon's coronas rgb 0 (alpha kept), MoonTexture transparent; the constant properties sent again to the skybox, skymap, fog and water materials; everything restored when the sky is off; judged on night captures of Subnautica's picture along both signs of MoonLight's axis (the centre no brighter than the sky around it by TolLuma)
format night.lights the mirrored block lights (S7's kinds BlockPoint and BlockSpot) at NightLightPermille / 1000 + (1 - NightLightPermille / 1000) x DayNightCycle.GetLightScalar of their intensity; the character's lamps untouched
format orbit.colour SE's picture from a spectator over the site looking at the planet centre at each orbit row's altitude: the disc's inner CentrePermille radius (centre) judged as air.colour's face against the row's centre; information: its LimbInnerPermille-LimbOuterPermille ring against the row's limb, the rim beyond the disc against the ending's warm and cool rim, the centre's cloud share against the ending's cover
format seabase.room the camera in an airtight room under the sea (S7's inside flag): the FMODGameParams loops of state OnlyInBase play (their state set to OnlyInside, which S7's IsInsideWalkable answers) and one point light of SeabaseLight colour, SeabaseLightPermille / 1000 intensity and SeabaseLightRangeDm / 10 m range follows the camera; both restored when it leaves
format sky.dir each body's direction in the camera frame (right, up, forward): SE from its camera matrix and the body's centre, Subnautica from Unity's camera transform and the disc's position; the angle between them <= AngleCentiDeg / 100 degrees on the same SE tick while the SE camera is still
format sky.disc a camera-facing disc per body except the bound planet at DiscDistancePermille of the camera's far plane, radius D tan(alpha); vertex colours the palette lit by uSkyManager.SunDir as a sphere (floor AmbientPermille), the limb blended to the palette's limb colour past LimbPermille of the radius for a body with an atmosphere; Sprites/Default at RenderQueue, no depth write; shown while the camera is above the sea
format sky.size each body's angular radius asin(AverageRadius / distance): Subnautica's disc (atan(radius / distance) of its transform) within SizePermille / 1000 of SE's (the transport of the list and the frame); measured in pixels on a capture of each picture aimed at the body (atan(r px / focal px) from the vertical field of view) as information
format sonar.ping a functional block of the aboard grid whose CustomName holds SonarTag (any case) and is on, while seated, pings every SonarPeriodMs: SE counts it in the C6 events block; Subnautica plays the Cyclops's sonar sound (CyclopsSonarButton.soundFX's asset) and SNCameraRoot.SonarPing (the SonarScreenFX pulse), only with its audio live
format story.off Subnautica's story goals are skipped; every PDA voice request (SoundQueue.Play) is logged; the system lines DeepEngineers keeps (the Cyclops and Seamoth set, the sonar, Subnautica's own oxygen alert) are not story goals
format voice.end a line started under water plays to its end above water: the voice group stays live across the surface (audio.groups); each played line is followed to its end (its playback state, timeline position and length) with the voice group's lowest level and the master mute during it
format voice.gate per (event, grid): a rising edge of the event's condition after it was off RearmMs is an event; S16VoiceRule (one per event, CooldownMs) decides; a grid that becomes aboard takes its power state and its switch as the baseline (boarding a powered grid is no EnginePowerUp, a switched-off one no EnginePowerDown); no once-per-session rule
format voice.mix Subnautica plays each line (SubRoot.<member>.sound of the Cyclops prefab, Vehicle.welcomeNotification.sound of the Seamoth prefab) as an FMOD instance at the camera at S16VoiceMix.VoiceVolume, never started while its C5 snBusTarget says SE's audio is live; one voice at a time both ways on one shared clock: an S15 line waits QueueGapMs after the last S15 or S16 voice, S16's hull voice waits QueueGapMs after an S15 line, S16's creaks and groans duck under both; at most QueueMax lines waiting (a new line beyond that is dropped, a welcome replaces the newest other line)
format voice.power EnginePowerUp while the aboard grid is powered: its resource distributor's electricity state (MyResourceDistributorComponent.ResourceStateByType without a grid: the grid's whole network, read without a recompute) is not NoPower, so the edge is the grid's power going from none to some (a reactor placed or fuelled with uranium, the power switch on, any source); EnginePowerDown while the grid's power switch is off (MyCubeGrid.IsPowerSwitchOn false: SE's Y key, MyShipController.SwitchReactorsLocal) after it went off from a powered grid; never thrust or thruster strength
format voice.welcome Welcome while the aboard grid is a large grid or a station (MyCubeGrid.GridSizeEnum Large or IsStatic) and the player is in its seat or a sealed room of it; SmallWelcome the same for a small dynamic grid (the Seamoth's own boarding line, Vehicle.welcomeNotification of the Seamoth prefab, else its Vehicle.enterSound); the rising edge is the boarding itself, so a dive never greets
format water.far the far sea: above WaterFar.SwapUpM over the sea the SE plugin sets the planet's texture to WaterFar.Material and back to Water.Material below WaterFar.SwapDownM (once per crossing, a row each), never under S15.M4 or with the water switch off; the near texture is tuned for the switch (Subnautica's near water), the far one for the ending's hue
format water.material DE_4546B's sea uses the world mod's TransparentMaterial Material (Texture, generated, its mean sRGB pinned: the ending's sea hue at the near water's brightness through the band pair's gains) with the Water Mod's Transparent off, its MaterialId WaterFar.SeaMaterial (its own Water material with Reflectivity WaterFar.SeaReflectivity: the near water at the switch, run 20261024-063156), its DistantSoftnessOffset for the planet (the surface's soft fade capped at 30 m above 4.9 km) and its SpecularIntensity Water.SpecularIntensity (the mod adds a specular term to a quad only where its sunDot > 0, with a half vector mixing planet-local and world space: a step along the quad edges at the sea's terminator, the seam of the look sheet 20261024-124136); Data/WaterConfig.xml sets the five for new worlds (the softness and the specular at every load; the plugin's look defaults keep the specular at Water.SpecularIntensity), the SE plugin through de.water texture, transparent and material once per world load for an existing world (when MyTransparentMaterials.water material=WaterFar.PlainMaterial hold for the session); judged on captures (air.colour, orbit.colour), the read-back is information
```

**S12 conventions.** Scope: ROADMAP §14 "### S12 Interiors v2", docs/S12-BRIEF.md. Layout decision:
**layout 1.0 suffices, no re-pin**: the flood and vent rows travel in C6's region space at +0x800 (32-byte header + 8 × 128-byte
rows, to +0xC1F), past S15's blocks (+0x100, +0x200..+0x61F, +0x700..+0x73F), with their own seqlock word at their offset 0 (the
S15/S8a precedent); version byte 0 = absent; every earlier pin is unchanged. Code: Wire `S12Conventions` (the constants,
`CanonicalText`, `Hash`), `S12Rules` (`S12AirVolume` (format air.volume, air.hold, air.water's latch `Wet`, `CameraInRoom`:
air.camera), `S12AirHold`, `S12Fill` (flood.fill, flood.drain's `Water(o2, env)`), `S12FloodTrack` (flood.breach, flood.drain
with its own `DrainMs`), `S12VentEdge` (vent.event), `S12LookRules` (look.when, the bounce light), `S12Wire` (c6.s12),
`S12Boxes` (flood.plane's `Decompose` and `ForRow`)); the Python twin `tools/s12/s12_ref.py` (`--selftest`, `--vectors` into
`tests/s12/vectors/`, `--tables`). SE: ` (update thread: `S12Air` behind the S12 hook of `InteriorProbe.Decide`,
`S12Rooms` behind the hook of `InteriorRooms.Build` with a per-gas-system count of `OnProcessingDataComplete`, `S12Floods` (the
water cells' boxes, the re-pick of a null watched cell, `SurfaceAt`: flood.surface), `S12Vents` (the room at the vent's dummy),
`S12Se` (breach, repair, the test-only block swap and the vent rig, the `s12.char` rows)), `S12SeLogic.cs` (mutants S12.M1–M5
and M8–M12, switches including `third` and `charrows`, the snapshot, `S12Water`, the C6 writer), `S12Commands.cs` (`de.s12`,
dev); S2b's `SwimController.Tick` takes a flooding room's plane as the surface (one S12 hook); Subnautica: ` (main
thread: planes, splash, vent bubbles, the look, `de.s12`), `S12SnRules.cs` (mutants S12.M6–M7, the start reader, the easing,
the plane corners), `S12EffectsPlayer.cs` (S7's effect player's S12 entry points: its bubble budget and audio rule). Oracle
`tests/s12/analyze_s12.py`, session `launcher.py s12`.

*The air rule (decided by evidence).* The streaming gate `S1Telemetry.AreaBuilt = airAbove || camBox` is
the near-field "area built" test and does not grant air; the air (C1 `InRoom`: Subnautica's `IsInsideWalkable`, SE's swim gate
and its oxygen rule, the audio muffle) is S7's `InteriorProbe`: the room at the camera's cell (`WorldToGridInteger` rounding)
with a held per-grid answer while the gas system recomputes.
the character's (se-20261023-220926: 24 rows in a room with the character's eye not in it; se-20261024-102820: 5, seated, the
third-person camera passing the hull), the raw cell flips on every jitter across a face, and S7's held answer keeps the air
of a grid that recomputes after the camera left. S12 replaces both: the oxygen-volume test with a hysteresis margin per probe
point and grid (one flip per crossing of a room's face); the air reaches the room cells' faces, which are SE's airtight faces
a flat window's cell is room up to its glass (round 2's
mid-plane put a diver pressed against a window from inside out of the room, se-20261024-163424 14:41:24: S12.M9); in third
person the local character's eye must be in a room too; while SE recomputes, the snapshot taken after that gas system's last
`OnProcessingDataComplete`, else each probe point keeps its previous answer; a room that is not
airtight is water, whatever its drawn plane shows (round 2's air pocket above a rising breach: S12.M8); an airtight room with a
flood row has air above its plane with a latch at S2b's swim thresholds, and that plane is the character's water surface for
S2b's swim model (format flood.surface; round 2 swam against the sea 20 m up and flapped at the plane: S12.M11); S12.M1 brings
the S7 rule back. SE's own environment oxygen is SE's: the room's in an airtight room, else the Water Mod's provider (none
with the head under its water: WaterOxygenProvider).
airtight room (sealed by Window1x2Flat blocks, airtight by their full Right mount face, CubeBlocks_Windows.sbc), drawn by
Subnautica with S5's glass style, nearly invisible under water.

*SE's flood facts (VERIFIED).* A breach sets the airtight room's `IsAirtight` false and its `OxygenAmount` 0 at once and merges
its cells into the outer room; an airtight block placed in a
cell nulls that cell's room, so no room at the watched cell decides nothing and the
cell is picked again. SE keeps no fill level, so the rise is ours

A room SE rebuilds after the repair starts at its environment oxygen
so the water is judged against `EnvironmentOxygen` and drains at
`DrainMs` (format flood.drain); an Air Vent moves the oxygen
 of the room at its vent dummy. The vent's state:
`Status` is Pressurizing for any airtight room below full oxygen whether gas flows or not (MyAirVent `UpdateStatus`), so the
effect follows SE's own `m_isPlayingVentEffect` (set by `CheckForVentEffect` when gas moves). A flood row's boxes are the
room's water cells (its cells less the window, door and slope cells on its skin), never their bounding box (S12.M10). SE
flips a room's airtightness with every door cycle (IsAirtightBlock's PressurizedClosed), so a breach drains only after its
room stayed airtight RepairDebounceMs and a breach soon after goes on as the same flood; a draining room's boxes follow its

the house's 136-cell room rose and drained six times in 30 s, a chamber drawn with a plane at its ceiling and one on its
floor: S12.M12). Subnautica's `de.s12` reads the chat's words itself (`/de sn s12 look off` arrives as `{"argv": ["look",
"off"]}`, which S3Args.

*Canonical text* (exact; ASCII, `\n` after every line, the last included; the order is the code's: header, the const groups,
the header and row field lines, the format lines sorted by key), FNV-1a 64 **`0x9aa269decc02e106`** (109 lines, 10,496 bytes):
```
ext s12 interiors 4
const Air HysteresisPermille 40
const Air SnapshotMaxCells 262144
const Air WaterEnterMm 100
const Air WaterExitMm 100
const Look AmbientPermille 450
const Look SpecPermille 600
const Look TintRgb 12110046
const Look CausticsPermille 0
const Look SunShafts 0
const Look BouncePermille 150
const Look BounceRgb 16770764
const Look BounceRangeCells 4
const Look BounceMaxIntensityPermille 800
const Look BounceHeightMm 300
const Look RefTolPermille 120
const Look ExteriorTolPermille 20
const Look DarkerPermille 40
const Look WindowTolPermille 40
const Look LineTolPermille 60
const Look LineMinStepPermille 40
const Flood DischargePermille 600
const Flood GravityMmS2 9810
const Flood MinFillMs 2000
const Flood MaxFillMs 60000
const Flood MinDepthMm 500
const Flood HoleCellsMax 4
const Flood FullPermille 990
const Flood DryPermille 10
const Flood RangeM 60
const Flood OxygenStartPermille 20
const Flood RowTicks 6
const Flood DrainMs 20000
const Flood DryEnvMinPermille 50
const Flood RepairDebounceMs 2000
const Flood ReBreachQuietMs 5000
const Vent BubblesPerS 40
const Vent BurstBubbles 60
const Vent SplashBubbles 80
const Vent CaptureMinPermille 30
const C6 Offset 2048
const C6 HeaderSize 32
const C6 RowSize 128
const C6 MaxRows 8
const C6 MaxBoxes 6
const C6 Size 1056
const C6 Version 1
const C6Flags Active 1
const C6Flags AirRule 2
const C6Flags Look 4
const RowKind none 0
const RowKind flood 1
const RowKind vent 2
const FloodState none 0
const FloodState rising 1
const FloodState full 2
const FloodState draining 3
const FloodSource none 0
const FloodSource breach 1
const FloodSource oxygen 2
const VentState none 0
const VentState pressurizing 1
const VentState depressurizing 2
const UpAxis PosX 1
const UpAxis NegX 2
const UpAxis PosY 3
const UpAxis NegY 4
const UpAxis PosZ 5
const UpAxis NegZ 6
field s12 header seq 0x00 4 u32
field s12 header version 0x04 1 u8
field s12 header flags 0x05 1 u8
field s12 header count 0x06 1 u8
field s12 header reserved 0x07 1 u8
field s12 header worldEpoch 0x08 4 u32
field s12 header floodSeq 0x0c 4 u32
field s12 header ventSeq 0x10 4 u32
field s12 header tick 0x14 8 u64
field s12 header reserved2 0x1c 4 u32
field s12 row gridId 0x00 8 i64
field s12 row trackId 0x08 4 u32
field s12 row kind 0x0c 1 u8
field s12 row state 0x0d 1 u8
field s12 row source 0x0e 1 u8
field s12 row boxCount 0x0f 1 u8
field s12 row level 0x10 4 f32
field s12 row ratePerS 0x14 4 f32
field s12 row startSeq 0x18 4 u32
field s12 row gridSize 0x1c 1 u8
field s12 row upAxis 0x1d 1 u8
field s12 row flags 0x1e 2 u16
field s12 row point 0x20 12 f32x3
field s12 row dir 0x2c 12 f32x3
field s12 row boxes 0x38 72 i16x6x6
format air.camera C1 InRoom (Subnautica's inside-walkable picture, SE's audio muffle) is the camera's answer in first person and for the spectator; in third person (the camera controller is the local character or its seat, not in first person) it also needs the local character's eye in a room (its swim-gate answer of this tick or the last two): a character outside the hull never gets the dry picture
format air.flood a room that is not airtight is water: while a breach flood rises or is full (format flood.breach) the room gives no air to the camera or to any character, whatever its drawn plane shows (SE's own environment oxygen there is the water's); only an airtight room with a flood row has air, above its plane (format air.water)
format air.hold per probe point and grid: in stays true while d > -h and becomes true when d > +h, h = g x HysteresisPermille / 1000; one flip per crossing of a room's boundary; while the gas system processes, d comes from the grid's room snapshot (InteriorRooms: GetRooms, each airtight MyOxygenRoom.Blocks) taken after the gas system's last OnProcessingDataComplete, else (no snapshot, or an older one) the probe point keeps its previous answer for that grid
format air.volume the probe point p (grid-local, metres), its cell c = round(p / g) and f = p / g - c; the distance to the face of c in direction s e_a is g (1/2 - s f_a); when c is a cell of an airtight room the depth d = min over the faces whose neighbour cell is not of the same room of that distance, g / 2 with no such face (the air reaches the room cells' faces, where SE's airtight faces are: MyGridGasSystem.IsAirtightBlock; a window's, a door's or a slope's cell that SE counts as room is air up to its sealed face); else d = -(min over the faces whose neighbour is a cell of an airtight room of that distance), -g / 2 with none; the room cells are MyGridGasSystem.GetOxygenRoomForCubeGridPosition (IsAirtight) when the gas system is not processing, else format air.hold's
format air.water a probe point in an airtight room with a flood row (format flood.plane) has air only above the plane, with a latch per probe point and grid: water once its height above the room's water floor along upAxis < level x the water height - WaterEnterMm / 1000, air again once > level x the water height + WaterExitMm / 1000 (S2b's swim entry and grounded exit: SwimThresholds.SwimEnterEyeY, SwimExitGroundedEyeY)
format c6.s12 C6 region +0x800, own seqlock, written with every C6 while S12 is active: header (version, flags Active|AirRule|Look, count <= MaxRows, worldEpoch, floodSeq and ventSeq summed over the rows' starts, tick), then count rows of RowSize: one per flooding room (kind flood) and one per venting Air Vent (kind vent) within RangeM of the camera, nearest first; version 0 = absent
format flood.breach a room tracked while airtight whose watched cell (its snapshot's first cell) lies in a room that exists and is not airtight on a completed gas system (SE sets IsAirtight false and OxygenAmount 0 at once and merges the cells: MyGridGasSystem.RemoveBlock; a door that opens: IsAirtightBlock's PressurizedClosed) starts a breach flood (startSeq + 1) when its box centre is under the sea; no room at the watched cell (a block placed there: MyGridGasSystem.AddBlock nulls it) decides nothing, and the watched cell is picked again from the next snapshot's room over the track's cells; a repaired room drains once it stayed airtight RepairDebounceMs, a breach within ReBreachQuietMs of the drain's start goes on as the same flood (no new start), a later one rises again (startSeq + 1); a draining room's boxes follow its airtight room, and of two active tracks over the same cell only the one with the newer geometry draws it (one plane per cell)
format flood.drain the water an airtight room's oxygen gives is w = clamp((D - OxygenLevel) / D, 0, 1), D = MyOxygenRoom.EnvironmentOxygen when it is >= DryEnvMinPermille / 1000 (a room SE rebuilds on a breathable planet starts at it: MyGridGasSystem.CreateAirtightRoom), else 1; after the repair the level moves toward w no faster than a full room in DrainMs and never above the level at the repair; an airtight room under water whose vent depressurizes it floods with level = w once w >= OxygenStartPermille / 1000 (source oxygen); a row ends when level <= DryPermille / 1000 while draining
format flood.fill a breach rises at level(t) = min(1, t / T), T = clamp(V / Q, MinFillMs, MaxFillMs), V = the room's cells x g^3, Q = DischargePermille / 1000 x A x sqrt(2 x GravityMmS2 / 1000 x max(depth, MinDepthMm / 1000)), A = min(holes, HoleCellsMax) x g^2 (holes >= 1), depth = the room box centre's depth below the sea (m); rising becomes full at FullPermille / 1000
format flood.plane a flood row's boxes are the room's water cells (its cells less the cells whose block has a face on the room's boundary: windows, doors, slopes; MyCubeGrid.GetCubeBlock) decomposed into boxes (the lowest remaining cell in z, y, x order: its run along x, grown along y, then z), the MaxBoxes largest by cells, never their bounding box; Subnautica draws one water plane per box (the box's cross-section normal to upAxis at the box floor + level x the height of the boxes' union, under the mirrored grid's Entity; Unity-local = SE-local with z negated), with BaseWaterPlane's own material (its private waterRender's material, read from a loaded BaseWaterPlane) or a transparent fallback; the level eased toward the row's (ratePerS between records); one splash per startSeq increment (Subnautica's Vehicle.splashSound and SplashBubbles on the room floor under the row's point, through S7's effect player and its caps)
format flood.surface inside an airtight room with a flood row the room's plane is the character's water surface: S2b's swim model takes the eye's height above the plane (the plane's SN y at the eye) instead of above the sea (its entry below -0.1 m, exit above +0.8 m or +0.1 m grounded) and SE walks above it; when the row ends the room is dry and the swim gate's airtight room releases the swim latch at once (SwimExit.Room)
format look.preset caustics: WaterscapeVolume.causticsAmount x CausticsPermille / 1000; sun shafts: WaterSunShaftsOnCamera.enabled = SunShafts != 0; the interior chunks' sky: a copy of MarmoSkies' BaseInterior sky with DiffIntensity x AmbientPermille / 1000, SpecIntensity x SpecPermille / 1000 and its SH coefficients x TintRgb / 255 per channel; a floor bounce point light (no shadows) BounceHeightMm above the room floor under the camera, colour BounceRgb, intensity min(BounceMaxIntensityPermille / 1000, BouncePermille / 1000 x the brightest mirrored lamp within BounceRangeCells cells), range BounceRangeCells x g; each value restored when the look ends
format look.when the preset applies while de.s12 look is on, the camera is in an airtight room (C1 InRoom, the S12 air rule) and the camera is under the sea (SN y < 0); the exterior is untouched
format vent.block SE's own Air Vent carries the effect; no world-mod block
format vent.event an Air Vent within RangeM of the camera vents while SE plays its own vent effect (MyAirVent.m_isPlayingVentEffect, set by CheckForVentEffect from VentToRoom/DrainFromRoom) or while de.s12 vent drives it (dev): one row (kind vent, state Pressurizing or Depressurizing from IsDepressurizing, point = the vent's centre, dir = its forward; its room is the room at its vent dummy: MyAirVent.GetOxygenBlock), startSeq + 1 at each start; Subnautica bubbles BurstBubbles at a start and BubblesPerS while it vents (S7p's per-frame bubble budget), no sound of its own (SE's PressurizeSound/DepressurizeSound play on SE's side)
```
Pinned by `S12ConventionsTests` (text byte for byte, hash, the C6 block past S15's and inside C6's region, layout 1.0 and the
S7/S15/S16 pins unchanged, de.s12 dev-only) and by the Python twin (`s12_ref.py --selftest`). Changing a line bumps
`ext s12 interiors <n>` and updates both pins.

**S17 conventions.** Scope: docs/S17-BRIEF.md
Layout decision: **layout 1.0 suffices, no re-pin**
Subnautica fills the C9 `SnCreatures` table (laid out since S0, written by nobody before S17; its v1 consumers were logs
and tests) with the nearby fish of the kind table, C9 `flags` bit 8 = `Catchable` (bits 0-1 keep the layout's hunting /
targetIsMirror); SE's catch request travels in the C5 `SeState` pad after S15's block, bytes 0xCC-0xEB, and Subnautica's
answer in the C7 `SnStatus` pad after S14's block, bytes 0xC8-0xD7, each with its version byte (0 = absent). One request
in flight with its own sequence number: Subnautica acts once per `reqSeq`, SE gives the item once per matching `ackSeq`,
so no ring is needed; a new C4/C8 message type or ring was not needed. Neither the S11 C8 units nor S6's damage path has a
request/ack of this shape (they are one-way SN -> SE records), so S17 adds its own blocks. The kinds are the edible fish
of the S8a spawn table (`format spawn.table`; Mesmer is not edible); S11's tables hold no small fish, and adding a kind to
the S8a spawns is its own change. Code: Wire `S17Conventions` (the constants, the kind table, `CanonicalText`, `Hash`),
`S17Wire` (the C5/C7 codec), `S17Aim`, `S17CatchBook` (SE's one-in-flight bookkeeping); SE ` (the target,
F, the item, the HUD line, `de.s17`), ` (the C5 block `Link.WriteC5` fills, the switch); Subnautica
` (the C9 writer, the catch, the C7 block, `de.s17`) and the tombstone hook `S8aSpawns.OnCaught` (a caught spawn-point fish does not respawn until the camera leaves the point); Wire `S17C9` is the C9 codec both sides use; the world mod `Data/PhysicalItems_DE_Food.sbc` and
`Data/Blueprints_DE_Food.sbc` (checked by `tools/worldmod/s17_food.py`); the Python twin `tools/s17/s17_ref.py`
(`--selftest`, `--vectors` into `tests/s17/vectors/`); the oracle `tests/s17/analyze_s17.py` (`S17.CATCH.*`). Rows:
`s17.catch` on both sides (SE phases request, ack, given, refused, timeout, late; Subnautica one row per request handled
with its result). `DEEPENGINEERS_S17=off` (either side) restores the earlier behaviour: no C9 rows, no request or answer
block (version 0), no catch. Control (dev builds): `de.s17 {status}` on both sides (`/de s17 status`, `/de sn s17 status`).
Multiplayer is not covered: the item is added by the local client's plugin (single-player worlds).

*Canonical text* (exact; ASCII, `
` after every line, the last included; header, const groups, fields, kinds, format lines
sorted by key), FNV-1a 64 **`0x120ee00b7501f416`** (70 lines, 5,469 bytes):
```
ext s17 fish 2
const C9 RowsMax 32
const C9 RadiusCm 2000
const C9 PeriodMs 100
const C9 StaleMs 1000
const C9Flags Catchable 256
const Catch ReachCm 250
const Catch AimCm 60
const Catch LeadMaxMs 200
const Catch SlackCm 150
const Catch AckTimeoutMs 1000
const Catch UnpublishedMs 5000
const Catch ForgetMs 30000
const C5 Offset 204
const C5 Size 32
const C5 Version 1
const C5Flags Active 1
field s17 version 0xcc 1 u8
field s17 flags 0xcd 1 u8
field s17 reserved 0xce 2 u16
field s17 reqSeq 0xd0 4 u32
field s17 instanceId 0xd4 4 u32
field s17 techType 0xd8 4 i32
field s17 posSn 0xdc 12 f32x3
field s17 worldEpoch 0xe8 4 u32
const C7 Offset 200
const C7 Size 16
const C7 Version 1
field s17ack version 0xc8 1 u8
field s17ack result 0xc9 1 u8
field s17ack reserved 0xca 2 u16
field s17ack ackSeq 0xcc 4 u32
field s17ack instanceId 0xd0 4 u32
field s17ack techType 0xd4 4 i32
const Result Caught 1
const Result Gone 2
const Result Moved 3
const Result Kind 4
const Result Off 5
const Refusal Busy 1
const Refusal Again 2
const Refusal Timeout 3
const Refusal Late 4
const Refusal NoItem 5
const Refusal NotCaught 6
const Food RawMicro 2670
const Food CookedMicro 5340
const Food TimeS 30
const Food CookS 4
const Item MassG 1500
const Item VolumeMl 1250
const Item SizeMm 150
kind Peeper 2504 name Peeper raw DE_Fish_Peeper_Raw cooked DE_Fish_Peeper_Cooked blueprint DE_CookFish_Peeper
kind Bladderfish 2515 name Bladderfish raw DE_Fish_Bladderfish_Raw cooked DE_Fish_Bladderfish_Cooked blueprint DE_CookFish_Bladderfish
kind Boomerang 2510 name Boomerang raw DE_Fish_Boomerang_Raw cooked DE_Fish_Boomerang_Cooked blueprint DE_CookFish_Boomerang
kind Hoopfish 2531 name Hoopfish raw DE_Fish_Hoopfish_Raw cooked DE_Fish_Hoopfish_Cooked blueprint DE_CookFish_Hoopfish
kind Spadefish 2520 name Spadefish raw DE_Fish_Spadefish_Raw cooked DE_Fish_Spadefish_Cooked blueprint DE_CookFish_Spadefish
kind Reginald 2519 name Reginald raw DE_Fish_Reginald_Raw cooked DE_Fish_Reginald_Cooked blueprint DE_CookFish_Reginald
kind Eyeye 2513 name Eyeye raw DE_Fish_Eyeye_Raw cooked DE_Fish_Eyeye_Cooked blueprint DE_CookFish_Eyeye
kind HoleFish 2501 name Holefish raw DE_Fish_HoleFish_Raw cooked DE_Fish_HoleFish_Cooked blueprint DE_CookFish_HoleFish
kind Spinefish 2546 name Spinefish raw DE_Fish_Spinefish_Raw cooked DE_Fish_Spinefish_Cooked blueprint DE_CookFish_Spinefish
kind GarryFish 2507 name Garryfish raw DE_Fish_GarryFish_Raw cooked DE_Fish_GarryFish_Cooked blueprint DE_CookFish_GarryFish
format aim the target is the Catchable C9 row nearest the eye with t = (p - cam). fwd > 0, |(p - cam) - t * fwd| <= AimCm / 100 and |p - eye| <= ReachCm / 100; p = SnToSe(posSn + velSn * min(age, LeadMaxMs)), age = since the C9 read; eye = the character's head; empty hands, on foot (no seat), alive, no SE use object in front (the character detector's UseObject null), C9 fresh (its seq changed within StaleMs), the chart not in transition
format c5.s17 SeState 0xcc-0xeb written by SE every tick while S17 runs (version 0 = absent): the last catch request; flags Active while it waits for its ack (cleared on the ack or after AckTimeoutMs); reqSeq starts at a per-session value and grows by one per request
format c7.s17 SnStatus 0xc8-0xd7 written by Subnautica every frame while S17 runs (version 0 = absent): the answer to the last request handled, ackSeq = its reqSeq, result = Result
format c9.s17 Subnautica writes C9 every PeriodMs: the RowsMax nearest creatures of a kind row within RadiusCm of the camera, dead ones too (none in a containment: WaterParkCreature), nearest first; the overlap's collider buffer grows (up to 4096) when a scan fills it; instanceId = GameObject.GetInstanceID as u32, techType, posSn, velSn (its Rigidbody), aggression 0, flags Catchable, targetEntityId 0; count 0 when none
format catch.se F (SE's USE control, newly pressed) on a target sends one request when none waits (Busy) and the fish was not requested within ForgetMs (Again; a Moved answer or a timeout forgets it); the ack with ackSeq = reqSeq within AckTimeoutMs of the request's first write into C5 (UnpublishedMs from F while it was never written) gives the item (Caught) or refuses (NotCaught); none: Timeout, no item then; a later Caught ack (Late) still gives the item
format catch.sn a request (version 1, Active, reqSeq not the last handled) is handled once: the creature of instanceId among the last C9 rows, Gone when its object no longer exists, Kind when it is not a kind row, Moved when it lies over SlackCm from posSn, else Caught: its GameObject destroyed (no death, no loot) and acked; Gone too when the request's worldEpoch is not its C5 record's; a WaterParkCreature is Kind; a fish of an S8a spawn point leaves a tombstone there (no respawn until the camera is beyond the spawns' DespawnM); Off while Subnautica has no world loaded
format item.give one raw item of the kind into the character's inventory (MyInventory.AddItems), dropped at its feet (MyFloatingObjects.Spawn) when it does not fit; NoItem when the world mod's definition is missing
format items ConsumableItem DE_Fish_<Kind>_Raw and DE_Fish_<Kind>_Cooked: DisplayName <name> (raw) / <name> (cooked), Mass MassG / 1000 kg, Volume VolumeMl / 1000 L, Size SizeMm / 1000 m (SE's meal packs), one Food stat RawMicro or CookedMicro / 1e6 per TimeS (raw = MealPack_KelpCrisp, SE's smallest meal pack), no Health stat; blueprint DE_CookFish_<Kind>: 1 raw -> 1 cooked in CookS s, class FoodProcessorRations
```
Pinned by `S17ConventionsTests` (text byte for byte, hash, both blocks inside their pads after the earlier ones, layout 1.0
and the S11/S14/S15/S16 pins unchanged, de.s17 dev-only) and by the Python twin (`s17_ref.py --selftest`). Changing a line
bumps `ext s17 fish <n>` and updates both pins.

#### S10 presets

The performance presets of both plugins (Wire `S10Presets`; ROADMAP §14 "### S10": low/mid/high, the auto-pick, the 30 fps
cap on shared GPUs; the life factor is R7's spawn volume for the S8a spawns). No wire layout change: the preset reaches each
plugin as `DEEPENGINEERS_S10_PRESET` (set by `launcher.py run` from `%LOCALAPPDATA%\DeepEngineers\s10-preset.txt`, written by
the auto-pick at the first play start or by `launcher.py s10 --preset`); every knob's own variable still wins; no preset = mid
= every switch's built-in default. Canonical text (FNV-1a 64 `0x42e90bc51d397ecf`):

```
ext s10 presets 1
preset low fpsCap=30 lightsPerGrid=6 previewCap=500 colliderNearM=100 lod=on lodM=100 life=0.5
preset mid fpsCap=0 lightsPerGrid=12 previewCap=1000 colliderNearM=150 lod=off lodM=150 life=1.0
preset high fpsCap=0 lightsPerGrid=24 previewCap=3000 colliderNearM=250 lod=off lodM=150 life=1.5
auto sharedGpu or ramGB<20 -> low; ramGB>=30 and dedicatedGpu -> high; else mid
```
Pinned by `S10PerfRulesTests.PresetsArePinnedAndPicked` (the hash, the auto-pick, the cap) and by the Python twin
(`tests/s10/analyze_s10.py --selftest`). Changing a line bumps `ext s10 presets <n>` and updates both pins.

---

## 6. Protocol identity

| Item | Value |
|---|---|
| Section name | `Local\DeepEngineers_v1` (the `_v1` follows `layoutMajor`) |
| Magic | the four bytes `D E E P` (u32 0x50454544 read little-endian), written **last** with release semantics by the creator; cleared to 0 first when an existing section is re-initialised |
| Versions | `layoutMajor.layoutMinor` = 1.0; `wireVersion` = 1 (`WIRE_VERSION` in `wire_ref.py`); `siteVersion` = 1 (`SITE_VERSION` in `site_v1.py`); `damageScaleVersion` = 1 |
| Byte order | little-endian only (x64 on both sides); `byteOrderMark` 0x01020304 must read back as written; IEEE-754 floats; quaternions `(x, y, z, w)`; SE matrices as 12 floats in row order Right, Up, Backward, Translation |
| Layout hash | FNV-1a 64 (offset basis 0xcbf29ce484222325, prime 0x100000001b3) over a canonical ASCII text in six blocks: one line per region `region <id> <kind> 0x<offset> 0x<size> <recordSize> <version>` in id order; one line per ring-control entry `ring <index> <region id> 0x<head offset> 0x<tail offset>` in index order; one line per fixed record or record array inside a region `table <region id> <name> 0x<offset> <count> <stride> <record>` (offset from the region start) by region id then offset; one line per byte-ring message type `message <region id> <type> <record>` by region id then type; one line per enumeration value, flag value and message code of §5.4 `const <group> <name> <value>` (decimal) by group, then value, then name; then one line per field `field <record> <name> 0x<offset> <size> <type>` sorted by record name then offset; lower-case hex, single spaces, `\n` line ends, no trailing spaces; names are the `Records.cs` struct, class and member names. Generated from `Layout.cs`; the independent Python oracle builds the same text from its own table. The value for layout 1.0 is `0x4f3a35f6fc1eaf31` (pinned by the S0.N1 C# layout test; canonical text: `tests/DeepEngineers.Wire.Tests/Fixtures/layout-1.0.txt`, 506 lines, reproduced byte for byte by the Python oracle). The `ring`, `table`, `message` and `const` blocks were added during S0 and replace the earlier 321-line value `0x5e245e58a5cc1fe8`, which no build uses. Conventions of the text (S0, D2; reproduced by the Python oracle): ids, kinds, record sizes, counts, strides, field sizes and `const` values in decimal; offsets and region sizes as `0x` + lower-case hex without leading zeros (`0x0`); a `\n` after every line, the last included; "sorted" means ordinal (byte) order of the ASCII names; record names are the C# struct names (`SectionHeader`, `SeEntity`, `ModelChunkHeader`…); field types `u8 u16 u32 u64 i16 i32 i64 f32 f64`, an array as `<type>[n]` (`f32[3]`); every pad is a field named `pad` typed `u32`, `u16` or `u8[n]`; `ω` is spelled `omega`; a C10 path is two fields, `pathLength` (u16) and `path` (`u8[230]` in the request, `u8[222]` in the result) |
| Extensions pinned outside the layout hash | the C1 swim extension (`ext C1 swim 1 0x94 12`, FNV-1a 64 `0x832ba93ca25bc307`, S5a, §5.4) and the S5 mirror conventions (`ext s5 mirror 1`, `0x31dfd6f9befdef5d`, with the canonical skeleton `canon1`, `0x995765282e7d4f1d`, §5.4); since then the S6 combat conventions (`ext s6 combat 1`, `0xb6ee880712f8253b`, §5.4) and the S7a conventions (`ext s7a picture 3`, `0x4257c11c17ff772a`, §5.4: the C1 locomotion extension at 0xA0–0xAF, the C6 sky pole, the ghost marker and the picture flag) and the S7 conventions (`ext s7 look 1`, `0x1f9b9b4a876a97f9`, §5.4: the C1 interior extension at 0xB0–0xBF, the C2 light rows and effect-event window, RoomSet flags, C12 version and flags, `ExtraBones`) and the S8a conventions (`ext s8a terrain 6`, `0x3188251ab11e2fec`, §5.4: the C2 terrain notices at 0x50000, the C7 acknowledgement block at 0x8C, the octree, store, overlay, window, chart and sculpt rules) and the S7p effect conventions (`ext s7p fx 1`, `0xc83edaca61ce1545`, §5.4 "S7p effects-under", S7p: the S7 event row's pad at 0x3E (`extra`: a thruster's flame, a shot's hit distance) and the drills' `subtype` bits 8–11; 0 = absent, the S7 behaviour; Wire-pinned only, no Python twin) and the S16 conventions (`ext s16 depth 10`, `0x2e5e460ba5724d39`, §5.4: the C5 `SeState` extension at 0x80–0xA9 and the C7 `SnStatus` HUD block at 0xBC–0xBF) and the S13 conventions (`ext s13 char 1`, `0xbe077eea1976d36d`, §5.4: the C1 look-frame velocity extension at 0xC0–0xC7, the light fit, the blend roots) and the S11 conventions and the S14 conventions (`ext s14 audio 6`, `0x1261b20244b0b097`, §5.4: the C7 `SnStatus` engine block at 0xC4–0xC7 and the audio rules both plugins and the twin share) and the S15 conventions (`ext s15 voice 5`, `0xd5d5c7fdeeece0ec`, §5.4: the C5 `SeState` S15 block at 0xAC–0xCB, version 2, and C6's region space past `SeTime`: the sky frame at +0x100, the body list at +0x200 and the events block (the sonar) at +0x700, each with its own seqlock) and the S12 conventions (`ext s12 interiors 4`, `0x9aa269decc02e106`, §5.4: C6's region space at +0x800, the flood and vent rows with their own seqlock) and the S17 conventions (`ext s17 fish 2`, `0x120ee00b7501f416`, §5.4: C9 written with the fish list, flags bit 8 `Catchable`, the C5 `SeState` catch request at 0xCC–0xEB and the C7 `SnStatus` answer at 0xC8–0xD7). All live in pad bytes, unpinned region space or unpinned fields of layout 1.0, carry their own version (a byte or a 4-bit nibble; 0 = absent, the earlier behaviour) and are pinned by Wire tests and a Python twin. They are not compared between the peers at run time: both plugins load the same `DeepEngineers.Wire.dll`, whose sha256 the deploy manifest records for each side |
| `siteHash` | §3 (FNV-1a 64 over the canonical JSON of `site_v1.record`), carried in C5 |
| Security | security descriptor `D:P(A;GA;SY)(A;GA;BA)(A;GA;<current user SID>)S:(ML;NW;ME)` on the section (P/Invoke `ConvertStringSecurityDescriptorToSecurityDescriptorW`). Mixed elevation between the two games is not supported (UIPI would block the window calls and `GetAsyncKeyState`); the launcher refuses it |
| Mutexes | `Local\DeepEngineers_v1_sn` (SN plugin, held while in bridge mode) and `Local\DeepEngineers_v1_se` (SE plugin, held while linked). A second instance of either side finds the mutex held, logs `DE.Link REFUSE second instance` and stays dormant. SE takes its mutex only after validation succeeds; `HeldElsewhere` or an error (the name used by another object type, access denied) makes that SE dormant for the rest of its process, with no retry (S0, D33) |
| Bridge token | Subnautica enters bridge mode only with `DEEPENGINEERS_BRIDGE=1` in its environment or the launcher's named **mutex** `Local\DeepEngineers_v1_launcher` present (S0, D46: created by the launcher with `CreateMutexW`; the SN plugin checks it with `OpenMutexW` and also accepts the name existing as another object type). The launcher holds it until it sees the ready phase or its wait ends (`Menu` in S0, `ReadyIdle` from S1); otherwise Subnautica stays dormant |
| Creator | Subnautica (started first), rules below |
| Opener | SE and every test tool open only, never create: `OpenFileMappingW`, failing cleanly when the section is absent (Python tools use ctypes `OpenFileMappingW`, the spy with `FILE_MAP_READ`; never `mmap` tag names, which create). SE retries once per second (every 5 s after the first minute), running in "renderer offline" mode meanwhile. The launcher closes its handle once the ready phase is seen; its preflight requires the section to be absent (`ERROR_FILE_NOT_FOUND`) before Subnautica starts, and lists holder PIDs otherwise (relaunch: §7). S8: the launcher's exit wait reads the header read-only once per 0.25 s poll, opening and closing the section each time, so it never keeps it alive. SE opener rules (S0, D6, D10, D32): `magic` 0 at open means the creator is (re-)initialising, so SE logs `DE.Link WAIT`, not REJECT; one WAIT limiter for every cause (at most one line per 10 s); a REJECT is logged once per distinct field and value, at most once a minute, and the handle is closed after every rejected attempt. Once attached, SE keeps the section, and its handle on the peer process, after PeerExited and waits for a new generation. It detaches on a failed re-validation, on a non-zero wrong `magic`, and when `magic` stays 0 for 10 s while the recorded `snPid` is dead (a creator that died during re-initialisation), so that a restarted Subnautica can take over. Comparing the region table is optional for the opener; steps 1–6 decide |

**Creator rules (Subnautica, in bridge mode, holding its mutex).**
1. `CreateFileMappingW` with the security descriptor. A new section is zero-filled by the OS: write the header and the
 region table, then the magic last. A seqlock record the creator does not write at creation stays at `seq` 0, which
 readers take as "no data" (§5.3): the C# creator leaves C7, C9 and C10 so until the plugin's first write, the
 Python stand-in writes them at once (S0, D45; harmless).
2. If the section already exists (`ERROR_ALREADY_EXISTS`): read the header. If `magic`, `layoutMajor`, `sectionSize`
 or `layoutHash` differ, or any other identity word of validation steps 1–5 (`byteOrderMark`, `headerSize`,
 `wireVersion`, `siteVersion`, `damageScaleVersion`, `qpcFrequency`) or the region table differs from the creator's
 own → **refuse**: log the field and both values, write nothing, stay dormant (a holder of another version keeps
 it; rule 3 rewrites only the SN block, so a creator never inherits another version's words). If the recorded
 `snPid` is a live process (`OpenProcess` + `WaitForSingleObject(h, 0)`) → refuse; an `OpenProcess` failure other
 than "no such process" (access denied, any other error) counts as alive (S0, D7). Process identity: a recorded
 `snPid` equal to the creator's own PID is a dead predecessor whose PID was reused and does not refuse; the opener
 keeps its `SYNCHRONIZE` handle on the recorded `snPid` until it sees a new generation, so while SE holds the
 section the PID of an exited Subnautica cannot be given to another process.
3. Otherwise re-initialise in place: set `snPhase = Booting`, clear `magic` to 0 (release), rewrite the SN header block
 (PID, `snGeneration` = the value found + 1, heartbeat), set the tail of every ring it consumes (C3, C4, C11c) to the
 current head, keep every producer head and every seqlock `seq` it finds (continuing from them), rewrite its own
 seqlock records in place, then store the magic last.

**Validation on both sides** (any failure: the display bridge stays off, both logs name the field and both values, SE
plays normally with the Water Mod view):
1. magic, `byteOrderMark`, `headerSize`, `sectionSize`;
2. `layoutMajor` equal (a different minor is accepted and logged: minors only append fields or flags). As implemented
 for layout 1.0 (S0, D3), step 3 still requires an equal hash and the hash covers every field, so a peer of another
 minor fails step 3 anyway: until open item O16 is decided (before any layout 1.1), a minor bump is in practice as
 incompatible as a major one;
3. `layoutHash` equal to the compiled one, and `Marshal.SizeOf` and the managed size of every record struct equal to
 the layout constants (layout test on both sides at load). The managed size is `Layout.ManagedSize`, the stride of a
 pinned two-element array, because `Unsafe.SizeOf` needs a package neither plugin ships; the unit tests assert
 `sizeof` (S0, D8);
4. `wireVersion`, `siteVersion`, `damageScaleVersion` equal;
5. `qpcFrequency` equal to the local `QueryPerformanceFrequency`;
6. WM-1 in-game self-check PASS (§2.9).

When: the creator validates its own section (steps 1–5 and the region table) after rule 1 or 3 and, on a failure,
withdraws (`snPhase = None`, view closed, mutex released, dormant); a step-6 failure only keeps the display bridge off
(C7 `wireSelfTestPass` = 0). The opener validates at open and again whenever it sees a new Subnautica PID or
generation, whether or not it sampled `magic` at 0 during the re-initialisation; a failure detaches it.

**Hello.** In world, SE publishes in C5 `{Rs, siteHash, siteVersion, helloEpoch = worldEpoch}`; Subnautica clears its
state for that epoch and answers `helloAckEpoch` in C7 together with `hostKind` and its capability bits. SE streams
C3/C4 only after the ack. Order and acceptance (S0, D22): on attach SE writes its heartbeat, then its PID and
generation (one u64, "Restart detection" below), then C5. Subnautica ignores the C5 contents present when it first
sees a new SE generation and reads a hello only after C5's `seq` has changed since, so a C5 left by an earlier SE
generation is never taken for a new hello (this also covers a writer that publishes C5 first, as the Python SE
stand-in does). Subnautica acknowledges only when `siteVersion` and `siteHash` equal its compiled values; Rs is logged,
not checked. In S0 SE publishes the compiled `SiteV1.RsExpected` (D31); the Water Mod check of Rs arrives with S1 (§3).
From S1 the hello also waits for a bound site (`DE.Site OK`) and carries the Rs validated
through the Water Mod API (60000.00390625, bits 0x476a6001); a world without `DE_4546B` or a refused site publishes no
hello (S1 D61).
SE publishes a hello only in world, with its activation gate open (§1.2), its version pin accepted and its start-up
self-checks (WM-1 subset, Harmony targets) passed; otherwise `helloEpoch` stays 0.

**Link epoch (recovery without a new generation).** When the Subnautica heartbeat becomes fresh again after
PeerStalled, with the same PID and generation, SE bumps `linkEpoch` in C5. Both sides re-synchronise the rings they
consume (§5.2). Subnautica re-validates and answers `linkAckEpoch` in C7 with its counters (`mirroredEntityCount`,
`pendingParts`). SE resends everything (`ClearAll(worldEpoch, linkEpoch)`, then grids, parts, skins, rooms, models and
materials) if the stall lasted ≥ 10 s or the counters disagree with what it sent; otherwise it continues. `snReady`
(including `streamingSettled`) gates the next dive. An attaching SE continues `linkEpoch` from the value it finds in C5
and never resets it (S0, D36). A resend is complete when the consumer's tails have passed the heads at which the
resend ended and a C7 newer than the resend has been read (the S0 stand-ins' rule, D41; the real SE uses it from S1).

**Restart detection.**
- Each side writes its PID and bumps its generation on every attach; the peer treats a changed PID or generation as a
 new peer and re-synchronises its consumer tails (§5.2). The heartbeat is written first, then PID and generation
 together as one aligned u64 store (C0 0x040 for Subnautica, 0x080 for SE; low word PID, high word generation), and
 the peer reads the pair as one u64, so it never sees a new PID with an old generation.
- New Subnautica generation: SE sends `ClearAll(worldEpoch)` in C4, then every mirrored grid, part, skin and room
 again, and every model and material again (its "sent" set is reset), within the normal budgets, nearest first.
- New SE generation: Subnautica destroys all mirrors, proxies, pending parts and its statics-sent set, re-syncs every
 ring it consumes (C3, C4 and C11c, §5.2), re-reports the statics around the camera, and waits for a new hello.
- `worldEpoch` change: each side drops every record of the old epoch and waits for the hello of the new one. SE bumps
 `worldEpoch` only for a world load (a `MyGuiScreenLoading.RunLoad` whose target is the gameplay screen), never for a
 menu load (S0, D36).
- Damage dedup state is per Subnautica generation (§4.1); records are validated before use whatever their origin.

---

## 7. Lifecycle

**Launcher (one table for every document).**

| Item | Rule |
|---|---|
| Subnautica launch | `Subnautica.exe -monitor 1` started directly by the launcher (it owns the PID) with `DEEPENGINEERS_BRIDGE=1` in the environment, plus the named mutex `Local\DeepEngineers_v1_launcher` held until the ready phase is seen or the wait ends. The pack's `steam_appid.txt` (264710) allows the direct launch. Fallback only: `steam.exe -applaunch 264710`, PID found by exact path. The SN plugin runs its whole bridge start on its first player-loop `Update`, never in `Awake`: an object created in `Awake` (before the first scene load) was destroyed by the StartScreen load without one `Update`, although `DontDestroyOnLoad` had been called |
| SE launch | `Legacy.exe -noUpdate -noSplash -noPrompt -bin64 "<SE>\Bin64"` (optionally `-continue`) as the launcher's child, then `AllowSetForegroundWindow(sePid)` |
| Ready wait | S0: `snPhase = Menu` (no host save yet). From S1: `snPhase = ReadyIdle`, timeout 180 s. Measured: `ReadyIdle` 18.4–20 s after the launcher starts Subnautica (16.8 s for Subnautica alone; host world load 12.5 s), so the 180 s wait stays. The launcher also checks that `snPid` is the process it started |
| On timeout | do not start SE: report, leave Subnautica running, never kill (close it normally or with `launcher.py stop`). Changed after the S0 real run: starting SE anyway hid a destroyed bridge object for 180 s |
| Relaunch | none automatic in v1; the player restarts the renderer from the launcher, which starts it only after the old PID and its crash handler are gone and a 20 s cool-down. Its preflight differs from a first start's on one item: SE keeps the section after PeerExited, so the section may be present when its recorded `snPid` is dead and its other holder is the running SE; the new Subnautica then re-initialises it by creator rule 3 (§6). **Built: `launcher.py restart-renderer`**: refuses without a running SE; waits while the old PID or any `UnityCrashHandler64` lives, then 20 s from the old renderer's exit (the exit wait's record, else its last heartbeat in the section, else from the moment it was seen gone); refuses a section whose recorded `snPid` is alive or that a Subnautica process holds; waits up to 30 s for SE's window to be in the foreground (the new window is then created by a background process: HYPOTHESIS that SE keeps the foreground, S8.T-L1d); starts Subnautica as the launch row says, the ready wait checks `snPid`; SE is never touched; then the exit wait on both |
| Preflight | no game process or stale `UnityCrashHandler64` (crash handlers closed by exact PID); a `SpaceEngineers. no zombie signature; ≥ 12 GB RAM; a backup set (`se-appdata`, `subnautica-saves`, `subnautica-locallow`) newer than the last DE session; section absent; Subnautica registry `Screenmanager` keys equal to the launcher's backup |
| Elevation | both games at the same integrity level, else refuse |
| Exit wait | `run` waits on both process handles (0.25 s polls, sleeping on SE's handle so its exit is seen at once) and samples the header read-only at every poll (`seExiting`, both heartbeats and phases). SE gone and Subnautica still running 15 s later: one `CloseMainWindow`; 30 s later the launcher **offers** a kill by exact PID (in an interactive console the player presses K and types the PID; otherwise `launcher.py kill --pid N`, which also asks for the typed PID and accepts only game and crash-handler processes) and **never kills by itself**; the same offer when a heartbeat is 30 s old (150 s in a loading phase) while its process lives, withdrawn when it beats again. Subnautica gone while SE runs: no relaunch, the launcher prints the `restart-renderer` hint. The session is recorded clean when SE wrote `seExiting` before its process ended, or never linked |
| Settings | §1.1: `settings-backup.json` (SpaceEngineers.cfg `GameVolume`, `Music_Volume`, `VoiceChatVolume`; the Water Mod `<Volume>`) written at the first `run`, never overwritten; `session-settings.json` snapshots them before every session and records the end. At the next clean start, after an unclean end (or a pending `restore.json`), only a value the bridge can leave behind is written back (Water Mod `Volume` 0; `GameVolume` 0.7 × and `Music_Volume` 0 × the pre-session value), the original file copied to `settings-restore\` first, and only with no game running; any other difference is reported, never changed. `restore-settings [--yes]` restores everything from the backups (and the registry keys from theirs) |
| Updates | both games set to "only update when I launch it"; the launcher never uses Steam to start SE |
| Install | `Install.cmd` (release folder, `launcher/de_install.py`; a second run repairs): stops before writing anything when a game runs, a game is missing, SE's Steam build is not 24675677, Subnautica's is not 20241558 with changeset 83031, or Subnautica's CompiledOctreesCache differs from the terrain gate's pinned listing; FAIL (the rest still installed) for a missing Workshop item, a Water Mod other than 3.23, no Freedom Subnautica save, a download that fails or whose SHA-256 differs; WARN for a game not set to "only update when I launch it". Pulsar 2.4.3 and Tobey's BepInEx pack 5.4.23-pack.3.1.1 are fetched at install time from their pinned GitHub URLs and SHA-256 (`de_pins.py`), never shipped. Every file, folder and profile entry it creates or replaces is recorded with its SHA-256 (and the backup of a replaced file) in `%LOCALAPPDATA%\DeepEngineers\install\install-manifest.json`; `Uninstall.cmd` undoes them in reverse order. Integration: WARN when the release folder's `release.json` flavour is not `release` (only the `DEEPENG_RELEASE` build passes `check_release.py`); a dry run plans the plugins on a PC without BepInEx yet |
| Release paths | a release folder's `launcher.py` never uses this PC's paths: games from the installer's `paths.json` or Steam; Pulsar `%LOCALAPPDATA%\DeepEngineers\Pulsar` (or the one the installer recorded); backups `%LOCALAPPDATA%\DeepEngineers\backups` made by `launcher.py backup` (Play.cmd); it sets `DEEPENG_SUBNAUTICA` (the SE terrain gate's folder) and `DEEPENGINEERS_SN_HOST_SESSION`/`_SLOT` (the installer's Freedom save) in the games' environment unless already set |

| Event | SE side | Subnautica side | Player sees |
|---|---|---|---|
| Start | as the launcher table; the SE plugin publishes `seHwnd`, takes the foreground itself when the link comes up (normally at its main menu; S2), then places Subnautica directly under SE | bridge token checked; mutex; section created (creator rules); Freedom host save loaded; Idle (frozen, cameras off, (0, 10) fps, muted; from S2); window restyled hidden→shown, lowered, `AllowSetForegroundWindow(ASFW_ANY)` (S2); `ReadyIdle`. In S1 (no Idle recipe yet) Subnautica stayed in `ReadyIdle` with its camera driven, cameras on and time running (S1 D73) | Subnautica's loading, then SE's menu |
| SE without Subnautica (not started, failed, refused, dormant) | "renderer offline" mode: tiles unaffected (they do not depend on Subnautica), view state never Underwater, Water Mod view; reconnects when a valid section appears | n/a | plain SE with the Water Mod |
| Multiplayer session | plugin inert (`DE.Gate REFUSE multiplayer`; what stays active in S0 is listed in §1.2, O17) | n/a | plain SE |
| World load | prefix on `MyGuiScreenLoading.RunLoad` sets `sePhase = LoadingWorld` before any blocking work (the `DE.Phase Menu -> LoadingWorld` log line comes only when `RunLoad` returns, §5.4); heartbeat also from `MySession.LoadingStep`/`LoadingLocal*`; `worldEpoch`++; 50271 handler registered in `LoadData`; C after `MyEntities.Load`; 34 tiles in `BeforeStart`; site validation at the first `UpdateBeforeSimulation` (≤ 600 ticks); hello. A load longer than 120 s only stalls the link. Measured (O11): 22.9–29.6 s from `RunLoad` to the first update for the lab world, 23. clears per epoch, acks the hello | SE loading screen |
| Tiles cannot be created in a DE world (terrain gate REFUSE, §1.2 conditions 3–7) | **fail-safe, never fail-open**, for the rest of the session (it is never lifted and no tile is created later; a reload evaluates again): `DE.Terrain FAILSAFE ON <reason>` once, `seFlags.terrainUp` 0, `seFlags.failSafeActive` 1. **Saving blocked (S4):** a prefix on `MySession.Save(out MySessionSnapshot, string, Action<SaveProgress>)` returns false with `__result = false` and a null snapshot (manual saves, autosave, "save as" and "save and exit" all pass through it; VERIFIED), shows the HUD warning "DeepEngineers terrain missing: saving disabled" and logs `DE.Terrain SaveBlocked n=<count>`. **Footprint:** the grid's position mapped through F lies in `max(\|x\|, \|z\|) ≤ 2,100 m` and `y < 0` (SN). **Grids held (S8, S8.N8):** every simulation tick each non-static grid in the footprint gets zero linear and angular velocity and zero gravity (HYPOTHESIS mechanism: `MyGridPhysics.Gravity` setter, `LinearVelocity`, `AngularVelocity`) and is flagged; flagged grids take no abyss crush (§4.1) for the session. Characters are not held (they can swim or fly). Until S8 lands, a grid in the footprint may sink during that session, but no save records it | n/a | the HUD warning; the world on disk is not changed |
| Normal play | view state machine (ARCHITECTURE §4) | follows C5: Idle / Track / Full, with `snPhase` ReadyIdle / WarmingUp / Active (§5.4 "C5/C7 semantics for S2") | |
| Pause (ESC, Steam overlay, medical screen) | `sePaused`; time of day not advanced | SE menu (over the frozen Subnautica picture when underwater) |
| A GUI screen other than gameplay, not paused (inventory, terminal, chat) | `guiScreenOpen`; SE writes no audio volume | not frozen (SE's simulation runs); mix per state | the screen (over the live Subnautica picture when underwater) |
| Alt+Tab away | `seForeground = false`; SE mutes itself, the plugin writes nothing | muted; (0, 30) fps if Underwater | both silent |
| Alt+Tab back | SE's restore, then the plugin's mix in the `UpdateSound` postfix; z-order re-asserted with `AllowSetForegroundWindow(snPid)` | unmuted per state | |
| SE save / autosave | prefix on `MySession. a finalizer clears both | waits | normal SE save |
| Teleport or jump within the site | C1 `teleported`; beyond 150 m (a relocation, `ViewStateDriver.ResettleJumpM`) Underwater exits until `snReady` again (with `streamingSettled`); streams | Water Mod view for the streaming time, then Subnautica |
| Leaving the site (jump drive, other planet, beyond 7,900 m) | view state Space | Idle; the camera and the ghost Player hold their last driven pose outside the dive zone | SE |
| Character death | C1 `characterDead`; SE death and respawn screens are SE GUI | hides the avatar, disables the character section; camera keeps following SE | SE death screen (over Subnautica if underwater) |
| Respawn | new character id → `CharacterAdd` | new mirror | |
| Subnautica stalls (heartbeat ≥ 3 s, 120 s while loading; process alive) | PeerStalled within one frame underwater: scene drawn, margins off, audio restored, HUD notice (after 10 s elsewhere); recovery with `linkEpoch`. S8: the scene is drawn and the crossfade snaps on the frame the loss is found (the PrepareForDraw step re-reads the link), the mix is restored without the 300 ms ramp, the notice is "DeepEngineers: underwater renderer offline (Subnautica is not responding) …" (exited: "… (Subnautica closed) … restart the renderer from the launcher (restart-renderer)"), at once with Subnautica's picture on screen, after 10 s elsewhere, only in a DE world, once per loss; rows `s8.peer`, `s8.notice`; the launcher offers a kill of Subnautica after 30 s without a heartbeat and never performs it | n/a | SE's Water Mod view underwater; nothing elsewhere |
| Subnautica exits, crashes or quits | PeerExited (process exit polled every tick while margins are on, otherwise once per second; lost window, `Quitting`, changed PID/generation) | sets `Quitting` and waits ≤ 100 ms for SE's acknowledgement when it quits on its own: spread over frames, never blocking, except inside `OnApplicationQuit`, where it is a bounded ≤ 100 ms poll on the main thread, the only blocking wait on a game thread (S0, D20) | SE's Water Mod view; simulation never stopped |
| Subnautica restarted by the player (launcher) | new generation: tails re-synchronised, everything resent | new generation, Idle → Track → Full | Subnautica returns at the next dive |
| SE exits normally | prefix on `MySandboxGame.ExitThreadSafe` (priority First) writes `seExiting` before Pulsar's own prefix kills the process; no other exit cleanup (VERIFIED real run, S0.N9: SE closed in 1 s) | mutes, freezes, quits without saving within 10 s, through the game's own quit path (S0: `uGUI_MainMenu.OnButtonQuit` at the menu). An SE block already dead when Subnautica first sees it is Stale and never makes Subnautica quit (S0, D19). S8: minimised without activation too; in game `IngameMenu.QuitGameAsync(true)` waits for `LargeWorldStreamer.IsWorldSettled` without a timeout, so `Application.Quit` follows 5 s after the issue when SE exited (10 s for other quits); the SE prefix writes `seExiting` before its log flush; rows `s8.peer`, `s8.quit` | desktop |
| SE crashes | n/a (Pulsar starts `SpaceEngineers.exe -report`, a crash form) | process exit seen by the once-per-second poll (≤ 1 s + one frame, D38): mutes, freezes, minimises without activation, quits within 10 s | SE's crash form |
| SE hangs | n/a | heartbeat 3 s (120 s in SE loading phases): mutes and freezes; **never quits on heartbeat loss**; resumes if SE does (S8: Idle too; the launcher offers a kill of SE after 30 s without a heartbeat, 150 s in a loading phase, and never performs it) | frozen SE (underwater: the frozen Subnautica picture under SE's frozen HUD) |
| GPU driver reset | SE's own driver-crash message | likely dies (HYPOTHESIS, never induced) | crash; no relaunch in the same boot while a game process lingers |
| Uninstall | `docs/install/UNINSTALL.md`, generated from a manifest of everything created or changed: `<Pulsar>\Legacy\Local\DeepEngineers.SE\` and the `Current.xml` `<Local>` entry; `<Subnautica>\BepInEx\plugins\DeepEngineers\` (`DeepEngineers.SN.dll`, `DeepEngineers.Wire.dll`, pdb); loaders per `docs/install/subnautica-bepinex-files.txt`; settings back to the backup values (`WaterClientSettings.xml`, `SpaceEngineers.cfg` audio keys, the Subnautica registry keys); keep the world mod while a world contains `DE_4546B` (HYPOTHESIS, S4.G2-E5); `%LOCALAPPDATA%\DeepEngineers\`. S9 integration: `Uninstall.cmd` keeps what `%LOCALAPPDATA%\DeepEngineers\` held before the first install (the manifest's `localBefore`, e.g. a development setup's logs, census and `registry-backup.json`) and its `backups\` unless `-RemoveBackups` | | the folders listed |
| Game or loader update | display version pins fail → display bridge refuses with a log line; the terrain keeps its own gate (§1.2) and, if it fails, the fail-safe protects the world. **S9:** the `DE.Version REFUSE` line carries the plain words of `Wire.VersionPinRules` (build needed, inactive for the underwater view, "update only when launched") and one red HUD notification is shown at the first in-world update (`IMyUtilities.ShowNotification`, 20 s) | Subnautica's pin (§0) fails in a bridge launch → dormant (no section; §1.2), one `ErrorMessage.AddMessage` notice on its own visible screen and one `DE.Version REFUSE sn` line; a plain launch only logs `DE.Version MISMATCH sn` | SE with the Water Mod view, builds intact; the notice names the build needed |

Process rules: nobody force-kills a game during D3D initialisation
confirmation; normal closing by `CloseMainWindow` and waiting on the PID; no automatic Subnautica relaunch in v1.

---

## 8. Not covered (v1)

| Not covered | What the player sees |
|---|---|
| Multiplayer, other players' characters | single player only; the plugin is inert in multiplayer |
| SE 3D-only feedback beyond grids and the character (highlights, LCD text and offscreen textures, first-person hands and tools beyond the skinned body; the placement ghost since S7a; welding, grinding and drilling since S7 as Subnautica's own effects from event records) | invisible in Subnautica's picture; HUD sprites remain |
| Weather | none on `DE_4546B` |
| FAR zone (beyond 7,800/7,900 m) and other planets' oceans | SE's Water Mod view (no "void treadmill"); **S8a:** with `TreadmillOn` the treadmill charts keep `DE_4546B`'s camera out of FAR (§3.3 "Chart and treadmill"); other planets unchanged |
| Hull pressure gameplay | no crush except the abyss rule and the floor guard |
| Subnautica 2 | not supported |
| Terrain edits on the crater | blocked |
| Creature rams, reaper grabs, `AttackCyclops`, Subnautica creatures damaged by SE | creatures pushed out of hulls; bites, shocks and blasts only |
| Subnautica gameplay systems (PDA, crafting, vehicles, bases, survival stats) | unused; ghost frozen, its UI hidden |
| Subnautica's above-water objects in SE's picture, and as SE collision above the sea | not drawn by SE; primitives entirely above SN y = 0 are not sent to SE |
| Subnautica story timeline (Aurora explosion, Sunbeam, precursor gun) | runs on its own clock, restarted each session (not saved) |
| Several sites, several dive planets | one site, one planet |
| Lights and sounds of Subnautica creatures in SE above water | sounds: n/a (Subnautica is muted above water in both picture policies: the mix still follows the Underwater state); lights: S2's `Waterline` policy hides Subnautica above water, while from S7a (`Site` policy) Subnautica's own picture shows its creatures, birds and lights above water inside the site, so nothing of them needs to reach SE |
| SE's above-water effects in Subnautica's picture (S7a `Site` policy) | S7 draws SE's lights everywhere and, for event records above water, a light flash or glow only (no flame, smoke or spark look and no Subnautica sound in air; the splash is drawn at the surface)
| Creature positions used by SE gameplay (sonar, HUD markers) | C9 exists; only logs and tests read it in v1 |
5) is lifted once to the lifepod point SN (0, +0.6, 0), head out, velocity 0 (`RespawnRules.Fire`, `RespawnWatch`, SwimPatches per physics step; log `DE.Respawn LIFEPOD`). SE's medical rooms and survival kits are untouched (their characters appear out of the water or in a room); the saved character of a world load is never touched. Cause: SE's drop pod lands on the planet's solid surface, 1.5 km under the water sphere (S6-3) |
| A DE world opened without the plugin, or with the activation gate refused (multiplayer, more than one `DE_4546B`; §1.2: the terrain gate needs the global gate open, and a closed global gate leaves every patch body inert, the fail-safe included) | nothing can protect it (grids on the crater sink); README warning |

---

## 9. Open items

|---|---|---|
| O1 | **Closed:** `wire_ref.py` has the site selector (default `v1`), pose and camera samples over `y ∈ [−3,040, +350]`, rotated-box and `static` record kinds; sha256 values in §2.9; `siteHash` `0x53ab8e77d68103ab` unchanged | closed |
| O2 | E1: flat faces where Subnautica's data stops (plug) | first Subnautica real run (S4.G2-E1) |
| O3 | A-T1: 1024³ vs 2048³ tiles | SE real run |
| O4 | **Closed:** region map with ids, kinds, record sizes and versions (§5.2), record layouts (§5.4) and the layout-1.0 hash `0x4f3a35f6fc1eaf31` with its text conventions (§6) are pinned by the S0 layout tests (C# net48 and net472, and the hand-written Python oracle, byte-identical, 506 lines); the carried-in prototypes were migrated | closed |
| O5 | Respawn on `DE_4546B` (Water Mod respawn pods or a sea-surface spawn) | gameplay decision |
| O6 | **Closed (VERIFIED, mod source):** the Water Mod volume is a client setting in `WaterClientSettings.xml` (SE global storage, shared by all worlds), written on every SE save and by `/wvolume`. The plugin never writes it (§1.1) | closed |
| O7 | Damage scale, `k_char` absolute vs scaled with `MaxHealthBuff`, abyss rates and floor-guard depth are gameplay choices
| O8 | **Closed (VERIFIED):** `MyMaxHealthBuff` sets `Health.SetMaxValue(StatValuePerLevel[Level])`, levels 100…150 in `EntityComponents.sbc`; `k_char` stays 1.0 per SN_HP; the worked percentages assume level 0 | closed |
| O9 | S2.N8: whether a fully covered or alpha-1-composed Subnautica keeps rendering; which alternative (margins in Prime, 1 px alpha-0 region, `onPostRender` stamp, `areaBuilt`-only entry). Input from S1 (real run): `areaBuilt` means the ±16 m near field (±56 m was never reported built); the `camBuiltHalf` ladder (56/32/16/8/4 m) in `DE.S1 sec` and the probe lines measures what is built for the `areaBuilt`-only alternative.6 fps (worst 1 s window 158.6) with the DWM margins on, 164.7 fps with them off, heartbeat gap ≤ 110 ms, presentedSeTick age ≤ 4 ticks on 100 % of the samples; the Day-0 covered stall does not reproduce at full screen with the S2 recipes; design F continues, margins in Prime as designed, no alternative needed | S2 first gate (done) |
| O10 | Water Mod emitter mute by a Harmony postfix on the mod's in-memory script assembly (else the in-memory `Settings.Volume` fallback) | S2 |
launcher ready wait: 180 s stays); SE `runLoadToFirstUpdateMs` DE Lab 22.9–29.6 s (8 loads; `runLoadMs` 18.5–24.4 s (`runLoadMs` 16.1 s). The 120 s loading heartbeat covers both with margin | S1 (done) |
| O12 | Exact `IMyStorageDataProvider` request-flag handling and the half-voxel convention | S4.A-T0b, S4.N3 |
| O13 | Publishing the decompilation-derived notes (EULA 2.2.4.2) and the combination question (EULA 2.2.
| O14 | Environment `SunAzimuth`/`SunElevation` values that give base direction (0, 0, 1), read back at bind time | S7 |
| O15 | Amplitude of Subnautica's displaced water surface at the camera vs the 0.20 m entry threshold. **Still open after the S1 real runs:** `Ocean.GetOceanLevel = 0` holds, but the displacement sampler read ±20 m: its texel-mapping HYPOTHESIS (S1 D84) does not hold, a tooling bug, not a measurement. Until the fixed sampler measures it, the 0.20 m threshold stays | S1.N10 re-run with the fixed sampler (S2) |
| O19 | Wording of C7 `presentedSeTick` (S1 D74): "the newer bracket tick" (implemented, real run) or "the tick of the newest snapshot used" (one tick newer when the newest snapshot is < 0.25 tick old);T-D1 measures the crossing latency |
| O20 | Planet flags the site record leaves at SE defaults (S1 D55): `AllowMeteors`, `AllowSurfaceEconomyStations`, `AllowOrbitalEconomyStations`, `AllowContractSpawns` are true, so SE could place NPC stations on the seabed
| O16 | Minor-version rule (D3): validation step 2 accepts another minor, but step 3's hash covers every field and rejects it. Before any layout 1.1, either step 3 compares the hash of the 1.1 |
| O17 | Activation gate closed in multiplayer: S0 keeps the link open (heartbeat, `sePhase` from the installed phase prefixes, C5 with `gateOpen` 0 and no hello).N6's multiplayer join |

| O21 | S5 capacity of layout 1. `c2Dropped` in `s5.budget` decides whether a layout 1.1 is needed | S5.N6 real run |
| O22 | Character animation while swimming: humanoid (path H, `HumanPoseHandler`) or generic (path G) retarget of Subnautica's swim states onto `canon1`, the T-pose correction of SE's A-pose bind, the 0.25 s source blend, the first-person head anchor (§5.4 "Character", HYPOTHESIS) | S5 real run (`DE.S5 rig`, S5.
| O24 | Bones of other characters (v3 multiplayer): C1 carries the local viewer's character only; other characters' bone records need a per-character channel (a C2 tail table or a layout 1.1 region), keyed by character id | v3 contract |
| O25 | Whether the Water Mod 3.23 treats an airtight room under water as dry for characters (buoyancy, drag, drowning); the S7 swim gate covers only DeepEngineers' swim model | S7.
| O26 | Whether Subnautica's waterscape fog with `_CameraInside` spares a dry interior while the water seen through a window stays fogged | S7.N1 real run (A/B probes) |
| O27 | `.derec` recordings do not carry the S7 C2 extension (lights, events): `PackC2` zeroes the header pad; replays show no S7 effects | S8 or when a replay needs them |
| O23 | `PartAdd.flags` `MetalnessColorable` for unskinned cube parts (SE's grid render flags) and its use in the paint mapping | S3 paint fix pass (S3.C13), with S5 |
