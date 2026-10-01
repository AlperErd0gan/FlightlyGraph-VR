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

## 6. Info card, guided tour and controls

Also no scene change. The info card (`GraphInfoPanel`) and the tour caption
(`GuidedTour`) use the dashboard's look (`UIKit`). Inspector fields renamed, so
old scene values no longer apply: `GraphInfoPanel.cardWidth` (was
`panelWidth`), `valueUnit` / `weightUnit` (were `valueLabel` / `weightLabel`,
now "Flights"), `GuidedTour.captionCentreHeight` (was `captionHeight`).

- **Info card**: opens to the lower right of your view (`viewSideOffset`),
  shows the airport / route / connection as tiles and lines, follows the
  timeline's month, and has a close X.
- **Tour**: 8 steps with a title, a progress bar, Next and Stop; step 7 opens
  the dashboard's Controls page.
- **Re-centre** (TASKS B3, `GraphRecenter`, added by the dashboard like the
  timeline): R3 / H / Controls page teleports you to the graph's centre and
  turns you to its front; the graph stays put. `matchEyeHeight` (on by
  default) also sets your eyes to the centre's height for seated use and moves
  the teleport floors along, so the virtual floor stays at the real one; off =
  horizontal teleport only.
- **Controls**: every button per panel / situation is in
  [CONTROLS.md](CONTROLS.md); the in-app table (dashboard → Controls) comes from
  `Assets/Scripts/UI/ControlsHelp.cs`. Keep the two in sync.
