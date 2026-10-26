"""DeepEngineers key art: a deterministic composition of our own in-game captures (no generated imagery).

v2: the title and subtitle stay, nothing else; --top is the Space Engineers picture, --bottom the Subnautica one.

Top: Space Engineers' picture of planet 4546B from 30 km (the S8a look queue, orbit-30km.png). Bottom: Subnautica's
picture of the sea floor on that planet (look-09.png). A wavy waterline with foam joins them, the title sits on it, and
two small insets show the games as they are played: SE on the water at night with its HUD, and SE blocks in the
shallows seen from Subnautica (the S3 captures). Fonts are Windows' own (Bahnschrift, Segoe UI).

Usage: python tools/media/key_art.py [--out docs/media/deepengineers-key-art.png] [--width 1920]
"""
import argparse
import math
import os

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
LOOK = os.path.join(ROOT, 'tests', 'out', 's8a', '20261024-154142', 'look')
S3 = os.path.join(ROOT, 'tests', 'out', 's3', '20261016-185815', 'captures', '20261016-185815-shallow')
SE_SHOTS = os.path.join(os.environ.get('APPDATA', ''), 'SpaceEngineers', 'Screenshots')

SOURCES = {
    'space': os.path.join(LOOK, 'orbit-30km.png'),
    'sea': os.path.join(LOOK, 'look-09.png'),
    'se_night': os.path.join(SE_SHOTS, '2026-10-16-20-54-55-581.png'),
    'shallows': os.path.join(S3, 'P3_000_f9305.png'),
}

def font(name, size):
    for f in (name, 'bahnschrift.ttf', 'segoeuib.ttf', 'arialbd.ttf'):
        p = os.path.join(r'C:\Windows\Fonts', f)
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default()

def cover(im, w, h, anchor=(0.5, 0.5)):
    """Scale to cover w x h, then crop around the anchor (fractions of the overflow)."""
    s = max(w / im.width, h / im.height)
    im = im.resize((max(w, int(im.width * s + 0.5)), max(h, int(im.height * s + 0.5))), Image.LANCZOS)
    x = int((im.width - w) * anchor[0])
    y = int((im.height - h) * anchor[1])
    return im.crop((x, y, x + w, y + h))

def waterline_mask(w, h, y0, amp, waves, blur):
    """A mask: 255 above the wavy line (sea surface), 0 below; soft edge."""
    m = Image.new('L', (w, h), 0)
    d = ImageDraw.Draw(m)
    pts = [(0, 0)]
    for x in range(0, w + 1, 4):
        t = x / w
        y = y0 + amp * math.sin(t * waves * 2 * math.pi) + amp * 0.45 * math.sin(t * waves * 5.3 * math.pi + 1.3)
        pts.append((x, y))
    pts += [(w, 0)]
    d.polygon(pts, fill=255)
    return m.filter(ImageFilter.GaussianBlur(blur)) if blur else m

def rounded_inset(canvas, im, box, caption, fnt, radius=14, border=(210, 235, 245)):
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    tile = cover(im, w, h)
    mask = Image.new('L', (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, fill=255)
    shadow = Image.new('RGBA', (w + 40, h + 40), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle((20, 24, w + 20, h + 24), radius=radius + 4, fill=(0, 0, 0, 150))
    shadow = shadow.filter(ImageFilter.GaussianBlur(12))
    canvas.alpha_composite(shadow, (x0 - 20, y0 - 20))
    canvas.paste(tile, (x0, y0), mask)
    # the caption on a dark band inside the inset, the font shrunk until it fits the width
    band = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    bd = ImageDraw.Draw(band)
    bd.rounded_rectangle((0, 0, w - 1, h - 1), radius=radius, fill=(0, 0, 0, 0))
    bd.rectangle((0, h - 38, w, h), fill=(5, 15, 25, 165))
    band.putalpha(Image.composite(band.getchannel('A'), Image.new('L', (w, h), 0), mask))
    canvas.alpha_composite(band, (x0, y0))
    d = ImageDraw.Draw(canvas)
    d.rounded_rectangle((x0, y0, x1 - 1, y1 - 1), radius=radius, outline=border + (200,), width=2)
    size = fnt.size
    while size > 10 and fnt.getlength(caption) > w - 24:
        size -= 1
        fnt = fnt.font_variant(size=size)
    text_shadow(d, (x0 + 12, y1 - 29), caption, fnt, (235, 245, 250))

def text_shadow(d, xy, s, fnt, fill, shadow=(0, 0, 0), off=2, anchor='la'):
    x, y = xy
    for dx, dy in ((off, off), (-off, off), (off, -off), (-off, -off), (0, off), (off, 0)):
        d.text((x + dx, y + dy), s, font=fnt, fill=shadow + (170,), anchor=anchor)
    d.text(xy, s, font=fnt, fill=fill, anchor=anchor)

def gradient_word(word, fnt, top_rgb, bottom_rgb, stretch=1.0, outline=(10, 16, 24), outline_px=3):
    """A word filled with a vertical gradient (top to bottom colour), an outline, as an RGBA image; stretch widens it."""
    tmp = ImageDraw.Draw(Image.new('L', (1, 1)))
    x0, y0, x1, y1 = tmp.textbbox((0, 0), word, font=fnt)
    pad = outline_px + 6
    w, h = x1 - x0 + 2 * pad, y1 - y0 + 2 * pad
    mask = Image.new('L', (w, h), 0)
    ImageDraw.Draw(mask).text((pad - x0, pad - y0), word, font=fnt, fill=255)
    grad = Image.new('RGBA', (w, h))
    gp = grad.load()
    for y in range(h):
        t = min(1.0, max(0.0, (y - pad) / max(1, h - 2 * pad)))
        c = tuple(int(top_rgb[i] + (bottom_rgb[i] - top_rgb[i]) * t) for i in range(3)) + (255,)
        for x in range(w):
            gp[x, y] = c
    fill = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    fill.paste(grad, (0, 0), mask)
    out = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    od = ImageDraw.Draw(out)
    od.text((pad - x0, pad - y0), word, font=fnt, fill=outline + (255,), stroke_width=outline_px, stroke_fill=outline + (255,))
    out.alpha_composite(fill)
    if stretch != 1.0:
        out = out.resize((int(out.width * stretch), out.height), Image.LANCZOS)
    return out

def mixed_title(canvas, cx, baseline_y, size):
    """v6: DEEP in Subnautica's warm orange-to-yellow, ENGINEERS in Space
 Engineers' steel blue-grey, widened like its wordmark; both with the dark outline and a soft shadow."""
    f_deep = font('segoeuib.ttf', size)
    f_eng = font('bahnschrift.ttf', int(size * 0.92))
    deep = gradient_word('DEEP', f_deep, (255, 196, 70), (232, 108, 20))
    eng = gradient_word('ENGINEERS', f_eng, (236, 244, 250), (150, 176, 196), stretch=1.18)
    gap = int(size * 0.08)
    total = deep.width + gap + eng.width
    x0 = cx - total // 2
    y_deep = baseline_y - deep.height + int(size * 0.18)
    y_eng = baseline_y - eng.height + int(size * 0.18)
    for im, x, y in ((deep, x0, y_deep), (eng, x0 + deep.width + gap, y_eng)):
        sh = Image.new('RGBA', (im.width + 30, im.height + 30), (0, 0, 0, 0))
        sh.paste((0, 0, 0, 255), (15, 18), im.getchannel('A').point(lambda v: int(v * 0.75)))
        sh = sh.filter(ImageFilter.GaussianBlur(7))
        canvas.alpha_composite(sh, (x - 15, y - 15))
        canvas.alpha_composite(im, (x, y))
    return total

def compose_full(width, path, anchor=(0.5, 0.5), title_x=0.5, title_y=0.80, title_scale=0.8, logos=None, logo_h=0.055, mixed=False, tagline=None,
                 logos_x=None, logos_y=None, cross_scale=1.0):
    """v4: one capture, whole, with the
 title block on its empty area (the capsule's deck) and nothing else."""
    W, H = width, width * 9 // 16
    u = W / 1920.0
    canvas = cover(Image.open(path).convert('RGB'), W, H, anchor=anchor).convert('RGBA')
    d = ImageDraw.Draw(canvas)
    f_title = font('bahnschrift.ttf', int(128 * u * title_scale))
    f_sub = font('bahnschrift.ttf', int(40 * u * title_scale))
    f_tag = font('segoeuib.ttf', int(26 * u * title_scale))
    cx, ty = int(W * title_x), int(H * title_y)
    tag = tagline or 'An ocean planet in the Star System, rendered by Subnautica'
    if mixed:
        mixed_title(canvas, cx, ty, int(128 * u * title_scale))
    else:
        text_shadow(d, (cx, ty), 'DEEPENGINEERS', f_title, (245, 250, 252), off=int(3 * u), anchor='ms')
    if logos:
        # v5: the publishers' logos (docs/media/logos, Steam's
 # store copies) side by side at one height, a cross between them, each with a soft dark shadow so it reads on the deck
        se_path, sn_path = logos
        h = int(H * logo_h)
        se = Image.open(se_path).convert('RGBA'); sn = Image.open(sn_path).convert('RGBA')
        se = se.resize((int(se.width * h / se.height), h), Image.LANCZOS)
        sn_h = int(h * 0.62)  # the Subnautica wordmark is a wide strip: a little lower than the SE mark's cap height
        sn = sn.resize((int(sn.width * sn_h / sn.height), sn_h), Image.LANCZOS)
        gap = int(34 * u)
        cross = font('bahnschrift.ttf', int(34 * u * cross_scale))
        cw = int(cross.getlength('×'))
        total = se.width + gap + cw + gap + sn.width
        # v7: the logo line can sit apart from the title
        lx = int(W * logos_x) if logos_x is not None else cx
        x0 = lx - total // 2
        top = int(H * logos_y) if logos_y is not None else ty + int(18 * u * title_scale)
        cx = lx
        for im, x in ((se, x0), (sn, x0 + se.width + gap + cw + gap)):
            y = top + (h - im.height) // 2
            sh = Image.new('RGBA', (im.width + 24, im.height + 24), (0, 0, 0, 0))
            alpha = im.getchannel('A').point(lambda v: int(v * 0.7))
            sh.paste((0, 0, 0, 255), (12, 14), alpha)
            sh = sh.filter(ImageFilter.GaussianBlur(5 * u))
            canvas.alpha_composite(sh, (x - 12, y - 12))
            canvas.alpha_composite(im, (x, y))
        text_shadow(d, (x0 + se.width + gap + cw // 2, top + h // 2 + int(12 * u * cross_scale)), '×', cross, (225, 240, 245), off=int(2 * u), anchor='ms')
        text_shadow(d, (cx, top + h + int(36 * u * title_scale)), tag, f_tag, (225, 240, 245), off=int(2 * u), anchor='ms')
        return canvas.convert('RGB')
    text_shadow(d, (cx, ty + int(52 * u * title_scale)), 'SPACE  ENGINEERS   ×   SUBNAUTICA', f_sub, (205, 240, 250), off=int(2 * u), anchor='ms')
    text_shadow(d, (cx, ty + int(92 * u * title_scale)), tag, f_tag, (225, 240, 245), off=int(2 * u), anchor='ms')
    return canvas.convert('RGB')

def compose(width, top_path, bottom_path, top_anchor=(0.5, 0.5), bottom_anchor=(0.5, 1.0), top_gain=1.0, extras=False, line=0.40, title_x=0.5):
    W, H = width, width * 9 // 16
    u = W / 1920.0  # layout unit
    space = Image.open(top_path).convert('RGB')
    sea = Image.open(bottom_path).convert('RGB')
    y_line = int(H * line)  # v3: the seam can sit on the top picture's own horizon

 # the two pictures, each covering the whole canvas so the wavy seam can cut anywhere
    top = cover(space, W, H, anchor=top_anchor)
    if top_gain != 1.0:
        top = ImageEnhance.Brightness(top).enhance(top_gain)
    top = top.convert('RGBA')
    bottom = cover(sea, W, H, anchor=bottom_anchor).convert('RGBA')      # anchored at the bottom: a depth pill at the top is cropped away
 # the sea floor dims towards the seam (depth), space brightens a touch towards it (the planet's glow)
    grad = Image.new('L', (1, H))
    for y in range(H):
        t = (y - y_line) / max(1, H - y_line)
        grad.putpixel((0, y), int(255 * max(0.0, min(1.0, 0.55 + 0.45 * t))) if y >= y_line else 255)
    grad = grad.resize((W, H))
    dark = Image.new('RGBA', (W, H), (4, 22, 36, 255))
    bottom = Image.composite(bottom, dark, grad)

    mask = waterline_mask(W, H, y_line, amp=14 * u, waves=2.0, blur=1.5 * u)
    canvas = Image.composite(top, bottom, mask)

    # foam: the seam itself, a thin bright line with a soft glow under it
    foam = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    edge = mask.filter(ImageFilter.FIND_EDGES).point(lambda v: 255 if v > 40 else 0)
    glow = edge.filter(ImageFilter.GaussianBlur(10 * u)).point(lambda v: min(255, v * 3))
    foam.paste((170, 230, 245, 255), (0, 0), glow)
    line = edge.filter(ImageFilter.GaussianBlur(1.2 * u))
    foam.paste((235, 250, 255, 255), (0, 0), line)
    canvas.alpha_composite(foam)

    d = ImageDraw.Draw(canvas)
    # title block on the waterline
    f_title = font('bahnschrift.ttf', int(128 * u))
    f_sub = font('bahnschrift.ttf', int(40 * u))
    f_tag = font('segoeuib.ttf', int(26 * u))
    f_cap = font('segoeuib.ttf', int(20 * u))
    cx = int(W * title_x)  # v3: the title block can move off a subject in the top picture
    text_shadow(d, (cx, y_line - int(20 * u)), 'DEEPENGINEERS', f_title, (245, 250, 252), off=int(3 * u), anchor='ms')
    text_shadow(d, (cx, y_line + int(56 * u)), 'SPACE  ENGINEERS   \u00d7   SUBNAUTICA', f_sub, (205, 240, 250), off=int(2 * u), anchor='ms')
    text_shadow(d, (cx, y_line + int(100 * u)), 'An ocean planet in the Star System, rendered by Subnautica', f_tag, (225, 240, 245), off=int(2 * u), anchor='ms')

    if not extras:
        return canvas.convert('RGB')
    # corner labels: which game draws which half
    text_shadow(d, (int(36 * u), int(34 * u)), 'ABOVE  \u2014  SPACE ENGINEERS: PLANET 4546B FROM 30 KM', f_cap, (200, 220, 235))
    text_shadow(d, (int(36 * u), H - int(52 * u)), 'BELOW  \u2014  SUBNAUTICA: THE SEA FLOOR OF THE SAME PLANET', f_cap, (200, 230, 240))

    # insets: the games as played
    iw, ih = int(400 * u), int(225 * u)
    gap = int(22 * u)
    x1 = W - int(36 * u)
    y1 = H - int(70 * u)
    se_night = Image.open(SOURCES['se_night']).convert('RGB').crop((400, 400, 2300, 1440))   # the sea plane and SE's HUD
    se_night = ImageEnhance.Brightness(se_night).enhance(1.6)
    rounded_inset(canvas, se_night, (x1 - iw, y1 - ih, x1, y1), 'Space Engineers on the water, its HUD', f_cap)
    rounded_inset(canvas, Image.open(SOURCES['shallows']).convert('RGB'),
                  (x1 - 2 * iw - gap, y1 - ih, x1 - iw - gap, y1), 'SE blocks in the shallows, from Subnautica', f_cap)

    # footer: provenance
    f_foot = font('segoeuib.ttf', int(17 * u))
    text_shadow(d, (W - int(36 * u), H - int(26 * u)), 'Every frame captured in game from the DeepEngineers build. No generated imagery.',
                f_foot, (170, 200, 215), anchor='rs')
    return canvas.convert('RGB')

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default=os.path.join(ROOT, 'docs', 'media', 'deepengineers-key-art.png'))
    ap.add_argument('--width', type=int, default=1920)
    ap.add_argument('--top', default=SOURCES['se_night'], help='the Space Engineers picture (above the waterline)')
    ap.add_argument('--bottom', default=SOURCES['sea'], help='the Subnautica picture (below the waterline)')
    ap.add_argument('--top-anchor', default='0.5,0.35', help='crop anchor of the top picture, fractions x,y')
    ap.add_argument('--bottom-anchor', default='0.5,1.0')
    ap.add_argument('--top-gain', type=float, default=1.0, help='brightness factor of the top picture (a night shot: 1.5)')
    ap.add_argument('--extras', action='store_true', help='the v1 corner labels, insets and footer')
    ap.add_argument('--line', type=float, default=0.40, help='the waterline seam as a fraction of the height (0.585 puts it on the Aurora deck shot horizon)')
    ap.add_argument('--title-x', type=float, default=0.5, help='the title block centre as a fraction of the width')
    ap.add_argument('--full', action='store_true', help='one whole capture with the title block, no seam, no bottom picture')
    ap.add_argument('--title-y', type=float, default=0.80, help='--full: the title baseline as a fraction of the height')
    ap.add_argument('--title-scale', type=float, default=0.8, help='--full: the title block size relative to the split layout')
    ap.add_argument('--logos', action='store_true', help='--full: the publisher logos (docs/media/logos) instead of the game-names line')
    ap.add_argument('--logo-h', type=float, default=0.055, help='--full --logos: the Space Engineers mark height as a fraction of the height')
    ap.add_argument('--mixed', action='store_true', help='--full: the title in both logo styles (DEEP warm, ENGINEERS steel)')
    ap.add_argument('--tagline', default=None, help='the line under the logos (default: the ocean planet line)')
    ap.add_argument('--logos-x', type=float, default=None, help='--full --logos: the logo line centre as a fraction of the width (default: under the title)')
    ap.add_argument('--logos-y', type=float, default=None, help='--full --logos: the logo line top as a fraction of the height')
    ap.add_argument('--cross-scale', type=float, default=1.0, help='--full --logos: the size of the cross between the logos')
    a = ap.parse_args()
    for k, p in (('top', a.top), ('bottom', a.bottom)) + (tuple(SOURCES.items()) if a.extras else ()):
        if not os.path.exists(p):
            raise SystemExit('missing source %s: %s' % (k, p))
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    ta = tuple(float(v) for v in a.top_anchor.split(','))
    ba = tuple(float(v) for v in a.bottom_anchor.split(','))
    logos = (os.path.join(ROOT, 'docs', 'media', 'logos', 'space-engineers-logo.png'), os.path.join(ROOT, 'docs', 'media', 'logos', 'subnautica-logo.png')) if a.logos else None
    im = compose_full(a.width, a.top, ta, a.title_x, a.title_y, a.title_scale, logos, a.logo_h, a.mixed, a.tagline, a.logos_x, a.logos_y, a.cross_scale) if a.full else compose(a.width, a.top, a.bottom, ta, ba, a.top_gain, a.extras, a.line, a.title_x)
    im.save(a.out, optimize=True)
    print('wrote', a.out, im.size)

if __name__ == '__main__':
    main()
