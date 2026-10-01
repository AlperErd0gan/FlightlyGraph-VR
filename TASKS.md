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
- [x] X5: Same-city clustering — done as CityClusters (IATA multi-airport cities by ICAO code, not the `city` text: CDG / MXP list municipalities); collapse / expand animated, routes follow; dashboard Clusters page or C. Original note: — collapse airports sharing a city (e.g. LHR/LGW/STN -> "London") into one cluster node; click expands to member airports, click again collapses. Needs `city` in nodes.json (build_graph.py A_CITY column), cluster edge aggregation (sum weights), and GraphSelector expand/collapse state.
- [x] X6: Edge weight threshold — done as the dashboard's Filters tab (minimum flights per route, plus segment / country)
- [x] X7: World-space info panel for the selection (GraphInfoPanel: follows the view, city / country, routes, clusters) — redesigned as a dashboard-style card (tiles, busiest routes, close X), follows the timeline month

## Part D — Immersive VR + data exploration (Unity 6, Quest / Link; C# not compiled on the dev Mac)
- [x] V1: EUROCONTROL pipeline (build_graph_ectrl.py, 2020-2025 monthly axis) + extra layers (build_ectrl_extras.py: trajectories, FIR crossings, FIR bounds)
- [x] V2: Immersive 3D layout (layout_3d.py: force-directed on a 3-8 m shell, Louvain communities); edges arc around the viewer and touch the floor tangentially at most
- [x] V3: All edges in one mesh (EdgeRibbon shader), shared node materials, runtime assets cleaned up
- [x] V4: XR input: select with trigger / pinch, hold-to-connect routes (ConnectionFinder, A / X), hand teleport, input ignored over UI
- [x] V5: View modes (top routes / regional clusters), rank colour scale, hub labels — toggle moved from B / Y to one button, L3 (GraphViewMode.toggleBinding)
- [x] V6: Data dashboard (left menu button): overview chart, top airports / routes, traffic mix, clusters, selected airport, Find list (turns you to the airport), Filters, pinned comparison; follows the user, UI sounds
- [x] V7: Spoken narration via Meta Voice SDK / Wit.ai (NodeNarrator) and a guided tour (GuidedTour: 8 data-driven steps incl. insights and controls, input locked while it runs; caption with step titles, progress, Next / Stop)
- [x] V10: Controls reference: docs/CONTROLS.md (every button per panel / situation) and the dashboard's Controls page (UI/ControlsHelp.cs), shown in the tour
- [x] V11: Map view (GeoMapView, build_geomap.py): airports fly onto a gently curved map of Europe (azimuthal equidistant around the airports' smallest enclosing circle, Natural Earth, Turkish point of view with Western Sahara separate; dark map only, a satellite style was tried and dropped for readability); routes arc over it; the 22 airports beyond Europe sit on a band outside the rim in their true direction. B / M / dashboard / voice. Coordinates checked against OurAirports (all 200 within 2.5 km).
- [x] V12: Live flights (FlightSimulator + FlightPanel, build_flight_sim.py): every flight of the busiest day of Aug 2025 on the graph's routes (~25,000) as small aircraft in accelerated time; on the map at their real positions (EUROCONTROL tracks, gaps / no track: great circle with the real times), in 3D along the route arcs; panel with play / speed / time slider and a chart of aircraft in the air; routes dimmed and the graph on that month meanwhile. Replaces the flight-sim branch (its B / Y controls and old flat-map projection).
- [x] V8: Timeline (TimelinePanel + GraphLoader.SetPeriod): month slider over the 68 months with play / step / speed / all months; sizes, routes, selection, info panel and dashboard lists follow the month
- [x] V9: Dashboard redesign: sidebar navigation (Explore / Tools), page titles, cards, KPI tiles, tables with clickable rows, shared UIKit look (rounded boxes, hover colours, icons)

## Backlog
- [ ] B1: 3D view of the selected airport ("Fly to airport"). Options, pick by target platform:
  - A. Cesium for Unity + Google Photorealistic 3D Tiles — realistic terminals / aircraft / runways; needs a Google Map Tiles API key (billing account), streams over the internet, heavy: fine for the PC EXE over Link, risky on Quest standalone.
  - B. Cesium for Unity + Cesium OSM Buildings (Cesium ion free tier) — imagery + grey OSM building blocks; lighter than A, still online.
  - C. Offline OSM diorama — Python fetches runways / taxiways / terminals per airport once (Overpass API) into JSON, Unity builds a stylised miniature next to the node; works on Quest offline, fits the neon look. Needs "© OpenStreetMap contributors" (ODbL).
  - D. 360° street-level panoramas from Mapillary (CC-BY-SA) shown on an inverted sphere; not every airport has one. (Google Street View is not allowed outside Google Maps.)
  - Recommendation: A if demos run as EXE over Link, C if the app must run standalone on Quest.
- [x] B2: Voice commands — done as VoiceAssistant "Rebecca": push to talk on Y (N in the Editor) through the Voice SDK's AppVoiceExperience (Wit.ai, by reflection), so only what is said while Y is held reaches Wit; optional AlwaysListening mode acts on sentences starting with her name; airports / routes / filters / view / timeline / dashboard / tour / city groups / centre, matched against the loaded data; caption in view; F8 test phrase. Microphone checked first with MicTest. Not done: "compare X with Y" (the dashboard's pinned comparison is not public).
- [x] B3: Seated use / height calibration — done as GraphRecenter: R3 (right stick click), H, or the dashboard's Controls page teleports you to the graph centre facing its front (the graph stays put); matchEyeHeight (on by default) also sets your eyes to the centre's height and moves the teleport floors along. Original note: one button re-centres the graph at the current head height (and in front of the user) for seated people or different heights.
- [x] B4: Insights tab in the dashboard: facts found automatically in the data (fastest / slowest recovering airports, fastest growing route, where cargo dominates, ...); tapping a row selects the airport or route; the guided tour can reuse them.
- [ ] B5: Real-time voice conversation with Rebecca (Gemini Live API or OpenAI Realtime API): speech in, speech out, no Wit in between; the app's commands become the model's tools (show_airport, find_route, set_filter, set_month, open_dashboard, get_airport_stats, get_insights) so answers come from the loaded data.
  - Pros: very low latency, very natural voice, the user can interrupt her; feels most like talking to a real robot.
  - Cons: audio streams over a WebSocket, longer to set up in Unity; costs more than text models.
  - Before it: API key must not ship inside the EXE / APK (small proxy server, or a restricted key with a spending limit for demos); check whether the EUROCONTROL research licence allows sending figures derived from the data to the model provider.
  - Simpler first step (not started): Wit speech-to-text -> text LLM with the same tools -> Wit TTS, falling back to today's fixed commands without internet or a key.
- [ ] B6: Graph search algorithms (BFS / DFS) for flight and route finding, shown in VR. Today ConnectionFinder already runs one BFS (fewest stops, A / X or Shift + click) and shows only the result. Ideas: step-by-step visualisation of BFS (layer by layer) vs DFS (one branch at a time) from a selected airport; DFS for all routes up to k stops between two airports; weighted search (Dijkstra by distance or flight time) next to the fewest-stops one; reachability within k stops; respect the timeline month (only legs with flights that month) and the filters.
- [ ] B7: Real connections (time-feasible transfers) — TODO, later. Today "no direct flight from X to Y, go via Z" only means both legs exist somewhere in 2020-2025: the second flight could leave a day or years after the first lands. A real connection needs the second leg to depart within a transfer window after the first arrives (e.g. 45 min minimum connection time to 24 h), on the same day / dates. The EUROCONTROL flight lists have per-flight off-block and arrival times, so: build per-day (or sample-week) leg lists with times in the Python pipeline, then search earliest arrival over that timetable (time-dependent Dijkstra or the Connection Scan Algorithm) for a chosen date; show waiting times at the hubs; fall back to "no connection that day".
