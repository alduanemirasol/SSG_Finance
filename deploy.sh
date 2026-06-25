#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

if [ ! -f .env ]; then
  echo "ERROR: .env file not found."
  echo "       Copy .env.example to .env and fill in your secrets."
  exit 1
fi

echo "Pulling latest code..."
git pull

echo "Building and starting containers..."
docker compose up -d --build

HOST_IP=$(hostname -I | awk '{print $1}')
echo ""
echo "========================================"
echo "  SSG Finance is running!"
echo "  LAN access: http://${HOST_IP}:5183"
echo "========================================"
