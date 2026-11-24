#!/usr/bin/env bash
# Commit and push site/backend.json so GitHub Pages serves the current tunnel URL.
# Not run automatically. Assumes this repo has a GitHub remote with Pages set up (see README).
set -euo pipefail
cd "$(dirname "$0")/.."
git add site/backend.json
if git diff --cached --quiet -- site/backend.json; then
  echo "backend.json unchanged, nothing to publish."
  exit 0
fi
git commit -m "Update backend URL" -- site/backend.json
git push
