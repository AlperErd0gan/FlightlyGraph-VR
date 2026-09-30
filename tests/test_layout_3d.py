import json
import math
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
NODES = ROOT / "data" / "processed" / "nodes.json"
EDGES = ROOT / "data" / "processed" / "edges.json"
NODES_3D = ROOT / "data" / "processed" / "nodes_3d.json"
SCRIPT = ROOT / "scripts" / "layout_3d.py"


def run_layout():
    subprocess.run([sys.executable, str(SCRIPT)], check=True)
    return NODES_3D.read_bytes()


@pytest.fixture(scope="module", autouse=True)
def built():
    return run_layout()


@pytest.fixture(scope="module")
def nodes3d():
    return json.loads(NODES_3D.read_text(encoding="utf-8"))


def test_same_nodes_and_schema(nodes3d):
    source = json.loads(NODES.read_text(encoding="utf-8"))
    assert [n["id"] for n in nodes3d] == [n["id"] for n in source]
    for n, src in zip(nodes3d, source):
        assert set(n) == set(src) | {"community"}
        assert n["label"] == src["label"] and n["value"] == src["value"]


def test_edges_still_reference_nodes(nodes3d):
    ids = {n["id"] for n in nodes3d}
    for e in json.loads(EDGES.read_text(encoding="utf-8")):
        assert e["source"] in ids and e["target"] in ids


def test_shell_and_comfort_band(nodes3d):
    for n in nodes3d:
        r = math.sqrt(n["x"] ** 2 + n["y"] ** 2 + n["z"] ** 2)
        assert 3.0 - 1e-3 <= r <= 8.0 + 1e-3
        # eyes ~1.5 m above floor: keep every node at least 0.4 m above it
        assert n["y"] >= -1.1


def test_biggest_hub_in_front(nodes3d):
    hub = max(nodes3d, key=lambda n: n["value"])
    assert abs(hub["x"]) < 1e-3 and hub["z"] > 0


def test_communities(nodes3d):
    ids = {n["community"] for n in nodes3d}
    assert all(isinstance(c, int) and c >= 0 for c in ids)
    assert ids == set(range(len(ids)))


def test_deterministic(built):
    assert run_layout() == built
