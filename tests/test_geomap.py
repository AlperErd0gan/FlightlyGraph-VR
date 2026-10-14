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
