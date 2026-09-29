#!/usr/bin/env python3
"""Immersive 3D layout: place graph nodes on a shell around the viewer.

Unlike the map view (position = geography), positions here come from the
network structure:
  1. 3D force-directed layout (Fruchterman-Reingold, networkx.spring_layout):
     strongly connected airports attract, unconnected ones repel
  2. each node's direction from the layout centre becomes its direction
     around the viewer; the layout's centre-to-periphery order becomes its
     distance (central hubs closest, periphery farthest)
  3. elevations are squeezed into a comfortable band (no nodes under the
     floor or straight overhead); the biggest hub is rotated to straight ahead
  4. Louvain communities are exported so Unity can colour clusters

Output keeps the nodes.json schema (x, y, z now in metres relative to the
GraphLoader object, which should sit at head height) plus `community`.
edges.json is reused unchanged.

Usage: python scripts/layout_3d.py [--dataset openflights|ectrl] [--seed N]
                                   [--inner M] [--outer M]
"""
import argparse
import json
import logging
import math
import sys
from pathlib import Path

import networkx as nx
import numpy as np

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "data" / "processed"
DATASETS = {
    # name: (nodes in, edges in, nodes out)
    "openflights": ("nodes.json", "edges.json", "nodes_3d.json"),
    "ectrl": ("nodes_ectrl.json", "edges_ectrl.json", "nodes_3d_ectrl.json"),
}

log = logging.getLogger("layout_3d")


def load(dataset):
    nodes_name, edges_name, _ = DATASETS[dataset]
    nodes = json.loads((OUT_DIR / nodes_name).read_text(encoding="utf-8"))
    edges = json.loads((OUT_DIR / edges_name).read_text(encoding="utf-8"))
    return nodes, edges


def build_graph(nodes, edges):
    g = nx.Graph()
    # Insert in a fixed order so the layout only depends on the seed.
    for n in sorted(nodes, key=lambda n: n["id"]):
        g.add_node(n["id"])
    for e in sorted(edges, key=lambda e: (e["source"], e["target"])):
        # log: a 400-flight pair should pull harder than a 1-flight pair, not 400x harder.
        g.add_edge(e["source"], e["target"], w=math.log1p(e["weight"]))
    return g


def force_layout(g, seed, iterations):
    pos = nx.spring_layout(g, dim=3, weight="w", seed=seed, iterations=iterations)
    ids = list(g.nodes)
    xyz = np.array([pos[i] for i in ids], dtype=float)
    xyz -= xyz.mean(axis=0)
    return ids, xyz


def to_shell(xyz, inner, outer, min_elev_deg, max_elev_deg):
    """Map layout points to (x, y, z) metres on a shell around the origin (Unity: y up, z forward)."""
    dist = np.linalg.norm(xyz, axis=1)
    directions = xyz / np.maximum(dist, 1e-9)[:, None]

    # Distance by rank: evenly spread between inner and outer, central nodes closest.
    rank = np.argsort(np.argsort(dist))
    radius = inner + (outer - inner) * rank / max(len(rank) - 1, 1)

    # Squeeze elevation into [min, max] by remapping sin(elevation) linearly;
    # keeps the vertical order of the layout while avoiding floor and zenith.
    lo, hi = math.sin(math.radians(min_elev_deg)), math.sin(math.radians(max_elev_deg))
    sin_elev = lo + (np.clip(directions[:, 1], -1.0, 1.0) + 1.0) * 0.5 * (hi - lo)
    elev = np.arcsin(sin_elev)
    azim = np.arctan2(directions[:, 0], directions[:, 2])
    return radius, elev, azim


def rotate_to_front(azim, index):
    """Rotate all azimuths so node `index` is straight ahead (+Z)."""
    return (azim - azim[index] + math.pi) % (2 * math.pi) - math.pi


def communities(g, seed):
    parts = nx.community.louvain_communities(g, weight="w", seed=seed)
    # Largest community = 0, so colours stay stable-ish across runs.
    parts = sorted(parts, key=lambda c: (-len(c), min(c)))
    return {node: i for i, part in enumerate(parts) for node in part}


def build(dataset, seed, iterations, inner, outer, min_elev, max_elev):
    nodes, edges = load(dataset)
    g = build_graph(nodes, edges)
    ids, xyz = force_layout(g, seed, iterations)
    radius, elev, azim = to_shell(xyz, inner, outer, min_elev, max_elev)

    by_id = {n["id"]: n for n in nodes}
    hub = max(range(len(ids)), key=lambda i: (by_id[ids[i]]["value"], ids[i]))
    azim = rotate_to_front(azim, hub)
    community = communities(g, seed)
    log.info("layout: %d nodes, %d edges, %d communities; hub in front: %s",
             len(ids), g.number_of_edges(), len(set(community.values())), by_id[ids[hub]]["label"])

    out = []
    for i, node_id in enumerate(ids):
        horizontal = radius[i] * math.cos(elev[i])
        node = dict(by_id[node_id])
        node["x"] = round(float(horizontal * math.sin(azim[i])), 4)
        node["y"] = round(float(radius[i] * math.sin(elev[i])), 4)
        node["z"] = round(float(horizontal * math.cos(azim[i])), 4)
        node["community"] = community[node_id]
        out.append(node)
    # Same order as the input file (value descending).
    order = {n["id"]: k for k, n in enumerate(nodes)}
    out.sort(key=lambda n: order[n["id"]])
    return out, edges


def validate(nodes, edges, inner, outer, min_elev, max_elev):
    ids = {n["id"] for n in nodes}
    assert len(ids) == len(nodes), "duplicate node ids"
    for e in edges:
        assert e["source"] in ids and e["target"] in ids, f"dangling edge {e['source']}-{e['target']}"
    lo = math.sin(math.radians(min_elev)) * outer - 1e-3
    hi = math.sin(math.radians(max_elev)) * outer + 1e-3
    for n in nodes:
        r = math.sqrt(n["x"] ** 2 + n["y"] ** 2 + n["z"] ** 2)
        assert inner - 1e-3 <= r <= outer + 1e-3, f"{n['id']} radius {r:.3f} outside shell"
        assert lo <= n["y"] <= hi, f"{n['id']} height {n['y']} outside elevation band"
        assert isinstance(n["community"], int)
    log.info("validated: %d nodes within %.1f-%.1f m", len(nodes), inner, outer)


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--dataset", choices=sorted(DATASETS), default="openflights")
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--iterations", type=int, default=300)
    p.add_argument("--inner", type=float, default=2.0, help="closest node distance from the viewer (m)")
    p.add_argument("--outer", type=float, default=4.0, help="farthest node distance from the viewer (m)")
    p.add_argument("--min-elev", type=float, default=-15.0,
                   help="lowest elevation (deg); with outer=4 m and eyes at 1.5 m, -15 keeps nodes >0.4 m above the floor")
    p.add_argument("--max-elev", type=float, default=55.0, help="highest elevation (deg), avoids straight overhead")
    args = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    nodes, edges = build(args.dataset, args.seed, args.iterations,
                         args.inner, args.outer, args.min_elev, args.max_elev)
    out_path = OUT_DIR / DATASETS[args.dataset][2]
    out_path.write_text(json.dumps(nodes, indent=1, ensure_ascii=False), encoding="utf-8")
    validate(json.loads(out_path.read_text(encoding="utf-8")), edges,
             args.inner, args.outer, args.min_elev, args.max_elev)
    log.info("wrote %s", out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
