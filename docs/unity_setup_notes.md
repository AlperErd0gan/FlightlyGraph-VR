# Unity setup notes (manual steps — no Unity was available when this was written)

## 1. Add Newtonsoft JSON

`GraphLoader.cs` uses `Newtonsoft.Json.JsonConvert`. In Unity:

Window → Package Manager → "+" → **Add package by name…** →
`com.unity.nuget.newtonsoft-json` (leave version blank; Unity resolves it).

Without this package `Assets/Scripts/GraphLoader.cs` will not compile
(`The type or namespace name 'Newtonsoft' could not be found`).

## 2. Sync graph data into StreamingAssets

The loader reads `nodes.json` / `edges.json` from
`Application.streamingAssetsPath` via `UnityWebRequest` (works in Editor and
on Android/Quest, where plain `File.ReadAllText` does not).

After running `python scripts/build_graph.py`, copy the outputs:

```sh
scripts/sync_streaming_assets.sh            # from repo root
```

which does, in effect:

```sh
mkdir -p Assets/StreamingAssets
cp data/processed/nodes.json data/processed/edges.json Assets/StreamingAssets/
```

Re-run whenever the pipeline output changes. `Assets/StreamingAssets/` is
intentionally not created by this repo until a Unity project exists there.

## 3. Scene wiring

1. Empty GameObject → add `GraphLoader` component.
2. Inspector: optionally assign `nodePrefab` (else a sphere primitive is
   created per node), assign `edgeMaterial` (any unlit/line material;
   `Sprites/Default` works), leave `positionScale` = 1, filenames default to
   `nodes.json` / `edges.json`.
3. Press Play. Console should log `GraphLoader: loaded N nodes, M edges`.

## 4. Not yet wired

`GraphLoader.SetEdgeWeightThreshold(float)` exists but has no UI (future task).

## 5. Timeline, dashboard and city groups

No scene change is needed: `DataDashboard` adds `TimelinePanel` and
`CityClusters` to its own object when the scene has none. The time axis comes
from `GraphLoader.metaFileName` (default `meta_ectrl.json`, next to the nodes /
edges files in StreamingAssets); without it the timeline is unavailable and
everything else works as before.

| What | Controller | Editor |
|---|---|---|
| Dashboard (menu) | left menu button | F1 |
| Timeline (time slider) | dashboard: Timeline / "Play over time" | T |
| View mode (top routes / regional) | left thumbstick click (L3) | V |
| City groups on / off | dashboard: Clusters page | C |
| Open / close one city | select the city node / its label | click |

- **Timeline**: play / pause, step, speed (1 / 2 / 4 months per second),
  "All months". Airport sizes, visible routes, the dashboard lists and the info
  panel follow the month; the selection stays. Closing returns to all months.
- **View mode**: one button toggles both ways. `GraphViewMode.toggleBinding`
  sets it (e.g. `<XRController>{RightHand}/{Primary2DAxisClick}` for R3);
  B / Y are no longer used for it.
- **City groups** (TASKS X5): airports of one city (IATA multi-airport cities:
  London, Paris, Istanbul, Milan, Moscow, ...; `CityClusters.metros`) collapse
  into one node with a ring and a "London · 6" label; their routes start there.
  Selecting it opens the city (airports fly back), selecting its label closes
  it; selecting a hidden airport (e.g. from the dashboard) opens its city.
