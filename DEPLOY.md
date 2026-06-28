# SSG Finance — Server Deployment

This guide is for the pre-provisioned deployment environment described in
[STUDENT-ONBOARDING.md](STUDENT-ONBOARDING.md). The server has Nginx, MySQL,
and a user systemd service already configured.

## Prerequisites

- SSH access to the deployment server
- Files placed in `~/app/` (git clone, scp, or rsync)
- `.env` file in `~/app/` with database and SMTP credentials
- Rootless Docker available (pre-configured)

## Quick Start

```bash
# 1. SSH into the server
ssh <your-username>@<server-ip>

# 2. Deploy the application files to ~/app/
cd ~/app
git clone <repo-url> .   # or scp/rsync your files

# 3. Ensure .env exists (the server admin should have placed one)
#    If missing, copy .env.example and set the correct values:
#    cp .env.example .env
#    nano .env

# 4. Build and start
bash deploy-server.sh
```

## What deploy-server.sh Does

1. Verifies `.env` and Docker are available
2. Builds the Docker image (`docker compose build`)
3. Starts the container (`docker compose up -d`)
4. Checks the container is running
5. Prints the access URL

## Architecture

```
                         Host Server
┌─────────────────────────────────────────────────────┐
│  Nginx (port 80/443)                                │
│    └─ proxy_pass http://127.0.0.1:3000             │
│                                                     │
│  MySQL (port 3306, host) ◄── app connects via       │
│                         127.0.0.1 (host networking) │
│                                                     │
│  ~/app/          Application files + Docker config  │
│  ~/uploads/      Persistent user uploads            │
│  ~/backups/      Database + file backups            │
│  ~/logs/         Application logs                   │
│                                                     │
│  Docker Container: ssgfinance-app                   │
│    Port: ${PORT} (default 3000)                     │
│    Volume: ~/uploads/ → /app/wwwroot/uploads        │
│    .env via env_file                                │
└─────────────────────────────────────────────────────┘
```

## Environment Variables

The `.env` file must contain:

| Variable     | Description                         |
|--------------|-------------------------------------|
| `DB_HOST`    | MySQL host (`127.0.0.1`)            |
| `DB_PORT`    | MySQL port (`3306`)                 |
| `DB_DATABASE`| Database name                       |
| `DB_USERNAME`| Database user                       |
| `DB_PASSWORD`| Database password                   |
| `PORT`       | Application HTTP port (default 3000)|
| `SMTP_HOST`  | SMTP server (blank to disable)      |
| `SMTP_PORT`  | SMTP port                           |
| `SMTP_USERNAME` | SMTP user                       |
| `SMTP_PASSWORD` | SMTP password                    |

## Database Migrations

Migrations run automatically on startup (`Program.cs` calls
`context.Database.MigrateAsync()`). No manual migration step needed.

The first startup also seeds a default admin account:
- **Email:** `admin@ssg.com`
- **Password:** `admin123`

**Change the admin password immediately after first login.**

## Updating

```bash
cd ~/app
bash update.sh
```

This pulls the latest code, rebuilds the Docker image, and restart the container.

## Backups

```bash
cd ~/app
bash backup.sh
```

This creates:
- `~/backups/db_<date>.sql.gz` — MySQL dump
- `~/backups/uploads_<date>.tgz` — Uploaded files

Schedule daily backups via cron:
```bash
crontab -e
# Add:  0 3 * * * cd ~/app && bash backup.sh
```

## Logs

```bash
docker compose logs -f          # live app logs
docker compose logs --tail=100  # last 100 lines
```

## SSE (Real-Time Updates)

The app uses Server-Sent Events for live dashboard updates. The Nginx
reverse proxy must disable buffering for the `/Home/Events` endpoint.
See `nginx-proxy.conf` for the required directives. If the server team
has already configured this (per the WebSocket setup in STUDENT-ONBOARDING.md),
no action is needed.

## Commands

| Action               | Command                                   |
|----------------------|-------------------------------------------|
| Start                | `docker compose up -d`                    |
| Stop                 | `docker compose down`                     |
| Restart              | `docker compose restart`                  |
| Full redeploy        | `bash deploy-server.sh`                   |
| Update code & reload | `bash update.sh`                           |
| Backup               | `bash backup.sh`                           |
| View logs            | `docker compose logs -f`                   |
| Container status     | `docker ps --filter name=ssgfinance-app`   |

## Troubleshooting

| Symptom | Check |
|---------|-------|
| Container exits immediately | `docker compose logs --tail=30` |
| MySQL connection refused | Verify DB_HOST, DB_PORT, DB_USERNAME, DB_PASSWORD in `.env` |
| Can't reach app from browser | Check Nginx config points to correct port; verify container is running |
| SSE events not reaching browser | Add `proxy_buffering off;` to Nginx location for `/Home/Events` |
| File uploads lost after restart | Ensure `~/uploads/` bind mount is correct in `docker-compose.yml` |
