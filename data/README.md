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
nodes.json: [{ "id", "label", "x", "y", "z", "value" }, ...]
edges.json: [{ "source", "target", "weight" }, ...]
```

- `id` — OpenFlights airport id (string); `label` — name + IATA code.
- `x`/`z` — equirectangular projection of longitude/latitude into [-10, 10];
  `y` — field altitude scaled to [0, 2].
- `value` — route-count degree of the airport in the full (unfiltered) network.
- Only the top `--top` (default 200) airports by degree are kept; `weight` is
  the number of distinct directed routes between the pair, collapsed into one
  undirected edge.

## Source: EUROCONTROL R&D Archive (Feb 2021)

`new_data/Flights_20210201_20210228.csv.gz` (gitignored) comes from the
EUROCONTROL Aviation Data Repository for Research. It covers flights
departing, arriving or overflying the EUROCONTROL area in February 2021
(COVID period, reduced traffic). Use is governed by the EUROCONTROL R&D data
licence (research only) — check it before redistributing the raw files or the
derived `*_ectrl.json`.

`scripts/build_graph_ectrl.py` builds `nodes_ectrl.json`, `edges_ectrl.json`
and `meta_ectrl.json`. Airport names/city/country/elevation are joined from
OpenFlights `airports.dat` on ICAO code. Flights with `ZZZZ` (unknown airport),
missing coordinates or ADEP == ADES are dropped. All times are UTC.

```
nodes_ectrl.json: [{ id (ICAO), label, x, y, z, value, lat, lon, city, country,
                     departures, arrivals, avgDepDelayMin, avgArrDelayMin,
                     scheduledShare, cargoShare, segments, topOperator, topAcType,
                     daily, hourly }, ...]
edges_ectrl.json: [{ source, target, weight, forward, backward,
                     avgDistanceNm, avgDurationMin, avgDelayMin,
                     scheduledShare, cargoShare, segments, topOperator, topAcType,
                     daily, hourly }, ...]
meta_ectrl.json:  { source, dateFrom, dateTo, days, timezone, totalFlights, nodeCount, edgeCount }
```

- `value` — flights touching the airport (departures + arrivals), all airports counted.
- `weight` — flights between the pair, both directions; `forward` = source→target
  (source < target alphabetically), `backward` = the other way.
- `avgDepDelayMin` / `avgArrDelayMin` / `avgDelayMin` — actual minus filed
  off-block / arrival time; negative = early. Gaps > 24 h are discarded.
- `avgDurationMin` — actual off-block to actual arrival (includes taxi).
- `segments` — STATFOR market segment → flight count.
- `topOperator` — most frequent ICAO operator code, excluding anonymised `ZZZ`
  (null if all flights are anonymised).
- `daily[i]` — flights on day `meta.dateFrom + i` (departure time for edges and
  departing flights, arrival time for arriving flights; actual, else filed).
- `hourly[h]` — flights in UTC hour `h` over the whole month.
