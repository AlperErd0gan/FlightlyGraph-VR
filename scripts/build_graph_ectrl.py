#!/usr/bin/env python3
"""Build a time-aware VR airport graph from EUROCONTROL R&D flight data.

Input: any number of monthly EUROCONTROL `Flights_<YYYYMMDD>_<YYYYMMDD>.csv[.gz]`
files, found recursively under the --input paths (default: new_data/ and every
year folder 20??/ in the repo). Each file is one period on the time axis; a
period found twice is read once. Airport name / IATA / city / country /
elevation come from OurAirports (downloaded to data/raw/ourairports/ on first
run), joined on ICAO code.

Steps:
  1. read each file; drop flights with an unknown airport (ZZZZ / AFIL),
     self-loops, missing coordinates or no usable off-block / arrival time
  2. aggregate per directed airport pair and period (counts, delays, durations,
     distances) plus all-period operator / aircraft / segment / hour counts
  3. keep the top-N airports by flight count over all periods
  4. keep pairs between them that reach --min-daily flights per day in at least
     one period; collapse both directions into one undirected edge
  5. project (lat, lon, alt) -> (x, y, z) with the same projection as build_graph.py
  6. write data/processed/nodes_ectrl.json, edges_ectrl.json, meta_ectrl.json and validate

Time axis: meta.periods[i] is the month of monthly[i] in every node and edge,
meta.periodDays[i] its length in days, so monthly[i] / periodDays[i] is flights
per day. A flight belongs to the period of the file it is in. All times are UTC.

Usage: python scripts/build_graph_ectrl.py [--input PATH ...] [--top N] [--min-daily F]
"""
import argparse
import json
import logging
import math
import re
import sys
import urllib.request
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path

import pandas as pd

ROOT = Path(__file__).resolve().parent.parent
OUT_DIR = ROOT / "data" / "processed"
AIRPORTS_DIR = ROOT / "data" / "raw" / "ourairports"
OURAIRPORTS_URL = "https://davidmegginson.github.io/ourairports-data/"
OURAIRPORTS_FILES = ("airports.csv", "countries.csv")

FILE_RE = re.compile(r"^Flights_(\d{8})_(\d{8})\.csv(\.gz)?$")
TIME_FMT = "%d-%m-%Y %H:%M:%S"
UNKNOWN_AIRPORTS = ("ZZZZ", "AFIL")
ANONYMOUS_OPERATOR = "ZZZ"
CARGO_SEGMENT = "All-Cargo"
# Some monthly files carry no market segment ("Not Classified" for every flight);
# those flights are left out of segments and cargoShare.
UNCLASSIFIED_SEGMENTS = ("Not Classified", "")
# Delays / durations beyond this are data errors, not flights.
MAX_ABS_MINUTES = 24 * 60
# An OurAirports row this far from the EUROCONTROL position is another place that
# reuses the ICAO code (e.g. a closed airport); its name is not used.
MAX_LABEL_KM = 20.0

COLUMNS = {
    "ADEP": "adep", "ADES": "ades",
    "ADEP Latitude": "adep_lat", "ADEP Longitude": "adep_lon",
    "ADES Latitude": "ades_lat", "ADES Longitude": "ades_lon",
    "FILED OFF BLOCK TIME": "filed_off", "FILED ARRIVAL TIME": "filed_arr",
    "ACTUAL OFF BLOCK TIME": "actual_off", "ACTUAL ARRIVAL TIME": "actual_arr",
    "AC Type": "ac_type", "AC Operator": "operator",
    "ICAO Flight Type": "flight_type", "STATFOR Market Segment": "segment",
    "Actual Distance Flown (nm)": "distance",
}
PAIR = ["adep", "ades"]
# Per directed pair and period: flight count and (sum, count) of each averaged quantity.
PAIR_SUMS = ["scheduled", "dep_delay", "arr_delay", "duration", "distance"]
PAIR_COLUMNS = ["n"] + [f"{q}_{part}" for q in PAIR_SUMS for part in ("sum", "n")]
# Per directed pair over all periods: flights per category value.
CATEGORIES = ["segment", "operator", "ac_type", "off_hour", "arr_hour"]

log = logging.getLogger("build_graph_ectrl")


# ---------------------------------------------------------------- inputs

def default_inputs():
    return [ROOT / "new_data"] + sorted(p for p in ROOT.glob("20[0-9][0-9]") if p.is_dir())


def find_flight_files(paths):
    """Return [(period, days, path)] sorted by period, one file per period."""
    found = []
    for p in paths:
        if p.is_dir():
            files = sorted(p.rglob("Flights_*.csv*"))
        elif p.is_file():
            files = [p]
        else:
            log.warning("input not found: %s", p)
            continue
        for f in files:
            m = FILE_RE.match(f.name)
            if not m:
                log.warning("skipping %s: name is not Flights_YYYYMMDD_YYYYMMDD.csv[.gz]", f)
                continue
            start, end = (datetime.strptime(g, "%Y%m%d").date() for g in m.groups()[:2])
            found.append((start, end, f))
    periods, seen = [], {}
    for start, end, f in sorted(found, key=lambda t: (t[0], str(t[2]))):
        period = start.strftime("%Y-%m")
        if period in seen:
            log.warning("period %s already read from %s; skipping %s", period, seen[period], f)
            continue
        seen[period] = f
        periods.append((period, (end - start).days + 1, f))
    return periods


def minutes(a, b):
    """b - a in minutes; NaN if either is missing or the gap is implausible."""
    m = (b - a).dt.total_seconds() / 60.0
    return m.where(m.abs() <= MAX_ABS_MINUTES)


def read_flights(path, positions):
    """One cleaned row per flight, the per-reason drop counts and how many kept
    flights got an airport position from `positions` (lat / lon by ICAO code)."""
    raw = pd.read_csv(path, usecols=list(COLUMNS), dtype=str, keep_default_na=False).rename(columns=COLUMNS)
    df = pd.DataFrame({"adep": raw["adep"], "ades": raw["ades"]})
    for c in ("adep_lat", "adep_lon", "ades_lat", "ades_lon", "distance"):
        df[c] = pd.to_numeric(raw[c], errors="coerce")
    # Some files leave a known airport's coordinates empty (e.g. FAOR in 2020-2022).
    filled = pd.Series(False, index=df.index)
    for side in PAIR:
        lat, lon = f"{side}_lat", f"{side}_lon"
        fill = (df[lat].isna() | df[lon].isna()) & df[side].isin(positions.index)
        df.loc[fill, lat] = df.loc[fill, side].map(positions["lat"])
        df.loc[fill, lon] = df.loc[fill, side].map(positions["lon"])
        filled |= fill
    t = {c: pd.to_datetime(raw[c], format=TIME_FMT, errors="coerce")
         for c in ("filed_off", "filed_arr", "actual_off", "actual_arr")}
    off = t["actual_off"].fillna(t["filed_off"])  # actual time if known, else filed
    arr = t["actual_arr"].fillna(t["filed_arr"])

    reasons = [
        ("unknown airport", df["adep"].isin(UNKNOWN_AIRPORTS) | df["ades"].isin(UNKNOWN_AIRPORTS)),
        ("self-loop", df["adep"] == df["ades"]),
        ("missing coords", df[["adep_lat", "adep_lon", "ades_lat", "ades_lon"]].isna().any(axis=1)),
        ("missing times", off.isna() | arr.isna()),
    ]
    keep = pd.Series(True, index=df.index)
    dropped = Counter()
    for reason, mask in reasons:
        dropped[reason] = int((keep & mask).sum())
        keep &= ~mask

    df["off_hour"] = off.dt.hour
    df["arr_hour"] = arr.dt.hour
    df["dep_delay"] = minutes(t["filed_off"], t["actual_off"])
    df["arr_delay"] = minutes(t["filed_arr"], t["actual_arr"])
    duration = minutes(t["actual_off"], t["actual_arr"])
    df["duration"] = duration.where(duration > 0)
    df["distance"] = df["distance"].where(df["distance"] > 0)
    df["scheduled"] = (raw["flight_type"] == "S").astype(int)
    for c in ("segment", "operator", "ac_type"):
        df[c] = raw[c]
    df = df[keep]
    df = df.astype({"off_hour": int, "arr_hour": int})
    return df, len(raw), dropped, int((filled & keep).sum())


def aggregate_pairs(df):
    """Per directed pair: n plus <q>_sum / <q>_n for every averaged quantity."""
    g = df.groupby(PAIR, sort=True)
    cols = {"n": g.size()}
    for q in PAIR_SUMS:
        cols[f"{q}_sum"] = g[q].sum()
        cols[f"{q}_n"] = g[q].count()
    return pd.DataFrame(cols)


def read_all(periods, positions):
    """Aggregate every file. Returns the per-period pair table, category counts,
    latest coordinates per airport, kept flights per period and the share of
    them with a known market segment."""
    per_period, flights, coverage = [], [], []
    categories = dict.fromkeys(CATEGORIES)
    coords = {}
    for period, _, path in periods:
        df, rows, dropped, filled = read_flights(path, positions)
        log.info("%s: %d rows, %d kept (%d with OurAirports coordinates), dropped %s (%s)",
                 period, rows, len(df), filled, dict(dropped), path.name)
        per_period.append(aggregate_pairs(df))
        flights.append(len(df))
        coverage.append(round(float((~df["segment"].isin(UNCLASSIFIED_SEGMENTS)).mean()), 3) if len(df) else 0.0)
        if coverage[-1] < 0.5:
            log.warning("%s: only %.0f%% of flights have a market segment", period, 100 * coverage[-1])
        for c in CATEGORIES:
            counts = df.groupby(PAIR + [c]).size()
            categories[c] = counts if categories[c] is None else categories[c].add(counts, fill_value=0)
        # Periods are read in order, so the latest position of an airport wins.
        for side in PAIR:
            last = df.drop_duplicates(side, keep="last")
            coords.update((icao, (float(lat), float(lon)))
                          for icao, lat, lon in zip(last[side], last[f"{side}_lat"], last[f"{side}_lon"]))
    table = pd.concat(per_period, keys=[p for p, _, _ in periods], names=["period"])
    return table, categories, coords, flights, coverage


# ---------------------------------------------------------------- airport info

def download_airport_info(directory):
    directory.mkdir(parents=True, exist_ok=True)
    for name in OURAIRPORTS_FILES:
        dest = directory / name
        if dest.exists():
            continue
        log.info("downloading %s", OURAIRPORTS_URL + name)
        tmp = dest.with_suffix(".part")
        try:
            urllib.request.urlretrieve(OURAIRPORTS_URL + name, tmp)
            tmp.replace(dest)
        except OSError as ex:
            log.warning("could not download %s (%s); labels fall back to ICAO codes", name, ex)


def load_ourairports(directory):
    """OurAirports rows indexed by ICAO code (with float lat / lon) and {iso code: country name}.

    An ICAO code can sit in icao_code, ident or gps_code. Prefer open airports,
    then that column order. Both are empty if the files are missing."""
    path = directory / "airports.csv"
    if not path.exists():
        log.warning("%s missing; labels fall back to ICAO codes", path)
        return pd.DataFrame(columns=["lat", "lon"]), {}
    ap = pd.read_csv(path, dtype=str, keep_default_na=False)
    countries = {}
    if (directory / "countries.csv").exists():
        c = pd.read_csv(directory / "countries.csv", dtype=str, keep_default_na=False)
        countries = dict(zip(c["code"], c["name"]))
    candidates = pd.concat(
        [pd.DataFrame({"code": ap[col], "row": ap.index, "open": ap["type"] != "closed", "prio": prio})
         for prio, col in enumerate(("gps_code", "ident", "icao_code"))])
    candidates = candidates[candidates["code"].str.len() == 4]
    best = candidates.sort_values(["code", "open", "prio"]).drop_duplicates("code", keep="last")
    airports = ap.loc[best["row"].to_numpy()]
    airports.index = best["code"].to_numpy()
    airports = airports.assign(lat=pd.to_numeric(airports["latitude_deg"], errors="coerce"),
                               lon=pd.to_numeric(airports["longitude_deg"], errors="coerce"))
    airports = airports[airports["lat"].notna() & airports["lon"].notna()]
    log.info("OurAirports: %d ICAO codes", len(airports))
    return airports, countries


def airport_info(airports, countries, codes):
    """Return {icao: dict(name, iata, city, country, alt, lat, lon)} for the `codes` OurAirports knows."""
    def text(v):
        return v or None

    info = {}
    for code in sorted(set(codes) & set(airports.index)):
        r = airports.loc[code]
        try:
            alt = float(r["elevation_ft"])
        except ValueError:
            alt = 0.0
        info[code] = dict(
            name=r["name"], iata=text(r["iata_code"]), city=text(r["municipality"]),
            country=countries.get(r["iso_country"]) or text(r["iso_country"]),
            alt=alt, lat=float(r["lat"]), lon=float(r["lon"]),
        )
    return info


def haversine_km(lat1, lon1, lat2, lon2):
    p1, p2 = math.radians(lat1), math.radians(lat2)
    a = math.sin((p2 - p1) / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(math.radians(lon2 - lon1) / 2) ** 2
    return 2 * 6371.0 * math.asin(math.sqrt(a))


# ---------------------------------------------------------------- aggregation helpers

def mean(total, count):
    return round(float(total) / float(count), 1) if count else None


def top_key(counts, exclude=()):
    """Most frequent key (ties: alphabetical), skipping empty and excluded keys."""
    for key, _ in sorted(counts.items(), key=lambda kv: (-kv[1], kv[0])):
        if key and key not in exclude:
            return key
    return None


def segment_fields(counts):
    """segments (known ones, largest first) and cargoShare among flights with a known segment."""
    seg = {k: v for k, v in counts.items() if k not in UNCLASSIFIED_SEGMENTS}
    known = sum(seg.values())
    return dict(
        cargoShare=round(seg.get(CARGO_SEGMENT, 0) / known, 3) if known else 0.0,
        segments=dict(sorted(seg.items(), key=lambda kv: (-kv[1], kv[0]))),
    )


def undirected(df):
    """Add canonical s < t columns and fwd (flight goes s -> t) to a frame with adep / ades."""
    fwd = df["adep"] < df["ades"]
    df["s"] = df["adep"].where(fwd, df["ades"])
    df["t"] = df["ades"].where(fwd, df["adep"])
    df["fwd"] = fwd
    return df


def nested(series):
    """(key, value) -> n series as {key: {value: n}}."""
    out = defaultdict(dict)
    for (key, value), n in series.items():
        if n:
            out[key][value] = int(n)
    return out


def node_counts(counts, keep, sides=PAIR):
    """Category counts per airport, summed over its departures (adep) and / or arrivals (ades)."""
    df = counts.rename("n").reset_index()
    col = df.columns[2]
    total = None
    for side in sides:
        part = df[df[side].isin(keep)].groupby([side, col])["n"].sum()
        part.index = part.index.set_names(["key", col])
        total = part if total is None else total.add(part, fill_value=0)
    return nested(total)


def edge_counts(counts, pairs):
    """Category counts per undirected pair s|t (both directions)."""
    df = undirected(counts.rename("n").reset_index())
    col = df.columns[2]
    df["key"] = df["s"] + "|" + df["t"]
    df = df[df["key"].isin(pairs)]
    return nested(df.groupby(["key", col])["n"].sum())


def hourly(hours):
    out = [0] * 24
    for h, n in hours.items():
        out[int(h)] += n
    return out


def by_period(series, periods, fill=0):
    """(key, period) -> value series as {key: [value per period]}."""
    wide = series.unstack("period").reindex(columns=periods)
    return {key: [fill if pd.isna(v) else v for v in row] for key, row in zip(wide.index, wide.to_numpy().tolist())}


def ratio_by_period(table, key, quantity, periods):
    """Mean of `quantity` per (key, period) as {key: [mean or None per period]}."""
    g = table.groupby(level=[key, "period"])[[f"{quantity}_sum", f"{quantity}_n"]].sum()
    means = {k: [None] * len(periods) for k in g.index.get_level_values(0).unique()}
    index = {p: i for i, p in enumerate(periods)}
    for (k, p), s, n in zip(g.index, g[f"{quantity}_sum"], g[f"{quantity}_n"]):
        means[k][index[p]] = mean(s, n)
    return means


def project(lat, lon, alt, max_alt):
    """Same projection as build_graph.py: x = lon/18 in [-10, 10], z = lat/18 in [-5, 5], y = alt in [0, 2]."""
    return round(lon / 18.0, 4), round(alt / max_alt * 2.0, 4), round(lat / 18.0, 4)


# ---------------------------------------------------------------- build

def build(periods, top_n, min_daily, airports_dir):
    airports, countries = load_ourairports(airports_dir)
    table, categories, coords, period_flights, segment_coverage = read_all(periods, airports[["lat", "lon"]])
    info = airport_info(airports, countries, coords)
    names = [p for p, _, _ in periods]
    days = pd.Series([d for _, d, _ in periods], index=names)

    departures = table.groupby(level="adep")["n"].sum()
    arrivals = table.groupby(level="ades")["n"].sum()
    volume = departures.add(arrivals, fill_value=0).astype(int)
    keep = [icao for icao, _ in sorted(volume.items(), key=lambda kv: (-kv[1], kv[0]))[:top_n]]
    log.info("kept top %d airports by flight count; dropped %d", len(keep), len(volume) - len(keep))

    # Undirected pairs between kept airports, flights per period in both directions.
    flat = table.reset_index()
    inner = undirected(flat[flat["adep"].isin(keep) & flat["ades"].isin(keep)].copy())
    pair_monthly = inner.groupby(["s", "t", "period"])["n"].sum().unstack("period").reindex(columns=names).fillna(0)
    peak_daily = pair_monthly.div(days, axis=1).max(axis=1)
    pairs = sorted(pair_monthly.index[peak_daily >= min_daily])
    log.info("pairs between kept airports: %d; %d reach %.2f flights/day in some period",
             len(pair_monthly), len(pairs), min_daily)

    # Drop airports left without any edge.
    connected = {icao for pair in pairs for icao in pair}
    keep = [icao for icao in keep if icao in connected]
    keys = {f"{s}|{t}" for s, t in pairs}

    # ---- nodes
    dep_monthly = table.groupby(level=["adep", "period"])["n"].sum().rename_axis(["key", "period"])
    arr_monthly = table.groupby(level=["ades", "period"])["n"].sum().rename_axis(["key", "period"])
    node_monthly = by_period(dep_monthly.add(arr_monthly, fill_value=0), names)
    dep_delay = ratio_by_period(table, "adep", "dep_delay", names)
    arr_delay = ratio_by_period(table, "ades", "arr_delay", names)
    dep_sums = table.groupby(level="adep")[["scheduled_sum", "dep_delay_sum", "dep_delay_n"]].sum()
    arr_sums = table.groupby(level="ades")[["scheduled_sum", "arr_delay_sum", "arr_delay_n"]].sum()
    segments = node_counts(categories["segment"], keep)
    operators = node_counts(categories["operator"], keep)
    ac_types = node_counts(categories["ac_type"], keep)
    off_hours = node_counts(categories["off_hour"], keep, sides=("adep",))
    arr_hours = node_counts(categories["arr_hour"], keep, sides=("ades",))

    def trusted_info(icao):
        meta = info.get(icao)
        if meta is None:
            return {}
        lat, lon = coords[icao]
        km = haversine_km(lat, lon, meta["lat"], meta["lon"])
        if km > MAX_LABEL_KM:
            log.warning("%s: OurAirports '%s' is %.0f km from the EUROCONTROL position; not using its name",
                        icao, meta["name"], km)
            return {}
        return meta

    airport = {icao: trusted_info(icao) for icao in keep}
    max_alt = max((airport[i].get("alt", 0.0) for i in keep), default=1.0) or 1.0
    nodes = []
    for icao in keep:
        lat, lon = coords[icao]
        meta = airport[icao]
        x, y, z = project(lat, lon, meta.get("alt", 0.0), max_alt)
        dep, arr = int(departures.get(icao, 0)), int(arrivals.get(icao, 0))
        d = dep_sums.loc[icao] if icao in dep_sums.index else None
        a = arr_sums.loc[icao] if icao in arr_sums.index else None
        value = dep + arr
        node = dict(
            id=icao,
            label=f"{meta['name']} ({meta.get('iata') or icao})" if meta else icao,
            x=x, y=y, z=z,
            value=value,
            lat=lat, lon=lon,
            city=meta.get("city"),
            country=meta.get("country"),
            departures=dep,
            arrivals=arr,
            avgDepDelayMin=mean(d["dep_delay_sum"], d["dep_delay_n"]) if d is not None else None,
            avgArrDelayMin=mean(a["arr_delay_sum"], a["arr_delay_n"]) if a is not None else None,
            scheduledShare=round(float((d["scheduled_sum"] if d is not None else 0) +
                                       (a["scheduled_sum"] if a is not None else 0)) / value, 3),
            **segment_fields(segments.get(icao, {})),
            topOperator=top_key(operators.get(icao, {}), exclude=(ANONYMOUS_OPERATOR,)),
            topAcType=top_key(ac_types.get(icao, {})),
            monthly=[int(v) for v in node_monthly[icao]],
            monthlyDepDelayMin=dep_delay.get(icao, [None] * len(names)),
            monthlyArrDelayMin=arr_delay.get(icao, [None] * len(names)),
            hourly=[dh + ah for dh, ah in zip(hourly(off_hours.get(icao, {})), hourly(arr_hours.get(icao, {})))],
        )
        nodes.append(node)
    nodes.sort(key=lambda n: (-n["value"], n["id"]))

    # ---- edges
    inner["key"] = inner["s"] + "|" + inner["t"]
    inner = inner[inner["key"].isin(keys)]
    edge_table = inner.set_index(["key", "period"])
    edge_sums = edge_table.groupby(level="key")[PAIR_COLUMNS].sum()
    forward = inner[inner["fwd"]].groupby("key")["n"].sum()
    edge_monthly = by_period(edge_table.groupby(level=["key", "period"])["n"].sum(), names)
    edge_delay = ratio_by_period(edge_table, "key", "arr_delay", names)
    e_segments = edge_counts(categories["segment"], keys)
    e_operators = edge_counts(categories["operator"], keys)
    e_ac_types = edge_counts(categories["ac_type"], keys)
    e_hours = edge_counts(categories["off_hour"], keys)

    edges = []
    for s, t in pairs:
        key = f"{s}|{t}"
        r = edge_sums.loc[key]
        weight = int(r["n"])
        fwd = int(forward.get(key, 0))
        edges.append(dict(
            source=s,
            target=t,
            weight=weight,
            forward=fwd,
            backward=weight - fwd,
            avgDistanceNm=mean(r["distance_sum"], r["distance_n"]),
            avgDurationMin=mean(r["duration_sum"], r["duration_n"]),
            avgDelayMin=mean(r["arr_delay_sum"], r["arr_delay_n"]),
            scheduledShare=round(float(r["scheduled_sum"]) / weight, 3),
            **segment_fields(e_segments.get(key, {})),
            topOperator=top_key(e_operators.get(key, {}), exclude=(ANONYMOUS_OPERATOR,)),
            topAcType=top_key(e_ac_types.get(key, {})),
            monthly=[int(v) for v in edge_monthly[key]],
            monthlyDelayMin=edge_delay[key],
            hourly=hourly(e_hours.get(key, {})),
        ))
    edges.sort(key=lambda e: (-e["weight"], e["source"], e["target"]))

    first, last = periods[0][2], periods[-1][2]
    meta = dict(
        source="EUROCONTROL R&D Archive",
        files=[p.name for _, _, p in periods],
        airportInfo="OurAirports (public domain)",
        dateFrom=datetime.strptime(FILE_RE.match(first.name).group(1), "%Y%m%d").date().isoformat(),
        dateTo=datetime.strptime(FILE_RE.match(last.name).group(2), "%Y%m%d").date().isoformat(),
        periods=names,
        periodDays=[int(d) for d in days],
        periodFlights=period_flights,
        segmentCoverage=segment_coverage,
        days=int(days.sum()),
        timezone="UTC",
        totalFlights=sum(period_flights),
        topN=top_n,
        minDaily=min_daily,
        nodeCount=len(nodes),
        edgeCount=len(edges),
    )
    return nodes, edges, meta


# ---------------------------------------------------------------- output

def write_records(path, records):
    """One compact JSON object per line: small file, still diff/grep friendly."""
    lines = ",\n".join(json.dumps(r, ensure_ascii=False, separators=(",", ":")) for r in records)
    path.write_text("[\n" + lines + "\n]\n", encoding="utf-8")


def validate(nodes_path, edges_path, meta_path):
    nodes = json.loads(nodes_path.read_text(encoding="utf-8"))
    edges = json.loads(edges_path.read_text(encoding="utf-8"))
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    n_periods = len(meta["periods"])
    assert meta["periods"] == sorted(set(meta["periods"])), "periods not unique and sorted"
    assert len(meta["periodDays"]) == len(meta["periodFlights"]) == len(meta["segmentCoverage"]) == n_periods
    ids = {n["id"] for n in nodes}
    assert len(ids) == len(nodes), "duplicate node ids"
    for n in nodes:
        assert len(n["monthly"]) == len(n["monthlyDepDelayMin"]) == len(n["monthlyArrDelayMin"]) == n_periods, \
            f"bad time profile {n['id']}"
        assert len(n["hourly"]) == 24, f"bad hourly profile {n['id']}"
        assert sum(n["monthly"]) == sum(n["hourly"]) == n["value"] == n["departures"] + n["arrivals"], \
            f"time profile != value {n['id']}"
    pairs = set()
    for e in edges:
        assert e["source"] in ids and e["target"] in ids, f"dangling edge {e['source']}-{e['target']}"
        assert e["source"] < e["target"], "edge pair not canonical"
        assert e["forward"] + e["backward"] == e["weight"]
        assert len(e["monthly"]) == len(e["monthlyDelayMin"]) == n_periods
        assert sum(e["monthly"]) == sum(e["hourly"]) == e["weight"]
        assert max(m / d for m, d in zip(e["monthly"], meta["periodDays"])) >= meta["minDaily"]
        pairs.add((e["source"], e["target"]))
    assert len(pairs) == len(edges), "duplicate edge pairs"
    log.info("validated: %d nodes, %d edges, %d periods (%s .. %s)",
             len(nodes), len(edges), n_periods, meta["periods"][0], meta["periods"][-1])


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--input", type=Path, nargs="+", default=None,
                   help="Flights_*.csv[.gz] files or folders searched recursively (default: new_data/ and 20??/)")
    p.add_argument("--top", type=int, default=200, help="airports to keep (150-300)")
    p.add_argument("--min-daily", type=float, default=1.0,
                   help="keep an edge if it has at least this many flights per day in some period")
    p.add_argument("--out-dir", type=Path, default=OUT_DIR)
    p.add_argument("--airports-dir", type=Path, default=AIRPORTS_DIR, help="OurAirports airports.csv / countries.csv")
    args = p.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")

    periods = find_flight_files(args.input or default_inputs())
    if not periods:
        log.error("no Flights_YYYYMMDD_YYYYMMDD.csv[.gz] files found")
        return 1
    log.info("%d periods: %s", len(periods), ", ".join(p for p, _, _ in periods))
    download_airport_info(args.airports_dir)
    nodes, edges, meta = build(periods, args.top, args.min_daily, args.airports_dir)
    args.out_dir.mkdir(parents=True, exist_ok=True)
    nodes_path = args.out_dir / "nodes_ectrl.json"
    edges_path = args.out_dir / "edges_ectrl.json"
    meta_path = args.out_dir / "meta_ectrl.json"
    write_records(nodes_path, nodes)
    write_records(edges_path, edges)
    meta_path.write_text(json.dumps(meta, indent=1) + "\n", encoding="utf-8")
    validate(nodes_path, edges_path, meta_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
