#!/usr/bin/env bash
# Run at login by the nook systemd --user service: start the proxy, then the tunnel
# (retrying while the network comes up), then publish the new URL.
# Publishing can fail (no network/credentials): that is logged, never fatal.
cd "$(dirname "$0")/.."
export PATH="$HOME/.local/bin:/usr/local/bin:/usr/bin:/bin:$PATH"  # cloudflared, ollama-on/off live in ~/.local/bin
mkdir -p data && chmod 700 data
LOG=data/autostart.log
exec >> "$LOG" 2>&1
log() { echo "$(date '+%Y-%m-%d %H:%M:%S') $*"; }
log "autostart begin"

for _ in $(seq 60); do  # up to ~2 minutes for DNS/network
  getent hosts api.trycloudflare.com >/dev/null 2>&1 && break
  sleep 2
done
getent hosts api.trycloudflare.com >/dev/null 2>&1 || log "network still not ready, trying anyway"

scripts/start.sh --local
rc=$?
if [ "$rc" = 3 ]; then log "proxy already running"
elif [ "$rc" != 0 ]; then log "proxy failed to start (exit $rc), giving up"; exit 1
fi

published=0
for attempt in 1 2 3 4 5; do
  scripts/start.sh --tunnel-only
  rc=$?
  if [ "$rc" = 0 ]; then published=1; break; fi
  if [ "$rc" = 3 ]; then log "tunnel already running, nothing to publish"; break; fi
  log "tunnel attempt $attempt failed (exit $rc), retrying in 15s"
  sleep 15
done

if [ "$published" = 1 ]; then
  if scripts/publish-url.sh; then log "published backend URL"
  else log "WARNING: publishing the URL failed (git push). The site keeps the old URL until scripts/publish-url.sh succeeds."
  fi
elif [ "$rc" != 3 ]; then
  log "WARNING: no tunnel after 5 attempts; the proxy is running locally only"
fi
log "autostart done"
exit 0  # never fail the unit (that would stop the proxy)
