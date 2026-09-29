import csv
import gzip
import json
import subprocess
import sys
from datetime import datetime, timedelta
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "scripts" / "build_graph_ectrl.py"
REAL_INPUTS = [ROOT / "new_data"] + sorted(p for p in ROOT.glob("20[0-9][0-9]") if p.is_dir())
HAS_REAL_DATA = any(any(p.rglob("Flights_*.csv*")) for p in REAL_INPUTS if p.is_dir())

NODE_KEYS = {"id", "label", "x", "y", "z", "value", "lat", "lon", "city", "country",
             "departures", "arrivals", "avgDepDelayMin", "avgArrDelayMin",
             "scheduledShare", "cargoShare", "segments", "topOperator", "topAcType",
             "monthly", "monthlyDepDelayMin", "monthlyArrDelayMin", "hourly"}
EDGE_KEYS = {"source", "target", "weight", "forward", "backward",
             "avgDistanceNm", "avgDurationMin", "avgDelayMin",
             "scheduledShare", "cargoShare", "segments", "topOperator", "topAcType",
             "monthly", "monthlyDelayMin", "hourly"}

HEADER = ["ECTRL ID", "ADEP", "ADEP Latitude", "ADEP Longitude", "ADES", "ADES Latitude", "ADES Longitude",
          "FILED OFF BLOCK TIME", "FILED ARRIVAL TIME", "ACTUAL OFF BLOCK TIME", "ACTUAL ARRIVAL TIME",
          "AC Type", "AC Operator", "AC Registration", "ICAO Flight Type", "STATFOR Market Segment",
          "Requested FL", "Actual Distance Flown (nm)"]
POS = {"AAAA": (40.0, 20.0), "BBBB": (41.0, 21.0), "CCCC": (42.0, 22.0), "DDDD": (43.0, 23.0),
       "EEEE": (44.0, 24.0), "FFFF": (45.0, 25.0), "ZZZZ": ("", "")}
AIRPORTS = [
    # ident, type, name, lat, lon, elevation_ft, iso_country, municipality, icao_code, iata_code, gps_code
    ("AAAA", "large_airport", "Alpha Intl", 40.0, 20.0, 300, "TR", "Alphaville", "AAAA", "AAA", "AAAA"),
    ("XX-1", "closed", "Old Alpha", 40.0, 20.0, 0, "TR", "Alphaville", "", "", "AAAA"),
    ("BBBB", "large_airport", "Bravo", 41.0, 21.0, 100, "FR", "Bravoville", "BBBB", "BBB", "BBBB"),
    ("CCCC", "medium_airport", "Charlie", 42.0, 22.0, 0, "FR", "", "CCCC", "", "CCCC"),
    # Same ICAO code as the EUROCONTROL airport but 500+ km away: a different place.
    ("DDDD", "heliport", "Faraway Pad", 48.0, 23.0, 0, "FR", "", "", "", "DDDD"),
]


def flights(adep, ades, count, month, dep_delay=10, arr_delay=5, operator="THY", segment="Mainline",
            ac_type="A320", flight_type="S", positions=None):
    positions = positions or POS
    fmt = "%d-%m-%Y %H:%M:%S"
    rows = []
    for i in range(count):
        filed_off = datetime(2025, month, 1 + i % 28, 8)
        filed_arr = filed_off + timedelta(minutes=120)
        rows.append([
            "1", adep, *positions[adep], ades, *positions[ades],
            filed_off.strftime(fmt), filed_arr.strftime(fmt),
            (filed_off + timedelta(minutes=dep_delay)).strftime(fmt),
            (filed_arr + timedelta(minutes=arr_delay)).strftime(fmt),
            ac_type, operator, "REG", flight_type, segment, "300", "500",
        ])
    return rows


def write_flights(path, rows):
    path.parent.mkdir(parents=True, exist_ok=True)
    opener = gzip.open if path.suffix == ".gz" else open
    with opener(path, "wt", encoding="utf-8", newline="") as f:
        w = csv.writer(f, quoting=csv.QUOTE_ALL)
        w.writerow(HEADER)
        w.writerows(rows)


@pytest.fixture(scope="module")
def synthetic(tmp_path_factory):
    """Two monthly files (gz + plain csv), one duplicate period, tiny OurAirports; run the pipeline."""
    base = tmp_path_factory.mktemp("ectrl")
    jan = (flights("AAAA", "BBBB", 40, 1)                      # 1.3/day -> edge kept
           + flights("BBBB", "AAAA", 10, 1, operator="ZZZ")    # anonymised operator
           + flights("AAAA", "CCCC", 5, 1)                     # sparse in Jan ...
           + flights("AAAA", "DDDD", 31, 1)
           + flights("BBBB", "DDDD", 3, 1)                     # never 1/day -> edge dropped
           + flights("AAAA", "EEEE", 31, 1, segment="All-Cargo", flight_type="N")
           + flights("FFFF", "AAAA", 1, 1)                     # FFFF is outside the top 5
           + flights("CCCC", "EEEE", 1, 1, positions=dict(POS, CCCC=("", "")))  # position from OurAirports
           + flights("GGGG", "AAAA", 1, 1, positions=dict(POS, GGGG=("", "")))  # no position anywhere -> dropped
           + flights("ZZZZ", "AAAA", 3, 1)                     # unknown airport -> dropped
           + flights("AAAA", "AAAA", 2, 1))                    # self-loop -> dropped
    moved = dict(POS, AAAA=(40.01, 20.0))
    feb = (flights("CCCC", "AAAA", 30, 2, dep_delay=-4, arr_delay=20, positions=moved)  # ... but 1.07/day in Feb
           + flights("AAAA", "BBBB", 28, 2, positions=moved,     # AAAA position updated in Feb;
                     segment="Not Classified"))                 # source left the segment empty
    write_flights(base / "2025" / "202501" / "Flights_20250101_20250131.csv.gz", jan)
    write_flights(base / "2025" / "202502" / "Flights_20250201_20250228.csv", feb)
    # Same period again under another folder: must be skipped (it would double February).
    write_flights(base / "new_data" / "Flights_20250201_20250228.csv.gz", feb)

    airports_dir = base / "ourairports"
    airports_dir.mkdir()
    with open(airports_dir / "airports.csv", "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f)
        w.writerow(["ident", "type", "name", "latitude_deg", "longitude_deg", "elevation_ft", "iso_country",
                    "municipality", "icao_code", "iata_code", "gps_code"])
        w.writerows(AIRPORTS)
    (airports_dir / "countries.csv").write_text('"code","name"\n"TR","Turkey"\n"FR","France"\n', encoding="utf-8")

    out = base / "out"
    subprocess.run([sys.executable, str(SCRIPT), "--input", str(base / "2025"), str(base / "new_data"),
                    "--top", "5", "--out-dir", str(out), "--airports-dir", str(airports_dir)], check=True)
    nodes = json.loads((out / "nodes_ectrl.json").read_text(encoding="utf-8"))
    edges = json.loads((out / "edges_ectrl.json").read_text(encoding="utf-8"))
    meta = json.loads((out / "meta_ectrl.json").read_text(encoding="utf-8"))
    return {n["id"]: n for n in nodes}, {(e["source"], e["target"]): e for e in edges}, meta


def test_periods_from_file_names_and_duplicates_skipped(synthetic):
    _, _, meta = synthetic
    assert meta["periods"] == ["2025-01", "2025-02"]
    assert meta["periodDays"] == [31, 28]
    # Unknown airport, self-loops and GGGG (no position) dropped; February read once.
    assert meta["periodFlights"] == [122, 58]
    assert meta["segmentCoverage"] == [1.0, round(30 / 58, 3)]
    assert meta["dateFrom"] == "2025-01-01" and meta["dateTo"] == "2025-02-28"


def test_top_n_and_edge_threshold(synthetic):
    nodes, edges, meta = synthetic
    assert set(nodes) == {"AAAA", "BBBB", "CCCC", "DDDD", "EEEE"}
    # BBBB-DDDD never reaches 1 flight/day; AAAA-CCCC does, but only in February.
    assert set(edges) == {("AAAA", "BBBB"), ("AAAA", "CCCC"), ("AAAA", "DDDD"), ("AAAA", "EEEE")}
    assert meta["nodeCount"] == len(nodes) and meta["edgeCount"] == len(edges)


def test_schema(synthetic):
    nodes, edges, _ = synthetic
    for n in nodes.values():
        assert set(n) == NODE_KEYS
    for e in edges.values():
        assert set(e) == EDGE_KEYS


def test_monthly_counts(synthetic):
    nodes, edges, _ = synthetic
    a = nodes["AAAA"]
    # Value counts every flight touching the airport, also to airports outside the graph (FFFF).
    assert a["departures"] == 40 + 5 + 31 + 31 + 28 and a["arrivals"] == 10 + 1 + 30
    assert a["monthly"] == [40 + 10 + 5 + 31 + 31 + 1, 30 + 28]
    assert sum(a["monthly"]) == sum(a["hourly"]) == a["value"]
    ab = edges[("AAAA", "BBBB")]
    assert (ab["weight"], ab["forward"], ab["backward"]) == (78, 68, 10)
    assert ab["monthly"] == [50, 28]
    assert edges[("AAAA", "CCCC")]["monthly"] == [5, 30]


def test_delays_and_hours(synthetic):
    nodes, edges, _ = synthetic
    ac = edges[("AAAA", "CCCC")]
    assert ac["monthlyDelayMin"] == [5.0, 20.0]
    assert ac["avgDelayMin"] == round((5 * 5 + 30 * 20) / 35, 1)
    assert ac["avgDurationMin"] == pytest.approx(round((5 * 115 + 30 * 144) / 35, 1))
    c = nodes["CCCC"]
    assert c["monthlyDepDelayMin"] == [10.0, -4.0] and c["monthlyArrDelayMin"] == [5.0, None]
    # Hours use actual times: CCCC departs 4 min early (07:56), arrivals land at 10:05.
    assert c["hourly"][7] == 30 and c["hourly"][10] == 5
    assert ac["hourly"][7] == 30 and ac["hourly"][8] == 5


def test_labels_and_positions(synthetic):
    nodes, _, _ = synthetic
    a = nodes["AAAA"]
    assert a["label"] == "Alpha Intl (AAA)"  # open airport wins over the closed row with the same gps_code
    assert (a["city"], a["country"]) == ("Alphaville", "Turkey")
    assert nodes["CCCC"]["label"] == "Charlie (CCCC)" and nodes["CCCC"]["city"] is None
    assert nodes["CCCC"]["departures"] == 30 + 1  # includes the flight with empty CCCC coordinates
    assert nodes["DDDD"]["label"] == "DDDD"  # OurAirports row is 500+ km away: not used
    assert nodes["EEEE"]["label"] == "EEEE"  # not in OurAirports
    assert a["lat"] == 40.01  # latest period's position
    assert a["x"] == round(20.0 / 18, 4) and a["z"] == round(40.01 / 18, 4)


def test_shares_and_top_keys(synthetic):
    nodes, edges, _ = synthetic
    ab = edges[("AAAA", "BBBB")]
    assert ab["topOperator"] == "THY"  # ZZZ is anonymised, never the top operator
    ae = edges[("AAAA", "EEEE")]
    assert ae["cargoShare"] == 1.0 and ae["scheduledShare"] == 0.0
    assert ae["segments"] == {"All-Cargo": 31}


def test_unclassified_segment_left_out(synthetic):
    nodes, edges, _ = synthetic
    # February AAAA -> BBBB has no segment: not in segments, not in the cargoShare denominator.
    assert edges[("AAAA", "BBBB")]["segments"] == {"Mainline": 50}
    a = nodes["AAAA"]
    assert "Not Classified" not in a["segments"]
    assert sum(a["segments"].values()) == a["value"] - 28
    assert a["cargoShare"] == round(31 / (a["value"] - 28), 3)


@pytest.mark.skipif(not HAS_REAL_DATA, reason="EUROCONTROL data not present (new_data/ and 20??/ are gitignored)")
def test_real_data(tmp_path):
    """Full run on whatever Flights_* files are on disk; the script validates its own output."""
    subprocess.run([sys.executable, str(SCRIPT), "--out-dir", str(tmp_path)], check=True)
    nodes = json.loads((tmp_path / "nodes_ectrl.json").read_text(encoding="utf-8"))
    edges = json.loads((tmp_path / "edges_ectrl.json").read_text(encoding="utf-8"))
    meta = json.loads((tmp_path / "meta_ectrl.json").read_text(encoding="utf-8"))
    assert 150 <= len(nodes) <= 300
    for n in nodes:
        assert set(n) == NODE_KEYS
        assert n["id"] not in ("ZZZZ", "AFIL") and len(n["id"]) == 4
        assert -10.5 <= n["x"] <= 10.5 and -5.5 <= n["z"] <= 5.5
    for e in edges:
        assert set(e) == EDGE_KEYS
        assert len(e["monthly"]) == len(meta["periods"])
