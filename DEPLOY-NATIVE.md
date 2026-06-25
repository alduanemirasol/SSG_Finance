# Native Deployment (Ubuntu Server, no Docker)

Run SSG Finance directly on Kestrel + native MySQL, reachable on the school LAN
at `http://<server-ip>:8085`.

## One-command deploy

On the Ubuntu server, from the repo root:

```bash
cp .env.native.example .env
nano .env                 # set DB_PASSWORD (and SMTP, optionally)
sudo bash deploy-native.sh
```

The script:

1. Installs the **.NET 10 SDK** and **MySQL server** if missing.
2. Creates the database and app user from `.env`.
3. `dotnet publish` → `/opt/ssg` and creates the writable `wwwroot/uploads` dirs.
4. Installs and starts the **`ssg`** systemd service bound to `0.0.0.0:8085` (HTTP).
5. Opens port `8085/tcp` in `ufw` (if active).

Re-running it pulls in code changes and restarts (stop → publish → start).

## After first launch

- Open `http://<server-ip>:8085` from any LAN client.
- Log in as `admin@ssg.com` / `admin123` and **change the password immediately**
  (it is seeded by [ApplicationDbContextSeed.cs](Data/ApplicationDbContextSeed.cs)).
- Reserve a static IP / DHCP lease for the server so the URL stays stable.

## Operating the service

```bash
systemctl status ssg          # health
journalctl -u ssg -f          # live logs
systemctl restart ssg         # restart
```

## Why HTTP-on-IP works

The app calls `UseHsts()` and (in Production) `UseHttpsRedirection()`
([Program.cs](Program.cs)). Because the service binds **HTTP only** and never sets
`ASPNETCORE_HTTPS_PORT`, the redirect is a no-op, and browsers ignore HSTS for raw
IP addresses — so `http://<server-ip>:8085` serves normally.

> Traffic is unencrypted on the LAN (passwords/cookies in clear). Acceptable for a
> trusted internal network; add a self-signed cert on Kestrel if encryption is required.

## Backups

```bash
mysqldump ssg_system > ssg_$(date +%F).sql   # database
tar czf uploads_$(date +%F).tgz /opt/ssg/wwwroot/uploads   # uploaded files
```

## Notes

- No reverse proxy is required. If you later add **Nginx**, disable response
  buffering for the SSE endpoint (`proxy_buffering off;` + long read timeout) or
  live updates will stall.
- This replaces the Docker workflow (`Dockerfile`, `docker-compose.yml`,
  `deploy.sh`). Use one or the other, not both.
