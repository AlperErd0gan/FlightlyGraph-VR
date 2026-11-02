#!/usr/bin/env python3
"""World texture for the dome view (GeoMapView, "you at the centre of the Earth").

The dome shows the whole globe around the viewer at true scale; the sharp map of
Europe (build_geomap.py) lies on top of it. This draws the rest of the world in
the same dark style (sea, land, borders, coastlines, lakes, a 10 degree graticule)
as an equirectangular image: x = longitude -180..180 (left to right), y = latitude
90..-90 (top to bottom). Unity maps it onto a sphere with u = (lon + 180) / 360,
v = (lat + 90) / 180.

Rings that cross the 180 degree meridian (Russia, Fiji, Antarctica) are unwrapped
and drawn three times (shifted by -360, 0, +360) so they do not smear across the
image.

Output (public domain map data, safe to commit; no EUROCONTROL figures):
  data/processed/globe_dark.png

Usage: python scripts/build_globe.py [--width 8192] [--out PATH]
"""
import argparse
import logging
import sys
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
from build_geomap import (  # noqa: E402  (same folder; shares the palette and the Natural Earth data)
    BORDER, COAST, COASTLINE, COUNTRIES, GRATICULE, LAKES, LAND, PROCESSED, SEA, SPLIT_LINES,
    download, latitude_line, rings,
)
import json  # noqa: E402

log = logging.getLogger("build_globe")


def unwrap(coords):
    """[lon, lat] points with longitude jumps over 180 degrees removed (continuous across the 180 meridian)."""
    out = []
    shift = 0.0
    previous = None
    for lon, lat in coords:
        if previous is not None:
            step = lon + shift - previous
            if step > 180.0:
                shift -= 360.0
            elif step < -180.0:
                shift += 360.0
        out.append((lon + shift, lat))
        previous = lon + shift
    return out


class WorldCanvas:
    """Equirectangular image, drawn at `scale` x and reduced for anti-aliasing."""

    def __init__(self, width, scale=2):
        self.width, self.height, self.scale = width, width // 2, scale
        self.image = Image.new("RGB", (self.width * scale, self.height * scale), SEA)
        self.draw = ImageDraw.Draw(self.image)

    def pixels(self, coords, offset):
        w, h = self.width * self.scale, self.height * self.scale
        return [((lon + offset + 180.0) / 360.0 * w, (90.0 - lat) / 180.0 * h) for lon, lat in coords]

    def offsets(self, coords):
        """Shifts that bring an unwrapped ring onto the image (its own place and its copies one turn left / right)."""
        lons = [c[0] for c in coords]
        return [o for o in (-360.0, 0.0, 360.0) if max(lons) + o >= -180.0 and min(lons) + o <= 180.0]

    def polygon(self, coords, fill):
        coords = unwrap(coords)
        if len(coords) < 3:
            return
        lons = [c[0] for c in coords]
        if max(lons) - min(lons) > 350.0:
            # A ring around a pole (Antarctica): close it along the pole's edge of the image.
            pole = -90.0 if sum(c[1] for c in coords) < 0 else 90.0
            coords = coords + [(coords[-1][0], pole), (coords[0][0], pole)]
        for o in self.offsets(coords):
            self.draw.polygon(self.pixels(coords, o), fill=fill)

    def line(self, coords, fill, width):
        coords = unwrap(coords)
        if len(coords) < 2:
            return
        for o in self.offsets(coords):
            self.draw.line(self.pixels(coords, o), fill=fill, width=max(1, round(width * self.scale)), joint="curve")

    def result(self):
        return self.image.resize((self.width, self.height), Image.LANCZOS)


def render(countries, coastline, lakes, width, scale=2):
    canvas = WorldCanvas(width, scale)
    for lon in range(-180, 181, 10):
        canvas.line([(lon, lat) for lat in range(-90, 91, 2)], GRATICULE, 1.0)
    for lat in range(-80, 81, 10):
        canvas.line([(lon, lat) for lon in range(-180, 181, 2)], GRATICULE, 1.0)
    for f in countries["features"]:
        outer, holes = rings(f["geometry"])
        for ring in outer:
            canvas.polygon(ring, LAND)
        for ring in holes:
            canvas.polygon(ring, SEA)
    for f in lakes["features"]:
        outer, _ = rings(f["geometry"])
        for ring in outer:
            canvas.polygon(ring, SEA)
    for f in countries["features"]:
        outer, holes = rings(f["geometry"])
        for ring in outer + holes:
            canvas.line(ring, BORDER, 1.2)
    for name, lat, _, _ in SPLIT_LINES:
        line = latitude_line(countries, name, lat)
        if line is not None:
            canvas.line(line, BORDER, 1.2)
    for f in coastline["features"]:
        lines, _ = rings(f["geometry"])
        for line in lines:
            canvas.line(line, COAST, 1.4)
    return canvas.result()


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--width", type=int, default=8192, help="image width (px); the height is half of it")
    p.add_argument("--out", type=Path, default=PROCESSED / "globe_dark.png")
    args = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    countries = json.loads(download(COUNTRIES).read_text(encoding="utf-8"))
    coastline = json.loads(download(COASTLINE).read_text(encoding="utf-8"))
    lakes = json.loads(download(LAKES).read_text(encoding="utf-8"))
    image = render(countries, coastline, lakes, args.width)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    image.save(args.out, optimize=True)
    log.info("wrote %s (%d x %d, %.1f MB)", args.out, image.width, image.height, args.out.stat().st_size / 1e6)
    return 0


if __name__ == "__main__":
    sys.exit(main())
