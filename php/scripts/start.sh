#!/usr/bin/env bash
set -e

BASE_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PID_FILE="$BASE_DIR/runtime/ws_server.pid"
LOG_FILE="$BASE_DIR/runtime/ws_server.out.log"

mkdir -p "$BASE_DIR/runtime"

if [ -f "$PID_FILE" ]; then
  OLD_PID="$(cat "$PID_FILE" || true)"
  if [ -n "$OLD_PID" ] && kill -0 "$OLD_PID" 2>/dev/null; then
    echo "ws_server.php already running, pid=$OLD_PID"
    exit 0
  fi
fi

cd "$BASE_DIR"
nohup php ws_server.php >> "$LOG_FILE" 2>&1 &
PID=$!
echo "$PID" > "$PID_FILE"

echo "started, pid=$PID"
echo "log: $LOG_FILE"
