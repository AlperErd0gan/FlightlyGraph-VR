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

## Part C — Exploration UX (Unity 6, compiled + tested in "My project")
- [x] X1: FlyCamera.cs — WASD/QE move, arrow/right-drag look, scroll zoom, Ctrl+scroll speed, R reset (new Input System)
- [x] X2: MapPlane.cs + NASA Blue Marble texture; build_graph.py projection fixed to 2:1 (z = lat/18), lat/lon exported
- [x] X3: GraphLoader — sqrt node sizing, value colour gradient, arced edges, GraphNode/GraphEdge metadata
- [x] X4: GraphSelector.cs — click node (raycast) / edge (screen-space pick); highlight incident edges, dim rest; Esc clears
- [ ] X5: Same-city clustering — collapse airports sharing a city (e.g. LHR/LGW/STN -> "London") into one cluster node; click expands to member airports, click again collapses. Needs `city` in nodes.json (build_graph.py A_CITY column), cluster edge aggregation (sum weights), and GraphSelector expand/collapse state.
- [ ] X6: Edge weight threshold slider UI (SetEdgeWeightThreshold exists, unwired)
- [ ] X7: World-space label panel next to selected node (name, degree, lat/lon)
