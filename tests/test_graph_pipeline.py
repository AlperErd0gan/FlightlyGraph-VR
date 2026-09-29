import json
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
NODES = ROOT / "data" / "processed" / "nodes.json"
EDGES = ROOT / "data" / "processed" / "edges.json"


@pytest.fixture(scope="module", autouse=True)
def built():
    """Run the pipeline end-to-end so tests check real output."""
    subprocess.run([sys.executable, str(ROOT / "scripts" / "build_graph.py")], check=True)


@pytest.fixture(scope="module")
def nodes():
    return json.loads(NODES.read_text())


@pytest.fixture(scope="module")
def edges():
    return json.loads(EDGES.read_text())


def test_files_exist_and_are_json():
    assert NODES.exists() and EDGES.exists()
    assert isinstance(json.loads(NODES.read_text()), list)
    assert isinstance(json.loads(EDGES.read_text()), list)


def test_node_budget(nodes):
    assert 150 <= len(nodes) <= 300


def test_node_schema(nodes):
    for n in nodes:
        assert set(n) == {"id", "label", "x", "y", "z", "value", "lat", "lon"}
        assert isinstance(n["id"], str) and isinstance(n["label"], str)
        assert all(isinstance(n[k], (int, float)) for k in ("x", "y", "z", "lat", "lon"))
        assert isinstance(n["value"], int)
        assert all(-10.5 <= n[k] <= 10.5 for k in "xyz")
        assert -90 <= n["lat"] <= 90 and -180 <= n["lon"] <= 180


def test_unique_node_ids(nodes):
    ids = [n["id"] for n in nodes]
    assert len(ids) == len(set(ids))


def test_edges_reference_real_nodes(nodes, edges):
    ids = {n["id"] for n in nodes}
    for e in edges:
        assert set(e) == {"source", "target", "weight"}
        assert e["source"] in ids and e["target"] in ids
        assert isinstance(e["weight"], int) and e["weight"] >= 1


def test_no_duplicate_pairs(edges):
    pairs = [(e["source"], e["target"]) for e in edges]
    assert len(pairs) == len(set(pairs))
    undirected = {frozenset(p) for p in pairs}
    assert len(undirected) == len(pairs), "parallel edges in opposite directions"
