# CLAUDE.md

FlightlyVR: Unity 6 VR app (Meta Quest, via Link or APK) for exploring airport networks.
The repo holds a Python data pipeline and the Unity C# scripts. It is **not** a full Unity
project: there is no ProjectSettings/ and no scene here.

## Hard rules

- **EUROCONTROL data is research-licensed.** Never commit raw or derived data
  (`Flights_*.csv*`, `*_ectrl.json`, `*_ectrl*.zip`, `data/processed/eurocontrol/` ...;
  see `.gitignore`). Check `git status` before every commit.
- **Secrets never go in git**: the Wit.ai token and the Gemini API key (`gemini_key.txt`
  lives in `Application.persistentDataPath`, or `GEMINI_API_KEY`). Never ask the user to
  send a key.
- `GeminiLiveAssistant` stays off for `*_ectrl` scenes unless the user ticks
  `allowLicensedData`. Do not change that default.
- Commit / push only when asked. Branch first for larger features (PRs into `main`).
- Keep `docs/CONTROLS.md` and `Assets/Scripts/UI/ControlsHelp.cs` in sync.
- Track work in `TASKS.md` (backlog items B1, B2, ...; `[x]` when done).

## Where things run

| Copy | Path | Notes |
|---|---|---|
| Repo (this) | `~/Documents/VR-Data-Exploration` | git, edit here |
| Mac Unity project | `~/Documents/FlighlyVR` | not git; copy changed scripts there to compile-check |
| Windows test PC | `C:\Users\admin\Desktop\FlighlyVR\FlighlyVR` | where the headset is tested; files are sent over (single files or zips of `Assets/Scripts` / `Assets/Scripts/UI`) |

The Mac Unity copy uses smaller node / edge defaults (`GraphLoader.nodeBaseSize 0.01f`,
`edgeWidth 0.004f`): keep them when copying `GraphLoader.cs` there.

## Commands

```sh
# Python pipeline (venv: .venv; requirements.txt)
.venv/bin/pytest tests/
python scripts/build_graph.py            # OpenFlights -> data/processed/nodes.json, edges.json
python scripts/build_graph_ectrl.py      # EUROCONTROL (licensed, local only)
python scripts/build_ectrl_extras.py
python scripts/layout_3d.py              # nodes_3d*.json (force-directed shell)
python scripts/build_geomap.py           # geomap.json + geomap_dark.png for the map view
scripts/sync_streaming_assets.sh [UNITY_PROJECT_DIR]   # copy outputs into StreamingAssets

# C# compile check on the Mac (after copying scripts to ~/Documents/FlighlyVR/Assets/Scripts)
/Applications/Unity/Hub/Editor/6000.3.24f1-arm64/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -quit -projectPath ~/Documents/FlighlyVR -logFile <log>
grep "error CS" <log>
```

There are no C# tests; the headset test on Windows is the real check. Say so when a
change is only compiled, not tested.

## Stack

Unity 6000.3.24f1, URP, OpenXR, XR Interaction Toolkit 3.6.1, new Input System,
Newtonsoft JSON, Meta Voice SDK (Wit.ai; used by reflection so the scripts compile
without it), Gemini Live API over a raw `ClientWebSocket`.

## Architecture (Assets/Scripts)

- `GraphLoader` loads `nodes*.json` / `edges*.json` / `meta_ectrl.json` from
  StreamingAssets (UnityWebRequest), builds nodes and one edge mesh, timeline periods,
  filters. Layouts plug in via `IGraphLayout` (`Home`, `FillEdge`, `NodeScale`,
  `EdgeWidthScale`); edges stay `minEdgeHeight` above the real floor (largest teleport
  area collider, `FloorY`).
- `GeoMapView` (+ `GeoMapProjection`): map layout of Europe; `maxNodeDiameter` caps node
  size on the map.
- `DataDashboard`: the menu; at runtime it adds `TimelinePanel`, `CityClusters`,
  `GraphRecenter`, `GeoMapView` when the scene has none.
- Selection / info: `GraphSelector`, `GraphInfoPanel`, `ConnectionFinder`,
  `GraphViewMode`, `HubLabels`, `GraphInsights`.
- XR: `XRGraphInput`, `HandTeleport`, `GraphRecenter` (teleports the user to the graph,
  `matchEyeHeight`), `FlyCamera` (Editor).
- Voice: `VoiceAssistant` (Wit, push-to-talk on left Y / N key, regex commands, easter
  eggs, public `RunCommand` / `FindAirport` / `ShowCaption`), `NodeNarrator`,
  `GuidedTour`, `GeminiLiveAssistant` (realtime voice, tools call
  `VoiceAssistant.RunCommand`; `commandsOnly`, `englishOnly`). `MicTest` is a debug tool.
- UI: `UI/UIKit` (shared look), `UIIcon`, `UISounds`, `ControlsHelp`, `UIChart`.

## Docs

- `docs/unity_setup_notes.md`: scene setup per feature (incl. Gemini key setup).
- `docs/CONTROLS.md`: every control per panel.
- `TASKS.md` / `PROGRESS.md`: task list and log.

## Style

C# matches the existing files: Inspector fields with short `[Tooltip]`s, components
that add their own helpers at runtime instead of requiring scene changes, Console logs
prefixed with the class name (`GraphLoader: ...`). The user writes in Turkish; answer
in Turkish.
