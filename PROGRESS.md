# PROGRESS

## Log

### 2026-09-13 21:30 — cycle 0 (bootstrap)
- Found: project dir had no own git repo (home dir `~/.git` with zero commits shadowed it). Ran `git init` inside project, branch `overnight-auto`. `~/.git` untouched.
- Anaconda pytest broken (`attrs() got an unexpected keyword argument 'eq'`). Created `.venv` (gitignored) with pytest 9.1.1; use `.venv/bin/pytest tests/`.
- Wrote TASKS.md (D1-D5, U1-U6), PROGRESS.md, .gitignore.

### 2026-09-13 21:50 — cycle 1 (D1-D5)
- Wrote `scripts/build_graph.py` (download idempotent → parse → filter top 200 by degree → collapse duplicate pairs → equirectangular projection → JSON export → validate).
- Output: 200 nodes, 4516 edges. 1 airport dropped for invalid lat/lon; 7497 dropped by top-N filter. x=lon/18, z=lat/9, y=alt/max_alt*2. `value` = full-network route degree.
- `data/raw/` gitignored (script downloads it); `data/processed/*.json` committed.
- Wrote `tests/test_graph_pipeline.py` (6 tests, fixture reruns pipeline) + `data/README.md` (ODbL, June-2014 caveat).
- VERIFY: `.venv/bin/pytest tests/` → 6 passed, exit 0. D1-D5 ticked.

### 2026-09-13 22:10 — cycle 2 (U1, U2)
- U1: `docs/unity_setup_notes.md` (Newtonsoft add-by-name, StreamingAssets sync, scene wiring) + `scripts/sync_streaming_assets.sh`. No Assets/StreamingAssets created.
- U2: `Assets/Scripts/Data/GraphData.cs` — NodeData{id,label:string; x,y,z:float; value:int}, EdgeData{source,target:string; weight:int}.
  - (a) delimiters: 2 classes, braces balanced, every field `;`-terminated.
  - (b) schema cross-check vs real output: nodes.json keys exactly {id,label,x,y,z,value}, types {str,str,float,float,float,int} on all 200 rows; edges.json keys exactly {source,target,weight}, types {str,str,int} on all 4516 rows. Match field-by-field.
  - Status: semantically reviewed — NOT compiled or run.
- Also gitignored stray `.claude/`.

### 2026-09-13 22:45 — cycle 3 (U3, U4, U5 — GraphLoader.cs)
Wrote `Assets/Scripts/GraphLoader.cs` (one file covers U3-U5). Status: **semantically reviewed — NOT compiled or run.**
- (a) Delimiters: counted `{`=39/`}`=39, `(`=83/`)`=83, `[`=7/`]`=7. Read whole file: every statement `;`-terminated; class, 6 methods, nested class, all closed. No `yield` inside a `try` that has a `catch` (C# forbids it) — the JSON try/catch contains no yield; the `using` block (try/finally) does contain a yield, which C# allows.
- (b) Schema: loader only touches `node.id/label/x/y/z/value` and `edge.source/target/weight` — exactly the fields in GraphData.cs, which were checked against real JSON in cycle 2.
- (c) API usage, line by line:
  - `UnityWebRequest.Get(uri)` → `yield return request.SendWebRequest()` → `if (request.result != UnityWebRequest.Result.Success) { LogError; yield break; }` → only then `request.downloadHandler.text`. ✔ result checked before parse. Same helper used for both files (no platform branch, no File.ReadAllText).
  - `JsonConvert.DeserializeObject<List<NodeData>>` / `<List<EdgeData>>` inside try/catch(JsonException); null result also guarded. ✔
  - Edge endpoints: `nodeInstances.TryGetValue(edge.source, out a)` and `TryGetValue(edge.target, out b)`; miss → LogWarning + `continue`. No indexer anywhere on the dictionary (only `ContainsKey`/`Add` in BuildNodes, `TryGetValue` in BuildEdges). ✔
  - Scale: `localPosition = new Vector3(node.x, node.y, node.z) * positionScale` — multiplies all three components. ✔
  - LineRenderer: `positionCount = 2`, `SetPosition(0/1, a.position/b.position)`, `startWidth/endWidth` = base + weight·perWeight, `startColor/endColor` = Lerp(low, high, normalized weight), material assigned if set. ✔ width and color driven by weight.
  - `SetEdgeWeightThreshold(float t)`: iterates `edgeInstances`, `edge.gameObject.SetActive(edge.weight >= t)` — mutates the edge GameObjects' active state. ✔ Unwired (no callers).
- (d) Execution trace (hand):
  1. Unity calls `Start()` → `StartCoroutine(LoadGraph())`.
  2. `LoadGraph`: `yield return ReadStreamingAsset("nodes.json", cb)`. Inside: `path = Path.Combine(streamingAssetsPath, "nodes.json")`; if path has no `://` prefix `file://`; `UnityWebRequest.Get`; `yield return SendWebRequest()`; result==Success → `cb(text)` sets `nodesJson`. Coroutine returns; `nodesJson != null` so continue.
  3. Same for `edges.json` → `edgesJson`.
  4. Deserialize both lists (200 NodeData, 4516 EdgeData for current output).
  5. `BuildNodes`: create "Nodes" empty child; for each node: skip empty/duplicate id; `Instantiate(nodePrefab, nodesRoot)` or `CreatePrimitive(Sphere)` + SetParent; name=label; localPosition=(x,y,z)*positionScale; localScale=base+value·perValue; `nodeInstances.Add(id, transform)`. Since nodesRoot is at identity under the loader, world position == local position when loader is at origin.
  6. `BuildEdges`: create "Edges" child; first loop finds maxWeight (2 for current data); second loop per edge: TryGetValue both → new GameObject → AddComponent<LineRenderer> → useWorldSpace=true, 2 positions from node `.position` (world), width, color (weight 1 → low, weight 2 → high), material → add to `edgeInstances`. Last edge in list gets its LineRenderer here; loop ends.
  7. `Debug.Log("GraphLoader: loaded 200 nodes, 4516 edges (...)")`.
- Note: 4516 LineRenderers = 4516 GameObjects/draw calls. Acceptable for Phase 1 on desktop; may need batching/mesh lines for Quest later (out of scope).

### 2026-09-13 23:15 — cycle 4 (U6 — full fresh-read review pass)
Re-read `GraphData.cs` and `GraphLoader.cs` top to bottom against checklist a-e.
- **Finding & fix:** `yield break` was inside a `catch` block (LoadGraph). C# disallows yield statements in catch/finally blocks (CS1631-family). Restructured: catch stores `ex.Message` in `parseError`; the `yield break` now sits after the try/catch. Both files re-counted: `{}`=40/40, `()`=84/84, `[]`=7/7; GraphData.cs 4/4 braces.
- (b) schema re-confirmed against committed data/processed/*.json (unchanged since cycle 2).
- (c) re-checked: result check precedes `downloadHandler.text` (l.117-123); only `TryGetValue` on edge lookups (l.183-184); scale applied to full Vector3 (l.157); `SetEdgeWeightThreshold` calls `SetActive` per edge (l.224).
- Definite-assignment note: `!TryGetValue(src, out a) || !TryGetValue(dst, out b)` — when the whole condition is false both calls ran, so `a`,`b` are definitely assigned after the `if` per C# spec rules for `||`; believed to compile.
- Trace from cycle 3 still valid (only change is error-path control flow).
- Status of all .cs: **semantically reviewed — NOT compiled or run.**

**Phase 1 complete — Part A tested, Part B written and semantically reviewed but NOT compiled or run; needs a real Unity compile + Play-mode check before trusting it, then ready for Section 7 (VR interaction).**

## Blocked
(none)

## Needs Real Verification (no Unity available)
- `UnityWebRequest.Result` enum requires Unity 2020.2+; on older versions use `isNetworkError || isHttpError`.
- `file://` + `Application.streamingAssetsPath` URI form: correct on macOS/Linux (path starts with `/` → `file:///...`); on Windows (`file://C:/...`) UnityWebRequest is believed to accept it but not verified here. On Android the path is already `jar:file://...` so no prefix is added; `Path.Combine` on that string should join with `/` — believed correct, unverified.
- `Newtonsoft.Json.JsonException` as catch type — exists in Newtonsoft; assumed the Unity package (`com.unity.nuget.newtonsoft-json`) exposes the same namespace.
- `Instantiate(GameObject, Transform parent)` overload — exists in Unity ≥5.4; assumed.
- `IReadOnlyDictionary<string, Transform>` property — requires .NET 4.x / .NET Standard 2.0 API level (default in modern Unity).
- LineRenderer `startColor/endColor` only show if `edgeMaterial` uses vertex colors (e.g. `Sprites/Default`); with `edgeMaterial` unset lines render magenta (missing material) — behavior not observed here.
- Coroutine-with-callback pattern (`System.Action<string>` lambda assigning an outer local inside an iterator) — legal C#, but untested here.
- Definite assignment of `out b` after short-circuit `||` (GraphLoader.cs l.183-184) — per spec fine; if compiler complains, split into two `if`s.
- LineRenderer uses world space; if the GraphLoader root is moved after load, lines will not follow nodes (Phase 1 acceptable).
- Whole file has never been compiled; typos/overload mismatches possible.
