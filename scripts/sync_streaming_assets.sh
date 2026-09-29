#!/usr/bin/env sh
# Copy processed graph JSON into a Unity StreamingAssets folder.
# Usage: scripts/sync_streaming_assets.sh [UNITY_PROJECT_DIR]
#   default: this repo (Assets/StreamingAssets). Pass the Unity project root
#   (e.g. ~/Documents/"My project") to sync the project you actually open in Unity.
# Requires data/processed/*.json (python scripts/build_graph.py).
# Optional outputs (build_graph_ectrl.py, layout_3d.py) are copied too when present.
set -eu
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="${1:-$ROOT}"
DEST="$PROJECT/Assets/StreamingAssets"
mkdir -p "$DEST"
cp "$ROOT/data/processed/nodes.json" "$ROOT/data/processed/edges.json" "$DEST/"
echo "synced nodes.json + edges.json -> $DEST"
for f in nodes_ectrl.json edges_ectrl.json meta_ectrl.json nodes_3d.json nodes_3d_ectrl.json; do
  if [ -f "$ROOT/data/processed/$f" ]; then
    cp "$ROOT/data/processed/$f" "$DEST/"
    echo "synced $f -> $DEST"
  fi
done
