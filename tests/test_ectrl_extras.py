import csv
import gzip
import json
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "scripts" / "build_ectrl_extras.py"

FLIGHT_HEADER = ["ECTRL ID", "ADEP", "ADEP Latitude", "ADEP Longitude", "ADES", "ADES Latitude", "ADES Longitude",
                 "FILED OFF BLOCK TIME", "FILED ARRIVAL TIME", "ACTUAL OFF BLOCK TIME", "ACTUAL ARRIVAL TIME",
                 "AC Type", "AC Operator", "AC Registration", "ICAO Flight Type", "STATFOR Market Segment",
                 "Requested FL", "Actual Distance Flown (nm)"]


def write_gz(path, header, rows):
    with gzip.open(path, "wt", encoding="utf-8", newline="") as f:
        w = csv.writer(f, quoting=csv.QUOTE_ALL)
        w.writerow(header)
        w.writerows(rows)


def flight(fid, adep, ades):
    return [fid, adep, "0", "0", ades, "0", "0", "", "", "", "", "A320", "THY", "R", "S", "Mainline", "300", "500"]


@pytest.fixture(scope="module")
def extras(tmp_path_factory):
    base = tmp_path_factory.mktemp("extras")
    raw = base / "raw"
    raw.mkdir()
    # Graph: A-B and A-C are edges, B-C is not; D is not a node.
    (base / "nodes.json").write_text(json.dumps([{"id": i} for i in ("AAAA", "BBBB", "CCCC")]))
    (base / "edges.json").write_text(json.dumps([{"source": "AAAA", "target": "BBBB"},
                                                 {"source": "AAAA", "target": "CCCC"}]))
    flights = [flight(str(i), "AAAA", "BBBB") for i in range(1, 6)] + [
        flight("10", "BBBB", "AAAA"),   # reverse direction, same edge
        flight("20", "AAAA", "CCCC"),
        flight("30", "BBBB", "CCCC"),   # not an edge -> ignored
        flight("40", "AAAA", "DDDD"),   # unknown airport -> ignored
    ]
    write_gz(raw / "Flights_20210201_20210228.csv.gz", FLIGHT_HEADER, flights)

    points_header = ["ECTRL ID", "Sequence Number", "Time Over", "Flight Level", "Latitude", "Longitude"]
    actual = [[fid, str(s), "", str(s * 10), str(40 + s), str(20 + s)]
              for fid in ("1", "3", "5", "10", "20") for s in range(100)]  # 100 points, thinned to --max-points
    filed = [[fid, str(s), "", "0", "40", "20"] for fid in ("1", "20") for s in range(3)]
    write_gz(raw / "Flight_Points_Actual_20210201_20210228.csv.gz", points_header, actual)
    write_gz(raw / "Flight_Points_Filed_20210201_20210228.csv.gz", points_header, filed)

    firs_header = ["ECTRL ID", "Sequence Number", "FIR ID", "Entry Time", "Exit Time"]
    crossings = []
    for fid in ("1", "2", "3", "4", "5"):
        crossings += [[fid, "0", "TAXI_OUT", "", ""], [fid, "1", "XXXXFIR", "", ""],
                      [fid, "2", "YYYYFIR", "", ""], [fid, "3", "TAXI_IN", "", ""]]
    # Reverse flight crosses the same FIRs in the other order -> same normalised path.
    crossings += [["10", "1", "YYYYFIR", "", ""], ["10", "2", "XXXXFIR", "", ""]]
    write_gz(raw / "Flight_FIRs_Actual_20210201_20210228.csv.gz", firs_header, crossings)

    bounds_header = ["Airspace ID", "Min Flight Level", "Max Flight Level", "Sequence Number", "Latitude", "Longitude"]
    bounds = [["XXXXFIR", "0", "999", str(s), str(40 + s), "20"] for s in (3, 1, 2)]
    write_gz(raw / "FIR_2102.csv.gz", bounds_header, bounds)

    out = base / "out"
    subprocess.run([sys.executable, str(SCRIPT), "--raw-dir", str(raw), "--nodes", str(base / "nodes.json"),
                    "--edges", str(base / "edges.json"), "--out-dir", str(out),
                    "--samples", "3", "--max-points", "10"], check=True)
    load = lambda name: json.loads((out / name).read_text(encoding="utf-8"))
    return (load("trajectories_ectrl.json"), load("edge_firs_ectrl.json"),
            load("firs_ectrl.json"), load("extras_meta_ectrl.json"))


def test_only_graph_edges(extras):
    trajectories, edge_firs, _, meta = extras
    assert {(t["source"], t["target"]) for t in trajectories} == {("AAAA", "BBBB"), ("AAAA", "CCCC")}
    assert meta["flightsOnEdges"] == 7 and meta["edgesWithFlights"] == 2
    ab = next(t for t in trajectories if t["target"] == "BBBB")
    assert ab["flights"] == 6  # both directions counted


def test_samples_spread_and_thinned(extras):
    trajectories, _, _, _ = extras
    ab = next(t for t in trajectories if t["target"] == "BBBB")
    # 6 flights sorted by id (1..5, 10), 3 evenly spread: first, middle, last.
    assert [s["id"] for s in ab["samples"]] == ["1", "3", "10"]
    first = ab["samples"][0]
    assert len(first["actual"]) == 10
    assert first["actual"][0] == [40.0, 20.0, 0] and first["actual"][-1] == [139.0, 119.0, 990]
    assert len(first["filed"]) == 3
    assert ab["samples"][2]["from"] == "BBBB" and ab["samples"][2]["filed"] == []


def test_fir_crossings(extras):
    _, edge_firs, _, _ = extras
    ab = next(e for e in edge_firs if e["target"] == "BBBB")
    assert ab["flights"] == 6
    assert ab["firs"] == [{"id": "XXXXFIR", "share": 1.0}, {"id": "YYYYFIR", "share": 1.0}]
    # Taxi pseudo-FIRs removed; the reverse flight is read source -> target.
    assert ab["typicalPath"] == ["XXXXFIR", "YYYYFIR"] and ab["typicalPathShare"] == 1.0


def test_fir_polygons_in_sequence_order(extras):
    _, _, firs, _ = extras
    assert firs == [{"id": "XXXXFIR", "minFL": 0, "maxFL": 999, "points": [[41.0, 20.0], [42.0, 20.0], [43.0, 20.0]]}]
