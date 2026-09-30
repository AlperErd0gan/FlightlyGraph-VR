#!/usr/bin/env python3
"""Extra EUROCONTROL layers for an existing ECTRL graph, as separate files.

The graph (nodes_ectrl.json / edges_ectrl.json, e.g. built on another machine
from 2020-2025 data) is read only and left untouched. From one month of the
EUROCONTROL R&D export (the files next to Flights_*.csv.gz) this script adds
what the graph does not have, linked to its edges by the (source, target) pair:

  trajectories_ectrl.json  per edge: a few sample flights with their actual and
                           filed 3D paths ([lat, lon, flight level] points)
  edge_firs_ectrl.json     per edge: which FIRs (flight information regions)
                           its flights cross, with the share of flights, and the
                           most common FIR sequence from source to target
  firs_ectrl.json          FIR boundary polygons (one AIRAC cycle), a map layer
  extras_meta_ectrl.json   inputs, period and coverage

Only flights between two graph airports that are also an edge of the graph are
used; flights are matched to edges in either direction. All data stays under
the EUROCONTROL research licence (the *_ectrl.json names are gitignored).

Usage: python scripts/build_ectrl_extras.py [--raw-dir DIR] [--flights PATH]
           [--nodes PATH] [--edges PATH] [--out-dir DIR] [--samples 3] [--max-points 60]
"""
import argparse
import csv
import gzip
import json
import logging
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROCESSED = ROOT / "data" / "processed"

# Pseudo FIR ids in Flight_FIRs_*: ground phases and unknown airspace, not regions.
PSEUDO_FIRS = {"TAXI_OUT", "TAXI_IN", "FIR_UNK"}
# FIRs crossed by fewer flights than this share of an edge are left out of its list.
MIN_FIR_SHARE = 0.05

log = logging.getLogger("build_ectrl_extras")


def open_text(path):
    return gzip.open(path, "rt", encoding="utf-8", newline="") if path.suffix == ".gz" \
        else open(path, encoding="utf-8", newline="")


def find_one(raw_dir, pattern):
    """The single file matching pattern (the last one by name if several, e.g. AIRAC cycles)."""
    matches = sorted(raw_dir.glob(pattern))
    if not matches:
        raise FileNotFoundError(f"no {pattern} in {raw_dir}")
    return matches[-1]


def load_graph(nodes_path, edges_path):
    nodes = json.loads(nodes_path.read_text(encoding="utf-8"))
    edges = json.loads(edges_path.read_text(encoding="utf-8"))
    edge_keys = {tuple(sorted((e["source"], e["target"]))) for e in edges}
    log.info("graph: %d nodes, %d edges (read only)", len(nodes), len(edge_keys))
    return {n["id"] for n in nodes}, edge_keys


def flights_on_edges(flights_path, node_ids, edge_keys):
    """{ectrl_id: (adep, ades)} for flights whose airport pair is a graph edge."""
    out = {}
    with open_text(flights_path) as f:
        for row in csv.DictReader(f):
            adep, ades = row["ADEP"], row["ADES"]
            if adep in node_ids and ades in node_ids and tuple(sorted((adep, ades))) in edge_keys:
                out[row["ECTRL ID"]] = (adep, ades)
    log.info("flights on graph edges: %d", len(out))
    return out


def pick_samples(flights, per_edge):
    """Evenly spread, deterministic sample of flight ids per edge (sorted by ECTRL ID)."""
    by_edge = defaultdict(list)
    for fid, (adep, ades) in flights.items():
        by_edge[tuple(sorted((adep, ades)))].append(fid)
    samples = {}
    for key, ids in by_edge.items():
        ids.sort(key=lambda s: (len(s), s))  # numeric order for numeric ids
        if len(ids) <= per_edge:
            samples[key] = ids
        else:
            step = (len(ids) - 1) / (per_edge - 1) if per_edge > 1 else 0
            samples[key] = [ids[round(i * step)] for i in range(per_edge)]
    return by_edge, samples


def read_points(path, wanted):
    """{ectrl_id: [[lat, lon, fl], ...]} in sequence order, for the wanted ids only."""
    points = defaultdict(list)
    with open_text(path) as f:
        for row in csv.DictReader(f):
            fid = row["ECTRL ID"]
            if fid not in wanted:
                continue
            try:
                seq = int(row["Sequence Number"])
                lat, lon = float(row["Latitude"]), float(row["Longitude"])
                fl = int(float(row["Flight Level"]))
            except ValueError:
                continue
            points[fid].append((seq, [round(lat, 4), round(lon, 4), fl]))
    return {fid: [p for _, p in sorted(pts)] for fid, pts in points.items()}


def thin(points, max_points):
    """At most max_points, evenly spread, always keeping the first and last point."""
    if len(points) <= max_points:
        return points
    step = (len(points) - 1) / (max_points - 1)
    return [points[round(i * step)] for i in range(max_points)]


def read_fir_paths(path, flights):
    """{ectrl_id: [fir, ...]} in crossing order, pseudo FIRs and repeats removed."""
    rows = defaultdict(list)
    with open_text(path) as f:
        for row in csv.DictReader(f):
            fid = row["ECTRL ID"]
            if fid in flights and row["FIR ID"] not in PSEUDO_FIRS:
                rows[fid].append((int(row["Sequence Number"]), row["FIR ID"]))
    paths = {}
    for fid, seq in rows.items():
        path_ = []
        for _, fir in sorted(seq):
            if not path_ or path_[-1] != fir:
                path_.append(fir)
        paths[fid] = path_
    return paths


def build_edge_firs(by_edge, flights, fir_paths):
    out = []
    for key in sorted(by_edge):
        ids = [fid for fid in by_edge[key] if fid in fir_paths]
        if not ids:
            continue
        crossed = Counter()
        sequences = Counter()
        for fid in ids:
            path_ = fir_paths[fid]
            crossed.update(set(path_))
            # Normalise direction so every sequence reads source -> target.
            sequences[tuple(path_ if flights[fid][0] == key[0] else reversed(path_))] += 1
        firs = [dict(id=fir, share=round(n / len(ids), 3))
                for fir, n in sorted(crossed.items(), key=lambda kv: (-kv[1], kv[0]))
                if n / len(ids) >= MIN_FIR_SHARE]
        typical, typical_n = max(sequences.items(), key=lambda kv: (kv[1], kv[0]))
        out.append(dict(source=key[0], target=key[1], flights=len(ids), firs=firs,
                        typicalPath=list(typical), typicalPathShare=round(typical_n / len(ids), 3)))
    return out


def build_trajectories(by_edge, samples, flights, actual, filed, max_points):
    out = []
    for key in sorted(samples):
        entries = []
        for fid in samples[key]:
            if fid not in actual and fid not in filed:
                continue
            adep, ades = flights[fid]
            entries.append({"id": fid, "from": adep, "to": ades,
                            "actual": thin(actual.get(fid, []), max_points),
                            "filed": thin(filed.get(fid, []), max_points)})
        if entries:
            out.append(dict(source=key[0], target=key[1], flights=len(by_edge[key]), samples=entries))
    return out


def build_firs(path):
    polygons = defaultdict(list)
    with open_text(path) as f:
        for row in csv.DictReader(f):
            key = (row["Airspace ID"], int(row["Min Flight Level"]), int(row["Max Flight Level"]))
            polygons[key].append((int(row["Sequence Number"]),
                                  [round(float(row["Latitude"]), 4), round(float(row["Longitude"]), 4)]))
    return [dict(id=fir, minFL=lo, maxFL=hi, points=[p for _, p in sorted(pts)])
            for (fir, lo, hi), pts in sorted(polygons.items())]


def write_records(path, records):
    """One compact JSON object per line, like the other *_ectrl.json files."""
    lines = ",\n".join(json.dumps(r, ensure_ascii=False, separators=(",", ":")) for r in records)
    path.write_text("[\n" + lines + "\n]\n", encoding="utf-8")


def validate(edge_keys, trajectories, edge_firs, firs, per_edge, max_points):
    for rec in trajectories + edge_firs:
        assert (rec["source"], rec["target"]) in edge_keys, f"unknown edge {rec['source']}-{rec['target']}"
    for rec in trajectories:
        assert 1 <= len(rec["samples"]) <= per_edge
        for s in rec["samples"]:
            assert len(s["actual"]) <= max_points and len(s["filed"]) <= max_points
    for rec in edge_firs:
        assert all(0 < f["share"] <= 1 for f in rec["firs"])
        assert not PSEUDO_FIRS & set(rec["typicalPath"])
    assert all(len(p["points"]) >= 3 for p in firs), "FIR polygon with < 3 points"
    log.info("validated: %d edges with trajectories, %d with FIRs, %d FIR polygons",
             len(trajectories), len(edge_firs), len(firs))


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--raw-dir", type=Path, default=ROOT / "data" / "raw" / "eurocontrol" / "other" / "202102",
                   help="folder with one month of Flight_Points_*, Flight_FIRs_*, FIR_* files")
    p.add_argument("--flights", type=Path, default=None,
                   help="the Flights_*.csv[.gz] of the same month; default: the one in --raw-dir")
    p.add_argument("--nodes", type=Path, default=PROCESSED / "nodes_ectrl.json")
    p.add_argument("--edges", type=Path, default=PROCESSED / "edges_ectrl.json")
    p.add_argument("--out-dir", type=Path, default=PROCESSED)
    p.add_argument("--samples", type=int, default=3, help="sample flights per edge")
    p.add_argument("--max-points", type=int, default=60, help="points kept per trajectory")
    args = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    flights_path = args.flights or find_one(args.raw_dir, "Flights_*.csv*")
    actual_path = find_one(args.raw_dir, "Flight_Points_Actual_*.csv*")
    filed_path = find_one(args.raw_dir, "Flight_Points_Filed_*.csv*")
    fir_paths_path = find_one(args.raw_dir, "Flight_FIRs_Actual_*.csv*")
    fir_bounds_path = find_one(args.raw_dir, "FIR_*.csv*")

    node_ids, edge_keys = load_graph(args.nodes, args.edges)
    flights = flights_on_edges(flights_path, node_ids, edge_keys)
    by_edge, samples = pick_samples(flights, args.samples)
    wanted = {fid for ids in samples.values() for fid in ids}
    log.info("reading trajectories of %d sample flights", len(wanted))
    actual = read_points(actual_path, wanted)
    filed = read_points(filed_path, wanted)
    log.info("reading FIR crossings")
    fir_paths = read_fir_paths(fir_paths_path, flights)

    trajectories = build_trajectories(by_edge, samples, flights, actual, filed, args.max_points)
    edge_firs = build_edge_firs(by_edge, flights, fir_paths)
    firs = build_firs(fir_bounds_path)
    validate(edge_keys, trajectories, edge_firs, firs, args.samples, args.max_points)

    args.out_dir.mkdir(parents=True, exist_ok=True)
    write_records(args.out_dir / "trajectories_ectrl.json", trajectories)
    write_records(args.out_dir / "edge_firs_ectrl.json", edge_firs)
    write_records(args.out_dir / "firs_ectrl.json", firs)
    meta = dict(
        source="EUROCONTROL R&D Archive",
        files=[pth.name for pth in (flights_path, actual_path, filed_path, fir_paths_path, fir_bounds_path)],
        graph=dict(nodes=args.nodes.name, edges=args.edges.name, edgeCount=len(edge_keys)),
        flightsOnEdges=len(flights),
        edgesWithFlights=len(by_edge),
        samplesPerEdge=args.samples,
        maxPointsPerTrajectory=args.max_points,
        minFirShare=MIN_FIR_SHARE,
        pointFormat="[lat, lon, flightLevel]",
        firPointFormat="[lat, lon]",
    )
    (args.out_dir / "extras_meta_ectrl.json").write_text(json.dumps(meta, indent=1) + "\n", encoding="utf-8")
    log.info("wrote 4 files to %s", args.out_dir)
    return 0


if __name__ == "__main__":
    sys.exit(main())
