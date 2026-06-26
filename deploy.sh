#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

if [ ! -f .env ]; then
  echo "ERROR: .env file not found."
  echo "       Copy .env.example to .env and fill in your secrets."
  exit 1
fi

if ! docker info > /dev/null 2>&1; then
  echo "ERROR: Cannot connect to Docker daemon."
  echo "       Run:  sudo usermod -aG docker \$USER"
  echo "       Then log out and back in, or run:  newgrp docker"
  exit 1
fi

for network in ssgfinance-network proxy-network; do
  if ! docker network inspect "$network" > /dev/null 2>&1; then
    echo "ERROR: Docker network '$network' does not exist."
    echo "       Create it first: docker network create $network"
    exit 1
  fi
done

echo "Pulling latest code..."
git pull

echo "Building and starting containers..."
docker compose up -d --build

PORT=$(awk -F= '/^[[:space:]]*PORT[[:space:]]*=/{value=$0; sub(/^[^=]*=/, "", value); gsub(/^[[:space:]]+|[[:space:]]+$/, "", value); print value; exit}' .env)
PORT=${PORT:-3000}

echo ""
echo "========================================"
echo "  SSG Finance is running!"
echo "  Container: ssgfinance-app:${PORT}"
echo "  Proxy URL depends on your nginx config."
echo "========================================"
