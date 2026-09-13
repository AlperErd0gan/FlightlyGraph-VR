# Phase 1 Tasks

Legend: [ ] todo · [x] done · status notes inline.

## Part A — Data pipeline (Python, verified by `pytest tests/`)
- [ ] D1: Download airports.dat + routes.dat into data/raw/ (scripts/build_graph.py `download` step, idempotent)
- [ ] D2: Parse CSVs; drop airports with invalid lat/lon; compute route-count degree
- [ ] D3: Filter to top 150-300 airports by degree; keep routes with both endpoints; collapse duplicate pairs into weight
- [ ] D4: Equirectangular projection (lat, lon, alt) -> (x, y, z) normalized ~[-10, 10]; export nodes.json / edges.json with exact schema; validate + log counts
- [ ] D5: tests/test_graph_pipeline.py (files exist, valid JSON, node budget, edge refs, no duplicate pairs) + data/README.md (ODbL attribution, historical caveat)

## Part B — Unity import layer (C#, NO compiler here; semantic review only)
- [ ] U1: docs/unity_setup_notes.md — Newtonsoft package add + StreamingAssets sync note/script
- [ ] U2: Assets/Scripts/Data/GraphData.cs — NodeData / EdgeData (schema cross-checked vs D4 output)
- [ ] U3: Assets/Scripts/GraphLoader.cs — UnityWebRequest load of both files, result check, JsonConvert deserialize
- [ ] U4: GraphLoader — node instantiation (prefab or sphere fallback), scale applied, id->Transform dictionary
- [ ] U5: GraphLoader — edges via TryGetValue, LineRenderer per edge, width/color by weight, SetEdgeWeightThreshold
- [ ] U6: Full semantic review pass (checklist a-e) of all .cs files; execution trace + "Needs Real Verification" in PROGRESS.md
