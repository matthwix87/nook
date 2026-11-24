#!/usr/bin/env bash
# Stop and remove the nook systemd --user service. (Stopping it also stops the gateway.)
set -uo pipefail
UNIT="$HOME/.config/systemd/user/nook.service"
systemctl --user disable --now nook.service 2>/dev/null || true
rm -f "$UNIT"
systemctl --user daemon-reload
echo "Removed nook.service. Start the gateway by hand with scripts/start.sh."
