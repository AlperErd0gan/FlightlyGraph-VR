# FlightlyVR

Immersive exploration of airport networks on Meta Quest. A Unity 6 VR app shows a
network of airports and routes in 3D, on a map of Europe and on a dome around you
("you at the centre of the Earth"). It has a timeline over 68 months, a day of live
flights, real connection search and voice control (Wit.ai commands and a Gemini Live
assistant).

This repository holds the **Python data pipeline** and the **Unity C# scripts**. It is
not a full Unity project: there is no `ProjectSettings/` and no scene. The scripts are
copied into a Unity 6 project (see `docs/unity_setup_notes.md`).

> **Data licence.** The EUROCONTROL R&D data is research-licensed. Raw and derived
> EUROCONTROL files (`Flights_*.csv*`, `*_ectrl.json`, `data/processed/eurocontrol/` …)
> are never committed (see `.gitignore`, enforced by CI). OpenFlights (ODbL) and
> Natural Earth (public domain) outputs are in the repo.

---

## Features

| Area | What it does | Main scripts |
|---|---|---|
| Graph | 200 busiest airports, routes as one ribbon mesh, sizes by traffic, regional clusters, city groups | `GraphLoader`, `GraphViewMode`, `CityClusters`, `HubLabels` |
| Layouts | 3D force layout, flat map of Europe, dome (true-scale world around you); spring transitions | `GeoMapView`, `GeoMapProjection`, `DomeToggle` |
| Selection | Ray / pinch / mouse; hover names; info cards; route between two airports; BFS / DFS search | `XRGraphInput`, `GraphSelector`, `HoverLabel`, `GraphInfoPanel`, `ConnectionFinder`, `GraphSearch` |
| Time | Timeline over months; live flights of one day (≈25 k aircraft, real tracks) | `TimelinePanel`, `FlightPanel`, `FlightSimulator` |
| Connections | Real connections over one day's passenger flights (≤ 3 flights, 45 min – 6 h changes) | `ConnectionSearch`, `ConnectionFinder` |
| Voice | Push-to-talk regex commands (Wit.ai), narration, guided tour, realtime Gemini | `VoiceAssistant`, `NodeNarrator`, `GuidedTour`, `GeminiLiveAssistant` |
| UI | Start screen + wrist Home / Menu, dashboard with charts, panels that grow from their source, Reduce motion | `StartPanel`, `DataDashboard`, `UIKit`, `PanelTransition`, `Comfort` |
| Tools | FPS monitor with CSV, mic test | `PerfMonitor`, `MicTest` |

All controls: `docs/CONTROLS.md`.

---

## Architecture

### 1. System overview

```mermaid
flowchart LR
    subgraph Sources["Data sources"]
        OF["OpenFlights<br/>airports.dat, routes.dat"]
        EC["EUROCONTROL R&D archive<br/>Flights_*, Flight_Points_Actual_*<br/>(licensed, local only)"]
        NE["Natural Earth<br/>countries, coastline, lakes"]
    end

    subgraph Pipeline["Python pipeline (scripts/)"]
        PY["build_*.py, layout_3d.py"]
    end

    subgraph Processed["data/processed/"]
        JSON["nodes*.json, edges*.json,<br/>meta_ectrl.json, flights_ectrl.json"]
        MAP["geomap.json, geomap_dark.png,<br/>globe_dark.png, globe_labels.json"]
    end

    subgraph Unity["Unity 6 project"]
        SA["Assets/StreamingAssets"]
        APP["C# scripts<br/>(Assets/Scripts)"]
    end

    subgraph Devices["Run on"]
        LINK["Quest via Link / Air Link<br/>(PC renders)"]
        APK["Quest standalone APK"]
        DESK["Desktop / Editor<br/>(keyboard + mouse)"]
    end

    OF --> PY
    EC --> PY
    NE --> PY
    PY --> JSON
    PY --> MAP
    JSON -- "sync_streaming_assets.sh" --> SA
    MAP -- "sync_streaming_assets.sh" --> SA
    SA --> APP
    APP --> LINK
    APP --> APK
    APP --> DESK
```

### 2. Data pipeline

```mermaid
flowchart TD
    A["build_graph.py<br/>(OpenFlights)"] --> A1["nodes.json<br/>edges.json"]
    B["build_graph_ectrl.py<br/>(EUROCONTROL, licensed)"] --> B1["nodes_ectrl.json<br/>edges_ectrl.json<br/>meta_ectrl.json"]
    B --> C["build_ectrl_extras.py"]
    A1 --> D["layout_3d.py<br/>force-directed shell"]
    B1 --> D
    D --> D1["nodes_3d.json<br/>nodes_3d_ectrl.json"]
    E["build_geomap.py<br/>(Natural Earth + airports)"] --> E1["geomap.json<br/>geomap_dark.png"]
    F["build_globe.py<br/>(Natural Earth)"] --> F1["globe_dark.png<br/>globe_labels.json"]
    G["build_flight_sim.py<br/>(EUROCONTROL tracks)"] --> G1["flights_ectrl.json"]
    B1 --> G

    A1 & B1 & D1 & E1 & F1 & G1 --> S["sync_streaming_assets.sh<br/>→ Assets/StreamingAssets"]
```

Tests: `.venv/bin/pytest tests/` (CI runs them and checks that no licensed data is committed).

### 3. Unity runtime components

Most components add themselves at runtime when the scene has a `GraphLoader`, so a
scene needs only the XR rig, the graph object and the dashboard.

```mermaid
flowchart TB
    GL["GraphLoader<br/>loads JSON, builds nodes + edge mesh,<br/>periods, filters, floor"]
    LAYOUT{{"IGraphLayout"}}
    GL --> LAYOUT
    LAYOUT --> L3D["3D layout<br/>(nodes_3d)"]
    LAYOUT --> GEO["GeoMapView<br/>flat map / dome"]
    GEO --> PROJ["GeoMapProjection<br/>azimuthal equidistant"]
    DT["DomeToggle"] --> GEO

    DB["DataDashboard<br/>(menu, charts)"]
    DB -. "adds if missing" .-> TP["TimelinePanel"]
    DB -. "adds if missing" .-> CC["CityClusters"]
    DB -. "adds if missing" .-> GR["GraphRecenter"]
    DB -. "adds if missing" .-> GEO
    DB -. "adds if missing" .-> FP["FlightPanel"]
    FP --> FS["FlightSimulator<br/>GPU-instanced aircraft"]
    FS --> GEO

    XR["XRGraphInput<br/>rays, pinch, mouse"] --> SEL["GraphSelector"]
    SEL --> INFO["GraphInfoPanel<br/>cards"]
    SEL --> CF["ConnectionFinder"]
    CF --> CS["ConnectionSearch<br/>real connections"]
    CS --> FS
    SEL --> NN["NodeNarrator"]
    HL["HoverLabel"] --> XR

    VA["VoiceAssistant<br/>Wit.ai, RunCommand"] --> DB
    VA --> SEL
    VA --> GEO
    VA --> TP
    VA --> FP
    GEM["GeminiLiveAssistant<br/>realtime voice"] -- "tool calls" --> VA
    TOUR["GuidedTour"] --> VA

    SP["StartPanel<br/>start screen, wrist Home / Menu"] --> DB
    UI["UIKit · PanelTransition · Comfort ·<br/>ControllerHaptics · UISounds"] -. "shared by all panels" .-> DB

    GL --> SEL
    GL --> TP
```

### 4. Views and switches

```mermaid
stateDiagram-v2
    [*] --> Start: app opens
    Start --> Graph3D: Start exploring / Guided tour
    Graph3D --> FlatMap: B / dashboard "Map view" / voice "map view"
    FlatMap --> Graph3D: B / dashboard "3D view"
    FlatMap --> Dome: Dome button / G
    Dome --> FlatMap: Flat map button / G
    Dome --> Graph3D: B (next map opens flat)
    Graph3D --> Start: wrist Home
    FlatMap --> Start: wrist Home
    Dome --> Start: wrist Home

    note right of Dome
        EUROCONTROL graph only.
        World texture + country names,
        true scale, Europe 40° up.
    end note
```

### 5. Route between two airports (with live flights)

```mermaid
sequenceDiagram
    actor U as User
    participant XR as XRGraphInput
    participant S as GraphSelector
    participant CF as ConnectionFinder
    participant CS as ConnectionSearch
    participant FS as FlightSimulator
    participant IP as GraphInfoPanel

    U->>XR: trigger on airport A
    XR->>S: SelectNode(A)
    U->>XR: hold trigger on airport B (or A / X)
    XR->>CF: Connect(A, B)
    alt live flights shown
        CF->>CS: search that day's passenger flights
        CS-->>CF: itinerary (≤ 3 flights)
        CF->>FS: ShowItinerary(flights)
        CF->>IP: ShowConnection(itinerary)
    else graph only
        CF->>CF: BFS, fewest stops
        CF->>IP: show path
    end
    IP-->>U: card + highlighted route
```

### 6. Voice

```mermaid
sequenceDiagram
    actor U as User
    participant VA as VoiceAssistant (Wit.ai)
    participant GEM as GeminiLiveAssistant
    participant APP as Dashboard / Selector / Map / Timeline / Flights

    U->>VA: hold Y (N in the Editor), speak
    alt Gemini on (key set, allowed for the dataset)
        VA->>GEM: audio stream (WebSocket)
        GEM->>VA: tool call RunCommand("route from London to Rome")
    else Wit only
        VA->>VA: transcript → regex commands
    end
    VA->>APP: run the command
    APP-->>U: view changes, caption, spoken answer
```

---

## Repository layout

```
Assets/Scripts/          C# scripts (copy into the Unity project)
  Data/                  JSON data classes
  UI/                    UIKit, StartPanel, ControlsHelp, PanelTransition, Comfort, …
Assets/Shaders/          EdgeRibbon.shader
Assets/StreamingAssets/  sample OpenFlights data (the app reads its data from here)
scripts/                 Python pipeline + sync_streaming_assets.sh
tests/                   pytest for the pipeline
data/processed/          pipeline outputs (licensed files ignored)
docs/                    controls, Unity setup, literature review, research questions
TASKS.md / PROGRESS.md   backlog (B1, B2, …) and log
```

## Quick start

```sh
# 1. Python pipeline
python -m venv .venv && .venv/bin/pip install -r requirements.txt
.venv/bin/python scripts/build_graph.py      # OpenFlights graph
.venv/bin/python scripts/layout_3d.py
.venv/bin/python scripts/build_geomap.py     # map of Europe
.venv/bin/python scripts/build_globe.py      # dome: world texture + country names
.venv/bin/pytest tests/

# 2. Copy outputs into a Unity project
scripts/sync_streaming_assets.sh /path/to/UnityProject
```

3. Unity 6000.3.24f1 (URP), with OpenXR, XR Interaction Toolkit 3.6.1, XR Hands, the
   Input System, Newtonsoft JSON and (optional) the Meta Voice SDK. Copy
   `Assets/Scripts` and `Assets/Shaders` into the project and follow
   `docs/unity_setup_notes.md`.
4. Gemini (optional): put the API key in `gemini_key.txt` in
   `Application.persistentDataPath`, or set `GEMINI_API_KEY`. Never commit it. Gemini
   stays off for the EUROCONTROL scenes unless `allowLicensedData` is ticked.

## Docs

- `docs/CONTROLS.md`: every control (controller, hands, keyboard, voice)
- `docs/unity_setup_notes.md`: scene setup per feature
- `docs/literature_review/`: literature review (LaTeX)
- `TASKS.md`: backlog and status of each item
