#!/usr/bin/env sh
# Copy processed graph JSON into Unity's StreamingAssets folder.
# Run from anywhere; requires data/processed/*.json (python scripts/build_graph.py).
# EUROCONTROL files (build_graph_ectrl.py) are copied too when present.
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/Assets/StreamingAssets"
mkdir -p "$DEST"
cp "$ROOT/data/processed/nodes.json" "$ROOT/data/processed/edges.json" "$DEST/"
echo "synced nodes.json + edges.json -> $DEST"
for f in nodes_ectrl.json edges_ectrl.json meta_ectrl.json; do
  if [ -f "$ROOT/data/processed/$f" ]; then
    cp "$ROOT/data/processed/$f" "$DEST/"
    echo "synced $f -> $DEST"
  fi
done
