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
