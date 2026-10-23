#!/usr/bin/env python3
"""Map for the geographic view (GeoMapView.cs): projection + texture.

Europe is drawn on a disc in an azimuthal equidistant projection centred on the
smallest circle that holds the graph's European airports (those inside
--region). Distances and directions from the centre are true; Unity bends the
disc onto a gently curved surface and places every airport with the same
formula, so they sit exactly on the map. Airports outside the disc (the
intercontinental ends: New York, Dubai, Singapore, ...) are shown by Unity on a
ring beyond the map's edge, in their true direction.

Outputs (public domain map data, safe to commit; no EUROCONTROL figures):
  data/processed/geomap.json       centre, map radius, texture extent, country labels
  data/processed/geomap_dark.png   square texture: x = east, y = north (top row = north)

Source: Natural Earth 1:10m admin-0 countries, Turkish point of view (as chosen for
the project: Crimea in Ukraine, Northern Cyprus and Kosovo shown, the Golan Heights
in Syria), plus the Morocco / Western Sahara line at 27 deg 40' N that this view
leaves out; 1:10m coastline and 1:50m lakes. Downloaded once into
data/raw/naturalearth/.

Usage: python scripts/build_geomap.py [--nodes PATH] [--size 4096]
           [--region LON_MIN LON_MAX LAT_MIN LAT_MAX] [--margin 1.4]
"""
import argparse
import json
import logging
import math
import sys
import urllib.request
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
PROCESSED = ROOT / "data" / "processed"
NATURAL_EARTH = ROOT / "data" / "raw" / "naturalearth"
NE_URL = "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/{}.geojson"
COUNTRIES = "ne_10m_admin_0_countries_tur"
# Lines the point of view does not draw: (country polygon, latitude, name south of it, its label at lat / lon).
# Morocco and Western Sahara are one polygon there; the internationally recognised line between them is 27 deg 40' N.
SPLIT_LINES = [("Morocco", 27.0 + 40.0 / 60.0, "W. Sahara", (24.3, -13.0))]
COASTLINE = "ne_10m_coastline"
LAKES = "ne_50m_lakes"

# Airports the map must hold: Europe and the rest of the EUROCONTROL area
# (Canaries, Azores, Iceland, Turkey, Caucasus, Levant, Egypt, Morocco).
DEFAULT_REGION = (-32.0, 46.0, 26.0, 72.0)

# Dark theme, close to the dashboard (UIKit): sea darkest, land a step lighter,
# coastlines bright blue so the shape of Europe reads first, country borders
# nearly as clear but neutral grey (a different tone, not a second coastline).
SEA = (9, 16, 29)
LAND = (27, 37, 52)
GRATICULE = (24, 36, 56)
BORDER = (138, 146, 164)
COAST = (110, 150, 196)

log = logging.getLogger("build_geomap")


# ---- Projection (same formulas in GeoMapProjection.cs) -------------------

def angular_distance(lat1, lon1, lat2, lon2):
    """Great-circle distance in degrees."""
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dl = math.radians(lon2 - lon1)
    c = math.sin(p1) * math.sin(p2) + math.cos(p1) * math.cos(p2) * math.cos(dl)
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


def project(lat, lon, lat0, lon0):
    """Azimuthal equidistant: (x east, y north) in degrees of arc from the centre."""
    p, p0 = math.radians(lat), math.radians(lat0)
    dl = math.radians(lon - lon0)
    cos_c = math.sin(p0) * math.sin(p) + math.cos(p0) * math.cos(p) * math.cos(dl)
    c = math.acos(max(-1.0, min(1.0, cos_c)))
    k = 1.0 if c < 1e-9 else c / math.sin(c)
    x = k * math.cos(p) * math.sin(dl)
    y = k * (math.cos(p0) * math.sin(p) - math.sin(p0) * math.cos(p) * math.cos(dl))
    return math.degrees(x), math.degrees(y)


def enclosing_cap(points, step=0.25):
    """Centre (lat, lon) and radius (deg) of the smallest circle holding all points (grid search, then refine)."""
    lats = [p[0] for p in points]
    lons = [p[1] for p in points]
    best = None
    lat_range = (min(lats), max(lats))
    lon_range = (min(lons), max(lons))
    for res in (2.0, step):
        if best is not None:
            _, blat, blon = best
            lat_range = (blat - 2.0, blat + 2.0)
            lon_range = (blon - 2.0, blon + 2.0)
        lat = lat_range[0]
        while lat <= lat_range[1] + 1e-9:
            lon = lon_range[0]
            while lon <= lon_range[1] + 1e-9:
                r = max(angular_distance(lat, lon, a, b) for a, b in points)
                if best is None or r < best[0]:
                    best = (r, lat, lon)
                lon += res
            lat += res
    r, lat, lon = best
    return round(lat, 4), round(lon, 4), r


# ---- Inputs ----------------------------------------------------------------

def download(name, directory=NATURAL_EARTH):
    path = directory / f"{name}.geojson"
    if not path.exists():
        directory.mkdir(parents=True, exist_ok=True)
        log.info("downloading %s", name)
        urllib.request.urlretrieve(NE_URL.format(name), path)
    return path


def read_airports(path):
    nodes = json.loads(Path(path).read_text(encoding="utf-8"))
    return [(n["id"], float(n["lat"]), float(n["lon"])) for n in nodes
            if n.get("lat") is not None and n.get("lon") is not None]


def rings(geometry):
    """Outer and inner rings of a (Multi)Polygon, or the lines of a (Multi)LineString."""
    t, c = geometry["type"], geometry["coordinates"]
    if t == "Polygon":
        return [c[0]], c[1:]
    if t == "MultiPolygon":
        return [p[0] for p in c], [h for p in c for h in p[1:]]
    if t == "LineString":
        return [c], []
    if t == "MultiLineString":
        return list(c), []
    return [], []


# ---- Texture -----------------------------------------------------------------

class Canvas:
    """Square image over the projected square [-half, half]^2 (degrees), drawn at `scale` x for anti-aliasing."""

    def __init__(self, size, half, lat0, lon0, scale=2):
        self.size, self.half, self.lat0, self.lon0, self.scale = size, half, lat0, lon0, scale
        self.image = Image.new("RGB", (size * scale, size * scale), SEA)
        self.draw = ImageDraw.Draw(self.image)
        # Rings wholly this far from the centre are off the map (and AEQD degenerates near the antipode).
        self.reach = half * math.sqrt(2) + 10.0

    def pixels(self, coords):
        """Projected pixel coordinates of [lon, lat] points, or None when the ring is wholly off the map."""
        n = self.size * self.scale
        out, near = [], False
        for lon, lat in coords:
            if angular_distance(lat, lon, self.lat0, self.lon0) <= self.reach:
                near = True
            x, y = project(lat, lon, self.lat0, self.lon0)
            out.append(((x + self.half) / (2 * self.half) * n, (self.half - y) / (2 * self.half) * n))
        return out if near and len(out) >= 2 else None

    def polygon(self, coords, fill):
        pts = self.pixels(coords)
        if pts is not None and len(pts) >= 3:
            self.draw.polygon(pts, fill=fill)

    def line(self, coords, fill, width):
        pts = self.pixels(coords)
        if pts is not None:
            self.draw.line(pts, fill=fill, width=max(1, round(width * self.scale)), joint="curve")

    def result(self):
        return self.image.resize((self.size, self.size), Image.LANCZOS)


def graticule(step=10):
    """Meridians and parallels every `step` degrees, as [lon, lat] lines."""
    lines = []
    for lon in range(-180, 180, step):
        lines.append([[lon, lat] for lat in range(-80, 81, 2)])
    for lat in range(-80, 81, step):
        lines.append([[lon, lat] for lon in range(-180, 181, 2)])
    return lines


def latitude_line(countries, name, lat, step=0.25):
    """[lon, lat] points along `lat` across the named country's polygon (its westmost to eastmost crossing)."""
    xs = []
    for f in countries["features"]:
        if f["properties"].get("NAME") != name:
            continue
        for ring in rings(f["geometry"])[0]:
            for (x1, y1), (x2, y2) in zip(ring, ring[1:] + ring[:1]):
                if (y1 - lat) * (y2 - lat) < 0:
                    xs.append(x1 + (lat - y1) * (x2 - x1) / (y2 - y1))
    if len(xs) < 2:
        return None
    a, b = min(xs), max(xs)
    n = max(2, int(math.ceil((b - a) / step)) + 1)
    return [[a + (b - a) * i / (n - 1), lat] for i in range(n)]


def render(countries, coastline, lakes, size, half, lat0, lon0, scale=2):
    canvas = Canvas(size, half, lat0, lon0, scale)
    for line in graticule():
        canvas.line(line, GRATICULE, 1.0)
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
            canvas.line(ring, BORDER, 1.6)
    for name, lat, _, _ in SPLIT_LINES:
        line = latitude_line(countries, name, lat)
        if line is not None:
            canvas.line(line, BORDER, 1.6)
    for f in coastline["features"]:
        lines, _ = rings(f["geometry"])
        for line in lines:
            canvas.line(line, COAST, 1.8)
    return canvas.result()


def area_km2(geometry):
    """Rough area (local equirectangular shoelace per outer ring), enough to tell microstates from countries."""
    total = 0.0
    for ring in rings(geometry)[0]:
        lat_mid = math.radians(sum(pt[1] for pt in ring) / len(ring))
        xy = [(lon * 111.32 * math.cos(lat_mid), lat * 110.57) for lon, lat in ring]
        total += abs(sum(x1 * y2 - x2 * y1 for (x1, y1), (x2, y2) in zip(xy, xy[1:] + xy[:1]))) / 2.0
    return total


def country_labels(countries, lat0, lon0, radius, max_min_label=5.0, min_area_km2=15000.0):
    """Country names to place on the map: label point inside the disc; no dependencies or small countries (they crowd the Levant and the Balkans)."""
    labels = []
    for f in countries["features"]:
        p = f["properties"]
        rank = p.get("LABELRANK", 10)
        lx, ly = p.get("LABEL_X"), p.get("LABEL_Y")
        if lx is None or ly is None or p.get("TYPE") == "Dependency":
            continue
        if p.get("MIN_LABEL", 10) > max_min_label or area_km2(f["geometry"]) < min_area_km2:
            continue
        if angular_distance(ly, lx, lat0, lon0) > radius - 1.0:
            continue
        x, y = project(ly, lx, lat0, lon0)
        labels.append({"name": p["NAME"], "x": round(x, 3), "y": round(y, 3), "rank": rank})
    for _, _, south, (la, lo) in SPLIT_LINES:
        if angular_distance(la, lo, lat0, lon0) <= radius - 1.0:
            x, y = project(la, lo, lat0, lon0)
            labels.append({"name": south, "x": round(x, 3), "y": round(y, 3), "rank": 4})
    labels.sort(key=lambda l: (l["rank"], l["name"]))
    return labels


def build(airports, countries, coastline, lakes, region=DEFAULT_REGION, margin=1.4, size=4096, scale=2):
    lon_min, lon_max, lat_min, lat_max = region
    inside = [(lat, lon) for _, lat, lon in airports if lon_min <= lon <= lon_max and lat_min <= lat <= lat_max]
    if not inside:
        raise ValueError("no airports inside the region")
    lat0, lon0, fit = enclosing_cap(inside)
    radius = round(fit + margin, 3)
    half = radius + 0.5
    outside = [a for a in airports if angular_distance(a[1], a[2], lat0, lon0) > radius]
    log.info("map centre %.2fN %.2fE, radius %.2f deg (airports fit in %.2f); %d inside, %d outside: %s",
             lat0, lon0, radius, fit, len(airports) - len(outside), len(outside),
             " ".join(a[0] for a in outside))
    image = render(countries, coastline, lakes, size, half, lat0, lon0, scale)
    meta = {
        "projection": "azimuthal equidistant; x east, y north, degrees of arc from the centre",
        "source": "Natural Earth (public domain): 1:10m admin-0 countries (Turkish point of view, "
                  "Morocco / Western Sahara line at 27 deg 40' N added), "
                  "1:10m coastline, 1:50m lakes",
        "centerLat": lat0,
        "centerLon": lon0,
        "radiusDeg": radius,
        "textureHalfSizeDeg": half,
        "texture": "geomap_dark.png",
        "labels": country_labels(countries, lat0, lon0, radius),
    }
    return meta, image, outside


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--nodes", type=Path, default=None,
                   help="nodes JSON with lat / lon (default: nodes_ectrl.json, else nodes_3d_ectrl.json)")
    p.add_argument("--out-dir", type=Path, default=PROCESSED)
    p.add_argument("--size", type=int, default=4096, help="texture size (px)")
    p.add_argument("--region", type=float, nargs=4, default=DEFAULT_REGION,
                   metavar=("LON_MIN", "LON_MAX", "LAT_MIN", "LAT_MAX"),
                   help="airports the map must hold (default: Europe and the EUROCONTROL area)")
    p.add_argument("--margin", type=float, default=1.4, help="degrees added around the farthest airport")
    args = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    nodes = args.nodes
    if nodes is None:
        nodes = next((PROCESSED / f for f in ("nodes_ectrl.json", "nodes_3d_ectrl.json")
                      if (PROCESSED / f).exists()), None)
    if nodes is None or not Path(nodes).exists():
        log.error("no nodes file (run build_graph_ectrl.py, or pass --nodes)")
        return 1
    load = lambda name: json.loads(download(name).read_text(encoding="utf-8"))
    meta, image, _ = build(read_airports(nodes), load(COUNTRIES), load(COASTLINE), load(LAKES),
                           tuple(args.region), args.margin, args.size)
    args.out_dir.mkdir(parents=True, exist_ok=True)
    (args.out_dir / "geomap.json").write_text(json.dumps(meta, indent=1, ensure_ascii=False), encoding="utf-8")
    image.save(args.out_dir / meta["texture"], optimize=True)
    log.info("wrote geomap.json (%d labels) and %s (%dx%d)", len(meta["labels"]), meta["texture"], args.size, args.size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
