#!/usr/bin/env python3
"""Erzeugt das NetScanner-App-Icon reproduzierbar aus Code.

Motiv: Radarschirm — konzentrische Ringe in Weiß, Sweep-Strahl und erfasster
Punkt in Kroste-Gold, auf einem abgerundeten Quadrat in Kroste-Blau. Das Quadrat
mit Rundecken ist plattformneutral (Windows-Taskbar, GNOME/KDE-Dock, AppImage).

Ausgabe (beide werden eingecheckt):
  NetScanner/Assets/netscanner.png  256x256, transparente Ecken — Fenster, Tray, AppImage
  NetScanner/Assets/netscanner.ico  multi-res 16/24/32/48/64/128/256 — <ApplicationIcon>

Aufruf aus dem Repo-Root:  python3 scripts/build_icon.py
Abhängigkeit: Pillow (pip install pillow)

Der PowerShell-Port scripts/build_icon.ps1 muss bei Änderungen mitgezogen
werden, sonst driften die Icons je nach Rechner auseinander.
"""
from __future__ import annotations

import pathlib

from PIL import Image, ImageDraw

APP_NAME = "netscanner"
SIZE = 256
SUPERSAMPLE = 4          # gegen ausgefranste Kanten: groß zeichnen, dann herunterskalieren
BG = (18, 62, 107, 255)         # #123E6B — Kroste-Akzent (Grundfläche)
RING = (255, 255, 255)          # Ringe: Weiß, damit sie auf dem Blau stehen
SIGNAL = (224, 177, 76)         # #E0B14C — Kroste-Gold (Sweep + erfasster Punkt)
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

ASSETS = pathlib.Path(__file__).resolve().parent.parent / "NetScanner" / "Assets"


def draw_icon(size: int) -> Image.Image:
    """Zeichnet das Radar-Motiv in der gewünschten Kantenlänge."""
    s = size * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Abgerundetes Quadrat als Grundfläche.
    radius = int(s * 0.18)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=radius, fill=BG)

    cx = cy = s / 2

    # Unter 32 px verschmelzen drei duenne Ringe zu Matsch: dort nur zwei Ringe,
    # dafuer kraeftiger und voll deckend. Bei jeder Aenderung am Motiv die
    # 16x16-Silhouette gegenpruefen, sie ist der harte Massstab.
    small = size < 32
    rings = ((0.36, 235), (0.17, 255)) if small else ((0.40, 150), (0.28, 195), (0.15, 240))
    ring_w = s * (0.045 if small else 0.012)
    sweep_w = s * (0.075 if small else 0.016)
    sweep_reach = s * (0.33 if small else 0.38)
    dot_r = s * (0.075 if small else 0.045)

    # Konzentrische Ringe: nach außen hin blasser, wie ein abklingendes Echo.
    for factor, alpha in rings:
        r = s * factor
        d.ellipse([cx - r, cy - r, cx + r, cy + r],
                  outline=RING + (alpha,), width=max(1, int(ring_w)))

    # Sweep-Strahl vom Zentrum nach rechts oben (45 Grad).
    d.line([cx, cy, cx + sweep_reach, cy - sweep_reach],
           fill=SIGNAL + (255,), width=max(1, int(sweep_w)))

    # Mittelpunkt = erfasstes Geraet.
    d.ellipse([cx - dot_r, cy - dot_r, cx + dot_r, cy + dot_r], fill=SIGNAL + (255,))

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)

    master = draw_icon(SIZE)
    png = ASSETS / f"{APP_NAME}.png"
    master.save(png)
    print(f"geschrieben: {png}")

    # Jede ICO-Groesse einzeln zeichnen statt herunterzuskalieren — bei 16x16
    # verschwinden sonst die duennen Ringe im Matsch.
    frames = [draw_icon(n) for n in ICO_SIZES]
    ico = ASSETS / f"{APP_NAME}.ico"
    frames[-1].save(ico, format="ICO",
                    sizes=[(n, n) for n in ICO_SIZES], append_images=frames[:-1])
    print(f"geschrieben: {ico} ({', '.join(str(n) for n in ICO_SIZES)})")


if __name__ == "__main__":
    main()
