"""S9: derives the release's template world from the project's lab save, read-only.

 python -I tools/release/make_template_world.py [--source "<Saves>\\<id>\\DE Lab S4"] [--vx2 FILE] [--out DIR]
 python -I tools/release/make_template_world.py --check [--out DIR] (verifies an existing template)

What a DeepEngineers world needs:
 * Sandbox_config.sbc AND Sandbox.sbc: SE loads the settings and the mod list from Sandbox_config.sbc when it exists
 (MyLocalCache.LoadCheckpoint overwrites the checkpoint's Settings and Mods with it), so every setting is written to
 both. (The lab save shows why: its S7 sun edit went into Sandbox.sbc only, and its config file still says a
 120-minute day.)
 * the mods: Water Mod 2200451495, Text HUD API 758597413 (a dependency), the local DeepEngineers.World;
 * the settings of docs/worldmod/DE_LAB_WORLD.md section 2 (Creative, no asteroids, oxygen and airtightness on,
 destructible blocks, scripts and copy/paste on, safe meteors, no weather, economy, cargo ships, drones, planetary
 encounters, spiders or wolves) and CONTRACT section 3's 20-minute day (EnableSunRotation, SunRotationIntervalMinutes
 20); the sector Environment's sun (azimuth pi, elevation 0: base direction (0, 0, 1)) and the sector weather's
 BaseSunDirection (0, 0, 1) are kept from the lab save;
 * the planet entity DE_4546B (StorageName DE_4546B-4546d120000, seed 4546, Radius 60000, AtmosphereRadius 105000,
 MinimumSurfaceRadius 57000, MaximumHillRadius 67200, min corner (-1131072.5, 118927.5, 1368927.5) so the centre is
 the site centre C = (-1000000, 250000, 1500000)), copied as SE wrote it. Its AtmosphereSettings element is not used
 at load (MyPlanet.Init takes them from the generator definition, i.e. the world mod);
 * the planet's voxel storage file: MyPlanet.Init throws "No storage loaded for planet" without
 <StorageName>.vx2 (MyStorageBase.LoadFromFile returns null for a missing file), so it cannot be regenerated from the
 definition and seed alone. The template carries the pristine copy SE wrote at the spawn (882 bytes unpacked: the
 voxel material name table, the planet generator's name, seed and radius, an empty octree; no edit), identified by
 its SHA-256 below and found in the lab saves or their backups;
 * the Water Mod's sea for that planet: the "JWater2" variable of ScriptManagerData in Sandbox.sbc (an escaped
 WaterModComponentObjectBuilder, keyed by the planet's EntityId), kept for DE_4546B only;
 * one player identity, its player entry and its character, so that a stranger's game takes the saved player over
 (MySession: TryFindSavingPlayerId finds the identity whose CharacterEntityId is the ControlledObject, and
 MyPlayerCollection.LoadConnectedPlayers / LoadControlledEntities map that player to the local Steam user when the
 local user is not in AllPlayersData). The template player's id is a placeholder (ClientId 1, no hashed Steam id),
 its name "Engineer" (SE renames it to the Steam name at load).
What is removed: every grid (38), proxy antenna, inventory bag and the six other planets with their seas, weather
records and marked areas; the project's GPS points, coordinate systems, colour slots and name; the cockpit seat (the
character is a free entity hovering on its jetpack 50 m above the sea over the dive-site anchor, facing site north,
docs/worldmod/DE_LAB_WORLD.md section 5); the binary sector cache (SANDBOX_0_0_0_.sbsB5, written by the game), so the
first load shows SE's "convert this world from XML" box (MySession.LoadWorld -> MyLoadingNeedXMLException,
MyGuiScreenLoading's LoadingNeedsXML message): click OK; the thumbnail and SE's Backup folder.
The output is text edits of SE's own XML (never re-serialised; the xsd prefix inside JWater2's xsi:type must stay
declared), written with CRLF line ends. --check re-reads the template and asserts all of the above.
"""
from __future__ import annotations

import argparse
import glob
import hashlib
import json
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
DEFAULT_OUT = os.path.join(HERE, "world-template", "DeepEngineers")
SOURCE_NAME = "DE Lab S4"
STORAGE = "DE_4546B-4546d120000"
VX2 = STORAGE + ".vx2"
PRISTINE_VX2_SHA256 = "f190f4527b3ad912e4de5daa7bc26c521c3a7297b723ec9690a2f696fc370e8d"
PLANET_ID = "-2652213890903021152"
PLANET_MIN_CORNER = ("-1131072.5", "118927.5", "1368927.5")
CENTRE = (-1000000.0, 250000.0, 1500000.0)
CHAR_ID = "103753901892655110"
PLAYER_IDENTITY = "144115188075855883"
TEMPLATE_CLIENT_ID = "1"
TEMPLATE_NAME = "DeepEngineers"
PLAYER_NAME = "Engineer"
CHAR_POS = (-1000000.0, 250000.0, 1560050.0)        # 50 m above the sea over the anchor (DE_LAB_WORLD section 5)
CHAR_FORWARD = (0.0, -1.0, 0.0)                      # site north N0 (CONTRACT section 3 anchor)
CHAR_UP = (0.0, 0.0, 1.0)                            # U0, away from the planet centre
SPECTATOR_POS = (-1000000.0, 250000.0, 1560060.0)
ANCHOR_GPS = ("DeepEngineers dive site", "SN (0, 0, 0): the sea surface over the dive site",
              ("-1000000", "250000", "1560000.00390625"))
TEMPLATE_WORLD_ID = "6e1e5c3a-0d8f-4b52-9a51-454600000001"
DESCRIPTION = ("DeepEngineers dive world: the ocean planet DE_4546B, where Subnautica draws the sea. Needs the "
               "DeepEngineers plugin, Water Mod, Text HUD API and the DeepEngineers.World mod; never open it without "
               "them.")
MODS = ("2200451495", "758597413", "DeepEngineers.World")
SETTINGS = (
    ("GameMode", "Creative"), ("OnlineMode", "OFFLINE"), ("ProceduralDensity", "0"), ("EnableOxygen", "true"),
    ("EnableOxygenPressurization", "true"), ("DestructibleBlocks", "true"), ("EnableIngameScripts", "true"),
    ("EnableCopyPaste", "true"), ("EnvironmentHostility", "SAFE"), ("WeatherSystem", "false"),
    ("EnableEconomy", "false"), ("CargoShipsEnabled", "false"), ("EnableDrones", "false"),
    ("EnablePlanetaryEncounters", "false"), ("EnableEncounters", "false"), ("EnableSpiders", "false"),
    ("EnableWolfs", "false"), ("EnableSunRotation", "true"), ("SunRotationIntervalMinutes", "20"),
)
OWNER_MARKERS = tuple(x for x in os.environ.get("DEEPENG_OWNER_MARKERS", "").split(",") if x)   # the world author's own ids, never in the template

# ----------------------------------------------------------------------------------------------------------------
# small text helpers (SE's XML edited in place)
# ----------------------------------------------------------------------------------------------------------------

def span(text: str, open_re: str, close: str, start: int = 0):
    m = re.compile(open_re).search(text, start)
    if not m:
        raise ValueError("not found: " + open_re)
    end = text.find(close, m.end())
    if end < 0:
        raise ValueError("no %s after %s" % (close, open_re))
    return m.start(), end + len(close)

def replace_span(text: str, open_re: str, close: str, new: str, start: int = 0) -> str:
    a, b = span(text, open_re, close, start)
    return text[:a] + new + text[b:]

def set_scalar(text: str, tag: str, value: str, within=None, required=True) -> str:
    lo, hi = within if within else (0, len(text))
    pat = re.compile(r"<%s>[^<]*</%s>" % (tag, tag))
    m = pat.search(text, lo, hi)
    if not m:
        if required:
            raise ValueError("no <%s> in the range" % tag)
        return text
    return text[:m.start()] + "<%s>%s</%s>" % (tag, value, tag) + text[m.end():]

def settings_range(text: str):
    a, b = span(text, r"<Settings[ >]", "</Settings>")
    return a, b

def apply_settings(text: str) -> str:
    for tag, value in SETTINGS:
        a, b = settings_range(text)
        text = set_scalar(text, tag, value, (a, b))
    return text

def indent_block(block: str, indent: int) -> str:
    lines = block.split("\n")
    first = len(lines[0]) - len(lines[0].lstrip(" "))
    out = []
    for i, ln in enumerate(lines):
        lead = len(ln) - len(ln.lstrip(" "))
        cut = min(lead, first) if i else lead
        out.append(" " * indent + ln[cut:])
    return "\n".join(out)

def fmt(v: float) -> str:
    s = repr(float(v))
    return s[:-2] if s.endswith(".0") else s

def quat_from_forward_up(f, u):
    """VRage Quaternion.CreateFromForwardUp: the rotation matrix with Right = F x U, Up = U, Backward = -F (row
 vectors), converted with the XNA rule. (0, -1, 0) / (0, 0, 1) gives (0, 0.7071, 0.7071, 0)."""
    r = (f[1] * u[2] - f[2] * u[1], f[2] * u[0] - f[0] * u[2], f[0] * u[1] - f[1] * u[0])
    m = (r, u, (-f[0], -f[1], -f[2]))
    m11, m12, m13 = m[0]
    m21, m22, m23 = m[1]
    m31, m32, m33 = m[2]
    tr = m11 + m22 + m33
    if tr > 0:
        s = math.sqrt(tr + 1.0)
        w = s * 0.5
        s = 0.5 / s
        return ((m23 - m32) * s, (m31 - m13) * s, (m12 - m21) * s, w)
    if m11 >= m22 and m11 >= m33:
        s = math.sqrt(1.0 + m11 - m22 - m33)
        t = 0.5 / s
        return (0.5 * s, (m12 + m21) * t, (m13 + m31) * t, (m23 - m32) * t)
    if m22 > m33:
        s = math.sqrt(1.0 + m22 - m11 - m33)
        t = 0.5 / s
        return ((m21 + m12) * t, 0.5 * s, (m32 + m23) * t, (m31 - m13) * t)
    s = math.sqrt(1.0 + m33 - m11 - m22)
    t = 0.5 / s
    return ((m31 + m13) * t, (m32 + m23) * t, 0.5 * s, (m12 - m21) * t)

def rotate(q, v):
    x, y, z, w = q
    # v' = v + 2w (q x v) + 2 q x (q x v)
    cx, cy, cz = y * v[2] - z * v[1], z * v[0] - x * v[2], x * v[1] - y * v[0]
    c2x, c2y, c2z = y * cz - z * cy, z * cx - x * cz, x * cy - y * cx
    return (v[0] + 2 * (w * cx + c2x), v[1] + 2 * (w * cy + c2y), v[2] + 2 * (w * cz + c2z))

def pos_block(indent: int, pos, fwd, up) -> str:
    q = quat_from_forward_up(fwd, up)
    pad = " " * indent
    qs = ["%.8f" % c for c in q]
    qs = ["0" if abs(float(c)) < 1e-12 else c.rstrip("0").rstrip(".") for c in qs]
    return ("{p}<PositionAndOrientation>\n{p}  <Position x=\"{x}\" y=\"{y}\" z=\"{z}\" />\n"
            "{p}  <Forward x=\"{fx}\" y=\"{fy}\" z=\"{fz}\" />\n{p}  <Up x=\"{ux}\" y=\"{uy}\" z=\"{uz}\" />\n"
            "{p}  <Orientation>\n{p}    <X>{qx}</X>\n{p}    <Y>{qy}</Y>\n{p}    <Z>{qz}</Z>\n{p}    <W>{qw}</W>\n"
            "{p}  </Orientation>\n{p}</PositionAndOrientation>").format(
        p=pad, x=fmt(pos[0]), y=fmt(pos[1]), z=fmt(pos[2]), fx=fmt(fwd[0]), fy=fmt(fwd[1]), fz=fmt(fwd[2]),
        ux=fmt(up[0]), uy=fmt(up[1]), uz=fmt(up[2]), qx=qs[0], qy=qs[1], qz=qs[2], qw=qs[3])

def xml_unescape(s: str) -> str:
    return s.replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", '"').replace("&apos;", "'").replace(
        "&amp;", "&")

def xml_escape_text(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

# ----------------------------------------------------------------------------------------------------------------
# the three world files
# ----------------------------------------------------------------------------------------------------------------

def sea_value(sbc: str) -> str:
    """The JWater2 value reduced to the DE_4546B sea, Timer and TideTimer reset."""
    m = re.search(r"(<Key>JWater2</Key>\s*<Value xsi:type=\"xsd:string\">)(.*?)(</Value>)", sbc, re.S)
    if not m:
        raise ValueError("no JWater2 (Water Mod sea) in ScriptManagerData")
    inner = xml_unescape(m.group(2))
    waters = re.findall(r"  <Waters>.*?</Waters>\n", inner, re.S)
    mine = [w for w in waters if "<EntityId>%s</EntityId>" % PLANET_ID in w]
    if len(mine) != 1:
        raise ValueError("expected one Waters entry for EntityId %s, found %d" % (PLANET_ID, len(mine)))
    w = re.sub(r"<Timer>[^<]*</Timer>", "<Timer>0</Timer>", mine[0])
    w = re.sub(r"<TideTimer>[^<]*</TideTimer>", "<TideTimer>0</TideTimer>", w)
    head = inner[:inner.find("  <Waters>")]
    tail = inner[inner.rfind("</Waters>\n") + len("</Waters>\n"):]
    new_inner = head + w + tail
    return sbc[:m.start(2)] + xml_escape_text(new_inner) + sbc[m.end(2):]

def make_sandbox(sbc: str) -> str:
    t = sbc
    t = set_scalar(t, "ElapsedGameTime", "0")
    t = set_scalar(t, "SessionName", TEMPLATE_NAME)
    a, b = span(t, r"<SpectatorPosition>", "</SpectatorPosition>")
    t = t[:a] + "<SpectatorPosition>\n" + pos_block(4, SPECTATOR_POS, CHAR_FORWARD, CHAR_UP).split(
        "<PositionAndOrientation>\n", 1)[1].rsplit("\n", 1)[0] + "\n  </SpectatorPosition>" + t[b:]
    t = set_scalar(t, "CameraController", "Entity")
    t = set_scalar(t, "CameraEntity", CHAR_ID)
    t = set_scalar(t, "ControlledObject", CHAR_ID)
    t = set_scalar(t, "Description", DESCRIPTION)
    t = replace_span(t, r"<ControlledEntities>", "</ControlledEntities>",
                     "<ControlledEntities>\n    <dictionary>\n      <item>\n        <Key>%s</Key>\n        <Value>\n"
                     "          <ClientId>%s</ClientId>\n          <SerialId>0</SerialId>\n"
                     "          <HashedId>0</HashedId>\n        </Value>\n      </item>\n    </dictionary>\n"
                     "  </ControlledEntities>" % (CHAR_ID, TEMPLATE_CLIENT_ID))
    t = apply_settings(t)
    t = sea_value(t)
    # the player identity: name only (its character id is CHAR_ID already)
    a, b = span(t, r"<MyObjectBuilder_Identity>\s*<IdentityId>%s</IdentityId>" % PLAYER_IDENTITY,
                "</MyObjectBuilder_Identity>")
    ident = t[a:b]
    if "<CharacterEntityId>%s</CharacterEntityId>" % CHAR_ID not in ident:
        raise ValueError("the player identity does not own character %s" % CHAR_ID)
    ident = set_scalar(ident, "DisplayName", PLAYER_NAME)
    t = t[:a] + ident + t[b:]
    # the player entry
    a, b = span(t, r"<AllPlayersData>", "</AllPlayersData>")
    apd = t[a:b]
    apd = re.sub(r"<Key>\s*<ClientId>[^<]*</ClientId>\s*<SerialId>0</SerialId>\s*<HashedId>[^<]*</HashedId>\s*</Key>",
                 "<Key>\n          <ClientId>%s</ClientId>\n          <SerialId>0</SerialId>\n"
                 "          <HashedId>0</HashedId>\n        </Key>" % TEMPLATE_CLIENT_ID, apd, count=1)
    if apd.count("<IdentityId>") != 1 or "<IdentityId>%s</IdentityId>" % PLAYER_IDENTITY not in apd:
        raise ValueError("AllPlayersData must hold exactly the player %s" % PLAYER_IDENTITY)
    apd = set_scalar(apd, "DisplayName", PLAYER_NAME)
    ca, cb = span(apd, r"<EntityCameraData>", "</EntityCameraData>")
    cams = re.findall(r"<CameraControllerSettings EntityId=\"%s\">.*?</CameraControllerSettings>" % CHAR_ID,
                      apd[ca:cb], re.S)
    apd = apd[:ca] + "<EntityCameraData>\n            " + "\n            ".join(cams) + "\n          </EntityCameraData>" \
        + apd[cb:]
    t = t[:a] + apd + t[b:]
    t = replace_span(t, r"<AllPlayersColors>", "</AllPlayersColors>",
                     "<AllPlayersColors>\n    <dictionary />\n  </AllPlayersColors>")
    # GPS: the dive site only
    a, b = span(t, r"<Gps>", "</Gps>")
    gps = t[a:b]
    entry = re.search(r"<Entry>.*?</Entry>", gps, re.S)
    if not entry:
        raise ValueError("no GPS entry to copy the format from")
    e = entry.group(0)
    name, desc, (x, y, z) = ANCHOR_GPS
    e = set_scalar(e, "name", name)
    e = set_scalar(e, "DisplayName", name)
    e = set_scalar(e, "description", desc)
    e = set_scalar(e, "X", x)
    e = set_scalar(e, "Y", y)
    e = set_scalar(e, "Z", z)
    t = t[:a] + ("<Gps>\n    <dictionary>\n      <item>\n        <Key>%s</Key>\n        <Value>\n          <Entries>\n"
                 "            %s\n          </Entries>\n        </Value>\n      </item>\n    </dictionary>\n  </Gps>" % (
                     PLAYER_IDENTITY, e)) + t[b:]
    # session components
    a, b = span(t, r'<MyObjectBuilder_SessionComponent xsi:type="MyObjectBuilder_SectorWeatherComponent">',
                "</MyObjectBuilder_SessionComponent>")
    weather = t[a:b]
    wa, wb = span(weather, r"<WeatherPlanetData>", "</WeatherPlanetData>")
    keep = re.findall(r"<MyObjectBuilder_WeatherPlanetData>\s*<PlanetId>%s</PlanetId>.*?"
                      r"</MyObjectBuilder_WeatherPlanetData>" % PLANET_ID, weather[wa:wb], re.S)
    weather = weather[:wa] + "<WeatherPlanetData>\n        " + "\n        ".join(keep) + "\n      </WeatherPlanetData>" \
        + weather[wb:]
    t = t[:a] + weather + t[b:]
    a, b = span(t, r'<MyObjectBuilder_SessionComponent xsi:type="MyObjectBuilder_CoordinateSystem">',
                "</MyObjectBuilder_SessionComponent>")
    cs = t[a:b]
    cs = set_scalar(cs, "LastCoordSysId", "0")
    cs = replace_span(cs, r"<CoordSystems>", "</CoordSystems>", "<CoordSystems />") if "<CoordSystems>" in cs else cs
    t = t[:a] + cs + t[b:]
    a, b = span(t, r'<MyObjectBuilder_SessionComponent xsi:type="MyObjectBuilder_WorldGenerator">',
                "</MyObjectBuilder_SessionComponent>")
    wg = t[a:b]
    areas = re.findall(r"<EmptyArea>.*?</EmptyArea>", wg, re.S)
    mine = [x for x in areas if "<X>-1000000</X>" in x and "<Y>250000</Y>" in x and "<Z>1500000</Z>" in x]
    if len(mine) != 1:
        raise ValueError("expected the DE_4546B marked area once, found %d" % len(mine))
    wg = replace_span(wg, r"<MarkedAreas>", "</MarkedAreas>",
                      "<MarkedAreas>\n        " + mine[0] + "\n      </MarkedAreas>")
    t = t[:a] + wg + t[b:]
    for tag in ("VicinityModelsCache", "VicinityArmorModelsCache"):
        if re.search(r"<%s>" % tag, t):
            t = replace_span(t, r"<%s>" % tag, "</%s>" % tag, "<%s />" % tag)
    t = set_scalar(t, "WorldId", TEMPLATE_WORLD_ID)
    return t

def make_config(cfg: str) -> str:
    t = apply_settings(cfg)
    return set_scalar(t, "SessionName", TEMPLATE_NAME)

def character_block(sbs: str) -> str:
    a, b = span(sbs, r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Character">', "</MyObjectBuilder_EntityBase>")
    line_start = sbs.rfind("\n", 0, a) + 1
    block = indent_block(sbs[line_start:b], 4)
    if "<EntityId>%s</EntityId>" % CHAR_ID not in block:
        raise ValueError("the first character is not %s" % CHAR_ID)
    block = replace_span(block, r"\n\s*<PositionAndOrientation>", "</PositionAndOrientation>",
                         "\n" + pos_block(6, CHAR_POS, CHAR_FORWARD, CHAR_UP))
    block = replace_span(block, r"<LocalPositionAndOrientation>", "</LocalPositionAndOrientation>",
                         '<LocalPositionAndOrientation xsi:nil="true" />')
    if "<AssetModifiers>" in block:
        block = replace_span(block, r"<AssetModifiers>", "</AssetModifiers>", "<AssetModifiers />")
    block = set_scalar(block, "DisplayName", PLAYER_NAME)
    block = set_scalar(block, "MovementState", "Flying")
    block = set_scalar(block, "JetpackEnabled", "true")
    block = re.sub(r"(<SubtypeName>HydrogenBottle</SubtypeName>\s*<GasLevel>)[^<]*(</GasLevel>)", r"\g<1>1\g<2>", block)
    block = set_scalar(block, "OwningPlayerIdentityId", PLAYER_IDENTITY)
    return block

def planet_block(sbs: str) -> str:
    pos = 0
    while True:
        a, b = span(sbs, r'<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Planet">',
                    "</MyObjectBuilder_EntityBase>", pos)
        blk = sbs[a:b]
        if "<StorageName>%s</StorageName>" % STORAGE in blk:
            line_start = sbs.rfind("\n", 0, a) + 1
            blk = indent_block(sbs[line_start:b], 4)
            if "<EntityId>%s</EntityId>" % PLANET_ID not in blk:
                raise ValueError("planet EntityId changed: %s expected" % PLANET_ID)
            mc = re.search(r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)" />', blk)
            if mc.groups() != PLANET_MIN_CORNER:
                raise ValueError("planet min corner %s, expected %s" % (mc.groups(), PLANET_MIN_CORNER))
            return blk
        pos = b

def make_sector(sbs: str) -> str:
    head_end = sbs.index("<SectorObjects>") + len("<SectorObjects>")
    return sbs[:head_end] + "\n" + planet_block(sbs) + "\n" + character_block(sbs) + \
        "\n  </SectorObjects>\n</MyObjectBuilder_Sector>\n"

# ----------------------------------------------------------------------------------------------------------------
# source discovery, writing, checking
# ----------------------------------------------------------------------------------------------------------------

def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()

def saves_roots():
    return sorted(glob.glob(os.path.join(os.environ.get("APPDATA", ""), "SpaceEngineers", "Saves", "*")))

def find_source():
    for root in saves_roots():
        p = os.path.join(root, SOURCE_NAME)
        if os.path.isfile(os.path.join(p, "Sandbox.sbc")):
            return p
    return None

def find_vx2(source: str):
    cands = [os.path.join(source, VX2)] + sorted(glob.glob(os.path.join(source, "Backup", "*", VX2)))
    for root in saves_roots():
        cands += sorted(glob.glob(os.path.join(root, "*", VX2))) + sorted(glob.glob(os.path.join(root, "*", "Backup", "*", VX2)))
    for c in cands:
        if os.path.isfile(c) and sha256(c) == PRISTINE_VX2_SHA256:
            return c
    return None

def read_text(path: str) -> str:
    with open(path, encoding="utf-8-sig") as f:
        return f.read()

def write_text(path: str, text: str):
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(text.replace("\r\n", "\n"))

def check(out: str) -> list:
    """Problems of a template folder (empty list = good). Used by --check and tests/release."""
    probs = []
    need = ("Sandbox.sbc", "Sandbox_config.sbc", "SANDBOX_0_0_0_.sbs", VX2)
    have = sorted(os.listdir(out)) if os.path.isdir(out) else []
    if sorted(need) != have:
        probs.append("files %s, expected exactly %s" % (have, sorted(need)))
        return probs
    if sha256(os.path.join(out, VX2)) != PRISTINE_VX2_SHA256:
        probs.append("%s is not the pristine planet storage" % VX2)
    sbc, cfg, sbs = (read_text(os.path.join(out, n)) for n in need[:3])
    import xml.etree.ElementTree as ET
    for n, t in (("Sandbox.sbc", sbc), ("Sandbox_config.sbc", cfg), ("SANDBOX_0_0_0_.sbs", sbs)):
        try:
            ET.fromstring(t.encode("utf-8"))
        except ET.ParseError as e:
            probs.append("%s is not well-formed XML: %s" % (n, e))
        for mark in OWNER_MARKERS:
            if mark in t:
                probs.append("%s still holds %r" % (n, mark))
        if 'xmlns:xsd="http://www.w3.org/2001/XMLSchema"' not in t[:400]:
            probs.append("%s lost the xsd namespace declaration" % n)
    for n, t in (("Sandbox.sbc", sbc), ("Sandbox_config.sbc", cfg)):
        a, b = settings_range(t)
        for tag, value in SETTINGS:
            m = re.search(r"<%s>([^<]*)</%s>" % (tag, tag), t[a:b])
            if not m or m.group(1) != value:
                probs.append("%s %s = %s, expected %s" % (n, tag, m.group(1) if m else None, value))
        for mod in MODS:
            if "<Name>%s" % mod not in t:
                probs.append("%s lacks the mod %s" % (n, mod))
        if "<SessionName>%s</SessionName>" % TEMPLATE_NAME not in t:
            probs.append("%s SessionName is not %s" % (n, TEMPLATE_NAME))
    if sbs.count('xsi:type="MyObjectBuilder_Planet"') != 1 or "<StorageName>%s</StorageName>" % STORAGE not in sbs:
        probs.append("the sector must hold exactly the planet %s" % STORAGE)
    if 'xsi:type="MyObjectBuilder_CubeGrid"' in sbs or "ProxyAntenna" in sbs:
        probs.append("the sector still holds grids or antennas")
    if sbs.count('xsi:type="MyObjectBuilder_Character"') != 1:
        probs.append("the sector must hold exactly one character")
    if '<Position x="%s" y="%s" z="%s" />' % PLANET_MIN_CORNER not in sbs:
        probs.append("the planet is not at the site min corner")
    if '<Position x="-1000000" y="250000" z="1560050" />' not in sbs:
        probs.append("the character is not 50 m above the anchor")
    if "<MovementState>Sitting</MovementState>" in sbs:
        probs.append("the character is still seated")
    if "<ControlledObject>%s</ControlledObject>" % CHAR_ID not in sbc:
        probs.append("ControlledObject is not the character")
    m = re.search(r"<Key>JWater2</Key>\s*<Value xsi:type=\"xsd:string\">(.*?)</Value>", sbc, re.S)
    inner = xml_unescape(m.group(1)) if m else ""
    if inner.count("<Waters>") != 1 or "<EntityId>%s</EntityId>" % PLANET_ID not in inner or \
            "<Radius>1.05263162</Radius>" not in inner or "<CrushDamage>0</CrushDamage>" not in inner:
        probs.append("the Water Mod sea is not the DE_4546B sea alone (radius 1.05263162, no crush)")
    return probs

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--source", default=None)
    ap.add_argument("--vx2", default=None)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--check", action="store_true")
    a = ap.parse_args(argv)
    if a.check:
        probs = check(a.out)
        for p in probs:
            print("FAIL", p)
        print("TEMPLATE %s %s" % ("PASS" if not probs else "FAIL", a.out))
        return 0 if not probs else 1
    source = a.source or find_source()
    if not source or not os.path.isfile(os.path.join(source, "Sandbox.sbc")):
        print("no source world (pass --source \"<Saves>\\<id>\\%s\")" % SOURCE_NAME)
        return 1
    vx2 = a.vx2 or find_vx2(source)
    if not vx2 or sha256(vx2) != PRISTINE_VX2_SHA256:
        print("no pristine %s (SHA-256 %s) found; pass --vx2" % (VX2, PRISTINE_VX2_SHA256))
        return 1
    files = {n: os.path.join(source, n) for n in ("Sandbox.sbc", "Sandbox_config.sbc", "SANDBOX_0_0_0_.sbs")}
    src_sha = {n: sha256(p) for n, p in files.items()}
    sbc = make_sandbox(read_text(files["Sandbox.sbc"]))
    cfg = make_config(read_text(files["Sandbox_config.sbc"]))
    sbs = make_sector(read_text(files["SANDBOX_0_0_0_.sbs"]))
    os.makedirs(a.out, exist_ok=True)
    for n in os.listdir(a.out):
        os.remove(os.path.join(a.out, n))
    write_text(os.path.join(a.out, "Sandbox.sbc"), sbc)
    write_text(os.path.join(a.out, "Sandbox_config.sbc"), cfg)
    write_text(os.path.join(a.out, "SANDBOX_0_0_0_.sbs"), sbs)
    with open(vx2, "rb") as f, open(os.path.join(a.out, VX2), "wb") as g:
        g.write(f.read())
    prov = {"generator": "tools/release/make_template_world.py", "source": os.path.basename(source),
            "sourceSha256": src_sha, "vx2From": os.path.relpath(vx2, os.path.dirname(source)),
            "vx2Sha256": PRISTINE_VX2_SHA256, "planet": {"entityId": PLANET_ID, "minCorner": PLANET_MIN_CORNER,
                                                           "centre": CENTRE},
            "character": {"entityId": CHAR_ID, "position": CHAR_POS, "forward": CHAR_FORWARD, "up": CHAR_UP,
                          "orientation": quat_from_forward_up(CHAR_FORWARD, CHAR_UP)},
            "settings": dict(SETTINGS), "mods": MODS,
            "files": {n: sha256(os.path.join(a.out, n)) for n in sorted(os.listdir(a.out))}}
    with open(os.path.join(os.path.dirname(a.out), "template.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(prov, f, indent=1, sort_keys=True)
    probs = check(a.out)
    for p in probs:
        print("FAIL", p)
    print("TEMPLATE %s %s (%s)" % ("PASS" if not probs else "FAIL", a.out, ", ".join(
        "%s %d bytes" % (n, os.path.getsize(os.path.join(a.out, n))) for n in sorted(os.listdir(a.out)))))
    return 0 if not probs else 1

if __name__ == "__main__":
    sys.exit(main())
