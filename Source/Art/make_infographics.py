from PIL import Image, ImageDraw, ImageFont
import os

BG = "#1E1D1B"; TILE = "#262523"; TEXT = "#F0EDE4"; BODY = "#C8C5BE"; CYAN = "#3EC8DC"
FD = "/usr/share/fonts/truetype/dejavu/"
H = lambda s: ImageFont.truetype(FD + "DejaVuSans-Bold.ttf", s)
B = lambda s: ImageFont.truetype(FD + "DejaVuSans.ttf", s)
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Workshop"); os.makedirs(OUT, exist_ok=True)
W = 1000


def wrap(d, text, font, width):
    words, lines, cur = text.split(), [], ""
    for w in words:
        t = (cur + " " + w).strip()
        if d.textlength(t, font=font) <= width: cur = t
        else: lines.append(cur); cur = w
    if cur: lines.append(cur)
    return lines


def tiles_image(name, kicker, tiles, cols=2, swatch=False):
    """tiles: list of (heading, body, colour-or-None)."""
    pad, gap = 30, 16
    tw = (W - 2 * pad - gap * (cols - 1)) // cols
    probe = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    hf, bf = H(22), B(17)
    heights = []
    for h, b, c in tiles:
        n = len(wrap(probe, b, bf, tw - 44))
        heights.append(22 + 30 + 10 + n * 24 + 20)
    rows = [heights[i:i + cols] for i in range(0, len(heights), cols)]
    rowh = [max(r) for r in rows]
    top = 70
    hgt = top + sum(rowh) + gap * (len(rows) - 1) + pad
    im = Image.new("RGB", (W, hgt), BG); d = ImageDraw.Draw(im)
    d.text((pad, 26), " ".join(kicker.upper()), font=H(15), fill=CYAN)
    y = top
    for ri, r in enumerate(rows):
        for ci in range(len(r)):
            h, b, c = tiles[ri * cols + ci]
            x = pad + ci * (tw + gap)
            d.rectangle([x, y, x + tw, y + rowh[ri]], fill=TILE)
            d.rectangle([x, y, x + 5, y + rowh[ri]], fill=c or CYAN)
            tx = x + 24
            if swatch and c:
                d.rounded_rectangle([tx, y + 24, tx + 16, y + 40], 3, fill=c); tx += 26
            d.text((tx, y + 20), h, font=hf, fill=TEXT)
            ty = y + 62
            for ln in wrap(d, b, bf, tw - 44):
                d.text((x + 24, ty), ln, font=bf, fill=BODY); ty += 24
        y += rowh[ri] + gap
    im.save(f"{OUT}/{name}.png")


tiles_image("workload-statuses", "Workload status, tracked over two days", [
    ("Clear", "Nothing waiting.", "#5FAF5F"),
    ("Keeping up", "Work arrives and gets done.", "#5FAF5F"),
    ("Slow", "Backlog for a day, but it is being worked.", "#D8C14A"),
    ("Falling behind", "Backlog for a day and at least 25% bigger than two days ago.", "#E09A3E"),
    ("Needs more workers", "Backlog for two days with steady work on it, still not shrinking.", "#D9573E"),
    ("Not being worked", "Backlog for a day and almost nobody touched it: the assignments are wrong.", "#D9573E"),
    ("Open bills", "Bills waiting at benches.", "#8A8780"),
    ("Stuck bills", "Waiting a day with nobody on them, often missing materials.", "#B06AD0"),
], swatch=True)

tiles_image("job-advisor", "Job advisor: advice from more than skill level", [
    ("Fit score", "Skill, passion (read from its real learning rate, so modded passions count), health, work speed, traits, genes and VSE expertise.", None),
    ("Worker quality 0-100", "How valuable each colonist's skilled work is right now. Hauling and cleaning go to the lowest scores first.", None),
    ("Roles", "Up to three per colonist, such as \"Cook, Butcher\", or Generalist / Support.", None),
    ("Benches kept for the best", "A strong priority 1 worker keeps weaker colonists off the bench unless they have passion or expertise.", None),
    ("Sized to real demand", "Priority 1 headcount matches the hours of that work actually done in the last day.", None),
    ("Fake-skill work is unskilled", "Chemfuel refining, Complex Jobs' Nurse and other jobs that list a skill but never use it.", None),
    ("Neglected jobs rotate", "Unskilled work nobody does is handed daily to low scorers, one specific job each.", None),
    ("You stay in charge", "Apply, Apply all, Undo last apply, changes shown as 3>1. Title holders are never changed.", None),
])

tiles_image("options", "Good to know: nothing happens until you say so", [
    ("Badges on portraits  -  ON", "Two-letter coloured badge showing what each colonist is doing.", "#5FAF5F"),
    ("Daily review  -  OFF", "Re-checks assignments once a day and sends a letter saying what changed.", "#8A8780"),
    ("Rest unused work  -  OFF", "Switches off work nobody needed for 3 days, back on within an hour. Never medical, firefighting, rescue, childcare or warden.", "#8A8780"),
    ("Mod settings", "Untick workbenches or single bills to leave them out of the workload analysis. The title library lives here, shared by every game.", None),
], swatch=True)
print(os.listdir(OUT))
