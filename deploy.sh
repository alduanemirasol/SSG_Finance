#!/usr/bin/env bash
set -euo pipefail

# ============================================================================
# SSG Finance — Ubuntu Deployment Script
# Deploys the ASP.NET Core MVC 10 app + MySQL + Nginx reverse proxy
# Usage: sudo bash deploy.sh
# Prerequisites: a .env file in the same directory (see .env.example)
# ============================================================================

ENV_FILE="$(cd "$(dirname "$0")" && pwd)/.env"
APP_DIR="/opt/ssg-finance"
SRC_DIR="${APP_DIR}/src"
PUBLISH_DIR="${APP_DIR}/publish"
UPLOADS_DIR="${APP_DIR}/uploads"
NGINX_SITE="ssg-finance"
SERVICE_NAME="ssg-finance"
APP_PORT=8085
APP_USER="www-data"
REPO_URL="https://github.com/anomalyco/SSG_Finance.git"
BRANCH="main"

# ---- Colors ----
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${GREEN}[✓]${NC} $1"; }
warn() { echo -e "${YELLOW}[!]${NC} $1"; }
err()  { echo -e "${RED}[✗]${NC} $1"; exit 1; }

# ---- Load .env ----
load_env() {
    if [[ ! -f "$ENV_FILE" ]]; then
        err ".env file not found at $ENV_FILE
Create one from .env.example:
  cp .env.example .env
  nano .env"
    fi
    set -a
    source "$ENV_FILE"
    set +a

    : "${SSG_DB_PASSWORD:?Missing SSG_DB_PASSWORD in .env}"
    : "${SSG_SMTP_PASSWORD:?Missing SSG_SMTP_PASSWORD in .env}"
    : "${SSG_SMTP_USER:=ctuginatilanextensioncampus@gmail.com}"
    : "${SSG_DOMAIN:=localhost}"
    DB_NAME="${SSG_DB_NAME:-ssg_system}"
    DB_USER="${SSG_DB_USER:-ssg_user}"
    DB_HOST="${SSG_DB_HOST:-127.0.0.1}"
    SMTP_HOST="${SSG_SMTP_HOST:-smtp.gmail.com}"
    SMTP_PORT="${SSG_SMTP_PORT:-587}"
    REPO_URL="${SSG_REPO_URL:-$REPO_URL}"
    BRANCH="${SSG_BRANCH:-$BRANCH}"
    log "Loaded .env (DB: ${DB_USER}@${DB_HOST}/${DB_NAME})"
}

# ---- Preflight ----
check_root() {
    if [[ $EUID -ne 0 ]]; then
        err "This script must be run as root (sudo)"
    fi
}

# ---- Phase 1: System Packages ----
install_system_packages() {
    log "Updating system packages..."
    DEBIAN_FRONTEND=noninteractive apt-get update -qq

    log "Installing dependencies..."
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
        curl wget git nginx mysql-server \
        software-properties-common apt-transport-https \
        ca-certificates gnupg ufw
}

# ---- Phase 2: .NET 10 SDK (preview) ----
install_dotnet() {
    if command -v dotnet &>/dev/null && dotnet --list-sdks | grep -q "^10."; then
        log ".NET 10 SDK already installed ($(dotnet --version))"
        local dotnet_path
        dotnet_path=$(command -v dotnet)
        ln -sf "$dotnet_path" /usr/local/bin/dotnet
    else
        log "Installing .NET 10 SDK (preview channel)..."
        wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
        chmod +x /tmp/dotnet-install.sh

        /tmp/dotnet-install.sh \
            --channel 10.0 \
            --install-dir /usr/share/dotnet \
            --no-path

        ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
    fi

    # Verify
    dotnet --version || err ".NET SDK installation failed"
    log ".NET SDK $(dotnet --version) installed"
}

# ---- Phase 3: MySQL ----
configure_mysql() {
    log "Ensuring MySQL is running..."
    if ! systemctl is-active --quiet mysql; then
        systemctl start mysql
    fi

    # Create database if not exists
    mysql -u root -e "CREATE DATABASE IF NOT EXISTS \`${DB_NAME}\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;" 2>/dev/null || true

    # Create user and grant privileges (idempotent, handles password rotation)
    mysql -u root <<SQL
CREATE USER IF NOT EXISTS '${DB_USER}'@'${DB_HOST}' IDENTIFIED BY '${SSG_DB_PASSWORD}';
ALTER USER '${DB_USER}'@'${DB_HOST}' IDENTIFIED BY '${SSG_DB_PASSWORD}';
GRANT ALL PRIVILEGES ON \`${DB_NAME}\`.* TO '${DB_USER}'@'${DB_HOST}';
FLUSH PRIVILEGES;
SQL

    log "Database '${DB_NAME}' and user '${DB_USER}' ready"

    # Bind to localhost only for security
    if grep -q "^bind-address\s*=\s*0\.0\.0\.0" /etc/mysql/mysql.conf.d/mysqld.cnf 2>/dev/null; then
        sed -i 's/^bind-address\s*=\s*0\.0\.0\.0/bind-address = 127.0.0.1/' /etc/mysql/mysql.conf.d/mysqld.cnf
        systemctl restart mysql
        log "MySQL bound to localhost only"
    fi
}

# ---- Phase 4: Application Files ----
clone_or_pull_repo() {
    if [[ -d "${SRC_DIR}/.git" ]]; then
        log "Fetching latest code from ${BRANCH} branch..."
        git -C "$SRC_DIR" fetch origin "$BRANCH"
        git -C "$SRC_DIR" reset --hard "origin/${BRANCH}"
        git -C "$SRC_DIR" clean -fd
    else
        log "Cloning ${REPO_URL} (${BRANCH})..."
        git clone --branch "$BRANCH" --single-branch "$REPO_URL" "$SRC_DIR"
    fi

    log "Source ready at ${SRC_DIR}"
}

setup_app_dirs() {
    mkdir -p "$PUBLISH_DIR"
    mkdir -p "${UPLOADS_DIR}/avatars"
    mkdir -p "${UPLOADS_DIR}/expenses"

    log "Application directories created"
}

write_appsettings_production() {
    local config_file="${SRC_DIR}/appsettings.Production.json"

    log "Writing production configuration..."

    cat > "$config_file" <<JSON
{
  "ConnectionStrings": {
    "DefaultConnection": "server=${DB_HOST};database=${DB_NAME};uid=${DB_USER};pwd=${SSG_DB_PASSWORD};port=3306;"
  },
  "SmtpSettings": {
    "Host": "${SMTP_HOST}",
    "Port": ${SMTP_PORT},
    "UserName": "${SSG_SMTP_USER}",
    "Password": "${SSG_SMTP_PASSWORD}",
    "EnableSsl": true
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:${APP_PORT}"
      }
    }
  }
}
JSON

    chown "${APP_USER}:${APP_USER}" "$config_file"
    chmod 640 "$config_file"
    log "appsettings.Production.json written"
}

# ---- Phase 5: Build & Publish ----
build_and_publish() {
    log "Restoring NuGet packages..."
    dotnet restore "${SRC_DIR}/MyMvcApp.csproj" --verbosity quiet

    log "Building and publishing application..."
    dotnet publish "${SRC_DIR}/MyMvcApp.csproj" \
        -c Release \
        -o "$PUBLISH_DIR" \
        --no-restore \
        --verbosity quiet

    # Ensure uploads symlink exists in publish output
    ln -sfn "${UPLOADS_DIR}" "${PUBLISH_DIR}/uploads"

    # Set permissions
    chown -R "${APP_USER}:${APP_USER}" "$PUBLISH_DIR"
    chown -R "${APP_USER}:${APP_USER}" "$UPLOADS_DIR"
    find "$PUBLISH_DIR" -type d -exec chmod 755 {} \;
    find "$PUBLISH_DIR" -type f -exec chmod 644 {} \;

    log "Application published to ${PUBLISH_DIR}"
}

# ---- Phase 6: systemd Service ----
write_systemd_service() {
    local service_file="/etc/systemd/system/${SERVICE_NAME}.service"

    log "Writing systemd service..."

    cat > "$service_file" <<UNIT
[Unit]
Description=SSG Finance Management System
After=network.target mysql.service
Wants=network-online.target
Requires=mysql.service

[Service]
Type=simple
User=${APP_USER}
Group=${APP_USER}
WorkingDirectory=${PUBLISH_DIR}
ExecStart=/usr/local/bin/dotnet ${PUBLISH_DIR}/MyMvcApp.dll
Restart=always
RestartSec=10
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:${APP_PORT}
Environment=DOTNET_CLI_HOME=/tmp

# Security hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ReadWritePaths=${UPLOADS_DIR}

[Install]
WantedBy=multi-user.target
UNIT

    systemctl daemon-reload
    log "systemd service written"
}

enable_service() {
    systemctl enable "${SERVICE_NAME}" --quiet
    systemctl restart "${SERVICE_NAME}"
    log "${SERVICE_NAME} service started"

    # Wait for app to respond
    local retries=30
    local i=0
    while ! curl -s -o /dev/null "http://127.0.0.1:${APP_PORT}" && [[ $i -lt $retries ]]; do
        sleep 2
        ((i++))
    done

    if [[ $i -lt $retries ]]; then
        log "Application is responding on port ${APP_PORT}"
    else
        warn "Application may still be starting. Check: journalctl -u ${SERVICE_NAME}"
    fi
}

# ---- Phase 6b: Print active endpoints ----
print_active_endpoints() {
    local ip
    ip=$(ip -4 addr show | grep -oP '(?<=inet\s)\d+\.\d+\.\d+\.\d+' | grep -v '127.0.0.1' | head -1)

    echo ""
    echo "=============================================="
    echo "  Active Endpoints"
    echo "=============================================="
    echo ""
    echo "  Kestrel:  http://127.0.0.1:${APP_PORT}"
    echo "  Network:  http://${ip:-<unresolved>}:${APP_PORT}"
    echo "  Nginx:    http://${ip:-<unresolved>}/"
    echo ""
    echo "  Verify locally:"
    echo "    curl -s http://127.0.0.1:${APP_PORT}/"
    echo ""
}

# ---- Phase 7: Nginx ----
write_nginx_config() {
    local nginx_conf="/etc/nginx/sites-available/${NGINX_SITE}"

    log "Writing Nginx configuration..."

    cat > "$nginx_conf" <<NGINX
# SSG Finance — Nginx Reverse Proxy
upstream ssg-finance {
    server 127.0.0.1:${APP_PORT};
    keepalive 64;
}

server {
    listen 80;
    server_name ${SSG_DOMAIN};

    # Security headers (app also sets these, defense-in-depth)
    add_header X-Content-Type-Options nosniff;
    add_header X-Frame-Options DENY;
    add_header X-XSS-Protection "1; mode=block";
    add_header Referrer-Policy strict-origin-when-cross-origin;

    # Gzip
    gzip on;
    gzip_types text/plain text/css application/json application/javascript text/xml image/svg+xml;

    # Uploads — serve directly if file exists, else proxy
    location /uploads/ {
        alias ${UPLOADS_DIR}/;
        expires 7d;
        add_header Cache-Control "public, immutable";
        try_files \$uri @app;
    }

    # Static assets
    location ~ ^/(css|js|lib|images)/ {
        root ${PUBLISH_DIR}/wwwroot;
        expires 30d;
        add_header Cache-Control "public, immutable";
        try_files \$uri @app;
    }

    # Favicon
    location = /favicon.ico {
        root ${PUBLISH_DIR}/wwwroot;
        expires 30d;
        add_header Cache-Control "public, immutable";
    }

    # SSE endpoint — disable buffering
    location /Home/Events {
        proxy_pass http://ssg-finance;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 86400s;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }

    # Main app proxy
    location / {
        proxy_pass http://ssg-finance;
        proxy_http_version 1.1;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "keep-alive";
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 120s;
        proxy_send_timeout 120s;

        # Don't cache HTML
        add_header Cache-Control "no-store, no-cache, must-revalidate";
    }

    # Named fallback for try_files
    location @app {
        proxy_pass http://ssg-finance;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
    }
}
NGINX

    # Enable site
    ln -sf "$nginx_conf" "/etc/nginx/sites-enabled/${NGINX_SITE}"

    # Disable default site if it exists
    rm -f /etc/nginx/sites-enabled/default

    log "Nginx configuration written"
}

reload_nginx() {
    nginx -t || err "Nginx configuration is invalid"
    systemctl reload nginx || systemctl restart nginx
    log "Nginx reloaded"
}

# ---- Phase 8: Firewall ----
configure_firewall() {
    log "Configuring UFW firewall..."
    ufw --force reset

    ufw default deny incoming
    ufw default allow outgoing

    ufw allow 22/tcp comment 'SSH'
    ufw allow 80/tcp comment 'HTTP'
    ufw allow 443/tcp comment 'HTTPS'

    ufw --force enable
    log "Firewall enabled (SSH, HTTP, HTTPS allowed)"
}

# ---- Phase 9: Post-install Summary ----
print_summary() {
    local seed_pass="${SSG_ADMIN_PASSWORD:-admin123}"

    echo ""
    echo "=============================================="
    echo "  SSG Finance — Deployment Complete"
    echo "=============================================="
    echo ""
    echo "  URL:          http://${SSG_DOMAIN}/"
    echo "  App port:     ${APP_PORT} (localhost)"
    echo ""
    echo "  Default Admin Login:"
    echo "    School ID:  ADMIN-001"
    echo "    Password:   ${seed_pass}"
    echo "    Email:      admin@ssg.com"
    echo ""
    echo "  Directories:"
    echo "    Source:     ${SRC_DIR}"
    echo "    Publish:    ${PUBLISH_DIR}"
    echo "    Uploads:    ${UPLOADS_DIR}/"
    echo ""
    echo "  Services:"
    echo "    App:        sudo systemctl status ${SERVICE_NAME}"
    echo "    Logs:       journalctl -u ${SERVICE_NAME} -f"
    echo "    Nginx:      sudo systemctl status nginx"
    echo "    MySQL:      sudo systemctl status mysql"
    echo ""
    echo "  .env file:    ${ENV_FILE}"
    echo "=============================================="
}

# ============================================================================
# Main
# ============================================================================
main() {
    echo ""
    echo "=============================================="
    echo "  SSG Finance — Ubuntu Deployment"
    echo "=============================================="
    echo ""

    check_root
    load_env
    install_system_packages
    install_dotnet
    configure_mysql
    clone_or_pull_repo
    setup_app_dirs
    write_appsettings_production
    build_and_publish
    write_systemd_service
    enable_service
    print_active_endpoints
    write_nginx_config
    reload_nginx
    configure_firewall
    print_summary
}

main "$@"
