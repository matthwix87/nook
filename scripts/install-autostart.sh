#!/usr/bin/env bash
# Install and start the nook systemd --user service (starts the gateway at login).
set -euo pipefail
DIR="$(cd "$(dirname "$0")/.." && pwd)"
UNIT="$HOME/.config/systemd/user/nook.service"
mkdir -p "$(dirname "$UNIT")"
sed "s#@DIR@#$DIR#g" "$DIR/scripts/nook.service.in" > "$UNIT"
systemctl --user daemon-reload
systemctl --user enable --now nook.service
echo "Installed $UNIT and started it. Logs: journalctl --user -u nook, $DIR/data/autostart.log"
echo "Optional, start at boot without logging in: loginctl enable-linger $USER"
