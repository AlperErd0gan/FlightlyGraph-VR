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

## Blocked
(none)

## Needs Real Verification (no Unity available)
(filled in during Part B)
