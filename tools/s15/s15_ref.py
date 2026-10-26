"""S15 conventions, the Python twin of the Wire polish rules (src/DeepEngineers.Wire/S15Conventions.cs, S15Rules.cs): the
canonical text and its hash (`ext s15 voice 5`; CONTRACT §5.4 "S15 conventions"), the voice gate over S16's voice rule (the

levels, the sky math (through worldmap_ref.Site's own mapdir, not the C# matrix), the disc light, the palettes and the colour
check. Shares no code with the C#; the vectors it writes under tests/s15/vectors/ are read by the C# tests (S15Tests in
tests/DeepEngineers.Wire.Tests).

Usage (repository root; stdlib only):
 python -I tools/s15/s15_ref.py --selftest hand checks, the pin, the CONTRACT block, vectors current
 python -I tools/s15/s15_ref.py --vectors (re)writes tests/s15/vectors/{gate,aboard,sky,light,palette,wire,picks}.tsv
 python -I tools/s15/s15_ref.py --text prints the canonical text
 python -I tools/s15/s15_ref.py --pin prints hash, lines, bytes
 python -I tools/s15/s15_ref.py --tables prints the voice, mix, colour and sky tables as written
"""
import math
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
VEC_DIR = os.path.join(ROOT, "tests", "s15", "vectors")
CONTRACT = os.path.join(ROOT, "docs", "CONTRACT.md")
for _p in (os.path.join(ROOT, "tools", "worldmap"),):
    if _p not in sys.path:
        sys.path.insert(0, _p)
import worldmap_ref as wm   # noqa: E402 (the site map's own reference: frame_at, dir_se_to_sn)
import site_v1 as sv        # noqa: E402 (SITE_V1: the test site)

# =====================================================================================================================
# the tables (CONTRACT §5.4 "S15 conventions"; the same numbers as S15Conventions.cs)
# =====================================================================================================================
HEADER = "ext s15 voice 5"
PINNED_HASH = 0xd5d5c7fdeeece0ec
CANON_LINES, CANON_BYTES = 126, 16636

WELCOME, POWER_UP, POWER_DOWN, SMALL_WELCOME = 1, 2, 3, 4
C5_COUNT, EVENT_COUNT = 4, 4

VOICES = [(WELCOME, "Welcome", 5000, 120000, "SubRoot", "welcomeNotification", "SubRoot.cs:64", "cyclops", "boarding a large grid or a station"),
          (POWER_UP, "EnginePowerUp", 2000, 10000, "SubRoot", "enginePowerUpNotification", "SubRoot.cs:98", "cyclops", "the aboard grid's power from none to some"),
          (POWER_DOWN, "EnginePowerDown", 2000, 10000, "SubRoot", "enginePowerDownNotification", "SubRoot.cs:100", "cyclops", "the aboard grid's power switch off"),
          (SMALL_WELCOME, "SmallWelcome", 5000, 120000, "Vehicle", "welcomeNotification", "Vehicle.cs:105", "seamoth", "boarding a small grid")]
VOICE_BY_ID = {v[0]: v for v in VOICES}
S16_VOICE_VOLUME_PERMILLE, S16_DUCK_MS = 1600, 3500
VOICE = [("AboardMs", 1000), ("ScanTicks", 10), ("SnBusLivePermille", 500), ("QueueGapMs", S16_DUCK_MS), ("QueueMax", 2), ("VolumePermille", S16_VOICE_VOLUME_PERMILLE)]
V = dict(VOICE)
C5 = [("Offset", 0xAC), ("Size", 0x20), ("Version", 2)]
C5D = dict(C5)
C5_FLAGS = [("Aboard", 1), ("Seated", 2), ("Room", 4), ("Powered", 8), ("Large", 16), ("Station", 32)]
CF = dict(C5_FLAGS)
EVENTS = [("Offset", 0x700), ("Size", 0x40), ("Version", 2)]
EV = dict(EVENTS)
EVENT_FLAGS = [("SonarOn", 1)]
PICKS = [("NightLightPermille", 400), ("SplashMinUnderMs", 1000), ("SonarPeriodMs", 5000), ("SeabaseLightPermille", 600), ("SeabaseLightRangeDm", 80),
         ("FishScareSpeedDms", 40), ("FishScareRangeM", 15), ("FishScarePermille", 1000), ("FishScanMs", 1000), ("FishJumpSpeedDms", 1000)]
PK = dict(PICKS)
MIX = [("MusicAbovePermille", 600), ("VoiceAbovePermille", 600), ("CrossfadeMs", 2000), ("RowMs", 250)]
MX = dict(MIX)
STORY = [("AuroraNeverS", 1000000000)]
# Subnautica's options VCAs by GUID: effects/ambience, music, voice
VCA = {"sfx": "634d9305-7695-40df-999a-cc1d049e1ebe", "music": "0d0dd1e2-9d46-4a5b-aae8-ae412d827831", "voice": "3c4e7775-5fbe-4b00-83e0-35bc9add106a",
       "source": "SoundSystem.cs:57-59"}
MUTANT_PENDING_MS = 60000   # S15.M17 only: the old held edge
SONAR_TAG = "sonar"
SEABASE_LIGHT = (255, 240, 220)
SKY = [("FrameOffset", 0x100), ("FrameSize", 0x60), ("ListOffset", 0x200), ("ListHeader", 0x20), ("RowSize", 0x40), ("MaxBodies", 16),
       ("NameMax", 28), ("Version", 1), ("ListRefreshTicks", 300), ("DiscDistancePermille", 900), ("DiscSegments", 48), ("DiscRings", 6),
       ("AmbientPermille", 60), ("LimbPermille", 850), ("DayAlphaPermille", 850), ("NightAlphaPermille", 1000), ("RenderQueue", 2600),
       ("RowTicks", 60)]
SK = dict(SKY)
SKY_FLAGS = [("Valid", 1)]
BODY_FLAGS = [("Bound", 1), ("Atmosphere", 2)]
ORACLE = [("AngleCentiDeg", 100), ("SizePermille", 100), ("StillCentiDeg", 5)]
OR = dict(ORACLE)
MOONS = [("PlanetRadiusMicro", 1000), ("TextureSize", 4)]
COLOUR = [("TolChannel", 24), ("TolLuma", 16), ("CentrePermille", 400), ("LimbInnerPermille", 850), ("LimbOuterPermille", 970), ("BandOffsetM", 50),
          ("FacePermille", 300), ("SeaLeadMin", 25), ("CloudLumaMin", 200), ("SeabedMaxPermille", 100), ("SeaChromaPermille", 30)]
CO = dict(COLOUR)
# Subnautica's measured band colours (Wire S7tBandTargets; tests/s7t/band_compare.py TARGETS["sn"])
SN_NEAR_WATER, SN_SKY_HORIZON = (65, 97, 122), (197, 241, 255)
PALETTES = [(0, "*", (128, 128, 128), (170, 180, 200)),
            (1, "DE_4546B", SN_NEAR_WATER, SN_SKY_HORIZON),
            (2, "EarthLike", (70, 110, 160), (140, 190, 255)),
            (3, "Moon", (150, 150, 150), (150, 150, 150)),
            (4, "Mars", (170, 100, 70), (210, 160, 130)),
            (5, "Europa", (200, 215, 230), (220, 235, 255)),
            (6, "Alien", (110, 130, 90), (170, 220, 160)),
            (7, "Titan", (200, 170, 120), (230, 200, 150)),
            (8, "Triton", (180, 200, 215), (220, 230, 240)),
            (9, "Pertam", (190, 140, 90), (230, 190, 140))]
# Subnautica's ending from orbit (format ending.palette; tools/s15/measure_ending.
ENDING = {"sea": (34, 59, 75), "cloud": (120, 114, 110), "cover": 127, "limb": (66, 69, 77), "rimWarm": (164, 135, 121), "rimCool": (82, 85, 94)}
ORBIT = [(600, ENDING["sea"], ENDING["limb"]), (200, ENDING["sea"], ENDING["limb"]), (100, ENDING["sea"], ENDING["limb"])]
WATER = {"material": "DE_4546B_Water", "texture": "Textures\\DE_4546B_Water.dds", "size": 256, "mean": (110, 156, 184), "previous": "JWater2",
         "transparent": False, "softnessOffset": "0.0001", "specularIntensity": "0"}
AIR_KM = (30, 5)
WATER_FAR = {"material": "DE_4546B_WaterFar", "texture": "Textures\\DE_4546B_WaterFar.dds", "mean": (62, 96, 122), "up": 20000, "down": 15000,
             "seaMaterial": "DE_4546B_Sea", "reflectivity": "0.5", "plain": "Water"}

FORMAT_LINES = [
    "c6.s15events C6 region +0x700, its own seqlock, written with every C6 while S15 is active: version 2, flags (SonarOn: the sonar switch on aboard), count 0, worldEpoch, the sonar ping counter (read the c5.s15 way); the rest zero",
    "aboard the aboard grid is the grid of the cockpit the local character sits in (MyCharacter.UsingEntity as MyCockpit), else the grid whose airtight room (the S7 air rule) holds the character's head, else (for voice.power only) a station grid (IsStatic) whose world box holds the head: a base or a house; it counts after AboardMs; a door, a ladder or an open deck of a ship never counts",
    "voice.welcome Welcome while the aboard grid is a large grid or a station (MyCubeGrid.GridSizeEnum Large or IsStatic) and the player is in its seat or a sealed room of it; SmallWelcome the same for a small dynamic grid (the Seamoth's own boarding line, Vehicle.welcomeNotification of the Seamoth prefab, else its Vehicle.enterSound); the rising edge is the boarding itself, so a dive never greets",
    "voice.power EnginePowerUp while the aboard grid is powered: its resource distributor's electricity state (MyResourceDistributorComponent.ResourceStateByType without a grid: the grid's whole network, read without a recompute) is not NoPower, so the edge is the grid's power going from none to some (a reactor placed or fuelled with uranium, the power switch on, any source); EnginePowerDown while the grid's power switch is off (MyCubeGrid.IsPowerSwitchOn false: SE's Y key, MyShipController.SwitchReactorsLocal) after it went off from a powered grid; never thrust or thruster strength",
    "voice.gate per (event, grid): a rising edge of the event's condition after it was off RearmMs is an event; S16VoiceRule (one per event, CooldownMs) decides; an event met while SE's audio is live (C5 snBusTarget < SnBusLivePermille / 1000) is skipped: not sent, no cooldown started, never held or replayed (owner 2026-10-24: no line on a dive); a grid that becomes aboard takes its power state and its switch as the baseline (boarding a powered grid is no EnginePowerUp, a switched-off one no EnginePowerDown); no once-per-session rule",
    "c5.s15 SeState 0xac-0xcb, version 2 (0 = absent): flags (Aboard, Seated, Room, Powered, Large, Station), worldEpoch, the counters of events 1-4 (u32, never reset in an SE session), the aboard grid; a reader takes its first value as the baseline and plays one line per increment seen in the same worldEpoch (S16SeqReader)",
    "voice.mix Subnautica plays each line (SubRoot.<member>.sound of the Cyclops prefab, Vehicle.welcomeNotification.sound of the Seamoth prefab) as an FMOD instance at the camera at S16VoiceMix.VoiceVolume, never started while its C5 snBusTarget says SE's audio is live; one voice at a time both ways on one shared clock: an S15 line waits QueueGapMs after the last S15 or S16 voice, S16's hull voice waits QueueGapMs after an S15 line, S16's creaks and groans duck under both; at most QueueMax lines waiting (a new line beyond that is dropped, a welcome replaces the newest other line)",
    "voice.end a line started under water plays to its end above water: the voice group stays live across the surface (audio.groups); each played line is followed to its end (its playback state, timeline position and length) with the voice group's lowest level and the master mute during it",
    "audio.groups Subnautica's mix in groups (its options VCAs by GUID, SoundSystem.cs:57-59; the player's own slider value kept as the factor): the effects and ambience group follows the S2 rule (C5 snBusTarget over crossfadeMs: 0 above water), the music and voice groups go to Mix.MusicAbovePermille and Mix.VoiceAbovePermille / 1000 above water and back to 1 under it over Mix.CrossfadeMs; bus:/ stays at 1 and unmuted above water (still muted with no live SE, SE not in front, quitting); without the VCAs the S2 rule's old bus:/ fade and mute",
    "story.off Subnautica's story goals are skipped (StoryGoal.Execute, StoryGoal.cs:31: no PDA log line, no radio message, no databank entry, no goal completion); every PDA voice request (SoundQueue.Play) is logged; the system lines DeepEngineers keeps (the Cyclops and Seamoth set, the sonar, Subnautica's own oxygen alert) are not story goals",
    "aurora.off the Aurora's countdown and explosion never start (CrashedShipExploder: its warning and countdown times moved to Story.AuroraNeverS before each Update, so AuroraWarnings and the countdown sound, explosion, camera shake and radius damage never trigger); a save whose Aurora already exploded keeps its model and its Update is skipped (no periodic camera shake or rumble)",
    "night.lights the mirrored block lights (S7's kinds BlockPoint and BlockSpot) at NightLightPermille / 1000 + (1 - NightLightPermille / 1000) x DayNightCycle.GetLightScalar() of their intensity; the character's lamps untouched",
    "lens.splash Subnautica's LensWater.CreateSplash on the main camera when the camera rises through the sea surface after SplashMinUnderMs under it",
    "sonar.ping a functional block of the aboard grid whose CustomName holds SonarTag (any case) and is on, while seated, pings every SonarPeriodMs: SE counts it in the C6 events block; Subnautica plays the Cyclops's sonar sound (CyclopsSonarButton.soundFX's asset) and SNCameraRoot.SonarPing (the SonarScreenFX pulse), only with its audio live",
    "seabase.room the camera in an airtight room under the sea (S7's inside flag): the FMODGameParams loops of state OnlyInBase play (their state set to OnlyInside, which S7's IsInsideWalkable answers) and one point light of SeabaseLight colour, SeabaseLightPermille / 1000 intensity and SeabaseLightRangeDm / 10 m range follows the camera; both restored when it leaves",
    "fish.scare a ship's proxy section (S6) faster than FishScareSpeedDms / 10 m/s (and not over FishJumpSpeedDms / 10: a jump) scares the small fish (a Scareable with a FleeWhenScared) within FishScareRangeM of it: Creature.Scared + FishScarePermille / 1000, CreatureFear.SetScarePosition at the section, the flee action started; checked every FishScanMs",
    "air.colour SE's picture from a spectator over the site looking down at each air row's altitude (de.s15 orbit, SE's own screenshot): the face (the frame's central FacePermille circle) has at most SeabedMaxPermille seabed pixels (neither sea: the larger of green and blue ahead of red by SeaLeadMin, nor cloud: luma CloudLumaMin) and, for a row at or above WaterFar.SwapUpM (the far sea), its sea pixels' mean has the row's hue (each of r, g, b / (r + g + b) within SeaChromaPermille / 1000) and a luma from the row's less TolLuma to Subnautica's near water's plus TolLuma (the ending's low sun to the noon over the site); below it (the near sea, tuned for the switch) the hue is information",
    "water.far the far sea: above WaterFar.SwapUpM over the sea the SE plugin sets the planet's texture to WaterFar.Material and back to Water.Material below WaterFar.SwapDownM (once per crossing, a row each), never under S15.M4 or with the water switch off; the near texture is tuned for the switch (Subnautica's near water), the far one for the ending's hue",
    "band.colour the four bands of compare.py bands (sky_top, sky_horizon, far_water, near_water) of SE's picture at band enter + BandOffsetM against Subnautica's at band enter - BandOffsetM, same heading: sky_top, sky_horizon and near_water each within TolChannel per channel and TolLuma, far_water by band.far; the pair with the Water Mod's own material (WaterFar.PlainMaterial) is information",
    "band.far SE's far_water within TolChannel per channel and TolLuma of the haze line: the segment from Subnautica's far_water to its sky_horizon in the same pair (its nearest point in RGB; the luma against that point's). SE's atmosphere carries the far sea to its horizon colour within a few km (run 20261024-063156, luma: (far - near) / (horizon - near) 0.98, Subnautica's water fog 0.15) and no Water Mod value moves it (its billboards take Reflectivity x max(fresnel, 0.1), the light, the specular and the texture, no fog above water; Reflectivity 0.925 to 0.5 moved the far luma by -4, the near by +15), so any point of that line is either game's far sea; a far sea off it (teal, dark, land) fails",
    "c6.s15frame C6 region +0x100, its own seqlock, written with every C6: tick, listSeq, camRel = P - C (SE world, f64), the rows of mapdir at P for the current chart (SN = M x SE, f32); a reader takes the seq it finds first as a baseline (fresh only after a change, 3 s) and the frame's worldEpoch must be C5's",
    "c6.s15list C6 region +0x200, its own seqlock: count <= MaxBodies, nearest the bound planet first: centre - C (SE world, f64), AverageRadius, AtmosphereRadius, palette, flags, ASCII name <= NameMax; written when the list changes (listSeq + 1) and every ListRefreshTicks",
    "moons.hide Subnautica's two sky bodies off: uSkyManager.planetRadius PlanetRadiusMicro with the planet held opposite the sun (planetOrbitSpeed 0, planetZenith from the sun: no eclipse), the planet's and the moon's coronas rgb 0 (alpha kept), MoonTexture transparent; the constant properties sent again to the skybox, skymap, fog and water materials; everything restored when the sky is off; judged on night captures of Subnautica's picture along both signs of MoonLight's axis (the centre no brighter than the sky around it by TolLuma)",
    "orbit.colour SE's picture from a spectator over the site looking at the planet centre at each orbit row's altitude: the disc's inner CentrePermille radius (centre) judged as air.colour's face against the row's centre; information: its LimbInnerPermille-LimbOuterPermille ring against the row's limb, the rim beyond the disc against the ending's warm and cool rim, the centre's cloud share against the ending's cover (SE draws the Water Mod's surface after its atmosphere and cloud layers, MyTransparentRendering.cs:193-213, so neither haze nor clouds can cover the sea)",
    "ending.palette Subnautica's ending from orbit (the owner's frames, tools/s15/measure_ending.py, palette only): the sea, the clouds and their cover of the face, the limb, the rim warm and cool; the orbit and air rows' targets, the water texture's hue and the planet definition's atmosphere and clouds",
    "sky.dir each body's direction in the camera frame (right, up, forward): SE from its camera matrix and the body's centre, Subnautica from Unity's camera transform and the disc's position; the angle between them <= AngleCentiDeg / 100 degrees on the same SE tick while the SE camera is still",
    "sky.disc a camera-facing disc per body except the bound planet at DiscDistancePermille of the camera's far plane, radius D tan(alpha); vertex colours the palette lit by uSkyManager.SunDir as a sphere (floor AmbientPermille), the limb blended to the palette's limb colour past LimbPermille of the radius for a body with an atmosphere; Sprites/Default at RenderQueue, no depth write; shown while the camera is above the sea",
    "sky.size each body's angular radius asin(AverageRadius / distance): Subnautica's disc (atan(radius / distance) of its transform) within SizePermille / 1000 of SE's (the transport of the list and the frame); measured in pixels on a capture of each picture aimed at the body (atan(r px / focal px) from the vertical field of view) as information",
    "water.material DE_4546B's sea uses the world mod's TransparentMaterial Material (Texture, generated, its mean sRGB pinned: the ending's sea hue at the near water's brightness through the band pair's gains) with the Water Mod's Transparent off, its MaterialId WaterFar.SeaMaterial (its own Water material with Reflectivity WaterFar.SeaReflectivity: the near water at the switch, run 20261024-063156), its DistantSoftnessOffset for the planet (the surface's soft fade capped at 30 m above 4.9 km) and its SpecularIntensity Water.SpecularIntensity (the mod adds a specular term to a quad only where its sunDot > 0, with a half vector mixing planet-local and world space: a step along the quad edges at the sea's terminator, the seam of the look sheet 20261024-124136); Data/WaterConfig.xml sets the five for new worlds (the softness and the specular at every load; the plugin's look defaults keep the specular at Water.SpecularIntensity), the SE plugin through de.water texture, transparent and material once per world load for an existing world (when MyTransparentMaterials.ContainsMaterial), every 10 s until the three read back, never again after that (the owner's /wtransparent and de.water material=WaterFar.PlainMaterial hold for the session); judged on captures (air.colour, orbit.colour), the read-back is information",
]

# the gate's results (S15GateResult; 3 was the retired once-per-session result)
NONE, PLAYED, COOLDOWN, SEAUDIO = 0, 1, 2, 4
RESULT_NAMES = {NONE: "none", PLAYED: "played", COOLDOWN: "cooldown", SEAUDIO: "seAudio"}

def _rgb(c):
    return "%d,%d,%d" % tuple(c)

def canonical_text():
    out = [HEADER]
    for vid, name, rearm, cool, owner, member, src, prefab, when in VOICES:
        out.append("voice %d %s key grid rearmMs %d cooldownMs %d member %s.%s %s prefab %s when %s" % (vid, name, rearm, cool, owner, member, src, prefab, when))
    groups = [("Voice", VOICE), ("C5", C5), ("C5Flags", C5_FLAGS), ("Events", EVENTS), ("EventFlags", EVENT_FLAGS), ("Picks", PICKS), ("Mix", MIX),
              ("Story", STORY), ("Sky", SKY), ("SkyFlags", SKY_FLAGS), ("BodyFlags", BODY_FLAGS), ("Oracle", ORACLE), ("Moons", MOONS), ("Colour", COLOUR)]
    for g, items in groups:
        for k, v in items:
            out.append("const %s %s %d" % (g, k, v))
    for pid, prefix, rgb, limb in PALETTES:
        out.append("palette %d %s rgb %s limb %s" % (pid, prefix, _rgb(rgb), _rgb(limb)))
    for km, centre, limb in ORBIT:
        out.append("orbit %d km centre %s limb %s" % (km, _rgb(centre), _rgb(limb)))
    e = ENDING
    out.append("ending sea %s cloud %s cover %d limb %s rim warm %s cool %s" % (_rgb(e["sea"]), _rgb(e["cloud"]), e["cover"], _rgb(e["limb"]),
                                                                              _rgb(e["rimWarm"]), _rgb(e["rimCool"])))
    for km in AIR_KM:
        out.append("air %d km face %s" % (km, _rgb(e["sea"])))
    out.append("mix vca sfx %s music %s voice %s %s" % (VCA["sfx"], VCA["music"], VCA["voice"], VCA["source"]))
    out.append("sonar tag %s" % SONAR_TAG)
    out.append("seabase light %s" % _rgb(SEABASE_LIGHT))
    f = WATER_FAR
    out.append("water far %s texture %s mean %s up %d down %d seaMaterial %s reflectivity %s plain %s" % (f["material"], f["texture"], _rgb(f["mean"]), f["up"], f["down"],
                                                                                                   f["seaMaterial"], f["reflectivity"], f["plain"]))
    out.append("water %s texture %s size %d mean %s previous %s transparent %s softnessOffset %s specularIntensity %s" % (
        WATER["material"], WATER["texture"], WATER["size"], _rgb(WATER["mean"]), WATER["previous"], "on" if WATER["transparent"] else "off", WATER["softnessOffset"],
        WATER["specularIntensity"]))
    for f in sorted(FORMAT_LINES):
        out.append("format " + f)
    return "\n".join(out) + "\n"

def fnv1a64(text):
    h = 0xcbf29ce484222325
    for b in text.encode("ascii"):
        h ^= b
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h

# =====================================================================================================================
# the rules
# =====================================================================================================================
class VoiceRule:
    """S16VoiceRule's twin: a key plays when it played none within the cooldown."""

    def __init__(self, cooldown_ms):
        self.cooldown, self.last = cooldown_ms, {}

    def try_fire(self, key, now):
        if key in self.last and now - self.last[key] < self.cooldown:
            return False
        self.last[key] = now
        return True

class Gate:
    """S15VoiceGate's twin (format voice.gate): edges, rearm, S16's cooldown; an edge met while SE's audio is live is skipped for
 hold=True is the seeded failure S15.M17: the old held edge); a new aboard grid's baseline."""

    def __init__(self):
        self.state = {}
        self.rules = {vid: VoiceRule(VOICE_BY_ID[vid][3]) for vid in VOICE_BY_ID}

    def _s(self, ev, key):
        return self.state.setdefault((ev, key), {"on": False, "ever": False, "last_on": 0, "pending": False, "since": 0})

    def baseline(self, ev, key, cond, now):
        if ev not in VOICE_BY_ID:
            return
        s = self._s(ev, key)
        s["on"], s["pending"] = bool(cond), False
        if cond:
            s["ever"], s["last_on"] = True, now

    def pending(self):
        return sorted(k for k, s in self.state.items() if s["on"] and s["pending"])

    def step(self, ev, key, cond, now, live=True, no_rules=False, hold=False):
        row = VOICE_BY_ID.get(ev)
        if row is None:
            return NONE
        s = self._s(ev, key)
        r = NONE
        if cond and not s["on"]:
            off_for = now - s["last_on"] if s["ever"] else float("inf")
            if no_rules:
                r = PLAYED
            elif s["ever"] and off_for < row[2]:
                r = NONE
            elif not live:
                r = SEAUDIO
                if hold:
                    s["pending"], s["since"] = True, now
            elif not self.rules[ev].try_fire(key, now):
                r = COOLDOWN
            else:
                r = PLAYED
        elif cond and s["pending"]:
            if now - s["since"] > MUTANT_PENDING_MS:
                s["pending"] = False
            elif live:
                s["pending"] = False
                r = PLAYED if self.rules[ev].try_fire(key, now) else COOLDOWN
        s["on"] = bool(cond)
        if cond:
            s["ever"], s["last_on"] = True, now
        else:
            s["pending"] = False
        return r

def sn_audio_live(sn_bus_target):
    return sn_bus_target * 1000.0 >= V["SnBusLivePermille"]

class AboardHold:
    """S15AboardHold's twin (format aboard): the raw aboard grid counts after AboardMs."""

    def __init__(self):
        self.candidate, self.since, self.current = 0, 0, 0

    def step(self, raw, now, no_hold=False):
        if raw != self.candidate:
            self.candidate, self.since = raw, now
        if raw == 0:
            self.current = 0
            return 0
        if no_hold or now - self.since >= V["AboardMs"]:
            self.current = raw
        elif self.current != raw:
            self.current = 0
        return self.current

class PowerSwitch:
    """S15PowerSwitch's twin (format voice.power): EnginePowerDown's condition, the switch off after it went off from a powered
 grid (the scan before); a new grid is its baseline."""

    def __init__(self):
        self.have, self.grid, self.switch_on, self.last_powered, self.powered_at_off = False, 0, True, False, False

    def baseline(self, grid, switch_on, powered):
        self.have, self.grid, self.switch_on, self.last_powered, self.powered_at_off = True, grid, bool(switch_on), bool(powered), False

    def step(self, grid, switch_on, powered):
        if not self.have or grid != self.grid:
            self.baseline(grid, switch_on, powered)
            return False
        if self.switch_on and not switch_on:
            self.powered_at_off = self.last_powered or bool(powered)
        elif switch_on:
            self.powered_at_off = False
        self.switch_on, self.last_powered = bool(switch_on), bool(powered)
        return (not switch_on) and self.powered_at_off

# ---------------------------------------------------------------------------------------------------------- audio groups
def clamp01(v):
    return 0.0 if v != v else (0.0 if v < 0.0 else (1.0 if v > 1.0 else v))

def group_target(t, above_level):
    """format audio.groups: a live group's target (the music, the voice) for the S2 target t (1 under water, 0 above)."""
    a = clamp01(above_level)
    return a + (1.0 - a) * clamp01(t)

def vca_volume(user, level):
    return clamp01(user) * clamp01(level)

# ---------------------------------------------------------------------------------------------------------- codecs
def pack_c5(flags, epoch, welcome, up, down, small, grid):
    return struct.pack("<BBHIIIIIq", C5D["Version"], flags, 0, epoch, welcome, up, down, small, grid)

def unpack_c5(b):
    if len(b) < C5D["Size"] or b[0] != C5D["Version"]:
        return None
    v, flags, _r, epoch, w, u, d, s, grid = struct.unpack("<BBHIIIIIq", bytes(b[:C5D["Size"]]))
    return {"flags": flags, "epoch": epoch, "seq": {WELCOME: w, POWER_UP: u, POWER_DOWN: d, SMALL_WELCOME: s}, "grid": grid}

def pack_events(flags, epoch, sonar_seq):
    """The events block's bytes 0..Size with a zero seq word (format c6.s15events, version 2: the sonar)."""
    b = bytearray(EV["Size"])
    struct.pack_into("<BBHII", b, 4, EV["Version"], flags, 0, epoch, sonar_seq)
    return bytes(b)

def unpack_events(b):
    if len(b) < EV["Size"] or b[4] != EV["Version"]:
        return None
    version, flags, count, epoch, sonar = struct.unpack_from("<BBHII", b, 4)
    return {"flags": flags, "count": count, "epoch": epoch, "sonarSeq": sonar}

# ---------------------------------------------------------------------------------------------------------- the kept picks' rules
def night_factor(light_scalar):
    n = PK["NightLightPermille"] / 1000.0
    return n + (1.0 - n) * max(0.0, min(1.0, light_scalar))

def scares(speed_mps):
    return speed_mps * 10.0 > PK["FishScareSpeedDms"] and not speed_mps * 10.0 > PK["FishJumpSpeedDms"]

def is_sonar_name(name):
    return bool(name) and SONAR_TAG in name.lower()

def pack_frame(flags, body_count, list_seq, epoch, tick, cam_rel, m9):
    """The frame record's bytes 4..FrameSize (the seq word is the seqlock's)."""
    b = struct.pack("<BBHIIQddd9f", SK["Version"], flags, body_count, list_seq, epoch, tick, cam_rel[0], cam_rel[1], cam_rel[2], *m9)
    return b + b"\0" * (SK["FrameSize"] - 4 - len(b))

def pack_list(list_seq, epoch, bodies):
    """The list record's bytes 4..end; bodies = (rel, radius, atmo, palette, flags, name)."""
    n = min(len(bodies), SK["MaxBodies"])
    head = struct.pack("<BBHII", SK["Version"], n, 0, list_seq, epoch) + b"\0" * 16
    rows = b""
    for i in range(SK["MaxBodies"]):
        if i < n:
            rel, rad, atmo, pal, fl, name = bodies[i]
            nm = "".join(c if 32 <= ord(c) < 127 else "?" for c in name)[:SK["NameMax"]].encode("ascii")
            row = struct.pack("<dddffHBB", rel[0], rel[1], rel[2], rad, atmo, pal, fl, len(nm)) + nm
            rows += row + b"\0" * (SK["RowSize"] - len(row))
        else:
            rows += b"\0" * SK["RowSize"]
    return head + rows

# ---------------------------------------------------------------------------------------------------------- sky
def frame_rows(site, P):
    """mapdir at P as rows (SN = M x SE), through Site.dir_se_to_sn on the three SE axes."""
    cols = [site.dir_se_to_sn(P, a) for a in ((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0))]
    return [cols[0][0], cols[1][0], cols[2][0], cols[0][1], cols[1][1], cols[2][1], cols[0][2], cols[1][2], cols[2][2]]

def sky_view(site, P, body_centre, radius):
    """(dir_sn unit, angular radius rad, distance m) of a body seen from SE point P, or None inside it."""
    d = wm.sub(body_centre, P)
    dist = wm.norm(d)
    if not (dist > radius) or not (radius > 0):
        return None
    w = wm.mul(d, 1.0 / dist)
    dn = site.dir_se_to_sn(P, w)
    return wm.unit(dn), math.asin(radius / dist), dist

def angle_deg(a, b):
    la, lb = wm.norm(a), wm.norm(b)
    if not (la > 0 and lb > 0):
        return float("nan")
    c = max(-1.0, min(1.0, wm.dot(a, b) / (la * lb)))
    return math.degrees(math.acos(c))

def light(a, b, to_body, right, up, to_sun):
    r2 = a * a + b * b
    z = math.sqrt(1.0 - r2) if r2 < 1.0 else 0.0
    n = wm.add(wm.add(wm.mul(right, a), wm.mul(up, b)), wm.mul(to_body, -z))
    l = wm.dot(n, to_sun)
    floor = SK["AmbientPermille"] / 1000.0
    return l if l > floor else floor

def limb_weight(r):
    r0 = SK["LimbPermille"] / 1000.0
    if r <= r0:
        return 0.0
    if r >= 1:
        return 1.0
    t = (r - r0) / (1 - r0)
    return t * t * (3 - 2 * t)

def palette_of(subtype):
    if subtype:
        for pid, prefix, _rgb_, _limb in PALETTES[1:]:
            if subtype.lower().startswith(prefix.lower()):
                return pid
    return 0

def classify(rgb):
    """format air.colour: 'sea' (the larger of green and blue ahead of red by SeaLeadMin), 'cloud' (luma >= CloudLumaMin), else 'seabed'."""
    r, g, b = rgb
    if max(g, b) - r >= CO["SeaLeadMin"]:
        return "sea"
    if luma(r, g, b) >= CO["CloudLumaMin"]:
        return "cloud"
    return "seabed"

def luma(r, g, b):
    return 0.2126 * r + 0.7152 * g + 0.0722 * b

def chroma(rgb):
    s = float(sum(rgb)) or 1.0
    return tuple(c / s for c in rgb)

def sea_ok(rgb, target):
    """formats air.colour / orbit.colour: (ok, hue off per mille, luma): the target's hue within SeaChromaPermille, a luma from
 the target's less TolLuma to Subnautica's near water's plus TolLuma (the ending's low sun to the noon over the site)."""
    d = 1000.0 * max(abs(a - b) for a, b in zip(chroma(rgb), chroma(target)))
    lo, hi = luma(*target) - CO["TolLuma"], luma(*SN_NEAR_WATER) + CO["TolLuma"]
    lu = luma(*rgb)
    return d <= CO["SeaChromaPermille"] and lo <= lu <= hi, d, lu

def colour_within(rgb, target, tol_c=None, tol_l=None):
    tol_c = CO["TolChannel"] if tol_c is None else tol_c
    tol_l = CO["TolLuma"] if tol_l is None else tol_l
    d_max = max(abs(a - b) for a, b in zip(rgb, target))
    d_l = abs(luma(*rgb) - luma(*target))
    return d_max <= tol_c and d_l <= tol_l, d_max, d_l

def haze_line(rgb, sn_far, sn_horizon, tol_c=None, tol_l=None):
    """format band.far: SE's far water against the segment from Subnautica's far water to its horizon (same pair): its nearest
 point in RGB, then T5 per channel and in luma against that point. (ok, dMax, dL, t, point)"""
    d = [h - f for h, f in zip(sn_horizon, sn_far)]
    dd = sum(c * c for c in d)
    t = 0.0 if dd <= 0 else max(0.0, min(1.0, sum((c - f) * k for c, f, k in zip(rgb, sn_far, d)) / dd))
    point = tuple(f + t * k for f, k in zip(sn_far, d))
    ok, d_max, d_l = colour_within(rgb, point, tol_c, tol_l)
    return ok, d_max, d_l, t, point

# =====================================================================================================================
# vectors
# =====================================================================================================================
GATE_SCENARIOS = [
    # (scenario, [(ev, key, cond, ms, live, mode)]); mode 0 normal, 1 no rules (S15.M1), 2 a baseline sample, 3 the held edge (S15.M17)
    ("welcome", [(WELCOME, 7, 1, 0, 1, 0), (WELCOME, 7, 0, 1000, 1, 0), (WELCOME, 7, 1, 3000, 1, 0), (WELCOME, 7, 0, 4000, 1, 0),
                 (WELCOME, 7, 1, 10000, 1, 0), (WELCOME, 7, 0, 11000, 1, 0), (WELCOME, 7, 1, 200000, 1, 0), (WELCOME, 7, 1, 201000, 1, 0)]),
    ("power", [(POWER_UP, 9, 1, 0, 1, 0), (POWER_UP, 9, 0, 1000, 1, 0), (POWER_UP, 9, 1, 5000, 1, 0), (POWER_UP, 9, 0, 6000, 1, 0),
               (POWER_UP, 9, 1, 12000, 1, 0), (POWER_UP, 9, 0, 13000, 1, 0), (POWER_UP, 9, 1, 13500, 1, 0)]),
    ("down", [(POWER_DOWN, 3, 1, 0, 1, 0), (POWER_DOWN, 3, 0, 4000, 1, 0), (POWER_DOWN, 3, 1, 8000, 1, 0), (POWER_DOWN, 3, 0, 9000, 1, 0),
              (POWER_DOWN, 3, 1, 15000, 1, 0)]),
    ("se-audio", [(WELCOME, 3, 1, 0, 0, 0), (WELCOME, 3, 1, 20000, 1, 0), (WELCOME, 3, 0, 21000, 1, 0), (WELCOME, 3, 1, 30000, 1, 0)]),
    ("surface-dive", [(SMALL_WELCOME, 6, 1, 0, 0, 0), (SMALL_WELCOME, 6, 1, 167, 0, 0), (SMALL_WELCOME, 6, 1, 20000, 1, 0), (SMALL_WELCOME, 6, 1, 20167, 1, 0)]),
    ("held-m17", [(WELCOME, 6, 1, 0, 0, 3), (WELCOME, 6, 1, 167, 0, 3), (WELCOME, 6, 1, 20000, 1, 3), (WELCOME, 6, 1, 20167, 1, 3)]),
    ("keys", [(POWER_UP, 1, 1, 0, 1, 0), (POWER_UP, 2, 1, 10, 1, 0), (POWER_UP, 1, 0, 3000, 1, 0), (POWER_UP, 1, 1, 6000, 1, 0), (WELCOME, 1, 1, 6000, 1, 0)]),
    ("bounce", [(POWER_UP, 4, 1, 0, 1, 0), (POWER_UP, 4, 0, 500, 1, 0), (POWER_UP, 4, 1, 1000, 1, 0), (POWER_UP, 4, 0, 1500, 1, 0), (POWER_UP, 4, 1, 4000, 1, 0)]),
    ("mutant-m1", [(WELCOME, 5, 1, 0, 1, 1), (WELCOME, 5, 0, 100, 1, 1), (WELCOME, 5, 1, 200, 0, 1), (WELCOME, 5, 0, 300, 1, 1), (WELCOME, 5, 1, 400, 1, 1)]),
    ("baseline", [(POWER_UP, 7, 1, 0, 1, 2), (POWER_UP, 7, 1, 167, 1, 0), (POWER_UP, 7, 0, 1000, 1, 0), (POWER_UP, 7, 1, 4000, 1, 0)]),
]

ABOARD_SCENARIO = [(0, 0), (7, 100), (7, 600), (7, 1100), (7, 1500), (0, 1600), (7, 1700), (7, 2600), (8, 2700), (8, 3800), (0, 4000)]

# the power switch (grid, switchOn, powered) per scan; "B" marks a baseline sample
POWER_SCENARIOS = [
    ("y-off", [("B", 5, 1, 1), (5, 1, 1), (5, 0, 0), (5, 0, 0), (5, 1, 1), (5, 0, 0)]),
    ("off-unpowered", [("B", 5, 1, 0), (5, 1, 0), (5, 0, 0), (5, 0, 0)]),
    ("boarded-off", [("B", 6, 0, 0), (6, 0, 0), (6, 1, 0), (6, 1, 1), (6, 0, 0)]),
    ("new-grid", [("B", 5, 1, 1), (5, 0, 0), (8, 0, 0), (8, 0, 0)]),
]

def gate_vectors():
    lines = ["scenario\tev\tkey\tcond\tms\tlive\tmode\tresult"]
    for name, steps in GATE_SCENARIOS:
        g = Gate()
        for ev, key, cond, ms, live, mode in steps:
            if mode == 2:
                g.baseline(ev, key, cond, ms)
                r = NONE
            else:
                r = g.step(ev, key, cond, ms, bool(live), mode == 1, mode == 3)
            lines.append("%s\t%d\t%d\t%d\t%d\t%d\t%d\t%d" % (name, ev, key, cond, ms, live, mode, r))
    return lines

def aboard_vectors():
    lines = ["raw\tms\tnohold\tcurrent"]
    for nh in (0, 1):
        h = AboardHold()
        for raw, ms in ABOARD_SCENARIO:
            lines.append("%d\t%d\t%d\t%d" % (raw, ms, nh, h.step(raw, ms, bool(nh))))
    return lines

SKY_CAMS = [(0.0, 150.0, 0.0), (5000.0, 800.0, -3000.0), (40000.0, 300.0, 20000.0), (-12000.0, 900.0, 61000.0)]
SKY_BODIES = [("Moon", (0.0, 0.0, 300000.0), 19000.0), ("EarthLike", (-400000.0, 250000.0, 100000.0), 60000.0),
              ("Mars", (900000.0, -150000.0, -500000.0), 60000.0), ("Europa", (120000.0, 520000.0, -60000.0), 9500.0)]

def sky_vectors():
    site = sv.SITE_V1
    lines = ["cam\tbody\tdirX\tdirY\tdirZ\tangRad\tdist\tm00\tm01\tm02\tm10\tm11\tm12\tm20\tm21\tm22"]
    for ci, q in enumerate(SKY_CAMS):
        P = site.sn_to_se(q)
        m = frame_rows(site, P)
        for name, rel, rad in SKY_BODIES:
            v = sky_view(site, P, wm.add(site.C, rel), rad)
            if v is None:
                continue
            d, a, dist = v
            lines.append("%d\t%s\t%.12f\t%.12f\t%.12f\t%.12f\t%.6f\t%s" % (ci, name, d[0], d[1], d[2], a, dist, "\t".join("%.12f" % x for x in m)))
    return lines

def light_vectors():
    lines = ["a\tb\tbx\tby\tbz\trx\try\trz\tux\tuy\tuz\tsx\tsy\tsz\tlight\tlimb"]
    to_body = wm.unit((0.3, 0.5, 0.81))
    right = wm.unit(wm.cross((0.0, 1.0, 0.0), to_body))
    up = wm.cross(to_body, right)
    for sun in ((0.0, 1.0, 0.0), wm.unit((1.0, 0.2, -0.3)), wm.unit((-0.3, -0.5, -0.81))):
        for a, b in ((0.0, 0.0), (0.5, 0.0), (-0.5, 0.3), (0.0, -0.9), (0.6, 0.6), (0.999, 0.0)):
            r = math.hypot(a, b)
            lines.append("\t".join("%.12f" % x for x in (a, b) + to_body + right + up + sun + (light(a, b, to_body, right, up, sun), limb_weight(r))))
    return lines

PALETTE_NAMES = ["DE_4546B", "de_4546b", "EarthLike", "EarthLikeTutorial", "Moon", "MoonTutorial", "Mars", "MarsTutorial", "Europa", "Alien",
                 "AlienPlanet", "Titan", "Triton", "Pertam", "Venus", "", "Planet"]

def palette_vectors():
    return ["name\tpalette"] + ["%s\t%d" % (n, palette_of(n)) for n in PALETTE_NAMES]

def wire_vectors():
    lines = ["kind\thex"]
    lines.append("c5\t" + pack_c5(1 | 2 | 8 | 16, 7, 1, 2, 3, 4, 0x0123456789ABCDEF).hex())
    lines.append("c5zero\t" + pack_c5(0, 1, 0, 0, 0, 0, -5).hex())
    m9 = [0.5, -0.25, 0.125, 1.0, 0.0, -1.0, 0.75, 0.0625, -0.5]
    lines.append("frame\t" + pack_frame(1, 3, 42, 9, 123456789, (1.5, -2.25, 60150.0), m9).hex())
    bodies = [((0.0, 0.0, 300000.0), 19000.0, 0.0, 3, 0, "Moon"), ((1.0, 2.0, 3.0), 60000.0, 69000.0, 1, 3, "DE_4546B"),
              ((-4.5, 0.25, 9e5), 9500.0, 10450.0, 5, 2, "Europa-with-a-very-long-name-cut")]
    lines.append("list\t" + pack_list(5, 9, bodies).hex())
    lines.append("events\t" + pack_events(1, 9, 17).hex())
    return lines

def picks_vectors():
    lines = ["kind\ta\tb\tc\tresult"]
    for l in (0.0, 0.25, 0.5, 1.0, 1.5, -0.5):
        lines.append("night\t%r\t\t\t%r" % (l, night_factor(l)))
    for v in (0.0, 3.9, 4.0, 4.01, 12.0, 100.0, 100.5, 3088.0):
        lines.append("scare\t%r\t\t\t%d" % (v, 1 if scares(v) else 0))
    for n in ("Sonar", "my SONAR antenna", "Battery", "", "son ar"):
        lines.append("sonar\t%s\t\t\t%d" % (n, 1 if is_sonar_name(n) else 0))
    for name, steps in POWER_SCENARIOS:
        p = PowerSwitch()
        for st in steps:
            if st[0] == "B":
                _b, grid, on, pw = st
                p.baseline(grid, bool(on), bool(pw))
                lines.append("power\t%s B\t%d\t%d/%d\t0" % (name, grid, on, pw))
            else:
                grid, on, pw = st
                lines.append("power\t%s\t%d\t%d/%d\t%d" % (name, grid, on, pw, 1 if p.step(grid, bool(on), bool(pw)) else 0))
    for t, above in ((0.0, 0.6), (1.0, 0.6), (0.5, 0.6), (0.0, 0.0), (0.25, 1.0), (-1.0, 0.6), (2.0, 0.6), (0.0, 1.5)):
        lines.append("group\t%r\t%r\t\t%r" % (t, above, group_target(t, above)))
    for user, level in ((1.0, 0.6), (0.8, 0.6), (0.0, 1.0), (1.2, 0.5), (0.5, -1.0)):
        lines.append("vca\t%r\t%r\t\t%r" % (user, level, vca_volume(user, level)))
    return lines

VECTORS = {"gate.tsv": gate_vectors, "aboard.tsv": aboard_vectors, "sky.tsv": sky_vectors, "light.tsv": light_vectors,
           "palette.tsv": palette_vectors, "wire.tsv": wire_vectors, "picks.tsv": picks_vectors}

def write_vectors():
    os.makedirs(VEC_DIR, exist_ok=True)
    for name, fn in VECTORS.items():
        with open(os.path.join(VEC_DIR, name), "w", encoding="ascii", newline="\n") as f:
            f.write("\n".join(fn()) + "\n")
        print("wrote %s" % os.path.join(VEC_DIR, name))

def vectors_current():
    bad = []
    for name, fn in VECTORS.items():
        p = os.path.join(VEC_DIR, name)
        if not os.path.isfile(p):
            bad.append(name + " missing")
            continue
        with open(p, encoding="ascii") as f:
            if f.read() != "\n".join(fn()) + "\n":
                bad.append(name + " stale")
    return bad

def contract_block():
    """The fenced canonical text after "S15 conventions" in CONTRACT §5.4, or None."""
    if not os.path.isfile(CONTRACT):
        return None
    with open(CONTRACT, encoding="utf-8") as f:
        t = f.read()
    i = t.find("**S15 conventions")
    if i < 0:
        return None
    j = t.find("```\n" + HEADER + "\n", i)
    if j < 0:
        return None
    k = t.find("```", j + 4)
    return t[j + 4:k]

def tables():
    out = ["voice events (format voice.gate; key = the aboard grid; no once-per-session rule; skipped, never held, while SE's audio is live):"]
    out.append("  %-15s %-6s %-9s %-46s %s" % ("event", "rearm", "cooldown", "condition (rising edge)", "Subnautica line"))
    for vid, name, rearm, cool, owner, member, src, prefab, when in VOICES:
        out.append("  %-15s %-6s %-9s %-46s %s.%s of the %s prefab (%s)" % (name, "%d s" % (rearm // 1000), "%d s" % (cool // 1000), when, owner, member, prefab, src))
    out.append("  (S16 keeps \"Hull integrity critical\", event:/sub/cyclops/AI_hull_crit, unchanged)")
    out.append("audio groups across the surface (format audio.groups; the player's slider value x the level; VCAs %s):" % VCA["source"])
    out.append("  effects/ambience: 1 under water, 0 above (the S2 rule, C5 crossfadeMs); music %.1f and voice %.1f above water, 1 under, over %d ms"
               % (MX["MusicAbovePermille"] / 1000.0, MX["VoiceAbovePermille"] / 1000.0, MX["CrossfadeMs"]))
    out.append("colour targets (sRGB, tolerance %d per channel, %d luma):" % (CO["TolChannel"], CO["TolLuma"]))
    out.append("  band (SE at band + %d m vs Subnautica at band - %d m): the four compare.py bands, pairwise" % (CO["BandOffsetM"], CO["BandOffsetM"]))
    for km, c, l in ORBIT:
        out.append("  orbit %d km: disc centre %s, limb %s" % (km, c, l))
    out.append("  water material %s: texture mean %s (generated)" % (WATER["material"], WATER["mean"]))
    out.append("sky bodies (palette by subtype prefix; angle <= %.1f deg, size within %d %%):" % (OR["AngleCentiDeg"] / 100.0, OR["SizePermille"] // 10))
    for pid, prefix, rgb, limb in PALETTES:
        out.append("  %d %-9s rgb %-15s limb %s" % (pid, prefix, rgb, limb))
    return "\n".join(out)

# =====================================================================================================================
# self-test
# =====================================================================================================================
def selftest():
    res = []

    def check(c, what):
        res.append(bool(c))
        print("  %s %s" % ("PASS" if c else "FAIL", what))

    t = canonical_text()
    h = fnv1a64(t)
    check(h == PINNED_HASH and t.count("\n") == CANON_LINES and len(t) == CANON_BYTES,
          "pin %s (%d lines, %d bytes) is 0x%016x" % (hex(h), t.count("\n"), len(t), PINNED_HASH))
    cb = contract_block()
    check(cb == t, "CONTRACT §5.4 S15 block equals the canonical text" + ("" if cb is not None else " (block missing)"))

    g = Gate()
    check(g.step(WELCOME, 1, 1, 0) == PLAYED and g.step(WELCOME, 1, 0, 500) == NONE, "welcome on boarding: played")
    check(g.step(WELCOME, 1, 1, 2000) == NONE, "back within 5 s: no new event (a step out)")
    g.step(WELCOME, 1, 0, 2500)
    check(g.step(WELCOME, 1, 1, 9000) == COOLDOWN, "back after 6.5 s within S16VoiceRule's 120 s: cooldown")
    g.step(WELCOME, 1, 0, 9500)
    check(g.step(WELCOME, 1, 1, 130000) == PLAYED, "after the cooldown: played again")
    g = Gate()
    check(g.step(POWER_UP, 2, 1, 0) == PLAYED and g.step(POWER_UP, 2, 0, 3000) == NONE and g.step(POWER_UP, 2, 1, 8000) == COOLDOWN,
          "engine powering up: a second transition within 10 s is the cooldown")
    g.step(POWER_UP, 2, 0, 9000)
    check(g.step(POWER_UP, 2, 1, 12000) == PLAYED, "engine powering up: no once-per-session rule (again 12 s later)")
    g = Gate()
    check(g.step(SMALL_WELCOME, 8, 1, 0, live=False) == SEAUDIO and g.step(SMALL_WELCOME, 8, 1, 5000, live=False) == NONE and
          g.step(SMALL_WELCOME, 8, 1, 9000, live=True) == NONE and not g.pending(),
          "owner 2026-10-24: boarded at the surface (SE's audio live), then the dive: nothing, ever (skipped, not held)")
    g = Gate()
    check(g.step(WELCOME, 8, 1, 0, live=False, hold=True) == SEAUDIO and g.step(WELCOME, 8, 1, 9000, live=True, hold=True) == PLAYED,
          "S15.M17 (the old held edge): the welcome plays after the dive (the owner's complaint)")
    g = Gate()
    g.baseline(POWER_UP, 9, True, 0)
    check(g.step(POWER_UP, 9, 1, 167) == NONE and g.step(POWER_UP, 9, 0, 1000) == NONE and g.step(POWER_UP, 9, 1, 4000) == PLAYED,
          "boarding a powered grid is the baseline; the next power edge plays")
    check(not sn_audio_live(0.0) and sn_audio_live(1.0) and sn_audio_live(0.5) and not sn_audio_live(0.49), "the S2 rule's threshold")
    g = Gate()
    check(all(g.step(WELCOME, 5, c, ms, live=bool(c), no_rules=True) in (PLAYED, NONE) for c, ms in ((1, 0), (0, 10), (1, 20))) and
          g.step(WELCOME, 5, 0, 30, no_rules=True) == NONE and g.step(WELCOME, 5, 1, 40, live=False, no_rules=True) == PLAYED, "S15.M1: every rising edge sent")
    # the power switch
    p = PowerSwitch()
    p.baseline(5, True, True)
    check(not p.step(5, True, True) and p.step(5, False, False) and p.step(5, False, False) and not p.step(5, True, True),
          "Y off from a powered grid: EnginePowerDown's condition; Y on clears it")
    p = PowerSwitch()
    p.baseline(5, True, False)
    check(not p.step(5, False, False), "Y off from an unpowered grid: nothing to shut down")
    p = PowerSwitch()
    p.baseline(6, False, False)
    check(not p.step(6, False, False), "boarded with the switch already off: the baseline")
    # aboard
    a = AboardHold()
    check([a.step(r, ms) for r, ms in ((7, 0), (7, 900), (7, 1000), (0, 1100), (7, 1200), (7, 1900), (7, 2200))] == [0, 0, 7, 0, 0, 0, 7],
          "aboard counts after 1 s, any gap restarts the hold")
    # the audio groups
    check(abs(group_target(0.0, 0.6) - 0.6) < 1e-12 and group_target(1.0, 0.6) == 1.0 and abs(group_target(0.5, 0.6) - 0.8) < 1e-12,
          "audio.groups: music and voice 0.6 above water, 1 under, linear between")
    check(abs(vca_volume(0.8, 0.6) - 0.48) < 1e-12 and vca_volume(1.2, 0.5) == 0.5, "the VCA gets the player's slider value x the level")
    # codecs
    c5 = pack_c5(3, 7, 1, 2, 3, 4, -9)
    check(len(c5) == C5D["Size"] and unpack_c5(c5)["seq"] == {1: 1, 2: 2, 3: 3, 4: 4} and unpack_c5(c5)["grid"] == -9 and c5[0] == 2,
          "C5 block 32 bytes, version 2, round trip")
    check(C5D["Offset"] >= 0xAA and C5D["Offset"] + C5D["Size"] <= 0x100 and C5D["Offset"] % 4 == 0, "C5 block after S16's 0x80-0xA9, inside pad_80")
    evb = pack_events(1, 9, 17)
    check(len(evb) == EV["Size"] and unpack_events(evb) == {"flags": 1, "count": 0, "epoch": 9, "sonarSeq": 17} and not any(evb[0x10:]),
          "the events block, version 2: the sonar only")
    fr = pack_frame(1, 2, 3, 4, 5, (1.0, 2.0, 3.0), [0.0] * 9)
    check(len(fr) == SK["FrameSize"] - 4, "frame %d bytes" % (len(fr) + 4))
    check(SK["ListOffset"] + SK["ListHeader"] + SK["MaxBodies"] * SK["RowSize"] <= 0x1000 and SK["FrameOffset"] + SK["FrameSize"] <= SK["ListOffset"]
          and SK["FrameOffset"] >= 0x40, "frame and list inside C6's 4 KiB region past the 64-byte SeTime")
    lst = pack_list(1, 1, [((0, 0, 0), 1.0, 0.0, 0, 0, "x" * 40)])
    check(len(lst) == SK["ListHeader"] + SK["MaxBodies"] * SK["RowSize"] - 4 and lst[0x20 - 4 + 0x23] == SK["NameMax"], "list rows; a long name cut to 28")
    # the sky math
    site = sv.SITE_V1
    P = site.sn_to_se((0.0, 150.0, 0.0))
    up_body = wm.add(site.C, wm.mul(wm.unit(wm.sub(P, site.C)), 1e6))
    v = sky_view(site, P, up_body, 10000.0)
    check(v is not None and angle_deg(v[0], (0.0, 1.0, 0.0)) < 1e-6, "a body straight above the camera is SN up")
    check(abs(v[1] - math.asin(10000.0 / v[2])) < 1e-15, "angular radius asin(R / d)")
    m = frame_rows(site, P)
    check(abs(m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]) + 1.0) < 1e-9,
          "mapdir's determinant is -1 (the handedness change)")
    check(sky_view(site, P, site.C, 61000.0) is None and sky_view(site, P, site.C, 60000.0) is not None, "a camera inside a body's sphere: no disc; 150 m above the sea: the bound planet seen")
    # light and palettes
    tb = (0.0, 0.0, 1.0)
    check(abs(light(0, 0, tb, (1.0, 0, 0), (0, 1.0, 0), (0, 0, -1.0)) - 1.0) < 1e-12, "the disc centre facing a sun behind the camera is full")
    check(light(0, 0, tb, (1.0, 0, 0), (0, 1.0, 0), (0, 0, 1.0)) == SK["AmbientPermille"] / 1000.0, "the night side's floor")
    check(limb_weight(0.5) == 0.0 and limb_weight(1.0) == 1.0 and 0 < limb_weight(0.92) < 1, "the limb weight")
    check(palette_of("DE_4546B") == 1 and palette_of("MoonTutorial") == 3 and palette_of("Venus") == 0 and palette_of("") == 0, "palettes by prefix")
    check(classify((36, 84, 125)) == "sea" and classify((107, 110, 124)) == "seabed" and classify((181, 170, 162)) == "seabed" and
          classify((230, 235, 240)) == "cloud", "the owner's 30-40 km looks: opaque JWater2 sea, the see-through seabed (grey, beige), clouds")
    check(colour_within((58, 97, 122), SN_NEAR_WATER)[0] and not colour_within((36, 84, 125), SN_NEAR_WATER)[0],
          "our water predicted from the owner's 40 km JWater2 capture is within T5 of the near water, JWater2 itself is not (dMax 29)")
    sea = ENDING["sea"]
    check(sea_ok((58, 97, 122), sea)[0] and sea_ok(SN_NEAR_WATER, sea)[0] and sea_ok(sea, sea)[0] and not sea_ok((36, 84, 125), sea)[0] and
          not sea_ok((65, 95, 118), sea)[0] and not sea_ok((107, 110, 124), sea)[0] and not sea_ok((20, 35, 45), sea)[0],
          "the sea's hue rule: ours predicted, the near water and the ending pass; JWater2, our first texture, the grey seabed, too dark fail")
    check(abs(night_factor(0.0) - 0.4) < 1e-12 and night_factor(1.0) == 1.0 and is_sonar_name("Antenna SONAR") and not is_sonar_name("Antenna"),
          "the night factor and the sonar name")
    check(colour_within((65, 97, 122), SN_NEAR_WATER)[0] and not colour_within((65, 130, 122), SN_NEAR_WATER)[0], "the colour tolerance")
    # run 20261024-063156's band pair: SE's far water at its horizon passes, the near texture's teal (72, 110, 112) and black fail
    far, hor = (106.3, 135.4, 171.0), (156.9, 207.6, 247.1)
    h1, h2, h3 = haze_line((163.8, 204.8, 238.5), far, hor), haze_line((72, 110, 112), far, hor), haze_line((10, 12, 14), far, hor)
    check(h1[0] and round(h1[1], 1) == 8.8 and abs(h1[3] - 0.963) < 0.001 and not h2[0] and not h3[0],
          "the haze line (band.far): SE's far water dMax %.1f dL %.1f at t %.3f; teal dMax %.0f, black dMax %.0f" % (h1[1], h1[2], h1[3], h2[1], h3[1]))
    bad = vectors_current()
    check(not bad, "vectors current" + (": " + ", ".join(bad) if bad else ""))
    ok = all(res)
    print("S15 REF SELFTEST %s (%d/%d)" % ("PASS" if ok else "FAIL", sum(res), len(res)))
    return 0 if ok else 1

def main(argv):
    if "--text" in argv:
        sys.stdout.write(canonical_text())
        return 0
    if "--pin" in argv:
        t = canonical_text()
        print("0x%016x %d lines %d bytes" % (fnv1a64(t), t.count("\n"), len(t)))
        return 0
    if "--vectors" in argv:
        write_vectors()
        return 0
    if "--tables" in argv:
        print(tables())
        return 0
    if "--selftest" in argv:
        return selftest()
    print(__doc__)
    return 2

if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
