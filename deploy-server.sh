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

# Pre-flight: ensure MySQL is reachable before we touch the container
echo "Checking MySQL connectivity..."
DB_HOST=$(grep -E '^DB_HOST=' .env | cut -d= -f2)
DB_PORT=$(grep -E '^DB_PORT=' .env | cut -d= -f2)
DB_PORT=${DB_PORT:-3306}
DB_USERNAME=$(grep -E '^DB_USERNAME=' .env | cut -d= -f2)
DB_PASSWORD=$(grep -E '^DB_PASSWORD=' .env | cut -d= -f2)

if ! MYSQL_PWD="${DB_PASSWORD}" mysqladmin ping \
  -h "${DB_HOST:-127.0.0.1}" \
  -P "${DB_PORT}" \
  -u "${DB_USERNAME}" \
  --silent > /dev/null 2>&1; then
  echo "ERROR: Cannot reach MySQL at ${DB_HOST:-127.0.0.1}:${DB_PORT}."
  echo "       Check DB_HOST/DB_PORT/DB_USERNAME/DB_PASSWORD in .env"
  echo "       and ensure MySQL is running."
  exit 1
fi
echo "MySQL is reachable."

# Pre-deploy backup (uses existing backup.sh)
echo "Creating pre-deploy backup..."
bash backup.sh
echo ""

# Pull latest base images for security patches
echo "Pulling latest base images..."
docker compose pull
echo ""

echo "Building Docker image..."
docker compose build --pull

echo "Starting service..."
docker compose up -d

PORT=$(grep -E '^PORT=' .env | cut -d= -f2)
PORT=${PORT:-3000}

echo ""
echo "Waiting for container to become healthy (up to 60s)..."
HEALTHY=false
for i in $(seq 1 60); do
  if curl -sf "http://127.0.0.1:${PORT}/" > /dev/null 2>&1; then
    HEALTHY=true
    echo "Container ready (${i}s)"
    break
  fi
  sleep 1
done

if [ "$HEALTHY" = true ]; then
  # Cleanup: remove dangling images and old backups
  echo ""
  echo "Cleaning up old Docker images..."
  docker image prune -f --filter "until=72h" 2>/dev/null || true

  echo "Cleaning up backups older than 7 days..."
  BACKUP_DIR="${HOME}/backups"
  if [ -d "${BACKUP_DIR}" ]; then
    find "${BACKUP_DIR}" -name "db_*.sql.gz" -mtime +7 -delete 2>/dev/null || true
    find "${BACKUP_DIR}" -name "uploads_*.tgz" -mtime +7 -delete 2>/dev/null || true
  fi

  GIT_SHA=$(git rev-parse --short HEAD 2>/dev/null || echo "N/A")
  IMAGE_TAG=$(docker images --format '{{.Repository}}:{{.Tag}}' | grep ssgfinance | head -1 || echo "ssgfinance-app:latest")

  echo ""
  echo "============================================"
  echo "  SSG Finance is RUNNING"
  echo "  Container: ssgfinance-app (port ${PORT})"
  echo "  Image:     ${IMAGE_TAG}"
  echo "  Commit:    ${GIT_SHA}"
  echo "  Backup:    $(ls -t ${BACKUP_DIR}/db_*.sql.gz 2>/dev/null | head -1 || echo 'N/A')"
  echo ""
  echo "  Visit: http://<your-domain>"
  echo "         (Nginx proxies to port ${PORT})"
  echo ""
  echo "  Logs:  docker compose logs -f"
  echo "  Stop:  docker compose down"
  echo "============================================"
else
  echo "ERROR: Container failed health check within 60s."
  echo ""
  docker compose logs --tail=30
  echo ""
  echo "Rolling back — stopping failed container..."
  docker compose down --timeout 10 2>/dev/null || true
  echo ""
  echo "Rollback complete. The previous container has been stopped."
  echo "To investigate: docker compose logs --tail=50"
  echo "To retry:       bash deploy-server.sh"
  exit 1
fi
