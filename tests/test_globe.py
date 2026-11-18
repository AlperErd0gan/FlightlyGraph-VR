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


def test_world_labels_keep_big_countries_and_skip_small_or_dependent_ones():
    def country(name, ring, **props):
        f = polygon(name, ring)
        f["properties"].update(props)
        return f
    big = [[-2, 43], [7, 43], [7, 50], [-2, 50], [-2, 43]]          # ~500 000 km2
    tiny = [[7.4, 43.7], [7.45, 43.7], [7.45, 43.75], [7.4, 43.75], [7.4, 43.7]]
    countries = lines(
        country("France", big, LABEL_X=2.5, LABEL_Y=46.5, LABELRANK=2, MIN_LABEL=1.7),
        country("Monaco", tiny, LABEL_X=7.42, LABEL_Y=43.73, LABELRANK=6, MIN_LABEL=4),
        country("Greenland", big, LABEL_X=-41.0, LABEL_Y=74.0, LABELRANK=2, MIN_LABEL=1.7, TYPE="Dependency"),
        country("Minor", big, LABEL_X=0.0, LABEL_Y=0.0, LABELRANK=9, MIN_LABEL=8),
        country("Brazil", big, LABEL_X=-49.6, LABEL_Y=-12.1, LABELRANK=1, MIN_LABEL=1),
    )
    labels = b.world_labels(countries)
    assert [l["name"] for l in labels] == ["Brazil", "France"]       # by rank, then name
    assert labels[1] == {"name": "France", "lat": 46.5, "lon": 2.5, "rank": 2}
