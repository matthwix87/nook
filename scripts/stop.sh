#!/usr/bin/env bash
# Stop the tunnel and the proxy started by start.sh (uses pid files, no pkill).
# A pid is only killed if it still belongs to that program (pids can be reused after a crash).
cd "$(dirname "$0")/.."
# Patterns match the whole command line start.sh uses (not e.g. `tail -f data/cloudflared.log`).
for entry in "cloudflared|^(\S*/)?cloudflared tunnel --no-autoupdate --url " "server|^(\S*/)?python3 server/server\.py \$"; do
  name="${entry%%|*}"; pattern="${entry#*|}"
  f="data/$name.pid"
  [ -f "$f" ] || continue
  pid="$(cat "$f")"
  if [ -n "$pid" ] && tr '\0' ' ' 2>/dev/null < "/proc/$pid/cmdline" | grep -qE -- "$pattern"; then
    kill "$pid" 2>/dev/null && echo "stopped $name (pid $pid)"
  fi
  rm -f "$f"
done
