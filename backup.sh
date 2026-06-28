#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

if [ ! -f .env ]; then
  echo "ERROR: .env not found"
  exit 1
fi

BACKUP_DIR="${HOME}/backups"
mkdir -p "${BACKUP_DIR}"

DATE=$(date +%F_%H%M%S)

DB_HOST=$(grep -E '^DB_HOST=' .env | cut -d= -f2)
DB_PORT=$(grep -E '^DB_PORT=' .env | cut -d= -f2)
DB_PORT=${DB_PORT:-3306}
DB_DATABASE=$(grep -E '^DB_DATABASE=' .env | cut -d= -f2)
DB_USERNAME=$(grep -E '^DB_USERNAME=' .env | cut -d= -f2)
line=$(grep -E '^DB_PASSWORD=' .env)
DB_PASSWORD="${line#*=}"

if [ -z "${DB_DATABASE}" ] || [ -z "${DB_USERNAME}" ]; then
  echo "ERROR: DB_DATABASE or DB_USERNAME not found in .env"
  exit 1
fi

echo "Backing up database..."
MYSQL_PWD="${DB_PASSWORD}" mysqldump \
  -h 127.0.0.1 \
  -P "${DB_PORT}" \
  -u "${DB_USERNAME}" \
  "${DB_DATABASE}" \
  | gzip > "${BACKUP_DIR}/db_${DATE}.sql.gz"

echo "Backing up uploads..."
UPLOADS_DIR="${HOME}/uploads"
if [ -d "${UPLOADS_DIR}" ]; then
  tar czf "${BACKUP_DIR}/uploads_${DATE}.tgz" -C "${UPLOADS_DIR}" .
else
  echo "WARNING: Uploads directory not found at ${UPLOADS_DIR}, skipping."
fi

echo ""
echo "============================================"
echo "  Backup complete"
echo "  Database: ${BACKUP_DIR}/db_${DATE}.sql.gz"
echo "  Uploads:  ${BACKUP_DIR}/uploads_${DATE}.tgz"
echo "============================================"
