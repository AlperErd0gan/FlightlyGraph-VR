"""Tests for scripts/build_geomap.py (synthetic map data: no download needed)."""
import json
import math
import random
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))
import build_geomap as g  # noqa: E402


def square(lon, lat, half):
    return [[lon - half, lat - half], [lon + half, lat - half], [lon + half, lat + half], [lon - half, lat + half],
            [lon - half, lat - half]]


def feature(name, ring, **props):
    p = {"NAME": name, "TYPE": "Sovereign country", "LABELRANK": 2, "MIN_LABEL": 2,
         "LABEL_X": ring[0][0] + 1, "LABEL_Y": ring[0][1] + 1}
    p.update(props)
    return {"type": "Feature", "properties": p, "geometry": {"type": "Polygon", "coordinates": [ring]}}


def test_projection_keeps_distance_and_direction_from_centre():
    random.seed(1)
    lat0, lon0 = 44.0, 9.0
    assert g.project(lat0, lon0, lat0, lon0) == pytest.approx((0.0, 0.0), abs=1e-9)
    x, y = g.project(54.0, 9.0, lat0, lon0)                     # 10 deg due north
    assert (x, y) == pytest.approx((0.0, 10.0), abs=1e-6)
    x, y = g.project(34.0, 9.0, lat0, lon0)                     # due south
    assert (x, y) == pytest.approx((0.0, -10.0), abs=1e-6)
    assert g.project(44.0, 20.0, lat0, lon0)[0] > 0             # east is +x
    for _ in range(200):
        lat, lon = random.uniform(-60, 80), random.uniform(-120, 140)
        x, y = g.project(lat, lon, lat0, lon0)
        assert math.hypot(x, y) == pytest.approx(g.angular_distance(lat, lon, lat0, lon0), abs=1e-6)


def test_enclosing_cap_holds_all_points_and_is_tight():
    points = [(40.0, 0.0), (50.0, 0.0), (45.0, 7.0), (45.0, -7.0)]
    lat, lon, r = g.enclosing_cap(points)
    assert all(g.angular_distance(lat, lon, a, b) <= r + 1e-9 for a, b in points)
    assert r == pytest.approx(5.0, abs=0.3)
    assert lat == pytest.approx(45.0, abs=0.5) and lon == pytest.approx(0.0, abs=0.5)


def test_build_fits_region_airports_and_leaves_the_rest_outside():
    airports = [("A", 40.0, 0.0), ("B", 50.0, 10.0), ("C", 45.0, -5.0), ("FAR", 40.6, -73.8)]
    land = {"features": [feature("Landia", square(5.0, 45.0, 3.0)),
                         feature("Tiny", square(0.0, 40.0, 0.05)),
                         feature("Colony", square(8.0, 48.0, 1.0), TYPE="Dependency")]}
    empty = {"features": []}
    meta, image, outside = g.build(airports, land, empty, empty, region=(-20, 30, 30, 60), margin=1.0, size=128, scale=1)

    assert [a[0] for a in outside] == ["FAR"]
    for _, lat, lon in airports[:3]:
        assert g.angular_distance(lat, lon, meta["centerLat"], meta["centerLon"]) <= meta["radiusDeg"]
    assert meta["textureHalfSizeDeg"] > meta["radiusDeg"]
    assert image.size == (128, 128)
    # Labels: the country, not the microstate or the dependency.
    assert [l["name"] for l in meta["labels"]] == ["Landia"]

    # The land square is drawn where the projection puts it; sea far from it.
    half = meta["textureHalfSizeDeg"]
    x, y = g.project(45.0, 5.0, meta["centerLat"], meta["centerLon"])
    px = int((x + half) / (2 * half) * 128)
    py = int((half - y) / (2 * half) * 128)
    assert image.getpixel((px, py)) == g.LAND
    assert image.getpixel((1, 1)) == g.SEA
    json.dumps(meta)  # serialisable


def test_main_writes_outputs(tmp_path, monkeypatch):
    nodes = tmp_path / "nodes.json"
    nodes.write_text(json.dumps([{"id": "A", "lat": 40.0, "lon": 0.0}, {"id": "B", "lat": 50.0, "lon": 10.0}]))
    data = {"features": [feature("Landia", square(5.0, 45.0, 3.0))]}
    monkeypatch.setattr(g, "download", lambda name, directory=None: _write(tmp_path / f"{name}.geojson", data))
    assert g.main(["--nodes", str(nodes), "--out-dir", str(tmp_path), "--size", "64"]) == 0
    meta = json.loads((tmp_path / "geomap.json").read_text())
    assert (tmp_path / meta["texture"]).exists()
    assert {"centerLat", "centerLon", "radiusDeg", "textureHalfSizeDeg", "labels"} <= set(meta)


def _write(path, data):
    path.write_text(json.dumps(data))
    return path


def test_unproject_grid_inverts_project():
    import numpy as np
    random.seed(2)
    lat0, lon0 = 44.18, 8.8
    pts = [(random.uniform(10, 75), random.uniform(-60, 80)) for _ in range(100)]
    xy = [g.project(lat, lon, lat0, lon0) for lat, lon in pts]
    lat, lon = g.unproject_grid(np.array([p[0] for p in xy]), np.array([p[1] for p in xy]), lat0, lon0)
    for (la, lo), a, b in zip(pts, lat, lon):
        assert a == pytest.approx(la, abs=1e-6) and b == pytest.approx(lo, abs=1e-6)


BOX = (-40.0, 80.0, 0.0, 85.0)   # lon_min, lon_max, lat_min, lat_max of the synthetic image


def _world(box=BOX, w=600, h=425):
    """Synthetic equirectangular image of `box`: red = longitude, green = latitude (rounded, ~0.5 / 0.3 deg steps)."""
    import numpy as np
    lon = box[0] + (np.arange(w) + 0.5) / w * (box[1] - box[0])
    lat = box[3] - (np.arange(h) + 0.5) / h * (box[3] - box[2])
    img = np.zeros((h, w, 3), dtype=np.uint8)
    img[..., 0] = np.round((lon - box[0]) / (box[1] - box[0]) * 255)[None, :]
    img[..., 1] = np.round((lat - box[2]) / (box[3] - box[2]) * 255)[:, None]
    return img


def test_reproject_samples_the_right_place():
    import numpy as np
    lat0, lon0, half, size = 44.0, 9.0, 30.0, 64
    out = g.reproject(_world(), BOX, size, half, lat0, lon0)
    lon_step = (BOX[1] - BOX[0]) / 255
    lat_step = (BOX[3] - BOX[2]) / 255
    for px, py in [(32, 32), (10, 10), (50, 40), (5, 60), (60, 5)]:
        cx = -half + (px + 0.5) * 2 * half / size
        cy = half - (py + 0.5) * 2 * half / size
        lat, lon = (float(v) for v in g.unproject_grid(np.array(cx), np.array(cy), lat0, lon0))
        r, gr, _ = out[py, px]
        # Rounded twice (source, output): within one colour step.
        assert BOX[0] + r * lon_step == pytest.approx(lon, abs=lon_step), (px, py)
        assert BOX[2] + gr * lat_step == pytest.approx(lat, abs=lat_step), (px, py)


def test_render_satellite_crops_dims_and_keeps_size():
    from PIL import Image
    world = Image.fromarray(_world((-180.0, 180.0, -90.0, 90.0), 720, 360))
    image = g.render_satellite(world, {"features": []}, {"features": []}, 64, 30.0, 44.0, 9.0, scale=1)
    assert image.size == (64, 64)
    assert max(image.getextrema(), key=lambda e: e[1])[1] <= round(255 * g.SATELLITE_DIM) + 1
