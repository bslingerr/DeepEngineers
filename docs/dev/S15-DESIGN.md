# S15 designer's list: small things that add play or immersion

Written by the S15 pass playing the designer (docs/S15-BRIEF.md part 1). Sources: ROADMAP §14 "### S15", docs/SHOWCASE-POST.md and the session logs under `tests/out/` (S11, S16, S14). The two Reddit threads

reported from them. Already built and dropped from the list: the "hull integrity" voice and the creaks (S16), the depth
meter for the diver and the seated readouts (S16 HUD A/B), the underwater muffle and the thruster sound (S14), the above-water
jets (S14 point 5), bigger leviathans and grabs (S11), the Aurora beacon (S8a). Decided and being built in this slice, so not
ranked: the Cyclops voice on SE grids (welcome aboard, engine powering up, silent running, fire: part A), one planet from
orbit to the seabed (part B), SE's planets in Subnautica's sky (part C).

Every item keeps these rules: the S2 audio rule (one game's audio is live; nothing of Subnautica plays while SE's audio is
live, and SE's own sounds above water stay SE's), the S16 voice rule and mix (`S16VoiceRule`, `S16VoiceMix`: once per event
with a cooldown, 1.6x voice, S16's creaks ducked), the health ownership (SE owns health and block damage; Subnautica only
reports through C8), the pins (a new meaning goes into a new pinned block with its Python twin; no pinned line edited), no
game asset shipped (Subnautica's own sounds and effects are loaded from the player's game at run time).

Cost: S = one build pass pass (a rule, a hook, a row), M = a few hooks on both sides and an oracle scene, L = a new mirror path.

## `ext s15 voice 5`)

His verdict on the voice: "trop et mal fait" (too many lines, badly placed): "Welcome aboard" played on dives when he was
already aboard,
a false "ship will explode 3 2 1" (the abandon-ship sequence of item 1 and, independently, the Aurora's own countdown of
Subnautica's story), and far too many lines overall. His rules, now the code:
- **Only four lines remain**: "Welcome aboard" (the Cyclops's, on boarding a large grid or a base: seat or sealed room), the
 Seamoth's own boarding line on a small grid (`Vehicle.welcomeNotification` of the Seamoth prefab: Subnautica's Seamoth does
 have a welcome voice), "Engine powering up" and "Engine shutting down", plus S16's "Hull integrity critical" (unchanged).
- **The engine lines follow the grid's power, never the thrust**: up when a reactor placed or fuelled (or the Y switch, or any
 source) brings the grid from no power to some while the player is aboard; down when the player switches the power off with Y.
 Once per transition with a 10 s cooldown, no once-per-session rule.
- **No line is ever held**: an event met while SE's audio is live (above water) is skipped, never replayed after the dive.
- **Removed from play** (rules, rows, mutants and dev commands deleted, not defaulted off): part A's fire and silent running,
 item 1 entirely (creature attack, hull breach and restored, engine power down on thrust, abandon ship and its countdown),
 item 4 (the oxygen warnings: Subnautica's own `LowOxygenAlert` is no longer disabled, it is back exactly as the game has it)
 and item 7 (the first-encounter lines). Why: each was a line he did not want in play; together they made the voice noise.
- **Kept as they were**: items 3 (sonar), 5 (the seabase hum and light), 8 (night dimming), 9 (the lens splash), 11 (the fish),
 part C (SE's planets in the sky) and part B (the sea).
- **Added on his audio report of the same play**: Subnautica's music (and a voice line under way) no longer cut above water
 (its options VCAs carry the surface: the effects follow the S2 rule, the music and the voice stay at 0.6 above water), and
 Subnautica's story voice and radio lines off (story goals skipped, the Aurora's countdown and explosion held off: the
 "Not on the list" line below, "SN lore shouldn't be in SE", applied to the game's own story).

## The ranked list

**1. The rest of the Cyclops voice: creature attack, hull breach / restored, engine power down, abandon ship (S).** *Built as
* Inside

leviathan bites or grabs the grid, "Hull breach detected" when an airtight room of the grid you are in loses its seal under
water and "Hull integrity restored" when it is sealed again, "Engine powering down" when you leave the cockpit after a flight,
"Abandon ship" when the grid's integrity falls under a quarter while deep. Hooks: S11's C8 bite and grab records (`S11Records`,
SE's drain) for the attack; the S7 air rule on the aboard grid (`MyGridGasSystem` room `IsAirtight` going false while its
cells are wet) for the breach; the part-A C5 block gets four more counters; Subnautica plays `SubRoot.creatureAttackNotification`,
`hullBreachNotification`, `hullRestoredNotification`, `enginePowerDownNotification`, `abandonShipNotification`
through the S15 player.
("the Reaper grabs the small ship... excellent", S16's voice "fired once and never again" until repeated). Must not break:
S16 keeps "hull integrity critical"; one voice at a time (the S15 queue waits for S16's line).

**2. Silent running as gameplay: lights off and thrusters idle make the leviathans lose you (M).** With every light of the
aboard grid off and the thrusters near idle, the grid's noise drops and Subnautica's leviathans (S11's targets) stop homing on
it from far away; flood lights and full thrust make it loud again. The S15 "silent running" line already tells the player.
Hooks: SE sends a noise level in the part-A block (lights on count, thrust fraction); Subnautica scales the S6/S11 proxy
sections' `EcoTarget` reach the way the Cyclops does (`CyclopsNoiseManager.GetNoisePercent`, feeds the leviathans'
attraction).
19:3x). Must not break: S11's debris filter and grab classes; health stays SE's.

**3. Sonar ping from a ship (M).** A hotkey (or an SE sensor/antenna block's action) pings: Subnautica plays the Cyclops
sonar sound and its screen effect (`SonarScreenFX`, `CyclopsSonarButton`), and SE shows the leviathans within 300 m as HUD
markers for 10 s from the C9 creature table (CONTRACT §5.1 C9: "v1 consumers: logs and tests (sonar/HUD later)"). Evidence:
the scaled leviathans hunt ships now (S11 passes 2-5) and under water there is no way to see them coming; C9 already carries
them. Must not break: C9's seqlock and its 10 Hz writer; SE markers only, no SE entity created.

**4. Oxygen warnings for the diver (S).** *Built as pick 4, REMOVED on: Subnautica's own alert is back as the
game has it.* Swimming on SE's suit oxygen, the diver hears Subnautica's own PDA oxygen
warnings at the same thresholds as Subnautica
driven by SE's oxygen (the S2b/S5a tank mapping, 45 s per tank). Evidence:

Must not break: SE owns the oxygen; only the sound moves; the S2 rule (under water Subnautica's audio is live anyway).

**5. A seabase that feels like one: interior light and hum in pressurised rooms (M).** Inside an airtight room under water
the room is lit like a Subnautica base (a dimmer clone of the base-interior sky, SE's light blocks glowing in the mirror
through emissive materials) and the base's low hum plays. Hooks: S7's interior route (`InteriorView`, `SkyApplier`
custom sky, the light table at the P10 scales), Subnautica's base ambience emitter. Evidence: "is it possible to use SN
interior lighting when building an underwater base?
journaled "next week" with exactly these three steps); the showcase's "sealed under-water house". Must not break: the S7

**6. Creatures heard through the hull, quieter behind heavy armour (M).** Inside a sealed grid, Subnautica's creature
sounds are muffled and attenuated by the hull class S16 already computes (light armour little, heavy armour more, a pressure
hull most), like the Cyclops interior. Hooks: S16's hull class per grid in its C5 block (already sent: `HullCode`, `Tier`);
Subnautica applies one FMOD low-pass snapshot or bus volume to the creature category while the camera is in the room (C5
bit 4 `CameraInAirtightRoom`).
lives inside ships under water; S14 muffles SE's sounds only. Must not break: S14's rule (Subnautica's sounds are never
filtered twice); the S2 rule.

**7. First-encounter lines (S).** *Built as pick 7, REMOVED on.* The first time in a session a leviathan comes
within 150 m, or the player first goes
below 200 m / below the grid's crush depth band, Subnautica's PDA or the Cyclops voice says the matching line once
(creature attack warning for a leviathan, the S16 creak band for the depth). Hooks: S11's C9 creature rows (Subnautica side)
and the S16 C5 values; the S15 voice gate with "once per session". Evidence: ROADMAP S15 examples (first dive below 200 m,
first leviathan); Must not break: one voice at a time; nothing above water.

**8. Night dims the ship lights the Subnautica way (S).** At night under water, Subnautica's world glows (its
bioluminescence) and SE's mirrored lights take a lower scale, so the ship's flood lights stop whiting out the scene. Hooks:
the S7 light table scale (`de.s7 lightScale`) driven by the C6 time of day (uSky `NightTime`).
complaints were tuned by hand at day.

**9. Water on the glass when surfacing (S).** Coming out of the water in a cockpit or as a diver, Subnautica's own wet-lens
drips run down the picture for a second, and the cockpit glass shows it from inside. Hooks: the S2 crossing event (C5
`crossingTick`) and Subnautica's camera water-drip effect on the main camera.
the dive and the
surfacing are the showcase's headline. Must not break: the S7t look match at the band; the picture latch.

**10. Silt kicked up by thrusters near the seabed (M).** A thruster plume within 10 m of the floor raises a sand cloud

he is while he dodges the floor. Hooks: S7p's thruster rows (flame position, direction, strength) and a layer-30 raycast on
the Subnautica side.
all the time).

**11. Small fish react to the ship (M).** Small schooling fish scatter from a ship moving fast and gather in its lights at
night, as they do around Subnautica's own vehicles. Hooks: the S6 proxies as Subnautica obstacles and fear sources
(`FleeOnDamage`-like scare on the proxy's velocity), the S7 light pool as the lure. Evidence: the ROADMAP example list
("wildlife reacting to lights and thruster noise"); the showcase's "exploring under water in a ship". Must not break: S11's
avoidance additions (the proxies in the scans) and the creature caps.

**12. SE beacons and antennas as Subnautica beacons under water (S).** An SE beacon or antenna block on a grid shows as a
Subnautica beacon marker (name and distance) in Subnautica's HUD while the picture is Subnautica's, so a parked base or ship
can be found again in murky water. Hooks: C4/C2 already carry the block and its grid; Subnautica's `PingInstance` for the
marker.
Must not break: SE's own GPS markers (they keep drawing on top); the HUD mask (S16's overlay canvas is the precedent).

## Not on the list (and why)
- Ships above the water seen from below: S10's far-grid impostor (the addendum says so).
- A dive computer on the HUD: S16's HUD A/B shows depth, crush depth, hull and battery already.

