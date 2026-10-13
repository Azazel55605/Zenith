# Distributor dashboard

The optional manager is available at https://zenith-stable.net.local/manage/ after
installation. It uses Python's standard library and static HTML/CSS/JavaScript:
no Node runtime, npm dependencies, database, CDN, or frontend build step on the VM.

It displays the effective timer interval, last service start, next scheduled poll,
tag/commit/build status, recent logs and published core/extra package versions.
Repository tabs and search filter the inventory. Run now queues the existing
zenith-distributor.service; completed tags remain skipped and failed tags still
need the explicit CLI retry described in AUTOMATION.md. It does not edit timers,
delete packages, install updates on guests or run arbitrary shell commands.

## Install on the existing Debian VM

Update ~/Zenith to this committed source first. Keep the distributor service,
timer, signing keys and TLS settings already configured. Run as zenith-release:

```sh
cd ~/Zenith
sudo apt-get install -y sudo apache2-utils
sudo bash deploy/distributor/install-dashboard.sh
```

First installation prompts for the dashboard admin password. Existing credentials
are retained on subsequent installs. Change a password with:

```sh
sudo htpasswd /etc/nginx/zenith-dashboard.htpasswd admin
```

In /etc/nginx/sites-available/zenith-packages, **inside the existing server block**,
add the following line if no dashboard include already exists:

```nginx
include /etc/nginx/snippets/zenith-dashboard.conf;
```

The repository's nginx.conf already has an optional wildcard include; if using
that version, do not add the snippet again. Retain your current nginx site settings.

```sh
sudo nginx -t
sudo systemctl reload nginx
systemctl status zenith-dashboard.service --no-pager
```

Open the HTTPS /manage/ URL and log in as admin. Existing Zoraxy routing to port 80
can carry this path without another proxy target. Use the HTTPS proxy when logging
in; the VM's HTTP package endpoint remains useful for signed package delivery.
Package downloads require no dashboard login. The dashboard snippet adds
[nginx Basic authentication](https://nginx.org/en/docs/http/ngx_http_auth_basic_module.html)
only to /manage/. Keep this manager on the existing LAN/VPN network.

## Permissions and behavior

- The loopback-only service listens on 127.0.0.1:8090 as zenith-dashboard. Root owns
  its code under /opt/zenith-dashboard; nginx handles authentication and existing
  Zoraxy handles TLS. Signing keys remain owned by zenith-release with directory
  0700/private key 0600, inaccessible to the manager's user.
- Membership in zenith-release grants traversal/read access to build records and
  top-level logs. Polling now writes these files 0640; the installer updates only
  the named existing public state/log files and directory traversal permissions.
  Failed build directories and the signing directory are not made group-readable.
- A root-owned sudoers rule permits exactly systemctl start --no-block
  zenith-distributor.service. Run requests additionally require same-origin and
  per-process CSRF token checks. The manager accepts no command arguments.
- Timer and service data come from systemctl show, not a hard-coded schedule.
  Poll-lock checking also detects manually invoked poll.py. A leftover building
  record with no active poll appears Interrupted. State during Git fetching/index
  renewal is shown through the busy button even when no individual tag is building.
- The display refreshes every five seconds while visible. Logs are limited to the
  latest 64 KiB; full logs remain on disk. Package inventory comes from unsigned
  release receipts and does not independently attest signatures. Published time
  is receipt modification time. It covers tagged polling releases; the legacy
  manual release command does not create tag records.
- Next run is calculated from systemd's monotonic timer deadline; while a poll is
  active it shows After current run because the timer waits for completion before
  starting its interval. Dates follow the browser's local timezone/locale.

## Check deployment

```sh
curl -I https://zenith-stable.net.local/manage/
curl -I http://10.0.20.7/core/zenith-kernel/0.0.6/zenith-kernel.zpk
journalctl -u zenith-dashboard.service -n 50 --no-pager
```

Use your existing public proxy certificate with curl --cacert if its trust is not
already installed. The first request should require authentication (401); the
second should serve an existing public package. After browser login, verify real
timer values, package inventory and a Run now operation. Missing systemd/data
permissions produce visible warnings and disable unsafe starts.

Local acceptance uses disposable state/receipts and stubbed systemd start calls;
real Debian service/sudo/nginx/Zoraxy activation remains operator-run. To inspect
the direct backend over SSH without nginx, forward local port 8090 to the VM's
127.0.0.1:8090; do not expose the unauthenticated backend directly on the LAN.
