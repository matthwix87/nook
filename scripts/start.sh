#!/usr/bin/env bash
# Start the gateway proxy and a Cloudflare quick tunnel, then write the tunnel
# URL to docs/backend.json.   Usage: scripts/start.sh [--local]   (--local = no tunnel)
set -euo pipefail
cd "$(dirname "$0")/.."
PORT="${PORT:-$(python3 -c 'import json; print(json.load(open("config.json"))["port"])')}"
LOCAL=0; [ "${1:-}" = "--local" ] && LOCAL=1

if [ "$LOCAL" = 0 ] && ! command -v cloudflared >/dev/null 2>&1; then
  echo "error: cloudflared is not installed (see README: 'Install cloudflared')." >&2
  echo "       Run 'scripts/start.sh --local' to start only the proxy." >&2
  exit 1
fi
mkdir -p data && chmod 700 data
if [ -f data/server.pid ] && kill -0 "$(cat data/server.pid)" 2>/dev/null; then
  echo "error: already running (pid $(cat data/server.pid)). Use scripts/stop.sh first." >&2
  exit 1
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
[ "$LOCAL" = 1 ] && exit 0

: > data/cloudflared.log
nohup cloudflared tunnel --no-autoupdate --url "http://127.0.0.1:$PORT" >> data/cloudflared.log 2>&1 &
echo $! > data/cloudflared.pid
URL=""
for _ in $(seq 60); do
  URL="$(grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' data/cloudflared.log | head -n1 || true)"
  [ -n "$URL" ] && break
  sleep 1
done
if [ -z "$URL" ]; then
  echo "error: no tunnel URL after 60s, see data/cloudflared.log" >&2
  exit 1
fi
printf '{"url": "%s", "updated": "%s"}\n' "$URL" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > docs/backend.json
echo "Tunnel: $URL  (written to docs/backend.json)"
echo "Run scripts/publish-url.sh to push the new URL to GitHub Pages."
