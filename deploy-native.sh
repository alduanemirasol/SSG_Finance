#!/usr/bin/env bash
#
# SSG Finance — native (no Docker) deployment for Ubuntu Server.
#
# Installs the .NET 10 SDK + MySQL, provisions the database, publishes the app
# to $APP_DIR, installs a systemd service, opens the LAN port, and starts it.
# Re-running performs an in-place update (build -> restart). Idempotent.
#
# Usage (run from the repo root on the target Ubuntu server):
#     cp .env.native.example .env
#     nano .env
#     sudo bash deploy-native.sh
#
set -euo pipefail

# --- Resolve repo dir even when invoked via sudo from elsewhere ---
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

log()  { echo -e "\n\033[1;32m==>\033[0m $*"; }
warn() { echo -e "\033[1;33mWARN:\033[0m $*"; }
die()  { echo -e "\033[1;31mERROR:\033[0m $*" >&2; exit 1; }

# --- Must be root (apt, systemd, /opt, ufw) ---
[ "$(id -u)" -eq 0 ] || die "Run with sudo:  sudo bash deploy-native.sh"

# --- Load configuration ---
[ -f .env ] || die ".env not found. Run:  cp .env.native.example .env  then edit it."
set -a; . ./.env; set +a

: "${DB_NAME:?DB_NAME missing in .env}"
: "${DB_USER:?DB_USER missing in .env}"
: "${DB_PASSWORD:?DB_PASSWORD missing in .env}"
APP_PORT="${APP_PORT:-8085}"
APP_DIR="${APP_DIR:-/opt/ssg}"
SERVICE_USER="${SERVICE_USER:-www-data}"
SMTP_HOST="${SMTP_HOST:-}"
SMTP_PORT="${SMTP_PORT:-587}"
SMTP_USERNAME="${SMTP_USERNAME:-}"
SMTP_PASSWORD="${SMTP_PASSWORD:-}"
SERVICE_NAME="ssg"

[ "$DB_PASSWORD" = "change_me_to_a_strong_password" ] && \
  die "Set a real DB_PASSWORD in .env before deploying."

# --- Ensure the .NET 10 SDK is present (needed to publish) ---
ensure_dotnet() {
  if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    log ".NET 10 SDK already installed."
    return
  fi
  log "Installing .NET 10 SDK..."
  apt-get update -y
  if apt-get install -y dotnet-sdk-10.0; then return; fi

  warn "dotnet-sdk-10.0 not in the default feed; adding the Microsoft package repo."
  . /etc/os-release
  local deb="/tmp/packages-microsoft-prod.deb"
  wget -q "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -O "$deb" \
    || die "Could not download the Microsoft package repo for Ubuntu ${VERSION_ID}."
  dpkg -i "$deb"
  apt-get update -y
  apt-get install -y dotnet-sdk-10.0 \
    || die "Failed to install .NET 10 SDK. Install it manually, then re-run."
}

# --- Ensure MySQL is installed and running ---
ensure_mysql() {
  if dpkg -l 2>/dev/null | grep -q '^ii  mysql-server'; then
    log "MySQL already installed."
  else
    log "Installing MySQL server..."
    DEBIAN_FRONTEND=noninteractive apt-get install -y mysql-server
  fi
  systemctl enable --now mysql
}

# --- Create database + app user (idempotent) ---
setup_database() {
  log "Provisioning database '${DB_NAME}' and user '${DB_USER}'..."
  # Root connects over the local socket (default auth_socket) since we are root.
  mysql --protocol=socket -uroot <<SQL
CREATE DATABASE IF NOT EXISTS \`${DB_NAME}\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER IF NOT EXISTS '${DB_USER}'@'localhost' IDENTIFIED BY '${DB_PASSWORD}';
ALTER USER '${DB_USER}'@'localhost' IDENTIFIED BY '${DB_PASSWORD}';
GRANT ALL PRIVILEGES ON \`${DB_NAME}\`.* TO '${DB_USER}'@'localhost';
FLUSH PRIVILEGES;
SQL
}

# --- Build & publish into APP_DIR ---
publish_app() {
  log "Publishing application to ${APP_DIR}..."
  # Stop the service first so we don't overwrite a running binary.
  systemctl stop "${SERVICE_NAME}" 2>/dev/null || true
  mkdir -p "${APP_DIR}"
  dotnet publish -c Release -o "${APP_DIR}"

  # Upload targets used at runtime (receipts, avatars) must exist and be writable.
  mkdir -p "${APP_DIR}/wwwroot/uploads/expenses" "${APP_DIR}/wwwroot/uploads/avatars"

  id "${SERVICE_USER}" >/dev/null 2>&1 || die "Service user '${SERVICE_USER}' does not exist."
  chown -R "${SERVICE_USER}:${SERVICE_USER}" "${APP_DIR}"
}

# --- Write the systemd unit ---
install_service() {
  log "Installing systemd service '${SERVICE_NAME}'..."
  local unit="/etc/systemd/system/${SERVICE_NAME}.service"
  local conn="server=localhost;port=3306;database=${DB_NAME};uid=${DB_USER};pwd=${DB_PASSWORD};"

  local smtp_block=""
  if [ -n "${SMTP_HOST}" ]; then
    smtp_block=$(cat <<SMTP
Environment=SmtpSettings__Host=${SMTP_HOST}
Environment=SmtpSettings__Port=${SMTP_PORT}
Environment=SmtpSettings__UserName=${SMTP_USERNAME}
Environment=SmtpSettings__Password=${SMTP_PASSWORD}
Environment=SmtpSettings__EnableSsl=true
SMTP
)
  else
    warn "SMTP_HOST is blank — password-reset email will be disabled."
  fi

  cat > "${unit}" <<UNIT
[Unit]
Description=SSG Finance (ASP.NET Core, native)
After=network.target mysql.service
Requires=mysql.service

[Service]
WorkingDirectory=${APP_DIR}
ExecStart=/usr/bin/dotnet ${APP_DIR}/MyMvcApp.dll
Restart=always
RestartSec=10
User=${SERVICE_USER}
KillSignal=SIGINT
SyslogIdentifier=${SERVICE_NAME}
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:${APP_PORT}
Environment=DOTNET_NOLOGO=true
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
Environment="ConnectionStrings__DefaultConnection=${conn}"
${smtp_block}

[Install]
WantedBy=multi-user.target
UNIT

  chmod 600 "${unit}"   # contains DB/SMTP secrets
  systemctl daemon-reload
  systemctl enable "${SERVICE_NAME}"
  systemctl restart "${SERVICE_NAME}"
}

# --- Open the LAN port ---
open_firewall() {
  if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    log "Opening port ${APP_PORT}/tcp in ufw..."
    ufw allow "${APP_PORT}/tcp" || true
  fi
}

# --- Run ---
ensure_dotnet
ensure_mysql
setup_database
publish_app
install_service
open_firewall

# --- Health check + summary ---
sleep 3
HOST_IP="$(hostname -I | awk '{print $1}')"
echo
echo "========================================================"
if systemctl is-active --quiet "${SERVICE_NAME}"; then
  echo "  SSG Finance is RUNNING (native, no Docker)."
else
  echo "  Service failed to start. Check:  journalctl -u ${SERVICE_NAME} -e"
fi
echo "  LAN access:   http://${HOST_IP}:${APP_PORT}"
echo "  Default login: admin@ssg.com / admin123  (CHANGE IT NOW)"
echo
echo "  Status:  systemctl status ${SERVICE_NAME}"
echo "  Logs:    journalctl -u ${SERVICE_NAME} -f"
echo "========================================================"
