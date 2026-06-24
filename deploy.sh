#!/usr/bin/env bash
# ============================================================================
# SSG Finance — Direct-to-Kestrel Deployment Script
# Target:  Ubuntu 22.04 / 24.04 LTS  (x86_64)
# Stack:   ASP.NET Core MVC (.NET 10) + MySQL 8 + systemd (no Nginx)
# Deploy:  ~/ssg-finance/  from GitHub private repo
# Visit:   http://<server-ip>:8085
# Usage:
#   1. cp .env.example .env
#   2. nano .env           # fill in GitHub token, repo URL, DB password
#   3. bash deploy.sh      [--force] [--skip-build] [--dry-run]
# ============================================================================
set -euo pipefail

# Auto-load .env if it exists next to this script
ENV_FILE="$(cd "$(dirname "$0")" && pwd)/.env"
if [ -f "$ENV_FILE" ]; then
  set -a
  source "$ENV_FILE"
  set +a
fi

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
LOG_FILE="$HOME/ssg-finance-deploy-$(date +%Y%m%d-%H%M%S).log"
exec > >(tee -a "$LOG_FILE") 2>&1

info()  { echo -e "${GREEN}\xE2\x9C\x93${NC} $1"; }
warn()  { echo -e "${YELLOW}\xE2\x9A\xA0${NC} $1"; }
error() { echo -e "${RED}\xE2\x9C\x97${NC} $1"; }
phase() { echo -e "\n${CYAN}══════════════════════════════════════════════════════════════${NC}"
          echo -e "${CYAN}  Phase $1: $2${NC}"
          echo -e "${CYAN}══════════════════════════════════════════════════════════════${NC}"; }

ROLLBACK_NEEDED=false
trap 'rollback' ERR
trap 'echo -e "\n${RED}Script interrupted.${NC}"; rollback' INT TERM

DEPLOY_DIR="${DEPLOY_DIR:-$HOME/ssg-finance}"
BRANCH="${BRANCH:-main}"
DB_NAME="${DB_NAME:-ssg_system}"
DB_USER="${DB_USER:-ssg_finance}"
DB_HOST="${DB_HOST:-localhost}"
DB_PORT="${DB_PORT:-3306}"
APP_PORT=8085
REPO_URL="${REPO_URL:-}"
DB_PASSWORD="${DB_PASSWORD:-}"
GITHUB_TOKEN="${GITHUB_TOKEN:-}"
FORCE=false; SKIP_BUILD=false; DRY_RUN=false
COMMIT_HASH=""; BACKUP_DIR=""

for arg in "$@"; do
  case "$arg" in
    --force)  FORCE=true ;;
    --skip-build) SKIP_BUILD=true ;;
    --dry-run) DRY_RUN=true ;;
  esac
done

# ======================================================================
# ROLLBACK
# ======================================================================
rollback() {
  echo -e "\n${RED}═══ ROLLBACK ═══${NC}"
  if [ "$ROLLBACK_NEEDED" = true ] && [ -n "$BACKUP_DIR" ]; then
    warn "Restoring from backup: $BACKUP_DIR"
    [ -d "$DEPLOY_DIR/app" ] && rm -rf "$DEPLOY_DIR/app" && cp -a "$BACKUP_DIR/app" "$DEPLOY_DIR/app" 2>/dev/null || true
    [ -f "$BACKUP_DIR/appsettings.json" ] && cp "$BACKUP_DIR/appsettings.json" "$DEPLOY_DIR/appsettings.json"
    info "Backup restored."
  fi
  echo -e "\n${YELLOW}Recovery:${NC}  Log: $LOG_FILE  |  Backup: $BACKUP_DIR  |  Re-run: bash $0"
  exit 1
}

# ======================================================================
# PHASE 1 — Environment Validation
# ======================================================================
phase_1_validate_env() {
  phase 1 "Environment Validation"
  [ ! -f /etc/os-release ] && error "Cannot detect OS." && exit 1
  source /etc/os-release
  [ "$ID" != "ubuntu" ] || { [ "$VERSION_ID" != "22.04" ] && [ "$VERSION_ID" != "24.04" ]; } && \
    error "Unsupported: $ID $VERSION_ID. Requires Ubuntu 22.04/24.04." && exit 1
  info "OS: $NAME $VERSION_ID"
  ARCH=$(uname -m)
  [ "$ARCH" != "x86_64" ] && [ "$ARCH" != "aarch64" ] && \
    error "Arch: $ARCH not supported." && exit 1
  info "Architecture: $ARCH"
  [ -z "${BASH_VERSION:-}" ] && error "Bash required." && exit 1
  info "Bash: $BASH_VERSION"
  sudo -n true 2>/dev/null && info "Sudo: accessible" || warn "Some steps need sudo."
  curl -s --max-time 5 https://archive.ubuntu.com >/dev/null 2>&1 && info "Internet: OK" || warn "No internet."
}

# ======================================================================
# PHASE 2 — Dependency Management
# ======================================================================
phase_2_dependencies() {
  phase 2 "Dependency Management"
  local pkgs="curl git ufw wget"
  local install=""
  for p in $pkgs; do dpkg -s "$p" >/dev/null 2>&1 || install="$install $p"; done
  if [ -n "$install" ]; then
    info "Installing:$install"
    [ "$DRY_RUN" = false ] && sudo apt-get update -qq && sudo apt-get install -y -qq $install
  fi
  info "System packages: OK"

  if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    info "Installing .NET SDK 10.0..."
    if [ "$DRY_RUN" = false ]; then
      wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
      chmod +x /tmp/dotnet-install.sh && /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
      rm -f /tmp/dotnet-install.sh; export PATH="$HOME/.dotnet:$PATH"
    fi
  fi
  echo "$PATH" | grep -q "$HOME/.dotnet" || export PATH="$HOME/.dotnet:$PATH"
  dotnet --list-sdks 2>/dev/null | grep -q '^10\.' || { error ".NET SDK 10 not found."; exit 1; }
  info ".NET SDK: $(dotnet --version)"

  command -v mysql &>/dev/null || { info "Installing mysql-client..."; [ "$DRY_RUN" = false ] && sudo apt-get install -y -qq mysql-client; }
  info "MySQL client: OK"
}

# ======================================================================
# PHASE 3 — Configuration Validation
# ======================================================================
phase_3_configuration() {
  phase 3 "Configuration Validation"
  local MISSING=""
  [ -z "$GITHUB_TOKEN" ] && MISSING="$MISSING GITHUB_TOKEN"
  [ -z "$REPO_URL" ] && MISSING="$MISSING REPO_URL"
  [ -z "$DB_PASSWORD" ] && MISSING="$MISSING DB_PASSWORD"

  if [ -n "$MISSING" ]; then
    error "Missing required variables:$MISSING"
    echo ""
    echo -e "${YELLOW}Setup instructions:${NC}"
    echo "  1. Install MySQL manually (if not installed):"
    echo "       sudo apt install mysql-server -y"
    echo ""
    echo "  2. Create database and user:"
    echo "       sudo mysql"
    echo "       CREATE USER 'ssg_finance'@'localhost' IDENTIFIED BY 'YourPassword!';"
    echo "       CREATE DATABASE ssg_system;"
    echo "       GRANT ALL PRIVILEGES ON ssg_system.* TO 'ssg_finance'@'localhost';"
    echo "       FLUSH PRIVILEGES;"
    echo "       EXIT;"
    echo ""
    echo "  3. cp .env.example .env"
    echo "  4. nano .env         # fill in the missing values"
    echo "  5. bash $0           # re-run"
    exit 1
  fi
  info "Deploy: $DEPLOY_DIR | Branch: $BRANCH | Port: $APP_PORT | DB: $DB_HOST:$DB_PORT/$DB_NAME"
}

# ======================================================================
# PHASE 4 — Source Control
# ======================================================================
phase_4_source_control() {
  phase 4 "Source Control"
  local auth_repo; auth_repo=$(echo "$REPO_URL" | sed "s|https://|https://$GITHUB_TOKEN@|")
  if [ ! -d "$DEPLOY_DIR/.git" ]; then
    info "Cloning repository..."
    [ "$DRY_RUN" = false ] && git clone --branch "$BRANCH" "$auth_repo" "$DEPLOY_DIR"
  else
    info "Repository exists. Checking for updates..."
    if [ "$DRY_RUN" = false ]; then
      cd "$DEPLOY_DIR"
      git diff --quiet || { warn "Stashing changes..."; git stash push -m "deploy-stash $(date +%Y%m%d-%H%M%S)"; }
      git fetch origin
      local LOCAL=$(git rev-parse HEAD)
      local REMOTE=$(git rev-parse "@{upstream}" 2>/dev/null || echo "")
      [ "$LOCAL" = "$REMOTE" ] && [ "$FORCE" = false ] && { info "Already at latest."; COMMIT_HASH="$LOCAL"; return 0; }
      git checkout "$BRANCH" && git pull origin "$BRANCH"
    fi
  fi
  [ "$DRY_RUN" = false ] && cd "$DEPLOY_DIR" && COMMIT_HASH=$(git rev-parse HEAD 2>/dev/null || echo "unknown")
  info "Commit: $COMMIT_HASH"
}

# ======================================================================
# PHASE 5 — Permissions
# ======================================================================
phase_5_permissions() {
  phase 5 "User & Permission Management"
  [ "$DRY_RUN" = true ] && { warn "[DRY-RUN] Would set up directories"; return 0; }
  mkdir -p "$DEPLOY_DIR/app" "$DEPLOY_DIR/wwwroot/uploads/avatars" "$DEPLOY_DIR/wwwroot/uploads/expenses"
  export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
  grep -q '\.dotnet' "$HOME/.bashrc" 2>/dev/null || echo 'export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"' >> "$HOME/.bashrc"
  info "Directories + PATH configured"
}

# ======================================================================
# PHASE 6 — Database
# ======================================================================
phase_6_database() {
  phase 6 "Database & External Service Validation"
  [ "$DRY_RUN" = true ] && { warn "[DRY-RUN] Would validate DB"; return 0; }
  local MYSQL_CMD="mysql -h $DB_HOST -P $DB_PORT -u $DB_USER -p$DB_PASSWORD"
  $MYSQL_CMD -e "SELECT 1" >/dev/null 2>&1 || { error "Cannot connect to MySQL."; exit 1; }
  info "MySQL: OK"
  $MYSQL_CMD -e "CREATE DATABASE IF NOT EXISTS \`$DB_NAME\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"
  info "Database '$DB_NAME': ensured"
  BACKUP_DIR="$HOME/backups/ssg-finance-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$BACKUP_DIR"
  $MYSQL_CMD "$DB_NAME" -e "SELECT COUNT(*) FROM accounts" >/dev/null 2>&1 && \
    mysqldump -h "$DB_HOST" -P "$DB_PORT" -u "$DB_USER" -p"$DB_PASSWORD" "$DB_NAME" > "$BACKUP_DIR/database.sql" && \
    info "Backup: $BACKUP_DIR/database.sql"
  [ -f "$DEPLOY_DIR/Database_mysql" ] && $MYSQL_CMD "$DB_NAME" < "$DEPLOY_DIR/Database_mysql" && info "Schema applied."
  local TC; TC=$($MYSQL_CMD -N "$DB_NAME" -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='$DB_NAME';" 2>/dev/null || echo "0")
  info "Tables: $TC"
}

# ======================================================================
# PHASE 7 — Backup
# ======================================================================
phase_7_backup() {
  phase 7 "Backup Preparation"
  [ "$DRY_RUN" = true ] && { warn "[DRY-RUN] Would backup"; return 0; }
  [ ! -d "$DEPLOY_DIR/app" ] || [ ! -f "$DEPLOY_DIR/app/MyMvcApp.dll" ] && { info "No previous deployment to backup."; return 0; }
  ROLLBACK_NEEDED=true
  cp -a "$DEPLOY_DIR/app" "$BACKUP_DIR/app"
  [ -f "$DEPLOY_DIR/appsettings.json" ] && cp "$DEPLOY_DIR/appsettings.json" "$BACKUP_DIR/appsettings.json"
  [ -d "$DEPLOY_DIR/wwwroot/uploads" ] && [ "$(ls -A "$DEPLOY_DIR/wwwroot/uploads" 2>/dev/null)" ] && \
    tar -czf "$BACKUP_DIR/uploads.tar.gz" -C "$DEPLOY_DIR/wwwroot" uploads/
  info "Backup: $BACKUP_DIR"
}

# ======================================================================
# PHASE 8 — Build
# ======================================================================
phase_8_build() {
  phase 8 "Build & Packaging"
  [ "$SKIP_BUILD" = true ] && { info "Build skipped."; return 0; }
  [ "$DRY_RUN" = true ] && { warn "[DRY-RUN] Would build"; return 0; }
  cd "$DEPLOY_DIR"
  if [ -f "$DEPLOY_DIR/app/MyMvcApp.dll" ] && [ "$FORCE" = false ]; then
    local src=$(find . -name '*.cs' -newer "$DEPLOY_DIR/app/MyMvcApp.dll" -type f 2>/dev/null | head -1)
    [ -z "$src" ] && [ "$COMMIT_HASH" != "unknown" ] && { info "No changes since last build."; return 0; }
  fi
  info "Restoring packages..."
  dotnet restore && info "Restore OK."
  info "Publishing..."
  dotnet publish -c Release --self-contained false -o "$DEPLOY_DIR/app" -p:EnvironmentName=Production
  [ -f "$DEPLOY_DIR/app/MyMvcApp.dll" ] || { error "Build failed."; exit 1; }
  info "Publish: OK"
}

# ======================================================================
# PHASE 9 — Deploy
# ======================================================================
phase_9_deploy() {
  phase 9 "Deployment Execution"
  [ "$DRY_RUN" = true ] && { warn "[DRY-RUN] Would deploy"; return 0; }
  mkdir -p "$DEPLOY_DIR/app/wwwroot/uploads/avatars" "$DEPLOY_DIR/app/wwwroot/uploads/expenses"
  [ ! -L "$DEPLOY_DIR/app/wwwroot/uploads" ] && [ -d "$DEPLOY_DIR/wwwroot/uploads" ] && \
    rm -rf "$DEPLOY_DIR/app/wwwroot/uploads" && ln -sf "$DEPLOY_DIR/wwwroot/uploads" "$DEPLOY_DIR/app/wwwroot/uploads" && info "Linked uploads"
  if [ -f "$DEPLOY_DIR/appsettings.json" ]; then
    cp "$DEPLOY_DIR/appsettings.json" "$DEPLOY_DIR/app/appsettings.json" && chmod 600 "$DEPLOY_DIR/app/appsettings.json" && info "Config copied"
  else
    warn "No appsettings.json. Generating template..."
    cat > "$DEPLOY_DIR/app/appsettings.json" <<- 'EOF'
{
  "ConnectionStrings": { "DefaultConnection": "" },
  "SmtpSettings": { "Host": "", "Port": 587, "UserName": "", "Password": "", "EnableSsl": true },
  "Logging": { "LogLevel": { "Default": "Warning", "Microsoft.AspNetCore": "Warning", "Microsoft.EntityFrameworkCore": "Warning" } },
  "AllowedHosts": "*"
}
EOF
    chmod 600 "$DEPLOY_DIR/app/appsettings.json"
    warn "!!! EDIT appsettings.json with DB and SMTP credentials !!!"
  fi
  info "Artifacts: $DEPLOY_DIR/app"
}

# ======================================================================
# PHASE 10 — Service (systemd user service)
# ======================================================================
phase_10_service() {
  phase 10 "Service Management (systemd)"
  [ "$DRY_RUN" = true ] && { warn "[DRY-RUN] Would create service"; return 0; }
  local UNIT_DIR="$HOME/.config/systemd/user"; mkdir -p "$UNIT_DIR"
  cat > "$UNIT_DIR/ssg-finance.service" << UNITEOF
[Unit]
Description=SSG Finance ASP.NET Core Web Application
After=network.target mysql.service

[Service]
Type=simple
WorkingDirectory=$DEPLOY_DIR/app
ExecStart=$(which dotnet) MyMvcApp.dll
Restart=always
RestartSec=10
Environment=ASPNETCORE_URLS=http://0.0.0.0:$APP_PORT
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
Environment=HOME=$HOME
Environment=PATH=$HOME/.dotnet:/usr/bin:/bin

[Install]
WantedBy=default.target
UNITEOF
  info "systemd unit: $UNIT_DIR/ssg-finance.service"
  systemctl --user daemon-reload && systemctl --user enable ssg-finance.service && systemctl --user restart ssg-finance.service
  sudo loginctl enable-linger "$(whoami)" 2>/dev/null || true
  sleep 5
  if systemctl --user is-active --quiet ssg-finance.service; then
    info "Service: active"
  else
    warn "Service failed. Logs:" && journalctl --user -u ssg-finance.service --no-pager -n 30 || true
    error "Service not running." && exit 1
  fi
}

# ======================================================================
# PHASE 11 — REMOVED (No Nginx)
# ======================================================================
phase_11_direct() {
  phase 11 "Web Server — Direct Kestrel (no reverse proxy)"
  info "No Nginx. App binds to 0.0.0.0:$APP_PORT — accessible from LAN."
}

# ======================================================================
# PHASE 12 — Firewall (port 8085 only)
# ======================================================================
phase_12_firewall() {
  phase 12 "Firewall & Network Configuration"
  [ "$DRY_RUN" = true ] && { warn "[DRY-RUN] Would configure firewall"; return 0; }
  command -v ufw &>/dev/null || { info "UFW not installed. Skipping."; return 0; }
  sudo ufw allow 22/tcp comment 'SSH access' 2>/dev/null || true
  sudo ufw allow $APP_PORT/tcp comment 'SSG Finance direct' 2>/dev/null || true
  sudo ufw status | grep -q "Status: inactive" && sudo ufw --force enable
  info "Firewall:" && sudo ufw status verbose
}

# ======================================================================
# PHASE 13 — Verification (Kestrel-only)
# ======================================================================
phase_13_verification() {
  phase 13 "Post-Deployment Verification"
  [ "$DRY_RUN" = true ] && { warn "[DRY-RUN] Would verify"; return 0; }
  local FAILURES=0

  info "Verifying app on port $APP_PORT..."
  local CODE=$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 http://localhost:$APP_PORT/Home/Index || echo "000")
  if [ "$CODE" = "200" ] || [ "$CODE" = "302" ]; then
    info "  App: HTTP $CODE \xE2\x9C\x93"
  else
    warn "  App: HTTP $CODE (expected 200/302)"; FAILURES=$((FAILURES+1))
  fi

  local HTML=$(curl -s --max-time 10 http://localhost:$APP_PORT/Home/Index 2>/dev/null || echo "")
  echo "$HTML" | grep -qiE '(ssg|login|finance|dashboard)' && info "  Content: SSG app identified \xE2\x9C\x93" || \
    { warn "  Content: unexpected"; FAILURES=$((FAILURES+1)); }

  local MYSQL_CMD="mysql -h $DB_HOST -P $DB_PORT -u $DB_USER -p$DB_PASSWORD"
  if $MYSQL_CMD "$DB_NAME" -e "SELECT COUNT(*) FROM accounts" >/dev/null 2>&1; then
    local AC=$($MYSQL_CMD -N "$DB_NAME" -e "SELECT COUNT(*) FROM accounts" 2>/dev/null || echo "0")
    info "  Database: $AC accounts \xE2\x9C\x93"
  else
    warn "  Database: query failed"; FAILURES=$((FAILURES+1))
  fi

  systemctl --user is-active --quiet ssg-finance.service && info "  Service: running \xE2\x9C\x93" || \
    { warn "  Service: NOT running"; FAILURES=$((FAILURES+1)); }

  info "Failures: $FAILURES"
  return $FAILURES
}

# ======================================================================
# PHASE 14 — Summary
# ======================================================================
phase_14_summary() {
  phase 14 "Deployment Summary"
  local IP=$(ip -4 addr show | grep -oP '(?<=inet\s)\d+(\.\d+){3}' | grep -v '127.0.0.1' | head -1 || echo "unknown")
  echo ""
  echo -e "${CYAN}╔══════════════════════════════════════════════════════════════════╗${NC}"
  echo -e "${CYAN}║          SSG Finance — Deployment Complete (Direct)             ║${NC}"
  echo -e "${CYAN}╠══════════════════════════════════════════════════════════════════╣${NC}"
  echo -e "${CYAN}║${NC}  1  Environment Validation .............. ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  2  Dependency Management .............. ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  3  Configuration ..................... ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  4  Source Control .................... ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  5  Permissions ....................... ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  6  Database .......................... ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  7  Backup ............................ ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  8  Build ............................. ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  9  Deploy ............................ ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  10 Service ........................... ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  11 Web Server (direct Kestrel) ....... ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  12 Firewall .......................... ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}║${NC}  13 Verification ...................... ${GREEN}PASS${NC}  ║"
  echo -e "${CYAN}╠══════════════════════════════════════════════════════════════════╣${NC}"
  echo -e "${CYAN}║${NC}  Commit:  ${COMMIT_HASH:0:12}                                   ║"
  echo -e "${CYAN}║${NC}  Time:    $(date '+%Y-%m-%d %H:%M:%S') UTC                    ║"
  echo -e "${CYAN}║${NC}  Backup:  $BACKUP_DIR  ║"
  echo -e "${CYAN}║${NC}  Deploy:  $DEPLOY_DIR                              ║"
  echo -e "${CYAN}║${NC}  Visit:   http://$IP:$APP_PORT                               ║"
  echo -e "${CYAN}║${NC}  Service: ssg-finance.service                                ║"
  echo -e "${CYAN}║${NC}  Ports:   22/tcp (SSH), $APP_PORT/tcp (App)                  ║"
  echo -e "${CYAN}╚══════════════════════════════════════════════════════════════════╝${NC}"
  echo ""
  info "Log: $LOG_FILE"
  echo ""
  echo -e "${YELLOW}Troubleshooting:${NC}"
  echo "  Logs:      journalctl --user -u ssg-finance.service -f"
  echo "  Status:    systemctl --user status ssg-finance.service"
  echo "  Config:    cat $DEPLOY_DIR/app/appsettings.json"
  echo "  Re-deploy: bash $0 --force"
}

# ======================================================================
# MAIN
# ======================================================================
main() {
  echo -e "${CYAN}╔══════════════════════════════════════════════════════════════════╗${NC}"
  echo -e "${CYAN}║   SSG Finance — Direct Kestrel Deployment                       ║${NC}"
  echo -e "${CYAN}║   Ubuntu  \xC2\xB7  .NET 10  \xC2\xB7  MySQL 8  \xC2\xB7  Port $APP_PORT                   ║${NC}"
  echo -e "${CYAN}╚══════════════════════════════════════════════════════════════════╝${NC}"
  phase_1_validate_env
  phase_2_dependencies
  phase_3_configuration
  phase_4_source_control
  phase_5_permissions
  phase_6_database
  phase_7_backup
  phase_8_build
  phase_9_deploy
  phase_10_service
  phase_11_direct
  phase_12_firewall
  phase_13_verification
  phase_14_summary
  echo -e "\n${GREEN}Done.${NC}"
}

main "$@"
