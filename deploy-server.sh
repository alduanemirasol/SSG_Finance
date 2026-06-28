#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

if [ ! -f .env ]; then
  echo "ERROR: .env file not found."
  echo "       The server admin should have placed one at ~/app/.env"
  echo "       See .env.example for the required variables."
  exit 1
fi

if ! docker info > /dev/null 2>&1; then
  echo "ERROR: Cannot connect to Docker daemon."
  exit 1
fi

echo "Building Docker image..."
docker compose build

echo "Starting service..."
docker compose up -d

echo ""
echo "Waiting for container to start..."
sleep 3

if docker ps --filter "name=ssgfinance-app" --filter "status=running" | grep -q ssgfinance-app; then
  PORT=$(grep -E '^PORT=' .env | cut -d= -f2)
  PORT=${PORT:-3000}
  echo ""
  echo "============================================"
  echo "  SSG Finance is RUNNING"
  echo "  Container: ssgfinance-app (port ${PORT})"
  echo ""
  echo "  Visit: http://<your-domain>"
  echo "         (Nginx proxies to port ${PORT})"
  echo ""
  echo "  Logs:  docker compose logs -f"
  echo "  Stop:  docker compose down"
  echo "============================================"
else
  echo "ERROR: Container failed to start."
  echo ""
  docker compose logs --tail=30
  exit 1
fi
