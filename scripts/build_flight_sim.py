#!/usr/bin/env python3
"""Timed flight paths for the Unity flight simulation (Assets/Scripts/FlightSimulator.cs).

Picks the flights of one time window (default 2025-08-01 06:00-09:00 UTC) that
fly between two airports of the ECTRL graph along one of its edges, and writes a
timed path for each: points with time, latitude, longitude, flight level and
heading (initial great-circle course to the next point, degrees from true north).

Path source, per flight:
  actual       EUROCONTROL actual point profile (Flight_Points_Actual_<month>*),
               used when that month's file is found under data/raw/eurocontrol/
               (or given with --points)
  greatCircle  no point profile: great circle from origin to destination with the
               flight's real off-block / arrival times, taxi on the ground, climb
               to its requested flight level and descent. Timing and direction are
               real, the lateral route is not (no airways, no detours around
               closed airspace such as Ukraine since 2022).

Output: data/processed/flights_ectrl.json
  { source, flightsFile, pointsFile, day, startLabel, startEpoch, durationSec,
    candidateFlights, flightCount, pathSources,
    flights: [{ id, from, to, op, type, path, dep, arr, t[], lat[], lon[], fl[], hdg[] }] }
t / dep / arr are seconds from the window start (startEpoch, Unix time, UTC);
a flight already under way at the start has a negative dep.

Usage: python scripts/build_flight_sim.py [--day 2025-08-01] [--start 06:00] [--hours 3]
           [--max-flights 800] [--points PATH | --no-points]
"""
import argparse
import json
import logging
import math
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parent.parent
PROCESSED = ROOT / "data" / "processed"
ECTRL_DIR = ROOT / "data" / "raw" / "eurocontrol"
FLIGHTS_DIR = ECTRL_DIR / "flights"

TIME_FMT = "%d-%m-%Y %H:%M:%S"
# Great-circle model (used without a point profile).
TAXI_OUT_MIN, TAXI_IN_MIN = 12, 6
CLIMB_FL_PER_MIN, DESCENT_FL_PER_MIN = 20, 15  # about 2,000 / 1,500 ft per minute
DEFAULT_CRUISE_FL, MIN_CRUISE_FL, MAX_CRUISE_FL = 350, 50, 510
GREAT_CIRCLE_STEP_MIN = 5
# Consecutive points closer than this have no meaningful heading (taxi, holding).
MIN_HEADING_KM = 0.5

FLIGHT_COLUMNS = ["ECTRL ID", "ADEP", "ADEP Latitude", "ADEP Longitude", "ADES", "ADES Latitude",
                  "ADES Longitude", "FILED OFF BLOCK TIME", "FILED ARRIVAL TIME", "ACTUAL OFF BLOCK TIME",
                  "ACTUAL ARRIVAL TIME", "AC Type", "AC Operator", "Requested FL"]
POINT_COLUMNS = ["ECTRL ID", "Sequence Number", "Time Over", "Flight Level", "Latitude", "Longitude"]

log = logging.getLogger("build_flight_sim")


# ---------------------------------------------------------------- inputs

def month_file(directory, prefix, day):
    """The <prefix>_<YYYYMM>01_*.csv[.gz] file of day's month under directory, or None."""
    matches = sorted(directory.rglob(f"{prefix}_{day:%Y%m}01_*.csv*")) if directory.is_dir() else []
    return matches[0] if matches else None


def load_graph(nodes_path, edges_path):
    nodes = json.loads(nodes_path.read_text(encoding="utf-8"))
    edges = json.loads(edges_path.read_text(encoding="utf-8"))
    return {n["id"] for n in nodes}, {"|".join(sorted((e["source"], e["target"]))) for e in edges}


def read_flights(path, node_ids, edge_keys, w0, w1):
    """Flights between two graph airports on a graph edge whose off-block..arrival overlaps [w0, w1)."""
    df = pd.read_csv(path, usecols=FLIGHT_COLUMNS, dtype=str, keep_default_na=False)
    t = {c: pd.to_datetime(df[c], format=TIME_FMT, errors="coerce") for c in FLIGHT_COLUMNS if "TIME" in c}
    df["off"] = t["ACTUAL OFF BLOCK TIME"].fillna(t["FILED OFF BLOCK TIME"])
    df["arr"] = t["ACTUAL ARRIVAL TIME"].fillna(t["FILED ARRIVAL TIME"])
    lo = df["ADEP"].where(df["ADEP"] < df["ADES"], df["ADES"])
    hi = df["ADES"].where(df["ADEP"] < df["ADES"], df["ADEP"])
    on_edge = (lo + "|" + hi).isin(edge_keys) & df["ADEP"].isin(node_ids) & df["ADES"].isin(node_ids)
    active = (df["off"] < w1) & (df["arr"] > w0) & (df["arr"] > df["off"])
    df = df[on_edge & active].copy()
    for c in ("ADEP Latitude", "ADEP Longitude", "ADES Latitude", "ADES Longitude"):
        df[c] = pd.to_numeric(df[c], errors="coerce")
    df = df.dropna(subset=["ADEP Latitude", "ADEP Longitude", "ADES Latitude", "ADES Longitude"])
    df["id_num"] = pd.to_numeric(df["ECTRL ID"], errors="coerce")
    return df.sort_values(["off", "id_num", "ECTRL ID"]).reset_index(drop=True)


def evenly(n, k):
    """k indices spread evenly over range(n), first and last included."""
    if n <= k:
        return list(range(n))
    if k == 1:
        return [0]
    return [round(i * (n - 1) / (k - 1)) for i in range(k)]


def read_points(path, wanted):
    """{ectrl_id: DataFrame(time, lat, lon, fl)} in sequence order, for the wanted flights only."""
    parts = []
    for chunk in pd.read_csv(path, usecols=POINT_COLUMNS, dtype=str, keep_default_na=False, chunksize=2_000_000):
        chunk = chunk[chunk["ECTRL ID"].isin(wanted)]
        if len(chunk):
            parts.append(chunk)
    if not parts:
        return {}
    df = pd.concat(parts)
    df = pd.DataFrame({
        "id": df["ECTRL ID"],
        "seq": pd.to_numeric(df["Sequence Number"], errors="coerce"),
        "time": pd.to_datetime(df["Time Over"], format=TIME_FMT, errors="coerce"),
        "fl": pd.to_numeric(df["Flight Level"], errors="coerce"),
        "lat": pd.to_numeric(df["Latitude"], errors="coerce"),
        "lon": pd.to_numeric(df["Longitude"], errors="coerce"),
    }).dropna()
    return {fid: g.sort_values("seq") for fid, g in df.groupby("id")}


# ---------------------------------------------------------------- geometry

def bearing(lat1, lon1, lat2, lon2):
    """Initial great-circle course from point 1 to point 2, degrees from true north."""
    p1, p2, dl = math.radians(lat1), math.radians(lat2), math.radians(lon2 - lon1)
    y = math.sin(dl) * math.cos(p2)
    x = math.cos(p1) * math.sin(p2) - math.sin(p1) * math.cos(p2) * math.cos(dl)
    return (math.degrees(math.atan2(y, x)) + 360.0) % 360.0


def distance_km(lat1, lon1, lat2, lon2):
    p1, p2 = math.radians(lat1), math.radians(lat2)
    a = math.sin((p2 - p1) / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(math.radians(lon2 - lon1) / 2) ** 2
    return 2 * 6371.0 * math.asin(min(1.0, math.sqrt(a)))


def headings(lat, lon):
    """Heading of each point towards the next one; points that do not move (taxi)
    take the heading of the next moving segment, the last point that of the one before."""
    n = len(lat)
    out = [None] * n
    for i in range(n - 1):
        if distance_km(lat[i], lon[i], lat[i + 1], lon[i + 1]) >= MIN_HEADING_KM:
            out[i] = bearing(lat[i], lon[i], lat[i + 1], lon[i + 1])
    nxt = None
    for i in range(n - 1, -1, -1):  # fill from the next moving segment
        if out[i] is None:
            out[i] = nxt
        else:
            nxt = out[i]
    prev = None
    for i in range(n):  # trailing points: from the segment before
        if out[i] is None:
            out[i] = prev
        else:
            prev = out[i]
    return [int(round(h)) % 360 if h is not None else 0 for h in out]


def great_circle(lat1, lon1, lat2, lon2, fractions):
    """Points at the given fractions (0..1) of the great circle from point 1 to point 2."""
    def unit(lat, lon):
        p, l = math.radians(lat), math.radians(lon)
        return np.array([math.cos(p) * math.cos(l), math.cos(p) * math.sin(l), math.sin(p)])

    a, b = unit(lat1, lon1), unit(lat2, lon2)
    omega = math.acos(float(np.clip(a @ b, -1.0, 1.0)))
    out = []
    for f in fractions:
        if omega < 1e-9:
            v = a
        else:
            v = (math.sin((1 - f) * omega) * a + math.sin(f * omega) * b) / math.sin(omega)
        out.append((math.degrees(math.asin(float(np.clip(v[2], -1.0, 1.0)))), math.degrees(math.atan2(v[1], v[0]))))
    return out


def unwrap_lon(lon):
    """Longitudes without jumps > 180 degrees between points (a flight across the antimeridian)."""
    return list(np.degrees(np.unwrap(np.radians(lon)))) if len(lon) > 1 else list(lon)


# ---------------------------------------------------------------- paths

def cruise_level(requested):
    try:
        fl = int(float(requested))
    except ValueError:
        return DEFAULT_CRUISE_FL
    return fl if MIN_CRUISE_FL <= fl <= MAX_CRUISE_FL else DEFAULT_CRUISE_FL


def great_circle_path(row, w0):
    """(t, lat, lon, fl) lists: taxi out, great circle with climb / cruise / descent, taxi in."""
    off = (row["off"] - w0).total_seconds()
    arr = (row["arr"] - w0).total_seconds()
    total = arr - off
    takeoff = off + min(TAXI_OUT_MIN * 60, 0.15 * total)
    landing = arr - min(TAXI_IN_MIN * 60, 0.10 * total)
    airborne = landing - takeoff
    n = max(2, math.ceil(airborne / (GREAT_CIRCLE_STEP_MIN * 60)) + 1)
    fractions = [i / (n - 1) for i in range(n)]
    lat1, lon1 = row["ADEP Latitude"], row["ADEP Longitude"]
    lat2, lon2 = row["ADES Latitude"], row["ADES Longitude"]
    cruise = cruise_level(row["Requested FL"])
    t, lat, lon, fl = [off], [lat1], [lon1], [0]
    for f, (la, lo) in zip(fractions, great_circle(lat1, lon1, lat2, lon2, fractions)):
        ts = takeoff + f * airborne
        level = min(cruise, CLIMB_FL_PER_MIN * (ts - takeoff) / 60, DESCENT_FL_PER_MIN * (landing - ts) / 60)
        t.append(ts)
        lat.append(la)
        lon.append(lo)
        fl.append(max(0, level))
    t.append(arr)
    lat.append(lat2)
    lon.append(lon2)
    fl.append(0)
    return t, lat, lon, fl


def actual_path(points, w0, max_points):
    """(t, lat, lon, fl) lists from a point profile, thinned to max_points (first and last kept)."""
    keep = evenly(len(points), max_points)
    p = points.iloc[keep]
    t = [(x - w0).total_seconds() for x in p["time"]]
    # Time Over is not always monotonic between neighbouring points; never go back in time.
    t = list(np.maximum.accumulate(t))
    return t, list(p["lat"]), list(p["lon"]), [max(0, int(v)) for v in p["fl"]]


def flight_record(row, path_kind, t, lat, lon, fl):
    lon = unwrap_lon(lon)
    return dict(
        id=row["ECTRL ID"],
        **{"from": row["ADEP"]},
        to=row["ADES"],
        op=row["AC Operator"] or None,
        type=row["AC Type"] or None,
        path=path_kind,
        dep=int(round(t[0])),
        arr=int(round(t[-1])),
        t=[int(round(v)) for v in t],
        lat=[round(float(v), 4) for v in lat],
        lon=[round(float(v), 4) for v in lon],
        fl=[int(round(v)) for v in fl],
        hdg=headings(lat, lon),
    )


# ---------------------------------------------------------------- output

def write(path, head, flights):
    """Header fields indented, then one compact flight per line."""
    body = json.dumps(head, ensure_ascii=False, indent=1)
    lines = ",\n".join(json.dumps(f, ensure_ascii=False, separators=(",", ":")) for f in flights)
    path.write_text(body[:-2] + ',\n "flights": [\n' + lines + "\n ]\n}\n", encoding="utf-8")


def validate(path, node_ids, edge_keys):
    data = json.loads(path.read_text(encoding="utf-8"))
    assert data["flightCount"] == len(data["flights"])
    for f in data["flights"]:
        n = len(f["t"])
        assert n >= 2 and len(f["lat"]) == len(f["lon"]) == len(f["fl"]) == len(f["hdg"]) == n, f"bad arrays {f['id']}"
        assert all(a <= b for a, b in zip(f["t"], f["t"][1:])), f"time goes back {f['id']}"
        assert f["dep"] == f["t"][0] and f["arr"] == f["t"][-1]
        assert f["dep"] < data["durationSec"] and f["arr"] > 0, f"outside the window {f['id']}"
        assert f["from"] in node_ids and f["to"] in node_ids
        assert "|".join(sorted((f["from"], f["to"]))) in edge_keys, f"not a graph edge {f['id']}"
        assert all(0 <= h < 360 for h in f["hdg"]) and min(f["fl"]) >= 0
        assert all(-90 <= v <= 90 for v in f["lat"])
    log.info("validated: %d flights (%s)", data["flightCount"], data["pathSources"])


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--day", default="2025-08-01", help="UTC day, YYYY-MM-DD")
    p.add_argument("--start", default="06:00", help="window start, UTC HH:MM")
    p.add_argument("--hours", type=float, default=3.0, help="window length")
    p.add_argument("--max-flights", type=int, default=800, help="flights kept, spread evenly over departure time")
    p.add_argument("--max-points", type=int, default=40, help="points kept per actual profile")
    p.add_argument("--flights", type=Path, default=None, help="Flights_*.csv[.gz] of the month (default: found by --day)")
    p.add_argument("--points", type=Path, default=None,
                   help="Flight_Points_Actual_*.csv[.gz] of the month (default: searched under data/raw/eurocontrol)")
    p.add_argument("--no-points", action="store_true", help="great-circle paths even if a point profile exists")
    p.add_argument("--nodes", type=Path, default=PROCESSED / "nodes_ectrl.json")
    p.add_argument("--edges", type=Path, default=PROCESSED / "edges_ectrl.json")
    p.add_argument("--out", type=Path, default=PROCESSED / "flights_ectrl.json")
    args = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    day = datetime.strptime(args.day, "%Y-%m-%d")
    w0 = datetime.strptime(f"{args.day} {args.start}", "%Y-%m-%d %H:%M")
    w1 = w0 + timedelta(hours=args.hours)
    flights_path = args.flights or month_file(FLIGHTS_DIR, "Flights", day)
    if flights_path is None:
        log.error("no Flights_%s01_*.csv[.gz] under %s", f"{day:%Y%m}", FLIGHTS_DIR)
        return 1
    points_path = None
    if not args.no_points:
        points_path = args.points or month_file(ECTRL_DIR, "Flight_Points_Actual", day)
    if points_path is None:
        log.warning("no Flight_Points_Actual_%s01_* file: all paths are great circles "
                    "(real times, approximate route)", f"{day:%Y%m}")

    node_ids, edge_keys = load_graph(args.nodes, args.edges)
    candidates = read_flights(flights_path, node_ids, edge_keys, w0, w1)
    picked = candidates.iloc[evenly(len(candidates), args.max_flights)]
    log.info("%d flights on graph edges in %s .. %s UTC; keeping %d", len(candidates), w0, w1, len(picked))

    profiles = read_points(points_path, set(picked["ECTRL ID"])) if points_path else {}
    flights, sources = [], {"actual": 0, "greatCircle": 0}
    for _, row in picked.iterrows():
        points = profiles.get(row["ECTRL ID"])
        if points is not None and len(points) >= 2:
            kind, path_ = "actual", actual_path(points, w0, args.max_points)
        else:
            kind, path_ = "greatCircle", great_circle_path(row, w0)
        flights.append(flight_record(row, kind, *path_))
        sources[kind] += 1

    head = dict(
        source="EUROCONTROL R&D Archive",
        flightsFile=flights_path.name,
        pointsFile=points_path.name if points_path else None,
        day=args.day,
        startLabel=f"{w0:%Y-%m-%d %H:%M} UTC",
        startEpoch=int(w0.replace(tzinfo=timezone.utc).timestamp()),
        durationSec=int(round(args.hours * 3600)),
        candidateFlights=len(candidates),
        flightCount=len(flights),
        pathSources=sources,
        pointFormat="t: seconds from startEpoch; fl: flight level (100 ft); hdg: degrees from true north to the next point",
    )
    args.out.parent.mkdir(parents=True, exist_ok=True)
    write(args.out, head, flights)
    validate(args.out, node_ids, edge_keys)
    log.info("wrote %s (%.1f MB)", args.out, args.out.stat().st_size / 1e6)
    return 0


if __name__ == "__main__":
    sys.exit(main())
