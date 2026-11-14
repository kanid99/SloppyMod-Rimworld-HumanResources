#!/usr/bin/env python3
"""Draws the Steam Workshop page images (Workshop/*.png) and About/Preview.png, About/ModIcon.png.

Usage: python3 Source/Art/make_workshop.py <folder with the Archivo .ttf files>
Fonts: Archivo (github.com/Omnibus-Type/Archivo, fonts/ttf). They are not kept in this repository.
"""
import os, sys
from PIL import Image, ImageDraw, ImageFont

FONTS = sys.argv[1] if len(sys.argv) > 1 else "."
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
W = 1000
CYAN, WHITE, BODY = (62, 200, 220), (240, 237, 228), (200, 197, 190)
BG, TILE, EDGE = (30, 29, 27), (38, 37, 35), (58, 57, 54)
S1, S2 = (37, 36, 34), (41, 40, 38)

def font(name, size): return ImageFont.truetype(os.path.join(FONTS, name + ".ttf"), size)
def black(size): return font("Archivo-Black", size)
def heavy(size): return font("Archivo-ExtraBold", size)
def bold(size): return font("Archivo-Bold", size)
def regular(size): return font("Archivo-Regular", size)

def stripes(w, h):
    im = Image.new("RGB", (w, h), S1)
    d = ImageDraw.Draw(im)
    for x in range(-h, w + h, 20):
        d.polygon([(x, h), (x + 10, h), (x + 10 + h, 0), (x + h, 0)], fill=S2)
    return im

def spaced(d, xy, text, f, fill, gap):
    x, y = xy
    for ch in text:
        d.text((x, y), ch, font=f, fill=fill)
        x += d.textlength(ch, font=f) + gap

def banner(path, kicker, title, icon=None):
    im = stripes(W, 170)
    d = ImageDraw.Draw(im)
    spaced(d, (48, 40), kicker.upper(), bold(16), CYAN, 2.6)
    d.text((48, 62), title.upper(), font=black(54), fill=WHITE)
    d.rectangle([0, 164, W, 170], fill=CYAN)
    if icon is not None:
        ic = icon.resize((112, 112), Image.LANCZOS)
        im.paste(ic, (W - 48 - 112, 26), ic)
    im.save(path)

def wrap(d, text, f, width):
    lines, cur = [], ""
    for word in text.split():
        t = (cur + " " + word).strip()
        if d.textlength(t, font=f) <= width: cur = t
        else: lines.append(cur); cur = word
    if cur: lines.append(cur)
    return lines

def tiles(path, items, cols=2, bar=True):
    """items: (heading, body). Feature tiles with a cyan bar down the left side."""
    pad, gap = 30, 14
    tw = (W - 2 * pad - (cols - 1) * gap) // cols
    hf, bf = heavy(24), regular(19)
    probe = ImageDraw.Draw(Image.new("RGB", (10, 10)))
    rows = [items[i:i + cols] for i in range(0, len(items), cols)]
    heights = []
    for row in rows:
        most = max(len(wrap(probe, body, bf, tw - 56)) for _, body in row)
        heights.append(22 + 32 + most * 26 + 22)
    H = 2 * pad + sum(heights) + gap * (len(rows) - 1)
    im = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, W - 1, H - 1], outline=EDGE, width=2)
    y = pad
    for row, h in zip(rows, heights):
        for c, (head, body) in enumerate(row):
            x = pad + c * (tw + gap)
            d.rectangle([x, y, x + tw, y + h], fill=TILE, outline=EDGE, width=2)
            if bar: d.rectangle([x, y, x + 5, y + h], fill=CYAN)
            d.text((x + 28, y + 20), head, font=hf, fill=WHITE)
            for i, line in enumerate(wrap(d, body, bf, tw - 56)):
                d.text((x + 28, y + 54 + i * 26), line, font=bf, fill=BODY)
        y += h + gap
    im.save(path)

def glance(path, stats):
    """stats: (big, heading, body), three across."""
    pad, gap, cols = 30, 14, 3
    tw = (W - 2 * pad - (cols - 1) * gap) // cols
    th = 178
    rows = [stats[i:i + cols] for i in range(0, len(stats), cols)]
    H = 2 * pad + th * len(rows) + gap * (len(rows) - 1)
    im = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, W - 1, H - 1], outline=EDGE, width=2)
    for r, row in enumerate(rows):
        for c, (big, head, body) in enumerate(row):
            x, y = pad + c * (tw + gap), pad + r * (th + gap)
            d.rectangle([x, y, x + tw, y + th], fill=TILE, outline=EDGE, width=2)
            d.text((x + 22, y + 14), big, font=black(44), fill=CYAN)
            d.text((x + 22, y + 72), head, font=heavy(23), fill=WHITE)
            for i, line in enumerate(wrap(d, body, regular(18), tw - 44)):
                d.text((x + 22, y + 104 + i * 24), line, font=regular(18), fill=BODY)
    im.save(path)

def badge(size=512):
    """The mod's icon: a staff ID badge on a clip."""
    s = size / 512.0
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    def R(box, r, **kw): d.rounded_rectangle([int(v * s) for v in box], int(r * s), **kw)
    R((176, 20, 336, 96), 14, fill=(92, 96, 100), outline=(30, 30, 30), width=max(2, int(6 * s)))      # clip
    R((214, 40, 298, 72), 8, fill=(40, 42, 44))
    R((72, 76, 440, 492), 34, fill=(226, 222, 210), outline=(30, 30, 30), width=max(2, int(8 * s)))     # card
    R((72, 76, 440, 168), 34, fill=CYAN, outline=(30, 30, 30), width=max(2, int(8 * s)))
    d.rectangle([int(76 * s), int(134 * s), int(436 * s), int(168 * s)], fill=CYAN)
    d.line([int(76 * s), int(168 * s), int(436 * s), int(168 * s)], fill=(30, 30, 30), width=max(2, int(6 * s)))
    R((206, 100, 306, 124), 12, fill=(30, 30, 30))                                                       # slot
    R((108, 204, 248, 372), 12, fill=(150, 156, 160), outline=(30, 30, 30), width=max(2, int(6 * s)))    # photo
    d.ellipse([int(148 * s), int(228 * s), int(208 * s), int(288 * s)], fill=(58, 62, 66))
    d.pieslice([int(124 * s), int(298 * s), int(232 * s), int(430 * s)], 180, 360, fill=(58, 62, 66))
    for i, wdt in enumerate((130, 100, 130, 76)):                                                        # text lines
        R((276, 212 + i * 42, 276 + wdt, 232 + i * 42), 8, fill=(96, 98, 100) if i else (40, 42, 44))
    R((108, 404, 404, 456), 10, fill=(40, 42, 44))                                                       # title strip
    for i in range(3): R((126 + i * 62, 418, 172 + i * 62, 442), 6, fill=CYAN if i == 0 else (120, 124, 128))
    return im

def preview(path, icon):
    im = Image.new("RGB", (640, 360), (34, 33, 31))
    d = ImageDraw.Draw(im)
    for yy in range(360):       # soft vertical shade, like the other SloppyMods previews
        v = int(30 + 14 * (1 - abs(yy - 150) / 210.0))
        d.line([0, yy, 640, yy], fill=(v + 4, v + 3, v + 1))
    d.text((30, 38), "SLOPPYMODS", font=bold(17), fill=(170, 167, 160))
    d.text((30, 62), "HUMAN", font=black(50), fill=WHITE)
    d.text((30, 116), "RESOURCES", font=black(50), fill=CYAN)
    d.rectangle([30, 182, 250, 185], fill=CYAN)
    d.text((30, 202), "Job titles, work advice and", font=bold(17), fill=(200, 197, 190))
    d.text((30, 226), "workload tracking for your colony", font=bold(17), fill=(200, 197, 190))
    y = 268
    x = 30
    for label in ("TIME USE", "ADVISOR", "WORKLOAD", "TITLES"):
        wdt = int(d.textlength(label, font=bold(13))) + 22
        d.rounded_rectangle([x, y, x + wdt, y + 30], 6, fill=(52, 50, 47), outline=(80, 78, 74))
        d.text((x + 11, y + 7), label, font=bold(13), fill=WHITE)
        x += wdt + 8
    ic = icon.resize((232, 232), Image.LANCZOS)
    im.paste(ic, (398, 62), ic)
    im.save(path)

if __name__ == "__main__":
    ws = os.path.join(ROOT, "Workshop")
    os.makedirs(ws, exist_ok=True)
    icon = badge(512)
    icon.resize((256, 256), Image.LANCZOS).save(os.path.join(ROOT, "About", "ModIcon.png"))
    preview(os.path.join(ROOT, "About", "Preview.png"), icon)

    banner(os.path.join(ws, "banner-01-overview.png"), "SloppyMods", "Human Resources", icon)
    banner(os.path.join(ws, "banner-02-pages.png"), "One new button, four pages", "The HR tab")
    banner(os.path.join(ws, "banner-03-titles.png"), "Your own work templates", "Job titles")
    banner(os.path.join(ws, "banner-04-advice.png"), "Who should do what", "Advice and workload")
    banner(os.path.join(ws, "banner-05-settings.png"), "Tune it to your game", "Settings")
    banner(os.path.join(ws, "banner-06-faq.png"), "Questions people ask", "FAQ")
    banner(os.path.join(ws, "banner-07-bugs.png"), "Please include your build number", "Bug reports")
    banner(os.path.join(ws, "banner-08-sloppymods.png"), "A note on how this was made", "What is SloppyMods?")

    glance(os.path.join(ws, "glance.png"), [
        ("4", "HR pages", "Time use, Job advisor, Workload and Job titles"),
        ("1.7", "RimWorld", "Built for 1.7 only"),
        ("3", "Titles per colonist", "Merged in the order you set"),
        ("2", "Days of workload", "Tells a busy day from work nobody does"),
        ("1", "Requirement", "Harmony. No DLC needed"),
        ("0", "New items", "No buildings, research or defs to learn"),
    ])
    tiles(os.path.join(ws, "pages.png"), [
        ("Time use", "How each colonist spent the last hours or days: every kind of work, sleep, recreation, idling, and how much of it was walking."),
        ("Job advisor", "Suggested work priorities for every colonist, with the reasons. Apply one colonist, apply everyone, or undo."),
        ("Workload", "Every kind of work with how much is waiting, how long it has waited and who is on it."),
        ("Job titles", "Named templates of work priorities that you build, assign and reuse in every game."),
    ])
    tiles(os.path.join(ws, "titles.png"), [
        ("Build a title once", "Name it, click its priorities on a row laid out like the Work tab, and assign it to as many colonists as you like."),
        ("Up to three per colonist", "Doctor, then Crafter, then a baseline. Each title is used as written and they are merged together."),
        ("The higher title wins", "Two titles only conflict when both set the same kind of work. Drag a colonist's titles into order to decide which wins."),
        ("Hand edits are kept", "Change a title holder's box on the Work tab and it is remembered on top of their titles, and marked."),
        ("A library for every game", "Save a title to the library and load it into any other colony."),
        ("On the Work tab too", "A Title column shows who holds what. Click it to change a colonist's titles, or its header to set everyone at once."),
    ])
    tiles(os.path.join(ws, "advice.png"), [
        ("More than skill level", "Fit is scored from skill, passions, traits, genes and health, including passions added by other mods."),
        ("Worker quality", "Skilled colonists are scored by how much the colony needs their skills, so hauling and cleaning go to the others first."),
        ("Falling behind, or ignored?", "Work that is being done but piling up needs more workers. Work nobody touched in a day needs its assignments fixed."),
        ("Show me where", "Click a kind of work to step the camera through what is waiting, with the reason it isn't being done."),
        ("One change at a time", "Each suggestion changes one colonist, and says who and why."),
        ("Daily review, if you want it", "Optionally re-check assignments once a day. Title holders are never changed, only reported on."),
        ("Benches for the best", "A bench job with a strong priority 1 worker is kept clear of weaker colonists."),
        ("Rest unused work", "Optionally switch off work the colony has had none of for three days, and bring it back when some appears."),
    ])
    print("done")
