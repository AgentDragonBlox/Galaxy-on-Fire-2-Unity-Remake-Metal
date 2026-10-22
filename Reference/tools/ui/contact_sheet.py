"""Contact sheet of a UiShots series (Assets/Scripts/Editor/UiShots.cs, docs/UI-DESIGN.md "Checking a screen").

    python Reference/tools/ui/contact_sheet.py Logs/UiShots/<tag> [width]

Every PNG of the folder scaled to the same height, labelled with its name, in rows no wider than 'width' (default
2400 px), written as <folder>/sheet.png. Needs Pillow.
"""
import os
import sys

from PIL import Image, ImageDraw

ORDER = ["desktop-1080p", "desktop-720p", "desktop-4x3", "desktop-1x1", "desktop-21x9", "desktop-32x9",
         "deck-1280x800", "tv-1080p", "phone-20x9", "phone-16x9"]


def main():
    folder = sys.argv[1]
    max_w = int(sys.argv[2]) if len(sys.argv) > 2 else 2400
    names = [f[:-4] for f in os.listdir(folder) if f.endswith(".png") and f != "sheet.png"]
    names.sort(key=lambda n: (ORDER.index(n) if n in ORDER else 99, n))
    tile_h, label_h, gap = 300, 26, 8
    tiles = []
    for n in names:
        im = Image.open(os.path.join(folder, n + ".png")).convert("RGB")
        w = round(im.width * tile_h / im.height)
        tiles.append((n, im.resize((w, tile_h), Image.LANCZOS)))
    rows, row, x = [], [], 0
    for t in tiles:
        if row and x + t[1].width > max_w:
            rows.append(row)
            row, x = [], 0
        row.append(t)
        x += t[1].width + gap
    if row:
        rows.append(row)
    sheet_w = max(sum(t[1].width + gap for t in r) for r in rows)
    sheet = Image.new("RGB", (sheet_w, len(rows) * (tile_h + label_h + gap)), (24, 24, 24))
    d = ImageDraw.Draw(sheet)
    y = 0
    for r in rows:
        x = 0
        for n, im in r:
            d.text((x + 4, y + 6), n, fill=(255, 220, 120))
            sheet.paste(im, (x, y + label_h))
            x += im.width + gap
        y += tile_h + label_h + gap
    out = os.path.join(folder, "sheet.png")
    sheet.save(out)
    print(out)


if __name__ == "__main__":
    main()
