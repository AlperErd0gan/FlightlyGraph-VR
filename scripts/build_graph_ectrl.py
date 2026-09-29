#!/usr/bin/env python3
"""Build a VR-ready airport flight graph from EUROCONTROL R&D flight data.

Input: new_data/Flights_<from>_<to>.csv.gz (one row per flight, Feb 2021).
Names / city / country / elevation come from OpenFlights data/raw/airports.dat,
joined on ICAO code (run scripts/build_graph.py once to download it).

Steps:
  1. parse flights; drop rows with unknown airport (ZZZZ), missing coords or self-loops
  2. keep top-N airports by flight count (departures + arrivals)
  3. collapse flights between kept airports into one undirected edge per pair
  4. aggregate per node / edge: counts, segments, delays, durations, distance,
     and time profiles (flights per day, flights per UTC hour)
  5. project (lat, lon, alt) -> (x, y, z) with the same projection as build_graph.py
  6. write data/processed/nodes_ectrl.json, edges_ectrl.json, meta_ectrl.json and validate

All times are UTC. Day index 0 of every `daily` array is meta.dateFrom.

Usage: python scripts/build_graph_ectrl.py [--top N] [--flights PATH]
"""
import argparse
import csv
import gzip
import json
import logging
import sys
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NEW_DATA = ROOT / "new_data"
RAW_DIR = ROOT / "data" / "raw"
OUT_DIR = ROOT / "data" / "processed"
DEFAULT_FLIGHTS = NEW_DATA / "Flights_20210201_20210228.csv.gz"

# OpenFlights airports.dat column indices (headerless CSV)
A_NAME, A_CITY, A_COUNTRY, A_IATA, A_ICAO, A_LAT, A_LON, A_ALT = 1, 2, 3, 4, 5, 6, 7, 8

TIME_FMT = "%d-%m-%Y %H:%M:%S"
UNKNOWN_AIRPORT = "ZZZZ"
ANONYMOUS_OPERATOR = "ZZZ"
CARGO_SEGMENT = "All-Cargo"
# Delays / durations beyond this are data errors, not flights.
MAX_ABS_MINUTES = 24 * 60

log = logging.getLogger("build_graph_ectrl")


def parse_time(text):
    try:
        return datetime.strptime(text, TIME_FMT)
    except ValueError:
        return None


def minutes(a, b):
    """b - a in minutes, or None if either is missing or the gap is implausible."""
    if a is None or b is None:
        return None
    m = (b - a).total_seconds() / 60.0
    return m if abs(m) <= MAX_ABS_MINUTES else None


def load_airport_info():
    """Return {icao: dict(name, iata, city, country, alt)} from OpenFlights airports.dat."""
    path = RAW_DIR / "airports.dat"
    if not path.exists():
        log.warning("%s missing; labels fall back to ICAO codes (run build_graph.py first)", path)
        return {}
    info = {}
    with open(path, encoding="utf-8", newline="") as f:
        for row in csv.reader(f):
            icao = row[A_ICAO]
            if len(icao) != 4:
                continue
            try:
                alt = float(row[A_ALT])
            except ValueError:
                alt = 0.0
            info[icao] = dict(
                name=row[A_NAME],
                iata=row[A_IATA] if row[A_IATA] not in ("", "\\N") else None,
                city=row[A_CITY] if row[A_CITY] not in ("", "\\N") else None,
                country=row[A_COUNTRY] if row[A_COUNTRY] not in ("", "\\N") else None,
                alt=alt,
            )
    return info


def load_flights(path):
    """Return list of cleaned flight dicts plus {icao: (lat, lon)}."""
    flights, coords, dropped = [], {}, Counter()
    with gzip.open(path, "rt", encoding="utf-8", newline="") as f:
        for row in csv.DictReader(f):
            adep, ades = row["ADEP"], row["ADES"]
            if UNKNOWN_AIRPORT in (adep, ades):
                dropped["unknown airport"] += 1
                continue
            if adep == ades:
                dropped["self-loop"] += 1
                continue
            try:
                a_lat, a_lon = float(row["ADEP Latitude"]), float(row["ADEP Longitude"])
                d_lat, d_lon = float(row["ADES Latitude"]), float(row["ADES Longitude"])
            except ValueError:
                dropped["missing coords"] += 1
                continue
            coords.setdefault(adep, (a_lat, a_lon))
            coords.setdefault(ades, (d_lat, d_lon))
            try:
                distance = float(row["Actual Distance Flown (nm)"])
            except ValueError:
                distance = None
            flights.append(dict(
                adep=adep,
                ades=ades,
                filed_off=parse_time(row["FILED OFF BLOCK TIME"]),
                filed_arr=parse_time(row["FILED ARRIVAL TIME"]),
                actual_off=parse_time(row["ACTUAL OFF BLOCK TIME"]),
                actual_arr=parse_time(row["ACTUAL ARRIVAL TIME"]),
                ac_type=row["AC Type"],
                operator=row["AC Operator"],
                scheduled=row["ICAO Flight Type"] == "S",
                segment=row["STATFOR Market Segment"],
                distance=distance if distance and distance > 0 else None,
            ))
    log.info("flights: %d kept, dropped %s", len(flights), dict(dropped))
    return flights, coords


def date_window(flights):
    """First/last calendar day of the export, taken from filed off-block times."""
    days = [f["filed_off"].date() for f in flights if f["filed_off"] is not None]
    return min(days), max(days)


def event_time(flight, actual_key, filed_key):
    """Actual time if known, else filed time."""
    return flight[actual_key] or flight[filed_key]


def day_index(t, start, n_days):
    """Day offset from the window start, clamped so off-by-one-day actuals stay in range."""
    return min(max((t.date() - start).days, 0), n_days - 1)


def mean(values):
    return round(sum(values) / len(values), 1) if values else None


def top_key(counter, exclude=()):
    for key, _ in counter.most_common():
        if key and key not in exclude:
            return key
    return None


class Stats:
    """Accumulator shared by nodes and edges."""

    def __init__(self, n_days):
        self.count = 0
        self.scheduled = 0
        self.segments = Counter()
        self.operators = Counter()
        self.ac_types = Counter()
        self.daily = [0] * n_days
        self.hourly = [0] * 24

    def add(self, flight, t, start, n_days):
        self.count += 1
        self.scheduled += flight["scheduled"]
        self.segments[flight["segment"]] += 1
        self.operators[flight["operator"]] += 1
        self.ac_types[flight["ac_type"]] += 1
        if t is not None:
            self.daily[day_index(t, start, n_days)] += 1
            self.hourly[t.hour] += 1

    def common_fields(self):
        return dict(
            scheduledShare=round(self.scheduled / self.count, 3),
            cargoShare=round(self.segments[CARGO_SEGMENT] / self.count, 3),
            segments=dict(self.segments.most_common()),
            topOperator=top_key(self.operators, exclude=(ANONYMOUS_OPERATOR,)),
            topAcType=top_key(self.ac_types),
            daily=self.daily,
            hourly=self.hourly,
        )


def project(lat, lon, alt, max_alt):
    """Same projection as build_graph.py: x = lon/18 in [-10, 10], z = lat/18 in [-5, 5], y = alt in [0, 2]."""
    return round(lon / 18.0, 4), round(alt / max_alt * 2.0, 4), round(lat / 18.0, 4)


def build(flights_path, top_n):
    flights, coords = load_flights(flights_path)
    info = load_airport_info()
    start, end = date_window(flights)
    n_days = (end - start).days + 1

    volume = Counter()
    for f in flights:
        volume[f["adep"]] += 1
        volume[f["ades"]] += 1
    keep = {icao for icao, _ in volume.most_common(top_n)}
    log.info("kept top %d airports by flight count; dropped %d", len(keep), len(volume) - len(keep))

    node_stats = defaultdict(lambda: Stats(n_days))
    departures, arrivals = Counter(), Counter()
    dep_delays, arr_delays = defaultdict(list), defaultdict(list)

    edge_stats = defaultdict(lambda: Stats(n_days))
    forward = Counter()  # flights in source -> target direction (source < target)
    edge_delays, edge_durations, edge_distances = defaultdict(list), defaultdict(list), defaultdict(list)

    for f in flights:
        adep, ades = f["adep"], f["ades"]
        off = event_time(f, "actual_off", "filed_off")
        arr = event_time(f, "actual_arr", "filed_arr")

        # Node stats use every flight touching the airport (also to non-kept airports).
        if adep in keep:
            node_stats[adep].add(f, off, start, n_days)
            departures[adep] += 1
            d = minutes(f["filed_off"], f["actual_off"])
            if d is not None:
                dep_delays[adep].append(d)
        if ades in keep:
            node_stats[ades].add(f, arr, start, n_days)
            arrivals[ades] += 1
            d = minutes(f["filed_arr"], f["actual_arr"])
            if d is not None:
                arr_delays[ades].append(d)

        if adep in keep and ades in keep:
            pair = tuple(sorted((adep, ades)))
            edge_stats[pair].add(f, off, start, n_days)
            if adep == pair[0]:
                forward[pair] += 1
            d = minutes(f["filed_arr"], f["actual_arr"])
            if d is not None:
                edge_delays[pair].append(d)
            d = minutes(f["actual_off"], f["actual_arr"])
            if d is not None and d > 0:
                edge_durations[pair].append(d)
            if f["distance"] is not None:
                edge_distances[pair].append(f["distance"])

    # Drop airports left without any edge between kept airports.
    connected = {icao for pair in edge_stats for icao in pair}
    keep &= connected

    max_alt = max((info.get(i, {}).get("alt", 0.0) for i in keep), default=1.0) or 1.0
    nodes = []
    for icao in sorted(keep, key=lambda i: (-volume[i], i)):
        lat, lon = coords[icao]
        meta = info.get(icao, {})
        code = meta.get("iata") or icao
        x, y, z = project(lat, lon, meta.get("alt", 0.0), max_alt)
        node = dict(
            id=icao,
            label=f"{meta['name']} ({code})" if meta else icao,
            x=x, y=y, z=z,
            value=volume[icao],
            lat=lat, lon=lon,
            city=meta.get("city"),
            country=meta.get("country"),
            departures=departures[icao],
            arrivals=arrivals[icao],
            avgDepDelayMin=mean(dep_delays[icao]),
            avgArrDelayMin=mean(arr_delays[icao]),
        )
        node.update(node_stats[icao].common_fields())
        nodes.append(node)

    edges = []
    for pair, stats in sorted(edge_stats.items(), key=lambda kv: (-kv[1].count, kv[0])):
        edge = dict(
            source=pair[0],
            target=pair[1],
            weight=stats.count,
            forward=forward[pair],
            backward=stats.count - forward[pair],
            avgDistanceNm=mean(edge_distances[pair]),
            avgDurationMin=mean(edge_durations[pair]),
            avgDelayMin=mean(edge_delays[pair]),
        )
        edge.update(stats.common_fields())
        edges.append(edge)

    meta = dict(
        source="EUROCONTROL R&D Archive: " + flights_path.name,
        dateFrom=start.isoformat(),
        dateTo=end.isoformat(),
        days=n_days,
        timezone="UTC",
        totalFlights=len(flights),
        nodeCount=len(nodes),
        edgeCount=len(edges),
    )
    return nodes, edges, meta


def write_records(path, records):
    """One compact JSON object per line: small file, still diff/grep friendly."""
    lines = ",\n".join(json.dumps(r, ensure_ascii=False, separators=(",", ":")) for r in records)
    path.write_text("[\n" + lines + "\n]\n", encoding="utf-8")


def validate(nodes_path, edges_path, meta_path):
    nodes = json.loads(nodes_path.read_text(encoding="utf-8"))
    edges = json.loads(edges_path.read_text(encoding="utf-8"))
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    ids = {n["id"] for n in nodes}
    assert len(ids) == len(nodes), "duplicate node ids"
    for n in nodes:
        assert len(n["daily"]) == meta["days"] and len(n["hourly"]) == 24, f"bad time profile {n['id']}"
        assert sum(n["daily"]) == sum(n["hourly"]) == n["value"], f"time profile != value {n['id']}"
    pairs = set()
    for e in edges:
        assert e["source"] in ids and e["target"] in ids, f"dangling edge {e['source']}-{e['target']}"
        assert e["source"] < e["target"], "edge pair not canonical"
        assert e["forward"] + e["backward"] == e["weight"]
        assert sum(e["daily"]) == sum(e["hourly"]) == e["weight"]
        pairs.add((e["source"], e["target"]))
    assert len(pairs) == len(edges), "duplicate edge pairs"
    log.info("validated: %d nodes, %d edges, %d days", len(nodes), len(edges), meta["days"])


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--top", type=int, default=200, help="airports to keep (150-300)")
    p.add_argument("--flights", type=Path, default=DEFAULT_FLIGHTS, help="EUROCONTROL Flights_*.csv.gz")
    args = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    if not args.flights.exists():
        log.error("flights file not found: %s", args.flights)
        return 1
    nodes, edges, meta = build(args.flights, args.top)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    nodes_path = OUT_DIR / "nodes_ectrl.json"
    edges_path = OUT_DIR / "edges_ectrl.json"
    meta_path = OUT_DIR / "meta_ectrl.json"
    write_records(nodes_path, nodes)
    write_records(edges_path, edges)
    meta_path.write_text(json.dumps(meta, indent=1) + "\n", encoding="utf-8")
    validate(nodes_path, edges_path, meta_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
