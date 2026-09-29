# Data

## Source: OpenFlights

`data/raw/airports.dat` and `data/raw/routes.dat` are downloaded verbatim from
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

Monthly `Flights_<YYYYMMDD>_<YYYYMMDD>.csv[.gz]` files (gitignored) come from
the EUROCONTROL Aviation Data Repository for Research. Each covers flights
departing, arriving or overflying the EUROCONTROL area in one month; the repo
currently has every month from January 2020 to August 2025 (`2020/` .. `2025/`;
`new_data/` holds a second copy of February 2021, read once). Only the `Flights_*` files are used; the FIR / AUA / point
profile / route files are not needed. Use is governed by the EUROCONTROL R&D
data licence (research only) — check it before redistributing the raw files or
the derived `*_ectrl.json`.

To add more months, drop the unchanged `Flights_*` files into a year folder
(e.g. `2024/202403/Flights_20240301_20240331.csv.gz`) and re-run the script:
every `Flights_*` file under `new_data/` and `20??/` becomes one period on the
time axis. A month found twice is read once.

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
  `--inner`..`--outer` metres (default 2-4 m, central hubs closest).
- Elevation is squeezed into `--min-elev`..`--max-elev` (default -15..55 deg) so
  nodes stay above the floor and out of the zenith; the biggest hub is rotated
  to straight ahead (+Z).
- `community` — Louvain community id (0 = largest), for colouring clusters.
- `x`, `y`, `z` are metres relative to the GraphLoader object, which should sit
  at head height with scale 1, `positionScale = 1`, `altitudeScale = 1`.
