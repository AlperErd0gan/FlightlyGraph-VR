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
    assert data["pathSources"] == {"actual": 1, "greatCircle": 1}


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
    assert data["pathSources"] == {"actual": 0, "greatCircle": 2} and data["pointsFile"] is None
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
