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

Controller vibration (`ControllerHaptics`, in code) goes through each
controller's `HapticImpulsePlayer`, which the XRI Starter Assets rig
(XR Origin (XR Rig)) already has. That rig's Near-Far interactors also carry
`SimpleHapticFeedback`, which pulses on hover (0.25 for 0.1 s) and select
(0.5 for 0.1 s) of XR interactables, so the airports vibrate without our code;
our code adds the pulses it does not cover (UI buttons, aircraft, routes, a
found connection) and fills in the airport select pulse only when the rig has
none. Hand tracking has no vibration. Error messages of the route gestures use
`VoiceAssistant.ShowCaption`: without a `VoiceAssistant` in the scene they only
reach the Console.

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

## 7. Map view (geographic layout)

No scene change: the dashboard adds `GeoMapView` like the timeline. It needs
`geomap.json` and `geomap_dark.png` in StreamingAssets: they are committed under `data/processed`
(public domain map data) and copied by `scripts/sync_streaming_assets.sh`.
Without them the map view is unavailable and nothing else changes (Console:
"GeoMapView: 'geomap.json' not found").

- Rebuild the map (only if the airports change): `python scripts/build_geomap.py`
  (downloads Natural Earth once).
- `GeoMapView` fields to tune in the headset: `distance` (1.8 m), `height`
  (-0.35 m below the graph centre), `tilt` (30°: 0 = upright wall, 90 = table),
  `mapRadius` (1 m), `curvatureRadius` (3.5 m; > 0 globe-like, < 0 curves
  around you, 0 flat), `nodeScale`, `edgeWidthScale`, `arcHeight`,
  `toggleBinding` (B), `floorClearance` (0.1 m: the map is raised so its lowest
  part stays this far above the floor; the Console logs how much).
- Map names: `countryNameScale` (0.8), `outsideNameSize` (0.22), `onePerCity`
  (only the busiest airport of a city beyond the map is named) and
  `hideOverlappingNames` (a name that would overlap one already placed is left
  out; the Console says how many).
- Builds: the map uses runtime materials (`Universal Render Pipeline/Unlit`
  found by name). In the Editor over Link that is fine; for an EXE / APK assign
  an Unlit material to `GeoMapView.mapMaterial` so the shader is included.
- `HubLabels.layoutLabelScale` (0.35) shrinks the airport codes on the map.
Map size (2026-11-02): `mapRadius` 1.3 m (was 1.0), `tilt` 20° (was 30°: more
upright, facing the viewer), airports on the map `maxNodeDiameter` 0.04 m (was 0.03)
and `nodeScale` 0.6 (was 0.45). Its lowest part stays `floorClearance` above the
floor, so a bigger / more upright map is raised (the Console logs "map raised X m").
These are script defaults: a `GeoMapView` already in the scene keeps its Inspector
values. Radius and tilt are read at start; the airport sizes change live in Play mode.

## 8. Real-time voice with Gemini (TASKS B5, branch b5-realtime-voice)

`GeminiLiveAssistant` talks to the Gemini Live API over one WebSocket, straight
from Unity: hold Y (N in the Editor), talk, release; the answer is spoken in a
Gemini voice and can be interrupted with Y. Tools let the model run
`VoiceAssistant` commands and read figures from the loaded graph. While it is
connected, VoiceAssistant (Wit) only runs its commands and the Wit narrator
stops describing selections; without a connection both work as before.

1. API key from aistudio.google.com → Get API key. Put only the key in
   `gemini_key.txt` in `Application.persistentDataPath` (Windows:
   `%USERPROFILE%\AppData\LocalLow\<Company Name>\<Product Name>`; the exact
   path is logged on Play), or set `GEMINI_API_KEY`. Never inside the project.
2. Add `GeminiLiveAssistant` to the `VoiceInput` object (next to
   `VoiceAssistant`). Defaults: model `gemini-3.8-live`, voice Charon, helmet
   effect on. Change Voice in Play mode to compare (it reconnects).
3. EUROCONTROL scenes (`*_ectrl` files) stay on the Wit commands unless
   `allowLicensedData` is ticked — only when the data licence allows sending
   figures to Google.
4. Quest APK: Player Settings → Android → Internet Access = Require; copy the
   key file with `adb push gemini_key.txt /sdcard/Android/data/<package>/files/`.

The key is in memory while the app runs: fine for development and demos; a
shared build needs ephemeral tokens from a small server.

## 9. Live flights (flight simulation)

No scene change: the dashboard adds `FlightPanel`, which adds a
`FlightSimulator`. It needs `flights_ectrl.json` in StreamingAssets
(`python scripts/build_flight_sim.py`, then the sync script; EUROCONTROL-derived,
so it is shared outside git like the graph files). Without it the Live flights
button is disabled (Console: a warning, not an error).

- The aircraft are a low-poly 3D airliner built at runtime (or `aircraftMesh`),
  drawn instanced. With `litAircraft` (on) they use **URP / Simple Lit** so the
  scene's light shows their shape; an Unlit `aircraftMaterial` is replaced by it.
  For a build, assign a **Simple Lit** material with **Enable GPU Instancing** to
  `FlightSimulator.aircraftMaterial` so the shader is included (in the Editor over
  Link the shader is found by name).
- Tune: `mapAircraftSize` (3.8 cm), `aircraftSize` (3D, 11 cm), `mapCruiseHeight`
  (FL400 = 4 cm above the map), `speedSteps`, `FlightPanel.routeDimming` (0: route arcs hidden while live flights show; raise it to see them dimmed).
- Real connections (TASKS B7, `ConnectionFinder` + `ConnectionSearch`): while the
  Live flights panel is open, the route gesture shows actual flights of that
  day. Fields on `ConnectionFinder`: `minConnectionMinutes` (45),
  `maxWaitHours` (6), `maxFlights` (3), `sameAirlineOnly` (also a checkbox on
  the card). Needs `seg` (market segment) in `flights_ectrl.json`: rebuild it
  with the current `build_flight_sim.py` (an older file works, but cargo and
  business flights are then used too). `FlightSimulator.itineraryPathScale`
  (1.6) thickens the connection's path lines.
- Flight phase colours: `colorByPhase`, `climbColor`, `descendColor` (cruising =
  `aircraftColor`), `levelChangeFtPerMin` (300). Selected aircraft: `pickAngle`
  (2.5°; a near aircraft is hit anywhere on its visible size), `selectedColor`, `selectedScale`, `pathWidth` (4 mm), optional `pathMaterial`
  (vertex colours; Sprites/Default is created otherwise — assign one for builds).
- The route mesh renders before the other transparent objects (it used to be
  drawn over the dashboard and the info card).

## 10. Performance monitor (debug tool, TASKS B22)

No scene change: `PerfMonitor` adds itself when the scene has a `GraphLoader`.
**F3** turns it on / off (off at start; `showOnStart` to change). While on:

- A small text near the top right of the view: average FPS and frame time of the
  last second, the worst frame, frames over the headset's budget (its refresh rate,
  else `targetFps` 72), and what was going on (3D / map, timeline, flights, Gemini).
  Red when more than 10 % of the frames were over budget.
- One CSV row per second in `Application.persistentDataPath`
  (`perf_<date>_<time>.csv`; the path is logged when it starts): `time_s, scene,
  fps_avg, frame_ms_avg, frame_ms_worst, frames, over_budget, budget_fps, view,
  timeline_playing, flights_playing, gemini_speaking`. Numbers use a dot as the
  decimal separator on every PC.

Over Link the numbers are the PC's frame times, not the Quest's own. For "runs at
N FPS on Quest 3" in a paper, measure in a standalone APK build (or with OVR Metrics
Tool). The headset has no F3, so for an APK add the `PerfMonitor` component to any
object in the scene and tick **Show On Start** before building (it then does not add a
second one).
