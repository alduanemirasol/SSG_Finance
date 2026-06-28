#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

echo "Pulling latest code..."
git pull

echo "Rebuilding image..."
docker compose build

echo "Restarting service..."
docker compose up -d

echo ""
echo "Update complete."
