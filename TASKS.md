# Phase 1 Tasks

Legend: [ ] todo · [x] done · status notes inline.

## Part A — Data pipeline (Python, verified by `pytest tests/`)
- [x] D1: Download airports.dat + routes.dat into data/raw/ (scripts/build_graph.py `download` step, idempotent) — done, pytest passing
- [x] D2: Parse CSVs; drop airports with invalid lat/lon; compute route-count degree — done, pytest passing
- [x] D3: Filter to top 150-300 airports by degree; keep routes with both endpoints; collapse duplicate pairs into weight — done, pytest passing
- [x] D4: Equirectangular projection (lat, lon, alt) -> (x, y, z) normalized ~[-10, 10]; export nodes.json / edges.json with exact schema; validate + log counts — done, pytest passing
- [x] D5: tests/test_graph_pipeline.py (files exist, valid JSON, node budget, edge refs, no duplicate pairs) + data/README.md (ODbL attribution, historical caveat) — done, pytest passing

## Part B — Unity import layer (C#, NO compiler here; semantic review only)
- [x] U1: docs/unity_setup_notes.md — Newtonsoft package add + StreamingAssets sync note/script — done (docs only)
- [x] U2: Assets/Scripts/Data/GraphData.cs — NodeData / EdgeData (schema cross-checked vs D4 output) — implemented — semantically reviewed, not compiled
- [x] U3: Assets/Scripts/GraphLoader.cs — UnityWebRequest load of both files, result check, JsonConvert deserialize — implemented — semantically reviewed, not compiled
- [x] U4: GraphLoader — node instantiation (prefab or sphere fallback), scale applied, id->Transform dictionary — implemented — semantically reviewed, not compiled
- [x] U5: GraphLoader — edges via TryGetValue, LineRenderer per edge, width/color by weight, SetEdgeWeightThreshold — implemented — semantically reviewed, not compiled
- [x] U6: Full semantic review pass (checklist a-e) of all .cs files; execution trace + "Needs Real Verification" in PROGRESS.md — implemented — semantically reviewed, not compiled
