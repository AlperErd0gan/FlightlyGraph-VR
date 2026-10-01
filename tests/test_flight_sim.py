import calendar
import csv
import gzip
import json
import subprocess
import sys
from datetime import datetime
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "scripts" / "build_flight_sim.py"
REAL_FLIGHTS = ROOT / "data" / "raw" / "eurocontrol" / "flights" / "2025"
REAL_GRAPH = ROOT / "data" / "processed" / "nodes_ectrl.json"

FLIGHT_HEADER = ["ECTRL ID", "ADEP", "ADEP Latitude", "ADEP Longitude", "ADES", "ADES Latitude", "ADES Longitude",
                 "FILED OFF BLOCK TIME", "FILED ARRIVAL TIME", "ACTUAL OFF BLOCK TIME", "ACTUAL ARRIVAL TIME",
                 "AC Type", "AC Operator", "AC Registration", "ICAO Flight Type", "STATFOR Market Segment",
                 "Requested FL", "Actual Distance Flown (nm)"]
POINT_HEADER = ["ECTRL ID", "Sequence Number", "Time Over", "Flight Level", "Latitude", "Longitude"]
POS = {"AAAA": (40.0, 20.0), "BBBB": (40.0, 30.0), "CCCC": (50.0, 20.0), "DDDD": (45.0, 25.0)}


def flight(fid, adep, ades, off, arr, fl="350"):
    t = lambda hm: f"01-08-2025 {hm}:00"
    return [fid, adep, *POS[adep], ades, *POS[ades], t(off), t(arr), t(off), t(arr),
            "A320", "THY", "REG", "S", "Mainline", fl, "400"]


def write_csv_gz(path, header, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(path, "wt", encoding="utf-8", newline="") as f:
        w = csv.writer(f, quoting=csv.QUOTE_ALL)
        w.writerow(header)
        w.writerows(rows)


@pytest.fixture(scope="module")
def setup(tmp_path_factory):
    base = tmp_path_factory.mktemp("sim")
    nodes = [{"id": i} for i in ("AAAA", "BBBB", "CCCC")]
    edges = [{"source": "AAAA", "target": "BBBB"}, {"source": "AAAA", "target": "CCCC"}]
    (base / "nodes.json").write_text(json.dumps(nodes))
    (base / "edges.json").write_text(json.dumps(edges))
    write_csv_gz(base / "Flights_20250801_20250831.csv.gz", FLIGHT_HEADER, [
        flight("1", "AAAA", "BBBB", "06:30", "08:00"),         # in the window, has a point profile
        flight("2", "BBBB", "AAAA", "05:00", "06:30", "240"),  # already under way at the start
        flight("3", "AAAA", "CCCC", "10:00", "11:00"),         # after the window
        flight("4", "AAAA", "CCCC", "04:00", "05:30"),         # landed before the window
        flight("5", "BBBB", "CCCC", "06:30", "08:00"),         # both graph airports, but not a graph edge
        flight("6", "AAAA", "DDDD", "06:30", "08:00"),         # DDDD is not in the graph
    ])
    write_csv_gz(base / "Flight_Points_Actual_20250801_20250831.csv.gz", POINT_HEADER, [
        # Out of sequence order on purpose: the script sorts by sequence number.
        ["1", "2", "01-08-2025 07:00:00", "200", "40.0", "25.0"],
        ["1", "0", "01-08-2025 06:30:00", "0", "40.0", "20.0"],
        ["1", "1", "01-08-2025 06:40:00", "0", "40.0", "20.0"],
        ["1", "3", "01-08-2025 07:50:00", "0", "40.0", "30.0"],
        ["1", "4", "01-08-2025 08:00:00", "0", "40.0", "30.0"],
        ["3", "0", "01-08-2025 10:00:00", "0", "40.0", "20.0"],  # not a picked flight
    ])
    return base


def run(base, name, *extra):
    out = base / name
    subprocess.run([sys.executable, str(SCRIPT), "--day", "2025-08-01", "--start", "06:00", "--hours", "2",
                    "--flights", str(base / "Flights_20250801_20250831.csv.gz"),
                    "--points", str(base / "Flight_Points_Actual_20250801_20250831.csv.gz"),
                    "--nodes", str(base / "nodes.json"), "--edges", str(base / "edges.json"),
                    "--out", str(out), *extra], check=True)
    data = json.loads(out.read_text(encoding="utf-8"))
    return data, {f["id"]: f for f in data["flights"]}


def test_window_and_graph_filter(setup):
    data, flights = run(setup, "all.json")
    assert set(flights) == {"1", "2"}
    assert data["candidateFlights"] == data["flightCount"] == 2
    assert data["startEpoch"] == calendar.timegm(datetime(2025, 8, 1, 6, 0).timetuple())
    assert data["durationSec"] == 7200
    assert data["pathSources"] == {"actual": 1, "partial": 0, "greatCircle": 1}


def test_actual_profile(setup):
    _, flights = run(setup, "all.json")
    f = flights["1"]
    assert f["path"] == "actual" and (f["from"], f["to"]) == ("AAAA", "BBBB")
    assert f["t"] == [1800, 2400, 3600, 6600, 7200]
    assert (f["dep"], f["arr"]) == (1800, 7200)
    assert f["lon"] == [20.0, 20.0, 25.0, 30.0, 30.0]
    assert f["fl"] == [0, 0, 200, 0, 0]
    # Eastbound along 40N: each leg's initial great-circle course is ~88 deg.
    # Taxi points take the next moving leg's heading, points after landing the one before.
    assert all(85 <= h <= 90 for h in f["hdg"])
    assert f["hdg"][0] == f["hdg"][1] == f["hdg"][2]
    assert f["hdg"][3] == f["hdg"][4] == f["hdg"][2]


def test_great_circle_fallback(setup):
    _, flights = run(setup, "all.json")
    f = flights["2"]
    assert f["path"] == "greatCircle" and (f["from"], f["to"]) == ("BBBB", "AAAA")
    assert (f["dep"], f["arr"]) == (-3600, 1800)  # 05:00 .. 06:30 around a 06:00 start
    assert (f["lat"][0], f["lon"][0]) == POS["BBBB"] and (f["lat"][-1], f["lon"][-1]) == POS["AAAA"]
    assert f["fl"][0] == f["fl"][-1] == 0 and max(f["fl"]) <= 240  # requested FL240
    assert all(250 <= h <= 290 for h in f["hdg"])  # westbound
    assert all(a <= b for a, b in zip(f["t"], f["t"][1:]))
    assert all(39.9 <= la <= 41.0 for la in f["lat"])  # great circle bulges slightly north


def test_no_points_and_max_flights(setup):
    data, flights = run(setup, "gc.json", "--no-points")
    assert data["pathSources"] == {"actual": 0, "partial": 0, "greatCircle": 2} and data["pointsFile"] is None
    data, flights = run(setup, "one.json", "--max-flights", "1")
    assert set(flights) == {"2"}  # earliest off-block kept first
    assert data["candidateFlights"] == 2 and data["flightCount"] == 1


@pytest.mark.skipif(not (REAL_GRAPH.exists() and any(REAL_FLIGHTS.glob("Flights_202508*"))),
                    reason="EUROCONTROL data / graph not present (gitignored)")
def test_real_data(tmp_path):
    out = tmp_path / "flights.json"
    subprocess.run([sys.executable, str(SCRIPT), "--out", str(out), "--max-flights", "200"], check=True)
    data = json.loads(out.read_text(encoding="utf-8"))
    assert data["flightCount"] == 200 and data["candidateFlights"] > 1000


sys.path.insert(0, str(ROOT / "scripts"))
import build_flight_sim as bfs  # noqa: E402


def test_busiest_day_counts_graph_edge_flights(tmp_path):
    def f(fid, adep, ades, day):
        t = f"{day:02d}-08-2025 10:00:00"
        return [fid, adep, *POS[adep], ades, *POS[ades], t, t, t, t, "A320", "THY", "R", "S", "M", "350", "1"]
    rows = [f("1", "AAAA", "BBBB", 1), f("2", "BBBB", "AAAA", 1),
            f("3", "AAAA", "CCCC", 2), f("4", "CCCC", "AAAA", 2), f("5", "AAAA", "BBBB", 2),
            *[f(str(10 + i), "BBBB", "CCCC", 3) for i in range(5)]]   # not a graph edge
    path = tmp_path / "Flights_20250801_20250831.csv.gz"
    write_csv_gz(path, FLIGHT_HEADER, rows)
    day, count = bfs.busiest_day(path, {"AAAA", "BBBB", "CCCC"}, {"AAAA|BBBB", "AAAA|CCCC"})
    assert (day.day, count) == (2, 3)


def test_simplify_keeps_turns_climbs_and_stops():
    # Straight, steady, level: only the ends.
    t = [0, 60, 120, 180, 240]
    lat = [40.0] * 5
    lon = [20.0, 20.5, 21.0, 21.5, 22.0]
    assert bfs.simplify(t, lat, lon, [350] * 5, 3.0, 30) == [0, 4]
    # A stop (taxi): same place for 10 min, then moving: the stop's end is kept.
    t2 = [0, 600, 900, 1200]
    assert 1 in bfs.simplify(t2, [40.0] * 4, [20.0, 20.0, 20.5, 21.0], [0, 0, 100, 200], 3.0, 30)
    # A turn (north, then east) keeps its corner; a climb keeps its top.
    assert 2 in bfs.simplify([0, 60, 120, 180, 240], [40.0, 40.5, 41.0, 41.0, 41.0],
                             [20.0, 20.0, 20.0, 20.7, 21.4], [350] * 5, 3.0, 30)
    assert 2 in bfs.simplify(t, lat, lon, [0, 150, 300, 300, 300], 3.0, 30)
    # max_points: never more, ends kept.
    n = 200
    zig = bfs.simplify(list(range(0, 60 * n, 60)), [40.0 + 0.05 * (i % 2) for i in range(n)],
                       [20.0 + 0.02 * i for i in range(n)], [350] * n, 0.1, 30)
    assert len(zig) <= 30 and zig[0] == 0 and zig[-1] == n - 1


def test_fill_ends_bridges_a_profile_that_starts_far_away():
    import pandas as pd
    w0 = pd.Timestamp("2025-08-01 00:00")
    row = {"off": pd.Timestamp("2025-08-01 06:00"), "arr": pd.Timestamp("2025-08-01 10:00"),
           "ADEP Latitude": 40.6, "ADEP Longitude": -73.8, "ADES Latitude": 51.5, "ADES Longitude": -0.5}
    # Profile only near Europe: from 20W at 08:30 to the destination at 10:00.
    t = [8.5 * 3600, 9.5 * 3600, 10 * 3600]
    lat, lon, fl = [52.0, 51.6, 51.5], [-20.0, -5.0, -0.5], [370, 200, 0]
    t2, lat2, lon2, fl2, filled = bfs.fill_ends(row, w0, t, lat, lon, fl)
    assert filled
    assert (t2[0], lat2[0], lon2[0], fl2[0]) == (6 * 3600, 40.6, -73.8, 0)   # at New York at off-block
    assert all(a <= b for a, b in zip(t2, t2[1:]))
    assert t2[-3:] == t and lon2[-3:] == lon                                  # the real part kept as is
    assert max(fl2[:-3]) <= 370 and min(lon2) >= -73.81                       # climbs to the profile's level
    # A complete profile is left alone.
    t3, *_, filled3 = bfs.fill_ends(row, w0, [6 * 3600, 10 * 3600], [40.6, 51.5], [-73.8, -0.5], [0, 0])
    assert not filled3 and t3 == [6 * 3600, 10 * 3600]
