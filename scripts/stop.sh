#!/usr/bin/env bash
# Stop the tunnel and the proxy started by start.sh (uses pid files, no pkill).
# A pid is only killed if it still belongs to that program (pids can be reused after a crash).
cd "$(dirname "$0")/.."
for entry in "cloudflared:cloudflared" "server:server/server.py"; do
  name="${entry%%:*}"; pattern="${entry#*:}"
  f="data/$name.pid"
  [ -f "$f" ] || continue
  pid="$(cat "$f")"
  if [ -n "$pid" ] && tr '\0' ' ' 2>/dev/null < "/proc/$pid/cmdline" | grep -q -- "$pattern"; then
    kill "$pid" 2>/dev/null && echo "stopped $name (pid $pid)"
  fi
  rm -f "$f"
done
