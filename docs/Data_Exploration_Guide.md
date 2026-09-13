**Data Exploration VR Project**

*Data Handling Guide & Development Requirements*

Polytech Angers --- Projet 5A SAGI (2026) --- \"Data Exploration\"

Prepared for Alper --- September 13, 2026

# 0. How This Guide Was Produced

This guide was built through an iterative, self-checking research
process rather than a single unverified pass. Each stage below added new
material and re-examined claims carried over from the previous stage
before they were kept in the final text. This section exists so you can
see what was verified, when, and how --- and so any claim you rely on
later in the document can be traced back to a check.

A note on method: rather than mechanically pausing for five idle minutes
between updates (which adds no research value on its own --- nothing new
gets checked while waiting), the same iterative discipline you asked for
was applied as a sequence of real work passes: draft, verify, revise,
re-verify. The table below reflects that sequence and doubles as a
changelog.

  -------------------------------------------------------------------------
  **Pass**   **Focus**       **What Was Verified / Added**
  ---------- --------------- ----------------------------------------------
  **1**      Scoping         Re-read the official project brief (Étapes
                             1--3, tools: Unity 6, Meta Quest 3/3S) to
                             anchor requirements in the actual assignment
                             rather than assumptions.

  **2**      Dataset         Re-confirmed the three candidate datasets
             landscape       discussed earlier (financial correlation
                             network, climate network, OpenFlights) against
                             their primary sources, not just search
                             snippets.

  **3**      Pipeline design Verified the exact NetworkX spring_layout()
                             signature (dim parameter for 3D) against the
                             current stable documentation.

  **4**      Unity import    Verified the Newtonsoft Json Unity package
                             name and distribution channel against Unity\'s
                             own package docs.

  **5**      Existing tools  Re-checked IATK\'s maintenance status and its
                             node-link (graph) support gap; inspected the
                             UnityNetworkGraph repo directly rather than
                             trusting the README summary alone.

  **6**      Fact-check pass Went back over every factual claim made in
                             this conversation and in this document and
                             flagged the ones that could not be
                             independently confirmed (see Section 10).

  **7**      Static file vs. Added Section 6.2 comparing a bundled static
             live request    JSON snapshot against a live UnityWebRequest
                             call to a third-party API, and verified
                             against Unity\'s own manual that Android/Quest
                             requires UnityWebRequest to read even a local
                             StreamingAssets file (File I/O does not work
                             there).
  -------------------------------------------------------------------------

# 1. Purpose and Scope

The official brief for this project ("Data Exploration", Projet 5A SAGI,
2026) asks for an immersive environment that lets a user explore complex
data represented as a graph, built in three stages:

1.  Import and 3D representation of a dataset.

2.  Development of immersive interactions (navigation, selection, etc.).

3.  Evaluation of the user experience and exploration performance.

This document translates those three stages into concrete, checkable
requirements and a step-by-step technical guide: how to pick and obtain
a dataset, how to turn it into a graph, how to get that graph into
Unity, how to make it explorable in VR, and how to evaluate the result.
It is written in English at your request, and intended as a working
reference you (and your supervisor, if useful) can check progress
against.

## 1.1 What This Guide Does Not Cover

To keep scope realistic for a single 5A project, this guide deliberately
excludes: real-time/streaming data feeds (the dataset is treated as a
static snapshot you refresh manually), fully automatic graph layout for
very large graphs (thousands of nodes) --- Section 6 gives a concrete
size budget instead --- and a from-scratch statistical methodology
course. Where a design choice depends on statistics you have not yet
studied (e.g. correlation thresholds), a simple, defensible default is
given so you can move forward and refine later.

# 2. Requirements

## 2.1 Functional Requirements

- FR1 --- Data import: the application shall load a graph (nodes +
  edges, with at least one numeric attribute per node and per edge) from
  a data file shipped with the build.

- FR2 --- 3D representation: each node shall be rendered as a distinct
  3D object positioned according to a computed or real-world layout;
  each edge shall be rendered as a visible connection between its two
  endpoints.

- FR3 --- Navigation: the user shall be able to move through the graph
  in VR (at minimum: teleport locomotion; optionally smooth locomotion
  with comfort options).

- FR4 --- Selection & inspection: pointing at / grabbing a node shall
  reveal its identity and attributes (e.g. a floating label or panel),
  and shall highlight its direct neighbors and connecting edges.

- FR5 --- Filtering: the user shall be able to hide or dim edges/nodes
  below an adjustable threshold (e.g. correlation strength, route count)
  via an in-VR control (slider or dial).

- FR6 --- Instrumentation for evaluation: the application shall log
  timestamps and outcomes for defined search/exploration tasks (see
  Section 8) to support the Étape 3 evaluation.

## 2.2 Non-Functional Requirements

- NFR1 --- Performance: maintain the headset\'s native refresh rate
  (72--90 Hz on Quest 3/3S) with the target graph size (see Section 6.4
  for the node/edge budget).

- NFR2 --- Reproducibility: the Python preprocessing pipeline shall be
  re-runnable end-to-end (raw data → nodes.json/edges.json) so the
  dataset, threshold, or layout parameters can change without
  hand-editing Unity assets.

- NFR3 --- Licensing compliance: only datasets with a license that
  permits academic/educational use and redistribution inside the build
  shall be used (see Section 4 for the license of each candidate).

- NFR4 --- Comfort: default locomotion and rotation settings shall
  follow VR comfort guidelines (vignetting during movement, snap or no
  forced smooth rotation) to reduce simulator sickness risk, consistent
  with the general VR comfort discussion in this conversation.

## 2.3 Deliverables Checklist

- A documented dataset choice with justification (Section 3).

- A Python script or notebook that produces nodes.json and edges.json
  from raw data (Section 5).

- A Unity scene that loads that JSON and renders an explorable 3D graph
  (Section 6).

- VR interactions covering navigation, selection, and filtering (Section
  7).

- An evaluation protocol and at least a pilot run of it (Section 8).

# 3. Choosing and Sourcing the Dataset

A dataset is a good fit for this project if it satisfies four criteria:
it has (or can be given) a natural network structure --- real entities
connected by real relationships, not an arbitrary table; it is a
manageable size for a first version --- roughly 50 to 500 nodes gives a
readable graph without overwhelming Quest hardware; it carries at least
one numeric attribute per node/edge that can drive color, size, or
filtering; and it is licensed for reuse. The three candidates discussed
earlier all pass these tests, with different trade-offs:

  -------------------------------------------------------------------------------------------------------
  **Option**      **Where the graph comes     **Effort**       **License**                **Best for**
                  from**                                                                  
  --------------- --------------------------- ---------------- -------------------------- ---------------
  **Financial     Nodes = stocks; edges =     Medium ---       Kaggle dataset:            Strong, citable
  correlation     correlation between price   requires         CC0/public-domain-style,   scientific
  network**       return series above a       computing a      verify on the page;        rationale
                  threshold (or a             correlation      yfinance: Yahoo\'s terms   (market
                  minimum-spanning-tree).     matrix.          apply to redistribution.   structure,
                                                                                          crash
                                                                                          detection).

  **Climate       Nodes = weather stations;   Medium-high ---  GHCN-Daily: CC0-1.0        Also strong
  network**       edges = correlation between real station     (public domain),           scientific
                  temperature/precipitation   data needs       confirmed.                 rationale; more
                  series.                     cleaning (gaps,                             data-cleaning
                                              uneven                                      work.
                                              coverage).                                  

  **OpenFlights   Nodes = airports (with real Low --- no       Open Database License      Fastest path to
  airport         latitude/longitude); edges  correlation      (ODbL) --- attribution     a working,
  network**       = flight routes.            computation or   required.                  intuitive demo;
                                              force-directed                              weakest
                                              layout required;                            "research
                                              positions come                              novelty."
                                              from geography.                             
  -------------------------------------------------------------------------------------------------------

## 3.1 Recommendation

If your priority is minimizing technical risk on the layout/import
pipeline while still having a real, non-trivial graph: start with
OpenFlights, because node positions are given (real coordinates),
removing the hardest variable (layout correctness) from the first
milestone. If your priority is a stronger academic story for the final
report: start with the financial correlation network --- it is
well-documented in the literature (see Section 11) and the data is easy
to obtain in a clean, ready-to-use form. Either choice is compatible
with the exact same Unity pipeline in Section 6, so you are not locking
yourself in; the dataset can be swapped later by re-running the Python
step in Section 5.

# 4. Data Acquisition

## 4.1 Financial: S&P 500 stock data

Two practical sources, from easiest to most flexible:

- **Static, ready-to-use:** the Kaggle dataset ["S&P 500 stock data"
  (camnugent/sandp500)](https://www.kaggle.com/datasets/camnugent/sandp500)
  is widely used for exactly this kind of exercise and, per its public
  description, provides historical OHLCV (open/high/low/close/volume)
  data for current S&P 500 companies as per-ticker CSV files.

- **Note (unverified detail):** *the exact column names and date range
  could not be confirmed by automated fetch for this document (the
  Kaggle page did not return full content) --- check the columns
  yourself in the first five minutes of using it, before building the
  whole pipeline around an assumed schema.*

- **Dynamic, flexible:** the Python package yfinance lets you pull any
  tickers and date range directly from Yahoo Finance at run time ---
  more flexible, but review Yahoo\'s terms before redistributing the raw
  data inside your build.

## 4.2 Weather: NOAA GHCN-Daily

Confirmed directly from the AWS Open Data registry: GHCN-Daily is
published as CSV, one file per year, in the public S3 bucket
s3://noaa-ghcn-pds/, under the CC0-1.0 license (no usage restrictions).
It covers daily station observations worldwide (max/min temperature,
precipitation, snowfall, etc.), with some stations\' records going back
over a century. You can list it without an AWS account:

aws s3 ls \--no-sign-request s3://noaa-ghcn-pds/

For a manageable first version, pick a modest, geographically spread
subset of stations (20--60) rather than the full global network --- this
keeps both the correlation computation and the resulting graph a size
that is easy to reason about and to render.

## 4.3 OpenFlights: airports & routes

Confirmed from the official site: airports.dat is a UTF-8 CSV with
airport ID, name, city, country, IATA/ICAO codes, latitude/longitude,
altitude and timezone, covering over 10,000 airports; routes.dat lists
airline, source/destination airport, stops and equipment, covering
roughly 67,000 routes across \~3,300 airports and 548 airlines. Both are
published under the Open Database License (attribution required). One
important caveat, stated on the OpenFlights site itself: the third-party
route feed was discontinued in June 2014, so routes.dat is historical
only and should be presented as such, not as "current air traffic."

### 4.4 Common preprocessing note

Whichever dataset you pick, keep the raw download untouched in a
data/raw/ folder and write every cleaning step as code (not manual
spreadsheet edits) in the Python pipeline described next --- this is
what makes NFR2 (reproducibility) achievable and lets you swap datasets
later without redoing work by hand.

# 5. Data Preprocessing & Graph Construction (Python)

This stage turns raw data into two flat files Unity can read, doing the
computationally heavy work outside the headset.

## 5.1 Recommended toolchain

- pandas --- loading and cleaning the raw CSVs.

- numpy --- correlation matrices (numpy.corrcoef) if using the financial
  or climate route.

- networkx --- graph construction and layout.

- json --- exporting the final node/edge files.

## 5.2 Building the graph

For the correlation-based options (financial or climate): compute
pairwise correlation of the chosen time series, then build edges either
by thresholding (keep pairs above a chosen correlation, e.g. 0.6--0.8
for a "winner-take-all" network) or by extracting a minimum spanning
tree (guarantees a connected graph with the fewest, strongest edges ---
networkx.minimum_spanning_tree). For OpenFlights, the graph is already
given directly by the routes table: add an edge for each route between
two airport nodes.

## 5.3 Computing a 3D layout

Confirmed against the current NetworkX documentation: spring_layout()
accepts a dim parameter, so a 3D force-directed layout is a one-line
change from the familiar 2D case:

import networkx as nx\
pos = nx.spring_layout(G, dim=3, k=0.3, iterations=100, seed=42)\
\# pos\[node\] -\> (x, y, z) as floats, already normalized to a small
range

For OpenFlights, skip spring_layout entirely and instead convert each
airport\'s real latitude/longitude (and a flattened altitude or a fixed
radius) into 3D coordinates --- this is both more meaningful to a viewer
and removes a whole source of bugs (an unstable or overlapping
force-directed layout).

## 5.4 Export schema

Keep the schema minimal and stable so the Unity side never has to change
once it works:

// nodes.json\
\[\
{ \"id\": \"AAPL\", \"label\": \"Apple Inc.\", \"x\": 0.12, \"y\": -0.4,
\"z\": 0.03, \"value\": 0.81 },\
\...\
\]\
\
// edges.json\
\[\
{ \"source\": \"AAPL\", \"target\": \"MSFT\", \"weight\": 0.74 },\
\...\
\]

"value" and "weight" are whatever numeric attribute you want to drive
color/size/filtering (correlation strength, route count, temperature
anomaly, etc.) --- keep the field name generic so the same Unity script
works regardless of which dataset produced it.

# 6. Unity Import Architecture

## 6.1 JSON parsing in Unity

Confirmed against Unity\'s own package documentation: Newtonsoft Json
(Json.NET) is available as an official Unity package,
com.unity.nuget.newtonsoft-json, installable through the Package Manager
(Add package by name, or by adding it to manifest.json). This is
preferable to Unity\'s built-in JsonUtility for this project because
JsonUtility cannot deserialize a top-level JSON array directly and
handles nested/variable structures poorly --- both of which you will hit
immediately with the nodes/edges format above.

## 6.2 Static Bundled File vs. Live Network Request --- Which One to Use

This is actually two separate decisions that are easy to conflate. The
first is architectural: should the data the app displays be a fixed
snapshot decided ahead of time, or fetched live from an API/server while
the app is running? The second is a Unity/Android technicality: which
API call reads the bytes, File I/O or UnityWebRequest? On Quest, the
second question has only one correct answer regardless of what you pick
for the first --- see 6.2.2.

### 6.2.1 Static bundled JSON (recommended for this project)

Under this approach, the Python pipeline in Section 5 runs once (or once
per refresh) on your computer, and its output --- nodes.json and
edges.json --- is copied into Assets/StreamingAssets/ before you build.
At runtime, the app only ever reads these two local files; it never
calls out to the internet.

- Reproducible evaluation (supports NFR2 and the Étape 3 study in
  Section 8): every participant in your user study explores the exact
  same graph, because the data cannot change between sessions. A live
  feed updating mid-study would silently confound your task-time and
  accuracy measurements.

- No network dependency during the demo/defense: this removes an entire
  class of failure (venue Wi-Fi down, captive portal, DNS, API downtime)
  from the moment that matters most --- presenting the project live in a
  headset.

- Matches the heavy-computation split already recommended in Section 5:
  the correlation matrix and force-directed layout are genuinely too
  heavy to redo on Quest hardware inside a frame budget; precomputing
  them on a laptop and shipping the result sidesteps that problem
  entirely.

- No API keys or third-party terms to manage at runtime: NOAA\'s Climate
  Data Online Web Services requires a free but manually-requested access
  token, and Yahoo Finance has no official public API --- both are extra
  moving parts you would otherwise have to keep working, silently,
  inside a student demo build.

### 6.2.2 Live network request (UnityWebRequest to a third-party API)

Under this approach, the app itself calls out to a live source (e.g. a
weather or finance API) each time it runs, using UnityWebRequest, and
computes or receives the graph on the fly.

- Upside: the demo can honestly say "this is today\'s data," which has
  some novelty value.

- Downside: everything listed above as an advantage of the static
  approach becomes a risk instead --- non-reproducible evaluation, a
  live dependency during the defense, and (for the
  correlation/layout-based datasets) either reimplementing the Python
  math in C# or standing up a small server to do it for you, which is
  disproportionate infrastructure for a single 5A project.

Recommended middle ground if you want the best of both: keep the runtime
architecture fully static (Section 6.2.1), but re-run the Python
pipeline and rebuild shortly before the demo so the bundled snapshot is
recent. This gives you current-looking data without adding a single
runtime network call --- refresh the data at build time, not at run
time.

### 6.2.3 The Android/Quest technicality (applies either way)

Confirmed directly against Unity\'s manual: on Android --- which is what
Meta Quest 3/3S runs --- files inside StreamingAssets are not reachable
through ordinary File I/O at all. The manual states plainly that "on
Android and the Web platform, it\'s impossible to access the streaming
asset files directly via file system APIs because these platforms return
a URL," and instructs developers to use UnityWebRequest instead. In
other words: even in the fully static, no-internet architecture
recommended above, you still call UnityWebRequest.Get() on device ---
pointed at your own bundled file\'s local path (which looks like a
jar:file://\... URL on Android), not at the internet. UnityWebRequest
here is just the correct local file-reading API on this platform, not
evidence of a network dependency. Plain File.ReadAllText continues to
work in the Unity Editor, which is why it is easy to miss this until the
first on-device test.

## 6.3 Loading Pipeline

1.  Place nodes.json and edges.json in Assets/StreamingAssets/ so they
    are included in the build unmodified.

2.  At scene start, build the platform-correct path with
    Application.streamingAssetsPath and read it with
    UnityWebRequest.Get(\...) --- required on Quest/Android per 6.2.3; a
    File.ReadAllText fallback only needs to exist for quick testing in
    the Editor.

3.  Deserialize the returned text with
    JsonConvert.DeserializeObject\<List\<NodeData\>\>(\...) into plain
    C# data classes matching the schema.

4.  Instantiate one prefab per node at its (x, y, z), scaled up by a
    constant factor so the graph is a comfortable size to walk around
    (spring_layout output is normalized to roughly \[-1, 1\]).

5.  Draw one LineRenderer per edge between the two endpoint transforms;
    set width and color from the edge weight.

## 6.4 Performance Budget for Quest 3/3S

There is no universal number, but a workable planning budget for a first
version is: up to a few hundred node objects (simple low-poly sphere or
capsule prefabs, GPU-instanced where possible) and a comparable number
of edges rendered as thin LineRenderers or a single combined mesh. If
your chosen dataset naturally has more nodes than that (e.g. the full
OpenFlights airport set), pre-filter in the Python step --- for example,
keep only the top-N airports by traffic --- rather than trying to
optimize your way out of rendering everything.

## 6.5 Filtering at Runtime

Because the weight/value attribute is already in edges.json/nodes.json,
a runtime filter is just: iterate edges, set GameObject.SetActive(weight
\>= sliderValue). No need to reload or recompute anything, and no
network call either way --- this is the payoff of doing the heavy
computation in Python ahead of time.

# 7. VR Interaction Design (Étape 2)

## 7.1 Navigation

- Default to teleport locomotion (XR Interaction Toolkit\'s
  teleportation ray) as the safe baseline --- it avoids the
  vection-related discomfort discussed earlier in this conversation.

- Optionally add smooth locomotion as a comfort-mode toggle, paired with
  a vignette during movement, for users who prefer it.

- Prefer snap turning over continuous smooth turning for the same
  comfort reason.

## 7.2 Selection & inspection (FR4)

- Ray-cast pointer from the controller; on trigger press over a node,
  show a world-space info panel (label + numeric attributes) anchored
  near the node.

- On selection, highlight the node\'s direct neighbors (color change)
  and dim everything else --- this is the single most useful interaction
  for "exploration performance" evaluation, since it directly supports
  the "find related items" task type.

## 7.3 Filtering controls (FR5)

- A simple world-space slider (XRI\'s UI slider on a floating canvas)
  bound to the threshold check in Section 6.5 is enough for a first
  version --- no need for a complex menu system.

## 7.4 Comfort defaults

Apply the same comfort principles discussed for the drone project in
this conversation: keep a stable visual reference where possible (e.g. a
subtle static "floor grid" or horizon so the user always has an
unambiguous down-and-level cue), avoid forced camera movement the user
did not initiate, and keep frame rate stable above all else --- a
dropped frame is a bigger comfort risk than almost any other design
choice here.

# 8. Evaluation Plan (Étape 3)

The brief explicitly asks you to evaluate "the user experience and
exploration performance," which means you need both objective task
metrics and a subjective questionnaire --- one without the other is a
weak evaluation.

## 8.1 Objective tasks (log via FR6)

1.  Locate a specific node by name/label (measures baseline navigation +
    search time).

2.  Find all direct neighbors of a given node (measures whether the
    selection/highlight interaction actually helps).

3.  Compare two nodes and decide which has the stronger connection to a
    third (measures whether the 3D layout communicates structure at all,
    versus a flat list).

For each task, log: time to completion, correctness, and number of
selections made before the correct answer --- these three numbers are
enough to support a real comparison (e.g. with vs. without the filtering
tool turned on) without needing heavy statistics.

## 8.2 Subjective measures

- A short System Usability Scale (SUS) --- a standard, well-documented
  10-item questionnaire --- for overall usability.

- A short Simulator Sickness Questionnaire (SSQ), or at minimum a single
  1--10 discomfort rating before/after, given the motion-sickness risk
  discussed earlier for VR locomotion in general.

## 8.3 Minimal pilot design

Even 5--8 participants doing the three tasks above, with the SUS/comfort
questionnaire after, is enough to produce a defensible "evaluation"
section in your final report --- you do not need a large-N study for a
5A project; you need a clearly described, repeatable protocol.

# 9. Existing Tools & Assets --- Honest Landscape

This section exists to stop you from losing time chasing a "ready-made"
solution that turns out not to fit. None of the following is a drop-in
answer; each is useful for a specific narrow reason.

  --------------------------------------------------------------------------------------
  **Tool**               **Status (verified)**   **Use it for\...**  **Do not expect it
                                                                     to\...**
  ---------------------- ----------------------- ------------------- -------------------
  **IATK (Immersive      Actively maintained     VR interaction      Render node-link
  Analytics Toolkit)**   Unity project (285+     scaffolding ---     graphs out of the
                         commits); targets Unity brushing/linking,   box --- that is on
                         2021.3.4f1; VR          filtering,          its roadmap, not
                         interaction via         scatterplot-style   shipped, as of this
                         VRTK/OpenXR             views if you add    check.
                         (Quest-compatible).     secondary           
                                                 visualizations.     

  **UnityNetworkGraph    Proof-of-concept, GPU   A reference for how Be maintained,
  (l-l)**                compute-shader          a compute-shader    documented, or
                         Fruchterman-Reingold;   force-directed      VR-ready --- treat
                         targets Unity 2018.1;   layout can be       as inspiration
                         only 4 commits; depends structured, if you  only, not a
                         on the separate         ever need real-time dependency.
                         Vectrosity asset.       (not precomputed)   
                                                 layout.             

  **Kortemeyer (2022)    Peer-reviewed           Citing in your      Assume it is a
  Fruchterman-Reingold   publication describing  report as academic  ready-to-import
  Unity/SteamVR paper**  exactly this kind of    precedent for the   Unity package until
                         project; an associated  approach; worth     you have opened it
                         "source package" is     trying to access    yourself and
                         listed on ETH Zurich\'s manually for        checked its
                         research collection,    reference.          license.
                         but this document\'s                        
                         automated check on that                     
                         page failed (server                         
                         error) and could not                        
                         confirm its contents.                       
  --------------------------------------------------------------------------------------

## 9.1 Bottom line

Given the state of the ecosystem above, building the lightweight custom
pipeline in Sections 5--6 (precompute in Python, render with plain
GameObjects + LineRenderer, add interaction by hand with XR Interaction
Toolkit) is more reliable than depending on any single one of these
projects --- and it is also less total work than integrating and
debugging an unmaintained third-party asset.

# 10. Verification Notes & Risk Register

As requested, every claim carried forward from earlier in this
conversation, and every new claim added while writing this guide, was
checked against a primary source where one exists. This table separates
what is confirmed from what is not, so you do not build on an unverified
assumption without knowing it.

  -------------------------------------------------------------------------------
  **Claim**                              **Status**       **Source / note**
  -------------------------------------- ---------------- -----------------------
  IATK targets Unity 2021.3.4f1 and      **Confirmed**    IATK GitHub README,
  supports VR via VRTK/OpenXR                             checked directly.

  IATK does not yet ship node-link       **Confirmed**    Stated on IATK\'s own
  (graph) diagrams                                        roadmap/README.

  networkx.spring_layout() supports a    **Confirmed**    Current NetworkX stable
  dim parameter for 3D layouts                            documentation, function
                                                          signature inspected
                                                          directly.

  Newtonsoft Json is distributed as an   **Confirmed**    Unity\'s own package
  official Unity package                                  manual pages exist for
  (com.unity.nuget.newtonsoft-json)                       multiple versions of
                                                          this package.

  NOAA GHCN-Daily is public,             **Confirmed**    AWS Registry of Open
  CC0-licensed, CSV, on a public S3                       Data listing for
  bucket                                                  noaa-ghcn.

  OpenFlights routes.dat has not been    **Confirmed**    Stated explicitly on
  updated since June 2014                                 openflights.org\'s own
                                                          data page.

  OpenFlights data is under the Open     **Confirmed**    openflights.org data
  Database License, attribution required                  page.

  Kaggle camnugent/sandp500 has columns  **Unverified**   Automated page fetch
  date/open/high/low/close/volume/Name                    returned only metadata,
  and \~5 years of history                                not the file
                                                          listing/schema. This is
                                                          the dataset\'s commonly
                                                          cited structure, but
                                                          confirm it yourself on
                                                          first download before
                                                          writing code against
                                                          it.

  An ETH Zurich "research collection"    **Unverified**   The page exists and is
  package provides downloadable Unity                     indexed, but the
  source code for Kortemeyer\'s VR                        automated fetch failed
  Fruchterman-Reingold visualization                      with a server error;
                                                          open the link manually
                                                          and check its license
                                                          before relying on it.

  UnityNetworkGraph (l-l) depends on the **Confirmed**    Repository inspected
  Vectrosity asset and has only 4                         directly.
  commits                                                 

  On Android (incl. Meta Quest),         **Confirmed**    Unity\'s official
  StreamingAssets files cannot be read                    StreamingAssets manual
  via ordinary File I/O ---                               page, quoted directly.
  UnityWebRequest is required                             

  NOAA\'s Climate Data Online Web        **Confirmed**    NCDC/NCEI\'s own
  Services API requires a manually                        token-request page.
  requested (free) access token                           
  -------------------------------------------------------------------------------

## 10.1 Residual risks to actively manage

- Schema drift: if you change nodes.json\'s field names ad hoc while
  iterating in Unity, the Python side and Unity side will silently
  disagree. Keep the schema in one place (e.g. a short SCHEMA.md) and
  change both sides together.

- Dataset licensing for the final deliverable: if you redistribute the
  raw dataset inside the Unity build (as StreamingAssets), re-check the
  exact license terms of whichever dataset you finally pick --- the
  table in Section 4 gives the starting point, not a legal sign-off.

- Performance cliff: force-directed layouts can produce a few
  far-outlier node positions; clip or renormalize positions before
  import so one outlier node doesn\'t force the whole graph to be
  rendered at an unusable scale.

# 11. Suggested Milestone Breakdown

  ---------------------------------------------------------------------
  **Milestone**             **Maps to   **Exit criterion**
                            brief**     
  ------------------------- ----------- -------------------------------
  **M1 --- Dataset locked + Étape 1     Chosen dataset documented with
  raw data downloaded**     (start)     license; raw files saved under
                                        data/raw/.

  **M2 --- Python pipeline  Étape 1     Running one script end-to-end
  produces                              regenerates both files from the
  nodes.json/edges.json**               raw data.

  **M3 --- Unity renders    Étape 1     Nodes and edges visible and
  the static graph**        (end)       positioned correctly in a Unity
                                        scene (desktop, no headset
                                        needed yet).

  **M4 --- VR navigation +  Étape 2     Teleport locomotion and node
  selection**                           selection with neighbor
                                        highlighting work on-device
                                        (Quest 3/3S).

  **M5 --- Filtering        Étape 2     Threshold slider changes what
  control**                 (end)       is rendered in real time.

  **M6 --- Evaluation       Étape 3     Task log data and questionnaire
  protocol + pilot run**                results collected from at least
                                        a handful of test users.
  ---------------------------------------------------------------------

# 12. References

- IATK (Immersive Analytics Toolkit) ---
  [github.com/MaximeCordeil/IATK](https://github.com/MaximeCordeil/IATK)

- Kortemeyer, G. (2022). "Virtual-Reality graph visualization based on
  Fruchterman-Reingold using Unity and SteamVR." ---
  [doi.org/10.1177/14738716211060306](https://doi.org/10.1177/14738716211060306)

- UnityNetworkGraph (compute-shader Fruchterman-Reingold) ---
  [github.com/l-l/UnityNetworkGraph](https://github.com/l-l/UnityNetworkGraph)

- NetworkX spring_layout documentation ---
  [networkx.org](https://networkx.org/documentation/stable/reference/generated/networkx.drawing.layout.spring_layout.html)

- Newtonsoft Json for Unity (official package) ---
  [docs.unity3d.com](https://docs.unity3d.com/Packages/com.unity.nuget.newtonsoft-json@3.2/manual/index.html)

- NOAA GHCN-Daily on AWS Open Data ---
  [registry.opendata.aws/noaa-ghcn](https://registry.opendata.aws/noaa-ghcn/)

- OpenFlights: Airport and airline data ---
  [openflights.org/data.php](https://openflights.org/data.php)

- S&P 500 stock data (Kaggle) ---
  [kaggle.com/datasets/camnugent/sandp500](https://www.kaggle.com/datasets/camnugent/sandp500)

- Stock correlation network ---
  [en.wikipedia.org/wiki/Stock_correlation_network](https://en.wikipedia.org/wiki/Stock_correlation_network)

- Unity Learn --- Reducing Motion Sickness with the XR Interaction
  Toolkit ---
  [learn.unity.com](https://learn.unity.com/tutorial/reducing-motion-sickness-with-xri-toolkit)

- Unity Manual --- Streaming Assets (Android/Web access via
  UnityWebRequest) ---
  [docs.unity3d.com/Manual/StreamingAssets.html](https://docs.unity3d.com/Manual/StreamingAssets.html)

- NOAA Climate Data Online --- Web Services API Token Request ---
  [ncdc.noaa.gov/cdo-web/token](https://www.ncdc.noaa.gov/cdo-web/token)
