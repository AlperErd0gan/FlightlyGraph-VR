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
  data/processed/geomap.json            centre, map radius, texture extent, country labels
  data/processed/geomap_dark.png        square texture: x = east, y = north (top row = north)
  data/processed/geomap_satellite.jpg   (--style satellite / both) the same square from
                                        NASA Blue Marble imagery, borders drawn on top

Sources: Natural Earth 1:10m admin-0 countries, French point of view (borders as
France recognises them; the project is at the University of Angers), 1:10m
coastline and 1:50m lakes, downloaded once into data/raw/naturalearth/; NASA
Blue Marble Next Generation, July 2004, topography and bathymetry
(21600 x 10800 px, ~30 MB), downloaded once into data/raw/bluemarble/.

Usage: python scripts/build_geomap.py [--nodes PATH] [--size 4096] [--style dark|satellite|both]
           [--region LON_MIN LON_MAX LAT_MIN LAT_MAX] [--margin 1.4]
"""
import argparse
import json
import logging
import math
import sys
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
PROCESSED = ROOT / "data" / "processed"
NATURAL_EARTH = ROOT / "data" / "raw" / "naturalearth"
NE_URL = "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/{}.geojson"
COUNTRIES = "ne_10m_admin_0_countries_fra"
COASTLINE = "ne_10m_coastline"
LAKES = "ne_50m_lakes"
BLUE_MARBLE = ROOT / "data" / "raw" / "bluemarble" / "world.topo.bathy.200407.3x21600x10800.jpg"
# July: little snow, so white labels and routes stay readable over Scandinavia and Russia.
BLUE_MARBLE_URL = ("https://eoimages.gsfc.nasa.gov/images/imagerecords/73000/73751/"
                   "world.topo.bathy.200407.3x21600x10800.jpg")
SATELLITE_TEXTURE = "geomap_satellite.jpg"

# Airports the map must hold: Europe and the rest of the EUROCONTROL area
# (Canaries, Azores, Iceland, Turkey, Caucasus, Levant, Egypt, Morocco).
DEFAULT_REGION = (-32.0, 46.0, 26.0, 72.0)

# Dark theme, close to the dashboard (UIKit): sea darkest, land a step lighter,
# coastlines brightest so the shape of Europe reads first, borders between.
SEA = (9, 16, 29)
LAND = (27, 37, 52)
GRATICULE = (24, 36, 56)
BORDER = (70, 88, 114)
COAST = (104, 134, 170)
# Satellite: imagery darkened so airports and routes stay the brightest things; light lines.
SATELLITE_DIM = 0.78
SATELLITE_BORDER = (190, 198, 212)
SATELLITE_COAST = (214, 224, 238)

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


def unproject_grid(x, y, lat0, lon0):
    """Inverse of project() for arrays of planar degrees: (lat, lon) in degrees."""
    c = np.radians(np.hypot(x, y))
    theta = np.arctan2(x, y)                      # bearing from north, clockwise
    p0 = math.radians(lat0)
    lat = np.arcsin(np.clip(np.sin(p0) * np.cos(c) + np.cos(p0) * np.sin(c) * np.cos(theta), -1.0, 1.0))
    lon = math.radians(lon0) + np.arctan2(np.sin(theta) * np.sin(c) * math.cos(p0),
                                          np.cos(c) - math.sin(p0) * np.sin(lat))
    lon = (np.degrees(lon) + 180.0) % 360.0 - 180.0
    return np.degrees(lat), lon


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

    def __init__(self, size, half, lat0, lon0, scale=2, background=None):
        self.size, self.half, self.lat0, self.lon0, self.scale = size, half, lat0, lon0, scale
        n = size * scale
        self.image = background.resize((n, n), Image.BILINEAR) if background is not None else Image.new("RGB", (n, n), SEA)
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
            canvas.line(ring, BORDER, 1.2)
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


def reproject(source, box, size, half, lat0, lon0):
    """
    The projected square sampled (bilinear) from an equirectangular image `source`
    (H x W x 3 uint8) covering box = (lon_min, lon_max, lat_min, lat_max).
    """
    lon_min, lon_max, lat_min, lat_max = box
    h, w = source.shape[:2]
    out = np.empty((size, size, 3), dtype=np.uint8)
    step = 2 * half / size
    xs = (-half + (np.arange(size) + 0.5) * step).astype(np.float32)
    for row0 in range(0, size, 512):                       # in bands: bounded memory
        rows = np.arange(row0, min(size, row0 + 512))
        ys = (half - (rows + 0.5) * step).astype(np.float32)
        x, y = np.meshgrid(xs, ys)
        lat, lon = unproject_grid(x, y, lat0, lon0)
        fx = np.clip((lon - lon_min) / (lon_max - lon_min) * w - 0.5, 0, w - 1.001)
        fy = np.clip((lat_max - lat) / (lat_max - lat_min) * h - 0.5, 0, h - 1.001)
        x0, y0 = fx.astype(np.int64), fy.astype(np.int64)
        tx, ty = (fx - x0)[..., None], (fy - y0)[..., None]
        top = source[y0, x0] * (1 - tx) + source[y0, x0 + 1] * tx
        bottom = source[y0 + 1, x0] * (1 - tx) + source[y0 + 1, x0 + 1] * tx
        out[rows[0]:rows[-1] + 1] = np.clip(top * (1 - ty) + bottom * ty + 0.5, 0, 255).astype(np.uint8)
    return out


def render_satellite(world, countries, coastline, size, half, lat0, lon0, scale=2):
    """Blue Marble (`world`: the whole equirectangular PIL image) in the projection, dimmed, borders on top."""
    # Crop to what the square needs (+ margin), so only that part is held as an array.
    step = 2 * half / 64
    grid = -half + (np.arange(65) * step)
    lat, lon = unproject_grid(*np.meshgrid(grid, grid), lat0, lon0)
    box = (max(-180.0, float(lon.min()) - 2), min(180.0, float(lon.max()) + 2),
           max(-90.0, float(lat.min()) - 2), min(90.0, float(lat.max()) + 2))
    w, h = world.size
    crop = world.crop((int((box[0] + 180) / 360 * w), int((90 - box[3]) / 180 * h),
                       int(math.ceil((box[1] + 180) / 360 * w)), int(math.ceil((90 - box[2]) / 180 * h))))
    # The crop's pixel edges, in degrees (rounded to whole pixels).
    l0 = int((box[0] + 180) / 360 * w) / w * 360 - 180
    l1 = int(math.ceil((box[1] + 180) / 360 * w)) / w * 360 - 180
    t0 = 90 - int((90 - box[3]) / 180 * h) / h * 180
    t1 = 90 - int(math.ceil((90 - box[2]) / 180 * h)) / h * 180
    pixels = reproject(np.asarray(crop.convert("RGB")), (l0, l1, t1, t0), size, half, lat0, lon0)
    pixels = (pixels.astype(np.float32) * SATELLITE_DIM + 0.5).astype(np.uint8)
    canvas = Canvas(size, half, lat0, lon0, scale, background=Image.fromarray(pixels))
    for f in countries["features"]:
        outer, holes = rings(f["geometry"])
        for ring in outer + holes:
            canvas.line(ring, SATELLITE_BORDER, 1.0)
    for f in coastline["features"]:
        lines, _ = rings(f["geometry"])
        for line in lines:
            canvas.line(line, SATELLITE_COAST, 1.2)
    return canvas.result()


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
        "source": "Natural Earth (public domain): 1:10m admin-0 countries (French point of view), "
                  "1:10m coastline, 1:50m lakes",
        "centerLat": lat0,
        "centerLon": lon0,
        "radiusDeg": radius,
        "textureHalfSizeDeg": half,
        "texture": "geomap_dark.png",
        "satelliteTexture": SATELLITE_TEXTURE,
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
    p.add_argument("--style", choices=("dark", "satellite", "both"), default="dark",
                   help="textures to draw (satellite downloads NASA Blue Marble once, ~30 MB)")
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
    countries, coastline = load(COUNTRIES), load(COASTLINE)
    meta, image, _ = build(read_airports(nodes), countries, coastline, load(LAKES),
                           tuple(args.region), args.margin, args.size)
    args.out_dir.mkdir(parents=True, exist_ok=True)
    (args.out_dir / "geomap.json").write_text(json.dumps(meta, indent=1, ensure_ascii=False), encoding="utf-8")
    log.info("wrote geomap.json (%d labels)", len(meta["labels"]))
    if args.style in ("dark", "both"):
        image.save(args.out_dir / meta["texture"], optimize=True)
        log.info("wrote %s (%dx%d)", meta["texture"], args.size, args.size)
    if args.style in ("satellite", "both"):
        if not BLUE_MARBLE.exists():
            BLUE_MARBLE.parent.mkdir(parents=True, exist_ok=True)
            log.info("downloading NASA Blue Marble (~30 MB)")
            urllib.request.urlretrieve(BLUE_MARBLE_URL, BLUE_MARBLE)
        Image.MAX_IMAGE_PIXELS = None   # 21600 x 10800 is a known, trusted file
        satellite = render_satellite(Image.open(BLUE_MARBLE), countries, coastline, args.size,
                                     meta["textureHalfSizeDeg"], meta["centerLat"], meta["centerLon"])
        satellite.save(args.out_dir / SATELLITE_TEXTURE, quality=90, optimize=True)
        log.info("wrote %s (%dx%d)", SATELLITE_TEXTURE, args.size, args.size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
