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
