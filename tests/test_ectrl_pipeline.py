import json
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
FLIGHTS = ROOT / "new_data" / "Flights_20210201_20210228.csv.gz"
NODES = ROOT / "data" / "processed" / "nodes_ectrl.json"
EDGES = ROOT / "data" / "processed" / "edges_ectrl.json"
META = ROOT / "data" / "processed" / "meta_ectrl.json"

pytestmark = pytest.mark.skipif(not FLIGHTS.exists(), reason="EUROCONTROL data not present (new_data/ is gitignored)")

NODE_KEYS = {"id", "label", "x", "y", "z", "value", "lat", "lon", "city", "country",
             "departures", "arrivals", "avgDepDelayMin", "avgArrDelayMin",
             "scheduledShare", "cargoShare", "segments", "topOperator", "topAcType", "daily", "hourly"}
EDGE_KEYS = {"source", "target", "weight", "forward", "backward",
             "avgDistanceNm", "avgDurationMin", "avgDelayMin",
             "scheduledShare", "cargoShare", "segments", "topOperator", "topAcType", "daily", "hourly"}


@pytest.fixture(scope="module", autouse=True)
def built():
    """Run the pipeline end-to-end so tests check real output."""
    subprocess.run([sys.executable, str(ROOT / "scripts" / "build_graph_ectrl.py")], check=True)


@pytest.fixture(scope="module")
def nodes():
    return json.loads(NODES.read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def edges():
    return json.loads(EDGES.read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def meta():
    return json.loads(META.read_text(encoding="utf-8"))


def test_node_budget(nodes, meta):
    assert 150 <= len(nodes) <= 300
    assert meta["nodeCount"] == len(nodes)


def test_node_schema(nodes):
    for n in nodes:
        assert set(n) == NODE_KEYS
        assert n["id"] != "ZZZZ" and len(n["id"]) == 4
        assert -10.5 <= n["x"] <= 10.5 and -5.5 <= n["z"] <= 5.5
        assert n["departures"] + n["arrivals"] == n["value"]


def test_edges_reference_real_nodes(nodes, edges, meta):
    ids = {n["id"] for n in nodes}
    assert meta["edgeCount"] == len(edges)
    for e in edges:
        assert set(e) == EDGE_KEYS
        assert e["source"] in ids and e["target"] in ids
        assert e["source"] < e["target"]
        assert e["forward"] + e["backward"] == e["weight"] >= 1


def test_no_duplicate_pairs(edges):
    pairs = [(e["source"], e["target"]) for e in edges]
    assert len(pairs) == len(set(pairs))


def test_time_profiles(nodes, edges, meta):
    for item in nodes + edges:
        count = item["value"] if "value" in item else item["weight"]
        assert len(item["daily"]) == meta["days"]
        assert len(item["hourly"]) == 24
        assert sum(item["daily"]) == sum(item["hourly"]) == count
