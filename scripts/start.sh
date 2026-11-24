#!/usr/bin/env bash
# Start the gateway proxy and a Cloudflare quick tunnel, then write the tunnel
# URL to docs/backend.json.
# Usage: scripts/start.sh [--local | --tunnel-only]
#   --local        proxy only, no tunnel
#   --tunnel-only  tunnel only (the proxy must already be running)
# Exit codes: 0 ok, 1 error, 2 tunnel failed (proxy left running), 3 already running.
set -euo pipefail
cd "$(dirname "$0")/.."
PORT="${PORT:-$(python3 -c 'import json; print(json.load(open("config.json"))["port"])')}"
MODE="${1:-all}"

# A pid file counts only if that process is still alive AND is the program we started
# (after a crash the pid may be gone or reused by something else).
alive() {  # alive NAME PATTERN
  local f="data/$1.pid" pid
  [ -f "$f" ] || return 1
  pid="$(cat "$f")"
  if [ -n "$pid" ] && tr '\0' ' ' 2>/dev/null < "/proc/$pid/cmdline" | grep -q -- "$2"; then
    return 0
  fi
  rm -f "$f"  # stale
  return 1
}

if [ "$MODE" != "--local" ] && ! command -v cloudflared >/dev/null 2>&1; then
  echo "error: cloudflared is not installed (see README: 'Install cloudflared')." >&2
  echo "       Run 'scripts/start.sh --local' to start only the proxy." >&2
  exit 1
fi
mkdir -p data && chmod 700 data

if [ "$MODE" != "--tunnel-only" ]; then
  if alive server "server/server.py"; then
    echo "already running (pid $(cat data/server.pid)). Use scripts/stop.sh first." >&2
    exit 3
  fi
  # First run: creates the owner account and prints the key here, in your terminal.
  python3 server/server.py --init

  nohup python3 server/server.py >> data/server.log 2>&1 &
  echo $! > data/server.pid
  for _ in $(seq 20); do
    curl -fs "http://127.0.0.1:$PORT/status" >/dev/null 2>&1 && break
    sleep 0.5
  done
  curl -fs "http://127.0.0.1:$PORT/status" >/dev/null || { echo "error: proxy did not start, see data/server.log" >&2; exit 1; }
  echo "Proxy running on http://127.0.0.1:$PORT (log: data/server.log)"
  [ "$MODE" = "--local" ] && exit 0
elif ! alive server "server/server.py"; then
  echo "error: --tunnel-only needs the proxy running" >&2
  exit 1
fi

if alive cloudflared "cloudflared"; then
  echo "tunnel already running (pid $(cat data/cloudflared.pid))." >&2
  exit 3
fi
: > data/cloudflared.log
nohup cloudflared tunnel --no-autoupdate --url "http://127.0.0.1:$PORT" >> data/cloudflared.log 2>&1 &
echo $! > data/cloudflared.pid
URL=""
for _ in $(seq 60); do
  URL="$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' data/cloudflared.log | head -n1 || true)"
  [ -n "$URL" ] && break
  kill -0 "$(cat data/cloudflared.pid)" 2>/dev/null || break  # cloudflared exited (e.g. no network)
  sleep 1
done
if [ -z "$URL" ]; then
  echo "error: no tunnel URL, see data/cloudflared.log" >&2
  kill "$(cat data/cloudflared.pid)" 2>/dev/null || true
  rm -f data/cloudflared.pid
  exit 2
fi
printf '{"url": "%s", "updated": "%s"}\n' "$URL" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > docs/backend.json
echo "Tunnel: $URL  (written to docs/backend.json)"
echo "Run scripts/publish-url.sh to push the new URL to GitHub Pages."
