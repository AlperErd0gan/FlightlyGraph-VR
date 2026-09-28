#!/usr/bin/env python3
"""Build a VR-ready airport route graph from OpenFlights data.

End-to-end, no manual steps:
  1. download airports.dat / routes.dat into data/raw/ (skipped if present)
  2. parse, drop airports with invalid lat/lon
  3. keep top-N airports by route-count degree
  4. keep routes whose endpoints both survived; collapse duplicates -> weight
  5. project (lat, lon, alt) -> (x, y, z) in roughly [-10, 10]
  6. write data/processed/nodes.json + edges.json and validate them

Usage: python scripts/build_graph.py [--top N] [--force-download]
"""
import argparse
import csv
import json
import logging
import sys
import urllib.request
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RAW_DIR = ROOT / "data" / "raw"
OUT_DIR = ROOT / "data" / "processed"
BASE_URL = "https://raw.githubusercontent.com/jpatokal/openflights/master/data/"
FILES = ("airports.dat", "routes.dat")

# Column indices (headerless CSV)
A_ID, A_NAME, A_CITY, A_COUNTRY, A_IATA, A_ICAO, A_LAT, A_LON, A_ALT = range(9)
R_SRC, R_SRC_ID, R_DST, R_DST_ID = 2, 3, 4, 5

log = logging.getLogger("build_graph")


def download(force=False):
    RAW_DIR.mkdir(parents=True, exist_ok=True)
    for name in FILES:
        dest = RAW_DIR / name
        if dest.exists() and not force:
            log.info("raw file present, skipping download: %s", dest)
            continue
        log.info("downloading %s", BASE_URL + name)
        urllib.request.urlretrieve(BASE_URL + name, dest)


def load_airports():
    """Return {airport_id: dict}. Drops rows with invalid/missing lat/lon."""
    airports, dropped = {}, 0
    with open(RAW_DIR / "airports.dat", encoding="utf-8", newline="") as f:
        for row in csv.reader(f):
            try:
                lat, lon = float(row[A_LAT]), float(row[A_LON])
                alt = float(row[A_ALT]) if row[A_ALT] not in ("", "\\N") else 0.0
            except (ValueError, IndexError):
                dropped += 1
                continue
            if not (-90 <= lat <= 90 and -180 <= lon <= 180) or (lat == 0 and lon == 0):
                dropped += 1
                continue
            iata = row[A_IATA] if row[A_IATA] not in ("", "\\N") else None
            label = f"{row[A_NAME]} ({iata})" if iata else row[A_NAME]
            airports[row[A_ID]] = dict(id=row[A_ID], label=label, lat=lat, lon=lon, alt=alt)
    log.info("airports: %d valid, %d dropped (invalid lat/lon)", len(airports), dropped)
    return airports


def load_routes(airports):
    """Return set of (src_id, dst_id) distinct directed routes with known endpoints."""
    routes, skipped = set(), 0
    with open(RAW_DIR / "routes.dat", encoding="utf-8", newline="") as f:
        for row in csv.reader(f):
            src, dst = row[R_SRC_ID], row[R_DST_ID]
            if src in airports and dst in airports and src != dst:
                routes.add((src, dst))
            else:
                skipped += 1
    log.info("routes: %d distinct directed, %d skipped (unknown endpoint/self-loop)", len(routes), skipped)
    return routes


def project(airports):
    """Equirectangular, 2:1 aspect: x <- lon in [-10, 10], z <- lat in [-5, 5], y <- altitude.
    Matches a 20 x 10 map quad centred at the origin (see MapPlane.cs)."""
    max_alt = max((a["alt"] for a in airports.values()), default=1.0) or 1.0
    for a in airports.values():
        a["x"] = round(a["lon"] / 18.0, 4)          # [-180,180] -> [-10,10]
        a["z"] = round(a["lat"] / 18.0, 4)          # [-90,90]   -> [-5,5]
        a["y"] = round(a["alt"] / max_alt * 2.0, 4)  # altitude as small vertical offset [0,2]


def build(top_n):
    airports = load_airports()
    routes = load_routes(airports)

    degree = Counter()
    for s, d in routes:
        degree[s] += 1
        degree[d] += 1
    keep = {aid for aid, _ in degree.most_common(top_n)}
    log.info("kept top %d airports by degree; dropped %d", len(keep), len(airports) - len(keep))

    # undirected pair -> number of distinct routes (either direction)
    weights = Counter()
    for s, d in routes:
        if s in keep and d in keep:
            weights[tuple(sorted((s, d)))] += 1
    # drop nodes isolated after filtering (their degree came from dropped airports)
    connected = {aid for pair in weights for aid in pair}
    keep &= connected
    value = degree  # node value = full route-count degree (pre-filter)

    kept = {aid: airports[aid] for aid in keep}
    project(kept)
    nodes = [dict(id=a["id"], label=a["label"], x=a["x"], y=a["y"], z=a["z"], value=value[a["id"]],
                  lat=a["lat"], lon=a["lon"])
             for a in sorted(kept.values(), key=lambda a: -value[a["id"]])]
    edges = [dict(source=s, target=d, weight=w)
             for (s, d), w in sorted(weights.items(), key=lambda kv: -kv[1])]
    return nodes, edges


def validate(nodes_path, edges_path):
    nodes = json.loads(nodes_path.read_text())
    edges = json.loads(edges_path.read_text())
    ids = {n["id"] for n in nodes}
    assert len(ids) == len(nodes), "duplicate node ids"
    for e in edges:
        assert e["source"] in ids and e["target"] in ids, f"dangling edge {e}"
    pairs = [(e["source"], e["target"]) for e in edges]
    assert len(pairs) == len(set(pairs)), "duplicate edge pairs"
    log.info("validated: %d nodes, %d edges", len(nodes), len(edges))


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--top", type=int, default=200, help="airports to keep (150-300)")
    p.add_argument("--force-download", action="store_true")
    args = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    download(args.force_download)
    nodes, edges = build(args.top)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    nodes_path, edges_path = OUT_DIR / "nodes.json", OUT_DIR / "edges.json"
    nodes_path.write_text(json.dumps(nodes, indent=1, ensure_ascii=False))
    edges_path.write_text(json.dumps(edges, indent=1, ensure_ascii=False))
    validate(nodes_path, edges_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
