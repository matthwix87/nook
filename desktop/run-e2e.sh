#!/usr/bin/env bash
# End-to-end check of the desktop client against the real gateway: starts server/server.py on a spare
# port with a throwaway data dir and the fake Ollama (never touches data/ or the live gateway), then runs
# E2eGatewayTests. Usage: desktop/run-e2e.sh
set -euo pipefail
cd "$(dirname "$0")"
REPO="$(cd .. && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
PORT="${E2E_PORT:-8823}" FAKE_PORT="${E2E_FAKE_PORT:-11623}"
DATA="$(mktemp -d)"
FAKE="$REPO/tests/fake_ollama.py"
cleanup() { kill "$SRV" 2>/dev/null || true; pkill -f "fake_ollama.py $FAKE_PORT" 2>/dev/null || true; rm -rf "$DATA"; }
trap cleanup EXIT
DATA_DIR="$DATA" PORT="$PORT" OLLAMA_URL="http://127.0.0.1:$FAKE_PORT" AUTO_MANAGE_OLLAMA=1 \
  OLLAMA_ON_CMD="sh -c 'exec python3 $FAKE $FAKE_PORT >/dev/null 2>&1 &'" OLLAMA_OFF_CMD="pkill -f 'fake_ollama.py $FAKE_PORT'" \
  python3 "$REPO/server/server.py" > "$DATA/server.log" 2>&1 < /dev/null &
SRV=$!
for _ in $(seq 40); do curl -s "http://127.0.0.1:$PORT/status" >/dev/null && break; sleep 0.25; done
KEY="$(sed -n 's/^key: //p' "$DATA/owner_key.txt")"
NOOK_E2E_URL="http://127.0.0.1:$PORT" NOOK_E2E_USER=owner NOOK_E2E_KEY="$KEY" \
  dotnet test Nook.Tests --filter "FullyQualifiedName~E2eGatewayTests" "$@"
