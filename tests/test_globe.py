"""Tests for scripts/build_globe.py (synthetic map data: no download needed)."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "scripts"))
import build_globe as b  # noqa: E402
import build_geomap as g  # noqa: E402


def polygon(name, ring):
    return {"type": "Feature", "properties": {"NAME": name}, "geometry": {"type": "Polygon", "coordinates": [ring]}}


def lines(*features):
    return {"type": "FeatureCollection", "features": list(features)}


def pixel(image, lat, lon):
    x = int((lon + 180.0) / 360.0 * (image.width - 1))
    y = int((90.0 - lat) / 180.0 * (image.height - 1))
    return image.getpixel((x, y))


def test_unwrap_makes_rings_continuous_across_the_dateline():
    ring = [(170.0, 60.0), (179.0, 60.0), (-179.0, 60.0), (-170.0, 62.0)]
    assert b.unwrap(ring) == [(170.0, 60.0), (179.0, 60.0), (181.0, 60.0), (190.0, 62.0)]


def test_render_is_equirectangular_with_land_where_it_belongs():
    france = polygon("France", [[-2, 43], [7, 43], [7, 50], [-2, 50], [-2, 43]])
    # Across the dateline: must be land on both edges, not a band over the whole image.
    chukotka = polygon("Russia", [[170, 62], [-170, 62], [-170, 68], [170, 68], [170, 62]])
    image = b.render(lines(france, chukotka), lines(), lines(), width=720, scale=1)
    assert image.size == (720, 360)
    assert pixel(image, 46.5, 2.5) == g.LAND          # Paris area
    assert pixel(image, -30.0, 0.0) == g.SEA          # South Atlantic
    assert pixel(image, 65.0, 175.0) == g.LAND        # east of 170 E
    assert pixel(image, 65.0, -175.0) == g.LAND       # west of 170 W
    assert pixel(image, 65.0, 0.0) == g.SEA           # no smear across the map
