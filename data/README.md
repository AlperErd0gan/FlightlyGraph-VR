# Data

## Layout

Everything under `data/raw/` is gitignored (downloaded or licence-restricted):

```
data/
  raw/
    eurocontrol/
      flights/<YYYY>/Flights_<YYYYMMDD>_<YYYYMMDD>.csv.gz   input of build_graph_ectrl.py
      other/202102/        FIR / point profile / AUA / route / AIRAC files of Feb 2021
                           (FIR and point profile files: input of build_ectrl_extras.py)
    ourairports/           airports.csv, countries.csv (downloaded by build_graph_ectrl.py)
    openflights/           airports.dat, routes.dat (downloaded by build_graph.py)
  processed/               JSON for Unity (*_ectrl.json gitignored)
```

EUROCONTROL data must never be committed. Besides `data/raw/` (and the legacy
`new_data/` folder), `.gitignore` matches the files by name wherever they end up:
raw exports (`Flights_*.csv[.gz]`, `Flight_*.csv.gz`, `FIR_*`, `Route_*`,
`AIRAC_*`) and everything derived (`*_ectrl.json`, `*_ectrl.json.meta`,
`*_ectrl*.zip`), e.g. copies in `Assets/StreamingAssets/` or zips for sharing.
Share these files outside git (they are research-licensed).

## Source: OpenFlights

`data/raw/openflights/airports.dat` and `routes.dat` are downloaded verbatim from
the [OpenFlights](https://openflights.org/data.html) project
(https://github.com/jpatokal/openflights).

**License:** OpenFlights data is released under the
[Open Database License (ODbL)](https://opendatacommons.org/licenses/odbl/1-0/).
Attribution is required: any use or redistribution of this data or of
`data/processed/*.json` derived from it must credit OpenFlights and keep this
notice. The derived files are likewise ODbL.

**Currency caveat:** `routes.dat` is historical — OpenFlights stopped updating
it in **June 2014**. The route network rendered by this project is therefore
illustrative of the global air network as of mid-2014, **not** current traffic.
Airport records (`airports.dat`) come from a later snapshot; the two files are
joined on OpenFlights airport id.

## Processed output

`scripts/build_graph.py` produces `data/processed/nodes.json` and
`edges.json` (schemas read verbatim by the Unity loader):

```
nodes.json: [{ "id", "label", "x", "y", "z", "value", "lat", "lon" }, ...]
edges.json: [{ "source", "target", "weight" }, ...]
```

- `id` — OpenFlights airport id (string); `label` — name + IATA code.
- `x`/`z` — equirectangular projection (2:1) of longitude into [-10, 10] and latitude into [-5, 5];
  `y` — field altitude scaled to [0, 2].
- `lat`/`lon` — raw airport coordinates in degrees (for labels / globe layouts).
- `value` — route-count degree of the airport in the full (unfiltered) network.
- Only the top `--top` (default 200) airports by degree are kept; `weight` is
  the number of distinct directed routes between the pair, collapsed into one
  undirected edge.

## Source: EUROCONTROL R&D Archive (monthly flight files)

Monthly `Flights_<YYYYMMDD>_<YYYYMMDD>.csv[.gz]` files come from the
EUROCONTROL Aviation Data Repository for Research. Each covers flights
departing, arriving or overflying the EUROCONTROL area in one month; the
project uses every month from January 2020 to August 2025 in
`data/raw/eurocontrol/flights/2020/` .. `2025/`. The graph uses only the
`Flights_*` files; the FIR and point profile files of one month (Feb 2021) feed
the optional extra layers (see below), the AUA / route / AIRAC files are unused. Use is
governed by the EUROCONTROL R&D data licence (research only) — check it before
redistributing the raw files or the derived `*_ectrl.json`.

To add more months, drop the unchanged `Flights_*` files into their year folder
(e.g. `data/raw/eurocontrol/flights/2019/Flights_20190301_20190331.csv.gz`) and
re-run the script: every `Flights_*` file under `data/raw/eurocontrol/flights/`
becomes one period on the time axis. A month found twice is read once.

## Source: OurAirports

Airport name, IATA code, city, country and elevation come from
[OurAirports](https://ourairports.com/data/) (`airports.csv`, `countries.csv`,
public domain), downloaded to `data/raw/ourairports/` on the first run and
joined on ICAO code. Positions come from EUROCONTROL (latest period); only
when a file leaves a known airport's coordinates empty (e.g. FAOR, HSSK in
2020-2022) is the OurAirports position used, so those flights are kept.
If the OurAirports row is more than 20 km away, the code belongs to another
place (e.g. a closed airport) and the label falls back to the ICAO code.

## EUROCONTROL graph (time axis)

`scripts/build_graph_ectrl.py [--input PATH ...] [--top 200] [--min-daily 1]`
builds `nodes_ectrl.json`, `edges_ectrl.json` and `meta_ectrl.json`. Flights
with an unknown airport (`ZZZZ` / `AFIL`), coordinates missing in both
EUROCONTROL and OurAirports, no usable
off-block / arrival time or ADEP == ADES are dropped. All times are UTC.

```
nodes_ectrl.json: [{ id (ICAO), label, x, y, z, value, lat, lon, city, country,
                     departures, arrivals, avgDepDelayMin, avgArrDelayMin,
                     scheduledShare, cargoShare, segments, topOperator, topAcType,
                     monthly, monthlyDepDelayMin, monthlyArrDelayMin, hourly }, ...]
edges_ectrl.json: [{ source, target, weight, forward, backward,
                     avgDistanceNm, avgDurationMin, avgDelayMin,
                     scheduledShare, cargoShare, segments, topOperator, topAcType,
                     monthly, monthlyDelayMin, hourly }, ...]
meta_ectrl.json:  { source, files, airportInfo, dateFrom, dateTo, periods, periodDays,
                    periodFlights, segmentCoverage, days, timezone, totalFlights,
                    topN, minDaily, nodeCount, edgeCount }
```

- Nodes are the top `--top` airports by flights over all periods. An edge is
  kept if it has at least `--min-daily` flights per day in at least one period,
  so seasonal routes stay; airports left without edges are dropped.
- `meta.periods[i]` (`"yyyy-MM"`) is the period of `monthly[i]` and
  `monthlyXxxDelayMin[i]` in every node and edge. `meta.periodDays[i]` is its
  length, so `monthly[i] / periodDays[i]` is flights per day. Periods need not be
  consecutive. A flight belongs to the period of the file it is in.
- `meta.periodFlights[i]` — all kept flights in the period (whole network, not
  only the graph); `meta.totalFlights` is their sum.
- `value` — flights touching the airport (departures + arrivals), all airports
  counted, all periods; `sum(monthly) == sum(hourly) == value`.
- `weight` — flights between the pair, both directions, all periods; `forward` =
  source→target (source < target alphabetically), `backward` = the other way;
  `sum(monthly) == sum(hourly) == weight`.
- `avgDepDelayMin` / `avgArrDelayMin` / `avgDelayMin` — actual minus filed
  off-block / arrival time; negative = early. Gaps > 24 h are discarded.
  `avgDelayMin` and `monthlyDelayMin` are arrival delays. Monthly delays are
  null for a period without data.
- `avgDurationMin` — actual off-block to actual arrival (includes taxi).
- `segments` — STATFOR market segment → flight count, known segments only;
  `cargoShare` is `All-Cargo` among those flights (0 if none is known). Seven
  source files carry no segment at all (`Not Classified` for every flight:
  2022-09, 2022-12, 2023-03, 2023-06, 2023-09, 2023-12, 2024-03); their flights
  are left out of both. 2020-03 uses the older `Traditional Scheduled` label
  (Mainline + Regional together), kept as is.
- `meta.segmentCoverage[i]` — share of the period's flights with a known segment.
- `topOperator` — most frequent ICAO operator code, excluding anonymised `ZZZ`
  (null if all flights are anonymised); ties go to the alphabetically first code.
- `hourly[h]` — flights in UTC hour `h` over all periods (off-block hour for
  edges and departures, arrival hour for arrivals; actual, else filed).

## Immersive 3D layout

`scripts/layout_3d.py [--dataset openflights|ectrl]` writes `nodes_3d.json`
(or `nodes_3d_ectrl.json`) for the "graph around you" scene. Same schema as the
input nodes file, plus `community`; the edges file is reused unchanged.

- Positions come from a 3D force-directed layout (networkx `spring_layout`,
  weights `log1p(weight)`, fixed seed), not geography.
- Each node's direction from the layout centre becomes its direction around the
  viewer; its centre-to-periphery rank becomes its distance, spread evenly in
  `--inner`..`--outer` metres (default 3-8 m, central hubs closest).
- Elevation is squeezed into `--min-elev`..`--max-elev` (default -7..60 deg) so
  nodes stay above the floor and out of the zenith; the biggest hub is rotated
  to straight ahead (+Z).
- `community` — Louvain community id (0 = largest), for colouring clusters.
- `x`, `y`, `z` are metres relative to the GraphLoader object, which should sit
  at head height with scale 1, `positionScale = 1`, `altitudeScale = 1`.

## EUROCONTROL extra layers (one month)

`scripts/build_ectrl_extras.py` adds, as separate files, what the graph does not
have: real flight paths and airspace crossings. The graph files are only read
(never changed); records are linked to their edge by `source` / `target`, the
same sorted ICAO pair as in `edges_ectrl.json`. Standard library only.

```
python scripts/build_ectrl_extras.py \
    --raw-dir data/raw/eurocontrol/other/202102 \
    --flights data/raw/eurocontrol/flights/2021/Flights_20210201_20210228.csv.gz \
    [--nodes data/processed/nodes_ectrl.json] [--edges data/processed/edges_ectrl.json] \
    [--out-dir data/processed] [--samples 3] [--max-points 60]
```

`--raw-dir` holds one month of `Flight_Points_Actual_*`, `Flight_Points_Filed_*`,
`Flight_FIRs_Actual_*` and `FIR_*` files (the last AIRAC cycle by name is used);
`--flights` is the `Flights_*` file of the same month (default: the one in
`--raw-dir`). The graph may come from another machine or cover more months:
only its airport ids and edge pairs are used.

```
trajectories_ectrl.json: [{ source, target, flights,
                            samples: [{ id, from, to, actual, filed }, ...] }, ...]
edge_firs_ectrl.json:    [{ source, target, flights, firs: [{ id, share }, ...],
                            typicalPath, typicalPathShare }, ...]
firs_ectrl.json:         [{ id, minFL, maxFL, points }, ...]
extras_meta_ectrl.json:  { source, files, graph, flightsOnEdges, edgesWithFlights,
                           samplesPerEdge, maxPointsPerTrajectory, minFirShare,
                           pointFormat, firPointFormat }
```

- Only flights between two graph airports whose pair is a graph edge are used,
  in either direction. Edges without a flight in that month have no record, so
  with a multi-year graph and one month of extras many edges are missing
  (6,225 edges vs 3,243 with flights in Feb 2021).
- `flights` — flights on the edge in the month, both directions.
- `samples` — up to `--samples` flights per edge, evenly spread over the edge's
  flights sorted by ECTRL ID (deterministic). `from` / `to` give the direction.
- `actual` / `filed` — flown and flight-plan paths as `[lat, lon, flightLevel]`
  points in sequence order, thinned evenly to at most `--max-points` (first and
  last point kept). Either can be empty if the source has no profile.
- `firs` — FIRs crossed by at least 5 % of the edge's flights, with the share of
  flights crossing each. Ground phases and unknown airspace (`TAXI_OUT`,
  `TAXI_IN`, `FIR_UNK`) are removed; oceanic / special areas are kept.
- `typicalPath` — most common FIR sequence, read from `source` to `target`
  (reverse flights are flipped), consecutive repeats merged;
  `typicalPathShare` is the share of flights that followed it.
- `firs_ectrl.json` — FIR boundary polygons of that AIRAC cycle as `[lat, lon]`
  rings in sequence order; a FIR with several flight-level layers has one record
  per layer (`minFL` / `maxFL`, flight levels, 999 = unlimited).
- Unity does not read these files yet; they are data for later features
  (e.g. drawing a selected edge's real routes, an airspace layer).
