Autonomous overnight loop, target cadence ~30 minutes. No one is
watching — do not ask questions or wait for confirmation. Make the most
reasonable choice, write it down, and keep going. Work only on branch
overnight-auto (create if missing); never touch main; never push
anywhere; never force-push or hard-reset.

NOTE: No local Unity installation is available in this environment. All
C#/Unity-side work must be written and semantically reviewed as described
below — do NOT attempt to invoke a Unity executable, batch mode, or any
Unity-dependent build/test command. Do not fabricate a "compiled" or
"tests passed" result for anything Unity-related.

=== PHASE 1 SPEC (authoritative for this loop — supplements
docs/Data_Exploration_Guide.md, which does not contain these specific
implementation details) ===

--- Part A: Data pipeline (OpenFlights) ---
Deliverable: data/processed/nodes.json and edges.json.
- Download (into data/raw/):
  https://raw.githubusercontent.com/jpatokal/openflights/master/data/airports.dat
  https://raw.githubusercontent.com/jpatokal/openflights/master/data/routes.dat
  Both headerless CSV. airports.dat columns, in order: airport_id, name,
  city, country, iata, icao, latitude, longitude, altitude, timezone,
  dst, tz_database, type, source. routes.dat columns: airline,
  airline_id, source_airport, source_airport_id, dest_airport,
  dest_airport_id, codeshare, stops, equipment.
- scripts/build_graph.py, runnable end-to-end, no manual steps:
  drop airports with invalid lat/lon; keep only the top ~150-300
  airports by route-count degree (log how many were dropped); keep only
  routes where both endpoints survived; convert (lat, lon, altitude) to
  (x, y, z) via an equirectangular-style projection, normalized to
  roughly [-10, 10]; node "value" = route-count degree; edge "weight" =
  count of distinct routes between that pair (collapse duplicates, don't
  emit parallel edges).
- Export schema (field names are exact, Unity reads them verbatim):
  nodes.json: [{ "id", "label", "x", "y", "z", "value" }, ...]
  edges.json: [{ "source", "target", "weight" }, ...]
- Validate: both files parse as JSON; every edge's source/target exists
  in nodes.json; log final node/edge counts.
- data/README.md: OpenFlights Open Database License, attribution
  required; note routes.dat is historical only (no updates since June
  2014) — describe the network as illustrative, not current traffic.
- tests/test_graph_pipeline.py (pytest): assert both files exist and are
  valid JSON; assert node count is within the 150-300 budget; assert
  every edge references a real node id; assert no duplicate (source,
  target) pairs.

--- Part B: Unity import layer (code only — no Unity install here) ---
Deliverable: C# source that WOULD render the Part A graph correctly once
opened in Unity by a human later. You cannot run or compile it in this
environment — write it carefully and review it as described in VERIFY
below.
- A short note/script describing how nodes.json/edges.json should be
  synced into Assets/StreamingAssets/ (a human will run this once Unity
  is available; don't assume an Assets/ folder exists yet).
- Add-package instruction file (e.g. docs/unity_setup_notes.md): add
  com.unity.nuget.newtonsoft-json via Package Manager ("Add package by
  name", let Unity resolve the version) — this is a manual step for the
  human, just document it.
- Assets/Scripts/Data/GraphData.cs:
  [System.Serializable] public class NodeData { public string id, label;
  public float x, y, z; public int value; }
  [System.Serializable] public class EdgeData { public string source,
  target; public int weight; }
- Assets/Scripts/GraphLoader.cs (MonoBehaviour):
  Inspector fields: optional node prefab (fallback to
  GameObject.CreatePrimitive(PrimitiveType.Sphere) if unset), LineRenderer
  material, position scale multiplier, JSON filenames.
  ALWAYS read both files via UnityWebRequest against
  Application.streamingAssetsPath (never branch File.ReadAllText vs.
  UnityWebRequest by platform — UnityWebRequest works in-Editor too, and
  plain File I/O does not work on Android/Quest at all). Check
  request.result == UnityWebRequest.Result.Success before touching
  downloadHandler.text.
  Deserialize with JsonConvert.DeserializeObject<List<T>>. Instantiate
  one object per node at (x,y,z)*scale; keep a Dictionary<string,
  Transform> id->instance. For each edge, use TryGetValue for both
  endpoints; if either is missing, log a warning and skip (don't throw
  or index a missing key). Draw a LineRenderer per surviving edge,
  width/color driven by weight. Public method
  SetEdgeWeightThreshold(float t): SetActive(weight >= t) on all edges —
  leave unwired (no UI yet, that's a future task).
  Log final loaded node/edge counts on Start.
- Do NOT implement VR locomotion, selection, filtering UI, or evaluation
  logging (Sections 7-8 of the guide) — out of scope for this loop.

=== LOOP MECHANICS ===

Every cycle, even if you recall earlier cycles in this conversation,
re-verify ground truth from disk first:
1. Read PROGRESS.md (create if missing).
2. Read TASKS.md (create if missing) by breaking Part A into D1-D5 and
   Part B into U1-U6, in dependency order (D before U, since U's review
   step re-checks against D's actual output file).
3. Run `git log --oneline -20` and `git status`; if the repo disagrees
   with PROGRESS.md, trust the repo and correct PROGRESS.md.

Each cycle:
- Pick the single next unblocked task.
- Do one bounded chunk of work you can finish and commit this cycle.
- VERIFY before marking anything done — the check differs by part:

  PART A (Python) — real automated check:
  Run `pytest tests/`. Only tick the task if it exits 0. If it fails,
  fix the issue or, if genuinely blocked, log it and move to another
  task — never tick a task whose test is failing.

  PART B (C#/Unity) — no compiler available, so run this semantic
  review checklist on every .cs file you write or touch instead, and
  record the results in PROGRESS.md as "semantically reviewed — NOT
  compiled or run":
    a. Delimiter sanity: read the whole file and confirm every brace,
       parenthesis, and bracket is balanced, every statement ends in
       ';', and every method/class opened is properly closed.
    b. Schema cross-check: open data/processed/nodes.json and edges.json
       (from Part A's actual output, not the spec text) and confirm the
       real field names and value types match GraphData.cs exactly —
       field-by-field, not just "looks similar".
    c. API-usage check: re-read each UnityWebRequest, JsonConvert,
       LineRenderer, Transform, and Dictionary call against the intended
       behavior described in Part B, line by line, and confirm: the
       success/error result is checked before parsing; every dictionary
       lookup used for edges is TryGetValue (never a direct indexer that
       could throw); the scale multiplier is actually applied to x, y,
       and z; the threshold method actually mutates the state it claims
       to.
    d. Trace the full execution path by hand from Start() to the last
       LineRenderer being drawn, step by step, and write that trace into
       PROGRESS.md as evidence of the review (not just "looks fine").
    e. List, explicitly, anything you are NOT confident about without a
       real compiler (e.g. an overload you're not 100% sure exists, a
       coroutine/async pattern you couldn't verify) under "## Needs Real
       Verification (no Unity available)" in PROGRESS.md.
  Only tick a Part B task as done in TASKS.md with the status
  "implemented — semantically reviewed, not compiled"; never write
  "tested" or "passed" for anything under Part B.

- If genuinely blocked (ambiguous requirement, missing tool): log under
  "## Blocked" in PROGRESS.md, switch tasks, continue.
- Commit with a message referencing the task ID (e.g. "D3: coordinate
  projection + JSON export", "U3: GraphLoader — semantically reviewed").
- Update PROGRESS.md: timestamped entry, what was done, verification
  result, tick TASKS.md.
- Clean working tree at the end of every cycle.

Stop (don't schedule another wake-up) when EITHER:
- D1-D5 (pytest-passing) and U1-U6 (semantically reviewed) are all
  ticked, and PROGRESS.md says "Phase 1 complete — Part A tested, Part B
  written and semantically reviewed but NOT compiled or run; needs a
  real Unity compile + Play-mode check before trusting it, then ready
  for Section 7 (VR interaction)"; or
- it's past 05:00 local time — write "Stopping for the night — human
  review needed" and stop.