"""S15 part B checks of the world mod's own water material (CONTRACT §5.4 "S15 conventions", format water.material), called by
tools/worldmod/check_planet.py (`run`, S15 hook). Rows (Report ids):

 s15.material.parse Data/TransparentMaterials.sbc parses: <Definitions><TransparentMaterials><TransparentMaterial> x 1
 s15.material.values Id TransparentMaterialDefinition/DE_4546B_Water, Texture Textures\\DE_4546B_Water.dds, and the Water Mod
 3.23 JWater2 values (AlphaMistingEnable false, AlphaSaturation 1, CanBeAffectedByOtherLights false,
 SoftParticleDistanceScale 5, UseAtlas false, IsFlareOccluder true, Reflectivity 0)
 s15.material.known every element is a MyObjectBuilder_TransparentMaterialDefinition member (SE's serializer ignores others;
 tests/worldmod/DeepEngineers.WorldMod.Tests/S15MaterialTests.cs checks with SE's own serializer)
 s15.texture Textures/DE_4546B_Water.dds is the generator's (tools/s15/gen_water_texture.py: byte-identical, mean
 sRGB = the pin)
 s15.waterConfig Data/WaterConfig.
 opaque sea) and <WaterTextures> lists it
"""
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
MATERIAL = "DE_4546B_Water"
TEXTURE = "Textures\\DE_4546B_Water.dds"
FILE_REL = "Data/TransparentMaterials.sbc"
TEXTURE_REL = "Textures/DE_4546B_Water.dds"
SOFTNESS_OFFSET = "0.0001"   # S15Conventions.Water.DistantSoftnessOffset (the pin)
SPECULAR_INTENSITY = "0"     # S15Conventions.Water.SpecularIntensity (the pin): the Water Mod's terminator step off (the sea seam)
MATERIAL_FAR = "DE_4546B_WaterFar"
TEXTURE_FAR = "Textures\\DE_4546B_WaterFar.dds"
TEXTURE_FAR_REL = "Textures/DE_4546B_WaterFar.dds"
SEA_MATERIAL = "DE_4546B_Sea"   # the planet's Water Mod material, Reflectivity 0.5 (S15Conventions.WaterFar.SeaMaterial; run 20261024-063156)
SEA_VALUES = {"Density": "1024", "Viscosity": "0.05", "DrawSplashes": "true", "DrawBubbles": "true", "CollectedItem": "Ice", "CollectedAmount": "10",
              "MaxSurfaceFluctuation": "5", "SurfaceFluctuationAngleSpeed": "2", "SurfaceFluctuationSpeed": "0.5", "Reflectivity": "0.5",
              "UnderwaterReflectivity": "0.2", "Fresnel": "2", "MinFogDensity": "0.06", "FogDensityDepth": "0.00125", "FogDensityDepthScalar": "100",
              "InitialSunIntensity": "8"}
# the fields of MyObjectBuilder_TransparentMaterialDefinition (VRage.Game) and its base MyObjectBuilder_DefinitionBase
KNOWN = {"Id", "DisplayName", "Description", "Icon", "Public", "Enabled", "AvailableInSurvival", "DescriptionArgs", "DLC",
         "Texture", "GlossTexture", "TextureType", "CanBeAffectedByOtherLights", "AlphaMistingEnable", "UseAtlas", "AlphaMistingStart",
         "AlphaMistingEnd", "SoftParticleDistanceScale", "AlphaSaturation", "Color", "ColorAdd", "ShadowMultiplier", "LightMultiplier",
         "Reflectivity", "Fresnel", "ReflectionShadow", "Gloss", "GlossTextureAdd", "SpecularColorFactor", "IsFlareOccluder",
         "TriangleFaceCulling", "AlphaCutout", "TargetSize"}
VALUES = {"AlphaMistingEnable": "false", "AlphaSaturation": "1", "CanBeAffectedByOtherLights": "false", "SoftParticleDistanceScale": "5",
          "UseAtlas": "false", "IsFlareOccluder": "true", "Reflectivity": "0"}

def check(rep, mod):
    path = os.path.join(mod, FILE_REL)
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as e:
        rep.add("s15.material.parse", False, str(e))
        return
    items = root.findall("TransparentMaterials/TransparentMaterial")
    others = [c.tag for c in root if c.tag != "TransparentMaterials"]
    rep.add("s15.material.parse", root.tag == "Definitions" and len(items) == 2 and not others,
            "<%s>, %d TransparentMaterial%s (near, far)" % (root.tag, len(items), ", other roots %s" % others if others else ""))
    if len(items) != 2:
        return
    problems = []
    for m, (want_id, want_tex) in zip(items, ((MATERIAL, TEXTURE), (MATERIAL_FAR, TEXTURE_FAR))):
        tid = (m.findtext("Id/TypeId") or "").strip()
        sid = (m.findtext("Id/SubtypeId") or "").strip()
        if tid != "TransparentMaterialDefinition" or sid != want_id:
            problems.append("Id %s/%s" % (tid, sid))
        if (m.findtext("Texture") or "").strip() != want_tex:
            problems.append("%s Texture %r" % (sid, m.findtext("Texture")))
        for k, v in VALUES.items():
            got = (m.findtext(k) or "").strip().lower()
            if got != v:
                problems.append("%s %s %r (JWater2's %s)" % (sid, k, got, v))
    rep.add("s15.material.values", not problems, "%s and %s, JWater2's values" % (MATERIAL, MATERIAL_FAR) if not problems else "; ".join(problems))
    m = items[0]
    unknown = sorted({c.tag for c in m if c.tag not in KNOWN})
    rep.add("s15.material.known", not unknown, "every element a MyObjectBuilder_TransparentMaterialDefinition member" if not unknown else
            "ignored by SE's serializer: %s" % ", ".join(unknown))
    tex = os.path.join(mod, *TEXTURE_REL.split("/"))
    gen_dir = os.path.join(ROOT, "tools", "s15")
    if gen_dir not in sys.path:
        sys.path.insert(0, gen_dir)
    try:
        import gen_water_texture as G   # noqa: E402
        with open(tex, "rb") as f:
            have = f.read()
        same = have == G.dds_bytes()
        mean = G.top_mean(have)
        near = all(abs(a - b) <= G.MEAN_TOL for a, b in zip(mean, G.TARGET_MEAN_SRGB))
        with open(os.path.join(mod, *TEXTURE_FAR_REL.split("/")), "rb") as f:
            have_far = f.read()
        same_far = have_far == G.dds_bytes(G.FAR_MEAN_SRGB)
        rep.add("s15.texture", same and near and same_far, "%s %d bytes, the generator's %s, mean sRGB (%.1f, %.1f, %.1f) pin %s; %s the generator's %s" %
                (TEXTURE_REL, len(have), "yes" if same else "NO", mean[0], mean[1], mean[2], G.TARGET_MEAN_SRGB, TEXTURE_FAR_REL, "yes" if same_far else "NO"))
    except OSError as e:
        rep.add("s15.texture", False, "%s: %s (python -I tools/s15/gen_water_texture.py --write)" % (TEXTURE_REL, e))
    wc = os.path.join(mod, "Data", "WaterConfig.xml")
    try:
        w = ET.parse(wc).getroot()
    except (ET.ParseError, OSError) as e:
        rep.add("s15.waterConfig", False, str(e))
        return
    ours = [p for p in w.findall("PlanetConfigs/PlanetConfig") if p.get("SubtypeId") == "DE_4546B"]
    tex_set = ours and (ours[0].findtext("WaterSettings/Texture/String") or "").strip() == MATERIAL
    opaque = ours and (ours[0].findtext("WaterSettings/Transparent") or "").strip().lower() == "false"
    listed_all = [(t.text or "").strip() for t in w.findall("WaterTextures/Texture")]
    listed = MATERIAL in listed_all and MATERIAL_FAR in listed_all
    seas = [m for m in w.findall("MaterialConfigs/MaterialConfig") if m.get("SubtypeId") == SEA_MATERIAL]
    bad_sea = [k for k, v in SEA_VALUES.items() if not seas or (seas[0].findtext(k) or "").strip() != v]
    mat_id = (ours[0].findtext("WaterSettings/MaterialId") or "").strip() if ours else ""
    rep.add("s15.waterMaterial", len(seas) == 1 and not bad_sea and mat_id == SEA_MATERIAL,
            "MaterialConfig %s: the mod's Water values, Reflectivity 0.5%s; DE_4546B WaterSettings/MaterialId %s" %
            (SEA_MATERIAL, "" if seas and not bad_sea else " (MISSING or wrong: %s)" % bad_sea, mat_id or "MISSING"))
    soft = (ours[0].findtext("DistantSoftnessOffset") or "").strip() if ours else ""
    soft_ok = soft == SOFTNESS_OFFSET
    spec = (ours[0].findtext("SpecularIntensity") or "").strip() if ours else ""
    spec_ok = spec == SPECULAR_INTENSITY
    rep.add("s15.waterConfig", bool(tex_set and opaque and listed and soft_ok and spec_ok),
            "DE_4546B WaterSettings/Texture %s, Transparent %s, DistantSoftnessOffset %s, SpecularIntensity %s, WaterTextures %s" %
            ("= " + MATERIAL if tex_set else "MISSING", "false" if opaque else "NOT false", soft or "MISSING", spec or "MISSING", "lists it" if listed else "does NOT list it"))
