#!/usr/bin/env bash
# Stop the tunnel and the proxy started by start.sh (uses pid files, no pkill).
cd "$(dirname "$0")/.."
for name in cloudflared server; do
  f="data/$name.pid"
  if [ -f "$f" ]; then
    pid="$(cat "$f")"
    if kill "$pid" 2>/dev/null; then echo "stopped $name (pid $pid)"; fi
    rm -f "$f"
  fi
done
